using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Godot;
using Refii.Core;

namespace Refii.AndroidRuntime;

/// <summary>
/// `android_character_cull_back`: a CHARACTER's two-sided surfaces — its alpha-tested cutouts and
/// both passes of its translucent ones (hair cards, lace, fringes) — drawn with back faces culled.
/// </summary>
/// <remarks>
/// <para>WHY. The remake draws every cutout and translucent surface two-sided (standard_material,
/// standard_material_translucent: `cull_disabled`), where the title culls every standard draw CCW
/// but a tree's (`sub_8221BD40`; standard_material_culled.gdshader). Two-sided, each of those
/// triangles is binned and rasterised twice, and a back face drawn before the front face over it is
/// shaded in full: the surfaces discard, so the depth they would write is not known before their
/// shader runs.</para>
/// <para>HOW. A lite specialisation (Engine_ShaderSpecialisation): the pass's build with
/// <see cref="Define"/>, whose composition turns the render mode's `cull_disabled` into `cull_back`.
/// Only for models with skinned parts (MdlInfo.SkinnedCount), keyed apart in the shared material
/// cache as `android_translucent_single_pass` is (<see cref="AndroidTranslucentPass.KeyPart(Refii.Models.MdlInfo)"/>),
/// so the world's cards, glass and vegetation keep both sides.</para>
/// <para>WHAT CHANGES. A surface seen from behind is not drawn: the inside of a hood or a coat's
/// hem, a hair card's back. The title draws them that way. Read once at the first material build;
/// a relaunch changes it. Nothing on the desktop.</para>
/// </remarks>
public static class AndroidCharacterCull
{
    /// <summary>The refii.toml key; off while it is measured.</summary>
    public const string Key = "android_character_cull_back";

    /// <summary>The specialisation's marker: a build carrying it is composed with `cull_back`.</summary>
    public const string Define = "FABLE_SPEC_CULL_BACK";

    private static readonly Regex RenderMode = new(@"(?m)^(\s*render_mode\s+[^;]*);", RegexOptions.CultureInvariant);

    private static readonly Regex TwoSided = new(@"\bcull_disabled\b", RegexOptions.CultureInvariant);

    /// <summary>Whether the configuration turns it on.</summary>
    public static bool Wanted(IReadOnlyDictionary<string, string> values) => Engine_RefiiConfig.Bool(values, Key, false);

    /// <summary>Whether a model's two-sided surfaces are culled: the key on and a model with skinned parts. Pure.</summary>
    public static bool Decide(bool on, uint skinnedParts) => on && skinnedParts != 0;

    /// <summary>
    /// <paramref name="code"/> with `cull_disabled` in its render mode replaced by `cull_back`;
    /// unchanged when the render mode does not name it. Pure.
    /// </summary>
    public static string CullBack(string code)
    {
        Match mode = RenderMode.Match(code);
        if (!mode.Success || !TwoSided.IsMatch(mode.Groups[1].Value))
        {
            return code;
        }

        string culled = TwoSided.Replace(mode.Groups[1].Value, "cull_back", 1);
        return code.Remove(mode.Index, mode.Length).Insert(mode.Index, culled + ";");
    }

    /// <summary>Whether a shader's text draws both sides.</summary>
    public static bool IsTwoSided(string code)
    {
        Match mode = RenderMode.Match(code);
        return mode.Success && TwoSided.IsMatch(mode.Groups[1].Value);
    }

    private static readonly Lazy<bool> Chosen = new(Read, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Whether this process culls its characters' two-sided surfaces.</summary>
    public static bool On => Chosen.Value;

    /// <summary>Whether this model's two-sided surfaces are culled (Engine_MdlMaterial.ApplyShaderVariant).</summary>
    public static bool For(Refii.Models.MdlInfo? info) => Decide(On, info?.SkinnedCount ?? 0);

    private static bool Read()
    {
        if (!Engine_Platform.IsAndroid || !Refii.Models.Engine_ShaderVariant.Lite)
        {
            return false;
        }

        bool on;
        try
        {
            on = Wanted(Engine_RefiiConfig.Read(out _));
        }
        catch (Exception)
        {
            on = false;
        }

        GD.Print($"[material] {Key}={on}: characters' cutout and translucent surfaces draw " +
            (on ? "front faces only (relaunch to change)" : "both sides"));
        return on;
    }
}
