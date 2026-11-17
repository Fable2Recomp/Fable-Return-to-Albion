using System;
using System.Collections.Generic;
using Godot;
using Refii.Core;

namespace Refii.Level;

/// <summary>Builds the region's sky and publishes its shared atmosphere state to world materials.</summary>
public static class Engine_SkyEnvironment
{
    private static readonly StringName BetaMieName = "beta_mie";
    private static readonly StringName BetaRayleighName = "beta_rayleigh";
    private static readonly StringName HasDomeName = "has_dome";
    private static readonly StringName HorizonDistanceName = "horizon_distance";
    private static readonly StringName SunEngineName = "sun_engine";
    private static readonly StringName WLightName = "w_light";
    private static readonly StringName WSunName = "w_sun";

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

        Engine_DynamicWeatherChoice.SetLevel(level);
        if (Engine_DynamicSky.Enabled)
        {
            Engine_DynamicSky.EnsureGlobals();
        }

        string shaderPath = Engine_DynamicSky.Enabled ? Engine_DynamicSky.ShaderPath : ShaderPath;
        var shader = GD.Load<Shader>(shaderPath);
        if (shader is null)
        {
            GD.Print($"[sky] shader missing at {shaderPath}");
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
        ApplyFrame(material, frame, sky);
        if (Engine_DynamicSky.Enabled)
        {
            Engine_DynamicSky.Prime(material, frame, sky, weather);
        }

        var environment = new Godot.Environment
        {
            // Under classic the title's own dome (Engine_ClassicSkyDome) is the sky and covers the
            // whole view; Godot's would be drawn and radiance-filtered behind it for nothing. The
            // material stays bound: the sky node still feeds it.
            BackgroundMode = Refii.Models.Engine_ClassicMaterial.Active
                ? Godot.Environment.BGMode.Color
                : Godot.Environment.BGMode.Sky,
            BackgroundColor = Colors.Black,
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

    public static void ApplyFrame(ShaderMaterial material, SkyFrame frame, SkyTheme theme)
    {
        (AtmosphereState state, ImageTexture lut) = ComputeDome(frame, theme);
        ApplyDomeUniforms(material, frame, state, lut);
        ApplyAtmosphereGlobals(frame, theme, state, lut);
    }

    /// <summary>
    /// The atmosphere for this frame — the dome's in-scatter table and the state behind it.
    /// </summary>
    /// <remarks>
    /// TAKES THE THEME, not just the evaluated frame, because the fog does. The Fogging record's
    /// six fields never reach a SkyFrame — they are not part of what the dome needs — and building
    /// the inputs here without them left <see cref="AtmosphereState.ScatteringMisc"/> holding a
    /// FoggingStart of zero for as long as nothing consumed it.
    /// </remarks>
    public static (AtmosphereState State, ImageTexture Lut) ComputeDome(
        SkyFrame frame, SkyTheme theme) => ComputeDome(frame, theme, null);

    /// <summary>
    /// As above, writing the table into <paramref name="reuse"/> when given one, so a dome
    /// re-evaluated many times a second updates one texture rather than creating one each time.
    /// </summary>
    [ThreadStatic]
    private static byte[]? _lutBytes;

    public static (AtmosphereState State, ImageTexture Lut) ComputeDome(
        SkyFrame frame, SkyTheme theme, ImageTexture? reuse)
    {
        AtmosphereInputs inputs = Engine_SkyAtmosphere.FromFrameScratch(frame, theme);
        AtmosphereState state = Engine_SkyAtmosphere.Compute(inputs);
        float[] lut = Engine_SkyAtmosphere.BuildInScatterLut(state);

        // The dynamic sky re-evaluates the dome at 15 Hz: the byte copy is kept per thread (Godot
        // copies it into the image), and an image that only updated the kept texture is released
        // by RetireLutImage rather than left to its finalizer.
        byte[] bytes = _lutBytes is { } kept && kept.Length == lut.Length * sizeof(float)
            ? kept
            : _lutBytes = new byte[lut.Length * sizeof(float)];
        Buffer.BlockCopy(lut, 0, bytes, 0, bytes.Length);
        var image = Image.CreateFromData(
            Engine_SkyAtmosphere.LutWidth, 1, false, Image.Format.Rgbaf, bytes);
        if (reuse is not null && reuse.GetWidth() == image.GetWidth() &&
            reuse.GetHeight() == image.GetHeight())
        {
            // --render-thread separate warning: never call image.Dispose() immediately after
            // texture.Update(image); route through Engine_TextureUpload so RD health and
            // storage-layout invariants are enforced consistently, then retire deterministically.
            Refii.Textures.Engine_TextureUpload.SetImage(reuse, image, allowD3D12Replace: false);
            RetireLutImage(image);
            return (state, reuse);
        }

        return (state, Refii.Textures.Engine_TextureUpload.CreateTexture(image));
    }

    /// <summary>Frames an updated LUT image is held before its wrapper is disposed.</summary>
    private const ulong LutImageHoldFrames = 2;

    [ThreadStatic]
    private static Queue<(Image Image, ulong After)>? _retiredLutImages;

    /// <summary>
    /// Disposes the images handed to <see cref="ImageTexture.Update"/> once the rendering server
    /// has released them, never in the same tick.
    /// </summary>
    /// <remarks>
    /// `texture_2d_update` takes a <c>Ref&lt;Image&gt;</c>; while that reference is live the
    /// wrapper's GC handle is the STRONG one, and the server's release swaps it back to weak and
    /// frees it. Disposing right after Update raced that swap: Dispose freed a handle the swap had
    /// already freed, threw "Handle is not initialized" out of ApplyAtmosphere, and the tick's dome,
    /// fog, water and clouds were never written (godot.log 2026-11-15, twice).
    /// </remarks>
    private static void RetireLutImage(Image image)
    {
        Queue<(Image Image, ulong After)> retired = _retiredLutImages ??= new Queue<(Image, ulong)>();
        ulong now = Engine.GetProcessFrames();
        while (retired.Count > 0 && retired.Peek().After <= now)
        {
            retired.Dequeue().Image.Dispose();
        }

        retired.Enqueue((image, now + LutImageHoldFrames));
    }

    public static void ApplyDomeUniforms(
        ShaderMaterial material, SkyFrame frame, AtmosphereState state, ImageTexture lut)
    {
        // ONLY WHEN IT IS A DIFFERENT TEXTURE. A texture parameter queues the material's uniform
        // set for a rebuild on the render thread; the continuous tick updates one texture in place.
        if (material.GetShaderParameter(Param_in_scatter_lut).AsGodotObject() != lut)
        {
            material.SetShaderParameter(Param_in_scatter_lut, lut);
        }

        material.SetShaderParameter(BetaRayleighName, new Vector3(
            state.BetaRayleigh[0], state.BetaRayleigh[1], state.BetaRayleigh[2]));
        material.SetShaderParameter(BetaMieName, new Vector3(
            state.BetaMie[0], state.BetaMie[1], state.BetaMie[2]));
        material.SetShaderParameter(SunEngineName, Engine_SkyFrame.ToDome(frame.SunDirection));
        material.SetShaderParameter(HorizonDistanceName,
            Engine_SkyAtmosphere.HorizonDistance);
    }

    /// <summary>
    /// Publishes the atmosphere to every world surface, as global shader parameters.
    /// </summary>
    /// <remarks>
    /// GLOBAL, NOT PER MATERIAL. The fog is one thing for the whole region and it reaches every
    /// prop, every wall and the ground — thousands of materials in a populated level, rebuilt as
    /// props stream in. Setting seven uniforms on each of them once a frame is not a cost worth
    /// paying for state that never differs between them, and it would also mean every code path
    /// that constructs a material having to remember to seed it.
    ///
    /// `project.godot` declares the parameters. A shader referring to a global that has not been
    /// declared fails to COMPILE, so the declaration has to be in project settings rather than
    /// added at runtime — the editor compiles these shaders too.
    ///
    /// The sky dome and the water take the same values through their own material uniforms and not
    /// through these, because they were already wired that way and a sky is drawn once.
    /// </remarks>
    public static void ApplyAtmosphereGlobals(
        SkyFrame frame, SkyTheme theme, AtmosphereState state, ImageTexture lut) =>
        ApplyAtmosphereGlobals(frame, theme, state, lut, report: true);

    public static float FogOpticalDepthScale
    {
        get
        {
            if (_lastAtmosphereState is not { } s || !s.HasFog)
            {
                return 0f;
            }

            return s.FogOpticalDepthAtFar;
        }
    }

    private static AtmosphereState? _lastAtmosphereState;

    /// <summary>The most recently published <see cref="AtmosphereState"/> for the standing region.</summary>
    public static AtmosphereState? LastAtmosphereState => _lastAtmosphereState;

    /// <summary>Publishes one atmosphere update, including guarded immutable near-fog endpoints.</summary>
    /// <param name="frame">The evaluated sky orientation used by the dome and fog.</param>
    /// <param name="theme">The resolved region theme; the supplied state already contains its fog curve.</param>
    /// <param name="state">The atmosphere state whose copied values seed both analytic and precomputed paths.</param>
    /// <param name="lut">The in-scatter texture shared with the dome.</param>
    /// <param name="report">False for the continuous tick, which would print every change.</param>
    public static void ApplyAtmosphereGlobals(
        SkyFrame frame, SkyTheme theme, AtmosphereState state, ImageTexture lut, bool report)
    {
        _lastAtmosphereState = state;

        // A render thread seeing any part of this update must fall back until all endpoint
        // copies are queued. This also leaves fallback selected if later publication fails.
        RenderingServer.GlobalShaderParameterSet(Engine_NearFogKnots.ReadyGlobal, false);
        var beta = new Vector3(
            state.BetaRayleigh[0] + state.BetaMie[0],
            state.BetaRayleigh[1] + state.BetaMie[1],
            state.BetaRayleigh[2] + state.BetaMie[2]);
        var curve = new Vector3(state.FogOpticalDepthAtFar, state.FogRecipFarRange, state.FogExponent);
        var range = new Vector4(state.ScatteringMisc[0], Engine_SkyAtmosphere.MaxFogDistance, state.ScatteringMisc[2], 0f);
        bool hasFog = state.HasFog;
        NearFogSnapshot nearFog = Engine_NearFogKnots.Create(hasFog, beta.X, beta.Y, beta.Z,
            curve.X, curve.Y, curve.Z, range.Y);
        // ONLY WHEN IT IS A DIFFERENT TEXTURE. Setting a sampler global queues EVERY material
        // that reads it for a uniform-set rebuild (`MaterialStorage::global_shader_parameter_set`,
        // the `texture_materials` loop) — every lit surface in the region. At the atmosphere's
        // 15 Hz that held the render thread at ~50 ms a frame. The table is updated in place.
        if (!ReferenceEquals(lut, _publishedLut))
        {
            RenderingServer.GlobalShaderParameterSet(AtmosInscatterName, lut);
            _publishedLut = lut;
        }

        // The sum, because the sum is what the guest's LUT baker exponentiates — it loads both
        // registers of the atmospheric constant block and adds them per channel before the exp.
        RenderingServer.GlobalShaderParameterSet(AtmosBetaName, new Vector3(
            state.BetaRayleigh[0] + state.BetaMie[0],
            state.BetaRayleigh[1] + state.BetaMie[1],
            state.BetaRayleigh[2] + state.BetaMie[2]));

        // The same axes and the same X flip `ApplyDomeUniforms` sends the dome, because the fog
        // samples the dome's table and has to agree with it about where the sun is.
        RenderingServer.GlobalShaderParameterSet(AtmosSunName,
            Engine_SkyFrame.ToDome(frame.SunDirection));

        // Off the STATE, which has already resolved it against the guest's default of white —
        // reading `theme.FogColour` here instead would let SkyTheme's own blue-grey stand-in
        // through on a theme that states no colour.
        RenderingServer.GlobalShaderParameterSet(AtmosCloseFogName, new Vector3(
            state.FogCloseColour[0], state.FogCloseColour[1], state.FogCloseColour[2]));

        RenderingServer.GlobalShaderParameterSet(AtmosRangeName, new Vector4(
            state.ScatteringMisc[0],
            Engine_SkyAtmosphere.MaxFogDistance,
            state.ScatteringMisc[2],
            0f));

        RenderingServer.GlobalShaderParameterSet(AtmosCurveName, new Vector3(
            state.FogOpticalDepthAtFar, state.FogRecipFarRange, state.FogExponent));

        RenderingServer.GlobalShaderParameterSet(AtmosFactorsName, new Vector2(
            Engine_SkyAtmosphere.FogBlendFactor, Engine_SkyAtmosphere.FogColourFactor));

        RenderingServer.GlobalShaderParameterSet(AtmosFogOnName, state.HasFog);

        // Publish invalidates independently as well, so the same tested publisher is safe
        // for initial loads, theme transitions, fog-free levels and later region reloads.
        Engine_NearFogKnots.Publish(nearFog,
            static (name, rgb) => RenderingServer.GlobalShaderParameterSet(name, new Vector3(rgb.Red, rgb.Green, rgb.Blue)),
            static (name, ready) => RenderingServer.GlobalShaderParameterSet(name, ready));
        Refii.AndroidRuntime.AndroidLeanShading.Atmosphere(hasFog, beta, curve, range, nearFog);

        if (report)
        {
            ReportAtmosphere(state);
        }
    }

    private static ImageTexture? _publishedLut;

    private static readonly StringName Param_in_scatter_lut = "in_scatter_lut";

    private static string _lastAtmosphereReport = string.Empty;

    /// <summary>
    /// Prints the fog actually being PUBLISHED, and what it comes to on the screen.
    /// </summary>
    /// <remarks>
    /// THE BUILD-TIME LOG IS NOT THIS. `Engine_LevelScene` prints the theme resolved when the level
    /// was constructed, but the theme is re-blended from the day set every frame, so what the
    /// shader receives can differ from what was logged — reasoning about the picture from the
    /// build-time numbers is reasoning about a different fog.
    ///
    /// It prints the SOLVED CURVE and then the fog percentage at a ladder of distances, because
    /// the curve's parameters are not readable by eye: an optical depth of 553 and an exponent of
    /// 1.14 say nothing about whether a wall thirty metres away should be visible, and that is the
    /// only question worth asking of a screenshot.
    ///
    /// Keyed on the formatted line rather than on a timer, so a stable atmosphere prints once and a
    /// changing one prints every time it actually changes — no rate limit to misread as a
    /// frequency, and no flood while the sun crosses the sky.
    /// </remarks>
    private static void ReportAtmosphere(AtmosphereState state)
    {
        if (!state.HasFog)
        {
            return;
        }

        float betaR = state.BetaRayleigh[0] + state.BetaMie[0];
        float betaG = state.BetaRayleigh[1] + state.BetaMie[1];
        float betaB = state.BetaRayleigh[2] + state.BetaMie[2];

        var ladder = new System.Text.StringBuilder();
        foreach (float metres in new[] { 25f, 50f, 100f, 200f, 400f, 800f })
        {
            float past = metres - state.ScatteringMisc[0];
            if (past <= 0f)
            {
                ladder.Append($" {metres:0}=0%");
                continue;
            }

            float t = MathF.Min(past, Engine_SkyAtmosphere.MaxFogDistance) * state.FogRecipFarRange;
            float depth = state.FogOpticalDepthAtFar * MathF.Pow(t, state.FogExponent);

            // Green, and the effective transmittance rather than the raw one — the same number the
            // shader arrives at, damping included.
            float raw = MathF.Exp(-betaG * depth);
            float fog = Engine_SkyAtmosphere.FogBlendFactor * (1f - raw);
            ladder.Append($" {metres:0}={fog * 100f:0}%");
        }

        // SPLIT, NOT JUST THE SUM. The sum is what the shader exponentiates, so it is what decides
        // how thick the fog is — but it cannot say WHY, and the two halves fail differently. Mie is
        // wavelength-independent and Rayleigh goes as 1/lambda^4, so a total whose blue/red ratio
        // is near one is a Mie-dominated atmosphere: a flat grey haze rather than the blue-biased
        // extinction that makes distance read as distance. Printing only the sum makes that
        // indistinguishable from a Rayleigh term that is simply too large.
        string chroma = betaR > 0f
            ? $"b/r={betaB / betaR:0.00}"
            : "b/r=n/a";

        string split =
            $"rayleigh=({state.BetaRayleigh[0]:0.00000},{state.BetaRayleigh[1]:0.00000}," +
            $"{state.BetaRayleigh[2]:0.00000}) " +
            $"mie=({state.BetaMie[0]:0.00000},{state.BetaMie[1]:0.00000},{state.BetaMie[2]:0.00000})";

        string line =
            $"[fog] published: start={state.ScatteringMisc[0]:0.#} " +
            $"depth={state.FogOpticalDepthAtFar:0.##} " +
            $"range={(state.FogRecipFarRange > 0f ? 1f / state.FogRecipFarRange : 0f):0.#} " +
            $"k={state.FogExponent:0.###} " +
            $"beta=({betaR:0.00000},{betaG:0.00000},{betaB:0.00000}) " +
            $"closemax={(state.ScatteringMisc[2] > 0f ? 1f / state.ScatteringMisc[2] : 0f):0.#} " +
            $"Fx={Engine_SkyAtmosphere.FogBlendFactor:0.##} {chroma} |{ladder}\n" +
            $"[fog] beta split: {split}";

        if (line == _lastAtmosphereReport)
        {
            return;
        }

        _lastAtmosphereReport = line;
        GD.Print(line);
    }

    public static void ApplyWaterFrame(
        ShaderMaterial material, SkyFrame frame, AtmosphereState state, ImageTexture lut)
    {
        ApplyDomeUniforms(material, frame, state, lut);
        material.SetShaderParameter(HasDomeName, true);

        Vector3 sun = Engine_SkyFrame.ToGodot(frame.SunDirection);
        if (sun.LengthSquared() > 1e-8f)
        {
            sun = sun.Normalized();
        }

        float sunUp = Math.Clamp(sun.Y * 3f, 0f, 1f);
        material.SetShaderParameter(WLightName, new Vector4(sun.X, sun.Y, sun.Z, 0f));
        material.SetShaderParameter(WSunName, new Vector4(
            1f, 0.55f + (0.42f * sunUp), 0.30f + (0.60f * sunUp), sun.Y));
    }

    public static List<byte[]> BuildGdbChain(LevelEntry level, Func<string, byte[]?> read) =>
        BuildGdbChain(level, read, out _);

    public static List<byte[]> BuildGdbChain(
        LevelEntry level, Func<string, byte[]?> read, out string levelGdbPath) =>
        BuildGdbChain(level, read, out levelGdbPath, Refii.Core.Engine_Title.Active);

    public static List<byte[]> BuildGdbChain(
        LevelEntry level, Func<string, byte[]?> read, out string levelGdbPath,
        Refii.Core.Engine_Title title)
    {
        var chain = new List<byte[]>();

        byte[]? levelGdb = Engine_LevelGdb.TryRead(level, read, out levelGdbPath);
        if (levelGdb is not null)
        {
            chain.Add(levelGdb);
        }

        AppendSharedGdbs(chain, read);
        AppendTitleThemes(chain, levelGdb, read, title);
        return chain;
    }

    public static List<byte[]> BuildGdbChain(string levelGdbPath, Func<string, byte[]?> read) =>
        BuildGdbChain(levelGdbPath, read, Refii.Core.Engine_Title.Active);

    public static List<byte[]> BuildGdbChain(
        string levelGdbPath, Func<string, byte[]?> read, Refii.Core.Engine_Title title)
    {
        var chain = new List<byte[]>();

        byte[]? levelGdb = levelGdbPath.Length == 0 ? null : read(levelGdbPath);
        if (levelGdb is not null)
        {
            chain.Add(levelGdb);
        }

        AppendSharedGdbs(chain, read);
        AppendTitleThemes(chain, levelGdb, read, title);
        return chain;
    }

    /// <summary>
    /// Fable 3 keeps its themes out of the gdbs: the level's stack is <c>envthemebase</c> and the
    /// <c>.envtheme</c> its <c>LevelSpecificData.EnvironmentTheme</c> names
    /// (<see cref="Engine_EnvThemeFile.AppendLevelThemes"/>), appended after the gdbs, lowest
    /// layer first. The theme readers take the stack in place of any day set the gdbs carry.
    /// </summary>
    private static void AppendTitleThemes(
        List<byte[]> chain, byte[]? levelGdb, Func<string, byte[]?> read, Refii.Core.Engine_Title title)
    {
        if (title == Refii.Core.Engine_Title.Fable3)
        {
            Engine_EnvThemeFile.AppendLevelThemes(levelGdb, read, chain);
        }
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

    // Made once: a string literal converts to a new StringName on every call.
    private static readonly StringName AtmosInscatterName = "atmos_inscatter";
    private static readonly StringName AtmosBetaName = "atmos_beta";
    private static readonly StringName AtmosSunName = "atmos_sun";
    private static readonly StringName AtmosCloseFogName = "atmos_close_fog";
    private static readonly StringName AtmosRangeName = "atmos_range";
    private static readonly StringName AtmosCurveName = "atmos_curve";
    private static readonly StringName AtmosFactorsName = "atmos_factors";
    private static readonly StringName AtmosFogOnName = "atmos_fog_on";
}
