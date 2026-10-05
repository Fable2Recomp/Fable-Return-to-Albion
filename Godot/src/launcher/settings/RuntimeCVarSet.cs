using System.Collections.Generic;
using System.Linq;

namespace Refii.Launcher.Core;

/// <summary>
/// Settings that belong to the GODOT side of the app, hand maintained.
/// </summary>
/// <remarks>
/// <see cref="RuntimeCVars"/> is generated from the native runtime's own cvar registry — 203 of
/// them — so it cannot describe anything Godot owns, and hand-editing it would last exactly until
/// the next regeneration. This is the other half.
///
/// The names are flat with a prefix rather than a <c>[texture_upscaling]</c> table, because
/// <c>ApplyTomlTable</c> on the native side flattens every table into <c>section_key</c> anyway,
/// so a table buys nothing and changes the key the reader sees.
///
/// The guest reads this same refii.toml (the host bridge states the app root), so it logs
/// <c>Config: unknown cvar</c> once per key for these: they are not in its registry.
/// </remarks>
public static class RuntimeCVarsGodot
{
    public const string Category = "Textures";

    public const string DeveloperCategory = "Developer";

    public const string LightingCategory = "Lighting";

    public const string VideoCategory = "Video";

    /// <summary>
    /// The picture: everything Godot can be asked for that the old GPU/UI categories used to
    /// pretend to offer. The window's own size, mode, monitor and vsync are generated entries
    /// re-homed here (see <c>RuntimeConfig.IsDisplayCVar</c>); these are the ones Godot owns
    /// outright and nothing else could have provided.
    /// </summary>
    public const string DisplayCategory = "Display";

    public static readonly IReadOnlyList<RuntimeCVarDefinition> Definitions =
        new List<RuntimeCVarDefinition>
        {
            new("materials_modernized", Category, RuntimeCVarType.Bool, "true",
                "The remaster switch: modernized textures and materials, or the game exactly as " +
                "it shipped. Off restores the original look — no upscaling, no occlusion, no " +
                "metallic, no parallax. Everything below follows this unless set explicitly."),
            new("texture_upscaling_enabled", Category, RuntimeCVarType.Bool, "true",
                "Enhance textures at runtime. Off keeps the original assets exactly as shipped. " +
                "Follows materials_modernized unless set."),
            new("texture_upscaling_model", Category, RuntimeCVarType.String, "resample",
                "Which upscaler to use: 'resample' is the built-in classical filter, " +
                "'realesrgan' the neural one (falls back to resample if unavailable).",
                "resample,realesrgan"),
            new("texture_upscaling_workers", Category, RuntimeCVarType.Int, "0",
                "Textures enhanced at once; 0 picks half the CPU cores. Clamped to 1-4 — measured to stop scaling past four."),
            new("texture_upscaling_weights", Category, RuntimeCVarType.String,
                "realesr-general-x4v3",
                "Base name of the neural weights beside the build (.param/.bin). The scale is " +
                "read from this name."),
            new("texture_upscaling_when", Category, RuntimeCVarType.String, "boot",
                "When textures are enhanced — all of them: props, UI and terrain alike. "
                + "'boot' prepares the whole game behind the start-up screen, so everything "
                + "is sharp from the first frame; 'play' spreads the cost through play — "
                + "props and menus sharpen moments after they appear, terrain pays a longer "
                + "FIRST load per region; 'off' turns enhancement off everywhere.",
                "boot,play,off"),
            new("texture_orm_enabled", Category, RuntimeCVarType.Bool, "true",
                "Modernized materials: occlusion, roughness, metallic and height packed into one " +
                "ORM texture, all four derived from the shipped maps. Off falls back to the " +
                "original specular-only shading. Follows materials_modernized unless set. " +
                "Applies at the next level load, not immediately."),
            new("texture_orm_ao_strength_percent", Category, RuntimeCVarType.Int, "50",
                "How strongly cavity occlusion derived from the normal map fills the ORM's red " +
                "channel. 0 disables it; the original game applies no occlusion at all."),
            new("texture_orm_metallic_strength_percent", Category, RuntimeCVarType.Int, "100",
                "How strongly a metallic channel is derived from the specular map's colour, which " +
                "is a Fresnel term in all but name — bright and coloured is metal, grey and dim is " +
                "not. 0 disables it. About a fifth of the game's specular maps describe metal; the " +
                "rest are untouched whatever this says. Needs texture_orm_enabled."),
            new("texture_orm_height_enabled", Category, RuntimeCVarType.Bool, "true",
                "Solve a height field from the normal map into the ORM's alpha channel, so " +
                "surfaces can take parallax. 89.8% of materials have a normal map to derive it " +
                "from. Off buys back half the ORM's bytes — a varying alpha forces a larger " +
                "block format. Needs texture_orm_enabled."),
            new("texture_cache_enabled", Category, RuntimeCVarType.Bool, "true",
                "Keep decoded and enhanced textures on disk so the work happens once per asset."),
            new("texture_cache_max_mb", Category, RuntimeCVarType.Int, "0",
                "Disk the texture cache may hold; 0 picks 4 GB, or 12 GB with upscaling on. " +
                "The oldest entries are evicted above it."),
            new("pubgame_vector_shapes", Category, RuntimeCVarType.Bool, "true",
                "Draw the pub games (Keystone and friends) as vector geometry, so their artwork " +
                "is sharp at any window size. Off falls back to drawing it into bitmaps at the " +
                "size it was authored at, which is softer, slower and uses more memory."),
            new("pubgame_raster_scale_percent", Category, RuntimeCVarType.Int, "100",
                "How sharply the pub games (Keystone and friends) draw their Flash artwork. " +
                "100 is the size it was authored at; 0 follows the window. Higher is sharper " +
                "but slower to load — Keystone measures 4.6 s and 109 MB at 100, and 11.9 s " +
                "and 348 MB at 250, because the artwork is drawn on the CPU. Ignored unless " +
                "pubgame_vector_shapes is off."),
            new("developer_menu_enabled", DeveloperCategory, RuntimeCVarType.Bool, "false",
                "Show the Refii debug overlay in game \u2014 guest state, the save loader and "
                + "the Lua console. Off is the shipping default; the overlay is a development "
                + "fixture and draws over the game's own UI."),
            new("display_aspect", DisplayCategory, RuntimeCVarType.String, "16:9",
                "The shape of the window. Picking one narrows the resolution list below to the "
                + "sizes that match it, the way a modern game's video options do.",
                "16:9,16:10,4:3,21:9,32:9"),

            new("display_resolution", DisplayCategory, RuntimeCVarType.String, "1280x720",
                "The window size, chosen from the sizes that fit the aspect ratio above. This "
                + "replaces typing a width and a height by hand."),

            new("max_fps", DisplayCategory, RuntimeCVarType.Int, "0",
                "Frame rate cap. 0 is uncapped, which with vsync off will run the GPU as hard "
                + "as it can. The SIMULATION is not affected: the guest advances on its own "
                + "fixed step and only the drawing rate changes."),

            new("vsync_mode", DisplayCategory, RuntimeCVarType.String, "mailbox",
                "How frames meet the display when vsync is on. 'mailbox' shows the newest "
                + "finished frame at each refresh with no tearing and no cap at a fraction of "
                + "the refresh rate; 'on' waits for every refresh, so a frame that misses one "
                + "shows at half the rate; 'adaptive' waits when the frame is fast and tears "
                + "when it is slow. Where the driver has no mailbox it falls back to 'adaptive', "
                + "and to 'on' only if that is missing too. "
                + "vsync = false turns all of it off.",
                "mailbox,on,adaptive"),

            new("draw_latency_ticks", DisplayCategory, RuntimeCVarType.Double, "0",
                "How far behind the game's simulation the picture is drawn, in 1/15 s ticks. "
                + "The 360 drew half a tick behind. 0 follows the frame time: half a tick plus "
                + "one frame, so a faster display draws closer to the simulation. A fixed value "
                + "below what the frame time allows makes motion hold and step instead of "
                + "gliding; above 1.5 adds lag for nothing."),

            new("rendering_driver", DisplayCategory, RuntimeCVarType.String,
                System.OperatingSystem.IsWindows() ? "d3d12" : "vulkan",
                "The graphics API Godot draws the game with. Windows offers Direct3D 12 (required for HDR) "
                + "or Vulkan; everywhere else it is Vulkan. Takes effect at the next launch.",
                "d3d12|Direct3D 12,vulkan|Vulkan"),

            new("render_thread", DisplayCategory, RuntimeCVarType.Bool, "true",
                "Run Godot's renderer on its own thread, so the frame costs the longer of the "
                + "game update and the draw rather than their sum. Godot marks this mode "
                + "experimental; off follows Godot's default. Takes effect at the next launch."),

            new("gpu_profile", DisplayCategory, RuntimeCVarType.Bool, "false",
                "Print Godot's own per-pass GPU profile once a second to the log: shadows, "
                + "opaque, sky, transparent, SDFGI, fog and tonemap, each with its GPU and CPU "
                + "milliseconds. The engine's `--gpu-profile`; costs a little per frame while "
                + "on. Takes effect at the next launch."),

            new("perf_overlay", DisplayCategory, RuntimeCVarType.Bool, "false",
                "Show the live performance overlay from the start: frame rate, frame time, the "
                + "main thread, the render thread's CPU and GPU time, draw calls, video memory, "
                + "and Godot's per-pass GPU split when gpu_profile is on. F3 toggles it in game "
                + "either way. Costs nothing measurable; it redraws its text four times a second."),

            new("renderdoc", DisplayCategory, RuntimeCVarType.Bool, "false",
                "Arm RenderDoc's Vulkan capture layer for the game: F12 or the debug channel's "
                + "`capture` op writes a frame capture (.rdc) into the user data folder's "
                + "captures/. Needs a RenderDoc install (renderdoc_path, or renderdoccmd on PATH, "
                + "or one under ~/.local/share/refii/renderdoc). NOT FOR PLAY: the layer costs most of "
                + "the render thread even when nothing is being captured (measured 66 -> 15 fps), so "
                + "turn it on for a capture session only. Takes effect at the next launch."),

            new("renderdoc_path", DisplayCategory, RuntimeCVarType.String, "",
                "The unpacked RenderDoc directory (the one holding bin/ and lib/), when it is "
                + "not on PATH or under ~/.local/share/refii/renderdoc. Empty searches those."),

            new("gc_heaps", DisplayCategory, RuntimeCVarType.Int, "4",
                "How many threads the .NET garbage collector works with. Each collection "
                + "stops the game while it runs; with one thread that was a 25-55 ms hitch every "
                + "few seconds in town. 0 uses the single-threaded collector. Takes effect at "
                + "the next launch."),

            new("mesh_lod_pixels", DisplayCategory, RuntimeCVarType.Double, "1",
                "When a model switches to its coarser versions, in the shadow passes too. 1 "
                + "follows the 360 exactly: the game's own rule, a model's reference size "
                + "against a twentieth of the screen height per level, at any resolution. Below "
                + "1 holds the full model longer (0.5 switches at half the distance's worth of "
                + "size, twice as far away); above 1 switches sooner and cuts triangles on a "
                + "slow GPU."),

            new("prop_draw_distance_scale", DisplayCategory, RuntimeCVarType.Double, "1",
                "How far placed scenery draws, as a multiple of the distance the level data "
                + "states for each placement. 1 is the 360's own distances: undergrowth about "
                + "40 m, rocks about 100 m, trees about 1 km. 2 doubles them, at the cost of "
                + "drawing more. Trees switch to their distant model at the same multiple."),
            new("entity_draw_distance_scale", DisplayCategory, RuntimeCVarType.Double, "2",
                "How far characters and the props they carry or trade draw, as a multiple of the "
                + "distance each one's game data states. 1 is the 360's own distances: most "
                + "townsfolk 25-64 m, and their shadows stop there too. 2 doubles them. You and "
                + "your co-op partner are always drawn. Read at launch."),
            new("prop_fade_fraction", DisplayCategory, RuntimeCVarType.Double, "0.15",
                "The last share of a placement's draw distance over which it fades out rather "
                + "than popping. The 360 faded rocks over 0.03 and cut undergrowth hard; 0.15 "
                + "softens the edge at modern resolutions. 0 keeps the 360's own edges."),
            new("prop_cull_min_pixels", DisplayCategory, RuntimeCVarType.Int, "4",
                "Scenery smaller than this many pixels tall on a 1080-line display stops "
                + "drawing and casting shadows, fading out over the last stretch. A railing "
                + "post is culled past ~50 m, a two-metre rock past ~350 m. 0 draws every prop "
                + "at any distance."),

            new("render_scale_percent", DisplayCategory, RuntimeCVarType.Int, "100",
                "Renders the 3D world at this percentage of the window and scales it up, "
                + "leaving the UI and text at full resolution. Below 100 buys frame rate on a "
                + "GPU-bound machine; above 100 is supersampling."),

            new("msaa", DisplayCategory, RuntimeCVarType.Int, "0",
                "Multisample anti-aliasing on the 3D world. Costs memory bandwidth rather than "
                + "shading, so it is usually cheaper than supersampling for the same edge "
                + "quality. It smooths geometry edges only, not shimmer inside a surface.",
                "0|Off,2|2x,4|4x,8|8x"),

            new("screen_space_aa", DisplayCategory, RuntimeCVarType.String, "off",
                "Post-process anti-aliasing: smooths every edge, including shimmer MSAA cannot "
                + "reach, at a small cost. FXAA is the cheapest and the softest; SMAA keeps more "
                + "detail.",
                "off|Off,fxaa|FXAA,smaa|SMAA"),

            new("taa", DisplayCategory, RuntimeCVarType.Bool, "false",
                "Temporal anti-aliasing: accumulates jittered frames, which removes shimmer that "
                + "no other method does, at the price of softness and some ghosting in motion. "
                + "Not applied with FSR 2.2, which is temporal anti-aliasing itself."),

            new("render_scaler", DisplayCategory, RuntimeCVarType.String, "bilinear",
                "How the 3D world is scaled to the window when it renders below 100%. FSR 1.0 "
                + "sharpens each frame on its own; FSR 2.2 accumulates detail over frames and, at "
                + "Native, is anti-aliasing at full resolution. Nearest is a crisp pixel scale. "
                + "Off by default: the game reaches its frame rate at native resolution.",
                "bilinear|Bilinear,fsr|FSR 1.0,fsr2|FSR 2.2,nearest|Nearest"),

            new("fsr_quality", DisplayCategory, RuntimeCVarType.String, "custom",
                "FSR's quality mode: the resolution the 3D world renders at before FSR scales "
                + "it up, as AMD defines each mode. Custom uses render_scale_percent. Only read "
                + "when render_scaler is FSR 1.0 or FSR 2.2; FSR 1.0 at Native does nothing.",
                "custom|Custom (render_scale_percent),native|Native (100%),"
                + "ultra_quality|Ultra Quality (77%),quality|Quality (67%),"
                + "balanced|Balanced (59%),performance|Performance (50%),"
                + "ultra_performance|Ultra Performance (33%)"),

            new("fsr_sharpness", DisplayCategory, RuntimeCVarType.Double, "0.2",
                "Sharpening for FSR 1.0 and FSR 2.2: 0 is the sharpest, and sharpness halves "
                + "with each whole step up to 2. 0.2 is Godot's default."),

            new("texture_mipmap_bias", DisplayCategory, RuntimeCVarType.Double, "0",
                "Texture sharpness at a distance. Below 100% render scale the bias already "
                + "follows the scale, so an upscaled frame keeps native texture detail; this is "
                + "added to that. Negative is sharper and grainier, positive is softer; -0.75 to "
                + "0 is the useful range."),

            new("anisotropic_filtering", DisplayCategory, RuntimeCVarType.Int, "4",
                "Texture sharpness on surfaces seen at a slant — roads and floors running away "
                + "from the camera. 4x is Godot's default.",
                "0|Off,2|2x,4|4x,8|8x,16|16x"),

            new("debanding", DisplayCategory, RuntimeCVarType.Bool, "false",
                "Adds a trace of noise to hide banding in smooth gradients such as the sky, fog "
                + "and dark interiors."),

            new("hdr_output", DisplayCategory, RuntimeCVarType.Bool, "false",
                "Output HDR to a display that supports it: highlights the SDR picture clips at "
                + "white are shown up to the display's peak brightness, and everything below "
                + "white is unchanged. Requires Direct3D 12 on Windows (selected automatically). "
                + "Falls back to SDR where the display or OS has no HDR, and follows the window between screens."),

            new("hdr_reference_nits", DisplayCategory, RuntimeCVarType.Double, "0",
                "The brightness of white — the UI, and everything the SDR picture shows — in "
                + "HDR, in nits. 0 follows the OS's own SDR brightness setting. Windows only; "
                + "elsewhere the OS's value is always used."),

            new("hdr_peak_nits", DisplayCategory, RuntimeCVarType.Double, "0",
                "The brightest the display can show, in nits, which bounds the highlights. 0 "
                + "uses what the display reports; set it when that report is wrong. Windows and "
                + "macOS only."),

            new("borderless", DisplayCategory, RuntimeCVarType.Bool, "false",
                "Drops the window's title bar and border. With a window sized to the monitor "
                + "this is the usual 'borderless fullscreen', which alt-tabs faster than "
                + "exclusive fullscreen."),

            new("video_skip", VideoCategory, RuntimeCVarType.Bool, "true",
                "Let a mouse click or the A button skip a full-screen movie. The game's own "
                + "rules still apply to the movies it marks unskippable."),
            new("video_skip_after", VideoCategory, RuntimeCVarType.Double, "0.35",
                "Seconds a movie has to be on screen before a click or A can skip it, so the "
                + "press that started it does not also end it."),
            new("sun_shadow_distance", LightingCategory, RuntimeCVarType.Double, "200",
                "How far from the eye the sun and moon cast dynamic shadows, in metres, by day; "
                + "night uses half. The four cascades share this depth, so a longer reach spends "
                + "the same shadow map over more ground and draws every caster in it: 300 m "
                + "measured four times the visible geometry again in the shadow passes. 200 "
                + "keeps the shadows a player looks at and drops the ones under the horizon haze."),

            new("sun_shadow_splits", LightingCategory, RuntimeCVarType.Int, "2",
                "How many cascades the sun's shadow map is split into by day, 2 or 4. Every "
                + "cascade draws every caster in its slice again: four measured four times the "
                + "visible geometry in the shadow passes. Two keeps a sharp near cascade to 35 m "
                + "and one beyond; four is finer past that at twice the shadow cost."),

            new("sun_shadow_map", LightingCategory, RuntimeCVarType.Int, "4096",
                "The sun's shadow map size in pixels, split four ways into cascades. 4096 is "
                + "Godot's default; the shadow bias follows the texel, so 2048 trades edge "
                + "sharpness for fill on a slow GPU without striping the ground, and 8192 suits "
                + "a 4K display."),

            new("sun_shadows", LightingCategory, RuntimeCVarType.Bool, "true",
                "Let the sun and moon cast dynamic shadows. Fable 2 gates this on the day-set's "
                + "MainLightShadowed, which ships as 0 for most themes \u2014 the shadowing is "
                + "baked into the lightmaps instead. On is the remastered default; off follows "
                + "the shipped theme exactly."),
            new("tree_shadow_reach", LightingCategory, RuntimeCVarType.String, "full",
                "How far a tree casts its shadow. 'full' casts it as far as the sun's shadow "
                + "cascades reach; 'stated' stops it at the tree's own shadow distance from the "
                + "level file, as the 360 did, which leaves distant trees unshadowed. Applies "
                + "at the next launch.",
                "full,stated"),
            new("sun_light_scale", LightingCategory, RuntimeCVarType.Double, "1.35",
                "Multiplier on the sun and moon's light energy. The shipped balance keeps the "
                + "direct sun mild against the baked lighting, so people and ground barely "
                + "react to it — raising this makes characters and terrain visibly sun-lit "
                + "and deepens their shadows. 1.0 follows the shipped energy exactly."),
            new("placed_light_budget", LightingCategory, RuntimeCVarType.Int, "0",
                "How many placed lights stay visible at once, nearest to the eye first. 0 keeps "
                + "every fixture in the region lit all the time, which is the remaster's setting: "
                + "a budget makes lamps switch on and off as the camera moves. Every fixture "
                + "lit measured 13 ms of draw in Bowerstone Market with every shadow off."),
            new("placed_light_fade", LightingCategory, RuntimeCVarType.Bool, "false",
                "Whether a placed light fades out and stops drawing past its authored camera "
                + "cutoff, as the 360 did. Off keeps every fixture lit at any distance; its "
                + "range still bounds what it reaches. The fable model always fades."),
            new("placed_light_shadows", LightingCategory, RuntimeCVarType.Bool, "true",
                "Let torches, lamps and fixtures cast shadows. On: "
                + "the fixtures nearest the eye and on screen hold shadow maps, up to "
                + "placed_light_shadow_budget of them, fading in and out as the camera moves. "
                + "Off follows the shipped data, where only the few spot lights marked to cast do."),
            new("cull_back_faces", VideoCategory, RuntimeCVarType.Bool, "true",
                "Draw ordinary models from their front side only, as the original game does. "
                + "Halves the pixel work on solid shapes. Off draws every surface from both "
                + "sides. Trees and leaves are always drawn from both. Takes effect on the next "
                + "level load."),
            new("dynamic_sky_and_weather", VideoCategory, RuntimeCVarType.Bool, "false",
                "Dynamic Sky and Weather. On draws the new procedural sky — sun, moon, stars, "
                + "moving clouds — with weather that changes from visit to visit: rain, storms, "
                + "fog, overcast. The childhood is always snowing, Rookridge always raining, "
                + "and Knothole Island follows its own weather. Off keeps the original Fable 2 "
                + "sky dome and weather exactly as shipped. Lighting is identical either way."),
            new("dynamic_sky_weather", VideoCategory, RuntimeCVarType.String, "theme",
                "Weather override for the dynamic sky. 'theme' lets each region decide — the "
                + "childhood snows, Rookridge rains, Knothole Island follows its own weather, "
                + "everywhere else rolls. Any other value forces that weather everywhere.",
                "theme,clear,partlycloudy,cloudy,overcast,rain,storm,snow,fog"),
            new("gi_half_resolution", LightingCategory, RuntimeCVarType.Bool, "true",
                "Resolve the bounced light at half the screen's resolution and scale it up. The "
                + "bounce itself is unchanged; its edges where one surface meets another are a "
                + "little softer. Frees graphics-card time at high resolutions. Off resolves it "
                + "at every pixel."),
            new("placed_light_shadows_world", LightingCategory, RuntimeCVarType.Bool, "false",
                "What a torch or lamp shadows. Off: characters and the other things the game "
                + "moves, which is nearly free. On: the level itself as well, so walls and "
                + "furniture block fixture light; each shadowed fixture then redraws everything "
                + "in its reach six times a frame."),
            new("placed_light_shadow_budget", LightingCategory, RuntimeCVarType.Int, "8",
                "How many placed lights cast shadows at once. Each one redraws everything in "
                + "its reach into its own shadow map every frame, so this is the lighting dial "
                + "with the most direct cost: 4 cost about 3 fps in Bowerstone Market. 0 turns "
                + "fixture shadows off."),
            new("modern_dark_lift", LightingCategory, RuntimeCVarType.Double, "1",
                "Maximum lift for frames far darker than the original's average-luminance floor "
                + "in the modern lighting model. The original pins a frame's mean at that floor, "
                + "so a cave whose mean sits at a fortieth of it renders at 7% grey; the lift "
                + "lowers the floor only for frames more than ten times darker than it, up to "
                + "this factor, and leaves daylight and ordinary night alone. 1 follows the original."),
            new("placed_light_range", LightingCategory, RuntimeCVarType.Double, "1.5",
                "Multiplier on the reach of torch, lamp and fixture lights. The shipped torches "
                + "light 5-6 m, balanced for the original's baked glow between them; the remaster "
                + "widens the pools so a cave floor between two braziers is lit. 1.0 follows the "
                + "shipped data."),
            new("placed_light_scale", LightingCategory, RuntimeCVarType.Double, "1.5",
                "Multiplier on torch, lamp and fixture light intensity. The authored fixtures "
                + "throw modest pools — a tall lamp post barely reaches the ground — "
                + "so the remaster brightens them. 1.0 follows the shipped data exactly."),
            new("lighting_model", LightingCategory, RuntimeCVarType.String, "modern",
                "Where the world's ambient light comes from. 'modern' derives it live from "
                + "the sky dome, so every surface breathes with the visible sky — golden at "
                + "sunset, blue at night — one source for baked, probe-lit and bare objects "
                + "alike. 'fable' reads the authored day-set ambient exactly as the 360 did."),
            new("ambient_light_scale", LightingCategory, RuntimeCVarType.Double, "1.0",
                "The one fill dial. In the modern model it scales both halves of the fill: the flat "
                + "floor every surface keeps in shadow, and the sky light Godot's global illumination "
                + "spreads where the sky is seen. 1.0 is the level the theme states. In the fable model "
                + "it lifts the 360's own probe and theme ambient."),
            new("interior_ambient_scale", LightingCategory, RuntimeCVarType.Double, "1.0",
                "Scales the theme's flat ambient in levels with no sky. 1.0 is the shipped "
                + "value and the one to keep: the room's brightness comes from the exposure "
                + "gain (interior_dark_lift), which lifts the whole frame; scaling one term "
                + "breaks the authored balance between ambient and bake. Modern model only."),
            new("interior_ambient_neutral", LightingCategory, RuntimeCVarType.Double, "0.0",
                "How far the interior ambient lift is pulled toward neutral. The cave themes "
                + "state a saturated orange ambient; lifted as-is it paints every surface "
                + "orange. 0 keeps the theme's colour, 1 lifts with white."),
            new("interior_bake_scale", LightingCategory, RuntimeCVarType.Double, "1.0",
                "NO LONGER READ BY EITHER MODEL: it scaled the authored bake in the old modern model's interiors, and the modern model now emits no bake. Scales the authored vertex bakes in levels with no sky. 1.0 is the shipped "
                + "strength and the one to keep: above it, baked floors and paintings blow out "
                + "while surfaces with no bake stay black. Modern model only."),
            new("modern_tone_curve", LightingCategory, RuntimeCVarType.String, "filmic",
                "The tone curve in the modern model. 'filmic' is a curve with a toe and a "
                + "shoulder, applied where there is no sky (caves, rooms) so the interior lift "
                + "keeps its blacks; 'all' applies it outdoors too; 'fable' is the original's "
                + "curve everywhere. The original's exposure, key and floors are kept either way."),
            new("modern_depth_of_field", LightingCategory, RuntimeCVarType.Bool, "false",
                "Whether the modern model applies the original's depth of field. The "
                + "themes blur everything past their far plane, twenty metres in a cave, "
                + "which reads as a soft picture on a modern display. The fable model "
                + "always applies it."),
            new("interior_dark_lift", LightingCategory, RuntimeCVarType.Double, "16.0",
                "The cap on the exposure gain in levels with no sky: the frame is lifted by "
                + "the tone floor (0.15) over its raw mean, up to this value. 16 is the "
                + "original's own exposure clamp, and the gain lifts the whole frame so the "
                + "authored balance between ambient, bake and torches is kept. Outdoors uses "
                + "modern_dark_lift. Modern model only."),
            new("interior_fog_scale", LightingCategory, RuntimeCVarType.Double, "0.2",
                "How much of the authored fog a level with no sky keeps. The cave records "
                + "state a fog that walls off everything past ten metres; the 360 reference "
                + "shows the far walls. 1.0 is the shipped fog, 0 removes it. Modern model only."),
            new("baked_light_weight", LightingCategory, RuntimeCVarType.Double, "0.45",
                "FABLE MODEL ONLY: the modern model lights every surface from Godot's ambient and GI and emits no authored bake. One weight on every AUTHORED exterior bake — the terrain lightmap and prop "
                + "vertex colours — so adjacent surfaces scale together. Lower hands the look "
                + "to the live sun, lamps and their shadows, which is what makes their "
                + "shadows READ; 1.0 is the shipped richness exactly. In the modern model "
                + "interior lightmaps ignore this (they have no live sun to fall back on), "
                + "and the probe ambient and flat ambient floor are never scaled."),
            new("prop_probe_bake_weight", LightingCategory, RuntimeCVarType.Bool, "false",
                "FABLE MODEL ONLY: the modern model emits no probe fill to weight. Apply baked_light_weight to STATIC PROPS the artists never baked, which fall "
                + "back to probe ambient — a silver birch, a barrel, a door. The 360 scaled "
                + "neither population (verified: the executable sets no ambient gain on either "
                + "path), so dimming only the authored bakes left the unbaked props about twice "
                + "as bright as their neighbours: the blown-out white tree trunks. Characters "
                + "are never affected — they ride the probe field at full strength, and dimming "
                + "that blacks them out at night. OFF BY DEFAULT: it dims the unbaked props into "
                + "line, but they also carry the modernized AO that an authored bake is exempt "
                + "from, so they take two penalties at once and the result read as wrong. The "
                + "AO exemption has to move with the dial before this can be the default."),
            new("shader_warmup_enabled", DeveloperCategory, RuntimeCVarType.Bool, "true",
                "Compile every shader pipeline behind a preparation screen before the guest "
                + "boots. Skipping it lets pipeline compilation stall the main thread during "
                + "guest boot, which can hang the launch after any shader change."),
            new("lighting_debug_view", DeveloperCategory, RuntimeCVarType.Int, "0",
                "Isolate one lighting term on every lit surface, to attribute a bad colour to "
                + "the term carrying it. Off draws the frame; every other mode replaces it with "
                + "one term. Bake mode colours each surface by the bake it took: red flat "
                + "ambient, yellow probe, green vertex bake, blue texture lightmap, cyan "
                + "terrain. Sun shadow term is white where lit and black where shadowed; sun "
                + "NdotL is signed, 0.5 grazing. Direct light on white sums every light's "
                + "direct term over a white albedo and exists in the standard material only.",
                "0|Off,1|Bake and ambient only,2|Direct light only,3|Raw albedo,"
                + "4|Normal-map darkening,5|Bake mode,6|Sun shadow term,7|Sun NdotL (signed),"
                + "8|View-space normal,9|Direct light on white"),
            new("debug_channel", DeveloperCategory, RuntimeCVarType.Bool, "false",
                "Open the loopback debug channel at launch and publish its handshake file, "
                + "the same as launching with REFII_DEBUG_PORT=1. Off for players."),
            new("fx_additive_gain", LightingCategory, RuntimeCVarType.Double, "1.0",
                "How strongly the additive particle layer (fire, sparkles, spell cores — every "
                + "ONE/ONE effect) joins the scene before the bloom extract and the tonemap. "
                + "The original composited that layer scaled by its framebuffer brightness; "
                + "1.0 adds it as scene radiance. Raise it if fire and the quest trail read "
                + "too faint in daylight; the debug channel's `fxlayer` command changes it live."),
            new("postfx_dump_period", DeveloperCategory, RuntimeCVarType.Int, "0",
                "Print the tone-mapping histogram, the adapted bounds, the exposure and the "
                + "bloom coverage every N frames. 0 is off. This reads back from the GPU and "
                + "stalls the device, so it is a diagnostic, not something to leave on."),
            new("postfx_flicker_trap", DeveloperCategory, RuntimeCVarType.Double, "0",
                "Arm the flicker trap: keep the last 48 rendered frames on the GPU and write "
                + "them out as PNGs under user://flicker when the scene's mean luminance jumps "
                + "by more than this ratio between consecutive frames (2.5 is a good start; 0 "
                + "is off). The debug channel's `flicker` command writes the ring on demand. "
                + "Costs one quarter-resolution copy per frame and a small readback every 20 "
                + "frames; read at launch."),
        };
}

/// <summary>
/// Every runtime setting: the generated native ones and the hand-maintained Godot ones.
/// </summary>
/// <remarks>
/// Everything that reads, writes or displays settings goes through this rather than through
/// <see cref="RuntimeCVars.Definitions"/> directly, so a Godot-side setting is a real setting —
/// it gets a type, a default, a description and a place in the settings panel, and the writer
/// emits it rather than treating it as debris.
/// </remarks>
public static class RuntimeCVarSet
{
    private static IReadOnlyList<RuntimeCVarDefinition>? _all;

    public static IReadOnlyList<RuntimeCVarDefinition> All =>
        _all ??= RuntimeCVars.Definitions
            .Concat(
                // The generated list WINS on a name collision. If the native runtime ever grows a
                // cvar of the same name, its type and description are the authoritative ones and
                // a stale hand-written duplicate must not shadow them.
                RuntimeCVarsGodot.Definitions.Where(
                    godot => !RuntimeCVars.Definitions.Any(
                        native => native.Name == godot.Name)))
            .ToList();
}
