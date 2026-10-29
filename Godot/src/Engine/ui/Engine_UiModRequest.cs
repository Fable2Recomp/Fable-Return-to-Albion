using System;
using System.Collections.Generic;
using System.Globalization;

namespace Refii.Ui;

/// <summary>
/// A UI request a native or Lua mod made — <c>refii_mod_open_menu</c> / <c>Refii.open_menu</c>,
/// <c>refii_mod_message_box</c> / <c>Refii.message_box</c>, <c>refii_mod_notice</c> /
/// <c>Refii.notice</c>, <c>refii_mod_menu_box</c> / <c>Refii.menu_box</c>, and the HUD's own: markers, fades,
/// meters, input claims (<c>refii_mod_input_claim</c>) and HUD overrides (<c>refii_mod_hud_icon</c> /
/// <c>_hud_slide</c> / <c>_hud_hold</c>) — as it crosses the mod bus to Godot
/// under <see cref="Kind"/> (REFII_MOD_HOST_UI_KIND, core/refii_mod_events.h): never an event,
/// posted whatever the mask says. And the guest command a box's answer goes back as, which raises
/// the <c>ModMessageBox</c> event for native and Lua handlers.
/// </summary>
/// <remarks>
/// Pure text work, held by tests without a Godot type in the host; <see cref="Engine_UiModBoxes"/>
/// acts on it. The fields are the bus's: <c>key=value</c>, text <c>'quoted'</c>, never carrying a
/// quote; a line break inside a text arrives as the two characters <c>\n</c>.
/// </remarks>
public static class Engine_UiModRequest
{
	/// <summary>REFII_MOD_HOST_UI_KIND.</summary>
	public const int Kind = 1002;

	/// <summary>
	/// The first id a Godot script's box takes. The payload numbers its boxes 1 .. 2^23-1, Godot
	/// from here to 2^24-1, so every id is exact as a Lua number and none collides.
	/// </summary>
	public const uint FirstHostBoxId = 0x800000u;

	/// <summary>The last id either side issues (exact as a Lua number).</summary>
	public const uint LastBoxId = 0xFFFFFFu;

	/// <summary>The guest command that raises <c>ModMessageBox</c> (Fable2/src/vfs/src/hooks/mod_commands.cpp).</summary>
	public const string AnswerVerb = "box_answer";

	/// <summary>The guest command that raises <c>ModNotice</c>.</summary>
	public const string NoticeVerb = "notice_done";

	/// <summary>The guest command that raises <c>ModMenuBox</c>.</summary>
	public const string MenuBoxVerb = "menu_box_answer";

	/// <summary>The most options a menu box takes on any surface (the bus carries <c>o0</c>..<c>o15</c>).</summary>
	public const int MaxMenuOptions = 16;

	public enum Op
	{
		OpenMenu,
		MessageBox,
		Notice,
		MenuBox,

		/// <summary>A world marker (<c>refii_mod_mark</c>): <see cref="Request.Entity"/> (a handle) or the point, <see cref="Request.Icon"/>, <see cref="Request.Text"/>.</summary>
		Mark,

		/// <summary>A marker removed by id.</summary>
		Unmark,

		/// <summary>A player's screen fade (<c>refii_mod_fade</c>): <see cref="Request.ToBlack"/> over <see cref="Request.Seconds"/>.</summary>
		Fade,

		/// <summary>A HUD meter (<c>refii_mod_meter</c>): <see cref="Request.Name"/> its id, <see cref="Request.Title"/> its label, <see cref="Request.Value"/> of <see cref="Request.Max"/>, <see cref="Request.Style"/>.</summary>
		Meter,

		/// <summary>A meter removed by id (<see cref="Request.Name"/>).</summary>
		MeterRemove,

		/// <summary>An input claim (<c>refii_mod_input_claim</c>): <see cref="Request.Name"/> the scoped action, <see cref="Request.On"/>.</summary>
		InputClaim,

		/// <summary>A HUD element's icon (<c>refii_mod_hud_icon</c>): <see cref="Request.Name"/> the element, <see cref="Request.Icon"/> the texture ("" clears).</summary>
		HudIcon,

		/// <summary>A HUD element's slide (<c>refii_mod_hud_slide</c>): <see cref="Request.Name"/> the element, <see cref="Request.Slide"/> (0 clears).</summary>
		HudSlide,

		/// <summary>A HUD piece held visible (<c>refii_mod_hud_hold</c>): <see cref="Request.Name"/> the piece, <see cref="Request.On"/>.</summary>
		HudHold,
	}

	/// <summary>
	/// One request. <see cref="Player"/> is 1 or 2, or 0 for whoever owns the menus on this
	/// machine. For a box: <see cref="Question"/> raises two buttons (A, B) captioned
	/// <see cref="Accept"/> and <see cref="Cancel"/> (null keeps the box's own), else one (a notice).
	/// </summary>
	/// <para>A notice is shown for <see cref="Seconds"/> (0: the channel's own length). A menu box
	/// offers <see cref="Options"/> and is <see cref="Cancelable"/> with B.</para>
	/// <para>A marker, fade or meter (<see cref="Op.Mark"/> and after) uses the trailing fields:
	/// <see cref="Entity"/> a handle, <see cref="X"/>/<see cref="Y"/>/<see cref="Z"/> a guest
	/// point, <see cref="Icon"/>, <see cref="ToBlack"/>, <see cref="Value"/>/<see cref="Max"/>, <see cref="Style"/>.</para>
	public readonly record struct Request(
		Op Op, uint Id, int Player, string Name, string Title, string Text, string? Accept, string? Cancel, bool Question,
		IReadOnlyList<string>? Options = null, float Seconds = 0f, bool Cancelable = true,
		uint Entity = 0, float X = 0f, float Y = 0f, float Z = 0f, string Icon = "", bool ToBlack = false,
		float Value = 0f, float Max = 0f, string Style = "", bool On = false, int Slide = 0);

	/// <summary>The request in <paramref name="fields"/>, or null when it is not one (no op, an open with no menu, a box with no id or text).</summary>
	public static Request? Parse(IEnumerable<KeyValuePair<string, object>> fields)
	{
		var values = new Dictionary<string, object>(StringComparer.Ordinal);
		foreach ((string key, object value) in fields)
		{
			values[key] = value;
		}

		string Text(string key) => values.TryGetValue(key, out object? v) && v is string s ? Unescape(s) : string.Empty;
		double Number(string key) => values.TryGetValue(key, out object? v)
			? v switch { long l => l, double d => d, bool b => b ? 1 : 0, _ => 0 }
			: 0;

		int player = (int)Math.Clamp(Number("player"), 0, 2);
		switch (Text("op"))
		{
			case "open_menu":
			{
				string name = Text("name").Trim();
				return name.Length == 0
					? null
					: new Request(Op.OpenMenu, 0u, player, name, string.Empty, string.Empty, null, null, false);
			}

			case "message_box":
			{
				double rawId = Number("id");
				uint id = rawId is >= 1 and <= LastBoxId ? (uint)rawId : 0u;
				string title = Text("title");
				string text = Text("text");
				if (id == 0 || title.Length + text.Length == 0)
				{
					return null;
				}

				bool question = Number("buttons") >= 2;
				string? accept = Text("accept") is { Length: > 0 } a ? a : null;
				string? cancel = Text("cancel") is { Length: > 0 } c ? c : null;
				return new Request(Op.MessageBox, id, player, string.Empty, title, text, accept, question ? cancel : null, question);
			}

			case "notice":
			{
				uint id = Id(Number("id"));
				string title = Text("title");
				string text = Text("text");
				if (id == 0 || title.Length + text.Length == 0)
				{
					return null;
				}

				double seconds = Number("seconds");
				return new Request(Op.Notice, id, player, string.Empty, title, text, null, null, false,
					Seconds: double.IsFinite(seconds) && seconds > 0 ? (float)Math.Min(seconds, 600) : 0f);
			}

			case "menu_box":
			{
				uint id = Id(Number("id"));
				int count = (int)Math.Clamp(Number("count"), 0, MaxMenuOptions);
				var options = new List<string>(count);
				for (int i = 0; i < count; i++)
				{
					options.Add(Text("o" + i.ToString(CultureInfo.InvariantCulture)));
				}

				if (id == 0 || options.Count == 0)
				{
					return null;
				}

				return new Request(Op.MenuBox, id, player, string.Empty, Text("title"), Text("text"), null, null, false,
					Options: options, Cancelable: !values.ContainsKey("cancel") || Number("cancel") != 0);
			}

			case "mark":
			{
				uint id = Id(Number("id"));
				string icon = Text("icon").Trim();
				string text = Text("text");
				if (id == 0 || icon.Length + text.Length == 0)
				{
					return null;
				}

				double entity = Number("entity");
				return new Request(Op.Mark, id, player, string.Empty, string.Empty, text, null, null, false,
					Entity: entity is > 0 and <= uint.MaxValue ? (uint)entity : 0u,
					X: (float)Number("x"), Y: (float)Number("y"), Z: (float)Number("z"), Icon: icon);
			}

			case "unmark":
			{
				uint id = Id(Number("id"));
				return id == 0 ? null : new Request(Op.Unmark, id, player, string.Empty, string.Empty, string.Empty, null, null, false);
			}

			case "fade":
			{
				double seconds = Number("seconds");
				return new Request(Op.Fade, 0u, player, string.Empty, string.Empty, string.Empty, null, null, false,
					Seconds: double.IsFinite(seconds) && seconds > 0 ? (float)Math.Min(seconds, 600) : 0f,
					ToBlack: Number("to") != 0);
			}

			case "meter":
			{
				string name = Text("id").Trim();
				if (name.Length == 0)
				{
					return null;
				}

				return new Request(Op.Meter, 0u, player, name, Text("label"), string.Empty, null, null, false,
					Value: (float)Number("value"), Max: (float)Number("max"), Style: Text("style").Trim());
			}

			case "meter_remove":
			{
				string name = Text("id").Trim();
				return name.Length == 0
					? null
					: new Request(Op.MeterRemove, 0u, player, name, string.Empty, string.Empty, null, null, false);
			}

			case "input_claim":
			{
				string action = Text("action").Trim();
				return action.Length == 0
					? null
					: new Request(Op.InputClaim, 0u, player, action, string.Empty, string.Empty, null, null, false, On: Number("on") != 0);
			}

			case "hud_icon":
			{
				string element = Text("element").Trim();
				return element.Length == 0
					? null
					: new Request(Op.HudIcon, 0u, player, element, string.Empty, string.Empty, null, null, false, Icon: Text("texture").Trim());
			}

			case "hud_slide":
			{
				string element = Text("element").Trim();
				double slide = Number("slide");
				return element.Length == 0 || slide < 0 || slide > 65535
					? null
					: new Request(Op.HudSlide, 0u, player, element, string.Empty, string.Empty, null, null, false, Slide: (int)slide);
			}

			case "hud_hold":
			{
				string piece = Text("piece").Trim();
				return piece.Length == 0
					? null
					: new Request(Op.HudHold, 0u, player, piece, string.Empty, string.Empty, null, null, false, On: Number("on") != 0);
			}

			default:
				return null;
		}
	}

	/// <summary>An id as the bus states it: 1 .. <see cref="LastBoxId"/>, else 0.</summary>
	private static uint Id(double raw) => raw is >= 1 and <= LastBoxId ? (uint)raw : 0u;

	/// <summary>The guest command that raises <c>ModNotice</c>: <c>notice_done &lt;id&gt; &lt;outcome&gt; &lt;player&gt;</c>.</summary>
	public static string NoticeCommandLine(uint id, int outcome, int player) =>
		string.Create(CultureInfo.InvariantCulture, $"{NoticeVerb} {id} {outcome} {player}");

	/// <summary>The guest command that raises <c>ModMenuBox</c>: <c>menu_box_answer &lt;id&gt; &lt;option&gt; &lt;player&gt;</c>.</summary>
	public static string MenuBoxCommandLine(uint id, int option, int player) =>
		string.Create(CultureInfo.InvariantCulture, $"{MenuBoxVerb} {id} {option} {player}");

	/// <summary>The box's body: the title above the text, a blank line between; either alone stands alone.</summary>
	public static string Body(string title, string text) =>
		title.Length == 0 ? text : text.Length == 0 ? title : $"{title}\n\n{text}";

	/// <summary>A bus text back to its own: the two characters <c>\n</c> are a line break.</summary>
	public static string Unescape(string text) => text.Replace("\\n", "\n", StringComparison.Ordinal);

	/// <summary>The guest command that raises <c>ModMessageBox</c>: <c>box_answer &lt;id&gt; &lt;button&gt; &lt;player&gt;</c>.</summary>
	public static string AnswerCommandLine(uint id, int button, int player) =>
		string.Create(CultureInfo.InvariantCulture, $"{AnswerVerb} {id} {button} {player}");

	/// <summary>
	/// Whether this machine plays <paramref name="player"/> and so shows their menus and boxes:
	/// player 0 (whoever owns the menus here) always; alone and in local co-op both players; on an
	/// online host player one, on an online joiner player two. A mod's code runs on both machines,
	/// so a request for the other machine's player is that machine's to show.
	/// </summary>
	public static bool PlayedHere(int player, bool client, bool peerStanding) =>
		player == 0 || (client ? player == 2 : !peerStanding || player == 1);
}
