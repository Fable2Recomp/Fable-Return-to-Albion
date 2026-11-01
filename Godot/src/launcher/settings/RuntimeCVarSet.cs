using System.Collections.Generic;
using System;
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
/// The guest reads this same file — the running title's config, which the host bridge states —
/// so it logs <c>Config: unknown cvar</c> once per key for these: they are not in its registry.
///
/// Each says which title it applies to (<see cref="RuntimeCVarDefinition.OwnerTitle"/>): shared
/// code reads all but the few below that only Fable 2's own systems read.
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

    /// <summary>Discord Rich Presence: the game's name and the player's level, on their profile.</summary>
    public const string PresenceCategory = "Discord";

    /// <summary>The camera and the keyboard and mouse: how the player's hands reach the game.</summary>
    public const string ControlsCategory = "Controls";

    /// <summary>Co-op, beside the runtime's RexNet settings: the panel lists both as one group.</summary>
    public const string MultiplayerCategory = "Multiplayer";

    /// <summary>
    /// The pub games are Fable 2's (the SWF stack is <c>Titles/Fable2/pubgames</c>; Fable 3's disc
    /// has no .swf): their raster settings are read by the shared texture settings for that stack only.
    /// </summary>
    private static readonly string Fable2PubGames = Refii.Core.Engine_Title.Fable2.Folder;

    /// <summary>
    /// Local co-op is Fable 2's: split screen takes its second camera only when
    /// <c>Engine_TitleGame.Active.LocalCoop</c>, which only Fable 2 states, and its screen is
    /// <c>Titles/Fable2/game/GameUiCoopScreen.cs</c>.
    /// </summary>
    private static readonly string Fable2LocalCoop = Refii.Core.Engine_Title.Fable2.Folder;

    public static readonly IReadOnlyList<RuntimeCVarDefinition> Definitions =
        [
            new("decorate_mode_enabled", ControlsCategory, RuntimeCVarType.Bool, "false",
                "Enable free furniture placement through an owned home's Redecorate option. Furniture layouts are kept with the character's save.",
                // Read only by Titles/Fable2/game/GameDecorate.cs (Engine_DecorateCatalog.Setting).
                OwnerTitle: Refii.Core.Engine_Title.Fable2.Folder),
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
                "size it was authored at, which is softer, slower and uses more memory.",
                OwnerTitle: Fable2PubGames),
            new("pubgame_raster_scale_percent", Category, RuntimeCVarType.Int, "100",
                "How sharply the pub games (Keystone and friends) draw their Flash artwork. " +
                "100 is the size it was authored at; 0 follows the window. Higher is sharper " +
                "but slower to load — Keystone measures 4.6 s and 109 MB at 100, and 11.9 s " +
                "and 348 MB at 250, because the artwork is drawn on the CPU. Ignored unless " +
                "pubgame_vector_shapes is off.",
                OwnerTitle: Fable2PubGames),
            new("developer_menu_enabled", DeveloperCategory, RuntimeCVarType.Bool, "false",
                "Show the Refii debug overlay in game \u2014 guest state, the save loader and "
                + "the Lua console. Off is the shipping default; the overlay is a development "
                + "fixture and draws over the game's own UI.",
                // Read only by Titles/Fable2/game/GameUi.cs.
                OwnerTitle: Refii.Core.Engine_Title.Fable2.Folder),
            new("display_aspect", DisplayCategory, RuntimeCVarType.String, "16:9",
                "The shape of the window. Picking one narrows the resolution list below to the "
                + "sizes that match it, the way a modern game's video options do.",
                "16:9,16:10,4:3,21:9,32:9"),

            new("display_resolution", DisplayCategory, RuntimeCVarType.String, "1280x720",
                "The window size, chosen from the sizes that fit the aspect ratio above. This "
                + "replaces typing a width and a height by hand."),

            ..RuntimeCVarsAndroid.GraphicsPreset,

            new("max_fps", DisplayCategory, RuntimeCVarType.Int, "0",
                "Frame rate cap. 0 is uncapped, which with vsync off will run the GPU as hard "
                + "as it can. The SIMULATION is not affected: the guest advances on its own "
                + "fixed step and only the drawing rate changes."),

            new("vsync_mode", DisplayCategory, RuntimeCVarType.String, "mailbox",
                "How frames meet the display. 'mailbox' shows the newest finished frame at each "
                + "refresh with no tearing and no cap at a fraction of the refresh rate; 'on' "
                + "waits for every refresh, so a frame that misses one shows at half the rate; "
                + "'adaptive' waits when the frame is fast and tears when it is slow; 'off' "
                + "draws as fast as it can and may tear. Where the driver has no mailbox it falls "
                + "back to 'adaptive', and to 'on' only if that is missing too.",
                "mailbox|Mailbox,on|On,adaptive|Adaptive,off|Off"),

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

            ..RuntimeCVarsAndroid.RenderThread,

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
            new("tree_far_reach", DisplayCategory, RuntimeCVarType.Double, "0",
                "The farthest any tree draws, in metres, fading out over the last eighth. 0 keeps "
                + "the level's own reach (about 1 km), where the 360 drew a flat card a tree and "
                + "this draws the tree's distant model. Changes live."),
            new("tree_lod_bias", DisplayCategory, RuntimeCVarType.Double, "1",
                "How soon the distant trees take their thinned levels, as Godot's level bias on "
                + "each tree's far model: 0.5 switches each level at half the distance "
                + "mesh_lod_pixels gives the rest of the scenery, 1 at the same. Only leaf cards "
                + "are dropped; the trees near the camera keep the shared rule. Changes live."),
            new("terrain_lod_bias", DisplayCategory, RuntimeCVarType.Double, "1",
                "How soon the ground takes its coarser levels (terrain_lod), as Godot's level bias "
                + "on every ground chunk and distant landscape: 0.5 switches each level at half the "
                + "distance, 1 as mesh_lod_pixels sets for everything else. Seams never open: the "
                + "chunk borders are kept. Changes live."),
            new("foliage_cell_metres", DisplayCategory, RuntimeCVarType.Double, "0",
                "The size of the cells ground foliage (grass, nettles, bracken) is drawn in, in "
                + "metres. A cell is cut whole once its nearest plants are past their distance; "
                + "smaller cells drop the far plants' geometry sooner with the same picture, at the "
                + "cost of more draws. 0 keeps the 32 m of everything else. Applies to the next "
                + "region loaded."),
            new("prop_cull_min_pixels", DisplayCategory, RuntimeCVarType.Int, "4",
                "Scenery smaller than this many pixels tall on a 1080-line display stops "
                + "drawing and casting shadows, fading out over the last stretch. A railing "
                + "post is culled past ~50 m, a two-metre rock past ~350 m. 0 draws every prop "
                + "at any distance."),
            new("prop_cull_distance_scale", DisplayCategory, RuntimeCVarType.Double, "1",
                "Scale factor for distance culling of non-building props and dressing by size "
                + "(<0.5m: 45m, <1m: 60m, <1.5m: 90m, <2.5m: 120m, never past the range the game states). "
                + "Buildings and large objects >=2.5m are excluded. 1 is default scale; higher values push "
                + "culling further away; 0 disables size culling."),

            new("occlusion_culling", DisplayCategory, RuntimeCVarType.Bool, "false",
                "Experimental: skip drawing what the town's buildings hide, using each building's "
                + "own solid walls as the occluder. Saves geometry in a dense town at the cost of "
                + "some CPU on worker threads; windows and cutouts never hide anything. Read at launch."),

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

            ..RuntimeCVarsAndroid.Tuning,

            new("entity_skip_index", DisplayCategory, RuntimeCVarType.Bool, "false",
                "CPU: the entity walk answers the props, markers and still scenery that have not "
                + "changed from what it decided for them the walk before, instead of looking each "
                + "one up again on every guest frame. The same things are drawn either way; each "
                + "is still re-checked twice a second. Live through the debug channel."),

            new("entity_still_reconcile_scale", DisplayCategory, RuntimeCVarType.Int, "1",
                "CPU, with entity_skip_index: still scenery the entity walk has not seen change is "
                + "re-checked against the game this many times less often (1 to 8). Moving and "
                + "animated things are unaffected; a still prop's change the game does not announce "
                + "(its lighting after a time-of-day change, a part swapped) shows up to that many "
                + "times later. Live."),

            ..RuntimeCVarsAndroid.LohCompaction,

            new("loh_compaction_defer_seconds", DisplayCategory, RuntimeCVarType.Int, "0",
                "Memory: the compaction of the app's large memory blocks after a load (always on the "
                + "desktop, android_loh_compaction on Android) waits instead of holding the loading "
                + "screen: it runs once the pause menu is open, at the next loading screen, or after "
                + "this many seconds of play at the latest, a pause of a second or two then. 0 runs "
                + "it at the end of the loading screen."),

            new("hero_texture_sources_mb", DisplayCategory, RuntimeCVarType.Int, "0",
                "Memory: how many MB of decoded textures are kept for building characters' layered "
                + "skins (tattoos, dirt, dyes) faster the next time, 0 for no limit. Past it they "
                + "are dropped and decoded again when needed, in the background. Applies at the "
                + "next launch."),

            new("anim_collapse_constant_tracks", DisplayCategory, RuntimeCVarType.Bool, "false",
                "Memory: an animation track whose keys are all the same (a bone that does not move "
                + "in that clip) is kept as one key instead of one per frame, which shrinks the "
                + "decoded clips the characters share. The same poses either way. Takes effect at "
                + "the next launch."),

            new("fx_cpu_fastpaths", DisplayCategory, RuntimeCVarType.Bool, "false",
                "Effects: the main thread's shortcuts that draw the same picture: a particle's fade "
                + "tested before its size and colour, no back-to-front sort for additive batches, "
                + "the batches' bounds and step-alpha writes trimmed. The debug channel's render op "
                + "(fxFastPaths) flips it live."),

            ..RuntimeCVarsAndroid.EffectsAndCanvas,

            new("fx_no_depth_reads", DisplayCategory, RuntimeCVarType.Bool, "false",
                "Effects without a soft edge are drawn by shaders that do not declare the scene's "
                + "depth, and the sun and moon glares are left out of a frame they cannot reach, so "
                + "a phone's GPU can draw the world and its see-through surfaces in one pass with no "
                + "depth copy. The same picture. The debug channel's render op (fxNoDepthReads) flips it live."),

            new("sky_radiance_lean", DisplayCategory, RuntimeCVarType.Bool, "false",
                "Sky: the sky's reflection map, which nothing samples under the game's own lighting "
                + "on a phone, is kept at its smallest size, and the dome's sun is moved 15 times a "
                + "second instead of every frame, so the map is refreshed less often. Where something "
                + "does sample it the map keeps its size. The debug channel's render op "
                + "(skyRadianceLean) flips it live."),

            new("far_rig_pose_hz", DisplayCategory, RuntimeCVarType.Double, "0",
                "CPU: how many times a second a character farther than 20 m from the camera is "
                + "posed (4 to 30). 0 keeps the game's own rule, half rate, which under 30 FPS is "
                + "every frame. 7.5 halves those characters' animation work on a phone running at "
                + "15 FPS; they move a little less smoothly. Live through the debug channel."),

            new("entity_create_budget_ms", DisplayCategory, RuntimeCVarType.Double, "0",
                "CPU: how many milliseconds a frame may spend creating characters and props as they "
                + "come into view, at least one a frame. 0 keeps the older rule, at least 8 a frame "
                + "whatever they cost, which can stall one frame for 100-200 ms when a crowd appears. "
                + "5 spreads that over a few frames; they appear a little later. Live with cvar-set."),

            new("far_rig_pose_spread", DisplayCategory, RuntimeCVarType.Bool, "true",
                "With far_rig_pose_hz set: each far character posed on its own beat, so every "
                + "frame does the same share of the work. Off poses them all on the same frames, "
                + "which keeps a lamp's shadow of far characters redrawing at that rate only."),

            new("anisotropic_filtering", DisplayCategory, RuntimeCVarType.Int, "4",
                "Texture sharpness on surfaces seen at a slant — roads and floors running away "
                + "from the camera. 4x is Godot's default.",
                "0|Off,2|2x,4|4x,8|8x,16|16x"),

            new("tree_card_lod", DisplayCategory, RuntimeCVarType.Bool, "false",
                "Thins the distant trees: the far tree models, which ship no coarser level, get "
                + "two that keep trunk and boughs and drop half, then three quarters, of the leaf "
                + "cards, switching in with distance as mesh_lod_pixels sets. Distant crowns read "
                + "slightly sparser. On in the phone presets, off on the desktop. Applies at the next launch."),

            new("tree_card_far_levels", DisplayCategory, RuntimeCVarType.Bool, "false",
                "With tree_card_lod, two more levels for the most distant trees: an eighth, then a "
                + "sixteenth, of their leaf cards, trunk and boughs kept. Applies at the next launch."),
            new("terrain_normal_maps", DisplayCategory, RuntimeCVarType.Bool, "true",
                "The ground's bump: each painted layer's normal map, with the cavity and parallax "
                + "derived from it. Off draws the ground from its colours and bake alone and frees "
                + "their video memory (about 275 MB in Bowerstone on a phone without BC textures). "
                + "Applies at the next level load."),

            new("debanding", DisplayCategory, RuntimeCVarType.Bool, "false",
                "Adds a trace of noise to hide banding in smooth gradients such as the sky, fog "
                + "and dark interiors."),

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
            new("lighting_preset", LightingCategory, RuntimeCVarType.String, "balanced",
                "Overall lighting and shadow fidelity preset: Performance, Balanced, Quality, or Custom. "
                + "Controls light and shadow budgets and reach.",
                "performance|Performance,balanced|Balanced,quality|Quality,custom|Custom"),

            new("hdr_output", LightingCategory, RuntimeCVarType.Bool, "false",
                "Output HDR to a display that supports it: highlights the SDR picture clips at "
                + "white are shown up to the display's peak brightness, and everything below "
                + "white is unchanged. Requires Direct3D 12 on Windows (selected automatically). "
                + "Falls back to SDR where the display or OS has no HDR, and follows the window between screens."),

            new("hdr_reference_nits", LightingCategory, RuntimeCVarType.Double, "200",
                "The brightness of white — the UI, and everything the SDR picture shows — in "
                + "HDR, in nits. Recommended: 200 nits. 0 follows the OS's own SDR brightness setting."),

            new("hdr_peak_nits", LightingCategory, RuntimeCVarType.Double, "1000",
                "The brightest the display can show, in nits, which bounds the highlights. 0 "
                + "uses what the display reports; set it when that report is wrong."),

            new("placed_shadow_range", LightingCategory, RuntimeCVarType.Double, "30.0",
                "Maximum distance from the eye in metres beyond which placed light shadows fade and cull (Performance: 10m, Balanced: 30m, Quality: 70m)."),

            new("sun_shadow_distance", LightingCategory, RuntimeCVarType.Double, "200",
                "How far from the eye the sun and moon cast dynamic shadows, in metres, by day; "
                + "night uses half. The four cascades share this depth, so a longer reach spends "
                + "the same shadow map over more ground and draws every caster in it: 300 m "
                + "measured four times the visible geometry again in the shadow passes. 200 "
                + "keeps the shadows a player looks at and drops the ones under the horizon haze. "
                + "0 turns the sun's dynamic shadow off, even where the theme casts it."),

            new("sun_shadow_reach_scale", LightingCategory, RuntimeCVarType.Double, "1",
                "Multiplier on size-tiered sun shadow reach distances. Lower values cull small and medium prop shadows earlier while keeping large landscape shadows intact."),

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
            new("placed_light_limit", LightingCategory, RuntimeCVarType.Int, "0",
                "The most placed lights (lamps, torches, fixtures) lit at once: the nearest that "
                + "reach the view, the others fading out until they are among the nearest again. "
                + "0 lights every one within placed_light_range. Each lit light is shaded on "
                + "everything it reaches, so a town at night costs a phone most in its far lamps. "
                + "Not the retired placed_light_budget: range culling still applies first."),
            ..RuntimeCVarsAndroid.StableLightCounts,
            new("placed_light_fade", LightingCategory, RuntimeCVarType.Bool, "true",
                "Whether a placed light fades out and stops drawing past its authored camera "
                + "cutoff, as the 360 did. Off keeps every fixture lit at any distance; its "
                + "range still bounds what it reaches. The fable model always fades."),
            new("placed_light_shadows", LightingCategory, RuntimeCVarType.Bool, "true",
                "Let torches, lamps and fixtures cast dynamic shadows within placed_shadow_range. "
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
                + "sky dome and weather exactly as shipped. Lighting Override optionally changes sunlight."),
            new("dynamic_sky_lighting_override", VideoCategory, RuntimeCVarType.Bool, "false",
                "Use the dynamic sky's sun as the scene's directional light, with elevation-dependent colour and intensity. "
                + "Off preserves level lighting, including disabling cloud-cast shadows. "
                + "Ambient lighting and local lights retain the level's settings."),
            new("dynamic_sky_cloud_shadows", VideoCategory, RuntimeCVarType.Bool, "false",
                "Clouds cast moving shadows from the sun in the modern renderer; requires "
                + "Dynamic Sky and Weather and Lighting Override. Classic surfaces receive the same projected cloud mask."),
            new("dynamic_sky_volumetric_clouds", VideoCategory, RuntimeCVarType.Bool, "false",
                "Cloud rendering for Dynamic Sky and Weather. Off uses the 2D procedural layers. "
                + "On raymarches a 3D cloud volume with self-shadowing. Applies immediately; "
                + "requires Dynamic Sky and Weather and uses more GPU time."),
            new("dynamic_sky_weather", VideoCategory, RuntimeCVarType.String, "theme",
                "Weather override for the dynamic sky. 'theme' lets each region decide — the "
                + "childhood snows, Rookridge rains, Knothole Island follows its own weather, "
                + "everywhere else rolls. Any other value forces that weather everywhere.",
                "theme,clear,partlycloudy,cloudy,overcast,rain,storm,snow,fog,lightrain,lightsnow,blizzard"),
            new("dynamic_sky_time_of_day", VideoCategory, RuntimeCVarType.String, "auto",
                "Time of day override for Dynamic Sky and Weather. Auto follows the game clock; "
                + "an hour freezes presentation time for sky, sun, fog and scene lighting. Game simulation time is unchanged.",
                "auto,0,1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16,17,18,19,20,21,22,23"),
            new("dynamic_sky_aurora", VideoCategory, RuntimeCVarType.String, "auto",
                "Aurora: Auto gives each game night a 10% chance. On forces it at night; Off hides it. "
                + "Daylight fades it out and clouds obscure it. Requires Dynamic Sky and Weather.", "auto,on,off"),
            new("dynamic_sky_surface_weather", VideoCategory, RuntimeCVarType.Bool, "false",
                "Surface Weather: rain wets exposed terrain and meshes and forms puddles on flat areas; "
                + "snow builds up on upward-facing surfaces and melts into wetness. Off restores original materials immediately. "
                + "Requires Dynamic Sky and Weather; supports modern materials and classic world-surface shader families."),
            new("gi_half_resolution", LightingCategory, RuntimeCVarType.Bool, "true",
                "Resolve the bounced light at half the screen's resolution and scale it up. The "
                + "bounce itself is unchanged; its edges where one surface meets another are a "
                + "little softer. Frees graphics-card time at high resolutions. Off resolves it "
                + "at every pixel."),
            new("modern_dark_lift", LightingCategory, RuntimeCVarType.Double, "1",
                "Maximum lift for frames far darker than the original's average-luminance floor "
                + "in the modern lighting model. The original pins a frame's mean at that floor, "
                + "so a cave whose mean sits at a fortieth of it renders at 7% grey; the lift "
                + "lowers the floor only for frames more than ten times darker than it, up to "
                + "this factor, and leaves daylight and ordinary night alone. 1 follows the original."),
            new("placed_light_range", LightingCategory, RuntimeCVarType.Double, "65.0",
                "Maximum distance from the eye in metres beyond which placed fixture lights fade and cull (Performance: 45m, Balanced: 65m, Quality: 85m)."),
            new("placed_light_scale", LightingCategory, RuntimeCVarType.Double, "1.0",
                "Multiplier on torch, lamp and fixture light intensity. 1.0 is the vanilla "
                + "baseline following the shipped data; higher values brighten fixtures."),
            new("placed_light_reach_scale", LightingCategory, RuntimeCVarType.Double, "1.0",
                "Multiplier on torch, lamp and fixture radius (reach). 1.0 is the "
                + "vanilla baseline using the shipped radius; higher values expand reach."),
            new("building_interior_ambient_scale", LightingCategory, RuntimeCVarType.Double, "1.0",
                "Multiplier on ambient fill inside town buildings in outdoor levels "
                + "(taverns, shops, houses). 1.0 is the vanilla baseline. Caves and sky-less "
                + "levels are unaffected and use interior_ambient_scale instead."),
            new("building_interior_light_scale", LightingCategory, RuntimeCVarType.Double, "1.0",
                "Multiplier on placed light intensity inside town buildings in outdoor levels. "
                + "1.0 is the vanilla baseline. Caves are unaffected."),
            new("building_interior_reach_scale", LightingCategory, RuntimeCVarType.Double, "1.0",
                "Multiplier on placed light radius (reach) inside town buildings in outdoor "
                + "levels. 1.0 preserves the authored room reach without over-overlapping. "
                + "Caves are unaffected."),
            new("static_shadow_resolution", LightingCategory, RuntimeCVarType.Int, "2048",
                "Atlas resolution for pre-baked static fixture shadow maps (2048, 4096, 8192). "
                + "Automatically selects the optimal lights-per-slice grid for each preset (36 at 2K, 64 at 4K/8K). "
                + "Changing this triggers a re-bake during the startup warmup screen.",
                "2048|2048 (Default),4096|4096 (High),8192|8192 (Ultra)"),
            new("lighting_model", LightingCategory, RuntimeCVarType.String, "modern",
                "How the world is lit. 'modern' is the remaster: Godot's lighting, with the "
                + "ambient derived live from the sky dome blended with the region's authored "
                + "palette so every surface breathes with the visible sky. 'classic' is the "
                + "original: the game's own shaders, translated and drawn with the constants the "
                + "game states each frame — built by Compile Shaders on the Reinstall tab, or "
                + "ticked during install — and, where a shader has not been built, the authored "
                + "day-set ambient exactly as the 360 read it.",
                "modern|Modern,classic|Classic"),
            new("bloom_scale", LightingCategory, RuntimeCVarType.Double, "0.25",
                "Multiplier on the scene's bloom in the modern compositor. 0.25, the default, "
                + "balances scene bloom with volumetric fog scattering; 0.0 disables scene bloom; higher values "
                + "increase the dreamy glow. The sun, the moon and lamp halos glow regardless."),
            new("halo_distance_fade", LightingCategory, RuntimeCVarType.Bool, "true",
                "Gradually attenuate the bloom halos of distant point light fixtures (street lamps, "
                + "market stalls, torches) so they do not produce oversized screen halos at long distance. "
                + "Sun and moon glare remain unaffected. Default is on."),
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
            new("modern_tone_curve", LightingCategory, RuntimeCVarType.String, "fable",
                "The tone curve in the modern model. 'fable' (the default) uses the original's "
                + "Extended Reinhard + smoothed CDF local contrast + HDR/SDR highlight shoulder "
                + "+ shadow toe everywhere; 'filmic' applies an ACES curve where there is no sky "
                + "(caves, rooms); 'all' applies the ACES curve outdoors too. The original's "
                + "exposure, key and floors are kept either way."),
            new("modern_depth_of_field", LightingCategory, RuntimeCVarType.Bool, "false",
                "Whether the modern model applies the original's depth of field. The "
                + "themes blur everything past their far plane, twenty metres in a cave, "
                + "which reads as a soft picture on a modern display. The fable model "
                + "always applies it."),
            new("interior_dark_lift", LightingCategory, RuntimeCVarType.Double, "1.0",
                "Normalized multiplier (1.0 = calibrated baseline for the active tone curve) "
                + "on the exposure floor lift in sky-less cave levels; outdoors uses modern_dark_lift. Modern model only."),
            new("interior_fog_scale", LightingCategory, RuntimeCVarType.Double, "1.0",
                "Multiplier on authored fog optical depth in sky-less cave and interior levels. "
                + "1.0 matches the base game's authored fog curve, 0 removes it, values above 1.0 "
                + "thicken the fog exponentially. Modern model only."),
            new("outdoor_fog_scale", LightingCategory, RuntimeCVarType.Double, "1.0",
                "Multiplier on authored fog optical depth in outdoor sky-lit levels. "
                + "1.0 matches the base game's authored fog curve, 0 removes it, values above 1.0 "
                + "thicken the fog exponentially. Modern model only."),
            new("volumetric_shaft_scale", LightingCategory, RuntimeCVarType.Double, "1.0",
                "Multiplier on volumetric light-shaft in-scattering radiance across placed and dynamic lights (not celestial sun/moon rays). "
                + "1.0 is calibrated baseline, 0 disables placed light shafts, values above 1.0 intensify shafts without changing atmosphere fog density. Modern model only."),
            new("volumetric_fog", LightingCategory, RuntimeCVarType.Bool, "true",
                "Whether the modern model uses Godot's 3D Volumetric Fog in the near-to-mid field "
                + "so dynamic lights, spot shadows, and sun cascades scatter through the fog and "
                + "produce volumetric light shafts. Modern model only."),
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
            ..RuntimeCVarsAndroid.GcStress,
            new("hitch_log_ms", DeveloperCategory, RuntimeCVarType.Int, "200",
                "Testing: a frame slower than this many milliseconds is written to the log with "
                + "what moved during it and what it made for the first time (effect textures, "
                + "entity nodes, clip decodes). Takes effect at the next launch."),
            new("debug_channel", DeveloperCategory, RuntimeCVarType.Bool, "false",
                "Open the loopback debug channel at launch and publish its handshake file, "
                + "the same as launching with REFII_DEBUG_PORT=1. Off for players."),
            new("tracy", DeveloperCategory, RuntimeCVarType.Bool, "false",
                "The game's own Tracy profiler: captures are started, read and saved in the dev "
                + "menu (F6). It records the game and its threads only, inside the game: no "
                + "network, no other programs. Off, Tracy never starts. Takes effect at the next "
                + "launch."),
            new("tracy_advanced", DeveloperCategory, RuntimeCVarType.Bool, "false",
                "Adds CPU sampling with call stacks and context switches for the game's threads "
                + "to Tracy captures (needs tracy). On Windows that data needs the game to run as "
                + "Administrator, so Play asks first. On Linux sampling works as is; context "
                + "switches need kernel.perf_event_paranoid at -1. Takes effect at the next launch."),
            new("fx_additive_gain", LightingCategory, RuntimeCVarType.Double, "1.0",
                "How strongly the additive particle layer (fire, sparkles, spell cores — every "
                + "ONE/ONE effect) joins the scene before the bloom extract and the tonemap. "
                + "The original composited that layer scaled by its framebuffer brightness; "
                + "1.0 adds it as scene radiance. Raise it if fire and the quest trail read "
                + "too faint in daylight; the debug channel's `fxlayer` command changes it live."),
            new("fx_weapon_augment_always_active", Category, RuntimeCVarType.Bool, "true",
                "Keep weapon augmentation particle effects (sparkles, elemental glows) active and playing continuously "
                + "while moving (walking and running) rather than stopping like the original 2008 release. Default is on.",
                OwnerTitle: Refii.Core.Engine_Title.Fable2.Folder),
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

            new("camera_invert_y", ControlsCategory, RuntimeCVarType.Bool, "false",
                "Invert the camera's up and down, as the console's own profile setting: a new " +
                "game starts from it, and the game's Controls page overrides it per save."),
            new("camera_invert_x", ControlsCategory, RuntimeCVarType.Bool, "false",
                "Invert the camera's left and right, on the stick and the mouse. The game has no " +
                "setting of its own for this."),
            new("controller_vibration", ControlsCategory, RuntimeCVarType.Bool, "true",
                "Rumble the controller as the console did: hits taken and landed, blocks, shots, " +
                "spells, landings and the near-death heartbeat. Played by touch on Android, the " +
                "phone vibrates instead. The game's own Controls page has a Controller Vibration " +
                "switch too; both must be on."),
            new("mouse_sensitivity", ControlsCategory, RuntimeCVarType.Double, "1.0",
                "How far the camera turns for a given mouse movement. 1.0 is the default; 2.0 " +
                "turns twice as far."),
            new("mouse_input", ControlsCategory, RuntimeCVarType.Bool, "true",
                "Allow the mouse to control the camera, menus and actions. Off completely disables " +
                "mouse look, clicks and pointer navigation, keeping the cursor free and input on the " +
                "controller or keyboard."),
            new("mouse_lock", ControlsCategory, RuntimeCVarType.Bool, "true",
                "Lock the mouse cursor inside the game window during play. Off keeps the cursor visible " +
                "and free to move outside the window (convenient for multi-monitor setups or while developing)."),
            new(Refii.Game.GameSplitView.SwitchKey, MultiplayerCategory, RuntimeCVarType.Bool, "true",
                "Local co-op on split screen: each player gets their own camera and their own half "
                + "of the screen, HUD and menus. Off shares one screen and one camera, as the "
                + "original game does. Read at launch.",
                OwnerTitle: Fable2LocalCoop),
            new(Refii.Game.GameSplitView.OrientationKey, MultiplayerCategory, RuntimeCVarType.String, "vertical",
                "How split screen divides the window. Vertical puts player one on the left and "
                + "player two on the right; horizontal puts player one on top and player two "
                + "below. Read at launch.",
                "vertical|Vertical (side by side),horizontal|Horizontal (top and bottom)",
                OwnerTitle: Fable2LocalCoop),
            new("discord_presence", PresenceCategory, RuntimeCVarType.Bool, "true",
                "Show the game you are playing and the level you are in on your "
                + "Discord profile while Discord is running."),
            ..KeyBindings(),
        ];

    /// <summary>
    /// One setting per keyboard and mouse action, from the game's own table so the defaults live
    /// in one place: a comma list of key names, or MouseLeft / MouseRight / MouseMiddle /
    /// Mouse4 / Mouse5.
    /// </summary>
    private static IEnumerable<RuntimeCVarDefinition> KeyBindings() =>
        Refii.Game.GameInput.KeyActions.Select(action => new RuntimeCVarDefinition(
            "key_" + action.Name, ControlsCategory, RuntimeCVarType.String, action.Defaults,
            action.Description + " Default: " + action.Defaults + "."));

    /// <summary>The group a mod's rebindable actions sit under: the Controls group, titled by the mod.</summary>
    public static string ModControlsCategory(string title) => ControlsCategory + ": " + title;

    /// <summary>
    /// THE MODS' ACTIONS, read from every enabled mod's input.json when the panel loads
    /// (<see cref="RuntimeCVarSet.Refresh"/>): the same <c>key_&lt;mod&gt;.&lt;action&gt;</c> /
    /// <c>pad_&lt;mod&gt;.&lt;action&gt;</c> keys the game's Options › Controls page writes, one group
    /// per mod under its title. THE DEFAULT IS EMPTY, never the file's binding: the launcher writes
    /// every setting it offers, and an empty value is what the game reads as "the mod's own", so a
    /// save of the panel pins nothing and a later change to the mod's file still reaches the
    /// player — the game's page shows the same row as Default. <c>none</c> unbinds.
    /// </summary>
    public static IEnumerable<RuntimeCVarDefinition> ModBindings()
    {
        var titles = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Refii.Mods.ModInstall install in Refii.Mods.ModWorkspace.DiscoverModInstalls(Refii.Assets.Engine_GameAssets.ModsRoot))
        {
            titles[install.Name] = install.Metadata.Title.Length != 0 ? install.Metadata.Title : install.Name;
        }

        return ModBindings(Refii.Mods.ModInput.ReadDeclared(), mod => titles.TryGetValue(mod, out string? title) ? title : mod);
    }

    /// <summary>The definitions for <paramref name="actions"/>: a key row where keys are declared, a pad row where buttons or triggers are.</summary>
    public static IEnumerable<RuntimeCVarDefinition> ModBindings(
        IEnumerable<Refii.Mods.ModInput.Action> actions, Func<string, string> title)
    {
        foreach (Refii.Mods.ModInput.Action action in actions)
        {
            string category = ModControlsCategory(title(action.Mod));
            if (action.Keys.Count != 0)
            {
                string keys = string.Join(",", action.Keys.Select(Refii.Mods.ModInputUi.KeyName));
                yield return new RuntimeCVarDefinition(
                    Refii.Mods.ModInput.KeySettingKey(action), category, RuntimeCVarType.String, string.Empty,
                    action.Label + " on the keyboard: a comma list of key names (F, Ctrl, Space, F5, BracketLeft ...); " +
                    "empty keeps the mod's own (" + keys + "), none unbinds. The game's Options > Controls page edits the same setting.");
            }

            if (action.Buttons.Count != 0 || action.Triggers.Count != 0)
            {
                yield return new RuntimeCVarDefinition(
                    Refii.Mods.ModInput.PadSettingKey(action), category, RuntimeCVarType.String, string.Empty,
                    action.Label + " on the pad: a comma list of A B X Y LB RB LT RT L3 R3 Start Back DpadUp DpadDown DpadLeft DpadRight " +
                    "or JoyButton names; empty keeps the mod's own (" + Refii.Mods.ModInput.DeclaredPadText(action) + "), none unbinds. " +
                    "The game's Options > Controls page edits the same setting.");
            }
        }
    }
}

/// <summary>
/// Every runtime setting: the generated native ones and the hand-maintained Godot ones.
/// </summary>
/// <remarks>
/// Everything that reads, writes or displays settings goes through this rather than through
/// <see cref="RuntimeCVars.Definitions"/> directly, so a Godot-side setting is a real setting —
/// it gets a type, a default, a description and a place in the settings panel, and the writer
/// emits it rather than treating it as debris.
///
/// <see cref="All"/> is every title's; <see cref="Active"/> is what the active title's screen
/// lists and its config file holds. A setting of another title is neither shown nor written.
/// </remarks>
public static class RuntimeCVarSet
{
    private static IReadOnlyList<RuntimeCVarDefinition>? _all;

    /// <summary>Every title's settings: the catalog a key is looked up in.</summary>
    public static IReadOnlyList<RuntimeCVarDefinition> All => _all ??= Build();

    /// <summary>The settings <paramref name="title"/> reads, in <see cref="All"/>'s order.</summary>
    public static IReadOnlyList<RuntimeCVarDefinition> For(Refii.Core.Engine_Title title) =>
        All.Where(definition => definition.AppliesTo(title)).ToList();

    /// <summary>The active title's settings (<see cref="Refii.Core.Engine_Title.Active"/>).</summary>
    public static IReadOnlyList<RuntimeCVarDefinition> Active => For(Refii.Core.Engine_Title.Active);

    /// <summary>
    /// Reads the list again: the mods' Controls rows (<see cref="RuntimeCVarsGodot.ModBindings()"/>)
    /// follow the enabled mods' input.json, so the settings panel refreshes them whenever it
    /// reloads (<c>RuntimeSettingsModel.Reload</c>).
    /// </summary>
    public static void Refresh() => _all = Build();

    private static IReadOnlyList<RuntimeCVarDefinition> Build()
    {
        var all = RuntimeCVars.Definitions
            .Concat(
                // The generated list WINS on a name collision. If the native runtime ever grows a
                // cvar of the same name, its type and description are the authoritative ones and
                // a stale hand-written duplicate must not shadow them.
                RuntimeCVarsGodot.Definitions.Where(
                    godot => !RuntimeCVars.Definitions.Any(
                        native => native.Name == godot.Name)))
            .ToList();
        try
        {
            all.AddRange(RuntimeCVarsGodot.ModBindings().Where(mod => all.All(known => known.Name != mod.Name)));
        }
        catch (Exception exception)
        {
            // No mods root, or a file that will not read: the shipped rows stand; the game's own
            // page reads the mods directly.
            Godot.GD.PushWarning("[settings] the mods' Controls rows could not be read: " + exception.Message);
        }

        return all;
    }
}
