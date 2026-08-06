#pragma once

#include "renderer_hook_targets.h"

#include <array>
#include <cstddef>
#include <cstdint>

struct PPCContext;

namespace refii::renderer {

enum class RendererHookKind {
  kGeneral,
  kResource,
  kDraw,
};

struct RendererHookArgs {
  uint32_t r3 = 0;
  uint32_t r4 = 0;
  uint32_t r5 = 0;
  uint32_t r6 = 0;
  uint32_t r7 = 0;
  uint32_t r8 = 0;
  uint32_t r9 = 0;
  uint32_t r10 = 0;
  uint32_t r14 = 0;
  uint32_t r15 = 0;
  uint32_t r16 = 0;
  uint32_t r17 = 0;
  uint32_t r18 = 0;
  uint32_t r19 = 0;
  uint32_t r20 = 0;
  uint32_t r21 = 0;
  uint32_t r22 = 0;
  uint32_t r23 = 0;
  uint32_t r24 = 0;
  uint32_t r25 = 0;
  uint32_t r26 = 0;
  uint32_t r27 = 0;
  uint32_t r28 = 0;
  uint32_t r29 = 0;
  uint32_t r30 = 0;
  uint32_t r31 = 0;
  uint32_t r1 = 0;
  uint32_t lr = 0;
  // r3_u64/r5_u64/r6_u64 DELETED 2026-07-27: write-only. RendererHookArgs is
  // filled on EVERY hooked guest call (twice -- enter and exit), and those three
  // 64-bit views were stored at 19 sites and read at NONE. r4_u64 is kept
  // because it has 9 real reads -- same struct, same grep, opposite result,
  // which is what proves this was a dead store and not a search gap.
  uint64_t r4_u64 = 0;
  // r7_u64 added 2026-08-03. D3DDevice_Set{Vertex,Pixel}ShaderConstantF take
  // their float-constant dirty mask in r7 as a FULL 64-bit value, and the
  // guest's own encoding puts the meaningful bits at the TOP: register group
  // g = reg>>2 sets bit 63-g (IDA: sub_8221A880 `ld r11,0(r3) / or r11,r11,r7
  // / std r11,0(r3)`, and the caller-side closed form at 0x83048A64 builds the
  // run with `extldi r9,r9,64,63` then `srad`/`srd`). So every call with
  // first_register < 128 lands entirely in bits 63..32 and reads as ZERO
  // through the 32-bit view. Truncating here silently un-dirtied the banks.
  uint64_t r7_u64 = 0;
  double f1 = 0.0;
  double f2 = 0.0;
  double f3 = 0.0;
  double f4 = 0.0;
  double f5 = 0.0;
  double f6 = 0.0;
};

struct RendererHookTimingSnapshot {
  uint64_t hook_enters = 0;
  uint64_t renderer_setup_hooks = 0;
  uint64_t draw_hook_enters = 0;
  uint64_t display_present_hooks = 0;
  uint64_t plume_present_requests = 0;
  uint64_t plume_present_pump_requests = 0;
  uint64_t plume_present_pump_fable_advances = 0;
  uint64_t guest_suppressed_hooks = 0;
  int64_t last_hook_gap_us = 0;
  int64_t last_draw_hook_gap_us = 0;
  int64_t last_display_present_gap_us = 0;
  int64_t last_plume_present_request_gap_us = 0;
  int64_t last_plume_present_pump_gap_us = 0;
  uint32_t fable_renderer = 0;
  uint32_t fable_renderer_owner = 0;
  uint32_t fable_game = 0;
  uint32_t fable_device = 0;
  uint32_t fable_present_submitted = 0;
  uint32_t fable_present_completed = 0;
  uint32_t fable_swap_callbacks = 0;
  uint32_t fable_present_pending = 0;
  uint32_t plume_present_pump_target_fps = 0;
  bool seen_display_swap_hook = false;
  bool plume_present_pump_enabled = false;
  char last_renderer_setup_symbol[64] = {};
  char last_hook_symbol[64] = {};
  char last_display_present_symbol[64] = {};
  char last_plume_present_symbol[64] = {};
  char last_plume_present_reason[32] = {};
  char last_guest_suppressed_symbol[64] = {};
};

struct RendererHookStatsSnapshot {
  RendererHookId id = RendererHookId::kCount;
  char name[64] = {};
  uint32_t sonic_address = 0;
  uint32_t fable_address = 0;
  uint64_t hit_count = 0;
  uint64_t guest_passthrough_count = 0;
  uint64_t first_frame_seen = 0;
  uint64_t last_frame_seen = 0;
};

using RendererHookStatsSnapshots =
    std::array<RendererHookStatsSnapshot,
               static_cast<std::size_t>(RendererHookId::kCount)>;

bool PlumeAvailable();
void ShutdownRendererPresentPump();

// Present-decoupling STEP 6 (2026-08-02): capture-tied ack gate interface
// between the record path (plume_backend.cpp) and the guest-thread Present
// hook (renderer_runtime.cpp). The record path samples the guest present
// boundary sequence BEFORE its drain + RecordFableDirectFrame and publishes
// that value back once the record returns (all CPU-side guest-memory reads
// for the frame complete); the Present hook holds the guest's synthesized
// present ack until captures cover the previous boundary (depth 1).
struct FableCaptureAckGateCensus {
  uint64_t checks = 0;
  uint64_t disarms = 0;
  uint64_t waits = 0;
  uint64_t wait_us = 0;
  uint64_t timeouts = 0;
  uint64_t max_outstanding = 0;
  uint64_t last_outstanding = 0;
  uint64_t captured_boundary = 0;
  uint64_t present_boundary = 0;
  uint64_t publishes = 0;
};
uint64_t FableGuestPresentBoundarySeqForCapture();
void PublishFableCapturesCompletedThroughBoundary(uint64_t boundary_seq);
FableCaptureAckGateCensus GetFableCaptureAckGateCensus();
void RegisterFableStartupVideoPumpContext(uint8_t* base,
                                          const PPCContext& ctx,
                                          uint32_t wrapper_guest);
void RegisterFableStartupVideoPumpContext(uint8_t* base,
                                          const PPCContext& ctx,
                                          uint32_t wrapper_guest,
                                          uint32_t update_guest);
void NotifyFableStartupVideoPumpInitComplete(uint32_t wrapper_guest,
                                             uint32_t update_guest);
void NotifyFableStartupVideoPumpDispatchComplete(uint32_t wrapper_guest,
                                                 uint32_t update_guest);
void NotifyFableStartupVideoPumpRunComplete(uint32_t update_guest);
void NotifyFableStartupVideoBinkMovieActive(uint8_t* base,
                                            uint32_t movie_guest);
void NotifyFableStartupVideoBinkMovieFinished(uint8_t* base,
                                              uint32_t movie_guest);
bool FableStartupVideoPumpActive();
bool FableStartupVideoPumpCompletedForHandoff();
void PumpStartupBinkAudioCursorsFromPlume();
void NotifyPlumeBinkVideoUploadReady();
// Cumulative count of NotifyPlumeBinkVideoUploadReady calls. The present path
// compares it against the value captured at its last frame record: a change
// means fresh Bink texels landed, which must take the record path (never the
// re-present fast path).
uint64_t PlumeBinkVideoUploadReadyCount();
RendererHookTimingSnapshot GetRendererHookTimingSnapshot();
RendererHookStatsSnapshots GetRendererHookStatsSnapshots();
RendererHookArgs CaptureRendererHookArgs(const PPCContext& ctx);
// kLight capture: r1, r3-r10, lr, r4_u64 only; r14-r31 (and f1-f6, which no
// capture ever filled) stay at their deterministic zero defaults. See
// FableHookArgsCaptureLevel in renderer_hook_targets.h for the audit.
RendererHookArgs CaptureRendererHookArgsLight(const PPCContext& ctx);
// Desc-driven capture used by every hook wrapper (enter and exit): dispatches
// on desc->capture_level (null desc => full, the safe width) and accumulates
// the measured capture cost into the `capture` segment of the hook wrapper
// census line.
RendererHookArgs CaptureRendererHookArgsFor(const FableHookSymbolDesc* desc,
                                            const PPCContext& ctx);

// Resolves (and memoises) the per-symbol descriptor. Thread-safe; the first
// resolve for a symbol takes a mutex and runs the existing string-based
// helpers once, afterwards the pointer is immutable. Intended usage is a
// function-local static in each hook wrapper.
const FableHookSymbolDesc* ResolveFableHookSymbolDesc(const char* symbol,
                                                      RendererHookKind kind);
// Implemented in hook_observer.cpp: fills the obs_* / slot / draw-submit
// fields from the observer's own predicates (single source of truth).
void FillFableHookSymbolDescObserverFields(FableHookSymbolDesc& desc);

// Scene-scope marker short-circuit (sub_822975F8 / sub_8222C2B0 /
// sub_82192028 / sub_822749E0): under the shipping hook policy the ONLY
// consumer of these four pure scope markers is the GTL scene_submit_depth
// pair (no RendererHookTarget entry -> no stats, not observer-allowed in
// root-only mode, never native-work because they are title scene population
// hooks). Begin returns false whenever any diagnostic mode could observe
// them (deep observer, telemetry, tracing, non-plume backend) so the full
// wrapper path still runs in those configurations.
bool BeginFableSceneScopeMarkerFastPath(const char* symbol, uint8_t* base,
                                        const FableHookSymbolDesc* desc);
void EndFableSceneScopeMarkerFastPath();

// Cross-thread autopsy telemetry (drain-spin diagnosis): every hooked guest
// call records {guest_tid, symbol, enter timestamp} in a lock-free registry
// so a DIFFERENT thread (the stream-drain spin logger in vfs) can report what
// the parked owner thread last executed and how long ago. Nearly free: one
// steady_clock read + two relaxed stores per hook enter.
void NoteRendererHookThreadTelemetry(const char* symbol);
// Returns false if guest_tid never entered a hook. us_since_enter is measured
// against steady_clock now.
bool QueryRendererHookThreadTelemetry(uint32_t guest_tid,
                                      const char** last_symbol,
                                      uint64_t* us_since_enter);

// One entry per guest thread that has ever entered a renderer hook.
struct RendererHookThreadTelemetryEntry {
  uint32_t guest_tid = 0;
  const char* last_symbol = nullptr;
  uint64_t us_since_enter = 0;
};
// Whole-registry snapshot for the guest-render stall watchdog: when guest
// content publication stops, the ONLY question that matters is which guest
// thread stopped and what it last executed. Fills up to `capacity` entries and
// returns how many were written. Safe from any thread (symbols are literals).
size_t SnapshotRendererHookThreadTelemetry(
    RendererHookThreadTelemetryEntry* out, size_t capacity);

// No desc default: every call site must resolve a FableHookSymbolDesc (the
// observer dispatch dereferences it unconditionally).
void OnRendererGuestHookEnter(const char* symbol, RendererHookKind kind,
                              const RendererHookArgs& args, uint8_t* base,
                              const FableHookSymbolDesc* desc);
void OnRendererStateDispatchHook(const RendererHookArgs& args, uint8_t* base);
void OnRendererPassQueueHook(const RendererHookArgs& args, uint8_t* base);
void OnRendererPassQueueSnapshot(const RendererHookArgs& args, uint8_t* base);
void OnRendererGuestHookExit(const char* symbol, RendererHookKind kind,
                             const RendererHookArgs& args,
                             const PPCContext& ctx, uint8_t* base,
                             const FableHookSymbolDesc* desc);
void RepairFableSceneStreamPublishHandoffForWait(const char* reason,
                                                 uint8_t* base);
// Diagnostic counters for the guest GUI quad-batch pipeline stages
// (queue append sub_82A5AA98 / enable+prep sub_822AA0A0 / dispatcher
// sub_821CA5E8); logged by the traversal counter dump.
void CountFableGuiPipelineStage(uint32_t stage);

bool ShouldRunGuestRendererHook(const char* symbol, RendererHookKind kind);
bool ShouldRunGuestRendererHook(const char* symbol, RendererHookKind kind,
                                const RendererHookArgs& args, uint8_t* base,
                                const FableHookSymbolDesc* desc = nullptr);
void OnRendererGuestHookSuppressedForPlume(
    const char* symbol, RendererHookKind kind, const RendererHookArgs& args,
    PPCContext& ctx, uint8_t* base, const FableHookSymbolDesc* desc);
bool BridgeFableRendererGetTimeDeltaForPlume(const RendererHookArgs& args,
                                             PPCContext& ctx, uint8_t* base);

}  // namespace refii::renderer
