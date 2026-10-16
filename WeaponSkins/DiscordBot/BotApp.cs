using SharedEconPreview = global::WeaponSkins.EconItemPreview;
using System.Globalization;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using WeaponSkinsBot.Catalog;
using WeaponSkinsBot.Database;
using WeaponSkinsBot.Discord;

namespace WeaponSkinsBot;

public sealed partial class BotApp
{
	private const string Branding = "WeaponSkins | By ✪ Stαr";
	private readonly string token;
    private readonly Func<string, object[], string>? translate;
    private string T(string key, params object[] args) => translate?.Invoke(key, args) ?? DiscordEnglish.Text(key, args);
    private string Description(string key) => DiscordText.Short(T(key), 100);
	private readonly global::WeaponSkins.Database sourceDatabase;
	private readonly string moduleDirectory;
	private readonly global::WeaponSkins.ApiConfig apiConfig;
	private readonly ILogger logger;
	private readonly Action connected;
    private readonly Action<ulong> loadoutChanged;
    private readonly Func<ulong, CancellationToken, Task<WeaponSkinsDatabase.Permissions>> livePermissions;
    private readonly global::WeaponSkins.LoadoutStore? mutationStore;
    private CancellationTokenSource? lifetime;
    private CancellationToken cancellationToken;
    private InteractionDispatcher dispatcher = null!;
    private readonly SemaphoreSlim botOperations = new(4, 4);
	private DiscordSocketClient client = null!;
	private CatalogService catalog = null!;
	private PickerService picker = null!;
	private WeaponSkinsDatabase database = null!;
	private int ready;
	private int shuttingDown;
	private ulong activeGuildId;
	private bool extraGuildsLogged;

    // Servers registered this run. Reconnects do not register them again.
    private readonly HashSet<ulong> commandGuilds = [];

    private readonly object commandGuildSync = new();

    private ApplicationCommandProperties[]? slashCommands;

    private ApplicationCommandProperties[] SlashCommands =>
        slashCommands ??= BuildSlashCommands().Select(builder => (ApplicationCommandProperties)builder.Build()).ToArray();

    // Prefix commands use a button to open a private reply for the caller.
    private sealed record Pending(ulong GuildId, ulong UserId, string Command, List<string> Args, DateTimeOffset CreatedAt);

    private sealed record Payload(string? Text, Embed? Embed, MessageComponent? Components);

    private readonly Dictionary<string, Pending> prompts = [];
    private readonly HashSet<string> pendingOpeners = [];
    private readonly HashSet<Task> openerCleanup = [];
    private const int MaxOpeners = 512;
    private static readonly TimeSpan OpenerLifetime = TimeSpan.FromMinutes(2);
    private readonly object promptSync = new();

	private static readonly HashSet<string> KnownCommands = new(StringComparer.OrdinalIgnoreCase)
	{
		"help", "wshelp", "link", "unlink", "me", "loadout",
        "skins", "skin", "knife", "knives", "knifes", "gloves", "glove",
        "agents", "agent", "music", "mk", "pins", "pin",
        "wear", "float", "seed", "pattern", "nametag", "tag",
        "stattrak", "st", "stickers", "sticker", "g", "gen"
    };

    private string StickersDenied => T("discord.stickers_denied");

    private string GenDenied => T("discord.gen_denied");

	public BotApp(
		string token,
		global::WeaponSkins.Database database,
		string moduleDirectory,
		global::WeaponSkins.ApiConfig apiConfig,
		ILogger logger,
		Action connected,
        Action<ulong> loadoutChanged,
        Func<ulong, CancellationToken, Task<WeaponSkinsDatabase.Permissions>> livePermissions,
        global::WeaponSkins.LoadoutStore? mutationStore = null,
        Func<string, object[], string>? translate = null)
	{
		this.token = token;
		sourceDatabase = database;
		this.moduleDirectory = moduleDirectory;
		this.apiConfig = apiConfig;
		this.logger = logger;
		this.connected = connected;
        this.loadoutChanged = loadoutChanged;
        this.livePermissions = livePermissions;
        this.mutationStore = mutationStore;
        this.translate = translate ?? new global::WeaponSkins.PluginTranslations(moduleDirectory, apiConfig.Language, logger).Text;
	}

	public async Task RunAsync(CancellationToken cancellationToken)
	{
		lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		this.cancellationToken = lifetime.Token;
		try
		{
            dispatcher = new InteractionDispatcher(logger, this.cancellationToken);
            database = new WeaponSkinsDatabase(sourceDatabase, OnLoadoutChanged, this.cancellationToken, livePermissions, mutationStore, translate);
            ownedWeapons = new OwnedWeaponCache(database.GetOwnedWeaponsAsync,
                ex => logger.LogWarning(ex, "WeaponSkinsBOT could not refresh weapon suggestions"), this.cancellationToken);
			catalog = new CatalogService(
				Path.Combine(moduleDirectory, "BotData", "catalog"),
				Path.Combine(moduleDirectory, "Data"),
				apiConfig);
			await catalog.LoadAsync(this.cancellationToken);
			picker = new PickerService(catalog, database, this.cancellationToken, botOperations, translate);

			client = new DiscordSocketClient(new DiscordSocketConfig
			{
				GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildMessages | GatewayIntents.MessageContent,
				LogGatewayIntentWarnings = false,
				MessageCacheSize = 20
			});

			client.Log += OnLog;
			client.Ready += DispatchReady;
			client.JoinedGuild += DispatchJoined;
			client.LeftGuild += DispatchLeft;
			client.MessageReceived += DispatchMessage;
			client.SlashCommandExecuted += DispatchSlash;
			client.ButtonExecuted += DispatchButton;
			client.SelectMenuExecuted += DispatchSelect;
			client.ModalSubmitted += DispatchModal;
            client.AutocompleteExecuted += DispatchAutocomplete;

			await client.LoginAsync(TokenType.Bot, token).WaitAsync(this.cancellationToken);
			this.cancellationToken.ThrowIfCancellationRequested();
			await client.StartAsync().WaitAsync(this.cancellationToken);
			await Task.Delay(Timeout.InfiniteTimeSpan, this.cancellationToken);
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

        lifetime?.Cancel();
        var drained = dispatcher?.StopAsync() ?? Task.CompletedTask;
        Task cleanup;
        lock (promptSync)
        {
            prompts.Clear();
            cleanup = Task.WhenAll(openerCleanup.ToArray());
        }

		if (client != null)
		{
			client.Log -= OnLog;
			client.Ready -= DispatchReady;
			client.JoinedGuild -= DispatchJoined;
			client.LeftGuild -= DispatchLeft;
			client.MessageReceived -= DispatchMessage;
			client.SlashCommandExecuted -= DispatchSlash;
			client.ButtonExecuted -= DispatchButton;
			if (picker != null)
			{
				client.SelectMenuExecuted -= DispatchSelect;
				client.ModalSubmitted -= DispatchModal;
                client.AutocompleteExecuted -= DispatchAutocomplete;
			}

			// Disconnect first; callbacks may be waiting for the game thread.
            await WithTimeout(client.StopAsync(), TimeSpan.FromSeconds(2));
            await WithTimeout(client.LogoutAsync(), TimeSpan.FromSeconds(1));
			try { client.Dispose(); }
            catch (Exception ex) { logger.LogWarning(ex, "WeaponSkinsBOT client disposal failed"); }
		}

		var allWork = Task.WhenAll(drained, cleanup, ownedWeapons?.StopAsync() ?? Task.CompletedTask);
        await WithTimeout(allWork, TimeSpan.FromSeconds(2));
		catalog?.Dispose();
        if (allWork.IsCompleted) lifetime?.Dispose();
        else _ = allWork.ContinueWith(_ => lifetime?.Dispose(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
	}

	private static async Task WithTimeout(Task task, TimeSpan timeout)
	{
		try
		{
			await task.WaitAsync(timeout);
		}
		catch
		{
            // Observe errors even if cleanup finishes after the timeout.
            _ = task.ContinueWith(failed => _ = failed.Exception,
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
		}
	}

    // Registers commands in the active server and removes them from other servers.
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

    // Registers commands once per server during this run.
    private async Task RegisterGuildCommands(SocketGuild guild)
    {
        lock (commandGuildSync)
        {
            if (!commandGuilds.Add(guild.Id))
                return;
        }

        try
        {
            await guild.BulkOverwriteApplicationCommandAsync(SlashCommands, options: new RequestOptions { CancelToken = cancellationToken });
        }
        catch (Exception ex)
        {
            // Retry registration on the next reconnect.
            lock (commandGuildSync)
                commandGuilds.Remove(guild.Id);

			logger.LogWarning("WeaponSkinsBOT could not register commands for {Guild} ({GuildId}): {Error}", guild.Name, guild.Id, ex.Message);
		}
	}

	private async Task ClearGuildCommands(SocketGuild guild)
	{
		try
		{
			await guild.BulkOverwriteApplicationCommandAsync([], options: new RequestOptions { CancelToken = cancellationToken });
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
            // Remove old global commands. Commands belong to the active server.
            await client.BulkOverwriteGlobalApplicationCommandsAsync([], options: new RequestOptions { CancelToken = cancellationToken });
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
        var now = DateTimeOffset.UtcNow;
        lock (promptSync)
        {
            foreach (var stale in prompts.Where(pair => now - pair.Value.CreatedAt >= OpenerLifetime).Select(pair => pair.Key).ToArray())
                prompts.Remove(stale);
            // Count used prompts until their cleanup finishes.
            if (pendingOpeners.Count >= MaxOpeners || cancellationToken.IsCancellationRequested)
                return;
            pendingOpeners.Add(token);
            prompts[token] = new Pending(guildChannel.Guild.Id, message.Author.Id, command, args, now);
        }

        try
        {
            try
            {
                await message.DeleteAsync(options: new RequestOptions { CancelToken = cancellationToken });
            }
            catch when (!cancellationToken.IsCancellationRequested)
            {
                // Without Manage Messages permission, the command stays visible.
            }

            cancellationToken.ThrowIfCancellationRequested();
            var opener = await message.Channel.SendMessageAsync(
                embed: new EmbedBuilder()
                    .WithTitle("WeaponSkins")
                    .WithDescription(T("discord.open_prompt", message.Author.Mention, command))
                    .WithFooter(Branding)
                    .Build(),
                components: new ComponentBuilder().WithButton(T("discord.open"), $"open:{token}", ButtonStyle.Primary).Build(),
                options: new RequestOptions { CancelToken = cancellationToken });

            var cleanup = ExpireOpener(token, opener);
            lock (promptSync) openerCleanup.Add(cleanup);
            _ = cleanup.ContinueWith(completed =>
            {
                lock (promptSync) openerCleanup.Remove(completed);
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        catch
        {
            lock (promptSync)
            {
                prompts.Remove(token);
                pendingOpeners.Remove(token);
            }
            throw;
        }
    }

    private async Task ExpireOpener(string token, IUserMessage opener)
    {
        try
        {
            await Task.Delay(OpenerLifetime, cancellationToken);
            await opener.DeleteAsync(options: new RequestOptions { CancelToken = cancellationToken });
        }
        catch
        {
            // The prompt was removed, or the bot is stopping.
        }
        finally
        {
            lock (promptSync)
            {
                prompts.Remove(token);
                pendingOpeners.Remove(token);
            }
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
            if (cancellationToken.IsCancellationRequested) return;
            logger.LogError(ex, "WeaponSkinsBOT prefix command {Command} failed", job.Command);
            payload = new Payload(ex is InvalidOperationException or ArgumentException ? Safe(ex.Message) : T("discord.request_failed"), null, null);
        }

        await component.FollowupAsync(
            text: payload.Text,
            embed: payload.Embed,
            components: payload.Components,
            ephemeral: true);
    }

	private async Task<Payload> BuildPayload(ulong guildId, ulong userId, string command, List<string> args)
    {
        // Reply before waiting for database access.
        await botOperations.WaitAsync(cancellationToken);
        try { return await BuildPayloadCore(guildId, userId, command, args); }
        finally { botOperations.Release(); }
    }

    private async Task<Payload> BuildPayloadCore(ulong guildId, ulong userId, string command, List<string> args)
	{
		switch (command)
		{
			case "help":
			case "wshelp":
                return new Payload(null, HelpEmbed(), null);

            case "link":
            {
                if (args.Count < 1)
                    return Say(T("discord.link_usage"));
				// Limit link attempts while the code is valid.
                var wait = LinkCooldown(userId);
                if (wait > 0)
                    return Say(T("discord.link_cooldown", wait));

				var result = await database.CompleteLinkAsync(userId, args[0]);
                if (result.Success) ownedWeapons.Prime(userId, result.SteamId);
                return Say(result.Message);
            }


        }

        var context = await LinkedContext(guildId, userId);
        if (!context.Ok)
            return Say(context.Error);

        switch (command)
        {
            case "unlink":
                return View(picker.StartUnlink(guildId, userId, context.SteamId));

            case "me":
                return Say(T("discord.linked_steam", context.SteamId));

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
                        return Say(T("discord.knife_not_found"));
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
                    !float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var wear) || !float.IsFinite(wear) || wear is < 0 or > 1)
                    return Say(T("discord.wear_usage"));
                var ok = await context.Db.SetWearAsync(context.SteamId, TeamFor(weapon, team), weapon.DefIndex, wear, catalog);
                return Say(ok ? T("discord.wear_changed") : T("discord.choose_skin"));
            }

            case "seed":
            case "pattern":
            {
                if (!TryWeaponTeamValue(args, out var weapon, out var team, out var raw) ||
                    !int.TryParse(raw, out var seed) || seed is < 0 or > 1000)
                    return Say(T("discord.seed_usage"));
                var ok = await context.Db.SetSeedAsync(context.SteamId, TeamFor(weapon, team), weapon.DefIndex, seed);
                return Say(ok ? T("discord.pattern_changed") : T("discord.choose_skin"));
            }

            case "nametag":
            case "tag":
            {
                if (!TryWeaponTeamValue(args, out var weapon, out var team, out var text))
                    return Say(T("discord.nametag_usage"));
                var tag = text == "-" ? null : text.Length > 64 ? text[..64] : text;
                var ok = await context.Db.SetNameTagAsync(context.SteamId, TeamFor(weapon, team), weapon.DefIndex, tag);
                return Say(ok ? T("discord.nametag_changed") : T("discord.choose_skin"));
            }

            case "stattrak":
            case "st":
            {
                if (!TryWeaponTeamValue(args, out var weapon, out var team, out var raw) || !TryBool(raw, out var enabled))
                    return Say(T("discord.stattrak_usage"));
                var ok = await context.Db.SetStatTrakAsync(context.SteamId, TeamFor(weapon, team), weapon.DefIndex, enabled);
                return Say(ok ? (enabled ? T("discord.stattrak_enabled") : T("discord.stattrak_disabled")) : T("discord.choose_skin"));
            }

            case "stickers":
            case "sticker":
            {
                if (args.Count == 0)
                    return Say(T("discord.stickers_usage", PickerService.StickerSlots));

                if (!(await context.Db.PermissionsAsync(context.SteamId)).Stickers)
                    return Say(StickersDenied);

                var allSlots = args.RemoveAll(x => x.Equals("all", StringComparison.OrdinalIgnoreCase)) > 0;
                var team = TakeTeam(args, TeamTarget.Both);
                var slot = allSlots ? -1 : TakeSlot(args);
                var weaponText = args.Count > 0 ? string.Join(" ", args) : "";
                var weapon = catalog.FindWeapon(weaponText);
                if (weapon == null)
                    return Say(T("discord.weapon_not_found"));
                return View(picker.StartStickers(guildId, userId, context.SteamId, TeamFor(weapon, team), weapon.DefIndex, slot, ""));
            }

            case "g":
            case "gen":
            {
                if (args.Count == 0)
                    return Say(T("discord.gen_usage"));
                var team = TeamTarget.Both;
                if (TeamTargetExtensions.TryParse(args[0], out var parsed))
                {
                    team = parsed;
                    args.RemoveAt(0);
                }
                if (!SharedEconPreview.TryParse(string.Join(" ", args), out var item))
                    return Say(T("discord.inspect_invalid"));

                // Check gen-code access and sticker access separately.
                var allowed = await context.Db.PermissionsAsync(context.SteamId);
                if (!allowed.Gen)
                    return Say(GenDenied);

                var stripped = !allowed.Stickers && (item.Stickers.Count > 0 || item.Keychains.Count > 0);
                var name = await context.Db.ApplyGenAsync(context.SteamId, team, item, catalog, allowed.Stickers);
                return Say(T("discord.applied", name) + (stripped ? T("discord.decorations_skipped") : ""));
            }
        }

        return Say(T("discord.unknown_command"));
    }

    private readonly Dictionary<ulong, List<DateTimeOffset>> linkAttempts = [];
    private DateTimeOffset nextLinkPrune;

    private int LinkCooldown(ulong userId)
    {
        const int limit = 5;
        var window = TimeSpan.FromMinutes(1);
        var now = DateTimeOffset.UtcNow;

        lock (promptSync)
        {
            if (now >= nextLinkPrune || linkAttempts.Count >= 1024)
            {
                foreach (var expired in linkAttempts.Where(pair => pair.Value.Count == 0 || now - pair.Value[^1] >= window).Select(pair => pair.Key).ToArray())
                    linkAttempts.Remove(expired);
                nextLinkPrune = now.Add(window);
            }
            if (!linkAttempts.TryGetValue(userId, out var recent))
            {
                // Keep active cooldowns so repeated guesses cannot reset them.
                if (linkAttempts.Count >= 1024)
                    return (int)window.TotalSeconds;
                linkAttempts[userId] = recent = [];
            }

            recent.RemoveAll(stamp => now - stamp > window);
            if (recent.Count >= limit)
                return Math.Max(1, (int)(window - (now - recent[0])).TotalSeconds);

            recent.Add(now);
            return 0;
        }
    }

    private static Payload Say(string text) => new(text, null, null);

    private static Payload View(PickerView view) => new(null, view.Embed, view.Components);

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
        for (var i = 1; i < args.Count; i++)
        {
            if (args[i].Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                args.RemoveAt(i);
                return -1;
            }
            if (int.TryParse(args[i], out var slot) && slot >= 1 && slot <= PickerService.StickerSlots)
            {
                args.RemoveAt(i);
                return slot;
            }
        }
        return 0;
    }

    // Skips the team choice for weapons available to one team.
    private TeamTarget TeamFor(WeaponDef weapon, TeamTarget requested)
    {
        var fixedTeam = catalog.TeamOf(weapon.DefIndex);
        return fixedTeam == TeamTarget.Both ? requested : fixedTeam;
    }

    private const int LoadoutPageSize = 18;

    private async Task<Payload> LoadoutPayload(Linked context, int page)
    {
        var data = await context.Db.GetLoadoutAsync(context.SteamId);
        var entries = LoadoutEntries(data);
        var pages = Paginate(entries, LoadoutPageSize);
        page = Math.Clamp(page, 0, pages.Count - 1);

        var embed = new EmbedBuilder()
            .WithTitle(T("discord.your_loadout"))
            .WithDescription($"SteamID `{context.SteamId}`\n\n{string.Join("\n", pages[page])}")
			.WithFooter(T("discord.loadout_footer", page + 1, pages.Count, entries.Count, Branding))
            .Build();

        if (pages.Count == 1)
            return new Payload(null, embed, null);

        var components = new ComponentBuilder()
            .WithButton(T("discord.previous"), $"loadout:{page - 1}", ButtonStyle.Secondary, disabled: page <= 0)
            .WithButton(T("discord.next"), $"loadout:{page + 1}", ButtonStyle.Secondary, disabled: page >= pages.Count - 1)
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
                entries.Add((section, $"**{T("menu.knife")}** {catalog.WeaponName(knife)}"));

            if (data.Gloves.TryGetValue(team, out var gloves) && gloves.Def > 0)
            {
                var paint = catalog.FindPaint(gloves.Def, gloves.Paint);
                entries.Add((section, $"**{T("menu.gloves")}** {catalog.WeaponName(gloves.Def)}{(paint == null ? "" : $" | {paint.Name}")}"));
            }

            if (data.Agents.TryGetValue(team, out var model) && model.Length > 0)
            {
                var agent = (team == 2 ? catalog.AgentsT : catalog.AgentsCT).FirstOrDefault(item => item.Model == model);
                entries.Add((section, $"**{T("discord.agent")}** {agent?.Name ?? model}"));
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
                entries.Add((section, T("discord.nothing_set")));
        }

        var music = catalog.MusicKits.FirstOrDefault(item => item.Id == data.Music);
        var pin = catalog.Pins.FirstOrDefault(item => item.Id == data.Pin);
        entries.Add((T("discord.profile"), $"**{T("menu.music")}** {music?.Name ?? T("menu.default")}"));
        entries.Add((T("discord.profile"), $"**{T("menu.pins")}** {pin?.Name ?? T("discord.none")}"));
        return entries;
    }

    // Splits lines into pages and repeats section headings.
    private List<List<string>> Paginate(List<(string Section, string Line)> entries, int perPage)
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
            pages.Add([T("discord.nothing_set")]);

        return pages;
    }

    private async Task OnSlash(SocketSlashCommand command)
    {
        if (command.GuildId == null)
        {
            await command.RespondAsync(T("discord.guild_only"), ephemeral: true);
            return;
        }

		var guildId = command.GuildId.Value;
		if (!IsActiveGuild(guildId))
		{
			await command.RespondAsync(T("discord.another_server"), ephemeral: true);
			return;
		}

        try
        {
            // Use the same handler for slash and prefix commands.
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

            // Defer before saving; Discord allows only three seconds for the first reply.
            await command.DeferAsync(ephemeral: true);

            var payload = await BuildPayload(guildId, command.User.Id, command.CommandName, args);
            await command.FollowupAsync(
                text: payload.Text,
                embed: payload.Embed,
                components: payload.Components,
                ephemeral: true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (cancellationToken.IsCancellationRequested) return;
            logger.LogError(ex, "WeaponSkinsBOT slash command {Command} failed", command.CommandName);
            var error = ex is InvalidOperationException or ArgumentException ? Safe(ex.Message) : T("discord.request_failed");
            if (command.HasResponded)
                await command.FollowupAsync(error, ephemeral: true);
            else
                await command.RespondAsync(error, ephemeral: true);
        }
    }

    private List<string> SlashArgs(SocketSlashCommand command, params string[] names)
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
            await botOperations.WaitAsync(cancellationToken);
            try
            {
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
            finally { botOperations.Release(); }
        }

        if (component.Data.CustomId.StartsWith("open:", StringComparison.Ordinal))
        {
            var token = component.Data.CustomId[5..];
            Pending? job;
            lock (promptSync)
            {
                prompts.TryGetValue(token, out job);
                if (job != null && DateTimeOffset.UtcNow - job.CreatedAt >= OpenerLifetime)
                {
                    prompts.Remove(token);
                    job = null;
                }
                if (job != null && job.UserId == component.User.Id)
                    prompts.Remove(token);
            }

            if (job == null)
            {
                await component.RespondAsync(T("discord.opener_expired"), ephemeral: true);
                return;
            }

            if (job.UserId != component.User.Id)
            {
                await component.RespondAsync(T("discord.opener_owned"), ephemeral: true);
                return;
            }

            await RunPrefixAsync(component, job);
            try
            {
                await component.Message.DeleteAsync();
            }
            catch
            {
                // The prompt is gone or cannot be deleted.
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
			return new Linked(false, guildId, 0, database, T("discord.link_required"));
		ownedWeapons.Prime(discordId, steamId.Value);
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

        // Treat the last argument as the value, even if it looks like a team.
        value = args[^1];
        var weaponArgs = args.Take(args.Count - 1).ToList();
        weapon = catalog.FindAnyWeapon(string.Join(" ", weaponArgs))!;
        if (weapon != null) return true;

        // Resolve the weapon before checking for a numeric team.
        for (var i = weaponArgs.Count - 1; weaponArgs.Count > 1 && i >= 0; i--)
        {
            if (!TeamTargetExtensions.TryParse(weaponArgs[i], out var parsed)) continue;
            weapon = catalog.FindAnyWeapon(string.Join(" ", weaponArgs.Where((_, index) => index != i)))!;
            if (weapon == null) continue;
            team = parsed;
            return true;
        }
        // Also accept the optional team after the value.
        if (args.Count > 2 && TeamTargetExtensions.TryParse(args[^1], out var trailingTeam))
        {
            weapon = catalog.FindAnyWeapon(string.Join(" ", args.Take(args.Count - 2)))!;
            if (weapon != null)
            {
                team = trailingTeam;
                value = args[^2];
                return true;
            }
        }
        return false;
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

    private string TextOption(SocketSlashCommand command, string name, bool required = true)
    {
        var option = command.Data.Options.FirstOrDefault(item => item.Name == name);
        if (option == null || option.Value == null)
        {
            if (required)
                throw new InvalidOperationException(T("discord.missing_option", name));
            return "";
        }
        return Convert.ToString(option.Value, CultureInfo.InvariantCulture) ?? "";
    }

	private Embed HelpEmbed() => new EmbedBuilder()
        .WithTitle("WeaponSkins_Bot")
        .WithDescription(T("discord.help_intro"))
        .AddField(T("discord.link"), T("discord.help_link"))
        .AddField(T("discord.cosmetics"), "`/skins`, `/knife`, `/gloves`, `/agents`, `/music`, `/pins`, `/stickers`, `/loadout`")
		.AddField(T("discord.settings"), T("discord.help_settings"))
		.WithFooter(Branding)
		.Build();

    private IEnumerable<SlashCommandBuilder> BuildSlashCommands()
    {
		yield return new SlashCommandBuilder().WithName("help").WithDescription(Description("discord.desc_help"));
        yield return new SlashCommandBuilder().WithName("me").WithDescription(Description("discord.desc_me"));
        yield return new SlashCommandBuilder().WithName("loadout").WithDescription(Description("discord.desc_loadout"));
        yield return new SlashCommandBuilder().WithName("link").WithDescription(Description("discord.desc_link"))
            .AddOption(new SlashCommandOptionBuilder().WithName("code").WithDescription(Description("discord.desc_code")).WithType(ApplicationCommandOptionType.String).WithRequired(true));
        yield return new SlashCommandBuilder().WithName("unlink").WithDescription(Description("discord.desc_unlink"));

        foreach (var alias in new[] { "skins", "skin" })
            yield return SkinCommand(alias);
        foreach (var alias in new[] { "knife", "knives", "knifes" })
            yield return KnifeCommand(alias);

        yield return TeamOnly("gloves", T("discord.desc_gloves"));
        yield return TeamOnly("agents", T("discord.desc_agents"), false);
        yield return new SlashCommandBuilder().WithName("music").WithDescription(Description("discord.desc_music"));
        yield return new SlashCommandBuilder().WithName("pins").WithDescription(Description("discord.desc_pins"));
        yield return new SlashCommandBuilder().WithName("pin").WithDescription(Description("discord.desc_pins"));
        yield return WeaponValueCommand("wear", T("discord.desc_wear"), ApplicationCommandOptionType.Number, T("discord.desc_wear_value"));
        yield return WeaponValueCommand("seed", T("discord.desc_seed"), ApplicationCommandOptionType.Integer, T("discord.desc_seed_value"));

        yield return WeaponTeamBase("nametag", T("discord.desc_nametag"))
            .AddOption(new SlashCommandOptionBuilder().WithName("text").WithDescription(Description("discord.desc_tag_value")).WithType(ApplicationCommandOptionType.String).WithRequired(true));
        yield return WeaponTeamBase("stattrak", T("discord.desc_stattrak"))
            .AddOption(new SlashCommandOptionBuilder().WithName("enabled").WithDescription(Description("discord.desc_stattrak_value")).WithType(ApplicationCommandOptionType.Boolean).WithRequired(true));
        yield return WeaponTeamBase("stickers", T("discord.desc_stickers"));
        yield return new SlashCommandBuilder().WithName("gen").WithDescription(Description("discord.desc_gen"))
            .AddOption(new SlashCommandOptionBuilder().WithName("code").WithDescription(Description("discord.desc_inspect_value")).WithType(ApplicationCommandOptionType.String).WithRequired(true));
    }

    private SlashCommandBuilder SkinCommand(string name) =>
        new SlashCommandBuilder().WithName(name).WithDescription(Description("discord.desc_skins")).AddOption(TeamOption(false));

    private SlashCommandBuilder KnifeCommand(string name) =>
        new SlashCommandBuilder().WithName(name).WithDescription(Description("discord.desc_knife")).AddOption(TeamOption());

    private SlashCommandBuilder TeamOnly(string name, string description, bool both = true) =>
        new SlashCommandBuilder().WithName(name).WithDescription(DiscordText.Short(description, 100)).AddOption(TeamOption(both));

    private SlashCommandBuilder WeaponTeamBase(string name, string description) =>
        new SlashCommandBuilder().WithName(name).WithDescription(DiscordText.Short(description, 100))
            .AddOption(new SlashCommandOptionBuilder().WithName("weapon").WithDescription(Description("discord.desc_weapon_value"))
                .WithType(ApplicationCommandOptionType.String).WithRequired(true).WithAutocomplete(true));

    private SlashCommandBuilder WeaponValueCommand(string name, string description, ApplicationCommandOptionType type, string valueDescription) =>
        WeaponTeamBase(name, description).AddOption(new SlashCommandOptionBuilder().WithName("value").WithDescription(DiscordText.Short(valueDescription, 100)).WithType(type).WithRequired(true));

    private SlashCommandOptionBuilder TeamOption(bool both = true)
    {
        var option = new SlashCommandOptionBuilder().WithName("team").WithDescription(Description("discord.team"))
            .WithType(ApplicationCommandOptionType.String).WithRequired(true);
        if (both) option.AddChoice(DiscordText.Short(T("discord.team_both"), 100), "both");
        return option.AddChoice(DiscordText.Short(T("discord.team_t"), 100), "t").AddChoice(DiscordText.Short(T("discord.team_ct"), 100), "ct");
    }

	private static string Safe(string text) => DiscordText.Short(text, 500);
}
