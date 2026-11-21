using System;
using System.Collections.Generic;
using Godot;
using Refii.Core;

namespace Refii.AndroidRuntime;

/// <summary>
/// `sky_dome_globals`: the sky dome (sky.gdshader, FABLE_SKY_GLOBALS) reads the sun and the two
/// scattering betas from global uniforms, and the atmosphere tick no longer writes them to the sky
/// material. Off by default; read once per process.
/// </summary>
/// <remarks>
/// <para>WHY. Any write to a sky material's uniforms marks the sky dirty (sky.cpp,
/// <c>uniform_set_updated</c>), and Godot then re-renders and re-filters its radiance map. Under
/// `sky_radiance_lean` the map is held at 32 texels but the dome's four writes still dirtied it at the
/// atmosphere's 15 Hz. A scalar global write only updates the global buffer, so with this on the map
/// is filtered when the sky is built and when its table texture changes, not every tick.</para>
/// <para>THE SAME PICTURE, except that the dome's sun follows <c>atmos_sun</c>, which is published
/// every frame, instead of the material's copy written at 15 Hz: a step of well under a hundredth of
/// a degree. While something reads the radiance map (<see cref="Refii.Level.Engine_SkyCosts"/>; never
/// on a phone so far) the tick still writes one value so the map keeps following the sky.</para>
/// </remarks>
public static class AndroidSkyGlobals
{
    /// <summary>The refii.toml setting.</summary>
    public const string Key = "sky_dome_globals";

    /// <summary>The sky shader's define.</summary>
    public const string Define = "FABLE_SKY_GLOBALS";

    /// <summary>The two new globals (atmos_sun is declared in project.godot already).</summary>
    public const string BetaRayleighGlobal = "fable_sky_beta_rayleigh";

    /// <summary>See <see cref="BetaRayleighGlobal"/>.</summary>
    public const string BetaMieGlobal = "fable_sky_beta_mie";

    private static readonly StringName HorizonDistanceName = "horizon_distance";

    /// <summary>Whether the values ask for it; unset is off. Pure.</summary>
    public static bool Wanted(IReadOnlyDictionary<string, string> values) => Engine_RefiiConfig.Bool(values, Key, false);

    /// <summary>The sky shader's text with the define. Pure.</summary>
    public static string Compose(string code) => Refii.Fx.Engine_FxDepthReads.Compose(code, Define);

    /// <summary>Whether the tick must still dirty the material: while the radiance map may be read. Pure.</summary>
    public static bool KeepsRefreshing(bool radianceKnownUnread) => !radianceKnownUnread;

    private static readonly Lazy<bool> Chosen = new(() =>
    {
        if (!Engine_Platform.IsAndroid)
        {
            return false;
        }

        try
        {
            return Wanted(Engine_RefiiConfig.Read(out _));
        }
        catch (Exception)
        {
            return false;
        }
    }, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly object Gate = new();
    private static Shader? _built;

    /// <summary>
    /// The sky shader at <paramref name="path"/>: the file, or with the key on and the plain dome
    /// (not the dynamic sky), one shared build with <see cref="Define"/>, its globals declared first.
    /// </summary>
    public static Shader? Load(string path)
    {
        Shader? file = GD.Load<Shader>(path);
        if (file is null || !Chosen.Value || path != Refii.Level.Engine_SkyEnvironment.ShaderPath)
        {
            return file;
        }

        lock (Gate)
        {
            if (_built is null)
            {
                // Declared before the build compiles: a global it names must exist by then.
                Godot.Collections.Array<StringName> declared = RenderingServer.GlobalShaderParameterGetList();
                if (!declared.Contains(BetaRayleighGlobal))
                {
                    RenderingServer.GlobalShaderParameterAdd(BetaRayleighGlobal, RenderingServer.GlobalShaderParameterType.Vec3, new Vector3(0.001f, 0.0015f, 0.004f));
                }

                if (!declared.Contains(BetaMieGlobal))
                {
                    RenderingServer.GlobalShaderParameterAdd(BetaMieGlobal, RenderingServer.GlobalShaderParameterType.Vec3, new Vector3(0.0056f, 0.0064f, 0.0105f));
                }

                _built = new Shader { Code = Compose(file.Code) };
            }

            return _built;
        }
    }

    /// <summary>Whether <paramref name="material"/> draws this class's build.</summary>
    public static bool Drives(ShaderMaterial? material) => _built is not null && material is not null && ReferenceEquals(material.Shader, _built);

    /// <summary>
    /// The dome's hook (Engine_SkyEnvironment.ApplyDomeUniforms): for this class's build, publishes the
    /// betas as globals and writes the material only when needed; true when it did the dome's writes.
    /// </summary>
    public static bool Publish(ShaderMaterial material, float[] betaRayleigh, float[] betaMie)
    {
        if (!Drives(material))
        {
            return false;
        }

        RenderingServer.GlobalShaderParameterSet(BetaRayleighGlobal, new Vector3(betaRayleigh[0], betaRayleigh[1], betaRayleigh[2]));
        RenderingServer.GlobalShaderParameterSet(BetaMieGlobal, new Vector3(betaMie[0], betaMie[1], betaMie[2]));
        float horizon = Refii.Level.Engine_SkyAtmosphere.HorizonDistance;
        if (KeepsRefreshing(Refii.Level.Engine_SkyCosts.RadianceKnownUnread) || material.GetShaderParameter(HorizonDistanceName).AsSingle() != horizon)
        {
            material.SetShaderParameter(HorizonDistanceName, horizon);
        }

        return true;
    }
}
