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
        AssertThat(Engine_MouseLook.PivotFor(camera, Engine_MouseLook.ModeInPlace, 5f)).IsEqual(camera.Origin);
        AssertThat(Engine_MouseLook.PivotFor(camera, Engine_MouseLook.ModeOrbit, 5f)
            .IsEqualApprox(new Vector3(1f, 3f, -3f))).IsTrue();
    }

    [TestCase]
    public void TestTurnFollowsTheCamerasMode()
    {
        var camera = new Transform3D(Basis.Identity, new Vector3(0f, 1f, 4f));
        AssertThat(Engine_MouseLook.Turn(camera, Engine_MouseLook.ModeNone, 4f, 30.0)).IsEqual(camera);

        Transform3D inPlace = Engine_MouseLook.Turn(camera, Engine_MouseLook.ModeInPlace, 4f, 30.0);
        AssertThat(inPlace.Origin.IsEqualApprox(camera.Origin)).IsTrue();
        AssertThat(Math.Abs(Heading(inPlace) - 30.0) < 1e-3).IsTrue();

        // An orbit about the focus 4 m ahead keeps the focus on the line of sight.
        Transform3D orbit = Engine_MouseLook.Turn(camera, Engine_MouseLook.ModeOrbit, 4f, 90.0);
        AssertThat(Math.Abs(Heading(orbit) - 90.0) < 1e-3).IsTrue();
        AssertThat(Engine_MouseLook.Pivot(orbit, 4f).IsEqualApprox(new Vector3(0f, 1f, 0f))).IsTrue();
    }

    [TestCase]
    public void TestOutstandingIsPostedNotYetTakenInTheCamerasDegrees()
    {
        var look = new Engine_MouseLook();
        AssertThat(look.Observe(10.0, 4.0, 0.0, 1.5f, 3f, Engine_MouseLook.ModeOrbit)).IsEqual(0.0);
        AssertThat(Math.Abs(look.Outstanding - 9.0) < Near).IsTrue();
        AssertThat(look.Mode).IsEqual(Engine_MouseLook.ModeOrbit);
        AssertThat(look.PivotMetres).IsEqual(3f);

        // The next update takes the rest and its camera turned 9 degrees for it.
        double turned = look.Observe(12.0, 10.0, 9.0, 1.5f, 3f, Engine_MouseLook.ModeOrbit);
        AssertThat(Math.Abs(turned - 9.0) < Near).IsTrue();
        AssertThat(Math.Abs(look.Outstanding - 3.0) < Near).IsTrue();

        // A read that straddled an update: only the new posts count, against the last whole read.
        look.ObservePosted(14.0);
        AssertThat(Math.Abs(look.Outstanding - 6.0) < Near).IsTrue();

        // A camera that takes no look input is drawn with nothing ahead.
        look.Observe(14.0, 10.0, 9.0, 1.5f, 0f, Engine_MouseLook.ModeNone);
        AssertThat(look.Outstanding).IsEqual(0.0);
        AssertThat(look.Observe(20.0, 20.0, 9.0, 0f, 0f, 7)).IsEqual(0.0);
        AssertThat(look.Mode).IsEqual(Engine_MouseLook.ModeNone);
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

    /// <summary>
    /// The whole contract, frame by frame: the mouse turns the drawn view the frame it moves; the
    /// guest's next update takes that turn and states a camera carrying it, the history is
    /// restated by it, and the drawn view does not move.
    /// </summary>
    [TestCase]
    public void TestATakenTurnMovesFromAheadToStatedWithoutAJump()
    {
        const float focus = 4f;
        var look = new Engine_MouseLook();
        var timeline = new Engine_TickTimeline(stillWhenUnchanged: true);
        var stated = new Transform3D(Basis.Identity, new Vector3(0f, 2f, 4f));
        ulong now = 1_000_000UL;

        look.Observe(0.0, 0.0, 0.0, 1f, focus, Engine_MouseLook.ModeOrbit);
        timeline.Reset(10.0, stated, 60f);
        Transform3D line = timeline.Advance(now, 10.0);

        // Two frames of mouse, 3 then 4 degrees: each frame's view turns by everything posted.
        look.ObservePosted(3.0);
        Transform3D frame1 = Engine_MouseLook.Turn(line, look.Mode, look.PivotMetres, look.Outstanding);
        AssertThat(Math.Abs(Heading(frame1) - 3.0) < 1e-3).IsTrue();
        look.ObservePosted(7.0);
        Transform3D frame2 = Engine_MouseLook.Turn(line, look.Mode, look.PivotMetres, look.Outstanding);
        AssertThat(Math.Abs(Heading(frame2) - 7.0) < 1e-3).IsTrue();

        // The update takes all 7 and its camera turned 7 about the same focus.
        Vector3 pivot = Engine_MouseLook.PivotFor(line, look.Mode, look.PivotMetres);
        Transform3D carried = Engine_MouseLook.Orbit(stated, pivot, 7.0);
        int drawnMode = look.Mode;
        float drawnPivot = look.PivotMetres;
        double turned = look.Observe(7.0, 7.0, 7.0, 1f, focus, Engine_MouseLook.ModeOrbit);
        timeline.TurnAbout(Engine_MouseLook.PivotFor(line, drawnMode, drawnPivot), turned);
        timeline.Observe(11.0, carried, 60f);
        now += 16_666UL;
        line = timeline.Advance(now, 10.25);
        Transform3D frame3 = Engine_MouseLook.Turn(line, look.Mode, look.PivotMetres, look.Outstanding);

        AssertThat(look.Outstanding).IsEqual(0.0);
        AssertThat(Same(frame3, frame2)).IsTrue();
        AssertThat(Same(frame3, carried)).IsTrue();
    }
}
