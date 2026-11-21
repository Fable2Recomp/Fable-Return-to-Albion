using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using GdUnit4;
using Godot;
using Refii.AndroidRuntime;
using Refii.Launcher.Core;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>`android_displacement_rect` (<see cref="AndroidDisplacementRect"/>): the projection, the padding, the fallbacks and the setting.</summary>
[TestSuite]
public sealed class AndroidDisplacementRectTests
{
    private static string FileText(string repoPath, [CallerFilePath] string source = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../../..", repoPath))).Replace("\r\n", "\n");

    /// <summary>A camera at the origin looking down -Z: 90 degrees, square, near 0.1.</summary>
    private static Projection Lens() => Projection.CreatePerspective(90f, 1f, 0.1f, 100f, false);

    /// <summary>The frustum of <see cref="Lens"/> from the origin, outward normals as Camera3D.GetFrustum gives them.</summary>
    private static Plane[] LensFrustum()
    {
        float h = MathF.Sqrt(0.5f);
        return new[]
        {
            new Plane(new Vector3(0, 0, 1), -0.1f), new Plane(new Vector3(0, 0, -1), 100f),
            new Plane(new Vector3(-h, 0, h), 0f), new Plane(new Vector3(h, 0, h), 0f),
            new Plane(new Vector3(0, -h, h), 0f), new Plane(new Vector3(0, h, h), 0f),
        };
    }

    private static Aabb Cube(Vector3 centre, float size) => new(centre - Vector3.One * (size / 2f), Vector3.One * size);

    private static void Start(out Vector2 min, out Vector2 max)
    {
        min = new Vector2(float.MaxValue, float.MaxValue);
        max = new Vector2(float.MinValue, float.MinValue);
    }

    private static bool Near(float a, float b) => MathF.Abs(a - b) < 1e-4f;

    /// <summary>A box ahead projects to the screen rect of its nearest face; up on screen is a smaller uv y.</summary>
    [TestCase]
    public void ABoxAheadProjectsToItsScreenRect()
    {
        Start(out Vector2 min, out Vector2 max);
        // x and y in [-1, 1], z in [-11, -9]: the near face at 9 m spans 1/9 either side of the centre.
        AssertThat(AndroidDisplacementRect.AddBox(Transform3D.Identity, Lens(), Cube(new Vector3(0, 0, -10), 2), ref min, ref max)).IsTrue();
        AssertThat(Near(min.X, 0.5f - 0.5f / 9f) && Near(max.X, 0.5f + 0.5f / 9f)).IsTrue();
        AssertThat(Near(min.Y, 0.5f - 0.5f / 9f) && Near(max.Y, 0.5f + 0.5f / 9f)).IsTrue();

        Start(out min, out max);
        AndroidDisplacementRect.AddBox(Transform3D.Identity, Lens(), Cube(new Vector3(0, 5, -10), 1), ref min, ref max);
        AssertThat(max.Y).IsLess(0.5f);

        // The view transform moves the world: the camera 10 m to the right sees the box on its left.
        Start(out min, out max);
        var view = new Transform3D(Basis.Identity, new Vector3(10, 0, 0)).AffineInverse();
        AndroidDisplacementRect.AddBox(view, Lens(), Cube(new Vector3(0, 0, -20), 1), ref min, ref max);
        AssertThat(max.X).IsLess(0.5f);
    }

    /// <summary>A box across the near plane, or behind the camera, cannot be projected: full screen.</summary>
    [TestCase]
    public void ABoxAcrossTheNearPlaneFallsBackToFullScreen()
    {
        Start(out Vector2 min, out Vector2 max);
        AssertThat(AndroidDisplacementRect.AddBox(Transform3D.Identity, Lens(), Cube(Vector3.Zero, 4), ref min, ref max)).IsFalse();
        Start(out min, out max);
        AssertThat(AndroidDisplacementRect.AddBox(Transform3D.Identity, Lens(), Cube(new Vector3(0, 0, 0.05f), 0.02f), ref min, ref max)).IsFalse();

        Plane[] frustum = LensFrustum();
        Start(out min, out max);
        AssertThat(AndroidDisplacementRect.ScreenRect(Transform3D.Identity, Lens(), frustum,
            new[] { Cube(new Vector3(0, 0, -10), 2), Cube(Vector3.Zero, 4) }, ref min, ref max)).IsEqual(AndroidDisplacementRect.Fit.Full);
    }

    /// <summary>Boxes out of the frustum are left out; none in view is empty; several merge.</summary>
    [TestCase]
    public void OnlyBoxesInViewCount()
    {
        Plane[] frustum = LensFrustum();
        Start(out Vector2 min, out Vector2 max);
        // Behind the camera (and across its plane would be full screen, but out of view it never projects).
        AssertThat(AndroidDisplacementRect.ScreenRect(Transform3D.Identity, Lens(), frustum,
            new[] { Cube(new Vector3(0, 0, 30), 2) }, ref min, ref max)).IsEqual(AndroidDisplacementRect.Fit.Empty);
        AssertThat(AndroidDisplacementRect.ScreenRect(Transform3D.Identity, Lens(), frustum,
            ReadOnlySpan<Aabb>.Empty, ref min, ref max)).IsEqual(AndroidDisplacementRect.Fit.Empty);

        Start(out min, out max);
        AssertThat(AndroidDisplacementRect.ScreenRect(Transform3D.Identity, Lens(), frustum,
            new[] { Cube(new Vector3(-5, 0, -10), 1), Cube(new Vector3(0, 0, 30), 2), Cube(new Vector3(5, 0, -10), 1) }, ref min, ref max))
            .IsEqual(AndroidDisplacementRect.Fit.Rect);
        AssertThat(min.X).IsLess(0.3f);
        AssertThat(max.X).IsGreater(0.7f);
    }

    /// <summary>Pixels round outwards, grow by the pad and stay on the target; a rect off it is empty.</summary>
    [TestCase]
    public void PixelsGrowRoundOutAndClamp()
    {
        var size = new Vector2I(1000, 500);
        Rect2I r = AndroidDisplacementRect.ToPixels(new Vector2(0.4001f, 0.25f), new Vector2(0.5999f, 0.75f), size, 0f);
        AssertThat(r).IsEqual(new Rect2I(400, 125, 200, 250));
        r = AndroidDisplacementRect.ToPixels(new Vector2(0.25f, 0.25f), new Vector2(0.75f, 0.75f), size, 10f);
        AssertThat(r).IsEqual(new Rect2I(240, 115, 520, 270));
        // Past the edges: clamped.
        r = AndroidDisplacementRect.ToPixels(new Vector2(-0.5f, 0.875f), new Vector2(0.125f, 1.5f), size, 10f);
        AssertThat(r).IsEqual(new Rect2I(0, 427, 135, 73));
        // Wholly off screen: nothing.
        r = AndroidDisplacementRect.ToPixels(new Vector2(1.2f, 0.4f), new Vector2(1.5f, 0.6f), size, 10f);
        AssertThat(r.Size.X * r.Size.Y).IsEqual(0);

        Rect2I copy = AndroidDisplacementRect.Grow(new Rect2I(400, 200, 200, 100), size, 250.5f);
        AssertThat(copy).IsEqual(new Rect2I(149, 0, 702, 500));
        AssertThat(AndroidDisplacementRect.Grow(new Rect2I(400, 200, 200, 100), size, 5000f).Size).IsEqual(size);
        AssertThat(AndroidDisplacementRect.Share(new Rect2I(0, 0, 500, 250), size)).IsEqual(0.25f);
        AssertThat(AndroidDisplacementRect.UvRect(new Rect2I(100, 50, 400, 200), size)).IsEqual(new Vector4(0.1f, 0.1f, 0.5f, 0.5f));
    }

    /// <summary>The copy pad is the shader's largest offset (disp_pixel_scale on either axis) plus the linear fetch.</summary>
    [TestCase]
    public void ThePadIsTheLargestOffset()
    {
        AssertThat(AndroidDisplacementRect.MaxOffset(new Vector2(255f, 255f))).IsEqual(256f);
        AssertThat(AndroidDisplacementRect.MaxOffset(new Vector2(-100f, 40f))).IsEqual(101f);
        // The apply shader's own scale at 2280x1080 (Engine_FxDisplacement.Resize), Strength 1.
        float s = 0.5f * ((2280f / 1280f) + (1080f / 720f)) * 255f;
        AssertThat(AndroidDisplacementRect.MaxOffset(new Vector2(s, s))).IsGreater(400f);
    }

    /// <summary>The boxes are the gate's wakers and the static meshes, this frame and the last; a wake without a box means full screen.</summary>
    [TestCase]
    public void AWakeWithoutABoxMeansFullScreen()
    {
        AndroidDisplacementGate.Reset();
        AndroidDisplacementRect.Reset();
        var boxes = new List<Aabb>();
        AndroidDisplacementGate.Note(AndroidDisplacementGate.Kind.Effect, new object(), null, Cube(Vector3.Zero, 1), true, false, 100);
        AssertThat(AndroidDisplacementRect.Collect(100, boxes)).IsTrue();
        AssertThat(boxes.Count).IsEqual(1);
        AndroidDisplacementRect.NoteStaticAt(Cube(Vector3.One, 1), 100);
        AssertThat(AndroidDisplacementRect.Collect(101, boxes)).IsTrue();
        AssertThat(boxes.Count).IsEqual(2);
        AssertThat(AndroidDisplacementRect.Collect(102, boxes)).IsTrue();
        AssertThat(boxes.Count).IsEqual(0);

        AndroidDisplacementGate.Note(AndroidDisplacementGate.Kind.Drunk, null, null, default, false, true, 103);
        AssertThat(AndroidDisplacementRect.Collect(103, boxes)).IsFalse();
        AssertThat(AndroidDisplacementRect.Collect(104, boxes)).IsFalse();
        AndroidDisplacementGate.Note(AndroidDisplacementGate.Kind.Effect, new object(), null, Cube(Vector3.Zero, 1), true, false, 104);
        // The drunk frame still keeps the layer awake this frame.
        AssertThat(AndroidDisplacementRect.Collect(104, boxes)).IsFalse();
        AndroidDisplacementGate.Note(AndroidDisplacementGate.Kind.Effect, new object(), null, Cube(Vector3.Zero, 1), true, false, 105);
        AssertThat(AndroidDisplacementRect.Collect(105, boxes)).IsTrue();

        AndroidDisplacementGate.Note(AndroidDisplacementGate.Kind.FxMesh, new object(), "smoke", default, false, true, 110);
        AssertThat(AndroidDisplacementRect.Collect(110, boxes)).IsFalse();

        AndroidDisplacementGate.Reset();
        for (int i = 0; i <= AndroidDisplacementGate.Capacity; i++)
        {
            AndroidDisplacementGate.Note(AndroidDisplacementGate.Kind.Effect, new object(), null, Cube(Vector3.Zero, 1), true, true, 120);
        }
        AssertThat(AndroidDisplacementRect.Collect(120, boxes)).IsFalse();
        AndroidDisplacementGate.Reset();
        AndroidDisplacementRect.Reset();
    }

    /// <summary>The projection agrees with Godot's own camera: uv is UnprojectPosition over the viewport size.</summary>
    [TestCase]
    [RequireGodotRuntime]
    public void TheProjectionMatchesTheCamera()
    {
        var tree = (SceneTree)Engine.GetMainLoop();
        var viewport = new SubViewport { Size = new Vector2I(320, 180), RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled };
        var camera = new Camera3D { Fov = 60f, Near = 0.1f, Far = 500f, HOffset = 0.5f };
        viewport.AddChild(camera);
        tree.Root.AddChild(viewport);
        try
        {
            camera.GlobalTransform = new Transform3D(new Basis(Vector3.Up, 0.3f), new Vector3(1, 2, 3));
            Vector3 point = new(-1f, 2.5f, -12f);
            Vector2 min = new(float.MaxValue, float.MaxValue), max = new(float.MinValue, float.MinValue);
            AssertThat(AndroidDisplacementRect.AddBox(camera.GetCameraTransform().AffineInverse(), camera.GetCameraProjection(),
                new Aabb(point, Vector3.Zero), ref min, ref max)).IsTrue();
            Vector2 expected = camera.UnprojectPosition(point) / new Vector2(320, 180);
            AssertThat(MathF.Abs(min.X - expected.X) < 1e-3f && MathF.Abs(min.Y - expected.Y) < 1e-3f)
                .OverrideFailureMessage($"{min} against {expected}").IsTrue();
        }
        finally
        {
            viewport.QueueFree();
        }
    }

    /// <summary>A phone Tuning row, off by default (the desktop's), named for a player, a preset member; off on the desktop.</summary>
    [TestCase]
    public void TheKeyIsRegisteredOff()
    {
        RuntimeCVarDefinition? row = RuntimeCVarsAndroid.Tuning.SingleOrDefault(d => d.Name == AndroidDisplacementRect.Key);
        AssertThat(row).IsNotNull();
        AssertThat(row!.DefaultValue).IsEqual("false");
        AssertThat(AndroidSettingNames.Keys.Contains(AndroidDisplacementRect.Key)).IsTrue();
        AssertThat(GraphicsPresets.Members.Contains(AndroidDisplacementRect.Key)).IsTrue();
        AndroidDisplacementRect.Force(null);
        AssertThat(AndroidDisplacementRect.Enabled).IsFalse();
    }

    /// <summary>The hooks, and a shader whose default copies and clamps to the whole screen as before.</summary>
    [TestCase]
    public void TheHooksAndTheShaderDefaultAreInPlace()
    {
        AssertThat(FileText("Godot/src/Engine/fx/Engine_FxDisplacement.cs"))
            .Contains("if (_rect is not null && !Refii.AndroidRuntime.AndroidDisplacementRect.Owns(_rect) && _rect.Size != v)");
        AssertThat(FileText("Godot/src/Engine/fx/Engine_DisplacementMeshes.cs"))
            .Contains("else shownStatic++;\n                    if (!held.Skinned) Refii.AndroidRuntime.AndroidDisplacementRect.NoteStatic(held.Bounds, held.Placed);");
        AssertThat(FileText("Godot/src/android/AndroidDisplacementGate.cs")).Contains("AndroidDisplacementRect.Place(frame);");
        string shader = FileText("Godot/src/Engine/fx/fx_displacement_apply.gdshader");
        AssertThat(shader).Contains("uniform vec4 copy_rect = vec4(0.0, 0.0, 1.0, 1.0);");
        AssertThat(shader).Contains("uv = clamp(uv, max(copy_rect.xy, vec2(0.0)) + SCREEN_PIXEL_SIZE * 0.5, min(copy_rect.zw, vec2(1.0)) - SCREEN_PIXEL_SIZE * 0.5);");
        AssertThat(AndroidDisplacementRect.WholeScreen).IsEqual(new Vector4(0f, 0f, 1f, 1f));
    }
}
