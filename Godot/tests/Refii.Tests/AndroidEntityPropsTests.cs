using System.Collections.Generic;
using System.Linq;
using GdUnit4;
using Godot;
using Refii.AndroidRuntime;
using Refii.Core;
using Refii.Launcher.Core;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>
/// The rigid entity props' levers (AndroidEntityProps): `android_entity_batches`,
/// `android_entity_batch_cell_metres`, `android_entity_prop_lod_bias`, `android_entity_prop_distance_scale`,
/// and the shared range rule's reach scale they ride on.
/// </summary>
[TestSuite]
public sealed class AndroidEntityPropsTests
{
    private static string FileText(string repoPath, [System.Runtime.CompilerServices.CallerFilePath] string source = "") =>
        System.IO.File.ReadAllText(System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(source)!, "../../..", repoPath))).Replace("\r\n", "\n");

    private static Dictionary<string, string> With(string key, string value) => new() { [key] = value };

    /// <summary>Unset or unknown is auto; the three stated modes read as themselves, in any case.</summary>
    [TestCase]
    public void ModeIsAutoUnlessStated()
    {
        AssertThat(AndroidEntityProps.Mode(new Dictionary<string, string>())).IsEqual(AndroidEntityProps.Auto);
        AssertThat(AndroidEntityProps.Mode(With(AndroidEntityProps.BatchesKey, "\"cells\""))).IsEqual(AndroidEntityProps.Cells);
        AssertThat(AndroidEntityProps.Mode(With(AndroidEntityProps.BatchesKey, "ON"))).IsEqual(AndroidEntityProps.On);
        AssertThat(AndroidEntityProps.Mode(With(AndroidEntityProps.BatchesKey, "off"))).IsEqual(AndroidEntityProps.Off);
        AssertThat(AndroidEntityProps.Mode(With(AndroidEntityProps.BatchesKey, "sometimes"))).IsEqual(AndroidEntityProps.Auto);
    }

    /// <summary>Auto is what the phone did before: off under lean modern, upstream's 64 m batching otherwise.</summary>
    [TestCase]
    public void AutoKeepsThePhonesFormerBatching()
    {
        AssertThat(AndroidEntityProps.Batching(AndroidEntityProps.Auto, true, 8f) == (false, 64f)).IsTrue();
        AssertThat(AndroidEntityProps.Batching(AndroidEntityProps.Auto, false, 8f) == (true, 64f)).IsTrue();
        AssertThat(AndroidEntityProps.Batching(AndroidEntityProps.On, true, 8f) == (true, 64f)).IsTrue();
        AssertThat(AndroidEntityProps.Batching(AndroidEntityProps.Off, false, 8f) == (false, 64f)).IsTrue();
        AssertThat(AndroidEntityProps.Batching(AndroidEntityProps.Cells, true, 8f) == (true, 8f)).IsTrue();
        AssertThat(AndroidEntityProps.MaxCellMetres).IsEqual(Refii.Level.Engine_LevelProps.BatchCellMetres * 2f);
    }

    /// <summary>The cell defaults to 8 m and is held to 4..64; the bias to 0.1..4 and the distance to 0.25..2, 1 unless set.</summary>
    [TestCase]
    public void ValuesAreClamped()
    {
        AssertThat(AndroidEntityProps.WantedCell(new Dictionary<string, string>())).IsEqual(8f);
        AssertThat(AndroidEntityProps.WantedCell(With(AndroidEntityProps.CellKey, "1"))).IsEqual(4f);
        AssertThat(AndroidEntityProps.WantedCell(With(AndroidEntityProps.CellKey, "500"))).IsEqual(64f);
        AssertThat(AndroidEntityProps.WantedCell(With(AndroidEntityProps.CellKey, "-2"))).IsEqual(8f);
        AssertThat(AndroidEntityProps.WantedBias(new Dictionary<string, string>())).IsEqual(1f);
        AssertThat(AndroidEntityProps.WantedBias(With(AndroidEntityProps.LodBiasKey, "0.5"))).IsEqual(0.5f);
        AssertThat(AndroidEntityProps.WantedBias(With(AndroidEntityProps.LodBiasKey, "0"))).IsEqual(1f);
        AssertThat(AndroidEntityProps.WantedBias(With(AndroidEntityProps.LodBiasKey, "9"))).IsEqual(4f);
        AssertThat(AndroidEntityProps.WantedDistance(new Dictionary<string, string>())).IsEqual(1f);
        AssertThat(AndroidEntityProps.WantedDistance(With(AndroidEntityProps.DistanceKey, "0.75"))).IsEqual(0.75f);
        AssertThat(AndroidEntityProps.WantedDistance(With(AndroidEntityProps.DistanceKey, "0.1"))).IsEqual(0.25f);
        AssertThat(AndroidEntityProps.WantedDistance(With(AndroidEntityProps.DistanceKey, "5"))).IsEqual(2f);
    }

    /// <summary>Off a phone both prop levers are neutral and the batches keep upstream's cell.</summary>
    [TestCase]
    public void TheDesktopKeepsTheSharedRule()
    {
        if (Engine_Platform.IsAndroid)
        {
            return;
        }

        AssertThat(AndroidEntityProps.LodBias).IsEqual(1f);
        AssertThat(AndroidEntityProps.DistanceScale).IsEqual(1f);
        AssertThat(Refii.Entities.Engine_EntityBatches.CellMetres).IsEqual(Refii.Level.Engine_LevelProps.BatchCellMetres * 2f);
    }

    /// <summary>The reach scale multiplies a stated range and its margin; 1 leaves the shared rule exactly as it was.</summary>
    [TestCase]
    [RequireGodotRuntime]
    public void ReachScaleMultipliesTheStatedRange()
    {
        var mesh = new BoxMesh { Size = new Vector3(3f, 3f, 3f) };
        var node = new MeshInstance3D { Mesh = mesh };
        try
        {
            node.SetMeta(Refii.Level.Engine_PropDrawRange.Meta, new[] { 0f, 0f, 40f, 4f, 1f });
            Refii.Level.Engine_PropStreamer.ApplyVisibilityRange(node, mesh);
            float end = node.VisibilityRangeEnd, margin = node.VisibilityRangeEndMargin;
            Refii.Level.Engine_PropStreamer.ApplyVisibilityRange(node, mesh, 1f);
            AssertThat(node.VisibilityRangeEnd).IsEqual(end);
            AssertThat(node.VisibilityRangeEndMargin).IsEqual(margin);
            Refii.Level.Engine_PropStreamer.ApplyVisibilityRange(node, mesh, 0.5f);
            AssertThat(node.VisibilityRangeEnd).IsEqual(end * 0.5f);
            AssertThat(node.VisibilityRangeEndMargin).IsEqual(margin * 0.5f);
            // Read again from the stamp, never compounded.
            Refii.Level.Engine_PropStreamer.ApplyVisibilityRange(node, mesh, 0.5f);
            AssertThat(node.VisibilityRangeEnd).IsEqual(end * 0.5f);
        }
        finally
        {
            node.Free();
        }
    }

    /// <summary>A small prop's size tier moves with the scale too.</summary>
    [TestCase]
    [RequireGodotRuntime]
    public void ReachScaleMultipliesTheSizeTier()
    {
        var mesh = new BoxMesh { Size = new Vector3(0.4f, 0.4f, 0.4f) };
        var node = new MeshInstance3D { Mesh = mesh };
        try
        {
            Refii.Level.Engine_PropStreamer.ApplyVisibilityRange(node, mesh);
            float end = node.VisibilityRangeEnd;
            if (end <= 0f)
            {
                return; // prop_cull_distance_scale 0 in this configuration: no tier to scale.
            }

            Refii.Level.Engine_PropStreamer.ApplyVisibilityRange(node, mesh, 0.5f);
            AssertThat(node.VisibilityRangeEnd).IsEqualApprox(end * 0.5f, 1e-4f);
        }
        finally
        {
            node.Free();
        }
    }

    /// <summary>Every key is registered with its default and has a player's name.</summary>
    [TestCase]
    public void EveryKeyIsRegisteredWithItsDefaultAndAPlayersName()
    {
        var expected = new Dictionary<string, string>
        {
            [AndroidEntityProps.BatchesKey] = "auto",
            [AndroidEntityProps.CellKey] = "8",
            [AndroidEntityProps.LodBiasKey] = "1",
            [AndroidEntityProps.DistanceKey] = "1",
        };
        foreach ((string key, string value) in expected)
        {
            RuntimeCVarDefinition? row = RuntimeCVarsAndroid.Tuning.SingleOrDefault(d => d.Name == key);
            AssertThat(row).OverrideFailureMessage(key).IsNotNull();
            AssertThat(row!.DefaultValue).OverrideFailureMessage(key).IsEqual(value);
            AssertThat(AndroidSettingNames.Keys.Contains(key)).OverrideFailureMessage(key).IsTrue();
        }
    }

    /// <summary>
    /// The hooks: the host states the batching through the lever (no longer a bare lean-modern switch),
    /// the feed's rigid props and the batches take their range through it, interiors keep the shared one.
    /// </summary>
    [TestCase]
    public void TheHooksAreInPlace()
    {
        string host = FileText("Godot/src/android/AndroidGameHost.cs");
        AssertThat(host).Contains("AndroidEntityProps.ApplyBatching();");
        AssertThat(host).Contains("Refii.AndroidRuntime.AndroidEntityProps.Tick();");
        AssertThat(host).NotContains("if (AndroidLeanModern.Fx) Refii.Entities.Engine_EntityBatches.Enabled = false;");

        string feed = FileText("Godot/src/game/GameEntities.cs");
        AssertThat(feed).NotContains("Refii.Level.Engine_PropStreamer.ApplyVisibilityRange(node,");
        AssertThat(feed).Contains("Refii.Level.Engine_PropStreamer.ApplyVisibilityRange(intNode, intMesh);");

        string batches = FileText("Godot/src/Engine/entities/Engine_EntityBatches.cs");
        AssertThat(batches).Contains("float cellMetres = CellMetres;");
        AssertThat(batches).Contains("Refii.AndroidRuntime.AndroidEntityProps.ApplyVisibilityRange(batch.Node, batch.Mesh.Mesh);");
        AssertThat(batches).Contains("Cover(batch, ranges: true);");
    }
}
