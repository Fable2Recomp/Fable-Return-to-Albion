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
            new("interior_ambient_scale", LightingCategory, RuntimeCVarType.Double, "3.0",
                "Lifts the theme's flat ambient in levels with no sky — caves, crypts, "
                + "windowless rooms — where it is the only light between the torches. The "
                + "360 rendered those rooms far brighter than the shipped numbers alone "
                + "give; 1.0 is the shipped value, higher is the remaster's cave look. "
                + "Modern model only."),
            new("interior_ambient_neutral", LightingCategory, RuntimeCVarType.Double, "0.6",
                "How far the interior ambient lift is pulled toward neutral. The cave themes "
                + "state a saturated orange ambient; lifted as-is it paints every surface "
                + "orange. 0 keeps the theme's colour, 1 lifts with white."),
            new("interior_bake_scale", LightingCategory, RuntimeCVarType.Double, "3.0",
                "Lifts the authored vertex bakes in levels with no sky, so the room's own "
                + "painted colour and shading carry part of the cave look instead of the "
                + "flat ambient alone. 1.0 is the shipped strength. Modern model only."),
            new("interior_dark_lift", LightingCategory, RuntimeCVarType.Double, "3.0",
                "The tone floor divisor in levels with no sky. The original clamps the "
                + "scene average to 0.15 before exposing, which renders any room far below "
                + "that as a dark room; this lets the floor fall to 0.15 divided by the value "
                + "for such a frame. 1.0 is the original behaviour. Outdoors uses "
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
