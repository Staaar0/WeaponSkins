using System.Collections;
using System.Reflection;
using CounterStrikeSharp.API.Core.Capabilities;
using Microsoft.Extensions.Logging;

namespace WeaponSkins;

public sealed class DiscordUtilitiesLinkProvider
{
	private const string CapabilityName = "discord_utilities";
	private const string ApiTypeName = "DiscordUtilitiesAPI.IDiscordUtilitiesAPI";
	private const int RetryMs = 1000;
	private const int WarningDelayMs = 5000;
	private const int WarningCooldownMs = 60000;

	private readonly ILogger logger;
	private object? api;
	private MethodInfo? isDatabaseLoaded;
	private MethodInfo? getLinkedPlayers;
	private long firstAttempt;
	private long nextAttempt;
	private long nextWarning;

	public DiscordUtilitiesLinkProvider(ILogger logger)
	{
		this.logger = logger;
	}

	public bool TryIsLinked(ulong steamId, out bool linked)
	{
		linked = false;
		if (!Connect())
			return false;

		try
		{
			if (isDatabaseLoaded!.Invoke(api, null) is not true)
			{
				if (Environment.TickCount64 - firstAttempt >= WarningDelayMs)
					Warn("linking_method is Discord-Utilities, but its database is not available");
				return false;
			}

			if (getLinkedPlayers!.Invoke(api, null) is not IDictionary players)
				return false;

			linked = players.Contains(steamId);
			return true;
		}
		catch (Exception ex)
		{
			Reset();
			Warn($"Discord Utilities link check failed: {ex.GetBaseException().Message}");
			return false;
		}
	}

	private bool Connect()
	{
		if (api != null)
			return true;

		var now = Environment.TickCount64;
		if (firstAttempt == 0)
			firstAttempt = now;
		if (now < nextAttempt)
			return false;

		nextAttempt = now + RetryMs;
		foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
		{
			var apiType = assembly.GetType(ApiTypeName, false);
			if (apiType == null)
				continue;

			try
			{
				var capabilityType = typeof(PluginCapability<>).MakeGenericType(apiType);
				var capability = Activator.CreateInstance(capabilityType, CapabilityName);
				var value = capabilityType.GetMethod("Get")?.Invoke(capability, null);
				var databaseMethod = apiType.GetMethod("IsDatabaseLoaded");
				var playersMethod = apiType.GetMethod("GetLinkedPlayers");
				if (value == null || databaseMethod == null || playersMethod == null)
					continue;

				api = value;
				isDatabaseLoaded = databaseMethod;
				getLinkedPlayers = playersMethod;
				return true;
			}
			catch (TargetInvocationException ex) when (ex.InnerException is KeyNotFoundException)
			{
				continue;
			}
			catch (Exception ex)
			{
				Warn($"Discord Utilities API connection failed: {ex.GetBaseException().Message}");
			}
		}

		if (now - firstAttempt >= WarningDelayMs)
			Warn("linking_method is Discord-Utilities, but the Discord Utilities API is not available");

		return false;
	}

	private void Reset()
	{
		api = null;
		isDatabaseLoaded = null;
		getLinkedPlayers = null;
		nextAttempt = Environment.TickCount64 + RetryMs;
	}

	private void Warn(string message)
	{
		var now = Environment.TickCount64;
		if (now < nextWarning)
			return;

		nextWarning = now + WarningCooldownMs;
		logger.LogWarning("{Message}", message);
	}
}
