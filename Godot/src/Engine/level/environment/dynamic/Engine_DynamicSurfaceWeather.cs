using System;
using Godot;

namespace Refii.Level;

public sealed class Engine_DynamicSurfaceState
{
    public float Wetness { get; private set; }
    public float Snow { get; private set; }
    public float Puddles => Math.Clamp((Wetness - 0.55f) / 0.45f, 0f, 1f);

    public void Advance(float delta, in DynamicWeatherState weather, float daylight, bool enabled)
    {
        if (!enabled) { Wetness = 0f; Snow = 0f; return; }
        if (!float.IsFinite(delta) || delta <= 0f) return;
        var w = weather.Sanitized();
        float day = float.IsFinite(daylight) ? Math.Clamp(daylight, 0f, 1f) : 0f;
        float previous = Snow;
        float snowTarget = w.Snow;
        Snow = Approach(Snow, snowTarget, delta / (snowTarget > Snow ? 45f : 150f / (0.3f + day)));
        float melt = MathF.Max(previous - Snow, 0f);
        float wetTarget = MathF.Max(w.Rain, melt > 0f ? 0.65f : 0f);
        Wetness = Approach(Wetness, wetTarget, delta / (wetTarget > Wetness ? 16f : 100f / (0.25f + day)));
        Wetness = Math.Clamp(Wetness + melt * 0.65f, 0f, 1f);
    }

    private static float Approach(float value, float target, float amount) =>
        value < target ? MathF.Min(target, value + amount) : MathF.Max(target, value - amount);
}

public static class Engine_DynamicSurfaceWeather
{
    public static void EnsureGlobals()
    {
        if (!Engine.IsEditorHint()) return;
        var names = RenderingServer.GlobalShaderParameterGetList();
        var definitions = new (string Name, RenderingServer.GlobalShaderParameterType Type, Variant Value)[]
        {
            ("dynamic_surface_weather", RenderingServer.GlobalShaderParameterType.Vec4, Vector4.Zero),
            ("dynamic_surface_time", RenderingServer.GlobalShaderParameterType.Float, 0f),
            ("dynamic_surface_rain", RenderingServer.GlobalShaderParameterType.Float, 0f),
            ("dynamic_track_map", RenderingServer.GlobalShaderParameterType.Sampler2D, default),
            ("dynamic_track_projection", RenderingServer.GlobalShaderParameterType.Vec4, Vector4.Zero),
            ("dynamic_sun_toward", RenderingServer.GlobalShaderParameterType.Vec3, Vector3.Up),
            ("dynamic_sun_radiance", RenderingServer.GlobalShaderParameterType.Vec3, Vector3.Zero),
            ("dynamic_cloud_mask", RenderingServer.GlobalShaderParameterType.Sampler2D, default),
            ("dynamic_cloud_projection", RenderingServer.GlobalShaderParameterType.Vec4, Vector4.Zero),
        };
        foreach (var entry in definitions)
            if (!names.Contains(entry.Name)) RenderingServer.GlobalShaderParameterAdd(entry.Name, entry.Type, entry.Value);
    }

    public const string SwitchKey = "dynamic_sky_surface_weather";
    public static bool Enabled => Engine_DynamicSky.Enabled && Refii.Core.Engine_Switches.Flag(SwitchKey, false);

    public static void Publish(Engine_DynamicSurfaceState state, float time, bool enabled, float rain = 0f)
    {
        RenderingServer.GlobalShaderParameterSet("dynamic_surface_weather", enabled
            ? new Vector4(state.Wetness, state.Snow, state.Puddles, 1f) : Vector4.Zero);
        RenderingServer.GlobalShaderParameterSet("dynamic_surface_time", time);
        RenderingServer.GlobalShaderParameterSet("dynamic_surface_rain", enabled ? Engine_DynamicInteractionState.Unit(rain) : 0f);
    }

    public static void Reset()
    {
        RenderingServer.GlobalShaderParameterSet("dynamic_surface_weather", Vector4.Zero);
        RenderingServer.GlobalShaderParameterSet("dynamic_surface_rain", 0f);
        RenderingServer.GlobalShaderParameterSet("dynamic_track_projection", Vector4.Zero);
    }
}
