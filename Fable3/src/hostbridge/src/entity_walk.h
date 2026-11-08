#pragma once

// refii - Fable 3's entity walk and attachment walk over any guest reader (guest_span.h), each
// object's fields read as one span. entity_feed.cpp runs them on guest memory; the test
// (tests/entity_walk_test.cpp) runs them on synthetic records against the per-field reads.
//
// Spans, each inside its object as the Fable 3 IDB (360 disc) allocates it:
//
//   CEntity (PC RTTI; its dtor 0x826180 walks the same {id, component} pairs), 0x98 bytes:
//   `li r3,0x98` / `bl sub_8221F300` then the ctor sub_823F1630 (0x823F1A10..0x823F1A24, again
//   0x823F1C20..0x823F1C2C). Fields [0x18, 0x95):
//     +0x18/+0x1C the 64-bit unique id   `stw r5,0x18(r31)` 0x823F1A94, `stw r11,0x1C(r31)` 0x823F1A9C
//     +0x20       the .gdb record id     `*(_QWORD *)(a1 + 32) = v74` 0x823F3B98 (sub_823F3A98)
//     +0x54/+0x58 the component vector, {u32 id, u32 component} pairs: `lwz r31,0x54(r3)` 0x823F3474,
//                 `lwz r10,0x58(r3)` 0x823F3478, component `lwz r3,4(r31)` 0x823F348C, step
//                 `addi r31,r31,8` (sub_823F3458); ctor zeroes +0x54..+0x5C (0x823F1690..0x823F169C)
//     +0x80       the placement          `lwz r11,4(r3)` / `lwz r30,0x80(r11)` 0x8221B0DC..0x8221B0E0
//                 (the renderable's owner, sub_8221B070); ctor `+0x80 = 0` 0x823F16B8
//     +0x94       flags, 0x40 live       sub_82399558 `(*(_BYTE *)(result + 148) & 0x40)`; ctor 0xCC
//                 at 0x823F16F8; sub_823F3458 `lbz r11,0x94(r3)` 0x823F346C
//   The list node is the entity's own +0xC..+0x17, {next, prev, entity} (sub_82399558: a2[3], a2[4],
//   a2[5] = a2), read as one 12-byte block.
//
//   CECGraphicAppearance (NGraphicAppearance, vtable 0x82003120), component 3, 0x140 bytes:
//   `li r3,0x140` 0x825420BC before its ctor (sub_825420A8). Fields [0x50, 0x118):
//     +0x50 tint RGBA       `addi r31,r3,0x50` 0x82688994, `std` 0x826889F4 / 0x826889FC (sub_82688980)
//     +0x78 scale           `stfs f1,0x78(r31)` 0x826883B0 (sub_82688368)
//     +0x7C alpha           `stfs f31,0x7C(r31)` 0x826884E0 (sub_82688410); read `lfs f13,0x7C(r3)` 0x8221B0B0
//     +0x80 camera alpha    `stfs f31,0x80(r31)` 0x82688658 (sub_82688530); read `lfs f0,0x80(r3)` 0x8221B0A8
//     +0xA0/+0xB0 latch     `stvx128 v0,r31,r7` r7=0xA0 0x8221F008, `stvx128 v13,r31,r4` r4=0xB0
//                           0x8221F028 (sub_8221EFB8); read `lvx128 v0,r31,r7` 0x8221B148
//     +0xD0 highlight RGBA  `addi r11,r31,0xD0` 0x82688C9C, `std` 0x82688D50 / 0x82688D54 (sub_82688C38)
//     +0xE5 drawable        `stb r4,0xE5(r3)` 0x826876C0 (sub_826876B0); read `lbz r11,0xE5(r3)` 0x8221B08C
//     +0xE9 interpolate     `lbz r29,0xE9(r3)` 0x8221B0F4
//     +0x114 draw veto      `lwz r11,0x114(r3)` 0x8221B0C4
//
// The component pairs are read as one block of the stated [begin, end), at most 128 pairs, the
// bytes the per-pair scan reads. Vectors are read 16 bytes at a time.

#include <cmath>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <initializer_list>

#include "guest_span.h"

namespace refii::fable3::walk {

using refii::hostcore::GuestBlockWord;
using refii::hostcore::GuestSpan;
using refii::hostcore::ReadGuestWords;

constexpr uint32_t kMaxListed = 8192u;

/// Words per entity record, the shell's layout (HostBridge.EntityStride): 12 stated, the previous
/// transform and its interpolation flag, the highlight, and eight of appearance.
constexpr uint32_t kEntityStride = 29u;
constexpr uint32_t kOne = 0x3F800000u;
constexpr uint32_t kAppearanceDrawable = 1u;
constexpr uint32_t kAttachmentComponent = 26u;
constexpr uint32_t kAttachmentGripWords = 18u;

inline bool Plausible(uint32_t address) { return address >= 0x10000u && address < 0xC0000000u; }

/// A word at `address`, 0 for an address no guest object lies at.
template <typename Memory>
uint32_t WordAt(const Memory& memory, uint32_t address) {
    return Plausible(address) ? memory.Word(address) : 0u;
}

using EntityFields = GuestSpan<0x18u, 0x95u>;
using RenderableFields = GuestSpan<0x50u, 0x118u>;

/// An entity field at `offset`, 0 where `entity + offset` is no guest address.
inline uint32_t EntityWord(const EntityFields& fields, uint32_t entity, uint32_t offset) {
    return Plausible(entity + offset) ? fields.Word(offset) : 0u;
}

inline bool Live(const EntityFields& fields) { return (fields.Byte(0x94u) & 0x40u) != 0u; }

/// The list node at `node`: the entity it carries and the next node.
template <typename Memory>
void ListNode(const Memory& memory, uint32_t node, uint32_t* out_entity, uint32_t* out_next) {
    uint32_t link[3];
    ReadGuestWords(memory, node, link, 3u);
    *out_next = link[0];
    *out_entity = link[2];
}

/// The entity's component vector, read once; Find answers as the first pair with the id.
struct ComponentPairs {
    static constexpr uint32_t kMaxBytes = 8u * 128u;

    uint8_t bytes[kMaxBytes];
    uint32_t pairs = 0u;

    template <typename Memory>
    ComponentPairs(const Memory& memory, const EntityFields& fields, uint32_t entity) {
        const uint32_t first = EntityWord(fields, entity, 0x54u);
        const uint32_t last = EntityWord(fields, entity, 0x58u);
        if (!Plausible(first) || last < first || last - first > kMaxBytes) {
            return;
        }

        pairs = (last - first + 7u) / 8u;
        memory.Copy(first, bytes, pairs * 8u);
    }

    uint32_t Find(uint32_t id) const {
        for (uint32_t pair = 0; pair < pairs; ++pair) {
            if (GuestBlockWord(bytes, pair * 2u) == id) {
                const uint32_t component = GuestBlockWord(bytes, (pair * 2u) + 1u);
                return Plausible(component) ? component : 0u;
            }
        }
        return 0u;
    }
};

/// THE LIVE PLACEMENT. entity+0x80 is a component-2 (CECPhysics*) object, and its vtable slots
/// 0x50 / 0x54 answer position / orientation; every class states them one of three ways, read
/// here as the getter reads them (Fable 3 IDB, each getter decompiled):
///
///   direct    `lvx128 v0,r4,r11` off the component: position sub_82333A80 (+0x20) or
///             sub_821FC010 (+0x40); orientation sub_829B1C18 (+0x30), or sub_825AC090, which
///             returns the global unk_835587A0 (the light's orientation is shared)
///   delegate  sub_822061E8 / sub_8221AFF8: the object at +0x20 answers its slot 0x0C / 0x10;
///             a null object is the origin and the identity (`vspltisw v0,0`, w = flt_82065480)
///   hybrid    sub_822062B8 / sub_8221E4F0: the body at *(*(+0x20)+0x38) answers slot 0x18 /
///             0x1C while +0xB4 is clear and the chain holds; else position is the cache at
///             +0x80 and orientation the identity
///
/// The vtables are the ones each registered factory stores LAST (component_ids: CECPhysicsSimple
/// sub_82541238 ... CECPhysicsSimulationStatic sub_82541800, which overrides the base ctor
/// sub_827D1BF8's 0x820B1808 with 0x82007DF8 at 0x82541874; the character factories likewise
/// override sub_82761060's 0x820AD878); a class this table does not name is reported through
/// `out_unknown_vtables`, and its row keeps the renderable's latch.
constexpr uint16_t kNoField = 0xFFFFu;

enum class PlacementKind : uint8_t { Direct, Delegate, Hybrid };

struct Placement {
    uint32_t vtable;
    PlacementKind kind;
    uint16_t position;     // Direct: byte offset of the position vector
    uint16_t orientation;  // Direct: byte offset of the quaternion, kNoField = the global
};

constexpr Placement kPlacements[] = {
    {0x82007D20u, PlacementKind::Direct, 0x20u, 0x30u},      // CECPhysicsSimple
    {0x82007A98u, PlacementKind::Direct, 0x20u, 0x30u},      // CECPhysicsSimpleScaled
    {0x82007B70u, PlacementKind::Direct, 0x20u, kNoField},   // CECPhysicsSimpleLight
    {0x820999D8u, PlacementKind::Direct, 0x40u, 0x30u},      // CECPhysicsSimpleNetwork
    {0x82007DF8u, PlacementKind::Delegate, 0u, 0u},          // CECPhysicsSimulationStatic (sub_82541800)
    {0x820B1808u, PlacementKind::Delegate, 0u, 0u},          // the Static base / RopeBridge (sub_827D1BF8)
    {0x820079C0u, PlacementKind::Delegate, 0u, 0u},          // CECPhysicsSimulationDynamic
    {0x82007FA8u, PlacementKind::Delegate, 0u, 0u},          // CECPhysicsSimulationKeyframed
    {0x82007C48u, PlacementKind::Hybrid, 0u, 0u},            // CECPhysicsSimulationCharacterControlled (sub_82541B30)
    {0x82007ED0u, PlacementKind::Hybrid, 0u, 0u},            // CECPhysicsSimulationCharacterNavigator (sub_82541C38)
    {0x820AD878u, PlacementKind::Hybrid, 0u, 0u},            // their base (sub_82761060)
};

/// The objects a delegate or hybrid placement asks, by vtable: {position, orientation} offsets
/// of the vectors their getters return, or `hop` when the getter asks the body at +0x38 instead.
///   0x82006FF8  slot 0x0C sub_82BFA8F0 `li r11,0x50`, slot 0x10 sub_827375A8 `li r11,0x60`
///   0x820070F8  slot 0x0C sub_821FC010 `li r11,0x40`, slot 0x10 sub_82BFA8F0 `li r11,0x50`
///   0x82007270  the 80-byte proxy sub_829A9908 / sub_829AA468 give a Dynamic or Keyframed
///               placement (`stw` to +0x20 through sub_827D2C78 at 0x829AA4E4): slot 0x0C
///               sub_82206180 and 0x10 sub_821FAB28 ask `*(+0x38)` slot 0x18 / 0x1C, a null body
///               answering the origin / identity
///   bodies (slots 0x18 / 0x1C, asked by a hybrid or by that proxy):
///   0x82005C50  sub_82782B18 `li r11,0x100`, sub_82782B28 `li r11,0x110` — on the body itself
///   0x82006070  0x82206130 / 0x821FAAD0: `lwz r11,0x38(r4)`, then 16 bytes at +0x120 / `lvx128`
///               at +0x160 of that inner object; null inner -> origin / identity (its slots
///               0x0C / 0x10, sub_8227D818 / sub_8227D920, are the SETTERS: `stvx128 v1` then
///               sub_82F3A5A8(inner, &v))
///   0x82005DA0  0x82206330: `lwz r11,0x50(r4)`, position at +0xD0 of that inner object;
///               sub_8221E4A8: the quaternion of the 3x3 at inner+0xA0 (three 16-byte columns),
///               through sub_82EF2D88 -> sub_82EF2BA8 (trace > 0: w = sqrt(tr+1)/2, z =
///               (m[1]-m[4])/(4w) ...; else the largest-diagonal branch) — the inner object is a
///               Havok transform, columns then translation
struct PlacementTarget {
    uint32_t vtable;
    uint16_t inner;        // offset of the object the vectors are read from; 0 = this object
    uint16_t position;
    uint16_t orientation;  // a quaternion, or with `matrix` the first of three 16-byte columns
    bool hop;              // the getters ask *(+0x38) its slots 0x18 / 0x1C instead (a body)
    bool matrix;
};

constexpr PlacementTarget kDelegateTargets[] = {
    {0x82006FF8u, 0u, 0x50u, 0x60u, false, false},
    {0x820070F8u, 0u, 0x40u, 0x50u, false, false},
    {0x82007270u, 0u, 0u, 0u, true, false},
};

constexpr PlacementTarget kBodyTargets[] = {
    {0x82005C50u, 0u, 0x100u, 0x110u, false, false},
    {0x82006070u, 0x38u, 0x120u, 0x160u, false, false},
    {0x82005DA0u, 0x50u, 0xD0u, 0xA0u, false, true},
};

constexpr uint32_t kSharedLightOrientation = 0x835587A0u;
constexpr uint32_t kIdentity[4] = {0u, 0u, 0u, kOne};

inline float FloatOf(uint32_t bits) {
    float value;
    std::memcpy(&value, &bits, sizeof(value));
    return value;
}

/// A rotation quaternion is unit by definition; a torn read of one is not. True when the four
/// lanes at `at` are finite and unit, with them copied to `out`.
template <typename Memory>
bool UnitQuaternionAt(const Memory& memory, uint32_t at, uint32_t* out) {
    uint32_t bits[4];
    ReadGuestWords(memory, at, bits, 4u);
    float length = 0.0f;
    for (uint32_t lane = 0; lane < 4u; ++lane) {
        const float q = FloatOf(bits[lane]);
        if (!std::isfinite(q)) {
            return false;
        }
        length += q * q;
    }

    if (!(length > 0.98f && length < 1.02f)) {
        return false;
    }

    std::memcpy(out, bits, sizeof(bits));
    return true;
}

template <size_t N>
const PlacementTarget* FindTarget(const PlacementTarget (&targets)[N], uint32_t vtable) {
    for (const PlacementTarget& target : targets) {
        if (target.vtable == vtable) {
            return &target;
        }
    }
    return nullptr;
}

/// sub_82EF2BA8: the quaternion of a rotation stored as three 16-byte columns at `at`
/// (element (row r, column c) = word 4c + r). False when the columns are not finite.
template <typename Memory>
bool QuaternionOfColumns(const Memory& memory, uint32_t at, uint32_t* out) {
    uint32_t bits[12];
    ReadGuestWords(memory, at, bits, 12u);
    float m[12];
    for (uint32_t i = 0; i < 12u; ++i) {
        m[i] = FloatOf(bits[i]);
        if (!std::isfinite(m[i])) {
            return false;
        }
    }

    float q[4];
    const float trace = m[0] + m[5] + m[10];
    if (trace > 0.0f) {
        const float s = std::sqrt(trace + 1.0f);
        const float f = 0.5f / s;
        q[3] = s * 0.5f;
        q[0] = (m[6] - m[9]) * f;
        q[1] = (m[8] - m[2]) * f;
        q[2] = (m[1] - m[4]) * f;
    } else {
        uint32_t i = m[5] > m[0] ? 1u : 0u;
        if (m[10] > m[5u * i]) i = 2u;
        const uint32_t j = (i + 1u) % 3u;
        const uint32_t k = (i + 2u) % 3u;
        const float s = std::sqrt((m[5u * i] - (m[5u * j] + m[5u * k])) + 1.0f);
        if (!(s > 0.0f)) {
            return false;
        }
        const float f = 0.5f / s;
        q[i] = s * 0.5f;
        q[j] = (m[(4u * i) + j] + m[(4u * j) + i]) * f;
        q[k] = (m[(4u * i) + k] + m[(4u * k) + i]) * f;
        q[3] = (m[(4u * j) + k] - m[(4u * k) + j]) * f;
    }

    for (uint32_t lane = 0; lane < 4u; ++lane) {
        std::memcpy(&out[lane], &q[lane], sizeof(float));
    }
    return true;
}

/// The vectors a non-hop target states: through its inner object when it has one (a null inner
/// is the origin and the identity, as the getters answer), a quaternion or a column matrix.
template <typename Memory>
bool ReadTarget(const Memory& memory, uint32_t object, const PlacementTarget& target, uint32_t* out_position,
                uint32_t* out_orientation) {
    uint32_t from = object;
    if (target.inner != 0u) {
        from = WordAt(memory, object + target.inner);
        if (from == 0u) {
            std::memset(out_position, 0, 3u * sizeof(uint32_t));
            std::memcpy(out_orientation, kIdentity, sizeof(kIdentity));
            return true;
        }
        if (!Plausible(from)) {
            return false;
        }
    }

    ReadGuestWords(memory, from + target.position, out_position, 3u);
    return target.matrix ? QuaternionOfColumns(memory, from + target.orientation, out_orientation)
                         : UnitQuaternionAt(memory, from + target.orientation, out_orientation);
}

/// A body at `body` (the object a hop proxy or a hybrid keeps at +0x38), by its vtable; false
/// with `*out_vtable` naming it when it is not known.
template <typename Memory>
bool ReadBody(const Memory& memory, uint32_t body, uint32_t* out_position, uint32_t* out_orientation,
              uint32_t* out_vtable) {
    const uint32_t body_vtable = Plausible(body) ? memory.Word(body) : 0u;
    const PlacementTarget* target = FindTarget(kBodyTargets, body_vtable);
    if (target == nullptr) {
        *out_vtable = body_vtable;
        return false;
    }

    return ReadTarget(memory, body, *target, out_position, out_orientation);
}

/// The live transform of the placement `placement` (entity+0x80), as its getters state it.
/// `out_position` takes three lanes, `out_orientation` four. False when the placement, or the
/// object it defers to, is a class this file does not know; `*out_vtable` then names it (0 when
/// there is no placement at all).
template <typename Memory>
bool LivePlacement(const Memory& memory, uint32_t placement, uint32_t* out_position, uint32_t* out_orientation,
                   uint32_t* out_vtable) {
    *out_vtable = 0u;
    if (!Plausible(placement)) {
        return false;
    }

    const uint32_t vtable = memory.Word(placement);
    *out_vtable = vtable;
    const Placement* layout = nullptr;
    for (const Placement& candidate : kPlacements) {
        if (candidate.vtable == vtable) {
            layout = &candidate;
            break;
        }
    }
    if (layout == nullptr) {
        return false;
    }

    switch (layout->kind) {
        case PlacementKind::Direct:
            ReadGuestWords(memory, placement + layout->position, out_position, 3u);
            if (layout->orientation == kNoField) {
                if (!UnitQuaternionAt(memory, kSharedLightOrientation, out_orientation)) {
                    std::memcpy(out_orientation, kIdentity, sizeof(kIdentity));
                }
            } else if (!UnitQuaternionAt(memory, placement + layout->orientation, out_orientation)) {
                return false;
            }
            return true;

        case PlacementKind::Delegate: {
            const uint32_t object = WordAt(memory, placement + 0x20u);
            if (object == 0u) {
                std::memset(out_position, 0, 3u * sizeof(uint32_t));
                std::memcpy(out_orientation, kIdentity, sizeof(kIdentity));
                return true;
            }

            const uint32_t object_vtable = Plausible(object) ? memory.Word(object) : 0u;
            const PlacementTarget* target = FindTarget(kDelegateTargets, object_vtable);
            if (target == nullptr) {
                *out_vtable = object_vtable;
                return false;
            }

            if (!target->hop) {
                return ReadTarget(memory, object, *target, out_position, out_orientation);
            }

            const uint32_t body = WordAt(memory, object + 0x38u);
            if (body == 0u) {
                std::memset(out_position, 0, 3u * sizeof(uint32_t));
                std::memcpy(out_orientation, kIdentity, sizeof(kIdentity));
                return true;
            }

            return ReadBody(memory, body, out_position, out_orientation, out_vtable);
        }

        case PlacementKind::Hybrid: {
            const uint32_t object = WordAt(memory, placement + 0x20u);
            const uint32_t body = Plausible(object) ? WordAt(memory, object + 0x38u) : 0u;
            if (body == 0u) {
                ReadGuestWords(memory, placement + 0x80u, out_position, 3u);
                std::memcpy(out_orientation, kIdentity, sizeof(kIdentity));
                return true;
            }

            if (!ReadBody(memory, body, out_position, out_orientation, out_vtable)) {
                return false;
            }

            // sub_822062B8: +0xB4 set takes the cached position; the orientation getter has no
            // such gate.
            if (memory.Byte(placement + 0xB4u) != 0u) {
                ReadGuestWords(memory, placement + 0x80u, out_position, 3u);
            }
            return true;
        }
    }

    return false;
}

/// The renderable's targeting highlight as the shell's RGBA word, lanes {B, G, R, A} as Fable 2's
/// `sub_82572C18` packs them (HostBridge.GuestEntity.HighlightColour): the float colour at +0xD0
/// quantised, alpha 255; a black colour is the guest's off, {0,0,0,255}.
inline uint32_t HighlightWord(const RenderableFields& look) {
    uint32_t bytes[3];
    bool lit = false;
    for (uint32_t lane = 0; lane < 3u; ++lane) {
        const float value = FloatOf(look.Word(0xD0u + (lane * 4u)));
        const float clamped = !(value > 0.0f) ? 0.0f : (value > 1.0f ? 1.0f : value);
        bytes[lane] = static_cast<uint32_t>(clamped * 255.0f + 0.5f);
        lit = lit || bytes[lane] != 0u;
    }

    return lit ? (bytes[2] << 24) | (bytes[1] << 16) | (bytes[0] << 8) | 0xFFu : 0x000000FFu;
}

/// Every live entity with a renderable on the list at `em+4`, in the shell's 29-word record (see
/// RefiiHostGetEntities); `note_unknown(vtable)` for each one whose placement class is not known.
template <typename Memory, typename NoteUnknown>
uint32_t WalkEntities(const Memory& memory, uint32_t em, uint32_t* out, uint32_t max_entities,
                      NoteUnknown&& note_unknown) {
    uint32_t written = 0u;
    uint32_t node = WordAt(memory, em + 4u);
    for (uint32_t walked = 0; Plausible(node) && walked < kMaxListed && written < max_entities; ++walked) {
        uint32_t entity = 0u;
        ListNode(memory, node, &entity, &node);
        if (!Plausible(entity)) {
            continue;
        }

        const EntityFields fields(memory, entity);
        if (!Live(fields)) {
            continue;
        }

        const ComponentPairs components(memory, fields, entity);
        const uint32_t renderable = components.Find(3u);
        if (renderable == 0u) {
            continue;
        }

        uint32_t model = 0u;
        for (uint32_t id : {6u, 4u}) {
            const uint32_t mesh = components.Find(id);
            if (mesh != 0u && (model = memory.Word(mesh + 0x18u)) != 0u) {
                break;
            }
        }

        uint32_t* row = out + (written * kEntityStride);
        const RenderableFields look(memory, renderable);

        // The previous transform: the latch the tick's first act took (header, component 3).
        look.Words(0xA0u, row + 12, 3u);
        look.Words(0xB0u, row + 15, 4u);

        // The live transform, from the placement as its own getters read it. A class not in the
        // table, or a torn quaternion, states the latch twice and does not interpolate.
        uint32_t vtable = 0u;
        const bool live = LivePlacement(memory, EntityWord(fields, entity, 0x80u), row, row + 3, &vtable);
        if (!live) {
            std::memcpy(row, row + 12, 7u * sizeof(uint32_t));
            if (vtable != 0u) {
                note_unknown(vtable);
            }
        }

        row[7] = model;
        row[8] = entity;
        row[9] = fields.Word(0x18u);
        row[10] = fields.Word(0x1Cu);
        row[11] = fields.Word(0x20u);
        row[19] = live && look.Byte(0xE9u) != 0u ? 1u : 0u;
        row[20] = HighlightWord(look);

        // The appearance as the renderable holds it (header, component 3). Drawable is the draw
        // gate's own: SetAsDrawable set and +0x114 clear.
        uint32_t* appearance = row + 21;
        appearance[0] = look.Word(0x7Cu);
        appearance[1] = look.Word(0x80u);
        appearance[2] = look.Word(0x78u);
        look.Words(0x50u, appearance + 3, 4u);
        appearance[7] = look.Byte(0xE5u) != 0u && look.Word(0x114u) == 0u ? kAppearanceDrawable : 0u;
        ++written;
    }

    return written;
}

/// The attachment walk: every live entity's component 26 (CECObjectAttachment) list, sentinel at
/// comp+0xC, nodes {next, prev, record}; the child is **(record+4) and is visited only while live
/// (child+0x94 & 0x40), as the guest places it. Visits (parent, record, child, ordinal) up to
/// `max_records`; returns the count visited.
template <typename Memory, typename Visit>
uint32_t ForEachAttachment(const Memory& memory, uint32_t em, uint32_t max_records, Visit&& visit) {
    uint32_t written = 0u;
    uint32_t node = WordAt(memory, em + 4u);
    for (uint32_t walked = 0; Plausible(node) && walked < kMaxListed && written < max_records; ++walked) {
        uint32_t entity = 0u;
        ListNode(memory, node, &entity, &node);
        if (!Plausible(entity)) {
            continue;
        }

        const EntityFields fields(memory, entity);
        if (!Live(fields)) {
            continue;
        }

        const uint32_t attachment = ComponentPairs(memory, fields, entity).Find(kAttachmentComponent);
        const uint32_t sentinel = attachment == 0u ? 0u : WordAt(memory, attachment + 0xCu);
        if (!Plausible(sentinel)) {
            continue;
        }

        uint32_t link = memory.Word(sentinel);
        for (uint32_t depth = 0u; depth < 64u && written < max_records && link != sentinel && Plausible(link);
             ++depth) {
            uint32_t record = 0u;
            ListNode(memory, link, &record, &link);
            const uint32_t child = Plausible(record) ? WordAt(memory, WordAt(memory, record + 4u)) : 0u;
            if (!Plausible(child) || (memory.Byte(child + 0x94u) & 0x40u) == 0u) {
                continue;
            }

            visit(entity, record, child, written);
            ++written;
        }
    }

    return written;
}

/// An LHString field (it holds a Rep: +0 characters, +4 length) into a NUL-terminated buffer,
/// truncated to fit unless `whole`; false when it is empty, does not fit whole, or is not printable.
template <typename Memory>
bool ReadRep(const Memory& memory, uint32_t field, char* out, uint32_t capacity, bool whole) {
    std::memset(out, 0, capacity);
    const uint32_t rep = WordAt(memory, field);
    uint32_t head[2] = {0u, 0u};
    if (Plausible(rep)) {
        ReadGuestWords(memory, rep, head, 2u);
    }

    const uint32_t chars = head[0];
    const uint32_t length = head[1];
    if (!Plausible(chars) || length == 0u || length > 1024u || (whole && length >= capacity)) {
        return false;
    }

    const uint32_t copied = length < capacity ? length : capacity - 1u;
    memory.Copy(chars, out, copied);
    for (uint32_t i = 0; i < copied; ++i) {
        const uint8_t c = static_cast<uint8_t>(out[i]);
        if (c < 0x20u || c > 0x7Eu) {
            std::memset(out, 0, capacity);
            return false;
        }
    }

    return true;
}

}  // namespace refii::fable3::walk
