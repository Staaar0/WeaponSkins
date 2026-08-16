using System.Globalization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

namespace WeaponSkins;

public sealed class Commands
{
	private readonly WeaponSkins plugin;

	public Commands(WeaponSkins plugin)
	{
		this.plugin = plugin;
	}

	public void Register()
	{
		var commands = plugin.Config.Commands;
		Add(commands.Skins, "Opens the weapon skins menu", OnSkins);
		Add(commands.Knife, "Opens the knife menu", OnKnife);
		Add(commands.Gloves, "Opens the gloves menu", OnGloves);
		Add(commands.Agents, "Opens the agents menu", OnAgents);
		Add(commands.Music, "Opens the music kit menu", OnMusic);
		Add(commands.Pins, "Opens the pin menu", OnPins);
		Add(commands.Stickers, "Opens the sticker menu for the held weapon", OnStickers);
		Add(commands.NameTag, "Sets a name tag on the held weapon", OnNameTag);
		Add(commands.StatTrak, "Toggles StatTrak on the held weapon", OnStatTrak);
		Add(commands.Wear, "Sets the wear of the held weapon", OnWear);
		Add(commands.Seed, "Sets the pattern seed of the held weapon", OnSeed);
		Add(commands.Gen, "Applies an inspect code to your loadout", OnGen);
		Add(commands.Reload, "Reloads the item data", OnReload);

		if (plugin.Links.Required)
			plugin.AddCommand("css_link", "Gives you a code to link your Steam account in Discord", OnLink);
	}

	private void Add(List<string> aliases, string description, CommandInfo.CommandCallback handler)
	{
		foreach (var alias in aliases.Distinct(StringComparer.OrdinalIgnoreCase))
		{
			if (alias.Length > 0)
				plugin.AddCommand($"css_{alias}", description, handler);
		}
	}

	private CCSPlayerController? Ready(CCSPlayerController? player)
	{
		if (player == null || !player.IsValid || player.IsBot)
			return null;

		if (!plugin.CanUseSkins(player))
		{
			plugin.Reply(player, "link_required");
			return null;
		}

		if (!plugin.Catalog.Loaded)
		{
			plugin.Reply(player, "data_not_loaded");
			return null;
		}

		if (plugin.Cache.Get(player) == null)
		{
			plugin.Reply(player, "loadout_not_ready");
			return null;
		}

		return player;
	}

	private void OnLink(CCSPlayerController? caller, CommandInfo info)
	{
		if (caller == null || !caller.IsValid || caller.IsBot)
			return;

		plugin.Links.RequestCode(caller);
	}

	private void OnSkins(CCSPlayerController? caller, CommandInfo info)
	{
		if (Ready(caller) is { } player)
			plugin.Menus.OpenSkins(player);
	}

	private void OnKnife(CCSPlayerController? caller, CommandInfo info)
	{
		if (Ready(caller) is { } player)
			plugin.Menus.OpenKnife(player);
	}

	private void OnGloves(CCSPlayerController? caller, CommandInfo info)
	{
		if (Ready(caller) is { } player)
			plugin.Menus.OpenGloves(player);
	}

	private void OnAgents(CCSPlayerController? caller, CommandInfo info)
	{
		if (Ready(caller) is { } player)
			plugin.Menus.OpenAgents(player);
	}

	private void OnMusic(CCSPlayerController? caller, CommandInfo info)
	{
		if (Ready(caller) is { } player)
			plugin.Menus.OpenMusic(player);
	}

	private void OnPins(CCSPlayerController? caller, CommandInfo info)
	{
		if (Ready(caller) is { } player)
			plugin.Menus.OpenPins(player);
	}

	private void OnStickers(CCSPlayerController? caller, CommandInfo info)
	{
		if (Ready(caller) is not { } player)
			return;

		if (!plugin.Config.Stickers.Enabled)
		{
			plugin.Reply(player, "stickers_disabled");
			return;
		}

		if (!plugin.StickersAllowed(player))
		{
			plugin.Reply(player, "vip_only");
			return;
		}

		var def = HeldWeapon(player, false);
		if (def == 0)
			return;

		if (!plugin.Menus.IsSkinned(player, def))
		{
			plugin.Reply(player, "skin_required");
			return;
		}

		plugin.Menus.OpenStickers(player, def, info.ArgString.Trim().Trim('"'));
	}

	private void OnNameTag(CCSPlayerController? caller, CommandInfo info)
	{
		if (Ready(caller) is not { } player)
			return;

		var def = HeldWeapon(player, true);
		if (def == 0)
			return;

		if (!plugin.AllowCosmeticAction(player))
			return;

		var text = info.ArgString.Trim().Trim('"');
		var tag = text.Length == 0 || text == "-" ? null : text.Length > 64 ? text[..64] : text;
		plugin.Menus.SetNameTag(player, def, tag);
	}

	private void OnStatTrak(CCSPlayerController? caller, CommandInfo info)
	{
		if (Ready(caller) is not { } player)
			return;

		var def = HeldWeapon(player, true);
		if (def == 0)
			return;

		if (!plugin.AllowCosmeticAction(player))
			return;

		plugin.Menus.ToggleStatTrak(player, def);
	}

	private void OnWear(CCSPlayerController? caller, CommandInfo info)
	{
		if (Ready(caller) is not { } player)
			return;

		float? wear = null;
		if (info.ArgCount >= 2)
		{
			if (!float.TryParse(info.GetArg(1), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || value is < 0f or > 1f)
			{
				plugin.Reply(player, "wear_invalid");
				return;
			}

			wear = value;
		}

		var def = HeldWeapon(player, true);
		if (def == 0)
			return;

		if (wear.HasValue && !plugin.AllowCosmeticAction(player))
			return;

		plugin.Menus.OpenWear(player, def, wear);
	}

	private void OnSeed(CCSPlayerController? caller, CommandInfo info)
	{
		if (Ready(caller) is not { } player)
			return;

		int? seed = null;
		if (info.ArgCount >= 2)
		{
			if (!int.TryParse(info.GetArg(1), out var value) || value is < 0 or > 1000)
			{
				plugin.Reply(player, "seed_invalid");
				return;
			}

			seed = value;
		}

		var def = HeldWeapon(player, true);
		if (def == 0)
			return;

		if (seed.HasValue && !plugin.AllowCosmeticAction(player))
			return;

		plugin.Menus.OpenSeed(player, def, seed);
	}

	private void OnGen(CCSPlayerController? caller, CommandInfo info)
	{
		if (Ready(caller) is not { } player)
			return;

		if (!plugin.GenAllowed(player))
		{
			plugin.Reply(player, "gen_vip_only");
			return;
		}

		if (!plugin.AllowGenAction(player))
			return;

		if (!EconItemPreview.TryParse(info.ArgString, out var item))
		{
			var key = info.CallingContext == CommandCallingContext.Chat && info.ArgString.Length >= 120
				? "gen_too_long"
				: "gen_invalid";
			plugin.Reply(player, key);
			return;
		}

		var loadout = plugin.Cache.Get(player);
		if (loadout == null)
			return;

		if (KnifeService.IsKnifeDef(item.DefIndex))
			ApplyGenKnife(player, loadout, item);
		else if (plugin.Catalog.Gloves.Any(g => g.DefIndex == item.DefIndex))
			ApplyGenGloves(player, loadout, item);
		else if (plugin.Catalog.Paints.ContainsKey(item.DefIndex))
			ApplyGenWeapon(player, loadout, item);
		else
			plugin.Reply(player, "gen_unknown_item");
	}

	private void ApplyGenKnife(CCSPlayerController player, PlayerLoadout loadout, EconItemPreview item)
	{
		foreach (var team in PlayerCache.TargetTeams(player))
		{
			var side = loadout.For(team);
			side.Knife = item.DefIndex;

			var entry = side.Equip(item.DefIndex, item.PaintIndex);
			FillEntry(entry, item);
			entry.Wear = Math.Max(entry.Wear, KnifeService.MinimumWear);
			entry.Stickers.Clear();
			entry.Charm = null;
			plugin.Save(plugin.Store.SaveGeneratedKnife(player.SteamID, team, item.DefIndex, entry));
		}

		plugin.Applier.RefreshOwned(player, item.DefIndex);
		plugin.Reply(player, "gen_applied", GenName(item));
	}

	private void ApplyGenGloves(CCSPlayerController player, PlayerLoadout loadout, EconItemPreview item)
	{
		foreach (var team in PlayerCache.TargetTeams(player))
		{
			var side = loadout.For(team);
			side.GloveDef = item.DefIndex;
			side.GlovePaint = item.PaintIndex;
			side.Gloves.Wear = item.PaintWear > 0f ? item.PaintWear : 0.000001f;
			side.Gloves.Seed = item.PaintSeed;
			plugin.Save(plugin.Store.SaveGloves(player.SteamID, team, side));
		}

		plugin.GloveApply.Apply(player, true);
		plugin.Reply(player, "gen_applied", GenName(item));
	}

	private void ApplyGenWeapon(CCSPlayerController player, PlayerLoadout loadout, EconItemPreview item)
	{
		var allowed = plugin.StickersAllowed(player);
		var stripped = !allowed && (item.Stickers.Count > 0 || item.Keychains.Count > 0);
		var stickers = allowed ? MapStickers(item.Stickers) : [];
		var charm = allowed ? MapCharm(item.Keychains) : null;

		foreach (var team in PlayerCache.TargetTeams(player))
		{
			var entry = loadout.For(team).Equip(item.DefIndex, item.PaintIndex);
			FillEntry(entry, item);
			entry.Stickers = stickers.Select(Clone).ToList();
			entry.Charm = charm == null ? null : Clone(charm);
			plugin.Save(plugin.Store.SaveGeneratedWeapon(player.SteamID, team, item.DefIndex, entry));
		}

		plugin.Applier.RefreshOwned(player, item.DefIndex);
		plugin.Reply(player, "gen_applied", GenName(item));
		if (stripped)
			plugin.Reply(player, "gen_stickers_stripped");
	}

	private static void FillEntry(WeaponEntry entry, EconItemPreview item)
	{
		entry.Wear = item.PaintWear > 0f ? item.PaintWear : 0.000001f;
		entry.Seed = item.PaintSeed;
		entry.NameTag = item.CustomName is { Length: > 0 } name ? (name.Length > 64 ? name[..64] : name) : null;
		entry.StatTrak = item.StatTrak ? Math.Max(item.KillEaterValue, 0) : -1;
	}

	private string GenName(EconItemPreview item)
	{
		return plugin.Catalog.FindPaint(item.DefIndex, item.PaintIndex)?.Name ?? plugin.Catalog.WeaponName(item.DefIndex);
	}

	private static List<StickerEntry> MapStickers(List<EconSticker> source)
	{
		const int slots = 6;
		var result = new List<StickerEntry>();
		var used = new bool[slots];
		var nextFree = 0;
		var nextZero = 4;

		foreach (var sticker in source)
		{
			if (sticker.Id <= 0 || sticker.Slot < 0 || sticker.Slot > 31 || result.Count >= 5)
				continue;

			var origin = sticker.Slot;
			var slot = origin;
			var schema = 0;

			if (origin >= slots || used[origin])
			{
				schema = origin;

				if (origin == 0)
				{
					while (nextZero < slots && used[nextZero])
						nextZero++;

					slot = nextZero < slots ? nextZero++ : TakeFreeSlot(used, ref nextFree);
				}
				else
				{
					slot = TakeFreeSlot(used, ref nextFree);
				}

				if (slot < 0)
					continue;
			}
			else if (origin >= 4)
			{
				schema = origin;
			}

			used[slot] = true;
			result.Add(new StickerEntry
			{
				Slot = slot,
				Id = sticker.Id,
				Wear = sticker.Wear,
				Scale = sticker.Scale == 0f ? 1f : sticker.Scale,
				Rotation = sticker.Rotation,
				OffsetX = sticker.OffsetX,
				OffsetY = sticker.OffsetY,
				Schema = schema
			});
		}

		return result;
	}

	private static int TakeFreeSlot(bool[] used, ref int cursor)
	{
		while (cursor < used.Length && used[cursor])
			cursor++;
		return cursor < used.Length ? cursor++ : -1;
	}

	private static CharmEntry? MapCharm(List<EconSticker> keychains)
	{
		var charm = keychains.FirstOrDefault(k => k.Id > 0);
		if (charm == null)
			return null;

		return new CharmEntry
		{
			Id = charm.Id,
			Pattern = charm.Pattern,
			Sticker = charm.Sticker,
			Highlight = charm.Highlight,
			OffsetX = charm.OffsetX,
			OffsetY = charm.OffsetY,
			OffsetZ = charm.OffsetZ
		};
	}

	private static StickerEntry Clone(StickerEntry source) => new()
	{
		Slot = source.Slot,
		Id = source.Id,
		Wear = source.Wear,
		Scale = source.Scale,
		Rotation = source.Rotation,
		OffsetX = source.OffsetX,
		OffsetY = source.OffsetY,
		Schema = source.Schema
	};

	private static CharmEntry Clone(CharmEntry source) => new()
	{
		Id = source.Id,
		Pattern = source.Pattern,
		Sticker = source.Sticker,
		Highlight = source.Highlight,
		OffsetX = source.OffsetX,
		OffsetY = source.OffsetY,
		OffsetZ = source.OffsetZ
	};

	private void OnReload(CCSPlayerController? caller, CommandInfo info)
	{
		if (caller != null)
		{
			if (!caller.IsValid || !AdminManager.PlayerHasPermissions(caller, "@css/root"))
			{
				if (caller.IsValid)
					plugin.Reply(caller, "no_access");
				return;
			}

			plugin.Reply(caller, "reloading");
		}

		var slot = caller?.Slot ?? -1;
		Task.Run(async () =>
		{
			var loaded = false;
			try
			{
				await plugin.Catalog.LoadAsync(plugin.Config.Api, plugin.Db.Configured);
				loaded = plugin.Catalog.Loaded;
			}
			catch (Exception ex)
			{
				plugin.Logger.LogError("Item data reload failed: {Error}", ex.Message);
			}

			Server.NextFrame(() =>
			{
				plugin.Menus.InvalidateCaches();
				plugin.Menus.Prewarm();
				var player = slot >= 0 ? Utilities.GetPlayerFromSlot(slot) : null;
				if (player != null && player.IsValid)
					plugin.Reply(player, loaded ? "reloaded" : "reload_failed");
				else
					plugin.Logger.LogInformation(loaded ? "Item data reloaded" : "Item data reload failed");
			});
		});
	}

	private int HeldWeapon(CCSPlayerController player, bool allowKnife)
	{
		var def = HeldDef(player, allowKnife);
		if (def == 0)
			plugin.Reply(player, "no_weapon");
		return def;
	}

	private int HeldDef(CCSPlayerController player, bool allowKnife)
	{
		var weapon = player.PlayerPawn.Value?.WeaponServices?.ActiveWeapon.Value;
		if (weapon == null || !weapon.IsValid)
			return 0;

		var def = (int)weapon.AttributeManager.Item.ItemDefinitionIndex;
		if (KnifeService.IsKnifeClass(weapon.DesignerName))
			return allowKnife ? def : 0;

		return plugin.Catalog.Paints.ContainsKey(def) ? def : 0;
	}
}
