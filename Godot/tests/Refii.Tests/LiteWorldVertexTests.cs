using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GdUnit4;
using Refii.Launcher.Core;
using Refii.Models;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>`lite_world_vertex`: an Android setting that composes FABLE_LITE_WORLD_VERTEX into the lite builds.</summary>
[TestSuite]
public sealed class LiteWorldVertexTests
{
    private const string Define = "FABLE_LITE_WORLD_VERTEX";

    private static string Source(params string[] defines) =>
        string.Concat(defines.Select(d => $"#define {d}\n")) + "#include \"body.gdshaderinc\"\n";

    [TestCase]
    public void TheKeyIsRegisteredOffWithAPlayersName()
    {
        RuntimeCVarDefinition? row = RuntimeCVarsAndroid.Tuning.SingleOrDefault(d => d.Name == "lite_world_vertex");
        AssertThat(row).IsNotNull();
        AssertThat(row!.DefaultValue).IsEqual("false");
        AssertThat(AndroidSettingNames.Keys.Contains("lite_world_vertex")).IsTrue();
        AssertThat(Engine_ShaderVariant.WorldVertex).IsEqual(Define);
    }

    [TestCase]
    public void UnsetIsOffAndTrueIsOn()
    {
        AssertThat(Engine_ShaderVariant.WorldVertexWanted(new Dictionary<string, string>())).IsFalse();
        AssertThat(Engine_ShaderVariant.WorldVertexWanted(new Dictionary<string, string> { ["lite_world_vertex"] = "true" })).IsTrue();
    }

    [TestCase]
    public void TheProcessDefinesCarryItAfterTheSkippedLightsAndAheadOfTheExtras()
    {
        string[] extra = { "FABLE_LITE_MEDIUMP" };
        AssertThat(Engine_ShaderVariant.ProcessDefines(true, extra, worldVertex: true))
            .IsEqual(new[] { Engine_ShaderVariant.SkipBlackLights, Define, "FABLE_LITE_MEDIUMP" });
        AssertThat(Engine_ShaderVariant.ProcessDefines(false, extra)).IsEqual(extra);
    }

    [TestCase]
    public void ComposingAddsTheDefineOnceAndOnlyWhenAsked()
    {
        string code = Source("FABLE_LITE");
        string on = Engine_ShaderVariant.WithExtraDefines(code, Engine_ShaderVariant.ProcessDefines(false, Array.Empty<string>(), true));
        string off = Engine_ShaderVariant.WithExtraDefines(code, Engine_ShaderVariant.ProcessDefines(false, Array.Empty<string>()));
        AssertThat(Engine_ShaderVariant.Defines(on, Define)).IsTrue();
        AssertThat(Engine_ShaderVariant.Defines(off, Define)).IsFalse();
        string stated = Engine_ShaderVariant.WithExtraDefines(Source("FABLE_LITE", Define),
            Engine_ShaderVariant.ProcessDefines(false, new[] { Define }, true));
        AssertThat(stated.Split(Define).Length - 1).IsEqual(1);
    }

    /// <summary>The model body's own switches (the phone's include it hooks in a lite build) state the render
    /// mode under the define itself, so the ground's builds, which take the same process defines, keep their
    /// own vertex space.</summary>
    [TestCase]
    public void OnlyTheModelBodyTurnsOnWorldVertexCoordinates()
    {
        string? root = Root();
        AssertThat(root).IsNotNull();
        string body = File.ReadAllText(Path.Combine(root!, "Godot/src/Engine/material/standard_material_body.gdshaderinc"));
        AssertThat(body.Contains("#ifdef FABLE_LITE\n#include \"res://src/android/standard_lite_switches.gdshaderinc\"\n#else", StringComparison.Ordinal)).IsTrue();
        AssertThat(body.Contains("world_vertex_coords", StringComparison.Ordinal)).IsFalse();
        string switches = File.ReadAllText(Path.Combine(root!, "Godot/src/android/standard_lite_switches.gdshaderinc"));
        AssertThat(switches.Contains("#ifdef " + Define + "\nrender_mode world_vertex_coords;", StringComparison.Ordinal)).IsTrue();
        string terrain = File.ReadAllText(Path.Combine(root!, "Godot/src/Engine/level/terrain/terrain_splat_body.gdshaderinc"));
        AssertThat(terrain.Contains("world_vertex_coords", StringComparison.Ordinal)).IsFalse();
        AssertThat(terrain.Contains("standard_lite_switches", StringComparison.Ordinal)).IsFalse();
    }

    private static string? Root()
    {
        string? at = AppContext.BaseDirectory;
        for (int i = 0; i < 12 && at != null; i++)
        {
            if (File.Exists(Path.Combine(at, "Godot", "project.godot")))
            {
                return at;
            }

            at = Path.GetDirectoryName(at);
        }

        return null;
    }
}
