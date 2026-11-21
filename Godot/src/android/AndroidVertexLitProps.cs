using System;
using Refii.Core;

namespace Refii.AndroidRuntime;

/// <summary>
/// `lite_vertex_lit_props`: the lite opaque surface builds (props, buildings and entities on
/// standard_material and standard_material_culled) compiled with Godot's `vertex_lighting` for every material
/// that is not foliage, cut out (alpha scissor), grass or a character: such a material's specialisation carries <see cref="Define"/>
/// (Engine_MdlMaterial.Lite), and the build that carries it takes the word (Engine_ShaderSpecialisation.Compose).
/// Android only; read once, as the variant is, so a change applies at the next start. On in Low.
/// </summary>
/// <remarks>
/// WHAT IT SAVES. The props' direct light is worked out at their corners and interpolated, so a pixel
/// runs no light loop. At Low in Oakfield at 13:00 (2026-11-14, three shuffled launches each, foliage and
/// cut-outs per pixel): Galaxy S10e 23.8 -> 22.6 Mcycles a frame (-5%), OnePlus 7 Pro 43.7 -> 40.8 GPU ms
/// (-7%), Galaxy S25+ -5%.
/// <para>WHAT CHANGES. Under `vertex_lighting` the Mobile renderer runs its own per-vertex light
/// functions (scene_forward_mobile.glsl, light_process_*_vertex) and never calls the material's
/// light(): the sun keeps its Lambert and its per-pixel shadow, but lamps fall off as Godot's lights
/// do rather than by the game's curve, without the direct AO, and a lamp pool on a large flat wall
/// follows the wall's few vertices. The ambient, bake and fog in fragment() are untouched. Trees,
/// grass, translucent surfaces and characters keep per-pixel light, and so do foliage and cut-out props: a
/// leaf card is drawn from both sides and the game's light() lights its back, which Godot's vertex light
/// cannot, so the dead-leaf bushes (cut-out props, not flagged foliage) went black (S25+, 2026-11-14).</para>
/// </remarks>
public static class AndroidVertexLitProps
{
    /// <summary>The refii.toml key.</summary>
    public const string Key = "lite_vertex_lit_props";

    /// <summary>The render-mode word the builds take.</summary>
    public const string RenderModeWord = "vertex_lighting";

    /// <summary>The marker a vertex-lit material's specialisation carries.</summary>
    public const string Define = "FABLE_LITE_VERTEX_LIT_PROP";

    private static readonly Lazy<bool> Chosen = new(() =>
    {
        try
        {
            return Engine_Platform.IsAndroid && Engine_RefiiConfig.Bool(Engine_RefiiConfig.Read(out _), Key, false);
        }
        catch (Exception)
        {
            return false;
        }
    });

    /// <summary>This process's choice: false off Android.</summary>
    public static bool On => Chosen.Value;

    /// <summary>`lite_vertex_lit_no_specular`: a vertex-lit build of a material that is not authored PBR also takes
    /// `specular_disabled`. Such a material sets SPECULAR and METALLIC to 0, so Godot's per-vertex specular is
    /// multiplied by an f0 of 0: the same picture without a normalize, dot and pow per light and per vertex.</summary>
    public const string NoSpecularKey = "lite_vertex_lit_no_specular";

    /// <summary>The marker a vertex-lit, non-PBR material's specialisation carries.</summary>
    public const string NoSpecularDefine = "FABLE_LITE_VERTEX_LIT_NO_SPEC";

    private static readonly Lazy<bool> NoSpecularChosen = new(() =>
    {
        try
        {
            return Engine_Platform.IsAndroid && Engine_RefiiConfig.Bool(Engine_RefiiConfig.Read(out _), NoSpecularKey, false);
        }
        catch (Exception)
        {
            return false;
        }
    });

    /// <summary>This process's choice of <see cref="NoSpecularKey"/>: false off Android.</summary>
    public static bool NoSpecularOn => NoSpecularChosen.Value;

    /// <summary>Whether <paramref name="path"/> names a lite standard or culled surface build (fable or modern).</summary>
    /// <param name="path">The lite build's resource path.</param>
    public static bool IsPropBuild(string path)
    {
        int slash = path.LastIndexOf('/');
        string name = slash < 0 ? path : path[(slash + 1)..];
        return path.Contains("/material/lite/", StringComparison.Ordinal)
            && (name.StartsWith("standard_material_lite", StringComparison.Ordinal)
                || name.StartsWith("standard_material_culled_lite", StringComparison.Ordinal));
    }

    /// <summary>Whether a material on <paramref name="path"/> with these facts takes <see cref="Define"/> in this process.</summary>
    /// <param name="path">The material's lite build.</param>
    /// <param name="foliage">Whether it is foliage or cut out (alpha scissor).</param>
    /// <param name="grass">Whether it is grass.</param>
    /// <param name="character">Whether it is a character's.</param>
    public static bool Marks(string path, bool foliage, bool grass, bool character) => Marks(path, foliage, grass, character, On);

    /// <summary>Whether a material on <paramref name="path"/> with these facts takes <see cref="Define"/>. Pure.</summary>
    /// <param name="path">The material's lite build.</param>
    /// <param name="foliage">Whether it is foliage or cut out (alpha scissor).</param>
    /// <param name="grass">Whether it is grass.</param>
    /// <param name="character">Whether it is a character's.</param>
    /// <param name="on">Whether vertex-lit props are chosen.</param>
    public static bool Marks(string path, bool foliage, bool grass, bool character, bool on) =>
        on && !foliage && !grass && !character && IsPropBuild(path);

    /// <summary>
    /// <paramref name="code"/> with `vertex_lighting` in its render mode when <paramref name="defines"/> carry
    /// <see cref="Define"/>; otherwise unchanged. Pure.
    /// </summary>
    /// <param name="code">A specialised build's code.</param>
    /// <param name="defines">The specialisation's defines.</param>
    public static string Compose(string code, System.Collections.Generic.IReadOnlyList<string> defines)
    {
        bool lit = false, noSpecular = false;
        foreach (string define in defines)
        {
            lit |= define == Define;
            noSpecular |= define == NoSpecularDefine;
        }

        if (lit)
        {
            code = Refii.Models.Engine_ShaderSpecialisation.WithRenderMode(code, RenderModeWord);
            if (noSpecular)
            {
                code = Refii.Models.Engine_ShaderSpecialisation.WithRenderMode(code, "specular_disabled");
            }
        }

        return code;
    }
}
