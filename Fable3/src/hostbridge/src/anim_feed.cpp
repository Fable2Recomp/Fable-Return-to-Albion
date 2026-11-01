// refii - Fable 3's animation state, as the guest holds it: the clips each rig is playing, read
// out of its CECAnimationManager into the shell's clip-leaf layout (HostBridge.GetEntityAnimState),
// with the guest's own fold stated on them — which record is on top, which records it crossfades
// against and by how much, which overlays wrap it and at what share.
//
// Every offset is from the Fable 3 IDB, each against its own base (anim/CSA_SPEC.md and
// anim3/REPORT.md have the derivations):
//
//   component 23 = CECAnimationManager (factory sub_82543A20, 704 bytes, ctor sub_826BBDF8,
//             vtable 0x82003800). Found through the entity's {u32 id, component} vector at
//             entity+0x54/+0x58 as entity_feed.cpp finds every component.
//   mgr+28/+32  PRIMARY list sentinel / count, 12-byte nodes {next, prev, record}. sub_826BC820
//             inserts by priority (record+8) ascending, so the TAIL is the highest priority.
//             Primaries carry track 1 and mask = their own priority (`sub_8228FE70(r, &tree,
//             prio, 1, prio, in, out)` at 0x826bc93c).
//   mgr+40/+44  OVERLAY list sentinel / count (inserted by sub_826BC9D0 at the tail, then
//             list-sorted by track then priority, stable: the comparator in sub_826C4328
//             0x826c43d8..0x826c4410).
//   mgr+84    bone LOD tier: 4 = full (sub_823531E0 0x823538ec).
//   mgr+88/+92  the previous / current REPLICATED tree (a co-op client's remote rig); +96 / +100
//             their receipt times, +104 the time the current one arrived (crossfade start).
//   mgr+644   highest live primary priority (sub_82296448 0x822964fc).
//   record    68 bytes, ctor sub_8228FE70: +8 priority, +12 track, +16 priority bitmask, +20
//             weight current, +24 weight previous, +28/+32 the same pair copied at the end of
//             each step (sub_8220E630 0x8220f060..0x8220f090), +36 state (0 live, 1 retiring,
//             2 dead), +40 uid (mgr+52 counter), +44 RENDER tree root, +52 ramp-in, +60
//             ramp-out. A ramp (sub_8217EEF8 / sub_8228F868) holds +4 the step per 15 Hz tick,
//             `min(1/(seconds·15), 1)`, +8 the seconds; vtables 0x82001B2C (in) / 0x82001858
//             (out), and the instant ones 0x820019F4 / 0x820A7108.
//   THE STEP  sub_8220E630 (comp 23 vtable slot 72) per game tick: every record steps its OWN
//             ramp — ramp-in while state 0, ramp-out once retiring — `+24 = +20; +20 ±= step ·
//             timescale` clamped to [0, 1] (0x8227b740 / 0x82189568); a finished ramp settles
//             `+24 = +20` (ramp-in done above 0.9999, out below 1e-4; 0x8221e560 / 0x821839e0).
//             A retiring record under 0.05 and a dead one are unlinked. So the stated pair IS
//             the per-tick weight; nothing here re-derives it.
//   THE FOLD  sub_821E9AC0 -> sub_821EB068, every frame at the draw alpha α:
//             TOP = the tail record. `w = lerp(top+24, top+20, α)`. With the crossfade on
//             (byte_8329E3D7) and `|w − 1| ≥ 0.05`, sub_826BC358 builds the PARTNER: walking the
//             other records from the top down, `remaining = 1 − w` (1 when byte_83556685), each
//             takes `s = min(remaining, lerp(r+32, r+28, α))` while `|remaining| ≥ 0.05`, kept
//             when `|s| ≥ 0.05`; the kept trees form a weighted set normalised by Σs. The pose is
//             a crossfade node (sub_826B9AE0, kind 1) of partner (A) against top (B) by w, which
//             the evaluator prunes to one side when `min(w, 1−w) ≤ 0.05`. No partner → the top
//             alone at full weight.
//             Each primary's tree is wrapped by the overlays first (sub_821EADF0 -> sub_821CD100):
//             per track, the overlays passing `|lerp(o+24, o+20, α)| ≥ 0.05 && (o+16 & rec+8)`
//             (sub_826BB2C0); one passing → its tree at its weight; several → walking the group
//             from its tail, `s = min(remaining, w)` from remaining 1 while `|remaining| ≥ 0.05`,
//             a normalised set at Σs (sub_826BE298); the layer is a kind-3 node (sub_82198FD0).
//   render node  kind at +24, weight current +16 / previous +20 (sub_8222AEF8, 0x8222af8c;
//             `a30` = {0.05 cull, 0.5}, flt_8206C78C / flt_8206C788, set at 0x821e9e84..90):
//             0 clip:      +28 clip key, +32 previous time, +36 current time, +40 loop byte.
//             1 crossfade: +28 A, +32 B, +36 factor, +40 previous; one side alone when
//                          `min(f, 1−f) · importance ≤ 0.05` — B when f ≥ 0.5 (0x8222b4dc..b530).
//             2 set:       children in [+32, +36), each weighted by its own +16/+20; sorted
//                          heaviest first (sub_8228F9F8), the first always, the next while
//                          `w · importance > 0.05`, normalised by the running sum.
//             3 layer:     +32 base, +28 layer applied at the layer's own +16/+20 weight when
//                          `w · importance ≥ 0.05`; a clip layer is a whole-body blend (the clip
//                          delta byte, clip+72 via vtable slot 40, is only ever zeroed).
//             4 bone mask: +28 child, +32 skeleton id, [+40, +44) one byte per bone (sub_821C7B18
//                          writes `byte · flt_832A8930` into each pose element's mask lane).
//             5 difference: +32 pose minus +28 reference (sub_82D4E5D0), additive.
//   clip key  the FNV-1 of the clip's lowercased bank path (sub_82D3AD68 opens the VFS by it).
//
// Out words per leaf are Fable 2's REFII_ANIM_LEAF_WORDS (host_bridge.cpp, "Out words"): [0] clip
// key, [1] current time bits, [2] previous time bits, [3] weight bits, [4] flags (bit0 loop, bit1
// additive/layer, bit2 OVERLAY, bit3 times are a replica extrapolation, bit4 the guest's TOP,
// bit5 a member of the guest's PARTNER set, bits 8..15 priority, bits 16..23 track), [5] record
// uid, [6]/[7] ramp-in / ramp-out seconds, [8] the nearest bone-mask node above the leaf,
// [9] the record's priority bitmask, [10]/[11] the record's stated SHARE of the fold at the
// previous / current tick (see `EntityAnimState`).

#include <algorithm>
#include <atomic>
#include <cmath>
#include <cstdint>
#include <cstring>

#include "core/rex_payload_bridge.h"
#include "host_abi.h"
#include "host_core.h"

namespace {

using refii::hostcore::GuestByteAt;
using refii::hostcore::GuestWordAt;
using refii::hostcore::SafeKernelMemory;

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

}  // namespace

extern "C" {

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

/// Fable 2's aim and look post-ops (skeleton chains blended toward per-record 4-vectors after
/// each node) have no Fable 3 counterpart: Fable 3's evaluator (sub_8222AEF8) has six node kinds
/// and none of them is an aim or look op — its kind 4 is the bone mask above — and its record
/// (sub_8228FE70) carries no op pointers. Nothing to state.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetAnimPostOps(uint32_t /*entity*/, uint32_t* /*out_words*/,
                                                       uint32_t /*word_capacity*/) {
    return 0u;
}

/// Fable 2's five counters (host_bridge.cpp RefiiHostGetAnimWalkStats): [0] rigs without the
/// animation component (and no linked clip), [1] component bit set but unresolved (never, here:
/// the vector is walked), [2] managers found, [3] managers whose lists gave no leaf, [4] rigs posed
/// by a linked animation (component 215).
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetAnimWalkStats(uint32_t* out_words, uint32_t capacity) {
    if (out_words == nullptr || capacity < 5u) {
        return 0u;
    }
    out_words[0] = static_cast<uint32_t>(gNoManager.load(std::memory_order_relaxed));
    out_words[1] = 0u;
    out_words[2] = static_cast<uint32_t>(gManagerFound.load(std::memory_order_relaxed));
    out_words[3] = static_cast<uint32_t>(gEmptyList.load(std::memory_order_relaxed));
    out_words[4] = static_cast<uint32_t>(gLinked.load(std::memory_order_relaxed));
    return 5u;
}

}  // extern "C"
