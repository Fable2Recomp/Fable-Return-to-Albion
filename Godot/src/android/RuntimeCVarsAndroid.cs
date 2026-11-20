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
            + "phones. Low runs the modern lighting model with its lite shaders' cheaper fog, ground "
            + "shadow and hand-offs, the others the fable model; a change of model applies at the next "
            + "launch. Changing any of those settings afterwards reads as Custom.",
            "android_low|Low,android_mid|Medium,android|High,android_high|Ultra,custom|Custom"),

        new(GraphicsPresets.RevisionKey, DisplayCategory, RuntimeCVarType.Int, "1",
            "The presets' revision these settings were last written from; a phone still on a "
            + "named preset takes that preset's newer values once, on its next start."),
    };

    /// <summary>Retained empty group after promoting <c>sun_shadow_filter</c> to a shared desktop + mobile CVar.</summary>
    public static readonly RuntimeCVarDefinition[] SunShadowFilter = System.Array.Empty<RuntimeCVarDefinition>();

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

    /// <summary>The launcher's Baked lamp shadows choice (<see cref="Refii.AndroidRuntime.AndroidBakedShadows"/>), before <c>static_shadow_resolution</c>.</summary>
    public static readonly RuntimeCVarDefinition[] BakedShadows =
    {
        // Display, not Lighting: the launcher's list leaves Lighting to the desktop's own dialog.
        new(Refii.AndroidRuntime.AndroidBakedShadows.Key, DisplayCategory, RuntimeCVarType.String,
            Refii.AndroidRuntime.AndroidBakedShadows.OffValue,
            "Android: when lamp and torch shadows are baked on the phone and drawn from the stored result. "
            + "'true' (Before playing): Play bakes every area first, lamp shadows and ground AO, about 2.5 GB "
            + "and hours the first time; 'while_playing': each area's lamp shadows bake the first time it is "
            + "entered, under a minute of stutter, no ground AO; 'false' (Off): none, and baked files are "
            + "ignored. Applies at the next launch.",
            Refii.AndroidRuntime.AndroidBakedShadows.Options),
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

    /// <summary>The lite shaders, the effect-light limit and the cave sun, the pipeline warm-up, the CPU and GC placement, the colour buffer and the motion fit, after <c>texture_mipmap_bias</c>.</summary>
    public static readonly RuntimeCVarDefinition[] Tuning =
    {
        new("shader_variant", DisplayCategory, RuntimeCVarType.String, "full",
            "Android: the surface shaders. 'lite' compiles out what the phone never draws "
            + "(weather, debug views, parallax, specular and cube work where none applies, flat "
            + "terrain's cliff projection), which frees GPU registers; 'lite_flat' also drops the "
            + "ground's bump. The desktop always draws 'full'. Applies at the next launch.",
            "full|Full,lite|Lite,lite_flat|Lite, flat ground"),

        new("android_optimize_index_order", DisplayCategory, RuntimeCVarType.Bool, "false",
            "Android: reorder each opaque (non-blended) model surface's indices, and its LOD levels', "
            + "for the vertex cache (meshoptimizer), so a tile-based GPU shades fewer vertices. Only the "
            + "index order changes; the same triangles. Costs a little load time. Applies to models "
            + "built after the next launch."),

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

        new(Refii.Models.Engine_ShaderSpecialisation.TerrainBitLoopKey, DisplayCategory, RuntimeCVarType.Bool, "true",
            "Android, lite ground: the splat loop visits only the layers painted near each pixel "
            + "instead of stepping past every absent one. The same picture bit for bit. Applies at "
            + "the next launch."),

        new(Refii.Models.Engine_ShaderSpecialisation.TerrainMaxLayersKey, DisplayCategory, RuntimeCVarType.Int, "0",
            "Android, lite ground: at most this many of a pixel's painted layers are drawn, the "
            + "strongest, their weight rescaled so the ground keeps its brightness; each layer left "
            + "out saves a texture fetch (three on a cliff). 1 to 4; 0 draws them all. Only where "
            + "layers meet changes: 3 by a level or two, 1 gives hard edges. Applies at the next launch."),

        new(Refii.Models.Engine_ShaderSpecialisation.TerrainMinWeightKey, DisplayCategory, RuntimeCVarType.Double, "0",
            "Android, lite ground: a painted layer weighing less than this (0 to 0.5) is not drawn and "
            + "the rest are rescaled to its weight; 0 draws every layer. 0.03 is a level of 255 at "
            + "the faint edges of a blend, 0.08 a few. Applies at the next launch."),

        new(Refii.Models.Engine_ShaderSpecialisation.TerrainCliffSharpnessKey, DisplayCategory, RuntimeCVarType.Double, "0",
            "Android, lite ground: how much of a rock face's blend between its two projections is "
            + "given to one of them (0 to 0.45), so most faces sample one cliff texture instead of "
            + "two. 0 blends as the original; 0.2 sharpens the blend visibly on faces turned "
            + "diagonally. Applies at the next launch."),

        new(Refii.Models.Engine_ShaderSpecialisation.TerrainTrilinearKey, DisplayCategory, RuntimeCVarType.Bool, "false",
            "Android, lite ground: the ground's layer and cliff textures filtered without anisotropy, "
            + "fewer texture reads where the ground is seen at a grazing angle. The far ground "
            + "turns softer. Applies at the next launch."),

        new(Refii.Models.Engine_ShaderSpecialisation.TerrainVertexFogKey, DisplayCategory, RuntimeCVarType.Bool, "false",
            "Android, lite ground: the distance fog on the ground worked out at each corner of the "
            + "ground's mesh and blended across it, instead of at every pixel; about a fifth of the "
            + "ground's pixel instructions. Within a level or two of 255 of the per-pixel fog. Applies at "
            + "the next launch."),

        new(Refii.Models.Engine_ShaderVariant.SkipBlackLightsKey, DisplayCategory, RuntimeCVarType.Bool, "true",
            "Android, lite shaders: the lighting maths is skipped for lights that add nothing (Godot's "
            + "empty first slot for each light type). The same picture; about 5% of a Galaxy S10e's "
            + "GPU time in town, nothing measurable on a Galaxy S25+. Applies at the next launch."),

        new(Refii.AndroidRuntime.AndroidGroundShading.Key, DisplayCategory, RuntimeCVarType.String, Refii.AndroidRuntime.AndroidGroundShading.Default,
            "Android, lite shaders under the modern lighting model (android_modern_lighting): how the "
            + "baked ground-contact shadow (where objects meet the ground) is fetched. 'full' on every "
            + "pixel; 'near' only on the pixels of objects within a metre of the ground, the same "
            + "picture; 'fast' at the objects' vertices, "
            + "softer along long walls; 'fastest' at the ground's vertices too, softer at the foot of "
            + "walls; 'off' without it. No effect under the fable model or with baked_ao_enabled off. "
            + "Applies at the next launch.",
            "full|Full,near|Near the ground,fast|Fast,fastest|Fastest,off|Off"),

        new(Refii.AndroidRuntime.AndroidGroundShading.GatherKey, DisplayCategory, RuntimeCVarType.Bool, "false",
            "Android, lite shaders under the modern lighting model: the ground-contact shadow's four "
            + "texels read with three gather instructions instead of four fetches, wherever an object reads "
            + "them (lite_ground_ao near's vertices and pixels, fast and fastest). The same "
            + "picture. Applies at the next launch."),

        new(Refii.Models.Engine_ShaderVariant.FableNoBakedAoKey, DisplayCategory, RuntimeCVarType.Bool, "true",
            "Android, lite shaders under the fable lighting model: the baked ambient-occlusion lookups, "
            + "which that model always runs at zero strength, are compiled out of the surface and ground "
            + "shaders. The same picture. Off keeps them in, as before. Being measured. Applies at the next launch."),

        new(Refii.Models.Engine_ShaderVariant.FoldFableModelKey, DisplayCategory, RuntimeCVarType.Bool, "false",
            "Android, lite shaders under the fable lighting model: the lighting values that model "
            + "never changes (the modern model's blend, sky tether, interior lift and fill floor) "
            + "are compiled into the surface and ground shaders instead of read per pixel. The same "
            + "picture; about 6% fewer prop fragment instructions. No effect under the modern model. "
            + "Applies at the next launch."),

        new(Refii.Models.Engine_ShaderVariant.NormalMapsKey, DisplayCategory, RuntimeCVarType.String, "full",
            "Android, lite shaders: how models' normal maps (the small bumps on walls, clothes and "
            + "props) are drawn. full: through each model's stored tangents, as before. direct: the "
            + "shader's own bumped normal is used as is, without the engine working it out a second "
            + "time; the same picture. fast: the tangents are worked out per pixel from the screen "
            + "instead of carried by every vertex; the same picture to a shade, much less work per "
            + "vertex. off: no bumps. The ground's are terrain_normal_maps. Applies at the next launch.",
            "full|Full,direct|Direct,fast|Fast,off|Off"),

        new(Refii.Models.Engine_ShaderVariant.HalfPrecisionLightingKey, DisplayCategory, RuntimeCVarType.Bool, "false",
            "Android, lite shaders: the lighting maths' colours, light falloff and shading weights are "
            + "marked half precision (FABLE_LITE_MEDIUMP), which a phone GPU may run at twice the rate. "
            + "Positions, distances and the specular cosine stay full precision. Nearly the same "
            + "picture; the faintest dark gradients may band. Applies at the next launch."),

        new(Refii.Models.Engine_ShaderVariant.FoldModernModelKey, DisplayCategory, RuntimeCVarType.Bool, "false",
            "Android, lite shaders under the modern lighting model (android_modern_lighting): the "
            + "model's fixed blend is compiled into the surface and ground shaders, and, when baked "
            + "AO starts at its default strength and light effect, those too. The same picture; about "
            + "2.5% fewer prop and ground fragment instructions. With it on, a change to baked AO's "
            + "strength or light effect, or turning baked AO off, waits for the next launch. No effect "
            + "under the fable model. Applies at the next launch."),

        new(Refii.AndroidRuntime.AndroidLeanShading.FogKey, DisplayCategory, RuntimeCVarType.String, Refii.AndroidRuntime.AndroidLeanShading.FogDefault,
            "Android, lite shaders: how the surface and ground shaders work out the distance fog. full: "
            + "as before. lean: the same sums with less work (the distance and the view direction from one "
            + "square root, the sun's length, the sky table's size and the fog factors as the constants they "
            + "are); the same picture. table: lean, with the fog's thickness read from a small table built whenever the "
            + "atmosphere changes instead of worked out per pixel (a power and three exponentials); within "
            + "a fifth of a level of 255 in the shipped outdoor fog. Applies under the modern lighting model "
            + "only (android_modern_lighting), where it is faster; under the fable model, where it is slower, "
            + "table draws as lean. Applies at the next launch.",
            "full|Full,lean|Lean,table|Table"),

        new(Refii.AndroidRuntime.AndroidLeanShading.DarkeningKey, DisplayCategory, RuntimeCVarType.Bool, "true",
            "Android, lite shaders: the ambient's darkening where a normal map turns away from the light "
            + "is worked out with the camera's normals, without turning both into world space or "
            + "normalising the bent normal (three square roots fewer per pixel). The same picture. "
            + "Applies at the next launch."),

        new(Refii.AndroidRuntime.AndroidLeanTerms.LightAoKey, DisplayCategory, RuntimeCVarType.Bool, "true",
            "Android, lite shaders under the modern lighting model (android_modern_lighting): what each light "
            + "does with the baked ambient occlusion is worked out without the clamps the occlusion's range "
            + "already settles (a few instructions fewer per light per pixel). The same picture bit for bit. "
            + "Applies at the next launch."),

        new(Refii.AndroidRuntime.AndroidLeanTerms.FrameSignKey, DisplayCategory, RuntimeCVarType.Bool, "false",
            "Android, lite shaders with lite_normal_maps = fast: the handedness of each pixel's bump frame is "
            + "taken from a cross product the frame already has, one fewer per pixel. The same sum in a "
            + "different order: it can differ only on triangles seen exactly edge-on. Applies at the next launch."),

        new(Refii.AndroidRuntime.AndroidLeanVaryings.TrimKey, DisplayCategory, RuntimeCVarType.Bool, "true",
            "Android, lite shaders: values the vertex stage hands to the pixel stage that no pixel reads "
            + "are not handed on (a solid prop's fade, the ground's page rectangle outside the distant pages, "
            + "a flat ground's normal, the lightmap coordinates of a prop drawn by a build for another bake). "
            + "The same picture bit for bit; less vertex memory for a tile-based GPU. Applies at the next launch."),

        new(Refii.AndroidRuntime.AndroidLeanVaryings.PackKey, DisplayCategory, RuntimeCVarType.Bool, "true",
            "Android, lite shaders under the modern lighting model (android_modern_lighting): a prop's height "
            + "above the ground, its fade and its per-corner ground shadow ride the vertex colour, which that "
            + "model does not otherwise read, instead of values of their own. The same picture. Applies at the "
            + "next launch."),

        new(Refii.AndroidRuntime.AndroidLeanVaryings.FragmentWorldKey, DisplayCategory, RuntimeCVarType.Bool, "false",
            "Android, lite shaders: each pixel works out its world position from the camera's view of it (about "
            + "a dozen instructions) instead of every vertex handing it on: three values fewer per vertex for a "
            + "tile-based GPU to store. The same picture to rounding. Being measured. Applies at the next launch."),

        new(Refii.AndroidRuntime.AndroidLeanVaryings.TerrainViewFrameKey, DisplayCategory, RuntimeCVarType.Bool, "false",
            "Android, lite ground: each pixel works out the ground's own position and normal from the camera's "
            + "instead of every vertex handing them on, six values fewer per vertex; with lite_fragment_world the "
            + "ground hands on only the engine's own two. Within a level or two of 255. Being measured. Applies "
            + "at the next launch."),

        new(Refii.AndroidRuntime.AndroidLeanVaryings.MediumpKey, DisplayCategory, RuntimeCVarType.Bool, "false",
            "Android, lite shaders: the small-range values the vertex stage hands on (fog, ground shadow, heights, "
            + "the ground's normal) are marked half precision, which a phone GPU may store at half the size. "
            + "Nearly the same picture. Applies at the next launch."),

        new(Refii.AndroidRuntime.AndroidLeanVaryings.PropFragmentWorldKey, DisplayCategory, RuntimeCVarType.Bool, "false",
            "Android, lite shaders: lite_fragment_world for objects only (props, buildings, people): each of their pixels "
            + "works out its world position from the camera's view of it, three values fewer per vertex; the ground "
            + "keeps handing its own on. With lite_normal_maps = fast an object's pixel then reads 14 values from its "
            + "vertices instead of 23. The same picture to rounding. Being measured. Applies at the next launch."),

        new(Refii.AndroidRuntime.AndroidLeanVaryings.PackLightmapUvKey, DisplayCategory, RuntimeCVarType.Bool, "false",
            "Android, lite shaders under the modern lighting model with lite_pack_varyings: a solid object's "
            + "indoor light map coordinates ride the vertex colour's two free channels instead of values of their "
            + "own, two values fewer per pixel (not with lite_ground_ao = fast or fastest, which use one of them). "
            + "The colour crosses at half precision: within a sixtieth of a light-map texel. Applies at the next launch."),

        new(Refii.Models.Engine_ShaderVariant.WorldVertexKey, DisplayCategory, RuntimeCVarType.Bool, "false",
            "Android, lite shaders: models' vertex shaders work in world space, so each vertex skips "
            + "multiplying the camera into its model's matrix (per vertex for batched props). About a "
            + "sixth less vertex work for a batched prop, a fifth less in the binning pass. The same "
            + "picture. Meant for presets without shadows (Low, Medium): while a shadow map renders, "
            + "solid objects then cast through their own shader instead of the engine's shared "
            + "depth-only one. Being measured. Applies at the next launch."),

        new(Refii.AndroidRuntime.AndroidModernAmbientFold.Key, DisplayCategory, RuntimeCVarType.Bool, "false",
            "Android, lite shaders under the modern lighting model (android_modern_lighting): "
            + "Godot's own ambient light is compiled out of the surface shaders, which add the same "
            + "ambient themselves. The same picture, except that the ambient on a decal's pixels "
            + "shades the surface under it. No effect under the fable model. Applies at the next launch."),

        new(Refii.AndroidRuntime.AndroidLightTablePacking.Key, DisplayCategory, RuntimeCVarType.Bool, "true",
            "Android, lite shaders: each lamp's record in the light table is read in one fetch instead "
            + "of three. The same picture. Applies at the next launch."),

        new(Refii.AndroidRuntime.AndroidNoSpotLights.Key, DisplayCategory, RuntimeCVarType.Bool, "false",
            "Android: spot lights are not drawn, so surfaces skip the spot-light pass. Spot-lit "
            + "areas lose that light. Applies at the next launch."),

        new(Refii.AndroidRuntime.AndroidTranslucentPass.Key, DisplayCategory, RuntimeCVarType.Bool, "false",
            "Android: people's and creatures' see-through surfaces (hair, lace, veils) draw once, "
            + "cut out where they are more than half opaque, instead of twice (solid, then blended "
            + "over). Cheaper; their edges turn hard and the faintest strands go. Windows and "
            + "other glass in the world keep both. Applies at the next launch."),

        new(Refii.AndroidRuntime.AndroidOpaqueOrder.DiscardKey, DisplayCategory, RuntimeCVarType.Int, "0",
            "Android: where surfaces that can cut holes (leaves, grass, cut-out cards, swaying and "
            + "fading objects) draw among the solid ones: above 0 after them, below 0 before them, "
            + "0 as the engine sorts. The same picture except where two surfaces are exactly level "
            + "with each other. -16 to 16. Applies at the next launch."),

        new(Refii.AndroidRuntime.AndroidOpaqueOrder.TerrainKey, DisplayCategory, RuntimeCVarType.Int, "0",
            "Android: where the ground draws among the solid surfaces: above 0 after them, so the "
            + "ground hidden behind buildings and props is skipped, below 0 before them, 0 as the "
            + "engine sorts. The same picture except where a surface is exactly level with the "
            + "ground. -16 to 16. Applies at the next launch."),

        new(Refii.AndroidRuntime.AndroidCharacterCull.Key, DisplayCategory, RuntimeCVarType.Bool, "false",
            "Android, lite shaders: people's and creatures' cut-out and see-through surfaces (hair, "
            + "lace, fringes, hems) draw only the side facing the camera, as the original game draws "
            + "them, instead of both sides. The inside of a hood or a coat's hem seen from behind is "
            + "not drawn. Applies at the next launch."),

        new(Refii.AndroidRuntime.AndroidFurShells.Key, DisplayCategory, RuntimeCVarType.Int, "0",
            "Android: at most this many fur layers on furred creatures (the dog, wolves); each layer "
            + "past the first draws the whole coat again. The original uses 16 to 30. Fewer layers "
            + "keep the coat's length but look coarser up close. 0 keeps the original count. Takes "
            + "effect live."),

        new(Refii.Models.Engine_MdlAutoLod.SkinnedKey, DisplayCategory, RuntimeCVarType.Bool, "false",
            "Android: people's and creatures' model pieces that ship no simpler versions (the hero's "
            + "clothes, hair and body, the dog, some creatures) get generated ones too, as scenery "
            + "does under mesh_auto_lod. Townsfolk already ship theirs. Applies to models loaded "
            + "after the next launch."),

        new(Refii.AndroidRuntime.AndroidCharacterFarLods.Key, DisplayCategory, RuntimeCVarType.Double, "0",
            "Android: people and creatures get two much simpler versions for far away, their shapes "
            + "rebuilt on a grid of this many centimetres and 2.5 times that, switched by the same "
            + "rule as their other versions (mesh_lod_pixels). A townsperson's simplest shipped "
            + "version is still 2,500 to 4,500 triangles; 5 gives about 1,300 to 3,200 and then 400 "
            + "to 950. Lumpier shapes far away. 0 is off; 1 to 50. Applies to models loaded after the "
            + "next launch."),

        new(Refii.AndroidRuntime.AndroidCharacterLevers.LodBiasKey, DisplayCategory, RuntimeCVarType.Double, "1",
            "Android: when people and creatures switch to their simpler versions, as a multiple of "
            + "the shared rule (mesh_lod_pixels): 0.5 switches each at half the distance, 1 keeps the "
            + "rule. Also their fur, what they carry and wear. 0.1 to 4. Takes effect live."),

        new(Refii.AndroidRuntime.AndroidCharacterLevers.FarShadingKey, DisplayCategory, RuntimeCVarType.Double, "0",
            "Android, lite shaders: people and creatures farther than this many metres draw without "
            + "their normal maps, as prop_far_shading_metres does for objects; 0 is off, 15 to 25 "
            + "suggested. The first switch of each kind of surface compiles its shader. Takes effect live."),

        new(Refii.AndroidRuntime.AndroidCharacterLevers.FarSinglePassKey, DisplayCategory, RuntimeCVarType.Double, "0",
            "Android: people and creatures farther than this many metres draw their hair, lace and "
            + "other see-through surfaces once, cut out with hard edges, instead of twice (as "
            + "android_translucent_single_pass does everywhere); nearer ones keep both passes. 0 is "
            + "off, 10 to 20 suggested. Takes effect live."),

        new(Refii.AndroidRuntime.AndroidEntityProps.BatchesKey, DisplayCategory, RuntimeCVarType.String, Refii.AndroidRuntime.AndroidEntityProps.Auto,
            "Android, modern lighting: whether still objects the game places (crates, stall parts, "
            + "bottles) are drawn in batches. 'auto' is off under android_lean_modern and on otherwise; "
            + "'off' draws each one by itself; 'on' batches them over 64 m squares, which draws every "
            + "member at its nearest one's detail and range; 'cells' batches them over "
            + "android_entity_batch_cell_metres squares. The same objects drawn. Takes effect live.",
            "auto|Auto,off|Off,on|On,cells|Cells"),

        new(Refii.AndroidRuntime.AndroidEntityProps.CellKey, DisplayCategory, RuntimeCVarType.Double, "8",
            "Android: the square, in metres, android_entity_batches = 'cells' gathers still objects over. "
            + "Smaller keeps each object nearer its own detail and distance; larger draws fewer batches. "
            + "4 to 64. Takes effect live."),

        new(Refii.AndroidRuntime.AndroidEntityProps.LodBiasKey, DisplayCategory, RuntimeCVarType.Double, "1",
            "Android: when objects the game places (crates, stall parts, bottles, doors) switch to their "
            + "simpler versions, as a multiple of the shared rule (mesh_lod_pixels): 0.5 switches each at "
            + "half the distance, 1 keeps the rule. People keep android_character_lod_bias. 0.1 to 4. "
            + "Takes effect live."),

        new(Refii.AndroidRuntime.AndroidEntityProps.DistanceKey, DisplayCategory, RuntimeCVarType.Double, "1",
            "Android: how far objects the game places (crates, stall parts, bottles, doors) are drawn, "
            + "as a multiple of their stated distance and of the small objects' size limits "
            + "(prop_cull_distance_scale); people keep entity_draw_distance_scale. 0.25 to 2. Takes "
            + "effect live."),

        new(Refii.AndroidRuntime.AndroidCharacterShading.LiteKey, DisplayCategory, RuntimeCVarType.Bool, "false",
            "Android, lite shaders: people's and creatures' surfaces on shaders of their own without "
            + "what no character uses (the indoor light map's coordinates, the scenery's fade-in) and "
            + "with their materials' fixed switches built in. The same picture; a few more shaders. "
            + "Applies at the next launch."),

        new(Refii.AndroidRuntime.AndroidCharacterShading.NormalMapsKey, DisplayCategory, RuntimeCVarType.String, "full",
            "Android, lite shaders: how people's and creatures' normal maps are applied. 'fast' "
            + "works their tangents out per pixel instead of carrying them from every vertex, as "
            + "lite_normal_maps = \"fast\" does for everything; the bumps are flat across each "
            + "triangle. 'full' draws them as the rest of the world. Applies at the next launch.",
            "full|As the world,fast|Fast"),

        new(Refii.AndroidRuntime.AndroidCharacterShading.FragmentWorldKey, DisplayCategory, RuntimeCVarType.Bool, "false",
            "Android, lite shaders: people and creatures work out their world position per pixel "
            + "instead of carrying it from every vertex, as lite_prop_fragment_world does for "
            + "objects. The same picture to rounding. Applies at the next launch."),

        new(Refii.AndroidRuntime.AndroidCharacterShading.GroundAoKey, DisplayCategory, RuntimeCVarType.String, "near",
            "Android, lite shaders: the ground's contact shadow on people's and creatures' feet. "
            + "'origin' decides which pixels are near enough the ground to look it up by their "
            + "height above the character's feet instead of reading the ground map at every vertex: "
            + "the same picture except beside a ledge higher than 0.58 m. 'off' drops it from "
            + "characters. 'near' draws it as on objects. Applies at the next launch.",
            "near|As objects,origin|From the feet,off|Off"),

        new(Refii.AndroidRuntime.AndroidFurShellLods.Key, DisplayCategory, RuntimeCVarType.Bool, "false",
            "Android: a furred creature's fur layers (the dog, wolves) use the simpler versions its "
            + "body has, instead of always the full model, so far fur is as simple as the body under "
            + "it. Matters only where the body has simpler versions (mesh_auto_lod_skinned, "
            + "android_character_far_lod_cm). Applies at the next launch."),

        new(Refii.AndroidRuntime.AndroidBodyMerge.Key, DisplayCategory, RuntimeCVarType.Bool, "true",
            "Android: a dressed body's pieces of one material are drawn as one even when their "
            + "simpler versions switch at different distances, each piece still switching where it "
            + "would alone. The same picture in fewer draws, mostly once mesh_auto_lod_skinned is on. "
            + "Applies at the next launch."),

        new(Refii.AndroidRuntime.AndroidLeanTreeStreams.Key, DisplayCategory, RuntimeCVarType.Bool, "false",
            "Android: a tree's wind data is stored in 4 bytes a vertex instead of 48 (the sway reads "
            + "the same values), so a tree vertex is about 32 bytes. Applies to trees built after the "
            + "change; ignored under the classic renderer."),

        new(Refii.AndroidRuntime.AndroidHudRate.Key, DisplayCategory, RuntimeCVarType.Bool, "false",
            "Android: the in-game HUD is drawn every other frame while nothing on it follows the "
            + "world or the camera; menus, markers, mod pieces and a turning camera draw every "
            + "frame. Its own animations then move at half the frame rate. Takes effect live."),

        new(Refii.AndroidRuntime.AndroidTargetingHighlight.Key, DisplayCategory, RuntimeCVarType.Bool, "true",
            "Android: draw the glow around the person or object the hero faces. While one shows, "
            + "the phone draws it in three small passes of its own (about 1.4 to 1.6 ms of an "
            + "Adreno 640's frame in the Bowerstone market) and a full-view pass that reads the "
            + "scene's depth. Off: no outline. Takes effect live."),

        new(Refii.AndroidRuntime.AndroidTargetingHighlight.DepthTestKey, DisplayCategory, RuntimeCVarType.Bool, "false",
            "Android: the target outline is hidden behind walls by the depth buffer's own test, one "
            + "quad per highlighted object at the depth it is tested at, instead of by reading a copy "
            + "of the scene's depth. The same picture; while an outline shows, the main view no "
            + "longer ends its opaque pass, copies depth and reopens a pass for the see-through "
            + "objects (unless soft effects or water also read depth: fx_no_depth_reads, "
            + "android_fx_hard_all). Takes effect live."),

        new(Refii.AndroidRuntime.AndroidPoseRate.Key, DisplayCategory, RuntimeCVarType.Double, "0",
            "Android: people and creatures within this many metres of the camera are posed every "
            + "frame; farther ones only on the game's tick, or at far_rig_pose_hz when that is set. "
            + "0 keeps the game's 55 m. Less main-thread time; mid-distance people move less smoothly. "
            + "Takes effect live."),

        new(Refii.AndroidRuntime.AndroidOffscreenRigs.Key, DisplayCategory, RuntimeCVarType.Int, "1",
            "Android: people and creatures out of view are updated on one frame in this many (2-8); 1 "
            + "updates every one every frame. Only while the sun casts no shadow (Low, Medium). Less "
            + "main-thread time; a lantern or effect carried by someone out of view moves less smoothly, "
            + "and someone walking into view can be placed a few frames late for one frame. Takes effect live."),

        new(Refii.AndroidRuntime.AndroidSkipIndexHolds.RigPropsKey, DisplayCategory, RuntimeCVarType.Bool, "true",
            "Android, with entity_skip_index: doors, signs and lamps whose animation has settled skip "
            + "the scene update between the checks that read their animation (one frame in six), "
            + "which still notice a door starting to open on the same frame as before; out of view, "
            + "those still animating skip it too until they come into view (they are posed only in "
            + "view either way). The same picture; less main-thread time. Takes effect live."),

        new(Refii.AndroidRuntime.AndroidSkipIndexHolds.BuildingsKey, DisplayCategory, RuntimeCVarType.Bool, "true",
            "Android, with entity_skip_index: buildings made of several models skip the scene update "
            + "while they stand still in a level's first minutes too, as they do afterwards. The same "
            + "picture; less main-thread time. Takes effect live."),

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

        new(Refii.Models.Engine_MdlAutoLod.FarKey, DisplayCategory, RuntimeCVarType.Bool, "false",
            "Android: models that get generated simpler versions (mesh_auto_lod) also get two much "
            + "simpler ones for far away, switched by the same rule as the others (mesh_lod_pixels). "
            + "Past about four times its own size a model otherwise has nothing simpler to switch to. "
            + "A little longer loading. Applies to models loaded after the next launch."),

        new(Refii.Level.Engine_LevelProps.PropCellKey, DisplayCategory, RuntimeCVarType.Double, "0",
            "Android: the size, in metres, of the cells placed objects, buildings and trees are "
            + "batched in (undergrowth has foliage_cell_metres); 0 keeps 32. A batch switches to its "
            + "simpler models as one, at its cell's nearest corner, so smaller cells let the far "
            + "copies near you simplify; more, smaller draws. 8 to 32. Applies at the next region load."),

        new(Refii.AndroidRuntime.AndroidFxLightLimit.Key, DisplayCategory, RuntimeCVarType.Int, "0",
            "Android: only the nearest this many effect lights (torch and brazier flames, spells) "
            + "light their surroundings at once, ranked from the camera four times a second; the "
            + "others fade out, and a lit one keeps its place until another is clearly nearer. "
            + "Each light is shaded on every pixel of every object it reaches, and the lamp limit "
            + "(placed_light_limit) does not count these. 0 lights them all. Takes effect live."),

        new(Refii.AndroidRuntime.AndroidNearFog.Key, DisplayCategory, RuntimeCVarType.Bool, "true",
            "Android, modern lighting model: keep the haze near the camera. The modern model takes "
            + "the first 128 m of the distance fog out of every surface for Godot's volumetric fog to "
            + "put back, which a phone's renderer never draws, so without this the near world is "
            + "clearer than on the desktop or under the fable model. Takes effect live."),

        new("android_modern_lighting", DisplayCategory, RuntimeCVarType.Bool, "false",
            "Android: run lighting_model as configured (modern by default) instead of the fable "
            + "model a phone otherwise forces, while the two are compared on phones. Each graphics preset "
            + "sets it: Low on, Medium, High and Ultra off. Applies at the next launch."),

        new(Refii.AndroidRuntime.AndroidLeanModern.Key, DisplayCategory, RuntimeCVarType.Bool, "true",
            "Android, modern lighting model: particles keep the fable model's reach, fade and light "
            + "cutoff, people and scenery draw without batches, and lamps take the reach and fade "
            + "below. Much less main-thread and GPU work for nearly the same picture. Applies at "
            + "the next launch."),

        new(Refii.AndroidRuntime.AndroidLeanModern.LampReachKey, DisplayCategory, RuntimeCVarType.Double, "1",
            "Android, modern lighting model with android_lean_modern: how far lamps, torches and "
            + "effect lights reach, as a multiple of the original game's reach (the modern model "
            + "elsewhere: 1.5). A lamp that states a fade distance fades the same share sooner. "
            + "Each lamp is shaded on everything it reaches, so a shorter reach is cheaper; the "
            + "modern model's doubled brightness stays. 0.5 to 1.5. Unlike placed_light_reach_scale "
            + "it leaves the fable model alone. Takes effect live."),

        new(Refii.AndroidRuntime.AndroidLeanModern.LampFadeKey, DisplayCategory, RuntimeCVarType.Double, "0",
            "Android, modern lighting model with android_lean_modern: the share of a lamp's fade "
            + "distance over which it fades out. The modern model fades over at least half of it "
            + "(a lamp with no fade of its own from 15 m to placed_light_range's 45 m), so lamps "
            + "across a square are already dim; 0.33 keeps them bright to two thirds of the way. "
            + "0 keeps the modern model's; 0.05 to 1. The same cost. Takes effect live."),

        new(Refii.AndroidRuntime.AndroidInteriorSun.Key, DisplayCategory, RuntimeCVarType.Bool, "false",
            "Android: in a level whose lighting states no sun or moon (caves, crypts), the "
            + "unlit sun is removed instead of shaded on every pixel at no brightness. Building "
            + "interiors in a town keep it, for the street through the door. A level drawn "
            + "without it compiles its shaders for that once. Takes effect at the sun's next update."),

        new("android_pipeline_warmup", DisplayCategory, RuntimeCVarType.Bool, "false",
            "Android: under each level's loading screen, every surface the level draws is drawn "
            + "once more beside the camera under each light count it can meet in play (none, one "
            + "or several omni and spot lights), so walking compiles no pipelines. A longer load "
            + "and some driver memory for each pipeline; applies at the next level load."),

        new(Refii.AndroidRuntime.AndroidMeshCompression.Key, DisplayCategory, RuntimeCVarType.Bool, "false",
            "Android: models that are not skinned (scenery, props, buildings) are stored on the GPU "
            + "with Godot's attribute compression: positions in 16 bits within the model's box, "
            + "normal and tangent in 6 bytes, UVs in 16 bits; about 16 of a vertex's 36 bytes. "
            + "Characters, models over 32 m and UVs tiled more than 8 times stay as they are. Being "
            + "measured. Applies to models loaded after the next launch."),

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

        new(Refii.AndroidRuntime.AndroidCanvasResolution.Key, DisplayCategory, RuntimeCVarType.Bool, "false",
            "Android: the window's 2D, and the world's finished image under it, drawn at the game's "
            + "layout size (about 1560 x 739) and stretched to the screen by the copy that shows the "
            + "frame, instead of at the screen's own pixels; the world keeps as many pixels as "
            + "render_scale_percent gives. The tone-map pass and every 2D pass touch about half the "
            + "pixels on a 2280 x 1080 phone. The HUD is drawn at that size already; the stretch "
            + "repeats pixels rather than blending them, so text and the touch controls' edges are a "
            + "little uneven, and the target outline is thinner. Takes effect live."),

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

    /// <summary>The effect texture prefetch, the effect nodes built per walk and the lean touch-control canvas, after <c>fx_cpu_fastpaths</c>.</summary>
    public static readonly RuntimeCVarDefinition[] EffectsAndCanvas =
    {
        new("fx_prefetch_prepare", DisplayCategory, RuntimeCVarType.Bool, "false",
            "Android, effects: the background read of the effect textures also converts each "
            + "one for a GPU without the game's compressed formats, instead of the game doing it "
            + "on the frame an effect first appears. The same picture; the waiting textures take "
            + "more memory until used. Takes effect at the next launch."),

        new(Refii.Game.AndroidFxSpawns.Key, DisplayCategory, RuntimeCVarType.Int, "16",
            "Android, effects: how many effects start in one game frame. Entering a town starts "
            + "every torch, chimney and lamp at once, a pause of its own; past this many, the rest "
            + "start over the next few frames, oldest first. 0 starts them all at once. Applies at "
            + "the next loading screen."),

        new("canvas_lean", DisplayCategory, RuntimeCVarType.Bool, "false",
            "Android 2D: the touch controls drawn in the UI layer's screen pass instead of one "
            + "of their own while nothing shows between the two, and a screen fade too faint to "
            + "change a pixel not drawn. The same picture; the debug channel's render op "
            + "(canvasLean) flips it live."),

        new(Refii.Fx.Engine_FxDepthReads.HardAllKey, DisplayCategory, RuntimeCVarType.Bool, "false",
            "Android, effects: every smoke, steam, dust and fire particle drawn without its soft "
            + "fade where it meets a wall or the ground, so none reads the scene's depth: with "
            + "fx_no_depth_reads (the sun and moon glares) and android_highlight_depth_test only "
            + "water and rain still end the main view's opaque pass and copy depth. Particles that "
            + "touch a surface show a straight edge there. Takes effect live."),

        new(Refii.Fx.Engine_ParticleVertexLighting.Key, DisplayCategory, RuntimeCVarType.Bool, "false",
            "Android, effects: the game's own smoke, fire and trail particles are lit at their "
            + "corners instead of at every pixel, as the original game lights them. Nearly the "
            + "same picture: a lamp inside a large puff shows no bright middle, smoke within a "
            + "metre of a lamp reads brighter, and lamp light is not stopped by walls on them. "
            + "Takes effect at the next launch."),
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

    /// <summary>Every CVar key defined exclusively for Android so the desktop settings panel can hide them.</summary>
    public static readonly System.Collections.Generic.IReadOnlySet<string> AllKeys = BuildAllKeys();

    private static System.Collections.Generic.HashSet<string> BuildAllKeys()
    {
        var keys = new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal);
        foreach (RuntimeCVarDefinition[] group in new[]
        {
            GraphicsPreset,
            SunShadowFilter,
            IntroVideos,
            TextureStorage,
            BakedShadows,
            FpsCounter,
            VibrationStrength,
            RenderThread,
            Tuning,
            LohCompaction,
            EffectsAndCanvas,
            StableLightCounts,
            GcStress,
        })
        {
            foreach (RuntimeCVarDefinition definition in group)
            {
                keys.Add(definition.Name);
            }
        }

        return keys;
    }
}
