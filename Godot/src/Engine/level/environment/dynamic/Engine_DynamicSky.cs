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

    public static DynamicWeatherState Lerp(in DynamicWeatherState a, in DynamicWeatherState b, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
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
        DynamicWeatherPreset.Clear => new()
        {
            Coverage = 0.18f, Density = 0.55f, WindSpeed = 0.8f, Cirrus = 0.45f,
        },
        DynamicWeatherPreset.PartlyCloudy => new()
        {
            Coverage = 0.45f, Density = 0.70f, WindSpeed = 1.0f, Cirrus = 0.35f,
        },
        DynamicWeatherPreset.Cloudy => new()
        {
            Coverage = 0.68f, Density = 0.85f, Overcast = 0.25f, WindSpeed = 1.3f, Cirrus = 0.2f,
        },
        DynamicWeatherPreset.Overcast => new()
        {
            Coverage = 0.9f, Density = 1.0f, Overcast = 0.85f, FogHaze = 0.15f, WindSpeed = 1.2f,
        },
        DynamicWeatherPreset.Rain => new()
        {
            Coverage = 0.95f, Density = 1.0f, Overcast = 0.95f, FogHaze = 0.3f, WindSpeed = 1.8f,
            Rain = 0.6f,
        },
        DynamicWeatherPreset.Storm => new()
        {
            Coverage = 1.0f, Density = 1.0f, Overcast = 1.0f, Storm = 1.0f, FogHaze = 0.35f,
            WindSpeed = 3.0f, Rain = 1.0f,
        },
        DynamicWeatherPreset.Snow => new()
        {
            Coverage = 0.9f, Density = 0.95f, Overcast = 0.8f, Snow = 1.0f, FogHaze = 0.4f,
            WindSpeed = 0.9f, SnowFall = 1.0f,
        },
        DynamicWeatherPreset.Fog => new()
        {
            Coverage = 0.5f, Density = 0.6f, Overcast = 0.4f, FogHaze = 1.0f, WindSpeed = 0.4f,
            Cirrus = 0.1f,
        },
        _ => new()
        {
            Coverage = 0.45f, Density = 0.70f, WindSpeed = 1.0f, Cirrus = 0.35f,
        },
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

    public static DynamicWeatherRule CurrentRule { get; private set; } = DynamicWeatherRule.Random;

    public static DynamicWeatherPreset? CurrentPick { get; set; }

    public static void SetLevel(LevelEntry level)
    {
        DynamicWeatherRule rule = Engine_DynamicWeatherRules.Resolve(level);
        if (rule != CurrentRule || rule.Mode != DynamicWeatherMode.Random)
        {
            CurrentPick = null;
        }

        CurrentRule = rule;
        GD.Print($"[dynamic-sky] {level}: scenario='{level.Scenario}' weather rule={rule.Mode} " +
            $"preset={rule.Preset} ({rule.Reason})");
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

        DynamicWeatherState state = CurrentRule.Mode switch
        {
            DynamicWeatherMode.Fixed => DynamicWeatherState.Of(CurrentRule.Preset),
            DynamicWeatherMode.None => DynamicWeatherState.Of(DynamicWeatherPreset.Clear),
            DynamicWeatherMode.Random when CurrentPick is { } pick => DynamicWeatherState.Of(pick),
            _ => DynamicWeatherState.FromTheme(weather),
        };
        if (TryParsePreset(WeatherSwitch, out DynamicWeatherPreset forced) &&
            forced != DynamicWeatherPreset.Theme)
        {
            state = DynamicWeatherState.Of(forced);
        }

        ApplyWeather(material, state);
        ApplyClock(0f, 0f, Vector3.Back);
        GD.Print($"[dynamic-sky] on: weather={WeatherSwitch} coverage={state.Coverage:0.00} " +
            $"overcast={state.Overcast:0.00} storm={state.Storm:0.00} snow={state.Snow:0.00} " +
            $"hide_clouds={HideGameClouds} hide_celestial={HideGameCelestial}");
    }

    public static void Configure(ShaderMaterial material, bool cubemapFableDome)
    {
        material.SetShaderParameter("cubemap_fable_dome", cubemapFableDome);

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

    public static void ApplyClock(float skyTime, float lightning, Vector3 lightningDir)
    {
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
        Godot.Environment environment, in DynamicWeatherState w, Vector3 horizon, float day)
    {
        float density = w.FogHaze * w.FogHaze * FogDensityMax;
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

        if (environment.FogMode != Godot.Environment.FogModeEnum.Exponential)
        {
            environment.FogMode = Godot.Environment.FogModeEnum.Exponential;
            environment.FogLightEnergy = 1f;
            environment.FogSkyAffect = 0f;
            environment.FogAerialPerspective = 0.25f;
            environment.FogSunScatter = 0f;
            environment.FogHeightDensity = 0f;
        }

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

    private static Vector3 Soft(float[] c, float r, float g, float b)
    {
        float x = float.IsFinite(c[0]) ? c[0] : r;
        float y = float.IsFinite(c[1]) ? c[1] : g;
        float z = float.IsFinite(c[2]) ? c[2] : b;
        float peak = MathF.Max(MathF.Max(x, y), MathF.Max(z, 1e-3f));
        if (peak > 1.2f)
        {
            float k = 1.2f / peak;
            x *= k;
            y *= k;
            z *= k;
        }

        return new Vector3(Math.Clamp(x, 0f, 1.2f), Math.Clamp(y, 0f, 1.2f), Math.Clamp(z, 0f, 1.2f));
    }
}

public sealed class Engine_DynamicSkyDriver
{
    public static Engine_DynamicSkyDriver? Current { get; private set; }

    private const float TimeRollover = 100000f;

    private DynamicWeatherState _from;
    private DynamicWeatherState _to;
    private DynamicWeatherState _now;
    private float _blend = 1f;
    private float _blendSeconds = 1f;
    private float _time;

    private float _lightning;
    private float _nextStrike = 4f;
    private Vector3 _strikeDir = new(0f, 0.3f, 1f);
    private readonly Random _random = new();

    private DynamicWeatherPreset _forced = DynamicWeatherPreset.Theme;
    private bool _forcedParsed;
    private float _untilChange = -1f;
    private int _switchGeneration = -1;
    private WeatherTheme? _lastTheme;

    public DynamicWeatherState Weather => _now;

    public Engine_DynamicSkyDriver()
    {
        _from = _to = _now = DynamicWeatherState.Of(DynamicWeatherPreset.PartlyCloudy);
        Current = this;
    }

    public void SetWeather(in DynamicWeatherState target, float seconds)
    {
        _from = _now;
        _to = target;
        _blendSeconds = MathF.Max(seconds, 0.01f);
        _blend = 0f;
    }

    public void SetWeather(DynamicWeatherPreset preset, float seconds)
    {
        _forced = preset;
        _forcedParsed = true;
        if (preset != DynamicWeatherPreset.Theme)
        {
            SetWeather(DynamicWeatherState.Of(preset), seconds);
        }
    }

    public void FollowTheme(WeatherTheme theme)
    {
        _lastTheme = theme;
        if (!_forcedParsed)
        {
            _forcedParsed = true;
            if (Engine_DynamicSky.TryParsePreset(Engine_DynamicSky.WeatherSwitch, out var preset))
            {
                _forced = preset;
            }
        }

        if (_forced != DynamicWeatherPreset.Theme)
        {
            Retarget(DynamicWeatherState.Of(_forced));
            return;
        }

        DynamicWeatherRule rule = Engine_DynamicSky.CurrentRule;
        switch (rule.Mode)
        {
            case DynamicWeatherMode.Fixed:
                Retarget(DynamicWeatherState.Of(rule.Preset));
                break;
            case DynamicWeatherMode.None:
                Retarget(DynamicWeatherState.Of(DynamicWeatherPreset.Clear) with
                {
                    Coverage = 0.1f, Cirrus = 0.1f,
                });
                break;
            case DynamicWeatherMode.Random:
                if (Engine_DynamicSky.CurrentPick is not { } pick)
                {
                    pick = Engine_DynamicWeatherRules.Roll(_random);
                    Engine_DynamicSky.CurrentPick = pick;
                    _untilChange = NextChangeSeconds();
                    GD.Print($"[dynamic-sky] rolled {pick}");
                }

                Retarget(DynamicWeatherState.Of(pick));
                break;
            default:
                Retarget(DynamicWeatherState.FromTheme(theme));
                break;
        }
    }

    private void Retarget(in DynamicWeatherState target)
    {
        if (_blend >= 1f && SameState(_to, target))
        {
            return;
        }

        SetWeather(target, Engine_DynamicSky.BlendSeconds);
    }

    private static bool SameState(in DynamicWeatherState a, in DynamicWeatherState b) =>
        MathF.Abs(a.Coverage - b.Coverage) < 1e-4f &&
        MathF.Abs(a.Density - b.Density) < 1e-4f &&
        MathF.Abs(a.Overcast - b.Overcast) < 1e-4f &&
        MathF.Abs(a.Storm - b.Storm) < 1e-4f &&
        MathF.Abs(a.Snow - b.Snow) < 1e-4f &&
        MathF.Abs(a.FogHaze - b.FogHaze) < 1e-4f &&
        MathF.Abs(a.WindSpeed - b.WindSpeed) < 1e-4f &&
        MathF.Abs(a.Cirrus - b.Cirrus) < 1e-4f &&
        MathF.Abs(a.Rain - b.Rain) < 1e-4f &&
        MathF.Abs(a.SnowFall - b.SnowFall) < 1e-4f;

    private float NextChangeSeconds()
    {
        float minutes = MathF.Max(Engine_DynamicWeatherRules.ChangeMinutes, 0.1f);
        return minutes * 60f * (0.6f + ((float)_random.NextDouble() * 0.8f));
    }

    private void TickRandomDrift(float delta)
    {
        if (_forced != DynamicWeatherPreset.Theme ||
            Engine_DynamicSky.CurrentRule.Mode != DynamicWeatherMode.Random ||
            Engine_DynamicSky.CurrentPick is not { } current)
        {
            return;
        }

        if (_untilChange < 0f)
        {
            _untilChange = NextChangeSeconds();
        }

        _untilChange -= delta;
        if (_untilChange > 0f)
        {
            return;
        }

        _untilChange = NextChangeSeconds();
        DynamicWeatherPreset next = Engine_DynamicWeatherRules.Drift(current, _random);
        if (next == current)
        {
            return;
        }

        Engine_DynamicSky.CurrentPick = next;
        GD.Print($"[dynamic-sky] weather drifts {current} -> {next}");
        SetWeather(DynamicWeatherState.Of(next), Engine_DynamicSky.BlendSeconds * 3f);
    }

    private Vector2 _windDir = new(1f, 0.25f);
    private float _day = 1f;
    private bool _sceneDirty = true;

    public void SetWind(Vector2 direction)
    {
        _windDir = direction;
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
        Engine_DynamicPrecipitation? precipitation, Godot.Environment? environment)
    {
        _time = (_time + delta) % TimeRollover;

        int generation = Refii.Core.Engine_Switches.Generation;
        if (generation != _switchGeneration)
        {
            _switchGeneration = generation;
            _forcedParsed = false;
            if (_lastTheme is { } theme)
            {
                FollowTheme(theme);
            }
            else if (Engine_DynamicSky.TryParsePreset(Engine_DynamicSky.WeatherSwitch, out var forced))
            {
                _forcedParsed = true;
                _forced = forced;
                if (forced != DynamicWeatherPreset.Theme)
                {
                    SetWeather(DynamicWeatherState.Of(forced), Engine_DynamicSky.BlendSeconds);
                }
            }
        }

        if (_blend < 1f)
        {
            _blend = MathF.Min(1f, _blend + (delta / _blendSeconds));
            float eased = _blend * _blend * (3f - (2f * _blend));
            _now = DynamicWeatherState.Lerp(_from, _to, eased);
            Engine_DynamicSky.ApplyWeather(material, _now);
            _sceneDirty = true;
        }

        DynamicWeatherState ground = _now with { FogHaze = _now.FogHaze * _haze };
        bool fogWanted = ground.FogHaze * ground.FogHaze * Engine_DynamicSky.FogDensityMax > 1e-5f;
        if (_sceneDirty || (environment is not null && environment.FogEnabled != fogWanted))
        {
            _sceneDirty = false;
            float brightness = 0.25f + (0.75f * _day);
            precipitation?.Apply(
                _now.Rain * _falling, _now.SnowFall * _falling, _windDir, _now.WindSpeed, brightness);
            if (environment is not null)
            {
                Engine_DynamicSky.ApplyFog(
                    environment, ground, Engine_DynamicSky.HorizonColour(material), _day);
            }
        }

        TickRandomDrift(delta);
        TickLightning(delta);
        Engine_DynamicSky.ApplyClock(_time, _lightning, _strikeDir);
    }

    private void TickLightning(float delta)
    {
        if (_lightning > 0f)
        {
            _lightning = MathF.Max(0f, _lightning - (delta * 6f));
        }

        if (_now.Storm < 0.05f)
        {
            return;
        }

        _nextStrike -= delta;
        if (_nextStrike > 0f)
        {
            return;
        }

        float angle = (float)(_random.NextDouble() * Math.PI * 2.0);
        _strikeDir = new Vector3(MathF.Cos(angle), 0.25f, MathF.Sin(angle));
        _lightning = 0.6f + ((float)_random.NextDouble() * 0.6f) * _now.Storm;
        _nextStrike = 2f + ((float)_random.NextDouble() * 10f) / MathF.Max(_now.Storm, 0.2f);
    }
}
