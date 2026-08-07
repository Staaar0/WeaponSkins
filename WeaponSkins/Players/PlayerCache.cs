using System.Collections.Concurrent;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

namespace WeaponSkins;

public sealed class PlayerCache
{
	private readonly ConcurrentDictionary<ulong, PlayerLoadout> loadouts = new();
	private readonly object loadSync = new();
	private readonly Dictionary<ulong, CancellationTokenSource> loading = [];
	private readonly LoadoutStore store;
	private readonly ILogger logger;

	public event Action<ulong>? LoadoutReady;

	public PlayerCache(LoadoutStore store, ILogger logger)
	{
		this.store = store;
		this.logger = logger;
	}

	public PlayerLoadout? Get(ulong steamId)
	{
		return loadouts.TryGetValue(steamId, out var loadout) && loadout.Ready ? loadout : null;
	}

	public PlayerLoadout? Get(CCSPlayerController? player)
	{
		return player == null || !player.IsValid || player.IsBot ? null : Get(player.SteamID);
	}

	public bool IsCached(ulong steamId) => loadouts.ContainsKey(steamId);

	public void Drop(ulong steamId)
	{
		CancellationTokenSource? cancellation;
		lock (loadSync)
		{
			loadouts.TryRemove(steamId, out _);
			loading.Remove(steamId, out cancellation);
		}

		Cancel(cancellation);
	}

	public void Clear()
	{
		CancellationTokenSource[] cancellations;
		lock (loadSync)
		{
			loadouts.Clear();
			cancellations = loading.Values.ToArray();
			loading.Clear();
		}

		foreach (var cancellation in cancellations)
			Cancel(cancellation);
	}



	public void Fetch(ulong steamId)
	{
		if (steamId == 0)
			return;

		CancellationTokenSource cancellation;
		lock (loadSync)
		{
			if (loadouts.ContainsKey(steamId) || loading.ContainsKey(steamId))
				return;

			cancellation = new CancellationTokenSource();
			loading.Add(steamId, cancellation);
		}
		var token = cancellation.Token;

		Task.Run(async () =>
		{
			try
			{
				for (var attempt = 1; attempt <= 5; attempt++)
				{
					try
					{
						var loadout = await store.Load(steamId, token);
						lock (loadSync)
						{
							if (token.IsCancellationRequested ||
								!loading.TryGetValue(steamId, out var current) ||
								!ReferenceEquals(current, cancellation))
								return;

							loadout.Ready = true;
							loadouts[steamId] = loadout;
						}

						Server.NextFrame(() =>
						{
							if (!token.IsCancellationRequested &&
								loadouts.TryGetValue(steamId, out var current) &&
								ReferenceEquals(current, loadout))
								LoadoutReady?.Invoke(steamId);
						});
						return;
					}
					catch (OperationCanceledException)
					{
						throw;
					}
					catch (Exception ex) when (attempt < 5)
					{
						logger.LogWarning("Loadout fetch attempt {Attempt} failed for {SteamId}: {Error}", attempt, steamId, ex.Message);
						await Task.Delay(attempt * 3000, token);
					}
				}
			}
			catch (OperationCanceledException)
			{
			}
			catch (Exception ex)
			{
				logger.LogError("Giving up loading loadout for {SteamId}: {Error}", steamId, ex.Message);
			}
			finally
			{
				lock (loadSync)
				{
					if (loading.TryGetValue(steamId, out var current) && ReferenceEquals(current, cancellation))
						loading.Remove(steamId);
				}
				cancellation.Dispose();
			}
		});
	}

	public static List<CsTeam> TargetTeams(CCSPlayerController player)
	{
		return player.Team is CsTeam.Terrorist or CsTeam.CounterTerrorist
			? [player.Team]
			: [CsTeam.Terrorist, CsTeam.CounterTerrorist];
	}

	private static void Cancel(CancellationTokenSource? cancellation)
	{
		try
		{
			cancellation?.Cancel();
		}
		catch (ObjectDisposedException)
		{
		}
	}
}
