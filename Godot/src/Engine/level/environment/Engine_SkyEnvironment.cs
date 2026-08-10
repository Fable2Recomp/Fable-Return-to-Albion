using System;
using System.Collections.Generic;
using Godot;

namespace Refii.Level;

public static class Engine_SkyEnvironment
{
    public const string ShaderPath = "res://src/Engine/level/environment/sky.gdshader";

    /// <summary>
    /// Name of the region's environment node. It is also the address everything in the region
    /// looks the lighting up at, so it is a constant rather than a literal in two places.
    /// </summary>
    public const string NodeName = "SkyEnvironment";

    public const string EnvironmentThemesPath = "environmentthemes/environmentthemes.gdb";

    public const string GlobalsPath = "globals/globals.gdb";

    public static WorldEnvironment? Build(
        LevelEntry level, Func<string, byte[]?> read, out SkyTheme sky, out WeatherTheme weather)
    {
        sky = new SkyTheme();
        weather = new WeatherTheme();

        List<byte[]> chain = BuildGdbChain(level, read, out string levelGdbPath);
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
        string? forcedTod = System.Environment.GetEnvironmentVariable("REFII_FORCE_TOD");
        if (forcedTod is not null &&
            float.TryParse(
                forcedTod, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float forced))
        {
            timeOfDay = forced;
        }

        SkyFrame frame = Engine_SkyFrame.Evaluate(sky, timeOfDay);

        var material = new ShaderMaterial { Shader = shader };
        ApplyFrame(material, frame);

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

        var node = new Engine_SkyEnvironmentNode { Name = NodeName, Environment = environment };
        node.Initialise(sky, timeOfDay, levelGdbPath);
        return node;
    }

    public static void ApplyFrame(ShaderMaterial material, SkyFrame frame)
    {
        material.SetShaderParameter("sky_top", ToVector4(frame.SkyTop, 1f));
        material.SetShaderParameter("sky_bottom", ToVector4(frame.SkyBottom, 1f));
        material.SetShaderParameter("sky_sunset", ToVector4(frame.SkySunset, 1f));
        material.SetShaderParameter("sky_params", new Vector4(
            frame.SkyParams[0], frame.SkyParams[1], frame.SkyParams[2], frame.SkyParams[3]));
        material.SetShaderParameter("sky_sun", ToVector4(frame.SunDirection, 0f));
    }

    public static List<byte[]> BuildGdbChain(LevelEntry level, Func<string, byte[]?> read) =>
        BuildGdbChain(level, read, out _);

    public static List<byte[]> BuildGdbChain(
        LevelEntry level, Func<string, byte[]?> read, out string levelGdbPath)
    {
        var chain = new List<byte[]>();

        byte[]? levelGdb = Engine_LevelGdb.TryRead(level, read, out levelGdbPath);
        if (levelGdb is not null)
        {
            chain.Add(levelGdb);
        }

        AppendSharedGdbs(chain, read);
        return chain;
    }

    public static List<byte[]> BuildGdbChain(string levelGdbPath, Func<string, byte[]?> read)
    {
        var chain = new List<byte[]>();

        byte[]? levelGdb = levelGdbPath.Length == 0 ? null : read(levelGdbPath);
        if (levelGdb is not null)
        {
            chain.Add(levelGdb);
        }

        AppendSharedGdbs(chain, read);
        return chain;
    }

    private static void AppendSharedGdbs(List<byte[]> chain, Func<string, byte[]?> read)
    {
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
    }

    private static Vector4 ToVector4(float[] values, float w) =>
        new(values[0], values[1], values[2], w);
}
