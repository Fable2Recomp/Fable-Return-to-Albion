namespace Refii.Tests.Lighting;

using System.Linq;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Core;
using Refii.Models;

/// <summary>
/// The classic renderer's frame registers per title: Fable 2's exactly as its banks bind them
/// (unchanged by the title split), Fable 3's as its 385 shaders' constant tables bind the same
/// names, the light blocks' sub-layouts from g_GlobalLightParamsPS / VS, and the members a title's
/// banks do not declare left absent.
/// </summary>
[TestSuite]
public class ClassicRegistersTests
{
    [TestCase]
    public void Fable2KeepsItsRegisters()
    {
        Engine_ClassicRegisters.Map m = Engine_ClassicRegisters.For(Engine_Title.Fable2);
        AssertThat(m.Ps.TileOffsetSize).IsEqual(0);
        AssertThat(m.Ps.ShadowRangeConstants).IsEqual(12);
        AssertThat(m.Ps.ShadowMapResolution0).IsEqual(13);
        AssertThat(m.Ps.EyePosition).IsEqual(19);
        AssertThat(m.Ps.GlobalAmbientAndBrightness).IsEqual(20);
        AssertThat(m.Ps.LightmapFactorAndGamma).IsEqual(21);
        AssertThat(m.Ps.NormalMapLightAngleConstants).IsEqual(23);
        AssertThat(m.Ps.AtmosphericFactors).IsEqual(28);
        AssertThat(m.Ps.GlobalTextureGammaRamp).IsEqual(30);
        AssertThat(m.Ps.DirectionalLightShadowTransform0).IsEqual(36);
        AssertThat(m.Ps.DirectionalLightShadowTransform1).IsEqual(40);
        AssertThat(m.Ps.AlphaStippleScreenUVFactor).IsEqual(50);
        AssertThat(m.Ps.AtmosphereBetaRayleigh).IsEqual(64);
        AssertThat(m.Ps.MistColour).IsEqual(72);
        AssertThat(m.Ps.LightPositions).IsEqual(80);
        AssertThat(m.Ps.LightColors).IsEqual(87);
        AssertThat(m.Ps.LightSpotParameters).IsEqual(105);
        AssertThat(m.Ps.LightScreenSpaceShadowMask).IsEqual(112);
        AssertThat(m.Ps.LightBlockEnd).IsEqual(116);
        AssertThat(m.Ps.HostInverseViewProjection).IsEqual(224);
        AssertThat(m.Ps.HostSunGlareColour).IsEqual(231);
        AssertThat(m.Vs.EyePosition).IsEqual(9);
        AssertThat(m.Vs.TreeConstants).IsEqual(15);
        AssertThat(m.Vs.CameraPosition).IsEqual(46);
        AssertThat(m.Vs.CameraUpVector).IsEqual(114);
        AssertThat(m.Vs.CameraRightVector).IsEqual(115);
        AssertThat(m.Vs.GlobalAmbientAndBrightness).IsEqual(54);
        AssertThat(m.Vs.LightPositions).IsEqual(76);
        AssertThat(m.Vs.LightBlockEnd).IsEqual(108);
        AssertThat(m.Vs.AtmosphereBetaRayleigh).IsEqual(108);
        AssertThat(m.Vs.HostWindDirection).IsEqual(200);
        AssertThat(m.Vs.HostStarsEye).IsEqual(231);
        AssertThat(m.LightsPerDraw).IsEqual(7);
        AssertThat(m.LightBlockRegisters).IsEqual(36);
        AssertThat(m.Ps.ViewRegisters.SequenceEqual(new[]
        {
            0, 18, 19, 36, 37, 38, 39, 40, 41, 42, 43, 50, 224, 225, 226, 227, 228, 229, 230, 231,
        })).IsTrue();
        AssertThat(m.Vs.ViewRegisters.SequenceEqual(new[]
        {
            9, 46, 114, 115, 224, 225, 226, 227, 228, 229, 230, 231,
        })).IsTrue();
    }

    [TestCase]
    public void Fable2HasNoneOfFable3sRegisters()
    {
        Engine_ClassicRegisters.Map m = Engine_ClassicRegisters.For(Engine_Title.Fable2);
        AssertThat(m.Ps.AOContrast).IsEqual(Engine_ClassicRegisters.Absent);
        AssertThat(m.Ps.OneOverRenderTargetSize).IsEqual(Engine_ClassicRegisters.Absent);
        AssertThat(m.Ps.LightmapFlickeringFactor).IsEqual(Engine_ClassicRegisters.Absent);
        AssertThat(m.Vs.CameraPixelOffset).IsEqual(Engine_ClassicRegisters.Absent);
        AssertThat(m.Vs.ShadowRangeConstants).IsEqual(Engine_ClassicRegisters.Absent);
        AssertThat(m.Vs.LightmapFlickeringFactor).IsEqual(Engine_ClassicRegisters.Absent);
        AssertThat(m.Vs.AOContrast).IsEqual(Engine_ClassicRegisters.Absent);
    }

    [TestCase]
    public void Fable3MovesTheNamesItsBanksMoved()
    {
        Engine_ClassicRegisters.Map m = Engine_ClassicRegisters.For(Engine_Title.Fable3);
        AssertThat(m.Ps.ShadowRangeConstants).IsEqual(12);
        AssertThat(m.Ps.PerspectiveConstants).IsEqual(18);
        AssertThat(m.Ps.EyePosition).IsEqual(19);
        AssertThat(m.Ps.GlobalAmbientAndBrightness).IsEqual(20);
        AssertThat(m.Ps.AmbientNormalMapDarkeningColour).IsEqual(22);
        AssertThat(m.Ps.NormalMapLightAngleConstants).IsEqual(23);
        AssertThat(m.Ps.AtmosphericFactors).IsEqual(29);
        AssertThat(m.Ps.GlobalTextureGammaRamp).IsEqual(31);
        AssertThat(m.Ps.AOContrast).IsEqual(32);
        AssertThat(m.Ps.SpotLightShadowTransform).IsEqual(33);
        AssertThat(m.Ps.DirectionalLightShadowTransform0).IsEqual(37);
        AssertThat(m.Ps.DirectionalLightShadowTransform1).IsEqual(41);
        AssertThat(m.Ps.OneOverRenderTargetSize).IsEqual(47);
        AssertThat(m.Ps.LightmapFlickeringFactor).IsEqual(51);
        AssertThat(m.Ps.AtmosphereBetaRayleigh).IsEqual(64);
        AssertThat(m.Ps.AtmosphereCloseFogColour).IsEqual(68);
        AssertThat(m.Ps.MistColour).IsEqual(72);
        AssertThat(m.Ps.MistHeightScaleAndOffsetDensityFallOff).IsEqual(76);
        AssertThat(m.Vs.EyePosition).IsEqual(9);
        AssertThat(m.Vs.SimulationTime).IsEqual(10);
        AssertThat(m.Vs.LightmapFactorAndGamma).IsEqual(18);
        AssertThat(m.Vs.AmbientLightReferenceDirection).IsEqual(19);
        AssertThat(m.Vs.WindDirections).IsEqual(21);
        AssertThat(m.Vs.GlobalAmbientAndBrightness).IsEqual(27);
        AssertThat(m.Vs.CameraPixelOffset).IsEqual(28);
        AssertThat(m.Vs.AtmosphereBetaRayleigh).IsEqual(38);
        AssertThat(m.Vs.AtmosphereCloseFogColour).IsEqual(42);
        AssertThat(m.Vs.CameraPosition).IsEqual(43);
        AssertThat(m.Vs.CameraUpVector).IsEqual(100);
        AssertThat(m.Vs.CameraRightVector).IsEqual(101);
        AssertThat(m.Vs.ShadowRangeConstants).IsEqual(112);
        AssertThat(m.Vs.LightmapFlickeringFactor).IsEqual(113);
        AssertThat(m.Vs.AOContrast).IsEqual(114);
    }

    [TestCase]
    public void Fable3LeavesAbsentWhatItsBanksNeverDeclare()
    {
        Engine_ClassicRegisters.Map m = Engine_ClassicRegisters.For(Engine_Title.Fable3);
        AssertThat(m.Ps.TileOffsetSize).IsEqual(Engine_ClassicRegisters.Absent);
        AssertThat(m.Ps.ShadowMapResolution0).IsEqual(Engine_ClassicRegisters.Absent);
        AssertThat(m.Ps.SpotShadowMapResolution).IsEqual(Engine_ClassicRegisters.Absent);
        AssertThat(m.Ps.AlphaStippleScreenUVFactor).IsEqual(Engine_ClassicRegisters.Absent);
        // An absent register stays negative under the small offsets the writers add to it.
        AssertThat(m.Ps.ShadowMapResolution0 + 3 < 0).IsTrue();
    }

    [TestCase]
    public void Fable3LightBlockIsEightLightsFromTheConstantTable()
    {
        Engine_ClassicRegisters.Map m = Engine_ClassicRegisters.For(Engine_Title.Fable3);
        // g_GlobalLightParamsPS c80 x40: Positions[8] Colors[8] Type[2] InvRadii[2] SpotDirections[8]
        // SpotParameters[4] PrimaryLightDirection padding[3] ScreenSpaceShadowMask[2] SpotLightShadowMask[2].
        AssertThat(m.Ps.LightPositions).IsEqual(80);
        AssertThat(m.Ps.LightColors).IsEqual(88);
        AssertThat(m.Ps.LightType).IsEqual(96);
        AssertThat(m.Ps.LightInvRadii).IsEqual(98);
        AssertThat(m.Ps.LightSpotDirections).IsEqual(100);
        AssertThat(m.Ps.LightSpotParameters).IsEqual(108);
        AssertThat(m.Ps.LightPrimaryDirection).IsEqual(112);
        AssertThat(m.Ps.LightScreenSpaceShadowMask).IsEqual(116);
        AssertThat(m.Ps.LightSpotShadowMask).IsEqual(118);
        AssertThat(m.Ps.LightBlockEnd).IsEqual(120);
        AssertThat(m.LightsPerDraw).IsEqual(8);
        AssertThat(m.LightBlockRegisters).IsEqual(m.Ps.LightBlockEnd - m.Ps.LightPositions);
        // g_GlobalLightParamsVS c44 x36: the pixel block's layout up to its padding, 36 registers lower.
        AssertThat(m.Vs.LightPositions).IsEqual(44);
        AssertThat(m.Vs.LightBlockEnd).IsEqual(80);
        AssertThat(m.Ps.LightColors - m.Vs.LightColors).IsEqual(36);
        AssertThat(m.Ps.LightType - m.Vs.LightType).IsEqual(36);
        AssertThat(m.Ps.LightInvRadii - m.Vs.LightInvRadii).IsEqual(36);
        AssertThat(m.Ps.LightSpotDirections - m.Vs.LightSpotDirections).IsEqual(36);
        AssertThat(m.Ps.LightSpotParameters - m.Vs.LightSpotParameters).IsEqual(36);
        AssertThat(m.Ps.LightPrimaryDirection - m.Vs.LightPrimaryDirection).IsEqual(36);
    }

    [TestCase]
    public void EveryViewRegisterIsADeclaredRegister()
    {
        foreach (Engine_ClassicRegisters.Map m in new[] { Engine_ClassicRegisters.Fable2, Engine_ClassicRegisters.Fable3 })
        {
            AssertThat(m.Ps.ViewRegisters.All(r => r >= 0 && r < 256)).IsTrue();
            AssertThat(m.Vs.ViewRegisters.All(r => r >= 0 && r < 256)).IsTrue();
        }
        Engine_ClassicRegisters.Map f3 = Engine_ClassicRegisters.Fable3;
        AssertThat(f3.Ps.ViewRegisters.Contains(f3.Ps.OneOverRenderTargetSize)).IsTrue();
        AssertThat(f3.Ps.ViewRegisters.Contains(f3.Ps.DirectionalLightShadowTransform1 + 3)).IsTrue();
        AssertThat(f3.Vs.ViewRegisters.Contains(f3.Vs.CameraPosition)).IsTrue();
    }

    [TestCase]
    public void TheStaticMembersFollowTheActiveTitle()
    {
        Engine_ClassicRegisters.Map m = Engine_ClassicRegisters.For(Engine_Title.Active);
        AssertThat(Engine_ClassicRegisters.Ps.EyePosition).IsEqual(m.Ps.EyePosition);
        AssertThat(Engine_ClassicRegisters.Ps.AtmosphericFactors).IsEqual(m.Ps.AtmosphericFactors);
        AssertThat(Engine_ClassicRegisters.Vs.GlobalAmbientAndBrightness).IsEqual(m.Vs.GlobalAmbientAndBrightness);
        AssertThat(Engine_ClassicRegisters.LightsPerDraw).IsEqual(m.LightsPerDraw);
        AssertThat(Engine_ClassicRegisters.LightBlockRegisters).IsEqual(m.LightBlockRegisters);
    }
}
