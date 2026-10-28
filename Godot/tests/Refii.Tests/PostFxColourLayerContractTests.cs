using Godot;
using GdUnit4;
using Refii.Lighting;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>Checks scene-color compute permissions and fallback lifecycles without native Godot objects.</summary>
[TestSuite]
public sealed class PostFxColourLayerContractTests
{
    private const RenderingDevice.DataFormat Hdr = RenderingDevice.DataFormat.R16G16B16A16Sfloat;
    private const RenderingDevice.TextureUsageBits Sampled = RenderingDevice.TextureUsageBits.SamplingBit;
    private const RenderingDevice.TextureUsageBits Storage = RenderingDevice.TextureUsageBits.StorageBit;

    /// <summary>MSAA off/on/off follows rebuilt images and caches the staged route between frames.</summary>
    [TestCase]
    public void MsaaLifecycleCachesStagingAndReturnsToDirect()
    {
        var contract = new Engine_PostFxEffect.ColourLayerContract();
        AssertThat(contract.Inspect(10, Hdr, Storage).Route).IsEqual(Engine_PostFxModel.ColourRoute.Direct);
        var staged = contract.Inspect(11, Hdr, Sampled | RenderingDevice.TextureUsageBits.CanCopyToBit,
            RenderingDevice.TextureUsageBits.CanCopyFromBit, 20);
        AssertThat(staged.Route).IsEqual(Engine_PostFxModel.ColourRoute.Staged);
        AssertThat(staged.Usable).IsTrue();
        AssertThat(staged.ReportRefusal).IsFalse();
        for (int frame = 0; frame < 120; frame++)
        {
            AssertThat(contract.TryGetRoute(11, 20, out var route)).IsTrue();
            AssertThat(route).IsEqual(Engine_PostFxModel.ColourRoute.Staged);
        }
        AssertThat(contract.TryGetRoute(11, 21, out _)).IsFalse();
        AssertThat(contract.TryGetRoute(11, 0, out _)).IsFalse();
        AssertThat(contract.Inspect(12, Hdr, Storage).Route).IsEqual(Engine_PostFxModel.ColourRoute.Direct);
    }

    /// <summary>An absent scene or MSAA source cannot reuse staging permission from an earlier frame.</summary>
    [TestCase]
    public void MissingImagesCannotReuseStaging()
    {
        var contract = new Engine_PostFxEffect.ColourLayerContract();
        var copyTo = RenderingDevice.TextureUsageBits.CanCopyToBit;
        var copyFrom = RenderingDevice.TextureUsageBits.CanCopyFromBit;
        contract.Inspect(1, Hdr, copyTo, copyFrom, 2);
        AssertThat(contract.TryGetRoute(0, 2, out var absent)).IsTrue();
        AssertThat(absent).IsEqual(Engine_PostFxModel.ColourRoute.Unusable);
        AssertThat(contract.Inspect(0, Hdr, Storage, copyFrom, 2).Usable).IsFalse();
        AssertThat(contract.Inspect(1, Hdr, copyTo, copyFrom, 0).Usable).IsFalse();
        AssertThat(contract.TryGetRoute(1, 2, out _)).IsFalse();
    }

    /// <summary>Unsupported Mobile images recover through MSAA then direct storage without repeating fallback logs.</summary>
    [TestCase]
    public void FallbackStagingAndDirectLifecycleBoundsRefusal()
    {
        var contract = new Engine_PostFxEffect.ColourLayerContract();
        AssertThat(contract.Inspect(1, Hdr, Sampled).ReportRefusal).IsTrue();
        var staged = contract.Inspect(2, Hdr, RenderingDevice.TextureUsageBits.CanCopyToBit,
            RenderingDevice.TextureUsageBits.CanCopyFromBit, 3);
        AssertThat(staged.Route).IsEqual(Engine_PostFxModel.ColourRoute.Staged);
        AssertThat(staged.ReportRefusal).IsFalse();
        AssertThat(contract.Inspect(4, Hdr, Storage).Route).IsEqual(Engine_PostFxModel.ColourRoute.Direct);
        var refused = contract.Inspect(5, Hdr, Sampled);
        AssertThat(refused.Route).IsEqual(Engine_PostFxModel.ColourRoute.Unusable);
        AssertThat(refused.ReportRefusal).IsFalse();
    }

    /// <summary>A Mobile RGBA16F color attachment is rejected when it cannot be bound as a storage image.</summary>
    [TestCase]
    public void HdrSamplingAndAttachmentDoNotAuthorizeCompute()
    {
        var contract = new Engine_PostFxEffect.ColourLayerContract();
        var decision = contract.Inspect(1, Hdr, Sampled | RenderingDevice.TextureUsageBits.ColorAttachmentBit);
        AssertThat(decision.Usable).IsFalse();
        AssertThat(decision.ReportRefusal).IsTrue();
    }

    /// <summary>Existing compatible HDR storage images remain accepted, including additional renderer usage flags.</summary>
    [TestCase]
    public void CompatibleHdrStorageKeepsTheComputePath()
    {
        var contract = new Engine_PostFxEffect.ColourLayerContract();
        foreach (var usage in new[] { Storage, Storage | Sampled,
            Storage | Sampled | RenderingDevice.TextureUsageBits.ColorAttachmentBit })
        {
            var decision = contract.Inspect(1, Hdr, usage);
            AssertThat(decision.Usable).IsTrue();
            AssertThat(decision.ReportRefusal).IsFalse();
        }
    }

    /// <summary>Storage eligibility does not relax the compute shader's authored RGBA16F format contract.</summary>
    [TestCase]
    public void OtherStorageFormatsStillRequireFallback()
    {
        foreach (var format in new[] { RenderingDevice.DataFormat.R8G8B8A8Unorm,
            RenderingDevice.DataFormat.R32G32B32A32Sfloat })
        {
            var contract = new Engine_PostFxEffect.ColourLayerContract();
            AssertThat(contract.Inspect(1, format, Storage | Sampled).Usable).IsFalse();
        }
    }

    /// <summary>An absent image takes the fallback without querying invalid native texture handles.</summary>
    [TestCase]
    public void AbsentColourNeedsNoInspectionOrDiagnostic()
    {
        var contract = new Engine_PostFxEffect.ColourLayerContract();
        AssertThat(contract.TryGet(0, out bool usable)).IsTrue();
        AssertThat(usable).IsFalse();
        AssertThat(contract.Inspect(0, Hdr, Storage).ReportRefusal).IsFalse();
        AssertThat(contract.TryGet(1, out _)).IsFalse();
    }

    /// <summary>Repeated fallback frames reuse the rejected image and resizing cannot cause an error-report storm.</summary>
    [TestCase]
    public void FallbackPersistsAcrossFramesAndReportsOnlyOnceAcrossResizes()
    {
        var contract = new Engine_PostFxEffect.ColourLayerContract();
        AssertThat(contract.Inspect(10, Hdr, Sampled).ReportRefusal).IsTrue();
        for (int frame = 0; frame < 120; frame++)
        {
            AssertThat(contract.TryGet(10, out bool usable)).IsTrue();
            AssertThat(usable).IsFalse();
        }
        foreach (ulong resized in new ulong[] { 11, 12, 13 })
        {
            AssertThat(contract.TryGet(resized, out _)).IsFalse();
            var decision = contract.Inspect(resized, Hdr, Sampled);
            AssertThat(decision.Usable).IsFalse();
            AssertThat(decision.ReportRefusal).IsFalse();
        }
    }

    /// <summary>Replacing a rejected buffer with valid storage resumes compute; later refusal remains bounded.</summary>
    [TestCase]
    public void ReplacementRecoversAndLaterFallbackDoesNotRepeatTheDiagnostic()
    {
        var contract = new Engine_PostFxEffect.ColourLayerContract();
        AssertThat(contract.Inspect(1, Hdr, Sampled).Usable).IsFalse();
        AssertThat(contract.TryGet(2, out _)).IsFalse();
        var recovered = contract.Inspect(2, Hdr, Storage | Sampled);
        AssertThat(recovered.Usable).IsTrue();
        AssertThat(recovered.ReportRefusal).IsFalse();
        AssertThat(contract.TryGet(2, out bool usable)).IsTrue();
        AssertThat(usable).IsTrue();
        var refusedAgain = contract.Inspect(3, Hdr, Sampled);
        AssertThat(refusedAgain.Usable).IsFalse();
        AssertThat(refusedAgain.ReportRefusal).IsFalse();
    }
}
