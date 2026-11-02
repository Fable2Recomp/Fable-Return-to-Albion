using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Refii.Mods;

/// <summary>
/// A VERDICT'S SYNCHRONOUS READ (<c>RefiiHost.read</c>, <see cref="ModApi.VerdictReadMeaning"/>): the
/// line the payload answers and the fields a script gets. Only a verb of <see cref="ModApi.VerdictReads"/>
/// crosses; the payload answers it with the query's own branch (mod_commands.cpp ModVerdictRead) on the
/// guest thread inside the seam, and refuses it anywhere else. Pure.
/// </summary>
public static class ModVerdictRead
{
	/// <summary>payload_bridge.h REFII_MOD_READ_OUTSIDE: not on the guest thread inside a verdict handler.</summary>
	public const int Outside = -1;

	/// <summary>payload_bridge.h REFII_MOD_READ_REFUSED: a verb that changes something, or none.</summary>
	public const int Refused = -2;

	public static bool Allowed(string verb) => ModApi.VerdictReads.Any(r => r.Verb == verb);

	/// <summary>"VERB WORD..." as the payload reads it (a query's words without its id), or null for a verb that is not a read.</summary>
	public static string? Line(string verb, IEnumerable<string> words)
	{
		if (string.IsNullOrEmpty(verb) || !Allowed(verb))
		{
			return null;
		}

		var line = new StringBuilder(verb);
		foreach (string word in words)
		{
			line.Append(' ').Append(word.Replace(' ', '_'));
		}

		return line.ToString();
	}

	/// <summary>
	/// The read's fields: the payload's answer ("ok=…,<fields>") parsed, or <c>ok = false</c> with the
	/// <c>error</c> that says why nothing was read.
	/// </summary>
	public static List<KeyValuePair<string, object>> Fields(int result, string answer, string verb) => result switch
	{
		> 0 => RefiiHost.ParseFieldsPlain(answer),
		Outside => Error($"read(\"{verb}\") answers only inside an on_verdict handler, on the game's thread; use ask(\"{verb}\", args, callback)"),
		Refused => Error($"read(\"{verb}\"): not a query verb that changes nothing (ModApi.VerdictReads); use ask"),
		_ => Error($"read(\"{verb}\"): no answer"),
	};

	private static List<KeyValuePair<string, object>> Error(string error) => new()
	{
		new KeyValuePair<string, object>("ok", false),
		new KeyValuePair<string, object>("error", error),
	};
}
