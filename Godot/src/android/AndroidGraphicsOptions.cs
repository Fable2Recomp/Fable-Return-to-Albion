using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Refii.AndroidRuntime;

/// <summary>How much a graphics setting moves the frame rate across its steps, as the panel shows it.</summary>
public enum AndroidGraphicsImpact { Low, Medium, High }

/// <summary>
/// One graphics setting the Options panel steps with its - and + buttons: its <c>refii.toml</c> key, the
/// name it shows, how much it costs, the values the buttons step through (worst picture first) and how a
/// value reads. A row may also move other settings with it (<see cref="Linked"/>), so the player sees one
/// choice where the file keeps several.
/// </summary>
/// <param name="Key">The setting's key in refii.toml: the one the row reads its step from.</param>
/// <param name="Name">The row's name.</param>
/// <param name="Impact">How much the setting moves the frame rate across its steps.</param>
/// <param name="About">What the setting does, for the row's ? button.</param>
/// <param name="Steps">The values in order, as written to the file.</param>
/// <param name="Show">The text for a value; one off the steps reads as written.</param>
public sealed record AndroidGraphicsOption(
    string Key, string Name, AndroidGraphicsImpact Impact, string About, string[] Steps, Func<double, string> Show)
{
    /// <summary>Other settings that take a value of their own at each step: key, then one value per step of <see cref="Steps"/>.</summary>
    public IReadOnlyList<(string Key, string[] Values)> Linked { get; init; } = Array.Empty<(string, string[])>();

    /// <summary>A setting this row only matters while it is on (not 0 or false), or null: the sun shadow's
    /// quality and softness while the Shadow distance is Off change nothing on screen.</summary>
    public string? Needs { get; init; }
}

/// <summary>
/// The graphics settings the in-game Options panel offers besides the preset: those that change the
/// picture and that the running game picks up at once, or at the next loading screen where it says so.
/// The host rereads the render scale and mesh LOD (AndroidGameHost.RefreshRenderScale); the rest reach
/// their readers through <see cref="Refii.Core.Engine_RefiiConfig.TrySetGodotCvar"/> (placed lights, the
/// sun shadow's depth and reach, the props' draw ranges, the trees' far reach and coarser levels, the
/// ground's coarser levels, the bloom, and the undergrowth at its next build). The host also rereads the
/// sun shadow's map size and edge filter (<see cref="AndroidSunShadows"/>). Settings read only at launch
/// (the characters' draw distance, the trees' shadow reach and thinned crowns) are left to the launcher.
/// </summary>
/// <remarks>
/// The names are a player's words, not the settings' keys: "Resolution", not render scale; "Distant
/// detail", not mesh LOD pixels. Settings the presets always move together are one row with
/// <see cref="AndroidGraphicsOption.Linked"/> steps, each step one of the presets' own combinations, so
/// stepping a row through a preset's value lands on that preset again.
/// </remarks>
public static class AndroidGraphicsOptions
{
    /// <summary>The rows, top to bottom.</summary>
    /// <remarks>The impacts are from the phone measurements in GraphicsPresets: the render scale is
    /// most of the GPU's work (on the S25+, 60% saved about 6% against 67%, and 30% is the largest part
    /// of Low's 28% off High); the sun shadow off is about 12% alone; eight lit lamps to four
    /// took 8% on an Adreno 640 (2-3% in the 2026-11-07 walks: 24 to 8 at High, 8 to 4 at Medium, so Lamps
    /// reads Low); every lamp that casts renders its own shadow map. Distant detail and
    /// object distance are most of the rest of Ultra's +25-30% over High; trees past 300 m are 7.6% of
    /// the S10e's frame at Bower Lake and half the undergrowth 7.5%. The lamp range and bloom moved
    /// nothing measurable. Shadow softness Off to High cost the S25+ about 9%; shadow quality 1024 to
    /// 2048 nothing measurable.</remarks>
    public static readonly AndroidGraphicsOption[] All =
    {
        new("render_scale_percent", "Resolution", AndroidGraphicsImpact.High,
            "How sharp the world is drawn, as a share of the screen's own resolution. Menus and text stay sharp either way. The biggest single cost: lower is softer and faster.",
            new[] { "30", "40", "45", "50", "60", "67", "75", "85", "100" },
            v => Number(v) + "%"),
        // Larger pixels is coarser: a model's simpler versions from nearer. The trees' and the ground's own
        // coarser levels move with it, as every preset sets them.
        new("mesh_lod_pixels", "Distant detail", AndroidGraphicsImpact.Medium,
            "How soon models, trees and the ground switch to their simpler versions as they get further away. Low switches early (plainer distance, faster); Ultra keeps full detail furthest.",
            new[] { "4", "3", "2", "1", "0.5" },
            v => v switch { 4 => "Low", 3 => "Medium", 2 => "High", 1 => "Ultra", 0.5 => "Max", _ => Number(v) })
        {
            Linked = new[]
            {
                ("tree_lod_bias", new[] { "0.5", "0.5", "0.75", "1", "1" }),
                ("terrain_lod_bias", new[] { "0.5", "0.75", "1", "1", "1" }),
            },
        },
        // 0 lifts the size culling altogether.
        new("prop_cull_distance_scale", "Object distance", AndroidGraphicsImpact.Medium,
            "How far away small objects (crates, barrels, produce, furniture, fences) are still drawn, as a share of the PC version's distances. All draws every object at any distance.",
            new[] { "0.4", "0.5", "0.6", "0.8", "1", "1.5", "2", "0" },
            v => v == 0 ? "All" : Number(v * 100) + "%"),
        // 0 keeps every tree's stated reach (up to about a kilometre).
        new("tree_far_reach", "Tree distance", AndroidGraphicsImpact.Medium,
            "How far away trees are still drawn. Distant trees are many small leaves, which an older phone pays for whatever the resolution; a shorter distance thins the far tree line.",
            new[] { "250", "300", "350", "500", "0" },
            v => v == 0 ? "All" : Number(v) + " m"),
        new(Refii.Level.Engine_LevelProps.FoliageDensityKey, "Plant density", AndroidGraphicsImpact.Medium,
            "How much of the grass, ferns and nettles is drawn, thinned evenly. Trees stay whole. Takes effect at the next loading screen.",
            new[] { "0.25", "0.5", "0.75", "1" },
            v => Number(v * 100) + "%"),
        // The props' shadow reach moves with the distance: the presets give the full reach only from 120 m.
        new("sun_shadow_distance", "Shadow distance", AndroidGraphicsImpact.High,
            "How far from you the sun and moon cast moving shadows; further also lets smaller objects cast them. Off removes them, a large saving.",
            new[] { "0", "50", "80", "100", "120", "160", "200" },
            v => v == 0 ? "Off" : Number(v) + " m")
        {
            Linked = new[] { ("sun_shadow_reach_scale", new[] { "0.5", "0.5", "0.5", "0.75", "1", "1", "1" }) },
        },
        new(AndroidSunShadows.MapKey, "Shadow quality", AndroidGraphicsImpact.Low,
            "How finely the sun's shadows are drawn. Higher shows smaller leaf and branch shadows and tighter edges, for a little more memory. Needs Shadow distance on.",
            new[] { "1024", "2048", "4096" },
            v => v switch { 1024 => "Low", 2048 => "Medium", 4096 => "High", _ => Number(v) })
        {
            Needs = "sun_shadow_distance",
        },
        new(AndroidSunShadows.FilterKey, "Shadow softness", AndroidGraphicsImpact.Medium,
            "How smooth the edges of the sun's shadows are. Off shows hard, stepped edges; Low is soft but grainy; Medium and High are smoother and cost more. Needs Shadow distance on.",
            new[] { "0", "1", "2", "3" },
            v => v switch { 0 => "Off", 1 => "Low", 2 => "Medium", 3 => "High", _ => Number(v) })
        {
            Needs = "sun_shadow_distance",
        },
        // 0 lights every one within the range. The range moves with the count: the presets reach further
        // only once 32 or more are lit.
        new("placed_light_limit", "Lamps", AndroidGraphicsImpact.Low,
            "How many of the nearest lamps, torches and fires light their surroundings at once, and from how far; the rest fade out until you come near. All lights every one.",
            new[] { "4", "8", "16", "24", "32", "48", "0" },
            v => v == 0 ? "All" : Number(v))
        {
            Linked = new[] { ("placed_light_range", new[] { "45.0", "45.0", "45.0", "45.0", "65.0", "65.0", "85.0" }) },
        },
        new("placed_light_shadows", "Lamp shadows", AndroidGraphicsImpact.High,
            "Lets lamps and torches near you cast moving shadows. Each one draws its own shadow, so it costs a lot.",
            new[] { "false", "true" },
            v => v != 0 ? "On" : "Off"),
        new("bloom_scale", "Bloom", AndroidGraphicsImpact.Low,
            "The soft glow around bright light. The sun, the moon and lamp halos glow either way.",
            new[] { "0", "0.25", "0.5", "1" },
            v => v switch { 0 => "Off", 0.25 => "Low", 0.5 => "Medium", 1 => "High", _ => Number(v) }),
    };

    /// <summary>
    /// Every setting a step to <paramref name="value"/> writes: the row's own key, then each linked setting's
    /// value at that step. A value off the steps (set by hand) writes only the row's key.
    /// </summary>
    public static IReadOnlyList<(string Key, string Value)> Writes(AndroidGraphicsOption option, string value)
    {
        var writes = new List<(string, string)> { (option.Key, value) };
        int at = Index(option, value);
        if (Parse(option.Steps[at]) == Parse(value))
        {
            foreach ((string key, string[] values) in option.Linked)
            {
                writes.Add((key, values[at]));
            }
        }

        return writes;
    }

    /// <summary>Whether a row changes anything with the settings as they are: false while the setting it
    /// <see cref="AndroidGraphicsOption.Needs"/> is off, so the panel can grey its buttons.</summary>
    public static bool Applies(AndroidGraphicsOption option, Func<string, string> valueOf) =>
        option.Needs is not { } needed || Parse(valueOf(needed)) != 0;

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
