using System.Globalization;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using WeaponSkinsBot.Catalog;
using WeaponSkinsBot.Database;
using WeaponSkinsBot.Discord;

namespace WeaponSkinsBot;

public sealed class BotApp
{
	private const string Branding = "WeaponSkins | By ✪ Stαr";
	private readonly string token;
	private readonly global::WeaponSkins.Database sourceDatabase;
	private readonly string moduleDirectory;
	private readonly global::WeaponSkins.ApiConfig apiConfig;
	private readonly ILogger logger;
	private readonly Action connected;
	private DiscordSocketClient client = null!;
	private CatalogService catalog = null!;
	private PickerService picker = null!;
	private WeaponSkinsDatabase database = null!;
	private int ready;
	private int shuttingDown;
	private ulong activeGuildId;
	private bool extraGuildsLogged;

    /// <summary>Servers whose slash commands have been written this run, so a reconnect
    /// does not push them all again.</summary>
    private readonly HashSet<ulong> commandGuilds = [];

    private readonly object commandGuildSync = new();

    private ApplicationCommandProperties[]? slashCommands;

    /// <summary>Built once: the same set goes to the global registration and to each server.</summary>
    private ApplicationCommandProperties[] SlashCommands =>
        slashCommands ??= BuildSlashCommands().Select(builder => (ApplicationCommandProperties)builder.Build()).ToArray();

    /// <summary>
    /// Discord only lets an interaction reply privately, a plain message can never
    /// be one. So a ! command posts a small opener that only the caller can press,
    /// and the button reply is the private, dismissible one.
    /// </summary>
    private sealed record Pending(ulong GuildId, ulong UserId, string Command, List<string> Args, DateTimeOffset CreatedAt);

    private sealed record Payload(string? Text, Embed? Embed, MessageComponent? Components);

    private readonly Dictionary<string, Pending> prompts = [];
    private readonly object promptSync = new();

	private static readonly HashSet<string> KnownCommands = new(StringComparer.OrdinalIgnoreCase)
	{
		"help", "wshelp", "link", "unlink", "me", "loadout",
        "skins", "skin", "knife", "knives", "knifes", "gloves", "glove",
        "agents", "agent", "music", "mk", "pins", "pin",
        "wear", "float", "seed", "pattern", "nametag", "tag",
        "stattrak", "st", "stickers", "sticker", "g", "gen"
    };

    /// <summary>
    /// Shown when the plugin says this player may not use stickers, either
    /// because `vip_only` is on and they are not VIP, or because stickers are
    /// switched off for the whole server.
    /// </summary>
    private const string StickersDenied = "stickers are available only to VIP members.";

    /// <summary>Shown when the plugin says this player may not use gen codes.</summary>
    private const string GenDenied = "Gen codes are available only to VIP members.";

	public BotApp(
		string token,
		global::WeaponSkins.Database database,
		string moduleDirectory,
		global::WeaponSkins.ApiConfig apiConfig,
		ILogger logger,
		Action connected)
	{
		this.token = token;
		sourceDatabase = database;
		this.moduleDirectory = moduleDirectory;
		this.apiConfig = apiConfig;
		this.logger = logger;
		this.connected = connected;
	}

	public async Task RunAsync(CancellationToken cancellationToken)
	{
		try
		{
			database = new WeaponSkinsDatabase(sourceDatabase);
			catalog = new CatalogService(
				Path.Combine(moduleDirectory, "BotData", "catalog"),
				Path.Combine(moduleDirectory, "Data"),
				apiConfig);
			await catalog.LoadAsync(cancellationToken);
			picker = new PickerService(catalog, database);

			client = new DiscordSocketClient(new DiscordSocketConfig
			{
				GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildMessages | GatewayIntents.MessageContent,
				LogGatewayIntentWarnings = false,
				MessageCacheSize = 20
			});

			client.Log += OnLog;
			client.Ready += OnReady;
			client.JoinedGuild += OnJoinedGuild;
			client.LeftGuild += OnLeftGuild;
			client.MessageReceived += OnMessage;
			client.SlashCommandExecuted += OnSlash;
			client.ButtonExecuted += OnButton;
			client.SelectMenuExecuted += picker.HandleSelectAsync;
			client.ModalSubmitted += OnModal;

			await client.LoginAsync(TokenType.Bot, token);
			await client.StartAsync();
			await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
		}
		finally
		{
			await ShutdownAsync();
		}
	}

	private Task OnLog(LogMessage message)
	{
		if (message.Severity is LogSeverity.Info or LogSeverity.Verbose or LogSeverity.Debug)
			return Task.CompletedTask;

		var level = message.Severity switch
		{
			LogSeverity.Critical => LogLevel.Critical,
			LogSeverity.Error => LogLevel.Error,
			LogSeverity.Warning => LogLevel.Warning,
			_ => LogLevel.Error
		};
		logger.Log(level, message.Exception, "WeaponSkinsBOT {Source}: {Message}", message.Source, message.Message);
		return Task.CompletedTask;
	}

	private async Task ShutdownAsync()
	{
		if (Interlocked.Exchange(ref shuttingDown, 1) != 0)
			return;

		if (client != null)
		{
			client.Log -= OnLog;
			client.Ready -= OnReady;
			client.JoinedGuild -= OnJoinedGuild;
			client.LeftGuild -= OnLeftGuild;
			client.MessageReceived -= OnMessage;
			client.SlashCommandExecuted -= OnSlash;
			client.ButtonExecuted -= OnButton;
			if (picker != null)
			{
				client.SelectMenuExecuted -= picker.HandleSelectAsync;
				client.ModalSubmitted -= OnModal;
			}

			await WithTimeout(client.StopAsync());
			await WithTimeout(client.LogoutAsync());
			client.Dispose();
		}

		catalog?.Dispose();
	}

	private static async Task WithTimeout(Task task)
	{
		try
		{
			if (await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(5))) == task)
				await task;
		}
		catch
		{
		}
	}

    /// <summary>
    /// Writes the commands straight to a server the moment the bot is added to it.
    /// The global copy registered at startup is cached by Discord and can take up to an
    /// hour to reach a client, which is why the commands only turned up after restarting
    /// Discord. A guild copy is pushed immediately, and it shadows the global one of the
    /// same name rather than showing twice.
    /// </summary>
	private async Task OnJoinedGuild(SocketGuild guild)
	{
		if (ClaimGuild(guild.Id))
			await RegisterGuildCommands(guild);
		else
			await ClearGuildCommands(guild);
	}

	private async Task OnLeftGuild(SocketGuild guild)
	{
		lock (commandGuildSync)
		{
			commandGuilds.Remove(guild.Id);
			if (activeGuildId == guild.Id)
				activeGuildId = 0;
		}

		var replacement = client.Guilds.Where(item => item.Id != guild.Id).OrderBy(item => item.Id).FirstOrDefault();
		if (replacement != null && ClaimGuild(replacement.Id))
			await RegisterGuildCommands(replacement);
	}

    /// <summary>
    /// Writes the commands straight to one server. Skips a server already done this run,
    /// because Ready fires again on every reconnect and there is no reason to push the
    /// same set repeatedly.
    /// </summary>
    private async Task RegisterGuildCommands(SocketGuild guild)
    {
        lock (commandGuildSync)
        {
            if (!commandGuilds.Add(guild.Id))
                return;
        }

        try
        {
            await guild.BulkOverwriteApplicationCommandAsync(SlashCommands);
        }
        catch (Exception ex)
        {
            // Let it be retried on the next reconnect, and note the global copy still
            // arrives on its own once Discord's cache catches up.
            lock (commandGuildSync)
                commandGuilds.Remove(guild.Id);

			logger.LogWarning("WeaponSkinsBOT could not register commands for {Guild} ({GuildId}): {Error}", guild.Name, guild.Id, ex.Message);
		}
	}

	private async Task ClearGuildCommands(SocketGuild guild)
	{
		try
		{
			await guild.BulkOverwriteApplicationCommandAsync([]);
		}
		catch (Exception ex)
		{
			logger.LogWarning("WeaponSkinsBOT could not clear commands from ignored Discord server {GuildId}: {Error}", guild.Id, ex.Message);
		}
	}

    private async Task OnReady()
    {
        if (Interlocked.Exchange(ref ready, 1) == 0)
        {
            // Commands are registered per server, never globally. A global copy does not
            // get replaced by the guild one, it sits beside it, so every command shows
            // twice. Nothing is lost by dropping it: slash commands are refused outside a
            // server anyway, and the per-server copy appears immediately instead of
            // waiting on Discord's global cache.
            //
            // The empty overwrite clears a global set left by an older build. Without it
            // those entries stay registered on Discord's side and the duplicates never go.
            await client.BulkOverwriteGlobalApplicationCommandsAsync([]);
			connected();
		}

		await client.SetGameAsync(Branding);
		var guilds = client.Guilds.OrderBy(guild => guild.Id).ToList();
		if (guilds.Count == 0)
		{
			logger.LogWarning("WeaponSkinsBOT is online but has not been added to a Discord server");
			return;
		}

		ClaimGuild(guilds[0].Id);
		foreach (var guild in guilds)
		{
			if (IsActiveGuild(guild.Id))
				await RegisterGuildCommands(guild);
			else
				await ClearGuildCommands(guild);
		}

		if (guilds.Count > 1 && !extraGuildsLogged)
		{
			extraGuildsLogged = true;
			var active = guilds.First(guild => IsActiveGuild(guild.Id));
			logger.LogWarning("WeaponSkinsBOT is in multiple Discord servers; only {Guild} ({GuildId}) is active", active.Name, active.Id);
		}
	}

	private bool ClaimGuild(ulong guildId)
	{
		lock (commandGuildSync)
		{
			if (activeGuildId == 0)
				activeGuildId = guildId;
			return activeGuildId == guildId;
		}
	}

	private bool IsActiveGuild(ulong guildId)
	{
		lock (commandGuildSync)
			return activeGuildId == guildId;
	}

	private async Task OnMessage(SocketMessage raw)
	{
		if (raw is not SocketUserMessage message || message.Author.IsBot || message.Channel is not SocketGuildChannel guildChannel)
			return;
		if (!IsActiveGuild(guildChannel.Guild.Id))
			return;
        if (!message.Content.StartsWith('!'))
            return;

        var args = CommandLine.Split(message.Content[1..].Trim());
        if (args.Count == 0)
            return;

        var command = args[0].ToLowerInvariant();
        args.RemoveAt(0);
        if (!KnownCommands.Contains(command))
            return;

        var token = Guid.NewGuid().ToString("N")[..10];
        lock (promptSync)
        {
            foreach (var stale in prompts.Where(pair => pair.Value.CreatedAt < DateTimeOffset.UtcNow.AddMinutes(-10)).Select(pair => pair.Key).ToList())
                prompts.Remove(stale);
            prompts[token] = new Pending(guildChannel.Guild.Id, message.Author.Id, command, args, DateTimeOffset.UtcNow);
        }

        try
        {
            await message.DeleteAsync();
        }
        catch
        {
            // No Manage Messages permission, the command stays in the channel.
        }

        var opener = await message.Channel.SendMessageAsync(
            embed: new EmbedBuilder()
                .WithTitle("WeaponSkins")
                .WithDescription($"{message.Author.Mention} press **Open** for `!{command}`. Only you will see it.")
				.WithFooter(Branding)
                .Build(),
            components: new ComponentBuilder().WithButton("Open", $"open:{token}", ButtonStyle.Primary).Build());

        _ = ExpireOpener(opener);
    }

    private static async Task ExpireOpener(IUserMessage opener)
    {
        await Task.Delay(TimeSpan.FromMinutes(2));
        try
        {
            await opener.DeleteAsync();
        }
        catch
        {
            // Already gone, nothing to clean up.
        }
    }

    private async Task RunPrefixAsync(SocketMessageComponent component, Pending job)
    {
        await component.DeferAsync(ephemeral: true);

        Payload payload;
        try
        {
            payload = await BuildPayload(job.GuildId, job.UserId, job.Command, job.Args);
        }
        catch (Exception ex)
        {
            payload = new Payload($"WeaponSkins error: {Safe(ex.Message)}", null, null);
        }

        await component.FollowupAsync(
            text: payload.Text,
            embed: payload.Embed,
            components: payload.Components,
            ephemeral: true);
    }

    /// <summary>Runs a ! command and hands back whatever should be shown privately.</summary>
	private async Task<Payload> BuildPayload(ulong guildId, ulong userId, string command, List<string> args)
	{
		switch (command)
		{
			case "help":
			case "wshelp":
                return new Payload(null, HelpEmbed(), null);

            case "link":
            {
                if (args.Count < 1)
                    return Say("Usage: `!link ABCD-1234`, using the code `!link` gives you inside the CS2 server.");
				// Codes are short lived, this makes guessing them pointless as well.
                var wait = LinkCooldown(userId);
                if (wait > 0)
                    return Say($"Too many link attempts. Wait {wait} seconds and try again.");

				var result = await database.CompleteLinkAsync(userId, args[0]);
                return Say(result.Message);
            }

            case "unlink":
            {
				var removed = await database.UnlinkAsync(userId);
                return Say(removed
                    ? "Steam account unlinked. Your saved skins stay, link again with `!link` to use them from Discord."
                    : "Your Discord account is not linked.");
            }
        }

        var context = await LinkedContext(guildId, userId);
        if (!context.Ok)
            return Say(context.Error);

        switch (command)
        {
            case "me":
                return Say($"Linked SteamID: `{context.SteamId}`");

            case "loadout":
                return await LoadoutPayload(context, 0);

            case "skins":
            case "skin":
            {
                var team = TakeTeam(args, TeamTarget.Both);
                var category = args.Count > 0 ? string.Join(" ", args) : "";
                return View(picker.StartSkins(guildId, userId, context.SteamId, team, category));
            }

            case "knife":
            case "knives":
            case "knifes":
            {
                var team = TakeTeam(args, TeamTarget.Both);
                KnifeDef? knife = null;
                if (args.Count > 0)
                {
                    knife = catalog.FindKnife(string.Join(" ", args));
                    if (knife == null)
                        return Say("Knife not found. Use `!knife` on its own to choose from the menu.");
                }
                return View(picker.StartKnife(guildId, userId, context.SteamId, team, knife));
            }

            case "gloves":
            case "glove":
                return View(picker.StartGloves(guildId, userId, context.SteamId, TakeTeam(args, TeamTarget.Both)));

            case "agents":
            case "agent":
                return View(picker.StartAgents(guildId, userId, context.SteamId, TakeTeam(args, TeamTarget.Both)));

            case "music":
            case "mk":
                return View(picker.StartMusic(guildId, userId, context.SteamId));

            case "pins":
            case "pin":
                return View(picker.StartPins(guildId, userId, context.SteamId));

            case "wear":
            case "float":
            {
                if (!TryWeaponTeamValue(args, out var weapon, out var team, out var raw) ||
                    !float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var wear) || wear is < 0 or > 1)
                    return Say("Usage: `!wear <weapon> [t|ct|both] <0-1>`");
                var ok = await context.Db.SetWearAsync(context.SteamId, TeamFor(weapon, team), weapon.DefIndex, wear, catalog);
                return Say(ok ? "Wear changed." : "Choose a skin for that weapon first.");
            }

            case "seed":
            case "pattern":
            {
                if (!TryWeaponTeamValue(args, out var weapon, out var team, out var raw) ||
                    !int.TryParse(raw, out var seed) || seed is < 0 or > 1000)
                    return Say("Usage: `!seed <weapon> [t|ct|both] <0-1000>`");
                var ok = await context.Db.SetSeedAsync(context.SteamId, TeamFor(weapon, team), weapon.DefIndex, seed);
                return Say(ok ? "Pattern changed." : "Choose a skin for that weapon first.");
            }

            case "nametag":
            case "tag":
            {
                if (!TryWeaponTeamValue(args, out var weapon, out var team, out var text))
                    return Say("Usage: `!nametag <weapon> [t|ct|both] <text|->` (quote names with spaces)");
                var tag = text == "-" ? null : text.Length > 64 ? text[..64] : text;
                var ok = await context.Db.SetNameTagAsync(context.SteamId, TeamFor(weapon, team), weapon.DefIndex, tag);
                return Say(ok ? "Name tag changed." : "Choose a skin for that weapon first.");
            }

            case "stattrak":
            case "st":
            {
                if (!TryWeaponTeamValue(args, out var weapon, out var team, out var raw) || !TryBool(raw, out var enabled))
                    return Say("Usage: `!stattrak <weapon> [t|ct|both] <on|off>`");
                var ok = await context.Db.SetStatTrakAsync(context.SteamId, TeamFor(weapon, team), weapon.DefIndex, enabled);
                return Say(ok ? $"StatTrak {(enabled ? "enabled" : "disabled")}." : "Choose a skin for that weapon first.");
            }

            case "stickers":
            case "sticker":
            {
                if (args.Count == 0)
                    return Say($"Usage: `!stickers <weapon> [t|ct|both] [slot 1-{PickerService.StickerSlots}|all] [search]`");

                if (!(await context.Db.PermissionsAsync(context.SteamId)).Stickers)
                    return Say(StickersDenied);

                var team = TakeTeam(args, TeamTarget.Both);
                var slot = TakeSlot(args);
                var weaponText = args.Count > 0 ? string.Join(" ", args) : "";
                var weapon = catalog.FindWeapon(weaponText);
                if (weapon == null)
                    return Say("Weapon not found, or it does not take stickers.");
                return View(picker.StartStickers(guildId, userId, context.SteamId, TeamFor(weapon, team), weapon.DefIndex, slot, ""));
            }

            case "g":
            case "gen":
            {
                if (args.Count == 0)
                    return Say("Usage: `!g [t|ct|both] <inspect code>`");
                var team = TeamTarget.Both;
                if (TeamTargetExtensions.TryParse(args[0], out var parsed))
                {
                    team = parsed;
                    args.RemoveAt(0);
                }
                if (!EconItemPreview.TryParse(string.Join(" ", args), out var item))
                    return Say("Invalid inspect code.");

                // Two separate rules in the plugin: whether gen may be used at all,
                // and whether the stickers on the craft come with it.
                var allowed = await context.Db.PermissionsAsync(context.SteamId);
                if (!allowed.Gen)
                    return Say(GenDenied);

                var stripped = !allowed.Stickers && (item.Stickers.Count > 0 || item.Keychains.Count > 0);
                var name = await context.Db.ApplyGenAsync(context.SteamId, team, item, catalog, allowed.Stickers);
                return Say($"Applied **{name}**." + (stripped ? " | Stickers and charms were skipped, They are disabled on the server." : ""));
            }
        }

        return Say("Unknown command. Try `!help`.");
    }

    private readonly Dictionary<ulong, List<DateTimeOffset>> linkAttempts = [];

    private int LinkCooldown(ulong userId)
    {
        const int limit = 5;
        var window = TimeSpan.FromMinutes(1);
        var now = DateTimeOffset.UtcNow;

        lock (promptSync)
        {
            if (!linkAttempts.TryGetValue(userId, out var recent))
                linkAttempts[userId] = recent = [];

            recent.RemoveAll(stamp => now - stamp > window);
            if (recent.Count >= limit)
                return Math.Max(1, (int)(window - (now - recent[0])).TotalSeconds);

            recent.Add(now);
            return 0;
        }
    }

    private static Payload Say(string text) => new(text, null, null);

    private static Payload View(PickerView view) => new(null, view.Embed, view.Components);

    /// <summary>Pulls a team word out of the arguments wherever the player put it.</summary>
    private static TeamTarget TakeTeam(List<string> args, TeamTarget fallback)
    {
        for (var i = 0; i < args.Count; i++)
        {
            if (!TeamTargetExtensions.TryParse(args[i], out var parsed))
                continue;
            args.RemoveAt(i);
            return parsed;
        }
        return fallback;
    }

    private static int TakeSlot(List<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i].Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                args.RemoveAt(i);
                return -1;
            }
            if (int.TryParse(args[i], out var slot) && slot >= 1 && slot <= PickerService.StickerSlots)
            {
                args.RemoveAt(i);
                return slot - 1;
            }
        }
        return 0;
    }

    /// <summary>A weapon only one side can carry never needs the team asking.</summary>
    private TeamTarget TeamFor(WeaponDef weapon, TeamTarget requested)
    {
        var fixedTeam = catalog.TeamOf(weapon.DefIndex);
        return fixedTeam == TeamTarget.Both ? requested : fixedTeam;
    }

    private const int LoadoutPageSize = 18;

    /// <summary>The whole loadout, paged, so nothing hides behind "and N more".</summary>
    private async Task<Payload> LoadoutPayload(Linked context, int page)
    {
        var data = await context.Db.GetLoadoutAsync(context.SteamId);
        var entries = LoadoutEntries(data);
        var pages = Paginate(entries, LoadoutPageSize);
        page = Math.Clamp(page, 0, pages.Count - 1);

        var embed = new EmbedBuilder()
            .WithTitle("Your loadout")
            .WithDescription($"SteamID `{context.SteamId}`\n\n{string.Join("\n", pages[page])}")
			.WithFooter($"Page {page + 1}/{pages.Count} • {entries.Count} item(s) • {Branding}")
            .Build();

        if (pages.Count == 1)
            return new Payload(null, embed, null);

        var components = new ComponentBuilder()
            .WithButton("Previous", $"loadout:{page - 1}", ButtonStyle.Secondary, disabled: page <= 0)
            .WithButton("Next", $"loadout:{page + 1}", ButtonStyle.Secondary, disabled: page >= pages.Count - 1)
            .Build();

        return new Payload(null, embed, components);
    }

    private List<(string Section, string Line)> LoadoutEntries(WeaponSkinsDatabase.LoadoutSummary data)
    {
        var entries = new List<(string, string)>();

        foreach (var team in new[] { 2, 3 })
        {
            var section = team == 2 ? "T" : "CT";
            var before = entries.Count;

            if (data.Knives.TryGetValue(team, out var knife) && knife > 0)
                entries.Add((section, $"**Knife** {catalog.WeaponName(knife)}"));

            if (data.Gloves.TryGetValue(team, out var gloves) && gloves.Def > 0)
            {
                var paint = catalog.FindPaint(gloves.Def, gloves.Paint);
                entries.Add((section, $"**Gloves** {catalog.WeaponName(gloves.Def)}{(paint == null ? "" : $" | {paint.Name}")}"));
            }

            if (data.Agents.TryGetValue(team, out var model) && model.Length > 0)
            {
                var agent = (team == 2 ? catalog.AgentsT : catalog.AgentsCT).FirstOrDefault(item => item.Model == model);
                entries.Add((section, $"**Agent** {agent?.Name ?? model}"));
            }

            if (data.Weapons.TryGetValue(team, out var weapons))
            {
                foreach (var entry in weapons.OrderBy(item => catalog.WeaponName(item.Def), StringComparer.OrdinalIgnoreCase))
                {
                    var paint = catalog.FindPaint(entry.Def, entry.Paint);
                    entries.Add((section, $"{catalog.WeaponName(entry.Def)} | {paint?.Name ?? entry.Paint.ToString()}"));
                }
            }

            if (entries.Count == before)
                entries.Add((section, "*nothing set*"));
        }

        var music = catalog.MusicKits.FirstOrDefault(item => item.Id == data.Music);
        var pin = catalog.Pins.FirstOrDefault(item => item.Id == data.Pin);
        entries.Add(("Profile", $"**Music** {music?.Name ?? "default"}"));
        entries.Add(("Profile", $"**Pin** {pin?.Name ?? "none"}"));
        return entries;
    }

    /// <summary>Chunks lines, repeating a heading when its section runs onto the next page.</summary>
    private static List<List<string>> Paginate(List<(string Section, string Line)> entries, int perPage)
    {
        var pages = new List<List<string>>();
        var current = new List<string>();
        string? heading = null;

        foreach (var entry in entries)
        {
            if (current.Count >= perPage)
            {
                pages.Add(current);
                current = [];
                heading = null;
            }

            if (entry.Section != heading)
            {
                heading = entry.Section;
                current.Add($"__**{heading}**__");
            }

            current.Add(entry.Line);
        }

        if (current.Count > 0)
            pages.Add(current);
        if (pages.Count == 0)
            pages.Add(["*nothing set*"]);

        return pages;
    }

    private async Task OnSlash(SocketSlashCommand command)
    {
        if (command.GuildId == null)
        {
            await command.RespondAsync("Use this command inside a Discord server.", ephemeral: true);
            return;
        }

		var guildId = command.GuildId.Value;
		if (!IsActiveGuild(guildId))
		{
			await command.RespondAsync("This WeaponSkins bot is assigned to another Discord server.", ephemeral: true);
			return;
		}

        try
        {
            // Slash and ! run the exact same code, so the two can never drift apart.
            var args = command.CommandName switch
            {
                "skins" or "skin" => SlashArgs(command, "category", "team"),
                "knife" or "knives" or "knifes" => SlashArgs(command, "weapon", "team"),
                "gloves" or "agents" => SlashArgs(command, "team"),
                "wear" or "seed" => SlashArgs(command, "weapon", "team", "value"),
                "nametag" => SlashArgs(command, "weapon", "team", "text"),
                "stattrak" => SlashArgs(command, "weapon", "team", "enabled"),
                "stickers" => SlashArgs(command, "weapon", "team", "slot"),
                "gen" => SlashArgs(command, "team", "code"),
                "link" => SlashArgs(command, "code"),
                _ => []
            };

            // A gen code or a skin write is a full MySQL transaction, which can take
            // longer than the three seconds Discord allows for a first answer.
            await command.DeferAsync(ephemeral: true);

            var payload = await BuildPayload(guildId, command.User.Id, command.CommandName, args);
            await command.FollowupAsync(
                text: payload.Text,
                embed: payload.Embed,
                components: payload.Components,
                ephemeral: true);
        }
        catch (Exception ex)
        {
            if (command.HasResponded)
                await command.FollowupAsync($"WeaponSkins error: {Safe(ex.Message)}", ephemeral: true);
            else
                await command.RespondAsync($"WeaponSkins error: {Safe(ex.Message)}", ephemeral: true);
        }
    }

    private static List<string> SlashArgs(SocketSlashCommand command, params string[] names)
    {
        var args = new List<string>();
        foreach (var name in names)
        {
            var text = TextOption(command, name, false);
            if (!string.IsNullOrWhiteSpace(text))
                args.Add(text);
        }
        return args;
    }

	private async Task OnButton(SocketMessageComponent component)
	{
		if (component.GuildId == null || !IsActiveGuild(component.GuildId.Value))
			return;

        if (component.Data.CustomId.StartsWith("pick:", StringComparison.Ordinal))
        {
            await picker.HandleButtonAsync(component);
            return;
        }

        if (component.Data.CustomId.StartsWith("loadout:", StringComparison.Ordinal))
        {
            if (!int.TryParse(component.Data.CustomId[8..], out var page))
                return;

            await component.DeferAsync();
            var linked = await LinkedContext(component.GuildId ?? 0, component.User.Id);
            if (!linked.Ok)
            {
                await component.FollowupAsync(linked.Error, ephemeral: true);
                return;
            }

            var paged = await LoadoutPayload(linked, page);
            await component.ModifyOriginalResponseAsync(message =>
            {
                message.Embed = paged.Embed;
                message.Components = paged.Components;
            });
            return;
        }

        if (component.Data.CustomId.StartsWith("open:", StringComparison.Ordinal))
        {
            var token = component.Data.CustomId[5..];
            Pending? job;
            lock (promptSync)
            {
                prompts.TryGetValue(token, out job);
                if (job != null && job.UserId == component.User.Id)
                    prompts.Remove(token);
            }

            if (job == null)
            {
                await component.RespondAsync("That menu expired, type the command again.", ephemeral: true);
                return;
            }

            if (job.UserId != component.User.Id)
            {
                await component.RespondAsync("That menu belongs to someone else, type the command yourself.", ephemeral: true);
                return;
            }

            await RunPrefixAsync(component, job);
            try
            {
                await component.Message.DeleteAsync();
            }
            catch
            {
                // The opener is already gone or cannot be deleted here.
            }
            return;
        }

	}

	private async Task OnModal(SocketModal modal)
	{
		if (modal.GuildId == null || !IsActiveGuild(modal.GuildId.Value))
			return;

		await picker.HandleModalAsync(modal);
	}

	private async Task<Linked> LinkedContext(ulong guildId, ulong discordId)
	{
		var steamId = await database.GetSteamIdAsync(discordId);
		if (!steamId.HasValue)
			return new Linked(false, guildId, 0, database, "Your Steam account is not linked. Join the CS2 server, type `!link`, then use `/link CODE` here.");
		return new Linked(true, guildId, steamId.Value, database, "");
	}

    private sealed record Linked(bool Ok, ulong GuildId, ulong SteamId, WeaponSkinsDatabase Db, string Error);

    private bool TryWeaponTeamValue(List<string> args, out WeaponDef weapon, out TeamTarget team, out string value)
    {
        weapon = null!;
        team = TeamTarget.Both;
        value = "";
        if (args.Count < 2)
            return false;

        team = TakeTeam(args, TeamTarget.Both);
        if (args.Count < 2)
            return false;

        value = args[^1];
        weapon = catalog.FindAnyWeapon(string.Join(" ", args.Take(args.Count - 1)))!;
        return weapon != null;
    }

    private static bool TryBool(string value, out bool result)
    {
        if (value.Equals("on", StringComparison.OrdinalIgnoreCase) || value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1")
        {
            result = true;
            return true;
        }
        if (value.Equals("off", StringComparison.OrdinalIgnoreCase) || value.Equals("false", StringComparison.OrdinalIgnoreCase) || value == "0")
        {
            result = false;
            return true;
        }
        result = false;
        return false;
    }

    private static string TextOption(SocketSlashCommand command, string name, bool required = true)
    {
        var option = command.Data.Options.FirstOrDefault(item => item.Name == name);
        if (option == null || option.Value == null)
        {
            if (required)
                throw new InvalidOperationException($"Missing `{name}`.");
            return "";
        }
        return Convert.ToString(option.Value, CultureInfo.InvariantCulture) ?? "";
    }

	private static Embed HelpEmbed() => new EmbedBuilder()
        .WithTitle("WeaponSkins_Bot")
        .WithDescription("Link your Steam account and manage WeaponSkins from Discord. Slash commands and `!` commands are supported.")
        .AddField("Link", "Join CS2 → type `!link` → use `/link CODE` or `!link CODE` here. `/unlink` undoes it.")
        .AddField("Cosmetics", "`/skins`, `/knife`, `/gloves`, `/agents`, `/music`, `/pins`, `/stickers`, `/loadout`")
		.AddField("Settings", "`/wear`, `/seed`, `/nametag`, `/stattrak`, `/gen`\nSkins ask for pattern, wear, StatTrak and stickers before saving.")
		.WithFooter(Branding)
		.Build();

    private IEnumerable<SlashCommandBuilder> BuildSlashCommands()
    {
		yield return new SlashCommandBuilder().WithName("help").WithDescription("Show WeaponSkins_Bot commands");
        yield return new SlashCommandBuilder().WithName("me").WithDescription("Show your linked SteamID");
        yield return new SlashCommandBuilder().WithName("loadout").WithDescription("Show everything you have equipped");
        yield return new SlashCommandBuilder().WithName("link").WithDescription("Link your Steam account")
            .AddOption(new SlashCommandOptionBuilder().WithName("code").WithDescription("Code from !link in CS2").WithType(ApplicationCommandOptionType.String).WithRequired(true));
        yield return new SlashCommandBuilder().WithName("unlink").WithDescription("Unlink your Steam account");

        foreach (var alias in new[] { "skins", "skin" })
            yield return SkinCommand(alias);
        foreach (var alias in new[] { "knife", "knives", "knifes" })
            yield return KnifeCommand(alias);

        yield return TeamOnly("gloves", "Choose gloves");
        yield return TeamOnly("agents", "Choose an agent");
        yield return new SlashCommandBuilder().WithName("music").WithDescription("Choose a music kit");
        yield return new SlashCommandBuilder().WithName("pins").WithDescription("Choose a pin");
        yield return WeaponValueCommand("wear", "Change skin wear", ApplicationCommandOptionType.Number, "Wear from 0 to 1");
        yield return WeaponValueCommand("seed", "Change skin pattern", ApplicationCommandOptionType.Integer, "Pattern from 0 to 1000");

        yield return WeaponTeamBase("nametag", "Change a weapon name tag")
            .AddOption(new SlashCommandOptionBuilder().WithName("text").WithDescription("Name tag or - to remove").WithType(ApplicationCommandOptionType.String).WithRequired(true));
        yield return WeaponTeamBase("stattrak", "Enable or disable StatTrak")
            .AddOption(new SlashCommandOptionBuilder().WithName("enabled").WithDescription("Enable StatTrak").WithType(ApplicationCommandOptionType.Boolean).WithRequired(true));
        yield return WeaponTeamBase("stickers", "Choose stickers, with search in the menu")
            .AddOption(SlotOption());
        yield return new SlashCommandBuilder().WithName("gen").WithDescription("Apply a CS2 inspect code")
            .AddOption(TeamOption())
            .AddOption(new SlashCommandOptionBuilder().WithName("code").WithDescription("Inspect code or preview URL").WithType(ApplicationCommandOptionType.String).WithRequired(true));
    }

    private SlashCommandBuilder SkinCommand(string name)
    {
        var builder = new SlashCommandBuilder().WithName(name).WithDescription("Choose a weapon skin");
        var category = new SlashCommandOptionBuilder().WithName("category").WithDescription("Weapon category").WithType(ApplicationCommandOptionType.String).WithRequired(false);
        foreach (var item in catalog.Categories.Take(25))
            category.AddChoice(item, item);
        builder.AddOption(category);
        builder.AddOption(TeamOption());
        return builder;
    }

    private static SlashCommandBuilder KnifeCommand(string name) => new SlashCommandBuilder().WithName(name).WithDescription("Choose a knife and skin")
        .AddOption(TeamOption())
        .AddOption(new SlashCommandOptionBuilder().WithName("weapon").WithDescription("Optional knife name, e.g. Karambit").WithType(ApplicationCommandOptionType.String).WithRequired(false));

    private static SlashCommandBuilder TeamOnly(string name, string description) => new SlashCommandBuilder().WithName(name).WithDescription(description).AddOption(TeamOption());

    private static SlashCommandBuilder WeaponTeamBase(string name, string description) => new SlashCommandBuilder().WithName(name).WithDescription(description)
        .AddOption(new SlashCommandOptionBuilder().WithName("weapon").WithDescription("Weapon name").WithType(ApplicationCommandOptionType.String).WithRequired(true))
        .AddOption(TeamOption());

    private static SlashCommandBuilder WeaponValueCommand(string name, string description, ApplicationCommandOptionType type, string valueDescription) =>
        WeaponTeamBase(name, description).AddOption(new SlashCommandOptionBuilder().WithName("value").WithDescription(valueDescription).WithType(type).WithRequired(true));

    // Optional: a weapon only one team can carry picks its own side, and the rest
    // default to both, so nobody has to answer a question they do not care about.
    private static SlashCommandOptionBuilder TeamOption() => new SlashCommandOptionBuilder()
        .WithName("team").WithDescription("T, CT, or Both (skipped for one-team weapons)").WithType(ApplicationCommandOptionType.String).WithRequired(false)
        .AddChoice("Both", "both").AddChoice("T", "t").AddChoice("CT", "ct");

    private static SlashCommandOptionBuilder SlotOption()
    {
        var slot = new SlashCommandOptionBuilder()
            .WithName("slot").WithDescription($"Sticker slot 1-{PickerService.StickerSlots}, or all of them")
            .WithType(ApplicationCommandOptionType.String).WithRequired(false)
            .AddChoice("All slots", "all");
        for (var index = 1; index <= PickerService.StickerSlots; index++)
            slot.AddChoice($"Slot {index}", index.ToString());
        return slot;
    }

	private static string Safe(string text) => text.Length <= 500 ? text : text[..500];
}
