using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace WeaponSkins;

public sealed class PluginTranslations
{
	private static readonly Regex Arguments = new(@"\{(\d+)\}", RegexOptions.Compiled);
	private readonly Dictionary<string, string> messages = new(StringComparer.Ordinal);
	public string Language { get; }

	public PluginTranslations(string moduleDirectory, string language, ILogger? logger = null)
	{
		Language = NormalizeLanguage(language);
		Load("en");
		if (!Language.Equals("en", StringComparison.OrdinalIgnoreCase))
			Load(Language);

		void Load(string code)
		{
			var path = new[] { "lang", "Lang" }
				.Select(folder => Path.Combine(moduleDirectory, folder, code + ".json"))
				.FirstOrDefault(File.Exists);
			if (path == null)
			{
				logger?.LogWarning("Translation file {Language}.json is missing; using English where needed", code);
				return;
			}
			try
			{
				var values = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
				if (values != null)
					foreach (var (key, value) in values)
						if (!string.IsNullOrWhiteSpace(value))
							messages[key] = value;
			}
			catch (Exception ex)
			{
				logger?.LogWarning("Could not load translation file {Language}.json: {Error}", code, ex.Message);
			}
		}
	}

	public static string NormalizeLanguage(string? language)
	{
		if (string.IsNullOrWhiteSpace(language))
			return "en";
		var code = language.Trim().Replace('_', '-');
		try { return CultureInfo.GetCultureInfo(code).Name; }
		catch (CultureNotFoundException) { return "en"; }
	}

	public string Text(string key, params object[] args)
	{
		var text = messages.GetValueOrDefault(key, key);
		if (args.Length == 0)
			return text;
		return Arguments.Replace(text, match =>
			int.TryParse(match.Groups[1].Value, out var index) && index < args.Length
				? Convert.ToString(args[index], CultureInfo.InvariantCulture) ?? ""
				: match.Value);
	}
}
