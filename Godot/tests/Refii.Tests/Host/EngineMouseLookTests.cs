namespace Refii.Tests;

using System;
using GdUnit4;
using static GdUnit4.Assertions;
using Godot;
using Refii.Core;

[TestSuite]
public class EngineMouseLookTests
{
    private const double Near = 1e-6;

    /// <summary>Rightward degrees of <paramref name="camera"/>'s forward about +Y.</summary>
    private static double Heading(Transform3D camera)
    {
        Vector3 forward = -camera.Basis.Z;
        return Mathf.RadToDeg(Math.Atan2(forward.X, -forward.Z));
    }

    /// <summary>Upward degrees of <paramref name="camera"/>'s forward.</summary>
    private static double Elevation(Transform3D camera)
    {
        Vector3 forward = (-camera.Basis.Z).Normalized();
        return Mathf.RadToDeg(Math.Asin(forward.Y));
    }

    private static bool Same(Transform3D a, Transform3D b) =>
        a.Origin.DistanceTo(b.Origin) < 1e-3f &&
        a.Basis.X.DistanceTo(b.Basis.X) < 1e-4f &&
        a.Basis.Y.DistanceTo(b.Basis.Y) < 1e-4f &&
        a.Basis.Z.DistanceTo(b.Basis.Z) < 1e-4f;

    [TestCase]
    public void TestCountsBecomeDegreesAtTheMiddleSensitivity()
    {
        Vector2 turn = Engine_MouseLook.Degrees(new Vector2(10f, 0f), 1f, invertX: false);
        AssertThat(Math.Abs(turn.X - 1.5f) < 1e-5f).IsTrue();
        AssertThat(turn.Y).IsEqual(0f);

        // Godot's relative motion is down-positive; the camera's turn is up-positive.
        Vector2 down = Engine_MouseLook.Degrees(new Vector2(0f, 10f), 2f, invertX: false);
        AssertThat(Math.Abs(down.Y + 3f) < 1e-5f).IsTrue();

        Vector2 inverted = Engine_MouseLook.Degrees(new Vector2(10f, 0f), 1f, invertX: true);
        AssertThat(Math.Abs(inverted.X + 1.5f) < 1e-5f).IsTrue();
    }

    [TestCase]
    public void TestOrbitTurnsAboutThePivotAndKeepsItInView()
    {
        var camera = new Transform3D(Basis.Identity, new Vector3(0f, 2f, 4f));
        Transform3D turned = Engine_MouseLook.Orbit(camera, new Vector3(0f, 0f, 0f), 90.0);
        AssertThat(turned.Origin.IsEqualApprox(new Vector3(-4f, 2f, 0f))).IsTrue();
        Vector3 forward = -turned.Basis.Z;
        AssertThat(forward.IsEqualApprox(new Vector3(1f, 0f, 0f))).IsTrue();
        AssertThat(Math.Abs(Heading(turned) - 90.0) < 1e-3).IsTrue();
        AssertThat(Engine_MouseLook.Orbit(camera, Vector3.Zero, 0.0)).IsEqual(camera);
    }

    [TestCase]
    public void TestPivotIsOnTheLevelLineOfSightAtTheStatedDistance()
    {
        // Looking down 45 degrees: the pivot is still out along the LEVEL forward, at eye height.
        var camera = new Transform3D(new Basis(Vector3.Right, -Mathf.Pi / 4f), new Vector3(1f, 3f, 2f));
        AssertThat(Engine_MouseLook.Pivot(camera, 5f).IsEqualApprox(new Vector3(1f, 3f, -3f))).IsTrue();
        AssertThat(Engine_MouseLook.Pivot(camera, 0f)).IsEqual(camera.Origin);
        AssertThat(Engine_MouseLook.PivotFor(camera, Engine_MouseLook.ModeInPlace, new Vector3(5f, 0f, 0f)))
            .IsEqual(camera.Origin);
        AssertThat(Engine_MouseLook.PivotFor(camera, Engine_MouseLook.ModeOrbit, new Vector3(5f, 0f, 0f))
            .IsEqualApprox(new Vector3(1f, 3f, -3f))).IsTrue();
    }

    [TestCase]
    public void TestTurnFollowsTheCamerasMode()
    {
        var camera = new Transform3D(Basis.Identity, new Vector3(0f, 1f, 4f));
        var focus = new Vector3(4f, 0f, 0f);
        AssertThat(Engine_MouseLook.Turn(camera, Engine_MouseLook.ModeNone, focus, 30.0)).IsEqual(camera);

        Transform3D inPlace = Engine_MouseLook.Turn(camera, Engine_MouseLook.ModeInPlace, focus, 30.0);
        AssertThat(inPlace.Origin.IsEqualApprox(camera.Origin)).IsTrue();
        AssertThat(Math.Abs(Heading(inPlace) - 30.0) < 1e-3).IsTrue();

        // An orbit about the focus 4 m ahead keeps the focus on the line of sight.
        Transform3D orbit = Engine_MouseLook.Turn(camera, Engine_MouseLook.ModeOrbit, focus, 90.0);
        AssertThat(Math.Abs(Heading(orbit) - 90.0) < 1e-3).IsTrue();
        AssertThat(Engine_MouseLook.Pivot(orbit, 4f).IsEqualApprox(new Vector3(0f, 1f, 0f))).IsTrue();
    }

    [TestCase]
    public void TestOutstandingIsPostedNotYetTakenInTheCamerasDegrees()
    {
        var look = new Engine_MouseLook();
        var focus = new Vector3(3f, 0f, 0f);
        AssertThat(look.Observe(10.0, 4.0, 0.0, 2.0, 0.0, 0.0, 1.5f, focus, Engine_MouseLook.ModeOrbit))
            .IsEqual((0.0, 0.0));
        AssertThat(Math.Abs(look.Outstanding - 9.0) < Near).IsTrue();
        AssertThat(look.Mode).IsEqual(Engine_MouseLook.ModeOrbit);
        AssertThat(look.PivotMetres).IsEqual(3f);

        // The third-person camera draws no pitch ahead: its vertical is a height, not a turn.
        AssertThat(look.OutstandingPitch).IsEqual(0.0);

        // The next update takes the rest and its camera turned 9 degrees for it.
        (double right, double up) = look.Observe(12.0, 10.0, 9.0, 2.0, 2.0, 0.0, 1.5f, focus, Engine_MouseLook.ModeOrbit);
        AssertThat(Math.Abs(right - 9.0) < Near).IsTrue();
        AssertThat(up).IsEqual(0.0);
        AssertThat(Math.Abs(look.Outstanding - 3.0) < Near).IsTrue();

        // A read that straddled an update: only the new posts count, against the last whole read.
        look.ObservePosted(14.0, 2.0);
        AssertThat(Math.Abs(look.Outstanding - 6.0) < Near).IsTrue();

        // A camera that takes no look input is drawn with nothing ahead.
        look.Observe(14.0, 10.0, 9.0, 2.0, 2.0, 0.0, 1.5f, Vector3.Zero, Engine_MouseLook.ModeNone);
        AssertThat(look.Outstanding).IsEqual(0.0);
        AssertThat(look.Observe(20.0, 20.0, 9.0, 2.0, 2.0, 0.0, 0f, Vector3.Zero, 7)).IsEqual((0.0, 0.0));
        AssertThat(look.Mode).IsEqual(Engine_MouseLook.ModeNone);
    }

    [TestCase]
    public void TestTheAimDrawsPitchAheadAndNamesItsAim()
    {
        var look = new Engine_MouseLook();
        var head = new Vector3(1f, -0.5f, -0.2f);
        look.Observe(4.0, 0.0, 0.0, -2.0, 0.0, 0.0, 1.25f, head, Engine_MouseLook.ModeAim, 30f, 0x40001230u);
        AssertThat(Math.Abs(look.Outstanding - 5.0) < Near).IsTrue();
        AssertThat(Math.Abs(look.OutstandingPitch + 2.5) < Near).IsTrue();
        AssertThat(look.PivotOffsets).IsEqual(head);
        AssertThat(look.PitchLimit).IsEqual(30f);
        AssertThat(look.Aim).IsEqual(0x40001230u);

        // The update takes it all: 5 right and 2.5 down, absorbed whole.
        (double right, double up) = look.Observe(4.0, 4.0, 5.0, -2.0, -2.0, -2.5, 1.25f, head,
            Engine_MouseLook.ModeAim, 30f, 0x40001230u);
        AssertThat(Math.Abs(right - 5.0) < Near).IsTrue();
        AssertThat(Math.Abs(up + 2.5) < Near).IsTrue();
        AssertThat(look.Outstanding).IsEqual(0.0);
        AssertThat(look.OutstandingPitch).IsEqual(0.0);

        // Out of the aim the stated aim is no one's.
        look.Observe(4.0, 4.0, 5.0, -2.0, -2.0, -2.5, 1.25f, new Vector3(3f, 0f, 0f), Engine_MouseLook.ModeOrbit, 30f, 0x40001230u);
        AssertThat(look.Aim).IsEqual(0u);
        AssertThat(look.PitchLimit).IsEqual(0f);
    }

    [TestCase]
    public void TestTheAimPivotIsInTheCamerasLevelFrame()
    {
        // Looking 20 degrees down along -Z: forward and right are level, up is the world's.
        var camera = new Transform3D(new Basis(Vector3.Right, -Mathf.DegToRad(20f)), new Vector3(0f, 2f, 4f));
        Vector3 pivot = Engine_MouseLook.AimPivot(camera, new Vector3(1f, -0.5f, -0.3f));
        AssertThat(pivot.IsEqualApprox(new Vector3(-0.5f, 1.7f, 3f))).IsTrue();
    }

    [TestCase]
    public void TestTheAimTurnsRigidlyAboutTheHead()
    {
        var camera = new Transform3D(new Basis(Vector3.Up, 0.4f) * new Basis(Vector3.Right, -0.1f), new Vector3(2f, 3f, 1f));
        var offsets = new Vector3(1f, -0.5f, -0.25f);
        Vector3 head = Engine_MouseLook.AimPivot(camera, offsets);
        Vector3 anchor = camera * new Vector3(1.5f, 0.3f, -20f);

        Transform3D turned = Engine_MouseLook.Turn(camera, Engine_MouseLook.ModeAim, offsets, 12.0, 4.0, 30f);
        Basis turn = Engine_MouseLook.AimRotation(camera, 12.0, 4.0);
        Vector3 turnedAnchor = head + (turn * (anchor - head));

        // The view turned 12 right and 4 up about the head, and the aim point sits where it sat in
        // the view: the reticule does not move on the screen. A yaw alone keeps the head where the
        // camera's level frame says it is.
        Transform3D yawed = Engine_MouseLook.Turn(camera, Engine_MouseLook.ModeAim, offsets, 12.0);
        AssertThat(Engine_MouseLook.AimPivot(yawed, offsets).DistanceTo(head) < 1e-3f).IsTrue();
        AssertThat(Engine_MouseLook.Rigid(camera, head, turn).Origin.DistanceTo(turned.Origin) < 1e-4f).IsTrue();
        AssertThat(Math.Abs(Heading(turned) - Heading(camera) - 12.0) < 1e-2).IsTrue();
        AssertThat(Math.Abs(Elevation(turned) - Elevation(camera) - 4.0) < 1e-2).IsTrue();
        Vector3 before = camera.AffineInverse() * anchor;
        Vector3 after = turned.AffineInverse() * turnedAnchor;
        AssertThat(before.DistanceTo(after) < 1e-3f).IsTrue();
    }

    [TestCase]
    public void TestTheAimPitchStopsAtTheCamerasBound()
    {
        var level = new Transform3D(new Basis(Vector3.Right, Mathf.DegToRad(25f)), Vector3.Zero);
        AssertThat(Math.Abs(Engine_MouseLook.BoundPitch(level, 10.0, 30f) - 5.0) < 1e-3).IsTrue();
        AssertThat(Math.Abs(Engine_MouseLook.BoundPitch(level, -10.0, 30f) + 10.0) < 1e-3).IsTrue();

        // Already past the bound the pitch is never pushed further out, and may come back.
        var past = new Transform3D(new Basis(Vector3.Right, Mathf.DegToRad(35f)), Vector3.Zero);
        AssertThat(Engine_MouseLook.BoundPitch(past, 5.0, 30f)).IsEqual(0.0);
        AssertThat(Math.Abs(Engine_MouseLook.BoundPitch(past, -3.0, 30f) + 3.0) < 1e-3).IsTrue();

        // No stated bound bounds nothing.
        AssertThat(Engine_MouseLook.BoundPitch(past, 5.0, 0f)).IsEqual(5.0);
    }

    /// <summary>
    /// The host's aim turn is the guest's: the core camera chunk's arithmetic (yaw about +Z by
    /// minus the rightward degrees, then the tilt about the heading's level right (hy, -hx, 0) by
    /// Rodrigues), carried to Godot by its axis map (-x, z, y), lands where AimRotation puts it.
    /// </summary>
    [TestCase]
    public void TestTheAimTurnIsTheGuestsArithmetic()
    {
        static Vector3 ToGodot(double x, double y, double z) => new((float)-x, (float)z, (float)y);

        (double X, double Y, double Z) GuestTurn((double X, double Y, double Z) v, (double X, double Y, double Z) d,
            double yaw, double tilt)
        {
            double y = -yaw * Math.PI / 180.0, t = tilt * Math.PI / 180.0;
            double cy = Math.Cos(y), sy = Math.Sin(y), ct = Math.Cos(t), st = Math.Sin(t);
            double hx = d.X * cy - d.Y * sy, hy = d.X * sy + d.Y * cy;
            double level = Math.Sqrt(hx * hx + hy * hy);
            double rx = hy / level, ry = -hx / level;
            double ax = v.X * cy - v.Y * sy, ay = v.X * sy + v.Y * cy, az = v.Z;
            double dot = ax * rx + ay * ry;
            return (ax * ct + ry * az * st + rx * dot * (1 - ct),
                ay * ct - rx * az * st + ry * dot * (1 - ct),
                az * ct + (rx * ay - ry * ax) * st);
        }

        // A guest camera direction 30 degrees off +Y, 10 degrees down, as Godot draws it.
        double heading = 30.0 * Math.PI / 180.0, pitch = -10.0 * Math.PI / 180.0;
        var d = (Math.Sin(heading) * Math.Cos(pitch), Math.Cos(heading) * Math.Cos(pitch), Math.Sin(pitch));
        Vector3 forward = ToGodot(d.Item1, d.Item2, d.Item3).Normalized();
        var camera = new Transform3D(Basis.LookingAt(forward, Vector3.Up), Vector3.Zero);
        Basis turn = Engine_MouseLook.AimRotation(camera, 17.0, -6.0);

        foreach (var v in new[] { d, (1.0, 2.0, 3.0), (-4.0, 0.5, -1.0) })
        {
            var g = GuestTurn(v, d, 17.0, -6.0);
            Vector3 expected = ToGodot(g.X, g.Y, g.Z);
            Vector3 actual = turn * ToGodot(v.Item1, v.Item2, v.Item3);
            AssertThat(actual.DistanceTo(expected) < 1e-4f).IsTrue();
        }
    }

    [TestCase]
    public void TestTurnAboutRestatesTheWholeHistory()
    {
        var timeline = new Engine_TickTimeline(stillWhenUnchanged: true);
        var camera = new Transform3D(Basis.Identity, new Vector3(0f, 2f, 4f));
        timeline.Reset(10.0, camera, 60f);
        timeline.Advance(1_000_000UL, 10.0);

        timeline.TurnAbout(Vector3.Zero, 90.0);
        Transform3D expected = Engine_MouseLook.Orbit(camera, Vector3.Zero, 90.0);
        AssertThat(Same(timeline.Newest, expected)).IsTrue();
        AssertThat(Same(timeline.Advance(1_016_666UL, 10.0), expected)).IsTrue();
        AssertThat(timeline.DrawnScalar).IsEqual(60f);
    }

    [TestCase]
    public void TestTurnAboutAnyRotationRestatesTheWholeHistory()
    {
        var timeline = new Engine_TickTimeline(stillWhenUnchanged: true);
        var camera = new Transform3D(Basis.Identity, new Vector3(0f, 2f, 4f));
        timeline.Reset(10.0, camera, 60f);
        timeline.Advance(1_000_000UL, 10.0);

        var pivot = new Vector3(0.5f, 1.5f, 3f);
        Basis turn = Engine_MouseLook.AimRotation(camera, 20.0, 7.0);
        timeline.TurnAbout(pivot, turn);
        Transform3D expected = Engine_MouseLook.Rigid(camera, pivot, turn);
        AssertThat(Same(timeline.Newest, expected)).IsTrue();
        AssertThat(Same(timeline.Advance(1_016_666UL, 10.0), expected)).IsTrue();
    }

    /// <summary>
    /// The whole contract, frame by frame: the mouse turns the drawn view the frame it moves; the
    /// guest's next update takes that turn and states a camera carrying it, the history is
    /// restated by it, and the drawn view does not move.
    /// </summary>
    [TestCase]
    public void TestATakenTurnMovesFromAheadToStatedWithoutAJump()
    {
        var focus = new Vector3(4f, 0f, 0f);
        var look = new Engine_MouseLook();
        var timeline = new Engine_TickTimeline(stillWhenUnchanged: true);
        var stated = new Transform3D(Basis.Identity, new Vector3(0f, 2f, 4f));
        ulong now = 1_000_000UL;

        look.Observe(0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 1f, focus, Engine_MouseLook.ModeOrbit);
        timeline.Reset(10.0, stated, 60f);
        Transform3D line = timeline.Advance(now, 10.0);

        // Two frames of mouse, 3 then 4 degrees: each frame's view turns by everything posted.
        look.ObservePosted(3.0, 0.0);
        Transform3D frame1 = look.DrawAhead(line);
        AssertThat(Math.Abs(Heading(frame1) - 3.0) < 1e-3).IsTrue();
        look.ObservePosted(7.0, 0.0);
        Transform3D frame2 = look.DrawAhead(line);
        AssertThat(Math.Abs(Heading(frame2) - 7.0) < 1e-3).IsTrue();

        // The update takes all 7 and its camera turned 7 about the same focus.
        Vector3 pivot = Engine_MouseLook.PivotFor(line, look.Mode, look.PivotOffsets);
        Transform3D carried = Engine_MouseLook.Orbit(stated, pivot, 7.0);
        int drawnMode = look.Mode;
        Vector3 drawnPivot = look.PivotOffsets;
        (double right, double up) = look.Observe(7.0, 7.0, 7.0, 0.0, 0.0, 0.0, 1f, focus, Engine_MouseLook.ModeOrbit);
        timeline.TurnAbout(Engine_MouseLook.PivotFor(line, drawnMode, drawnPivot),
            Engine_MouseLook.Rotation(line, drawnMode, right, up));
        timeline.Observe(11.0, carried, 60f);
        now += 16_666UL;
        line = timeline.Advance(now, 10.25);
        Transform3D frame3 = look.DrawAhead(line);

        AssertThat(look.Outstanding).IsEqual(0.0);
        AssertThat(Same(frame3, frame2)).IsTrue();
        AssertThat(Same(frame3, carried)).IsTrue();
    }

    /// <summary>
    /// The aim's contract, frame by frame: the mouse turns the drawn view and the reticule anchor
    /// together about the head; the guest's update takes the turn whole and states a camera and an
    /// aim point carrying it; the history is restated, and neither the view nor the reticule moves.
    /// </summary>
    [TestCase]
    public void TestATakenAimTurnMovesFromAheadToStatedWithoutAJump()
    {
        var head = new Vector3(1f, -0.5f, -0.3f);
        const uint aim = 0x40002000u;
        var look = new Engine_MouseLook();
        var timeline = new Engine_TickTimeline(stillWhenUnchanged: true);
        var stated = new Transform3D(new Basis(Vector3.Right, -0.15f), new Vector3(0f, 2f, 4f));
        Vector3 anchor = stated * new Vector3(0.8f, 0.2f, -25f);
        ulong now = 1_000_000UL;

        look.Observe(0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 1f, head, Engine_MouseLook.ModeAim, 30f, aim);
        timeline.Reset(10.0, stated, 60f);
        Transform3D line = timeline.Advance(now, 10.0);

        // Two frames of mouse: 2 right then 3 more right and 1.5 up.
        look.ObservePosted(2.0, 0.0);
        look.ObservePosted(5.0, 1.5);
        Transform3D frame = look.DrawAhead(line);
        (Vector3 pivot, Basis turn) = look.Ahead(line);
        Vector3 drawnAnchor = pivot + (turn * (anchor - pivot));
        AssertThat(Math.Abs(Heading(frame) - Heading(line) - 5.0) < 1e-2).IsTrue();
        AssertThat(Math.Abs(Elevation(frame) - Elevation(line) - 1.5) < 1e-2).IsTrue();

        // The guest's update applies the same turn to the camera and to the aim point.
        Transform3D carried = Engine_MouseLook.Rigid(stated, pivot, turn);
        Vector3 carriedAnchor = pivot + (turn * (anchor - pivot));
        int drawnMode = look.Mode;
        Vector3 drawnPivot = look.PivotOffsets;
        (double right, double up) = look.Observe(5.0, 5.0, 5.0, 1.5, 1.5, 1.5, 1f, head,
            Engine_MouseLook.ModeAim, 30f, aim);
        timeline.TurnAbout(Engine_MouseLook.PivotFor(line, drawnMode, drawnPivot),
            Engine_MouseLook.Rotation(line, drawnMode, right, up));
        timeline.Observe(11.0, carried, 60f);
        now += 16_666UL;
        line = timeline.Advance(now, 10.25);
        Transform3D after = look.DrawAhead(line);
        (Vector3 pivotAfter, Basis turnAfter) = look.Ahead(line);
        Vector3 anchorAfter = pivotAfter + (turnAfter * (carriedAnchor - pivotAfter));

        AssertThat(look.Outstanding).IsEqual(0.0);
        AssertThat(look.OutstandingPitch).IsEqual(0.0);
        AssertThat(Same(after, frame)).IsTrue();
        AssertThat(Same(after, carried)).IsTrue();
        AssertThat(anchorAfter.DistanceTo(drawnAnchor) < 1e-3f).IsTrue();
    }

    [TestCase]
    public void TestObservePostedUpdatesOutstandingWithoutMutatingGuestState()
    {
        var head = new Vector3(0.5f, 1.5f, -0.25f);
        var look = new Engine_MouseLook();
        var timeline = new Engine_TickTimeline(stillWhenUnchanged: true);
        var stated = new Transform3D(Basis.Identity, new Vector3(0f, 2f, 4f));

        look.Observe(0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 1f, head, Engine_MouseLook.ModeAim, 30f, 0u);
        timeline.Reset(10.0, stated, 60f);
        Transform3D line = timeline.Advance(1_000_000UL, 10.0);
        Transform3D before = look.DrawAhead(line);

        look.ObservePosted(4.0, -1.5);
        Transform3D after = look.DrawAhead(line);

        AssertThat(look.Mode).IsEqual(Engine_MouseLook.ModeAim);
        AssertThat(look.PivotOffsets).IsEqualApprox(head, new Vector3(1e-4f, 1e-4f, 1e-4f));
        AssertThat(look.Outstanding).IsEqual(4.0);
        AssertThat(look.OutstandingPitch).IsEqual(-1.5);
        AssertThat(Math.Abs((Heading(after) - Heading(before)) - 4.0) < 1e-2).IsTrue();
        AssertThat(Math.Abs((Elevation(after) - Elevation(before)) + 1.5) < 1e-2).IsTrue();
    }
}
