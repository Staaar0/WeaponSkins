using System.Text.Json.Serialization;
using CounterStrikeSharp.API.Core;

namespace WeaponSkins;

public class SkinsConfig : BasePluginConfig
{
	[JsonPropertyName("ConfigVersion")]
	public override int Version { get; set; } = 2;

	[JsonPropertyName("api")]
	public ApiConfig Api { get; set; } = new();

	[JsonPropertyName("database")]
	public DatabaseConfig Database { get; set; } = new();

	[JsonPropertyName("link_required")]
	public bool LinkRequired { get; set; } = false;

	[JsonPropertyName("discord_link")]
	public string DiscordLink { get; set; } = "";

	[JsonPropertyName("stickers")]
	public StickersConfig Stickers { get; set; } = new();

	[JsonPropertyName("agents")]
	public AgentsConfig Agents { get; set; } = new();

	[JsonPropertyName("menu")]
	public MenuConfig Menu { get; set; } = new();

	[JsonPropertyName("commands")]
	public CommandsConfig Commands { get; set; } = new();
}

public class ApiConfig
{
	[JsonPropertyName("base_url")]
	public string BaseUrl { get; set; } = "https://cdn.jsdelivr.net/gh/ByMykel/CSGO-API@main/public/api";

	[JsonPropertyName("language")]
	public string Language { get; set; } = "en";

	[JsonPropertyName("timeout_seconds")]
	public int TimeoutSeconds { get; set; } = 15;
}

public class DatabaseConfig
{
	[JsonPropertyName("host")]
	public string Host { get; set; } = "";

	[JsonPropertyName("port")]
	public uint Port { get; set; } = 3306;

	[JsonPropertyName("user")]
	public string User { get; set; } = "";

	[JsonPropertyName("password")]
	public string Password { get; set; } = "";

	[JsonPropertyName("name")]
	public string Name { get; set; } = "";

	[JsonPropertyName("ssl_mode")]
	public string SslMode { get; set; } = "Preferred";
}

public class AgentsConfig
{
	[JsonPropertyName("change_cooldown")]
	public int ChangeCooldown { get; set; } = 5;
}

public class StickersConfig
{
	[JsonPropertyName("enabled")]
	public bool Enabled { get; set; } = true;

	[JsonPropertyName("vip_only")]
	public bool VipOnly { get; set; } = false;

	[JsonPropertyName("vip_flag")]
	public string VipFlag { get; set; } = "@css/vip";
}

public class MenuConfig
{
	[JsonPropertyName("freeze_player")]
	public bool FreezePlayer { get; set; } = false;

	[JsonPropertyName("items_per_page")]
	public int ItemsPerPage { get; set; } = 4;

	[JsonPropertyName("show_image")]
	public bool ShowImage { get; set; } = true;

	[JsonPropertyName("image_seconds")]
	public float ImageSeconds { get; set; } = 2f;
}

public class CommandsConfig
{
	[JsonPropertyName("skins")]
	public List<string> Skins { get; set; } = ["ws", "skins", "skin"];

	[JsonPropertyName("knife")]
	public List<string> Knife { get; set; } = ["knife", "knives"];

	[JsonPropertyName("gloves")]
	public List<string> Gloves { get; set; } = ["gloves", "glove"];

	[JsonPropertyName("agents")]
	public List<string> Agents { get; set; } = ["agents", "agent"];

	[JsonPropertyName("music")]
	public List<string> Music { get; set; } = ["music", "mk"];

	[JsonPropertyName("pins")]
	public List<string> Pins { get; set; } = ["pins", "pin"];

	[JsonPropertyName("stickers")]
	public List<string> Stickers { get; set; } = ["stickers", "sticker"];

	[JsonPropertyName("nametag")]
	public List<string> NameTag { get; set; } = ["nametag", "tag"];

	[JsonPropertyName("stattrak")]
	public List<string> StatTrak { get; set; } = ["stattrak", "st"];

	[JsonPropertyName("wear")]
	public List<string> Wear { get; set; } = ["wear", "float"];

	[JsonPropertyName("seed")]
	public List<string> Seed { get; set; } = ["seed", "pattern"];

	[JsonPropertyName("gen")]
	public List<string> Gen { get; set; } = ["g", "gen"];

	[JsonPropertyName("reload")]
	public List<string> Reload { get; set; } = ["ws_reload"];
}