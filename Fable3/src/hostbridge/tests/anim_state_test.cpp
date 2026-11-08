// refii - Fable 3's span-read animation-state walk (anim_walk.h) against the per-field reads it
// replaced, on synthetic guest memory: rigs with 0..64 records on both lists, render trees of every
// node kind (nested, shared and cyclic), set children, bone masks, replicated trees, linked
// animations with their clip maps, physics proxies and ragdolls, implausible and misaligned
// pointers, objects across page boundaries and up against the 0xC0000000 ceiling, and pages that
// cannot be read. Every out word, the out flags, the walk-stat causes and every bone-mask byte
// must match. Exits non-zero on the first difference.

#include <algorithm>
#include <array>
#include <atomic>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <limits>
#include <random>
#include <unordered_map>
#include <unordered_set>
#include <vector>

#include "anim_walk.h"

namespace {

namespace anim = refii::fable3::anim;
using refii::hostcore::ForEachGuestPage;
using refii::hostcore::kGuestSpanPage;

/// Guest memory as the host bridge reads it: a page is readable or not, a span copy is split at
/// pages (CopyGuestPages) and a word or byte read is one guarded copy (GuestWordAt / GuestByteAt),
/// zero when any byte of it is unreadable. `guarded` counts the guarded copies.
struct SyntheticMemory {
    std::unordered_map<uint32_t, std::array<uint8_t, kGuestSpanPage>> pages;
    std::unordered_set<uint32_t> unreadable;
    uint64_t ticks = 0;
    mutable uint64_t guarded = 0;
    mutable uint64_t outside = 0;  // guarded copies touching no guest address (Plausible)

    bool Readable(uint32_t page) const { return pages.count(page) != 0 && unreadable.count(page) == 0; }

    uint8_t* At(uint32_t address) {
        auto& page = pages[address / kGuestSpanPage];
        return page.data() + (address % kGuestSpanPage);
    }

    void Put8(uint32_t address, uint8_t value) { *At(address) = value; }

    void Put32(uint32_t address, uint32_t value) {
        for (uint32_t i = 0; i < 4u; ++i) Put8(address + i, static_cast<uint8_t>(value >> (24u - 8u * i)));
    }

    void PutF(uint32_t address, float value) {
        uint32_t bits;
        std::memcpy(&bits, &value, sizeof(bits));
        Put32(address, bits);
    }

    void Guarded(uint32_t address, uint8_t* out, std::size_t size) const {
        ++guarded;
        if (address < anim::kFirstGuest || uint64_t{address} + size > anim::kGuestCeiling) {
            ++outside;
        }
        const uint64_t first = address / kGuestSpanPage;
        const uint64_t last = (uint64_t{address} + size - 1u) / kGuestSpanPage;
        for (uint64_t page = first; page <= last; ++page) {
            if (page > 0xFFFFFu || !Readable(static_cast<uint32_t>(page))) {
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

    // The walk's reader (guest_span.h's contract, plus the clock).
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

    uint64_t GuestTicks() const { return ticks; }
};

// ---------------------------------------------------------------------------------------------
// THE REFERENCE: anim_feed.cpp as it read the guest before the spans, one read per field. Copied
// verbatim; only the guest's primitives below are the synthetic memory's.

namespace reference {

const SyntheticMemory* g = nullptr;
const uint8_t kBaseByte = 0u;

uint32_t GuestWordAt(const uint8_t* /*base*/, uint32_t address) { return g->Word(address); }
uint8_t GuestByteAt(const uint8_t* /*base*/, uint32_t address) { return g->Byte(address); }
uint64_t refii_bridge_rex_query_guest_tick_count() { return g->ticks; }

namespace rex::memory {
struct Memory {
    const uint8_t* virtual_membase() const { return &kBaseByte; }
};
}  // namespace rex::memory

rex::memory::Memory* SafeKernelMemory() {
    static rex::memory::Memory memory;
    return &memory;
}

#define REFII_HOST_ABI_EXPORT

constexpr uint32_t kAnimationManager = 23u;
constexpr uint32_t kLinkedAnimation = 215u;
constexpr uint32_t kLeafWords = 12u;
constexpr uint32_t kMaxRecords = 64u;
constexpr uint32_t kMaxSetChildren = 64u;
constexpr uint32_t kFullTier = 4u;

/// The evaluator's cull, `a30[0]` (flt_8206C78C), and every 0.05 test of the fold.
constexpr float kCull = 0.05f;

/// byte_8329E3D7: the fold crossfades the top against a partner at all (sub_821EB068).
constexpr uint32_t kCrossfadeOn = 0x8329E3D7u;
/// byte_83556685: the partner walk starts from 1 instead of 1 − w (sub_826BC358 0x826bc3fc).
constexpr uint32_t kPartnerFromOne = 0x83556685u;
/// qword_83556BD8 / dbl_83556BD0: the guest clock `(mftb − base) / frequency` (sub_8220A380).
constexpr uint32_t kTimeBase = 0x83556BD8u;
constexpr uint32_t kTimeFrequency = 0x83556BD0u;

constexpr uint32_t kRampInTimed = 0x82001B2Cu;
constexpr uint32_t kRampOutTimed = 0x82001858u;

/// The ragdoll's class (ctor sub_82B06638 stores it at 0x82b06668); its slot 2 answers 1, the
/// test sub_8270CCF8 makes.
constexpr uint32_t kRagdollVtable = 0x82006DE0u;
/// `lwz r3, 0x20(r3); blr` — slot 0x70 of every non-simple placement class (0x82007DF8,
/// 0x820079C0, 0x82007FA8, 0x82007C48, 0x82007ED0, ...); the simple ones answer 0 (sub_82CE17A8).
constexpr uint32_t kPhysicsProxyGetter = 0x82E78F88u;

bool Plausible(uint32_t address) { return address >= 0x10000u && address < 0xC0000000u; }

uint32_t Word(const uint8_t* base, uint32_t address) {
    return Plausible(address) ? GuestWordAt(base, address) : 0u;
}

uint8_t Byte(const uint8_t* base, uint32_t address) {
    return Plausible(address) ? GuestByteAt(base, address) : 0u;
}

float Float(const uint8_t* base, uint32_t address) {
    const uint32_t bits = Word(base, address);
    float value;
    std::memcpy(&value, &bits, sizeof(value));
    return value;
}

uint64_t Quad(const uint8_t* base, uint32_t address) {
    return (static_cast<uint64_t>(Word(base, address)) << 32) | Word(base, address + 4u);
}

uint32_t Bits(float value) {
    uint32_t bits;
    std::memcpy(&bits, &value, sizeof(bits));
    return bits;
}

float Finite(float value) { return std::isfinite(value) ? value : 0.0f; }

/// The entity's component `id`, or 0 (the {u32 id, component} vector at +0x54/+0x58).
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

/// The guest clock in seconds, as sub_8220A380 reads it: `(mftb − qword_83556BD8) /
/// dbl_83556BD0`. `mftb` in the recompiled module is the payload's tick count.
bool GuestSeconds(const uint8_t* base, double* out) {
    const uint64_t time_base = Quad(base, kTimeBase);
    const uint64_t frequency_bits = Quad(base, kTimeFrequency);
    double frequency;
    std::memcpy(&frequency, &frequency_bits, sizeof(frequency));
    if (!std::isfinite(frequency) || frequency <= 0.0) {
        return false;
    }
    const int64_t ticks = static_cast<int64_t>(refii_bridge_rex_query_guest_tick_count() - time_base);
    *out = static_cast<double>(ticks) / frequency;
    return std::isfinite(*out);
}

/// A ramp's length in seconds: `1/(step·15)` for a timed ramp (step at +4), 0 for an instant one.
uint32_t RampSecondsBits(const uint8_t* base, uint32_t ramp, uint32_t timed_vtable) {
    if (!Plausible(ramp) || Word(base, ramp) != timed_vtable) {
        return 0u;
    }
    const float step = Float(base, ramp + 4u);
    if (!(step > 1e-6f) || !(step < 1.0f)) {
        return 0u;
    }
    return Bits(1.0f / (step * 15.0f));
}

/// Whether the guest's physics owns this entity's bones: sub_823531E0 (0x82353a18..0x82353a78)
/// takes `entity+0x80`'s slot 0x70 (the physics proxy, `*(placement+0x20)`), finds the object
/// sub_8270CCF8 picks in its {object, ref} vector at +4/+8, and hands the pose to physics when
/// its byte +0x44 is set and its mode +0x40 is 3 or 4. The host cannot call the virtuals; it
/// reads the slot's implementation and the ragdoll's class, and fails closed on anything else.
bool PhysicsOwnsPose(const uint8_t* base, uint32_t entity) {
    const uint32_t placement = Word(base, entity + 0x80u);
    const uint32_t vtable = Word(base, placement);
    if (!Plausible(vtable) || Word(base, vtable + 0x70u) != kPhysicsProxyGetter) {
        return false;
    }
    const uint32_t proxy = Word(base, placement + 0x20u);
    const uint32_t begin = Word(base, proxy + 4u);
    const uint32_t end = Word(base, proxy + 8u);
    if (!Plausible(begin) || end < begin || end - begin > 8u * 64u) {
        return false;
    }
    for (uint32_t at = begin; at + 8u <= end; at += 8u) {
        const uint32_t object = Word(base, at);
        if (Word(base, object) != kRagdollVtable) {
            continue;
        }
        const uint32_t mode = Word(base, object + 0x40u);
        return Byte(base, object + 0x44u) != 0u && (mode == 3u || mode == 4u);
    }
    return false;
}

/// Why a rig had no leaves, by cause, for RefiiHostGetAnimWalkStats.
std::atomic<uint64_t> gNoManager{0};
std::atomic<uint64_t> gManagerFound{0};
std::atomic<uint64_t> gEmptyList{0};
std::atomic<uint64_t> gLinked{0};

/// One record's stated place in the fold, carried onto each of its leaves.
struct Stated {
    uint32_t priority = 0;
    uint32_t track = 0;
    uint32_t priority_mask = 0;
    uint32_t uid = 0;
    uint32_t share_prev_bits = 0;
    uint32_t share_cur_bits = 0;
    uint32_t ramp_in_bits = 0;
    uint32_t ramp_out_bits = 0;
    uint32_t role = 0;  // 4 overlay, 16 top, 32 partner
    bool replica = false;
    float replica_alpha = 0.0f;
};

/// Flattens one render tree into clip leaves, depth-first, as sub_8222AEF8 evaluates it.
/// `weight` is the leaf's share of the final pose; `importance` is the evaluator's `a7`, the
/// product the culls test (a set's children carry their raw weight in it, not the normalised one).
struct Flattener {
    const uint8_t* base;
    uint32_t* out;
    uint32_t capacity;
    uint32_t leaves = 0;
    Stated stated;

    struct Frame {
        uint32_t node;
        float weight;
        float importance;
        bool additive;
        uint32_t mask;
    };

    void Emit(const Frame& frame) {
        if (leaves >= capacity) {
            return;
        }
        uint32_t* leaf = out + leaves * kLeafWords;
        leaf[0] = Word(base, frame.node + 28u);
        leaf[1] = Word(base, frame.node + 36u);
        leaf[2] = Word(base, frame.node + 32u);
        if (stated.replica) {
            // `(1 − α)·+32 + α·+36` at the receipt alpha, the previous value one graph step back
            // so consecutive folds join (Fable 2's replica contract).
            const float previous = Float(base, frame.node + 32u);
            const float current = Float(base, frame.node + 36u);
            float step = current - previous;
            if (!(step > 0.0f) || !std::isfinite(step)) {
                step = 1.0f / 15.0f;
            }
            const float sampled = previous + step * stated.replica_alpha;
            if (std::isfinite(sampled)) {
                leaf[1] = Bits(sampled);
                leaf[2] = Bits(sampled - step);
            }
        }
        leaf[3] = Bits(frame.weight);
        leaf[4] = (Byte(base, frame.node + 40u) != 0u ? 1u : 0u) | (frame.additive ? 2u : 0u) |
                  (stated.replica ? 8u : 0u) | stated.role | ((stated.priority & 0xFFu) << 8) |
                  ((stated.track & 0xFFu) << 16);
        leaf[5] = stated.uid;
        leaf[6] = stated.ramp_in_bits;
        leaf[7] = stated.ramp_out_bits;
        leaf[8] = frame.mask;
        leaf[9] = stated.priority_mask;
        leaf[10] = stated.share_prev_bits;
        leaf[11] = stated.share_cur_bits;
        ++leaves;
    }

    void Walk(uint32_t root, float weight, float importance) {
        Frame stack[64];
        int depth = 0;
        stack[depth++] = Frame{root, weight, importance, false, 0u};
        int steps = 0;
        while (depth > 0 && leaves < capacity && ++steps < 512) {
            const Frame frame = stack[--depth];
            if (!Plausible(frame.node)) {
                continue;
            }
            const auto push = [&](uint32_t child, float w, float imp, bool additive, uint32_t mask) {
                if (depth < 64 && Plausible(child)) {
                    stack[depth++] = Frame{child, w, imp, additive, mask};
                }
            };
            switch (Word(base, frame.node + 24u)) {
                case 0u:
                    Emit(frame);
                    break;
                case 1u: {
                    const float f = Finite(Float(base, frame.node + 36u));
                    const uint32_t a = Word(base, frame.node + 28u);
                    const uint32_t b = Word(base, frame.node + 32u);
                    if (std::min(f, 1.0f - f) * frame.importance <= kCull) {
                        push(f >= 0.5f ? b : a, frame.weight, frame.importance, frame.additive, frame.mask);
                        break;
                    }
                    const float t = std::clamp(f, 0.0f, 1.0f);
                    push(a, frame.weight * (1.0f - t), frame.importance * (1.0f - t), frame.additive, frame.mask);
                    push(b, frame.weight * t, frame.importance * t, frame.additive, frame.mask);
                    break;
                }
                case 2u: {
                    const uint32_t begin = Word(base, frame.node + 32u);
                    const uint32_t end = Word(base, frame.node + 36u);
                    if (!Plausible(begin) || end < begin || end - begin > 4u * kMaxSetChildren) {
                        break;
                    }
                    uint32_t child[kMaxSetChildren];
                    float w[kMaxSetChildren];
                    uint32_t count = 0;
                    for (uint32_t at = begin; at + 4u <= end && count < kMaxSetChildren; at += 4u) {
                        child[count] = Word(base, at);
                        w[count] = Finite(Float(base, child[count] + 16u));
                        ++count;
                    }
                    // Heaviest first, stable for equal weights.
                    uint32_t order[kMaxSetChildren];
                    for (uint32_t i = 0; i < count; ++i) {
                        order[i] = i;
                    }
                    std::stable_sort(order, order + count, [&](uint32_t l, uint32_t r) { return w[l] > w[r]; });
                    uint32_t kept = count == 0u ? 0u : 1u;
                    float sum = count == 0u ? 0.0f : w[order[0]];
                    while (kept < count && w[order[kept]] * frame.importance > kCull) {
                        sum += w[order[kept]];
                        ++kept;
                    }
                    for (uint32_t i = 0; i < kept; ++i) {
                        const uint32_t k = order[i];
                        const float share = sum > 1e-6f ? w[k] / sum : (i == 0u ? 1.0f : 0.0f);
                        push(child[k], frame.weight * share, frame.importance * w[k], frame.additive, frame.mask);
                    }
                    break;
                }
                case 3u: {
                    push(Word(base, frame.node + 32u), frame.weight, frame.importance, frame.additive, frame.mask);
                    const uint32_t layer = Word(base, frame.node + 28u);
                    const float lw = Finite(Float(base, layer + 16u));
                    if (Plausible(layer) && lw * frame.importance >= kCull) {
                        push(layer, frame.weight * lw, frame.importance * lw, true, frame.mask);
                    }
                    break;
                }
                case 4u:
                    push(Word(base, frame.node + 28u), frame.weight, frame.importance, frame.additive, frame.node);
                    break;
                case 5u:
                    push(Word(base, frame.node + 32u), frame.weight, frame.importance, true, frame.mask);
                    break;
                default:
                    break;
            }
        }
    }
};

/// The 68-byte record, read once.
struct Record {
    uint32_t address = 0;
    uint32_t priority = 0;
    uint32_t track = 0;
    uint32_t mask = 0;
    float weight_cur = 0.0f;   // +20
    float weight_prev = 0.0f;  // +24
    float pair_cur = 0.0f;     // +28
    float pair_prev = 0.0f;    // +32
    uint32_t uid = 0;
    uint32_t root = 0;
    uint32_t ramp_in = 0;
    uint32_t ramp_out = 0;
};

uint32_t ReadList(const uint8_t* base, uint32_t manager, uint32_t sentinel_offset, Record* out) {
    const uint32_t sentinel = Word(base, manager + sentinel_offset);
    const uint32_t count = Word(base, manager + sentinel_offset + 4u);
    if (!Plausible(sentinel) || count == 0u || count > kMaxRecords) {
        return 0u;
    }
    uint32_t read = 0;
    uint32_t node = Word(base, sentinel);
    for (uint32_t i = 0; i < count && Plausible(node) && node != sentinel; ++i) {
        const uint32_t address = Word(base, node + 8u);
        node = Word(base, node);
        if (!Plausible(address)) {
            continue;
        }
        Record& r = out[read++];
        r.address = address;
        r.priority = Word(base, address + 8u);
        r.track = Word(base, address + 12u);
        r.mask = Word(base, address + 16u);
        r.weight_cur = Finite(Float(base, address + 20u));
        r.weight_prev = Finite(Float(base, address + 24u));
        r.pair_cur = Finite(Float(base, address + 28u));
        r.pair_prev = Finite(Float(base, address + 32u));
        r.uid = Word(base, address + 40u);
        r.root = Word(base, address + 44u);
        r.ramp_in = Word(base, address + 52u);
        r.ramp_out = Word(base, address + 60u);
    }
    return read;
}

/// sub_826BC358 at one end of the tick (`current` = α 1, else α 0): each non-top primary's
/// share of the partner set, normalised; false when the guest builds no partner there.
bool PartnerShares(const Record* records, uint32_t count, float top_weight, bool from_one, bool current,
                   float* shares) {
    std::fill(shares, shares + count, 0.0f);
    if (count < 2u || std::fabs(top_weight - 1.0f) < kCull) {
        return false;
    }
    float remaining = from_one ? 1.0f : 1.0f - top_weight;
    float total = 0.0f;
    for (uint32_t i = count - 1u; i-- > 0u;) {
        if (std::fabs(remaining) < kCull) {
            break;
        }
        const float w = current ? records[i].pair_cur : records[i].pair_prev;
        float s = (remaining - w < 0.0f || std::isnan(remaining - w)) ? remaining : w;
        if (std::fabs(s) < kCull) {
            continue;
        }
        if (from_one) {
            s *= remaining;
        }
        remaining -= s;
        total += s;
        shares[i] = s;
    }
    if (!(total > 1e-6f)) {
        std::fill(shares, shares + count, 0.0f);
        return false;
    }
    for (uint32_t i = 0; i < count; ++i) {
        shares[i] /= total;
    }
    return true;
}

/// sub_821CD100 / sub_826BE298 at one end of the tick, for the primary of priority `priority`:
/// each overlay's share of its track's layer, restated as the factor a SEQUENTIAL application
/// in list order needs — the walk takes a group tail first with shares s_k, the shell blends
/// the group head first, and `f_k = s_k / (1 − Σ_{j<k} s_j)` leaves every layer at s_k and the
/// base at `1 − Σ s` as the guest's one normalised layer does.
void OverlayFactors(const Record* overlays, uint32_t count, uint32_t priority, bool current, float* factors) {
    std::fill(factors, factors + count, 0.0f);
    uint32_t start = 0;
    while (start < count) {
        uint32_t end = start + 1u;
        while (end < count && overlays[end].track == overlays[start].track) {
            ++end;
        }
        float remaining = 1.0f;
        float taken = 0.0f;
        for (uint32_t k = end; k-- > start;) {
            if (std::fabs(remaining) < kCull) {
                break;
            }
            const Record& o = overlays[k];
            const float w = current ? o.weight_cur : o.weight_prev;
            if (std::fabs(w) < kCull || (o.mask & priority) == 0u) {
                continue;
            }
            const float s = (remaining - w < 0.0f || std::isnan(remaining - w)) ? remaining : w;
            factors[k] = 1.0f - taken > 1e-6f ? s / (1.0f - taken) : 0.0f;
            taken += s;
            remaining -= s;
        }
        start = end;
    }
}

/// THE CLIP A LINKED ANIMATION PLAYS — component 215, as sub_82711898 evaluates it — as one leaf,
/// or 0 when the guest poses nothing. The source is the weak ref at +12/+16 (sub_829A2650:
/// `**(comp215+16)`), live with source+148 bit 0x40 and its component 23 (sub_826C1570): the
/// replicated tree's dominant leaf (time `leaf+36 + now − mgr+100`) when one is installed, else
/// the TOP record's (tail) tree reduced by sub_821FB610. The leaf's key is looked up in the map
/// at +28 (node +12 key, +16 value, +21 nil) and the VALUE is played: one kind-0 node at weight 1,
/// looping, `+32 = +36 = t`.
uint32_t LinkedAnimLeaf(const uint8_t* base, uint32_t entity, uint32_t* out) {
    const uint32_t linked = Component(base, entity, kLinkedAnimation);
    if (linked == 0u) {
        return 0u;
    }
    const uint32_t control = Word(base, linked + 16u);
    const uint32_t source = Word(base, control);
    if (!Plausible(source) || (Byte(base, source + 148u) & 0x40u) == 0u) {
        return 0u;
    }
    const uint32_t manager = Component(base, source, kAnimationManager);
    if (manager == 0u) {
        return 0u;
    }

    // sub_821FB610: 0 the leaf; 1 B (+32) when +36 ≥ 0.5 else A (+28); 3 and 4 take +28; 5 +32;
    // a set has no dominant leaf.
    const auto dominant = [&](uint32_t node) -> uint32_t {
        for (int depth = 0; depth < 32 && Plausible(node); ++depth) {
            switch (Word(base, node + 24u)) {
                case 0u:
                    return node;
                case 1u:
                    node = Word(base, node + (Float(base, node + 36u) >= 0.5f ? 32u : 28u));
                    break;
                case 3u:
                case 4u:
                    node = Word(base, node + 28u);
                    break;
                case 5u:
                    node = Word(base, node + 32u);
                    break;
                default:
                    return 0u;
            }
        }
        return 0u;
    };

    uint32_t leaf = 0u;
    float time = 0.0f;
    float previous = 0.0f;
    bool replica = false;
    const uint32_t tree = Word(base, manager + 92u);
    if (Plausible(tree)) {
        leaf = dominant(tree);
        double now = 0.0;
        if (leaf == 0u || !GuestSeconds(base, &now)) {
            return 0u;
        }
        const float current = Float(base, leaf + 36u);
        float step = current - Float(base, leaf + 32u);
        if (!(step > 0.0f) || !std::isfinite(step)) {
            step = 1.0f / 15.0f;
        }
        time = static_cast<float>(now - Float(base, manager + 100u)) + current;
        previous = time - step;
        replica = true;
    } else {
        const uint32_t sentinel = Word(base, manager + 28u);
        const uint32_t tail = Word(base, sentinel + 4u);
        if (!Plausible(sentinel) || tail == sentinel) {
            return 0u;
        }
        leaf = dominant(Word(base, Word(base, tail + 8u) + 44u));
        if (leaf == 0u) {
            return 0u;
        }
        time = Float(base, leaf + 36u);
        previous = Float(base, leaf + 32u);
    }
    if (!std::isfinite(time) || !std::isfinite(previous)) {
        return 0u;
    }

    const uint32_t source_key = Word(base, leaf + 28u);
    const uint32_t head = Word(base, linked + 28u);
    if (!Plausible(head)) {
        return 0u;
    }
    uint32_t found = head;
    uint32_t at = Word(base, head + 4u);
    for (int steps = 0; steps < 64 && Plausible(at) && Byte(base, at + 21u) == 0u; ++steps) {
        if (Word(base, at + 12u) >= source_key) {
            found = at;
            at = Word(base, at);
        } else {
            at = Word(base, at + 8u);
        }
    }
    if (found == head || source_key < Word(base, found + 12u)) {
        return 0u;
    }
    const uint32_t key = Word(base, found + 16u);
    if (key == 0u) {
        return 0u;
    }

    out[0] = key;
    out[1] = Bits(time);
    out[2] = Bits(previous);
    out[3] = Bits(1.0f);
    // Looping, the sole top, and a replica extrapolation when the time ran on from a receipt.
    out[4] = 1u | (replica ? 8u : 0u) | 16u;
    // The leaf's identity: the linked component, never a record uid of the rig.
    out[5] = linked;
    out[6] = 0u;
    out[7] = 0u;
    out[8] = 0u;
    out[9] = 0xFFFFFFFFu;
    out[10] = Bits(1.0f);
    out[11] = Bits(1.0f);
    return 1u;
}

/// The replicated tree as sub_826BDEC8 folds it: the current tree at its receipt alpha
/// `(now − mgr+100)·15`, crossfaded from the previous one (+88, alpha from +96) by a kind-6 node
/// whose factor `clamp((now − mgr+104)·13.333, 0, 1)` reaches 1 in 75 ms. The component-25
/// layer sub_826BDEC8 wraps over it (its type-44 object's +212 tree) is not read yet.
void WalkReplica(const uint8_t* base, uint32_t manager, Flattener& flat) {
    double now = 0.0;
    const bool clock = GuestSeconds(base, &now);
    const auto alpha_since = [&](uint32_t receipt_offset) -> float {
        if (!clock) {
            return 1.0f;
        }
        const double alpha = (now - Float(base, manager + receipt_offset)) * 15.0;
        // A minute of extrapolation is a tree the host stopped sending, not a clip to run on.
        return std::isfinite(alpha) && alpha > -1.0 && alpha < 15.0 * 60.0 ? static_cast<float>(alpha) : 1.0f;
    };

    float fade = 1.0f;
    const uint32_t previous = Word(base, manager + 88u);
    if (Plausible(previous) && clock) {
        fade = std::clamp(static_cast<float>((now - Float(base, manager + 104u)) * 13.33333280351429), 0.0f, 1.0f);
    }

    flat.stated = Stated{};
    flat.stated.priority_mask = 0xFFFFFFFFu;
    flat.stated.role = 16u;
    flat.stated.replica = clock;
    flat.stated.share_prev_bits = Bits(1.0f);
    flat.stated.share_cur_bits = Bits(1.0f);
    if (fade < 1.0f && std::min(fade, 1.0f - fade) > kCull) {
        flat.stated.replica_alpha = alpha_since(96u);
        flat.Walk(previous, 1.0f - fade, 1.0f - fade);
        flat.stated.replica_alpha = alpha_since(100u);
        flat.Walk(Word(base, manager + 92u), fade, fade);
    } else {
        flat.stated.replica_alpha = alpha_since(fade < 0.5f ? 96u : 100u);
        flat.Walk(fade < 0.5f ? previous : Word(base, manager + 92u), 1.0f, 1.0f);
    }
}

/// Out flags: bit0 the guest's full bone tier (mgr+84 == 4), bit3 physics owns the pose (the
/// ragdoll in mode 3 or 4 — the shell hands the rig back to the pose feed).
///
/// [10]/[11] ARE THE FOLD'S SHARES, not the raw record weights. The shell folds the top against
/// its partner by `top / (top + partner)` and applies overlays one after another; stated as
/// shares, that arithmetic gives the guest's: the top states `w` at each end of the tick (1 where
/// the guest builds no partner), each partner `(1 − w)·s_i/Σs`, so the partner set sums to
/// `1 − w`; each overlay its sequential factor (OverlayFactors). Each end is the guest's own fold
/// at α 0 and α 1; between them the shell interpolates, which is exact while the partner set does
/// not change inside the tick.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetEntityAnimState(uint32_t entity, uint32_t* out_words, uint32_t word_capacity,
                                                            uint32_t* out_flags) {
    if (out_flags != nullptr) {
        *out_flags = 0u;
    }
    if (out_words == nullptr || word_capacity < kLeafWords || !Plausible(entity)) {
        return 0u;
    }
    rex::memory::Memory* memory = SafeKernelMemory();
    const uint8_t* base = memory == nullptr ? nullptr : memory->virtual_membase();
    if (base == nullptr) {
        return 0u;
    }

    uint32_t flags = PhysicsOwnsPose(base, entity) ? 8u : 0u;
    const uint32_t manager = Component(base, entity, kAnimationManager);
    const uint32_t primary_count = manager == 0u ? 0u : Word(base, manager + 32u);
    const uint32_t replica = manager == 0u ? 0u : Word(base, manager + 92u);

    // THE GATE'S CHOICE OF SOURCE (sub_823531E0 0x82353c30..0x82353e8c): component 23 with records
    // or a replicated tree poses the rig; otherwise component 215 does, when present.
    if (manager == 0u || (primary_count == 0u && !Plausible(replica))) {
        const uint32_t linked = LinkedAnimLeaf(base, entity, out_words);
        if (out_flags != nullptr) {
            *out_flags = flags;
        }
        if (linked != 0u) {
            gLinked.fetch_add(1u, std::memory_order_relaxed);
            return linked;
        }
        if (manager == 0u) {
            gNoManager.fetch_add(1u, std::memory_order_relaxed);
        } else {
            gManagerFound.fetch_add(1u, std::memory_order_relaxed);
            gEmptyList.fetch_add(1u, std::memory_order_relaxed);
        }
        return 0u;
    }
    gManagerFound.fetch_add(1u, std::memory_order_relaxed);

    if (Word(base, manager + 84u) == kFullTier) {
        flags |= 1u;
    }
    if (out_flags != nullptr) {
        *out_flags = flags;
    }

    Flattener flat{base, out_words, word_capacity / kLeafWords};

    // THE GUEST'S OWN CHOICE OF ROOT (sub_821E9AC0 0x821e9af8, sub_82208F50 0x82208f88): the
    // replicated tree when it exists and the primary list is empty or holds nothing at 128.
    if (Plausible(replica) && (primary_count == 0u || static_cast<int32_t>(Word(base, manager + 644u)) < 128)) {
        WalkReplica(base, manager, flat);
        if (flat.leaves == 0u) {
            gEmptyList.fetch_add(1u, std::memory_order_relaxed);
        }
        return flat.leaves;
    }

    Record primaries[kMaxRecords];
    Record overlays[kMaxRecords];
    const uint32_t count = ReadList(base, manager, 28u, primaries);
    const uint32_t overlay_count = ReadList(base, manager, 40u, overlays);
    if (count == 0u) {
        gEmptyList.fetch_add(1u, std::memory_order_relaxed);
        return 0u;
    }

    // TOP = the tail (sub_821EB068: `sentinel->prev`), whatever its state.
    const Record& top = primaries[count - 1u];
    const bool crossfade = Byte(base, kCrossfadeOn) != 0u;
    const bool from_one = Byte(base, kPartnerFromOne) != 0u;
    float share_prev[kMaxRecords];
    float share_cur[kMaxRecords];
    const bool partner_prev = crossfade && PartnerShares(primaries, count, top.weight_prev, from_one, false, share_prev);
    const bool partner_cur = crossfade && PartnerShares(primaries, count, top.weight_cur, from_one, true, share_cur);
    if (!partner_prev) {
        std::fill(share_prev, share_prev + count, 0.0f);
    }
    if (!partner_cur) {
        std::fill(share_cur, share_cur + count, 0.0f);
    }

    uint32_t heaviest_partner = count;
    for (uint32_t i = 0; i + 1u < count; ++i) {
        if (share_cur[i] > 0.0f && (heaviest_partner == count || share_cur[i] > share_cur[heaviest_partner])) {
            heaviest_partner = i;
        }
    }

    const auto stated_for = [&](const Record& r, uint32_t role, float prev, float cur) {
        Stated s;
        s.priority = r.priority;
        s.track = r.track;
        s.priority_mask = r.mask;
        s.uid = r.uid;
        s.share_prev_bits = Bits(prev);
        s.share_cur_bits = Bits(cur);
        s.ramp_in_bits = RampSecondsBits(base, r.ramp_in, kRampInTimed);
        s.ramp_out_bits = RampSecondsBits(base, r.ramp_out, kRampOutTimed);
        s.role = role;
        return s;
    };

    // The top: `w` where the guest crossfades, the whole pose where it builds no partner. A top
    // on its opening tick weighs 0; the leaf carries Fable 2's floor so the shell registers the
    // channel, the share stays the guest's.
    {
        const float prev = partner_prev ? top.weight_prev : 1.0f;
        const float cur = partner_cur ? top.weight_cur : 1.0f;
        flat.stated = stated_for(top, 16u, prev, cur);
        flat.Walk(top.root, std::max(cur, 1e-4f), cur);
    }

    // The partner set: `(1 − w)·s_i/Σs` at each end.
    for (uint32_t i = 0; i + 1u < count; ++i) {
        const float prev = partner_prev ? (1.0f - top.weight_prev) * share_prev[i] : 0.0f;
        const float cur = partner_cur ? (1.0f - top.weight_cur) * share_cur[i] : 0.0f;
        if (prev <= 0.0f && cur <= 0.0f) {
            continue;
        }
        flat.stated = stated_for(primaries[i], 32u, prev, cur);
        flat.Walk(primaries[i].root, std::max(cur, 1e-4f), cur);
    }

    // The overlays, as they wrap the top (and, for those the top's priority does not admit, the
    // heaviest partner); the shell gates each again by its priority mask against both.
    if (overlay_count != 0u) {
        float factor_prev[kMaxRecords];
        float factor_cur[kMaxRecords];
        OverlayFactors(overlays, overlay_count, top.priority, false, factor_prev);
        OverlayFactors(overlays, overlay_count, top.priority, true, factor_cur);
        if (heaviest_partner != count && primaries[heaviest_partner].priority != top.priority) {
            float partner_factor_prev[kMaxRecords];
            float partner_factor_cur[kMaxRecords];
            const uint32_t priority = primaries[heaviest_partner].priority;
            OverlayFactors(overlays, overlay_count, priority, false, partner_factor_prev);
            OverlayFactors(overlays, overlay_count, priority, true, partner_factor_cur);
            for (uint32_t k = 0; k < overlay_count; ++k) {
                if (factor_prev[k] <= 0.0f && factor_cur[k] <= 0.0f) {
                    factor_prev[k] = partner_factor_prev[k];
                    factor_cur[k] = partner_factor_cur[k];
                }
            }
        }
        for (uint32_t k = 0; k < overlay_count; ++k) {
            if (factor_prev[k] <= 0.0f && factor_cur[k] <= 0.0f) {
                continue;
            }
            flat.stated = stated_for(overlays[k], 4u, factor_prev[k], factor_cur[k]);
            flat.Walk(overlays[k].root, std::max(factor_cur[k], 1e-4f), factor_cur[k]);
        }
    }

    if (flat.leaves == 0u) {
        gEmptyList.fetch_add(1u, std::memory_order_relaxed);
    }
    return flat.leaves;
}

/// THE BYTES OF ONE BONE MASK NODE — kind 4 (sub_8222AEF8 case 4 -> sub_821C7B18): one byte per
/// skeleton bone in [+40, +44), applied only when the pose's skeleton id equals +32 (0x821c7b68).
/// Returns the bytes written (0 for a node that is not a mask) and, when asked, that skeleton id.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetAnimMask(uint32_t node, uint8_t* out_bytes, uint32_t capacity,
                                                    uint32_t* out_skeleton_id) {
    if (out_skeleton_id != nullptr) {
        *out_skeleton_id = 0u;
    }
    if (out_bytes == nullptr || capacity == 0u) {
        return 0u;
    }
    rex::memory::Memory* memory = SafeKernelMemory();
    const uint8_t* base = memory == nullptr ? nullptr : memory->virtual_membase();
    if (base == nullptr || !Plausible(node) || Word(base, node + 24u) != 4u) {
        return 0u;
    }
    const uint32_t begin = Word(base, node + 40u);
    const uint32_t end = Word(base, node + 44u);
    if (!Plausible(begin) || end < begin || end - begin > 1024u) {
        return 0u;
    }
    if (out_skeleton_id != nullptr) {
        *out_skeleton_id = Word(base, node + 32u);
    }
    uint32_t written = 0u;
    for (uint32_t at = begin; at < end && written < capacity; ++at) {
        out_bytes[written++] = GuestByteAt(base, at);
    }
    return written;
}

#undef REFII_HOST_ABI_EXPORT

}  // namespace reference

// ---------------------------------------------------------------------------------------------
// Synthetic rigs.

constexpr uint32_t kRegion = 0x40000000u;
constexpr uint32_t kRampInInstant = 0x820019F4u;
constexpr uint32_t kRampOutInstant = 0x820A7108u;

/// Node sizes by kind, as the guest allocates them (anim_walk.h).
constexpr uint32_t kNodeSize[] = {0x2Cu, 0x2Cu, 0x2Cu, 0x24u, 0x38u, 0x24u, 0x30u};

struct World {
    SyntheticMemory memory;
    std::mt19937 rng;
    uint32_t cursor = kRegion;
    std::vector<uint32_t> edges;  // pages either side of an object across a page boundary
    std::vector<uint32_t> nodes;
    std::vector<uint32_t> masks;
    std::vector<uint32_t> keys;
    std::vector<uint32_t> rigs;
    int depth_budget = 0;

    explicit World(uint32_t seed) : rng(seed) {}

    uint32_t Rand(uint32_t n) { return n == 0u ? 0u : static_cast<uint32_t>(rng() % n); }
    bool Chance(double p) { return std::uniform_real_distribution<double>(0.0, 1.0)(rng) < p; }
    float Uniform(float lo, float hi) { return std::uniform_real_distribution<float>(lo, hi)(rng); }

    /// A fresh object of `size` bytes, zeroed: mostly 16-aligned; some across a page boundary
    /// (4-aligned, or not), whose pages are decommitted more often; a few at an address that is not
    /// 4-aligned; some running over the 0xC0000000 ceiling, filled with noise, so a field past it
    /// reads non-zero unless it is held to 0 as no guest address.
    uint32_t Alloc(uint32_t size) {
        const uint32_t roll = Rand(100u);
        if (roll < 2u) {
            return AtCeiling(size);
        }
        uint32_t at = (cursor + 15u) & ~15u;
        if (roll < 10u) {
            const uint32_t boundary = (at + kGuestSpanPage) & ~(kGuestSpanPage - 1u);
            at = boundary - 4u * (1u + Rand(std::max(1u, size / 4u)));
            if (roll >= 7u) at += 1u + Rand(3u);
            edges.push_back(boundary / kGuestSpanPage - 1u);
            edges.push_back(boundary / kGuestSpanPage);
        } else if (roll < 12u) {
            at += 1u + Rand(3u);
        } else if (roll < 24u) {
            at += 16u * Rand(8u);
        }
        cursor = at + size;
        for (uint32_t i = 0; i < size; ++i) memory.Put8(at + i, 0u);
        return at;
    }

    /// An object running over the 0xC0000000 ceiling, noise throughout.
    uint32_t AtCeiling(uint32_t size) {
        const uint32_t at = anim::kGuestCeiling - 4u * (1u + Rand(size / 4u + 1u)) - (Chance(0.2) ? 1u + Rand(3u) : 0u);
        for (uint32_t i = 0; i < size; ++i) memory.Put8(at + i, static_cast<uint8_t>(rng()));
        return at;
    }

    uint32_t Garbage() {
        static constexpr uint32_t kOdd[] = {0u,          4u,          0xFFFCu,     0x10000u, 0xBFFFFFFCu,
                                            0xC0000010u, 0xFFFFFFF0u, 0x0000F000u, 0x7FFF0000u};
        switch (Rand(4u)) {
            case 0: return kOdd[Rand(sizeof(kOdd) / sizeof(kOdd[0]))];
            case 1: return kRegion + Rand(cursor - kRegion + 0x4000u);  // anywhere, mapped or not
            case 2: return static_cast<uint32_t>(rng());
            default: return nodes.empty() ? 0u : nodes[Rand(static_cast<uint32_t>(nodes.size()))] + 4u * Rand(12u);
        }
    }

    float Weight() {
        switch (Rand(12u)) {
            case 0: return 0.0f;
            case 1: return 1.0f;
            case 2: return 0.05f;
            case 3: return 0.0499f;
            case 4: return 0.95f;
            case 5: return -Uniform(0.0f, 0.3f);
            case 6: return std::numeric_limits<float>::quiet_NaN();
            case 7: return std::numeric_limits<float>::infinity();
            case 8: return Uniform(1.0f, 3.0f);
            case 9: return 0.5f;
            default: return Uniform(0.0f, 1.0f);
        }
    }

    uint32_t Key() {
        if (keys.size() >= 24u || (!keys.empty() && Chance(0.7))) return keys[Rand(static_cast<uint32_t>(keys.size()))];
        const uint32_t key = static_cast<uint32_t>(rng()) | 1u;
        keys.push_back(key);
        return key;
    }

    uint32_t Ramp(uint32_t timed) {
        switch (Rand(6u)) {
            case 0: return Garbage();
            case 1: {
                const uint32_t ramp = Alloc(4u);
                memory.Put32(ramp, timed == anim::kRampInTimed ? kRampInInstant : kRampOutInstant);
                return ramp;
            }
            default: {
                const uint32_t ramp = Alloc(12u);
                memory.Put32(ramp, Chance(0.9) ? timed : (anim::kRampInTimed ^ anim::kRampOutTimed ^ timed));
                static constexpr float kSteps[] = {0.0f, 1e-7f, 0.01f, 0.0666f, 0.2f, 0.999f, 1.0f, 1.5f};
                memory.PutF(ramp + 4u, Chance(0.1) ? std::numeric_limits<float>::quiet_NaN()
                                                   : kSteps[Rand(sizeof(kSteps) / sizeof(kSteps[0]))]);
                memory.PutF(ramp + 8u, Uniform(0.0f, 2.0f));
                return ramp;
            }
        }
    }

    uint32_t Tree(int depth) {
        if (depth_budget <= 0 || Chance(0.03)) return Garbage();
        if (!nodes.empty() && Chance(0.05)) return nodes[Rand(static_cast<uint32_t>(nodes.size()))];
        --depth_budget;
        uint32_t kind = depth <= 0 ? 0u : Rand(10u);
        if (kind >= 7u) kind = Chance(0.2) ? 9u : 0u;
        const uint32_t size = kind < 7u ? kNodeSize[kind] : 0x2Cu;
        const uint32_t node = Alloc(size);
        nodes.push_back(node);
        memory.Put32(node, 0x82000DE0u);
        memory.PutF(node + 16u, Weight());
        memory.PutF(node + 20u, Weight());
        memory.Put32(node + 24u, kind);
        switch (kind) {
            case 0:
                memory.Put32(node + 28u, Key());
                memory.PutF(node + 32u, Chance(0.05) ? std::numeric_limits<float>::quiet_NaN() : Uniform(0.0f, 5.0f));
                memory.PutF(node + 36u, Chance(0.05) ? std::numeric_limits<float>::infinity() : Uniform(0.0f, 5.0f));
                memory.Put8(node + 40u, static_cast<uint8_t>(Chance(0.5) ? 0u : Rand(256u)));
                break;
            case 1:
                memory.Put32(node + 28u, Tree(depth - 1));
                memory.Put32(node + 32u, Tree(depth - 1));
                memory.PutF(node + 36u, Weight());
                memory.PutF(node + 40u, Weight());
                break;
            case 2: {
                uint32_t n = Rand(7u);
                if (Chance(0.03)) n = 64u + Rand(3u);
                const uint32_t children = Chance(0.08) ? AtCeiling(4u * std::max(n, 1u)) : Alloc(4u * std::max(n, 1u));
                for (uint32_t i = 0; i < n; ++i) memory.Put32(children + 4u * i, Chance(0.1) ? Garbage() : Tree(depth - 1));
                uint32_t end = children + 4u * n;
                if (Chance(0.05)) end = children - 4u;
                if (Chance(0.05)) end += 1u + Rand(3u);
                memory.Put32(node + 32u, Chance(0.03) ? Garbage() : children);
                memory.Put32(node + 36u, end);
                memory.Put32(node + 40u, end);
                break;
            }
            case 3:
            case 5:
                memory.Put32(node + 28u, Tree(depth - 1));
                memory.Put32(node + 32u, Tree(depth - 1));
                break;
            case 4: {
                masks.push_back(node);
                memory.Put32(node + 28u, Tree(depth - 1));
                memory.Put32(node + 32u, static_cast<uint32_t>(rng()));
                uint32_t n = Rand(90u);
                if (Chance(0.05)) n = 1020u + Rand(8u);
                const uint32_t bytes = Chance(0.05) ? AtCeiling(std::max(n, 1u)) : Alloc(std::max(n, 1u));
                for (uint32_t i = 0; i < n; ++i) memory.Put8(bytes + i, static_cast<uint8_t>(rng()));
                memory.Put32(node + 40u, Chance(0.03) ? Garbage() : bytes);
                memory.Put32(node + 44u, Chance(0.03) ? bytes - 1u : bytes + n);
                break;
            }
            case 6:
                memory.Put32(node + 28u, Tree(depth - 1));
                memory.Put32(node + 32u, Tree(depth - 1));
                memory.PutF(node + 36u, Weight());
                break;
            default:
                memory.Put32(node + 28u, Tree(depth - 1));
                memory.Put32(node + 32u, Tree(depth - 1));
                memory.Put32(node + 36u, static_cast<uint32_t>(rng()));
                break;
        }
        return node;
    }

    uint32_t Record() {
        const uint32_t record = Alloc(68u);
        memory.Put32(record, 0x82000E94u);
        memory.Put32(record + 4u, 1u);
        static constexpr uint32_t kPriorities[] = {0u, 1u, 2u, 4u, 8u, 16u, 64u, 128u, 255u, 256u, 300u};
        memory.Put32(record + 8u, kPriorities[Rand(sizeof(kPriorities) / sizeof(kPriorities[0]))]);
        memory.Put32(record + 12u, Rand(4u));
        memory.Put32(record + 16u, Chance(0.3) ? 0xFFFFFFFFu : static_cast<uint32_t>(rng()));
        for (uint32_t offset = 20u; offset <= 32u; offset += 4u) memory.PutF(record + offset, Weight());
        memory.Put32(record + 36u, Rand(3u));
        memory.Put32(record + 40u, static_cast<uint32_t>(rng()));
        depth_budget = 24;
        memory.Put32(record + 44u, Tree(1 + static_cast<int>(Rand(5u))));
        memory.Put32(record + 52u, Ramp(anim::kRampInTimed));
        memory.Put32(record + 60u, Ramp(anim::kRampOutTimed));
        return record;
    }

    /// A list at manager+offset: sentinel, count and up to `n` nodes.
    void List(uint32_t manager, uint32_t offset, uint32_t n) {
        const uint32_t sentinel = Alloc(12u);
        std::vector<uint32_t> links{sentinel};
        for (uint32_t i = 0; i < n; ++i) {
            const uint32_t node = Alloc(12u);
            memory.Put32(node + 8u, Chance(0.04) ? Garbage() : Record());
            links.push_back(node);
        }
        for (std::size_t i = 0; i < links.size(); ++i) {
            const uint32_t next = links[(i + 1u) % links.size()];
            const uint32_t prev = links[(i + links.size() - 1u) % links.size()];
            memory.Put32(links[i], Chance(0.02) ? (Chance(0.5) ? Garbage() : links[Rand(static_cast<uint32_t>(links.size()))]) : next);
            memory.Put32(links[i] + 4u, prev);
        }
        uint32_t count = n;
        if (Chance(0.05)) count = n + Rand(4u);
        if (Chance(0.03)) count = 65u + Rand(10u);
        if (Chance(0.03) && n > 0u) count = n - 1u;
        memory.Put32(manager + offset, Chance(0.02) ? Garbage() : sentinel);
        memory.Put32(manager + offset + 4u, count);
    }

    float Now() const {
        return 100.0f;  // the guest clock below reads 100 s
    }

    uint32_t Manager() {
        const uint32_t manager = Alloc(704u);
        memory.Put32(manager, 0x82003800u);
        uint32_t primaries = Rand(6u);
        if (Chance(0.15)) primaries = Rand(65u);
        uint32_t overlays = Chance(0.5) ? Rand(5u) : 0u;
        if (Chance(0.08)) overlays = Rand(65u);
        if (Chance(0.15)) primaries = 0u;
        List(manager, 28u, primaries);
        List(manager, 40u, overlays);
        memory.Put32(manager + 84u, Chance(0.7) ? 4u : Rand(6u));
        if (Chance(0.25)) {
            depth_budget = 24;
            memory.Put32(manager + 88u, Tree(1 + static_cast<int>(Rand(4u))));
        }
        if (Chance(0.3)) {
            depth_budget = 24;
            memory.Put32(manager + 92u, Tree(1 + static_cast<int>(Rand(4u))));
        }
        for (uint32_t offset = 96u; offset <= 104u; offset += 4u) {
            float t = Now() - Uniform(0.0f, 0.2f);
            if (Chance(0.05)) t = Now() - 100.0f;
            if (Chance(0.03)) t = std::numeric_limits<float>::quiet_NaN();
            if (Chance(0.03)) t = Now() + 1.0f;
            memory.PutF(manager + offset, t);
        }
        static constexpr int32_t kTop[] = {0, 1, 127, 128, 129, 255, -5};
        memory.Put32(manager + 644u, static_cast<uint32_t>(kTop[Rand(sizeof(kTop) / sizeof(kTop[0]))]));
        return manager;
    }

    /// A std::map<u32, u32> of `n` keys (MSVC: the head is the nil node, +21 set).
    uint32_t Map(uint32_t n) {
        const uint32_t head = Alloc(24u);
        memory.Put8(head + 20u, 1u);
        memory.Put8(head + 21u, Chance(0.97) ? 1u : 0u);
        std::vector<uint32_t> sorted;
        for (uint32_t i = 0; i < n; ++i) sorted.push_back(Key());
        std::sort(sorted.begin(), sorted.end());
        sorted.erase(std::unique(sorted.begin(), sorted.end()), sorted.end());
        const auto build = [&](auto&& self, int lo, int hi, uint32_t parent) -> uint32_t {
            if (lo >= hi) return head;
            const int mid = (lo + hi) / 2;
            const uint32_t node = Alloc(24u);
            memory.Put32(node + 4u, parent);
            memory.Put32(node + 12u, sorted[mid]);
            memory.Put32(node + 16u, Chance(0.1) ? 0u : static_cast<uint32_t>(rng()));
            memory.Put32(node, Chance(0.02) ? Garbage() : self(self, lo, mid, node));
            memory.Put32(node + 8u, Chance(0.02) ? Garbage() : self(self, mid + 1, hi, node));
            return node;
        };
        memory.Put32(head + 4u, Chance(0.03) ? Garbage() : build(build, 0, static_cast<int>(sorted.size()), head));
        return head;
    }

    uint32_t Placement() {
        const uint32_t placement = Alloc(0x40u);
        const uint32_t vtable = Alloc(0x80u);
        memory.Put32(placement, Chance(0.05) ? Garbage() : vtable);
        memory.Put32(vtable + 0x70u, Chance(0.6) ? anim::kPhysicsProxyGetter : 0x82CE17A8u);
        const uint32_t proxy = Alloc(12u);
        memory.Put32(placement + 0x20u, Chance(0.05) ? Garbage() : proxy);
        const uint32_t n = Chance(0.05) ? 64u + Rand(3u) : Rand(5u);
        const uint32_t pairs = Chance(0.08) ? AtCeiling(8u * std::max(n, 1u)) : Alloc(8u * std::max(n, 1u));
        for (uint32_t i = 0; i < n; ++i) {
            uint32_t object;
            if (Chance(0.5)) {
                object = Alloc(160u);
                memory.Put32(object, anim::kRagdollVtable);
                memory.Put32(object + 0x40u, Rand(6u));
                memory.Put8(object + 0x44u, static_cast<uint8_t>(Chance(0.3) ? 0u : 1u + Rand(255u)));
            } else {
                object = Chance(0.2) ? Garbage() : Alloc(0x50u);
                if (Plausible(object)) memory.Put32(object, 0x82007000u + 8u * Rand(16u));
            }
            memory.Put32(pairs + 8u * i, object);
            memory.Put32(pairs + 8u * i + 4u, static_cast<uint32_t>(rng()));
        }
        uint32_t end = pairs + 8u * n;
        if (Chance(0.05)) end += 1u + Rand(7u);
        if (Chance(0.03)) end = pairs - 8u;
        memory.Put32(proxy + 4u, Chance(0.03) ? Garbage() : pairs);
        memory.Put32(proxy + 8u, end);
        return Chance(0.03) ? Garbage() : placement;
    }

    static bool Plausible(uint32_t address) { return anim::Plausible(address); }

    uint32_t Entity(bool manager, bool linked, int nesting) {
        const uint32_t entity = Alloc(0x98u);
        memory.Put8(entity + 0x94u, static_cast<uint8_t>(Chance(0.8) ? 0x40u | Rand(256u) : Rand(256u) & ~0x40u));
        memory.Put32(entity + 0x80u, Placement());
        std::vector<std::pair<uint32_t, uint32_t>> pairs;
        const uint32_t noise = Rand(6u);
        for (uint32_t i = 0; i < noise; ++i) pairs.push_back({1u + Rand(40u), Garbage()});
        if (manager) pairs.push_back({anim::kAnimationManager, Chance(0.03) ? Garbage() : Manager()});
        if (linked && nesting < 2) {
            const uint32_t component = Alloc(0x38u);
            const uint32_t control = Alloc(8u);
            memory.Put32(component + 16u, Chance(0.03) ? Garbage() : control);
            memory.Put32(control, Chance(0.05) ? Garbage() : Entity(Chance(0.9), false, nesting + 1));
            memory.Put32(component + 28u, Chance(0.03) ? Garbage() : Map(Rand(9u)));
            pairs.push_back({anim::kLinkedAnimation, component});
        }
        if (Chance(0.05) && !pairs.empty()) pairs.push_back(pairs[Rand(static_cast<uint32_t>(pairs.size()))]);
        for (std::size_t i = pairs.size(); i > 1u; --i) std::swap(pairs[i - 1u], pairs[Rand(static_cast<uint32_t>(i))]);
        const uint32_t vector_size = 8u * std::max<uint32_t>(static_cast<uint32_t>(pairs.size()), 1u);
        const uint32_t vector = Chance(0.04) ? AtCeiling(vector_size) : Alloc(vector_size);
        for (std::size_t i = 0; i < pairs.size(); ++i) {
            memory.Put32(vector + 8u * static_cast<uint32_t>(i), pairs[i].first);
            memory.Put32(vector + 8u * static_cast<uint32_t>(i) + 4u, pairs[i].second);
        }
        uint32_t end = vector + 8u * static_cast<uint32_t>(pairs.size());
        if (Chance(0.04)) end -= 4u;
        if (Chance(0.03)) end = vector + 2000u;
        if (Chance(0.02)) end = vector - 8u;
        memory.Put32(entity + 0x54u, Chance(0.02) ? Garbage() : vector);
        memory.Put32(entity + 0x58u, end);
        return entity;
    }

    void Globals() {
        memory.Put8(anim::kCrossfadeOn, static_cast<uint8_t>(Chance(0.8) ? 1u : 0u));
        memory.Put8(anim::kPartnerFromOne, static_cast<uint8_t>(Chance(0.3) ? 1u : 0u));
        // The clock: frequency 50 MHz, base so that now = 100 s; sometimes no usable frequency.
        double frequency = 50e6;
        if (Chance(0.05)) frequency = 0.0;
        if (Chance(0.03)) frequency = std::numeric_limits<double>::quiet_NaN();
        uint64_t bits;
        std::memcpy(&bits, &frequency, sizeof(bits));
        const uint64_t base = 123456789ull;
        memory.Put32(anim::kTimeFrequency, static_cast<uint32_t>(bits >> 32));
        memory.Put32(anim::kTimeFrequency + 4u, static_cast<uint32_t>(bits));
        memory.Put32(anim::kTimeFrequency + 8u, static_cast<uint32_t>(base >> 32));
        memory.Put32(anim::kTimeFrequency + 12u, static_cast<uint32_t>(base));
        memory.ticks = base + static_cast<uint64_t>(100.0 * 50e6) + Rand(1000u);
        // Readable bytes where no guest object is, so a read there that should be 0 is not.
        for (uint32_t i = 0; i < 64u; ++i) {
            memory.Put32(0x0000u + 4u * i, 0xDEAD0000u | i);
            memory.Put32(0xF000u + 4u * i, 0xBEEF0000u | i);
            memory.Put32(0xFFC0u + (i % 16u) * 4u, 0x0BAD0000u | i);
            memory.Put32(0xC0000000u + 4u * i, 0xFACE0000u | i);
        }
    }
};

struct Totals {
    uint64_t rigs = 0;
    uint64_t posed = 0;
    uint64_t leaves = 0;
    uint64_t masks = 0;
    uint64_t reference_reads = 0;
    uint64_t span_reads = 0;
    uint64_t reference_posed_reads = 0;
    uint64_t span_posed_reads = 0;
    uint64_t reference_mask_reads = 0;
    uint64_t span_mask_reads = 0;
    uint64_t reference_outside = 0;
    uint64_t span_outside = 0;
};

bool Fail(const char* what, uint32_t seed, uint32_t address, uint32_t index, uint32_t expected, uint32_t actual) {
    std::fprintf(stderr, "anim_state_test: %s differs (world %u, 0x%08X, word %u): per-field 0x%08X, span 0x%08X\n",
                 what, seed, address, index, expected, actual);
    return false;
}

uint32_t Causes() {
    // The reference's counters, as the bits anim_walk.h reports.
    static uint64_t last[4] = {0, 0, 0, 0};
    const uint64_t now[4] = {reference::gNoManager.load(), reference::gManagerFound.load(), reference::gEmptyList.load(),
                             reference::gLinked.load()};
    uint32_t bits = 0u;
    const uint32_t kBits[4] = {anim::kCountNoManager, anim::kCountManagerFound, anim::kCountEmptyList, anim::kCountLinked};
    for (int i = 0; i < 4; ++i) {
        if (now[i] - last[i] > 1u) bits |= 0x100u;  // never more than one per rig
        if (now[i] != last[i]) bits |= kBits[i];
        last[i] = now[i];
    }
    return bits;
}

bool CheckRig(World& world, uint32_t seed, uint32_t entity, Totals& totals) {
    static constexpr uint32_t kCapacities[] = {0u, 11u, 12u, 13u, 24u, 60u, 12u * 40u, 12u * 300u};
    const uint32_t capacity = world.Chance(0.7) ? 12u * 300u : kCapacities[world.Rand(8u)];
    const bool flags = !world.Chance(0.1);
    std::vector<uint32_t> expected(capacity + 8u, 0xA5A5A5A5u);
    std::vector<uint32_t> actual(capacity + 8u, 0xA5A5A5A5u);
    uint32_t expected_flags = 0x5A5A5A5Au;
    uint32_t actual_flags = 0x5A5A5A5Au;

    const SyntheticMemory& memory = world.memory;
    reference::g = &memory;
    uint64_t before = memory.guarded;
    uint64_t outside = memory.outside;
    const uint32_t expected_count = reference::RefiiHostGetEntityAnimState(
        entity, expected.data(), capacity, flags ? &expected_flags : nullptr);
    const uint64_t reference_reads = memory.guarded - before;
    totals.reference_outside += memory.outside - outside;
    const uint32_t expected_causes = Causes();

    before = memory.guarded;
    outside = memory.outside;
    const anim::AnimState state = anim::EntityAnimState(memory, entity, actual.data(), capacity, flags ? &actual_flags : nullptr);
    const uint64_t span_reads = memory.guarded - before;
    totals.span_outside += memory.outside - outside;

    ++totals.rigs;
    totals.reference_reads += reference_reads;
    totals.span_reads += span_reads;
    if (expected_count != 0u) {
        ++totals.posed;
        totals.leaves += expected_count;
        totals.reference_posed_reads += reference_reads;
        totals.span_posed_reads += span_reads;
    }

    if (expected_count != state.leaves) return Fail("leaf count", seed, entity, 0u, expected_count, state.leaves);
    if (expected_flags != actual_flags) return Fail("out flags", seed, entity, 0u, expected_flags, actual_flags);
    if (expected_causes != state.counts) return Fail("walk stats", seed, entity, 0u, expected_causes, state.counts);
    for (uint32_t i = 0; i < expected.size(); ++i) {
        if (expected[i] != actual[i]) return Fail("out word", seed, entity, i, expected[i], actual[i]);
    }
    return true;
}

bool CheckMask(World& world, uint32_t seed, uint32_t node, Totals& totals) {
    static constexpr uint32_t kCapacities[] = {0u, 1u, 7u, 64u, 1024u, 2000u};
    const uint32_t capacity = kCapacities[world.Rand(6u)];
    const bool skeleton = !world.Chance(0.2);
    const bool bytes = !world.Chance(0.05);
    std::vector<uint8_t> expected(capacity + 8u, 0xA5u);
    std::vector<uint8_t> actual(capacity + 8u, 0xA5u);
    uint32_t expected_id = 0x5A5A5A5Au;
    uint32_t actual_id = 0x5A5A5A5Au;

    const SyntheticMemory& memory = world.memory;
    reference::g = &memory;
    uint64_t before = memory.guarded;
    const uint32_t expected_count = reference::RefiiHostGetAnimMask(node, bytes ? expected.data() : nullptr, capacity,
                                                                    skeleton ? &expected_id : nullptr);
    totals.reference_mask_reads += memory.guarded - before;
    before = memory.guarded;
    const uint32_t actual_count =
        anim::AnimMask(memory, node, bytes ? actual.data() : nullptr, capacity, skeleton ? &actual_id : nullptr);
    totals.span_mask_reads += memory.guarded - before;
    ++totals.masks;

    if (expected_count != actual_count) return Fail("mask length", seed, node, 0u, expected_count, actual_count);
    if (expected_id != actual_id) return Fail("mask skeleton", seed, node, 0u, expected_id, actual_id);
    for (uint32_t i = 0; i < expected.size(); ++i) {
        if (expected[i] != actual[i]) return Fail("mask byte", seed, node, i, expected[i], actual[i]);
    }
    return true;
}

bool CheckWorld(World& world, uint32_t seed, Totals& totals) {
    for (const uint32_t entity : world.rigs) {
        if (!CheckRig(world, seed, entity, totals)) return false;
    }
    // Rig pointers that are no entity: implausible, unmapped, or inside another object.
    for (uint32_t i = 0; i < 8u; ++i) {
        if (!CheckRig(world, seed, world.Garbage(), totals)) return false;
    }
    for (const uint32_t node : world.masks) {
        if (!CheckMask(world, seed, node, totals)) return false;
    }
    for (uint32_t i = 0; i < 16u && !world.nodes.empty(); ++i) {
        const uint32_t node = world.Chance(0.5) ? world.nodes[world.Rand(static_cast<uint32_t>(world.nodes.size()))]
                                                : world.Garbage();
        if (!CheckMask(world, seed, node, totals)) return false;
    }
    return true;
}

/// Guarded reads for one plain rig, per-field then span: five components, a placement whose proxy
/// holds a ragdoll, two primaries each crossfading two clips, one clip overlay, timed ramps.
std::pair<uint64_t, uint64_t> TypicalRig() {
    SyntheticMemory m;
    uint32_t next = 0x50000000u;
    const auto alloc = [&](uint32_t size) {
        const uint32_t at = next;
        next += (size + 15u) & ~15u;
        for (uint32_t i = 0; i < size; ++i) m.Put8(at + i, 0u);
        return at;
    };
    m.Put8(anim::kCrossfadeOn, 1u);
    m.Put8(anim::kPartnerFromOne, 0u);
    const auto clip = [&](uint32_t key) {
        const uint32_t node = alloc(0x2Cu);
        m.PutF(node + 16u, 1.0f);
        m.Put32(node + 28u, key);
        m.PutF(node + 32u, 0.5f);
        m.PutF(node + 36u, 0.55f);
        m.Put8(node + 40u, 1u);
        return node;
    };
    const auto crossfade = [&](uint32_t a, uint32_t b, float f) {
        const uint32_t node = alloc(0x2Cu);
        m.PutF(node + 16u, 1.0f);
        m.Put32(node + 24u, 1u);
        m.Put32(node + 28u, a);
        m.Put32(node + 32u, b);
        m.PutF(node + 36u, f);
        return node;
    };
    const auto ramp = [&](uint32_t vtable) {
        const uint32_t r = alloc(12u);
        m.Put32(r, vtable);
        m.PutF(r + 4u, 0.25f);
        return r;
    };
    const auto record = [&](uint32_t priority, uint32_t track, float w, uint32_t root) {
        const uint32_t r = alloc(68u);
        m.Put32(r + 8u, priority);
        m.Put32(r + 12u, track);
        m.Put32(r + 16u, 0xFFFFFFFFu);
        for (uint32_t offset = 20u; offset <= 32u; offset += 4u) m.PutF(r + offset, w);
        m.Put32(r + 40u, priority * 7u);
        m.Put32(r + 44u, root);
        m.Put32(r + 52u, ramp(anim::kRampInTimed));
        m.Put32(r + 60u, ramp(anim::kRampOutTimed));
        return r;
    };
    const auto list = [&](uint32_t manager, uint32_t offset, std::vector<uint32_t> records) {
        const uint32_t sentinel = alloc(12u);
        std::vector<uint32_t> links{sentinel};
        for (const uint32_t r : records) {
            const uint32_t node = alloc(12u);
            m.Put32(node + 8u, r);
            links.push_back(node);
        }
        for (std::size_t i = 0; i < links.size(); ++i) {
            m.Put32(links[i], links[(i + 1u) % links.size()]);
            m.Put32(links[i] + 4u, links[(i + links.size() - 1u) % links.size()]);
        }
        m.Put32(manager + offset, sentinel);
        m.Put32(manager + offset + 4u, static_cast<uint32_t>(records.size()));
    };

    const uint32_t manager = alloc(704u);
    m.Put32(manager + 84u, 4u);
    list(manager, 28u,
         {record(1u, 1u, 0.4f, crossfade(clip(11u), clip(12u), 0.3f)),
          record(2u, 1u, 0.6f, crossfade(clip(13u), clip(14u), 0.7f))});
    list(manager, 40u, {record(3u, 2u, 0.5f, clip(15u))});

    const uint32_t ragdoll = alloc(160u);
    m.Put32(ragdoll, anim::kRagdollVtable);
    m.Put32(ragdoll + 0x40u, 1u);
    const uint32_t other = alloc(0x50u);
    m.Put32(other, 0x82007000u);
    const uint32_t pairs = alloc(16u);
    m.Put32(pairs, other);
    m.Put32(pairs + 8u, ragdoll);
    const uint32_t proxy = alloc(12u);
    m.Put32(proxy + 4u, pairs);
    m.Put32(proxy + 8u, pairs + 16u);
    const uint32_t vtable = alloc(0x80u);
    m.Put32(vtable + 0x70u, anim::kPhysicsProxyGetter);
    const uint32_t placement = alloc(0x40u);
    m.Put32(placement, vtable);
    m.Put32(placement + 0x20u, proxy);

    const uint32_t entity = alloc(0x98u);
    const uint32_t vector = alloc(40u);
    const uint32_t ids[] = {2u, 3u, 6u, 23u, 26u};
    for (uint32_t i = 0; i < 5u; ++i) {
        m.Put32(vector + 8u * i, ids[i]);
        m.Put32(vector + 8u * i + 4u, ids[i] == 23u ? manager : 0x60000000u + i * 0x100u);
    }
    m.Put32(entity + 0x54u, vector);
    m.Put32(entity + 0x58u, vector + 40u);
    m.Put32(entity + 0x80u, placement);

    uint32_t out[12u * 16u];
    uint32_t flags = 0u;
    reference::g = &m;
    uint64_t before = m.guarded;
    reference::RefiiHostGetEntityAnimState(entity, out, 12u * 16u, &flags);
    const uint64_t per_field = m.guarded - before;
    before = m.guarded;
    anim::EntityAnimState(m, entity, out, 12u * 16u, &flags);
    return {per_field, m.guarded - before};
}

}  // namespace

int main() {
    constexpr uint32_t kWorlds = 300u;
    Totals totals;
    for (uint32_t seed = 1; seed <= kWorlds; ++seed) {
        World world(seed * 2654435761u);
        world.Globals();
        const uint32_t rigs = 20u + world.Rand(30u);
        for (uint32_t i = 0; i < rigs; ++i) {
            const uint32_t roll = world.Rand(100u);
            if (roll < 60u) {
                world.rigs.push_back(world.Entity(true, world.Chance(0.2), 0));
            } else if (roll < 80u) {
                world.rigs.push_back(world.Entity(world.Chance(0.5), true, 0));
            } else if (roll < 92u) {
                world.rigs.push_back(world.Entity(false, false, 0));
            } else {
                world.rigs.push_back(world.Garbage());
            }
        }

        // Every world twice: all pages readable, then 1-7% of them decommitted (globals too), and a
        // quarter of the pages either side of an object across a page boundary.
        if (!CheckWorld(world, seed, totals)) return 1;
        static constexpr double kDecommit[] = {0.01, 0.03, 0.07};
        const double decommit = kDecommit[world.Rand(3u)];
        for (const auto& [page, bytes] : world.memory.pages) {
            if (world.Chance(decommit)) world.memory.unreadable.insert(page);
        }
        for (const uint32_t page : world.edges) {
            if (world.Chance(0.25)) world.memory.unreadable.insert(page);
        }
        if (!CheckWorld(world, seed, totals)) return 1;
    }
    const auto [typical_reference, typical_span] = TypicalRig();

    std::printf(
        "anim_state_test: %u worlds, %llu rigs (%llu posed, %llu leaves), %llu masks: span reads equal the per-field "
        "reads\n",
        kWorlds, static_cast<unsigned long long>(totals.rigs), static_cast<unsigned long long>(totals.posed),
        static_cast<unsigned long long>(totals.leaves), static_cast<unsigned long long>(totals.masks));
    std::printf(
        "anim_state_test: guarded reads per rig %.1f -> %.1f (posed rigs %.1f -> %.1f, per leaf %.2f -> %.2f); per mask "
        "%.1f -> %.1f; reads at no guest address %llu -> %llu\n",
        static_cast<double>(totals.reference_reads) / static_cast<double>(totals.rigs),
        static_cast<double>(totals.span_reads) / static_cast<double>(totals.rigs),
        static_cast<double>(totals.reference_posed_reads) / static_cast<double>(std::max<uint64_t>(totals.posed, 1u)),
        static_cast<double>(totals.span_posed_reads) / static_cast<double>(std::max<uint64_t>(totals.posed, 1u)),
        static_cast<double>(totals.reference_posed_reads) / static_cast<double>(std::max<uint64_t>(totals.leaves, 1u)),
        static_cast<double>(totals.span_posed_reads) / static_cast<double>(std::max<uint64_t>(totals.leaves, 1u)),
        static_cast<double>(totals.reference_mask_reads) / static_cast<double>(std::max<uint64_t>(totals.masks, 1u)),
        static_cast<double>(totals.span_mask_reads) / static_cast<double>(std::max<uint64_t>(totals.masks, 1u)),
        static_cast<unsigned long long>(totals.reference_outside), static_cast<unsigned long long>(totals.span_outside));
    std::printf("anim_state_test: a plain rig (2 crossfaded primaries, 1 overlay, ragdoll proxy): %llu -> %llu guarded reads\n",
                static_cast<unsigned long long>(typical_reference), static_cast<unsigned long long>(typical_span));
    return 0;
}
