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
    private static readonly StringName RainPName = "rain_p";
    private static readonly StringName SnowPName = "snow_p";
    private static readonly StringName WeatherPhaseName = "weather_phase";
    private static readonly StringName WeatherTimeName = "weather_time";
    private static readonly StringName ShelterOnName = "shelter_on";
    private static readonly StringName ShelterBoxName = "shelter_box";
    private static readonly StringName ShelterOnSecondName = "shelter_on_second";
    private static readonly StringName ShelterBoxSecondName = "shelter_box_second";

    /// <summary>Quads the mesh holds per kind; the shader draws the first N of each.</summary>
    public int RainCapacity { get; init; }

    public int SnowCapacity { get; init; }

    public Vector4 Area { get; init; } = Engine_Weather.DefaultArea;

    public Vector2 Wind { get; init; }

    private ShaderMaterial? _material;

    private float _rainDensity = -1f;
    private float _rainSize = -1f;
    private float _snowFallSpeed = -1f;
    private float _snowSize = -1f;

    private int _rainCount;
    private int _snowCount;
    private bool _raining;
    private bool _snowing;
    private bool _everApplied;
    private bool _visible;
    private bool _visibleKnown;
    private bool _phaseDirty = true;
    private int _shelterVersion = -1;
    private WeatherMotionState _motion;

    public void Attach(ShaderMaterial material)
    {
        _material = material;
        MaterialOverride = material;
        _phaseDirty = true;
    }

    private void SetVisibleIfChanged(bool visible)
    {
        if (_visibleKnown && _visible == visible)
        {
            return;
        }

        _visibleKnown = true;
        _visible = visible;
        Visible = visible;
        if (visible)
        {
            _phaseDirty = true;
        }
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
        _rainCount = rainCount;
        _snowCount = snowCount;

        _material.SetShaderParameter(RainPName, new Vector4(
            rainCount, MathF.Max(rainSize, 0.15f), Engine_Weather.DefaultRainSpeed, 0.34f));
        _material.SetShaderParameter(SnowPName, new Vector4(
            snowCount, MathF.Max(snowSize, 0.15f), MathF.Max(snowFallSpeed, Engine_Weather.MinSnowSpeed), 1.0f));

        SetVisibleIfChanged(rainCount > 0 || snowCount > 0);

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
        // Stand down under Classic mode (Engine_ClassicWeather) or when Dynamic Sky hides legacy weather.
        if (WeatherMotionState.ShouldSuppressLegacyWeather(
            Refii.Models.Engine_ClassicMaterial.Active,
            Engine_DynamicSky.Enabled,
            Engine_DynamicSky.HideGameWeather))
        {
            SetVisibleIfChanged(false);
            _everApplied = false;
            return;
        }

        if (HostBridge.Weather() is { } live)
        {
            // The one thing here that is NOT the guest's: its weather has no indoor test, so the
            // remaster keeps it out of the hero's building — see Engine_WeatherShelter.
            if (_shelterVersion != Engine_WeatherShelter.Version && _material is not null)
            {
                _shelterVersion = Engine_WeatherShelter.Version;
                _material.SetShaderParameter(ShelterOnName, Engine_WeatherShelter.HasBox ? 1f : 0f);
                _material.SetShaderParameter(ShelterBoxName, Engine_WeatherShelter.ToBox);
                _material.SetShaderParameter(ShelterOnSecondName, Engine_WeatherShelter.HasBoxTwo ? 1f : 0f);
                _material.SetShaderParameter(ShelterBoxSecondName, Engine_WeatherShelter.ToBoxTwo);
            }

            float open = Engine_WeatherShelter.Falling;
            Apply(live.RainDensity * open, live.RainSize, live.SnowFallSpeed, live.SnowSize * open, "guest");
        }

        if (_material is null || (_rainCount <= 0 && _snowCount <= 0))
        {
            return;
        }

        double worldDelta = Refii.Core.Engine_DrawAdvance.Seconds(delta);
        float snowSpeed = MathF.Max(_snowFallSpeed, Engine_Weather.MinSnowSpeed);
        bool advanced = _motion.Advance(
            worldDelta,
            Engine_Weather.DefaultRainSpeed,
            snowSpeed,
            Wind,
            Area,
            _rainCount > 0,
            _snowCount > 0);

        if (_phaseDirty || advanced)
        {
            _material.SetShaderParameter(WeatherPhaseName, _motion.PhaseVector);
            if (_snowCount > 0 || _phaseDirty)
            {
                _material.SetShaderParameter(WeatherTimeName, _motion.SwayClock);
            }

            _phaseDirty = false;
        }
    }
}
