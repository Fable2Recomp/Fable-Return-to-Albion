using System;
using System.Collections.Generic;
using System.Linq;

namespace Refii.Launcher.Core;

/// <summary>
/// The phone's named sets of the picture's cost settings, the way <c>lighting_preset</c> names
/// light and shadow ranges: choosing one writes its values into the individual settings, and any
/// later change to one of them reads back as "custom". Android only (<see cref="Offered"/>).
/// </summary>
/// <remarks>
/// Quality (stored "android") is the phone's default; Performance (stored "android_low") gives
/// up more to hold 30 FPS once the phone has throttled fully. They were measured on a Galaxy S25+
/// and a Galaxy S10e in Bowerstone Market, where the phone is held at a fraction of its GPU clock by its own heat,
/// so every setting is a trade of picture against sustained frame rate (docs/android.md, "Android
/// graphics preset"). The stored names predate the labels and stay, so existing phones keep their
/// choice. A setting outside the sets is untouched. On a desktop the preset is neither shown nor
/// written, and changing a member setting records nothing.
/// </remarks>
public static class GraphicsPresets
{
    public const string Key = "graphics_preset";

    public const string Custom = "custom";

    /// <summary>refii.toml's record of the presets' revision a phone's settings were last written from.</summary>
    public const string RevisionKey = "graphics_preset_revision";

    /// <summary>The presets' revision: raised whenever a preset's values change, so that a phone
    /// still on that preset takes the new values on its next start (<see cref="Upgrade"/>).</summary>
    /// <remarks>1: the presets as the 2026-10-25 preview shipped them, before the revision was
    /// recorded. 2: Quality takes the lite shaders, the 10-bit colour buffer and the same-picture
    /// shortcuts; Performance the lite shaders without shadow sampling and the stable light counts.
    /// 3: both take the pipeline warm-up. 4: Quality takes the stable light counts. 5: Performance
    /// places the guest's game thread on the prime core. 6: Performance draws bodies and the camera
    /// by their statements' arrival times (motion_arrival_fit). 7: Performance sets the .NET GC's
    /// young budget and high-load threshold, compacts the large object heap after a load, collapses
    /// constant animation tracks and prepares effect textures on the prefetch thread. 8: Quality
    /// takes those four and the prime core too. 9: both thin the distant trees (tree_card_lod),
    /// which the launcher had been writing off on every phone.</remarks>
    public const int Revision = 9;

    /// <summary>Quality: the phone's default picture.</summary>
    public const string Quality = "android";

    /// <summary>Performance: Quality with the compromises a fully throttled phone needs for 30 FPS.</summary>
    public const string Performance = "android_low";

    /// <summary>Whether this platform offers the presets: Android. Tests stand in for a phone with it.</summary>
    public static bool Offered { get; set; } = Refii.Core.Engine_Platform.IsAndroid;

    /// <summary>The frame cap (<c>max_fps</c>) a phone starts with, set beside the preset, not in it.</summary>
    /// <remarks>An even 30 rather than an uneven 25-40 that heats the phone into lower clocks. Kept
    /// out of the presets so the in-game Options panel can change either without the other:
    /// a cap of 60 still reads as Performance, and switching preset keeps the player's cap.</remarks>
    public const string AndroidFrameCap = "30";

    /// <summary>The Quality preset's values, in refii.toml spelling.</summary>
    public static readonly IReadOnlyDictionary<string, string> QualityValues =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // The world at half the screen's resolution; menus and text stay sharp.
            ["render_scale_percent"] = "50",
            // The 360's own distances: characters at their stated range, trees shadowing only as
            // far as the level file says. Coarser models a little sooner than the 360 switched.
            ["entity_draw_distance_scale"] = "1",
            ["tree_shadow_reach"] = "stated",
            ["mesh_lod_pixels"] = "2",
            // Small props stop drawing at 60% of the size-tiered distances: a chair at 36 m, not
            // 60. Most of what it removes in Bowerstone is furniture inside buildings.
            ["prop_cull_distance_scale"] = "0.6",
            // Sun shadows where the theme casts them (as the 360), over a shorter reach.
            ["sun_shadows"] = "false",
            ["sun_shadow_distance"] = "80",
            ["sun_shadow_reach_scale"] = "0.5",
            // Placed lights as the shipped data: Performance's light range (45 m) and Balanced's
            // shadow range (30 m), and shadows only from the few spot lights marked to cast.
            // The nearest 24 placed lights lit at once; the far lamps fade out until near. 16
            // already darkened lit shop windows across the Bowerstone square at dusk.
            ["placed_light_limit"] = "24",
            ["placed_light_shadows"] = "false",
            ["placed_light_range"] = "45.0",
            ["placed_shadow_range"] = "30.0",
            // The rest are the same picture for less work, measured at the Bowerstone Market archway
            // in daylight on both phones (relaunch-paired, GPU work in Mcycles a frame); kept in
            // Quality wherever the S25+ showed no difference, for the phones built like the S10e.
            // The 10-bit colour buffer: half the bandwidth. S25+ 16.3 -> 15.2 / 15.0 (-7%); an
            // Adreno 640 6%.
            ["hdr_2d"] = "false",
            // Lite surface shaders: the work Android never shows compiled out, about a third fewer
            // fragment instructions. S25+ 15.2 / 15.4 / 15.6 -> 12.4 / 13.3 / 13.3 (-15%); an
            // Adreno 640 30%.
            ["shader_variant"] = "lite",
            // The entity walk's skip index: the same entities drawn. S25+ entities 3.17 -> 2.79 /
            // 2.82 ms; the S10e's 13.5 -> 10.3-11.2 ms.
            ["entity_skip_index"] = "true",
            // Entity creation's frame budget: off in both until measured on a phone (5 suggested
            // for Performance).
            ["entity_create_budget_ms"] = "0",
            // The effects' main-thread shortcuts: 0.4-0.5 ms of the S10e's FX step.
            ["fx_cpu_fastpaths"] = "true",
            // Zero-energy fillers pinning Mobile's per-draw light counts, so walking past lamps asks
            // for no new pipelines; the same picture and GPU work. On an Adreno 640 (no ubershaders)
            // a 40 s walk through Bowerstone compiled 31-39 pipelines, 0.4-2.9 s each, and 7 with
            // this. The S25+ keeps ubershaders, and with the warm-up on the same walk with no
            // pipeline cache went from worst frames 36.5 / 23.9 ms, p99 25.0 / 21.4, to 21.8 / 21.1,
            // p99 20.8 / 20.0.
            ["stable_light_counts"] = "true",
            // Lite builds without Godot's shadow sampling while no shadow map renders: Quality's
            // sun shadow renders wherever the theme casts one, so only Performance takes it.
            ["lite_shadowless"] = "false",
            // Static props' lite surfaces on a build per bake mode: nothing on the S10e.
            ["lite_bake_variants"] = "false",
            // Model materials' set-once flags (PBR, ORM, emission, normal map...) folded, and the
            // lite ground's no-weight fallback through its splat loop's own fetch: S25+ 13.7 / 13.1
            // -> 13.5 / 13.3 (nothing); an Adreno 640 8% and about 2%.
            ["lite_material_folds"] = "true",
            ["lite_terrain_single_fetch"] = "true",
            // The touch controls in the UI layer's 2D pass, and the sky's unread radiance map held
            // small (the dome's sun on the 15 Hz atmosphere tick): S25+ 15.9 / 16.1 -> 15.8 / 15.5
            // (-2%); an Adreno 640 2% each.
            ["canvas_lean"] = "true",
            ["sky_radiance_lean"] = "true",
            // Effects with no soft fade built without the depth texture, glares out of view hidden,
            // so Mobile can keep one render pass: 0 at the Bowerstone archway on the S10e, with no
            // glare or water in view; left for where they are.
            ["fx_no_depth_reads"] = "false",
            // The glares' occlusion once per corner instead of per pixel: 0 at the same spot.
            ["sky_glare_cheap"] = "false",
            // Every surface kind drawn under the loading screen, so the pipelines a first walk would
            // compile at their first draw are compiled at load. S25+ (ubershaders on), a 40 s walk
            // from the Bowerstone Market archway with no pipeline cache: worst frames 539 ms and
            // 7.9 s without, 87 and 44 ms with; no longer to load. On the S10e (no ubershaders),
            // with stable_light_counts: 25 compiled in 13 s of loading, none in the walk.
            ["android_pipeline_warmup"] = "true",
            // The guest's game thread on the prime core at nice -10. On the S10e it wandered between
            // cpu5 and 6 (2.13 GHz under load) and the little cores at 77% of a core; on cpu7 (2.84 GHz),
            // paired relaunches walking at a 20 fps cap ran the world at 0.76-0.77 of real time
            // against 0.66. The S25+ already runs the world at full speed: there the thread went from
            // 45% of a core to 39-40%, and nothing else changed.
            ["android_guest_prime_core"] = "true",
            // A 32 MB young generation (the runtime picks ~2.7 MB on the S10e) and the high-load
            // threshold at 97% instead of 90%: in game the S10e sits at 90-92% memory load, where every
            // collection compacts and a fuller gen0 escalated to a 1 s blocking gen2. Paired relaunches
            // walking the archway: GC pause per 50 s walk ~200 against ~570 ms, no gen2, +40 MB committed.
            ["android_dotnet_gc"] = "GCgen0size=2000000 GCHighMemPercent=61",
            // The large object heap compacted at the release after a load, collapsed animation tracks
            // whose keys are all equal (the same poses), and effect textures converted to ASTC on the
            // prefetch thread instead of the main thread at an effect's first sight. With the GC values
            // above, paired relaunches walking the archway, Bowerlake and the square: managed heap
            // 0.83-0.85 against 1.41 GB, PSS -200-400 MB, continue-to-world 97-100 against 108-122 s,
            // archway frames at 50 ms 85-87% against 81-82% and its worst 84 against 150-1153 ms.
            // The five together on the S25+ (Quality, uncapped, four relaunch pairs at the archway in
            // daylight): GC pause per 50 s walk 126-155 against 307-363 ms, no gen2 against one,
            // managed heap 1.08-1.16 against 1.33-1.43 GB, the game thread 38-40% of a core against
            // 44-46%; frame rate (~77 fps), frame times and GPU work the same.
            ["android_loh_compaction"] = "true",
            ["anim_collapse_constant_tracks"] = "true",
            ["fx_prefetch_prepare"] = "true",
            // Bodies and the camera drawn along a line through their statements' arrival times: on the
            // S25+, whose world runs at full speed, out-and-back walks toggled live in three pairs moved
            // no evenness figure, and it costs ~40 ms of latency. Performance only.
            ["motion_arrival_fit"] = "false",
            // Far characters on the game's own half rate; a real-time rate is measured first.
            ["far_rig_pose_hz"] = "0",
            // Still scenery re-checked on the reconcile cadence; a longer one is measured first.
            ["entity_still_reconcile_scale"] = "1",
            // The far tree models, which ship no coarser level, get two thinned ones: 2.5% off the
            // S25+'s frame work at Quality, the crowns indistinguishable at the spot. Meant as an
            // Android default, but the launcher writes the setting's own default (off) into every
            // phone's refii.toml, so the presets carry it.
            ["tree_card_lod"] = "true",
        };

    /// <summary>
    /// Performance: Quality with the compromises a fully throttled phone needs to hold 30 FPS.
    /// Measured at the Bowerstone Market save spot with the S25+ at its floor (GPU ~375-400 of
    /// 1200 MHz), these together took 28% off the GPU's work per frame against Quality.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> PerformanceValues =
        new Dictionary<string, string>(QualityValues, StringComparer.Ordinal)
        {
            // 30% of the screen's resolution, bilinear: the phone's own pixels are tiny, and on an
            // Adreno 640 the ground's and the models' pixel shading is still half the frame here.
            ["render_scale_percent"] = "30",
            // No dynamic sun shadow at all, even where the theme casts one: about 12% alone.
            ["sun_shadow_distance"] = "0",
            // The nearest four placed lights: each lit one is shaded on everything it reaches, and
            // on an Adreno 640 eight to four took 8% off the GPU's work in Bowerstone Market.
            ["placed_light_limit"] = "4",
            ["prop_cull_distance_scale"] = "0.4",
            ["mesh_lod_pixels"] = "4",
            // The lite shaders without the shadow sampling a frame with no shadow map never runs
            // (sun_shadow_distance 0 above; ignored the moment anything could cast): 7% of an
            // Adreno 640's GPU work.
            ["lite_shadowless"] = "true",
            // Bodies and the camera drawn along a line fitted through their statements' arrival
            // times, 100 ms behind the frame, instead of by their half-a-tick-out stamps. On the S10e,
            // out-and-back walks at a 20 fps cap with the key toggled live in pairs: the hero's
            // per-frame step sd 17/12/13/28% of its local median against 34/59/44/45% without, stalls
            // and lurches 3/0/1/2 per ~115 frames against 10/32/22/22, and its step against the
            // camera's ~20% against ~49%.
            ["motion_arrival_fit"] = "true",
        };

    /// <summary>The settings a preset decides: exactly the Quality preset's keys.</summary>
    public static IEnumerable<string> Members => QualityValues.Keys;

    /// <summary>The name a player sees for a stored preset.</summary>
    public static string Label(string preset) => preset.ToLowerInvariant() switch
    {
        Quality => "Quality",
        Performance => "Performance",
        _ => "Custom",
    };

    /// <summary>The values a named preset writes; null for an unknown name or "custom".</summary>
    public static IReadOnlyDictionary<string, string>? Values(string preset)
    {
        if (preset.Equals(Quality, StringComparison.OrdinalIgnoreCase))
        {
            return QualityValues;
        }

        if (preset.Equals(Performance, StringComparison.OrdinalIgnoreCase))
        {
            return PerformanceValues;
        }

        return null;
    }

    /// <summary>The preset every member setting matches, or "custom".</summary>
    public static string Current(IReadOnlyDictionary<string, string> values)
    {
        foreach (string preset in new[] { Performance, Quality })
        {
            if (Values(preset)!.All(pair => Same(values.TryGetValue(pair.Key, out string? value) ? value : DefaultOf(pair.Key), pair.Value)))
            {
                return preset;
            }
        }

        return Custom;
    }

    /// <summary>Writes a named preset's values; false for "custom" or an unknown name.</summary>
    public static bool Apply(string preset, IDictionary<string, string> values)
    {
        if (Values(preset) is not { } chosen)
        {
            return false;
        }

        foreach ((string key, string value) in chosen)
        {
            values[key] = value;
        }

        values[Key] = preset.ToLowerInvariant();
        values[RevisionKey] = Revision.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return true;
    }

    /// <summary>Brings settings written from an earlier revision of the presets up to this one.</summary>
    /// <remarks>A phone whose stored preset is still a named one never changed one of its settings
    /// by hand (that stores "custom"), so it takes the preset's current values; a custom phone
    /// keeps every value it has. Either way the revision is recorded, so this runs once per
    /// revision and a later hand edit of refii.toml stays as written.</remarks>
    /// <param name="values">The runtime configuration's values, changed in place.</param>
    /// <returns>Whether anything changed.</returns>
    public static bool Upgrade(IDictionary<string, string> values)
    {
        if (!values.TryGetValue(Key, out string? preset) ||
            (values.TryGetValue(RevisionKey, out string? stored) &&
             int.TryParse(stored, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int revision) &&
             revision >= Revision))
        {
            return false;
        }

        if (!Apply(preset, values))
        {
            values[RevisionKey] = Revision.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return true;
    }

    private static string DefaultOf(string key) =>
        RuntimeCVarSet.All.FirstOrDefault(definition => definition.Name == key)?.DefaultValue ?? string.Empty;

    /// <summary>Numbers compare by value ("1" is "1.0"), everything else by its unquoted text.</summary>
    private static bool Same(string left, string right)
    {
        left = left.Trim().Trim('"');
        right = right.Trim().Trim('"');
        if (double.TryParse(left, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double a) &&
            double.TryParse(right, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double b))
        {
            return Math.Abs(a - b) < 1e-6;
        }

        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }
}
