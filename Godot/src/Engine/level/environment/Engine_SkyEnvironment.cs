using System;
using System.Collections.Generic;
using Godot;

namespace Refii.Level;

public static class Engine_SkyEnvironment
{
    public const string ShaderPath = "res://src/Engine/level/environment/sky.gdshader";

    public const string EnvironmentThemesPath = "environmentthemes/environmentthemes.gdb";

    public const string GlobalsPath = "globals/globals.gdb";

    public static WorldEnvironment? Build(
        LevelEntry level, Func<string, byte[]?> read, out SkyTheme sky, out WeatherTheme weather)
    {
        sky = new SkyTheme();
        weather = new WeatherTheme();

        List<byte[]> chain = BuildGdbChain(level, read);
        if (chain.Count == 0)
        {
            return null;
        }

        Engine_EnvironmentTheme.TryExtractSky(chain, out sky);
        Engine_EnvironmentTheme.TryExtractWeather(chain, out weather);

        var shader = GD.Load<Shader>(ShaderPath);
        if (shader is null)
        {
            GD.Print($"[sky] shader missing at {ShaderPath}");
            return null;
        }

        float timeOfDay = sky.SourceTimeOfDay >= 0f ? sky.SourceTimeOfDay : 0.5f;
        SkyFrame frame = Engine_SkyFrame.Evaluate(sky, timeOfDay);

        var material = new ShaderMaterial { Shader = shader };
        material.SetShaderParameter("sky_top", ToVector4(frame.SkyTop, 1f));
        material.SetShaderParameter("sky_bottom", ToVector4(frame.SkyBottom, 1f));
        material.SetShaderParameter("sky_sunset", ToVector4(frame.SkySunset, 1f));
        material.SetShaderParameter("sky_params", new Vector4(
            frame.SkyParams[0], frame.SkyParams[1], frame.SkyParams[2], frame.SkyParams[3]));
        material.SetShaderParameter("sky_sun", ToVector4(frame.SunDirection, 0f));

        var environment = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Sky,
            Sky = new Sky { SkyMaterial = material },
            AmbientLightSource = Godot.Environment.AmbientSource.Sky,
        };

        GD.Print(
            $"[sky] {level}: tod={frame.TimeOfDay:0.##} " +
            $"sun=({frame.SunDirection[0]:0.00},{frame.SunDirection[1]:0.00},{frame.SunDirection[2]:0.00}) " +
            $"ray={frame.SkyParams[2]:0.00} mie={frame.SkyParams[3]:0.00}");

        return new WorldEnvironment { Name = "SkyEnvironment", Environment = environment };
    }

    public static List<byte[]> BuildGdbChain(LevelEntry level, Func<string, byte[]?> read)
    {
        var chain = new List<byte[]>();

        byte[]? levelGdb = ReadLevelGdb(level, read);
        if (levelGdb is not null)
        {
            chain.Add(levelGdb);
        }

        byte[]? themes = read(EnvironmentThemesPath) ?? read("data/" + EnvironmentThemesPath);
        if (themes is not null)
        {
            chain.Add(themes);
        }

        byte[]? globals = read(GlobalsPath) ?? read("data/" + GlobalsPath);
        if (globals is not null)
        {
            chain.Add(globals);
        }

        return chain;
    }

    private static byte[]? ReadLevelGdb(LevelEntry level, Func<string, byte[]?> read) =>
        Engine_LevelGdb.TryRead(level, read, out _);

    private static Vector4 ToVector4(float[] values, float w) =>
        new(values[0], values[1], values[2], w);
}
