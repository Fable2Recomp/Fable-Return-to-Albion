using static Refii.Launcher.Core.RuntimeCVarsGodot;

namespace Refii.Launcher.Core;

/// <summary>
/// The phone's own rows of <see cref="RuntimeCVarsGodot.Definitions"/>: settings only Android reads
/// or acts on. Each group is spread into that list where it stood, so the settings panel and the
/// written refii.toml keep the same order and categories on every platform.
/// </summary>
public static class RuntimeCVarsAndroid
{
    /// <summary>The graphics preset and the presets' revision it was written from (<see cref="GraphicsPresets"/>), after <c>display_resolution</c>.</summary>
    public static readonly RuntimeCVarDefinition[] GraphicsPreset =
    {
        new(GraphicsPresets.Key, DisplayCategory, RuntimeCVarType.String, GraphicsPresets.High,
            "The picture's cost settings as one choice, on a phone. Low gives up the sun's "
            + "shadow, most of the resolution and some distance for the most frame rate and the "
            + "least memory (a 5-6 GB phone); Medium adds resolution, lamps and distance back (an "
            + "8 GB Adreno 640 phone's smoothest); High adds the sun's shadows and sharpness; Ultra "
            + "gives back resolution, distance, shadow reach and the ground's bump, for the fastest "
            + "phones. Changing any of those settings afterwards reads as Custom.",
            "android_low|Low,android_mid|Medium,android|High,android_high|Ultra,custom|Custom"),

        new(GraphicsPresets.RevisionKey, DisplayCategory, RuntimeCVarType.Int, "1",
            "The presets' revision these settings were last written from; a phone still on a "
            + "named preset takes that preset's newer values once, on its next start."),
    };

    /// <summary>The sun shadow's edge filter (<see cref="Refii.AndroidRuntime.AndroidSunShadows"/>), after <c>sun_shadow_map</c>.</summary>
    public static readonly RuntimeCVarDefinition[] SunShadowFilter =
    {
        new(Refii.AndroidRuntime.AndroidSunShadows.FilterKey, LightingCategory, RuntimeCVarType.Int, "0",
            "Android: how smoothly the edges of the sun's shadows are filtered. 0 is hard (Godot's "
            + "default on a phone, stair-stepped), 1 to 5 are Godot's Soft Very Low to Soft Ultra; "
            + "1 is grainy without TAA. The desktop always filters at Soft Medium. Options in the "
            + "game steps it, and it applies at once."),
    };

    /// <summary>The launcher's Skip intro videos (<see cref="Refii.AndroidRuntime.AndroidStartup"/>), beside Baked lamp shadows.</summary>
    public static readonly RuntimeCVarDefinition[] IntroVideos =
    {
        new(Refii.AndroidRuntime.AndroidStartup.SkipVideosKey, DisplayCategory, RuntimeCVarType.Bool, "false",
            "Android: skip the logo videos when the game starts."),
    };

    /// <summary>How textures are stored for the GPU. Advanced rows of the launcher.</summary>
    public static readonly RuntimeCVarDefinition[] TextureStorage =
    {
        new("android_tex_upload_cache", DisplayCategory, RuntimeCVarType.Bool, "true",
            "Android: keep each texture's GPU-ready form on disk so later loads skip the conversion."),
        new("android_eac_normals", DisplayCategory, RuntimeCVarType.Bool, "false",
            "Android, GPUs without BC: store normal maps as EAC RG11 rather than RG8, at half the "
            + "memory. A one-time conversion cost as each is first loaded."),
    };

    /// <summary>The launcher's Baked lamp shadows option (<see cref="Refii.AndroidRuntime.AndroidBakedShadows"/>), before <c>static_shadow_resolution</c>.</summary>
    public static readonly RuntimeCVarDefinition[] BakedShadows =
    {
        // Display, not Lighting: the launcher's list leaves Lighting to the desktop's own dialog.
        new(Refii.AndroidRuntime.AndroidBakedShadows.Key, DisplayCategory, RuntimeCVarType.Bool, "false",
            "Android: bake lamp and torch shadows on the phone at Play and draw them from the "
            + "stored result. About 2 GB and a long one-time bake."),
    };

    /// <summary>The frame rate in the corner of the screen (<see cref="Refii.AndroidRuntime.AndroidFpsCounter"/>), after <c>max_fps</c>.</summary>
    public static readonly RuntimeCVarDefinition[] FpsCounter =
    {
        new("fps_counter", DisplayCategory, RuntimeCVarType.Bool, "false",
            "Android: show the frame rate, small, in the top left corner of the screen. Options "
            + "in the game turns it on and off."),
    };

    /// <summary>The rumble's strength (<see cref="Refii.Game.GameInput.VibrationStrength"/>), after <c>controller_vibration</c>.</summary>
    public static readonly RuntimeCVarDefinition[] VibrationStrength =
    {
        new(Refii.AndroidRuntime.AndroidQuickSettings.VibrationStrengthKey, ControlsCategory, RuntimeCVarType.Int, "50",
            "Android: how strong the rumble is, as a percentage of what the game asks for, on the "
            + "phone's vibrator played by touch and on a controller. controller_vibration turns it "
            + "off. Options in the game steps it."),
    };

    /// <summary>Godot's render thread on Android, after <c>render_thread</c>.</summary>
    public static readonly RuntimeCVarDefinition[] RenderThread =
    {
        new("android_render_thread", DisplayCategory, RuntimeCVarType.Bool, "false",
            "Android: run Godot's renderer on its own thread. Its own key, not render_thread: "
            + "the launcher has written the desktop key's default (true) into every Android "
            + "config already. Needs an engine with the render-thread fixes (Godot 4.7.1 stock "
            + "crashes on resume with it). Takes effect at the next launch."),
    };

    /// <summary>The lite shaders, the pipeline warm-up, the CPU and GC placement, the colour buffer and the motion fit, after <c>texture_mipmap_bias</c>.</summary>
    public static readonly RuntimeCVarDefinition[] Tuning =
    {
        new("shader_variant", DisplayCategory, RuntimeCVarType.String, "full",
            "Android: the surface shaders. 'lite' compiles out what the phone never draws "
            + "(weather, debug views, parallax, specular and cube work where none applies, flat "
            + "terrain's cliff projection), which frees GPU registers; 'lite_flat' also drops the "
            + "ground's bump. The desktop always draws 'full'. Applies at the next launch.",
            "full|Full,lite|Lite,lite_flat|Lite, flat ground"),

        new("lite_shadowless", DisplayCategory, RuntimeCVarType.Bool, "false",
            "Android, lite shaders: compile out Godot's shadow sampling while the settings render "
            + "no shadow map (sun_shadow_distance 0, placed_light_shadows off); the few spot lights "
            + "that cast even then cast nothing. A setting that would cast puts the shadowed shaders "
            + "back for the session at the next level load (a pause while they recompile). Applies "
            + "at the next launch."),

        new("lite_bake_variants", DisplayCategory, RuntimeCVarType.Bool, "false",
            "Android, lite shaders: a static prop draws with a shader built for its own baked "
            + "lighting (lightmap, vertex bake, none), with the other modes and the PRT transfer "
            + "compiled out. The same picture; up to five builds of each surface shader. Applies at "
            + "the next launch."),

        new("lite_material_folds", DisplayCategory, RuntimeCVarType.Bool, "false",
            "Android, lite shaders: each model material's fixed switches (authored PBR, ORM, "
            + "self-illumination, premultiplied alpha, grass bend, and its normal map) are compiled "
            + "into its shader instead of read at draw. A surface without a normal map stops "
            + "carrying tangents too. The same picture; a few more shader builds. Applies at the next launch."),

        new("lite_material_folds_low_ram", DisplayCategory, RuntimeCVarType.String, "off",
            "Android, lite shaders, in the low-memory tier: which of lite_material_folds' switches are "
            + "compiled in, since each mix of them is a shader build of its own. const: only those "
            + "that add no build. normal: those and the normal map, at most one more build of each. "
            + "all: every switch. off: none. The same picture. Applies at the next launch.",
            "off|Off,const|No extra builds,normal|With the normal map,all|All"),

        new(Refii.Models.Engine_ShaderSpecialisation.VegetationFoldsKey, DisplayCategory, RuntimeCVarType.Bool, "false",
            "Android, lite shaders: plants' fixed switches (leaves, grass, their own normals) are "
            + "compiled into their shaders instead of read at draw, and trees stop passing an "
            + "always-zero value between shader stages. The same picture; a few more shader "
            + "builds. Applies at the next launch."),

        new("lite_terrain_single_fetch", DisplayCategory, RuntimeCVarType.Bool, "false",
            "Android, lite ground: the fallback for ground no layer is painted on is drawn by the "
            + "splat loop's own layer fetch instead of a second copy of it, a smaller shader with "
            + "the same result. Applies at the next launch."),

        new(Refii.AndroidRuntime.AndroidHudRate.Key, DisplayCategory, RuntimeCVarType.Bool, "false",
            "Android: the in-game HUD is drawn every other frame while nothing on it follows the "
            + "world or the camera; menus, markers, mod pieces and a turning camera draw every "
            + "frame. Its own animations then move at half the frame rate. Takes effect live."),

        new(Refii.Level.Engine_TreeSwayFar.Key, DisplayCategory, RuntimeCVarType.Double, "0",
            "Android, lite shaders: trees farther than this many metres stand still in the wind "
            + "(they settle over the last eighth of it); 0 is off and every tree sways. Past "
            + "150 m a tree's sway is about a pixel at a low render scale. Takes effect live."),

        new("prop_far_shading_metres", DisplayCategory, RuntimeCVarType.Double, "0",
            "Android, lite shaders: props whose nearest point is farther than this many metres draw "
            + "without their normal map, which is below a pixel there at a low render scale; 0 is "
            + "off, 30 is the suggested distance. Batches of several materials stay as they are. "
            + "Takes effect live; set before the level loads so the loading screen also prepares "
            + "the far shaders."),

        new("android_pipeline_warmup", DisplayCategory, RuntimeCVarType.Bool, "false",
            "Android: under each level's loading screen, every surface the level draws is drawn "
            + "once more beside the camera under each light count it can meet in play (none, one "
            + "or several omni and spot lights), so walking compiles no pipelines. A longer load "
            + "and some driver memory for each pipeline; applies at the next level load."),

        new("android_guest_prime_core", DisplayCategory, RuntimeCVarType.Bool, "false",
            "Android, CPU: the game's simulation thread is held on the phone's fastest cores "
            + "(those with the highest maximum clock: one on a Snapdragon 855, two on an 8 Elite) "
            + "and given a higher priority, instead of moving between the big and the little "
            + "cores. The same game; it runs nearer full speed where that thread is the limit. "
            + "Takes effect at the next launch."),

        new("android_dotnet_gc", DisplayCategory, RuntimeCVarType.String, "",
            "Android, memory: .NET garbage collector settings, as space-separated NAME=VALUE "
            + "pairs in the runtime's own spelling (hexadecimal values), each set as a DOTNET_ "
            + "variable before the runtime starts. GCgen0size=2000000 gives the youngest "
            + "generation a 32 MB budget (a few MB by default: about 2.7 MB on a Galaxy S10e), so "
            + "collections come roughly a tenth as often; GCHighMemPercent=61 moves the memory load at which it starts "
            + "compacting to save memory from 90% to 97% (a phone in a game sits near 90%); "
            + "gcConcurrent=0 turns background collection off. Only GC names "
            + "are taken. Empty leaves the defaults. Takes effect at the next launch; the desktop "
            + "reads DOTNET_ variables from its own environment and ignores it."),

        new(Refii.AndroidRuntime.AndroidPoolSpin.Key, DisplayCategory, RuntimeCVarType.String, "auto",
            "Android, CPU: how long an idle .NET worker thread spins, in rounds, before it sleeps "
            + "(the runtime's own is 280). Less spinning leaves the cores idle between the frame's "
            + "bursts of pose and cloth work, for a wake when work arrives. auto spins 35 in the "
            + "low-memory tier and leaves the runtime's own elsewhere; a number from 0 sets it; "
            + "default leaves it. Takes effect at the next launch."),

        new("android_host_off_prime", DisplayCategory, RuntimeCVarType.Bool, "false",
            "Android, CPU: every thread but the game's simulation thread is kept off the phone's "
            + "fastest cores, so that thread has them to itself instead of sharing them with "
            + "Godot's own threads. The same game; the rest of the work runs on the other cores. "
            + "Takes effect at the next launch."),

        new(Refii.AndroidRuntime.AndroidLoadPlacement.Key, DisplayCategory, RuntimeCVarType.String, "off",
            "Android, CPU: while a level builds, the .NET and Godot worker threads run on the "
            + "phone's faster cores only, and go back when the build ends; workers_main moves "
            + "the main thread too. For a phone with little cores and at least three faster "
            + "ones; the game's own threads stay where they are. The same game. Takes effect at "
            + "the next launch.",
            "off|Off,workers|Workers,workers_main|Workers and main thread"),

        new("foliage_density", DisplayCategory, RuntimeCVarType.Double, "1",
            "The share of ground foliage (nettles, bracken, grass clumps) drawn, from 0.25 to 1. Each "
            + "clump is kept or left out by where it stands, so the cover thins evenly. Applies to the "
            + "next area loaded."),

        new("android_guest_real_clock", DisplayCategory, RuntimeCVarType.Bool, "false",
            "Android, CPU: the game reads the real clock directly, instead of through the "
            + "desktop's stepped clock with a lock on every read. The same game at the same "
            + "speed, with less waiting between its threads. Takes effect at the next launch."),

        new(Refii.AndroidRuntime.AndroidIdlePower.ScreenMinutesKey, DisplayCategory, RuntimeCVarType.Int, "10",
            "Android: minutes with no input until play pauses and the screen may turn off by the "
            + "phone's own setting, which also stops the game using power. 0 keeps the screen on "
            + "for as long as the game is open."),

        new("hdr_2d", DisplayCategory, RuntimeCVarType.Bool, "true",
            "Android: the world's colour in half floats (on) or 10 bits (off). Off halves the "
            + "colour bandwidth for highlights that clip a little sooner; a change pauses the game "
            + "a few seconds while the GPU's pipelines are rebuilt. The desktop ignores it."),

        new("motion_arrival_fit", DisplayCategory, RuntimeCVarType.Bool, "false",
            "Motion: walking characters and the camera are drawn along a line fitted through "
            + "the last few positions the game stated, timed by when each arrived, a tenth of "
            + "a second behind, instead of by the game tick each was stamped with. For a game "
            + "running slow or unevenly (Android), where the stamps are half a tick out and a "
            + "walk went in stop-go steps; costs about 40 ms more latency. Live through the "
            + "debug channel (render motionArrivalFit)."),
    };

    /// <summary>The large-object-heap compaction after a load, after <c>entity_still_reconcile_scale</c>.</summary>
    public static readonly RuntimeCVarDefinition[] LohCompaction =
    {
        new("android_loh_compaction", DisplayCategory, RuntimeCVarType.Bool, "false",
            "Android, memory: when a level has finished loading, the app's large memory blocks "
            + "are compacted as on the desktop, giving back the gaps the load left. A slightly "
            + "longer pause at the end of the loading screen; nothing changes in play."),
        new("android_memory_release", DisplayCategory, RuntimeCVarType.Bool, "true",
            "Android, memory: while a level builds, memory it has finished with is collected, and "
            + "at a level change what the last area no longer needs goes back to the phone before "
            + "the next builds. A slightly longer loading screen, for a phone that runs out of memory."),
        new(Refii.Game.AndroidMemoryRelease.BigCoreThreadKey, DisplayCategory, RuntimeCVarType.Bool, "false",
            "Android, memory: with android_memory_release, the collections during a level build "
            + "run on a thread of their own, held on the phone's faster cores, instead of on any "
            + "worker. Applies at the next level load."),
        new(Refii.Game.AndroidMemoryRelease.NonBlockingKey, DisplayCategory, RuntimeCVarType.Bool, "false",
            "Android, memory: with android_memory_release, the collections during a level build "
            + "run in the background instead of stopping the build. Memory comes back later, so "
            + "the peak can be higher. Applies at the next level load."),
        new(Refii.Game.AndroidMemoryRelease.CeilingKey, DisplayCategory, RuntimeCVarType.Int, "0",
            "Android, memory: with android_memory_release, a collection during a level build "
            + "waits until the managed heap reaches this many MB or the phone's free memory and "
            + "swap are short; 0 collects at every 200 MB of growth. Applies at the next level load."),
        new("android_low_ram_mode", DisplayCategory, RuntimeCVarType.String, "auto",
            "Android, memory: the tier for a phone short of memory. Surface detail one size smaller, "
            + "fewer shader shortcuts and menus built when they first open, which costs that first "
            + "open a moment; hero_texture_sources_mb at 0 means 32 here instead of no limit. auto "
            + "turns it on under 7 GB of memory; on and off choose, and the Low graphics preset "
            + "sets on. Takes effect at the next launch.",
            "auto|Auto,on|On,off|Off"),
    };

    /// <summary>The effect texture prefetch and the lean touch-control canvas, after <c>fx_cpu_fastpaths</c>.</summary>
    public static readonly RuntimeCVarDefinition[] EffectsAndCanvas =
    {
        new("fx_prefetch_prepare", DisplayCategory, RuntimeCVarType.Bool, "false",
            "Android, effects: the background read of the effect textures also converts each "
            + "one for a GPU without the game's compressed formats, instead of the game doing it "
            + "on the frame an effect first appears. The same picture; the waiting textures take "
            + "more memory until used. Takes effect at the next launch."),

        new("canvas_lean", DisplayCategory, RuntimeCVarType.Bool, "false",
            "Android 2D: the touch controls drawn in the UI layer's screen pass instead of one "
            + "of their own while nothing shows between the two, and a screen fade too faint to "
            + "change a pixel not drawn. The same picture; the debug channel's render op "
            + "(canvasLean) flips it live."),
    };

    /// <summary>The stable light counts, after <c>placed_light_limit</c>.</summary>
    public static readonly RuntimeCVarDefinition[] StableLightCounts =
    {
        new("stable_light_counts", LightingCategory, RuntimeCVarType.Bool, "false",
            "Android: zero-energy, never-shaded lights paired with every object, so each draw "
            + "keeps the same light count as lamps come and go and Godot compiles no new "
            + "pipeline for it (a hitch of up to seconds on Adreno). The same picture; turning "
            + "it on recompiles each material once. The debug channel's render op "
            + "(stableLightCounts) flips it live."),
        new("stable_light_counts_sentinel", LightingCategory, RuntimeCVarType.Bool, "false",
            "With stable_light_counts: one filler of each type kept in the light buffer, so an "
            + "object no lamp reaches shades a defined light that adds nothing instead of an "
            + "unwritten entry. Costs two cheap light evaluations per pixel; only for when the "
            + "plain fillers show a stray light. Live as stableLightSentinel."),
    };

    /// <summary>The garbage-collection stress test, after <c>lighting_debug_view</c>.</summary>
    public static readonly RuntimeCVarDefinition[] GcStress =
    {
        new("debug_gc_stress_ms", DeveloperCategory, RuntimeCVarType.Int, "0",
            "Android, testing: a background thread forces a garbage collection of the young "
            + "generations every this many milliseconds, to make crash windows that need a "
            + "collection at the wrong moment show up in a few loads. Slows the game; 0 is off. "
            + "Takes effect at the next launch."),
    };
}
