using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using GdUnit4;
using Refii.Core;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>Checks exact endpoint semantics, unsupported-state fallback and main-thread publication lifecycles.</summary>
[TestSuite]
public sealed class NearFogKnotsTests
{
    /// <summary>Builds a normal synthetic atmosphere without engine resources.</summary>
    private static NearFogSnapshot Create(float depth = 100f, float exponent = 0.4f, bool enabled = true) =>
        Engine_NearFogKnots.Create(enabled, 0.01f, 0.02f, 0.04f, depth, 0.01f, exponent, 2000f);

    /// <summary>Checks harmless endpoint values while readiness selects the unchanged shader path.</summary>
    private static void AssertDisabled(NearFogSnapshot snapshot)
    {
        AssertThat(snapshot.Ready).IsFalse();
        for (int index = 0; index < 5; index++)
        {
            NearFogRgb rgb = snapshot.At(index);
            AssertThat(rgb.Red).IsEqual(1f);
            AssertThat(rgb.Green).IsEqual(1f);
            AssertThat(rgb.Blue).IsEqual(1f);
        }
    }

    /// <summary>Finds a production resource relative to the test source rather than the runner directory.</summary>
    private static string Resource(string path, [CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../..", path));

    /// <summary>All five RGB knots use the guest spacing and a power-one case with independently known depths.</summary>
    [TestCase]
    public void LinearCurveKeepsFiveDistinctRgbEndpoints()
    {
        NearFogSnapshot snapshot = Engine_NearFogKnots.Create(true, 0.01f, 0.02f, 0.04f, 32f, 0.03125f, 1f, 2000f);
        AssertThat(snapshot.Ready).IsTrue();
        for (int index = 0; index < 5; index++)
        {
            float distance = index * 7.8125f;
            NearFogRgb rgb = snapshot.At(index);
            AssertThat(rgb.Red).IsEqual(MathF.Exp(-0.01f * distance));
            AssertThat(rgb.Green).IsEqual(MathF.Exp(-0.02f * distance));
            AssertThat(rgb.Blue).IsEqual(MathF.Exp(-0.04f * distance));
        }
        AssertThat(snapshot.At(1).Red > snapshot.At(1).Green && snapshot.At(1).Green > snapshot.At(1).Blue).IsTrue();
    }

    /// <summary>Distance clamping freezes later knots while RGB beta zero and finite exponential underflow remain valid.</summary>
    [TestCase]
    public void RangeClampZeroBetaAndUnderflowHaveDefinedResults()
    {
        NearFogSnapshot clamped = Engine_NearFogKnots.Create(true, 0f, 0.02f, 0.04f, 32f, 0.03125f, 1f, 5f);
        AssertThat(clamped.Ready).IsTrue();
        for (int index = 1; index < 5; index++)
        {
            AssertThat(clamped.At(index).Red).IsEqual(1f);
            AssertThat(clamped.At(index).Green).IsEqual(MathF.Exp(-0.1f));
            AssertThat(clamped.At(index).Blue).IsEqual(MathF.Exp(-0.2f));
        }
        NearFogSnapshot dense = Engine_NearFogKnots.Create(true, 100f, 200f, 300f, 100f, 1f, 1f, 2000f);
        AssertThat(dense.Ready).IsTrue();
        AssertThat(dense.At(0).Red).IsEqual(1f);
        AssertThat(dense.At(1).Red).IsEqual(0f);
        NearFogSnapshot noDepth = Create(depth: 0f);
        AssertThat(noDepth.Ready).IsTrue();
        AssertThat(noDepth.At(4).Blue).IsEqual(1f);
    }

    /// <summary>Every nonfinite or negative input and a nonpositive exponent preserves the original calculations.</summary>
    [TestCase]
    public void InvalidInputsNeverBecomeReady()
    {
        foreach (float invalid in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity, -0.01f })
        {
            for (int parameter = 0; parameter < 7; parameter++)
            {
                float[] values = [0.01f, 0.02f, 0.04f, 100f, 0.01f, 0.4f, 2000f];
                values[parameter] = invalid;
                AssertDisabled(Engine_NearFogKnots.Create(true, values[0], values[1], values[2], values[3], values[4], values[5], values[6]));
            }
        }
        AssertDisabled(Create(exponent: 0f));
        AssertDisabled(Create(exponent: -0f));
        AssertDisabled(Engine_NearFogKnots.Create(true, 1f, 1f, 1f, float.MaxValue, float.MaxValue, 4f, 2000f));
    }

    /// <summary>A fog-free reused atmosphere ignores otherwise valid retained curve fields and cannot retain old readiness.</summary>
    [TestCase]
    public void DisabledFogAndNewSnapshotsDoNotRetainEarlierValues()
    {
        NearFogSnapshot original = Create();
        NearFogRgb captured = original.At(1);
        NearFogSnapshot updated = Create(depth: 300f);
        AssertThat(updated.At(1).Red < captured.Red).IsTrue();
        AssertThat(original.At(1).Red).IsEqual(captured.Red);
        AssertThat(original.Ready).IsTrue();
        AssertDisabled(Create(enabled: false));
        AssertThat(default(NearFogSnapshot).Ready).IsFalse();
    }

    /// <summary>Only the five registered knot indices can be published or read.</summary>
    [TestCase]
    public void KnotIndicesAndRegisteredNamesAreBounded()
    {
        for (int index = 0; index < 5; index++)
            AssertThat(Engine_NearFogKnots.GlobalName(index)).IsEqual("atmos_near_t" + index);
        foreach (int invalid in new[] { -1, 5, int.MinValue, int.MaxValue })
        {
            bool readFailed = false, nameFailed = false;
            try { Create().At(invalid); } catch (ArgumentOutOfRangeException) { readFailed = true; }
            try { Engine_NearFogKnots.GlobalName(invalid); } catch (ArgumentOutOfRangeException) { nameFailed = true; }
            AssertThat(readFailed && nameFailed).IsTrue();
        }
    }

    /// <summary>Valid, changed, disabled, invalid and reloaded atmosphere states publish complete snapshots before enabling.</summary>
    [TestCase]
    public void AtmospherePublicationLifecycleNeverExposesPartialReadyEndpoints()
    {
        bool ready = false;
        var rgb = new Dictionary<string, NearFogRgb>();
        foreach (NearFogSnapshot snapshot in new[] { Create(), Create(depth: 300f), Create(enabled: false), Create(exponent: 0f), Create() })
        {
            var order = new List<string>();
            Engine_NearFogKnots.Publish(snapshot, (name, value) =>
            {
                AssertThat(ready).IsFalse();
                rgb[name] = value;
                order.Add(name);
            }, (name, value) =>
            {
                AssertThat(name).IsEqual(Engine_NearFogKnots.ReadyGlobal);
                if (value)
                {
                    AssertThat(order.Count).IsEqual(6);
                    for (int index = 0; index < 5; index++)
                        AssertThat(rgb[Engine_NearFogKnots.GlobalName(index)].Red).IsEqual(snapshot.At(index).Red);
                }
                ready = value;
                order.Add(value ? "ready" : "fallback");
            });
            AssertThat(order.Count).IsEqual(7);
            AssertThat(order[0]).IsEqual("fallback");
            AssertThat(ready).IsEqual(snapshot.Ready);
            for (int index = 0; index < 5; index++)
                AssertThat(order[index + 1]).IsEqual(Engine_NearFogKnots.GlobalName(index));
        }
    }

    /// <summary>A failed endpoint write leaves fallback selected; an ordinary retry can publish a complete replacement.</summary>
    [TestCase]
    public void PublisherFailureDoesNotEnablePartialSnapshotAndRetrySucceeds()
    {
        bool ready = true, failed = false;
        int writes = 0;
        Action<string, bool> publishReady = (_, value) => ready = value;
        try
        {
            Engine_NearFogKnots.Publish(Create(), (_, _) =>
            {
                AssertThat(ready).IsFalse();
                if (++writes == 3) throw new InvalidOperationException("synthetic publisher failure");
            }, publishReady);
        }
        catch (InvalidOperationException) { failed = true; }
        AssertThat(failed).IsTrue();
        AssertThat(ready).IsFalse();
        AssertThat(writes).IsEqual(3);
        writes = 0;
        Engine_NearFogKnots.Publish(Create(depth: 300f), (_, _) => { AssertThat(ready).IsFalse(); writes++; }, publishReady);
        AssertThat(ready).IsTrue();
        AssertThat(writes).IsEqual(5);
    }

    /// <summary>Null publishers are rejected before any readiness or endpoint write.</summary>
    [TestCase]
    public void MissingPublisherHasNoSideEffects()
    {
        int writes = 0;
        bool rejectedRgb = false, rejectedBoolean = false;
        try { Engine_NearFogKnots.Publish(Create(), null!, (_, _) => writes++); }
        catch (ArgumentNullException) { rejectedRgb = true; }
        try { Engine_NearFogKnots.Publish(Create(), (_, _) => writes++, null!); }
        catch (ArgumentNullException) { rejectedBoolean = true; }
        AssertThat(rejectedRgb && rejectedBoolean).IsTrue();
        AssertThat(writes).IsEqual(0);
    }

    /// <summary>Failed invalidation cannot start endpoint writes; failed final enable leaves the fallback already published.</summary>
    [TestCase]
    public void ReadinessPublicationFailuresCannotPublishAnIncompleteReadySet()
    {
        foreach (bool failAtEnable in new[] { false, true })
        {
            bool ready = true, failed = false;
            int writes = 0;
            try
            {
                Engine_NearFogKnots.Publish(Create(), (_, _) => writes++, (_, value) =>
                {
                    if (value == failAtEnable) throw new InvalidOperationException("readiness publication");
                    ready = value;
                });
            }
            catch (InvalidOperationException) { failed = true; }
            AssertThat(failed).IsTrue();
            AssertThat(writes).IsEqual(failAtEnable ? 5 : 0);
            AssertThat(ready).IsEqual(!failAtEnable);
        }
    }

    /// <summary>Shader globals and publication retain the snapshot contract while upstream shifts the guest lookup by half a texel.</summary>
    [TestCase]
    public void ProductionRegistrationAndPublisherUseTheSameSnapshotContract()
    {
        string project = File.ReadAllText(Resource("project.godot")).Replace("\r\n", "\n");
        AssertThat(project.Contains("atmos_near_ready={\n\"type\": \"bool\",\n\"value\": false\n}", StringComparison.Ordinal)).IsTrue();
        for (int index = 0; index < 5; index++)
            AssertThat(project.Contains(Engine_NearFogKnots.GlobalName(index) + "={\n\"type\": \"vec3\",\n\"value\": Vector3(1, 1, 1)\n}", StringComparison.Ordinal)).IsTrue();
        string publisher = File.ReadAllText(Resource("src/Engine/level/environment/Engine_SkyEnvironment.cs"));
        int invalidated = publisher.IndexOf("GlobalShaderParameterSet(Engine_NearFogKnots.ReadyGlobal, false)", StringComparison.Ordinal);
        int beta = publisher.IndexOf("GlobalShaderParameterSet(AtmosBetaName", StringComparison.Ordinal);
        int published = publisher.IndexOf("Engine_NearFogKnots.Publish(nearFog", StringComparison.Ordinal);
        AssertThat(invalidated >= 0 && beta > invalidated && published > beta).IsTrue();
        string shader = File.ReadAllText(Resource("src/Engine/level/environment/fable_atmosphere.gdshaderinc"));
        AssertThat(shader.Contains("float dl = max(d - (0.5 * knot), 0.0);", StringComparison.Ordinal)).IsTrue();
        AssertThat(shader.Contains("dl < knot * 4.0 && fable_near_fog_precompute && atmos_near_ready", StringComparison.Ordinal)).IsTrue();
        AssertThat(shader.Contains("(dl - below) / knot", StringComparison.Ordinal)).IsTrue();
        AssertThat(shader.Contains("float t = min(dl, atmos_range.y) * atmos_curve.y;", StringComparison.Ordinal)).IsTrue();
    }
}
