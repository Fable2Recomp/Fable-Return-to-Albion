using System;
using GdUnit4;
using Refii.Hero;
using Refii.Models;
using Refii.Textures;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>Exercises actual native-free morph cache observation and invalidation; image pixels/lifetimes require Godot.</summary>
[TestSuite]
public sealed class HeroTextureMorphOwnershipTests
{
    /// <summary>Retirement drops only current texture references, preserves the catalogue, and permits a fresh region decode.</summary>
    [TestCase]
    public void RegionRetirementPreservesCatalogueAndAllowsFreshDecode()
    {
        const string path = "fixture/retired-morph.tex";
        int reads = 0;
        var catalogue = new System.Collections.Generic.List<TextureMorphPair>();
        var pairsField = typeof(Engine_HeroTextureMorph).GetField("_pairs",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        Engine_HeroTextureMorph.SetResolver(_ => null);
        pairsField.SetValue(null, catalogue);
        Engine_TexPathIndex.SetPathSource(() => new[] { path });
        Engine_BnkMeshLoader.SetResolver(_ => { ++reads; return null; });
        try
        {
            uint original = Engine_TexPathIndex.PathHash(path);
            var layers = new[] { new Refii.Host.HostBridge.GuestTextureLayer
                { Texture = original, HasTint = true, Weight = 1, Brightness = 1 } };
            AssertThat(Engine_HeroTextureMorph.Composite(original, layers, null, Engine_HeroTextureMorph.OverlayMap.Diffuse)).IsNull();
            AssertThat(Engine_HeroTextureMorph.CacheMetrics.DecodedEntries).IsEqual(1);
            long built = Engine_HeroTextureMorph.CompositesBuilt;
            Engine_HeroTextureMorph.ReleaseRegionTextures();
            AssertThat(ReferenceEquals(Engine_HeroTextureMorph.Pairs(), catalogue)).IsTrue();
            AssertThat(Engine_HeroTextureMorph.CacheMetrics.DecodedEntries).IsEqual(0);
            AssertThat(Engine_HeroTextureMorph.CacheMetrics.CompositedEntries).IsEqual(0);
            AssertThat(Engine_HeroTextureMorph.CompositesBuilt).IsEqual(built);
            Engine_HeroTextureMorph.ReleaseRegionTextures();
            AssertThat(Engine_HeroTextureMorph.Composite(original, layers, null, Engine_HeroTextureMorph.OverlayMap.Diffuse)).IsNull();
            AssertThat(reads).IsEqual(2);
            AssertThat(Engine_HeroTextureMorph.CacheMetrics.DecodedEntries).IsEqual(1);
        }
        finally
        {
            Engine_HeroTextureMorph.SetResolver(null);
            Engine_TexPathIndex.SetPathSource(null);
            Engine_BnkMeshLoader.SetResolver(null);
        }
    }

    /// <summary>Both composite entry points reject a decode publication when retirement occurs during its resolver call.</summary>
    [TestCase]
    public void InFlightDecodeCannotRepopulateRetiredTextureCaches()
    {
        const string path = "fixture/retired-source.tex";
        const string target = "fixture/retired-target.tex";
        Engine_HeroTextureMorph.Invalidate();
        Engine_TexPathIndex.SetPathSource(() => new[] { path, target });
        int reads = 0;
        Engine_BnkMeshLoader.SetResolver(_ =>
        {
            ++reads;
            Engine_HeroTextureMorph.ReleaseRegionTextures();
            return null;
        });
        try
        {
            uint original = Engine_TexPathIndex.PathHash(path);
            var layers = new[] { new Refii.Host.HostBridge.GuestTextureLayer
                { Texture = original, HasTint = true, Weight = 1, Brightness = 1 } };
            AssertThat(Engine_HeroTextureMorph.Composite(original, layers, null, Engine_HeroTextureMorph.OverlayMap.Diffuse)).IsNull();
            AssertThat(Engine_HeroTextureMorph.CacheMetrics.DecodedEntries).IsEqual(0);
            var active = new System.Collections.Generic.Dictionary<uint,
                System.Collections.Generic.List<TextureMorphEntry>>
            {
                [original] = new() { new() { MorphHash = Engine_TexPathIndex.PathHash(target),
                    Weight = 1, Blend = TextureMorphBlend.Swap } }
            };
            AssertThat(Engine_HeroTextureMorph.Resolve(path, active)).IsNull();
            AssertThat(reads).IsEqual(2);
            AssertThat(Engine_HeroTextureMorph.CacheMetrics.DecodedEntries).IsEqual(0);
            AssertThat(Engine_HeroTextureMorph.CacheMetrics.CompositedEntries).IsEqual(0);
        }
        finally
        {
            Engine_HeroTextureMorph.Invalidate();
            Engine_TexPathIndex.SetPathSource(null);
            Engine_BnkMeshLoader.SetResolver(null);
        }
    }

    /// <summary>Reading metrics is side-effect-free; a failed source is counted once with no invented RGBA or texture bytes.</summary>
    [TestCase]
    public void FailedDecodeCacheAndReadOnlyMetricsHaveAConsistentLifecycle()
    {
        const string path = "fixture/missing-morph.tex";
        int reads = 0;
        Engine_HeroTextureMorph.Invalidate();
        Engine_TexPathIndex.SetPathSource(() => new[] { path });
        Engine_BnkMeshLoader.SetResolver(_ => { ++reads; return null; });
        try
        {
            var before = Engine_HeroTextureMorph.CacheMetrics;
            AssertThat(before.DecodedEntries).IsEqual(0);
            AssertThat(before.DecodedRgbaBytes).IsEqual(0L);
            uint original = Engine_TexPathIndex.PathHash(path);
            var layers = new[] { new Refii.Host.HostBridge.GuestTextureLayer
                { Texture = original, HasTint = true, Weight = 1, Brightness = 1 } };
            AssertThat(Engine_HeroTextureMorph.Composite(original, layers, null, Engine_HeroTextureMorph.OverlayMap.Diffuse)).IsNull();
            var cached = Engine_HeroTextureMorph.CacheMetrics;
            AssertThat(cached.DecodedEntries).IsEqual(1);
            AssertThat(cached.DecodedRgbaBytes).IsEqual(0L);
            AssertThat(cached.CompositedEntries).IsEqual(0);
            AssertThat(cached.CompositesBuilt).IsEqual(before.CompositesBuilt);
            AssertThat(Engine_HeroTextureMorph.CacheMetrics).IsEqual(cached);
            AssertThat(Engine_HeroTextureMorph.Composite(original, layers, null, Engine_HeroTextureMorph.OverlayMap.Diffuse)).IsNull();
            AssertThat(reads).IsEqual(1);
            AssertThat(Engine_HeroTextureMorph.CacheMetrics).IsEqual(cached);
            Engine_HeroTextureMorph.Invalidate();
            AssertThat(Engine_HeroTextureMorph.CacheMetrics.DecodedEntries).IsEqual(0);
            AssertThat(Engine_HeroTextureMorph.CacheMetrics.CompositesBuilt).IsEqual(before.CompositesBuilt);
            AssertThat(reads).IsEqual(1);
        }
        finally
        {
            Engine_HeroTextureMorph.Invalidate();
            Engine_TexPathIndex.SetPathSource(null);
            Engine_BnkMeshLoader.SetResolver(null);
        }
    }

    /// <summary>Malformed data is retained as an empty failed decode; invalidation and resolver reset clear current metrics.</summary>
    [TestCase]
    public void MalformedDecodeAndResolverResetClearCurrentFootprint()
    {
        const string path = "fixture/malformed-morph.tex";
        int reads = 0;
        Engine_HeroTextureMorph.Invalidate();
        Engine_TexPathIndex.SetPathSource(() => new[] { path });
        Engine_BnkMeshLoader.SetResolver(_ => { ++reads; return Array.Empty<byte>(); });
        try
        {
            uint original = Engine_TexPathIndex.PathHash(path);
            var layers = new[] { new Refii.Host.HostBridge.GuestTextureLayer
                { Texture = original, HasTint = true, Weight = 1, Brightness = 1 } };
            AssertThat(Engine_HeroTextureMorph.Composite(original, layers, null, Engine_HeroTextureMorph.OverlayMap.Diffuse)).IsNull();
            var failed = Engine_HeroTextureMorph.CacheMetrics;
            AssertThat(failed.DecodedEntries).IsEqual(1);
            AssertThat(failed.DecodedRgbaBytes).IsEqual(0L);
            AssertThat(failed.CompositedEntries).IsEqual(0);
            Engine_HeroTextureMorph.SetResolver(_ => null);
            var cleared = Engine_HeroTextureMorph.CacheMetrics;
            AssertThat(cleared.DecodedEntries).IsEqual(0);
            AssertThat(cleared.DecodedRgbaBytes).IsEqual(0L);
            AssertThat(cleared.CompositedEntries).IsEqual(0);
            AssertThat(cleared.CompositesBuilt).IsEqual(failed.CompositesBuilt);
            AssertThat(failed.DecodedEntries).IsEqual(1); // Published snapshots remain values after cache mutation.
            AssertThat(reads).IsEqual(1);
        }
        finally
        {
            Engine_HeroTextureMorph.SetResolver(null);
            Engine_TexPathIndex.SetPathSource(null);
            Engine_BnkMeshLoader.SetResolver(null);
        }
    }
}
