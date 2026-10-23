using System.Collections.Concurrent;

namespace Refii.Core;

/// <summary>
/// What the main thread tells the hang watchdog: its frame stamp and the phase it enters. The
/// watchdog itself is native (hang_watchdog.cpp) and reads these from its own thread when the
/// stamp stops moving; this is the feed, kept Godot-free so the index cache is testable.
/// </summary>
/// <remarks>
/// A phase is passed as a registered INDEX, not a string: `Measure` opens a phase on every
/// frame of every screen and the native side keeps a fixed table, so each name crosses the
/// bridge once and is cached here. A name the table refused (0) is cached as 0 too, so a full
/// table costs one call per name, not one per frame.
///
/// No log feed, by design: Godot's logger chain dispatches into every managed Logger through
/// the C# glue, which looks StringNames up, and a print made under the StringName mutex (the
/// 2026-10-21 freeze) would deadlock on it. The report names Godot's log file instead.
/// </remarks>
public static class Engine_HangWatchdogFeed
{
	private static readonly ConcurrentDictionary<string, uint> Indices = new();

	private static System.Func<string, uint>? _register;
	private static System.Action<uint>? _enter;

	/// <summary>Phase names registered through <see cref="OnPhaseEnter"/> so far.</summary>
	public static int RegisteredCount => Indices.Count;

	/// <summary>
	/// Wires the feed: <paramref name="register"/> turns a phase name into its native index,
	/// <paramref name="enter"/> stamps an index, <paramref name="alive"/> stamps a frame.
	/// Hooks <see cref="Engine_FramePhases"/>.
	/// </summary>
	public static void Install(System.Func<string, uint> register, System.Action<uint> enter,
		System.Action<long> alive)
	{
		Indices.Clear();
		_register = register;
		_enter = enter;
		Engine_FramePhases.PhaseEnter = OnPhaseEnter;
		Engine_FramePhases.FrameAlive = alive;
	}

	/// <summary>The index for a phase name, registering it on first sight; 0 when refused.</summary>
	public static uint IndexOf(string name)
	{
		if (Indices.TryGetValue(name, out uint known))
		{
			return known;
		}

		uint index = _register?.Invoke(name) ?? 0u;
		Indices[name] = index;
		return index;
	}

	/// <summary>The main thread entered a phase: its index goes to the watchdog, 0 never does.</summary>
	public static void OnPhaseEnter(string name)
	{
		uint index = IndexOf(name);
		if (index != 0u)
		{
			_enter?.Invoke(index);
		}
	}
}
