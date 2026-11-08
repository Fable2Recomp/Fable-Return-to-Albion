using System.Numerics;
using GdUnit4;
using Refii.Animations;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>The decode cache's size is a mask, so each tier's must be a power of two.</summary>
[TestSuite]
public sealed class AnimDecoderBucketsTests
{
    [TestCase]
    public void TheLowTierKeepsAQuarter()
    {
        AssertThat(Engine_AnimDecoder.BucketsFor(false)).IsEqual(1024);
        AssertThat(Engine_AnimDecoder.BucketsFor(true)).IsEqual(256);
        AssertThat(BitOperations.IsPow2(Engine_AnimDecoder.BucketsFor(false))).IsTrue();
        AssertThat(BitOperations.IsPow2(Engine_AnimDecoder.BucketsFor(true))).IsTrue();
    }
}
