using System.Text;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Modules.Extensions;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

namespace WeaponSkins;

[MinimumApiVersion(371)]
public sealed class WeaponSkins : BasePlugin, IPluginConfig<SkinsConfig>
{
    public static WeaponSkins? Instance { get; private set; }
	public override string ModuleName => "WeaponSkins";
	public override string ModuleAuthor => "✪ Stαr";
	public override string ModuleVersion => "1.1.9";
	public override string ModuleDescription => "Gives players full control over how their loadout looks";

	public SkinsConfig Config { get; set; } = new();
	private PluginTranslations? translations;
	public CatalogService Catalog { get; private set; } = null!;
	public Database Db { get; private set; } = null!;
	public LoadoutStore Store { get; private set; } = null!;
	public PlayerCache Cache { get; private set; } = null!;
	public WeaponApplier Applier { get; private set; } = null!;
	public GloveService GloveApply { get; private set; } = null!;
	public ProfileService Profile { get; private set; } = null!;
	public MenuRenderer Menu { get; private set; } = null!;
	public SkinMenus Menus { get; private set; } = null!;
	public LinkService Links { get; private set; } = null!;
	public LinkStore LinkStore { get; private set; } = null!;

	public bool Initialized => initialized;
	public bool Stopping => stopping;

	private static readonly Dictionary<string, char> ColorMap = new(StringComparer.OrdinalIgnoreCase)
	{
		["default"] = '\x01',
		["white"] = '\x01',
		["darkred"] = '\x02',
		["lightpurple"] = '\x03',
		["green"] = '\x04',
		["olive"] = '\x05',
		["lime"] = '\x06',
		["red"] = '\x07',
		["grey"] = '\x08',
		["yellow"] = '\x09',
		["lightyellow"] = '\x09',
		["silver"] = '\x0A',
		["bluegrey"] = '\x0A',
		["blue"] = '\x0B',
		["lightblue"] = '\x0B',
		["darkblue"] = '\x0C',
		["purple"] = '\x0E',
		["magenta"] = '\x0E',
		["lightred"] = '\x0F',
		["gold"] = '\x10',
		["orange"] = '\x10'
	};
	private readonly object actionSync = new();
	private readonly Dictionary<int, long> nextCosmeticAction = [];
	private readonly Dictionary<int, long> nextGenAction = [];
	private readonly object databaseErrorSync = new();
	private Events? events;
	private Commands? commands;
	private DiscordBotCoordinator? discordBot;
	private Task? bootstrapTask;
	private Task? catalogTask;
	private Task? shutdownTask;
	private Task? previousShutdownTask;
	private readonly CancellationTokenSource lifetimeCancellation = new();
	private CancellationToken lifetimeToken;
	internal CancellationToken LifetimeToken => lifetimeToken;
	private CancellationTokenSource? statTrakFlushCancellation;
	private Task? statTrakFlushTask;
	private long nextDatabaseErrorLog;
	private int suppressedDatabaseErrors;
	private bool initialized;
	private volatile bool stopping;
	private const string PendingShutdownKey = "WeaponSkins.PendingShutdown";

	public void OnConfigParsed(SkinsConfig config)
	{
		config.Version = Math.Max(config.Version, 6);
		config.Menu ??= new MenuConfig();
		config.Api ??= new ApiConfig();
		config.Api.Language = PluginTranslations.NormalizeLanguage(config.Api.Language);
		if (ConfigMigration.IsOriginalApi(config.Api.BaseUrl))
			config.Api.BaseUrl = ApiConfig.DefaultBaseUrl;
		if (config.LinkingMethod == "2" || string.Equals(config.LinkingMethod, "Discord-Utilities", StringComparison.OrdinalIgnoreCase))
			config.LinkingMethod = "Discord-Utilities";
		else if (config.LinkingMethod == "1" || string.Equals(config.LinkingMethod, "WeaponSkinsBOT", StringComparison.OrdinalIgnoreCase))
			config.LinkingMethod = "WeaponSkinsBOT";
		else
		{
			Logger.LogWarning("Unknown linking_method '{Method}', using WeaponSkinsBOT", config.LinkingMethod);
			config.LinkingMethod = "WeaponSkinsBOT";
		}

		Config = config;
		translations = new PluginTranslations(ModuleDirectory, config.Api.Language, Logger);
		UpdateConfigFile();
	}

	private void UpdateConfigFile()
	{
		string? temporary = null;
		try
		{
			var path = Config.GetConfigPath();
			if (!File.Exists(path) || !string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase))
				return;

			// Update known defaults and keep the server's other settings.
			var originalBytes = File.ReadAllBytes(path);
			using var reader = new StreamReader(new MemoryStream(originalBytes), Encoding.UTF8, true);
			var original = reader.ReadToEnd();
			var updated = ConfigMigration.UpdateJson(original);
			if (original == updated)
				return;

			var encoding = reader.CurrentEncoding;
			var preamble = encoding.GetPreamble();
			var keepPreamble = preamble.Length > 0 && originalBytes.AsSpan().StartsWith(preamble);
			var payload = encoding.GetBytes(updated);
			var updatedBytes = keepPreamble ? preamble.Concat(payload).ToArray() : payload;
			temporary = path + ".tmp." + Guid.NewGuid().ToString("N");
			File.WriteAllBytes(temporary, updatedBytes);
			if (!OperatingSystem.IsWindows())
				File.SetUnixFileMode(temporary, File.GetUnixFileMode(path));

			if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(originalBytes))
			{
				Logger.LogWarning("Config changed during migration; leaving it untouched until the next load");
				return;
			}
			File.Move(temporary, path, overwrite: true);
			temporary = null;
		}
		catch (Exception ex)
		{
			Logger.LogWarning("Could not update the config: {Error}", ex.GetBaseException().Message);
		}
		finally
		{
			if (temporary != null)
				try { File.Delete(temporary); } catch { }
		}
	}

	public override void Load(bool hotReload)
	{
		lifetimeToken = lifetimeCancellation.Token;
		Task? previousShutdown;
		lock (AppDomain.CurrentDomain)
			previousShutdown = AppDomain.CurrentDomain.GetData(PendingShutdownKey) as Task;
		previousShutdownTask = previousShutdown;
        Instance = this;
		EconAttributes.Init(Logger);

		Db = new Database(Config.Database);
		Catalog = new CatalogService(Logger, Path.Combine(ModuleDirectory, "Data"),
			(action, token) => Server.NextWorldUpdateAsync(() =>
			{
				if (!stopping)
					action();
			}).WaitAsync(token));
		Store = new LoadoutStore(Db);
		Store.WriteFailed += LogDatabaseError;
		Cache = new PlayerCache(Store, Logger);
		Applier = new WeaponApplier(this, Cache, Catalog);
		GloveApply = new GloveService(this, Cache);
		Profile = new ProfileService(this, Cache);
		Menu = new MenuRenderer(Config.Menu, Localizer, key => Text(key));
		Menus = new SkinMenus(this);
		LinkStore = new LinkStore(Db);
		Links = new LinkService(this, LinkStore);
		if (DiscordBotEnabled)
		{
			discordBot = new DiscordBotCoordinator(this, Db, Config.DiscordBotToken.Trim());
			discordBot.Start();
		}
		commands = new Commands(this);
		commands.RegisterLink();
		events = new Events(this);
		events.RegisterPrecache();
		var commandsPrewarm = Task.Run(() => commands.PrepareHandlers(LifetimeToken));

		if (!Links.UsesDiscordUtilities && Config.LinkRequired && !Db.Configured)
			Logger.LogWarning("link_required is enabled but the database is not configured, Discord linking stays off");
		else if (!Links.UsesDiscordUtilities && Config.LinkRequired && !HasDiscordBotToken)
			Logger.LogWarning("link_required is enabled but discord_bot_token is not configured, WeaponSkinsBOT cannot come online");

		if (!Links.UsesDiscordUtilities && HasDiscordBotToken && !Db.Configured)
			Logger.LogWarning("discord_bot_token is configured but the database is not, WeaponSkinsBOT stays offline");

		// Load bundled data first; install the online catalog later.
		var catalogLoaded = Catalog.LoadBundled(LifetimeToken);
		try
		{
			commandsPrewarm.GetAwaiter().GetResult();
			if (catalogLoaded)
			{
				var defaultLabel = Text("menu.default");
				var installMenus = Task.Run(() => Menus.PrepareCatalogMenus(defaultLabel)).GetAwaiter().GetResult();
				installMenus();
			}
		}
		catch (Exception ex)
		{
			Logger.LogWarning("Command preparation failed: {Error}", ex.GetBaseException().Message);
		}

		if (!catalogLoaded)
			Logger.LogWarning("Item data is not loaded yet, retrying on map start");
		else
			Menus.Prewarm();

		Catalog.Changed += OnCatalogChanged;
		if (Db.Configured)
			bootstrapTask = Task.Run(() => StartAfterPreviousShutdown(previousShutdown, hotReload, LifetimeToken));
		else
		{
			Logger.LogWarning("Database is not configured, loadouts will not be saved");
			if (previousShutdown is { IsCompleted: false })
				bootstrapTask = Task.Run(() => StartAfterPreviousShutdown(previousShutdown, hotReload, LifetimeToken));
			else
				FinishLoad(hotReload);
		}

		if (Db.Configured && !string.IsNullOrWhiteSpace(Config.Api.BaseUrl))
			catalogTask = Task.Run(() => RefreshCatalog(LifetimeToken));
	}

	public override void OnAllPluginsLoaded(bool hotReload)
	{
		Menu.Bind(NativeMenuProvider.Find(Config.Menu.MenuStyle, Logger));
	}

	private async Task StartAfterPreviousShutdown(Task? previousShutdown, bool hotReload, CancellationToken cancellationToken)
	{
		if (previousShutdown != null)
		{
			if (!previousShutdown.IsCompleted)
				Logger.LogInformation("Waiting for the previous WeaponSkins database writes to finish before enabling this instance");
			try { await previousShutdown.WaitAsync(cancellationToken); }
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
			catch (Exception ex)
			{
				Logger.LogWarning("Previous plugin shutdown reported an error: {Error}", ex.GetBaseException().Message);
			}
		}
		if (Db.Configured)
			await BootstrapAndLoad(cancellationToken);
		else
			Server.NextWorldUpdate(() => { if (!stopping) FinishLoad(hotReload); });
	}

	private void OnCatalogChanged()
	{
		if (stopping)
			return;
		Menu.CloseAll();
		Menus.InvalidateCaches();
		Menus.Prewarm();
	}

	private async Task RefreshCatalog(CancellationToken cancellationToken)
	{
		try
		{
			await Catalog.LoadAsync(Config.Api, Db.Configured, cancellationToken);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
		}
		catch (Exception ex)
		{
			Logger.LogWarning("Item data refresh failed: {Error}", ex.GetBaseException().Message);
		}
	}

	private async Task BootstrapAndLoad(CancellationToken cancellationToken)
	{
		var retrySeconds = 5;
		while (!cancellationToken.IsCancellationRequested)
		{
			try
			{
				using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
				attempt.CancelAfter(TimeSpan.FromMinutes(3));
				await Db.Bootstrap(Links.CanIssueCodes, DiscordBotEnabled, attempt.Token);
				cancellationToken.ThrowIfCancellationRequested();
				discordBot?.DatabaseReady();

				Server.NextWorldUpdate(() =>
				{
					if (!stopping)
					{
						statTrakFlushCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
						statTrakFlushTask = FlushStatTrakLoop(statTrakFlushCancellation.Token);
						FinishLoad(true);
					}
				});

				await BackfillPermissions(cancellationToken);
				return;
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				return;
			}
			catch (Exception ex)
			{
				Logger.LogError("Database bootstrap failed; retrying in {Seconds}s: {Error}", retrySeconds, ex.GetBaseException().Message);
			}
			try { await Task.Delay(TimeSpan.FromSeconds(retrySeconds), cancellationToken); }
			catch (OperationCanceledException) { return; }
			retrySeconds = Math.Min(retrySeconds * 2, 30);
		}
	}

	private async Task BackfillPermissions(CancellationToken cancellationToken)
	{
		if (!Links.CanIssueCodes || stopping)
			return;

		List<ulong> linked;
		try
		{
			linked = await LinkStore.ReadLinked(cancellationToken);
		}
		catch (Exception ex)
		{
			if (!stopping)
				LogDatabaseError(ex.GetBaseException());
			return;
		}

		if (linked.Count == 0 || stopping)
			return;

		Server.NextFrame(() =>
		{
			if (stopping)
				return;

			var rows = new List<(ulong, bool, bool)>(linked.Count);
			foreach (var steamId in linked)
				rows.Add((steamId, StickersAllowed(steamId), GenAllowed(steamId)));

			Save(LinkStore.SavePermissions(rows, cancellationToken));
		});
	}

	private void FinishLoad(bool loadConnectedPlayers)
	{
		if (initialized || stopping)
			return;

		commands!.Register();
		events!.Register();
		Links.Start();
		Profile.Start();
		initialized = true;

		if (loadConnectedPlayers)
		{
			foreach (var player in Utilities.GetPlayers())
			{
				if (player.IsValid && !player.IsBot)
					EnsureLoadout(player.SteamID);
			}
		}
	}

	private readonly Dictionary<ulong, (bool Stickers, bool Gen)> publishedPermissions = [];
	private readonly Dictionary<ulong, ((bool Stickers, bool Gen) Value, long Revision)> pendingPermissions = [];
	private long permissionRevision;
	private readonly object permissionSync = new();

	public void EnsureLoadout(ulong steamId)
	{
		Links.Track(steamId);
		PublishPermissions(steamId);
	}

	private void PublishPermissions(ulong steamId)
	{
		if (!Db.Configured || stopping)
			return;

		var stickers = StickersAllowed(steamId);
		var gen = GenAllowed(steamId);

		long revision;
		lock (permissionSync)
		{
			if (pendingPermissions.TryGetValue(steamId, out var pending))
			{
				if (pending.Value == (stickers, gen))
					return;
			}
			else if (publishedPermissions.TryGetValue(steamId, out var previous) && previous == (stickers, gen))
				return;

			revision = ++permissionRevision;
			pendingPermissions[steamId] = ((stickers, gen), revision);
		}

		Save(PublishPermissionsAsync(steamId, stickers, gen, revision));
	}

	private async Task PublishPermissionsAsync(ulong steamId, bool stickers, bool gen, long revision)
	{
		try
		{
			await Store.RunMutationAsync(steamId,
				() => LinkStore.SavePermissions(steamId, stickers, gen, LifetimeToken), LifetimeToken);
			lock (permissionSync)
			{
				if (pendingPermissions.TryGetValue(steamId, out var pending) && pending.Revision == revision)
				{
					publishedPermissions[steamId] = (stickers, gen);
					pendingPermissions.Remove(steamId);
				}
			}
		}
		finally
		{
			lock (permissionSync)
			{
				if (pendingPermissions.TryGetValue(steamId, out var pending) && pending.Revision == revision)
					pendingPermissions.Remove(steamId);
			}
		}
	}

	public void ForgetPermissions(ulong steamId)
	{
		lock (permissionSync)
		{
			publishedPermissions.Remove(steamId);
			pendingPermissions.Remove(steamId);
		}
	}

	public bool CanUseSkins(CCSPlayerController player)
	{
		return !Links.Required || Links.IsLinked(player.SteamID);
	}

	public override void Unload(bool hotReload)
	{
		stopping = true;
        if (ReferenceEquals(Instance, this)) Instance = null;
		events?.Unregister();
		lifetimeCancellation.Cancel();
		statTrakFlushCancellation?.Cancel();
		discordBot?.RequestStop();
		Links.RequestStop();
		Catalog.Changed -= OnCatalogChanged;
		Cache.Clear();
		Applier.Dispose();
		Menu.CloseAll();
		// Cancel now, then finish saving in the background.
		var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		// Also wait for any earlier plugin shutdown.
		shutdownTask = previousShutdownTask == null
			? completion.Task : Task.WhenAll(previousShutdownTask, completion.Task);
		lock (AppDomain.CurrentDomain)
			AppDomain.CurrentDomain.SetData(PendingShutdownKey, shutdownTask);
		_ = shutdownTask.ContinueWith(completed =>
		{
			if (completed.IsFaulted)
				_ = completed.Exception;
			lock (AppDomain.CurrentDomain)
			{
				if (ReferenceEquals(AppDomain.CurrentDomain.GetData(PendingShutdownKey), completed))
					AppDomain.CurrentDomain.SetData(PendingShutdownKey, null);
			}
		}, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
		_ = Task.Run(async () =>
		{
			Exception? shutdownError = null;
			try
			{
				await ShutdownAsync();
			}
			catch (Exception ex)
			{
				Logger.LogError("Plugin shutdown failed: {Error}", ex.GetBaseException().Message);
				shutdownError = ex;
			}
			// Let old SQL commands finish before the next instance saves.
			try { await Store.DrainCompletion; }
			catch (OperationCanceledException) { }
			catch (Exception ex)
			{
				Logger.LogWarning("Pending database writes ended with an error during shutdown: {Error}", ex.GetBaseException().Message);
			}
			if (shutdownError != null)
				completion.TrySetException(shutdownError);
			else
				completion.TrySetResult();
		});
	}

	private async Task ShutdownAsync()
	{
		using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
		try
		{
			if (discordBot != null)
				await discordBot.StopAsync(deadline.Token);
		}
		catch (Exception ex)
		{
			Logger.LogError("Discord bot shutdown failed: {Error}", ex.GetBaseException().Message);
		}

		try
		{
			await Links.StopAsync(deadline.Token);
		}
		catch (Exception ex)
		{
			Logger.LogError("Discord link shutdown failed: {Error}", ex.GetBaseException().Message);
		}

		try
		{
			await Store.Stop(deadline.Token);
		}
		catch (Exception ex)
		{
			Logger.LogError("Database shutdown failed: {Error}", ex.GetBaseException().Message);
		}
		finally
		{
			await ObserveShutdownTask(bootstrapTask, deadline.Token);
			await ObserveShutdownTask(catalogTask, deadline.Token);
			await ObserveShutdownTask(statTrakFlushTask, deadline.Token);
			statTrakFlushCancellation?.Dispose();
			Store.WriteFailed -= LogDatabaseError;
			lifetimeCancellation.Dispose();
		}
	}

	private async Task ObserveShutdownTask(Task? task, CancellationToken cancellationToken)
	{
		if (task == null)
			return;
		try { await task.WaitAsync(cancellationToken); }
		catch (OperationCanceledException) { }
		catch (Exception ex)
		{
			Logger.LogWarning("Background shutdown failed: {Error}", ex.GetBaseException().Message);
		}
	}

	public bool HasDiscordBotToken =>
		!string.IsNullOrWhiteSpace(Config.DiscordBotToken) &&
		!string.Equals(Config.DiscordBotToken.Trim(), "YOUR_BOT_TOKEN", StringComparison.OrdinalIgnoreCase);

	internal static void PrintDiscordBotLoading()
	{
		Console.WriteLine("\u001b[93m[WeaponSkins] \u001b[97mLoading Discord BOT...\u001b[0m");
	}

	internal static void PrintDiscordBotConnected()
	{
		Console.WriteLine("\u001b[92m[WeaponSkins] \u001b[97mDiscord BOT has been connected!\u001b[0m");
	}

	private bool DiscordBotEnabled =>
		!Links.UsesDiscordUtilities && Db.Configured && HasDiscordBotToken;

	private async Task FlushStatTrakLoop(CancellationToken cancellationToken)
	{
		while (true)
		{
			await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
			try
			{
				await Store.FlushStatTrak(cancellationToken: cancellationToken);
			}
			catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
			{
				LogDatabaseError(ex);
			}
		}
	}

	public void Save(Task task)
	{
		task.ContinueWith(
			t => LogDatabaseError(t.Exception?.GetBaseException()),
			CancellationToken.None,
			TaskContinuationOptions.OnlyOnFaulted,
			TaskScheduler.Default);
	}

	public void LogDatabaseError(Exception? exception)
	{
		var now = Environment.TickCount64;
		lock (databaseErrorSync)
		{
			if (now < nextDatabaseErrorLog)
			{
				suppressedDatabaseErrors++;
				return;
			}

			var suppressed = suppressedDatabaseErrors;
			suppressedDatabaseErrors = 0;
			nextDatabaseErrorLog = now + 5000;
			if (suppressed > 0)
				Logger.LogError("Database write failed: {Error} ({Suppressed} similar errors suppressed)", exception?.Message, suppressed);
			else
				Logger.LogError("Database write failed: {Error}", exception?.Message);
		}
	}

	public bool AllowCosmeticAction(CCSPlayerController player)
	{
		return AllowAction(nextCosmeticAction, player.Slot, 200);
	}

	public bool AllowGenAction(CCSPlayerController player)
	{
		return AllowAction(nextGenAction, player.Slot, 1000);
	}

	private bool AllowAction(Dictionary<int, long> actions, int slot, int cooldownMs)
	{
		var now = Environment.TickCount64;
		lock (actionSync)
		{
			if (actions.TryGetValue(slot, out var next) && now < next)
				return false;

			actions[slot] = now + cooldownMs;
			return true;
		}
	}

	public void DropActionState(int slot)
	{
		lock (actionSync)
		{
			nextCosmeticAction.Remove(slot);
			nextGenAction.Remove(slot);
		}
	}

	public bool StickersAllowed(CCSPlayerController player)
	{
		return Config.Stickers.Enabled && (!Config.Stickers.VipOnly || AdminManager.PlayerHasPermissions(player, Config.Stickers.VipFlag));
	}

	public bool GenAllowed(CCSPlayerController player)
	{
		return !Config.Stickers.VipOnly || AdminManager.PlayerHasPermissions(player, Config.Stickers.VipFlag);
	}

	public bool StickersAllowed(ulong steamId)
	{
		return Config.Stickers.Enabled && (!Config.Stickers.VipOnly || HasVip(steamId));
	}

	public bool GenAllowed(ulong steamId)
	{
		return !Config.Stickers.VipOnly || HasVip(steamId);
	}

	// Read current admin permissions on the game thread.
	internal async Task<WeaponSkinsBot.Database.WeaponSkinsDatabase.Permissions> DiscordPermissionsAsync(
		ulong steamId, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		return await Server.NextWorldUpdateAsync(() =>
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (stopping)
				return new WeaponSkinsBot.Database.WeaponSkinsDatabase.Permissions(false, false);
			return new WeaponSkinsBot.Database.WeaponSkinsDatabase.Permissions(StickersAllowed(steamId), GenAllowed(steamId));
		}).WaitAsync(cancellationToken);
	}

	private bool HasVip(ulong steamId)
	{
		return AdminManager.PlayerHasPermissions(new SteamID(steamId), Config.Stickers.VipFlag);
	}

	public string Text(string key, params object[] args)
	{
		if (translations != null)
			return translations.Text(key, args);
		var value = Localizer[key].Value;
		for (var i = 0; i < args.Length; i++)
			value = value.Replace("{" + i + "}", args[i]?.ToString() ?? "");

		return value;
	}

	public void Reply(CCSPlayerController player, string key, params object[] args)
	{
		player.PrintToChat(" " + Colorize($"{Text("prefix")} {Text(key, args)}"));
	}

	public string Colorize(string text)
	{
		var builder = new StringBuilder(text.Length);
		var i = 0;
		while (i < text.Length)
		{
			if (text[i] == '{')
			{
				var end = text.IndexOf('}', i + 1);
				if (end > i && ColorMap.TryGetValue(text[(i + 1)..end], out var color))
				{
					builder.Append(color);
					i = end + 1;
					continue;
				}
			}

			builder.Append(text[i]);
			i++;
		}

		return builder.ToString();
	}
}
