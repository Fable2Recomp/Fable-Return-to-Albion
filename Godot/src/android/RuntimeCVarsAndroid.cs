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
        new(GraphicsPresets.Key, DisplayCategory, RuntimeCVarType.String, GraphicsPresets.Balanced,
            "The picture's cost settings as one choice, on a phone. Balanced trades distance, "
            + "shadows and resolution for a steady frame rate on a phone, which its own heat "
            + "holds well below its peak clock; Ultra Performance gives up the sun's shadow, most "
            + "of the resolution and some distance for the most frame rate; Performance keeps "
            + "the shadowless picture with more resolution, lights and distance; Quality "
            + "gives back resolution, distance and shadow reach on a phone with frames to "
            + "spare. Changing any of those settings afterwards reads as Custom.",
            "android_low|Ultra Performance,android_mid|Performance,android|Balanced,android_high|Quality,custom|Custom"),

        new(GraphicsPresets.RevisionKey, DisplayCategory, RuntimeCVarType.Int, "1",
            "The presets' revision these settings were last written from; a phone still on a "
            + "named preset takes that preset's newer values once, on its next start."),
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

        new("lite_terrain_single_fetch", DisplayCategory, RuntimeCVarType.Bool, "false",
            "Android, lite ground: the fallback for ground no layer is painted on is drawn by the "
            + "splat loop's own layer fetch instead of a second copy of it, a smaller shader with "
            + "the same result. Applies at the next launch."),

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

        new("android_host_off_prime", DisplayCategory, RuntimeCVarType.Bool, "false",
            "Android, CPU: every thread but the game's simulation thread is kept off the phone's "
            + "fastest cores, so that thread has them to itself instead of sharing them with "
            + "Godot's own threads. The same game; the rest of the work runs on the other cores. "
            + "Takes effect at the next launch."),

        new("foliage_density", DisplayCategory, RuntimeCVarType.Double, "1",
            "The share of ground foliage (nettles, bracken, grass clumps) drawn, from 0.25 to 1. Each "
            + "clump is kept or left out by where it stands, so the cover thins evenly. Applies to the "
            + "next area loaded."),

        new("android_guest_real_clock", DisplayCategory, RuntimeCVarType.Bool, "false",
            "Android, CPU: the game reads the real clock directly, instead of through the "
            + "desktop's stepped clock with a lock on every read. The same game at the same "
            + "speed, with less waiting between its threads. Takes effect at the next launch."),

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
