using System;
using Refii.Core;

namespace Refii.AndroidRuntime;

/// <summary>
/// `lite_flat_cutouts`: the props' foliage and cut-out materials (hedgerows, brambles, bushes, railings) drawn as
/// `lite_flat_leaves` draws the trees' leaves: `unshaded`, lit at one level from the sun
/// (FABLE_LITE_FLAT_LEAVES, standard_lite_fragment_end.gdshaderinc). Android only; read once, so a change
/// applies at the next start. On in Low.
/// </summary>
/// <remarks>
/// WHY. `lite_vertex_lit_props` leaves these per pixel (a card drawn from both sides goes black under Godot's
/// vertex light), and they are stacked cards up close: the hedges along Oakfield's roads fill a quarter of
/// the screen several layers deep.
/// <para>WHAT CHANGES. As flat leaves: half the sun's Lambert times the direct AO, plus the bake and fog;
/// no difference between a bush's lit and shaded sides, and lamps no longer light it. Blended (translucent)
/// passes, grass and characters keep their own light.</para>
/// </remarks>
public static class AndroidFlatCutouts
{
    /// <summary>The refii.toml key.</summary>
    public const string Key = "lite_flat_cutouts";

    /// <summary>The marker a flat cut-out's specialisation carries; the build takes flat leaves' define and `unshaded`.</summary>
    public const string Define = "FABLE_LITE_FLAT_CUTOUT";

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

    /// <summary>Whether a material on <paramref name="path"/> with these facts takes <see cref="Define"/> in this process.</summary>
    public static bool Marks(string path, bool cutout, bool blended, bool grass, bool character) =>
        Marks(path, cutout, blended, grass, character, On);

    /// <summary>Whether a material on <paramref name="path"/> with these facts takes <see cref="Define"/>. Pure.</summary>
    /// <param name="path">The material's lite build.</param>
    /// <param name="cutout">Whether it is foliage or cut out (alpha scissor).</param>
    /// <param name="blended">Whether it is translucent.</param>
    /// <param name="grass">Whether it is grass.</param>
    /// <param name="character">Whether it is a character's.</param>
    /// <param name="on">Whether flat cut-outs are chosen.</param>
    public static bool Marks(string path, bool cutout, bool blended, bool grass, bool character, bool on) =>
        on && cutout && !blended && !grass && !character && AndroidVertexLitProps.IsPropBuild(path);

    /// <summary>`lite_flat_swaying_plants`: the same flat lighting for swaying plants (Low only; changes the picture).</summary>
    public const string SwayingKey = "lite_flat_swaying_plants";

    private static readonly Lazy<bool> SwayingChosen = new(() =>
    {
        try
        {
            return Engine_Platform.IsAndroid && Engine_RefiiConfig.Bool(Engine_RefiiConfig.Read(out _), SwayingKey, false);
        }
        catch (Exception)
        {
            return false;
        }
    });

    /// <summary>This process's <see cref="SwayingKey"/>: false off Android.</summary>
    public static bool SwayingOn => SwayingChosen.Value;

    /// <summary>
    /// Whether a material on <paramref name="path"/> takes <see cref="Define"/> under <see cref="SwayingKey"/>: a
    /// swaying plant's (wind lite build) foliage, cut-out or grass pass that is not blended or a character's. These
    /// stay per pixel under `lite_flat_cutouts`; they are low cards stacked several deep (water-and-undergrowth UP1).
    /// Flat, they lose their lit and shaded sides and lamp light, as flat cut-outs do. Pure.
    /// </summary>
    public static bool MarksSwaying(string path, bool cutout, bool blended, bool grass, bool character, bool on) =>
        on && (cutout || grass) && !blended && !character && IsWindBuild(path);

    /// <summary>Whether <paramref name="path"/> is a lite build of the wind (swaying) shader.</summary>
    public static bool IsWindBuild(string path) =>
        Refii.Models.Engine_ShaderVariant.IsBuild(path) &&
        Refii.Models.Engine_ShaderVariant.FullPathOf(path) == Refii.Models.Engine_ShaderFamily.StandardWindPath;

    /// <summary>
    /// `debug_cutouts` (testing, off, in no preset): prices a depth pre-pass for the cut-outs with launch A/Bs.
    /// The builds that can discard (two-sided, tree and wind: AndroidOpaqueOrder.Discards) are changed in every
    /// lite build; solid and blended builds are not. "opaque": the prop fade dither and the alpha scissor compiled
    /// out, so they draw as solid cards with early depth working (a wrong picture; the floor a pre-pass could
    /// approach). "depth_only": the discard kept, the build `unshaded` and black (what a depth pre-pass of the same
    /// geometry costs). "hidden": their triangles placed outside the view (the vertex work alone). Read once.
    /// </summary>
    public const string DebugKey = "debug_cutouts";

    /// <summary>The values of <see cref="DebugKey"/>.</summary>
    public enum DebugMode
    {
        Off,
        Opaque,
        DepthOnly,
        Hidden,
    }

    /// <summary>The define each <see cref="DebugMode"/> composes into the lite builds (standard_material_body, standard_lite_*).</summary>
    public static string[] DebugDefines(DebugMode mode) => mode switch
    {
        DebugMode.Opaque => new[] { "FABLE_DEBUG_CUTOUTS_OPAQUE" },
        DebugMode.DepthOnly => new[] { "FABLE_DEBUG_CUTOUTS_DEPTH_ONLY" },
        DebugMode.Hidden => new[] { "FABLE_DEBUG_CUTOUTS_HIDDEN" },
        _ => Array.Empty<string>(),
    };

    /// <summary><see cref="DebugKey"/> as <paramref name="values"/> give it; unset or unknown is off. Pure.</summary>
    public static DebugMode DebugWanted(System.Collections.Generic.IReadOnlyDictionary<string, string> values) =>
        Engine_RefiiConfig.String(values, DebugKey, "off").Trim().Trim('"').ToLowerInvariant() switch
        {
            "opaque" => DebugMode.Opaque,
            "depth_only" => DebugMode.DepthOnly,
            "hidden" => DebugMode.Hidden,
            _ => DebugMode.Off,
        };

    private static readonly Lazy<DebugMode> DebugChosen = new(() =>
    {
        try
        {
            DebugMode mode = Engine_Platform.IsAndroid ? DebugWanted(Engine_RefiiConfig.Read(out _)) : DebugMode.Off;
            if (mode != DebugMode.Off)
            {
                Godot.GD.Print($"[material] {DebugKey}={mode}: cut-out builds changed for testing (relaunch to change)");
            }

            return mode;
        }
        catch (Exception)
        {
            return DebugMode.Off;
        }
    }, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>This process's <see cref="DebugKey"/>: off off Android.</summary>
    public static DebugMode DebugOn => DebugChosen.Value;

    /// <summary>
    /// The lite build <paramref name="path"/>'s code under <paramref name="mode"/>: `unshaded` added to a build that
    /// can discard under "depth_only"; otherwise unchanged. Pure.
    /// </summary>
    public static string ComposeDebug(string code, string path, DebugMode mode) =>
        mode == DebugMode.DepthOnly && AndroidOpaqueOrder.Discards(Refii.Models.Engine_ShaderVariant.FullPathOf(path))
            ? Refii.Models.Engine_ShaderSpecialisation.WithRenderMode(code, "unshaded")
            : code;

    /// <summary>
    /// <paramref name="code"/> with flat leaves' define ahead of its body and `unshaded` in its render mode when
    /// <paramref name="defines"/> carry <see cref="Define"/>; otherwise unchanged. Pure.
    /// </summary>
    /// <param name="code">A specialised build's code.</param>
    /// <param name="defines">The specialisation's defines.</param>
    public static string Compose(string code, System.Collections.Generic.IReadOnlyList<string> defines)
    {
        foreach (string define in defines)
        {
            if (define == Define)
            {
                string text = code.Contains("#define " + AndroidFlatLeaves.Define + "\n", StringComparison.Ordinal)
                    ? code
                    : Refii.Fx.Engine_FxDepthReads.Compose(code, AndroidFlatLeaves.Define);
                return Refii.Models.Engine_ShaderSpecialisation.WithRenderMode(text, "unshaded");
            }
        }

        return code;
    }
}
