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
