using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;

namespace WeaponSkins;

public sealed class ProfileService
{
	private static readonly float[] DefaultRetries = [0.25f, 1f, 3f, 5f];

	private const float ReassertSeconds = 5f;

	private readonly WeaponSkins plugin;
	private readonly PlayerCache cache;
	private readonly Dictionary<int, ushort> originalMusic = [];
	private readonly Dictionary<int, MedalRank_t> originalPin = [];
	private readonly Dictionary<int, int> agentRevisions = [];
	private readonly Dictionary<int, int> profileRevisions = [];
	private bool reassertStarted;

	public ProfileService(WeaponSkins plugin, PlayerCache cache)
	{
		this.plugin = plugin;
		this.cache = cache;
	}

	public void Start()
	{
		if (reassertStarted)
			return;

		reassertStarted = true;
		plugin.AddTimer(ReassertSeconds, () =>
		{
			if (plugin.Stopping)
				return;

			foreach (var player in Utilities.GetPlayers())
			{
				if (!player.IsValid || player.IsBot || player.SteamID == 0 || !NeedsProfile(player))
					continue;

				ApplyMusic(player);
				ApplyPin(player);
			}
		}, TimerFlags.REPEAT);
	}

	public void Drop(int slot)
	{
		originalMusic.Remove(slot);
		originalPin.Remove(slot);
		agentRevisions[slot] = agentRevisions.GetValueOrDefault(slot) + 1;
		profileRevisions[slot] = profileRevisions.GetValueOrDefault(slot) + 1;
	}

	public void DropAll()
	{
		foreach (var slot in profileRevisions.Keys.ToList())
			profileRevisions[slot] = profileRevisions[slot] + 1;
	}

	public void Schedule(CCSPlayerController player, params float[] delays)
	{
		if (!player.IsValid || player.IsBot)
			return;

		var slot = player.Slot;
		var steamId = player.SteamID;
		var revision = profileRevisions.GetValueOrDefault(slot) + 1;
		profileRevisions[slot] = revision;

		ApplyMusic(player);
		ApplyPin(player);

		foreach (var delay in delays.Length > 0 ? delays : DefaultRetries)
		{
			plugin.AddTimer(delay, () =>
			{
				if (profileRevisions.GetValueOrDefault(slot) != revision)
					return;

				var current = Utilities.GetPlayerFromSlot(slot);
				if (current == null || !current.IsValid || current.IsBot || current.SteamID != steamId)
					return;

				if (!NeedsProfile(current))
					return;

				ApplyMusic(current);
				ApplyPin(current);
			}, TimerFlags.STOP_ON_MAPCHANGE);
		}
	}

	public void ApplyAgent(CCSPlayerController player)
	{
		var playerRef = PlayerRef.Capture(player);
		var loadout = cache.Get(player);
		if (playerRef == null || loadout == null)
			return;

		var model = loadout.For(player.Team).AgentModel;
		if (string.IsNullOrEmpty(model))
			return;

		var slot = player.Slot;
		var revision = agentRevisions.GetValueOrDefault(slot) + 1;
		agentRevisions[slot] = revision;
		Server.NextFrame(() =>
		{
			if (agentRevisions.GetValueOrDefault(slot) != revision)
				return;

			var current = playerRef.Value.Resolve();
			var pawn = current?.PlayerPawn.Value;
			if (current == null || pawn == null || !pawn.IsValid)
				return;

			pawn.SetModel(model);
			var currentLoadout = cache.Get(current);
			if (currentLoadout == null)
				return;

			var side = currentLoadout.For(current.Team);
			if (side.GloveDef > 0 && side.GlovePaint > 0)
			{
				pawn.AcceptInput("SetBodygroup", value: "default_gloves,1");
				pawn.AcceptInput("SetBodygroup", value: "first_or_third_person,1");
			}
		});
	}

	public bool NeedsProfile(CCSPlayerController player)
	{
		var loadout = cache.Get(player);
		var inventory = player.InventoryServices;
		if (loadout == null || inventory == null || inventory.Rank.Length <= 5)
			return false;

		if (loadout.MusicKit > 0 && inventory.MusicID != loadout.MusicKit)
			return true;

		return loadout.Pin > 0 && (int)inventory.Rank[5] != loadout.Pin;
	}

	public void ApplyMusic(CCSPlayerController player)
	{
		var loadout = cache.Get(player);
		if (loadout == null || player.InventoryServices == null)
			return;

		var kit = loadout.MusicKit;
		if (kit > 0)
		{
			if (!originalMusic.ContainsKey(player.Slot))
				originalMusic[player.Slot] = player.InventoryServices.MusicID;

			SetMusic(player, (ushort)kit);
		}
		else if (originalMusic.Remove(player.Slot, out var original))
		{
			SetMusic(player, original);
		}
	}

	private static void SetMusic(CCSPlayerController player, ushort kit)
	{
		player.MusicKitID = kit;
		player.InventoryServices!.MusicID = kit;
		Utilities.SetStateChanged(player, "CCSPlayerController", "m_iMusicKitID");
		Utilities.SetStateChanged(player, "CCSPlayerController", "m_pInventoryServices");
	}

	public void ApplyPin(CCSPlayerController player)
	{
		var loadout = cache.Get(player);
		var inventory = player.InventoryServices;
		if (loadout == null || inventory == null || inventory.Rank.Length <= 5)
			return;

		var pin = loadout.Pin;
		if (pin > 0)
		{
			if (!originalPin.ContainsKey(player.Slot))
				originalPin[player.Slot] = inventory.Rank[5];

			SetPin(player, (MedalRank_t)pin);
		}
		else if (originalPin.Remove(player.Slot, out var original))
		{
			SetPin(player, original);
		}
	}

	private static void SetPin(CCSPlayerController player, MedalRank_t pin)
	{
		var inventory = player.InventoryServices;
		if (inventory == null || inventory.Rank.Length <= 5)
			return;

		inventory.Rank[5] = pin;
		Utilities.SetStateChanged(player, "CCSPlayerController", "m_pInventoryServices");
	}
}
