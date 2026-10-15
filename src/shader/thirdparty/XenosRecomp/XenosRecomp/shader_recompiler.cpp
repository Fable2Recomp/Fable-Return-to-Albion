#include "shader_recompiler.h"
#include "shader_common.h"

#include <unordered_set>
#include <cctype>

static constexpr char SWIZZLES[] = 
{ 
    'x',
    'y', 
    'z', 
    'w', 
    '0', 
    '1',
    '_',
    '_'
};

static constexpr const char* USAGE_TYPES[] =
{
    "float4", // POSITION
    "float4", // BLENDWEIGHT
    "uint4", // BLENDINDICES
    "uint4", // NORMAL
    "float4", // PSIZE
    "float4", // TEXCOORD
    "uint4", // TANGENT
    "uint4", // BINORMAL
    "float4", // TESSFACTOR
    "float4", // POSITIONT
    "float4", // COLOR
    "float4", // FOG
    "float4", // DEPTH
    "float4", // SAMPLE
};

static constexpr const char* USAGE_VARIABLES[] =
{
    "Position",
    "BlendWeight",
    "BlendIndices",
    "Normal",
    "PointSize",
    "TexCoord",
    "Tangent",
    "Binormal",
    "TessFactor",
    "PositionT",
    "Color",
    "Fog",
    "Depth",
    "Sample"
};

static constexpr const char* USAGE_SEMANTICS[] =
{
    "POSITION",
    "BLENDWEIGHT",
    "BLENDINDICES",
    "NORMAL",
    "PSIZE",
    "TEXCOORD",
    "TANGENT",
    "BINORMAL",
    "TESSFACTOR",
    "POSITIONT",
    "COLOR",
    "FOG",
    "DEPTH",
    "SAMPLE"
};

struct DeclUsageLocation
{
    DeclUsage usage;
    uint32_t usageIndex;
    uint32_t location;
};

// NOTE: These are specialized Vulkan locations for Unleashed Recompiled. Change as necessary. Likely not going to work with other games.
static constexpr DeclUsageLocation USAGE_LOCATIONS[] =
{
    { DeclUsage::Position, 0, 0 },
    { DeclUsage::Normal, 0, 1 },
    { DeclUsage::Tangent, 0, 2 },
    { DeclUsage::Binormal, 0, 3 },
    { DeclUsage::TexCoord, 0, 4 },
    { DeclUsage::TexCoord, 1, 5 },
    { DeclUsage::TexCoord, 2, 6 },
    { DeclUsage::TexCoord, 3, 7 },
    { DeclUsage::Color, 0, 8 },
    { DeclUsage::BlendIndices, 0, 9 },
    { DeclUsage::BlendWeight, 0, 10 },
    { DeclUsage::Color, 1, 11 },
    { DeclUsage::TexCoord, 4, 12 },
    { DeclUsage::TexCoord, 5, 13 },
    { DeclUsage::TexCoord, 6, 14 },
    { DeclUsage::TexCoord, 7, 15 },
    { DeclUsage::Position, 1, 15 },
};

static constexpr std::pair<DeclUsage, size_t> INTERPOLATORS[] =
{
    { DeclUsage::TexCoord, 0 },
    { DeclUsage::TexCoord, 1 },
    { DeclUsage::TexCoord, 2 },
    { DeclUsage::TexCoord, 3 },
    { DeclUsage::TexCoord, 4 },
    { DeclUsage::TexCoord, 5 },
    { DeclUsage::TexCoord, 6 },
    { DeclUsage::TexCoord, 7 },
    { DeclUsage::TexCoord, 8 },
    { DeclUsage::TexCoord, 9 },
    { DeclUsage::TexCoord, 10 },
    { DeclUsage::TexCoord, 11 },
    { DeclUsage::TexCoord, 12 },
    { DeclUsage::TexCoord, 13 },
    { DeclUsage::TexCoord, 14 },
    { DeclUsage::TexCoord, 15 },
    { DeclUsage::Color, 0 },
    { DeclUsage::Color, 1 }
};

static constexpr std::string_view TEXTURE_DIMENSIONS[] = 
{
    "2D",
    "3D", 
    "Cube" 
};

static constexpr uint32_t TEXTURE_DESCRIPTOR_SLOTS = 32;
static constexpr uint32_t TEXTURE_DESCRIPTOR_BYTES =
    TEXTURE_DESCRIPTOR_SLOTS * sizeof(uint32_t);

// --- Godot dialect ------------------------------------------------------------------------
//
// Godot's shading language is GLSL with its own front end, and it differs from HLSL exactly
// where the emitter below is terse: no implicit bool-to-float, no scalar-to-vector assignment,
// no `saturate`/`frac`/`rcp`, no `select`, no `return` inside a processor function, loop
// counters must be declared in the `for`, and `continue` may not sit inside a `switch`. Each of
// those has one spelling here; the helpers it calls are in
// Godot/src/Engine/material/xenos_common.gdshaderinc.

// GLSL reads "0" as an int; a float literal has to look like one.
static std::string formatFloat(float value)
{
    std::string text = fmt::format("{}", value);
    if (text.find_first_of(".eEin") == std::string::npos)
        text += ".0";
    return text;
}

static uint32_t maskCount(uint32_t mask)
{
    uint32_t count = 0;
    for (uint32_t i = 0; i < 4; i++)
        count += (mask >> i) & 0x1;
    return count;
}

// The GLSL type that holds `count` components.
static const char* vecType(uint32_t count)
{
    static constexpr const char* TYPES[] = { "float", "float", "vec2", "vec3", "vec4" };
    return TYPES[std::min<uint32_t>(count, 4)];
}

std::string ShaderRecompiler::godotVaryingName(uint32_t usage, uint32_t usageIndex) const
{
    std::string name = USAGE_VARIABLES[usage];
    for (char& c : name)
        c = char(std::tolower(static_cast<unsigned char>(c)));
    return fmt::format("v_{}{}", name, usageIndex);
}

// The Godot vertex built-in that carries a Xenos vertex element, as a vec4. Godot hands the
// attributes over decoded, so there is no R11G11B10 unpack. Texcoords 2..5 ride the four
// CUSTOM channels, which is where the mesh import has to put them. What Godot has no slot for
// (blend weights and indices, which Godot's skinning consumes before vertex() runs, a second
// colour, point size) reads as zero and is listed in the header.
//
// IN THE FILE'S OWN AXES. The mesh import rotates every model Z-up to Y-up
// (Engine_MdlGeometry.ConvertZUpToYUp); the shader was written against the file's axes, and
// its constants are in the guest's, so the rotation is undone here and the matrix aliases below
// put it back (xenos_common.gdshaderinc, xr_file_*).
std::string ShaderRecompiler::godotVertexInput(uint32_t usage, uint32_t usageIndex)
{
    switch (DeclUsage(usage))
    {
    case DeclUsage::Position:
        // A transfer-lit (`_amb2`) vertex carries its cluster offset in the position's w; the
        // mesh builder puts it in CUSTOM3.x (Engine_MdlTransfer.TryRaw).
        // A particle record's position is in the group's own space, as the stream holds it,
        // and the shader places it with g_MatWorld: no file rotation applies.
        if (usageIndex == 0 && godotParticleShader())
            return "vec4(VERTEX, 1.0)";
        if (usageIndex == 0 && godotWorldSpaceShader())
            return "(XR_GODOT_TO_GUEST_WORLD * MODEL_MATRIX * vec4(VERTEX, 1.0))";
        // A billboard record is a guest-world point with the tree's radius in w; the host puts
        // the point in VERTEX and the radius in CUSTOM0.x (Engine_ClassicBillboards).
        if (usageIndex == 0 && godotBillboardShader())
            return "vec4((XR_GODOT_TO_GUEST_WORLD * MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz, CUSTOM0.x)";
        // A repeated mesh packs its texture coordinate into the spare lanes: u in POSITION.w,
        // v in NORMAL.w (its stage exports texcoord0 = (position.w, normal.w)).
        if (usageIndex == 0 && godotRepeatedShader())
            return "vec4(xr_file_pos(VERTEX), UV.x)";
        if (usageIndex == 0)
            return godotTransferShader() ? "vec4(xr_file_pos(VERTEX), CUSTOM3.x)" : "vec4(xr_file_pos(VERTEX), 1.0)";
        // A repeated mesh's instance stream: three matrix rows whose w is the translation
        // RELATIVE to the block origin the shader adds back as g_WorldPositionAndReciprocalScale.
        // Godot's MODEL_MATRIX is the instance's own world transform and that alias is its
        // position, so the rows are the world rows with no translation.
        if (godotRepeatedShader() && usageIndex >= 2 && usageIndex <= 4)
            return fmt::format("vec4(xr_row(xr_world, {}).xyz, 0.0)", usageIndex - 2);
        // A tree's wind lanes (`sub_82A70510`: POSITION1 FLOAT16_4 at +20, COLOR1 and COLOR2
        // D3DCOLOR at +28 and +32), as stored: the mesh builder keeps POSITION1's xyz (all a tree
        // shader reads) in CUSTOM3.yzw beside an AMB2 vertex's cluster offset, and the colours
        // in CUSTOM1 and CUSTOM2 (Engine_MdlTreeStreams).
        if (usageIndex == 1 && godotTreeShader())
            return "vec4(CUSTOM3.yzw, 1.0)";
        break;
    case DeclUsage::Normal:
        if (usageIndex == 0 && godotRepeatedShader()) return "vec4(xr_file_dir(NORMAL), UV.y)";
        if (usageIndex == 0) return "vec4(xr_file_dir(NORMAL), 0.0)";
        break;
    case DeclUsage::Tangent:
        if (usageIndex == 0) return "vec4(xr_file_dir(TANGENT), 0.0)";
        break;
    case DeclUsage::Binormal:
        if (usageIndex == 0) return "vec4(xr_file_dir(BINORMAL), 0.0)";
        break;
    case DeclUsage::TexCoord:
        // Fable's streams, as its standard vertex shaders read them: TEXCOORD0 the texture
        // coordinate, TEXCOORD1 the tangent (`_nrm1` takes it whole as xyzw), TEXCOORD2 the
        // lightmap coordinate (`_amb3`, xy, Godot's UV2) or the radiance-transfer weights
        // (`_amb2`, xyzw, which the mesh builder keeps in CUSTOM0). The handedness Godot folds
        // into BINORMAL is given as 1.
        // A particle record's TEXCOORD0 (sizes, rotation, keyframe time) and TEXCOORD1 (the
        // animation lanes) are float4s the mesh builder keeps whole in CUSTOM0 and CUSTOM1.
        if (godotParticleShader() && usageIndex <= 1)
            return usageIndex == 0 ? "CUSTOM0" : "CUSTOM1";
        switch (usageIndex)
        {
        case 0: return "vec4(UV, 0.0, 0.0)";
        case 1: return "vec4(xr_file_dir(TANGENT), 1.0)";
        case 2: return godotTransferShader() || godotFurShellShader() ? "CUSTOM0" : "vec4(UV2, 0.0, 0.0)";
        case 3: return "CUSTOM0";
        case 4: return "CUSTOM1";
        case 5: return "CUSTOM2";
        }
        break;
    case DeclUsage::Color:
        if (usageIndex == 0) return "COLOR";
        if (godotTreeShader() && (usageIndex == 1 || usageIndex == 2))
            return usageIndex == 1 ? "CUSTOM1" : "CUSTOM2";
        break;
    default:
        break;
    }

    const std::string semantic = fmt::format("{}{}", USAGE_SEMANTICS[usage], usageIndex);
    if (std::find(godotUnsupportedInputs.begin(), godotUnsupportedInputs.end(), semantic) == godotUnsupportedInputs.end())
        godotUnsupportedInputs.push_back(semantic);
    return "vec4(0.0)";
}

// Where a constant's value comes from in Godot.
//
// ALIAS: the object's matrices. The guest uploads them per draw; Godot has them as built-ins,
// so the shader reads those (in the guest's axes, see xenos_common.gdshaderinc) and nothing has
// to be fed. The names are the ones the standard-material vertex shaders dot against position
// and normal: `oPos = dot(g_WorldViewProjection(i), pos)`, `worldPos = dot(g_WorldTransform(i),
// pos)`, `normal *= g_WorldPositionAndReciprocalScale.w`.
//
// MATERIAL: set per material from its own record (texture-set matrices, the material's scalar
// block, its tint, its radiance transfer, its lightmap slot) — a per-material uniform.
//
// FRAME: everything else is state the guest writes at FIXED registers into the D3D device's
// constant shadow (eye, ambient, atmosphere, mist, shadow transforms, light groups), identical
// in every shader that uses it; it is read by absolute register from the frame texture, which
// the engine fills from that shadow.
static constexpr const char* GODOT_MATERIAL_CONSTANT_PREFIXES[] =
{
    "g_StandardMaterial",
    "g_PRTConstants",
    "g_LightmapIndexOffset",
    // g_LightmapFactorAndSRGBGamma is the FRAME's PS c21 (`sub_821B16F8`: (LIGHTMAP_COLOUR.xyz,
    // 1.3), live in the device shadow), not a material's: as a material uniform nobody set,
    // every lightmapped pixel shader (180 of them) multiplied its bake by zero.
    // g_TileOffsetSize is the FRAME's PS c0: the tiler loads it per tile, before the scene's
    // draws (`sub_82B8B338`, (tile.x0 / W, tile.y0 / H, 1 / W, 1 / H)); untiled here.
    "g_DoubleSidedLightingSign",
    // The terrain's per-patch and per-layer constants: the title sets them per draw from the
    // patch and its splat layer (`sub_8221FC40`, `sub_822101F8`, `sub_822159D8`); a host patch
    // material carries its own.
    "g_PositionOffSetXY",
    "g_PositionScale",
    "g_TexCoordScale",
    "g_GridWidth",
    "g_LandscapeBlendMapUVScaleAndOffset",
    "g_ThemeWeightDiscardThreshold",
    "g_UVCliffFactor",
    "g_LandscapeLightmapUVFactor",
    "g_CliffBlendOffsetAndScale",
    "g_LandscapeAmbientLightFactor",
    "g_LandscapeDistantShadowFactor",
    "g_BackgroundFadeDistanceSquaredFactorAndOffset",
    "g_BackgroundMap",
    "g_IndexOffset",
    // The sky's and clouds' per-draw constants (`sub_8228F038`, `Render_MaterialQuadPass`,
    // the celestial draws): each is one object's material.
    "g_Tzenith",
    "g_SkyOverlayTextureBlend",
    "g_Clouds",
    "g_AccumulatedUVOffset",
    "g_StarParams",
    "g_CamPos",
    "g_SunColour",
    "g_SunDiscColour",
    "g_MoonColour",
    "g_Transparency",
    "g_ViewerPosition",
    "g_ViewerDirection",
    "g_NormalStrength",
    "g_Brightness",
    "g_LightColor",
    "g_LightPosition",
    // The water's constant block: the title fills it per draw from the day-set theme
    // (`sub_821E4C88`), so a host water material carries its own.
    "g_WaterConstants",
};

// Per-draw constants the title sets BY ID from the object being drawn (the particle draw,
// `sub_821CA108` / `sub_821CA790`): the group's transform, its material's keyframe tables and
// texture timings, the camera axes it faces, its fades, its ambient. A host particle material
// carries its own. Matched whole, because `g_GlobalAmbient` is also the head of the frame's
// `g_GlobalAmbientAndBrightness`.
static constexpr const char* GODOT_MATERIAL_CONSTANT_NAMES[] =
{
    "g_GlobalAmbient",
    // The clouds' ambient (PS c71). Whole, not a prefix: `g_AmbientLightReferenceDirection` is
    // the frame's VS c19 and the prefix made it a material uniform nobody set.
    "g_AmbientLight",
    // g_CameraPosition / g_CameraRightVector / g_CameraUpVector are the frame's (the feed
    // writes VS c46 / c115 / c114 once a frame), not each particle material's.
    "g_FadeInDistanceInfo",
    "g_FadeOutDistanceInfo",
    "g_KeyFrameColors",
    "g_KeyFrameScalesSelfIllumFactorStartTimes",
    "g_KeyFrameGlobalScales",
    // A trail's own (`sub_821B25F8`, `0x82A59DD0`): c340 its keyframes' self-illum and start
    // times, c366 (s, 1/((n-1)s), 1/s, 0) from its point count and subdivisions, and c299 the
    // camera's z or its own fixed axis. Per draw, so per material.
    "g_KeyFrameSelfIllumFactorStartTimesScale",
    "g_ParticleTrailProperties",
    "g_CameraZVector",
    "g_MatWorld",
    "g_TextureStartTimes",
    "g_TextureStartTimesInvDelta",
    "g_TextureUVScales",
    "g_TextureNumXDivs",
    "g_DepthFadeScale",
    "g_InverseViewProjection",
    // The water draw (`sub_82B3CDE8`): the last directional light's direction and colour,
    // and its FOV adjustment.
    "g_LightDirection",
    "g_FOVAdjustment",
    // Per PASS (`sub_82214FB0`): x = 1 (the pass fogs), y = 1 on a draw's first pass (its
    // fog colour), 0 on the additive passes after it (a later splat layer, a later light
    // group). A frame register cannot differ per pass, so it is each material's own.
    "g_AtmosphericFactors",
    // The tree draw's VS c8 (`sub_82210750`, binding 281): x = 1 on the pass that carries the
    // in-scatter, 0 otherwise; y..w never written (0). Per pass, so per material.
    "g_AtmosphericAdditiveFactor",
    // The vista ocean's sheet height (VS c119, binding 412: ocean+76, `sub_821D1790`). Its
    // own object's, and c119 is the snow's for something else.
    "g_WaterHeight",
    // The repeated mesh's (0.2, 15, (2 r + D)^2, 0) (`sub_82224578`): its own object's draw
    // distance and model radius, so per instance (an instance uniform, below).
    "g_RepeatedMeshConstants",
    // The repeated mesh's wind (`sub_82224578`): c25 its own sway speed (.mdl +84), c26 the
    // factors from the scene wind and its amplitude; 0 when its wind gate is off. Per draw.
    "g_SwaySpeed",
    "g_WindFactors",
    // The repeated mesh's height ramp (`sub_82233570`, VS c45): its own mesh's, per draw.
    "g_NormalMapGradient_Enable_Factor_Offset",
    // A repeated or tree draw's fade (`sub_821FCCB0`): (D(1-f), 1/(fD), D(1-f), 0) from its own
    // draw range. Per batch, so an instance uniform (below).
    "g_FadeBeginDistanceAndRange",
    // The background landscape patch's placement (VS, `landscape_background_*`): the world
    // origin and size its baked texture covers. Per patch.
    "g_OffSetX",
    "g_OffSetY",
    "g_WidthTextureMeters",
    "g_HeightTextureMeters",
    // The rain draw's own (`0x822803D0`): the camera it builds its curtains around (VS c8, c46),
    // its colours from the light zone and density (PS c46, c31), its three nearest lights.
    // The snow draw's (`0x821BDE68`): the clock, its size and fall terms, the spell pushes, and
    // per 16-unit block its origin, size and roof planes. Per draw, so per material; c8, c46,
    // c114 and c115 are frame registers for everything else.
    "g_CameraPos",
    "g_CameraDir",
    "g_RainDiffColour",
    "g_RainSpecColour",
    "g_RainLight",
    "g_Time",
    "g_SnowParams1",
    "g_SnowParams2",
    "g_PushVector",
    "g_PushVectorOverDist",
    "g_GridOrigin",
    "g_GridSize",
    "g_ClipShape",
    "g_SnowColour",
    // The decal draw's own (`Render_ParticlePoolDraw_INFERRED` 0x821A3B10): its fade, the
    // view-projection and owner matrix it places the projected grid with (VS c72, c113: c113..115
    // are frame registers for everything else), and the ambient with the distance fade (PS c31).
    "g_DecalAlpha",
    "g_MatViewProj",
    "g_PrimitiveWorldMatrix",
    "g_GlobalAmbientAndDistanceAlpha",
};

ShaderRecompiler::GodotConstantSource ShaderRecompiler::godotConstantSource(const char* name) const
{
    const std::string_view view(name);
    if (godotPostShader())
        return GodotConstantSource::Material;
    for (const char* exact : GODOT_MATERIAL_CONSTANT_NAMES)
    {
        if (view == exact)
            return GodotConstantSource::Material;
    }
    if (!isPixelShader &&
        (view == "g_WorldViewProjection" || view == "g_WorldTransform" ||
         view == "g_WorldPositionAndReciprocalScale" || view == "g_TreeConstants"))
    {
        return GodotConstantSource::Alias;
    }
    if (view.find("UVMatrix") != std::string_view::npos)
        return GodotConstantSource::Material;
    for (const char* prefix : GODOT_MATERIAL_CONSTANT_PREFIXES)
    {
        if (view.rfind(prefix, 0) == 0)
            return GodotConstantSource::Material;
    }
    return GodotConstantSource::Frame;
}

std::string ShaderRecompiler::godotUniformName(const char* constantName) const
{
    std::string name = fmt::format("{}{}", stagePrefix(), constantName);
    for (char& c : name)
    {
        if (c == '.')
            c = '_';
    }
    return name;
}

std::string ShaderRecompiler::godotAliasedConstant(const char* name, uint32_t index, uint32_t reg) const
{
    const std::string_view view(name);
    if (view == "g_WorldViewProjection")
        return fmt::format("xr_row(xr_wvp, {})", index);
    if (view == "g_WorldTransform")
        return fmt::format("xr_row(xr_world, {})", index);
    // The frame's c15..c17 with the tree's own lanes (its phase, the wind in its axes) put in
    // from its world matrix: xenos_common.gdshaderinc, `xr_tree_constant`.
    if (view == "g_TreeConstants")
        return fmt::format("xr_tree_constant(xr_vs_constants, {}, xr_world)", reg);
    return "xr_world_pos_and_rscale(xr_world)";
}

std::string ShaderRecompiler::godotFrameConstant(const std::string& registerExpression) const
{
    // A particle vertex stage's camera axes (c114 up, c115 right) are the group's own fixed axes
    // when its material states them (`sub_821CA108`, M+56): the read goes through XR_VS_CONST,
    // which the particle composition defines (xenos_common's default passes the register).
    if (!isPixelShader && godotParticleShader())
        return fmt::format("XR_VS_CONST({0}, xr_const(xr_vs_constants, {0}))", registerExpression);
    return fmt::format("xr_const({}, {})", isPixelShader ? "xr_ps_constants" : "xr_vs_constants",
        registerExpression);
}

// Everything the body referred to, declared: assembled after the body so a shader declares only
// what it touches. Stage-owned uniforms carry the stage prefix because the other stage's include
// sits in the same Godot shader; varyings are shared and guarded.
std::string ShaderRecompiler::godotHeader() const
{
    StringBuffer h;
    h.println("// {} shader recompiled by XenosRecomp for Godot. Uniforms are this stage's ({}),",
        isPixelShader ? "Pixel" : "Vertex", stagePrefix());
    h.println("// varyings are shared with the other stage behind include guards.");
    if (!godotUnsupportedInputs.empty())
    {
        h.print("// Vertex inputs with no Godot attribute, read as zero:");
        for (const std::string& input : godotUnsupportedInputs)
            h.print(" {}", input);
        h.out += '\n';
    }
    if (godotWritesExtraTargets)
        h.println("// Writes render targets beyond oC0, which a Godot material cannot carry; those are dropped.");
    h.println("#include \"res://src/Engine/material/xenos_common.gdshaderinc\"");
    if (godotUsesVertexStream)
    {
        h.println("#ifndef XR_VERTEX_STREAM");
        h.println("#define XR_VERTEX_STREAM");
        h.println("uniform sampler2D xr_vertex_stream : filter_nearest, repeat_disable;");
        h.println("#endif");
    }
    if (godotTreeAmbient)
        h.println("instance uniform vec4 xr_tree_ambient;");

    for (const std::string& varying : godotVaryings)
    {
        std::string guard = "XR_" + varying;
        for (char& c : guard)
            c = char(std::toupper(static_cast<unsigned char>(c)));
        h.println("#ifndef {}", guard);
        h.println("#define {}", guard);
        h.println("varying vec4 {};", varying);
        h.println("#endif");
    }

    std::vector<const ConstantInfo*> constants(godotConstants.begin(), godotConstants.end());
    std::sort(constants.begin(), constants.end(),
        [](const ConstantInfo* a, const ConstantInfo* b) { return a->registerIndex < b->registerIndex; });
    for (const ConstantInfo* constant : constants)
    {
        const char* name = reinterpret_cast<const char*>(constantTableData + constant->name);
        const std::string uniform = godotUniformName(name);
        if (constant->registerCount > 1)
            h.println("uniform vec4 {}[{}];", uniform, constant->registerCount.get());
        else if (std::string_view(name) == "g_StandardMaterialTintColour")
            // Per DRAW in the title (PS c49 from the instance's +240, the appearance's tint),
            // so per instance here, white until an appearance states one.
            h.println("instance uniform vec4 {} = vec4(1.0);", uniform);
        else if (std::string_view(name) == "g_AtmosphericFactors")
            // A first pass unless the material says otherwise.
            h.println("uniform vec4 {} = vec4(1.0, 1.0, 0.0, 0.0);", uniform);
        else if (std::string_view(name) == "g_AtmosphericAdditiveFactor")
            h.println("uniform vec4 {} = vec4(1.0, 0.0, 0.0, 0.0);", uniform);
        else if (std::string_view(name) == "g_RepeatedMeshConstants")
            // Per batch; unset it kills nothing (the batch's own range still applies).
            h.println("instance uniform vec4 {} = vec4(0.2, 15.0, 1.0e30, 0.0);", uniform);
        else if (std::string_view(name) == "g_FadeBeginDistanceAndRange")
            // Per batch; unset it never fades.
            h.println("instance uniform vec4 {} = vec4(0.0, 0.0, 0.0, 1.0);", uniform);
        else if (std::string_view(name) == "g_GridOrigin")
            // The snow's per-block origin (`0x821BDE68`, binding 327): one draw per 16-unit
            // block, so per instance.
            h.println("instance uniform vec4 {} = vec4(0.0);", uniform);
        else
            h.println("uniform vec4 {};", uniform);
    }
    std::vector<std::pair<uint32_t, const char*>> bools(boolConstants.begin(), boolConstants.end());
    std::sort(bools.begin(), bools.end());
    for (auto& [reg, name] : bools)
        h.println("#define {}{} (1u << {}u)", stagePrefix(), name, reg + (isPixelShader ? 16 : 0));

    for (uint32_t id : godotLoopIds)
    {
        h.println("uniform ivec4 {}i{} = ivec4({}, {}, {}, {});", stagePrefix(), id,
            godotIntDefaults[id][0], godotIntDefaults[id][1], godotIntDefaults[id][2], godotIntDefaults[id][3]);
    }

    for (auto& [name, type] : godotSamplers)
    {
        // The frame's lookup tables and shadow maps are read at the edge of their range —
        // u = 1 is the far end of the extinction table, the last cascade texel — so they
        // clamp; Godot's default would wrap them to texel 0. Every other sampler keeps the
        // default (repeat), which the tiling textures need.
        const bool clamped = name.find("InScattering") != std::string::npos ||
            name.find("Extinction") != std::string::npos ||
            name.find("ShadowMap") != std::string::npos ||
            name.find("Lightmap") != std::string::npos;
        if (name.find("g_RefractionSampler") != std::string::npos)
            // The title's refraction tile is the scene below the water plane; the scene behind
            // the surface as Godot has drawn it stands in.
            h.println("uniform {} {} : hint_screen_texture, filter_linear, repeat_disable;", type, name);
        // AN UNBOUND SAMPLER READS BLACK, as the 360's does: the shaders fetch their optional
        // maps unconditionally (the self-illumination map scaled by its constant, the specular
        // map) and Godot's default for an unset sampler is white.
        else if (name.find("g_ReflectionSampler") != std::string::npos)
            h.println("uniform {} {} : hint_default_black, filter_linear, repeat_disable;", type, name);
        else if (name.find("g_DepthSampler") != std::string::npos ||
                 name.find("g_RainDepthTexture") != std::string::npos)
            // The scene depth the title resolved before its translucent passes: Godot's own
            // depth buffer, which a transparent material reads as it draws.
            h.println("uniform {} {} : hint_depth_texture, filter_nearest, repeat_disable;", type, name);
        else if (name.find("ShadowMap") != std::string::npos)
            h.println("uniform {} {} : hint_default_black, filter_nearest, repeat_disable;", type, name);
        else if (clamped)
            h.println("uniform {} {} : hint_default_black, filter_linear, repeat_disable;", type, name);
        else
            h.println("uniform {} {} : hint_default_black;", type, name);
    }

    h.out += '\n';
    return h.out;
}

// A blob the Godot dialect does not carry: a valid stage that draws nothing useful, so the
// artifact exists and the report says why.
void ShaderRecompiler::recompileGodotUnsupported(const char* reason)
{
    out.clear();
    println("// {} shader not recompiled for Godot: {}.", isPixelShader ? "Pixel" : "Vertex", reason);
    println("#include \"res://src/Engine/material/xenos_common.gdshaderinc\"");
    if (isPixelShader)
        out += "void fragment()\n{\n\tALBEDO = vec3(1.0, 0.0, 1.0);\n}\n";
    else
        out += "void vertex()\n{\n}\n";
}

static FetchDestinationSwizzle getDestSwizzle(uint32_t dstSwizzle, uint32_t index)
{
    return FetchDestinationSwizzle((dstSwizzle >> (index * 3)) & 0x7);
}

void ShaderRecompiler::printDstSwizzle(uint32_t dstSwizzle, bool operand)
{
    for (size_t i = 0; i < 4; i++)
    {
        const auto swizzle = getDestSwizzle(dstSwizzle, i);
        if (swizzle >= FetchDestinationSwizzle::X && swizzle <= FetchDestinationSwizzle::W)
            out += SWIZZLES[operand ? uint32_t(swizzle) : i];
    }
}

void ShaderRecompiler::printDstSwizzle01(uint32_t dstRegister, uint32_t dstSwizzle)
{
    for (size_t i = 0; i < 4; i++)
    {
        const auto swizzle = getDestSwizzle(dstSwizzle, i);
        if (swizzle == FetchDestinationSwizzle::Zero)
        {
            indent();
            println("r{}.{} = 0.0;", dstRegister, SWIZZLES[i]);
        }
        else if (swizzle == FetchDestinationSwizzle::One)
        {
            indent();
            println("r{}.{} = 1.0;", dstRegister, SWIZZLES[i]);
        }
    }
}

void ShaderRecompiler::recompile(const VertexFetchInstruction& instr, uint32_t address)
{
    if (instr.isPredicated)
    {
        indent();
        println("if ({}p0)", instr.predicateCondition ? "" : "!");

        indent();
        out += "{\n";
        ++indentation;
    }

    indent();
    print("r{}.", instr.dstRegister);
    printDstSwizzle(instr.dstSwizzle, false);

    out += " = ";

    auto findResult = vertexElements.find(address);
    assert(findResult != vertexElements.end());

    if (godot() && godotTrailShader())
    {
        // An indexed fetch: the element at the index register's value, rounded or truncated as
        // the instruction asks, from the host's control-point texture.
        godotUsesVertexStream = true;
        print("xr_stream_fetch(xr_vertex_stream, r{}.{}, {}u, {})", instr.srcRegister,
            SWIZZLES[instr.srcSwizzle], uint32_t(findResult->second.usageIndex),
            instr.isIndexRounded ? "true" : "false");
    }
    else if (godot())
    {
        const uint32_t usage = uint32_t(findResult->second.usage);
        const uint32_t usageIndex = uint32_t(findResult->second.usageIndex);
        const std::string input = godotVertexInput(usage, usageIndex);
        if (findResult->second.usage == DeclUsage::TexCoord)
            print("xr_tfetchTexcoord(xr_swapped_texcoords, {}, {}u)", input, usageIndex);
        else
            out += input;
    }
    else
    {
        switch (findResult->second.usage)
        {
        case DeclUsage::Normal:
        case DeclUsage::Tangent:
        case DeclUsage::Binormal:
            specConstantsMask |= SPEC_CONSTANT_R11G11B10_NORMAL;
            print("tfetchR11G11B10(");
            break;

        case DeclUsage::TexCoord:
            print("tfetchTexcoord(g_SwappedTexcoords, ");
            break;
        }

        print("i{}{}", USAGE_VARIABLES[uint32_t(findResult->second.usage)], uint32_t(findResult->second.usageIndex));

        switch (findResult->second.usage)
        {
        case DeclUsage::Normal:
        case DeclUsage::Tangent:
        case DeclUsage::Binormal:
            out += ')';
            break;

        case DeclUsage::TexCoord:
            print(", {})", uint32_t(findResult->second.usageIndex));
            break;
        }
    }

    out += '.';
    printDstSwizzle(instr.dstSwizzle, true);

    out += ";\n";

    printDstSwizzle01(instr.dstRegister, instr.dstSwizzle);

    if (instr.isPredicated)
    {
        --indentation;
        indent();
        out += "}\n";
    }
}

void ShaderRecompiler::recompile(const TextureFetchInstruction& instr, bool bicubic)
{
    if (instr.opcode != FetchOpcode::TextureFetch && instr.opcode != FetchOpcode::GetTextureWeights)
        return;

    if (instr.isPredicated)
    {
        indent();
        println("if ({}p0)", instr.predCondition ? "" : "!");

        indent();
        out += "{\n";
        ++indentation;
    }

    auto printSrcRegister = [&](size_t componentCount)
        {
            print("r{}.", instr.srcRegister);

            for (size_t i = 0; i < componentCount; i++)
                out += SWIZZLES[((instr.srcSwizzle >> (i * 2))) & 0x3];
        };

    std::string constName;
    const char* constNamePtr = nullptr;
#ifdef UNLEASHED_RECOMP
    bool subtractFromOne = false;
#endif

    auto findResult = samplers.find(instr.constIndex);
    if (findResult != samplers.end())
    {
        constNamePtr = findResult->second;

    #ifdef UNLEASHED_RECOMP
        subtractFromOne = hasMtxPrevInvViewProjection && strcmp(constNamePtr, "sampZBuffer") == 0;
    #endif
    }
    else
    {
        constName = fmt::format("s{}", instr.constIndex);
        constNamePtr = constName.c_str();
    }

#ifdef UNLEASHED_RECOMP
    if (instr.constIndex == 0 && instr.dimension == TextureDimension::Texture2D)
    {
        indent();
        print("pixelCoord = getPixelCoord({}_Texture2DDescriptorIndex, ", constNamePtr);
        printSrcRegister(2);
        out += ");\n";
    }
#endif

    indent();
    print("r{}.", instr.dstRegister);
    printDstSwizzle(instr.dstSwizzle, false);

    out += " = ";
    switch (instr.opcode)
    {
    case FetchOpcode::TextureFetch:
    {
    #ifdef UNLEASHED_RECOMP
        if (subtractFromOne)
            out += "1.0 - ";
    #endif

        out += "tfetch";
        break;
    }
    case FetchOpcode::GetTextureWeights:
    {
        out += "getWeights";
        break;
    }
    }

    std::string_view dimension;
    uint32_t componentCount = 0;

    switch (instr.dimension)
    {
    case TextureDimension::Texture1D:
        dimension = "1D";
        componentCount = 1;
        break;
    case TextureDimension::Texture2D:
        dimension = "2D";
        componentCount = 2;
        break;
    case TextureDimension::Texture3D:
        dimension = "3D";
        componentCount = 3;
        break;
    case TextureDimension::TextureCube:
        dimension = "Cube";
        componentCount = 3;
        break;
    }

    if (godot())
    {
        // One sampler uniform per (register, dimension): the 2D one carries the bare name, which
        // is what a material binds its texture to; 3D and cube fetches get a suffix.
        const char* samplerType = "sampler2D";
        const char* suffix = "";
        if (instr.dimension == TextureDimension::Texture3D)
        {
            samplerType = "sampler3D";
            suffix = "_3d";
        }
        else if (instr.dimension == TextureDimension::TextureCube)
        {
            samplerType = "samplerCube";
            suffix = "_cube";
        }
        const std::string uniformName = fmt::format("{}{}{}", stagePrefix(), constNamePtr, suffix);
        godotSamplers.emplace(uniformName, samplerType);

        // The "tfetch"/"getWeights" already written is the HLSL name; the Godot helper is prefixed.
        out.erase(out.size() - (instr.opcode == FetchOpcode::GetTextureWeights ? 10 : 6));
        print("{}{}({}, ", instr.opcode == FetchOpcode::GetTextureWeights ? "xr_getWeights" : "xr_tfetch",
            // The scene depth is a hinted sampler, and Godot refuses one helper for samplers
            // whose hints differ, so it fetches through its own.
            instr.opcode != FetchOpcode::GetTextureWeights &&
                (std::string_view(constNamePtr).find("g_DepthSampler") != std::string_view::npos ||
                 std::string_view(constNamePtr).find("g_RainDepthTexture") != std::string_view::npos) ? "2D_depth" :
            instr.opcode != FetchOpcode::GetTextureWeights &&
                std::string_view(constNamePtr).find("g_RefractionSampler") != std::string_view::npos ? "2D_screen" :
            // The host's mirror is a right-handed camera's image of the reflected world, which
            // is the title's reflection tile mirrored in u (Engine_WaterReflection).
            instr.opcode != FetchOpcode::GetTextureWeights &&
                std::string_view(constNamePtr).find("g_ReflectionSampler") != std::string_view::npos ? "2D_mirror" :
            // Unnormalised coordinates (texCoordDenorm: the source's texels, DOWNSCALE4's vPos x 4)
            // and a 1D fetch's texel offset (the tone map's +0.5) each take a helper of their own.
            instr.opcode != FetchOpcode::GetTextureWeights && instr.dimension == TextureDimension::Texture2D &&
                instr.texCoordDenorm ? "2D_denorm" :
            instr.opcode != FetchOpcode::GetTextureWeights && instr.dimension == TextureDimension::Texture1D ? "1D_offset" :
            dimension, uniformName);
        printSrcRegister(componentCount);

        switch (instr.dimension)
        {
        case TextureDimension::Texture1D:
            if (instr.opcode != FetchOpcode::GetTextureWeights)
                print(", {}", formatFloat(instr.offsetX * 0.5f));
            break;
        case TextureDimension::Texture2D:
            print(", vec2({}, {})", formatFloat(instr.offsetX * 0.5f), formatFloat(instr.offsetY * 0.5f));
            break;
        case TextureDimension::TextureCube:
            out += ", cubeMapData";
            break;
        }

        out += ").";
    }
    else
    {
        out += dimension;

#ifdef UNLEASHED_RECOMP
        if (bicubic)
            out += "Bicubic";
#endif

        print("({0}_Texture{1}DescriptorIndex, {0}_SamplerDescriptorIndex, ", constNamePtr, dimension);
        printSrcRegister(componentCount);

        switch (instr.dimension)
        {
        case TextureDimension::Texture2D:
            print(", float2({}, {})", instr.offsetX * 0.5f, instr.offsetY * 0.5f);
            break;
        case TextureDimension::TextureCube:
            out += ", cubeMapData";
            break;
        }

        out += ").";
    }

    printDstSwizzle(instr.dstSwizzle, true);

    out += ";\n";

    printDstSwizzle01(instr.dstRegister, instr.dstSwizzle);

    if (instr.isPredicated)
    {
        --indentation;
        indent();
        out += "}\n";
    }
}

void ShaderRecompiler::recompile(const AluInstruction& instr)
{
    if (instr.isPredicated)
    {
        indent();
        println("if ({}p0)", instr.predicateCondition ? "" : "!");

        indent(); 
        out += "{\n";
        ++indentation;
    }

    enum
    {
        VECTOR_0,
        VECTOR_1,
        VECTOR_2,
        SCALAR_0,
        SCALAR_1,
        SCALAR_CONSTANT_0,
        SCALAR_CONSTANT_1
    };

    auto op = [&](size_t operand)
        {
            size_t reg = 0;
            size_t swizzle = 0;
            bool select = true;
            bool negate = false;
            bool abs = false;

            switch (operand)
            {
            case SCALAR_CONSTANT_0:
                reg = instr.src3Register;
                swizzle = instr.src3Swizzle;
                select = false;
                negate = instr.src3Negate;
                abs = instr.absConstants;
                break;

            case SCALAR_CONSTANT_1:
                reg = (uint32_t(instr.scalarOpcode) & 1) | (instr.src3Select << 1) | (instr.src3Swizzle & 0x3C);
                swizzle = instr.src3Swizzle;
                select = true;
                negate = instr.src3Negate;
                abs = instr.absConstants;
                break;

            default:
                switch (operand)
                {
                case VECTOR_0:
                    reg = instr.src1Register;
                    swizzle = instr.src1Swizzle;
                    select = instr.src1Select;
                    negate = instr.src1Negate;
                    break;
                case VECTOR_1:
                    reg = instr.src2Register;
                    swizzle = instr.src2Swizzle;
                    select = instr.src2Select;
                    negate = instr.src2Negate;
                    break;
                case VECTOR_2:
                case SCALAR_0:
                case SCALAR_1:
                    reg = instr.src3Register;
                    swizzle = instr.src3Swizzle;
                    select = instr.src3Select;
                    negate = instr.src3Negate;
                    break;
                }

                if (select)
                {
                    abs = (reg & 0x80) != 0;
                    reg &= 0x3F;
                }
                else
                {
                    abs = instr.absConstants;
                }

                break;
            }

            std::string regFormatted;

            if (select)
            {
                regFormatted = fmt::format("r{}", reg);
            }
            else
            {
                auto findResult = float4Constants.find(reg);
                const bool useRelativeAddressing = instr.const0Relative || instr.const1Relative;
                const char* relativeSuffix = "";
                if (useRelativeAddressing)
                {
                    relativeSuffix = instr.constAddressRegisterRelative ? " + a0" : " + aL";
                }
                if (findResult != float4Constants.end())
                {
                    const char* constantName = reinterpret_cast<const char*>(constantTableData + findResult->second->name);
                    if (godot())
                    {
                        const uint32_t count = findResult->second->registerCount;
                        const uint32_t index = reg - findResult->second->registerIndex;
                        // A tree's flat ambient: its one-register g_PRTConstants (VS c28) is the
                        // mesh's cluster-0 mean transfer dotted with the tree's own L1 environment
                        // (`sub_82238330`, `sub_8225DC80`), so it is the instance's, not the
                        // material's.
                        if (!isPixelShader && count == 1 && godotTreeShader() &&
                            std::string_view(constantName) == "g_PRTConstants")
                        {
                            regFormatted = "xr_tree_ambient";
                            godotTreeAmbient = true;
                        }
                        else
                        switch (godotConstantSource(constantName))
                        {
                        case GodotConstantSource::Alias:
                            regFormatted = godotAliasedConstant(constantName, index, uint32_t(reg));
                            if (std::string_view(constantName) == "g_WorldViewProjection")
                                godotAliasedPosition = true;
                            break;

                        case GodotConstantSource::Material:
                            godotConstants.insert(findResult->second);
                            if (count > 1)
                            {
                                // A constant index is inside the array by construction; a
                                // relative one is clamped and zeroed past the end, as the HLSL
                                // `select` does.
                                if (useRelativeAddressing)
                                {
                                    regFormatted = fmt::format("(({0}{1}) < {2} ? {3}[min({0}{1}, {4})] : vec4(0.0))",
                                        index, relativeSuffix, count, godotUniformName(constantName), count - 1);
                                }
                                else
                                {
                                    regFormatted = fmt::format("{}[{}]", godotUniformName(constantName), index);
                                }
                            }
                            else
                            {
                                regFormatted = godotUniformName(constantName);
                            }
                            break;

                        case GodotConstantSource::Frame:
                            // By absolute register: the frame texture is the device's constant
                            // shadow, and the title writes these at fixed registers.
                            regFormatted = godotFrameConstant(
                                useRelativeAddressing ? fmt::format("{}{}", reg, relativeSuffix)
                                                      : fmt::format("{}", reg));
                            break;
                        }
                    }
                    else if (findResult->second->registerCount > 1)
                    {
                    #ifdef UNLEASHED_RECOMP
                        if (hasMtxProjection && strcmp(constantName, "g_MtxProjection") == 0)
                        {
                            regFormatted = fmt::format("(iterationIndex == 0 ? mtxProjectionReverseZ[{0}] : mtxProjection[{0}])",
                                reg - findResult->second->registerIndex);
                        }
                        else
                    #endif
                        {
                            regFormatted = fmt::format("{}({}{})", constantName,
                                reg - findResult->second->registerIndex, useRelativeAddressing ? relativeSuffix : "");
                        }
                    }
                    else
                    {
                        // Some shaders still flag relative addressing even for scalar constants.
                        // Treat these as direct constant references instead of aborting.
                        regFormatted = constantName;
                    }
                }
                else if (godot() && godotLiteralRegisters.count(reg) == 0)
                {
                    // No constant-table entry and no literal definition: whatever the guest put in
                    // that register of the shadow.
                    regFormatted = godotFrameConstant(
                        useRelativeAddressing ? fmt::format("{}{}", reg, relativeSuffix)
                                              : fmt::format("{}", reg));
                }
                else
                {
                    // Keep recompilation alive for unknown constants even when relative flags are set.
                    regFormatted = fmt::format("c{}", reg);
                }
            }

            std::string result;

            if (negate)
                result += '-';

            if (abs)
                result += "abs(";

            result += regFormatted;
            result += '.';

            switch (operand)
            {
            case VECTOR_0:
            case VECTOR_1:
            case VECTOR_2:
            {
                uint32_t mask;

                switch (instr.vectorOpcode)
                {
                case AluVectorOpcode::Dp2Add:
                    mask = (operand == VECTOR_2) ? 0b1 : 0b11;
                    break;

                case AluVectorOpcode::Dp3:
                    mask = 0b111;
                    break;

                case AluVectorOpcode::Dp4:
                case AluVectorOpcode::Max4:
                    mask = 0b1111;
                    break;

                default:
                    mask = instr.vectorWriteMask != 0 ? instr.vectorWriteMask : 0b1;
                    break;
                }

                for (size_t i = 0; i < 4; i++)
                {
                    if ((mask >> i) & 0x1)
                        result += SWIZZLES[((swizzle >> (i * 2)) + i) & 0x3];
                }

                break;
            }

            case SCALAR_0:
            case SCALAR_CONSTANT_0:
                result += SWIZZLES[((swizzle >> 6) + 3) & 0x3];
                break;

            case SCALAR_1:
            case SCALAR_CONSTANT_1:
                result += SWIZZLES[swizzle & 0x3];
                break;
            }

            if (abs)
                result += ")";

            return result;
        };

    auto emitKill = [&](const char* hlslOperator, const char* godotTest)
        {
            indent();
            if (godot())
                println("if ({}({}, {})) {{ discard; }}", godotTest, op(VECTOR_0), op(VECTOR_1));
            else
                println("clip(any({} {} {}) ? -1 : 1);", op(VECTOR_0), hlslOperator, op(VECTOR_1));
        };

    switch (instr.vectorOpcode)
    {
    case AluVectorOpcode::KillEq:
        emitKill("==", "xr_any_eq");
        break;

    case AluVectorOpcode::KillGt:
        emitKill(">", "xr_any_gt");
        break;

    case AluVectorOpcode::KillGe:
        emitKill(">=", "xr_any_ge");
        break;

    case AluVectorOpcode::KillNe:
        emitKill("!=", "xr_any_ne");
        break;
    }

    bool closeIfBracket = false;

    std::string exportRegisterStorage;
    std::string_view exportRegister;
    bool hasResolvedExportRegister = false;
    // Index of the eM# register this instruction exports to, or -1.
    int32_t memexportDataRegister = -1;
    if (instr.exportData && memexportEnabled &&
        instr.vectorDest >= uint32_t(ExportRegister::ExportAddress) &&
        instr.vectorDest <= uint32_t(ExportRegister::ExportData4))
    {
        // Memory export registers live in their own file - resolving them here
        // (instead of letting them fall through to r32..r37) is what stops the
        // export writes from being silently dropped, and stops them colliding
        // with genuine uses of GPRs 32..37.
        if (instr.vectorDest == uint32_t(ExportRegister::ExportAddress))
        {
            exportRegister = "eA";
        }
        else
        {
            memexportDataRegister =
                int32_t(instr.vectorDest) - int32_t(ExportRegister::ExportData0);
            exportRegisterStorage = fmt::format("eM{}", memexportDataRegister);
            exportRegister = exportRegisterStorage;
        }
        hasResolvedExportRegister = true;
    }
    else if (instr.exportData)
    {
        if (isPixelShader)
        {
            switch (ExportRegister(instr.vectorDest))
            {
            case ExportRegister::PSColor0:
                exportRegister = "oC0";
                hasResolvedExportRegister = true;
                break;        
            case ExportRegister::PSColor1:
                exportRegister = "oC1";
                hasResolvedExportRegister = true;
                break;        
            case ExportRegister::PSColor2:
                exportRegister = "oC2";
                hasResolvedExportRegister = true;
                break;            
            case ExportRegister::PSColor3:
                exportRegister = "oC3";
                hasResolvedExportRegister = true;
                break;           
            case ExportRegister::PSDepth:
                exportRegister = "oDepth";
                hasResolvedExportRegister = true;
                break;
            }
        }
        else
        {
            switch (ExportRegister(instr.vectorDest))
            {
            case ExportRegister::VSPosition:
                exportRegister = "oPos";
                hasResolvedExportRegister = true;

            #ifdef UNLEASHED_RECOMP
                if (hasMtxProjection)
                {
                    indent();
                    out += "if ((g_SpecConstants() & SPEC_CONSTANT_REVERSE_Z) == 0 || iterationIndex == 0)\n";
                    indent();
                    out += "{\n";
                    ++indentation;

                    closeIfBracket = true;
                }
            #endif

                break;

            case ExportRegister::VSPointSizeEdgeFlagKillVertex:
                // oPts: x the point size (a point list's sprites: stars, snow), y the edge flag,
                // z the vertex kill. Godot takes the size as POINT_SIZE.
                if (godot())
                {
                    exportRegister = "oPts";
                    hasResolvedExportRegister = true;
                    godotWritesPointSize = true;
                }
                break;

            default:
            {
                auto findResult = interpolators.find(instr.vectorDest);
                if (findResult != interpolators.end())
                {
                    exportRegisterStorage = findResult->second;
                    exportRegister = exportRegisterStorage;
                    hasResolvedExportRegister = true;
                }
                break;
            }
            }
        }
    }

    if (instr.vectorOpcode >= AluVectorOpcode::SetpEqPush && instr.vectorOpcode <= AluVectorOpcode::SetpGePush)
    {
        indent();
        print("p0 = {} == 0.0 && {} ", op(VECTOR_0), op(VECTOR_1));

        switch (instr.vectorOpcode)
        {
        case AluVectorOpcode::SetpEqPush:
            out += "==";
            break;
        case AluVectorOpcode::SetpNePush:
            out += "!=";
            break;
        case AluVectorOpcode::SetpGtPush:
            out += ">";
            break;
        case AluVectorOpcode::SetpGePush:
            out += ">=";
            break;
        }

        out += " 0.0;\n";
    }
    else if (instr.vectorOpcode >= AluVectorOpcode::MaxA)
    {
        indent();
        if (godot())
            println("a0 = int(clamp(floor(({}).w + 0.5), -256.0, 255.0));", op(VECTOR_0));
        else
            println("a0 = (int)clamp(floor(({}).w + 0.5), -256.0, 255.0);", op(VECTOR_0));
    }

    uint32_t vectorWriteMask = instr.vectorWriteMask;
    if (instr.exportData)
        vectorWriteMask &= ~instr.scalarWriteMask;

    // Depth is a scalar: HLSL tolerates `oDepth.x`, Godot's DEPTH does not.
    const bool godotScalarExport = godot() && hasResolvedExportRegister && exportRegister == "oDepth";

    if (vectorWriteMask != 0)
    {
        indent();
        if (hasResolvedExportRegister)
        {
            out += exportRegister;
            if (!godotScalarExport)
                out += '.';
        }
        else
        {
            print("r{}.", instr.vectorDest);
        }

        if (!godotScalarExport)
        {
            for (size_t i = 0; i < 4; i++)
            {
                if ((vectorWriteMask >> i) & 0x1)
                    out += SWIZZLES[i];
            }
        }

        out += " = ";

        const uint32_t vectorCount = maskCount(vectorWriteMask);

        // An export's vector write loses the components its scalar half writes, but the operands
        // were sized to the instruction's full mask; GLSL will not drop the extra components, so
        // the result is swizzled down to the positions that remain.
        const bool godotTrim = godot() && instr.vectorWriteMask != 0 && vectorWriteMask != instr.vectorWriteMask &&
            instr.vectorOpcode != AluVectorOpcode::Cube && instr.vectorOpcode != AluVectorOpcode::Dst &&
            !(instr.vectorOpcode == AluVectorOpcode::Dp4 || instr.vectorOpcode == AluVectorOpcode::Dp3 ||
              instr.vectorOpcode == AluVectorOpcode::Dp2Add || instr.vectorOpcode == AluVectorOpcode::Max4 ||
              (instr.vectorOpcode >= AluVectorOpcode::KillEq && instr.vectorOpcode <= AluVectorOpcode::KillNe));
        std::string godotTrimSwizzle;
        if (godotTrim)
        {
            uint32_t position = 0;
            for (size_t i = 0; i < 4; i++)
            {
                if ((instr.vectorWriteMask >> i) & 0x1)
                {
                    if ((vectorWriteMask >> i) & 0x1)
                        godotTrimSwizzle += SWIZZLES[position];
                    ++position;
                }
            }
            if (maskCount(instr.vectorWriteMask) == 1)
                godotTrimSwizzle.clear();
        }

        if (instr.vectorSaturate)
            out += godot() ? "clamp(" : "saturate(";

        // GLSL does not spread a scalar across a wider destination; the scalar-result ops are
        // wrapped in the destination's type.
        const bool godotBroadcast = godot() && vectorCount > 1 &&
            (instr.vectorOpcode == AluVectorOpcode::Dp4 || instr.vectorOpcode == AluVectorOpcode::Dp3 ||
             instr.vectorOpcode == AluVectorOpcode::Dp2Add || instr.vectorOpcode == AluVectorOpcode::Max4 ||
             (instr.vectorOpcode >= AluVectorOpcode::KillEq && instr.vectorOpcode <= AluVectorOpcode::KillNe));
        if (godotBroadcast)
            print("{}(", vecType(vectorCount));
        if (!godotTrimSwizzle.empty())
            out += '(';

        // Nor does it drop components: the vec4-result ops take the destination's swizzle.
        const bool godotNarrow = godot() && vectorCount < 4 &&
            (instr.vectorOpcode == AluVectorOpcode::Cube || instr.vectorOpcode == AluVectorOpcode::Dst);

        // Comparisons produce 0.0/1.0 floats; HLSL converts the bool, GLSL needs the helper.
        auto emitCompare = [&](const char* hlslOperator, const char* godotHelper)
            {
                if (godot())
                    print("{}({}, {})", godotHelper, op(VECTOR_0), op(VECTOR_1));
                else
                    print("{} {} {}", op(VECTOR_0), hlslOperator, op(VECTOR_1));
            };
        auto emitConditional = [&](const char* hlslOperator, const char* godotHelper)
            {
                if (godot())
                    print("{}({}, {}, {})", godotHelper, op(VECTOR_0), op(VECTOR_1), op(VECTOR_2));
                else
                    print("select({} {} 0.0, {}, {})", op(VECTOR_0), hlslOperator, op(VECTOR_1), op(VECTOR_2));
            };
        auto emitAnyKill = [&](const char* hlslOperator, const char* godotTest)
            {
                if (godot())
                    print("({}({}, {}) ? 1.0 : 0.0)", godotTest, op(VECTOR_0), op(VECTOR_1));
                else
                    print("any({} {} {})", op(VECTOR_0), hlslOperator, op(VECTOR_1));
            };

        switch (instr.vectorOpcode)
        {
        case AluVectorOpcode::Add:
            print("{} + {}", op(VECTOR_0), op(VECTOR_1));
            break;

        case AluVectorOpcode::Mul:
            print("{} * {}", op(VECTOR_0), op(VECTOR_1));
            break;

        case AluVectorOpcode::Max:
        case AluVectorOpcode::MaxA:
            print("max({}, {})", op(VECTOR_0), op(VECTOR_1));
            break;

        case AluVectorOpcode::Min:
            print("min({}, {})", op(VECTOR_0), op(VECTOR_1));
            break;

        case AluVectorOpcode::Seq:
            emitCompare("==", "xr_seq");
            break;

        case AluVectorOpcode::Sgt:
            emitCompare(">", "xr_sgt");
            break;

        case AluVectorOpcode::Sge:
            emitCompare(">=", "xr_sge");
            break;

        case AluVectorOpcode::Sne:
            emitCompare("!=", "xr_sne");
            break;

        case AluVectorOpcode::Frc:
            print("{}({})", godot() ? "fract" : "frac", op(VECTOR_0));
            break;

        case AluVectorOpcode::Trunc:
            print("trunc({})", op(VECTOR_0));
            break;

        case AluVectorOpcode::Floor:
            print("floor({})", op(VECTOR_0));
            break;

        case AluVectorOpcode::Mad:
            print("{} * {} + {}", op(VECTOR_0), op(VECTOR_1), op(VECTOR_2));
            break;

        case AluVectorOpcode::CndEq:
            emitConditional("==", "xr_cnd_eq");
            break;

        case AluVectorOpcode::CndGe:
            emitConditional(">=", "xr_cnd_ge");
            break;

        case AluVectorOpcode::CndGt:
            emitConditional(">", "xr_cnd_gt");
            break;

        case AluVectorOpcode::Dp4:
        case AluVectorOpcode::Dp3:
            print("dot({}, {})", op(VECTOR_0), op(VECTOR_1));
            break;

        case AluVectorOpcode::Dp2Add:
            print("dot({}, {}) + {}", op(VECTOR_0), op(VECTOR_1), op(VECTOR_2));
            break;

        case AluVectorOpcode::Cube:
            print("{}cube(r{}, cubeMapData)", godot() ? "xr_" : "", instr.src1Register);
            break;

        case AluVectorOpcode::Max4:
            print("{}max4({})", godot() ? "xr_" : "", op(VECTOR_0));
            break;

        case AluVectorOpcode::SetpEqPush:
        case AluVectorOpcode::SetpNePush:
        case AluVectorOpcode::SetpGtPush:
        case AluVectorOpcode::SetpGePush:
            if (godot() && vectorCount > 1)
                print("(p0 ? {}(0.0) : ({} + 1.0))", vecType(vectorCount), op(VECTOR_0));
            else
                print("p0 ? 0.0 : {} + 1.0", op(VECTOR_0));
            break;

        case AluVectorOpcode::KillEq:
            emitAnyKill("==", "xr_any_eq");
            break;

        case AluVectorOpcode::KillGt:
            emitAnyKill(">", "xr_any_gt");
            break;

        case AluVectorOpcode::KillGe:
            emitAnyKill(">=", "xr_any_ge");
            break;

        case AluVectorOpcode::KillNe:
            emitAnyKill("!=", "xr_any_ne");
            break;

        case AluVectorOpcode::Dst:
            print("{}dst({}, {})", godot() ? "xr_" : "", op(VECTOR_0), op(VECTOR_1));
            break;
        }

        if (!godotTrimSwizzle.empty())
        {
            out += ").";
            out += godotTrimSwizzle;
        }

        if (godotBroadcast)
            out += ')';

        if (godotNarrow)
        {
            out += '.';
            for (size_t i = 0; i < 4; i++)
            {
                if ((vectorWriteMask >> i) & 0x1)
                    out += SWIZZLES[i];
            }
        }

        if (instr.vectorSaturate)
            out += godot() ? ", 0.0, 1.0)" : ")";

        out += ";\n";
    }

    if (instr.scalarOpcode != AluScalarOpcode::RetainPrev)
    {
        if (instr.scalarOpcode >= AluScalarOpcode::SetpEq && instr.scalarOpcode <= AluScalarOpcode::SetpRstr)
        {
            indent();
            out += "p0 = ";

            switch (instr.scalarOpcode)
            {
            case AluScalarOpcode::SetpEq:
                print("{} == 0.0", op(SCALAR_0));
                break;

            case AluScalarOpcode::SetpNe:
                print("{} != 0.0", op(SCALAR_0));
                break;

            case AluScalarOpcode::SetpGt:
                print("{} > 0.0", op(SCALAR_0));
                break;

            case AluScalarOpcode::SetpGe:
                print("{} >= 0.0", op(SCALAR_0));
                break;

            case AluScalarOpcode::SetpInv:
                print("{} == 1.0", op(SCALAR_0));
                break;

            case AluScalarOpcode::SetpPop:
                print("{} - 1.0 <= 0.0", op(SCALAR_0));
                break;

            case AluScalarOpcode::SetpClr:
                out += "false";
                break;

            case AluScalarOpcode::SetpRstr:
                print("{} == 0.0", op(SCALAR_0));
                break;
            }

            out += ";\n";
        }

        indent();
        out += "ps = ";
        if (instr.scalarSaturate)
            out += godot() ? "clamp(" : "saturate(";

        // Scalar comparisons as 0.0/1.0, and the tests that kill through `ps`.
        auto emitScalarCompare = [&](const char* hlslOperator, const char* godotHelper, const char* rhs)
            {
                if (godot())
                    print("{}({}, {})", godotHelper, op(SCALAR_0), rhs);
                else
                    print("{} {} {}", op(SCALAR_0), hlslOperator, rhs);
            };

        switch (instr.scalarOpcode)
        {
        case AluScalarOpcode::Adds:
            print("{} + {}", op(SCALAR_0), op(SCALAR_1));
            break;

        case AluScalarOpcode::AddsPrev:
            print("{} + ps", op(SCALAR_0));
            break;

        case AluScalarOpcode::Muls:
            print("{} * {}", op(SCALAR_0), op(SCALAR_1));
            break;

        case AluScalarOpcode::MulsPrev:
        case AluScalarOpcode::MulsPrev2:
            print("{} * ps", op(SCALAR_0));
            break;

        case AluScalarOpcode::Maxs:
        case AluScalarOpcode::MaxAs:
        case AluScalarOpcode::MaxAsf:
            print("max({}, {})", op(SCALAR_0), op(SCALAR_1));
            break;

        case AluScalarOpcode::Mins:
            print("min({}, {})", op(SCALAR_0), op(SCALAR_1));
            break;

        case AluScalarOpcode::Seqs:
            emitScalarCompare("==", "xr_seq", "0.0");
            break;

        case AluScalarOpcode::Sgts:
            emitScalarCompare(">", "xr_sgt", "0.0");
            break;

        case AluScalarOpcode::Sges:
            emitScalarCompare(">=", "xr_sge", "0.0");
            break;

        case AluScalarOpcode::Snes:
            emitScalarCompare("!=", "xr_sne", "0.0");
            break;

        case AluScalarOpcode::Frcs:
            print("{}({})", godot() ? "fract" : "frac", op(SCALAR_0));
            break;

        case AluScalarOpcode::Truncs:
            print("trunc({})", op(SCALAR_0));
            break;

        case AluScalarOpcode::Floors:
            print("floor({})", op(SCALAR_0));
            break;

        case AluScalarOpcode::Exp:
            print("exp2({})", op(SCALAR_0));
            break;

        case AluScalarOpcode::Logc:
        case AluScalarOpcode::Log:
            print("clamp(log2({}), FLT_MIN, FLT_MAX)", op(SCALAR_0));
            break;

        case AluScalarOpcode::Rcpc:
        case AluScalarOpcode::Rcpf:
        case AluScalarOpcode::Rcp:
            print("clamp({}({}), FLT_MIN, FLT_MAX)", godot() ? "xr_rcp" : "rcp", op(SCALAR_0));
            break;

        case AluScalarOpcode::Rsqc:
        case AluScalarOpcode::Rsqf:
        case AluScalarOpcode::Rsq:
            print("clamp({}({}), FLT_MIN, FLT_MAX)", godot() ? "inversesqrt" : "rsqrt", op(SCALAR_0));
            break;

        case AluScalarOpcode::Subs:
            print("{} - {}", op(SCALAR_0), op(SCALAR_1));
            break;

        case AluScalarOpcode::SubsPrev:
            print("{} - ps", op(SCALAR_0));
            break;

        case AluScalarOpcode::SetpEq:
        case AluScalarOpcode::SetpNe:
        case AluScalarOpcode::SetpGt:
        case AluScalarOpcode::SetpGe:
            out += "p0 ? 0.0 : 1.0";
            break;

        case AluScalarOpcode::SetpInv:
            print("{0} == 0.0 ? 1.0 : {0}", op(SCALAR_0));
            break;

        case AluScalarOpcode::SetpPop:
            print("p0 ? 0.0 : ({} - 1.0)", op(SCALAR_0));
            break;

        case AluScalarOpcode::SetpClr:
            out += "FLT_MAX";
            break;

        case AluScalarOpcode::SetpRstr:
            print("p0 ? 0.0 : {}", op(SCALAR_0));
            break;

        case AluScalarOpcode::KillsEq:
            emitScalarCompare("==", "xr_seq", "0.0");
            break;

        case AluScalarOpcode::KillsGt:
            emitScalarCompare(">", "xr_sgt", "0.0");
            break;

        case AluScalarOpcode::KillsGe:
            emitScalarCompare(">=", "xr_sge", "0.0");
            break;

        case AluScalarOpcode::KillsNe:
            emitScalarCompare("!=", "xr_sne", "0.0");
            break;

        case AluScalarOpcode::KillsOne:
            emitScalarCompare("==", "xr_seq", "1.0");
            break;

        case AluScalarOpcode::Sqrt:
            print("sqrt({})", op(SCALAR_0));
            break;

        case AluScalarOpcode::Mulsc0:
        case AluScalarOpcode::Mulsc1:
            print("{} * {}", op(SCALAR_CONSTANT_0), op(SCALAR_CONSTANT_1));
            break;

        case AluScalarOpcode::Addsc0:
        case AluScalarOpcode::Addsc1:
            print("{} + {}", op(SCALAR_CONSTANT_0), op(SCALAR_CONSTANT_1));
            break;

        case AluScalarOpcode::Subsc0:
        case AluScalarOpcode::Subsc1:
            print("{} - {}", op(SCALAR_CONSTANT_0), op(SCALAR_CONSTANT_1));
            break;

        case AluScalarOpcode::Sin:
            print("sin({})", op(SCALAR_0));
            break;

        case AluScalarOpcode::Cos:
            print("cos({})", op(SCALAR_0));
            break;
        }

        if (instr.scalarSaturate)
            out += godot() ? ", 0.0, 1.0)" : ")";

        out += ";\n";

        switch (instr.scalarOpcode)
        {
        case AluScalarOpcode::MaxAs:
            indent();
            if (godot())
                println("a0 = int(clamp(floor({} + 0.5), -256.0, 255.0));", op(SCALAR_0));
            else
                println("a0 = (int)clamp(floor({} + 0.5), -256.0, 255.0);", op(SCALAR_0));
            break;
        case AluScalarOpcode::MaxAsf:
            indent();
            if (godot())
                println("a0 = int(clamp(floor({}), -256.0, 255.0));", op(SCALAR_0));
            else
                println("a0 = (int)clamp(floor({}), -256.0, 255.0);", op(SCALAR_0));
            break;
        }
    }

    uint32_t scalarWriteMask = instr.scalarWriteMask;
    if (instr.exportData)
        scalarWriteMask &= ~instr.vectorWriteMask;

    if (scalarWriteMask != 0)
    {
        indent();
        if (hasResolvedExportRegister)
        {
            out += exportRegister;
            if (!godotScalarExport)
                out += '.';
        }
        else
        {
            print("r{}.", instr.scalarDest);
        }

        if (!godotScalarExport)
        {
            for (size_t i = 0; i < 4; i++)
            {
                if ((scalarWriteMask >> i) & 0x1)
                    out += SWIZZLES[i];
            }
        }

        const uint32_t scalarCount = godotScalarExport ? 1 : maskCount(scalarWriteMask);
        if (godot() && scalarCount > 1)
            println(" = {}(ps);", vecType(scalarCount));
        else
            out += " = ps;\n";
    }

    if (instr.exportData && hasResolvedExportRegister && !godotScalarExport)
    {
        uint32_t zeroMask = instr.scalarDestRelative ? (0b1111 & ~(instr.vectorWriteMask | instr.scalarWriteMask)) : 0;
        uint32_t oneMask = instr.vectorWriteMask & instr.scalarWriteMask;

        for (size_t i = 0; i < 4; i++)
        {
            uint32_t mask = 1 << i;
            if (zeroMask & mask)
            {
                indent();
                println("{}.{} = 0.0;", exportRegister, SWIZZLES[i]);
            }
            else if (oneMask & mask)
            {
                indent();
                println("{}.{} = 1.0;", exportRegister, SWIZZLES[i]);
            }
        }
    }

    if (memexportDataRegister >= 0)
    {
        // Track which eM# were written by this invocation - the write can sit
        // inside a predicate or a conditional exec, so the mask has to be a
        // runtime value (the SDK keeps the same bitfield in
        // system_temp_memexport_enabled_and_eM_written_.y).
        indent();
        println("eMWritten |= {}u;", 1u << uint32_t(memexportDataRegister));
    }

    if (instr.scalarOpcode >= AluScalarOpcode::KillsEq && instr.scalarOpcode <= AluScalarOpcode::KillsOne)
    {
        indent();
        out += godot() ? "if (ps != 0.0) { discard; }\n" : "clip(ps != 0.0 ? -1 : 1);\n";
    }

    if (closeIfBracket)
    {
        --indentation;
        indent();
        out += "}\n";
    }

    if (instr.isPredicated)
    {
        --indentation;
        indent();
        out += "}\n";
    }
}

// Scans the control flow / ALU stream for Xenos memory exports before any code
// is emitted, so the prologue knows whether to declare eA/eM# and pull in the
// memexport runtime.
//
// Mirrors rexglue-sdk Shader::GatherAluInstructionInformation() and
// ParsedAluInstruction::GetMemExportStreamConstant()
// (rexglue-sdk/src/graphics/pipeline/shader/translator.cpp:500 and :1413).
void ShaderRecompiler::analyzeMemoryExports(const uint8_t* shaderData)
{
    const auto shaderContainer = reinterpret_cast<const ShaderContainer*>(shaderData);
    const auto shader = reinterpret_cast<const Shader*>(shaderData + shaderContainer->shaderOffset);
    const be<uint32_t>* code = reinterpret_cast<const be<uint32_t>*>(
        shaderData + shaderContainer->virtualSize + shader->physicalOffset);

    union
    {
        ControlFlowInstruction controlFlow[2];
        struct
        {
            uint32_t code0;
            uint32_t code1;
            uint32_t code2;
            uint32_t code3;
        };
    };

    // Clamp the control flow region exactly the way the emitting pass does, so
    // the ALU scan below never walks off into instruction memory.
    auto controlFlowCode = code;
    uint32_t instrAddress = 0;
    uint32_t instrSize = shader->size;

    while (instrAddress < instrSize)
    {
        code0 = controlFlowCode[0];
        code1 = controlFlowCode[1] & 0xFFFF;
        code2 = (controlFlowCode[1] >> 16) | (controlFlowCode[2] << 16);
        code3 = controlFlowCode[2] >> 16;

        for (auto& cfInstr : controlFlow)
        {
            uint32_t address = 0;

            switch (cfInstr.opcode)
            {
            case ControlFlowOpcode::Exec:
            case ControlFlowOpcode::ExecEnd:
                address = cfInstr.exec.address;
                break;

            case ControlFlowOpcode::CondExec:
            case ControlFlowOpcode::CondExecEnd:
            case ControlFlowOpcode::CondExecPredClean:
            case ControlFlowOpcode::CondExecPredCleanEnd:
                address = cfInstr.condExec.address;
                break;

            case ControlFlowOpcode::CondExecPred:
            case ControlFlowOpcode::CondExecPredEnd:
                address = cfInstr.condExecPred.address;
                break;
            }

            if (address != 0)
                instrSize = std::min<uint32_t>(instrSize, address * 12);
        }

        controlFlowCode += 3;
        instrAddress += 12;
    }

    controlFlowCode = code;
    instrAddress = 0;

    while (instrAddress < instrSize)
    {
        code0 = controlFlowCode[0];
        code1 = controlFlowCode[1] & 0xFFFF;
        code2 = (controlFlowCode[1] >> 16) | (controlFlowCode[2] << 16);
        code3 = controlFlowCode[2] >> 16;

        for (auto& cfInstr : controlFlow)
        {
            uint32_t address = 0;
            uint32_t count = 0;
            uint32_t sequence = 0;

            switch (cfInstr.opcode)
            {
            case ControlFlowOpcode::Exec:
            case ControlFlowOpcode::ExecEnd:
                address = cfInstr.exec.address;
                count = cfInstr.exec.count;
                sequence = cfInstr.exec.sequence;
                break;

            case ControlFlowOpcode::CondExec:
            case ControlFlowOpcode::CondExecEnd:
            case ControlFlowOpcode::CondExecPredClean:
            case ControlFlowOpcode::CondExecPredCleanEnd:
                address = cfInstr.condExec.address;
                count = cfInstr.condExec.count;
                sequence = cfInstr.condExec.sequence;
                break;

            case ControlFlowOpcode::CondExecPred:
            case ControlFlowOpcode::CondExecPredEnd:
                address = cfInstr.condExecPred.address;
                count = cfInstr.condExecPred.count;
                sequence = cfInstr.condExecPred.sequence;
                break;
            }

            auto instructionCode = code + address * 3;

            for (uint32_t i = 0; i < count; i++)
            {
                union
                {
                    AluInstruction alu;
                    struct
                    {
                        uint32_t aluCode0;
                        uint32_t aluCode1;
                        uint32_t aluCode2;
                    };
                };

                aluCode0 = instructionCode[0];
                aluCode1 = instructionCode[1];
                aluCode2 = instructionCode[2];

                // Only ALU slots can export; fetch slots cannot.
                if ((sequence & 0x1) == 0 && alu.exportData)
                {
                    const uint32_t dest = alu.vectorDest;
                    if (dest == uint32_t(ExportRegister::ExportAddress))
                    {
                        // `mad eA, r#, const0100, c#` - the third operand is the
                        // stream float4 constant. Every condition here comes from
                        // GetMemExportStreamConstant().
                        const bool isStreamConstantIdiom =
                            alu.vectorOpcode == AluVectorOpcode::Mad &&
                            alu.vectorWriteMask == 0b1111 &&
                            !alu.vectorSaturate &&
                            !alu.src3Select &&                  // a float constant, not a GPR
                            !alu.src3Negate &&
                            !alu.absConstants &&
                            (alu.src3Swizzle & 0xFF) == 0 &&    // standard .xyzw swizzle
                            !alu.const0Relative &&
                            !alu.const1Relative;

                        if (isStreamConstantIdiom)
                        {
                            const uint32_t streamConstant = alu.src3Register;
                            if (std::find(memexportStreamConstants.begin(),
                                          memexportStreamConstants.end(),
                                          streamConstant) == memexportStreamConstants.end())
                            {
                                memexportStreamConstants.push_back(streamConstant);
                            }
                        }
                        else
                        {
                            // Still exported at runtime (eA is decoded in the
                            // shader), but the destination cannot be validated
                            // ahead of time. Surfaced in the translator report.
                            ++memexportUnresolvedAddressWrites;
                        }
                    }
                    else if (dest >= uint32_t(ExportRegister::ExportData0) &&
                             dest <= uint32_t(ExportRegister::ExportData4))
                    {
                        memexportEMWrittenMask |=
                            1u << (dest - uint32_t(ExportRegister::ExportData0));
                    }
                }

                sequence >>= 2;
                instructionCode += 3;
            }
        }

        controlFlowCode += 3;
        instrAddress += 12;
    }

    // The SDK gates the whole memexport path on eM# actually being written -
    // an eA write on its own exports nothing.
    memexportEnabled = memexportEMWrittenMask != 0;
}

// Writes out the eM# registers accumulated since the last `alloc` (or since the
// start of the shader). Mirrors DxbcShaderTranslator::ProcessAllocInstruction /
// the memexport tail of CompleteShaderCode.
void ShaderRecompiler::emitMemoryExportFlush()
{
    if (!memexportEnabled)
        return;

    indent();
    out += "memexportFlush(g_MemoryExportWindowBase, g_MemoryExportWindowBytes, "
           "eA, eMWritten, eM0, eM1, eM2, eM3, eM4);\n";
    indent();
    out += "eMWritten = 0u;\n";
}

void ShaderRecompiler::recompile(const uint8_t* shaderData, const std::string_view& include)
{
    const auto shaderContainer = reinterpret_cast<const ShaderContainer*>(shaderData);

    assert((shaderContainer->flags & 0xFFFFFF00) == 0x102A1100);
    assert(shaderContainer->constantTableOffset != NULL);

    isPixelShader = (shaderContainer->flags & 0x1) == 0;

    analyzeMemoryExports(shaderData);

    if (godot() && memexportEnabled)
    {
        // Writes to memory from a shader have no place in a Godot material; the stub keeps the
        // artifact set complete and the report names the reason.
        recompileGodotUnsupported("it exports to memory (Xenos memexport), which a Godot material cannot do");
        return;
    }

    if (!godot())
    {
        println("#define REFII_MEMEXPORT_ENABLED {}", memexportEnabled ? 1 : 0);
        println("#define REFII_MEMEXPORT_STREAMS {}",
            memexportEnabled ? uint32_t(memexportStreamConstants.size()) : 0u);
        println("#define REFII_SHADER_STAGE_PIXEL {}", isPixelShader ? 1 : 0);
        out += include;
        out += "\n#undef REFII_SHADER_STAGE_PIXEL\n\n";
    }

    const auto constantTableContainer = reinterpret_cast<const ConstantTableContainer*>(shaderData + shaderContainer->constantTableOffset);
    constantTableData = reinterpret_cast<const uint8_t*>(&constantTableContainer->constantTable);

    if (!godot())
        out += "#ifdef __spirv__\n\n";

#ifdef UNLEASHED_RECOMP
    bool isMetaInstancer = false;
    bool hasIndexCount = false;
#endif

    for (uint32_t i = 0; i < constantTableContainer->constantTable.constants; i++)
    {
        const auto constantInfo = reinterpret_cast<const ConstantInfo*>(
            constantTableData + constantTableContainer->constantTable.constantInfo + i * sizeof(ConstantInfo));

        const char* constantName = reinterpret_cast<const char*>(constantTableData + constantInfo->name);

    #ifdef UNLEASHED_RECOMP
        if (!isPixelShader)
        {
            if (strcmp(constantName, "g_MtxProjection") == 0)
                hasMtxProjection = true;
            else if (strcmp(constantName, "g_InstanceTypes") == 0)
                isMetaInstancer = true;
            else if (strcmp(constantName, "g_IndexCount") == 0)
                hasIndexCount = true;
        }
        else
        {
            if (strcmp(constantName, "g_MtxPrevInvViewProjection") == 0)
                hasMtxPrevInvViewProjection = true;
        }
    #endif

        switch (constantInfo->registerSet)
        {
        case RegisterSet::Float4:
        {
            const char* shaderName = isPixelShader ? "Pixel" : "Vertex";

            if (godot())
            {
                // Declared in the header, from what the body used.
            }
            else if (constantInfo->registerCount > 1)
            {
                uint32_t tailCount = (isPixelShader ? 224 : 256) - constantInfo->registerIndex;

                println("#define {}(INDEX) select((INDEX) < {}, vk::RawBufferLoad<float4>(g_PushConstants.{}ShaderConstants + ({} + min(INDEX, {})) * 16, 0x10), 0.0)",
                    constantName, tailCount, shaderName, constantInfo->registerIndex.get(), tailCount - 1);
            }
            else
            {
                println("#define {} vk::RawBufferLoad<float4>(g_PushConstants.{}ShaderConstants + {}, 0x10)",
                    constantName, shaderName, constantInfo->registerIndex * 16);
            }

            for (uint16_t j = 0; j < constantInfo->registerCount; j++)
                float4Constants.emplace(constantInfo->registerIndex + j, constantInfo);

            break;
        }

        case RegisterSet::Sampler:
        {
            if (!godot())
            {
                for (size_t j = 0; j < std::size(TEXTURE_DIMENSIONS); j++)
                {
                    println("#define {}_Texture{}DescriptorIndex vk::RawBufferLoad<uint>(g_PushConstants.SharedConstants + {})",
                        constantName, TEXTURE_DIMENSIONS[j],
                        j * TEXTURE_DESCRIPTOR_BYTES + constantInfo->registerIndex * sizeof(uint32_t));
                }
                println("#define {}_Texture1DDescriptorIndex {}_Texture2DDescriptorIndex",
                    constantName, constantName);

                println("#define {}_SamplerDescriptorIndex vk::RawBufferLoad<uint>(g_PushConstants.SharedConstants + {})",
                    constantName,
                    std::size(TEXTURE_DIMENSIONS) * TEXTURE_DESCRIPTOR_BYTES +
                        constantInfo->registerIndex * sizeof(uint32_t));
            }

            samplers.emplace(constantInfo->registerIndex, constantName);
            break;
        }

        }
    }

    // The HLSL binding surface: descriptor-heap indices for SPIR-V, constant buffers otherwise.
    // Godot binds uniforms by name, so none of it applies there.
    if (!godot())
    {
    for (uint32_t registerIndex = 0; registerIndex < 32; ++registerIndex)
    {
        for (size_t dimensionIndex = 0; dimensionIndex < std::size(TEXTURE_DIMENSIONS); ++dimensionIndex)
        {
            println("#define s{}_Texture{}DescriptorIndex vk::RawBufferLoad<uint>(g_PushConstants.SharedConstants + {})",
                registerIndex,
                TEXTURE_DIMENSIONS[dimensionIndex],
                dimensionIndex * TEXTURE_DESCRIPTOR_BYTES +
                    registerIndex * sizeof(uint32_t));
        }
        println("#define s{}_Texture1DDescriptorIndex s{}_Texture2DDescriptorIndex",
            registerIndex, registerIndex);
        println("#define s{}_SamplerDescriptorIndex vk::RawBufferLoad<uint>(g_PushConstants.SharedConstants + {})",
            registerIndex,
            std::size(TEXTURE_DIMENSIONS) * TEXTURE_DESCRIPTOR_BYTES +
                registerIndex * sizeof(uint32_t));
    }

    out += "\n#else\n\n";

    println("cbuffer {}ShaderConstants : register(b{}, space4)", isPixelShader ? "Pixel" : "Vertex", isPixelShader ? 1 : 0);
    out += "{\n";

    for (uint32_t i = 0; i < constantTableContainer->constantTable.constants; i++)
    {
        const auto constantInfo = reinterpret_cast<const ConstantInfo*>(
            constantTableData + constantTableContainer->constantTable.constantInfo + i * sizeof(ConstantInfo));

        if (constantInfo->registerSet == RegisterSet::Float4)
        {
            const char* constantName = reinterpret_cast<const char*>(constantTableData + constantInfo->name);

            print("\tfloat4 {}", constantName);

            if (constantInfo->registerCount > 1)
                print("[{}]", constantInfo->registerCount.get());

            println(" : packoffset(c{});", constantInfo->registerIndex.get());

            if (constantInfo->registerCount > 1)
            {
                uint32_t tailCount = (isPixelShader ? 224 : 256) - constantInfo->registerIndex;
                println("#define {0}(INDEX) select((INDEX) < {1}, {0}[min(INDEX, {2})], 0.0)", constantName, tailCount, tailCount - 1);
            }
        }
    }

    out += "};\n\n";

    out += "cbuffer SharedConstants : register(b2, space4)\n";
    out += "{\n";

    for (size_t j = 0; j < std::size(TEXTURE_DIMENSIONS); j++)
    {
        println("\tuint4 g_Texture{}DescriptorIndexRows[8] : packoffset(c{});",
            TEXTURE_DIMENSIONS[j], j * 8);
    }
    println("\tuint4 g_SamplerDescriptorIndexRows[8] : packoffset(c{});",
        8 * std::size(TEXTURE_DIMENSIONS));

    for (uint32_t registerIndex = 0; registerIndex < 32; ++registerIndex)
    {
        for (size_t dimensionIndex = 0; dimensionIndex < std::size(TEXTURE_DIMENSIONS); ++dimensionIndex)
        {
            println("#define s{}_Texture{}DescriptorIndex g_Texture{}DescriptorIndexRows[{}].{}",
                registerIndex,
                TEXTURE_DIMENSIONS[dimensionIndex],
                TEXTURE_DIMENSIONS[dimensionIndex],
                registerIndex / 4,
                SWIZZLES[registerIndex % 4]);
        }
        println("#define s{}_Texture1DDescriptorIndex s{}_Texture2DDescriptorIndex",
            registerIndex, registerIndex);
        println("#define s{}_SamplerDescriptorIndex g_SamplerDescriptorIndexRows[{}].{}",
            registerIndex,
            registerIndex / 4,
            SWIZZLES[registerIndex % 4]);
    }

    for (uint32_t i = 0; i < constantTableContainer->constantTable.constants; i++)
    {
        const auto constantInfo = reinterpret_cast<const ConstantInfo*>(
            constantTableData + constantTableContainer->constantTable.constantInfo + i * sizeof(ConstantInfo));

        if (constantInfo->registerSet == RegisterSet::Sampler)
        {
            const char* constantName = reinterpret_cast<const char*>(constantTableData + constantInfo->name);

            for (size_t j = 0; j < std::size(TEXTURE_DIMENSIONS); j++)
            {
                println("#define {}_Texture{}DescriptorIndex s{}_Texture{}DescriptorIndex",
                    constantName, TEXTURE_DIMENSIONS[j],
                    constantInfo->registerIndex.get(), TEXTURE_DIMENSIONS[j]);
            }
            println("#define {}_Texture1DDescriptorIndex {}_Texture2DDescriptorIndex",
                constantName, constantName);

            println("#define {}_SamplerDescriptorIndex s{}_SamplerDescriptorIndex",
                constantName, constantInfo->registerIndex.get());
        }
    }

    out += "\tDEFINE_SHARED_CONSTANTS();\n";
    if (memexportEnabled)
    {
        // Only exporting shaders get the extra cbuffer fields, so every other
        // blob's generated HLSL is byte-identical to before.
        out += "\tDEFINE_MEMEXPORT_SHARED_CONSTANTS();\n";
    }
    if (!isPixelShader)
    {
        // D3D12 mirror of the tessellation slots (c35). Guarded so the normal
        // variant's cbuffer is unchanged; only the .tess.<target>.bin compile
        // (dxc -D REFII_TESSELLATION_VARIANT=1) sees the extra fields.
        out += "#if REFII_TESSELLATION_VARIANT\n";
        out += "\tDEFINE_TESSELLATION_SHARED_CONSTANTS();\n";
        out += "#endif\n";
    }
    out += "};\n\n";

    out += "#endif\n";
    }

    for (uint32_t i = 0; i < constantTableContainer->constantTable.constants; i++)
    {
        const auto constantInfo = reinterpret_cast<const ConstantInfo*>(
            constantTableData + constantTableContainer->constantTable.constantInfo + i * sizeof(ConstantInfo));

        if (constantInfo->registerSet == RegisterSet::Bool)
        {
            const char* constantName = reinterpret_cast<const char*>(constantTableData + constantInfo->name);
            if (!godot())
                println("\t#define {} (1 << {})", constantName, constantInfo->registerIndex + (isPixelShader ? 16 : 0));
            boolConstants.emplace(constantInfo->registerIndex, constantName);
        }
    }

    out += '\n';

    const auto shader = reinterpret_cast<const Shader*>(shaderData + shaderContainer->shaderOffset);

    if (godot())
    {
        // Godot's processor function; the header, assembled at the end, goes in front of it.
        out += isPixelShader ? "void fragment()\n" : "void vertex()\n";
    }
    else
    {
        out += "#ifndef __spirv__\n";

        if (isPixelShader)
            out += "[shader(\"pixel\")]\n";
        else
            out += "[shader(\"vertex\")]\n";

        out += "#endif\n";

        out += "void main(\n";
    }

    if (isPixelShader)
    {
        auto pixelShader = reinterpret_cast<const PixelShader*>(shader);
        godotWritesColor0 = (pixelShader->outputs & PIXEL_SHADER_OUTPUT_COLOR0) != 0;
        godotWritesDepth = (pixelShader->outputs & PIXEL_SHADER_OUTPUT_DEPTH) != 0;
        godotWritesExtraTargets = (pixelShader->outputs &
            (PIXEL_SHADER_OUTPUT_COLOR1 | PIXEL_SHADER_OUTPUT_COLOR2 | PIXEL_SHADER_OUTPUT_COLOR3)) != 0;

        if (!godot())
        {
            out += "\tin float4 iPos : SV_Position,\n";

            for (auto& [usage, usageIndex] : INTERPOLATORS)
                println("\tin float4 i{0}{1} : {2}{1},", USAGE_VARIABLES[uint32_t(usage)], usageIndex, USAGE_SEMANTICS[uint32_t(usage)]);

            out += "#ifdef __spirv__\n";
            out += "\tin bool iFace : SV_IsFrontFace\n";
            out += "#else\n";
            out += "\tin uint iFace : SV_IsFrontFace\n";
            out += "#endif\n";

            if (pixelShader->outputs & PIXEL_SHADER_OUTPUT_COLOR0)
                out += ",\n\tout float4 oC0 : SV_Target0";
            if (pixelShader->outputs & PIXEL_SHADER_OUTPUT_COLOR1)
                out += ",\n\tout float4 oC1 : SV_Target1";
            if (pixelShader->outputs & PIXEL_SHADER_OUTPUT_COLOR2)
                out += ",\n\tout float4 oC2 : SV_Target2";
            if (pixelShader->outputs & PIXEL_SHADER_OUTPUT_COLOR3)
                out += ",\n\tout float4 oC3 : SV_Target3";
            if (pixelShader->outputs & PIXEL_SHADER_OUTPUT_DEPTH)
                out += ",\n\tout float oDepth : SV_Depth";
        }
    }
    else
    {
        auto vertexShader = reinterpret_cast<const VertexShader*>(shader);
        bool emitVkLocations = true;
        bool usedVkLocations[64]{};
        bool seenVertexInputForLocation[std::size(USAGE_VARIABLES)][16]{};

        for (uint32_t i = 0; i < vertexShader->vertexElementCount; i++)
        {
            union
            {
                VertexElement vertexElement;
                uint32_t value;
            };

            value = vertexShader->vertexElementsAndInterpolators[vertexShader->field18 + i];
            const uint32_t usageValue = uint32_t(vertexElement.usage);
            const uint32_t usageIndexValue = uint32_t(vertexElement.usageIndex);

            if (usageValue < std::size(USAGE_VARIABLES) &&
                usageIndexValue < std::size(seenVertexInputForLocation[0]))
            {
                if (seenVertexInputForLocation[usageValue][usageIndexValue])
                {
                    continue;
                }
                seenVertexInputForLocation[usageValue][usageIndexValue] = true;
            }

            int location = -1;
            for (auto& usageLocation : USAGE_LOCATIONS)
            {
                if (usageLocation.usage == vertexElement.usage &&
                    usageLocation.usageIndex == vertexElement.usageIndex)
                {
                    location = static_cast<int>(usageLocation.location);
                    break;
                }
            }

            if (location < 0 || location >= 64)
            {
                emitVkLocations = false;
                continue;
            }
            if (usedVkLocations[location])
            {
                emitVkLocations = false;
                continue;
            }
            usedVkLocations[location] = true;
        }

        bool declaredVertexInput[std::size(USAGE_VARIABLES)][16]{};
        for (uint32_t i = 0; i < vertexShader->vertexElementCount; i++)
        {
            union
            {
                VertexElement vertexElement;
                uint32_t value;
            };

            value = vertexShader->vertexElementsAndInterpolators[vertexShader->field18 + i];

            const char* usageType = USAGE_TYPES[uint32_t(vertexElement.usage)];

        #ifdef UNLEASHED_RECOMP
            if ((vertexElement.usage == DeclUsage::TexCoord && vertexElement.usageIndex == 2 && isMetaInstancer) ||
                (vertexElement.usage == DeclUsage::Position && vertexElement.usageIndex == 1))
            {
                usageType = "uint4";
            }
            #endif

            const uint32_t usageValue = uint32_t(vertexElement.usage);
            const uint32_t usageIndexValue = uint32_t(vertexElement.usageIndex);
            bool shouldDeclareInput = true;
            if (usageValue < std::size(declaredVertexInput) &&
                usageIndexValue < std::size(declaredVertexInput[0]))
            {
                shouldDeclareInput = !declaredVertexInput[usageValue][usageIndexValue];
            }

            if (shouldDeclareInput && !godot())
            {
                out += '\t';

                if (emitVkLocations)
                {
                    for (auto& usageLocation : USAGE_LOCATIONS)
                    {
                        if (usageLocation.usage == vertexElement.usage &&
                            usageLocation.usageIndex == vertexElement.usageIndex)
                        {
                            print("[[vk::location({})]] ", usageLocation.location);
                            break;
                        }
                    }
                }

                println("in {0} i{1}{2} : {3}{2},", usageType, USAGE_VARIABLES[usageValue],
                    usageIndexValue, USAGE_SEMANTICS[usageValue]);

                if (usageValue < std::size(declaredVertexInput) &&
                    usageIndexValue < std::size(declaredVertexInput[0]))
                {
                    declaredVertexInput[usageValue][usageIndexValue] = true;
                }
            }

            vertexElements.emplace(uint32_t(vertexElement.address), vertexElement);
        }

    #ifdef UNLEASHED_RECOMP
        if (hasIndexCount)
        {
            out += "\tin uint iVertexId : SV_VertexID,\n";
            out += "\tin uint iInstanceId : SV_InstanceID,\n";
        }
    #endif

        if (!godot())
        {
            // Tessellation-variant inputs (see REFII_TESSELLATION_VARIANT in
            // shader_common.h). SPIR-V/DXIL builtins, so no vk::location or input
            // slot is consumed; the normal variant's signature is unchanged.
            out += "#if REFII_TESSELLATION_VARIANT\n";
            out += "\tin uint iVertexId : SV_VertexID,\n";
            out += "\tin uint iInstanceId : SV_InstanceID,\n";
            out += "#endif\n";

            out += "\tout float4 oPos : SV_Position";

            for (auto& [usage, usageIndex] : INTERPOLATORS)
                print(",\n\tout float4 o{0}{1} : {2}{1}", USAGE_VARIABLES[uint32_t(usage)], usageIndex, USAGE_SEMANTICS[uint32_t(usage)]);
        }
    }

    if (!godot())
        out += ")\n";
    out += "{\n";

#ifdef UNLEASHED_RECOMP
    if (hasMtxProjection)
    {
        specConstantsMask |= SPEC_CONSTANT_REVERSE_Z;

        out += "\toPos = 0.0;\n";

        out += "\tfloat4x4 mtxProjection = float4x4(g_MtxProjection(0), g_MtxProjection(1), g_MtxProjection(2), g_MtxProjection(3));\n";
        out += "\tfloat4x4 mtxProjectionReverseZ = mul(mtxProjection, float4x4(1, 0, 0, 0, 0, 1, 0, 0, 0, 0, -1, 0, 0, 0, 1, 1));\n";

        out += "\t[unroll] for (int iterationIndex = 0; iterationIndex < 2; iterationIndex++)\n";
        out += "\t{\n";
    }
#endif

    bool declaredIntConstants[32]{};
    int32_t intConstantDefaults[32][4]{};
    if (shaderContainer->definitionTableOffset != NULL)
    {
        auto definitionTable = reinterpret_cast<const DefinitionTable*>(shaderData + shaderContainer->definitionTableOffset);
        auto definitions = definitionTable->definitions;
        while (*definitions != 0)
        {
            auto definition = reinterpret_cast<const Float4Definition*>(definitions);
            auto value = reinterpret_cast<const be<uint32_t>*>(shaderData + shaderContainer->virtualSize + definition->physicalOffset);
            for (uint16_t i = 0; i < (definition->count + 3) / 4; i++)
            {
                const uint32_t literalRegister = definition->registerIndex + i - (isPixelShader ? 256 : 0);
                if (godot())
                {
                    godotLiteralRegisters.insert(literalRegister);
                    println("\tvec4 c{} = uintBitsToFloat(uvec4(0x{:X}u, 0x{:X}u, 0x{:X}u, 0x{:X}u));",
                        literalRegister, value[0].get(), value[1].get(), value[2].get(), value[3].get());
                }
                else
                {
                    println("\tfloat4 c{} = asfloat(uint4(0x{:X}, 0x{:X}, 0x{:X}, 0x{:X}));",
                        literalRegister, value[0].get(), value[1].get(), value[2].get(), value[3].get());
                }

                value += 4;
            }
            definitions += 2;
        }
        ++definitions;
        while (*definitions != 0)
        {
            auto definition = reinterpret_cast<const Int4Definition*>(definitions);
            for (uint16_t i = 0; i < definition->count; i++)
            {
                union
                {
                    uint32_t value;
                    struct
                    {
                        int8_t x;
                        int8_t y;
                        int8_t z;
                        int8_t w;
                    };
                };

                value = definition->values[i].get();

                const int32_t intRegister = static_cast<int32_t>((definition->registerIndex - 8992) / 4 + i);
                if (intRegister >= 0 &&
                    intRegister < static_cast<int32_t>(32))
                {
                    declaredIntConstants[intRegister] = true;
                    intConstantDefaults[intRegister][0] = x;
                    intConstantDefaults[intRegister][1] = y;
                    intConstantDefaults[intRegister][2] = z;
                    intConstantDefaults[intRegister][3] = w;
                }
            }
            definitions += 2;
            definitions += definition->count;
        }

        out += "\n";
    }

    for (uint32_t i = 0; i < 32; ++i)
    {
        const char* shaderName = isPixelShader ? "g_Pixel" : "g_Vertex";
        if (godot())
        {
            // Loop counts are uniforms declared in the header, for the loops the body runs.
            for (uint32_t k = 0; k < 4; ++k)
                godotIntDefaults[i][k] = intConstantDefaults[i][k];
        }
        else if (declaredIntConstants[i])
        {
            println("\tint4 i{} = (({}IntegerConstantValidMask & (1u << {})) != 0u) ? {}IntegerConstant({}) : int4({}, {}, {}, {});",
                i, shaderName, i, shaderName, i,
                intConstantDefaults[i][0], intConstantDefaults[i][1],
                intConstantDefaults[i][2], intConstantDefaults[i][3]);
        }
        else
        {
            println("\tint4 i{} = {}IntegerConstant({});", i, shaderName, i);
        }
    }

    bool printedRegisters[64]{};

    uint32_t interpolatorCount = (shader->interpolatorInfo >> 5) & 0x1F;

    for (uint32_t i = 0; i < interpolatorCount; i++)
    {
        union
        {
            Interpolator interpolator;
            uint32_t value;
        };
    
        if (isPixelShader)
        {
            value = reinterpret_cast<const PixelShader*>(shader)->interpolators[i];
            if (godot())
            {
                const std::string varying = godotVaryingName(uint32_t(interpolator.usage), uint32_t(interpolator.usageIndex));
                godotVaryings.insert(varying);
                println("\tvec4 r{} = {};", uint32_t(interpolator.reg), varying);
            }
            else
            {
                println("\tfloat4 r{} = i{}{};", uint32_t(interpolator.reg), USAGE_VARIABLES[uint32_t(interpolator.usage)], uint32_t(interpolator.usageIndex));
            }
            if (interpolator.reg < 64)
            {
                printedRegisters[interpolator.reg] = true;
            }
        }
        else
        {
            auto vertexShader = reinterpret_cast<const VertexShader*>(shader);
            value = vertexShader->vertexElementsAndInterpolators[vertexShader->field18 + vertexShader->vertexElementCount + i];
            if (godot())
            {
                const std::string varying = godotVaryingName(uint32_t(interpolator.usage), uint32_t(interpolator.usageIndex));
                godotVaryings.insert(varying);
                interpolators[interpolator.reg] = varying;
            }
            else
            {
                interpolators[interpolator.reg] =
                    fmt::format("o{}{}", USAGE_VARIABLES[uint32_t(interpolator.usage)], uint32_t(interpolator.usageIndex));
            }
        }
    }

    if (!isPixelShader)
    {
    #ifdef UNLEASHED_RECOMP
        if (!hasMtxProjection)
            out += "\toPos = 0.0;\n";
    #endif

        if (godot())
        {
            // The object matrices the aliased constants read (xenos_common.gdshaderinc): Godot's
            // built-ins are readable only here, not in a helper.
            if (godotParticleShader() || godotWorldSpaceShader())
            {
                // These shaders take guest-world positions (the particle shaders place their
                // records with g_MatWorld; the water's vertices arrive in the world; the
                // landscape builds its own from g_PositionScale/OffSetXY), so
                // g_WorldViewProjection is the camera alone, guest world to Godot clip, and
                // g_WorldTransform is the identity a world-space vertex buffer is drawn with.
                out += "\tmat4 xr_wvp = XR_D3D_CLIP * PROJECTION_MATRIX * VIEW_MATRIX * XR_GODOT_TO_GUEST_WORLD;\n";
                out += "\tmat4 xr_world = mat4(1.0);\n";
            }
            else if (godotRepeatedShader() && !isPixelShader)
            {
                // A repeated mesh builds its own guest-world position from the instance rows
                // (POSITION2..4, here the rows of xr_world) plus the instance position
                // (g_WorldPositionAndReciprocalScale), then projects it with
                // g_WorldViewProjection, which for this draw is the camera alone. The object
                // matrix applied again sent every instance off screen.
                out += "\tmat4 xr_wvp = XR_D3D_CLIP * PROJECTION_MATRIX * VIEW_MATRIX * XR_GODOT_TO_GUEST_WORLD;\n";
                out += "\tmat4 xr_world = XR_GODOT_TO_GUEST_WORLD * MODEL_MATRIX * XR_FILE_TO_MODEL;\n";
            }
            else
            {
                out += "\tmat4 xr_wvp = XR_D3D_CLIP * PROJECTION_MATRIX * MODELVIEW_MATRIX * XR_FILE_TO_MODEL;\n";
                out += "\tmat4 xr_world = XR_GODOT_TO_GUEST_WORLD * MODEL_MATRIX * XR_FILE_TO_MODEL;\n";
            }
            out += "\tvec4 oPos = vec4(0.0);\n";
            out += "\tvec4 oPts = vec4(0.0);\n";
            for (const std::string& varying : godotVaryings)
                println("\t{} = vec4(0.0);", varying);
        }
        else
        {
            for (auto& [usage, usageIndex] : INTERPOLATORS)
                println("\to{}{} = 0.0;", USAGE_VARIABLES[uint32_t(usage)], usageIndex);
        }

        out += "\n";
    }
    else if (godot())
    {
        // Godot's fragment() has no out parameters; the targets are locals the epilogue hands on.
        if (godotWritesColor0)
            out += "\tvec4 oC0 = vec4(0.0);\n";
        if (godotWritesExtraTargets)
            out += "\tvec4 oC1 = vec4(0.0);\n\tvec4 oC2 = vec4(0.0);\n\tvec4 oC3 = vec4(0.0);\n";
        if (godotWritesDepth)
            out += "\tfloat oDepth = 0.0;\n";
    }

    for (size_t i = 0; i < 64; i++)
    {
        if (!printedRegisters[i])
        {
#ifndef UNLEASHED_RECOMP  // Unleashed's hasIndexCount path owns VS r0 below.
            if (!isPixelShader && i == 0 && !godot())
            {
                // Xenos tessellation input contract (IDA: terrain 0x82207AF8 /
                // water 0x8221C898 tess draws bind NO vertex streams; the guest
                // VS reads only r0.x = patch index, r0.yz = domain UV and does
                // its own grid-cell decode + vertex-texture fetches):
                //   r0.x = patch index  = SV_InstanceID + VGT_INDX_OFFSET
                //   r0.y = domain U     = (vid % (N+1)) / N
                //   r0.z = domain V     = (vid / (N+1)) / N
                //   r1.x = 0.0          (third quad-patch weight; satisfied by
                //                        the normal zero init of r1 below)
                // N and the index offset arrive via SharedConstants bytes
                // 560/564 (shader_common.h). N==0 (runtime not wired) clamps to
                // a 1x1 grid instead of dividing by zero. The body translation
                // is identical in both variants.
                out += "#if REFII_TESSELLATION_VARIANT\n";
                out += "\tuint refiiTessFactor = max(g_TessellationFactor, 1u);\n";
                out += "\tfloat4 r0 = float4(\n";
                out += "\t\tfloat(iInstanceId) + float(g_TessellationIndexOffset),\n";
                out += "\t\tfloat(iVertexId % (refiiTessFactor + 1u)) / float(refiiTessFactor),\n";
                out += "\t\tfloat(iVertexId / (refiiTessFactor + 1u)) / float(refiiTessFactor),\n";
                out += "\t\t0.0);\n";
                out += "#else\n";
                out += "\tfloat4 r0 = 0.0;\n";
                out += "#endif\n";
                continue;
            }
#endif

            if (godot())
            {
                print("\tvec4 r{} = ", i);
                if (isPixelShader && i == ((shader->fieldC >> 8) & 0xFF))
                    out += "vec4((FRAGCOORD.xy - 0.5) * vec2(FRONT_FACING ? 1.0 : -1.0, 1.0), XR_PARAM_GEN_ZW);\n";
                else if (!isPixelShader && i == 0 && godotLandscapeShader())
                    // The title draws its terrain as hardware quad patches with no vertex buffer:
                    // r0 arrives as (quadrant index, tessellation u, tessellation v). The host's
                    // patch meshes carry exactly that per vertex in CUSTOM0 (Engine_ClassicTerrain).
                    out += "CUSTOM0;\n";
                else if (!isPixelShader && i == 0)
                    // Xenos hands every vertex shader its vertex index in r0.x: a particle
                    // quad's corner (QUADLIST, four a record), a trail's strip position, a
                    // star's or snowflake's hash seed. The host meshes index the same way.
                    out += "vec4(float(VERTEX_ID), 0.0, 0.0, 0.0);\n";
                else
                    out += "vec4(0.0);\n";
                continue;
            }

            print("\tfloat4 r{} = ", i);
            if (isPixelShader && i == ((shader->fieldC >> 8) & 0xFF))
            {
                out += "float4((iPos.xy - 0.5) * float2(iFace ? 1.0 : -1.0, 1.0), 0.0, 0.0);\n";
            }
        #ifdef UNLEASHED_RECOMP
            else if (!isPixelShader && hasIndexCount && i == 0)
            {
                out += "float4(iVertexId + g_IndexCount.x * iInstanceId, 0.0, 0.0, 0.0);\n";
            }
        #endif
            else
            {
                out += "0.0;\n";
            }
        }
    }

    out += "\tint a0 = 0;\n";
    out += "\tint aL = 0;\n";
    out += "\tbool p0 = false;\n";
    out += "\tfloat ps = 0.0;\n";

    if (memexportEnabled)
    {
        // The export registers are a separate file from the 64 GPRs - shaders
        // such as vshader_decal_memexport use r32..r37 as ordinary registers as
        // well as exporting through eA/eM0..eM4, so they must not alias.
        out += "\tfloat4 eA = 0.0;\n";
        for (uint32_t i = 0; i < 5; i++)
            println("\tfloat4 eM{} = 0.0;", i);
        out += "\tuint eMWritten = 0u;\n";
    }
    if (isPixelShader)
    {
#ifdef UNLEASHED_RECOMP
        out += "\tfloat2 pixelCoord = 0.0;\n";
#endif
        if (godot())
            out += "\tCubeMapData cubeMapData;\n\tcubeMapData.index = 0;\n\tcubeMapData.dirs[0] = vec3(0.0);\n\tcubeMapData.dirs[1] = vec3(0.0);\n";
        else
            out += "\tCubeMapData cubeMapData = (CubeMapData)0;\n";
    }

    const be<uint32_t>* code = reinterpret_cast<const be<uint32_t>*>(shaderData + shaderContainer->virtualSize + shader->physicalOffset);

    union
    {
        ControlFlowInstruction controlFlow[2];
        struct
        {
            uint32_t code0;
            uint32_t code1;
            uint32_t code2;
            uint32_t code3;
        };
    };

    auto controlFlowCode = code;
    uint32_t instrAddress = 0;
    uint32_t instrSize = shader->size;
    bool simpleControlFlow = true;
    std::vector<ControlFlowInstruction> cfList;

    while (instrAddress < instrSize)
    {
        code0 = controlFlowCode[0];
        code1 = controlFlowCode[1] & 0xFFFF;
        code2 = (controlFlowCode[1] >> 16) | (controlFlowCode[2] << 16);
        code3 = controlFlowCode[2] >> 16;

        for (auto& cfInstr : controlFlow)
        {
            uint32_t address = 0;

            switch (cfInstr.opcode)
            {
            case ControlFlowOpcode::Exec:
            case ControlFlowOpcode::ExecEnd:
                address = cfInstr.exec.address;
                break;

            case ControlFlowOpcode::CondExec:
            case ControlFlowOpcode::CondExecEnd:
            case ControlFlowOpcode::CondExecPredClean:
            case ControlFlowOpcode::CondExecPredCleanEnd:
                address = cfInstr.condExec.address;
                break;

            case ControlFlowOpcode::CondExecPred:
            case ControlFlowOpcode::CondExecPredEnd:
                address = cfInstr.condExecPred.address;
                break;

            case ControlFlowOpcode::CondJmp:
            {
                if (cfInstr.condJmp.isUnconditional || cfInstr.condJmp.direction)
                    simpleControlFlow = false;

                break;
            }

            case ControlFlowOpcode::LoopStart:
                godotLoopIds.insert(uint32_t(cfInstr.loopStart.loopId));
                break;
            }
            cfList.push_back(cfInstr);

            if (address != 0)
                instrSize = std::min<uint32_t>(instrSize, address * 12);
        }

        controlFlowCode += 3;
        instrAddress += 12;
    }

    // A PREDICATED FORWARD JUMP OVER CODE THAT IS PREDICATED OFF ANYWAY is only a shortcut, and
    // as structured code it is wrong: the compiler writes an if/else as two such jumps whose
    // ranges overlap ("jmp L1 if !p0; (p0) kill; jmp L2 if p0; L1: (!p0) work; L2:"), and two
    // overlapping brace pairs nest the second inside the first, killing the work. A jump is
    // dropped when every instruction it skips would not run while it is taken: predicated
    // execs on the other predicate value, predicated jumps on the other value, and nops.
    std::unordered_set<uint32_t> redundantJumps;
    for (uint32_t i = 0; i < cfList.size(); ++i)
    {
        const auto& jmp = cfList[i].condJmp;
        if (cfList[i].opcode != ControlFlowOpcode::CondJmp || !jmp.isPredicated ||
            jmp.isUnconditional || jmp.direction || jmp.address <= i || jmp.address > cfList.size())
            continue;
        bool skippable = true;
        for (uint32_t k = i + 1; k < jmp.address && skippable; ++k)
        {
            const ControlFlowInstruction& other = cfList[k];
            switch (other.opcode)
            {
            case ControlFlowOpcode::Nop:
                break;
            case ControlFlowOpcode::CondExecPred:
            case ControlFlowOpcode::CondExecPredEnd:
                skippable = other.condExecPred.condition != jmp.condition;
                break;
            case ControlFlowOpcode::CondJmp:
                skippable = other.condJmp.isPredicated && !other.condJmp.isUnconditional &&
                    other.condJmp.condition != jmp.condition;
                break;
            default:
                skippable = false;
                break;
            }
        }
        if (skippable)
            redundantJumps.insert(i);
    }
    for (uint32_t i = 0; i < cfList.size(); ++i)
    {
        const auto& jmp = cfList[i].condJmp;
        if (cfList[i].opcode == ControlFlowOpcode::CondJmp && !jmp.isUnconditional && !jmp.direction &&
            redundantJumps.count(i) == 0)
            ++ifEndLabels[jmp.address];
    }

    if (godot())
    {
        // No `return` inside a Godot processor function, and no `continue` inside a `switch`:
        // straight-line code runs in a do-while(false) that `break` leaves, and the jump form
        // is a while(true) over an `if (pc == N)` chain, where falling off one block sets the
        // next pc and a jump sets pc and `continue`s.
        // THE SHADOW PASS NEEDS ONLY DEPTH. The sun cascades are the same materials drawn from
        // the light (Engine_ClassicShadows marks its cameras with XR_SHADOW_PASS_LAYER); a pixel
        // that is not alpha-tested skips its whole body there and still writes its depth.
        const char* shadowSkip = isPixelShader
            ? "((CAMERA_VISIBLE_LAYERS & XR_SHADOW_PASS_LAYER) != 0u && (xr_spec_constants & XR_SPEC_CONSTANT_ALPHA_TEST) == 0u)"
            : nullptr;
        if (simpleControlFlow)
        {
            out += "\n\tdo\n\t{\n";
            if (shadowSkip != nullptr)
                out += fmt::format("\t\tif {}\n\t\t{{\n\t\t\tbreak;\n\t\t}}\n", shadowSkip);
            indentation = 2;
        }
        else
        {
            if (shadowSkip != nullptr)
                out += fmt::format("\n\tint pc = {} ? -1 : 0;\n", shadowSkip);
            else
                out += "\n\tint pc = 0;\n";
            out += "\twhile (true)\n";
            out += "\t{\n";
        }
    }
    else if (simpleControlFlow)
    {
        out += '\n';
        indentation = 1;
    }
    else
    {
        out += "\n\tuint pc = 0;\n";
        out += "\twhile (true)\n";
        out += "\t{\n";
        out += "\t\tswitch (pc)\n";
        out += "\t\t{\n";
    }

    controlFlowCode = code;
    instrAddress = 0;
    uint32_t pc = 0;

    while (instrAddress < instrSize)
    {
        code0 = controlFlowCode[0];
        code1 = controlFlowCode[1] & 0xFFFF;
        code2 = (controlFlowCode[1] >> 16) | (controlFlowCode[2] << 16);
        code3 = controlFlowCode[2] >> 16;

        for (auto& cfInstr : controlFlow)
        {
            if (!simpleControlFlow)
            {
                indentation = 3;
                if (godot())
                {
                    if (pc > 0)
                        println("\t\t\tpc = {};\n\t\t}}", pc);
                    println("\t\tif (pc == {})\n\t\t{{", pc);
                }
                else
                {
                    println("\t\tcase {}:", pc);
                }
            }
            else
            {
                auto findResult = ifEndLabels.find(pc);
                if (findResult != ifEndLabels.end())
                {
                    for (uint32_t i = 0; i < findResult->second; i++)
                    {
                        --indentation;
                        indent();
                        out += "}\n";
                    }
                }
            }

            ++pc;

            uint32_t address = 0;
            uint32_t count = 0;
            uint32_t sequence = 0;
            bool shouldReturn = false;
            bool shouldCloseCurlyBracket = false;
            bool execGuarded = false;

            switch (cfInstr.opcode)
            {
            case ControlFlowOpcode::Exec:
            case ControlFlowOpcode::ExecEnd:
                address = cfInstr.exec.address;
                count = cfInstr.exec.count;
                sequence = cfInstr.exec.sequence;
                shouldReturn = (cfInstr.opcode == ControlFlowOpcode::ExecEnd);
                break;

            case ControlFlowOpcode::CondExec:
            case ControlFlowOpcode::CondExecEnd:
            case ControlFlowOpcode::CondExecPredClean:
            case ControlFlowOpcode::CondExecPredCleanEnd:
                address = cfInstr.condExec.address;
                count = cfInstr.condExec.count;
                sequence = cfInstr.condExec.sequence;
                shouldReturn = (cfInstr.opcode == ControlFlowOpcode::CondExecEnd || cfInstr.opcode == ControlFlowOpcode::CondExecPredCleanEnd);
                if (godot() && count != 0)
                {
                    // The exec runs only when its bool constant equals its condition.
                    uint32_t boolRegister = cfInstr.condExec.boolAddress;
                    if (isPixelShader && boolRegister >= 128)
                        boolRegister -= 128;
                    auto findResult = boolConstants.find(boolRegister);
                    indent();
                    if (findResult != boolConstants.end())
                        println("if (((xr_booleans & {}{}) != 0u) == {})", stagePrefix(), findResult->second,
                            cfInstr.condExec.condition ? "true" : "false");
                    else
                        println("if (((xr_booleans & (1u << {}u)) != 0u) == {})",
                            boolRegister + (isPixelShader ? 16u : 0u), cfInstr.condExec.condition ? "true" : "false");
                    indent();
                    out += "{\n";
                    ++indentation;
                    execGuarded = true;
                }
                break;

            case ControlFlowOpcode::CondExecPred:
            case ControlFlowOpcode::CondExecPredEnd:
                address = cfInstr.condExecPred.address;
                count = cfInstr.condExecPred.count;
                sequence = cfInstr.condExecPred.sequence;
                shouldReturn = (cfInstr.opcode == ControlFlowOpcode::CondExecPredEnd);
                if (godot() && count != 0)
                {
                    // The exec runs only when p0 equals its condition.
                    indent();
                    println("if ({}p0)", cfInstr.condExecPred.condition ? "" : "!");
                    indent();
                    out += "{\n";
                    ++indentation;
                    execGuarded = true;
                }
                break;

            case ControlFlowOpcode::LoopStart:
                if (simpleControlFlow)
                {
                    indent();
                    if (godot())
                    {
                        // The counter has to be declared in the `for`; aL is what the body reads.
                        const uint32_t loopId = uint32_t(cfInstr.loopStart.loopId);
                        println("for (int xr_l{0} = 0; xr_l{0} < {1}i{0}.x; xr_l{0}++)", loopId, stagePrefix());
                        indent();
                        out += "{\n";
                        ++indentation;
                        indent();
                        println("aL = xr_l{};", loopId);
                        break;
                    }
                #ifdef UNLEASHED_RECOMP
                    print("[unroll] ");
                #endif
                    println("for (aL = 0; aL < i{}.x; aL++)", uint32_t(cfInstr.loopStart.loopId));
                    indent();
                    out += "{\n";
                    ++indentation;
                }
                else
                {
                    out += "\t\t\taL = 0;\n";
                }
                break;

            case ControlFlowOpcode::LoopEnd:
                if (simpleControlFlow)
                {
                    --indentation;
                    indent();
                    out += "}\n";
                }
                else
                {
                    out += "\t\t\t++aL;\n";
                    println("\t\t\tif (aL < {}i{}.x)", godot() ? stagePrefix() : "", uint32_t(cfInstr.loopEnd.loopId));
                    out += "\t\t\t{\n";
                    println("\t\t\t\tpc = {};", uint32_t(cfInstr.loopEnd.address));
                    out += "\t\t\t\tcontinue;\n";
                    out += "\t\t\t}\n";
                }
                break;

            case ControlFlowOpcode::CondJmp:
            {
                if (simpleControlFlow && redundantJumps.count(pc - 1) != 0)
                    break;
                if (cfInstr.condJmp.isUnconditional)
                {
                    assert(!simpleControlFlow);
                    println("\t\t\tpc = {};", uint32_t(cfInstr.condJmp.address));
                    out += "\t\t\tcontinue;\n";
                }
                else
                {
                    indent();
                    if (cfInstr.condJmp.isPredicated)
                    {
                        println("if ({}p0)", cfInstr.condJmp.condition ^ simpleControlFlow ? "" : "!");
                    }
                    else
                    {
                        // Pixel shaders address the unified Xenos bool space at
                        // 128+, while the constant table (and the runtime's
                        // g_Booleans packing) uses 0-based per-stage registers.
                        uint32_t boolRegister = cfInstr.condJmp.boolAddress;
                        if (isPixelShader && boolRegister >= 128)
                            boolRegister -= 128;

                        auto findResult = boolConstants.find(boolRegister);
                        if (godot())
                        {
                            if (findResult != boolConstants.end())
                                println("if ((xr_booleans & {}{}) {}= 0u)", stagePrefix(), findResult->second, cfInstr.condJmp.condition ^ simpleControlFlow ? "!" : "=");
                            else
                                println("if ((xr_booleans & (1u << {}u)) {}= 0u)",
                                    boolRegister + (isPixelShader ? 16u : 0u),
                                    cfInstr.condJmp.condition ^ simpleControlFlow ? "!" : "=");
                        }
                        else if (findResult != boolConstants.end())
                            println("if ((g_Booleans & {}) {}= 0)", findResult->second, cfInstr.condJmp.condition ^ simpleControlFlow ? "!" : "=");
                        else
                            println("if ((g_Booleans & (1u << {})) {}= 0)",
                                boolRegister + (isPixelShader ? 16u : 0u),
                                cfInstr.condJmp.condition ^ simpleControlFlow ? "!" : "=");
                    }

                    if (simpleControlFlow)
                    {
                        indent();
                        out += "{\n";
                        ++indentation;
                    }
                    else
                    {
                        out += "\t\t\t{\n";
                        println("\t\t\t\tpc = {};", uint32_t(cfInstr.condJmp.address));
                        out += "\t\t\t\tcontinue;\n";
                        out += "\t\t\t}\n";
                    }
                }
                break;
            }

            case ControlFlowOpcode::Alloc:
                // Every `alloc` closes the previous memory export: the eM#
                // registers written since the last alloc belong to the stream
                // whose constant is still sitting in eA. Same flush point the
                // SDK uses in DxbcShaderTranslator::ProcessAllocInstruction.
                emitMemoryExportFlush();
                break;
            }

            auto instructionCode = code + address * 3;
            
            for (uint32_t i = 0; i < count; i++)
            {
                union
                {
                    VertexFetchInstruction vertexFetch;
                    TextureFetchInstruction textureFetch;
                    AluInstruction alu;
                    struct
                    {
                        uint32_t code0;
                        uint32_t code1;
                        uint32_t code2;
                    };
                };
            
                code0 = instructionCode[0];
                code1 = instructionCode[1];
                code2 = instructionCode[2];
            
                if ((sequence & 0x1) != 0)
                {
                    if (vertexFetch.opcode == FetchOpcode::VertexFetch)
                    {
                        recompile(vertexFetch, address + i);
                    }
                    else
                    {
                    #ifdef UNLEASHED_RECOMP
                        if (textureFetch.constIndex == 10) // g_GISampler
                        {
                            specConstantsMask |= SPEC_CONSTANT_BICUBIC_GI_FILTER;

                            indent();
                            out += "if (g_SpecConstants() & SPEC_CONSTANT_BICUBIC_GI_FILTER)";
                            indent();
                            out += '{';

                            ++indentation;
                            recompile(textureFetch, true);
                            --indentation;

                            indent();
                            out += "}";
                            indent();
                            out += "else";
                            indent();
                            out += '{';

                            ++indentation;
                            recompile(textureFetch, false);
                            --indentation;

                            indent();
                            out += '}';
                        }
                        else
                    #endif
                        {
                            recompile(textureFetch, false);
                        }
                    }
                }
                else
                {
                    recompile(alu);
                }
            
                sequence >>= 2;
                instructionCode += 3;
            }

            if (execGuarded)
            {
                --indentation;
                indent();
                out += "}\n";
            }

            if (shouldReturn && godot())
            {
                // The alpha test and the outputs happen once, after the block, where every exit
                // lands; here the block is only left.
                indent();
                out += "break;\n";
            }
            else if (shouldReturn)
            {
                // Anything still pending in eM# is exported at shader exit.
                emitMemoryExportFlush();

                if (isPixelShader)
                {
                    specConstantsMask |= SPEC_CONSTANT_ALPHA_TEST;

                    indent();
                    out += "[branch] if (g_SpecConstants() & SPEC_CONSTANT_ALPHA_TEST)";
                    indent();
                    out += '{';

                    indent();
                    out += "\tclip(oC0.w - g_AlphaThreshold);\n";

                    indent();
                    out += "}";

                #ifdef UNLEASHED_RECOMP
                    specConstantsMask |= SPEC_CONSTANT_ALPHA_TO_COVERAGE;

                    indent();
                    out += "else if (g_SpecConstants() & SPEC_CONSTANT_ALPHA_TO_COVERAGE)";
                    indent();
                    out += '{';

                    indent();
                    out += "\toC0.w *= 1.0 + computeMipLevel(pixelCoord) * 0.25;\n";
                    indent();
                    out += "\toC0.w = 0.5 + (oC0.w - g_AlphaThreshold) / max(fwidth(oC0.w), 1e-6);\n";

                    indent();
                    out += '}';
                #endif
                }
                else
                {
                #ifdef UNLEASHED_RECOMP
                    if (!hasMtxProjection)
                #endif
                    {
                        out += "\toPos.xy += g_HalfPixelOffset * oPos.w;\n";
                    }
                }

                if (simpleControlFlow)
                {
                    indent();
                #ifdef UNLEASHED_RECOMP
                    if (hasMtxProjection)
                    {
                        out += "continue;\n";
                    }
                    else
                #endif
                    {
                        out += "return;\n";
                    }
                }
                else
                {
                    out += "\t\t\tbreak;\n";
                }
            }

            if (shouldCloseCurlyBracket)
            {
                --indentation;
                indent();
                out += "}\n";
            }
        }

        controlFlowCode += 3;
        instrAddress += 12;
    }

    if (godot())
    {
        if (simpleControlFlow)
        {
            out += "\t}\n\twhile (false);\n";
        }
        else
        {
            // Close the last `if (pc == N)` block, then leave the loop: falling off the end is
            // the shader's end.
            println("\t\t\tpc = {};\n\t\t}}", pc);
            out += "\t\tbreak;\n";
            out += "\t}\n";
        }

        if (isPixelShader)
        {
            if (godotWritesColor0)
            {
                // The guest's alpha test, then the one colour a Godot material carries. The
                // shader has lit the pixel itself, so the material is unshaded and the result is
                // the albedo.
                out += "\tXR_BEFORE_OUTPUT(oC0);\n";
                out += "\tif ((xr_spec_constants & XR_SPEC_CONSTANT_ALPHA_TEST) != 0u && oC0.w < xr_alpha_threshold)\n";
                out += "\t{\n\t\tdiscard;\n\t}\n";
                out += "\tALBEDO = XR_COLOUR_OUT(oC0);\n";
                // Through a hook: Godot moves any surface whose shader writes ALPHA to the
                // blended alpha pass, so an opaque composition defines it empty.
                out += "\tXR_WRITE_ALPHA(XR_ALPHA_OUT(oC0.a));\n";
            }
            if (godotWritesDepth)
                out += "\tDEPTH = oDepth;\n";
        }
        else if (godotAliasedPosition)
        {
            // oPos came through the aliased world-view-projection, so it is Godot's own transform
            // of this vertex, computed the guest's way. XR_POSITION_OUT is the composition's
            // hook (xenos_common.gdshaderinc), the identity unless it gates part of the mesh.
            // oPos is in the title's clip convention (XR_D3D_CLIP); Godot's is its mirror in y.
            out += "\tPOSITION = XR_D3D_CLIP * XR_POSITION_OUT(oPos);\n";
        }
        else
        {
            // This family's matrices are not aliased yet: Godot's own transform by default, the
            // guest's clip-space result when its matrix constants are being fed. The expression
            // is exactly Godot's default, so a false switch changes nothing.
            out += "\tPOSITION = xr_use_guest_position ? oPos : (PROJECTION_MATRIX * MODELVIEW_MATRIX * vec4(VERTEX, 1.0));\n";
        }
        if (godotWritesPointSize)
            out += "\tPOINT_SIZE = XR_POINT_SIZE(oPts.x);\n";

        out += "}\n";
        out = godotHeader() + out;
        return;
    }

    if (!simpleControlFlow)
    {
        out += "\t\t\tbreak;\n";
        out += "\t\t}\n";
        out += "\t\tbreak;\n";
        out += "\t}\n";
    }

#ifdef UNLEASHED_RECOMP
    if (hasMtxProjection)
        out += "\t}\n";

    if (!isPixelShader && hasMtxProjection)
        out += "\toPos.xy += g_HalfPixelOffset * oPos.w;\n";
#endif

    out += "}";
}
