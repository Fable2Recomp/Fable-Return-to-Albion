using System;
using System.Collections.Generic;
using System.Globalization;
using Refii.Host;

namespace Refii.Mods;

/// <summary>
/// THE HOST'S OWN QUESTIONS to the guest tick, on the lane a script's <c>RefiiHost.query</c> uses
/// (mod_commands.cpp <c>query ID WHAT ...</c>, answered under REFII_MOD_HOST_REPLY_KIND): a host list
/// that needs what only the guest knows asks here, and its answer never reaches a script's
/// <c>reply</c> signal. Ids count down from -1, so they never meet a script's (which count up).
/// </summary>
/// <remarks>Main thread only (Godot's frame and RefiiHost's drain).</remarks>
public static class ModHostQueries
{
	private static long _next;
	private static readonly Dictionary<long, Action<IReadOnlyDictionary<string, object>>> _pending = new();

	/// <summary>Questions sent and not answered yet.</summary>
	public static int Pending => _pending.Count;

	/// <summary>
	/// Asks <paramref name="what"/> with its argument words; <paramref name="answered"/> gets the reply's
	/// fields (<c>ok</c> and the verb's own). The id, or 0 when no guest is up to ask.
	/// </summary>
	public static long Ask(string what, string arguments, Action<IReadOnlyDictionary<string, object>> answered)
	{
		if (!HostBridge.IsGuestUp)
		{
			return 0;
		}

		long id = --_next;
		_pending[id] = answered;
		HostBridge.PostModCommand(string.Create(CultureInfo.InvariantCulture, $"query {id} {what} {arguments}").TrimEnd());
		return id;
	}

	/// <summary>A reply off the bus: true when it answered one of these questions (and the callback ran).</summary>
	public static bool Take(IReadOnlyList<KeyValuePair<string, object>> fields)
	{
		// Every reply passes here and nearly all are a script's: its fields are copied only when ours.
		long id = 0;
		for (int i = 0; i < fields.Count; i++)
		{
			if (fields[i].Key == "id" && fields[i].Value is long l)
			{
				id = l;
			}
		}

		if (id >= 0 || !_pending.Remove(id, out Action<IReadOnlyDictionary<string, object>>? answered))
		{
			return false;
		}

		var values = new Dictionary<string, object>(StringComparer.Ordinal);
		foreach ((string key, object value) in fields)
		{
			values[key] = value;
		}

		answered(values);
		return true;
	}

	/// <summary>Forgets every question in flight (the guest went down: they will never be answered).</summary>
	public static void Clear() => _pending.Clear();
}
