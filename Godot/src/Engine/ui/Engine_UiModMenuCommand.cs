using System;
using System.Globalization;
using System.Text;

namespace Refii.Ui;

/// <summary>
/// The two row commands a MOD's menu row carries, parsed: <c>Opens:&lt;menu&gt;</c> opens a menu
/// <c>ui/templates.json</c> defines as a list over the one it was chosen from, and
/// <c>Mod:lua:&lt;function&gt;[:&lt;arg&gt;]</c> calls a global Lua function in the game VM with the
/// argument and the row's <c>mod_menu_row</c> event table.
/// </summary>
/// <remarks>
/// Pure text work — the parse, the Lua chunk and the guest command line — so the grammar is held by
/// tests without a Godot type in the host. <see cref="Engine_UiModMenus"/> runs them.
/// </remarks>
public static class Engine_UiModMenuCommand
{
	/// <summary>Opens the named menu as a list over the current one; B returns.</summary>
	public const string OpensPrefix = "Opens:";

	/// <summary>Calls a Lua global function: <c>Mod:lua:&lt;function&gt;[:&lt;arg&gt;]</c>.</summary>
	public const string LuaPrefix = "Mod:lua:";

	/// <summary>The guest command that raises the <c>ModMenuRow</c> event (src/vfs/src/hooks/mod_commands.cpp).</summary>
	public const string RowCommandVerb = "menu_row";

	public enum Verb
	{
		None,
		Opens,
		Lua,
	}

	/// <summary>
	/// A parsed command: <see cref="Menu"/> for <see cref="Verb.Opens"/>; <see cref="Function"/> and
	/// <see cref="Argument"/> (null when the command states none) for <see cref="Verb.Lua"/>.
	/// </summary>
	public readonly record struct Parsed(Verb Verb, string Menu, string Function, string? Argument);

	public static readonly Parsed NotOurs = new(Verb.None, string.Empty, string.Empty, null);

	/// <summary>
	/// The command's meaning, or <see cref="NotOurs"/> when it is neither form or names nothing: an
	/// empty menu, or a function that is not a Lua name path (<see cref="IsLuaFunctionName"/>).
	/// </summary>
	public static Parsed Parse(string? command)
	{
		if (string.IsNullOrEmpty(command))
		{
			return NotOurs;
		}

		if (command.StartsWith(OpensPrefix, StringComparison.Ordinal))
		{
			string menu = command[OpensPrefix.Length..].Trim();
			return menu.Length == 0 ? NotOurs : new Parsed(Verb.Opens, menu, string.Empty, null);
		}

		if (command.StartsWith(LuaPrefix, StringComparison.Ordinal))
		{
			string body = command[LuaPrefix.Length..];
			int colon = body.IndexOf(':');
			string function = (colon < 0 ? body : body[..colon]).Trim();
			string? argument = colon < 0 ? null : body[(colon + 1)..];
			return IsLuaFunctionName(function)
				? new Parsed(Verb.Lua, string.Empty, function, argument)
				: NotOurs;
		}

		return NotOurs;
	}

	/// <summary>
	/// A global's name or a dotted path through tables (<c>my_mod.on_row</c>): identifiers only, so
	/// the name is looked up, never compiled.
	/// </summary>
	public static bool IsLuaFunctionName(string name)
	{
		if (name.Length == 0)
		{
			return false;
		}

		bool start = true;
		foreach (char c in name)
		{
			if (c == '.')
			{
				if (start)
				{
					return false;
				}

				start = true;
				continue;
			}

			bool letter = c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or '_';
			if (!letter && !(c is >= '0' and <= '9' && !start))
			{
				return false;
			}

			start = false;
		}

		return !start;
	}

	/// <summary>
	/// The chunk that calls the row's function: <c>Refii._menu_row(function, arg, e)</c>, defined by
	/// the init chunk's preamble (Engine_ModLua), which looks the name up and runs it protected.
	/// <c>e</c> is the same table the <c>mod_menu_row</c> event carries.
	/// </summary>
	public static string LuaCall(Parsed command, string menu, string row, int player, string mod,
		string rowCommand = "", string button = "")
	{
		var text = new StringBuilder("if Refii and Refii._menu_row then Refii._menu_row(");
		text.Append(LuaLiteral(command.Function)).Append(", ");
		text.Append(command.Argument is null ? "nil" : LuaLiteral(command.Argument)).Append(", {");
		text.Append("menu=").Append(LuaLiteral(menu));
		text.Append(",row=").Append(LuaLiteral(row));
		text.Append(",player=").Append(player.ToString(CultureInfo.InvariantCulture));
		text.Append(",mod=").Append(LuaLiteral(mod));
		text.Append(",command=").Append(LuaLiteral(rowCommand ?? string.Empty));
		text.Append(",button=").Append(LuaLiteral(button ?? string.Empty));
		text.Append("}) end");
		return text.ToString();
	}

	/// <summary>
	/// The guest command line that raises <c>ModMenuRow</c>: <c>menu_row &lt;player&gt; &lt;menu&gt;
	/// &lt;row&gt; &lt;mod&gt; &lt;command&gt; &lt;button&gt;</c>. Words are space-separated, so a space
	/// in a name becomes '_'; an empty name is '-'. The row's own command (a row action's trailing
	/// field) crosses base64, since it carries any character; '-' for none.
	/// </summary>
	public static string RowCommandLine(string menu, string row, int player, string mod,
		string rowCommand = "", string button = "") =>
		$"{RowCommandVerb} {player.ToString(CultureInfo.InvariantCulture)} {Word(menu)} {Word(row)} {Word(mod)} " +
		$"{(string.IsNullOrEmpty(rowCommand) ? "-" : Convert.ToBase64String(Encoding.UTF8.GetBytes(rowCommand)))} {Word(button ?? string.Empty)}";

	private static string Word(string text)
	{
		string word = text.Trim().Replace(' ', '_').Replace('\t', '_').Replace('\n', '_').Replace('\r', '_');
		return word.Length == 0 ? "-" : word;
	}

	/// <summary>A Lua string literal for any text: single-quoted, with every escape Lua 5.1 reads.</summary>
	public static string LuaLiteral(string text)
	{
		var literal = new StringBuilder(text.Length + 2).Append('\'');
		foreach (char c in text)
		{
			switch (c)
			{
				case '\\': literal.Append("\\\\"); break;
				case '\'': literal.Append("\\'"); break;
				case '\n': literal.Append("\\n"); break;
				case '\r': literal.Append("\\r"); break;
				default:
					if (c < 0x20)
					{
						literal.Append('\\').Append(((int)c).ToString("000", CultureInfo.InvariantCulture));
					}
					else
					{
						literal.Append(c);
					}

					break;
			}
		}

		return literal.Append('\'').ToString();
	}
}
