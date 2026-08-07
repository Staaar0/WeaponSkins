using System.Text.Json;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

namespace WeaponSkins;

public sealed class CatalogService
{
	public bool Loaded { get; private set; }

	public IReadOnlyList<string> Categories { get; private set; } = [];
	public IReadOnlyDictionary<string, List<WeaponDef>> WeaponsByCategory { get; private set; } = new Dictionary<string, List<WeaponDef>>();
	public IReadOnlyDictionary<int, List<PaintDef>> Paints { get; private set; } = new Dictionary<int, List<PaintDef>>();
	public IReadOnlyList<KnifeDef> Knives { get; private set; } = [];
	public IReadOnlyList<GloveDef> Gloves { get; private set; } = [];
	public IReadOnlyList<StickerDef> Stickers { get; private set; } = [];
	public IReadOnlyList<CharmDef> Charms { get; private set; } = [];
	public IReadOnlyList<AgentDef> AgentsT { get; private set; } = [];
	public IReadOnlyList<AgentDef> AgentsCT { get; private set; } = [];
	public IReadOnlyList<string> AgentFactionsT { get; private set; } = [];
	public IReadOnlyList<string> AgentFactionsCT { get; private set; } = [];
	public IReadOnlyDictionary<string, List<AgentDef>> AgentsByFactionT { get; private set; } = new Dictionary<string, List<AgentDef>>();
	public IReadOnlyDictionary<string, List<AgentDef>> AgentsByFactionCT { get; private set; } = new Dictionary<string, List<AgentDef>>();
	public IReadOnlyList<MusicDef> MusicKits { get; private set; } = [];
	public IReadOnlyList<PinDef> Pins { get; private set; } = [];
	public IReadOnlyDictionary<string, List<PinDef>> PinsByGroup { get; private set; } = new Dictionary<string, List<PinDef>>();
	public IReadOnlyList<string> PinGroups { get; private set; } = [];

	public IReadOnlyDictionary<int, string> WeaponNames { get; private set; } = new Dictionary<int, string>();
	public IReadOnlyDictionary<int, string> WeaponClasses { get; private set; } = new Dictionary<int, string>();
	public IReadOnlyDictionary<int, string> StickerNames { get; private set; } = new Dictionary<int, string>();
	public IReadOnlyDictionary<int, string> CharmNames { get; private set; } = new Dictionary<int, string>();



	private readonly ILogger logger;
	private readonly string dataDirectory;
	private int loading;

	public CatalogService(ILogger logger, string dataDirectory)
	{
		this.logger = logger;
		this.dataDirectory = dataDirectory;
	}

	public PaintDef? FindPaint(int defIndex, int paint)
	{
		if (!Paints.TryGetValue(defIndex, out var list))
			return null;
		return list.Find(p => p.Paint == paint);
	}

	public string WeaponName(int defIndex) => WeaponNames.TryGetValue(defIndex, out var name) ? name : $"#{defIndex}";

	public bool LoadBlocking(ApiConfig config, bool useOnline)
	{
		try
		{
			LoadAsync(config, useOnline).GetAwaiter().GetResult();
		}
		catch (Exception ex)
		{
			logger.LogError("Failed to load item data: {Error}", ex.Message);
		}

		return Loaded;
	}

	public async Task LoadAsync(ApiConfig config, bool useOnline)
	{
		if (Interlocked.Exchange(ref loading, 1) == 1)
			return;

		try
		{
			var source = "local files";
			if (useOnline)
			{
				try
				{
					using var api = new ApiClient(config);
					await LoadFrom(api);
					source = "online API";
				}
				catch (Exception ex)
				{
					logger.LogWarning("Online item data failed, using local files: {Error}", ex.Message);
					using var local = new ApiClient(dataDirectory);
					await LoadFrom(local);
				}
			}
			else
			{
				using var local = new ApiClient(dataDirectory);
				await LoadFrom(local);
			}

			Loaded = true;
			logger.LogInformation("Item data loaded from {Source}: {Skins} skins, {Stickers} stickers, {Agents} agents, {Music} music kits, {Pins} pins, {Charms} charms",
				source, Paints.Sum(p => p.Value.Count), Stickers.Count, AgentsT.Count + AgentsCT.Count, MusicKits.Count, Pins.Count, Charms.Count);
		}
		finally
		{
			Interlocked.Exchange(ref loading, 0);
		}
	}

	private async Task LoadFrom(ApiClient api)
	{
		var skinsTask = api.Fetch("skins");
		var stickersTask = api.Fetch("stickers");
		var agentsTask = api.Fetch("agents");
		var musicTask = api.Fetch("music_kits");
		var pinsTask = api.Fetch("collectibles");
		var charmsTask = api.Fetch("keychains");
		Task<JsonDocument>[] tasks = [skinsTask, stickersTask, agentsTask, musicTask, pinsTask, charmsTask];
		var disposeFrom = 0;

		try
		{
			await Task.WhenAll(tasks);
			disposeFrom = 1;
			ParseSkins(skinsTask.Result);
			disposeFrom = 2;
			ParseStickers(stickersTask.Result);
			disposeFrom = 3;
			ParseAgents(agentsTask.Result);
			disposeFrom = 4;
			ParseMusic(musicTask.Result);
			disposeFrom = 5;
			ParsePins(pinsTask.Result);
			disposeFrom = 6;
			ParseCharms(charmsTask.Result);
		}
		catch
		{
			for (var i = disposeFrom; i < tasks.Length; i++)
			{
				var task = tasks[i];
				if (task.Status == TaskStatus.RanToCompletion)
					task.Result.Dispose();
			}
			throw;
		}
	}

	private void ParseSkins(JsonDocument doc)
	{
		using (doc)
		{
			var paints = new Dictionary<int, List<PaintDef>>();
			var seen = new HashSet<(int, int)>();
			var weapons = new Dictionary<int, (string Name, string Category)>();
			var knives = new Dictionary<int, string>();
			var gloves = new Dictionary<int, string>();
			var classes = new Dictionary<int, string>();

			foreach (var skin in doc.RootElement.EnumerateArray())
			{
				if (!skin.TryGetProperty("weapon", out var weapon) || !weapon.TryGetProperty("weapon_id", out var weaponId))
					continue;
				if (!skin.TryGetProperty("paint_index", out var paintProp))
					continue;

				var defIndex = weaponId.GetInt32();
				if (!int.TryParse(paintProp.GetString(), out var paint) || paint <= 0)
					continue;
				if (!seen.Add((defIndex, paint)))
					continue;

				var weaponName = NormalizeMenuName(weapon.GetProperty("name").GetString());
				if (weapon.TryGetProperty("id", out var className) && className.GetString() is { Length: > 0 } entityClass)
					classes[defIndex] = entityClass;
				var category = skin.GetProperty("category").GetProperty("name").GetString() ?? "";
				if (category == "Equipment")
					category = "Other";
				if (defIndex is 9 or 11 or 38 or 40)
					category = "Snipers";

				var paintName = skin.TryGetProperty("pattern", out var pattern) && pattern.ValueKind == JsonValueKind.Object
					? pattern.GetProperty("name").GetString() ?? "" : "";
				if (paintName.Length == 0 && skin.TryGetProperty("name", out var fullName))
					paintName = fullName.GetString() ?? "";
				if (skin.TryGetProperty("phase", out var phase) && phase.ValueKind == JsonValueKind.String)
					paintName = $"{paintName} ({phase.GetString()})";

				var minFloat = skin.TryGetProperty("min_float", out var min) && min.ValueKind == JsonValueKind.Number ? min.GetSingle() : 0f;
				var maxFloat = skin.TryGetProperty("max_float", out var max) && max.ValueKind == JsonValueKind.Number ? max.GetSingle() : 1f;
				var legacy = skin.TryGetProperty("legacy_model", out var leg) && leg.ValueKind == JsonValueKind.True;

				if (!paints.TryGetValue(defIndex, out var list))
					paints[defIndex] = list = [];
				list.Add(new PaintDef(paint, paintName, minFloat, maxFloat, legacy));

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

			var order = new List<string> { "Knives", "Rifles", "Pistols", "SMGs", "Heavy", "Snipers", "Other" };
			var byCategory = new Dictionary<string, List<WeaponDef>>();
			foreach (var (defIndex, info) in weapons)
			{
				if (!byCategory.TryGetValue(info.Category, out var list))
					byCategory[info.Category] = list = [];
				list.Add(new WeaponDef(defIndex, info.Name, info.Category));
			}

			if (knives.Count > 0)
			{
				var knifeList = new List<WeaponDef>(knives.Count);
				foreach (var (defIndex, name) in knives)
					knifeList.Add(new WeaponDef(defIndex, name, "Knives"));
				byCategory["Knives"] = knifeList;
			}

			foreach (var list in byCategory.Values)
				list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));

			var names = new Dictionary<int, string>();
			foreach (var (defIndex, info) in weapons)
				names[defIndex] = info.Name;
			foreach (var (defIndex, name) in knives)
				names[defIndex] = name;
			foreach (var (defIndex, name) in gloves)
				names[defIndex] = name;

			Paints = paints;
			WeaponsByCategory = byCategory;
			Categories = order.Where(byCategory.ContainsKey).Concat(byCategory.Keys.Where(c => !order.Contains(c)).Order()).ToList();
			Knives = knives.Select(k => new KnifeDef(k.Key, k.Value)).OrderBy(k => k.Name).ToList();
			Gloves = gloves.Select(g => new GloveDef(g.Key, g.Value)).OrderBy(g => g.Name).ToList();
			WeaponNames = names;
			WeaponClasses = classes;
		}
	}

	private void ParseStickers(JsonDocument doc)
	{
		using (doc)
		{
			var stickers = new List<StickerDef>();
			var names = new Dictionary<int, string>();

			foreach (var sticker in doc.RootElement.EnumerateArray())
			{
				if (!sticker.TryGetProperty("def_index", out var def) || !int.TryParse(def.GetString(), out var id))
					continue;

				var name = sticker.GetProperty("name").GetString() ?? "";
				if (name.StartsWith("Sticker | ", StringComparison.Ordinal))
					name = name[10..];

				var tournamentEvent = ReadString(sticker, "tournament_event");
				var tournamentPlayer = ReadString(sticker, "tournament_player");
				if (sticker.TryGetProperty("tournament", out var tournament) && tournament.ValueKind == JsonValueKind.Object)
				{
					if (tournamentEvent.Length == 0)
						tournamentEvent = ReadString(tournament, "event");
					if (tournamentPlayer.Length == 0)
						tournamentPlayer = ReadString(tournament, "player");
				}

				if (tournamentPlayer.Length == 0 &&
					sticker.TryGetProperty("player", out var player) && player.ValueKind == JsonValueKind.Object)
					tournamentPlayer = ReadString(player, "name");

				var kind = ReadString(sticker, "type");
				var capsule = ReadString(sticker, "capsule");
				if (capsule.Length == 0 && sticker.TryGetProperty("crates", out var crates) && crates.ValueKind == JsonValueKind.Array)
				{
					foreach (var crate in crates.EnumerateArray())
					{
						if (crate.ValueKind != JsonValueKind.Object)
							continue;
						capsule = ReadString(crate, "name");
						break;
					}
				}

				stickers.Add(new StickerDef(id, name, tournamentEvent, tournamentPlayer, capsule, kind));
				names[id] = name;
			}

			stickers.Sort((a, b) => a.Id.CompareTo(b.Id));
			Stickers = stickers;
			StickerNames = names;
		}
	}

	private void ParseAgents(JsonDocument doc)
	{
		using (doc)
		{
			var t = new List<AgentDef>();
			var ct = new List<AgentDef>();

			foreach (var agent in doc.RootElement.EnumerateArray())
			{
				var model = agent.TryGetProperty("model_player", out var m) ? m.GetString() : null;
				if (string.IsNullOrEmpty(model))
					continue;

				var name = agent.GetProperty("name").GetString() ?? "";
				var faction = "Other";
				var pipe = name.IndexOf(" | ", StringComparison.Ordinal);
				if (pipe > 0)
				{
					faction = NormalizeAgentGroup(name[(pipe + 3)..]);
					name = name[..pipe];
				}

				var team = agent.GetProperty("team").GetProperty("id").GetString();
				if (team == "terrorists")
					t.Add(new AgentDef(model, name, CsTeam.Terrorist, faction));
				else
					ct.Add(new AgentDef(model, name, CsTeam.CounterTerrorist, faction));
			}

			t.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));
			ct.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));
			AgentsT = t;
			AgentsCT = ct;
			(AgentFactionsT, AgentsByFactionT) = GroupAgents(t);
			(AgentFactionsCT, AgentsByFactionCT) = GroupAgents(ct);
		}
	}

	private static (IReadOnlyList<string>, IReadOnlyDictionary<string, List<AgentDef>>) GroupAgents(List<AgentDef> agents)
	{
		var groups = new Dictionary<string, List<AgentDef>>();
		foreach (var agent in agents)
		{
			if (!groups.TryGetValue(agent.Faction, out var list))
				groups[agent.Faction] = list = [];
			list.Add(agent);
		}

		return (groups.Keys.Order().ToList(), groups);
	}

	private void ParseMusic(JsonDocument doc)
	{
		using (doc)
		{
			var kits = new Dictionary<int, string>();

			foreach (var kit in doc.RootElement.EnumerateArray())
			{
				if (!kit.TryGetProperty("def_index", out var def) || !int.TryParse(def.GetString(), out var id))
					continue;

				var name = kit.GetProperty("name").GetString() ?? "";
				if (name.StartsWith("StatTrak", StringComparison.Ordinal) && kits.ContainsKey(id))
					continue;
				if (name.StartsWith("Music Kit | ", StringComparison.Ordinal))
					name = name[12..];
				else if (name.StartsWith("StatTrak\u2122 Music Kit | ", StringComparison.Ordinal))
					name = name[22..];

				kits[id] = name;
			}

			MusicKits = kits.Select(k => new MusicDef(k.Key, k.Value)).OrderBy(k => k.Id).ToList();
		}
	}

	private void ParsePins(JsonDocument doc)
	{
		using (doc)
		{
			var pins = new List<PinDef>();

			foreach (var item in doc.RootElement.EnumerateArray())
			{
				if (!item.TryGetProperty("def_index", out var def) || !int.TryParse(def.GetString(), out var id))
					continue;

				var name = item.GetProperty("name").GetString() ?? "";
				pins.Add(new PinDef(id, name, GetPinMenuGroup(name)));
			}

			pins.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));

			var groups = new Dictionary<string, List<PinDef>>();
			foreach (var pin in pins)
			{
				if (!groups.TryGetValue(pin.Group, out var list))
					groups[pin.Group] = list = [];
				list.Add(pin);
			}

			var groupOrder = new List<string>
			{
				"Major Trophies", "Major Pick'Em / Fantasy", "Major Viewer Passes / Coins",
				"Operations", "Workshop Map Coins", "Pins", "Service Medals", "Premier Medals",
				"Coins", "Other"
			};
			Pins = pins;
			PinsByGroup = groups;
			PinGroups = groupOrder.Where(groups.ContainsKey)
				.Concat(groups.Keys.Where(group => !groupOrder.Contains(group)).Order())
				.ToList();
		}
	}

	private void ParseCharms(JsonDocument doc)
	{
		using (doc)
		{
			var charms = new List<CharmDef>();
			var names = new Dictionary<int, string>();

			foreach (var charm in doc.RootElement.EnumerateArray())
			{
				if (!charm.TryGetProperty("def_index", out var def) || !int.TryParse(def.GetString(), out var id))
					continue;

				var name = charm.GetProperty("name").GetString() ?? "";
				if (name.StartsWith("Charm | ", StringComparison.Ordinal))
					name = name[8..];

				charms.Add(new CharmDef(id, name));
				names[id] = name;
			}

			charms.Sort((a, b) => a.Id.CompareTo(b.Id));
			Charms = charms;
			CharmNames = names;
		}
	}
	private static string ReadString(JsonElement element, string property)
	{
		return element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
			? value.GetString() ?? ""
			: "";
	}

	private static string NormalizeMenuName(string? value)
	{
		return string.IsNullOrWhiteSpace(value) ? "" : value.Replace("★", "", StringComparison.Ordinal).Trim();
	}

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
		if (name.Contains("Pick'Em Trophy", StringComparison.OrdinalIgnoreCase) ||
			name.Contains("Fantasy Trophy", StringComparison.OrdinalIgnoreCase))
			return "Major Pick'Em / Fantasy";

		if (name.StartsWith("Champion at ", StringComparison.OrdinalIgnoreCase) ||
			name.StartsWith("Finalist at ", StringComparison.OrdinalIgnoreCase) ||
			name.StartsWith("Semifinalist at ", StringComparison.OrdinalIgnoreCase) ||
			name.StartsWith("Quarterfinalist at ", StringComparison.OrdinalIgnoreCase))
			return "Major Trophies";

		if (name.Contains("Viewer Pass", StringComparison.OrdinalIgnoreCase) ||
			name.Contains("Souvenir Token", StringComparison.OrdinalIgnoreCase) ||
			name.Contains("Souvenir Package", StringComparison.OrdinalIgnoreCase) ||
			IsMajorEventCoin(name))
			return "Major Viewer Passes / Coins";

		if (name.Contains("Operation ", StringComparison.OrdinalIgnoreCase))
			return "Operations";
		if (name.Contains("Service Medal", StringComparison.OrdinalIgnoreCase))
			return "Service Medals";
		if (name.Contains("Premier Season", StringComparison.OrdinalIgnoreCase))
			return "Premier Medals";
		if (name.EndsWith("Map Coin", StringComparison.OrdinalIgnoreCase))
			return "Workshop Map Coins";
		if (name.EndsWith(" Pin", StringComparison.OrdinalIgnoreCase))
			return "Pins";
		if (name.EndsWith(" Coin", StringComparison.OrdinalIgnoreCase))
			return "Coins";

		return "Other";
	}

	private static bool IsMajorEventCoin(string name)
	{
		if (!name.Contains(" Coin", StringComparison.OrdinalIgnoreCase))
			return false;

		string[] events =
		[
			"Katowice", "Berlin", "Stockholm", "Antwerp", "Rio", "Paris", "Copenhagen",
			"Shanghai", "Austin", "Budapest", "Cologne", "Krakow", "Boston", "London",
			"Columbus", "Cluj-Napoca", "DreamHack", "ELEAGUE", "FACEIT", "BLAST.tv",
			"PGL", "StarLadder", "Perfect World", "EMS One", "ESL One", "MLG"
		];

		return events.Any(eventName => name.Contains(eventName, StringComparison.OrdinalIgnoreCase));
	}

}
