using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;

namespace WeaponSkins;

public sealed class ProfileService
{
	private readonly PlayerCache cache;
	private readonly Dictionary<int, ushort> originalMusic = [];
	private readonly Dictionary<int, MedalRank_t> originalPin = [];
	private readonly Dictionary<int, int> agentRevisions = [];

	public ProfileService(PlayerCache cache)
	{
		this.cache = cache;
	}

	public void Drop(int slot)
	{
		originalMusic.Remove(slot);
		originalPin.Remove(slot);
		agentRevisions[slot] = agentRevisions.GetValueOrDefault(slot) + 1;
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
