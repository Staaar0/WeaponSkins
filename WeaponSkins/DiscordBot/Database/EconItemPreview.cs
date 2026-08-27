using System.Net;

namespace WeaponSkinsBot.Database;

public sealed class EconSticker
{
	public int Slot;
	public int Id;
	public float Wear;
	public float Scale = 1f;
	public float Rotation;
	public float OffsetX;
	public float OffsetY;
	public float OffsetZ;
	public int Pattern;
	public int Highlight;
	public int Sticker;
}
public sealed class EconItemPreview
{
	private const string MarkerText = "csgo_econ_action_preview";

	public int DefIndex;
	public int PaintIndex;
	public int Rarity;
	public int Quality;
	public int PaintSeed;
	public float PaintWear;
	public int KillEaterType = -1;
	public int KillEaterValue = -1;
	public string? CustomName;
	public int MusicIndex;
	public List<EconSticker> Stickers = [];
	public List<EconSticker> Keychains = [];

	public bool StatTrak => KillEaterValue >= 0;

	public static bool TryParse(string input, out EconItemPreview item)
	{
		item = new EconItemPreview();

		var hex = ExtractHex(input);
		if (hex == null || hex.Length < 12 || hex.Length % 2 != 0)
			return false;

		var bytes = new byte[hex.Length / 2];
		for (var i = 0; i < bytes.Length; i++)
		{
			if (!byte.TryParse(hex.AsSpan(i * 2, 2), System.Globalization.NumberStyles.HexNumber, null, out bytes[i]))
				return false;
		}

		if (bytes[0] != 0x00)
		{
			var key = bytes[0];
			var unmasked = new byte[bytes.Length];
			for (var i = 1; i < bytes.Length; i++)
				unmasked[i] = (byte)(bytes[i] ^ key);

			bytes = unmasked[1] == 0x00 ? unmasked[1..] : unmasked;
		}

		if (bytes.Length < 6 || bytes[0] != 0x00)
			return false;

		try
		{
			return ParseBlock(bytes.AsSpan(1, bytes.Length - 5), item) && item.DefIndex > 0;
		}
		catch
		{
			return false;
		}
	}

	private static string? ExtractHex(string input)
	{
		var text = input.Trim().Trim('"', '\'');
		if (text.Length == 0)
			return null;

		var marker = text.IndexOf(MarkerText, StringComparison.OrdinalIgnoreCase);
		if (marker >= 0)
		{
			text = text[(marker + MarkerText.Length)..];
			text = WebUtility.UrlDecode(text).Trim();
		}

		var start = -1;
		for (var i = 0; i < text.Length; i++)
		{
			if (Uri.IsHexDigit(text[i]))
			{
				if (start < 0)
					start = i;
			}
			else if (start >= 0)
			{
				if (i - start >= 12)
					return text[start..i];
				start = -1;
			}
		}

		return start >= 0 && text.Length - start >= 12 ? text[start..] : null;
	}

	private static bool ParseBlock(ReadOnlySpan<byte> data, EconItemPreview item)
	{
		var pos = 0;
		while (pos < data.Length)
		{
			var tag = ReadVarint(data, ref pos);
			var field = (int)(tag >> 3);
			var wire = (int)(tag & 7);

			switch (field)
			{
				case 3:
					item.DefIndex = (int)ReadVarint(data, ref pos);
					break;
				case 4:
					item.PaintIndex = (int)ReadVarint(data, ref pos);
					break;
				case 5:
					item.Rarity = (int)ReadVarint(data, ref pos);
					break;
				case 6:
					item.Quality = (int)ReadVarint(data, ref pos);
					break;
				case 7:
					item.PaintWear = BitConverter.UInt32BitsToSingle((uint)ReadVarint(data, ref pos));
					break;
				case 8:
					item.PaintSeed = (int)ReadVarint(data, ref pos);
					break;
				case 9:
					item.KillEaterType = (int)ReadVarint(data, ref pos);
					break;
				case 10:
					item.KillEaterValue = (int)ReadVarint(data, ref pos);
					break;
				case 11:
					item.CustomName = ReadString(data, ref pos);
					break;
				case 12:
					item.Stickers.Add(ParseSticker(ReadBytes(data, ref pos)));
					break;
				case 17:
					item.MusicIndex = (int)ReadVarint(data, ref pos);
					break;
				case 20:
					item.Keychains.Add(ParseSticker(ReadBytes(data, ref pos)));
					break;
				default:
					Skip(data, ref pos, wire);
					break;
			}
		}

		return true;
	}

	private static EconSticker ParseSticker(ReadOnlySpan<byte> data)
	{
		var sticker = new EconSticker();
		var pos = 0;
		while (pos < data.Length)
		{
			var tag = ReadVarint(data, ref pos);
			var field = (int)(tag >> 3);
			var wire = (int)(tag & 7);

			switch (field)
			{
				case 1:
					sticker.Slot = (int)ReadVarint(data, ref pos);
					break;
				case 2:
					sticker.Id = (int)ReadVarint(data, ref pos);
					break;
				case 3:
					sticker.Wear = ReadFloat(data, ref pos);
					break;
				case 4:
					sticker.Scale = ReadFloat(data, ref pos);
					break;
				case 5:
					sticker.Rotation = ReadFloat(data, ref pos);
					break;
				case 7:
					sticker.OffsetX = ReadFloat(data, ref pos);
					break;
				case 8:
					sticker.OffsetY = ReadFloat(data, ref pos);
					break;
				case 9:
					sticker.OffsetZ = ReadFloat(data, ref pos);
					break;
				case 10:
					sticker.Pattern = (int)ReadVarint(data, ref pos);
					break;
				case 11:
					sticker.Highlight = (int)ReadVarint(data, ref pos);
					break;
				case 12:
					sticker.Sticker = (int)ReadVarint(data, ref pos);
					break;
				default:
					Skip(data, ref pos, wire);
					break;
			}
		}

		return sticker;
	}

	private static ulong ReadVarint(ReadOnlySpan<byte> data, ref int pos)
	{
		ulong value = 0;
		var shift = 0;
		while (pos < data.Length)
		{
			var b = data[pos++];
			value |= (ulong)(b & 0x7F) << shift;
			if ((b & 0x80) == 0)
				return value;
			shift += 7;
			if (shift > 63)
				break;
		}

		throw new FormatException();
	}

	private static float ReadFloat(ReadOnlySpan<byte> data, ref int pos)
	{
		if (pos + 4 > data.Length)
			throw new FormatException();
		var value = BitConverter.ToSingle(data.Slice(pos, 4));
		pos += 4;
		return value;
	}

	private static ReadOnlySpan<byte> ReadBytes(ReadOnlySpan<byte> data, ref int pos)
	{
		var length = (int)ReadVarint(data, ref pos);
		if (length < 0 || pos + length > data.Length)
			throw new FormatException();
		var span = data.Slice(pos, length);
		pos += length;
		return span;
	}

	private static string ReadString(ReadOnlySpan<byte> data, ref int pos)
	{
		return System.Text.Encoding.UTF8.GetString(ReadBytes(data, ref pos));
	}

	private static void Skip(ReadOnlySpan<byte> data, ref int pos, int wire)
	{
		switch (wire)
		{
			case 0:
				ReadVarint(data, ref pos);
				break;
			case 1:
				pos += 8;
				break;
			case 2:
				ReadBytes(data, ref pos);
				break;
			case 5:
				pos += 4;
				break;
			default:
				throw new FormatException();
		}
	}
}
