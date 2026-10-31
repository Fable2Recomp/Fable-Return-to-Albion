using System.IO;
using System.Runtime.CompilerServices;
using GdUnit4;
using Godot;
using Refii.Level;
using Refii.Models;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>`prop_far_shading_metres` (Engine_PropFarShading): the switch and the far build.</summary>
[TestSuite]
public sealed class PropFarShadingTests
{
    /// <summary>Far past the edge plus the hysteresis, near again inside the edge, held between.</summary>
    [TestCase]
    public void SwitchesWithHysteresis()
    {
        float edge = 30f;
        float past = edge + Engine_PropFarShading.HysteresisMetres;
        AssertThat(Engine_PropFarShading.Decide(edge + 1f, edge, far: false)).IsFalse();
        AssertThat(Engine_PropFarShading.Decide(past + 0.1f, edge, far: false)).IsTrue();
        AssertThat(Engine_PropFarShading.Decide(edge + 1f, edge, far: true)).IsTrue();
        AssertThat(Engine_PropFarShading.Decide(edge - 0.1f, edge, far: true)).IsFalse();
    }

    /// <summary>Off is near for every distance, a prop already far included.</summary>
    [TestCase]
    public void OffIsNear()
    {
        AssertThat(Engine_PropFarShading.Decide(1000f, 0f, far: true)).IsFalse();
        AssertThat(Engine_PropFarShading.Decide(1000f, 0f, far: false)).IsFalse();
    }

    /// <summary>Measured to the box's nearest point: 0 inside, the gap outside.</summary>
    [TestCase]
    public void MeasuresToTheNearestPoint()
    {
        var box = new Aabb(new Vector3(0f, 0f, 0f), new Vector3(10f, 2f, 10f));
        AssertThat(Engine_PropFarShading.Distance(box, new Vector3(5f, 1f, 5f))).IsEqual(0f);
        AssertThat(Engine_PropFarShading.Distance(box, new Vector3(13f, 1f, 5f))).IsEqual(3f);
        AssertThat(Engine_PropFarShading.Distance(box, new Vector3(13f, 6f, 5f))).IsEqual(5f);
    }

    private static string Here([CallerFilePath] string source = "") => Path.GetDirectoryName(source)!;

    /// <summary>The far build's defines are ones the model body compiles out.</summary>
    [TestCase]
    public void FarDefinesAreTheBodys()
    {
        string body = File.ReadAllText(Path.GetFullPath(Path.Combine(Here(),
            "../../src/Engine/material/standard_material_body.gdshaderinc")));
        foreach (string define in Engine_ShaderSpecialisation.FarDefines)
        {
            AssertThat(body.Contains("#ifdef " + define)).IsTrue();
        }
    }
}
