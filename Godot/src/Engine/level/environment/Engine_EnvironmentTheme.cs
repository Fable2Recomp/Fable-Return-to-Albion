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
    public float NearDistance { get; set; }
    public float NearDensity { get; set; }
    public float FarDistance { get; set; }
    public float FarDensity { get; set; }
    public float CloseFogMaxDistance { get; set; }
    public float FoggingStart { get; set; }
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

    private sealed class Extractor
    {
        private readonly List<GdbView> _views = new();

        public float TargetTime { get; set; } = 0.5f;

        public static Extractor? Create(IReadOnlyList<byte[]> gdbs)
        {
            var extractor = new Extractor();
            foreach (byte[] bytes in gdbs)
            {
                // Shared: this runs about nine times per re-evaluation over the same blobs.
                if (bytes is not null && GdbView.TryParseShared(bytes, out GdbView view, out _))
                {
                    extractor._views.Add(view);
                }
            }

            return extractor._views.Count == 0 ? null : extractor;
        }

        public bool ExtractSky(SkyTheme theme)
        {
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

        public bool ExtractWeather(WeatherTheme theme)
        {
            bool applied = RunChain(
                theme,
                (t, r) => ApplyWeatherThemeRecord(r, (WeatherTheme)t),
                (t, owner, hash) =>
                {
                    RecordRef target = ResolveRecordField(owner, hash);
                    return target.Valid && ApplyWeatherThemeRecord(target, (WeatherTheme)t);
                },
                time => theme.SourceTimeOfDay = time);

            theme.HasAny = applied;
            return applied;
        }

        private bool RunChain(
            object theme,
            Func<object, RecordRef, bool> applyRecord,
            Func<object, RecordRef, uint, bool> applyField,
            Action<float> setTime)
        {
            bool applied = false;

            RecordRef daySet = FindDaySet(true);
            if (daySet.Valid && SelectThemeFromDaySet(daySet, out RecordRef dayTheme, out float time))
            {
                setTime(time);
                applied |= applyRecord(theme, dayTheme);
            }

            if (!applied)
            {
                foreach (RecordRef level in LookupAllInDb(EnvHash.LevelData, 0))
                {
                    applied |= applyField(theme, level, EnvHash.EnvThemeGlobal);
                }
            }

            if (!applied)
            {
                RecordRef owner = FirstRecordWithFieldInDb(EnvHash.EnvThemeGlobal, 0);
                if (owner.Valid)
                {
                    applied |= applyField(theme, owner, EnvHash.EnvThemeGlobal);
                }
            }

            if (!applied)
            {
                foreach (RecordRef global in LookupAllInDb(EnvHash.EnvThemeGlobal, 0))
                {
                    applied |= applyRecord(theme, global);
                }
            }

            if (!applied)
            {
                daySet = FindDaySet(false);
                if (daySet.Valid &&
                    SelectThemeFromDaySet(daySet, out RecordRef fallbackTheme, out float fallbackTime))
                {
                    setTime(fallbackTime);
                    applied |= applyRecord(theme, fallbackTheme);
                }
            }

            if (!applied)
            {
                foreach (RecordRef global in LookupAll(EnvHash.EnvThemeGlobal))
                {
                    applied |= applyRecord(theme, global);
                }
            }

            return applied;
        }

        public bool ExtractClouds(CloudTheme theme)
        {
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

        public bool ExtractToneMap(ToneMapTheme theme)
        {
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

            if (Godot.OS.GetEnvironment("REFII_FOG_TRACE") == "1")
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

        private bool ApplySkyThemeRecord(RecordRef themeRecord, SkyTheme theme)
        {
            if (!themeRecord.Valid)
            {
                return false;
            }

            bool any = false;

            RecordRef sky = ResolveRecordField(themeRecord, EnvHash.Sky);
            any |= sky.Valid ? ApplySkyRecord(sky, theme) : ApplySkyRecord(themeRecord, theme);

            RecordRef fog = ResolveRecordField(themeRecord, EnvHash.Fogging);
            any |= fog.Valid ? ApplyFogRecord(fog, theme) : ApplyFogRecord(themeRecord, theme);

            RecordRef sun = ResolveRecordField(themeRecord, EnvHash.SunRecord);
            if (sun.Valid)
            {
                any |= ApplySunRecord(sun, theme);
            }
            else if (sky.Valid)
            {
                any |= ApplySunRecord(sky, theme);
            }

            RecordRef lighting = ResolveRecordField(themeRecord, EnvHash.Lighting);
            if (lighting.Valid)
            {
                var colour = new float[3];
                if (ReadColourRecordField(lighting, EnvHash.MainLightColour, colour) ||
                    ReadFlatColour(lighting, EnvHash.MainLightColourRed, EnvHash.MainLightColourGreen,
                        EnvHash.MainLightColourBlue, EnvHash.MainLightColourFactor, colour))
                {
                    Array.Copy(colour, theme.MainLightColour, 3);
                    theme.HasMainLightColour = true;
                    any = true;
                }

                // The sun's PATH. These live in the Lighting record, not in Sky — the guest
                // registers MainLightElevation0/6/12/18, TimeFactor and LightRigXYRotation
                // against the "Lighting" group and only SunAxis* against "Sky".
                any |= Param(lighting, EnvHash.MainLightElevation0,
                    v => { theme.HasMainLightElevation = true; theme.MainLightElevation[0] = v; });
                any |= Param(lighting, EnvHash.MainLightElevation6,
                    v => { theme.HasMainLightElevation = true; theme.MainLightElevation[1] = v; });
                any |= Param(lighting, EnvHash.MainLightElevation12,
                    v => { theme.HasMainLightElevation = true; theme.MainLightElevation[2] = v; });
                any |= Param(lighting, EnvHash.MainLightElevation18,
                    v => { theme.HasMainLightElevation = true; theme.MainLightElevation[3] = v; });
                any |= Param(lighting, EnvHash.LightRigXyRotation,
                    v => theme.LightRigXyRotation = v);

                if (ReadFloat(lighting, EnvHash.TimeFactor, out float lightingTimeFactor) &&
                    float.IsFinite(lightingTimeFactor) && lightingTimeFactor > 0f)
                {
                    theme.MainLightTimeFactor = lightingTimeFactor;
                }
            }

            return any;
        }

        private bool ApplySkyRecord(RecordRef sky, SkyTheme theme)
        {
            if (!sky.Valid)
            {
                return false;
            }

            bool any = false;
            var colour = new float[3];

            if (ReadColourRecordField(sky, EnvHash.SkyColour, colour) ||
                ReadFlatColour(sky, EnvHash.SkyColourRed, EnvHash.SkyColourGreen,
                    EnvHash.SkyColourBlue, EnvHash.SkyColourFactor, colour))
            {
                Array.Copy(colour, theme.SkyColour, 3);
                theme.HasSkyColour = true;
                any = true;
            }

            if (ReadColourRecordField(sky, EnvHash.SkyComplementaryColour, colour) ||
                ReadFlatColour(sky, EnvHash.SkyComplementaryColourRed,
                    EnvHash.SkyComplementaryColourGreen, EnvHash.SkyComplementaryColourBlue,
                    EnvHash.SkyComplementaryColourFactor, colour))
            {
                Array.Copy(colour, theme.ComplementaryColour, 3);
                theme.HasComplementaryColour = true;
                any = true;
            }

            if (ReadColourRecordField(sky, EnvHash.SunsetColour, colour) ||
                ReadFlatColour(sky, EnvHash.SunsetColourRed, EnvHash.SunsetColourGreen,
                    EnvHash.SunsetColourBlue, EnvHash.SunsetColourFactor, colour))
            {
                Array.Copy(colour, theme.SunsetColour, 3);
                theme.HasSunsetColour = true;
                any = true;
            }

            any |= Param(sky, EnvHash.SunIntensity, v => { theme.HasSunIntensity = true; theme.SunIntensity = v; });
            any |= Param(sky, EnvHash.SkyBetaRayleighMultiplier, v => { theme.HasRayleigh = true; theme.Rayleigh = v; });
            any |= Param(sky, EnvHash.SkyBetaMieMultiplier, v => { theme.HasMie = true; theme.Mie = v; });
            any |= Param(sky, EnvHash.SkyComplementaryColourBias, v => { theme.HasComplementaryBias = true; theme.ComplementaryBias = v; });
            any |= Param(sky, EnvHash.SunAxisElevation, v => { theme.HasSunAxis = true; theme.SunAxisElevation = v; });
            any |= Param(sky, EnvHash.SunAxisZOffset, v => { theme.HasSunAxis = true; theme.SunAxisZOffset = v; });
            any |= Param(sky, EnvHash.SunAxisXyRotationOffset, v => { theme.HasSunAxis = true; theme.SunAxisXyRotation = v; });

            if (ReadFloat(sky, EnvHash.TimeFactor, out float timeFactor) &&
                float.IsFinite(timeFactor) && timeFactor > 0f)
            {
                theme.MainLightTimeFactor = timeFactor;
            }

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

            any |= Param(sky, EnvHash.MoonIntensity, v => { theme.HasMoonParams = true; theme.MoonIntensity = v; });
            any |= Param(sky, EnvHash.MoonSize, v => { theme.HasMoonParams = true; theme.MoonSize = v; });
            any |= Param(sky, EnvHash.MoonGlareIntensity, v => { theme.HasMoonParams = true; theme.MoonGlareIntensity = v; });
            any |= Param(sky, EnvHash.MoonGlareSize, v => { theme.HasMoonParams = true; theme.MoonGlareSize = v; });
            any |= Param(sky, EnvHash.MoonTransparency, v => { theme.HasMoonParams = true; theme.MoonTransparency = v; });
            any |= Param(sky, EnvHash.MoonAxisElevation, v => { theme.HasMoonAxis = true; theme.MoonAxisElevation = v; });
            any |= Param(sky, EnvHash.MoonAxisZOffset, v => { theme.HasMoonAxis = true; theme.MoonAxisZOffset = v; });
            any |= Param(sky, EnvHash.MoonAxisXyRotationOffset, v => { theme.HasMoonAxis = true; theme.MoonAxisXyRotation = v; });
            any |= Param(sky, EnvHash.StarBrightness, v => { theme.HasStarBrightness = true; theme.StarBrightness = v; });

            return any;
        }

        private bool ApplySunRecord(RecordRef sun, SkyTheme theme)
        {
            if (!sun.Valid)
            {
                return false;
            }

            bool any = false;

            any |= Param(sun, EnvHash.DiscSize, v => { theme.HasSunDiscParams = true; theme.SunDiscSize = v; });
            any |= Param(sun, EnvHash.DiscIntensity, v => { theme.HasSunDiscParams = true; theme.SunDiscIntensity = v; });

            var colour = new float[3];
            if (ReadColourRecordField(sun, EnvHash.DiscColour, colour) ||
                ReadFlatColour(sun, EnvHash.DiscColourRed, EnvHash.DiscColourGreen,
                    EnvHash.DiscColourBlue, EnvHash.DiscColourFactor, colour))
            {
                Array.Copy(colour, theme.SunDiscColour, 3);
                theme.HasSunDiscColour = true;
                any = true;
            }

            any |= Param(sun, EnvHash.SunBeamsWidth, v => { theme.HasSunBeams = true; theme.SunBeamsWidth = v; });
            any |= Param(sun, EnvHash.SunBeamsHeight, v => { theme.HasSunBeams = true; theme.SunBeamsHeight = v; });
            any |= Param(sun, EnvHash.SunBeamsIntensity, v => { theme.HasSunBeams = true; theme.SunBeamsIntensity = v; });
            any |= Param(sun, EnvHash.GlareIntensity, v => { theme.HasSunGlare = true; theme.SunGlareIntensity = v; });
            any |= Param(sun, EnvHash.GlareSize, v => { theme.HasSunGlare = true; theme.SunGlareSize = v; });

            any |= Texture(sun, EnvHash.DiscTexture,
                h => { theme.HasSunDiscTexture = true; theme.SunDiscTextureHash = h; });
            any |= Texture(sun, EnvHash.SunBeamsTexture,
                h => { theme.HasSunBeamsTexture = true; theme.SunBeamsTextureHash = h; });
            any |= Texture(sun, EnvHash.GlareTexture,
                h => { theme.HasSunGlareTexture = true; theme.SunGlareTextureHash = h; });

            return any;
        }

        private bool ApplyFogRecord(RecordRef fog, SkyTheme theme)
        {
            if (!fog.Valid)
            {
                return false;
            }

            bool any = false;
            var colour = new float[3];

            if (ReadColourRecordField(fog, EnvHash.CloseFogColour, colour) ||
                ReadFlatColour(fog, EnvHash.CloseFogColourRed, EnvHash.CloseFogColourGreen,
                    EnvHash.CloseFogColourBlue, EnvHash.CloseFogColourFactor, colour))
            {
                Array.Copy(colour, theme.FogColour, 3);
                theme.HasFogColour = true;
                any = true;
            }

            any |= Param(fog, EnvHash.NearDistance, v => { theme.NearDistance = v; theme.HasNearFog = true; });
            any |= Param(fog, EnvHash.NearDensity, v => { theme.NearDensity = v; theme.HasNearFog = true; });
            any |= Param(fog, EnvHash.FarDistance, v => { theme.FarDistance = v; theme.HasFarFog = true; });
            any |= Param(fog, EnvHash.FarDensity, v => { theme.FarDensity = v; theme.HasFarFog = true; });
            any |= Param(fog, EnvHash.CloseFogMaxDistance, v => theme.CloseFogMaxDistance = v);
            any |= Param(fog, EnvHash.FoggingStart, v => { theme.FoggingStart = v; theme.HasFoggingStart = true; });

            if (Godot.OS.GetEnvironment("REFII_FOG_TRACE") == "1")
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

        private bool Param(RecordRef record, uint hash, Action<float> assign)
        {
            if (!ReadFloat(record, hash, out float value))
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

            foreach (GdbField field in _views[record.Db].Fields(record.Index))
            {
                if (field.Hash != fieldHash)
                {
                    continue;
                }

                if (expectedType != 0xFF && (byte)field.Type != expectedType)
                {
                    continue;
                }

                found = field;
                return true;
            }

            return false;
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

                if (FindLocalField(current, fieldHash, expectedType, out found))
                {
                    return true;
                }

                if (!FindLocalField(current, GdbView.HashParent, 0xFF, out GdbField parent))
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
            Math.Clamp(value * (1f / 255f), 0f, 1f);
    }
}
