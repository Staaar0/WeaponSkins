using System.Text.Json;
using WeaponSkinsBot.Database;

namespace WeaponSkinsBot.Catalog;

public sealed class CatalogService : IDisposable
{
	private readonly HttpClient http;
	private readonly string baseUrl;
	private readonly string cacheDirectory;
	private readonly string fallbackDirectory;

    public IReadOnlyList<string> Categories { get; private set; } = [];
    public IReadOnlyDictionary<string, List<WeaponDef>> WeaponsByCategory { get; private set; } = new Dictionary<string, List<WeaponDef>>();
    public IReadOnlyDictionary<int, List<PaintDef>> Paints { get; private set; } = new Dictionary<int, List<PaintDef>>();
    public IReadOnlyList<KnifeDef> Knives { get; private set; } = [];
    public IReadOnlyList<GloveDef> Gloves { get; private set; } = [];
    public IReadOnlyList<AgentDef> AgentsT { get; private set; } = [];
    public IReadOnlyList<AgentDef> AgentsCT { get; private set; } = [];
    public IReadOnlyList<MusicDef> MusicKits { get; private set; } = [];
    public IReadOnlyList<PinDef> Pins { get; private set; } = [];
    public IReadOnlyList<StickerDef> Stickers { get; private set; } = [];
    public IReadOnlyList<CharmDef> Charms { get; private set; } = [];
    public IReadOnlyDictionary<int, string> WeaponNames { get; private set; } = new Dictionary<int, string>();
    public IReadOnlyDictionary<int, TeamTarget> WeaponTeams { get; private set; } = new Dictionary<int, TeamTarget>();

    /// <summary>
    /// Which side can actually hold this weapon, straight from the item data.
    /// Both for anything either side can buy.
    /// </summary>
    public TeamTarget TeamOf(int defIndex) => WeaponTeams.TryGetValue(defIndex, out var team) ? team : TeamTarget.Both;

	public CatalogService(string cacheDirectory, string fallbackDirectory, global::WeaponSkins.ApiConfig config)
	{
		this.cacheDirectory = cacheDirectory;
		this.fallbackDirectory = fallbackDirectory;
		baseUrl = $"{config.BaseUrl.TrimEnd('/')}/{(string.IsNullOrWhiteSpace(config.Language) ? "en" : config.Language)}";
		http = new HttpClient { Timeout = TimeSpan.FromSeconds(Math.Max(5, config.TimeoutSeconds)) };
		Directory.CreateDirectory(this.cacheDirectory);
		http.DefaultRequestHeaders.UserAgent.ParseAdd("WeaponSkins_Bot");
	}

	public async Task LoadAsync(CancellationToken cancellationToken)
	{
		using var skins = await Fetch("skins", cancellationToken);
		using var agents = await Fetch("agents", cancellationToken);
		using var music = await Fetch("music_kits", cancellationToken);
		using var pins = await Fetch("collectibles", cancellationToken);
		using var stickers = await Fetch("stickers", cancellationToken);
		using var charms = await Fetch("keychains", cancellationToken);

        ParseSkins(skins.RootElement);
        ParseAgents(agents.RootElement);
        ParseMusic(music.RootElement);
        ParsePins(pins.RootElement);
        ParseStickers(stickers.RootElement);
        ParseCharms(charms.RootElement);
    }

    public PaintDef? FindPaint(int defIndex, int paint)
    {
        return Paints.TryGetValue(defIndex, out var list) ? list.FirstOrDefault(item => item.Paint == paint) : null;
    }

    public WeaponDef? FindWeapon(string value)
    {
        var text = Normalize(value);
        var all = WeaponsByCategory.Values.SelectMany(list => list).ToList();
        var exact = all.FirstOrDefault(item => Normalize(item.Name) == text || item.DefIndex.ToString() == text);
        if (exact != null)
            return exact;
        return all.FirstOrDefault(item => Normalize(item.Name).Contains(text, StringComparison.OrdinalIgnoreCase));
    }

    public KnifeDef? FindKnife(string value)
    {
        var text = Normalize(value);
        var exact = Knives.FirstOrDefault(item => Normalize(item.Name) == text || item.DefIndex.ToString() == text);
        if (exact != null)
            return exact;
        return Knives.FirstOrDefault(item => Normalize(item.Name).Contains(text, StringComparison.OrdinalIgnoreCase));
    }

    public WeaponDef? FindAnyWeapon(string value)
    {
        var regular = FindWeapon(value);
        if (regular != null)
            return regular;

        var text = Normalize(value);
        var exact = WeaponNames.FirstOrDefault(item => Normalize(item.Value) == text || item.Key.ToString() == text);
        if (exact.Key != 0)
            return new WeaponDef(exact.Key, exact.Value, Knives.Any(k => k.DefIndex == exact.Key) ? "Knives" : "Gloves");

        var partial = WeaponNames.FirstOrDefault(item => Normalize(item.Value).Contains(text, StringComparison.OrdinalIgnoreCase));
        return partial.Key == 0 ? null : new WeaponDef(partial.Key, partial.Value, Knives.Any(k => k.DefIndex == partial.Key) ? "Knives" : "Gloves");
    }

    public string WeaponName(int defIndex) => WeaponNames.TryGetValue(defIndex, out var name) ? name : $"#{defIndex}";

	private async Task<JsonDocument> Fetch(string name, CancellationToken cancellationToken)
	{
		var path = Path.Combine(cacheDirectory, $"{name}.json");
		try
		{
			var bytes = await http.GetByteArrayAsync($"{baseUrl}/{name}.json", cancellationToken);
			await File.WriteAllBytesAsync(path, bytes, cancellationToken);
			return JsonDocument.Parse(bytes);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch when (File.Exists(path))
		{
			return JsonDocument.Parse(await File.ReadAllBytesAsync(path, cancellationToken));
		}
		catch when (File.Exists(Path.Combine(fallbackDirectory, $"{name}.json")))
		{
			return JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(fallbackDirectory, $"{name}.json"), cancellationToken));
		}
	}

    private void ParseSkins(JsonElement root)
    {
        var paints = new Dictionary<int, List<PaintDef>>();
        var seen = new HashSet<(int, int)>();
        var weapons = new Dictionary<int, (string Name, string Category)>();
        var knives = new Dictionary<int, string>();
        var gloves = new Dictionary<int, string>();
        var teams = new Dictionary<int, TeamTarget>();

        foreach (var skin in root.EnumerateArray())
        {
            if (!skin.TryGetProperty("weapon", out var weapon) || !weapon.TryGetProperty("weapon_id", out var weaponId))
                continue;
            if (!skin.TryGetProperty("paint_index", out var paintProp))
                continue;

            var defIndex = weaponId.GetInt32();
            if (!int.TryParse(paintProp.GetString(), out var paint) || paint <= 0 || !seen.Add((defIndex, paint)))
                continue;

            var weaponName = NormalizeMenuName(ReadString(weapon, "name"));
            var category = skin.TryGetProperty("category", out var categoryObj) && categoryObj.ValueKind == JsonValueKind.Object
                ? ReadString(categoryObj, "name") : "Other";
            if (category == "Equipment")
                category = "Other";
            if (defIndex is 9 or 11 or 38 or 40)
                category = "Snipers";

            var paintName = "";
            if (skin.TryGetProperty("pattern", out var pattern) && pattern.ValueKind == JsonValueKind.Object)
                paintName = ReadString(pattern, "name");
            if (paintName.Length == 0)
                paintName = ReadString(skin, "name");
            if (skin.TryGetProperty("phase", out var phase) && phase.ValueKind == JsonValueKind.String)
                paintName = $"{paintName} ({phase.GetString()})";

            var minFloat = skin.TryGetProperty("min_float", out var min) && min.ValueKind == JsonValueKind.Number ? min.GetSingle() : 0f;
            var maxFloat = skin.TryGetProperty("max_float", out var max) && max.ValueKind == JsonValueKind.Number ? max.GetSingle() : 1f;
            var image = ReadString(skin, "image");

            // A weapon only one side can buy never needs a team question.
            var side = skin.TryGetProperty("team", out var teamObj) && teamObj.ValueKind == JsonValueKind.Object
                ? ReadString(teamObj, "id") : "";
            var weaponTeam = side switch
            {
                "terrorists" => TeamTarget.Terrorist,
                "counter-terrorists" => TeamTarget.CounterTerrorist,
                _ => TeamTarget.Both
            };
            teams[defIndex] = teams.TryGetValue(defIndex, out var known) && known != weaponTeam
                ? TeamTarget.Both
                : weaponTeam;

            if (!paints.TryGetValue(defIndex, out var list))
                paints[defIndex] = list = [];
            list.Add(new PaintDef(paint, paintName, minFloat, maxFloat, image));

            switch (category)
            {
                case "Knives":
                    knives[defIndex] = weaponName;
                    break;
                case "Gloves":
                    gloves[defIndex] = weaponName;
                    break;
                default:
                    weapons[defIndex] = (weaponName, category);
                    break;
            }
        }

        var byCategory = new Dictionary<string, List<WeaponDef>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (defIndex, info) in weapons)
        {
            if (!byCategory.TryGetValue(info.Category, out var list))
                byCategory[info.Category] = list = [];
            list.Add(new WeaponDef(defIndex, info.Name, info.Category, teams.GetValueOrDefault(defIndex, TeamTarget.Both)));
        }

        foreach (var list in byCategory.Values)
            list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        foreach (var list in paints.Values)
            list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        var order = new[] { "Rifles", "Pistols", "SMGs", "Heavy", "Snipers", "Other" };
        Categories = order.Where(byCategory.ContainsKey).Concat(byCategory.Keys.Where(c => !order.Contains(c)).Order()).ToList();
        WeaponsByCategory = byCategory;
        Paints = paints;
        Knives = knives.Select(item => new KnifeDef(item.Key, item.Value)).OrderBy(item => item.Name).ToList();
        Gloves = gloves.Select(item => new GloveDef(item.Key, item.Value)).OrderBy(item => item.Name).ToList();

        var names = new Dictionary<int, string>();
        foreach (var (defIndex, info) in weapons)
            names[defIndex] = info.Name;
        foreach (var item in Knives)
            names[item.DefIndex] = item.Name;
        foreach (var item in Gloves)
            names[item.DefIndex] = item.Name;
        WeaponNames = names;
        WeaponTeams = teams;
    }

    private void ParseAgents(JsonElement root)
    {
        var t = new List<AgentDef>();
        var ct = new List<AgentDef>();
        foreach (var agent in root.EnumerateArray())
        {
            var model = ReadString(agent, "model_player");
            if (model.Length == 0)
                continue;

            var name = ReadString(agent, "name");
            var faction = "Other";
            var pipe = name.IndexOf(" | ", StringComparison.Ordinal);
            if (pipe > 0)
            {
                faction = NormalizeAgentGroup(name[(pipe + 3)..]);
                name = name[..pipe];
            }

            var team = agent.TryGetProperty("team", out var teamObj) && teamObj.ValueKind == JsonValueKind.Object
                ? ReadString(teamObj, "id") : "";
            var item = new AgentDef(model, name, team == "terrorists" ? 2 : 3, faction, ReadString(agent, "image"));
            if (item.Team == 2)
                t.Add(item);
            else
                ct.Add(item);
        }

        AgentsT = t.OrderBy(item => item.Name).ToList();
        AgentsCT = ct.OrderBy(item => item.Name).ToList();
    }

    private void ParseMusic(JsonElement root)
    {
        var kits = new Dictionary<int, MusicDef>();
        foreach (var kit in root.EnumerateArray())
        {
            if (!kit.TryGetProperty("def_index", out var def) || !int.TryParse(def.GetString(), out var id))
                continue;

            var name = ReadString(kit, "name");
            if (name.StartsWith("StatTrak", StringComparison.Ordinal) && kits.ContainsKey(id))
                continue;
            if (name.StartsWith("Music Kit | ", StringComparison.Ordinal))
                name = name[12..];
            else if (name.StartsWith("StatTrak™ Music Kit | ", StringComparison.Ordinal))
                name = name[22..];

            kits[id] = new MusicDef(id, name, ReadString(kit, "image"));
        }
        MusicKits = kits.Values.OrderBy(item => item.Name).ToList();
    }

    private void ParsePins(JsonElement root)
    {
        var list = new List<PinDef>();
        foreach (var item in root.EnumerateArray())
        {
            if (!item.TryGetProperty("def_index", out var def) || !int.TryParse(def.GetString(), out var id))
                continue;
            var name = ReadString(item, "name");
            list.Add(new PinDef(id, name, GetPinMenuGroup(name), ReadString(item, "image")));
        }
        Pins = list.OrderBy(item => item.Name).ToList();
    }

    private void ParseStickers(JsonElement root)
    {
        var list = new List<StickerDef>();
        foreach (var item in root.EnumerateArray())
        {
            if (!item.TryGetProperty("def_index", out var def) || !int.TryParse(def.GetString(), out var id))
                continue;
            var name = ReadString(item, "name");
            if (name.StartsWith("Sticker | ", StringComparison.Ordinal))
                name = name[10..];
            list.Add(new StickerDef(id, name));
        }
        Stickers = list.OrderBy(item => item.Name).ToList();
    }

    private void ParseCharms(JsonElement root)
    {
        var list = new List<CharmDef>();
        foreach (var item in root.EnumerateArray())
        {
            if (!item.TryGetProperty("def_index", out var def) || !int.TryParse(def.GetString(), out var id))
                continue;
            var name = ReadString(item, "name");
            if (name.StartsWith("Charm | ", StringComparison.Ordinal))
                name = name[8..];
            list.Add(new CharmDef(id, name));
        }
        Charms = list.OrderBy(item => item.Name).ToList();
    }

    private static string ReadString(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? "" : "";
    }

    private static string NormalizeMenuName(string value) => value.Replace("★", "", StringComparison.Ordinal).Trim();
    private static string Normalize(string value) => value.Trim().Replace("★", "", StringComparison.Ordinal).ToLowerInvariant();

    private static string NormalizeAgentGroup(string value)
    {
        return value.Trim() switch
        {
            "FBI" or "FBI HRT" or "FBI Sniper" or "FBI SWAT" => "FBI",
            "NZSAS" or "SAS" => "SAS",
            var group => group
        };
    }

    private static string GetPinMenuGroup(string name)
    {
        if (name.Contains("Pick'Em Trophy", StringComparison.OrdinalIgnoreCase) || name.Contains("Fantasy Trophy", StringComparison.OrdinalIgnoreCase))
            return "Major Pick'Em / Fantasy";
        if (name.StartsWith("Champion at ", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Finalist at ", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("Semifinalist at ", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Quarterfinalist at ", StringComparison.OrdinalIgnoreCase))
            return "Major Trophies";
        if (name.Contains("Viewer Pass", StringComparison.OrdinalIgnoreCase) || name.Contains("Souvenir Token", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Souvenir Package", StringComparison.OrdinalIgnoreCase) || IsMajorEventCoin(name))
            return "Major Viewer Passes / Coins";
        if (name.Contains("Operation ", StringComparison.OrdinalIgnoreCase)) return "Operations";
        if (name.Contains("Service Medal", StringComparison.OrdinalIgnoreCase)) return "Service Medals";
        if (name.Contains("Premier Season", StringComparison.OrdinalIgnoreCase)) return "Premier Medals";
        if (name.EndsWith("Map Coin", StringComparison.OrdinalIgnoreCase)) return "Workshop Map Coins";
        if (name.EndsWith(" Pin", StringComparison.OrdinalIgnoreCase)) return "Pins";
        if (name.EndsWith(" Coin", StringComparison.OrdinalIgnoreCase)) return "Coins";
        return "Other";
    }

    private static bool IsMajorEventCoin(string name)
    {
        if (!name.Contains(" Coin", StringComparison.OrdinalIgnoreCase))
            return false;
        string[] events = ["Katowice", "Berlin", "Stockholm", "Antwerp", "Rio", "Paris", "Copenhagen", "Shanghai", "Austin", "Budapest", "Cologne", "Krakow", "Boston", "London", "Columbus", "Cluj-Napoca", "DreamHack", "ELEAGUE", "FACEIT", "BLAST.tv", "PGL", "StarLadder", "Perfect World", "EMS One", "ESL One", "MLG"];
        return events.Any(eventName => name.Contains(eventName, StringComparison.OrdinalIgnoreCase));
    }

    public void Dispose() => http.Dispose();
}
