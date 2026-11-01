using System;
using System.Globalization;
using System.Linq;

namespace Refii.AndroidRuntime;

/// <summary>How much a graphics setting moves the frame rate across its steps, as the panel shows it.</summary>
public enum AndroidGraphicsImpact { Low, Medium, High }

/// <summary>
/// One graphics setting the Options panel steps with its - and + buttons: its <c>refii.toml</c> key, the
/// name it shows, how much it costs, the values the buttons step through (worst picture first) and how a
/// value reads.
/// </summary>
/// <param name="Key">The setting's key in refii.toml.</param>
/// <param name="Name">The row's name.</param>
/// <param name="Impact">How much the setting moves the frame rate across its steps.</param>
/// <param name="About">What the setting does, for the row's ? button.</param>
/// <param name="Steps">The values in order, as written to the file.</param>
/// <param name="Show">The text for a value; one off the steps reads as written.</param>
public sealed record AndroidGraphicsOption(
    string Key, string Name, AndroidGraphicsImpact Impact, string About, string[] Steps, Func<double, string> Show);

/// <summary>
/// The graphics settings the in-game Options panel offers besides the preset: those that change the
/// picture and that the running game picks up at once. The host rereads the render scale and mesh LOD
/// (AndroidGameHost.RefreshRenderScale); the rest reach their readers through
/// <see cref="Refii.Core.Engine_RefiiConfig.TrySetGodotCvar"/> (placed lights, the sun shadow's depth and
/// reach, the props' draw ranges, the bloom). Settings read only at launch (the characters' draw distance,
/// the trees' shadow reach and thinned crowns) are left to the launcher.
/// </summary>
public static class AndroidGraphicsOptions
{
    /// <summary>The rows, top to bottom.</summary>
    /// <remarks>The impacts are from the phone measurements in GraphicsPresets: the render scale is
    /// most of the GPU's work (on the S25+, 60% saved about 6% against 67%, and 30% is the largest part
    /// of Performance's 28% off Balanced); the sun shadow off is about 12% alone; eight lit lamps to four
    /// took 8% on an Adreno 640; every lamp that casts renders its own shadow map. Model detail and prop
    /// distance are most of the rest of Quality's +25-30% over Balanced. The shadow reach, lamp range and
    /// bloom moved nothing measurable.</remarks>
    public static readonly AndroidGraphicsOption[] All =
    {
        new("render_scale_percent", "Render scale", AndroidGraphicsImpact.High,
            "How many pixels the world is drawn with, as a share of the screen's; menus and text stay sharp. The biggest single cost: lower is softer and faster.",
            new[] { "30", "40", "50", "60", "67", "75", "85", "100" },
            v => Number(v) + "%"),
        // Larger is coarser: a model's coarser levels from further in.
        new("mesh_lod_pixels", "Model detail", AndroidGraphicsImpact.Medium,
            "How soon models switch to their simpler versions as they get further away. Low switches early (plainer distant models, faster); Ultra keeps full detail furthest.",
            new[] { "4", "2", "1", "0.5" },
            v => v switch { 4 => "Low", 2 => "Medium", 1 => "High", 0.5 => "Ultra", _ => Number(v) }),
        // 0 lifts the size culling altogether.
        new("prop_cull_distance_scale", "Prop distance", AndroidGraphicsImpact.Medium,
            "How far small props (crates, produce, furniture, fences) are drawn, as a share of the desktop's distances. All draws every prop at any distance.",
            new[] { "0.4", "0.6", "0.8", "1", "1.5", "2", "0" },
            v => v == 0 ? "All" : Number(v * 100) + "%"),
        new("sun_shadow_distance", "Sun shadows", AndroidGraphicsImpact.High,
            "How far from you the sun and moon cast moving shadows, in metres; night uses half. Off removes them, a large saving.",
            new[] { "0", "50", "80", "100", "120", "160", "200" },
            v => v == 0 ? "Off" : Number(v) + " m"),
        new("sun_shadow_reach_scale", "Shadow reach", AndroidGraphicsImpact.Low,
            "How far small and medium props cast the sun's shadow, as a share of their full reach. Large landscape shadows stay either way.",
            new[] { "0.5", "0.75", "1" },
            v => Number(v * 100) + "%"),
        // 0 lights every one within the range.
        new("placed_light_limit", "Lamps lit", AndroidGraphicsImpact.Medium,
            "How many of the nearest lamps, torches and fires are lit at once; the rest fade out until you come near. All lights every one in range.",
            new[] { "4", "8", "16", "24", "32", "48", "0" },
            v => v == 0 ? "All" : Number(v)),
        new("placed_light_range", "Lamp range", AndroidGraphicsImpact.Low,
            "How far away a lamp or torch still lights its surroundings, in metres.",
            new[] { "30", "45", "65", "85" },
            v => Number(v) + " m"),
        new("placed_light_shadows", "Lamp shadows", AndroidGraphicsImpact.High,
            "Lets lamps and torches near you cast moving shadows. Each one draws its own shadow map, so it costs a lot.",
            new[] { "false", "true" },
            v => v != 0 ? "On" : "Off"),
        new("bloom_scale", "Bloom", AndroidGraphicsImpact.Low,
            "The soft glow around bright light. The sun, the moon and lamp halos glow either way.",
            new[] { "0", "0.25", "0.5", "1" },
            v => v switch { 0 => "Off", 0.25 => "Low", 0.5 => "Medium", 1 => "High", _ => Number(v) }),
    };

    /// <summary>
    /// The value one step from <paramref name="current"/> (<paramref name="direction"/> -1 or +1), stopping at
    /// either end; one off the steps moves from the step nearest it.
    /// </summary>
    public static string Step(AndroidGraphicsOption option, string current, int direction)
    {
        int at = Index(option, current);
        return option.Steps[Math.Clamp(at + Math.Sign(direction), 0, option.Steps.Length - 1)];
    }

    /// <summary>Whether a step in <paramref name="direction"/> would change <paramref name="current"/>.</summary>
    public static bool CanStep(AndroidGraphicsOption option, string current, int direction)
    {
        int at = Index(option, current);
        bool exact = Parse(option.Steps[at]) == Parse(current);
        int to = at + Math.Sign(direction);
        return to >= 0 && to < option.Steps.Length || !exact;
    }

    /// <summary>The impact line under a row's name.</summary>
    public static string ImpactLabel(AndroidGraphicsImpact impact) => "Perf impact: " + impact;

    /// <summary>The value's text: the row's own wording for a step, the number for anything else.</summary>
    public static string Label(AndroidGraphicsOption option, string value) => option.Show(Parse(value));

    /// <summary>The step equal to <paramref name="value"/>, or the one nearest it by number.</summary>
    private static int Index(AndroidGraphicsOption option, string value)
    {
        double wanted = Parse(value);
        return Enumerable.Range(0, option.Steps.Length).MinBy(i => Math.Abs(Parse(option.Steps[i]) - wanted));
    }

    /// <summary>A value as a number: true is 1, false 0, anything unreadable 0.</summary>
    private static double Parse(string value)
    {
        value = value.Trim().Trim('"');
        if (bool.TryParse(value, out bool on)) return on ? 1 : 0;
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) ? number : 0;
    }

    private static string Number(double value) => Math.Round(value, 2).ToString("0.##", CultureInfo.InvariantCulture);
}
