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
}
