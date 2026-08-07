using CounterStrikeSharp.API.Modules.Utils;

namespace WeaponSkins;

public static class KnifeService
{
	public const float MinimumWear = 0.01f;

	private static readonly IReadOnlyDictionary<int, string> Classes = new Dictionary<int, string>
	{
		[500] = "weapon_bayonet",
		[503] = "weapon_knife_css",
		[505] = "weapon_knife_flip",
		[506] = "weapon_knife_gut",
		[507] = "weapon_knife_karambit",
		[508] = "weapon_knife_m9_bayonet",
		[509] = "weapon_knife_tactical",
		[512] = "weapon_knife_falchion",
		[514] = "weapon_knife_survival_bowie",
		[515] = "weapon_knife_butterfly",
		[516] = "weapon_knife_push",
		[517] = "weapon_knife_cord",
		[518] = "weapon_knife_canis",
		[519] = "weapon_knife_ursus",
		[520] = "weapon_knife_gypsy_jackknife",
		[521] = "weapon_knife_outdoor",
		[522] = "weapon_knife_stiletto",
		[523] = "weapon_knife_widowmaker",
		[525] = "weapon_knife_skeleton",
		[526] = "weapon_knife_kukri"
	};

	public static bool IsKnifeClass(string designerName) =>
		designerName.Contains("knife", StringComparison.Ordinal) || designerName.Contains("bayonet", StringComparison.Ordinal);

	public static bool IsKnifeDef(int defIndex) => Classes.ContainsKey(defIndex) || defIndex is 42 or 59;

	public static int DefaultDef(CsTeam team) => team == CsTeam.Terrorist ? 59 : 42;
}
