using System;
using System.Collections.Generic;
using GdUnit4;
using Refii.Level;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>Verifies build-local terrain texture sharing without constructing native Godot resources.</summary>
[TestSuite]
public sealed class TerrainLightmapDedupTests
{
    /// <summary>Repeated surfaces share one result while distinct complete 64-bit IDs remain independent.</summary>
    [TestCase]
    public void RepeatedSurfacesDecodeOncePerCompleteLightmapId()
    {
        var textures = new Dictionary<ulong, object?>();
        int decodes = 0;
        object? Decode(byte[] _) { ++decodes; return new object(); }
        byte[] blob = { 1, 2, 3 };
        object? first = Engine_LevelScene.GetTerrainLightmapTexture(textures, 7, blob, Decode);
        for (int i = 0; i < 24; ++i)
            AssertThat(ReferenceEquals(first,
                Engine_LevelScene.GetTerrainLightmapTexture(textures, 7, blob, Decode))).IsTrue();
        object? distinct = Engine_LevelScene.GetTerrainLightmapTexture(textures, 0x100000007, blob, Decode);
        AssertThat(ReferenceEquals(first, distinct)).IsFalse();
        AssertThat(decodes).IsEqual(2);
        AssertThat(textures.Count).IsEqual(2);
    }

    /// <summary>Failed decodes stay failed for the current build and can succeed in the next region build.</summary>
    [TestCase]
    public void FailedBakeDoesNotRetryEverySurfaceButANewBuildCanRecover()
    {
        var textures = new Dictionary<ulong, object?>();
        int failures = 0;
        object? Fail(byte[] _) { ++failures; return null; }
        AssertThat(Engine_LevelScene.GetTerrainLightmapTexture(textures, ulong.MaxValue, Array.Empty<byte>(), Fail)).IsNull();
        AssertThat(Engine_LevelScene.GetTerrainLightmapTexture(textures, ulong.MaxValue, Array.Empty<byte>(), Fail)).IsNull();
        AssertThat(failures).IsEqual(1);
        AssertThat(textures.Count).IsEqual(1);
        var nextBuild = new Dictionary<ulong, object?>();
        object recovered = new();
        AssertThat(ReferenceEquals(recovered, Engine_LevelScene.GetTerrainLightmapTexture(
            nextBuild, ulong.MaxValue, new byte[] { 1 }, _ => recovered))).IsTrue();
        AssertThat(textures[ulong.MaxValue]).IsNull();
    }

    /// <summary>A decoder exception cannot publish a partial entry and a subsequent resolution can retry.</summary>
    [TestCase]
    public void DecoderExceptionLeavesNoCacheEntry()
    {
        var textures = new Dictionary<ulong, object?>();
        bool caught = false;
        try
        {
            Engine_LevelScene.GetTerrainLightmapTexture<object>(textures, 0, Array.Empty<byte>(),
                _ => throw new InvalidOperationException("fixture decode fault"));
        }
        catch (InvalidOperationException) { caught = true; }
        AssertThat(caught).IsTrue();
        AssertThat(textures.Count).IsEqual(0);
        object success = new();
        AssertThat(ReferenceEquals(success, Engine_LevelScene.GetTerrainLightmapTexture(
            textures, 0, Array.Empty<byte>(), _ => success))).IsTrue();
    }

    /// <summary>Region and asset-overlay lifecycles do not retain stale data or dispose existing material borrowers.</summary>
    [TestCase]
    public void BuildsResolveChangedAssetsIndependentlyAndBorrowersSurviveCacheRelease()
    {
        var firstBuild = new Dictionary<ulong, DecodedFixture?>();
        var secondBuild = new Dictionary<ulong, DecodedFixture?>();
        byte[] original = { 10 }, overlay = { 20 };
        int decodes = 0;
        DecodedFixture Decode(byte[] bytes) { ++decodes; return new(bytes[0]); }
        var borrowers = new List<DecodedFixture?>();
        for (int i = 0; i < 6; ++i)
            borrowers.Add(Engine_LevelScene.GetTerrainLightmapTexture(firstBuild, 42, original, Decode));
        DecodedFixture? changed = Engine_LevelScene.GetTerrainLightmapTexture(secondBuild, 42, overlay, Decode);
        AssertThat(decodes).IsEqual(2);
        AssertThat(changed!.Value).IsEqual((byte)20);
        firstBuild.Clear();
        foreach (DecodedFixture? borrower in borrowers)
        {
            AssertThat(borrower!.Value).IsEqual((byte)10);
            AssertThat(borrower.Disposed).IsFalse();
            AssertThat(ReferenceEquals(borrowers[0], borrower)).IsTrue();
        }
        secondBuild.Clear();
        AssertThat(changed.Disposed).IsFalse();
    }

    /// <summary>Models a decoded value with observable disposal, without native interop.</summary>
    private sealed class DecodedFixture : IDisposable
    {
        /// <summary>Retains one independently supplied source value.</summary>
        internal DecodedFixture(byte value) => Value = value;
        /// <summary>The immutable source value resolved by this build.</summary>
        internal byte Value { get; }
        /// <summary>Whether an owner explicitly disposed this borrowed result.</summary>
        internal bool Disposed { get; private set; }
        /// <summary>Records disposal without modifying other borrowers.</summary>
        public void Dispose() => Disposed = true;
    }
}
