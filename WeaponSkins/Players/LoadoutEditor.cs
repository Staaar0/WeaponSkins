using CounterStrikeSharp.API.Modules.Utils;

namespace WeaponSkins;

// Changes loadout values and requests persistence. The caller retains control
// over messages, menu navigation, and main-thread game refreshes.
public sealed class LoadoutEditor(CatalogService catalog, LoadoutStore store, Action<Task> save)
{
	public void SetPaint(PlayerLoadout loadout, ulong steamId, IEnumerable<CsTeam> teams, int def, PaintDef? paint)
	{
		foreach (var team in teams)
		{
			var entry = loadout.For(team).Equip(def, paint?.Paint ?? 0);
			if (paint != null)
				entry.Wear = Math.Clamp(entry.Wear, MinimumWeaponWear(def, paint), paint.MaxFloat);
			save(store.SaveWeaponAndEquip(steamId, team, def, entry));
		}
	}

	public float SetWear(PlayerLoadout loadout, ulong steamId, IEnumerable<CsTeam> teams, int def, float wear)
	{
		var applied = wear;
		foreach (var team in teams)
		{
			var entry = loadout.For(team).GetOrAddWeapon(def);
			var paint = catalog.FindPaint(def, entry.Paint);
			applied = paint != null
				? Math.Clamp(wear, MinimumWeaponWear(def, paint), paint.MaxFloat)
				: Math.Clamp(wear, KnifeService.IsKnifeDef(def) ? KnifeService.MinimumWear : 0.000001f, 1f);
			entry.Wear = applied;
			save(store.SaveWeapon(steamId, team, def, entry));
		}
		return applied;
	}

	public void SetSeed(PlayerLoadout loadout, ulong steamId, IEnumerable<CsTeam> teams, int def, int seed)
	{
		foreach (var team in teams)
		{
			var entry = loadout.For(team).GetOrAddWeapon(def);
			entry.Seed = seed;
			save(store.SaveWeapon(steamId, team, def, entry));
		}
	}

	public bool ToggleStatTrak(PlayerLoadout loadout, ulong steamId, IEnumerable<CsTeam> teams, int def)
	{
		var enabled = false;
		foreach (var team in teams)
		{
			var entry = loadout.For(team).GetOrAddWeapon(def);
			entry.StatTrak = entry.StatTrak >= 0 ? -1 : 0;
			enabled = entry.StatTrak >= 0;
			save(store.SaveWeapon(steamId, team, def, entry));
		}
		return enabled;
	}

	public void SetNameTag(PlayerLoadout loadout, ulong steamId, IEnumerable<CsTeam> teams, int def, string? tag)
	{
		foreach (var team in teams)
		{
			var entry = loadout.For(team).GetOrAddWeapon(def);
			entry.NameTag = tag;
			save(store.SaveWeapon(steamId, team, def, entry));
		}
	}

	public void SetKnife(PlayerLoadout loadout, ulong steamId, IEnumerable<CsTeam> teams, int def)
	{
		foreach (var team in teams)
		{
			loadout.For(team).Knife = def;
			save(store.SaveKnife(steamId, team, def));
		}
	}

	public void ResetGloves(PlayerLoadout loadout, ulong steamId, IEnumerable<CsTeam> teams)
	{
		foreach (var team in teams)
		{
			var side = loadout.For(team);
			side.GloveDef = 0;
			side.GlovePaint = 0;
			save(store.SaveGloves(steamId, team, side));
		}
	}

	public void SetGlovePaint(PlayerLoadout loadout, ulong steamId, IEnumerable<CsTeam> teams, int def, PaintDef paint)
	{
		foreach (var team in teams)
		{
			var side = loadout.For(team);
			side.GloveDef = def;
			side.GlovePaint = paint.Paint;
			side.Gloves.Wear = Math.Clamp(side.Gloves.Wear, MinWear(paint), paint.MaxFloat);
			save(store.SaveGloves(steamId, team, side));
		}
	}

	public void SetGloveSeed(PlayerLoadout loadout, ulong steamId, IEnumerable<CsTeam> teams, int seed)
	{
		foreach (var team in teams)
		{
			var side = loadout.For(team);
			side.Gloves.Seed = seed;
			save(store.SaveGloves(steamId, team, side));
		}
	}

	public float SetGloveWear(PlayerLoadout loadout, ulong steamId, IEnumerable<CsTeam> teams, float wear)
	{
		var applied = wear;
		foreach (var team in teams)
		{
			var side = loadout.For(team);
			var paint = catalog.FindPaint(side.GloveDef, side.GlovePaint);
			applied = paint != null
				? Math.Clamp(wear, MinWear(paint), paint.MaxFloat)
				: Math.Clamp(wear, 0.000001f, 1f);
			side.Gloves.Wear = applied;
			save(store.SaveGloves(steamId, team, side));
		}
		return applied;
	}

	public void PlaceSticker(PlayerLoadout loadout, ulong steamId, IEnumerable<CsTeam> teams, int def, int slot, StickerDef sticker)
	{
		foreach (var team in teams)
		{
			var entry = loadout.For(team).GetOrAddWeapon(def);
			if (slot < 0)
			{
				entry.Stickers.Clear();
				for (var i = 0; i < 4; i++)
					entry.Stickers.Add(new StickerEntry { Slot = i, Id = sticker.Id });
			}
			else
			{
				entry.Stickers.RemoveAll(s => s.Slot == slot);
				entry.Stickers.Add(new StickerEntry { Slot = slot, Id = sticker.Id });
			}

			DropUnmanagedStickers(entry);
			save(store.SaveWeaponAndStickers(steamId, team, def, entry));
		}
	}

	public void RemoveSticker(PlayerLoadout loadout, ulong steamId, IEnumerable<CsTeam> teams, int def, int slot)
	{
		foreach (var team in teams)
		{
			var entry = loadout.For(team).GetOrAddWeapon(def);
			if (slot < 0)
				entry.Stickers.Clear();
			else
				entry.Stickers.RemoveAll(s => s.Slot == slot);

			DropUnmanagedStickers(entry);
			save(store.SaveStickers(steamId, team, def, entry.Paint, entry.Stickers));
		}
	}

	public void RemoveAllStickers(PlayerLoadout loadout, ulong steamId, IEnumerable<CsTeam> teams, int def)
	{
		foreach (var team in teams)
		{
			var entry = loadout.For(team).GetOrAddWeapon(def);
			entry.Stickers.Clear();
			entry.Charm = null;
			save(store.SaveStickersAndCharm(
				steamId,
				team,
				def,
				entry.Paint,
				entry.Stickers,
				entry.Charm));
		}
	}

	public void SetAgent(PlayerLoadout loadout, ulong steamId, CsTeam team, string? model)
	{
		loadout.For(team).AgentModel = model;
		save(store.SaveAgent(steamId, team, model));
	}

	public void SetMusic(PlayerLoadout loadout, ulong steamId, int value)
	{
		loadout.MusicKit = value;
		save(store.SaveMusic(steamId, value));
	}

	public void SetPin(PlayerLoadout loadout, ulong steamId, int value)
	{
		loadout.Pin = value;
		save(store.SavePin(steamId, value));
	}

	private static float MinWear(PaintDef paint) => Math.Max(paint.MinFloat, 0.000001f);

	private static float MinimumWeaponWear(int def, PaintDef paint)
	{
		var minimum = KnifeService.IsKnifeDef(def) ? KnifeService.MinimumWear : 0.000001f;
		return Math.Min(paint.MaxFloat, Math.Max(paint.MinFloat, minimum));
	}

	private static void DropUnmanagedStickers(WeaponEntry entry)
	{
		entry.Stickers.RemoveAll(s => s.Slot >= 4);
	}
}
