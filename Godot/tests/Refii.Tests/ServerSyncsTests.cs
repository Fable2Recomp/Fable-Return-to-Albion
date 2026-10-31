using GdUnit4;
using Godot;
using Refii.Core;
using Refii.Models;
using System.Collections.Generic;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>The server-sync census and the material copy's type test, without Godot.</summary>
[TestSuite]
public sealed class ServerSyncsTests
{
    /// <summary>A window reports only what was counted inside it, by site.</summary>
    [TestCase]
    public void AWindowCountsOnlyItsOwnCalls()
    {
        Engine_ServerSyncs.End(Engine_ServerSyncs.Site.SurfaceArrays, Engine_ServerSyncs.Begin());
        Engine_ServerSyncs.Snapshot since = Engine_ServerSyncs.Take();
        Engine_ServerSyncs.End(Engine_ServerSyncs.Site.MultimeshBuffer, Engine_ServerSyncs.Begin());
        Engine_ServerSyncs.End(Engine_ServerSyncs.Site.MultimeshBuffer, Engine_ServerSyncs.Begin());

        Dictionary<string, object?> report = Engine_ServerSyncs.Report(since);
        var sites = (Dictionary<string, object?>)report["sites"]!;
        AssertThat(sites.ContainsKey("SurfaceArrays")).IsFalse();
        var buffer = (Dictionary<string, object?>)sites["MultimeshBuffer"]!;
        AssertThat((long)buffer["count"]!).IsEqual(2L);
        AssertThat((long)report["count"]!).IsEqual(2L);
        AssertThat(Engine_ServerSyncs.Describe(since, Engine_ServerSyncs.Take())).StartsWith("2 server sync(s)");
    }

    /// <summary>Godot's cached-value test: nil is never cached, arrays convert, the rest must match.</summary>
    [TestCase]
    public void TheCopyTakesOnlyWhatGodotWouldKeep()
    {
        AssertThat(Engine_MaterialCopy.TypesCompatible(Variant.Type.Float, Variant.Type.Nil)).IsFalse();
        AssertThat(Engine_MaterialCopy.TypesCompatible(Variant.Type.Float, Variant.Type.Float)).IsTrue();
        AssertThat(Engine_MaterialCopy.TypesCompatible(Variant.Type.Float, Variant.Type.Int)).IsFalse();
        AssertThat(Engine_MaterialCopy.TypesCompatible(Variant.Type.Vector4, Variant.Type.Color)).IsFalse();
        AssertThat(Engine_MaterialCopy.TypesCompatible(Variant.Type.PackedVector4Array, Variant.Type.Array)).IsTrue();
        AssertThat(Engine_MaterialCopy.TypesCompatible(Variant.Type.Array, Variant.Type.PackedFloat32Array)).IsTrue();
        AssertThat(Engine_MaterialCopy.TypesCompatible(Variant.Type.PackedVector4Array, Variant.Type.PackedFloat32Array)).IsFalse();
        AssertThat(Engine_MaterialCopy.TypesCompatible(Variant.Type.Object, Variant.Type.Object)).IsTrue();
    }
}
