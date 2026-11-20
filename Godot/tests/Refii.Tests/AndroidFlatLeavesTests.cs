using System;
using System.IO;
using System.Runtime.CompilerServices;
using GdUnit4;
using Refii.AndroidRuntime;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>
/// `lite_flat_leaves`: the lite tree builds, and only they, take FABLE_LITE_FLAT_LEAVES and `unshaded`
/// when the setting is on; the include they compile has the branch and the sun it reads.
/// </summary>
[TestSuite]
public sealed class AndroidFlatLeavesTests
{
    private const string TreePath = "res://src/Engine/material/lite/standard_material_tree_lite.gdshader";
    private const string TreeNoSpecPath = "res://src/Engine/material/lite/standard_material_tree_lite_nospec.gdshader";
    private const string PropPath = "res://src/Engine/material/lite/standard_material_lite.gdshader";

    /// <summary>The shape of a composed lite tree build.</summary>
    private const string Tree =
        "shader_type spatial;\nrender_mode blend_mix, depth_draw_opaque, cull_disabled;\n" +
        "#define FABLE_LITE\n#define FABLE_TREE_WIND\n#include \"res://src/Engine/material/standard_material_body.gdshaderinc\"\n";

    private static string Source(string relative, [CallerFilePath] string source = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../..", relative))).Replace("\r\n", "\n");

    [TestCase]
    public void OffLeavesEveryBuildAlone()
    {
        AssertThat(AndroidFlatLeaves.Compose(Tree, TreePath, false)).IsEqual(Tree);
        AssertThat(AndroidFlatLeaves.Compose(Tree, PropPath, false)).IsEqual(Tree);
    }

    [TestCase]
    public void OnLeavesOtherBuildsAlone()
    {
        AssertThat(AndroidFlatLeaves.Compose(Tree, PropPath, true)).IsEqual(Tree);
        AssertThat(AndroidFlatLeaves.Compose(Tree, "res://src/Engine/material/standard_material_tree.gdshader", true)).IsEqual(Tree);
    }

    [TestCase]
    public void OnDrawsBothTreeBuildsUnshadedWithTheDefine()
    {
        foreach (string path in new[] { TreePath, TreeNoSpecPath })
        {
            string code = AndroidFlatLeaves.Compose(Tree, path, true);
            AssertThat(code).Contains("render_mode blend_mix, depth_draw_opaque, cull_disabled, unshaded;\n");
            AssertThat(code).Contains("#define " + AndroidFlatLeaves.Define + "\n");
            // The define comes before the body that reads it.
            AssertThat(code.IndexOf(AndroidFlatLeaves.Define, StringComparison.Ordinal))
                .IsLess(code.IndexOf("#include", StringComparison.Ordinal));
            AssertThat(code).Contains("#define FABLE_LITE\n#define FABLE_TREE_WIND\n");
        }
    }

    [TestCase]
    public void OnTwiceChangesNothingMore()
    {
        string once = AndroidFlatLeaves.Compose(Tree, TreePath, true);
        AssertThat(AndroidFlatLeaves.Compose(once, TreePath, true)).IsEqual(once);
    }

    [TestCase]
    public void TheLiteIncludesCarryTheBranchAndTheSun()
    {
        string end = Source("src/android/standard_lite_fragment_end.gdshaderinc");
        AssertThat(end).Contains("#ifdef FABLE_LITE_FLAT_LEAVES\n");
        AssertThat(end).Contains("ALBEDO = ALBEDO * (fable_sun_colour * (0.5 * fable_direct_ao_for_light(v_combined_ao, true))) + EMISSION;");
        AssertThat(end).Contains("EMISSION = vec3(0.0);");
        string varyings = Source("src/android/standard_lite_varyings.gdshaderinc");
        AssertThat(varyings).Contains("#ifdef FABLE_LITE_FLAT_LEAVES\n");
        AssertThat(varyings).Contains("global uniform vec3 fable_sun_colour;");
        // Off Android the choice is never on.
        AssertThat(AndroidFlatLeaves.On).IsFalse();
    }
}
