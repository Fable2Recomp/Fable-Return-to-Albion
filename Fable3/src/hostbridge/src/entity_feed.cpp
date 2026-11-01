// refii - Fable 3's entities, as the guest holds them: the live entity walk, each one's transform
// and model, its skeleton pose, and the heroes.
//
// Every offset is from the Fable 3 IDB, each against its own base:
//
//   Game      = the game of the world on screen, *0x83556534 (presented_world.h)
//   EM        = *(*(*(Game+0x1C)+4)+4)   the entity manager (GetHeroEntityFromGameWorld sub_821A8D40)
//   EM+4      the live list's head; each node {next, prev, entity} sits at entity+0xC, pushed at the
//             front by sub_82399558 (`stw r11,4(r31)` 0x823995D8, count `+0xC` 0x823995E4)
//   EM+0x80 / EM+0x88   the slots holding player one's / player two's hero (** each)
//   entity+0x18  64-bit unique id   +0x20 the .gdb record id   +0x54/+0x58 the component vector,
//                {u32 id, u32 component} pairs   +0x80 the placement   +0x94 flags, 0x40 = live
//   component 3 (the renderable, CECGraphicAppearance, ctor sub_826868B8, 320 bytes):
//             +0xA0 position, +0xB0 orientation quaternion — the PREVIOUS transform: the game tick's
//             first act (sub_821C5928 0x821C5948 -> sub_821EE4D8, over EM+0x70..+0x74) latches the
//             placement's slots 0x50/0x54 there (sub_8221EFB8: `stvx128` at r31+160 and +176) and
//             zeroes the dirty word +0x94; the tick then moves the placement, and the high-frequency
//             pass draws `prev*(1-alpha) + live*alpha` with a slerp (sub_8221B070: `lvx128 v0,r31,r7`
//             r7=160 at 0x8221B138, sub_82206C20 on +176 at 0x8221B198), alpha = (step % 2 + 1) / 2
//             (sub_821CB758 0x821CBCE0..0x821CBCF4);
//             +0xE9 the interpolation ENABLE (`lbz r29,0xE9(r3)` 0x8221B0F4: zero takes the live
//             transform), 1 from the ctor (0x82686AA0); the other `stb ...,0xE9(r)` sites
//             (0x82964718, 0x82849474) are on objects not tied to this component;
//             +0x50 tint, float RGBA (SetTintColour sub_82688A78 -> sub_82688980, bytes/255, ctor 1s);
//             +0x78 scale (SetScale sub_82688368), +0x7C alpha (SetAlpha sub_82688410), +0x80 camera
//             alpha (SetCameraAlpha sub_82688530), all 1.0 from the ctor; the draw gate is
//             `+0xE5 && +0x7C * +0x80 >= 1e-4 && +0x114 == 0` (0x8221B08C..0x8221B0C4);
//             +0xE5 drawable (SetAsDrawable sub_826876B0);
//             +0xD0 the targeting highlight, float RGBA: the per-frame driver sub_8219AFD0 ramps the
//             category colour in through sub_82688C38 (bytes B,G,R,A x 1/255 -> lanes R,G,B,A, stored
//             at +208..+223, 0x82688D50..0x82688D54), +0xC0 strength, +0xC4 shininess, +0xC8 minimum
//             brightness (sub_82688EE8/F78/9008, from flt_8329EACC..D4 = 10, 4, 1); the proxy packet
//             sub_82688690 forwards +0xD0's lanes (words 8..10) and +0xC0/+0xC4 (6, 7), never the
//             Lua SetHighlightColour bytes at +0x60. Off is the ctor's zero colour (dword_83559AD0).
//   the placement, entity+0x80: a component-2 class, read as its slot 0x50 (position) / 0x54
//             (orientation) getters read it — see kPlacements below.
//   component 6 (animated mesh) / 4 (static mesh): +0x18 the model's resource id (FNV-1 of the
//             backslashed path, sub_822D78B0)
//   component 6: +0x1C the published pose holder — sub_821F0218 swaps a fresh block in FIRST
//             (0x821F0278 bl sub_82258410) and only then copies the pose into it (0x821F02B4
//             bl sub_8221F3D8, from comp+0x38) and applies the one-bone override (comp+0x44 index,
//             comp+0x48 transform, 0x821F02B8..0x821F02EC): block +0xC bone count, +0x10 bones,
//             48 bytes each (quaternion xyzw, translation at +0x10, scale at +0x20), model space
//             (sub_82D4E7C0: root stamped identity, children through sub_8222AD80, which reads and
//             writes +0x00/+0x10 only — scale is each bone's own), skeleton order;
//             +0xA8 the skeleton: +0x38..+0x3C bone records {u32 name hash, i32 parent}, +0x9C..+0xA0
//             the rest pose (local, same 48-byte bones)
//
// The guest's world is Z-up, forward +Y; the shell converts.

#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstring>

#include "host_abi.h"
#include "host_core.h"
#include "presented_world.h"

namespace {

using refii::hostcore::GuestByteAt;
using refii::hostcore::GuestWordAt;
using refii::hostcore::SafeKernelMemory;

constexpr uint32_t kMaxListed = 8192u;
constexpr uint32_t kMorphComponent = 20u;
constexpr uint32_t kEyesComponent = 7u;
constexpr uint32_t kMorphPartBytes = 128u;
constexpr uint32_t kBoneBytes = 48u;
constexpr uint32_t kMaxBones = 512u;
constexpr uint32_t kMultipleMeshComponent = 5u;
constexpr uint32_t kMeshRecordBytes = 64u;
constexpr uint32_t kGenderComponent = 13u;
constexpr uint32_t kWornComponent = 18u;
constexpr uint32_t kWornBytes = 0x60u;
constexpr uint32_t kAttachmentComponent = 26u;
constexpr uint32_t kAttachmentName = 64u;
constexpr uint32_t kBuildingComponent = 79u;
constexpr uint32_t kCollectableComponent = 84u;
constexpr uint32_t kBoneMorphBytes = 32u;
constexpr uint32_t kBoneMorphWords = 7u;

/// Words per entity record, the shell's layout (HostBridge.EntityStride): 12 stated, the previous
/// transform and its interpolation flag, the highlight, and eight of appearance.
constexpr uint32_t kEntityStride = 29u;
constexpr uint32_t kOne = 0x3F800000u;
constexpr uint32_t kAppearanceDrawable = 1u;

bool Plausible(uint32_t address) { return address >= 0x10000u && address < 0xC0000000u; }

const uint8_t* GuestBase() {
    rex::memory::Memory* memory = SafeKernelMemory();
    return memory == nullptr ? nullptr : memory->virtual_membase();
}

uint32_t Word(const uint8_t* base, uint32_t address) {
    return Plausible(address) ? GuestWordAt(base, address) : 0u;
}

/// The entity manager of the world standing now (the game's, or the GUI world's), else 0.
uint32_t EntityManager(const uint8_t* base) {
    const uint32_t game = refii::fable3::PresentedGame(base);
    if (game == 0u || GuestByteAt(base, game + 0xBAu) == 0u) {
        return 0u;
    }

    const uint32_t em = Word(base, Word(base, Word(base, game + 0x1Cu) + 4u) + 4u);
    return Plausible(em) ? em : 0u;
}

/// The entity's component `id`, or 0.
uint32_t Component(const uint8_t* base, uint32_t entity, uint32_t id) {
    const uint32_t first = Word(base, entity + 0x54u);
    const uint32_t last = Word(base, entity + 0x58u);
    if (!Plausible(first) || last < first || last - first > 8u * 128u) {
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

bool Live(const uint8_t* base, uint32_t entity) {
    return (GuestByteAt(base, entity + 0x94u) & 0x40u) != 0u;
}

float Float(const uint8_t* base, uint32_t address) {
    const uint32_t bits = GuestWordAt(base, address);
    float value;
    std::memcpy(&value, &bits, sizeof(value));
    return value;
}

/// An LHString field (it holds a Rep: +0 characters, +4 length) into a NUL-terminated buffer,
/// truncated to fit unless `whole`; false when it is empty, does not fit whole, or is not printable.
bool ReadRep(const uint8_t* base, uint32_t field, char* out, uint32_t capacity, bool whole) {
    std::memset(out, 0, capacity);
    const uint32_t rep = Word(base, field);
    const uint32_t chars = Word(base, rep);
    const uint32_t length = Plausible(rep) ? GuestWordAt(base, rep + 4u) : 0u;
    if (!Plausible(chars) || length == 0u || length > 1024u || (whole && length >= capacity)) {
        return false;
    }

    const uint32_t copied = length < capacity ? length : capacity - 1u;
    for (uint32_t i = 0; i < copied; ++i) {
        const uint8_t c = GuestByteAt(base, chars + i);
        if (c < 0x20u || c > 0x7Eu) {
            std::memset(out, 0, capacity);
            return false;
        }

        out[i] = static_cast<char>(c);
    }

    return true;
}

/// Component 5's records [begin, end), checked as a begin <= end <= capacity triple; false when
/// the entity has none or the vector is mid-write.
bool MeshRecords(const uint8_t* base, uint32_t entity, uint32_t* out_begin, uint32_t* out_end) {
    const uint32_t component = Plausible(entity) ? Component(base, entity, kMultipleMeshComponent) : 0u;
    if (component == 0u) {
        return false;
    }

    const uint32_t begin = Word(base, component + 0x10u);
    const uint32_t end = Word(base, component + 0x14u);
    const uint32_t capacity = Word(base, component + 0x18u);
    if (!Plausible(begin) || end <= begin || capacity < end || ((end - begin) % kMeshRecordBytes) != 0u ||
        ((capacity - begin) % kMeshRecordBytes) != 0u || (end - begin) > 0x10000u) {
        return false;
    }

    *out_begin = begin;
    *out_end = end;
    return true;
}

/// The presented game's world manager, *(*(*(*(Game+0x1C)+4)+0x10)+4) (level_feed.cpp), else 0.
uint32_t WorldManager(const uint8_t* base) {
    const uint32_t game = refii::fable3::PresentedGame(base);
    const uint32_t container = game == 0u ? 0u : Word(base, Word(base, game + 0x1Cu) + 4u);
    const uint32_t world = Word(base, Word(base, container + 0x10u) + 4u);
    return Plausible(world) ? world : 0u;
}

/// An MSVC std::list<u32> whose head pointer is at `list` and size at `list+4`: the hashes into
/// `out` (up to `capacity`, `out` may be null). Returns the count walked.
uint32_t LayerList(const uint8_t* base, uint32_t list, uint32_t* out, uint32_t capacity) {
    const uint32_t head = Word(base, list);
    const uint32_t size = GuestWordAt(base, list + 4u);
    if (!Plausible(head) || size > 4096u) {
        return 0u;
    }

    uint32_t written = 0u;
    uint32_t node = GuestWordAt(base, head);
    for (uint32_t guard = 0; guard <= size + 8u && node != head && Plausible(node); ++guard) {
        if (out != nullptr && written < capacity) {
            out[written] = GuestWordAt(base, node + 8u);
        }

        ++written;
        node = GuestWordAt(base, node);
    }

    return written;
}

/// The attachment walk: every live entity's component 26 (CECObjectAttachment) list, sentinel at
/// comp+0xC, nodes {next, prev, record}; the child is **(record+4) and is visited only while live
/// (child+0x94 & 0x40), as the guest places it. Visits (parent, record, child, ordinal) up to
/// `max_records`; returns the count visited.
template <typename Visit>
uint32_t ForEachAttachment(const uint8_t* base, uint32_t max_records, Visit&& visit) {
    const uint32_t em = EntityManager(base);
    if (em == 0u) {
        return 0u;
    }

    uint32_t written = 0u;
    uint32_t node = Word(base, em + 4u);
    for (uint32_t walked = 0; Plausible(node) && walked < kMaxListed && written < max_records; ++walked) {
        const uint32_t entity = GuestWordAt(base, node + 8u);
        node = GuestWordAt(base, node);
        if (!Plausible(entity) || !Live(base, entity)) {
            continue;
        }

        const uint32_t attachment = Component(base, entity, kAttachmentComponent);
        const uint32_t sentinel = attachment == 0u ? 0u : Word(base, attachment + 0xCu);
        if (!Plausible(sentinel)) {
            continue;
        }

        uint32_t link = GuestWordAt(base, sentinel);
        for (uint32_t depth = 0u; depth < 64u && written < max_records && link != sentinel && Plausible(link);
             ++depth) {
            const uint32_t record = GuestWordAt(base, link + 8u);
            link = GuestWordAt(base, link);
            const uint32_t child = Plausible(record) ? Word(base, Word(base, record + 4u)) : 0u;
            if (!Plausible(child) || !Live(base, child)) {
                continue;
            }

            visit(entity, record, child, written);
            ++written;
        }
    }

    return written;
}

/// The hero in slot `slot` (0x80 player one, 0x88 player two), or 0.
uint32_t Hero(const uint8_t* base, uint32_t em, uint32_t slot) {
    const uint32_t entity = Word(base, Word(base, em + slot));
    return Plausible(entity) ? entity : 0u;
}

/// The animated mesh's skeleton and its published pose block; false without a rig.
bool Rig(const uint8_t* base, uint32_t entity, uint32_t* out_skeleton, uint32_t* out_block,
         uint32_t* out_bones) {
    const uint32_t mesh = Component(base, entity, 6u);
    if (mesh == 0u) {
        return false;
    }

    const uint32_t skeleton = Word(base, mesh + 0xA8u);
    const uint32_t block = Word(base, Word(base, mesh + 0x1Cu));
    if (!Plausible(skeleton) || !Plausible(block)) {
        return false;
    }

    const uint32_t count = GuestWordAt(base, block + 0xCu);
    const uint32_t records_first = Word(base, skeleton + 0x38u);
    const uint32_t records_last = Word(base, skeleton + 0x3Cu);
    if (count == 0u || count > kMaxBones || !Plausible(records_first) ||
        (records_last - records_first) / 8u != count) {
        return false;
    }

    *out_skeleton = skeleton;
    *out_block = block;
    *out_bones = count;
    return true;
}

/// `count` 48-byte bones at `from` into 12 floats each, and the skeleton's parents.
void CopyBones(const uint8_t* base, uint32_t from, uint32_t skeleton, uint32_t count,
               float* out_transforms, int32_t* out_parents) {
    const uint32_t records = Word(base, skeleton + 0x38u);
    for (uint32_t bone = 0; bone < count; ++bone) {
        for (uint32_t lane = 0; lane < 12u; ++lane) {
            const uint32_t bits = GuestWordAt(base, from + (bone * kBoneBytes) + (lane * 4u));
            std::memcpy(&out_transforms[(bone * 12u) + lane], &bits, sizeof(bits));
        }

        if (out_parents != nullptr) {
            out_parents[bone] = static_cast<int32_t>(GuestWordAt(base, records + (bone * 8u) + 4u));
        }
    }
}

/// The element a fresh block's bones are constructed with (sub_82D4D2D8, the stores at
/// 0x82D4D528..0x82D4D55C): quaternion (0,0,0,1), translation 0, scale (1,1,1,1).
constexpr uint32_t kFreshBone[12] = {0u, 0u, 0u, kOne, 0u, 0u, 0u, 0u, kOne, kOne, kOne, kOne};

/// Whether the published block is still waiting for its copy. The copy is forward (sub_8221F3D8
/// advances its pointers), so a block caught before or during it ends in constructor bones; a
/// last bone the skeleton rests away from the origin cannot sit there exactly in a copied pose.
bool AwaitingCopy(const uint8_t* base, uint32_t skeleton, uint32_t count, const uint32_t* words) {
    if (count < 2u || std::memcmp(words + ((count - 1u) * 12u), kFreshBone, sizeof(kFreshBone)) != 0) {
        return false;
    }

    const uint32_t rest = Word(base, skeleton + 0x9Cu) + ((count - 1u) * kBoneBytes) + 0x10u;
    for (uint32_t lane = 0; lane < 3u; ++lane) {
        if ((Word(base, rest + (lane * 4u)) & 0x7FFFFFFFu) != 0u) {
            return true;
        }
    }

    return false;
}

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

void ReadLanes(const uint8_t* base, uint32_t at, uint32_t* out, uint32_t lanes) {
    for (uint32_t lane = 0; lane < lanes; ++lane) {
        out[lane] = GuestWordAt(base, at + (lane * 4u));
    }
}

/// A rotation quaternion is unit by definition; a torn read of one is not. True when the four
/// lanes at `at` are finite and unit, with them copied to `out`.
bool UnitQuaternionAt(const uint8_t* base, uint32_t at, uint32_t* out) {
    float q[4];
    float length = 0.0f;
    for (uint32_t lane = 0; lane < 4u; ++lane) {
        q[lane] = Float(base, at + (lane * 4u));
        if (!std::isfinite(q[lane])) {
            return false;
        }
        length += q[lane] * q[lane];
    }

    if (!(length > 0.98f && length < 1.02f)) {
        return false;
    }

    ReadLanes(base, at, out, 4u);
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
bool QuaternionOfColumns(const uint8_t* base, uint32_t at, uint32_t* out) {
    float m[12];
    for (uint32_t i = 0; i < 12u; ++i) {
        m[i] = Float(base, at + (i * 4u));
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
bool ReadTarget(const uint8_t* base, uint32_t object, const PlacementTarget& target, uint32_t* out_position,
                uint32_t* out_orientation) {
    uint32_t from = object;
    if (target.inner != 0u) {
        from = Word(base, object + target.inner);
        if (from == 0u) {
            std::memset(out_position, 0, 3u * sizeof(uint32_t));
            std::memcpy(out_orientation, kIdentity, sizeof(kIdentity));
            return true;
        }
        if (!Plausible(from)) {
            return false;
        }
    }

    ReadLanes(base, from + target.position, out_position, 3u);
    return target.matrix ? QuaternionOfColumns(base, from + target.orientation, out_orientation)
                         : UnitQuaternionAt(base, from + target.orientation, out_orientation);
}

/// A body at `body` (the object a hop proxy or a hybrid keeps at +0x38), by its vtable; false
/// with `*out_vtable` naming it when it is not known.
bool ReadBody(const uint8_t* base, uint32_t body, uint32_t* out_position, uint32_t* out_orientation,
              uint32_t* out_vtable) {
    const uint32_t body_vtable = Plausible(body) ? GuestWordAt(base, body) : 0u;
    const PlacementTarget* target = FindTarget(kBodyTargets, body_vtable);
    if (target == nullptr) {
        *out_vtable = body_vtable;
        return false;
    }

    return ReadTarget(base, body, *target, out_position, out_orientation);
}

/// The placement's live transform, as its getters state it. `out_position` takes three lanes,
/// `out_orientation` four. False when the placement, or the object it defers to, is a class
/// this file does not know; `*out_vtable` then names it (0 when there is no placement at all).
bool LivePlacement(const uint8_t* base, uint32_t entity, uint32_t* out_position, uint32_t* out_orientation,
                   uint32_t* out_vtable) {
    *out_vtable = 0u;
    const uint32_t placement = Word(base, entity + 0x80u);
    if (!Plausible(placement)) {
        return false;
    }

    const uint32_t vtable = GuestWordAt(base, placement);
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
            ReadLanes(base, placement + layout->position, out_position, 3u);
            if (layout->orientation == kNoField) {
                if (!UnitQuaternionAt(base, kSharedLightOrientation, out_orientation)) {
                    std::memcpy(out_orientation, kIdentity, sizeof(kIdentity));
                }
            } else if (!UnitQuaternionAt(base, placement + layout->orientation, out_orientation)) {
                return false;
            }
            return true;

        case PlacementKind::Delegate: {
            const uint32_t object = Word(base, placement + 0x20u);
            if (object == 0u) {
                std::memset(out_position, 0, 3u * sizeof(uint32_t));
                std::memcpy(out_orientation, kIdentity, sizeof(kIdentity));
                return true;
            }

            const uint32_t object_vtable = Plausible(object) ? GuestWordAt(base, object) : 0u;
            const PlacementTarget* target = FindTarget(kDelegateTargets, object_vtable);
            if (target == nullptr) {
                *out_vtable = object_vtable;
                return false;
            }

            if (!target->hop) {
                return ReadTarget(base, object, *target, out_position, out_orientation);
            }

            const uint32_t body = Word(base, object + 0x38u);
            if (body == 0u) {
                std::memset(out_position, 0, 3u * sizeof(uint32_t));
                std::memcpy(out_orientation, kIdentity, sizeof(kIdentity));
                return true;
            }

            return ReadBody(base, body, out_position, out_orientation, out_vtable);
        }

        case PlacementKind::Hybrid: {
            const uint32_t object = Word(base, placement + 0x20u);
            const uint32_t body = Plausible(object) ? Word(base, object + 0x38u) : 0u;
            if (body == 0u) {
                ReadLanes(base, placement + 0x80u, out_position, 3u);
                std::memcpy(out_orientation, kIdentity, sizeof(kIdentity));
                return true;
            }

            if (!ReadBody(base, body, out_position, out_orientation, out_vtable)) {
                return false;
            }

            // sub_822062B8: +0xB4 set takes the cached position; the orientation getter has no
            // such gate.
            if (GuestByteAt(base, placement + 0xB4u) != 0u) {
                ReadLanes(base, placement + 0x80u, out_position, 3u);
            }
            return true;
        }
    }

    return false;
}

/// The renderable's targeting highlight as the shell's RGBA word, lanes {B, G, R, A} as Fable 2's
/// `sub_82572C18` packs them (HostBridge.GuestEntity.HighlightColour): the float colour at +0xD0
/// quantised, alpha 255; a black colour is the guest's off, {0,0,0,255}.
uint32_t HighlightWord(const uint8_t* base, uint32_t renderable) {
    uint32_t bytes[3];
    bool lit = false;
    for (uint32_t lane = 0; lane < 3u; ++lane) {
        const float value = Float(base, renderable + 0xD0u + (lane * 4u));
        const float clamped = !(value > 0.0f) ? 0.0f : (value > 1.0f ? 1.0f : value);
        bytes[lane] = static_cast<uint32_t>(clamped * 255.0f + 0.5f);
        lit = lit || bytes[lane] != 0u;
    }

    return lit ? (bytes[2] << 24) | (bytes[1] << 16) | (bytes[0] << 8) | 0xFFu : 0x000000FFu;
}

}  // namespace

extern "C" {

/// Every live entity with a renderable, in the shell's 29-word record: words 0..6 the live
/// placement, 7..11 model and identity, 12..18 the renderable's per-tick latch (the previous
/// transform), 19 the interpolation enable, 20 the highlight, 21..28 the appearance — alpha,
/// camera alpha, scale, tint RGBA, flags. An entity whose placement class is not known keeps the
/// latch for both transforms with interpolation off, and the class is reported.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetEntities(uint32_t* out, uint32_t max_entities, uint32_t* out_unknown,
                                                    uint32_t* out_unknown_vtables, uint32_t max_unknown_vtables,
                                                    uint32_t* out_unknown_vtable_count) {
    if (out_unknown != nullptr) *out_unknown = 0u;
    if (out_unknown_vtable_count != nullptr) *out_unknown_vtable_count = 0u;
    if (out == nullptr || max_entities == 0u) {
        return 0u;
    }

    const uint8_t* base = GuestBase();
    const uint32_t em = base == nullptr ? 0u : EntityManager(base);
    if (em == 0u) {
        return 0u;
    }

    // Distinct unknown classes as {vtable, entities} pairs; the pair carries the tally.
    uint32_t unknown = 0u;
    uint32_t unknown_vtable_count = 0u;
    const auto note_unknown = [&](uint32_t vtable) {
        ++unknown;
        if (out_unknown_vtables == nullptr) {
            return;
        }
        for (uint32_t i = 0; i < unknown_vtable_count; ++i) {
            if (out_unknown_vtables[i * 2u] == vtable) {
                ++out_unknown_vtables[(i * 2u) + 1u];
                return;
            }
        }
        if (unknown_vtable_count < max_unknown_vtables) {
            out_unknown_vtables[unknown_vtable_count * 2u] = vtable;
            out_unknown_vtables[(unknown_vtable_count * 2u) + 1u] = 1u;
            ++unknown_vtable_count;
        }
    };

    uint32_t written = 0u;
    uint32_t node = Word(base, em + 4u);
    for (uint32_t walked = 0; Plausible(node) && walked < kMaxListed && written < max_entities; ++walked) {
        const uint32_t entity = GuestWordAt(base, node + 8u);
        node = GuestWordAt(base, node);
        if (!Plausible(entity) || !Live(base, entity)) {
            continue;
        }

        const uint32_t renderable = Component(base, entity, 3u);
        if (renderable == 0u) {
            continue;
        }

        uint32_t model = 0u;
        for (uint32_t id : {6u, 4u}) {
            const uint32_t mesh = Component(base, entity, id);
            if (mesh != 0u && (model = GuestWordAt(base, mesh + 0x18u)) != 0u) {
                break;
            }
        }

        uint32_t* row = out + (written * kEntityStride);

        // The previous transform: the latch the tick's first act took (header, component 3).
        ReadLanes(base, renderable + 0xA0u, row + 12, 3u);
        ReadLanes(base, renderable + 0xB0u, row + 15, 4u);

        // The live transform, from the placement as its own getters read it. A class not in the
        // table, or a torn quaternion, states the latch twice and does not interpolate.
        uint32_t vtable = 0u;
        const bool live = LivePlacement(base, entity, row, row + 3, &vtable);
        if (!live) {
            std::memcpy(row, row + 12, 7u * sizeof(uint32_t));
            if (vtable != 0u) {
                note_unknown(vtable);
            }
        }

        row[7] = model;
        row[8] = entity;
        row[9] = GuestWordAt(base, entity + 0x18u);
        row[10] = GuestWordAt(base, entity + 0x1Cu);
        row[11] = GuestWordAt(base, entity + 0x20u);
        row[19] = live && GuestByteAt(base, renderable + 0xE9u) != 0u ? 1u : 0u;
        row[20] = HighlightWord(base, renderable);

        // The appearance as the renderable holds it (header, component 3). Drawable is the draw
        // gate's own: SetAsDrawable set and +0x114 clear.
        uint32_t* appearance = row + 21;
        appearance[0] = GuestWordAt(base, renderable + 0x7Cu);
        appearance[1] = GuestWordAt(base, renderable + 0x80u);
        appearance[2] = GuestWordAt(base, renderable + 0x78u);
        ReadLanes(base, renderable + 0x50u, appearance + 3, 4u);
        appearance[7] = GuestByteAt(base, renderable + 0xE5u) != 0u && GuestWordAt(base, renderable + 0x114u) == 0u
                            ? kAppearanceDrawable
                            : 0u;
        ++written;
    }

    if (out_unknown != nullptr) *out_unknown = unknown;
    if (out_unknown_vtable_count != nullptr) *out_unknown_vtable_count = unknown_vtable_count;
    return written;
}

/// The live pose: the block the guest last published, model space (2).
///
/// TORN IS POSSIBLE. The block is published before it is filled (see the header), so a read can
/// land on constructor bones or a copy in progress. Coherent = the holder still names the block
/// after two copies, the copies agree, and the block is not still awaiting its copy. A torn read
/// still fills the bones; the shell holds its last pose.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetEntityPose(uint32_t entity, float* out_transforms, int32_t* out_parents,
                                                      uint32_t max_bones, uint32_t* out_space, uint32_t* out_torn) {
    if (out_space != nullptr) *out_space = 0u;
    if (out_torn != nullptr) *out_torn = 0u;
    const uint8_t* base = GuestBase();
    uint32_t skeleton = 0u, block = 0u, count = 0u;
    if (base == nullptr || out_transforms == nullptr || !Plausible(entity) ||
        !Rig(base, entity, &skeleton, &block, &count) || count > max_bones) {
        return 0u;
    }

    const uint32_t holder = Word(base, Component(base, entity, 6u) + 0x1Cu);
    static thread_local uint32_t first[kMaxBones * 12u];
    static thread_local uint32_t second[kMaxBones * 12u];
    const uint32_t words = count * 12u;
    bool coherent = false;
    for (int attempt = 0; attempt < 4 && !coherent; ++attempt) {
        const uint32_t published = Word(base, holder);
        const uint32_t bones = Word(base, published + 0x10u);
        if (!Plausible(published) || !Plausible(bones)) {
            return 0u;
        }

        for (uint32_t at = 0; at < words; ++at) first[at] = GuestWordAt(base, bones + (at * 4u));
        for (uint32_t at = 0; at < words; ++at) second[at] = GuestWordAt(base, bones + (at * 4u));
        coherent = Word(base, holder) == published && std::memcmp(first, second, words * 4u) == 0 &&
                   !AwaitingCopy(base, skeleton, count, first);
    }

    std::memcpy(out_transforms, first, words * 4u);
    if (out_parents != nullptr) {
        const uint32_t records = Word(base, skeleton + 0x38u);
        for (uint32_t bone = 0; bone < count; ++bone) {
            out_parents[bone] = static_cast<int32_t>(GuestWordAt(base, records + (bone * 8u) + 4u));
        }
    }

    if (out_space != nullptr) *out_space = 2u;
    if (out_torn != nullptr) *out_torn = coherent ? 0u : 1u;
    return count;
}

/// The skeleton's rest pose, local.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetEntityRestPose(uint32_t entity, float* out_transforms, int32_t* out_parents,
                                                          uint32_t max_bones) {
    const uint8_t* base = GuestBase();
    uint32_t skeleton = 0u, block = 0u, count = 0u;
    if (base == nullptr || out_transforms == nullptr || !Plausible(entity) ||
        !Rig(base, entity, &skeleton, &block, &count) || count > max_bones) {
        return 0u;
    }

    const uint32_t first = Word(base, skeleton + 0x9Cu);
    const uint32_t last = Word(base, skeleton + 0xA0u);
    if (!Plausible(first) || last < first || (last - first) / kBoneBytes != count) {
        return 0u;
    }

    CopyBones(base, first, skeleton, count, out_transforms, out_parents);
    return count;
}

/// Bone names as the model states them: Fable 3 keeps only each name's hash (CRC-32, init 0, no
/// final xor, sub_823F04B8), so a bone is named "0x%08X", as Engine_MdlEngine47 names the mesh's.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetEntityBoneNames(uint32_t entity, char* out_names, uint32_t stride,
                                                           uint32_t max_bones) {
    const uint8_t* base = GuestBase();
    uint32_t skeleton = 0u, block = 0u, count = 0u;
    if (base == nullptr || out_names == nullptr || stride < 11u || !Plausible(entity) ||
        !Rig(base, entity, &skeleton, &block, &count) || count > max_bones) {
        return 0u;
    }

    const uint32_t records = Word(base, skeleton + 0x38u);
    for (uint32_t bone = 0; bone < count; ++bone) {
        std::snprintf(out_names + (bone * stride), stride, "0x%08X", GuestWordAt(base, records + (bone * 8u)));
    }

    return count;
}

REFII_HOST_ABI_EXPORT uint64_t RefiiHostGetEntityUid(uint32_t entity) {
    const uint8_t* base = GuestBase();
    if (base == nullptr || !Plausible(entity)) {
        return 0u;
    }

    return (static_cast<uint64_t>(GuestWordAt(base, entity + 0x18u)) << 32) | GuestWordAt(base, entity + 0x1Cu);
}

/// The {id, component} pairs of the entity's component vector (+0x54..+0x58), as Fable 2's.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetEntityComponents(uint32_t entity, uint32_t* out_pairs, uint32_t max_pairs) {
    const uint8_t* base = GuestBase();
    if (base == nullptr || out_pairs == nullptr || max_pairs == 0u || !Plausible(entity)) {
        return 0u;
    }

    const uint32_t first = Word(base, entity + 0x54u);
    const uint32_t last = Word(base, entity + 0x58u);
    if (!Plausible(first) || last <= first || last - first > 0x4000u) {
        return 0u;
    }

    uint32_t written = 0u;
    for (uint32_t at = first; at + 8u <= last && written < max_pairs; at += 8u) {
        out_pairs[written * 2u] = GuestWordAt(base, at);
        out_pairs[(written * 2u) + 1u] = GuestWordAt(base, at + 4u);
        ++written;
    }

    return written;
}

/// The `Mesh` hashes of component 5 (StaticMultipleMesh): records at comp+0x10 begin, +0x14 end,
/// +0x18 capacity (ctor sub_82542280, push sub_829AEA88), 64 bytes each, the hash at +4.
/// `out_total` is the record count the component states.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetEntityMeshParts(uint32_t entity, uint32_t* out_hashes, uint32_t max_hashes,
                                                           uint32_t* out_total) {
    if (out_total != nullptr) *out_total = 0u;
    if (out_hashes == nullptr || max_hashes == 0u) {
        return 0u;
    }

    const uint8_t* base = GuestBase();
    uint32_t begin = 0u, end = 0u;
    if (base == nullptr || !MeshRecords(base, entity, &begin, &end)) {
        return 0u;
    }

    if (out_total != nullptr) *out_total = (end - begin) / kMeshRecordBytes;
    uint32_t written = 0u;
    for (uint32_t at = begin; at + kMeshRecordBytes <= end && written < max_hashes; at += kMeshRecordBytes) {
        const uint32_t hash = GuestWordAt(base, at + 4u);
        if (hash != 0u) {
            out_hashes[written++] = hash;
        }
    }

    return written;
}

/// Component 5's interior records: the `Mesh` hash of each record whose byte +0x3C is set.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetEntityInteriorMeshes(uint32_t entity, uint32_t* out_hashes,
                                                                uint32_t max_hashes) {
    if (out_hashes == nullptr || max_hashes == 0u) {
        return 0u;
    }

    const uint8_t* base = GuestBase();
    uint32_t begin = 0u, end = 0u;
    if (base == nullptr || !MeshRecords(base, entity, &begin, &end)) {
        return 0u;
    }

    uint32_t written = 0u;
    for (uint32_t at = begin; at + kMeshRecordBytes <= end && written < max_hashes; at += kMeshRecordBytes) {
        const uint32_t hash = GuestWordAt(base, at + 4u);
        if (hash != 0u && GuestByteAt(base, at + 0x3Cu) != 0u) {
            out_hashes[written++] = hash;
        }
    }

    return written;
}

/// Whether the entity's light (component 8, CECGraphicAppearanceLight) states IgnoreZoning: the
/// record's bool, kept at comp+0x9A (sub_82797250 0x82797514), which the light's zone update reads
/// as "zone 0" (sub_827983C0 0x82798414: `v15 = +154 != 0 ? 0 : zone`).
bool LightIgnoresZoning(const uint8_t* base, uint32_t entity) {
    const uint32_t light = Component(base, entity, 8u);
    return light != 0u && GuestByteAt(base, light + 0x9Au) != 0u;
}

/// The lighting zone of the entity's geometry: the renderable's (component 3) +0xF4, which
/// sub_82264F98 keeps by LightZoneMode at +0xF0. A CECBuilding (component 79) in mode 0 is in the
/// zone of its own record id, `entity+0x20 & 0x7FFFFFFF`, while live, else 1 (the static-mesh
/// override sub_829AD718, 0x829AD73C..0x829AD840). 0 when the entity's light ignores zoning.
/// 1 without a renderable.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetEntityLightZone(uint32_t entity) {
    const uint8_t* base = GuestBase();
    if (base == nullptr || !Plausible(entity)) {
        return 1u;
    }

    if (LightIgnoresZoning(base, entity)) {
        return 0u;
    }

    const uint32_t renderable = Component(base, entity, 3u);
    if (renderable == 0u) {
        return 1u;
    }

    if (Component(base, entity, kBuildingComponent) != 0u && GuestWordAt(base, renderable + 0xF0u) == 0u) {
        return Live(base, entity) ? GuestWordAt(base, entity + 0x20u) & 0x7FFFFFFFu : 1u;
    }

    return GuestWordAt(base, renderable + 0xF4u);
}

/// The entity's OWN zone, the renderable's +0xF4 without the static-mesh building override — what
/// a light placed on a CECBuilding entity lights from. 0 when its light ignores zoning, 1 without
/// a renderable.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetEntityOwnLightZone(uint32_t entity) {
    const uint8_t* base = GuestBase();
    if (base == nullptr || !Plausible(entity)) {
        return 1u;
    }

    if (LightIgnoresZoning(base, entity)) {
        return 0u;
    }

    const uint32_t renderable = Component(base, entity, 3u);
    return renderable == 0u ? 1u : GuestWordAt(base, renderable + 0xF4u);
}

/// The renderable's shadow word, +0x90, with 0x100 standing for its two-sided caster byte +0xE1;
/// 0xFFFFFFFF without a renderable.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetEntityCasterFlags(uint32_t entity) {
    const uint8_t* base = GuestBase();
    const uint32_t renderable = base == nullptr || !Plausible(entity) ? 0u : Component(base, entity, 3u);
    if (renderable == 0u) {
        return 0xFFFFFFFFu;
    }

    uint32_t flags = GuestWordAt(base, renderable + 0x90u) & ~0x100u;
    if (GuestByteAt(base, renderable + 0xE1u) != 0u) {
        flags |= 0x100u;
    }

    return flags;
}

/// The renderable's draw distance (+0x24) and fade-range factor (+0x28), written by sub_826873F0
/// (the reverse of Fable 2's order). 1 with both, 0 without a renderable.
REFII_HOST_ABI_EXPORT int RefiiHostGetEntityDrawRange(uint32_t entity, float* out_distance, float* out_fade_factor) {
    if (out_distance == nullptr || out_fade_factor == nullptr) {
        return 0;
    }

    const uint8_t* base = GuestBase();
    const uint32_t renderable = base == nullptr || !Plausible(entity) ? 0u : Component(base, entity, 3u);
    if (renderable == 0u) {
        return 0;
    }

    *out_distance = Float(base, renderable + 0x24u);
    *out_fade_factor = Float(base, renderable + 0x28u);
    return 1;
}

/// The FNV-1 hashes of the activated layers: the world manager's list at +0x40 (head) / +0x44
/// (size), nodes {next, prev, hash}. Returns the count, which may exceed `capacity`.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetActiveLayers(uint32_t* out, uint32_t capacity) {
    const uint8_t* base = GuestBase();
    const uint32_t world = base == nullptr ? 0u : WorldManager(base);
    return world == 0u ? 0u : LayerList(base, world + 0x40u, out, capacity);
}

/// The FNV-1 hashes of the deactivated layers: the world manager's list at +0x4C (head) / +0x50
/// (size). Returns the count, which may exceed `capacity`.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetDeactivatedLayers(uint32_t* out, uint32_t capacity) {
    const uint8_t* base = GuestBase();
    const uint32_t world = base == nullptr ? 0u : WorldManager(base);
    return world == 0u ? 0u : LayerList(base, world + 0x4Cu, out, capacity);
}

/// The UIDs of the destroyed placements the guest will not respawn: the entity manager's map at
/// +0xE8, head *(EM+0xEC), size *(EM+0xF0); nodes +0 left, +4 parent, +8 right, +12 UID hi,
/// +16 UID lo, +20 layer hash, +0x19 isnil.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetDeadEntities(uint64_t* out_uids, uint32_t max_uids) {
    if (out_uids == nullptr || max_uids == 0u) {
        return 0u;
    }

    const uint8_t* base = GuestBase();
    const uint32_t em = base == nullptr ? 0u : EntityManager(base);
    const uint32_t head = em == 0u ? 0u : Word(base, em + 0xECu);
    if (!Plausible(head)) {
        return 0u;
    }

    constexpr uint32_t kMaxNodes = 8192u;
    const uint32_t stated = GuestWordAt(base, em + 0xF0u);
    uint32_t budget = stated < kMaxNodes ? stated + 1u : kMaxNodes;
    uint32_t stack[64];
    uint32_t depth = 0u;
    uint32_t written = 0u;
    const uint32_t root = Word(base, head + 4u);
    if (Plausible(root) && root != head) {
        stack[depth++] = root;
    }

    while (depth != 0u && written < max_uids && budget-- != 0u) {
        const uint32_t node = stack[--depth];
        if (!Plausible(node) || node == head) {
            continue;
        }

        if (GuestByteAt(base, node + 0x19u) == 0u) {
            out_uids[written++] = (static_cast<uint64_t>(GuestWordAt(base, node + 12u)) << 32) |
                                  GuestWordAt(base, node + 16u);
        }

        const uint32_t left = GuestWordAt(base, node);
        const uint32_t right = GuestWordAt(base, node + 8u);
        if (depth + 2u <= 64u) {
            if (Plausible(left) && left != head) stack[depth++] = left;
            if (Plausible(right) && right != head) stack[depth++] = right;
        }
    }

    return written;
}

/// The one-time collectables some player has taken: (entity, mask) pairs for each live entity
/// with component 84, bit 0 when its player-one state (+0xC) is 1 or 2, bit 1 for player two's
/// (+0x10). Entities neither player has taken are left out. Returns the pairs written.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetCollectedKeys(uint32_t* out, uint32_t capacity) {
    if (out == nullptr) {
        return 0u;
    }

    const uint8_t* base = GuestBase();
    const uint32_t em = base == nullptr ? 0u : EntityManager(base);
    if (em == 0u) {
        return 0u;
    }

    const auto taken = [](uint32_t state) { return state == 1u || state == 2u; };
    uint32_t written = 0u;
    uint32_t node = Word(base, em + 4u);
    for (uint32_t walked = 0; Plausible(node) && walked < kMaxListed && written + 2u <= capacity; ++walked) {
        const uint32_t entity = GuestWordAt(base, node + 8u);
        node = GuestWordAt(base, node);
        if (!Plausible(entity) || !Live(base, entity)) {
            continue;
        }

        const uint32_t collectable = Component(base, entity, kCollectableComponent);
        if (collectable == 0u) {
            continue;
        }

        const uint32_t mask = (taken(GuestWordAt(base, collectable + 0xCu)) ? 1u : 0u) |
                              (taken(GuestWordAt(base, collectable + 0x10u)) ? 2u : 0u);
        if (mask != 0u) {
            out[written++] = entity;
            out[written++] = mask;
        }
    }

    return written / 2u;
}

/// Every live attachment: [child, parent, dummy index, ticket] and the parent's dummy name
/// (kAttachmentName bytes per record, NUL-padded). See ForEachAttachment.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetAttachments(uint32_t* out_words, uint32_t max_records, char* out_names) {
    if (out_words == nullptr || max_records == 0u) {
        return 0u;
    }

    const uint8_t* base = GuestBase();
    if (base == nullptr) {
        return 0u;
    }

    return ForEachAttachment(base, max_records, [&](uint32_t parent, uint32_t record, uint32_t child, uint32_t at) {
        uint32_t* words = out_words + (at * 4u);
        words[0] = child;
        words[1] = parent;
        words[2] = GuestWordAt(base, record + 0xCu);
        words[3] = GuestWordAt(base, record + 0x90u);
        if (out_names != nullptr) {
            ReadRep(base, record + 8u, out_names + (at * kAttachmentName), kAttachmentName, false);
        }
    });
}

/// Each live attachment's grip: [child, ticket, record+0x50 as 16 floats] — the rigid inverse of
/// the child's own grip dummy, row-major, row vectors, in RefiiHostGetAttachments' order.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetAttachmentGrips(uint32_t* out_words, uint32_t max_records) {
    if (out_words == nullptr || max_records == 0u) {
        return 0u;
    }

    const uint8_t* base = GuestBase();
    if (base == nullptr) {
        return 0u;
    }

    return ForEachAttachment(base, max_records, [&](uint32_t, uint32_t record, uint32_t child, uint32_t at) {
        uint32_t* words = out_words + (at * 18u);
        words[0] = child;
        words[1] = GuestWordAt(base, record + 0x90u);
        for (uint32_t i = 0u; i < 16u; ++i) {
            words[2u + i] = GuestWordAt(base, record + 0x50u + (i * 4u));
        }
    });
}

/// What each built part covers, component 20's built records (comp+0x38..+0x3C, 128 bytes):
/// model hash +0, layer +0x34, alpha +0x38, the cluster std::set<LHString> head +0x28 / size +0x2C
/// (20-byte nodes: left, parent, right, value +12, isnil +17). A part covers only while opaque
/// (alpha > 0.9999); otherwise its line carries no names. Lines are `hash layer body name;name;...`,
/// hash hex, layer signed, body always 0. Returns the part count.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetEntityPartHides(uint32_t entity, char* out_text, uint32_t text_capacity) {
    if (out_text == nullptr || text_capacity < 2u) {
        return 0u;
    }

    out_text[0] = '\0';
    const uint8_t* base = GuestBase();
    const uint32_t morph = base == nullptr || !Plausible(entity) ? 0u : Component(base, entity, kMorphComponent);
    if (morph == 0u) {
        return 0u;
    }

    const uint32_t begin = Word(base, morph + 0x38u);
    const uint32_t end = Word(base, morph + 0x3Cu);
    if (!Plausible(begin) || end <= begin || ((end - begin) % kMorphPartBytes) != 0u ||
        (end - begin) > kMorphPartBytes * 256u) {
        return 0u;
    }

    uint32_t used = 0u;
    const auto put = [&](const char* text, uint32_t length) -> bool {
        if (used + length + 1u >= text_capacity) {
            return false;
        }

        std::memcpy(out_text + used, text, length);
        used += length;
        out_text[used] = '\0';
        return true;
    };

    uint32_t parts = 0u;
    for (uint32_t record = begin; record + kMorphPartBytes <= end; record += kMorphPartBytes) {
        char head_text[48];
        const int head_length = std::snprintf(head_text, sizeof(head_text), "%08X %d 0", GuestWordAt(base, record),
                                              static_cast<int32_t>(GuestWordAt(base, record + 0x34u)));
        if (head_length <= 0 || !put(head_text, static_cast<uint32_t>(head_length))) {
            break;
        }

        const uint32_t set_head = Word(base, record + 0x28u);
        const uint32_t set_size = GuestWordAt(base, record + 0x2Cu);
        if (Float(base, record + 0x38u) > 0.9999f && Plausible(set_head) && set_size != 0u && set_size <= 64u) {
            uint32_t stack[64];
            uint32_t depth = 0u;
            uint32_t budget = 256u;
            const uint32_t root = Word(base, set_head + 4u);
            if (Plausible(root) && root != set_head) {
                stack[depth++] = root;
            }

            bool first = true;
            while (depth != 0u && budget-- != 0u) {
                const uint32_t node = stack[--depth];
                if (!Plausible(node) || node == set_head) {
                    continue;
                }

                char name[96];
                if (GuestByteAt(base, node + 17u) == 0u && ReadRep(base, node + 12u, name, sizeof(name), true)) {
                    put(first ? " " : ";", 1u);
                    put(name, static_cast<uint32_t>(std::strlen(name)));
                    first = false;
                }

                const uint32_t left = GuestWordAt(base, node);
                const uint32_t right = GuestWordAt(base, node + 8u);
                if (depth + 2u <= 64u) {
                    if (Plausible(left) && left != set_head) stack[depth++] = left;
                    if (Plausible(right) && right != set_head) stack[depth++] = right;
                }
            }
        }

        put("\n", 1u);
        ++parts;
    }

    return parts;
}

/// The body's proportions: the record *(comp6+0xAC) holds a dense vector at +0x10..+0x14, one
/// 32-byte entry per skeleton bone (offset float4 +0, scale float4 +16). Writes [bone, offset xyz,
/// scale xyz] for each bone that is not identity. Returns the entry count.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetBoneMorph(uint32_t entity, uint32_t* out_words, uint32_t word_capacity) {
    if (out_words == nullptr || word_capacity < kBoneMorphWords) {
        return 0u;
    }

    const uint8_t* base = GuestBase();
    const uint32_t mesh = base == nullptr || !Plausible(entity) ? 0u : Component(base, entity, 6u);
    const uint32_t record = mesh == 0u ? 0u : Word(base, mesh + 0xACu);
    const uint32_t skeleton = mesh == 0u ? 0u : Word(base, mesh + 0xA8u);
    if (!Plausible(record) || !Plausible(skeleton)) {
        return 0u;
    }

    const uint32_t first = Word(base, record + 0x10u);
    const uint32_t last = Word(base, record + 0x14u);
    const uint32_t bones = (Word(base, skeleton + 0x3Cu) - Word(base, skeleton + 0x38u)) / 8u;
    if (!Plausible(first) || last <= first || ((last - first) % kBoneMorphBytes) != 0u ||
        (last - first) / kBoneMorphBytes != bones || bones > kMaxBones) {
        return 0u;
    }

    const uint32_t max_entries = word_capacity / kBoneMorphWords;
    uint32_t written = 0u;
    for (uint32_t bone = 0u; bone < bones && written < max_entries; ++bone) {
        const uint32_t at = first + (bone * kBoneMorphBytes);
        bool identity = true;
        for (uint32_t lane = 0u; lane < 3u; ++lane) {
            identity = identity && Float(base, at + (lane * 4u)) == 0.0f &&
                       Float(base, at + 16u + (lane * 4u)) == 1.0f;
        }

        if (identity) {
            continue;
        }

        uint32_t* out = out_words + (written * kBoneMorphWords);
        out[0] = bone;
        for (uint32_t lane = 0u; lane < 3u; ++lane) {
            out[1u + lane] = GuestWordAt(base, at + (lane * 4u));
            out[4u + lane] = GuestWordAt(base, at + 16u + (lane * 4u));
        }

        ++written;
    }

    return written;
}

/// 0: Fable 3's morph record carries no height factor.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetBoneMorphHeight(uint32_t /*entity*/) {
    return 0u;
}

/// What the entity wears, component 18's vector (+0x10 begin, +0x14 end, 0x60-byte elements):
/// item = *(element+0), its GDB record *(item+4). Words per item: [record, flags], flags bit 0
/// female (component 13 +0xC == 1); bit 1, Fable 2's open state, is never set. Returns the count.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetEntityWornItems(uint32_t entity, uint32_t* out_words, uint32_t word_capacity) {
    if (out_words == nullptr || word_capacity < 2u) {
        return 0u;
    }

    const uint8_t* base = GuestBase();
    const uint32_t worn = base == nullptr || !Plausible(entity) ? 0u : Component(base, entity, kWornComponent);
    if (worn == 0u) {
        return 0u;
    }

    const uint32_t gender = Component(base, entity, kGenderComponent);
    const uint32_t flags = gender != 0u && GuestWordAt(base, gender + 0xCu) == 1u ? 1u : 0u;
    const uint32_t begin = Word(base, worn + 0x10u);
    const uint32_t end = Word(base, worn + 0x14u);
    if (!Plausible(begin) || end <= begin || ((end - begin) % kWornBytes) != 0u || (end - begin) / kWornBytes > 256u) {
        return 0u;
    }

    uint32_t written = 0u;
    for (uint32_t at = begin; at < end && written + 2u <= word_capacity; at += kWornBytes) {
        const uint32_t item = Word(base, at);
        const uint32_t record = Plausible(item) ? GuestWordAt(base, item + 4u) : 0u;
        if (record == 0u) {
            continue;
        }

        out_words[written++] = record;
        out_words[written++] = flags;
    }

    if (GuestWordAt(base, worn + 0x10u) != begin || GuestWordAt(base, worn + 0x14u) != end) {
        return 0u;  // a push reallocated mid-walk
    }

    return written / 2u;
}

/// Player one's and player two's heroes.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetCoopPlayerEntities(uint32_t* out_one, uint32_t* out_two) {
    const uint8_t* base = GuestBase();
    const uint32_t em = base == nullptr ? 0u : EntityManager(base);
    const uint32_t one = em == 0u ? 0u : Hero(base, em, 0x80u);
    const uint32_t two = em == 0u ? 0u : Hero(base, em, 0x88u);
    if (out_one != nullptr) *out_one = one;
    if (out_two != nullptr) *out_two = two;
    return (one != 0u ? 1u : 0u) + (two != 0u ? 1u : 0u);
}

/// The hero, which is what the shell asks this for; Fable 3's vitals are not stated yet, so the
/// words and values are left zero.
/// Player one's vitals in the shell's HeroVitals layout (HostBridge.GetHeroVitals), read from the
/// hero's components as Fable 3's own accessors read them. Component ids are the registrations'
/// (component_ids: CECStats 15, CECHealth 40, CECMoney 102):
///
///   words[0]   gold          CECMoney+0x0C   (vtable slot 0x38 sub_823AFCE0: `*(a1+12)`)
///   words[1]   renown        CECStats+0x44C  guild seals accumulated via renown (sub_823B90B8
///                                            "GetTotalNumberOfGuildSealsAccumulatedViaRenown" less
///                                            the standing renown pool)
///   values[0]  health        CECHealth+0x14  (SetHeroHealth sub_82335DD8 0x82335F44 deltas it;
///                                            sub_825FF9B0 clamps it at 0x825FFBB4..0x825FFC0C)
///   values[1]  base health   CECHealth+0x1C
///   values[2]  max health    sub_8227F7F8: +0x1C + +0x20, capped at +0x24 while +0x50 is set
///   values[3]  morality      CECStats+0x368  ("GetMorality" sub_82228128 `lfs 0x368`; the
///                                            accessor adds ability 11's modifier on top)
///   values[5]  morality min  CECStats+0x36C  ("GetMinMorality" sub_823B81C0)
///   values[6]  morality max  CECStats+0x370  ("GetMaxMorality" sub_823B81B8)
///   values[12] seals         CECStats+0x444 + +0x448   ("GetNumberOfGuildSeals" sub_823B90A0)
///   values[13] renown seals  CECStats+0x444  ("GetNumberOfGuildSealsRenown" sub_823B90B0)
///   values[14] pure seals    CECStats+0x448  ("ModifyGuildSeals" sub_823B90C8 adds here)
///   values[16] seals earned  CECStats+0x444 + +0x44C
///
/// Fable 3 has no purity, no strength/skill/will pools and no ability levels; those lanes stay 0.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetHeroVitals(uint32_t* words, uint32_t word_capacity, float* values,
                                                      uint32_t value_capacity) {
    if (words != nullptr) std::memset(words, 0, word_capacity * sizeof(uint32_t));
    if (values != nullptr) std::memset(values, 0, value_capacity * sizeof(float));
    const uint8_t* base = GuestBase();
    const uint32_t em = base == nullptr ? 0u : EntityManager(base);
    const uint32_t hero = em == 0u ? 0u : Hero(base, em, 0x80u);
    if (hero == 0u) {
        return 0u;
    }

    const auto word = [&](uint32_t index, uint32_t value) {
        if (words != nullptr && index < word_capacity) words[index] = value;
    };
    const auto real = [&](uint32_t index, float value) {
        if (values != nullptr && index < value_capacity) values[index] = value;
    };

    if (const uint32_t money = Component(base, hero, 102u)) {
        word(0, GuestWordAt(base, money + 0x0Cu));
    }

    if (const uint32_t health = Component(base, hero, 40u)) {
        const float base_health = Float(base, health + 0x1Cu);
        float max_health = base_health + Float(base, health + 0x20u);
        if (GuestByteAt(base, health + 0x50u) != 0u) {
            const float cap = Float(base, health + 0x24u);
            max_health = max_health < 0.0f ? 0.0f : (max_health > cap ? cap : max_health);
        }

        real(0, Float(base, health + 0x14u));
        real(1, base_health);
        real(2, max_health);
    }

    if (const uint32_t stats = Component(base, hero, 15u)) {
        const uint32_t renown_seals = GuestWordAt(base, stats + 0x444u);
        const uint32_t pure_seals = GuestWordAt(base, stats + 0x448u);
        const uint32_t via_renown = GuestWordAt(base, stats + 0x44Cu);
        word(1, via_renown);
        real(3, Float(base, stats + 0x368u));
        real(5, Float(base, stats + 0x36Cu));
        real(6, Float(base, stats + 0x370u));
        real(12, static_cast<float>(renown_seals + pure_seals));
        real(13, static_cast<float>(renown_seals));
        real(14, static_cast<float>(pure_seals));
        real(16, static_cast<float>(renown_seals + via_renown));
    }

    return hero;
}

/// A character's parts, as the guest builds them to draw (component 20, CECGraphicAppearanceMorph:
/// `GetECType` 0x82272018 `li r3,0x14`). The draw virtual sub_8222C190 rebuilds the list through
/// sub_82774448 and reads it at comp+0x38/+0x3C (`lwz 0x2C(r24)`/`0x30(r24)` at 0x8222C3E8, r24 =
/// comp+0xC), 128-byte records (`clrrwi r8,r9,7`), each led by the part's model hash. Until it is
/// built the source list at comp+0x28/+0x2C (one hash per u32) stands. The eyes are component 7's
/// own models (+0x18 right, +0x28 left, shown while +0x60 is set), appended as Fable 2 does.
/// `out_built` is the built record count, 0 when the source list answered. The guest sorts the
/// built list by layer (+0x34) DESCENDING, unlike Fable 2, so the order here carries no layering.
/// A built part draws at its record's alpha +0x38 times the entity's fade (`lfs f0,0x38(r24)`,
/// `fmuls` by +0x80 and +0x7C at 0x8222D24C; `lfs f0,0x38(r6)` at 0x8222C364): at or above 0.9999
/// (flt_82073D6C) it takes the opaque path, below it the translucent one at that alpha. A part at
/// alpha 0 is therefore invisible and is left out: the hero's fire gauntlet sits in his list at
/// layer 80, alpha 0, until the game gives it to him.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetEntityMorphParts(uint32_t entity, uint32_t* out_hashes,
                                                           uint32_t max_hashes, uint32_t* out_built) {
    if (out_built != nullptr) {
        *out_built = 0u;
    }
    if (out_hashes == nullptr || max_hashes == 0u) {
        return 0u;
    }

    const uint8_t* base = GuestBase();
    if (base == nullptr || !Plausible(entity)) {
        return 0u;
    }

    uint32_t written = 0u;
    const uint32_t morph = Component(base, entity, kMorphComponent);
    if (morph != 0u) {
        const uint32_t begin = Word(base, morph + 0x38u);
        const uint32_t end = Word(base, morph + 0x3Cu);
        const uint32_t capacity = Word(base, morph + 0x40u);
        const bool built = Plausible(begin) && end > begin && capacity >= end &&
                           ((end - begin) % kMorphPartBytes) == 0u &&
                           ((capacity - begin) % kMorphPartBytes) == 0u &&
                           (end - begin) <= kMorphPartBytes * 256u;
        if (built) {
            if (out_built != nullptr) {
                *out_built = (end - begin) / kMorphPartBytes;
            }
            for (uint32_t at = begin; at + kMorphPartBytes <= end && written < max_hashes; at += kMorphPartBytes) {
                const uint32_t hash = GuestWordAt(base, at);
                if (hash != 0u && Float(base, at + 0x38u) > 0.0f) {
                    out_hashes[written++] = hash;
                }
            }
        } else {
            const uint32_t first = Word(base, morph + 0x28u);
            const uint32_t last = Word(base, morph + 0x2Cu);
            if (Plausible(first) && last > first && ((last - first) % 4u) == 0u && (last - first) <= 4u * 256u) {
                for (uint32_t at = first; at < last && written < max_hashes; at += 4u) {
                    const uint32_t hash = GuestWordAt(base, at);
                    if (hash != 0u) {
                        out_hashes[written++] = hash;
                    }
                }
            }
        }
    }

    const uint32_t eyes = Component(base, entity, kEyesComponent);
    if (eyes != 0u && GuestByteAt(base, eyes + 0x60u) != 0u) {
        for (uint32_t offset : {0x18u, 0x28u}) {
            const uint32_t hash = Word(base, eyes + offset);
            if (hash != 0u && written < max_hashes) {
                out_hashes[written++] = hash;
            }
        }
    }

    return written;
}

/// The built parts the guest draws as displacement meshes: records whose +0x71 byte is set (the
/// draw ORs 0x840 into the part's flags for them, 0x8222C5D4); alpha 0 draws nothing, as in
/// RefiiHostGetEntityMorphParts.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetEntityDisplacementParts(uint32_t entity, uint32_t* out_hashes,
                                                                  uint32_t max_hashes) {
    if (out_hashes == nullptr || max_hashes == 0u) {
        return 0u;
    }

    const uint8_t* base = GuestBase();
    const uint32_t morph = base == nullptr || !Plausible(entity) ? 0u : Component(base, entity, kMorphComponent);
    if (morph == 0u) {
        return 0u;
    }

    const uint32_t begin = Word(base, morph + 0x38u);
    const uint32_t end = Word(base, morph + 0x3Cu);
    if (!Plausible(begin) || end <= begin || ((end - begin) % kMorphPartBytes) != 0u ||
        (end - begin) > kMorphPartBytes * 256u) {
        return 0u;
    }

    uint32_t written = 0u;
    for (uint32_t at = begin; at + kMorphPartBytes <= end && written < max_hashes; at += kMorphPartBytes) {
        const uint32_t hash = GuestWordAt(base, at);
        if (hash != 0u && GuestByteAt(base, at + 0x71u) != 0u && Float(base, at + 0x38u) > 0.0f) {
            out_hashes[written++] = hash;
        }
    }

    return written;
}

}  // extern "C"
