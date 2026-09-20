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
    private static string CanonicalCategory(string value) => value.Trim().ToLowerInvariant() switch
    {
        "snipers" or "rifles" => "Rifles", "pistols" or "other" or "equipment" => "Pistols",
        "smg" or "smgs" => "SMG", "knifes" or "knife" or "knives" => "Knifes",
        "gloves" => "Gloves", "heavy" => "Heavy", _ => value.Trim()
    };
    private static string Short(string text, int max = 100) => text.Length <= max ? text : text[..(max - 1)] + "…";
    private static string Inline(string text) => text.Replace('`', 'ˋ');
    private static string TeamLabel(TeamTarget team) => team switch {
        TeamTarget.Terrorist => "Terrorist", TeamTarget.CounterTerrorist => "Counter-Terrorist", _ => "Both"
    };
    private static string WearName(float wear) => wear switch {
        < .07f => "Factory New", < .15f => "Minimal Wear", < .38f => "Field-Tested", < .45f => "Well-Worn", _ => "Battle-Scarred"
    };
    private static string StatTrakText(Session s) => s.StatTrak ? "ON :green_circle:" : "OFF :red_circle:";
    private string PaintName(Session s) => string.IsNullOrEmpty(s.Paint?.DisplayName)
        ? catalog.WeaponName(s.DefIndex) + " | " + s.Paint?.Name : s.Paint.DisplayName;
    private static float EffectiveWear(Session s, float value)
    {
        var floor = s.Kind == ItemKind.Knife ? .01f : .000001f;
        if (s.Paint != null) floor = Math.Min(s.Paint.MaxFloat, Math.Max(s.Paint.MinFloat, floor));
        return Math.Clamp(value, floor, s.Paint?.MaxFloat ?? 1);
    }
    private static bool Searchable(Session s) => s.Stage is Stage.Agents or Stage.Music or Stage.Pins or Stage.Stickers;
    private static string StageName(Session s) => s.Stage.ToString().ToLowerInvariant();
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
        builder.WithButton(label, Id(s, action), style, emote: emoji == null ? null : Emoji.Parse(emoji), disabled: disabled, row: 0);

    private readonly ConcurrentDictionary<string, List<Choice>> catalogueChoices = new();

    private List<Choice> Choices(Session s)
    {
        var key = $"{s.Stage}|{s.Category}|{s.DefIndex}|{s.Team}|{s.Filter}";
        if (s.CacheKey == key) return s.Cached;
        // Share immutable unfiltered catalogue choices across players. Only search results belong to a session.
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
            Stage.Categories => catalog.MenuCategories.Where(Categories.Contains).Select(x => new Choice(x, x)),
            Stage.Weapons when s.Category == "Knifes" => catalog.Knives.OrderBy(x => catalog.MenuWeaponOrder.GetValueOrDefault(x.DefIndex)).Select(x => new Choice(x.DefIndex.ToString(), "★ " + x.Name)),
            Stage.Weapons when s.Category == "Gloves" => catalog.Gloves.OrderBy(x => catalog.MenuWeaponOrder.GetValueOrDefault(x.DefIndex)).Select(x => new Choice(x.DefIndex.ToString(), "★ " + x.Name)),
            Stage.Weapons => catalog.WeaponsByCategory.Where(x => CanonicalCategory(x.Key) == s.Category).SelectMany(x => x.Value)
                .Where(x => s.Team == TeamTarget.Both || x.Team == TeamTarget.Both || x.Team == s.Team)
                .OrderBy(x => catalog.MenuWeaponOrder.GetValueOrDefault(x.DefIndex)).Select(x => new Choice(x.DefIndex.ToString(), x.Name)),
            Stage.Paints => (catalog.Paints.GetValueOrDefault(s.DefIndex) ?? []).Select(x => new Choice(x.Paint.ToString(),
                string.IsNullOrEmpty(x.DisplayName) ? catalog.WeaponName(s.DefIndex) + " | " + x.Name : x.DisplayName, x.Image, x.RarityColor)),
            Stage.Agents => (s.Team == TeamTarget.CounterTerrorist ? catalog.AgentsCT : catalog.AgentsT)
                // Values are stable indexes; long agent model paths never exceed Discord's 100-character option limit.
                .Select((x, i) => new Choice("agent_" + i, string.IsNullOrEmpty(x.DisplayName) ? x.Name : x.DisplayName, x.Image, x.RarityColor)),
            Stage.Music => catalog.MusicKits.Select(x => new Choice(x.Id.ToString(), x.Name, x.Image)),
            Stage.Pins => catalog.Pins.Select(x => new Choice(x.Id.ToString(), x.Name, x.Image)),
            Stage.Slots => Enumerable.Range(1, StickerSlots).Select(x => new Choice(x.ToString(), "Slot " + x)).Append(new Choice("all", "All four slots")),
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
            Button(components, s, "unlink", "Unlink", ButtonStyle.Danger);
            Button(components, s, "cancel", "Cancel", ButtonStyle.Secondary);
            return new(Embed("Unlink Steam account", $"> Unlink SteamID {s.SteamId} from WeaponSkins?"), components.Build());
        }
        if (s.Stage == Stage.Preview)
        {
            Button(components, s, "back", "Back", ButtonStyle.Danger, ":back:");
            Button(components, s, "seed", "Seed / Pattern", emoji: ":pencil:");
            Button(components, s, "tag", "Nametag", emoji: ":card_index:", disabled: s.Kind == ItemKind.Glove);
            Button(components, s, "st", "Stattrak", emoji: ":bar_chart:", disabled: s.Kind == ItemKind.Glove);
            Button(components, s, "save", "Save", ButtonStyle.Success, ":pushpin:");
            var wearMenu = new SelectMenuBuilder().WithCustomId(Id(s, "wear")).WithPlaceholder("Select a skin wear");
            foreach (var option in WearOptions) wearMenu.AddOption(option.Label, option.Value);
            wearMenu.AddOption("Custom Float", "custom");
            components.WithSelectMenu(wearMenu, row: 1);
            var text = $"> Wear: `{WearName(s.Wear)}` ({s.Wear.ToString("0.######", CultureInfo.InvariantCulture)})\n" +
                $"> Seed / Pattern: `{s.Seed}`\n> Rarity: `{s.Paint?.Rarity}`\n> Stattrak: {StatTrakText(s)}\n> Nametag: `{Inline(s.NameTag.Length == 0 ? "Not Set" : s.NameTag)}`";
            return new(Embed(PaintName(s), text, s.Paint?.RarityColor ?? "#ffffff", s.Paint?.Image ?? ""), components.Build());
        }
        var choices = Choices(s);
        int pages = Math.Max(1, (choices.Count + PageSize - 1) / PageSize);
        s.Page = Math.Clamp(s.Page, 0, pages - 1);
        if (s.Stage == Stage.Paints)
        {
            Button(components, s, "back", "Back", ButtonStyle.Danger, ":back:");
            Button(components, s, "default", "Default", ButtonStyle.Secondary);
        }
        else if (s.Stage == Stage.Weapons)
        {
            if (s.Category is "Knifes" or "Gloves") Button(components, s, "default", "Default " + s.Category, ButtonStyle.Secondary);
        }
        else
        {
            if (Searchable(s))
            {
                Button(components, s, "search", "Search");
                Button(components, s, "default", s.Stage == Stage.Stickers ? "Remove from selected slots" : "Default", ButtonStyle.Secondary);
                if (s.Stage == Stage.Stickers) Button(components, s, "clear", "Remove all stickers and charm", ButtonStyle.Danger);
            }
            Button(components, s, "cancel", "Back", ButtonStyle.Danger, ":back:");
        }
        if (choices.Count > 0)
        {
            var placeholder = s.Stage switch {
                Stage.Weapons => "Select a weapon (" + s.Category + ")", Stage.Paints => "Select a skin",
                Stage.Agents => "Select a agent", _ => "Select " + StageName(s)
            };
            var menu = new SelectMenuBuilder().WithCustomId(Id(s, "select")).WithPlaceholder(Short(placeholder));
            if (s.Stage == Stage.Paints && s.Page < pages - 1) menu.AddOption("Next Page", "__next", emote: Emoji.Parse(":arrow_right:"));
            if (s.Page > 0) menu.AddOption("Previous Page", "__prev", emote: Emoji.Parse(":arrow_left:"));
            foreach (var choice in choices.Skip(s.Page * PageSize).Take(PageSize))
                menu.AddOption(Short(choice.Label), choice.Value, "Click to select", RarityEmoji(choice.Rarity));
            if (s.Stage != Stage.Paints && s.Page < pages - 1) menu.AddOption("Next Page", "__next", emote: Emoji.Parse(":arrow_right:"));
            // Discord Utilities puts buttons above the selection menu.
            var hasButtons = s.Stage != Stage.Weapons || s.Category is "Knifes" or "Gloves";
            components.WithSelectMenu(menu, row: hasButtons ? 1 : 0);
        }
        if (s.Stage == Stage.Weapons && choices.Count > 0) return new(null, components.Build());
        if (s.Stage == Stage.Paints)
            return new(Embed((s.Kind is ItemKind.Knife or ItemKind.Glove ? "★ " : "") + catalog.WeaponName(s.DefIndex), "> Please choose a skin", image: s.Kind == ItemKind.Glove ? "" : catalog.WeaponImages.GetValueOrDefault(s.DefIndex, "")), components.Build());
        var title = s.Stage switch {
            Stage.Agents => "Agents — " + (s.Team == TeamTarget.CounterTerrorist ? "CT" : "T"),
            Stage.Stickers => catalog.WeaponName(s.DefIndex) + " — stickers", _ => s.Stage.ToString()
        };
        var description = (choices.Count == 0 ? "No matches. Use Search to change the filter." : "Click to select") + $"\n> Page {s.Page + 1}/{pages}";
        return new(Embed(title, "> " + description), components.Build());
    }

    private static Modal BuildModal(Session s, string action)
    {
        if (action == "search" && !Searchable(s) || action != "search" && s.Stage != Stage.Preview)
            throw new InvalidOperationException("This control is no longer available.");
        var (title, label, placeholder, value, max, required) = action switch {
            "search" => ("Search " + StageName(s), "Name or item ID", "", s.Filter, 100, false),
            "seed" => ("Change a seed / pattern", "Seed / Pattern", "Min. 0 and Max. 1000", s.Seed.ToString(), 4, true),
            "tag" => ("Change a nametag", "Nametag", "Leave empty to remove", s.NameTag, 64, false),
            _ => ("Change a skin wear", "Float", "Min. 0 and Max. 1 (skin limits apply)", s.Wear.ToString("0.######", CultureInfo.InvariantCulture), 16, true)
        };
        return new ModalBuilder().WithTitle(title).WithCustomId(Id(s, action))
            .AddTextInput(label, "value", placeholder: placeholder.Length == 0 ? null : placeholder,
                value: value.Length == 0 ? null : value, maxLength: max, required: required).Build();
    }
}
