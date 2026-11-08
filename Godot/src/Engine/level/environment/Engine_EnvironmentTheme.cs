using System;
using System.Collections.Generic;
using Refii.Gdb;

namespace Refii.Level;

public sealed class SkyTheme
{
    public bool HasAny { get; set; }
    public bool HasSkyColour { get; set; }
    public bool HasComplementaryColour { get; set; }
    public bool HasSunsetColour { get; set; }
    public bool HasFogColour { get; set; }
    public bool HasSunIntensity { get; set; }
    public bool HasRayleigh { get; set; }
    public bool HasMie { get; set; }
    public bool HasComplementaryBias { get; set; }
    public bool HasNearFog { get; set; }
    public bool HasFarFog { get; set; }
    public bool HasNearDistance { get; set; }
    public bool HasNearDensity { get; set; }
    public bool HasFarDistance { get; set; }
    public bool HasFarDensity { get; set; }
    public bool HasCloseFogMaxDistance { get; set; }
    public bool HasFoggingStart { get; set; }
    public bool HasSunAxis { get; set; }
    public bool HasMoonAxis { get; set; }
    public bool HasMoonParams { get; set; }
    public bool HasStarBrightness { get; set; }
    public bool HasMainLightColour { get; set; }
    public bool HasSkyOverlayTexture { get; set; }
    public bool HasMoonTexture { get; set; }
    public bool HasMoonGlareTexture { get; set; }
    public bool HasSunDiscTexture { get; set; }
    public bool HasSunBeamsTexture { get; set; }
    public bool HasSunGlareTexture { get; set; }
    public bool HasSunDiscParams { get; set; }
    public bool HasSunDiscColour { get; set; }
    public bool HasSunBeams { get; set; }
    public bool HasSunGlare { get; set; }

    public float[] SkyColour { get; } = { 0.42f, 0.56f, 0.76f };
    public float[] ComplementaryColour { get; } = { 0.72f, 0.64f, 0.54f };
    public float[] SunsetColour { get; } = { 1.0f, 0.47f, 0.22f };
    public float[] FogColour { get; } = { 0.48f, 0.55f, 0.62f };
    public float[] MainLightColour { get; } = { 1.0f, 1.0f, 1.0f };
    public float[] SunDiscColour { get; } = { 1.0f, 1.0f, 1.0f };

    public float SunIntensity { get; set; } = 1.0f;
    public float Rayleigh { get; set; } = 1.0f;
    public float Mie { get; set; } = 1.0f;
    public float ComplementaryBias { get; set; } = 0.35f;
    public float NearDistance { get; set; } = 10f;
    public float NearDensity { get; set; } = 0.3f;
    public float FarDistance { get; set; } = 100f;
    public float FarDensity { get; set; } = 0.3f;
    public float CloseFogMaxDistance { get; set; } = 100f;
    public float FoggingStart { get; set; } = 1000000f;
    public float SunAxisElevation { get; set; } = 26.0f;
    public float SunAxisZOffset { get; set; }
    public float SunAxisXyRotation { get; set; }
    public float MainLightTimeFactor { get; set; } = 1.0f;

    /// <summary>
    /// The main light's elevation at 0, 6, 12 and 18 hours, IN DEGREES.
    ///
    /// The sun's height is authored as four keyframes and cosine-blended between them; it is
    /// not derived from a single axis tilt. `sub_821DF670` reads them at theme+864/880/896/912
    /// and converts with 0.017453292, which is what proves the unit.
    /// </summary>
    /// <remarks>
    /// THE DEFAULTS ARE THE GUEST'S OWN AND THEY ARE NOT ALL THE SAME. <c>sub_82A45DD0</c> states
    /// all four — 50, 20, 70, 20 — at 0x82A46554/64/74/80, and marks them stated, so the guest
    /// always has a real arc: low at midnight, lower at dawn, high at noon, low again at dusk.
    ///
    /// Four copies of 26 was a host invention standing in for values nobody had read, and it makes
    /// the sun hold one elevation all day — no arc at all, which is the one shape a sun never has.
    /// </remarks>
    public float[] MainLightElevation { get; } = { 50.0f, 20.0f, 70.0f, 20.0f };

    public bool HasMainLightElevation { get; set; }

    /// <summary>The light rig's Z rotation, in degrees. The ONLY azimuth offset the main
    /// light gets — SunAxisXYRotationOffset belongs to a different vector.</summary>
    public float LightRigXyRotation { get; set; }
    public float MoonIntensity { get; set; } = 1.0f;
    public float MoonSize { get; set; } = 1.0f;
    public float MoonGlareIntensity { get; set; }
    public float MoonGlareSize { get; set; } = 1.0f;
    public float MoonTransparency { get; set; } = 1.0f;
    public float MoonAxisElevation { get; set; } = 26.0f;
    public float MoonAxisZOffset { get; set; }
    public float MoonAxisXyRotation { get; set; }
    public float StarBrightness { get; set; }
    public float SunDiscSize { get; set; } = 1.0f;
    public float SunDiscIntensity { get; set; } = 1.0f;
    public float SunBeamsWidth { get; set; } = 1.0f;
    public float SunBeamsHeight { get; set; } = 1.0f;
    public float SunBeamsIntensity { get; set; }
    public float SunGlareIntensity { get; set; }
    public float SunGlareSize { get; set; } = 1.0f;
    public float SourceTimeOfDay { get; set; } = -1.0f;

    public uint SkyOverlayTextureHash { get; set; }
    public uint MoonTextureHash { get; set; }
    public uint MoonGlareTextureHash { get; set; }
    public uint SunDiscTextureHash { get; set; }
    public uint SunBeamsTextureHash { get; set; }
    public uint SunGlareTextureHash { get; set; }

    /// <summary>
    /// Bumped each time a kept instance is blended over in place
    /// (<see cref="Engine_ThemeTimeline.BlendSkyInto"/>). With the reference, what says two reads
    /// saw the same theme: a cache keyed on the reference alone would take a rewritten one for
    /// the one it saw.
    /// </summary>
    public int Revision { get; set; }
}

public sealed class WeatherTheme
{
    public bool HasAny { get; set; }
    public bool HasRain { get; set; }
    public bool HasSnow { get; set; }
    public bool HasWind { get; set; }
    public bool HasGroundMist { get; set; }

    public float RainDensity { get; set; }
    public float RainSize { get; set; }
    public float SnowFallSpeed { get; set; }
    public float SnowSize { get; set; }

    public float WindStrengthMin { get; set; }
    public float WindStrengthMax { get; set; }
    public float WindStrengthVariation { get; set; }
    public float WindXyRotationMin { get; set; }
    public float WindXyRotationMax { get; set; }
    public float WindElevationMin { get; set; }
    public float WindElevationMax { get; set; }
    public float WindChangeFrequency { get; set; }
    public float WindChangeDuration { get; set; }
    public float WindDirectionVariation { get; set; }

    public float GroundMistStrength { get; set; }
    public float SourceTimeOfDay { get; set; } = -1.0f;
}

public sealed class CloudLayerTheme
{
    public bool Enabled { get; set; }
    public bool HasDensityMap { get; set; }
    public bool HasPosition { get; set; }
    public bool HasSize { get; set; }
    public bool HasTextureScale { get; set; }
    public bool HasVelocity { get; set; }
    public bool HasHeight { get; set; }
    public bool HasTransparency { get; set; }
    public bool HasBrightness { get; set; }
    public bool HasAmbient { get; set; }
    public bool HasNormalStrength { get; set; }
    public bool HasTranslucencyStrength { get; set; }

    public uint DensityMapHash { get; set; }
    public float PositionX { get; set; }
    public float PositionY { get; set; }
    public float SizeX { get; set; }
    public float SizeY { get; set; }
    public float TextureScaleX { get; set; } = 0.001f;
    public float TextureScaleY { get; set; } = 0.001f;
    public float VelocityX { get; set; }
    public float VelocityY { get; set; }
    public float Height { get; set; }
    public float Transparency { get; set; }
    public float Brightness { get; set; }
    public float AmbientLight { get; set; }
    public float NormalStrength { get; set; }
    public float TranslucencyStrength { get; set; }

    public CloudLayerTheme Clone() => (CloudLayerTheme)MemberwiseClone();
}

public sealed class CloudTheme
{
    public bool HasAny { get; set; }

    public int LayerCount { get; set; }

    public CloudLayerTheme[] Layers { get; } =
    {
        new(), new(), new(), new(),
    };

    public float SourceTimeOfDay { get; set; } = -1.0f;
}

public static class EnvHash
{
    public const uint Null = 0x811C9DC5;
    public const uint Clouds = 0x7439046F;
    public const uint Layer1 = 0x6A570941;
    public const uint Layer2 = 0x6A570942;
    public const uint Layer3 = 0x6A570943;
    public const uint Layer4 = 0x6A570944;
    public const uint DensityMap = 0x13821B7F;
    public const uint PositionX = 0x1E72B2E4;
    public const uint PositionY = 0x1E72B2E5;
    public const uint SizeX = 0x9C014CCE;
    public const uint SizeY = 0x9C014CCF;
    public const uint TextureScaleX = 0x0A8BA024;
    public const uint TextureScaleY = 0x0A8BA025;
    public const uint VelocityX = 0x5CE30740;
    public const uint VelocityY = 0x5CE30741;
    public const uint Height = 0xF47DB020;
    public const uint Transparency = 0x383FDB33;
    public const uint NormalStrength = 0xB5B0AE93;
    public const uint TranslucencyStrength = 0x114E67B1;
    public const uint Brightness = 0xC452018C;
    public const uint AmbientLight = 0x15DD1091;
    public const uint LevelData = 0x123F8AFD;
    public const uint EnvThemeGlobal = 0xAE17B958;
    public const uint EnvironmentThemeDaySet = 0x0843AB41;
    public const uint Theme = 0xB57E3290;
    public const uint TimeOfDay = 0x9723C2C9;
    public const uint Sky = 0x2420BFA4;
    public const uint Lighting = 0x0B152C5D;
    public const uint Fogging = 0xDDF56C9A;
    public const uint Wind = 0xBE284853;
    public const uint GroundMist = 0x65AA790F;
    public const uint SunRecord = 0x2620C36D;
    public const uint ToneMappingRecord = 0xE35DE525;
    public const uint DepthOfFieldRecord = 0x745C80F1;

    public const uint Red = 0x3A232172;
    public const uint Green = 0x608C9792;
    public const uint Blue = 0xB1911CC9;
    public const uint Factor = 0xBF21DA70;

    public const uint SunIntensity = 0xC868C0DC;
    public const uint SkyBetaRayleighMultiplier = 0x59837340;
    public const uint SkyBetaMieMultiplier = 0xD7FC122C;
    public const uint SkyColour = 0xD78A6E40;
    public const uint SkyComplementaryColour = 0x5CBE1462;
    public const uint SkyComplementaryColourBias = 0x2DA0C989;
    public const uint SunsetColour = 0x897262B7;
    public const uint SkyOverlayTexture = 0xF8C0DD95;
    public const uint MoonTexture = 0x20D88F43;
    public const uint MoonGlareTexture = 0xF2C7518C;
    public const uint DiscTexture = 0x6B29E8A3;

    public const uint SkyColourRed = 0x86B2D6AD;
    public const uint SkyColourGreen = 0x0E4C7541;
    public const uint SkyColourBlue = 0xFDCC27B4;
    public const uint SkyColourFactor = 0x1273DB31;
    public const uint SkyComplementaryColourRed = 0x507FFA3F;
    public const uint SkyComplementaryColourGreen = 0xDA12836B;
    public const uint SkyComplementaryColourBlue = 0x0593BF6A;
    public const uint SkyComplementaryColourFactor = 0xD433126B;
    public const uint SunsetColourRed = 0x896E8E84;
    public const uint SunsetColourGreen = 0x8FC37AA4;
    public const uint SunsetColourBlue = 0xCFED8B8F;
    public const uint SunsetColourFactor = 0xB2447252;
    public const uint CloseFogColour = 0x66353755;
    public const uint CloseFogColourRed = 0x2229E682;
    public const uint CloseFogColourGreen = 0x4D9D5A22;
    public const uint CloseFogColourBlue = 0x876515F9;
    public const uint CloseFogColourFactor = 0x010020C0;
    public const uint MainLightColour = 0x40A12D92;
    public const uint MainLightColourRed = 0x9F76036F;
    public const uint MainLightColourGreen = 0xE3D88F9B;
    public const uint MainLightColourBlue = 0x3E7D387A;
    public const uint MainLightColourFactor = 0xE67DD6DB;

    public const uint SunAxisElevation = 0x2682515B;
    public const uint SunAxisZOffset = 0x2EF474B9;
    public const uint SunAxisXyRotationOffset = 0x2E4D729C;
    public const uint TimeFactor = 0x703AEFF3;

    // Lighting group. FNV-1 of the property names the guest registers at 0x83289A7C onward.
    public const uint MainLightElevation0 = 0xFFC79021;
    public const uint MainLightElevation6 = 0xFFC79027;
    public const uint MainLightElevation12 = 0xC727E252;
    public const uint MainLightElevation18 = 0xC727E258;
    public const uint LightRigXyRotation = 0x3E2D0280;

    public const uint NearDistance = 0xDA3F7AAA;
    public const uint NearDensity = 0x91F00AC5;
    public const uint FarDistance = 0xFF154645;
    public const uint FarDensity = 0xE1DF20B4;
    public const uint CloseFogMaxDistance = 0xCC4BDF70;
    public const uint FoggingStart = 0x754D898A;

    public const uint MoonIntensity = 0x8DA685FD;
    public const uint MoonSize = 0x771E440D;
    public const uint MoonGlareIntensity = 0x326BAB3E;
    public const uint MoonGlareSize = 0x593E77C4;
    public const uint MoonTransparency = 0xAF2DDBD4;
    public const uint MoonAxisElevation = 0xF32010F6;
    public const uint MoonAxisZOffset = 0x425F14D0;
    public const uint MoonAxisXyRotationOffset = 0x695E86AB;
    public const uint StarBrightness = 0xE52870D8;

    public const uint DiscSize = 0x9C3FDEED;
    public const uint DiscIntensity = 0x7269BF5D;
    public const uint DiscColour = 0x40EBF67E;
    public const uint DiscColourRed = 0x33E4BA23;
    public const uint DiscColourGreen = 0x36D9C5CF;
    public const uint DiscColourBlue = 0xBC29377E;
    public const uint DiscColourFactor = 0x4BFBB0BF;
    public const uint SunBeamsWidth = 0x23777251;
    public const uint SunBeamsHeight = 0x7FA6150A;
    public const uint SunBeamsIntensity = 0x057D3A8A;
    public const uint SunBeamsTexture = 0xC5C32228;
    public const uint GlareIntensity = 0x7ADC8EE5;
    public const uint GlareSize = 0x57AF2EF5;
    public const uint GlareTexture = 0x495DCD2B;

    public const uint Strength = 0x6CC36A2E;
    public const uint RainDensity = 0x0043FE91;
    public const uint RainSize = 0xF2494D2C;
    public const uint SnowFallSpeed = 0x1C33AED8;
    public const uint SnowSize = 0xC5487977;
    public const uint WindStrengthMin = 0x467F5320;
    public const uint WindStrengthMax = 0x3E7F46CE;
    public const uint WindStrengthVariation = 0xEC5CF413;
    public const uint WindXyRotationMin = 0x8724E1D8;
    public const uint WindXyRotationMax = 0x8F24EE36;
    public const uint WindElevationMin = 0xD6C55ADC;
    public const uint WindElevationMax = 0xDEC56732;
    public const uint WindChangeFrequency = 0xA1C12827;
    public const uint WindChangeDuration = 0x5B45F873;
    public const uint WindDirectionVariation = 0x5B71ED9D;
}

public static class Engine_EnvironmentTheme
{
    private readonly record struct RecordRef(int Db, int Index, bool Valid);

    public static bool TryExtractSky(IReadOnlyList<byte[]> gdbs, out SkyTheme theme) =>
        TryExtractSky(gdbs, 0.5f, out theme);

    public static bool TryExtractSky(
        IReadOnlyList<byte[]> gdbs, float targetTime, out SkyTheme theme)
    {
        theme = new SkyTheme();
        var extractor = Extractor.Create(gdbs);
        if (extractor is null)
        {
            return false;
        }

        extractor.TargetTime = targetTime;
        return extractor.ExtractSky(theme);
    }

        /// <summary>
        /// The sky/fog theme of ONE day set, by its record hash — a painted layer's.
        /// Takes only the fields the record actually OVERRIDES, not those it inherits, so folding
        /// it over the level's own theme leaves unstated fields intact (<c>sub_82A44B88</c>).
        /// </summary>
        public static bool TryExtractSky(
            IReadOnlyList<byte[]> gdbs, float targetTime, uint daySetHash, out SkyTheme theme)
        {
            theme = new SkyTheme();
            var extractor = Extractor.Create(gdbs);
            if (extractor is null)
            {
                return false;
            }

            extractor.TargetTime = float.IsFinite(targetTime) && targetTime >= 0f ? targetTime : 0.5f;
            return extractor.ExtractSkyPainted(daySetHash, theme);
        }

        /// <summary>The keyframe times of one day set for sky/fog extraction, sorted.</summary>
        public static List<float> DaySetTimes(IReadOnlyList<byte[]> gdbs, uint daySetHash) =>
            Refii.Lighting.Engine_LightingTheme.DaySetTimes(gdbs, daySetHash);

        /// <summary>
        /// Folds a painted day set's <see cref="SkyTheme"/> over the running theme by its layer
        /// weight, the way <c>sub_82197938</c> lerps each layer over the accumulated result.
        /// Fields the painted theme does not state keep the base.
        /// </summary>
        public static SkyTheme FoldPaintedSky(SkyTheme into, SkyTheme painted, float weight)
        {
            float w = Math.Clamp(weight, 0f, 1f);
            if (w <= 0f || !painted.HasAny)
            {
                return into;
            }

            SkyTheme near = w < 0.5f ? into : painted;
            var o = new SkyTheme
            {
                HasAny = into.HasAny || painted.HasAny,
                HasSkyColour = into.HasSkyColour || painted.HasSkyColour,
                HasComplementaryColour = into.HasComplementaryColour || painted.HasComplementaryColour,
                HasSunsetColour = into.HasSunsetColour || painted.HasSunsetColour,
                HasFogColour = into.HasFogColour || painted.HasFogColour,
                HasSunIntensity = into.HasSunIntensity || painted.HasSunIntensity,
                HasRayleigh = into.HasRayleigh || painted.HasRayleigh,
                HasMie = into.HasMie || painted.HasMie,
                HasComplementaryBias = into.HasComplementaryBias || painted.HasComplementaryBias,
                HasNearFog = into.HasNearFog || painted.HasNearFog,
                HasFarFog = into.HasFarFog || painted.HasFarFog,
                HasNearDistance = into.HasNearDistance || painted.HasNearDistance,
                HasNearDensity = into.HasNearDensity || painted.HasNearDensity,
                HasFarDistance = into.HasFarDistance || painted.HasFarDistance,
                HasFarDensity = into.HasFarDensity || painted.HasFarDensity,
                HasCloseFogMaxDistance = into.HasCloseFogMaxDistance || painted.HasCloseFogMaxDistance,
                HasFoggingStart = into.HasFoggingStart || painted.HasFoggingStart,
                HasSunAxis = into.HasSunAxis || painted.HasSunAxis,
                HasMoonAxis = into.HasMoonAxis || painted.HasMoonAxis,
                HasMoonParams = into.HasMoonParams || painted.HasMoonParams,
                HasStarBrightness = into.HasStarBrightness || painted.HasStarBrightness,
                HasMainLightColour = into.HasMainLightColour || painted.HasMainLightColour,
                HasSkyOverlayTexture = painted.HasSkyOverlayTexture && w >= 0.5f ? true : into.HasSkyOverlayTexture,
                HasMoonTexture = painted.HasMoonTexture && w >= 0.5f ? true : into.HasMoonTexture,
                HasMoonGlareTexture = painted.HasMoonGlareTexture && w >= 0.5f ? true : into.HasMoonGlareTexture,
                HasSunDiscTexture = painted.HasSunDiscTexture && w >= 0.5f ? true : into.HasSunDiscTexture,
                HasSunBeamsTexture = painted.HasSunBeamsTexture && w >= 0.5f ? true : into.HasSunBeamsTexture,
                HasSunGlareTexture = painted.HasSunGlareTexture && w >= 0.5f ? true : into.HasSunGlareTexture,
                HasSunDiscParams = into.HasSunDiscParams || painted.HasSunDiscParams,
                HasSunDiscColour = into.HasSunDiscColour || painted.HasSunDiscColour,
                HasSunBeams = into.HasSunBeams || painted.HasSunBeams,
                HasSunGlare = into.HasSunGlare || painted.HasSunGlare,
                HasMainLightElevation = into.HasMainLightElevation || painted.HasMainLightElevation,
                SourceTimeOfDay = into.SourceTimeOfDay,

                SunIntensity = FoldScalar(into.SunIntensity, painted.SunIntensity, painted.HasSunIntensity, w),
                Rayleigh = FoldScalar(into.Rayleigh, painted.Rayleigh, painted.HasRayleigh, w),
                Mie = FoldScalar(into.Mie, painted.Mie, painted.HasMie, w),
                ComplementaryBias = FoldScalar(into.ComplementaryBias, painted.ComplementaryBias, painted.HasComplementaryBias, w),
                NearDistance = FoldScalar(into.NearDistance, painted.NearDistance, painted.HasNearDistance, w),
                NearDensity = FoldScalar(into.NearDensity, painted.NearDensity, painted.HasNearDensity, w),
                FarDistance = FoldScalar(into.FarDistance, painted.FarDistance, painted.HasFarDistance, w),
                FarDensity = FoldScalar(into.FarDensity, painted.FarDensity, painted.HasFarDensity, w),
                CloseFogMaxDistance = FoldScalar(into.CloseFogMaxDistance, painted.CloseFogMaxDistance, painted.HasCloseFogMaxDistance, w),
                FoggingStart = FoldScalar(into.FoggingStart, painted.FoggingStart, painted.HasFoggingStart, w),
                SunAxisElevation = FoldScalar(into.SunAxisElevation, painted.SunAxisElevation, painted.HasSunAxis, w),
                SunAxisZOffset = FoldScalar(into.SunAxisZOffset, painted.SunAxisZOffset, painted.HasSunAxis, w),
                SunAxisXyRotation = FoldScalar(into.SunAxisXyRotation, painted.SunAxisXyRotation, painted.HasSunAxis, w),
                MainLightTimeFactor = into.MainLightTimeFactor,
                MoonIntensity = FoldScalar(into.MoonIntensity, painted.MoonIntensity, painted.HasMoonParams, w),
                MoonSize = FoldScalar(into.MoonSize, painted.MoonSize, painted.HasMoonParams, w),
                MoonGlareIntensity = FoldScalar(into.MoonGlareIntensity, painted.MoonGlareIntensity, painted.HasMoonParams, w),
                MoonGlareSize = FoldScalar(into.MoonGlareSize, painted.MoonGlareSize, painted.HasMoonParams, w),
                MoonTransparency = FoldScalar(into.MoonTransparency, painted.MoonTransparency, painted.HasMoonParams, w),
                MoonAxisElevation = FoldScalar(into.MoonAxisElevation, painted.MoonAxisElevation, painted.HasMoonAxis, w),
                MoonAxisZOffset = FoldScalar(into.MoonAxisZOffset, painted.MoonAxisZOffset, painted.HasMoonAxis, w),
                MoonAxisXyRotation = FoldScalar(into.MoonAxisXyRotation, painted.MoonAxisXyRotation, painted.HasMoonAxis, w),
                StarBrightness = FoldScalar(into.StarBrightness, painted.StarBrightness, painted.HasStarBrightness, w),
                SunDiscSize = FoldScalar(into.SunDiscSize, painted.SunDiscSize, painted.HasSunDiscParams, w),
                SunDiscIntensity = FoldScalar(into.SunDiscIntensity, painted.SunDiscIntensity, painted.HasSunDiscParams, w),
                SunBeamsWidth = FoldScalar(into.SunBeamsWidth, painted.SunBeamsWidth, painted.HasSunBeams, w),
                SunBeamsHeight = FoldScalar(into.SunBeamsHeight, painted.SunBeamsHeight, painted.HasSunBeams, w),
                SunBeamsIntensity = FoldScalar(into.SunBeamsIntensity, painted.SunBeamsIntensity, painted.HasSunBeams, w),
                SunGlareIntensity = FoldScalar(into.SunGlareIntensity, painted.SunGlareIntensity, painted.HasSunGlare, w),
                SunGlareSize = FoldScalar(into.SunGlareSize, painted.SunGlareSize, painted.HasSunGlare, w),
                LightRigXyRotation = into.LightRigXyRotation,

                SkyOverlayTextureHash = painted.HasSkyOverlayTexture && w >= 0.5f ? painted.SkyOverlayTextureHash : into.SkyOverlayTextureHash,
                MoonTextureHash = painted.HasMoonTexture && w >= 0.5f ? painted.MoonTextureHash : into.MoonTextureHash,
                MoonGlareTextureHash = painted.HasMoonGlareTexture && w >= 0.5f ? painted.MoonGlareTextureHash : into.MoonGlareTextureHash,
                SunDiscTextureHash = painted.HasSunDiscTexture && w >= 0.5f ? painted.SunDiscTextureHash : into.SunDiscTextureHash,
                SunBeamsTextureHash = painted.HasSunBeamsTexture && w >= 0.5f ? painted.SunBeamsTextureHash : into.SunBeamsTextureHash,
                SunGlareTextureHash = painted.HasSunGlareTexture && w >= 0.5f ? painted.SunGlareTextureHash : into.SunGlareTextureHash,
            };

            FoldColour(into.SkyColour, painted.SkyColour, painted.HasSkyColour, w, o.SkyColour);
            FoldColour(into.ComplementaryColour, painted.ComplementaryColour, painted.HasComplementaryColour, w, o.ComplementaryColour);
            FoldColour(into.SunsetColour, painted.SunsetColour, painted.HasSunsetColour, w, o.SunsetColour);
            FoldColour(into.FogColour, painted.FogColour, painted.HasFogColour, w, o.FogColour);
            FoldColour(into.MainLightColour, painted.MainLightColour, painted.HasMainLightColour, w, o.MainLightColour);
            FoldColour(into.SunDiscColour, painted.SunDiscColour, painted.HasSunDiscColour, w, o.SunDiscColour);
            for (int i = 0; i < 4; i++)
            {
                o.MainLightElevation[i] = FoldScalar(
                    into.MainLightElevation[i], painted.MainLightElevation[i], painted.HasMainLightElevation, w);
            }

            return o;
        }

        private static float FoldScalar(float a, float b, bool stated, float w) =>
            stated ? a + ((b - a) * w) : a;

        private static void FoldColour(float[] a, float[] b, bool stated, float w, float[] target)
        {
            for (int i = 0; i < 3; i++)
            {
                target[i] = stated ? a[i] + ((b[i] - a[i]) * w) : a[i];
            }
        }

    public static bool TryExtractWeather(IReadOnlyList<byte[]> gdbs, out WeatherTheme theme)
    {
        theme = new WeatherTheme();
        var extractor = Extractor.Create(gdbs);
        return extractor is not null && extractor.ExtractWeather(theme);
    }

    public static bool TryExtractClouds(IReadOnlyList<byte[]> gdbs, out CloudTheme theme) =>
        TryExtractClouds(gdbs, 0.5f, out theme);

    public static bool TryExtractClouds(
        IReadOnlyList<byte[]> gdbs, float targetTime, out CloudTheme theme)
    {
        theme = new CloudTheme();
        var extractor = Extractor.Create(gdbs);
        if (extractor is null)
        {
            return false;
        }

        extractor.TargetTime = targetTime;
        return extractor.ExtractClouds(theme);
    }

    /// <summary>The tone mapping one theme record states, by its record hash — see
    /// <see cref="Extractor.ExtractToneMapRecord"/>.</summary>
    public static bool TryExtractToneMapRecord(IReadOnlyList<byte[]> gdbs, uint recordHash, out ToneMapTheme theme)
    {
        theme = new ToneMapTheme();
        var extractor = Extractor.Create(gdbs);
        return extractor is not null && extractor.ExtractToneMapRecord(recordHash, theme);
    }

    /// <summary>
    /// The tone mapping ONE Fable 3 theme states at a time of day — the <c>.envtheme</c> a
    /// pushed map id resolves to (<see cref="Engine_EnvThemeFile.TryMapTheme"/>), evaluated at
    /// <paramref name="time"/> alone. Only the parameters the theme itself keyframes are marked
    /// present (<c>Block.IsStated</c>), so a fold over the chain's own tone map leaves the rest
    /// alone, as the guest's <c>sub_82228D38</c> does for a parameter a layer does not state.
    /// </summary>
    public static bool TryExtractToneMapEnvTheme(Engine_EnvThemeFile file, float time, ToneMapTheme theme)
    {
        for (int i = 0; i < ToneMapTheme.ParamCount; i++)
        {
            theme.Present[i] = false;
        }

        return Extractor.ApplyEnvThemeToneMap(Engine_EnvThemeFile.Evaluate(new[] { file }, time), time, theme);
    }

    public static bool TryExtractToneMap(IReadOnlyList<byte[]> gdbs, out ToneMapTheme theme) =>
        TryExtractToneMap(gdbs, 0.5f, out theme);

    public static bool TryExtractToneMap(
        IReadOnlyList<byte[]> gdbs, float targetTime, out ToneMapTheme theme)
    {
        theme = new ToneMapTheme();
        var extractor = Extractor.Create(gdbs);
        if (extractor is null)
        {
            return false;
        }

        extractor.TargetTime = targetTime;
        return extractor.ExtractToneMap(theme);
    }

    /// <summary>
    /// Every record in the chain that carries an <c>EnvironmentThemeDaySet</c>, with its name,
    /// its record type hash and the day set's entry times. The sweep prints it; the runtime
    /// selects the loaded level's own record rather than the first one found.
    /// </summary>
    public static List<(string Owner, uint Type, List<float> Times)> ListDaySetOwners(IReadOnlyList<byte[]> gdbs)
    {
        var rows = new List<(string, uint, List<float>)>();
        var extractor = Extractor.Create(gdbs);
        extractor?.CollectDaySetOwners(rows);
        return rows;
    }

    public static bool TryGetDaySetTimes(
        IReadOnlyList<byte[]> gdbs, out List<float> times)
    {
        times = new List<float>();
        var extractor = Extractor.Create(gdbs);
        if (extractor is null)
        {
            return false;
        }

        extractor.CollectDaySetTimes(times);
        times.Sort();
        return times.Count > 0;
    }

    /// <summary>
    /// The title's blended theme block at a time of day, for the classic look where no guest
    /// runs: 194 float4s (<see cref="Engine_ClassicThemeParams"/>) in <paramref name="values"/>.
    /// </summary>
    /// <remarks>
    /// THE TITLE'S OWN EVALUATION, verified in IDA. EnvThemeGlobal is loaded over the defaults
    /// (`sub_82A45DD0`, `sub_82A44D08`); the level's day set (`sub_82A46BD0`) is evaluated at the
    /// time by `sub_8218E2F0` — the entries either side of it, wrapping the day, at 1-f and f —
    /// and overlaid on the global per parameter by `sub_821D8588`: out = out * (1 - flag) + day,
    /// the flag being how much of the day set states that parameter. A loaded float is (v, 0, 0,
    /// 0); a colour (R, G, B) / 255 with w = 1, times Factor for type 2; a parameter whose group
    /// record is present but whose field is not is zero with flag 0; one whose group is missing
    /// is left as it was. Painted, weather and pushed themes are runtime layers and not here.
    /// </remarks>
    public static bool TryEvaluateClassic(IReadOnlyList<byte[]> gdbs, float dayFraction, float[] values)
    {
        if (values.Length < Engine_ClassicThemeParams.Count * 4)
        {
            return false;
        }
        Extractor? extractor = Extractor.Create(gdbs);
        return extractor is not null && extractor.EvaluateClassic(dayFraction, values);
    }

    /// <summary>
    /// The Fable 3 theme stack a chain carries — the <c>.envtheme</c> documents
    /// <c>Engine_SkyEnvironment.BuildGdbChain</c> appends, lowest layer first — evaluated at a
    /// time of day. False when the chain has none.
    /// </summary>
    /// <summary>Whether the chain carries <c>.envtheme</c> layers, i.e. is a Fable 3 theme stack.</summary>
    public static bool CarriesEnvThemes(IReadOnlyList<byte[]> gdbs)
    {
        foreach (byte[] bytes in gdbs)
        {
            if (bytes is not null && Engine_EnvThemeFile.TryGet(bytes, out _))
            {
                return true;
            }
        }

        return false;
    }

    public static bool TryEvaluateEnvThemes(
        IReadOnlyList<byte[]> gdbs, float time, out Engine_EnvThemeFile.Block block)
    {
        block = null!;
        var layers = new List<Engine_EnvThemeFile>();
        foreach (byte[] bytes in gdbs)
        {
            if (bytes is not null && Engine_EnvThemeFile.TryGet(bytes, out Engine_EnvThemeFile file))
            {
                layers.Add(file);
            }
        }

        if (layers.Count == 0)
        {
            return false;
        }

        block = Engine_EnvThemeFile.Evaluate(layers, time);
        return true;
    }

    private sealed class Extractor
    {
        private readonly List<GdbView> _views = new();

        /// <summary>
        /// The chain's <c>.envtheme</c> layers. When a chain carries any, they ARE the theme: the
        /// Fable 3 manager (<c>sub_8248BDC0</c>) reads <c>LevelSpecificData.EnvironmentTheme</c>
        /// and nothing in the title reads <c>EnvironmentThemeDaySet</c> — the name is absent from
        /// its strings and its FNV-1 is never loaded — so the F2-format day sets still shipped in
        /// <c>environmentthemes.gdb</c> are not consulted.
        /// </summary>
        private readonly List<Engine_EnvThemeFile> _envThemes = new();

        public float TargetTime { get; set; } = 0.5f;

        public static Extractor? Create(IReadOnlyList<byte[]> gdbs)
        {
            var extractor = new Extractor();
            foreach (byte[] bytes in gdbs)
            {
                if (bytes is null)
                {
                    continue;
                }

                if (Engine_EnvThemeFile.TryGet(bytes, out Engine_EnvThemeFile envTheme))
                {
                    extractor._envThemes.Add(envTheme);
                    continue;
                }

                // Shared: this runs about nine times per re-evaluation over the same blobs.
                if (GdbView.TryParseShared(bytes, out GdbView view, out _))
                {
                    extractor._views.Add(view);
                }
            }

            return extractor._views.Count == 0 && extractor._envThemes.Count == 0 ? null : extractor;
        }

        public bool ExtractSky(SkyTheme theme)
        {
            if (_envThemes.Count != 0)
            {
                return ApplyEnvThemeSky(theme);
            }

            bool applied = RunChain(
                theme,
                (t, r) => ApplySkyThemeRecord(r, (SkyTheme)t),
                (t, owner, hash) =>
                {
                    RecordRef target = ResolveRecordField(owner, hash);
                    return target.Valid && ApplySkyThemeRecord(target, (SkyTheme)t);
                },
                time => theme.SourceTimeOfDay = time);

            if (!applied)
            {
                applied = ApplyFirstMatching(r => ApplySkyThemeRecord(r, theme));
            }

            theme.HasAny = applied;
            return applied;
        }

        public bool ExtractSkyPainted(uint daySetHash, SkyTheme theme)
        {
            RecordRef daySet = LookupFirst(daySetHash);
            if (!daySet.Valid || !SelectThemeFromDaySet(daySet, out RecordRef dayTheme, out float dayTime))
            {
                return false;
            }

            theme.SourceTimeOfDay = dayTime;
            theme.HasAny = ApplySkyThemeRecord(dayTheme, theme, overridesOnly: true);
            return theme.HasAny;
        }

        private bool Stated(RecordRef record, bool overridesOnly, params (uint Hash, byte Type)[] fields)
        {
            if (!overridesOnly)
            {
                return true;
            }

            foreach ((uint hash, byte type) in fields)
            {
                if (hash != 0 && CountField(record, hash, type) >= 2)
                {
                    return true;
                }
            }

            return false;
        }

        private int CountField(RecordRef record, uint fieldHash, byte expectedType)
        {
            if (!record.Valid || record.Db >= _views.Count)
            {
                return 0;
            }

            int count = 0;
            RecordRef current = record;
            var seen = new HashSet<long>();

            for (int depth = 0; depth < 64; depth++)
            {
                long key = ((long)current.Db << 48) ^ current.Index;
                if (!seen.Add(key))
                {
                    return count;
                }

                if (FindLocalField(current, fieldHash, expectedType, out _) && ++count >= 2)
                {
                    return count;
                }

                if (!FindLocalField(current, GdbView.HashParent, 0xFF, out GdbField parent))
                {
                    return count;
                }

                current = FieldToRecord(parent);
                if (!current.Valid)
                {
                    return count;
                }
            }

            return count;
        }

        public bool ExtractWeather(WeatherTheme theme)
        {
            if (_envThemes.Count != 0)
            {
                return ApplyEnvThemeWeather(theme);
            }

            bool applied = RunChain(
                theme,
                (t, r) => ApplyWeatherThemeRecord(r, (WeatherTheme)t),
                (t, owner, hash) =>
                {
                    RecordRef target = ResolveRecordField(owner, hash);
                    return target.Valid && ApplyWeatherThemeRecord(target, (WeatherTheme)t);
                },
                time => theme.SourceTimeOfDay = time,
                sharedGlobalsMayApply: false);

            theme.HasAny = applied;
            return applied;
        }

        /// <param name="sharedGlobalsMayApply">
        /// Whether the last-ditch fallback may reach records from the SHARED databases.
        /// `AppendSharedGdbs` puts `environmentthemes.gdb` and `globals.gdb` on every level's
        /// chain, so that fallback can hand a level a theme belonging to no level in
        /// particular. Harmless for lighting, which needs some answer. NOT harmless for
        /// weather: it is how an interior that states none of its own inherited snow.
        /// </param>
        private bool RunChain(
            object theme,
            Func<object, RecordRef, bool> applyRecord,
            Func<object, RecordRef, uint, bool> applyField,
            Action<float> setTime,
            bool sharedGlobalsMayApply = true)
        {
            bool applied = false;

            if (sharedGlobalsMayApply)
            {
                bool globalApplied = false;
                foreach (RecordRef level in LookupAllInDb(EnvHash.LevelData, 0))
                {
                    globalApplied |= applyField(theme, level, EnvHash.EnvThemeGlobal);
                }

                if (!globalApplied)
                {
                    RecordRef owner = FirstRecordWithFieldInDb(EnvHash.EnvThemeGlobal, 0);
                    if (owner.Valid)
                    {
                        globalApplied |= applyField(theme, owner, EnvHash.EnvThemeGlobal);
                    }
                }

                if (!globalApplied)
                {
                    foreach (RecordRef global in LookupAllInDb(EnvHash.EnvThemeGlobal, 0))
                    {
                        globalApplied |= applyRecord(theme, global);
                    }
                }

                if (!globalApplied)
                {
                    foreach (RecordRef global in LookupAll(EnvHash.EnvThemeGlobal))
                    {
                        if (applyRecord(theme, global))
                        {
                            globalApplied = true;
                            break;
                        }
                    }
                }

                applied |= globalApplied;
            }

            bool dayApplied = false;
            RecordRef daySet = FindDaySet(true);
            if (daySet.Valid && SelectThemeFromDaySet(daySet, out RecordRef dayTheme, out float time))
            {
                setTime(time);
                dayApplied = applyRecord(theme, dayTheme);
            }

            if (!dayApplied && !sharedGlobalsMayApply)
            {
                foreach (RecordRef level in LookupAllInDb(EnvHash.LevelData, 0))
                {
                    dayApplied |= applyField(theme, level, EnvHash.EnvThemeGlobal);
                }

                if (!dayApplied)
                {
                    RecordRef owner = FirstRecordWithFieldInDb(EnvHash.EnvThemeGlobal, 0);
                    if (owner.Valid)
                    {
                        dayApplied |= applyField(theme, owner, EnvHash.EnvThemeGlobal);
                    }
                }

                if (!dayApplied)
                {
                    foreach (RecordRef global in LookupAllInDb(EnvHash.EnvThemeGlobal, 0))
                    {
                        dayApplied |= applyRecord(theme, global);
                    }
                }
            }

            if (!dayApplied)
            {
                daySet = FindDaySet(false);
                if (daySet.Valid &&
                    SelectThemeFromDaySet(daySet, out RecordRef fallbackTheme, out float fallbackTime))
                {
                    setTime(fallbackTime);
                    dayApplied = applyRecord(theme, fallbackTheme);
                }
            }

            applied |= dayApplied;
            return applied;
        }

        public bool ExtractClouds(CloudTheme theme)
        {
            if (_envThemes.Count != 0)
            {
                return ApplyEnvThemeClouds(theme);
            }

            bool applied = RunChain(
                theme,
                (t, r) => ApplyCloudThemeRecord(r, (CloudTheme)t),
                (t, owner, hash) =>
                {
                    RecordRef target = ResolveRecordField(owner, hash);
                    return target.Valid && ApplyCloudThemeRecord(target, (CloudTheme)t);
                },
                time => theme.SourceTimeOfDay = time);

            int count = 0;
            foreach (CloudLayerTheme layer in theme.Layers)
            {
                if (layer.Enabled)
                {
                    count++;
                }
            }

            theme.LayerCount = count;
            theme.HasAny = applied;
            return applied;
        }

        /// <summary>
        /// The tone mapping ONE theme record states — `EnvThemeSlowTime2` and the other themes
        /// the guest pushes on its stack, looked up by the record hash the guest keys them by
        /// (the FNV-1 of the name). Only what the record and its sub-records state is marked
        /// present; fields a theme leaves to its base stay unmarked so a blend leaves them alone.
        /// </summary>
        public bool ExtractToneMapRecord(uint recordHash, ToneMapTheme theme)
        {
            // A Fable 3 pushed theme is its own .envtheme, not a record in this chain.
            if (_envThemes.Count != 0)
            {
                return false;
            }

            bool applied = false;
            foreach (RecordRef record in LookupAll(recordHash))
            {
                applied |= ApplyToneMapThemeRecord(record, theme);
            }

            theme.HasAny = applied;
            return applied;
        }

        public bool ExtractToneMap(ToneMapTheme theme)
        {
            if (_envThemes.Count != 0)
            {
                return ApplyEnvThemeToneMap(theme);
            }

            bool applied = RunChain(
                theme,
                (t, r) => ApplyToneMapThemeRecord(r, (ToneMapTheme)t),
                (t, owner, hash) =>
                {
                    RecordRef target = ResolveRecordField(owner, hash);
                    return target.Valid && ApplyToneMapThemeRecord(target, (ToneMapTheme)t);
                },
                time => theme.SourceTimeOfDay = time);

            theme.HasAny = applied;

            if (System.Environment.GetEnvironmentVariable("REFII_FOG_TRACE") == "1")
            {
                var sb = new System.Text.StringBuilder("[tone-trace] stated:");
                for (int i = 0; i < ToneMapTheme.ParamCount; i++)
                {
                    if (theme.Present[i])
                    {
                        sb.Append(' ').Append((ToneMapParam)i).Append('=').Append(theme.V[i].ToString("0.####"));
                    }
                }
                Godot.GD.Print(sb.ToString());
            }

            return applied;
        }

        private bool ApplyToneMapThemeRecord(RecordRef themeRecord, ToneMapTheme theme)
        {
            if (!themeRecord.Valid)
            {
                return false;
            }

            bool any = false;
            RecordRef tone = ResolveRecordField(themeRecord, EnvHash.ToneMappingRecord);
            if (tone.Valid)
            {
                any |= ApplyToneMapRecord(tone, theme);
            }

            RecordRef dof = ResolveRecordField(themeRecord, EnvHash.DepthOfFieldRecord);
            if (dof.Valid)
            {
                any |= ApplyToneMapRecord(dof, theme);
            }

            if (!any)
            {
                any = ApplyToneMapRecord(themeRecord, theme);
            }

            return any;
        }

        private bool ApplyToneMapRecord(RecordRef root, ToneMapTheme theme)
        {
            if (!root.Valid)
            {
                return false;
            }

            bool any = false;
            for (int i = 0; i < ToneMapTheme.ParamCount; i++)
            {
                if (ReadFloatDeep(root, ToneMapTheme.FieldHashes[i], out float value, 2))
                {
                    theme.V[i] = value;
                    theme.Present[i] = true;
                    any = true;
                }
            }

            return any;
        }

        private bool ReadFloatDeep(RecordRef record, uint fieldHash, out float value, int depth)
        {
            value = 0f;
            if (!record.Valid || record.Db >= _views.Count)
            {
                return false;
            }

            if (ReadFloat(record, fieldHash, out value))
            {
                return true;
            }

            if (depth <= 0)
            {
                return false;
            }

            foreach (GdbField field in _views[record.Db].Fields(record.Index))
            {
                if (field.Hash == GdbView.HashParent)
                {
                    continue;
                }

                byte type = (byte)field.Type;
                if (type != 4 && type != 6 && type != 7)
                {
                    continue;
                }

                RecordRef child = FieldToRecord(field);
                if (!child.Valid || (child.Db == record.Db && child.Index == record.Index))
                {
                    continue;
                }

                if (ReadFloatDeep(child, fieldHash, out value, depth - 1))
                {
                    return true;
                }
            }

            return false;
        }

        private bool ApplyCloudThemeRecord(RecordRef themeRecord, CloudTheme theme)
        {
            if (!themeRecord.Valid)
            {
                return false;
            }

            RecordRef clouds = ResolveRecordField(themeRecord, EnvHash.Clouds);
            if (!clouds.Valid)
            {
                clouds = themeRecord;
            }

            var layerFields = new[]
            {
                EnvHash.Layer1, EnvHash.Layer2, EnvHash.Layer3, EnvHash.Layer4,
            };

            bool any = false;
            for (int i = 0; i < 4; i++)
            {
                CloudLayerTheme layer = theme.Layers[i].Clone();
                RecordRef target = ResolveRecordField(clouds, layerFields[i]);
                if (target.Valid && ReadCloudLayerRecord(target, layer) > 0)
                {
                    theme.Layers[i] = layer;
                    any = true;
                }
            }

            if (!any)
            {
                CloudLayerTheme layer = theme.Layers[0].Clone();
                int fieldCount = ReadCloudLayerRecord(clouds, layer);
                if (fieldCount >= 2 || layer.HasDensityMap)
                {
                    theme.Layers[0] = layer;
                    any = true;
                }
            }

            return any;
        }

        private int ReadCloudLayerRecord(RecordRef layerRecord, CloudLayerTheme layer)
        {
            if (!layerRecord.Valid)
            {
                return 0;
            }

            int fields = 0;

            if (FindField(layerRecord, EnvHash.DensityMap, 0xFF, out GdbField density) &&
                density.Raw != 0 && density.Raw != EnvHash.Null)
            {
                layer.HasDensityMap = true;
                layer.DensityMapHash = density.Raw;
                fields++;
            }

            void Read(uint hash, Action<float> assign, Action flag)
            {
                if (ReadFloat(layerRecord, hash, out float value))
                {
                    flag();
                    assign(value);
                    fields++;
                }
            }

            Read(EnvHash.PositionX, v => layer.PositionX = v, () => layer.HasPosition = true);
            Read(EnvHash.PositionY, v => layer.PositionY = v, () => layer.HasPosition = true);
            Read(EnvHash.SizeX, v => layer.SizeX = v, () => layer.HasSize = true);
            Read(EnvHash.SizeY, v => layer.SizeY = v, () => layer.HasSize = true);
            Read(EnvHash.TextureScaleX, v => layer.TextureScaleX = v, () => layer.HasTextureScale = true);
            Read(EnvHash.TextureScaleY, v => layer.TextureScaleY = v, () => layer.HasTextureScale = true);
            Read(EnvHash.VelocityX, v => layer.VelocityX = v, () => layer.HasVelocity = true);
            Read(EnvHash.VelocityY, v => layer.VelocityY = v, () => layer.HasVelocity = true);
            Read(EnvHash.Height, v => layer.Height = v, () => layer.HasHeight = true);
            Read(EnvHash.Transparency, v => layer.Transparency = v, () => layer.HasTransparency = true);
            Read(EnvHash.Brightness, v => layer.Brightness = v, () => layer.HasBrightness = true);
            Read(EnvHash.AmbientLight, v => layer.AmbientLight = v, () => layer.HasAmbient = true);
            Read(EnvHash.NormalStrength, v => layer.NormalStrength = v, () => layer.HasNormalStrength = true);
            Read(EnvHash.TranslucencyStrength, v => layer.TranslucencyStrength = v,
                () => layer.HasTranslucencyStrength = true);

            if (fields > 0)
            {
                layer.Enabled = true;
            }

            return fields;
        }

        private bool ApplyFirstMatching(Func<RecordRef, bool> apply)
        {
            for (int db = 0; db < _views.Count; db++)
            {
                for (int record = 0; record < _views[db].RecordCount; record++)
                {
                    if (apply(new RecordRef(db, record, true)))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private bool ApplySkyThemeRecord(RecordRef themeRecord, SkyTheme theme, bool overridesOnly = false)
        {
            if (!themeRecord.Valid)
            {
                return false;
            }

            bool any = false;

            RecordRef sky = ResolveRecordField(themeRecord, EnvHash.Sky);
            any |= sky.Valid ? ApplySkyRecord(sky, theme, overridesOnly) : ApplySkyRecord(themeRecord, theme, overridesOnly);

            RecordRef fog = ResolveRecordField(themeRecord, EnvHash.Fogging);
            any |= fog.Valid ? ApplyFogRecord(fog, theme, overridesOnly) : ApplyFogRecord(themeRecord, theme, overridesOnly);

            RecordRef sun = ResolveRecordField(themeRecord, EnvHash.SunRecord);
            if (sun.Valid)
            {
                any |= ApplySunRecord(sun, theme, overridesOnly);
            }
            else if (sky.Valid)
            {
                any |= ApplySunRecord(sky, theme, overridesOnly);
            }

            RecordRef lighting = ResolveRecordField(themeRecord, EnvHash.Lighting);
            if (lighting.Valid)
            {
                var colour = new float[3];
                if (Stated(lighting, overridesOnly,
                        (EnvHash.MainLightColour, (byte)6), (EnvHash.MainLightColourRed, (byte)3),
                        (EnvHash.MainLightColourGreen, (byte)3), (EnvHash.MainLightColourBlue, (byte)3),
                        (EnvHash.MainLightColourFactor, (byte)3)) &&
                    (ReadColourRecordField(lighting, EnvHash.MainLightColour, colour) ||
                    ReadFlatColour(lighting, EnvHash.MainLightColourRed, EnvHash.MainLightColourGreen,
                        EnvHash.MainLightColourBlue, EnvHash.MainLightColourFactor, colour)))
                {
                    Array.Copy(colour, theme.MainLightColour, 3);
                    theme.HasMainLightColour = true;
                    any = true;
                }

                // The sun's PATH. These live in the Lighting record, not in Sky — the guest
                // registers MainLightElevation0/6/12/18, TimeFactor and LightRigXYRotation
                // against the "Lighting" group and only SunAxis* against "Sky".
                any |= Param(lighting, EnvHash.MainLightElevation0,
                    v => { theme.HasMainLightElevation = true; theme.MainLightElevation[0] = v; }, overridesOnly);
                any |= Param(lighting, EnvHash.MainLightElevation6,
                    v => { theme.HasMainLightElevation = true; theme.MainLightElevation[1] = v; }, overridesOnly);
                any |= Param(lighting, EnvHash.MainLightElevation12,
                    v => { theme.HasMainLightElevation = true; theme.MainLightElevation[2] = v; }, overridesOnly);
                any |= Param(lighting, EnvHash.MainLightElevation18,
                    v => { theme.HasMainLightElevation = true; theme.MainLightElevation[3] = v; }, overridesOnly);
                any |= Param(lighting, EnvHash.LightRigXyRotation,
                    v => theme.LightRigXyRotation = v, overridesOnly);

                if (Stated(lighting, overridesOnly, (EnvHash.TimeFactor, (byte)3)) &&
                    ReadFloat(lighting, EnvHash.TimeFactor, out float lightingTimeFactor) &&
                    float.IsFinite(lightingTimeFactor) && lightingTimeFactor > 0f)
                {
                    theme.MainLightTimeFactor = lightingTimeFactor;
                }
            }

            return any;
        }

        private bool ApplySkyRecord(RecordRef sky, SkyTheme theme, bool overridesOnly = false)
        {
            if (!sky.Valid)
            {
                return false;
            }

            bool any = false;
            var colour = new float[3];

            if (Stated(sky, overridesOnly,
                    (EnvHash.SkyColour, (byte)6), (EnvHash.SkyColourRed, (byte)3),
                    (EnvHash.SkyColourGreen, (byte)3), (EnvHash.SkyColourBlue, (byte)3),
                    (EnvHash.SkyColourFactor, (byte)3)) &&
                (ReadColourRecordField(sky, EnvHash.SkyColour, colour) ||
                ReadFlatColour(sky, EnvHash.SkyColourRed, EnvHash.SkyColourGreen,
                    EnvHash.SkyColourBlue, EnvHash.SkyColourFactor, colour)))
            {
                Array.Copy(colour, theme.SkyColour, 3);
                theme.HasSkyColour = true;
                any = true;
            }

            if (Stated(sky, overridesOnly,
                    (EnvHash.SkyComplementaryColour, (byte)6), (EnvHash.SkyComplementaryColourRed, (byte)3),
                    (EnvHash.SkyComplementaryColourGreen, (byte)3), (EnvHash.SkyComplementaryColourBlue, (byte)3),
                    (EnvHash.SkyComplementaryColourFactor, (byte)3)) &&
                (ReadColourRecordField(sky, EnvHash.SkyComplementaryColour, colour) ||
                ReadFlatColour(sky, EnvHash.SkyComplementaryColourRed,
                    EnvHash.SkyComplementaryColourGreen, EnvHash.SkyComplementaryColourBlue,
                    EnvHash.SkyComplementaryColourFactor, colour)))
            {
                Array.Copy(colour, theme.ComplementaryColour, 3);
                theme.HasComplementaryColour = true;
                any = true;
            }

            if (Stated(sky, overridesOnly,
                    (EnvHash.SunsetColour, (byte)6), (EnvHash.SunsetColourRed, (byte)3),
                    (EnvHash.SunsetColourGreen, (byte)3), (EnvHash.SunsetColourBlue, (byte)3),
                    (EnvHash.SunsetColourFactor, (byte)3)) &&
                (ReadColourRecordField(sky, EnvHash.SunsetColour, colour) ||
                ReadFlatColour(sky, EnvHash.SunsetColourRed, EnvHash.SunsetColourGreen,
                    EnvHash.SunsetColourBlue, EnvHash.SunsetColourFactor, colour)))
            {
                Array.Copy(colour, theme.SunsetColour, 3);
                theme.HasSunsetColour = true;
                any = true;
            }

            any |= Param(sky, EnvHash.SunIntensity, v => { theme.HasSunIntensity = true; theme.SunIntensity = v; }, overridesOnly);
            any |= Param(sky, EnvHash.SkyBetaRayleighMultiplier, v => { theme.HasRayleigh = true; theme.Rayleigh = v; }, overridesOnly);
            any |= Param(sky, EnvHash.SkyBetaMieMultiplier, v => { theme.HasMie = true; theme.Mie = v; }, overridesOnly);
            any |= Param(sky, EnvHash.SkyComplementaryColourBias, v => { theme.HasComplementaryBias = true; theme.ComplementaryBias = v; }, overridesOnly);
            any |= Param(sky, EnvHash.SunAxisElevation, v => { theme.HasSunAxis = true; theme.SunAxisElevation = v; }, overridesOnly);
            any |= Param(sky, EnvHash.SunAxisZOffset, v => { theme.HasSunAxis = true; theme.SunAxisZOffset = v; }, overridesOnly);
            any |= Param(sky, EnvHash.SunAxisXyRotationOffset, v => { theme.HasSunAxis = true; theme.SunAxisXyRotation = v; }, overridesOnly);

            if (Stated(sky, overridesOnly, (EnvHash.TimeFactor, (byte)3)) &&
                ReadFloat(sky, EnvHash.TimeFactor, out float timeFactor) &&
                float.IsFinite(timeFactor) && timeFactor > 0f)
            {
                theme.MainLightTimeFactor = timeFactor;
            }

            if (!overridesOnly)
            {
                any |= Texture(sky, EnvHash.SkyOverlayTexture,
                    h => { theme.HasSkyOverlayTexture = true; theme.SkyOverlayTextureHash = h; });
                any |= Texture(sky, EnvHash.MoonTexture,
                    h => { theme.HasMoonTexture = true; theme.MoonTextureHash = h; });
                any |= Texture(sky, EnvHash.MoonGlareTexture,
                    h => { theme.HasMoonGlareTexture = true; theme.MoonGlareTextureHash = h; });
                any |= Texture(sky, EnvHash.DiscTexture,
                    h => { theme.HasSunDiscTexture = true; theme.SunDiscTextureHash = h; });
                any |= Texture(sky, EnvHash.SunBeamsTexture,
                    h => { theme.HasSunBeamsTexture = true; theme.SunBeamsTextureHash = h; });
                any |= Texture(sky, EnvHash.GlareTexture,
                    h => { theme.HasSunGlareTexture = true; theme.SunGlareTextureHash = h; });
            }

            any |= Param(sky, EnvHash.MoonIntensity, v => { theme.HasMoonParams = true; theme.MoonIntensity = v; }, overridesOnly);
            any |= Param(sky, EnvHash.MoonSize, v => { theme.HasMoonParams = true; theme.MoonSize = v; }, overridesOnly);
            any |= Param(sky, EnvHash.MoonGlareIntensity, v => { theme.HasMoonParams = true; theme.MoonGlareIntensity = v; }, overridesOnly);
            any |= Param(sky, EnvHash.MoonGlareSize, v => { theme.HasMoonParams = true; theme.MoonGlareSize = v; }, overridesOnly);
            any |= Param(sky, EnvHash.MoonTransparency, v => { theme.HasMoonParams = true; theme.MoonTransparency = v; }, overridesOnly);
            any |= Param(sky, EnvHash.MoonAxisElevation, v => { theme.HasMoonAxis = true; theme.MoonAxisElevation = v; }, overridesOnly);
            any |= Param(sky, EnvHash.MoonAxisZOffset, v => { theme.HasMoonAxis = true; theme.MoonAxisZOffset = v; }, overridesOnly);
            any |= Param(sky, EnvHash.MoonAxisXyRotationOffset, v => { theme.HasMoonAxis = true; theme.MoonAxisXyRotation = v; }, overridesOnly);
            any |= Param(sky, EnvHash.StarBrightness, v => { theme.HasStarBrightness = true; theme.StarBrightness = v; }, overridesOnly);

            return any;
        }

        private bool ApplySunRecord(RecordRef sun, SkyTheme theme, bool overridesOnly = false)
        {
            if (!sun.Valid)
            {
                return false;
            }

            bool any = false;

            any |= Param(sun, EnvHash.DiscSize, v => { theme.HasSunDiscParams = true; theme.SunDiscSize = v; }, overridesOnly);
            any |= Param(sun, EnvHash.DiscIntensity, v => { theme.HasSunDiscParams = true; theme.SunDiscIntensity = v; }, overridesOnly);

            var colour = new float[3];
            if (Stated(sun, overridesOnly,
                    (EnvHash.DiscColour, (byte)6), (EnvHash.DiscColourRed, (byte)3),
                    (EnvHash.DiscColourGreen, (byte)3), (EnvHash.DiscColourBlue, (byte)3),
                    (EnvHash.DiscColourFactor, (byte)3)) &&
                (ReadColourRecordField(sun, EnvHash.DiscColour, colour) ||
                ReadFlatColour(sun, EnvHash.DiscColourRed, EnvHash.DiscColourGreen,
                    EnvHash.DiscColourBlue, EnvHash.DiscColourFactor, colour)))
            {
                Array.Copy(colour, theme.SunDiscColour, 3);
                theme.HasSunDiscColour = true;
                any = true;
            }

            any |= Param(sun, EnvHash.SunBeamsWidth, v => { theme.HasSunBeams = true; theme.SunBeamsWidth = v; }, overridesOnly);
            any |= Param(sun, EnvHash.SunBeamsHeight, v => { theme.HasSunBeams = true; theme.SunBeamsHeight = v; }, overridesOnly);
            any |= Param(sun, EnvHash.SunBeamsIntensity, v => { theme.HasSunBeams = true; theme.SunBeamsIntensity = v; }, overridesOnly);
            any |= Param(sun, EnvHash.GlareIntensity, v => { theme.HasSunGlare = true; theme.SunGlareIntensity = v; }, overridesOnly);
            any |= Param(sun, EnvHash.GlareSize, v => { theme.HasSunGlare = true; theme.SunGlareSize = v; }, overridesOnly);

            if (!overridesOnly)
            {
                any |= Texture(sun, EnvHash.DiscTexture,
                    h => { theme.HasSunDiscTexture = true; theme.SunDiscTextureHash = h; });
                any |= Texture(sun, EnvHash.SunBeamsTexture,
                    h => { theme.HasSunBeamsTexture = true; theme.SunBeamsTextureHash = h; });
                any |= Texture(sun, EnvHash.GlareTexture,
                    h => { theme.HasSunGlareTexture = true; theme.SunGlareTextureHash = h; });
            }

            return any;
        }

        private bool ApplyFogRecord(RecordRef fog, SkyTheme theme, bool overridesOnly = false)
        {
            if (!fog.Valid)
            {
                return false;
            }

            bool any = false;
            var colour = new float[3];

            if (Stated(fog, overridesOnly,
                    (EnvHash.CloseFogColour, (byte)6), (EnvHash.CloseFogColourRed, (byte)3),
                    (EnvHash.CloseFogColourGreen, (byte)3), (EnvHash.CloseFogColourBlue, (byte)3),
                    (EnvHash.CloseFogColourFactor, (byte)3)) &&
                (ReadColourRecordField(fog, EnvHash.CloseFogColour, colour) ||
                ReadFlatColour(fog, EnvHash.CloseFogColourRed, EnvHash.CloseFogColourGreen,
                    EnvHash.CloseFogColourBlue, EnvHash.CloseFogColourFactor, colour)))
            {
                Array.Copy(colour, theme.FogColour, 3);
                theme.HasFogColour = true;
                any = true;
            }

            any |= Param(fog, EnvHash.NearDistance, v => { theme.NearDistance = v; theme.HasNearFog = true; theme.HasNearDistance = true; }, overridesOnly);
            any |= Param(fog, EnvHash.NearDensity, v => { theme.NearDensity = v; theme.HasNearFog = true; theme.HasNearDensity = true; }, overridesOnly);
            any |= Param(fog, EnvHash.FarDistance, v => { theme.FarDistance = v; theme.HasFarFog = true; theme.HasFarDistance = true; }, overridesOnly);
            any |= Param(fog, EnvHash.FarDensity, v => { theme.FarDensity = v; theme.HasFarFog = true; theme.HasFarDensity = true; }, overridesOnly);
            any |= Param(fog, EnvHash.CloseFogMaxDistance, v => { theme.CloseFogMaxDistance = v; theme.HasCloseFogMaxDistance = true; }, overridesOnly);
            any |= Param(fog, EnvHash.FoggingStart, v => { theme.FoggingStart = v; theme.HasFoggingStart = true; }, overridesOnly);

            if (System.Environment.GetEnvironmentVariable("REFII_FOG_TRACE") == "1")
            {
                Godot.GD.Print("[fog-trace] Fogging record states:" +
                    (ReadFloat(fog, EnvHash.NearDistance, out float nd) ? $" NearDistance={nd}" : " NearDistance=-") +
                    (ReadFloat(fog, EnvHash.NearDensity, out float nn) ? $" NearDensity={nn}" : " NearDensity=-") +
                    (ReadFloat(fog, EnvHash.FarDistance, out float fd) ? $" FarDistance={fd}" : " FarDistance=-") +
                    (ReadFloat(fog, EnvHash.FarDensity, out float fn) ? $" FarDensity={fn}" : " FarDensity=-") +
                    (ReadFloat(fog, EnvHash.FoggingStart, out float fs) ? $" FoggingStart={fs}" : " FoggingStart=-") +
                    (ReadFloat(fog, EnvHash.CloseFogMaxDistance, out float cm) ? $" CloseFogMaxDistance={cm}" : " CloseFogMaxDistance=-") +
                    (theme.HasFogColour ? $" CloseFogColour=({theme.FogColour[0]:0.000},{theme.FogColour[1]:0.000},{theme.FogColour[2]:0.000})" : " CloseFogColour=-"));
            }

            return any;
        }

        private bool ApplyWeatherThemeRecord(RecordRef themeRecord, WeatherTheme theme)
        {
            if (!themeRecord.Valid)
            {
                return false;
            }

            bool any = false;

            RecordRef sky = ResolveRecordField(themeRecord, EnvHash.Sky);
            any |= sky.Valid
                ? ApplyWeatherSkyRecord(sky, theme)
                : ApplyWeatherSkyRecord(themeRecord, theme);

            RecordRef wind = ResolveRecordField(themeRecord, EnvHash.Wind);
            any |= wind.Valid
                ? ApplyWeatherWindRecord(wind, theme)
                : ApplyWeatherWindRecord(themeRecord, theme);

            RecordRef mist = ResolveRecordField(themeRecord, EnvHash.GroundMist);
            if (mist.Valid && ReadFloat(mist, EnvHash.Strength, out float strength))
            {
                theme.HasGroundMist = true;
                theme.GroundMistStrength = strength;
                any = true;
            }

            return any;
        }

        private bool ApplyWeatherSkyRecord(RecordRef sky, WeatherTheme theme)
        {
            if (!sky.Valid)
            {
                return false;
            }

            bool any = false;
            any |= Param(sky, EnvHash.RainDensity, v => { theme.HasRain = true; theme.RainDensity = v; });
            any |= Param(sky, EnvHash.RainSize, v => { theme.HasRain = true; theme.RainSize = v; });
            any |= Param(sky, EnvHash.SnowFallSpeed, v => { theme.HasSnow = true; theme.SnowFallSpeed = v; });
            any |= Param(sky, EnvHash.SnowSize, v => { theme.HasSnow = true; theme.SnowSize = v; });
            return any;
        }

        private bool ApplyWeatherWindRecord(RecordRef wind, WeatherTheme theme)
        {
            if (!wind.Valid)
            {
                return false;
            }

            bool any = false;
            any |= Param(wind, EnvHash.WindStrengthMin, v => { theme.HasWind = true; theme.WindStrengthMin = v; });
            any |= Param(wind, EnvHash.WindStrengthMax, v => { theme.HasWind = true; theme.WindStrengthMax = v; });
            any |= Param(wind, EnvHash.WindStrengthVariation, v => { theme.HasWind = true; theme.WindStrengthVariation = v; });
            any |= Param(wind, EnvHash.WindXyRotationMin, v => { theme.HasWind = true; theme.WindXyRotationMin = v; });
            any |= Param(wind, EnvHash.WindXyRotationMax, v => { theme.HasWind = true; theme.WindXyRotationMax = v; });
            any |= Param(wind, EnvHash.WindElevationMin, v => { theme.HasWind = true; theme.WindElevationMin = v; });
            any |= Param(wind, EnvHash.WindElevationMax, v => { theme.HasWind = true; theme.WindElevationMax = v; });
            any |= Param(wind, EnvHash.WindChangeFrequency, v => { theme.HasWind = true; theme.WindChangeFrequency = v; });
            any |= Param(wind, EnvHash.WindChangeDuration, v => { theme.HasWind = true; theme.WindChangeDuration = v; });
            any |= Param(wind, EnvHash.WindDirectionVariation, v => { theme.HasWind = true; theme.WindDirectionVariation = v; });
            return any;
        }

        private bool Param(RecordRef record, uint hash, Action<float> assign, bool overridesOnly = false)
        {
            if (!Stated(record, overridesOnly, (hash, (byte)3)) ||
                !ReadFloat(record, hash, out float value))
            {
                return false;
            }

            assign(value);
            return true;
        }

        private bool Texture(RecordRef record, uint hash, Action<uint> assign)
        {
            if (!FindField(record, hash, 0xFF, out GdbField field) ||
                field.Raw == 0 || field.Raw == EnvHash.Null)
            {
                return false;
            }

            assign(field.Raw);
            return true;
        }

        public void CollectDaySetTimes(List<float> times)
        {
            // A Fable 3 theme has no day set: every parameter is its own timeline, evaluated
            // exactly at the time asked for, so callers take the direct path.
            if (_envThemes.Count != 0)
            {
                return;
            }

            RecordRef daySet = FindDaySet(true);
            if (!daySet.Valid)
            {
                daySet = FindDaySet(false);
            }

            if (!daySet.Valid)
            {
                return;
            }

            foreach (GdbField field in _views[daySet.Db].Fields(daySet.Index))
            {
                byte type = (byte)field.Type;
                if (type != 4 && type != 6 && type != 7)
                {
                    continue;
                }

                RecordRef entry = FieldToRecord(field);
                if (!entry.Valid)
                {
                    continue;
                }

                RecordRef theme = ResolveRecordField(entry, EnvHash.Theme);
                if (!theme.Valid)
                {
                    continue;
                }

                float time = 0.5f;
                if (ReadFloat(entry, EnvHash.TimeOfDay, out float readTime))
                {
                    time = Refii.Lighting.Chain.NormalizeTimeOfDay(readTime);
                }

                if (!times.Contains(time))
                {
                    times.Add(time);
                }
            }
        }

        private bool SelectThemeFromDaySet(RecordRef daySet, out RecordRef outTheme, out float outTime)
        {
            outTheme = default;
            outTime = 0.5f;

            if (!daySet.Valid)
            {
                return false;
            }

            bool found = false;
            float bestDistance = float.MaxValue;

            foreach (GdbField field in _views[daySet.Db].Fields(daySet.Index))
            {
                byte type = (byte)field.Type;
                if (type != 4 && type != 6 && type != 7)
                {
                    continue;
                }

                RecordRef entry = FieldToRecord(field);
                if (!entry.Valid)
                {
                    continue;
                }

                RecordRef theme = ResolveRecordField(entry, EnvHash.Theme);
                if (!theme.Valid)
                {
                    continue;
                }

                float time = 0.5f;
                if (ReadFloat(entry, EnvHash.TimeOfDay, out float readTime))
                {
                    time = Refii.Lighting.Chain.NormalizeTimeOfDay(readTime);
                }

                float distance = Math.Abs(time - TargetTime);
                distance = MathF.Min(distance, 1.0f - distance);
                if (!found || distance < bestDistance)
                {
                    bestDistance = distance;
                    outTheme = theme;
                    outTime = time;
                    found = true;
                }
            }

            return found;
        }

        public void CollectDaySetOwners(List<(string, uint, List<float>)> rows)
        {
            for (int db = 0; db < _views.Count; db++)
            {
                foreach (RecordRef level in LookupAllInDb(EnvHash.LevelData, db))
                {
                    rows.Add(($"db{db}:LevelData:{_views[db].RecordName(level.Index)}",
                        _views[db].RecordHash(level.Index),
                        new List<float> { ResolveRecordField(level, EnvHash.EnvironmentThemeDaySet).Valid ? 1f : 0f }));
                }

                for (int record = 0; record < _views[db].RecordCount; record++)
                {
                    var owner = new RecordRef(db, record, true);
                    if (!FindLocalField(owner, EnvHash.EnvironmentThemeDaySet, 0xFF, out _))
                    {
                        continue;
                    }

                    RecordRef daySet = ResolveRecordField(owner, EnvHash.EnvironmentThemeDaySet);
                    var times = new List<float>();
                    if (daySet.Valid)
                    {
                        foreach (GdbField field in _views[daySet.Db].Fields(daySet.Index))
                        {
                            byte type = (byte)field.Type;
                            if (type != 4 && type != 6 && type != 7)
                            {
                                continue;
                            }

                            RecordRef entry = FieldToRecord(field);
                            if (entry.Valid && ReadFloat(entry, EnvHash.TimeOfDay, out float t))
                            {
                                times.Add(ToLinearInput(t));
                            }
                        }
                    }

                    rows.Add(($"db{db}:{_views[db].RecordName(record)}", _views[db].RecordHash(record), times));
                }
            }
        }

        private RecordRef FindDaySet(bool levelOnly)
        {
            int dbCount = levelOnly ? Math.Min(_views.Count, 1) : _views.Count;

            for (int db = 0; db < dbCount; db++)
            {
                foreach (RecordRef level in LookupAllInDb(EnvHash.LevelData, db))
                {
                    RecordRef daySet = ResolveRecordField(level, EnvHash.EnvironmentThemeDaySet);
                    if (daySet.Valid)
                    {
                        return daySet;
                    }
                }
            }

            for (int db = 0; db < dbCount; db++)
            {
                for (int record = 0; record < _views[db].RecordCount; record++)
                {
                    var owner = new RecordRef(db, record, true);
                    if (!FindLocalField(owner, EnvHash.EnvironmentThemeDaySet, 0xFF, out _))
                    {
                        continue;
                    }

                    RecordRef daySet = ResolveRecordField(owner, EnvHash.EnvironmentThemeDaySet);
                    if (daySet.Valid)
                    {
                        return daySet;
                    }
                }
            }

            return default;
        }

        private RecordRef FirstRecordWithFieldInDb(uint fieldHash, int db)
        {
            if (db >= _views.Count)
            {
                return default;
            }

            for (int record = 0; record < _views[db].RecordCount; record++)
            {
                var candidate = new RecordRef(db, record, true);
                if (FindLocalField(candidate, fieldHash, 0xFF, out _))
                {
                    return candidate;
                }
            }

            return default;
        }

        private List<RecordRef> LookupAll(uint hash)
        {
            var result = new List<RecordRef>();
            if (hash == 0 || hash == EnvHash.Null)
            {
                return result;
            }

            for (int db = 0; db < _views.Count; db++)
            {
                if (_views[db].TryLookup(hash, out int index))
                {
                    result.Add(new RecordRef(db, index, true));
                }
            }

            return result;
        }

        private List<RecordRef> LookupAllInDb(uint hash, int db)
        {
            var result = new List<RecordRef>();
            if (hash == 0 || hash == EnvHash.Null || db >= _views.Count)
            {
                return result;
            }

            if (_views[db].TryLookup(hash, out int index))
            {
                result.Add(new RecordRef(db, index, true));
            }

            return result;
        }

        private RecordRef LookupFirst(uint hash)
        {
            List<RecordRef> all = LookupAll(hash);
            return all.Count == 0 ? default : all[0];
        }

        private bool FindLocalField(RecordRef record, uint fieldHash, byte expectedType, out GdbField found)
        {
            found = default;
            if (!record.Valid || record.Db >= _views.Count)
            {
                return false;
            }

            return _views[record.Db].TryFindFirst(
                record.Index, fieldHash, expectedType == 0xFF ? -1 : expectedType, out found);
        }

        private bool FindField(RecordRef record, uint fieldHash, byte expectedType, out GdbField found)
        {
            found = default;
            if (!record.Valid || record.Db >= _views.Count)
            {
                return false;
            }

            RecordRef current = record;
            var seen = new HashSet<long>();

            for (int depth = 0; depth < 64; depth++)
            {
                long key = ((long)current.Db << 48) ^ current.Index;
                if (!seen.Add(key))
                {
                    return false;
                }

                bool hasParent = FindLocalField(current, GdbView.HashParent, 0xFF, out GdbField parent);
                if (depth > 0 && !hasParent)
                {
                    return false;
                }

                if (FindLocalField(current, fieldHash, expectedType, out found))
                {
                    return true;
                }

                if (!hasParent)
                {
                    return false;
                }

                current = FieldToRecord(parent);
                if (!current.Valid)
                {
                    return false;
                }
            }

            return false;
        }

        public bool EvaluateClassic(float dayFraction, float[] output)
        {
            if (_envThemes.Count != 0)
            {
                return EvaluateClassicEnvTheme(dayFraction, output);
            }

            int count = Engine_ClassicThemeParams.Count;
            var global = new float[count * 4];
            var globalFlags = new float[count];
            ClassicDefaults(global, globalFlags);

            RecordRef globalRecord = default;
            foreach (RecordRef level in LookupAllInDb(EnvHash.LevelData, 0))
            {
                globalRecord = ResolveRecordField(level, EnvHash.EnvThemeGlobal);
                if (globalRecord.Valid)
                {
                    break;
                }
            }
            if (!globalRecord.Valid)
            {
                RecordRef owner = FirstRecordWithFieldInDb(EnvHash.EnvThemeGlobal, 0);
                globalRecord = owner.Valid ? ResolveRecordField(owner, EnvHash.EnvThemeGlobal) : default;
            }
            if (!globalRecord.Valid)
            {
                List<RecordRef> shared = LookupAll(EnvHash.EnvThemeGlobal);
                globalRecord = shared.Count != 0 ? shared[0] : default;
            }
            bool any = globalRecord.Valid && LoadClassic(globalRecord, global, globalFlags);

            // The day set's entries: (key, theme), the key from the entry's HH.MM TimeOfDay.
            var entries = new List<(float Key, RecordRef Theme)>();
            RecordRef daySet = FindDaySet(true);
            if (!daySet.Valid)
            {
                daySet = FindDaySet(false);
            }
            if (daySet.Valid)
            {
                foreach (GdbField field in _views[daySet.Db].Fields(daySet.Index))
                {
                    byte type = (byte)field.Type;
                    if (type != 4 && type != 6 && type != 7)
                    {
                        continue;
                    }
                    RecordRef entry = FieldToRecord(field);
                    RecordRef theme = entry.Valid ? ResolveRecordField(entry, EnvHash.Theme) : default;
                    if (!theme.Valid || !ReadFloat(entry, EnvHash.TimeOfDay, out float hhmm))
                    {
                        continue;
                    }
                    // `sub_82A46BD0`: n = (int)(TimeOfDay * 100); key = minutes / 60 / 24 + hours / 24.
                    int n = (int)(hhmm * 100.0f);
                    float key = (n % 100) * 0.016666668f * 0.041666668f + (n / 100) * 0.041666668f;
                    entries.Add((key, theme));
                }
            }
            entries.Sort((a, b) => a.Key.CompareTo(b.Key));

            Array.Copy(global, output, count * 4);
            if (entries.Count != 0)
            {
                var day = new float[count * 4];
                var dayFlags = new float[count];
                if (entries.Count == 1)
                {
                    LoadClassic(entries[0].Theme, day, dayFlags);
                }
                else
                {
                    // `sub_8218E2F0`: next = the first entry after the time (wrapping), prev the one
                    // before it (wrapping); a prev after the time is a day earlier, a next before
                    // it a day later; f = saturate((t - t0) / (t1 - t0)).
                    int next = entries.FindIndex(e => e.Key > dayFraction);
                    if (next < 0)
                    {
                        next = 0;
                    }
                    int prev = (next - 1 + entries.Count) % entries.Count;
                    float t0 = entries[prev].Key, t1 = entries[next].Key;
                    if (t0 > dayFraction)
                    {
                        t0 -= 1f;
                    }
                    if (t1 < dayFraction)
                    {
                        t1 += 1f;
                    }
                    float f = Math.Clamp((dayFraction - t0) / (t1 - t0), 0f, 1f);
                    var a = new float[count * 4];
                    var aFlags = new float[count];
                    var b = new float[count * 4];
                    var bFlags = new float[count];
                    LoadClassic(entries[prev].Theme, a, aFlags);
                    LoadClassic(entries[next].Theme, b, bFlags);
                    for (int i = 0; i < count * 4; i++)
                    {
                        day[i] = a[i] * (1f - f) + b[i] * f;
                    }
                    for (int i = 0; i < count; i++)
                    {
                        dayFlags[i] = aFlags[i] * (1f - f) + bFlags[i] * f;
                    }
                }
                // `sub_821D8588` at layer weight 1.
                for (int i = 0; i < count; i++)
                {
                    for (int lane = 0; lane < 4; lane++)
                    {
                        output[i * 4 + lane] = output[i * 4 + lane] * (1f - dayFlags[i]) + day[i * 4 + lane];
                    }
                }
                any = true;
            }
            return any;
        }

        /// <summary>`sub_82A44D08`: each parameter from its group record, by its field's name.</summary>
        private bool LoadClassic(RecordRef theme, float[] values, float[] flags)
        {
            bool any = false;
            var groups = new Dictionary<string, RecordRef>();
            foreach (Engine_ClassicThemeParams.Param param in Engine_ClassicThemeParams.All)
            {
                if (!groups.TryGetValue(param.Group, out RecordRef group))
                {
                    group = theme;
                    foreach (string part in param.Group.Split(','))
                    {
                        group = group.Valid ? ResolveRecordField(group, Refii.Core.Engine_GameHash.Fnv1(part)) : default;
                    }
                    groups[param.Group] = group;
                }
                if (!group.Valid)
                {
                    continue;
                }
                int at = param.Index * 4;
                uint field = Refii.Core.Engine_GameHash.Fnv1(param.Field);
                bool read = false;
                if (param.Type == 3)
                {
                    if (ReadFloat(group, field, out float v))
                    {
                        values[at] = v; values[at + 1] = 0f; values[at + 2] = 0f; values[at + 3] = 0f;
                        read = true;
                    }
                }
                else
                {
                    // A colour is a record of Red/Green/Blue(/Factor), or the same four fields
                    // flattened into the group as <Field>Red, <Field>Green, ... (both ship).
                    RecordRef colour = ResolveRecordField(group, field);
                    float r = 0f, g = 0f, bl = 0f, factor = 1f;
                    bool rgb = colour.Valid
                        ? ReadFloat(colour, EnvHash.Red, out r) && ReadFloat(colour, EnvHash.Green, out g) && ReadFloat(colour, EnvHash.Blue, out bl)
                        : ReadFloat(group, Refii.Core.Engine_GameHash.Fnv1(param.Field + "Red"), out r) &&
                          ReadFloat(group, Refii.Core.Engine_GameHash.Fnv1(param.Field + "Green"), out g) &&
                          ReadFloat(group, Refii.Core.Engine_GameHash.Fnv1(param.Field + "Blue"), out bl);
                    if (rgb)
                    {
                        if (param.Type == 2)
                        {
                            bool hasFactor = colour.Valid
                                ? ReadFloat(colour, EnvHash.Factor, out factor)
                                : ReadFloat(group, Refii.Core.Engine_GameHash.Fnv1(param.Field + "Factor"), out factor);
                            if (!hasFactor)
                            {
                                factor = 1f;
                            }
                        }
                        const float inv = 0.0039215689f;
                        values[at] = r * factor * inv; values[at + 1] = g * factor * inv; values[at + 2] = bl * factor * inv;
                        values[at + 3] = 1f;
                        read = true;
                    }
                }
                if (read)
                {
                    flags[param.Index] = 1f;
                    any = true;
                }
                else
                {
                    values[at] = values[at + 1] = values[at + 2] = values[at + 3] = 0f;
                    flags[param.Index] = 0f;
                }
            }
            return any;
        }

        /// <summary>`sub_82A45DD0`'s defaults for the parameters the classic frame reads.</summary>
        private static void ClassicDefaults(float[] values, float[] flags)
        {
            void Colour(int i, float r, float g, float b)
            {
                values[i * 4] = r; values[i * 4 + 1] = g; values[i * 4 + 2] = b; values[i * 4 + 3] = 1f; flags[i] = 1f;
            }
            void Float(int i, float v)
            {
                values[i * 4] = v; flags[i] = 1f;
            }
            Colour(0, 1f, 1f, 1f);
            Colour(2, 0.8f, 0.8f, 1f);
            Colour(3, 1f, 1f, 1f);
            Colour(4, 0f, 0f, 0f);
            Float(7, 10f); Float(8, 1f); Float(9, 1f);
            Colour(10, 1f, 1f, 1f); Colour(11, 1f, 1f, 1f);
            Float(12, 0f);
            Colour(13, 1f, 0.3f, 0.1f);
            Float(14, 1e6f); Float(15, 10f); Float(16, 0.3f); Float(17, 100f); Float(18, 0.3f);
            Colour(19, 1f, 1f, 1f);
            Float(20, 100f);
            Float(53, 50f); Float(54, 20f); Float(55, 70f); Float(56, 20f);
            Float(57, 0f); Float(58, 1f); Float(59, 70f); Float(60, 0f); Float(61, 0f);
            Float(62, 50f); Float(63, 0f); Float(64, 0f);
        }

        // THE FABLE 3 STACK, mapped onto the F2 theme structures by the parameter each F3 name
        // corresponds to (the F3 names are the F2 field names spelled out). Every value is the
        // guest's block at TargetTime — the stated key, or the title's default where no layer
        // states one — and a Has flag means a layer STATES it, as it does for a GDB theme.

        private Engine_EnvThemeFile.Block EnvBlock() =>
            Engine_EnvThemeFile.Evaluate(_envThemes, TargetTime);

        private static bool EnvFloat(Engine_EnvThemeFile.Block block, string name, Action<float> assign)
        {
            assign(block.Float(name));
            return block.IsStated(name);
        }

        private static bool EnvColour(Engine_EnvThemeFile.Block block, string name, float[] into)
        {
            System.Numerics.Vector4 v = block.Vector(name);
            into[0] = v.X;
            into[1] = v.Y;
            into[2] = v.Z;
            return block.IsStated(name);
        }

        private static bool EnvTexture(Engine_EnvThemeFile.Block block, string name, Action<uint> assign)
        {
            uint hash = block.TextureHash(name);
            if (hash == 0)
            {
                return false;
            }

            assign(hash);
            return true;
        }

        private bool ApplyEnvThemeSky(SkyTheme theme)
        {
            Engine_EnvThemeFile.Block b = EnvBlock();

            theme.HasSkyColour = EnvColour(b, "Sky Colour", theme.SkyColour);
            theme.HasComplementaryColour = EnvColour(b, "Sky Complementary Colour", theme.ComplementaryColour);
            theme.HasSunsetColour = EnvColour(b, "Sunset Colour", theme.SunsetColour);
            theme.HasFogColour = EnvColour(b, "Close Fog Colour", theme.FogColour);
            theme.HasMainLightColour = EnvColour(b, "Main Light Colour", theme.MainLightColour);
            theme.HasSunDiscColour = EnvColour(b, "Sun Disc Colour", theme.SunDiscColour);

            theme.HasSunIntensity = EnvFloat(b, "Sun Intensity", v => theme.SunIntensity = v);
            theme.HasRayleigh = EnvFloat(b, "Sky Beta Rayleigh Multiplier", v => theme.Rayleigh = v);
            theme.HasMie = EnvFloat(b, "Sky Beta Mie Multiplier", v => theme.Mie = v);
            theme.HasComplementaryBias = EnvFloat(b, "Sky Complementary Colour Bias", v => theme.ComplementaryBias = v);

            theme.HasNearFog = EnvFloat(b, "Near Distance", v => theme.NearDistance = v) |
                EnvFloat(b, "Near Density", v => theme.NearDensity = v);
            theme.HasFarFog = EnvFloat(b, "Far Distance", v => theme.FarDistance = v) |
                EnvFloat(b, "Far Density", v => theme.FarDensity = v);
            EnvFloat(b, "Close Fog Max Distance", v => theme.CloseFogMaxDistance = v);
            theme.HasFoggingStart = EnvFloat(b, "Fogging Start", v => theme.FoggingStart = v);

            // F3 has no SunAxisZOffset; its main light is one elevation timeline, so the four
            // F2 keyframes all carry its value at this time.
            theme.HasSunAxis = EnvFloat(b, "Sun Axis Elevation", v => theme.SunAxisElevation = v) |
                EnvFloat(b, "Sun Axis XY Rotation Offset", v => theme.SunAxisXyRotation = v);
            theme.HasMainLightElevation = EnvFloat(b, "Main Light Elevation", v =>
            {
                for (int i = 0; i < 4; i++)
                {
                    theme.MainLightElevation[i] = v;
                }
            });
            EnvFloat(b, "Main Light XY Rotation", v => theme.LightRigXyRotation = v);
            float timeFactor = b.Float("Time Factor");
            if (float.IsFinite(timeFactor) && timeFactor > 0f)
            {
                theme.MainLightTimeFactor = timeFactor;
            }

            theme.HasMoonParams = EnvFloat(b, "Moon Intensity", v => theme.MoonIntensity = v) |
                EnvFloat(b, "Moon Size", v => theme.MoonSize = v) |
                EnvFloat(b, "Moon Glare Intensity", v => theme.MoonGlareIntensity = v) |
                EnvFloat(b, "Moon Glare Size", v => theme.MoonGlareSize = v) |
                EnvFloat(b, "Moon Transparency", v => theme.MoonTransparency = v);
            theme.HasMoonAxis = EnvFloat(b, "Moon Axis Elevation", v => theme.MoonAxisElevation = v) |
                EnvFloat(b, "Moon Axis XY Rotation Offset", v => theme.MoonAxisXyRotation = v);
            theme.HasStarBrightness = EnvFloat(b, "Star Brightness", v => theme.StarBrightness = v);

            theme.HasSunDiscParams = EnvFloat(b, "Sun Disc Size", v => theme.SunDiscSize = v) |
                EnvFloat(b, "Sun Disc Intensity", v => theme.SunDiscIntensity = v);
            theme.HasSunBeams = EnvFloat(b, "Sun Beams Width", v => theme.SunBeamsWidth = v) |
                EnvFloat(b, "Sun Beams Height", v => theme.SunBeamsHeight = v) |
                EnvFloat(b, "Sun Beams Intensity", v => theme.SunBeamsIntensity = v);
            theme.HasSunGlare = EnvFloat(b, "Sun Glare Intensity", v => theme.SunGlareIntensity = v) |
                EnvFloat(b, "Sun Glare Size", v => theme.SunGlareSize = v);

            theme.HasSkyOverlayTexture = EnvTexture(b, "Sky Overlay Texture", h => theme.SkyOverlayTextureHash = h);
            theme.HasMoonTexture = EnvTexture(b, "Moon Texture", h => theme.MoonTextureHash = h);
            theme.HasMoonGlareTexture = EnvTexture(b, "Moon Glare Texture", h => theme.MoonGlareTextureHash = h);
            theme.HasSunDiscTexture = EnvTexture(b, "Sun Disc Texture", h => theme.SunDiscTextureHash = h);
            theme.HasSunBeamsTexture = EnvTexture(b, "Sun Beams Texture", h => theme.SunBeamsTextureHash = h);
            theme.HasSunGlareTexture = EnvTexture(b, "Sun Glare Texture", h => theme.SunGlareTextureHash = h);

            theme.SourceTimeOfDay = TargetTime - MathF.Floor(TargetTime);
            theme.HasAny = true;
            return true;
        }

        private bool ApplyEnvThemeWeather(WeatherTheme theme)
        {
            Engine_EnvThemeFile.Block b = EnvBlock();

            theme.HasRain = EnvFloat(b, "Rain Density", v => theme.RainDensity = v) |
                EnvFloat(b, "Rain Size", v => theme.RainSize = v);
            theme.HasSnow = EnvFloat(b, "Snow Fall Speed", v => theme.SnowFallSpeed = v) |
                EnvFloat(b, "Snow Size", v => theme.SnowSize = v);
            theme.HasWind = EnvFloat(b, "Wind Strength Minimum", v => theme.WindStrengthMin = v) |
                EnvFloat(b, "Wind Strength Maximum", v => theme.WindStrengthMax = v) |
                EnvFloat(b, "Wind Strength Variation", v => theme.WindStrengthVariation = v) |
                EnvFloat(b, "Wind XY Rotation Minimum", v => theme.WindXyRotationMin = v) |
                EnvFloat(b, "Wind XY Rotation Maximum", v => theme.WindXyRotationMax = v) |
                EnvFloat(b, "Wind Elevation Minimum", v => theme.WindElevationMin = v) |
                EnvFloat(b, "Wind Elevation Maximum", v => theme.WindElevationMax = v) |
                EnvFloat(b, "Wind Change Frequency", v => theme.WindChangeFrequency = v) |
                EnvFloat(b, "Wind Change Duration", v => theme.WindChangeDuration = v) |
                EnvFloat(b, "Wind Direction Variation", v => theme.WindDirectionVariation = v);
            // "Strength" (block +708) sits where F2's GroundMist group's Strength sits.
            theme.HasGroundMist = EnvFloat(b, "Strength", v => theme.GroundMistStrength = v);

            theme.SourceTimeOfDay = TargetTime - MathF.Floor(TargetTime);
            theme.HasAny = theme.HasRain || theme.HasSnow || theme.HasWind || theme.HasGroundMist;
            return theme.HasAny;
        }

        /// <summary>F3 carries two cloud layers, "… 1" and "… 2"; no position, and no
        /// translucency the guest registers.</summary>
        private bool ApplyEnvThemeClouds(CloudTheme theme)
        {
            Engine_EnvThemeFile.Block b = EnvBlock();
            int count = 0;
            for (int i = 0; i < 2; i++)
            {
                string n = (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
                CloudLayerTheme layer = theme.Layers[i];
                layer.HasDensityMap = EnvTexture(b, "Cloud Texture " + n, h => layer.DensityMapHash = h);
                layer.HasTransparency = EnvFloat(b, "Cloud Transparency " + n, v => layer.Transparency = v);
                layer.HasNormalStrength = EnvFloat(b, "Cloud Normal Strength " + n, v => layer.NormalStrength = v);
                layer.HasHeight = EnvFloat(b, "Cloud Height " + n, v => layer.Height = v);
                layer.HasAmbient = EnvFloat(b, "Cloud Ambient Light " + n, v => layer.AmbientLight = v);
                layer.HasBrightness = EnvFloat(b, "Cloud Brightness " + n, v => layer.Brightness = v);
                layer.HasSize = EnvFloat(b, "Cloud Size X " + n, v => layer.SizeX = v) |
                    EnvFloat(b, "Cloud Size Y " + n, v => layer.SizeY = v);
                layer.HasTextureScale = EnvFloat(b, "Cloud Scale X " + n, v => layer.TextureScaleX = v) |
                    EnvFloat(b, "Cloud Scale Y " + n, v => layer.TextureScaleY = v);
                layer.HasVelocity = EnvFloat(b, "Cloud Velocity X " + n, v => layer.VelocityX = v) |
                    EnvFloat(b, "Cloud Velocity Y " + n, v => layer.VelocityY = v);
                layer.Enabled = layer.HasDensityMap || layer.HasTransparency || layer.HasNormalStrength ||
                    layer.HasHeight || layer.HasAmbient || layer.HasBrightness || layer.HasSize ||
                    layer.HasTextureScale || layer.HasVelocity;
                if (layer.Enabled)
                {
                    count++;
                }
            }

            theme.LayerCount = count;
            theme.SourceTimeOfDay = TargetTime - MathF.Floor(TargetTime);
            theme.HasAny = count != 0;
            return theme.HasAny;
        }

        private static readonly (ToneMapParam Param, string Name)[] EnvToneMapNames =
        {
            (ToneMapParam.MiddleGray, "Exposure"),
            (ToneMapParam.Contrast, "Contrast"),
            (ToneMapParam.ContrastClamping, "Contrast Clamping"),
            (ToneMapParam.ContrastEnhancement, "Contrast Enhancement"),
            (ToneMapParam.ContrastEnhancementExponent, "Contrast Enhancement Exponent"),
            (ToneMapParam.ContrastMaxDeviation, "Contrast Enhancement Max Deviation"),
            (ToneMapParam.BloomThresholdAvgFactor, "Bloom Threshold"),
            (ToneMapParam.BloomFactor, "Bloom Strength"),
            (ToneMapParam.BloomBlurSize, "Bloom Blur Size"),
            (ToneMapParam.Saturation, "Saturation"),
            (ToneMapParam.SaturationMasked, "Saturation Masked"),
            (ToneMapParam.Brightness, "Brightness"),
            (ToneMapParam.BrightnessMasked, "Brightness Masked"),
            (ToneMapParam.RadialBlurFactor, "Radial Blur Factor"),
            (ToneMapParam.DofNearPlane, "Depth of Field Near Plane"),
            (ToneMapParam.DofNearFocalPlane, "Depth of Field Near Focal Plane"),
            (ToneMapParam.DofFarFocalPlane, "Depth of Field Far Focal Plane"),
            (ToneMapParam.DofFarPlane, "Depth of Field Far Plane"),
            (ToneMapParam.DofNearBlur, "Depth of Field Near Blur Amount"),
            (ToneMapParam.DofFarBlur, "Depth of Field Far Blur Amount"),
        };

        /// <summary>The F2 tone-map fields F3 has a parameter for; the rest keep the F2 guest's
        /// defaults, unmarked. F3's Filmic parameters have no F2 field and stay on the block.</summary>
        private bool ApplyEnvThemeToneMap(ToneMapTheme theme) => ApplyEnvThemeToneMap(EnvBlock(), TargetTime, theme);

        internal static bool ApplyEnvThemeToneMap(Engine_EnvThemeFile.Block b, float time, ToneMapTheme theme)
        {
            bool any = false;
            foreach ((ToneMapParam param, string name) in EnvToneMapNames)
            {
                theme[param] = b.Float(name);
                if (b.IsStated(name))
                {
                    theme.Present[(int)param] = true;
                    any = true;
                }
            }

            theme.SourceTimeOfDay = time - MathF.Floor(time);
            theme.HasAny = any;
            return any;
        }

        /// <summary>The classic block's parameters F3 has a counterpart for, by index.</summary>
        private static readonly (int Index, string Name)[] ClassicEnvThemeNames =
        {
            (0, "Main Light Colour"), (1, "Main Light Shadowed"), (2, "Ambient Colour"),
            (4, "Ambient Normal Map Darkening"), (5, "Distant Shadow Factor"),
            (6, "Enable Light Zone Shadow Culling"),
            (7, "Sun Intensity"), (8, "Sky Beta Rayleigh Multiplier"), (9, "Sky Beta Mie Multiplier"),
            (10, "Sky Colour"), (11, "Sky Complementary Colour"), (12, "Sky Complementary Colour Bias"),
            (13, "Sunset Colour"),
            (14, "Fogging Start"), (15, "Near Distance"), (16, "Near Density"), (17, "Far Distance"),
            (18, "Far Density"), (19, "Close Fog Colour"), (20, "Close Fog Max Distance"),
            (22, "Exposure"), (23, "Contrast"), (27, "Contrast Enhancement"),
            (28, "Contrast Enhancement Exponent"), (29, "Contrast Enhancement Max Deviation"),
            (30, "Contrast Clamping"), (34, "Bloom Threshold"), (35, "Bloom Strength"),
            (36, "Bloom Blur Size"), (44, "Saturation"), (45, "Saturation Masked"), (46, "Brightness"),
            (47, "Brightness Masked"), (48, "Radial Blur Factor"),
            (53, "Main Light Elevation"), (54, "Main Light Elevation"), (55, "Main Light Elevation"),
            (56, "Main Light Elevation"), (57, "Main Light XY Rotation"), (58, "Time Factor"),
            (59, "Sun Axis Elevation"), (61, "Sun Axis XY Rotation Offset"),
            (62, "Moon Axis Elevation"), (64, "Moon Axis XY Rotation Offset"), (65, "Moon Intensity"),
            (66, "Moon Size"), (67, "Moon Glare Intensity"), (68, "Moon Glare Size"),
            (69, "Moon Transparency"), (70, "Star Brightness"),
            (71, "Snow Size"), (72, "Snow Fall Speed"), (73, "Rain Size"), (74, "Rain Density"),
            (75, "Hide Occlusion Errors"),
            (76, "Wind Strength Minimum"), (77, "Wind Strength Maximum"), (78, "Wind Strength Variation"),
            (79, "Wind XY Rotation Minimum"), (80, "Wind Elevation Minimum"),
            (81, "Wind XY Rotation Maximum"), (82, "Wind Elevation Maximum"),
            (83, "Wind Direction Variation"), (84, "Wind Change Frequency"), (85, "Wind Change Duration"),
            (86, "Depth of Field Near Plane"), (87, "Depth of Field Near Focal Plane"),
            (88, "Depth of Field Far Focal Plane"), (89, "Depth of Field Far Plane"),
            (90, "Depth of Field Near Blur Amount"), (91, "Depth of Field Far Blur Amount"),
            (92, "Sun Disc Size"), (93, "Sun Disc Colour"), (94, "Sun Disc Intensity"),
            (95, "Sun Beams Width"), (96, "Sun Beams Height"), (97, "Sun Beams Intensity"),
            (100, "Sun Glare Intensity"), (101, "Sun Glare Size"), (102, "Disable Vista Water"),
            (133, "Cloud Transparency 1"), (134, "Cloud Height 1"), (135, "Cloud Normal Strength 1"),
            (137, "Cloud Ambient Light 1"), (138, "Cloud Brightness 1"), (141, "Cloud Size X 1"),
            (142, "Cloud Size Y 1"), (143, "Cloud Scale X 1"), (144, "Cloud Scale Y 1"),
            (145, "Cloud Velocity X 1"), (146, "Cloud Velocity Y 1"),
            (147, "Cloud Transparency 2"), (148, "Cloud Height 2"), (149, "Cloud Normal Strength 2"),
            (151, "Cloud Ambient Light 2"), (152, "Cloud Brightness 2"), (155, "Cloud Size X 2"),
            (156, "Cloud Size Y 2"), (157, "Cloud Scale X 2"), (158, "Cloud Scale Y 2"),
            (159, "Cloud Velocity X 2"), (160, "Cloud Velocity Y 2"),
            (189, "Strength"),
        };

        /// <summary>
        /// The classic block for a Fable 3 stack: the F3 block's value for every index above — a
        /// colour as (X, Y, Z, 1), its X/Y/Z already carrying the factor; a float as (v, 0, 0, 0)
        /// — and <see cref="ClassicDefaults"/> for the indices F3 has no parameter for.
        /// </summary>
        private bool EvaluateClassicEnvTheme(float dayFraction, float[] output)
        {
            int count = Engine_ClassicThemeParams.Count;
            Array.Clear(output, 0, count * 4);
            ClassicDefaults(output, new float[count]);

            Engine_EnvThemeFile.Block b = Engine_EnvThemeFile.Evaluate(_envThemes, dayFraction);
            foreach ((int index, string name) in ClassicEnvThemeNames)
            {
                int at = index * 4;
                if (Engine_ClassicThemeParams.All[index].Type == 3)
                {
                    output[at] = b.Float(name);
                    output[at + 1] = output[at + 2] = output[at + 3] = 0f;
                }
                else
                {
                    System.Numerics.Vector4 v = b.Vector(name);
                    output[at] = v.X;
                    output[at + 1] = v.Y;
                    output[at + 2] = v.Z;
                    output[at + 3] = 1f;
                }
            }

            return true;
        }

        private RecordRef FieldToRecord(GdbField field)
        {
            if (field.Raw == 0 || field.Raw == EnvHash.Null)
            {
                return default;
            }

            byte type = (byte)field.Type;
            if (type != 4 && type != 6 && type != 7)
            {
                return default;
            }

            return LookupFirst(field.Raw);
        }

        private RecordRef ResolveRecordField(RecordRef record, uint fieldHash)
        {
            return FindField(record, fieldHash, 0xFF, out GdbField field)
                ? FieldToRecord(field)
                : default;
        }

        private bool ReadFloat(RecordRef record, uint fieldHash, out float value)
        {
            value = 0f;
            if (!FindField(record, fieldHash, 3, out GdbField field))
            {
                return false;
            }

            if (!float.IsFinite(field.AsFloat))
            {
                return false;
            }

            value = field.AsFloat;
            return true;
        }

        private bool ReadColour(RecordRef record, uint red, uint green, uint blue, float[] outColour)
        {
            if (!ReadFloat(record, red, out float r) ||
                !ReadFloat(record, green, out float g) ||
                !ReadFloat(record, blue, out float b))
            {
                return false;
            }

            outColour[0] = ToLinearInput(r);
            outColour[1] = ToLinearInput(g);
            outColour[2] = ToLinearInput(b);
            return true;
        }

        private bool ReadFlatColour(
            RecordRef record, uint red, uint green, uint blue, uint factorHash, float[] outColour)
        {
            if (!ReadColour(record, red, green, blue, outColour))
            {
                return false;
            }

            if (factorHash != 0 &&
                ReadFloat(record, factorHash, out float factor) &&
                float.IsFinite(factor) && factor > 0f)
            {
                outColour[0] *= factor;
                outColour[1] *= factor;
                outColour[2] *= factor;
            }

            return true;
        }

        private bool ReadColourRecordField(RecordRef record, uint fieldHash, float[] outColour)
        {
            RecordRef colour = ResolveRecordField(record, fieldHash);
            if (!colour.Valid)
            {
                return false;
            }

            if (!ReadColour(colour, EnvHash.Red, EnvHash.Green, EnvHash.Blue, outColour))
            {
                return false;
            }

            if (ReadFloat(colour, EnvHash.Factor, out float factor) &&
                float.IsFinite(factor) && factor > 0f)
            {
                outColour[0] *= factor;
                outColour[1] *= factor;
                outColour[2] *= factor;
            }

            return true;
        }

        private static float ToLinearInput(float value) =>
            MathF.Max(value * (1f / 255f), 0f);
    }
}
