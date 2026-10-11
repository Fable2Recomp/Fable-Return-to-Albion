using System;
using Godot;

namespace Refii.Level;

[Tool]
public partial class Engine_DynamicSkyPreview : WorldEnvironment
{
    private float _timeOfDay = 8.5f;
    private DynamicWeatherPreset _preset = DynamicWeatherPreset.PartlyCloudy;
    private float _blendSeconds = 3f;
    private float _exposure = 1f;
    private float _dream = 0.6f;
    private float _haze = 0.4f;
    private bool _animate = true;
    private float _cloudSpeed = 1f;
    private bool _volumetricClouds;
    private bool _cloudShadows;
    private Engine_DynamicCloudShadows? _cloudShadowCaster;
    private DirectionalLight3D? _previewSun;
    private bool _ownsPreviewSun;
    private float _originalSunEnergy;
    private Transform3D _originalSunTransform;
    private float _dayLengthSeconds = 0f;

    private ShaderMaterial? _material;
    private Engine_DynamicSkyDriver? _driver;
    private Engine_DynamicPrecipitation? _precipitation;
    private readonly SkyTheme _theme = new();

    [Export(PropertyHint.Range, "0,24,0.05,suffix:h")]
    public float TimeOfDay
    {
        get => _timeOfDay;
        set
        {
            _timeOfDay = value;
            PushFrame();
        }
    }

    [Export]
    public DynamicWeatherPreset Weather
    {
        get => _preset;
        set
        {
            _preset = value;
            _driver?.SetWeather(
                value == DynamicWeatherPreset.Theme ? DynamicWeatherPreset.PartlyCloudy : value,
                _blendSeconds);
        }
    }

    [Export]
    public bool VolumetricClouds
    {
        get => _volumetricClouds;
        set
        {
            _volumetricClouds = value;
            if (_material is not null)
                Engine_DynamicSky.ApplyCloudChoice(_material, value);
        }
    }

    [Export]
    public bool CloudShadows
    {
        get => _cloudShadows;
        set { _cloudShadows = value; SyncCloudShadows(); }
    }

    [Export(PropertyHint.Range, "0,60,0.5,suffix:s")]
    public float BlendSeconds { get => _blendSeconds; set => _blendSeconds = value; }

    [Export(PropertyHint.Range, "0,4,0.01")]
    public float Exposure
    {
        get => _exposure;
        set { _exposure = value; _material?.SetShaderParameter("exposure", value); }
    }

    [Export(PropertyHint.Range, "0,1,0.01")]
    public float Dream
    {
        get => _dream;
        set { _dream = value; _material?.SetShaderParameter("dream", value); }
    }

    [Export(PropertyHint.Range, "0,1,0.01")]
    public float Haze
    {
        get => _haze;
        set { _haze = value; _material?.SetShaderParameter("haze", value); }
    }

    [Export(PropertyHint.Range, "0,10,0.1")]
    public float CloudSpeed
    {
        get => _cloudSpeed;
        set
        {
            _cloudSpeed = Math.Clamp(value, 0f, 10f);
            _material?.SetShaderParameter("cloud_motion_multiplier", _cloudSpeed);
        }
    }

    [Export]
    public bool Animate { get => _animate; set => _animate = value; }

    private DynamicPrecipitationLook _look = DynamicPrecipitationLook.Default;

    private void PushLook()
    {
        if (_precipitation is not null)
        {
            _precipitation.Look = _look;
        }
    }

    [ExportGroup("Precipitation")]
    [Export(PropertyHint.Range, "0,1,0.01")]
    public float RainOpacity { get => _look.RainOpacity; set { _look.RainOpacity = value; PushLook(); } }

    [Export(PropertyHint.Range, "0.001,0.05,0.001")]
    public float RainWidth { get => _look.RainWidth; set { _look.RainWidth = value; PushLook(); } }

    [Export(PropertyHint.Range, "0.02,2,0.01")]
    public float RainLength { get => _look.RainLength; set { _look.RainLength = value; PushLook(); } }

    [Export(PropertyHint.Range, "1,40,0.5")]
    public float RainSpeed { get => _look.RainSpeed; set { _look.RainSpeed = value; PushLook(); } }

    [Export(PropertyHint.Range, "0,1,0.01")]
    public float RainDensity { get => _look.RainDensity; set { _look.RainDensity = value; PushLook(); } }

    [Export(PropertyHint.Range, "0,1,0.01")]
    public float SnowOpacity { get => _look.SnowOpacity; set { _look.SnowOpacity = value; PushLook(); } }

    [Export(PropertyHint.Range, "0.005,0.1,0.001")]
    public float SnowSize { get => _look.SnowSize; set { _look.SnowSize = value; PushLook(); } }

    [Export(PropertyHint.Range, "0.1,5,0.05")]
    public float SnowSpeed { get => _look.SnowSpeed; set { _look.SnowSpeed = value; PushLook(); } }

    [Export(PropertyHint.Range, "0,1,0.01")]
    public float SnowDensity { get => _look.SnowDensity; set { _look.SnowDensity = value; PushLook(); } }

    [Export(PropertyHint.Range, "0,1,0.01")]
    public float SnowSway { get => _look.SnowSway; set { _look.SnowSway = value; PushLook(); } }

    [ExportGroup("")]

    [Export(PropertyHint.Range, "0,600,1,suffix:s")]
    public float DayLengthSeconds { get => _dayLengthSeconds; set => _dayLengthSeconds = value; }

    public override void _Ready()
    {
        _material = Engine_DynamicSky.BuildMaterial(cubemapFableDome: false);
        if (_material is null)
        {
            return;
        }

        Engine_DynamicSky.ApplyCloudChoice(_material, _volumetricClouds);
        _material.SetShaderParameter("exposure", _exposure);
        _material.SetShaderParameter("dream", _dream);
        _material.SetShaderParameter("haze", _haze);
        _material.SetShaderParameter("cloud_motion_multiplier", _cloudSpeed);

        Environment = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Sky,
            Sky = new Sky { SkyMaterial = _material, RadianceSize = Sky.RadianceSizeEnum.Size32 },
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = new Color(0.5f, 0.5f, 0.55f),
            ReflectedLightSource = Godot.Environment.ReflectionSource.Disabled,
            TonemapMode = Godot.Environment.ToneMapper.Filmic,
        };

        _previewSun = GetParent()?.GetNodeOrNull<DirectionalLight3D>("Sun");
        _ownsPreviewSun = _previewSun is null;
        if (_previewSun is null)
        {
            _previewSun = new DirectionalLight3D
            {
                Name = "DynamicSkySun",
                ShadowEnabled = true,
                DirectionalShadowMaxDistance = 600f,
                DirectionalShadowBlendSplits = true,
                SkyMode = DirectionalLight3D.SkyModeEnum.LightOnly,
            };
            AddChild(_previewSun);
            _previewSun.TopLevel = true;
        }
        _originalSunEnergy = _previewSun.LightEnergy;
        _originalSunTransform = _previewSun.Transform;

        _driver = new Engine_DynamicSkyDriver();
        var initialWeather = _preset == DynamicWeatherPreset.Theme ? DynamicWeatherPreset.PartlyCloudy : _preset;
        _driver.SetWeather(initialWeather, 0.01f);
        Engine_DynamicSky.ApplyWeather(_material, DynamicWeatherState.Of(initialWeather));
        Engine_DynamicSky.ApplyWind(_material, new Vector2(1f, 0.25f));
        _driver.SetWind(new Vector2(1f, 0.25f));

        _precipitation = GetNodeOrNull<Engine_DynamicPrecipitation>(
            Engine_DynamicPrecipitation.NodeName);
        if (_precipitation is null && Engine_DynamicPrecipitation.Build() is { } built)
        {
            _precipitation = built;
            AddChild(_precipitation);
        }

        PushLook();

        PushFrame();
    }

    public override void _Process(double delta)
    {
        if (_material is null || _driver is null)
        {
            return;
        }

        if (_animate)
        {
            _driver.Tick((float)delta, _material, _precipitation, Environment, _volumetricClouds);
        }

        if (_dayLengthSeconds > 0f && _animate)
        {
            _timeOfDay = (_timeOfDay + ((float)delta * 24f / _dayLengthSeconds)) % 24f;
            PushFrame();
        }
        SyncCloudShadows();
    }

    private void SyncCloudShadows()
    {
        if (_material is null || !IsInsideTree()) return;
        Camera3D? camera = GetViewport().GetCamera3D();
#if TOOLS
        if (Engine.IsEditorHint()) camera ??= EditorInterface.Singleton.GetEditorViewport3D(0)?.GetCamera3D();
#endif
        Engine_DynamicCloudShadows.Sync(this, ref _cloudShadowCaster, _material, _cloudShadows, camera, _previewSun);
    }

    public override void _ExitTree()
    {
        if (!_ownsPreviewSun && _previewSun is not null && GodotObject.IsInstanceValid(_previewSun))
        {
            _previewSun.LightEnergy = _originalSunEnergy;
            _previewSun.Transform = _originalSunTransform;
        }
    }

    private void PushFrame()
    {
        if (_material is null)
        {
            return;
        }

        SkyFrame frame = Engine_SkyFrame.Evaluate(_theme, _timeOfDay / 24f);
        Engine_DynamicSky.ApplyFrame(_material, frame, _theme);
        _driver?.SetDaylight(Engine_DynamicSky.Daylight(frame));
        if (_previewSun is not null)
        {
            Vector3 towardSun = Engine_SkyFrame.ToGodot(frame.SunDirection).Normalized();
            Basis basis = Basis.LookingAt(-towardSun, Mathf.Abs(towardSun.Y) > 0.99f ? Vector3.Forward : Vector3.Up);
            if (_previewSun.IsInsideTree()) _previewSun.GlobalBasis = basis;
            else _previewSun.Basis = basis;
            _previewSun.LightEnergy = _originalSunEnergy * Engine_DynamicSky.Daylight(frame);
        }
        SyncCloudShadows();
    }
}
