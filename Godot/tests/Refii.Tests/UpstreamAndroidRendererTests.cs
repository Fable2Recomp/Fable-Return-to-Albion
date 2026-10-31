using System;
using System.Collections;
using System.Reflection;
using GdUnit4;
using Refii.Hero;
using Refii.Level;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>Checks upstream appearance sizing and RG8 terrain against retained Android ownership and support contracts.</summary>
[TestSuite]
public sealed class UpstreamAndroidRendererTests
{
    /// <summary>Optional level previews use explicit paths and never invent a hero recipe when the developer fixture is absent.</summary>
    [TestCase]
    public void OptionalHeroPreviewRecipeHandlesUnsetAndExplicitParts()
    {
        AssertThat(Refii.Launcher.Tools.LevelViewer.ParseHeroPreviewParts("")).IsEqual(Array.Empty<string>());
        AssertThat(Refii.Launcher.Tools.LevelViewer.ParseHeroPreviewParts(" ; fixture/body.mdl; ; fixture/eyes.mdl "))
            .IsEqual(new[] { "fixture/body.mdl", "fixture/eyes.mdl" });
    }

    /// <summary>Builds a managed-only decoded source without invoking native Godot image APIs.</summary>
    private static object Decoded(byte[] rgba)
    {
        Type type = typeof(Engine_HeroTextureMorph).GetNestedType("DecodedTexture", BindingFlags.NonPublic)!;
        object value = Activator.CreateInstance(type, true)!;
        type.GetProperty("Width")!.SetValue(value, 2);
        type.GetProperty("Height")!.SetValue(value, 1);
        type.GetProperty("HasAlpha")!.SetValue(value, true);
        type.GetProperty("Ok")!.SetValue(value, true);
        type.GetProperty("Rgba")!.SetValue(value, rgba);
        return value;
    }

    /// <summary>Calls the actual upstream bilinear sizing operation using native-free decoded values.</summary>
    private static object Fit(object source, int width, int height) =>
        typeof(Engine_HeroTextureMorph).GetMethod("Fit", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new[] { source, (object)width, height })!;

    /// <summary>A matching layer remains a borrow; upsampling filters texel centers and preserves source bytes.</summary>
    [TestCase]
    public void BilinearLayerSizingPreservesCoverageAndBorrowedSources()
    {
        byte[] original = { 0, 0, 0, 32, 255, 255, 255, 224 };
        object source = Decoded(original);
        AssertThat(ReferenceEquals(Fit(source, 2, 1), source)).IsTrue();
        object resized = Fit(source, 4, 1);
        byte[] pixels = (byte[])resized.GetType().GetProperty("Rgba")!.GetValue(resized)!;
        AssertThat(pixels).IsEqual(new byte[] { 0, 0, 0, 32, 64, 64, 64, 80, 191, 191, 191, 176, 255, 255, 255, 224 });
        AssertThat(original).IsEqual(new byte[] { 0, 0, 0, 32, 255, 255, 255, 224 });
    }

    /// <summary>Region retirement clears cache roots without invalidating a caller's resized layer or publishing a stale source.</summary>
    [TestCase]
    public void SizedLayerSurvivesRegionRetirementAndFreshPublication()
    {
        Engine_HeroTextureMorph.Invalidate();
        IDictionary cache = (IDictionary)typeof(Engine_HeroTextureMorph)
            .GetField("DecodedCache", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        object source = Decoded(new byte[] { 0, 0, 0, 32, 255, 255, 255, 224 });
        try
        {
            cache.Add("fixture/layer.tex", source);
            object resized = Fit(source, 4, 1);
            AssertThat(Engine_HeroTextureMorph.CacheMetrics.DecodedRgbaBytes).IsEqual(8L);
            Engine_HeroTextureMorph.ReleaseRegionTextures();
            AssertThat(Engine_HeroTextureMorph.CacheMetrics.DecodedEntries).IsEqual(0);
            AssertThat(((byte[])resized.GetType().GetProperty("Rgba")!.GetValue(resized)!)[7]).IsEqual((byte)80);
            cache.Add("fixture/new-region.tex", Decoded(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }));
            AssertThat(Engine_HeroTextureMorph.CacheMetrics.DecodedEntries).IsEqual(1);
        }
        finally { Engine_HeroTextureMorph.Invalidate(); }
    }

    /// <summary>RG8 retains zero-cliff ground weights, quantized low weights and independent neighboring plane offsets.</summary>
    [TestCase]
    public void TerrainGroundAndCliffChannelsRetainTheSameWeightSupport()
    {
        float[] weights = { 1f, .5f, .001f, .002f, 0f, .25f };
        float[] cliffs = { 0f, 1f, .5f, .25f };
        byte[] first = Engine_TerrainWeightSupport.PackLayer(weights, cliffs, 0, 3);
        byte[] second = Engine_TerrainWeightSupport.PackLayer(weights, cliffs, 3, 3);
        AssertThat(first).IsEqual(new byte[] { 255, 0, 128, 255, 0, 128 });
        AssertThat(second).IsEqual(new byte[] { 1, 64, 0, 255, 64, 255 });
        for (int i = 0; i < weights.Length; i++)
            AssertThat((i < 3 ? first : second)[(i % 3) * 2]).IsEqual(Engine_TerrainWeightSupport.Quantize(weights[i]));
        weights[0] = 0f;
        AssertThat(first[0]).IsEqual((byte)255);
        AssertThat(Engine_TerrainWeightSupport.PackLayer(weights, cliffs, 0, 3)[0]).IsEqual((byte)0);
    }
}
