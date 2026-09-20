namespace WeaponSkins;

public static class LinkPolicy
{
    public static bool TryNormalize(string value, out string method)
    {
        if (value == "2" || string.Equals(value, "Discord-Utilities", StringComparison.OrdinalIgnoreCase))
        {
            method = "Discord-Utilities";
            return true;
        }
        method = "WeaponSkinsBOT";
        return value == "1" || string.Equals(value, method, StringComparison.OrdinalIgnoreCase);
    }

    public static bool RequiresLink(bool discordUtilities, bool configuredRequired, bool databaseConfigured) =>
        discordUtilities || configuredRequired && databaseConfigured;

    public static bool CanIssueCodes(bool discordUtilities, bool required, bool databaseConfigured, bool hasToken) =>
        !discordUtilities && (required || databaseConfigured && hasToken);

    public static bool RunBot(bool discordUtilities, bool databaseConfigured, bool hasToken) =>
        !discordUtilities && databaseConfigured && hasToken;
}
