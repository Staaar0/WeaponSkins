using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using WeaponSkinsBot.Database;
using WeaponSkins;

namespace WeaponSkinsBot.Catalog;

public sealed class CatalogService : IDisposable
{
	private readonly HttpClient http;
	private readonly string baseUrl;
	private readonly string englishBaseUrl;
	private readonly string language;
	private readonly string cacheDirectory;
	private readonly string fallbackDirectory;

    public IReadOnlyList<string> MenuCategories { get; private set; } = [];
    public IReadOnlyDictionary<int, int> MenuWeaponOrder { get; private set; } = new Dictionary<int, int>();

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

    // Returns the weapon's team, or Both if either team can use it.
    public TeamTarget TeamOf(int defIndex) => WeaponTeams.TryGetValue(defIndex, out var team) ? team : TeamTarget.Both;

	public CatalogService(string cacheDirectory, string fallbackDirectory, global::WeaponSkins.ApiConfig config)
	{
		this.fallbackDirectory = fallbackDirectory;
		language = string.IsNullOrWhiteSpace(config.Language) ? "en" : config.Language;
		var root = config.BaseUrl.TrimEnd('/');
		this.cacheDirectory = Path.Combine(cacheDirectory, CacheKey(root));
		baseUrl = $"{root}/{language}";
		englishBaseUrl = $"{root}/en";
		http = new HttpClient(new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All })
		{
			Timeout = TimeSpan.FromSeconds(Math.Max(5, config.TimeoutSeconds))
		};
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

        LoadWeaponImages();
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

    public IReadOnlyDictionary<int, string> WeaponImages { get; private set; } = new Dictionary<int, string>();

    private void LoadWeaponImages()
    {
        var path = Path.Combine(fallbackDirectory, "base_weapons.json");
        if (!File.Exists(path)) return;
        using var data = JsonDocument.Parse(File.ReadAllBytes(path));
        WeaponImages = data.RootElement.EnumerateArray()
            .Where(x => x.TryGetProperty("def_index", out var id) && id.ValueKind == JsonValueKind.Number)
            .GroupBy(x => x.GetProperty("def_index").GetInt32())
            .ToDictionary(x => x.Key, x => ReadString(x.First(), "image"));
    }

    public static int RarityRank(string color) => color.ToLowerInvariant() switch
    {
        "#e4ae39" => 7, "#eb4b4b" => 6, "#d32ce6" => 5,
        "#8847ff" => 4, "#4b69ff" => 3, "#5e98d9" => 2, _ => 1
    };

    private static string RarityValue(JsonElement item, string property) =>
        item.TryGetProperty("rarity", out var rarity) && rarity.ValueKind == JsonValueKind.Object
            ? ReadString(rarity, property) : "";

    public string WeaponName(int defIndex) => WeaponNames.TryGetValue(defIndex, out var name) ? name : $"#{defIndex}";

	private async Task<JsonDocument> Fetch(string name, CancellationToken cancellationToken)
	{
		var path = Path.Combine(cacheDirectory, CacheKey(language.ToLowerInvariant()), $"{name}.json");
		var englishPath = Path.Combine(cacheDirectory, CacheKey("en"), $"{name}.json");
		var errors = new List<Exception>();
		foreach (var source in language.Equals("en", StringComparison.OrdinalIgnoreCase)
			? new[] { (Url: $"{baseUrl}/{name}.json", Path: path) }
			: new[] { (Url: $"{baseUrl}/{name}.json", Path: path), (Url: $"{englishBaseUrl}/{name}.json", Path: englishPath) })
		{
			JsonDocument? downloaded = null;
			try
			{
				var bytes = await http.GetByteArrayAsync(source.Url, cancellationToken);
				downloaded = ParseCatalog(name, bytes);
				await SaveCacheAsync(source.Path, bytes, cancellationToken);
				return downloaded;
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				downloaded?.Dispose();
				throw;
			}
			catch (Exception ex)
			{
				downloaded?.Dispose();
				errors.Add(ex);
			}
		}

		foreach (var localPath in new[] { path, englishPath, Path.Combine(fallbackDirectory, $"{name}.json") }.Distinct(StringComparer.Ordinal))
		{
			try
			{
				return ParseCatalog(name, await File.ReadAllBytesAsync(localPath, cancellationToken));
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
			catch (Exception ex) { errors.Add(ex); }
		}
		throw new AggregateException($"Unable to load the {name} catalog from the API, cache, or bundled data.", errors);
	}

	private static string CacheKey(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

	private static async Task SaveCacheAsync(string path, byte[] bytes, CancellationToken cancellationToken)
	{
		var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
		try
		{
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			await File.WriteAllBytesAsync(temporaryPath, bytes, cancellationToken);
			cancellationToken.ThrowIfCancellationRequested();
			File.Move(temporaryPath, path, overwrite: true);
		}
		catch (IOException) { /* Keep the downloaded catalog if caching fails. */ }
		catch (UnauthorizedAccessException) { }
		finally
		{
			try { File.Delete(temporaryPath); }
			catch (IOException) { }
			catch (UnauthorizedAccessException) { }
		}
	}

	private static JsonDocument ParseCatalog(string name, byte[] bytes)
	{
		var document = JsonDocument.Parse(bytes);
		try
		{
			if (document.RootElement.ValueKind != JsonValueKind.Array)
				throw new JsonException($"The {name} catalog must be an array.");
			foreach (var item in document.RootElement.EnumerateArray())
			{
				if (item.ValueKind != JsonValueKind.Object)
					throw new JsonException($"The {name} catalog contains a non-object item.");
				if (name == "skins")
				{
					if (!item.TryGetProperty("category", out var category) || category.ValueKind != JsonValueKind.Object ||
						!item.TryGetProperty("weapon", out var weapon) || weapon.ValueKind != JsonValueKind.Object ||
						!weapon.TryGetProperty("weapon_id", out var id) || id.ValueKind != JsonValueKind.Number || !id.TryGetInt32(out _))
						throw new JsonException("A skin is missing its category or numeric weapon ID.");
					ValidateStringField(item, "paint_index");
					ReadWearRange(item);
				}
				else if (name is "music_kits" or "collectibles" or "stickers" or "keychains")
					ValidateStringField(item, "def_index");
			}
			return document;
		}
		catch
		{
			document.Dispose();
			throw;
		}
	}

	private static void ValidateStringField(JsonElement item, string name)
	{
		if (item.TryGetProperty(name, out var value) && value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
			throw new JsonException($"Catalog field {name} must be a string.");
	}

	private static (float Minimum, float Maximum) ReadWearRange(JsonElement skin)
	{
		float Read(string name, float fallback)
		{
			if (!skin.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return fallback;
			if (value.ValueKind != JsonValueKind.Number || !value.TryGetSingle(out var number) || !float.IsFinite(number))
				throw new JsonException($"Catalog field {name} must be a finite number.");
			return number;
		}
		var minimum = Read("min_float", 0f);
		var maximum = Read("max_float", 1f);
		if (minimum < 0f || maximum > 1f || minimum > maximum)
			throw new JsonException("A skin has an invalid wear range.");
		return (minimum, maximum);
	}

    private void ParseSkins(JsonElement root)
    {
        MenuCategories = root.EnumerateArray().Select(x => CanonicalCategory(x.GetProperty("category")) switch {
            "Knives" => "Knifes", "SMGs" => "SMG", "Other" => "Pistols", var name => name
        }).Distinct().ToArray();
        MenuWeaponOrder = root.EnumerateArray().Select(x => x.GetProperty("weapon").GetProperty("weapon_id").GetInt32())
            .Distinct().Select((def, index) => (def, index)).ToDictionary(x => x.def, x => x.index);
        var weaponImages = WeaponImages.ToDictionary();
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
            var vanillaKnife = ReadString(skin, "id").StartsWith("skin-vanilla-weapon_", StringComparison.Ordinal);
            var paint = 0;
            if ((!vanillaKnife && (!int.TryParse(paintProp.GetString(), out paint) || paint <= 0)) || !seen.Add((defIndex, paint)))
                continue;
            if (vanillaKnife || !weaponImages.ContainsKey(defIndex)) weaponImages[defIndex] = ReadString(skin, "image");

            var weaponName = NormalizeMenuName(ReadString(weapon, "name"));
            var category = skin.TryGetProperty("category", out var categoryObj) && categoryObj.ValueKind == JsonValueKind.Object
                ? CanonicalCategory(categoryObj) : "Other";
            if (defIndex is 9 or 11 or 38 or 40)
                category = "Snipers";

            var paintName = "";
            if (skin.TryGetProperty("pattern", out var pattern) && pattern.ValueKind == JsonValueKind.Object)
                paintName = ReadString(pattern, "name");
            if (paintName.Length == 0)
                paintName = ReadString(skin, "name");
            if (skin.TryGetProperty("phase", out var phase) && phase.ValueKind == JsonValueKind.String)
                paintName = $"{paintName} ({phase.GetString()})";

            var (minFloat, maxFloat) = ReadWearRange(skin);
            var image = ReadString(skin, "image");

            // Skip the team choice for weapons available to one team.
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
            var displayName = ReadString(skin, "name") + " [" + paint + "]";
            list.Add(new PaintDef(paint, paintName, minFloat, maxFloat, image,
                displayName, RarityValue(skin, "name"), RarityValue(skin, "color")));

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
        foreach (var def in paints.Keys.ToArray())
            paints[def] = paints[def].OrderByDescending(x => RarityRank(x.RarityColor)).ToList();
        WeaponImages = weaponImages;

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

            var rawName = ReadString(agent, "name");
            var name = rawName;
            var faction = "Other";
            var pipe = name.IndexOf(" | ", StringComparison.Ordinal);
            if (pipe > 0)
            {
                faction = NormalizeAgentGroup(name[(pipe + 3)..]);
                name = name[..pipe];
            }

            var team = agent.TryGetProperty("team", out var teamObj) && teamObj.ValueKind == JsonValueKind.Object
                ? ReadString(teamObj, "id") : "";
            var item = new AgentDef(model, name, team == "terrorists" ? 2 : 3, faction, ReadString(agent, "image"), rawName, RarityValue(agent, "color"));
            if (item.Team == 2)
                t.Add(item);
            else
                ct.Add(item);
        }

        AgentsT = t.OrderByDescending(item => RarityRank(item.RarityColor)).ThenBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
        AgentsCT = ct.OrderByDescending(item => RarityRank(item.RarityColor)).ThenBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private void ParseMusic(JsonElement root)
    {
        var kits = new Dictionary<int, MusicDef>();
        foreach (var kit in root.EnumerateArray())
        {
            if (!kit.TryGetProperty("def_index", out var def) || !int.TryParse(def.GetString(), out var id))
                continue;

            if (CatalogNames.IsStatTrak(kit) && kits.ContainsKey(id))
                continue;
            var name = CatalogNames.ItemName(kit);

            kits[id] = new MusicDef(id, name, ReadString(kit, "image"));
        }
        MusicKits = kits.Values.OrderBy(item => item.Name).ToList();
    }

    private void ParsePins(JsonElement root)
    {
        var list = new List<PinDef>();
        var labels = new CatalogNames.PinLabels(root);
        foreach (var item in root.EnumerateArray())
        {
            if (!item.TryGetProperty("def_index", out var def) || !int.TryParse(def.GetString(), out var id))
                continue;
            list.Add(new PinDef(id, labels.Name(item), CatalogNames.PinGroup(item), ReadString(item, "image")));
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
            var name = CatalogNames.ItemName(item);
            list.Add(new StickerDef(id, name, ReadString(item, "image")));
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
            var name = CatalogNames.ItemName(item);
            list.Add(new CharmDef(id, name));
        }
        Charms = list.OrderBy(item => item.Name).ToList();
    }

	private static string CanonicalCategory(JsonElement category)
	{
		var name = ReadString(category, "name");
		return ReadString(category, "id") switch
		{
			"sfui_invpanel_filter_gloves" => "Gloves",
			"sfui_invpanel_filter_melee" => "Knives",
			"loadoutslot_equipment" => "Other",
			"csgo_inventory_weapon_category_pistols" => "Pistols",
			"csgo_inventory_weapon_category_smgs" => "SMGs",
			"csgo_inventory_weapon_category_rifles" => "Rifles",
			"csgo_inventory_weapon_category_heavy" => "Heavy",
			_ => name == "Equipment" ? "Other" : name
		};
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

    public void Dispose() => http.Dispose();
}
