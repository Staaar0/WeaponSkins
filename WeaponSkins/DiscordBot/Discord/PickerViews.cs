using System.Globalization;
using System.Collections.Concurrent;
using Discord;
using WeaponSkinsBot.Catalog;
using WeaponSkinsBot.Database;

namespace WeaponSkinsBot.Discord;

public sealed partial class PickerService
{
    private static readonly string[] Categories = ["Rifles", "Pistols", "SMG", "Heavy", "Knifes", "Gloves"];
    private static readonly (string Label, string Value)[] WearOptions = [
        ("Factory New", "0.02"), ("Minimal Wear", "0.08"), ("Field-Tested", "0.16"),
        ("Well-Worn", "0.38"), ("Battle-Scarred", "0.45")
    ];
    private string CanonicalCategory(string value) => Categories.FirstOrDefault(category => string.Equals(CategoryLabel(category), value.Trim(), StringComparison.OrdinalIgnoreCase)) ?? value.Trim().ToLowerInvariant() switch
    {
        "snipers" or "rifles" => "Rifles", "pistols" or "other" or "equipment" => "Pistols",
        "smg" or "smgs" => "SMG", "knifes" or "knife" or "knives" => "Knifes",
        "gloves" => "Gloves", "heavy" => "Heavy", _ => value.Trim()
    };
    private static string Short(string text, int max = 100) => DiscordText.Short(text, max);
    private static string Inline(string text) => text.Replace('`', 'ˋ');
    private string TeamLabel(TeamTarget team) => team switch {
        TeamTarget.Terrorist => T("discord.team_t"), TeamTarget.CounterTerrorist => T("discord.team_ct"), _ => T("discord.team_both")
    };
    private string WearName(float wear) => T(wear switch {
        < .07f => "menu.wear_fn", < .15f => "menu.wear_mw", < .38f => "menu.wear_ft", < .45f => "menu.wear_ww", _ => "menu.wear_bs"
    });
    private string StatTrakText(Session s) => s.StatTrak ? T("menu.on") + " :green_circle:" : T("menu.off") + " :red_circle:";
    private string PaintName(Session s) => string.IsNullOrEmpty(s.Paint?.DisplayName)
        ? catalog.WeaponName(s.DefIndex) + " | " + s.Paint?.Name : s.Paint.DisplayName;
    private static float EffectiveWear(Session s, float value)
    {
        var floor = s.Kind == ItemKind.Knife ? .01f : .000001f;
        if (s.Paint != null) floor = Math.Min(s.Paint.MaxFloat, Math.Max(s.Paint.MinFloat, floor));
        return Math.Clamp(value, floor, s.Paint?.MaxFloat ?? 1);
    }
    private static bool Searchable(Session s) => s.Stage is Stage.Agents or Stage.Music or Stage.Pins or Stage.Stickers;
    private string CategoryLabel(string category) => category switch
    {
        "Knifes" => T("menu.knife"), "Gloves" => T("menu.gloves"),
        "Rifles" => T("category.rifles"), "Pistols" => T("category.pistols"),
        "SMG" => T("category.smgs"), "Heavy" => T("category.heavy"), _ => category
    };
    private string StageLabel(Session s) => s.Stage switch
    {
        Stage.Categories => T("discord.categories"), Stage.Weapons => T("discord.weapons"),
        Stage.Paints or Stage.Preview => T("menu.skins"), Stage.Agents => T("menu.agents"),
        Stage.Music => T("menu.music"), Stage.Pins => T("menu.pins"),
        Stage.Slots => T("discord.sticker_slots"), Stage.Stickers => T("menu.stickers"),
        Stage.Unlink => T("discord.unlink"), _ => "WeaponSkins"
    };
    private static string Id(Session s, string action) => $"pick:{s.Id}:{s.Revision}:{action}";
    private static Emoji? RarityEmoji(string color) => color.ToLowerInvariant() switch {
        "#b0c3d9" => new Emoji("⚪"), "#5e98d9" or "#4b69ff" => new Emoji("🔵"),
        "#8847ff" or "#d32ce6" => new Emoji("🟣"), "#eb4b4b" => new Emoji("🔴"),
        "#e4ae39" => new Emoji("🟠"), _ => null
    };
    private static Embed Embed(string title, string text, string color = "#ffffff", string image = "", bool thumbnail = true)
    {
        var builder = new EmbedBuilder().WithDescription(string.IsNullOrEmpty(text) ? "> " : text);
        if (!string.IsNullOrEmpty(title)) builder.WithTitle(Short(title, 256));
        builder.WithColor(uint.TryParse(color.TrimStart('#'), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb) ? new Color(rgb) : Color.LightGrey);
        if (Uri.TryCreate(image, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http")
        {
            if (thumbnail) builder.WithThumbnailUrl(image); else builder.WithImageUrl(image);
        }
        return builder.Build();
    }
    private static void Button(ComponentBuilder builder, Session s, string action, string label, ButtonStyle style = ButtonStyle.Primary, string? emoji = null, bool disabled = false) =>
        builder.WithButton(Short(label, 80), Id(s, action), style, emote: emoji == null ? null : Emoji.Parse(emoji), disabled: disabled, row: 0);

    private readonly ConcurrentDictionary<string, List<Choice>> catalogueChoices = new();

    private List<Choice> Choices(Session s)
    {
        var key = $"{s.Stage}|{s.Category}|{s.DefIndex}|{s.Team}|{s.Filter}";
        if (s.CacheKey == key) return s.Cached;
        // Share the full catalog; keep search results per menu.
        var catalogueKey = s.Stage switch {
            Stage.Weapons => $"weapons|{s.Category}|{s.Team}", Stage.Paints => $"paints|{s.DefIndex}",
            Stage.Agents => $"agents|{s.Team}", _ => s.Stage.ToString()
        };
        var items = catalogueChoices.GetOrAdd(catalogueKey, _ => BuildChoices(s));
        s.CacheKey = key;
        s.Cached = s.Filter.Length == 0 ? items : items.Where(x =>
            x.Label.Contains(s.Filter, StringComparison.OrdinalIgnoreCase) || x.Value == s.Filter).ToList();
        return s.Cached;
    }

    private List<Choice> BuildChoices(Session s)
    {
        IEnumerable<Choice> items = s.Stage switch {
            Stage.Categories => catalog.MenuCategories.Where(Categories.Contains).Select(x => new Choice(x, CategoryLabel(x))),
            Stage.Weapons when s.Category == "Knifes" => catalog.Knives.OrderBy(x => catalog.MenuWeaponOrder.GetValueOrDefault(x.DefIndex)).Select(x => new Choice(x.DefIndex.ToString(), "★ " + x.Name)),
            Stage.Weapons when s.Category == "Gloves" => catalog.Gloves.OrderBy(x => catalog.MenuWeaponOrder.GetValueOrDefault(x.DefIndex)).Select(x => new Choice(x.DefIndex.ToString(), "★ " + x.Name)),
            Stage.Weapons => catalog.WeaponsByCategory.Where(x => CanonicalCategory(x.Key) == s.Category).SelectMany(x => x.Value)
                .Where(x => s.Team == TeamTarget.Both || x.Team == TeamTarget.Both || x.Team == s.Team)
                .OrderBy(x => catalog.MenuWeaponOrder.GetValueOrDefault(x.DefIndex)).Select(x => new Choice(x.DefIndex.ToString(), x.Name)),
            Stage.Paints => (catalog.Paints.GetValueOrDefault(s.DefIndex) ?? []).Select(x => new Choice(x.Paint.ToString(),
                string.IsNullOrEmpty(x.DisplayName) ? catalog.WeaponName(s.DefIndex) + " | " + x.Name : x.DisplayName, x.Image, x.RarityColor)),
            Stage.Agents => (s.Team == TeamTarget.CounterTerrorist ? catalog.AgentsCT : catalog.AgentsT)
                // Use indexes to stay within Discord's 100-character value limit.
                .Select((x, i) => new Choice("agent_" + i, string.IsNullOrEmpty(x.DisplayName) ? x.Name : x.DisplayName, x.Image, x.RarityColor)),
            Stage.Music => catalog.MusicKits.Select(x => new Choice(x.Id.ToString(), x.Name, x.Image)),
            Stage.Pins => catalog.Pins.Select(x => new Choice(x.Id.ToString(), x.Name, x.Image)),
            Stage.Slots => Enumerable.Range(1, StickerSlots).Select(x => new Choice(x.ToString(), T("menu.sticker_slot", x))).Append(new Choice("all", T("menu.set_all"))),
            Stage.Stickers => catalog.Stickers.Select(x => new Choice(x.Id.ToString(), x.Name, x.Image)),
            _ => []
        };
        return items.ToList();
    }

    private PickerView Render(Session s)
    {
        var components = new ComponentBuilder();
        if (s.Stage == Stage.Unlink)
        {
            Button(components, s, "unlink", T("discord.unlink"), ButtonStyle.Danger);
            Button(components, s, "cancel", T("discord.cancel"), ButtonStyle.Secondary);
            return new(Embed(T("discord.unlink_title"), T("discord.unlink_confirm", s.SteamId)), components.Build());
        }
        if (s.Stage == Stage.Preview)
        {
            Button(components, s, "back", T("discord.back"), ButtonStyle.Danger, ":back:");
            Button(components, s, "seed", T("discord.seed_pattern"), emoji: ":pencil:");
            Button(components, s, "tag", T("menu.nametag"), emoji: ":card_index:", disabled: s.Kind == ItemKind.Glove);
            Button(components, s, "st", T("menu.stattrak"), emoji: ":bar_chart:", disabled: s.Kind == ItemKind.Glove);
            Button(components, s, "save", T("discord.save"), ButtonStyle.Success, ":pushpin:");
            var wearMenu = new SelectMenuBuilder().WithCustomId(Id(s, "wear")).WithPlaceholder(Short(T("discord.wear_placeholder")));
            foreach (var option in WearOptions) wearMenu.AddOption(Short(WearName(float.Parse(option.Value, CultureInfo.InvariantCulture))), option.Value);
            wearMenu.AddOption(Short(T("discord.custom_float")), "custom");
            components.WithSelectMenu(wearMenu, row: 1);
            var text = T("discord.preview_details", WearName(s.Wear), s.Wear.ToString("0.######", CultureInfo.InvariantCulture), s.Seed, s.Paint?.Rarity ?? "", StatTrakText(s), Inline(s.NameTag.Length == 0 ? T("discord.not_set") : s.NameTag));
            return new(Embed(PaintName(s), text, s.Paint?.RarityColor ?? "#ffffff", s.Paint?.Image ?? ""), components.Build());
        }
        var choices = Choices(s);
        int pages = Math.Max(1, (choices.Count + PageSize - 1) / PageSize);
        s.Page = Math.Clamp(s.Page, 0, pages - 1);
        if (s.Stage == Stage.Paints)
        {
            Button(components, s, "back", T("discord.back"), ButtonStyle.Danger, ":back:");
            Button(components, s, "default", T("menu.default"), ButtonStyle.Secondary);
        }
        else if (s.Stage == Stage.Weapons)
        {
            if (s.Category is "Knifes" or "Gloves") Button(components, s, "default", T("menu.default") + " " + CategoryLabel(s.Category), ButtonStyle.Secondary);
        }
        else
        {
            if (Searchable(s))
            {
                Button(components, s, "search", T("discord.search"));
                Button(components, s, "default", s.Stage == Stage.Stickers ? T("discord.remove_selected") : T("menu.default"), ButtonStyle.Secondary);
                if (s.Stage == Stage.Stickers) Button(components, s, "clear", T("discord.remove_decorations"), ButtonStyle.Danger);
            }
            Button(components, s, "cancel", T("discord.back"), ButtonStyle.Danger, ":back:");
        }
        if (choices.Count > 0)
        {
            var placeholder = s.Stage switch {
                Stage.Weapons => T("discord.weapon_placeholder", CategoryLabel(s.Category)), Stage.Paints => T("discord.skin_placeholder"),
                Stage.Agents => T("discord.agent_placeholder"), _ => T("discord.select_stage", StageLabel(s))
            };
            var menu = new SelectMenuBuilder().WithCustomId(Id(s, "select")).WithPlaceholder(Short(placeholder));
            if (s.Stage == Stage.Paints && s.Page < pages - 1) menu.AddOption(Short(T("discord.next_page")), "__next", emote: Emoji.Parse(":arrow_right:"));
            if (s.Page > 0) menu.AddOption(Short(T("discord.previous_page")), "__prev", emote: Emoji.Parse(":arrow_left:"));
            foreach (var choice in choices.Skip(s.Page * PageSize).Take(PageSize))
                menu.AddOption(Short(choice.Label), choice.Value, Short(T("discord.click_select")), RarityEmoji(choice.Rarity));
            if (s.Stage != Stage.Paints && s.Page < pages - 1) menu.AddOption(Short(T("discord.next_page")), "__next", emote: Emoji.Parse(":arrow_right:"));
            // Show buttons above the menu, like Discord Utilities.
            var hasButtons = s.Stage != Stage.Weapons || s.Category is "Knifes" or "Gloves";
            components.WithSelectMenu(menu, row: hasButtons ? 1 : 0);
        }
        if (s.Stage == Stage.Weapons && choices.Count > 0) return new(null, components.Build());
        if (s.Stage == Stage.Paints)
            return new(Embed((s.Kind is ItemKind.Knife or ItemKind.Glove ? "★ " : "") + catalog.WeaponName(s.DefIndex), T("discord.choose_skin_prompt"), image: s.Kind == ItemKind.Glove ? "" : catalog.WeaponImages.GetValueOrDefault(s.DefIndex, "")), components.Build());
        var title = s.Stage switch {
            Stage.Agents => T("menu.agents") + " — " + (s.Team == TeamTarget.CounterTerrorist ? "CT" : "T"),
            Stage.Stickers => catalog.WeaponName(s.DefIndex) + " — " + T("menu.stickers"), _ => StageLabel(s)
        };
        var description = (choices.Count == 0 ? T("discord.no_matches") : T("discord.click_select")) + "\n> " + T("discord.page", s.Page + 1, pages);
        return new(Embed(title, "> " + description), components.Build());
    }

    private Modal BuildModal(Session s, string action)
    {
        if (action == "search" && !Searchable(s) || action != "search" && s.Stage != Stage.Preview)
            throw new InvalidOperationException(T("discord.control_unavailable"));
        var (title, label, placeholder, value, max, required) = action switch {
            "search" => (T("discord.search_title", StageLabel(s)), T("discord.name_or_id"), "", s.Filter, 100, false),
            "seed" => (T("discord.seed_title"), T("discord.seed_pattern"), T("discord.seed_range"), s.Seed.ToString(), 4, true),
            "tag" => (T("discord.nametag_title"), T("menu.nametag"), T("discord.empty_remove"), s.NameTag, 64, false),
            _ => (T("discord.wear_title"), T("discord.float"), T("discord.wear_range"), s.Wear.ToString("0.######", CultureInfo.InvariantCulture), 16, true)
        };
        return new ModalBuilder().WithTitle(Short(title, 45)).WithCustomId(Id(s, action))
            .AddTextInput(Short(label, 45), "value", placeholder: placeholder.Length == 0 ? null : Short(placeholder),
                value: value.Length == 0 ? null : value, maxLength: max, required: required).Build();
    }
}
