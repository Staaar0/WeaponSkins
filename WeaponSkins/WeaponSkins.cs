using System.Text;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

namespace WeaponSkins;

[MinimumApiVersion(371)]
public sealed class WeaponSkins : BasePlugin, IPluginConfig<SkinsConfig>
{
	public override string ModuleName => "WeaponSkins";
	public override string ModuleAuthor => "✪ Stαr";
	public override string ModuleVersion => "1.0.0";
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
	private Task? bootstrapTask;
	private CancellationTokenSource? statTrakFlushCancellation;
	private Task? statTrakFlushTask;
	private long nextDatabaseErrorLog;
	private int suppressedDatabaseErrors;
	private bool initialized;
	private volatile bool stopping;

	public void OnConfigParsed(SkinsConfig config)
	{
		Config = config;
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
		Profile = new ProfileService(Cache);
		Menu = new MenuRenderer(Config.Menu, Localizer);
		Menus = new SkinMenus(this);
		events = new Events(this);
		events.RegisterPrecache();

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
			await Db.Bootstrap();
		}
		catch (Exception ex)
		{
			if (!stopping)
				Logger.LogCritical("Database bootstrap failed: {Error}", ex.GetBaseException().Message);
			return;
		}

		Server.NextFrame(() =>
		{
			if (!stopping)
				FinishLoad(true);
		});
	}

	private void FinishLoad(bool loadConnectedPlayers)
	{
		if (initialized || stopping)
			return;

		commands = new Commands(this);
		commands.Register();
		events!.Register();
		initialized = true;

		if (loadConnectedPlayers)
		{
			foreach (var player in Utilities.GetPlayers())
			{
				if (player.IsValid && !player.IsBot)
					Cache.Fetch(player.SteamID);
			}
		}
	}

	public override void Unload(bool hotReload)
	{
		stopping = true;
		events?.Unregister();
		statTrakFlushCancellation?.Cancel();
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

	private void LogDatabaseError(Exception? exception)
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
