using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;
using Godot;

namespace Refii.Fx;

/// <summary>
/// `fx_no_depth_reads`: keep the scene-depth texture out of the frame where nothing reads it, so
/// the Mobile renderer can draw the opaque and transparent lists in one render pass.
/// </summary>
/// <remarks>
/// WHY THE UNIFORM, NOT THE READ. Godot marks a material as reading depth when its shader merely
/// DECLARES a `hint_depth_texture` uniform (shader_compiler.cpp), whatever a runtime uniform skips.
/// One such material in the render list ends the opaque pass on Mobile, copies depth to a texture
/// and reopens a transparent pass that loads colour and depth back (render_forward_mobile.cpp):
/// "Render Opaque" and "Render Transparent" instead of "Render Opaque + Transparent". Every
/// effect particle declared it (fx_soft), and the sun and moon glares did (celestial_overlay)
/// until their occlusion moved to the hardware depth test, so the split ran every frame.
///
/// On: a particle material whose soft distance is zero is built from its shader with FX_HARD,
/// which compiles the uniform and the fade out (the fade is exactly 1 at zero distance, so the
/// picture is the same), and a glare that cannot put a pixel on the window's view is hidden
/// (Engine_CelestialElements; behind the camera its vertex stage collapses it, off screen it has
/// no pixel), so it leaves the render list instead of drawing nothing in it. Water reads depth and
/// the screen whatever this says.
///
/// Live: the debug channel's `render` op (`fxNoDepthReads`) re-points every hard-capable effect
/// material it has seen, and the glares follow on the next frame.
/// </remarks>
public static class Engine_FxDepthReads
{
    public const string Key = "fx_no_depth_reads";

    public const string RequestKey = "fxNoDepthReads";

    /// <summary>The define fx_soft.gdshaderinc compiles its depth uniform out under.</summary>
    public const string HardDefine = "FX_HARD";

    private static bool? _enabled;

    /// <summary>Main thread. The setting at first use; the debug channel may change it.</summary>
    public static bool Enabled
    {
        get => _enabled ??= Configured();
        set
        {
            bool changed = Enabled != value;
            _enabled = value;
            if (changed)
            {
                Repoint();
            }
        }
    }

    private static bool Configured()
    {
        try
        {
            bool on = Refii.Core.Engine_RefiiConfig.Bool(Refii.Core.Engine_RefiiConfig.Read(out _), Key, false);
            if (on)
            {
                GD.Print($"[fx] {Key}: hard particles without the depth texture, glares out of view hidden");
            }

            return on;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Validates the optional lever before the debug endpoint mutates rendering state.</summary>
    /// <returns>The requested flag or null without changing process state.</returns>
    internal static bool? ReadRequest(JsonElement request)
    {
        if (!request.TryGetProperty(RequestKey, out JsonElement value)) return null;
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new ArgumentException(RequestKey + " must be a boolean.");
        return value.GetBoolean();
    }

    /// <summary>
    /// `android_fx_hard_all`: every effect particle drawn from its hard build, the ones with a soft
    /// distance too, so no effect material is left to read depth. Android only; off by default.
    /// </summary>
    /// <remarks>
    /// WHY. <see cref="Enabled"/> hardens only the materials whose fade is exactly 1 (soft distance
    /// zero); smoke, steam and dust with a soft distance still declare the depth texture, and one
    /// of them in view keeps Mobile's frame split ("Render Opaque", depth copy, "Render
    /// Transparent"). With this on, their fade is 1 too: the picture changes where such a particle
    /// meets a wall or the ground, which shows its quad's straight edge instead of fading out over
    /// the soft distance. With fx_no_depth_reads (the glares) and android_highlight_depth_test, only
    /// water and rain can still split the pass. Live: a `cvar_set` re-points every effect material
    /// the key reaches.
    /// </remarks>
    public const string HardAllKey = "android_fx_hard_all";

    private static bool? _hardAll;
    private static bool _hardAllHooked;

    /// <summary>Whether <see cref="HardAllKey"/> is on, on Android only; read once and again on each live change.</summary>
    public static bool HardAll
    {
        get
        {
            if (_hardAll is { } known) return known;
            bool on = false;
            try
            {
                on = Refii.Core.Engine_Platform.IsAndroid &&
                    Refii.Core.Engine_RefiiConfig.Bool(Refii.Core.Engine_RefiiConfig.Read(out _), HardAllKey, false);
            }
            catch (Exception)
            {
                on = false;
            }

            if (!_hardAllHooked)
            {
                _hardAllHooked = true;
                Refii.Core.Engine_RefiiConfig.OnConfigChanged += (name, _) =>
                {
                    if (!string.Equals(name, HardAllKey, StringComparison.OrdinalIgnoreCase)) return;
                    bool was = _hardAll ?? false;
                    _hardAll = null;
                    if (HardAll != was && OS.GetThreadCallerId() == OS.GetMainThreadId())
                    {
                        Repoint();
                    }
                };
            }

            _hardAll = on;
            if (on)
            {
                GD.Print($"[fx] {HardAllKey}: every effect particle hard, soft fades included");
            }

            return on;
        }
    }

    /// <summary>
    /// Whether an effect material draws from its hard build: every one under
    /// <paramref name="hardAll"/>, else one without a soft distance under
    /// <paramref name="noDepthReads"/>. Pure.
    /// </summary>
    public static bool ShouldBeHard(bool noDepthReads, bool hardAll, bool soft) => hardAll || (noDepthReads && !soft);

    /// <summary>
    /// Whether an effect material built in <paramref name="mode"/> with <paramref name="softDepth"/>
    /// is kept for a hard build: any fx_soft mode without a soft distance, and on Android one with a
    /// soft distance as well (<see cref="HardAll"/>). Pure but for the platform.
    /// </summary>
    public static bool Adoptable(float softDepth, string mode) =>
        SoftMode(mode) && (softDepth <= 0f || Refii.Core.Engine_Platform.IsAndroid);

    /// <summary>The effect modes whose shader includes fx_soft.gdshaderinc, the depth fade; the
    /// additive layer's and the displacement shaders never declare the depth texture. Pure.</summary>
    public static bool SoftMode(string mode) => mode is not ("addlayer" or "disp");

    private static readonly Regex Include = new(@"(?m)^[ \t]*#include\b", RegexOptions.CultureInvariant);

    private static readonly Regex ShaderType = new(@"(?m)^[ \t]*shader_type[^;]*;[^\n]*\n?", RegexOptions.CultureInvariant);

    /// <summary>
    /// <paramref name="code"/> with `#define <paramref name="define"/>` before its first `#include`,
    /// so every included body sees it; with no include, after its `shader_type` line. Pure.
    /// </summary>
    public static string Compose(string code, string define)
    {
        string text = code.Replace("\r\n", "\n", StringComparison.Ordinal);
        string line = "#define " + define + "\n";
        Match include = Include.Match(text);
        if (include.Success)
        {
            return text.Insert(include.Index, line);
        }

        Match type = ShaderType.Match(text);
        if (!type.Success)
        {
            return line + text;
        }

        int end = type.Index + type.Length;
        return end > 0 && text[end - 1] != '\n'
            ? text.Insert(end, "\n" + line)
            : text.Insert(end, line);
    }

    private static readonly Dictionary<string, Shader> HardShaders = new(StringComparer.Ordinal);

    /// <summary>
    /// The FX_HARD build of a particle shader file, one per file, shared. The file itself when it
    /// has no path or no code (a build can only be made from a file).
    /// </summary>
    public static Shader Hard(Shader soft)
    {
        string path = soft.ResourcePath;
        if (path.Length == 0 || soft.Code.Length == 0)
        {
            return soft;
        }

        if (HardShaders.TryGetValue(path, out Shader? hard) && GodotObject.IsInstanceValid(hard))
        {
            return hard;
        }

        hard = new Shader { Code = Compose(soft.Code, HardDefine) };
        HardShaders[path] = hard;
        return hard;
    }

    /// <summary>A material that may draw hard, the file shader it was built from, and whether it
    /// has a soft distance (hard then only under <see cref="HardAll"/>).</summary>
    private readonly record struct Capable(WeakReference<ShaderMaterial> Material, Shader Soft, bool SoftFade);

    private static readonly List<Capable> Materials = new();

    /// <summary>The list length at which <see cref="Adopt"/> next drops freed materials.</summary>
    private static int _pruneAt = 64;

    /// <summary>
    /// Main thread: <paramref name="material"/> may draw from the hard build of
    /// <paramref name="soft"/>: it fades by nothing (its soft distance is zero), or, with
    /// <paramref name="softFade"/>, only under <see cref="HardAll"/>. Points it at the build the
    /// settings ask for, and keeps it for a live change.
    /// </summary>
    public static void Adopt(ShaderMaterial material, Shader soft, bool softFade = false)
    {
        if (Materials.Count >= _pruneAt)
        {
            Prune(false);
            _pruneAt = Math.Max(64, Materials.Count * 2);
        }

        Materials.Add(new Capable(new WeakReference<ShaderMaterial>(material), soft, softFade));
        Point(material, soft, ShouldBeHard(Enabled, HardAll, softFade));
    }

    private static void Point(ShaderMaterial material, Shader soft, bool hard)
    {
        Shader want = hard ? Hard(soft) : soft;
        if (!ReferenceEquals(material.Shader, want))
        {
            // The server keeps a material's parameters across a shader change, by name; both
            // builds declare the same ones but the depth texture.
            material.Shader = want;
        }
    }

    private static void Repoint()
    {
        int kept = Prune(true);
        GD.Print($"[fx] {Key} {(Enabled ? "on" : "off")}, {HardAllKey} {(HardAll ? "on" : "off")}: {kept} effect materials re-pointed");
    }

    /// <summary>Drops freed materials, pointing the live ones at the build the settings ask for
    /// when <paramref name="repoint"/>. Returns how many are kept.</summary>
    private static int Prune(bool repoint)
    {
        bool noDepthReads = Enabled;
        bool hardAll = HardAll;
        int kept = 0;
        for (int i = 0; i < Materials.Count; i++)
        {
            Capable entry = Materials[i];
            if (!entry.Material.TryGetTarget(out ShaderMaterial? material) || !GodotObject.IsInstanceValid(material) ||
                !GodotObject.IsInstanceValid(entry.Soft))
            {
                continue;
            }

            if (repoint)
            {
                Point(material, entry.Soft, ShouldBeHard(noDepthReads, hardAll, entry.SoftFade));
            }

            Materials[kept++] = entry;
        }

        Materials.RemoveRange(kept, Materials.Count - kept);
        return kept;
    }

    /// <summary>Effect materials that may draw hard, live ones and not yet pruned.</summary>
    public static int AdoptedCount => Materials.Count;
}
