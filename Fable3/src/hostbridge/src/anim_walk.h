#pragma once

// refii - Fable 3's animation-state walk (anim_feed.cpp) over any guest reader (guest_span.h),
// each guest object's fields read as one span. anim_feed.cpp runs it on guest memory; the test
// (tests/anim_state_test.cpp) runs it on synthetic rigs against the per-field reads it replaced.
// anim_feed.cpp's header states what each field means; this one states each span's extent.
//
// Spans, each inside its object as the Fable 3 IDBs allocate it (TU3 addresses unless marked disc):
//
//   CEntity, 0x98 bytes (`li r3,0x98` disc 0x823F1A10 / `bl sub_8221E3B0` disc 0x823F1A24). [0x54, 0x95):
//     +0x54/+0x58 component vector (`lwz r31,0x54(r3)` 0x824157E4, `lwz r10,0x58(r3)` 0x824157E8),
//     +0x80 placement (`lwz r3,0x80(r25)` 0x8235BE98), +0x94 flags (`lbz r11,0x94(r3)` 0x824157DC).
//   Component pairs {u32 id, component}: the stated [begin, end) rounded up to whole pairs, at
//     most 128 — the bytes the per-pair scan reads (step `addi r31,r31,8`, sub_824157C8).
//   CECAnimationManager, 0x2D0 bytes in TU3 (`li r11,0x2D0` 0x82589550), 0x2C0 on the disc (`li r11,0x2C0`
//     disc 0x825586F0); registered as component `li r3,0x17` (0x8258954C / disc 0x825586EC), ctor
//     sub_826E74C8. [28, 108): +28 primary sentinel `stw` 0x826E754C, +32 count `= 0` 0x826E753C,
//     +40 overlay sentinel 0x826E7570, +44 count 0x826E7560, +84 tier `stw r9,0x54(r31)` 0x826E75A8
//     (disc 0x826BBED8) — the same in both builds. From +88 the builds differ (CurrentTreeOffset below):
//     TU3 stores +88/+92/+96 (`stw r30,0x58/0x5C/0x60(r31)` 0x826E75B0/B4/B8), the disc +88/+92
//     (`stw r30,0x58/0x5C(r31)` disc 0x826BBEE0/E4). The highest primary priority, +652 in TU3
//     (`stw r6,0x28C(r4)` 0x826E7FD4) and +644 on the disc (`a2[161]`, disc 0x826BC854), is read
//     alone, only on the disc, only where the walk asks for it.
//   List node, 12 bytes: `sub_8221E3C8(12)` 0x826F08B4 (primary insert sub_826F0890, stores +0/+4/+8
//     0x826F08C8 / 0x826F08CC / 0x826F08DC) and 0x826E8250 (overlay insert, 0x826E8260..0x826E8274);
//     the sentinels disc 0x826BBE70 / 0x826E7564. [0, 12).
//   Record, 0x48 bytes in TU3 (`li r3,0x48` 0x826E8004, the byte +0x44 added), 0x44 on the disc:
//     `li r3,0x44` disc 0x826BC884 / `bl sub_8221E3B0` disc 0x826BC888 (primary) and
//     disc 0x826BCA5C / disc 0x826BCA60 (overlay); ctor sub_8228E178 stores +8..+64 (disc 0x8228FEAC..disc 0x8228FEF8).
//     [8, 64).
//   Render node: every kind derives from one base (+16/+20 weights, +24 kind) and is allocated
//     `sub_8221E3C8(n)` with n by kind (sub_826E6A00 / sub_826E6EB0 / sub_82D82800, the tree copies):
//       0 clip       0x2C  0x826E6A3C (inline, kind store 0x826E6A9C); sub_8217F448 `sub_8221E3B0(44)`
//                          0x8217F460, +24 0x8217F4D8, +28 0x8217F4E4, +32/+36 0x8217F4E8/E4, +40 0x8217F4F0
//       1 crossfade  0x2C  0x826E6AE0, disc 0x821EB554, 0x82D82A54; Blend vf5 `sub_8221E3B0(44)` 0x8288763C;
//                          ctor sub_826E5090 stores +28..+40 (0x826E50CC..0x826E5128)
//       2 set        0x2C  0x826E6B38 (kind 0x826E6B78), 0x826E7D30 (kind 0x826E7D68, vector +32..+40
//                          0x826E7D74..0x826E7D7C); sub_826EA518 `sub_8221E3B0(44)` 0x826EA53C
//       3 layer      0x24  0x826E6C64, 0x821CD3E8, disc 0x826BE198, 0x82D82C64; ctor sub_821990E0 +28/+32
//       4 bone mask  0x38  0x826E6D28, 0x82D82D68; ctor sub_826E53A8 +28, +32, vector at +36, +52
//       5 difference 0x24  0x826E6DB4, 0x82D82E24; ctor sub_826E5510 +28/+32
//       6 replica crossfade 0x30  `li r3,0x30` 0x826EA400, disc 0x826BE008, 0x82D82F58; ctor sub_826E55B0
//                          stores +28 A, +32 B, +36 factor, +40/+44 the alphas (0x826E55F0..0x826E5650)
//       7 replica    0x24  TU3 only: `li r3,0x24` 0x826EA2C8; ctor sub_826E5658 `li r6,7` 0x826E5670,
//                          +28 child `stw r11,0x1C(r3)` 0x826E5698, +32 alpha `stfs f1,0x20(r3)` 0x826E56C0
//     So [24, 36) lies inside every node; kinds 0, 1 and 2 also own [36, 44), kinds 4 and 6 [36, 48).
//     The evaluator sub_82229C90 reads them: kind `lwz r11,0x18(r28)` disc 0x8222AF74, clip key
//     `lwz 0x1C` disc 0x8222AFA4, times `lfs 0x24` disc 0x8222AFE8 / `lfs 0x20` disc 0x8222AFF0, loop `lbz 0x28`
//     disc 0x8222B028, crossfade factor `lfs 0x24` disc 0x8222B484, A/B `lwz 0x1C` disc 0x8222B538 / `lwz 0x20`
//     disc 0x8222B55C, set vector `lwz 0x24` disc 0x8222B64C / `lwz 0x20` disc 0x8222B654, a child's weight
//     `lfs 0x10` 0x8222A4BC, the layer `lwz 0x1C` disc 0x8222B99C and its weight `lfs 0x10` 0x8222A74C.
//     A child's +16 weight is read alone, before the child is visited.
//   Set children: the stated [begin, end) of node pointers, whole words, at most 64.
//   Bone-mask bytes: the stated [begin, end) of the kind-4 node's vector at +36 (sub_821C7B78:
//     `lwz r8,8(r31)` 0x821C7C10 with r31 = node+32, `lbzx r8,r10,r8` 0x821C7C2C), at most 1024.
//   Ramps are 12 bytes timed (`sub_8221E3C8(12)` before sub_8217EE18 / sub_8228DBF0) but 4 bytes
//     instant (`sub_8221E3C8(4)` 0x8228E35C, vtable only): +4 is read alone, only on a timed ramp.
//   Guest clock: dbl_835976D0 (`lfd` 0x822099F4) then qword_835976D8 (`ld` 0x822099E4), 16 bytes.
//   CECLinkedAnimation, 0x38 bytes (`li r3,0xD7` 0x82599584, `li r11,0x38` 0x82599588; factory
//     sub_8257F350 `li r3,0x38` 0x8257F364). [16, 32): +16 the weak ref (ctor `= 0` 0x82745A18),
//     +28 the map head (`stw` 0x82745A54).
//   Map node, 24 bytes: the head is `sub_8221E3C8(24)` 0x82745A48 with its colour/nil bytes +20/+21
//     0x82745A7C / 0x82745A84; every node of the tree is that type. [0, 22).
//   Physics proxy: sub_82740F90 reads its vector `lwz r31,4(r3)` 0x82740F9C, `lwz r11,8(r3)`
//     0x82740FA4, pairs {object, ref} `lwz r30,0(r31)` 0x82740FB8 / `lwz r11,4(r31)` 0x82740FB4, step
//     `addi r31,r31,8` 0x82741010. [4, 12), and the pairs as one block.
//   Ragdoll (vtable 0x82006D00, stored 0x82B449A4; ctor sub_82B44938 stores to +156): [0x40, 0x45),
//     mode `lwz r11,0x40(r11)` 0x8235BEE8 and flag `lbz r10,0x44(r11)` 0x8235BEDC.
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
#include "../../vfs/include/fable3_guest_build.h"

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

/// The evaluator's cull, `a30[0]` (flt_8206C74C), and every 0.05 test of the fold.
constexpr float kCull = 0.05f;

/// byte_832DE527: the fold crossfades the top against a partner at all (disc sub_821EB068).
const refii::guest::Addr kCrossfadeOn{0x832DE527u};
/// byte_8359717C: the partner walk starts from 1 instead of 1 − w (sub_826E7AC0 disc 0x826BC3FC).
const refii::guest::Addr kPartnerFromOne{0x8359717Cu};
/// dbl_835976D0 / qword_835976D8: the guest clock `(mftb − base) / frequency` (sub_822099C0).
const refii::guest::Addr kTimeFrequency{0x835976D0u};

const refii::guest::Addr kRampInTimed{0x82001AC8u};
const refii::guest::Addr kRampOutTimed{0x820017F4u};

/// The ragdoll's class (ctor sub_82B44938 stores it at 0x82B449A4); its slot 2 answers 1, the
/// test sub_82740F90 makes.
const refii::guest::Addr kRagdollVtable{0x82006D00u};
/// `lwz r3, 0x20(r3); blr` — slot 0x70 of every non-simple placement class (0x82007D28,
/// 0x820078F0, 0x82007ED8, 0x82007B78, 0x82007E00, ...); the simple ones answer 0 (sub_82F5D180).
const refii::guest::Addr kPhysicsProxyGetter{0x82EAA858u};

/// The running build (fable3_guest_build.h). The bridge is compiled once; each walk asks.
inline bool Tu3() { return refii::guest::Current() == refii::guest::Build::kTu3; }

/// mgr+96 TU3 / mgr+92 disc: the current REPLICATED tree. TU3 inserted a word at +88 (the replica
/// record, below), so the trees moved up one: TU3 ctor `stw r30,0x60(r31)` 0x826E75B8, receipt
/// `stw r30,0x60(r31)` 0x826EE310, gate `lwz r11,0x60(r31)` 0x8235C0B4, linked pose `lwz r10,0x60(r31)`
/// 0x82745E90; disc ctor `stw r30,0x5C(r31)` disc 0x826BBEE4, gate `lwz r11,0x5C(r31)` disc 0x82353C30,
/// linked pose `lwz r11,0x5C(r31)` disc 0x826C1590.
inline uint32_t CurrentTreeOffset(bool tu3) { return tu3 ? 96u : 92u; }

/// mgr+104 TU3 / mgr+100 disc: the current tree's receipt time. TU3 `stfs f31,0x68(r31)` 0x826EE314,
/// read `lfs f0,0x68(r31)` 0x82745EE4; disc `lfs f0,0x64(r31)` disc 0x826C1610.
inline uint32_t CurrentReceiptOffset(bool tu3) { return tu3 ? 104u : 100u; }

/// A receipt alpha the shell can use: a minute of extrapolation is a tree the host stopped sending,
/// not a clip to run on (the disc walk's guard, applied to TU3's stated alphas alike).
inline float ReceiptAlpha(double alpha) {
    return std::isfinite(alpha) && alpha > -1.0 && alpha < 15.0 * 60.0 ? static_cast<float>(alpha) : 1.0f;
}

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
/// Inside a bone-mask node (kind 4) or a replica crossfade (kind 6).
template <typename Memory>
using MaskTail = Fields<Memory, 36u, 48u>;
template <typename Memory>
using FadeTail = Fields<Memory, 36u, 48u>;
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

/// The guest clock in seconds, as sub_822099C0 reads it: `(mftb − qword_835976D8) /
/// dbl_835976D0`; `memory.GuestTicks()` is the recompiled module's `mftb`.
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

/// Flattens one render tree into clip leaves, depth-first, as sub_82229C90 evaluates it.
/// `weight` is the leaf's share of the final pose; `importance` is the evaluator's `a7`, the
/// product the culls test (a set's children carry their raw weight in it, not the normalised one).
///
/// TU3 walks kinds 0..7 (`cmplwi cr6,r11,7` 0x82229D14; the disc `cmplwi cr6,r11,6` disc 0x8222AF7C, and
/// the disc walk takes no kind 6 or 7: its replica is WalkReplica's). The two replica kinds replace
/// the evaluator's draw alpha (its f2, which a clip reads as `(1 − α)·+32 + α·+36`, `fmadds f31` 0x82229DA0)
/// for their subtree:
///   7: +28 at alpha +32 (`lwz r31,0x1C(r25)` 0x8222AB54, `lfs f2,0x20(r25)` 0x8222AB58), the same
///      importance (`fmr f1,f30` 0x8222ABA8);
///   6: A +28 at alpha +40 (`lfs f2,0x28(r25)` 0x8222AA3C) by `1 − f`, B +32 at alpha +44 (`lfs f2,0x2C(r25)`
///      0x8222AAB8) by f, f = +36 (`lfs f31,0x24(r25)` 0x8222A9CC); one side alone when
///      `min(f, 1 − f) · importance ≤ cull` (`ble cr6` 0x8222AA34), B when f ≥ 0.5 (`lis r10,0x3F00`
///      0x8222AB00) — kind 1's rule, with an alpha per side.
template <typename Memory>
struct Flattener {
    const Memory& memory;
    uint32_t* out;
    uint32_t capacity;
    uint32_t leaves = 0;
    Stated stated;
    bool tu3 = Tu3();

    struct Frame {
        uint32_t node;
        float weight;
        float importance;
        bool additive;
        uint32_t mask;
        bool replica;  // the leaf's times are a replica extrapolation at `alpha`
        float alpha;
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
        if (frame.replica) {
            // `(1 − α)·+32 + α·+36` at the receipt alpha, the previous value one graph step back
            // so consecutive folds join (Fable 2's replica contract).
            const float previous = head.Float(32u);
            const float current = tail.Float(36u);
            float step = current - previous;
            if (!(step > 0.0f) || !std::isfinite(step)) {
                step = 1.0f / 15.0f;
            }
            const float sampled = previous + step * frame.alpha;
            if (std::isfinite(sampled)) {
                leaf[1] = Bits(sampled);
                leaf[2] = Bits(sampled - step);
            }
        }
        leaf[3] = Bits(frame.weight);
        leaf[4] = (tail.Byte(40u) != 0u ? 1u : 0u) | (frame.additive ? 2u : 0u) | (frame.replica ? 8u : 0u) |
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
        stack[depth++] = Frame{root, weight, importance, false, 0u, stated.replica, stated.replica_alpha};
        int steps = 0;
        while (depth > 0 && leaves < capacity && ++steps < 512) {
            const Frame frame = stack[--depth];
            if (!Plausible(frame.node)) {
                continue;
            }
            const auto push = [&](uint32_t child, float w, float imp, bool additive, uint32_t mask) {
                if (depth < 64 && Plausible(child)) {
                    stack[depth++] = Frame{child, w, imp, additive, mask, frame.replica, frame.alpha};
                }
            };
            // A TU3 replica kind's child, at the alpha the node states.
            const auto push_at = [&](uint32_t child, float w, float imp, float alpha) {
                if (depth < 64 && Plausible(child)) {
                    stack[depth++] = Frame{child, w, imp, frame.additive, frame.mask, true, ReceiptAlpha(alpha)};
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
                case 6u: {
                    if (!tu3) {
                        break;
                    }
                    const FadeTail<Memory> tail(memory, frame.node);
                    const float f = Finite(tail.Float(36u));
                    const uint32_t a = head.Word(28u);
                    const uint32_t b = head.Word(32u);
                    const float alpha_a = tail.Float(40u);
                    const float alpha_b = tail.Float(44u);
                    if (std::min(f, 1.0f - f) * frame.importance <= kCull) {
                        if (f >= 0.5f) {
                            push_at(b, frame.weight, frame.importance, alpha_b);
                        } else {
                            push_at(a, frame.weight, frame.importance, alpha_a);
                        }
                        break;
                    }
                    const float t = std::clamp(f, 0.0f, 1.0f);
                    push_at(a, frame.weight * (1.0f - t), frame.importance * (1.0f - t), alpha_a);
                    push_at(b, frame.weight * t, frame.importance * t, alpha_b);
                    break;
                }
                case 7u:
                    if (tu3) {
                        push_at(head.Word(28u), frame.weight, frame.importance, head.Float(32u));
                    }
                    break;
                default:
                    break;
            }
        }
    }
};

/// The record (72 bytes TU3, 68 disc; the same [8, 64) in both), read once.
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

/// sub_826E7AC0 at one end of the tick (`current` = α 1, else α 0): each non-top primary's
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

/// sub_821CD208 / sub_826EA518 at one end of the tick, for the primary of priority `priority`:
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

    // THE DOMINANT LEAF, sub_821FAD98 (disc sub_821FB610), both builds: 0 the leaf; 1 B (+32) when
    // +36 ≥ 0.5 else A (+28); 3 and 4 take +28; 5 +32. The disc stops there (its switch holds 0, 1, 3,
    // 4, 5; anything else `*a1 = 0` disc 0x821FB8F0). TU3 goes on (`cmplwi r10,7` 0x821FADC8):
    //   2 the heaviest child by `+20·0 + +16` (`lfs 0x14`, `lfs 0x10` 0x821FB16C..0x821FB180), the first
    //     of equals, above 0 (`fcmpu` 0x821FB178 against a best starting 0.0); none, no leaf;
    //   6 its B, +32 (`addi r4,r11,0x20` 0x821FB084); 7 its child, +28 (`lwz r11,0x1C(r11)` 0x821FB094);
    //   above 7 the node itself (`bgt cr6` 0x821FADCC -> `stw r11,0(r31)` 0x821FB1B8).
    const bool tu3 = Tu3();
    const auto heaviest_child = [&](uint32_t node, const NodeHead<Memory>& head) -> uint32_t {
        const uint32_t begin = head.Word(32u);
        const uint32_t end = NodeTail<Memory>(memory, node).Word(36u);
        // `(end − begin) & ~3 > 0` (0x821FB150); the children bound is the walk's own, as the set's.
        if (!Plausible(begin) || end < begin || end - begin > 4u * kMaxSetChildren ||
            ((end - begin) & ~3u) == 0u) {
            return 0u;
        }
        uint32_t child[kMaxSetChildren];
        const uint32_t count = (end - begin) / 4u;
        ReadWords(memory, begin, child, count, true);
        uint32_t heaviest = 0u;
        bool found = false;
        float best = 0.0f;
        for (uint32_t i = 0; i < count; ++i) {
            // `+20·0 + +16`: +16 itself unless +20 is not finite, when no comparison holds.
            const float w = FloatAt(memory, child[i] + 16u);
            if (std::isfinite(FloatAt(memory, child[i] + 20u)) && w > best) {
                best = w;
                heaviest = child[i];
                found = true;
            }
        }
        return found ? heaviest : 0u;
    };
    const auto dominant = [&](uint32_t node) -> uint32_t {
        for (int depth = 0; depth < 32 && Plausible(node); ++depth) {
            const NodeHead<Memory> head(memory, node);
            const uint32_t kind = head.Word(24u);
            if (tu3 && kind > 7u) {
                return node;
            }
            switch (kind) {
                case 0u:
                    return node;
                case 1u:
                    node = head.Word(NodeTail<Memory>(memory, node).Float(36u) >= 0.5f ? 32u : 28u);
                    break;
                case 2u:
                    if (!tu3) {
                        return 0u;
                    }
                    node = heaviest_child(node, head);
                    break;
                case 3u:
                case 4u:
                    node = head.Word(28u);
                    break;
                case 5u:
                    node = head.Word(32u);
                    break;
                case 6u:
                    if (!tu3) {
                        return 0u;
                    }
                    node = head.Word(32u);
                    break;
                case 7u:
                    if (!tu3) {
                        return 0u;
                    }
                    node = head.Word(28u);
                    break;
                default:
                    return 0u;
            }
        }
        return 0u;
    };

    // The linked pose's source, sub_82745D38 (disc sub_826C1570): the current replicated tree's
    // dominant leaf when one is installed (`lwz r10,0x60(r31)` 0x82745E90 / disc `lwz r11,0x5C(r31)`
    // disc 0x826C1590), at `now − receipt + leaf+36` (0x82745EF4 / disc 0x826C1624); else the TOP record's.
    const ManagerSpan<Memory> fields(memory, manager);
    uint32_t leaf = 0u;
    double now = 0.0;
    const uint32_t tree = fields.Word(CurrentTreeOffset(tu3));
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

    // `dominant` answers a kind-0 node, which owns [28, 40), or in TU3 one of a kind above 7, whose
    // +28 and +36 the guest reads all the same (`lwz r30,0x1C(r3)` / `lfs f31,0x24(r3)` off
    // sub_821FB200's answer, 0x82745ED8 / 0x82745EDC).
    const ClipSpan<Memory> clip(memory, leaf);
    float time = clip.Float(36u);
    float previous = clip.Float(32u);
    if (replica) {
        const float current = time;
        float step = current - previous;
        if (!(step > 0.0f) || !std::isfinite(step)) {
            step = 1.0f / 15.0f;
        }
        time = static_cast<float>(now - fields.Float(CurrentReceiptOffset(tu3))) + current;
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

/// The replicated tree as disc sub_826BDEC8 folds it (anim_feed.cpp states the contract). DISC ONLY:
/// its offsets are the disc manager's — +88 previous tree (`lwz r11,0x58(r31)` disc 0x826BDF78), +92
/// current (`lwz r11,0x5C(r4)` disc 0x826BDEE0), +96 / +100 their receipts (`lfs f0,0x60(r31)` disc
/// 0x826BE000, `lfs f0,0x64(r31)` disc 0x826BDF54), +104 the fade start (`lfs f0,0x68(r31)` disc 0x826BDF88).
/// TU3 has no such fold: its replica is a primary record (EntityAnimState states it).
template <typename Memory>
void WalkReplica(const Memory& memory, const ManagerSpan<Memory>& manager, Flattener<Memory>& flat) {
    double now = 0.0;
    const bool clock = GuestSeconds(memory, &now);
    const auto alpha_since = [&](uint32_t receipt_offset) -> float {
        if (!clock) {
            return 1.0f;
        }
        return ReceiptAlpha((now - manager.Float(receipt_offset)) * 15.0);
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
    const bool tu3 = Tu3();
    const ManagerSpan<Memory> fields(memory, manager);
    const uint32_t primary_count = manager == 0u ? 0u : fields.Word(32u);
    const uint32_t replica = manager == 0u ? 0u : fields.Word(CurrentTreeOffset(tu3));

    // THE GATE'S CHOICE OF SOURCE (sub_8235B698: `lwz r11,0x20(r31)` 0x8235C0A8, then the current
    // tree `lwz r11,0x60(r31)` 0x8235C0B4 / disc `lwz r11,0x5C(r31)` disc 0x82353C30): component 23 with
    // records or a replicated tree poses the rig; otherwise component 215 does, when present.
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

    Flattener<Memory> flat{memory, out_words, word_capacity / kLeafWords, 0u, Stated{}, tu3};

    // THE GUEST'S OWN CHOICE OF ROOT, sub_821E9728 (disc sub_821E9AC0).
    //
    // DISC: the replicated tree when it exists and the primary list is empty or holds nothing at 128
    // (`lwz r11,0x284(r3)` + `cmpwi cr6,r11,0x80` disc 0x821E9AF8 / disc 0x821E9AFC, then `lwz r11,0x5C(r27)`
    // disc 0x821E9B0C -> `bl sub_826BDEC8` disc 0x821E9B40; likewise sub_822086C8 disc 0x82208F6C..disc 0x82208F80),
    // else the primary fold (`bl sub_821EB068` disc 0x821E9BD0) — one or the other.
    //
    // TU3: no choice. The replica is a PRIMARY RECORD: on receipt sub_826EE1A0 inserts it into the
    // primary list at priority 128 (`li r6,0x80` 0x826EE554, `bl NAnimationManager__CCompositeState_p_ctor`
    // 0x826EE564, which links at mgr+28: `addi r4,r29,0x1C` 0x826E8058) and keeps it at mgr+88; the step
    // states its weight at +20/+24 (`stfs f13,0x18(r11)` / `stfs f0,0x14(r11)` 0x8220E53C / 0x8220E540).
    // Each fold rebuilds its root — the current tree under a kind-7 node, or a kind-6 crossfade from the
    // previous one (sub_826EA238) — when the current tree is set and the weight is not 0 (`lwz r11,0x60(r3)`
    // 0x821E9738, `lfs f0,0x2CC(r3)` 0x821E9760 against 1e-4, `bl sub_82293B30` on `lwz r3,0x58(r27)`
    // 0x821E97CC..0x821E97D0), then ALWAYS runs the primary fold (`li r6,0` 0x821E9810, `bl sub_826E9CD8`
    // 0x821E9820). So the primary walk below is the whole of it: the record is read as any other, and
    // the Flattener takes its kind-7 / kind-6 root. (The +652 / 256 test — `lwz r11,0x28C(r31)`,
    // `cmpwi cr6,r11,0x100` 0x826EA4EC / 0x826EA4F0 — only drives that weight, in the step.)
    if (!tu3 && Plausible(replica) &&
        (primary_count == 0u || static_cast<int32_t>(WordAt(memory, manager + 644u)) < 128)) {
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

    // TOP = the tail (disc sub_821EB068: `sentinel->prev`), whatever its state. TU3's tail getter
    // sub_826E7A70 skips records whose +0x44 byte is 0 only while byte_8359717E is set (`lbz r6,0x44(r7)`
    // 0x826E7A9C), and only sub_826E9CD8 sets it, for a nonzero fifth argument (`stb r11` 0x826E9D10,
    // cleared `stb r27` 0x826EA21C); the pose fold passes 0 (`li r6,0` 0x821E9810), so here it is the tail.
    // The overlay test sub_826E6920 and the partner walk sub_826E7AC0 read the same byte (0x826E6978 /
    // 0x826E7C38) and skip nothing either.
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
