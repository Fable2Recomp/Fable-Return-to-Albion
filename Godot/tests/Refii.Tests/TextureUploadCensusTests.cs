using System;
using System.Threading.Tasks;
using GdUnit4;
using Godot;
using Refii.Textures;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>Validates scalar-only Android upload evidence without native image constructors.</summary>
[TestSuite]
public sealed class TextureUploadCensusTests
{
    /// <summary>BC expansion includes mip payloads and keeps replacements separate from initial uploads.</summary>
    [TestCase]
    public void UploadLifecycleSeparatesOperationsAndPreservesSnapshots()
    {
        var census = new Engine_TextureUploadCensus();
        AssertThat(census.Snapshot().Length).IsEqual(0);
        census.Record(TextureUploadKind.Create2D, Image.Format.Dxt1, Image.Format.Rgba8, 699008, 5592064, 1024, 1024);
        var first = census.Snapshot();
        census.Record(TextureUploadKind.Create2D, Image.Format.Dxt1, Image.Format.Rgba8, 174752, 1398016, 512, 512);
        census.Record(TextureUploadKind.Replace2D, Image.Format.Dxt1, Image.Format.Rgba8, 699008, 5592064, 1024, 1024);
        census.Record(TextureUploadKind.Layer, Image.Format.RgtcR, Image.Format.R8, 32768, 65536, 256, 256);
        var latest = census.Snapshot();
        AssertThat(first.Length).IsEqual(1);
        AssertThat(first[0].Images).IsEqual(1L);
        AssertThat(latest.Length).IsEqual(3);
        AssertThat(latest[0].Images).IsEqual(2L);
        AssertThat(latest[0].SourceBytes).IsEqual(873760L);
        AssertThat(latest[0].UploadedBytes).IsEqual(6990080L);
        AssertThat(latest[0].LargestUploadBytes).IsEqual(5592064L);
        AssertThat(latest[0].LargestEdge).IsEqual(1024);
        AssertThat(latest[1].Images).IsEqual(1L);
        AssertThat(latest[2].UploadedFormat).IsEqual(Image.Format.R8);
    }

    /// <summary>Concurrent level uploads cannot lose increments or create per-resource buckets.</summary>
    [TestCase]
    public void ParallelUploadsRemainBoundedAndExact()
    {
        var census = new Engine_TextureUploadCensus();
        Parallel.For(0, 4096, _ => census.Record(TextureUploadKind.Create2D,
            Image.Format.Dxt5, Image.Format.Rgba8, 16, 64, 4, 4));
        var rows = census.Snapshot();
        AssertThat(rows.Length).IsEqual(1);
        AssertThat(rows[0].Images).IsEqual(4096L);
        AssertThat(rows[0].UploadedBytes).IsEqual(262144L);
        AssertThat(rows[0].SourceBytes).IsEqual(65536L);
    }

    /// <summary>Malformed metadata is ignored and does not produce unbounded buckets or native calls.</summary>
    [TestCase]
    public void InvalidMetadataCannotPolluteDiagnostics()
    {
        var census = new Engine_TextureUploadCensus();
        AssertThat(census.Record((TextureUploadKind)999, Image.Format.Rgba8, Image.Format.Rgba8, 4, 4, 1, 1)).IsFalse();
        AssertThat(census.Record(TextureUploadKind.Create2D, (Image.Format)999, Image.Format.Rgba8, 4, 4, 1, 1)).IsFalse();
        AssertThat(census.Record(TextureUploadKind.Create2D, Image.Format.Rgba8, (Image.Format)999, 4, 4, 1, 1)).IsFalse();
        AssertThat(census.Record(TextureUploadKind.Create2D, Image.Format.Rgba8, Image.Format.Rgba8, 0, 4, 1, 1)).IsFalse();
        AssertThat(census.Record(TextureUploadKind.Create2D, Image.Format.Rgba8, Image.Format.Rgba8, 4, -4, 1, 1)).IsFalse();
        AssertThat(census.Record(TextureUploadKind.Create2D, Image.Format.Rgba8, Image.Format.Rgba8, 4, 4, 0, 1)).IsFalse();
        AssertThat(census.Snapshot().Length).IsEqual(0);
    }
}
