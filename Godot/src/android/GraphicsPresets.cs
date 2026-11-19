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
/// High (stored "android") is the phone's default; Low (stored "android_low")
/// gives up everything but the look of the game for frame rate, Medium (stored "android_mid")
/// sits between the two, and Ultra (stored "android_high") gives up less, for a phone with
/// headroom at the 30 FPS cap. Since revision 15 each has the phones it is for: Low and Medium
/// a 5-6 GB phone like the OnePlus 7 Pro, Medium and High an Adreno 640 with 8 GB like the Galaxy S10e,
/// High and Ultra a Snapdragon 8 Elite like the Galaxy S25+ (the 2026-11-07 measurements are in
/// docs/android.md). They were measured on a Galaxy S25+
/// and a Galaxy S10e in Bowerstone Market, where the phone is held at a fraction of its GPU clock by its own heat,
/// so every setting is a trade of picture against sustained frame rate (docs/android.md, "Android
/// graphics preset"). The stored names predate the labels and stay, so existing phones keep their
/// choice: "android_low" was labelled Performance and then Ultra Performance, "android_mid" Performance,
/// "android" Quality and then Balanced, and "android_high" Quality. A setting outside the sets is untouched. On a desktop the preset is neither shown nor
/// written, and changing a member setting records nothing.
/// <para>Each preset sets its lighting model (<c>android_modern_lighting</c>): Low runs the modern model
/// with the lite shaders' cheaper ways of drawing it, Medium, High and Ultra the fable model, and every
/// preset names every setting Low's modern drawing decides, so picking one puts all of them back.</para>
/// <para>Low (lean) (stored "android_low_lean", revisions 17 and 18, opt-in) was Low under the modern
/// model; at revision 19 Low took its settings, with Low's normal maps off where it had them fast, and it
/// was retired. A phone that stored it is on Low: it takes Low's values on its next start
/// (<see cref="Upgrade"/>), and the name reads as Low wherever it is still found.</para>
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
    /// which the launcher had been writing off on every phone. Revisions 1-9 call Balanced by its
    /// earlier label, Quality; the Quality preset after them is a new one, so no phone was on it.
    /// 10: Quality renders at 60% instead of 67%. 11: Performance ends trees at 300 m, switches the
    /// far trees' and the ground's coarser levels at half the distance, leaves the ground's normal
    /// maps out, and draws half the undergrowth. 12: each preset's render scale goes up a step: Ultra
    /// Performance 40%, Performance 50%, Balanced 60%, Quality 67%. These are the labels of their day:
    /// Ultra Performance is now Low, the later Performance Medium, Balanced High and the later Quality Ultra.
    /// 13: Low takes the low-memory tier on any phone (android_low_ram_mode). 14: High softens the
    /// sun shadow's edges at Soft Low and Ultra at Soft Medium, both on a map of 2048; the map had
    /// been fixed at 1024, and the filter at Godot's phone default, Hard. 15: the presets re-tiered so
    /// each has a phone it runs well on: Low and Medium the OnePlus 7 Pro (5.3 GB), Medium and High the
    /// Galaxy S10e (Adreno 640), High and Ultra the Galaxy S25+. High renders at 50%, lights 16 lamps,
    /// draws small props to half their distances, three quarters of the undergrowth and the far trees'
    /// thinned crowns sooner; Medium renders at 45% with the far trees' crowns at Low's distance; Low,
    /// Medium and High stop trees past 150 m swaying. Ultra keeps what it had and gives back the trees,
    /// the undergrowth and the sway. 16: every preset runs Godot's renderer on its own thread.
    /// 17: Low, Medium and High (and Ultra, which takes High's) skip the lighting maths for lights that add nothing (lite_skip_black_lights).
    /// 18: Low turns normal maps off and Low (lean) the ground's contact shadow: looks traded for speed on
    /// the lowest presets only; Medium and above keep them.
    /// 19: Low takes Low (lean)'s settings and Low (lean) is gone; each preset sets its lighting model.
    /// 20: Each preset sets its corpse persistence time (<c>corpse_lifetime_seconds</c>): Low 0s, Medium 10s, High 20s, Ultra 45s.</remarks>
    public const int Revision = 20;

    /// <summary>High: the phone's default picture.</summary>
    public const string High = "android";

    /// <summary>Low: the most frame rate that still looks like the game.</summary>
    public const string Low = "android_low";

    /// <summary>Medium: between Low and High, for a phone that runs High at half its cap.</summary>
    public const string Medium = "android_mid";

    /// <summary>Ultra: High with fewer compromises, for a phone with headroom at 30 FPS.</summary>
    public const string Ultra = "android_high";

    /// <summary>Low (lean)'s stored name (revisions 17 and 18), retired at 19: Low now.</summary>
    private const string RetiredLowLean = "android_low_lean";

    /// <summary>The named presets.</summary>
    private static readonly string[] Named = { Low, Medium, High, Ultra };

    /// <summary>The preset a stored name stands for, unquoted and in lower case: a retired name is its
    /// successor (<c>android_low_lean</c> is Low), any other name itself.</summary>
    public static string Canonical(string preset)
    {
        string name = preset.Trim().Trim('"').ToLowerInvariant();
        return name == RetiredLowLean ? Low : name;
    }

    /// <summary>Whether this platform offers the presets: Android. Tests stand in for a phone with it.</summary>
    public static bool Offered { get; set; } = Refii.Core.Engine_Platform.IsAndroid;

    /// <summary>Whether refii.toml holds the setting as far as the presets decide (<c>RuntimeConfig.ShouldWriteCVar</c>).</summary>
    /// <remarks>The graphics preset and its revision are a phone's (<see cref="Offered"/>):
    /// a desktop neither shows nor writes them.</remarks>
    public static bool ShouldWrite(string name) => (name != Key && name != RevisionKey) || Offered;

    /// <summary>The frame cap (<c>max_fps</c>) a phone starts with, set beside the preset, not in it.</summary>
    /// <remarks>An even 30 rather than an uneven 25-40 that heats the phone into lower clocks. Kept
    /// out of the presets so the in-game Options panel can change either without the other:
    /// a cap of 60 still reads as Low, and switching preset keeps the player's cap.</remarks>
    public const string AndroidFrameCap = "30";

    /// <summary>The High preset's values, in refii.toml spelling.</summary>
    public static readonly IReadOnlyDictionary<string, string> HighValues =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // Measured 2026-11-08 at the Bowerstone Crucible, uncapped: the S10e 24-26 FPS to 29 and
            // the OnePlus 7 Pro 26 to 28-29, GPU time unchanged; background and resume held on both.
            ["android_render_thread"] = "true",
            // The world at 50% of the screen's resolution; menus and text stay sharp. 60% (revisions
            // 10-14) cost an Adreno 640 15% more GPU work in Oakfield, the S25+ 12.5%.
            ["render_scale_percent"] = "50",
            // The 360's own distances: characters at their stated range, trees shadowing only as
            // far as the level file says. Coarser models a little sooner than the 360 switched.
            ["entity_draw_distance_scale"] = "1",
            ["corpse_lifetime_seconds"] = "20",
            ["tree_shadow_reach"] = "stated",
            ["mesh_lod_pixels"] = "2",
            // Far trees' thinned crowns from three quarters of the distance, the ground's levels at the
            // shared rule (Options' Distant detail High). On an Adreno 640 in Oakfield, where trees with no
            // coarser model are most of the 2.4 million triangles in view, the trees and the models'
            // levels a step coarser took 3% off High; the trees alone half again took 8.5% off Medium.
            ["tree_lod_bias"] = "0.75",
            ["terrain_lod_bias"] = "1",
            // Trees past 150 m stand still in the wind, where their sway is about a pixel: 3% of an
            // Adreno 640's work at Medium in Oakfield, 5% of the S25+'s at High (live, alternating).
            [Refii.Level.Engine_TreeSwayFar.Key] = "150",
            // Small props stop drawing at half the size-tiered distances: a chair at 30 m, not
            // 60. Most of what it removes in Bowerstone is furniture inside buildings.
            ["prop_cull_distance_scale"] = "0.5",
            // Sun shadows where the theme casts them (as the 360), over a shorter reach.
            ["sun_shadows"] = "false",
            ["sun_shadow_distance"] = "80",
            ["sun_shadow_reach_scale"] = "0.5",
            // The sun's map at 2048, twice the 1024 the phone always had, so the soft edge blurs half
            // as much ground, and its edges filtered at Godot's Soft Low instead of the phone default,
            // Hard, which stair-steps every leaf. S25+ at an Oakfield save, GPU-bound at 734 MHz: Soft
            // Low +0.5 Mcycles a frame (+3%) against Hard, a relaunch per filter; Soft Very Low cost
            // nothing measurable but is grainy without TAA. The 2048 map, switched live in three pairs
            // with the sun up, cost nothing (20.65-20.74 against 20.96-21.05 ms) and 6 MB more memory.
            ["sun_shadow_map"] = "2048",
            ["sun_shadow_filter"] = "2",
            // Placed lights as the shipped data: the lighting presets' Performance light range
            // (45 m) and Balanced shadow range (30 m), and shadows only from the few spot lights
            // marked to cast.
            // The nearest 16 placed lights lit at once; the far lamps fade out until near. 16
            // darkens a few lit shop windows across the Bowerstone square at dusk; 24 (revisions
            // 1-14) is Ultra's.
            ["placed_light_limit"] = "16",
            ["placed_light_shadows"] = "false",
            ["placed_light_range"] = "45.0",
            ["placed_shadow_range"] = "30.0",
            // The rest are the same picture for less work, measured at the Bowerstone Market archway
            // at night (00:50) on both phones (relaunch-paired, GPU work in Mcycles a frame); kept in
            // High wherever the S25+ showed no difference, for the phones built like the S10e.
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
            // for Low).
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
            // Lite builds without Godot's shadow sampling while no shadow map renders: High's
            // sun shadow renders wherever the theme casts one, so only Low and Medium take it.
            ["lite_shadowless"] = "false",
            // Static props' lite surfaces on a build per bake mode: nothing on the S10e.
            ["lite_bake_variants"] = "false",
            // Model materials' set-once flags (PBR, ORM, emission, normal map...) folded, and the
            // lite ground's no-weight fallback through its splat loop's own fetch: S25+ 13.7 / 13.1
            // -> 13.5 / 13.3 (nothing); an Adreno 640 8% and about 2%.
            ["lite_material_folds"] = "true",
            ["lite_terrain_single_fetch"] = "true",
            // The lighting maths skipped for lights that add nothing (Godot's empty first slot per
            // light type): about 5% of an Adreno 640's GPU time in town, nothing measurable on the S25+.
            ["lite_skip_black_lights"] = "true",
            // Normal maps drawn through the meshes' tangents, as on desktop (Low turns them off).
            [Refii.Models.Engine_ShaderVariant.NormalMapsKey] = "full",
            // The fable lighting model a phone has run since the preview, not Low's modern one: on the
            // S10e High costs less under it.
            ["android_modern_lighting"] = "false",
            // Low's cheaper ways of drawing the modern model, at their own defaults, so that leaving Low
            // puts them back: the fog's lean sums, the ground-contact shadow's exact fetch near the ground,
            // objects' positions and indoor light map coordinates handed on by their vertices, the modern
            // blend read per pixel.
            [Refii.AndroidRuntime.AndroidLeanShading.FogKey] = "lean",
            [Refii.AndroidRuntime.AndroidGroundShading.Key] = "near",
            [Refii.AndroidRuntime.AndroidLeanVaryings.PropFragmentWorldKey] = "false",
            [Refii.AndroidRuntime.AndroidLeanVaryings.PackLightmapUvKey] = "false",
            [Refii.Models.Engine_ShaderVariant.FoldModernModelKey] = "false",
            // The touch controls in the UI layer's 2D pass, and the sky's unread radiance map held
            // small (the dome's sun on the 15 Hz atmosphere tick): S25+ 15.9 / 16.1 -> 15.8 / 15.5
            // (-2%); an Adreno 640 2% each.
            ["canvas_lean"] = "true",
            ["sky_radiance_lean"] = "true",
            // Effects with no soft fade built without the depth texture, glares out of view hidden,
            // so Mobile can keep one render pass: 0 at the Bowerstone archway on the S10e, with no
            // glare or water in view; left for where they are.
            ["fx_no_depth_reads"] = "false",
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
            // The five together on the S25+ (High, uncapped, four relaunch pairs at the archway at
            // night, 00:50): GC pause per 50 s walk 126-155 against 307-363 ms, no gen2 against one,
            // managed heap 1.08-1.16 against 1.33-1.43 GB, the game thread 38-40% of a core against
            // 44-46%; frame rate (~77 fps), frame times and GPU work the same.
            ["android_loh_compaction"] = "true",
            ["anim_collapse_constant_tracks"] = "true",
            ["fx_prefetch_prepare"] = "true",
            // Bodies and the camera drawn along a line through their statements' arrival times: on the
            // S25+, whose world runs at full speed, out-and-back walks toggled live in three pairs moved
            // no evenness figure, and it costs ~40 ms of latency. Low and Medium only.
            ["motion_arrival_fit"] = "false",
            // Far characters on the game's own half rate; a real-time rate is measured first.
            ["far_rig_pose_hz"] = "0",
            // Still scenery re-checked on the reconcile cadence; a longer one is measured first.
            ["entity_still_reconcile_scale"] = "1",
            // The far tree models, which ship no coarser level, get two thinned ones: 2.5% off the
            // S25+'s frame work at High, the crowns indistinguishable at the spot. Meant as an
            // Android default, but the launcher writes the setting's own default (off) into every
            // phone's refii.toml, so the presets carry it.
            ["tree_card_lod"] = "true",
            // The low-memory tier (AndroidMemoryRelease.LowRamMode) by the phone's memory; Low asks for it outright.
            ["android_low_ram_mode"] = "auto",
            // Every tree to its stated reach (about a kilometre), the setting's own default, which Low
            // trades away. Three quarters of the undergrowth, as Medium.
            ["tree_far_reach"] = "0",
            ["foliage_density"] = "0.75",
            // The ground's layer normal maps left out, as Low and Medium have them since revision 11: an
            // Adreno 640 holds them uncompressed. On the S10e at High, relaunch-paired walks: 24% less GPU
            // work a frame in the Westcliff forest and 8% in Bowerstone Market, ~480 MB less memory in
            // Westcliff. Ultra binds them.
            ["terrain_normal_maps"] = "false",
        };

    /// <summary>
    /// Low: High with every compromise that still looks like the game, for the
    /// most frame rate (labelled Performance until revision 11, then Ultra Performance). Measured at the Bowerstone Market
    /// save spot with the S25+ at its floor (GPU ~375-400 of 1200 MHz), these together took 28%
    /// off the GPU's work per frame against High.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> LowValues =
        new Dictionary<string, string>(HighValues, StringComparer.Ordinal)
        {
            // Every memory saving there is, on any phone: about 500 MB more free on a Galaxy S10e
            // at Westcliff, for about 1 FPS and a slower first open of the pause and Items menus.
            ["android_low_ram_mode"] = "on",
            // 40% of the screen's resolution, bilinear: the phone's own pixels are tiny, and on an
            // Adreno 640 the ground's and the models' pixel shading is still half the frame here.
            ["render_scale_percent"] = "40",
            // No dynamic sun shadow at all, even where the theme casts one: about 12% alone.
            ["sun_shadow_distance"] = "0",
            // The nearest four placed lights: each lit one is shaded on everything it reaches, and
            // on an Adreno 640 eight to four took 8% off the GPU's work in Bowerstone Market.
            ["placed_light_limit"] = "4",
            ["prop_cull_distance_scale"] = "0.4",
            ["mesh_lod_pixels"] = "4",
            ["corpse_lifetime_seconds"] = "0",
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
            // Trees end at 300 m instead of about a kilometre, the far trees' thinned crowns and the
            // ground's coarser levels come in at half the distance. On the S10e at Bower Lake (noon,
            // standing, GPU work toggled live in four alternating pairs) the three together took
            // 11% off the frame (39.6 -> 35.1 Mcycles); the trees past 300 m alone 7.6%, and there
            // the countryside's trees are half the frame. The far hills keep a thinner tree line.
            // 250 m saved 14% but bared them; 350 m saved 2%.
            ["tree_far_reach"] = "300",
            ["tree_lod_bias"] = "0.5",
            ["terrain_lod_bias"] = "0.5",
            // The ground's layer normal maps left out: about 275 MB of video memory in Bowerstone on
            // an Adreno 640, which holds them uncompressed, for a bump hard to see at 30%. An 8 GB
            // phone runs out of memory on long sessions; no GPU time either way.
            ["terrain_normal_maps"] = "false",
            // Half the undergrowth's clumps, evenly: 7.5% off the S10e's frame at Bower Lake, where
            // the nettles are alpha-cut cards stacked up close. The trees stay whole.
            ["foliage_density"] = "0.5",
            // Normal maps off (flatter stone and cloth, nothing missing): 6-12% of an Adreno 640's GPU
            // work under the fable lighting (S10e, 2026-11-12). Low trades looks for speed; Medium keeps them.
            // Low (lean) drew them fast (tangents per pixel); off is faster still.
            [Refii.Models.Engine_ShaderVariant.NormalMapsKey] = "off",
            // Low (lean)'s, since revision 19. The modern lighting model instead of the fable one; lean
            // modern (android_lean_modern, on by default) keeps effects, crowds and lamps near the fable
            // cost. On the S10e on 2026-11-12, GPU work in Mcycles a frame: Low under lean modern 17.7;
            // with the fog table, fast normal maps, the modern fold and the ground's contact shadow at the
            // vertices 15.2 (-14%).
            ["android_modern_lighting"] = "true",
            // The fog's thickness read from a small table built when the atmosphere changes, instead of a
            // power and three exponentials per pixel: within a fifth of a level of 255. Under the modern
            // model only (AndroidLeanShading.FogFor): under the fable one it cost the S10e 8%, where under
            // the modern one it saves 8%.
            [Refii.AndroidRuntime.AndroidLeanShading.FogKey] = "table",
            // The modern model's fixed blend (and baked AO at its default strength) compiled in.
            [Refii.Models.Engine_ShaderVariant.FoldModernModelKey] = "true",
            // Objects work out their world position per pixel (the ground keeps its own), and solid
            // objects' indoor light map coordinates ride the vertex colour: fewer values an object's pixel
            // reads from its vertices (12 instead of 23 with fast normal maps; fewer still with them off).
            [Refii.AndroidRuntime.AndroidLeanVaryings.PropFragmentWorldKey] = "true",
            [Refii.AndroidRuntime.AndroidLeanVaryings.PackLightmapUvKey] = "true",
            // No contact shadow under objects and walls (the stone keeps its bumps): 13.8 against the
            // bundle's 14.7 Mcycles with it, 10.6% under Low lean modern's 15.5 (S10e, 2026-11-12, n=2).
            [Refii.AndroidRuntime.AndroidGroundShading.Key] = "off",
        };

    /// <summary>
    /// Medium: between Low and High. Low's shadowless
    /// shaders and lighter ground, with more of the resolution, the lights and the distance back.
    /// </summary>
    /// <remarks>
    /// On the S10e, where Low runs about 20 FPS and High about 10: at noon,
    /// standing, toggled live in three alternating pairs, these together cost 32% more GPU work than
    /// Low at the Bowerstone Market archway (20.3 -> 26.5 Mcycles a frame) and 42% more
    /// at Bower Lake (30.2 -> 42.9), against High's roughly 120% more at the archway.
    /// </remarks>
    public static readonly IReadOnlyDictionary<string, string> MediumValues =
        new Dictionary<string, string>(LowValues, StringComparer.Ordinal)
        {
            // The low-memory tier by the phone's memory, as High.
            ["android_low_ram_mode"] = "auto",
            // Low's flat surfaces stay Low's: Medium keeps the normal maps.
            [Refii.Models.Engine_ShaderVariant.NormalMapsKey] = "full",
            // The fable lighting model, as High (Medium under lean modern is being measured).
            ["android_modern_lighting"] = "false",
            // Low's cheaper ways of drawing the modern model back at their defaults, as High has them.
            [Refii.AndroidRuntime.AndroidLeanShading.FogKey] = "lean",
            [Refii.AndroidRuntime.AndroidGroundShading.Key] = "near",
            [Refii.AndroidRuntime.AndroidLeanVaryings.PropFragmentWorldKey] = "false",
            [Refii.AndroidRuntime.AndroidLeanVaryings.PackLightmapUvKey] = "false",
            [Refii.Models.Engine_ShaderVariant.FoldModernModelKey] = "false",
            // 45% of the screen's resolution, 1.27 times Low's pixels. 50% (revisions 1-14) cost an
            // Adreno 640 8.4% more GPU work in Oakfield, where Medium ran 14 FPS.
            ["render_scale_percent"] = "45",
            // The nearest eight placed lights: +6% in town, nothing in the countryside.
            ["placed_light_limit"] = "8",
            // Small props to half their tiered distances, and the coarser models a little later:
            // +4% / +2%, and +2% / +1%.
            ["prop_cull_distance_scale"] = "0.5",
            ["mesh_lod_pixels"] = "3",
            ["corpse_lifetime_seconds"] = "10",
            // Every tree to its stated reach, the ground's coarser levels at three quarters of the
            // distance and the far trees' thinned crowns at half (Options' Distant detail Medium): the
            // trees at 0.75 cost an Adreno 640 8.5% more in Oakfield. A far reach of 400 m or 300 m
            // saved under 2% there.
            ["tree_far_reach"] = "0",
            ["tree_lod_bias"] = "0.5",
            ["terrain_lod_bias"] = "0.75",
            // Three quarters of the undergrowth's clumps.
            ["foliage_density"] = "0.75",
        };

    /// <summary>
    /// Ultra: High with its distance, shadow and resolution compromises given back toward
    /// the desktop's, for a phone that holds the 30 FPS cap at High with room to spare. The
    /// same-picture shortcuts stay as High has them.
    /// </summary>
    /// <remarks>
    /// Measured on the S25+ at the Bowerstone Market archway at noon, relaunch-paired: GPU work
    /// 12.0-13.7 Mcycles a frame against High's 9.5-9.9 (uncapped 61-65 FPS against 72-78).
    /// At the 30 FPS cap it missed 0.05-0.4% of frames, as High did: on a cooler over 15 and
    /// 18 minutes, and without one over 16 minutes at thermal status 3 after 40 minutes uncapped
    /// (2.9% in the first 2 minutes at the cap, while the GPU clock climbed back). The GPU ran at
    /// 525 MHz against High's 389-443. Memory and load time are High's. Of the variants,
    /// 75% resolution cost 4% more and 60% saved 6%, both hard to tell from 67% on the phone (so
    /// Ultra takes 60%), and
    /// a 100 m sun shadow saved nothing repeatable. The S10e is not an Ultra phone: it ran the
    /// High of revisions 10-14 at about 13 FPS. Revision 15 keeps these values while High gives up
    /// resolution, lamps and distance, so Ultra is now the S25+'s look of revisions 10-14 and more:
    /// on the S25+ uncapped 53-60 FPS in Bowerstone and Westcliff, 37 in Oakfield, 91 in a cave.
    /// </remarks>
    public static readonly IReadOnlyDictionary<string, string> UltraValues =
        new Dictionary<string, string>(HighValues, StringComparer.Ordinal)
        {
            // 67% of the screen's resolution, 1.8 times High's pixels.
            ["render_scale_percent"] = "67",
            // Coarser models when the 360 switched to them, small props at the desktop's distances.
            ["mesh_lod_pixels"] = "1",
            ["prop_cull_distance_scale"] = "1",
            ["corpse_lifetime_seconds"] = "45",
            // The sun's shadow over half again High's reach, every prop's shadow at its
            // size tier's full reach.
            ["sun_shadow_distance"] = "120",
            ["sun_shadow_reach_scale"] = "1",
            // The edges at Soft Medium: +1.5 Mcycles (+9%) against Hard on the same S25+ runs.
            ["sun_shadow_filter"] = "3",
            // The nearest 32 placed lights out to the lighting presets' Balanced light range.
            ["placed_light_limit"] = "32",
            ["placed_light_range"] = "65.0",
            // The far trees' crowns and the ground at the shared rule, every plant, and every tree
            // swaying in the wind however far.
            ["tree_lod_bias"] = "1",
            ["terrain_lod_bias"] = "1",
            ["foliage_density"] = "1",
            [Refii.Level.Engine_TreeSwayFar.Key] = "0",
            // The ground's bump, which High leaves out.
            ["terrain_normal_maps"] = "true",
            // The fable lighting model, as High.
            ["android_modern_lighting"] = "false",
        };

    /// <summary>The settings a preset decides: the High preset's keys, which every preset sets.</summary>
    public static IEnumerable<string> Members => HighValues.Keys;

    /// <summary>The name a player sees for a stored preset.</summary>
    public static string Label(string preset) => Canonical(preset) switch
    {
        High => "High",
        Low => "Low",
        Medium => "Medium",
        Ultra => "Ultra",
        _ => "Custom",
    };

    /// <summary>What a player is told before <paramref name="preset"/> applies, to confirm or cancel; null for a preset that applies at once.</summary>
    public static string? PickWarning(string preset) => preset.Equals(Ultra, StringComparison.OrdinalIgnoreCase)
        ? "Ultra is not intended for casual use and has performance issues. Expect lower frame rates and a hotter phone. For normal play, use High or lower."
        : null;

    /// <summary>The values a named preset writes (a retired name its successor's, <see cref="Canonical"/>);
    /// null for an unknown name or "custom".</summary>
    public static IReadOnlyDictionary<string, string>? Values(string preset)
    {
        preset = Canonical(preset);
        if (preset.Equals(High, StringComparison.OrdinalIgnoreCase))
        {
            return HighValues;
        }

        if (preset.Equals(Low, StringComparison.OrdinalIgnoreCase))
        {
            return LowValues;
        }

        if (preset.Equals(Medium, StringComparison.OrdinalIgnoreCase))
        {
            return MediumValues;
        }

        if (preset.Equals(Ultra, StringComparison.OrdinalIgnoreCase))
        {
            return UltraValues;
        }

        return null;
    }

    /// <summary>The preset every member setting matches, or "custom".</summary>
    /// <remarks>The stored preset wins while it still matches, so a phone on Low stays on Low whatever
    /// it set by hand outside the presets' settings; otherwise the first match does. Every preset sets
    /// the same settings, so no two match the same values. A retired stored name reads as its successor
    /// (<see cref="Canonical"/>).</remarks>
    public static string Current(IReadOnlyDictionary<string, string> values)
    {
        string storedName = values.TryGetValue(Key, out string? name) ? Canonical(name) : string.Empty;
        IReadOnlyDictionary<string, string>? stored = Values(storedName);
        if (stored is not null && Matches(stored, values))
        {
            return storedName;
        }

        foreach (string preset in Named)
        {
            IReadOnlyDictionary<string, string> chosen = Values(preset)!;
            if (Matches(chosen, values) &&
                (stored is null || stored.Keys.All(key => chosen.ContainsKey(key) || Same(ValueOf(values, key), DefaultOf(key)))))
            {
                return preset;
            }
        }

        return Custom;
    }

    /// <summary>
    /// What <see cref="Apply"/> writes for <paramref name="preset"/> over <paramref name="values"/>, besides
    /// the preset's name and revision: its own values, and the defaults of any settings that only the
    /// stored preset being left decides (none since revision 19, when every preset sets the same ones).
    /// Null for "custom" or an unknown name.
    /// </summary>
    public static IReadOnlyDictionary<string, string>? Writes(string preset, IDictionary<string, string> values)
    {
        if (Values(preset) is not { } chosen)
        {
            return null;
        }

        var writes = new Dictionary<string, string>(chosen, StringComparer.Ordinal);
        string stored = values.TryGetValue(Key, out string? name) ? Canonical(name) : string.Empty;
        if (stored != Canonical(preset) && Values(stored) is { } leaving)
        {
            foreach (string key in leaving.Keys.Where(key => !chosen.ContainsKey(key)))
            {
                writes[key] = DefaultOf(key);
            }
        }

        return writes;
    }

    /// <summary>Writes a named preset's values (<see cref="Writes"/>); false for "custom" or an unknown name.</summary>
    public static bool Apply(string preset, IDictionary<string, string> values)
    {
        if (Writes(preset, values) is not { } chosen)
        {
            return false;
        }

        foreach ((string key, string value) in chosen)
        {
            values[key] = value;
        }

        values[Key] = Canonical(preset);
        values[RevisionKey] = Revision.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return true;
    }

    /// <summary>Brings settings written from an earlier revision of the presets up to this one.</summary>
    /// <remarks>A phone whose stored preset is still a named one never changed one of its settings
    /// by hand (that stores "custom"), so it takes the preset's current values; a custom phone
    /// keeps every value it has. A retired name takes its successor's values and name (Low (lean)
    /// becomes Low at revision 19). Either way the revision is recorded, so this runs once per
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

    private static bool Matches(IReadOnlyDictionary<string, string> preset, IReadOnlyDictionary<string, string> values) =>
        preset.All(pair => Same(ValueOf(values, pair.Key), pair.Value));

    private static string ValueOf(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out string? value) ? value : DefaultOf(key);

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
