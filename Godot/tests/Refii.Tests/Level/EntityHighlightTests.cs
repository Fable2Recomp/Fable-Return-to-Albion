namespace Refii.Tests.Level;

using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Entities;

[TestSuite]
public class EntityHighlightTests
{
    [TestCase]
    public void TestClampCutoutThresholdTranslucentClampsToHalf()
    {
        // Translucent materials (OpaquePassAlpha = 254.5f / 255.0f ≈ 0.998f) must be clamped
        // to 0.5f so hair and fur card strands are not completely omitted from the highlight.
        AssertThat(Engine_EntityHighlight.ClampCutoutThreshold(254.5f / 255.0f)).IsEqual(0.5f);
        AssertThat(Engine_EntityHighlight.ClampCutoutThreshold(1.0f)).IsEqual(0.5f);
        AssertThat(Engine_EntityHighlight.ClampCutoutThreshold(0.95f)).IsEqual(0.5f);
    }

    [TestCase]
    public void TestClampCutoutThresholdCutoutPreserved()
    {
        // Standard cutout materials below 0.9f retain their authored threshold.
        AssertThat(Engine_EntityHighlight.ClampCutoutThreshold(0.5f)).IsEqual(0.5f);
        AssertThat(Engine_EntityHighlight.ClampCutoutThreshold(0.35f)).IsEqual(0.35f);
        AssertThat(Engine_EntityHighlight.ClampCutoutThreshold(0.1f)).IsEqual(0.1f);
    }

    [TestCase]
    public void TestTryGetAlphaCutoutWithNullMaterialReturnsFalse()
    {
        AssertBool(Engine_EntityHighlight.TryGetAlphaCutout(null, out _, out _)).IsFalse();
    }

    [TestCase]
    public void TestForgetClearsHighlightCachesWithoutError()
    {
        Engine_EntityHighlight.Forget();
        AssertThat(Engine_EntityHighlight.HighlightedThisFrame).IsEqual(0);
    }
}
