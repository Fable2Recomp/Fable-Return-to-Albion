using System;
using System.Collections.Generic;
using Godot;

namespace Refii.Mods;

/// <summary>
/// THE PURE PART of a mod action's chord and claim: what a <c>"chord"</c> names, which XInput bits
/// a pad binding occupies, and whether a plain action is silenced by a chord on the same button.
/// No Godot runtime here (the enums only), so the rules are held by tests; <see cref="ModInput"/>
/// reads the devices and applies them.
/// </summary>
public static class ModInputClaims
{
	/// <summary>A chord's modifier: a trigger or bumper on the pad, or a key on the keyboard.</summary>
	public enum Modifier
	{
		None,
		LT,
		RT,
		LB,
		RB,
		Key,
	}

	/// <summary>The XInput button bits the guest's pad reads (<c>X_INPUT_GAMEPAD_*</c>).</summary>
	public const uint DpadUp = 0x0001, DpadDown = 0x0002, DpadLeft = 0x0004, DpadRight = 0x0008,
		Start = 0x0010, Back = 0x0020, LeftThumb = 0x0040, RightThumb = 0x0080,
		LeftShoulder = 0x0100, RightShoulder = 0x0200,
		ButtonA = 0x1000, ButtonB = 0x2000, ButtonX = 0x4000, ButtonY = 0x8000;

	/// <summary>The pad modifiers a chord may name, as the file spells them.</summary>
	public static readonly string[] PadModifiers = { "LT", "RT", "LB", "RB" };

	/// <summary>
	/// What a <c>"chord"</c> value names: LT / RT / LB / RB (any case) a pad modifier; anything
	/// else a keyboard key, which the engine resolves (<see cref="Modifier.Key"/>); empty is none.
	/// </summary>
	public static Modifier ParseModifier(string? chord)
	{
		switch ((chord ?? string.Empty).Trim().ToLowerInvariant())
		{
			case "": return Modifier.None;
			case "lt": return Modifier.LT;
			case "rt": return Modifier.RT;
			case "lb": return Modifier.LB;
			case "rb": return Modifier.RB;
			default: return Modifier.Key;
		}
	}

	/// <summary>The XInput bit a pad button occupies on the guest's pad; 0 for one the guest never reads.</summary>
	public static uint PadBit(JoyButton button) => button switch
	{
		JoyButton.A => ButtonA,
		JoyButton.B => ButtonB,
		JoyButton.X => ButtonX,
		JoyButton.Y => ButtonY,
		JoyButton.LeftShoulder => LeftShoulder,
		JoyButton.RightShoulder => RightShoulder,
		JoyButton.LeftStick => LeftThumb,
		JoyButton.RightStick => RightThumb,
		JoyButton.Start => Start,
		JoyButton.Back => Back,
		JoyButton.DpadUp => DpadUp,
		JoyButton.DpadDown => DpadDown,
		JoyButton.DpadLeft => DpadLeft,
		JoyButton.DpadRight => DpadRight,
		_ => 0u,
	};

	/// <summary>The bits of every button in <paramref name="buttons"/>.</summary>
	public static uint PadBits(IEnumerable<JoyButton> buttons)
	{
		uint bits = 0;
		foreach (JoyButton button in buttons)
		{
			bits |= PadBit(button);
		}

		return bits;
	}

	/// <summary>The bit a pad modifier occupies (LB / RB); 0 for the triggers and a key.</summary>
	public static uint ModifierBit(Modifier modifier) => modifier switch
	{
		Modifier.LB => LeftShoulder,
		Modifier.RB => RightShoulder,
		_ => 0u,
	};

	/// <summary>
	/// Whether a pad modifier is held on a pad state: a trigger past the guest's own 30/255
	/// threshold, a bumper's bit down. A key modifier is the keyboard's (<see cref="ModInput"/>).
	/// </summary>
	public static bool ModifierHeld(Modifier modifier, uint buttons, uint leftTrigger, uint rightTrigger) => modifier switch
	{
		Modifier.LT => leftTrigger > 30,
		Modifier.RT => rightTrigger > 30,
		Modifier.LB => (buttons & LeftShoulder) != 0,
		Modifier.RB => (buttons & RightShoulder) != 0,
		_ => false,
	};

	/// <summary>
	/// Whether a PLAIN action (no chord) is silenced this frame: another declared action binds one
	/// of the same pad buttons or triggers (or keys) under a chord whose modifier is held now.
	/// <paramref name="chordHeld"/> answers whether an action's chord is held.
	/// </summary>
	public static bool PlainSuppressed(
		ModInput.Action plain, IReadOnlyList<ModInput.Action> actions, Func<ModInput.Action, bool> chordHeld,
		bool keyboard)
	{
		if (plain.Chord.Length != 0)
		{
			return false;
		}

		foreach (ModInput.Action other in actions)
		{
			if (other.Chord.Length == 0 || other.Name == plain.Name || !chordHeld(other))
			{
				continue;
			}

			if (keyboard ? SharesKey(plain, other) : SharesPad(plain, other))
			{
				return true;
			}
		}

		return false;
	}

	private static bool SharesPad(ModInput.Action a, ModInput.Action b)
	{
		(IReadOnlyList<JoyButton> aButtons, IReadOnlyList<JoyAxis> aTriggers) = ModInput.EffectivePad(a);
		(IReadOnlyList<JoyButton> bButtons, IReadOnlyList<JoyAxis> bTriggers) = ModInput.EffectivePad(b);
		foreach (JoyButton button in aButtons)
		{
			if (bButtons.Contains(button))
			{
				return true;
			}
		}

		foreach (JoyAxis trigger in aTriggers)
		{
			if (bTriggers.Contains(trigger))
			{
				return true;
			}
		}

		return false;
	}

	private static bool SharesKey(ModInput.Action a, ModInput.Action b)
	{
		foreach (Key key in ModInput.EffectiveKeys(a))
		{
			if (ModInput.EffectiveKeys(b).Contains(key))
			{
				return true;
			}
		}

		return false;
	}

	private static bool Contains<T>(this IReadOnlyList<T> list, T value) where T : struct
	{
		foreach (T item in list)
		{
			if (item.Equals(value))
			{
				return true;
			}
		}

		return false;
	}
}
