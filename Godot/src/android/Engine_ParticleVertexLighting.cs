using System;
using System.Text.RegularExpressions;
using Godot;

namespace Refii.Fx;

/// <summary>
/// `android_particles_vertex_lit`: the guest's particles and trails (fx_guest_particle,
/// fx_guest_trail) lit at their vertices instead of their pixels, as the title lights them.
/// </summary>
/// <remarks>
/// WHAT IT SAVES. A lit particle runs fx_guest_particle_pixel's `light()` on every pixel for every
/// light Godot paired with its group, and Mobile's light loop carries a fixed cost per call
/// (inputs widened to fp32, matrices rebuilt, no early exit) over smoke and fire that overlap many
/// times. `vertex_lighting` defines USE_VERTEX_LIGHTING: the omni, spot and directional loops run
/// in the vertex stage (scene_forward_vertex_lights_inc.glsl, `light_compute_vertex`), four times a
/// quad, and the pixel adds one interpolated colour. The custom `light()` is then never called.
///
/// WHAT STAYS THE SAME. Godot's Lambert there is `max(N.L, 0) x colour x energy x PI / PI` times
/// Godot's window `(1 - (d/R)^4)^2 / d` (OmniAttenuation 1.0) and the spot cone, which is what
/// `fable_direct` forms from LIGHT_COLOR / PI and ATTENUATION; the shaders are specular_disabled and
/// shadows_disabled either way, and the ambient and self-illumination are fragment() terms, unchanged.
///
/// WHAT DIFFERS (`fable_attenuation` is not run):
/// - no 1 m clamp: a vertex nearer a lamp than a metre takes Godot's `1/d` (2x at 50 cm), so
///   the corners of smoke or a trail right at a brazier read brighter;
/// - the light is sampled at the corners and interpolated, so a lamp inside a large puff gives no
///   hot spot at its middle, only the corners' level;
/// - no light-table work: a flickering torch's inward radius breathing (up to its RadiusRange,
///   ~21%) is not seen, its energy flicker still is; no baked static lamp shadow, so a lamp behind a
///   wall lights the smoke past it; no cloud shadow on the sun.
/// - the sun's NdotL is per vertex, which over a puff's normals (eye plus corner) is nearly linear.
///
/// The classic renderer's particles and the FxSystem effects (fx_particle*) are `unshaded` and are
/// left alone. Read once at the first composed particle shader; relaunch to change.
/// </remarks>
public static class Engine_ParticleVertexLighting
{
    public const string Key = "android_particles_vertex_lit";

    /// <summary>The render mode word that moves Godot's light loops to the vertex stage.</summary>
    public const string Word = "vertex_lighting";

    private static readonly Lazy<bool> Read = new(Configured, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>On a phone with the key set; never on a desktop.</summary>
    public static bool Enabled => Read.Value;

    private static bool Configured()
    {
        if (!Refii.Core.Engine_Platform.IsAndroid)
        {
            return false;
        }

        try
        {
            bool on = Refii.Core.Engine_RefiiConfig.Bool(Refii.Core.Engine_RefiiConfig.Read(out _), Key, false);
            if (on)
            {
                GD.Print($"[fx] {Key}: the guest's particles and trails lit per vertex (relaunch to change)");
            }

            return on;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>The particle shader <paramref name="code"/> as the setting asks: <see cref="Compose"/>.</summary>
    public static string Apply(string code) => Compose(code, Enabled);

    private static readonly Regex RenderMode = new(@"(?m)^(\s*render_mode\s+)([^;]*);", RegexOptions.CultureInvariant);

    private static readonly Regex ShaderType = new(@"(?m)^[ \t]*shader_type\s+spatial\s*;", RegexOptions.CultureInvariant);

    /// <summary>
    /// <paramref name="code"/> with `vertex_lighting` added to its render mode when
    /// <paramref name="on"/>; unchanged when off, when the mode is `unshaded` (no light to move) or
    /// already has the word. A spatial shader with no render mode gets one. Pure.
    /// </summary>
    public static string Compose(string code, bool on)
    {
        if (!on)
        {
            return code;
        }

        Match mode = RenderMode.Match(code);
        if (mode.Success)
        {
            string words = mode.Groups[2].Value;
            if (HasWord(words, "unshaded") || HasWord(words, Word))
            {
                return code;
            }

            return code.Remove(mode.Index, mode.Length)
                .Insert(mode.Index, mode.Groups[1].Value + words.TrimEnd() + ", " + Word + ";");
        }

        Match type = ShaderType.Match(code);
        return type.Success
            ? code.Insert(type.Index + type.Length, "\nrender_mode " + Word + ";")
            : code;
    }

    private static bool HasWord(string words, string word)
    {
        foreach (string part in words.Split(','))
        {
            if (part.Trim() == word)
            {
                return true;
            }
        }

        return false;
    }
}
