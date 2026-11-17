using System;
using GdUnit4;
using Refii.Entities;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>The far rigs' real-time write rate: what it writes per second and per walk.</summary>
[TestSuite]
public sealed class FarRigPoseRateTests
{
	/// <summary>Walks of <paramref name="rigs"/> always-visible far rigs; per-rig writes and the busiest and idlest walk.</summary>
	private static (int[] Writes, int MostInAWalk, int FewestInAWalk) Simulate(
		double fps, double tickHz, float hz, bool spread, double seconds, int rigs)
	{
		var writes = new int[rigs];
		var lastMs = new ulong[rigs];
		int most = 0;
		int fewest = int.MaxValue;
		double from = 0.0;
		double to = 0.0;
		long lastTick = -1;
		int walks = (int)(seconds * fps);
		for (int w = 1; w <= walks; ++w)
		{
			double now = 100.0 + (w / fps);
			long tick = (long)Math.Floor(now * tickHz);
			bool tickStep = tick != lastTick;
			lastTick = tick;
			if (tickStep)
			{
				from = to;
				to = now;
			}

			ulong nowMs = (ulong)(now * 1000.0);
			int thisWalk = 0;
			for (int r = 0; r < rigs; ++r)
			{
				uint address = 0x40000000u + ((uint)r * 0x340u);
				ulong since = lastMs[r] == 0 ? ulong.MaxValue / 4 : nowMs - lastMs[r];
				if (Engine_FarRigPoseRate.Due(tickStep, from, to, hz, spread, address, since))
				{
					++writes[r];
					lastMs[r] = nowMs;
					++thisWalk;
				}
			}

			// The first second settles the never-written rigs.
			if (now > 101.0 && tickStep)
			{
				most = Math.Max(most, thisWalk);
				fewest = Math.Min(fewest, thisWalk);
			}
		}

		return (writes, most, fewest);
	}

	[TestCase]
	public void TheRateIsClamped()
	{
		AssertThat(Engine_FarRigPoseRate.Clamp(0f)).IsEqual(0f);
		AssertThat(Engine_FarRigPoseRate.Clamp(-3f)).IsEqual(0f);
		AssertThat(Engine_FarRigPoseRate.Clamp(float.NaN)).IsEqual(0f);
		AssertThat(Engine_FarRigPoseRate.Clamp(1f)).IsEqual(Engine_FarRigPoseRate.MinHz);
		AssertThat(Engine_FarRigPoseRate.Clamp(120f)).IsEqual(Engine_FarRigPoseRate.MaxHz);
		AssertThat(Engine_FarRigPoseRate.Clamp(7.5f)).IsEqual(7.5f);
	}

	/// <summary>Never between tick steps, whatever the slot or the wait: the shadow mirror writes only on those.</summary>
	[TestCase]
	public void NeverOffATickStep()
	{
		AssertThat(Engine_FarRigPoseRate.Due(false, 0.0, 10.0, 7.5f, true, 1u, 100000ul)).IsFalse();
		AssertThat(Engine_FarRigPoseRate.Due(true, 0.0, 10.0, 7.5f, true, 1u, 0ul)).IsTrue();
	}

	/// <summary>A rig that missed two of its slots writes on the next tick step.</summary>
	[TestCase]
	public void AStaleRigWritesAtOnce()
	{
		// A walk so short no slot of either phase can lie in it.
		AssertThat(Engine_FarRigPoseRate.Due(true, 10.0, 10.0, 10f, false, 1u, 150ul)).IsFalse();
		AssertThat(Engine_FarRigPoseRate.Due(true, 10.0, 10.0, 10f, false, 1u, 200ul)).IsTrue();
	}

	/// <summary>
	/// At 14 fps every walk is a tick step (the S10e): the guest's divider writes every far rig on
	/// every walk; 7 Hz writes each rig seven times a second, spread so every walk does half.
	/// </summary>
	[TestCase]
	public void UnderThirtyFpsTheRateHolds()
	{
		(int[] writes, int most, int fewest) = Simulate(fps: 14.0, tickHz: 15.0, hz: 7f, spread: true, seconds: 10.0, rigs: 200);
		foreach (int count in writes)
		{
			AssertThat(count >= 69 && count <= 72).OverrideFailureMessage($"{count} writes in 10 s").IsTrue();
		}

		// 200 rigs at 7 of 14 walks a second: about 100 a walk, never all and never none.
		AssertThat(most < 140 && fewest > 60).OverrideFailureMessage($"{fewest}-{most} a walk").IsTrue();
	}

	/// <summary>Together, every far rig writes on the same walks and none on the others.</summary>
	[TestCase]
	public void TogetherTheyShareOnePhase()
	{
		(int[] writes, int most, int fewest) = Simulate(fps: 14.0, tickHz: 15.0, hz: 7f, spread: false, seconds: 10.0, rigs: 50);
		AssertThat(most).IsEqual(50);
		AssertThat(fewest).IsEqual(0);
		foreach (int count in writes)
		{
			AssertThat(count).IsEqual(writes[0]);
		}
	}

	/// <summary>At 60 fps with the tick on one walk in four, 15 Hz writes on every tick step, as the guest's divider does.</summary>
	[TestCase]
	public void AtTheGuestsRateItIsTheGuestsDivider()
	{
		(int[] writes, _, int fewest) = Simulate(fps: 60.0, tickHz: 15.0, hz: 15f, spread: true, seconds: 4.0, rigs: 40);
		foreach (int count in writes)
		{
			AssertThat(count >= 58 && count <= 61).OverrideFailureMessage($"{count} writes in 4 s").IsTrue();
		}

		AssertThat(fewest).IsEqual(40);
	}

	[TestCase]
	public void PhasesSpreadOverTheSlot()
	{
		var buckets = new int[4];
		for (uint address = 0x40000000u; address < 0x40000000u + (400u * 0x340u); address += 0x340u)
		{
			double phase = Engine_FarRigPoseRate.Phase(address);
			AssertThat(phase >= 0.0 && phase < 1.0).IsTrue();
			++buckets[(int)(phase * 4.0)];
		}

		foreach (int bucket in buckets)
		{
			AssertThat(bucket > 60 && bucket < 140).OverrideFailureMessage($"{bucket} of 400 in a quarter").IsTrue();
		}
	}

	[TestCase]
	public void HasBonePoseChangedIgnoresUnnormalizedAndAntipodalIdenticalQuaternions()
	{
		var pos = new Godot.Vector3(1.25f, -0.5f, 3.0f);
		var rot = Godot.Quaternion.FromEuler(new Godot.Vector3(0.3f, -0.7f, 1.1f)).Normalized();
		var unnormalizedSame = new Godot.Quaternion(rot.X * 0.985f, rot.Y * 0.985f, rot.Z * 0.985f, rot.W * 0.985f);
		var antipodalSame = new Godot.Quaternion(-rot.X, -rot.Y, -rot.Z, -rot.W);

		AssertThat(Engine_EntitySkeleton.HasBonePoseChanged(pos, pos, rot, unnormalizedSame)).IsFalse();
		AssertThat(Engine_EntitySkeleton.HasBonePoseChanged(pos, pos, rot, antipodalSame)).IsFalse();

		var rotated = Godot.Quaternion.FromEuler(new Godot.Vector3(0.35f, -0.7f, 1.1f)).Normalized();
		var moved = pos + new Godot.Vector3(0.01f, 0f, 0f);
		AssertThat(Engine_EntitySkeleton.HasBonePoseChanged(pos, pos, rot, rotated)).IsTrue();
		AssertThat(Engine_EntitySkeleton.HasBonePoseChanged(pos, moved, rot, rot)).IsTrue();
	}

	[TestCase]
	public void ObservePropAndFreewheelDriveHostClockAutonomously()
	{
		var playhead = new Refii.Animations.Engine_ClipPlayhead();

		// Initial trigger of a 1.5s one-shot door open animation at t = 0.05s:
		playhead.ObserveProp(previous: 0.0, current: 0.05, tick: 10u, length: 1.5, loop: false, drawTick: 10.0);

		// Advance 60 display frames (1.0 second) on the Host clock while guest ticks continue:
		double last = playhead.Time;
		for (int frame = 1; frame <= 60; ++frame)
		{
			uint guestTick = 10u + (uint)(frame / 4);
			double drawTick = 10.0 + (frame / 4.0);
			double guestTime = Math.Min(1.5, (frame / 4) * (1.0 / 15.0));
			playhead.ObserveProp(previous: guestTime, current: guestTime, tick: guestTick, length: 1.5, loop: false, drawTick: drawTick);
			double now = playhead.Freewheel(1.0 / 60.0, drawTick);
			AssertThat(now > last).IsTrue();
			last = now;
		}

		// After 1.0s of 60 Hz host-clock stepping from 0.0s, playhead is at ~1.0s * Pace:
		double expected = 1.0 * Refii.Core.Engine_DrawAdvance.Pace;
		AssertThat(Math.Abs(last - expected) < 1e-2).OverrideFailureMessage($"last={last} expected={expected} rate={playhead.Rate}").IsTrue();

		// Stepping another 2.0s clamps cleanly at the 1.5s one-shot clip length:
		for (int frame = 1; frame <= 120; ++frame)
		{
			playhead.Freewheel(1.0 / 60.0, 25.0 + (frame / 4.0));
		}
		AssertThat(Math.Abs(playhead.Time - 1.5) < 1e-5).IsTrue();

		// A restart cut on the guest (current < Time - 0.15) re-anchors the prop playhead:
		playhead.ObserveProp(previous: 0.0, current: 0.02, tick: 40u, length: 1.5, loop: false, drawTick: 40.0);
		AssertThat(Math.Abs(playhead.Time - 0.0) < 1e-5).IsTrue();
	}

	/// <summary>
	/// A looping prop clip runs at the rate its stated time moves: the loop byte is every control's
	/// default (`sub_82283BB8`), and only SetTime (`sub_82200F60`) or a cut (`sub_822B4970`) moves
	/// the time, so a constant pair is a held pose.
	/// </summary>
	[TestCase]
	public void ObservePropRunsALoopingClipAtItsStatedRate()
	{
		var held = new Refii.Animations.Engine_ClipPlayhead();
		for (int frame = 0; frame < 120; ++frame)
		{
			uint tick = 10u + (uint)(frame / 4);
			double drawTick = 10.0 + (frame / 4.0);
			held.ObserveProp(previous: 0.4, current: 0.4, tick: tick, length: 2.0, loop: true, drawTick: drawTick);
			held.Freewheel(1.0 / 60.0, drawTick);
		}

		AssertThat(held.Rate).IsEqual(0.0);
		AssertThat(Math.Abs(held.Time - 0.4) < 1e-9).OverrideFailureMessage($"held time {held.Time}").IsTrue();

		// SetTime each tick, one tick's step on: the clip runs at 1x and keeps running past the wrap.
		const double step = 1.0 / Refii.Core.Engine_TickClock.GameTicksPerSecond;
		var moving = new Refii.Animations.Engine_ClipPlayhead();
		double start = double.NaN;
		for (int frame = 0; frame < 120; ++frame)
		{
			uint tick = 10u + (uint)(frame / 4);
			double drawTick = 10.0 + (frame / 4.0);
			double current = ((tick - 9u) * step) % 2.0;
			double previous = (((tick - 10u) * step) % 2.0);
			moving.ObserveProp(previous, current, tick, length: 2.0, loop: true, drawTick: drawTick);
			if (frame == 0)
			{
				start = moving.Time;
			}

			moving.Freewheel(1.0 / 60.0, drawTick);
		}

		double expected = 2.0 * Refii.Core.Engine_DrawAdvance.Pace;
		AssertThat(Math.Abs(moving.Rate - 1.0) < 1e-6).OverrideFailureMessage($"rate {moving.Rate}").IsTrue();
		AssertThat(Math.Abs((moving.Time - start) - expected) < 1e-2)
			.OverrideFailureMessage($"advanced {moving.Time - start} expected {expected}").IsTrue();
	}

	[TestCase]
	public void RemasterTreeShadowAndDrawRangesBoundDistantTreesWhileExtendingStreetShadows()
	{
		bool prevStated = Refii.Level.Engine_PropDrawRange.TreeShadowStated;
		try
		{
			// A typical Bowerstone Market tree placement with stated Shadow S = 60 m, Lod L = 120 m, Draw D = 2056 m:
			var r = new Refii.Level.StatedRange(Draw: 2056f, Fade: 0.125f, NearDraw: 0f, NearFade: 0f, Shadow: 60f, Lod: 120f);

			// In remaster "full" mode (TreeShadowStated = false), tree shadows extend to 2x stated S (120 m, clamped to 100..150 m)
			// rather than 0 (infinite 2,056 m) or the 360's short 60 m cut:
			Refii.Level.Engine_PropDrawRange.TreeShadowStated = false;
			AssertThat(Refii.Level.Engine_PropDrawRange.Describe(r, Refii.Level.PropModelClass.Tree, Refii.Level.PropRole.Shadow, hasLod: true, out float[] fullShadow)).IsTrue();
			AssertThat(Math.Abs(fullShadow[2] - 120f) < 1e-4f).OverrideFailureMessage($"fullShadow[2]={fullShadow[2]}").IsTrue();

			// A short bush/tree with S = 35 m still gets at least 100 m shadow reach down the street:
			var shortTree = new Refii.Level.StatedRange(Draw: 500f, Fade: 0.125f, NearDraw: 0f, NearFade: 0f, Shadow: 35f, Lod: 80f);
			AssertThat(Refii.Level.Engine_PropDrawRange.Describe(shortTree, Refii.Level.PropModelClass.Tree, Refii.Level.PropRole.Shadow, hasLod: true, out float[] minShadow)).IsTrue();
			AssertThat(Math.Abs(minShadow[2] - 100f) < 1e-4f).IsTrue();

			// A large landmark tree with S = 90 m clamps cleanly at 150 m:
			var tallTree = new Refii.Level.StatedRange(Draw: 2056f, Fade: 0.125f, NearDraw: 0f, NearFade: 0f, Shadow: 90f, Lod: 150f);
			AssertThat(Refii.Level.Engine_PropDrawRange.Describe(tallTree, Refii.Level.PropModelClass.Tree, Refii.Level.PropRole.Shadow, hasLod: true, out float[] maxShadow)).IsTrue();
			AssertThat(Math.Abs(maxShadow[2] - 150f) < 1e-4f).IsTrue();

			// In stated 360 mode (TreeShadowStated = true), it uses the raw stated S (60 m):
			Refii.Level.Engine_PropDrawRange.TreeShadowStated = true;
			AssertThat(Refii.Level.Engine_PropDrawRange.Describe(r, Refii.Level.PropModelClass.Tree, Refii.Level.PropRole.Shadow, hasLod: true, out float[] statedShadow)).IsTrue();
			AssertThat(Math.Abs(statedShadow[2] - 60f) < 1e-4f).IsTrue();
		}
		finally
		{
			Refii.Level.Engine_PropDrawRange.TreeShadowStated = prevStated;
		}
	}
}
