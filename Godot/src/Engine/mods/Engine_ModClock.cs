using System;
using System.Collections.Generic;

namespace Refii.Mods;

/// <summary>
/// THE GAME CLOCK AND TIMERS for Godot scripts (<c>RefiiHost.now / real_now / after / cancel</c>), by
/// the rule Lua's <c>Refii.now / after</c> keeps (<see cref="ModApi.TimerMeaning"/>), fed by the same
/// bus statements: each <c>tick</c> event's seconds, counted into <see cref="Now"/> only while no mode
/// of the <c>paused</c> event is in force, into <see cref="RealNow"/> always. A timer is due on the
/// first tick the clock reaches its time; due timers run in time order, then in the order they were
/// set. A key replaces the timer standing under it; <see cref="Cancel"/> drops by key or by the
/// number <see cref="After"/> answered for a keyless timer. Each surface counts from its own start
/// (Lua's from its VM, Godot's from its host): durations agree, absolute values need not. Pure.
/// </summary>
/// <typeparam name="T">What a timer runs: the host's Callable, a test's marker.</typeparam>
public sealed class ModClock<T>
{
	private sealed record Timer(double At, object? Key, long Seq, T Run);

	private List<Timer> _timers = new();
	private long _seq;

	/// <summary>The game clock: seconds of ticks counted while no paused mode held.</summary>
	public double Now { get; private set; }

	/// <summary>Seconds of every tick, menus included.</summary>
	public double RealNow { get; private set; }

	/// <summary>Timers standing.</summary>
	public int Count => _timers.Count;

	/// <summary>
	/// Sets <paramref name="run"/> due <paramref name="seconds"/> of game clock from now. A non-null
	/// <paramref name="key"/> replaces the timer standing under it and is the answer; else the
	/// timer's number is.
	/// </summary>
	public object After(double seconds, T run, object? key = null)
	{
		if (key is not null)
		{
			Cancel(key);
		}

		_seq++;
		_timers.Add(new Timer(Now + (double.IsFinite(seconds) ? seconds : 0), key, _seq, run));
		return key ?? _seq;
	}

	/// <summary>Drops the timer under <paramref name="key"/>, or the keyless one numbered so; true when one went.</summary>
	public bool Cancel(object key)
	{
		long? number = key switch
		{
			long l => l,
			int i => i,
			double d when d == Math.Floor(d) => (long)d,
			_ => null,
		};
		int removed = _timers.RemoveAll(t => (t.Key is not null && t.Key.Equals(key)) || (t.Key is null && number == t.Seq));
		return removed != 0;
	}

	/// <summary>
	/// One guest tick of <paramref name="seconds"/>: the real clock always, the game clock while
	/// <paramref name="paused"/> is false; the timers it made due, in due order (removed).
	/// </summary>
	public List<T> Tick(double seconds, bool paused)
	{
		var due = new List<T>();
		double dt = double.IsFinite(seconds) && seconds > 0 ? seconds : 0;
		RealNow += dt;
		if (paused)
		{
			return due;
		}

		Now += dt;
		if (!_timers.Exists(t => t.At <= Now))
		{
			return due;
		}

		var ready = new List<Timer>();
		var keep = new List<Timer>();
		foreach (Timer t in _timers)
		{
			(t.At <= Now ? ready : keep).Add(t);
		}

		_timers = keep;
		ready.Sort((a, b) => a.At != b.At ? a.At.CompareTo(b.At) : a.Seq.CompareTo(b.Seq));
		foreach (Timer t in ready)
		{
			due.Add(t.Run);
		}

		return due;
	}

	/// <summary>Drops every timer (the host going).</summary>
	public void Clear() => _timers.Clear();
}
