using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;

namespace WeaponSkins;

public sealed class WeaponApplier
{
	private const int MaxStickerSlot = 5;

	private const float StickerWearStep = 0.001f;

	private readonly WeaponSkins plugin;
	private readonly PlayerCache cache;
	private readonly CatalogService catalog;
	private readonly Dictionary<int, Dictionary<int, float>> stickerWear = [];
	private readonly HashSet<int> refreshing = [];
	private readonly Dictionary<int, List<string?>> pendingRefreshes = [];
	private readonly Dictionary<int, int> lifeRevisions = [];
	private readonly Dictionary<int, int> activeRefreshRevisions = [];
	private int patternSeed = 1;
	private int mapGeneration;
	private int nextRefreshRevision;

	private readonly record struct RefreshContext(
		int Slot,
		ulong SteamId,
		CsTeam Team,
		uint PawnHandle,
		int LifeRevision,
		int MapGeneration,
		int RefreshRevision);

	public WeaponApplier(WeaponSkins plugin, PlayerCache cache, CatalogService catalog)
	{
		this.plugin = plugin;
		this.cache = cache;
		this.catalog = catalog;
	}

	public void Apply(CCSPlayerController player, CBasePlayerWeapon weapon)
	{
		var loadout = cache.Get(player);
		if (loadout == null)
			return;

		var side = loadout.For(player.Team);
		var item = weapon.AttributeManager.Item;
		var defIndex = (int)item.ItemDefinitionIndex;
		var isKnife = KnifeService.IsKnifeClass(weapon.DesignerName);

		if (isKnife)
		{
			var knifeDef = side.Knife > 0 ? side.Knife : KnifeService.DefaultDef(player.Team);
			if (defIndex != knifeDef)
			{
				weapon.AcceptInput("ChangeSubclass", value: knifeDef.ToString());
				item.ItemDefinitionIndex = (ushort)knifeDef;
				defIndex = knifeDef;
			}

			if (side.Knife <= 0)
			{
				item.AttributeList.Attributes.RemoveAll();
				item.NetworkedDynamicAttributes.Attributes.RemoveAll();
				item.CustomName = "";
				item.EntityQuality = 0;
				weapon.FallbackPaintKit = 0;
				weapon.FallbackSeed = 0;
				weapon.FallbackWear = 0f;
				weapon.FallbackStatTrak = -1;
				item.Initialized = true;
				return;
			}

			item.EntityQuality = 3;
		}

		side.Weapons.TryGetValue(defIndex, out var entry);
		if (entry == null || entry.Paint <= 0)
		{
			if (entry != null)
			{
				item.AttributeList.Attributes.RemoveAll();
				item.NetworkedDynamicAttributes.Attributes.RemoveAll();
				item.CustomName = "";
				weapon.FallbackPaintKit = 0;
				weapon.FallbackSeed = 0;
				weapon.FallbackWear = 0f;
				weapon.FallbackStatTrak = -1;
				if (!isKnife)
					item.EntityQuality = 0;
			}

			if (isKnife)
				EconAttributes.MarkCustom(item, player.SteamID);
			item.Initialized = true;
			return;
		}

		var paint = catalog.FindPaint(defIndex, entry.Paint);
		var seedValue = Math.Clamp(entry.Seed, 0, 1000);
		var seed = seedValue == 0 && entry.Paint is 38 or 417 or 568 or 59 or 44 or 12 ? NextPatternSeed() : seedValue;
		var wear = ClampWear(entry.Wear, paint, isKnife);
		entry.Seed = seedValue;
		entry.Wear = wear;
		weapon.FallbackPaintKit = entry.Paint;
		weapon.FallbackSeed = seed;
		weapon.FallbackWear = wear;
		weapon.FallbackStatTrak = entry.StatTrak;
		ApplyItemAttributes(player, item, entry, isKnife, seed, wear);

		if (StoredStickerWear(player.Slot, defIndex) is { } bumped)
			weapon.FallbackWear = bumped;

		if (paint != null && !isKnife)
			weapon.AcceptInput("SetBodygroup", value: $"body,{(paint.Legacy ? 1 : 0)}");
	}

	private static float ClampWear(float wear, PaintDef? paint, bool isKnife)
	{
		var minimum = Math.Clamp(Math.Max(paint?.MinFloat ?? 0f, isKnife ? KnifeService.MinimumWear : 0.000001f), 0f, 1f);
		var maximum = Math.Max(minimum, Math.Clamp(paint?.MaxFloat ?? 1f, 0f, 1f));
		return float.IsFinite(wear) ? Math.Clamp(wear, minimum, maximum) : minimum;
	}

	private void ApplyItemAttributes(CCSPlayerController player, CEconItemView item, WeaponEntry entry, bool isKnife, int seed, float wear)
	{
		item.AttributeList.Attributes.RemoveAll();
		item.NetworkedDynamicAttributes.Attributes.RemoveAll();
		EconAttributes.MarkCustom(item, player.SteamID);
		item.CustomName = entry.NameTag ?? "";
		item.EntityQuality = isKnife ? 3 : entry.StatTrak >= 0 ? 9 : 0;
		ApplyPaintAttributes(item, entry.Paint, seed, wear);

		if (entry.StatTrak >= 0)
		{
			EconAttributes.Set(item.NetworkedDynamicAttributes, "kill eater", EconAttributes.AsFloat(entry.StatTrak));
			EconAttributes.Set(item.NetworkedDynamicAttributes, "kill eater score type", 0);
			EconAttributes.Set(item.AttributeList, "kill eater", EconAttributes.AsFloat(entry.StatTrak));
			EconAttributes.Set(item.AttributeList, "kill eater score type", 0);
		}

		for (var slot = 0; slot <= MaxStickerSlot; slot++)
		{
			var sticker = entry.StickerAt(slot);
			if (sticker != null)
				ApplySticker(item, sticker);
			else
				EconAttributes.Set(item.NetworkedDynamicAttributes, $"sticker slot {slot} id", 0f);
		}

		if (entry.Charm != null)
			ApplyCharm(item, entry.Charm);

		item.Initialized = true;
	}

	private static void ApplyPaintAttributes(CEconItemView item, int paint, int seed, float wear)
	{
		EconAttributes.Set(item.NetworkedDynamicAttributes, "set item texture prefab", paint);
		EconAttributes.Set(item.NetworkedDynamicAttributes, "set item texture seed", seed);
		EconAttributes.Set(item.NetworkedDynamicAttributes, "set item texture wear", wear);
		EconAttributes.Set(item.AttributeList, "set item texture prefab", paint);
		EconAttributes.Set(item.AttributeList, "set item texture seed", seed);
		EconAttributes.Set(item.AttributeList, "set item texture wear", wear);
	}

	private int NextPatternSeed()
	{
		var seed = patternSeed++;
		if (patternSeed > 1000)
			patternSeed = 1;
		return seed;
	}

	private void ApplySticker(CEconItemView item, StickerEntry sticker)
	{
		if (sticker.Id <= 0 || sticker.Slot < 0 || sticker.Slot > MaxStickerSlot)
			return;

		var list = item.NetworkedDynamicAttributes;
		var schema = Math.Max(sticker.Schema, 0);

		EconAttributes.Set(list, $"sticker slot {sticker.Slot} id", EconAttributes.AsFloat(sticker.Id));

		if (schema != 0 || sticker.Slot >= 4)
			EconAttributes.Set(list, $"sticker slot {sticker.Slot} schema", EconAttributes.AsFloat(schema));

		if (sticker.OffsetX != 0f)
			EconAttributes.Set(list, $"sticker slot {sticker.Slot} offset x", sticker.OffsetX);
		if (sticker.OffsetY != 0f)
			EconAttributes.Set(list, $"sticker slot {sticker.Slot} offset y", sticker.OffsetY);

		EconAttributes.Set(list, $"sticker slot {sticker.Slot} wear", sticker.Wear);

		if (sticker.Scale != 0f)
			EconAttributes.Set(list, $"sticker slot {sticker.Slot} scale", sticker.Scale);
		if (sticker.Rotation != 0f)
			EconAttributes.Set(list, $"sticker slot {sticker.Slot} rotation", sticker.Rotation);
	}

	private float? StoredStickerWear(int slot, int defIndex)
	{
		return stickerWear.TryGetValue(slot, out var byWeapon) && byWeapon.TryGetValue(defIndex, out var wear)
			? wear
			: null;
	}

	private void BumpStickerWear(int slot, int defIndex, float wear)
	{
		if (!stickerWear.TryGetValue(slot, out var byWeapon))
			stickerWear[slot] = byWeapon = [];

		var next = byWeapon.TryGetValue(defIndex, out var previous) ? previous + StickerWearStep : wear + StickerWearStep;

		if (next <= wear || next > wear + 10f * StickerWearStep)
			next = wear + StickerWearStep;

		byWeapon[defIndex] = Math.Min(next, 1f);
	}

	private static void ApplyCharm(CEconItemView item, CharmEntry charm)
	{
		if (charm.Id <= 0)
			return;

		var list = item.NetworkedDynamicAttributes;
		EconAttributes.Set(list, "keychain slot 0 id", EconAttributes.AsFloat(charm.Id));
		EconAttributes.Set(list, "keychain slot 0 offset x", charm.OffsetX);
		EconAttributes.Set(list, "keychain slot 0 offset y", charm.OffsetY);
		EconAttributes.Set(list, "keychain slot 0 offset z", charm.OffsetZ);
		EconAttributes.Set(list, "keychain slot 0 seed", EconAttributes.AsFloat(charm.Pattern));
		EconAttributes.Set(list, "keychain slot 0 pattern", EconAttributes.AsFloat(charm.Pattern));

		if (charm.Sticker > 0)
			EconAttributes.Set(list, "keychain slot 0 sticker", EconAttributes.AsFloat(charm.Sticker));
		if (charm.Highlight > 0)
			EconAttributes.Set(list, "keychain slot 0 highlight", EconAttributes.AsFloat(charm.Highlight));
	}

	public void RefreshOwned(CCSPlayerController player, int defIndex)
	{
		BumpRenderWear(player, defIndex);

		var slotCommand = FindSlotCommand(player, defIndex);
		if (slotCommand != null)
			RefreshAll(player, slotCommand);
	}

	public void RefreshKnife(CCSPlayerController player)
	{
		if (PlayerHasKnife(player))
			RefreshAll(player, "slot3");
	}

	public void RefreshViewModel(CCSPlayerController player)
	{
		var weapon = player.PlayerPawn.Value?.WeaponServices?.ActiveWeapon.Value;
		var slotCommand = GetSlotCommand(weapon);
		if (slotCommand != null)
			RefreshAll(player, slotCommand);
	}

	private void BumpRenderWear(CCSPlayerController player, int defIndex)
	{
		var entry = cache.Get(player)?.For(player.Team).Weapons.GetValueOrDefault(defIndex);
		if (entry is { Paint: > 0 })
			BumpStickerWear(player.Slot, defIndex, entry.Wear);
	}

	public void ApplyInventory(CCSPlayerController player)
	{
		if (!player.IsValid || !player.PawnIsAlive || cache.Get(player) == null)
			return;

		var weapons = player.PlayerPawn.Value?.WeaponServices?.MyWeapons;
		if (weapons == null)
			return;

		foreach (var handle in weapons)
		{
			var weapon = handle.Value;
			if (weapon != null && weapon.IsValid)
				Apply(player, weapon);
		}
	}

	public void SyncSpawnViewModel(CCSPlayerController player)
	{
		if (!player.IsValid || !player.PawnIsAlive || cache.Get(player) == null)
			return;

		var weapon = player.PlayerPawn.Value?.WeaponServices?.ActiveWeapon.Value;
		Utilities.SetStateChanged(player, "CCSPlayerController", "m_pInventoryServices");

		var slotCommand = GetSlotCommand(weapon);
		if (slotCommand != null)
			player.ExecuteClientCommand(slotCommand);
	}

	public void RefreshAll(CCSPlayerController player, string? slotCommand = null)
	{
		var loadout = cache.Get(player);
		if (!player.IsValid || !player.PawnIsAlive || loadout == null)
			return;

		var playerSlot = player.Slot;
		if (!refreshing.Add(playerSlot))
		{
			if (!pendingRefreshes.TryGetValue(playerSlot, out var pending))
				pendingRefreshes[playerSlot] = pending = [];

			if (slotCommand == null)
			{
				pending.Clear();
				pending.Add(null);
			}
			else if (!pending.Contains(null) && !pending.Contains(slotCommand))
			{
				pending.Add(slotCommand);
			}
			return;
		}

		var pawn = player.PlayerPawn.Value;
		var weapons = pawn?.WeaponServices?.MyWeapons;
		if (pawn == null || !pawn.IsValid || weapons == null || player.Team is CsTeam.None or CsTeam.Spectator)
		{
			refreshing.Remove(playerSlot);
			return;
		}

		var context = BeginRefresh(player, pawn);
		var restoreSlotCommand = slotCommand ?? GetSlotCommand(pawn.WeaponServices?.ActiveWeapon.Value);
		var hasKnife = false;
		List<uint> weaponsToKill = [];
		Dictionary<string, List<(int Clip, int Reserve)>> weaponsWithAmmo = [];

		foreach (var handle in weapons)
		{
			var weapon = handle.Value;
			if (weapon == null || !weapon.IsValid || !weapon.DesignerName.StartsWith("weapon_", StringComparison.Ordinal))
				continue;

			var isKnife = KnifeService.IsKnifeClass(weapon.DesignerName);
			var weaponSlot = isKnife ? "slot3" : GetSlotCommand(weapon);
			if (slotCommand != null && weaponSlot != slotCommand)
				continue;

			if (isKnife)
			{
				weaponsToKill.Add(weapon.EntityHandle.Raw);
				hasKnife = true;
				continue;
			}

			if (weaponSlot is "slot1" or "slot2")
			{
				var defIndex = (int)weapon.AttributeManager.Item.ItemDefinitionIndex;
				var className = catalog.WeaponClasses.GetValueOrDefault(defIndex) ?? weapon.DesignerName;
				if (!weaponsWithAmmo.TryGetValue(className, out var ammo))
					weaponsWithAmmo[className] = ammo = [];

				var reserve = weapon.ReserveAmmo.Length > 0 ? weapon.ReserveAmmo[0] : 0;
				ammo.Add((weapon.Clip1, reserve));
				weaponsToKill.Add(weapon.EntityHandle.Raw);
			}
		}

		var needsKnife = hasKnife;
		var killStageComplete = false;
		var refreshCancelled = false;

		plugin.AddTimer(0.1f, () =>
		{
			var current = ResolveRefreshPlayer(context);
			if (current == null)
			{
				refreshCancelled = true;
				CancelRefresh(context);
				return;
			}

			var currentPawn = current.PlayerPawn.Value;
			if (currentPawn == null || !currentPawn.IsValid)
			{
				refreshCancelled = true;
				CancelRefresh(context);
				return;
			}

			foreach (var weaponHandle in weaponsToKill)
			{
				var weapon = ResolveWeapon(weaponHandle);
				if (weapon != null && OwnedBy(weapon, currentPawn))
					weapon.AddEntityIOEvent("Kill", weapon, null, "", 0f);
			}

			killStageComplete = true;
		}, TimerFlags.STOP_ON_MAPCHANGE);

		plugin.AddTimer(0.23f, () =>
		{
			if (refreshCancelled)
				return;

			if (!killStageComplete)
			{
				CancelRefresh(context);
				return;
			}

			var current = ResolveRefreshPlayer(context);
			if (current == null)
			{
				CancelRefresh(context);
				return;
			}

			if (needsKnife && !PlayerHasKnife(current))
			{
				var knifeClass = DefaultKnifeClass(current.Team);
				var firstKnifeHandle = GiveWeapon(current, knifeClass);
				var temporaryPistolHandle = GiveWeapon(current, "weapon_usp_silencer");
				var finalKnifeHandle = GiveWeapon(current, knifeClass);

				Server.NextFrame(() =>
				{
					var nextPlayer = ResolveRefreshPlayer(context);
					if (nextPlayer == null)
						return;

					var finalKnife = ResolveWeapon(finalKnifeHandle);
					var firstKnife = ResolveWeapon(firstKnifeHandle);
					var temporaryPistol = ResolveWeapon(temporaryPistolHandle);
					var knife = finalKnife ?? firstKnife;

					if (finalKnife != null && firstKnife != null)
						firstKnife.AddEntityIOEvent("Kill", firstKnife, null, "", 0.01f);
					if (temporaryPistol != null)
						temporaryPistol.AddEntityIOEvent("Kill", temporaryPistol, null, "", 0.01f);
					if (knife != null)
					{
						Apply(nextPlayer, knife);
						Utilities.SetStateChanged(nextPlayer, "CCSPlayerController", "m_pInventoryServices");
					}
				});
			}

			foreach (var entry in weaponsWithAmmo)
			{
				foreach (var ammo in entry.Value)
				{
					var replacementHandle = GiveWeapon(current, entry.Key);
					if (replacementHandle == 0)
						continue;

					Server.NextFrame(() =>
					{
						var nextPlayer = ResolveRefreshPlayer(context);
						var replacement = ResolveWeapon(replacementHandle);
						if (nextPlayer == null || replacement == null)
							return;

						replacement.Clip1 = ammo.Clip;
						if (replacement.ReserveAmmo.Length > 0)
							replacement.ReserveAmmo[0] = ammo.Reserve;
					});
				}
			}

			RestoreActiveWeaponSlot(context, restoreSlotCommand);
			plugin.AddTimer(0.12f, () => CompleteRefresh(context), TimerFlags.STOP_ON_MAPCHANGE);
		}, TimerFlags.STOP_ON_MAPCHANGE);
	}

	private RefreshContext BeginRefresh(CCSPlayerController player, CCSPlayerPawn pawn)
	{
		var slot = player.Slot;
		var revision = unchecked(++nextRefreshRevision);
		if (revision == 0)
			revision = unchecked(++nextRefreshRevision);

		activeRefreshRevisions[slot] = revision;
		return new RefreshContext(
			slot,
			player.SteamID,
			player.Team,
			pawn.EntityHandle.Raw,
			lifeRevisions.GetValueOrDefault(slot),
			mapGeneration,
			revision);
	}

	private CCSPlayerController? ResolveRefreshPlayer(RefreshContext context)
	{
		if (context.MapGeneration != mapGeneration ||
			!activeRefreshRevisions.TryGetValue(context.Slot, out var revision) || revision != context.RefreshRevision ||
			lifeRevisions.GetValueOrDefault(context.Slot) != context.LifeRevision)
			return null;

		var player = ResolvePlayer(context.Slot);
		if (player == null || player.SteamID != context.SteamId || player.Team != context.Team || !player.PawnIsAlive)
			return null;

		var pawn = player.PlayerPawn.Value;
		return pawn is { IsValid: true } && pawn.EntityHandle.Raw == context.PawnHandle ? player : null;
	}

	private void CancelRefresh(RefreshContext context)
	{
		if (!activeRefreshRevisions.TryGetValue(context.Slot, out var revision) || revision != context.RefreshRevision)
			return;

		activeRefreshRevisions.Remove(context.Slot);
		refreshing.Remove(context.Slot);
		pendingRefreshes.Remove(context.Slot);
	}

	private static uint GiveWeapon(CCSPlayerController player, string className)
	{
		var handle = player.GiveNamedItem(className);
		if (handle == 0)
			return 0;

		var weapon = new CBasePlayerWeapon(handle);
		return weapon.EntityHandle.Raw;
	}

	private static CCSPlayerController? ResolvePlayer(int slot)
	{
		var player = Utilities.GetPlayerFromSlot(slot);
		return player != null && player.IsValid && !player.IsBot ? player : null;
	}

	private static CBasePlayerWeapon? ResolveWeapon(uint rawHandle)
	{
		if (rawHandle == 0)
			return null;

		var weapon = new CHandle<CBasePlayerWeapon>(rawHandle).Value;
		return weapon is { IsValid: true } ? weapon : null;
	}

	private static bool OwnedBy(CBasePlayerWeapon weapon, CCSPlayerPawn pawn)
	{
		var owner = weapon.OwnerEntity.Value;
		return owner != null && owner.IsValid && owner.EntityHandle.Raw == pawn.EntityHandle.Raw;
	}

	private string? FindSlotCommand(CCSPlayerController player, int defIndex)
	{
		if (KnifeService.IsKnifeDef(defIndex))
			return PlayerHasKnife(player) ? "slot3" : null;

		var weapons = player.PlayerPawn.Value?.WeaponServices?.MyWeapons;
		if (weapons == null)
			return null;

		foreach (var handle in weapons)
		{
			var weapon = handle.Value;
			if (weapon != null && weapon.IsValid && weapon.AttributeManager.Item.ItemDefinitionIndex == defIndex)
				return GetSlotCommand(weapon);
		}

		return null;
	}

	private void RestoreActiveWeaponSlot(RefreshContext context, string? slotCommand)
	{
		if (slotCommand == null)
			return;

		plugin.AddTimer(0.05f, () =>
		{
			var player = ResolveRefreshPlayer(context);
			if (player != null)
				player.ExecuteClientCommand(slotCommand);
		}, TimerFlags.STOP_ON_MAPCHANGE);
	}

	private void CompleteRefresh(RefreshContext context)
	{
		if (!activeRefreshRevisions.TryGetValue(context.Slot, out var revision) || revision != context.RefreshRevision)
			return;

		if (!pendingRefreshes.TryGetValue(context.Slot, out var pending) || pending.Count == 0)
		{
			activeRefreshRevisions.Remove(context.Slot);
			refreshing.Remove(context.Slot);
			return;
		}

		if (ResolveRefreshPlayer(context) == null)
		{
			CancelRefresh(context);
			return;
		}

		plugin.AddTimer(0.05f, () =>
		{
			var current = ResolveRefreshPlayer(context);
			if (current == null)
			{
				CancelRefresh(context);
				return;
			}

			if (!pendingRefreshes.TryGetValue(context.Slot, out var queued) || queued.Count == 0)
			{
				activeRefreshRevisions.Remove(context.Slot);
				refreshing.Remove(context.Slot);
				return;
			}

			var nextSlot = queued[0];
			queued.RemoveAt(0);
			if (queued.Count == 0)
				pendingRefreshes.Remove(context.Slot);

			activeRefreshRevisions.Remove(context.Slot);
			refreshing.Remove(context.Slot);
			RefreshAll(current, nextSlot);
		}, TimerFlags.STOP_ON_MAPCHANGE);
	}

	private static bool PlayerHasKnife(CCSPlayerController player)
	{
		var weapons = player.PlayerPawn.Value?.WeaponServices?.MyWeapons;
		if (weapons == null)
			return false;

		foreach (var handle in weapons)
		{
			var weapon = handle.Value;
			if (weapon != null && weapon.IsValid && KnifeService.IsKnifeClass(weapon.DesignerName))
				return true;
		}

		return false;
	}

	private static string DefaultKnifeClass(CsTeam team)
	{
		return team == CsTeam.Terrorist ? "weapon_knife_t" : "weapon_knife";
	}

	private static gear_slot_t? GetGearSlot(CBasePlayerWeapon weapon)
	{
		try
		{
			return weapon.As<CCSWeaponBase>().VData?.GearSlot;
		}
		catch
		{
			return null;
		}
	}

	private static string? GetSlotCommand(CBasePlayerWeapon? weapon)
	{
		if (weapon == null || !weapon.IsValid)
			return null;

		try
		{
			return GetGearSlot(weapon) switch
			{
				gear_slot_t.GEAR_SLOT_RIFLE => "slot1",
				gear_slot_t.GEAR_SLOT_PISTOL => "slot2",
				gear_slot_t.GEAR_SLOT_KNIFE => "slot3",
				_ => null
			};
		}
		catch
		{
			return null;
		}
	}



	public void AdvanceLife(int slot)
	{
		lifeRevisions[slot] = unchecked(lifeRevisions.GetValueOrDefault(slot) + 1);
		activeRefreshRevisions.Remove(slot);
		refreshing.Remove(slot);
		pendingRefreshes.Remove(slot);
	}

	public void Drop(int slot)
	{
		stickerWear.Remove(slot);
		lifeRevisions.Remove(slot);
		activeRefreshRevisions.Remove(slot);
		refreshing.Remove(slot);
		pendingRefreshes.Remove(slot);
	}

	public void Reset()
	{
		mapGeneration = unchecked(mapGeneration + 1);
		lifeRevisions.Clear();
		activeRefreshRevisions.Clear();
		refreshing.Clear();
		pendingRefreshes.Clear();
		patternSeed = 1;
	}

	public void Dispose()
	{
		Reset();
		stickerWear.Clear();
	}
}
