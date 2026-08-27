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
	public override string ModuleName => "WeaponSkins";
	public override string ModuleAuthor => "✪ Stαr";
	public override string ModuleVersion => "1.1.0";
	public override string ModuleDescription => "Gives players full control over how their loadout looks";

	public SkinsConfig Config { get; set; } = new();
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
	private CancellationTokenSource? statTrakFlushCancellation;
	private Task? statTrakFlushTask;
	private long nextDatabaseErrorLog;
	private int suppressedDatabaseErrors;
	private bool initialized;
	private volatile bool stopping;

	public void OnConfigParsed(SkinsConfig config)
	{
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
		UpdateConfigNotes();
	}

	private void UpdateConfigNotes()
	{
		try
		{
			var path = Config.GetConfigPath();
			if (!File.Exists(path) || !string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase))
				return;

			var original = File.ReadAllLines(path);
			var lines = original.Where(line => !line.Contains("\"linking_method_note\"", StringComparison.Ordinal)).ToArray();
			var changed = lines.Length != original.Length;
			for (var i = 0; i < lines.Length; i++)
			{
				var trimmed = lines[i].TrimStart();
				string? note = null;
				if (trimmed.StartsWith("\"linking_method\"", StringComparison.Ordinal))
					note = "link options 1=WeaponSkinsBOT 2=Discord-Utilities";
				else if (trimmed.StartsWith("\"discord_bot_token\"", StringComparison.Ordinal))
					note = "Discord bot will start/stop with cs2 server";

				if (note == null)
					continue;

				var comment = lines[i].IndexOf("//", StringComparison.Ordinal);
				var value = (comment < 0 ? lines[i] : lines[i][..comment]).TrimEnd();
				var updated = $"{value} // {note}";
				changed |= lines[i] != updated;
				lines[i] = updated;
			}

			if (changed)
				File.WriteAllLines(path, lines);
		}
		catch (Exception ex)
		{
			Logger.LogWarning("Could not add the config notes: {Error}", ex.GetBaseException().Message);
		}
	}

	public override void Load(bool hotReload)
	{
		EconAttributes.Init(Logger);

		Db = new Database(Config.Database);
		Catalog = new CatalogService(Logger, Path.Combine(ModuleDirectory, "Data"));
		Store = new LoadoutStore(Db);
		Cache = new PlayerCache(Store, Logger);
		Applier = new WeaponApplier(this, Cache, Catalog);
		GloveApply = new GloveService(this, Cache);
		Profile = new ProfileService(this, Cache);
		Menu = new MenuRenderer(Config.Menu, Localizer);
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

		if (!Links.UsesDiscordUtilities && Config.LinkRequired && !Db.Configured)
			Logger.LogWarning("link_required is enabled but the database is not configured, Discord linking stays off");
		else if (!Links.UsesDiscordUtilities && Config.LinkRequired && !HasDiscordBotToken)
			Logger.LogWarning("link_required is enabled but discord_bot_token is not configured, WeaponSkinsBOT cannot come online");

		if (!Links.UsesDiscordUtilities && HasDiscordBotToken && !Db.Configured)
			Logger.LogWarning("discord_bot_token is configured but the database is not, WeaponSkinsBOT stays offline");

		if (Db.Configured)
		{
			statTrakFlushCancellation = new CancellationTokenSource();
			statTrakFlushTask = FlushStatTrakLoop(statTrakFlushCancellation.Token);
		}

		if (Db.Configured)
			bootstrapTask = BootstrapAndLoad();
		else
		{
			Logger.LogWarning("Database is not configured, loadouts will not be saved");
			FinishLoad(hotReload);
		}

		if (!Catalog.LoadBlocking(Config.Api, Db.Configured))
			Logger.LogWarning("Item data is not loaded yet, retrying on map start");
		else
			Menus.Prewarm();
	}

	private async Task BootstrapAndLoad()
	{
		try
		{
			await Db.Bootstrap(Links.CanIssueCodes, DiscordBotEnabled);
			discordBot?.DatabaseReady();
		}
		catch (Exception ex)
		{
			discordBot?.DatabaseUnavailable();
			if (!stopping)
				Logger.LogCritical("Database bootstrap failed: {Error}", ex.GetBaseException().Message);
			return;
		}

		Server.NextFrame(() =>
		{
			if (!stopping)
				FinishLoad(true);
		});

		await BackfillPermissions();
	}

	private async Task BackfillPermissions()
	{
		if (!Links.CanIssueCodes || stopping)
			return;

		List<ulong> linked;
		try
		{
			linked = await LinkStore.ReadLinked(CancellationToken.None);
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

			Save(LinkStore.SavePermissions(rows, CancellationToken.None));
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

		lock (permissionSync)
		{
			if (publishedPermissions.TryGetValue(steamId, out var previous) && previous == (stickers, gen))
				return;

			publishedPermissions[steamId] = (stickers, gen);
		}

		Save(LinkStore.SavePermissions(steamId, stickers, gen, CancellationToken.None));
	}

	public void ForgetPermissions(ulong steamId)
	{
		lock (permissionSync)
			publishedPermissions.Remove(steamId);
	}

	public bool CanUseSkins(CCSPlayerController player)
	{
		return !Links.Required || Links.IsLinked(player.SteamID);
	}

	public override void Unload(bool hotReload)
	{
		stopping = true;
		events?.Unregister();
		statTrakFlushCancellation?.Cancel();
		discordBot?.Stop();
		Links.Stop();
		Cache.Clear();
		Applier.Dispose();
		Menu.CloseAll();

		try
		{
			bootstrapTask?.GetAwaiter().GetResult();
		}
		catch (Exception ex)
		{
			Logger.LogError("Database bootstrap shutdown failed: {Error}", ex.GetBaseException().Message);
		}

		try
		{
			statTrakFlushTask?.GetAwaiter().GetResult();
		}
		catch (OperationCanceledException)
		{
		}

		try
		{
			Store.FlushStatTrak().GetAwaiter().GetResult();
			Store.Stop().GetAwaiter().GetResult();
		}
		catch (Exception ex)
		{
			Logger.LogError("Database shutdown failed: {Error}", ex.GetBaseException().Message);
		}
		finally
		{
			statTrakFlushCancellation?.Dispose();
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
				await Store.FlushStatTrak();
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

	private bool HasVip(ulong steamId)
	{
		return AdminManager.PlayerHasPermissions(new SteamID(steamId), Config.Stickers.VipFlag);
	}

	public string Text(string key, params object[] args)
	{
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
