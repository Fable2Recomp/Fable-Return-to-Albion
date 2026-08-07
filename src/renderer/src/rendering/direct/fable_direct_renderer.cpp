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
PlumeResourceManagerState g_plume_resources;
#endif


using DirectTextureFetchInfo = FableTextureFetchInfo;

using FableDirectClock = std::chrono::steady_clock;

double FableDirectElapsedMs(FableDirectClock::time_point begin,
                            FableDirectClock::time_point end) {
  return std::chrono::duration<double, std::milli>(end - begin).count();
}


#if REFII_RENDERER_HAS_PLUME

const char* DirectPrivateTextureResolveSourceName(
    DirectPrivateTextureResolveSource source) {
  switch (source) {
    case DirectPrivateTextureResolveSource::kNone:
      return "none";
    case DirectPrivateTextureResolveSource::kIdentityCommandFetch:
      return "identity-command-fetch";
    case DirectPrivateTextureResolveSource::kIdentityResourceFetch:
      return "identity-resource-fetch";
    case DirectPrivateTextureResolveSource::kIdentityBase:
      return "identity-base";
  }
  return "unknown";
}


bool DirectPrivateTextureCopyActionMatchIsExactRegistered(
    DirectPrivateTextureCopyActionMatch match) {
  switch (match) {
    case DirectPrivateTextureCopyActionMatch::kExactDestination:
    case DirectPrivateTextureCopyActionMatch::kExactTrackedDestination:
    case DirectPrivateTextureCopyActionMatch::kExactParentDestination:
    case DirectPrivateTextureCopyActionMatch::kExactIdentityDestination:
    case DirectPrivateTextureCopyActionMatch::kExactSurfaceTextureLink:
    case DirectPrivateTextureCopyActionMatch::kExactCurrentColorAction:
      return true;
    default:
      return false;
  }
}


// Cross-draw memo for the private-rect feedback-alias ensure/prewarm pass
// (aliassplit feedback=, 9-11ms/compose at title, run 480). The ensure body is
// pure lookup + idempotent upsert: given identical inputs it re-derives the
// identical registrations, so once it has run for a (draw-key, slot) the
// re-run is a provable no-op until an input changes. The inputs are covered by
// EQUIVALENCE-CHECKED generations only (never raw store-counter serials — the
// run-479 lesson; surface_texture_link_serial in particular mints a serial per
// registration attempt and would defeat every hit):
//  - relation_generation: FableDirectGetSurfaceDestinationRelationGeneration()
//    — the destscan generation; link registration stores its destination
//    relation through SurfaceDestinationRelationStoreEquivalent, so any real
//    link/relation change moves it while byte-identical re-registrations do
//    not.
//  - private_copy_serial: DirectPrivateTextureCopySerialLocked() — the copy-
//    action bank (registration serial + equivalence-checked index serial).
//  - texture_identity_serial: identity registration triggers the link/action
//    index BACKFILLS that bypass the relation store — this covers them.
//  - private_texture_alias_serial: the renderer-side alias maps, bumped only
//    on real value change via MarkDirectPrivateTextureAliasStateChangedLocked.
//  - source_content_serial: the SAMPLED feedback-source surface's visible-
//    content serial — a redrawn storybook panel invalidates the memo for
//    draws that sample it. This is deliberately NOT the bound RT0's serial:
//    every submitted draw marks its bound color targets GPU-produced
//    (RememberDrawnSceneTargetLocked / the synthetic private-rect mark in the
//    submit path), so keying on RT0 made the memo miss 100% by construction
//    (run 481: fbmemo=0/2374). The redrawn-panel case is additionally covered
//    by private_copy_serial: the panel's refresh registers a copy action whose
//    content-serial fields fail PrivateTextureCopyActionStoreEquivalent and
//    bump the index serial.
//  - target_content_serial: compared ONLY when the sampled source is itself
//    bound as a target of the draw (true self-feedback). There the ensure
//    body's alias validation genuinely consumes the bound target's serial,
//    which moves on every draw — such entries never hit, which is the
//    fail-open answer, not a defect.
// Frame-scoped (cleared in BeginFableDirectFrame) because per-frame dedup key
// sets and inter-frame resource retirement are not represented in any
// generation — fail open across frames rather than reason about them.


#endif


// GPU-rendered content queued to be copied into a replacement texture after
// a tracked image was recreated. Xbox physical memory / EDRAM contents
// survive D3D descriptor recreation, so when the game re-describes a render
// target (format 20<->75 flips, mip count changes) the pixels must carry
// over or downstream samples read an uninitialized texture.

// Per-compose memo over RecordDirectClearCommand's framebuffer resolution
// (EnsurePrivateRectSetupClearSurfacesLocked + ResolveDirectFramebufferLocked
// + the failed-resolve sync retry): ~870us per clear at 96 clears/compose
// (census recordsplit clear=83.3ms) against only a handful of distinct
// (targets, flags) configurations. The callees are shared with the draw path,
// so the memo lives at the CLEAR call site only.
//
// Equivalence generation (narrowed 2026-08-01, run 490: the original set
// carried resource_serial + four registry content serials, all of which panel
// redraws bump every menu compose — clearmemo=110/554, forcing ~554 full
// re-resolutions per compose). The memo's product is ONLY the resolved
// DirectFramebufferBinding: pointers to tracked textures/views, padding, and
// a cache framebuffer. That binding changes exactly when the STRUCTURE of the
// resource set changes, never on content uploads, so validity is:
//  - framebuffer_invalidation_generation: bumped at every site that retires
//    or drops a framebuffer or a tracked texture/view a binding could
//    reference (RetireFableFramebuffersLocked, RetirePlumeResourceLocked's
//    texture branch — which every promotion/recreate funnels through — and
//    the raw framebuffers.clear() sites). This is what actually covers the
//    old serials' correctness duty: promotions re-register identities/links,
//    but only because they retire+recreate the texture, which bumps here.
//  - record_less_addresses: RUN-494 narrowing. The old validity compared the
//    GLOBAL resource_creations/resource_destroys counters, which churn every
//    frame at transitions (new panel headers), voiding the whole memo at the
//    exact shape it exists for (run 494 attract burst: 418 full resolutions
//    against 262 hits in one compose). A creation can only CHANGE this
//    memoized resolution at a requested address that had NO tracked record
//    when the binding was stored (those resolved to padding); so the entry
//    stores that (<=5 address) set and validity checks whether a record has
//    since appeared at one of them. Destroys are covered by the invalidation
//    generation for anything the binding references; destroying a
//    texture-less record at a requested address leaves the slot padded
//    either way, and its later recreate heals at the promotion/retire bump —
//    the same healing point as the documented readiness-flip staleness.
// Known bounded staleness (accepted): an EXISTING tracked resource that gains
// its first texture mid-compose (readiness flip with no create/retire event)
// does not move this set, so a memoized padding-substituted binding can stay
// padded for the remainder of the compose. Negative resolutions are never
// memoized, the memo is frame-scoped, and the next compose re-resolves.
// Commands carrying the synthetic private-rect copy/depth shaders are NOT
// memoized (fail open): only for them do the skipped callees perform
// per-command side-effect registration
// (RememberMaterializedPrivateRectTargetSourcesLocked and the depth-target
// edge branch). Cleared where the drawphase accumulators reset (frame begin),
// so a stored framebuffer pointer never outlives the retire-epoch guarantee.


FableDirectRendererState g_direct_renderer;

// Resets every `logged_*` diagnostic counter in one place (renderer plan,
// "First cleanup target"). These counters exist only to cap log spam -- each
// one gates an `if (counter < N)` guard -- so they carry no rendering
// behavior and resetting them together is behavior-identical for the frame
// path.
//
// This exists because the three reset sites had drifted badly: Init reset 52
// of the 104 counters, Shutdown 40 (a strict subset of Init's), and
// EnsureDirectDrawResourcesLocked 10, one of which Init never reset at all.
// The 52 counters Init missed kept their exhausted log caps across a device
// re-init, which silently suppressed exactly the diagnostics (private-rect
// setup clears, surface-alias hazard copies, resolve-copy failures, title
// texture binding probes) most likely to be wanted on the run after a
// swapchain rebuild. One list, called from every reset site, removes that
// failure mode.
void ResetDirectDiagnosticsLocked() { g_direct_renderer.diagnostics = {}; }

// Releases the held present/video textures and clears present bookkeeping.
// The hold textures are device allocations, so this must run before the
// device/VMA allocator is destroyed or VMA asserts on leaked blocks at exit.
// DirectPresentTrackingState declares each view before its texture, so this
// assignment releases views before the images they reference.
void ResetDirectPresentTrackingLocked() { g_direct_renderer.present = {}; }

#if REFII_RENDERER_HAS_PLUME
#endif

// ---------------------------------------------------------------------------
// Fable full-screen postprocess pixel shaders (Phase 2, 2026-07-26)
// ---------------------------------------------------------------------------
// These three bytecode hashes replace the pixel-shader OBJECT addresses that
// the scene_private_copy_ps and texture_slot_mask_slot0_ps gates used to test.
//
// VERIFIED, guest side (IDA): D3DDevice_CreatePixelShader (0x82B992B0) reads
// the shader container's +4 (payload offset) and +8 (payload size), allocates
// the D3DPixelShader through the D3D object allocator (0x8222C9D0 ->
// Render_AllocD3DObject_INFERRED 0x8221F258) and memcpy's both the container
// and the microcode into freshly allocated blocks. The object pointer that
// D3DDevice_SetPixelShader (0x82208BB0) then parks in the device is therefore a
// pool allocation with no content identity at all - the payload it copies is
// the only stable identity the guest has. That payload is exactly what Refii
// FNV-1a-64 hashes (PopulateShaderRecordFrom* reads the same +4/+8 container
// shape the guest constructor does), so the hash is the run-stable spelling of
// what these gates were reaching for.
//
// VERIFIED, run 288 log ("plume direct shader linked"), and independently by
// re-hashing the extracted payloads under assets/unpacked/shaders/blobs:
//   0x4215A710 -> 0xDBF69BA3792D00A9 -> 0170_pshader_postprocessing_radialblur
//   0x4216D090 -> 0x824604B7737F75AF -> 0160_pshader_postprocessing_downscale2
//   0x42220410 -> 0x940E12CB0ED381C2 -> 0161_pshader_postprocessing_downscale4
// Each hash had exactly ONE object address in that run, so the hash form is not
// a widening of the address form on title content.
//
// NAMING DEFECT (not fixed here - fable_title_constants.h is out of scope for
// this change): the three constants aliased below carry PRE-RENAME names. The
// 2026-07-26 payloadId fix corrected 563/588 blob names but the constant names
// (and the stale "0170_shader_postprocessing_gaussblur" style comments in
// fable_texture_slot_masks.cpp / fable_title_classification.cpp) still say
// gaussblur / screenfacingatlassprite / screenfacingsprite. The VALUES are
// right, the names are wrong. The blob-index prefixes in those old comments
// (0170/0160/0160-ish) are the part that survived correctly.
//
// SEMANTICS: all three are full-screen postprocess passes. Their translated
// HLSL declares a single named sampler (g_TextureSampler) bound at shared
// constant offset 0, i.e. guest texture slot 0, and nothing else - which is
// precisely the "collapse the pixel texture slot mask to slot 0" behaviour the
// texture_slot_mask_slot0_ps gate hardcoded, and the "this draw copies a scene
// private texture into a target" shape scene_private_copy_ps records.

// True when `pixel_shader_hash` is one of the two single-source postprocess
// passes whose draws bind only guest texture slot 0.
bool IsFableSingleSourcePostprocessPixelShaderHash(uint64_t pixel_shader_hash) {
  return pixel_shader_hash == kFablePostprocessRadialBlurPixelHash ||
         pixel_shader_hash == kFablePostprocessDownscale2PixelHash;
}

// The scene private-copy family adds the downscale4 sibling: same single-source
// full-screen postprocess shape, one more step down the same chain.
bool IsFableScenePrivateCopyPixelShaderHash(uint64_t pixel_shader_hash) {
  return IsFableSingleSourcePostprocessPixelShaderHash(pixel_shader_hash) ||
         pixel_shader_hash == kFablePostprocessDownscale4SiblingPixelHash;
}

// ---------------------------------------------------------------------------
// Hardcoded guest-address gate census (Phase 1, 2026-07-26)
// ---------------------------------------------------------------------------
// Every remaining hardcoded 0x4xxxxxxx guest-heap literal in this file was
// captured from ONE title-screen run. Guest heap addresses are POOL-ALLOCATED
// and vary per run — the storybook panel atlas was 0x12CFD000 in runs 264-269
// and 0x12D33000 in run 271 for identical content — so these literals were
// already only firing by luck, matching a subset of runs. At gameplay every
// one of them silently stops matching: no log, no counter, and whatever
// correctness they supply turns off invisibly. That is what makes gameplay
// undiagnosable — a real defect and a title gate that quietly stopped firing
// look identical.
//
// This census makes each gate observable BEFORE anything is replaced:
//   tested   — how often control reached the comparison
//   matched  — how often the hardcoded literal actually answered yes
//   derived  — of those matches, how many a DERIVED discriminator available at
//              the same site (shader hash, declaration elements hash, existing
//              structural predicate) would have covered on its own
//   lit_only — matches the derived term did NOT cover; a gate whose lit_only
//              stays 0 across a run is safe to re-express structurally
//   key      — the derived key observed at the last match (shader hash, decl
//              hash, fetch base ...). This is the raw material for Phase 2:
//              it names what the address actually IS, per run, automatically.
//
// Reporting is throttled PER GATE, not by a flat log cap: a flat cap fills
// with the first hot always-passing gate before anything interesting runs.
// Each gate reports on its first few tests, on its first match, then at
// exponentially spaced test counts. A full table is dumped periodically from
// the frame-begin reset so a run's tail shows which gates are load-bearing and
// which are already dead (tested > 0, matched == 0).
//
// Off switch is an ENV VAR, deliberately not a cvar: a cvar gate failed to
// hold on 2026-07-25 because the key was not registered in the launcher's
// GetRuntimeCVarDefinitions. REFII_RENDERER_ADDRESS_GATE_CENSUS=0 silences the
// logging; the counters themselves are always maintained (relaxed atomics, so
// the sites that run outside g_direct_renderer.mutex stay well-defined).


DirectAddressGateCensusSlot g_direct_address_gate_census[kDirectAddressGateCount];

bool DirectAddressGateCensusLoggingEnabled() {
  // OPT-IN since 2026-07-28. This was opt-OUT (unset env => enabled), so the
  // census ran on every one of the 40 DirectAddressGateTest sites per draw in
  // every default run. The gates it audits have all been decided; set
  // REFII_RENDERER_ADDRESS_GATE_CENSUS=1 to re-arm when adding a new gate.
  static const bool enabled = [] {
    const char* value = std::getenv("REFII_RENDERER_ADDRESS_GATE_CENSUS");
    return value != nullptr && value[0] != '\0' && value[0] != '0';
  }();
  return enabled;
}

// Per-gate throttle: the first four tests, then powers of two, then every
// 4096th. A hot always-passing gate settles into near-silence on its own
// without ever starving a rare gate of its first report.
bool DirectAddressGateShouldReport(uint64_t tested) {
  if (tested <= 4u) {
    return true;
  }
  if (tested < 4096u) {
    return (tested & (tested - 1u)) == 0u;
  }
  return (tested % 4096u) == 0u;
}

// `literal_hit` is the hardcoded comparison's own answer and is returned
// unchanged — instrumenting a gate must never change what it decides.
// `derived_hit` is an independent discriminator available at the same site
// (already-computed hash, already-evaluated structural predicate); pass false
// when no derived term exists yet, and the gate's lit_only column then simply
// equals its match count, which is exactly the "not yet derivable" signal.
bool DirectAddressGateTest(DirectAddressGate gate, bool literal_hit,
                           uint32_t observed_address,
                           uint64_t derived_key, bool derived_hit) {
  DirectAddressGateCensusSlot& slot =
      g_direct_address_gate_census[static_cast<size_t>(gate)];
  const uint64_t tested =
      slot.tested.fetch_add(1u, std::memory_order_relaxed) + 1u;
  uint64_t matched = slot.matched.load(std::memory_order_relaxed);
  if (literal_hit) {
    matched = slot.matched.fetch_add(1u, std::memory_order_relaxed) + 1u;
    if (derived_hit) {
      slot.derived_covered.fetch_add(1u, std::memory_order_relaxed);
    } else {
      slot.literal_only.fetch_add(1u, std::memory_order_relaxed);
    }
    slot.observed_address.store(observed_address, std::memory_order_relaxed);
    slot.observed_key.store(derived_key, std::memory_order_relaxed);
    slot.last_match_frame.store(g_direct_renderer.current_frame_index,
                                std::memory_order_relaxed);
  }
  if (DirectAddressGateCensusLoggingEnabled() &&
      ((literal_hit && matched == 1u) ||
       DirectAddressGateShouldReport(tested))) {
    const DirectAddressGateDescriptor& descriptor =
        kDirectAddressGateDescriptors[static_cast<size_t>(gate)];
    REXLOG_INFO(
        "Refii addrgate {} frame={} tested={} matched={} derived={} "
        "lit_only={} addr=0x{:08X} key=0x{:016X} literals=[{}] gates=[{}]",
        descriptor.name, g_direct_renderer.current_frame_index,
        static_cast<unsigned long long>(tested),
        static_cast<unsigned long long>(matched),
        static_cast<unsigned long long>(
            slot.derived_covered.load(std::memory_order_relaxed)),
        static_cast<unsigned long long>(
            slot.literal_only.load(std::memory_order_relaxed)),
        slot.observed_address.load(std::memory_order_relaxed),
        static_cast<unsigned long long>(
            slot.observed_key.load(std::memory_order_relaxed)),
        descriptor.literals, descriptor.gates);
  }
  return literal_hit;
}

void ReportDirectAddressGateCensus(const char* reason) {
  if (!DirectAddressGateCensusLoggingEnabled()) {
    return;
  }
  std::string dead;
  std::string never_tested;
  for (size_t index = 0; index < kDirectAddressGateCount; ++index) {
    const DirectAddressGateCensusSlot& slot = g_direct_address_gate_census[index];
    const DirectAddressGateDescriptor& descriptor =
        kDirectAddressGateDescriptors[index];
    const uint64_t tested = slot.tested.load(std::memory_order_relaxed);
    const uint64_t matched = slot.matched.load(std::memory_order_relaxed);
    if (tested == 0u) {
      never_tested.append(never_tested.empty() ? "" : ",");
      never_tested.append(descriptor.name);
      continue;
    }
    if (matched == 0u) {
      dead.append(dead.empty() ? "" : ",");
      dead.append(descriptor.name);
      continue;
    }
    REXLOG_INFO(
        "Refii addrgate census[{}] {} tested={} matched={} derived={} "
        "lit_only={} last_match_frame={} addr=0x{:08X} key=0x{:016X} "
        "literals=[{}] gates=[{}]",
        reason, descriptor.name,
        static_cast<unsigned long long>(tested),
        static_cast<unsigned long long>(matched),
        static_cast<unsigned long long>(
            slot.derived_covered.load(std::memory_order_relaxed)),
        static_cast<unsigned long long>(
            slot.literal_only.load(std::memory_order_relaxed)),
        slot.last_match_frame.load(std::memory_order_relaxed),
        slot.observed_address.load(std::memory_order_relaxed),
        static_cast<unsigned long long>(
            slot.observed_key.load(std::memory_order_relaxed)),
        descriptor.literals, descriptor.gates);
  }
  REXLOG_INFO(
      "Refii addrgate census[{}] frame={} tested_but_never_matched=[{}] "
      "never_tested=[{}]",
      reason, g_direct_renderer.current_frame_index,
      dead.empty() ? "none" : dead.c_str(),
      never_tested.empty() ? "none" : never_tested.c_str());
}

// Arms a RenderDoc capture of the next frame when a GTL defect signal
// fires (a divergence, a channel miss, an order inversion) — the renderer
// knows the exact moment a draw goes wrong, so it captures its own
// evidence instead of waiting for a manual F12. Same gate as the
// final-failed-command captures (renderer_renderdoc_capture_on_error);
// per-class caps keep disk bounded across a long run.
bool RequestGtlDefectCaptureLocked(const char* defect_class,
                                   uint32_t& fired_count, uint32_t cap) {
  if (fired_count >= cap || !RenderDocCaptureOnErrorEnabled()) {
    return false;
  }
  char reason[96] = {};
  std::snprintf(reason, sizeof(reason), "gtl_%s_frame_%u", defect_class,
                g_direct_renderer.current_frame_index);
  if (!RequestRenderDocFrameCaptureWithReason(reason)) {
    return false;
  }
  ++fired_count;
  REXLOG_WARN(
      "Refii plume RenderDoc defect capture armed class={} #{} frame={}",
      defect_class, fired_count, g_direct_renderer.current_frame_index);
  return true;
}

void ResetDirectRenderGraphExecutionSnapshotLocked(uint32_t frame_index) {
  g_direct_renderer.render_graph_graph_owned_render_steps = 0;
  g_direct_renderer.render_graph_graph_owned_clear_steps = 0;
  g_direct_renderer.render_graph_graph_owned_clear_commands = 0;
  g_direct_renderer.render_graph_graph_owned_draw_steps = 0;
  g_direct_renderer.render_graph_graph_owned_draw_commands = 0;
  g_direct_renderer.render_graph_graph_owned_resolve_steps = 0;
  g_direct_renderer.render_graph_graph_owned_resolve_commands = 0;
  g_direct_renderer.packet_queue_replayed_surface_destination_actions = 0;
  g_direct_renderer.packet_queue_replayed_present_blit_actions = 0;
  g_direct_renderer.render_graph_bridge_steps = 0;
  g_direct_renderer.render_graph_bridge_commands = 0;
  g_direct_renderer.current_frame_index = frame_index;
  // Address-gate census: a full table early (so a short run still produces
  // one) and then periodically, so a run's tail names every gate that is
  // load-bearing, every gate that is tested-but-dead, and every gate that was
  // never reached at all.
  static bool reported_early_address_gate_census = false;
  static uint32_t last_periodic_address_gate_census = 0;
  if (frame_index >= 240u && !reported_early_address_gate_census) {
    reported_early_address_gate_census = true;
    ReportDirectAddressGateCensus("early");
  } else if (frame_index >= last_periodic_address_gate_census + 1800u) {
    last_periodic_address_gate_census = frame_index;
    ReportDirectAddressGateCensus("periodic");
  }
}

#if REFII_RENDERER_HAS_PLUME
bool DecodeDirectSyntheticGeneratedShaderId(uint32_t shader_id,
                                            uint32_t& generated_id) {
  if ((shader_id & 0xFF000000u) != 0x0E000000u) {
    return false;
  }
  const uint32_t encoded = shader_id & 0xFFFFu;
  uint32_t decoded = 0;
  for (int shift = 12; shift >= 0; shift -= 4) {
    const uint32_t digit = (encoded >> shift) & 0xFu;
    if (digit > 9u) {
      return false;
    }
    decoded = decoded * 10u + digit;
  }
  if (decoded == 0) {
    return false;
  }
  generated_id = decoded;
  return true;
}

std::string DirectResourceMarkerNameLocked(uint32_t address) {
  const PlumeTrackedResource* resource = FindDirectResourceLocked(address);
  if (resource != nullptr) {
    if (!resource->debug_summary.empty()) {
      return resource->debug_summary;
    }
    if (!resource->guest_shader.name.empty()) {
      return resource->guest_shader.name;
    }
    if (!resource->shader_name.empty()) {
      return resource->shader_name;
    }
  }
  uint32_t generated_id = 0;
  RendererShaderLibraryEntry shader_entry;
  if (DecodeDirectSyntheticGeneratedShaderId(address, generated_id) &&
      FindRendererShaderLibraryEntryById(generated_id, shader_entry)) {
    return shader_entry.name;
  }
  return {};
}

void AppendDirectMarkerName(std::string& label, std::string_view prefix,
                            std::string_view name) {
  if (name.empty()) {
    return;
  }
  constexpr size_t kMaxMarkerName = 64;
  label.append(" ");
  label.append(prefix);
  label.append(name.substr(0, kMaxMarkerName));
}

// Is anything actually consuming the per-command debug labels?
//
// GATED ON CAPTURE TOOLING (2026-08-01). The labels' only command-stream
// consumer is vkCmd*DebugUtilsLabelEXT (RenderDoc captures); when
// librenderdoc is not loaded, nothing reads them, and the run-49x census
// audit flagged the per-command label work (two label pairs plus a snprintf
// per command, ~4 heap-allocating std::strings and two std::map lookups per
// draw) as a cheap win never implemented. So: default = ON exactly when the
// RenderDoc API is loaded (any capture-tooling run keeps full labels),
// REFII_RENDERER_DEBUG_LABELS=1 forces on regardless, =0 forces off (the
// original A/B lever, unchanged).
//
// Switching off is safe at every consumer, which is why the gate is sound:
//   - plume's beginDebugLabel/endDebugLabel take a name and, at most, hand it
//     to vkCmd*DebugUtilsLabelEXT (plume_vulkan.cpp:3060). No state, no
//     ordering, nothing the command stream depends on.
//   - the frame-database JSON dumps (plume_backend.cpp:3868/4073) already
//     branch on `debug_label.empty()` and synthesize a label from the packet.
//   - ZoneText only annotates a Tracy zone; Tracy's ZoneScopedN zones are
//     independent of these labels and stay on.
// Memoized: librenderdoc must be loaded before Vulkan device creation to
// capture at all, so its presence is settled before the first compose asks.
bool DirectDebugLabelsEnabled() {
  static const bool enabled = [] {
    const char* value = std::getenv("REFII_RENDERER_DEBUG_LABELS");
    if (value != nullptr && value[0] != '\0') {
      return value[0] != '0';
    }
    return RenderDocDebuggerAvailable();
  }();
  return enabled;
}

std::string BuildDirectDrawDebugLabelLocked(
    const FableDirectRenderCommand& command,
    uint32_t draw_count,
    bool synthetic_quad_list,
    std::string_view pass_name) {
  if (!DirectDebugLabelsEnabled()) {
    return std::string();
  }
  const uint64_t draw_serial =
      command.draw_serial != 0 ? command.draw_serial : command.serial;
  char label[256] = {};
  std::snprintf(
      label, sizeof(label),
      "RefiiRenderer::SubmitDrawPacket Frame %u Draw %llu Cmd %llu "
      "Pass_%.*s Source %u VS_%08X PS_%08X RT_%08X DS_%08X Prim_%u "
      "Count_%u Indexed_%u QuadList_%u",
      g_direct_renderer.current_frame_index,
      static_cast<unsigned long long>(draw_serial),
      static_cast<unsigned long long>(command.serial),
      static_cast<int>(pass_name.size()), pass_name.data(),
      static_cast<uint32_t>(command.draw.source),
      command.resources.vertex_shader, command.resources.pixel_shader,
      command.resources.render_targets[0], command.resources.depth_stencil,
      command.draw.draw.primitive_type, draw_count,
      command.draw.draw.indexed ? 1u : 0u, synthetic_quad_list ? 1u : 0u);
  std::string marker = label;
  AppendDirectMarkerName(
      marker, "VSName_",
      DirectResourceMarkerNameLocked(command.resources.vertex_shader));
  AppendDirectMarkerName(
      marker, "PSName_",
      DirectResourceMarkerNameLocked(command.resources.pixel_shader));
  return marker;
}


std::string BuildDirectPassDebugLabelLocked(std::string_view pass_name) {
  if (!DirectDebugLabelsEnabled()) {
    return std::string();
  }
  char label[128] = {};
  std::snprintf(label, sizeof(label), "Pass: %.*s Frame %u",
                static_cast<int>(pass_name.size()), pass_name.data(),
                g_direct_renderer.current_frame_index);
  return label;
}

std::string NormalizeDirectRenderPassName(std::string_view pass_name) {
  std::string normalized;
  normalized.reserve(pass_name.size());
  for (unsigned char ch : pass_name) {
    if (std::isalnum(ch) != 0) {
      normalized.push_back(static_cast<char>(std::tolower(ch)));
    }
  }
  return normalized;
}

void MarkDirectPrivateTextureAliasStateChangedLocked() {
  ++g_direct_renderer.private_texture_alias_serial;
}

void ResetDirectPrivateFetchAliasFrameCountersLocked() {
  g_direct_renderer.frame_private_fetch_identity_aliases = 0;
  g_direct_renderer.frame_private_copy_exact_matches = 0;
}
#endif


uint32_t ClampCounter(uint64_t value) {
  return static_cast<uint32_t>(
      std::min<uint64_t>(value, static_cast<uint64_t>(UINT32_MAX)));
}

void RefreshDirectStatsFromHooksLocked(
    uint32_t frame_index, bool force_hook_snapshot,
    const FableDirectStateSnapshot* direct_snapshot_override) {
  const FableDirectStateSnapshot direct_snapshot =
      direct_snapshot_override != nullptr ? *direct_snapshot_override
                                          : GetFableDirectStateSnapshot();
  constexpr uint32_t kHookStatsRefreshPeriodFrames = 30;
  const bool refresh_hook_snapshot =
      force_hook_snapshot || !g_direct_renderer.have_cached_hook_stats ||
      frame_index < g_direct_renderer.last_hook_stats_frame ||
      frame_index - g_direct_renderer.last_hook_stats_frame >=
          kHookStatsRefreshPeriodFrames;
  if (refresh_hook_snapshot) {
    g_direct_renderer.cached_hook_stats = GetRendererHookObserverSnapshot();
    g_direct_renderer.have_cached_hook_stats = true;
    g_direct_renderer.last_hook_stats_frame = frame_index;
  }
  const RendererHookObserverSnapshot& hook_snapshot =
      g_direct_renderer.cached_hook_stats;
  g_direct_renderer.stats.last_frame_index = frame_index;
  g_direct_renderer.stats.draw_count = ClampCounter(direct_snapshot.draw_count);
  g_direct_renderer.stats.indexed_draw_count =
      ClampCounter(direct_snapshot.indexed_draw_count);
  g_direct_renderer.stats.last_render_commands =
      ClampCounter(direct_snapshot.render_command_count);
  g_direct_renderer.stats.last_draw_events =
      ClampCounter(direct_snapshot.draw_event_count);
  g_direct_renderer.stats.resource_creations =
      ClampCounter(g_direct_renderer.resource_creations != 0
                       ? g_direct_renderer.resource_creations
                       : hook_snapshot.resource_creates);
  g_direct_renderer.stats.resource_destroys =
      ClampCounter(g_direct_renderer.resource_destroys != 0
                       ? g_direct_renderer.resource_destroys
                       : hook_snapshot.resource_destroys);
  g_direct_renderer.stats.resource_uploads =
      ClampCounter(g_direct_renderer.resource_uploads);
  g_direct_renderer.stats.live_resources =
      ClampCounter(hook_snapshot.live_resources);
  g_direct_renderer.stats.live_textures = ClampCounter(hook_snapshot.live_textures);
  g_direct_renderer.stats.live_surfaces = ClampCounter(hook_snapshot.live_surfaces);
  g_direct_renderer.stats.live_vertex_buffers =
      ClampCounter(hook_snapshot.live_vertex_buffers);
  g_direct_renderer.stats.live_index_buffers =
      ClampCounter(hook_snapshot.live_index_buffers);
  g_direct_renderer.stats.live_shaders = ClampCounter(hook_snapshot.live_shaders);
  g_direct_renderer.stats.failed_resource_syncs =
      ClampCounter(g_direct_renderer.failed_resource_syncs);
  const uint64_t render_target_switch_count =
      direct_snapshot.render_target_switch_count;
  g_direct_renderer.stats.render_target_switches =
      render_target_switch_count >=
              g_direct_renderer.last_render_target_switch_count
          ? ClampCounter(render_target_switch_count -
                         g_direct_renderer.last_render_target_switch_count)
          : ClampCounter(render_target_switch_count);
  g_direct_renderer.last_render_target_switch_count =
      render_target_switch_count;
  const uint64_t depth_target_switch_count =
      direct_snapshot.depth_target_switch_count;
  g_direct_renderer.stats.depth_target_switches =
      depth_target_switch_count >= g_direct_renderer.last_depth_target_switch_count
          ? ClampCounter(depth_target_switch_count -
                         g_direct_renderer.last_depth_target_switch_count)
          : ClampCounter(depth_target_switch_count);
  g_direct_renderer.last_depth_target_switch_count =
      depth_target_switch_count;
  g_direct_renderer.stats.last_resource_address =
      hook_snapshot.last_resource_address;
  g_direct_renderer.stats.last_resource_size = hook_snapshot.last_resource_size;
  if (g_direct_renderer.stats.last_present_target == 0) {
    g_direct_renderer.stats.last_present_target =
        direct_snapshot.targets.color[0] != 0 ? direct_snapshot.targets.color[0]
                                              : hook_snapshot.render_targets[0];
  }
  if (g_direct_renderer.stats.last_present_width == 0 &&
      g_direct_renderer.stats.last_present_height == 0 &&
      hook_snapshot.display_present_width != 0 &&
      hook_snapshot.display_present_height != 0) {
    g_direct_renderer.stats.last_present_width =
        hook_snapshot.display_present_width;
    g_direct_renderer.stats.last_present_height =
        hook_snapshot.display_present_height;
  }
}

#if REFII_RENDERER_HAS_PLUME
bool DirectCommandUsesTextureSlotForSyncLocked(
    const FableDirectRenderCommand& command, uint32_t texture_slot_mask,
    uint32_t slot) {
  if (slot < 32u && (texture_slot_mask & (1u << slot)) != 0) {
    return true;
  }
  if (command.draw.source != RendererDrawEventSource::kPrivateTextureRectangle ||
      !command.draw.private_texture_rectangle ||
      !IsFableSyntheticPrivateRectCopyShader(command.resources.pixel_shader)) {
    return false;
  }

  if (texture_slot_mask != 0u) {
    return false;
  }

  const uint32_t explicit_private_slot =
      command.draw.private_texture_slot & (kMaxFableTextureSlots - 1u);
  return slot == kFablePrivateRectCopyTextureSlot ||
         slot == explicit_private_slot;
}


bool DirectSurfaceTextureLinkExactLocalWrapperVisibleToCommandLocked(
    const FableDirectRenderCommand& command,
    const FableSurfaceTextureLink& link) {
  if (link.copy_symbol_address != kFablePrivateRectTitleLocalWrapperCallsite ||
      command.draw.source == RendererDrawEventSource::kPixVideo) {
    return true;
  }
  return DirectSurfaceTextureLinkExactWrapperOwnedByCommand(command, link) ||
         DirectSurfaceTextureLinkIsCurrentTargetExactEdgeLocked(command, link);
}

bool DirectSurfaceTextureLinkPrivateWrapperVisibleToCommandLocked(
    const FableDirectRenderCommand& command,
    const FableSurfaceTextureLink& link) {
  if (!DirectSurfaceTextureLinkExactLocalWrapperVisibleToCommandLocked(command,
                                                                       link)) {
    return false;
  }
  if (!DirectSurfaceTextureLinkIsPrivateTitleWrapperRoot(link) ||
      DirectSurfaceTextureLinkWrapperOwnedByCommand(command, link) ||
      DirectSurfaceTextureLinkIsCurrentTargetExactEdgeLocked(command,
                                                                   link)) {
    return true;
  }

  const bool begin_vertices_title_command =
      command.draw.private_texture_wrapper_symbol_address ==
          kFableBeginVerticesTitleWrapperSymbol &&
      (command.draw.private_texture_wrapper_call_serial != 0 ||
       command.draw.private_texture_wrapper_context_key != 0);
  const bool d3d_indexed_title_command =
      command.draw.source == RendererDrawEventSource::kSceneObject &&
      command.draw.private_texture_wrapper_symbol_address ==
          kFableD3DBeginIndexedVerticesWrapperSymbol &&
      (command.draw.private_texture_wrapper_call_serial != 0 ||
       command.draw.private_texture_wrapper_context_key != 0);
  const bool sibling_title_link =
      link.copy_symbol_address == kFablePrivateRectSiblingWrapperSymbol ||
      link.copy_symbol_address == kFablePrivateRectSiblingWrapperCallsite;
  const bool begin_vertices_title_link =
      link.copy_symbol_address == kFableBeginVerticesTitleWrapperSymbol;
  const uint32_t command_wrapper_source =
      command.draw.private_texture_wrapper_source_texture;
  const bool source_identity_required =
      command_wrapper_source != 0 || link.wrapper_source_texture != 0;
  const bool wrapper_source_matches =
      !source_identity_required ||
      (d3d_indexed_title_command && sibling_title_link) ||
      (command_wrapper_source != 0 && link.wrapper_source_texture != 0 &&
       DirectResourceAddressMatchesLocked(command_wrapper_source,
                                          link.wrapper_source_texture));
  return (begin_vertices_title_command ||
          (d3d_indexed_title_command && sibling_title_link)) &&
         (sibling_title_link || begin_vertices_title_link) &&
         wrapper_source_matches &&
         DirectSurfaceTextureLinkTouchesFablePrivateChain(link, nullptr);
}

bool DirectSurfaceTextureLinkHasExactWrapperProducerSurfaceIdentity(
    const FableSurfaceTextureLink& link) {
  if (!DirectSurfaceTextureLinkIsPrivateTitleWrapperRoot(link) ||
      link.source_surface == 0 || link.target_content_serial == 0 ||
      link.kind == FablePrivateTextureCopyKind::kDepth ||
      (link.copy_call_serial == 0 && link.copy_context_key == 0 &&
       link.copy_symbol_address == 0)) {
    return false;
  }
  if (link.texture == 0 && link.texture_base_address == 0 &&
      FableDirectTextureFetchKey(link.texture_fetch_dwords) == 0) {
    return false;
  }
  if (link.sampled_source.kind != FableRegisteredSampleSourceKind::kGpuSurface) {
    return false;
  }
  if (link.sampled_source.resource != 0 &&
      !DirectResourceAddressMatchesLocked(link.sampled_source.resource,
                                         link.source_surface)) {
    return false;
  }
  return link.sampled_source.content_serial == 0 ||
         link.sampled_source.content_serial <= link.target_content_serial;
}

void PopulateDirectPrivateTextureWrapperSourceIdentity(
    FablePrivateTextureCopyAction& action,
    const FableDirectRenderCommand& command,
    const FableSurfaceTextureLink* link) {
  if (command.draw.private_texture_wrapper_source_texture != 0) {
    action.wrapper_source_texture =
        command.draw.private_texture_wrapper_source_texture;
    action.wrapper_source_exact =
        command.draw.private_texture_wrapper_source_exact;
    return;
  }

  if (link != nullptr && link->wrapper_source_texture != 0) {
    action.wrapper_source_texture = link->wrapper_source_texture;
    action.wrapper_source_exact = link->wrapper_source_exact;
    return;
  }

  action.wrapper_source_texture = action.source_texture;
  action.wrapper_source_exact = action.wrapper_source_texture != 0;
}

bool DirectSurfaceTextureLinkHasCommandConsumableSampleIdentity(
    const FableSurfaceTextureLink& link) {
  const bool has_backing_identity =
      link.backing_content_serial != 0 || link.backing_texture != 0 ||
      link.backing_base_address != 0 ||
      FableDirectTextureFetchKey(link.backing_fetch_dwords) != 0;
  const bool sampled_producer_surface =
      link.sampled_source.kind == FableRegisteredSampleSourceKind::kGpuSurface &&
      link.sampled_source.resource != 0 && link.source_surface != 0 &&
      DirectResourceAddressMatchesLocked(link.sampled_source.resource,
                                         link.source_surface);
  bool has_sample_identity = false;
  switch (link.sampled_source.kind) {
    case FableRegisteredSampleSourceKind::kBackingTexture:
    case FableRegisteredSampleSourceKind::kGuestTexture:
      has_sample_identity =
          link.sampled_source.resource != 0 ||
          link.sampled_source.base_address != 0 ||
          FableDirectTextureFetchKey(link.sampled_source.fetch_dwords) != 0 ||
          link.sampled_source.content_serial != 0;
      break;
    case FableRegisteredSampleSourceKind::kGpuSurface:
      has_sample_identity = link.sampled_source.resource != 0 &&
                            link.sampled_source.content_serial != 0 &&
                            !sampled_producer_surface;
      break;
    case FableRegisteredSampleSourceKind::kUnknown:
    default:
      break;
  }
  if (DirectSurfaceTextureLinkRequiresExactWrapperSourceForMutableHeaderResolve(
          link)) {
    return has_backing_identity || has_sample_identity ||
           DirectSurfaceTextureLinkHasExactWrapperProducerSurfaceIdentity(link);
  }
  return link.target_content_serial != 0 || has_backing_identity ||
         has_sample_identity;
}

bool DirectSurfaceTextureLinkHasExactCopyPayloadIdentity(
    const FableSurfaceTextureLink& link) {
  const bool has_backing_identity =
      link.backing_content_serial != 0 || link.backing_texture != 0 ||
      link.backing_base_address != 0 ||
      FableDirectTextureFetchKey(link.backing_fetch_dwords) != 0;
  const bool sampled_producer_surface =
      link.sampled_source.kind == FableRegisteredSampleSourceKind::kGpuSurface &&
      link.sampled_source.resource != 0 && link.source_surface != 0 &&
      DirectResourceAddressMatchesLocked(link.sampled_source.resource,
                                         link.source_surface);
  const bool has_sample_identity =
      link.sampled_source.kind == FableRegisteredSampleSourceKind::kBackingTexture ||
      link.sampled_source.kind == FableRegisteredSampleSourceKind::kGuestTexture ||
      (link.sampled_source.kind == FableRegisteredSampleSourceKind::kGpuSurface &&
       !sampled_producer_surface);
  const bool has_sample_address =
      has_sample_identity &&
      (link.sampled_source.resource != 0 ||
       link.sampled_source.base_address != 0 ||
       FableDirectTextureFetchKey(link.sampled_source.fetch_dwords) != 0 ||
       link.sampled_source.content_serial != 0);
  if (DirectSurfaceTextureLinkRequiresExactWrapperSourceForMutableHeaderResolve(
          link)) {
    return has_backing_identity || has_sample_address;
  }
  return link.target_content_serial != 0 || has_backing_identity ||
         has_sample_address;
}

FableGuestResourceUploadCacheEntry UploadCacheEntryForSnapshot(
    const FableGuestResourceSnapshot& snapshot) {
  FableGuestResourceUploadCacheEntry entry;
  entry.kind = snapshot.kind;
  entry.upload_generation = snapshot.upload_generation;
  switch (snapshot.kind) {
    case FableGuestResourceKind::kVertexBuffer:
    case FableGuestResourceKind::kIndexBuffer:
      entry.data_address = snapshot.buffer.gpu_address;
      entry.byte_size = snapshot.buffer.byte_size;
      entry.meta = snapshot.buffer.stride_or_index_format;
      entry.hash = snapshot.buffer.upload_hash;
      break;
    case FableGuestResourceKind::kTexture:
      entry.data_address = snapshot.texture.base_address;
      entry.byte_size = snapshot.texture.byte_size;
      entry.meta = snapshot.texture.raw_fetch_dwords[0];
      entry.hash = snapshot.texture.upload_hash != 0
                       ? snapshot.texture.upload_hash
                       : snapshot.texture.upload_generation;
      break;
    case FableGuestResourceKind::kSurface:
    case FableGuestResourceKind::kDepthSurface:
      entry.data_address = snapshot.surface.parent_address;
      entry.byte_size = snapshot.surface.byte_size;
      entry.meta = snapshot.surface.d3d_format;
      entry.hash = snapshot.serial;
      break;
    case FableGuestResourceKind::kVertexDeclaration:
      entry.data_address = snapshot.vertex_declaration.elements_address;
      entry.byte_size = snapshot.vertex_declaration.elements_size;
      entry.meta = snapshot.vertex_declaration.element_count;
      entry.hash = snapshot.vertex_declaration.elements_hash;
      break;
    case FableGuestResourceKind::kVertexShader:
    case FableGuestResourceKind::kPixelShader:
      entry.data_address = snapshot.shader.bytecode_address;
      entry.byte_size = snapshot.shader.bytecode_size;
      entry.meta = snapshot.shader.bytecode_offset;
      entry.hash = snapshot.shader.bytecode_hash;
      break;
    case FableGuestResourceKind::kUnknown:
      break;
  }
  return entry;
}

bool SameDirectUploadCacheEntry(
    const FableGuestResourceUploadCacheEntry& lhs,
    const FableGuestResourceUploadCacheEntry& rhs) {
  const bool same_upload_generation =
      lhs.upload_generation == rhs.upload_generation ||
      (lhs.hash != 0 && lhs.hash == rhs.hash);
  return lhs.kind == rhs.kind && lhs.data_address == rhs.data_address &&
         lhs.byte_size == rhs.byte_size && lhs.meta == rhs.meta &&
         lhs.hash == rhs.hash && same_upload_generation;
}

void LogDirectResourceUploadSyncLocked(
    const FableGuestResourceSnapshot& snapshot,
    const FableGuestResourceUploadCacheEntry& upload_entry) {
  if (!RendererResourceTracingEnabled() ||
      g_direct_renderer.diagnostics.logged_resource_upload_syncs >= 48u) {
    return;
  }
  ++g_direct_renderer.diagnostics.logged_resource_upload_syncs;
  REXLOG_INFO(
      "Refii plume direct resource upload sync #{} frame={} guest=0x{:08X} "
      "kind={} payload={} gen={} data=0x{:08X} bytes={} meta=0x{:08X} "
      "hash=0x{:016X} serial={} scratch={}",
      g_direct_renderer.diagnostics.logged_resource_upload_syncs,
      g_direct_renderer.current_frame_index, snapshot.guest_address,
      TrackedResourceKindName(snapshot.kind), snapshot.upload_data.size(),
      static_cast<unsigned long long>(snapshot.upload_generation),
      upload_entry.data_address, upload_entry.byte_size, upload_entry.meta,
      static_cast<unsigned long long>(upload_entry.hash), snapshot.serial,
      snapshot.scratch ? 1 : 0);
}

bool DirectAddressInFableD3DDeviceMirror(uint32_t address) {
  constexpr uint32_t kFableD3DDeviceMirrorBase = 0x44134280u;
  constexpr uint32_t kFableD3DDeviceMirrorBytes = 0x1000u;
  return address >= kFableD3DDeviceMirrorBase &&
         address < kFableD3DDeviceMirrorBase + kFableD3DDeviceMirrorBytes;
}

bool DirectImageSnapshotReferencesD3DDeviceMirror(
    const FableGuestResourceSnapshot& snapshot) {
  if (DirectAddressInFableD3DDeviceMirror(snapshot.guest_address) ||
      DirectAddressInFableD3DDeviceMirror(snapshot.parent_address)) {
    return true;
  }

  switch (snapshot.kind) {
    case FableGuestResourceKind::kTexture:
      return DirectAddressInFableD3DDeviceMirror(
                 snapshot.texture.object_address) ||
             DirectAddressInFableD3DDeviceMirror(
                 snapshot.texture.parent_address) ||
             DirectAddressInFableD3DDeviceMirror(snapshot.texture.base_address);
    case FableGuestResourceKind::kSurface:
    case FableGuestResourceKind::kDepthSurface:
      return DirectAddressInFableD3DDeviceMirror(
                 snapshot.surface.object_address) ||
             DirectAddressInFableD3DDeviceMirror(
                 snapshot.surface.parent_address);
    default:
      return false;
  }
}

bool DirectTextureSnapshotHasStorageIdentity(
    const FableGuestResourceSnapshot& snapshot) {
  if (snapshot.kind != FableGuestResourceKind::kTexture) {
    return true;
  }
  return snapshot.texture.base_address != 0 || snapshot.texture.byte_size != 0 ||
         snapshot.texture.upload_hash != 0 || snapshot.upload_generation != 0 ||
         !snapshot.upload_data.empty() ||
         HasTrackedFetchDwords(snapshot.texture.raw_fetch_dwords) ||
         HasTrackedFetchDwords(snapshot.texture.normalized_fetch_dwords);
}

const char* DirectImageSnapshotQuarantineReason(
    const FableGuestResourceSnapshot& snapshot) {
  if (!IsImageResource(snapshot.kind)) {
    return nullptr;
  }
  if (DirectImageSnapshotReferencesD3DDeviceMirror(snapshot)) {
    return "d3d_device_mirror";
  }
  if (!DirectTextureSnapshotHasStorageIdentity(snapshot)) {
    return "texture_without_storage_identity";
  }
  return nullptr;
}

void LogDirectImageSnapshotQuarantineLocked(
    const FableGuestResourceSnapshot& snapshot, const char* reason) {
  static uint32_t logged_image_snapshot_quarantines = 0;
  if (!reason || logged_image_snapshot_quarantines >= 64u) {
    return;
  }
  ++logged_image_snapshot_quarantines;
  REXLOG_WARN(
      "Refii plume direct image snapshot quarantined #{} frame={} "
      "reason={} guest=0x{:08X} parent=0x{:08X} kind={} "
      "tex_object=0x{:08X} tex_parent=0x{:08X} base=0x{:08X} "
      "bytes={} fetch0=0x{:08X} surf_object=0x{:08X} "
      "surf_parent=0x{:08X}",
      logged_image_snapshot_quarantines, g_direct_renderer.current_frame_index,
      reason, snapshot.guest_address, snapshot.parent_address,
      TrackedResourceKindName(snapshot.kind), snapshot.texture.object_address,
      snapshot.texture.parent_address, snapshot.texture.base_address,
      snapshot.texture.byte_size, snapshot.texture.raw_fetch_dwords[0],
      snapshot.surface.object_address, snapshot.surface.parent_address);
}

bool ReadBinaryFile(const std::string& path, std::vector<uint8_t>& bytes) {
  bytes.clear();
  if (path.empty()) {
    return false;
  }
  std::ifstream file(path, std::ios::binary | std::ios::ate);
  if (!file) {
    return false;
  }
  const std::streamsize size = file.tellg();
  if (size <= 0) {
    return false;
  }
  file.seekg(0, std::ios::beg);
  bytes.resize(static_cast<size_t>(size));
  file.read(reinterpret_cast<char*>(bytes.data()), size);
  if (!file) {
    bytes.clear();
    return false;
  }
  return true;
}


bool CreateNullTextureLocked(plume::RenderDevice* device, uint32_t set_index,
                             plume::RenderTextureDimension dimension,
                             plume::RenderTextureViewDimension view_dimension,
                             plume::RenderTextureFlags flags,
                             uint32_t depth,
                             uint32_t array_size) {
  if (!device || set_index >= std::size(g_plume_resources.null_textures)) {
    return false;
  }
  if (g_plume_resources.null_textures[set_index] &&
      g_plume_resources.null_texture_views[set_index]) {
    return true;
  }

  plume::RenderTextureDesc desc = plume::RenderTextureDesc::Texture(
      dimension, 1, 1, depth, 1, array_size,
      plume::RenderFormat::R8G8B8A8_UNORM, flags);
  g_plume_resources.null_textures[set_index] = device->createTexture(desc);
  if (!g_plume_resources.null_textures[set_index]) {
    return false;
  }
  char texture_name[96] = {};
  std::snprintf(texture_name, sizeof(texture_name),
                "Refii Fable null texture set=%u dim=%u view=%u",
                set_index, static_cast<uint32_t>(dimension),
                static_cast<uint32_t>(view_dimension));
  g_plume_resources.null_textures[set_index]->setName(texture_name);
  g_direct_renderer.known_texture_layouts.erase(
      g_plume_resources.null_textures[set_index].get());
  plume::RenderTextureViewDesc view_desc;
  view_desc.format = plume::RenderFormat::R8G8B8A8_UNORM;
  view_desc.dimension = view_dimension;
  view_desc.componentMapping = IdentityTextureComponentMapping();
  g_plume_resources.null_texture_views[set_index] =
      g_plume_resources.null_textures[set_index]->createTextureView(view_desc);
  if (g_plume_resources.null_texture_views[set_index] == nullptr) {
    return false;
  }

  // STAGE THE NULL TEXTURE'S CONTENT (2026-08-03). Until now these textures
  // were created, named, viewed and bound as descriptor 0 of every set — and
  // never written. No upload, no clear. An unresolved texture slot leaves
  // tex2d/tex3d/texcube at 0, i.e. descriptor 0, so every MISSING TEXTURE
  // SAMPLED UNDEFINED DEVICE MEMORY: driver- and allocation-dependent, and not
  // reproducible between two runs of the same build. That makes any screenshot
  // judgement about a missing texture worthless, and it is the likely source of
  // the white-vs-black disagreement in the run-521 notes. `null_texture_uploads`
  // was declared for exactly this and had only ever been reset, never filled.
  //
  // Opaque white, deliberately, not a loud magenta: white is the identity for
  // the albedo multiply that dominates material shaders, so an unresolved slot
  // degrades to "unshaded but still lit" instead of punching a black hole
  // through the frame. It also matches the guest's own encoding of "nothing
  // bound", which is a 1x1 render target (g_DefaultRTTexture 0x8331B0C0), not
  // NULL. Magenta would be easier to spot by eye but would corrupt every
  // lighting result that sampled it.
  //
  // 256 bytes for a single texel: Vulkan takes bufferRowLength in TEXELS with
  // no row alignment requirement (plume_vulkan.cpp:3552), but D3D12 aligns a
  // placed footprint's row pitch to 256, so the larger allocation keeps the
  // copy in bounds on either backend.
  constexpr uint32_t kNullTexel = 0xFFFFFFFFu;
  std::array<uint8_t, 256> staging = {};
  std::memcpy(staging.data(), &kNullTexel, sizeof(kNullTexel));
  g_plume_resources.null_texture_uploads[set_index] = device->createBuffer(
      plume::RenderBufferDesc::UploadBuffer(staging.size()));
  if (g_plume_resources.null_texture_uploads[set_index]) {
    if (void* mapped = g_plume_resources.null_texture_uploads[set_index]->map()) {
      std::memcpy(mapped, staging.data(), staging.size());
      plume::RenderRange written_range(0, staging.size());
      g_plume_resources.null_texture_uploads[set_index]->unmap(0,
                                                               &written_range);
    }
  }
  return true;
}

void ResetPlumeResourceManagerObjectsLocked() {
  // Framebuffers, descriptor sets, and descriptor caches hold raw references to
  // tracked textures/views. Drop them before releasing any tracked resources.
  ++g_plume_resources.framebuffer_invalidation_generation;
  g_plume_resources.framebuffers.clear();
  g_plume_resources.retired_framebuffers.clear();
  for (auto& descriptor_set : g_plume_resources.texture_descriptor_sets) {
    descriptor_set.reset();
  }
  g_plume_resources.sampler_descriptor_set.reset();
  g_plume_resources.cached_texture_descriptors = {};
  for (auto& lookup : g_plume_resources.texture_descriptor_lookup) {
    lookup.clear();
  }
  for (auto& lookup :
       g_plume_resources.texture_descriptor_identity_lookup) {
    lookup.clear();
  }
  g_plume_resources.cached_sampler_descriptors.fill(nullptr);
  g_plume_resources.sampler_descriptor_lookup.clear();
  g_plume_resources.present_blit_texture_sets.clear();
  g_plume_resources.present_blit_texture_set_textures = {};
  g_plume_resources.present_blit_texture_set_views = {};
  g_plume_resources.present_blit_texture_set_layouts = {};
  g_plume_resources.present_blit_sampler_set.reset();
  g_plume_resources.present_output_cache.clear();
  InvalidateFableDescriptorCacheLocked();
  ClearFableFrameLookupCacheLocked();
  ClearFableTextureLookupIndexLocked();

  g_plume_resources.resources.clear();
  g_plume_resources.decoded_color_targets.clear();
  g_plume_resources.decoded_depth_targets.clear();
  g_plume_resources.decoded_color_target_keys.clear();
  g_plume_resources.decoded_depth_target_keys.clear();
  g_plume_resources.retired_resources.clear();
  g_plume_resources.resource_retire_epoch = 0;
  g_plume_resources.pipelines.clear();
  g_plume_resources.color_attachment_padding.clear();
  g_plume_resources.fable_samplers.clear();
  g_plume_resources.next_texture_descriptor_indices = {1u, 1u, 1u};
  g_plume_resources.next_sampler_descriptor_index = 1u;
  for (auto& texture : g_plume_resources.null_textures) {
    texture.reset();
  }
  for (auto& view : g_plume_resources.null_texture_views) {
    view.reset();
  }
  g_plume_resources.null_sampler.reset();
  g_plume_resources.zero_constants.reset();
  g_plume_resources.default_vertex_input_buffer.reset();
  g_plume_resources.implicit_rectangle_vertex_buffer.reset();
  g_plume_resources.quad_list_index_buffers.clear();
  for (auto& upload : g_plume_resources.null_texture_uploads) {
    upload.reset();
  }
  // Re-arm the one-shot: the textures above are gone, so the next
  // CreateNullTextureLocked will recreate and re-stage them.
  g_plume_resources.null_texture_content_initialized = false;
  g_plume_resources.frame_constant_slab.reset();
  g_plume_resources.frame_constant_slab_mapped = nullptr;
  g_plume_resources.frame_constant_slab_size = 0;
  g_plume_resources.frame_constant_slab_offset = 0;
  for (auto& frame_slabs : g_plume_resources.frame_texture_upload_slabs) {
    for (PlumeFrameTextureUploadSlab& slab : frame_slabs) {
      if (slab.buffer && slab.mapped) {
        slab.buffer->unmap();
        slab.mapped = nullptr;
      }
    }
    frame_slabs.clear();
  }
  g_plume_resources.frame_texture_upload_slab_indices = {};
  for (auto& decode_cache : g_plume_resources.frame_texture_upload_decode_caches) {
    decode_cache.clear();
  }
  g_plume_resources.frame_texture_upload_slab_index = 0;
  g_plume_resources.frame_vertex_slab.reset();
  g_plume_resources.frame_vertex_slab_mapped = nullptr;
  g_plume_resources.frame_vertex_slab_size = 0;
  g_plume_resources.frame_vertex_slab_offset = 0;
  g_plume_resources.frame_vertex_buffer_cache.clear();
  g_plume_resources.frame_index_buffer_cache.clear();
  g_plume_resources.frame_input_layout_cache.clear();
  g_plume_resources.pipeline_layout.reset();
  g_plume_resources.present_blit_shader_format =
      plume::RenderShaderFormat::UNKNOWN;
  g_plume_resources.present_blit_pipeline_layout.reset();
  g_plume_resources.present_blit_pipeline.reset();
  g_plume_resources.present_blit_texture_set_index = 0;
  g_plume_resources.present_blit_sampler.reset();
  ResetDirectScaledPrivateRectBlitResourcesLocked();
  g_plume_resources.rectangle_list_shader_format =
      plume::RenderShaderFormat::UNKNOWN;
  g_plume_resources.rectangle_list_geometry_shader.reset();
}

void ResetLiveScenePresentSourceLocked() {
  g_direct_renderer.last_scene_texture = nullptr;
  g_direct_renderer.last_scene_view = nullptr;
  g_direct_renderer.last_scene_layout = plume::RenderTextureLayout::UNKNOWN;
  g_direct_renderer.last_scene_target = 0;
  g_direct_renderer.last_scene_width = 0;
  g_direct_renderer.last_scene_height = 0;
  g_direct_renderer.last_scene_write_serial = 0;
  g_direct_renderer.last_scene_frame_index = 0;
}

void ResetSubmittedScenePresentSourceLocked() {
  g_direct_renderer.last_submitted_scene_texture = nullptr;
  g_direct_renderer.last_submitted_scene_view = nullptr;
  g_direct_renderer.last_submitted_scene_layout =
      plume::RenderTextureLayout::UNKNOWN;
  g_direct_renderer.last_submitted_scene_target = 0;
  g_direct_renderer.last_submitted_scene_width = 0;
  g_direct_renderer.last_submitted_scene_height = 0;
  g_direct_renderer.last_submitted_scene_write_serial = 0;
  g_direct_renderer.last_submitted_scene_frame_index = 0;
}

void ResetFableSceneTargetLocked() {
  g_direct_renderer.fable_scene_target = 0;
  g_direct_renderer.fable_scene_width = 0;
  g_direct_renderer.fable_scene_height = 0;
  g_direct_renderer.title_scene_target = 0;
  g_direct_renderer.title_scene_width = 0;
  g_direct_renderer.title_scene_height = 0;
}

void ResetPixVideoPresentTargetLocked() {
  g_direct_renderer.last_pix_video_target = 0;
  g_direct_renderer.last_pix_video_frame = 0;
}

bool EnsureDirectDrawResourcesLocked(plume::RenderDevice* device,
                                     plume::RenderShaderFormat shader_format) {
  if (!device) {
    return false;
  }
  if (g_direct_renderer.draw_resources_ready &&
      g_direct_renderer.device == device &&
      g_direct_renderer.shader_format == shader_format &&
      g_plume_resources.pipeline_layout) {
    return true;
  }

  ResetPlumeResourceManagerObjectsLocked();
  g_direct_renderer.retained_replaced_texture_views.clear();
  g_direct_renderer.pipelines.clear();
  ClearDirectPipelineEnsureMemoLocked();
  g_direct_renderer.tess_vertex_shader_variants.clear();
  g_direct_renderer.tess_vertex_shader_variant_misses.clear();
  g_direct_renderer.tess_grid_index_buffers.clear();
  g_direct_renderer.frame_upload_buffers.clear();
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
  ClearDirectFramePrivateRectCachesLocked();
  g_direct_renderer.command_input_ready_cache.clear();
  g_direct_renderer.exact_current_producer_owner_cache.clear();
  g_direct_renderer.prepared_exact_copy_destination_keys.clear();
  g_direct_renderer.published_stable_surface_texture_link_keys.clear();
  g_direct_renderer.stable_private_copy_action_keys.clear();
  g_direct_renderer.default_sampler.reset();
  ResetLiveScenePresentSourceLocked();
  g_direct_renderer.known_texture_layouts.clear();
  ResetSubmittedScenePresentSourceLocked();
  ResetFableSceneTargetLocked();
  ResetDirectRenderGraphExecutionSnapshotLocked(0);
  g_direct_renderer.current_frame_has_pix_video_draw = false;
  ResetPixVideoPresentTargetLocked();
  g_direct_renderer.scene_write_serial = 0;
  ResetDirectDiagnosticsLocked();

  plume::RenderDescriptorSetBuilder texture_set_builder;
  texture_set_builder.begin();
  texture_set_builder.addTexture(0, kFableDescriptorCount);
  texture_set_builder.end(true, kFableDescriptorCount);
  for (uint32_t set = 0; set < kDirectTextureDescriptorSets; ++set) {
    g_plume_resources.texture_descriptor_sets[set] =
        texture_set_builder.create(device);
    if (!g_plume_resources.texture_descriptor_sets[set]) {
      return false;
    }
  }

  plume::RenderDescriptorSetBuilder sampler_set_builder;
  sampler_set_builder.begin();
  sampler_set_builder.addSampler(0, kFableSamplerDescriptorCount);
  sampler_set_builder.end(true, kFableSamplerDescriptorCount);
  g_plume_resources.sampler_descriptor_set =
      sampler_set_builder.create(device);
  if (!g_plume_resources.sampler_descriptor_set) {
    return false;
  }

  plume::RenderPipelineLayoutBuilder pipeline_layout_builder;
  pipeline_layout_builder.begin(false, true);
  pipeline_layout_builder.addDescriptorSet(texture_set_builder);
  pipeline_layout_builder.addDescriptorSet(texture_set_builder);
  pipeline_layout_builder.addDescriptorSet(texture_set_builder);
  pipeline_layout_builder.addDescriptorSet(sampler_set_builder);
  if (shader_format == plume::RenderShaderFormat::SPIRV) {
    pipeline_layout_builder.addPushConstant(
        0, 4, sizeof(uint64_t) * 3u,
        plume::RenderShaderStageFlag::VERTEX |
            plume::RenderShaderStageFlag::PIXEL);
  } else {
    pipeline_layout_builder.addRootDescriptor(
        0, 4, plume::RenderRootDescriptorType::CONSTANT_BUFFER);
    pipeline_layout_builder.addRootDescriptor(
        1, 4, plume::RenderRootDescriptorType::CONSTANT_BUFFER);
    pipeline_layout_builder.addRootDescriptor(
        2, 4, plume::RenderRootDescriptorType::CONSTANT_BUFFER);
  }
  pipeline_layout_builder.end();
  g_plume_resources.pipeline_layout = pipeline_layout_builder.create(device);
  if (!g_plume_resources.pipeline_layout) {
    return false;
  }

  g_direct_renderer.default_sampler =
      device->createSampler(plume::RenderSamplerDesc());
  if (!g_direct_renderer.default_sampler) {
    return false;
  }
  SetFableSamplerDescriptorAtIndexLocked(
      0, g_direct_renderer.default_sampler.get());

  if (!CreateNullTextureLocked(
          device, 0, plume::RenderTextureDimension::TEXTURE_2D,
          plume::RenderTextureViewDimension::TEXTURE_2D) ||
      !CreateNullTextureLocked(
          device, 1, plume::RenderTextureDimension::TEXTURE_3D,
          plume::RenderTextureViewDimension::TEXTURE_3D,
          plume::RenderTextureFlag::NONE, 1, 1) ||
      !CreateNullTextureLocked(
          device, 2, plume::RenderTextureDimension::TEXTURE_2D,
          plume::RenderTextureViewDimension::TEXTURE_CUBE,
          plume::RenderTextureFlag::CUBE, 1, 6)) {
    return false;
  }
  for (uint32_t set = 0; set < kDirectTextureDescriptorSets; ++set) {
    SetFableTextureDescriptorAtIndexLocked(
        set, 0, g_plume_resources.null_textures[set].get(),
        plume::RenderTextureLayout::SHADER_READ,
        g_plume_resources.null_texture_views[set].get());
  }

  if (!EnsureFrameTextureUploadSlabs(device)) {
    return false;
  }

  g_direct_renderer.draw_resources_ready = true;
  return true;
}

plume::RenderTextureAddressMode ConvertDirectSamplerAddressMode(uint32_t mode) {
  switch (mode & kDirectPackedSamplerStateAddressMask) {
    case 0:
      return plume::RenderTextureAddressMode::WRAP;
    case 1:
      return plume::RenderTextureAddressMode::MIRROR;
    case 2:
      return plume::RenderTextureAddressMode::CLAMP;
    case 3:
      return plume::RenderTextureAddressMode::MIRROR_ONCE;
    case 6:
      return plume::RenderTextureAddressMode::BORDER;
    // Xenos clamp policies 4/5/7 (ClampToHalfway / MirrorOnceToHalfway /
    // MirrorOnceToBorder) have no exact Vulkan equivalent; map to the
    // nearest supported mode. Previously they fell to UNKNOWN, which is a
    // LIVE assert(false) abort inside plume_vulkan on the Debug build — a
    // single world-content sampler using one ended the process mid-scene.
    // A nearest-mode sampler is visually minor (edge behavior of the last
    // texel row); an abort is not.
    case 4:
      return plume::RenderTextureAddressMode::BORDER;
    case 5:
    case 7:
      return plume::RenderTextureAddressMode::MIRROR_ONCE;
    default:
      return plume::RenderTextureAddressMode::UNKNOWN;
  }
}

plume::RenderSampler* GetOrCreateDirectSamplerLocked(uint32_t packed_state) {
  if (!g_direct_renderer.device || !g_direct_renderer.default_sampler ||
      (packed_state & kDirectPackedSamplerStateValid) == 0) {
    return g_direct_renderer.default_sampler.get();
  }

  auto sampler_it = g_plume_resources.fable_samplers.find(packed_state);
  if (sampler_it != g_plume_resources.fable_samplers.end()) {
    return sampler_it->second.get();
  }

  plume::RenderSamplerDesc desc;
  desc.magFilter = (packed_state & kDirectPackedSamplerStateMagLinear) != 0
                       ? plume::RenderFilter::LINEAR
                       : plume::RenderFilter::NEAREST;
  desc.minFilter = (packed_state & kDirectPackedSamplerStateMinLinear) != 0
                       ? plume::RenderFilter::LINEAR
                       : plume::RenderFilter::NEAREST;
  desc.mipmapMode = (packed_state & kDirectPackedSamplerStateMipLinear) != 0
                        ? plume::RenderMipmapMode::LINEAR
                        : plume::RenderMipmapMode::NEAREST;
  desc.addressU = ConvertDirectSamplerAddressMode(
      packed_state >> kDirectPackedSamplerStateAddressUShift);
  desc.addressV = ConvertDirectSamplerAddressMode(
      packed_state >> kDirectPackedSamplerStateAddressVShift);
  desc.addressW = ConvertDirectSamplerAddressMode(
      packed_state >> kDirectPackedSamplerStateAddressWShift);
  // Mip LOD bias — wired 2026-08-04. The guest's D3DSAMP_MIPMAPLODBIAS was
  // captured into the packed state but never consumed here, so every sampler
  // in the game ran at bias 0. (It was also packed by an expression that could
  // only ever yield zero; see StorePackedSamplerStateValue case 3.)
  desc.mipLODBias = DirectSamplerLodBiasFromPackedState(packed_state);
  // Anisotropy stays OFF, and that is a known gap rather than a decision:
  // D3DDevice_SetSamplerState_MaxAnisotropy (0x821E5290, which writes fetch
  // dword 3 [27:25] through the LUT at dword_82005110) is NOT HOOKED, so the
  // value never reaches us to begin with. Hooking it is the prerequisite;
  // enabling anisotropy here first would just invent a filter the guest never
  // asked for.
  desc.anisotropyEnabled = false;
  desc.maxAnisotropy = 1;

  std::unique_ptr<plume::RenderSampler> sampler =
      g_direct_renderer.device->createSampler(desc);
  if (!sampler) {
    return g_direct_renderer.default_sampler.get();
  }
  plume::RenderSampler* sampler_ptr = sampler.get();
  g_plume_resources.fable_samplers.emplace(packed_state, std::move(sampler));
  return sampler_ptr;
}

PlumeColorAttachmentPadding* GetOrCreateDirectColorAttachmentPaddingLocked(
    plume::RenderDevice* device, uint32_t width, uint32_t height,
    plume::RenderFormat format) {
  if (!device || width == 0 || height == 0) {
    return nullptr;
  }
  if (format == plume::RenderFormat::UNKNOWN) {
    format = plume::RenderFormat::B8G8R8A8_UNORM;
  }

  std::string key = std::to_string(width) + "x" + std::to_string(height) +
                    ":" + std::to_string(static_cast<uint32_t>(format));
  auto padding_it = g_plume_resources.color_attachment_padding.find(key);
  if (padding_it != g_plume_resources.color_attachment_padding.end()) {
    return &padding_it->second;
  }

  PlumeColorAttachmentPadding padding;
  padding.width = width;
  padding.height = height;
  padding.format = format;
  plume::RenderTextureDesc desc = plume::RenderTextureDesc::Texture2D(
      width, height, 1, format, plume::RenderTextureFlag::RENDER_TARGET);
  padding.texture = device->createTexture(desc);
  if (!padding.texture) {
    return nullptr;
  }
  g_direct_renderer.known_texture_layouts.erase(padding.texture.get());
  padding.texture->setName("Refii Fable direct sparse MRT padding");

  plume::RenderTextureViewDesc view_desc;
  view_desc.format = format;
  view_desc.dimension = plume::RenderTextureViewDimension::TEXTURE_2D;
  view_desc.componentMapping = IdentityTextureComponentMapping();
  padding.view = padding.texture->createTextureView(view_desc);
  if (!padding.view) {
    return nullptr;
  }

  auto [it, inserted] = g_plume_resources.color_attachment_padding.emplace(
      key, std::move(padding));
  (void)inserted;
  return &it->second;
}

bool UseSyntheticPrivateRectInputLayout(
    const FableDirectRenderCommand& command) {
  if (command.resources.vertex_shader !=
      kFableSyntheticPrivateRectVertexShader) {
    return false;
  }
  return command.draw.private_texture_rectangle ||
         ResolveDirectInputSlotStrideLocked(command, 0) == 48u;
}

bool DirectCommandCanMaterializePrivateRectTargetLocked(
    const FableDirectRenderCommand& command, uint32_t target) {
  uint32_t target_slot = kMaxFableRenderTargets;
  for (uint32_t slot = 0; slot < command.resources.render_targets.size();
       ++slot) {
    if (command.resources.render_targets[slot] == target) {
      target_slot = slot;
      break;
    }
  }
  // A draw packet's attachment register is authoritative evidence that an
  // otherwise-untracked guest object is a color target. This covers generic
  // off-screen producer surfaces before a later private-rect consumer exposes
  // their surface/texture relationship; waiting for that consumer drops the
  // producer draw and leaves the copied image undefined.
  const bool packet_described_offscreen_target =
      target_slot < command.pipeline.pm4.rb_color_info.size() &&
      command.pipeline.pm4.valid &&
      command.pipeline.pm4.rb_color_info[target_slot] != 0 &&
      target != g_direct_renderer.fable_scene_target &&
      !IsDisplaySurfaceAliasTargetLocked(target);
  // DEAD ADDRESS GATE primitive8_copy_target_decl — DELETED 2026-07-26.
  // A `primitive8_copy_target` clause required vertex_declaration ==
  // 0x40070190. Run 288 census[final] (frame=2952) lists the gate under
  // tested_but_never_matched: control reached it thousands of times and the
  // literal never once said yes, so the clause could never contribute to the
  // materialization decision. Removed along with its census slot's only test
  // site (the enum/descriptor entries stay so the census indices are stable).
  //
  // DEAD ADDRESS GATE title_scene_private_target_scene_exclusion — DELETED
  // 2026-07-26. `title_scene_private_target` carried an extra term
  // `!DirectAddressGateTest(..., target == kFableTitleSceneRenderTarget)`.
  // Same census verdict: never matched, so the negation was unconditionally
  // TRUE and the term was a no-op. The REST of title_scene_private_target is
  // live and is kept verbatim below.
  const bool title_scene_private_target =
      command.draw.source == RendererDrawEventSource::kSceneObject &&
      IsFableTitleSceneVertexShaderCommandLocked(command) &&
      target != g_direct_renderer.fable_scene_target &&
      !IsDisplaySurfaceAliasTargetLocked(target);
  // GTL scratch-wrapper composite/private-rect family (BeginVertices,
  // BeginIndexedVertices, the 0x8220A3F0 quad wrapper -- e.g.
  // Render_ResolveAndCompositePass-style rectlist post-process composites).
  // `target` here is command.resources.render_targets[slot], populated
  // directly from the device's live RT-base register self-read at Begin/quad-
  // wrapper EXIT (refii_gtl.cpp, offset 0x3098 -- the SAME register the
  // observer reads): this is definitive hardware evidence the guest bound
  // `target` as a color render target for THIS draw, at least as authoritative
  // as packet_described_offscreen_target's PM4 rb_color_info check. It is
  // needed because BuildDirectCommandFromGtlDrawPacketLocked (the GTL-
  // authoritative command builder) never populates command.pipeline.pm4, so
  // pm4.valid is always false for these draws and packet_described_offscreen_
  // target can never fire; primitive8_copy_target/title_scene_private_target
  // require kSceneObject, not kScratchWrapper; and UseSyntheticPrivateRectInput
  // Layout requires the synthetic private-rect vertex shader, not a real
  // title-scene shader. Without this clause every composite draw into a
  // not-yet-seen offscreen surface is rejected forever (run231: 120
  // missing_framebuffer rejects, 24 distinct draws, ALL source=kScratchWrapper
  // decl=0x40080190 -- the menu/character-select post-process chain).
  const bool gtl_scratch_wrapper_offscreen_target =
      command.draw.source == RendererDrawEventSource::kScratchWrapper &&
      target != g_direct_renderer.fable_scene_target &&
      !IsDisplaySurfaceAliasTargetLocked(target);
  // GAMEPLAY scene draws -- the same clause as the scratch-wrapper one above,
  // for the one source class that never had it. Added 2026-08-03.
  //
  // The comment on gtl_scratch_wrapper_offscreen_target already spells out why
  // this is sound, and every word of it applies verbatim here: `target` is
  // command.resources.render_targets[slot], set at fable_direct_renderer.cpp
  // :53419 from packet.render_targets, which the GTL fills from the device's
  // LIVE RT-base register self-read at 0x3098. That is definitive hardware
  // evidence the guest bound this surface as a color render target for THIS
  // draw -- at least as authoritative as packet_described_offscreen_target's
  // PM4 rb_color_info check.
  //
  // What was missing: for a kSceneObject draw the ONLY reachable clause was
  // title_scene_private_target, which additionally demands
  // IsFableTitleSceneVertexShaderCommandLocked -- a TITLE-scene shader test
  // that gameplay shaders cannot pass. And packet_described_offscreen_target
  // needs command.pipeline.pm4.valid, which BuildDirectCommandFromGtlDraw
  // PacketLocked never sets (pm4.valid=true appears at exactly one site in
  // this file, the tess builder). So a gameplay world draw into a not-yet-seen
  // offscreen scene target could never mint it, was rejected forever by
  // ResolveDirectFramebufferLocked, and died as `failed direct command
  // last_reason=missing_framebuffer` -- 26 of them on run 538's first world
  // frame, into 0x40CEC3D0 / 0x40CECB20 / 0x40D1F340 / 0x40D1F3A0. The scene
  // colour targets were therefore never written, so the composite sampled
  // empty textures (`resolver null reason ... tracked=0 image=0`, `textureless
  // visible bind`) and the present source was painted with nothing. That is
  // the black loading screen and the black game world.
  //
  // This is exactly the run-231 fix (120 missing_framebuffer rejects on the
  // menu post-process chain) applied to the source class it was never extended
  // to. The extent test below still gates the mint, and the 2026-07-29 mint
  // gate + staleness sweep still bound the VRAM cost.
  const bool gtl_scene_object_offscreen_target =
      command.draw.source == RendererDrawEventSource::kSceneObject &&
      target != g_direct_renderer.fable_scene_target &&
      !IsDisplaySurfaceAliasTargetLocked(target);
  if (target == 0 ||
      (command.draw.source != RendererDrawEventSource::kPrivateTextureRectangle &&
       !UseSyntheticPrivateRectInputLayout(command) &&
       !title_scene_private_target &&
       !packet_described_offscreen_target &&
       !gtl_scratch_wrapper_offscreen_target &&
       !gtl_scene_object_offscreen_target)) {
    return false;
  }
  if (command.viewport.valid && command.viewport.width > 0.0f &&
      command.viewport.height > 0.0f) {
    return true;
  }
  if (command.scissor.valid && command.scissor.right > command.scissor.left &&
      command.scissor.bottom > command.scissor.top) {
    return true;
  }
  return command.draw.private_texture_corners_valid;
}

void BuildSyntheticPrivateRectInputLayout(
    std::vector<plume::RenderInputSlot>& input_slots,
    std::vector<plume::RenderInputElement>& input_elements) {
  input_slots.clear();
  input_elements.clear();
  input_slots.emplace_back(0, 48);
  input_elements.emplace_back("POSITION", 0, 0,
                              plume::RenderFormat::R32G32B32A32_FLOAT, 0, 0);
  input_elements.emplace_back("TEXCOORD", 0, 4,
                              plume::RenderFormat::R32G32B32A32_FLOAT, 0, 16);
  input_elements.emplace_back("TEXCOORD", 1, 5,
                              plume::RenderFormat::R32G32B32A32_FLOAT, 0, 32);
}

void BuildSyntheticPrimitive8RectangleInputLayout(
    std::vector<plume::RenderInputSlot>& input_slots,
    std::vector<plume::RenderInputElement>& input_elements) {
  input_slots.clear();
  input_elements.clear();
  input_slots.emplace_back(0, 8);
  input_elements.emplace_back("POSITION", 0, 0,
                              plume::RenderFormat::R32G32_FLOAT, 0, 0);
}


bool DirectCommandUsesExactBeginVerticesInlineStride(
    const FableDirectRenderCommand& command, uint32_t slot) {
  if (slot != 0 ||
      command.draw.source != RendererDrawEventSource::kScratchWrapper ||
      command.draw.private_texture_wrapper_symbol_address !=
          kFableBeginVerticesTitleWrapperSymbol ||
      command.draw.draw.indexed || command.draw.draw.primitive_type != 8u ||
      command.draw.draw.primitive_count != 3u ||
      slot >= command.inline_vertex_buffers.size()) {
    return false;
  }

  const FableDirectInlineBuffer& inline_buffer =
      command.inline_vertex_buffers[slot];
  return inline_buffer.ready() &&
         (inline_buffer.stride_or_format == 8u ||
          inline_buffer.stride_or_format == 20u);
}

uint32_t ResolveDirectInputSlotStrideLocked(
    const FableDirectRenderCommand& command, uint32_t slot) {
  if (slot >= command.resources.streams.size()) {
    return 0;
  }

  if (DirectCommandUsesExactBeginVerticesInlineStride(command, slot)) {
    return command.inline_vertex_buffers[slot].stride_or_format;
  }

  const RendererStreamBindingSnapshot& stream = command.resources.streams[slot];
  if (stream.stride != 0) {
    return stream.stride;
  }

  if (slot < command.inline_vertex_buffers.size()) {
    const FableDirectInlineBuffer& inline_buffer =
        command.inline_vertex_buffers[slot];
    if (inline_buffer.ready() && inline_buffer.stride_or_format != 0) {
      return inline_buffer.stride_or_format;
    }
  }

  PlumeTrackedResource* declaration =
      FindDirectReadyDeclarationResourceByIdentityLocked(
          command.resources.vertex_declaration);
  if (declaration && PlumeGuestDeclarationReady(*declaration)) {
    const uint32_t declaration_stride = InputLayoutSlotMaxElementEnd(
        declaration->guest_vertex_declaration.input_elements, slot);
    if (declaration_stride != 0) {
      return declaration_stride;
    }
  }

  PlumeTrackedResource* buffer = FindDirectResourceLocked(stream.buffer);
  return buffer ? PlumeGuestBufferStrideOrFormat(*buffer) : 0u;
}

void ResolveDirectInputSlotStridesLocked(
    const FableDirectRenderCommand& command,
    std::vector<plume::RenderInputSlot>& input_slots) {
  for (plume::RenderInputSlot& input_slot : input_slots) {
    const uint32_t stride =
        ResolveDirectInputSlotStrideLocked(command, input_slot.index);
    if (stride != 0) {
      input_slot.stride = stride;
    }
  }
}


uint64_t DirectVertexShaderHashLocked(uint32_t shader) {
  const PlumeTrackedResource* resource = FindDirectResourceLocked(shader);
  if (resource && resource->guest_shader.bytecode_hash != 0) {
    return resource->guest_shader.bytecode_hash;
  }
  const FableShaderIdentity* identity =
      FableDirectFindShaderIdentityByObject(shader);
  return identity ? identity->bytecode_hash : 0ull;
}

bool DirectShaderObjectsEquivalentLocked(uint32_t lhs, uint32_t rhs,
                                         FableGuestResourceKind kind) {
  if (lhs == rhs) {
    return true;
  }
  if (lhs == 0 || rhs == 0) {
    return false;
  }

  const auto shader_hash_for_kind = [&](uint32_t shader) -> uint64_t {
    const PlumeTrackedResource* resource = FindDirectResourceLocked(shader);
    if (resource && resource->kind == kind &&
        resource->guest_shader.bytecode_hash != 0) {
      return resource->guest_shader.bytecode_hash;
    }
    const FableShaderIdentity* identity =
        FableDirectFindShaderIdentityByObject(shader);
    if (identity && identity->kind == kind) {
      return identity->bytecode_hash;
    }
    return 0;
  };

  const uint64_t lhs_hash = shader_hash_for_kind(lhs);
  const uint64_t rhs_hash = shader_hash_for_kind(rhs);
  return lhs_hash != 0 && lhs_hash == rhs_hash;
}

bool DirectVertexShaderObjectsEquivalentLocked(uint32_t lhs, uint32_t rhs) {
  return DirectShaderObjectsEquivalentLocked(
      lhs, rhs, FableGuestResourceKind::kVertexShader);
}

bool DirectPixelShaderObjectsEquivalentLocked(uint32_t lhs, uint32_t rhs) {
  return DirectShaderObjectsEquivalentLocked(
      lhs, rhs, FableGuestResourceKind::kPixelShader);
}

bool DirectActionRootIsOlderThanCommand(uint64_t action_call_serial,
                                        uint32_t action_symbol,
                                        const FableDirectStateRoot& command_root) {
  if (action_call_serial == 0 || command_root.call_serial == 0 ||
      action_call_serial >= command_root.call_serial) {
    return false;
  }
  if (action_symbol != 0 && command_root.symbol_address != 0 &&
      action_symbol != command_root.symbol_address) {
    return false;
  }
  return true;
}

bool DirectActionRootIsUnkeyedForCommand(
    uint64_t action_call_serial, uint64_t action_context_key,
    uint32_t action_symbol, uint32_t action_lr,
    const FableDirectStateRoot& command_root) {
  const bool action_has_root = action_call_serial != 0 ||
                               action_context_key != 0 ||
                               action_symbol != 0 || action_lr != 0;
  const bool command_has_root = command_root.call_serial != 0 ||
                                command_root.context_key != 0 ||
                                command_root.symbol_address != 0 ||
                                command_root.lr != 0;
  return !action_has_root && command_has_root;
}

bool DirectPrivateTextureCopyActionShaderRootsSupersededLocked(
    const FablePrivateTextureCopyAction& action,
    const FableDirectRenderCommand& command) {
  return DirectActionRootIsOlderThanCommand(
             action.vertex_shader_root_call_serial,
             action.vertex_shader_root_symbol_address,
             command.resources.shader_roots.vertex_shader) ||
         DirectActionRootIsOlderThanCommand(
             action.pixel_shader_root_call_serial,
             action.pixel_shader_root_symbol_address,
             command.resources.shader_roots.pixel_shader) ||
         DirectActionRootIsUnkeyedForCommand(
             action.vertex_shader_root_call_serial,
             action.vertex_shader_root_context_key,
             action.vertex_shader_root_symbol_address,
             action.vertex_shader_root_lr,
             command.resources.shader_roots.vertex_shader) ||
         DirectActionRootIsUnkeyedForCommand(
             action.pixel_shader_root_call_serial,
             action.pixel_shader_root_context_key,
             action.pixel_shader_root_symbol_address,
             action.pixel_shader_root_lr,
             command.resources.shader_roots.pixel_shader);
}

bool DirectPrivateTextureCopyActionTargetShapeCompatibleWithResource(
    const FablePrivateTextureCopyAction& action,
    const PlumeTrackedResource& target) {
  const uint32_t target_width = PlumeGuestTextureWidth(target);
  const uint32_t target_height = PlumeGuestTextureHeight(target);
  if (action.target_width != 0 && target_width != 0 &&
      action.target_width != target_width) {
    return false;
  }
  if (action.target_height != 0 && target_height != 0 &&
      action.target_height != target_height) {
    return false;
  }
  return true;
}

uint64_t DirectVertexDeclarationHashLocked(uint32_t declaration) {
  const PlumeTrackedResource* resource = FindDirectResourceLocked(declaration);
  if (resource && PlumeGuestDeclarationReady(*resource) &&
      resource->guest_vertex_declaration.elements_hash != 0) {
    return resource->guest_vertex_declaration.elements_hash;
  }
  if (EnsureDirectKnownVertexDeclarationResourceLocked(declaration)) {
    resource = FindDirectResourceLocked(declaration);
    if (resource && PlumeGuestDeclarationReady(*resource) &&
        resource->guest_vertex_declaration.elements_hash != 0) {
      return resource->guest_vertex_declaration.elements_hash;
    }
  }
  const FableVertexDeclarationIdentity* identity =
      FableDirectFindVertexDeclarationIdentityByObject(declaration);
  if (identity && identity->elements_hash != 0) {
    return identity->elements_hash;
  }
  return 0ull;
}

bool DirectVertexDeclarationMatchesHashLocked(uint32_t declaration,
                                              uint64_t expected_hash) {
  if (expected_hash == 0) {
    return false;
  }
  return DirectVertexDeclarationHashLocked(declaration) == expected_hash;
}

bool IsFableTitleSceneVertexShaderCommandLocked(
    const FableDirectRenderCommand& command) {
  if (IsFableTitleSceneVertexShader(command.resources.vertex_shader)) {
    return true;
  }

  const uint64_t shader_hash =
      DirectVertexShaderHashLocked(command.resources.vertex_shader);
  const uint32_t declaration = command.resources.vertex_declaration;
  const uint32_t stream_stride = ResolveDirectInputSlotStrideLocked(command, 0);

  if (shader_hash == kFableTitleWaterTessellatorZPassVertexHash) {
    if (!DirectVertexDeclarationMatchesHashLocked(
            declaration, kFableTitleIndexedDeclarationHash)) {
      return false;
    }
    if (stream_stride == 72u) {
      return true;
    }
    // This vertex shader is shared between the water z-pass AND tree-billboard
    // sway animation (same transform math, different pixel shader -- confirmed
    // via the extracted PSHADER_TREEBILLBOARD HLSL, which is a real foliage
    // material shader, not a prepass). `stream_stride` above is
    // ResolveDirectInputSlotStrideLocked's result, which returns the RAW
    // device-bound stream stride FIRST (the actual per-vertex byte pitch of
    // whatever buffer is bound) and only falls back to the declaration-derived
    // size if that is zero. In menu/character-select the guest binds tree
    // billboards through a shared, larger vertex-pool buffer (stride 304
    // observed via RenderDoc, frame1557/frame3357), so `stream_stride` can
    // never equal 72 there even though the DECLARATION itself only reads 72
    // bytes of actual per-vertex attributes for this shader. Without this,
    // IsFableTitleSceneVertexShaderCommandLocked rejects every tree-billboard
    // draw sharing that buffer, DirectCommandPixelTextureSlotMaskLocked never
    // reaches the kFableTitleTreeBillboardPixelHash classification, and the
    // draw executes with NO texture bound (RenderDoc: ps=none/smp=none).
    // Check the declaration's own element span (real captured guest
    // declaration data, not a guess) as an independent second signal before
    // rejecting.
    if (PlumeTrackedResource* decl_resource =
            FindDirectReadyDeclarationResourceByIdentityLocked(declaration);
        decl_resource && PlumeGuestDeclarationReady(*decl_resource)) {
      return InputLayoutSlotMaxElementEnd(
                 decl_resource->guest_vertex_declaration.input_elements, 0) ==
             72u;
    }
    return false;
  }
  if (shader_hash == kFableTitleWaterTessellatorMistedZPassVertexHash) {
    return DirectVertexDeclarationMatchesHashLocked(
               declaration, kFablePrivateRectDeclarationHash) &&
           stream_stride == 20u;
  }
  if (shader_hash == kFableTitleGuiText3DVertexHash) {
    return IsFableTitleGuiQuadDeclaration(declaration) && stream_stride == 8u;
  }
  if (shader_hash == kFableTitleOceanWaterVertexHash) {
    return DirectVertexDeclarationMatchesHashLocked(
               declaration, kFablePrivateRectDeclarationHash) &&
           stream_stride == 20u;
  }
  if (shader_hash == kFableRepeatedMeshesAlphaNoStippleVertexHash) {
    return DirectVertexDeclarationMatchesHashLocked(
        declaration, kFableRepeatedMeshDeclarationHash);
  }
  return false;
}

bool IsFableTitleSceneFramebuffer(const DirectFramebufferBinding& framebuffer) {
  for (uint32_t slot = 0; slot < framebuffer.color_count; ++slot) {
    if (IsFableTitleSceneTargetLocked(framebuffer.color_addresses[slot])) {
      return true;
    }
  }
  return IsFableTitleSceneTargetLocked(framebuffer.color_address);
}

std::string DirectFramebufferKey(
                                 const std::array<PlumeTrackedResource*,
                                                  kMaxFableRenderTargets>& colors,
                                 uint32_t color_count,
                                 const PlumeTrackedResource* depth,
                                 plume::RenderFramebuffer* output_framebuffer) {
  if (color_count == 0 && !depth) {
    return "output:" + std::to_string(
                         reinterpret_cast<uintptr_t>(output_framebuffer));
  }

  std::string key;
  key.reserve(320);
  key += "direct:";
  for (uint32_t slot = 0; slot < color_count; ++slot) {
    const PlumeTrackedResource* color = colors[slot];
    if (slot != 0) {
      key += ',';
    }
    if (color) {
      key += std::to_string(color->guest_address);
      key += ':';
      key += std::to_string(color->serial);
      key += ':';
      key += std::to_string(reinterpret_cast<uintptr_t>(
          color->guest_texture.texture.get()));
      key += ':';
      key += std::to_string(reinterpret_cast<uintptr_t>(
          color->guest_surface.attachment_view.get()));
    } else {
      key += "0";
    }
  }
  key += '|';
  if (depth) {
    key += std::to_string(depth->guest_address);
    key += ':';
    key += std::to_string(depth->serial);
    key += ':';
    key += std::to_string(reinterpret_cast<uintptr_t>(
        depth->guest_texture.texture.get()));
    key += ':';
    key += std::to_string(reinterpret_cast<uintptr_t>(
        depth->guest_surface.attachment_view.get()));
  } else {
    key += "0";
  }
  return key;
}

std::string DirectStableAttachmentFramebufferKey(
    const DirectFramebufferBinding& binding,
    plume::RenderFramebuffer* output_framebuffer) {
  if (binding.color_count == 0 && binding.depth_texture == nullptr) {
    return "output-stable:" +
           std::to_string(reinterpret_cast<uintptr_t>(output_framebuffer));
  }

  std::string key;
  key.reserve(384);
  key += "direct-stable:";
  key += std::to_string(binding.width);
  key += 'x';
  key += std::to_string(binding.height);
  key += ':';
  key += std::to_string(binding.color_count);
  key += ':';
  for (uint32_t slot = 0; slot < binding.color_count; ++slot) {
    if (slot != 0) {
      key += ',';
    }
    key += std::to_string(binding.color_addresses[slot]);
    key += ':';
    key += std::to_string(static_cast<uint32_t>(binding.color_formats[slot]));
    key += ':';
    key += std::to_string(reinterpret_cast<uintptr_t>(
        binding.color_textures[slot]));
    key += ':';
    key += std::to_string(reinterpret_cast<uintptr_t>(
        binding.color_views[slot]));
  }
  key += '|';
  key += std::to_string(binding.depth_address);
  key += ':';
  key += std::to_string(static_cast<uint32_t>(binding.depth_format));
  key += ':';
  key += std::to_string(reinterpret_cast<uintptr_t>(binding.depth_texture));
  key += ':';
  key += std::to_string(reinterpret_cast<uintptr_t>(binding.depth_view));
  return key;
}


bool IsDisplaySurfaceAliasTargetLocked(uint32_t address) {
  if (address == 0) {
    return false;
  }

  const FableDirectPresentSnapshot present = GetFableDirectPresentSnapshot();
  if (present.display_surface_object != 0 &&
      address == present.display_surface_object) {
    return true;
  }

  for (uint32_t i = 0; i < present.surface_copy_alias_count; ++i) {
    const RendererSurfaceCopyAliasSnapshot& alias =
        present.surface_copy_aliases[i];
    if (alias.destination_surface == present.display_surface_object &&
        alias.source_surface == address) {
      return true;
    }
  }

  return false;
}

bool IsFableSceneTargetCandidate(uint32_t address,
                                  uint32_t width,
                                  uint32_t height) {
  return FableTitleSceneTargetCandidate(address, width, height);
}

bool IsFablePresentTargetCandidate(uint32_t address,
                                    uint32_t width,
                                    uint32_t height) {
  return FableTitlePresentTargetCandidate(address, width, height);
}

void RememberFableSceneTargetLocked(uint32_t address,
                                     uint32_t width,
                                     uint32_t height,
                                     const char* reason) {
  if (!IsFableSceneTargetCandidate(address, width, height)) {
    return;
  }
  if (address == g_direct_renderer.fable_scene_target &&
      width == g_direct_renderer.fable_scene_width &&
      height == g_direct_renderer.fable_scene_height) {
    return;
  }

  const uint64_t candidate_area = uint64_t(width) * uint64_t(height);
  const uint64_t remembered_area =
      uint64_t(g_direct_renderer.fable_scene_width) *
      uint64_t(g_direct_renderer.fable_scene_height);
  if (g_direct_renderer.fable_scene_target != 0 &&
      candidate_area < remembered_area) {
    return;
  }

  const uint32_t previous_target = g_direct_renderer.fable_scene_target;
  const uint32_t previous_width = g_direct_renderer.fable_scene_width;
  const uint32_t previous_height = g_direct_renderer.fable_scene_height;
  g_direct_renderer.fable_scene_target = address;
  g_direct_renderer.fable_scene_width = width;
  g_direct_renderer.fable_scene_height = height;
  const bool previous_title_target_was_display_alias =
      g_direct_renderer.title_scene_target == kFableTitleVisibleRenderTarget ||
      IsDisplaySurfaceAliasTargetLocked(g_direct_renderer.title_scene_target);
  const bool candidate_is_display_alias =
      IsDisplaySurfaceAliasTargetLocked(address);
  if (g_direct_renderer.title_scene_target == 0 ||
      address == kFableTitleSceneRenderTarget ||
      (previous_title_target_was_display_alias && !candidate_is_display_alias)) {
    g_direct_renderer.title_scene_target = address;
    g_direct_renderer.title_scene_width = width;
    g_direct_renderer.title_scene_height = height;
  }
  if (g_direct_renderer.diagnostics.logged_fable_scene_targets < 16u) {
    REXLOG_INFO(
        "Refii plume direct renderer Fable scene target #{} reason={} "
        "target=0x{:08X} size={}x{} previous=0x{:08X}/{}x{} "
        "title=0x{:08X}/{}x{}",
        ++g_direct_renderer.diagnostics.logged_fable_scene_targets,
        reason ? reason : "unknown", address, width, height,
        previous_target, previous_width, previous_height,
        g_direct_renderer.title_scene_target,
        g_direct_renderer.title_scene_width,
        g_direct_renderer.title_scene_height);
  }
}

bool IsFableTitleSceneTargetLocked(uint32_t address) {
  return address == kFableTitleSceneRenderTarget ||
         (address != 0 && address == g_direct_renderer.title_scene_target) ||
         IsDisplaySurfaceAliasTargetLocked(address);
}

bool DirectResourceIsTrackedSceneOutputLocked(
    const PlumeTrackedResource& resource) {
  const uint32_t width = PlumeGuestTextureWidth(resource);
  const uint32_t height = PlumeGuestTextureHeight(resource);
  const bool fable_sized =
      IsFableSceneTargetCandidate(resource.guest_address, width, height);
  const auto matches_address = [&](uint32_t address) {
    if (address == 0) {
      return false;
    }
    if (address == g_direct_renderer.fable_scene_target ||
        IsDisplaySurfaceAliasTargetLocked(address)) {
      return true;
    }
    return fable_sized &&
           (address == g_direct_renderer.last_scene_target ||
            address == g_direct_renderer.last_submitted_scene_target);
  };

  return matches_address(resource.guest_address) ||
         matches_address(resource.parent_address) ||
         (fable_sized &&
          (resource.guest_texture.texture.get() ==
               g_direct_renderer.last_scene_texture ||
           resource.guest_texture.view.get() ==
               g_direct_renderer.last_scene_view ||
           resource.guest_texture.texture.get() ==
               g_direct_renderer.last_submitted_scene_texture ||
           resource.guest_texture.view.get() ==
               g_direct_renderer.last_submitted_scene_view));
}

bool IsFableTitleSceneDrawTargetLocked(
    const FableDirectRenderCommand& command,
    const DirectFramebufferBinding& framebuffer) {
  if (framebuffer.color_count == 0) {
    return false;
  }
  const bool target_matches =
      IsFableTitleSceneTargetLocked(command.resources.render_targets[0]) ||
      IsFableTitleSceneTargetLocked(framebuffer.color_address);
  const bool final_title_private_indirection =
      IsFableFinalTitlePrivateIndirectionCommandLocked(command);
  const bool shader_matches =
      final_title_private_indirection ||
      IsFableTitleSceneVertexShaderCommandLocked(command) ||
      command.resources.pixel_shader == kFableTitleSceneTextPixelShader;
  if (!target_matches) {
    return command.draw.source == RendererDrawEventSource::kSceneObject &&
           shader_matches;
  }
  if (command.draw.source == RendererDrawEventSource::kSceneObject) {
    return true;
  }
  return shader_matches;
}

bool ShouldPreserveTrackedSceneOverDisplayAliasLocked(
    uint32_t candidate_width,
    uint32_t candidate_height,
    uint32_t candidate_target,
    uint32_t remembered_width,
    uint32_t remembered_height,
    uint32_t remembered_target) {
  if (candidate_target == 0 || remembered_target == 0 ||
      candidate_target == remembered_target ||
      !IsDisplaySurfaceAliasTargetLocked(candidate_target) ||
      IsDisplaySurfaceAliasTargetLocked(remembered_target)) {
    return false;
  }

  const uint64_t candidate_area =
      uint64_t(candidate_width) * uint64_t(candidate_height);
  const uint64_t remembered_area =
      uint64_t(remembered_width) * uint64_t(remembered_height);
  return candidate_area <= remembered_area;
}

bool ShouldReplaceDisplayAliasWithSceneTargetLocked(uint32_t candidate_width,
                                                    uint32_t candidate_height,
                                                    uint32_t candidate_target,
                                                    uint32_t remembered_target) {
  return candidate_target != 0 && remembered_target != 0 &&
         candidate_target != remembered_target &&
         IsFableSceneTargetCandidate(candidate_target, candidate_width,
                                     candidate_height) &&
         !IsDisplaySurfaceAliasTargetLocked(candidate_target) &&
         IsDisplaySurfaceAliasTargetLocked(remembered_target);
}

void MarkDirectResourceGpuProducedContentLocked(
    uint32_t address, uint64_t write_serial, bool clear_only,
    const plume::RenderTexture* attachment) {
  if (address == 0 || write_serial == 0) {
    return;
  }
  PlumeTrackedResource* resource = FindDirectResourceLocked(address);
  if (!resource || !IsImageResource(resource->kind)) {
    return;
  }
  resource->content_serial = std::max(resource->content_serial, write_serial);
  if (clear_only) {
    resource->clear_content_serial =
        std::max(resource->clear_content_serial, write_serial);
  } else {
    resource->draw_content_serial =
        std::max(resource->draw_content_serial, write_serial);
    resource->upload_generation_at_gpu_write = std::max(
        resource->upload_generation_at_gpu_write, resource->upload_generation);
    // GPU-authoritative index: draws only (clears must not mask streamed CPU
    // content), and only when the rasterized/copied image IS this resource's
    // own texture. Address-level marks also land on same-base aliases whose
    // pixels never received the raster (the carry can miss them) — indexing
    // those made the sampler bind blank pages.
    const plume::RenderTexture* native =
        resource->guest_texture.texture ? resource->guest_texture.texture.get()
                                        : resource->guest_surface.texture;
    const uint32_t base = PlumeGuestTextureBaseAddress(*resource);
    if (base != 0 && attachment != nullptr && attachment == native &&
        !resource->texture_depth_target) {
      g_direct_renderer.gpu_drawn_textures_by_base[base] =
          resource->guest_address;
    }
  }
}

void RememberDrawnScenePrivateTextureCopyActionsLocked(
    const FableDirectRenderCommand& command,
    const DirectFramebufferBinding& binding) {
  if (command.draw.source != RendererDrawEventSource::kSceneObject ||
      command.draw.source == RendererDrawEventSource::kPixVideo ||
      (!IsFableTitleSceneVertexShaderCommandLocked(command) &&
       !IsFableTitleSceneTargetLocked(command.resources.render_targets[0]))) {
    return;
  }
  // DERIVED 2026-07-26 (census: scene_private_copy_ps). Was three pixel-shader
  // OBJECT pointers captured from one title run — pool-allocated (verified in
  // IDA at D3DDevice_CreatePixelShader, see the postprocess-hash block near the
  // top of this file), so at gameplay the whole function would have silently
  // become a no-op with no log and no counter. Re-expressed against the guest
  // shader payload hash, which is the identity the guest itself copies:
  //   0x4215A710 -> 0170_pshader_postprocessing_radialblur
  //   0x4216D090 -> 0160_pshader_postprocessing_downscale2
  //   0x42220410 -> 0161_pshader_postprocessing_downscale4
  // The census call is DELIBERATELY INVERTED relative to Phase 1: the hash test
  // now DECIDES and the retired address list rides along as the `derived_hit`
  // observability term. So for this gate, from now on:
  //   matched  — hash decisions (run 288 baseline: 2304; a DROP means the hash
  //              set is wrong or a shader payload changed)
  //   derived  — matches the retired address literal also covered (expect
  //              exactly 2304 while title content is unchanged)
  //   lit_only — matches ONLY the hash caught, i.e. the widening. Run 288 had
  //              one object address per hash, so 0 is expected at title; a
  //              non-zero value at gameplay is the gate correctly following
  //              content the address form could never have seen.
  const uint64_t scene_private_copy_hash =
      DirectPixelShaderHashLocked(command.resources.pixel_shader);
  const bool scene_private_copy_derived =
      IsFableScenePrivateCopyPixelShaderHash(scene_private_copy_hash);
  const bool scene_private_copy_retired_literal =
      command.resources.pixel_shader == 0x4215A710u ||
      command.resources.pixel_shader == 0x4216D090u ||
      command.resources.pixel_shader == 0x42220410u;
  if (!DirectAddressGateTest(
          DirectAddressGate::kScenePrivateCopyPixelShader,
          scene_private_copy_derived, command.resources.pixel_shader,
          scene_private_copy_hash, scene_private_copy_retired_literal)) {
    return;
  }

  const uint32_t texture_slot_mask =
      DirectCommandPixelTextureSlotMaskLocked(command);
  for (uint32_t target_slot = 0; target_slot < binding.color_count;
       ++target_slot) {
    const uint32_t target_address = binding.color_addresses[target_slot];
    if (target_address == 0 ||
        DirectColorWriteMaskForFramebufferTarget(command, binding,
                                                 target_slot) == 0) {
      continue;
    }

    PlumeTrackedResource* target = FindDirectResourceLocked(target_address);
    if (!target || !IsImageResource(target->kind) ||
        !PlumeGuestImageReady(*target) ||
        !target->texture_render_target || target->texture_depth_target ||
        !DirectCommandBindsResourceAsTargetLocked(command, *target)) {
      continue;
    }

    for (uint32_t source_slot = 0;
         source_slot < command.resources.textures.size(); ++source_slot) {
      if (!DirectCommandUsesTextureSlotForSyncLocked(
              command, texture_slot_mask, source_slot)) {
        continue;
      }

      const uint32_t source_address = command.resources.textures[source_slot];
      if (source_address == 0 ||
          DirectResourceAddressMatchesLocked(source_address,
                                             target->guest_address)) {
        continue;
      }

      PlumeTrackedResource* source = FindDirectResourceLocked(source_address);
      if (!source || source == target || !IsImageResource(source->kind) ||
          !PlumeGuestImageReady(*source) ||
          !DirectCommandUsesFablePrivateTexturePath(command, source_slot,
                                                    source) ||
          DirectCommandBindsResourceAsTargetLocked(command, *source)) {
        continue;
      }

      RememberDirectPrivateTextureCopyActionLocked(
          command, source_slot, *source, *target,
          FablePrivateTextureCopyKind::kColor, false);

      static uint32_t logged_drawn_scene_private_copy_actions = 0;
      if (logged_drawn_scene_private_copy_actions < 32u) {
        REXLOG_INFO(
            "Refii plume registered drawn scene private copy action #{} "
            "frame={} slot={} source=0x{:08X}/{}x{} target=0x{:08X}/{}x{} "
            "rt=0x{:08X} ds=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
            ++logged_drawn_scene_private_copy_actions,
            g_direct_renderer.current_frame_index, source_slot,
            source->guest_address, PlumeGuestTextureWidth(*source),
            PlumeGuestTextureHeight(*source), target->guest_address,
            PlumeGuestTextureWidth(*target), PlumeGuestTextureHeight(*target),
            command.resources.render_targets[0], command.resources.depth_stencil,
            command.resources.vertex_shader, command.resources.pixel_shader);
      }
    }
  }
}

void RememberDrawnSceneTargetLocked(const FableDirectRenderCommand& command,
                                    const DirectFramebufferBinding& binding,
                                    bool submitted_command_source) {
  if (!binding.color_texture || !binding.color_sample_view) {
    return;
  }
  constexpr uint32_t kMinimumSceneTargetDimension = 64u;
  if (binding.width < kMinimumSceneTargetDimension ||
      binding.height < kMinimumSceneTargetDimension) {
    return;
  }

  uint32_t primary_slot = UINT32_MAX;
  for (uint32_t slot = 0; slot < binding.color_count; ++slot) {
    if (binding.color_addresses[slot] == 0 ||
        DirectColorWriteMaskForFramebufferTarget(command, binding, slot) == 0) {
      continue;
    }
    if (primary_slot == UINT32_MAX) {
      primary_slot = slot;
    }
  }
  if (primary_slot == UINT32_MAX) {
    return;
  }

  const uint64_t write_serial = ++g_direct_renderer.scene_write_serial;
  for (uint32_t slot = 0; slot < binding.color_count; ++slot) {
    if (binding.color_addresses[slot] == 0 ||
        DirectColorWriteMaskForFramebufferTarget(command, binding, slot) == 0) {
      continue;
    }
    MarkDirectResourceGpuProducedContentLocked(
        binding.color_addresses[slot], write_serial, false,
        slot < binding.color_textures.size() ? binding.color_textures[slot]
                                             : nullptr);
  }
  RememberDrawnScenePrivateTextureCopyActionsLocked(command, binding);
  const uint32_t primary_address = binding.color_addresses[primary_slot];
  plume::RenderTexture* primary_texture = binding.color_textures[primary_slot];
  plume::RenderTextureView* primary_view =
      binding.color_sample_views[primary_slot];
  if (!primary_texture || !primary_view) {
    return;
  }
  if (!IsFableSceneTargetCandidate(primary_address, binding.width,
                                   binding.height)) {
    return;
  }
  const bool refresh_last_scene =
      primary_address == g_direct_renderer.last_scene_target ||
      primary_texture == g_direct_renderer.last_scene_texture;
  if (refresh_last_scene ||
      ShouldReplaceDisplayAliasWithSceneTargetLocked(
          binding.width, binding.height, primary_address,
          g_direct_renderer.last_scene_target) ||
      (!ShouldPreserveTrackedSceneOverDisplayAliasLocked(
          binding.width, binding.height, primary_address,
          g_direct_renderer.last_scene_width,
          g_direct_renderer.last_scene_height,
          g_direct_renderer.last_scene_target) &&
       ShouldReplaceTrackedSceneTarget(
          binding.width, binding.height, g_direct_renderer.last_scene_width,
          g_direct_renderer.last_scene_height, primary_address,
          g_direct_renderer.last_scene_target))) {
    g_direct_renderer.last_scene_texture = primary_texture;
    g_direct_renderer.last_scene_view = primary_view;
    g_direct_renderer.last_scene_layout = plume::RenderTextureLayout::COLOR_WRITE;
    g_direct_renderer.last_scene_target = primary_address;
    g_direct_renderer.last_scene_width = binding.width;
    g_direct_renderer.last_scene_height = binding.height;
    g_direct_renderer.last_scene_write_serial = write_serial;
    g_direct_renderer.last_scene_frame_index =
        g_direct_renderer.current_frame_index;
  }
  if (submitted_command_source) {
    const bool refresh_submitted_scene =
        primary_address == g_direct_renderer.last_submitted_scene_target ||
        primary_texture == g_direct_renderer.last_submitted_scene_texture;
    if (refresh_submitted_scene ||
        ShouldReplaceDisplayAliasWithSceneTargetLocked(
            binding.width, binding.height, primary_address,
            g_direct_renderer.last_submitted_scene_target) ||
        (!ShouldPreserveTrackedSceneOverDisplayAliasLocked(
            binding.width, binding.height, primary_address,
            g_direct_renderer.last_submitted_scene_width,
            g_direct_renderer.last_submitted_scene_height,
            g_direct_renderer.last_submitted_scene_target) &&
         ShouldReplaceTrackedSceneTarget(
            binding.width, binding.height,
            g_direct_renderer.last_submitted_scene_width,
            g_direct_renderer.last_submitted_scene_height,
            primary_address,
            g_direct_renderer.last_submitted_scene_target))) {
      g_direct_renderer.last_submitted_scene_texture = primary_texture;
      g_direct_renderer.last_submitted_scene_view = primary_view;
      g_direct_renderer.last_submitted_scene_layout =
          plume::RenderTextureLayout::COLOR_WRITE;
      g_direct_renderer.last_submitted_scene_target = primary_address;
      g_direct_renderer.last_submitted_scene_width = binding.width;
      g_direct_renderer.last_submitted_scene_height = binding.height;
      g_direct_renderer.last_submitted_scene_write_serial = write_serial;
      g_direct_renderer.last_submitted_scene_frame_index =
          g_direct_renderer.current_frame_index;
    }
  }
}

void InvalidateRememberedSceneTargetForClearLocked(
    uint32_t address,
    plume::RenderTexture* texture,
    uint32_t width,
    uint32_t height) {
  if (address == 0 && texture == nullptr) {
    return;
  }
  const auto matches = [&](uint32_t remembered_target,
                           plume::RenderTexture* remembered_texture) {
    return (address != 0 && address == remembered_target) ||
           (texture != nullptr && texture == remembered_texture);
  };

  const bool clear_last_scene =
      matches(g_direct_renderer.last_scene_target,
              g_direct_renderer.last_scene_texture);
  const bool clear_last_submitted_scene =
      matches(g_direct_renderer.last_submitted_scene_target,
              g_direct_renderer.last_submitted_scene_texture);
  if (!clear_last_scene && !clear_last_submitted_scene) {
    return;
  }

  const bool preserve_title_clear =
      address == kFableTitleSceneRenderTarget ||
      address == kFableTitleVisibleRenderTarget ||
      (!IsFableSceneTargetCandidate(address, width, height) &&
       IsDisplaySurfaceAliasTargetLocked(address));
  if (preserve_title_clear) {
    return;
  }

  if (g_direct_renderer.diagnostics.logged_scene_clear_invalidations < 32u) {
    REXLOG_INFO(
        "Refii plume direct renderer invalidated tracked scene on clear "
        "#{} target=0x{:08X} size={}x{} old_scene=0x{:08X}/{}x{} "
        "old_submitted=0x{:08X}/{}x{} serials={}/{}",
        ++g_direct_renderer.diagnostics.logged_scene_clear_invalidations, address, width,
        height, g_direct_renderer.last_scene_target,
        g_direct_renderer.last_scene_width, g_direct_renderer.last_scene_height,
        g_direct_renderer.last_submitted_scene_target,
        g_direct_renderer.last_submitted_scene_width,
        g_direct_renderer.last_submitted_scene_height,
        g_direct_renderer.last_scene_write_serial,
        g_direct_renderer.last_submitted_scene_write_serial);
  }

  if (clear_last_scene) {
    ResetLiveScenePresentSourceLocked();
  }
  if (clear_last_submitted_scene) {
    ResetSubmittedScenePresentSourceLocked();
  }
}

void ForgetDrawnSceneTargetLocked(
    uint32_t address, const PlumeTrackedResource* destroyed_resource) {
  plume::RenderTexture* destroyed_texture =
      destroyed_resource ? destroyed_resource->guest_texture.texture.get()
                         : nullptr;
  const auto matches = [&](uint32_t remembered_target,
                           plume::RenderTexture* remembered_texture) {
    return (address != 0 && address == remembered_target) ||
           (destroyed_texture != nullptr &&
            destroyed_texture == remembered_texture);
  };

  if (matches(g_direct_renderer.last_scene_target,
              g_direct_renderer.last_scene_texture)) {
    ResetLiveScenePresentSourceLocked();
  }
  if (matches(g_direct_renderer.last_submitted_scene_target,
              g_direct_renderer.last_submitted_scene_texture)) {
    ResetSubmittedScenePresentSourceLocked();
  }
  if (g_direct_renderer.last_pix_video_target == address) {
    ResetPixVideoPresentTargetLocked();
  }
}

uint32_t PackFableSurfaceExtent(uint32_t width, uint32_t height) {
  width = std::clamp(width, 1u, 0x4000u);
  height = std::clamp(height, 1u, 0x8000u);
  return ((width - 1u) << 18u) | ((height - 1u) << 3u);
}

bool ResolvePrivateRectTargetExtent(const FableDirectRenderCommand& command,
                                    uint32_t& width, uint32_t& height) {
  auto valid_extent = [](uint32_t candidate_width, uint32_t candidate_height) {
    return candidate_width != 0 && candidate_height != 0 &&
           candidate_width <= 2048u && candidate_height <= 2048u;
  };

  // A VIEWPORT IS NOT AN EXTENT (reordered 2026-08-04, on capture evidence).
  //
  // This function decides how big a synthetic render target to CREATE for a
  // private-rect draw whose guest address is not materialised yet. It used to
  // take the viewport verbatim, ahead of every other candidate. Gameplay
  // capture frame4735 shows what that produces:
  //   eid=1550  rt guest=0x40D566D0  1433x737 R8G8B8A8  vp=1433x737
  //   eid=1564  blit FROM that surface INTO guest=0x404DF550, 280x180
  //   eid=415   rt guest=0x40D56550  217x1923 R8G8B8A8  vp=217x1923
  // We fabricated a 1433x737 attachment to match a fabricated viewport, ran the
  // post chain into it, and then copied the result into the 280x180 surface the
  // guest actually uses. That is also why the attachment clamp never fires on
  // these: the attachment was sized to the bogus viewport BY CONSTRUCTION, so
  // the viewport fits it perfectly.
  //
  // 1433x737 and 217x1923 are decoded allocator ADDRESSES
  // (width=(p>>18)+1, height=((p>>3)&0x7FFF)+1 over a word that is not a packed
  // extent), which is exactly the emulation the architecture rule forbids: a
  // stated create-call integer cannot produce 217x1923.
  //
  // So order the candidates by how STATED they are:
  //   1. the draw's own quad corners  - real geometry the guest submitted
  //   2. the scissor rect            - a stated D3D9 rect
  //   3. the viewport                - last resort only, and logged loudly
  // The viewport stays as a fallback rather than being removed outright,
  // because returning false here means no target at all, which is a worse
  // failure than a wrong-sized one. `extent_source` in the log below says which
  // branch won, so a run tells us whether the fallback is still load-bearing.
  uint32_t corner_width = 0;
  uint32_t corner_height = 0;
  if (command.draw.private_texture_corners_valid) {
    const auto& corners = command.draw.private_texture_corners;
    corner_width =
        static_cast<uint32_t>(std::lround(std::fabs(corners[2] - corners[0])));
    corner_height =
        static_cast<uint32_t>(std::lround(std::fabs(corners[5] - corners[1])));
  }
  uint32_t scissor_width = 0;
  uint32_t scissor_height = 0;
  if (command.scissor.valid && command.scissor.right > command.scissor.left &&
      command.scissor.bottom > command.scissor.top) {
    scissor_width =
        static_cast<uint32_t>(command.scissor.right - command.scissor.left);
    scissor_height =
        static_cast<uint32_t>(command.scissor.bottom - command.scissor.top);
  }
  uint32_t viewport_width = 0;
  uint32_t viewport_height = 0;
  if (command.viewport.valid && command.viewport.width > 0.0f &&
      command.viewport.height > 0.0f) {
    viewport_width = static_cast<uint32_t>(std::lround(command.viewport.width));
    viewport_height =
        static_cast<uint32_t>(std::lround(command.viewport.height));
  }

  const char* extent_source = nullptr;
  if (valid_extent(corner_width, corner_height)) {
    width = corner_width;
    height = corner_height;
    extent_source = "corners";
  } else if (valid_extent(scissor_width, scissor_height)) {
    width = scissor_width;
    height = scissor_height;
    extent_source = "scissor";
  } else if (valid_extent(viewport_width, viewport_height)) {
    width = viewport_width;
    height = viewport_height;
    extent_source = "VIEWPORT";
  } else {
    return false;
  }

  static uint32_t logged_extent_sources = 0;
  if (logged_extent_sources < 32u) {
    ++logged_extent_sources;
    REXLOG_INFO(
        "Refii plume private-rect extent #{} source={} -> {}x{} | "
        "corners {}x{} scissor {}x{} viewport {}x{} — a viewport is not an "
        "extent; source=VIEWPORT means no stated geometry was available",
        logged_extent_sources, extent_source, width, height, corner_width,
        corner_height, scissor_width, scissor_height, viewport_width,
        viewport_height);
  }
  return true;
}

bool DirectSetupClearCanMaterializePrivateRectTargetLocked(
    const FableDirectRenderCommand& command, uint32_t target,
    bool depth_target) {
  if (depth_target || command.type != FableDirectRenderCommandType::kClear ||
      target == 0 ||
      !IsFablePrivateTextureRectangleSetupClearLr(
          command.draw.state_flush_lr)) {
    return false;
  }
  if (target == g_direct_renderer.fable_scene_target ||
      IsDisplaySurfaceAliasTargetLocked(target)) {
    return false;
  }

  uint32_t width = 0;
  uint32_t height = 0;
  return ResolvePrivateRectTargetExtent(command, width, height);
}

void RegisterMaterializedPrivateRectTargetEdgeLocked(
    const FableDirectRenderCommand& command, const PlumeTrackedResource& target,
    uint32_t slot) {
  if (target.guest_address == 0 || !IsImageResource(target.kind) ||
      !PlumeGuestImageReady(target) ||
      (!target.texture_render_target && !target.texture_depth_target)) {
    return;
  }

  FableSurfaceTextureLink link;
  link.source_slot = 0xFFFFFFFFu;
  link.trusted_destination_edge = true;
  link.texture = target.guest_address;
  link.texture_base_address = PlumeGuestTextureBaseAddress(target);
  link.texture_width = PlumeGuestTextureWidth(target);
  link.texture_height = PlumeGuestTextureHeight(target);
  link.texture_format = PlumeGuestTextureGuestFormat(target);
  link.texture_fetch_dwords = PlumeGuestTextureFetchDwords(target);
  link.source_surface = target.guest_address;
  link.backing_texture = target.guest_address;
  link.backing_base_address = PlumeGuestTextureBaseAddress(target);
  link.backing_fetch_dwords = PlumeGuestTextureFetchDwords(target);
  link.backing_content_serial = FableTrackedResourceVisibleContentSerial(target);
  link.target_content_serial = FableTrackedResourceVisibleContentSerial(target);
  link.render_target =
      slot < command.resources.render_targets.size()
          ? command.resources.render_targets[slot]
          : command.resources.render_targets[0];
  link.depth_stencil = command.resources.depth_stencil;
  link.vertex_shader = command.resources.vertex_shader;
  link.pixel_shader = command.resources.pixel_shader;
  PopulateDirectSurfaceTextureLinkShaderRoots(link, command);
  link.frame_index = g_direct_renderer.current_frame_index;
  link.draw_serial = command.draw_serial != 0 ? command.draw_serial
                                              : command.serial;
  link.kind = DirectSyntheticPrivateRectCopyKindForTarget(command, target);
  RegisterDirectSurfaceTextureLinkPreparedLocked(link, "synthetic-self-edge");
}

bool EnsurePrivateRectRenderTargetSurfaceLocked(
    plume::RenderDevice* device, const FableDirectRenderCommand& command,
    uint32_t slot, bool remember_source_edges,
    const char* reason) {
  if (!device || slot >= command.resources.render_targets.size()) {
    return false;
  }
  const uint32_t target = command.resources.render_targets[slot];
  if (target == 0) {
    return false;
  }
  if (PlumeTrackedResource* existing = FindDirectResourceLocked(target);
      existing && PlumeGuestSurfaceReady(*existing)) {
    if (remember_source_edges &&
        !DirectSyntheticPrivateRectCommandUsesDepthTarget(command)) {
      RememberMaterializedPrivateRectTargetSourcesLocked(command, *existing);
    }
    return true;
  }
  if (!DirectCommandCanMaterializePrivateRectTargetLocked(command, target)) {
    return false;
  }

  uint32_t width = 0;
  uint32_t height = 0;
  if (!ResolvePrivateRectTargetExtent(command, width, height)) {
    return false;
  }

  const uint32_t render_target_info =
      command.pipeline.pm4.valid &&
              slot < command.pipeline.pm4.rb_color_info.size()
          ? command.pipeline.pm4.rb_color_info[slot]
          : 0u;

  FableGuestResourceSnapshot snapshot;
  snapshot.kind = FableGuestResourceKind::kSurface;
  snapshot.guest_address = target;
  snapshot.parent_address = target;
  snapshot.surface.object_address = target;
  snapshot.surface.parent_address = target;
  snapshot.surface.render_target_info = render_target_info;
  snapshot.surface.packed_extent = PackFableSurfaceExtent(width, height);
  snapshot.surface.d3d_format = render_target_info & 0x3Fu;
  snapshot.surface.pitch_msaa = width;
  snapshot.surface.byte_size = width * height * 4u;
  snapshot.serial = command.resource_serial != 0
                        ? command.resource_serial
                        : (command.draw_serial != 0 ? command.draw_serial
                                                    : command.serial);

  if (!SyncTrackedResourceLocked(device, snapshot, nullptr)) {
    return false;
  }
  g_direct_renderer.known_uploads[target] = UploadCacheEntryForSnapshot(snapshot);
  if (PlumeTrackedResource* tracked_target = FindDirectResourceLocked(target)) {
    if (!DirectSyntheticPrivateRectCommandUsesDepthTarget(command)) {
      RegisterMaterializedPrivateRectTargetEdgeLocked(command, *tracked_target,
                                                     slot);
      if (remember_source_edges) {
        RememberMaterializedPrivateRectTargetSourcesLocked(command,
                                                           *tracked_target);
      }
    }
  }
  if (g_direct_renderer.diagnostics.logged_synthetic_private_rect_targets < 32u) {
    const bool prepared = reason && std::strcmp(reason, "preflight") == 0;
    if (prepared) {
      REXLOG_INFO(
          "Refii plume direct renderer prepared private rect target #{} "
          "guest=0x{:08X} slot={} size={}x{} rt_info=0x{:08X} serial={}",
          ++g_direct_renderer.diagnostics.logged_synthetic_private_rect_targets, target,
          slot, width, height, render_target_info, snapshot.serial);
    } else {
      REXLOG_INFO(
          "Refii plume direct renderer materialized private rect target #{} "
          "guest=0x{:08X} slot={} size={}x{} rt_info=0x{:08X} serial={}",
          ++g_direct_renderer.diagnostics.logged_synthetic_private_rect_targets, target,
          slot, width, height, render_target_info, snapshot.serial);
    }
  }
  return true;
}

void PrepareDirectPrivateRectRenderTargetsForCommandLocked(
    plume::RenderDevice* device, const FableDirectRenderCommand& command) {
  if (!device) {
    return;
  }
  switch (command.type) {
    case FableDirectRenderCommandType::kDrawPrimitive:
    case FableDirectRenderCommandType::kDrawIndexedPrimitive:
    case FableDirectRenderCommandType::kDrawPrimitiveUP:
      break;
    default:
      return;
  }
  for (uint32_t slot = 0; slot < command.resources.render_targets.size();
       ++slot) {
    const uint32_t target = command.resources.render_targets[slot];
    if (!DirectCommandCanMaterializePrivateRectTargetLocked(command, target)) {
      continue;
    }
    EnsurePrivateRectRenderTargetSurfaceLocked(device, command, slot, true,
                                               "preflight");
  }
}

bool EnsurePrivateRectSetupClearSurfaceLocked(
    plume::RenderDevice* device, const FableDirectRenderCommand& command,
    uint32_t slot) {
  if (!device || slot >= command.resources.render_targets.size()) {
    return false;
  }
  if ((command.clear.flags & (1u << slot)) == 0) {
    return false;
  }
  const uint32_t target = command.resources.render_targets[slot];
  if (target == 0) {
    return false;
  }
  if (PlumeTrackedResource* existing = FindDirectResourceLocked(target);
      existing && PlumeGuestSurfaceReady(*existing)) {
    return true;
  }
  if (!DirectSetupClearCanMaterializePrivateRectTargetLocked(command, target,
                                                             false)) {
    return false;
  }

  uint32_t width = 0;
  uint32_t height = 0;
  if (!ResolvePrivateRectTargetExtent(command, width, height)) {
    return false;
  }

  const uint32_t render_target_info =
      command.pipeline.pm4.valid &&
              slot < command.pipeline.pm4.rb_color_info.size()
          ? command.pipeline.pm4.rb_color_info[slot]
          : 0u;

  FableGuestResourceSnapshot snapshot;
  snapshot.kind = FableGuestResourceKind::kSurface;
  snapshot.guest_address = target;
  snapshot.parent_address = target;
  snapshot.surface.object_address = target;
  snapshot.surface.parent_address = target;
  snapshot.surface.render_target_info = render_target_info;
  snapshot.surface.packed_extent = PackFableSurfaceExtent(width, height);
  snapshot.surface.d3d_format = render_target_info & 0x3Fu;
  snapshot.surface.pitch_msaa = width;
  snapshot.surface.byte_size = width * height * 4u;
  snapshot.serial = command.resource_serial != 0
                        ? command.resource_serial
                        : (command.draw_serial != 0 ? command.draw_serial
                                                    : command.serial);

  if (!SyncTrackedResourceLocked(device, snapshot, nullptr)) {
    return false;
  }
  g_direct_renderer.known_uploads[target] =
      UploadCacheEntryForSnapshot(snapshot);
  if (PlumeTrackedResource* tracked_target = FindDirectResourceLocked(target)) {
    RegisterMaterializedPrivateRectTargetEdgeLocked(command, *tracked_target,
                                                   slot);
  }
  if (g_direct_renderer.diagnostics.logged_setup_clear_private_rect_targets < 32u) {
    REXLOG_INFO(
        "Refii plume materialized private setup-clear target #{} frame={} "
        "guest=0x{:08X} slot={} size={}x{} rt_info=0x{:08X} "
        "lr=0x{:08X} serial={}",
        ++g_direct_renderer.diagnostics.logged_setup_clear_private_rect_targets,
        g_direct_renderer.current_frame_index, target, slot, width, height,
        render_target_info, command.draw.state_flush_lr, snapshot.serial);
  }
  return true;
}

bool PromoteDirectResourceToDepthTargetLocked(
    plume::RenderDevice* device, PlumeTrackedResource& target,
    const FableDirectRenderCommand& command, const char* reason) {
  if (!device || !IsImageResource(target.kind) ||
      !PlumeGuestImageReady(target) || target.guest_address == 0 ||
      target.guest_address != command.resources.depth_stencil ||
      PlumeGuestTextureWidth(target) == 0 ||
      PlumeGuestTextureHeight(target) == 0 ||
      target.texture_dimension != plume::RenderTextureDimension::TEXTURE_2D ||
      target.texture_view_dimension !=
          plume::RenderTextureViewDimension::TEXTURE_2D) {
    return false;
  }
  if (target.texture_depth_target && target.guest_surface.attachment_view) {
    return true;
  }

  plume::RenderFormat depth_format = PlumeGuestTextureFormat(target);
  if (!plume::RenderFormatIsDepth(depth_format)) {
    const uint32_t depth_info =
        command.pipeline.pm4.valid ? command.pipeline.pm4.rb_depth_info : 0u;
    depth_format = ConvertFableD3DDepthFormat(depth_info & 0x3Fu);
  }
  if (!IsUsablePlumeTextureFormat(depth_format) ||
      !plume::RenderFormatIsDepth(depth_format)) {
    depth_format = plume::RenderFormat::D32_FLOAT_S8_UINT;
  }

  PlumeTrackedResource retired;
  retired.kind = target.kind;
  retired.serial = target.serial;
  retired.guest_address = target.guest_address;
  retired.parent_address = target.parent_address;
  retired.guest_texture.texture = std::move(target.guest_texture.texture);
  retired.guest_texture.view = std::move(target.guest_texture.view);
  retired.guest_texture.mapped_views =
      std::move(target.guest_texture.mapped_views);
  retired.guest_surface.attachment_view =
      std::move(target.guest_surface.attachment_view);
  ForgetDirectTextureLayoutLocked(retired.guest_texture.texture.get());
  RetirePlumeResourceLocked(std::move(retired),
                            g_direct_renderer.current_frame_index);

  target.kind = FableGuestResourceKind::kDepthSurface;
  target.texture_format = depth_format;
  target.texture_guest_format =
      command.pipeline.pm4.valid ? (command.pipeline.pm4.rb_depth_info & 0x3Fu)
                                 : target.texture_guest_format;
  target.guest_texture.format = depth_format;
  target.guest_surface.format = depth_format;
  target.texture_render_target = false;
  target.texture_depth_target = true;
  target.texture_host_color_target = false;
  target.guest_texture.view.reset();
  target.guest_surface.attachment_view.reset();
  target.guest_texture.texture = device->createTexture(
      plume::RenderTextureDesc::DepthTarget(PlumeGuestTextureWidth(target),
                                            PlumeGuestTextureHeight(target),
                                            depth_format));
  if (!target.guest_texture.texture) {
    target.texture_depth_target = false;
    RefreshPlumeGuestResourceHandlesFromTrackedFields(target);
    return false;
  }
  StampPlumeLiveImageShape(target, PlumeGuestTextureWidth(target),
                           PlumeGuestTextureHeight(target));

  char name[256] = {};
  std::snprintf(name, sizeof(name),
                "Refii Fable exact depth target guest=0x%08X base=0x%08X "
                "%ux%u fmt=%u reason=%s",
                target.guest_address, PlumeGuestTextureBaseAddress(target),
                PlumeGuestTextureWidth(target), PlumeGuestTextureHeight(target),
                static_cast<uint32_t>(depth_format),
                reason ? reason : "private-rect-depth");
  target.guest_texture.texture->setName(name);
  ForgetDirectTextureLayoutLocked(target.guest_texture.texture.get());
  if (!RefreshDirectTextureSampleViewLocked(target)) {
    RefreshPlumeGuestResourceHandlesFromTrackedFields(target);
    return false;
  }
  target.guest_surface.attachment_view =
      target.guest_texture.texture->createTextureView(
          plume::RenderTextureViewDesc::Texture2D(depth_format));
  RefreshPlumeGuestResourceHandlesFromTrackedFields(target);
  RegisterDirectTextureIdentityFromResourceLocked(
      target, PlumeGuestTextureFetchDwords(target));
  InvalidateFableDescriptorCacheLocked();
  ++g_plume_resources.framebuffer_invalidation_generation;
  g_plume_resources.framebuffers.clear();

  static uint32_t logged_exact_depth_target_promotions = 0;
  if (logged_exact_depth_target_promotions < 32u) {
    REXLOG_INFO(
        "Refii plume exact private rect depth target identity #{} frame={} "
        "reason={} depth=0x{:08X} base=0x{:08X} {}x{} fmt={} "
        "rt=0x{:08X} ds=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
        ++logged_exact_depth_target_promotions,
        g_direct_renderer.current_frame_index,
        reason ? reason : "private-rect-depth", target.guest_address,
        PlumeGuestTextureBaseAddress(target), PlumeGuestTextureWidth(target),
        PlumeGuestTextureHeight(target), static_cast<uint32_t>(depth_format),
        command.resources.render_targets[0], command.resources.depth_stencil,
        command.resources.vertex_shader, command.resources.pixel_shader);
  }
  return target.guest_surface.attachment_view != nullptr;
}

bool EnsureDirectPrivateRectDepthTargetSurfaceLocked(
    plume::RenderDevice* device, const FableDirectRenderCommand& command,
    const char* reason) {
  if (!device || !DirectSyntheticPrivateRectCommandUsesDepthTarget(command)) {
    return false;
  }
  const uint32_t depth_target = command.resources.depth_stencil;
  if (depth_target == 0) {
    return false;
  }
  if (PlumeTrackedResource* existing = FindDirectResourceLocked(depth_target);
      existing && PromoteDirectResourceToDepthTargetLocked(device, *existing,
                                                           command, reason)) {
    return true;
  }

  uint32_t width = 0;
  uint32_t height = 0;
  if (!ResolvePrivateRectTargetExtent(command, width, height)) {
    return false;
  }

  const uint32_t depth_info =
      command.pipeline.pm4.valid ? command.pipeline.pm4.rb_depth_info : 0u;
  FableGuestResourceSnapshot snapshot;
  snapshot.kind = FableGuestResourceKind::kDepthSurface;
  snapshot.guest_address = depth_target;
  snapshot.parent_address = depth_target;
  snapshot.surface.object_address = depth_target;
  snapshot.surface.parent_address = depth_target;
  snapshot.surface.depth_stencil_info = depth_info;
  snapshot.surface.packed_extent = PackFableSurfaceExtent(width, height);
  snapshot.surface.d3d_format = depth_info & 0x3Fu;
  snapshot.surface.pitch_msaa = width;
  snapshot.surface.byte_size = width * height * 4u;
  snapshot.serial = command.resource_serial != 0
                        ? command.resource_serial
                        : (command.draw_serial != 0 ? command.draw_serial
                                                    : command.serial);
  if (!SyncTrackedResourceLocked(device, snapshot, nullptr)) {
    return false;
  }
  g_direct_renderer.known_uploads[depth_target] =
      UploadCacheEntryForSnapshot(snapshot);
  if (PlumeTrackedResource* tracked = FindDirectResourceLocked(depth_target)) {
    return PromoteDirectResourceToDepthTargetLocked(device, *tracked, command,
                                                   reason);
  }
  return false;
}

void EnsurePrivateRectSetupClearSurfacesLocked(
    plume::RenderDevice* device, const FableDirectRenderCommand& command) {
  if (command.type != FableDirectRenderCommandType::kClear ||
      !IsFablePrivateTextureRectangleSetupClearLr(
          command.draw.state_flush_lr)) {
    return;
  }
  const bool any_color_flags =
      (command.clear.flags & kFableClearColorTargetMask) != 0;
  for (uint32_t slot = 0; slot < command.resources.render_targets.size();
       ++slot) {
    if (any_color_flags && (command.clear.flags & (1u << slot)) == 0) {
      continue;
    }
    EnsurePrivateRectSetupClearSurfaceLocked(device, command, slot);
  }
}

DirectFramebufferBinding ResolveDirectFramebufferLocked(
    plume::RenderDevice* device, plume::RenderFramebuffer* output_framebuffer,
    plume::RenderFormat output_depth_format,
    const FableDirectRenderCommand& command) {
  DirectFramebufferBinding binding;
  binding.framebuffer = nullptr;
  binding.depth_format = output_depth_format;
  binding.width = output_framebuffer ? output_framebuffer->getWidth() : 0;
  binding.height = output_framebuffer ? output_framebuffer->getHeight() : 0;
  binding.color_count = 1;
  binding.color_formats[0] = binding.color_format;
  if (!device || !output_framebuffer) {
    return binding;
  }

  std::array<PlumeTrackedResource*, kMaxFableRenderTargets> colors = {};
  PlumeTrackedResource* first_color = nullptr;
  uint32_t highest_color_slot = UINT32_MAX;
  for (uint32_t slot = 0; slot < kMaxFableRenderTargets; ++slot) {
    EnsurePrivateRectRenderTargetSurfaceLocked(device, command, slot);
    PlumeTrackedResource* color =
        FindDirectResourceLocked(command.resources.render_targets[slot]);
    if (!color || !PlumeGuestSurfaceReady(*color)) {
      continue;
    }
    colors[slot] = color;
    if (!first_color) {
      first_color = color;
    }
    highest_color_slot = slot;
  }
  EnsureDirectPrivateRectDepthTargetSurfaceLocked(device, command,
                                                 "framebuffer");
  PlumeTrackedResource* depth =
      FindDirectResourceLocked(command.resources.depth_stencil);
  if (depth && (!PlumeGuestSurfaceReady(*depth) || !depth->texture_depth_target ||
                !depth->guest_surface.attachment_view)) {
    depth = nullptr;
  }
  if (depth && DirectSyntheticPrivateRectCommandUsesDepthTarget(command)) {
    if (!DirectDepthTargetHasExactPrivateRectActionLocked(command, *depth)) {
      RegisterMaterializedPrivateRectTargetEdgeLocked(command, *depth, 0);
      RememberMaterializedPrivateRectTargetSourcesLocked(command, *depth);
    }
  }
  if (!first_color && !depth) {
    return binding;
  }

  binding.color_count =
      first_color && highest_color_slot != UINT32_MAX ? highest_color_slot + 1u
                                                      : 0u;
  const uint32_t padding_width =
      first_color ? PlumeGuestTextureWidth(*first_color) : 0u;
  const uint32_t padding_height =
      first_color ? PlumeGuestTextureHeight(*first_color) : 0u;
  const plume::RenderFormat padding_format =
      first_color ? PlumeGuestTextureFormat(*first_color)
                  : plume::RenderFormat::UNKNOWN;
  for (uint32_t slot = 0; slot < binding.color_count; ++slot) {
    PlumeTrackedResource* color = colors[slot];
    if (color && color->guest_texture.texture &&
        (!color->texture_render_target ||
         !color->guest_surface.attachment_view)) {
      // The game is rendering into memory we first met as a sampled texture
      // (EDRAM alias). The backing image lacks RENDER_TARGET usage, and
      // VulkanFramebuffer asserts on such attachments. Reuse the exact-copy
      // promotion (retire + recreate as a color target with the format-safety
      // rules); a default link is kColor, which passes its blit gate.
      FableSurfaceTextureLink promote_link;
      if (!PromoteDirectExactCopyDestinationToColorTargetLocked(
              device, *color, promote_link)) {
        static uint32_t logged_framebuffer_promotion_failures = 0;
        if (logged_framebuffer_promotion_failures < 16u) {
          REXLOG_WARN(
              "Refii plume framebuffer color attachment 0x{:08X} slot={} is "
              "not render-target capable and promotion failed #{}; padding "
              "substituted (draws to it are discarded)",
              color->guest_address, slot,
              ++logged_framebuffer_promotion_failures);
        }
        color = nullptr;
      }
    }
    if (color) {
      binding.color_addresses[slot] = color->guest_address;
      binding.color_textures[slot] = color->guest_texture.texture.get();
      binding.color_views[slot] = color->guest_surface.attachment_view.get();
      binding.color_sample_views[slot] = color->guest_texture.view.get();
      binding.color_formats[slot] = PlumeGuestTextureFormat(*color);
      continue;
    }
    PlumeColorAttachmentPadding* padding =
        GetOrCreateDirectColorAttachmentPaddingLocked(
            device, padding_width, padding_height, padding_format);
    if (!padding || !padding->texture || !padding->view) {
      binding.framebuffer = nullptr;
      return binding;
    }
    binding.color_textures[slot] = padding->texture.get();
    binding.color_views[slot] = padding->view.get();
    binding.color_sample_views[slot] = padding->view.get();
    binding.color_formats[slot] = padding->format;
  }
  if (first_color) {
    binding.color_address = first_color->guest_address;
    binding.color_texture = first_color->guest_texture.texture.get();
    binding.color_view = first_color->guest_surface.attachment_view.get();
    binding.color_sample_view = first_color->guest_texture.view.get();
    binding.color_format = PlumeGuestTextureFormat(*first_color);
    binding.width = PlumeGuestTextureWidth(*first_color);
    binding.height = PlumeGuestTextureHeight(*first_color);
    // THE NUMERATOR MUST DESCRIBE THE IMAGE WE ARE ABOUT TO BIND (2026-08-04).
    //
    // binding.width above is METADATA. The attachment below is
    // first_color->guest_surface.attachment_view — a view onto the live image.
    // When a promotion landed after the image was materialized, the metadata
    // said 1680x1080 and the image was still 1120x720, so this numerator with
    // promoted_from_guest_* as the denominator produced a 1.5x scale onto an
    // image that had never grown, and we rasterized outside the attachment.
    // Measured run 596 frame 13696: sunglare, sundisc, sunbeams.
    //
    // DirectImageShapeForTrackedResource now reports the live extent, so the
    // shape-change retire recreates such images and this should not trigger.
    // It stays as the invariant that keeps a metadata bug off the GPU: the
    // viewport is computed from binding.width, so binding.width is only ever
    // allowed to be the real image extent.
    const uint32_t live_width = first_color->guest_texture.image_width;
    const uint32_t live_height = first_color->guest_texture.image_height;
    if (live_width != 0 && live_height != 0 &&
        (live_width != binding.width || live_height != binding.height)) {
      static uint32_t logged_live_extent_divergence = 0;
      if (logged_live_extent_divergence < 32u) {
        REXLOG_WARN(
            "Refii plume attachment extent DIVERGED #{} frame={} "
            "color=0x{:08X} metadata={}x{} live_image={}x{} "
            "promoted_from={}x{} — binding the live extent; the image was not "
            "recreated when its metadata was promoted",
            ++logged_live_extent_divergence,
            g_direct_renderer.current_frame_index, binding.color_address,
            binding.width, binding.height, live_width, live_height,
            first_color->promoted_from_guest_width,
            first_color->promoted_from_guest_height);
      }
      binding.width = live_width;
      binding.height = live_height;
    }
    // Guest-space denominator for the one scale factor. A surface that was
    // never promoted reports its own extent, so the ratio is exactly 1.
    binding.guest_width = first_color->promoted_from_guest_width != 0
                              ? first_color->promoted_from_guest_width
                              : binding.width;
    binding.guest_height = first_color->promoted_from_guest_height != 0
                               ? first_color->promoted_from_guest_height
                               : binding.height;
  }
  // UNDERSIZED DEPTH IS A DEVICE-LOSS FAULT, NOT A COSMETIC MISMATCH
  // (2026-08-03). This block used to bind the depth attachment without ever
  // comparing its extent against the colour attachment's — `binding` does not
  // even carry a depth extent — while the viewport and scissor are computed
  // from `binding.width/height`, i.e. the COLOUR extent (:45636-45674), and the
  // guest viewport is actively SCALED UP by colour/native. Rasterization then
  // addresses pixels outside the depth attachment.
  //
  // That is a hardware fault, and the NVIDIA kernel names it exactly:
  //   Xid 13 ... 3D WIDTH ZT Violation.  Coordinates: (0x1e0, ...)   [run 549]
  //   Xid 13 ... 3D HEIGHT ZT Violation. Coordinates: (0x40, 0x140)  [run 548]
  // "ZT" is the Zeta (depth/stencil) target. 0x1E0 = 480 and run 549 creates
  // 480x270 depth surfaces alongside 1920x1080 colour — the promotion ladder in
  // FablePromotedRenderSurfaceExtent maps guest shapes through 1x, 1/2 and 1/4
  // divisors (plus a no-op), so a colour/depth pair whose guest shapes land in
  // different buckets emerges with different host extents.
  //
  // Vulkan validation is SILENT on this by design: a viewport or scissor larger
  // than an attachment is not an API error, the spec merely leaves those
  // fragments undefined. Validation ran clean all of run 549 and the device
  // still died. Do not read validation silence as "not this".
  //
  // DROP the depth attachment rather than clamp the render area to it. Clamping
  // would shrink the COLOUR area to the depth target's size and render a
  // fraction of the scene; this function already treats depth as optional
  // everywhere else (a null depth is an ordinary, supported state and the
  // pipeline forces depth test/write off), so the degradation is bounded and
  // already-modelled. The real repair is upstream — the two producers of
  // unpromoted depth images, PromoteDirectResourceToDepthTargetLocked (:4329,
  // which never calls the output-extent promotion) and
  // EnsureDirectPrivateRectDepthTargetSurfaceLocked (raw guest-space extent) —
  // but a mismatch must never again reach the GPU as a fault.
  if (depth && first_color) {
    const uint32_t depth_width = PlumeGuestTextureWidth(*depth);
    const uint32_t depth_height = PlumeGuestTextureHeight(*depth);
    if (depth_width < binding.width || depth_height < binding.height) {
      static uint32_t logged_undersized_depth = 0;
      if (logged_undersized_depth < 32u) {
        REXLOG_WARN(
            "Refii plume framebuffer DROPPING undersized depth #{} frame={} "
            "color=0x{:08X} {}x{} depth=0x{:08X} {}x{} — binding it would "
            "rasterize outside the depth target (Xid 13 ZT violation)",
            ++logged_undersized_depth, g_direct_renderer.current_frame_index,
            binding.color_address, binding.width, binding.height,
            depth->guest_address, depth_width, depth_height);
      }
      depth = nullptr;
    }
  }
  if (depth) {
    // Name the live depth attachment for the stale sweep. FindDirectResource
    // Locked above already stamped last_referenced_frame, so the TTL alone
    // protects it; this is the belt-and-braces guard so an eviction can never
    // pull the attachment out from under a binding even if the stamp path
    // changes.
    g_direct_renderer.last_bound_depth_target = depth->guest_address;
    g_direct_renderer.last_bound_depth_frame =
        g_direct_renderer.current_frame_index;
    binding.depth_address = depth->guest_address;
    binding.depth_texture = depth->guest_texture.texture.get();
    binding.depth_view = depth->guest_surface.attachment_view.get();
    binding.depth_format = PlumeGuestTextureFormat(*depth);
    if (!first_color) {
      binding.width = PlumeGuestTextureWidth(*depth);
      binding.height = PlumeGuestTextureHeight(*depth);
      binding.guest_width = depth->promoted_from_guest_width != 0
                                ? depth->promoted_from_guest_width
                                : binding.width;
      binding.guest_height = depth->promoted_from_guest_height != 0
                                 ? depth->promoted_from_guest_height
                                 : binding.height;
    }
  } else {
    binding.depth_format = plume::RenderFormat::UNKNOWN;
  }
  if (binding.color_address != 0) {
    RememberFableSceneTargetLocked(binding.color_address, binding.width,
                                    binding.height, "framebuffer");
  }

  const bool stable_title_or_private_framebuffer =
      command.draw.private_texture_rectangle ||
      IsFableTitleSceneTargetLocked(command.resources.render_targets[0]) ||
      IsFableTitleSceneTargetLocked(binding.color_address);
  const std::string key =
      stable_title_or_private_framebuffer
          ? DirectStableAttachmentFramebufferKey(binding, output_framebuffer)
          : DirectFramebufferKey(colors, binding.color_count, depth,
                                 output_framebuffer);
  auto framebuffer_it = g_plume_resources.framebuffers.find(key);
  if (framebuffer_it != g_plume_resources.framebuffers.end()) {
    binding.framebuffer = framebuffer_it->second.get();
    return binding;
  }

  if (g_plume_resources.framebuffers.size() >= kMaxFableFramebufferCacheEntries) {
    RetireFableFramebuffersLocked();
  }

  std::array<const plume::RenderTexture*, kMaxFableRenderTargets>
      color_textures = {};
  std::array<const plume::RenderTextureView*, kMaxFableRenderTargets>
      color_views = {};
  for (uint32_t slot = 0; slot < binding.color_count; ++slot) {
    color_textures[slot] = binding.color_textures[slot];
    color_views[slot] = binding.color_views[slot];
  }
  plume::RenderFramebufferDesc desc;
  desc.colorAttachments = color_textures.data();
  desc.colorAttachmentViews = color_views.data();
  desc.colorAttachmentsCount = binding.color_count;
  desc.depthAttachment = binding.depth_texture;
  desc.depthAttachmentView = binding.depth_view;
  std::unique_ptr<plume::RenderFramebuffer> framebuffer =
      device->createFramebuffer(desc);
  if (!framebuffer) {
    binding.framebuffer = nullptr;
    return binding;
  }

  binding.framebuffer = framebuffer.get();
  g_plume_resources.framebuffers.emplace(key, std::move(framebuffer));
  if (!g_direct_renderer.diagnostics.logged_first_scene_framebuffer) {
    REXLOG_INFO(
        "Refii plume direct renderer created Fable scene framebuffer "
        "color=0x{:08X} depth=0x{:08X} {}x{}",
        binding.color_address, binding.depth_address, binding.width,
        binding.height);
    g_direct_renderer.diagnostics.logged_first_scene_framebuffer = true;
  }
  return binding;
}

// Distinct mapped_views key namespace for per-mip render-attachment views
// (audit R1). The cache lives ON the resource, so keying by level here keys
// (texture, level) overall and the views retire with the texture like every
// other mapped view.

bool ResolveDirectSingleColorFramebufferForResourceLocked(
    plume::RenderDevice* device, PlumeTrackedResource& target,
    DirectFramebufferBinding& binding, uint32_t mip_level) {
  binding = {};
  if (!device || !PlumeGuestImageReady(target) || !target.guest_texture.texture) {
    return false;
  }

  if (!target.texture_render_target || !target.texture_host_color_target) {
    // The feedback alias renders into a texture first met as sampled-only;
    // VulkanFramebuffer asserts on attachments without RENDER_TARGET usage.
    // texture_render_target alone is NOT proof of that usage — refresh-path
    // metadata decodes and copy-destination hints flip it on live resources
    // without a recreate (runs 470/471 SIGABRT) — so require the host flag,
    // which only the creation sites write.
    // Promote (retire + recreate as a color target) before binding; promotion
    // may change the texture format, so it must precede the format read.
    FableSurfaceTextureLink promote_link;
    if (!PromoteDirectExactCopyDestinationToColorTargetLocked(device, target,
                                                              promote_link)) {
      return false;
    }
  }

  const plume::RenderFormat format = PlumeGuestTextureFormat(target);
  const uint32_t width = PlumeGuestTextureWidth(target);
  const uint32_t height = PlumeGuestTextureHeight(target);
  if (format == plume::RenderFormat::UNKNOWN || width == 0 || height == 0) {
    return false;
  }

  plume::RenderTextureView* attachment_view = nullptr;
  if (mip_level == 0) {
    if (!target.guest_surface.attachment_view) {
      target.guest_surface.attachment_view =
          target.guest_texture.texture->createTextureView(
              plume::RenderTextureViewDesc::Texture2D(format));
      RefreshPlumeGuestResourceHandlesFromTrackedFields(target);
    }
    attachment_view = target.guest_surface.attachment_view.get();
  } else {
    // Audit R1: the guest's mip-chain filler (sub_821FC5D0) resolves
    // DestLevel=1..N-1 (render-to-texture + texture bake). Attaching the
    // level-0 view here wrote mip 0 at the wrong scale for those resolves;
    // attach the requested level instead. Framebuffer attachments need
    // exactly one level, and the level must exist on the HOST image.
    if (mip_level >= std::max<uint32_t>(1u, target.texture_mip_levels)) {
      return false;
    }
    const uint32_t view_key = kDirectResolveMipAttachmentViewKey ^
                              (mip_level << 8u) ^
                              static_cast<uint32_t>(format);
    auto view_it = target.guest_texture.mapped_views.find(view_key);
    if (view_it != target.guest_texture.mapped_views.end() &&
        view_it->second) {
      attachment_view = view_it->second.get();
    } else {
      plume::RenderTextureViewDesc view_desc =
          plume::RenderTextureViewDesc::Texture2D(format);
      view_desc.mipSlice = mip_level;
      view_desc.mipLevels = 1;
      std::unique_ptr<plume::RenderTextureView> view =
          target.guest_texture.texture->createTextureView(view_desc);
      if (!view) {
        return false;
      }
      attachment_view = view.get();
      target.guest_texture.mapped_views[view_key] = std::move(view);
    }
  }
  if (!attachment_view) {
    return false;
  }

  // Dimensions of the attached level (min 1), mirroring the copy path's
  // Subresource math.
  const uint32_t level_width = std::max(1u, width >> mip_level);
  const uint32_t level_height = std::max(1u, height >> mip_level);

  binding.color_count = 1;
  binding.color_address = target.guest_address;
  binding.color_addresses[0] = target.guest_address;
  binding.color_texture = target.guest_texture.texture.get();
  binding.color_textures[0] = target.guest_texture.texture.get();
  binding.color_view = attachment_view;
  binding.color_views[0] = attachment_view;
  binding.color_sample_view = target.guest_texture.view.get();
  binding.color_sample_views[0] = target.guest_texture.view.get();
  binding.color_format = format;
  binding.color_formats[0] = format;
  binding.depth_format = plume::RenderFormat::UNKNOWN;
  binding.width = level_width;
  binding.height = level_height;

  char key_buffer[192] = {};
  std::snprintf(key_buffer, sizeof(key_buffer),
                "fable-direct-feedback-alias-fb:0x%08X:%p:%p:%ux%u:%u:m%u",
                target.guest_address,
                static_cast<const void*>(binding.color_texture),
                static_cast<const void*>(binding.color_view), level_width,
                level_height, static_cast<uint32_t>(format), mip_level);
  const std::string key(key_buffer);
  if (auto it = g_plume_resources.framebuffers.find(key);
      it != g_plume_resources.framebuffers.end() && it->second) {
    binding.framebuffer = it->second.get();
    return true;
  }
  if (g_plume_resources.framebuffers.size() >= kMaxFableFramebufferCacheEntries) {
    RetireFableFramebuffersLocked();
  }

  std::array<const plume::RenderTexture*, kMaxFableRenderTargets>
      color_textures = {};
  std::array<const plume::RenderTextureView*, kMaxFableRenderTargets>
      color_views = {};
  color_textures[0] = binding.color_texture;
  color_views[0] = binding.color_view;

  plume::RenderFramebufferDesc desc;
  desc.colorAttachments = color_textures.data();
  desc.colorAttachmentViews = color_views.data();
  desc.colorAttachmentsCount = 1;
  std::unique_ptr<plume::RenderFramebuffer> framebuffer =
      device->createFramebuffer(desc);
  if (!framebuffer) {
    return false;
  }

  binding.framebuffer = framebuffer.get();
  g_plume_resources.framebuffers.emplace(key, std::move(framebuffer));
  return true;
}

bool CreatePresentBlitShaderForFormat(
    plume::RenderDevice* device, plume::RenderShaderFormat shader_format,
    bool vertex_shader, std::unique_ptr<plume::RenderShader>& shader) {
  const auto name_shader = [&]() {
    if (shader) {
      shader->setName(vertex_shader ? "Refii Fable direct blit VS"
                                    : "Refii Fable direct blit PS");
    }
    return shader != nullptr;
  };
  switch (shader_format) {
    case plume::RenderShaderFormat::SPIRV:
      shader = device->createShader(
          vertex_shader ? plume_present_blit_vsBlobSPIRV
                        : plume_present_blit_psBlobSPIRV,
          vertex_shader ? plume_present_blit_vsBlobSPIRV_size
                        : plume_present_blit_psBlobSPIRV_size,
          vertex_shader ? "VSMain" : "PSMain", shader_format);
      return name_shader();
#if defined(_WIN64)
    case plume::RenderShaderFormat::DXIL:
      shader = device->createShader(
          vertex_shader ? plume_present_blit_vsBlobDXIL
                        : plume_present_blit_psBlobDXIL,
          vertex_shader ? plume_present_blit_vsBlobDXIL_size
                        : plume_present_blit_psBlobDXIL_size,
          vertex_shader ? "VSMain" : "PSMain", shader_format);
      return name_shader();
#endif
    default:
      return false;
  }
}

bool EnsurePresentBlitResourcesLocked(
    plume::RenderDevice* device, plume::RenderShaderFormat shader_format) {
  if (!device) {
    return false;
  }
  auto fail = [&](const char* step) {
    static uint32_t logged_present_blit_resource_failures = 0;
    if (logged_present_blit_resource_failures < 32u) {
      REXLOG_WARN(
          "Refii plume direct renderer could not create present blit "
          "resources step={} shader_format={}",
          step, static_cast<uint32_t>(shader_format));
      ++logged_present_blit_resource_failures;
    }
    return false;
  };
  if (g_plume_resources.present_blit_pipeline &&
      g_plume_resources.present_blit_pipeline_layout &&
      g_plume_resources.present_blit_sampler_set &&
      g_plume_resources.present_blit_sampler &&
      g_plume_resources.present_blit_shader_format == shader_format) {
    return true;
  }

  g_plume_resources.present_blit_shader_format = shader_format;
  g_plume_resources.present_blit_pipeline_layout.reset();
  g_plume_resources.present_blit_pipeline.reset();
  g_plume_resources.present_blit_texture_sets.clear();
  g_plume_resources.present_blit_texture_set_index = 0;
  g_plume_resources.present_blit_texture_set_textures = {};
  g_plume_resources.present_blit_texture_set_views = {};
  g_plume_resources.present_blit_texture_set_layouts = {};
  g_plume_resources.present_blit_sampler_set.reset();
  g_plume_resources.present_blit_sampler.reset();

  plume::RenderDescriptorSetBuilder texture_set_builder;
  texture_set_builder.begin();
  texture_set_builder.addTexture(0, 1);
  texture_set_builder.end();
  plume::RenderDescriptorSetBuilder sampler_set_builder;
  sampler_set_builder.begin();
  sampler_set_builder.addSampler(0, 1);
  sampler_set_builder.end();

  plume::RenderPipelineLayoutBuilder layout_builder;
  layout_builder.begin(false, false);
  layout_builder.addDescriptorSet(texture_set_builder);
  layout_builder.addDescriptorSet(sampler_set_builder);
  if (shader_format == plume::RenderShaderFormat::SPIRV) {
    layout_builder.addPushConstant(
        0, 2, sizeof(PlumePresentBlitPushConstants),
        plume::RenderShaderStageFlag::PIXEL);
  } else {
    layout_builder.addRootDescriptor(
        0, 2, plume::RenderRootDescriptorType::CONSTANT_BUFFER);
  }
  layout_builder.end();
  g_plume_resources.present_blit_pipeline_layout =
      layout_builder.create(device);
  if (!g_plume_resources.present_blit_pipeline_layout) {
    return fail("pipeline_layout");
  }

  for (uint32_t i = 0; i < kPresentBlitDescriptorSetRingSize; ++i) {
    std::unique_ptr<plume::RenderDescriptorSet> descriptor_set =
        texture_set_builder.create(device);
    if (!descriptor_set) {
      return fail("texture_descriptor_set");
    }
    g_plume_resources.present_blit_texture_sets.emplace_back(
        std::move(descriptor_set));
  }
  g_plume_resources.present_blit_sampler_set =
      sampler_set_builder.create(device);
  g_plume_resources.present_blit_sampler =
      device->createSampler(plume::RenderSamplerDesc());
  if (!g_plume_resources.present_blit_sampler_set ||
      !g_plume_resources.present_blit_sampler) {
    return fail("sampler_descriptor_set");
  }
  g_plume_resources.present_blit_sampler_set->setSampler(
      0, g_plume_resources.present_blit_sampler.get());

  std::unique_ptr<plume::RenderShader> vertex_shader;
  std::unique_ptr<plume::RenderShader> pixel_shader;
  if (!CreatePresentBlitShaderForFormat(device, shader_format, true,
                                        vertex_shader) ||
      !CreatePresentBlitShaderForFormat(device, shader_format, false,
                                        pixel_shader)) {
    return fail("shader");
  }

  plume::RenderGraphicsPipelineDesc desc;
  desc.pipelineLayout = g_plume_resources.present_blit_pipeline_layout.get();
  desc.vertexShader = vertex_shader.get();
  desc.pixelShader = pixel_shader.get();
  desc.renderTargetFormat[0] = plume::RenderFormat::B8G8R8A8_UNORM;
  desc.renderTargetBlend[0] = plume::RenderBlendDesc::Copy();
  desc.renderTargetCount = 1;
  desc.primitiveTopology = plume::RenderPrimitiveTopology::TRIANGLE_LIST;
  desc.cullMode = plume::RenderCullMode::NONE;
  g_plume_resources.present_blit_pipeline =
      device->createGraphicsPipeline(desc);
  if (!g_plume_resources.present_blit_pipeline) {
    return fail("pipeline");
  }
  g_plume_resources.present_blit_pipeline->setName(
      "Refii Fable direct present blit");
  return true;
}


DirectPresentSource PresentSourceFromResourceLocked(uint32_t address) {
  DirectPresentSource source;
  PlumeTrackedResource* resource = FindDirectResourceLocked(address);
  if (!resource || !PlumeGuestImageReady(*resource)) {
    return source;
  }
  source.texture = resource->guest_texture.texture.get();
  source.view = resource->guest_texture.view.get();
  source.target = resource->guest_address;
  source.width = PlumeGuestTextureWidth(*resource);
  source.height = PlumeGuestTextureHeight(*resource);
  source.texture_width = source.width;
  source.texture_height = source.height;
  source.frame_index = g_direct_renderer.current_frame_index;
  source.layout = resource->texture_render_target
                      ? plume::RenderTextureLayout::COLOR_WRITE
                      : plume::RenderTextureLayout::SHADER_READ;
  source.write_serial = FableTrackedResourceVisibleContentSerial(*resource);
  source.format = resource->texture_format;
  return source;
}

FableTitleFramePacketTargetFactsList DirectTitleFramePacketTargetFactsLocked(
    const FableDirectFrameDatabaseDrawPacket& packet) {
  FableTitleFramePacketTargetFactsList facts;
  for (uint32_t slot = 0; slot < packet.render_targets.size(); ++slot) {
    FableTitleFramePacketTargetFacts& target = facts[slot];
    target.target = packet.render_targets[slot];
    target.title_scene_target = IsFableTitleSceneTargetLocked(target.target);

    const PlumeTrackedResource* resource =
        FindDirectResourceLocked(target.target);
    if (!resource || !IsImageResource(resource->kind) ||
        !PlumeGuestImageReady(*resource)) {
      continue;
    }
    target.image_ready = true;
    target.width = PlumeGuestTextureWidth(*resource);
    target.height = PlumeGuestTextureHeight(*resource);
    target.scene_target_candidate =
        FableTitleSceneTargetCandidate(target.target, target.width,
                                       target.height);
    target.present_target_candidate =
        FableTitlePresentTargetCandidate(target.target, target.width,
                                         target.height);
  }
  return facts;
}

FableTitleFramePacketShaderFacts DirectTitleFramePacketShaderFactsLocked(
    const FableDirectFrameDatabaseDrawPacket& packet) {
  FableTitleFramePacketShaderFacts facts;
  facts.title_indexed_declaration = DirectVertexDeclarationMatchesHashLocked(
      packet.vertex_declaration, kFableTitleIndexedDeclarationHash);
  facts.title_billboard_mesh_declaration =
      DirectVertexDeclarationMatchesHashLocked(
          packet.vertex_declaration, kFableTitleBillboardMeshDeclarationHash);
  facts.title_gui_quad_declaration =
      IsFableTitleGuiQuadDeclaration(packet.vertex_declaration);
  facts.title_scene_vertex_shader =
      IsFableTitleSceneVertexShader(packet.vertex_shader);
  facts.title_gui_text_3d_pixel_shader_address =
      IsFableTitleGuiText3DPixelShaderAddress(packet.pixel_shader);
  facts.synthetic_private_rect_copy_shader =
      IsFableSyntheticPrivateRectCopyShader(packet.pixel_shader);
  facts.vertex_hash = DirectVertexShaderHashLocked(packet.vertex_shader);
  facts.pixel_hash = DirectPixelShaderHashLocked(packet.pixel_shader);
  return facts;
}

FableTitlePresentSourceFacts DirectTitlePresentSourceFactsLocked(
    const DirectPresentSource& source) {
  return {
      .source_ready = source.texture && source.view,
      .frame_matches =
          source.frame_index == g_direct_renderer.current_frame_index,
      .target = source.target,
      .write_serial = source.write_serial,
  };
}


bool DirectPresentSourceHasSameFrameDrawProducerLocked(
    const DirectPresentSource& source,
    DirectFrameDatabaseDrawPacketProducerKind producer_kind) {
  if (!FableTitlePresentSourceCanUseSameFrameProducer(
          DirectTitlePresentSourceFactsLocked(source))) {
    return false;
  }
  for (const FableDirectFrameDatabaseDrawPacket& packet :
       g_direct_renderer.frame_database_draw_packets) {
    const FableTitleFramePacketTargetFactsList target_facts =
        DirectTitleFramePacketTargetFactsLocked(packet);
    const FableTitleFramePacketShaderFacts shader_facts =
        DirectTitleFramePacketShaderFactsLocked(packet);
    const FableTitleFramePacketDerivedFacts derived_facts =
        BuildFableTitleFramePacketDerivedFacts(packet, target_facts,
                                              shader_facts);
    bool producer_matches = false;
    switch (producer_kind) {
      case DirectFrameDatabaseDrawPacketProducerKind::kPresentColor:
        producer_matches = derived_facts.can_produce_present_color;
        break;
      case DirectFrameDatabaseDrawPacketProducerKind::kTitlePresent:
        producer_matches = derived_facts.title_present_producer;
        break;
      case DirectFrameDatabaseDrawPacketProducerKind::kFullScene:
        producer_matches = derived_facts.full_scene_producer;
        break;
      case DirectFrameDatabaseDrawPacketProducerKind::kTitleVisible:
        producer_matches = derived_facts.visible_producer;
        break;
    }
    if (!producer_matches) {
      continue;
    }
    if (FableTitleFramePacketTargetsContain(packet, source.target)) {
      return true;
    }
  }
  return false;
}

bool DirectPresentSourceHasSameFrameResolveProducerLocked(
    const DirectPresentSource& source) {
  for (const FableDirectFrameDatabaseCommandPacket& packet :
       g_direct_renderer.frame_database_command_packets) {
    if (packet.kind == "resolve" && packet.resolve_dest_base == source.target) {
      return true;
    }
  }
  return false;
}

bool DirectPresentSourceHasSameFrameTitlePresentProducerLocked(
    const DirectPresentSource& source) {
  return DirectPresentSourceHasSameFrameDrawProducerLocked(
      source, DirectFrameDatabaseDrawPacketProducerKind::kTitlePresent);
}

bool DirectPresentSourceHasSameFrameFullSceneProducerLocked(
    const DirectPresentSource& source) {
  return DirectPresentSourceHasSameFrameDrawProducerLocked(
             source, DirectFrameDatabaseDrawPacketProducerKind::kFullScene) ||
         DirectPresentSourceHasSameFrameResolveProducerLocked(source);
}

bool DirectPresentSourceHasSameFrameTitleVisibleProducerLocked(
    const DirectPresentSource& source) {
  return DirectPresentSourceHasSameFrameDrawProducerLocked(
      source, DirectFrameDatabaseDrawPacketProducerKind::kTitleVisible);
}

plume::RenderTextureViewDesc SampleTextureViewDescForTrackedResource(
    const PlumeTrackedResource& tracked) {
  plume::RenderTextureViewDesc view_desc =
      TextureViewDescForDimension(tracked.texture_view_dimension,
                                  PlumeGuestTextureFormat(tracked));
  if (IsSurfaceResource(tracked.kind)) {
    view_desc.componentMapping = IdentityTextureComponentMapping();
    return view_desc;
  }

  namespace xenos = rex::graphics::xenos;
  const auto& fetch_dwords = PlumeGuestTextureFetchDwords(tracked);
  xenos::xe_gpu_texture_fetch_t fetch = {};
  fetch.dword_0 = fetch_dwords[0];
  fetch.dword_1 = fetch_dwords[1];
  fetch.dword_2 = fetch_dwords[2];
  fetch.dword_3 = fetch_dwords[3];
  fetch.dword_4 = fetch_dwords[4];
  fetch.dword_5 = fetch_dwords[5];
  if (fetch.type == xenos::FetchConstantType::kTexture ||
      fetch.type == xenos::FetchConstantType::kInvalidTexture) {
    view_desc.componentMapping =
        XenosTextureComponentMapping(PlumeGuestTextureGuestFormat(tracked),
                                     fetch.swizzle);
  } else {
    view_desc.componentMapping = IdentityTextureComponentMapping();
  }
  return view_desc;
}

uint32_t TextureViewMappingKeyForTrackedResource(
    const PlumeTrackedResource& tracked) {
  namespace xenos = rex::graphics::xenos;
  uint32_t fetch_swizzle = UINT32_MAX;
  uint32_t fetch_type = UINT32_MAX;
  if (!IsSurfaceResource(tracked.kind)) {
    const auto& fetch_dwords = PlumeGuestTextureFetchDwords(tracked);
    xenos::xe_gpu_texture_fetch_t fetch = {};
    fetch.dword_0 = fetch_dwords[0];
    fetch.dword_1 = fetch_dwords[1];
    fetch.dword_2 = fetch_dwords[2];
    fetch.dword_3 = fetch_dwords[3];
    fetch.dword_4 = fetch_dwords[4];
    fetch.dword_5 = fetch_dwords[5];
    fetch_type = static_cast<uint32_t>(fetch.type);
    if (fetch.type == xenos::FetchConstantType::kTexture ||
        fetch.type == xenos::FetchConstantType::kInvalidTexture) {
      fetch_swizzle = fetch.swizzle;
    }
  }

  uint32_t key = 2166136261u;
  auto mix = [&](uint32_t value) {
    key ^= value;
    key *= 16777619u;
  };
  mix(static_cast<uint32_t>(tracked.texture_view_dimension));
  mix(static_cast<uint32_t>(PlumeGuestTextureFormat(tracked)));
  mix(PlumeGuestTextureGuestFormat(tracked) & 0x3Fu);
  mix(fetch_type);
  mix(fetch_swizzle);
  return key;
}

bool RefreshDirectTextureSampleViewLocked(PlumeTrackedResource& tracked) {
  if (!tracked.guest_texture.texture) {
    return false;
  }
  const uint32_t view_key = TextureViewMappingKeyForTrackedResource(tracked);
  if (tracked.guest_texture.view && tracked.guest_texture.view_mapping_key ==
                                        view_key) {
    return true;
  }

  const bool replacing_view = tracked.guest_texture.view != nullptr;
  std::unique_ptr<plume::RenderTextureView> view =
      tracked.guest_texture.texture->createTextureView(
          SampleTextureViewDescForTrackedResource(tracked));
  if (!view) {
    return false;
  }
  if (replacing_view) {
    g_direct_renderer.retained_replaced_texture_views.push_back(
        {g_direct_renderer.current_frame_index,
         std::move(tracked.guest_texture.view)});
  }
  tracked.guest_texture.view = std::move(view);
  tracked.guest_texture.view_mapping_key = view_key;
  RefreshPlumeGuestResourceHandlesFromTrackedFields(tracked);
  if (replacing_view) {
    InvalidateFableDescriptorCacheLocked();
  }
  return true;
}

plume::RenderTextureView* DirectBinkPlaneIdentitySampleViewLocked(
    PlumeTrackedResource& tracked) {
  if (!tracked.guest_texture.texture ||
      tracked.texture_view_dimension !=
          plume::RenderTextureViewDimension::TEXTURE_2D) {
    return tracked.guest_texture.view.get();
  }

  constexpr uint32_t kDirectBinkPlaneIdentityViewKey = 0xB1140000u;
  const uint32_t key =
      kDirectBinkPlaneIdentityViewKey ^
      (static_cast<uint32_t>(tracked.texture_view_dimension) << 16u) ^
      static_cast<uint32_t>(PlumeGuestTextureFormat(tracked));
  auto view_it = tracked.guest_texture.mapped_views.find(key);
  if (view_it != tracked.guest_texture.mapped_views.end() &&
      view_it->second) {
    return view_it->second.get();
  }

  plume::RenderTextureViewDesc view_desc = TextureViewDescForDimension(
      tracked.texture_view_dimension, PlumeGuestTextureFormat(tracked));
  view_desc.componentMapping = IdentityTextureComponentMapping();
  std::unique_ptr<plume::RenderTextureView> view =
      tracked.guest_texture.texture->createTextureView(view_desc);
  if (!view) {
    return tracked.guest_texture.view.get();
  }

  plume::RenderTextureView* result = view.get();
  tracked.guest_texture.mapped_views[key] = std::move(view);
  return result;
}

bool PromoteDirectExactCopyDestinationToColorTargetLocked(
    plume::RenderDevice* device, PlumeTrackedResource& target,
    FableSurfaceTextureLink link) {
  if (!device || !IsImageResource(target.kind) || target.texture_depth_target ||
      !PlumeGuestImageReady(target) ||
      !DirectPrivateTextureCopyKindCanBlitToColorTarget(link.kind) ||
      target.guest_texture.texture.get() == nullptr ||
      PlumeGuestTextureWidth(target) == 0 || PlumeGuestTextureHeight(target) == 0 ||
      target.texture_dimension != plume::RenderTextureDimension::TEXTURE_2D ||
      target.texture_view_dimension != plume::RenderTextureViewDimension::TEXTURE_2D) {
    return false;
  }
  if (target.texture_render_target && target.texture_host_color_target &&
      target.guest_surface.attachment_view) {
    return true;
  }

  // A destination whose identity came from a depth-sample fetch constant
  // (k_24_8 / k_24_8_FLOAT) converts to single-channel R32_FLOAT, which is
  // technically color-target usable but destroys RGB for Fable's k_8_8_8_8
  // copy/present chain.
  namespace xenos = rex::graphics::xenos;
  const uint32_t guest_copy_format = PlumeGuestTextureGuestFormat(target) & 0x3Fu;
  const bool depth_derived_copy_identity =
      guest_copy_format == static_cast<uint32_t>(xenos::TextureFormat::k_24_8) ||
      guest_copy_format ==
          static_cast<uint32_t>(xenos::TextureFormat::k_24_8_FLOAT);
  plume::RenderFormat color_target_format =
      depth_derived_copy_identity ? plume::RenderFormat::R8G8B8A8_UNORM
                                  : target.texture_format;
  if (!IsUsablePlumeColorTargetFormat(color_target_format)) {
    color_target_format = PlumeGuestTextureFormat(target);
  }
  if (depth_derived_copy_identity ||
      !IsUsablePlumeColorTargetFormat(color_target_format)) {
    color_target_format = plume::RenderFormat::R8G8B8A8_UNORM;
  }
  target.texture_format = color_target_format;
  target.guest_texture.format = color_target_format;
  target.guest_surface.format = color_target_format;

  PlumeTrackedResource retired;
  retired.kind = target.kind;
  retired.serial = target.serial;
  retired.guest_address = target.guest_address;
  retired.parent_address = target.parent_address;
  retired.guest_texture.texture = std::move(target.guest_texture.texture);
  retired.guest_texture.view = std::move(target.guest_texture.view);
  retired.guest_texture.mapped_views =
      std::move(target.guest_texture.mapped_views);
  retired.guest_surface.attachment_view =
      std::move(target.guest_surface.attachment_view);
  ForgetDirectTextureLayoutLocked(retired.guest_texture.texture.get());
  RetirePlumeResourceLocked(std::move(retired),
                            g_direct_renderer.current_frame_index);

  target.texture_render_target = true;
  target.texture_depth_target = false;
  target.texture_host_color_target = false;
  target.guest_texture.view.reset();
  target.guest_surface.attachment_view.reset();
  target.guest_texture.texture = device->createTexture(
      plume::RenderTextureDesc::ColorTarget(PlumeGuestTextureWidth(target),
                                            PlumeGuestTextureHeight(target),
                                            PlumeGuestTextureFormat(target)));
  if (!target.guest_texture.texture) {
    target.texture_render_target = false;
    RefreshPlumeGuestResourceHandlesFromTrackedFields(target);
    return false;
  }
  StampPlumeLiveImageShape(target, PlumeGuestTextureWidth(target),
                           PlumeGuestTextureHeight(target));
  target.texture_host_color_target = true;

  char name[160] = {};
  std::snprintf(name, sizeof(name),
                "Refii Fable exact copy destination guest=0x%08X base=0x%08X %ux%u fmt=%u source=0x%08X",
                target.guest_address, PlumeGuestTextureBaseAddress(target),
                PlumeGuestTextureWidth(target), PlumeGuestTextureHeight(target),
                static_cast<uint32_t>(PlumeGuestTextureFormat(target)),
                link.source_surface);
  target.guest_texture.texture->setName(name);
  ForgetDirectTextureLayoutLocked(target.guest_texture.texture.get());
  if (!RefreshDirectTextureSampleViewLocked(target)) {
    return false;
  }
  target.guest_surface.attachment_view =
      target.guest_texture.texture->createTextureView(
          plume::RenderTextureViewDesc::Texture2D(
              PlumeGuestTextureFormat(target)));
  RefreshPlumeGuestResourceHandlesFromTrackedFields(target);
  RegisterDirectTextureIdentityFromResourceLocked(
      target, PlumeGuestTextureFetchDwords(target));
  InvalidateFableDescriptorCacheLocked();

  static uint32_t logged_exact_copy_destination_promotions = 0;
  if (RendererVerboseTracingEnabled() &&
      logged_exact_copy_destination_promotions < 48u) {
    REXLOG_INFO(
        "Refii plume promoted exact copy destination #{} frame={} "
        "texture=0x{:08X}/{}x{} fmt={} source=0x{:08X} rt=0x{:08X} "
        "vs=0x{:08X} ps=0x{:08X} serial={}",
        ++logged_exact_copy_destination_promotions,
        g_direct_renderer.current_frame_index, target.guest_address,
        PlumeGuestTextureWidth(target), PlumeGuestTextureHeight(target),
        static_cast<uint32_t>(PlumeGuestTextureFormat(target)),
        link.source_surface, link.render_target, link.vertex_shader,
        link.pixel_shader, static_cast<unsigned long long>(link.serial));
  }
  return target.guest_surface.attachment_view != nullptr;
}

uint64_t DirectPreparedExactCopyDestinationKey(
    const FableSurfaceTextureLink& link,
    const PlumeTrackedResource& destination) {
  size_t seed = 0;
  DirectHashCombine(seed, std::hash<uint32_t>{}(0xECDC0B1u));
  DirectHashCombine(seed, std::hash<uint32_t>{}(destination.guest_address));
  DirectHashCombine(seed, std::hash<uint32_t>{}(destination.parent_address));
  DirectHashCombine(seed, std::hash<uint64_t>{}(destination.serial));
  DirectHashCombine(seed, std::hash<uint64_t>{}(destination.content_serial));
  DirectHashCombine(seed, std::hash<uint64_t>{}(destination.upload_generation));
  DirectHashCombine(seed,
              std::hash<uint32_t>{}(PlumeGuestTextureBaseAddress(destination)));
  DirectHashCombine(seed, std::hash<uint32_t>{}(PlumeGuestTextureWidth(destination)));
  DirectHashCombine(seed, std::hash<uint32_t>{}(PlumeGuestTextureHeight(destination)));
  DirectHashCombine(seed,
              std::hash<uint32_t>{}(PlumeGuestTextureGuestFormat(destination)));
  DirectHashCombine(seed, std::hash<uint32_t>{}(link.source_slot));
  DirectHashCombine(seed, std::hash<uint32_t>{}(link.texture));
  DirectHashCombine(seed, std::hash<uint32_t>{}(link.texture_base_address));
  DirectHashCombine(seed,
              std::hash<uint64_t>{}(
                  FableDirectTextureFetchKey(link.texture_fetch_dwords)));
  DirectHashCombine(seed, std::hash<uint32_t>{}(link.source_surface));
  DirectHashCombine(seed, std::hash<uint32_t>{}(link.backing_texture));
  DirectHashCombine(seed, std::hash<uint32_t>{}(link.backing_base_address));
  DirectHashCombine(seed,
              std::hash<uint64_t>{}(
                  FableDirectTextureFetchKey(link.backing_fetch_dwords)));
  DirectHashCombine(seed, std::hash<uint64_t>{}(link.backing_content_serial));
  DirectHashCombine(seed, std::hash<uint64_t>{}(link.target_content_serial));
  DirectHashCombine(seed, std::hash<uint32_t>{}(link.render_target));
  DirectHashCombine(seed, std::hash<uint32_t>{}(link.depth_stencil));
  DirectHashCombine(seed, std::hash<uint32_t>{}(link.vertex_shader));
  DirectHashCombine(seed, std::hash<uint32_t>{}(link.pixel_shader));
  DirectHashCombine(seed, std::hash<uint32_t>{}(link.copy_symbol_address));
  DirectHashCombine(seed,
              std::hash<uint32_t>{}(
                  static_cast<uint32_t>(link.sampled_source.kind)));
  DirectHashCombine(seed, std::hash<uint32_t>{}(link.sampled_source.resource));
  DirectHashCombine(seed, std::hash<uint32_t>{}(link.sampled_source.base_address));
  DirectHashCombine(seed,
              std::hash<uint64_t>{}(
                  FableDirectTextureFetchKey(
                      link.sampled_source.fetch_dwords)));
  DirectHashCombine(seed, std::hash<uint64_t>{}(link.sampled_source.content_serial));
  DirectHashCombine(seed,
              std::hash<uint32_t>{}(static_cast<uint32_t>(link.kind)));
  return seed != 0 ? static_cast<uint64_t>(seed) : 1ull;
}

bool RememberDirectPreparedExactCopyDestinationLocked(uint64_t key) {
  if (key == 0) {
    return false;
  }
  constexpr size_t kMaxPreparedExactCopyDestinationKeys = 16384u;
  if (g_direct_renderer.prepared_exact_copy_destination_keys.size() >=
      kMaxPreparedExactCopyDestinationKeys) {
    g_direct_renderer.prepared_exact_copy_destination_keys.clear();
  }
  return g_direct_renderer.prepared_exact_copy_destination_keys.insert(key)
      .second;
}

bool DirectPreparedExactCopyDestinationCachedLocked(uint64_t key) {
  return key != 0 &&
         g_direct_renderer.prepared_exact_copy_destination_keys.find(key) !=
             g_direct_renderer.prepared_exact_copy_destination_keys.end();
}

bool DirectSurfaceTextureLinkStablePublicationReason(const char* reason) {
  if (!reason) {
    return false;
  }
  return std::strcmp(reason, "beginvertices-title-action") == 0 ||
         std::strcmp(reason, "beginvertices-title-source-sample") == 0 ||
         std::strcmp(reason, "private-rect-exact-current-target") == 0 ||
         std::strcmp(reason, "private-rect-exact-current-target-action") == 0 ||
         std::strcmp(reason, "exact-current-target-surface-texture-identity") == 0 ||
         std::strcmp(reason, "surface-alias-hazard-copy") == 0;
}

uint64_t DirectStableSurfaceTextureLinkPublicationKey(
    const FableSurfaceTextureLink& link) {
  if (!link.trusted_destination_edge || link.texture == 0 ||
      link.source_surface == 0 ||
      !DirectSurfaceTextureLinkHasCommandConsumableSampleIdentity(link)) {
    return 0;
  }
  if (DirectSurfaceTextureLinkIsPrivateTitleWrapperRoot(link) &&
      link.wrapper_source_texture == 0) {
    return 0;
  }

  size_t seed = 0;
  DirectHashCombine(seed, std::hash<uint32_t>{}(0x51A8E11u));
  DirectHashCombine(seed, std::hash<uint32_t>{}(link.source_slot));
  DirectHashCombine(seed, std::hash<uint32_t>{}(link.texture));
  DirectHashCombine(seed, std::hash<uint32_t>{}(link.texture_base_address));
  DirectHashCombine(seed, std::hash<uint32_t>{}(link.texture_width));
  DirectHashCombine(seed, std::hash<uint32_t>{}(link.texture_height));
  DirectHashCombine(seed, std::hash<uint32_t>{}(link.texture_format));
  DirectHashCombine(seed,
              std::hash<uint64_t>{}(
                  FableDirectTextureFetchKey(link.texture_fetch_dwords)));
  DirectHashCombine(seed, std::hash<uint32_t>{}(link.source_surface));
  DirectHashCombine(seed, std::hash<uint32_t>{}(link.backing_texture));
  DirectHashCombine(seed, std::hash<uint32_t>{}(link.backing_base_address));
  DirectHashCombine(seed,
              std::hash<uint64_t>{}(
                  FableDirectTextureFetchKey(link.backing_fetch_dwords)));
  DirectHashCombine(seed, std::hash<uint64_t>{}(link.backing_content_serial));
  DirectHashCombine(seed, std::hash<uint64_t>{}(link.target_content_serial));
  DirectHashCombine(seed, std::hash<uint32_t>{}(link.render_target));
  DirectHashCombine(seed, std::hash<uint32_t>{}(link.depth_stencil));
  DirectHashCombine(seed, std::hash<uint32_t>{}(link.vertex_shader));
  DirectHashCombine(seed, std::hash<uint32_t>{}(link.pixel_shader));
  DirectHashCombine(seed, std::hash<uint32_t>{}(link.copy_symbol_address));
  DirectHashCombine(seed, std::hash<uint32_t>{}(link.wrapper_source_texture));
  DirectHashCombine(seed, std::hash<bool>{}(link.wrapper_source_exact));
  for (uint32_t setup_global : link.wrapper_setup_globals) {
    DirectHashCombine(seed, std::hash<uint32_t>{}(setup_global));
  }
  DirectHashCombine(seed,
              std::hash<uint32_t>{}(
                  static_cast<uint32_t>(link.sampled_source.kind)));
  DirectHashCombine(seed, std::hash<uint32_t>{}(link.sampled_source.resource));
  DirectHashCombine(seed, std::hash<uint32_t>{}(link.sampled_source.base_address));
  DirectHashCombine(seed,
              std::hash<uint64_t>{}(
                  FableDirectTextureFetchKey(
                      link.sampled_source.fetch_dwords)));
  DirectHashCombine(seed, std::hash<uint64_t>{}(link.sampled_source.content_serial));
  DirectHashCombine(seed,
              std::hash<uint32_t>{}(static_cast<uint32_t>(link.kind)));
  return seed != 0 ? static_cast<uint64_t>(seed) : 1ull;
}

bool DirectStableSurfaceTextureLinkPublicationRegisteredLocked(
    uint64_t key, const FableSurfaceTextureLink& link) {
  if (key == 0 || link.texture == 0) {
    return false;
  }
  const std::vector<FableSurfaceTextureLink>* links =
      FableDirectFindSurfaceTextureLinksForDestinationTexture(link.texture);
  if (!links) {
    return false;
  }
  for (auto it = links->rbegin(); it != links->rend(); ++it) {
    if (DirectStableSurfaceTextureLinkPublicationKey(*it) == key) {
      return true;
    }
  }
  return false;
}

bool DirectStableSurfaceTextureLinkPublicationCachedLocked(
    uint64_t key, const FableSurfaceTextureLink& link) {
  return key != 0 &&
         g_direct_renderer.published_stable_surface_texture_link_keys.find(key) !=
             g_direct_renderer.published_stable_surface_texture_link_keys.end() &&
         DirectStableSurfaceTextureLinkPublicationRegisteredLocked(key, link);
}

void RememberDirectStableSurfaceTextureLinkPublicationLocked(uint64_t key) {
  if (key == 0) {
    return;
  }
  constexpr size_t kMaxStableSurfaceTextureLinkPublicationKeys = 16384u;
  if (g_direct_renderer.published_stable_surface_texture_link_keys.size() >=
      kMaxStableSurfaceTextureLinkPublicationKeys) {
    g_direct_renderer.published_stable_surface_texture_link_keys.clear();
  }
  g_direct_renderer.published_stable_surface_texture_link_keys.insert(key);
}

void PrepareDirectExactCopyDestinationFromLinkLocked(
    const FableSurfaceTextureLink& link, const char* reason) {
  if (!g_direct_renderer.device || !link.trusted_destination_edge ||
      link.source_surface == 0 ||
      !DirectPrivateTextureCopyKindUsesColorBacking(link.kind)) {
    return;
  }

  std::unordered_set<uint32_t> candidates;
  auto add_candidate = [&](uint32_t guest) {
    if (guest != 0 && guest != link.source_surface &&
        guest != link.render_target && guest != link.depth_stencil &&
        guest < 0xF1000000u) {
      candidates.insert(guest);
    }
  };
  auto add_identity = [&](const FableTextureIdentity* identity) {
    if (!identity) {
      return;
    }
    add_candidate(identity->object_texture);
    add_candidate(identity->parent_texture);
  };
  auto add_identity_objects = [&](const std::vector<uint32_t>* objects) {
    if (!objects) {
      return;
    }
    for (uint32_t object : *objects) {
      add_candidate(object);
    }
  };

  add_candidate(link.texture);
  add_candidate(link.backing_texture);
  add_identity(FableDirectFindTextureIdentityByObject(link.texture));
  add_identity(FableDirectFindTextureIdentityByObject(link.backing_texture));
  if (link.texture_base_address != 0) {
    add_identity(FableDirectFindTextureIdentityByBase(link.texture_base_address));
    add_identity_objects(FableDirectFindTextureIdentityObjectsByBase(
        link.texture_base_address));
  }
  if (link.backing_base_address != 0) {
    add_identity(FableDirectFindTextureIdentityByBase(link.backing_base_address));
    add_identity_objects(FableDirectFindTextureIdentityObjectsByBase(
        link.backing_base_address));
  }
  if (HasTrackedFetchDwords(link.texture_fetch_dwords)) {
    add_identity(FableDirectFindTextureIdentityByFetch(
        link.texture_fetch_dwords));
    add_identity_objects(FableDirectFindTextureIdentityObjectsByFetch(
        link.texture_fetch_dwords));
  }
  if (HasTrackedFetchDwords(link.backing_fetch_dwords)) {
    add_identity(FableDirectFindTextureIdentityByFetch(
        link.backing_fetch_dwords));
    add_identity_objects(FableDirectFindTextureIdentityObjectsByFetch(
        link.backing_fetch_dwords));
  }

  for (uint32_t guest : candidates) {
    PlumeTrackedResource* destination = FindDirectResourceLocked(guest);
    if (!destination || destination->kind != FableGuestResourceKind::kTexture ||
        !IsImageResource(destination->kind) || !PlumeGuestImageReady(*destination) ||
        destination->texture_depth_target ||
        destination->guest_address == link.source_surface ||
        destination->parent_address == link.source_surface) {
      continue;
    }
    const uint64_t prepared_key =
        DirectPreparedExactCopyDestinationKey(link, *destination);
    if (DirectPreparedExactCopyDestinationCachedLocked(prepared_key)) {
      continue;
    }
    const bool was_render_target =
        destination->texture_render_target &&
        destination->guest_surface.attachment_view != nullptr;
	    if (!PromoteDirectExactCopyDestinationToColorTargetLocked(
	            g_direct_renderer.device, *destination, link)) {
	      continue;
	    }
	    FableSurfaceTextureLink destination_link = link;
	    destination_link.source_slot = 0xFFFFFFFFu;
	    destination_link.texture = destination->guest_address;
	    destination_link.texture_base_address =
	        PlumeGuestTextureBaseAddress(*destination);
	    destination_link.texture_width = PlumeGuestTextureWidth(*destination);
	    destination_link.texture_height = PlumeGuestTextureHeight(*destination);
	    destination_link.texture_format = PlumeGuestTextureGuestFormat(*destination);
	    destination_link.texture_fetch_dwords =
	        PlumeGuestTextureFetchDwords(*destination);
	    FableDirectRegisterSurfaceTextureLink(destination_link);
	    RememberDirectPreparedExactCopyDestinationLocked(prepared_key);
	    static uint32_t logged_prepared_exact_copy_destination_edges = 0;
	    if (RendererVerboseTracingEnabled() &&
	        logged_prepared_exact_copy_destination_edges < 48u) {
	      REXLOG_INFO(
	          "Refii plume prepared exact copy destination edge #{} frame={} "
	          "reason={} guest=0x{:08X}/{}x{} base=0x{:08X} "
	          "source=0x{:08X} link_tex=0x{:08X} link_base=0x{:08X} "
	          "kind={} serial={}",
	          ++logged_prepared_exact_copy_destination_edges,
	          g_direct_renderer.current_frame_index, reason ? reason : "register",
	          destination_link.texture, destination_link.texture_width,
	          destination_link.texture_height,
	          destination_link.texture_base_address,
	          destination_link.source_surface, link.texture,
	          link.texture_base_address,
	          FablePrivateTextureCopyKindName(destination_link.kind),
	          static_cast<unsigned long long>(link.serial));
	    }
	    if (!was_render_target) {
	      static uint32_t logged_prepared_exact_copy_destinations = 0;
	      if (RendererVerboseTracingEnabled() &&
	          logged_prepared_exact_copy_destinations < 48u) {
        REXLOG_INFO(
            "Refii plume prepared exact copy destination #{} frame={} "
            "reason={} guest=0x{:08X}/{}x{} fmt={} source=0x{:08X} "
            "link_tex=0x{:08X} base=0x{:08X} kind={} serial={}",
            ++logged_prepared_exact_copy_destinations,
            g_direct_renderer.current_frame_index,
            reason ? reason : "register", destination->guest_address,
            PlumeGuestTextureWidth(*destination),
            PlumeGuestTextureHeight(*destination),
            static_cast<uint32_t>(PlumeGuestTextureFormat(*destination)),
            link.source_surface, link.texture, link.texture_base_address,
            FablePrivateTextureCopyKindName(link.kind),
            static_cast<unsigned long long>(link.serial));
      }
    }
  }
}

void RegisterDirectSurfaceTextureLinkPreparedLocked(
    const FableSurfaceTextureLink& link, const char* reason) {
  if ((link.source_surface & 0xFF000000u) == 0xF1000000u) {
    return;
  }
  if (DirectSurfaceTextureLinkTouchesFablePrivateChain(link, nullptr) &&
      !DirectSurfaceTextureLinkHasCommandConsumableSampleIdentity(link)) {
    static uint32_t logged_unconsumable_private_chain_link_rejects = 0;
    if (RendererVerboseTracingEnabled() &&
        logged_unconsumable_private_chain_link_rejects < 48u) {
      REXLOG_INFO(
          "Refii plume unconsumable private-chain surface-link reject #{} "
          "frame={} reason={} slot={} texture=0x{:08X}/{}x{} base=0x{:08X} "
          "surface=0x{:08X} backing=0x{:08X} target_serial={} "
          "backing_serial={} call={} context=0x{:016X} symbol=0x{:08X} "
          "rt=0x{:08X} ds=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
          ++logged_unconsumable_private_chain_link_rejects,
          g_direct_renderer.current_frame_index, reason ? reason : "register",
          link.source_slot, link.texture, link.texture_width,
          link.texture_height, link.texture_base_address, link.source_surface,
          link.backing_texture,
          static_cast<unsigned long long>(link.target_content_serial),
          static_cast<unsigned long long>(link.backing_content_serial),
          static_cast<unsigned long long>(link.copy_call_serial),
          static_cast<unsigned long long>(link.copy_context_key),
          link.copy_symbol_address, link.render_target, link.depth_stencil,
          link.vertex_shader, link.pixel_shader);
    }
    return;
  }
  const uint64_t stable_publication_key =
      DirectSurfaceTextureLinkStablePublicationReason(reason)
          ? DirectStableSurfaceTextureLinkPublicationKey(link)
          : 0ull;
  if (DirectStableSurfaceTextureLinkPublicationCachedLocked(
          stable_publication_key, link)) {
    return;
  }
  const FableDirectClock::time_point register_t0 = FableDirectClock::now();
  FableDirectRegisterSurfaceTextureLink(link);
  const double register_ms =
      FableDirectElapsedMs(register_t0, FableDirectClock::now());
  const FableDirectClock::time_point prepare_t0 = FableDirectClock::now();
  PrepareDirectExactCopyDestinationFromLinkLocked(link, reason);
  const double prepare_ms =
      FableDirectElapsedMs(prepare_t0, FableDirectClock::now());
  RememberDirectStableSurfaceTextureLinkPublicationLocked(
      stable_publication_key);
  const double total_ms = register_ms + prepare_ms;
  static uint32_t logged_surface_texture_link_prepare_timing = 0;
  if ((total_ms >= 0.5 || register_ms >= 0.25 || prepare_ms >= 0.25) &&
      logged_surface_texture_link_prepare_timing < 128u) {
    REXLOG_INFO(
        "Refii plume surface-texture link timing #{} frame={} reason={} "
        "slot={} texture=0x{:08X}/{}x{} base=0x{:08X} "
        "surface=0x{:08X} kind={} total_ms={:.3f} register_ms={:.3f} "
        "prepare_ms={:.3f} target_serial={} backing=0x{:08X} "
        "sample_kind={} sample=0x{:08X} rt=0x{:08X} ds=0x{:08X} "
        "vs=0x{:08X} ps=0x{:08X}",
        ++logged_surface_texture_link_prepare_timing,
        g_direct_renderer.current_frame_index, reason ? reason : "register",
        link.source_slot, link.texture, link.texture_width,
        link.texture_height, link.texture_base_address, link.source_surface,
        FablePrivateTextureCopyKindName(link.kind), total_ms, register_ms,
        prepare_ms, static_cast<unsigned long long>(link.target_content_serial),
        link.backing_texture, static_cast<uint32_t>(link.sampled_source.kind),
        link.sampled_source.resource, link.render_target, link.depth_stencil,
        link.vertex_shader, link.pixel_shader);
  }
}

DirectPresentSource
DirectPresentSourceFromCurrentFrameSurfaceDestinationLocked() {
  struct SurfaceDestinationPresentCandidate {
    DirectPresentSource source;
    uint64_t score = 0;
  };

  SurfaceDestinationPresentCandidate best;
  for (auto it = g_direct_renderer.frame_database_command_packets.rbegin();
       it != g_direct_renderer.frame_database_command_packets.rend(); ++it) {
    const FableDirectFrameDatabaseCommandPacket& packet = *it;
    if (packet.kind != "surface_destination") {
      continue;
    }
    const FableDirectFrameDatabaseSurfaceDestinationPacket& relation =
        packet.surface_destination;
    if (!relation.trusted_destination_edge ||
        relation.source_surface == 0 || relation.destination_texture == 0 ||
        relation.source_width == 0 || relation.source_height == 0 ||
        relation.destination_width == 0 || relation.destination_height == 0) {
      continue;
    }

    const bool title_wrapper =
        FableTitleSurfaceDestinationRootIsWrapper(packet.root.symbol_address);
    if (!title_wrapper &&
        !IsFableTitleSceneTargetLocked(relation.source_surface) &&
        !IsFableTitleSceneTargetLocked(relation.destination_texture)) {
      continue;
    }

    const std::array<uint32_t, 2> candidates = {
        relation.source_surface,
        relation.destination_texture,
    };
    for (uint32_t index = 0; index < candidates.size(); ++index) {
      DirectPresentSource candidate =
          PresentSourceFromResourceLocked(candidates[index]);
      const bool source_surface = index == 0u;
      const FableTitleSurfaceDestinationPresentCandidateFacts
          candidate_facts = {
              .source_ready = candidate.texture && candidate.view,
              .source_surface = source_surface,
              .title_wrapper = title_wrapper,
              .wrapper_source_exact = relation.wrapper_source_exact,
              .color_write_layout =
                  candidate.layout == plume::RenderTextureLayout::COLOR_WRITE,
              .present_target_candidate = IsFablePresentTargetCandidate(
                  candidate.target, candidate.width, candidate.height),
              // "Full resolution" = the live guest display extent
              // (qword_834A5FE8), not a hardcoded 1280x720.
              .full_resolution_candidate =
                  candidate.width >= FableGuestDisplayWidth() &&
                  candidate.height >= FableGuestDisplayHeight(),
              .width = candidate.width,
              .height = candidate.height,
              .write_serial = candidate.write_serial,
          };
      const uint64_t score =
          FableTitleSurfaceDestinationPresentScore(candidate_facts);
      if (score == 0) {
        continue;
      }
      if (!best.source.texture || score > best.score) {
        candidate.reason = source_surface
                               ? "current_frame_surface_destination_source"
                               : "current_frame_surface_destination_texture";
        best = {candidate, score};
      }
    }
  }

  return best.source;
}

DirectPresentSource DirectPresentSourceFromCurrentFrameTitleDrawsLocked() {
  struct TitleDrawPresentCandidate {
    DirectPresentSource source;
    uint64_t score = 0;
  };

  TitleDrawPresentCandidate best;
  for (const FableDirectFrameDatabaseDrawPacket& packet :
       g_direct_renderer.frame_database_draw_packets) {
    const FableTitleFramePacketTargetFactsList target_facts =
        DirectTitleFramePacketTargetFactsLocked(packet);
    const FableTitleFramePacketShaderFacts shader_facts =
        DirectTitleFramePacketShaderFactsLocked(packet);

    for (uint32_t target : packet.render_targets) {
      DirectPresentSource candidate = PresentSourceFromResourceLocked(target);
      const FableTitleFramePacketPresentCandidateFacts candidate_facts = {
          .source_ready = candidate.texture && candidate.view,
          .title_scene_target = IsFableTitleSceneTargetLocked(target),
          .source_scene_target_candidate = IsFableSceneTargetCandidate(
              candidate.target, candidate.width, candidate.height),
          .width = candidate.width,
          .height = candidate.height,
          .write_serial = candidate.write_serial,
      };
      const uint64_t score = FableTitleFramePacketCurrentFramePresentScore(
          packet, target_facts, shader_facts, candidate_facts);
      if (score == 0) {
        continue;
      }
      if (!best.source.texture || score > best.score) {
        candidate.reason = "current_frame_title_draw_target";
        best = {candidate, score};
      }
    }
  }

  return best.source;
}

// Plan-doc first cleanup target: the per-frame private-rect cache clears
// were duplicated between draw-resource setup and per-frame recording.
void ClearDirectFramePrivateRectCachesLocked() {
  g_direct_renderer.private_texture_copy_action_cache.clear();
  g_direct_renderer.private_texture_copy_action_sample_cache.clear();
  g_direct_renderer.private_texture_exact_interest_cache.clear();
  g_direct_renderer.private_texture_backing_miss_cache.clear();
  g_direct_renderer.registered_private_rect_execute_decision_cache.clear();
  g_direct_renderer.surface_texture_link_sample_frame_cache.clear();
  g_direct_renderer.surface_destination_copy_frame_keys.clear();
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
  g_direct_renderer.targetless_wrapper_materialization_reject_frame_keys.clear();
  g_direct_renderer.materialized_private_rect_actions_by_exact_key.clear();
}

#endif

}  // namespace refii::renderer
