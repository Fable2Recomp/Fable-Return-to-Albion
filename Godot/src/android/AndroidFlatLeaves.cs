using System;
using System.Text.RegularExpressions;
using Refii.Core;

namespace Refii.AndroidRuntime;

/// <summary>
/// `lite_flat_leaves`: the lite tree shaders drawn `unshaded`, their leaves lit at one level from the
/// sun instead of their own facing (FABLE_LITE_FLAT_LEAVES, standard_lite_fragment_end.gdshaderinc).
/// Android only; read once, as the variant is, so a change applies at the next start. On in Low.
/// </summary>
/// <remarks>
/// WHAT IT SAVES. In Oakfield trees are about 40-45% of a phone's GPU time and their meshes are dense
/// (most of 2.4 million triangles in view). Unshaded, a tree draws no Godot lighting stage and needs
/// no normal or tangent, so its vertices carry less and its leaves skip the light loop: about 4% of
/// a Galaxy S10e's frame at Low there (2026-11-14, six probe runs, day and night). Cheaper lighting
/// that kept each leaf's normal saved nothing measurable, and moving work to the vertices cost more.
/// <para>WHAT CHANGES. A leaf takes half the sun's full Lambert, times the direct AO, plus its bake
/// and fog as before: crowns keep their brightness by day and darken at night, but lose the
/// difference between their sunlit and shaded sides, and lamps no longer light leaves.</para>
/// </remarks>
public static class AndroidFlatLeaves
{
    /// <summary>The refii.toml key.</summary>
    public const string Key = "lite_flat_leaves";

    /// <summary>The define the tree builds take (standard_lite_fragment_end.gdshaderinc).</summary>
    public const string Define = "FABLE_LITE_FLAT_LEAVES";

    private static readonly Regex RenderMode = new(@"(?m)^(\s*render_mode\s+)([^;]*);", RegexOptions.CultureInvariant);

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

    /// <summary>Whether <paramref name="path"/> names one of the lite tree builds.</summary>
    /// <param name="path">The lite build's resource path.</param>
    public static bool IsTreeBuild(string path) =>
        path.Contains("/standard_material_tree_lite", StringComparison.Ordinal);

    /// <summary>The lite build's code with this process's choice applied.</summary>
    /// <param name="code">The composed lite code.</param>
    /// <param name="path">The lite build's resource path.</param>
    public static string Compose(string code, string path) => Compose(code, path, On);

    /// <summary>
    /// <paramref name="code"/> with <see cref="Define"/> ahead of its body and `unshaded` added to its
    /// render mode when <paramref name="on"/> and <paramref name="path"/> is a tree build; otherwise
    /// unchanged. Pure.
    /// </summary>
    /// <param name="code">The composed lite code.</param>
    /// <param name="path">The lite build's resource path.</param>
    /// <param name="on">Whether flat leaves are chosen.</param>
    public static string Compose(string code, string path, bool on)
    {
        if (!on || !IsTreeBuild(path) || code.Contains("#define " + Define + "\n", StringComparison.Ordinal))
        {
            return code;
        }

        string text = Refii.Fx.Engine_FxDepthReads.Compose(code, Define);
        Match mode = RenderMode.Match(text);
        if (!mode.Success)
        {
            return text;
        }

        string words = mode.Groups[2].Value.TrimEnd();
        return text.Remove(mode.Index, mode.Length).Insert(mode.Index, mode.Groups[1].Value + words + ", unshaded;");
    }
}
