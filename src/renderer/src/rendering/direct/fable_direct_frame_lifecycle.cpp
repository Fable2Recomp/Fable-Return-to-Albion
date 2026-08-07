#include "runtime/refii_renderer/plume/fable/direct/fable_direct_renderer.h"
#include "runtime/refii_renderer/plume/fable/direct/fable_direct_renderer_internal.h"

#include "runtime/cvars.h"
#include "runtime/hook_observer.h"
#include "runtime/refii_renderer/bik/bink_guest_memory.h"
#include "runtime/refii_renderer/bik/bink_plane_publish.h"
#include "runtime/refii_renderer/gtl/refii_gtl.h"
#include "renderdoc_debugger.h"
#include "runtime/refii_renderer/plume/fable/fable_guest_display.h"
#include "runtime/refii_renderer/plume/fable/resource/fable_guest_resource_registry.h"
#include "runtime/refii_renderer/plume/fable/private_rect/fable_direct_private_rect_symbols.h"
#include "runtime/refii_renderer/plume/fable/private_rect/fable_surface_texture_link_classification.h"
#include "runtime/refii_renderer/plume/fable/private_rect/fable_private_rect_geometry.h"
#include "runtime/refii_renderer/plume/fable/direct/fable_direct_cache_keys.h"
#include "runtime/refii_renderer/plume/fable/direct/fable_direct_command_batch.h"
#include "runtime/refii_renderer/plume/fable/direct/fable_direct_command_classification.h"
#include "runtime/refii_renderer/plume/fable/direct/fable_direct_command_readback.h"
#include "runtime/refii_renderer/plume/fable/direct/fable_direct_framebuffer_binding.h"
#include "runtime/refii_renderer/plume/fable/direct/fable_direct_index_data.h"
#include "runtime/refii_renderer/plume/fable/direct/fable_direct_inline_data.h"
#include "runtime/refii_renderer/plume/fable/direct/fable_direct_primitive8.h"
#include "runtime/refii_renderer/plume/fable/direct/fable_direct_render_state.h"
#include "runtime/refii_renderer/plume/fable/direct/fable_direct_scratch_wrapper_state.h"
#include "runtime/refii_renderer/plume/fable/direct/fable_direct_state.h"
#include "runtime/refii_renderer/plume/fable/direct/fable_direct_present_state.h"
#include "runtime/refii_renderer/plume/fable/resource/fable_direct_resource_readiness.h"
#include "runtime/refii_renderer/plume/fable/resource/fable_texture_fetch.h"
#include "runtime/refii_renderer/plume/fable/private_rect/fable_direct_synthetic_private_rect.h"
#include "runtime/refii_renderer/plume/fable/packets/fable_frame_database_command_packets.h"
#include "runtime/refii_renderer/plume/fable/packets/fable_frame_actions.h"
#include "runtime/refii_renderer/plume/fable/packets/fable_refii_packet_analysis.h"
#include "runtime/refii_renderer/plume/fable/packets/fable_refii_packet_builders.h"
#include "runtime/refii_renderer/plume/fable/packets/fable_refii_replay_builders.h"
#include "runtime/refii_renderer/plume/fable/packets/fable_texture_slot_masks.h"
#include "runtime/refii_renderer/plume/fable/debug/fable_renderdoc_capture.h"
#include "runtime/refii_renderer/plume/fable/debug/fable_resource_debug_snapshot.h"
#include "runtime/refii_renderer/plume/fable/debug/fable_renderdoc_draw_targets.h"
#include "runtime/refii_renderer/plume/fable/packets/fable_render_graph_snapshot.h"
#include "runtime/refii_renderer/plume/fable/title/fable_title_classification.h"
#include "runtime/refii_renderer/plume/fable/title/fable_title_constants.h"
#include "runtime/refii_renderer/plume/fable/title/fable_title_draw_classification.h"
#include "runtime/refii_renderer/plume/fable/title/fable_title_frame_packet_classification.h"
#include "runtime/refii_renderer/plume/fable/title/fable_title_resource_facts.h"
#include "runtime/refii_renderer/plume/fable/video/fable_bink_plane_readiness.h"
#include "runtime/refii_renderer/plume/fable/video/fable_bink_vertex_transform.h"
#include "runtime/refii_renderer/plume/fable/video/fable_pix_video_readiness.h"
#include "runtime/refii_renderer/plume/fable/video/fable_pix_video_uploads.h"
#include "runtime/refii_renderer/plume/fable/video/fable_video_classification.h"
#include "runtime/refii_renderer/plume/fable/video/fable_video_constants.h"
#include "runtime/plume/draw/plume_draw_state.h"
#include "runtime/refii_renderer/plume/general/plume_renderdoc_capture.h"
#include "runtime/plume/resource/plume_resource_manager_internal.h"
#include "runtime/plume/resource/plume_vertex_declaration.h"
#include "renderer_runtime.h"
#include "runtime/shader_library.h"

#include <algorithm>
#include <atomic>
#include <array>
#include <chrono>
#include <cctype>
#include <bit>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <deque>
#include <fstream>
#include <limits>
#include <memory>
#include <mutex>
#include <span>
#include <string>
#include <string_view>
#include <unordered_map>
#include <unordered_set>
#include <vector>

#include <rex/logging.h>
#include <tracy/Tracy.hpp>

#if REFII_RENDERER_HAS_PLUME
#include <plume_render_interface_builders.h>
#if defined(_WIN64)
#include "plume_present_blit_ps.hlsl.dxil.h"
#include "plume_present_blit_vs.hlsl.dxil.h"
#endif
#include "plume_present_blit_ps.hlsl.spirv.h"
#include "plume_present_blit_vs.hlsl.spirv.h"
#endif

namespace refii::renderer {

#if REFII_RENDERER_HAS_PLUME

void SyncDirectResourcesLocked(
    plume::RenderDevice* device, plume::RenderCommandList* command_list,
    const std::unordered_set<uint32_t>* texture_upload_filter,
    const std::unordered_set<uint32_t>* resource_filter,
    bool wait_for_observer, bool only_changed_resource_filter) {
  const FableDirectClock::time_point sync_t0 = FableDirectClock::now();
  ++g_direct_sync_calls_this_compose;
  FableGuestResourceChangeSet changes;
  if (wait_for_observer) {
    // Canonical lock order is observer -> registry (2026-07-06 title
    // freeze); we hold the registry here, so drop it across the blocking
    // observer acquisition.
    ScopedFableDirectRegistryUnlock registry_unlock;
    changes = GetFableDirectResourceChanges(true);
  } else {
    changes = GetFableDirectResourceChanges(false);
  }
  const FableDirectClock::time_point changes_t1 = FableDirectClock::now();
  // Compose-scoped dead-address filter (run 491): 522 sync calls per deep-menu
  // compose — clear-path retries, dest-scan runs and the compose syncs — keep
  // re-requesting the SAME never-materializing addresses (stale-frame dead
  // panel headers) at ~2-11ms fixed overhead each (clearsplit sync=5850,
  // sdsplit sync=1064, compose sync_ms=1073). An address that produced no
  // snapshot AND has no renderer record while the observer was fully
  // readable cannot materialize within this compose except through the
  // changed stream, so it is stripped from every later call's filters this
  // compose. Invalidation: a changed-stream appearance (the harvest above)
  // erases it; the set clears on every new frame index and on overflow.
  static uint32_t sync_dead_addresses_frame = UINT32_MAX;
  static std::unordered_set<uint32_t> sync_dead_addresses;
  if (sync_dead_addresses_frame != g_direct_renderer.current_frame_index) {
    sync_dead_addresses_frame = g_direct_renderer.current_frame_index;
    sync_dead_addresses.clear();
  }
  if (!sync_dead_addresses.empty()) {
    for (const uint32_t address : changes.changed_addresses) {
      sync_dead_addresses.erase(address);
    }
  }
  const bool had_requested_resources_before_strip =
      resource_filter && !resource_filter->empty();
  std::unordered_set<uint32_t> stripped_resource_filter_storage;
  std::unordered_set<uint32_t> stripped_texture_filter_storage;
  if (!sync_dead_addresses.empty()) {
    // Strip on LOCAL copies (callers own their sets; null stays null — an
    // absent resource_filter means "sync all changed", which an empty set
    // does not).
    const auto strip_dead_addresses =
        [&](const std::unordered_set<uint32_t>*& filter,
            std::unordered_set<uint32_t>& storage) {
          if (filter == nullptr || filter->empty()) {
            return;
          }
          bool any_dead = false;
          for (const uint32_t address : *filter) {
            if (sync_dead_addresses.count(address) != 0) {
              any_dead = true;
              break;
            }
          }
          if (!any_dead) {
            return;
          }
          storage.reserve(filter->size());
          for (const uint32_t address : *filter) {
            if (sync_dead_addresses.count(address) == 0) {
              storage.insert(address);
            }
          }
          filter = &storage;
        };
    strip_dead_addresses(resource_filter, stripped_resource_filter_storage);
    strip_dead_addresses(texture_upload_filter,
                         stripped_texture_filter_storage);
  }
  const bool has_requested_resources =
      resource_filter && !resource_filter->empty();
  const bool dead_filter_stripped_empty =
      had_requested_resources_before_strip && !has_requested_resources;
  if (changes.observer_busy && !has_requested_resources) {
    if (dead_filter_stripped_empty) {
      ++g_direct_sync_early_outs_this_compose;
    }
    const FableDirectClock::time_point pending_only_t1 =
        FableDirectClock::now();
    const double sync_total_ms =
        FableDirectElapsedMs(sync_t0, pending_only_t1);
    if (sync_total_ms >= 10.0 ||
        g_direct_renderer.current_frame_index % 120u == 0) {
      static uint32_t logged_direct_sync_busy_path_timings = 0;
      static FableDirectClock::time_point last_busy_timing_log{};
      if (logged_direct_sync_busy_path_timings == 0 ||
          FableDirectElapsedMs(last_busy_timing_log, pending_only_t1) >=
              1500.0) {
        last_busy_timing_log = pending_only_t1;
        REXLOG_INFO(
            "Refii plume direct sync timing #{} frame={} total_ms={:.3f} "
            "changes_ms={:.3f} pending_upload_ms={:.3f} early_outs={} busy=1 "
            "uploads={}",
            ++logged_direct_sync_busy_path_timings,
            g_direct_renderer.current_frame_index, sync_total_ms,
            FableDirectElapsedMs(sync_t0, changes_t1),
            FableDirectElapsedMs(changes_t1, pending_only_t1),
            g_direct_sync_early_outs_this_compose,
            g_direct_renderer.resource_uploads);
      }
    }
    return;
  }
  if (changes.serial == g_direct_renderer.resource_serial &&
      changes.changed_addresses.empty() &&
      changes.destroyed_addresses.empty() && !has_requested_resources) {
    if (dead_filter_stripped_empty) {
      ++g_direct_sync_early_outs_this_compose;
    }
    const FableDirectClock::time_point pending_only_t1 =
        FableDirectClock::now();
    const double sync_total_ms =
        FableDirectElapsedMs(sync_t0, pending_only_t1);
    if (sync_total_ms >= 10.0 ||
        g_direct_renderer.current_frame_index % 120u == 0) {
      static uint32_t logged_direct_sync_fast_path_timings = 0;
      static FableDirectClock::time_point last_fast_timing_log{};
      if (logged_direct_sync_fast_path_timings == 0 ||
          FableDirectElapsedMs(last_fast_timing_log, pending_only_t1) >=
              1500.0) {
        last_fast_timing_log = pending_only_t1;
        REXLOG_INFO(
            "Refii plume direct sync timing #{} frame={} total_ms={:.3f} "
            "changes_ms={:.3f} changes_lock_ms={:.3f} changes_drain_ms={:.3f} "
            "changes_refresh_ms={:.3f} pending_upload_ms={:.3f} early_outs={} "
            "fast=1 uploads={}",
            ++logged_direct_sync_fast_path_timings,
            g_direct_renderer.current_frame_index, sync_total_ms,
            FableDirectElapsedMs(sync_t0, changes_t1),
            changes.lock_wait_ms, changes.teardown_drain_ms,
            changes.refresh_ms,
            FableDirectElapsedMs(changes_t1, pending_only_t1),
            g_direct_sync_early_outs_this_compose,
            g_direct_renderer.resource_uploads);
      }
    }
    return;
  }

  // Run-489 destroyed-pass budget: one call spent destroyed_ms=626 — every
  // retirement pays the full forget fan-out (each Forget* below walks every
  // derived private-copy/link index end-to-end), so the 48-count cap still
  // admitted a ~0.6s burst while the title churns panel headers. Retirement
  // is now bounded by a hard per-call time budget with renderer-side
  // carry-over (pending_destroyed_retirements): the wholesale serial ack at
  // the end of this function erases observer destroyed_serials regardless,
  // so the queue owns whatever is deferred — which also fixes the silent
  // pre-existing loss of entries past the 48 cap (they previously fell to
  // the staleness sweep's 240-frame TTL). Ordering hazard: a destroyed
  // address being re-registered THIS call must still retire before the
  // re-registration syncs — the same per-address rule
  // DrainPendingReleaseTeardownsForRegistrationLocked applies observer-side.
  // Changed-set entries ignore the budget for that reason; for everything
  // else (run 490 narrowed this away from filter membership) the rule is
  // enforced at the sync point itself: the snapshot loop retires a queued
  // pending destroy for an address right before syncing its snapshot.
  //
  // 2026-08-02 (run 513): the forget fan-out itself is no longer paid per
  // address — retirements collect into forget_fanout_batch and one batched
  // walk after the sweeps forgets the whole set (see the flush below), so
  // the budgets now meter only the exact-key per-address teardown.
  std::unordered_set<uint32_t> changed_addresses(
      changes.changed_addresses.begin(), changes.changed_addresses.end());
  // Run-490 hazard narrowing: the hazard override existed so that a destroyed
  // address being re-registered THIS call retires before the re-registration
  // syncs. Membership in a REQUESTED filter is not that: the title compose's
  // missing-resource filter names ~1000 pure-interest addresses per call and
  // returned ZERO snapshots (frame 4722: destroyed=68/1094 retired past the
  // budget for destroyed_retire_ms=228.6 — each indexed panel header pays the
  // full forget fan-out, ~3.4ms). The re-registration a filter membership was
  // guarding against can only take effect through the snapshot loop below, so
  // the exact rule is enforced THERE (pending-retirement check per synced
  // snapshot) and the budget override here keeps only the changed-set case —
  // an address the observer explicitly reports re-registered this harvest.
  const auto destroyed_retire_hazard = [&](uint32_t address) {
    return changed_addresses.count(address) != 0;
  };
  constexpr double kDestroyedRetireBudgetMs = 4.0;
  constexpr size_t kMaxPendingDestroyedRetirements = 4096u;
  // 48 -> 512 with the batched forget flush (run 513): the count cap existed
  // to bound the per-address forget fan-out, which is now one batched walk;
  // the remaining per-address work is exact-key. 512 clears the settled-title
  // mint rate (~100-150 destroys/compose) with ~4x headroom so the carry
  // queue no longer recirculates the steady state; the time budget still
  // backstops pathological bursts.
  constexpr size_t kMaxDestroyedResourcesPerSync = 512u;
  // Batched forget fan-out (2026-08-02, run 513): retirements no longer run
  // the registry forget recipes inline — each one walked every primary and
  // derived link/action index end-to-end (~35 maps, 60-85us per address),
  // which at ~100-150 retirements/compose WAS destroyed_retire + the bulk of
  // destroyed_sweep. Every retirement instead records its address here and
  // ONE FableDirectForgetResourceRegistrationsBatch call after the sweeps
  // runs the walks once for the whole set (same shape as the batched link
  // repair, GDB-proven 2026-08-02). Safe because nothing between a
  // retirement and the flush re-registers a retired address — the snapshot
  // loop, the only place a re-registration takes effect, runs after the
  // flush, and it retires its own queued-destroy hazards inline (see
  // defer_forget_fanout below).
  std::vector<uint32_t> forget_fanout_batch;
  bool defer_forget_fanout = true;
  const FableDirectClock::time_point destroyed_retire_t0 =
      FableDirectClock::now();
  const auto destroyed_retire_over_budget = [&] {
    return FableDirectElapsedMs(destroyed_retire_t0, FableDirectClock::now()) >=
           kDestroyedRetireBudgetMs;
  };
  const auto retire_destroyed_address = [&](uint32_t address,
                                            uint64_t destroyed_serial,
                                            bool carried) {
    auto destroyed_it = g_direct_renderer.resources.find(address);
    if (carried && destroyed_serial != 0 &&
        destroyed_it != g_direct_renderer.resources.end() &&
        destroyed_it->second.serial > destroyed_serial) {
      // The record at this address was re-synced from a registration NEWER
      // than the queued destroy (recycled address); the destroy is stale for
      // the live record and its identities — retiring would tear down live
      // state the fresh path only just built.
      return;
    }
    if (defer_forget_fanout) {
      forget_fanout_batch.push_back(address);
    } else {
      // Post-flush (snapshot-loop hazard retire): the address's
      // re-registration syncs in this same loop iteration, so the forget
      // cannot be deferred. The private-copy forget tail-forgets the surface
      // links for the same address unconditionally, so no separate link
      // forget is needed (the third call here was always a duplicate of that
      // tail call).
      FableDirectForgetTextureIdentity(address);
      FableDirectForgetPrivateTextureCopyActionsForResource(address);
    }
    const bool has_shader_identity =
        FableDirectFindShaderIdentityByObject(address) != nullptr;
    const bool has_declaration_identity =
        FableDirectFindVertexDeclarationIdentityByObject(address) != nullptr;
    if (!has_shader_identity) {
      FableDirectForgetShaderIdentity(address);
    }
    if (!has_declaration_identity) {
      FableDirectForgetVertexDeclarationIdentity(address);
    }
    if (destroyed_it != g_direct_renderer.resources.end()) {
      const bool preserve_shader =
          has_shader_identity &&
          (destroyed_it->second.kind == FableGuestResourceKind::kVertexShader ||
           destroyed_it->second.kind == FableGuestResourceKind::kPixelShader);
      const bool preserve_declaration =
          has_declaration_identity &&
          destroyed_it->second.kind == FableGuestResourceKind::kVertexDeclaration;
      if (!preserve_shader && !preserve_declaration) {
        ForgetDrawnSceneTargetLocked(address, &destroyed_it->second);
        InvalidateFableDescriptorCacheLocked();
        InvalidateDirectResourceMemosForResourceLocked(destroyed_it->second);
        ForgetDirectTextureLayoutLocked(
            destroyed_it->second.guest_texture.texture.get());
        RetirePlumeResourceLocked(std::move(destroyed_it->second),
                                  changes.serial);
        g_direct_renderer.resources.erase(destroyed_it);
        InvalidateDirectResourceFastCacheLocked();
        ++g_direct_renderer.resource_destroys;
      }
    } else {
      ForgetDrawnSceneTargetLocked(address, nullptr);
    }
    g_direct_renderer.known_uploads.erase(address);
    g_direct_renderer.registered_texture_identity_cache.erase(address);
  };
  // Drain carried-over deferred retirements first: they are strictly older
  // than this harvest's list, so retire-before-re-register order holds. The
  // whole map is scanned so an over-budget call still catches every hazard
  // entry; the membership tests are O(1) hash probes.
  uint32_t destroyed_carry_drained = 0;
  if (!g_direct_renderer.pending_destroyed_retirements.empty()) {
    std::vector<uint32_t> drained_addresses;
    for (const auto& [address, serial] :
         g_direct_renderer.pending_destroyed_retirements) {
      if (destroyed_retire_over_budget() &&
          !destroyed_retire_hazard(address)) {
        continue;
      }
      retire_destroyed_address(address, serial, true);
      drained_addresses.push_back(address);
    }
    for (uint32_t address : drained_addresses) {
      g_direct_renderer.pending_destroyed_retirements.erase(address);
    }
    destroyed_carry_drained = static_cast<uint32_t>(drained_addresses.size());
  }
  std::vector<uint32_t> acknowledged_destroyed_addresses;
  std::vector<uint64_t> acknowledged_destroyed_serials;
  acknowledged_destroyed_addresses.reserve(
      std::min(changes.destroyed_addresses.size(),
               kMaxDestroyedResourcesPerSync));
  acknowledged_destroyed_serials.reserve(
      acknowledged_destroyed_addresses.capacity());
  uint32_t destroyed_deferred = 0;
  uint32_t destroyed_carry_dropped = 0;
  for (size_t destroyed_index = 0;
       destroyed_index < changes.destroyed_addresses.size();
       ++destroyed_index) {
    const uint32_t address = changes.destroyed_addresses[destroyed_index];
    const uint64_t destroyed_serial =
        destroyed_index < changes.destroyed_serials.size()
            ? changes.destroyed_serials[destroyed_index]
            : 0ull;
    const bool within_budget =
        acknowledged_destroyed_addresses.size() <
            kMaxDestroyedResourcesPerSync &&
        !destroyed_retire_over_budget();
    if (!within_budget && !destroyed_retire_hazard(address)) {
      auto& pending = g_direct_renderer.pending_destroyed_retirements;
      auto pending_it = pending.find(address);
      if (pending_it != pending.end()) {
        pending_it->second = std::max(pending_it->second, destroyed_serial);
      } else if (pending.size() < kMaxPendingDestroyedRetirements) {
        pending.emplace(address, destroyed_serial);
      } else {
        // Overflow backstop is the staleness sweep, exactly the pre-budget
        // behaviour for entries past the 48 cap.
        ++destroyed_carry_dropped;
      }
      ++destroyed_deferred;
      continue;
    }
    retire_destroyed_address(address, destroyed_serial, false);
    // A re-delivered address (serial ack skipped on a busy call) may still
    // sit in the carry-over map; the inline retirement covers it.
    g_direct_renderer.pending_destroyed_retirements.erase(address);
    acknowledged_destroyed_addresses.push_back(address);
    acknowledged_destroyed_serials.push_back(destroyed_serial);
  }
  if (!acknowledged_destroyed_addresses.empty()) {
    AcknowledgeFableDirectDestroyedResources(acknowledged_destroyed_addresses,
                                             acknowledged_destroyed_serials);
  }
  const FableDirectClock::time_point destroyed_retire_t1 =
      FableDirectClock::now();

  // Staleness sweep 2026-07-29: Release-driven retirement can never fire for
  // guest addresses the guest never Releases — the title storybook recreates
  // panel texture headers at FRESH heap addresses every frame, and each one
  // that reached materialization pinned a VkImage forever (run 436: map grew
  // 2 -> 4618 in one title sitting, ~7-11/frame, until device memory
  // exhausted). Address-keyed identity plus Release retirement cannot bound
  // that population, so evict tracked textures no bind/sync path has
  // referenced for kStale frames. Eviction is a pure cache decision: if the
  // guest samples the address again, the normal starved-interest recovery
  // re-materializes it. Render/depth targets, channel-owned planes (Bink)
  // and scratch TEXTURES are exempt.
  //
  // Extended 2026-07-30 to vertex/index buffers (run 456: the registry grew
  // 2 -> 3896 over one static menu sit, ~5/frame, and ~98% of it was
  // kVertexBuffer). A scratch draw's stream.buffer is a bump-allocated RING
  // CURSOR, not a resource identity (see BindDirectVertexInputLocked), so
  // every frame's draws mint records at fresh cursor addresses whose storage
  // the guest ring has already recycled — on hardware a wrapped ring address
  // is the SAME storage, so a lingering cursor record is pure bookkeeping
  // and pins a live plume UPLOAD buffer forever. The observer's own scratch
  // registry is capped and trims oldest-first; our side had NO retirement at
  // all for the kind. Scratch-flagged buffers therefore age on a short TTL:
  // their bytes are consumed by the draws of the frame that allocated them,
  // a re-recorded draw re-syncs (and re-stamps) its record every record
  // pass, and in-flight GPU references are protected by the same deferred
  // RetirePlumeResourceLocked the destroyed pass uses. Non-scratch buffers
  // age on the standard TTL, same cache semantics as textures. Shaders and
  // vertex declarations stay exempt: they are few and cheap, and the
  // identity caches the record path resolves them through deliberately
  // preserve their records even across guest destroys (see preserve_shader /
  // preserve_declaration above) — evicting them would strand those
  // identities.
  //
  // Extended again 2026-07-30 (run 457) to kSurface/kDepthSurface: with the
  // buffers bounded, skip[kind=] kept growing ~+9/s at the static menu. The
  // growth is the SYNTHETIC surface mints — EnsurePrivateRectRenderTarget /
  // SetupClear / EnsureDirectResolveDestinationResourceLocked all dedup by
  // destination address, but the title recreates the private-rect panel
  // headers at FRESH heap addresses every submit (run 457: guest=0x404DB400,
  // 0x404DB3A0, 0x404DB490, ... 0x404E4610 within one second, never
  // recurring), so the dedup never fires and every abandoned header pins a
  // render-target VkImage — the same population class as the run-436
  // kTexture leak, entering under a kind the sweep excluded. No source-side
  // rekey is possible (the addresses genuinely differ; nothing wraps), so
  // they age on the standard TTL with the SAME exemption set as textures:
  // present sources, depth targets, channel-owned and scratch images stay.
  // A surface whose VkImage is still sampled or composited is re-looked-up
  // (and re-stamped) every frame, so only abandoned headers go stale.
  uint32_t stale_evict_carry_drained = 0;
  // sweepsplit census (run 513): splits the destroyed_sweep window into its
  // candidate-selection scan vs the eviction work vs the batched forget flush
  // so a regression names its phase instead of the whole window.
  double sweep_scan_ms = 0.0;
  double sweep_retire_ms = 0.0;
  uint32_t sweep_candidates = 0;
  uint32_t sweep_evicted = 0;
  {
    // kStaleResourceFrames is now file-scope (see above) so the eviction
    // predicate and the skip census cannot drift apart again.
    // Ring-cursor buffer records: the staging window between a draw's
    // interest sync and its bind is within one record pass, and every
    // re-record re-stamps, so anything unreferenced for 64 frames is storage
    // the ring has long recycled. Bounds the scratch population at roughly
    // mint-rate * 64 instead of unbounded.
    constexpr uint64_t kStaleScratchBufferFrames = 64;
    constexpr uint32_t kStaleSweepInterval = 16;
    // 256 -> 1024 with the batched forget flush (run 513): the candidate cap
    // existed to bound per-eviction forget fan-out, now one batched walk.
    // Worst-case stale backlog per 16-frame window is ~2400 at the settled
    // title's ~150/frame mint rate; 1024 per sweep plus the every-compose
    // carry drain clears it, and queue_depth in the sweepsplit census says so
    // if it ever stops being true.
    constexpr size_t kMaxStaleEvictionsPerSweep = 1024;
    // Run-489 eviction budget, retained as the burst backstop: the fan-out
    // it was built for is now the batched flush, but the remaining per-item
    // work (record teardown + plume retirement) is still bounded per call.
    // The remainder carries over in pending_stale_evictions and is drained —
    // re-validated — a budget's worth on EVERY sync call until the next
    // sweep rebuilds the list, so no dedup bookkeeping is needed.
    constexpr double kStaleEvictionBudgetMs = 4.0;
    const uint64_t sweep_frame = g_direct_renderer.current_frame_index;
    const FableDirectClock::time_point evict_t0 = FableDirectClock::now();
    const auto evict_over_budget = [&] {
      return FableDirectElapsedMs(evict_t0, FableDirectClock::now()) >=
             kStaleEvictionBudgetMs;
    };
    // Re-validated eligibility, shared by the sweep's candidate list and the
    // carry-over drain: a queued entry can be re-referenced, promoted to a
    // present source, or destroyed outright while it waits.
    const auto stale_entry_eligible = [&](uint32_t guest_address,
                                          const PlumeTrackedResource& entry) {
      const bool is_present_source =
          guest_address == g_direct_renderer.last_scene_target ||
          guest_address == g_direct_renderer.last_submitted_scene_target ||
          guest_address == g_direct_renderer.last_pix_video_target;
      const bool sweep_buffer_kind =
          entry.kind == FableGuestResourceKind::kVertexBuffer ||
          entry.kind == FableGuestResourceKind::kIndexBuffer;
      const bool sweep_image_kind =
          entry.kind == FableGuestResourceKind::kTexture ||
          entry.kind == FableGuestResourceKind::kSurface ||
          entry.kind == FableGuestResourceKind::kDepthSurface;
      // DEPTH-STENCIL LEAK FIXED 2026-08-04. This test used to read
      // `|| entry.texture_depth_target`, which is set for EVERY kDepthSurface
      // (DecodeFableTextureMetadata: `texture_depth_target = kind ==
      // kDepthSurface`). So the 2026-07-30 change that added kDepthSurface to
      // sweep_image_kind was cancelled out on the very next line and the
      // kDepthSurface arm of the eviction switch was unreachable.
      //
      // Measured, run 570: `reg[ds=]` climbed 1 -> 1230 strictly monotonically
      // across all 90 sweeps with `evicted[ds]` summing to EXACTLY 0, and
      // `skip[depth=N]` equalled `reg[ds=N]` in every single sweep — every
      // depth-stencil skipped, always, at ~7.3/s in gameplay and never freed.
      // 1229 of the registry's 1457 growth was depth. That is the registry-
      // growth degradation mechanism: gameplay mean frame time rose from
      // 39.1ms (frames 4000-4999) to 72.8ms (10000+), +86% inside one run,
      // while the lookup scan cost went 166 -> 902 entries per scan.
      //
      // A LIVE depth target cannot be evicted by removing the flag test: the
      // depth attachment is resolved through FindDirectResourceLocked (see the
      // framebuffer binding builder), and that stamps last_referenced_frame on
      // every lookup — the same mechanism that already makes kSurface eviction
      // safe. Only a header nothing has looked up for the full TTL goes. The
      // explicit last_bound_depth_target guard below covers the case anyway.
      //
      // This matters more than a memory number: rasterizing against a depth
      // target whose bounds do not match is the Xid 13 ZT violation, so depth
      // is the one kind where getting eviction wrong is a device loss. Hence
      // the second guard rather than relying on the TTL alone.
      if ((!sweep_image_kind && !sweep_buffer_kind) || is_present_source ||
          IsLiveBoundDepthTargetLocked(guest_address, entry, sweep_frame) ||
          (entry.texture_depth_target &&
           entry.kind != FableGuestResourceKind::kDepthSurface) ||
          (!sweep_buffer_kind && entry.scratch) ||
          entry.content_channel != FableGuestContentChannel::kNone ||
          entry.last_referenced_frame == 0) {
        return false;
      }
      const bool scratch_ring_cursor =
          sweep_buffer_kind && (entry.scratch || entry.guest_buffer.scratch);
      const uint64_t ttl_frames = scratch_ring_cursor
                                      ? kStaleScratchBufferFrames
                                      : kStaleResourceFrames;
      return sweep_frame - entry.last_referenced_frame > ttl_frames;
    };
    uint32_t evicted_textures = 0, evicted_surfaces = 0,
             evicted_depth_surfaces = 0, evicted_vertex_buffers = 0,
             evicted_index_buffers = 0;
    const auto evict_stale_resource = [&](uint32_t address) {
      auto stale_it = g_direct_renderer.resources.find(address);
      if (stale_it == g_direct_renderer.resources.end() ||
          !stale_entry_eligible(address, stale_it->second)) {
        return false;
      }
      switch (stale_it->second.kind) {
        case FableGuestResourceKind::kTexture:
          ++evicted_textures;
          break;
        case FableGuestResourceKind::kSurface:
          ++evicted_surfaces;
          break;
        case FableGuestResourceKind::kDepthSurface:
          ++evicted_depth_surfaces;
          break;
        case FableGuestResourceKind::kVertexBuffer:
          ++evicted_vertex_buffers;
          break;
        case FableGuestResourceKind::kIndexBuffer:
          ++evicted_index_buffers;
          break;
        default:
          break;
      }
      // Registry forget fan-out is deferred to the batched flush below —
      // evictions only ever run before it (carry drain + sweep both precede
      // the flush point).
      forget_fanout_batch.push_back(address);
      ForgetDrawnSceneTargetLocked(address, &stale_it->second);
      InvalidateFableDescriptorCacheLocked();
      InvalidateDirectResourceMemosForResourceLocked(stale_it->second);
      ForgetDirectTextureLayoutLocked(
          stale_it->second.guest_texture.texture.get());
      RetirePlumeResourceLocked(std::move(stale_it->second), changes.serial);
      g_direct_renderer.resources.erase(stale_it);
      InvalidateDirectResourceFastCacheLocked();
      g_direct_renderer.known_uploads.erase(address);
      g_direct_renderer.registered_texture_identity_cache.erase(address);
      ++g_direct_renderer.resource_destroys;
      return true;
    };
    // Between-sweep drain of the carry-over list, oldest first.
    if (!g_direct_renderer.pending_stale_evictions.empty()) {
      auto& pending = g_direct_renderer.pending_stale_evictions;
      size_t drained = 0;
      while (drained < pending.size() && !evict_over_budget()) {
        if (evict_stale_resource(pending[drained])) {
          ++stale_evict_carry_drained;
        }
        ++drained;
      }
      pending.erase(pending.begin(),
                    pending.begin() + static_cast<ptrdiff_t>(drained));
      sweep_retire_ms +=
          FableDirectElapsedMs(evict_t0, FableDirectClock::now());
    }
    if (sweep_frame > kStaleResourceFrames &&
        (sweep_frame % kStaleSweepInterval) == 0) {
      const FableDirectClock::time_point scan_t0 = FableDirectClock::now();
      std::vector<uint32_t> stale_addresses;
      // Exemption census (run 455): the registry grew 2843->5014 over a
      // settled menu sit with ZERO evictions logged — every sweep found no
      // candidate. These counters name which exemption class the growth
      // hides in. skip_kind now counts only the kinds the sweep still
      // excludes (shaders, declarations, unknown); images and buffers are
      // swept and land in the other counters. reg[] is the full registry
      // kind histogram — ground truth for which kind any future growth is.
      uint32_t skip_kind = 0, skip_present = 0, skip_depth = 0,
               skip_scratch = 0, skip_channel = 0, skip_unref = 0,
               skip_fresh = 0;
      std::array<uint32_t, 9> registry_kind_counts = {};
      for (const auto& [guest_address, entry] : g_direct_renderer.resources) {
        const size_t entry_kind_index = static_cast<size_t>(entry.kind);
        if (entry_kind_index < registry_kind_counts.size()) {
          ++registry_kind_counts[entry_kind_index];
        }
        // Run 437: blanket-exempting texture_render_target let the
        // population climb to 3281 anyway — the title panels are
        // private-rect resolve/copy DESTINATIONS, so the exact-copy
        // destination hint flags nearly all of them RT. The genuinely
        // unevictable set is tiny and explicit: the live/submitted present
        // sources, the pix-video target, and the CURRENTLY BOUND depth
        // attachment. Everything else ages out on the same TTL regardless of
        // RT usage.
        //
        // "depth targets" used to mean ALL of them here, matching the equally
        // blanket test in the eviction predicate — which is why this census
        // never graded the bug it was reporting. Run 570: `skip[depth=N]`
        // equalled `reg[ds=N]` in all 90 sweeps because both sides asked the
        // same wrong question.
        const bool is_present_source =
            guest_address == g_direct_renderer.last_scene_target ||
            guest_address == g_direct_renderer.last_submitted_scene_target ||
            guest_address == g_direct_renderer.last_pix_video_target;
        const bool sweep_buffer_kind =
            entry.kind == FableGuestResourceKind::kVertexBuffer ||
            entry.kind == FableGuestResourceKind::kIndexBuffer;
        const bool sweep_image_kind =
            entry.kind == FableGuestResourceKind::kTexture ||
            entry.kind == FableGuestResourceKind::kSurface ||
            entry.kind == FableGuestResourceKind::kDepthSurface;
        if (!sweep_image_kind && !sweep_buffer_kind) {
          ++skip_kind;
          continue;
        }
        if (is_present_source) {
          ++skip_present;
          continue;
        }
        if (IsLiveBoundDepthTargetLocked(guest_address, entry,
                                         g_direct_renderer.current_frame_index) ||
            (entry.texture_depth_target &&
             entry.kind != FableGuestResourceKind::kDepthSurface)) {
          ++skip_depth;
          continue;
        }
        if (!sweep_buffer_kind && entry.scratch) {
          // Scratch TEXTURES keep their exemption; scratch BUFFERS are the
          // ring cursors this sweep exists to retire.
          ++skip_scratch;
          continue;
        }
        if (entry.content_channel != FableGuestContentChannel::kNone) {
          ++skip_channel;
          continue;
        }
        if (entry.last_referenced_frame == 0) {
          ++skip_unref;
          continue;
        }
        const bool scratch_ring_cursor =
            sweep_buffer_kind &&
            (entry.scratch || entry.guest_buffer.scratch);
        const uint64_t ttl_frames = scratch_ring_cursor
                                        ? kStaleScratchBufferFrames
                                        : kStaleResourceFrames;
        if (sweep_frame - entry.last_referenced_frame <= ttl_frames) {
          ++skip_fresh;
          continue;
        }
        stale_addresses.push_back(guest_address);
        if (stale_addresses.size() >= kMaxStaleEvictionsPerSweep) {
          break;
        }
      }
      const FableDirectClock::time_point scan_t1 = FableDirectClock::now();
      sweep_scan_ms = FableDirectElapsedMs(scan_t0, scan_t1);
      sweep_candidates = static_cast<uint32_t>(stale_addresses.size());
      // Evict under the shared budget (already partly spent by the drain
      // above on a sweep frame); the remainder REPLACES the carry-over list
      // — anything still stale that the replacement misses is re-collected
      // by the next sweep.
      std::vector<uint32_t> deferred_evictions;
      for (size_t stale_index = 0; stale_index < stale_addresses.size();
           ++stale_index) {
        if (evict_over_budget()) {
          deferred_evictions.assign(
              stale_addresses.begin() +
                  static_cast<ptrdiff_t>(stale_index),
              stale_addresses.end());
          break;
        }
        if (evict_stale_resource(stale_addresses[stale_index])) {
          ++sweep_evicted;
        }
      }
      sweep_retire_ms += FableDirectElapsedMs(scan_t1, FableDirectClock::now());
      g_direct_renderer.pending_stale_evictions =
          std::move(deferred_evictions);
      {
        // Unconditional (time-limited): a zero-eviction sweep against a
        // growing registry is exactly the signal the silent-on-empty log
        // hid in run 455.
        static std::chrono::steady_clock::time_point last_sweep_log{};
        const auto sweep_now = std::chrono::steady_clock::now();
        if (!stale_addresses.empty() ||
            sweep_now - last_sweep_log >= std::chrono::seconds(5)) {
          last_sweep_log = sweep_now;
          static uint32_t logged_stale_sweeps = 0;
          REXLOG_INFO(
              "Refii plume stale-resource sweep #{} frame={} evicted={} "
              "evicted[tex={} surf={} ds={} vb={} ib={}] carry={} "
              "carry_drained={} resources={} "
              "skip[kind={} present={} depth={} scratch={} channel={} "
              "unref={} fresh={}] reg[u={} tex={} surf={} ds={} vb={} ib={} "
              "decl={} vs={} ps={}]",
              ++logged_stale_sweeps,
              static_cast<unsigned long long>(sweep_frame),
              static_cast<uint32_t>(stale_addresses.size()),
              evicted_textures, evicted_surfaces, evicted_depth_surfaces,
              evicted_vertex_buffers, evicted_index_buffers,
              static_cast<uint32_t>(
                  g_direct_renderer.pending_stale_evictions.size()),
              stale_evict_carry_drained,
              static_cast<uint32_t>(g_direct_renderer.resources.size()),
              skip_kind, skip_present, skip_depth, skip_scratch, skip_channel,
              skip_unref, skip_fresh, registry_kind_counts[0],
              registry_kind_counts[1], registry_kind_counts[2],
              registry_kind_counts[3], registry_kind_counts[4],
              registry_kind_counts[5], registry_kind_counts[6],
              registry_kind_counts[7], registry_kind_counts[8]);
        }
      }
    }
  }
  // Companion to the stale-RESOURCE sweep above, same cadence: the
  // private-rect action / surface-link registrations are keyed by the same
  // one-shot title header addresses but live in the guest resource registry,
  // where only destroy/evict of a TRACKED resource forgets a key. Run 473:
  // with no retirement the record loop's per-draw action/link selection grew
  // with the tables until the compose fed back on its own command backlog
  // (hold_ms 535 -> 2635 -> 7070 across three frames).
  {
    constexpr uint32_t kPrivateRegistrationSweepInterval = 16;
    if ((g_direct_renderer.current_frame_index %
         kPrivateRegistrationSweepInterval) == 0) {
      FableDirectRetireStalePrivateRegistrations(
          g_direct_renderer.current_frame_index);
    }
  }
  // Batched forget flush: ONE walk of the registry indexes for every address
  // retired above (destroyed pass + carry drains + staleness sweep) — the
  // per-address fan-out these paths used to pay inline. Must run before the
  // snapshot loop so retire-before-re-register order holds for re-registered
  // addresses (their snapshots sync only there).
  double forget_fanout_ms = 0.0;
  if (!forget_fanout_batch.empty()) {
    const FableDirectClock::time_point forget_t0 = FableDirectClock::now();
    FableDirectForgetResourceRegistrationsBatch(forget_fanout_batch);
    forget_fanout_ms = FableDirectElapsedMs(forget_t0, FableDirectClock::now());
  }
  // From here on a queued destroy retired by the snapshot loop forgets
  // inline: its re-registration syncs in the same loop iteration.
  defer_forget_fanout = false;
  const FableDirectClock::time_point destroyed_t1 = FableDirectClock::now();

  // (changed_addresses is built at the top of the destroyed pass — the
  // hazard override needs it before retirement runs.)
  std::unordered_set<uint32_t> addresses_to_sync;
  if (resource_filter) {
    for (uint32_t address : *resource_filter) {
      if (only_changed_resource_filter &&
          changed_addresses.find(address) == changed_addresses.end()) {
        continue;
      }
      AddDirectResourceInterest(addresses_to_sync, address);
    }
  } else {
    for (uint32_t address : changed_addresses) {
      AddDirectResourceInterest(addresses_to_sync, address);
    }
  }
  if (texture_upload_filter) {
    for (uint32_t address : *texture_upload_filter) {
      if (IsFableTinyTitleUploadedTextureFilterAddress(address) ||
          IsFableExactTitleUploadedTextureFilterAddress(address) ||
          IsFableTitleBillboardAtlasUploadFilterAddress(address) ||
          IsFableTitleGuiTextAtlasUploadFilterAddress(address)) {
        AddDirectResourceInterest(addresses_to_sync, address);
      }
    }
  }
  // SKIP-OPPORTUNITY PROBE (2026-07-28, temporary, measurement only — changes
  // no behaviour). drawphase shows interior=17.6ms is the largest phase of
  // record_ms, and the interior span is up to 4 SyncDirectResourcesLocked calls
  // plus the vertex/index binds, per draw. Every one of those rebuilds
  // addresses_to_sync from the draw's filter and re-fetches observer snapshots
  // for all of them, with no "already tracked and unchanged" skip anywhere.
  //
  // The obvious optimisation is to skip an address that is already in
  // g_direct_renderer.resources and is not reported changed or destroyed. That
  // is NOT safe to assume: Bink planes are linear A8 written by the CPU with no
  // Lock/Unlock traffic, so they change WITHOUT appearing in changed_addresses
  // (see refii-bink-gtl-model). Skipping them would freeze video — the known
  // black-video failure mode.
  //
  // So measure the opportunity first: how many requested addresses WOULD be
  // skippable. If the share is small the optimisation is not worth the risk; if
  // it is large, the fix needs an explicit staleness rule for CPU-written
  // linear surfaces rather than a blanket skip.
  {
    static std::atomic<uint64_t> probe_requested{0};
    static std::atomic<uint64_t> probe_skippable{0};
    static std::atomic<uint64_t> probe_calls{0};
    uint64_t skippable = 0;
    for (uint32_t address : addresses_to_sync) {
      const bool tracked = g_direct_renderer.resources.count(address) != 0;
      const bool changed =
          std::find(changes.changed_addresses.begin(),
                    changes.changed_addresses.end(),
                    address) != changes.changed_addresses.end();
      const bool destroyed =
          std::find(changes.destroyed_addresses.begin(),
                    changes.destroyed_addresses.end(),
                    address) != changes.destroyed_addresses.end();
      if (tracked && !changed && !destroyed) {
        ++skippable;
      }
    }
    const uint64_t req = probe_requested.fetch_add(addresses_to_sync.size(),
                                                   std::memory_order_relaxed) +
                         addresses_to_sync.size();
    const uint64_t skip =
        probe_skippable.fetch_add(skippable, std::memory_order_relaxed) +
        skippable;
    const uint64_t n = probe_calls.fetch_add(1, std::memory_order_relaxed) + 1u;
    // Threshold 4096 produced ZERO lines in run 375 while the sibling probes
    // logged normally, so this point is reached far less often than the ~650
    // calls/frame the interior call sites suggested — those sync calls are all
    // conditional and evidently rare. 64 confirms the actual rate.
    if ((n % 64u) == 0u) {
      REXLOG_INFO(
          "Refii sync SKIP-OPPORTUNITY calls={} requested={} skippable={} "
          "({:.1f}%)",
          static_cast<unsigned long long>(n),
          static_cast<unsigned long long>(req),
          static_cast<unsigned long long>(skip),
          req ? 100.0 * static_cast<double>(skip) / static_cast<double>(req)
              : 0.0);
    }
  }

  uint32_t shader_identity_prewarm_count = 0;
  if (resource_filter && !changed_addresses.empty()) {
    for (uint32_t address : changed_addresses) {
      if (only_changed_resource_filter &&
          resource_filter->find(address) == resource_filter->end()) {
        continue;
      }
      const FableShaderIdentity* identity =
          FableDirectFindShaderIdentityByObject(address);
      if (!identity || identity->bytecode_hash == 0 ||
          identity->kind == FableGuestResourceKind::kUnknown) {
        continue;
      }

      RendererShaderLibraryEntry entry;
      if (!FindRendererShaderLibraryEntryByGuestHash(identity->bytecode_hash,
                                                    entry)) {
        continue;
      }

      const size_t before_size = addresses_to_sync.size();
      AddDirectResourceInterest(addresses_to_sync, identity->object_shader);
      AddDirectResourceInterest(addresses_to_sync, identity->parent_shader);
      AddDirectResourceInterest(addresses_to_sync, identity->bytecode_address);
      if (addresses_to_sync.size() != before_size) {
        ++shader_identity_prewarm_count;
      }
    }
  }
  const FableDirectClock::time_point address_t1 = FableDirectClock::now();
  const std::unordered_map<uint32_t, FableGuestResourceUploadCacheEntry>*
      snapshot_known_uploads_ptr = &g_direct_renderer.known_uploads;

  std::vector<FableGuestResourceSnapshot> snapshots;
  bool snapshots_observer_busy = false;
  if (!addresses_to_sync.empty()) {
    if (wait_for_observer) {
      {
        ScopedFableDirectRegistryUnlock registry_unlock;
        snapshots = GetFableDirectResourceSnapshotsForAddressesBlocking(
            addresses_to_sync, snapshot_known_uploads_ptr,
            texture_upload_filter ? texture_upload_filter : &addresses_to_sync);
      }
      DrainPendingFableDirectTextureIdentityOperations();
    } else {
      snapshots = GetFableDirectResourceSnapshotsForAddresses(
          addresses_to_sync, snapshot_known_uploads_ptr,
          texture_upload_filter ? texture_upload_filter : &addresses_to_sync,
          &snapshots_observer_busy);
    }
  }
  const FableDirectClock::time_point snapshots_t1 = FableDirectClock::now();
  std::array<uint32_t, 9> snapshot_kind_counts = {};
  uint32_t exact_gui_snapshot_count = 0;
  for (const FableGuestResourceSnapshot& snapshot : snapshots) {
    const size_t kind_index = static_cast<size_t>(snapshot.kind);
    if (kind_index < snapshot_kind_counts.size()) {
      ++snapshot_kind_counts[kind_index];
    }
    if (IsFableExactTitleGuiTextAtlasSnapshot(snapshot)) {
      ++exact_gui_snapshot_count;
    }
  }
  // Starved-request recovery: a requested address with NO direct record and
  // NO returned snapshot is a materialization request the observer never
  // fulfilled — under contention (busy=1) the getter degrades to cache and
  // never-delivered records silently starve (0x4411D8A8 class: bound 43x,
  // tile-captured observer-side, never materialized). Retry those ONCE with
  // the blocking getter (canonical order: drop the registry across the
  // observer acquire); the once-per-address memo keeps the video publish
  // path's long observer holds from being re-stalled every frame. Records
  // created later arrive through the changed stream as normal.
  {
    static uint64_t logged_starved_snapshot_requests = 0;
    std::unordered_set<uint32_t> returned;
    returned.reserve(snapshots.size());
    for (const FableGuestResourceSnapshot& snapshot : snapshots) {
      returned.insert(snapshot.guest_address);
    }
    std::unordered_set<uint32_t> starved_retry;
    // RUN-510 BLOCKING BUDGET: at the title every compose makes ~10 sync
    // calls (compose syncs + surface-destination runs + clear retries), and
    // panel-header churn supplies FRESH never-memoized addresses to most of
    // them, so SEVERAL calls per compose paid the blocking observer acquire
    // below — sync timing showed resource_ms=194-230 per slow call (loop
    // itself ~9ms) with sync_ms up to 270 over 12 calls, stacking into the
    // multi-second title frames. One blocking acquisition per compose is the
    // materialization backstop the retry exists for; later calls' starved
    // addresses stay UN-memoized and UN-marked so the next compose's first
    // sync retries them (they lose one compose of latency, not liveness).
    static uint32_t starved_blocking_budget_frame = UINT32_MAX;
    static bool starved_blocking_spent_this_compose = false;
    if (starved_blocking_budget_frame !=
        g_direct_renderer.current_frame_index) {
      starved_blocking_budget_frame = g_direct_renderer.current_frame_index;
      starved_blocking_spent_this_compose = false;
    }
    // Run-491 dead-set feed: a still-missing address becomes compose-dead
    // when the observer was FULLY readable and still had nothing for it
    // (non-busy call, or a blocking retry — this call's or an earlier one's —
    // that recovered nothing). Busy-and-never-blocking-retried addresses stay
    // eligible for the next call. Only UNTRACKED addresses can enter, so the
    // Bink "changes without a changed_addresses entry" trap (tracked planes)
    // cannot be caught by this set.
    std::vector<uint32_t> dead_candidates;
    for (const uint32_t requested : addresses_to_sync) {
      if (returned.count(requested) != 0 ||
          g_direct_renderer.resources.count(requested) != 0) {
        continue;
      }
      ++logged_starved_snapshot_requests;
      if (logged_starved_snapshot_requests <= 48u ||
          (logged_starved_snapshot_requests % 256u) == 0u) {
        REXLOG_INFO(
            "Refii plume sync starved request #{} frame={} guest=0x{:08X} "
            "busy={} requested={} returned={}",
            logged_starved_snapshot_requests,
            g_direct_renderer.current_frame_index, requested,
            snapshots_observer_busy ? 1 : 0, addresses_to_sync.size(),
            snapshots.size());
      }
      if (snapshots_observer_busy) {
        // Four attempts, not one. `busy=1` means the observer lock was
        // contended and the cache-only fallback ran — a path with no mint
        // funnels at all — so a failure here says nothing about whether the
        // record exists. Streamed world textures in particular appear over
        // several composes. Bounded so run 487's never-materializing endpoints
        // still stop costing blocking waits.
        constexpr uint32_t kMaxStarvedBlockingRetryAttempts = 4u;
        const auto attempt_it =
            g_direct_renderer.starved_blocking_retry_attempts.find(requested);
        const uint32_t attempts =
            attempt_it ==
                    g_direct_renderer.starved_blocking_retry_attempts.end()
                ? 0u
                : attempt_it->second;
        if (attempts >= kMaxStarvedBlockingRetryAttempts) {
          dead_candidates.push_back(requested);  // budget exhausted
        } else if (!starved_blocking_spent_this_compose &&
                   starved_retry.size() < 32u) {
          // Count ONLY addresses actually queued for the blocking retry.
          // (The old code also memoized past-the-32-cap overflow addresses
          // without ever retrying them, silently treating them as retried.)
          g_direct_renderer.starved_blocking_retry_attempts[requested] =
              attempts + 1u;
          starved_retry.insert(requested);
          dead_candidates.push_back(requested);  // pending retry outcome
        }
        // else: blocking budget spent (or retry set full) this compose —
        // leave the address un-memoized and un-marked; the next compose's
        // first sync call retries it.
      } else {
        dead_candidates.push_back(requested);
      }
    }
    if (!starved_retry.empty()) {
      starved_blocking_spent_this_compose = true;
      std::vector<FableGuestResourceSnapshot> retried_snapshots;
      {
        ScopedFableDirectRegistryUnlock registry_unlock;
        retried_snapshots = GetFableDirectResourceSnapshotsForAddressesBlocking(
            starved_retry, snapshot_known_uploads_ptr, &starved_retry);
      }
      static uint32_t logged_starved_retries = 0;
      if (logged_starved_retries < 24u) {
        REXLOG_INFO(
            "Refii plume sync starved blocking retry #{} frame={} "
            "requested={} recovered={}",
            ++logged_starved_retries, g_direct_renderer.current_frame_index,
            starved_retry.size(), retried_snapshots.size());
      }
      for (FableGuestResourceSnapshot& snapshot : retried_snapshots) {
        // A recovered address is not dead this compose.
        dead_candidates.erase(std::remove(dead_candidates.begin(),
                                          dead_candidates.end(),
                                          snapshot.guest_address),
                              dead_candidates.end());
        // Recovery PROVES the address is real, so return its retry budget: a
        // later starvation of the same address is a fresh transient, not a
        // continuation of this one. Without this the budget would erode across
        // a session and land back at the permanent-give-up it replaced.
        g_direct_renderer.starved_blocking_retry_attempts.erase(
            snapshot.guest_address);
        snapshots.push_back(std::move(snapshot));
      }
    }
    for (const uint32_t address : dead_candidates) {
      if (sync_dead_addresses.size() >= 16384u) {
        sync_dead_addresses.clear();  // bounded: overflow restarts the set
      }
      sync_dead_addresses.insert(address);
    }
  }
  if (texture_upload_filter && !texture_upload_filter->empty()) {
    const bool requested_exact_gui =
        texture_upload_filter->find(0x1A0D9000u) != texture_upload_filter->end() ||
        texture_upload_filter->find(0x1A0EA000u) != texture_upload_filter->end();
    if (requested_exact_gui) {
      static uint32_t logged_exact_gui_sync_queries = 0;
      if (logged_exact_gui_sync_queries < 64u) {
        REXLOG_INFO(
            "Refii plume exact title GUI sync query #{} frame={} "
            "addresses={} snapshots={} exact_gui_snapshots={} "
            "filter_1a0d9000={} filter_1a0ea000={} resource_1a0d9000={} "
            "resource_1a0ea000={}",
            ++logged_exact_gui_sync_queries,
            g_direct_renderer.current_frame_index, addresses_to_sync.size(),
            snapshots.size(), exact_gui_snapshot_count,
            texture_upload_filter->find(0x1A0D9000u) !=
                    texture_upload_filter->end()
                ? 1
                : 0,
            texture_upload_filter->find(0x1A0EA000u) !=
                    texture_upload_filter->end()
                ? 1
                : 0,
            addresses_to_sync.find(0x1A0D9000u) != addresses_to_sync.end() ? 1
                                                                           : 0,
            addresses_to_sync.find(0x1A0EA000u) != addresses_to_sync.end() ? 1
                                                                           : 0);
      }
    }
  }
  const uint64_t uploads_before_sync = g_direct_renderer.resource_uploads;
  uint32_t inserted_resources = 0;
  uint32_t failed_resources = 0;
  uint32_t unchanged_resources = 0;
  DirectResourceSyncBreakdown sync_breakdown;
  for (const FableGuestResourceSnapshot& snapshot : snapshots) {
    const FableDirectClock::time_point snapshot_loop_t0 =
        FableDirectClock::now();
    if (DirectImageSnapshotReferencesD3DDeviceMirror(snapshot)) {
      sync_breakdown.deferred_snapshot_ms +=
          FableDirectElapsedMs(snapshot_loop_t0, FableDirectClock::now());
      ++sync_breakdown.deferred_snapshot_calls;
      ++unchanged_resources;
      continue;
    }
    // A snapshot arriving for an address (or its parent alias) proves it is
    // not compose-dead — un-mark before the sync so later calls this compose
    // see the fresh record.
    sync_dead_addresses.erase(snapshot.guest_address);
    if (snapshot.parent_address != 0) {
      sync_dead_addresses.erase(snapshot.parent_address);
    }
    // Retire-before-re-register, enforced at the only point a re-registration
    // takes effect: a snapshot about to sync for an address with a queued
    // destroy retires the old record FIRST (the ordering rule the destroyed
    // pass's hazard override used to over-approximate with filter membership
    // — see the run-490 note above the destroyed pass). The carried-serial
    // guard inside retire_destroyed_address keeps a stale queued destroy from
    // tearing down state a newer registration already rebuilt.
    {
      auto pending_retire_it = g_direct_renderer.pending_destroyed_retirements
                                   .find(snapshot.guest_address);
      if (pending_retire_it !=
          g_direct_renderer.pending_destroyed_retirements.end()) {
        const uint64_t pending_serial = pending_retire_it->second;
        g_direct_renderer.pending_destroyed_retirements.erase(
            pending_retire_it);
        retire_destroyed_address(snapshot.guest_address, pending_serial, true);
      }
    }
    const FableGuestResourceUploadCacheEntry upload_entry =
        UploadCacheEntryForSnapshot(snapshot);
    const auto known_it =
        g_direct_renderer.known_uploads.find(snapshot.guest_address);
	    const auto tracked_it =
	        g_direct_renderer.resources.find(snapshot.guest_address);
	    const bool registered_private_proxy_source =
	        snapshot.kind == FableGuestResourceKind::kTexture &&
	        IsFableTitlePrivateTextureProxyAddress(snapshot.guest_address) &&
	        (FableDirectFindPrivateTextureCopyActionForDestinationTexture(
	             snapshot.guest_address) != nullptr ||
	         FableDirectFindSurfaceTextureLinkForDestinationTexture(
	             snapshot.guest_address) != nullptr);
	    const bool requested_title_texture_payload =
	        snapshot.kind == FableGuestResourceKind::kTexture &&
	        snapshot.upload_data.empty() && texture_upload_filter &&
	        !registered_private_proxy_source &&
	        (IsFableTitlePrivateTextureProxyAddress(snapshot.guest_address) ||
	         IsFableExactTitleUploadedSnapshot(snapshot)) &&
	        DirectSnapshotMatchesResourceFilter(snapshot, *texture_upload_filter) &&
	        (tracked_it == g_direct_renderer.resources.end() ||
	         !FableTrackedResourceHasMeaningfulTextureUpload(tracked_it->second));
	    const bool exact_gui_snapshot =
	        IsFableExactTitleGuiTextAtlasSnapshot(snapshot);
    const bool exact_billboard_snapshot =
        IsFableDirectTitleBillboardAtlasSnapshot(snapshot);
    const std::array<uint32_t, 6>& snapshot_fetch_dwords =
        HasTrackedFetchDwords(snapshot.texture.normalized_fetch_dwords)
            ? snapshot.texture.normalized_fetch_dwords
            : snapshot.texture.raw_fetch_dwords;
    const DirectTextureFetchInfo snapshot_fetch_info =
        DirectTextureFetchInfoFromDwords(snapshot_fetch_dwords);
	    const bool unchanged_tracked_resource =
	        known_it != g_direct_renderer.known_uploads.end() &&
	        SameDirectUploadCacheEntry(known_it->second, upload_entry) &&
	        tracked_it != g_direct_renderer.resources.end();
	    const bool exact_gui_tracked_upload_matches_snapshot =
	        exact_gui_snapshot && tracked_it != g_direct_renderer.resources.end() &&
	        PlumeGuestImageReady(tracked_it->second) &&
	        FableTrackedResourceHasMeaningfulTextureUpload(tracked_it->second) &&
	        tracked_it->second.texture_upload_hash == snapshot.texture.upload_hash &&
	        tracked_it->second.upload_generation == snapshot.upload_generation &&
	        tracked_it->second.texture_upload_size == snapshot.upload_data.size() &&
	        tracked_it->second.texture_upload_size != 0;
    const bool exact_billboard_tracked_upload_matches_snapshot =
        exact_billboard_snapshot &&
        tracked_it != g_direct_renderer.resources.end() &&
        snapshot.upload_data.empty() && snapshot_fetch_info.valid &&
        PlumeGuestImageReady(tracked_it->second) &&
        DirectFableTitleBillboardAtlasHasAuthoritativeUpload(
            tracked_it->second) &&
        PlumeGuestTextureBaseAddress(tracked_it->second) ==
            snapshot.texture.base_address &&
        PlumeGuestTextureWidth(tracked_it->second) ==
            snapshot_fetch_info.width &&
        PlumeGuestTextureHeight(tracked_it->second) ==
            snapshot_fetch_info.height;
    const FableDirectClock::time_point snapshot_classify_t1 =
        FableDirectClock::now();
    sync_breakdown.snapshot_classify_ms +=
        FableDirectElapsedMs(snapshot_loop_t0, snapshot_classify_t1);
		    if (exact_gui_snapshot) {
		      static uint32_t logged_exact_gui_sync_snapshots = 0;
		      if (logged_exact_gui_sync_snapshots < 64u) {
		        const PlumeTrackedResource* tracked_before =
		            tracked_it != g_direct_renderer.resources.end()
		                ? &tracked_it->second
		                : nullptr;
		        REXLOG_INFO(
		            "Refii plume exact title GUI sync snapshot #{} frame={} "
		            "guest=0x{:08X} object=0x{:08X} parent=0x{:08X} "
		            "base=0x{:08X} bytes={} upload={} gen={} "
		            "hash=0x{:016X} known={} tracked=0x{:08X} "
		            "tracked_ready={} tracked_upload={} requested_payload={} "
		            "filter_match={}",
		            ++logged_exact_gui_sync_snapshots,
		            g_direct_renderer.current_frame_index, snapshot.guest_address,
		            snapshot.texture.object_address, snapshot.parent_address,
		            snapshot.texture.base_address, snapshot.texture.byte_size,
		            snapshot.upload_data.size(),
		            static_cast<unsigned long long>(snapshot.upload_generation),
		            static_cast<unsigned long long>(snapshot.texture.upload_hash),
		            known_it != g_direct_renderer.known_uploads.end() ? 1 : 0,
		            tracked_before ? tracked_before->guest_address : 0u,
		            tracked_before && PlumeGuestImageReady(*tracked_before) ? 1
		                                                                    : 0,
		            tracked_before &&
		                    FableTrackedResourceHasMeaningfulTextureUpload(*tracked_before)
		                ? 1
		                : 0,
		            requested_title_texture_payload ? 1 : 0,
		            texture_upload_filter &&
		                    DirectSnapshotMatchesResourceFilter(
		                        snapshot, *texture_upload_filter)
		                ? 1
		                : 0);
		      }
		    }
		    if (exact_gui_snapshot && !requested_title_texture_payload &&
		        unchanged_tracked_resource && exact_gui_tracked_upload_matches_snapshot) {
	      ++unchanged_resources;
	      static uint32_t logged_exact_unchanged_gui_sync_snapshots = 0;
	      if (logged_exact_unchanged_gui_sync_snapshots < 64u) {
	        REXLOG_INFO(
	            "Refii plume exact unchanged title GUI sync snapshot covered #{} "
	            "frame={} guest=0x{:08X} base=0x{:08X} bytes={} gen={} "
	            "hash=0x{:016X} upload_size={} tracked_gen={} "
	            "tracked_hash=0x{:016X}",
	            ++logged_exact_unchanged_gui_sync_snapshots,
	            g_direct_renderer.current_frame_index, snapshot.guest_address,
	            snapshot.texture.base_address, snapshot.texture.byte_size,
	            static_cast<unsigned long long>(snapshot.upload_generation),
	            static_cast<unsigned long long>(snapshot.texture.upload_hash),
	            static_cast<unsigned long long>(
	                tracked_it->second.texture_upload_size),
	            static_cast<unsigned long long>(
	                tracked_it->second.upload_generation),
	            static_cast<unsigned long long>(
	                tracked_it->second.texture_upload_hash));
	      }
      sync_breakdown.deferred_snapshot_ms +=
          FableDirectElapsedMs(snapshot_classify_t1, FableDirectClock::now());
      ++sync_breakdown.deferred_snapshot_calls;
	      continue;
	    }
    if (exact_billboard_tracked_upload_matches_snapshot) {
      ++unchanged_resources;
      static uint32_t logged_preserved_billboard_upload_snapshots = 0;
      if (logged_preserved_billboard_upload_snapshots < 64u) {
        REXLOG_INFO(
            "Refii plume preserved title billboard upload on empty snapshot #{} "
            "frame={} guest=0x{:08X} tracked=0x{:08X} base=0x{:08X} "
            "{}x{} upload_size={} gen={} hash=0x{:016X}",
            ++logged_preserved_billboard_upload_snapshots,
            g_direct_renderer.current_frame_index, snapshot.guest_address,
            tracked_it->second.guest_address, snapshot.texture.base_address,
            snapshot_fetch_info.width, snapshot_fetch_info.height,
            static_cast<unsigned long long>(
                tracked_it->second.texture_upload_size),
            static_cast<unsigned long long>(tracked_it->second.upload_generation),
            static_cast<unsigned long long>(
                tracked_it->second.texture_upload_hash));
      }
      sync_breakdown.deferred_snapshot_ms +=
          FableDirectElapsedMs(snapshot_classify_t1, FableDirectClock::now());
      ++sync_breakdown.deferred_snapshot_calls;
      continue;
    }
    if (!requested_title_texture_payload && snapshot.upload_data.empty() &&
        unchanged_tracked_resource) {
      ++unchanged_resources;
      sync_breakdown.deferred_snapshot_ms +=
          FableDirectElapsedMs(snapshot_classify_t1, FableDirectClock::now());
      ++sync_breakdown.deferred_snapshot_calls;
      continue;
    }
    const bool inserted =
        g_direct_renderer.resources.find(snapshot.guest_address) ==
        g_direct_renderer.resources.end();
    const FableDirectClock::time_point tracked_sync_t0 =
        FableDirectClock::now();
    if (!SyncTrackedResourceLocked(device, snapshot, &sync_breakdown)) {
      sync_breakdown.tracked_sync_ms +=
          FableDirectElapsedMs(tracked_sync_t0, FableDirectClock::now());
      ++g_direct_renderer.failed_resource_syncs;
      ++failed_resources;
      LogDirectResourceSyncFailureLocked(snapshot, "resource_slice");
      continue;
    }
    const FableDirectClock::time_point tracked_sync_t1 =
        FableDirectClock::now();
    sync_breakdown.tracked_sync_ms +=
        FableDirectElapsedMs(tracked_sync_t0, tracked_sync_t1);
    if (inserted) {
      ++g_direct_renderer.resource_creations;
      ++inserted_resources;
    }
    if (exact_gui_snapshot) {
      static uint32_t logged_exact_gui_sync_results = 0;
      if (logged_exact_gui_sync_results < 64u) {
        const PlumeTrackedResource* tracked_after =
            FindDirectResourceLocked(snapshot.guest_address);
        REXLOG_INFO(
            "Refii plume exact title GUI sync result #{} frame={} "
            "guest=0x{:08X} inserted={} tracked=0x{:08X} ready={} "
            "upload={} size={} gen={} hash=0x{:016X}",
            ++logged_exact_gui_sync_results,
            g_direct_renderer.current_frame_index, snapshot.guest_address,
            inserted ? 1 : 0,
            tracked_after ? tracked_after->guest_address : 0u,
            tracked_after && PlumeGuestImageReady(*tracked_after) ? 1 : 0,
            tracked_after && tracked_after->texture_upload_has_source ? 1 : 0,
            tracked_after
                ? static_cast<unsigned long long>(
                      tracked_after->texture_upload_size)
                : 0ull,
            tracked_after
                ? static_cast<unsigned long long>(
                      tracked_after->upload_generation)
                : 0ull,
            tracked_after
                ? static_cast<unsigned long long>(
                      tracked_after->texture_upload_hash)
                : 0ull);
      }
    }
    if (snapshot.kind == FableGuestResourceKind::kTexture &&
        snapshot.upload_data.empty() &&
        (IsFableTitlePrivateTextureProxyAddress(snapshot.guest_address) ||
         requested_title_texture_payload) &&
        !registered_private_proxy_source &&
        !FableTrackedResourceHasMeaningfulTextureUpload(
            g_direct_renderer.resources[snapshot.guest_address])) {
      g_direct_renderer.known_uploads.erase(snapshot.guest_address);
    } else {
      g_direct_renderer.known_uploads[snapshot.guest_address] = upload_entry;
    }
    sync_breakdown.post_sync_ms +=
        FableDirectElapsedMs(tracked_sync_t1, FableDirectClock::now());
  }
  const FableDirectClock::time_point resource_sync_t1 =
      FableDirectClock::now();

  if (texture_upload_filter && !texture_upload_filter->empty()) {
    RecordPendingTextureUploadsLocked(command_list, texture_upload_filter);
  }
  RecordPendingSurfaceContentCarriesLocked(command_list);
  const FableDirectClock::time_point pending_t1 = FableDirectClock::now();
  if (!changes.observer_busy && !snapshots_observer_busy) {
    g_direct_renderer.resource_serial = changes.serial;
    AcknowledgeFableDirectResourceChanges(changes.serial);
  }
  const FableDirectClock::time_point acknowledge_t1 = FableDirectClock::now();
  const double sync_total_ms = FableDirectElapsedMs(sync_t0, acknowledge_t1);
  if (sync_total_ms >= 10.0 ||
      g_direct_renderer.current_frame_index % 120u == 0) {
    // Time-limited, not lifetime-capped: the old 512-print cap went dark 90s
    // into a title sit (run 480 lost all settled sync data past frame 2823) —
    // same trap the compose timing line hit and fixed.
    static uint32_t logged_direct_sync_timings = 0;
    static FableDirectClock::time_point last_sync_timing_log{};
    if (logged_direct_sync_timings == 0 ||
        FableDirectElapsedMs(last_sync_timing_log, acknowledge_t1) >= 1500.0) {
      last_sync_timing_log = acknowledge_t1;
      REXLOG_INFO(
          "Refii plume direct sync timing #{} frame={} total_ms={:.3f} "
          "changes_ms={:.3f} changes_lock_ms={:.3f} changes_drain_ms={:.3f} "
          "changes_refresh_ms={:.3f} destroyed_ms={:.3f} "
          "destroyed_retire_ms={:.3f} destroyed_sweep_ms={:.3f} "
          "address_ms={:.3f} "
          "snapshot_ms={:.3f} resource_ms={:.3f} pending_upload_ms={:.3f} "
          "ack_ms={:.3f} changed={} destroyed={}/{} destroyed_deferred={} "
          "destroyed_carry={} carry_drained={} carry_dropped={} "
          "evict_queue={} evict_drained={} address_sync={} "
          "snapshots={} inserted={} failed={} unchanged={} upload_delta={} "
          "texture_filter={} resource_filter={} kinds={} {} {} {} {} {} {} {} {} "
          "fast=0 early_outs={} dead={} snapshot_busy={} uploads={}",
          ++logged_direct_sync_timings, g_direct_renderer.current_frame_index,
          sync_total_ms, FableDirectElapsedMs(sync_t0, changes_t1),
          changes.lock_wait_ms, changes.teardown_drain_ms, changes.refresh_ms,
          FableDirectElapsedMs(changes_t1, destroyed_t1),
          FableDirectElapsedMs(changes_t1, destroyed_retire_t1),
          FableDirectElapsedMs(destroyed_retire_t1, destroyed_t1),
          FableDirectElapsedMs(destroyed_t1, address_t1),
          FableDirectElapsedMs(address_t1, snapshots_t1),
          FableDirectElapsedMs(snapshots_t1, resource_sync_t1),
          FableDirectElapsedMs(resource_sync_t1, pending_t1),
          FableDirectElapsedMs(pending_t1, acknowledge_t1),
          changes.changed_addresses.size(), acknowledged_destroyed_addresses.size(),
          changes.destroyed_addresses.size(), destroyed_deferred,
          static_cast<uint32_t>(
              g_direct_renderer.pending_destroyed_retirements.size()),
          destroyed_carry_drained, destroyed_carry_dropped,
          static_cast<uint32_t>(
              g_direct_renderer.pending_stale_evictions.size()),
          stale_evict_carry_drained,
          addresses_to_sync.size(), snapshots.size(), inserted_resources,
          failed_resources, unchanged_resources,
          g_direct_renderer.resource_uploads - uploads_before_sync,
          texture_upload_filter ? texture_upload_filter->size() : 0u,
          resource_filter ? resource_filter->size() : 0u,
          snapshot_kind_counts[0], snapshot_kind_counts[1],
          snapshot_kind_counts[2], snapshot_kind_counts[3],
          snapshot_kind_counts[4], snapshot_kind_counts[5],
          snapshot_kind_counts[6], snapshot_kind_counts[7],
          snapshot_kind_counts[8], g_direct_sync_early_outs_this_compose,
          static_cast<uint32_t>(sync_dead_addresses.size()),
          snapshots_observer_busy ? 1 : 0,
          g_direct_renderer.resource_uploads);
      // Destroyed-sweep phase split (run 513). candidates/scan_ms are the
      // sweep's full-population candidate scan (zero on non-sweep frames),
      // retired/retire_ms the eviction work (carry drain + sweep), and
      // forget[] the ONE batched registry forget walk covering every
      // retirement this call — the fan-out that used to be paid per address
      // inside retire_ms and destroyed_retire_ms.
      REXLOG_INFO(
          "Refii plume direct sync sweepsplit[candidates={} retired={} "
          "queue_depth={} scan_ms={:.3f} retire_ms={:.3f}] "
          "forget[batch={} ms={:.3f}]",
          sweep_candidates, stale_evict_carry_drained + sweep_evicted,
          static_cast<uint32_t>(
              g_direct_renderer.pending_stale_evictions.size()),
          sweep_scan_ms, sweep_retire_ms,
          static_cast<uint32_t>(forget_fanout_batch.size()),
          forget_fanout_ms);
      if (shader_identity_prewarm_count != 0) {
        REXLOG_INFO(
            "Refii plume direct shader identity prewarm frame={} count={} "
            "resource_filter={} changed={}",
            g_direct_renderer.current_frame_index,
            shader_identity_prewarm_count,
            resource_filter ? resource_filter->size() : 0u,
            changes.changed_addresses.size());
      }
      REXLOG_INFO(
          "Refii plume direct resource breakdown frame={} "
          "kind_ms={:.3f}/{:.3f}/{:.3f}/{:.3f}/{:.3f}/{:.3f}/{:.3f}/{:.3f}/{:.3f} "
          "kind_calls={} {} {} {} {} {} {} {} {} "
          "image_shape_ms={:.3f} image_meta_ms={:.3f} "
          "image_create_ms={:.3f} image_view_ms={:.3f} "
          "image_upload_ms={:.3f} image_upload_calls={} "
          "classify_ms={:.3f} tracked_sync_ms={:.3f} "
          "post_sync_ms={:.3f} lookup_ms={:.3f} "
          "texture_identity_ms={:.3f}/{}/{} "
          "shader_decl_identity_ms={:.3f}/{} "
          "deferred_snapshot_ms={:.3f}/{} resources={}",
          g_direct_renderer.current_frame_index, sync_breakdown.kind_ms[0],
          sync_breakdown.kind_ms[1], sync_breakdown.kind_ms[2],
          sync_breakdown.kind_ms[3], sync_breakdown.kind_ms[4],
          sync_breakdown.kind_ms[5], sync_breakdown.kind_ms[6],
          sync_breakdown.kind_ms[7], sync_breakdown.kind_ms[8],
          sync_breakdown.kind_calls[0], sync_breakdown.kind_calls[1],
          sync_breakdown.kind_calls[2], sync_breakdown.kind_calls[3],
          sync_breakdown.kind_calls[4], sync_breakdown.kind_calls[5],
          sync_breakdown.kind_calls[6], sync_breakdown.kind_calls[7],
          sync_breakdown.kind_calls[8], sync_breakdown.image_shape_ms,
          sync_breakdown.image_metadata_ms, sync_breakdown.image_create_ms,
          sync_breakdown.image_view_ms, sync_breakdown.image_upload_ms,
          sync_breakdown.image_upload_calls,
          sync_breakdown.snapshot_classify_ms, sync_breakdown.tracked_sync_ms,
          sync_breakdown.post_sync_ms, sync_breakdown.resource_lookup_ms,
          sync_breakdown.texture_identity_ms,
          sync_breakdown.texture_identity_calls,
          sync_breakdown.texture_identity_skips,
          sync_breakdown.shader_decl_identity_ms,
          sync_breakdown.shader_decl_identity_calls,
          sync_breakdown.deferred_snapshot_ms,
          sync_breakdown.deferred_snapshot_calls,
          sync_breakdown.resources_size);
    }
  }
}
#endif


#if REFII_RENDERER_HAS_PLUME

void InitializeFableDirectRenderer(plume::RenderDevice* device,
                                   plume::RenderShaderFormat shader_format) {
  std::lock_guard<std::mutex> lock(g_direct_renderer.mutex);
  g_direct_renderer.device = device;
  g_direct_renderer.shader_format = shader_format;
  ResetDirectDiagnosticsLocked();
  g_direct_renderer.resource_serial = 0;
  g_direct_renderer.resource_creations = 0;
  g_direct_renderer.resource_destroys = 0;
  g_direct_renderer.resource_uploads = 0;
  g_direct_renderer.failed_resource_syncs = 0;
  g_direct_renderer.cached_hook_stats = RendererHookObserverSnapshot{};
  g_direct_renderer.have_cached_hook_stats = false;
  g_direct_renderer.last_hook_stats_frame = 0;
  g_direct_renderer.last_render_target_switch_count = 0;
  g_direct_renderer.last_depth_target_switch_count = 0;
  g_direct_renderer.resources.clear();
  InvalidateDirectResourceFastCacheLocked();
  ClearDirectResourceMemosLocked();
  g_direct_renderer.known_uploads.clear();
  g_direct_renderer.registered_texture_identity_cache.clear();
  g_direct_renderer.pipelines.clear();
  ClearDirectPipelineEnsureMemoLocked();
  g_direct_renderer.tess_vertex_shader_variants.clear();
  g_direct_renderer.tess_vertex_shader_variant_misses.clear();
  g_direct_renderer.tess_grid_index_buffers.clear();
  g_direct_renderer.frame_upload_buffers.clear();
  g_direct_renderer.cached_upload_buffers.clear();
  g_direct_renderer.clear_framebuffer_memo.clear();
  g_direct_renderer.private_texture_aliases_by_fetch.clear();
  g_direct_renderer.private_texture_aliases_by_base.clear();
  g_direct_renderer.private_texture_aliases_by_object.clear();
  g_direct_renderer.gpu_drawn_textures_by_base.clear();
  g_direct_renderer.private_texture_alias_serial = 0;
  ResetDirectPrivateFetchAliasFrameCountersLocked();
  g_direct_renderer.private_texture_feedback_aliases.clear();
  g_direct_renderer.private_texture_feedback_aliases_by_target_identity.clear();
  g_direct_renderer.private_texture_feedback_alias_target_serial.clear();
  g_direct_renderer.private_texture_feedback_alias_order.clear();
  g_direct_renderer.private_feedback_ensure_memo.clear();
  g_direct_renderer.surface_alias_copy_cache.clear();
  g_direct_renderer.private_texture_copy_action_cache.clear();
  g_direct_renderer.private_texture_copy_action_sample_cache.clear();
  g_direct_renderer.private_texture_exact_interest_cache.clear();
  g_direct_renderer.mutable_private_header_exact_samples.clear();
  g_direct_renderer.private_texture_backing_miss_cache.clear();
  g_direct_renderer.registered_private_rect_execute_decision_cache.clear();
  g_direct_renderer.command_input_ready_cache.clear();
  g_direct_renderer.exact_current_producer_owner_cache.clear();
  g_direct_renderer.surface_texture_link_sample_frame_cache.clear();
  g_direct_renderer.surface_destination_copy_frame_keys.clear();
  g_direct_renderer.prepared_exact_copy_destination_keys.clear();
  g_direct_renderer.published_stable_surface_texture_link_keys.clear();
  g_direct_renderer.materialized_private_rect_action_frame_keys.clear();
  g_direct_renderer.begin_vertices_title_action_frame_keys.clear();
  g_direct_renderer.preflight_private_copy_action_frame_keys.clear();
  g_direct_renderer.remembered_private_copy_action_frame_keys.clear();
  g_direct_renderer.stable_private_copy_action_keys.clear();
  g_direct_renderer.remembered_private_rect_target_source_frame_keys.clear();
  g_direct_renderer.private_copy_action_surface_link_frame_keys.clear();
  g_direct_renderer.materialized_private_surface_link_frame_keys.clear();
  g_direct_renderer.remembered_private_feedback_surface_link_frame_keys.clear();
  g_direct_renderer.exact_private_rect_edge_frame_keys.clear();
  g_direct_renderer.current_target_surface_link_frame_keys.clear();
  g_direct_renderer.sibling_source_action_owner_frame_keys.clear();
  g_direct_renderer.surface_link_command_identity_exclusion_frame_keys.clear();
  g_direct_renderer.targetless_wrapper_materialization_reject_frame_keys.clear();
  g_direct_renderer.materialized_private_rect_actions_by_exact_key.clear();
  g_direct_renderer.next_private_texture_upload_alias = 0xF1000000u;
  g_direct_renderer.next_title_billboard_upload_alias = 0xF2000000u;
  g_direct_renderer.cached_upload_buffer_count = 0;
  g_direct_renderer.cached_upload_buffer_trim_frame = UINT32_MAX;
  DestroyDirectConstantArenaLocked();
  g_direct_renderer.default_sampler.reset();
  ResetFableDirectLivePresentSourcesLocked(false);
  g_direct_renderer.current_frame_index = 0;
  g_direct_renderer.current_frame_has_pix_video_draw = false;
  ResetPixVideoPresentTargetLocked();
  g_direct_renderer.scene_write_serial = 0;
  g_direct_renderer.draw_resources_ready = false;
  g_direct_renderer.last_failed_direct_draw_reason.clear();
  g_direct_renderer.last_failed_direct_draw_serial = 0;
  FableDirectClearPrivateTextureCopyActions();
  FableDirectClearTextureIdentities();
  FableDirectClearTextureHeaderActions();
  FableDirectClearShaderIdentities();
  FableDirectClearVertexDeclarationIdentities();
  g_direct_renderer.present.last_logged_source = 0;
  g_direct_renderer.present.last_logged_width = 0;
  g_direct_renderer.present.last_logged_height = 0;
  g_direct_renderer.present.last_logged_detail_serial = 0;
  g_direct_renderer.present.last_logged_detail_target = 0;
}

// Draw packets carried across pump frames that composed no vertex draws (see the
// PACKET CARRY note in RecordFableDirectFrame). Touched only from that function,
// which runs on the single present-pump thread under g_direct_renderer.mutex.
std::vector<refii::gtl::DrawPacket> g_gtl_carried_draw_packets;

// GUEST-FRAME HOLD (authoritative present model). The present pump fires several
// times per guest frame (guest Present 0x82B6F1D0 runs ~8fps), but the packet
// drain CONSUMES a guest frame's packets on the first pump frame past its
// boundary. The following pump frames inside the SAME guest frame then drain
// nothing, and with the observer submission gone they composed an EMPTY
// framebuffer -> full/blank flashing. Retain the last non-empty guest-frame
// packet set keyed by its boundary and re-present it on those redundant pump
// frames, so every presented frame carries the current guest frame's content --
// the continuous re-submission the observer used to provide, now sourced purely
// from the GTL stream. Authoritative mode only; touched on the pump thread under
// g_direct_renderer.mutex.
uint64_t g_gtl_held_guest_frame_boundary = 0;
std::vector<refii::gtl::DrawPacket> g_gtl_held_draw_packets;
std::vector<refii::gtl::ResolvePacket> g_gtl_held_resolve_packets;
std::vector<refii::gtl::ClearPacket> g_gtl_held_clear_packets;
std::vector<refii::gtl::TessellatedPatchDrawPacket> g_gtl_held_tess_packets;

// See the header comment. Mirrors the inputs RecordFableDirectFrame's drain +
// GUEST-FRAME HOLD would act on, without draining anything: if every check
// says "nothing new", a record would reproduce the held packet set byte for
// byte, so the caller may re-present its last offscreen compose instead.
FableDirectRecordReason FableDirectFrameRecordReason() {
  if (!RendererGtlAuthoritativeSubmissionEnabled()) {
    return FableDirectRecordReason::kNotAuthoritative;
  }
  if (FableStartupVideoPumpActive()) {
    return FableDirectRecordReason::kStartupVideoPump;
  }
  const uint64_t boundary =
      refii::gtl::GetStatedPresentSource().guest_frame_boundary_packet_id;
  if (boundary == 0u || boundary != g_gtl_held_guest_frame_boundary) {
    return FableDirectRecordReason::kGuestBoundary;
  }
  if (!g_gtl_carried_draw_packets.empty()) {
    return FableDirectRecordReason::kCarriedPackets;
  }
  if (refii::gtl::HasPendingPacketsUpTo(boundary)) {
    // Would change the held set via the drained_new_content branch of the
    // GUEST-FRAME HOLD. Self-limiting: the record this answer forces drains
    // exactly these packets.
    return FableDirectRecordReason::kPendingPackets;
  }
  return FableDirectRecordReason::kNone;
}

void RecordFableDirectFrame(plume::RenderDevice* device,
                            plume::RenderCommandList* command_list,
                            plume::RenderFramebuffer* output_framebuffer,
                            plume::RenderTexture* output_texture,
                            plume::RenderFramebuffer* present_framebuffer,
                            plume::RenderShaderFormat shader_format,
                            plume::RenderFormat output_depth_format,
                            uint32_t frame_index) {
  ZoneScopedN("RefiiPlume/FableDirectRenderer");

  refii::gtl::OnFrameBoundary();
  // GTL consumer seam (§3.4 step 1: replay packets in order). Execution
  // still flows through the direct command path; the drained batch feeds the
  // shadow validator below (stash happens under the renderer mutex).
  // Guest-frame alignment for the authoritative replay: drain only COMPLETED
  // guest frames (packet_id < the boundary captured at the last guest Present),
  // leaving in-progress packets for next time. The observer command batch was
  // implicitly guest-frame aligned; the GTL streams are pump-frame drained and
  // the pump over-runs the guest ~1.75x, so an unbounded drain hands the replay
  // a FRACTION of the frame (run 218: gtl_draws=1 vs observer submitted=32). The
  // in-place merge does not need this (it aligns to the observer batch), so keep
  // the unbounded drains there.
  const bool gtl_authoritative_drain =
      RendererGtlAuthoritativeSubmissionEnabled();
  const uint64_t gtl_guest_frame_boundary =
      gtl_authoritative_drain
          ? refii::gtl::GetStatedPresentSource().guest_frame_boundary_packet_id
          : 0u;
  std::vector<refii::gtl::DrawPacket> gtl_packets =
      gtl_authoritative_drain
          ? refii::gtl::DrainDrawPacketsUpTo(gtl_guest_frame_boundary)
          : refii::gtl::DrainDrawPackets();
  // PACKET CARRY (run 207 frame 2640: gtl_packets=0 against 154 observer draws).
  // The guest emits its draws in a burst, but the present pump interleaves heavy
  // compose frames (submitted=168) with light ones (submitted=7..20). The drain
  // takes EVERYTHING each pump frame and the stash below used to REPLACE, so a
  // light frame that happened to run between the burst and its compose swallowed
  // the burst and dropped it — the compose frame then had no packets to match
  // and every draw fell back to the observer. That is packet starvation, not an
  // identity mismatch, and it also strands video compose packets the same way.
  // Carry packets forward across frames that composed no vertex draws so they
  // are still there when their compose frame arrives.
  if (!g_gtl_carried_draw_packets.empty()) {
    gtl_packets.insert(
        gtl_packets.begin(),
        std::make_move_iterator(g_gtl_carried_draw_packets.begin()),
        std::make_move_iterator(g_gtl_carried_draw_packets.end()));
    g_gtl_carried_draw_packets.clear();
  }
  std::vector<refii::gtl::ResolvePacket> gtl_resolve_packets =
      gtl_authoritative_drain
          ? refii::gtl::DrainResolvePacketsUpTo(gtl_guest_frame_boundary)
          : refii::gtl::DrainResolvePackets();
  // Clear class: drained per frame and stashed below — the submission merge
  // emits these at packet_id positions and drops observer kClear commands
  // (renderer_gtl_clears_authoritative).
  std::vector<refii::gtl::ClearPacket> gtl_clear_packets =
      gtl_authoritative_drain
          ? refii::gtl::DrainClearPacketsUpTo(gtl_guest_frame_boundary)
          : refii::gtl::DrainClearPackets();
  // T2: tessellated patch draws (terrain/water) ride the same packet_id spine
  // and interleave at their captured position alongside resolves/clears.
  std::vector<refii::gtl::TessellatedPatchDrawPacket> gtl_tess_packets =
      gtl_authoritative_drain
          ? refii::gtl::DrainTessellatedPatchDrawPacketsUpTo(
                gtl_guest_frame_boundary)
          : refii::gtl::DrainTessellatedPatchDrawPackets();
  // GUEST-FRAME HOLD (see g_gtl_held_* above). Only in authoritative mode: the
  // in-place merge already re-composes continuously off the observer batch.
  if (gtl_authoritative_drain) {
    const bool drained_new_content = !gtl_packets.empty() ||
                                     !gtl_resolve_packets.empty() ||
                                     !gtl_clear_packets.empty() ||
                                     !gtl_tess_packets.empty();
    if (drained_new_content) {
      // New packets for this (or a newer) guest frame -> this becomes the set we
      // re-present on the redundant pump frames that follow within it.
      g_gtl_held_draw_packets = gtl_packets;
      g_gtl_held_resolve_packets = gtl_resolve_packets;
      g_gtl_held_clear_packets = gtl_clear_packets;
      g_gtl_held_tess_packets = gtl_tess_packets;
      g_gtl_held_guest_frame_boundary = gtl_guest_frame_boundary;
    } else if (gtl_guest_frame_boundary == g_gtl_held_guest_frame_boundary &&
               !g_gtl_held_draw_packets.empty()) {
      // Redundant pump frame inside the SAME guest frame (boundary did not
      // advance and nothing new drained) -> re-present the held content instead
      // of composing black. A genuinely-new blank guest frame advances the
      // boundary and is intentionally left empty.
      gtl_packets = g_gtl_held_draw_packets;
      gtl_resolve_packets = g_gtl_held_resolve_packets;
      gtl_clear_packets = g_gtl_held_clear_packets;
      gtl_tess_packets = g_gtl_held_tess_packets;
      static uint32_t logged_guest_frame_hold = 0;
      if (logged_guest_frame_hold < 20u ||
          (logged_guest_frame_hold % 256u) == 0u) {
        REXLOG_INFO(
            "Refii GTL guest-frame HOLD #{} frame={} boundary={} "
            "re-present[draws={} resolves={} clears={}]",
            ++logged_guest_frame_hold, frame_index,
            static_cast<unsigned long long>(gtl_guest_frame_boundary),
            gtl_packets.size(), gtl_resolve_packets.size(),
            gtl_clear_packets.size());
      } else {
        ++logged_guest_frame_hold;
      }
    }
  }
  if ((frame_index % 240u) == 0u) {
    REXLOG_INFO("Refii GTL clear census frame={} clears_this_drain={}",
                frame_index, gtl_clear_packets.size());
  }

  // Migration step 3b: the packets are not registered as private-copy ACTIONS.
  // Registering them cannot work: SelectDirectPrivateTextureCopyActionLocked
  // scores candidates on same_draw (the OBSERVER's command draw serial),
  // shader_root_match and an exact wrapper call/context key — identities minted
  // by the observer's own capture events (++g_observer.serial), which a GTL
  // packet cannot reproduce by construction. Packet actions therefore tie on
  // every real discriminator and fall through to "newest serial wins", so each
  // page's blit sampled an arbitrary backing (runs 421/422/427: a checkerboard
  // of real page content).
  //
  // The bind-seam override that used to read these packets is DELETED
  // (2026-07-17): it asked "what does the blit sample", and sub_82206888 is
  // D3DDevice_Resolve — it samples nothing. The packets are stashed below for
  // the capture census only.

  // Cache the guest's own DisplaySwap frontbuffer here, before any renderer
  // lock is taken (the observer snapshot takes the observer mutex; acquiring
  // it under the renderer mutex is the documented AB-BA class).
  {
    // MIGRATION STEP 4 COMPLETE (2026-08-04). The observer is deleted; the GTL
    // is the only source. It states the front buffer from D3DDevice_Swap r4 at
    // the present root, and decodes the GPU base from that stated header
    // (header+0x20 & 0xFFFFF000) at the same instant. The shadow comparison
    // that used to live here existed solely to prove the GTL agreed with the
    // observer before this swap; with the observer gone there is nothing left
    // to compare against, and the observer read returned 0 anyway.
    const refii::gtl::StatedPresentSource present_source =
        refii::gtl::GetStatedPresentSource();
    g_direct_renderer.guest_swap_frontbuffer_object = present_source.front_buffer;
    g_direct_renderer.guest_swap_frontbuffer_base = present_source.front_buffer_base;
    g_direct_renderer.gtl_swap_frontbuffer_object = present_source.front_buffer;
    g_direct_renderer.observer_draws_at_frame_top = 0;
  }

  // Queued by the upload gate while sync/harvest locks were held; run here
  // with no renderer locks held, so a blocking observer acquire is safe and
  // the consuming draw's fetch layout can override a stale record fetch.
  for (const PartialUploadRecaptureRequest& recapture_request :
       DrainPartialUploadRecaptures()) {
    const bool recaptured = RefreshRendererTextureFromFetchBase(
        recapture_request.guest_address, "partial-drain",
        /*allow_blocking=*/true, &recapture_request.fetch_dwords);
    static uint32_t logged_partial_recapture_drains = 0;
    if (logged_partial_recapture_drains < 24u) {
      REXLOG_INFO(
          "Refii plume partial-upload recapture drain #{} guest=0x{:08X} "
          "recaptured={}",
          ++logged_partial_recapture_drains, recapture_request.guest_address,
          recaptured ? 1 : 0);
    }
  }

  std::lock_guard<std::mutex> lock(g_direct_renderer.mutex);
  if (device != nullptr) {
    g_direct_renderer.device = device;
  }
  g_direct_renderer.shader_format = shader_format;
  {
    // GTL shadow validation: report last frame's packet-vs-direct match rate,
    // then arm this frame's batch.
    if (!g_direct_renderer.gtl_resolve_shadow_packets.empty()) {
      // §3b resolve census (rewritten 2026-07-17). The previous
      // "placement-coherence" gate is DELETED: it summed dest_rect areas per
      // batch_seq batch and called overlap the scramble signature. Every input
      // to that test was wrong. The rects were pSourceRects (r5), not dest
      // rects; they come from D3DDevice_EndTiling, which partitions the render
      // target into predicated tiles, so they tile their union exactly BY
      // CONSTRUCTION and the test reported "coherent" regardless of any defect.
      // Its batch boundary was keyed on batch_seq resetting at
      // XGCompressSurface, which is not a resolve and never fired, so every
      // frame was one batch.
      //
      // What actually gates the ownership flip is whether the capture is
      // complete and self-consistent:
      //   unbound_source   - flags&7 named an RT/DS slot the tracked device
      //                      state had nothing in => device-state tracking gap.
      //   unregistered_dest- r6 destination header no Create root registered.
      //   surface_mismatch - source_surface disagrees with the tracked binding
      //                      for that slot => the two disagree about the frame.
      uint32_t unbound_source = 0;
      uint32_t unregistered_dest = 0;
      uint32_t surface_mismatch = 0;
      uint32_t depth_resolves = 0;
      uint32_t clearing_resolves = 0;
      uint32_t default_source_rect = 0;
      uint32_t default_dest_point = 0;
      uint32_t invalid_source_rect = 0;
      uint32_t complete = 0;
      // HOST-COPY FEASIBILITY (the real §3b gate). Executing a resolve needs a
      // host resource on BOTH sides. The source is exact-identity: the guest RT
      // object the device had bound. The destination has no usable object
      // identity — its r6 header is reused scratch (run 437) — so it must be
      // keyed on the fetch constant, i.e. the console memory being written.
      // Count what a real host copy would fail to find; both must reach ~0
      // before the copy is worth building.
      uint32_t no_host_source = 0;
      uint32_t no_host_dest = 0;
      uint32_t no_dest_fetch = 0;
      // no_host_dest split by cause (see the comment at the call below).
      uint32_t dest_fetch_dwords_rejected = 0;
      uint32_t dest_no_identity_objects = 0;
      uint32_t dest_filtered_out = 0;
      // The destination-oriented lookup (no content gate). This is the number
      // that decides whether the resolve host-copy is buildable.
      uint32_t no_resolve_dest = 0;
      uint32_t resolve_dest_ambiguous = 0;
      // Which gate refused, for the destinations this lookup still misses.
      uint32_t rd_candidates = 0, rd_no_identity_record = 0, rd_no_resource = 0;
      uint32_t rd_not_image = 0, rd_not_ready = 0, rd_fetch_mismatch = 0;
      // REGISTRY ROUTE (measurement only, no consumer yet). The fetch route
      // above asks: fetch constant -> texture identity -> resource. The
      // migration doc's replacement map says a packet should instead name its
      // resource DIRECTLY: packet -> registry -> host resource. r6 IS a
      // registered object now (resolve_unregistered_dest == 0 since the
      // eviction fix, run 448: dest_reg == dest_tex on every probe), and this
      // is the same lookup the SOURCE side already passes 100% of the time
      // (no_host_source == 0), so the source and destination would finally be
      // resolved by one rule instead of two.
      // Measure it against the fetch route before anything consumes it: if
      // obj_no_resource reaches ~0 where the fetch route misses 10/19, the
      // route is the answer and the identity/fetch maze is not needed for
      // destinations. If it misses the SAME ones, the resource genuinely does
      // not exist yet and this is a materialization-ordering problem instead —
      // a completely different fix. Do not skip this step: "plausible" is what
      // the fetch_mismatch prime-suspect was.
      uint32_t obj_no_resource = 0, obj_not_image = 0, obj_not_ready = 0;
      uint32_t obj_ok = 0;
      // BASE-ADDRESS route (§3b identity, measurement only). The two routes
      // already measured are both wrong in opposite directions because they ask
      // the question in the wrong NAMESPACE: they compare r6 — an object handle
      // — against a set that mixes object addresses (guest/parent) with console
      // MEMORY addresses (data/gpu/texture-base). Those are different spaces
      // that can collide numerically, which is how a source and destination
      // collapsed onto one host image.
      //
      // The design's key is the memory the header NAMES (Resource::base_address,
      // stated by XGOffsetBaseTextureAddress), matched against the host
      // resource's OWN base address — memory-to-memory, one namespace, no
      // "try every field".
      //
      // Count 0/1/many rather than taking the first hit: if a base is claimed by
      // several host resources the key is ambiguous and refusing is correct
      // (design: refuse rather than guess). `no_base` means the registry has no
      // re-point on record for r6 yet — a different gap (Create-time coverage),
      // not an ambiguity.
      uint32_t base_no_base = 0, base_none = 0, base_unique = 0, base_many = 0;
      // Refinements after run 459 (unique=19 many=5 none=1 no_fetch=30 of 55):
      // - `many` resolves with the wrapper's own tiebreak: prefer the host
      //   resource whose guest object IS r6 (the object the call named);
      //   refuse when even that is ambiguous.
      // - `no_fetch` conflated "fetch unreadable" with "base bits genuinely
      //   zero". dword1 like 0x00000097 is format-only — base 0 — and phys 0
      //   is a legal 360 GPU write target, so zero-base is a real placement,
      //   not missing data. Count the two separately.
      uint32_t base_many_tiebreak = 0, base_zero = 0;
      // §3b host-copy buildability, DRY RUN (records nothing). Answers the
      // question the ownership flip rests on: of the resolves whose source and
      // destination both resolve, how many can a host copy actually express?
      // format_mismatch is the one to watch — a plain copy cannot convert
      // formats, and depth resolves are expected to refuse there.
      GtlResolveHostCopyStats copy_stats;
      for (const refii::gtl::ResolvePacket& packet :
           g_direct_renderer.gtl_resolve_shadow_packets) {
        const uint32_t fm_before = copy_stats.format_mismatch;
        const uint32_t ext_before = copy_stats.extent_mismatch;
        if (!IsGtlResolveHostCopyBuildableLocked(packet, &copy_stats) &&
            (copy_stats.format_mismatch != fm_before ||
             copy_stats.extent_mismatch != ext_before)) {
          // The copy refused on format or extent fit — the draw path is the
          // owner for this packet; dry-run it so the census predicts the full
          // family.
          RecordGtlResolveDrawLocked(nullptr, packet, &copy_stats,
                                     /*dry_run=*/true);
        }
      }
      for (const refii::gtl::ResolvePacket& packet :
           g_direct_renderer.gtl_resolve_shadow_packets) {
        const bool source_ok = packet.source_surface != 0;
        const bool dest_ok = packet.dest_texture != 0;
        const bool dest_registered = packet.dest_registry_object != 0;
        if (source_ok &&
            FindDirectResourceLocked(packet.source_surface) == nullptr) {
          ++no_host_source;
        }
        if (dest_ok) {
          PlumeTrackedResource* obj_dest =
              FindDirectResourceLocked(packet.dest_texture);
          if (obj_dest == nullptr) {
            ++obj_no_resource;
          } else if (!IsImageResource(obj_dest->kind)) {
            ++obj_not_image;
          } else if (!PlumeGuestImageReady(*obj_dest)) {
            ++obj_not_ready;
          } else {
            ++obj_ok;
          }
          // Memory-to-memory: does exactly one host image own the console
          // memory this header names? The base comes from the packet's OWN
          // fetch dwords — dword[1] (r6+32) is the base-address field, and
          // IDA (2026-07-17, D3DDevice_Resolve 0x82206888) proves it is the
          // very dword the resolve masks into RB_COPY_DEST_BASE. Decoding with
          // FableDecodeTextureFetchAddressDword matches the resolve's
          // wrap-carry + 29-bit arithmetic instruction for instruction, and
          // PlumeGuestTextureBaseAddress is populated from the same decode on
          // the host side — one representation on both ends. The registry's
          // base_address is NOT used here: it lags this (0 until the object
          // passes the offset root), while the fetch dwords are captured at
          // the resolve itself.
          const uint32_t dest_base_from_fetch =
              packet.dest_fetch_valid
                  ? FableDecodeTextureFetchAddressDword(
                        packet.dest_fetch_dwords[1])
                  : 0;
          if (!packet.dest_fetch_valid) {
            ++base_no_base;
          } else if (dest_base_from_fetch == 0) {
            ++base_zero;
          } else {
            uint32_t base_hits = 0;
            bool r6_claims = false;
            for (auto& [address, resource] : g_direct_renderer.resources) {
              (void)address;
              if (!IsImageResource(resource.kind)) {
                continue;
              }
              if (PlumeGuestTextureBaseAddress(resource) ==
                  dest_base_from_fetch) {
                ++base_hits;
                if (resource.guest_address == packet.dest_texture) {
                  r6_claims = true;
                }
              }
            }
            if (base_hits == 0) {
              ++base_none;
            } else if (base_hits == 1) {
              ++base_unique;
            } else if (r6_claims) {
              // Several host images claim this memory, but one of them IS the
              // object the wrapper named — the call itself disambiguates.
              ++base_many_tiebreak;
            } else {
              ++base_many;
            }
          }
        }
        if (!packet.dest_fetch_valid) {
          ++no_dest_fetch;
        } else {
          // Split the miss by CAUSE. `no_host_dest` alone conflates at least
          // three very different worlds and cannot drive a decision:
          //   (a) the fetch dwords are rejected outright,
          //   (b) no identity object is registered for that console memory at
          //       all, or
          //   (c) objects exist but every one is filtered out by the
          //       ready/has-content/dimension gates — which is the predicted
          //       case, since a resolve destination that has never been written
          //       is exactly what fails a "has content" test.
          // (a) vs (b) vs (c) point at completely different fixes, so measure.
          const std::array<uint32_t, 6> norm =
              FableNormalizeTextureFetchDwords(packet.dest_fetch_dwords);
          // Keep measuring the SAMPLING lookup (no_host_dest) purely as the
          // before/after control — it is the wrong question for a destination
          // and is expected to keep missing ~94%.
          if (FindDirectReadyRegisteredTextureByFetchDwordsLocked(
                  norm, packet.dest_texture) == nullptr) {
            ++no_host_dest;
            if (!HasTrackedFetchDwords(norm)) {
              ++dest_fetch_dwords_rejected;
            } else if (FableDirectFindTextureIdentityObjectsByFetch(norm) ==
                       nullptr) {
              ++dest_no_identity_objects;
            } else {
              ++dest_filtered_out;
            }
          }
          // The DESTINATION lookup — the one a real host copy would use. If the
          // "wrong question" diagnosis is right, this recovers the ~80% the
          // content gate was discarding and only the genuine no_identity cases
          // remain. If it does NOT, the diagnosis is wrong and the copy must
          // not be built on it.
          bool dest_ambiguous = false;
          ResolveDestLookupStats dest_stats;
          if (FindDirectResolveDestinationByFetchDwordsLocked(
                  norm, packet.dest_texture, &dest_ambiguous, &dest_stats) ==
              nullptr) {
            ++no_resolve_dest;
            if (dest_ambiguous) {
              ++resolve_dest_ambiguous;
            }
            rd_candidates += dest_stats.candidates;
            rd_no_identity_record += dest_stats.no_identity_record;
            rd_no_resource += dest_stats.no_resource;
            rd_not_image += dest_stats.not_image;
            rd_not_ready += dest_stats.not_ready;
            rd_fetch_mismatch += dest_stats.fetch_mismatch;
          }
        }
        // A NULL rect/point is the ABI default, not a capture failure.
        const bool source_rect_ok =
            packet.source_rect_valid || packet.source_rect_default;
        unbound_source += source_ok ? 0u : 1u;
        unregistered_dest += (dest_ok && !dest_registered) ? 1u : 0u;
        depth_resolves += packet.source_is_depth ? 1u : 0u;
        clearing_resolves += (packet.flags & 0x300u) != 0 ? 1u : 0u;
        default_source_rect += packet.source_rect_default ? 1u : 0u;
        default_dest_point += packet.dest_point_default ? 1u : 0u;
        invalid_source_rect += source_rect_ok ? 0u : 1u;
        // Cross-check the packet's source against the binding it was captured
        // with: they are read from the same tracked state, so a disagreement
        // means the state moved under us.
        const uint32_t expected =
            packet.source_is_depth
                ? packet.dest_depth
                : (packet.source_select < packet.dest_targets.size()
                       ? packet.dest_targets[packet.source_select]
                       : 0u);
        surface_mismatch += packet.source_surface == expected ? 0u : 1u;
        complete += source_ok && dest_ok && source_rect_ok ? 1u : 0u;
      }
      static uint64_t gtl_resolve_census_reports = 0;
      ++gtl_resolve_census_reports;
      // Print every report that carries a MISS on either route, not just a
      // head/periodic sample: the two routes are being compared per-packet, and
      // a sample biased to the first 16 reports (all inside the ~frame 1919-1941
      // warm-up) is what made the fetch route look like it was merely warming up
      // when it was in fact alternating hit/miss between two distinct
      // destinations. Resolves run ~1/frame, so this stays cheap.
      if (gtl_resolve_census_reports <= 16u ||
          (gtl_resolve_census_reports % 256u) == 0u ||
          complete != g_direct_renderer.gtl_resolve_shadow_packets.size() ||
          surface_mismatch != 0 || no_resolve_dest != 0 ||
          obj_no_resource != 0 || obj_not_image != 0 || obj_not_ready != 0) {
        REXLOG_INFO(
            "Refii GTL resolve census #{} frame={} packets={} complete={} "
            "unbound_source={} unregistered_dest={} surface_mismatch={} "
            "depth={} clearing={} default_src_rect={} default_dest_pt={} "
            "invalid_src_rect={} | host_copy: no_host_source={} "
            "no_dest_fetch={} no_host_dest={} (dwords_rejected={} "
            "no_identity={} filtered_out={}) | RESOLVE_DEST: no_dest={} "
            "ambiguous={} why[cand={} no_id_rec={} no_res={} not_img={} "
            "not_ready={} fetch_mismatch={}] | OBJ_ROUTE: ok={} no_res={} "
            "not_img={} not_ready={} | BASE_ROUTE: unique={} none={} many={} "
            "no_fetch={} tiebreak={} zero_base={} | HOST_COPY(dry): buildable={} no_src={} "
            "no_dst={} not_img={} not_ready={} same_tex={} fmt_mismatch={} "
            "empty_rect={} | DRAW(dry): ok={} depth={} no_fb={} no_pipe={}",
            static_cast<unsigned long long>(gtl_resolve_census_reports),
            frame_index, g_direct_renderer.gtl_resolve_shadow_packets.size(),
            complete, unbound_source, unregistered_dest, surface_mismatch,
            depth_resolves, clearing_resolves, default_source_rect,
            default_dest_point, invalid_source_rect, no_host_source,
            no_dest_fetch, no_host_dest, dest_fetch_dwords_rejected,
            dest_no_identity_objects, dest_filtered_out, no_resolve_dest,
            resolve_dest_ambiguous, rd_candidates, rd_no_identity_record,
            rd_no_resource, rd_not_image, rd_not_ready, rd_fetch_mismatch,
            obj_ok, obj_no_resource, obj_not_image, obj_not_ready, base_unique,
            base_none, base_many, base_no_base, base_many_tiebreak, base_zero,
            copy_stats.recorded, copy_stats.no_source_resource,
            copy_stats.no_dest_resource, copy_stats.not_image,
            copy_stats.not_ready, copy_stats.same_texture,
            copy_stats.format_mismatch, copy_stats.empty_rect,
            copy_stats.draw_recorded, copy_stats.draw_depth_deferred,
            copy_stats.draw_no_framebuffer, copy_stats.draw_no_pipeline);
      }
    }
    const size_t previous_packets = g_direct_renderer.gtl_shadow_packets.size();
    if (previous_packets != 0 || g_direct_renderer.gtl_shadow_direct_draws != 0) {
      const size_t matched = static_cast<size_t>(
          std::count(g_direct_renderer.gtl_shadow_matched.begin(),
                     g_direct_renderer.gtl_shadow_matched.end(), true));
      static uint64_t gtl_shadow_reports = 0;
      ++gtl_shadow_reports;
      if (gtl_shadow_reports <= 8u || (gtl_shadow_reports % 256u) == 0u ||
          (previous_packets != 0 && matched * 2 < previous_packets &&
           (gtl_shadow_reports % 32u) == 0u)) {
        // Family breakdown of the unmatched packets: which draw roots the
        // direct pipeline never executed this frame (missing-compose hunt).
        std::string unmatched_families;
        for (size_t shadow_index = 0;
             shadow_index < g_direct_renderer.gtl_shadow_packets.size() &&
             shadow_index < g_direct_renderer.gtl_shadow_matched.size();
             ++shadow_index) {
          if (g_direct_renderer.gtl_shadow_matched[shadow_index]) {
            continue;
          }
          if (!unmatched_families.empty()) {
            unmatched_families += ',';
          }
          char family[16];
          std::snprintf(
              family, sizeof(family), "%08X",
              g_direct_renderer.gtl_shadow_packets[shadow_index].source_symbol);
          unmatched_families += family;
          if (unmatched_families.size() > 120u) {
            unmatched_families += ",...";
            break;
          }
        }
        REXLOG_INFO(
            "Refii GTL shadow #{} frame={} packets={} matched={} "
            "direct_draws={} gtl_missing={} unmatched=[{}]",
            gtl_shadow_reports, frame_index, previous_packets, matched,
            g_direct_renderer.gtl_shadow_direct_draws,
            g_direct_renderer.gtl_shadow_direct_unmatched, unmatched_families);
      }
    }
    // §3.4 executor completeness is owned by the submission merge above
    // (matched GTL sourcing + video packet insertion). The old
    // packet-synthesis channel (force-capturing unmatched packets through
    // the observer event pipeline, cvar renderer_gtl_packet_synthesis,
    // default OFF, zero-fire) was deleted 2026-07-18 as superseded.
    // Order flip gate verdict for the finished frame: adjacent descents in
    // the matched packet_id sequence = draws the direct pipeline recorded
    // out of guest dispatch order. Zero across runs proves execution order
    // already equals packet order (the §3.4 executor's ordering axis is a
    // no-op cutover); nonzero counts localize the reordering work.
    if (!g_direct_renderer.gtl_order_sequence.empty()) {
      uint32_t frame_inversions = 0;
      uint64_t first_inverted_prev = 0;
      uint64_t first_inverted_next = 0;
      for (size_t order_index = 1;
           order_index < g_direct_renderer.gtl_order_sequence.size();
           ++order_index) {
        if (g_direct_renderer.gtl_order_sequence[order_index] <
            g_direct_renderer.gtl_order_sequence[order_index - 1u]) {
          ++frame_inversions;
          if (first_inverted_prev == 0) {
            first_inverted_prev =
                g_direct_renderer.gtl_order_sequence[order_index - 1u];
            first_inverted_next =
                g_direct_renderer.gtl_order_sequence[order_index];
          }
        }
      }
      ++g_direct_renderer.gtl_order_frames;
      g_direct_renderer.gtl_order_draws +=
          g_direct_renderer.gtl_order_sequence.size();
      g_direct_renderer.gtl_order_inversions += frame_inversions;
      g_direct_renderer.gtl_order_disordered_frames +=
          frame_inversions != 0 ? 1u : 0u;
      if (frame_inversions != 0) {
        static uint32_t order_inversion_captures = 0;
        RequestGtlDefectCaptureLocked("order_inversion",
                                      order_inversion_captures, 2u);
        // Identity of the inverted pair (run 572: constant delta of 13 —
        // one draw family systematically mis-slotted; name it).
        const auto log_packet_identity = [&](const char* role, uint64_t id) {
          for (const refii::gtl::DrawPacket& packet :
               g_direct_renderer.gtl_shadow_packets) {
            if (packet.packet_id != id) {
              continue;
            }
            REXLOG_INFO(
                "Refii GTL order inversion {} pkt={} symbol=0x{:08X} "
                "vs=0x{:08X} ps=0x{:08X} indexed={} prims={} rt0=0x{:08X} "
                "tex0=0x{:08X} tex13=0x{:08X}",
                role, static_cast<unsigned long long>(id),
                packet.source_symbol, packet.vertex_shader,
                packet.pixel_shader, packet.indexed ? 1 : 0,
                packet.primitive_count, packet.render_targets[0],
                packet.textures[0],
                packet.textures.size() > 13u ? packet.textures[13] : 0u);
            return;
          }
          REXLOG_INFO("Refii GTL order inversion {} pkt={} NOT-IN-GENERATION",
                      role, static_cast<unsigned long long>(id));
        };
        log_packet_identity("prev", first_inverted_prev);
        log_packet_identity("next", first_inverted_next);
      }
      static uint64_t logged_order_reports = 0;
      ++logged_order_reports;
      if ((frame_inversions != 0 && logged_order_reports <= 4096u) ||
          logged_order_reports <= 4u || (logged_order_reports % 1024u) == 0u) {
        REXLOG_INFO(
            "Refii GTL order shadow frame={} draws={} inversions={} "
            "first={}>{} cum_frames={} cum_draws={} cum_inversions={} "
            "cum_disordered_frames={}",
            frame_index, g_direct_renderer.gtl_order_sequence.size(),
            frame_inversions,
            static_cast<unsigned long long>(first_inverted_prev),
            static_cast<unsigned long long>(first_inverted_next),
            static_cast<unsigned long long>(g_direct_renderer.gtl_order_frames),
            static_cast<unsigned long long>(g_direct_renderer.gtl_order_draws),
            static_cast<unsigned long long>(
                g_direct_renderer.gtl_order_inversions),
            static_cast<unsigned long long>(
                g_direct_renderer.gtl_order_disordered_frames));
      }
      g_direct_renderer.gtl_order_sequence.clear();
    }
    g_direct_renderer.gtl_shadow_packets_prev =
        std::move(g_direct_renderer.gtl_shadow_packets);
    g_direct_renderer.gtl_shadow_matched_prev =
        std::move(g_direct_renderer.gtl_shadow_matched);
    g_direct_renderer.gtl_shadow_packets = std::move(gtl_packets);
    g_direct_renderer.gtl_shadow_matched.assign(
        g_direct_renderer.gtl_shadow_packets.size(), false);
    g_direct_renderer.gtl_authority_consumed_prev =
        std::move(g_direct_renderer.gtl_authority_consumed);
    g_direct_renderer.gtl_authority_consumed.assign(
        g_direct_renderer.gtl_shadow_packets.size(), false);
    g_direct_renderer.gtl_shadow_direct_draws = 0;
    g_direct_renderer.gtl_shadow_direct_unmatched = 0;
    g_direct_renderer.gtl_resolve_shadow_packets_prev =
        std::move(g_direct_renderer.gtl_resolve_shadow_packets);
    g_direct_renderer.gtl_resolve_shadow_packets = std::move(gtl_resolve_packets);
    g_direct_renderer.gtl_clear_shadow_packets = std::move(gtl_clear_packets);
    g_direct_renderer.gtl_tess_shadow_packets = std::move(gtl_tess_packets);
    // The durable proxy->source-fetch index that used to be folded in here is
    // deleted with its consumer (see the note on gtl_resolve_shadow_packets):
    // it indexed r6, the resolve DESTINATION, as if it were a sampled source.
  }
  // §3.4 executor, shadow phase: resolve every packet's identity fields
  // through the SAME exact-identity machinery the executor will use, and
  // account misses by category. "executable" packets could have been drawn
  // from the packet alone (identity-wise); the per-category misses are the
  // executor's remaining input gaps (pipeline state and constants are known
  // gaps — the GTL does not capture them yet).
  if (RendererGtlExecutorShadowEnabled() &&
      !g_direct_renderer.gtl_shadow_packets.empty()) {
    uint32_t executable = 0;
    uint32_t miss_vs = 0;
    uint32_t miss_ps = 0;
    uint32_t miss_texture = 0;
    uint32_t miss_vertex_buffer = 0;
    uint32_t miss_index_buffer = 0;
    uint32_t miss_render_target = 0;
    // miss_vs/miss_ps conflate TWO different failures: the packet not carrying
    // a shader at all, versus carrying one the registry does not track as that
    // kind. They need opposite fixes — the first is a packet-build/state
    // problem, the second is a resource-registration problem — so count them
    // apart. (2026-07-22: three changes aimed at the first cause moved the
    // miss rate 0%, which is what prompted splitting this.)
    uint32_t absent_vs = 0;
    uint32_t absent_ps = 0;
    uint32_t untracked_vs = 0;
    uint32_t untracked_ps = 0;
    const auto tracked_of_kind = [&](uint32_t guest,
                                     FableGuestResourceKind kind) {
      const PlumeTrackedResource* resource = FindDirectResourceLocked(guest);
      return resource != nullptr && resource->kind == kind;
    };
    // sub_82206888 D3DDevice_Resolve emits a LEGACY DrawPacket alongside its
    // ResolvePacket (refii_gtl.cpp kResolve: "known-meaningless under the
    // corrected arg map ... a resolve has no primitive type and no vertex
    // data"). Grading those by draw criteria counts a shader that cannot
    // exist as a miss.
    //
    // Run 138 measured the damage: vs_absent = 391 EXACTLY equalled
    // resolve_packets = 391, while 2021 of 2021 real draws carried their
    // shader. The "shader gap" was entirely this artifact — several changes
    // were spent chasing it before the counters were separated. Skip them.
    constexpr uint32_t kResolveSymbol = 0x82206888u;
    uint32_t skipped_resolve_packets = 0;
    for (const refii::gtl::DrawPacket& packet :
         g_direct_renderer.gtl_shadow_packets) {
      if (packet.source_symbol == kResolveSymbol) {
        ++skipped_resolve_packets;
        continue;
      }
      bool ok = true;
      if (packet.vertex_shader == 0 ||
          !tracked_of_kind(packet.vertex_shader,
                           FableGuestResourceKind::kVertexShader)) {
        ++miss_vs;
        if (packet.vertex_shader == 0) {
          ++absent_vs;
        } else {
          ++untracked_vs;
        }
        ok = false;
      }
      if (packet.pixel_shader == 0 ||
          !tracked_of_kind(packet.pixel_shader,
                           FableGuestResourceKind::kPixelShader)) {
        ++miss_ps;
        if (packet.pixel_shader == 0) {
          ++absent_ps;
        } else {
          ++untracked_ps;
        }
        ok = false;
      }
      // Texture slots are advisory: packets snapshot all 26 device slots and
      // stale slots the draw's shader never samples are harmless (Xenos
      // ignores unused fetches; a genuinely sampled-but-unresolved slot is
      // the null-texture class the compose-hold already handles).
      for (uint32_t texture : packet.textures) {
        if (texture == 0) {
          continue;
        }
        const PlumeTrackedResource* resource =
            FindDirectResourceLocked(texture);
        if (resource == nullptr || !IsImageResource(resource->kind) ||
            !PlumeGuestImageReady(*resource)) {
          ++miss_texture;
          break;
        }
      }
      if (packet.vertex_buffers[0] != 0 &&
          !packet.draw_up &&
          FindDirectResourceLocked(packet.vertex_buffers[0]) == nullptr) {
        ++miss_vertex_buffer;
        ok = false;
      }
      if (packet.indexed && packet.index_buffer != 0 &&
          FindDirectResourceLocked(packet.index_buffer) == nullptr) {
        ++miss_index_buffer;
        ok = false;
      }
      if (packet.render_targets[0] != 0) {
        const PlumeTrackedResource* target =
            FindDirectResourceLocked(packet.render_targets[0]);
        if (target == nullptr || !IsImageResource(target->kind)) {
          ++miss_render_target;
          ok = false;
        }
      }
      executable += ok ? 1u : 0u;
    }
    static uint64_t gtl_executor_shadow_reports = 0;
    ++gtl_executor_shadow_reports;
    // Graded population only — resolve-sourced legacy packets are excluded
    // above, so counting them here would understate executability forever.
    const size_t packet_count =
        g_direct_renderer.gtl_shadow_packets.size() - skipped_resolve_packets;
    if (gtl_executor_shadow_reports <= 8u ||
        (gtl_executor_shadow_reports % 256u) == 0u ||
        (executable < packet_count &&
         (gtl_executor_shadow_reports % 32u) == 0u)) {
      REXLOG_INFO(
          "Refii GTL exec shadow #{} frame={} packets={} executable={} "
          "miss vs={} ps={} tex={} vb={} ib={} rt={} "
          "vs_absent={} vs_untracked={} ps_absent={} ps_untracked={} "
          "skipped_resolve={} rs_mask=0x{:08X} "
          "state_cmp={}/{} const_cmp={}/{} vp_cmp={}/{} sc_cmp={}/{}",
          gtl_executor_shadow_reports, frame_index, packet_count, executable,
          miss_vs, miss_ps, miss_texture, miss_vertex_buffer,
          miss_index_buffer, miss_render_target, absent_vs, untracked_vs,
          absent_ps, untracked_ps, skipped_resolve_packets,
          g_direct_renderer.gtl_shadow_packets.front()
              .render_state_valid_mask,
          static_cast<unsigned long long>(
              g_direct_renderer.gtl_state_diverged),
          static_cast<unsigned long long>(
              g_direct_renderer.gtl_state_compared),
          static_cast<unsigned long long>(
              g_direct_renderer.gtl_const_diverged),
          static_cast<unsigned long long>(
              g_direct_renderer.gtl_const_compared),
          static_cast<unsigned long long>(
              g_direct_renderer.gtl_viewport_diverged),
          static_cast<unsigned long long>(
              g_direct_renderer.gtl_viewport_compared),
          static_cast<unsigned long long>(
              g_direct_renderer.gtl_scissor_diverged),
          static_cast<unsigned long long>(
              g_direct_renderer.gtl_scissor_compared));
    }
  }
  ResetDirectRenderGraphExecutionSnapshotLocked(frame_index);
  g_direct_renderer.current_frame_has_pix_video_draw = false;
  g_direct_renderer.revalidated_cpu_textures.clear();
  g_direct_renderer.textureless_compose_targets.clear();
  ClearDirectFramePrivateRectCachesLocked();
  FableDirectFrameCommandBatch command_batch =
      CaptureFableDirectFrameCommandBatch();
  // (Compose-census supersession counts are captured AFTER the GTL merge and
  // the record backlog cap below — both extend the batch's counters.)
  if (RendererGtlSubmissionEnabled()) {
    // Step 5 (§3.4 executor cutover): the GTL packet stream DRIVES the VERTEX
    // draws. The GTL captures every vertex draw at the wrapper root (race-free,
    // unmatched=0), so these are not subject to the observer publish/replay
    // drop pipeline. MERGE (not replace): keep the observer batch's non-vertex-
    // draw commands (clears, resolves, private-rect blits, pix-video).
    //
    // CLEARS stay observer-sourced for now (capture census only — see below):
    // the observer executes MANY more clears than the two D3D clear roots the
    // GTL captures (private-rect SETUP clears, synthetic per-target clears —
    // run 378 recorded up to 265/frame vs the GTL's 2/frame). Those derived
    // clears belong to the §3b private-rect (ResolveBlitPacket) ownership, not
    // the clear-root capture, so dropping observer clears now regresses the
    // frame. The ClearPacket stream is captured + census-verified and drained
    // each frame; it drives submission only once the GTL also covers the
    // private-rect setup clears. Begin-family packets carry stale Begin-ENTER
    // state and are never submitted; the private-rect 0x82206888 packet is a
    // blit, handled by the observer path.
    // IN-PLACE replacement to preserve DRAW<->COMPOSITE ORDER: the Fable scene
    // is a storybook composite (private-rect blits + resolves compositing what
    // the vertex draws produce). Appending all GTL draws AFTER the observer non-
    // draws broke that order (composites ran before their source draws -> flat
    // scene). Instead, walk the observer batch IN ORDER; each plain vertex draw
    // is replaced IN PLACE by the identity-matched GTL packet's command (same
    // match the working executor-authority path uses); clears/resolves/private-
    // rect/video stay in their positions. Unmatched draws keep the observer
    // command (never drop a draw). This is the ordered step-5 cutover for the
    // vertex class; the remaining classes stay observer-sourced until their
    // packet streams drive submission too.
    std::vector<FableDirectRenderCommand> merged;
    // Reserve for the AUTHORITATIVE shape, not just the observer batch size:
    // under authoritative submission `merged` holds one command per emitted
    // GTL draw packet, up to two per resolve packet (resolve + its EDRAM
    // clear) and one per authoritative clear packet — with a multi-guest-
    // frame packet backlog that dwarfs the observer batch, and every vector
    // growth reallocation relocates ALL of the ~12.6KB elements already
    // built (a 2375-command menu backlog is ~30MB of payload to re-move per
    // doubling).
    merged.reserve(std::max(
        command_batch.submitted_commands.size(),
        g_direct_renderer.gtl_shadow_packets.size() +
            2u * g_direct_renderer.gtl_resolve_shadow_packets.size() +
            g_direct_renderer.gtl_clear_shadow_packets.size() +
            g_direct_renderer.gtl_tess_shadow_packets.size()));
    std::vector<bool> consumed(g_direct_renderer.gtl_shadow_packets.size(),
                               false);
    uint32_t gtl_draws = 0;
    uint32_t observer_kept = 0;
    uint32_t unmatched_draws = 0;
    // §3b resolve INTERLEAVE: observer resolves never ride this frame's command
    // stream (ordering census runs 466/469/470: observer_resolves=0 always —
    // kResolveCopy commands come from the deferred replay, out-of-band), so
    // there is nothing to swap in place. Instead INSERT the GTL resolve
    // execution at its packet_id position between the GTL vertex draws:
    // ResolvePacket.packet_id and DrawPacket.packet_id share one counter, so a
    // resolve with id R captured between draws A(id<R) and B(id>R) executes
    // after A and before B — after the draws that filled its source RT, before
    // draws sampling its destination. Resolves left after the last matched draw
    // flush at the end of the walk.
    const bool interleave_gtl_resolves = RendererGtlPrivateRectComposeEnabled();
    const bool gtl_resolve_clears = RendererGtlResolveClearsEnabled();
    // Authoritative clears ride the resolve interleave's order spine, so they
    // require it; both packet streams share the packet_id counter.
    const bool gtl_clears_authoritative =
        interleave_gtl_resolves && RendererGtlClearsAuthoritativeEnabled();
    size_t next_gtl_resolve = 0;
    size_t next_gtl_clear = 0;
    // T2: tessellated patch draws ride the same packet_id order spine as the
    // resolves/clears (all three streams share the GTL packet_id counter), so
    // they interleave between the vertex draws exactly as captured — after
    // the pass that fills the vertex textures they sample, before the resolve
    // that consumes the scene RT they render into.
    size_t next_gtl_tess = 0;
    uint32_t gtl_resolves_emitted = 0;
    uint32_t gtl_tess_emitted = 0;
    const auto emit_gtl_packets_up_to = [&](uint64_t packet_id_limit) {
      const auto& resolve_packets = g_direct_renderer.gtl_resolve_shadow_packets;
      const auto& clear_packets = g_direct_renderer.gtl_clear_shadow_packets;
      const auto& tess_packets = g_direct_renderer.gtl_tess_shadow_packets;
      for (;;) {
        const bool have_resolve =
            interleave_gtl_resolves &&
            next_gtl_resolve < resolve_packets.size() &&
            resolve_packets[next_gtl_resolve].packet_id < packet_id_limit;
        const bool have_clear =
            gtl_clears_authoritative &&
            next_gtl_clear < clear_packets.size() &&
            clear_packets[next_gtl_clear].packet_id < packet_id_limit;
        const bool have_tess =
            interleave_gtl_resolves &&
            next_gtl_tess < tess_packets.size() &&
            tess_packets[next_gtl_tess].packet_id < packet_id_limit;
        if (!have_resolve && !have_clear && !have_tess) {
          break;
        }
        const uint64_t resolve_id =
            have_resolve ? resolve_packets[next_gtl_resolve].packet_id
                         : std::numeric_limits<uint64_t>::max();
        const uint64_t clear_id =
            have_clear ? clear_packets[next_gtl_clear].packet_id
                       : std::numeric_limits<uint64_t>::max();
        const uint64_t tess_id =
            have_tess ? tess_packets[next_gtl_tess].packet_id
                      : std::numeric_limits<uint64_t>::max();
        if (have_tess && tess_id < resolve_id && tess_id < clear_id) {
          merged.push_back(BuildDirectCommandFromGtlTessPacketLocked(
              tess_packets[next_gtl_tess]));
          ++next_gtl_tess;
          ++gtl_tess_emitted;
          continue;
        }
        if (have_clear && (!have_resolve || clear_id < resolve_id)) {
          // PREDICATED CLEAR SKIP (2026-07-25, IDA-traced): a nonzero
          // predication_mask means this ClearF call was one of the
          // Render_BeginPredicatedTileClears_INFERRED pair, which real
          // hardware only applies to specific EDRAM tiles based on a GPU
          // predicate register Refii does not track. Submitting it as an
          // unconditional full-target Vulkan clear reproducibly wiped the
          // storybook menu scene to solid white (run 257-259). Refii's
          // renderer targets full-size Vulkan surfaces directly and never
          // splits a frame into EDRAM-size tile passes, so the EDRAM-reuse
          // optimization this predication exists for has no work to do here
          // — skipping is the architecturally correct behavior, not an
          // approximation. See refii-predicated-clear-white-screen.md.
          if (clear_packets[next_gtl_clear].predication_mask != 0u) {
            ++g_gtl_clear_merge_stats.predicated_skipped;
            ++next_gtl_clear;
            continue;
          }
          merged.push_back(BuildDirectCommandFromGtlClearPacketLocked(
              clear_packets[next_gtl_clear]));
          ++g_gtl_clear_merge_stats.authoritative_emitted;
          ++next_gtl_clear;
          continue;
        }
        const refii::gtl::ResolvePacket& packet =
            resolve_packets[next_gtl_resolve];
        FableDirectRenderCommand resolve_command;
        resolve_command.type = FableDirectRenderCommandType::kGtlResolve;
        resolve_command.gtl_resolve = packet;
        resolve_command.gtl_resolve_valid = true;
        merged.push_back(std::move(resolve_command));
        // Flags 0x100/0x200: the resolve also clears its EDRAM source; emit
        // that clear right after so the next draws see primed EDRAM exactly
        // as on hardware (EndTiling's per-tile 0x300 pattern).
        if (gtl_resolve_clears && (packet.flags & 0x300u) != 0) {
          merged.push_back(BuildDirectCommandFromGtlResolveClearLocked(packet));
          ++g_gtl_clear_merge_stats.resolve_clears;
        }
        ++next_gtl_resolve;
        ++gtl_resolves_emitted;
      }
    };
    // OBSERVER SUBMISSION CUTOVER. Replay the GTL packet streams by packet_id
    // and DO NOT walk the observer command batch at all. Draws (Begin /
    // DrawVertices / DrawIndexedUP incl. video 0x8220A3F0), resolves (=
    // private-rect) and clears share one packet_id counter, so
    // emit_gtl_packets_up_to interleaves the resolves/clears between the draws
    // exactly as captured. Only EXECUTABLE draw packets are emitted: skip the
    // EDRAM-resolve legacy draws (they ride the ResolvePacket stream) and any
    // packet with no shader or no vertex source (incomplete capture -> would
    // draw degenerate). This reproduces the frame with ZERO observer input and
    // is what lets the observer be deleted. Falls back to the in-place merge
    // when the cvar is off (A/B).
    const bool gtl_authoritative =
        RendererGtlAuthoritativeSubmissionEnabled() &&
        interleave_gtl_resolves && gtl_clears_authoritative;
    if (gtl_authoritative) {
      uint32_t rej_resolve = 0, rej_noshader = 0, rej_nogeom = 0;
      uint64_t min_pid = UINT64_MAX, max_pid = 0;
      // GTL-STREAM BACKLOG SUPERSESSION (run 485). The direct-command filter
      // in CaptureFableDirectFrameCommandBatch can never bound the record
      // volume here: under authoritative submission the batch it filtered is
      // REPLACED by this packet replay (command_batch.submitted_commands =
      // std::move(merged) below), and GTL-built commands never pass
      // PushRenderCommandLocked, so their publish_generation is 0 —
      // permanently exempt from that filter. Run 485 measured the
      // consequence: frames_superseded=0 while one compose recorded 1595
      // draws across a multi-guest-frame packet backlog and held the record
      // lock 15.5s. The volume carrier is THIS stream, so supersede HERE.
      // DrawPackets carry the guest-frame ordinal stamped AT HOOK TIME under
      // the GTL's own mutex (stated_present_epoch + 1, refii_gtl.cpp
      // BuildDrawPacketLocked — epoch and the drain boundary advance in the
      // same critical section at the guest Present, so drained sets
      // partition exactly by ordinal). Non-video DRAW packets of complete
      // guest frames older than the newest complete frame are dropped before
      // conversion; ResolvePackets/ClearPackets ride separate streams, are
      // NEVER dropped, and emit_gtl_packets_up_to still flushes them in
      // packet_id order (a superseded resolve may fill a surface the newest
      // frame samples — same contract as the direct-command filter).
      // Exemptions use the video module's two-factor classification (run-475
      // lesson: never a single enum): the Bink UP-wrapper builder root
      // (kFableBinkIndexedVerticesWrapperBuilderSymbol — the family whose
      // compose quads FableVideoCommandHasBinkWrapperSource matches at
      // command level) and any known video compose pixel shader
      // (FableVideoCommandUsesKnownVideoPixelShader). The whole pass is off
      // during the startup-video pump (FMV fence entanglement,
      // refii-bink-plane-setup-race), and ordinal 0 (stamped before the
      // first Present / stalled boundary) is exempt — a stalled seal can
      // never drop live content (the count-keyed record backlog cap below
      // covers that regime instead). In-progress packets never reach this
      // set in authoritative mode (DrainDrawPacketsUpTo takes only
      // packet_id < the Present boundary); the packet_id guard keeps that
      // true for carried/held sets too, so the still-assembling frame can
      // neither claim newest nor be dropped (run-483 lesson).
      const auto gtl_video_exempt_packet =
          [](const refii::gtl::DrawPacket& packet) {
            return packet.source_symbol ==
                       kFableBinkIndexedVerticesWrapperBuilderSymbol ||
                   FableVideoCommandUsesKnownVideoPixelShader(
                       DirectPixelShaderHashLocked(packet.pixel_shader));
          };
      const bool gtl_backlog_supersession =
          RendererBacklogSupersessionEnabled() &&
          !FableStartupVideoPumpActive();
      uint32_t newest_complete_ordinal = 0;
      if (gtl_backlog_supersession) {
        for (const refii::gtl::DrawPacket& packet :
             g_direct_renderer.gtl_shadow_packets) {
          // Only frames that will actually EMIT draws may claim newest (same
          // reject filters as the replay loop): a frame of all-rejected
          // packets superseding a drawable one would blank the compose.
          if (packet.guest_frame_ordinal == 0 ||
              packet.source_symbol == 0x82206888u ||
              packet.vertex_shader == 0 ||
              (packet.inline_vertex_data.empty() &&
               packet.vertex_buffers[0] == 0) ||
              gtl_video_exempt_packet(packet)) {
            continue;
          }
          if (gtl_guest_frame_boundary != 0 &&
              packet.packet_id >= gtl_guest_frame_boundary) {
            continue;  // in-progress frame must not claim newest.
          }
          newest_complete_ordinal =
              std::max(newest_complete_ordinal, packet.guest_frame_ordinal);
        }
      }
      uint32_t backlog_draws_dropped = 0;
      std::vector<uint32_t> backlog_superseded_ordinals;
      for (size_t i = 0; i < g_direct_renderer.gtl_shadow_packets.size(); ++i) {
        const refii::gtl::DrawPacket& packet =
            g_direct_renderer.gtl_shadow_packets[i];
        min_pid = std::min(min_pid, packet.packet_id);
        max_pid = std::max(max_pid, packet.packet_id);
        if (packet.source_symbol == 0x82206888u) {
          ++rej_resolve;
          continue;  // EDRAM resolve — rides the ResolvePacket stream.
        }
        if (packet.vertex_shader == 0) {
          ++rej_noshader;
          continue;  // incomplete capture: no shader.
        }
        if (packet.inline_vertex_data.empty() &&
            packet.vertex_buffers[0] == 0) {
          ++rej_nogeom;
          continue;  // incomplete capture: no vertex source.
        }
        if (gtl_backlog_supersession && newest_complete_ordinal != 0 &&
            packet.guest_frame_ordinal != 0 &&
            packet.guest_frame_ordinal < newest_complete_ordinal &&
            (gtl_guest_frame_boundary == 0 ||
             packet.packet_id < gtl_guest_frame_boundary) &&
            !gtl_video_exempt_packet(packet)) {
          // Superseded complete-frame draw: drop before conversion. Its
          // frame's clears/resolves still flush via emit_gtl_packets_up_to
          // at the next kept draw (or the final flush), in packet_id order.
          ++backlog_draws_dropped;
          if (std::find(backlog_superseded_ordinals.begin(),
                        backlog_superseded_ordinals.end(),
                        packet.guest_frame_ordinal) ==
              backlog_superseded_ordinals.end()) {
            backlog_superseded_ordinals.push_back(packet.guest_frame_ordinal);
          }
          consumed[i] = true;  // handled: keep the video-insert pass off it.
          continue;
        }
        emit_gtl_packets_up_to(packet.packet_id);
        merged.push_back(BuildDirectCommandFromGtlDrawPacketLocked(packet));
        consumed[i] = true;
        ++gtl_draws;
      }
      if (backlog_draws_dropped != 0) {
        // Fold into the batch's supersession census — the compose lock
        // census line (frames_superseded=/draws_dropped=) reads these.
        command_batch.superseded_frames +=
            static_cast<uint32_t>(backlog_superseded_ordinals.size());
        command_batch.superseded_draws_dropped += backlog_draws_dropped;
        static uint32_t logged_gtl_backlog_drops = 0;
        if (logged_gtl_backlog_drops < 64u ||
            (logged_gtl_backlog_drops % 64u) == 0u) {
          REXLOG_INFO(
              "Refii GTL superseded packet backlog #{} frame={} "
              "frames_superseded={} draws_dropped={} newest_ordinal={} "
              "kept_draws={}",
              logged_gtl_backlog_drops, g_direct_renderer.current_frame_index,
              static_cast<uint32_t>(backlog_superseded_ordinals.size()),
              backlog_draws_dropped, newest_complete_ordinal, gtl_draws);
        }
        ++logged_gtl_backlog_drops;
      }
      // Diagnose partial-frame emission (the present-loop deadlock cause): how
      // many packets the replay had, their packet_id range vs the guest frame
      // boundary, and which filter rejected them.
      static uint32_t logged_authoritative_diag = 0;
      // Gate on a HEAVY frame — the light 1-2-draw frames (post-freeze steady
      // state) burned the cap before the real 107-draw compose (run 222: max
      // shadow=2, never saw the heavy frame). Capture frames with real draw
      // activity so we see the compose where the present-loop deadlock triggers.
      const bool heavy_diag_frame =
          g_direct_renderer.gtl_shadow_packets.size() > 5 ||
          command_batch.submitted_commands.size() > 5;
      if (heavy_diag_frame && (logged_authoritative_diag < 60u ||
                               (logged_authoritative_diag % 64u) == 0u)) {
        REXLOG_INFO(
            "Refii GTL authoritative DIAG #{} frame={} boundary={} "
            "shadow_packets={} pid_range=[{}..{}] emitted={} rej[resolve={} "
            "noshader={} nogeom={}] observer_submitted={}",
            ++logged_authoritative_diag, g_direct_renderer.current_frame_index,
            static_cast<unsigned long long>(gtl_guest_frame_boundary),
            g_direct_renderer.gtl_shadow_packets.size(),
            static_cast<unsigned long long>(
                min_pid == UINT64_MAX ? 0 : min_pid),
            static_cast<unsigned long long>(max_pid), gtl_draws, rej_resolve,
            rej_noshader, rej_nogeom, command_batch.submitted_commands.size());
      }
    }
    // AUTHORITATIVE-MODE OBSERVER-CLEAR CENSUS (2026-07-25): gtl_authoritative
    // mode `break`s on the FIRST observer command below, so the WHOLE observer
    // batch is skipped, not just draws -- including whatever clear commands it
    // would have issued that the GTL's own gtl_clear_shadow_packets stream
    // (drained above via emit_gtl_packets_up_to) does NOT cover. An older
    // comment near the loop below cites a run-543 census claiming GTL clears
    // outnumber observer clears (2027 vs 1325) -- if that's stale for THIS
    // content (the storybook menu), this is the direct measurement to prove
    // or disprove it, rather than trusting a comment from an unknown context.
    if (gtl_authoritative) {
      uint32_t observer_clear_count = 0;
      for (const FableDirectRenderCommand& command :
           command_batch.submitted_commands) {
        if (command.type == FableDirectRenderCommandType::kClear) {
          ++observer_clear_count;
        }
      }
      static uint32_t logged_authoritative_clear_census = 0;
      if (observer_clear_count > 0 && logged_authoritative_clear_census < 2000u) {
        ++logged_authoritative_clear_census;
        REXLOG_INFO(
            "Refii GTL authoritative-mode DROPPED-CLEAR census #{} frame={} "
            "observer_would_clear={} gtl_captured_clears={} "
            "observer_total_commands={}",
            logged_authoritative_clear_census,
            g_direct_renderer.current_frame_index, observer_clear_count,
            g_direct_renderer.gtl_clear_shadow_packets.size(),
            static_cast<uint32_t>(command_batch.submitted_commands.size()));
      }
    }
    const bool gtl_video_draws = RendererGtlVideoDrawsEnabled();
    // OBSERVER COMMAND-BATCH MERGE WALK — DELETED 2026-07-26.
    // A ~189-line `for (command : command_batch.submitted_commands)` walk used
    // to live here: it dropped observer clears, swapped plain vertex draws for
    // identity-matched GTL packets in place, and kept unmatched observer draws.
    // Its very first statement was `if (gtl_authoritative) break;`, and
    // gtl_authoritative is true whenever renderer_gtl_authoritative_submission,
    // renderer_gtl_private_rect_compose and renderer_gtl_clears_authoritative
    // are set — all three default to true. So under every shipping
    // configuration the walk executed ZERO iterations and the authoritative
    // packet replay above owned the whole stream. Removed as part of observer
    // deletion. The census counters it fed (other_root, observer_dropped) and
    // the locals it wrote (observer_kept, unmatched_draws) are retained so the
    // submission census line keeps its shape and reads 0 for them.
    emit_gtl_packets_up_to(std::numeric_limits<uint64_t>::max());
    // Video-compose packets the observer funnel DROPPED (no submitted command
    // ever existed to match): insert them from the packet stream so the
    // compose executes anyway. This is the video-white root cause measured in
    // the pixvideo captures — published compose draws never reaching a pump
    // frame. Narrow predicate: the inline-UP compose root with a known YCrCb
    // wrapper pixel shader and captured inline geometry.
    uint32_t gtl_video_inserted = 0;
    if (gtl_video_draws) {
      for (size_t i = 0; i < g_direct_renderer.gtl_shadow_packets.size(); ++i) {
        if (consumed[i]) {
          continue;
        }
        const refii::gtl::DrawPacket& packet =
            g_direct_renderer.gtl_shadow_packets[i];
        if (packet.source_symbol != 0x8220A3F0u ||
            IsGtlPreDispatchBeginPacket(packet)) {
          continue;
        }
        ++g_gtl_video_merge_stats.insert_candidates;
        const uint64_t ps_hash =
            DirectPixelShaderHashLocked(packet.pixel_shader);
        if (!FableVideoCommandUsesKnownVideoPixelShader(ps_hash)) {
          ++g_gtl_video_merge_stats.insert_no_video_ps;
          continue;
        }
        if (packet.primitive_count == 0 || packet.inline_vertex_data.empty()) {
          ++g_gtl_video_merge_stats.insert_no_geometry;
          continue;
        }
        merged.push_back(BuildDirectCommandFromGtlDrawPacketLocked(packet));
        consumed[i] = true;
        ++gtl_video_inserted;
        ++g_gtl_video_merge_stats.video_inserted;
      }
    }
    // PACKET CARRY (second half): this pump frame composed no vertex draws at
    // all, so its packets were never offered a command to match. Discarding them
    // here is exactly what starved the following compose frame. Hand them to the
    // next frame instead. Only carry when nothing was consumed, so packets can
    // never be replayed twice, and bound the carry so a stall cannot grow it
    // without limit (oldest dropped first — they are the most stale).
    if (gtl_draws == 0 && unmatched_draws == 0 && gtl_video_inserted == 0) {
      // Bound tightly. Each packet owns two 1024-dword constant banks (~8KB), so
      // this is a heavyweight vector: a generous cap plus a per-light-frame deep
      // COPY was megabytes of memcpy several times a second for no benefit. The
      // real accumulation between a burst and its compose is ~130 packets, so
      // cap near that and keep only the NEWEST (a packet older than one compose
      // cycle is stale and will never match anything).
      constexpr size_t kMaxCarriedDrawPackets = 512;
      // Non-const so make_move_iterator actually moves. Safe: the merge is
      // already built by this point and only the census's .size() reads this
      // vector afterwards (element count is unchanged by moving out elements).
      auto& src = g_direct_renderer.gtl_shadow_packets;
      const size_t keep = std::min(src.size(), kMaxCarriedDrawPackets);
      g_gtl_carried_draw_packets.assign(
          std::make_move_iterator(src.end() - static_cast<ptrdiff_t>(keep)),
          std::make_move_iterator(src.end()));
    }
    // Sample frames that ACTUALLY COMPOSED, not every 240th frame. The pump
    // interleaves ~3 light frames per heavy one and the heavy composes land on
    // arbitrary indices (1929/1936/1943...), so a %240 gate sampled only
    // submitted=0 frames — three runs produced censuses that all read
    // gtl_draws=0/unmatched=0 purely because nothing was being drawn on the
    // sampled frame. Gate on draw activity instead: first 40 composing frames,
    // then every 64th, so both the early and steady state are covered.
    static uint32_t logged_active_submissions = 0;
    // VERTEX draws only. Including video inserts made every sampled frame a
    // video-only one (run 209: gtl_draws=0 unmatched=0 video_inserted=1 over and
    // over), which spent the whole budget without ever showing a real compose.
    const bool composing_frame = (gtl_draws + unmatched_draws) > 0u;
    if (composing_frame && (logged_active_submissions < 40u ||
                            (logged_active_submissions % 64u) == 0u)) {
      ++logged_active_submissions;
      REXLOG_INFO(
          "Refii GTL submission frame={} gtl_packets={} gtl_draws={} "
          "observer_nondraw_kept={} unmatched_draws={} gtl_resolves={} "
          "gtl_tess_draws={} "
          "video_inserted={} clr_cum[or={} rc={} ae={} od={} ps={}] vid_cum[matched={} inserted={} cand={} no_ps={} "
          "no_geom={}] live[copy={} draw={} depth_def={} ext={} "
          "no_fb={} no_pipe={} no_src={} no_dst={} same_tex={} not_ready={} "
          "rp={} rpc={}] "
          "(ordered in-place)",
          frame_index, g_direct_renderer.gtl_shadow_packets.size(), gtl_draws,
          observer_kept, unmatched_draws, gtl_resolves_emitted,
          gtl_tess_emitted, gtl_video_inserted,
          static_cast<unsigned long long>(g_gtl_clear_merge_stats.other_root),
          static_cast<unsigned long long>(
              g_gtl_clear_merge_stats.resolve_clears),
          static_cast<unsigned long long>(
              g_gtl_clear_merge_stats.authoritative_emitted),
          static_cast<unsigned long long>(
              g_gtl_clear_merge_stats.observer_dropped),
          static_cast<unsigned long long>(
              g_gtl_clear_merge_stats.predicated_skipped),
          static_cast<unsigned long long>(g_gtl_video_merge_stats.video_matched),
          static_cast<unsigned long long>(
              g_gtl_video_merge_stats.video_inserted),
          static_cast<unsigned long long>(
              g_gtl_video_merge_stats.insert_candidates),
          static_cast<unsigned long long>(
              g_gtl_video_merge_stats.insert_no_video_ps),
          static_cast<unsigned long long>(
              g_gtl_video_merge_stats.insert_no_geometry),
          g_gtl_resolve_live_stats.recorded,
          g_gtl_resolve_live_stats.draw_recorded,
          g_gtl_resolve_live_stats.draw_depth_deferred,
          g_gtl_resolve_live_stats.extent_mismatch,
          g_gtl_resolve_live_stats.draw_no_framebuffer,
          g_gtl_resolve_live_stats.draw_no_pipeline,
          g_gtl_resolve_live_stats.no_source_resource,
          g_gtl_resolve_live_stats.no_dest_resource,
          g_gtl_resolve_live_stats.same_texture,
          g_gtl_resolve_live_stats.not_ready,
          static_cast<unsigned long long>(g_gtl_resolve_repoint_refreshes),
          static_cast<unsigned long long>(g_gtl_resolve_repoint_recreates));
    }
    // Guarded because the observer merge walk is gone (see above): with the
    // walk deleted, `merged` holds NO vertex draws unless the authoritative
    // replay produced them, so overwriting the batch unconditionally would
    // blank the frame in the A/B "authoritative off" configuration. In that
    // configuration keep the pure observer batch instead.
    if (gtl_authoritative) {
      // copies= census: each merged command was built by value and moved into
      // the vector once (~12.6KB relocation each; the vector itself moves
      // into the batch for free).
      g_direct_draw_phases.copy_count += merged.size();
      g_direct_draw_phases.copy_bytes +=
          merged.size() * sizeof(FableDirectRenderCommand);
      command_batch.submitted_commands = std::move(merged);
    }
  }
  // RECORD BACKLOG CAP — count-keyed safety valve behind the ordinal-keyed
  // supersession above (separately gated: renderer_record_backlog_cap,
  // default ON). Supersession needs the guest Present boundary to be
  // advancing; a stalled boundary stamps every packet with one ordinal and
  // supersedes nothing, and a single pathological frame can itself exceed
  // several frames of draws. Mirror the guest's own rule at the blunt end
  // (its ring interpreter sub_822DFE08 discards >150ms of stale backlog): if
  // this compose would still record more than ~3 guest frames of draws
  // (~1200 at the title's ~400/frame), drop the OLDEST non-video draw
  // commands down to the cap. Keyed purely on COUNT — works when boundaries
  // stall. Non-draw commands (clears/resolves/state) always survive, and the
  // newest draws sit at the tail of the batch, so drop-from-front spares the
  // most recent frame by construction. Runs on BOTH submission paths (the
  // GTL-authoritative merge and the observer batch).
  if (RendererRecordBacklogCapEnabled()) {
    constexpr uint32_t kRecordBacklogMaxDraws = 1200u;
    const auto cap_droppable_draw =
        [&](const FableDirectRenderCommand& command) {
          if (!IsDirectDrawCommand(command)) {
            return false;
          }
          // Already marked dropped by the capture-time supersession pass:
          // not live, not droppable again.
          if (command.record_dropped) {
            return false;
          }
          if (command.draw.source == RendererDrawEventSource::kPixVideo) {
            return false;
          }
          // Protect the still-assembling frame's published commands
          // (observer path; GTL-built commands carry generation 0 and rely
          // on tail position instead).
          if (command.publish_generation != 0 &&
              command.publish_generation ==
                  command_batch.in_progress_generation) {
            return false;
          }
          // Video compose quads: the video module's two-factor
          // classification (wrapper source + known video pixel shader),
          // never a single enum (run-475 lesson).
          if (FableVideoCommandHasBinkWrapperSource(command) &&
              FableVideoCommandUsesKnownVideoPixelShader(
                  DirectPixelShaderHashLocked(
                      command.resources.pixel_shader))) {
            return false;
          }
          return true;
        };
    uint32_t droppable_draws = 0;
    for (const FableDirectRenderCommand& command :
         command_batch.submitted_commands) {
      droppable_draws += cap_droppable_draw(command) ? 1u : 0u;
    }
    if (droppable_draws > kRecordBacklogMaxDraws) {
      uint32_t to_drop = droppable_draws - kRecordBacklogMaxDraws;
      const uint32_t capped_dropped = to_drop;
      // MARK, don't erase: erase/remove_if here compacted the whole tail of a
      // vector of ~12.6KB elements per cap event (the run-321 memmove-storm
      // class — ~15MB of element moves at the run-486 menu shape). Marked
      // commands are swept by the record path's single strip compaction;
      // BuildEffectiveDirectFrameStatsSnapshot and the scratch-wrapper shader
      // normalizer skip them in between.
      for (FableDirectRenderCommand& command :
           command_batch.submitted_commands) {
        if (to_drop == 0) {
          break;
        }
        if (!cap_droppable_draw(command)) {
          continue;
        }
        command.record_dropped = true;
        --to_drop;
      }
      command_batch.superseded_draws_dropped += capped_dropped;
      static uint32_t logged_record_backlog_caps = 0;
      if (logged_record_backlog_caps < 64u ||
          (logged_record_backlog_caps % 64u) == 0u) {
        // kept = commands not marked dropped (the vector still stores marks
        // until the record strip sweeps them).
        const uint32_t kept_commands = static_cast<uint32_t>(std::count_if(
            command_batch.submitted_commands.begin(),
            command_batch.submitted_commands.end(),
            [](const FableDirectRenderCommand& command) {
              return !command.record_dropped;
            }));
        REXLOG_INFO(
            "Refii plume record backlog CAP #{} frame={} droppable_draws={} "
            "cap={} draws_dropped={} kept_commands={}",
            logged_record_backlog_caps, g_direct_renderer.current_frame_index,
            droppable_draws, kRecordBacklogMaxDraws, capped_dropped,
            kept_commands);
      }
      ++logged_record_backlog_caps;
    }
  }
  // For the compose lock census: the batch is moved into the record loop
  // below, so hold the supersession + cap counts here — AFTER the GTL packet
  // supersession and the record backlog cap, both of which extend them.
  const uint32_t census_superseded_frames = command_batch.superseded_frames;
  const uint32_t census_superseded_draws =
      command_batch.superseded_draws_dropped;
  const FableDirectStateSnapshot frame_state_snapshot =
      BuildEffectiveDirectFrameStatsSnapshot(command_batch);
  // COMPOSE LOCK BOUNDARIES (2026-07-30). The registry recursive mutex used
  // to be acquired HERE and held to the end of the function, which put the
  // whole 46-100ms compose inside the one lock guest hook writers queue
  // behind (fable_direct_state.cpp submit-chain census: "held by the present
  // pump for most of each recorded frame"). Phase audit:
  //   - identity drain: self-locking (hook_observer.cpp DrainPending* swap
  //     their queues under their own mutexes and apply through self-locking
  //     registry entry points), no caller hold needed;
  //   - EnsureDirectDrawResourcesLocked / Bink plane apply / retire trim:
  //     touch only g_direct_renderer + g_plume_resources state owned by this
  //     thread under g_direct_renderer.mutex — registry-free;
  //   - record loop + surface-destination pass: read registry storage RAW
  //     (serial-validated caches retaining pointers into registry maps, e.g.
  //     DirectPrivateTextureCopyActionCacheEntry::action; identity-object
  //     vectors dereferenced after self-locking lookups return) — these NEED
  //     the hold and CANNOT run unlocked without a snapshot layer;
  //   - normalize / present blit / hook-stats refresh / logging:
  //     registry-free (and the 30-frame observer snapshot inside the stats
  //     refresh no longer runs while holding the registry, which inverted
  //     the canonical observer -> registry order).
  // So the hold now starts at the record boundary below and ends right after
  // the surface-destination pass; the compose lock census at the bottom
  // proves the split per run.
  const FableDirectClock::time_point prep_t0 = FableDirectClock::now();
  DrainPendingFableDirectTextureIdentityOperations();
  const FableDirectClock::time_point ensure_t0 = FableDirectClock::now();
  EnsureDirectDrawResourcesLocked(g_direct_renderer.device, shader_format);
  const FableDirectClock::time_point ensure_t1 = FableDirectClock::now();
  // GTL content channel: apply Bink plane publishes delivered outside the
  // observer-mutex harvest. Creation still belongs to the first regular
  // sync; this only refreshes textures that already exist.
  std::vector<FableGuestResourceSnapshot> bink_plane_retry;
  for (FableGuestResourceSnapshot &plane_snapshot :
       DrainBinkPlaneSnapshotsForPlume()) {
    auto plane_it =
        g_direct_renderer.resources.find(plane_snapshot.guest_address);
    if (plane_it == g_direct_renderer.resources.end() ||
        !plane_it->second.guest_texture.texture) {
      bink_plane_retry.push_back(std::move(plane_snapshot));
      continue;
    }
    // Green-strip hunt, plume side: mirror of the observer publish scanner.
    // A chroma plane arriving here with a mostly-zero right region is the
    // strip entering the host texture; if this never fires while the strip
    // shows, the drawn textures are healthy and the defect is compose-side.
    {
      const uint32_t plane_width = PlumeGuestTextureWidth(plane_it->second);
      const uint32_t plane_height = PlumeGuestTextureHeight(plane_it->second);
      if (plane_width >= 128u && plane_height >= 128u &&
          plane_width <= 1024u &&
          plane_snapshot.upload_data.size() >=
              static_cast<size_t>(plane_width) * plane_height) {
        const uint32_t right_start = (plane_width * 3u) / 4u;
        uint32_t zero_rows = 0;
        uint32_t sampled_rows = 0;
        for (uint32_t y = 0; y < plane_height; y += 8u) {
          ++sampled_rows;
          const uint8_t* row =
              plane_snapshot.upload_data.data() +
              static_cast<size_t>(y) * plane_width;
          bool row_zero = true;
          for (uint32_t x = right_start; x < plane_width; x += 16u) {
            if (row[x] != 0) {
              row_zero = false;
              break;
            }
          }
          zero_rows += row_zero ? 1u : 0u;
        }
        if (sampled_rows != 0 && zero_rows * 2u > sampled_rows) {
          static uint32_t logged_plume_chroma_zero_right = 0;
          if (logged_plume_chroma_zero_right < 24u ||
              (logged_plume_chroma_zero_right % 64u) == 0u) {
            REXLOG_INFO(
                "Refii plume bink apply right-zero #{} guest=0x{:08X} "
                "{}x{} bytes={} zero_rows={}/{} gen={}",
                logged_plume_chroma_zero_right,
                plane_snapshot.guest_address, plane_width, plane_height,
                plane_snapshot.upload_data.size(), zero_rows, sampled_rows,
                static_cast<unsigned long long>(
                    plane_snapshot.upload_generation));
          }
          ++logged_plume_chroma_zero_right;
        }
      }
    }
    if (RefreshTrackedTextureUpload(g_direct_renderer.device, plane_snapshot,
                                    plane_it->second)) {
      RecordPendingTextureUploadLocked(command_list, plane_it->second);
    }
  }
  if (!bink_plane_retry.empty()) {
    RequeueBinkPlaneSnapshotsForPlume(std::move(bink_plane_retry));
  }
  ++g_plume_resources.resource_retire_epoch;
  const FableDirectClock::time_point trim_t0 = FableDirectClock::now();
  TrimRetiredPlumeResourcesLocked();
  const FableDirectClock::time_point trim_t1 = FableDirectClock::now();
  // RecordDirectRenderCommandsLocked consumes/clears the packet queue. Take
  // the ownership decision before that boundary so the post-draw fallback
  // cannot mistake an emptied queue for an unowned primitive family.
  const bool packet_queue_owns_surface_destination =
      std::any_of(g_direct_renderer.refii_packet_queue.actions.begin(),
                  g_direct_renderer.refii_packet_queue.actions.end(),
                  [](const RefiiFrameActionPacket& action) {
                    return action.type ==
                           RefiiFrameActionType::kSurfaceDestination;
                  });
  g_direct_renderer.packet_queue_owns_surface_destination =
      packet_queue_owns_surface_destination;
  // Registry hold begins here (see COMPOSE LOCK BOUNDARIES above). Depth must
  // stay exactly one across the record: SyncDirectResourcesLocked's blocking
  // observer branch drops the lock via ScopedFableDirectRegistryUnlock, which
  // pops a single level.
  const FableDirectClock::time_point registry_wait_t0 = FableDirectClock::now();
  std::unique_lock<std::recursive_mutex> registry_lock(
      FableDirectGuestResourceRegistryMutex());
  const FableDirectClock::time_point registry_hold_t0 = FableDirectClock::now();
  // Reset the per-frame identity-backfill budget and complete carried-over
  // backfills, bounded (run 477: the eager per-registration backfill made a
  // post-idle transition frame cost multi-second composes; see
  // FableDirectDrainPendingTextureIdentityBackfills).
  FableDirectDrainPendingTextureIdentityBackfills(2.0);
  RecordDirectRenderCommandsLocked(g_direct_renderer.device, command_list,
                                   output_framebuffer, output_texture,
                                   present_framebuffer, output_depth_format,
                                   std::move(command_batch));
  const FableDirectClock::time_point commands_t1 = FableDirectClock::now();
  const uint32_t post_draw_surface_destination_commands =
      !packet_queue_owns_surface_destination &&
              g_direct_renderer
                      .packet_queue_replayed_surface_destination_actions ==
                  0
          ? RecordDirectSurfaceDestinationCopiesLocked(command_list, false)
          : 0u;
  if (post_draw_surface_destination_commands != 0) {
    g_direct_renderer.render_graph_graph_owned_resolve_commands +=
        post_draw_surface_destination_commands;
    g_direct_renderer.render_graph_graph_owned_resolve_steps = 1;
    g_direct_renderer.render_graph_graph_owned_render_steps =
        g_direct_renderer.render_graph_graph_owned_clear_steps +
        g_direct_renderer.render_graph_graph_owned_draw_steps +
        g_direct_renderer.render_graph_graph_owned_resolve_steps;
  }
  g_direct_renderer.packet_queue_owns_surface_destination = false;
  // Population sizes for the compose lock census below: run 473's record-loop
  // runaway grew in exactly these tables, so the census names them every time
  // it fires — bounded-vs-growing is visible without a second instrument.
  // Read under the registry hold; four size() loads.
  const FableGuestResourceRegistry& census_registry =
      FableDirectGuestResourceRegistry();
  const size_t census_private_targets =
      census_registry.private_texture_copies_by_target.size();
  const size_t census_action_bank =
      census_registry.private_texture_copy_action_by_wrapper_call.size() +
      census_registry.private_texture_copy_action_by_wrapper_context.size();
  const size_t census_links =
      census_registry.surface_texture_links_by_surface.size();
  const size_t census_link_dest =
      census_registry.surface_texture_link_by_destination_texture.size();
  const FablePrivateBackfillCensus census_backfill =
      FableDirectConsumePrivateBackfillCensus();
  // Last registry reader was the surface-destination pass; everything from
  // here on is g_direct_renderer-only, so let guest hook writers back in.
  registry_lock.unlock();
  const FableDirectClock::time_point registry_hold_t1 = FableDirectClock::now();
  NormalizeDirectSurfaceDestinationStatsLocked();
  const FableDirectClock::time_point aliases_t1 = FableDirectClock::now();
  if (g_direct_renderer.packet_queue_replayed_present_blit_actions == 0) {
    RecordDirectPresentBlitLocked(g_direct_renderer.device, command_list,
                                  output_texture, present_framebuffer,
                                  shader_format, frame_state_snapshot);
  }
  const FableDirectClock::time_point present_t1 = FableDirectClock::now();
  RefreshDirectStatsFromHooksLocked(frame_index, false, &frame_state_snapshot);
  if (post_draw_surface_destination_commands != 0) {
    g_direct_renderer.stats.last_render_commands = ClampCounter(
        uint64_t(g_direct_renderer.stats.last_render_commands) +
        post_draw_surface_destination_commands);
  }
  const FableDirectClock::time_point stats_t1 = FableDirectClock::now();
  const double total_ms = FableDirectElapsedMs(ensure_t0, stats_t1);
  if (total_ms >= 20.0 || frame_index % 120u == 0) {
    static uint32_t logged_direct_timing_breakdowns = 0;
    if (logged_direct_timing_breakdowns < 160u) {
      REXLOG_INFO(
          "Refii plume direct renderer timing #{} frame={} total_ms={:.3f} "
          "ensure_ms={:.3f} trim_ms={:.3f} commands_ms={:.3f} "
          "aliases_ms={:.3f} present_ms={:.3f} stats_ms={:.3f} "
          "resources={} pipelines={} uploads={} pipeline_hits={} "
          "pipeline_misses={} target=0x{:08X}",
          ++logged_direct_timing_breakdowns, frame_index, total_ms,
          FableDirectElapsedMs(ensure_t0, ensure_t1),
          FableDirectElapsedMs(trim_t0, trim_t1),
          FableDirectElapsedMs(trim_t1, commands_t1),
          FableDirectElapsedMs(commands_t1, aliases_t1),
          FableDirectElapsedMs(aliases_t1, present_t1),
          FableDirectElapsedMs(present_t1, stats_t1),
          g_direct_renderer.resources.size(), g_direct_renderer.pipelines.size(),
          g_direct_renderer.resource_uploads,
          g_direct_renderer.stats.pipeline_cache_hits,
          g_direct_renderer.stats.pipeline_cache_misses,
          g_direct_renderer.stats.last_present_target);
    }
    // Compose lock census: hold_ms is the registry-mutex hold (record loop +
    // surface-destination pass), record_unlocked_ms is compose work now
    // running OUTSIDE the hold (identity drain / ensure / bink apply / trim
    // before it, normalize / present blit / hook-stats refresh after),
    // reacquire_ms is the wait to take the registry at the record boundary
    // (guest-side contention made visible). Same cadence as the timing line
    // above so the two correlate per frame.
    static uint32_t logged_compose_lock_censuses = 0;
    if (logged_compose_lock_censuses < 160u) {
      REXLOG_INFO(
          "Refii compose lock census #{} frame={} hold_ms={:.3f} "
          "record_unlocked_ms={:.3f} reacquire_ms={:.3f} total_ms={:.3f} "
          "private_targets={} action_bank={} links={} link_dest={} "
          "frames_superseded={} draws_dropped={} backfill_ms={:.3f} "
          "backfill[inline={} deferred={} drained={} dropped={} pending={} "
          "actions={} links={}]",
          ++logged_compose_lock_censuses, frame_index,
          FableDirectElapsedMs(registry_hold_t0, registry_hold_t1),
          FableDirectElapsedMs(prep_t0, registry_wait_t0) +
              FableDirectElapsedMs(registry_hold_t1, stats_t1),
          FableDirectElapsedMs(registry_wait_t0, registry_hold_t0),
          total_ms, census_private_targets, census_action_bank, census_links,
          census_link_dest, census_superseded_frames,
          census_superseded_draws, census_backfill.backfill_ms,
          census_backfill.inline_backfills, census_backfill.deferred,
          census_backfill.drained, census_backfill.dropped,
          census_backfill.pending, census_backfill.actions_examined,
          census_backfill.links_examined);
    }
  }
  if (!g_direct_renderer.diagnostics.logged_first_frame) {
    REXLOG_INFO(
        "Refii plume direct renderer is active frame={} draws={} indexed={} "
        "commands={} resources={} uploads={} target=0x{:08X}",
        frame_index, g_direct_renderer.stats.draw_count,
        g_direct_renderer.stats.indexed_draw_count,
        g_direct_renderer.stats.last_render_commands,
        g_direct_renderer.stats.resource_creations,
        g_direct_renderer.stats.resource_uploads,
        g_direct_renderer.stats.last_present_target);
    g_direct_renderer.diagnostics.logged_first_frame = true;
  }
}
#endif

void BeginFableDirectFrame(uint32_t frame_index) {
  std::lock_guard<std::mutex> lock(g_direct_renderer.mutex);
  ++g_direct_renderer.stats.frames_begun;
  g_direct_renderer.stats.last_frame_index = frame_index;
  g_direct_renderer.stats.frame_active = true;
  g_direct_renderer.stats.presented = false;
  g_direct_renderer.stats.renderdoc_capture_started = false;
  g_direct_renderer.stats.last_present_target = 0;
  g_direct_renderer.stats.last_present_width = 0;
  g_direct_renderer.stats.last_present_height = 0;
  g_direct_renderer.stats.packet_queue_depth = 0;
  g_direct_renderer.stats.packet_queue_replay_planned = 0;
  g_direct_renderer.stats.packet_queue_replay_unsupported = 0;
  g_direct_renderer.stats.packet_queue_replay_enabled = false;
  // Recycle this frame slot's constant arena. Safe by the frame-context fence:
  // plume waits on the context from N frames ago before recording starts.
  ResetDirectConstantArenaForFrameLocked(frame_index);
  // Release views replaced 4+ frames ago -- see RetainedTextureView. Guarded
  // against a frame_index that has not advanced (or wrapped) so a stalled
  // counter can never free a view the GPU still holds.
  {
    constexpr uint32_t kRetainedViewFrameMargin = 4u;
    if (frame_index >= kRetainedViewFrameMargin) {
      const uint32_t retire_before = frame_index - kRetainedViewFrameMargin;
      auto& retained = g_direct_renderer.retained_replaced_texture_views;
      const size_t before = retained.size();
      retained.erase(
          std::remove_if(retained.begin(), retained.end(),
                         [retire_before](const auto& entry) {
                           return entry.frame_index <= retire_before;
                         }),
          retained.end());
      const size_t retired = before - retained.size();
      if (retired != 0) {
        g_direct_renderer.stats.retired_replaced_texture_views += retired;
      }
      g_direct_renderer.stats.live_replaced_texture_views = retained.size();
    }
  }
  g_direct_resource_lookup_census = DirectResourceLookupCensus{};
  g_direct_draw_phases = DirectDrawPhaseAccumulator{};
  g_direct_surface_destination_split = DirectSurfaceDestinationSplit{};
  ResetDirectRenderGraphExecutionSnapshotLocked(frame_index);
  g_direct_renderer.frame_database_draw_packets.clear();
  g_direct_renderer.frame_database_draw_packet_index_by_serial.clear();
  g_direct_renderer.frame_database_command_packets.clear();
  g_direct_renderer.frame_database_omitted_draw_packets = 0;
  g_direct_renderer.frame_database_omitted_command_packets = 0;
  g_direct_renderer.refii_packet_queue = {};
  ResetDirectPrivateFetchAliasFrameCountersLocked();
#if REFII_RENDERER_HAS_PLUME
  // Frame-scoped by design — see DirectPrivateFeedbackEnsureMemoEntry.
  g_direct_renderer.private_feedback_ensure_memo.clear();
  // Per-compose by design (framebuffer pointers must not outlive the
  // retire-epoch guarantee) — see DirectClearFramebufferMemoEntry.
  g_direct_renderer.clear_framebuffer_memo.clear();
  g_direct_renderer.materialized_private_rect_action_frame_keys.clear();
  g_direct_renderer.begin_vertices_title_action_frame_keys.clear();
  g_direct_renderer.preflight_private_copy_action_frame_keys.clear();
  g_direct_renderer.remembered_private_copy_action_frame_keys.clear();
  g_direct_renderer.remembered_private_rect_target_source_frame_keys.clear();
  g_direct_renderer.private_copy_action_surface_link_frame_keys.clear();
  g_direct_renderer.materialized_private_surface_link_frame_keys.clear();
  g_direct_renderer.remembered_private_feedback_surface_link_frame_keys.clear();
  g_direct_renderer.exact_private_rect_edge_frame_keys.clear();
  g_direct_renderer.current_target_surface_link_frame_keys.clear();
  g_direct_renderer.sibling_source_action_owner_frame_keys.clear();
  g_direct_renderer.surface_link_command_identity_exclusion_frame_keys.clear();
  g_direct_renderer.initialized_title_scene_targets_this_frame.clear();
  g_direct_renderer.targetless_wrapper_materialization_reject_frame_keys.clear();
  g_direct_renderer.materialized_private_rect_actions_by_exact_key.clear();
#endif
  g_direct_renderer.frame_written_color_targets.clear();
  if (!g_plume_resources.frame_texture_upload_slabs.empty()) {
    g_plume_resources.frame_texture_upload_slab_index =
        frame_index %
        static_cast<uint32_t>(
            g_plume_resources.frame_texture_upload_slabs.size());
    const uint32_t slab_frame =
        g_plume_resources.frame_texture_upload_slab_index;
    g_plume_resources.frame_texture_upload_slab_indices[slab_frame] = 0;
    for (PlumeFrameTextureUploadSlab& slab :
         g_plume_resources.frame_texture_upload_slabs[slab_frame]) {
      slab.offset = 0;
    }
    g_plume_resources.frame_texture_upload_decode_caches[slab_frame].clear();
  }
  RefreshDirectStatsFromHooksLocked(frame_index);
}

void EndFableDirectFrame() {
  std::lock_guard<std::mutex> lock(g_direct_renderer.mutex);
  ++g_direct_renderer.stats.frames_ended;
  g_direct_renderer.stats.frame_active = false;
  // Unmapping flushes each chunk's written range through VMA -- this is the
  // correctness half of keeping chunks mapped across the frame, not cleanup.
  FlushDirectConstantArenaLocked();
}

void PresentFableDirectFrame(uint32_t present_target, uint32_t width,
                             uint32_t height) {
  std::lock_guard<std::mutex> lock(g_direct_renderer.mutex);
  ++g_direct_renderer.stats.presents;
  g_direct_renderer.stats.presented = true;
  if (present_target != 0) {
    g_direct_renderer.stats.last_present_target = present_target;
  }
  g_direct_renderer.stats.last_present_width = width;
  g_direct_renderer.stats.last_present_height = height;
}

void ResetFableDirectLivePresentSourcesLocked(bool reset_stats) {
  ResetLiveScenePresentSourceLocked();
  ResetSubmittedScenePresentSourceLocked();
  ResetFableSceneTargetLocked();
  if (reset_stats) {
    g_direct_renderer.stats.last_present_target = 0;
    g_direct_renderer.stats.last_present_width = 0;
    g_direct_renderer.stats.last_present_height = 0;
  }
}

FableDirectRendererStats GetFableDirectRendererStats() {
  std::lock_guard<std::mutex> lock(g_direct_renderer.mutex);
  return g_direct_renderer.stats;
}

FableDirectFrameDatabaseDrawPackets GetFableDirectFrameDatabaseDrawPackets() {
  std::lock_guard<std::mutex> lock(g_direct_renderer.mutex);
  FableDirectFrameDatabaseDrawPackets packets;
  packets.packets = g_direct_renderer.frame_database_draw_packets;
  packets.omitted_packets = g_direct_renderer.frame_database_omitted_draw_packets;
  return packets;
}

FableDirectFrameDatabaseCommandPackets
GetFableDirectFrameDatabaseCommandPackets() {
  std::lock_guard<std::mutex> lock(g_direct_renderer.mutex);
  FableDirectFrameDatabaseCommandPackets packets;
  packets.packets = g_direct_renderer.frame_database_command_packets;
  packets.omitted_packets =
      g_direct_renderer.frame_database_omitted_command_packets;
  return packets;
}

RefiiFramePacketDump GetRefiiFramePacketDump() {
  std::lock_guard<std::mutex> lock(g_direct_renderer.mutex);
  RefiiFramePacketDump dump;
  dump.frame_id = g_direct_renderer.refii_packet_queue.frame_id;
  dump.omitted_packets = g_direct_renderer.refii_packet_queue.omitted_packets;
  dump.omitted_actions = g_direct_renderer.refii_packet_queue.omitted_actions;
  dump.packets = g_direct_renderer.refii_packet_queue.packets;
  dump.actions = g_direct_renderer.refii_packet_queue.actions;
  return dump;
}

RefiiFramePacketQueueSnapshot GetRefiiFramePacketQueueSnapshot() {
  std::lock_guard<std::mutex> lock(g_direct_renderer.mutex);
  return g_direct_renderer.refii_packet_queue;
}

FableDirectResourceDebugSnapshot GetFableDirectResourceDebugSnapshot() {
#if REFII_RENDERER_HAS_PLUME
  std::lock_guard<std::mutex> lock(g_direct_renderer.mutex);
  std::vector<const PlumeTrackedResource*> resources;
  resources.reserve(g_direct_renderer.resources.size());
  for (const auto& [address, tracked] : g_direct_renderer.resources) {
    (void)address;
    if (tracked.kind == FableGuestResourceKind::kUnknown ||
        tracked.guest_address == 0) {
      continue;
    }
    resources.push_back(&tracked);
  }
  return BuildFableDirectResourceDebugSnapshot(std::move(resources));
#else
  return {};
#endif
}

FableDirectRenderGraphSnapshot GetFableDirectRenderGraphSnapshot() {
  std::lock_guard<std::mutex> lock(g_direct_renderer.mutex);
  return BuildFableDirectRenderGraphSnapshot(
      g_direct_renderer.current_frame_index,
      g_direct_renderer.frame_database_draw_packets,
      g_direct_renderer.frame_database_omitted_draw_packets,
      g_direct_renderer.frame_database_command_packets,
      g_direct_renderer.frame_database_omitted_command_packets);
}

FableDirectRenderGraphExecutionSnapshot
GetFableDirectRenderGraphExecutionSnapshot() {
  std::lock_guard<std::mutex> lock(g_direct_renderer.mutex);
  return BuildFableDirectRenderGraphExecutionSnapshot(
      g_direct_renderer.current_frame_index,
      g_direct_renderer.render_graph_graph_owned_render_steps,
      g_direct_renderer.render_graph_graph_owned_clear_steps,
      g_direct_renderer.render_graph_graph_owned_clear_commands,
      g_direct_renderer.render_graph_graph_owned_draw_steps,
      g_direct_renderer.render_graph_graph_owned_draw_commands,
      g_direct_renderer.render_graph_graph_owned_resolve_steps,
      g_direct_renderer.render_graph_graph_owned_resolve_commands,
      g_direct_renderer.render_graph_bridge_steps,
      g_direct_renderer.render_graph_bridge_commands,
      g_direct_renderer.frame_database_command_packets);
}

bool FableDirectQueuedDrawPresent(uint64_t draw_id, size_t max_commands) {
  if (draw_id == 0 || max_commands == 0) {
    return false;
  }

  const std::vector<FableDirectRenderCommand> commands =
      FableDirectQueuedRenderCommandSnapshot(max_commands);
  for (const FableDirectRenderCommand& command : commands) {
    if (DirectRenderCommandTypeIsDraw(command.type) &&
        DirectRenderCommandDrawId(command) == draw_id) {
      return true;
    }
  }
  return false;
}

bool FableDirectQueuedRenderPassPresent(std::string_view normalized_pass_name,
                                        size_t max_commands) {
  if (normalized_pass_name.empty() || max_commands == 0) {
    return false;
  }

  const std::vector<FableDirectRenderCommand> commands =
      FableDirectQueuedRenderCommandSnapshot(max_commands);
  std::lock_guard<std::mutex> lock(g_direct_renderer.mutex);
  for (const FableDirectRenderCommand& command : commands) {
    if (!DirectRenderCommandTypeIsDraw(command.type)) {
      continue;
    }
    const char* pass_name = ClassifyDirectRenderPassLocked(command);
    if (NormalizeDirectRenderPassName(pass_name) == normalized_pass_name) {
      return true;
    }
  }
  return false;
}

bool FableDirectQueuedRenderDocTargetPresent(std::string_view target,
                                             size_t max_commands) {
  if (target.empty() || max_commands == 0) {
    return false;
  }

  const std::string normalized_target(target);
  const std::vector<FableDirectRenderCommand> commands =
      FableDirectQueuedRenderCommandSnapshot(max_commands);
  std::lock_guard<std::mutex> lock(g_direct_renderer.mutex);
  for (const FableDirectRenderCommand& command : commands) {
    if (IsRenderDocResolveTargetMatch(normalized_target, command) ||
        IsRenderDocDrawTargetMatchLocked(normalized_target, command, nullptr)) {
      return true;
    }
  }
  return false;
}

void ResetFableDirectLivePresentSources() {
  std::lock_guard<std::mutex> lock(g_direct_renderer.mutex);
  ResetFableDirectLivePresentSourcesLocked();
}

void ShutdownFableDirectRenderer() {
  std::lock_guard<std::mutex> lock(g_direct_renderer.mutex);
  // Final address-gate census: the whole-run totals, emitted once at teardown
  // so a run that never reaches the periodic dump still reports which
  // hardcoded guest-address gates were load-bearing and which were dead.
  ReportDirectAddressGateCensus("final");
  g_direct_renderer.stats = {};
  g_direct_renderer.frame_database_draw_packets.clear();
  g_direct_renderer.frame_database_draw_packet_index_by_serial.clear();
  g_direct_renderer.frame_database_command_packets.clear();
  g_direct_renderer.frame_database_omitted_draw_packets = 0;
  g_direct_renderer.frame_database_omitted_command_packets = 0;
  g_direct_renderer.refii_packet_queue = {};
  g_direct_renderer.resource_serial = 0;
  g_direct_renderer.resource_creations = 0;
  g_direct_renderer.resource_destroys = 0;
  g_direct_renderer.resource_uploads = 0;
  g_direct_renderer.failed_resource_syncs = 0;
  ResetDirectDiagnosticsLocked();
  g_direct_renderer.cached_hook_stats = RendererHookObserverSnapshot{};
  g_direct_renderer.have_cached_hook_stats = false;
  g_direct_renderer.last_hook_stats_frame = 0;
#if REFII_RENDERER_HAS_PLUME
  g_direct_renderer.device = nullptr;
  g_direct_renderer.shader_format = plume::RenderShaderFormat::UNKNOWN;
  g_direct_renderer.resources.clear();
  InvalidateDirectResourceFastCacheLocked();
  ClearDirectResourceMemosLocked();
  g_direct_renderer.known_uploads.clear();
  g_direct_renderer.registered_texture_identity_cache.clear();
  g_direct_renderer.pipelines.clear();
  ClearDirectPipelineEnsureMemoLocked();
  g_direct_renderer.tess_vertex_shader_variants.clear();
  g_direct_renderer.tess_vertex_shader_variant_misses.clear();
  g_direct_renderer.tess_grid_index_buffers.clear();
  g_direct_renderer.frame_upload_buffers.clear();
  g_direct_renderer.cached_upload_buffers.clear();
  g_direct_renderer.clear_framebuffer_memo.clear();
  g_direct_renderer.private_texture_aliases_by_fetch.clear();
  g_direct_renderer.private_texture_aliases_by_base.clear();
  g_direct_renderer.private_texture_aliases_by_object.clear();
  g_direct_renderer.gpu_drawn_textures_by_base.clear();
  g_direct_renderer.private_texture_alias_serial = 0;
  ResetDirectPrivateFetchAliasFrameCountersLocked();
  g_direct_renderer.private_texture_feedback_aliases.clear();
  g_direct_renderer.private_texture_feedback_aliases_by_target_identity.clear();
  g_direct_renderer.private_texture_feedback_alias_target_serial.clear();
  g_direct_renderer.private_texture_feedback_alias_order.clear();
  g_direct_renderer.private_feedback_ensure_memo.clear();
  g_direct_renderer.surface_alias_copy_cache.clear();
  g_direct_renderer.private_texture_copy_action_cache.clear();
  g_direct_renderer.private_texture_copy_action_sample_cache.clear();
  g_direct_renderer.private_texture_exact_interest_cache.clear();
  g_direct_renderer.mutable_private_header_exact_samples.clear();
  g_direct_renderer.private_texture_backing_miss_cache.clear();
  g_direct_renderer.registered_private_rect_execute_decision_cache.clear();
  g_direct_renderer.command_input_ready_cache.clear();
  g_direct_renderer.exact_current_producer_owner_cache.clear();
  g_direct_renderer.surface_texture_link_sample_frame_cache.clear();
  g_direct_renderer.surface_destination_copy_frame_keys.clear();
  g_direct_renderer.prepared_exact_copy_destination_keys.clear();
  g_direct_renderer.published_stable_surface_texture_link_keys.clear();
  g_direct_renderer.materialized_private_rect_action_frame_keys.clear();
  g_direct_renderer.begin_vertices_title_action_frame_keys.clear();
  g_direct_renderer.preflight_private_copy_action_frame_keys.clear();
  g_direct_renderer.remembered_private_copy_action_frame_keys.clear();
  g_direct_renderer.stable_private_copy_action_keys.clear();
  g_direct_renderer.remembered_private_rect_target_source_frame_keys.clear();
  g_direct_renderer.private_copy_action_surface_link_frame_keys.clear();
  g_direct_renderer.materialized_private_surface_link_frame_keys.clear();
  g_direct_renderer.remembered_private_feedback_surface_link_frame_keys.clear();
  g_direct_renderer.exact_private_rect_edge_frame_keys.clear();
  g_direct_renderer.current_target_surface_link_frame_keys.clear();
  g_direct_renderer.sibling_source_action_owner_frame_keys.clear();
  g_direct_renderer.surface_link_command_identity_exclusion_frame_keys.clear();
  g_direct_renderer.duplicate_exact_private_rect_surface_link_keys.clear();
  g_direct_renderer.retired_stale_surface_link_log_keys.clear();
  g_direct_renderer.targetless_wrapper_materialization_reject_frame_keys.clear();
  g_direct_renderer.materialized_private_rect_actions_by_exact_key.clear();
  g_direct_renderer.next_private_texture_upload_alias = 0xF1000000u;
  g_direct_renderer.next_title_billboard_upload_alias = 0xF2000000u;
  g_direct_renderer.cached_upload_buffer_count = 0;
  g_direct_renderer.cached_upload_buffer_trim_frame = UINT32_MAX;
  DestroyDirectConstantArenaLocked();
  g_direct_renderer.default_sampler.reset();
  ResetDirectPresentTrackingLocked();
  g_direct_renderer.pending_video_hold_texture = nullptr;
  g_direct_renderer.retained_replaced_texture_views.clear();
  g_direct_renderer.pending_surface_content_carries.clear();
  g_direct_renderer.deferred_resolve_interest.clear();
  ResetPlumeResourceManagerObjectsLocked();
  g_direct_renderer.draw_resources_ready = false;
  g_direct_renderer.last_failed_direct_draw_reason.clear();
  g_direct_renderer.last_failed_direct_draw_serial = 0;
  FableDirectClearPrivateTextureCopyActions();
  FableDirectClearTextureIdentities();
  FableDirectClearTextureHeaderActions();
  FableDirectClearShaderIdentities();
  FableDirectClearVertexDeclarationIdentities();
  ResetFableDirectLivePresentSourcesLocked();
  ResetDirectRenderGraphExecutionSnapshotLocked(0);
  g_direct_renderer.current_frame_has_pix_video_draw = false;
  ResetPixVideoPresentTargetLocked();
  g_direct_renderer.scene_write_serial = 0;
  // The present log-throttle fields reset with the rest of present tracking
  // in ResetDirectPresentTrackingLocked() above.
#endif
}


}  // namespace refii::renderer
