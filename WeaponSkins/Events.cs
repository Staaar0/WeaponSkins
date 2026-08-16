using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

namespace WeaponSkins;

public sealed class Events
{
	private readonly WeaponSkins plugin;
	private readonly Dictionary<int, ulong> steamIdsBySlot = [];
	private readonly Dictionary<int, int> pendingInventoryApplies = [];
	private int profileResyncRevision;
	private int worldGeneration;
	private bool registered;

	public Events(WeaponSkins plugin)
	{
		this.plugin = plugin;
	}

	public void RegisterPrecache()
	{
		plugin.RegisterListener<Listeners.OnServerPrecacheResources>(OnPrecacheResources);
	}

	public void Register()
	{
		if (registered)
			return;

		plugin.RegisterListener<Listeners.OnTick>(OnTick);
		plugin.RegisterListener<Listeners.OnMapStart>(OnMapStart);
		plugin.RegisterListener<Listeners.OnClientAuthorized>(OnClientAuthorized);
		plugin.RegisterListener<Listeners.OnClientDisconnect>(OnClientDisconnect);
		plugin.RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn);
		plugin.RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
		plugin.RegisterEventHandler<EventPlayerConnectFull>(OnPlayerConnectFull);
		plugin.RegisterEventHandler<EventItemPickup>(OnItemPickup);
		plugin.RegisterEventHandler<EventRoundStart>(OnRoundStart);
		plugin.AddCommandListener("say", OnSay);
		plugin.AddCommandListener("say_team", OnSay);
		VirtualFunctions.GiveNamedItemFunc.Hook(OnGiveNamedItem, HookMode.Post);
		plugin.Cache.LoadoutReady += OnLoadoutReady;
		registered = true;
	}

	public void Unregister()
	{
		if (!registered)
			return;

		VirtualFunctions.GiveNamedItemFunc.Unhook(OnGiveNamedItem, HookMode.Post);
		plugin.Cache.LoadoutReady -= OnLoadoutReady;
		steamIdsBySlot.Clear();
		pendingInventoryApplies.Clear();
		profileResyncRevision++;
		registered = false;
	}

	private void OnTick() => plugin.Menu.OnTick();

	private HookResult OnGiveNamedItem(DynamicHook hook)
	{
		try
		{
			var weapon = hook.GetReturn<CBasePlayerWeapon>();
			if (weapon == null || !weapon.IsValid)
				return HookResult.Continue;

			var owner = PlayerFromItemServices(hook.GetParam<CCSPlayer_ItemServices>(0));
			if (owner != null)
				plugin.Applier.Apply(owner, weapon);

			var weaponHandle = weapon.EntityHandle.Raw;
			Server.NextWorldUpdate(() => ApplyByHandle(weaponHandle));
		}
		catch
		{
		}

		return HookResult.Continue;
	}

	private static CCSPlayerController? PlayerFromItemServices(CCSPlayer_ItemServices services)
	{
		if (services.Pawn.Value is not { IsValid: true } pawn ||
			pawn.Controller.Value is not { IsValid: true } controller)
			return null;

		var player = new CCSPlayerController(controller.Handle);
		return player.IsValid && !player.IsBot ? player : null;
	}

	private void ApplyByHandle(uint weaponHandle)
	{
		if (weaponHandle == 0)
			return;

		var weapon = new CHandle<CBasePlayerWeapon>(weaponHandle).Value;
		if (weapon == null || !weapon.IsValid)
			return;

		var player = ResolveOwner(weapon);
		if (player != null)
			plugin.Applier.Apply(player, weapon);
	}

	private static CCSPlayerController? ResolveOwner(CBasePlayerWeapon weapon)
	{
		var owner = weapon.OwnerEntity.Value;
		if (owner != null && owner.IsValid && owner.DesignerName == "player")
		{
			var pawn = new CCSPlayerPawn(owner.Handle);
			if (pawn.Controller.Value is { IsValid: true } controller)
			{
				var direct = new CCSPlayerController(controller.Handle);
				if (direct.IsValid && !direct.IsBot)
					return direct;
			}
		}

		var account = weapon.OriginalOwnerXuidLow;
		if (account == 0)
			return null;

		var player = Utilities.GetPlayerFromSteamId(account + 76561197960265728UL);
		return player != null && player.IsValid && !player.IsBot ? player : null;
	}

	private HookResult OnPlayerSpawn(EventPlayerSpawn @event, GameEventInfo info)
	{
		var player = @event.Userid;
		if (player == null || !player.IsValid || player.IsBot)
			return HookResult.Continue;

		plugin.Applier.AdvanceLife(player.Slot);
		plugin.Profile.ApplyAgent(player);
		steamIdsBySlot[player.Slot] = player.SteamID;
		plugin.Profile.ApplyMusic(player);
		plugin.Profile.ApplyPin(player);
		plugin.GloveApply.Drop(player.Slot);
		plugin.GloveApply.Apply(player, false);
		var playerRef = PlayerRef.Capture(player);
		if (playerRef is { } spawnRef)
		{
			Server.NextWorldUpdate(() =>
			{
				var current = spawnRef.Resolve();
				if (current != null)
					plugin.Applier.SyncSpawnViewModel(current);
			});
		}

		var spawnSlot = player.Slot;
		var spawnSteamId = player.SteamID;
		var spawnTeam = player.Team;
		plugin.AddTimer(0.01f, () =>
		{
			var current = Utilities.GetPlayerFromSlot(spawnSlot);
			if (current == null || !current.IsValid || current.IsBot ||
				current.SteamID != spawnSteamId || current.Team != spawnTeam || !current.PawnIsAlive)
				return;

			plugin.Profile.ApplyAgent(current);
		}, TimerFlags.STOP_ON_MAPCHANGE);

		return HookResult.Continue;
	}

	private HookResult OnPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
	{
		var victim = @event.Userid;
		if (victim != null && victim.IsValid && !victim.IsBot)
			plugin.Applier.AdvanceLife(victim.Slot);

		var attacker = @event.Attacker;
		if (attacker == null || !attacker.IsValid || attacker.IsBot || attacker == @event.Userid)
			return HookResult.Continue;

		if (attacker.Team is not (CsTeam.Terrorist or CsTeam.CounterTerrorist))
			return HookResult.Continue;

		var loadout = plugin.Cache.Get(attacker);
		if (loadout == null)
			return HookResult.Continue;

		var weapon = attacker.PlayerPawn.Value?.WeaponServices?.ActiveWeapon.Value;
		if (weapon == null || !weapon.IsValid)
			return HookResult.Continue;

		var def = (int)weapon.AttributeManager.Item.ItemDefinitionIndex;
		var side = loadout.For(attacker.Team);
		if (!side.Weapons.TryGetValue(def, out var entry) || entry.StatTrak < 0)
			return HookResult.Continue;

		entry.StatTrak++;
		weapon.FallbackStatTrak = entry.StatTrak;

		var item = weapon.AttributeManager.Item;
		var statTrak = EconAttributes.AsFloat(entry.StatTrak);
		EconAttributes.Set(item.NetworkedDynamicAttributes, "kill eater", statTrak);
		EconAttributes.Set(item.AttributeList, "kill eater", statTrak);
		Utilities.SetStateChanged(weapon, "CEconEntity", "m_nFallbackStatTrak");

		plugin.Store.MarkStatTrak(attacker.SteamID, attacker.Team, def, entry.Paint, entry.StatTrak);
		return HookResult.Continue;
	}

	private HookResult OnItemPickup(EventItemPickup @event, GameEventInfo info)
	{
		var player = @event.Userid;
		if (player == null || !player.IsValid || player.IsBot)
			return HookResult.Continue;

		if (!KnifeService.IsKnifeClass(@event.Item))
			return HookResult.Continue;

		var playerRef = PlayerRef.Capture(player);
		if (playerRef == null)
			return HookResult.Continue;

		plugin.Applier.ApplyInventory(player);

		var slot = playerRef.Value.Slot;
		var generation = worldGeneration;
		if (pendingInventoryApplies.ContainsKey(slot))
			return HookResult.Continue;

		pendingInventoryApplies[slot] = generation;
		Server.NextWorldUpdate(() =>
		{
			if (!pendingInventoryApplies.TryGetValue(slot, out var pendingGeneration) || pendingGeneration != generation)
				return;

			pendingInventoryApplies.Remove(slot);
			var current = playerRef.Value.Resolve();
			if (current != null)
				plugin.Applier.ApplyInventory(current);
		});

		return HookResult.Continue;
	}

	private void OnClientAuthorized(int slot, SteamID steamId)
	{
		steamIdsBySlot[slot] = steamId.SteamId64;
		plugin.EnsureLoadout(steamId.SteamId64);
		ScheduleProfileResync(1f, 3f, 7f, 15f);
	}

	private void ResyncProfiles()
	{
		foreach (var current in Utilities.GetPlayers())
		{
			if (!current.IsValid || current.IsBot || !plugin.Profile.NeedsProfile(current))
				continue;

			plugin.Profile.ApplyMusic(current);
			plugin.Profile.ApplyPin(current);
		}
	}

	private void ScheduleProfileResync(params float[] delays)
	{
		var revision = ++profileResyncRevision;
		ResyncProfiles();
		foreach (var delay in delays)
		{
			plugin.AddTimer(delay, () =>
			{
				if (revision == profileResyncRevision)
					ResyncProfiles();
			}, TimerFlags.STOP_ON_MAPCHANGE);
		}
	}


	private HookResult OnPlayerConnectFull(EventPlayerConnectFull @event, GameEventInfo info)
	{
		var player = @event.Userid;
		if (player == null || !player.IsValid || player.IsBot)
			return HookResult.Continue;

		steamIdsBySlot[player.Slot] = player.SteamID;
		plugin.EnsureLoadout(player.SteamID);
		ScheduleProfileResync(0.25f, 1f, 3f, 5f);
		return HookResult.Continue;
	}

	private HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
	{
		foreach (var player in Utilities.GetPlayers())
		{
			if (!player.IsValid || player.IsBot)
				continue;

			steamIdsBySlot[player.Slot] = player.SteamID;
			if (!plugin.Cache.IsCached(player.SteamID))
				plugin.EnsureLoadout(player.SteamID);
		}

		return HookResult.Continue;
	}

	private void OnClientDisconnect(int slot)
	{
		steamIdsBySlot.Remove(slot, out var steamId);
		if (steamId == 0)
		{
			var player = Utilities.GetPlayerFromSlot(slot);
			if (player != null && player.IsValid && !player.IsBot)
				steamId = player.SteamID;
		}

		if (steamId > 0)
		{
			plugin.Save(plugin.Store.FlushStatTrak(steamId));
			plugin.Cache.Drop(steamId);
			plugin.Links.Forget(steamId);
		}

		pendingInventoryApplies.Remove(slot);
		plugin.DropActionState(slot);
		plugin.Menu.Drop(slot);
		plugin.Profile.Drop(slot);
		plugin.Applier.Drop(slot);
		plugin.GloveApply.Drop(slot);
		plugin.Menus.Drop(slot);
	}

	private void OnMapStart(string mapName)
	{
		worldGeneration++;
		plugin.Menu.CloseAll();
		plugin.Applier.Reset();
		pendingInventoryApplies.Clear();
		profileResyncRevision++;

		if (plugin.Catalog.Loaded)
			return;

		Task.Run(async () =>
		{
			try
			{
				await plugin.Catalog.LoadAsync(plugin.Config.Api, plugin.Db.Configured);
				if (plugin.Catalog.Loaded)
					plugin.Menus.Prewarm();
			}
			catch (Exception ex)
			{
				plugin.Logger.LogError("Item data load failed: {Error}", ex.Message);
			}
		});
	}

	private void OnPrecacheResources(ResourceManifest manifest)
	{
		var models = new List<string>(plugin.Catalog.AgentsT.Count + plugin.Catalog.AgentsCT.Count);
		foreach (var agent in plugin.Catalog.AgentsT)
			models.Add(agent.Model);
		foreach (var agent in plugin.Catalog.AgentsCT)
			models.Add(agent.Model);

		foreach (var model in models)
			manifest.AddResource(model);
	}

	private HookResult OnSay(CCSPlayerController? player, CommandInfo info)
	{
		if (player == null || !player.IsValid)
			return HookResult.Continue;

		return plugin.Menu.CaptureChat(player, info.GetArg(1)) ? HookResult.Handled : HookResult.Continue;
	}

	private void OnLoadoutReady(ulong steamId)
	{
		var player = Utilities.GetPlayerFromSteamId(steamId);
		if (player == null || !player.IsValid)
			return;

		steamIdsBySlot[player.Slot] = steamId;
		plugin.Profile.ApplyMusic(player);
		plugin.Profile.ApplyPin(player);

		if (!player.PawnIsAlive)
			return;

		plugin.Profile.ApplyAgent(player);
		plugin.Applier.BumpAllRenderWear(player);
		plugin.GloveApply.Apply(player, false, refresh: false);
		plugin.Applier.RefreshAll(player);
	}
}
