using System.Text.Json;

namespace WeaponSkins;

internal static class CatalogNames
{
	public static string ItemName(JsonElement item)
	{
		var name = ReadString(item, "name").Trim();
		var separator = name.IndexOf('|');
		return separator >= 0 && separator < name.Length - 1 ? name[(separator + 1)..].Trim() : name;
	}

	public static string StickerCanonicalCapsule(string crateId, string fallback) => crateId switch
	{
		"crate-4007" => "Sticker Capsule",
		"crate-4012" => "Sticker Capsule 2",
		"crate-4016" => "Community Sticker Capsule 1",
		"crate-4090" => "Enfu Sticker Capsule",
		"crate-4183" => "Pinups Capsule",
		"crate-4184" => "Slid3 Capsule",
		"crate-4185" => "Team Roles Capsule",
		"crate-4282" => "Sugarface Capsule",
		"crate-4283" => "Bestiary Capsule",
		"crate-4404" => "Perfect World Sticker Capsule 1",
		"crate-4405" => "Perfect World Sticker Capsule 2",
		"crate-4470" => "Community Capsule 2018",
		"crate-4547" => "Skill Groups Capsule",
		"crate-4599" => "Feral Predators Capsule",
		"crate-4477" => "Chicken Capsule",
		"crate-4600" => "Shattered Web Sticker Collection",
		"crate-4670" => "CS20 Sticker Capsule",
		"crate-4597" => "Halo Capsule",
		"crate-4694" => "Half-Life: Alyx Sticker Capsule",
		"crate-4616" => "Warhammer 40,000 Sticker Capsule",
		"crate-4719" => "Recoil Sticker Collection",
		"crate-4729" => "Broken Fang Sticker Collection",
		"crate-4746" => "Poorly Drawn Capsule",
		"crate-4779" => "Riptide Surf Shop Sticker Collection",
		"crate-4782" => "2021 Community Sticker Capsule",
		"crate-4789" => "Battlefield 2042 Sticker Capsule",
		"crate-4783" => "Operation Riptide Sticker Collection",
		"crate-4784" => "The Boardroom Sticker Capsule",
		"crate-4847" => "10 Year Birthday Sticker Capsule",
		"crate-4879" => "Espionage Sticker Capsule",
		"crate-4905" => "Ambush Sticker Capsule",
		"crate-7013" => "Warhammer 40,000 Adeptus Astartes Sticker Capsule",
		"crate-7011" => "Warhammer 40,000 Traitor Astartes Sticker Capsule",
		"crate-7015" => "Warhammer 40,000 Imperium Sticker Capsule",
		"crate-7017" => "Warhammer 40,000 Xenos Sticker Capsule",
		"crate-7041" => "Jackass Sticker Capsule",
		_ => fallback
	};

	public static string StickerTournamentKey(JsonElement tournament)
	{
		var id = tournament.TryGetProperty("id", out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : 0;
		return id switch
		{
			1 => "DreamHack 2013",
			3 => "Katowice 2014",
			4 => "Cologne 2014",
			5 => "DreamHack 2014",
			6 => "Katowice 2015",
			7 => "Cologne 2015",
			8 => "Cluj-Napoca 2015",
			9 => "MLG Columbus 2016",
			10 => "Cologne 2016",
			11 => "Atlanta 2017",
			12 => "Krakow 2017",
			13 => "Boston 2018",
			14 => "London 2018",
			15 => "Katowice 2019",
			16 => "Berlin 2019",
			17 => "2020 RMR",
			18 => "Stockholm 2021",
			19 => "Antwerp 2022",
			20 => "Rio 2022",
			21 => "Paris 2023",
			22 => "Copenhagen 2024",
			23 => "Shanghai 2024",
			24 => "Austin 2025",
			25 => "Budapest 2025",
			26 => "Cologne 2026",
			_ => ReadString(tournament, "name")
		};
	}

	public static bool IsStatTrak(JsonElement item) => ReadString(item, "id").EndsWith("_st", StringComparison.Ordinal);

	public static string PinGroup(JsonElement item)
	{
		// The API keeps these type values the same in every language.
		var group = ReadString(item, "type") switch
		{
			"Tournament Finalist Trophy" => "Major Trophies",
			"Old Pick'Em Trophy" or "Fantasy Trophy" => "Major Pick'Em / Fantasy",
			"Pick'Em Coin" or "Tournament Pass" or "Souvenir Token" => "Major Viewer Passes / Coins",
			"Operation Coin" or "Operation Pass" or "Stars for Operation" => "Operations",
			"Map Contributor Coin" => "Workshop Map Coins",
			"Pin" => "Pins",
			"Service Medal" => "Service Medals",
			"Premier Season Coin" => "Premier Medals",
			_ => ""
		};
		if (group.Length > 0)
			return group;

		if (item.TryGetProperty("original", out var original) && original.ValueKind == JsonValueKind.Object)
		{
			var token = ReadString(original, "item_name");
			if (token is "#CSGO_CollectibleCoin_FiveYearService" or "#CSGO_CollectibleCoin_TenYearService" or
				"#CSGO_CollectibleCoin_CsgoTenYearAnniversaryMemorabilia")
				return "Coins";
		}

		return EnglishPinGroup(ReadString(item, "name"));
	}

	public sealed class PinLabels
	{
		private readonly Dictionary<bool, List<(string Prefix, string Suffix)>> affixes = new();

		public PinLabels(JsonElement items)
		{
			var pins = items.EnumerateArray().Where(item => ReadString(item, "type") == "Pin").ToArray();
			var subjects = new Dictionary<string, string>(StringComparer.Ordinal);
			foreach (var pin in pins)
			{
				var marketName = ReadString(pin, "market_hash_name");
				var key = OriginalKey(pin);
				if (key.Length > 0 && marketName.EndsWith(" Pin", StringComparison.Ordinal))
					subjects[key] = marketName[..^4];
			}

			foreach (var group in pins.GroupBy(IsGenuine))
			{
				var names = group.Select(item => ReadString(item, "name").Trim()).Distinct(StringComparer.Ordinal).ToArray();
				if (names.Length < 5)
					continue;

				// Read the shared pin label from this language instead of translating names ourselves.
				var prefix = SharedAffix(names, false);
				var suffix = SharedAffix(names, true);
				var labels = new List<(string Prefix, string Suffix)> { (prefix, suffix) };
				foreach (var pin in group)
				{
					if (!subjects.TryGetValue(OriginalKey(pin), out var subject)) continue;
					var name = ReadString(pin, "name").Trim();
					var index = name.Replace('\u00a0', ' ').IndexOf(subject.Replace('\u00a0', ' '), StringComparison.Ordinal);
					if (index >= 0 && (index == 0 || !char.IsLetterOrDigit(name[index - 1])) &&
						(index + subject.Length == name.Length || !char.IsLetterOrDigit(name[index + subject.Length])))
						labels.Add((name[..index], name[(index + subject.Length)..]));
				}
				affixes[group.Key] = labels.Distinct().OrderByDescending(label => label.Prefix.Length + label.Suffix.Length).ToList();
			}
		}

		public string Name(JsonElement item)
		{
			var name = ReadString(item, "name").Trim();
			if (ReadString(item, "type") != "Pin")
				return name;
			if (affixes.TryGetValue(IsGenuine(item), out var labels))
				foreach (var label in labels)
				{
					if (name.Length <= label.Prefix.Length + label.Suffix.Length ||
						!name.StartsWith(label.Prefix, StringComparison.Ordinal) || !name.EndsWith(label.Suffix, StringComparison.Ordinal)) continue;
					name = name[label.Prefix.Length..(name.Length - label.Suffix.Length)];
					break;
				}
			// Some older API entries still use the English label.
			if (name.StartsWith("Genuine ", StringComparison.Ordinal)) name = name[8..];
			if (name.EndsWith(" Pin", StringComparison.Ordinal)) name = name[..^4];
			return BarePinName(name.Trim(' ', '\t', '\u00a0', '-', '–', '|', ':', '：'));
		}

		private static string BarePinName(string name) => name switch
		{
			// These catalog labels change the subject's grammar or join it to the word for pin.
			"Brigadegeneralsknappenål" or "Brigadegeneralsknappenål (Ægte)" => "Brigadegeneral",
			"Pin del guardián" or "Pin del guardián de aspecto genuino" => "Guardián",
			"Pin del guardián de élite" or "Pin del guardián de élite de aspecto genuino" => "Guardián de élite",
			"Pin del guardián 2" or "Pin del guardián 2 de aspecto genuino" => "Guardián 2",
			"Pin del guardián 3" or "Pin del guardián 3 de aspecto genuino" => "Guardián 3",
			"Terveyspinssi" => "Terveys",
			"Combine-kypäräpinssi" => "Combine-kypärä",
			"Suojelijaeliitin pinssi" or "Aito Suojelijaeliitin pinssi" => "Suojelijaeliitti",
			"Voittopinssi" or "Aito Voittopinssi" => "Voitto",
			"Vartijapinssi" or "Aito Vartijapinssi" => "Vartija",
			"Segerpin" or "Äkta Segerpin" => "Seger",
			"Taktikpin" or "Äkta Taktikpin" => "Taktik",
			"Значок елітного вартового" or "Значок елітного вартового (з першої партії)" => "Елітний вартовий",
			"Χάλκινη καρφίτσα Λάμδα" => "Χάλκινο Λάμδα",
			"Πολιτικής Προστασίας" => "Πολιτική Προστασία",
			"γλυφής κεφαλοκάβουρα" => "Γλυφή κεφαλοκάβουρα",
			"Υγείας" => "Υγεία",
			"Πόλης 17" => "Πόλη 17",
			"κράνος Combine" => "Κράνος Combine",
			"della Protezione civile" => "Protezione civile",
			"della vittoria" => "Vittoria",
			"Ochrony Cywilnej" => "Ochrona Cywilna",
			"pożywienia" => "Pożywienie",
			"Wortygonta" => "Wortygont",
			"piktogramu głowokraba" => "Piktogram głowokraba",
			"zdrowia" => "Zdrowie",
			"lambdy" => "Lambda",
			"miedzianej lambdy" => "Miedziana lambda",
			"hełmu Kombinatu" => "Hełm Kombinatu",
			"elitarnego obrońcy" => "Elitarny obrońca",
			"zwycięstwa" => "Zwycięstwo",
			"obrońcy" => "Obrońca",
			"taktyka" => "Taktyk",
			"obrońcy 2" => "Obrońca 2",
			"obrońcy 3" => "Obrońca 3",
			"Valerii z Phoenix" => "Valeria z Phoenix",
			"wyroku śmierci" => "Wyrok śmierci",
			"łatwizny" => "Łatwizna",
			"asa pik" => "As pik",
			"ryku" => "Ryk",
			"generała brygady" => "Generał brygady",
			_ => name
		};

		private static string SharedAffix(string[] names, bool suffix)
		{
			var counts = new Dictionary<string, int>(StringComparer.Ordinal);
			foreach (var name in names)
				for (var size = 1; size < name.Length; size++)
				{
					var text = suffix ? name[^size..] : name[..size];
					counts[text] = counts.GetValueOrDefault(text) + 1;
				}
			var minimum = Math.Max(5, (int)Math.Ceiling(names.Length * 0.70));
			return counts.Where(pair => pair.Value >= minimum)
				.OrderByDescending(pair => pair.Key.Length).Select(pair => pair.Key).FirstOrDefault() ?? "";
		}

		private static string OriginalKey(JsonElement item) => item.TryGetProperty("original", out var original) && original.ValueKind == JsonValueKind.Object
			? ReadString(original, "item_name") : "";

		private static bool IsGenuine(JsonElement item) => item.TryGetProperty("genuine", out var value) && value.ValueKind == JsonValueKind.True;
	}

	private static string ReadString(JsonElement item, string property) =>
		item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

	private static string EnglishPinGroup(string name)
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
}
