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

        float requestedTime = -1f;
        string? forcedEnv = System.Environment.GetEnvironmentVariable("REFII_FORCE_TOD");
        if (forcedEnv is not null &&
            float.TryParse(
                forcedEnv, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float forcedTime))
        {
            requestedTime = forcedTime;
        }

        if (requestedTime >= 0f)
        {
            Engine_ThemeTimeline.TryExtractSkyBlended(chain, requestedTime, out sky);
        }
        else
        {
            Engine_EnvironmentTheme.TryExtractSky(chain, out sky);
            if (sky.SourceTimeOfDay >= 0f)
            {
                Engine_ThemeTimeline.TryExtractSkyBlended(
                    chain, sky.SourceTimeOfDay, out sky);
            }
        }

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
        (AtmosphereState state, ImageTexture lut) = ComputeDome(frame);
        ApplyDomeUniforms(material, frame, state, lut);
    }

    public static (AtmosphereState State, ImageTexture Lut) ComputeDome(SkyFrame frame)
    {
        var inputs = new AtmosphereInputs
        {
            SunIntensity = frame.SkyParams[0],
            ComplementaryBias = frame.SkyParams[1],
            BetaRayleighMultiplier = frame.SkyParams[2],
            BetaMieMultiplier = frame.SkyParams[3],
        };
        for (int i = 0; i < 3; i++)
        {
            inputs.SkyColour[i] = frame.SkyTop[i];
            inputs.ComplementaryColour[i] = frame.SkyBottom[i];
            inputs.SunsetColour[i] = frame.SkySunset[i];
        }

        inputs.SunDirection[0] = frame.SunDirection[0];
        inputs.SunDirection[1] = frame.SunDirection[2];
        inputs.SunDirection[2] = frame.SunDirection[1];
        inputs.SunDirection[3] = 0f;

        AtmosphereState state = Engine_SkyAtmosphere.Compute(inputs);
        float[] lut = Engine_SkyAtmosphere.BuildInScatterLut(state);

        var bytes = new byte[lut.Length * sizeof(float)];
        Buffer.BlockCopy(lut, 0, bytes, 0, bytes.Length);
        var image = Image.CreateFromData(
            Engine_SkyAtmosphere.LutWidth, 1, false, Image.Format.Rgbaf, bytes);
        return (state, ImageTexture.CreateFromImage(image));
    }

    public static void ApplyDomeUniforms(
        ShaderMaterial material, SkyFrame frame, AtmosphereState state, ImageTexture lut)
    {
        material.SetShaderParameter("in_scatter_lut", lut);
        material.SetShaderParameter("beta_rayleigh", new Vector3(
            state.BetaRayleigh[0], state.BetaRayleigh[1], state.BetaRayleigh[2]));
        material.SetShaderParameter("beta_mie", new Vector3(
            state.BetaMie[0], state.BetaMie[1], state.BetaMie[2]));
        material.SetShaderParameter("sun_engine", new Vector3(
            -frame.SunDirection[0], frame.SunDirection[2], frame.SunDirection[1]));
        material.SetShaderParameter("horizon_distance",
            Engine_SkyAtmosphere.HorizonDistance);
        material.SetShaderParameter("dome_exposure", 10.0f);
    }

    public static void ApplyWaterFrame(
        ShaderMaterial material, SkyFrame frame, AtmosphereState state, ImageTexture lut)
    {
        ApplyDomeUniforms(material, frame, state, lut);

        var sun = new Vector3(
            -frame.SunDirection[0],
            frame.SunDirection[1],
            frame.SunDirection[2]);
        if (sun.LengthSquared() > 1e-8f)
        {
            sun = sun.Normalized();
        }

        float sunUp = Math.Clamp(sun.Y * 3f, 0f, 1f);
        material.SetShaderParameter("w_light", new Vector4(sun.X, sun.Y, sun.Z, 0f));
        material.SetShaderParameter("w_sun", new Vector4(
            1f, 0.55f + (0.42f * sunUp), 0.30f + (0.60f * sunUp), sun.Y));
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
