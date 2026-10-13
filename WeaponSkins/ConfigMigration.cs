using System.Text;
using System.Text.Json;

namespace WeaponSkins;

public static class ConfigMigration
{
	private const string MenuNote = "1=Built-in 2=NickFox-MenuManager 3=RRimmer-PanoramaMenu";

	public static string UpdateJson(string text)
	{
		var root = Parse(text);
		var version = Find(root, "ConfigVersion");
		if (version == null)
			text = InsertFirst(text, root, "\"ConfigVersion\": 6");
		else
		{
			if (version.Value.Kind != JsonTokenType.Number ||
				!int.TryParse(text.AsSpan(version.Value.Start, version.Value.End - version.Value.Start), out var number))
				throw new JsonException("ConfigVersion must be a whole number.");
			if (number < 6)
				text = Replace(text, version.Value.Start, version.Value.End, "6");
		}

		root = Parse(text);
		var menu = Find(root, "menu");
		if (menu == null)
		{
			var indent = ChildIndent(text, root);
			text = InsertFirst(text, root, "\"menu\": " + NewMenu(text, indent));
		}
		else if (menu.Value.Kind == JsonTokenType.Null)
			text = Replace(text, menu.Value.Start, menu.Value.End, NewMenu(text, LineIndent(text, menu.Start)));
		else if (menu.Value.Kind != JsonTokenType.StartObject)
			throw new JsonException("menu must be an object.");

		root = Parse(text);
		var menuObject = Find(root, "menu")!.Value;
		var style = Find(menuObject, "Menu_Style");
		if (style == null)
		{
			text = InsertFirst(text, menuObject, "\"Menu_Style\": 1", MenuNote);
			style = Find(Find(Parse(text), "menu")!.Value, "Menu_Style")!;
		}

		text = AddNote(text, style!, MenuNote, replaceExisting: true);
		root = Parse(text);
		var token = Find(root, "discord_bot_token");
		if (token != null)
			text = AddNote(text, token, "Discord bot will start/stop with cs2 server", replaceExisting: false);
		root = Parse(text);
		var linking = Find(root, "linking_method");
		if (linking != null)
			text = AddNote(text, linking, "link options 1=WeaponSkinsBOT 2=Discord-Utilities", replaceExisting: false);

		Parse(text);
		return text;
	}

	private static string NewMenu(string text, string indent)
	{
		var newline = Newline(text);
		return "{" + newline + indent + IndentUnit(text) + "\"Menu_Style\": 1 // " + MenuNote + newline + indent + "}";
	}

	private static string InsertFirst(string text, ValueSpan value, string property, string? note = null)
	{
		var hasProperties = value.Properties!.Count != 0;
		var position = value.Start + 1;
		var newline = Newline(text);
		var inserted = newline + ChildIndent(text, value) + property + (hasProperties ? "," : "");
		if (note != null)
			inserted += " // " + note;

		var whitespaceEnd = position;
		while (whitespaceEnd < text.Length && char.IsWhiteSpace(text[whitespaceEnd]))
			whitespaceEnd++;
		if (!text.AsSpan(position, whitespaceEnd - position).Contains('\n') &&
			!text.AsSpan(position, whitespaceEnd - position).Contains('\r'))
			inserted += newline + (hasProperties ? ChildIndent(text, value) : LineIndent(text, value.Start));

		return Replace(text, position, position, inserted);
	}

	private static string AddNote(string text, PropertySpan property, string note, bool replaceExisting)
	{
		var position = property.Value.End;
		var cursor = SkipHorizontal(text, position);
		if (cursor < text.Length && text[cursor] == ',')
		{
			position = cursor + 1;
			cursor = SkipHorizontal(text, position);
		}

		if (text.AsSpan(cursor).StartsWith("//", StringComparison.Ordinal))
		{
			if (!replaceExisting)
				return text;
			var end = cursor + 2;
			while (end < text.Length && text[end] is not ('\r' or '\n'))
				end++;
			return Replace(text, cursor, end, "// " + note);
		}

		if (!replaceExisting && text.AsSpan(cursor).StartsWith("/*", StringComparison.Ordinal))
			return text;

		var sameLineContent = cursor < text.Length && text[cursor] is not ('\r' or '\n');
		var inserted = " // " + note + (sameLineContent ? Newline(text) : "");
		return Replace(text, position, sameLineContent ? position : cursor, inserted);
	}

	private static int SkipHorizontal(string text, int position)
	{
		while (position < text.Length && text[position] is ' ' or '\t')
			position++;
		return position;
	}

	private static string ChildIndent(string text, ValueSpan value)
	{
		if (value.Properties!.Count != 0)
		{
			var position = value.Properties[0].Start;
			var start = LineStart(text, position);
			var prefix = text[start..position];
			if (prefix.All(c => c is ' ' or '\t') && prefix.Length != 0)
				return prefix;
		}
		return LineIndent(text, value.Start) + IndentUnit(text);
	}

	private static string IndentUnit(string text)
	{
		for (var position = 0; position < text.Length; position++)
		{
			if (position != 0 && text[position - 1] is not ('\r' or '\n'))
				continue;
			var end = SkipHorizontal(text, position);
			if (end > position && end < text.Length && text[end] == '"')
				return text[position..end];
		}
		return "  ";
	}

	private static string LineIndent(string text, int position)
	{
		var start = LineStart(text, position);
		return text[start..SkipHorizontal(text, start)];
	}

	private static int LineStart(string text, int position)
	{
		while (position > 0 && text[position - 1] is not ('\r' or '\n'))
			position--;
		return position;
	}

	private static string Newline(string text) => text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : text.Contains('\r') && !text.Contains('\n') ? "\r" : "\n";

	private static string Replace(string text, int start, int end, string replacement) => text[..start] + replacement + text[end..];

	private static PropertySpan? Find(ValueSpan value, string name) => value.Properties?.LastOrDefault(property => property.Name == name);

	private static ValueSpan Parse(string text)
	{
		var offset = text.StartsWith('\uFEFF') ? 1 : 0;
		var bytes = Encoding.UTF8.GetBytes(text[offset..]);
		var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip });
		if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
			throw new JsonException("The config must be an object.");
		var root = ReadValue(ref reader, bytes, offset);
		if (reader.Read())
			throw new JsonException("Unexpected content after the config.");
		return root;
	}

	private static ValueSpan ReadValue(ref Utf8JsonReader reader, byte[] bytes, int offset)
	{
		var start = CharacterOffset(bytes, reader.TokenStartIndex, offset);
		var kind = reader.TokenType;
		List<PropertySpan>? properties = null;
		if (kind == JsonTokenType.StartObject)
		{
			properties = [];
			while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
			{
				if (reader.TokenType != JsonTokenType.PropertyName)
					throw new JsonException("Expected a config property.");
				var name = reader.GetString()!;
				var keyStart = CharacterOffset(bytes, reader.TokenStartIndex, offset);
				if (!reader.Read())
					throw new JsonException("Expected a config value.");
				properties.Add(new(name, keyStart, ReadValue(ref reader, bytes, offset)));
			}
			if (reader.TokenType != JsonTokenType.EndObject)
				throw new JsonException("The config object is incomplete.");
		}
		else if (kind == JsonTokenType.StartArray)
			reader.Skip();
		return new(kind, start, CharacterOffset(bytes, reader.BytesConsumed, offset), properties);
	}

	private static int CharacterOffset(byte[] bytes, long position, int offset) => offset + Encoding.UTF8.GetCharCount(bytes.AsSpan(0, checked((int)position)));

	private sealed record ValueSpan(JsonTokenType Kind, int Start, int End, List<PropertySpan>? Properties);
	private sealed record PropertySpan(string Name, int Start, ValueSpan Value);
}
