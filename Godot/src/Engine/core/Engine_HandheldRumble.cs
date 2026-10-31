using System;

namespace Refii.Core;

/// <summary>
/// Player one's rumble on the phone's own vibrator, while the touchscreen is the device and no
/// controller holds pad 0. The guest states two motors that run until stopped; a phone has one
/// motor and Godot's <c>Input.vibrate_handheld</c> is a ONE-SHOT of a given length and strength,
/// with no call that stops it. This turns the guest's pair into such pulses, told only when
/// something changes, the way <see cref="Engine_Rumble"/> tells a joypad.
/// </summary>
/// <remarks>
/// <para>ONE MOTOR: the stronger of the two, so a light right-motor tick is felt as lightly as the
/// pad's small motor would give it and a heavy hit as heavily. The level is quantised to
/// <see cref="Steps"/> strengths, never below the first so the faintest stated rumble is still a pulse: a
/// phone's motor shows few of Android's 255 amplitudes apart, and a decaying effect then changes
/// the pulse a handful of times rather than every frame.</para>
/// <para>BOUNDED PULSES. Each pulse runs <see cref="PulseMs"/> and is renewed after
/// <see cref="RenewMs"/> while the guest keeps stating it, so a steady rumble is continuous (the
/// overlap absorbs a slow frame at the 30 FPS cap) and a guest that stops being asked — a hang, the
/// app going to the background — leaves the phone quiet within a pulse. A changed level is told
/// at most every <see cref="MinGapMs"/>: about sixteen calls a second at worst into the Java bridge,
/// eight while a level holds, none while the pad is quiet.</para>
/// <para>STOPPING. Android's vibrator replaces an app's running one-shot with its next one, and
/// Godot passes no cancel through, so silence is told as a pulse of <see cref="StopMs"/> at the
/// least strength (<see cref="StopAmplitude"/>), which cuts the running pulse short and is not felt.
/// Unlike a start, a stop is told at once.</para>
/// <para>No Godot types: the Godot calls stay in <c>GameInput</c>, and this is what the tests cover.</para>
/// </remarks>
public sealed class Engine_HandheldRumble
{
	/// <summary>Strengths the motor level is quantised to.</summary>
	public const int Steps = 16;

	/// <summary>How long each pulse runs, in milliseconds.</summary>
	public const int PulseMs = 200;

	/// <summary>How long into a pulse it is renewed while the level holds.</summary>
	public const int RenewMs = 120;

	/// <summary>The shortest time between two pulses for a changed level.</summary>
	public const int MinGapMs = 60;

	/// <summary>The length of the pulse that stops a running one.</summary>
	public const int StopMs = 1;

	/// <summary>The stopping pulse's strength: Android's least, 1 of 255.</summary>
	public const float StopAmplitude = 1f / 255f;

	/// <summary>What the phone must be told after an observation.</summary>
	public enum Verdict
	{
		/// <summary>Nothing: the running pulse still holds, or silence while already silent.</summary>
		Unchanged,

		/// <summary>Vibrate <see cref="PulseMs"/> at <see cref="Amplitude"/>.</summary>
		Pulse,

		/// <summary>Vibrate <see cref="StopMs"/> at <see cref="StopAmplitude"/>, ending the pulse.</summary>
		Stop,
	}

	private ulong _toldAt;

	/// <summary>The strength last told, 0..1; 0 while stopped.</summary>
	public float Amplitude { get; private set; }

	/// <summary>Whether a pulse was last told and not stopped.</summary>
	public bool Running { get; private set; }

	/// <summary>
	/// The phone's strength for a guest pair: the stronger motor as a magnitude, rounded to the
	/// nearest of <see cref="Steps"/> and never below the first. Speeds under one step of the pad's
	/// motor byte are silence, as on the console (<see cref="Engine_Rumble.Quantise"/>).
	/// </summary>
	public static float Level(uint left, uint right)
	{
		uint speed = Math.Max(Engine_Rumble.Quantise(left), Engine_Rumble.Quantise(right));
		if (speed == 0)
		{
			return 0f;
		}

		return Math.Max(1f, MathF.Round(Engine_Rumble.Magnitude(speed) * Steps)) / Steps;
	}

	/// <summary>
	/// The guest's pair this frame, at <paramref name="nowMs"/> on a monotonic clock.
	/// <see cref="Verdict.Pulse"/> on a start, when the pulse is due its renewal, or when the level
	/// has changed and <see cref="MinGapMs"/> has passed; <see cref="Verdict.Stop"/> on the first
	/// silent frame after a run.
	/// </summary>
	public Verdict Observe(uint left, uint right, ulong nowMs)
	{
		float level = Level(left, right);
		if (level <= 0f)
		{
			return Silence();
		}

		ulong since = nowMs >= _toldAt ? nowMs - _toldAt : 0;
		if (Running && since < RenewMs && (level == Amplitude || since < MinGapMs))
		{
			return Verdict.Unchanged;
		}

		Amplitude = level;
		Running = true;
		_toldAt = nowMs;
		return Verdict.Pulse;
	}

	/// <summary>
	/// The phone must be quiet whatever the guest stated — the setting is off, a controller took the
	/// pad, the app lost focus, the guest is down. <see cref="Verdict.Stop"/> once, then
	/// <see cref="Verdict.Unchanged"/>.
	/// </summary>
	public Verdict Silence()
	{
		Amplitude = 0f;
		if (!Running)
		{
			return Verdict.Unchanged;
		}

		Running = false;
		return Verdict.Stop;
	}
}
