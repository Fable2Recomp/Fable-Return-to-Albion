// THE OBSERVER IS DELETED (2026-08-04).
//
// `hook_observer.cpp` — 23,427 lines — was removed wholesale at the user's
// explicit instruction, accepting runtime breakage. This file exists ONLY so
// the tree still links while its ~350 call sites are removed; it is scaffolding
// for the demolition, not a replacement layer.
//
// Every definition below is INERT BY DESIGN. Nothing here observes, mirrors,
// snapshots, or writes guest memory. If a capability turns out to be genuinely
// load-bearing it gets rebuilt in the GTL (`runtime/gtl/refii_gtl.cpp`) as the
// GTL's own contract — never reinstated here.
//
// THE RULE: delete call sites, do not grow this file. Every function that
// reaches zero callers should be deleted from here and from its header. This
// file is finished when it is empty and can itself be deleted.
//
// Known consequences of the inert returns, stated honestly rather than
// discovered later:
//   * Resource snapshots: RESOLVED 2026-08-04. The whole family moved to
//     runtime/gtl/refii_gtl.cpp and is served from the GTL registry + its
//     change journal. See the note where they used to be.
//   * Bink plane publishes drain to nothing (video content path).
//   * Shader float-constant sanitation is a PASSTHROUGH — the raw guest dword
//     is returned unchanged.
//   * All observer statistics read zero; diagnostics quoting them are
//     meaningless until their call sites go.

#include "runtime/hook_observer.h"

#include "renderer_runtime.h"
#include "runtime/refii_renderer/plume/fable/resource/fable_resource_snapshots.h"

namespace refii::renderer {

// ---- hook entry points: the observer no longer watches anything ----------
void ObserveRendererHookEnter(const char*, RendererHookKind,
                              const RendererHookArgs&, uint8_t*,
                              const FableHookSymbolDesc*) {}
void ObserveRendererHookExit(const char*, RendererHookKind,
                             const RendererHookArgs&, uint32_t, uint8_t*,
                             const RendererHookArgs*,
                             const FableHookSymbolDesc*) {}
void ObserveRendererStateDispatch(const RendererHookArgs&, uint8_t*) {}
void ObserveRendererPassQueue(const RendererHookArgs&, uint8_t*) {}
void ObserveNativeSceneWalkerProbe(const RendererHookArgs&, uint8_t*) {}
void ObserveEmulatedFablePm4SubmitSegment(uint32_t, uint32_t, uint32_t,
                                          uint32_t, uint8_t*) {}

// ---- resource snapshots: MIGRATED TO THE GTL 2026-08-04 ------------------
// GetFableDirectResourceSnapshotsForAddresses(+Blocking),
// GetFableDirectResourceChanges, DrainPendingFableDirectTextureIdentityOperations,
// AcknowledgeFableDirectResourceChanges and
// AcknowledgeFableDirectDestroyedResources now live in
// runtime/gtl/refii_gtl.cpp, backed by the GTL's own registry
// (g_state.resources / g_state.content_images) and its change journal. They are
// gone from here for good; do not re-add them.

// ---- texture capture / content ------------------------------------------
void DrainDeferredTextureCaptures() {}
uint64_t DeferredTextureCaptureEnqueueGeneration() { return 0; }
bool RefreshRendererTextureFromFetchBase(uint32_t, const char*, bool,
                                         const std::array<uint32_t, 6>*) {
  return false;
}

// ---- bink / streaming ----------------------------------------------------
void NotifyFableStreamedWrapperResident(uint32_t, uint8_t*) {}
bool NotifyFableScratchDrawInputMissing(uint32_t) { return false; }

// ---- shader constants: PASSTHROUGH, no sanitation ------------------------
// The observer rewrote suspect float bit patterns here. With it gone the guest
// dword is used verbatim; if a bad constant matters, the GTL must state why.
uint32_t RendererSanitizeShaderFloatConstantDword(bool, uint32_t,
                                                  uint32_t value) {
  return value;
}

// ---- hook metadata -------------------------------------------------------
void FillFableHookSymbolDescObserverFields(FableHookSymbolDesc&) {}

// ---- statistics: all zero, all meaningless until the callers go ----------
RendererHookObserverSnapshot GetRendererHookObserverSnapshot() { return {}; }
ResourceReleaseHookCostStats GetResourceReleaseHookCostStats() { return {}; }
void GetGuestByteScanStats(uint64_t& calls, uint64_t& ns, uint64_t& bytes) {
  calls = 0;
  ns = 0;
  bytes = 0;
}
void GetObserverMutexContentionStats(uint64_t& uncontended, uint64_t& contended,
                                     uint64_t& wait_us) {
  uncontended = 0;
  contended = 0;
  wait_us = 0;
}
void GetResourceReleaseDispositionStats(uint64_t& neutral, uint64_t& deferred,
                                        uint64_t& erased, uint64_t& norecord,
                                        uint64_t& drain_batch,
                                        uint64_t& drain_walks) {
  neutral = 0;
  deferred = 0;
  erased = 0;
  norecord = 0;
  drain_batch = 0;
  drain_walks = 0;
}
void GetSnapshotFetchStats(uint64_t& calls, uint64_t& busy, uint64_t& wait_us,
                           uint64_t& body_us, uint64_t& upload_bytes,
                           uint64_t& mints) {
  calls = 0;
  busy = 0;
  wait_us = 0;
  body_us = 0;
  upload_bytes = 0;
  mints = 0;
}
std::string FormatFeedCensus() { return "observer deleted"; }

}  // namespace refii::renderer
