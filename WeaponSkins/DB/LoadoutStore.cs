using CounterStrikeSharp.API.Modules.Utils;
using MySqlConnector;

namespace WeaponSkins;

public sealed class LoadoutStore
{
	private readonly record struct WeaponSnapshot(int Paint, float Wear, int Seed, string? NameTag, int StatTrak);
	private readonly record struct StickerSnapshot(int Slot, int Id, float Wear, float Scale, float Rotation, float OffsetX, float OffsetY, int Schema);
	private readonly record struct CharmSnapshot(int Id, int Pattern, int Sticker, int Highlight, float OffsetX, float OffsetY, float OffsetZ);
	private readonly record struct StatTrakKey(ulong SteamId, CsTeam Team, int DefIndex, int Paint);
	private readonly record struct CounterValue(int Count, long Version, long Epoch);
	private readonly record struct StatTrakWrite(CsTeam Team, int DefIndex, int Paint, CounterValue Value);
	private readonly record struct WriteKey(ulong SteamId, CsTeam Team, int DefIndex, WriteKind Kind, int Paint = 0, WeaponFields Fields = WeaponFields.None);

	[Flags]
	public enum WeaponFields
	{
		None = 0,
		Wear = 1,
		Seed = 2,
		NameTag = 4,
		StatTrak = 8,
		All = Wear | Seed | NameTag | StatTrak
	}

	private enum WriteKind
	{
		Weapon,
		WeaponAndEquip,
		WeaponAndStickers,
		Stickers,
		StickersAndCharm,
		GeneratedWeapon,
		GeneratedKnife,
		Knife,
		Gloves,
		Agent,
		Music,
		Pin
	}

	private sealed class CoalescedWrite
	{
		public required Func<Task> Operation;
		public Task Runner = Task.CompletedTask;
		public long Version;
	}

	private sealed class QueuedWrite(ulong steamId)
	{
		public readonly ulong SteamId = steamId;
		public readonly TaskCompletionSource Admission = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public bool Admitted;
		public LinkedListNode<QueuedWrite>? Waiting;
	}

	private const int MaxPendingWrites = 1024;
	private const int MaxPendingWritesPerPlayer = 64;

	private readonly Database database;
	private readonly object writeSync = new();
	private readonly Dictionary<ulong, Task> writeTails = [];
	private readonly Dictionary<ulong, int> pendingByPlayer = [];
	private readonly Dictionary<WriteKey, CoalescedWrite> coalescedWrites = [];
	private readonly Dictionary<StatTrakKey, CounterValue> pendingStatTrak = [];
	private readonly Dictionary<StatTrakKey, CounterValue> scheduledStatTrak = [];
	private readonly Dictionary<StatTrakKey, long> persistedCounterVersions = [];
	private readonly Dictionary<StatTrakKey, long> counterEpochs = [];
	private readonly Dictionary<ulong, Task> statTrakFlushes = [];
	private readonly LinkedList<QueuedWrite> admissionWaiters = [];
	private readonly SemaphoreSlim writeSlots = new(8, 8);
	private readonly CancellationTokenSource writeCancellation = new();
	private long counterVersion;
	private int pendingWrites;
	private bool acceptingWrites = true;
	private Task drainCompletion = Task.CompletedTask;
	public event Action<Exception>? WriteFailed;
	public Task DrainCompletion { get { lock (writeSync) return drainCompletion; } }

	public LoadoutStore(Database database)
	{
		this.database = database;
	}

	private static TeamLoadout Side(PlayerLoadout loadout, sbyte team) => team == 3 ? loadout.CT : loadout.T;

	private static string NormalizeAgentModel(string model)
	{
		var path = model.Trim();
		foreach (var prefix in new[] { "characters/models/", "agents/models/" })
			if (path.StartsWith(prefix, StringComparison.Ordinal))
				path = path[prefix.Length..];
		if (path.EndsWith(".vmdl", StringComparison.Ordinal))
			path = path[..^5];
		if (path.Length == 0 || path.Contains("..") || path.Contains('\\') || path.StartsWith('/'))
			return "";
		return "agents/models/" + path + ".vmdl";
	}

	public async Task<PlayerLoadout> Load(ulong steamId, CancellationToken cancellationToken = default)
	{
		var loadout = new PlayerLoadout();
		if (!database.Configured)
			return loadout;

		// Flush again to include kills earned during the earlier sync save.
		await FlushStatTrak(steamId, cancellationToken);

		Task? pending;
		lock (writeSync)
			writeTails.TryGetValue(steamId, out pending);

		if (pending != null)
		{
			try
			{
				await pending.WaitAsync(cancellationToken);
			}
			catch when (!cancellationToken.IsCancellationRequested)
			{
			}
		}

		await using var connection = await database.Open(cancellationToken);
		await using var command = connection.CreateCommand();
		command.CommandText = """
			SELECT team, defindex, paint, wear, seed, nametag, stattrak FROM ws_weapons WHERE steamid = @sid;
			SELECT team, defindex, paint, slot, sticker, wear, scale, rotation, offset_x, offset_y, schema_slot FROM ws_stickers WHERE steamid = @sid;
			SELECT team, defindex, paint, charm, pattern, sticker, highlight, offset_x, offset_y, offset_z FROM ws_charms WHERE steamid = @sid;
			SELECT team, defindex, paint FROM ws_equipped WHERE steamid = @sid;
			SELECT team, defindex FROM ws_knives WHERE steamid = @sid;
			SELECT team, defindex, paint FROM ws_gloves WHERE steamid = @sid;
			SELECT team, model FROM ws_agents WHERE steamid = @sid;
			SELECT kit FROM ws_music WHERE steamid = @sid;
			SELECT pin FROM ws_pins WHERE steamid = @sid;
			""";
		command.Parameters.AddWithValue("@sid", steamId);

		await using var reader = await command.ExecuteReaderAsync(cancellationToken);

		while (await reader.ReadAsync(cancellationToken))
		{
			var entry = Side(loadout, reader.GetSByte(0)).Skin(reader.GetInt32(1), reader.GetInt32(2));
			entry.Wear = reader.GetFloat(3);
			entry.Seed = reader.GetInt32(4);
			entry.NameTag = reader.IsDBNull(5) ? null : reader.GetString(5);
			entry.StatTrak = reader.GetInt32(6);
		}

		await reader.NextResultAsync(cancellationToken);
		while (await reader.ReadAsync(cancellationToken))
		{
			var entry = Side(loadout, reader.GetSByte(0)).Skin(reader.GetInt32(1), reader.GetInt32(2));
			entry.Stickers.Add(new StickerEntry
			{
				Slot = reader.GetSByte(3),
				Id = reader.GetInt32(4),
				Wear = reader.GetFloat(5),
				Scale = reader.GetFloat(6),
				Rotation = reader.GetFloat(7),
				OffsetX = reader.GetFloat(8),
				OffsetY = reader.GetFloat(9),
				Schema = reader.GetSByte(10)
			});
		}

		await reader.NextResultAsync(cancellationToken);
		while (await reader.ReadAsync(cancellationToken))
		{
			var entry = Side(loadout, reader.GetSByte(0)).Skin(reader.GetInt32(1), reader.GetInt32(2));
			entry.Charm = new CharmEntry
			{
				Id = reader.GetInt32(3),
				Pattern = reader.GetInt32(4),
				Sticker = reader.GetInt32(5),
				Highlight = reader.GetInt32(6),
				OffsetX = reader.GetFloat(7),
				OffsetY = reader.GetFloat(8),
				OffsetZ = reader.GetFloat(9)
			};
		}

		await reader.NextResultAsync(cancellationToken);
		while (await reader.ReadAsync(cancellationToken))
			Side(loadout, reader.GetSByte(0)).Equip(reader.GetInt32(1), reader.GetInt32(2));

		await reader.NextResultAsync(cancellationToken);
		while (await reader.ReadAsync(cancellationToken))
			Side(loadout, reader.GetSByte(0)).Knife = reader.GetInt32(1);

		await reader.NextResultAsync(cancellationToken);
		while (await reader.ReadAsync(cancellationToken))
		{
			var side = Side(loadout, reader.GetSByte(0));
			side.GloveDef = reader.GetInt32(1);
			side.GlovePaint = reader.GetInt32(2);
		}

		await reader.NextResultAsync(cancellationToken);
		while (await reader.ReadAsync(cancellationToken))
			Side(loadout, reader.GetSByte(0)).AgentModel = NormalizeAgentModel(reader.GetString(1));

		await reader.NextResultAsync(cancellationToken);
		while (await reader.ReadAsync(cancellationToken))
			loadout.MusicKit = reader.GetInt32(0);

		await reader.NextResultAsync(cancellationToken);
		while (await reader.ReadAsync(cancellationToken))
			loadout.Pin = reader.GetInt32(0);

		return loadout;
	}

	public Task SaveWeapon(ulong steamId, CsTeam team, int defIndex, WeaponEntry entry, WeaponFields fields = WeaponFields.All)
	{
		var weapon = Snapshot(entry);
		var resetVersion = CaptureCounterVersion();
		return QueueLatest(new WriteKey(steamId, team, defIndex, WriteKind.Weapon, weapon.Paint, fields), async () =>
		{
			await using var connection = await database.Open(writeCancellation.Token);
			await WriteWeapon(connection, null, steamId, team, defIndex, weapon, fields);
			if ((fields & WeaponFields.StatTrak) != 0)
				ResetStatTrak(steamId, team, defIndex, weapon.Paint, resetVersion);
		});
	}

	public Task SaveWeaponAndEquip(ulong steamId, CsTeam team, int defIndex, WeaponEntry entry)
	{
		var weapon = Snapshot(entry);
		return QueueLatest(new WriteKey(steamId, team, defIndex, WriteKind.WeaponAndEquip, weapon.Paint), () => Transaction(async (connection, transaction) =>
		{
			await WriteWeapon(connection, transaction, steamId, team, defIndex, weapon, WeaponFields.None);
			await WriteEquipped(connection, transaction, steamId, team, defIndex, weapon.Paint);
		}));
	}

	public Task SaveWeaponAndStickers(ulong steamId, CsTeam team, int defIndex, WeaponEntry entry)
	{
		var weapon = Snapshot(entry);
		var stickers = Snapshot(entry.Stickers);
		return QueueLatest(new WriteKey(steamId, team, defIndex, WriteKind.WeaponAndStickers, weapon.Paint), () => Transaction(async (connection, transaction) =>
		{
			await WriteWeapon(connection, transaction, steamId, team, defIndex, weapon, WeaponFields.None);
			await WriteStickers(connection, transaction, steamId, team, defIndex, weapon.Paint, stickers);
		}));
	}

	public Task SaveStickers(ulong steamId, CsTeam team, int defIndex, int paint, List<StickerEntry> stickers)
	{
		var snapshot = Snapshot(stickers);
		return QueueLatest(new WriteKey(steamId, team, defIndex, WriteKind.Stickers, paint), () => Transaction((connection, transaction) =>
			WriteStickers(connection, transaction, steamId, team, defIndex, paint, snapshot)));
	}

	public Task SaveStickersAndCharm(ulong steamId, CsTeam team, int defIndex, int paint, List<StickerEntry> stickers, CharmEntry? charm)
	{
		var stickerSnapshot = Snapshot(stickers);
		var charmSnapshot = Snapshot(charm);
		return QueueLatest(new WriteKey(steamId, team, defIndex, WriteKind.StickersAndCharm, paint), () => Transaction(async (connection, transaction) =>
		{
			await WriteStickers(connection, transaction, steamId, team, defIndex, paint, stickerSnapshot);
			await WriteCharm(connection, transaction, steamId, team, defIndex, paint, charmSnapshot);
		}));
	}

	public Task SaveGeneratedWeapon(ulong steamId, CsTeam team, int defIndex, WeaponEntry entry)
	{
		var weapon = Snapshot(entry);
		var stickers = Snapshot(entry.Stickers);
		var charm = Snapshot(entry.Charm);
		var resetVersion = CaptureCounterVersion();
		return QueueLatest(new WriteKey(steamId, team, defIndex, WriteKind.GeneratedWeapon, weapon.Paint), async () =>
		{
			await Transaction(async (connection, transaction) =>
			{
				await WriteWeapon(connection, transaction, steamId, team, defIndex, weapon);
				await WriteEquipped(connection, transaction, steamId, team, defIndex, weapon.Paint);
				await WriteStickers(connection, transaction, steamId, team, defIndex, weapon.Paint, stickers);
				await WriteCharm(connection, transaction, steamId, team, defIndex, weapon.Paint, charm);
			});
			ResetStatTrak(steamId, team, defIndex, weapon.Paint, resetVersion);
		});
	}

	public Task SaveGeneratedKnife(ulong steamId, CsTeam team, int defIndex, WeaponEntry entry)
	{
		var weapon = Snapshot(entry);
		var stickers = Snapshot(entry.Stickers);
		var charm = Snapshot(entry.Charm);
		var resetVersion = CaptureCounterVersion();
		return QueueLatest(new WriteKey(steamId, team, defIndex, WriteKind.GeneratedKnife, weapon.Paint), async () =>
		{
			await Transaction(async (connection, transaction) =>
			{
				await WriteKnife(connection, transaction, steamId, team, defIndex);
				await WriteWeapon(connection, transaction, steamId, team, defIndex, weapon);
				await WriteEquipped(connection, transaction, steamId, team, defIndex, weapon.Paint);
				await WriteStickers(connection, transaction, steamId, team, defIndex, weapon.Paint, stickers);
				await WriteCharm(connection, transaction, steamId, team, defIndex, weapon.Paint, charm);
			});
			ResetStatTrak(steamId, team, defIndex, weapon.Paint, resetVersion);
		});
	}

	public void MarkStatTrak(ulong steamId, CsTeam team, int defIndex, int paint, int count)
	{
		if (!database.Configured)
			return;

		lock (writeSync)
		{
			if (acceptingWrites)
			{
				var key = new StatTrakKey(steamId, team, defIndex, paint);
				pendingStatTrak[key] = new CounterValue(Math.Max(count, 0), ++counterVersion, counterEpochs.GetValueOrDefault(key));
			}
		}
	}

	public Task FlushStatTrak(ulong? steamId = null, CancellationToken cancellationToken = default)
	{
		return FlushRequestedCounters(steamId, false, cancellationToken);
	}

	private async Task FlushRequestedCounters(ulong? steamId, bool internalWrite, CancellationToken cancellationToken)
	{
		Dictionary<StatTrakKey, long> requested;
		Task batch;
		lock (writeSync)
		{
			requested = pendingStatTrak.Where(entry => !steamId.HasValue || entry.Key.SteamId == steamId.Value)
				.ToDictionary(entry => entry.Key, entry => entry.Value.Version);
			batch = FlushStatTrakCore(steamId, internalWrite);
		}
		while (true)
		{
			await batch.WaitAsync(cancellationToken);
			lock (writeSync)
			{
				if (!requested.Any(entry => pendingStatTrak.ContainsKey(entry.Key) &&
					persistedCounterVersions.GetValueOrDefault(entry.Key) < entry.Value))
					return;
				// A reset changed the counter. Queue the pending count again.
				batch = FlushStatTrakCore(steamId, internalWrite);
			}
			await Task.Yield();
		}
	}

	private Task FlushStatTrakCore(ulong? steamId, bool internalWrite)
	{
		if (!database.Configured)
			return Task.CompletedTask;

		var tasks = new List<Task>();
		lock (writeSync)
		{
			foreach (var active in statTrakFlushes)
			{
				if (!steamId.HasValue || active.Key == steamId.Value)
					tasks.Add(active.Value);
			}

			var groups = pendingStatTrak
				.Where(entry => (!steamId.HasValue || entry.Key.SteamId == steamId.Value) &&
					(!scheduledStatTrak.TryGetValue(entry.Key, out var scheduled) || scheduled != entry.Value))
				.GroupBy(entry => entry.Key.SteamId)
				.Select(group => (SteamId: group.Key, Writes: group.Select(entry => new StatTrakWrite(entry.Key.Team, entry.Key.DefIndex, entry.Key.Paint, entry.Value)).ToArray()))
				.ToArray();

			foreach (var group in groups)
			{
				foreach (var write in group.Writes)
					scheduledStatTrak[new StatTrakKey(group.SteamId, write.Team, write.DefIndex, write.Paint)] = write.Value;
				var task = Queue(group.SteamId, () => WriteStatTrakBatchSafe(group.SteamId, group.Writes), internalWrite);
				statTrakFlushes[group.SteamId] = task;
				tasks.Add(task);
				_ = task.ContinueWith(
						_ => CompleteStatTrakFlush(group.SteamId, task, group.Writes),
					CancellationToken.None,
					TaskContinuationOptions.ExecuteSynchronously,
					TaskScheduler.Default);
			}
		}

		return tasks.Count == 0 ? Task.CompletedTask : Task.WhenAll(tasks);
	}

	// Queue game and Discord saves together. Save earlier kills before a counter reset.
	public Task RunMutationAsync(ulong steamId, Func<Task> operation, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		lock (writeSync)
		{
			var counters = FlushStatTrakCore(steamId, false);
			var mutation = Queue(steamId, async () =>
			{
				cancellationToken.ThrowIfCancellationRequested();
				await counters;
				await operation();
			});
			return mutation.WaitAsync(cancellationToken);
		}
	}

	public Task SaveKnife(ulong steamId, CsTeam team, int defIndex)
	{
		return QueueLatest(new WriteKey(steamId, team, 0, WriteKind.Knife), async () =>
		{
			await using var connection = await database.Open(writeCancellation.Token);
			await WriteKnife(connection, null, steamId, team, defIndex);
		});
	}

	public Task SaveGloves(ulong steamId, CsTeam team, TeamLoadout side, WeaponFields fields = WeaponFields.All)
	{
		var defIndex = side.GloveDef;
		var paint = side.GlovePaint;
		var weapon = defIndex > 0 ? Snapshot(side.Gloves) : default;
		return QueueLatest(new WriteKey(steamId, team, defIndex, WriteKind.Gloves, paint, fields), () => Transaction(async (connection, transaction) =>
		{
			if (fields is WeaponFields.None or WeaponFields.All || defIndex <= 0)
				await WriteGloves(connection, transaction, steamId, team, defIndex, paint);
			if (defIndex > 0)
				await WriteWeapon(connection, transaction, steamId, team, defIndex, weapon, fields);
		}));
	}

	public Task SaveAgent(ulong steamId, CsTeam team, string? model)
	{
		return QueueLatest(new WriteKey(steamId, team, 0, WriteKind.Agent), () => !string.IsNullOrEmpty(model)
			? Run("INSERT INTO ws_agents (steamid, team, model) VALUES (@sid, @team, @value) ON DUPLICATE KEY UPDATE model = @value;", steamId, team, ("@value", model))
			: Run("DELETE FROM ws_agents WHERE steamid = @sid AND team = @team;", steamId, team));
	}

	public Task SaveMusic(ulong steamId, int kit)
	{
		return QueueLatest(new WriteKey(steamId, CsTeam.None, 0, WriteKind.Music), () => kit > 0
			? Run("INSERT INTO ws_music (steamid, kit) VALUES (@sid, @value) ON DUPLICATE KEY UPDATE kit = @value;", steamId, CsTeam.None, ("@value", kit))
			: Run("DELETE FROM ws_music WHERE steamid = @sid;", steamId, CsTeam.None));
	}

	public Task SavePin(ulong steamId, int pin)
	{
		return QueueLatest(new WriteKey(steamId, CsTeam.None, 0, WriteKind.Pin), () => pin > 0
			? Run("INSERT INTO ws_pins (steamid, pin) VALUES (@sid, @value) ON DUPLICATE KEY UPDATE pin = @value;", steamId, CsTeam.None, ("@value", pin))
			: Run("DELETE FROM ws_pins WHERE steamid = @sid;", steamId, CsTeam.None));
	}

	private long CaptureCounterVersion()
	{
		lock (writeSync)
			return counterVersion;
	}

	public void ResetStatTrak(ulong steamId, CsTeam team, int defIndex, int paint, long throughVersion = long.MaxValue)
	{
		lock (writeSync)
		{
			var key = new StatTrakKey(steamId, team, defIndex, paint);
			var epoch = counterEpochs.GetValueOrDefault(key) + 1;
			counterEpochs[key] = epoch;
			persistedCounterVersions[key] = Math.Max(persistedCounterVersions.GetValueOrDefault(key),
				throughVersion == long.MaxValue ? counterVersion : throughVersion);
			if (pendingStatTrak.TryGetValue(key, out var value))
			{
				if (value.Version <= throughVersion)
					pendingStatTrak.Remove(key);
				else
					pendingStatTrak[key] = value with { Epoch = epoch };
			}
		}
	}

	private Task QueueLatest(WriteKey key, Func<Task> operation)
	{
		if (!database.Configured)
			return Task.CompletedTask;

		lock (writeSync)
		{
			if (!acceptingWrites)
				return Task.FromException(new InvalidOperationException("The loadout store is stopping"));
			if (coalescedWrites.TryGetValue(key, out var pending) &&
				!pending.Runner.IsCompleted &&
				writeTails.TryGetValue(key.SteamId, out var tail) &&
				ReferenceEquals(tail, pending.Runner))
			{
				pending.Operation = operation;
				pending.Version++;
				return pending.Runner;
			}

			var state = new CoalescedWrite { Operation = operation };
			coalescedWrites[key] = state;
			state.Runner = Queue(key.SteamId, () => RunCoalesced(key, state));
			if (state.Runner.IsCompleted && state.Runner.IsFaulted)
				coalescedWrites.Remove(key);
			return state.Runner;
		}
	}

	private async Task RunCoalesced(WriteKey key, CoalescedWrite state)
	{
		while (true)
		{
			Func<Task> operation;
			long version;
			lock (writeSync)
			{
				operation = state.Operation;
				version = state.Version;
			}

			await operation();

			lock (writeSync)
			{
				if (state.Version != version)
					continue;
				if (coalescedWrites.TryGetValue(key, out var current) && ReferenceEquals(current, state))
					coalescedWrites.Remove(key);
				return;
			}
		}
	}

	public void BeginStop()
	{
		lock (writeSync)
			acceptingWrites = false;
	}

	public async Task Stop(CancellationToken cancellationToken = default)
	{
		BeginStop();
		using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		deadline.CancelAfter(TimeSpan.FromSeconds(10));
		try
		{
			await FlushRequestedCounters(null, true, deadline.Token);
		}
		finally
		{
			Task[] pending;
			lock (writeSync)
			{
				pending = writeTails.Values.ToArray();
				drainCompletion = Task.WhenAll(pending);
			}
			try
			{
				if (pending.Length > 0)
					await drainCompletion.WaitAsync(deadline.Token);
			}
			finally
			{
				writeCancellation.Cancel();
				// Observe unfinished tasks without delaying shutdown.
				foreach (var task in pending)
					_ = task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
			}
		}
	}

	private Task Queue(ulong steamId, Func<Task> operation, bool internalWrite = false)
	{
		if (!database.Configured)
			return Task.CompletedTask;

		Task next;
		QueuedWrite state;
		lock (writeSync)
		{
			if (!acceptingWrites && !internalWrite)
				return Task.FromException(new InvalidOperationException("The loadout store is stopping"));

			state = new QueuedWrite(steamId);
			// Wait for queue space and keep saves in submission order.
			state.Waiting = admissionWaiters.AddLast(state);
			AdmitWaitingWrites();
			var previous = writeTails.GetValueOrDefault(steamId, Task.CompletedTask);
			next = previous.ContinueWith(
				_ => RunQueuedWrite(state, operation),
				CancellationToken.None,
				TaskContinuationOptions.None,
				TaskScheduler.Default).Unwrap();
			writeTails[steamId] = next;
		}

		_ = next.ContinueWith(
			_ => CompleteWrite(state, next),
			CancellationToken.None,
			TaskContinuationOptions.ExecuteSynchronously,
			TaskScheduler.Default);

		return next;
	}

	private void AdmitWaitingWrites()
	{
		for (var node = admissionWaiters.First; node != null && pendingWrites < MaxPendingWrites;)
		{
			var next = node.Next;
			var state = node.Value;
			var count = pendingByPlayer.GetValueOrDefault(state.SteamId);
			if (count < MaxPendingWritesPerPlayer)
			{
				admissionWaiters.Remove(node);
				state.Waiting = null;
				state.Admitted = true;
				pendingWrites++;
				pendingByPlayer[state.SteamId] = count + 1;
				state.Admission.TrySetResult();
			}
			node = next;
		}
	}

	private async Task RunQueuedWrite(QueuedWrite state, Func<Task> operation)
	{
		await state.Admission.Task.WaitAsync(writeCancellation.Token);
		await RunWrite(operation);
	}

	private async Task RunWrite(Func<Task> operation)
	{
		await writeSlots.WaitAsync(writeCancellation.Token);
		try
		{
			var attempt = 0;
			while (true)
			{
				writeCancellation.Token.ThrowIfCancellationRequested();
				try
				{
					await operation();
					return;
				}
				catch (Exception ex) when (IsTransient(ex) && !writeCancellation.IsCancellationRequested)
				{
					try { WriteFailed?.Invoke(ex); } catch { }
					attempt = Math.Min(attempt + 1, 6);
					await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, 1 << attempt)), writeCancellation.Token);
				}
			}
		}
		finally
		{
			writeSlots.Release();
		}
	}

	private static bool IsTransient(Exception exception)
	{
		return exception is MySqlException { IsTransient: true } or TimeoutException or IOException;
	}

	private void CompleteWrite(QueuedWrite state, Task completed)
	{
		lock (writeSync)
		{
			var steamId = state.SteamId;
			if (state.Waiting != null)
				admissionWaiters.Remove(state.Waiting);
			if (state.Admitted)
			{
				pendingWrites--;
				var playerPending = pendingByPlayer.GetValueOrDefault(steamId);
				if (playerPending <= 1)
					pendingByPlayer.Remove(steamId);
				else
					pendingByPlayer[steamId] = playerPending - 1;
			}

			if (writeTails.TryGetValue(steamId, out var tail) && ReferenceEquals(tail, completed))
				writeTails.Remove(steamId);
			if (!writeTails.ContainsKey(steamId) && !pendingStatTrak.Keys.Any(key => key.SteamId == steamId))
			{
				// No older counter save remains. Release its tracking data.
				foreach (var key in counterEpochs.Keys.Where(key => key.SteamId == steamId).ToArray())
					counterEpochs.Remove(key);
				foreach (var key in persistedCounterVersions.Keys.Where(key => key.SteamId == steamId).ToArray())
					persistedCounterVersions.Remove(key);
			}
			AdmitWaitingWrites();
		}
	}

	private async Task WriteStatTrakBatchSafe(ulong steamId, StatTrakWrite[] writes)
	{
		var applied = await WriteStatTrakBatch(steamId, writes);
		lock (writeSync)
		{
			foreach (var write in applied)
			{
				var key = new StatTrakKey(steamId, write.Team, write.DefIndex, write.Paint);
				persistedCounterVersions[key] = Math.Max(persistedCounterVersions.GetValueOrDefault(key), write.Value.Version);
				if (pendingStatTrak.TryGetValue(key, out var current) && current == write.Value)
					pendingStatTrak.Remove(key);
			}
		}
	}

	private void CompleteStatTrakFlush(ulong steamId, Task task, StatTrakWrite[] writes)
	{
		lock (writeSync)
		{
			if (statTrakFlushes.TryGetValue(steamId, out var current) && ReferenceEquals(current, task))
				statTrakFlushes.Remove(steamId);
			foreach (var write in writes)
			{
				var key = new StatTrakKey(steamId, write.Team, write.DefIndex, write.Paint);
				if (scheduledStatTrak.TryGetValue(key, out var value) && value == write.Value)
					scheduledStatTrak.Remove(key);
			}
		}
	}

	private async Task<StatTrakWrite[]> WriteStatTrakBatch(ulong steamId, StatTrakWrite[] writes)
	{
		await using var connection = await database.Open(writeCancellation.Token);
		await using var transaction = await connection.BeginTransactionAsync(writeCancellation.Token);
		await using var command = connection.CreateCommand();
		command.Transaction = transaction;
		command.CommandText = "UPDATE ws_weapons SET stattrak = GREATEST(stattrak, @count) WHERE steamid = @sid AND team = @team AND defindex = @def AND paint = @paint AND stattrak >= 0;";
		command.Parameters.AddWithValue("@sid", steamId);
		command.Parameters.AddWithValue("@team", 0);
		command.Parameters.AddWithValue("@def", 0);
		command.Parameters.AddWithValue("@paint", 0);
		command.Parameters.AddWithValue("@count", 0);
		var applied = new List<StatTrakWrite>(writes.Length);

		foreach (var write in writes)
		{
			lock (writeSync)
			{
				if (counterEpochs.GetValueOrDefault(new StatTrakKey(steamId, write.Team, write.DefIndex, write.Paint)) != write.Value.Epoch)
					continue;
			}
			command.Parameters["@team"].Value = (sbyte)write.Team;
			command.Parameters["@def"].Value = write.DefIndex;
			command.Parameters["@paint"].Value = write.Paint;
			command.Parameters["@count"].Value = write.Value.Count;
			await command.ExecuteNonQueryAsync(writeCancellation.Token);
			applied.Add(write);
		}

		await transaction.CommitAsync(writeCancellation.Token);
		return applied.ToArray();
	}

	private async Task Transaction(Func<MySqlConnection, MySqlTransaction, Task> operation)
	{
		await using var connection = await database.Open(writeCancellation.Token);
		await using var transaction = await connection.BeginTransactionAsync(writeCancellation.Token);
		await operation(connection, transaction);
		await transaction.CommitAsync(writeCancellation.Token);
	}

	private async Task Run(string sql, ulong steamId, CsTeam team, params (string Name, object Value)[] extra)
	{
		await using var connection = await database.Open(writeCancellation.Token);
		await using var command = connection.CreateCommand();
		command.CommandText = sql;
		command.Parameters.AddWithValue("@sid", steamId);
		command.Parameters.AddWithValue("@team", (sbyte)team);
		foreach (var (name, value) in extra)
			command.Parameters.AddWithValue(name, value);
		await command.ExecuteNonQueryAsync(writeCancellation.Token);
	}

	private async Task WriteWeapon(
		MySqlConnection connection,
		MySqlTransaction? transaction,
		ulong steamId,
		CsTeam team,
		int defIndex,
		WeaponSnapshot weapon,
		WeaponFields fields = WeaponFields.All)
	{
		await using var command = connection.CreateCommand();
		command.Transaction = transaction;
		var updates = new List<string>(4);
		if ((fields & WeaponFields.Wear) != 0) updates.Add("wear = @wear");
		if ((fields & WeaponFields.Seed) != 0) updates.Add("seed = @seed");
		if ((fields & WeaponFields.NameTag) != 0) updates.Add("nametag = @nametag");
		if ((fields & WeaponFields.StatTrak) != 0) updates.Add("stattrak = @stattrak");
		command.CommandText = """
			INSERT INTO ws_weapons (steamid, team, defindex, paint, wear, seed, nametag, stattrak)
			VALUES (@sid, @team, @def, @paint, @wear, @seed, @nametag, @stattrak)
			ON DUPLICATE KEY UPDATE
			""" + " " + (updates.Count == 0 ? "paint = paint" : string.Join(", ", updates)) + ";";
		AddKey(command, steamId, team, defIndex, weapon.Paint);
		command.Parameters.AddWithValue("@wear", weapon.Wear);
		command.Parameters.AddWithValue("@seed", weapon.Seed);
		command.Parameters.AddWithValue("@nametag", (object?)weapon.NameTag ?? DBNull.Value);
		command.Parameters.AddWithValue("@stattrak", weapon.StatTrak);
		await command.ExecuteNonQueryAsync(writeCancellation.Token);
	}

	private async Task WriteEquipped(
		MySqlConnection connection,
		MySqlTransaction? transaction,
		ulong steamId,
		CsTeam team,
		int defIndex,
		int paint)
	{
		await using var command = connection.CreateCommand();
		command.Transaction = transaction;
		command.CommandText = """
			INSERT INTO ws_equipped (steamid, team, defindex, paint)
			VALUES (@sid, @team, @def, @paint)
			ON DUPLICATE KEY UPDATE paint = @paint;
			""";
		AddKey(command, steamId, team, defIndex, paint);
		await command.ExecuteNonQueryAsync(writeCancellation.Token);
	}

	private async Task WriteStickers(
		MySqlConnection connection,
		MySqlTransaction transaction,
		ulong steamId,
		CsTeam team,
		int defIndex,
		int paint,
		StickerSnapshot[] stickers)
	{
		await using (var command = connection.CreateCommand())
		{
			command.Transaction = transaction;
			command.CommandText = "DELETE FROM ws_stickers WHERE steamid = @sid AND team = @team AND defindex = @def AND paint = @paint;";
			AddKey(command, steamId, team, defIndex, paint);
			await command.ExecuteNonQueryAsync(writeCancellation.Token);
		}

		foreach (var sticker in stickers)
		{
			await using var insert = connection.CreateCommand();
			insert.Transaction = transaction;
			insert.CommandText = """
				INSERT INTO ws_stickers (steamid, team, defindex, paint, slot, sticker, wear, scale, rotation, offset_x, offset_y, schema_slot)
				VALUES (@sid, @team, @def, @paint, @slot, @sticker, @wear, @scale, @rotation, @x, @y, @schema);
				""";
			AddKey(insert, steamId, team, defIndex, paint);
			insert.Parameters.AddWithValue("@slot", (sbyte)sticker.Slot);
			insert.Parameters.AddWithValue("@sticker", sticker.Id);
			insert.Parameters.AddWithValue("@wear", sticker.Wear);
			insert.Parameters.AddWithValue("@scale", sticker.Scale);
			insert.Parameters.AddWithValue("@rotation", sticker.Rotation);
			insert.Parameters.AddWithValue("@x", sticker.OffsetX);
			insert.Parameters.AddWithValue("@y", sticker.OffsetY);
			insert.Parameters.AddWithValue("@schema", (sbyte)sticker.Schema);
			await insert.ExecuteNonQueryAsync(writeCancellation.Token);
		}
	}

	private async Task WriteCharm(
		MySqlConnection connection,
		MySqlTransaction? transaction,
		ulong steamId,
		CsTeam team,
		int defIndex,
		int paint,
		CharmSnapshot? charm)
	{
		await using var command = connection.CreateCommand();
		command.Transaction = transaction;

		if (!charm.HasValue || charm.Value.Id <= 0)
		{
			command.CommandText = "DELETE FROM ws_charms WHERE steamid = @sid AND team = @team AND defindex = @def AND paint = @paint;";
			AddKey(command, steamId, team, defIndex, paint);
			await command.ExecuteNonQueryAsync(writeCancellation.Token);
			return;
		}

		var value = charm.Value;
		command.CommandText = """
			INSERT INTO ws_charms (steamid, team, defindex, paint, charm, pattern, sticker, highlight, offset_x, offset_y, offset_z)
			VALUES (@sid, @team, @def, @paint, @charm, @pattern, @sticker, @highlight, @x, @y, @z)
			ON DUPLICATE KEY UPDATE charm = @charm, pattern = @pattern, sticker = @sticker, highlight = @highlight,
				offset_x = @x, offset_y = @y, offset_z = @z;
			""";
		AddKey(command, steamId, team, defIndex, paint);
		command.Parameters.AddWithValue("@charm", value.Id);
		command.Parameters.AddWithValue("@pattern", value.Pattern);
		command.Parameters.AddWithValue("@sticker", value.Sticker);
		command.Parameters.AddWithValue("@highlight", value.Highlight);
		command.Parameters.AddWithValue("@x", value.OffsetX);
		command.Parameters.AddWithValue("@y", value.OffsetY);
		command.Parameters.AddWithValue("@z", value.OffsetZ);
		await command.ExecuteNonQueryAsync(writeCancellation.Token);
	}

	private async Task WriteKnife(
		MySqlConnection connection,
		MySqlTransaction? transaction,
		ulong steamId,
		CsTeam team,
		int defIndex)
	{
		await using var command = connection.CreateCommand();
		command.Transaction = transaction;
		command.Parameters.AddWithValue("@sid", steamId);
		command.Parameters.AddWithValue("@team", (sbyte)team);

		if (defIndex > 0)
		{
			command.CommandText = """
				INSERT INTO ws_knives (steamid, team, defindex)
				VALUES (@sid, @team, @def)
				ON DUPLICATE KEY UPDATE defindex = @def;
				""";
			command.Parameters.AddWithValue("@def", defIndex);
		}
		else
		{
			command.CommandText = "DELETE FROM ws_knives WHERE steamid = @sid AND team = @team;";
		}

		await command.ExecuteNonQueryAsync(writeCancellation.Token);
	}

	private async Task WriteGloves(
		MySqlConnection connection,
		MySqlTransaction transaction,
		ulong steamId,
		CsTeam team,
		int defIndex,
		int paint)
	{
		await using var command = connection.CreateCommand();
		command.Transaction = transaction;
		command.Parameters.AddWithValue("@sid", steamId);
		command.Parameters.AddWithValue("@team", (sbyte)team);

		if (defIndex > 0)
		{
			command.CommandText = """
				INSERT INTO ws_gloves (steamid, team, defindex, paint)
				VALUES (@sid, @team, @def, @paint)
				ON DUPLICATE KEY UPDATE defindex = @def, paint = @paint;
				""";
			command.Parameters.AddWithValue("@def", defIndex);
			command.Parameters.AddWithValue("@paint", paint);
		}
		else
		{
			command.CommandText = "DELETE FROM ws_gloves WHERE steamid = @sid AND team = @team;";
		}

		await command.ExecuteNonQueryAsync(writeCancellation.Token);
	}

	private static void AddKey(MySqlCommand command, ulong steamId, CsTeam team, int defIndex, int paint)
	{
		command.Parameters.AddWithValue("@sid", steamId);
		command.Parameters.AddWithValue("@team", (sbyte)team);
		command.Parameters.AddWithValue("@def", defIndex);
		command.Parameters.AddWithValue("@paint", paint);
	}

	private static WeaponSnapshot Snapshot(WeaponEntry entry)
	{
		return new WeaponSnapshot(entry.Paint,
			CosmeticRules.IsValidWear(entry.Wear) ? entry.Wear : 0.000001f,
			Math.Clamp(entry.Seed, 0, 1000),
			entry.NameTag is { Length: > 64 } name ? name[..64] : entry.NameTag,
			Math.Max(entry.StatTrak, -1));
	}

	private static StickerSnapshot[] Snapshot(List<StickerEntry> stickers)
	{
		return stickers
			.Where(CosmeticRules.IsValidSticker)
			.GroupBy(sticker => sticker.Slot)
			.Select(group => group.Last())
			.Select(sticker => new StickerSnapshot(
				sticker.Slot,
				sticker.Id,
				sticker.Wear,
				sticker.Scale,
				sticker.Rotation,
				sticker.OffsetX,
				sticker.OffsetY,
				sticker.Schema))
			.ToArray();
	}

	private static CharmSnapshot? Snapshot(CharmEntry? charm)
	{
		return charm == null || !CosmeticRules.IsValidCharm(charm)
			? null
			: new CharmSnapshot(
				charm.Id,
				charm.Pattern,
				charm.Sticker,
				charm.Highlight,
				charm.OffsetX,
				charm.OffsetY,
				charm.OffsetZ);
	}
}
