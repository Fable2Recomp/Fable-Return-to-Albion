using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Refii.Ui;

/// <summary>
/// MOD MENU ROW SOURCES — rows a menu takes at runtime rather than from its record, through the same
/// source registry every shipped list uses (<see cref="Engine_UiMenuSources"/>):
/// <list type="bullet">
/// <item><c>"source": "rows"</c> (or <c>rows:&lt;list&gt;</c>) — the rows a script stated with
/// <c>set_menu_rows(list, rows, player)</c> (Godot <c>RefiiHost.set_menu_rows</c>, Lua
/// <c>Refii.set_menu_rows</c>, native <c>refii_mod_set_menu_rows</c>); a bare <c>rows</c> names the
/// list after the menu or entry it hangs on. PLAYER-AWARE: rows stated for player 1 or 2 are that
/// player's, rows for 0 everyone's, and a menu shows its player's own when they stated any.</item>
/// <item><c>"source": "values:&lt;binding&gt;=&lt;label&gt;,..."</c> — one row per published value
/// (<see cref="Engine_UiValueSource"/>: <c>mod:&lt;key&gt;</c>, <c>player:&lt;key&gt;</c>, with an
/// optional <c>|format</c>), labelled "&lt;label&gt;: &lt;value&gt;" and re-read every refresh.</item>
/// </list>
/// A stated row is <c>name</c>, <c>label</c> (a Babel tag or literal; the name when empty), an
/// optional <c>command</c> (any verb — <see cref="Engine_UiCommands"/>) and <c>ghosted</c>.
/// </summary>
/// <remarks>
/// Native and Lua rows cross the bus as one text (<see cref="ParseRows"/>): rows split by <c>;</c>,
/// fields by <c>|</c> (<c>name|label|command|ghosted</c>), <c>\</c> escaping either. The record is the
/// bus's: at most about 1.8 KB of rows. Pure apart from the live value lookups, so the forms are held
/// by tests.
/// </remarks>
public static class Engine_UiModRows
{
	public const string RowsSource = "rows";
	public const string ValuesSource = "values";

	/// <summary>One row a script stated.</summary>
	public readonly record struct Stated(string Name, string Label, string? Command, bool Ghosted);

	private static readonly Dictionary<(int Player, string List), List<Stated>> Lists = new();

	/// <summary>Bumped on every change, so a list can tell it must re-source.</summary>
	public static int Version { get; private set; }

	/// <summary>
	/// States <paramref name="list"/>'s rows for <paramref name="player"/> (1 or 2; 0 for everyone),
	/// replacing what was stated before; an empty set clears it.
	/// </summary>
	public static void Set(string list, IReadOnlyList<Stated> rows, int player)
	{
		string name = list.Trim();
		if (name.Length == 0)
		{
			return;
		}

		lock (Lists)
		{
			var key = (player is 1 or 2 ? player : 0, name);
			if (rows.Count == 0)
			{
				Lists.Remove(key);
			}
			else
			{
				Lists[key] = new List<Stated>(rows);
			}

			++Version;
		}
	}

	/// <summary>The rows <paramref name="list"/> shows for <paramref name="player"/>: theirs, else everyone's.</summary>
	public static IReadOnlyList<Stated> For(string list, int player)
	{
		lock (Lists)
		{
			if (player is 1 or 2 && Lists.TryGetValue((player, list), out List<Stated>? own))
			{
				return own;
			}

			return Lists.TryGetValue((0, list), out List<Stated>? shared) ? shared : Array.Empty<Stated>();
		}
	}

	/// <summary>Forgets every stated row (a remount: the mods that stated them may be gone).</summary>
	public static void Clear()
	{
		lock (Lists)
		{
			Lists.Clear();
			++Version;
		}
	}

	/// <summary>The <c>rows</c> source: the stated rows as menu rows, each label through the string book.</summary>
	public static IReadOnlyList<Engine_UiMenuSources.Row> Rows(string list) =>
		ToRows(For(list, Engine_UiModMenus.MenuPlayer), BookLabel);

	/// <summary>
	/// A stated label as the row draws it: a Babel tag's string (the tag as given, then with
	/// <c>TEXT_</c>, as <see cref="Engine_UiGuiText"/> resolves the game's), else the literal. The
	/// list takes a sourced row's label as its text, so the tag is resolved here.
	/// </summary>
	public static string BookLabel(string label) =>
		Engine_UiGuiText.Resolve(label, Refii.Core.Engine_TitleGame.Active.Strings);

	/// <summary>The stated rows as menu rows; <paramref name="label"/> turns a stated label into the row's text (as stated when null).</summary>
	public static List<Engine_UiMenuSources.Row> ToRows(IReadOnlyList<Stated> stated, Func<string, string>? label = null)
	{
		var rows = new List<Engine_UiMenuSources.Row>(stated.Count);
		foreach (Stated row in stated)
		{
			string text = row.Label.Length != 0 ? row.Label : row.Name;
			rows.Add(new Engine_UiMenuSources.Row(
				Name: row.Name,
				Label: label is null ? text : label(text),
				Icon: string.Empty,
				EventType: 0,
				Id: 0u,
				Rating: Engine_UiMenuConstants.EmptyRatingSlide,
				Committable: false,
				Ghosted: row.Ghosted,
				Command: string.IsNullOrEmpty(row.Command) ? null : row.Command,
				FromMod: true));
		}

		return rows;
	}

	/// <summary>The <c>values</c> source, read live.</summary>
	public static IReadOnlyList<Engine_UiMenuSources.Row> Values(string argument)
	{
		var lookups = new Engine_UiValueSource.Lookups(Engine_UiValueSource.Live(Engine_UiModMenus.MenuPlayer), _ => null);
		return ToRows(ValueRows(argument, lookups));
	}

	/// <summary>
	/// The rows a <c>values:</c> argument states: <c>&lt;binding&gt;=&lt;label&gt;</c> by commas, a
	/// binding with no <c>mod:</c>/<c>player:</c> read as <c>mod:</c>; the label is the key when absent.
	/// A value nobody published shows its label alone.
	/// </summary>
	public static List<Stated> ValueRows(string argument, in Engine_UiValueSource.Lookups lookups)
	{
		var rows = new List<Stated>();
		foreach (string item in argument.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			int equals = item.IndexOf('=');
			string binding = (equals < 0 ? item : item[..equals]).Trim();
			if (!Engine_UiValueSource.IsValue(binding))
			{
				binding = Engine_UiValueSource.ModPrefix + binding;
			}

			string key = Engine_UiValueSource.SplitFormat(binding).Binding;
			key = key[(key.IndexOf(':') + 1)..];
			string label = equals < 0 ? key : item[(equals + 1)..].Trim();
			string? value = Engine_UiValueSource.Resolve(binding, lookups);
			rows.Add(new Stated(key, value is null ? label : $"{label}: {value}", null, false));
		}

		return rows;
	}

	// ---- the bus text --------------------------------------------------------------------------

	/// <summary>Rows from the bus text: <c>name|label|command|ghosted</c> split by <c>;</c>, <c>\</c> escaping.</summary>
	public static List<Stated> ParseRows(string text)
	{
		var rows = new List<Stated>();
		var fields = new List<string>();
		var field = new StringBuilder();
		bool escaped = false;

		void EndRow()
		{
			fields.Add(field.ToString());
			field.Clear();
			string name = fields[0].Trim();
			string label = fields.Count > 1 ? fields[1] : string.Empty;
			if (name.Length == 0)
			{
				name = label.Trim();
			}

			if (name.Length != 0)
			{
				string? command = fields.Count > 2 && fields[2].Length != 0 ? fields[2] : null;
				bool ghosted = fields.Count > 3 && fields[3].Trim() is "1" or "true";
				rows.Add(new Stated(name, label, command, ghosted));
			}

			fields.Clear();
		}

		foreach (char c in text)
		{
			if (escaped)
			{
				field.Append(c);
				escaped = false;
			}
			else if (c == '\\')
			{
				escaped = true;
			}
			else if (c == '|')
			{
				fields.Add(field.ToString());
				field.Clear();
			}
			else if (c == ';')
			{
				EndRow();
			}
			else
			{
				field.Append(c);
			}
		}

		if (field.Length != 0 || fields.Count != 0)
		{
			EndRow();
		}

		return rows;
	}

	/// <summary>The bus text for <paramref name="rows"/> — what <see cref="ParseRows"/> reads back.</summary>
	public static string RowsText(IEnumerable<Stated> rows)
	{
		static string Escape(string text) =>
			text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("|", "\\|", StringComparison.Ordinal)
				.Replace(";", "\\;", StringComparison.Ordinal);

		var text = new StringBuilder();
		foreach (Stated row in rows)
		{
			if (text.Length != 0)
			{
				text.Append(';');
			}

			text.Append(Escape(row.Name)).Append('|').Append(Escape(row.Label)).Append('|')
				.Append(Escape(row.Command ?? string.Empty)).Append('|').Append(row.Ghosted ? '1' : '0');
		}

		return text.ToString();
	}

	/// <summary>REFII_MOD_HOST_MENU_KIND (core/refii_mod_events.h): a native or Lua mod's menu request.</summary>
	public const int Kind = 1003;

	public enum Op
	{
		MenuRows,
		OpenScreen,
	}

	/// <summary>
	/// One request off the bus: rows for a list, or a screen to open, for a player. A rows text
	/// longer than one bus record crosses in <see cref="Parts"/> records sharing <see cref="Seq"/>
	/// (refii_mod_set_menu_rows, up to REFII_MOD_MENU_ROWS_MAX bytes); <see cref="Text"/> is this
	/// part's raw share and <see cref="Rows"/> is parsed only for a request in one part.
	/// </summary>
	public readonly record struct Request(Op Op, int Player, string Menu, IReadOnlyList<Stated> Rows, uint Screen,
		long Seq = 0, int Part = 0, int Parts = 1, string Text = "");

	/// <summary>The most parts one rows text may take (REFII_MOD_MENU_ROWS_MAX / the 1800-byte share, rounded up).</summary>
	public const int MaxParts = 20;

	/// <summary>Parts arriving, by (player, menu): the seq they belong to and the shares so far.</summary>
	private static readonly Dictionary<(int Player, string Menu), (long Seq, string?[] Parts)> _arriving = new();

	/// <summary>
	/// One part of a rows text in; the whole text once every part of its <paramref name="seq"/> has
	/// arrived, else null. A part of a newer seq drops what an older one left unfinished (a lost
	/// record leaves its list as it was until the next statement).
	/// </summary>
	public static string? Assemble(int player, string menu, long seq, int part, int parts, string text)
	{
		if (parts <= 1)
		{
			_arriving.Remove((player, menu));
			return text;
		}

		if (parts > MaxParts || part < 0 || part >= parts)
		{
			return null;
		}

		var key = (player, menu);
		if (!_arriving.TryGetValue(key, out (long Seq, string?[] Parts) held) || held.Seq != seq || held.Parts.Length != parts)
		{
			held = (seq, new string?[parts]);
			_arriving[key] = held;
		}

		held.Parts[part] = text;
		foreach (string? share in held.Parts)
		{
			if (share is null)
			{
				return null;
			}
		}

		_arriving.Remove(key);
		return string.Concat(held.Parts);
	}

	/// <summary>The request in <paramref name="fields"/>, or null when it is not one.</summary>
	public static Request? Parse(IEnumerable<KeyValuePair<string, object>> fields)
	{
		var values = new Dictionary<string, object>(StringComparer.Ordinal);
		foreach ((string key, object value) in fields)
		{
			values[key] = value;
		}

		string Text(string key) => values.TryGetValue(key, out object? v) && v is string s ? s : string.Empty;
		double Number(string key) => values.TryGetValue(key, out object? v)
			? v switch { long l => l, double d => d, bool b => b ? 1 : 0, _ => 0 }
			: 0;

		int player = (int)Math.Clamp(Number("player"), 0, 2);
		switch (Text("op"))
		{
			case "menu_rows":
			{
				string menu = Text("menu").Trim();
				int parts = (int)Math.Clamp(Number("parts"), 1, MaxParts + 1);
				return menu.Length == 0
					? null
					: new Request(Op.MenuRows, player, menu, parts <= 1 ? ParseRows(Text("rows")) : Array.Empty<Stated>(), 0u,
						(long)Number("seq"), (int)Math.Clamp(Number("part"), 0, MaxParts + 1), parts, Text("rows"));
			}

			case "open_screen":
			{
				double id = Number("screen");
				return id is >= 1 and <= uint.MaxValue
					? new Request(Op.OpenScreen, player, string.Empty, Array.Empty<Stated>(), (uint)id)
					: null;
			}

			default:
				return null;
		}
	}

	/// <summary>Acts on one request off the bus.</summary>
	public static void Handle(string fields)
	{
		if (Parse(Refii.Mods.RefiiHost.ParseFieldsPlain(fields)) is not { } request)
		{
			Godot.GD.PushWarning($"[mod-rows] a menu request the host does not read: {fields}");
			return;
		}

		switch (request.Op)
		{
			case Op.MenuRows:
			{
				IReadOnlyList<Stated> rows = request.Rows;
				if (request.Parts > 1)
				{
					if (Assemble(request.Player, request.Menu, request.Seq, request.Part, request.Parts, request.Text) is not { } whole)
					{
						break;
					}

					rows = ParseRows(whole);
				}
				else
				{
					Assemble(request.Player, request.Menu, request.Seq, 0, 1, request.Text);
				}

				Set(request.Menu, rows, request.Player);
				Godot.GD.Print($"[mod-rows] '{request.Menu}' now has {rows.Count} row(s) for " +
					(request.Player == 0 ? "every player" : $"player {request.Player}") +
					(request.Parts > 1 ? $" ({request.Parts} parts)" : string.Empty));
				break;
			}

			case Op.OpenScreen:
				OpenScreen(request.Screen, request.Player);
				break;
		}
	}

	/// <summary>
	/// Opens a host screen for a script, through the open-screen verb as if a row on the list with
	/// the input named it (the pause book, when it is the one standing).
	/// </summary>
	public static bool OpenScreen(uint screen, int player)
	{
		if (!Engine_UiModBoxes.PlayedHere(player))
		{
			return false;
		}

		Engine_UiCommandList list = Engine_UiModMenus.FocusedList ?? Engine_UiCommandList.HostList;
		return Engine_UiCommands.Run(Engine_UiHostScreens.OpenCommand(screen), new Engine_UiCommandContext(
			list, Player: Engine_UiModBoxes.Resolve(player))).Outcome == Engine_UiCommandOutcome.Done;
	}
}
