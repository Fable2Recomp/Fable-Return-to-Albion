using System;
using System.Collections.Generic;
using Godot;
using Refii.Core;

namespace Refii.AndroidRuntime;

/// <summary>
/// `lite_fog_trims`: three exact trims of the lite surfaces' lean fog (fable_lite_lean.gdshaderinc),
/// composed into every lite build (Engine_ShaderVariant) and read once per process. Off by default.
/// </summary>
/// <remarks>
/// <para><see cref="SunViewDefine"/>: the sun dot taken as dot(sun R, v) with the sun turned by the view
/// matrix (uniform-only) instead of dot(sun, R v) with every pixel's ray turned into world space.
/// The same value to float rounding; about 9 ALU a pixel if the compiler hoists the uniform part.</para>
/// <para><see cref="NoVolumetricCancelDefine"/>: the volumetric cancel compiled out where
/// fable_volumetric_fog_end is always 0: under the fable model Engine_LevelLighting never sets a
/// density, and under the modern model AndroidNearFog publishes 0 while near fog is kept on a
/// renderer without volumetric fog (Mobile). Exact.</para>
/// <para><see cref="UnitScaleDefine"/>: under the fable model fable_interior_fog is always
/// outdoor_fog_scale (no interior path), so when that is 1 at launch the near branch's pow and the far
/// branch's multiply fold. Exact; a later change of outdoor_fog_scale applies at the next start.</para>
/// </remarks>
public static class AndroidFogTrims
{
    /// <summary>The refii.toml setting.</summary>
    public const string Key = "lite_fog_trims";

    /// <summary>The sun dot through the view matrix.</summary>
    public const string SunViewDefine = "FABLE_LITE_FOG_SUN_VIEW";

    /// <summary>The volumetric cancel compiled out.</summary>
    public const string NoVolumetricCancelDefine = "FABLE_LITE_FOG_NO_VOLUMETRIC_CANCEL";

    /// <summary>The fog scale as the constant 1.</summary>
    public const string UnitScaleDefine = "FABLE_LITE_FOG_UNIT_SCALE";

    /// <summary>Whether the values ask for the trims; unset is off. Pure.</summary>
    public static bool Wanted(IReadOnlyDictionary<string, string> values) => Engine_RefiiConfig.Bool(values, Key, false);

    /// <summary>
    /// Whether fable_volumetric_fog_end is 0 for the life of the process: the fable model, or the
    /// modern model with near fog kept on a renderer that draws no volumetric fog (AndroidNearFog). Pure.
    /// </summary>
    public static bool CancelAlwaysZero(bool modernModel, bool nearFogKept, bool rendererDrawsVolumetricFog) =>
        !modernModel || AndroidNearFog.Published(1f, nearFogKept, rendererDrawsVolumetricFog) == 0f;

    /// <summary>The defines for the state given, in order. Pure.</summary>
    public static string[] DefinesFor(bool on, bool modernModel, bool nearFogKept, bool rendererDrawsVolumetricFog, float outdoorFogScale)
    {
        if (!on)
        {
            return Array.Empty<string>();
        }

        var defines = new List<string>(3) { SunViewDefine };
        if (CancelAlwaysZero(modernModel, nearFogKept, rendererDrawsVolumetricFog))
        {
            defines.Add(NoVolumetricCancelDefine);
        }

        if (!modernModel && outdoorFogScale == 1f)
        {
            defines.Add(UnitScaleDefine);
        }

        return defines.ToArray();
    }

    private static readonly Lazy<string[]> Chosen = new(() =>
    {
        if (!Engine_Platform.IsAndroid)
        {
            return Array.Empty<string>();
        }

        try
        {
            IReadOnlyDictionary<string, string> values = Engine_RefiiConfig.Read(out _);
            string[] defines = DefinesFor(
                Wanted(values),
                Refii.Lighting.Engine_LightingFrame.ModernModel,
                Engine_RefiiConfig.Bool(values, AndroidNearFog.Key, true),
                AndroidNearFog.DrawsVolumetricFog(RenderingServer.GetCurrentRenderingMethod()),
                Refii.Lighting.Engine_LightingFrame.OutdoorFogScale);
            if (defines.Length != 0)
            {
                GD.Print($"[shader] {Key}: {string.Join(", ", defines)} (relaunch to change)");
            }

            return defines;
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>This process's defines, for Engine_ShaderVariant's process-wide list.</summary>
    public static string[] Defines() => Chosen.Value;
}
