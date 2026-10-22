using System;

namespace Refii.Core;

/// <summary>
/// One guest pad's rumble, as the guest last stated it through <c>XamInputSetState</c>, tracked
/// so the host joypad is told only when the pair changes. The guest's pad flush
/// (<c>sub_822B38C0</c>) writes both motors every frame a pad is connected, so without this a
/// steady rumble would restart the joypad's effect every frame and a silent pad would be
/// stopped every frame.
/// </summary>
/// <remarks>
/// <para>The motors are XInput's: the LEFT motor is the heavy low-frequency one, the RIGHT the
/// light high-frequency one. Godot names the same pair by feel — <c>strong</c> and <c>weak</c> —
/// so left maps to strong and right to weak. Speeds are 0..65535.</para>
/// <para>THE PAD'S OWN STEP. The 360 pad's rumble output report carries ONE BYTE per motor;
/// XInput scales the WORD the game states down to it (<c>speed &gt;&gt; 8</c>), so the console's
/// motor never saw finer than 256 steps. The pair is quantised to that byte
/// (<see cref="Quantise"/>) before it is compared, so a decaying effect restarts the joypad's
/// effect only when the byte the pad would have received changes, and a speed under one step
/// is the silence it was on the console. Full scale stays full: the byte is scaled back up by
/// 0x101 so 0xFF reads 0xFFFF.</para>
/// <para>No Godot types: the Godot calls stay in <c>GameInput</c>, and this is what the tests cover.</para>
/// </remarks>
public sealed class Engine_Rumble
{
	/// <summary>XInput's full motor speed.</summary>
	public const uint FullScale = 0xFFFF;

	/// <summary>Speeds per step of the pad's motor byte.</summary>
	public const uint Quantum = 0x100;

	/// <summary>What the host joypad must be told after an observation.</summary>
	public enum Verdict
	{
		/// <summary>Nothing: the same pair as last time, or silence while already silent.</summary>
		Unchanged,

		/// <summary>Start (or restart) the effect at <see cref="Weak"/>/<see cref="Strong"/>.</summary>
		Start,

		/// <summary>Stop the effect.</summary>
		Stop,
	}

	/// <summary>The left motor speed last applied, quantised, 0..65535.</summary>
	public uint Left { get; private set; }

	/// <summary>The right motor speed last applied, quantised, 0..65535.</summary>
	public uint Right { get; private set; }

	/// <summary>Whether the joypad was last told to run.</summary>
	public bool Running { get; private set; }

	/// <summary>Godot's weak (high-frequency) magnitude for the pair last applied, 0..1.</summary>
	public float Weak => Magnitude(Right);

	/// <summary>Godot's strong (low-frequency) magnitude for the pair last applied, 0..1.</summary>
	public float Strong => Magnitude(Left);

	/// <summary>A motor speed as a Godot magnitude, 0..1.</summary>
	public static float Magnitude(uint speed) => Math.Min(speed, FullScale) / (float)FullScale;

	/// <summary>
	/// A stated speed at the pad's own resolution: the byte the 360 pad's report carries, scaled
	/// back to full range so 0xFF00..0xFFFF reads 0xFFFF and anything under 0x100 reads 0.
	/// </summary>
	public static uint Quantise(uint speed) => (Math.Min(speed, FullScale) / Quantum) * 0x101u;

	/// <summary>
	/// The guest's pair this frame. <see cref="Verdict.Start"/> whenever a non-silent pair differs
	/// from the one running at the pad's resolution; <see cref="Verdict.Stop"/> on the first silent
	/// frame after a run.
	/// </summary>
	public Verdict Observe(uint left, uint right)
	{
		left = Quantise(left);
		right = Quantise(right);
		if (left == 0 && right == 0)
		{
			return Silence();
		}

		if (Running && left == Left && right == Right)
		{
			return Verdict.Unchanged;
		}

		Left = left;
		Right = right;
		Running = true;
		return Verdict.Start;
	}

	/// <summary>
	/// The pad must be quiet whatever the guest stated — the window lost focus, the joypad went,
	/// the guest is down. <see cref="Verdict.Stop"/> once, then <see cref="Verdict.Unchanged"/>.
	/// </summary>
	public Verdict Silence()
	{
		Left = 0;
		Right = 0;
		if (!Running)
		{
			return Verdict.Unchanged;
		}

		Running = false;
		return Verdict.Stop;
	}
}
