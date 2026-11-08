// refii - Fable 3's world, as its hooks state it (Fable3/src/hooks/src/world_hooks.cpp, the table
// in src/runtime/src/core/stated_world.cpp): whether the presented game's level stands, the
// effects the game created and placed, the decals it handed its renderer, the displacement quads
// it added to its render scene, the render objects its effects keep for mesh particles, and which
// heroes are drunk. Each export answers Fable 2's row for row
// (Fable2/src/hostbridge/src/host_bridge.cpp), so the shell reads either.
//
// Every address is the Fable 3 IDB; each offset names its base.
//
// THE LEVEL MANAGER of a game: *(*(*(*(game+0x1C)+4)+0x10)+4) (sub_8235B170: `v3 = *(*(a1+16)+4)`
// with a1 = *(*(game+0x1C)+4), passed to sub_82350AD8). Its level object is a shared_ptr at
// mgr+40 (stored by sub_82350488 at 0x82350590; sub_82350AD8 returns at once when it is null,
// 0x82350AE4). The hooks state up at the loader's success (sub_823535D8) and down at the unload.
//
// VMX LANES. The recompiler stores a vector register byte-reversed (rexglue codegen build_lvx:
// "we reverse the whole vector"), so a stated float4's lanes read f32[3] = x, f32[2] = y,
// f32[1] = z, f32[0] = w. The stated-FX rows carry all four lanes raw, as Fable 2's do; the
// active-FX rows take x, y, z from them.
//
// THE RENDER TWIN's world matrix is at twin+336: three float4 rows written by sub_82CD3AC0
// (`stvx128 v17, r0, r11` with r11 = twin+336 at 0x82CD3BB4, +352, +368) and read by the decal
// draw sub_82CA7010 (`addi r11, r11, 0x150` disc 0x82C72B60, `lvx128` at +0/+0x10/+0x20) — the same
// three rows Fable 2's disc sub_8220B490 loads from its twin+336, so the shell's decal row is the same.

#include <cmath>
#include <cstdint>
#include <cstring>

#include "core/payload_bridge.h"
#include "host_abi.h"
#include "host_core.h"
#include "presented_world.h"
#include "../../vfs/include/fable3_guest_build.h"

// The entity feed's exports (entity_feed.cpp, this library): the heroes and a component list.
extern "C" uint32_t RefiiHostGetCoopPlayerEntities(uint32_t* out_one, uint32_t* out_two);
extern "C" uint32_t RefiiHostGetEntityComponents(uint32_t entity, uint32_t* out_pairs, uint32_t max_pairs);

namespace {

using refii::hostcore::GuestWordAt;
using refii::hostcore::SafeKernelMemory;

constexpr uint32_t kActiveFxWords = 22u;
constexpr uint32_t kStatedFxWords = 10u;
constexpr uint32_t kDecalWords = REFII_WORLD_DECAL_WORDS;
constexpr uint32_t kDecalRowWords = kDecalWords + 12u;
constexpr uint32_t kTwinMatrix = 336u;
/// The twin table: a std::vector of twin pointers indexed by render object id (begin, end).
const refii::guest::Addr kTwinTable{0x835A0080u};
/// The FX mesh particle descriptor's vtable (sub_826243C8, 0x82624738).
const refii::guest::Addr kFxMeshDescriptor{0x820A3934u};
constexpr uint32_t kFxMeshFloats = 24u;
constexpr uint32_t kMaxFxMeshes = 1024u;
constexpr uint32_t kMaxEffects = 1024u;
constexpr uint32_t kQuadRowWords = 2u + REFII_WORLD_QUAD_WORDS;
constexpr uint32_t kMaxQuads = 8u;
/// The screen's width and height as the guest keeps them (qword_835A78B0: 1280, 720 in a run),
/// the extent the drunk warp's quad is built to (0x82187E54 `ld r10, qword_835A78B0`).
const refii::guest::Addr kScreenSize{0x835A78B0u};
/// CECPlayerDrunkenness (registration id 180, the component sub_82187C58 looks up by id at
/// 0x82187D48): the stat at +12, its threshold at +36.
constexpr uint32_t kDrunkennessComponent = 180u;

bool Plausible(uint32_t address) { return address >= 0x10000u && address < 0xC0000000u; }

const uint8_t* GuestBase() {
    rex::memory::Memory* memory = SafeKernelMemory();
    return memory == nullptr ? nullptr : memory->virtual_membase();
}

uint32_t Word(const uint8_t* base, uint32_t address) {
    return Plausible(address) ? GuestWordAt(base, address) : 0u;
}

/// The presented game's level manager, or 0.
uint32_t LevelManager(const uint8_t* base) {
    const uint32_t game = refii::fable3::PresentedGame(base);
    if (game == 0u) {
        return 0u;
    }

    const uint32_t container = Word(base, Word(base, game + 0x1Cu) + 4u);
    const uint32_t manager = Word(base, Word(base, container + 0x10u) + 4u);
    return Plausible(manager) ? manager : 0u;
}

uint32_t Bits(float value) {
    uint32_t bits;
    std::memcpy(&bits, &value, sizeof(bits));
    return bits;
}

/// The rotation's three rows from a unit quaternion (x, y, z, w): the D3D row-vector matrix,
/// row r the image of basis axis r, which is what sub_831B6B58 composes. It stores the stated
/// quaternion whole at emitter+0x10 (`stvx128 v1, r3, r10` 0x831B6B90, lane 0 = x, lane 3 = w:
/// an unrotated effect states (0, 0, 0, 1)), permutes it (`vpermwi128` 0xB1 = (y, x, w, z),
/// 0x4E = (z, w, x, y) at 0x831B6B64/0x831B6B6C), signs lanes with unk_82065200 and selects with
/// unk_82065260/B0/C0, then takes nine 4-lane dots (`vmsum4fp128` 0x831B6C04..0x831B6C24):
///   +0x20 = (x²-y²-z²+w², 2(xy+zw), 2(wy-xz))   stored at 0x831B6C4C
///   +0x30 = (2(zw-xy), w²-x²+y²-z², 2(yz+wx))   0x831B6C50
///   +0x40 = (2(xz+yw), 2(yz-xw), w²-x²-y²+z²)   0x831B6C54
/// and +0x50 the position row with w forced to 1 (0x831B6C60). sub_831B6D68 concatenates those
/// four rows as a row-major 4x4 with the parent's four at *(emitter+208)+0x20 (0x831B6D84..
/// 0x831B6E44: the emitter's columns against the parent's rows), so a row IS an axis image. An
/// unstated or degenerate quaternion gives the identity.
void RowsOf(const float* q, bool stated, float* rows) {
    const float x = q[0], y = q[1], z = q[2], w = q[3];
    const float n = x * x + y * y + z * z + w * w;
    if (!stated || !std::isfinite(n) || n < 1e-6f) {
        const float identity[9] = {1.0f, 0.0f, 0.0f, 0.0f, 1.0f, 0.0f, 0.0f, 0.0f, 1.0f};
        std::memcpy(rows, identity, sizeof(identity));
        return;
    }

    const float s = 2.0f / n;
    rows[0] = 1.0f - s * (y * y + z * z);
    rows[1] = s * (x * y + w * z);
    rows[2] = s * (x * z - w * y);
    rows[3] = s * (x * y - w * z);
    rows[4] = 1.0f - s * (x * x + z * z);
    rows[5] = s * (y * z + w * x);
    rows[6] = s * (x * z + w * y);
    rows[7] = s * (y * z - w * x);
    rows[8] = 1.0f - s * (x * x + y * y);
}

}  // namespace

extern "C" {

/// The presented game's level stands: its manager's loader has succeeded (sub_823535D8 ran) and
/// not unloaded since, and the level object at mgr+40 is still held. 0 before a game is presented,
/// through a load, and after the unload — the host does not mount against it then.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostWorldIsActive(void) {
    const uint8_t* base = GuestBase();
    const uint32_t manager = base == nullptr ? 0u : LevelManager(base);
    if (manager == 0u || refii_bridge_level_is_up(manager) == 0) {
        return 0u;
    }

    return Word(base, manager + 40u) != 0u ? 1u : 0u;
}

/// The transforms the particle wrappers were called with, Fable 2's ten words a row: the handle
/// (the identity the game keys the effect on), the statement sequence, the four position lanes,
/// the four orientation lanes. Rows with no stated position are left out.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetStatedFx(uint32_t* out_words, uint32_t word_capacity) {
    if (out_words == nullptr || word_capacity < kStatedFxWords) {
        return 0u;
    }

    static thread_local uint32_t stated[kMaxEffects * REFII_STATED_FX_WORDS];
    const uint32_t rows = refii_bridge_fx_rows(stated, kMaxEffects * REFII_STATED_FX_WORDS);
    const uint32_t room = word_capacity / kStatedFxWords;
    uint32_t written = 0u;
    for (uint32_t i = 0u; i < rows && written < room; ++i) {
        const uint32_t* row = stated + i * REFII_STATED_FX_WORDS;
        if ((row[3] & REFII_STATED_FX_PLACED) == 0u) {
            continue;
        }

        uint32_t* out = out_words + written * kStatedFxWords;
        out[0] = row[0];
        out[1] = row[2];
        std::memcpy(out + 2, row + 4, 8u * sizeof(uint32_t));
        ++written;
    }

    return written;
}

/// Statements the hooks have seen since boot; 0 means none has run yet.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetStatedFxCount(void) { return refii_bridge_fx_statement_count(); }

/// The live effects, Fable 2's 22 words a row: [0] the handle as the instance, [1] the effect's
/// name hash (FNV-1 of its backslashed path, the particle bank's key), [2] flags — 1 alive,
/// 4 emitting, 8 root: each row is the effect's placement, the position its root emitters take
/// (sub_831B5618 → emitter+0x50) — [3] 0 live particles (the guest's particles are not read),
/// [4..6] the stated position's x, y, z, [7..15] the rotation's rows from the stated orientation,
/// [16] the handle, [17] intensity 1, [18] scale 1, [19] fade 1 (no fade-out is stated), [20] the
/// entity that plays the effect and [21] the entity that one was attached to, as fx_owner_hooks.cpp
/// states them (0 for an effect no CECParticleEmitter plays). An effect is listed from its START
/// until its KILL or its scene's.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetActiveFx(uint32_t* out, uint32_t max_effects) {
    if (out == nullptr || max_effects == 0u) {
        return 0u;
    }

    static thread_local uint32_t stated[kMaxEffects * REFII_STATED_FX_WORDS];
    const uint32_t rows = refii_bridge_fx_rows(stated, kMaxEffects * REFII_STATED_FX_WORDS);
    uint32_t written = 0u;
    for (uint32_t i = 0u; i < rows && written < max_effects; ++i) {
        const uint32_t* row = stated + i * REFII_STATED_FX_WORDS;
        const uint32_t flags = row[3];
        if ((flags & REFII_STATED_FX_STARTED) == 0u || (flags & REFII_STATED_FX_PLACED) == 0u) {
            continue;
        }

        float lanes[4];
        std::memcpy(lanes, row + 4, sizeof(lanes));
        float quaternion[4];
        std::memcpy(quaternion, row + 8, sizeof(quaternion));
        const float position[3] = {lanes[3], lanes[2], lanes[1]};
        const float q[4] = {quaternion[3], quaternion[2], quaternion[1], quaternion[0]};
        if (!std::isfinite(position[0]) || !std::isfinite(position[1]) || !std::isfinite(position[2])) {
            continue;
        }

        float basis[9];
        RowsOf(q, (flags & REFII_STATED_FX_ORIENTED) != 0u, basis);

        uint32_t* fx = out + written * kActiveFxWords;
        fx[0] = row[0];
        fx[1] = row[1];
        fx[2] = 1u | 4u | 8u;
        fx[3] = 0u;
        for (uint32_t lane = 0u; lane < 3u; ++lane) {
            fx[4u + lane] = Bits(position[lane]);
        }
        for (uint32_t k = 0u; k < 9u; ++k) {
            fx[7u + k] = Bits(basis[k]);
        }
        fx[16] = row[0];
        fx[17] = Bits(1.0f);
        fx[18] = Bits(1.0f);
        fx[19] = Bits(1.0f);
        fx[20] = row[12];
        fx[21] = row[13];
        ++written;
    }

    return written;
}

/// The decals the guest handed its renderer (world_hooks.cpp's take of sub_82C73BF8), oldest
/// first, Fable 2's 27 words: [0..14] serial, owner twin, texture name hash, position, direction,
/// half sizes, life, flag; [15..26] the owner twin's world matrix, the three float4 rows at
/// twin+336, zeros without an owner. `out_serial` moves whenever the set changed.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetDecals(uint32_t* out, uint32_t capacity, uint32_t* out_serial) {
    uint32_t stated[50u * kDecalWords];
    const uint32_t count = refii_bridge_world_decals_read(stated, 50u * kDecalWords, out_serial);
    if (out == nullptr) {
        return 0u;
    }

    const uint8_t* base = GuestBase();
    uint32_t written = 0u;
    for (uint32_t i = 0u; i < count && (written + 1u) * kDecalRowWords <= capacity; ++i) {
        uint32_t* row = out + written * kDecalRowWords;
        std::memcpy(row, stated + i * kDecalWords, kDecalWords * sizeof(uint32_t));
        const uint32_t owner = row[1];
        for (uint32_t k = 0u; k < 12u; ++k) {
            row[kDecalWords + k] =
                base != nullptr && Plausible(owner) ? GuestWordAt(base, owner + kTwinMatrix + k * 4u) : 0u;
        }
        ++written;
    }

    return written;
}

/// The render scene's DISPLACEMENT QUADS, Fable 2's row for row: the type-29 objects the guest
/// added (world_hooks.cpp's take of sub_82256458 with the full-screen FXT message, until their
/// remove) — as shipped only the drunk warp. Per quad 16 floats from message 35's fields: [0..3]
/// x0 y0 x1 y1 pixels, [4] z, [5] rotation, [6..13] the corners' uv, [14..15] the guest's screen
/// size (qword_835A78B0), the extent the corners are in. out[0] = quads; returns the floats
/// written.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetDisplacementQuads(float* out, uint32_t capacity) {
    if (out == nullptr || capacity < 1u) {
        return 0u;
    }
    out[0] = 0.0f;
    const uint8_t* base = GuestBase();
    if (base == nullptr) {
        return 0u;
    }

    uint32_t stated[kMaxQuads * kQuadRowWords];
    const uint32_t count = refii_bridge_world_quads_read(stated, kMaxQuads * kQuadRowWords);
    const float width = static_cast<float>(static_cast<int32_t>(GuestWordAt(base, kScreenSize)));
    const float height = static_cast<float>(static_cast<int32_t>(GuestWordAt(base, kScreenSize + 4u)));
    uint32_t used = 1u;
    uint32_t quads = 0u;
    for (uint32_t i = 0u; i < count && used + 16u <= capacity; ++i) {
        const uint32_t* words = stated + i * kQuadRowWords + 2u;
        float* q = out + used;
        for (uint32_t k = 0u; k < 4u; ++k) {
            q[k] = static_cast<float>(static_cast<int32_t>(words[k]));
        }
        std::memcpy(q + 4, words + 4, 10u * sizeof(float));
        q[14] = width;
        q[15] = height;
        used += 16u;
        ++quads;
    }
    out[0] = static_cast<float>(quads);
    return used;
}

/// The FX MESH PARTICLES, Fable 2's row for row: per render object an effect's mesh renderer
/// added (world_hooks.cpp's take of sub_82256458 with an off_820A3934 descriptor, until its
/// remove), its descriptor and the twin the id names in the twin table dword_835A0080 (filled by
/// sub_821F0E70, `stw` at 0x821F0F94, when the render side runs the add). Per mesh 24 floats:
///   [0] descriptor (bits) [1] model hash, descriptor +128 (FNV-1 of the MeshID path,
///   sub_831A4A68 0x831A4B2C) [2] flags, descriptor +20 (0x800 displacement) [3..14] the world
///   rows twin+336 (3 x float4, translation in w; sub_82212F08 writes them from the twin's state,
///   0x82212FB0) [15..18] the tint twin+256 (message 15: `stvx128 v0, r30, r10(256)` 0x8229EC48)
///   [19] alpha, the state block's (*(twin+176)) +104 -> +108 at its +16 (message 14 writes +108,
///   0x8229EC14; a new statement moves +108 to +104 and marks +16 stale with -1, sub_822127E8
///   0x82212848 / 0x82212814; the renderer's sub_822055F8 stores the clamped [0, 1] factor,
///   0x82205660..disc 0x82205D04 — read clamped here, so a stale factor holds the previous alpha)
///   [20..22] the distance fade twin+284 (full within), +288 (slope), +292 (gone beyond; the
///   test sub_821FAAA0 0x821FAB08) [23] twin type (+4).
/// A descriptor no longer of that class (destroyed: sub_826242B0 replaces the vtable, 0x826242DC)
/// or no longer naming the id (desc+4, 0x826246F4) is skipped, as is an id with no twin yet.
/// out[0] = meshes; returns the floats written.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetFxMeshes(float* out, uint32_t capacity) {
    if (out == nullptr || capacity < 1u) {
        return 0u;
    }
    out[0] = 0.0f;
    const uint8_t* base = GuestBase();
    if (base == nullptr) {
        return 0u;
    }

    const auto read_floats = [base](uint32_t at, float* to, uint32_t count) {
        for (uint32_t i = 0u; i < count; ++i) {
            const uint32_t bits = GuestWordAt(base, at + i * 4u);
            std::memcpy(to + i, &bits, sizeof(float));
        }
    };
    const uint32_t table = Word(base, kTwinTable);
    const uint32_t table_end = Word(base, kTwinTable + 4u);
    if (!Plausible(table) || table_end < table) {
        return 1u;
    }

    static thread_local uint32_t pairs[kMaxFxMeshes * 2u];
    const uint32_t stated = refii_bridge_world_fx_meshes_read(pairs, kMaxFxMeshes);
    uint32_t used = 1u;
    uint32_t meshes = 0u;
    for (uint32_t i = 0u; i < stated && used + kFxMeshFloats <= capacity; ++i) {
        const uint32_t id = pairs[i * 2u];
        const uint32_t descriptor = pairs[i * 2u + 1u];
        if (id >= (table_end - table) / 4u || Word(base, descriptor) != kFxMeshDescriptor ||
            Word(base, descriptor + 4u) != id) {
            continue;
        }
        const uint32_t twin = Word(base, table + id * 4u);
        if (!Plausible(twin)) {
            continue;
        }

        float* m = out + used;
        std::memcpy(m + 0, &descriptor, sizeof(float));
        const uint32_t hash = Word(base, descriptor + 128u);
        std::memcpy(m + 1, &hash, sizeof(float));
        m[2] = static_cast<float>(Word(base, descriptor + 20u));
        read_floats(twin + kTwinMatrix, m + 3, 12u);
        read_floats(twin + 256u, m + 15, 4u);
        const uint32_t state = Word(base, twin + 176u);
        float alpha = 1.0f;
        if (Plausible(state)) {
            float lanes[3];
            read_floats(state + 104u, lanes, 2u);
            read_floats(state + 16u, lanes + 2, 1u);
            const float t = std::fmin(std::fmax(lanes[2], 0.0f), 1.0f);
            alpha = lanes[0] + (lanes[1] - lanes[0]) * t;
        }
        m[19] = alpha;
        read_floats(twin + 284u, m + 20, 3u);
        m[23] = static_cast<float>(Word(base, twin + 4u));
        used += kFxMeshFloats;
        ++meshes;
    }
    out[0] = static_cast<float>(meshes);
    return used;
}

/// Which heroes are drunk enough for the warp, the warp's own test (sub_82187C58: the hero's
/// CECPlayerDrunkenness, `*(float*)(comp+12) >= *(float*)(comp+36)` at 0x82187DD0): bit 0 hero
/// one, bit 1 hero two. The guest builds the one warp for the game view's player (the first,
/// `sub_821FF0A8(.., 1)` 0x82187C80); on split screen each view draws it for its own hero.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetDrunkPlayers(void) {
    const uint8_t* base = GuestBase();
    if (base == nullptr) {
        return 0u;
    }

    uint32_t heroes[2] = {0u, 0u};
    RefiiHostGetCoopPlayerEntities(heroes, heroes + 1);
    uint32_t bits = 0u;
    for (uint32_t player = 0u; player < 2u; ++player) {
        uint32_t pairs[2u * 256u];
        const uint32_t count = heroes[player] == 0u ? 0u : RefiiHostGetEntityComponents(heroes[player], pairs, 256u);
        for (uint32_t i = 0u; i < count; ++i) {
            if (pairs[i * 2u] != kDrunkennessComponent || !Plausible(pairs[i * 2u + 1u])) {
                continue;
            }
            const uint32_t component = pairs[i * 2u + 1u];
            float level = 0.0f, limit = 0.0f;
            const uint32_t level_bits = GuestWordAt(base, component + 12u);
            const uint32_t limit_bits = GuestWordAt(base, component + 36u);
            std::memcpy(&level, &level_bits, sizeof(level));
            std::memcpy(&limit, &limit_bits, sizeof(limit));
            if (level >= limit) {
                bits |= 1u << player;
            }
            break;
        }
    }
    return bits;
}

}  // extern "C"
