using System;
using System.Collections.Generic;
using Godot;

namespace Refii.Mods;

/// <summary>
/// A MOD'S PRESSES, the mirror of its claims: pad buttons and triggers a mod holds down on a
/// player's pad (<c>Refii.press</c> / <c>Refii.tap</c>, <c>RefiiHost.press</c> / <c>tap</c>,
/// <c>refii_mod_press</c> / <c>refii_mod_tap</c>), OR'd into that player's pad state at the point
/// claims are withheld (<see cref="ModInput.Withhold"/>, both pad paths in <c>GameInput</c>), so the
/// guest reads them as its own pad. A scripted source, not a device: it reaches the guest focused
/// or not, as the debug channel's presses do.
/// </summary>
/// <remarks>
/// <para>NAMES are input.json's (<see cref="ModInput.TryButton"/>: A B X Y LB RB L3 R3 Start Back,
/// the d-pad as DpadUp / DpadDown / DpadLeft / DpadRight) and LT / RT for the triggers. A HOLD
/// stays down until released (<c>held</c> false or 0); for a trigger <c>held</c> is the pull, 1
/// (true) a full one, 2..255 that much. A TAP is down for at least <c>ticks</c> whole guest ticks
/// (<c>game+0xFC</c>, <see cref="Refii.Host.HostBridge.SimClock"/>) from the frame it is first
/// published; with no world up the ticks count published frames, and a tap whose guest stops
/// ticking (a pause) is let go after <see cref="TapCeilingMs"/>. A trigger tap is a full pull.</para>
/// <para>Per player, 1 or 2: a press is one player's pad. Cleared with the claims — the mods' Lua
/// reloading, the game going down, the mod set reloading. Pure: no Godot runtime, the tick and
/// the clock are the caller's, so the rules are held by tests.</para>
/// </remarks>
public static class ModInputPresses
{
	/// <summary>The longest tap any surface takes, in guest ticks.</summary>
	public const int MaxTapTicks = 600;

	/// <summary>A tap is let go after this long whatever the guest's tick says.</summary>
	public const ulong TapCeilingMs = 10_000;

	/// <summary>Where a name lands on the pad: an XInput bit, or a trigger (1 LT, 2 RT).</summary>
	public readonly record struct Target(uint Bit, int Trigger);

	private sealed class TapState
	{
		public Target Target;
		public int Ticks;
		public bool Started;
		public uint StartTick;
		public bool TickKnown;
		public int Frames;
		public ulong StartMs;
	}

	/// <summary>Holds by (player, canonical name): the bit or trigger and its pull.</summary>
	private static readonly Dictionary<(int Player, string Name), (Target Target, uint Pull)> _holds = new();

	private static readonly List<(int Player, TapState Tap)> _taps = new();

	/// <summary>Whether anything is pressed for any player.</summary>
	public static bool Any => _holds.Count != 0 || _taps.Count != 0;

	/// <summary>The bit or trigger a name presses; false for a name the pad has not got.</summary>
	public static bool TryTarget(string? name, out Target target)
	{
		target = default;
		string text = (name ?? string.Empty).Trim();
		if (text.Length == 0)
		{
			return false;
		}

		if (ModInput.TryTrigger(text, out JoyAxis axis))
		{
			target = new Target(0u, axis == JoyAxis.TriggerLeft ? 1 : 2);
			return true;
		}

		if (ModInput.TryButton(text, out JoyButton button) && ModInputClaims.PadBit(button) is var bit && bit != 0u)
		{
			target = new Target(bit, 0);
			return true;
		}

		return false;
	}

	/// <summary>
	/// Holds or releases <paramref name="name"/> for <paramref name="player"/> (1 or 2).
	/// <paramref name="held"/> 0 releases; a button is down for any other value, a trigger pulled
	/// by it (1 a full pull). False for an unknown name or player.
	/// </summary>
	public static bool Press(int player, string? name, long held)
	{
		if (player is not (1 or 2) || !TryTarget(name, out Target target))
		{
			return false;
		}

		var key = (player, Canonical(target));
		if (held <= 0)
		{
			_holds.Remove(key);
			return true;
		}

		uint pull = target.Trigger == 0 || held == 1 ? 255u : (uint)Math.Min(held, 255);
		_holds[key] = (target, pull);
		return true;
	}

	/// <summary>Presses <paramref name="name"/> for at least <paramref name="ticks"/> guest ticks (1 .. <see cref="MaxTapTicks"/>).</summary>
	public static bool Tap(int player, string? name, long ticks)
	{
		if (player is not (1 or 2) || ticks < 1 || !TryTarget(name, out Target target))
		{
			return false;
		}

		_taps.Add((player, new TapState { Target = target, Ticks = (int)Math.Min(ticks, MaxTapTicks) }));
		return true;
	}

	/// <summary>
	/// ORs <paramref name="player"/>'s presses into one pad state. <paramref name="tick"/> is the
	/// guest's game tick when <paramref name="tickKnown"/>; <paramref name="nowMs"/> a monotonic
	/// clock for the tap ceiling. Called once per published frame per pad; a tap ends here.
	/// </summary>
	public static void Apply(int player, ref uint buttons, ref uint leftTrigger, ref uint rightTrigger,
		uint tick, bool tickKnown, ulong nowMs)
	{
		foreach (((int p, string _), (Target target, uint pull)) in _holds)
		{
			if (p == player)
			{
				Merge(target, pull, ref buttons, ref leftTrigger, ref rightTrigger);
			}
		}

		for (int i = _taps.Count - 1; i >= 0; i--)
		{
			(int p, TapState tap) = _taps[i];
			if (p != player)
			{
				continue;
			}

			if (!tap.Started)
			{
				tap.Started = true;
				tap.StartTick = tick;
				tap.TickKnown = tickKnown;
				tap.StartMs = nowMs;
			}

			// Down until the guest has completed `ticks` ticks after the one it was first published in.
			bool done = tap.TickKnown && tickKnown
				? unchecked(tick - tap.StartTick) > (uint)tap.Ticks
				: tap.Frames > tap.Ticks;
			if (done || nowMs - tap.StartMs >= TapCeilingMs)
			{
				_taps.RemoveAt(i);
				continue;
			}

			++tap.Frames;
			Merge(tap.Target, 255u, ref buttons, ref leftTrigger, ref rightTrigger);
		}
	}

	/// <summary>The names held for <paramref name="player"/>, as they were pressed (canonical), and the taps running.</summary>
	public static (List<string> Held, int Taps) For(int player)
	{
		var held = new List<string>();
		foreach (((int p, string name), _) in _holds)
		{
			if (p == player)
			{
				held.Add(name);
			}
		}

		int taps = 0;
		foreach ((int p, TapState _) in _taps)
		{
			if (p == player)
			{
				++taps;
			}
		}

		held.Sort(StringComparer.Ordinal);
		return (held, taps);
	}

	/// <summary>Releases every hold and tap (a Lua reload, the game going down, the set reloading).</summary>
	public static void Clear()
	{
		_holds.Clear();
		_taps.Clear();
	}

	private static void Merge(Target target, uint pull, ref uint buttons, ref uint leftTrigger, ref uint rightTrigger)
	{
		switch (target.Trigger)
		{
			case 1: leftTrigger = Math.Max(leftTrigger, pull); break;
			case 2: rightTrigger = Math.Max(rightTrigger, pull); break;
			default: buttons |= target.Bit; break;
		}
	}

	/// <summary>The name a target is listed under: the Xbox alias, the d-pad by its JoyButton name.</summary>
	public static string Canonical(Target target) => target.Trigger switch
	{
		1 => "LT",
		2 => "RT",
		_ => target.Bit switch
		{
			ModInputClaims.ButtonA => "A",
			ModInputClaims.ButtonB => "B",
			ModInputClaims.ButtonX => "X",
			ModInputClaims.ButtonY => "Y",
			ModInputClaims.LeftShoulder => "LB",
			ModInputClaims.RightShoulder => "RB",
			ModInputClaims.LeftThumb => "L3",
			ModInputClaims.RightThumb => "R3",
			ModInputClaims.Start => "Start",
			ModInputClaims.Back => "Back",
			ModInputClaims.DpadUp => "DpadUp",
			ModInputClaims.DpadDown => "DpadDown",
			ModInputClaims.DpadLeft => "DpadLeft",
			ModInputClaims.DpadRight => "DpadRight",
			_ => "0x" + target.Bit.ToString("x4", System.Globalization.CultureInfo.InvariantCulture),
		},
	};
}
