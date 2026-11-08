// refii - Fable 3's span-read entity walk (entity_walk.h) against the per-field reads it replaced,
// on synthetic guest memory: random entities, components, placements of every class, renderables,
// attachments and strings, with objects across page boundaries and pages that cannot be read.
// Every record word, every unknown-class report, every attachment and every name must match.
// Exits non-zero on the first difference.

#include <array>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <random>
#include <string>
#include <tuple>
#include <unordered_map>
#include <unordered_set>
#include <vector>

#include "entity_walk.h"

// The walk names TU3's addresses (fable3_guest_build.h); this synthetic guest is TU3, so every
// address is its own, as refiii_vfs answers before a disc payload says otherwise.
extern "C" {
int32_t refiii_guest_build(void) { return 0; }
void refiii_guest_build_set(int32_t) {}
uint32_t refiii_guest_address(uint32_t tu3_address) { return tu3_address; }
uint32_t refiii_guest_address_to_tu3(uint32_t live_address) { return live_address; }
}

namespace {

namespace walk = refii::fable3::walk;
using refii::hostcore::ForEachGuestPage;
using refii::hostcore::kGuestSpanPage;

/// Guest memory as the host bridge reads it: a page is readable or not, a span copy is split at
/// pages (CopyGuestPages) and a word or byte read is one guarded copy (GuestWordAt / GuestByteAt),
/// zero when any byte of it is unreadable.
struct SyntheticMemory {
    std::unordered_map<uint32_t, std::array<uint8_t, kGuestSpanPage>> pages;
    std::unordered_set<uint32_t> unreadable;

    bool Readable(uint32_t page) const { return pages.count(page) != 0 && unreadable.count(page) == 0; }

    uint8_t* At(uint32_t address) {
        auto& page = pages[address / kGuestSpanPage];
        return page.data() + (address % kGuestSpanPage);
    }

    void Put8(uint32_t address, uint8_t value) { *At(address) = value; }

    void Put32(uint32_t address, uint32_t value) {
        for (int i = 0; i < 4; ++i) Put8(address + i, static_cast<uint8_t>(value >> (24 - 8 * i)));
    }

    void PutF(uint32_t address, float value) {
        uint32_t bits;
        std::memcpy(&bits, &value, sizeof(bits));
        Put32(address, bits);
    }

    /// A guarded copy that does not split: all of it, or zeroes.
    void Guarded(uint32_t address, uint8_t* out, std::size_t size) const {
        const uint32_t first = address / kGuestSpanPage;
        const uint32_t last = static_cast<uint32_t>((address + size - 1u) / kGuestSpanPage);
        for (uint32_t page = first; page <= last; ++page) {
            if (!Readable(page)) {
                std::memset(out, 0, size);
                return;
            }
        }
        for (std::size_t done = 0; done < size;) {
            const uint32_t at = address + static_cast<uint32_t>(done);
            const std::size_t room = kGuestSpanPage - (at % kGuestSpanPage);
            const std::size_t chunk = size - done < room ? size - done : room;
            std::memcpy(out + done, pages.at(at / kGuestSpanPage).data() + (at % kGuestSpanPage), chunk);
            done += chunk;
        }
    }

    void Copy(uint32_t address, void* out, std::size_t size) const {
        ForEachGuestPage(address, out, size,
                         [this](uint32_t at, uint8_t* into, std::size_t chunk) { Guarded(at, into, chunk); });
    }

    uint32_t Word(uint32_t address) const {
        uint8_t b[4];
        Guarded(address, b, 4u);
        return (uint32_t{b[0]} << 24) | (uint32_t{b[1]} << 16) | (uint32_t{b[2]} << 8) | b[3];
    }

    uint8_t Byte(uint32_t address) const {
        uint8_t b;
        Guarded(address, &b, 1u);
        return b;
    }
};

// ---------------------------------------------------------------------------------------------
// THE REFERENCE: entity_feed.cpp as it read the guest before the spans, one read per field.

namespace reference {

using walk::kAppearanceDrawable;
using walk::kBodyTargets;
using walk::kDelegateTargets;
using walk::kEntityStride;
using walk::kIdentity;
using walk::kMaxListed;
using walk::kNoField;
using walk::kPlacements;
using walk::kSharedLightOrientation;
using walk::Placement;
using walk::PlacementKind;
using walk::PlacementTarget;
using walk::Plausible;

const SyntheticMemory* g;

uint32_t GuestWordAt(uint32_t address) { return g->Word(address); }
uint8_t GuestByteAt(uint32_t address) { return g->Byte(address); }
uint32_t Word(uint32_t address) { return Plausible(address) ? GuestWordAt(address) : 0u; }

float Float(uint32_t address) {
    const uint32_t bits = GuestWordAt(address);
    float value;
    std::memcpy(&value, &bits, sizeof(value));
    return value;
}

uint32_t Component(uint32_t entity, uint32_t id) {
    const uint32_t first = Word(entity + 0x54u);
    const uint32_t last = Word(entity + 0x58u);
    if (!Plausible(first) || last < first || last - first > 8u * 128u) {
        return 0u;
    }

    for (uint32_t at = first; at < last; at += 8u) {
        if (GuestWordAt(at) == id) {
            const uint32_t component = GuestWordAt(at + 4u);
            return Plausible(component) ? component : 0u;
        }
    }

    return 0u;
}

bool Live(uint32_t entity) { return (GuestByteAt(entity + 0x94u) & 0x40u) != 0u; }

bool ReadRep(uint32_t field, char* out, uint32_t capacity, bool whole) {
    std::memset(out, 0, capacity);
    const uint32_t rep = Word(field);
    const uint32_t chars = Word(rep);
    const uint32_t length = Plausible(rep) ? GuestWordAt(rep + 4u) : 0u;
    if (!Plausible(chars) || length == 0u || length > 1024u || (whole && length >= capacity)) {
        return false;
    }

    const uint32_t copied = length < capacity ? length : capacity - 1u;
    for (uint32_t i = 0; i < copied; ++i) {
        const uint8_t c = GuestByteAt(chars + i);
        if (c < 0x20u || c > 0x7Eu) {
            std::memset(out, 0, capacity);
            return false;
        }

        out[i] = static_cast<char>(c);
    }

    return true;
}

template <typename Visit>
uint32_t ForEachAttachment(uint32_t em, uint32_t max_records, Visit&& visit) {
    uint32_t written = 0u;
    uint32_t node = Word(em + 4u);
    for (uint32_t walked = 0; Plausible(node) && walked < kMaxListed && written < max_records; ++walked) {
        const uint32_t entity = GuestWordAt(node + 8u);
        node = GuestWordAt(node);
        if (!Plausible(entity) || !Live(entity)) {
            continue;
        }

        const uint32_t attachment = Component(entity, 26u);
        const uint32_t sentinel = attachment == 0u ? 0u : Word(attachment + 0xCu);
        if (!Plausible(sentinel)) {
            continue;
        }

        uint32_t link = GuestWordAt(sentinel);
        for (uint32_t depth = 0u; depth < 64u && written < max_records && link != sentinel && Plausible(link);
             ++depth) {
            const uint32_t record = GuestWordAt(link + 8u);
            link = GuestWordAt(link);
            const uint32_t child = Plausible(record) ? Word(Word(record + 4u)) : 0u;
            if (!Plausible(child) || !Live(child)) {
                continue;
            }

            visit(entity, record, child, written);
            ++written;
        }
    }

    return written;
}

void ReadLanes(uint32_t at, uint32_t* out, uint32_t lanes) {
    for (uint32_t lane = 0; lane < lanes; ++lane) {
        out[lane] = GuestWordAt(at + (lane * 4u));
    }
}

bool UnitQuaternionAt(uint32_t at, uint32_t* out) {
    float q[4];
    float length = 0.0f;
    for (uint32_t lane = 0; lane < 4u; ++lane) {
        q[lane] = Float(at + (lane * 4u));
        if (!std::isfinite(q[lane])) {
            return false;
        }
        length += q[lane] * q[lane];
    }

    if (!(length > 0.98f && length < 1.02f)) {
        return false;
    }

    ReadLanes(at, out, 4u);
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

bool QuaternionOfColumns(uint32_t at, uint32_t* out) {
    float m[12];
    for (uint32_t i = 0; i < 12u; ++i) {
        m[i] = Float(at + (i * 4u));
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

bool ReadTarget(uint32_t object, const PlacementTarget& target, uint32_t* out_position, uint32_t* out_orientation) {
    uint32_t from = object;
    if (target.inner != 0u) {
        from = Word(object + target.inner);
        if (from == 0u) {
            std::memset(out_position, 0, 3u * sizeof(uint32_t));
            std::memcpy(out_orientation, kIdentity, sizeof(kIdentity));
            return true;
        }
        if (!Plausible(from)) {
            return false;
        }
    }

    ReadLanes(from + target.position, out_position, 3u);
    return target.matrix ? QuaternionOfColumns(from + target.orientation, out_orientation)
                         : UnitQuaternionAt(from + target.orientation, out_orientation);
}

bool ReadBody(uint32_t body, uint32_t* out_position, uint32_t* out_orientation, uint32_t* out_vtable) {
    const uint32_t body_vtable = Plausible(body) ? GuestWordAt(body) : 0u;
    const PlacementTarget* target = reference::FindTarget(kBodyTargets, body_vtable);
    if (target == nullptr) {
        *out_vtable = body_vtable;
        return false;
    }

    return ReadTarget(body, *target, out_position, out_orientation);
}

bool LivePlacement(uint32_t entity, uint32_t* out_position, uint32_t* out_orientation, uint32_t* out_vtable) {
    *out_vtable = 0u;
    const uint32_t placement = Word(entity + 0x80u);
    if (!Plausible(placement)) {
        return false;
    }

    const uint32_t vtable = GuestWordAt(placement);
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
            ReadLanes(placement + layout->position, out_position, 3u);
            if (layout->orientation == kNoField) {
                if (!UnitQuaternionAt(kSharedLightOrientation, out_orientation)) {
                    std::memcpy(out_orientation, kIdentity, sizeof(kIdentity));
                }
            } else if (!UnitQuaternionAt(placement + layout->orientation, out_orientation)) {
                return false;
            }
            return true;

        case PlacementKind::Delegate: {
            const uint32_t object = Word(placement + 0x20u);
            if (object == 0u) {
                std::memset(out_position, 0, 3u * sizeof(uint32_t));
                std::memcpy(out_orientation, kIdentity, sizeof(kIdentity));
                return true;
            }

            const uint32_t object_vtable = Plausible(object) ? GuestWordAt(object) : 0u;
            const PlacementTarget* target = reference::FindTarget(kDelegateTargets, object_vtable);
            if (target == nullptr) {
                *out_vtable = object_vtable;
                return false;
            }

            if (!target->hop) {
                return ReadTarget(object, *target, out_position, out_orientation);
            }

            const uint32_t body = Word(object + 0x38u);
            if (body == 0u) {
                std::memset(out_position, 0, 3u * sizeof(uint32_t));
                std::memcpy(out_orientation, kIdentity, sizeof(kIdentity));
                return true;
            }

            return ReadBody(body, out_position, out_orientation, out_vtable);
        }

        case PlacementKind::Hybrid: {
            const uint32_t object = Word(placement + 0x20u);
            const uint32_t body = Plausible(object) ? Word(object + 0x38u) : 0u;
            if (body == 0u) {
                ReadLanes(placement + 0x80u, out_position, 3u);
                std::memcpy(out_orientation, kIdentity, sizeof(kIdentity));
                return true;
            }

            if (!ReadBody(body, out_position, out_orientation, out_vtable)) {
                return false;
            }

            if (GuestByteAt(placement + 0xB4u) != 0u) {
                ReadLanes(placement + 0x80u, out_position, 3u);
            }
            return true;
        }
    }

    return false;
}

uint32_t HighlightWord(uint32_t renderable) {
    uint32_t bytes[3];
    bool lit = false;
    for (uint32_t lane = 0; lane < 3u; ++lane) {
        const float value = Float(renderable + 0xD0u + (lane * 4u));
        const float clamped = !(value > 0.0f) ? 0.0f : (value > 1.0f ? 1.0f : value);
        bytes[lane] = static_cast<uint32_t>(clamped * 255.0f + 0.5f);
        lit = lit || bytes[lane] != 0u;
    }

    return lit ? (bytes[2] << 24) | (bytes[1] << 16) | (bytes[0] << 8) | 0xFFu : 0x000000FFu;
}

template <typename NoteUnknown>
uint32_t WalkEntities(uint32_t em, uint32_t* out, uint32_t max_entities, NoteUnknown&& note_unknown) {
    uint32_t written = 0u;
    uint32_t node = Word(em + 4u);
    for (uint32_t walked = 0; Plausible(node) && walked < kMaxListed && written < max_entities; ++walked) {
        const uint32_t entity = GuestWordAt(node + 8u);
        node = GuestWordAt(node);
        if (!Plausible(entity) || !Live(entity)) {
            continue;
        }

        const uint32_t renderable = Component(entity, 3u);
        if (renderable == 0u) {
            continue;
        }

        uint32_t model = 0u;
        for (uint32_t id : {6u, 4u}) {
            const uint32_t mesh = Component(entity, id);
            if (mesh != 0u && (model = GuestWordAt(mesh + 0x18u)) != 0u) {
                break;
            }
        }

        uint32_t* row = out + (written * kEntityStride);
        ReadLanes(renderable + 0xA0u, row + 12, 3u);
        ReadLanes(renderable + 0xB0u, row + 15, 4u);

        uint32_t vtable = 0u;
        const bool live = LivePlacement(entity, row, row + 3, &vtable);
        if (!live) {
            std::memcpy(row, row + 12, 7u * sizeof(uint32_t));
            if (vtable != 0u) {
                note_unknown(vtable);
            }
        }

        row[7] = model;
        row[8] = entity;
        row[9] = GuestWordAt(entity + 0x18u);
        row[10] = GuestWordAt(entity + 0x1Cu);
        row[11] = GuestWordAt(entity + 0x20u);
        row[19] = live && GuestByteAt(renderable + 0xE9u) != 0u ? 1u : 0u;
        row[20] = HighlightWord(renderable);

        uint32_t* appearance = row + 21;
        appearance[0] = GuestWordAt(renderable + 0x7Cu);
        appearance[1] = GuestWordAt(renderable + 0x80u);
        appearance[2] = GuestWordAt(renderable + 0x78u);
        ReadLanes(renderable + 0x50u, appearance + 3, 4u);
        appearance[7] = GuestByteAt(renderable + 0xE5u) != 0u && GuestWordAt(renderable + 0x120u) == 0u
                            ? kAppearanceDrawable
                            : 0u;
        ++written;
    }

    return written;
}

}  // namespace reference

// ---------------------------------------------------------------------------------------------
// Synthetic worlds.

constexpr uint32_t kHeap = 0x40000000u;
constexpr uint32_t kHeapPages = 192u;

struct World {
    SyntheticMemory memory;
    std::mt19937 rng;
    uint32_t next = kHeap;
    uint32_t em = 0u;

    explicit World(uint32_t seed) : rng(seed) {}

    uint32_t Below(uint32_t n) { return static_cast<uint32_t>(rng() % n); }
    bool Chance(uint32_t percent) { return Below(100u) < percent; }

    /// A 16-aligned object of `size` bytes filled with noise, placed anywhere in the heap so that
    /// some objects straddle pages.
    uint32_t Alloc(uint32_t size) {
        next += 16u * (1u + Below(24u));
        const uint32_t at = next;
        next += (size + 15u) & ~15u;
        for (uint32_t i = 0; i < size; ++i) memory.Put8(at + i, static_cast<uint8_t>(rng()));
        return at;
    }

    float Unit() { return std::uniform_real_distribution<float>(-1.0f, 1.0f)(rng); }

    /// Lanes as the guest might hold them: finite, unit, huge, NaN or infinite.
    float Lane() {
        switch (Below(10u)) {
            case 0: return std::nanf("");
            case 1: return Chance(50) ? INFINITY : -INFINITY;
            case 2: return Unit() * 1e6f;
            default: return Unit();
        }
    }

    void PutQuaternion(uint32_t at) {
        if (Chance(80)) {
            float q[4] = {Unit(), Unit(), Unit(), Unit()};
            const float n = std::sqrt(q[0] * q[0] + q[1] * q[1] + q[2] * q[2] + q[3] * q[3]);
            for (int i = 0; i < 4; ++i) memory.PutF(at + 4u * i, n > 0.0f ? q[i] / n : 0.0f);
        } else {
            for (int i = 0; i < 4; ++i) memory.PutF(at + 4u * i, Lane());
        }
    }

    /// Three 16-byte columns of a rotation (or noise), for the Havok transform target.
    void PutColumns(uint32_t at) {
        if (Chance(25)) {
            for (uint32_t i = 0; i < 12u; ++i) memory.PutF(at + 4u * i, Lane());
            return;
        }
        float x = Unit(), y = Unit(), z = Unit(), w = Unit();
        const float n = std::sqrt(x * x + y * y + z * z + w * w);
        x /= n; y /= n; z /= n; w /= n;
        const float m[3][3] = {
            {1 - 2 * (y * y + z * z), 2 * (x * y + z * w), 2 * (x * z - y * w)},
            {2 * (x * y - z * w), 1 - 2 * (x * x + z * z), 2 * (y * z + x * w)},
            {2 * (x * z + y * w), 2 * (y * z - x * w), 1 - 2 * (x * x + y * y)},
        };
        for (uint32_t c = 0; c < 3u; ++c)
            for (uint32_t r = 0; r < 4u; ++r) memory.PutF(at + 16u * c + 4u * r, r < 3u ? m[c][r] : Unit());
    }

    void PutVector(uint32_t at) {
        for (uint32_t i = 0; i < 4u; ++i) memory.PutF(at + 4u * i, Lane());
    }

    uint32_t Pointer(uint32_t valid) {
        switch (Below(20u)) {
            case 0: return 0u;
            case 1: return 0x8000u;          // implausible, low
            case 2: return 0xC0001000u;      // implausible, high
            default: return valid;
        }
    }

    uint32_t Body() {
        const uint32_t vtables[] = {0x82005BD0u, 0x82005FF0u, 0x82005D20u, 0x82000000u + Below(0x100000u)};
        const uint32_t vtable = vtables[Below(4u)];
        const uint32_t body = Alloc(0x180u);
        memory.Put32(body, vtable);
        PutVector(body + 0x100u);
        PutQuaternion(body + 0x110u);
        if (vtable == 0x82005FF0u || vtable == 0x82005D20u) {
            const uint32_t inner = Alloc(0x180u);
            PutVector(inner + 0x120u);
            PutQuaternion(inner + 0x160u);
            PutVector(inner + 0xD0u);
            PutColumns(inner + 0xA0u);
            memory.Put32(body + 0x38u, Chance(15) ? 0u : Pointer(inner));
            memory.Put32(body + 0x50u, Chance(15) ? 0u : Pointer(inner));
        }
        return body;
    }

    uint32_t PlacementObject() {
        const uint32_t vtables[] = {0x82007C50u, 0x820079C8u, 0x82007AA0u, 0x8209D448u, 0x82007D28u, 0x820B8030u,
                                    0x820078F0u, 0x82007ED8u, 0x82007B78u, 0x82007E00u, 0x820B0C68u, 0x8200ABCDu};
        const uint32_t kind = Below(12u);
        const uint32_t vtable = vtables[kind];
        const uint32_t placement = Alloc(0xC0u);
        memory.Put32(placement, vtable);
        PutVector(placement + 0x20u);
        PutQuaternion(placement + 0x30u);
        PutVector(placement + 0x40u);
        PutVector(placement + 0x80u);
        memory.Put8(placement + 0xB4u, Chance(30) ? 1u : 0u);

        // The delegate / hybrid object at +0x20.
        const uint32_t object_vtables[] = {0x82006F18u, 0x82007018u, 0x82007190u, 0x820011E4u};
        const uint32_t object = Alloc(0x80u);
        memory.Put32(object, object_vtables[Below(4u)]);
        PutVector(object + 0x40u);
        PutQuaternion(object + 0x50u);
        PutQuaternion(object + 0x60u);
        memory.Put32(object + 0x38u, Chance(15) ? 0u : Pointer(Body()));
        if (kind >= 4u && kind <= 10u) {
            memory.Put32(placement + 0x20u, Chance(10) ? 0u : Pointer(object));
        }
        return placement;
    }

    uint32_t Renderable() {
        const uint32_t renderable = Alloc(0x150u);
        for (uint32_t at = 0x50u; at < 0x124u; at += 4u) memory.PutF(renderable + at, Lane());
        for (uint32_t at = 0xD0u; at < 0xDCu; at += 4u) memory.PutF(renderable + at, Unit() * 1.5f);
        memory.Put8(renderable + 0xE5u, Chance(70) ? 1u : 0u);
        memory.Put8(renderable + 0xE9u, Chance(70) ? 1u : 0u);
        memory.Put32(renderable + 0x120u, Chance(80) ? 0u : rng());
        return renderable;
    }

    uint32_t Rep() {
        const uint32_t rep = Alloc(8u);
        const uint32_t length = Chance(10) ? 0u : (Chance(5) ? 2000u : 1u + Below(90u));
        const uint32_t chars = Alloc(length + 1u);
        for (uint32_t i = 0; i < length; ++i) {
            const uint8_t unprintable = static_cast<uint8_t>(Chance(50) ? Below(32u) : 0x7Fu + Below(129u));
            memory.Put8(chars + i, Chance(2) ? unprintable : static_cast<uint8_t>(0x20u + Below(95u)));
        }
        memory.Put32(rep, Pointer(chars));
        memory.Put32(rep + 4u, length);
        return rep;
    }

    uint32_t Entity(bool live);

    uint32_t Attachment() {
        const uint32_t component = Alloc(0x20u);
        const uint32_t sentinel = Alloc(12u);
        memory.Put32(component + 0xCu, Pointer(sentinel));
        uint32_t previous = sentinel;
        const uint32_t links = Below(5u);
        for (uint32_t i = 0; i < links; ++i) {
            const uint32_t link = Alloc(12u);
            const uint32_t record = Alloc(0xA0u);
            const uint32_t handle = Alloc(4u);
            memory.Put32(handle, Pointer(Entity(Chance(85))));
            memory.Put32(record + 4u, Pointer(handle));
            memory.Put32(record + 8u, Pointer(Rep()));
            memory.Put32(link + 8u, Pointer(record));
            memory.Put32(previous, link);
            previous = link;
        }
        memory.Put32(previous, Chance(90) ? sentinel : Pointer(Alloc(12u)));
        return component;
    }

    /// The {id, component} pairs; `*out_end` the end the entity states, mostly the true one.
    uint32_t ComponentVector(bool deep, uint32_t* out_end) {
        const uint32_t count = Below(24u);
        const uint32_t vector = Alloc(8u * count + 8u);
        for (uint32_t i = 0; i < count; ++i) {
            const uint32_t ids[] = {3u, 4u, 6u, 26u, 3u, 1u + Below(100u)};
            const uint32_t id = ids[Below(6u)];
            uint32_t component;
            if (id == 3u) {
                component = Renderable();
            } else if (id == 4u || id == 6u) {
                component = Alloc(0x20u);
                memory.Put32(component + 0x18u, Chance(30) ? 0u : rng());
            } else if (id == 26u && deep) {
                component = Attachment();
            } else {
                component = Alloc(0x10u);
            }
            memory.Put32(vector + 8u * i, id);
            memory.Put32(vector + 8u * i + 4u, Pointer(component));
        }
        switch (Below(20u)) {
            case 0:                                                 // not a whole pair: its id still counts
                memory.Put32(vector + 8u * count, Chance(50) ? 3u : 6u);
                memory.Put32(vector + 8u * count + 4u, Pointer(Chance(50) ? Renderable() : Alloc(0x20u)));
                *out_end = vector + 8u * count + 1u + Below(7u);
                break;
            case 1: *out_end = vector + 2000u; break;               // past the 128-pair bound
            case 2: *out_end = vector - 8u; break;                  // end before begin
            case 3: *out_end = vector + 8u * Below(24u); break;     // a stale end
            default: *out_end = vector + 8u * count; break;
        }
        return vector;
    }
};

uint32_t World::Entity(bool live) {
    const uint32_t entity = Alloc(0x98u);
    memory.Put8(entity + 0x94u, static_cast<uint8_t>((rng() & ~0x40u) | (live ? 0x40u : 0u)));
    uint32_t end = 0u;
    const uint32_t vector = ComponentVector(false, &end);
    memory.Put32(entity + 0x54u, Pointer(vector));
    memory.Put32(entity + 0x58u, end);
    memory.Put32(entity + 0x80u, Pointer(PlacementObject()));
    return entity;
}

/// A world: the entity manager, its list, and (shared) the light orientation global.
void Build(World& world, uint32_t entities) {
    SyntheticMemory& m = world.memory;
    world.em = world.Alloc(0x100u);
    uint32_t previous_slot = world.em + 4u;
    for (uint32_t i = 0; i < entities; ++i) {
        const uint32_t entity = world.Alloc(0x98u);
        m.Put8(entity + 0x94u, static_cast<uint8_t>((world.rng() & ~0x40u) | (world.Chance(85) ? 0x40u : 0u)));
        uint32_t end = 0u;
        const uint32_t vector = world.ComponentVector(true, &end);
        m.Put32(entity + 0x54u, world.Chance(5) ? world.Pointer(0u) : vector);
        m.Put32(entity + 0x58u, end);
        m.Put32(entity + 0x80u, world.Pointer(world.PlacementObject()));

        // The list node is the entity's own +0xC: {next, prev, entity}.
        const uint32_t node = entity + 0xCu;
        m.Put32(node + 8u, world.Chance(3) ? world.Pointer(0u) : entity);
        m.Put32(previous_slot, node);
        previous_slot = node;
    }
    m.Put32(previous_slot, 0u);

    if (world.Chance(50)) {
        world.PutQuaternion(walk::kSharedLightOrientation);
    }
}

/// Marks `percent` of the touched heap pages unreadable (decommitted).
void Decommit(World& world, uint32_t percent) {
    for (const auto& [page, bytes] : world.memory.pages) {
        if (world.Chance(percent)) world.memory.unreadable.insert(page);
    }
}

int g_failures = 0;

void Fail(uint32_t seed, const std::string& what) {
    if (++g_failures <= 20) std::fprintf(stderr, "entity_walk_test: seed %u: %s\n", seed, what.c_str());
}

void CheckWalk(uint32_t seed, World& world) {
    const SyntheticMemory& m = world.memory;
    reference::g = &m;
    constexpr uint32_t kMax = 512u;
    std::vector<uint32_t> expected(kMax * walk::kEntityStride, 0xA5A5A5A5u);
    std::vector<uint32_t> actual(kMax * walk::kEntityStride, 0xA5A5A5A5u);
    std::vector<uint32_t> expected_unknown, actual_unknown;
    for (uint32_t max : {kMax, 3u}) {
        expected_unknown.clear();
        actual_unknown.clear();
        const uint32_t want = reference::WalkEntities(world.em, expected.data(), max,
                                                      [&](uint32_t v) { expected_unknown.push_back(v); });
        const uint32_t got =
            walk::WalkEntities(m, world.em, actual.data(), max, [&](uint32_t v) { actual_unknown.push_back(v); });
        if (want != got) {
            Fail(seed, "entity count " + std::to_string(want) + " vs " + std::to_string(got));
            return;
        }
        for (uint32_t i = 0; i < want * walk::kEntityStride; ++i) {
            if (expected[i] != actual[i]) {
                char line[128];
                std::snprintf(line, sizeof(line), "row %u word %u: %08X vs %08X", i / walk::kEntityStride,
                              i % walk::kEntityStride, expected[i], actual[i]);
                Fail(seed, line);
                return;
            }
        }
        if (expected_unknown != actual_unknown) {
            Fail(seed, "unknown placement classes differ");
        }
    }
}

void CheckAttachments(uint32_t seed, World& world) {
    const SyntheticMemory& m = world.memory;
    reference::g = &m;
    using Visit = std::tuple<uint32_t, uint32_t, uint32_t, uint32_t, std::string, bool, std::vector<uint32_t>>;
    std::vector<Visit> expected, actual;
    const uint32_t want = reference::ForEachAttachment(world.em, 256u, [&](uint32_t p, uint32_t r, uint32_t c, uint32_t at) {
        char name[64];
        const bool ok = reference::ReadRep(r + 8u, name, sizeof(name), false);
        char whole[24];
        const bool fits = reference::ReadRep(r + 8u, whole, sizeof(whole), true);
        std::vector<uint32_t> grip(16);
        for (uint32_t i = 0; i < 16u; ++i) grip[i] = reference::GuestWordAt(r + 0x50u + 4u * i);
        expected.emplace_back(p, r, c, at, std::string(name, sizeof(name)) + std::string(whole, sizeof(whole)),
                              ok && fits, grip);
    });
    const uint32_t got = walk::ForEachAttachment(m, world.em, 256u, [&](uint32_t p, uint32_t r, uint32_t c, uint32_t at) {
        char name[64];
        const bool ok = walk::ReadRep(m, r + 8u, name, sizeof(name), false);
        char whole[24];
        const bool fits = walk::ReadRep(m, r + 8u, whole, sizeof(whole), true);
        std::vector<uint32_t> grip(16);
        refii::hostcore::ReadGuestWords(m, r + 0x50u, grip.data(), 16u);
        actual.emplace_back(p, r, c, at, std::string(name, sizeof(name)) + std::string(whole, sizeof(whole)),
                            ok && fits, grip);
    });
    if (want != got || expected != actual) {
        Fail(seed, "attachments differ (" + std::to_string(want) + " vs " + std::to_string(got) + ")");
    }
}

/// The pose and rest-pose copies: a block of words against the words one at a time.
void CheckWords(uint32_t seed, World& world) {
    const SyntheticMemory& m = world.memory;
    for (int trial = 0; trial < 64; ++trial) {
        const uint32_t count = 1u + world.Below(512u * 12u / 8u);
        const uint32_t at = kHeap + 4u * world.Below(kHeapPages * kGuestSpanPage / 4u);
        std::vector<uint32_t> block(count);
        refii::hostcore::ReadGuestWords(m, at, block.data(), count);
        for (uint32_t i = 0; i < count; ++i) {
            if (block[i] != m.Word(at + 4u * i)) {
                Fail(seed, "word block differs at word " + std::to_string(i));
                return;
            }
        }
    }
}

}  // namespace

int main() {
    uint32_t walked = 0u;
    for (uint32_t seed = 1u; seed <= 400u; ++seed) {
        World world(seed);
        Build(world, 1u + seed % 160u);
        if (seed % 2u == 0u) {
            Decommit(world, 1u + seed % 7u);
        }
        CheckWalk(seed, world);
        CheckAttachments(seed, world);
        CheckWords(seed, world);
        walked += 1u + seed % 160u;
    }

    if (g_failures != 0) {
        std::fprintf(stderr, "entity_walk_test: %d difference(s)\n", g_failures);
        return 1;
    }

    std::printf("entity_walk_test: 400 worlds, %u entities: span reads equal the per-field reads\n", walked);
    return 0;
}
