using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using WeaponSkinsBot;

namespace WeaponSkins;

public sealed class DiscordBotCoordinator
{
	private const int PollSeconds = 3;
	private const int LeaseSeconds = 15;
	private const int RestartSeconds = 30;
	private const int LeaseOperationSeconds = 3;
	// Leave time to disconnect before the lease expires.
	private const int LeaseSafetySeconds = 5;

	private readonly WeaponSkins plugin;
	private readonly Database database;
	private readonly string token;
	private readonly BotLease lease;
	private readonly TaskCompletionSource<bool> databaseReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private CancellationTokenSource? cancellation;
	private CancellationTokenSource? botCancellation;
	private Task? loop;
	private Task? botTask;
	private long nextErrorLog;
	private long leaseDeadline;

	public DiscordBotCoordinator(WeaponSkins plugin, Database database, string token)
	{
		this.plugin = plugin;
		this.database = database;
		this.token = token;
		lease = new BotLease(database, token);
	}

	public void Start()
	{
		if (loop != null)
			return;

		WeaponSkins.PrintDiscordBotLoading();
		cancellation = new CancellationTokenSource();
		loop = Run(cancellation.Token);
		var lifetime = cancellation;
		_ = loop.ContinueWith(finished =>
		{
			if (finished.IsFaulted)
				plugin.Logger.LogError("WeaponSkinsBOT coordination stopped: {Error}", finished.Exception!.GetBaseException().Message);
			lifetime.Dispose();
		}, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
	}

	public void DatabaseReady()
	{
		databaseReady.TrySetResult(true);
	}

	public void DatabaseUnavailable()
	{
		databaseReady.TrySetResult(false);
	}

	public void RequestStop()
	{
		CancelQuietly(cancellation);
		CancelQuietly(botCancellation);
	}

	private static void CancelQuietly(CancellationTokenSource? source)
	{
		try { source?.Cancel(); }
		catch (ObjectDisposedException) { }
	}

	public void Stop() => StopAsync().GetAwaiter().GetResult();

	public async Task StopAsync(CancellationToken cancellationToken = default)
	{
		RequestStop();
		var running = loop;
		if (running == null)
			return;
		try
		{
			await running.WaitAsync(TimeSpan.FromSeconds(6), cancellationToken);
		}
		catch (OperationCanceledException)
		{
		}
		catch (TimeoutException)
		{
			plugin.Logger.LogWarning("WeaponSkinsBOT shutdown exceeded its deadline; cleanup continues in the background");
		}
		catch (Exception ex)
		{
			plugin.Logger.LogError("WeaponSkinsBOT shutdown failed: {Error}", ex.GetBaseException().Message);
		}
	}

	private async Task Run(CancellationToken cancellationToken)
	{
		var ownsLease = false;
		var nextBotStart = 0L;

		try
		{
			if (!await databaseReady.Task.WaitAsync(cancellationToken))
				return;

			try
			{
				using var purgeCancellation = LeaseOperationCancellation(cancellationToken);
				await lease.PurgeStale(purgeCancellation.Token);
			}
			catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
			{
				LogError(ex);
			}

			while (!cancellationToken.IsCancellationRequested)
			{
				try
				{
					// Wait for the old session to stop before starting another.
					if (botTask != null && botCancellation?.IsCancellationRequested == true)
					{
						if (!botTask.IsCompleted)
						{
							await Task.Delay(TimeSpan.FromSeconds(PollSeconds), cancellationToken);
							continue;
						}
						await ObserveBot();
					}

					var started = Environment.TickCount64;
					using var operationCancellation = LeaseOperationCancellation(cancellationToken);
					if (!ownsLease)
					{
						ownsLease = await lease.TryAcquire(operationCancellation.Token);
						if (ownsLease)
							ConfirmLease(started);
					}
					else if (await lease.Renew(operationCancellation.Token))
					{
						ConfirmLease(started);
					}
					else
					{
						plugin.Logger.LogWarning("WeaponSkinsBOT lease was lost; switching this server to standby");
						if (await StopBot())
							lease.RelinquishLocal();
						ownsLease = false;
					}

					if (ownsLease)
					{
						if (botTask?.IsCompleted == true)
							await ObserveBot();

						var now = Environment.TickCount64;
						if (botTask == null && now < leaseDeadline && now >= nextBotStart)
						{
							StartBot(cancellationToken);
							nextBotStart = now + RestartSeconds * 1000L;
						}
					}
				}
				catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
				{
					break;
				}
				catch (Exception ex)
				{
					LogError(ex);
					// Include query time when checking the lease.
					if (ownsLease && Environment.TickCount64 >= leaseDeadline)
					{
						plugin.Logger.LogWarning("WeaponSkinsBOT lease could not be renewed; stopping the bot to prevent duplicate sessions");
						if (await StopBot())
							lease.RelinquishLocal();
						ownsLease = false;
					}
				}

				await Task.Delay(TimeSpan.FromSeconds(PollSeconds), cancellationToken);
			}
		}
		finally
		{
			var stopped = await StopBot();
			if (ownsLease && stopped)
			{
				try
				{
					using var releaseCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));
					await lease.Release(releaseCancellation.Token);
				}
				catch (Exception ex)
				{
					LogError(ex);
				}
			}
			else if (stopped)
				lease.RelinquishLocal();
			else
			{
				// Keep the local lock until the bot stops. Let the database lease expire.
				_ = botTask!.ContinueWith(_ => lease.RelinquishLocal(),
					CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
			}
		}
	}

	private static CancellationTokenSource LeaseOperationCancellation(CancellationToken cancellationToken)
	{
		var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		operation.CancelAfter(TimeSpan.FromSeconds(LeaseOperationSeconds));
		return operation;
	}

	private void ConfirmLease(long operationStarted)
	{
		// Start the deadline before the query so clock differences cannot extend it.
		leaseDeadline = operationStarted + (LeaseSeconds - LeaseSafetySeconds) * 1000L;
		if (botCancellation?.IsCancellationRequested == false)
			botCancellation.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(0, leaseDeadline - Environment.TickCount64)));
	}

	private void StartBot(CancellationToken coordinatorToken)
	{
		botCancellation = CancellationTokenSource.CreateLinkedTokenSource(coordinatorToken);
		// The timer also stops the bot if lease renewal hangs.
		botCancellation.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(0, leaseDeadline - Environment.TickCount64)));
		var bot = new BotApp(
			token,
			database,
			plugin.ModuleDirectory,
			plugin.Config.Api,
			plugin.Logger,
			WeaponSkins.PrintDiscordBotConnected,
            steamId => plugin.Links?.WakeSync(steamId),
            plugin.DiscordPermissionsAsync,
            plugin.Store,
            (key, args) => plugin.Text(key, args));
		botTask = bot.RunAsync(botCancellation.Token);
	}

	private async Task ObserveBot()
	{
		try
		{
			await botTask!;
		}
		catch (OperationCanceledException) when (botCancellation?.IsCancellationRequested == true)
		{
		}
		catch (Exception ex)
		{
			plugin.Logger.LogError("WeaponSkinsBOT stopped unexpectedly: {Error}", ex.GetBaseException().Message);
		}
		finally
		{
			botCancellation?.Dispose();
			botCancellation = null;
			botTask = null;
		}
	}

	private async Task<bool> StopBot()
	{
		if (botTask == null)
			return true;

		botCancellation?.Cancel();
		try
		{
			await botTask.WaitAsync(TimeSpan.FromSeconds(6));
		}
		catch (TimeoutException)
		{
			return false;
		}
		catch
		{
			// ObserveBot logs failures and clears the session.
		}
		await ObserveBot();
		return true;
	}

	private void LogError(Exception exception)
	{
		var now = Environment.TickCount64;
		if (now < Interlocked.Read(ref nextErrorLog))
			return;

		Interlocked.Exchange(ref nextErrorLog, now + 10000);
		plugin.Logger.LogError("WeaponSkinsBOT coordination failed: {Error}", exception.GetBaseException().Message);
	}

	private sealed class BotLease
	{
		private const int DuplicateKey = 1062;
		private readonly Database database;
		private readonly byte[] tokenHash;
		private readonly string localLockPath;
		private readonly string ownerId = Guid.NewGuid().ToString("N");
		private FileStream? localLock;

		public BotLease(Database database, string token)
		{
			this.database = database;
			tokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
			localLockPath = Path.Combine(Path.GetTempPath(), $"weaponskins-bot-{Convert.ToHexString(tokenHash)}.lock");
		}

		public async Task<bool> TryAcquire(CancellationToken cancellationToken)
		{
			if (!TryAcquireLocal())
				return false;

			try
			{
				var acquired = await TryAcquireDatabase(cancellationToken);
				if (!acquired)
					ReleaseLocal();
				return acquired;
			}
			catch
			{
				ReleaseLocal();
				throw;
			}
		}

		private async Task<bool> TryAcquireDatabase(CancellationToken cancellationToken)
		{
			await using var connection = await database.Open(cancellationToken);
			await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

			try
			{
				string? currentOwner = null;
				var expired = false;
				await using (var select = connection.CreateCommand())
				{
					select.Transaction = transaction;
					select.CommandTimeout = LeaseOperationSeconds;
					select.CommandText = "SELECT owner_id, lease_until <= UTC_TIMESTAMP(6) FROM ws_bot_leases WHERE token_hash = @token FOR UPDATE;";
					select.Parameters.AddWithValue("@token", tokenHash);
					await using var reader = await select.ExecuteReaderAsync(cancellationToken);
					if (await reader.ReadAsync(cancellationToken))
					{
						currentOwner = reader.GetString(0);
						expired = Convert.ToInt32(reader.GetValue(1)) != 0;
					}
				}

				if (currentOwner == null)
				{
					await using var insert = connection.CreateCommand();
					insert.Transaction = transaction;
					insert.CommandTimeout = LeaseOperationSeconds;
					insert.CommandText = $"INSERT INTO ws_bot_leases (token_hash, owner_id, lease_until) VALUES (@token, @owner, DATE_ADD(UTC_TIMESTAMP(6), INTERVAL {LeaseSeconds} SECOND));";
					insert.Parameters.AddWithValue("@token", tokenHash);
					insert.Parameters.AddWithValue("@owner", ownerId);
					await insert.ExecuteNonQueryAsync(cancellationToken);
				}
				else if (!expired && !string.Equals(currentOwner, ownerId, StringComparison.Ordinal))
				{
					await transaction.CommitAsync(cancellationToken);
					return false;
				}
				else
				{
					await using var update = connection.CreateCommand();
					update.Transaction = transaction;
					update.CommandTimeout = LeaseOperationSeconds;
					update.CommandText = $"UPDATE ws_bot_leases SET owner_id = @owner, lease_until = DATE_ADD(UTC_TIMESTAMP(6), INTERVAL {LeaseSeconds} SECOND) WHERE token_hash = @token;";
					update.Parameters.AddWithValue("@token", tokenHash);
					update.Parameters.AddWithValue("@owner", ownerId);
					await update.ExecuteNonQueryAsync(cancellationToken);
				}

				await transaction.CommitAsync(cancellationToken);
				return true;
			}
			catch (MySqlException ex) when ((int)ex.ErrorCode == DuplicateKey)
			{
				await RollbackQuietly(transaction);
				return false;
			}
			catch
			{
				await RollbackQuietly(transaction);
				throw;
			}
		}

		private static async Task RollbackQuietly(MySqlTransaction transaction)
		{
			try
			{
				using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));
				await transaction.RollbackAsync(cancellation.Token);
			}
			catch
			{
			}
		}

		public async Task<bool> Renew(CancellationToken cancellationToken)
		{
			await using var connection = await database.Open(cancellationToken);
			await using var command = connection.CreateCommand();
			command.CommandTimeout = LeaseOperationSeconds;
			command.CommandText = $"UPDATE ws_bot_leases SET lease_until = DATE_ADD(UTC_TIMESTAMP(6), INTERVAL {LeaseSeconds} SECOND) WHERE token_hash = @token AND owner_id = @owner AND lease_until > UTC_TIMESTAMP(6);";
			command.Parameters.AddWithValue("@token", tokenHash);
			command.Parameters.AddWithValue("@owner", ownerId);
			return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
		}

		public async Task Release(CancellationToken cancellationToken)
		{
			try
			{
				await using var connection = await database.Open(cancellationToken);
				await using var command = connection.CreateCommand();
				command.CommandTimeout = LeaseOperationSeconds;
				command.CommandText = "DELETE FROM ws_bot_leases WHERE token_hash = @token AND owner_id = @owner;";
				command.Parameters.AddWithValue("@token", tokenHash);
				command.Parameters.AddWithValue("@owner", ownerId);
				await command.ExecuteNonQueryAsync(cancellationToken);
			}
			finally
			{
				ReleaseLocal();
			}
		}

		public void RelinquishLocal() => ReleaseLocal();

		public async Task PurgeStale(CancellationToken cancellationToken)
		{
			await using var connection = await database.Open(cancellationToken);
			await using var command = connection.CreateCommand();
			command.CommandTimeout = LeaseOperationSeconds;
			command.CommandText = "DELETE FROM ws_bot_leases WHERE lease_until < DATE_SUB(UTC_TIMESTAMP(6), INTERVAL 7 DAY);";
			await command.ExecuteNonQueryAsync(cancellationToken);
		}

		private bool TryAcquireLocal()
		{
			if (localLock != null)
				return true;

			try
			{
				localLock = new FileStream(localLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
				return true;
			}
			catch (IOException)
			{
				return false;
			}
		}

		private void ReleaseLocal()
		{
			localLock?.Dispose();
			localLock = null;
		}
	}
}
