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
/// The native runtime will log <c>Config: unknown cvar</c> once per key for these, because they
/// are not in its registry. That does not happen in the Godot-hosted game — embedded, the guest
/// looks for refii.toml beside the HOST binary, finds none, and never loads it — so the noise is
/// limited to standalone native runs.
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

            new("render_scale_percent", DisplayCategory, RuntimeCVarType.Int, "100",
                "Renders the 3D world at this percentage of the window and scales it up, "
                + "leaving the UI and text at full resolution. Below 100 buys frame rate on a "
                + "GPU-bound machine; above 100 is supersampling."),

            new("msaa", DisplayCategory, RuntimeCVarType.Int, "0",
                "Multisample anti-aliasing on the 3D world: 0 off, or 2, 4, 8 samples. Costs "
                + "memory bandwidth rather than shading, so it is usually cheaper than "
                + "supersampling for the same edge quality."),

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
            new("sun_shadows", LightingCategory, RuntimeCVarType.Bool, "true",
                "Let the sun and moon cast dynamic shadows. Fable 2 gates this on the day-set's "
                + "MainLightShadowed, which ships as 0 for most themes \u2014 the shadowing is "
                + "baked into the lightmaps instead. On is the remastered default; off follows "
                + "the shipped theme exactly."),
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
            new("placed_light_shadows", LightingCategory, RuntimeCVarType.Bool, "false",
                "Developer lever. Off: no torch, lamp or fixture casts a shadow, as on the "
                + "360, so every lamp reads the same from every angle. On: only the few "
                + "nearest fixtures cast, which is inconsistent by construction, and all of "
                + "them casting halved the frame rate in Bowerstone Market."),
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
                "Lifts the probe-field and theme ambient — the light every UNBAKED object "
                + "stands in (rocks, barrels, doors, characters; the artists left them "
                + "movable, so they carry no painted bake). The sky tether now carries that "
                + "population, so the default follows the shipped level; raise it only if "
                + "movables still read dark beside baked surfaces."),
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
                "Scales the authored vertex bakes in levels with no sky. 1.0 is the shipped "
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
                "One weight on every AUTHORED exterior bake — the terrain lightmap and prop "
                + "vertex colours — so adjacent surfaces scale together. Lower hands the look "
                + "to the live sun, lamps and their shadows, which is what makes their "
                + "shadows READ; 1.0 is the shipped richness exactly. In the modern model "
                + "interior lightmaps ignore this (they have no live sun to fall back on), "
                + "and the probe ambient and flat ambient floor are never scaled."),
            new("shader_warmup_enabled", DeveloperCategory, RuntimeCVarType.Bool, "true",
                "Compile every shader pipeline behind a preparation screen before the guest "
                + "boots. Skipping it lets pipeline compilation stall the main thread during "
                + "guest boot, which can hang the launch after any shader change."),
            new("lighting_debug_view", DeveloperCategory, RuntimeCVarType.Int, "0",
                "Isolate one lighting term on every lit surface: 0 off, 1 bake/ambient only, "
                + "2 direct light only, 3 raw albedo, 4 normal-map darkening, 5 bake MODE "
                + "(red flat, yellow probe, green vertex bake, blue texture lightmap, cyan "
                + "terrain), 6 the sun's raw shadow term (white lit, black shadowed), 7 signed "
                + "NdotL against the sun, 8 the view-space normal as colour. A diagnostic "
                + "for attributing a bad colour to the term carrying it."),
            new("debug_channel", DeveloperCategory, RuntimeCVarType.Bool, "false",
                "Open the loopback debug channel at launch and publish its handshake file, "
                + "the same as launching with REFII_DEBUG_PORT=1. Off for players."),
            new("postfx_dump_period", DeveloperCategory, RuntimeCVarType.Int, "0",
                "Print the tone-mapping histogram, the adapted bounds, the exposure and the "
                + "bloom coverage every N frames. 0 is off. This reads back from the GPU and "
                + "stalls the device, so it is a diagnostic, not something to leave on."),
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
