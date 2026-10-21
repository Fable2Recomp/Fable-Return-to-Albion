using System;
using System.Collections.Generic;
using System.Globalization;

namespace Refii.Ui;

/// <summary>
/// A UI request a native or Lua mod made — <c>refii_mod_open_menu</c> / <c>Refii.open_menu</c>,
/// <c>refii_mod_message_box</c> / <c>Refii.message_box</c> — as it crosses the mod bus to Godot
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

	/// <summary>The guest command that raises <c>ModMessageBox</c> (src/vfs/src/hooks/mod_commands.cpp).</summary>
	public const string AnswerVerb = "box_answer";

	public enum Op
	{
		OpenMenu,
		MessageBox,
	}

	/// <summary>
	/// One request. <see cref="Player"/> is 1 or 2, or 0 for whoever owns the menus on this
	/// machine. For a box: <see cref="Question"/> raises two buttons (A, B) captioned
	/// <see cref="Accept"/> and <see cref="Cancel"/> (null keeps the box's own), else one (a notice).
	/// </summary>
	public readonly record struct Request(
		Op Op, uint Id, int Player, string Name, string Title, string Text, string? Accept, string? Cancel, bool Question);

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

			default:
				return null;
		}
	}

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
