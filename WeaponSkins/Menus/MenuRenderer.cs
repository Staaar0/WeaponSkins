using System.Collections;
using System.Net;
using System.Text;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Localization;

namespace WeaponSkins;

public sealed class MenuRenderer
{
	private const ulong InspectButton = 1UL << 35;
	private const int DrawIntervalMs = 75;
	private const int SelectCooldownMs = 200;

	private readonly Dictionary<int, MenuSession> sessions = [];
	private readonly Dictionary<int, (string Url, long Until)> images = [];

	private readonly List<KeyValuePair<int, MenuSession>> snapshot = [];
	private readonly MenuConfig config;
	private readonly IStringLocalizer localizer;
	private readonly StringBuilder builder = new(1024);
	private readonly MenuItem backItem;

	public MenuRenderer(MenuConfig config, IStringLocalizer localizer)
	{
		this.config = config;
		this.localizer = localizer;
		backItem = new MenuItem(localizer["menu.back"].Value, (player, session) => Back(player, session));
	}

	public MenuSession? SessionOf(CCSPlayerController player)
	{
		return sessions.GetValueOrDefault(player.Slot);
	}

	public void Open(CCSPlayerController player, Func<CCSPlayerController, MenuSession, Menu> factory, bool root = false)
	{
		if (!sessions.TryGetValue(player.Slot, out var session))
			sessions[player.Slot] = session = new MenuSession();

		if (root)
		{
			session.Stack.Clear();
			session.Ctx = new MenuContext();
		}
		else if (session.Current != null)
		{
			session.Stack[^1] = (session.Stack[^1].Factory, session.Cursor);
		}

		session.Stack.Add((factory, 0));
		session.Current = Compose(factory(player, session), session);
		session.Cursor = Math.Min(session.Stack.Count > 1 ? 1 : 0, Math.Max(0, session.Current.Items.Count - 1));
		session.Input = null;
		session.Html = null;
		session.PrevButtons = (ulong)player.Buttons;

		if (config.FreezePlayer)
			Freeze(player, session, true);
	}

	public void Rebuild(CCSPlayerController player, MenuSession session)
	{
		if (session.Stack.Count == 0)
			return;

		session.Current = Compose(session.Stack[^1].Factory(player, session), session);
		session.Cursor = Math.Clamp(session.Cursor, 0, Math.Max(0, session.Current.Items.Count - 1));
		session.Html = null;
	}

	public void Back(CCSPlayerController player, MenuSession session)
	{
		if (session.Stack.Count <= 1)
		{
			Close(player);
			return;
		}

		session.Stack.RemoveAt(session.Stack.Count - 1);
		var (factory, cursor) = session.Stack[^1];
		session.Current = Compose(factory(player, session), session);
		session.Cursor = Math.Clamp(cursor, 0, Math.Max(0, session.Current.Items.Count - 1));
		session.Input = null;
		session.Html = null;
	}

	public void Close(CCSPlayerController player)
	{
		if (!sessions.Remove(player.Slot, out var session))
			return;

		if (session.Frozen)
			Freeze(player, session, false);

		if (player.IsValid)
			player.PrintToCenterHtml(" ");
	}

	public void Drop(int slot)
	{
		sessions.Remove(slot);
		images.Remove(slot);
	}


	public void CloseAll()
	{
		foreach (var slot in sessions.Keys.ToList())
		{
			var player = Utilities.GetPlayerFromSlot(slot);
			if (player != null && player.IsValid)
				Close(player);
			else
				sessions.Remove(slot);
		}
	}

	public bool CaptureChat(CCSPlayerController player, string text)
	{
		if (!sessions.TryGetValue(player.Slot, out var session) || session.Input == null)
			return false;

		var now = Environment.TickCount64;
		if (now < session.NextSelectAt)
			return true;

		session.NextSelectAt = now + SelectCooldownMs;
		var input = session.Input;
		session.Input = null;
		session.Html = null;

		var trimmed = text.Trim();
		if (trimmed != "-")
			input.OnText(player, session, trimmed);
		else
			Rebuild(player, session);

		return true;
	}

	public void Prompt(CCSPlayerController player, MenuSession session, string prompt, Action<CCSPlayerController, MenuSession, string> onText)
	{
		session.Input = new PendingInput { Prompt = prompt, OnText = onText };
		session.Html = null;
	}

	public void ShowImage(CCSPlayerController player, string url)
	{
		if (!config.ShowImage || url.Length == 0)
			return;

		images[player.Slot] = (url, Environment.TickCount64 + (long)(config.ImageSeconds * 1000f));
	}

	private string WithImage(int slot, string html, long now)
	{
		if (!images.TryGetValue(slot, out var image))
			return html;

		if (now >= image.Until)
		{
			images.Remove(slot);
			return html;
		}

		return $"<img src='{image.Url}'><br>{html}";
	}

	public void OnTick()
	{
		if (sessions.Count == 0)
			return;

		snapshot.Clear();
		snapshot.AddRange(sessions);

		var now = Environment.TickCount64;
		foreach (var (slot, session) in snapshot)
		{
			var player = Utilities.GetPlayerFromSlot(slot);
			if (player == null || !player.IsValid || session.Current == null)
			{
				sessions.Remove(slot);
				continue;
			}

			if (session.Input == null)
			{
				HandleButtons(player, session, now);
				if (!sessions.ContainsKey(slot))
					continue;
			}

			var changed = session.Html == null;
			if (changed)
				session.Html = Render(session);

			if (changed || now >= session.NextDrawAt)
			{
				player.PrintToCenterHtml(WithImage(slot, session.Html!, now));
				session.NextDrawAt = now + DrawIntervalMs;
			}
		}
	}

	private void HandleButtons(CCSPlayerController player, MenuSession session, long now)
	{
		var buttons = (ulong)player.Buttons;
		var previous = session.PrevButtons;
		session.PrevButtons = buttons;

		var menu = session.Current!;
		var count = menu.Items.Count;
		var perPage = Math.Clamp(config.ItemsPerPage, 3, 9);

		var up = Held(buttons, (ulong)PlayerButtons.Forward);
		var down = Held(buttons, (ulong)PlayerButtons.Back);
		var pageBack = Held(buttons, (ulong)PlayerButtons.Moveleft);
		var pageForward = Held(buttons, (ulong)PlayerButtons.Moveright);

		if (up || down || pageBack || pageForward)
			session.HeldTicks++;
		else
			session.HeldTicks = 0;

		var repeat = session.HeldTicks > 16 && session.HeldTicks % 3 == 0;
		var pageRepeat = session.HeldTicks > 16 && session.HeldTicks % 6 == 0;

		var cursor = session.Cursor;
		if (count > 0)
		{
			if (Pressed(buttons, previous, (ulong)PlayerButtons.Forward) || (up && repeat))
				cursor = Math.Max(0, cursor - 1);
			else if (Pressed(buttons, previous, (ulong)PlayerButtons.Back) || (down && repeat))
				cursor = Math.Min(count - 1, cursor + 1);
			else if (Pressed(buttons, previous, (ulong)PlayerButtons.Moveleft) || (pageBack && pageRepeat))
				cursor = Math.Max(0, cursor - perPage);
			else if (Pressed(buttons, previous, (ulong)PlayerButtons.Moveright) || (pageForward && pageRepeat))
				cursor = Math.Min(count - 1, cursor + perPage);
		}

		if (cursor != session.Cursor)
		{
			session.Cursor = cursor;
			session.Html = null;
		}

		if (Pressed(buttons, previous, (ulong)PlayerButtons.Use))
		{
			if (now < session.NextSelectAt)
				return;

			session.NextSelectAt = now + SelectCooldownMs;
			var item = count > 0 ? menu.Items[session.Cursor] : null;
			item?.Select?.Invoke(player, session);
		}
		else if (Pressed(buttons, previous, InspectButton))
		{
			Back(player, session);
		}
		else if (Pressed(buttons, previous, (ulong)PlayerButtons.Reload))
		{
			Close(player);
		}
	}

	private static bool Held(ulong buttons, ulong button) => (buttons & button) != 0;

	private static bool Pressed(ulong buttons, ulong previous, ulong button)
	{
		return (buttons & button) != 0 && (previous & button) == 0;
	}

	private string Render(MenuSession session)
	{
		var menu = session.Current!;
		builder.Clear();

		if (session.Input != null)
		{
			builder.Append("<b><font color='#D4AF37'>").Append(WebUtility.HtmlEncode(menu.Title)).Append("</font></b><br>");
			builder.Append("<font color='#FFFFFF'>").Append(WebUtility.HtmlEncode(session.Input.Prompt)).Append("</font><br>");
			builder.Append("<font color='#808080'>").Append(localizer["menu.input_hint"]).Append("</font>");
			return builder.ToString();
		}

		var perPage = Math.Clamp(config.ItemsPerPage, 3, 9);
		var count = menu.Items.Count;
		var pages = Math.Max(1, (count + perPage - 1) / perPage);
		var page = count == 0 ? 0 : session.Cursor / perPage;
		var start = page * perPage;
		var end = Math.Min(count, start + perPage);

		builder.Append("<b><font color='#D4AF37'>").Append(WebUtility.HtmlEncode(menu.Title)).Append("</font></b>");
		if (pages > 1)
			builder.Append(" <font color='#808080'>").Append(page + 1).Append('/').Append(pages).Append("</font>");
		builder.Append("<br>");

		for (var i = start; i < end; i++)
		{
			var item = menu.Items[i];
			var text = item.Text.Length > 34 ? item.Text[..34] : item.Text;

			if (i == session.Cursor)
				builder.Append("<font color='#D4AF37'>\u25B8 </font><font color='#FFFFFF'>").Append(WebUtility.HtmlEncode(text)).Append("</font>");
			else
				builder.Append("<font color='#9E9E9E'>\u00A0\u00A0\u2009").Append(WebUtility.HtmlEncode(text)).Append("</font>");

			if (item.Value != null)
				builder.Append(" <font color='#6E6E6E'>").Append(WebUtility.HtmlEncode(item.Value)).Append("</font>");

			builder.Append("<br>");
		}

		builder.Append("<font color='#606060'>").Append(localizer["menu.footer"]).Append("</font>");
		return builder.ToString();
	}

	private static void Freeze(CCSPlayerController player, MenuSession session, bool freeze)
	{
		var pawn = player.PlayerPawn.Value;
		if (pawn == null || !pawn.IsValid || !player.PawnIsAlive)
			return;

		var moveType = freeze ? MoveType_t.MOVETYPE_NONE : MoveType_t.MOVETYPE_WALK;
		pawn.MoveType = moveType;
		pawn.ActualMoveType = moveType;
		Utilities.SetStateChanged(pawn, "CBaseEntity", "m_MoveType");

		if (freeze)
		{
			pawn.AbsVelocity.X = 0f;
			pawn.AbsVelocity.Y = 0f;
			pawn.AbsVelocity.Z = 0f;
		}

		session.Frozen = freeze;
	}

	private Menu Compose(Menu menu, MenuSession session)
	{
		if (session.Stack.Count <= 1)
			return menu;

		return new Menu { Title = menu.Title, Items = new BackedItems(menu.Items, backItem) };
	}

	private sealed class BackedItems : IReadOnlyList<MenuItem>
	{
		private readonly IReadOnlyList<MenuItem> inner;
		private readonly MenuItem back;

		public BackedItems(IReadOnlyList<MenuItem> inner, MenuItem back)
		{
			this.inner = inner;
			this.back = back;
		}

		public MenuItem this[int index] => index == 0 ? back : inner[index - 1];

		public int Count => inner.Count + 1;

		public IEnumerator<MenuItem> GetEnumerator()
		{
			yield return back;
			foreach (var item in inner)
				yield return item;
		}

		IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
	}
}
