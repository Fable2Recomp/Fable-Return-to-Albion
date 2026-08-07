#include "runtime/refii_renderer/gtl/refii_gtl.h"
#include "runtime/refii_renderer/gtl/refii_gtl_state.h"
#include "runtime/refii_renderer/gtl/refii_gtl_symbols.h"

#include <algorithm>
#include <array>
#include <bit>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <atomic>
#include <mutex>
#include <string>
#include <unordered_map>
#include <unordered_set>
#include "renderer_runtime.h"
#include "rex/logging.h"
#include "runtime/cvars.h"
#include <rex/chrono/clock.h>
#include <rex/graphics/pipeline/texture/info.h>
#include "runtime/refii_renderer/hooks/general/fable_hook_stack.h"
#include "runtime/refii_renderer/hooks/root/fable_hook_symbols.h"
#include "runtime/refii_renderer/bik/bink_guest_memory.h"
#include "runtime/refii_renderer/plume/fable/direct/fable_direct_state.h"
#include "runtime/refii_renderer/plume/fable/resource/fable_resource_snapshots.h"
#include "runtime/refii_renderer/plume/fable/resource/fable_upload_data.h"
#include "runtime/refii_renderer/plume/general/plume_fable_addressing.h"
#include "runtime/refii_renderer/plume/fable/fable_guest_display.h"

namespace refii::gtl {

uint64_t LoadGuestBigEndianU64(uint8_t *guest_base, uint32_t address) {
  const uint64_t hi =
      refii::renderer::bink::LoadGuestU32(guest_base, address);
  const uint64_t lo =
      refii::renderer::bink::LoadGuestU32(guest_base, address + 4u);
  return (hi << 32) | lo;
}

// Read N consecutive big-endian dwords out of the device object into `out`.
// Returns false and leaves `out` untouched when the range is unreadable, so a
// snapshot never carries invented state.
template <std::size_t N>
bool LoadDeviceDwordBlock(uint8_t *guest_base, uint32_t device, uint32_t offset,
                          std::array<uint32_t, N> &out) {
  const uint32_t at = device + offset;
  if (!refii::renderer::bink::IsReadableGuestByteRange(
          guest_base, at, static_cast<uint32_t>(N * sizeof(uint32_t)))) {
    return false;
  }
  for (std::size_t i = 0; i < N; ++i) {
    out[i] = refii::renderer::bink::LoadGuestU32(
        guest_base, at + static_cast<uint32_t>(i * sizeof(uint32_t)));
  }
  return true;
}

// Device-object offsets, straight from the IDB's typed D3DDevice.
constexpr uint32_t kSnapPendingMaskOffset = 0x0000u;      // m_Pending.m_Mask[5]
constexpr uint32_t kSnapPredicatedMask2Offset = 0x0028u;
constexpr uint32_t kSnapFetchOffset = 0x0480u;            // 192 dwords
constexpr uint32_t kSnapVertexFloatOffset = 0x0780u;      // 256 vec4
constexpr uint32_t kSnapPixelFloatOffset = 0x1780u;       // 256 vec4
constexpr uint32_t kSnapFlowOffset = 0x2780u;             // 40 dwords
constexpr uint32_t kSnapClipPlaneOffset = 0x2820u;        // 24 dwords
constexpr uint32_t kSnapPacketBlockOffset = 0x2880u;      // 124 dwords
// m_ControlPacket.ModeControl (0x2948) inside the 0x2880 block.
constexpr uint32_t kSnapModeControlBlockIndex =
    (0x2948u - kSnapPacketBlockOffset) / 4u;  // == 50
// D3DRS_CULLMODE mirror slot (fable_hook_symbols.cpp: sub_8220B470 -> 19).
constexpr uint32_t kSnapCullRenderStateSlot = 19u;

// Copy the 64 bytes a single dirty-mask bit covers. Bit numbering is FROM THE
// MSB (sub_8221DE68 walks with cntlzd), so bit k covers vec4 regs [4k..4k+3]
// == bank + 64*k == host dwords [16k..16k+15].
bool LoadDirtyConstantBlock(uint8_t *guest_base, uint32_t device,
                            uint32_t bank_offset, uint32_t bit,
                            std::array<uint32_t, 16> &out) {
  return LoadDeviceDwordBlock(guest_base, device, bank_offset + bit * 64u, out);
}

// Collect the set bits of a 64-bit dirty mask, MSB first (bit 0 == MSB), the
// same order sub_8221DE68 drains them in.
void CollectSetMaskBits(uint64_t mask, std::vector<uint8_t> &out) {
  out.clear();
  while (mask != 0) {
    const uint32_t bit = static_cast<uint32_t>(__builtin_clzll(mask));
    out.push_back(static_cast<uint8_t>(bit));
    mask &= ~((1ull << 63) >> bit);
  }
}

// ---------------------------------------------------------------------------
// PHASE 2, FETCH CLASS ONLY: device-sourced fetch constants.
// ---------------------------------------------------------------------------
//
// WHY THIS IS SAFE AT HOOK ENTRY (IDA-verified 2026-07-25):
//   * D3DDevice_SetTexture (0x821B6EE0) writes the six dwords of
//     m_Constants.Fetch[Sampler] DIRECTLY (`v12 = &pDevice->m_Constants
//     .Fetch[Sampler]; v12->Texture.dword[0..5] = ...`) and only then sets a
//     bit in m_Pending.m_Mask[3]. There is no pending shadow for fetch
//     constants — the texture POINTER array at device+0x3100 is what
//     SetTexture stages, not the constant.
//   * D3D_SetPending_FetchConstants (0x8221D1B0) only READS that block and
//     copies it into the ring at Xenos reg base 0x4800. It never writes it.
//   Therefore device+0x480 is always-current storage: whatever is there at
//   hook ENTRY is exactly what this draw will be handed, for every fetch
//   constant the guest set BEFORE calling the root.
//
// THE ONE ROOT THIS IS NOT TRUE FOR: sub_8220A3F0 (kDrawIndexedUserPrimitives)
// opens its body with `Render_FlushDirtyRenderStates()` (0x8221E4A0), the
// game's own deferred state drain — it walks g_RenderStateDirtyList and calls
// Render_ApplyDirtyStateVar, which is where the game's staged textures reach
// D3DDevice_SetTexture and therefore device+0x480. At that root's ENTRY the
// fetch block still describes the PREVIOUS draw. That root is excluded here
// and keeps its existing EXIT-time self-read
// (CaptureDeviceFetchConstantsIntoPacketLocked, called from OnHookExit).
//
// GATE: compile-time default plus an env override. DELIBERATELY NOT A CVAR —
// an unregistered key in the launcher's GetRuntimeCVarDefinitions silently
// reads false, which already cost a run. Nothing here depends on the launcher.
//   REFII_GTL_FETCH_DEVICE_SOURCED=0  -> mirror-fed (pre-cutover behaviour)
//   REFII_GTL_FETCH_DEVICE_SOURCED=1  -> device-sourced (the default)
constexpr bool kFetchDeviceSourcedDefault = true;

bool FetchDeviceSourced() {
  static const bool enabled = [] {
    const char *env = std::getenv("REFII_GTL_FETCH_DEVICE_SOURCED");
    if (env == nullptr || env[0] == '\0') {
      return kFetchDeviceSourcedDefault;
    }
    return !(env[0] == '0' && env[1] == '\0');
  }();
  return enabled;
}

// *** THE DEVICE DIRTY-MASK DRAIN — GTL-OWNED (2026-08-04). ***
//
// The guest driver contract is CONSUME-AND-CLEAR: the guest ORs dirty bits into
// m_Pending.m_Mask[5] at dev+0x00..0x20, and the driver is expected to zero
// them once it has acted. Nothing in the tree did this after the observer was
// deleted — it was the sole implementer — and the consequences are not subtle:
//
//   * The guest's own bool/int setters (sub_8226E120 / sub_82B99208) mark the
//     scalar block, and the packed bool words at dev+0x2780 are published on
//     that mark. The observer's own note recorded what a missed publish costs:
//     "every translated shader saw g_Booleans == 0".
//   * Our journal check reads `pending_mask[4] & 0x01000000` to decide whether
//     a flow change was ANNOUNCED. Never clearing means that bit latches set
//     after the first change, so the check silently reads "always journalled"
//     and the divergence census goes blind rather than reporting.
//
// We do NOT reproduce the observer's publish half. That existed to push the
// banks into the observer's own mirror; the GTL reads dev+0x480 (fetch) and
// dev+0x2780 (flow) DIRECTLY in CaptureDrawDeviceSnapshot, so the data is
// already in hand. Only the clear was missing.
//
// MUST run AFTER the snapshot, so the captured pending_mask still carries the
// bits this call is about to consume.
void DrainDeviceDirtyMasksLocked(uint32_t device, uint8_t *guest_base) {
  if (guest_base == nullptr || device == 0) {
    return;
  }
  if (!refii::renderer::bink::IsReadableGuestByteRange(
          guest_base, device + kSnapPendingMaskOffset, 0x28u)) {
    return;
  }
  for (uint32_t i = 0; i < 5u; ++i) {
    const uint32_t at = device + kSnapPendingMaskOffset + i * 8u;
    if (LoadGuestBigEndianU64(guest_base, at) != 0ull) {
      // Zero is byte-order agnostic, so two 32-bit stores clear the qword
      // regardless of endianness. There is no 64-bit guest store helper.
      refii::renderer::bink::StoreGuestU32(guest_base, at, 0u);
      refii::renderer::bink::StoreGuestU32(guest_base, at + 4u, 0u);
      ++g_state.stats.device_dirty_masks_drained;
    }
  }
}

DrawDeviceSnapshot CaptureDrawDeviceSnapshot(uint32_t device,
                                             uint8_t *guest_base) {
  DrawDeviceSnapshot snapshot;
  if (guest_base == nullptr || device == 0) {
    return snapshot;
  }
  // Same two guards every other device self-read in this file uses.
  if (!refii::renderer::IsLikelyFableObjectAddress(device)) {
    return snapshot;
  }
  if (!refii::renderer::bink::IsReadableGuestByteRange(
          guest_base, device + kSnapPendingMaskOffset, 0x30u)) {
    return snapshot;
  }
  snapshot.device = device;
  for (uint32_t i = 0; i < 5u; ++i) {
    snapshot.pending_mask[i] = LoadGuestBigEndianU64(
        guest_base, device + kSnapPendingMaskOffset + i * 8u);
  }
  snapshot.predicated_pending_mask2 =
      LoadGuestBigEndianU64(guest_base, device + kSnapPredicatedMask2Offset);

  // VS/PS float constants: ONLY the 64-byte blocks the guest actually dirtied
  // for this draw. Masks [0] and [1] are cleared by a DRAW and nothing else,
  // so this is a clean per-draw delta. A blind 4 KiB copy of both banks per
  // draw is what this deliberately avoids -- two such copies were already
  // removed from this path for exactly that cost.
  std::vector<uint8_t> bits;
  CollectSetMaskBits(snapshot.pending_mask[0], bits);
  snapshot.vs_dirty_blocks.reserve(bits.size());
  for (const uint8_t bit : bits) {
    DrawDeviceSnapshot::ConstantBlock block;
    block.bit = bit;
    if (LoadDirtyConstantBlock(guest_base, device, kSnapVertexFloatOffset, bit,
                               block.dwords)) {
      snapshot.vs_dirty_blocks.push_back(block);
    }
  }
  CollectSetMaskBits(snapshot.pending_mask[1], bits);
  snapshot.ps_dirty_blocks.reserve(bits.size());
  for (const uint8_t bit : bits) {
    DrawDeviceSnapshot::ConstantBlock block;
    block.bit = bit;
    if (LoadDirtyConstantBlock(guest_base, device, kSnapPixelFloatOffset, bit,
                               block.dwords)) {
      snapshot.ps_dirty_blocks.push_back(block);
    }
  }

  // Masks 2/3/4 are ALSO zeroed by resolve/clear/present, so their delta is
  // diluted and cannot be trusted as a per-draw signal. The regions they
  // describe are small (0x480 fetch, 0x2780 flow, 0x2820 clip, 0x2880..0x2A70
  // packets), so read them unconditionally instead of mask-gating them.
  snapshot.fetch_valid =
      LoadDeviceDwordBlock(guest_base, device, kSnapFetchOffset,
                           snapshot.fetch);
  snapshot.flow_valid =
      LoadDeviceDwordBlock(guest_base, device, kSnapFlowOffset, snapshot.flow);
  snapshot.clip_planes_valid = LoadDeviceDwordBlock(
      guest_base, device, kSnapClipPlaneOffset, snapshot.clip_planes);
  snapshot.packet_block_valid = LoadDeviceDwordBlock(
      guest_base, device, kSnapPacketBlockOffset, snapshot.packet_block);
  if (snapshot.packet_block_valid) {
    snapshot.mode_control = snapshot.packet_block[kSnapModeControlBlockIndex];
    snapshot.mode_control_valid = true;
  }
  snapshot.valid = true;
  return snapshot;
}

// Make the entry-time device fetch block AUTHORITATIVE for this packet
// (Phase 2, fetch class only — see FetchDeviceSourced above for the IDA proof
// that device+0x480 is current at entry, and for the one excluded root).
//
// Two things change relative to the mirror feed:
//   1. All 26 texture-fetch slots come from the device, INCLUDING the ones that
//      read zero. That is the point: the mirror only ever learns about a slot
//      when D3D_SetPending_FetchConstants fires with that slot's dirty bit set,
//      so a slot the guest has since unbound keeps its last nonzero base
//      forever, and a slot bound before the first dispatch never appears at
//      all. "Unbound" must be representable, not merely unlearnable.
//   2. fetch_dwords gets the full 192-dword block, so every fetch-based
//      classifier/binder (format, size, tiling, endian — not just the base)
//      sees the same state the GPU will.
// The vertex-fetch region (slots 26..31 / device+0x6F0) is copied verbatim as
// part of the block but is NOT base-decoded here: those are 2-dword vertex
// fetch constants, not 6-dword texture fetches, and nothing consumes
// fetch_bases past slot 25.
void ApplyDeviceSourcedFetchToPacket(DrawPacket &packet) {
  const DrawDeviceSnapshot &snap = packet.device_snapshot;
  if (!snap.valid || !snap.fetch_valid) {
    return;
  }
  // Both are std::array<uint32_t, 192> over the same device+0x480 block; a
  // shape change on either side breaks this assignment at compile time.
  packet.fetch_dwords = snap.fetch;
  packet.fetch_dwords_valid = true;
  for (uint32_t slot = 0; slot < 26u && slot < packet.fetch_bases.size();
       ++slot) {
    packet.fetch_bases[slot] = snap.fetch[slot * 6u + 1u] & 0xFFFFF000u;
  }
}

// Divergence census (PHASE 1 DELIVERABLE). Two distinct measurements, kept
// separate because they answer different questions:
//
//  CLASS A -- MIRROR COMPARE. For field classes the GTL already mirrors
//  (vs/ps float banks, fetch bases, the cull render state), compare the value
//  freshly read out of the device against what the mirror path put in THIS
//  packet. A disagreement is a stale mirror -- the defect class that produced
//  the WVP-zero bug and the 82%-wrong cull word.
//
//  CLASS B -- JOURNAL COMPARE. Flow constants (0x2780) and the GPU packet
//  block (0x2880..0x2A70) have NO GTL mirror to compare against, so instead
//  measure whether the bytes CHANGED since the previous draw's snapshot while
//  the corresponding dirty-journal bit was CLEAR. That is a write which
//  bypassed the dirty journal entirely -- structurally the same defect, and
//  exactly what the ~28 SetRenderState_*/SetSamplerState_* entry points with
//  direct (mostly GFxRendererXbox360_*) callers are expected to produce. Large
//  numbers on Scaleform/menu frames are CONFIRMATION, not a bug.
DivergenceCensus g_divergence_census;
// Keyed per DISTINCT divergence SHAPE, deliberately not a flat log cap: a flat
// cap fills up with the first repeating pass (Bink redraws one shader pair
// every pump frame) long before anything interesting runs. Bounded so a
// pathological per-draw-unique key cannot grow it without limit.
std::unordered_set<uint64_t> g_divergence_logged_shapes;
// Previous snapshot of the journal-compared regions, per device. Class B is a
// change detector, so it needs the last value.
struct JournalBaseline {
  bool have_flow = false;
  bool have_packets = false;
  std::array<uint32_t, 40> flow = {};
  std::array<uint32_t, 124> packet_block = {};
};
std::unordered_map<uint32_t, JournalBaseline> g_journal_baselines;

// Compare and count. Caller holds g_state.mutex. Reads only; the snapshot and
// the packet are both left exactly as they were.
void AccumulateDeviceSnapshotDivergenceLocked(const DrawPacket &packet) {
  const DrawDeviceSnapshot &snap = packet.device_snapshot;
  if (!snap.valid) {
    ++g_divergence_census.draws_no_snapshot;
    return;
  }
  ++g_divergence_census.draws_examined;

  // --- class A: vs/ps float banks -------------------------------------------
  // packet.vs_float_constants / ps_float_constants are host-order dwords, and
  // so is the snapshot (both go through LoadGuestU32 / bswap), so the compare
  // is apples to apples. Mask bit k covers dwords [16k .. 16k+15].
  uint32_t vs_diverged = 0;
  uint32_t ps_diverged = 0;
  const auto compare_bank =
      [](const std::vector<DrawDeviceSnapshot::ConstantBlock> &blocks,
         const std::vector<uint32_t> &mirror, uint64_t &compared_out,
         uint64_t &diverged_out, uint32_t &diverged_here) {
        for (const auto &block : blocks) {
          const std::size_t first = static_cast<std::size_t>(block.bit) * 16u;
          if (first + 16u > mirror.size()) {
            continue;  // mirror never populated this far; not a divergence
          }
          ++compared_out;
          if (std::memcmp(block.dwords.data(), mirror.data() + first,
                          block.dwords.size() * sizeof(uint32_t)) != 0) {
            ++diverged_out;
            ++diverged_here;
          }
        }
      };
  compare_bank(snap.vs_dirty_blocks, packet.vs_float_constants,
               g_divergence_census.vs_float_blocks,
               g_divergence_census.vs_float_blocks_diverged, vs_diverged);
  compare_bank(snap.ps_dirty_blocks, packet.ps_float_constants,
               g_divergence_census.ps_float_blocks,
               g_divergence_census.ps_float_blocks_diverged, ps_diverged);

  // --- class A: texture fetch bases -----------------------------------------
  // The snapshot's dword1 bits 12..31 are the live base, the SAME decode and
  // the SAME address (device+0x480+slot*24+4, & 0xFFFFF000) the mirror feed
  // uses in hook_observer.cpp's ObservePendingFetchConstantsLocked — so this
  // comparison is apples to apples in domain terms, not raw-vs-physical-view.
  //
  // TWO measurements, deliberately separate:
  //   packet compare  -> device vs what this packet will actually carry.
  //                      Collapses to ~0 once FetchDeviceSourced() is on, for
  //                      every root except the pre-flush wrapper.
  //   mirror compare  -> device vs g_state.device.fetch_bases, the
  //                      OnFetchConstantDispatch-fed cache. Unaffected by the
  //                      cutover, so the staleness number stays observable.
  uint32_t fetch_diverged = 0;
  if (snap.fetch_valid) {
    for (uint32_t slot = 0; slot < 26u && slot < packet.fetch_bases.size();
         ++slot) {
      const uint32_t live = snap.fetch[slot * 6u + 1u] & 0xFFFFF000u;
      const uint32_t carried = packet.fetch_bases[slot];
      if (live != 0 || carried != 0) {
        ++g_divergence_census.fetch_slots;
        if (live != carried) {
          ++g_divergence_census.fetch_slots_diverged;
          ++fetch_diverged;
        }
      }
      const uint32_t mirrored = g_state.device.fetch_bases[slot];
      if (live == 0 && mirrored == 0) {
        continue;  // slot unused by both; nothing to say
      }
      ++g_divergence_census.fetch_mirror_slots;
      if (live != mirrored) {
        ++g_divergence_census.fetch_mirror_slots_diverged;
      }
    }
  }

  // --- class A: the cull word ------------------------------------------------
  // IDA (0x8220B470) preserves bits 29..31 of the caller's Value into
  // device+0x2948, so the low 3 bits of the live dword and the render_states
  // mirror are the SAME domain. Only compared when the mirror was actually
  // written this session (valid_mask bit set) -- otherwise the mirror is the
  // session default and "divergence" would just be measuring the default.
  bool cull_diverged_here = false;
  if (snap.mode_control_valid &&
      (packet.render_state_valid_mask & (1u << kSnapCullRenderStateSlot)) !=
          0u) {
    ++g_divergence_census.cull_compared;
    const uint32_t live_cull = snap.mode_control & 7u;
    const uint32_t mirrored_cull =
        packet.render_states[kSnapCullRenderStateSlot] & 7u;
    if (live_cull != mirrored_cull) {
      ++g_divergence_census.cull_diverged;
      cull_diverged_here = true;
    }
  }

  // --- class B: flow + packet block against the dirty journal ---------------
  // Journal bits, per the mask map:
  //   m_Mask[4] bit 0x01000000 -> Flow (0x2780)
  //   m_Mask[2] (any bit)      -> the 0x2880 packet family
  bool flow_unjournalled = false;
  bool packets_unjournalled = false;
  JournalBaseline &baseline = g_journal_baselines[snap.device];
  if (snap.flow_valid) {
    const bool journalled = (snap.pending_mask[4] & 0x01000000ull) != 0ull;
    if (baseline.have_flow && !journalled &&
        std::memcmp(baseline.flow.data(), snap.flow.data(),
                    snap.flow.size() * sizeof(uint32_t)) != 0) {
      ++g_divergence_census.flow_unjournalled;
      flow_unjournalled = true;
    }
    baseline.flow = snap.flow;
    baseline.have_flow = true;
  }
  if (snap.packet_block_valid) {
    const bool journalled = snap.pending_mask[2] != 0ull;
    if (baseline.have_packets && !journalled &&
        std::memcmp(baseline.packet_block.data(), snap.packet_block.data(),
                    snap.packet_block.size() * sizeof(uint32_t)) != 0) {
      ++g_divergence_census.packets_unjournalled;
      packets_unjournalled = true;
    }
    baseline.packet_block = snap.packet_block;
    baseline.have_packets = true;
  }

  const uint32_t class_bits =
      (vs_diverged != 0 ? 1u : 0u) | (ps_diverged != 0 ? 2u : 0u) |
      (fetch_diverged != 0 ? 4u : 0u) | (cull_diverged_here ? 8u : 0u) |
      (flow_unjournalled ? 16u : 0u) | (packets_unjournalled ? 32u : 0u);
  if (class_bits == 0u) {
    return;
  }
  ++g_divergence_census.draws_diverged;

  // One line per DISTINCT divergence shape. Shape = which draw root, which
  // shader pair, and which classes disagreed -- so a new content class always
  // gets a line no matter how many repeats of an earlier one preceded it.
  uint64_t shape = 1469598103934665603ull;
  const auto mix = [&shape](uint64_t value) {
    shape = (shape ^ value) * 1099511628211ull;
  };
  mix(packet.source_symbol);
  mix(packet.vertex_shader);
  mix(packet.pixel_shader);
  mix(class_bits);
  if (g_divergence_logged_shapes.size() >= 512u ||
      !g_divergence_logged_shapes.insert(shape).second) {
    return;
  }
  ++g_divergence_census.distinct_shapes;
  REXLOG_INFO(
      "Refii GTL device-state DIVERGENCE shape #{} root=sub_{:08X} "
      "vs=0x{:08X} ps=0x{:08X} classes=[vs_float={} ps_float={} fetch={} "
      "cull={} flow_unjournalled={} packets_unjournalled={}] "
      "masks=[{:016X} {:016X} {:016X} {:016X} {:016X}] pred2={:016X} "
      "dirty[vs_blocks={} ps_blocks={}] mode_control=0x{:08X} "
      "mirror_cull=0x{:08X} packet_id={}",
      static_cast<unsigned long long>(g_divergence_census.distinct_shapes),
      packet.source_symbol, packet.vertex_shader, packet.pixel_shader,
      vs_diverged, ps_diverged, fetch_diverged, cull_diverged_here ? 1 : 0,
      flow_unjournalled ? 1 : 0, packets_unjournalled ? 1 : 0,
      static_cast<unsigned long long>(snap.pending_mask[0]),
      static_cast<unsigned long long>(snap.pending_mask[1]),
      static_cast<unsigned long long>(snap.pending_mask[2]),
      static_cast<unsigned long long>(snap.pending_mask[3]),
      static_cast<unsigned long long>(snap.pending_mask[4]),
      static_cast<unsigned long long>(snap.predicated_pending_mask2),
      static_cast<unsigned long long>(snap.vs_dirty_blocks.size()),
      static_cast<unsigned long long>(snap.ps_dirty_blocks.size()),
      snap.mode_control, packet.render_states[kSnapCullRenderStateSlot],
      static_cast<unsigned long long>(packet.packet_id));
}

// The D3D device for a draw root. Most roots take it in r3; the ones that do
// not fetch the g_D3DDevice_PresentModule singleton themselves (IDA), so read
// the same global rather than trusting an argument that is not a pointer.
// Returns 0 when nothing usable resolves, which every caller treats as "no
// live device read".
uint32_t ResolveDrawRootDevicePointer(uint32_t symbol_address, uint32_t r3,
                                      uint8_t *guest_base) {
  constexpr uint32_t kD3DDevicePresentModule = 0x83360364u;
  if (refii::renderer::IsLikelyFableObjectAddress(r3)) {
    return r3;
  }
  if (guest_base == nullptr) {
    return 0u;
  }
  const uint32_t singleton =
      refii::renderer::bink::IsReadableGuestByteRange(
          guest_base, kD3DDevicePresentModule, sizeof(uint32_t))
          ? refii::renderer::bink::LoadGuestU32(guest_base,
                                                kD3DDevicePresentModule)
          : 0u;
  (void)symbol_address;
  return refii::renderer::IsLikelyFableObjectAddress(singleton) ? singleton
                                                                : 0u;
}

void BuildDrawPacketLocked(uint32_t symbol_address,
                           const RendererHookArgs &args, uint8_t *guest_base) {
  DrawPacket packet;
  packet.packet_id = g_state.next_packet_id++;
  t_last_packet_id = packet.packet_id;
  t_last_packet_symbol = symbol_address;
  t_last_packet_device = args.r3;
  t_last_packet_guest_base = guest_base;
  packet.frame_id = g_state.frame_id;
  // Guest-frame stamp for backlog supersession (run 485): the frame in
  // progress at this draw. g_state.mutex is already held (all packet builds
  // and the kPresent epoch increment run under it), so this costs nothing
  // and is exactly coherent with guest_frame_boundary_packet_id — no
  // per-draw acquisition of any renderer/direct-state mutex on the guest
  // thread.
  packet.guest_frame_ordinal =
      static_cast<uint32_t>(g_state.stated_present_epoch) + 1u;
  packet.source_symbol = symbol_address;
  packet.scene_submit_depth = g_state.scene_submit_depth;

  const DeviceState &device = g_state.device;
  packet.vertex_shader = device.vertex_shader;
  packet.pixel_shader = device.pixel_shader;
  packet.render_targets = device.render_targets;
  packet.depth_target = device.depth_target;

  // Backfill shaders / targets from the DEVICE OBJECT at the draw root.
  //
  // Why this is needed: g_state.device is fed by kSetVertexShader /
  // kSetPixelShader / kSetRenderTarget, and Fable barely calls them — run 132
  // logged them 2, 2 and 8 times against ~168 draws PER FRAME, because the
  // game stages state into the device object and flushes at draw time rather
  // than using the D3D setter API (docs/refii_render_path_map.md §2). The exec
  // shadow measured the cost of that: of 45 sampled packets only 15 were
  // executable, missing vs=23 ps=23 rt=17 — while vb/ib missed ZERO, i.e.
  // geometry was complete and only the staged state was absent.
  //
  // Why it is still call-stated and not inference: a draw root IS the flush
  // point. The device's current shader/target at this instant is exactly what
  // this call is about to draw with, so reading it here is reading what the
  // call itself states, in the same category as kSetScissorRect reading the
  // RECT through the pointer its argument names. Offsets are the ones the
  // observer already uses (hook_observer.cpp kFableDevice*Offset).
  //
  // Staged values win: only backfill a field the setters left at zero.
  ++g_state.stats.draw_packet_builds;
  if (packet.vertex_shader != 0) {
    ++g_state.stats.shader_from_device_state;
  }
  // NOT EVERY DRAW ROOT TAKES THE DEVICE IN r3 (IDA, 2026-08-02). The dominant
  // in-game root sub_8220A3F0 is the indexed UP helper
  // `(vertexCount, primCount, pIndices, pVerts, stride)` -- its r3 is a COUNT,
  // and it fetches the device from the g_D3DDevice_PresentModule singleton
  // itself (`v12 = g_D3DDevice_PresentModule` right before
  // D3DDevice_BeginIndexedVertices). It was 15,288 of the world frame's draws
  // in run 535, so every live device read below was addressed off a small
  // integer for the MAJORITY of gameplay draws: the reads silently failed the
  // mapped-range check and fell back to the tracked mirror. The constants
  // block further down already resolved the singleton for this root; do it
  // once here so the render-target / depth / shader / RB_DEPTHCONTROL reads
  // get the same treatment, and validate it either way.
  const uint32_t device_pointer = ResolveDrawRootDevicePointer(
      symbol_address, args.r3, guest_base);
  if (guest_base != nullptr && device_pointer != 0) {
    g_state.last_draw_device_pointer = device_pointer;
    if (packet.vertex_shader == 0) {
      ++g_state.stats.shader_backfill_attempted;
    }
    constexpr uint32_t kDevicePixelShaderOffset = 0x3194;
    constexpr uint32_t kDeviceVertexShaderOffset = 0x3198;
    constexpr uint32_t kDeviceRenderTargetBaseOffset = 0x3098;
    constexpr uint32_t kDeviceDepthStencilOffset = 0x30A8;
    const auto load = [&](uint32_t offset) -> uint32_t {
      return refii::renderer::bink::IsReadableGuestByteRange(
                 guest_base, device_pointer + offset, sizeof(uint32_t))
                 ? refii::renderer::bink::LoadGuestU32(
                       guest_base, device_pointer + offset)
                 : 0u;
    };
    if (packet.vertex_shader == 0) {
      packet.vertex_shader = load(kDeviceVertexShaderOffset);
      if (packet.vertex_shader != 0) {
        ++g_state.stats.shader_backfill_filled;
      }
    }
    if (packet.pixel_shader == 0) {
      packet.pixel_shader = load(kDevicePixelShaderOffset);
    }
    // Targets are LIVE-WINS, not zero-backfill (2026-08-02). "Staged values
    // win" assumed the mirror is either current or zero, but the shadow
    // renderer binds its per-light depth surface through the render-target-set
    // class (sub_8226B078 via sub_8228DCC8, IDA), which the hooked setters
    // never see: the mirror then holds the PREVIOUS pass's nonzero target and
    // the backfill never corrects it — routing shadow-pass draws at the wrong
    // target. By this block's own rationale the draw root is the flush point,
    // so the device register at this instant IS the call's stated target;
    // the mirror remains the fallback when the register is unreadable/zero.
    for (uint32_t rt = 0; rt < kMaxRenderTargets; ++rt) {
      const uint32_t live_target =
          load(kDeviceRenderTargetBaseOffset + rt * sizeof(uint32_t));
      if (live_target != 0) {
        packet.render_targets[rt] = live_target;
      }
    }
    const uint32_t live_depth = load(kDeviceDepthStencilOffset);
    if (live_depth != 0) {
      packet.depth_target = live_depth;
    }
    // RB_DEPTHCONTROL, verbatim (see DrawPacket::live_depth_control). Zero is
    // a legal value here (it means depth+stencil test disabled), so validity
    // rides the READ succeeding, never on the value being non-zero.
    constexpr uint32_t kDeviceDepthControlOffset = 0x2934;
    const uint32_t depth_control_address = device_pointer + kDeviceDepthControlOffset;
    if (refii::renderer::bink::IsReadableGuestByteRange(
            guest_base, depth_control_address, sizeof(uint32_t))) {
      packet.live_depth_control =
          refii::renderer::bink::LoadGuestU32(guest_base, depth_control_address);
      packet.live_depth_control_valid = true;
    }
  }
  packet.textures = device.textures;
  packet.vertex_buffers = device.vertex_buffers;
  packet.vertex_offsets = device.vertex_offsets;
  packet.vertex_strides = device.vertex_strides;
  packet.index_buffer = device.index_buffer;
  packet.vertex_declaration = device.vertex_declaration;
  packet.viewport = device.viewport;
  packet.scissor = device.scissor;
  packet.render_states = device.render_states;
  packet.render_state_valid_mask = device.render_state_valid_mask;
  packet.sampler_states = device.sampler_states;
  packet.sampler_state_valid_mask = device.sampler_state_valid_mask;
  packet.vertex_buffer_pointers = device.vertex_buffer_pointers;
  packet.index_buffer_pointer = device.index_buffer_pointer;
  packet.fetch_bases = device.fetch_bases;

  switch (symbol_address) {
    case kDrawPrimitive:
      packet.primitive_type = args.r4;
      packet.start_index = args.r5;
      packet.primitive_count = args.r6;
      break;
    case kDrawIndexedPrimitive:
      packet.indexed = true;
      packet.primitive_type = args.r4;
      packet.base_vertex = args.r5;
      packet.start_index = args.r6;
      packet.index_count = args.r7;
      break;
    case kDrawPrimitiveUP:
      packet.draw_up = true;
      packet.primitive_type = args.r4;
      packet.primitive_count = args.r5;
      packet.up_vertex_data = args.r6;
      packet.up_vertex_stride = args.r7;
      break;
    case kBeginVertices:
      // IDA-pinned (sub_82205F68): (device r3, PrimitiveType r4,
      // VertexCount r5, VertexStreamZeroStride r6) — the ring allocates
      // (VertexCount*stride)>>2 dwords and VertexCount shifts into the draw
      // initiator. The wrapper RETURNS the ring write pointer (exit r3),
      // pinned at the next hook entry.
      packet.draw_up = true;
      packet.primitive_type = args.r4;
      packet.primitive_count = args.r5;
      packet.up_vertex_stride = args.r6;
      break;
    case kBeginIndexedVertices:
      // SEPARATE ARG MAP — do NOT fold this back into the kBeginVertices arm.
      // These two roots share nothing past r4. Sharing one case (until
      // 2026-07-25) put BaseVertexIndex in primitive_count, NumVertices in
      // up_vertex_stride, and dropped IndexCount / index format / the real
      // vertex stride on the floor.
      //
      // IDA-verified (sub_82217DB8, decompile + disasm 2026-07-25):
      //   r3  pDevice              r4  PrimitiveType     r5  BaseVertexIndex
      //   r6  NumVertices          r7  IndexCount        r8  IndexDataFormat
      //   r9  VertexStreamZeroStride
      //   r10 ppIndexData [out]    caller_sp+0x54 ppVertexData [out]
      // Body proof of the three load-bearing roles:
      //   * `(NumVertices * VertexStreamZeroStride) >> 2` (r6*r9, @0x82217DC8/
      //     0x82217DE4) sizes the VERTEX ring allocation.
      //   * `IndexDataFormat & 4` (@0x82217DE0) selects 32-bit vs 16-bit
      //     indices — it ORs 0x800 into the draw initiator (Xenos INDEX_SIZE).
      //   * the INDEX ring allocation is IndexCount dwords when 32-bit, else
      //     `(IndexCount + 1) >> 1` (@0x82217DF8).
      // IndexCount is also the value that shifts into the draw initiator's
      // num_indices field (`v39 = PrimitiveType & 0x3F | (IndexCount << 16)`).
      //
      // primitive_count keeps its documented MISNOMER meaning (a VERTEX count,
      // see the field comment in refii_gtl.h) — NumVertices is exactly that,
      // and it is what the vertex-payload byte math needs.
      packet.draw_up = true;
      packet.indexed = true;
      packet.primitive_type = args.r4;
      packet.base_vertex = args.r5;
      packet.primitive_count = args.r6;
      packet.index_count = args.r7;
      packet.index_format = args.r8;
      packet.up_vertex_stride = args.r9;
      // The OUT-PARAM ADDRESSES, not the rings — the callee has not written
      // them yet at enter. Stashed for the exit pin (see the thread_locals'
      // comment for why they are unrecoverable at exit).
      t_begin_indexed_index_out_address = args.r10;
      t_begin_indexed_vertex_out_address =
          args.r1 != 0 ? args.r1 + kBeginIndexedVerticesVertexOutStackOffset
                       : 0u;
      break;
    // kResolve DELETED 2026-08-04 with the legacy resolve DrawPacket build in
    // OnHookEnter. Its three assignments were known-meaningless under the
    // corrected arg map (r4 is Flags, r5 is pSourceRect — a resolve has no
    // primitive type and no vertex data), and BuildDrawPacketLocked is no
    // longer reached with this symbol. Read ResolvePacket instead.
    case kDrawIndexedUserPrimitives: {
      // (vertex_count=r3, prim_count=r4, index_ptr=r5, vertex_ptr=r6,
      // stride=r7); triangle list, 16-bit indices (6 bytes per primitive).
      packet.draw_up = true;
      packet.indexed = true;
      packet.primitive_type = 4;  // D3DPT_TRIANGLELIST
      packet.primitive_count = args.r4;
      packet.index_count = args.r4 * 3u;
      packet.up_vertex_count = args.r3;
      packet.up_vertex_data = args.r6;
      packet.up_vertex_stride = args.r7;
      packet.up_index_data = args.r5;
      const uint64_t vertex_bytes = uint64_t(args.r3) * uint64_t(args.r7);
      const uint64_t index_bytes = uint64_t(args.r4) * 6u;
      constexpr uint64_t kMaxInlineGeometryBytes = 1u << 20;
      if (guest_base != nullptr && vertex_bytes != 0 &&
          vertex_bytes <= kMaxInlineGeometryBytes && index_bytes != 0 &&
          index_bytes <= kMaxInlineGeometryBytes &&
          refii::renderer::bink::IsReadableGuestByteRange(
              guest_base, args.r6, static_cast<uint32_t>(vertex_bytes)) &&
          refii::renderer::bink::IsReadableGuestByteRange(
              guest_base, args.r5, static_cast<uint32_t>(index_bytes))) {
        packet.inline_vertex_data.resize(vertex_bytes);
        std::memcpy(packet.inline_vertex_data.data(),
                    guest_base + args.r6 +
                        refii::renderer::bink::GuestPhysicalHostOffset(args.r6),
                    vertex_bytes);
        packet.inline_index_data.resize(index_bytes);
        std::memcpy(packet.inline_index_data.data(),
                    guest_base + args.r5 +
                        refii::renderer::bink::GuestPhysicalHostOffset(args.r5),
                    index_bytes);
      }
      break;
    }
    default:
      break;
  }

  // Stream-0 stride authority for the stride-carrying roots (UP draws and
  // Begin[Indexed]Vertices). The mirror copied above only advances on
  // SetStreamSource, but the guest treats the wrapper's stride argument as
  // stream 0's stride for exactly this draw: Begin*Vertices installs
  // (Stride>>2) into the device's per-slot stride byte (dev+0x30F0)
  // TRANSIENTLY — saved at 0x82205F88/0x82217E04, restored at
  // 0x822061EC/0x82218054 before returning — so every post-return read (ours
  // included) sees the PREVIOUS binding. Run 497: a stale 48-byte stride over
  // real 12-byte quad-list vertices inflated the bind requirement 4x
  // (missing_vertex_input on the draws allocated near the arena top) and
  // mis-strode the survivors. The argument is the same value the guest
  // patches into the VS microcode vfetch for this draw; present it as the
  // stream-0 stride.
  if (packet.draw_up && packet.up_vertex_stride != 0) {
    packet.vertex_strides[0] = packet.up_vertex_stride;
  }

  // Constants capture (§3.2 self-containment): the device object holds the
  // at-draw float banks; function-level interception means they are complete
  // here. The 8220A3F0 wrapper carries no device argument — its callers use
  // the device singleton at 0x83360364.
  {
    constexpr uint32_t kFloatConstantDwords = 1024;
    constexpr uint32_t kVertexBankOffset = 120u * 16u;
    constexpr uint32_t kPixelBankOffset = 376u * 16u;
    // Same resolution as the live-read block above: r3 when it really is the
    // device, else the g_D3DDevice_PresentModule singleton. Previously this
    // special-cased only kDrawIndexedUserPrimitives, so any OTHER root whose
    // r3 is not the device fell through to reading constants off a non-pointer
    // -- and a failed read leaves the bank vector EMPTY, which downstream is
    // indistinguishable from "no constants" and lands as a zero matrix.
    const uint32_t device =
        ResolveDrawRootDevicePointer(symbol_address, args.r3, guest_base);
    // Vertex declaration authority (§3.2): the bound decl lives in the device
    // object (offset 11820), which the observer reads directly in
    // RefreshDeviceInputBindingsLocked. Hook-tracked device.vertex_declaration
    // only sees XGSetVertexDeclaration and misses the shader-patch bind path
    // (the inline-UP / Bink-compose decl), leaving it stale/wrong. Read the
    // live device register here — the same at-draw source the observer trusts —
    // and let it win when present. Guarded non-zero so a transient read never
    // clobbers a validly tracked decl.
    if (guest_base != nullptr && device != 0) {
      constexpr uint32_t kDeviceVertexDeclarationOffset = 11820u;
      const uint32_t live_declaration = refii::renderer::bink::LoadGuestU32(
          guest_base, device + kDeviceVertexDeclarationOffset);
      if (live_declaration != 0) {
        packet.vertex_declaration = live_declaration;
      }
    }
    const auto read_bank = [&](uint32_t bank_offset,
                               std::vector<uint32_t>& out) {
      if (guest_base == nullptr || device == 0) {
        return;
      }
      const uint32_t address = device + bank_offset;
      const uint32_t bytes = kFloatConstantDwords * 4u;
      if (!refii::renderer::bink::IsReadableGuestByteRange(guest_base, address,
                                                           bytes)) {
        return;
      }
      const uint8_t* block =
          guest_base + address +
          refii::renderer::bink::GuestPhysicalHostOffset(address);
      out.resize(kFloatConstantDwords);
      for (uint32_t dword_index = 0; dword_index < kFloatConstantDwords;
           ++dword_index) {
        uint32_t raw = 0;
        std::memcpy(&raw, block + dword_index * 4u, sizeof(raw));
        out[dword_index] = __builtin_bswap32(raw);
      }
    };
    // Float-bank ENTER source (feed-7 deletion, 2026-08-02): with
    // renderer_gtl_constant_self_read ON (default) the DIRECT DEVICE READ is
    // preferred over the observer-fed mirror. The device block at
    // dev+0x780/0x1780 is the guest's own constant staging — the deferred PM4
    // SET_CONSTANT flush lands there, and it also holds writes NO hook feed
    // can see (sub_82214E30's direct VMX memcpy, the WVP-zero carrier), so it
    // is a superset of every fed carrier. The fed mirror remains the FALLBACK
    // when the device pointer/base is unusable at this root (e.g. the quad
    // wrapper's singleton read failing), and the authoritative EXIT self-read
    // still overwrites both banks post-flush regardless. Note the ENTER value
    // is pre-flush either way (previous draw's bank) — the cvar changes the
    // SOURCE, not the staleness class; the exit re-read is what makes the
    // packet correct, and the divergence census there grades fed-vs-device.
    const bool constant_self_read =
        refii::renderer::RendererGtlConstantSelfReadEnabled();
    if (constant_self_read) {
      read_bank(kVertexBankOffset, packet.vs_float_constants);
      if (packet.vs_float_constants.empty() &&
          g_state.device.vs_float_bank_valid) {
        packet.vs_float_constants.assign(g_state.device.vs_float_bank.begin(),
                                         g_state.device.vs_float_bank.end());
      }
    } else if (g_state.device.vs_float_bank_valid) {
      packet.vs_float_constants.assign(g_state.device.vs_float_bank.begin(),
                                       g_state.device.vs_float_bank.end());
    } else {
      read_bank(kVertexBankOffset, packet.vs_float_constants);
    }
    // c0-c3 (WorldViewProjection) live-device-memory read for content that
    // needs it (2026-07-25, IDA-traced to sub_82214E30, a direct VMX memcpy
    // into device memory that bypasses PM4 SET_CONSTANT entirely -- see
    // refii-tree-billboard-actual-root-cause-wvp-zero.md). REVERTED here as
    // a blanket override for every kDrawIndexedUserPrimitives (0x8220A3F0)
    // draw: that root is shared by ~26 content classes (Bink, UI panels,
    // fullscreen composites, tree billboards, ...), most of which get a
    // CORRECT (often intentionally different, e.g. orthographic/identity)
    // transform via the normal PM4 path and don't read this device-memory
    // slot at all. Broadcasting one live read to all of them produced a
    // solid white screen (run 258, 2026-07-25): every consumer got
    // whichever unrelated content's matrix happened to be sitting there
    // last. The fix must be scoped to the specific vertex shader identity
    // proven to need it (0097_vshader_water_tessellator_zpass /
    // kFableTitleWaterTessellatorZPassVertexHash), which this GTL-layer
    // capture code cannot check (shader-hash classification lives in
    // fable_direct_renderer.cpp). Apply the override there instead, at
    // packet->command conversion time, alongside the existing tree-billboard
    // texture-slot override -- captured unconditionally into a SEPARATE
    // field (live_vertex_c0_c3) here so the converter can opt in per-shader
    // instead of this layer having to (and getting it wrong).
    if (symbol_address == kDrawIndexedUserPrimitives && guest_base != nullptr &&
        device != 0) {
      constexpr uint32_t kWorldViewProjectionDwords = 16;
      const uint32_t address = device + kVertexBankOffset;
      if (refii::renderer::bink::IsReadableGuestByteRange(
              guest_base, address, kWorldViewProjectionDwords * 4u)) {
        const uint8_t* block =
            guest_base + address +
            refii::renderer::bink::GuestPhysicalHostOffset(address);
        for (uint32_t dword_index = 0; dword_index < kWorldViewProjectionDwords;
             ++dword_index) {
          uint32_t raw = 0;
          std::memcpy(&raw, block + dword_index * 4u, sizeof(raw));
          packet.live_vertex_c0_c3[dword_index] = __builtin_bswap32(raw);
        }
        packet.live_vertex_c0_c3_valid = true;
      }
    }
    if (constant_self_read) {
      read_bank(kPixelBankOffset, packet.ps_float_constants);
      if (packet.ps_float_constants.empty() &&
          g_state.device.ps_float_bank_valid) {
        packet.ps_float_constants.assign(g_state.device.ps_float_bank.begin(),
                                         g_state.device.ps_float_bank.end());
      }
    } else if (g_state.device.ps_float_bank_valid) {
      packet.ps_float_constants.assign(g_state.device.ps_float_bank.begin(),
                                       g_state.device.ps_float_bank.end());
    } else {
      read_bank(kPixelBankOffset, packet.ps_float_constants);
    }
    // SHADER-LITERAL OVERLAY. The device banks read above are only half the
    // constant file: D3D_SetLiteralShaderConstants loads the bound shader's
    // embedded literals straight into the GPU's ALU registers by PM4 (type-3
    // opcode 0x2F), never touching dev+0x780/0x1780, and it runs AFTER the
    // shadow flush in the same draw root -- so for the registers it covers,
    // the literal is what the hardware samples. Apply it on top, in that
    // order. Registers no literal has written keep the device value.
    // See ApplyLiteralShaderConstants; this is what un-zeroed the
    // WorldViewProjection for the literal-driven draws.
    {
      const auto& covered = g_state.device.literal_alu_covered;
      const auto& literal = g_state.device.literal_alu;
      const auto overlay = [&](std::vector<uint32_t>& bank,
                               uint32_t register_base) {
        if (bank.empty()) {
          return;
        }
        for (uint32_t slot = 0; slot < 256u; ++slot) {
          const uint32_t reg = register_base + slot;
          if (covered[reg] == 0) {
            continue;
          }
          const size_t dword = static_cast<size_t>(slot) * 4u;
          if (dword + 4u > bank.size()) {
            break;
          }
          for (uint32_t component = 0; component < 4u; ++component) {
            bank[dword + component] =
                literal[static_cast<size_t>(reg) * 4u + component];
          }
        }
      };
      overlay(packet.vs_float_constants, 0u);
      overlay(packet.ps_float_constants, 256u);
    }
    // CONSTANT-BANK FORENSICS (2026-08-02). Both device banks read all-zero
    // for every draw class -- UI quads AND world terrain -- so geometry
    // collapses (post-VS clip=(0,0,0,0)). The device pointer is provably good
    // (RT0 at +0x3098 and RB_DEPTHCONTROL at +0x2934 read correct values from
    // it), so this asks the next question directly: is the WHOLE 256-register
    // bank empty (nobody wrote it / wrote elsewhere) or just c0-c3 (in-use
    // gating, or a literal carrier we still miss)? Also reports the guest's
    // own pending dirty mask -- non-zero there means the guest believes it has
    // constants staged for the flush -- and how many registers our literal
    // overlay has claimed.
    {
      static std::atomic<uint32_t> logged_bank_forensics{0};
      const uint32_t n = ++logged_bank_forensics;
      if (n <= 24u && guest_base != nullptr && device != 0) {
        uint32_t vs_nonzero = 0;
        uint32_t vs_first_nonzero_reg = 0xFFFFFFFFu;
        for (uint32_t dword = 0; dword < packet.vs_float_constants.size();
             ++dword) {
          if (packet.vs_float_constants[dword] != 0) {
            ++vs_nonzero;
            if (vs_first_nonzero_reg == 0xFFFFFFFFu) {
              vs_first_nonzero_reg = dword / 4u;
            }
          }
        }
        uint32_t ps_nonzero = 0;
        for (const uint32_t value : packet.ps_float_constants) {
          if (value != 0) {
            ++ps_nonzero;
          }
        }
        const auto load_dword = [&](uint32_t offset) -> uint32_t {
          const uint32_t at = device + offset;
          return refii::renderer::bink::IsReadableGuestByteRange(
                     guest_base, at, sizeof(uint32_t))
                     ? refii::renderer::bink::LoadGuestU32(guest_base, at)
                     : 0xDEADBEEFu;
        };
        REXLOG_INFO(
            "Refii GTL const forensics #{} src=0x{:08X} device=0x{:08X} "
            "vs_nonzero={}/{} first_nz_reg={} ps_nonzero={}/{} "
            "pending_mask0=0x{:08X}{:08X} pending_mask1=0x{:08X}{:08X} "
            "literal_regs={} vs=0x{:08X} ps=0x{:08X}",
            n, symbol_address, device, vs_nonzero,
            static_cast<uint32_t>(packet.vs_float_constants.size()),
            vs_first_nonzero_reg, ps_nonzero,
            static_cast<uint32_t>(packet.ps_float_constants.size()),
            load_dword(0x0u), load_dword(0x4u), load_dword(0x8u),
            load_dword(0xCu),
            static_cast<unsigned long long>(
                g_state.device.literal_alu_registers_written),
            packet.vertex_shader, packet.pixel_shader);
      }
    }
    // WVP-ZERO TRACE (2026-07-25): the quad-wrapper root (0x8220A3F0 /
    // kDrawIndexedUserPrimitives) is used by ~26 different content classes
    // (Bink, UI, tree billboards, procedural primitives, ...) sharing this
    // ONE capture path and the ONE global vs_float_bank. Tree-billboard
    // draws' actual GPU-side WorldViewProjection (registers c0-c3, dwords
    // 0-15) was proven all-zero at Vulkan-submission time (live capture,
    // see refii-tree-billboard-actual-root-cause-wvp-zero.md). This log
    // answers whether it's ALREADY zero right here at GTL capture time
    // (an observer/PM4-decode gap -- some other 0x8220A3F0 caller stomped
    // the global bank before this draw) or still correct here (meaning the
    // bug is downstream, in the packet->command->Vulkan pipeline instead).
    if (symbol_address == kDrawIndexedUserPrimitives) {
      static uint32_t logged_quad_wvp_trace = 0;
      if (logged_quad_wvp_trace < 64u) {
        ++logged_quad_wvp_trace;
        const auto& wvp = packet.vs_float_constants;
        const bool have16 = wvp.size() >= 16u;
        // Bank-identity probe (2026-08-02): the VS bank read here (dev+0x780,
        // vector index 120, written by sub_8221A880) is all-zero for these
        // draws, so their WorldViewProjection is a zero matrix and every
        // vertex collapses to clip=(0,0,0,0) -- RenderDoc post-VS confirms
        // ndc=inf and pixel history finds no draw covering any pixel. The
        // other candidate is dev+0x1780 (index 376, written by sub_82214E30),
        // which the engine's own Render_SetShaderConstantMatrixById
        // (0x82287478) uses for MATRIX constants by binding id. Print the head
        // of BOTH banks so one run names the bank that actually carries the
        // matrix, instead of flipping the source on a guess -- broadcasting an
        // unverified c0-c3 override is exactly what produced the solid white
        // screen in run 258.
        const auto& ps_bank = packet.ps_float_constants;
        const bool ps_have8 = ps_bank.size() >= 8u;
        REXLOG_INFO(
            "Refii GTL quad-wrapper bank probe #{} other_bank(0x1780) "
            "c0=({},{},{},{}) c1=({},{},{},{})",
            logged_quad_wvp_trace,
            ps_have8 ? std::bit_cast<float>(ps_bank[0]) : 0.0f,
            ps_have8 ? std::bit_cast<float>(ps_bank[1]) : 0.0f,
            ps_have8 ? std::bit_cast<float>(ps_bank[2]) : 0.0f,
            ps_have8 ? std::bit_cast<float>(ps_bank[3]) : 0.0f,
            ps_have8 ? std::bit_cast<float>(ps_bank[4]) : 0.0f,
            ps_have8 ? std::bit_cast<float>(ps_bank[5]) : 0.0f,
            ps_have8 ? std::bit_cast<float>(ps_bank[6]) : 0.0f,
            ps_have8 ? std::bit_cast<float>(ps_bank[7]) : 0.0f);
        REXLOG_INFO(
            "Refii GTL quad-wrapper WVP trace #{} vs_float_bank_valid={} "
            "ps=0x{:08X} vs=0x{:08X} c0=({},{},{},{}) c1=({},{},{},{})",
            logged_quad_wvp_trace, g_state.device.vs_float_bank_valid ? 1 : 0,
            packet.pixel_shader, packet.vertex_shader,
            have16 ? std::bit_cast<float>(wvp[0]) : 0.0f,
            have16 ? std::bit_cast<float>(wvp[1]) : 0.0f,
            have16 ? std::bit_cast<float>(wvp[2]) : 0.0f,
            have16 ? std::bit_cast<float>(wvp[3]) : 0.0f,
            have16 ? std::bit_cast<float>(wvp[4]) : 0.0f,
            have16 ? std::bit_cast<float>(wvp[5]) : 0.0f,
            have16 ? std::bit_cast<float>(wvp[6]) : 0.0f,
            have16 ? std::bit_cast<float>(wvp[7]) : 0.0f);
      }
    }

    // PHASE 1 (CAPTURE + COUNT ONLY): the device object's own state, read here
    // at the draw root's ENTRY. Must be at entry -- BeginVertices opens by
    // draining all five pending masks through sub_8221DE68, so by exit
    // m_Mask[0..4] are ALL ZERO and the per-draw delta is gone. Nothing
    // consumes device_snapshot; it exists so the divergence census can measure
    // how stale the mirrors above are before Phase 2 decides what to delete.
    packet.device_snapshot = CaptureDrawDeviceSnapshot(device, guest_base);
    DrainDeviceDirtyMasksLocked(device, guest_base);

    // PHASE 2 CUTOVER (FETCH CLASS ONLY -- nothing else in this packet changes
    // source). Run 284 measured the mirror wrong on 10420 of 12244 compared
    // slots (85%); the mirror is a hook-fed cache that only learns a slot when
    // D3D_SetPending_FetchConstants fires with that slot dirty, while
    // device+0x480 is the storage D3DDevice_SetTexture writes directly. Take
    // the device.
    //
    // EXCLUDED: kDrawIndexedUserPrimitives (sub_8220A3F0). That root runs
    // Render_FlushDirtyRenderStates() as its first statement, so its ENTRY
    // fetch block is the previous draw's -- the classic stale-by-one read this
    // cutover must not introduce. It already re-reads the same block at EXIT
    // (CaptureDeviceFetchConstantsIntoPacketLocked in OnHookExit), which is the
    // correct instant for it, and that path is left exactly as it was.
    //
    // ALSO EXCLUDED: kResolve. sub_82206888 is an EDRAM resolve that reaches
    // this function only through the legacy additive DrawPacket build; it has
    // no texture bindings to source and setting fetch_dwords_valid on it would
    // be a change outside the fetch-binding class this cutover is scoped to.
    const bool draw_root_for_fetch_cutover =
        symbol_address == kBeginVertices ||
        symbol_address == kBeginIndexedVertices ||
        symbol_address == kDrawPrimitive ||
        symbol_address == kDrawIndexedPrimitive ||
        symbol_address == kDrawPrimitiveUP;
    if (FetchDeviceSourced() && draw_root_for_fetch_cutover) {
      ApplyDeviceSourcedFetchToPacket(packet);
      ++g_divergence_census.fetch_device_sourced_draws;
    } else if (symbol_address == kDrawIndexedUserPrimitives) {
      ++g_divergence_census.fetch_preflush_root_draws;
    }
    AccumulateDeviceSnapshotDivergenceLocked(packet);
  }

  // Per-family packet-completeness census (staged observer-removal aid): for
  // each draw-root family, does the GTL packet carry the data needed to OWN
  // the draw (geometry + identity), i.e. is the observer's handling of this
  // family redundant? Accumulated under g_state.mutex; logged in
  // OnFrameBoundary. geom = inline UP bytes present; streamvb = a bound vertex
  // buffer; ident = both shaders set; decl = vertex declaration set.
  {
    FamilyCoverage& cov = g_family_coverage[symbol_address];
    ++cov.total;
    if (!packet.inline_vertex_data.empty() || packet.up_vertex_count != 0) {
      ++cov.geom;
    }
    if (packet.vertex_buffers[0] != 0) {
      ++cov.streamvb;
    }
    if (packet.vertex_shader != 0 && packet.pixel_shader != 0) {
      ++cov.ident;
    }
    if (packet.vertex_declaration != 0) {
      ++cov.decl;
    }
    if (packet.textures[0] != 0) {
      ++cov.tex0;
    }
    if (packet.render_targets[0] != 0) {
      ++cov.rt0;
    }
  }

  if (packet.vertex_shader == 0) {
    ++g_state.stats.shader_still_absent;
    // Name the root that produced a shaderless packet — if these cluster on
    // one source_symbol then args.r3 is not the device there and the backfill
    // never had a chance, which is a different fix from a timing problem.
    static std::unordered_map<uint32_t, uint32_t> absent_by_symbol;
    static uint32_t logged_absent_symbols = 0;
    const uint32_t seen = ++absent_by_symbol[packet.source_symbol];
    if (seen == 1u && logged_absent_symbols < 16u) {
      REXLOG_INFO(
          "Refii GTL packet has no vertex shader (distinct root #{}) "
          "source_symbol=0x{:08X} device_arg=0x{:08X} rt0=0x{:08X}",
          ++logged_absent_symbols, packet.source_symbol, args.r3,
          packet.render_targets[0]);
    }
  }
  g_state.packets.push_back(packet);
  ++g_state.stats.draw_packets;
  if (g_state.packets.size() > 65536u) {
    // A consumer stall must not grow unbounded; drop oldest.
    g_state.packets.erase(g_state.packets.begin(),
                          g_state.packets.begin() + 32768u);
  }
}

// Second packet class: sub_82206888 is D3DDevice_Resolve — a Xenos EDRAM
// resolve, not a vertex draw and not a blit (it emits its own PM4 quad, which
// is why the census sees ident=0, geom=0). Record the op verbatim from the
// wrapper args + tracked device targets.
//
// Arg map IDA-proven 2026-07-17, SUPERSEDING the 2026-07-14 map which was
// inverted (it read r6 as a source texture — it is the DESTINATION; r7 as a
// 4-int source rect — it is a 2-int D3DPOINT; r5 as a dest rect — it is the
// SOURCE rect):
//
//   r4 Flags | r5 pSourceRect (LTRB, RT space) | r6 pDestTexture
//   r7 pDestPoint {x,y} | r8 DestLevel | r9 DestSliceOrFace | r10 pClearColor
//
// The source is EDRAM, named by Flags&7 (0-3 = RT0..3, 4 = DS) — there is no
// source texture — so it comes from the tracked device binding, not an arg.
void BuildResolvePacketLocked(uint32_t symbol_address,
                              const RendererHookArgs &args,
                              uint8_t *guest_base) {
  ResolvePacket packet;
  packet.packet_id = g_state.next_packet_id++;
  packet.frame_id = g_state.frame_id;
  packet.source_symbol = symbol_address;
  packet.flags = args.r4;
  packet.dest_targets = g_state.device.render_targets;
  packet.dest_depth = g_state.device.depth_target;

  const auto read_dwords = [&](uint32_t ptr, uint32_t *out, uint32_t count) {
    if (guest_base == nullptr || ptr == 0 ||
        !refii::renderer::bink::IsReadableGuestByteRange(guest_base, ptr,
                                                         count * 4u)) {
      return false;
    }
    for (uint32_t field = 0; field < count; ++field) {
      out[field] =
          refii::renderer::bink::LoadGuestU32(guest_base, ptr + field * 4u);
    }
    return true;
  };

  // Source: the EDRAM surface Flags&7 names, from the DEVICE's own binding
  // registers, mirror as fallback. The hook mirror only advances on the
  // public SetRenderTarget/SetDepthStencilSurface roots, but the shadow
  // renderer binds its per-light depth surface through the render-target-set
  // class (sub_8226B078 via sub_8228DCC8, IDA 2026-08-01), which the mirror
  // never sees — so mirror-sourced packets named a stale surface for exactly
  // the passes whose resolves were being dropped (run 497). The resolve
  // itself reads the device state for its EDRAM source, so the live register
  // is the authority at this instant; same precedent as the live
  // vertex-declaration read in the draw-packet build above.
  packet.source_select = args.r4 & 7u;
  packet.source_is_depth = packet.source_select == 4u;
  {
    constexpr uint32_t kDeviceRenderTargetBaseOffset = 0x3098;
    constexpr uint32_t kDeviceDepthStencilOffset = 0x30A8;
    uint32_t live_source = 0;
    if (guest_base != nullptr && args.r3 != 0 &&
        (packet.source_is_depth || packet.source_select < kMaxRenderTargets)) {
      const uint32_t offset =
          packet.source_is_depth
              ? kDeviceDepthStencilOffset
              : kDeviceRenderTargetBaseOffset +
                    packet.source_select * static_cast<uint32_t>(sizeof(uint32_t));
      live_source =
          refii::renderer::bink::LoadGuestU32(guest_base, args.r3 + offset);
    }
    if (live_source != 0) {
      packet.source_surface = live_source;
    } else if (packet.source_is_depth) {
      packet.source_surface = g_state.device.depth_target;
    } else if (packet.source_select < kMaxRenderTargets) {
      packet.source_surface =
          g_state.device.render_targets[packet.source_select];
    }
  }
  if (packet.source_surface == 0) {
    ++g_state.stats.resolve_unbound_source;
  }

  // r5: source rect, LTRB. NULL is the ABI default meaning "full surface".
  packet.source_rect_default = args.r5 == 0;
  packet.source_rect_valid =
      !packet.source_rect_default &&
      read_dwords(args.r5, packet.source_rect.data(), 4u);

  // r6: destination texture header — the WRITE target.
  packet.dest_texture = args.r6;
  auto it = g_state.resources.find(args.r6);
  if (it != g_state.resources.end()) {
    packet.dest_registry_object = it->second.guest_address;
    // The memory the header NAMES right now, straight from the registry's
    // record of XGOffsetBaseTextureAddress. Recorded, never derived — this is
    // the destination's only durable identity (the object address is not one:
    // pooled headers get re-pointed).
    packet.dest_base_address = it->second.base_address;
    packet.dest_mip_address = it->second.mip_address;
    packet.dest_address_generation = it->second.address_generation;
  } else if (args.r6 != 0) {
    ++g_state.stats.resolve_unregistered_dest;
    // Dump what the registry ACTUALLY holds when a destination misses. Run 441
    // produced a contradiction that inference cannot settle: the observer shows
    // XGOffsetBaseTextureAddress touching 0x40137C90/0x40137CD0, which ARE
    // resolve destinations, and the GTL saw 618 of those events with every
    // object already registered — yet every destination lookup here misses.
    // Both cannot be true, so print the keys rather than reason about them.
    static uint32_t logged_dest_miss = 0;
    if (logged_dest_miss < 12u) {
      std::string sample;
      uint32_t shown = 0;
      for (const auto &entry : g_state.resources) {
        if (shown++ >= 8u) {
          break;
        }
        sample += fmt::format("{:08X} ", entry.first);
      }
      REXLOG_INFO(
          "Refii GTL resolve dest MISS #{} r6=0x{:08X} registry_size={} "
          "keys=[{}]",
          ++logged_dest_miss, args.r6, g_state.resources.size(), sample);
    }
  }
  // The destination's fetch constant, at the same offset the resolve reads it
  // from (pDestTexture+28..+48) for its tiled-base math. Under the old map this
  // was labelled the source's and its registry misses were explained away as
  // "private-texture proxy headers" — that claim rested on the inverted map and
  // is being re-tested via stats.resolve_unregistered_dest, not inherited.
  constexpr uint32_t kDestTextureFetchOffset = 28u;
  const uint32_t dest_fetch_address = args.r6 + kDestTextureFetchOffset;
  if (args.r6 != 0 && refii::renderer::IsLikelyFableObjectAddress(args.r6) &&
      read_dwords(dest_fetch_address, packet.dest_fetch_dwords.data(), 6u)) {
    for (uint32_t dword : packet.dest_fetch_dwords) {
      packet.dest_fetch_valid |= dword != 0;
    }
  }
  if (!packet.dest_fetch_valid) {
    ++g_state.stats.resolve_no_dest_fetch;
  }

  // r7: dest point {x,y} — TWO ints. NULL is the ABI default meaning {0,0}.
  packet.dest_point_default = args.r7 == 0;
  packet.dest_point_valid = !packet.dest_point_default &&
                            read_dwords(args.r7, packet.dest_point.data(), 2u);
  packet.dest_level = args.r8;
  packet.dest_slice_or_face = args.r9;

  // Clears ride the same call: EndTiling ORs 0x100/0x200 in on every non-final
  // tile to prime EDRAM for the next one, which is why flags&0x300 is common
  // (the old map misread that 0x300 as "batched/RT-select bits").
  if ((packet.flags & 0x100u) != 0) {
    packet.clear_color_valid =
        read_dwords(args.r10, packet.clear_color.data(), 4u);
  }
  if ((packet.flags & 0x200u) != 0) {
    packet.clear_z = static_cast<float>(args.f1);
    // ClearStencil is a STACK arg. The Xbox 360 ABI homes parameters in 8-byte
    // slots from caller_sp+0x10, so ClearZ (slot 8) sits in f1 and ClearStencil
    // (slot 9) lands at caller_sp+0x5C — verified against the resolve's own
    // read (`lwz r6, 0x160+arg_5C(r1)`) and its callers' stores. r1 at hook
    // entry is the caller's sp (pre-prologue). pParameters is at +0x64.
    constexpr uint32_t kClearStencilStackOffset = 0x5Cu;
    uint32_t stencil = 0;
    if (args.r1 != 0 &&
        read_dwords(args.r1 + kClearStencilStackOffset, &stencil, 1u)) {
      packet.clear_stencil = stencil;
    }
  }

  static uint32_t logged_resolve_probe = 0;
  if (logged_resolve_probe < 96u) {
    // dest_fetch is the destination's identity: the header object at r6 is
    // REUSED SCRATCH (run 437: one header carried fetch_base 0x12AE1000 and
    // 0x12CFD000, sizes 207360/829440, fmts 6/7/54), so its address names
    // nothing durable and dest_reg is expected to stay 0. The fetch constant
    // names the console memory this resolve writes into — print it raw so the
    // consumer's fetch-keyed lookup can be checked against the observer's own
    // fetch_base line for the same call.
    REXLOG_INFO(
        "Refii GTL resolve probe #{} flags=0x{:08X} sel={}{} src_surf=0x{:08X} "
        "src_rect=({},{},{},{}){} dest_tex=0x{:08X} dest_reg=0x{:08X} "
        "dest_pt=({},{}){} level={} slice={} dest_fetch_valid={} "
        "dest_fetch=[{:08X} {:08X} {:08X} {:08X} {:08X} {:08X}]",
        ++logged_resolve_probe, packet.flags, packet.source_select,
        packet.source_is_depth ? "(DS)" : "(RT)", packet.source_surface,
        packet.source_rect[0], packet.source_rect[1], packet.source_rect[2],
        packet.source_rect[3], packet.source_rect_default ? "[full]" : "",
        packet.dest_texture, packet.dest_registry_object, packet.dest_point[0],
        packet.dest_point[1], packet.dest_point_default ? "[0,0]" : "",
        packet.dest_level, packet.dest_slice_or_face,
        packet.dest_fetch_valid ? 1 : 0, packet.dest_fetch_dwords[0],
        packet.dest_fetch_dwords[1], packet.dest_fetch_dwords[2],
        packet.dest_fetch_dwords[3], packet.dest_fetch_dwords[4],
        packet.dest_fetch_dwords[5]);
  }

  // Mark the destination as GPU-produced. §4 reads `observer_only` as "content
  // the GTL is missing", but a resolve destination has NO CPU bytes by
  // construction — the content is a host RT→texture copy — so counting it as a
  // content gap overstates the gap and would argue for keeping observer
  // capture machinery that is answering a question nobody should be asking.
  // Recorded against both the stated destination and the registry identity,
  // because the two differ whenever Create never registered the raw header.
  for (const uint32_t key : {packet.dest_registry_object, packet.dest_texture}) {
    if (key == 0) {
      continue;
    }
    auto dest_it = g_state.resources.find(key);
    if (dest_it != g_state.resources.end()) {
      ++dest_it->second.gpu_content_generation;
      MarkResourceChangedLocked(key);
      break;
    }
  }

  // Reproduce the guest's own description of the destination, from the two
  // inputs it derives them from — the Flags argument and the destination
  // texture header. NOT read back from the RB_COPY_* device shadow: the resolve
  // zeroes that shadow before returning, so it reads zero at every hook-visible
  // moment (run 266 measured all four dwords zero on every resolve). See
  // docs/refii_resolve_fields.md §3.1.
  //
  // This is the field the host-copy path should be comparing against, instead
  // of Refii's own tracked destination format — the "fmt MISMATCH" refusals are
  // a comparison between two Refii-side plume enums, not against what the guest
  // actually asked for.
  if (packet.dest_fetch_valid) {
    const uint32_t dest_texture_format =
        ResolveDestTextureFormat(packet.dest_fetch_dwords);
    packet.copy_dest_format = ResolveCopyDestFormat(dest_texture_format);
    packet.copy_dest_number =
        ResolveCopyDestFormatUsesFloatNumber(packet.copy_dest_format) ? 7u : 0u;
    packet.copy_control = ResolveCopyControlFromFlags(packet.flags);
    packet.copy_sample_select = ResolveSampleIndexFromFlags(packet.flags);
    packet.copy_dest_exp_bias = ResolveExpBiasFromFlags(packet.flags);
    packet.copy_fields_valid = true;

    // One line per DISTINCT stated description, not per resolve: the same few
    // passes repeat every frame and a flat cap would fill with the first pass
    // before a storybook/menu resolve ever ran (the log-cap trap from
    // refii-gtl-draw-ownership).
    static std::unordered_map<uint32_t, uint32_t> logged_copy_fields;
    const uint32_t key = (dest_texture_format << 16) |
                         (packet.copy_dest_format << 8) |
                         packet.copy_sample_select;
    if (logged_copy_fields.size() < 64u &&
        logged_copy_fields.try_emplace(key, 0u).second) {
      REXLOG_INFO(
          "Refii GTL resolve COPY-FIELDS (distinct #{}) dest_tex=0x{:08X} "
          "sel={}{} | tex_fmt={} -> copy_fmt={} number={} exp_bias={} | "
          "frag={} sample={} clear[c={} d={}] | raw_ctl=0x{:08X} "
          "flags=0x{:08X} dest_fetch1=0x{:08X}",
          static_cast<uint32_t>(logged_copy_fields.size()), packet.dest_texture,
          packet.source_select, packet.source_is_depth ? "(DS)" : "(RT)",
          dest_texture_format, packet.copy_dest_format, packet.copy_dest_number,
          packet.copy_dest_exp_bias,
          ResolveFragmentSelectFromFlags(packet.flags),
          packet.copy_sample_select,
          ResolveClearsColorFromFlags(packet.flags) ? 1 : 0,
          ResolveClearsDepthFromFlags(packet.flags) ? 1 : 0,
          packet.copy_control, packet.flags, packet.dest_fetch_dwords[1]);
    }
  }

  g_state.resolve_packets.push_back(packet);
  ++g_state.stats.resolve_packets;
  if (g_state.resolve_packets.size() > 65536u) {
    constexpr size_t kTrimCount = 32768u;
    g_state.resolve_packets.erase(g_state.resolve_packets.begin(),
                                  g_state.resolve_packets.begin() + kTrimCount);
  }
}

// Clear capture (§3.4/step-5 submission cutover, clear class). Register mapping
// mirrors the observer's two QueueClearEventLocked call sites verbatim so the
// GTL clear count is directly comparable (census gate before submission owns
// clears). Records only what the wrapper states + the tracked RT/DS binding.
void BuildClearPacketLocked(uint32_t symbol_address,
                            const RendererHookArgs &args, uint8_t *guest_base) {
  ClearPacket packet;
  packet.packet_id = g_state.next_packet_id++;
  packet.frame_id = g_state.frame_id;
  packet.source_symbol = symbol_address;

  uint32_t color_pointer = 0;
  if (symbol_address == kClearF) {
    packet.flags = args.r4;
    color_pointer = args.r6;
    packet.stencil = args.r8;
    // r5 = optional D3DRECT pointer, LTRB int dwords.
    if (guest_base != nullptr && args.r5 != 0 &&
        refii::renderer::bink::IsReadableGuestByteRange(guest_base, args.r5,
                                                        16u)) {
      for (uint32_t i = 0; i < 4u; ++i) {
        packet.rect[i] = static_cast<int32_t>(
            refii::renderer::bink::LoadGuestU32(guest_base,
                                                args.r5 + i * 4u));
      }
      packet.rect_valid = true;
    }
  } else {  // kClearRect: color-slot-0 fill; depth/stencil owned by ClearF.
    packet.flags = 1u;
    color_pointer = args.r8;
    packet.stencil = 0;
    // r4-r7 = LEFT/TOP/RIGHT/BOTTOM — decompile-proven (D3D_ClearRect's tile
    // math treats r4..r6 as left..right and r5..r7 as top..bottom, and its
    // quad batch consumes {r4,r5,r6,r7} as the rect). NOT x/y/w/h; the
    // project-wide rule holds: every Fable rect is LTRB.
    packet.rect[0] = static_cast<int32_t>(args.r4);
    packet.rect[1] = static_cast<int32_t>(args.r5);
    packet.rect[2] = static_cast<int32_t>(args.r6);
    packet.rect[3] = static_cast<int32_t>(args.r7);
    packet.rect_valid = packet.rect[2] > packet.rect[0] &&
                        packet.rect[3] > packet.rect[1];
  }
  packet.depth = std::isfinite(args.f1) ? static_cast<float>(args.f1) : 1.0f;
  if (guest_base != nullptr && color_pointer != 0 &&
      refii::renderer::bink::IsReadableGuestByteRange(guest_base, color_pointer,
                                                      16u)) {
    for (uint32_t i = 0; i < 4u; ++i) {
      packet.color[i] = refii::renderer::bink::LoadGuestU32(
          guest_base, color_pointer + i * 4u);
    }
    packet.color_valid = true;
  }
  packet.scissor = g_state.device.scissor;
  packet.render_targets = g_state.device.render_targets;
  packet.depth_target = g_state.device.depth_target;

  // Predication state (2026-07-25, IDA-traced; offset re-verified 2026-07-26):
  // device + 0x2AB4 is pDevice->m_ZFilter[42], the DWORD
  // D3DDevice_SetPredication (0x822655F0) mirrors its raw mask argument into on
  // every call, both enable and disable. args.r3 is the device pointer for both
  // ClearF and ClearRect (same param-0 convention). See
  // ClearPacket::predication_mask for why this matters.
  {
    const uint32_t device = args.r3;
    if (guest_base != nullptr && device != 0 &&
        refii::renderer::bink::IsReadableGuestByteRange(
            guest_base, device + kDevicePredicationMaskLo, 4u)) {
      packet.predication_mask = refii::renderer::bink::LoadGuestU32(
          guest_base, device + kDevicePredicationMaskLo);
    }
  }

  g_state.clear_packets.push_back(packet);
  ++g_state.stats.clear_packets;
  if (g_state.clear_packets.size() > 65536u) {
    constexpr size_t kTrimCount = 32768u;
    g_state.clear_packets.erase(g_state.clear_packets.begin(),
                                g_state.clear_packets.begin() + kTrimCount);
  }
}

}  // namespace refii::gtl
