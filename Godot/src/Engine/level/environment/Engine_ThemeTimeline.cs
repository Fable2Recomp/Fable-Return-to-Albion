using System;
using System.Collections.Generic;
using Refii.Lighting;

namespace Refii.Level;

public static class Engine_ThemeTimeline
{
    public readonly record struct Bracket(float TimeA, float TimeB, float Weight);

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
        if (!Engine_EnvironmentTheme.TryGetDaySetTimes(gdbs, out List<float> times) ||
            times.Count < 2)
        {
            return Engine_EnvironmentTheme.TryExtractSky(gdbs, targetTime, out theme);
        }

        Bracket bracket = Resolve(times, targetTime);
        bool okA = Engine_EnvironmentTheme.TryExtractSky(gdbs, bracket.TimeA, out SkyTheme a);
        bool okB = Engine_EnvironmentTheme.TryExtractSky(gdbs, bracket.TimeB, out SkyTheme b);
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
        if (!Engine_EnvironmentTheme.TryGetDaySetTimes(gdbs, out List<float> times) ||
            times.Count < 2)
        {
            return Engine_EnvironmentTheme.TryExtractClouds(gdbs, targetTime, out theme);
        }

        Bracket bracket = Resolve(times, targetTime);
        bool okA = Engine_EnvironmentTheme.TryExtractClouds(gdbs, bracket.TimeA, out CloudTheme a);
        bool okB = Engine_EnvironmentTheme.TryExtractClouds(gdbs, bracket.TimeB, out CloudTheme b);
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
        if (!Engine_EnvironmentTheme.TryGetDaySetTimes(gdbs, out List<float> times) ||
            times.Count < 2)
        {
            return Engine_LightingTheme.TryExtract(gdbs, targetTime, out theme);
        }

        Bracket bracket = Resolve(times, targetTime);
        bool okA = Engine_LightingTheme.TryExtract(gdbs, bracket.TimeA, out LightingTheme a);
        bool okB = Engine_LightingTheme.TryExtract(gdbs, bracket.TimeB, out LightingTheme b);
        if (!okA || !okB)
        {
            theme = okA ? a : b;
            return okA || okB;
        }

        theme = BlendLighting(a, b, bracket.Weight);
        theme.SourceTimeOfDay = targetTime - MathF.Floor(targetTime);
        return true;
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
            NearDistance = Lerp(a.NearDistance, b.NearDistance, w),
            NearDensity = Lerp(a.NearDensity, b.NearDensity, w),
            FarDistance = Lerp(a.FarDistance, b.FarDistance, w),
            FarDensity = Lerp(a.FarDensity, b.FarDensity, w),
            CloseFogMaxDistance = Lerp(a.CloseFogMaxDistance, b.CloseFogMaxDistance, w),
            FoggingStart = Lerp(a.FoggingStart, b.FoggingStart, w),
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
            HasDistantShadowFactor = a.HasDistantShadowFactor || b.HasDistantShadowFactor,
            HasLightZoneShadowCulling =
                a.HasLightZoneShadowCulling || b.HasLightZoneShadowCulling,

            // SunIntensity is NOT blended here — it is registered against the Sky group, not
            // Lighting, so it rides on SkyTheme and BlendSky already interpolates it.
            MainLightShadowed = Lerp(a.MainLightShadowed, b.MainLightShadowed, w),
            DistantShadowFactor = Lerp(a.DistantShadowFactor, b.DistantShadowFactor, w),
            EnableLightZoneShadowCulling = w < 0.5f
                ? a.EnableLightZoneShadowCulling
                : b.EnableLightZoneShadowCulling,
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
