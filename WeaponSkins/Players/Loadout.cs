using CounterStrikeSharp.API.Modules.Utils;

namespace WeaponSkins;

public sealed class StickerEntry
{
	public int Slot;
	public int Id;
	public float Wear;
	public float Scale = 1f;
	public float Rotation;
	public float OffsetX;
	public float OffsetY;
	public int Schema = -1;
}

public sealed class CharmEntry
{
	public int Id;
	public int Pattern;
	public int Sticker;
	public int Highlight;
	public float OffsetX;
	public float OffsetY;
	public float OffsetZ;
}

public sealed class WeaponEntry
{
	public int Paint;
	public float Wear = 0.000001f;
	public int Seed;
	public string? NameTag;
	public int StatTrak = -1;
	public List<StickerEntry> Stickers = [];
	public CharmEntry? Charm;

	public StickerEntry? StickerAt(int slot) => Stickers.Find(s => s.Slot == slot);
}

public sealed class TeamLoadout
{
	public Dictionary<int, WeaponEntry> Weapons = [];
	public Dictionary<int, Dictionary<int, WeaponEntry>> Skins = [];
	public int Knife;
	public int GloveDef;
	public int GlovePaint;
	public string? AgentModel;

	public WeaponEntry GetOrAddWeapon(int defIndex)
	{
		if (!Weapons.TryGetValue(defIndex, out var entry))
			Weapons[defIndex] = entry = Skin(defIndex, 0);
		return entry;
	}

	public WeaponEntry Skin(int defIndex, int paint)
	{
		if (!Skins.TryGetValue(defIndex, out var byPaint))
			Skins[defIndex] = byPaint = [];
		if (!byPaint.TryGetValue(paint, out var entry))
			byPaint[paint] = entry = new WeaponEntry { Paint = paint };
		return entry;
	}

	public WeaponEntry Gloves => Skin(GloveDef, GlovePaint);

	public WeaponEntry Equip(int defIndex, int paint)
	{
		var entry = Skin(defIndex, paint);
		Weapons[defIndex] = entry;
		return entry;
	}
}

public sealed class PlayerLoadout
{
	public readonly TeamLoadout T = new();
	public readonly TeamLoadout CT = new();
	public volatile bool Ready;
	public int MusicKit;
	public int Pin;

	public TeamLoadout For(CsTeam team) => team == CsTeam.CounterTerrorist ? CT : T;
}

