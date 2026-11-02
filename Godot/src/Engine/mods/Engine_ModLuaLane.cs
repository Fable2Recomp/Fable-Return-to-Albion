using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Refii.Mods;

/// <summary>
/// THE LANE BETWEEN A GODOT SCRIPT AND THE GAME VM'S LUA ON ONE MACHINE, both ways, over the two
/// transports that already cross it: Lua to Godot is <c>Refii.signal</c> under the core's own scope
/// <see cref="Scope"/> (never raised as <c>mod_signal</c>), Godot to Lua is a <c>run_lua</c> chunk calling
/// the preamble's <c>Refii._godot_*</c> / <c>_host_*</c>. What rides it:
/// <list type="bullet">
/// <item><c>refii.ask</c> — Lua's <c>Refii.ask_host(what, fn, ...)</c>: <c>id\nwhat\narg…</c>; the host
/// runs the read or action <c>what</c> names (<see cref="ModApi.HostCalls"/>) and, for an id above 0,
/// answers with <see cref="ReplyChunk"/>.</item>
/// <item><c>refii.answer</c> — Lua's answer to a Godot <c>call_lua</c>: <c>id\nok\nvalue…</c>.</item>
/// <item><c>refii.watch</c> — a Lua handler subscribed to <c>value_changed</c>: the host restates
/// published numbers to Lua from then on (texts it always restates, for <c>Refii.get_text</c>).</item>
/// <item><c>refii.hello</c> — a new game VM's runtime: the host states every published text and every
/// pool a Godot script declared to it again.</item>
/// </list>
/// A value crossing to the host is one line: <c>n</c> and a number, <c>s</c> and a text (<c>\\</c> and
/// <c>\n</c> escaped), <c>b1</c> / <c>b0</c>, <c>-</c> for nil (a table crosses as nil). A value crossing to
/// Lua is a Lua literal (<see cref="Literal"/>). Pure: held by tests.
/// </summary>
public static class ModLuaLane
{
	/// <summary>The scope of the core's own signals: a mod named "refii" cannot exist.</summary>
	public const string Scope = "refii";

	public const string Ask = "ask";
	public const string Answer = "answer";
	public const string Watch = "watch";
	public const string Hello = "hello";

	/// <summary>How many values one answer carries (the preamble's <c>_godot_call</c> keeps five).</summary>
	public const int AnswerValues = 5;

	/// <summary>A Lua literal for a plain value: nil, a boolean, a number, a string, a list or a string-keyed map of them.</summary>
	public static string Literal(object? value)
	{
		var text = new StringBuilder();
		Append(text, value);
		return text.ToString();
	}

	private static void Append(StringBuilder text, object? value)
	{
		switch (value)
		{
			case null:
				text.Append("nil");
				break;
			case bool b:
				text.Append(b ? "true" : "false");
				break;
			case string s:
				AppendString(text, s);
				break;
			case float f:
				text.Append(float.IsFinite(f) ? f.ToString("R", CultureInfo.InvariantCulture) : "0");
				break;
			case double d:
				text.Append(double.IsFinite(d) ? d.ToString("R", CultureInfo.InvariantCulture) : "0");
				break;
			case sbyte or byte or short or ushort or int or uint or long or ulong:
				text.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
				break;
			case IDictionary map:
				text.Append('{');
				foreach (DictionaryEntry entry in map)
				{
					text.Append('[');
					Append(text, entry.Key is string ? entry.Key : Convert.ToString(entry.Key, CultureInfo.InvariantCulture));
					text.Append("]=");
					Append(text, entry.Value);
					text.Append(',');
				}

				text.Append('}');
				break;
			case IEnumerable list:
				text.Append('{');
				foreach (object? item in list)
				{
					Append(text, item);
					text.Append(',');
				}

				text.Append('}');
				break;
			default:
				AppendString(text, Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);
				break;
		}
	}

	/// <summary>A double-quoted Lua string: quote, backslash, line breaks and control bytes escaped as decimal.</summary>
	private static void AppendString(StringBuilder text, string s)
	{
		text.Append('"');
		foreach (char c in s)
		{
			switch (c)
			{
				case '"': text.Append("\\\""); break;
				case '\\': text.Append("\\\\"); break;
				case '\n': text.Append("\\n"); break;
				case '\r': text.Append("\\r"); break;
				default:
					if (c < ' ' || c == '\x7f')
					{
						text.Append('\\').Append(((int)c).ToString("000", CultureInfo.InvariantCulture));
					}
					else
					{
						text.Append(c);
					}

					break;
			}
		}

		text.Append('"');
	}

	/// <summary>One value line off the lane as a plain value (double, string, bool or null).</summary>
	public static object? Decode(string line)
	{
		if (line.Length == 0 || line == "-")
		{
			return null;
		}

		switch (line[0])
		{
			case 'n':
				return double.TryParse(line.AsSpan(1), NumberStyles.Float, CultureInfo.InvariantCulture, out double number) ? number : null;
			case 'b':
				return line == "b1";
			case 's':
				return Unescape(line[1..]);
			default:
				return null;
		}
	}

	private static string Unescape(string text)
	{
		if (text.IndexOf('\\') < 0)
		{
			return text;
		}

		var plain = new StringBuilder(text.Length);
		for (int i = 0; i < text.Length; i++)
		{
			if (text[i] == '\\' && i + 1 < text.Length)
			{
				plain.Append(text[++i] == 'n' ? '\n' : text[i]);
			}
			else
			{
				plain.Append(text[i]);
			}
		}

		return plain.ToString();
	}

	/// <summary>An ask off the lane: the id (0: no answer wanted), what it names, its arguments.</summary>
	public readonly record struct Request(long Id, string What, object?[] Args);

	/// <summary>The ask in <paramref name="payload"/>, or null when it is not one.</summary>
	public static Request? ParseAsk(string payload)
	{
		string[] lines = payload.Split('\n');
		if (lines.Length < 2 || !long.TryParse(lines[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long id) ||
			id < 0 || lines[1].Length == 0)
		{
			return null;
		}

		return new Request(id, lines[1], lines.Skip(2).Select(Decode).ToArray());
	}

	/// <summary>A Lua answer to a call_lua: the id, whether the expression ran, its values (trailing nils dropped).</summary>
	public readonly record struct Reply(long Id, bool Ok, object?[] Values);

	/// <summary>The answer in <paramref name="payload"/>, or null when it is not one.</summary>
	public static Reply? ParseAnswer(string payload)
	{
		string[] lines = payload.Split('\n');
		if (lines.Length < 2 || !long.TryParse(lines[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long id) || id <= 0)
		{
			return null;
		}

		var values = lines.Skip(2).Take(AnswerValues).Select(Decode).ToList();
		while (values.Count != 0 && values[^1] is null)
		{
			values.RemoveAt(values.Count - 1);
		}

		return new Reply(id, lines[1] == "b1", values.ToArray());
	}

	/// <summary>The run_lua chunk handing an ask's answer back to Lua's callback.</summary>
	public static string ReplyChunk(long id, object? value) =>
		"if Refii and Refii._host_reply then Refii._host_reply(" + id.ToString(CultureInfo.InvariantCulture) + ", " + Literal(value) + ") end";

	/// <summary>The run_lua chunk running <paramref name="expression"/> in the game VM and answering its values on the lane.</summary>
	public static string CallChunk(long id, string expression) =>
		"if Refii and Refii._godot_call then Refii._godot_call(" + id.ToString(CultureInfo.InvariantCulture) +
		", function() return " + expression.Replace("\r", string.Empty).Replace('\n', ' ') + " end) end";

	/// <summary>The run_lua chunk raising Lua's mod_signal event for a Godot <c>signal_lua</c>.</summary>
	public static string SignalChunk(string scoped, string payload) =>
		"if Refii and Refii._godot_signal then Refii._godot_signal(" + Literal(scoped) + ", " + Literal(payload) + ") end";

	/// <summary>
	/// The run_lua chunk stating published values to Lua: each (key, value, text) — texts kept for
	/// <c>Refii.get_text</c>; <paramref name="changed"/> raises Lua's value_changed for each.
	/// </summary>
	public static string ValuesChunk(IEnumerable<(string Key, object Value, bool Text)> values, bool changed)
	{
		var text = new StringBuilder("if Refii and Refii._host_values then Refii._host_values({");
		foreach ((string key, object value, bool isText) in values)
		{
			text.Append('{');
			AppendString(text, key);
			text.Append(',');
			Append(text, value);
			text.Append(isText ? ",true}," : ",false},");
		}

		text.Append("}, ").Append(changed ? "true" : "false").Append(") end");
		return text.ToString();
	}

	/// <summary>The run_lua chunk declaring a pool in the Lua runtime for a Godot <c>pool</c>: numbers only, max_key when given.</summary>
	public static string PoolChunk(int player, string key, double max, double floor, double regen, double delay, string maxKey)
	{
		var text = new StringBuilder("if Refii and Refii.pool then Refii.pool(");
		text.Append(player.ToString(CultureInfo.InvariantCulture)).Append(", ").Append(Literal(key)).Append(", {max=").Append(Literal(max))
			.Append(",floor=").Append(Literal(floor)).Append(",regen=").Append(Literal(regen)).Append(",delay=").Append(Literal(delay));
		if (!string.IsNullOrEmpty(maxKey))
		{
			text.Append(",max_key=").Append(Literal(maxKey));
		}

		text.Append("}) end");
		return text.ToString();
	}
}
