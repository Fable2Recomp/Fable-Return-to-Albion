using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using GdUnit4;
using Refii.Models;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>The mobile lite shader set: its policy, and that every build matches the shader it stands for.</summary>
[TestSuite]
public sealed class ShaderVariantTests
{
    private static string FileText(string resPath, [CallerFilePath] string source = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(source)!, "../..", resPath["res://".Length..]))).Replace("\r\n", "\n");

    /// <summary>A shader's text: the file, or a lite build made from its full shader's file.</summary>
    private static string Source(string resPath) => Engine_ShaderVariant.IsBuild(resPath)
        ? Engine_ShaderVariant.Compose(FileText(Engine_ShaderVariant.FullPathOf(resPath)), resPath)
        : FileText(resPath);

    private static string Line(string code, string prefix) =>
        code.Split('\n').FirstOrDefault(l => l.StartsWith(prefix, StringComparison.Ordinal)) ?? string.Empty;

    /// <summary>Only the two lite spellings select it; everything else is the full set.</summary>
    [TestCase]
    public void ParsesTheKey()
    {
        AssertThat(Engine_ShaderVariant.Parse("lite")).IsEqual(Engine_ShaderVariant.Mode.Lite);
        AssertThat(Engine_ShaderVariant.Parse("\"LITE_FLAT\"")).IsEqual(Engine_ShaderVariant.Mode.LiteFlat);
        AssertThat(Engine_ShaderVariant.Parse("full")).IsEqual(Engine_ShaderVariant.Mode.Full);
        AssertThat(Engine_ShaderVariant.Parse("fast")).IsEqual(Engine_ShaderVariant.Mode.Full);
    }

    /// <summary>Lite is refused off Android and wherever the code it drops would be visible; the
    /// lighting model plays no part in the decision.</summary>
    [TestCase]
    public void RefusesWhereTheDroppedCodeShows()
    {
        var lite = Engine_ShaderVariant.Mode.Lite;
        AssertThat(Engine_ShaderVariant.Decide(lite, true, false, false, out string why)).IsEqual(lite);
        AssertThat(why).IsEmpty();
        AssertThat(Engine_ShaderVariant.Decide(lite, false, false, false, out _)).IsEqual(Engine_ShaderVariant.Mode.Full);
        AssertThat(Engine_ShaderVariant.Decide(lite, true, true, false, out _)).IsEqual(Engine_ShaderVariant.Mode.Full);
        AssertThat(Engine_ShaderVariant.Decide(lite, true, false, true, out _)).IsEqual(Engine_ShaderVariant.Mode.Full);
    }

    /// <summary>A render mode is fixed per file: each lite build must state its source's exactly,
    /// and compile the body that source includes.</summary>
    [TestCase]
    public void EveryBuildMatchesItsSource()
    {
        int checkedCount = 0;
        foreach (string lite in Engine_ShaderVariant.AllVariantPaths)
        {
            string full = Engine_ShaderVariant.FullPathOf(lite);
            AssertThat(full).IsNotEqual(lite);
            string liteCode = Source(lite);
            string fullCode = Source(full);
            AssertThat(Line(liteCode, "render_mode")).IsEqual(Line(fullCode, "render_mode"));
            AssertThat(Line(liteCode, "#include")).IsEqual(Line(fullCode, "#include"));
            foreach (string define in new[] { "#define FABLE_SOLID", "#define FABLE_TRANSLUCENT", "#define FABLE_TREE_WIND", "#define FABLE_WIND" })
            {
                AssertThat(liteCode.Contains(define, StringComparison.Ordinal)).IsEqual(fullCode.Contains(define, StringComparison.Ordinal));
            }

            AssertThat(liteCode.Contains("#define FABLE_LITE", StringComparison.Ordinal) ||
                liteCode.Contains("#define FABLE_TERRAIN_PAGE", StringComparison.Ordinal)).IsTrue();

            // The bake drop belongs to the modern set alone: under the fable model it is the light.
            AssertThat(liteCode.Contains("#define FABLE_LITE_MODERN", StringComparison.Ordinal))
                .IsEqual(lite.Contains("/lite/modern/", StringComparison.Ordinal));
            checkedCount++;
        }

        AssertThat(checkedCount).IsEqual(36);
    }
}
