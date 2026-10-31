using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using GdUnit4;
using Refii.Textures;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>Verifies device-policy selection and immutable worker use without native Godot bindings.</summary>
[TestSuite]
public sealed class AndroidTextureUploadPolicyTests
{
    /// <summary>Desktop retains all source formats without consulting an Android renderer.</summary>
    [TestCase]
    public void DesktopDoesNotQueryOrChangeImageStorage()
    {
        int queries = 0;
        var policy = AndroidTextureUploadPolicy.Capture(false, _ => { queries++; return false; });
        foreach (Image.Format format in new[] { Image.Format.Dxt1, Image.Format.Dxt3,
            Image.Format.Dxt5, Image.Format.RgtcR, Image.Format.RgtcRg, Image.Format.Rgb8 })
            AssertThat(policy.Target(format)).IsEqual(format);
        AssertThat(queries).IsEqual(0);
    }

    /// <summary>Each unsupported BC family uses precisely the renderer's color/channel fallback.</summary>
    [TestCase]
    public void UnsupportedFormatsMatchGodotFallbackChannels()
    {
        var policy = AndroidTextureUploadPolicy.Capture(true, _ => false);
        AssertThat(policy.Target(Image.Format.Dxt1)).IsEqual(Image.Format.Rgba8);
        AssertThat(policy.Target(Image.Format.Dxt3)).IsEqual(Image.Format.Rgba8);
        AssertThat(policy.Target(Image.Format.Dxt5)).IsEqual(Image.Format.Rgba8);
        AssertThat(policy.Target(Image.Format.RgtcR)).IsEqual(Image.Format.R8);
        AssertThat(policy.Target(Image.Format.RgtcRg)).IsEqual(Image.Format.Rg8);
        AssertThat(policy.Target(Image.Format.Rgb8)).IsEqual(Image.Format.Rgba8);
        foreach (Image.Format format in new[] { Image.Format.R8, Image.Format.Rg8,
            Image.Format.Rgba8, Image.Format.Rf, Image.Format.Rgbah })
            AssertThat(policy.Target(format)).IsEqual(format);
    }

    /// <summary>Partial GPU support preserves compressed families independently of unsupported ones.</summary>
    [TestCase]
    public void SupportedCompressedFormatsArePreservedIndividually()
    {
        var policy = AndroidTextureUploadPolicy.Capture(true, format =>
            format is RenderingDevice.DataFormat.Bc1RgbUnormBlock or
                RenderingDevice.DataFormat.Bc5UnormBlock or RenderingDevice.DataFormat.R8G8B8Unorm);
        AssertThat(policy.Target(Image.Format.Dxt1)).IsEqual(Image.Format.Dxt1);
        AssertThat(policy.Target(Image.Format.RgtcRg)).IsEqual(Image.Format.RgtcRg);
        AssertThat(policy.Target(Image.Format.Rgb8)).IsEqual(Image.Format.Rgb8);
        AssertThat(policy.Target(Image.Format.Dxt5)).IsEqual(Image.Format.Rgba8);
        AssertThat(policy.Target(Image.Format.RgtcR)).IsEqual(Image.Format.R8);
    }

    /// <summary>Worker and layered decisions reuse the captured snapshot without further device calls.</summary>
    [TestCase]
    public void SnapshotSurvivesParallelWorkersAndUniformArraySlices()
    {
        var queried = new HashSet<RenderingDevice.DataFormat>();
        var policy = AndroidTextureUploadPolicy.Capture(true, format => { queried.Add(format); return false; });
        AssertThat(queried.Count).IsEqual(7);
        var targets = new Image.Format[32];
        Parallel.For(0, targets.Length, slice =>
        {
            for (int use = 0; use < 100; use++) targets[slice] = policy.Target(Image.Format.RgtcRg);
        });
        foreach (Image.Format target in targets) AssertThat(target).IsEqual(Image.Format.Rg8);
        AssertThat(queried.Count).IsEqual(7);
    }
}
