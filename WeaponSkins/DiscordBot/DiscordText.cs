using System.Globalization;

namespace WeaponSkinsBot;

internal static class DiscordText
{
    // Discord counts UTF-16 characters. Keep translated text within its limits.
    internal static string Short(string text, int max)
    {
        if (text.Length <= max) return text;
        var boundaries = StringInfo.ParseCombiningCharacters(text);
        var index = Array.BinarySearch(boundaries, max - 1);
        if (index < 0) index = ~index - 1;
        var length = boundaries[Math.Max(0, index)];
        return text[..length] + "…";
    }
}
