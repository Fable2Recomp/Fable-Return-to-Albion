namespace Refii.Tests.Ui;

using GdUnit4;
using static GdUnit4.Assertions;
using System;
using Godot;
using Refii.Core;
using Refii.Ui;

[TestSuite]
public class HudReticuleInterpolationTests
{
	[TestCase]
	public void ReticuleAnchorTimelineAdvancesSmoothlyBetweenTicks()
	{
		var timeline = new Engine_TickTimeline(stillWhenUnchanged: true);

		uint tick = 100u;
		var p0 = new Vector3(10f, 2f, -15f);
		var p1 = new Vector3(12f, 2f, -15f);

		timeline.Reset(tick, new Transform3D(Basis.Identity, p0));

		// Tick advances to 101, anchor moved 2 metres to the right
		tick = 101u;
		timeline.Observe(tick, new Transform3D(Basis.Identity, p1));

		ulong nowUsec = 1_000_000UL;
		// First frame establishes initial timestamp
		timeline.Advance(nowUsec, 100.0);

		// Advance midway between tick 100 and 101 (e.g. 33.3ms later at drawTick = 100.5)
		Transform3D drawnMid = timeline.Advance(nowUsec + 33_333UL, 100.5);

		// X should be smoothly between 10 and 12 (approx 11)
		AssertThat(drawnMid.Origin.X).IsGreater(10f);
		AssertThat(drawnMid.Origin.X).IsLess(12f);
		AssertThat(drawnMid.Origin.Y).IsEqualApprox(2f, 0.01f);
		AssertThat(drawnMid.Origin.Z).IsEqualApprox(-15f, 0.01f);
	}

	[TestCase]
	public void LargeJumpPastTeleportThresholdSnapsInsteadOfGliding()
	{
		var timeline = new Engine_TickTimeline(stillWhenUnchanged: true);

		uint tick = 50u;
		var p0 = new Vector3(0f, 0f, 0f);
		var pFar = new Vector3(50f, 0f, 0f); // 50m jump (e.g. retarget or teleport)

		timeline.Reset(tick, new Transform3D(Basis.Identity, p0));

		// Jump is greater than TeleportMetres
		float distSq = timeline.Newest.Origin.DistanceSquaredTo(pFar);
		AssertThat(distSq).IsGreater(Engine_TickTimeline.TeleportMetres * Engine_TickTimeline.TeleportMetres);

		// Reset on teleport
		timeline.Reset(tick + 1u, new Transform3D(Basis.Identity, pFar));
		AssertThat(timeline.Newest.Origin).IsEqualApprox(pFar, new Vector3(0.01f, 0.01f, 0.01f));
	}

	[TestCase]
	public void TestAnchorTrackerRejectsStaleSequenceAndClearResets()
	{
		var tracker = new Engine_HudReticule.AnchorTracker();
		AssertThat(tracker.Empty).IsTrue();

		var p1 = new Vector3(1f, 2f, 3f);
		tracker.Observe(
			new Engine_HudReticule.Aim(p1, 0f, Colors.White, 1u, 0, Tick: 10u, MouseSequence: 5u),
			turnsWithView: true,
			hostAiming: false,
			currentMouseSequence: 5u);

		AssertThat(tracker.Empty).IsFalse();
		Vector3 eval1 = tracker.Evaluate(true, 5u, 1_000_000UL, 10.0, null);
		AssertThat(eval1).IsEqualApprox(p1, new Vector3(0.001f, 0.001f, 0.001f));

		// Mismatched mouse sequence while !hostAiming must not poison the coherent anchor.
		var pStale = new Vector3(5f, 6f, 7f);
		tracker.Observe(
			new Engine_HudReticule.Aim(pStale, 0f, Colors.White, 1u, 0, Tick: 11u, MouseSequence: 4u),
			turnsWithView: true,
			hostAiming: false,
			currentMouseSequence: 5u);

		Vector3 evalAfterStale = tracker.Evaluate(true, 5u, 1_016_666UL, 10.25, null);
		AssertThat(evalAfterStale).IsEqualApprox(p1, new Vector3(0.001f, 0.001f, 0.001f));

		// Large teleport snaps cleanly.
		var pFar = new Vector3(20f, 2f, 3f);
		tracker.Observe(
			new Engine_HudReticule.Aim(pFar, 0f, Colors.White, 1u, 0, Tick: 12u, MouseSequence: 5u),
			turnsWithView: true,
			hostAiming: false,
			currentMouseSequence: 5u);
		Vector3 evalTeleport = tracker.Evaluate(true, 5u, 1_033_333UL, 10.5, null);
		AssertThat(evalTeleport).IsEqualApprox(pFar, new Vector3(0.001f, 0.001f, 0.001f));

		tracker.Clear();
		AssertThat(tracker.Empty).IsTrue();
	}

	[TestCase]
	public void TestPetalTransformScalesBeforeRotatingWithZeroShear()
	{
		const float k = 2.25f;
		const float s = 1.5f;
		const float scale = 1.0f;
		Transform3D t = Engine_HudReticule.PetalTransform(
			new Vector2(10f, 20f),
			new Vector2(4f, 0f),
			MathF.PI * 0.25f,
			k,
			s,
			scale);

		Vector3 c0 = t.Basis.Column0;
		Vector3 c1 = t.Basis.Column1;
		Vector3 c2 = t.Basis.Column2;

		AssertThat(MathF.Abs(c0.Dot(c1)) < 1e-5f).IsTrue();
		AssertThat(MathF.Abs(c0.Length() - (2f * k * s * scale)) < 1e-4f).IsTrue();
		AssertThat(MathF.Abs(c1.Length() - (2f * s * scale)) < 1e-4f).IsTrue();
		AssertThat(c2).IsEqualApprox(Vector3.Back, new Vector3(1e-5f, 1e-5f, 1e-5f));
	}
}
