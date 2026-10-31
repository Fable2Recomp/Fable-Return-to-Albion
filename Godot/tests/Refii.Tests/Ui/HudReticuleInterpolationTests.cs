namespace Refii.Tests.Ui;

using GdUnit4;
using static GdUnit4.Assertions;
using Godot;
using Refii.Core;

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
}
