using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using GdUnit4;
using Refii.AndroidRuntime;
using Refii.Models;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>The phone's one-fetch light record: the packed word, and that the shaders get the define exactly when the table packs.</summary>
[TestSuite]
public sealed class AndroidLightTablePackingTests
{
    private static string FileText(string repoPath, [CallerFilePath] string source = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../../..", repoPath))).Replace("\r\n", "\n");

    /// <summary>The ranges come back as the shader's unpackHalf2x16 reads them; no stated range is the range.</summary>
    [TestCase]
    public void PackedRangesRoundTrip()
    {
        AssertThat(AndroidLightTablePacking.Unpack(AndroidLightTablePacking.Pack(12.5f, 7.25f))).IsEqual((12.5f, 7.25f));
        AssertThat(AndroidLightTablePacking.Unpack(AndroidLightTablePacking.Pack(10f, 0f))).IsEqual((10f, 10f));
        AssertThat(AndroidLightTablePacking.Pack(0f, 5f)).IsEqual(0u);

        // Low half the range, high half the stated range.
        uint word = AndroidLightTablePacking.Pack(1f, 2f);
        AssertThat(word & 0xFFFFu).IsEqual((uint)BitConverter.HalfToUInt16Bits((Half)1f));
        AssertThat(word >> 16).IsEqual((uint)BitConverter.HalfToUInt16Bits((Half)2f));

        // Anything else lands on the nearest half.
        (float range, float stated) = AndroidLightTablePacking.Unpack(AndroidLightTablePacking.Pack(23.37f, 19.11f));
        AssertThat(range).IsEqual((float)(Half)23.37f);
        AssertThat(stated).IsEqual((float)(Half)19.11f);
        AssertThat(Math.Abs(range - 23.37f) < 0.01f).IsTrue();
    }

    /// <summary>The word read as a float is always an ordinary number, so nothing on the way to the GPU changes its bits.</summary>
    [TestCase]
    public void PackedWordIsANormalFloat()
    {
        foreach ((float range, float stated) in new[] { (0.0001f, 0f), (1e-9f, 1e-9f), (4f, 3.9f), (60f, 0f), (1e6f, 1e6f), (float.PositiveInfinity, 2f) })
        {
            float view = BitConverter.UInt32BitsToSingle(AndroidLightTablePacking.Pack(range, stated));
            AssertThat(float.IsNormal(view)).OverrideFailureMessage($"{range}, {stated}: {view}").IsTrue();
        }

        AssertThat(AndroidLightTablePacking.Unpack(AndroidLightTablePacking.Pack(1e6f, 0f))).IsEqual((65504f, 65504f));
    }

    /// <summary>A refresh that changes nothing reports nothing; the word is compared as bits.</summary>
    [TestCase]
    public void WriteReportsOnlyChanges()
    {
        float[] data = new float[8];
        AssertThat(AndroidLightTablePacking.Write(data, 4, 1f, 2f, 3f, 10f, 8f)).IsTrue();
        AssertThat(data.Take(4).All(v => v == 0f)).IsTrue();
        AssertThat(data[4] == 1f && data[5] == 2f && data[6] == 3f).IsTrue();
        AssertThat(BitConverter.SingleToUInt32Bits(data[7])).IsEqual(AndroidLightTablePacking.Pack(10f, 8f));
        AssertThat(AndroidLightTablePacking.Write(data, 4, 1f, 2f, 3f, 10f, 8f)).IsFalse();
        AssertThat(AndroidLightTablePacking.Write(data, 4, 1f, 2f, 3f, 10f, 7.5f)).IsTrue();
        AssertThat(AndroidLightTablePacking.Write(data, 4, 1f, 2f, 3.5f, 10f, 7.5f)).IsTrue();
    }

    /// <summary>On by default, needs the lite shaders with the lamp-shadow lookup out, and is never on here.</summary>
    [TestCase]
    public void OnOnlyWithTheLookupCompiledOut()
    {
        AssertThat(AndroidLightTablePacking.Wanted(new Dictionary<string, string>())).IsTrue();
        AssertThat(AndroidLightTablePacking.Wanted(new Dictionary<string, string> { [AndroidLightTablePacking.Key] = "false" })).IsFalse();
        AssertThat(AndroidLightTablePacking.Decide(true, true, true)).IsTrue();
        AssertThat(AndroidLightTablePacking.Decide(false, true, true)).IsFalse();
        AssertThat(AndroidLightTablePacking.Decide(true, false, true)).IsFalse();
        AssertThat(AndroidLightTablePacking.Decide(true, true, false)).IsFalse();
        AssertThat(AndroidLightTablePacking.On).IsFalse();
    }

    /// <summary>A lite build gets the define only beside the lamp-shadow drop, which the shader's guard also requires.</summary>
    [TestCase]
    public void ComposeAddsTheDefineOnlyWithTheShadowDrop()
    {
        const string full = "shader_type spatial;\n#include \"res://src/Engine/material/standard_material_body.gdshaderinc\"\n";
        string path = Engine_ShaderVariant.AllVariantPaths.First();
        string oneRow = "#define " + AndroidLightTablePacking.Define + "\n";
        string noShadow = "#define " + Engine_ShaderVariant.NoStaticShadow + "\n";

        string both = Engine_ShaderVariant.Compose(full, path, false, true, true);
        AssertThat(both.Contains(oneRow, StringComparison.Ordinal)).IsTrue();
        AssertThat(both.Contains(noShadow, StringComparison.Ordinal)).IsTrue();
        AssertThat(both.IndexOf(oneRow, StringComparison.Ordinal) < both.IndexOf("#include", StringComparison.Ordinal)).IsTrue();

        AssertThat(Engine_ShaderVariant.Compose(full, path, false, false, true).Contains(oneRow, StringComparison.Ordinal)).IsFalse();
        AssertThat(Engine_ShaderVariant.Compose(full, path, false, true, false).Contains(oneRow, StringComparison.Ordinal)).IsFalse();
        AssertThat(Engine_ShaderVariant.Compose(full, path, false, true, false).Contains(noShadow, StringComparison.Ordinal)).IsTrue();

        // Off a phone the process's own builds have neither.
        AssertThat(Engine_ShaderVariant.Compose(full, path).Contains(oneRow, StringComparison.Ordinal)).IsFalse();
    }

    /// <summary>The shader reads the row the table writes, behind both defines, and unpacks the word as it is packed.</summary>
    [TestCase]
    public void ShaderReadsThePackedRow()
    {
        // fable_lighting's hook: the phone's one-fetch record in place of upstream's two rows.
        string code = FileText("Godot/src/Engine/material/fable_lighting.gdshaderinc");
        string guard = $"#if defined({AndroidLightTablePacking.Define}) && defined({Engine_ShaderVariant.NoStaticShadow})";
        AssertThat(code.Contains(guard + "\n#include \"res://src/android/fable_lite_light_record.gdshaderinc\"\n#else", StringComparison.Ordinal)).IsTrue();
        string packed = FileText("Godot/src/android/fable_lite_light_record.gdshaderinc");
        AssertThat(packed.Contains($"ivec2(slot, {AndroidLightTablePacking.Row})", StringComparison.Ordinal)).IsTrue();
        AssertThat(packed.Contains("unpackHalf2x16(floatBitsToUint(packed_record.w))", StringComparison.Ordinal)).IsTrue();
        AssertThat(packed.Contains("ivec2(slot, 0)", StringComparison.Ordinal) || packed.Contains("ivec2(slot, 1)", StringComparison.Ordinal)).IsFalse();
    }
}
