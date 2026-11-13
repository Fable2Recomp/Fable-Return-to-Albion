using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Godot;
using GdUnit4;
using Refii.Fx;
using Refii.Lighting;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>
/// The per-frame writes that repeat: the displacement layer sleeping when no displacement mesh is
/// in view, and the lighting's Environment properties and global shader parameters going to the
/// renderer only when they change.
/// </summary>
[TestSuite]
public sealed class RepeatWriteSkipTests
{
    private static string FileText(string repoPath, [CallerFilePath] string source = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../../..", repoPath))).Replace("\r\n", "\n");

    /// <summary>A box-shaped "frustum" of half-size 10 about the origin, planes facing outward.</summary>
    private static Plane[] Cube() => new[]
    {
        new Plane(new Vector3(1, 0, 0), 10f), new Plane(new Vector3(-1, 0, 0), 10f),
        new Plane(new Vector3(0, 1, 0), 10f), new Plane(new Vector3(0, -1, 0), 10f),
        new Plane(new Vector3(0, 0, 1), 10f), new Plane(new Vector3(0, 0, -1), 10f),
    };

    private static readonly Aabb Unit = new(new Vector3(-0.5f, -0.5f, -0.5f), Vector3.One);

    [TestCase]
    public void AMeshInsideTheFrustumKeepsTheLayerAwake()
    {
        AssertThat(Engine_DisplacementMeshes.BoxTouchesFrustum(Unit, Transform3D.Identity, Cube())).IsTrue();
        AssertThat(Engine_DisplacementMeshes.BoxTouchesFrustum(Unit, new Transform3D(Basis.Identity, new Vector3(3, -4, 5)), Cube())).IsTrue();
    }

    [TestCase]
    public void AMeshStraddlingAPlaneKeepsTheLayerAwake()
    {
        var onEdge = new Transform3D(Basis.Identity, new Vector3(10f, 0, 0));
        AssertThat(Engine_DisplacementMeshes.BoxTouchesFrustum(Unit, onEdge, Cube())).IsTrue();
    }

    [TestCase]
    public void AMeshBeyondAPlaneLetsTheLayerSleep()
    {
        float gone = 10f + Engine_DisplacementMeshes.FrustumSlack + 1f;
        foreach (Vector3 at in new[] { new Vector3(gone, 0, 0), new Vector3(0, -gone, 0), new Vector3(0, 0, gone) })
        {
            AssertThat(Engine_DisplacementMeshes.BoxTouchesFrustum(Unit, new Transform3D(Basis.Identity, at), Cube())).IsFalse();
        }
    }

    [TestCase]
    public void TheSlackKeepsAMeshJustOutsideAwake()
    {
        var justOut = new Transform3D(Basis.Identity, new Vector3(10.5f + 0.5f * Engine_DisplacementMeshes.FrustumSlack, 0, 0));
        AssertThat(Engine_DisplacementMeshes.BoxTouchesFrustum(Unit, justOut, Cube())).IsTrue();
    }

    [TestCase]
    public void ARotatedAndScaledMeshUsesItsWorldBounds()
    {
        // A long box turned to point along X reaches the plane from far off centre; unturned it does not.
        var longBox = new Aabb(new Vector3(-0.5f, -0.5f, -8f), new Vector3(1f, 1f, 16f));
        var at = new Vector3(8f, 0f, 0f);
        var turned = new Transform3D(new Basis(Vector3.Up, Mathf.Pi / 2f), at);
        AssertThat(Engine_DisplacementMeshes.BoxTouchesFrustum(longBox, turned, Cube())).IsTrue();
        var far = new Vector3(30f, 0f, 0f);
        AssertThat(Engine_DisplacementMeshes.BoxTouchesFrustum(longBox, new Transform3D(Basis.Identity.Scaled(new Vector3(2, 2, 2)), far), Cube())).IsFalse();
    }

    [TestCase]
    public void AStaticOnlyWakeIsTakenBackInItsOwnFrameAgainstTheCopiedCamera()
    {
        string meshes = FileText("Godot/src/Engine/fx/Engine_DisplacementMeshes.cs");
        string layer = FileText("Godot/src/Engine/fx/Engine_FxDisplacement.cs");
        string session = FileText("Godot/src/game/GameSession.cs");

        // The pump places this frame's instances, states skinned parts as before, and only then
        // lets each view take back a wake it had for static meshes alone.
        AssertThat(meshes).Contains("public static void Pump()");
        AssertThat(meshes).NotContains("Engine_Views");
        AssertThat(meshes).Contains("if (held.Skinned) shownSkinned++;");
        int sleep = meshes.IndexOf("Engine_FxDisplacement.SleepUnseen(SeenBy);", StringComparison.Ordinal);
        AssertThat(sleep).IsGreater(meshes.IndexOf("held.Instance.SetVisible(visible);", StringComparison.Ordinal));
        AssertThat(sleep).IsGreater(meshes.IndexOf("Engine_FxDisplacement.NotifyActive();", StringComparison.Ordinal));
        AssertThat(meshes).Contains("if (!held.Skinned && held.Instance is not null && held.Visible &&");

        // The view wakes on the same frames as before; a static-only wake is marked with its own
        // frame, kept when anything else stated the layer active this frame, and tested against
        // the view's own camera, the copy it draws with.
        AssertThat(layer).Contains("bool active = _enabled && (stated || frame - _staticFrame <= 1);");
        AssertThat(layer).Contains("_staticOnlyFrame = active && !stated ? frame : ulong.MaxValue;");
        AssertThat(layer).Contains("if (_activeFrame == frame) return;");
        AssertThat(layer).Contains("if (_staticOnlyFrame != frame || _rect is null || _camera is null || seen(_camera)) return;");
        AssertThat(layer.IndexOf("_staticOnlyFrame = active", StringComparison.Ordinal))
            .IsLess(layer.IndexOf("_camera.GlobalTransform = main.GlobalTransform;", StringComparison.Ordinal));

        // The same frame: the layer processes before the session that pumps.
        AssertThat(layer).Contains("ProcessPriority = 1000;");
        AssertThat(session).Contains("ProcessPriority = int.MaxValue - 1;");
        AssertThat(session).Contains("Refii.Fx.Engine_DisplacementMeshes.Pump();");
    }

    [TestCase]
    public void ALastWrittenValueIsSentOnceUntilItChanges()
    {
        var last = new Engine_LastWritten<float>();
        AssertThat(last.Take(0f)).IsTrue();
        AssertThat(last.Take(0f)).IsFalse();
        AssertThat(last.Take(1.5f)).IsTrue();
        AssertThat(last.Take(1.5f)).IsFalse();
        AssertThat(last.Take(0f)).IsTrue();

        var vector = new Engine_LastWritten<Vector3>();
        AssertThat(vector.Take(Vector3.Zero)).IsTrue();
        AssertThat(vector.Take(Vector3.Zero)).IsFalse();
        AssertThat(vector.Take(new Vector3(0, 0, 1e-6f))).IsTrue();
    }

    [TestCase]
    public void NoEnvironmentPropertyOrFableGlobalIsWrittenUnguarded()
    {
        string src = FileText("Godot/src/Engine/Lighting/Engine_LevelLighting.cs");
        // Every property write is the guarded form `if (environment.X != v) environment.X = v;`.
        AssertThat(Regex.Matches(src, @"^\s*environment\.\w+ = ", RegexOptions.Multiline).Count).IsEqual(0);
        foreach (Match m in Regex.Matches(src, @"if \((?:!)?environment\.(\w+)[^)]*\) environment\.(\w+) = ", RegexOptions.Multiline))
        {
            AssertThat(m.Groups[2].Value).IsEqual(m.Groups[1].Value);
        }
        AssertThat(src).NotContains("GlobalShaderParameterSet(Fable");
        foreach (string name in new[] { "Unified", "FillFloor", "Interior", "AmbientRef", "InteriorFog", "InteriorBake", "SkyAmbient", "VolumetricFogEnd" })
        {
            AssertThat(src).Contains("Engine_LastWritten<");
            AssertThat(src).Contains("Last" + name + ".Set(Fable" + name + "Name,");
        }
    }
}
