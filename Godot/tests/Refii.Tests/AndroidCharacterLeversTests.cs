using System;
using System.Collections.Generic;
using System.Text.Json;
using GdUnit4;
using Godot;
using Refii.AndroidRuntime;
using Refii.Core;
using Refii.Models;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>
/// The character levers: `android_fur_max_shells` (AndroidFurShells), `android_character_cull_back`
/// (AndroidCharacterCull), the `charcensus` body rule (AndroidCharacterCensus) and the shader
/// ceiling's `characters` category.
/// </summary>
[TestSuite]
public sealed class AndroidCharacterLeversTests
{
    private static string FileText(string repoPath, [System.Runtime.CompilerServices.CallerFilePath] string source = "") =>
        System.IO.File.ReadAllText(System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(source)!, "../../..", repoPath))).Replace("\r\n", "\n");

    /// <summary>Unset is the title's rule; a cap below one shell pass is raised to one.</summary>
    [TestCase]
    public void FurCapIsOffUnlessSet()
    {
        AssertThat(AndroidFurShells.Wanted(new Dictionary<string, string>())).IsEqual(0);
        AssertThat(AndroidFurShells.Wanted(new Dictionary<string, string> { [AndroidFurShells.Key] = "8" })).IsEqual(8);
        AssertThat(AndroidFurShells.Wanted(new Dictionary<string, string> { [AndroidFurShells.Key] = "1" })).IsEqual(AndroidFurShells.MinLayers);
        AssertThat(AndroidFurShells.Wanted(new Dictionary<string, string> { [AndroidFurShells.Key] = "-3" })).IsEqual(0);
    }

    /// <summary>The cap only ever lowers the title's count, and never below one shell pass.</summary>
    [TestCase]
    public void FurCapOnlyLowers()
    {
        AssertThat(AndroidFurShells.Cap(16, 0)).IsEqual(16);
        AssertThat(AndroidFurShells.Cap(30, 0)).IsEqual(30);
        AssertThat(AndroidFurShells.Cap(16, 8)).IsEqual(8);
        AssertThat(AndroidFurShells.Cap(5, 8)).IsEqual(5);
        AssertThat(AndroidFurShells.Cap(16, 1)).IsEqual(AndroidFurShells.MinLayers);
    }

    /// <summary>Off a phone the title's count stands.</summary>
    [TestCase]
    public void TheDesktopKeepsTheTitlesFur()
    {
        if (Engine_Platform.IsAndroid)
        {
            return;
        }

        AssertThat(AndroidFurShells.Current).IsEqual(0);
        AssertThat(AndroidFurShells.Cap(16)).IsEqual(16);
    }

    /// <summary>Off by default; only models with skinned parts.</summary>
    [TestCase]
    public void CullBackIsCharactersOnlyAndOffByDefault()
    {
        AssertThat(AndroidCharacterCull.Wanted(new Dictionary<string, string>())).IsFalse();
        AssertThat(AndroidCharacterCull.Wanted(new Dictionary<string, string> { [AndroidCharacterCull.Key] = "true" })).IsTrue();
        AssertThat(AndroidCharacterCull.Decide(true, 3)).IsTrue();
        AssertThat(AndroidCharacterCull.Decide(true, 0)).IsFalse();
        AssertThat(AndroidCharacterCull.Decide(false, 3)).IsFalse();
    }

    /// <summary>Only the render mode's word changes; a comment naming it and a culled shader are left alone.</summary>
    [TestCase]
    public void CullBackSwapsOnlyTheRenderModeWord()
    {
        const string twoSided = "shader_type spatial;\n// Both sides (cull_disabled).\nrender_mode blend_mix, depth_draw_never, cull_disabled;\n#include \"x.gdshaderinc\"\n";
        string culled = AndroidCharacterCull.CullBack(twoSided);
        AssertThat(culled).Contains("render_mode blend_mix, depth_draw_never, cull_back;");
        AssertThat(culled).Contains("// Both sides (cull_disabled).");
        AssertThat(AndroidCharacterCull.IsTwoSided(twoSided)).IsTrue();
        AssertThat(AndroidCharacterCull.IsTwoSided(culled)).IsFalse();

        const string oneSided = "shader_type spatial;\nrender_mode blend_mix, depth_draw_opaque, cull_back;\n";
        AssertThat(AndroidCharacterCull.CullBack(oneSided)).IsEqual(oneSided);
    }

    /// <summary>The specialisation composes the swap only for a build carrying the marker.</summary>
    [TestCase]
    public void SpecialisationComposesTheCullOnlyWithItsMarker()
    {
        const string wrapper = "shader_type spatial;\nrender_mode blend_mix, depth_draw_opaque, cull_disabled;\n#include \"res://src/Engine/material/standard_material_body.gdshaderinc\"\n";
        string marked = Engine_ShaderSpecialisation.Compose(wrapper, new[] { AndroidCharacterCull.Define }, false);
        AssertThat(marked).Contains("cull_back;");
        AssertThat(marked).Contains("#define " + AndroidCharacterCull.Define);
        AssertThat(AndroidCharacterCull.IsTwoSided(marked)).IsFalse();

        string plain = Engine_ShaderSpecialisation.Compose(wrapper, new[] { "FABLE_SPEC_NO_NORMAL_MAP" }, false);
        AssertThat(AndroidCharacterCull.IsTwoSided(plain)).IsTrue();
    }

    /// <summary>Off a phone nothing culls and the shared material keys are as they were.</summary>
    [TestCase]
    public void TheDesktopKeepsBothSides()
    {
        if (Engine_Platform.IsAndroid)
        {
            return;
        }

        AssertThat(AndroidCharacterCull.On).IsFalse();
        AssertThat(AndroidCharacterCull.For(new MdlInfo { SkinnedCount = 2 })).IsFalse();
        AssertThat(AndroidTranslucentPass.KeyPart(new MdlInfo { SkinnedCount = 2 })).IsEqual(string.Empty);
    }

    /// <summary>A character's body is a mesh on a rig of at least twelve binds; a rigged door is not.</summary>
    [TestCase]
    [RequireGodotRuntime]
    public void CharacterBodiesAreMeshesOnFullRigs()
    {
        var rig = new Skin();
        for (int i = 0; i < AndroidCharacterCensus.CharacterBones; i++)
        {
            rig.AddBind(i, Transform3D.Identity);
        }

        var door = new Skin();
        door.AddBind(0, Transform3D.Identity);
        var body = new MeshInstance3D { Skin = rig };
        var hinge = new MeshInstance3D { Skin = door };
        var plain = new MeshInstance3D();
        var other = new Node3D();
        try
        {
            AssertThat(AndroidCharacterCensus.IsCharacterBody(body)).IsTrue();
            AssertThat(AndroidCharacterCensus.IsCharacterBody(hinge)).IsFalse();
            AssertThat(AndroidCharacterCensus.IsCharacterBody(plain)).IsFalse();
            AssertThat(AndroidCharacterCensus.IsCharacterBody(other)).IsFalse();
        }
        finally
        {
            body.Free();
            hinge.Free();
            plain.Free();
            other.Free();
        }
    }

    /// <summary>The census's distance bands: 0-5, 5-15, 15-30, 30-60 and past 60 m.</summary>
    [TestCase]
    public void DistanceBands()
    {
        AssertThat(AndroidCharacterCensus.BandOf(0f)).IsEqual(0);
        AssertThat(AndroidCharacterCensus.BandOf(4.9f)).IsEqual(0);
        AssertThat(AndroidCharacterCensus.BandOf(5f)).IsEqual(1);
        AssertThat(AndroidCharacterCensus.BandOf(29f)).IsEqual(2);
        AssertThat(AndroidCharacterCensus.BandOf(45f)).IsEqual(3);
        AssertThat(AndroidCharacterCensus.BandOf(500f)).IsEqual(4);
    }

    private static Engine_ShaderCeiling.Request Ceiling(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return Engine_ShaderCeiling.Parse(document.RootElement)!.Value;
    }

    /// <summary>Unnamed, the characters take the entities' mode, so an entities request means what it did before the split.</summary>
    [TestCase]
    public void CeilingCharactersFollowTheEntitiesUnlessNamed()
    {
        int entities = (int)Engine_ShaderCeiling.Category.Entities;
        int characters = (int)Engine_ShaderCeiling.Category.Characters;

        Engine_ShaderCeiling.Request all = Ceiling("{\"shaderCeiling\":{\"entities\":\"hide\"}}");
        AssertThat(all.Modes[characters] == Engine_ShaderCeiling.Mode.Hide).IsTrue();

        Engine_ShaderCeiling.Request only = Ceiling("{\"shaderCeiling\":{\"characters\":\"hide\"}}");
        AssertThat(only.Modes[characters] == Engine_ShaderCeiling.Mode.Hide).IsTrue();
        AssertThat(only.Modes[entities] == Engine_ShaderCeiling.Mode.Off).IsTrue();

        Engine_ShaderCeiling.Request rest = Ceiling("{\"shaderCeiling\":{\"entities\":\"flat\",\"characters\":\"off\"}}");
        AssertThat(rest.Modes[entities] == Engine_ShaderCeiling.Mode.Flat).IsTrue();
        AssertThat(rest.Modes[characters] == Engine_ShaderCeiling.Mode.Off).IsTrue();

        Engine_ShaderCeiling.Request words = Ceiling("{\"shaderCeiling\":{\"entities\":\"+shadows_disabled\"}}");
        AssertThat(string.Join(",", words.Words[characters]!)).IsEqual("shadows_disabled");

        Engine_ShaderCeiling.Request hidden = Ceiling("{\"shaderCeiling\":\"hide\"}");
        AssertThat(hidden.Modes[characters] == Engine_ShaderCeiling.Mode.Hide).IsTrue();
        AssertThat(hidden.Modes[(int)Engine_ShaderCeiling.Category.Attachments] == Engine_ShaderCeiling.Mode.Hide).IsTrue();
    }

    /// <summary>The census counts by the shader ceiling's own body rule, which lives in the shared debug server.</summary>
    [TestCase]
    [RequireGodotRuntime]
    public void TheCensusAndTheCeilingShareOneBodyRule()
    {
        AssertThat(AndroidCharacterCensus.CharacterBones).IsEqual(Refii.Game.GameDebugServer.CharacterBones);
        var full = new Skin();
        for (int i = 0; i < Refii.Game.GameDebugServer.CharacterBones; i++)
        {
            full.AddBind(i, Transform3D.Identity);
        }
        var nearly = new Skin();
        for (int i = 0; i < Refii.Game.GameDebugServer.CharacterBones - 1; i++)
        {
            nearly.AddBind(i, Transform3D.Identity);
        }
        var body = new MeshInstance3D { Skin = full };
        var prop = new MeshInstance3D { Skin = nearly };
        try
        {
            AssertThat(Refii.Game.GameDebugServer.IsCharacterBody(body)).IsTrue();
            AssertThat(Refii.Game.GameDebugServer.IsCharacterBody(prop)).IsFalse();
            AssertThat(AndroidCharacterCensus.IsCharacterBody(body)).IsTrue();
            AssertThat(AndroidCharacterCensus.IsCharacterBody(prop)).IsFalse();
        }
        finally
        {
            body.Free();
            prop.Free();
        }
        string ceiling = FileText("Godot/src/game/GameDebugServer.ShaderCeiling.cs");
        AssertThat(ceiling).NotContains("AndroidRuntime");
        AssertThat(ceiling).Contains("new(CeilingRoots, IsTreeProp, IsCharacterBody, CeilingSwitch)");
    }

    /// <summary>Off a phone a body's mesh swap does no lookup and touches no override.</summary>
    [TestCase]
    public void TheDesktopMeshSwapDoesNothing()
    {
        AssertThat(Engine_Platform.IsAndroid).IsFalse();
        AssertThat(FileText("Godot/src/android/AndroidCharacterLevers.cs"))
            .Contains("if (!Engine_Platform.IsAndroid || !ById.TryGetValue(node.GetInstanceId(), out Body? body))");
    }
}
