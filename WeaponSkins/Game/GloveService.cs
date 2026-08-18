using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;

namespace WeaponSkins;

public sealed class GloveService
{
	private sealed record GloveState(ulong SteamId, uint PawnHandle);

	private readonly PlayerCache cache;
	private readonly WeaponSkins plugin;
	private readonly Dictionary<int, GloveState> states = [];
	private readonly Dictionary<int, int> revisions = [];

	public GloveService(WeaponSkins plugin, PlayerCache cache)
	{
		this.plugin = plugin;
		this.cache = cache;
	}

	public void Drop(int slot)
	{
		states.Remove(slot);
		revisions[slot] = revisions.GetValueOrDefault(slot) + 1;
	}

	public void Apply(CCSPlayerController player, bool draw, bool refresh = true)
	{
		if (!player.IsValid || player.IsBot || player.SteamID == 0)
			return;

		var slot = player.Slot;
		var revision = revisions.GetValueOrDefault(slot) + 1;
		revisions[slot] = revision;

		if (!draw)
			QueueRetry(slot, player.SteamID, revision);

		var playerRef = PlayerRef.Capture(player);
		var loadout = cache.Get(player);
		if (playerRef == null || loadout == null || !EconAttributes.Available)
			return;

		var side = loadout.For(player.Team);
		if (side.GloveDef <= 0 || side.GlovePaint <= 0)
			return;

		var pawn = player.PlayerPawn.Value;
		if (pawn == null || !pawn.IsValid)
			return;

		if (!draw)
		{
			ApplyItem(player, pawn, side, playerRef.Value.PawnHandle, true);
			MarkChanged(pawn);
			pawn.AcceptInput("SetBodygroup", value: "default_gloves,1");
			pawn.AcceptInput("SetBodygroup", value: "first_or_third_person,1");
			return;
		}

		ClearItem(pawn.EconGloves);
		MarkChanged(pawn);
		pawn.AcceptInput("SetBodygroup", value: "default_gloves,0");
		pawn.AcceptInput("SetBodygroup", value: "first_or_third_person,0");

		Server.NextFrame(() =>
		{
			if (!IsCurrent(slot, revision))
				return;

			var current = playerRef.Value.Resolve();
			var alivePawn = current?.PlayerPawn.Value;
			var currentLoadout = current == null ? null : cache.Get(current);
			if (current == null || currentLoadout == null || alivePawn == null || !alivePawn.IsValid)
				return;

			var currentSide = currentLoadout.For(current.Team);
			if (currentSide.GloveDef <= 0 || currentSide.GlovePaint <= 0)
				return;

			ApplyItem(current, alivePawn, currentSide, playerRef.Value.PawnHandle, true);
			MarkChanged(alivePawn);
			alivePawn.AcceptInput("SetBodygroup", value: "default_gloves,1");
			alivePawn.AcceptInput("SetBodygroup", value: "first_or_third_person,0");
			if (refresh)
				plugin.Applier.RefreshViewModel(current);

			plugin.AddTimer(0.2f, () =>
			{
				if (!IsCurrent(slot, revision))
					return;

				var refreshed = playerRef.Value.Resolve();
				var refreshedPawn = refreshed?.PlayerPawn.Value;
				if (refreshedPawn != null && refreshedPawn.IsValid)
					refreshedPawn.AcceptInput("SetBodygroup", value: "first_or_third_person,1");
			}, TimerFlags.STOP_ON_MAPCHANGE);
		});
	}

	private void QueueRetry(int slot, ulong steamId, int revision)
	{
		var stagger = (slot & 7) * 0.01f;
		QueueApply(slot, steamId, revision, 0.04f + stagger);
		QueueApply(slot, steamId, revision, 0.5f + stagger);
		QueueApply(slot, steamId, revision, 1.5f + stagger);
	}

	private void QueueApply(int slot, ulong steamId, int revision, float delay)
	{
		plugin.AddTimer(delay, () =>
		{
			if (!IsCurrent(slot, revision))
				return;

			var current = Utilities.GetPlayerFromSlot(slot);
			if (current == null || !current.IsValid || current.IsBot ||
				current.SteamID != steamId || !current.PawnIsAlive)
				return;

			var alivePawn = current.PlayerPawn.Value;
			var currentLoadout = cache.Get(current);
			if (currentLoadout == null || alivePawn == null || !alivePawn.IsValid)
				return;

			var currentSide = currentLoadout.For(current.Team);
			if (currentSide.GloveDef <= 0 || currentSide.GlovePaint <= 0)
				return;

			ApplyItem(current, alivePawn, currentSide, alivePawn.EntityHandle.Raw, false);
			MarkChanged(alivePawn);
			alivePawn.AcceptInput("SetBodygroup", value: "default_gloves,1");
			alivePawn.AcceptInput("SetBodygroup", value: "first_or_third_person,1");
		}, TimerFlags.STOP_ON_MAPCHANGE);
	}

	private bool IsCurrent(int slot, int revision)
	{
		return revisions.TryGetValue(slot, out var currentRevision) && currentRevision == revision;
	}

	private void ApplyItem(
		CCSPlayerController player,
		CCSPlayerPawn pawn,
		TeamLoadout side,
		uint pawnHandle,
		bool renewIdentity)
	{
		if (!pawn.IsValid)
			return;

		var item = pawn.EconGloves;
		if (item.Handle == IntPtr.Zero)
			return;

		var newPawn = !states.TryGetValue(player.Slot, out var state) ||
			state.SteamId != player.SteamID || state.PawnHandle != pawnHandle;

		if (newPawn)
			states[player.Slot] = new GloveState(player.SteamID, pawnHandle);

		if (renewIdentity || newPawn)
			EconAttributes.MarkCustom(item, player.SteamID);

		item.ItemDefinitionIndex = (ushort)side.GloveDef;
		item.EntityQuality = 3;
		item.CustomName = "";

		item.NetworkedDynamicAttributes.Attributes.RemoveAll();
		EconAttributes.Set(item.NetworkedDynamicAttributes, "set item texture prefab", side.GlovePaint);
		EconAttributes.Set(item.NetworkedDynamicAttributes, "set item texture seed", side.Gloves.Seed);
		EconAttributes.Set(item.NetworkedDynamicAttributes, "set item texture wear", side.Gloves.Wear);

		item.AttributeList.Attributes.RemoveAll();
		EconAttributes.Set(item.AttributeList, "set item texture prefab", side.GlovePaint);
		EconAttributes.Set(item.AttributeList, "set item texture seed", side.Gloves.Seed);
		EconAttributes.Set(item.AttributeList, "set item texture wear", side.Gloves.Wear);

		item.Initialized = true;
	}

	private static void ClearItem(CEconItemView item)
	{
		if (item.Handle == IntPtr.Zero)
			return;

		item.ItemDefinitionIndex = 0;
		item.ItemID = 0;
		item.ItemIDLow = 0;
		item.ItemIDHigh = 0;
		item.AccountID = 0;
		item.EntityQuality = 0;
		item.CustomName = "";
		item.Initialized = false;
		item.NetworkedDynamicAttributes.Attributes.RemoveAll();
		item.AttributeList.Attributes.RemoveAll();
	}

	private static void MarkChanged(CCSPlayerPawn pawn)
	{
		unchecked
		{
			pawn.EconGlovesChanged++;
		}

		Utilities.SetStateChanged(pawn, "CCSPlayerPawn", "m_nEconGlovesChanged");
	}

	public void Restore(CCSPlayerController player)
	{
		var playerRef = PlayerRef.Capture(player);
		revisions[player.Slot] = revisions.GetValueOrDefault(player.Slot) + 1;
		states.Remove(player.Slot);
		if (playerRef == null)
			return;

		var pawn = player.PlayerPawn.Value;
		if (pawn == null || !pawn.IsValid)
			return;

		ClearItem(pawn.EconGloves);
		MarkChanged(pawn);
		pawn.AcceptInput("SetBodygroup", value: "default_gloves,0");
		pawn.AcceptInput("SetBodygroup", value: "first_or_third_person,0");
		plugin.Applier.RefreshViewModel(player);
	}
}
