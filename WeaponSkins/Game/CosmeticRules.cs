namespace WeaponSkins;

// Convert inspect-code decorations for the embedded Discord bot.
public static class CosmeticRules
{
	public static List<StickerEntry> MapStickers(List<EconSticker> source)
	{
		const int slots = 6;
		var result = new List<StickerEntry>();
		var used = new bool[slots];
		var nextFree = 0;
		var nextZero = 4;

		foreach (var sticker in source)
		{
			if (sticker.Id <= 0 || sticker.Slot < 0 || sticker.Slot > 31 || result.Count >= 5)
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
		var charm = keychains.FirstOrDefault(k => k.Id > 0);
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
