namespace WeaponSkinsBot.Database;

public enum TeamTarget
{
    Both = 0,
    Terrorist = 2,
    CounterTerrorist = 3
}

public enum ItemKind
{
    Weapon,
    Knife,
    Glove
}

public static class TeamTargetExtensions
{
    public static IReadOnlyList<int> Teams(this TeamTarget target)
    {
        return target switch
        {
            TeamTarget.Terrorist => [2],
            TeamTarget.CounterTerrorist => [3],
            _ => [2, 3]
        };
    }

    public static string Label(this TeamTarget target)
    {
        return target switch
        {
            TeamTarget.Terrorist => "T",
            TeamTarget.CounterTerrorist => "CT",
            _ => "Both"
        };
    }

    public static bool TryParse(string? value, out TeamTarget target)
    {
        target = TeamTarget.Both;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        switch (value.Trim().ToLowerInvariant())
        {
            case "t":
            case "terrorist":
            case "terrorists":
            case "2":
                target = TeamTarget.Terrorist;
                return true;
            case "ct":
            case "counter":
            case "counterterrorist":
            case "counter-terrorist":
            case "3":
                target = TeamTarget.CounterTerrorist;
                return true;
            case "both":
            case "all":
            case "0":
                target = TeamTarget.Both;
                return true;
            default:
                return false;
        }
    }
}
