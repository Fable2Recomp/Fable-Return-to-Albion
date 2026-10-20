using System;
using Godot;

namespace Refii.Level;

public enum DynamicWeatherPreset
{
    Theme,
    Clear,
    PartlyCloudy,
    Cloudy,
    Overcast,
    Rain,
    Storm,
    Snow,
    Fog,
    LightRain,
    LightSnow,
    Blizzard,
}

public struct DynamicWeatherState
{
    public float Coverage;
    public float Density;
    public float Overcast;
    public float Storm;
    public float Snow;
    public float FogHaze;
    public float WindSpeed;
    public float Cirrus;
    public float Rain;
    public float SnowFall;

    public readonly DynamicWeatherState Sanitized()
    {
        static float Clamp(float v, float max = 1f) => float.IsFinite(v) ? Math.Clamp(v, 0f, max) : 0f;
        return new DynamicWeatherState
        {
            Coverage = Clamp(Coverage), Density = Clamp(Density), Overcast = Clamp(Overcast),
            Storm = Clamp(Storm), Snow = Clamp(Snow), FogHaze = Clamp(FogHaze),
            WindSpeed = Clamp(WindSpeed, 4f), Cirrus = Clamp(Cirrus), Rain = Clamp(Rain), SnowFall = Clamp(SnowFall),
        };
    }

    public static DynamicWeatherState Lerp(in DynamicWeatherState a, in DynamicWeatherState b, float t)
    {
        t = float.IsFinite(t) ? Math.Clamp(t, 0f, 1f) : 0f;
        return new DynamicWeatherState
        {
            Coverage = a.Coverage + ((b.Coverage - a.Coverage) * t),
            Density = a.Density + ((b.Density - a.Density) * t),
            Overcast = a.Overcast + ((b.Overcast - a.Overcast) * t),
            Storm = a.Storm + ((b.Storm - a.Storm) * t),
            Snow = a.Snow + ((b.Snow - a.Snow) * t),
            FogHaze = a.FogHaze + ((b.FogHaze - a.FogHaze) * t),
            WindSpeed = a.WindSpeed + ((b.WindSpeed - a.WindSpeed) * t),
            Cirrus = a.Cirrus + ((b.Cirrus - a.Cirrus) * t),
            Rain = a.Rain + ((b.Rain - a.Rain) * t),
            SnowFall = a.SnowFall + ((b.SnowFall - a.SnowFall) * t),
        };
    }

    public static DynamicWeatherState Of(DynamicWeatherPreset preset) => preset switch
    {
        DynamicWeatherPreset.LightRain => Engine_DynamicWeatherProfile.FromUnreal(6, rain: 3, lightning: 1, wind: 2, fog: 1.5f),
        DynamicWeatherPreset.LightSnow => Engine_DynamicWeatherProfile.FromUnreal(6, snow: 3, wind: 1, fog: 3),
        DynamicWeatherPreset.Blizzard => Engine_DynamicWeatherProfile.FromUnreal(10, snow: 10, wind: 10, fog: 10),
        DynamicWeatherPreset.Clear => Engine_DynamicWeatherProfile.FromUnreal(0, wind: 2),
        DynamicWeatherPreset.PartlyCloudy => Engine_DynamicWeatherProfile.FromUnreal(3.8f, wind: 2, fog: 1),
        DynamicWeatherPreset.Cloudy => Engine_DynamicWeatherProfile.FromUnreal(5, wind: 2.5f, fog: 1),
        DynamicWeatherPreset.Overcast => Engine_DynamicWeatherProfile.FromUnreal(7.5f, wind: 3, fog: 1.5f),
        DynamicWeatherPreset.Rain => Engine_DynamicWeatherProfile.FromUnreal(7.5f, rain: 7, lightning: 4, wind: 3, fog: 3),
        DynamicWeatherPreset.Storm => Engine_DynamicWeatherProfile.FromUnreal(8, rain: 10, lightning: 10, wind: 10, fog: 6.5f),
        DynamicWeatherPreset.Snow => Engine_DynamicWeatherProfile.FromUnreal(8.5f, snow: 6, wind: 4, fog: 5),
        DynamicWeatherPreset.Fog => Engine_DynamicWeatherProfile.FromUnreal(0, wind: 1, fog: 10),
        _ => Engine_DynamicWeatherProfile.FromUnreal(3.8f, wind: 2, fog: 1),
    };

    public static DynamicWeatherState FromTheme(WeatherTheme theme)
    {
        float rain = theme.HasRain ? Math.Clamp(theme.RainDensity, 0f, 1f) : 0f;
        bool snowing = theme.HasSnow && theme.SnowSize > 0f;
        float wind = theme.HasWind
            ? Math.Clamp((theme.WindStrengthMin + theme.WindStrengthMax) * 0.5f, 0f, 4f)
            : 1f;
        float mist = theme.HasGroundMist ? Math.Clamp(theme.GroundMistStrength, 0f, 1f) : 0f;

        DynamicWeatherState state = Of(DynamicWeatherPreset.PartlyCloudy);
        if (snowing)
        {
            state = Of(DynamicWeatherPreset.Snow);
        }
        else if (rain > 0.001f)
        {
            state = Lerp(Of(DynamicWeatherPreset.Overcast), Of(DynamicWeatherPreset.Storm),
                MathF.Pow(rain, 1.5f));
        }

        state.WindSpeed = MathF.Max(0.3f, wind);
        state.FogHaze = MathF.Max(state.FogHaze, mist * 0.6f);
        state.Rain = rain;
        state.SnowFall = snowing ? 1f : 0f;
        return state;
    }
}

public static class Engine_DynamicSky
{
    public const string ShaderPath = "res://src/Engine/level/environment/dynamic/dynamic_sky.gdshader";

    public const string CloudShapePath =
        "res://src/Engine/level/environment/dynamic/dynamic_sky_cloud_shape.tres";

    public const string CloudDetailPath =
        "res://src/Engine/level/environment/dynamic/dynamic_sky_cloud_detail.tres";

    public const string SwitchKey = "dynamic_sky_and_weather";

    public const string VolumetricCloudsKey = "dynamic_sky_volumetric_clouds";

    public const string CloudShadowsKey = "dynamic_sky_cloud_shadows";

    public const string LightingOverrideKey = "dynamic_sky_lighting_override";

    public static bool LightingOverride => Refii.Core.Engine_Switches.Flag(LightingOverrideKey, false);

    public static bool CloudShadows => Refii.Core.Engine_Switches.Flag(CloudShadowsKey, false);

    private const string CloudVolumeShapePath =
        "res://src/Engine/level/environment/dynamic/dynamic_sky_cloud_volume_shape.tres";
    private const string CloudVolumeDetailPath =
        "res://src/Engine/level/environment/dynamic/dynamic_sky_cloud_volume_detail.tres";

    public static bool VolumetricClouds => Refii.Core.Engine_Switches.Flag(VolumetricCloudsKey, false);

    public static void ApplyCloudChoice(ShaderMaterial material, bool? volumetricClouds = null)
    {
        bool wanted = volumetricClouds ?? VolumetricClouds;
        if (wanted)
        {
            material.SetShaderParameter("cloud_volume_shape", GD.Load<Texture3D>(CloudVolumeShapePath));
            material.SetShaderParameter("cloud_volume_detail", GD.Load<Texture3D>(CloudVolumeDetailPath));
        }

        material.SetShaderParameter("volumetric_clouds", wanted);
    }

    private static bool _enabled;
    private static int _enabledGeneration = -1;

    public static bool Enabled
    {
        get
        {
            int generation = Refii.Core.Engine_Switches.Generation;
            if (generation != _enabledGeneration)
            {
                _enabledGeneration = generation;
                _enabled = Refii.Core.Engine_Switches.Flag(SwitchKey, false);
            }

            return _enabled;
        }
    }

    public static readonly bool HideGameClouds =
        Refii.Core.Engine_Switches.Flag("dynamic_sky_hide_game_clouds", true);

    public static readonly bool HideGameCelestial =
        Refii.Core.Engine_Switches.Flag("dynamic_sky_hide_game_celestial", true);

    public static string WeatherSwitch =>
        Refii.Core.Engine_Switches.String("dynamic_sky_weather", "theme");

    public static readonly float Exposure =
        Refii.Core.Engine_Switches.Float("dynamic_sky_exposure", 1f);

    public static readonly float Dream =
        Refii.Core.Engine_Switches.Float("dynamic_sky_dream", 0.6f);

    public static readonly float Haze =
        Refii.Core.Engine_Switches.Float("dynamic_sky_haze", 0.4f);

    public static readonly float BlendSeconds =
        Refii.Core.Engine_Switches.Float("dynamic_sky_weather_blend", 20f);

    public static readonly bool ThemeColours =
        Refii.Core.Engine_Switches.Flag("dynamic_sky_theme_colours", true);

    public static readonly bool HideGameWeather =
        Refii.Core.Engine_Switches.Flag("dynamic_sky_hide_game_weather", true);

    public static readonly float FogDensityMax =
        Refii.Core.Engine_Switches.Float("dynamic_sky_fog_density", 0.02f);

    private static readonly StringName MoonDirName = "moon_dir_world";
    private static readonly StringName SkyTimeGlobal = "dynamic_sky_time";
    private static readonly StringName LightningGlobal = "dynamic_sky_lightning";

    public static bool TryParsePreset(string text, out DynamicWeatherPreset preset)
    {
        foreach (DynamicWeatherPreset candidate in Enum.GetValues<DynamicWeatherPreset>())
        {
            if (candidate.ToString().Equals(text.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                preset = candidate;
                return true;
            }
        }

        preset = DynamicWeatherPreset.Theme;
        return false;
    }

    private static bool _globalsEnsured;
    private static bool _globalsAdded;

    public static void EnsureGlobals()
    {
        if (_globalsEnsured)
        {
            return;
        }

        _globalsEnsured = true;
        if (!Engine.IsEditorHint())
        {
            return;
        }

        bool hasTime = false;
        bool hasLightning = false;
        foreach (StringName name in RenderingServer.GlobalShaderParameterGetList())
        {
            hasTime |= name == SkyTimeGlobal;
            hasLightning |= name == LightningGlobal;
        }

        if (!hasTime)
        {
            RenderingServer.GlobalShaderParameterAdd(
                SkyTimeGlobal, RenderingServer.GlobalShaderParameterType.Float, 0f);
            _globalsAdded = true;
        }

        if (!hasLightning)
        {
            RenderingServer.GlobalShaderParameterAdd(
                LightningGlobal, RenderingServer.GlobalShaderParameterType.Vec4,
                new Vector4(0f, 0.3f, 1f, 0f));
            _globalsAdded = true;
        }

        if (_globalsAdded)
        {
            GD.Print("[dynamic-sky] registered shader globals the running editor had not loaded");
        }
    }

    public static ShaderMaterial? BuildMaterial(bool cubemapFableDome)
    {
        Engine_DynamicSurfaceWeather.EnsureGlobals();
        EnsureGlobals();
        var shader = GD.Load<Shader>(ShaderPath);
        if (shader is null)
        {
            GD.Print($"[dynamic-sky] shader missing at {ShaderPath}");
            return null;
        }

        if (_globalsAdded)
        {
            shader.Code = shader.Code;
            _globalsAdded = false;
        }

        var material = new ShaderMaterial { Shader = shader };
        Configure(material, cubemapFableDome);
        return material;
    }

    public static void Prime(
        ShaderMaterial material, SkyFrame frame, SkyTheme theme, WeatherTheme weather)
    {
        Configure(material, cubemapFableDome: true);
        ApplyFrame(material, frame, theme);

        DynamicWeatherState state = Engine_DynamicWeatherChoice.StateOf(Engine_DynamicWeatherChoice.Token, weather);
        ApplyWeather(material, state);
        ApplyClock(0f, 0f, Vector3.Back);
        GD.Print($"[dynamic-sky] on: weather={Engine_DynamicWeatherChoice.Token} coverage={state.Coverage:0.00} " +
            $"overcast={state.Overcast:0.00} storm={state.Storm:0.00} snow={state.Snow:0.00} " +
            $"hide_clouds={HideGameClouds} hide_celestial={HideGameCelestial}");
    }

    public static void Configure(ShaderMaterial material, bool cubemapFableDome)
    {
        material.SetShaderParameter("cubemap_fable_dome", cubemapFableDome);
        ApplyCloudChoice(material);

        var shape = GD.Load<Texture2D>(CloudShapePath);
        var detail = GD.Load<Texture2D>(CloudDetailPath);
        if (shape is not null)
        {
            material.SetShaderParameter("cloud_shape", shape);
        }

        if (detail is not null)
        {
            material.SetShaderParameter("cloud_detail", detail);
        }

        material.SetShaderParameter("exposure", Exposure);
        material.SetShaderParameter("dream", Dream);
        material.SetShaderParameter("haze", Haze);
        ApplyCelestialTextures(material);
    }

    public static void ApplyCelestialTextures(ShaderMaterial material)
    {
        const string moon = "res://assets/dynamic_sky/lroc_color_2k.jpg";
        Texture2D? texture = ResourceLoader.Exists(moon) ? GD.Load<Texture2D>(moon) : null;
        material.SetShaderParameter("moon_use_texture", texture is not null);
        if (texture is not null) material.SetShaderParameter("moon_texture", texture);
        material.SetShaderParameter("star_use_texture", false);
    }

    public static void ApplyFrame(ShaderMaterial material, SkyFrame frame, SkyTheme? theme)
    {
        material.SetShaderParameter("sun_engine", Engine_SkyFrame.ToDome(frame.SunDirection));
        material.SetShaderParameter(MoonDirName, Engine_SkyFrame.ToGodot(frame.MoonDirection));
        material.SetShaderParameter("sun_intensity", MathF.Max(frame.SkyParams[0], 0.05f));

        if (!ThemeColours)
        {
            return;
        }

        material.SetShaderParameter("colour_zenith", Soft(frame.SkyTop, 0.42f, 0.56f, 0.76f));
        material.SetShaderParameter("colour_horizon", Soft(frame.SkyBottom, 0.78f, 0.72f, 0.64f));
        material.SetShaderParameter("colour_sunset", Soft(frame.SkySunset, 1.0f, 0.52f, 0.30f));

        if (theme is not null && theme.HasSunDiscColour)
        {
            material.SetShaderParameter("sun_colour", Soft(theme.SunDiscColour, 1f, 0.94f, 0.82f));
        }

        if (theme is not null && theme.HasStarBrightness)
        {
            material.SetShaderParameter("star_brightness",
                Math.Clamp(theme.StarBrightness, 0f, 4f));
        }
    }

    public static void ApplyWeather(ShaderMaterial material, in DynamicWeatherState w)
    {
        material.SetShaderParameter("cloud_coverage", w.Coverage);
        material.SetShaderParameter("cloud_density", w.Density);
        material.SetShaderParameter("overcast", w.Overcast);
        material.SetShaderParameter("storm", w.Storm);
        material.SetShaderParameter("snow", w.Snow);
        material.SetShaderParameter("fog_haze", w.FogHaze);
        material.SetShaderParameter("cloud_speed", w.WindSpeed);
        material.SetShaderParameter("cirrus_amount", w.Cirrus);
    }

    public static void ApplyClock(float skyTime, float lightning, Vector3 lightningDir,
        ShaderMaterial? material = null)
    {
        material?.SetShaderParameter("cloud_time", skyTime);
        RenderingServer.GlobalShaderParameterSet(SkyTimeGlobal, skyTime);
        RenderingServer.GlobalShaderParameterSet(LightningGlobal,
            new Vector4(lightningDir.X, lightningDir.Y, lightningDir.Z, lightning));
    }

    public static float Daylight(SkyFrame frame)
    {
        float y = frame.SunDirection[1];
        float t = Math.Clamp((y + 0.14f) / 0.24f, 0f, 1f);
        return t * t * (3f - (2f * t));
    }

    public static Vector3 HorizonColour(ShaderMaterial material)
    {
        Variant v = material.GetShaderParameter("colour_horizon");
        return v.VariantType == Variant.Type.Vector3
            ? v.AsVector3()
            : new Vector3(0.78f, 0.72f, 0.64f);
    }

    public static void ApplyFog(
        Godot.Environment environment, in DynamicWeatherState w, Vector3 horizon, float day,
        bool usesFableAtmosphere = true)
    {
        float density = Engine_DynamicSkyLighting.FogDensity(usesFableAtmosphere, w.FogHaze, FogDensityMax);
        bool on = density > 1e-5f;
        if (environment.FogEnabled != on)
        {
            environment.FogEnabled = on;
        }

        if (!on)
        {
            return;
        }

        float grey = (horizon.X * 0.299f) + (horizon.Y * 0.587f) + (horizon.Z * 0.114f);
        Vector3 soft = horizon.Lerp(new Vector3(grey, grey, grey), 0.5f);
        Vector3 colour = soft * ((0.08f + (0.87f * day)) * (1f - (w.Overcast * 0.3f)));
        var fogColour = new Color(colour.X, colour.Y, colour.Z);

        environment.FogMode = Godot.Environment.FogModeEnum.Exponential;
        environment.FogLightEnergy = 1f;
        environment.FogSkyAffect = 0f;
        environment.FogAerialPerspective = 0.25f;
        environment.FogSunScatter = 0f;
        environment.FogHeightDensity = 0f;

        if (MathF.Abs(environment.FogDensity - density) > 1e-5f)
        {
            environment.FogDensity = density;
        }

        if (!environment.FogLightColor.IsEqualApprox(fogColour))
        {
            environment.FogLightColor = fogColour;
        }
    }

    public static void ApplyWind(ShaderMaterial material, Vector2 direction)
    {
        if (direction.LengthSquared() < 1e-6f)
        {
            direction = new Vector2(1f, 0.25f);
        }

        material.SetShaderParameter("wind_dir", direction.Normalized());
    }

    private static Vector3 Soft(float[] c, float r, float g, float b) =>
        Engine_DynamicSkyLighting.LinearColour(c, new Vector3(r, g, b));
}

public sealed class Engine_DynamicSkyDriver
{
    public event Action<DynamicLightningStrike>? LightningStruck;

    private bool _auroraWanted;
    private float _aurora;
    private readonly Engine_DynamicSurfaceState _surface = new();

    public void SetNightClock(float hour, int day, DynamicAuroraMode mode, Engine_DynamicNightSky? history = null)
    {
        _auroraWanted = (history ?? Engine_DynamicNightSky.Session).Visible(hour, day, mode);
    }

    public static Engine_DynamicSkyDriver? Current { get; private set; }

    private const float TimeRollover = 100000f;

    private readonly Engine_DynamicWeatherTransition _transition = new(DynamicWeatherState.Of(DynamicWeatherPreset.PartlyCloudy));
    private DynamicWeatherState _now => _transition.Value;
    private double _cloudX;
    private double _cloudY;
    private float _time;

    private float _lightning;
    private float _echoStrike;
    private float _echoStrength;
    private float _nextStrike = 4f;
    private Vector3 _strikeDir = new(0f, 0.3f, 1f);
    private readonly Random _random = new();

    private int _switchGeneration = -1;
    private int _choiceGeneration = -1;
    private WeatherTheme? _lastTheme;

    public DynamicWeatherState Weather => _now;

    public Engine_DynamicSurfaceState SurfaceState => _surface;

    public Engine_DynamicSkyDriver()
    {
        _switchGeneration = Refii.Core.Engine_Switches.Generation;
        if (Engine_DynamicSky.TryParsePreset(Engine_DynamicSky.WeatherSwitch, out var preset) &&
            preset != DynamicWeatherPreset.Theme)
            SetWeather(preset, 0f);
        Current = this;
    }

    public void SetWeather(in DynamicWeatherState target, float seconds)
    {
        _transition.Begin(target, seconds);
        _sceneDirty = true;
    }

    public void SetWeather(DynamicWeatherPreset preset, float seconds)
    {
        if (preset != DynamicWeatherPreset.Theme)
        {
            SetWeather(DynamicWeatherState.Of(preset), seconds);
        }
        else if (_lastTheme is { } theme)
        {
            FollowTheme(theme);
        }
    }

    /// <summary>The level's sky: the decided weather (<see cref="Engine_DynamicWeatherChoice"/>) over the game's theme.</summary>
    public void FollowTheme(WeatherTheme theme)
    {
        _lastTheme = theme;
        _choiceGeneration = Engine_DynamicWeatherChoice.Generation;
        SetWeather(Engine_DynamicWeatherChoice.StateOf(Engine_DynamicWeatherChoice.Token, theme),
            Engine_DynamicSky.BlendSeconds);
    }

    private Vector2 _windDir = new(1f, 0.25f);
    private float _day = 1f;
    private bool _sceneDirty = true;

    public void SetWind(Vector2 direction)
    {
        _windDir = direction.IsFinite() && direction.LengthSquared() > 1e-6f
            ? direction.Normalized() : new Vector2(1f, 0.25f).Normalized();
        _sceneDirty = true;
    }

    private float _haze = 1f;
    private float _falling = 1f;

    /// <summary>
    /// The hero's building (<see cref="Engine_WeatherShelter"/>): the scale on the haze in the
    /// air and on what falls. The sky itself keeps its weather.
    /// </summary>
    public void SetShelter(float haze, float falling)
    {
        if (haze != _haze || falling != _falling)
        {
            _haze = haze;
            _falling = falling;
            _sceneDirty = true;
        }
    }

    public void SetDaylight(float day)
    {
        if (MathF.Abs(day - _day) > 0.01f)
        {
            _day = day;
            _sceneDirty = true;
        }
    }

    public void Tick(
        float delta, ShaderMaterial material,
        Engine_DynamicPrecipitation? precipitation, Godot.Environment? environment,
        bool? volumetricClouds = null, bool? surfaceWeather = null)
    {
        delta = float.IsFinite(delta) ? MathF.Max(delta, 0f) : 0f;
        _time = (_time + delta) % TimeRollover;

        int generation = Refii.Core.Engine_Switches.Generation;
        if (generation != _switchGeneration)
        {
            _switchGeneration = generation;
            Engine_DynamicSky.ApplyCloudChoice(material, volumetricClouds);

            // A driver that follows no level (the options preview) shows this player's own override.
            if (_lastTheme is null &&
                Engine_DynamicSky.TryParsePreset(Engine_DynamicSky.WeatherSwitch, out var forced) &&
                forced != DynamicWeatherPreset.Theme)
            {
                SetWeather(DynamicWeatherState.Of(forced), Engine_DynamicSky.BlendSeconds);
            }
        }

        if (_lastTheme is { } theme && Engine_DynamicWeatherChoice.Generation != _choiceGeneration)
        {
            _choiceGeneration = Engine_DynamicWeatherChoice.Generation;
            SetWeather(Engine_DynamicWeatherChoice.StateOf(Engine_DynamicWeatherChoice.Token, theme),
                Engine_DynamicWeatherChoice.BlendSeconds);
        }

        DynamicWeatherState previous = _now;
        _transition.Advance(delta);
        if (!previous.Equals(_now)) _sceneDirty = true;
        if (_sceneDirty)
        {
            Engine_DynamicSky.ApplyWeather(material, _now);
        }

        Variant multiplier = material.GetShaderParameter("cloud_motion_multiplier");
        float motion = multiplier.VariantType == Variant.Type.Nil ? 1f : multiplier.AsSingle();
        motion = float.IsFinite(motion) ? Math.Clamp(motion, 0f, 10f) : 1f;
        double travel = delta * (previous.WindSpeed + _now.WindSpeed) * 0.5 * motion;
        _cloudX += _windDir.X * travel;
        _cloudY += _windDir.Y * travel;
        material.SetShaderParameter("cloud_offset", new Vector2((float)_cloudX, (float)_cloudY));

        DynamicWeatherState ground = _now with { FogHaze = _now.FogHaze * _haze };
        bool usesFableAtmosphere = material.GetShaderParameter("cubemap_fable_dome").AsBool();
        bool fogWanted = Engine_DynamicSkyLighting.FogDensity(
            usesFableAtmosphere, ground.FogHaze, Engine_DynamicSky.FogDensityMax) > 1e-5f;
        if (_sceneDirty || (environment is not null && environment.FogEnabled != fogWanted))
        {
            _sceneDirty = false;
            float brightness = 0.25f + (0.75f * _day);
            precipitation?.Apply(
                _now.Rain * _falling, _now.SnowFall * _falling, _windDir, _now.WindSpeed, brightness);
            if (environment is not null)
            {
                Engine_DynamicSky.ApplyFog(
                    environment, ground, Engine_DynamicSky.HorizonColour(material), _day, usesFableAtmosphere);
            }
        }

        TickLightning(delta);
        bool surfaces = surfaceWeather ?? Engine_DynamicSurfaceWeather.Enabled;
        _surface.Advance(delta, _now, _day, surfaces);
        Engine_DynamicSurfaceWeather.Publish(_surface, _time, surfaces, _now.Rain);
        _aurora = Mathf.MoveToward(_aurora, _auroraWanted ? 1f : 0f, delta / 6f);
        material.SetShaderParameter("aurora_strength", _aurora);
        precipitation?.AdvanceMotion(delta, _lightning);
        Engine_DynamicSky.ApplyClock(_time, _lightning, _strikeDir, material);
    }

    private void TickLightning(float delta)
    {
        if (delta <= 0f) return;
        if (_lightning > 0f)
        {
            _lightning = MathF.Max(0f, _lightning - (delta * 6f));
        }

        if (_now.Storm < 0.05f)
        {
            _echoStrike = 0f;
            return;
        }

        if (_echoStrike > 0f)
        {
            _echoStrike -= delta;
            if (_echoStrike <= 0f) _lightning = MathF.Max(_lightning, _echoStrength * _now.Storm);
        }

        _nextStrike -= delta;
        if (_nextStrike > 0f)
        {
            return;
        }

        float angle = (float)(_random.NextDouble() * Math.PI * 2.0);
        _strikeDir = new Vector3(MathF.Cos(angle), 0.25f, MathF.Sin(angle));
        _lightning = (0.6f + (float)_random.NextDouble() * 0.6f) * _now.Storm;
        LightningStruck?.Invoke(new DynamicLightningStrike(_strikeDir, _lightning,
            300f + (float)_random.NextDouble() * 1400f));
        _echoStrike = 0.12f + (float)_random.NextDouble() * 0.12f;
        _echoStrength = 0.35f + (float)_random.NextDouble() * 0.45f;
        _nextStrike = 2f + ((float)_random.NextDouble() * 10f) / MathF.Max(_now.Storm, 0.2f);
    }
}
