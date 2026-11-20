using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using GdUnit4;
using Godot;
using Refii.AndroidRuntime;
using Refii.Launcher.Core;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>`android_displacement_in_view` (<see cref="AndroidDisplacementGate"/>): the frustum decision, the wake bookkeeping and the setting.</summary>
[TestSuite]
public sealed class AndroidDisplacementGateTests
{
    private static string FileText(string repoPath, [CallerFilePath] string source = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../../..", repoPath))).Replace("\r\n", "\n");

    /// <summary>A box frustum, x, y and z in [-10, 10], with outward normals as Camera3D.GetFrustum gives them.</summary>
    private static Plane[] Cube() => new[]
    {
        new Plane(new Vector3(1, 0, 0), 10), new Plane(new Vector3(-1, 0, 0), 10),
        new Plane(new Vector3(0, 1, 0), 10), new Plane(new Vector3(0, -1, 0), 10),
        new Plane(new Vector3(0, 0, 1), 10), new Plane(new Vector3(0, 0, -1), 10),
    };

    private static Aabb At(float x, float size = 1f) => new(new Vector3(x, 0, 0), new Vector3(size, size, size));

    /// <summary>A box inside or across the frustum is seen; one past the slack is not.</summary>
    [TestCase]
    public void BoxesOutsideTheFrustumAreNotSeen()
    {
        Plane[] planes = Cube();
        AssertThat(AndroidDisplacementGate.AnyTouches(new[] { At(0) }, planes)).IsTrue();
        AssertThat(AndroidDisplacementGate.AnyTouches(new[] { At(9.5f) }, planes)).IsTrue();
        // Within Engine_DisplacementMeshes.FrustumSlack (1 m) past the plane: still seen.
        AssertThat(AndroidDisplacementGate.AnyTouches(new[] { At(10.5f) }, planes)).IsTrue();
        AssertThat(AndroidDisplacementGate.AnyTouches(new[] { At(12f) }, planes)).IsFalse();
        AssertThat(AndroidDisplacementGate.AnyTouches(new[] { At(-30f), At(0) }, planes)).IsTrue();
        AssertThat(AndroidDisplacementGate.AnyTouches(ReadOnlySpan<Aabb>.Empty, planes)).IsFalse();
    }

    /// <summary>The effect and body boxes are the local box under the transform, grown by their margins.</summary>
    [TestCase]
    public void BoxesFollowTheTransformAndGrow()
    {
        var placed = new Transform3D(Basis.Identity, new Vector3(20, 0, 0));
        Aabb local = new(Vector3.Zero, Vector3.One);
        Aabb effect = AndroidDisplacementGate.EffectBox(placed, local);
        AssertThat(effect.Position).IsEqual(new Vector3(20 - AndroidDisplacementGate.EffectMargin, -1, -1));
        AssertThat(effect.Size).IsEqual(Vector3.One * (1 + 2 * AndroidDisplacementGate.EffectMargin));
        Aabb body = AndroidDisplacementGate.BodyBox(placed, local);
        AssertThat(body.Size).IsEqual(Vector3.One * (1 + 2 * AndroidDisplacementGate.BodyPad));
        Aabb mesh = AndroidDisplacementGate.MeshParticleBox(placed, new Vector3(1, 0, 0), 0.5f);
        AssertThat(mesh.GetCenter()).IsEqual(new Vector3(21, 0, 0));
        AssertThat(mesh.Size.X).IsEqual(2 * Refii.Fx.FableFx.FreezeRadius);
    }

    /// <summary>Off, every wake is outright; on, a wake with a box is gated, the drunk warp never.</summary>
    [TestCase]
    public void OnlyBoxedWakesAreGatedAndOnlyWhenOn()
    {
        AndroidDisplacementGate.Reset();
        var a = new object();
        AssertThat(AndroidDisplacementGate.Note(AndroidDisplacementGate.Kind.Effect, a, null, At(0), true, enabled: false, 10)).IsFalse();
        AssertThat(AndroidDisplacementGate.Note(AndroidDisplacementGate.Kind.Effect, new object(), null, At(0), true, enabled: true, 10)).IsTrue();
        AssertThat(AndroidDisplacementGate.Note(AndroidDisplacementGate.Kind.Body, new object(), null, At(0), true, enabled: true, 10)).IsTrue();
        AssertThat(AndroidDisplacementGate.Note(AndroidDisplacementGate.Kind.FxMesh, new object(), null, default, false, enabled: true, 10)).IsFalse();
        AssertThat(AndroidDisplacementGate.Note(AndroidDisplacementGate.Kind.Drunk, null, null, At(0), true, enabled: true, 10)).IsFalse();
        AndroidDisplacementGate.Reset();
    }

    /// <summary>The take-back test sees this frame's and last frame's boxes, not older ones.</summary>
    [TestCase]
    public void SeesThisAndLastFramesBoxesOnly()
    {
        AndroidDisplacementGate.Reset();
        Plane[] planes = Cube();
        AndroidDisplacementGate.Note(AndroidDisplacementGate.Kind.Effect, new object(), null, At(50), true, true, 10);
        AssertThat(AndroidDisplacementGate.SeenIn(planes, 10)).IsFalse();
        AndroidDisplacementGate.Note(AndroidDisplacementGate.Kind.Body, new object(), null, At(0), true, true, 10);
        AssertThat(AndroidDisplacementGate.SeenIn(planes, 10)).IsTrue();
        AssertThat(AndroidDisplacementGate.SeenIn(planes, 11)).IsTrue();
        AndroidDisplacementGate.Note(AndroidDisplacementGate.Kind.Effect, new object(), null, At(50), true, true, 11);
        AssertThat(AndroidDisplacementGate.SeenIn(planes, 11)).IsTrue();
        AndroidDisplacementGate.Note(AndroidDisplacementGate.Kind.Effect, new object(), null, At(50), true, true, 12);
        AssertThat(AndroidDisplacementGate.SeenIn(planes, 12)).IsFalse();
        AndroidDisplacementGate.Reset();
    }

    /// <summary>One owner's boxes in a row merge into one; past the capacity a wake is outright.</summary>
    [TestCase]
    public void MergesAnOwnersBoxesAndOverflowsOutright()
    {
        AndroidDisplacementGate.Reset();
        Plane[] planes = Cube();
        var system = new object();
        AndroidDisplacementGate.Note(AndroidDisplacementGate.Kind.FxMesh, system, "smoke", At(-50), true, true, 20);
        AndroidDisplacementGate.Note(AndroidDisplacementGate.Kind.FxMesh, system, "smoke", At(50), true, true, 20);
        // The merged box spans -50 to 51 across the frustum.
        AssertThat(AndroidDisplacementGate.SeenIn(planes, 20)).IsTrue();

        AndroidDisplacementGate.Reset();
        for (int i = 0; i < AndroidDisplacementGate.Capacity; i++)
        {
            AssertThat(AndroidDisplacementGate.Note(AndroidDisplacementGate.Kind.Effect, new object(), null, At(50), true, true, 30)).IsTrue();
        }
        AssertThat(AndroidDisplacementGate.Note(AndroidDisplacementGate.Kind.Effect, new object(), null, At(50), true, true, 30)).IsFalse();
        AndroidDisplacementGate.Reset();
    }

    /// <summary>
    /// A layer put to sleep for a box behind the camera wakes once the live camera turns to it:
    /// the test reads the camera as it is now, every frame, asleep or not.
    /// </summary>
    [TestCase]
    [RequireGodotRuntime]
    public void ASleepingLayerWakesWhenTheCameraTurnsToTheBox()
    {
        var tree = (SceneTree)Engine.GetMainLoop();
        var viewport = new SubViewport { Size = new Vector2I(160, 90), RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled };
        var camera = new Camera3D { Fov = 60f, Near = 0.1f, Far = 500f };
        viewport.AddChild(camera);
        tree.Root.AddChild(viewport);
        try
        {
            AndroidDisplacementGate.Reset();
            // Looking down -Z; the haze 30 m behind, along +Z.
            camera.GlobalTransform = Transform3D.Identity;
            AndroidDisplacementGate.Note(AndroidDisplacementGate.Kind.Effect, new object(), "haze", new Aabb(new Vector3(-0.5f, -0.5f, 29.5f), Vector3.One), true, true, 40);
            AssertThat(AndroidDisplacementGate.SeenFrom(camera, 40)).IsFalse();

            // The next frames: the effect keeps waking it, the camera turns round.
            AndroidDisplacementGate.Note(AndroidDisplacementGate.Kind.Effect, new object(), "haze", new Aabb(new Vector3(-0.5f, -0.5f, 29.5f), Vector3.One), true, true, 41);
            camera.GlobalTransform = new Transform3D(new Basis(Vector3.Up, Mathf.Pi / 2f), Vector3.Zero);
            AssertThat(AndroidDisplacementGate.SeenFrom(camera, 41)).IsFalse();
            AndroidDisplacementGate.Note(AndroidDisplacementGate.Kind.Effect, new object(), "haze", new Aabb(new Vector3(-0.5f, -0.5f, 29.5f), Vector3.One), true, true, 42);
            camera.GlobalTransform = new Transform3D(new Basis(Vector3.Up, Mathf.Pi), Vector3.Zero);
            AssertThat(AndroidDisplacementGate.SeenFrom(camera, 42)).IsTrue();

            // And it sleeps again once the camera has turned away.
            AndroidDisplacementGate.Note(AndroidDisplacementGate.Kind.Effect, new object(), "haze", new Aabb(new Vector3(-0.5f, -0.5f, 29.5f), Vector3.One), true, true, 43);
            AndroidDisplacementGate.Note(AndroidDisplacementGate.Kind.Effect, new object(), "haze", new Aabb(new Vector3(-0.5f, -0.5f, 29.5f), Vector3.One), true, true, 44);
            camera.GlobalTransform = Transform3D.Identity;
            AssertThat(AndroidDisplacementGate.SeenFrom(camera, 44)).IsFalse();
        }
        finally
        {
            AndroidDisplacementGate.Reset();
            viewport.QueueFree();
        }
    }

    /// <summary>A phone Tuning row, off by default (the desktop's), named for a player; off on the desktop, and so is the log.</summary>
    [TestCase]
    public void TheKeysAreRegisteredOff()
    {
        foreach (string key in new[] { AndroidDisplacementGate.Key, AndroidDisplacementGate.LogKey })
        {
            RuntimeCVarDefinition? row = RuntimeCVarsAndroid.Tuning.SingleOrDefault(d => d.Name == key);
            AssertThat(row).OverrideFailureMessage(key).IsNotNull();
            AssertThat(row!.DefaultValue).OverrideFailureMessage(key).IsEqual("false");
        }
        AssertThat(AndroidSettingNames.Keys.Contains(AndroidDisplacementGate.Key)).IsTrue();
        AssertThat(GraphicsPresets.Members.Contains(AndroidDisplacementGate.LogKey)).IsFalse();
        AndroidDisplacementGate.Force(null);
        AssertThat(AndroidDisplacementGate.Enabled).IsFalse();
        AssertThat(AndroidDisplacementGate.Logging).IsFalse();
    }

    /// <summary>The shared files call the gate where they woke the layer.</summary>
    [TestCase]
    public void TheHooksAreInPlace()
    {
        string fx = FileText("Godot/src/Engine/fx/FableFx.cs");
        AssertThat(fx.Split("AndroidDisplacementGate.WakeEffect(this, CachedGlobalTransform, _dispMesh)").Length - 1).IsEqual(2);
        AssertThat(FileText("Godot/src/Engine/fx/Engine_FxSystem.cs")).Contains("AndroidDisplacementGate.WakeMeshParticle(");
        string meshes = FileText("Godot/src/Engine/fx/Engine_DisplacementMeshes.cs");
        AssertThat(meshes).Contains("if (held.Skinned && Refii.AndroidRuntime.AndroidDisplacementGate.TakesBody(held.Node, held.Mesh, held.Placed)) continue;\n                    if (held.Skinned) shownSkinned++;");
        AssertThat(meshes).Contains("Refii.AndroidRuntime.AndroidDisplacementGate.HoldIfSeen();\n        Engine_FxDisplacement.SleepUnseen(SeenBy);");
        AssertThat(FileText("Godot/src/Engine/fx/Engine_ModernDrunk.cs")).Contains("AndroidDisplacementGate.WakeDrunk();");
        string session = FileText("Godot/src/game/GameSession.cs");
        AssertThat(session).Contains("AndroidDisplacementGate.WakeDrunk();");
        AssertThat(session).Contains("Engine_DisplacementMeshes.Pump();\n\t\t\t\t\tRefii.AndroidRuntime.AndroidDisplacementGate.AfterPump();");
        AssertThat(FileText("Godot/src/game/GameDebugServer.cs")).Contains("[\"displacement\"] = Refii.AndroidRuntime.AndroidDisplacementGate.Describe(");
    }

    /// <summary>When inactive (desktop / settings off), hooks short-circuit without touching Godot node state and the frame-end node stays inside process_total.</summary>
    [TestCase]
    public void InactiveGateSkipsPerFrameInteropAndKeepsFrameEndBeforeMarkFrameEnd()
    {
        AndroidDisplacementGate.Reset();
        AndroidDisplacementGate.Force(false);
        AndroidDisplacementRect.Force(false);
        try
        {
            AssertThat(AndroidDisplacementGate.TakesBody(null!, null!, Transform3D.Identity)).IsFalse();
            AndroidDisplacementGate.HoldIfSeen();
            AndroidDisplacementGate.NoteStatic(3);
            AndroidDisplacementGate.AfterPump();

            string gateSource = FileText("Godot/src/android/AndroidDisplacementGate.cs");
            AssertThat(gateSource).Contains("private static bool Active => Enabled || AndroidDisplacementRect.Enabled || Logging;");
            AssertThat(gateSource).Contains("if (Active) EnsureFrameEnd();");
            AssertThat(gateSource).Contains("public override void _Ready() => ProcessPriority = int.MaxValue - 1;");
        }
        finally
        {
            AndroidDisplacementGate.Force(null);
            AndroidDisplacementRect.Force(null);
            AndroidDisplacementGate.Reset();
        }
    }
}
