using System;
using System.Buffers.Binary;
using GdUnit4;
using Refii.Level;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>`foliage_density` leaves large models whole: the radius read from a model header.</summary>
[TestSuite]
public sealed class FoliageRadiusTests
{
    private static byte[] Header(uint version, int at, float radius)
    {
        var b = new byte[48];
        System.Text.Encoding.ASCII.GetBytes("MeshFile").CopyTo(b, 0);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(8, 4), version);
        BinaryPrimitives.WriteSingleBigEndian(b.AsSpan(at, 4), radius);
        return b;
    }

    [TestCase]
    public void ReadsTheRadiusForBothHeaderVersions()
    {
        AssertThat(Engine_LevelProps.RadiusFromHeader(Header(36, 28, 8.39f))).IsEqual(8.39f);
        AssertThat(Engine_LevelProps.RadiusFromHeader(Header(47, 40, 2.3f))).IsEqual(2.3f);
    }

    [TestCase]
    public void UnreadableHeadersGiveZero()
    {
        AssertThat(Engine_LevelProps.RadiusFromHeader(null)).IsEqual(0f);
        AssertThat(Engine_LevelProps.RadiusFromHeader(new byte[10])).IsEqual(0f);
        AssertThat(Engine_LevelProps.RadiusFromHeader(Header(12, 28, 5f))).IsEqual(0f);
    }

    [TestCase]
    public void TheThresholdSitsBetweenClumpsAndLandmarks()
    {
        // From the game's headers: bracken 0.55 m, small bramble 1.32 m are thinned; large bramble 2.3 m and
        // BW_TreeLargeOak_V3 8.39 m are kept.
        AssertThat(1.32f < Engine_LevelProps.ThinnedRadiusMax).IsTrue();
        AssertThat(2.3f > Engine_LevelProps.ThinnedRadiusMax).IsTrue();
    }
}
