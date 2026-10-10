using System.Net;
using System.Buffers.Binary;
using System.Text;

namespace WeaponSkins;

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
	private const int MaximumInputLength = 65536;
	private static readonly UTF8Encoding TextEncoding = new(false, true);

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
		if (string.IsNullOrWhiteSpace(input) || input.Length > MaximumInputLength)
			return false;

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
			var parsed = new EconItemPreview();
			if (!ParseBlock(bytes.AsSpan(1, bytes.Length - 5), parsed) ||
				parsed.DefIndex is <= 0 or > ushort.MaxValue ||
				!CosmeticRules.IsValidWear(parsed.PaintWear) || !CosmeticRules.IsValidSeed(parsed.PaintSeed))
				return false;

			item = parsed;
			return true;
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
			var (field, wire) = ReadTag(data, ref pos);
			if (field is 3 or 4 or 5 or 6 or 7 or 8 or 9 or 10 or 17)
				RequireWire(wire, 0);
			else if (field is 11 or 12 or 20)
				RequireWire(wire, 2);

			switch (field)
			{
				case 3:
					item.DefIndex = ReadInt(data, ref pos);
					break;
				case 4:
					item.PaintIndex = ReadInt(data, ref pos);
					break;
				case 5:
					item.Rarity = ReadInt(data, ref pos);
					break;
				case 6:
					item.Quality = ReadInt(data, ref pos);
					break;
				case 7:
					item.PaintWear = BitConverter.UInt32BitsToSingle(ReadUInt(data, ref pos));
					if (!CosmeticRules.IsValidWear(item.PaintWear))
						throw new FormatException();
					break;
				case 8:
					item.PaintSeed = ReadInt(data, ref pos);
					break;
				case 9:
					item.KillEaterType = ReadInt(data, ref pos);
					break;
				case 10:
					item.KillEaterValue = ReadInt(data, ref pos);
					break;
				case 11:
					item.CustomName = ReadString(data, ref pos);
					break;
				case 12:
					item.Stickers.Add(ParseSticker(ReadBytes(data, ref pos)));
					break;
				case 17:
					item.MusicIndex = ReadInt(data, ref pos);
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
			var (field, wire) = ReadTag(data, ref pos);
			if (field is 1 or 2 or 10 or 11 or 12)
				RequireWire(wire, 0);
			else if (field is 3 or 4 or 5 or 7 or 8 or 9)
				RequireWire(wire, 5);

			switch (field)
			{
				case 1:
					sticker.Slot = ReadInt(data, ref pos);
					break;
				case 2:
					sticker.Id = ReadInt(data, ref pos);
					break;
				case 3:
					sticker.Wear = ReadFloat(data, ref pos);
					if (!CosmeticRules.IsValidWear(sticker.Wear))
						throw new FormatException();
					break;
				case 4:
					sticker.Scale = ReadFloat(data, ref pos);
					if (sticker.Scale < 0f)
						throw new FormatException();
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
					sticker.Pattern = ReadInt(data, ref pos);
					break;
				case 11:
					sticker.Highlight = ReadInt(data, ref pos);
					break;
				case 12:
					sticker.Sticker = ReadInt(data, ref pos);
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
		for (var index = 0; index < 10 && pos < data.Length; index++)
		{
			var b = data[pos++];
			if (index == 9 && b > 1)
				throw new FormatException();
			value |= (ulong)(b & 0x7F) << (index * 7);
			if ((b & 0x80) == 0)
				return value;
		}

		throw new FormatException();
	}

	private static (int Field, int Wire) ReadTag(ReadOnlySpan<byte> data, ref int pos)
	{
		var tag = ReadVarint(data, ref pos);
		var field = tag >> 3;
		var wire = (int)(tag & 7);
		if (field == 0 || field > 0x1FFFFFFF || wire is not (0 or 1 or 2 or 5))
			throw new FormatException();
		return ((int)field, wire);
	}

	private static void RequireWire(int actual, int expected)
	{
		if (actual != expected)
			throw new FormatException();
	}

	private static uint ReadUInt(ReadOnlySpan<byte> data, ref int pos)
	{
		var value = ReadVarint(data, ref pos);
		if (value > uint.MaxValue)
			throw new FormatException();
		return (uint)value;
	}

	private static int ReadInt(ReadOnlySpan<byte> data, ref int pos)
	{
		var value = ReadVarint(data, ref pos);
		if (value > int.MaxValue)
			throw new FormatException();
		return (int)value;
	}

	private static float ReadFloat(ReadOnlySpan<byte> data, ref int pos)
	{
		if (data.Length - pos < 4)
			throw new FormatException();
		var value = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(data.Slice(pos, 4)));
		pos += 4;
		if (!float.IsFinite(value))
			throw new FormatException();
		return value;
	}

	private static ReadOnlySpan<byte> ReadBytes(ReadOnlySpan<byte> data, ref int pos)
	{
		var encodedLength = ReadVarint(data, ref pos);
		if (encodedLength > (ulong)(data.Length - pos))
			throw new FormatException();
		var length = (int)encodedLength;
		var span = data.Slice(pos, length);
		pos += length;
		return span;
	}

	private static string ReadString(ReadOnlySpan<byte> data, ref int pos)
	{
		return TextEncoding.GetString(ReadBytes(data, ref pos));
	}

	private static void Skip(ReadOnlySpan<byte> data, ref int pos, int wire)
	{
		switch (wire)
		{
			case 0:
				ReadVarint(data, ref pos);
				break;
			case 1:
				Advance(data, ref pos, 8);
				break;
			case 2:
				ReadBytes(data, ref pos);
				break;
			case 5:
				Advance(data, ref pos, 4);
				break;
			default:
				throw new FormatException();
		}
	}

	private static void Advance(ReadOnlySpan<byte> data, ref int pos, int count)
	{
		if (data.Length - pos < count)
			throw new FormatException();
		pos += count;
	}
}

