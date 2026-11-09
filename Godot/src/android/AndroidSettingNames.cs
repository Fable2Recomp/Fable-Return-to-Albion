using System;
using System.Collections.Generic;

namespace Refii.Launcher.Core;

/// <summary>
/// The names and plain descriptions a phone's launcher shows for its settings, in a player's words
/// rather than the settings' keys: "Resolution", not "Render scale percent"; "Distant detail", not
/// "Mesh LOD pixels". The keys in refii.toml are unchanged. Android only (<see cref="GraphicsPresets.Offered"/>):
/// a desktop keeps its key-as-words labels and its own descriptions.
/// </summary>
/// <remarks>
/// Covers what the launcher shows a phone player without Advanced (the preset, the frame limit and the
/// settings beside them, the camera, vibration, online play and split screen) and every setting the
/// graphics presets decide, which Advanced lists. The in-game Options rows use the same names
/// (<see cref="Refii.AndroidRuntime.AndroidGraphicsOptions"/>). The presets' same-picture shortcuts are
/// named for what they do for a player, since none of them changes what the screen shows.
/// </remarks>
public static class AndroidSettingNames
{
    private static readonly Dictionary<string, (string Name, string? About)> Phone = new(StringComparer.Ordinal)
    {
        // What a player sets.
        [GraphicsPresets.Key] = ("Graphics preset",
            "The picture's settings as one choice. Low gives up shadows, sharpness and some distance for the "
            + "most frame rate and the least memory; Medium adds sharpness, lamps and distance back; High "
            + "adds the sun's shadows; Ultra is sharper still with longer shadows and more distance, for the "
            + "fastest phones. Changing one of the settings afterwards reads as Custom."),
        ["max_fps"] = ("Frame limit",
            "The most frames a second the game draws; 0 is no limit. An even 30 keeps a phone cooler and "
            + "steadier than an uneven higher rate. Options in the game has a button for each."),
        ["fps_counter"] = ("FPS counter", "Shows the frame rate, small, in the top left corner of the screen."),
        ["render_scale_percent"] = ("Resolution",
            "How sharp the world is drawn, as a percentage of the screen's own resolution. Menus and text "
            + "stay sharp either way. The biggest single cost: lower is softer and faster."),
        [Refii.AndroidRuntime.AndroidStartup.SkipVideosKey] = ("Skip intro videos",
            "Goes straight past the logo videos when the game starts."),
        [Refii.AndroidRuntime.AndroidBakedShadows.Key] = ("Baked lamp shadows",
            "Works out the shadows from lamps and torches ahead of time and stores them on the phone, so they "
            + "cost little while you play. Needs about 2 GB of space and a long one-time bake when you next "
            + "press Play. You can cancel the bake and carry on later."),
        ["camera_invert_x"] = ("Invert left/right", "Turns the camera the other way when you drag or push the stick sideways."),
        ["camera_invert_y"] = ("Invert up/down", "Tilts the camera the other way when you drag or push the stick up or down."),
        ["controller_vibration"] = ("Vibration", "Rumble on the phone, or on a controller."),
        ["vibration_strength"] = ("Vibration strength", "How strong the rumble is, as a percentage of what the game asks for."),
        ["rexnet"] = ("Online play (RexNet)", "Lets you play with friends over the internet and see them online."),
        ["rexnet_display_name"] = ("Online name", "The name your friends see. Up to 32 letters."),
        ["coop_split_screen"] = ("Split-screen co-op", "A second player with a controller gets half the screen."),
        ["coop_split_screen_orientation"] = ("Split-screen layout", "Side by side or one above the other."),

        // The settings the graphics presets decide (Advanced).
        ["entity_draw_distance_scale"] = ("Character distance", "How far away people and creatures are still drawn, against the original game's distances."),
        ["tree_shadow_reach"] = ("Tree shadow distance", "How far away trees cast their shadows."),
        ["mesh_lod_pixels"] = ("Distant detail",
            "How soon models switch to their simpler versions as they get further away. Larger numbers switch sooner: plainer distance, faster."),
        ["prop_cull_distance_scale"] = ("Object distance",
            "How far away small objects (crates, barrels, furniture, fences) are still drawn, against the PC version's distances. 0 draws them all."),
        ["sun_shadows"] = ("Sun shadows everywhere", "Off: the sun casts moving shadows only where the area's lighting asks for them, as on the Xbox 360."),
        ["sun_shadow_distance"] = ("Shadow distance", "How far from you, in metres, the sun and moon cast moving shadows. 0 turns them off."),
        ["sun_shadow_reach_scale"] = ("Small object shadows", "How far small and medium objects cast the sun's shadow, as a share of their full distance."),
        ["sun_shadow_map"] = ("Shadow quality", "How finely the sun's shadows are drawn: 1024, 2048 or 4096."),
        ["sun_shadow_filter"] = ("Shadow softness", "How smooth the edges of the sun's shadows are: 0 is hard, 1 soft but grainy, 2 and 3 smoother."),
        ["placed_light_limit"] = ("Lamps", "How many of the nearest lamps, torches and fires light their surroundings at once. 0 lights all."),
        ["placed_light_range"] = ("Lamp distance", "How far away, in metres, a lamp or torch still lights its surroundings."),
        ["placed_light_shadows"] = ("Lamp shadows", "Lets lamps and torches near you cast moving shadows. Costs a lot."),
        ["placed_shadow_range"] = ("Lamp shadow distance", "How far away, in metres, a lamp's shadows are drawn."),
        ["tree_far_reach"] = ("Tree distance", "How far away, in metres, trees are still drawn. 0 draws them as far as the area does."),
        ["tree_lod_bias"] = ("Distant tree detail", "When distant trees switch to their simpler versions: lower switches nearer."),
        ["terrain_lod_bias"] = ("Distant ground detail", "When the distant ground switches to its simpler versions: lower switches nearer."),
        ["terrain_normal_maps"] = ("Ground surface detail", "The small bumps drawn on paths, grass and rock. Off saves a lot of memory."),
        ["foliage_density"] = ("Plant density", "How much of the grass, ferns and nettles is drawn, thinned evenly. Applies at the next loading screen."),
        ["prop_sway_far_metres"] = ("Distant tree sway", "Trees farther than this many metres stand still in the wind, where their sway is about a pixel. 0 lets every tree sway."),
        ["tree_card_lod"] = ("Thinner far trees", "Distant trees drawn with fewer leaves. They look the same from that far."),
        ["bloom_scale"] = ("Bloom", "The soft glow around bright light."),
        ["android_low_ram_mode"] = ("Low-memory mode",
            "Saves memory for phones that have little: slightly softer ground and models, and some menus built the first time they open. Auto turns it on under 7 GB."),
        ["motion_arrival_fit"] = ("Smooth motion",
            "Moves the hero, people and the camera more evenly when the game runs below full speed, about a tenth of a second behind."),
        ["hdr_2d"] = ("High-precision colour", "A finer colour buffer. Off looks the same on a phone and is faster."),
        ["shader_variant"] = ("Shader set", "Lite leaves out what a phone never draws, for speed. Applies at the next start."),
        ["lite_shadowless"] = ("Shadowless shaders", "While no shadows are drawn, shaders without shadow work. Applies at the next start."),
        ["lite_bake_variants"] = ("Baked-light shaders", "Shaders built for each kind of baked lighting. The same picture."),
        ["lite_material_folds"] = ("Prebuilt material shaders", "Each material's fixed choices built into its shader. The same picture, faster."),
        ["lite_terrain_single_fetch"] = ("Lighter ground shader", "A smaller shader for unpainted ground. The same picture."),
        ["entity_skip_index"] = ("Faster scene updates", "Skips scenery that has nothing to update. The same picture."),
        ["entity_create_budget_ms"] = ("Spawn time budget", "Milliseconds a frame for bringing in new people and objects; 0 has no limit."),
        ["fx_cpu_fastpaths"] = ("Faster effects", "Shortcuts for fire, smoke and magic effects. The same picture."),
        ["stable_light_counts"] = ("Steady lamp shaders", "Avoids pauses when walking past lamps. The same picture."),
        ["canvas_lean"] = ("Lighter on-screen controls", "The touch controls drawn more cheaply. The same picture."),
        ["sky_radiance_lean"] = ("Lighter sky reflections", "The sky's reflection kept small, where nothing shows it. The same picture."),
        ["fx_no_depth_reads"] = ("Lighter effects", "Effects that need no soft edges drawn more cheaply."),
        ["android_pipeline_warmup"] = ("Prepare shaders while loading", "Avoids pauses the first time things are seen, for a slightly longer loading screen."),
        ["android_guest_prime_core"] = ("Fastest core for the game", "Keeps the game's own work on the phone's fastest processor core."),
        ["android_dotnet_gc"] = ("Memory manager tuning", "Settings for the memory manager. Leave as the preset sets it."),
        ["android_loh_compaction"] = ("Tidy memory after loading", "Packs memory together after each loading screen, which frees some."),
        ["anim_collapse_constant_tracks"] = ("Lighter animations", "Animation data that never changes is stored once. The same motion."),
        ["fx_prefetch_prepare"] = ("Prepare effects in the background", "Effect images are readied ahead instead of when first seen."),
        // The phone's own expert settings beside them (Advanced), and the scenery dials a player might try.
        ["android_render_thread"] = ("Separate render thread", "Draws on a thread of its own. Takes effect at the next start."),
        ["lite_material_folds_low_ram"] = ("Prebuilt shaders (low memory)", "Which prebuilt material choices the low-memory mode keeps. The same picture."),
        ["lite_vegetation_folds"] = ("Prebuilt plant shaders", "Plants' fixed choices built into their shaders. The same picture."),
        ["hud_half_rate"] = ("Half-rate HUD", "Draws the still parts of the HUD every other frame."),
        ["prop_far_shading_metres"] = ("Distant object bumps", "Past this many metres, objects drop the small bumps on their surfaces. 0 keeps them everywhere."),
        ["android_host_off_prime"] = ("Keep the fastest core free", "Moves the app's own work off the fastest processor core."),
        ["android_load_big_cores"] = ("Load on the big cores", "Loading screens use only the phone's faster cores."),
        ["android_pool_spin"] = ("Worker spin", "How long idle worker threads wait for more work before sleeping."),
        ["android_guest_real_clock"] = ("Real-time game clock", "Keeps the game's clock on real time when the phone falls behind."),
        ["android_idle_screen_minutes"] = ("Screen timeout while idle", "Minutes before the screen may turn off when nothing is touched."),
        ["android_memory_release"] = ("Release memory while loading", "Hands memory back to the phone during loading screens."),
        ["android_gc_big_core_thread"] = ("Memory manager on a big core", "Runs the memory manager on one of the faster cores."),
        ["android_gc_nonblocking"] = ("Background memory clean-up", "Cleans up memory without pausing the game where it can."),
        ["android_gc_load_ceiling_mb"] = ("Loading memory ceiling", "Megabytes the memory manager may hold during a load before it cleans up."),
        ["stable_light_counts_sentinel"] = ("Steady lamp shaders (check)", "A check for the steady lamp shaders. Leave as it is."),
        ["prop_draw_distance_scale"] = ("Scenery draw distance", "How far placed scenery is drawn, against the original game's distances."),
        ["prop_fade_fraction"] = ("Scenery fade", "The last share of a scenery object's distance over which it fades out instead of popping."),
        ["prop_cull_min_pixels"] = ("Smallest object drawn", "Objects smaller than this many pixels tall stop drawing. 0 draws them all. Takes effect at the next start."),
        ["foliage_cell_metres"] = ("Plant patch size", "The size, in metres, of the patches undergrowth is drawn in. 0 keeps 32."),
        ["tree_card_far_levels"] = ("Thinnest far trees", "Two more, thinner versions for the most distant trees. Takes effect at the next start."),
        ["far_rig_pose_spread"] = ("Spread distant animation", "Spreads distant people's pose updates over several frames."),
        ["far_rig_pose_hz"] = ("Distant animation rate", "How often distant people update their pose; 0 follows the game."),
        ["entity_still_reconcile_scale"] = ("Still scenery checks", "How often scenery that isn't moving is checked again."),
    };

    /// <summary>The phone's name for <paramref name="key"/>; null on a desktop or for a setting with none.</summary>
    public static string? Name(string key) =>
        GraphicsPresets.Offered && Phone.TryGetValue(key, out var entry) ? entry.Name : null;

    /// <summary>The phone's plain description of <paramref name="key"/>; null on a desktop or for a setting with none.</summary>
    public static string? About(string key) =>
        GraphicsPresets.Offered && Phone.TryGetValue(key, out var entry) ? entry.About : null;

    /// <summary>Every key with a phone name, for the tests.</summary>
    public static IEnumerable<string> Keys => Phone.Keys;
}
