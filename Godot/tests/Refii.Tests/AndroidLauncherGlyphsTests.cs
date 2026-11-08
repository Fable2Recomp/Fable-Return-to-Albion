using GdUnit4;
using Refii.Launcher;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>The launcher's drawn cross and gear: inked where the shape is, clear where it is not.</summary>
[TestSuite]
public sealed class AndroidLauncherGlyphsTests
{
    private static byte At(byte[] coverage, int size, float u, float v) =>
        coverage[(int)((v + 0.5f) * size) * size + (int)((u + 0.5f) * size)];

    /// <summary>The cross covers its centre and both diagonals, not the middle of an edge or a corner.</summary>
    [TestCase]
    public void CrossInksItsDiagonals()
    {
        const int size = 64;
        byte[] c = AndroidLauncherGlyphs.Coverage(AndroidLauncherGlyphs.Glyph.Cross, size);
        AssertThat(c.Length).IsEqual(size * size);
        AssertThat((int)At(c, size, 0f, 0f)).IsEqual(255);
        AssertThat((int)At(c, size, 0.2f, 0.2f)).IsEqual(255);
        AssertThat((int)At(c, size, -0.2f, 0.2f)).IsEqual(255);
        AssertThat((int)At(c, size, 0f, 0.25f)).IsEqual(0);
        AssertThat((int)At(c, size, 0.48f, 0.48f)).IsEqual(0);
    }

    /// <summary>The gear has a hole, a solid ring, teeth on the axes and gaps between them.</summary>
    [TestCase]
    public void GearHasHoleRingAndTeeth()
    {
        const int size = 64;
        byte[] c = AndroidLauncherGlyphs.Coverage(AndroidLauncherGlyphs.Glyph.Gear, size);
        AssertThat((int)At(c, size, 0f, 0f)).IsEqual(0);
        AssertThat((int)At(c, size, 0.22f, 0f)).IsEqual(255);
        AssertThat((int)At(c, size, 0.4f, 0f)).IsEqual(255);
        // Between two teeth (22.5 degrees off the axis), past the ring.
        AssertThat((int)At(c, size, 0.4f * 0.924f, 0.4f * 0.383f)).IsEqual(0);
        AssertThat((int)At(c, size, 0.49f, 0.49f)).IsEqual(0);
    }
}
