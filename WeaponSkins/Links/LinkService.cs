using System.Security.Cryptography;
using System.Text;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;
using Microsoft.Extensions.Logging;

namespace WeaponSkins;

public sealed class LinkService
{
	private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
	private const int CodeLength = 8;
	private const int CodeAttempts = 4;
	private const int CodeCooldownMs = 30000;
	private const int TickMs = 1000;
	private const int SyncEveryTicks = 2;
	private const int SweepEveryTicks = 10;
	private const int MaintenanceEveryTicks = 60;
	private const float JoinNoticeDelay = 3f;

	private readonly WeaponSkins plugin;
	private readonly LinkStore store;
	private readonly DiscordUtilitiesLinkProvider? discordUtilities;
	private readonly object sync = new();
	private readonly HashSet<ulong> online = [];
	private readonly HashSet<ulong> linked = [];
	private readonly HashSet<ulong> queried = [];
	private readonly Dictionary<ulong, long> pending = [];
	private readonly Dictionary<ulong, long> nextCode = [];
	private CancellationTokenSource? cancellation;
	private Task? loop;

	public bool UsesDiscordUtilities { get; }
	public bool Required { get; }
	public bool CanIssueCodes => !UsesDiscordUtilities &&
		(Required || plugin.Db.Configured && plugin.HasDiscordBotToken);

	public LinkService(WeaponSkins plugin, LinkStore store)
	{
		this.plugin = plugin;
		this.store = store;
		UsesDiscordUtilities = string.Equals(plugin.Config.LinkingMethod, "Discord-Utilities", StringComparison.OrdinalIgnoreCase);
		Required = UsesDiscordUtilities || plugin.Config.LinkRequired && plugin.Db.Configured;
		if (UsesDiscordUtilities)
			discordUtilities = new DiscordUtilitiesLinkProvider(plugin.Logger);
	}

	public void Start()
	{
		if ((!Required && !CanIssueCodes) || loop != null)
			return;

		cancellation = new CancellationTokenSource();
		loop = Poll(cancellation.Token);
		if (UsesDiscordUtilities)
			Server.NextFrame(SyncDiscordUtilities);
	}

	public void Stop()
	{
		cancellation?.Cancel();

		try
		{
			loop?.GetAwaiter().GetResult();
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex)
		{
			plugin.Logger.LogError("Discord link shutdown failed: {Error}", ex.GetBaseException().Message);
		}
		finally
		{
			cancellation?.Dispose();
			cancellation = null;
			loop = null;
		}

		lock (sync)
		{
			online.Clear();
			linked.Clear();
			pending.Clear();
			queried.Clear();
			nextCode.Clear();
		}
	}

	public bool IsLinked(ulong steamId)
	{
		lock (sync)
			return linked.Contains(steamId);
	}

	public void Track(ulong steamId)
	{
		if (steamId == 0)
			return;

		if (UsesDiscordUtilities)
		{
			lock (sync)
				online.Add(steamId);

			SyncDiscordUtilities(steamId);
			return;
		}

		bool fetch;
		var resolve = false;
		lock (sync)
		{
			online.Add(steamId);
			fetch = !Required || linked.Contains(steamId);
			if (!fetch)
				resolve = queried.Add(steamId);
		}

		if (fetch)
			plugin.Cache.Fetch(steamId);
		else if (resolve)
		{
			Notice(steamId);
			_ = Resolve(steamId);
		}
	}

	public void Forget(ulong steamId)
	{
		lock (sync)
		{
			online.Remove(steamId);
			linked.Remove(steamId);
			pending.Remove(steamId);
			queried.Remove(steamId);
			nextCode.Remove(steamId);
		}
	}

	public void RequestCode(CCSPlayerController player)
	{
		if (!CanIssueCodes)
			return;

		var steamId = player.SteamID;
		if (steamId == 0)
			return;

		if (IsLinked(steamId))
		{
			plugin.Reply(player, "link_already");
			return;
		}

		lock (sync)
		{
			var now = Environment.TickCount64;
			if (nextCode.TryGetValue(steamId, out var next) && now < next)
			{
				plugin.Reply(player, "link_cooldown", (int)Math.Ceiling((next - now) / 1000d));
				return;
			}

			online.Add(steamId);
			nextCode[steamId] = now + CodeCooldownMs;
		}

		_ = Issue(steamId);
	}

	private async Task Issue(ulong steamId)
	{
		var token = cancellation?.Token ?? CancellationToken.None;
		try
		{
			for (var attempt = 0; attempt < CodeAttempts; attempt++)
			{
				var code = NewCode();
				if (!await store.StoreCode(steamId, SHA256.HashData(Encoding.UTF8.GetBytes(code)), token))
					continue;

				lock (sync)
					pending[steamId] = Environment.TickCount64 + LinkStore.CodeLifetimeSeconds * 1000L;

				Server.NextFrame(() => Show(steamId, $"{code[..4]}-{code[4..]}"));
				return;
			}
		}
		catch (OperationCanceledException)
		{
			return;
		}
		catch (Exception ex)
		{
			plugin.LogDatabaseError(ex);
		}

		Server.NextFrame(() => Fail(steamId));
	}

	private void Show(ulong steamId, string code)
	{
		if (plugin.Stopping || Player(steamId) is not { } player)
			return;

		plugin.Reply(player, "link_code", code);
		if (plugin.Config.DiscordLink.Length > 0)
			plugin.Reply(player, "link_url", plugin.Config.DiscordLink);
	}

	private void Fail(ulong steamId)
	{
		lock (sync)
			nextCode.Remove(steamId);

		if (plugin.Stopping || Player(steamId) is not { } player)
			return;

		plugin.Reply(player, "link_failed");
	}

	private async Task Resolve(ulong steamId)
	{
		var token = cancellation?.Token ?? CancellationToken.None;
		try
		{
			if (await store.IsLinked(steamId, token))
			{
				Server.NextFrame(() => Complete(steamId, true));
				return;
			}
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex)
		{
			lock (sync)
				queried.Remove(steamId);

			plugin.LogDatabaseError(ex);
		}
	}

	private void Notice(ulong steamId)
	{
		if (plugin.Stopping)
			return;

		plugin.AddTimer(JoinNoticeDelay, () =>
		{
			if (plugin.Stopping || IsLinked(steamId) || Player(steamId) is not { } player)
				return;

			plugin.Reply(player, "link_required");
		}, TimerFlags.STOP_ON_MAPCHANGE);
	}

	private void Complete(ulong steamId, bool silent)
	{
		if (plugin.Stopping)
			return;

		lock (sync)
		{
			if (!online.Contains(steamId) || !linked.Add(steamId))
				return;

			pending.Remove(steamId);
			queried.Remove(steamId);
			nextCode.Remove(steamId);
		}

		plugin.Cache.Fetch(steamId);
		if (silent || Player(steamId) is not { } player)
			return;

		plugin.Reply(player, "link_success");
	}

	private async Task Poll(CancellationToken cancellationToken)
	{
		var tick = 0L;
		while (!cancellationToken.IsCancellationRequested)
		{
			try
			{
				await Task.Delay(TickMs, cancellationToken);
				tick++;

				if (UsesDiscordUtilities)
				{
					Server.NextFrame(SyncDiscordUtilities);
					continue;
				}

				if (CanIssueCodes)
					await Detect(tick % SweepEveryTicks == 0 ? Unlinked() : Pending(), cancellationToken);

				if (tick % SyncEveryTicks == 0)
					await Drain(cancellationToken);

				if (tick % MaintenanceEveryTicks == 0)
					await Maintain(cancellationToken);
			}
			catch (OperationCanceledException)
			{
				return;
			}
			catch (Exception ex)
			{
				plugin.LogDatabaseError(ex);
			}
		}
	}

	private void SyncDiscordUtilities()
	{
		if (plugin.Stopping)
			return;

		foreach (var steamId in Online())
			SyncDiscordUtilities(steamId);
	}

	private void SyncDiscordUtilities(ulong steamId)
	{
		if (plugin.Stopping || Player(steamId) is not { } player ||
			discordUtilities == null || !discordUtilities.TryIsLinked(steamId, out var isLinked))
			return;

		if (isLinked)
		{
			Complete(steamId, true);
			return;
		}

		bool wasLinked;
		bool notice;
		lock (sync)
		{
			wasLinked = linked.Remove(steamId);
			pending.Remove(steamId);
			nextCode.Remove(steamId);
			notice = queried.Add(steamId);
		}

		if (wasLinked)
		{
			plugin.Menu.Close(player);
			plugin.Save(plugin.Store.FlushStatTrak(steamId));
			plugin.Cache.Drop(steamId);
		}

		if (notice)
			Notice(steamId);
	}

	private async Task Detect(List<ulong> watched, CancellationToken cancellationToken)
	{
		if (watched.Count == 0)
			return;

		var found = await store.FindLinked(watched, cancellationToken);
		if (found.Count == 0)
			return;

		Server.NextFrame(() =>
		{
			foreach (var steamId in found)
				Complete(steamId, false);
		});
	}

	private async Task Drain(CancellationToken cancellationToken)
	{
		var players = Online();
		if (players.Count == 0)
			return;

		var rows = await store.ReadSync(players, cancellationToken);
		if (rows.Count == 0)
			return;

		await store.DeleteSync(rows.ConvertAll(row => row.Id), cancellationToken);

		var steamIds = rows.Select(row => row.SteamId).Distinct().ToList();
		Server.NextFrame(() =>
		{
			foreach (var steamId in steamIds)
				Refresh(steamId);
		});
	}

	private void Refresh(ulong steamId)
	{
		if (plugin.Stopping || (Required && !IsLinked(steamId)) || Player(steamId) is not { } player)
			return;

		plugin.Menu.Close(player);
		plugin.Save(plugin.Store.FlushStatTrak(steamId));
		plugin.Cache.Reload(steamId);
		plugin.Reply(player, "loadout_synced");
	}

	private async Task Maintain(CancellationToken cancellationToken)
	{
		if (CanIssueCodes)
			await store.PurgeCodes(cancellationToken);

		await store.PurgeSync(cancellationToken);
	}

	private List<ulong> Online()
	{
		lock (sync)
			return [.. online];
	}

	private List<ulong> Unlinked()
	{
		lock (sync)
		{
			var watched = new List<ulong>(online.Count);
			foreach (var steamId in online)
			{
				if (!linked.Contains(steamId))
					watched.Add(steamId);
			}

			return watched;
		}
	}

	private List<ulong> Pending()
	{
		var now = Environment.TickCount64;
		lock (sync)
		{
			var watched = new List<ulong>(pending.Count);
			List<ulong>? expired = null;
			foreach (var entry in pending)
			{
				if (entry.Value > now)
					watched.Add(entry.Key);
				else
					(expired ??= []).Add(entry.Key);
			}

			if (expired != null)
			{
				foreach (var steamId in expired)
					pending.Remove(steamId);
			}

			return watched;
		}
	}

	private static CCSPlayerController? Player(ulong steamId)
	{
		var player = Utilities.GetPlayerFromSteamId(steamId);
		return player != null && player.IsValid && !player.IsBot ? player : null;
	}

	private static string NewCode()
	{
		return string.Create(CodeLength, 0, (span, _) =>
		{
			for (var i = 0; i < span.Length; i++)
				span[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
		});
	}
}
