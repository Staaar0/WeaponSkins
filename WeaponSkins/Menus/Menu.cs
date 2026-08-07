using CounterStrikeSharp.API.Core;

namespace WeaponSkins;

public sealed class MenuItem
{
	public readonly string Text;
	public readonly string? Value;
	public readonly Action<CCSPlayerController, MenuSession>? Select;

	public MenuItem(string text, Action<CCSPlayerController, MenuSession>? select, string? value = null)
	{
		Text = text;
		Select = select;
		Value = value;
	}
}

public sealed class Menu
{
	public required string Title;
	public required IReadOnlyList<MenuItem> Items;
}

public sealed class MenuContext
{
	public int WeaponDef;
	public int StickerSlot;
	public int GloveDef;
	public string Search = "";
}

public sealed class PendingInput
{
	public required string Prompt;
	public required Action<CCSPlayerController, MenuSession, string> OnText;
}

public sealed class MenuSession
{
	public readonly List<(Func<CCSPlayerController, MenuSession, Menu> Factory, int Cursor)> Stack = [];
	public Menu? Current;
	public int Cursor;
	public MenuContext Ctx = new();
	public PendingInput? Input;
	public string? Html;
	public ulong PrevButtons;
	public int HeldTicks;
	public long NextDrawAt;
	public long NextSelectAt;
	public bool Frozen;
}

