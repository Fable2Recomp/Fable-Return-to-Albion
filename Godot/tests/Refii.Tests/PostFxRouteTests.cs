using System;
using System.Text.Json;
using GdUnit4;
using Refii.Lighting;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>
/// The gate of the layers only the post chain composites (Engine_PostFxRoute): shut until a chain
/// has found its colour layer usable, shut again when it refuses it or goes away.
/// </summary>
[TestSuite]
public sealed class PostFxRouteTests
{
    /// <summary>Only a usable route opens the layers; the legacy lever opens them regardless.</summary>
    [TestCase]
    public void OnlyAUsableRouteOpensTheLayers()
    {
        AssertThat(Engine_PostFxRoute.Allows(false, Engine_PostFxRoute.State.Unknown)).IsFalse();
        AssertThat(Engine_PostFxRoute.Allows(false, Engine_PostFxRoute.State.Unusable)).IsFalse();
        AssertThat(Engine_PostFxRoute.Allows(false, Engine_PostFxRoute.State.Usable)).IsTrue();
        foreach (Engine_PostFxRoute.State state in Enum.GetValues<Engine_PostFxRoute.State>())
        {
            AssertThat(Engine_PostFxRoute.Allows(true, state)).IsTrue();
        }
    }

    /// <summary>Direct and staged both run the chain; the Mobile refusal does not.</summary>
    [TestCase]
    public void RoutesMapToTheirStates()
    {
        AssertThat(Engine_PostFxRoute.Of(Engine_PostFxModel.ColourRoute.Direct)).IsEqual(Engine_PostFxRoute.State.Usable);
        AssertThat(Engine_PostFxRoute.Of(Engine_PostFxModel.ColourRoute.Staged)).IsEqual(Engine_PostFxRoute.State.Usable);
        AssertThat(Engine_PostFxRoute.Of(Engine_PostFxModel.ColourRoute.Unusable)).IsEqual(Engine_PostFxRoute.State.Unusable);
    }

    /// <summary>
    /// The Mobile colour layer (10-bit, or half floats without storage, no MSAA) is refused, so the
    /// layers stay shut there; a desktop storage layer opens them.
    /// </summary>
    [TestCase]
    public void TheMobileColourLayerKeepsTheLayersShut()
    {
        var sampled = Godot.RenderingDevice.TextureUsageBits.SamplingBit |
            Godot.RenderingDevice.TextureUsageBits.ColorAttachmentBit;
        var tenBit = Engine_PostFxModel.ChooseColourRoute(
            Godot.RenderingDevice.DataFormat.A2B10G10R10UnormPack32, sampled, null);
        var halfNoStorage = Engine_PostFxModel.ChooseColourRoute(
            Godot.RenderingDevice.DataFormat.R16G16B16A16Sfloat, sampled, null);
        var storage = Engine_PostFxModel.ChooseColourRoute(
            Godot.RenderingDevice.DataFormat.R16G16B16A16Sfloat, sampled | Godot.RenderingDevice.TextureUsageBits.StorageBit, null);
        AssertThat(Engine_PostFxRoute.Allows(false, Engine_PostFxRoute.Of(tenBit))).IsFalse();
        AssertThat(Engine_PostFxRoute.Allows(false, Engine_PostFxRoute.Of(halfNoStorage))).IsFalse();
        AssertThat(Engine_PostFxRoute.Allows(false, Engine_PostFxRoute.Of(storage))).IsTrue();
    }

    /// <summary>
    /// A chain withdrawing only clears its own publication: a level change builds the next chain,
    /// which may publish before the last one is freed.
    /// </summary>
    [TestCase]
    public void WithdrawClearsOnlyTheOwnersRoute()
    {
        object first = new(), second = new();
        bool legacy = Engine_PostFxRoute.LegacyLayers;
        try
        {
            Engine_PostFxRoute.LegacyLayers = false;
            Engine_PostFxRoute.Publish(first, Engine_PostFxModel.ColourRoute.Direct);
            AssertThat(Engine_PostFxRoute.Current).IsEqual(Engine_PostFxRoute.State.Usable);
            AssertThat(Engine_PostFxRoute.LayersReachScreen).IsTrue();

            Engine_PostFxRoute.Publish(second, Engine_PostFxModel.ColourRoute.Unusable);
            AssertThat(Engine_PostFxRoute.LayersReachScreen).IsFalse();
            Engine_PostFxRoute.Withdraw(first);
            AssertThat(Engine_PostFxRoute.Current).IsEqual(Engine_PostFxRoute.State.Unusable);

            Engine_PostFxRoute.LegacyLayers = true;
            AssertThat(Engine_PostFxRoute.LayersReachScreen).IsTrue();
            Engine_PostFxRoute.LegacyLayers = false;

            Engine_PostFxRoute.Withdraw(second);
            AssertThat(Engine_PostFxRoute.Current).IsEqual(Engine_PostFxRoute.State.Unknown);
            AssertThat(Engine_PostFxRoute.LayersReachScreen).IsFalse();
        }
        finally
        {
            Engine_PostFxRoute.Withdraw(first);
            Engine_PostFxRoute.Withdraw(second);
            Engine_PostFxRoute.LegacyLayers = legacy;
        }
    }

    /// <summary>The debug lever takes a boolean or nothing.</summary>
    [TestCase]
    public void TheLeverReadsABoolean()
    {
        using JsonDocument on = JsonDocument.Parse("{\"fxLegacyLayers\": true}");
        using JsonDocument none = JsonDocument.Parse("{}");
        using JsonDocument bad = JsonDocument.Parse("{\"fxLegacyLayers\": 1}");
        AssertThat(Engine_PostFxRoute.ReadRequest(on.RootElement)).IsEqual(true);
        AssertThat(Engine_PostFxRoute.ReadRequest(none.RootElement) is null).IsTrue();
        AssertThrown(() => Engine_PostFxRoute.ReadRequest(bad.RootElement)).IsInstanceOf<ArgumentException>();
    }
}
