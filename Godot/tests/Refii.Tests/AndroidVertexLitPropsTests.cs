using GdUnit4;
using Refii.AndroidRuntime;
using Refii.Models;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>
/// `lite_vertex_lit_props`: a prop material on the lite standard or culled builds that is not foliage, grass or a
/// character carries the marker, and the specialised build that carries it takes `vertex_lighting`.
/// </summary>
[TestSuite]
public sealed class AndroidVertexLitPropsTests
{
    private const string Lite = "res://src/Engine/material/lite/";

    private const string Prop =
        "shader_type spatial;\nrender_mode blend_mix, depth_draw_opaque, cull_disabled;\n" +
        "#define FABLE_LITE\n#include \"res://src/Engine/material/standard_material_body.gdshaderinc\"\n";

    [TestCase]
    public void TheNoSpecularMarkerAddsSpecularDisabledOnlyToAVertexLitBuild()
    {
        string both = AndroidVertexLitProps.Compose(Prop, new[] { AndroidVertexLitProps.Define, AndroidVertexLitProps.NoSpecularDefine });
        AssertThat(both.Contains("vertex_lighting")).IsTrue();
        AssertThat(both.Contains("specular_disabled")).IsTrue();

        string litOnly = AndroidVertexLitProps.Compose(Prop, new[] { AndroidVertexLitProps.Define });
        AssertThat(litOnly.Contains("specular_disabled")).IsFalse();

        // The marker alone, without vertex lighting, changes nothing.
        AssertThat(AndroidVertexLitProps.Compose(Prop, new[] { AndroidVertexLitProps.NoSpecularDefine })).IsEqual(Prop);
        AssertThat(AndroidVertexLitProps.NoSpecularOn).IsFalse();
    }

    [TestCase]
    public void OffMarksNothing()
    {
        AssertThat(AndroidVertexLitProps.Marks(Lite + "standard_material_lite.gdshader", false, false, false, false)).IsFalse();
        AssertThat(AndroidVertexLitProps.On).IsFalse();
    }

    [TestCase]
    public void OnMarksPlainPropsOnTheStandardAndCulledBuilds()
    {
        foreach (string name in new[]
        {
            "standard_material_lite.gdshader", "standard_material_lite_env.gdshader", "standard_material_lite_nospec.gdshader",
            "standard_material_culled_lite.gdshader", "modern/standard_material_lite.gdshader", "modern/standard_material_culled_lite_env.gdshader",
        })
        {
            AssertThat(AndroidVertexLitProps.Marks(Lite + name, false, false, false, true)).OverrideFailureMessage(name).IsTrue();
        }
    }

    [TestCase]
    public void FoliageGrassAndCharactersKeepPerPixelLight()
    {
        string path = Lite + "standard_material_lite.gdshader";
        AssertThat(AndroidVertexLitProps.Marks(path, true, false, false, true)).IsFalse();
        AssertThat(AndroidVertexLitProps.Marks(path, false, true, false, true)).IsFalse();
        AssertThat(AndroidVertexLitProps.Marks(path, false, false, true, true)).IsFalse();
    }

    [TestCase]
    public void OtherBuildsAreNeverMarked()
    {
        foreach (string path in new[]
        {
            Lite + "standard_material_tree_lite.gdshader", Lite + "modern/standard_material_tree_lite_nospec.gdshader",
            Lite + "standard_material_wind_lite.gdshader", Lite + "standard_material_translucent_lite.gdshader",
            "res://src/Engine/level/terrain/lite/terrain_splat_lite.gdshader",
            "res://src/Engine/material/standard_material.gdshader",
        })
        {
            AssertThat(AndroidVertexLitProps.Marks(path, false, false, false, true)).OverrideFailureMessage(path).IsFalse();
        }
    }

    [TestCase]
    public void OnlyAMarkedSpecialisationTakesTheWord()
    {
        string marked = Engine_ShaderSpecialisation.Compose(Prop, new[] { AndroidVertexLitProps.Define }, false);
        AssertThat(marked).Contains("render_mode blend_mix, depth_draw_opaque, cull_disabled, vertex_lighting;");
        AssertThat(marked).Contains("#define " + AndroidVertexLitProps.Define);

        string plain = Engine_ShaderSpecialisation.Compose(Prop, new[] { "FABLE_SPEC_NO_ORM" }, false);
        AssertThat(plain).NotContains("vertex_lighting");
    }
}
