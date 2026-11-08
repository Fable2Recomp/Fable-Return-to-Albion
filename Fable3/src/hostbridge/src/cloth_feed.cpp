// refii - Fable 3's cloth, as the guest states it: each cloth piece's drawn vertices, published by
// the guest's own draw (Fable3/src/hooks/src/cloth_hooks.cpp; the table in
// src/runtime/src/core/stated_cloth.cpp), joined here to the entity whose model it belongs to.
//
// THE JOIN IS THE RENDER HANDLE. A piece is stated with the handle of the render instance it
// belongs to. An entity holds that handle where its own draw addresses its instance (all Fable 3
// IDB, base named per line):
//
//   component 4 (static mesh) +0x10      its draw virtual (secondary vtable 0x820026AC slot 1,
//                                        sub_82252D00, this = comp+0xC) creates and updates the
//                                        instance through it, `addi r26,r3,4` 0x82252D28 ->
//                                        sub_826B17F8 0x82252F88; the model hash is +0x18
//   component 6 (animated mesh) +0x10    its draw (secondary vtable 0x82002720 slot 1, sub_821F46E0,
//                                        this = comp+0xC) commands it, `lwz r3,4(r22)` 0x821F4760 ->
//                                        sub_821F4630; the model hash is +0x18. (+0x14 is a second
//                                        object, type 34, which the morph draw tints at disc 0x8222E5B0.)
//   component 20 (composite) built part  the 128-byte records at comp+0x38..+0x3C, the part's model
//                                        hash at +0, its instance's handle at +0x20 (sub_8222AFD8:
//                                        `addi r28,r24,0x20` -> sub_826B17F8 disc 0x8222CDAC/disc 0x8222CF00)
//
// Measured on a live Bowerstone run: every component 4 handle at +0x10 named a type-2 (rigid)
// instance whose own +0x10 was the same handle (1721 of 1721 bound), every component 6 one a
// type-5 (skinned) instance (97 of 97); the 9 live cloth pieces joined to 3 composite parts and to
// 4 components 4 (flags sharing one definition).
//
// DRAWN AS CLOTH. The guest draws a record from its cloth piece only in a render frame whose
// kicker found the piece inside its fade (proxy+0x111, sub_821C68D8 0x821C6BF8/0x821C6C20) — only
// then does the instance build the cloth draw item that publishes (sub_82CC8720 0x82CC8CF4); in
// any other frame the record draws its own skinned submesh. So a piece is drawn as cloth while its
// last publish is from the render frame now counting (dword_83597310, incremented by sub_821989D8
// at 0x82198A64 before it kicks the cloth) or the one before it.

#include <cstdint>
#include <cstring>

#include "core/payload_bridge.h"
#include "host_abi.h"
#include "host_core.h"
#include "../../vfs/include/fable3_guest_build.h"

namespace {

using refii::hostcore::GuestWordAt;
using refii::hostcore::SafeKernelMemory;

const refii::guest::Addr kRenderFrame{0x83597310u};
constexpr uint32_t kStaticMesh = 4u;
constexpr uint32_t kAnimatedMesh = 6u;
constexpr uint32_t kComposite = 20u;
constexpr uint32_t kPartBytes = 128u;
constexpr uint32_t kMaxHandles = 64u;
constexpr uint32_t kMaxPieces = 64u;
constexpr uint32_t kNoHandle = 0xFFFFFFFFu;

/// Words per piece in the shell's layout (HostBridge.ClothPieceWords).
constexpr uint32_t kPieceWords = 8u;
constexpr uint32_t kSkinnedFlag = 1u;
constexpr uint32_t kDrawnFlag = 2u;

bool Plausible(uint32_t address) { return address >= 0x10000u && address < 0xC0000000u; }

uint32_t Word(const uint8_t* base, uint32_t address) {
    return Plausible(address) ? GuestWordAt(base, address) : 0u;
}

/// The entity's component `id` from its {id, component} vector at +0x54/+0x58, or 0.
uint32_t Component(const uint8_t* base, uint32_t entity, uint32_t id) {
    const uint32_t first = Word(base, entity + 0x54u);
    const uint32_t last = Word(base, entity + 0x58u);
    if (!Plausible(first) || last < first || last - first > 8u * 256u) {
        return 0u;
    }

    for (uint32_t at = first; at < last; at += 8u) {
        if (GuestWordAt(base, at) == id) {
            const uint32_t component = GuestWordAt(base, at + 4u);
            return Plausible(component) ? component : 0u;
        }
    }

    return 0u;
}

struct Handles {
    uint32_t handle[kMaxHandles];
    uint32_t model[kMaxHandles];
    uint32_t count = 0u;

    void Add(uint32_t h, uint32_t m) {
        if (h != kNoHandle && count < kMaxHandles) {
            handle[count] = h;
            model[count] = m;
            ++count;
        }
    }
};

/// Every render handle the entity draws a model through, with that model's hash.
void EntityHandles(const uint8_t* base, uint32_t entity, Handles* out) {
    for (uint32_t id : {kStaticMesh, kAnimatedMesh}) {
        const uint32_t mesh = Component(base, entity, id);
        if (mesh != 0u) {
            out->Add(GuestWordAt(base, mesh + 0x10u), GuestWordAt(base, mesh + 0x18u));
        }
    }

    const uint32_t composite = Component(base, entity, kComposite);
    if (composite == 0u) {
        return;
    }

    const uint32_t begin = Word(base, composite + 0x38u);
    const uint32_t end = Word(base, composite + 0x3Cu);
    if (!Plausible(begin) || end <= begin || ((end - begin) % kPartBytes) != 0u || end - begin > kPartBytes * 256u) {
        return;
    }

    for (uint32_t record = begin; record < end; record += kPartBytes) {
        out->Add(GuestWordAt(base, record + 0x20u), GuestWordAt(base, record));
    }
}

}  // namespace

extern "C" {

/// The cloth pieces the guest states for `entity`, as rows of 8 words: [0] the piece's key (for
/// RefiiHostGetClothPieceVertices), [1] the model hash of the part it belongs to, [2] the record
/// slot of its proxy in that model's instance — the rigid list's index, or with flag 1 the skinned
/// list's, [3] flags (1 the skinned list, 2 drawn as cloth in the guest's current or last render
/// frame; clear, the record draws its own skinned submesh), [4] its vertex count, [5] a sequence
/// that moves with every publish, [6] the game tick of its current pose, [7] that of the pose kept
/// for the previous tick, 0 when none. Returns the rows written.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetEntityClothPieces(uint32_t entity, uint32_t* out_words, uint32_t max_pieces) {
    rex::memory::Memory* memory = SafeKernelMemory();
    const uint8_t* base = memory == nullptr ? nullptr : memory->virtual_membase();
    if (base == nullptr || out_words == nullptr || max_pieces == 0u || !Plausible(entity)) {
        return 0u;
    }

    Handles handles;
    EntityHandles(base, entity, &handles);
    if (handles.count == 0u) {
        return 0u;
    }

    uint32_t rows[kMaxPieces * REFII_CLOTH_PIECE_WORDS];
    const uint32_t found = refii_bridge_cloth_pieces(handles.handle, handles.count, rows,
                                                     max_pieces < kMaxPieces ? max_pieces : kMaxPieces);
    const uint32_t frame = GuestWordAt(base, kRenderFrame);
    for (uint32_t i = 0u; i < found; ++i) {
        const uint32_t* row = rows + (i * REFII_CLOTH_PIECE_WORDS);
        uint32_t model = 0u;
        for (uint32_t h = 0u; h < handles.count; ++h) {
            if (handles.handle[h] == row[1]) {
                model = handles.model[h];
                break;
            }
        }

        uint32_t* out = out_words + (i * kPieceWords);
        out[0] = row[0];
        out[1] = model;
        out[2] = row[2];
        out[3] = ((row[3] & REFII_CLOTH_SKINNED) != 0u ? kSkinnedFlag : 0u) |
                 (frame - row[5] <= 1u ? kDrawnFlag : 0u);
        out[4] = row[4];
        out[5] = row[6];
        out[6] = row[7];
        out[7] = row[8];
    }

    return found;
}

/// One piece's drawn vertices, 12 floats each — position xyz 1, normal xyz 0, tangent xyz and
/// handedness — in the model's own space (the file's Z-up), as the guest wrote its vertex buffer:
/// the current pose, or with `previous` non-zero the last pose of the previous game tick.
/// `out_tick` is the pose's game tick. Returns the vertex count, 0 when the piece or the pose is
/// not there or does not fit.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetClothPieceVertices(uint32_t key, uint32_t previous, float* out_floats,
                                                              uint32_t max_vertices, uint32_t* out_tick) {
    return refii_bridge_cloth_vertices(key, previous, out_floats, max_vertices, out_tick, nullptr);
}

}  // extern "C"
