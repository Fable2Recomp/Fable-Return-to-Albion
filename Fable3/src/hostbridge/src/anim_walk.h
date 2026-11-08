#pragma once

// refii - Fable 3's animation-state walk (anim_feed.cpp) over any guest reader (guest_span.h),
// each guest object's fields read as one span. anim_feed.cpp runs it on guest memory; the test
// (tests/anim_state_test.cpp) runs it on synthetic rigs against the per-field reads it replaced.
// anim_feed.cpp's header states what each field means; this one states each span's extent.
//
// Spans, each inside its object as the Fable 3 IDB (360 disc) allocates it:
//
//   CEntity, 0x98 bytes (`li r3,0x98` 0x823F1A10 / `bl sub_8221F300` 0x823F1A24). [0x54, 0x95):
//     +0x54/+0x58 component vector (`lwz r31,0x54(r3)` 0x823F3474, `lwz r10,0x58(r3)` 0x823F3478),
//     +0x80 placement (`lwz r3,0x80(r25)` 0x82353A18), +0x94 flags (`lbz r11,0x94(r3)` 0x823F346C).
//   Component pairs {u32 id, component}: the stated [begin, end) rounded up to whole pairs, at
//     most 128 — the bytes the per-pair scan reads (step `addi r31,r31,8`, sub_823F3458).
//   CECAnimationManager, 0x2C0 bytes: registered as component `li r3,0x17` 0x825586EC with size
//     `li r11,0x2C0` 0x825586F0; factory sub_82543A20 `li r3,0x2C0` 0x82543A34 / `bl` 0x82543A38,
//     ctor sub_826BBDF8. [28, 108): +28 primary sentinel `stw` 0x826BBE7C, +32 count `= 0`
//     0x826BBE6C, +40 overlay sentinel 0x826BBEA0, +44 count 0x826BBE90, +84 tier `= 4` 0x826BBED8,
//     +88/+92 replicated trees 0x826BBEE0 / 0x826BBEE4, +96..+104 receipt times. +644 (`a2[161]`,
//     sub_826BC820 0x826BC850) is read alone, only where the walk asks for it.
//   List node, 12 bytes: `sub_822A7180(12)` 0x826C350C (primary insert sub_826C34E8, stores +0/+4/+8
//     0x826C3520 / 0x826C3524 / 0x826C3534) and 0x826BCAD0 (overlay insert, 0x826BCAE0..0x826BCAF4);
//     the sentinels 0x826BBE70 / 0x826BBE94. [0, 12).
//   Record, 0x44 bytes: `li r3,0x44` 0x826BC884 / `bl sub_8221F300` 0x826BC888 (primary) and
//     0x826BCA5C / 0x826BCA60 (overlay); ctor sub_8228FE70 stores +8..+64 (0x8228FEAC..0x8228FEF8).
//     [8, 64).
//   Render node: every kind derives from one base (+16/+20 weights, +24 kind) and is allocated
//     `sub_822A7180(n)` with n by kind (sub_826BB330 / sub_826BB7E0 / sub_82D50298, the tree copies):
//       0 clip       0x2C  0x826BB36C (inline, kind store 0x826BB3CC); sub_8217F540 `sub_8221F300(44)`
//                          0x8217F558, +24 0x8217F5D0, +28 0x8217F5DC, +32/+36 0x8217F5E0/E4, +40 0x8217F5E8
//       1 crossfade  0x2C  0x826BB410, 0x821EB554, 0x82D504EC; Blend vf5 `sub_8221F300(44)` 0x828297AC;
//                          ctor sub_826B9AE0 stores +28..+40 (0x826B9B1C..0x826B9B78)
//       2 set        0x2C  0x826BB468 (kind 0x826BB4A8), 0x826BC5B0 (kind 0x826BC5E8, vector +32..+40
//                          0x826BC5F4..0x826BC5FC); sub_826BE298 `sub_8221F300(44)` 0x826BE2BC
//       3 layer      0x24  0x826BB594, 0x821CD2E0, 0x826BE198, 0x82D506FC; ctor sub_82198FD0 +28/+32
//       4 bone mask  0x38  0x826BB658, 0x82D50800; ctor sub_826B9DA8 +28, +32, vector at +36, +52
//       5 difference 0x24  0x826BB6E4, 0x82D508BC; ctor sub_826B9F10 +28/+32
//       6 (not walked) 0x30  0x826BE008, 0x82D509F0; ctor sub_826B9FB0
//     So [24, 36) lies inside every node; kinds 0, 1 and 2 also own [36, 44), kind 4 [36, 48).
//     The evaluator sub_8222AEF8 reads them: kind `lwz r11,0x18(r28)` 0x8222AF74, clip key
//     `lwz 0x1C` 0x8222AFA4, times `lfs 0x24` 0x8222AFE8 / `lfs 0x20` 0x8222AFF0, loop `lbz 0x28`
//     0x8222B028, crossfade factor `lfs 0x24` 0x8222B484, A/B `lwz 0x1C` 0x8222B538 / `lwz 0x20`
//     0x8222B55C, set vector `lwz 0x24` 0x8222B64C / `lwz 0x20` 0x8222B654, a child's weight
//     `lfs 0x10` 0x8222B718, the layer `lwz 0x1C` 0x8222B99C and its weight `lfs 0x10` 0x8222B9A8.
//     A child's +16 weight is read alone, before the child is visited.
//   Set children: the stated [begin, end) of node pointers, whole words, at most 64.
//   Bone-mask bytes: the stated [begin, end) of the kind-4 node's vector at +36 (sub_821C7B18:
//     `lwz r8,8(r31)` 0x821C7BB0 with r31 = node+32, `lbzx r8,r10,r8` 0x821C7BCC), at most 1024.
//   Ramps are 12 bytes timed (`sub_822A7180(12)` before sub_8217EEF8 / sub_8228F868) but 4 bytes
//     instant (`sub_822A7180(4)` 0x82290050, vtable only): +4 is read alone, only on a timed ramp.
//   Guest clock: dbl_83556BD0 (`lfd` 0x8220A3B4) then qword_83556BD8 (`ld` 0x8220A3A4), 16 bytes.
//   CECLinkedAnimation, 0x38 bytes (`li r3,0xD7` 0x8256878C, `li r11,0x38` 0x82568790; factory
//     sub_8254E4F0 `li r3,0x38` 0x8254E504). [16, 32): +16 the weak ref (ctor `= 0` 0x82711578),
//     +28 the map head (`stw` 0x827115B4).
//   Map node, 24 bytes: the head is `sub_822A7180(24)` 0x827115A8 with its colour/nil bytes +20/+21
//     0x827115DC / 0x827115E4; every node of the tree is that type. [0, 22).
//   Physics proxy: sub_8270CCF8 reads its vector `lwz r31,4(r3)` 0x8270CD04, `lwz r11,8(r3)`
//     0x8270CD0C, pairs {object, ref} `lwz r30,0(r31)` 0x8270CD20 / `lwz r11,4(r31)` 0x8270CD1C, step
//     `addi r31,r31,8` 0x8270CD78. [4, 12), and the pairs as one block.
//   Ragdoll (vtable 0x82006DE0, stored 0x82B066A4; ctor sub_82B06638 stores to +156): [0x40, 0x45),
//     mode `lwz r11,0x40(r11)` 0x82353A68 and flag `lbz r10,0x44(r11)` 0x82353A5C.
//   The placement (+0 vtable, the vtable's +0x70, +0x20), the object vtables in the proxy pairs,
//   the two option bytes and the list sentinels' own links stay single reads.
//
// Exactness. A span is copied a 4 KiB page at a time, so an unreadable page zeroes only its own
// bytes: what the per-field reads returned for naturally aligned fields. An object at an address
// that is not 4-aligned (never a real guest object) is read field by field instead, so even those
// decode as before. A field whose address is no guest address (Plausible) reads 0 as before, and
// a span never copies such addresses.

#include <algorithm>
#include <cmath>
#include <cstdint>
#include <cstring>

#include "guest_span.h"

namespace refii::fable3::anim {

using refii::hostcore::GuestBlockWord;

constexpr uint32_t kAnimationManager = 23u;
constexpr uint32_t kLinkedAnimation = 215u;
constexpr uint32_t kLeafWords = 12u;
constexpr uint32_t kMaxRecords = 64u;
constexpr uint32_t kMaxSetChildren = 64u;
constexpr uint32_t kMaxComponentPairs = 128u;
constexpr uint32_t kMaxProxyPairs = 64u;
constexpr uint32_t kMaxMaskBytes = 1024u;
constexpr uint32_t kFullTier = 4u;

/// The evaluator's cull, `a30[0]` (flt_8206C78C), and every 0.05 test of the fold.
constexpr float kCull = 0.05f;

/// byte_8329E3D7: the fold crossfades the top against a partner at all (sub_821EB068).
constexpr uint32_t kCrossfadeOn = 0x8329E3D7u;
/// byte_83556685: the partner walk starts from 1 instead of 1 − w (sub_826BC358 0x826bc3fc).
constexpr uint32_t kPartnerFromOne = 0x83556685u;
/// dbl_83556BD0 / qword_83556BD8: the guest clock `(mftb − base) / frequency` (sub_8220A380).
constexpr uint32_t kTimeFrequency = 0x83556BD0u;

constexpr uint32_t kRampInTimed = 0x82001B2Cu;
constexpr uint32_t kRampOutTimed = 0x82001858u;

/// The ragdoll's class (ctor sub_82B06638 stores it at 0x82b066a4); its slot 2 answers 1, the
/// test sub_8270CCF8 makes.
constexpr uint32_t kRagdollVtable = 0x82006DE0u;
/// `lwz r3, 0x20(r3); blr` — slot 0x70 of every non-simple placement class (0x82007DF8,
/// 0x820079C0, 0x82007FA8, 0x82007C48, 0x82007ED0, ...); the simple ones answer 0 (sub_82CE17A8).
constexpr uint32_t kPhysicsProxyGetter = 0x82E78F88u;

constexpr uint32_t kFirstGuest = 0x10000u;
constexpr uint32_t kGuestCeiling = 0xC0000000u;

inline bool Plausible(uint32_t address) { return address >= kFirstGuest && address < kGuestCeiling; }

inline uint32_t Bits(float value) {
    uint32_t bits;
    std::memcpy(&bits, &value, sizeof(bits));
    return bits;
}

inline float FloatOf(uint32_t bits) {
    float value;
    std::memcpy(&value, &bits, sizeof(value));
    return value;
}

inline float Finite(float value) { return std::isfinite(value) ? value : 0.0f; }

/// A word at `address`, 0 for an address no guest object lies at.
template <typename Memory>
uint32_t WordAt(const Memory& memory, uint32_t address) {
    return Plausible(address) ? memory.Word(address) : 0u;
}

template <typename Memory>
uint8_t ByteAt(const Memory& memory, uint32_t address) {
    return Plausible(address) ? memory.Byte(address) : 0u;
}

template <typename Memory>
float FloatAt(const Memory& memory, uint32_t address) {
    return FloatOf(WordAt(memory, address));
}

/// The part of [address, address + size) that is guest addresses, copied; the rest zero.
template <typename Memory>
void CopyPlausible(const Memory& memory, uint32_t address, uint8_t* out, uint32_t size) {
    std::memset(out, 0, size);
    const uint64_t first = std::max<uint64_t>(address, kFirstGuest);
    const uint64_t end = std::min<uint64_t>(uint64_t{address} + size, kGuestCeiling);
    if (first < end) {
        memory.Copy(static_cast<uint32_t>(first), out + (first - address), static_cast<std::size_t>(end - first));
    }
}

/// Bytes [First, End) of the guest object at `object`, addressed by the object's own offsets.
/// A field reads exactly what `WordAt` / `ByteAt` on its address did.
template <typename Memory, uint32_t First, uint32_t End>
class Fields {
public:
    static_assert(First < End, "an empty span");

    Fields(const Memory& memory, uint32_t object)
        : memory_(&memory), object_(object), block_((object & 3u) == 0u) {
        if (block_) {
            CopyPlausible(memory, object + First, bytes_, End - First);
        }
    }

    uint32_t Word(uint32_t offset) const {
        const uint32_t address = object_ + offset;
        if (!Plausible(address)) {
            return 0u;
        }
        return block_ ? GuestBlockWord(bytes_ + (offset - First), 0u) : memory_->Word(address);
    }

    uint8_t Byte(uint32_t offset) const {
        const uint32_t address = object_ + offset;
        if (!Plausible(address)) {
            return 0u;
        }
        return block_ ? bytes_[offset - First] : memory_->Byte(address);
    }

    float Float(uint32_t offset) const { return FloatOf(Word(offset)); }

private:
    const Memory* memory_;
    uint32_t object_;
    bool block_;
    uint8_t bytes_[End - First];
};

template <typename Memory>
using EntitySpan = Fields<Memory, 0x54u, 0x95u>;
template <typename Memory>
using ManagerSpan = Fields<Memory, 28u, 108u>;
template <typename Memory>
using ListNodeSpan = Fields<Memory, 0u, 12u>;
template <typename Memory>
using RecordSpan = Fields<Memory, 8u, 64u>;
/// Inside every render node.
template <typename Memory>
using NodeHead = Fields<Memory, 24u, 36u>;
/// Inside a clip, crossfade or set node (kinds 0, 1, 2).
template <typename Memory>
using NodeTail = Fields<Memory, 36u, 44u>;
/// Inside a bone-mask node (kind 4).
template <typename Memory>
using MaskTail = Fields<Memory, 36u, 48u>;
/// A clip node (kind 0) known to be one.
template <typename Memory>
using ClipSpan = Fields<Memory, 28u, 40u>;
template <typename Memory>
using ClockSpan = Fields<Memory, 0u, 16u>;
template <typename Memory>
using LinkedSpan = Fields<Memory, 16u, 32u>;
template <typename Memory>
using MapNodeSpan = Fields<Memory, 0u, 22u>;
template <typename Memory>
using ProxySpan = Fields<Memory, 4u, 12u>;
template <typename Memory>
using RagdollSpan = Fields<Memory, 0x40u, 0x45u>;

/// `count` words at `address`, each as one read of it gave it: `checked` reads each through
/// `WordAt` (a word at no guest address is 0), unchecked as a bare read.
template <typename Memory>
void ReadWords(const Memory& memory, uint32_t address, uint32_t* out, uint32_t count, bool checked) {
    if ((address & 3u) != 0u) {
        for (uint32_t i = 0; i < count; ++i) {
            const uint32_t at = address + (i * 4u);
            out[i] = checked ? WordAt(memory, at) : memory.Word(at);
        }
        return;
    }
    if (checked) {
        CopyPlausible(memory, address, reinterpret_cast<uint8_t*>(out), count * 4u);
    } else {
        memory.Copy(address, out, count * 4u);
    }
    for (uint32_t i = 0; i < count; ++i) {
        out[i] = GuestBlockWord(out, i);
    }
}

/// The entity's component vector, read once; Find answers as the per-pair scan did: the first
/// pair with the id, its component when plausible.
struct Components {
    uint32_t words[2u * kMaxComponentPairs];
    uint32_t pairs = 0u;

    template <typename Memory>
    Components(const Memory& memory, const EntitySpan<Memory>& entity) {
        const uint32_t first = entity.Word(0x54u);
        const uint32_t last = entity.Word(0x58u);
        if (!Plausible(first) || last < first || last - first > 8u * kMaxComponentPairs) {
            return;
        }
        pairs = (last - first + 7u) / 8u;
        ReadWords(memory, first, words, pairs * 2u, false);
    }

    uint32_t Find(uint32_t id) const {
        for (uint32_t pair = 0; pair < pairs; ++pair) {
            if (words[pair * 2u] == id) {
                const uint32_t component = words[(pair * 2u) + 1u];
                return Plausible(component) ? component : 0u;
            }
        }
        return 0u;
    }
};

/// The guest clock in seconds, as sub_8220A380 reads it: `(mftb − qword_83556BD8) /
/// dbl_83556BD0`; `memory.GuestTicks()` is the recompiled module's `mftb`.
template <typename Memory>
bool GuestSeconds(const Memory& memory, double* out) {
    const ClockSpan<Memory> clock(memory, kTimeFrequency);
    const uint64_t time_base = (static_cast<uint64_t>(clock.Word(8u)) << 32) | clock.Word(12u);
    const uint64_t frequency_bits = (static_cast<uint64_t>(clock.Word(0u)) << 32) | clock.Word(4u);
    double frequency;
    std::memcpy(&frequency, &frequency_bits, sizeof(frequency));
    if (!std::isfinite(frequency) || frequency <= 0.0) {
        return false;
    }
    const int64_t ticks = static_cast<int64_t>(memory.GuestTicks() - time_base);
    *out = static_cast<double>(ticks) / frequency;
    return std::isfinite(*out);
}

/// A ramp's length in seconds: `1/(step·15)` for a timed ramp (step at +4), 0 for an instant one.
template <typename Memory>
uint32_t RampSecondsBits(const Memory& memory, uint32_t ramp, uint32_t timed_vtable) {
    if (!Plausible(ramp) || WordAt(memory, ramp) != timed_vtable) {
        return 0u;
    }
    const float step = FloatAt(memory, ramp + 4u);
    if (!(step > 1e-6f) || !(step < 1.0f)) {
        return 0u;
    }
    return Bits(1.0f / (step * 15.0f));
}

/// Whether the guest's physics owns this entity's bones (anim_feed.cpp states the contract).
template <typename Memory>
bool PhysicsOwnsPose(const Memory& memory, const EntitySpan<Memory>& entity) {
    const uint32_t placement = entity.Word(0x80u);
    const uint32_t vtable = WordAt(memory, placement);
    if (!Plausible(vtable) || WordAt(memory, vtable + 0x70u) != kPhysicsProxyGetter) {
        return false;
    }
    const uint32_t proxy = WordAt(memory, placement + 0x20u);
    const ProxySpan<Memory> vector(memory, proxy);
    const uint32_t begin = vector.Word(4u);
    const uint32_t end = vector.Word(8u);
    if (!Plausible(begin) || end < begin || end - begin > 8u * kMaxProxyPairs) {
        return false;
    }
    uint32_t pairs[2u * kMaxProxyPairs];
    const uint32_t count = (end - begin) / 8u;
    ReadWords(memory, begin, pairs, count * 2u, true);
    for (uint32_t i = 0; i < count; ++i) {
        const uint32_t object = pairs[i * 2u];
        if (WordAt(memory, object) != kRagdollVtable) {
            continue;
        }
        const RagdollSpan<Memory> ragdoll(memory, object);
        const uint32_t mode = ragdoll.Word(0x40u);
        return ragdoll.Byte(0x44u) != 0u && (mode == 3u || mode == 4u);
    }
    return false;
}

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
template <typename Memory>
struct Flattener {
    const Memory& memory;
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

    void Emit(const Frame& frame, const NodeHead<Memory>& head) {
        if (leaves >= capacity) {
            return;
        }
        const NodeTail<Memory> tail(memory, frame.node);
        uint32_t* leaf = out + leaves * kLeafWords;
        leaf[0] = head.Word(28u);
        leaf[1] = tail.Word(36u);
        leaf[2] = head.Word(32u);
        if (stated.replica) {
            // `(1 − α)·+32 + α·+36` at the receipt alpha, the previous value one graph step back
            // so consecutive folds join (Fable 2's replica contract).
            const float previous = head.Float(32u);
            const float current = tail.Float(36u);
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
        leaf[4] = (tail.Byte(40u) != 0u ? 1u : 0u) | (frame.additive ? 2u : 0u) | (stated.replica ? 8u : 0u) |
                  stated.role | ((stated.priority & 0xFFu) << 8) | ((stated.track & 0xFFu) << 16);
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
            const NodeHead<Memory> head(memory, frame.node);
            switch (head.Word(24u)) {
                case 0u:
                    Emit(frame, head);
                    break;
                case 1u: {
                    const NodeTail<Memory> tail(memory, frame.node);
                    const float f = Finite(tail.Float(36u));
                    const uint32_t a = head.Word(28u);
                    const uint32_t b = head.Word(32u);
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
                    const NodeTail<Memory> tail(memory, frame.node);
                    const uint32_t begin = head.Word(32u);
                    const uint32_t end = tail.Word(36u);
                    if (!Plausible(begin) || end < begin || end - begin > 4u * kMaxSetChildren) {
                        break;
                    }
                    uint32_t child[kMaxSetChildren];
                    float w[kMaxSetChildren];
                    const uint32_t count = (end - begin) / 4u;
                    ReadWords(memory, begin, child, count, true);
                    for (uint32_t i = 0; i < count; ++i) {
                        w[i] = Finite(FloatAt(memory, child[i] + 16u));
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
                    push(head.Word(32u), frame.weight, frame.importance, frame.additive, frame.mask);
                    const uint32_t layer = head.Word(28u);
                    const float lw = Finite(FloatAt(memory, layer + 16u));
                    if (Plausible(layer) && lw * frame.importance >= kCull) {
                        push(layer, frame.weight * lw, frame.importance * lw, true, frame.mask);
                    }
                    break;
                }
                case 4u:
                    push(head.Word(28u), frame.weight, frame.importance, frame.additive, frame.node);
                    break;
                case 5u:
                    push(head.Word(32u), frame.weight, frame.importance, true, frame.mask);
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

template <typename Memory>
uint32_t ReadList(const Memory& memory, const ManagerSpan<Memory>& manager, uint32_t sentinel_offset, Record* out) {
    const uint32_t sentinel = manager.Word(sentinel_offset);
    const uint32_t count = manager.Word(sentinel_offset + 4u);
    if (!Plausible(sentinel) || count == 0u || count > kMaxRecords) {
        return 0u;
    }
    uint32_t read = 0;
    uint32_t node = WordAt(memory, sentinel);
    for (uint32_t i = 0; i < count && Plausible(node) && node != sentinel; ++i) {
        const ListNodeSpan<Memory> link(memory, node);
        const uint32_t address = link.Word(8u);
        node = link.Word(0u);
        if (!Plausible(address)) {
            continue;
        }
        const RecordSpan<Memory> fields(memory, address);
        Record& r = out[read++];
        r.address = address;
        r.priority = fields.Word(8u);
        r.track = fields.Word(12u);
        r.mask = fields.Word(16u);
        r.weight_cur = Finite(fields.Float(20u));
        r.weight_prev = Finite(fields.Float(24u));
        r.pair_cur = Finite(fields.Float(28u));
        r.pair_prev = Finite(fields.Float(32u));
        r.uid = fields.Word(40u);
        r.root = fields.Word(44u);
        r.ramp_in = fields.Word(52u);
        r.ramp_out = fields.Word(60u);
    }
    return read;
}

/// sub_826BC358 at one end of the tick (`current` = α 1, else α 0): each non-top primary's
/// share of the partner set, normalised; false when the guest builds no partner there.
inline bool PartnerShares(const Record* records, uint32_t count, float top_weight, bool from_one, bool current,
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
/// in list order needs (anim_feed.cpp states the arithmetic).
inline void OverlayFactors(const Record* overlays, uint32_t count, uint32_t priority, bool current, float* factors) {
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

/// THE CLIP A LINKED ANIMATION PLAYS — component 215 (anim_feed.cpp states the contract) — as one
/// leaf, or 0 when the guest poses nothing. `components` are the rig's own.
template <typename Memory>
uint32_t LinkedAnimLeaf(const Memory& memory, const Components& components, uint32_t* out) {
    const uint32_t linked = components.Find(kLinkedAnimation);
    if (linked == 0u) {
        return 0u;
    }
    const LinkedSpan<Memory> link(memory, linked);
    const uint32_t control = link.Word(16u);
    const uint32_t source = WordAt(memory, control);
    if (!Plausible(source)) {
        return 0u;
    }
    const EntitySpan<Memory> source_fields(memory, source);
    if ((source_fields.Byte(0x94u) & 0x40u) == 0u) {
        return 0u;
    }
    const uint32_t manager = Components(memory, source_fields).Find(kAnimationManager);
    if (manager == 0u) {
        return 0u;
    }

    // sub_821FB610: 0 the leaf; 1 B (+32) when +36 ≥ 0.5 else A (+28); 3 and 4 take +28; 5 +32;
    // a set has no dominant leaf.
    const auto dominant = [&](uint32_t node) -> uint32_t {
        for (int depth = 0; depth < 32 && Plausible(node); ++depth) {
            const NodeHead<Memory> head(memory, node);
            switch (head.Word(24u)) {
                case 0u:
                    return node;
                case 1u:
                    node = head.Word(NodeTail<Memory>(memory, node).Float(36u) >= 0.5f ? 32u : 28u);
                    break;
                case 3u:
                case 4u:
                    node = head.Word(28u);
                    break;
                case 5u:
                    node = head.Word(32u);
                    break;
                default:
                    return 0u;
            }
        }
        return 0u;
    };

    const ManagerSpan<Memory> fields(memory, manager);
    uint32_t leaf = 0u;
    double now = 0.0;
    const uint32_t tree = fields.Word(92u);
    const bool replica = Plausible(tree);
    if (replica) {
        leaf = dominant(tree);
        if (leaf == 0u || !GuestSeconds(memory, &now)) {
            return 0u;
        }
    } else {
        const uint32_t sentinel = fields.Word(28u);
        const uint32_t tail = WordAt(memory, sentinel + 4u);
        if (!Plausible(sentinel) || tail == sentinel) {
            return 0u;
        }
        leaf = dominant(WordAt(memory, WordAt(memory, tail + 8u) + 44u));
        if (leaf == 0u) {
            return 0u;
        }
    }

    // `dominant` answers only a kind-0 node, which owns [28, 40).
    const ClipSpan<Memory> clip(memory, leaf);
    float time = clip.Float(36u);
    float previous = clip.Float(32u);
    if (replica) {
        const float current = time;
        float step = current - previous;
        if (!(step > 0.0f) || !std::isfinite(step)) {
            step = 1.0f / 15.0f;
        }
        time = static_cast<float>(now - fields.Float(100u)) + current;
        previous = time - step;
    }
    if (!std::isfinite(time) || !std::isfinite(previous)) {
        return 0u;
    }

    const uint32_t source_key = clip.Word(28u);
    const uint32_t head = link.Word(28u);
    if (!Plausible(head)) {
        return 0u;
    }
    uint32_t found = head;
    uint32_t found_key = 0u;
    uint32_t found_value = 0u;
    uint32_t at = WordAt(memory, head + 4u);
    for (int steps = 0; steps < 64 && Plausible(at); ++steps) {
        const MapNodeSpan<Memory> node(memory, at);
        if (node.Byte(21u) != 0u) {
            break;
        }
        if (node.Word(12u) >= source_key) {
            found = at;
            found_key = node.Word(12u);
            found_value = node.Word(16u);
            at = node.Word(0u);
        } else {
            at = node.Word(8u);
        }
    }
    if (found == head || source_key < found_key) {
        return 0u;
    }
    const uint32_t key = found_value;
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

/// The replicated tree as sub_826BDEC8 folds it (anim_feed.cpp states the contract).
template <typename Memory>
void WalkReplica(const Memory& memory, const ManagerSpan<Memory>& manager, Flattener<Memory>& flat) {
    double now = 0.0;
    const bool clock = GuestSeconds(memory, &now);
    const auto alpha_since = [&](uint32_t receipt_offset) -> float {
        if (!clock) {
            return 1.0f;
        }
        const double alpha = (now - manager.Float(receipt_offset)) * 15.0;
        // A minute of extrapolation is a tree the host stopped sending, not a clip to run on.
        return std::isfinite(alpha) && alpha > -1.0 && alpha < 15.0 * 60.0 ? static_cast<float>(alpha) : 1.0f;
    };

    float fade = 1.0f;
    const uint32_t previous = manager.Word(88u);
    if (Plausible(previous) && clock) {
        fade = std::clamp(static_cast<float>((now - manager.Float(104u)) * 13.33333280351429), 0.0f, 1.0f);
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
        flat.Walk(manager.Word(92u), fade, fade);
    } else {
        flat.stated.replica_alpha = alpha_since(fade < 0.5f ? 96u : 100u);
        flat.Walk(fade < 0.5f ? previous : manager.Word(92u), 1.0f, 1.0f);
    }
}

/// What a rig's walk adds to RefiiHostGetAnimWalkStats.
enum WalkCount : uint32_t {
    kCountNoManager = 1u,
    kCountManagerFound = 2u,
    kCountEmptyList = 4u,
    kCountLinked = 8u,
};

struct AnimState {
    uint32_t leaves = 0u;
    uint32_t counts = 0u;  // WalkCount bits
};

/// RefiiHostGetEntityAnimState's walk (anim_feed.cpp states the contract and the out words).
template <typename Memory>
AnimState EntityAnimState(const Memory& memory, uint32_t entity, uint32_t* out_words, uint32_t word_capacity,
                          uint32_t* out_flags) {
    AnimState state;
    if (out_flags != nullptr) {
        *out_flags = 0u;
    }
    if (out_words == nullptr || word_capacity < kLeafWords || !Plausible(entity)) {
        return state;
    }

    const EntitySpan<Memory> entity_fields(memory, entity);
    uint32_t flags = PhysicsOwnsPose(memory, entity_fields) ? 8u : 0u;
    const Components components(memory, entity_fields);
    const uint32_t manager = components.Find(kAnimationManager);
    const ManagerSpan<Memory> fields(memory, manager);
    const uint32_t primary_count = manager == 0u ? 0u : fields.Word(32u);
    const uint32_t replica = manager == 0u ? 0u : fields.Word(92u);

    // THE GATE'S CHOICE OF SOURCE (sub_823531E0 0x82353c30..0x82353e8c): component 23 with records
    // or a replicated tree poses the rig; otherwise component 215 does, when present.
    if (manager == 0u || (primary_count == 0u && !Plausible(replica))) {
        const uint32_t linked = LinkedAnimLeaf(memory, components, out_words);
        if (out_flags != nullptr) {
            *out_flags = flags;
        }
        if (linked != 0u) {
            state.counts = kCountLinked;
            state.leaves = linked;
            return state;
        }
        state.counts = manager == 0u ? kCountNoManager : (kCountManagerFound | kCountEmptyList);
        return state;
    }
    state.counts = kCountManagerFound;

    if (fields.Word(84u) == kFullTier) {
        flags |= 1u;
    }
    if (out_flags != nullptr) {
        *out_flags = flags;
    }

    Flattener<Memory> flat{memory, out_words, word_capacity / kLeafWords, 0u, Stated{}};

    // THE GUEST'S OWN CHOICE OF ROOT (sub_821E9AC0 0x821e9af8, sub_82208F50 0x82208f88): the
    // replicated tree when it exists and the primary list is empty or holds nothing at 128.
    if (Plausible(replica) && (primary_count == 0u || static_cast<int32_t>(WordAt(memory, manager + 644u)) < 128)) {
        WalkReplica(memory, fields, flat);
        if (flat.leaves == 0u) {
            state.counts |= kCountEmptyList;
        }
        state.leaves = flat.leaves;
        return state;
    }

    Record primaries[kMaxRecords];
    Record overlays[kMaxRecords];
    const uint32_t count = ReadList(memory, fields, 28u, primaries);
    const uint32_t overlay_count = ReadList(memory, fields, 40u, overlays);
    if (count == 0u) {
        state.counts |= kCountEmptyList;
        return state;
    }

    // TOP = the tail (sub_821EB068: `sentinel->prev`), whatever its state.
    const Record& top = primaries[count - 1u];
    const bool crossfade = ByteAt(memory, kCrossfadeOn) != 0u;
    const bool from_one = ByteAt(memory, kPartnerFromOne) != 0u;
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
        s.ramp_in_bits = RampSecondsBits(memory, r.ramp_in, kRampInTimed);
        s.ramp_out_bits = RampSecondsBits(memory, r.ramp_out, kRampOutTimed);
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
        state.counts |= kCountEmptyList;
    }
    state.leaves = flat.leaves;
    return state;
}

/// RefiiHostGetAnimMask's read: the kind-4 node's bytes, its skeleton id when asked.
template <typename Memory>
uint32_t AnimMask(const Memory& memory, uint32_t node, uint8_t* out_bytes, uint32_t capacity, uint32_t* out_skeleton_id) {
    if (out_skeleton_id != nullptr) {
        *out_skeleton_id = 0u;
    }
    if (out_bytes == nullptr || capacity == 0u || !Plausible(node)) {
        return 0u;
    }
    const NodeHead<Memory> head(memory, node);
    if (head.Word(24u) != 4u) {
        return 0u;
    }
    const MaskTail<Memory> tail(memory, node);
    const uint32_t begin = tail.Word(40u);
    const uint32_t end = tail.Word(44u);
    if (!Plausible(begin) || end < begin || end - begin > kMaxMaskBytes) {
        return 0u;
    }
    if (out_skeleton_id != nullptr) {
        *out_skeleton_id = head.Word(32u);
    }
    const uint32_t written = std::min(end - begin, capacity);
    memory.Copy(begin, out_bytes, written);
    return written;
}

}  // namespace refii::fable3::anim
