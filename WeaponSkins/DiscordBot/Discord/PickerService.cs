using System.Globalization;
using Discord;
using Discord.WebSocket;
using WeaponSkinsBot.Catalog;
using WeaponSkinsBot.Database;

namespace WeaponSkinsBot.Discord;

public sealed record PickerView(Embed? Embed, MessageComponent Components);

/// <summary>Owner-bound, expiring menus. Preview edits are local; Save writes one complete build.</summary>
public sealed partial class PickerService
{
    private enum Stage { Categories, Weapons, Paints, Preview, Agents, Music, Pins, Slots, Stickers, Unlink }
    private sealed class Session
    {
        public string Id = Guid.NewGuid().ToString("N");
        public ulong GuildId, UserId, SteamId;
        public TeamTarget Team;
        public Stage Stage;
        public string Category = "", Filter = "", NameTag = "";
        public int DefIndex, Page, Seed, Revision;
        public int[] Slots = [];
        public ItemKind Kind;
        public PaintDef? Paint;
        public float Wear;
        public bool StatTrak, Finished;
        public DateTimeOffset Expires = DateTimeOffset.UtcNow.AddMinutes(30);
        public readonly SemaphoreSlim Gate = new(1, 1);
        public string CacheKey = "";
        public List<Choice> Cached = [];
    }
    private sealed record Choice(string Value, string Label, string Image = "", string Rarity = "");
    public const int StickerSlots = WeaponSkinsDatabase.ManagedStickerSlots;
    private const int PageSize = 23;
    private readonly CatalogService catalog;
    private readonly WeaponSkinsDatabase database;
    private readonly CancellationToken cancellationToken;
    private readonly SemaphoreSlim operations;
    private readonly Dictionary<string, Session> sessions = [];
    private readonly object sync = new();

    public PickerService(CatalogService catalog, WeaponSkinsDatabase database, CancellationToken cancellationToken = default, SemaphoreSlim? operations = null)
    {
        this.catalog = catalog;
        this.database = database;
        this.cancellationToken = cancellationToken;
        this.operations = operations ?? new SemaphoreSlim(4, 4);
    }

    private Session New(ulong guild, ulong user, ulong steam, TeamTarget team, Stage stage) =>
        new() { GuildId = guild, UserId = user, SteamId = steam, Team = team, Stage = stage };

    private PickerView Start(Session s)
    {
        lock (sync)
        {
            foreach (var id in sessions.Where(x => x.Value.Expires <= DateTimeOffset.UtcNow).Select(x => x.Key).ToArray())
                sessions.Remove(id);
            // Bound retained menus even when users repeatedly open commands without dismissing them.
            if (sessions.Count >= 512)
                foreach (var id in sessions.OrderBy(x => x.Value.Expires).Take(64).Select(x => x.Key).ToArray())
                    sessions.Remove(id);
            sessions.Add(s.Id, s);
        }
        return Render(s);
    }

    public PickerView StartSkins(ulong guild, ulong user, ulong steam, TeamTarget team, string category)
    {
        var s = New(guild, user, steam, team, Stage.Categories);
        if (!string.IsNullOrWhiteSpace(category))
        {
            s.Category = CanonicalCategory(category);
            if (!Categories.Contains(s.Category)) throw new InvalidOperationException("Unknown weapon category.");
            s.Stage = Stage.Weapons;
        }
        return Start(s);
    }
    public PickerView StartKnife(ulong guild, ulong user, ulong steam, TeamTarget team, KnifeDef? knife)
    {
        var s = New(guild, user, steam, team, knife == null ? Stage.Weapons : Stage.Paints);
        s.Kind = ItemKind.Knife; s.Category = "Knifes"; s.DefIndex = knife?.DefIndex ?? 0;
        return Start(s);
    }
    public PickerView StartGloves(ulong guild, ulong user, ulong steam, TeamTarget team)
    {
        var s = New(guild, user, steam, team, Stage.Weapons);
        s.Kind = ItemKind.Glove; s.Category = "Gloves";
        return Start(s);
    }
    public PickerView StartAgents(ulong guild, ulong user, ulong steam, TeamTarget team) => Start(New(guild, user, steam, team, Stage.Agents));
    public PickerView StartMusic(ulong guild, ulong user, ulong steam) => Start(New(guild, user, steam, TeamTarget.Both, Stage.Music));
    public PickerView StartPins(ulong guild, ulong user, ulong steam) => Start(New(guild, user, steam, TeamTarget.Both, Stage.Pins));
    public PickerView StartUnlink(ulong guild, ulong user, ulong steam) => Start(New(guild, user, steam, TeamTarget.Both, Stage.Unlink));
    public PickerView StartStickers(ulong guild, ulong user, ulong steam, TeamTarget team, int def, int slot, string filter)
    {
        var s = New(guild, user, steam, team, slot == 0 ? Stage.Slots : Stage.Stickers);
        s.DefIndex = def; s.Filter = filter;
        // The prefix parser uses 0 for "ask", 1..4 for slots, -1 for all.
        s.Slots = slot < 0 ? Enumerable.Range(0, StickerSlots).ToArray() : slot > 0 ? [slot - 1] : [];
        return Start(s);
    }

    public Task HandleSelectAsync(SocketMessageComponent component) => HandleComponent(component, true);
    public Task HandleButtonAsync(SocketMessageComponent component) => HandleComponent(component, false);

    private Session? Find(string customId, ulong user, ulong? guild, out string action, out int revision)
    {
        action = ""; revision = -1;
        var parts = customId.Split(':');
        if (parts.Length != 4 || parts[0] != "pick" || !int.TryParse(parts[2], out revision)) return null;
        action = parts[3];
        lock (sync)
            return sessions.TryGetValue(parts[1], out var s) && s.UserId == user && s.GuildId == guild &&
                s.Expires > DateTimeOffset.UtcNow ? s : null;
    }

    private bool StillCurrent(Session s, int revision)
    {
        lock (sync)
            return !s.Finished && s.Expires > DateTimeOffset.UtcNow && s.Revision == revision &&
                sessions.TryGetValue(s.Id, out var live) && ReferenceEquals(s, live);
    }

    private async Task HandleComponent(SocketMessageComponent component, bool select)
    {
        var s = Find(component.Data.CustomId, component.User.Id, component.GuildId, out var action, out var revision);
        if (s == null)
        {
            await component.RespondAsync("This menu expired. Run the command again.", ephemeral: true);
            return;
        }
        var value = select ? component.Data.Values.FirstOrDefault() ?? "" : "";
        var modal = action is "search" or "seed" or "tag" || action == "wear" && value == "custom";
        // A modal is itself the acknowledgement; never defer before opening one.
        if (modal)
        {
            if (!await s.Gate.WaitAsync(0, cancellationToken))
            {
                await component.RespondAsync("This menu is updating. Try again in a moment.", ephemeral: true);
                return;
            }
            try
            {
                if (!StillCurrent(s, revision))
                    await component.RespondAsync("That menu changed. Use its latest controls.", ephemeral: true);
                else
                    await component.RespondWithModalAsync(BuildModal(s, action));
            }
            finally { s.Gate.Release(); }
            return;
        }

        await component.DeferAsync();
        await s.Gate.WaitAsync(cancellationToken);
        try
        {
            if (!StillCurrent(s, revision))
            {
                await component.FollowupAsync("That menu changed or was already saved. Use its latest controls.", ephemeral: true);
                return;
            }
            var writes = action is "save" or "unlink" or "default" or "clear" ||
                action == "select" && value is not ("__next" or "__prev") && s.Stage is Stage.Agents or Stage.Music or Stage.Pins or Stage.Stickers;
            PickerView? result;
            if (writes)
            {
                await operations.WaitAsync(cancellationToken);
                try { result = await Act(s, action, value); }
                finally { operations.Release(); }
            }
            else result = await Act(s, action, value);
            s.Revision++;
            await Show(component, result ?? Render(s));
        }
        catch (InvalidOperationException ex)
        {
            await component.FollowupAsync(ex.Message, ephemeral: true, allowedMentions: AllowedMentions.None);
        }
        finally { s.Gate.Release(); }
    }

    public async Task HandleModalAsync(SocketModal modal)
    {
        var s = Find(modal.Data.CustomId, modal.User.Id, modal.GuildId, out var action, out var revision);
        await modal.DeferAsync();
        if (s == null)
        {
            await modal.FollowupAsync("This menu expired. Run the command again.", ephemeral: true);
            return;
        }
        await s.Gate.WaitAsync(cancellationToken);
        try
        {
            if (!StillCurrent(s, revision))
            {
                await modal.FollowupAsync("That menu changed. Open the control again.", ephemeral: true);
                return;
            }
            var value = modal.Data.Components.FirstOrDefault(x => x.CustomId == "value")?.Value?.Trim() ?? "";
            switch (action)
            {
                case "search" when Searchable(s): s.Filter = Short(value, 100); s.Page = 0; break;
                case "seed" when s.Stage == Stage.Preview:
                    if (!int.TryParse(value, out var seed) || seed is < 0 or > 1000)
                        throw new InvalidOperationException("Seed / Pattern must be from 0 to 1000.");
                    s.Seed = seed; break;
                case "tag" when s.Stage == Stage.Preview:
                    s.NameTag = value == "-" ? "" : Short(value, 64); break;
                case "wear" when s.Stage == Stage.Preview:
                    if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var wear) ||
                        !float.IsFinite(wear) || wear is < 0 or > 1)
                        throw new InvalidOperationException("Float must be a number from 0 to 1.");
                    s.Wear = EffectiveWear(s, wear); break;
                default: throw new InvalidOperationException("This control is no longer available.");
            }
            s.Revision++;
            await Show(modal, Render(s));
        }
        catch (InvalidOperationException ex)
        {
            await modal.FollowupAsync(ex.Message, ephemeral: true, allowedMentions: AllowedMentions.None);
        }
        finally { s.Gate.Release(); }
    }

    private async Task CheckWriteAccess(Session s, bool stickers = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (await database.GetSteamIdAsync(s.UserId) != s.SteamId)
            throw new InvalidOperationException("Your linked account changed. Run the command again.");
        if (stickers && !(await database.PermissionsAsync(s.SteamId)).Stickers)
            throw new InvalidOperationException("Stickers are disabled for your account by WeaponSkins (VIP permission required).");
    }

    private async Task<PickerView?> Act(Session s, string action, string value)
    {
        if (action == "cancel") return Finish(s, "WeaponSkins", "Menu closed.", success: false);
        if (action == "back")
        {
            s.Page = 0; s.Filter = "";
            s.Stage = s.Stage switch { Stage.Preview => Stage.Paints, Stage.Paints => Stage.Weapons, _ => Stage.Categories };
            return null;
        }
        if (action == "wear" && s.Stage == Stage.Preview)
        {
            if (!WearOptions.Any(x => x.Value == value)) throw new InvalidOperationException("Select a valid wear.");
            s.Wear = EffectiveWear(s, float.Parse(value, CultureInfo.InvariantCulture)); return null;
        }
        if (action == "st" && s.Stage == Stage.Preview)
        {
            if (s.Kind != ItemKind.Glove) s.StatTrak = !s.StatTrak;
            return null;
        }
        if (action == "save" && s.Stage == Stage.Preview)
        {
            await CheckWriteAccess(s);
            await database.ApplyBuildAsync(s.SteamId, s.Team, s.DefIndex, s.Paint, s.Kind,
                s.Seed, s.Wear, s.StatTrak, null, nameTag: s.NameTag);
            return FinishBuild(s);
        }
        if (action == "unlink" && s.Stage == Stage.Unlink)
        {
            await CheckWriteAccess(s);
            await database.UnlinkAsync(s.UserId);
            return Finish(s, "Account unlinked", "Your Steam account has been unlinked from WeaponSkins. Your saved skins stay.");
        }
        if (action == "select")
        {
            if (value is "__next" or "__prev") { s.Page += value == "__next" ? 1 : -1; return null; }
            var choice = Choices(s).FirstOrDefault(x => x.Value == value)
                ?? throw new InvalidOperationException("This selection is no longer available.");
            s.Page = 0; s.Filter = "";
            switch (s.Stage)
            {
                case Stage.Categories: s.Category = value; s.Stage = Stage.Weapons; return null;
                case Stage.Weapons:
                    s.DefIndex = int.Parse(value);
                    s.Kind = s.Category == "Knifes" ? ItemKind.Knife : s.Category == "Gloves" ? ItemKind.Glove : ItemKind.Weapon;
                    var fixedTeam = catalog.TeamOf(s.DefIndex);
                    if (fixedTeam != TeamTarget.Both) s.Team = fixedTeam;
                    s.Stage = Stage.Paints; return null;
                case Stage.Paints:
                    s.Paint = catalog.FindPaint(s.DefIndex, int.Parse(value))!;
                    s.Seed = 0; s.Wear = EffectiveWear(s, .02f); s.NameTag = ""; s.StatTrak = false;
                    s.Stage = Stage.Preview; return null;
                case Stage.Slots:
                    s.Slots = value == "all" ? Enumerable.Range(0, StickerSlots).ToArray() : [int.Parse(value) - 1];
                    s.Stage = Stage.Stickers; return null;
            }
        }
        if (action is not ("select" or "default" or "clear")) return null;
        await CheckWriteAccess(s, s.Stage == Stage.Stickers);
        switch (s.Stage)
        {
            case Stage.Paints when action == "default":
                if (s.Kind == ItemKind.Glove) await database.SetGlovesAsync(s.SteamId, s.Team, s.DefIndex, null);
                else await database.SetSkinAsync(s.SteamId, s.Team, s.DefIndex, null, s.Kind == ItemKind.Knife);
                return Finish(s, "Loadout saved", "Default restored.");
            case Stage.Weapons when action == "default":
                if (s.Category == "Knifes") await database.ResetKnifeAsync(s.SteamId, s.Team);
                else if (s.Category == "Gloves") await database.SetGlovesAsync(s.SteamId, s.Team, 0, null);
                else return null;
                return Finish(s, "Loadout saved", "Default restored.");
            case Stage.Agents:
                var side = s.Team == TeamTarget.CounterTerrorist ? 3 : 2;
                var agent = action == "default" ? null : (side == 3 ? catalog.AgentsCT : catalog.AgentsT)[int.Parse(value[6..])];
                await database.SetAgentAsync(s.SteamId, side, agent);
                // Preserve the prefix command's existing Both flow (T first, then CT).
                if (s.Team == TeamTarget.Both) { s.Team = TeamTarget.CounterTerrorist; return null; }
                return Finish(s, "", $"Your agent for team **{TeamLabel(s.Team)}** has been successfully set to `{Inline(agent?.DisplayName ?? "Default")}`.", agent?.Image ?? "");
            case Stage.Music:
                var music = action == "default" ? null : catalog.MusicKits.First(x => x.Id.ToString() == value);
                await database.SetMusicAsync(s.SteamId, music?.Id ?? 0);
                return Finish(s, "Loadout saved", music?.Name ?? "Default", music?.Image ?? "");
            case Stage.Pins:
                var pin = action == "default" ? null : catalog.Pins.First(x => x.Id.ToString() == value);
                await database.SetPinAsync(s.SteamId, pin?.Id ?? 0);
                return Finish(s, "Loadout saved", pin?.Name ?? "Default", pin?.Image ?? "");
            case Stage.Stickers:
                var sticker = action == "select" ? catalog.Stickers.First(x => x.Id.ToString() == value) : null;
                var changed = action == "clear"
                    ? await database.ClearStickersAsync(s.SteamId, s.Team, s.DefIndex)
                    : await database.SetStickersAsync(s.SteamId, s.Team, s.DefIndex, s.Slots, sticker);
                if (!changed) throw new InvalidOperationException("Select a finish for this weapon and team first.");
                return Finish(s, "Loadout saved", action == "clear" ? "All stickers and charm removed" : sticker?.Name ?? "Selected sticker slots cleared", sticker?.Image ?? "");
        }
        return null;
    }

    private PickerView Finish(Session s, string title, string text, string image = "", bool success = true)
    {
        s.Finished = true;
        lock (sync) sessions.Remove(s.Id);
        return new PickerView(Embed(title, "> " + text, success ? "#66ff66" : "#ffffff", image, thumbnail: true), new ComponentBuilder().Build());
    }
    private PickerView FinishBuild(Session s)
    {
        var text = $"> Your skin for the weapon **{catalog.WeaponName(s.DefIndex)}** (Team: {TeamLabel(s.Team)}) has been successfully set to `{Inline(PaintName(s))}`\n\n" +
            $"> Wear: `{s.Wear.ToString("0.######", CultureInfo.InvariantCulture)}`\n> Pattern / Seed: `{s.Seed}`\n> Nametag: `{Inline(s.NameTag.Length == 0 ? "Not Set" : s.NameTag)}`\n> Stattrak: {StatTrakText(s)}";
        var result = Finish(s, "", "");
        return result with { Embed = Embed("", text, "#66ff66", s.Paint?.Image ?? "") };
    }
    private static async Task Show(SocketInteraction interaction, PickerView view) =>
        await interaction.ModifyOriginalResponseAsync(message => {
            message.Content = ""; message.Embeds = view.Embed == null ? Array.Empty<Embed>() : [view.Embed];
            message.Components = view.Components; message.AllowedMentions = AllowedMentions.None;
        });
}
