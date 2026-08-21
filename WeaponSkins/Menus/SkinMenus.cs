using System.Globalization;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;

namespace WeaponSkins;

public sealed class SkinMenus
{
	private static readonly (string Key, float Value)[] WearPresets =
	[
		("menu.wear_fn", 0.01f),
		("menu.wear_mw", 0.10f),
		("menu.wear_ft", 0.20f),
		("menu.wear_ww", 0.40f),
		("menu.wear_bs", 0.60f)
	];

	private readonly WeaponSkins plugin;
	private readonly Dictionary<int, DateTime> agentChange = [];
	private readonly Dictionary<int, List<MenuItem>> paintItems = [];
	private readonly Dictionary<int, List<MenuItem>> glovePaintItems = [];
	private readonly object stickerCacheSync = new();
	private List<MenuItem>? stickerItems;
	private StickerBrowser? stickerBrowser;
	private Task? stickerPrewarmTask;
	private int stickerCacheGeneration;

	public SkinMenus(WeaponSkins plugin)
	{
		this.plugin = plugin;
	}

	private CatalogService Catalog => plugin.Catalog;
	private PlayerCache Cache => plugin.Cache;
	private LoadoutStore Store => plugin.Store;
	private WeaponApplier Applier => plugin.Applier;
	private MenuRenderer Renderer => plugin.Menu;

	public void Drop(int slot) => agentChange.Remove(slot);

	public void InvalidateCaches()
	{
		paintItems.Clear();
		glovePaintItems.Clear();
		lock (stickerCacheSync)
		{
			stickerCacheGeneration++;
			stickerItems = null;
			stickerBrowser = null;
			stickerPrewarmTask = null;
		}
	}

	public void OpenSkins(CCSPlayerController player) => Renderer.Open(player, SkinsRoot, true);
	public void OpenKnife(CCSPlayerController player) => Renderer.Open(player, KnifeRoot, true);
	public void OpenGloves(CCSPlayerController player) => Renderer.Open(player, GlovesRoot, true);
	public void OpenAgents(CCSPlayerController player) => Renderer.Open(player, AgentsRoot, true);
	public void OpenMusic(CCSPlayerController player) => Renderer.Open(player, MusicRoot, true);
	public void OpenPins(CCSPlayerController player) => Renderer.Open(player, PinsRoot, true);

	public void OpenStickers(CCSPlayerController player, int defIndex, string search)
	{
		Renderer.Open(player, StickerSlots, true);
		var session = Renderer.SessionOf(player);
		if (session == null)
			return;

		session.Ctx.WeaponDef = defIndex;
		session.Ctx.Search = search;
		Renderer.Rebuild(player, session);
	}

	private Menu SkinsRoot(CCSPlayerController player, MenuSession session)
	{
		var items = new List<MenuItem>(Catalog.Categories.Count);
		foreach (var category in Catalog.Categories)
		{
			var name = category;
			items.Add(new MenuItem(name, (p, s) => Renderer.Open(p, (pp, ss) => CategoryMenu(pp, ss, name))));
		}

		return new Menu { Title = plugin.Text("menu.skins"), Items = items };
	}

	private Menu CategoryMenu(CCSPlayerController player, MenuSession session, string category)
	{
		var weapons = Catalog.WeaponsByCategory.GetValueOrDefault(category) ?? [];
		var items = new List<MenuItem>(weapons.Count);
		foreach (var weapon in weapons)
		{
			var def = weapon.DefIndex;
			if (KnifeService.IsKnifeDef(def))
			{
				var knife = new KnifeDef(def, weapon.Name);
				items.Add(new MenuItem(weapon.Name, (p, s) => SetKnife(p, s, knife)));
				continue;
			}

			items.Add(new MenuItem(weapon.Name, (p, s) =>
			{
				s.Ctx.WeaponDef = def;
				Renderer.Open(p, PaintList);
			}));
		}

		return new Menu { Title = category, Items = items };
	}

	private Menu PaintList(CCSPlayerController player, MenuSession session)
	{
		var def = session.Ctx.WeaponDef;
		if (!paintItems.TryGetValue(def, out var items))
		{
			var paints = Catalog.Paints.GetValueOrDefault(def) ?? [];
			items = new List<MenuItem>(paints.Count + 1)
			{
				new(plugin.Text("menu.default"), (p, s) => SetPaint(p, s, null))
			};
			foreach (var paint in paints)
			{
				var value = paint;
				items.Add(new MenuItem($"{paint.Name} ({paint.Paint})", (p, s) => SetPaint(p, s, value)));
			}

			paintItems[def] = items;
		}

		return new Menu { Title = Catalog.WeaponName(def), Items = items };
	}

	private void SetPaint(CCSPlayerController player, MenuSession session, PaintDef? paint)
	{
		if (paint != null)
			Renderer.ShowImage(player, paint.Image);

		var loadout = Cache.Get(player);
		if (loadout == null)
			return;

		var def = session.Ctx.WeaponDef;
		foreach (var team in PlayerCache.TargetTeams(player))
		{
			var entry = loadout.For(team).Equip(def, paint?.Paint ?? 0);
			if (paint != null)
				entry.Wear = Math.Clamp(entry.Wear, MinimumWeaponWear(def, paint), paint.MaxFloat);
			plugin.Save(Store.SaveWeaponAndEquip(player.SteamID, team, def, entry));
		}

		Applier.RefreshOwned(player, def);
		plugin.Reply(player, "skin_set", paint?.Name ?? plugin.Text("menu.default"));

		if (paint != null)
			Renderer.Open(player, Customize);
		else
			Renderer.Rebuild(player, session);
	}

	private Menu Customize(CCSPlayerController player, MenuSession session)
	{
		var def = session.Ctx.WeaponDef;
		var entry = Entry(player, def);
		var items = new List<MenuItem>
		{
			new(plugin.Text("menu.wear"), (p, s) => Renderer.Open(p, WearMenu), entry?.Wear.ToString("0.######", CultureInfo.InvariantCulture)),
			new(plugin.Text("menu.seed"), PromptSeed, entry?.Seed.ToString()),
			new(plugin.Text("menu.stattrak"), (p, s) =>
			{
				ToggleStatTrak(p, s.Ctx.WeaponDef);
				Renderer.Rebuild(p, s);
			}, plugin.Text(entry is { StatTrak: >= 0 } ? "menu.on" : "menu.off")),
			new(plugin.Text("menu.nametag"), (p, s) =>
			{
				if (Entry(p, s.Ctx.WeaponDef)?.NameTag != null)
					Renderer.Open(p, NameTagMenu);
				else
					PromptNameTag(p, s);
			}, entry?.NameTag)
		};

		if (!KnifeService.IsKnifeDef(def) && plugin.Config.Stickers.Enabled)
		{
			items.Add(new MenuItem(plugin.Text("menu.stickers"), (p, s) =>
			{
				if (plugin.StickersAllowed(p))
					Renderer.Open(p, StickerSlots);
				else
					plugin.Reply(p, "vip_only");
			}));
		}

		var title = entry != null ? Catalog.FindPaint(def, entry.Paint)?.Name : null;
		return new Menu { Title = title ?? Catalog.WeaponName(def), Items = items };
	}

	private Menu WearMenu(CCSPlayerController player, MenuSession session)
	{
		return WearMenu((p, s, wear) => SetWear(p, s.Ctx.WeaponDef, wear));
	}

	private Menu WearMenu(Action<CCSPlayerController, MenuSession, float> apply)
	{
		var items = new List<MenuItem>(WearPresets.Length + 1);
		foreach (var (key, value) in WearPresets)
		{
			var wear = value;
			items.Add(new MenuItem(plugin.Text(key), (p, s) =>
			{
				apply(p, s, wear);
				Renderer.Back(p, s);
			}, wear.ToString("0.00", CultureInfo.InvariantCulture)));
		}

		items.Add(new MenuItem(plugin.Text("menu.wear_custom"), (p, s) =>
			Renderer.Prompt(p, s, plugin.Text("prompt.wear"), (pp, ss, text) =>
			{
				if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var wear) && wear is >= 0f and <= 1f)
				{
					apply(pp, ss, wear);
					Renderer.Back(pp, ss);
				}
				else
				{
					plugin.Reply(pp, "wear_invalid");
					Renderer.Rebuild(pp, ss);
				}
			})));

		return new Menu { Title = plugin.Text("menu.wear"), Items = items };
	}

	public void SetWear(CCSPlayerController player, int def, float wear)
	{
		var loadout = Cache.Get(player);
		if (loadout == null || !Skinned(player, def))
			return;

		var applied = wear;
		foreach (var team in PlayerCache.TargetTeams(player))
		{
			var entry = loadout.For(team).GetOrAddWeapon(def);
			var paint = Catalog.FindPaint(def, entry.Paint);
			applied = paint != null
				? Math.Clamp(wear, MinimumWeaponWear(def, paint), paint.MaxFloat)
				: Math.Clamp(wear, KnifeService.IsKnifeDef(def) ? KnifeService.MinimumWear : 0.000001f, 1f);
			entry.Wear = applied;
			plugin.Save(Store.SaveWeapon(player.SteamID, team, def, entry));
		}

		Applier.RefreshOwned(player, def);
		plugin.Reply(player, "wear_set", applied.ToString("0.######", CultureInfo.InvariantCulture));
	}

	private void PromptSeed(CCSPlayerController player, MenuSession session)
	{
		PromptSeed(player, session, (p, s, seed) => SetSeed(p, s.Ctx.WeaponDef, seed));
	}

	private void PromptSeed(CCSPlayerController player, MenuSession session, Action<CCSPlayerController, MenuSession, int> apply)
	{
		Renderer.Prompt(player, session, plugin.Text("prompt.seed"), (p, s, text) =>
		{
			if (int.TryParse(text, out var seed) && seed is >= 0 and <= 1000)
				apply(p, s, seed);
			else
				plugin.Reply(p, "seed_invalid");

			Renderer.Rebuild(p, s);
		});
	}

	public void SetSeed(CCSPlayerController player, int def, int seed)
	{
		var loadout = Cache.Get(player);
		if (loadout == null || !Skinned(player, def))
			return;

		foreach (var team in PlayerCache.TargetTeams(player))
		{
			var entry = loadout.For(team).GetOrAddWeapon(def);
			entry.Seed = seed;
			plugin.Save(Store.SaveWeapon(player.SteamID, team, def, entry));
		}

		Applier.RefreshOwned(player, def);
		plugin.Reply(player, "seed_set", seed);
	}

	public void ToggleStatTrak(CCSPlayerController player, int def)
	{
		var loadout = Cache.Get(player);
		if (loadout == null || !Skinned(player, def))
			return;

		var enabled = false;
		foreach (var team in PlayerCache.TargetTeams(player))
		{
			var entry = loadout.For(team).GetOrAddWeapon(def);
			entry.StatTrak = entry.StatTrak >= 0 ? -1 : 0;
			enabled = entry.StatTrak >= 0;
			plugin.Save(Store.SaveWeapon(player.SteamID, team, def, entry));
		}

		Applier.RefreshOwned(player, def);
		plugin.Reply(player, enabled ? "stattrak_on" : "stattrak_off", Catalog.WeaponName(def));
	}

	private void PromptNameTag(CCSPlayerController player, MenuSession session)
	{
		Renderer.Prompt(player, session, plugin.Text("prompt.nametag"), (p, s, text) =>
		{
			var tag = text.Length > 64 ? text[..64] : text;
			SetNameTag(p, s.Ctx.WeaponDef, tag);
			Renderer.Rebuild(p, s);
		});
	}

	private Menu NameTagMenu(CCSPlayerController player, MenuSession session)
	{
		var entry = Entry(player, session.Ctx.WeaponDef);
		var items = new List<MenuItem>
		{
			new(plugin.Text("menu.change"), PromptNameTag, entry?.NameTag),
			new(plugin.Text("menu.remove_nametag"), (p, s) =>
			{
				SetNameTag(p, s.Ctx.WeaponDef, null);
				Renderer.Back(p, s);
			})
		};

		return new Menu { Title = plugin.Text("menu.nametag"), Items = items };
	}

	public void SetNameTag(CCSPlayerController player, int def, string? tag)
	{
		var loadout = Cache.Get(player);
		if (loadout == null || !Skinned(player, def))
			return;

		foreach (var team in PlayerCache.TargetTeams(player))
		{
			var entry = loadout.For(team).GetOrAddWeapon(def);
			entry.NameTag = tag;
			plugin.Save(Store.SaveWeapon(player.SteamID, team, def, entry));
		}

		Applier.RefreshOwned(player, def);
		if (tag == null)
			plugin.Reply(player, "nametag_cleared");
		else
			plugin.Reply(player, "nametag_set", tag);
	}

	private Menu KnifeRoot(CCSPlayerController player, MenuSession session)
	{
		var items = new List<MenuItem>(Catalog.Knives.Count + 1)
		{
			new(plugin.Text("menu.default"), (p, s) => SetKnife(p, s, null))
		};
		foreach (var knife in Catalog.Knives)
		{
			var value = knife;
			items.Add(new MenuItem(knife.Name, (p, s) => SetKnife(p, s, value)));
		}

		return new Menu { Title = plugin.Text("menu.knife"), Items = items };
	}

	private void SetKnife(CCSPlayerController player, MenuSession session, KnifeDef? knife)
	{
		var loadout = Cache.Get(player);
		if (loadout == null)
			return;

		var def = knife?.DefIndex ?? 0;
		foreach (var team in PlayerCache.TargetTeams(player))
		{
			loadout.For(team).Knife = def;
			plugin.Save(Store.SaveKnife(player.SteamID, team, def));
		}

		if (def > 0)
			Applier.RefreshOwned(player, def);
		else
			Applier.RefreshKnife(player);

		plugin.Reply(player, "knife_set", knife?.Name ?? plugin.Text("menu.default"));

		if (knife != null)
		{
			session.Ctx.WeaponDef = def;
			Renderer.Open(player, PaintList);
		}
		else
		{
			Renderer.Rebuild(player, session);
		}
	}

	private Menu GlovesRoot(CCSPlayerController player, MenuSession session)
	{
		var items = new List<MenuItem>(Catalog.Gloves.Count + 1)
		{
			new(plugin.Text("menu.default"), ResetGloves)
		};
		foreach (var glove in Catalog.Gloves)
		{
			var def = glove.DefIndex;
			items.Add(new MenuItem(glove.Name, (p, s) =>
			{
				s.Ctx.GloveDef = def;
				Renderer.Open(p, GlovePaintList);
			}));
		}

		return new Menu { Title = plugin.Text("menu.gloves"), Items = items };
	}

	private void ResetGloves(CCSPlayerController player, MenuSession session)
	{
		var loadout = Cache.Get(player);
		if (loadout == null)
			return;

		foreach (var team in PlayerCache.TargetTeams(player))
		{
			var side = loadout.For(team);
			side.GloveDef = 0;
			side.GlovePaint = 0;
			plugin.Save(Store.SaveGloves(player.SteamID, team, side));
		}

		plugin.GloveApply.Restore(player);
		plugin.Reply(player, "gloves_set", plugin.Text("menu.default"));
		Renderer.Rebuild(player, session);
	}

	private Menu GlovePaintList(CCSPlayerController player, MenuSession session)
	{
		var def = session.Ctx.GloveDef;
		if (!glovePaintItems.TryGetValue(def, out var items))
		{
			var paints = Catalog.Paints.GetValueOrDefault(def) ?? [];
			items = new List<MenuItem>(paints.Count);
			foreach (var paint in paints)
			{
				var value = paint;
				items.Add(new MenuItem(paint.Name, (p, s) => SetGlovePaint(p, s, value)));
			}

			glovePaintItems[def] = items;
		}

		return new Menu { Title = Catalog.WeaponName(def), Items = items };
	}

	private void SetGlovePaint(CCSPlayerController player, MenuSession session, PaintDef paint)
	{
		Renderer.ShowImage(player, paint.Image);

		var loadout = Cache.Get(player);
		if (loadout == null)
			return;

		var def = session.Ctx.GloveDef;
		foreach (var team in PlayerCache.TargetTeams(player))
		{
			var side = loadout.For(team);
			side.GloveDef = def;
			side.GlovePaint = paint.Paint;
			side.Gloves.Wear = Math.Clamp(side.Gloves.Wear, MinWear(paint), paint.MaxFloat);
			plugin.Save(Store.SaveGloves(player.SteamID, team, side));
		}

		plugin.GloveApply.Apply(player, true);
		plugin.Reply(player, "gloves_set", paint.Name);
		Renderer.Open(player, GloveCustomize);
	}

	private Menu GloveCustomize(CCSPlayerController player, MenuSession session)
	{
		var side = Side(player);
		var items = new List<MenuItem>
		{
			new(plugin.Text("menu.wear"), (p, s) => Renderer.Open(p, GloveWearMenu), side?.Gloves.Wear.ToString("0.######", CultureInfo.InvariantCulture)),
			new(plugin.Text("menu.seed"), PromptGloveSeed, side?.Gloves.Seed.ToString())
		};

		var title = side != null ? Catalog.FindPaint(side.GloveDef, side.GlovePaint)?.Name : null;
		return new Menu { Title = title ?? plugin.Text("menu.gloves"), Items = items };
	}

	private Menu GloveWearMenu(CCSPlayerController player, MenuSession session)
	{
		return WearMenu((p, s, wear) => SetGloveWear(p, wear));
	}

	public bool HasGloves(CCSPlayerController player)
	{
		var side = Side(player);
		return side is { GloveDef: > 0, GlovePaint: > 0 };
	}

	public void OpenWear(CCSPlayerController player, int def, float? wear)
	{
		if (KnifeService.IsKnifeDef(def) && HasGloves(player))
		{
			OpenTarget(player, () => ApplyWear(player, def, wear), () => ApplyGloveWear(player, wear));
			return;
		}

		ApplyWear(player, def, wear);
	}

	public void OpenSeed(CCSPlayerController player, int def, int? seed)
	{
		if (KnifeService.IsKnifeDef(def) && HasGloves(player))
		{
			OpenTarget(player, () => ApplySeed(player, def, seed), () => ApplyGloveSeed(player, seed));
			return;
		}

		ApplySeed(player, def, seed);
	}

	private void OpenTarget(CCSPlayerController player, Action onKnife, Action onGloves)
	{
		Renderer.Open(player, (p, s) => new Menu
		{
			Title = plugin.Text("menu.apply_to"),
			Items =
			[
				new MenuItem(plugin.Text("menu.knife"), (pp, ss) => onKnife()),
				new MenuItem(plugin.Text("menu.gloves"), (pp, ss) => onGloves())
			]
		});
	}

	private void ApplyWear(CCSPlayerController player, int def, float? wear)
	{
		if (wear.HasValue)
		{
			Renderer.Close(player);
			SetWear(player, def, wear.Value);
			return;
		}

		OpenFor(player, def, WearMenu);
	}

	private void ApplySeed(CCSPlayerController player, int def, int? seed)
	{
		if (seed.HasValue)
		{
			Renderer.Close(player);
			SetSeed(player, def, seed.Value);
			return;
		}

		OpenFor(player, def, SeedMenu);
	}

	private void ApplyGloveWear(CCSPlayerController player, float? wear)
	{
		if (wear.HasValue)
		{
			Renderer.Close(player);
			SetGloveWear(player, wear.Value);
			return;
		}

		Renderer.Open(player, GloveWearMenu, true);
	}

	private void ApplyGloveSeed(CCSPlayerController player, int? seed)
	{
		if (seed.HasValue)
		{
			Renderer.Close(player);
			SetGloveSeed(player, seed.Value);
			return;
		}

		Renderer.Open(player, GloveSeedMenu, true);
	}

	private void OpenFor(CCSPlayerController player, int def, Func<CCSPlayerController, MenuSession, Menu> factory)
	{
		Renderer.Open(player, factory, true);
		var session = Renderer.SessionOf(player);
		if (session == null)
			return;

		session.Ctx.WeaponDef = def;
		Renderer.Rebuild(player, session);
	}

	private Menu SeedMenu(CCSPlayerController player, MenuSession session)
	{
		var entry = Entry(player, session.Ctx.WeaponDef);
		var items = new List<MenuItem>
		{
			new(plugin.Text("menu.seed"), PromptSeed, entry?.Seed.ToString())
		};

		return new Menu { Title = Catalog.WeaponName(session.Ctx.WeaponDef), Items = items };
	}

	private Menu GloveSeedMenu(CCSPlayerController player, MenuSession session)
	{
		var side = Side(player);
		var items = new List<MenuItem>
		{
			new(plugin.Text("menu.seed"), PromptGloveSeed, side?.Gloves.Seed.ToString())
		};

		return new Menu { Title = plugin.Text("menu.gloves"), Items = items };
	}

	private void SetGloveSeed(CCSPlayerController player, int seed)
	{
		var loadout = Cache.Get(player);
		if (loadout == null)
			return;

		foreach (var team in PlayerCache.TargetTeams(player))
		{
			var side = loadout.For(team);
			side.Gloves.Seed = seed;
			plugin.Save(Store.SaveGloves(player.SteamID, team, side));
		}

		plugin.GloveApply.Apply(player, true);
		plugin.Reply(player, "seed_set", seed);
	}

	private void SetGloveWear(CCSPlayerController player, float wear)
	{
		var loadout = Cache.Get(player);
		if (loadout == null)
			return;

		var applied = wear;
		foreach (var team in PlayerCache.TargetTeams(player))
		{
			var side = loadout.For(team);
			var paint = Catalog.FindPaint(side.GloveDef, side.GlovePaint);
			applied = paint != null
				? Math.Clamp(wear, MinWear(paint), paint.MaxFloat)
				: Math.Clamp(wear, 0.000001f, 1f);
			side.Gloves.Wear = applied;
			plugin.Save(Store.SaveGloves(player.SteamID, team, side));
		}

		plugin.GloveApply.Apply(player, true);
		plugin.Reply(player, "wear_set", applied.ToString("0.######", CultureInfo.InvariantCulture));
	}

	private void PromptGloveSeed(CCSPlayerController player, MenuSession session)
	{
		PromptSeed(player, session, (p, s, seed) => SetGloveSeed(p, seed));
	}

	private Menu AgentsRoot(CCSPlayerController player, MenuSession session)
	{
		if (player.Team is CsTeam.CounterTerrorist or CsTeam.Terrorist)
			return AgentList(player, session, player.Team);

		var items = new List<MenuItem>
		{
			new(plugin.Text("menu.agents_ct"), (p, s) => Renderer.Open(p, (pp, ss) => AgentList(pp, ss, CsTeam.CounterTerrorist))),
			new(plugin.Text("menu.agents_t"), (p, s) => Renderer.Open(p, (pp, ss) => AgentList(pp, ss, CsTeam.Terrorist)))
		};

		return new Menu { Title = plugin.Text("menu.agents"), Items = items };
	}

	private Menu AgentList(CCSPlayerController player, MenuSession session, CsTeam team)
	{
		var factions = team == CsTeam.CounterTerrorist ? Catalog.AgentFactionsCT : Catalog.AgentFactionsT;
		var items = new List<MenuItem>(factions.Count + 1)
		{
			new(plugin.Text("menu.default"), (p, s) => SetAgent(p, s, team, null))
		};
		foreach (var faction in factions)
		{
			var name = faction;
			items.Add(new MenuItem(name, (p, s) => Renderer.Open(p, (pp, ss) => AgentFactionList(pp, ss, team, name))));
		}

		return new Menu { Title = plugin.Text("menu.agents"), Items = items };
	}

	private Menu AgentFactionList(CCSPlayerController player, MenuSession session, CsTeam team, string faction)
	{
		var groups = team == CsTeam.CounterTerrorist ? Catalog.AgentsByFactionCT : Catalog.AgentsByFactionT;
		var agents = groups.GetValueOrDefault(faction) ?? [];
		var items = new List<MenuItem>(agents.Count);
		foreach (var agent in agents)
		{
			var value = agent;
			items.Add(new MenuItem(agent.Name, (p, s) => SetAgent(p, s, team, value)));
		}

		return new Menu { Title = faction, Items = items };
	}

	private void SetAgent(CCSPlayerController player, MenuSession session, CsTeam team, AgentDef? agent)
	{
		var loadout = Cache.Get(player);
		if (loadout == null)
			return;

		var cooldown = plugin.Config.Agents.ChangeCooldown;
		if (cooldown > 0 && agentChange.TryGetValue(player.Slot, out var last))
		{
			var left = cooldown - (int)(DateTime.UtcNow - last).TotalSeconds;
			if (left > 0)
			{
				plugin.Reply(player, "agent_cooldown", left);
				return;
			}
		}

		agentChange[player.Slot] = DateTime.UtcNow;
		if (agent != null)
			Renderer.ShowImage(player, agent.Image);

		loadout.For(team).AgentModel = agent?.Model;
		plugin.Save(Store.SaveAgent(player.SteamID, team, agent?.Model));

		if (player.Team == team)
			plugin.Profile.ApplyAgent(player);

		plugin.Reply(player, "agent_set", agent?.Name ?? plugin.Text("menu.default"));
		Renderer.Rebuild(player, session);
	}

	private Menu MusicRoot(CCSPlayerController player, MenuSession session)
	{
		var items = new List<MenuItem>(Catalog.MusicKits.Count + 1)
		{
			new(plugin.Text("menu.default"), (p, s) => SetMusic(p, s, null))
		};
		foreach (var kit in Catalog.MusicKits)
		{
			var value = kit;
			items.Add(new MenuItem(kit.Name, (p, s) => SetMusic(p, s, value)));
		}

		return new Menu { Title = plugin.Text("menu.music"), Items = items };
	}

	private void SetMusic(CCSPlayerController player, MenuSession session, MusicDef? kit)
	{
		if (kit != null)
			Renderer.ShowImage(player, kit.Image);

		var loadout = Cache.Get(player);
		if (loadout == null)
			return;

		loadout.MusicKit = kit?.Id ?? 0;
		plugin.Save(Store.SaveMusic(player.SteamID, loadout.MusicKit));

		plugin.Profile.Schedule(player);
		plugin.Reply(player, "music_set", kit?.Name ?? plugin.Text("menu.default"));
		Renderer.Rebuild(player, session);
	}

	private Menu PinsRoot(CCSPlayerController player, MenuSession session)
	{
		var items = new List<MenuItem>(Catalog.PinGroups.Count + 1)
		{
			new(plugin.Text("menu.default"), (p, s) => SetPin(p, s, null))
		};
		foreach (var group in Catalog.PinGroups)
		{
			var name = group;
			items.Add(new MenuItem(name, (p, s) => Renderer.Open(p, (pp, ss) => PinGroupList(pp, ss, name))));
		}

		return new Menu { Title = plugin.Text("menu.pins"), Items = items };
	}

	private Menu PinGroupList(CCSPlayerController player, MenuSession session, string group)
	{
		var pins = Catalog.PinsByGroup.GetValueOrDefault(group) ?? [];
		var items = new List<MenuItem>(pins.Count);
		foreach (var pin in pins)
		{
			var value = pin;
			items.Add(new MenuItem(pin.Name, (p, s) => SetPin(p, s, value)));
		}

		return new Menu { Title = group, Items = items };
	}

	private void SetPin(CCSPlayerController player, MenuSession session, PinDef? pin)
	{
		if (pin != null)
			Renderer.ShowImage(player, pin.Image);

		var loadout = Cache.Get(player);
		if (loadout == null)
			return;

		loadout.Pin = pin?.Id ?? 0;
		plugin.Save(Store.SavePin(player.SteamID, loadout.Pin));

		plugin.Profile.Schedule(player);
		plugin.Reply(player, "pin_set", pin?.Name ?? plugin.Text("menu.default"));
		Renderer.Rebuild(player, session);
	}

	private const int MenuStickerSlots = 4;

	private static void DropUnmanagedStickers(WeaponEntry entry)
	{
		entry.Stickers.RemoveAll(s => s.Slot >= MenuStickerSlots);
	}

	private Menu StickerSlots(CCSPlayerController player, MenuSession session)
	{
		var def = session.Ctx.WeaponDef;
		var entry = Entry(player, def);
		var items = new List<MenuItem>(7)
		{
			new(plugin.Text("menu.search"), PromptStickerSearch, session.Ctx.Search.Length > 0 ? session.Ctx.Search : null)
		};

		for (var slot = 0; slot < 4; slot++)
		{
			var index = slot;
			var current = entry?.StickerAt(slot);
			var name = current != null ? Catalog.StickerNames.GetValueOrDefault(current.Id) : null;
			items.Add(new MenuItem(plugin.Text("menu.sticker_slot", slot + 1), (p, s) =>
			{
				s.Ctx.StickerSlot = index;
				OpenStickerPicker(p, s);
			}, name));
		}

		items.Add(new MenuItem(plugin.Text("menu.set_all"), (p, s) =>
		{
			s.Ctx.StickerSlot = -1;
			OpenStickerPicker(p, s);
		}));
		items.Add(new MenuItem(plugin.Text("menu.remove"), (p, s) => Renderer.Open(p, StickerRemoveMenu)));

		return new Menu { Title = plugin.Text("menu.stickers"), Items = items };
	}

	private Menu StickerRemoveMenu(CCSPlayerController player, MenuSession session)
	{
		var entry = Entry(player, session.Ctx.WeaponDef);
		var items = new List<MenuItem>(5);

		for (var slot = 0; slot < 4; slot++)
		{
			var index = slot;
			var current = entry?.StickerAt(slot);
			var name = current != null ? Catalog.StickerNames.GetValueOrDefault(current.Id) : null;
			items.Add(new MenuItem(plugin.Text("menu.sticker_slot", slot + 1), (p, s) =>
			{
				s.Ctx.StickerSlot = index;
				RemoveSticker(p, s);
			}, name));
		}

		items.Add(new MenuItem(plugin.Text("menu.remove_all"), RemoveAllStickers));

		return new Menu { Title = plugin.Text("menu.stickers"), Items = items };
	}

	private void OpenStickerPicker(CCSPlayerController player, MenuSession session)
	{
		if (!StickerCacheReady())
		{
			Prewarm();
			plugin.Reply(player, "stickers_loading");
			return;
		}

		if (session.Ctx.Search.Length > 0)
			Renderer.Open(player, FilteredStickerList);
		else
			Renderer.Open(player, StickerSourceMenu);
	}

	public void Prewarm()
	{
		if (!Catalog.Loaded)
			return;

		if (!plugin.Config.Stickers.Enabled)
			return;

		lock (stickerCacheSync)
		{
			if (stickerItems != null && stickerBrowser != null)
				return;
			if (stickerPrewarmTask is { IsCompleted: false })
				return;

			var generation = stickerCacheGeneration;
			var stickers = Catalog.Stickers.ToArray();
			stickerPrewarmTask = Task.Run(() =>
			{
				var items = BuildStickerItems(stickers);
				var browser = BuildStickerBrowser(stickers);
				lock (stickerCacheSync)
				{
					if (stickerCacheGeneration != generation)
						return;

					stickerItems = items;
					stickerBrowser = browser;
					stickerPrewarmTask = null;
				}
			});
		}
	}

	private bool StickerCacheReady()
	{
		lock (stickerCacheSync)
			return stickerItems != null && stickerBrowser != null;
	}

	private List<MenuItem>? StickerItems()
	{
		lock (stickerCacheSync)
			return stickerItems;
	}

	private StickerBrowser? Browser()
	{
		lock (stickerCacheSync)
			return stickerBrowser;
	}

	private List<MenuItem> BuildStickerItems(IReadOnlyList<StickerDef> stickers)
	{
		var items = new List<MenuItem>(stickers.Count);
		foreach (var sticker in stickers)
		{
			var value = sticker;
			items.Add(new MenuItem(sticker.Name, (p, s) => PlaceSticker(p, s, value)));
		}

		return items;
	}

	private StickerBrowser BuildStickerBrowser(IReadOnlyList<StickerDef> stickers)
	{
		var browser = new StickerBrowser();
		var bySource = new Dictionary<string, List<StickerMenuEntry>>();

		foreach (var sticker in stickers)
		{
			var source = GetStickerSource(sticker);
			var entry = new StickerMenuEntry(
				sticker,
				source,
				GetStickerGroup(sticker, source),
				GetStickerType(sticker.Name),
				IsPlayerSticker(sticker));

			if (!bySource.TryGetValue(source, out var entries))
				bySource[source] = entries = [];
			entries.Add(entry);
		}

		foreach (var source in StickerSources)
		{
			if (!bySource.TryGetValue(source, out var entries))
				continue;

			browser.Sources.Add(source);
			entries.Sort((a, b) => string.Compare(a.Sticker.Name, b.Sticker.Name, StringComparison.OrdinalIgnoreCase));

			var byGroup = new Dictionary<string, List<StickerMenuEntry>>();
			foreach (var entry in entries)
			{
				if (!byGroup.TryGetValue(entry.Group, out var list))
					byGroup[entry.Group] = list = [];
				list.Add(entry);
			}

			browser.Groups[source] = byGroup.Keys
				.Where(group => group.Length > 0)
				.OrderBy(group => StickerGroupOrder(source, group))
				.ThenBy(group => group, StringComparer.OrdinalIgnoreCase)
				.ToList();

			foreach (var (group, groupEntries) in byGroup)
				AddStickerTypes(browser, source, group, groupEntries);
		}

		return browser;
	}

	private void AddStickerTypes(StickerBrowser browser, string source, string group, List<StickerMenuEntry> entries)
	{
		var byType = new Dictionary<string, List<StickerMenuEntry>>();
		foreach (var entry in entries)
		{
			if (!byType.TryGetValue(entry.Type, out var list))
				byType[entry.Type] = list = [];
			list.Add(entry);
		}

		browser.Types[TypeKey(source, group)] = byType.Keys.OrderBy(type => Array.IndexOf(StickerTypes, type)).ToList();

		foreach (var (type, typeEntries) in byType)
		{
			browser.Lists[ListKey(source, group, type, "")] = StickerRows(typeEntries, "");
			if (!SplitStickerAudience(source, group, type))
				continue;

			browser.Lists[ListKey(source, group, type, "Teams")] = StickerRows(typeEntries, "Teams");
			browser.Lists[ListKey(source, group, type, "Players")] = StickerRows(typeEntries, "Players");
		}
	}

	private static bool SplitStickerAudience(string source, string group, string type)
	{
		if (source != "Major Stickers" || TeamOnlyStickerEvents.Contains(group, StringComparer.OrdinalIgnoreCase))
			return false;
		if (type == "Holo" && DirectHoloStickerEvents.Contains(group, StringComparer.OrdinalIgnoreCase))
			return false;
		return true;
	}

	private List<MenuItem> StickerRows(List<StickerMenuEntry> entries, string audience)
	{
		var items = new List<MenuItem>(entries.Count);

		foreach (var entry in entries)
		{
			if (audience.Length > 0 && entry.IsPlayer != (audience == "Players"))
				continue;

			var value = entry.Sticker;
			items.Add(new MenuItem(value.Name, (p, s) => PlaceSticker(p, s, value)));
		}

		return items;
	}

	private Menu StickerSourceMenu(CCSPlayerController player, MenuSession session)
	{
		var browser = Browser();
		if (browser == null)
			return StickerLoadingMenu();

		var items = new List<MenuItem>(browser.Sources.Count);

		foreach (var source in browser.Sources)
		{
			var value = source;
			items.Add(new MenuItem(source, (p, s) =>
			{
				if (value == "Community / Workshop Stickers")
					Renderer.Open(p, (pp, ss) => StickerTypeMenu(pp, ss, value, ""));
				else
					Renderer.Open(p, (pp, ss) => StickerEventMenu(pp, ss, value));
			}));
		}

		return new Menu
		{
			Title = plugin.Text("menu.sticker_source", StickerSlotTitle(session.Ctx.StickerSlot)),
			Items = items
		};
	}

	private Menu StickerEventMenu(CCSPlayerController player, MenuSession session, string source)
	{
		var browser = Browser();
		if (browser == null)
			return StickerLoadingMenu();

		var groups = browser.Groups.GetValueOrDefault(source) ?? [];
		var items = new List<MenuItem>(groups.Count);
		foreach (var group in groups)
		{
			var value = group;
			items.Add(new MenuItem(group, (p, s) => Renderer.Open(p, (pp, ss) => StickerTypeMenu(pp, ss, source, value))));
		}

		return new Menu { Title = plugin.Text("menu.sticker_event", source), Items = items };
	}

	private Menu StickerTypeMenu(CCSPlayerController player, MenuSession session, string source, string group)
	{
		var browser = Browser();
		if (browser == null)
			return StickerLoadingMenu();

		var types = browser.Types.GetValueOrDefault(TypeKey(source, group)) ?? [];
		var items = new List<MenuItem>(types.Count);
		foreach (var type in types)
		{
			var value = type;
			items.Add(new MenuItem(type, (p, s) =>
			{
				if (SplitStickerAudience(source, group, value))
					Renderer.Open(p, (pp, ss) => StickerAudienceMenu(pp, ss, source, group, value));
				else
					Renderer.Open(p, (pp, ss) => StickerSelectionList(pp, ss, source, group, value, ""));
			}));
		}

		var title = group.Length > 0 ? group : source;
		return new Menu { Title = plugin.Text("menu.sticker_type", title), Items = items };
	}

	private Menu StickerAudienceMenu(CCSPlayerController player, MenuSession session, string source, string group, string type)
	{
		var items = new List<MenuItem>
		{
			new(plugin.Text("menu.sticker_teams"), (p, s) => Renderer.Open(p, (pp, ss) => StickerSelectionList(pp, ss, source, group, type, "Teams"))),
			new(plugin.Text("menu.sticker_players"), (p, s) => Renderer.Open(p, (pp, ss) => StickerSelectionList(pp, ss, source, group, type, "Players")))
		};

		return new Menu { Title = plugin.Text("menu.sticker_audience", type), Items = items };
	}

	private Menu StickerSelectionList(CCSPlayerController player, MenuSession session, string source, string group, string type, string audience)
	{
		var browser = Browser();
		if (browser == null)
			return StickerLoadingMenu();

		var items = browser.Lists.GetValueOrDefault(ListKey(source, group, type, audience)) ?? [];
		return new Menu { Title = StickerListTitle(session), Items = items };
	}

	private Menu StickerLoadingMenu()
	{
		Prewarm();
		return new Menu
		{
			Title = plugin.Text("menu.stickers"),
			Items = [new MenuItem(plugin.Text("menu.loading"), null)]
		};
	}

	private static string TypeKey(string source, string group) => $"{source}|{group}";

	private static string ListKey(string source, string group, string type, string audience) => $"{source}|{group}|{type}|{audience}";

	private string StickerListTitle(MenuSession session)
	{
		return session.Ctx.StickerSlot < 0
			? plugin.Text("menu.set_all")
			: plugin.Text("menu.sticker_list", session.Ctx.StickerSlot + 1);
	}

	private Menu FilteredStickerList(CCSPlayerController player, MenuSession session)
	{
		var query = session.Ctx.Search;
		var items = new List<MenuItem>
		{
			new(plugin.Text("menu.search_all"), (p, s) =>
			{
				s.Ctx.Search = "";
				Renderer.Open(p, StickerSourceMenu);
			})
		};

		var stickerItems = StickerItems();
		if (stickerItems == null)
			return StickerLoadingMenu();

		foreach (var item in stickerItems)
		{
			if (item.Text.Contains(query, StringComparison.OrdinalIgnoreCase))
				items.Add(item);
		}

		if (items.Count == 2)
			items.Add(new MenuItem(plugin.Text("menu.no_results"), null));

		return new Menu { Title = StickerListTitle(session), Items = items };
	}

	private void PromptStickerSearch(CCSPlayerController player, MenuSession session)
	{
		Renderer.Prompt(player, session, plugin.Text("prompt.search"), (p, s, text) =>
		{
			s.Ctx.Search = text;
			Renderer.Rebuild(p, s);
		});
	}

	private void PlaceSticker(CCSPlayerController player, MenuSession session, StickerDef sticker)
	{
		var def = session.Ctx.WeaponDef;
		var loadout = Cache.Get(player);
		if (loadout == null || !Skinned(player, def))
			return;

		var slot = session.Ctx.StickerSlot;
		foreach (var team in PlayerCache.TargetTeams(player))
		{
			var entry = loadout.For(team).GetOrAddWeapon(def);
			if (slot < 0)
			{
				entry.Stickers.Clear();
				for (var i = 0; i < 4; i++)
					entry.Stickers.Add(new StickerEntry { Slot = i, Id = sticker.Id });
			}
			else
			{
				entry.Stickers.RemoveAll(s => s.Slot == slot);
				entry.Stickers.Add(new StickerEntry { Slot = slot, Id = sticker.Id });
			}

			DropUnmanagedStickers(entry);
			plugin.Save(Store.SaveWeaponAndStickers(player.SteamID, team, def, entry));
		}

		Applier.RefreshOwned(player, def);
		if (slot < 0)
			plugin.Reply(player, "sticker_set_all", sticker.Name);
		else
			plugin.Reply(player, "sticker_set", sticker.Name, slot + 1);
		Renderer.Back(player, session);
	}

	private void RemoveSticker(CCSPlayerController player, MenuSession session)
	{
		var def = session.Ctx.WeaponDef;
		var loadout = Cache.Get(player);
		if (loadout == null || !Skinned(player, def))
			return;

		var slot = session.Ctx.StickerSlot;
		foreach (var team in PlayerCache.TargetTeams(player))
		{
			var entry = loadout.For(team).GetOrAddWeapon(def);
			if (slot < 0)
				entry.Stickers.Clear();
			else
				entry.Stickers.RemoveAll(s => s.Slot == slot);

			DropUnmanagedStickers(entry);
			plugin.Save(Store.SaveStickers(player.SteamID, team, def, entry.Paint, entry.Stickers));
		}

		Applier.RefreshOwned(player, def);
		if (slot < 0)
			plugin.Reply(player, "stickers_cleared");
		else
			plugin.Reply(player, "sticker_removed", slot + 1);
		Renderer.Back(player, session);
	}

	private void RemoveAllStickers(CCSPlayerController player, MenuSession session)
	{
		var def = session.Ctx.WeaponDef;
		var loadout = Cache.Get(player);
		if (loadout == null || !Skinned(player, def))
			return;

		foreach (var team in PlayerCache.TargetTeams(player))
		{
			var entry = loadout.For(team).GetOrAddWeapon(def);
			entry.Stickers.Clear();
			entry.Charm = null;
			plugin.Save(Store.SaveStickersAndCharm(
				player.SteamID,
				team,
				def,
				entry.Paint,
				entry.Stickers,
				entry.Charm));
		}

		Applier.RefreshOwned(player, def);
		plugin.Reply(player, "stickers_cleared");
		Renderer.Rebuild(player, session);
	}

	private static string GetStickerSource(StickerDef sticker)
	{
		if (GetStickerEvent(sticker).Length > 0)
			return "Major Stickers";
		if (GetStickerOperationGroup(sticker).Length > 0)
			return "Operation Stickers";
		if (GetStickerCapsuleGroup(sticker).Length > 0)
			return "Capsule Stickers";

		return "Community / Workshop Stickers";
	}

	private static string GetStickerGroup(StickerDef sticker, string source)
	{
		return source switch
		{
			"Major Stickers" => GetStickerEvent(sticker),
			"Operation Stickers" => GetStickerOperationGroup(sticker),
			"Capsule Stickers" => GetStickerCapsuleGroup(sticker),
			_ => ""
		};
	}

	private static string GetStickerEvent(StickerDef sticker)
	{
		if (sticker.TournamentEvent.Length > 0)
			return sticker.TournamentEvent;

		var parts = sticker.Name.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
		if (parts.Length < 2)
			return "";

		var eventName = parts[^1];
		var index = Array.FindIndex(MajorStickerEvents, item => string.Equals(item, eventName, StringComparison.OrdinalIgnoreCase));
		return index >= 0 ? MajorStickerEvents[index] : "";
	}

	private static string GetStickerOperationGroup(StickerDef sticker)
	{
		foreach (var operation in OperationStickerEvents)
		{
			if (sticker.Capsule.Contains(operation, StringComparison.OrdinalIgnoreCase) ||
				sticker.Name.Contains(operation, StringComparison.OrdinalIgnoreCase))
				return operation;
		}

		var subject = GetStickerSubject(sticker.Name);
		if (ShatteredWebStickerSubjects.Contains(subject, StringComparer.OrdinalIgnoreCase))
			return "Shattered Web";
		if (RiptideStickerSubjects.Contains(subject, StringComparer.OrdinalIgnoreCase))
			return "Riptide";
		if (BrokenFangStickerSubjects.Contains(subject, StringComparer.OrdinalIgnoreCase))
			return "Broken Fang";

		return "";
	}

	private static string GetStickerCapsuleGroup(StickerDef sticker)
	{
		var capsule = sticker.Capsule;
		if (capsule.Contains("Warhammer", StringComparison.OrdinalIgnoreCase)) return "Warhammer";
		if (string.Equals(capsule, "Sticker Capsule 2", StringComparison.OrdinalIgnoreCase)) return "Sticker Capsule 2";
		if (string.Equals(capsule, "Sticker Capsule", StringComparison.OrdinalIgnoreCase)) return "Sticker Capsule";
		if (capsule.Contains("CS20", StringComparison.OrdinalIgnoreCase)) return "CS20";
		if (capsule.Contains("Community", StringComparison.OrdinalIgnoreCase) && capsule.Contains("2018", StringComparison.OrdinalIgnoreCase)) return "Community 2018";
		if (capsule.Contains("Community", StringComparison.OrdinalIgnoreCase) && capsule.Contains("2021", StringComparison.OrdinalIgnoreCase)) return "2021 Community";
		if (capsule.Contains("Community", StringComparison.OrdinalIgnoreCase) && capsule.Contains("1", StringComparison.OrdinalIgnoreCase)) return "Community 1";
		if (capsule.Contains("10 Year Birthday", StringComparison.OrdinalIgnoreCase)) return "10 Year Birthday";
		if (capsule.Contains("Ambush", StringComparison.OrdinalIgnoreCase)) return "Ambush";
		if (capsule.Contains("Pinups", StringComparison.OrdinalIgnoreCase)) return "Pinups";
		if (capsule.Contains("Enfu", StringComparison.OrdinalIgnoreCase)) return "Enfu";
		if (capsule.Contains("Perfect World", StringComparison.OrdinalIgnoreCase) && capsule.Contains("2", StringComparison.OrdinalIgnoreCase)) return "Perfect World 2";
		if (capsule.Contains("Perfect World", StringComparison.OrdinalIgnoreCase)) return "Perfect World 1";
		if (capsule.Contains("The Boardroom", StringComparison.OrdinalIgnoreCase)) return "The Boardroom";
		if (capsule.Contains("Half-Life", StringComparison.OrdinalIgnoreCase) || capsule.Contains("Alyx", StringComparison.OrdinalIgnoreCase)) return "Half-Life: Alyx";
		if (capsule.Contains("Halo", StringComparison.OrdinalIgnoreCase)) return "Halo";
		if (capsule.Contains("Skill Groups", StringComparison.OrdinalIgnoreCase)) return "Skill Groups";
		if (capsule.Contains("Espionage", StringComparison.OrdinalIgnoreCase)) return "Espionage";
		if (capsule.Contains("Poorly Drawn", StringComparison.OrdinalIgnoreCase)) return "Poorly Drawn";
		if (capsule.Contains("Team Roles", StringComparison.OrdinalIgnoreCase)) return "Team Roles";
		if (capsule.Contains("Recoil", StringComparison.OrdinalIgnoreCase)) return "Recoil";
		if (capsule.Contains("Bestiary", StringComparison.OrdinalIgnoreCase)) return "Bestiary";
		if (capsule.Contains("Slid3", StringComparison.OrdinalIgnoreCase)) return "Slid3";
		if (capsule.Contains("Chicken", StringComparison.OrdinalIgnoreCase)) return "Chicken";
		if (capsule.Contains("Sugarface", StringComparison.OrdinalIgnoreCase)) return "Sugarface";
		if (capsule.Contains("Feral Predators", StringComparison.OrdinalIgnoreCase)) return "Feral Predators";
		if (capsule.Contains("Battlefield 2042", StringComparison.OrdinalIgnoreCase)) return "Battlefield 2042";

		if (capsule.Length > 0)
		{
			var label = capsule
				.Replace("Sticker Capsule", "", StringComparison.OrdinalIgnoreCase)
				.Replace("Sticker Collection", "", StringComparison.OrdinalIgnoreCase)
				.Trim();
			return label.Length == 0 ? capsule : label;
		}

		var subject = GetStickerSubject(sticker.Name);
		if (StickerCapsule2Subjects.Contains(subject, StringComparer.OrdinalIgnoreCase))
			return "Sticker Capsule 2";
		if (AmbushStickerSubjects.Contains(subject, StringComparer.OrdinalIgnoreCase))
			return "Ambush";

		return "";
	}

	private static string GetStickerType(string name)
	{
		if (name.Contains("(Holo", StringComparison.OrdinalIgnoreCase)) return "Holo";
		if (name.Contains("(Foil", StringComparison.OrdinalIgnoreCase)) return "Foil";
		if (name.Contains("(Gold", StringComparison.OrdinalIgnoreCase)) return "Gold";
		if (name.Contains("(Glitter", StringComparison.OrdinalIgnoreCase)) return "Glitter";
		if (name.Contains("(Embroidered", StringComparison.OrdinalIgnoreCase)) return "Embroidered";
		if (name.Contains("(Lenticular", StringComparison.OrdinalIgnoreCase)) return "Lenticular";
		return "Normal";
	}

	private static bool IsPlayerSticker(StickerDef sticker)
	{
		if (string.Equals(sticker.Kind, "Autograph", StringComparison.OrdinalIgnoreCase) ||
			sticker.TournamentPlayer.Length > 0)
			return true;
		if (string.Equals(sticker.Kind, "Team", StringComparison.OrdinalIgnoreCase) ||
			string.Equals(sticker.Kind, "Event", StringComparison.OrdinalIgnoreCase))
			return false;

		var subject = GetStickerSubject(sticker.Name);
		if (subject.Length == 0)
			return false;
		if (StickerTeamNames.Any(team => string.Equals(subject, team, StringComparison.OrdinalIgnoreCase)))
			return false;
		if (MajorStickerEvents.Any(item => string.Equals(subject, item, StringComparison.OrdinalIgnoreCase)))
			return false;

		return true;
	}

	private static string GetStickerSubject(string name)
	{
		var parts = name.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
		var subject = parts.Length > 0 ? parts[0] : name;
		var parenthesis = subject.IndexOf('(');
		if (parenthesis >= 0)
			subject = subject[..parenthesis];
		return subject.Trim();
	}

	private static int StickerGroupOrder(string source, string group)
	{
		var groups = source switch
		{
			"Major Stickers" => MajorStickerEvents,
			"Operation Stickers" => OperationStickerEvents,
			"Capsule Stickers" => CapsuleStickerEvents,
			_ => []
		};

		var index = Array.FindIndex(groups, item => string.Equals(item, group, StringComparison.OrdinalIgnoreCase));
		return index >= 0 ? index : groups.Length;
	}

	private static string StickerSlotTitle(int slot)
	{
		return slot < 0 ? "All Slots" : (slot + 1).ToString(CultureInfo.InvariantCulture);
	}

	private sealed record StickerMenuEntry(StickerDef Sticker, string Source, string Group, string Type, bool IsPlayer);

	private sealed class StickerBrowser
	{
		public readonly List<string> Sources = [];
		public readonly Dictionary<string, List<string>> Groups = [];
		public readonly Dictionary<string, List<string>> Types = [];
		public readonly Dictionary<string, List<MenuItem>> Lists = [];
	}

	private static readonly string[] StickerSources =
	[
		"Major Stickers", "Operation Stickers", "Capsule Stickers", "Community / Workshop Stickers"
	];

	private static readonly string[] StickerTypes =
	[
		"Normal", "Holo", "Foil", "Gold", "Glitter", "Embroidered", "Lenticular"
	];

	private static readonly string[] MajorStickerEvents =
	[
		"Cologne 2026", "Budapest 2025", "Austin 2025", "Shanghai 2024", "Copenhagen 2024", "Paris 2023",
		"Rio 2022", "Antwerp 2022", "Stockholm 2021", "2020 RMR", "Berlin 2019", "Katowice 2019",
		"London 2018", "Boston 2018", "Krakow 2017", "Atlanta 2017", "Cologne 2016",
		"MLG Columbus 2016", "Cluj-Napoca 2015", "Cologne 2015", "Katowice 2015",
		"DreamHack 2014", "Cologne 2014", "Katowice 2014", "DreamHack 2013"
	];

	private static readonly string[] OperationStickerEvents =
	[
		"Shattered Web", "Riptide", "Broken Fang"
	];

	private static readonly string[] CapsuleStickerEvents =
	[
		"Sticker Capsule", "Sticker Capsule 2", "CS20", "Community 1", "10 Year Birthday",
		"Warhammer", "Ambush", "Pinups", "Enfu", "Community 2018", "Perfect World 1",
		"The Boardroom", "Half-Life: Alyx", "2021 Community", "Halo", "Skill Groups",
		"Espionage", "Perfect World 2", "Poorly Drawn", "Team Roles", "Recoil", "Bestiary",
		"Slid3", "Chicken", "Sugarface", "Feral Predators", "Battlefield 2042"
	];

	private static readonly string[] TeamOnlyStickerEvents =
	[
		"2020 RMR", "Katowice 2015", "DreamHack 2014", "Cologne 2014", "Katowice 2014", "DreamHack 2013"
	];

	private static readonly string[] DirectHoloStickerEvents =
	[
		"Berlin 2019", "London 2018", "Boston 2018", "Krakow 2017", "Atlanta 2017",
		"Cologne 2016", "MLG Columbus 2016"
	];

	private static readonly string[] StickerTeamNames =
	[
		"3DMAX", "9INE", "9z Team", "100 Thieves", "ALTERNATE aTTaX", "AMKAL ESPORTS", "Astralis",
		"AVANGAR", "Bad News Eagles", "BIG", "Cloud9", "Clan-Mystik", "Complexity Gaming", "compLexity Gaming",
		"Counter Logic Gaming", "Copenhagen Flames", "CR4ZY", "dAT team", "ENCE", "Epsilon eSports", "eSuba",
		"Eternal Fire", "FaZe Clan", "FlipSid3 Tactics", "Fluxo", "forZe eSports", "Fnatic", "FURIA",
		"G2 Esports", "Gambit Esports", "Gambit Gaming", "GamerLegion", "GODSENT", "Grayhound Gaming",
		"HellRaisers", "Heroic", "iBUYPOWER", "Imperial Esports", "Into The Breach", "Keyd Stars", "Legacy",
		"Liquid", "London Conspiracy", "Luminosity Gaming", "MIBR", "MOUZ", "mousesports", "Natus Vincere",
		"Ninjas in Pyjamas", "North", "OpTic Gaming", "paiN Gaming", "PENTA Sports", "Planetkey Dynamics",
		"Quantum Bellator Fire", "Reason Gaming", "Renegades", "SAW", "SK Gaming", "Space Soldiers", "Spirit",
		"Team Dignitas", "Team EnVyUs", "Team Immunity", "Team Kinguin", "Team LDLC.com", "Team Liquid",
		"Team SoloMid", "Team Spirit", "Titan", "TYLOO", "Vega Squadron", "Virtus.Pro", "Vitality",
		"Vox Eminor", "Winstrike Team", "Wolf", "The MongolZ", "Rare Atom", "FlyQuest", "M80", "Wildcard",
		"Passion UA", "Aurora", "Nemiga", "BetBoom Team", "Falcons", "PARIVISION", "B8", "Lynn Vision",
		"NRG", "BESTIA", "RED Canids", "OG", "Endpoint", "Entropiq", "Evil Geniuses", "00 Nation",
		"IHC Esports", "Outsiders", "Sprout", "Movistar Riders", "MASONIC", "ECSTATIC", "Team One",
		"Vici Gaming", "LGB eSports", "Flow Gaming", "mYinsanity", "Orbit eSport", "ex-Outsiders"
	];

	private static readonly string[] ShatteredWebStickerSubjects =
	[
		"Counter-Tech", "Gold Web", "Mastermind", "Shattered Web", "Terrorist-Tech", "Web Stuck"
	];

	private static readonly string[] RiptideStickerSubjects =
	[
		"Seeing Red", "Dead Eye", "Great Wave", "Gutted", "Kill Count", "Operation Riptide", "Liquid Fire", "Chicken of the Sky"
	];

	private static readonly string[] BrokenFangStickerSubjects =
	[
		"Ancient Beast", "Ancient Marauder", "Ancient Protector", "Badge of Service", "Battle Scarred",
		"Broken Fang", "Coiled Strike", "Enemy Spotted", "Stalking Prey", "Stone Scales"
	];

	private static readonly string[] StickerCapsule2Subjects =
	[
		"Crown"
	];

	private static readonly string[] AmbushStickerSubjects =
	[
		"Boom Detonation", "Boom Epicenter", "Boom Blast", "Boom Trail", "Rainbow Route"
	];

	private WeaponEntry? Entry(CCSPlayerController player, int def)
	{
		return Cache.Get(player)?.For(PlayerCache.TargetTeams(player)[0]).Weapons.GetValueOrDefault(def);
	}

	public bool IsSkinned(CCSPlayerController player, int def)
	{
		var entry = Entry(player, def);
		return entry != null && entry.Paint > 0;
	}

	private bool Skinned(CCSPlayerController player, int def)
	{
		if (IsSkinned(player, def))
			return true;

		plugin.Reply(player, "skin_required");
		return false;
	}

	private TeamLoadout? Side(CCSPlayerController player)
	{
		return Cache.Get(player)?.For(PlayerCache.TargetTeams(player)[0]);
	}

	private static float MinWear(PaintDef paint) => Math.Max(paint.MinFloat, 0.000001f);

	private static float MinimumWeaponWear(int def, PaintDef paint)
	{
		var minimum = KnifeService.IsKnifeDef(def) ? KnifeService.MinimumWear : 0.000001f;
		return Math.Min(paint.MaxFloat, Math.Max(paint.MinFloat, minimum));
	}
}
