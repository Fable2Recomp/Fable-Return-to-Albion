using System;
using System.Collections.Generic;
using Godot;
using Refii.Host;

namespace Refii.Mods;

/// <summary>
/// The mods' Lua errors in the running game, host side: the last <see cref="Capacity"/> of
/// them, each numbered, stamped and attributed. Filled once per frame from
/// <see cref="HostBridge.DrainModErrors"/> (init and handler errors, each "label: message",
/// where the label is the mod for an init error and <c>event &lt;kind&gt;</c> for a handler's);
/// read by the debug channel's <c>mod-errors</c> op so the Mod Studio shows them live.
/// </summary>
public static class ModErrors
{
	/// <summary>How many errors are kept; older ones fall off.</summary>
	public const int Capacity = 500;

	/// <summary>One error: its number (from 1, never reused in a run), when it was drained, who raised it, what it said.</summary>
	public sealed record Entry(long Seq, DateTime Time, string Mod, string Text);

	private static readonly object Gate = new();

	private static readonly Queue<Entry> Ring = new();

	private static long _sequence;

	/// <summary>The number of the newest error, 0 before the first.</summary>
	public static long Sequence
	{
		get
		{
			lock (Gate)
			{
				return _sequence;
			}
		}
	}

	/// <summary>Drains the guest's errors into the ring; once per frame. Nothing while the guest is down.</summary>
	public static void Pump()
	{
		List<string> drained = HostBridge.DrainModErrors();
		if (drained.Count == 0)
		{
			return;
		}

		DateTime now = DateTime.Now;
		lock (Gate)
		{
			foreach (string line in drained)
			{
				string text = line.TrimEnd('\r');
				if (text.Length == 0)
				{
					continue;
				}

				(string mod, string message) = Split(text);
				Ring.Enqueue(new Entry(++_sequence, now, mod, message));
				while (Ring.Count > Capacity)
				{
					Ring.Dequeue();
				}

				GD.PushWarning("[mods] lua error: " + text);
			}
		}
	}

	/// <summary>Every kept error numbered after <paramref name="sinceSequence"/>, oldest first.</summary>
	public static List<Entry> Recent(long sinceSequence)
	{
		var recent = new List<Entry>();
		lock (Gate)
		{
			foreach (Entry entry in Ring)
			{
				if (entry.Seq > sinceSequence)
				{
					recent.Add(entry);
				}
			}
		}

		return recent;
	}

	/// <summary>
	/// "label: message" into its two halves at the first ": ". A handler's label
	/// <c>event &lt;kind&gt;</c> is given the event's Lua name; a line with no label is the mods'.
	/// </summary>
	private static (string Mod, string Message) Split(string line)
	{
		int colon = line.IndexOf(": ", StringComparison.Ordinal);
		if (colon <= 0)
		{
			return ("mods", line);
		}

		string label = line[..colon];
		string message = line[(colon + 2)..];
		const string eventPrefix = "event ";
		if (label.StartsWith(eventPrefix, StringComparison.Ordinal) &&
			int.TryParse(label.AsSpan(eventPrefix.Length), out int kind))
		{
			foreach (ModApiEvent known in ModApi.Events)
			{
				if (known.Kind == kind)
				{
					return ($"handler for \"{known.LuaName}\"", message);
				}
			}
		}

		return (label, message);
	}
}
