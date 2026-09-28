using System;
using Godot;
using Refii.Host;

namespace Refii.Level;

/// <summary>
/// The precipitation layer, following the guest's BLENDED environment theme each tick.
///
/// THE GUEST HAS NO INDOOR TEST — its rain and snow draws gate on one float each of the
/// blended theme record (RainDensity, SnowSize) against 0.05, and a building is dry only
/// because the painted day set under the camera states no weather and the blend collapses to
/// zero. This node used to be built once from the LEVEL's day set and left alone, so a
/// snowing region kept snowing inside every tavern. Now the level's values are just the
/// starting state, and <see cref="HostBridge.Weather"/> — the same record the guest reads —
/// decides every frame. Without a guest (the editor) the level's values stand.
/// </summary>
public partial class Engine_WeatherNode : MeshInstance3D
{
    /// <summary>Quads the mesh holds per kind; the shader draws the first N of each.</summary>
    public int RainCapacity { get; init; }

    public int SnowCapacity { get; init; }

    private ShaderMaterial? _material;

    private float _rainDensity = -1f;
    private float _rainSize = -1f;
    private float _snowFallSpeed = -1f;
    private float _snowSize = -1f;

    private bool _raining;
    private bool _snowing;
    private bool _everApplied;

    public void Attach(ShaderMaterial material)
    {
        _material = material;
        MaterialOverride = material;
    }

    /// <summary>
    /// Applies four theme values. Clamped and gated exactly as <see cref="Engine_Weather"/>
    /// documents: the guest's 0.05 threshold and nothing else. Counts are the remaster's
    /// mapping of density onto particles, capped at the mesh capacity.
    /// </summary>
    public void Apply(float rainDensity, float rainSize, float snowFallSpeed, float snowSize,
        string source)
    {
        if (_material is null)
        {
            return;
        }

        rainDensity = Math.Clamp(rainDensity, 0f, 1f);
        rainSize = Math.Clamp(rainSize, 0f, 4f);
        snowFallSpeed = Math.Clamp(snowFallSpeed, 0f, 6f);
        snowSize = Math.Clamp(snowSize, 0f, 4f);

        if (_everApplied && rainDensity == _rainDensity && rainSize == _rainSize &&
            snowFallSpeed == _snowFallSpeed && snowSize == _snowSize)
        {
            return;
        }

        _everApplied = true;
        _rainDensity = rainDensity;
        _rainSize = rainSize;
        _snowFallSpeed = snowFallSpeed;
        _snowSize = snowSize;

        int rainCount = Engine_Weather.RainCount(rainDensity, rainSize);
        int snowCount = Engine_Weather.SnowCount(snowFallSpeed, snowSize);
        rainCount = Math.Min(rainCount, RainCapacity);
        snowCount = Math.Min(snowCount, SnowCapacity);

        _material.SetShaderParameter("rain_p", new Vector4(
            rainCount, MathF.Max(rainSize, 0.15f), 14.0f, 0.34f));
        _material.SetShaderParameter("snow_p", new Vector4(
            snowCount, MathF.Max(snowSize, 0.15f), MathF.Max(snowFallSpeed, 0.15f), 1.0f));

        Visible = rainCount > 0 || snowCount > 0;

        bool raining = rainCount > 0;
        bool snowing = snowCount > 0;
        if (raining != _raining || snowing != _snowing)
        {
            _raining = raining;
            _snowing = snowing;
            GD.Print($"[weather] {source}: {rainCount} rain (density {rainDensity:0.00}), " +
                $"{snowCount} snow (size {snowSize:0.00}, speed {snowFallSpeed:0.00})");
        }
    }

    public override void _Process(double delta)
    {
        if (HostBridge.Weather() is not { } live)
        {
            // No guest, or its world is not built yet: the last applied values stand.
            return;
        }

        Apply(live.RainDensity, live.RainSize, live.SnowFallSpeed, live.SnowSize, "guest");
    }
}
