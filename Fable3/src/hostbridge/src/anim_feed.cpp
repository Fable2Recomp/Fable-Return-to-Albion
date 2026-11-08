// refii - Fable 3's animation state, as the guest holds it: the clips each rig is playing, read
// out of its CECAnimationManager into the shell's clip-leaf layout (HostBridge.GetEntityAnimState),
// with the guest's own fold stated on them — which record is on top, which records it crossfades
// against and by how much, which overlays wrap it and at what share.
//
// Every offset is from the Fable 3 IDB, each against its own base (anim/CSA_SPEC.md and
// anim3/REPORT.md have the derivations):
//
//   component 23 = CECAnimationManager (factory sub_82574B08, 720 bytes in TU3 / 704 on the disc,
//             ctor sub_826E74C8, vtable 0x82003780). Found through the entity's {u32 id, component} vector at
//             entity+0x54/+0x58 as entity_feed.cpp finds every component.
//   mgr+28/+32  PRIMARY list sentinel / count, 12-byte nodes {next, prev, record}. sub_826E7FA0
//             inserts by priority (record+8) ascending, so the TAIL is the highest priority.
//             Primaries carry track 1 and mask = their own priority (`sub_8228E178(r, &tree,
//             prio, 1, prio, in, out)` at 0x826E80BC).
//   mgr+40/+44  OVERLAY list sentinel / count (inserted by sub_826E8150 at the tail, then
//             list-sorted by track then priority, stable: the comparator in sub_826F16D0
//             0x826F1780..0x826F17B8).
//   mgr+84    bone LOD tier: 4 = full (sub_8235B698 disc 0x823538EC).
//   REPLICATED trees (a co-op client's remote rig), where the builds differ (anim_walk.h):
//     disc    mgr+88/+92 the previous / current tree, +96 / +100 their receipt times, +104 the
//             crossfade start; mgr+644 the highest live primary priority. The fold takes the
//             replicated tree OR the primaries (disc sub_821E9AC0).
//     TU3     a word inserted at +88, the REPLICA RECORD: a primary of priority 128 whose weight
//             the step states (mgr+716 -> record+20) and whose root each fold rebuilds as a kind-7
//             node over the current tree, or a kind-6 crossfade from the previous one; the trees
//             are mgr+92/+96, the receipts +100/+104, the crossfade start +108, the priority
//             +652. The fold is always the primaries' (sub_821E9728).
//   record    72 bytes in TU3 (`li r3,0x48` 0x826E8004; 68 on the disc), ctor sub_8228E178: +8
//             priority, +12 track, +16 priority bitmask, +20 weight current, +24 weight previous,
//             +28/+32 the same pair copied at the end of each step (sub_8220DCD0 disc
//             0x8220F060..disc 0x8220F090), +36 state (0 live, 1 retiring, 2 dead), +40 uid (mgr+52
//             counter), +44 RENDER tree root, +52 ramp-in, +60
//             ramp-out. A ramp (sub_8217EE18 / sub_8228DBF0) holds +4 the step per 15 Hz tick,
//             `min(1/(seconds·15), 1)`, +8 the seconds; vtables 0x82001AC8 (in) / 0x820017F4
//             (out), and the instant ones 0x82001990 / 0x820AAAE8.
//   THE STEP  sub_8220DCD0 (comp 23 vtable slot 72) per game tick: every record steps its OWN
//             ramp — ramp-in while state 0, ramp-out once retiring — `+24 = +20; +20 ±= step ·
//             timescale` clamped to [0, 1] (0x82279A48 / 0x82189488); a finished ramp settles
//             `+24 = +20` (ramp-in done above 0.9999, out below 1e-4; 0x8221D610 / disc 0x821839E0).
//             A retiring record under 0.05 and a dead one are unlinked. So the stated pair IS
//             the per-tick weight; nothing here re-derives it.
//   THE FOLD  sub_821E9728 -> disc sub_821EB068, every frame at the draw alpha α:
//             TOP = the tail record. `w = lerp(top+24, top+20, α)`. With the crossfade on
//             (byte_832DE527) and `|w − 1| ≥ 0.05`, sub_826E7AC0 builds the PARTNER: walking the
//             other records from the top down, `remaining = 1 − w` (1 when byte_8359717C), each
//             takes `s = min(remaining, lerp(r+32, r+28, α))` while `|remaining| ≥ 0.05`, kept
//             when `|s| ≥ 0.05`; the kept trees form a weighted set normalised by Σs. The pose is
//             a crossfade node (sub_826E5090, kind 1) of partner (A) against top (B) by w, which
//             the evaluator prunes to one side when `min(w, 1−w) ≤ 0.05`. No partner → the top
//             alone at full weight.
//             Each primary's tree is wrapped by the overlays first (sub_821EA9E8 -> sub_821CD208):
//             per track, the overlays passing `|lerp(o+24, o+20, α)| ≥ 0.05 && (o+16 & rec+8)`
//             (sub_826E6920); one passing → its tree at its weight; several → walking the group
//             from its tail, `s = min(remaining, w)` from remaining 1 while `|remaining| ≥ 0.05`,
//             a normalised set at Σs (sub_826EA518); the layer is a kind-3 node (sub_821990E0).
//   render node  kind at +24, weight current +16 / previous +20 (sub_82229C90, disc 0x8222AF8C;
//             `a30` = {0.05 cull, 0.5}, flt_8206C74C / flt_8206C748, set at 0x821E9A58..90):
//             0 clip:      +28 clip key, +32 previous time, +36 current time, +40 loop byte.
//             1 crossfade: +28 A, +32 B, +36 factor, +40 previous; one side alone when
//                          `min(f, 1−f) · importance ≤ 0.05` — B when f ≥ 0.5 (0x8222A26C..b530).
//             2 set:       children in [+32, +36), each weighted by its own +16/+20; sorted
//                          heaviest first (sub_8228DD00), the first always, the next while
//                          `w · importance > 0.05`, normalised by the running sum.
//             3 layer:     +32 base, +28 layer applied at the layer's own +16/+20 weight when
//                          `w · importance ≥ 0.05`; a clip layer is a whole-body blend (the clip
//                          delta byte, clip+72 via vtable slot 40, is only ever zeroed).
//             4 bone mask: +28 child, +32 skeleton id, [+40, +44) one byte per bone (sub_821C7B78
//                          writes `byte · flt_832E93B8` into each pose element's mask lane).
//             5 difference: +32 pose minus +28 reference (sub_82D80CD8), additive.
//             6 replica crossfade: +28 A at alpha +40 and +32 B at alpha +44 by +36 (TU3 walks it).
//             7 replica (TU3 only): +28 at alpha +32, the alpha a clip interpolates +32 -> +36 by.
//   clip key  the FNV-1 of the clip's lowercased bank path (sub_82D6D260 opens the VFS by it).
//
// Out words per leaf are Fable 2's REFII_ANIM_LEAF_WORDS (host_bridge.cpp, "Out words"): [0] clip
// key, [1] current time bits, [2] previous time bits, [3] weight bits, [4] flags (bit0 loop, bit1
// additive/layer, bit2 OVERLAY, bit3 times are a replica extrapolation, bit4 the guest's TOP,
// bit5 a member of the guest's PARTNER set, bits 8..15 priority, bits 16..23 track), [5] record
// uid, [6]/[7] ramp-in / ramp-out seconds, [8] the nearest bone-mask node above the leaf,
// [9] the record's priority bitmask, [10]/[11] the record's stated SHARE of the fold at the
// previous / current tick (see `EntityAnimState`).
//
// The walk itself is anim_walk.h, each guest object read as one span (its extents and their
// addresses are stated there); tests/anim_state_test.cpp holds it to the per-field reads.

#include <atomic>
#include <cstdint>

#include "anim_walk.h"
#include "core/rex_payload_bridge.h"
#include "host_abi.h"
#include "host_core.h"

namespace {

namespace anim = refii::fable3::anim;
using refii::hostcore::SafeKernelMemory;

/// Guest memory for the walk: the guarded reads, and the recompiled module's `mftb`.
struct AnimReader : refii::hostcore::GuestReader {
    uint64_t GuestTicks() const { return refii_bridge_rex_query_guest_tick_count(); }
};

const uint8_t* GuestBase() {
    rex::memory::Memory* memory = SafeKernelMemory();
    return memory == nullptr ? nullptr : memory->virtual_membase();
}

/// Why a rig had no leaves, by cause, for RefiiHostGetAnimWalkStats.
std::atomic<uint64_t> gNoManager{0};
std::atomic<uint64_t> gManagerFound{0};
std::atomic<uint64_t> gEmptyList{0};
std::atomic<uint64_t> gLinked{0};

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
    if (out_words == nullptr || word_capacity < anim::kLeafWords || !anim::Plausible(entity)) {
        return 0u;
    }
    const uint8_t* base = GuestBase();
    if (base == nullptr) {
        return 0u;
    }

    const AnimReader reader{{base}};
    const anim::AnimState state = anim::EntityAnimState(reader, entity, out_words, word_capacity, out_flags);
    if ((state.counts & anim::kCountNoManager) != 0u) {
        gNoManager.fetch_add(1u, std::memory_order_relaxed);
    }
    if ((state.counts & anim::kCountManagerFound) != 0u) {
        gManagerFound.fetch_add(1u, std::memory_order_relaxed);
    }
    if ((state.counts & anim::kCountEmptyList) != 0u) {
        gEmptyList.fetch_add(1u, std::memory_order_relaxed);
    }
    if ((state.counts & anim::kCountLinked) != 0u) {
        gLinked.fetch_add(1u, std::memory_order_relaxed);
    }
    return state.leaves;
}

/// THE BYTES OF ONE BONE MASK NODE — kind 4 (sub_82229C90 case 4 -> sub_821C7B78): one byte per
/// skeleton bone in [+40, +44), applied only when the pose's skeleton id equals +32 (0x821C7BC8).
/// Returns the bytes written (0 for a node that is not a mask) and, when asked, that skeleton id.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetAnimMask(uint32_t node, uint8_t* out_bytes, uint32_t capacity,
                                                    uint32_t* out_skeleton_id) {
    if (out_skeleton_id != nullptr) {
        *out_skeleton_id = 0u;
    }
    if (out_bytes == nullptr || capacity == 0u) {
        return 0u;
    }
    const uint8_t* base = GuestBase();
    if (base == nullptr) {
        return 0u;
    }
    const AnimReader reader{{base}};
    return anim::AnimMask(reader, node, out_bytes, capacity, out_skeleton_id);
}

/// Fable 2's aim and look post-ops (skeleton chains blended toward per-record 4-vectors after
/// each node) have no Fable 3 counterpart: Fable 3's evaluator (sub_82229C90) has eight node kinds
/// in TU3 (seven on the disc) and none of them is an aim or look op — its kind 4 is the bone mask above — and its record
/// (sub_8228E178) carries no op pointers. Nothing to state.
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
