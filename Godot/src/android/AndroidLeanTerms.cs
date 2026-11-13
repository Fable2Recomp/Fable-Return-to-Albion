using System;
using System.Collections.Generic;
using Refii.Core;

namespace Refii.AndroidRuntime;

/// <summary>
/// `lite_lean_light_ao` and `lite_lean_frame_sign`: two of the modern lighting's per-pixel terms in
/// the phone's lite surfaces with less work. Composed into every lite build (Engine_ShaderVariant's
/// process-wide defines); read once per process, as the variant is.
/// </summary>
/// <remarks>
/// <para>THE LIGHT AO (`lite_lean_light_ao`, on by default): upstream's fable_direct_ao_for_light and
/// fable_specular_ao, which light() runs for every light that reaches a pixel, from
/// fable_lite_lean_light.gdshaderinc beside this file instead: without the clamps of the combined AO
/// to [0, 1] and the specular occlusion's floor at 0, which the AO's range already settles (every lite
/// body writes it from fable_apply_ao_strength, within [1e-4, 1]). The same picture bit for bit;
/// measured on the Mobile renderer's SPIR-V of the S10e's Low lean-modern builds, a culled prop's sun
/// 4 ALU fewer of 102 and each lamp 3 of 250, the ground's 1 and 2.</para>
/// <para>THE FRAME SIGN (`lite_lean_frame_sign`, off by default): with `lite_normal_maps = "fast"`,
/// the tangent frame's handedness test N . (dp1 x dp2) taken as dp2 . (N x dp1), N x dp1 being a
/// cross product the frame already has: one cross product (six instructions) fewer per normal-mapped
/// pixel. Exact in real arithmetic; the float roundings can disagree only where the product is within
/// rounding of zero, a triangle seen edge-on. Pixel-identical (8-bit) over a lit, normal- and
/// specular-mapped test scene; nearly the same picture by construction, so off until measured.</para>
/// </remarks>
public static class AndroidLeanTerms
{
    /// <summary>The light AO's refii.toml setting.</summary>
    public const string LightAoKey = "lite_lean_light_ao";

    /// <summary>The frame sign's refii.toml setting.</summary>
    public const string FrameSignKey = "lite_lean_frame_sign";

    /// <summary>The define of `lite_lean_light_ao`.</summary>
    public const string LightAoDefine = "FABLE_LITE_LEAN_LIGHT_AO";

    /// <summary>The define of `lite_lean_frame_sign`.</summary>
    public const string FrameSignDefine = "FABLE_LITE_LEAN_FRAME_SIGN";

    /// <summary>Whether the values ask for the lean light AO; unset is on (the same picture). Pure.</summary>
    public static bool LightAoWanted(IReadOnlyDictionary<string, string> values) =>
        Engine_RefiiConfig.Bool(values, LightAoKey, true);

    /// <summary>Whether the values ask for the lean frame sign; unset is off. Pure.</summary>
    public static bool FrameSignWanted(IReadOnlyDictionary<string, string> values) =>
        Engine_RefiiConfig.Bool(values, FrameSignKey, false);

    /// <summary>The defines a lite build takes for the two settings, in order. Pure.</summary>
    public static string[] DefinesFor(bool lightAo, bool frameSign)
    {
        var defines = new List<string>(2);
        if (lightAo)
        {
            defines.Add(LightAoDefine);
        }

        if (frameSign)
        {
            defines.Add(FrameSignDefine);
        }

        return defines.ToArray();
    }

    private static readonly Lazy<(bool LightAo, bool FrameSign)> Chosen = new(() =>
    {
        if (!Engine_Platform.IsAndroid)
        {
            return (false, false);
        }

        try
        {
            IReadOnlyDictionary<string, string> values = Engine_RefiiConfig.Read(out _);
            return (LightAoWanted(values), FrameSignWanted(values));
        }
        catch (Exception)
        {
            return (false, false);
        }
    }, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Whether this process's lite builds take the lean light AO (read once).</summary>
    public static bool LightAoOn => Chosen.Value.LightAo;

    /// <summary>Whether this process's lite builds take the lean frame sign (read once).</summary>
    public static bool FrameSignOn => Chosen.Value.FrameSign;

    /// <summary>This process's defines, for Engine_ShaderVariant's process-wide list.</summary>
    public static string[] Defines() => DefinesFor(LightAoOn, FrameSignOn);
}
