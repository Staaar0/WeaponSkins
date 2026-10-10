namespace WeaponSkins;

// Shared checks for game and Discord inspect imports.
public static class CosmeticRules
{
	public static bool IsValidWear(float wear) => float.IsFinite(wear) && wear is >= 0f and <= 1f;

	public static bool IsValidSeed(int seed) => seed is >= 0 and <= 1000;

	public static bool IsValidSticker(EconSticker sticker) =>
		sticker.Id > 0 && sticker.Slot is >= 0 and <= 31 &&
		IsValidWear(sticker.Wear) && float.IsFinite(sticker.Scale) && sticker.Scale >= 0f &&
		float.IsFinite(sticker.Rotation) && float.IsFinite(sticker.OffsetX) && float.IsFinite(sticker.OffsetY);

	public static bool IsValidSticker(StickerEntry sticker) =>
		sticker.Id > 0 && sticker.Slot is >= 0 and < 6 && sticker.Schema is >= -1 and <= 31 &&
		IsValidWear(sticker.Wear) && float.IsFinite(sticker.Scale) && sticker.Scale >= 0f &&
		float.IsFinite(sticker.Rotation) && float.IsFinite(sticker.OffsetX) && float.IsFinite(sticker.OffsetY);

	public static bool IsValidCharm(EconSticker charm) =>
		charm.Id > 0 && charm.Pattern >= 0 && charm.Sticker >= 0 && charm.Highlight >= 0 &&
		float.IsFinite(charm.OffsetX) && float.IsFinite(charm.OffsetY) && float.IsFinite(charm.OffsetZ);

	public static bool IsValidCharm(CharmEntry charm) =>
		charm.Id > 0 && charm.Pattern >= 0 && charm.Sticker >= 0 && charm.Highlight >= 0 &&
		float.IsFinite(charm.OffsetX) && float.IsFinite(charm.OffsetY) && float.IsFinite(charm.OffsetZ);

	public static List<StickerEntry> MapStickers(List<EconSticker> source)
	{
		const int slots = 6;
		var result = new List<StickerEntry>();
		var used = new bool[slots];
		var nextFree = 0;
		var nextZero = 4;

		foreach (var sticker in source)
		{
			if (!IsValidSticker(sticker) || result.Count >= 5)
				continue;

			var origin = sticker.Slot;
			var slot = origin;
			var schema = 0;

			if (origin >= slots || used[origin])
			{
				schema = origin;

				if (origin == 0)
				{
					while (nextZero < slots && used[nextZero])
						nextZero++;

					slot = nextZero < slots ? nextZero++ : TakeFreeSlot(used, ref nextFree);
				}
				else
				{
					slot = TakeFreeSlot(used, ref nextFree);
				}

				if (slot < 0)
					continue;
			}
			else if (origin >= 4)
			{
				schema = origin;
			}

			used[slot] = true;
			result.Add(new StickerEntry
			{
				Slot = slot,
				Id = sticker.Id,
				Wear = sticker.Wear,
				Scale = sticker.Scale == 0f ? 1f : sticker.Scale,
				Rotation = sticker.Rotation,
				OffsetX = sticker.OffsetX,
				OffsetY = sticker.OffsetY,
				Schema = schema
			});
		}

		return result;
	}

	private static int TakeFreeSlot(bool[] used, ref int cursor)
	{
		while (cursor < used.Length && used[cursor])
			cursor++;
		return cursor < used.Length ? cursor++ : -1;
	}

	public static CharmEntry? MapCharm(List<EconSticker> keychains)
	{
		var charm = keychains.FirstOrDefault(IsValidCharm);
		if (charm == null)
			return null;

		return new CharmEntry
		{
			Id = charm.Id,
			Pattern = charm.Pattern,
			Sticker = charm.Sticker,
			Highlight = charm.Highlight,
			OffsetX = charm.OffsetX,
			OffsetY = charm.OffsetY,
			OffsetZ = charm.OffsetZ
		};
	}
}
