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
//   component 3 (the renderable): +0xA0 position, +0xB0 orientation quaternion, latched from the
//             placement's slots 0x50/0x54 every tick (sub_8221EFB8: `stvx128` at r31+160 and +176);
//             +0xE5 drawable
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

}  // namespace

extern "C" {

/// Every live entity with a renderable, in the shell's 29-word record. The transform is the
/// renderable's per-tick latch — the guest's own resolved placement — so the previous transform
/// is stated as the same and interpolation off until the live placement is read.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetEntities(uint32_t* out, uint32_t max_entities, uint32_t* out_unknown,
                                                    uint32_t* out_unknown_vtables, uint32_t /*max_unknown_vtables*/,
                                                    uint32_t* out_unknown_vtable_count) {
    if (out_unknown != nullptr) *out_unknown = 0u;
    if (out_unknown_vtable_count != nullptr) *out_unknown_vtable_count = 0u;
    (void)out_unknown_vtables;
    if (out == nullptr || max_entities == 0u) {
        return 0u;
    }

    const uint8_t* base = GuestBase();
    const uint32_t em = base == nullptr ? 0u : EntityManager(base);
    if (em == 0u) {
        return 0u;
    }

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
        for (uint32_t lane = 0; lane < 3u; ++lane) {
            row[lane] = GuestWordAt(base, renderable + 0xA0u + (lane * 4u));
            row[12u + lane] = row[lane];
        }

        for (uint32_t lane = 0; lane < 4u; ++lane) {
            row[3u + lane] = GuestWordAt(base, renderable + 0xB0u + (lane * 4u));
            row[15u + lane] = row[3u + lane];
        }

        row[7] = model;
        row[8] = entity;
        row[9] = GuestWordAt(base, entity + 0x18u);
        row[10] = GuestWordAt(base, entity + 0x1Cu);
        row[11] = GuestWordAt(base, entity + 0x20u);
        row[19] = 0u;
        row[20] = 0x000000FFu;  // the guest's off highlight, {0,0,0,255}

        uint32_t* appearance = row + 21;
        appearance[0] = appearance[1] = appearance[2] = kOne;
        appearance[3] = appearance[4] = appearance[5] = appearance[6] = kOne;
        appearance[7] = GuestByteAt(base, renderable + 0xE5u) != 0u ? kAppearanceDrawable : 0u;
        ++written;
    }

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

/// The lighting zone of the entity's geometry: the renderable's (component 3) +0xF4, which
/// sub_82264F98 keeps by LightZoneMode at +0xF0. A CECBuilding (component 79) in mode 0 is in the
/// zone of its own record id, `entity+0x20 & 0x7FFFFFFF`, while live, else 1. 1 without a renderable.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetEntityLightZone(uint32_t entity) {
    const uint8_t* base = GuestBase();
    const uint32_t renderable = base == nullptr || !Plausible(entity) ? 0u : Component(base, entity, 3u);
    if (renderable == 0u) {
        return 1u;
    }

    if (Component(base, entity, kBuildingComponent) != 0u && GuestWordAt(base, renderable + 0xF0u) == 0u) {
        return Live(base, entity) ? GuestWordAt(base, entity + 0x20u) & 0x7FFFFFFFu : 1u;
    }

    return GuestWordAt(base, renderable + 0xF4u);
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
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetHeroVitals(uint32_t* words, uint32_t word_capacity, float* values,
                                                      uint32_t value_capacity) {
    if (words != nullptr) std::memset(words, 0, word_capacity * sizeof(uint32_t));
    if (values != nullptr) std::memset(values, 0, value_capacity * sizeof(float));
    const uint8_t* base = GuestBase();
    const uint32_t em = base == nullptr ? 0u : EntityManager(base);
    return em == 0u ? 0u : Hero(base, em, 0x80u);
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
