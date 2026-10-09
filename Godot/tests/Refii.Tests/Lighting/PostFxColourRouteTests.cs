namespace Refii.Tests.Lighting;

using GdUnit4;
using static GdUnit4.Assertions;
using Godot;
using Refii.Lighting;
using Route = Refii.Lighting.Engine_PostFxModel.ColourRoute;
using Usage = Godot.RenderingDevice.TextureUsageBits;

/// <summary>
/// Verifies how the post-fx chain chooses to reach the scene colour layer
/// (<see cref="Engine_PostFxModel.ChooseColourRoute"/>). The chain binds that layer as a storage
/// image in five passes; a layer without STORAGE made every one of those uniform sets null and
/// crashed the Windows D3D12 build as soon as MSAA was turned on.
/// </summary>
[TestSuite]
public class PostFxColourRouteTests
{
    private const RenderingDevice.DataFormat Rgba16f = RenderingDevice.DataFormat.R16G16B16A16Sfloat;

    /// <summary>Godot 4.7.1 <c>get_color_usage_bits</c>: the bits every colour texture gets.</summary>
    private const Usage ColourBase = Usage.SamplingBit | Usage.ColorAttachmentBit | Usage.InputAttachmentBit;

    /// <summary>The non-MSAA scene layer when the renderer allows storage.</summary>
    private const Usage PlainLayer = ColourBase | Usage.StorageBit;

    /// <summary>The MSAA resolve target as it reached the effect on D3D12: copy-to, no storage.</summary>
    private const Usage ResolveTargetNoStorage = ColourBase | Usage.CanCopyToBit;

    /// <summary>The MSAA resolve target when the renderer allows storage.</summary>
    private const Usage ResolveTargetWithStorage = ResolveTargetNoStorage | Usage.StorageBit;

    /// <summary>The multisampled colour itself: copy-from, never storage.</summary>
    private const Usage MsaaColour = ColourBase | Usage.CanCopyFromBit;

    /// <summary>A storage-capable rgba16f layer is bound directly, with or without MSAA.</summary>
    [TestCase]
    public void StorageLayer_IsDirect()
    {
        AssertThat(Engine_PostFxModel.ChooseColourRoute(Rgba16f, PlainLayer, null)).IsEqual(Route.Direct);
        AssertThat(Engine_PostFxModel.ChooseColourRoute(Rgba16f, ResolveTargetWithStorage, MsaaColour))
            .IsEqual(Route.Direct);
    }

    /// <summary>The crashing case: an MSAA resolve target without STORAGE goes through staging.</summary>
    [TestCase]
    public void MsaaResolveTargetWithoutStorage_IsStaged()
    {
        AssertThat(Engine_PostFxModel.ChooseColourRoute(Rgba16f, ResolveTargetNoStorage, MsaaColour))
            .IsEqual(Route.Staged);
    }

    /// <summary>Any format other than rgba16f is refused, whatever its usage bits.</summary>
    [TestCase]
    public void WrongFormat_IsUnusable()
    {
        AssertThat(Engine_PostFxModel.ChooseColourRoute(
            RenderingDevice.DataFormat.A2B10G10R10UnormPack32, PlainLayer, null)).IsEqual(Route.Unusable);
        AssertThat(Engine_PostFxModel.ChooseColourRoute(
            RenderingDevice.DataFormat.R8G8B8A8Unorm, ResolveTargetNoStorage, MsaaColour)).IsEqual(Route.Unusable);
    }

    /// <summary>Without STORAGE and without MSAA there is nothing to resolve a staging copy from.</summary>
    [TestCase]
    public void NoStorageNoMsaa_IsUnusable()
    {
        AssertThat(Engine_PostFxModel.ChooseColourRoute(Rgba16f, ColourBase | Usage.CanCopyToBit, null))
            .IsEqual(Route.Unusable);
    }

    /// <summary>Staging needs the result to go back: a layer without copy-to is refused.</summary>
    [TestCase]
    public void NoStorageNoCopyTo_IsUnusable()
    {
        AssertThat(Engine_PostFxModel.ChooseColourRoute(Rgba16f, ColourBase, MsaaColour)).IsEqual(Route.Unusable);
    }

    /// <summary>Staging needs to read the MSAA source: one without copy-from is refused.</summary>
    [TestCase]
    public void MsaaWithoutCopyFrom_IsUnusable()
    {
        AssertThat(Engine_PostFxModel.ChooseColourRoute(Rgba16f, ResolveTargetNoStorage, ColourBase))
            .IsEqual(Route.Unusable);
    }

    /// <summary>
    /// The player's settings lifecycle: MSAA off, then 4x, then off again. Each change rebuilds
    /// the scene buffers, and the route must follow the layer it is given each time instead of
    /// staying on the first decision.
    /// </summary>
    [TestCase]
    public void MsaaToggle_RouteFollowsTheBuffers()
    {
        Route off = Engine_PostFxModel.ChooseColourRoute(Rgba16f, PlainLayer, null);
        Route on = Engine_PostFxModel.ChooseColourRoute(Rgba16f, ResolveTargetNoStorage, MsaaColour);
        Route offAgain = Engine_PostFxModel.ChooseColourRoute(Rgba16f, PlainLayer, null);

        AssertThat(off).IsEqual(Route.Direct);
        AssertThat(on).IsEqual(Route.Staged);
        AssertThat(offAgain).IsEqual(Route.Direct);
    }
}
