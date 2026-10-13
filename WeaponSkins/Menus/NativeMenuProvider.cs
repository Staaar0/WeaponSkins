using System.Reflection;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Menu;
using Microsoft.Extensions.Logging;

namespace WeaponSkins;

public sealed class NativeMenuProvider
{
	private const string ApiTypeName = "MenuManager.IMenuApi";
	private const string CapabilityName = "menu:nfcore";
	private static readonly HashSet<int> warnedBackends = [];

	private readonly object api;
	private readonly MethodInfo createMenu;
	private readonly MethodInfo closeMenu;
	private readonly MethodInfo? addToggle;
	private readonly object? menuType;
	private readonly string providerName;
	private readonly ILogger logger;
	private int failureReported;

	private NativeMenuProvider(object api, MethodInfo createMenu, MethodInfo closeMenu, MethodInfo? addToggle,
		object? menuType, string providerName, ILogger logger)
	{
		this.api = api;
		this.createMenu = createMenu;
		this.closeMenu = closeMenu;
		this.addToggle = addToggle;
		this.menuType = menuType;
		this.providerName = providerName;
		this.logger = logger;
	}

	public static NativeMenuProvider? Find(int backend, ILogger logger)
	{
		if (backend == 1)
			return null;

		if (backend is not (2 or 3))
		{
			WarnOnce(backend, logger, $"Menu backend {backend} is invalid. Using built-in menus.");
			return null;
		}

		var providerName = backend == 2 ? "MenuManagerCS2" : "PanoramaMenuManagerCS2";
		var implementation = backend == 2 ? "MenuManager.CMenuApi" : "MenuManagerCore.CMenuApi";
		var reason = "its API is not available";
		foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
		{
			try
			{
				var apiType = assembly.GetType(ApiTypeName, false);
				if (apiType == null || !apiType.IsInterface)
					continue;

				// Resolve the optional API only after its plugin has loaded.
				var capabilityType = typeof(PluginCapability<>).MakeGenericType(apiType);
				var capability = Activator.CreateInstance(capabilityType, CapabilityName);
				var value = capabilityType.GetMethod("Get", Type.EmptyTypes)?.Invoke(capability, null);
				if (value == null)
					continue;
				if (!apiType.IsInstanceOfType(value))
				{
					reason = "its API is incompatible";
					continue;
				}
				if (value.GetType().FullName != implementation)
				{
					reason = "the active menu provider does not match";
					continue;
				}

				var closeMenu = apiType.GetMethod("CloseMenu", [typeof(CCSPlayerController)]);
				if (closeMenu == null || closeMenu.IsStatic || closeMenu.ContainsGenericParameters ||
					closeMenu.ReturnType != typeof(void))
				{
					reason = "its API is incompatible";
					continue;
				}

				MethodInfo? createMenu;
				object? menuType = null;
				if (backend == 2)
				{
					createMenu = apiType.GetMethod("GetMenu",
						[typeof(string), typeof(Action<CCSPlayerController>), typeof(Action<CCSPlayerController>)]);
				}
				else
				{
					var menuTypeType = assembly.GetType("MenuManager.MenuType", false);
					if (menuTypeType == null || !menuTypeType.IsEnum ||
						!Enum.TryParse(menuTypeType, "PanoramaMenu", out menuType))
					{
						reason = "its API does not support Panorama menus";
						continue;
					}
					createMenu = apiType.GetMethod("GetMenuForcetype",
						[typeof(string), menuTypeType, typeof(Action<CCSPlayerController>), typeof(Action<CCSPlayerController>)]);
				}

				if (createMenu == null || createMenu.IsStatic || createMenu.ContainsGenericParameters ||
					!typeof(IMenu).IsAssignableFrom(createMenu.ReturnType))
				{
					reason = "its API is incompatible";
					continue;
				}

				var addToggle = apiType.GetMethod("AddToggle", [typeof(IMenu), typeof(string), typeof(bool),
					typeof(Action<CCSPlayerController, ChatMenuOption>), typeof(bool)]);
				if (addToggle != null && (addToggle.IsStatic || addToggle.ContainsGenericParameters ||
					addToggle.ReturnType != typeof(ChatMenuOption)))
					addToggle = null;

				return new NativeMenuProvider(value, createMenu, closeMenu, addToggle, menuType, providerName, logger);
			}
			catch (Exception)
			{
				// Another copy of the API may be loaded in a different plugin context.
				continue;
			}
		}

		WarnOnce(backend, logger, $"{providerName} was selected, but {reason}. Using built-in menus.");
		return null;
	}

	public IMenu CreateMenu(string title, Action<CCSPlayerController>? back)
	{
		object?[] arguments = menuType == null ? [title, back, null] : [title, menuType, back, null];
		return createMenu.Invoke(api, arguments) as IMenu ??
			throw new InvalidOperationException($"{providerName} did not return a compatible menu.");
	}

	public void CloseMenu(CCSPlayerController player)
	{
		try
		{
			closeMenu.Invoke(api, [player]);
		}
		catch (Exception ex)
		{
			ReportFailure(ex);
		}
	}

	public bool TryAddToggle(IMenu menu, string text, bool value,
		Action<CCSPlayerController, ChatMenuOption> callback, bool disabled)
	{
		if (addToggle == null)
			return false;
		if (addToggle.Invoke(api, [menu, text, value, callback, disabled]) is not ChatMenuOption)
			throw new InvalidOperationException($"{providerName} did not return a compatible toggle option.");
		return true;
	}

	public void ReportFailure(Exception exception)
	{
		if (Interlocked.Exchange(ref failureReported, 1) != 0)
			return;
		logger.LogWarning("{Provider} menu failed. Using built-in menus. {Reason}",
			providerName, exception.GetBaseException().Message);
	}

	private static void WarnOnce(int backend, ILogger logger, string message)
	{
		lock (warnedBackends)
		{
			if (!warnedBackends.Add(backend))
				return;
		}
		logger.LogWarning("{Message}", message);
	}
}
