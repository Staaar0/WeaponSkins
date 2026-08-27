using System.Globalization;
using Discord;
using Discord.WebSocket;
using WeaponSkinsBot.Catalog;
using WeaponSkinsBot.Database;

namespace WeaponSkinsBot.Discord;

public sealed record PickerView(Embed Embed, MessageComponent Components);

public sealed class PickerService
{
    private enum Stage
    {
        SkinCategory,
        SkinWeapon,
        SkinPaint,
        Knife,
        KnifePaint,
        Glove,
        GlovePaint,
        AgentT,
        AgentCT,
        Music,
        Pin,
        Pattern,
        Wear,
        StatTrak,
        StickerSlot,
        StickerPick
    }

    private sealed class Session
    {
        public required string Id;
        public required ulong GuildId;
        public required ulong UserId;
        public required ulong SteamId;
        public required TeamTarget Team;
        public required Stage Stage;
        public string Category = "";
        public string Filter = "";
        public int DefIndex;
        public int Slot;
        public int Page;
        public ItemKind Kind = ItemKind.Weapon;
        public PaintDef? Paint;
        public int Seed;
        public float? Wear;
        public bool StatTrak;
        public bool TeamFixed;
        public bool ClearStickers;
        public Dictionary<int, int> Stickers = [];
        public DateTimeOffset LastUsed = DateTimeOffset.UtcNow;
        public string CacheKey = "";
        public List<Choice> Cached = [];
    }

    private sealed record Choice(string Value, string Label, string? Description = null, string? Image = null);

    private const int PageSize = 25;

    /// <summary>The plugin paints four sticker slots, the fifth only exists for gen codes.</summary>
    public const int StickerSlots = WeaponSkinsDatabase.ManagedStickerSlots;

    private const int AllSlots = -1;

	private const int SessionMinutes = 30;

	private readonly CatalogService catalog;
	private readonly WeaponSkinsDatabase database;
	private readonly Dictionary<string, Session> sessions = [];
	private readonly object sync = new();

	public PickerService(CatalogService catalog, WeaponSkinsDatabase database)
	{
		this.catalog = catalog;
		this.database = database;
    }

    public PickerView StartSkins(ulong guildId, ulong userId, ulong steamId, TeamTarget team, string category)
    {
        var actual = string.IsNullOrWhiteSpace(category)
            ? null
            : catalog.Categories.FirstOrDefault(item => string.Equals(item, category, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(category) && actual == null)
            throw new InvalidOperationException($"Unknown category. Use: {string.Join(", ", catalog.Categories)}");

        return Start(new Session
        {
            Id = NewId(), GuildId = guildId, UserId = userId, SteamId = steamId, Team = team,
            Stage = actual == null ? Stage.SkinCategory : Stage.SkinWeapon,
            Category = actual ?? ""
        });
    }

    public PickerView StartKnife(ulong guildId, ulong userId, ulong steamId, TeamTarget team, KnifeDef? knife)
    {
        return Start(new Session
        {
            Id = NewId(), GuildId = guildId, UserId = userId, SteamId = steamId, Team = team,
            Stage = knife == null ? Stage.Knife : Stage.KnifePaint,
            Kind = ItemKind.Knife,
            DefIndex = knife?.DefIndex ?? 0
        });
    }

    public PickerView StartGloves(ulong guildId, ulong userId, ulong steamId, TeamTarget team)
    {
        return Start(new Session
        {
            Id = NewId(), GuildId = guildId, UserId = userId, SteamId = steamId, Team = team,
            Stage = Stage.Glove, Kind = ItemKind.Glove
        });
    }

    public PickerView StartAgents(ulong guildId, ulong userId, ulong steamId, TeamTarget team)
    {
        return Start(new Session
        {
            Id = NewId(), GuildId = guildId, UserId = userId, SteamId = steamId, Team = team,
            Stage = team == TeamTarget.CounterTerrorist ? Stage.AgentCT : Stage.AgentT
        });
    }

    public PickerView StartMusic(ulong guildId, ulong userId, ulong steamId)
    {
        return Start(new Session
        {
            Id = NewId(), GuildId = guildId, UserId = userId, SteamId = steamId, Team = TeamTarget.Both, Stage = Stage.Music
        });
    }

    public PickerView StartPins(ulong guildId, ulong userId, ulong steamId)
    {
        return Start(new Session
        {
            Id = NewId(), GuildId = guildId, UserId = userId, SteamId = steamId, Team = TeamTarget.Both, Stage = Stage.Pin
        });
    }

    public PickerView StartStickers(ulong guildId, ulong userId, ulong steamId, TeamTarget team, int defIndex, int slot, string filter)
    {
        var fixedTeam = catalog.TeamOf(defIndex);
        return Start(new Session
        {
            Id = NewId(), GuildId = guildId, UserId = userId, SteamId = steamId,
            Team = fixedTeam == TeamTarget.Both ? team : fixedTeam,
            TeamFixed = fixedTeam != TeamTarget.Both,
            Stage = Stage.StickerPick, DefIndex = defIndex, Slot = slot, Filter = filter.Trim()
        });
    }

    public async Task HandleSelectAsync(SocketMessageComponent component)
    {
        if (!Mine(component.Data.CustomId))
            return;

		if (!TryGet(component.Data.CustomId, component.GuildId ?? 0, component.User.Id, out var session, out var action) || action != "select")
        {
            await Expired(component);
            return;
        }

        var value = component.Data.Values.FirstOrDefault();
        if (value == null)
            return;

        // Anything that needs typing opens a modal instead of a menu.
        if (value == "ask:pattern")
        {
            await component.RespondWithModalAsync(ValueModal(session, "pattern", "Pattern seed", "0 - 1000", session.Seed.ToString()));
            return;
        }
        if (value == "ask:wear")
        {
            await component.RespondWithModalAsync(ValueModal(session, "wear", "Wear", "0.00 - 1.00", "0.15"));
            return;
        }

        // Saving talks to MySQL, which can be slower than the three seconds
        // Discord allows for a first answer, so acknowledge before working.
        await component.DeferAsync();

        try
        {
            // Moving to a new list always starts at its first page.
            var before = session.Stage;
            var completed = await Apply(session, value);
            if (session.Stage != before)
                session.Page = 0;
            await Render(component, session, completed);
        }
        catch (Exception ex)
        {
            await component.FollowupAsync($"WeaponSkins database error: {Safe(ex.Message)}", ephemeral: true);
        }
    }

    public async Task HandleButtonAsync(SocketMessageComponent component)
    {
        if (!Mine(component.Data.CustomId))
            return;

		if (!TryGet(component.Data.CustomId, component.GuildId ?? 0, component.User.Id, out var session, out var action))
        {
            await Expired(component);
            return;
        }

        if (action == "search")
        {
            await component.RespondWithModalAsync(SearchModal(session));
            return;
        }

        if (action == "cancel")
        {
            Remove(session.Id);
            await component.UpdateAsync(message =>
            {
                message.Embed = new EmbedBuilder().WithTitle("WeaponSkins").WithDescription("Selection cancelled.").Build();
                message.Components = new ComponentBuilder().Build();
            });
            return;
        }

        await component.DeferAsync();

        if (action == "clear")
        {
            session.Filter = "";
            session.Page = 0;
        }
        else if (action == "prev")
        {
            session.Page = Math.Max(0, session.Page - 1);
        }
        else if (action == "next")
        {
            session.Page++;
        }
        else
        {
            return;
        }

        var paged = Build(session);
        await component.ModifyOriginalResponseAsync(message =>
        {
            message.Embed = paged.Embed;
            message.Components = paged.Components;
        });
    }

    private static bool Mine(string customId) => customId.StartsWith("pick:", StringComparison.Ordinal);

    private static async Task Expired(SocketMessageComponent component)
    {
        await component.RespondAsync(
            $"That menu is closed. It stays open for {SessionMinutes} minutes after your last click, run the command again.",
            ephemeral: true);
    }

    /// <summary>Handles the search box and the typed pattern/wear values.</summary>
    public async Task<bool> HandleModalAsync(SocketModal modal)
    {
        var parts = modal.Data.CustomId.Split(':', 3);
        if (parts.Length != 3 || parts[0] != "pickmodal")
            return false;

        Session session;
        lock (sync)
        {
			if (!sessions.TryGetValue(parts[1], out session!) || session.GuildId != (modal.GuildId ?? 0) || session.UserId != modal.User.Id)
                return true;
        }

        var text = modal.Data.Components.FirstOrDefault(item => item.CustomId == "value")?.Value?.Trim() ?? "";

        try
        {
            Embed? completed = null;
            switch (parts[2])
            {
                case "search":
                    session.Filter = text;
                    session.Page = 0;
                    break;

                case "pattern":
                    if (!int.TryParse(text, out var seed) || seed is < 0 or > 1000)
                    {
                        await modal.RespondAsync("Pattern seed must be a whole number between 0 and 1000.", ephemeral: true);
                        return true;
                    }
                    session.Seed = seed;
                    session.Stage = Stage.Wear;
                    session.Page = 0;
                    break;

                case "wear":
                {
                    if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var wear) || wear is < 0f or > 1f)
                    {
                        await modal.RespondAsync("Wear must be a number between 0.00 and 1.00.", ephemeral: true);
                        return true;
                    }

                    session.Wear = wear;
                    session.Page = 0;

                    // Gloves save at this point, so answer Discord before the write.
                    await modal.UpdateAsync(message =>
                    {
                        message.Embed = new EmbedBuilder().WithTitle("WeaponSkins").WithDescription("Saving…").Build();
                        message.Components = new ComponentBuilder().Build();
                    });

                    var saved = await AfterWear(session);
                    if (saved != null)
                        Remove(session.Id);

                    var next = saved == null ? Build(session) : new PickerView(saved, new ComponentBuilder().Build());
                    await modal.ModifyOriginalResponseAsync(message =>
                    {
                        message.Embed = next.Embed;
                        message.Components = next.Components;
                    });
                    return true;
                }

                default:
                    return true;
            }

            if (completed != null)
            {
                Remove(session.Id);
                await modal.UpdateAsync(message =>
                {
                    message.Embed = completed;
                    message.Components = new ComponentBuilder().Build();
                });
                return true;
            }

            var view = Build(session);
            await modal.UpdateAsync(message =>
            {
                message.Embed = view.Embed;
                message.Components = view.Components;
            });
        }
        catch (Exception ex)
        {
            await modal.RespondAsync($"WeaponSkins database error: {Safe(ex.Message)}", ephemeral: true);
        }

        return true;
    }

    private async Task Render(SocketMessageComponent component, Session session, Embed? completed)
    {
        if (completed != null)
        {
            Remove(session.Id);
            await component.ModifyOriginalResponseAsync(message =>
            {
                message.Embed = completed;
                message.Components = new ComponentBuilder().Build();
            });
            return;
        }

        var view = Build(session);
        await component.ModifyOriginalResponseAsync(message =>
        {
            message.Embed = view.Embed;
            message.Components = view.Components;
        });
    }

    private PickerView Start(Session session)
    {
        Prune();
        lock (sync)
            sessions[session.Id] = session;
        return Build(session);
    }

    private PickerView Build(Session session)
    {
        // Long lists are rebuilt only when the list itself changes, not on every page.
        List<Choice> choices;
        if (Searchable(session.Stage))
        {
            var key = $"{session.Stage}|{session.DefIndex}|{session.Category}|{session.Filter}";
            if (session.CacheKey != key)
            {
                session.Cached = Choices(session);
                session.CacheKey = key;
            }
            choices = session.Cached;
        }
        else
        {
            choices = Choices(session);
        }
        var pages = Math.Max(1, (int)Math.Ceiling(choices.Count / (double)PageSize));
        session.Page = Math.Clamp(session.Page, 0, pages - 1);
        var page = choices.Skip(session.Page * PageSize).Take(PageSize).ToList();

        var components = new ComponentBuilder();

        // Row 0 first, so Search sits above the menu where it gets noticed.
        if (Searchable(session.Stage))
            components.WithButton("🔎 Search by name", $"pick:{session.Id}:search", ButtonStyle.Primary, row: 0);
        if (session.Filter.Length > 0)
            components.WithButton("Clear search", $"pick:{session.Id}:clear", ButtonStyle.Secondary, row: 0);

        if (page.Count > 0)
        {
            var select = new SelectMenuBuilder()
                .WithCustomId($"pick:{session.Id}:select")
                .WithPlaceholder(Placeholder(session))
                .WithMinValues(1)
                .WithMaxValues(1);

            foreach (var item in page)
                select.AddOption(Trim(item.Label, 100), item.Value, item.Description == null ? null : Trim(item.Description, 100));

            components.WithSelectMenu(select, row: 1);
        }

        components.WithButton("Previous", $"pick:{session.Id}:prev", ButtonStyle.Secondary, disabled: session.Page <= 0, row: 2);
        components.WithButton("Next", $"pick:{session.Id}:next", ButtonStyle.Secondary, disabled: session.Page >= pages - 1, row: 2);
        components.WithButton("Cancel", $"pick:{session.Id}:cancel", ButtonStyle.Danger, row: 2);

        var embed = new EmbedBuilder()
            .WithTitle(Title(session))
            .WithDescription(Description(session))
            .WithFooter($"Page {session.Page + 1}/{pages} • {choices.Count} item(s)");

        if (session.Paint != null && !string.IsNullOrWhiteSpace(session.Paint.Image) &&
            Uri.TryCreate(session.Paint.Image, UriKind.Absolute, out _))
            embed.WithThumbnailUrl(session.Paint.Image);

        return new PickerView(embed.Build(), components.Build());
    }

    private string Description(Session session)
    {
        var lines = new List<string>();

        if (session.TeamFixed)
            lines.Add($"Team: **{session.Team.Label()}** (this weapon is {session.Team.Label()} only)");
        else
            lines.Add($"Team: **{session.Team.Label()}**");

        if (session.Paint != null)
        {
            var build = new List<string> { $"Skin: **{session.Paint.Name}**" };
            if (session.Stage is Stage.Wear or Stage.StatTrak or Stage.StickerSlot or Stage.StickerPick)
                build.Add($"Pattern: **{session.Seed}**");
            if (session.Stage is Stage.StatTrak or Stage.StickerSlot or Stage.StickerPick)
                build.Add($"Wear: **{(session.Wear.HasValue ? session.Wear.Value.ToString("0.####", CultureInfo.InvariantCulture) : "lowest")}**");
            if (session.Stage is Stage.StickerSlot or Stage.StickerPick)
                build.Add($"StatTrak: **{(session.StatTrak ? "on" : "off")}**");
            lines.Add(string.Join(" • ", build));
        }

        if (session.Filter.Length > 0)
            lines.Add($"Search: `{session.Filter}` — press **Clear search** to see everything again");
        else if (Searchable(session.Stage))
            lines.Add("Press **🔎 Search by name** instead of paging through the list.");

        return string.Join("\n", lines);
    }

    private static bool Searchable(Stage stage) => stage is Stage.SkinWeapon or Stage.SkinPaint or Stage.Knife or Stage.KnifePaint
        or Stage.Glove or Stage.GlovePaint or Stage.AgentT or Stage.AgentCT or Stage.Music or Stage.Pin or Stage.StickerPick;

    private static string Placeholder(Session session) => session.Stage switch
    {
        Stage.SkinCategory => "Choose a category",
        Stage.SkinWeapon => "Choose a weapon",
        Stage.SkinPaint or Stage.KnifePaint or Stage.GlovePaint => "Choose a skin",
        Stage.Knife => "Choose a knife",
        Stage.Glove => "Choose gloves",
        Stage.AgentT or Stage.AgentCT => "Choose an agent",
        Stage.Music => "Choose a music kit",
        Stage.Pin => "Choose a pin",
        Stage.Pattern => "Choose a pattern",
        Stage.Wear => "Choose the wear",
        Stage.StatTrak => "StatTrak?",
        Stage.StickerSlot => "Stickers",
        Stage.StickerPick => "Choose a sticker",
        _ => "Choose an item"
    };

    private List<Choice> Choices(Session session)
    {
        return session.Stage switch
        {
            Stage.SkinCategory => catalog.Categories
                .Select(item => new Choice(item, item, $"{catalog.WeaponsByCategory.GetValueOrDefault(item, []).Count} weapons")).ToList(),

            Stage.SkinWeapon => Filter(catalog.WeaponsByCategory.GetValueOrDefault(session.Category, []), session.Filter, item => item.Name)
                .Select(item => new Choice(item.DefIndex.ToString(), item.Name,
                    item.Team == TeamTarget.Both ? $"DefIndex {item.DefIndex}" : $"{item.Team.Label()} only")).ToList(),

            Stage.SkinPaint or Stage.KnifePaint => new List<Choice> { new("0", "Default") }
                .Concat(Filter(catalog.Paints.GetValueOrDefault(session.DefIndex) ?? [], session.Filter, item => item.Name)
                    .Select(item => new Choice(item.Paint.ToString(), item.Name, $"Paint {item.Paint}", item.Image))).ToList(),

            Stage.Knife => new List<Choice> { new("0", "Default Knife") }
                .Concat(Filter(catalog.Knives, session.Filter, item => item.Name)
                    .Select(item => new Choice(item.DefIndex.ToString(), item.Name, $"DefIndex {item.DefIndex}"))).ToList(),

            Stage.Glove => new List<Choice> { new("0", "Default Gloves") }
                .Concat(Filter(catalog.Gloves, session.Filter, item => item.Name)
                    .Select(item => new Choice(item.DefIndex.ToString(), item.Name, $"DefIndex {item.DefIndex}"))).ToList(),

            Stage.GlovePaint => Filter(catalog.Paints.GetValueOrDefault(session.DefIndex) ?? [], session.Filter, item => item.Name)
                .Select(item => new Choice(item.Paint.ToString(), item.Name, $"Paint {item.Paint}", item.Image)).ToList(),

            Stage.AgentT => new List<Choice> { new("default", "Default T Agent") }
                .Concat(Filter(catalog.AgentsT, session.Filter, item => item.Name)
                    .Select(item => new Choice(item.Model, item.Name, item.Faction, item.Image))).ToList(),

            Stage.AgentCT => new List<Choice> { new("default", "Default CT Agent") }
                .Concat(Filter(catalog.AgentsCT, session.Filter, item => item.Name)
                    .Select(item => new Choice(item.Model, item.Name, item.Faction, item.Image))).ToList(),

            Stage.Music => new List<Choice> { new("0", "Default Music Kit") }
                .Concat(Filter(catalog.MusicKits, session.Filter, item => item.Name)
                    .Select(item => new Choice(item.Id.ToString(), item.Name, $"ID {item.Id}", item.Image))).ToList(),

            Stage.Pin => new List<Choice> { new("0", "Default Pin") }
                .Concat(Filter(catalog.Pins, session.Filter, item => item.Name)
                    .Select(item => new Choice(item.Id.ToString(), item.Name, item.Group, item.Image))).ToList(),

            Stage.Pattern =>
            [
                new("0", "Default pattern", "Seed 0"),
                new("random", "Random pattern", "Any seed from 1 to 1000"),
                new("ask:pattern", "Type a pattern…", "Pick an exact seed, 0 to 1000"),
                new("apply", "Apply now", "Skip the rest and save")
            ],

            Stage.Wear =>
            [
                new("lowest", "Lowest wear", "The best float this skin allows"),
                new("0.02", "Factory New"),
                new("0.10", "Minimal Wear"),
                new("0.25", "Field-Tested"),
                new("0.41", "Well-Worn"),
                new("0.60", "Battle-Scarred"),
                new("ask:wear", "Type a wear…", "Pick an exact float, 0.00 to 1.00"),
                new("apply", "Apply now", "Skip the rest and save")
            ],

            Stage.StatTrak =>
            [
                new("off", "No StatTrak"),
                new("on", "StatTrak", "Counts your kills in game"),
                new("apply", "Apply now", "Skip the rest and save")
            ],

            Stage.StickerSlot => StickerSlotChoices(session),

            // Every sticker stays reachable by paging, Search is just the fast way in.
            Stage.StickerPick => new List<Choice> { new("0", session.Slot == AllSlots ? "Clear every slot" : "Clear this slot") }
                .Concat(Filter(catalog.Stickers, session.Filter, item => item.Name)
                    .Select(item => new Choice(item.Id.ToString(), item.Name, $"ID {item.Id}"))).ToList(),

            _ => []
        };
    }

    private List<Choice> StickerSlotChoices(Session session)
    {
        var choices = new List<Choice> { new("apply", "Apply now", "Save the weapon as it is") };

        for (var slot = 0; slot < StickerSlots; slot++)
        {
            var chosen = session.Stickers.TryGetValue(slot, out var id) && id > 0
                ? catalog.Stickers.FirstOrDefault(item => item.Id == id)?.Name ?? $"ID {id}"
                : "empty";
            choices.Add(new Choice($"slot:{slot}", $"Slot {slot + 1}", chosen));
        }

        choices.Add(new Choice($"slot:{AllSlots}", "All slots", $"Put one sticker in all {StickerSlots} slots"));
        choices.Add(new Choice("clearall", "Clear all stickers", "Every slot plus the charm, like in game"));
        return choices;
    }

    private async Task<Embed?> Apply(Session session, string value)
    {
		var db = database;

        switch (session.Stage)
        {
            case Stage.SkinCategory:
                session.Category = value;
                session.Filter = "";
                session.Stage = Stage.SkinWeapon;
                return null;

            case Stage.SkinWeapon:
                session.DefIndex = int.Parse(value);
                session.Kind = ItemKind.Weapon;
                LockTeam(session);
                session.Filter = "";
                session.Stage = Stage.SkinPaint;
                return null;

            case Stage.SkinPaint:
            case Stage.KnifePaint:
            {
                var paintId = int.Parse(value);
                if (paintId == 0)
                {
                    await db.SetSkinAsync(session.SteamId, session.Team, session.DefIndex, null, session.Kind == ItemKind.Knife);
                    return Done(session.Kind == ItemKind.Knife ? "Knife changed" : "Skin changed",
                        $"{catalog.WeaponName(session.DefIndex)} → Default");
                }

                session.Paint = catalog.FindPaint(session.DefIndex, paintId) ?? throw new InvalidOperationException("Invalid skin.");
                session.Filter = "";
                session.Stage = Stage.Pattern;
                return null;
            }

            case Stage.Knife:
                session.DefIndex = int.Parse(value);
                session.Kind = ItemKind.Knife;
                if (session.DefIndex == 0)
                {
                    await db.ResetKnifeAsync(session.SteamId, session.Team);
                    return Done("Knife changed", "Default Knife");
                }
                session.Filter = "";
                session.Stage = Stage.KnifePaint;
                return null;

            case Stage.Glove:
                session.DefIndex = int.Parse(value);
                session.Kind = ItemKind.Glove;
                if (session.DefIndex == 0)
                {
                    await db.SetGlovesAsync(session.SteamId, session.Team, 0, null);
                    return Done("Gloves changed", "Default Gloves");
                }
                session.Filter = "";
                session.Stage = Stage.GlovePaint;
                return null;

            case Stage.GlovePaint:
                session.Paint = catalog.FindPaint(session.DefIndex, int.Parse(value)) ?? throw new InvalidOperationException("Invalid glove paint.");
                session.Filter = "";
                session.Stage = Stage.Pattern;
                return null;

            case Stage.AgentT:
            {
                var agent = value == "default" ? null : catalog.AgentsT.FirstOrDefault(item => item.Model == value);
                await db.SetAgentAsync(session.SteamId, 2, agent);
                if (session.Team == TeamTarget.Both)
                {
                    session.Filter = "";
                    session.Stage = Stage.AgentCT;
                    return null;
                }
                return Done("Agent changed", agent?.Name ?? "Default T Agent", agent?.Image);
            }

            case Stage.AgentCT:
            {
                var agent = value == "default" ? null : catalog.AgentsCT.FirstOrDefault(item => item.Model == value);
                await db.SetAgentAsync(session.SteamId, 3, agent);
                return Done("Agent changed", agent?.Name ?? "Default CT Agent", agent?.Image);
            }

            case Stage.Music:
            {
                var id = int.Parse(value);
                var item = catalog.MusicKits.FirstOrDefault(entry => entry.Id == id);
                await db.SetMusicAsync(session.SteamId, id);
                return Done("Music kit changed", item?.Name ?? "Default Music Kit", item?.Image);
            }

            case Stage.Pin:
            {
                var id = int.Parse(value);
                var item = catalog.Pins.FirstOrDefault(entry => entry.Id == id);
                await db.SetPinAsync(session.SteamId, id);
                return Done("Pin changed", item?.Name ?? "Default Pin", item?.Image);
            }

            case Stage.Pattern:
                if (value == "apply")
                    return await Commit(session, db);
                session.Seed = value == "random" ? Random.Shared.Next(1, 1001) : int.Parse(value);
                session.Stage = Stage.Wear;
                return null;

            case Stage.Wear:
                if (value == "apply")
                    return await Commit(session, db);
                session.Wear = value == "lowest" ? null : float.Parse(value, CultureInfo.InvariantCulture);
                return await AfterWear(session);

            case Stage.StatTrak:
                if (value == "apply")
                    return await Commit(session, db);
                session.StatTrak = value == "on";
                if (session.Kind != ItemKind.Weapon)
                    return await Commit(session, db);
                // Stickers can be VIP only. When the plugin says no, the build
                // finishes here rather than offering a menu that would refuse.
                if (!(await db.PermissionsAsync(session.SteamId)).Stickers)
                    return await Commit(session, db);
                session.Stage = Stage.StickerSlot;
                return null;

            case Stage.StickerSlot:
                if (value == "apply")
                    return await Commit(session, db);
                if (value == "clearall")
                {
                    // Wipes every slot and the charm on save, like the in-game menu.
                    session.Stickers.Clear();
                    session.ClearStickers = true;
                    return null;
                }
                session.Slot = int.Parse(value.Split(':')[1]);
                session.Filter = "";
                session.Stage = Stage.StickerPick;
                return null;

            case Stage.StickerPick:
            {
                var id = int.Parse(value);
                if (session.Slot == AllSlots)
                {
                    for (var slot = 0; slot < StickerSlots; slot++)
                        session.Stickers[slot] = id;
                }
                else
                {
                    session.Stickers[session.Slot] = id;
                }

                // Stickers on their own (the /stickers command) save straight away.
                if (session.Paint == null)
                {
                    var sticker = id == 0 ? null : catalog.Stickers.FirstOrDefault(item => item.Id == id);

                    if (sticker == null && session.Slot == AllSlots)
                    {
                        var wiped = await db.ClearStickersAsync(session.SteamId, session.Team, session.DefIndex);
                        return wiped
                            ? Done("Stickers cleared", $"{catalog.WeaponName(session.DefIndex)} → every sticker and the charm removed")
                            : Done("Nothing changed", "Choose a skin for that weapon first.");
                    }

                    // One call for every slot being changed: each database write queues a
                    // sync row, and one row per slot made the weapon redraw four times.
                    var slots = session.Slot == AllSlots
                        ? Enumerable.Range(0, StickerSlots).ToArray()
                        : [session.Slot];

                    var ok = await db.SetStickersAsync(session.SteamId, session.Team, session.DefIndex, slots, sticker);
                    if (!ok)
                        return Done("Sticker not changed", "Choose a skin for that weapon first.");

                    return Done("Stickers changed",
                        $"{catalog.WeaponName(session.DefIndex)} → {sticker?.Name ?? "cleared"}" +
                        (session.Slot == AllSlots ? $" (all {StickerSlots} slots)" : $" (slot {session.Slot + 1})"));
                }

                session.Filter = "";
                session.Stage = Stage.StickerSlot;
                return null;
            }

            default:
                throw new InvalidOperationException("Invalid selection session.");
        }
    }

    private async Task<Embed?> AfterWear(Session session)
    {
		// Gloves carry no StatTrak and no stickers.
		if (session.Kind == ItemKind.Glove)
			return await Commit(session, database);

        session.Stage = Stage.StatTrak;
        return null;
    }

    private async Task<Embed> Commit(Session session, WeaponSkinsDatabase db)
    {
        await db.ApplyBuildAsync(
            session.SteamId,
            session.Team,
            session.DefIndex,
            session.Paint,
            session.Kind,
            session.Seed,
            session.Wear,
            session.StatTrak,
            session.Kind == ItemKind.Weapon ? session.Stickers : null,
            session.Kind == ItemKind.Weapon && session.ClearStickers);

        var parts = new List<string>
        {
            $"Team **{session.Team.Label()}**",
            $"pattern **{session.Seed}**",
            $"wear **{(session.Wear.HasValue ? session.Wear.Value.ToString("0.####", CultureInfo.InvariantCulture) : "lowest")}**"
        };

        if (session.Kind != ItemKind.Glove)
            parts.Add($"StatTrak **{(session.StatTrak ? "on" : "off")}**");

        var applied = session.Stickers.Count(pair => pair.Value > 0);
        if (session.Kind == ItemKind.Weapon && session.ClearStickers && applied == 0)
            parts.Add("stickers and charm cleared");
        else if (session.Kind == ItemKind.Weapon && session.Stickers.Count > 0)
            parts.Add(applied > 0 ? $"**{applied}** sticker(s)" : "stickers cleared");

        var title = session.Kind switch
        {
            ItemKind.Knife => "Knife applied",
            ItemKind.Glove => "Gloves applied",
            _ => "Skin applied"
        };

        return Done(title,
            $"**{catalog.WeaponName(session.DefIndex)} | {session.Paint?.Name ?? "Default"}**\n{string.Join(" • ", parts)}",
            session.Paint?.Image);
    }

    private void LockTeam(Session session)
    {
        var team = catalog.TeamOf(session.DefIndex);
        if (team == TeamTarget.Both)
            return;

        session.Team = team;
        session.TeamFixed = true;
    }

    private static Modal SearchModal(Session session) => new ModalBuilder("Search", $"pickmodal:{session.Id}:search")
        .AddTextInput("Type part of the name", "value", placeholder: "Leave empty to show everything", required: false, maxLength: 100)
        .Build();

    private static Modal ValueModal(Session session, string kind, string label, string placeholder, string value) =>
        new ModalBuilder(label, $"pickmodal:{session.Id}:{kind}")
            .AddTextInput(label, "value", placeholder: placeholder, required: true, maxLength: 12, value: value)
            .Build();

	private bool TryGet(string customId, ulong guildId, ulong userId, out Session session, out string action)
    {
        session = null!;
        action = "";
        var parts = customId.Split(':', 3);
        if (parts.Length != 3 || parts[0] != "pick")
            return false;

        lock (sync)
        {
			if (!sessions.TryGetValue(parts[1], out session!) || session.GuildId != guildId || session.UserId != userId)
                return false;
            session.LastUsed = DateTimeOffset.UtcNow;
        }

        action = parts[2];
        return true;
    }

    private string Title(Session session)
    {
        return session.Stage switch
        {
            Stage.SkinCategory => "Skins • Choose category",
            Stage.SkinWeapon => $"Skins • {session.Category} • Choose weapon",
            Stage.SkinPaint => $"Skins • {catalog.WeaponName(session.DefIndex)} • Choose skin",
            Stage.Knife => "Knife • Choose knife",
            Stage.KnifePaint => $"Knife • {catalog.WeaponName(session.DefIndex)} • Choose skin",
            Stage.Glove => "Gloves • Choose gloves",
            Stage.GlovePaint => $"Gloves • {catalog.WeaponName(session.DefIndex)} • Choose skin",
            Stage.AgentT => "Agents • Choose T agent",
            Stage.AgentCT => "Agents • Choose CT agent",
            Stage.Music => "Music • Choose kit",
            Stage.Pin => "Pins • Choose pin",
            Stage.Pattern => $"{catalog.WeaponName(session.DefIndex)} • Pattern",
            Stage.Wear => $"{catalog.WeaponName(session.DefIndex)} • Wear",
            Stage.StatTrak => $"{catalog.WeaponName(session.DefIndex)} • StatTrak",
            Stage.StickerSlot => $"{catalog.WeaponName(session.DefIndex)} • Stickers",
            Stage.StickerPick => session.Slot == AllSlots
                ? $"{catalog.WeaponName(session.DefIndex)} • Sticker for all slots"
                : $"{catalog.WeaponName(session.DefIndex)} • Sticker slot {session.Slot + 1}",
            _ => "WeaponSkins"
        };
    }

    private static Embed Done(string title, string description, string? image = null)
    {
        var builder = new EmbedBuilder().WithTitle(title).WithDescription(description);
        if (!string.IsNullOrWhiteSpace(image) && Uri.TryCreate(image, UriKind.Absolute, out _))
            builder.WithImageUrl(image);
        return builder.Build();
    }

    private static IEnumerable<T> Filter<T>(IEnumerable<T> source, string filter, Func<T, string> name)
    {
        if (string.IsNullOrWhiteSpace(filter))
            return source;
        return source.Where(item => name(item).Contains(filter, StringComparison.OrdinalIgnoreCase));
    }

    private void Remove(string id)
    {
        lock (sync)
            sessions.Remove(id);
    }

    private void Prune()
    {
        // Half an hour of doing nothing, not half an hour since it opened, so a
        // long scroll through 11k stickers never loses the menu.
        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-SessionMinutes);
        lock (sync)
        {
            foreach (var id in sessions.Where(pair => pair.Value.LastUsed < cutoff).Select(pair => pair.Key).ToList())
                sessions.Remove(id);
        }
    }

    private static string NewId() => Guid.NewGuid().ToString("N")[..10];
    private static string Trim(string value, int length) => value.Length <= length ? value : value[..(length - 1)] + "…";
    private static string Safe(string value) => value.Length <= 300 ? value : value[..300];
}
