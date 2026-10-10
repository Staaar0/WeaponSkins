using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;

namespace WeaponSkins;

public sealed class GloveService
{
	private sealed record HandRefresh(int Token);
	private sealed record CustomItems(ulong SteamId, HashSet<ulong> Ids);
	private sealed record NativeGloves(
		ulong SteamId, ushort Def, int Quality, uint Level, ulong ItemId, uint ItemIdHigh,
		uint ItemIdLow, uint Account, uint Position, bool Initialized, string Name, string NameOverride);

	private readonly PlayerCache cache;
	private readonly WeaponSkins plugin;
	private readonly Dictionary<int, PlayerRef> appliedGloves = [];
	private readonly Dictionary<int, PlayerRef> pendingChanges = [];
	private readonly Dictionary<int, int> revisions = [];
	private readonly Dictionary<uint, HandRefresh> handRefreshes = [];
	private readonly Dictionary<(int Slot, CsTeam Team), NativeGloves> nativeGloves = [];
	private readonly Dictionary<int, CustomItems> customItems = [];
	private int generation;
	private int nextHandToken;

	public GloveService(WeaponSkins plugin, PlayerCache cache)
	{
		this.plugin = plugin;
		this.cache = cache;
	}

	public void Drop(int slot)
	{
		appliedGloves.Remove(slot);
		pendingChanges.Remove(slot);
		revisions[slot] = revisions.GetValueOrDefault(slot) + 1;
	}

	public void Forget(int slot)
	{
		Drop(slot);
		foreach (var key in nativeGloves.Keys.Where(key => key.Slot == slot).ToArray())
			nativeGloves.Remove(key);
		customItems.Remove(slot);
	}

	public void Reset(bool finishHands = false)
	{
		if (finishHands)
			foreach (var pawnHandle in handRefreshes.Keys.ToArray())
				FinishHands(pawnHandle, allowStopping: true);
		generation++;
		appliedGloves.Clear();
		pendingChanges.Clear();
		handRefreshes.Clear();
		nativeGloves.Clear();
		customItems.Clear();
		foreach (var slot in revisions.Keys.ToArray())
			revisions[slot] = revisions[slot] + 1;
	}

	public void Apply(CCSPlayerController player, bool draw, bool refresh = true)
	{
		if (plugin.Stopping)
			return;

		var target = PlayerRef.Capture(player);
		var loadout = cache.Get(player);
		if (target == null || loadout == null)
			return;

		var slot = player.Slot;
		var side = loadout.For(player.Team);
		// The pending menu change will apply after the agent model.
		if (!draw && side.GloveDef > 0 && side.GlovePaint > 0 &&
			pendingChanges.TryGetValue(slot, out var pending) && pending == target.Value)
			return;

		var revision = revisions.GetValueOrDefault(slot) + 1;
		revisions[slot] = revision;
		pendingChanges.Remove(slot);
		var currentGeneration = generation;
		if (side.GloveDef <= 0 || side.GlovePaint <= 0)
		{
			RestoreItem(target.Value, player.PlayerPawn.Value!);
			return;
		}
		if (!EconAttributes.Available)
			return;

		if (!draw)
		{
			ApplyItem(player, target.Value, side);
		}
		else
		{
			pendingChanges[slot] = target.Value;
			plugin.AddTimer(0.08f, () =>
			{
				if (!IsCurrent(slot, revision, currentGeneration))
					return;
				pendingChanges.Remove(slot);
				var current = target.Value.Resolve();
				var currentSide = current == null ? null : cache.Get(current)?.For(current.Team);
				if (current == null || currentSide == null || !EconAttributes.Available)
					return;
				if (currentSide.GloveDef <= 0 || currentSide.GlovePaint <= 0)
				{
					RestoreItem(target.Value, current.PlayerPawn.Value!);
					return;
				}

				ApplyItem(current, target.Value, currentSide);
				if (refresh)
					RefreshHands(current);
			}, TimerFlags.STOP_ON_MAPCHANGE);
		}
	}

	private bool IsCurrent(int slot, int revision, int currentGeneration)
	{
		return !plugin.Stopping && generation == currentGeneration &&
			revisions.TryGetValue(slot, out var current) && current == revision;
	}

	private void ApplyItem(CCSPlayerController player, PlayerRef target, TeamLoadout side)
	{
		var pawn = player.PlayerPawn.Value;
		if (pawn == null || !pawn.IsValid || pawn.EconGloves.Handle == IntPtr.Zero)
			return;

		var item = pawn.EconGloves;
		var gloves = side.Gloves;
		var seed = Math.Clamp(gloves.Seed, 0, 1000);
		var paint = plugin.Catalog.FindPaint(side.GloveDef, side.GlovePaint);
		var min = Math.Clamp(paint?.MinFloat ?? 0f, 0f, 1f);
		var max = Math.Clamp(paint?.MaxFloat ?? 1f, 0f, 1f);
		if (!float.IsFinite(min)) min = 0f;
		if (!float.IsFinite(max)) max = 1f;
		if (max < min) (min, max) = (max, min);
		var wear = float.IsFinite(gloves.Wear) ? Math.Clamp(gloves.Wear, min, max) : min;

		RememberNative(target, item);
		item.ItemDefinitionIndex = (ushort)side.GloveDef;
		EconAttributes.AssignItemId(item);
		SetPaint(item.NetworkedDynamicAttributes, side.GlovePaint, seed, wear);
		SetPaint(item.AttributeList, side.GlovePaint, seed, wear);
		item.Initialized = true;

		if (!customItems.TryGetValue(target.Slot, out var owned) || owned.SteamId != target.SteamId)
			customItems[target.Slot] = owned = new CustomItems(target.SteamId, []);
		owned.Ids.Add(item.ItemID);
		MarkChanged(pawn);
		appliedGloves[player.Slot] = target;
		TouchHands(pawn);
	}

	private static void SetPaint(CAttributeList attributes, int paint, int seed, float wear)
	{
		attributes.Attributes.RemoveAll();
		EconAttributes.Set(attributes, "set item texture prefab", paint);
		EconAttributes.Set(attributes, "set item texture seed", seed);
		EconAttributes.Set(attributes, "set item texture wear", wear);
	}

	private void RememberNative(PlayerRef target, CEconItemView item)
	{
		var key = (target.Slot, target.Team);
		if (nativeGloves.TryGetValue(key, out var native) && native.SteamId == target.SteamId)
			return;
		// CS2 can copy our gloves into a new pawn after death.
		if (IsCustomItem(target, item.ItemID))
			return;

		nativeGloves[key] = new NativeGloves(target.SteamId, item.ItemDefinitionIndex, item.EntityQuality, item.EntityLevel,
			item.ItemID, item.ItemIDHigh, item.ItemIDLow, item.AccountID, item.InventoryPosition,
			item.Initialized, item.CustomName, item.CustomNameOverride);
	}

	private bool IsCustomItem(PlayerRef target, ulong itemId)
	{
		return customItems.TryGetValue(target.Slot, out var owned) &&
			owned.SteamId == target.SteamId && owned.Ids.Contains(itemId);
	}

	private void RestoreItem(PlayerRef target, CCSPlayerPawn pawn)
	{
		if (!pawn.IsValid || pawn.EconGloves.Handle == IntPtr.Zero)
			return;
		var item = pawn.EconGloves;
		var owned = appliedGloves.TryGetValue(target.Slot, out var applied) && applied == target;
		owned |= IsCustomItem(target, item.ItemID);
		if (!owned)
			return;

		FinishHands(pawn.EntityHandle.Raw);
		appliedGloves.Remove(target.Slot);
		item.AttributeList.Attributes.RemoveAll();
		item.NetworkedDynamicAttributes.Attributes.RemoveAll();
		if (nativeGloves.TryGetValue((target.Slot, target.Team), out var native) && native.SteamId == target.SteamId)
		{
			item.ItemDefinitionIndex = native.Def;
			item.EntityQuality = native.Quality;
			item.EntityLevel = native.Level;
			item.ItemID = native.ItemId;
			item.ItemIDHigh = native.ItemIdHigh;
			item.ItemIDLow = native.ItemIdLow;
			item.AccountID = native.Account;
			item.InventoryPosition = native.Position;
			item.CustomName = native.Name;
			item.CustomNameOverride = native.NameOverride;
			item.Initialized = native.Initialized || native.Def != 0 || native.ItemId != 0;
		}
		else
		{
			item.ItemDefinitionIndex = 0;
			item.ItemID = item.ItemIDHigh = item.ItemIDLow = 0;
			item.Initialized = false;
		}
		var player = target.Resolve();
		if (player != null)
			Utilities.SetStateChanged(player, "CCSPlayerController", "m_pInventoryServices");
		MarkChanged(pawn);
		pawn.AcceptInput("SetBodygroup", value: "first_or_third_person,0");
	}

	private static void MarkChanged(CCSPlayerPawn pawn)
	{
		unchecked { pawn.EconGlovesChanged++; }
		Utilities.SetStateChanged(pawn, "CCSPlayerPawn", "m_nEconGlovesChanged");
	}

	private void TouchHands(CCSPlayerPawn pawn)
	{
		var token = ++nextHandToken;
		var currentGeneration = generation;
		var pawnHandle = pawn.EntityHandle.Raw;
		handRefreshes[pawnHandle] = new HandRefresh(token);
		pawn.AcceptInput("SetBodygroup", value: "first_or_third_person,0");
		plugin.AddTimer(0.2f, () =>
		{
			if (plugin.Stopping || generation != currentGeneration ||
				!handRefreshes.TryGetValue(pawnHandle, out var pending) || pending.Token != token)
				return;
			FinishHands(pawnHandle);
		}, TimerFlags.STOP_ON_MAPCHANGE);
	}

	private void FinishHands(uint pawnHandle, bool allowStopping = false)
	{
		if (!handRefreshes.Remove(pawnHandle) || (plugin.Stopping && !allowStopping))
			return;
		// Finish the pawn we refreshed, even if its player has respawned.
		var pawn = new CHandle<CCSPlayerPawn>(pawnHandle).Value;
		if (pawn != null && pawn.IsValid)
			pawn.AcceptInput("SetBodygroup", value: "first_or_third_person,1");
	}

	public void Restore(CCSPlayerController player)
	{
		if (plugin.Stopping)
			return;
		var target = PlayerRef.Capture(player);
		if (target == null)
			return;
		revisions[player.Slot] = revisions.GetValueOrDefault(player.Slot) + 1;
		pendingChanges.Remove(player.Slot);
		RestoreItem(target.Value, player.PlayerPawn.Value!);
		RefreshHands(player);
	}

	private void RefreshHands(CCSPlayerController player)
	{
		var weapon = player.PlayerPawn.Value?.WeaponServices?.ActiveWeapon.Value;
		if (weapon != null && weapon.IsValid && KnifeService.IsKnifeClass(weapon.DesignerName))
			plugin.Applier.RefreshKnife(player);
		else
			player.ExecuteClientCommand("slot3");
	}
}
