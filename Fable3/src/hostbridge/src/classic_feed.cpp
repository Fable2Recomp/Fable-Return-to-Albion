// refii - Fable 3's classic renderer feeds: the frame constants' sources, the theme block and its
// texture slots, the render scene's lights, shadow spheres, mists, weather, particles and trails.
//
// Every export keeps Fable 2's signature and output layout (Fable2/src/hostbridge/src/
// host_bridge.cpp; Godot binds them in HostBridge.cs). Every offset is from the Fable 3 IDB,
// each against its own base:
//
//   Game  = the game of the world on screen, *0x83556534 (presented_world.h)
//   WG    = *(*(Game+0x1C)+4)   the world graphics (sub_821BF330 -> sub_821AF1B8 at 0x821BF3CC)
//   L     = *(WG+0x6C)          the sky owner, sub_821AF1B8's `sub_822488A0(a1[27])`: L+4 the
//                               scene index, L+8 the sun twin, L+12 the ambient twin, L+16 the sky
//   scene = *(*0x8355F454 + 4*(L+4)), bounded by *0x8355F458 (sub_8228D388)
//   twin  = *(*0x8355F464 + 4*h), bounded by *0x8355F468 (sub_8220D870)
//   scene+16    the theme block, 916 bytes, packed (sub_8224FBC8 copies it field by field)
//   scene+1000  the typed list (sub_82C6F230 in the scene ctor sub_82C57BA0): type nodes of 24
//               bytes at +4..+8 keyed at +0, objects +8..+12 (sub_821DBC80, sub_821F3F28);
//               category nodes at +20..+24, flag +4, objects +8..+12 (sub_8225DF60, sub_821F38E0)
//   scene+0x4F4 the light manager LM (sub_82190650 0x8219067C; ctor 0x82C57E60): 32-byte entries
//               at *(LM+8)..*(LM+12) {sphere vec4, +16 the light}, LM+24 the live mask, LM+32 the
//               directional mask (sub_822327E8)
//   light       +4 type (8 point, 9 spot, 10 directional, 11 global), +80/+96 AABB, +112 sphere,
//               +176 shadow object, +208 colour, +224 flicker brightness, +288 position,
//               +304 direction (what every packer reads: directional sub_82288FF0 0x8228902c
//               `lvx128 v10,r3,r7` r7 = 304, sub_82C758C0 0x82c758fc, spot sub_82C86E68
//               0x82c86ebc, all negated by sign xor; set from the descriptor's +128 by
//               sub_8229B940; the sky rig's command 4 writes +76 (sub_821A80A8 0x821a8114) and
//               no reader of +76 was found in the light classes), +320 range, +324 cone,
//               +328/+332 fade, +340/+341/+342 shadow
//               bytes, +343 skip, +416 zone, +420/+424 cone cos and 1/(1-cos), +428 1/range,
//               +432 flicker walk, +444 intensity (packer 0x82224000
//               `li r6,0x1BC`), +448 range scale, +484 caster, +560 spot planes (count +816)

#include <cmath>
#include <cstdint>
#include <cstring>

#include "host_abi.h"
#include "host_core.h"
#include "presented_world.h"

namespace {

using refii::hostcore::GuestByteAt;
using refii::hostcore::GuestWord64At;
using refii::hostcore::GuestWordAt;
using refii::hostcore::SafeKernelMemory;

constexpr uint32_t kScenes = 0x8355F454u;
constexpr uint32_t kScenesEnd = 0x8355F458u;
constexpr uint32_t kTwins = 0x8355F464u;
constexpr uint32_t kTwinsEnd = 0x8355F468u;
constexpr uint32_t kAmbientLight = 0x83564BD0u;
constexpr uint32_t kZonesBegin = 0x83564BD8u;
constexpr uint32_t kZonesEnd = 0x83564BDCu;
constexpr uint32_t kNormalMapDarkening = 0x83564BB0u;
// The lighting globals sub_82274678 sets each frame from the theme block T = scene+16 and
// sub_82259C38 builds the frame's lighting registers from: the lightmap factor (1, 1, 1, 0)
// (0x82274804), the local ambient colour T+64 (0x82274834), the AO contrast T+92 (0x82274848),
// the lightmap flicker factor 1 + flicker x T+100 (0x82274880), and the sun shadow pass's light
// (0x82274918, read by sub_822664B0 at 0x822639A0).
constexpr uint32_t kLightmapFactor = 0x83564BA0u;
constexpr uint32_t kLocalAmbient = 0x83564BC0u;
constexpr uint32_t kAOContrast = 0x8329D5E4u;
constexpr uint32_t kLightmapFlicker = 0x835568B0u;
constexpr uint32_t kShadowLight = 0x835568C0u;
constexpr uint32_t kAtmosphere = 0x83561560u;
constexpr uint32_t kRenderClock = 0x83556818u;

constexpr uint32_t kTheme = 16u;
constexpr uint32_t kTypedList = 1000u;
constexpr uint32_t kLightManager = 0x4F4u;
constexpr uint32_t kMaxNodes = 64u;

constexpr uint32_t kParticle = 12u;
constexpr uint32_t kWeather = 25u;
constexpr uint32_t kMist = 27u;
constexpr uint32_t kDisplacementParticle = 30u;
constexpr uint32_t kShadowSphere = 36u;
// The sky object: the first of the scene's type-24 objects (sub_821A8DE8, `li r4,0x18`), the one
// sub_821988C8 hands sub_82251098 (0x82198C1C).
constexpr uint32_t kSky = 24u;

constexpr uint32_t kThemeParams = 194u;

bool Plausible(uint32_t address) { return address >= 0x10000u && address < 0xC0000000u; }

const uint8_t* GuestBase() {
    rex::memory::Memory* memory = SafeKernelMemory();
    return memory == nullptr ? nullptr : memory->virtual_membase();
}

uint32_t Word(const uint8_t* base, uint32_t address) {
    return Plausible(address) ? GuestWordAt(base, address) : 0u;
}

float Float(const uint8_t* base, uint32_t address) {
    const uint32_t bits = Word(base, address);
    float value = 0.0f;
    std::memcpy(&value, &bits, sizeof(value));
    return value;
}

void Floats(const uint8_t* base, uint32_t address, float* to, uint32_t count) {
    for (uint32_t i = 0; i < count; ++i) {
        to[i] = Float(base, address + i * 4u);
    }
}

void PutBits(float* to, uint32_t bits) { std::memcpy(to, &bits, sizeof(bits)); }

/// The sky owner L, else 0.
uint32_t SkyOwner(const uint8_t* base) {
    const uint32_t game = refii::fable3::PresentedGame(base);
    const uint32_t graphics = Word(base, Word(base, game + 0x1Cu) + 4u);
    const uint32_t owner = Word(base, graphics + 0x6Cu);
    return Plausible(owner) ? owner : 0u;
}

/// Entry `index` of a guest pointer table {begin at `table`, end at `table_end`}, else 0.
uint32_t TableEntry(const uint8_t* base, uint32_t table, uint32_t table_end, uint32_t index) {
    const uint32_t begin = Word(base, table);
    const uint32_t end = Word(base, table_end);
    if (!Plausible(begin) || end <= begin || index >= (end - begin) / 4u) {
        return 0u;
    }
    const uint32_t entry = Word(base, begin + index * 4u);
    return Plausible(entry) ? entry : 0u;
}

uint32_t SceneOf(const uint8_t* base, uint32_t owner) {
    return owner == 0u ? 0u : TableEntry(base, kScenes, kScenesEnd, Word(base, owner + 4u));
}

uint32_t TwinOf(const uint8_t* base, uint32_t owner, uint32_t slot) {
    return owner == 0u ? 0u : TableEntry(base, kTwins, kTwinsEnd, Word(base, owner + slot));
}

/// The render scene the classic renderer draws, else 0.
uint32_t ClassicScene(const uint8_t* base) { return SceneOf(base, SkyOwner(base)); }

/// The object vector {begin, end} of every type node keyed `type` in the scene's typed list.
template <typename Visit>
void ForEachTyped(const uint8_t* base, uint32_t scene, uint32_t type, Visit&& visit) {
    const uint32_t list = scene + kTypedList;
    const uint32_t nodes_begin = Word(base, list + 4u);
    const uint32_t nodes_end = Word(base, list + 8u);
    if (!Plausible(nodes_begin) || nodes_end < nodes_begin || nodes_end - nodes_begin > kMaxNodes * 24u) {
        return;
    }
    for (uint32_t node = nodes_begin; node + 24u <= nodes_end; node += 24u) {
        if (Word(base, node) != type) {
            continue;
        }
        const uint32_t objects_begin = Word(base, node + 8u);
        const uint32_t objects_end = Word(base, node + 12u);
        if (!Plausible(objects_begin) || objects_end < objects_begin || objects_end - objects_begin > 4u * 8192u) {
            continue;
        }
        if (!visit(objects_begin, objects_end)) {
            return;
        }
    }
}

/// The manager's entries and live mask; false when the scene has none.
bool ManagerEntries(const uint8_t* base, uint32_t scene, uint32_t* out_entries, uint64_t* out_mask) {
    const uint32_t manager = scene != 0u ? Word(base, scene + kLightManager) : 0u;
    const uint32_t entries = Word(base, manager + 8u);
    if (!Plausible(manager) || !Plausible(entries)) {
        return false;
    }
    *out_entries = entries;
    *out_mask = GuestWord64At(base, manager + 24u);
    return true;
}

/// Fable 2's [15] flag word: bit0 cast shadow (+340), bit1 +341, bit2 excluded (+342),
/// bit3 caster (+484); and Fable 3's bit4, +343: the manager's LM+40 mask (sub_822327E8
/// 0x8223298C), which a draw's light group leaves out (sub_82215BD8 0x82215C08 `ld r11,0x28(r4)`,
/// `andc`). Fable 2's bridge never sets bit4.
float LightFlags(const uint8_t* base, uint32_t light) {
    uint32_t flags = 0u;
    flags |= GuestByteAt(base, light + 340u) != 0u ? 1u : 0u;
    flags |= GuestByteAt(base, light + 341u) != 0u ? 2u : 0u;
    flags |= GuestByteAt(base, light + 342u) != 0u ? 4u : 0u;
    flags |= GuestByteAt(base, light + 484u) != 0u ? 8u : 0u;
    flags |= GuestByteAt(base, light + 343u) != 0u ? 16u : 0u;
    return static_cast<float>(flags);
}

/// Fable 2's [4..16] of a light row from its object: colour, intensity, direction, type, range,
/// cone, range scale, flags, zone (raw bits).
void LightRow(const uint8_t* base, uint32_t light, float* o) {
    Floats(base, light + 208u, o + 4, 3u);
    o[7] = Float(base, light + 444u);
    Floats(base, light + 304u, o + 8, 3u);
    o[11] = static_cast<float>(Word(base, light + 4u));
    o[12] = Float(base, light + 320u);
    o[13] = Float(base, light + 324u);
    o[14] = Float(base, light + 448u);
    o[15] = LightFlags(base, light);
    PutBits(o + 16, Word(base, light + 416u));
}

/// One Fable 2 theme parameter (scene+16 + 16 x index) from Fable 3's packed block.
struct ThemeSlot {
    uint16_t f2_index;
    uint16_t f3_offset;
    uint8_t lanes;
};

/// Fable 3's packed theme parameters (`sub_82C43978` colour vec4, `sub_82C439E8` float) by name
/// into Fable 2's slots. The F3 parameters with no Fable 2 name are not carried.
constexpr ThemeSlot kThemeSlots[] = {
    {0u, 0u, 4u},  // MAIN_LIGHT_COLOUR <- Main Light Colour
    {1u, 16u, 1u},  // MAIN_LIGHT_SHADOWED <- Main Light Shadowed
    {2u, 32u, 4u},  // AMBIENT_COLOUR <- Ambient Colour
    {4u, 48u, 4u},  // AMBIENT_NORMAL_MAP_DARKENING_COLOUR <- Ambient Normal Map Darkening
    {5u, 80u, 1u},  // DISTANT_SHADOW_FACTOR <- Distant Shadow Factor
    {6u, 88u, 1u},  // ENABLE_LIGHT_ZONE_SHADOW_CULLING <- Enable Light Zone Shadow Culling
    {7u, 248u, 1u},  // SUN_INTENSITY <- Sun Intensity
    {8u, 108u, 1u},  // SKY_BETA_RAYLEIGH_MULTIPLAYER <- Sky Beta Rayleigh Multiplier
    {9u, 112u, 1u},  // SKY_BETA_MIE_MULTIPLAYER <- Sky Beta Mie Multiplier
    {10u, 128u, 4u},  // SKY_COLOUR <- Sky Colour
    {11u, 144u, 4u},  // SKY_COMPLEMENTARY_COLOUR <- Sky Complementary Colour
    {12u, 160u, 1u},  // SKY_COMPLEMENTARY_COLOUR_BIAS <- Sky Complementary Colour Bias
    {13u, 192u, 4u},  // SKY_SUNSET_COLOUR <- Sunset Colour
    {14u, 496u, 1u},  // SKY_FOGGING_START <- Fogging Start
    {15u, 500u, 1u},  // SKY_FOGGING_NEAR_DISTANCE <- Near Distance
    {16u, 504u, 1u},  // SKY_FOGGING_NEAR_DENSITY <- Near Density
    {17u, 508u, 1u},  // SKY_FOGGING_FAR_DISTANCE <- Far Distance
    {18u, 512u, 1u},  // SKY_FOGGING_FAR_DENSITY <- Far Density
    {19u, 528u, 4u},  // SKY_FOGGING_CLOSE_FOG_COLOUR <- Close Fog Colour
    {20u, 544u, 1u},  // SKY_FOGGING_CLOSE_FOG_MAX_DISTANCE <- Close Fog Max Distance
    {22u, 404u, 1u},  // TONE_MAPPING_MIDDLE_GRAY <- Exposure
    {23u, 424u, 1u},  // TONE_MAPPING_CONTRAST <- Contrast
    {27u, 432u, 1u},  // TONE_MAPPING_CONTRAST_ENHANCEMENT <- Contrast Enhancement
    {28u, 436u, 1u},  // TONE_MAPPING_CONTRAST_ENHANCEMENT_EXPONENT <- Contrast Enhancement Exponent
    {29u, 440u, 1u},  // TONE_MAPPING_CONTRAST_MAX_DEVIATION <- Contrast Enhancement Max Deviation
    {30u, 428u, 1u},  // TONE_MAPPING_CONTRAST_CLAMPING <- Contrast Clamping
    {34u, 452u, 1u},  // TONE_MAPPING_BLOOM_THRESHOLD_AVG_FACTOR <- Bloom Threshold
    {35u, 444u, 1u},  // TONE_MAPPING_BLOOM_FACTOR <- Bloom Strength
    {36u, 448u, 1u},  // TONE_MAPPING_BLOOM_BLUR_SIZE <- Bloom Blur Size
    {44u, 408u, 1u},  // TONE_MAPPING_SATURATION <- Saturation
    {45u, 412u, 1u},  // TONE_MAPPING_SATURATION_MASKED_OFFSET <- Saturation Masked
    {46u, 416u, 1u},  // TONE_MAPPING_BRIGHTNESS <- Brightness
    {47u, 420u, 1u},  // TONE_MAPPING_BRIGHTNESS_MASKED_OFFSET <- Brightness Masked
    {48u, 456u, 1u},  // TONE_MAPPING_RADIALBLUR_FACTOR <- Radial Blur Factor
    {58u, 104u, 1u},  // MAIN_LIGHT_TIME_FACTOR <- Time Factor
    {59u, 240u, 1u},  // SUN_AXIS_ELEVATION <- Sun Axis Elevation
    {61u, 244u, 1u},  // SUN_AXIS_XY_ROTATION <- Sun Axis XY Rotation Offset
    {62u, 344u, 1u},  // MOON_AXIS_ELEVATION <- Moon Axis Elevation
    {64u, 348u, 1u},  // MOON_AXIS_XY_ROTATION <- Moon Axis XY Rotation Offset
    {65u, 384u, 1u},  // MOON_INTENSITY <- Moon Intensity
    {66u, 392u, 1u},  // MOON_SIZE <- Moon Size
    {67u, 388u, 1u},  // MOON_GLARE_INTENSITY <- Moon Glare Intensity
    {68u, 396u, 1u},  // MOON_GLARE_SIZE <- Moon Glare Size
    {69u, 400u, 1u},  // MOON_TRANSPARENCY <- Moon Transparency
    {70u, 208u, 1u},  // STARS_BRIGHTNESS <- Star Brightness
    {71u, 212u, 1u},  // SNOW_SIZE <- Snow Size
    {72u, 216u, 1u},  // SNOW_FALLSPEED <- Snow Fall Speed
    {73u, 220u, 1u},  // RAIN_SIZE <- Rain Size
    {74u, 224u, 1u},  // RAIN_DENSITY <- Rain Density
    {75u, 228u, 1u},  // HIDE_OCCLUSION_ERRORS <- Hide Occlusion Errors
    {76u, 548u, 1u},  // WIND_STRENGTH_MIN <- Wind Strength Minimum
    {77u, 552u, 1u},  // WIND_STRENGTH_MAX <- Wind Strength Maximum
    {78u, 556u, 1u},  // WIND_STRENGTH_VARIATION <- Wind Strength Variation
    {79u, 560u, 1u},  // WIND_XY_ROTATION_MIN <- Wind XY Rotation Minimum
    {80u, 568u, 1u},  // WIND_ELEVATION_MIN <- Wind Elevation Minimum
    {81u, 564u, 1u},  // WIND_XY_ROTATION_MAX <- Wind XY Rotation Maximum
    {82u, 572u, 1u},  // WIND_ELEVATION_MAX <- Wind Elevation Maximum
    {83u, 576u, 1u},  // WIND_DIRECTION_VARIATION <- Wind Direction Variation
    {84u, 580u, 1u},  // WIND_CHANGE_FREQUENCY <- Wind Change Frequency
    {85u, 584u, 1u},  // WIND_CHANGE_DURATION <- Wind Change Duration
    {86u, 808u, 1u},  // DOF_NEAR_PLANE <- Depth of Field Near Plane
    {87u, 816u, 1u},  // DOF_NEAR_FOCAL_PLANE <- Depth of Field Near Focal Plane
    {88u, 820u, 1u},  // DOF_FAR_FOCAL_PLANE <- Depth of Field Far Focal Plane
    {89u, 812u, 1u},  // DOF_FAR_PLANE <- Depth of Field Far Plane
    {90u, 824u, 1u},  // DOF_NEAR_BLUR_AMOUNT <- Depth of Field Near Blur Amount
    {91u, 828u, 1u},  // DOF_FAR_BLUR_AMOUNT <- Depth of Field Far Blur Amount
    {92u, 268u, 1u},  // SUN_DISC_SIZE <- Sun Disc Size
    {93u, 272u, 4u},  // SUN_DISC_COLOUR <- Sun Disc Colour
    {94u, 288u, 1u},  // SUN_DISC_INTENSITY <- Sun Disc Intensity
    {95u, 308u, 1u},  // SUN_BEAMS_WIDTH <- Sun Beams Width
    {96u, 312u, 1u},  // SUN_BEAMS_HEIGHT <- Sun Beams Height
    {97u, 316u, 1u},  // SUN_BEAMS_INTENSITY <- Sun Beams Intensity
    {100u, 336u, 1u},  // SUN_GLARE_INTENSITY <- Sun Glare Intensity
    {101u, 340u, 1u},  // SUN_GLARE_SIZE <- Sun Glare Size
    {102u, 712u, 1u},  // WATER_VISTA_DISABLED <- Disable Vista Water
    {123u, 784u, 1u},  // WATER_REFLECTION_STRENGTH <- Water Reflection Strength
    {125u, 800u, 1u},  // WATER_GLITTERING_STRENGTH <- Water Glittering Strength
    {133u, 604u, 1u},  // CLOUDS_LAYER1_TRANSPARENCY <- Cloud Transparency 1
    {134u, 612u, 1u},  // CLOUDS_LAYER1_HEIGHT <- Cloud Height 1
    {135u, 608u, 1u},  // CLOUDS_LAYER1_NORMAL_STRENGTH <- Cloud Normal Strength 1
    {137u, 616u, 1u},  // CLOUDS_LAYER1_AMBIENT_LIGHT <- Cloud Ambient Light 1
    {138u, 620u, 1u},  // CLOUDS_LAYER1_BRIGHTNESS <- Cloud Brightness 1
    {141u, 624u, 1u},  // CLOUDS_LAYER1_SIZEX <- Cloud Size X 1
    {142u, 628u, 1u},  // CLOUDS_LAYER1_SIZEY <- Cloud Size Y 1
    {143u, 632u, 1u},  // CLOUDS_LAYER1_TEXTURE_SCALEX <- Cloud Scale X 1
    {144u, 636u, 1u},  // CLOUDS_LAYER1_TEXTURE_SCALEY <- Cloud Scale Y 1
    {145u, 640u, 1u},  // CLOUDS_LAYER1_VELOCITYX <- Cloud Velocity X 1
    {146u, 644u, 1u},  // CLOUDS_LAYER1_VELOCITYY <- Cloud Velocity Y 1
    {147u, 664u, 1u},  // CLOUDS_LAYER2_TRANSPARENCY <- Cloud Transparency 2
    {148u, 672u, 1u},  // CLOUDS_LAYER2_HEIGHT <- Cloud Height 2
    {149u, 668u, 1u},  // CLOUDS_LAYER2_NORMAL_STRENGTH <- Cloud Normal Strength 2
    {151u, 676u, 1u},  // CLOUDS_LAYER1_AMBIENT_LIGHT (layer 2) <- Cloud Ambient Light 2
    {152u, 680u, 1u},  // CLOUDS_LAYER1_BRIGHTNESS (layer 2) <- Cloud Brightness 2
    {155u, 684u, 1u},  // CLOUDS_LAYER2_SIZEX <- Cloud Size X 2
    {156u, 688u, 1u},  // CLOUDS_LAYER2_SIZEY <- Cloud Size Y 2
    {157u, 692u, 1u},  // CLOUDS_LAYER2_TEXTURE_SCALEX <- Cloud Scale X 2
    {158u, 696u, 1u},  // CLOUDS_LAYER2_TEXTURE_SCALEY <- Cloud Scale Y 2
    {159u, 700u, 1u},  // CLOUDS_LAYER2_VELOCITYX <- Cloud Velocity X 2
    {160u, 704u, 1u},  // CLOUDS_LAYER2_VELOCITYY <- Cloud Velocity Y 2
    {189u, 708u, 1u},  // GROUND_MIST_STRENGTH <- Strength
};

/// Fable 2's LIGHTMAP_COLOUR slot. Fable 3 has no such parameter: its lightmap factor is the
/// constant (1, 1, 1, 0) sub_82274678 stores at 0x83564BA0 (0x822747D8 `vperm` of 1.0 x 3 and
/// 0x8206C95C = 0, 0x82274804), which is what the slot's reader feeds the lightmap register from.
constexpr uint32_t kLightmapColourSlot = 3u;

/// Fable 2's theme parameters [0, `slots`) as float4s from the scene's packed block; `out` holds
/// `slots` x 4 floats, zeroed first.
void RemapTheme(const uint8_t* base, uint32_t scene, float* out, uint32_t slots) {
    std::memset(out, 0, static_cast<size_t>(slots) * 4u * sizeof(float));
    const uint32_t theme = scene + kTheme;
    for (const ThemeSlot& slot : kThemeSlots) {
        if (slot.f2_index < slots) {
            Floats(base, theme + slot.f3_offset, out + slot.f2_index * 4u, slot.lanes);
        }
    }
    if (kLightmapColourSlot < slots) {
        float* lightmap = out + kLightmapColourSlot * 4u;
        lightmap[0] = lightmap[1] = lightmap[2] = 1.0f;
    }
}

/// Fable 2's theme texture slots (T + 16 x (194 + slot)) and the Fable 3 header each comes from
/// (T + offset: +4 begin, +8 end of {f32 weight, u32 hash}, as sub_821A8308 copies it); 0 where
/// Fable 3 has no such texture.
constexpr uint16_t kThemeTextureSlots[15] = {
    0u,    // TONE_MAPPING_OVERLAY
    164u,  // SKY_OVERLAY <- Sky Overlay Texture
    292u,  // SKY_SUNBEAMS <- Sun Beams Texture
    252u,  // SKY_SUNDISC <- Sun Disc Texture
    0u,    // SKY_STREAKSTEXTURE
    320u,  // SKY_GLARETEXTURE <- Sun Glare Texture
    352u,  // SKY_MOONTEXTURE <- Moon Texture
    368u,  // SKY_MOONGLARETEXTURE <- Moon Glare Texture
    588u,  // CLOUDS_LAYER1_DENSITY_MAP <- Cloud Texture 1
    648u,  // CLOUDS_LAYER2_DENSITY_MAP <- Cloud Texture 2
    0u,    // CLOUDS_LAYER3_DENSITY_MAP
    0u,    // CLOUDS_LAYER4_DENSITY_MAP
    720u,  // WATER_NORMAL_MAP <- Water Normal Map
    0u,    // WATER_DETAIL_MAP
    0u,    // WATER_HEIGHT_MAP
};

/// A texture key that points at printable text carries its path (16 words).
void TextureKey(const uint8_t* base, uint32_t entry, float* o) {
    const uint32_t key = Word(base, entry + 4u);
    PutBits(o, key);
    o[1] = Float(base, entry + 8u);
    o[2] = static_cast<float>(static_cast<int8_t>(GuestByteAt(base, entry + 20u)));
    o[3] = static_cast<float>(static_cast<int8_t>(GuestByteAt(base, entry + 21u)));
    if (!Plausible(key)) {
        return;
    }
    for (uint32_t c = 0; c < 4u; ++c) {
        const uint8_t ch = GuestByteAt(base, key + c);
        if (ch < 0x20u || ch >= 0x7Fu) {
            return;
        }
    }
    for (uint32_t w = 0; w < 16u; ++w) {
        PutBits(o + 4 + w, Word(base, key + w * 4u));
    }
}

}  // namespace

extern "C" {

/// CONTRACT: 148 floats, Fable 2's layout. [0..3] ambient colour *0x83564BD0+208 and [4..7] its
/// direction +304 (sub_82259C38 binds +208 x 0x83564BC0, `lvx128 v13,r3,r10` r10 = 208);
/// [8..11] the lightmap factor 0x83564BA0; [12..15] normal-map darkening 0x83564BB0; [16..35]
/// the atmosphere block 0x83561560, 5 float4 (sub_821B2F40 `sub_8220F3D8(dev, 64, 0x83561560,
/// 5)`); [36..131] Fable 2's theme slots 0..23 (kThemeSlots); [132..135] the sun twin's colour
/// +208, [136..139] its direction +304, [140] its shadow byte +340, [141] its intensity +444
/// (the light packer scales +208 by it: 0x82224000 `li r6,0x1BC`, 0x82224048 `vmulfp128`);
/// [142] 1 (not found); [143] the scene's global mist strength scene+0x2D4 (the mist binder
/// sub_82C626E8 0x82C627BC); [144..146] the wind direction scene+972..980, [147] its speed
/// scene+984 (scene ctor sub_82C57BA0).
/// Given room for 155, Fable 3's tail the lighting registers need and Fable 2 has no slot for:
/// [148..151] the local ambient colour 0x83564BC0, [152] the AO contrast 0x8329D5E4, [153] the
/// lightmap flicker factor 0x835568B0, [154] the sun shadow light's range scale, *0x835568C0+336,
/// 1 without one (sub_822664B0 0x822664C4..0x822664C8). Returns 148 or 155, or 0 before the
/// ambient light is up.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetClassicFrame(float* out, uint32_t capacity) {
    constexpr uint32_t kFloats = 148u;
    constexpr uint32_t kTailFloats = 155u;
    const uint8_t* base = GuestBase();
    if (out == nullptr || capacity < kFloats || base == nullptr) {
        return 0u;
    }
    const uint32_t floats = capacity >= kTailFloats ? kTailFloats : kFloats;
    std::memset(out, 0, floats * sizeof(float));

    const uint32_t ambient = Word(base, kAmbientLight);
    if (!Plausible(ambient)) {
        return 0u;
    }
    Floats(base, ambient + 208u, out, 4u);
    Floats(base, ambient + 304u, out + 4, 4u);
    Floats(base, kLightmapFactor, out + 8, 4u);
    Floats(base, kNormalMapDarkening, out + 12, 4u);
    Floats(base, kAtmosphere, out + 16, 20u);
    out[141] = 1.0f;
    out[142] = 1.0f;

    const uint32_t owner = SkyOwner(base);
    const uint32_t scene = SceneOf(base, owner);
    if (scene != 0u) {
        RemapTheme(base, scene, out + 36, 24u);
        out[143] = Float(base, scene + 0x2D4u);
        Floats(base, scene + 972u, out + 144, 4u);
    }

    const uint32_t sun = TwinOf(base, owner, 8u);
    if (sun != 0u) {
        Floats(base, sun + 208u, out + 132, 4u);
        Floats(base, sun + 304u, out + 136, 4u);
        out[140] = static_cast<float>(GuestByteAt(base, sun + 340u));
        out[141] = Float(base, sun + 444u);
    }

    if (floats == kTailFloats) {
        Floats(base, kLocalAmbient, out + 148, 4u);
        out[152] = Float(base, kAOContrast);
        out[153] = Float(base, kLightmapFlicker);
        const uint32_t shadow_light = Word(base, kShadowLight);
        out[154] = Plausible(shadow_light) ? Float(base, shadow_light + 336u) : 1.0f;
    }
    return floats;
}

/// CONTRACT: 15 slots x 17 words, Fable 2's layout: [0] the element count, then up to 8 (weight
/// bits, hash) pairs in the vector's order. Each slot reads the Fable 3 header kThemeTextureSlots
/// names (theme block scene+16, header +4 begin / +8 end, sub_821A8308); a slot Fable 3 lacks
/// has count 0. Returns 255, or 0 when the scene is not up.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetClassicThemeTextures(uint32_t* out, uint32_t capacity) {
    constexpr uint32_t kSlots = 15u;
    constexpr uint32_t kPairs = 8u;
    constexpr uint32_t kStride = 1u + kPairs * 2u;
    const uint8_t* base = GuestBase();
    if (out == nullptr || capacity < kSlots * kStride || base == nullptr) {
        return 0u;
    }
    const uint32_t scene = ClassicScene(base);
    if (scene == 0u) {
        return 0u;
    }
    std::memset(out, 0, kSlots * kStride * sizeof(uint32_t));
    for (uint32_t slot = 0; slot < kSlots; ++slot) {
        if (kThemeTextureSlots[slot] == 0u) {
            continue;
        }
        uint32_t* row = out + slot * kStride;
        const uint32_t header = scene + kTheme + kThemeTextureSlots[slot];
        const uint32_t begin = Word(base, header + 4u);
        const uint32_t end = Word(base, header + 8u);
        if (!Plausible(begin) || end < begin || end - begin > 0x10000u) {
            continue;
        }
        const uint32_t count = (end - begin) >> 3;
        row[0] = count;
        for (uint32_t i = 0; i < count && i < kPairs; ++i) {
            row[1u + i * 2u] = Word(base, begin + i * 8u);
            row[2u + i * 2u] = Word(base, begin + i * 8u + 4u);
        }
    }
    return kSlots * kStride;
}

/// CONTRACT: 789 floats, Fable 2's layout: [0..775] Fable 2's 194 theme parameters x 4 lanes,
/// remapped by name from Fable 3's packed block at scene+16 (kThemeSlots; a float in lane 0);
/// [776..779] the sky object's sun colour S+144, [780..783] its sun direction S+160, [784..787]
/// its moon direction S+208, as sub_82251098 writes them each frame (0x8225151C `stvx128 v0`
/// at 0x80 and 0x90: lerp((1, 1, 1, 0), T+192 sunset colour, t), 0x82251514 S+96 to 0xA0,
/// 0x82251518 S+112 to 0xD0), S the first type-24 object (kSky); [788] the moon phase, zero:
/// its Fable 3 field is not established. Returns 789, or 0 when the scene is not up.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetClassicTheme(float* out, uint32_t capacity) {
    constexpr uint32_t kFloats = kThemeParams * 4u + 13u;
    const uint8_t* base = GuestBase();
    if (out == nullptr || capacity < kFloats || base == nullptr) {
        return 0u;
    }
    const uint32_t scene = ClassicScene(base);
    if (scene == 0u) {
        return 0u;
    }
    std::memset(out, 0, kFloats * sizeof(float));
    RemapTheme(base, scene, out, kThemeParams);
    uint32_t sky = 0u;
    ForEachTyped(base, scene, kSky, [&](uint32_t begin, uint32_t end) {
        sky = begin < end ? Word(base, begin) : 0u;
        return false;
    });
    if (Plausible(sky)) {
        float* sun_moon = out + kThemeParams * 4u;
        Floats(base, sky + 144u, sun_moon, 4u);
        Floats(base, sky + 160u, sun_moon + 4, 4u);
        Floats(base, sky + 208u, sun_moon + 8, 4u);
    }
    return kFloats;
}

/// CONTRACT: the light manager's live entries (scene+0x4F4; entries *(LM+8), 32 bytes, light at
/// +16; mask LM+24, sub_822327E8), 20 floats each as Fable 2's: [0..3] the entry's sphere
/// (sub_82CCF180: (+288, +448 x +320), radius 1e10 for a directional), [4..16] LightRow, [17] the
/// entry index, [18] the light's address (bits), [19] the colour's w +220, which every group
/// packer copies with the colour (point 0x82224018 `lvx128 v9,r3,r11` r11 = 0xD0; Fable 2's
/// row has 0 there). Returns the lights written.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetClassicLights(float* out, uint32_t capacity) {
    constexpr uint32_t kStride = 20u;
    const uint8_t* base = GuestBase();
    if (out == nullptr || capacity < kStride || base == nullptr) {
        return 0u;
    }
    uint32_t entries = 0u;
    uint64_t mask = 0u;
    if (!ManagerEntries(base, ClassicScene(base), &entries, &mask) || mask == 0u) {
        return 0u;
    }
    uint32_t written = 0u;
    for (uint32_t index = 0; index < 64u && (written + 1u) * kStride <= capacity; ++index) {
        if ((mask & (1ull << index)) == 0u) {
            continue;
        }
        const uint32_t entry = entries + index * 32u;
        const uint32_t light = Word(base, entry + 16u);
        if (!Plausible(light)) {
            continue;
        }
        float* o = out + written * kStride;
        Floats(base, entry, o, 4u);
        LightRow(base, light, o);
        o[17] = static_cast<float>(index);
        PutBits(o + 18, light);
        o[19] = Float(base, light + 220u);
        ++written;
    }
    return written;
}

/// CONTRACT: every light of the scene's category list (scene+1000, category nodes +20..+24 with
/// flag 1, objects +8..+12), less type 11, +342 and +343 — the set the manager's gather
/// sub_821F3980 tests (0x821F3A68 +343, 0x821F3A88 type 11 / +342). 36 floats each as Fable 2's:
/// [0..3] sphere +112, [4..16] LightRow, [17] 0, [18] the address (bits), [19] 0, [20..22]
/// position +288, [23] fade distance +328, [24] fade range +332, [25] flicker brightness +224,
/// [26] the flicker walk +432, [27..29] AABB min +80, [30..32] max +96. Returns the lights written.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetClassicSceneLights(float* out, uint32_t capacity) {
    constexpr uint32_t kStride = 36u;
    const uint8_t* base = GuestBase();
    if (out == nullptr || capacity < kStride || base == nullptr) {
        return 0u;
    }
    const uint32_t scene = ClassicScene(base);
    if (scene == 0u) {
        return 0u;
    }
    const uint32_t list = scene + kTypedList;
    const uint32_t nodes_begin = Word(base, list + 20u);
    const uint32_t nodes_end = Word(base, list + 24u);
    if (!Plausible(nodes_begin) || nodes_end < nodes_begin || (nodes_end - nodes_begin) % 24u != 0u ||
        (nodes_end - nodes_begin) / 24u > kMaxNodes) {
        return 0u;
    }
    uint32_t written = 0u;
    for (uint32_t node = nodes_begin; node < nodes_end && (written + 1u) * kStride <= capacity; node += 24u) {
        if ((Word(base, node + 4u) & 1u) == 0u) {
            continue;
        }
        const uint32_t objects = Word(base, node + 8u);
        const uint32_t objects_end = Word(base, node + 12u);
        if (!Plausible(objects) || objects_end < objects || objects_end - objects > 4u * 4096u) {
            continue;
        }
        for (uint32_t at = objects; at < objects_end && (written + 1u) * kStride <= capacity; at += 4u) {
            const uint32_t light = Word(base, at);
            const uint32_t type = Word(base, light + 4u);
            if (!Plausible(light) || type < 8u || type > 10u || GuestByteAt(base, light + 342u) != 0u ||
                GuestByteAt(base, light + 343u) != 0u) {
                continue;
            }
            float* o = out + written * kStride;
            std::memset(o, 0, kStride * sizeof(float));
            Floats(base, light + 112u, o, 4u);
            LightRow(base, light, o);
            PutBits(o + 18, light);
            Floats(base, light + 288u, o + 20, 3u);
            o[23] = Float(base, light + 328u);
            o[24] = Float(base, light + 332u);
            o[25] = Float(base, light + 224u);
            o[26] = Float(base, light + 432u);
            Floats(base, light + 80u, o + 27, 3u);
            Floats(base, light + 96u, o + 30, 3u);
            ++written;
        }
    }
    return written;
}

/// CONTRACT: per live spot (type 9) of the manager: [0] entry index, [1] plane count (<= 16),
/// [2..65] the planes at +560, count at +816. out[0] = spots; returns the floats written.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetSpotFrustums(float* out, uint32_t capacity) {
    constexpr uint32_t kRecord = 66u;
    if (out == nullptr || capacity < 1u) {
        return 0u;
    }
    out[0] = 0.0f;
    const uint8_t* base = GuestBase();
    uint32_t entries = 0u;
    uint64_t mask = 0u;
    if (base == nullptr || !ManagerEntries(base, ClassicScene(base), &entries, &mask)) {
        return 1u;
    }
    uint32_t used = 1u;
    uint32_t spots = 0u;
    for (uint32_t index = 0; index < 64u && used + kRecord <= capacity; ++index) {
        if ((mask & (1ull << index)) == 0u) {
            continue;
        }
        const uint32_t light = Word(base, entries + index * 32u + 16u);
        if (!Plausible(light) || Word(base, light + 4u) != 9u) {
            continue;
        }
        uint32_t count = Word(base, light + 816u);
        count = count > 16u ? 16u : count;
        float* f = out + used;
        std::memset(f, 0, kRecord * sizeof(float));
        f[0] = static_cast<float>(index);
        f[1] = static_cast<float>(count);
        Floats(base, light + 560u, f + 2, count * 4u);
        used += kRecord;
        ++spots;
    }
    out[0] = static_cast<float>(spots);
    return used;
}

/// CONTRACT: the manager's points and spots, 16 floats each as Fable 2's: [0] entry index,
/// [1..3] position +288, [4..6] direction +304, [7] has a map, [8] fovY 2 x +324, [9] near +272,
/// [10] far +448 x +320 (the spot camera sub_82C87118: `lfs 0x110`, `lfs 0x1C0` x `lfs 0x140`),
/// [11] depth bias +264, [12] slope-scale bias +268. A spot has a map when its shadow object
/// S = +176 is up (S+8), +340 is set and +342 is not (sub_82CE09E0; S+0x14 is the light,
/// sub_82CBC790 0x82CBC7A0). Fable 3's group packers read three stated terms Fable 2's row has
/// no slot for, so they go in its unused tail: [13] cone cos +420, [14] 1 / (1 - cos) +424
/// (spot 0x82C8701C / 0x82C87024), [15] 1 / range +428 (point 0x82224098, spot 0x82C86F44),
/// all three copied from the light's description (sub_8229B940 0x8229BA60..0x8229BA80 into
/// L+0xC0). Returns the lights written.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetClassicLocalLights(float* out, uint32_t capacity) {
    constexpr uint32_t kStride = 16u;
    const uint8_t* base = GuestBase();
    if (out == nullptr || capacity < kStride || base == nullptr) {
        return 0u;
    }
    uint32_t entries = 0u;
    uint64_t mask = 0u;
    if (!ManagerEntries(base, ClassicScene(base), &entries, &mask)) {
        return 0u;
    }
    uint32_t written = 0u;
    for (uint32_t index = 0; index < 64u && (written + 1u) * kStride <= capacity; ++index) {
        if ((mask & (1ull << index)) == 0u) {
            continue;
        }
        const uint32_t light = Word(base, entries + index * 32u + 16u);
        const uint32_t type = Plausible(light) ? Word(base, light + 4u) : 0u;
        if (type != 8u && type != 9u) {
            continue;
        }
        float* o = out + written * kStride;
        std::memset(o, 0, kStride * sizeof(float));
        o[0] = static_cast<float>(index);
        Floats(base, light + 288u, o + 1, 3u);
        Floats(base, light + 304u, o + 4, 3u);
        const uint32_t shadow = Word(base, light + 176u);
        const bool mapped = type == 9u && Plausible(shadow) && GuestByteAt(base, shadow + 8u) != 0u &&
                            GuestByteAt(base, light + 340u) != 0u && GuestByteAt(base, light + 342u) == 0u;
        o[7] = mapped ? 1.0f : 0.0f;
        o[8] = 2.0f * Float(base, light + 324u);
        o[10] = Float(base, light + 448u) * Float(base, light + 320u);
        o[13] = Float(base, light + 420u);
        o[14] = Float(base, light + 424u);
        o[15] = Float(base, light + 428u);
        if (mapped) {
            o[9] = Float(base, light + 272u);
            o[11] = Float(base, light + 264u);
            o[12] = Float(base, light + 268u);
        }
        ++written;
    }
    return written;
}

/// CONTRACT: the type-36 objects of the scene's typed list (ctor 0x82C4AE10 `li r4,0x24`), 8 floats
/// each as Fable 2's: [0..2] previous centre +176, [3] previous radius +224, [4..6] newest centre
/// +192, [7] newest radius +228 (the shift sub_82C8D2A0 moves +192 -> +176 and +228 -> +224).
/// Returns the spheres written.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetClassicShadowSpheres(float* out, uint32_t capacity) {
    constexpr uint32_t kStride = 8u;
    const uint8_t* base = GuestBase();
    if (out == nullptr || capacity < kStride || base == nullptr) {
        return 0u;
    }
    const uint32_t scene = ClassicScene(base);
    if (scene == 0u) {
        return 0u;
    }
    uint32_t written = 0u;
    ForEachTyped(base, scene, kShadowSphere, [&](uint32_t begin, uint32_t end) {
        for (uint32_t at = begin; at + 4u <= end && (written + 1u) * kStride <= capacity; at += 4u) {
            const uint32_t sphere = Word(base, at);
            if (!Plausible(sphere)) {
                continue;
            }
            float* o = out + written * kStride;
            Floats(base, sphere + 176u, o, 3u);
            o[3] = Float(base, sphere + 224u);
            Floats(base, sphere + 192u, o + 4, 3u);
            o[7] = Float(base, sphere + 228u);
            ++written;
        }
        return true;
    });
    return written;
}

/// CONTRACT: the type-27 objects of the scene's typed list (factory sub_82C3CF58), 36 floats each
/// at Fable 2's offsets, which the binder sub_82C626E8 reads unchanged (+184, +192, +316, +320,
/// +344, +352, +368): [0..2] bounds min +80, [3..5] max +96, [6..9] colour +192, [10] density
/// +184, [11] noise offset +216, [12] time speed +220, [13..14] tiling +264, [15] +316, [16..19]
/// +320, [20] global-strength weight +344, [21..24] +352, [25..28] +368, [29..31] extents +224,
/// [32..34] origin +240, [35] 1 when the height texture +276 is set. Returns the mists written.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetClassicMists(float* out, uint32_t capacity) {
    constexpr uint32_t kStride = 36u;
    const uint8_t* base = GuestBase();
    if (out == nullptr || capacity < kStride || base == nullptr) {
        return 0u;
    }
    const uint32_t scene = ClassicScene(base);
    if (scene == 0u) {
        return 0u;
    }
    uint32_t written = 0u;
    ForEachTyped(base, scene, kMist, [&](uint32_t begin, uint32_t end) {
        for (uint32_t at = begin; at + 4u <= end && (written + 1u) * kStride <= capacity; at += 4u) {
            const uint32_t mist = Word(base, at);
            if (!Plausible(mist)) {
                continue;
            }
            float* o = out + written * kStride;
            Floats(base, mist + 80u, o, 3u);
            Floats(base, mist + 96u, o + 3, 3u);
            Floats(base, mist + 192u, o + 6, 4u);
            o[10] = Float(base, mist + 184u);
            o[11] = Float(base, mist + 216u);
            o[12] = Float(base, mist + 220u);
            Floats(base, mist + 264u, o + 13, 2u);
            o[15] = Float(base, mist + 316u);
            Floats(base, mist + 320u, o + 16, 4u);
            o[20] = Float(base, mist + 344u);
            Floats(base, mist + 352u, o + 21, 4u);
            Floats(base, mist + 368u, o + 25, 4u);
            Floats(base, mist + 224u, o + 29, 3u);
            Floats(base, mist + 240u, o + 32, 3u);
            o[35] = Plausible(Word(base, mist + 276u)) ? 1.0f : 0.0f;
            ++written;
        }
        return false;
    });
    return written;
}

/// CONTRACT: the quad particle groups (R+240 == 1) of types 12 and 30 (ctors sub_82248718 /
/// sub_82C4A010 `li r4,0xC` / `li r4,0x1E`) in Fable 2's layout: per group a 340-float header,
/// then count x 12 floats. R = the render object, D = *(*(R+180)+4), M = *(D+48), all at Fable
/// 2's offsets, except: [12..15] the fades R+408..420 (sub_82C54180 `lfs 0x198..0x1A4`), [16] 0
/// (Fable 3's fade constant has no alpha lane), [17] the scale R+424 (the keyframe scale's
/// multiplier, sub_82C54220), [18..20] 0. The records live in a vertex buffer, not D+8, so each
/// group's count x 12 floats are zero. [7] owner, [51..53] the lit ambient and [54] probes are 0.
/// out[0] = groups, out[1] = floats used; returns the floats used, 0 when none.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetClassicParticles(float* out, uint32_t capacity) {
    constexpr uint32_t kHeader = 340u;
    constexpr uint32_t kRecord = 12u;
    constexpr uint32_t kMaxRecords = 8192u;
    const uint8_t* base = GuestBase();
    if (out == nullptr || capacity < 2u + kHeader || base == nullptr) {
        return 0u;
    }
    const uint32_t scene = ClassicScene(base);
    if (scene == 0u) {
        return 0u;
    }
    uint32_t used = 2u;
    uint32_t groups = 0u;
    const auto visit = [&](uint32_t begin, uint32_t end) {
        for (uint32_t at = begin; at + 4u <= end; at += 4u) {
            const uint32_t r = Word(base, at);
            if (!Plausible(r) || Word(base, r + 240u) != 1u) {
                continue;
            }
            const uint32_t count = Word(base, r + 176u);
            const uint32_t data = Word(base, Word(base, r + 180u) + 4u);
            const uint32_t material = Word(base, data + 48u);
            if (count == 0u || count > kMaxRecords || !Plausible(data) || !Plausible(material)) {
                continue;
            }
            if (used + kHeader + count * kRecord > capacity) {
                return false;
            }
            float* h = out + used;
            std::memset(h, 0, (kHeader + count * kRecord) * sizeof(float));
            PutBits(h + 0, r);
            h[1] = 1.0f;
            h[2] = static_cast<float>(count);
            h[3] = static_cast<float>(static_cast<int32_t>(Word(base, r + 244u)));
            Floats(base, r + 80u, h + 4, 3u);
            Floats(base, r + 96u, h + 8, 3u);
            Floats(base, r + 408u, h + 12, 4u);
            h[17] = Float(base, r + 424u);
            Floats(base, r + 320u, h + 21, 16u);
            PutBits(h + 37, material);
            const uint32_t textures = Word(base, material + 180u);
            h[38] = static_cast<float>(textures > 4u ? 4u : textures);
            h[39] = static_cast<float>(Word(base, material + 36u));
            h[40] = static_cast<float>(Word(base, material + 40u));
            h[41] = static_cast<float>(Word(base, material + 44u));
            h[42] = Float(base, material + 48u);
            h[43] = static_cast<float>(GuestByteAt(base, material + 52u));
            h[44] = static_cast<float>(GuestByteAt(base, material + 53u));
            h[45] = static_cast<float>(GuestByteAt(base, material + 56u));
            Floats(base, data + 32u, h + 46, 4u);
            const uint32_t keys_begin = Word(base, material + 8u);
            const uint32_t keys_end = Word(base, material + 12u);
            uint32_t keys = Plausible(keys_begin) && keys_end >= keys_begin ? (keys_end - keys_begin) / 40u : 0u;
            keys = keys > 19u ? 19u : keys;
            h[50] = static_cast<float>(keys);
            for (uint32_t k = 0; k < keys; ++k) {
                const uint32_t key = keys_begin + k * 40u;
                float* o = h + 64 + k * 10;
                Floats(base, key, o, 4u);
                Floats(base, key + 16u, o + 4, 6u);
            }
            for (uint32_t t = 0; t < textures && t < 4u; ++t) {
                TextureKey(base, material + 80u + t * 24u, h + 254 + t * 20);
            }
            used += kHeader + count * kRecord;
            ++groups;
        }
        return true;
    };
    ForEachTyped(base, scene, kParticle, visit);
    ForEachTyped(base, scene, kDisplacementParticle, visit);
    out[0] = static_cast<float>(groups);
    out[1] = static_cast<float>(used);
    return groups == 0u ? 0u : used;
}

/// CONTRACT: the trails (R+240 == 2) and double trails (3) of types 12 and 30 in Fable 2's
/// layout: a 288-float header, then (c + 3) x 8 floats. The materials are Fable 2's: TM =
/// *(D+0x40), DM = *(D+0x2C), blend TM+0x24, texture block TM+0x50 with its count at +100 (the
/// draw sub_82C55988 reads TM+36, TM+48, TM+80, TM+180). [12..15] the fades R+408..420, [16] 0;
/// c is D+36 under Fable 2's guard (n >= 2, n <= c <= 4096). The points live in a vertex buffer,
/// not D's ring, so the (c + 3) x 8 floats are zero and [6] closed is 0. [38] owner, [51..53] and
/// [54] are 0. out[0] = groups, out[1] = floats used; returns the floats used, 0 when none.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetClassicTrails(float* out, uint32_t capacity) {
    constexpr uint32_t kHeader = 288u;
    constexpr uint32_t kPoint = 8u;
    constexpr uint32_t kMaxPoints = 4096u;
    const uint8_t* base = GuestBase();
    if (out == nullptr || capacity < 2u + kHeader || base == nullptr) {
        return 0u;
    }
    const uint32_t scene = ClassicScene(base);
    if (scene == 0u) {
        return 0u;
    }
    uint32_t used = 2u;
    uint32_t groups = 0u;
    const auto visit = [&](uint32_t begin, uint32_t end) {
        for (uint32_t at = begin; at + 4u <= end; at += 4u) {
            const uint32_t r = Word(base, at);
            const uint32_t kind = Plausible(r) ? Word(base, r + 240u) : 0u;
            if (kind != 2u && kind != 3u) {
                continue;
            }
            const uint32_t d = Word(base, Word(base, r + 180u) + 4u);
            const uint32_t m = Plausible(d) ? Word(base, d + (kind == 2u ? 0x40u : 0x2Cu)) : 0u;
            if (!Plausible(m)) {
                continue;
            }
            const uint32_t n = Word(base, r + 176u);
            const uint32_t c = Word(base, d + 36u);
            if (n < 2u || c < n || c > kMaxPoints) {
                continue;
            }
            const int32_t sub = static_cast<int32_t>(Word(base, m + (kind == 2u ? 0x20u : 0x14u)));
            if (sub <= 0) {
                continue;
            }
            const uint32_t points = c + 3u;
            if (used + kHeader + points * kPoint > capacity) {
                return false;
            }
            float* h = out + used;
            std::memset(h, 0, (kHeader + points * kPoint) * sizeof(float));
            PutBits(h + 0, r);
            h[1] = static_cast<float>(kind);
            h[2] = static_cast<float>(n);
            h[3] = static_cast<float>(c);
            h[4] = static_cast<float>(sub);
            h[5] = static_cast<float>(2u * ((n - 1u) * static_cast<uint32_t>(sub) + 1u));
            PutBits(h + 7, m);
            const float span = static_cast<float>(n - 1u) * static_cast<float>(sub);
            h[8] = static_cast<float>(sub);
            h[9] = span > 0.0f ? 1.0f / span : 0.0f;
            h[10] = 1.0f / static_cast<float>(sub);
            Floats(base, r + 408u, h + 12, 4u);
            Floats(base, r + 320u, h + 17, 16u);
            if (kind == 2u) {
                h[33] = static_cast<float>(GuestByteAt(base, m + 0x38u));
            }
            Floats(base, d + 48u, h + 34, 4u);
            const uint32_t blend = m + (kind == 2u ? 0x24u : 0x18u);
            h[42] = static_cast<float>(Word(base, blend));
            h[43] = static_cast<float>(Word(base, blend + 4u));
            h[44] = static_cast<float>(Word(base, blend + 8u));
            h[45] = Float(base, blend + 12u);
            h[46] = static_cast<float>(GuestByteAt(base, blend + 16u));
            h[47] = static_cast<float>(GuestByteAt(base, blend + 17u));
            const uint32_t block = m + (kind == 2u ? 0x50u : 0x2Cu);
            const uint32_t textures = Word(base, block + 100u);
            h[48] = static_cast<float>(textures > 4u ? 4u : textures);
            const uint32_t keys_begin = Word(base, m + 8u);
            const uint32_t keys_end = Word(base, m + 12u);
            uint32_t keys = Plausible(keys_begin) && keys_end >= keys_begin ? (keys_end - keys_begin) / 28u : 0u;
            keys = keys > 19u ? 19u : keys;
            h[50] = static_cast<float>(keys);
            h[55] = GuestByteAt(base, m + (kind == 2u ? 0x34u : 0x28u)) != 0u ? 1.0f : 0.0f;
            for (uint32_t k = 0; k < keys; ++k) {
                Floats(base, keys_begin + k * 28u, h + 56 + k * 8, 7u);
            }
            for (uint32_t t = 0; t < textures && t < 4u; ++t) {
                TextureKey(base, block + t * 24u, h + 208 + t * 20);
            }
            used += kHeader + points * kPoint;
            ++groups;
        }
        return true;
    };
    ForEachTyped(base, scene, kParticle, visit);
    ForEachTyped(base, scene, kDisplacementParticle, visit);
    out[0] = static_cast<float>(groups);
    out[1] = static_cast<float>(used);
    return groups == 0u ? 0u : used;
}

/// CONTRACT: Fable 2's layout. [0] 1 when the light-zone stack (0x83564BD8 begin / 0x83564BDC
/// end, as the snow draw sub_82C91F28 reads it at 0x82C92B60) has a top zone Z, [1..3] its colour
/// Z+0xD0; [4] the render clock dbl_83556818; [5] n lights; [8..71] the weather's (type 25, ctor
/// sub_82C8E300 `li r7,0x19`) 8 push slots at W+0xB0, start and end vec4 each (sub_82C8F388:
/// W+176+32i / W+192+32i); [72..79] their 1 / d at W+0x1B0; [80 + 8i ..] the manager's entries
/// whose light has +192 == 1: position +288, range +320, colour +208, 0. The rain keeps the two
/// nearest; the cut is the caller's. Returns the floats written, 0 when the scene is not up.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetClassicWeather(float* out, uint32_t capacity) {
    constexpr uint32_t kHeader = 80u;
    constexpr uint32_t kLight = 8u;
    const uint8_t* base = GuestBase();
    if (out == nullptr || capacity < kHeader || base == nullptr) {
        return 0u;
    }
    const uint32_t scene = ClassicScene(base);
    if (scene == 0u) {
        return 0u;
    }
    std::memset(out, 0, kHeader * sizeof(float));

    const uint32_t zones_begin = Word(base, kZonesBegin);
    const uint32_t zones_end = Word(base, kZonesEnd);
    if (Plausible(zones_end) && zones_end > zones_begin && zones_end - zones_begin < 0x10000u) {
        const uint32_t zone = Word(base, zones_end - 4u);
        if (Plausible(zone)) {
            out[0] = 1.0f;
            Floats(base, zone + 0xD0u, out + 1, 3u);
        }
    }

    const uint64_t clock_bits = GuestWord64At(base, kRenderClock);
    double clock = 0.0;
    std::memcpy(&clock, &clock_bits, sizeof(clock));
    out[4] = std::isfinite(clock) ? static_cast<float>(clock) : 0.0f;

    ForEachTyped(base, scene, kWeather, [&](uint32_t begin, uint32_t end) {
        const uint32_t weather = end > begin ? Word(base, begin) : 0u;
        if (Plausible(weather)) {
            Floats(base, weather + 0xB0u, out + 8, 64u);
            Floats(base, weather + 0x1B0u, out + 72, 8u);
        }
        return false;
    });

    uint32_t lights = 0u;
    const uint32_t manager = Word(base, scene + kLightManager);
    const uint32_t entries_begin = Word(base, manager + 8u);
    const uint32_t entries_end = Word(base, manager + 0xCu);
    if (Plausible(entries_begin) && entries_end >= entries_begin && entries_end - entries_begin < 0x40000u) {
        for (uint32_t at = entries_begin; at + 32u <= entries_end; at += 32u) {
            if (kHeader + (lights + 1u) * kLight > capacity) {
                break;
            }
            const uint32_t light = Word(base, at + 16u);
            if (!Plausible(light) || Word(base, light + 192u) != 1u) {
                continue;
            }
            float* row = out + kHeader + lights * kLight;
            Floats(base, light + 288u, row, 3u);
            row[3] = Float(base, light + 320u);
            Floats(base, light + 208u, row + 4, 3u);
            row[7] = 0.0f;
            ++lights;
        }
    }
    out[5] = static_cast<float>(lights);
    return kHeader + lights * kLight;
}

}  // extern "C"
