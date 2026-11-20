using System.Linq;
using GdUnit4;
using Refii.AndroidRuntime;
using Refii.Launcher.Core;
using Refii.Models;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>`lite_flat_cutouts`: which materials take it, and what their build gains.</summary>
[TestSuite]
public sealed class AndroidFlatCutoutsTests
{
    private const string Prop = "res://src/Engine/material/lite/standard_material_lite.gdshader";
    private const string Tree = "res://src/Engine/material/lite/standard_material_tree_lite.gdshader";

    private const string PropWrapper =
        "shader_type spatial;\nrender_mode blend_mix, depth_draw_opaque, cull_disabled;\n" +
        "#define FABLE_LITE\n#include \"res://src/Engine/material/standard_material_body.gdshaderinc\"\n";

    [TestCase]
    public void ACutOutPropTakesIt()
    {
        AssertThat(AndroidFlatCutouts.Marks(Prop, cutout: true, blended: false, grass: false, character: false, on: true)).IsTrue();
    }

    [TestCase]
    public void SolidBlendedGrassAndCharacterPassesKeepTheirLight()
    {
        AssertThat(AndroidFlatCutouts.Marks(Prop, cutout: false, blended: false, grass: false, character: false, on: true)).IsFalse();
        AssertThat(AndroidFlatCutouts.Marks(Prop, cutout: true, blended: true, grass: false, character: false, on: true)).IsFalse();
        AssertThat(AndroidFlatCutouts.Marks(Prop, cutout: true, blended: false, grass: true, character: false, on: true)).IsFalse();
        AssertThat(AndroidFlatCutouts.Marks(Prop, cutout: true, blended: false, grass: false, character: true, on: true)).IsFalse();
    }

    [TestCase]
    public void OffOrOnATreeBuildNothingIsMarked()
    {
        AssertThat(AndroidFlatCutouts.Marks(Prop, cutout: true, blended: false, grass: false, character: false, on: false)).IsFalse();
        AssertThat(AndroidFlatCutouts.Marks(Tree, cutout: true, blended: false, grass: false, character: false, on: true)).IsFalse();
    }

    [TestCase]
    public void AMarkedBuildIsUnshadedWithFlatLeavesDefineBeforeItsBody()
    {
        string marked = Engine_ShaderSpecialisation.Compose(PropWrapper, new[] { AndroidFlatCutouts.Define }, false);

        int flat = marked.IndexOf("#define " + AndroidFlatLeaves.Define + "\n", System.StringComparison.Ordinal);
        int include = marked.IndexOf("#include", System.StringComparison.Ordinal);
        AssertThat(flat).IsGreaterEqual(0);
        AssertThat(flat).IsLess(include);
        AssertThat(marked).Contains("render_mode blend_mix, depth_draw_opaque, cull_disabled, unshaded;");

        // The marker's own Compose, alone, places the define before the first include too.
        string direct = AndroidFlatCutouts.Compose(PropWrapper, new[] { AndroidFlatCutouts.Define });
        AssertThat(direct.IndexOf("#define " + AndroidFlatLeaves.Define + "\n", System.StringComparison.Ordinal))
            .IsLess(direct.IndexOf("#include", System.StringComparison.Ordinal));

        string plain = Engine_ShaderSpecialisation.Compose(PropWrapper, new[] { "OTHER" }, false);
        AssertThat(plain).NotContains("unshaded");
        AssertThat(plain).NotContains(AndroidFlatLeaves.Define);
        AssertThat(AndroidFlatCutouts.Compose(PropWrapper, new[] { "OTHER" })).IsEqual(PropWrapper);
    }

    /// <summary>A pass's facts reach both prop shortcuts' choices the right way round (Engine_MdlMaterial.SpecialisationDefines).</summary>
    [TestCase]
    public void APassesFactsPickTheRightPropShortcut()
    {
        AssertThat(Defines(Engine_MdlMaterial.FactAlphaCut, flat: true)).Contains(AndroidFlatCutouts.Define);
        AssertThat(Defines(Engine_MdlMaterial.FactFoliage, flat: true)).Contains(AndroidFlatCutouts.Define);
        AssertThat(Defines(Engine_MdlMaterial.FactAlphaCut, flat: true)).NotContains(AndroidVertexLitProps.Define);
        AssertThat(Defines(Engine_MdlMaterial.FactAlphaCut | Engine_MdlMaterial.FactBlended, flat: true)).NotContains(AndroidFlatCutouts.Define);
        AssertThat(Defines(Engine_MdlMaterial.FactAlphaCut | Engine_MdlMaterial.FactGrass, flat: true)).NotContains(AndroidFlatCutouts.Define);

        // Each shortcut follows its own setting.
        AssertThat(Defines(Engine_MdlMaterial.FactAlphaCut, vertexLit: true)).NotContains(AndroidFlatCutouts.Define);
        AssertThat(Defines(0, flat: true)).NotContains(AndroidVertexLitProps.Define);
        AssertThat(Defines(0, vertexLit: true)).Contains(AndroidVertexLitProps.Define);
        AssertThat(Defines(0, vertexLit: true)).NotContains(AndroidFlatCutouts.Define);
    }

    /// <summary>A skinned model's pass takes neither prop shortcut, whether or not a character setting gave it a build.</summary>
    [TestCase]
    public void ACharactersPassesTakeNoPropShortcut()
    {
        foreach (int character in new[] { Engine_MdlMaterial.FactSkinned, Engine_MdlMaterial.FactCharacter })
        {
            AssertThat(Defines(Engine_MdlMaterial.FactAlphaCut | character, vertexLit: true, flat: true)).NotContains(AndroidFlatCutouts.Define);
            AssertThat(Defines(character, vertexLit: true, flat: true)).NotContains(AndroidVertexLitProps.Define);
        }
    }

    /// <summary>`debug_cutouts`: a developer row, off and in no preset; each mode its own define; only depth_only touches the render mode, and only of a build that can discard.</summary>
    [TestCase]
    public void TheCutoutDebugKeyIsOffInNoPresetAndComposesItsMode()
    {
        RuntimeCVarDefinition? row = RuntimeCVarsAndroid.EffectsAndCanvas.SingleOrDefault(d => d.Name == AndroidFlatCutouts.DebugKey);
        AssertThat(row).IsNotNull();
        AssertThat(row!.DefaultValue).IsEqual("off");
        AssertThat(row.Category).IsEqual(RuntimeCVarsGodot.DeveloperCategory);
        AssertThat(AndroidRestartNotes.DescriptionNeedsRestart(row.Description)).IsTrue();
        AssertThat(RuntimeCVarsAndroid.AllKeys.Contains(AndroidFlatCutouts.DebugKey)).IsTrue();
        foreach (string preset in new[] { GraphicsPresets.Low, GraphicsPresets.Medium, GraphicsPresets.High, GraphicsPresets.Ultra })
        {
            AssertThat(GraphicsPresets.Values(preset)!.ContainsKey(AndroidFlatCutouts.DebugKey)).IsFalse();
        }

        var none = new System.Collections.Generic.Dictionary<string, string>();
        AssertThat(AndroidFlatCutouts.DebugWanted(none)).IsEqual(AndroidFlatCutouts.DebugMode.Off);
        AssertThat(AndroidFlatCutouts.DebugDefines(AndroidFlatCutouts.DebugMode.Off)).IsEmpty();
        (string Value, AndroidFlatCutouts.DebugMode Mode, string Define)[] modes =
        {
            ("opaque", AndroidFlatCutouts.DebugMode.Opaque, "FABLE_DEBUG_CUTOUTS_OPAQUE"),
            ("\"depth_only\"", AndroidFlatCutouts.DebugMode.DepthOnly, "FABLE_DEBUG_CUTOUTS_DEPTH_ONLY"),
            ("Hidden", AndroidFlatCutouts.DebugMode.Hidden, "FABLE_DEBUG_CUTOUTS_HIDDEN"),
        };
        foreach ((string value, AndroidFlatCutouts.DebugMode mode, string define) in modes)
        {
            var set = new System.Collections.Generic.Dictionary<string, string> { [AndroidFlatCutouts.DebugKey] = value };
            AssertThat(AndroidFlatCutouts.DebugWanted(set)).IsEqual(mode);
            AssertThat(AndroidFlatCutouts.DebugDefines(mode)).ContainsExactly(define);
        }

        var odd = new System.Collections.Generic.Dictionary<string, string> { [AndroidFlatCutouts.DebugKey] = "true" };
        AssertThat(AndroidFlatCutouts.DebugWanted(odd)).IsEqual(AndroidFlatCutouts.DebugMode.Off);

        // depth_only makes a build that can discard unshaded; a solid (culled) build and the other modes are untouched.
        const string culled = "res://src/Engine/material/lite/standard_material_culled_lite.gdshader";
        AssertThat(AndroidFlatCutouts.ComposeDebug(PropWrapper, Prop, AndroidFlatCutouts.DebugMode.DepthOnly))
            .Contains("cull_disabled, unshaded;");
        AssertThat(AndroidFlatCutouts.ComposeDebug(PropWrapper, Tree, AndroidFlatCutouts.DebugMode.DepthOnly))
            .Contains("unshaded");
        AssertThat(AndroidFlatCutouts.ComposeDebug(PropWrapper, culled, AndroidFlatCutouts.DebugMode.DepthOnly)).IsEqual(PropWrapper);
        AssertThat(AndroidFlatCutouts.ComposeDebug(PropWrapper, Prop, AndroidFlatCutouts.DebugMode.Opaque)).IsEqual(PropWrapper);
        AssertThat(AndroidFlatCutouts.ComposeDebug(PropWrapper, Prop, AndroidFlatCutouts.DebugMode.Off)).IsEqual(PropWrapper);
    }

    private static string[] Defines(int facts, bool vertexLit = false, bool flat = false) =>
        Engine_MdlMaterial.SpecialisationDefines(facts, Prop, vertexLit, flat).ToArray();
}
