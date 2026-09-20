namespace WeaponSkins;

public static class CommandPolicy
{
	public static string Normalize(string command)
	{
		var normalized = command.Trim();
		while (normalized.StartsWith('!') || normalized.StartsWith('/'))
			normalized = normalized[1..];
		if (normalized.StartsWith("css_", StringComparison.OrdinalIgnoreCase))
			normalized = normalized[4..];
		return normalized;
	}

	public static bool IsVipOnly(IEnumerable<string> aliases, ISet<string> restricted) =>
		aliases.Any(alias => restricted.Contains(Normalize(alias)));
}
