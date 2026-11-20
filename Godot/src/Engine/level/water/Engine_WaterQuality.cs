using System;
using System.Collections.Generic;

namespace Refii.Level;

/// <summary>Modern water quality tiers controlled by the <c>water_quality</c> CVar.</summary>
public enum WaterQualityTier
{
    Low = 0,
    Balanced = 1,
    High = 2,
    Ultra = 3,
}

/// <summary>Planar reflection viewport configuration for a water quality tier.</summary>
public readonly record struct WaterMirrorSpec(
    bool Enabled,
    int TileHeight,
    int Cadence,
    float FarPlane,
    float LodPixels);

/// <summary>
/// Pure-managed policy mapping <c>water_quality</c> tiers to planar mirror and SSR budgets.
/// Safe to exercise from headless unit tests without initializing unmanaged Godot classes.
/// </summary>
public static class Engine_WaterQuality
{
    public const string CVarName = "water_quality";

    /// <summary>Parses a <c>water_quality</c> value, returning <paramref name="fallback"/> on unknown tokens.</summary>
    public static WaterQualityTier Parse(string? raw, WaterQualityTier fallback = WaterQualityTier.Balanced)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        return raw.Trim().Trim('"').ToLowerInvariant() switch
        {
            "low" or "0" => WaterQualityTier.Low,
            "balanced" or "medium" or "1" => WaterQualityTier.Balanced,
            "high" or "2" => WaterQualityTier.High,
            "ultra" or "3" => WaterQualityTier.Ultra,
            _ => fallback,
        };
    }

    /// <summary>Formats a <see cref="WaterQualityTier"/> to its canonical <c>refii.toml</c> string.</summary>
    public static string ToConfigString(WaterQualityTier tier) => tier switch
    {
        WaterQualityTier.Low => "low",
        WaterQualityTier.Balanced => "balanced",
        WaterQualityTier.High => "high",
        WaterQualityTier.Ultra => "ultra",
        _ => "balanced",
    };

    /// <summary>Resolves the effective water quality tier from parsed config values and platform.</summary>
    public static WaterQualityTier ResolveFromValues(
        IReadOnlyDictionary<string, string>? values,
        bool isAndroid)
    {
        WaterQualityTier fallback = isAndroid ? WaterQualityTier.Low : WaterQualityTier.Balanced;
        return values is not null && values.TryGetValue(CVarName, out string? raw) && raw.Length != 0
            ? Parse(raw, fallback)
            : fallback;
    }

    /// <summary>Returns the planar mirror budget for <paramref name="tier"/>.</summary>
    public static WaterMirrorSpec MirrorFor(WaterQualityTier tier) => tier switch
    {
        WaterQualityTier.Low => new(false, 256, 4, 50f, 4f),
        WaterQualityTier.Balanced => new(false, 256, 4, 200f, 4f),
        WaterQualityTier.High => new(true, 256, 3, 225f, 4f),
        WaterQualityTier.Ultra => new(true, 384, 2, 300f, 3f),
        _ => new(false, 256, 4, 200f, 4f),
    };

    /// <summary>Returns the linear SSR march step budget for <paramref name="tier"/>.</summary>
    public static int SsrStepsFor(WaterQualityTier tier, bool hasPlanarMirror)
    {
        _ = hasPlanarMirror;
        return tier switch
        {
            WaterQualityTier.Low => 0,
            WaterQualityTier.Balanced => 14,
            WaterQualityTier.High => 18,
            WaterQualityTier.Ultra => 24,
            _ => 14,
        };
    }
}
