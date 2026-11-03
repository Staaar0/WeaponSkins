using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

namespace WeaponSkins;

/// <summary>
/// Entry point for external menus such as PMM_WeaponSkins (added for the PMM build of WeaponSkins).
/// Only primitive types, delegates of shared types and JSON strings cross this boundary, so another
/// plugin can call it through reflection without sharing an assembly. Every method must be called on
/// the game thread. Nothing here changes how WeaponSkins behaves when no external menu is registered.
/// </summary>
public static class WeaponSkinsApi
{
	public const int ApiVersion = 1;

	/// <summary>Team bits used by the Save* methods: 1 = Terrorists, 2 = Counter-Terrorists, 0 = the player's own team.</summary>
	public const int TeamT = 1;
	public const int TeamCT = 2;

	private static readonly object sync = new();
	private static readonly List<Action<ulong>> listeners = [];
	private static readonly Dictionary<ulong, DateTime> agentChange = [];
	private static Func<CCSPlayerController, string, bool>? externalMenu;
	private static PlayerCache? hookedCache;
	private static CatalogService? hookedCatalog;
	private static int catalogStamp = 1;

	private static global::WeaponSkins.WeaponSkins? Plugin => global::WeaponSkins.WeaponSkins.Instance;

	/// <summary>WeaponSkins is loaded, started and has its item catalogue.</summary>
	public static bool IsReady
	{
		get
		{
			var plugin = Plugin;
			return plugin != null && plugin.Initialized && !plugin.Stopping && plugin.Catalog != null && plugin.Catalog.Loaded;
		}
	}

	/// <summary>Changes whenever the catalogue is reloaded; re-read <see cref="CatalogJson"/> when it does.</summary>
	public static int CatalogStamp
	{
		get
		{
			Hook();
			return catalogStamp;
		}
	}

	public static bool StickersEnabled => Plugin?.Config.Stickers.Enabled ?? false;

	// ------------------------------------------------------------------ external menu

	/// <summary>
	/// Registers the menu that the WeaponSkins menu commands (!ws, !knife, !gloves, !agents, !music, !pin,
	/// !stickers) open instead of the built-in one. Sections: skins, knife, gloves, agents, music, pins,
	/// stickers. Return false from the handler to fall back to the built-in menu. Pass null to unregister.
	/// </summary>
	public static void SetExternalMenu(Func<CCSPlayerController, string, bool>? handler)
	{
		externalMenu = handler;
		Hook();
	}

	public static bool HasExternalMenu => externalMenu != null;

	internal static bool OpenExternal(CCSPlayerController player, string section)
	{
		var handler = externalMenu;
		if (handler == null)
			return false;

		try
		{
			return handler(player, section);
		}
		catch (Exception ex)
		{
			Plugin?.Logger.LogError("External menu failed to open '{Section}': {Error}", section, ex.Message);
			return false;
		}
	}

	// ------------------------------------------------------------------ loadout events

	/// <summary>Called with a SteamID64 whenever a loadout finished (re)loading from the database.</summary>
	public static void AddLoadoutListener(Action<ulong> listener)
	{
		lock (sync)
		{
			if (!listeners.Contains(listener))
				listeners.Add(listener);
		}
		Hook();
	}

	public static void RemoveLoadoutListener(Action<ulong> listener)
	{
		lock (sync)
			listeners.Remove(listener);
	}

	private static void Hook()
	{
		var plugin = Plugin;
		if (plugin == null)
			return;

		var cache = plugin.Cache;
		if (cache != null && !ReferenceEquals(cache, hookedCache))
		{
			if (hookedCache != null)
				hookedCache.LoadoutReady -= OnLoadoutReady;
			cache.LoadoutReady += OnLoadoutReady;
			hookedCache = cache;
		}

		var catalog = plugin.Catalog;
		if (catalog != null && !ReferenceEquals(catalog, hookedCatalog))
		{
			if (hookedCatalog != null)
				hookedCatalog.Changed -= OnCatalogChanged;
			catalog.Changed += OnCatalogChanged;
			hookedCatalog = catalog;
			Interlocked.Increment(ref catalogStamp);
		}
	}

	private static void OnCatalogChanged() => Interlocked.Increment(ref catalogStamp);

	private static void OnLoadoutReady(ulong steamId)
	{
		Action<ulong>[] copy;
		lock (sync)
			copy = listeners.ToArray();

		foreach (var listener in copy)
		{
			try
			{
				listener(steamId);
			}
			catch (Exception ex)
			{
				Plugin?.Logger.LogError("Loadout listener failed: {Error}", ex.Message);
			}
		}
	}

	// ------------------------------------------------------------------ checks

	/// <summary>
	/// Null when the player can change skins right now, otherwise a WeaponSkins translation key
	/// (link_required, data_not_loaded, loadout_not_ready). With reply the reason is also printed to chat.
	/// </summary>
	public static string? CheckPlayer(CCSPlayerController? player, bool reply)
	{
		var error = Check(player, out _, out _);
		if (error != null && reply && player != null && player.IsValid && Plugin is { } plugin && error != "not_ready")
			plugin.Reply(player, error);
		return error;
	}

	public static bool StickersAllowed(CCSPlayerController player)
	{
		var plugin = Plugin;
		return plugin != null && player.IsValid && plugin.StickersAllowed(player);
	}

	private static string? Check(CCSPlayerController? player, out global::WeaponSkins.WeaponSkins plugin, out PlayerLoadout loadout)
	{
		plugin = null!;
		loadout = null!;
		var current = Plugin;
		if (current == null || !current.Initialized || current.Stopping)
			return "not_ready";
		if (player == null || !player.IsValid || player.IsBot)
			return "not_ready";
		if (!current.CanUseSkins(player))
			return "link_required";
		if (current.Catalog == null || !current.Catalog.Loaded)
			return "data_not_loaded";
		var found = current.Cache?.Get(player);
		if (found == null || !found.Ready)
			return "loadout_not_ready";

		plugin = current;
		loadout = found;
		return null;
	}

	private static List<CsTeam> Teams(CCSPlayerController player, int teams)
	{
		var result = new List<CsTeam>(2);
		if ((teams & TeamT) != 0)
			result.Add(CsTeam.Terrorist);
		if ((teams & TeamCT) != 0)
			result.Add(CsTeam.CounterTerrorist);
		return result.Count > 0 ? result : PlayerCache.TargetTeams(player);
	}

	// ------------------------------------------------------------------ catalogue

	/// <summary>
	/// The whole item catalogue in the server language:
	/// { stamp, names:{def:name}, weapons:[{d,n,c}], paints:{def:[{p,n,mn,mx,l}]}, knives:[{d,n}], gloves:[{d,n}],
	///   agents:[{m,n,t,f}], music:[{i,n}], pins:[{i,n,g}], stickers:[{i,n,k,e,v}], charms:[{i,n}] }.
	/// </summary>
	public static string? CatalogJson()
	{
		var plugin = Plugin;
		var catalog = plugin?.Catalog;
		if (catalog == null || !catalog.Loaded)
			return null;

		Hook();
		using var stream = new MemoryStream(1 << 20);
		using (var w = new Utf8JsonWriter(stream))
		{
			w.WriteStartObject();
			w.WriteNumber("stamp", catalogStamp);

			w.WriteStartObject("names");
			foreach (var (def, name) in catalog.WeaponNames)
				w.WriteString(def.ToString(CultureInfo.InvariantCulture), name);
			w.WriteEndObject();

			w.WriteStartArray("weapons");
			foreach (var category in catalog.Categories)
			{
				if (!catalog.WeaponsByCategory.TryGetValue(category, out var list))
					continue;
				foreach (var weapon in list)
				{
					w.WriteStartObject();
					w.WriteNumber("d", weapon.DefIndex);
					w.WriteString("n", weapon.Name);
					w.WriteString("c", weapon.Category);
					w.WriteEndObject();
				}
			}
			w.WriteEndArray();

			w.WriteStartObject("paints");
			foreach (var (def, paints) in catalog.Paints)
			{
				w.WriteStartArray(def.ToString(CultureInfo.InvariantCulture));
				foreach (var paint in paints)
				{
					w.WriteStartObject();
					w.WriteNumber("p", paint.Paint);
					w.WriteString("n", paint.Name);
					w.WriteNumber("mn", paint.MinFloat);
					w.WriteNumber("mx", paint.MaxFloat);
					if (paint.Legacy)
						w.WriteBoolean("l", true);
					w.WriteEndObject();
				}
				w.WriteEndArray();
			}
			w.WriteEndObject();

			w.WriteStartArray("knives");
			foreach (var knife in catalog.Knives)
			{
				w.WriteStartObject();
				w.WriteNumber("d", knife.DefIndex);
				w.WriteString("n", knife.Name);
				w.WriteEndObject();
			}
			w.WriteEndArray();

			w.WriteStartArray("gloves");
			foreach (var glove in catalog.Gloves)
			{
				w.WriteStartObject();
				w.WriteNumber("d", glove.DefIndex);
				w.WriteString("n", glove.Name);
				w.WriteEndObject();
			}
			w.WriteEndArray();

			w.WriteStartArray("agents");
			foreach (var agent in catalog.AgentsT.Concat(catalog.AgentsCT))
			{
				w.WriteStartObject();
				w.WriteString("m", agent.Model);
				w.WriteString("n", agent.Name);
				w.WriteNumber("t", agent.Team == CsTeam.CounterTerrorist ? 3 : 2);
				w.WriteString("f", agent.Faction);
				w.WriteEndObject();
			}
			w.WriteEndArray();

			w.WriteStartArray("music");
			foreach (var kit in catalog.MusicKits)
			{
				w.WriteStartObject();
				w.WriteNumber("i", kit.Id);
				w.WriteString("n", kit.Name);
				w.WriteEndObject();
			}
			w.WriteEndArray();

			w.WriteStartArray("pins");
			foreach (var pin in catalog.Pins)
			{
				w.WriteStartObject();
				w.WriteNumber("i", pin.Id);
				w.WriteString("n", pin.Name);
				w.WriteString("g", pin.Group);
				w.WriteEndObject();
			}
			w.WriteEndArray();

			w.WriteStartArray("stickers");
			foreach (var sticker in catalog.Stickers)
			{
				w.WriteStartObject();
				w.WriteNumber("i", sticker.Id);
				w.WriteString("n", sticker.Name);
				if (sticker.Kind.Length > 0)
					w.WriteString("k", sticker.Kind);
				if (sticker.Effect.Length > 0)
					w.WriteString("e", sticker.Effect);
				if (sticker.TournamentEvent.Length > 0)
					w.WriteString("v", sticker.TournamentEvent);
				w.WriteEndObject();
			}
			w.WriteEndArray();

			w.WriteStartArray("charms");
			foreach (var charm in catalog.Charms)
			{
				w.WriteStartObject();
				w.WriteNumber("i", charm.Id);
				w.WriteString("n", charm.Name);
				w.WriteEndObject();
			}
			w.WriteEndArray();

			w.WriteEndObject();
		}

		return Encoding.UTF8.GetString(stream.ToArray());
	}

	// ------------------------------------------------------------------ loadout

	/// <summary>
	/// The cached loadout of a player, or null while it is not loaded:
	/// { music, pin, t:Team, ct:Team } where Team = { knife, gd, gp, gw, gs, agent, w:{def:Entry}, saved:{def:{paint:Entry}} }
	/// and Entry = { p, w, s, tag, st, stk:[{sl,i,w,sc,r,x,y,sh}], ch:{i,p,x,y,z} }.
	/// "w" holds the equipped skin of each weapon, "saved" every skin the player has configured before.
	/// </summary>
	public static string? LoadoutJson(ulong steamId)
	{
		var loadout = Plugin?.Cache?.Get(steamId);
		if (loadout == null || !loadout.Ready)
			return null;

		using var stream = new MemoryStream(16 * 1024);
		using (var w = new Utf8JsonWriter(stream))
		{
			w.WriteStartObject();
			w.WriteNumber("music", loadout.MusicKit);
			w.WriteNumber("pin", loadout.Pin);
			WriteTeam(w, "t", loadout.T);
			WriteTeam(w, "ct", loadout.CT);
			w.WriteEndObject();
		}

		return Encoding.UTF8.GetString(stream.ToArray());
	}

	private static void WriteTeam(Utf8JsonWriter w, string name, TeamLoadout side)
	{
		w.WriteStartObject(name);
		w.WriteNumber("knife", side.Knife);
		w.WriteNumber("gd", side.GloveDef);
		w.WriteNumber("gp", side.GlovePaint);
		if (side.GloveDef > 0 && side.Skins.TryGetValue(side.GloveDef, out var gloves) && gloves.TryGetValue(side.GlovePaint, out var glove))
		{
			w.WriteNumber("gw", glove.Wear);
			w.WriteNumber("gs", glove.Seed);
		}
		if (!string.IsNullOrEmpty(side.AgentModel))
			w.WriteString("agent", side.AgentModel);

		w.WriteStartObject("w");
		foreach (var (def, entry) in side.Weapons)
		{
			w.WritePropertyName(def.ToString(CultureInfo.InvariantCulture));
			WriteEntry(w, entry);
		}
		w.WriteEndObject();

		w.WriteStartObject("saved");
		foreach (var (def, byPaint) in side.Skins)
		{
			w.WriteStartObject(def.ToString(CultureInfo.InvariantCulture));
			foreach (var (paint, entry) in byPaint)
			{
				if (paint <= 0)
					continue;
				w.WritePropertyName(paint.ToString(CultureInfo.InvariantCulture));
				WriteEntry(w, entry);
			}
			w.WriteEndObject();
		}
		w.WriteEndObject();

		w.WriteEndObject();
	}

	private static void WriteEntry(Utf8JsonWriter w, WeaponEntry entry)
	{
		w.WriteStartObject();
		w.WriteNumber("p", entry.Paint);
		w.WriteNumber("w", entry.Wear);
		w.WriteNumber("s", entry.Seed);
		if (!string.IsNullOrEmpty(entry.NameTag))
			w.WriteString("tag", entry.NameTag);
		w.WriteNumber("st", entry.StatTrak);
		if (entry.Stickers.Count > 0)
		{
			w.WriteStartArray("stk");
			foreach (var sticker in entry.Stickers)
			{
				w.WriteStartObject();
				w.WriteNumber("sl", sticker.Slot);
				w.WriteNumber("i", sticker.Id);
				w.WriteNumber("w", sticker.Wear);
				w.WriteNumber("sc", sticker.Scale);
				w.WriteNumber("r", sticker.Rotation);
				w.WriteNumber("x", sticker.OffsetX);
				w.WriteNumber("y", sticker.OffsetY);
				w.WriteNumber("sh", sticker.Schema);
				w.WriteEndObject();
			}
			w.WriteEndArray();
		}
		if (entry.Charm != null)
		{
			w.WriteStartObject("ch");
			w.WriteNumber("i", entry.Charm.Id);
			w.WriteNumber("p", entry.Charm.Pattern);
			w.WriteNumber("x", entry.Charm.OffsetX);
			w.WriteNumber("y", entry.Charm.OffsetY);
			w.WriteNumber("z", entry.Charm.OffsetZ);
			w.WriteEndObject();
		}
		w.WriteEndObject();
	}

	// ------------------------------------------------------------------ changes

	/// <summary>
	/// Saves and equips one skin of a weapon, a knife or the Zeus for the given teams, with its stickers and
	/// charm, and refreshes the weapon the player holds. Choosing a knife skin also selects that knife.
	/// json = { p, w, s, tag, st, stk:[{sl,i,w,sc,r,x,y,sh}], ch:{i,p,x,y,z} }; p = 0 removes the skin.
	/// st: -1 = StatTrak off, -2 = on and keep the current count, 0 or more = on with that count.
	/// Stickers and the charm are dropped when the player may not use them. Returns null or an error key.
	/// </summary>
	public static string? SaveWeapon(CCSPlayerController player, int teams, int defIndex, string json)
	{
		var error = Check(player, out var plugin, out var loadout);
		if (error != null)
			return error;
		if (!plugin.AllowCosmeticAction(player))
			return "cooldown";

		JsonObject? data;
		try
		{
			data = JsonNode.Parse(json) as JsonObject;
		}
		catch (JsonException)
		{
			data = null;
		}
		if (data == null || defIndex <= 0)
			return "invalid";

		var knife = KnifeService.IsKnifeDef(defIndex);
		if (!knife && !plugin.Catalog.Paints.ContainsKey(defIndex))
			return "invalid";

		var paint = ReadInt(data["p"], 0);
		var paintDef = paint > 0 ? plugin.Catalog.FindPaint(defIndex, paint) : null;
		if (paint > 0 && paintDef == null)
			return "invalid";

		var wear = ReadFloat(data["w"], 0.000001f);
		var seed = Math.Clamp(ReadInt(data["s"], 0), 0, 1000);
		var tag = ReadTag(data["tag"]);
		var statTrak = ReadInt(data["st"], -1);

		var allowed = paint > 0 && plugin.StickersAllowed(player);
		var stickers = allowed ? ReadStickers(data["stk"]) : [];
		var charm = allowed ? ReadCharm(data["ch"]) : null;

		foreach (var team in Teams(player, teams))
		{
			var side = loadout.For(team);
			if (knife)
				side.Knife = defIndex;

			var entry = side.Equip(defIndex, paint);
			entry.Wear = ClampWear(defIndex, paintDef, wear);
			entry.Seed = seed;
			entry.NameTag = tag;
			entry.StatTrak = statTrak switch
			{
				-2 => entry.StatTrak >= 0 ? entry.StatTrak : 0,
				< 0 => -1,
				_ => statTrak
			};
			entry.Stickers = stickers.Select(Clone).ToList();
			entry.Charm = charm == null ? null : Clone(charm);
			plugin.Save(knife
				? plugin.Store.SaveGeneratedKnife(player.SteamID, team, defIndex, entry)
				: plugin.Store.SaveGeneratedWeapon(player.SteamID, team, defIndex, entry));
		}

		plugin.Applier.RefreshOwned(player, defIndex);
		return null;
	}

	/// <summary>Selects a knife (0 = the default knife) for the given teams.</summary>
	public static string? SaveKnife(CCSPlayerController player, int teams, int defIndex)
	{
		var error = Check(player, out var plugin, out var loadout);
		if (error != null)
			return error;
		if (!plugin.AllowCosmeticAction(player))
			return "cooldown";
		if (defIndex > 0 && !KnifeService.IsKnifeDef(defIndex))
			return "invalid";

		var def = Math.Max(defIndex, 0);
		foreach (var team in Teams(player, teams))
		{
			loadout.For(team).Knife = def;
			plugin.Save(plugin.Store.SaveKnife(player.SteamID, team, def));
		}

		if (def > 0)
			plugin.Applier.RefreshOwned(player, def);
		else
			plugin.Applier.RefreshKnife(player);
		return null;
	}

	/// <summary>Selects gloves with a paint, wear and pattern for the given teams. defIndex = 0 restores the agent gloves.</summary>
	public static string? SaveGloves(CCSPlayerController player, int teams, int defIndex, int paint, float wear, int seed)
	{
		var error = Check(player, out var plugin, out var loadout);
		if (error != null)
			return error;
		if (!plugin.AllowCosmeticAction(player))
			return "cooldown";

		if (defIndex <= 0)
		{
			foreach (var team in Teams(player, teams))
			{
				var side = loadout.For(team);
				side.GloveDef = 0;
				side.GlovePaint = 0;
				plugin.Save(plugin.Store.SaveGloves(player.SteamID, team, side, LoadoutStore.WeaponFields.None));
			}
			plugin.GloveApply.Restore(player);
			return null;
		}

		var paintDef = plugin.Catalog.FindPaint(defIndex, paint);
		if (paintDef == null)
			return "invalid";

		foreach (var team in Teams(player, teams))
		{
			var side = loadout.For(team);
			side.GloveDef = defIndex;
			side.GlovePaint = paint;
			var entry = side.Gloves;
			var min = Math.Max(paintDef.MinFloat, 0.000001f);
			entry.Wear = Math.Clamp(CosmeticRules.IsValidWear(wear) ? wear : 0.000001f, Math.Min(min, paintDef.MaxFloat), paintDef.MaxFloat);
			entry.Seed = Math.Clamp(seed, 0, 1000);
			plugin.Save(plugin.Store.SaveGloves(player.SteamID, team, side));
		}

		plugin.GloveApply.Apply(player, true);
		return null;
	}

	/// <summary>Selects an agent model (null or empty = default) for the given teams. Error "agent_cooldown:N" while the cooldown runs.</summary>
	public static string? SaveAgent(CCSPlayerController player, int teams, string? model)
	{
		var error = Check(player, out var plugin, out var loadout);
		if (error != null)
			return error;

		var value = string.IsNullOrWhiteSpace(model) ? null : model;
		var targets = Teams(player, teams);
		if (value != null)
		{
			foreach (var team in targets)
			{
				var list = team == CsTeam.CounterTerrorist ? plugin.Catalog.AgentsCT : plugin.Catalog.AgentsT;
				if (!list.Any(a => string.Equals(a.Model, value, StringComparison.OrdinalIgnoreCase)))
					return "invalid";
			}
		}

		var cooldown = plugin.Config.Agents.ChangeCooldown;
		lock (sync)
		{
			if (cooldown > 0 && agentChange.TryGetValue(player.SteamID, out var last))
			{
				var left = cooldown - (int)(DateTime.UtcNow - last).TotalSeconds;
				if (left > 0)
					return "agent_cooldown:" + left.ToString(CultureInfo.InvariantCulture);
			}
			agentChange[player.SteamID] = DateTime.UtcNow;
		}

		var applyNow = false;
		foreach (var team in targets)
		{
			loadout.For(team).AgentModel = value;
			plugin.Save(plugin.Store.SaveAgent(player.SteamID, team, value));
			applyNow |= player.Team == team;
		}

		if (applyNow)
			plugin.Profile.ApplyAgent(player);
		return null;
	}

	/// <summary>Selects a music kit (0 = default).</summary>
	public static string? SaveMusic(CCSPlayerController player, int kit)
	{
		var error = Check(player, out var plugin, out var loadout);
		if (error != null)
			return error;
		if (!plugin.AllowCosmeticAction(player))
			return "cooldown";
		if (kit > 0 && !plugin.Catalog.MusicKits.Any(m => m.Id == kit))
			return "invalid";

		loadout.MusicKit = Math.Max(kit, 0);
		plugin.Save(plugin.Store.SaveMusic(player.SteamID, loadout.MusicKit));
		plugin.Profile.Schedule(player);
		return null;
	}

	/// <summary>
	/// Puts the player's own skin (paint, wear, pattern, StatTrak, name tag, stickers, charm) on a weapon the player
	/// holds, e.g. one picked up from another player. It goes through the same WeaponApplier.Apply as a bought weapon.
	/// When the player has no skin for that weapon, the weapon is cleared, so it does not keep the previous owner's skin.
	/// Knives are left alone: WeaponSkins already re-applies them on pickup. weaponHandle = weapon.EntityHandle.Raw.
	/// </summary>
	public static string? ApplyToWeapon(CCSPlayerController player, uint weaponHandle)
	{
		var error = Check(player, out var plugin, out var loadout);
		if (error != null)
			return error;

		var weapon = new CHandle<CBasePlayerWeapon>(weaponHandle).Value;
		var pawn = player.PlayerPawn.Value;
		var owner = weapon?.OwnerEntity.Value;
		if (weapon == null || !weapon.IsValid || pawn == null || !pawn.IsValid || owner == null || !owner.IsValid ||
			owner.EntityHandle.Raw != pawn.EntityHandle.Raw)
			return "invalid";
		if (KnifeService.IsKnifeClass(weapon.DesignerName))
			return null;

		var item = weapon.AttributeManager.Item;
		if (item.Handle == IntPtr.Zero)
			return "invalid";

		var defIndex = (int)item.ItemDefinitionIndex;
		loadout.For(player.Team).Weapons.TryGetValue(defIndex, out var entry);
		if (entry == null || entry.Paint <= 0)
		{
			item.AttributeList.Attributes.RemoveAll();
			item.NetworkedDynamicAttributes.Attributes.RemoveAll();
			item.CustomName = "";
			item.EntityQuality = 0;
			weapon.FallbackPaintKit = 0;
			weapon.FallbackSeed = 0;
			weapon.FallbackWear = 0f;
			weapon.FallbackStatTrak = -1;
			item.Initialized = true;
		}
		else
		{
			plugin.Applier.Apply(player, weapon);
		}

		if (pawn.WeaponServices?.ActiveWeapon.Value is { IsValid: true } active && active.EntityHandle.Raw == weaponHandle)
			plugin.Applier.RefreshViewModel(player);
		return null;
	}

	/// <summary>Selects a pin / collectible (0 = none).</summary>
	public static string? SavePin(CCSPlayerController player, int pin)
	{
		var error = Check(player, out var plugin, out var loadout);
		if (error != null)
			return error;
		if (!plugin.AllowCosmeticAction(player))
			return "cooldown";
		if (pin > 0 && !plugin.Catalog.Pins.Any(p => p.Id == pin))
			return "invalid";

		loadout.Pin = Math.Max(pin, 0);
		plugin.Save(plugin.Store.SavePin(player.SteamID, loadout.Pin));
		plugin.Profile.Schedule(player);
		return null;
	}

	// ------------------------------------------------------------------ helpers

	private static float ClampWear(int defIndex, PaintDef? paint, float wear)
	{
		var value = CosmeticRules.IsValidWear(wear) ? wear : 0.000001f;
		var knife = KnifeService.IsKnifeDef(defIndex);
		if (paint == null)
			return Math.Clamp(value, knife ? KnifeService.MinimumWear : 0.000001f, 1f);

		var minimum = knife ? KnifeService.MinimumWear : 0.000001f;
		var low = Math.Min(paint.MaxFloat, Math.Max(paint.MinFloat, minimum));
		return Math.Clamp(value, low, Math.Max(low, paint.MaxFloat));
	}

	private const int MaxStickerSlot = 4;
	private const int MaxStickerSchema = 5;

	private static List<StickerEntry> ReadStickers(JsonNode? node)
	{
		var result = new List<StickerEntry>();
		if (node is not JsonArray array)
			return result;

		var used = new HashSet<int>();
		foreach (var item in array)
		{
			if (item is not JsonObject o)
				continue;
			var sticker = new StickerEntry
			{
				Slot = ReadInt(o["sl"], -1),
				Id = ReadInt(o["i"], 0),
				Wear = Math.Clamp(ReadFloat(o["w"], 0f), 0f, 1f),
				Scale = Math.Clamp(ReadFloat(o["sc"], 1f), 0.1f, 4f),
				Rotation = Math.Clamp(ReadFloat(o["r"], 0f), -360f, 360f),
				OffsetX = Math.Clamp(ReadFloat(o["x"], 0f), -2f, 2f),
				OffsetY = Math.Clamp(ReadFloat(o["y"], 0f), -2f, 2f),
				// Model sticker spot the offsets are measured from (-1 = the slot's own spot). Slot 4, the fifth
				// sticker, always gets one: WeaponApplier writes "sticker slot 4 schema" for it.
				Schema = Math.Clamp(ReadInt(o["sh"], -1), -1, MaxStickerSchema)
			};
			// CS2 weapons take at most 5 stickers: slots 0..4.
			if (sticker.Slot is < 0 or > MaxStickerSlot || !used.Add(sticker.Slot) || !CosmeticRules.IsValidSticker(sticker))
				continue;
			result.Add(sticker);
		}

		return result;
	}

	private static CharmEntry? ReadCharm(JsonNode? node)
	{
		if (node is not JsonObject o)
			return null;

		var charm = new CharmEntry
		{
			Id = ReadInt(o["i"], 0),
			Pattern = Math.Clamp(ReadInt(o["p"], 0), 0, 100000),
			OffsetX = Math.Clamp(ReadFloat(o["x"], 0f), -50f, 50f),
			OffsetY = Math.Clamp(ReadFloat(o["y"], 0f), -50f, 50f),
			OffsetZ = Math.Clamp(ReadFloat(o["z"], 0f), -50f, 50f)
		};
		return CosmeticRules.IsValidCharm(charm) ? charm : null;
	}

	private static string? ReadTag(JsonNode? node)
	{
		if (node is not JsonValue value || !value.TryGetValue(out string? text))
			return null;
		var clean = new string((text ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
		if (clean.Length == 0)
			return null;
		return clean.Length > 64 ? clean[..64] : clean;
	}

	private static int ReadInt(JsonNode? node, int fallback)
	{
		if (node is not JsonValue value)
			return fallback;
		if (value.TryGetValue(out int number))
			return number;
		if (value.TryGetValue(out double real) && double.IsFinite(real))
			return (int)Math.Round(real);
		if (value.TryGetValue(out string? text) && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
			return number;
		return fallback;
	}

	private static float ReadFloat(JsonNode? node, float fallback)
	{
		if (node is not JsonValue value)
			return fallback;
		if (value.TryGetValue(out double real) && double.IsFinite(real))
			return (float)real;
		if (value.TryGetValue(out int number))
			return number;
		if (value.TryGetValue(out string? text) && float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && float.IsFinite(parsed))
			return parsed;
		return fallback;
	}

	private static StickerEntry Clone(StickerEntry s) => new()
	{
		Slot = s.Slot,
		Id = s.Id,
		Wear = s.Wear,
		Scale = s.Scale,
		Rotation = s.Rotation,
		OffsetX = s.OffsetX,
		OffsetY = s.OffsetY,
		Schema = s.Schema
	};

	private static CharmEntry Clone(CharmEntry c) => new()
	{
		Id = c.Id,
		Pattern = c.Pattern,
		Sticker = c.Sticker,
		Highlight = c.Highlight,
		OffsetX = c.OffsetX,
		OffsetY = c.OffsetY,
		OffsetZ = c.OffsetZ
	};
}
