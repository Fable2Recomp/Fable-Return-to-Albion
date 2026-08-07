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

void OnFrameBoundary() {
  std::lock_guard<std::mutex> lock(g_state.mutex);
  ++g_state.frame_id;
  ++g_state.stats.frames;
  // Hardware-tessellator patch VOLUME. A shape log says what these draws are;
  // this says how many there are, which is the other half of what a rendering
  // design needs. Reported only on frames that actually had some (silent at
  // title/menu, where terrain and water never run), plus a peak watermark.
  {
    const uint64_t frame_calls = g_state.stats.tess_terrain_calls_this_frame +
                                 g_state.stats.tess_water_calls_this_frame;
    if (frame_calls != 0) {
      if (frame_calls > g_state.stats.tess_peak_calls_per_frame) {
        g_state.stats.tess_peak_calls_per_frame = frame_calls;
      }
      REXLOG_INFO(
          "Refii GTL TESS-PATCH census frame={} this_frame[terrain={} water={} "
          "pm4_draws={}] totals[terrain={} water={} pm4_draws={}] "
          "distinct_shapes={} peak_calls_per_frame={} "
          "ring_capture_failed={} device_regs_unreadable={}",
          static_cast<unsigned long long>(g_state.frame_id),
          static_cast<unsigned long long>(
              g_state.stats.tess_terrain_calls_this_frame),
          static_cast<unsigned long long>(
              g_state.stats.tess_water_calls_this_frame),
          static_cast<unsigned long long>(
              g_state.stats.tess_pm4_draws_this_frame),
          static_cast<unsigned long long>(g_state.stats.tess_terrain_calls),
          static_cast<unsigned long long>(g_state.stats.tess_water_calls),
          static_cast<unsigned long long>(g_state.stats.tess_pm4_draws),
          static_cast<unsigned long long>(g_state.stats.tess_distinct_shapes),
          static_cast<unsigned long long>(
              g_state.stats.tess_peak_calls_per_frame),
          static_cast<unsigned long long>(
              g_state.stats.tess_ring_capture_failed),
          static_cast<unsigned long long>(
              g_state.stats.tess_device_regs_unreadable));
    }
    g_state.stats.tess_terrain_calls_this_frame = 0;
    g_state.stats.tess_water_calls_this_frame = 0;
    g_state.stats.tess_pm4_draws_this_frame = 0;
  }
  // Frame-cadenced observer-removal census (every 240 frames, independent of the
  // 512-frame stats block): the begin-pin outcome split + per-family coverage.
  // The 512 cadence missed the menu compose entirely in run 205 (ended frame
  // 1513, next 512-census at 1537), so sample often enough that any reached
  // screen is covered. These are the two numbers driving GTL draw ownership.
  if (g_state.frame_id % 240u == 0u) {
    REXLOG_INFO(
        "Refii GTL begin-pin census frame={} arm[reached={} rej_depth={} "
        "rej_symbol={} rej_bytes={} ok={}] drain[reached={} unreadable={} "
        "blank={} packet_gone={} ok={}]",
        static_cast<unsigned long long>(g_state.frame_id),
        g_begin_pin_census.arm_reached.load(std::memory_order_relaxed),
        g_begin_pin_census.arm_reject_depth.load(std::memory_order_relaxed),
        g_begin_pin_census.arm_reject_symbol.load(std::memory_order_relaxed),
        g_begin_pin_census.arm_reject_bytes.load(std::memory_order_relaxed),
        g_begin_pin_census.arm_ok.load(std::memory_order_relaxed),
        g_begin_pin_census.drain_reached.load(std::memory_order_relaxed),
        g_begin_pin_census.drain_unreadable.load(std::memory_order_relaxed),
        g_begin_pin_census.drain_blank.load(std::memory_order_relaxed),
        g_begin_pin_census.drain_packet_gone.load(std::memory_order_relaxed),
        g_begin_pin_census.drain_ok.load(std::memory_order_relaxed));
    for (const auto& [symbol, cov] : g_family_coverage) {
      REXLOG_INFO(
          "Refii GTL family coverage frame={} sub_{:08X} total={} geom={} "
          "streamvb={} ident={} decl={} tex0={} rt0={}",
          static_cast<unsigned long long>(g_state.frame_id), symbol,
          static_cast<unsigned long long>(cov.total),
          static_cast<unsigned long long>(cov.geom),
          static_cast<unsigned long long>(cov.streamvb),
          static_cast<unsigned long long>(cov.ident),
          static_cast<unsigned long long>(cov.decl),
          static_cast<unsigned long long>(cov.tex0),
          static_cast<unsigned long long>(cov.rt0));
    }
    // PHASE 1 rolling summary of the device-state divergence census. The
    // per-shape lines (one per distinct divergence shape, emitted from
    // AccumulateDeviceSnapshotDivergenceLocked) say WHAT diverges; this says
    // HOW MUCH. Ratios are the readable form: "diverged/compared" per class.
    // EXPECT LARGE class-B numbers on Scaleform/menu frames -- ~28 of the 111
    // SetRenderState_*/SetSamplerState_* entry points have direct callers
    // (mostly GFxRendererXbox360_*) that skip the dirty journal outright. That
    // is confirmation of the premise, not a defect in this census.
    //
    // FETCH READS AS TWO NUMBERS since the Phase-2 fetch cutover:
    //   fetch=<diverged>/<compared>        device vs WHAT THE PACKET CARRIES.
    //                                      Must collapse toward 0/N when
    //                                      fetch_src=1 -- that is the cutover's
    //                                      confirmation. Residual should be
    //                                      attributable to preflush_root.
    //   fetch_mirror=<diverged>/<compared> device vs the OnFetchConstantDispatch
    //                                      mirror. Unchanged by the cutover;
    //                                      run 284 baseline was 10420/12244.
    REXLOG_INFO(
        "Refii GTL device-state divergence census frame={} draws[examined={} "
        "no_snapshot={} diverged={}] mirror[vs_float={}/{} ps_float={}/{} "
        "fetch={}/{} cull={}/{}] fetch_mirror={}/{} "
        "fetch_src={} fetch_device_sourced_draws={} preflush_root_draws={} "
        "journal[flow_unjournalled={} "
        "packets_unjournalled={}] distinct_shapes={}",
        static_cast<unsigned long long>(g_state.frame_id),
        static_cast<unsigned long long>(g_divergence_census.draws_examined),
        static_cast<unsigned long long>(g_divergence_census.draws_no_snapshot),
        static_cast<unsigned long long>(g_divergence_census.draws_diverged),
        static_cast<unsigned long long>(
            g_divergence_census.vs_float_blocks_diverged),
        static_cast<unsigned long long>(g_divergence_census.vs_float_blocks),
        static_cast<unsigned long long>(
            g_divergence_census.ps_float_blocks_diverged),
        static_cast<unsigned long long>(g_divergence_census.ps_float_blocks),
        static_cast<unsigned long long>(
            g_divergence_census.fetch_slots_diverged),
        static_cast<unsigned long long>(g_divergence_census.fetch_slots),
        static_cast<unsigned long long>(g_divergence_census.cull_diverged),
        static_cast<unsigned long long>(g_divergence_census.cull_compared),
        static_cast<unsigned long long>(
            g_divergence_census.fetch_mirror_slots_diverged),
        static_cast<unsigned long long>(g_divergence_census.fetch_mirror_slots),
        FetchDeviceSourced() ? 1 : 0,
        static_cast<unsigned long long>(
            g_divergence_census.fetch_device_sourced_draws),
        static_cast<unsigned long long>(
            g_divergence_census.fetch_preflush_root_draws),
        static_cast<unsigned long long>(
            g_divergence_census.flow_unjournalled),
        static_cast<unsigned long long>(
            g_divergence_census.packets_unjournalled),
        static_cast<unsigned long long>(g_divergence_census.distinct_shapes));
  }
  if (g_state.frame_id % 512u == 1u) {
    REXLOG_INFO(
        "Refii GTL stats frame={} resources={} (created={} regen_carried={} "
        "fetch_stated={} destroyed={}) "
        "state_events={} draw_packets={} clear_packets={} resolve_packets={} "
        "content_packets={} content_stored={} content_unreadable={} "
        "content_store_mb={} "
        "streamed[done={} events={} unreg={}] "
        "tile_content[packets={} unmatched={} bad_args={} size_unknown={} "
        "unreadable={} off_skip={} outer_skip={} lvl_skip={} "
        "enters[out={} in={}]] "
        "unmatched={} pending_packets={} | vs_from_state={} "
        "vs_backfill_tried={} vs_backfill_filled={} vs_absent={}",
        static_cast<unsigned long long>(g_state.frame_id),
        g_state.resources.size(),
        static_cast<unsigned long long>(g_state.stats.resources_registered),
        static_cast<unsigned long long>(
            g_state.stats.resources_reregistered_generation_carried),
        static_cast<unsigned long long>(
            g_state.stats.resource_fetch_dwords_stated),
        static_cast<unsigned long long>(g_state.stats.resources_destroyed),
        static_cast<unsigned long long>(g_state.stats.state_events),
        static_cast<unsigned long long>(g_state.stats.draw_packets),
        static_cast<unsigned long long>(g_state.stats.clear_packets),
        static_cast<unsigned long long>(g_state.stats.resolve_packets),
        static_cast<unsigned long long>(g_state.stats.content_packets),
        static_cast<unsigned long long>(g_state.stats.content_bytes_stored),
        static_cast<unsigned long long>(g_state.stats.content_unreadable),
        static_cast<unsigned long long>(g_state.content_image_bytes >> 20),
        static_cast<unsigned long long>(g_state.stats.streamed_completions),
        static_cast<unsigned long long>(g_state.stats.streamed_content_events),
        static_cast<unsigned long long>(g_state.stats.streamed_unregistered),
        static_cast<unsigned long long>(g_state.stats.tile_content_packets),
        static_cast<unsigned long long>(g_state.stats.tile_content_unmatched),
        static_cast<unsigned long long>(g_state.stats.tile_content_bad_args),
        static_cast<unsigned long long>(
            g_state.stats.tile_content_size_unknown),
        static_cast<unsigned long long>(g_state.stats.tile_content_unreadable),
        static_cast<unsigned long long>(
            g_state.stats.tile_content_offset_skipped),
        static_cast<unsigned long long>(
            g_state.stats.tile_content_outer_skipped),
        static_cast<unsigned long long>(
            g_state.stats.tile_content_level_skipped),
        static_cast<unsigned long long>(g_state.stats.tile_outer_enters),
        static_cast<unsigned long long>(g_state.stats.tile_inner_enters),
        static_cast<unsigned long long>(g_state.stats.unmatched_set_events),
        g_state.packets.size(),
        static_cast<unsigned long long>(g_state.stats.shader_from_device_state),
        static_cast<unsigned long long>(
            g_state.stats.shader_backfill_attempted),
        static_cast<unsigned long long>(g_state.stats.shader_backfill_filled),
        static_cast<unsigned long long>(g_state.stats.shader_still_absent));
    // The fetch_ingest[...] half of this line was DELETED 2026-08-04. Its eight
    // counters had no increment site anywhere in the tree — IngestBoundTexture
    // Content was removed 2026-07-26 and the stats were "retained deliberately
    // as gap-attribution diagnostics". A field that is structurally incapable
    // of being non-zero attributes nothing; it printed a constant
    // `stored=0 unique=0 unreg=0 unchanged=0 declined[0 0 0 0]` twice per stats
    // dump and read as a measurement. base_alias is LIVE (CopyResourceContent's
    // physical-base fallback) and stays.
    REXLOG_INFO("Refii GTL stats base_alias[hit={} miss={}]",
        static_cast<unsigned long long>(g_state.stats.base_alias_content_hits),
        static_cast<unsigned long long>(g_state.stats.base_alias_content_misses));
    // STATED BUFFER PAYLOAD census. `served` is the number that says the
    // vertex/index feed is alive; `no_header` counts buffers whose header
    // states no allocation yet (expected before the first fill, a defect if it
    // stays high); `reloc` says whether the compactor is actually moving
    // payloads under us.
    REXLOG_INFO("Refii GTL stats buffers[binds={} adopted={}] "
                "payload[served={} no_header={} unreadable={} oversize={}] "
                "reloc[seen={} matched={}]",
        static_cast<unsigned long long>(g_state.stats.buffer_binds_stated),
        static_cast<unsigned long long>(g_state.stats.buffers_adopted_at_bind),
        static_cast<unsigned long long>(g_state.stats.buffer_content_served),
        static_cast<unsigned long long>(g_state.stats.buffer_content_no_header),
        static_cast<unsigned long long>(
            g_state.stats.buffer_content_unreadable),
        static_cast<unsigned long long>(g_state.stats.buffer_content_oversize),
        static_cast<unsigned long long>(g_state.stats.buffer_relocations),
        static_cast<unsigned long long>(
            g_state.stats.buffer_relocations_matched));
    // TENANCY census. `corrected` is the direct size of the stale-identity
    // population: header inits whose ENTER-side fetch read had captured the
    // OUTGOING tenant's dwords, which the GTL used to publish as fact.
    REXLOG_INFO("Refii GTL stats tenancy[bumps={} corrected={}] "
                "stated_tex_kept={} stated_tex_disagrees_fetch={} "
                "settex[nonzero={} slot0={} out_of_range={}] "
                "viewport[shadow={} rejected={}]",
                static_cast<unsigned long long>(
                    g_state.stats.header_init_tenancy_bumps),
                static_cast<unsigned long long>(
                    g_state.stats.header_init_fetch_corrected),
                static_cast<unsigned long long>(
                    g_state.stats.stated_texture_kept_over_empty_fetch),
                static_cast<unsigned long long>(
                    g_state.stats.stated_texture_disagrees_fetch),
                static_cast<unsigned long long>(
                    g_state.stats.set_texture_nonzero_binds),
                static_cast<unsigned long long>(
                    g_state.stats.set_texture_slot0_binds),
                static_cast<unsigned long long>(
                    g_state.stats.set_texture_slot_out_of_range),
                static_cast<unsigned long long>(
                    g_state.stats.viewport_from_device_shadow),
                static_cast<unsigned long long>(
                    g_state.stats.viewport_shadow_rejected));
    LogSelfReadExitCensusLocked();
    // §3b census. unbound_source != 0 => flags&7 named a slot the tracked
    // device state had nothing in (a device-state tracking gap); measured 0
    // over 442 resolves in run 437. unregistered_dest is EXPECTED ~100% and is
    // fine — the destination headers are reused scratch (run 437 saw one header
    // carry three different fetch constants), so Create identity does not
    // exist for them. no_dest_fetch is the one that must stay 0: the fetch
    // constant is the only durable name for the memory being written.
    REXLOG_INFO(
        "Refii GTL resolve stats total={} pending={} unbound_source={} "
        "unregistered_dest={} no_dest_fetch={} offset_events={} "
        "adopted_at_offset={} release_events={} released_destroyed={} "
        "released_survived={} release_bad_shape={} unmapped_symbols={} "
        "creates_dropped={} header_size_queries={} header_unreadable={}",
        static_cast<unsigned long long>(g_state.stats.resolve_packets),
        g_state.resolve_packets.size(),
        static_cast<unsigned long long>(g_state.stats.resolve_unbound_source),
        static_cast<unsigned long long>(g_state.stats.resolve_unregistered_dest),
        static_cast<unsigned long long>(g_state.stats.resolve_no_dest_fetch),
        static_cast<unsigned long long>(
            g_state.stats.offset_base_address_events),
        static_cast<unsigned long long>(
            g_state.stats.resources_adopted_at_offset),
        static_cast<unsigned long long>(g_state.stats.resources_release_events),
        static_cast<unsigned long long>(g_state.stats.resources_destroyed),
        static_cast<unsigned long long>(
            g_state.stats.resources_release_survived),
        static_cast<unsigned long long>(
            g_state.stats.resources_release_bad_shape),
        static_cast<unsigned long long>(g_state.stats.unmapped_symbol_events),
        static_cast<unsigned long long>(
            g_state.stats.creates_dropped_null_object),
        static_cast<unsigned long long>(g_state.stats.header_inits_size_query),
        static_cast<unsigned long long>(
            g_state.stats.header_inits_unreadable_out_pointer));
    // Per-family packet-completeness census: one line per draw-root family,
    // so we can read which families the packet already fully owns (geom+ident)
    // and are therefore observer-removal candidates.
    for (const auto& [symbol, cov] : g_family_coverage) {
      REXLOG_INFO(
          "Refii GTL family coverage sub_{:08X} total={} geom={} streamvb={} "
          "ident={} decl={} tex0={} rt0={}",
          symbol, static_cast<unsigned long long>(cov.total),
          static_cast<unsigned long long>(cov.geom),
          static_cast<unsigned long long>(cov.streamvb),
          static_cast<unsigned long long>(cov.ident),
          static_cast<unsigned long long>(cov.decl),
          static_cast<unsigned long long>(cov.tex0),
          static_cast<unsigned long long>(cov.rt0));
    }
    // Begin-family pin outcome census: splits the geometry-capture losses by
    // exact cause so the fix (making the GTL own BeginVertices draws) targets
    // the dominant one. arm_reached should equal the sub_82205F68/BeginIndexed
    // family totals; drain_ok is the count that actually gets geometry.
    REXLOG_INFO(
        "Refii GTL begin-pin census arm[reached={} rej_depth={} rej_symbol={} "
        "rej_bytes={} ok={}] drain[reached={} unreadable={} blank={} "
        "packet_gone={} ok={}]",
        g_begin_pin_census.arm_reached.load(std::memory_order_relaxed),
        g_begin_pin_census.arm_reject_depth.load(std::memory_order_relaxed),
        g_begin_pin_census.arm_reject_symbol.load(std::memory_order_relaxed),
        g_begin_pin_census.arm_reject_bytes.load(std::memory_order_relaxed),
        g_begin_pin_census.arm_ok.load(std::memory_order_relaxed),
        g_begin_pin_census.drain_reached.load(std::memory_order_relaxed),
        g_begin_pin_census.drain_unreadable.load(std::memory_order_relaxed),
        g_begin_pin_census.drain_blank.load(std::memory_order_relaxed),
        g_begin_pin_census.drain_packet_gone.load(std::memory_order_relaxed),
        g_begin_pin_census.drain_ok.load(std::memory_order_relaxed));
  }
}

std::vector<DrawPacket> DrainDrawPackets() {
  std::lock_guard<std::mutex> lock(g_state.mutex);
  std::vector<DrawPacket> drained;
  drained.swap(g_state.packets);
  return drained;
}

std::vector<ResolvePacket> DrainResolvePackets() {
  std::lock_guard<std::mutex> lock(g_state.mutex);
  std::vector<ResolvePacket> drained;
  drained.swap(g_state.resolve_packets);
  return drained;
}

std::vector<ClearPacket> DrainClearPackets() {
  std::lock_guard<std::mutex> lock(g_state.mutex);
  std::vector<ClearPacket> drained;
  drained.swap(g_state.clear_packets);
  return drained;
}

std::vector<TessellatedPatchDrawPacket> DrainTessellatedPatchDrawPackets() {
  std::lock_guard<std::mutex> lock(g_state.mutex);
  std::vector<TessellatedPatchDrawPacket> drained;
  drained.swap(g_state.tess_packets);
  return drained;
}



namespace {
// Drain packets belonging to COMPLETED guest frames (packet_id < boundary),
// leaving in-progress packets (>= boundary) in place for the next drain. This
// gives the authoritative submission one guest frame at a time instead of a
// pump-frame fraction (the observer command batch was implicitly guest-frame
// aligned; the GTL streams are not, and the pump over-runs the guest ~1.75x).
// boundary == 0 (before the first guest Present) drains everything, as the
// unbounded drains do.
template <typename PacketT>
std::vector<PacketT> DrainPacketsUpToBoundaryLocked(
    std::vector<PacketT>& source, uint64_t boundary) {
  if (boundary == 0) {
    std::vector<PacketT> drained;
    drained.swap(source);
    return drained;
  }
  std::vector<PacketT> drained;
  std::vector<PacketT> kept;
  drained.reserve(source.size());
  for (PacketT& packet : source) {
    if (packet.packet_id < boundary) {
      drained.push_back(std::move(packet));
    } else {
      kept.push_back(std::move(packet));
    }
  }
  source.swap(kept);
  return drained;
}
}  // namespace

std::vector<DrawPacket> DrainDrawPacketsUpTo(uint64_t boundary) {
  std::lock_guard<std::mutex> lock(g_state.mutex);
  return DrainPacketsUpToBoundaryLocked(g_state.packets, boundary);
}

std::vector<ResolvePacket> DrainResolvePacketsUpTo(uint64_t boundary) {
  std::lock_guard<std::mutex> lock(g_state.mutex);
  return DrainPacketsUpToBoundaryLocked(g_state.resolve_packets, boundary);
}

std::vector<ClearPacket> DrainClearPacketsUpTo(uint64_t boundary) {
  std::lock_guard<std::mutex> lock(g_state.mutex);
  return DrainPacketsUpToBoundaryLocked(g_state.clear_packets, boundary);
}

std::vector<TessellatedPatchDrawPacket> DrainTessellatedPatchDrawPacketsUpTo(
    uint64_t boundary) {
  std::lock_guard<std::mutex> lock(g_state.mutex);
  return DrainPacketsUpToBoundaryLocked(g_state.tess_packets, boundary);
}

bool HasPendingPacketsUpTo(uint64_t boundary) {
  std::lock_guard<std::mutex> lock(g_state.mutex);
  const auto has_pending = [boundary](const auto& packets) {
    if (boundary == 0) {
      return !packets.empty();
    }
    for (const auto& packet : packets) {
      if (packet.packet_id < boundary) {
        return true;
      }
    }
    return false;
  };
  return has_pending(g_state.packets) || has_pending(g_state.resolve_packets) ||
         has_pending(g_state.clear_packets) ||
         has_pending(g_state.tess_packets);
}

// DELETED 2026-07-26: IngestBoundTextureContent (+ its FnvHashContentBytes
// helper). It was the §4 "store the bytes the observer captured at each texture
// bind" experiment, REVERTED 2026-07-23 because it regressed rendering against
// the Xenia reference (lost menu/save text), and it has had zero callers ever
// since — hook_observer.cpp:2949 only mentions it in a comment. Nothing else
// ever set `Resource::content_from_fetch_ingest` or `ContentImage::from_ingest`
// to true, so both flags were already permanently false.
//
// THE RESIDUE IS NOW GONE TOO (2026-08-04): both flags, `fetch_content_hash`,
// the always-early-returning `DropFetchIngestedContentLocked` and its two call
// sites, and the eight `fetch_content_*` stats have been deleted.
//
// DO NOT re-add a naive version. The four regressions its guards encoded are the
// reason the observer's per-bind fetch capture is load-bearing and NOT
// deletable: it arbitrates render-target GPU-write vs CPU-overlay lifecycles
// (run 192, GUI text pages), resolve-produced GPU content with no CPU bytes,
// stated unlock/tile writers that own real bytes with a stated layout, and
// time-multiplexed pool bases where content is (object,time)-specific (run 194
// off-by-one). The real_gap for fetch-delivered content stays open BY DESIGN.

// Lock-free body, so callers that ALREADY hold g_state.mutex (the resource
// snapshot feed at the bottom of this file) can serve content without dropping
// and re-taking it. The public entry below is the only thing that locks.
bool CopyResourceContentLocked(uint32_t resource, bool mip_range,
                               std::vector<uint8_t> &out,
                               bool &out_source_linear) {
  const uint64_t key =
      (static_cast<uint64_t>(resource) << 1) | (mip_range ? 1u : 0u);
  auto it = g_state.content_images.find(key);
  if (it != g_state.content_images.end() && !it->second.bytes.empty()) {
    out = it->second.bytes;
    out_source_linear = it->second.source_linear;
    return true;
  }
  // Base-keyed SIBLING fallback, reinstated 2026-08-02 read-only and narrow.
  // The 2026-07-23 removal note below assumed "the observer serves it" — the
  // 07-29 draw-event gate ended that, and the designated alarm fired twice:
  // run 480 (title atlas nulls) and run 521 (`exact title GUI sync snapshot
  // known=0` for base 0x1A0D9000 whose bytes sit under the RETIRED sibling
  // header key). Fable recycles heap headers over a stable physical base; the
  // base is the Xbox-semantic identity, object keys are our bookkeeping. On an
  // object-key miss, serve the bytes stored under a sibling object that names
  // the same base. Gates: same base, size match when the requester's size is
  // known; newest content_generation wins among candidates. Store writers are
  // untouched — this changes reads only.
  auto requested = g_state.resources.find(resource);
  if (requested == g_state.resources.end() ||
      requested->second.base_address == 0) {
    return false;
  }
  const uint32_t base = requested->second.base_address;
  const uint32_t requested_size = requested->second.byte_size;
  const State::ContentImage *best = nullptr;
  uint64_t best_generation = 0;
  for (const auto &entry : g_state.resources) {
    if (entry.first == resource || entry.second.base_address != base) {
      continue;
    }
    const uint64_t sibling_key =
        (static_cast<uint64_t>(entry.first) << 1) | (mip_range ? 1u : 0u);
    auto sibling = g_state.content_images.find(sibling_key);
    if (sibling == g_state.content_images.end() ||
        sibling->second.bytes.empty()) {
      continue;
    }
    if (requested_size != 0 &&
        sibling->second.bytes.size() != requested_size) {
      continue;
    }
    if (best == nullptr || entry.second.content_generation >= best_generation) {
      best = &sibling->second;
      best_generation = entry.second.content_generation;
    }
  }
  if (best == nullptr) {
    // *** LAST TIER: READ THE GUEST'S STATED BASE. ***
    //
    // Some content NEVER passes an unlock root, a tile call, or any other
    // observable event — it is written by RAW CPU STORES straight into the
    // resource's memory. Proven case (IDA 2026-08-04): the tonemap's 512x1
    // R32F tone curve. sub_82A7B9A8 creates it over XPhysicalAlloc and
    // publishes it with XGOffsetResourceAddress; sub_8217E428 then fills it
    // EVERY FRAME with plain float stores (`v57 = v54[3]; v61 = (float*)(v57+8);
    // *(v61-2) = ...`) with NO Lock and NO Unlock anywhere on the path.
    // Contrast the scattering LUTs (sub_82A64988) which DO end in
    // D3D_UnlockResource — exactly why those are visible and this is not.
    //
    // A call-observing backend is structurally blind to this. But the GUEST
    // STATED the base address, so reading it is honouring stated data — the
    // §5 rule — not emulating hardware. It belongs HERE, in the GTL, as the
    // resource's own content: there is no observer in this path and none is
    // wanted.
    //
    // Read through the PHYSICAL VIEW. A raw physical base indexes the low
    // virtual heap, which is "frequently committed and non-zero, so reading it
    // fails silently instead of loudly" (plume_fable_addressing.h). Reading
    // the wrong form is why this content was long believed to be all zeros.
    constexpr uint32_t kMaxStatedBaseContentBytes = 1u << 20;
    uint8_t *guest_base = refii::renderer::FableGuestMemoryBase();
    if (guest_base != nullptr && requested_size != 0 &&
        requested_size <= kMaxStatedBaseContentBytes) {
      const uint32_t view =
          refii::renderer::FableGuestPhysicalViewAddress(base);
      if (refii::renderer::bink::IsReadableGuestByteRange(guest_base, view,
                                                          requested_size)) {
        const uint8_t *src = guest_base + view;
        // Validate before trusting. Freshly XPhysicalAlloc'd, never-written
        // memory reads as a UNIFORM poison fill — measured 0xFFC00000 (-NaN)
        // across all 512 curve entries at startup, before the per-frame fill
        // runs. Uploading that is worse than uploading nothing. Two
        // format-agnostic rejections: all-zero, and every 32-bit word
        // identical (a single repeated value is a fill pattern, never a
        // curve or an image).
        bool any_nonzero = false;
        bool uniform_word = true;
        uint32_t first_word = 0;
        const uint32_t whole_words = requested_size / 4u;
        for (uint32_t i = 0; i < requested_size; ++i) {
          if (src[i] != 0u) {
            any_nonzero = true;
            break;
          }
        }
        for (uint32_t i = 0; i < whole_words; ++i) {
          uint32_t w = 0;
          std::memcpy(&w, src + i * 4u, sizeof(w));
          if (i == 0) {
            first_word = w;
          } else if (w != first_word) {
            uniform_word = false;
            break;
          }
        }
        if (any_nonzero && !(whole_words > 1 && uniform_word)) {
          out.assign(src, src + requested_size);
          // The stated base names the resource's OWN allocation, so the bytes
          // carry whatever layout the resource has — same as an unlock range.
          out_source_linear = false;
          ++g_state.stats.stated_base_content_hits;
          return true;
        }
        ++g_state.stats.stated_base_content_rejected;
      }
    }
    ++g_state.stats.base_alias_content_misses;
    return false;
  }
  ++g_state.stats.base_alias_content_hits;
  out = best->bytes;
  out_source_linear = best->source_linear;
  return true;
}

bool CopyResourceContent(uint32_t resource, bool mip_range,
                         std::vector<uint8_t> &out, bool &out_source_linear) {
  std::lock_guard<std::mutex> lock(g_state.mutex);
  return CopyResourceContentLocked(resource, mip_range, out,
                                   out_source_linear);
}
// NOTE (2026-07-23): a base->object fallback lived here (resolve a physical-base
// query to the registered object owning that base). REMOVED with the fetch-store
// ingest — it served base-keyed content the observer already handles, and it
// only mattered for the ingested pool aliases. [2026-08-02: premise expired,
// fallback reinstated above — see its comment.]

bool CopyResourceContent(uint32_t resource, bool mip_range,
                         std::vector<uint8_t> &out) {
  bool ignored_source_linear = false;
  return CopyResourceContent(resource, mip_range, out, ignored_source_linear);
}


StatedPresentSource GetStatedPresentSource() {
  std::lock_guard<std::mutex> lock(g_state.mutex);
  StatedPresentSource out;
  out.rt0 = g_state.stated_present_rt0;
  out.front_buffer = g_state.stated_present_front_buffer;
  out.front_buffer_base = g_state.stated_present_front_buffer_base;
  out.epoch = g_state.stated_present_epoch;
  out.guest_frame_boundary_packet_id = g_state.guest_frame_boundary_packet_id;
  return out;
}

void SetGuestFramePresentedCallback(GuestFramePresentedCallback callback) {
  g_guest_frame_presented_callback.store(callback, std::memory_order_release);
}

ResourceContentStatus QueryResourceContentStatus(uint32_t resource) {
  ResourceContentStatus status;
  if (resource == 0) {
    return status;
  }
  std::lock_guard<std::mutex> lock(g_state.mutex);
  auto it = g_state.resources.find(resource);
  if (it == g_state.resources.end()) {
    return status;
  }
  status.known = true;
  status.create_symbol = it->second.create_symbol;
  status.create_lr = it->second.create_lr;
  status.content_generation = it->second.content_generation;
  status.gpu_content_generation = it->second.gpu_content_generation;
  status.base_address = it->second.base_address;
  const uint64_t base_key = static_cast<uint64_t>(resource) << 1;
  auto image = g_state.content_images.find(base_key);
  status.has_stored_bytes =
      image != g_state.content_images.end() && !image->second.bytes.empty();
  return status;
}

bool ResourceHasGpuProducedContent(uint32_t resource) {
  if (resource == 0) {
    return false;
  }
  std::lock_guard<std::mutex> lock(g_state.mutex);
  auto it = g_state.resources.find(resource);
  return it != g_state.resources.end() &&
         it->second.gpu_content_generation != 0;
}

ResourceIdentity QueryResourceIdentity(uint32_t guest_address) {
  ResourceIdentity identity;
  std::lock_guard<std::mutex> lock(g_state.mutex);
  const auto it = g_state.resources.find(guest_address);
  if (it == g_state.resources.end()) {
    return identity;
  }
  const Resource &resource = it->second;
  identity.known = true;
  identity.create_serial = resource.create_serial;
  identity.guest_address = resource.guest_address;
  identity.parent_texture = resource.view_parent_texture;
  identity.base_address = resource.base_address;
  identity.mip_address = resource.mip_address;
  identity.byte_size = resource.byte_size;
  identity.width = resource.width;
  identity.height = resource.height;
  identity.format = resource.format;
  identity.content_generation = resource.content_generation;
  identity.gpu_content_generation = resource.gpu_content_generation;
  // Absent until 2026-08-03, so no consumer could tell "same address, different
  // texture" from "same texture, changed content".
  identity.tenancy_generation = resource.tenancy_generation;
  identity.fetch_dwords = resource.fetch_dwords;
  identity.fetch_dwords_stated = resource.fetch_dwords_stated;
  identity.was_render_target = resource.was_render_target;
  return identity;
}

ResourceDirtyExtent ConsumeResourceDirtyExtent(uint32_t guest_address) {
  ResourceDirtyExtent extent;
  std::lock_guard<std::mutex> lock(g_state.mutex);
  const auto it = g_state.resources.find(guest_address);
  if (it == g_state.resources.end()) {
    return extent;
  }
  extent.known = true;
  extent.content_generation = it->second.content_generation;
  extent.base_start = it->second.dirty_base_start;
  extent.base_end = it->second.dirty_base_end;
  extent.mip_start = it->second.dirty_mip_start;
  extent.mip_end = it->second.dirty_mip_end;
  extent.base_allocation = it->second.dirty_base_allocation;
  extent.mip_allocation = it->second.dirty_mip_allocation;
  // Single-shot, mirroring the guest root: D3D::UnlockResource resets the
  // packed range at resource+0x14/+0x18 to the 0xFFFF0000 sentinel after
  // handing it to D3D_FlushCachedMemory, so a stated extent describes exactly
  // one flush. Without this reset, an unlock that bypasses this module's
  // entry capture (Plume-replaced hooks never re-state) hands the same stale
  // extent to every later consumer (run 424).
  it->second.dirty_base_start = 0;
  it->second.dirty_base_end = 0;
  it->second.dirty_mip_start = 0;
  it->second.dirty_mip_end = 0;
  it->second.dirty_base_allocation = 0;
  it->second.dirty_mip_allocation = 0;
  return extent;
}

}  // namespace refii::gtl
