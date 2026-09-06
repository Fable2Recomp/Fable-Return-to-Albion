using System;
using System.Collections.Generic;
using Refii.Lighting;

namespace Refii.Level;

public static class Engine_ThemeTimeline
{
    public readonly record struct Bracket(float TimeA, float TimeB, float Weight);

    /// <summary>
    /// The extracted themes for one gdb chain, kept per day-set ENTRY time.
    ///
    /// EXTRACTION, NOT PARSING, IS THE COST. Caching the parsed <c>GdbView</c> removes the record
    /// walk but not the field resolution above it: every read hunts a hash through a record's
    /// parent chain, up to 64 levels, and a theme is a few hundred of those.
    ///
    /// The saving is available because the endpoints are DISCRETE. Time of day moves continuously,
    /// but a blend only ever samples the two day-set entries bracketing it, and a level ships about
    /// seven of those. So each entry is extracted once and every later re-evaluation is just the
    /// lerp between two finished themes.
    ///
    /// KEYED ON THE GDB LIST INSTANCE. Each <c>Engine_SkyEnvironmentNode</c> holds one stable list
    /// for the level it is showing, so the list's identity is the chain's identity — and a weak
    /// table means a level change drops the list and the memo goes with it.
    ///
    /// ONLY BRACKET ENDPOINTS ARE STORED. The fallback path below extracts at the raw target time,
    /// which is continuous, and caching that would grow without bound. It stays uncached.
    /// </summary>
    private sealed class Memo
    {
        public List<float>? Times;

        public readonly Dictionary<float, SkyTheme> Sky = new();
        public readonly Dictionary<float, LightingTheme> Lighting = new();
        public readonly Dictionary<float, CloudTheme> Clouds = new();
        public readonly Dictionary<float, ToneMapTheme> ToneMap = new();
        public readonly Dictionary<float, WaterTheme> Water = new();

        public readonly Dictionary<uint, List<float>> PaintedTimes = new();

        public readonly Dictionary<(uint Hash, float Time), LightingTheme> PaintedLighting = new();
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, Memo>
        Memos = new();

    private static Memo MemoFor(IReadOnlyList<byte[]> gdbs) => Memos.GetOrCreateValue(gdbs);

    /// <summary>The day-set entry times, resolved once per chain.</summary>
    private static bool CachedTimes(IReadOnlyList<byte[]> gdbs, out List<float> times)
    {
        Memo memo = MemoFor(gdbs);
        if (memo.Times is not null)
        {
            times = memo.Times;
            return times.Count != 0;
        }

        bool ok = Engine_EnvironmentTheme.TryGetDaySetTimes(gdbs, out times!);
        memo.Times = ok && times is not null ? times : new List<float>();
        times = memo.Times;
        return ok && times.Count != 0;
    }

    private static bool CachedSky(IReadOnlyList<byte[]> gdbs, float time, out SkyTheme theme)
    {
        Memo memo = MemoFor(gdbs);
        if (memo.Sky.TryGetValue(time, out theme!))
        {
            return true;
        }

        bool ok = Engine_EnvironmentTheme.TryExtractSky(gdbs, time, out theme);
        if (ok)
        {
            memo.Sky[time] = theme;
        }

        return ok;
    }

    private static bool CachedClouds(IReadOnlyList<byte[]> gdbs, float time, out CloudTheme theme)
    {
        Memo memo = MemoFor(gdbs);
        if (memo.Clouds.TryGetValue(time, out theme!))
        {
            return true;
        }

        bool ok = Engine_EnvironmentTheme.TryExtractClouds(gdbs, time, out theme);
        if (ok)
        {
            memo.Clouds[time] = theme;
        }

        return ok;
    }

    private static bool CachedToneMap(IReadOnlyList<byte[]> gdbs, float time, out ToneMapTheme theme)
    {
        Memo memo = MemoFor(gdbs);
        if (memo.ToneMap.TryGetValue(time, out theme!))
        {
            return true;
        }

        bool ok = Engine_EnvironmentTheme.TryExtractToneMap(gdbs, time, out theme);
        if (ok)
        {
            memo.ToneMap[time] = theme;
        }

        return ok;
    }

    private static bool CachedWater(IReadOnlyList<byte[]> gdbs, float time, out WaterTheme theme)
    {
        Memo memo = MemoFor(gdbs);
        if (memo.Water.TryGetValue(time, out theme!))
        {
            return true;
        }

        bool ok = Engine_WaterTheme.TryExtract(gdbs, time, out theme);
        if (ok)
        {
            memo.Water[time] = theme;
        }

        return ok;
    }

    private static bool CachedLighting(
        IReadOnlyList<byte[]> gdbs, float time, out LightingTheme theme)
    {
        Memo memo = MemoFor(gdbs);
        if (memo.Lighting.TryGetValue(time, out theme!))
        {
            return true;
        }

        bool ok = Engine_LightingTheme.TryExtract(gdbs, time, out theme);
        if (ok)
        {
            memo.Lighting[time] = theme;
        }

        return ok;
    }

    public static Bracket Resolve(IReadOnlyList<float> sortedTimes, float target)
    {
        if (sortedTimes.Count == 0)
        {
            return new Bracket(target, target, 0f);
        }

        if (sortedTimes.Count == 1)
        {
            return new Bracket(sortedTimes[0], sortedTimes[0], 0f);
        }

        float t = target - MathF.Floor(target);

        int after = 0;
        while (after < sortedTimes.Count && sortedTimes[after] <= t)
        {
            after++;
        }

        int ia = after - 1;
        int ib = after;
        if (ia < 0 || ib >= sortedTimes.Count)
        {
            float wrapA = sortedTimes[^1];
            float wrapB = sortedTimes[0];
            float wrapSpan = (1f - wrapA) + wrapB;
            float progressed = ia < 0 ? t + (1f - wrapA) : t - wrapA;
            float weight = wrapSpan > 1e-5f
                ? Math.Clamp(progressed / wrapSpan, 0f, 1f)
                : 0f;
            return new Bracket(wrapA, wrapB, weight);
        }

        float ta = sortedTimes[ia];
        float tb = sortedTimes[ib];
        float span = tb - ta;
        return new Bracket(ta, tb,
            span > 1e-5f ? Math.Clamp((t - ta) / span, 0f, 1f) : 0f);
    }

    public static bool TryExtractSkyBlended(
        IReadOnlyList<byte[]> gdbs, float targetTime, out SkyTheme theme)
    {
        if (!CachedTimes(gdbs, out List<float> times) ||
            times.Count < 2)
        {
            return Engine_EnvironmentTheme.TryExtractSky(gdbs, targetTime, out theme);
        }

        Bracket bracket = Resolve(times, targetTime);
        bool okA = CachedSky(gdbs, bracket.TimeA, out SkyTheme a);
        bool okB = CachedSky(gdbs, bracket.TimeB, out SkyTheme b);
        if (!okA || !okB)
        {
            theme = okA ? a : b;
            return okA || okB;
        }

        theme = BlendSky(a, b, bracket.Weight);
        theme.SourceTimeOfDay = targetTime - MathF.Floor(targetTime);
        return true;
    }

    public static bool TryExtractCloudsBlended(
        IReadOnlyList<byte[]> gdbs, float targetTime, out CloudTheme theme)
    {
        if (!CachedTimes(gdbs, out List<float> times) ||
            times.Count < 2)
        {
            return Engine_EnvironmentTheme.TryExtractClouds(gdbs, targetTime, out theme);
        }

        Bracket bracket = Resolve(times, targetTime);
        bool okA = CachedClouds(gdbs, bracket.TimeA, out CloudTheme a);
        bool okB = CachedClouds(gdbs, bracket.TimeB, out CloudTheme b);
        if (!okA || !okB)
        {
            theme = okA ? a : b;
            return okA || okB;
        }

        theme = BlendClouds(a, b, bracket.Weight);
        theme.SourceTimeOfDay = targetTime - MathF.Floor(targetTime);
        return true;
    }

    public static bool TryExtractLightingBlended(
        IReadOnlyList<byte[]> gdbs, float targetTime, out LightingTheme theme)
    {
        if (!CachedTimes(gdbs, out List<float> times) ||
            times.Count < 2)
        {
            return Engine_LightingTheme.TryExtract(gdbs, targetTime, out theme);
        }

        Bracket bracket = Resolve(times, targetTime);
        bool okA = CachedLighting(gdbs, bracket.TimeA, out LightingTheme a);
        bool okB = CachedLighting(gdbs, bracket.TimeB, out LightingTheme b);
        if (!okA || !okB)
        {
            theme = okA ? a : b;
            return okA || okB;
        }

        theme = BlendLighting(a, b, bracket.Weight);
        theme.SourceTimeOfDay = targetTime - MathF.Floor(targetTime);
        return true;
    }

    /// <summary>
    /// The lighting of ONE PAINTED day set at a time, blended between its own keyframes
    /// exactly as the level day set is; keyed by the day-set record hash the grid holds.
    /// </summary>
    public static bool TryExtractLightingBlended(
        IReadOnlyList<byte[]> gdbs, float targetTime, uint daySetHash, out LightingTheme theme)
    {
        Memo memo = MemoFor(gdbs);
        if (!memo.PaintedTimes.TryGetValue(daySetHash, out List<float>? times))
        {
            times = Engine_LightingTheme.DaySetTimes(gdbs, daySetHash);
            memo.PaintedTimes[daySetHash] = times;
        }

        if (times.Count == 0)
        {
            theme = new LightingTheme();
            return false;
        }

        if (times.Count < 2)
        {
            return CachedPainted(gdbs, daySetHash, times[0], out theme);
        }

        Bracket bracket = Resolve(times, targetTime);
        bool okA = CachedPainted(gdbs, daySetHash, bracket.TimeA, out LightingTheme a);
        bool okB = CachedPainted(gdbs, daySetHash, bracket.TimeB, out LightingTheme b);
        if (!okA || !okB)
        {
            theme = okA ? a : b;
            return okA || okB;
        }

        theme = BlendLighting(a, b, bracket.Weight);
        theme.SourceTimeOfDay = targetTime - MathF.Floor(targetTime);
        return true;
    }

    private static bool CachedPainted(
        IReadOnlyList<byte[]> gdbs, uint daySetHash, float time, out LightingTheme theme)
    {
        Memo memo = MemoFor(gdbs);
        if (memo.PaintedLighting.TryGetValue((daySetHash, time), out theme!))
        {
            return true;
        }

        bool ok = Engine_LightingTheme.TryExtract(gdbs, time, daySetHash, out theme);
        if (ok)
        {
            memo.PaintedLighting[(daySetHash, time)] = theme;
        }

        return ok;
    }

    public static bool TryExtractToneMapBlended(
        IReadOnlyList<byte[]> gdbs, float targetTime, out ToneMapTheme theme)
    {
        if (!CachedTimes(gdbs, out List<float> times) ||
            times.Count < 2)
        {
            return Engine_EnvironmentTheme.TryExtractToneMap(gdbs, targetTime, out theme);
        }

        Bracket bracket = Resolve(times, targetTime);
        bool okA = CachedToneMap(gdbs, bracket.TimeA, out ToneMapTheme a);
        bool okB = CachedToneMap(gdbs, bracket.TimeB, out ToneMapTheme b);
        if (!okA || !okB)
        {
            theme = okA ? a : b;
            return okA || okB;
        }

        theme = BlendToneMap(a, b, bracket.Weight);
        theme.SourceTimeOfDay = targetTime - MathF.Floor(targetTime);
        return true;
    }

    public static bool TryExtractWaterBlended(
        IReadOnlyList<byte[]> gdbs, float targetTime, out WaterTheme theme)
    {
        if (!CachedTimes(gdbs, out List<float> times) ||
            times.Count < 2)
        {
            return Engine_WaterTheme.TryExtract(gdbs, targetTime, out theme);
        }

        Bracket bracket = Resolve(times, targetTime);
        bool okA = CachedWater(gdbs, bracket.TimeA, out WaterTheme a);
        bool okB = CachedWater(gdbs, bracket.TimeB, out WaterTheme b);
        if (!okA || !okB)
        {
            theme = okA ? a : b;
            return okA || okB;
        }

        theme = BlendWater(a, b, bracket.Weight);
        theme.SourceTimeOfDay = targetTime - MathF.Floor(targetTime);
        return true;
    }

    public static WaterTheme BlendWater(WaterTheme a, WaterTheme b, float w)
    {
        var o = new WaterTheme
        {
            HasAny = a.HasAny || b.HasAny,
        };

        for (int i = 0; i < WaterThemeParam.Count; i++)
        {
            o.Stated[i] = a.Stated[i] || b.Stated[i];
            o.Values[i] = o.Stated[i]
                ? LerpStated(a.Stated[i], a.Values[i], b.Stated[i], b.Values[i], w)
                : WaterTheme.Defaults[i];
        }

        return o;
    }

    public static ToneMapTheme BlendToneMap(ToneMapTheme a, ToneMapTheme b, float w)
    {
        var o = new ToneMapTheme
        {
            HasAny = a.HasAny || b.HasAny,
        };

        for (int i = 0; i < ToneMapTheme.ParamCount; i++)
        {
            o.V[i] = Lerp(a.V[i], b.V[i], w);
            o.Present[i] = a.Present[i] || b.Present[i];
        }

        return o;
    }

    public static SkyTheme BlendSky(SkyTheme a, SkyTheme b, float w)
    {
        SkyTheme near = w < 0.5f ? a : b;
        var o = new SkyTheme
        {
            HasAny = a.HasAny || b.HasAny,
            HasSkyColour = a.HasSkyColour || b.HasSkyColour,
            HasComplementaryColour = a.HasComplementaryColour || b.HasComplementaryColour,
            HasSunsetColour = a.HasSunsetColour || b.HasSunsetColour,
            HasFogColour = a.HasFogColour || b.HasFogColour,
            HasSunIntensity = a.HasSunIntensity || b.HasSunIntensity,
            HasRayleigh = a.HasRayleigh || b.HasRayleigh,
            HasMie = a.HasMie || b.HasMie,
            HasComplementaryBias = a.HasComplementaryBias || b.HasComplementaryBias,
            HasNearFog = a.HasNearFog || b.HasNearFog,
            HasFarFog = a.HasFarFog || b.HasFarFog,
            HasFoggingStart = a.HasFoggingStart || b.HasFoggingStart,
            HasSunAxis = a.HasSunAxis || b.HasSunAxis,
            HasMoonAxis = a.HasMoonAxis || b.HasMoonAxis,
            HasMoonParams = a.HasMoonParams || b.HasMoonParams,
            HasStarBrightness = a.HasStarBrightness || b.HasStarBrightness,
            HasMainLightColour = a.HasMainLightColour || b.HasMainLightColour,
            HasSkyOverlayTexture = near.HasSkyOverlayTexture,
            HasMoonTexture = near.HasMoonTexture,
            HasMoonGlareTexture = near.HasMoonGlareTexture,
            HasSunDiscTexture = near.HasSunDiscTexture,
            HasSunBeamsTexture = near.HasSunBeamsTexture,
            HasSunGlareTexture = near.HasSunGlareTexture,
            HasSunDiscParams = a.HasSunDiscParams || b.HasSunDiscParams,
            HasSunDiscColour = a.HasSunDiscColour || b.HasSunDiscColour,
            HasSunBeams = a.HasSunBeams || b.HasSunBeams,
            HasSunGlare = a.HasSunGlare || b.HasSunGlare,
            HasMainLightElevation = a.HasMainLightElevation || b.HasMainLightElevation,

            SunIntensity = Lerp(a.SunIntensity, b.SunIntensity, w),
            Rayleigh = Lerp(a.Rayleigh, b.Rayleigh, w),
            Mie = Lerp(a.Mie, b.Mie, w),
            ComplementaryBias = Lerp(a.ComplementaryBias, b.ComplementaryBias, w),
            // PRESENCE-WEIGHTED, unlike the plain lerps around them. See `LerpStated`.
            NearDistance = LerpStated(a.HasNearFog, a.NearDistance, b.HasNearFog, b.NearDistance, w),
            NearDensity = LerpStated(a.HasNearFog, a.NearDensity, b.HasNearFog, b.NearDensity, w),
            FarDistance = LerpStated(a.HasFarFog, a.FarDistance, b.HasFarFog, b.FarDistance, w),
            FarDensity = LerpStated(a.HasFarFog, a.FarDensity, b.HasFarFog, b.FarDensity, w),
            CloseFogMaxDistance = LerpStated(
                a.CloseFogMaxDistance > 0f, a.CloseFogMaxDistance,
                b.CloseFogMaxDistance > 0f, b.CloseFogMaxDistance, w),
            FoggingStart = LerpStated(
                a.HasFoggingStart, a.FoggingStart, b.HasFoggingStart, b.FoggingStart, w),
            SunAxisElevation = Lerp(a.SunAxisElevation, b.SunAxisElevation, w),
            SunAxisZOffset = Lerp(a.SunAxisZOffset, b.SunAxisZOffset, w),
            SunAxisXyRotation = Lerp(a.SunAxisXyRotation, b.SunAxisXyRotation, w),
            MainLightTimeFactor = Lerp(a.MainLightTimeFactor, b.MainLightTimeFactor, w),
            MoonIntensity = Lerp(a.MoonIntensity, b.MoonIntensity, w),
            MoonSize = Lerp(a.MoonSize, b.MoonSize, w),
            MoonGlareIntensity = Lerp(a.MoonGlareIntensity, b.MoonGlareIntensity, w),
            MoonGlareSize = Lerp(a.MoonGlareSize, b.MoonGlareSize, w),
            MoonTransparency = Lerp(a.MoonTransparency, b.MoonTransparency, w),
            MoonAxisElevation = Lerp(a.MoonAxisElevation, b.MoonAxisElevation, w),
            MoonAxisZOffset = Lerp(a.MoonAxisZOffset, b.MoonAxisZOffset, w),
            MoonAxisXyRotation = Lerp(a.MoonAxisXyRotation, b.MoonAxisXyRotation, w),
            StarBrightness = Lerp(a.StarBrightness, b.StarBrightness, w),
            SunDiscSize = Lerp(a.SunDiscSize, b.SunDiscSize, w),
            SunDiscIntensity = Lerp(a.SunDiscIntensity, b.SunDiscIntensity, w),
            SunBeamsWidth = Lerp(a.SunBeamsWidth, b.SunBeamsWidth, w),
            SunBeamsHeight = Lerp(a.SunBeamsHeight, b.SunBeamsHeight, w),
            SunBeamsIntensity = Lerp(a.SunBeamsIntensity, b.SunBeamsIntensity, w),
            SunGlareIntensity = Lerp(a.SunGlareIntensity, b.SunGlareIntensity, w),
            SunGlareSize = Lerp(a.SunGlareSize, b.SunGlareSize, w),
            LightRigXyRotation = Lerp(a.LightRigXyRotation, b.LightRigXyRotation, w),

            SkyOverlayTextureHash = near.SkyOverlayTextureHash,
            MoonTextureHash = near.MoonTextureHash,
            MoonGlareTextureHash = near.MoonGlareTextureHash,
            SunDiscTextureHash = near.SunDiscTextureHash,
            SunBeamsTextureHash = near.SunBeamsTextureHash,
            SunGlareTextureHash = near.SunGlareTextureHash,
        };

        LerpInto(a.SkyColour, b.SkyColour, w, o.SkyColour);
        LerpInto(a.ComplementaryColour, b.ComplementaryColour, w, o.ComplementaryColour);
        LerpInto(a.SunsetColour, b.SunsetColour, w, o.SunsetColour);
        LerpInto(a.FogColour, b.FogColour, w, o.FogColour);
        LerpInto(a.MainLightColour, b.MainLightColour, w, o.MainLightColour);
        LerpInto(a.SunDiscColour, b.SunDiscColour, w, o.SunDiscColour);
        for (int i = 0; i < 4; i++)
        {
            o.MainLightElevation[i] =
                Lerp(a.MainLightElevation[i], b.MainLightElevation[i], w);
        }

        return o;
    }

    public static CloudTheme BlendClouds(CloudTheme a, CloudTheme b, float w)
    {
        CloudTheme near = w < 0.5f ? a : b;
        var o = new CloudTheme
        {
            HasAny = a.HasAny || b.HasAny,
        };

        int count = 0;
        for (int i = 0; i < 4; i++)
        {
            CloudLayerTheme la = a.Layers[i];
            CloudLayerTheme lb = b.Layers[i];
            CloudLayerTheme lo = o.Layers[i];
            CloudLayerTheme ln = near.Layers[i];

            lo.Enabled = la.Enabled || lb.Enabled;
            lo.HasDensityMap = ln.HasDensityMap;
            lo.DensityMapHash = ln.DensityMapHash;
            if (!ln.HasDensityMap)
            {
                CloudLayerTheme other = near == a ? lb : la;
                lo.HasDensityMap = other.HasDensityMap;
                lo.DensityMapHash = other.DensityMapHash;
            }

            lo.HasPosition = la.HasPosition || lb.HasPosition;
            lo.HasSize = la.HasSize || lb.HasSize;
            lo.HasTextureScale = la.HasTextureScale || lb.HasTextureScale;
            lo.HasVelocity = la.HasVelocity || lb.HasVelocity;
            lo.HasHeight = la.HasHeight || lb.HasHeight;
            lo.HasTransparency = la.HasTransparency || lb.HasTransparency;
            lo.HasBrightness = la.HasBrightness || lb.HasBrightness;
            lo.HasAmbient = la.HasAmbient || lb.HasAmbient;
            lo.HasNormalStrength = la.HasNormalStrength || lb.HasNormalStrength;
            lo.HasTranslucencyStrength =
                la.HasTranslucencyStrength || lb.HasTranslucencyStrength;

            float ta = la.Enabled ? la.Transparency : 0f;
            float tb = lb.Enabled ? lb.Transparency : 0f;
            lo.Transparency = Lerp(ta, tb, w);

            lo.PositionX = LerpGeom(
                la.PositionX, lb.PositionX, la.Enabled, lb.Enabled, w);
            lo.PositionY = LerpGeom(
                la.PositionY, lb.PositionY, la.Enabled, lb.Enabled, w);
            lo.SizeX = LerpGeom(la.SizeX, lb.SizeX, la.Enabled, lb.Enabled, w);
            lo.SizeY = LerpGeom(la.SizeY, lb.SizeY, la.Enabled, lb.Enabled, w);
            lo.TextureScaleX = LerpGeom(
                la.TextureScaleX, lb.TextureScaleX, la.Enabled, lb.Enabled, w);
            lo.TextureScaleY = LerpGeom(
                la.TextureScaleY, lb.TextureScaleY, la.Enabled, lb.Enabled, w);
            lo.VelocityX = LerpGeom(la.VelocityX, lb.VelocityX, la.Enabled, lb.Enabled, w);
            lo.VelocityY = LerpGeom(la.VelocityY, lb.VelocityY, la.Enabled, lb.Enabled, w);
            lo.Height = LerpGeom(la.Height, lb.Height, la.Enabled, lb.Enabled, w);
            lo.Brightness = LerpGeom(la.Brightness, lb.Brightness, la.Enabled, lb.Enabled, w);
            lo.AmbientLight = LerpGeom(
                la.AmbientLight, lb.AmbientLight, la.Enabled, lb.Enabled, w);
            lo.NormalStrength = LerpGeom(
                la.NormalStrength, lb.NormalStrength, la.Enabled, lb.Enabled, w);
            lo.TranslucencyStrength = LerpGeom(
                la.TranslucencyStrength, lb.TranslucencyStrength,
                la.Enabled, lb.Enabled, w);

            if (lo.Enabled)
            {
                count++;
            }
        }

        o.LayerCount = count;
        return o;
    }

    public static LightingTheme BlendLighting(LightingTheme a, LightingTheme b, float w)
    {
        var o = new LightingTheme
        {
            HasAny = a.HasAny || b.HasAny,
            HasMainLightColour = a.HasMainLightColour || b.HasMainLightColour,
            HasMainLightShadowed = a.HasMainLightShadowed || b.HasMainLightShadowed,
            HasAmbientColour = a.HasAmbientColour || b.HasAmbientColour,
            HasLightmapColour = a.HasLightmapColour || b.HasLightmapColour,
            HasDarkeningColour = a.HasDarkeningColour || b.HasDarkeningColour,

            // SunIntensity is NOT blended here — it is registered against the Sky group, not
            // Lighting, so it rides on SkyTheme and BlendSky already interpolates it.
            MainLightShadowed = Lerp(a.MainLightShadowed, b.MainLightShadowed, w),
        };

        LerpInto(a.MainLightColour, b.MainLightColour, w, o.MainLightColour);
        LerpInto(a.AmbientColour, b.AmbientColour, w, o.AmbientColour);
        LerpInto(a.LightmapColour, b.LightmapColour, w, o.LightmapColour);
        LerpInto(a.DarkeningColour, b.DarkeningColour, w, o.DarkeningColour);
        return o;
    }

    private static float LerpGeom(float a, float b, bool hasA, bool hasB, float w)
    {
        if (hasA && hasB)
        {
            return Lerp(a, b, w);
        }

        return hasA ? a : b;
    }

    /// <summary>
    /// Blends two day-set entries for a field ONE OF THEM MAY NOT STATE.
    /// </summary>
    /// <remarks>
    /// THE GUEST DOES NOT LERP UNSTATED FIELDS TOWARD ZERO, and a plain lerp does. Its blend
    /// carries a presence flag per slot and weights the source by it (<c>sub_821D8588</c>):
    ///
    ///     821d8608  lfsx    f0, r10, r28        ; the SOURCE's presence flag
    ///     821d860c  fnmsubs f13, f0, f30, f31   ; 1 - srcFlag*weight
    ///     821d862c  vmulfp128 v8, v0, v11       ; src * weight
    ///     821d8630  vmaddfp   v7, v13, v8, v9
    ///
    /// so a source that does not state the slot contributes nothing AND does not scale the
    /// destination down — the value from the entry that does state it survives intact. With both
    /// flags set this reduces to exactly the lerp below, which is why the difference only shows on
    /// a partially-stated day set.
    ///
    /// It bites hardest on fog. `FarDistance` sits in the denominator of the optical-depth power
    /// law and inside the logarithm that solves its exponent, so dragging it from a stated 2000
    /// toward an unstated 0 does not thin the fog proportionally — it collapses the far range and
    /// the depth runs away. That reads as a wall of fog with a curve that is arithmetically
    /// correct.
    ///
    /// APPLIED TO THE FOG FIELDS ONLY, so far. The other three dozen fields here have the same
    /// defect and want the same treatment; they are left alone in this pass because none of them
    /// has been shown to misbehave and changing the sky and light blend on suspicion is how a fix
    /// for one thing becomes a regression in another.
    /// </remarks>
    private static float LerpStated(bool aHas, float a, bool bHas, float b, float w)
    {
        if (aHas && bHas)
        {
            return Lerp(a, b, w);
        }

        if (aHas)
        {
            return a;
        }

        return bHas ? b : 0f;
    }

    private static float Lerp(float a, float b, float w)
    {
        if (!float.IsFinite(a))
        {
            return b;
        }

        if (!float.IsFinite(b))
        {
            return a;
        }

        return a + ((b - a) * w);
    }

    private static void LerpInto(float[] a, float[] b, float w, float[] target)
    {
        for (int i = 0; i < target.Length && i < a.Length && i < b.Length; i++)
        {
            target[i] = Lerp(a[i], b[i], w);
        }
    }
}
