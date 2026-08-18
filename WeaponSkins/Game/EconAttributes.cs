using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using CounterStrikeSharp.API.Modules.Memory;
using Microsoft.Extensions.Logging;

namespace WeaponSkins;

public static class EconAttributes
{
	private static MemoryFunctionVoid<nint, string, float>? setAttribute;
	private static long nextItemId = 65536;

	public static bool Available => setAttribute != null;

	public static void Init(ILogger logger)
	{
		try
		{
			setAttribute = new MemoryFunctionVoid<nint, string, float>(GameData.GetSignature("CAttributeList_SetOrAddAttributeValueByName"));
		}
		catch (Exception ex)
		{
			logger.LogWarning("Attribute signature unavailable, stickers/charms/glove paints disabled: {Error}", ex.Message);
		}
	}

	public static void Set(CAttributeList list, string attribute, float value)
	{
		if (setAttribute == null || list.Handle == IntPtr.Zero || string.IsNullOrEmpty(attribute))
			return;

		setAttribute.Invoke(list.Handle, attribute, value);
	}

	public static float AsFloat(int value)
	{
		return BitConverter.Int32BitsToSingle(value);
	}

	public static void MarkCustom(CEconItemView item, ulong steamId)
	{
		if (item.Handle == IntPtr.Zero)
			return;

		var itemId = (ulong)Interlocked.Increment(ref nextItemId);
		item.ItemID = itemId;
		item.ItemIDLow = (uint)(itemId & 0xFFFFFFFF);
		item.ItemIDHigh = (uint)(itemId >> 32);
		item.AccountID = (uint)(steamId & 0xFFFFFFFF);
	}
}

