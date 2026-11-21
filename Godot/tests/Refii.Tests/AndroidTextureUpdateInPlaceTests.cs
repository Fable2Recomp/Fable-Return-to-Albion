using GdUnit4;
using Godot;
using Refii.Textures;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>
/// An Android image refresh of unchanged storage updates the texture instead of replacing it: a
/// replace frees the device texture and every material bound to it rebuilds on the render thread,
/// which the sky's 15 Hz in-scatter table turned into about 250 ms a frame.
/// </summary>
[TestSuite]
public sealed class AndroidTextureUpdateInPlaceTests
{
    private const int LutWidth = 64;

    [TestCase]
    public void TheSkyTableRefreshUpdatesInPlace() =>
        AssertThat(AndroidTextureRefresh.UpdatesInPlace(true,
            LutWidth, 1, Image.Format.Rgbaf, false,
            LutWidth, 1, Image.Format.Rgbaf, false)).IsTrue();

    [TestCase]
    public void AnyStorageChangeStillReplaces()
    {
        AssertThat(AndroidTextureRefresh.UpdatesInPlace(true, 64, 1, Image.Format.Rgbaf, false, 128, 1, Image.Format.Rgbaf, false)).IsFalse();
        AssertThat(AndroidTextureRefresh.UpdatesInPlace(true, 64, 1, Image.Format.Rgbaf, false, 64, 2, Image.Format.Rgbaf, false)).IsFalse();
        AssertThat(AndroidTextureRefresh.UpdatesInPlace(true, 64, 1, Image.Format.Rgbaf, false, 64, 1, Image.Format.Rgbah, false)).IsFalse();
        AssertThat(AndroidTextureRefresh.UpdatesInPlace(true, 64, 64, Image.Format.Rgba8, false, 64, 64, Image.Format.Rgba8, true)).IsFalse();
    }

    [TestCase]
    public void AnEmptyTextureIsReplaced() =>
        AssertThat(AndroidTextureRefresh.UpdatesInPlace(true, 0, 0, Image.Format.Rgba8, false, 0, 0, Image.Format.Rgba8, false)).IsFalse();

    /// <summary>A transcoded copy is disposed right after the call, before a queued update would read it.</summary>
    [TestCase]
    public void AConvertedImageIsReplaced() =>
        AssertThat(AndroidTextureRefresh.UpdatesInPlace(false,
            256, 256, Image.Format.Astc4X4, true,
            256, 256, Image.Format.Astc4X4, true)).IsFalse();
}
