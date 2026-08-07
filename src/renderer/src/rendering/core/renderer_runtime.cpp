#include "renderer_runtime.h"
#include "runtime/refii_renderer/guest/guest_memory_access.h"
#include "runtime/refii_renderer/hooks/renderer_runtime_internal.h"

#include "hooks/ppc_externs.h"
#include "runtime/refii_renderer/bik/bink_startup_audio_pump.h"
#include "runtime/refii_renderer/bik/bink_plane_publish.h"
#include "runtime/refii_renderer/bik/bink_video_plane_setup.h"
#include "renderdoc_debugger.h"
#include "runtime/cvars.h"
#include "runtime/hook_observer.h"
#include "runtime/refii_renderer/gtl/refii_gtl.h"
#include "runtime/renderer_hook_policy.h"
#include "runtime/refii_renderer/hooks/general/fable_scene_boundary_hooks.h"
#include "runtime/refii_renderer/plume/fable/direct/fable_direct_present_state.h"
#include "runtime/refii_renderer/plume/fable/direct/fable_direct_renderer.h"
#include "runtime/refii_renderer/plume/fable/direct/fable_direct_state.h"
#include "runtime/refii_renderer/plume/fable/title/fable_title_scene_population.h"
#include "runtime/refii_renderer/plume/general/plume_fable_addressing.h"
#include "runtime/plume/general/plume_backend.h"
#include "runtime/refii_renderer/hooks/root/fable_hook_symbols.h"

#include <rex/cvar.h>
#include <rex/logging.h>
#include <rex/graphics/pipeline/texture/info.h>
#include <rex/graphics/pipeline/texture/util.h>
#include <rex/graphics/xenos.h>
#include <rex/ppc/context.h>
#include <rex/system/xthread.h>
#include <tracy/Tracy.hpp>

#include <algorithm>
#include <array>
#include <atomic>
#include <cerrno>
#include <chrono>
#include <condition_variable>
#include <cstdint>
#include <cstdlib>
#include <cstring>
#include <cmath>
#include <deque>
#include <fstream>
#include <limits>
#include <memory>
#include <mutex>
#include <string>
#include <string_view>
#include <thread>
#include <unordered_map>
#include <unordered_set>

#if defined(_WIN32)
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#endif

namespace refii::renderer {

void CompleteFableStartupVideoPumpForHandoff(uint32_t completion_update,
                                             const char *reason);



bool g_logged_plume_unavailable = false;
bool g_logged_plume_observer = false;
bool g_logged_plume_present_driver = false;
bool g_logged_plume_end_scene_present_driver = false;
bool g_logged_plume_present_pump_driver = false;
bool g_logged_plume_present_failed = false;
bool g_logged_fable_renderer_setup_bridge = false;
bool g_logged_fable_present_bookkeeping = false;
bool g_logged_plume_pump_fable_bookkeeping = false;
bool g_logged_plume_pump_fable_bookkeeping_advance = false;
bool g_logged_plume_pump_fable_bookkeeping_complete = false;
bool g_logged_plume_pump_startup_video_context = false;
bool g_logged_plume_pump_startup_video_request = false;
bool g_logged_plume_pump_startup_video_deferred_for_update = false;
bool g_logged_plume_pump_startup_video_wait_native_init = false;
bool g_logged_plume_pump_startup_video_wait_native_update = false;
bool g_logged_plume_bink_upload_present_driver = false;
bool g_logged_renderer_root_only_hook_policy = false;
bool g_logged_fable_command_submit_emulation = false;
bool g_logged_fable_kickoff_segment_emulation = false;
bool g_logged_fable_kickoff_emulation = false;
bool g_logged_fable_query_buffer_space_emulation = false;
std::atomic<uint32_t> g_logged_fable_end_export_emulation{0};
bool g_logged_fable_plume_command_buffer_capture = false;
bool g_logged_fable_primary_list_space_emulation = false;
bool g_logged_fable_deferred_list_space_emulation = false;
bool g_logged_fable_begin_vertices_emulation = false;
bool g_logged_fable_begin_indexed_vertices_emulation = false;
bool g_logged_fable_draw_vertices_up_emulation = false;
bool g_logged_fable_pix_capture_emulation = false;
bool g_logged_fable_pix_capture_control_bookkeeping = false;
bool g_logged_fable_display_interval_emulation = false;
bool g_logged_fable_complex_swap_emulation = false;
bool g_logged_fable_scene_batch_toggle_emulation = false;
bool g_logged_fable_resource_lifetime_emulation = false;
bool g_logged_fable_resource_lifetime_shape = false;
bool g_logged_fable_resource_header_emulation = false;
bool g_logged_fable_resource_lock_emulation = false;
bool g_logged_fable_texture_surface_lock_emulation = false;
bool g_logged_fable_surface_copy_emulation = false;
bool g_logged_fable_surface_copy_shape = false;
bool g_logged_fable_surface_memory_copy_emulation = false;
bool g_logged_fable_surface_memory_copy_shape = false;
bool g_logged_fable_surface_memory_copy_linear_path = false;
bool g_logged_fable_texture_fill_emulation = false;
bool g_logged_fable_texture_file_create_emulation = false;
bool g_logged_fable_resource_query_emulation = false;
bool g_logged_fable_texture_offset_emulation = false;
bool g_logged_fable_texture_header_emulation = false;
bool g_logged_fable_texture_header_utility_emulation = false;
bool g_logged_fable_cpu_resource_helper_emulation = false;
bool g_logged_fable_cpu_texture_layout_fast_emulation = false;
bool g_logged_fable_cpu_texture_layout_fast_tiling = false;
bool g_logged_fable_surface_size_emulation = false;
bool g_logged_fable_surface_view_emulation = false;
bool g_logged_fable_resource_unlock_emulation = false;
bool g_logged_fable_buffer_create_emulation = false;
bool g_logged_fable_buffer_create_shape = false;
bool g_logged_fable_vertex_declaration_create_emulation = false;
bool g_logged_fable_shader_create_emulation = false;
bool g_logged_fable_vertex_shader_register_emulation = false;
bool g_logged_fable_shader_patch_helper_emulation = false;
bool g_logged_fable_shader_utility_emulation = false;
bool g_logged_fable_set_pending_shaders_emulation = false;
bool g_logged_fable_texture_create_emulation = false;
bool g_logged_fable_surface_create_emulation = false;
bool g_logged_fable_surface_wrapper_emulation = false;
bool g_logged_fable_time_delta_bridge = false;
bool g_logged_fable_surface_wrapper_shape = false;
bool g_logged_fable_render_depth_surface_ctor_emulation = false;
bool g_logged_fable_render_depth_surface_ctor_shape = false;
bool g_logged_fable_render_target_setup_emulation = false;
bool g_logged_fable_bink_video_plane_emulation = false;
bool g_logged_fable_bink_video_plane_shape = false;
bool g_logged_fable_get_render_target_emulation = false;
bool g_logged_fable_scene_stream_handoff_repair = false;
std::atomic_bool g_seen_plume_display_swap_hook = false;
std::atomic_bool g_plume_present_pump_running = false;
std::atomic_bool g_plume_present_pump_stop_requested = false;
std::atomic_bool g_plume_present_pump_shutdown_requested = false;
std::atomic<int64_t> g_last_renderer_hook_enter_ns{0};
std::atomic<int64_t> g_last_draw_hook_enter_ns{0};
std::atomic<int64_t> g_last_display_present_hook_ns{0};
std::atomic<int64_t> g_last_plume_present_request_ns{0};
std::atomic<int64_t> g_last_plume_present_pump_request_ns{0};
std::atomic<uint64_t> g_hook_enter_count{0};
std::atomic<uint64_t> g_renderer_setup_hook_count{0};
std::atomic<uint64_t> g_draw_hook_enter_count{0};
std::atomic<uint64_t> g_display_present_hook_count{0};
std::atomic<uint64_t> g_plume_present_request_count{0};
std::atomic<uint64_t> g_plume_present_pump_request_count{0};
std::atomic<uint64_t> g_plume_present_pump_fable_advance_count{0};
std::atomic<uint64_t> g_guest_suppressed_hook_count{0};
std::atomic<uint64_t> g_fable_plume_command_buffer_submit_count{0};
std::atomic<uint64_t> g_fable_scene_stream_handoff_repair_count{0};
std::atomic<uint64_t> g_fable_bink_video_plane_setup_count{0};
std::atomic<uint32_t> g_fable_plume_gpu_scratch_next{0xF0000000u};
struct RendererHookAtomicStats {
  std::atomic<uint64_t> hit_count{0};
  std::atomic<uint64_t> guest_passthrough_count{0};
  std::atomic<uint64_t> first_frame_seen{0};
  std::atomic<uint64_t> last_frame_seen{0};
};
std::array<RendererHookAtomicStats, kRendererHookTargetCount>
    g_renderer_hook_stats;
std::atomic<int64_t> g_last_hook_gap_us{0};
std::atomic<int64_t> g_last_draw_hook_gap_us{0};
std::atomic<int64_t> g_last_display_present_gap_us{0};
std::atomic<int64_t> g_last_plume_present_request_gap_us{0};
std::atomic<int64_t> g_last_plume_present_pump_gap_us{0};
// Pump iteration split (see the present-pump wait log): time inside
// PresentPlumeFrameFromRendererHook, and time in the loop body BEFORE it.
// Hook-wrapper phase census. The per-hook census showed SUPPRESSED roots costing
// 3.7ms (BeginIndexedVertices) and 19.3ms (BeginVertices) per call, on guest
// functions IDA proves are straight-line O(1) PM4 writers. The GTL lock was then
// measured clean (211457 acquisitions, 19ms total, over1ms=0), killing the
// contention theory. These split the wrapper so the next run names the phase
// instead of me guessing a fifth time. Run totals; take deltas.
// TOTAL time the GUEST thread spends inside our hook wrappers, ungated and
// across every hooked call (enter + passthrough exit + suppressed). Every
// component measured so far is individually cheap, yet the pump still reports
// ~231ms/frame blocked on the guest -- so measure the WHOLE instead of summing
// pieces and inferring. If wrapper_ms per frame approaches the frame time, the
// cost is ours and is spread thin across a huge call count rather than
// concentrated. If it is small, the guest is spending its time somewhere our
// hooks never see, and no amount of wrapper optimisation will help.
std::atomic<uint64_t> g_hook_wrapper_exit_us{0};
std::atomic<uint64_t> g_hook_wrapper_total_us{0};
// Enter-path segment attribution (temporary, 2026-07-29): run 414 measured
// the enter path at ~220ms of a ~250ms title frame across 5,790 calls/frame
// (~39us/call) with only ~6us/call in measured observer bodies. These name
// where the other ~32us/call go so the cut is surgical, not a guess.
std::atomic<uint64_t> g_hook_enter_seg_gtl_us{0};
std::atomic<uint64_t> g_hook_enter_seg_stats_us{0};
std::atomic<uint64_t> g_hook_enter_seg_misc_us{0};
std::atomic<uint64_t> g_hook_enter_seg_probes_us{0};
std::atomic<uint64_t> g_hook_enter_seg_allowed_us{0};
std::atomic<uint64_t> g_hook_wrapper_calls{0};
std::atomic<uint64_t> g_hook_phase_observe_enter_us{0};
// Args-capture cost (2026-07-29): the ~30-field RendererHookArgs struct build
// runs on EVERY hooked call at enter AND again at exit, so it was the largest
// unmeasured wrapper slice. All CaptureRendererHookArgsFor calls (both
// levels, enter and exit) accumulate here; reported as capture= in the
// wrapper census line. full_calls counts the kFull (r14-r31) captures so a
// run proves the light/full split is what the audit predicted.
std::atomic<uint64_t> g_hook_args_capture_us{0};
std::atomic<uint64_t> g_hook_args_capture_calls{0};
std::atomic<uint64_t> g_hook_args_capture_full_calls{0};
// Execution proof for the Release address dispatch in
// EmulateFableDirectResourceHook (2026-07-30, temporary): counts suppressed
// sub_821FC320 calls that took the integer fast path instead of the
// symbol-string ladder. Reported with the Release cost split in the
// per-symbol census dump.
std::atomic<uint64_t> g_release_dispatch_hits{0};
// Hook calls that entered with a resolved FableHookSymbolDesc (the per-call
// string-dispatch elimination). Reported in the wrapper census line as
// desc_hits= so a run proves coverage.
std::atomic<uint64_t> g_hook_desc_hits{0};
// Scene-scope marker short-circuit hits (the four pure scope markers running
// only the GTL depth pair). Counted separately because these calls skip the
// wrapper census timers entirely.
std::atomic<uint64_t> g_scene_scope_fast_hits{0};
// Wrapper fast-path split (2026-07-30): calls whose FableHookSymbolDesc
// proves the whole enter/exit body inert under the shipping policy and that
// therefore ran only the bare counters + begin-pin drain. Enter and exit are
// counted separately (a suppressed call has an enter but no passthrough
// exit). Reported as "hookpath fast=.../full=..." on the wrapper census line
// so the next run shows the split; like the census itself these are RUN
// TOTALS — take deltas between two lines.
std::atomic<uint64_t> g_hook_fast_path_enter_calls{0};
std::atomic<uint64_t> g_hook_fast_path_exit_calls{0};

// PER-SYMBOL ATTRIBUTION OF THE OBSERVE-ENTER PHASE (2026-07-28).
//
// Run 386 measured the title phase at 684ms/frame with 605ms of it inside our
// own hook wrappers, and the phase census pinned 37.6 SECONDS of that on
// ObserveRendererHookEnter alone (submit=1.4s, resource=2.1s, observe=5ms,
// gtlexit=63ms). But the ONLY existing per-symbol census is gated on
// g_panel_scroll_window_active, so it saw ~4.2s and could not name the feeds.
//
// A wholesale gate is already falsified: the comment at ObserveRendererHookEnter
// records that applying the draw/resource cvars there produced a BLACK SCREEN
// with no framerate gain, because that path is what feeds the direct renderer
// its per-draw state. So reduction has to be PER-FEED with its own falsifier —
// which needs per-symbol numbers.
//
// Lock-free and always-on. The existing census takes a global mutex + an
// unordered_map on every call; at ~11,000 hooked calls/sec that would add the
// very contention we are chasing. Keyed on the symbol POINTER, which is stable
// because every call site passes a string literal. Cost per call is a hash, an
// acquire load and two relaxed adds.
//
// CAVEAT: identical literals in different translation units may have distinct
// addresses, so one symbol can occupy two slots. Both are reported; sum them.
struct ObserveEnterCensusSlot {
  std::atomic<const char *> symbol{nullptr};
  std::atomic<uint64_t> calls{0};
  std::atomic<uint64_t> us{0};
};
constexpr size_t kObserveEnterCensusSlots = 256;
ObserveEnterCensusSlot g_observe_enter_census[kObserveEnterCensusSlots];
std::atomic<uint64_t> g_observe_enter_census_dropped{0};

void RecordObserveEnterCost(const char *symbol, uint64_t us) {
  if (symbol == nullptr) {
    return;
  }
  uintptr_t h = reinterpret_cast<uintptr_t>(symbol);
  h ^= h >> 16;
  h *= 0x9E3779B1u;
  h ^= h >> 13;
  for (size_t probe = 0; probe < 12; ++probe) {
    ObserveEnterCensusSlot &slot =
        g_observe_enter_census[(h + probe) & (kObserveEnterCensusSlots - 1)];
    const char *current = slot.symbol.load(std::memory_order_acquire);
    if (current == nullptr) {
      const char *expected = nullptr;
      if (!slot.symbol.compare_exchange_strong(expected, symbol,
                                               std::memory_order_acq_rel,
                                               std::memory_order_acquire) &&
          expected != symbol) {
        continue;  // lost the race to a different symbol; keep probing
      }
    } else if (current != symbol) {
      continue;
    }
    slot.calls.fetch_add(1, std::memory_order_relaxed);
    slot.us.fetch_add(us, std::memory_order_relaxed);
    return;
  }
  g_observe_enter_census_dropped.fetch_add(1, std::memory_order_relaxed);
}
// Per-target hit census. These counters have always been collected and NEVER
// reported, so we have been carrying 304 declared hooks with no idea which fire.
// Two products:
//   * zero-hit targets are removable dead weight
//   * the hottest targets are the candidates for suppressing at the ROOT of a
//     chain we already own end-to-end, instead of paying per leaf call inside it
void DumpRendererHookTargetCensus() {
  uint32_t live = 0;
  uint32_t dead = 0;
  for (uint32_t i = 0; i < kRendererHookTargetCount; ++i) {
    if (g_renderer_hook_stats[i].hit_count.load(std::memory_order_relaxed) !=
        0) {
      ++live;
    } else {
      ++dead;
    }
  }
  REXLOG_INFO("Refii hook target census targets={} live={} zero_hit={}",
              kRendererHookTargetCount, live, dead);
  std::vector<std::pair<uint64_t, const char *>> hot;
  hot.reserve(kRendererHookTargetCount);
  for (uint32_t i = 0; i < kRendererHookTargetCount; ++i) {
    const uint64_t hits =
        g_renderer_hook_stats[i].hit_count.load(std::memory_order_relaxed);
    if (hits == 0) {
      continue;
    }
    const RendererHookTarget *target =
        &kRendererHookTargets[i];
    hot.emplace_back(hits, target != nullptr ? target->name : "?");
  }
  std::sort(hot.begin(), hot.end(),
            [](const auto &a, const auto &b) { return a.first > b.first; });
  for (size_t i = 0; i < hot.size() && i < 12u; ++i) {
    REXLOG_INFO("Refii hook target hot #{} {} hits={}", i + 1, hot[i].second,
                static_cast<unsigned long long>(hot[i].first));
  }
}

std::atomic<uint64_t> g_hook_phase_submit_us{0};
std::atomic<uint64_t> g_hook_phase_resource_us{0};
std::atomic<uint64_t> g_hook_phase_observe_us{0};
std::atomic<uint64_t> g_hook_phase_gtlexit_us{0};
std::atomic<double> g_plume_pump_present_ms{0.0};
std::atomic<double> g_plume_pump_prebody_ms{0.0};
std::atomic<uint32_t> g_last_fable_device{0};
std::atomic<uint32_t> g_last_fable_renderer{0};
std::atomic<uint32_t> g_last_fable_renderer_owner{0};
std::atomic<uint32_t> g_last_fable_game{0};
std::atomic<uintptr_t> g_last_fable_memory_base{0};
std::atomic<uint32_t> g_last_fable_present_submitted{0};
std::atomic<uint32_t> g_last_fable_present_completed{0};
std::atomic<uint32_t> g_last_fable_swap_callbacks{0};
std::atomic<uint32_t> g_last_fable_present_pending{0};
std::mutex g_hook_timing_mutex;
std::mutex g_present_pump_mutex;
std::condition_variable g_present_pump_cv;
std::thread g_present_pump_thread;
// Guest-driven present (run 200 frame-loop fix): incremented by the GTL at each
// guest Present (0x82B6F1D0) via the registered callback below, and waited on by
// the pump so it composes+presents exactly ONE complete guest frame per guest
// Present instead of ticking on its own timer (which sliced guest frames = the
// fragmentation). `signal` is the guest-frame counter; `serviced` is how many
// the pump has consumed.
// Ring-wait probe accumulators. Defined HERE (before the pump) because the
// pump thread is the only place it is safe to log them: sub_822DFE08 runs
// inside the guest's stru_8349F7FC critical section, and logging from the hook
// deadlocked the title at frame 1 (build 105).
//
// The pump used to hardcode indices 9/10. It now iterates the whole table via
// g_wait_probe_names/g_wait_probe_count, because the frame-body bisection roots
// added 2026-07-28 push the count past what two hand-written indices can cover
// — and a probe whose number never gets printed is worse than no probe.
constexpr size_t kWaitProbeCapacity = 32;
std::atomic<uint64_t> wait_ns_storage[kWaitProbeCapacity]{};
std::atomic<uint64_t> *g_wait_probe_exits = nullptr;
std::atomic<uint64_t> *g_wait_probe_ns_public = nullptr;
const char *const *g_wait_probe_names = nullptr;
size_t g_wait_probe_count = 0;
// REAL producer publishes, distinguished by return address.
//
// The ProducerPublish root counts ALL calls to sub_821A5BD0, and that number is
// not a publish rate: the function has six call sites, and the one that
// actually exports the committed write cursor to the shared slot (0x28 -> 0x3C,
// making a batch visible to the consumer) is the `bl` at 0x8231A118, at the
// bottom of the frontend loop iteration. The other five — notably 0x821A5A04
// inside sub_821A59E0, which fires once per tick as well — are resyncs.
//
// This matters because the 30Hz gate at 0x82319D70 makes 30/sec a HARD CEILING
// (and 0x82319E1C discards any backlog over 2 ticks rather than catching up).
// A measured rate above 30/sec is therefore proof the counter is wrong, not
// proof the producer is healthy — which is exactly the error that made an
// earlier "39/sec, producer is fine" reading refute producer starvation when it
// had no standing to.
constexpr uint32_t kProducerPublishReturnAddress = 0x8231A11Cu;
std::atomic<uint64_t> g_producer_true_publishes{0};
std::atomic<uint64_t> g_guest_present_signal{0};
std::atomic<uint64_t> g_guest_present_serviced{0};
// Called by the GTL under its state mutex — SIGNAL ONLY (notify, no heavy lock).
void OnGuestFramePresentedFromGtl() {
  g_guest_present_signal.fetch_add(1, std::memory_order_release);
  g_present_pump_cv.notify_one();
}
// Present-decoupling STEP 6 (2026-08-02): capture-tied fence ack.
//
// Our port synthesizes the guest's entire present-completion contract at hook
// time — SetFence writes the fence write-back the moment the fence is minted,
// BlockOnFence publishes instantly, and the Present body (0x82B6F1D0) is
// suppressed — so the guest NEVER waits on the renderer and its only ack point
// is the return of the Present hook itself. On real hardware Present N's one
// hard gate is BlockOnFence on fence_{N-1} = ring CONSUMPTION through frame
// N-1 (0x82BA3B64 checks the fence saved at 0x82BA3BD0). Our equivalent of
// "the GPU consumed frame X" is "frame X's drain + deferred captures + record
// completed" (all record-time guest-memory reads done). This gate restores the
// guest's native depth-1 overlap on that translated meaning: Present N's hook
// return waits until captures through boundary N-1 are complete, so the guest
// may build N+1 while we capture N (the native overlap, accepted torn-read
// window) but can never run 2+ frames ahead of the capture pipeline — the
// safety mechanism the roadmap requires BEFORE present overlap deepens.
//
// The ack is delayed on the GUEST thread inside its own Present hook, NEVER in
// the host present path (07-27 starvation post-mortem: presents, pump wakes,
// TryAdvanceFablePresentationFromPump and startup-video/Bink servicing are all
// untouched). The wait is bounded so a stalled record path degrades to the
// status quo instead of hanging the guest, and it disarms until the record
// pipeline has proven itself (first publish) and during the startup-video
// pump (video exempt, matching the old gate).
REXCVAR_DEFINE_BOOL(renderer_capture_tied_ack, true, "Renderer/Plume",
                    "Capture-tied pipeline gate (present-decoupling step 6): "
                    "hold guest Present N's hook return until frame N-1's "
                    "drain + deferred texture captures + record completed "
                    "(capture-then-ack, depth 1 = the guest's native "
                    "BlockOnFence overlap). Bounded wait; delays only the "
                    "guest ack, never a host present. Disable for A/B to "
                    "restore the instant ack.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);
std::mutex g_capture_ack_mutex;
std::condition_variable g_capture_ack_cv;
// Highest guest present boundary whose captures are fully complete (published
// by the record path in plume_backend.cpp after RecordFableDirectFrame
// returns — every CPU-side guest-memory read for that frame is done by then).
std::atomic<uint64_t> g_capture_ack_captured_boundary{0};
std::atomic<uint64_t> g_capture_ack_publishes{0};
// Gate census: checks = ALL gate evaluations (cvar-on present boundaries),
// disarms = evaluations skipped by the publish-bootstrap/startup-video
// guards (armed = checks - disarms), waits = boundaries that actually
// blocked, wait_us = total blocked time, timeouts = bounded-wait expiries
// (record pipeline slower than the bound — gate degraded to advisory),
// max/last_outstanding = present_seq - captured_seq observed at gate entry
// (0 or 1 = gate is keeping depth 1 for free).
std::atomic<uint64_t> g_capture_ack_gate_checks{0};
std::atomic<uint64_t> g_capture_ack_gate_disarms{0};
std::atomic<uint64_t> g_capture_ack_gate_waits{0};
std::atomic<uint64_t> g_capture_ack_gate_wait_us{0};
std::atomic<uint64_t> g_capture_ack_gate_timeouts{0};
std::atomic<uint64_t> g_capture_ack_gate_max_outstanding{0};
std::atomic<uint64_t> g_capture_ack_gate_last_outstanding{0};
std::once_flag g_present_pump_atexit_once;
std::mutex g_startup_video_pump_mutex;
PPCContext g_startup_video_pump_context = {};
uint8_t *g_startup_video_pump_base = nullptr;
uint32_t g_startup_video_pump_wrapper = 0;
uint32_t g_startup_video_pump_update = 0;
uint32_t g_startup_video_pump_stack = 0;
std::atomic<uint32_t> g_startup_video_pump_requested_update{0};
std::atomic<uint32_t> g_startup_video_pump_serviced_update{0};
std::atomic<uint32_t> g_startup_video_pump_init_requested_update{0};
std::atomic<uint32_t> g_startup_video_pump_init_completed_update{0};
std::atomic<uint32_t> g_startup_video_pump_dispatch_requested_update{0};
std::atomic<uint32_t> g_startup_video_pump_dispatch_completed_update{0};
std::atomic<uint32_t> g_startup_video_pump_native_update_waiting_update{0};
std::atomic_bool g_startup_video_pump_in_flight{false};
std::atomic<uint32_t> g_startup_video_pump_launch_count{0};
std::atomic<uint32_t> g_startup_video_pump_complete_count{0};
std::atomic<uint32_t> g_startup_video_pump_completed_update{0};
std::atomic<uint32_t> g_startup_video_pump_in_flight_request_logs{0};
std::atomic<uint32_t> g_startup_video_observed_bink_blocked_update{0};
std::mutex g_startup_video_observed_bink_mutex;
uint8_t *g_startup_video_observed_bink_base = nullptr;
uint32_t g_startup_video_observed_bink_movie = 0;
std::atomic<int64_t> g_startup_video_observed_bink_ns{0};
std::atomic<uint32_t> g_startup_video_observed_bink_active_logs{0};
std::atomic_bool g_plume_bink_video_upload_ready{false};
std::atomic<uint64_t> g_plume_bink_video_upload_ready_count{0};
std::atomic<int64_t> g_plume_bink_video_upload_ready_ns{0};
std::atomic<int64_t> g_startup_video_bink_finished_ns{0};
char g_last_hook_symbol[64] = {};
char g_last_renderer_setup_symbol[64] = {};
char g_last_display_present_symbol[64] = {};
char g_last_plume_present_symbol[64] = {};
char g_last_plume_present_reason[32] = {};
char g_last_guest_suppressed_symbol[64] = {};
// Set once the storybook panel submits start; gates the panel inner-call
// timeline probe so its cap is not consumed by boot-time traffic.
std::atomic_bool g_panel_scroll_window_active{false};
std::unordered_set<std::string> g_logged_hook_decisions;
std::unordered_set<std::string> g_logged_guest_hook_suppressed;
std::mutex g_fable_unsupported_adapter_shape_mutex;
std::unordered_map<std::string, uint64_t>
    g_fable_unsupported_adapter_shape_counts;
uint32_t g_logged_fable_cpu_layout_source_alias_remaps = 0;
uint32_t g_logged_fable_cpu_layout_source_alias_blanks = 0;
uint32_t g_logged_fable_cpu_layout_blank_fast_path_rejects = 0;

namespace {

using RendererTraceClock = std::chrono::steady_clock;

int64_t RendererTraceNowNs() {
  return std::chrono::duration_cast<std::chrono::nanoseconds>(
             RendererTraceClock::now().time_since_epoch())
      .count();
}

bool IsMandatoryFableResourceReplacementHook(std::string_view symbol);
bool IsFableGuestTerminalDrawSubmissionHook(std::string_view symbol);



void MaybeLogRendererHookPolicy() {
  if (g_logged_renderer_root_only_hook_policy || !RendererHooksEnabled()) {
    return;
  }

  REXLOG_INFO(
      "Refii renderer hook policy active root_only={} deep_observer={} "
      "default_surface=required-root observer_surface=deep-observer",
      RendererRootOnlyHooksEnabled(), RendererDeepObserverHooksEnabled());
  g_logged_renderer_root_only_hook_policy = true;
}

// Running totals for the two Tracy plots below. These used to be recomputed by
// SumRendererHookCounter, which walks ALL 63 RendererHookAtomicStats entries
// (~2 KiB of shared, constantly-written memory) with a relaxed atomic load
// each. That ran THREE times per hooked guest call -- 189 atomic loads over
// contended cache lines, evaluated unconditionally as TracyPlot arguments even
// though TRACY_ON_DEMAND means nothing consumes them unless a profiler is
// attached. Maintaining the sums where the per-target counters are already
// bumped makes the plots O(1) and byte-identical in value.
std::atomic<uint64_t> g_renderer_hook_hit_total{0};
std::atomic<uint64_t> g_renderer_hook_passthrough_total{0};

void RecordRendererHookStatsForTarget(const RendererHookTarget *target,
                                      bool guest_passthrough);

void RecordRendererHookStats(std::string_view symbol, bool guest_passthrough) {
  RecordRendererHookStatsForTarget(ResolveRendererHookTarget(symbol),
                                   guest_passthrough);
}

// Desc fast path: the target pointer was resolved once at descriptor build, so
// the per-call string resolve (memoised, but still a call + census upkeep at
// -O0) disappears. Per-symbol fire counting moves to desc.hits.
void RecordRendererHookStatsForDesc(const FableHookSymbolDesc &desc,
                                    bool guest_passthrough) {
  if (!guest_passthrough) {
    desc.hits.fetch_add(1, std::memory_order_relaxed);
  }
  RecordRendererHookStatsForTarget(desc.target, guest_passthrough);
}

void RecordRendererHookStatsForTarget(const RendererHookTarget *target,
                                      bool guest_passthrough) {
  if (target == nullptr) {
    return;
  }

  const uint32_t index = static_cast<uint32_t>(target->id);
  if (index >= kRendererHookTargetCount) {
    return;
  }

  RendererHookAtomicStats &stats = g_renderer_hook_stats[index];
  if (!guest_passthrough) {
    const uint64_t previous =
        stats.hit_count.fetch_add(1, std::memory_order_relaxed);
    g_renderer_hook_hit_total.fetch_add(1, std::memory_order_relaxed);
    const uint64_t frame = CurrentPlumeOutputFrameIndex();
    if (previous == 0) {
      stats.first_frame_seen.store(frame, std::memory_order_relaxed);
    }
    stats.last_frame_seen.store(frame, std::memory_order_relaxed);
    return;
  }

  stats.guest_passthrough_count.fetch_add(1, std::memory_order_relaxed);
  g_renderer_hook_passthrough_total.fetch_add(1, std::memory_order_relaxed);
}

uint64_t SumRendererHookCounter(
    std::atomic<uint64_t> RendererHookAtomicStats::*counter_member) {
  uint64_t total = 0;
  for (const RendererHookAtomicStats &stats : g_renderer_hook_stats) {
    total += (stats.*counter_member).load(std::memory_order_relaxed);
  }
  return total;
}

void ConfigureRendererTracyPlots() {
  static std::once_flag configure_once;
  std::call_once(configure_once, [] {
    TracyPlotConfig("Refii Renderer Hook Gap Us", tracy::PlotFormatType::Number,
                    false, false, 0x38BDF8);
    TracyPlotConfig("Refii Renderer Draw Hook Gap Us",
                    tracy::PlotFormatType::Number, false, false, 0x22C55E);
    TracyPlotConfig("Refii Fable Present Hook Gap Us",
                    tracy::PlotFormatType::Number, false, false, 0xF59E0B);
    TracyPlotConfig("Refii Plume Present Request Gap Us",
                    tracy::PlotFormatType::Number, false, false, 0xA855F7);
    TracyPlotConfig("Refii Plume Present Pump Gap Us",
                    tracy::PlotFormatType::Number, false, false, 0x14B8A6);
    TracyPlotConfig("Refii Plume Present Result", tracy::PlotFormatType::Number,
                    true, false, 0x84CC16);
    TracyPlotConfig("Refii Renderer Hook Kind", tracy::PlotFormatType::Number,
                    true, false, 0x60A5FA);
    TracyPlotConfig("Refii.RendererHookHits", tracy::PlotFormatType::Number,
                    false, false, 0x2DD4BF);
    TracyPlotConfig("Refii.RendererHookGuestPassthroughs",
                    tracy::PlotFormatType::Number, false, false, 0xF97316);
    TracyPlotConfig("Refii Fable Presents Pending",
                    tracy::PlotFormatType::Number, true, false, 0xFB7185);
  });
}

void CopyTimingText(char *destination, size_t destination_size,
                    std::string_view text) {
  if (destination_size == 0) {
    return;
  }
  const size_t copy_size = std::min(destination_size - 1, text.size());
  std::memcpy(destination, text.data(), copy_size);
  destination[copy_size] = '\0';
}

void SetLastTimingText(char *destination, size_t destination_size,
                       std::string_view text) {
  std::lock_guard lock(g_hook_timing_mutex);
  CopyTimingText(destination, destination_size, text);
}

int64_t PlotDeltaUs(const char *name, std::atomic<int64_t> &previous_ns,
                    int64_t now_ns) {
  const int64_t prior_ns =
      previous_ns.exchange(now_ns, std::memory_order_relaxed);
  if (prior_ns == 0) {
    return 0;
  }
  const int64_t delta_us = (now_ns - prior_ns) / 1000;
  TracyPlot(name, delta_us);
  return delta_us;
}

std::chrono::nanoseconds PlumePresentPumpInterval(uint32_t target_fps) {
  target_fps = std::clamp<uint32_t>(target_fps, 1, 240);
  return std::chrono::nanoseconds(1000000000ull / target_fps);
}


bool LooksLikeFableStartupVideoUpdate(uint8_t *base, uint32_t update_guest) {
  constexpr uint32_t kStartupVideoUpdateVtable = 0x820A8FA8u;
  return IsReadableGuestByteRange(base, update_guest, 104u) &&
         LoadGuestU32(base, update_guest) == kStartupVideoUpdateVtable;
}


bool FableStartupVideoUpdateReadyForPump(uint8_t *base,
                                         uint32_t update_guest) {
  if (!LooksLikeFableStartupVideoUpdate(base, update_guest) ||
      FableStartupVideoUpdateComplete(base, update_guest)) {
    return false;
  }

  const uint32_t frame_counter = LoadGuestU32(base, update_guest + 4u);
  const uint32_t active_movie = LoadGuestU32(base, update_guest + 8u);
  const uint32_t movie_state = LoadGuestU32(base, update_guest + 12u);
  const uint32_t pump_state = LoadGuestU32(base, update_guest + 24u);
  return frame_counter != 0 && active_movie != 0 && movie_state != 0 &&
         pump_state != 0;
}

bool FableStartupVideoUpdateHasPumpState(uint8_t *base,
                                         uint32_t update_guest) {
  if (!LooksLikeFableStartupVideoUpdate(base, update_guest) ||
      FableStartupVideoUpdateComplete(base, update_guest)) {
    return false;
  }

  return LoadGuestU32(base, update_guest + 8u) != 0 &&
         LoadGuestU32(base, update_guest + 12u) != 0 &&
         LoadGuestU32(base, update_guest + 24u) != 0;
}

bool LooksLikeFableStartupBinkMovie(uint8_t *base, uint32_t movie_guest) {
  if (base == nullptr || movie_guest == 0 ||
      !IsReadableGuestByteRange(base, movie_guest, 860u)) {
    return false;
  }

  const uint32_t total_frames = LoadGuestU32(base, movie_guest + 8u);
  const uint32_t rate = LoadGuestU32(base, movie_guest + 20u);
  const uint32_t divisor = LoadGuestU32(base, movie_guest + 24u);
  const uint32_t plane_count = LoadGuestU32(base, movie_guest + 276u);
  const uint32_t plane_base = LoadGuestU32(base, movie_guest + 280u);
  if (total_frames == 0 || rate == 0 || divisor == 0 || plane_count == 0 ||
      plane_count > 4u || plane_base == 0) {
    return false;
  }

  for (uint32_t plane_index = 0; plane_index < plane_count; ++plane_index) {
    const uint32_t plane_guest = plane_base + plane_index * 388u;
    if (!IsReadableGuestByteRange(base, plane_guest, 388u) ||
        LoadGuestU32(base, plane_guest + 348u) != 0x832BDEA0u ||
        LoadGuestU32(base, plane_guest + 352u) != 0x832BE060u ||
        LoadGuestU32(base, plane_guest + 356u) != 0x832BE238u) {
      return false;
    }
  }

  return true;
}

bool FableStartupBinkMovieStillPlaying(uint8_t *base, uint32_t movie_guest) {
  if (!LooksLikeFableStartupBinkMovie(base, movie_guest)) {
    return false;
  }

  const uint32_t total_frames = LoadGuestU32(base, movie_guest + 8u);
  const uint32_t current_frame = LoadGuestU32(base, movie_guest + 12u);
  return total_frames > 1u && current_frame < total_frames;
}



void LogFableStartupVideoPumpDeferredForUpdate(uint8_t *base,
                                               uint32_t update) {
  if (g_logged_plume_pump_startup_video_deferred_for_update) {
    return;
  }

  REXLOG_INFO(
      "Refii plume present pump deferred startup video run until PPC "
      "update is initialized update=0x{:08X} frame_counter={} "
      "active_movie=0x{:08X} movie_state=0x{:08X} pump_state=0x{:08X} "
      "vector=0x{:08X}-0x{:08X} index={}",
      update, LoadGuestU32(base, update + 4u), LoadGuestU32(base, update + 8u),
      LoadGuestU32(base, update + 12u), LoadGuestU32(base, update + 24u),
      LoadGuestU32(base, update + 56u), LoadGuestU32(base, update + 60u),
      LoadGuestU32(base, update + 84u));
  g_logged_plume_pump_startup_video_deferred_for_update = true;
}

void LogFableStartupVideoPumpAlreadyInFlight(uint8_t *base, uint32_t wrapper,
                                             uint32_t update,
                                             bool observed_bink_active,
                                             bool update_complete,
                                             bool update_ready,
                                             bool has_pump_state) {
  const uint32_t count =
      g_startup_video_pump_in_flight_request_logs.fetch_add(
          1, std::memory_order_relaxed) +
      1u;
  const bool verbose_startup_video_logs = RendererDeepObserverHooksEnabled();
  const uint32_t initial_log_limit = verbose_startup_video_logs ? 8u : 2u;
  const uint32_t periodic_log_interval =
      verbose_startup_video_logs ? 120u : 480u;
  if (count > initial_log_limit && count % periodic_log_interval != 0u) {
    return;
  }

  uint32_t frame_counter = 0;
  uint32_t active_movie = 0;
  uint32_t movie_state = 0;
  uint32_t pump_state = 0;
  uint32_t video_index = 0;
  if (base != nullptr && LooksLikeFableStartupVideoUpdate(base, update)) {
    frame_counter = LoadGuestU32(base, update + 4u);
    active_movie = LoadGuestU32(base, update + 8u);
    movie_state = LoadGuestU32(base, update + 12u);
    pump_state = LoadGuestU32(base, update + 24u);
    video_index = LoadGuestU32(base, update + 84u);
  }

  REXLOG_INFO(
      "Refii plume present pump startup video request already in flight #{} "
      "wrapper=0x{:08X} update=0x{:08X} observed_bink={} complete={} "
      "ready={} has_state={} requested=0x{:08X} serviced=0x{:08X} "
      "launches={} completes={} frame_counter={} active_movie=0x{:08X} "
      "movie_state=0x{:08X} pump_state=0x{:08X} index={}",
      count, wrapper, update, observed_bink_active, update_complete,
      update_ready, has_pump_state,
      g_startup_video_pump_requested_update.load(std::memory_order_acquire),
      g_startup_video_pump_serviced_update.load(std::memory_order_acquire),
      g_startup_video_pump_launch_count.load(std::memory_order_acquire),
      g_startup_video_pump_complete_count.load(std::memory_order_acquire),
      frame_counter, active_movie, movie_state, pump_state, video_index);
}


uint32_t AllocateFableStartupVideoPumpStack(PPCContext ctx, uint8_t *base) {
  constexpr uint32_t kStackBytes = 64u * 1024u;
  constexpr uint32_t kStackReserve = 0x100u;
  ctx.r3.u64 = kStackBytes;
  ctx.r4.u64 = 0;
  rex_lhMemAlloc(ctx, base);
  const uint32_t stack_base = ctx.r3.u32;
  if (!IsReadableGuestByteRange(base, stack_base, kStackBytes)) {
    return 0;
  }

  return (stack_base + kStackBytes - kStackReserve) & ~0xFu;
}

void RequestFableStartupVideoPumpFromPlume() {
  uint8_t *base = nullptr;
  uint32_t wrapper = 0;
  uint32_t update = 0;

  {
    std::lock_guard lock(g_startup_video_pump_mutex);
    base = g_startup_video_pump_base;
    wrapper = g_startup_video_pump_wrapper;
    update = g_startup_video_pump_update;
  }

  if (update != 0 &&
      g_startup_video_pump_completed_update.load(std::memory_order_acquire) ==
          update) {
    return;
  }

  const bool observed_bink_active =
      base != nullptr ? FableStartupVideoObservedBinkActive(base) : false;
  const bool update_complete =
      base != nullptr && update != 0
          ? FableStartupVideoUpdateComplete(base, update)
          : true;
  const bool final_update_pending =
      update_complete &&
      g_startup_video_pump_completed_update.load(std::memory_order_acquire) ==
          0;
  if (base == nullptr || wrapper == 0 || update == 0 ||
      (update_complete && !observed_bink_active && !final_update_pending)) {
    return;
  }

  const bool initial_launch =
      g_startup_video_pump_launch_count.load(std::memory_order_acquire) == 0;
  const bool update_ready = FableStartupVideoUpdateReadyForPump(base, update);
  const bool has_pump_state = FableStartupVideoUpdateHasPumpState(base, update);
  if (!final_update_pending && !initial_launch && !observed_bink_active &&
      !update_ready && !has_pump_state) {
    LogFableStartupVideoPumpDeferredForUpdate(base, update);
    return;
  }

  g_startup_video_pump_requested_update.store(update,
                                              std::memory_order_release);

  if (g_startup_video_pump_init_completed_update.load(
          std::memory_order_acquire) != update) {
    g_startup_video_pump_init_requested_update.store(
        update, std::memory_order_release);
    if (!g_logged_plume_pump_startup_video_wait_native_init) {
      REXLOG_INFO(
          "Refii plume present pump waiting for native startup video init owner "
          "wrapper=0x{:08X} update=0x{:08X} frame_counter={} active_movie=0x{:08X} "
          "movie_state=0x{:08X} pump_state=0x{:08X} vector=0x{:08X}-0x{:08X} "
          "index={}",
          wrapper,
          update,
          LoadGuestU32(base, update + 4u),
          LoadGuestU32(base, update + 8u),
          LoadGuestU32(base, update + 12u),
          LoadGuestU32(base, update + 24u),
          LoadGuestU32(base, update + 56u),
          LoadGuestU32(base, update + 60u),
          LoadGuestU32(base, update + 84u));
      g_logged_plume_pump_startup_video_wait_native_init = true;
    }
    return;
  }

  bool expected_in_flight = false;
  if (!g_startup_video_pump_in_flight.compare_exchange_strong(
          expected_in_flight, true, std::memory_order_acq_rel)) {
    LogFableStartupVideoPumpAlreadyInFlight(
        base, wrapper, update, observed_bink_active, update_complete,
        update_ready, has_pump_state);
    return;
  }

  if (g_startup_video_pump_launch_count.load(std::memory_order_acquire) != 0) {
    g_startup_video_pump_serviced_update.store(update,
                                               std::memory_order_release);
    g_startup_video_pump_in_flight.store(true, std::memory_order_release);
    LogFableStartupVideoPumpAlreadyInFlight(
        base, wrapper, update, observed_bink_active, update_complete,
        update_ready, has_pump_state);
    return;
  }

  g_startup_video_pump_native_update_waiting_update.store(
      update, std::memory_order_release);
  g_startup_video_pump_serviced_update.store(update,
                                             std::memory_order_release);
  g_startup_video_pump_in_flight.store(true, std::memory_order_release);
  if (!g_logged_plume_pump_startup_video_wait_native_update) {
    REXLOG_INFO(
        "Refii plume present pump waiting for native startup video update owner "
        "wrapper=0x{:08X} update=0x{:08X} frame_counter={} active_movie=0x{:08X} "
        "movie_state=0x{:08X} pump_state=0x{:08X} vector=0x{:08X}-0x{:08X} "
        "index={}",
        wrapper,
        update,
        LoadGuestU32(base, update + 4u),
        LoadGuestU32(base, update + 8u),
        LoadGuestU32(base, update + 12u),
        LoadGuestU32(base, update + 24u),
        LoadGuestU32(base, update + 56u),
        LoadGuestU32(base, update + 60u),
        LoadGuestU32(base, update + 84u));
    g_logged_plume_pump_startup_video_wait_native_update = true;
  }
}





bool IsLikelyGuestDwordAddress(uint32_t guest_address);

void StoreFablePresentCounters(uint32_t device, uint32_t submitted,
                               uint32_t callbacks, uint32_t completed) {
  g_last_fable_device.store(device, std::memory_order_relaxed);
  g_last_fable_present_submitted.store(submitted, std::memory_order_relaxed);
  g_last_fable_swap_callbacks.store(callbacks, std::memory_order_relaxed);
  g_last_fable_present_completed.store(completed, std::memory_order_relaxed);
  g_last_fable_present_pending.store(submitted - completed,
                                     std::memory_order_relaxed);
  TracyPlot("Refii Fable Presents Pending",
            static_cast<int64_t>(submitted - completed));
}

void RememberFableMemoryBase(uint8_t *base) {
  if (base != nullptr) {
    g_last_fable_memory_base.store(reinterpret_cast<uintptr_t>(base),
                                   std::memory_order_relaxed);
  }
}

void EmulatePlumeOwnedFableSyncIntervalBookkeeping(uint32_t device,
                                                   uint8_t *base) {
  if (base == nullptr || device == 0) {
    return;
  }

  constexpr uint32_t kDisplayControlOffset = 0x2ABC;
  constexpr uint32_t kDisplayFlags2Offset = 0x2ABD;
  constexpr uint32_t kScanoutSurfaceOffset = 0x2AB4;
  constexpr uint32_t kScanoutAuxOffset = 0x2AB8;
  constexpr uint32_t kPersistSurfaceOffset = 0x31A4;
  constexpr uint32_t kPersistAuxOffset = 0x31A8;
  constexpr uint32_t kPersistModeOffset = 0x31AC;

  StoreGuestU32(base, device + kPersistSurfaceOffset, 0xFFFFFFFFu);
  StoreGuestU32(base, device + kPersistAuxOffset, 0);
  StoreGuestU32(base, device + kPersistModeOffset, 0);
  StoreGuestU32(base, device + kScanoutSurfaceOffset, 0);
  StoreGuestU32(base, device + kScanoutAuxOffset, 0);
  StoreGuestU8(base, device + kDisplayControlOffset,
               LoadGuestU8(base, device + kDisplayControlOffset) | 1u);
  StoreGuestU8(base, device + kDisplayFlags2Offset,
               LoadGuestU8(base, device + kDisplayFlags2Offset) | 0x10u);
}


void CompleteFableDisplaySwapBookkeeping(uint32_t device, uint8_t *base,
                                         uint32_t submitted) {
  if (base == nullptr || device == 0 || submitted == 0) {
    return;
  }

  constexpr uint32_t kSwapCallbackOffset = 0x40B4;
  constexpr uint32_t kCompletedOffset = 0x40B8;
  constexpr uint32_t kCurrentBackBufferOffset = 0x2A9C;
  constexpr uint32_t kLastBackBufferOffset = 0x2AB0;
  constexpr uint32_t kDisplayFlagsOffset = 0x2ABD;

  const uint32_t callbacks = std::max(
      LoadGuestU32(base, device + kSwapCallbackOffset) + 1u, submitted);
  const uint32_t completed =
      std::max(LoadGuestU32(base, device + kCompletedOffset), submitted);
  StoreGuestU32(base, device + kSwapCallbackOffset, callbacks);
  StoreGuestU32(base, device + kCompletedOffset, completed);
  EmulatePlumeOwnedFableSyncIntervalBookkeeping(device, base);

  const uint32_t current_back_buffer =
      LoadGuestU32(base, device + kCurrentBackBufferOffset);
  StoreGuestU32(base, device + kLastBackBufferOffset, current_back_buffer);
  StoreGuestU8(base, device + kDisplayFlagsOffset,
               LoadGuestU8(base, device + kDisplayFlagsOffset) & 0xEFu);
  EmulatePlumeOwnedFablePixCaptureControlBookkeeping(device, base);

  StoreFablePresentCounters(device, submitted, callbacks, completed);
  if (!g_logged_fable_present_bookkeeping) {
    REXLOG_INFO("Refii plume is emulating Fable display bookkeeping for "
                "Plume-owned presentation "
                "device=0x{:08X} submitted={} completed={} callbacks={}",
                device, submitted, completed, callbacks);
    g_logged_fable_present_bookkeeping = true;
  }
}


void CompletePlumeOwnedFableDisplaySwapBookkeeping(const RendererHookArgs &args,
                                                   uint8_t *base) {
  if (base == nullptr || args.r3 == 0) {
    return;
  }

  constexpr uint32_t kSubmittedOffset = 0x40B0;
  const uint32_t device = args.r3;
  RememberFableMemoryBase(base);
  CompleteFableDisplaySwapBookkeeping(
      device, base, LoadGuestU32(base, device + kSubmittedOffset));
}

void LogPlumePumpFablePresentationPolicy() {
  if (!g_logged_plume_pump_fable_bookkeeping) {
    REXLOG_INFO(
        "Refii plume present pump may advance Fable display bookkeeping when "
        "Plume-owned display-swap hooks stop arriving");
    g_logged_plume_pump_fable_bookkeeping = true;
  }
}

bool TryAdvanceFablePresentationFromPump(uint32_t target_fps) {
  if (!RendererPlumePresentPumpFableBookkeepingEnabled()) {
    return false;
  }

  uint8_t *base =
      reinterpret_cast<uint8_t *>(g_last_fable_memory_base.load(
          std::memory_order_relaxed));
  const uint32_t device = g_last_fable_device.load(std::memory_order_relaxed);
  if (base == nullptr || device == 0) {
    return false;
  }

  const int64_t now_ns = RendererTraceNowNs();
  const int64_t last_display_ns =
      g_last_display_present_hook_ns.load(std::memory_order_relaxed);
  const int64_t interval_ns =
      static_cast<int64_t>(PlumePresentPumpInterval(target_fps).count());
  const bool startup_video_active = IsFableStartupVideoPumpActive();
  if (!startup_video_active && last_display_ns != 0 &&
      now_ns - last_display_ns < interval_ns * 2) {
    return false;
  }

  constexpr uint32_t kSubmittedOffset = 0x40B0;
  constexpr uint32_t kCompletedOffset = 0x40B8;
  const uint32_t submitted = LoadGuestU32(base, device + kSubmittedOffset);
  const uint32_t completed = LoadGuestU32(base, device + kCompletedOffset);
  if (submitted != completed) {
    CompleteFableDisplaySwapBookkeeping(device, base, submitted);
    g_plume_present_pump_fable_advance_count.fetch_add(
        1, std::memory_order_relaxed);
    if (!g_logged_plume_pump_fable_bookkeeping_complete) {
      REXLOG_INFO(
          "Refii plume present pump is completing pending Fable display "
          "bookkeeping device=0x{:08X} submitted={} completed={}",
          device, submitted, completed);
      g_logged_plume_pump_fable_bookkeeping_complete = true;
    }
    return true;
  }

  const uint32_t next_submitted = submitted + 1u;
  StoreGuestU32(base, device + kSubmittedOffset, next_submitted);
  CompleteFableDisplaySwapBookkeeping(device, base, next_submitted);
  g_plume_present_pump_fable_advance_count.fetch_add(
      1, std::memory_order_relaxed);

  if (!g_logged_plume_pump_fable_bookkeeping_advance) {
    REXLOG_INFO(
        "Refii plume present pump is advancing Fable display bookkeeping "
        "device=0x{:08X} submitted={}",
        device, next_submitted);
    g_logged_plume_pump_fable_bookkeeping_advance = true;
  }
  return true;
}

bool PresentPlumeFrameFromRendererHook(const char *symbol,
                                       std::string_view reason,
                                       bool drive_fable_frame = false) {
  ZoneNamedN(tracy_present_zone, "RefiiRenderer/PresentPlumeFrameFromHook",
             true);
  ZoneTextV(tracy_present_zone, symbol, std::strlen(symbol));
  ZoneTextV(tracy_present_zone, reason.data(), reason.size());
  ConfigureRendererTracyPlots();

  const int64_t now_ns = RendererTraceNowNs();
  g_plume_present_request_count.fetch_add(1, std::memory_order_relaxed);
  g_last_plume_present_request_gap_us.store(
      PlotDeltaUs("Refii Plume Present Request Gap Us",
                  g_last_plume_present_request_ns, now_ns),
      std::memory_order_relaxed);
  {
    std::lock_guard lock(g_hook_timing_mutex);
    CopyTimingText(g_last_plume_present_symbol,
                   sizeof(g_last_plume_present_symbol), symbol);
    CopyTimingText(g_last_plume_present_reason,
                   sizeof(g_last_plume_present_reason), reason);
  }

  const bool presented = PresentPlumeFrame(drive_fable_frame);
  TracyPlot("Refii Plume Present Result",
            static_cast<int64_t>(presented ? 1 : 0));
  return presented;
}

void PlumePresentPumpMain() {
  ZoneScopedN("RefiiRenderer/PlumePresentPumpMain");
  ConfigureRendererTracyPlots();
  REXLOG_INFO("Refii plume present pump started");
  // Register the guest-Present signal so the pump can run GUEST-DRIVEN.
  refii::gtl::SetGuestFramePresentedCallback(&OnGuestFramePresentedFromGtl);

  auto next_present = RendererTraceClock::now();
  while (!g_plume_present_pump_stop_requested.load(std::memory_order_acquire)) {
    const uint32_t target_fps = RendererPlumePresentTargetFps();
    const std::chrono::nanoseconds interval =
        PlumePresentPumpInterval(target_fps);
    const bool guest_driven = RendererPlumeGuestDrivenPresentEnabled();
    if (guest_driven) {
      // GUEST-DRIVEN: wake on the next guest Present (one complete guest frame
      // per guest Present — frame-aligned, no independent FPS slicing).
      constexpr auto kGuestStallHeartbeat = std::chrono::milliseconds(250);
      // DECOUPLED PRESENT (2026-07-27). The guest renders from a 30Hz
      // fixed-timestep main loop (sub_82319BE8) and we deliberately do NOT
      // change that — game speed is a function of its tick rate, so touching it
      // changes how the game plays. Instead the pump stops being *paced* by the
      // guest: it wakes at the host present interval as well as on a guest
      // frame. Frame alignment is untouched (a partial guest frame is still
      // never composed); only the display cadence is decoupled from the tick.
      //
      // This is only affordable because of the record skip in the present
      // body. Before it, a wake with no new guest frame still cost a full
      // ~95ms record of an unchanged frame (run 327:
      // heartbeat_timeouts=109/120 with attempted/recorded/submitted identical
      // across consecutive composes) — roughly 380ms of CPU per second
      // rebuilding what was already on screen, while holding the locks the
      // guest needs to submit. A 60Hz wake was REVERTED on 2026-07-27 for
      // exactly that reason: waking faster while every wake still composed
      // multiplied the redundant ~95ms compose instead of relieving it.
      //
      // FAST WAKE (present-decoupling step 4, 2026-07-30). The condition the
      // revert note demanded now holds: the present body's step-3 fast path
      // (skip_record + FableDirectFrameRecordHasNewContent, run 459 verified)
      // re-presents an unchanged frame as a compose-target blit and records
      // only on new guest content, while presents and ALL guest servicing
      // (startup-video pump, Bink audio cursors, swap acknowledgement) stay
      // on every wake by construction. So a wake without a guest signal costs
      // ~a blit, and the pump can wake at the target-present interval.
      // Guest-signal and Bink-upload wakes behave exactly as before — this
      // only shortens the timer, never skips anything. Opt-out:
      // REX_RENDERER_PRESENT_FAST_WAKE=0 restores the flat 250ms heartbeat.
      const auto wake_interval =
          RendererPresentFastWakeEnabled()
              ? interval
              : std::chrono::duration_cast<std::chrono::nanoseconds>(
                    kGuestStallHeartbeat);
      // How long the pump BLOCKS here is the single most important number for
      // deciding where to spend optimisation effort, and nothing measured it.
      // Renderer work and this wait are serialised in the same loop, so the
      // frame period is (wait + our work). If the wait dominates, the guest is
      // the limiter and further renderer work buys nothing; if it is ~0, the
      // renderer is the limiter.
      //
      // METRIC CHANGE with the decoupled wake: the pump now wakes on the host
      // interval too, so a wake WITHOUT a new guest frame is the normal case,
      // not a fault. `no_new_frame` counts those (they are the ones the
      // recompose skip makes cheap). A genuine guest stall is now reported
      // separately as `stalls_250ms`, preserving the old heartbeat_timeouts
      // meaning: the guest produced no frame at all for 250ms.
      // Guest-side probe accumulators (defined near RendererWaitProbeNote
      // below). Reported HERE because the pump thread holds no guest lock;
      // logging from the hooks themselves deadlocks (build 105).
      //
      // Iterates the WHOLE root table rather than the two hardcoded indices it
      // used to print. Every root reports exits + total_ms + avg_us on one
      // line, so a new root is visible the moment it is added instead of
      // silently accumulating into a number nobody reads.
      {
        static uint64_t last_report_ns = 0;
        if (g_wait_probe_exits != nullptr && g_wait_probe_names != nullptr &&
            g_wait_probe_count != 0) {
          uint64_t total_ns = 0;
          for (size_t i = 0; i < g_wait_probe_count; ++i) {
            total_ns += wait_ns_storage[i].load(std::memory_order_relaxed);
          }
          if (total_ns != last_report_ns) {
            last_report_ns = total_ns;
            std::string line;
            for (size_t i = 0; i < g_wait_probe_count; ++i) {
              const uint64_t ex =
                  g_wait_probe_exits[i].load(std::memory_order_relaxed);
              if (ex == 0) {
                continue;
              }
              const uint64_t ns =
                  wait_ns_storage[i].load(std::memory_order_relaxed);
              line += fmt::format(
                  "{}{} n={} ms={:.1f} avg_us={:.1f}", line.empty() ? "" : " | ",
                  g_wait_probe_names[i], static_cast<unsigned long long>(ex),
                  ns / 1.0e6,
                  static_cast<double>(ns) / static_cast<double>(ex) / 1000.0);
            }
            uint64_t scan_calls = 0, scan_ns = 0, scan_bytes = 0;
            GetGuestByteScanStats(scan_calls, scan_ns, scan_bytes);
            // Why the resource map grows — see GetResourceReleaseDispositionStats.
            uint64_t rel_neutral = 0, rel_deferred = 0, rel_erased = 0,
                     rel_norecord = 0, rel_drain_batched = 0,
                     rel_drain_walks = 0;
            GetResourceReleaseDispositionStats(rel_neutral, rel_deferred,
                                               rel_erased, rel_norecord,
                                               rel_drain_batched,
                                               rel_drain_walks);
            REXLOG_INFO(
                "Refii releasezero erased={} neutral={} deferred={} "
                "norecord={} drain_batch={} drain_walks={}",
                static_cast<unsigned long long>(rel_erased),
                static_cast<unsigned long long>(rel_neutral),
                static_cast<unsigned long long>(rel_deferred),
                static_cast<unsigned long long>(rel_norecord),
                static_cast<unsigned long long>(rel_drain_batched),
                static_cast<unsigned long long>(rel_drain_walks));
            // Is the observer mutex the frame, or merely busy? The long-hold
            // log shows fetch-capture / deferred-capture-drain holding it
            // 2-44ms at a time; this says whether anyone is queued behind them.
            uint64_t mtx_uncontended = 0, mtx_contended = 0, mtx_wait_us = 0;
            GetObserverMutexContentionStats(mtx_uncontended, mtx_contended,
                                            mtx_wait_us);
            const uint64_t mtx_total = mtx_uncontended + mtx_contended;
            REXLOG_INFO(
                "Refii obsmutex acquisitions={} contended={} ({}%) "
                "wait_ms={} avg_wait_us={}",
                static_cast<unsigned long long>(mtx_total),
                static_cast<unsigned long long>(mtx_contended),
                static_cast<unsigned long long>(
                    mtx_total != 0 ? (mtx_contended * 100u) / mtx_total : 0u),
                static_cast<unsigned long long>(mtx_wait_us / 1000u),
                static_cast<unsigned long long>(
                    mtx_contended != 0 ? mtx_wait_us / mtx_contended : 0u));
            // The single most expensive observer ENTER feed: is it blocking on
            // the render thread's g_fable_direct.mutex, or doing real work?
            uint64_t ec_calls = 0, ec_wait_us = 0, ec_body_us = 0;
            GetExactCaptureStats(ec_calls, ec_wait_us, ec_body_us);
            REXLOG_INFO(
                "Refii exactcapture calls={} wait_ms={} body_ms={} "
                "avg_wait_us={} avg_body_us={}",
                static_cast<unsigned long long>(ec_calls),
                static_cast<unsigned long long>(ec_wait_us / 1000u),
                static_cast<unsigned long long>(ec_body_us / 1000u),
                static_cast<unsigned long long>(
                    ec_calls != 0 ? ec_wait_us / ec_calls : 0u),
                static_cast<unsigned long long>(
                    ec_calls != 0 ? ec_body_us / ec_calls : 0u));
            // Sync-stage snapshot_ms attribution (run 480: spikes 39-128ms,
            // settled ~1.5ms/snapshot, wait-vs-body-vs-copy blind until now).
            uint64_t sf_calls = 0, sf_busy = 0, sf_wait_us = 0, sf_body_us = 0,
                     sf_upload_bytes = 0, sf_mints = 0;
            GetSnapshotFetchStats(sf_calls, sf_busy, sf_wait_us, sf_body_us,
                                  sf_upload_bytes, sf_mints);
            REXLOG_INFO(
                "Refii snapfetch calls={} busy={} wait_ms={} body_ms={} "
                "upload_mb={:.1f} mints={} avg_wait_us={} avg_body_us={}",
                static_cast<unsigned long long>(sf_calls),
                static_cast<unsigned long long>(sf_busy),
                static_cast<unsigned long long>(sf_wait_us / 1000u),
                static_cast<unsigned long long>(sf_body_us / 1000u),
                static_cast<double>(sf_upload_bytes) / (1024.0 * 1024.0),
                static_cast<unsigned long long>(sf_mints),
                static_cast<unsigned long long>(
                    sf_calls != 0 ? sf_wait_us / sf_calls : 0u),
                static_cast<unsigned long long>(
                    sf_calls != 0 ? sf_body_us / sf_calls : 0u));
            REXLOG_INFO("Refii feedcensus {}", FormatFeedCensus());
            REXLOG_INFO("Refii guestprobe {} | TruePublishes={} | ByteScan "
                        "calls={} ms={:.1f} MB={:.1f}",
                        line,
                        static_cast<unsigned long long>(
                            g_producer_true_publishes.load(
                                std::memory_order_relaxed)),
                        static_cast<unsigned long long>(scan_calls),
                        scan_ns / 1.0e6, scan_bytes / 1048576.0);

            // Top observe-enter feeds by cost — the 37.6s phase, named. Logged
            // from the pump because it holds no guest lock (build 105).
            std::vector<std::pair<uint64_t, std::pair<const char *, uint64_t>>>
                feeds;
            for (auto &slot : g_observe_enter_census) {
              const char *sym = slot.symbol.load(std::memory_order_acquire);
              if (sym == nullptr) {
                continue;
              }
              const uint64_t us = slot.us.load(std::memory_order_relaxed);
              if (us == 0) {
                continue;
              }
              feeds.push_back(
                  {us, {sym, slot.calls.load(std::memory_order_relaxed)}});
            }
            std::sort(feeds.begin(), feeds.end(),
                      [](const auto &a, const auto &b) { return a.first > b.first; });
            std::string feed_line;
            for (size_t i = 0; i < feeds.size() && i < 12; ++i) {
              feed_line += fmt::format(
                  "{}{} n={} ms={:.0f} avg_us={:.1f}", feed_line.empty() ? "" : " | ",
                  feeds[i].second.first,
                  static_cast<unsigned long long>(feeds[i].second.second),
                  feeds[i].first / 1000.0,
                  static_cast<double>(feeds[i].first) /
                      static_cast<double>(feeds[i].second.second));
            }
            if (!feed_line.empty()) {
              REXLOG_INFO("Refii observeenter TOP {} (dropped={})", feed_line,
                          static_cast<unsigned long long>(
                              g_observe_enter_census_dropped.load(
                                  std::memory_order_relaxed)));
            }
          }
        }
      }
      const auto pump_wait_t0 = std::chrono::steady_clock::now();
      bool guest_signalled = false;
      {
        std::unique_lock lock(g_present_pump_mutex);
        guest_signalled = g_present_pump_cv.wait_for(
            lock, wake_interval, [] {
              return g_plume_present_pump_stop_requested.load(
                         std::memory_order_acquire) ||
                     g_plume_bink_video_upload_ready.load(
                         std::memory_order_acquire) ||
                     g_guest_present_signal.load(std::memory_order_acquire) !=
                         g_guest_present_serviced.load(
                             std::memory_order_acquire);
            });
        g_guest_present_serviced.store(
            g_guest_present_signal.load(std::memory_order_acquire),
            std::memory_order_release);
      }
      {
        const auto wait_end = std::chrono::steady_clock::now();
        const double wait_ms =
            std::chrono::duration<double, std::milli>(wait_end - pump_wait_t0)
                .count();
        static double accumulated_wait_ms = 0.0;
        static double peak_wait_ms = 0.0;
        static uint32_t wait_samples = 0;
        static uint32_t wait_timeouts = 0;
        static uint32_t stall_count = 0;
        static uint32_t logged_pump_waits = 0;
        static auto last_guest_frame_at = std::chrono::steady_clock::now();
        accumulated_wait_ms += wait_ms;
        peak_wait_ms = std::max(peak_wait_ms, wait_ms);
        ++wait_samples;
        if (guest_signalled) {
          last_guest_frame_at = wait_end;
        } else {
          ++wait_timeouts;
          if (wait_end - last_guest_frame_at >= kGuestStallHeartbeat) {
            ++stall_count;
            // Charge the stall once, not on every wake inside it.
            last_guest_frame_at = wait_end;
          }
        }
        if (wait_samples >= 120u && logged_pump_waits < 256u) {
          REXLOG_INFO(
              "Refii plume present pump wait #{} samples={} avg_ms={:.2f} "
              "peak_ms={:.2f} no_new_frame={} stalls_250ms={} "
              "guest_presents_pending={} (submitted={} completed={}) "
              "last_present_ms={:.2f} "
              "last_prebody_ms={:.2f} — wait is time BLOCKED on the guest; "
              "present/prebody split the pump body; pending is how many guest "
              "frames we let run ahead (guest self-blocks at 15)",
              ++logged_pump_waits, wait_samples,
              accumulated_wait_ms / static_cast<double>(wait_samples),
              peak_wait_ms, wait_timeouts, stall_count,
              g_last_fable_present_pending.load(std::memory_order_relaxed),
              g_last_fable_present_submitted.load(std::memory_order_relaxed),
              g_last_fable_present_completed.load(std::memory_order_relaxed),
              g_plume_pump_present_ms.load(std::memory_order_relaxed),
              g_plume_pump_prebody_ms.load(std::memory_order_relaxed));
          stall_count = 0;
          accumulated_wait_ms = 0.0;
          peak_wait_ms = 0.0;
          wait_samples = 0;
          wait_timeouts = 0;
        }
      }
      next_present = RendererTraceClock::now();
    } else {
      next_present += interval;

      std::unique_lock lock(g_present_pump_mutex);
      g_present_pump_cv.wait_until(lock, next_present, [] {
        return g_plume_present_pump_stop_requested.load(
                   std::memory_order_acquire) ||
               g_plume_bink_video_upload_ready.load(std::memory_order_acquire);
      });
    }

    const bool bink_upload_ready =
        g_plume_bink_video_upload_ready.exchange(false,
                                                 std::memory_order_acq_rel);
    if (g_plume_present_pump_stop_requested.load(std::memory_order_acquire)) {
      break;
    }
    if (!RendererPlumePresentPumpEnabled()) {
      next_present = RendererTraceClock::now();
      continue;
    }

    const int64_t now_ns = RendererTraceNowNs();
    g_plume_present_pump_request_count.fetch_add(1, std::memory_order_relaxed);
    g_last_plume_present_pump_gap_us.store(
        PlotDeltaUs("Refii Plume Present Pump Gap Us",
                    g_last_plume_present_pump_request_ns, now_ns),
        std::memory_order_relaxed);

    const auto pump_body_t0 = std::chrono::steady_clock::now();
    // Guest-servicing work runs on EVERY wake, including skipped ones. These
    // are how the guest's startup video and Bink audio cursors advance — gating
    // them behind a new guest frame would stall audio whenever the guest is
    // slow, which is exactly when they matter most.
    RequestFableStartupVideoPumpFromPlume();
    PumpStartupBinkAudioCursorsFromPlume();
    RepairFableSceneStreamPublishHandoff(
        "plume_present_pump",
        reinterpret_cast<uint8_t *>(g_last_fable_memory_base.load(
            std::memory_order_relaxed)));

    // RECOMPOSE SKIP. With the decoupled wake the pump runs faster than the
    // guest's 30Hz tick, so most wakes have no new guest frame. Composing then
    // rebuilds — draw for draw — the frame already on screen: run 327 measured
    // ~95ms per compose with attempted/recorded/submitted identical across
    // consecutive entries while 109 of 120 wakes carried no new frame. That is
    // pure waste, and worse than waste: the compose holds the direct-state and
    // registry locks the guest needs to submit its NEXT frame, so the redundant
    // work actively delays the thing it is waiting for.
    //
    // A keepalive present still goes out on the old stall cadence so the
    // swapchain never goes quiet (some presenters need it, and it preserves the
    // previous behaviour's liveness guarantee).
    // REVERTED 2026-07-27 — DO NOT REINSTATE THIS AS A BLANKET SKIP.
    //
    // A skip here (compose+present both suppressed when no new guest frame had
    // arrived) took the game from 4fps to 1fps and drove guest frame production
    // to EXACTLY ZERO: run 328 logged `no_new_frame=120` of 120 samples, with
    // the surviving ~1fps coming entirely from the keepalive.
    //
    // The pump's present path is NOT just display — it services the guest. Skip
    // it and the guest's swap never acknowledges (the D3DDevice_Swap ack loop
    // waits on dev+0x40B8, advanced only via D3D_SwapCallback), so the guest
    // never completes a frame, never signals, and the pump skips again:
    // a starvation loop of my own making.
    //
    // The waste it targeted is real and still unfixed — run 327 showed ~91% of
    // composes rebuilding an unchanged frame at ~95ms each. But the fix must
    // SPLIT the present path: keep the guest-facing swap acknowledgement on
    // every wake, and skip only the record/compose of unchanged content. A
    // blanket skip of both is what starves the guest.

    // Compose-trace: route the capture_render trigger through the backend's
    // OWN frame-capture machinery (RequestRenderDocFrameCaptureWithReason ->
    // BeginRenderDocFrameCapture inside the plume frame), which demonstrably
    // produces full-stream captures (record + submit + present, with file
    // tags and Frame DB sidecars). A pump-level Start/EndFrameCapture wrap
    // was tried first and captured 1 action / 0 draws — do not resurrect it.
    if (ConsumeRenderDocComposeTraceRequest()) {
      RequestRenderDocFrameCaptureWithReason("compose_trace");
    }
    // Close the frame-time accounting. Measured frame period was ~240ms while
    // pump wait (~33ms) plus plume's own frame (~41ms) plus the record window
    // only added up to about half of it. Rather than guess at the remainder,
    // split the pump iteration into the present call and everything else the
    // loop does around it (startup-video pump, Bink audio cursors, scene
    // publish repair, presentation policy).
    const auto pump_present_t0 = std::chrono::steady_clock::now();
    const bool presented = PresentPlumeFrameFromRendererHook(
        bink_upload_ready ? "plume_bink_upload_present" : "plume_present_pump",
        bink_upload_ready ? "bink-upload" : "fps-pump", true);
    g_plume_pump_present_ms.store(
        std::chrono::duration<double, std::milli>(
            std::chrono::steady_clock::now() - pump_present_t0)
            .count(),
        std::memory_order_relaxed);
    g_plume_pump_prebody_ms.store(
        std::chrono::duration<double, std::milli>(pump_present_t0 -
                                                  pump_body_t0)
            .count(),
        std::memory_order_relaxed);
    if (presented) {
      LogPlumePumpFablePresentationPolicy();
      // STEP 6 (2026-08-02): the pipeline-depth gate that used to live here
      // (kMaxPipelinedGuestFrames=2 on g_last_fable_present_pending) was DEAD
      // — swap bookkeeping completes instantly at hook time, so pending
      // measured 0 always and the branch never fired. Its job — bounding how
      // far the guest runs ahead of the renderer's reads of guest memory —
      // is now done for real by the capture-tied ack gate
      // (WaitForCaptureTiedAckAtPresentBoundary, guest thread, Present hook):
      // capture-then-ack at depth 1. Pump-side bookkeeping stays
      // unconditional on every wake (07-27 post-mortem: never withhold guest
      // servicing from the present path).
      TryAdvanceFablePresentationFromPump(target_fps);
    }
    if (presented && !g_logged_plume_present_pump_driver) {
      REXLOG_INFO(
          "Refii plume output is presenting from the Plume FPS pump at {} FPS",
          target_fps);
      g_logged_plume_present_pump_driver = true;
    }
    if (presented && bink_upload_ready &&
        !g_logged_plume_bink_upload_present_driver) {
      REXLOG_INFO(
          "Refii plume output is presenting Bink uploads directly from the "
          "Plume pump");
      g_logged_plume_bink_upload_present_driver = true;
    }
    const auto now = RendererTraceClock::now();
    if (bink_upload_ready && next_present > now + interval) {
      next_present = now + interval;
    } else if (next_present + interval < now) {
      next_present = now;
    }
  }

  REXLOG_INFO("Refii plume present pump stopped");
}

void StopPlumePresentPumpThread() {
  g_plume_present_pump_shutdown_requested.store(true,
                                                std::memory_order_release);
  g_plume_present_pump_stop_requested.store(true, std::memory_order_release);
  g_present_pump_cv.notify_all();

  if (!g_plume_present_pump_running.exchange(false,
                                             std::memory_order_acq_rel) &&
      !g_present_pump_thread.joinable()) {
    return;
  }

  if (g_present_pump_thread.joinable()) {
    if (g_present_pump_thread.get_id() == std::this_thread::get_id()) {
      g_present_pump_thread.detach();
    } else {
      g_present_pump_thread.join();
    }
  }
}

void EnsurePlumePresentPumpStarted() {
  if (g_plume_present_pump_shutdown_requested.load(std::memory_order_acquire)) {
    return;
  }
  if (!RendererPlumePresentPumpEnabled()) {
    return;
  }

  // Read the latch before the CAS: this runs from MaybeLogRendererBackendState
  // on EVERY full-path hook enter, and once the pump is up the unconditional
  // compare_exchange_strong was a per-call locked RMW on a shared cacheline
  // (a FAILED strong CAS still owns the line). Same false->true latch pattern
  // as NotifyPlumeBackendHook; a stale read costs one harmless extra CAS.
  if (g_plume_present_pump_running.load(std::memory_order_acquire)) {
    return;
  }

  bool expected = false;
  if (!g_plume_present_pump_running.compare_exchange_strong(
          expected, true, std::memory_order_acq_rel)) {
    return;
  }

  std::call_once(g_present_pump_atexit_once,
                 [] { std::atexit([] { StopPlumePresentPumpThread(); }); });
  if (g_plume_present_pump_shutdown_requested.load(std::memory_order_acquire)) {
    g_plume_present_pump_running.store(false, std::memory_order_release);
    return;
  }
  g_plume_present_pump_stop_requested.store(false, std::memory_order_release);
  g_present_pump_thread = std::thread(PlumePresentPumpMain);
}

const char *RendererBackendName(RendererBackend backend) {
  switch (backend) {
  case RendererBackend::kNative:
    return "native";
  case RendererBackend::kPlume:
    return "plume";
  case RendererBackend::kDisabled:
    return "disabled";
  }

  return "unknown";
}

const char *RendererGraphicsApiName(RendererGraphicsApi api) {
  switch (api) {
  case RendererGraphicsApi::kVulkan:
    return "vulkan";
  case RendererGraphicsApi::kD3D12:
    return "d3d12";
  case RendererGraphicsApi::kMetal:
    return "metal";
  }

  return "unknown";
}

bool ShouldTraceHook(RendererHookKind kind) {
  if (RendererHookTracingEnabled()) {
    return true;
  }

  switch (kind) {
  case RendererHookKind::kResource:
    return RendererResourceTracingEnabled();
  case RendererHookKind::kDraw:
    return RendererDrawTracingEnabled();
  case RendererHookKind::kGeneral:
    return false;
  }

  return false;
}

bool IsNativePresentationWorkHook(std::string_view symbol) {
  // D3DDevice_Swap owns Fable's PixCaptureVideoFrame call, so the wrapper must
  // run even when Plume owns presentation. Skip the lower swap/sync helpers
  // that submit guest presentation work instead.
  return symbol == "rex_lhRenderer_EndScene" ||
         symbol == "sub_82BA32F8" || // ComplexSwap
         symbol == "sub_82BA2DB8" || // SwapCallback
         symbol == "sub_82BA2F68" || // SynchronizeToPresentationInterval
         symbol == "sub_82BA2748";   // D3DDevice_BlockUntilIdle
}
bool IsFableGuestCommandSubmissionHook(std::string_view symbol) {
  // Keep replacement static at the boundary where work would reach original D3D.
  // Pending-state queue entry points are mirrored by EmulateFable* helpers so
  // Plume owns Bink/title state submission without falling back to guest D3D.
  return symbol == "sub_82BA25C0" || // AddCommandsToPrimaryBuffer
         symbol == "sub_822866E0" || // AddCallsToPrimaryBuffer
         symbol == "sub_822868C8" || // QueueIndirectBuffer
         symbol == "sub_821E8D38" || // KickOffSegment
         symbol == "sub_821E8EC0" || // KickOff
         symbol == "sub_8221F2E0" || // flush pending per-context state
         symbol == "sub_8221E4A0" || // deferred pending-state dispatch
         symbol == "sub_8221F348" || // process one pending-state queue
         symbol == "sub_82196628" || // BeginExport
         symbol == "sub_82196750" || // EndExport
         symbol == "sub_8217F3E8" || // D3DDevice_QueryBufferSpace
         symbol == "sub_821F6270" || // D3DDevice_IsFencePending
         symbol == "sub_82242668" || // D3D::CDevice::BlockOnFence
         symbol == "sub_822C6BA8";   // InvalidateCaches
}

bool IsFableGuestStateEmulationHook(std::string_view symbol) {
  // Skip state hook bodies only after their guest-visible device writes have
  // direct emulation. Shader hooks still patch inline constant state natively.
  return FableRenderStateSlotForSymbol(symbol) != UINT32_MAX ||
         FableSamplerStateSlotForSymbol(symbol) != UINT32_MAX ||
         symbol == "sub_821E2FE0" || // D3DDevice_ClearF
         symbol == "sub_821EF830" || // D3D::ClearRect
         symbol == "sub_822A09A0" || // D3D::SetColorClear
         symbol == "sub_822224A8" || // SetVertexDeclaration
         symbol == "sub_821F9F58" || // D3DDevice_SetViewport
         symbol == "sub_821F9C08" || // D3DDevice_SetScissorRect
         symbol == "sub_821F9D00" || // D3D::SetViewport
         symbol == "sub_821F9918" || // D3D::SetSurfaceClip
         symbol == "sub_8219CD68" || // D3DDevice_SetShaderGPRAllocation
         symbol == "sub_822655F0" || // D3DDevice_SetPredication
         symbol == "sub_822AF568" || // D3DDevice_SetScreenExtentQueryMode
         symbol == "sub_82BAEC38" || // D3DDevice_SetScalerParameters
         symbol == "sub_82BAEA88" || // D3D::SetupScaler
         symbol == "sub_822869A0" || // D3DDevice_SetRenderTarget
         symbol == "sub_82264590";   // D3DDevice_SetDepthStencilSurface
}

bool IsFableGuestPendingStateEmulationHook(std::string_view symbol) {
  // These pending-state packet builders are now mirrored directly. Keep shader
  // patch helpers native until their inline guest-memory side effects are fully
  // represented in Plume commands. D3D::SetPending_Shaders is deliberately not
  // skipped: IDA shows it calls Fable's DirectShaderPatch and
  // IncrementalShaderPatchAndLoad paths before writing PM4, and the direct
  // renderer needs those guest-visible patched shader/declaration side effects.
  return symbol == "sub_8221E550" || // pending state dispatch trampoline
         symbol == "sub_8221C7D8" || // D3D::SetPending_RenderStates
         symbol == "sub_8221D1B0" || // D3D::SetPending_FetchConstants
         symbol == "sub_822154B0" || // D3D::SetPending_Predicated
         symbol == "sub_8228EF80" || // D3D::SetPending_Split
         symbol == "sub_822B4008";   // D3D::SetPending_ClipPlanes
}

bool IsFableGuestClearSubmissionHook(std::string_view symbol) {
  return symbol == "sub_821E2FE0" || // D3DDevice_ClearF
         symbol == "sub_821EF830";   // D3D::ClearRect
}

bool IsFableGuestBindingStateSubmissionHook(std::string_view symbol) {
  return symbol == "sub_821B6C60" || // D3DDevice_SetStreamSource
         symbol == "sub_8219CC30" || // D3DDevice_SetIndices
         symbol == "sub_821B6EE0" || // D3DDevice_SetTexture
         symbol == "sub_82232510" || // D3DDevice_SetVertexShader
         symbol == "sub_82208BB0";   // D3DDevice_SetPixelShader
}

bool IsFableGuestTerminalDrawSubmissionHook(std::string_view symbol) {
  return IsFableGuestPrivateTextureRectangleHook(symbol) ||
         symbol == "sub_82BA95E0" ||
         symbol == "sub_82219740" ||
         symbol == "sub_822194B8" ||
         symbol == "sub_82205F68" ||
         symbol == "sub_82265CD8" ||
         symbol == "sub_82217DB8" ||
         IsFableGuestPlainRootDrawHook(symbol);
}

bool IsNativeRenderWorkHook(std::string_view symbol) {
  if (IsFableTitleScenePopulationHook(symbol)) {
    return false;
  }
  if (RendererPlumeOwnsCommandSubmissionEnabled() &&
      FableUnleashedExactHookKindForSymbol(symbol) ==
          FableUnleashedExactHookKind::kStub) {
    return true;
  }
  if (RendererReplaceResourcesEnabled() &&
      (IsFableResourceLifetimeHook(symbol) ||
       IsFableResourceHeaderBuildHook(symbol) ||
       IsFableResourceUpdateReplacementHook(symbol) ||
       IsFableBufferCreateHook(symbol) ||
       IsFableSurfaceCopyHook(symbol) ||
       IsFableSurfaceMemoryCopyHook(symbol) ||
       IsFableTextureHeaderUtilityHook(symbol) ||
       IsFableGetRenderTargetHook(symbol) ||
       IsFableSurfaceSizeHook(symbol))) {
    return true;
  }
  if (RendererReplacePresentEnabled() && IsNativePresentationWorkHook(symbol)) {
    return true;
  }
  if (RendererPlumeOwnsCommandSubmissionEnabled() &&
      IsFableGuestShaderConstantReplacementHook(symbol)) {
    return true;
  }
  if (RendererPlumeOwnsCommandSubmissionEnabled() &&
      IsFableGuestCommandSubmissionHook(symbol)) {
    return true;
  }
  if (RendererPlumeOwnsCommandSubmissionEnabled() &&
      IsFableGuestClearSubmissionHook(symbol)) {
    return true;
  }
  if (RendererPlumeOwnsCommandSubmissionEnabled() &&
      IsFableGuestBindingStateSubmissionHook(symbol)) {
    return true;
  }
  if (RendererPlumeOwnsCommandSubmissionEnabled() &&
      IsFableGuestStateEmulationHook(symbol)) {
    return true;
  }
  if (RendererPlumeOwnsCommandSubmissionEnabled() &&
      IsFableGuestPendingStateEmulationHook(symbol)) {
    return true;
  }
  if (RendererPlumeOwnsCommandSubmissionEnabled() &&
      IsFableGuestTerminalDrawSubmissionHook(symbol)) {
    return true;
  }

  return false;
}

const char *PlumeGuestPathDescription(RendererHookKind kind) {
  switch (kind) {
  case RendererHookKind::kResource:
    return "guest resource path active for Plume-owned resource tracking";
  case RendererHookKind::kDraw:
    return "guest draw/state path active for Plume staging";
  case RendererHookKind::kGeneral:
    return "guest utility path active for Plume tracking";
  }

  return "guest path active for Plume tracking";
}

std::string FableHookLogName(const char *symbol) {
  const char *lumen_name = FableHookName(symbol);
  if (lumen_name == nullptr || lumen_name[0] == '\0' ||
      std::string_view(lumen_name) == symbol) {
    return std::string(symbol);
  }
  return std::string(lumen_name) + " (" + symbol + ")";
}

const char *FableUnsupportedAdapterShapeCategory(const char *symbol) {
  if (IsFableVertexDeclarationCreateHook(symbol)) {
    return "vertex-declaration-create";
  }
  if (IsFableShaderCreateHook(symbol)) {
    return "shader-create";
  }
  if (IsFableTextureCreateHook(symbol)) {
    return "texture-create";
  }
  if (IsFableSurfaceCreateHook(symbol)) {
    return "surface-create";
  }
  if (IsFableRenderDepthSurfaceCtorHook(symbol)) {
    return "render-depth-surface-ctor";
  }
  if (IsFableBinkVideoPlaneSetupHook(symbol)) {
    return "bink-video-plane-setup";
  }
  if (IsFableBinkVideoPlaneSurfaceSetupHook(symbol)) {
    return "bink-video-plane-surface-setup";
  }
  if (IsFableSurfaceWrapperCreateHook(symbol)) {
    return "surface-wrapper-create";
  }
  if (IsFableBufferCreateHook(symbol)) {
    return "buffer-create";
  }
  if (IsFableSurfaceViewCreateHook(symbol)) {
    return "surface-view-create";
  }
  if (IsFableResourceQueryHook(symbol)) {
    return "resource-query";
  }
  if (IsFableTextureAddressOffsetHook(symbol)) {
    return "texture-address-offset";
  }
  if (IsFableTextureHeaderBuildHook(symbol)) {
    return "texture-header-build";
  }
  if (IsFableTextureHeaderUtilityHook(symbol)) {
    return "texture-header-utility";
  }
  if (IsFableCpuResourceHelperHook(symbol)) {
    return "cpu-resource-helper";
  }
  if (IsFableSurfaceMemoryCopyHook(symbol)) {
    return "surface-memory-copy";
  }
  if (IsFableTextureFillHook(symbol)) {
    return "texture-fill";
  }
  if (IsFableTextureFileCreateHook(symbol)) {
    return "texture-file-create";
  }
  if (IsFableSurfaceCopyHook(symbol)) {
    return "surface-copy";
  }
  if (IsFableTextureSurfaceLockHook(symbol)) {
    return "texture-surface-lock";
  }
  if (std::string_view(symbol) == "rex_lhGfx_LockBuffer") {
    return "resource-lock";
  }
  if (IsFableResourceLifetimeHook(symbol)) {
    return "resource-lifetime";
  }
  if (IsFableResourceUpdateReplacementHook(symbol)) {
    return "resource-update";
  }
  if (IsFableShaderPatchHelperHook(symbol)) {
    return "shader-patch-helper";
  }
  if (IsFableShaderUtilityHook(symbol)) {
    return "shader-utility";
  }

  return "resource-other";
}

bool IsMandatoryFableResourceReplacementHook(std::string_view symbol) {
  return IsFableSurfaceViewCreateHook(symbol) ||
         IsFableBufferCreateHook(symbol) ||
         IsFableVertexDeclarationCreateHook(symbol) ||
         IsFableShaderCreateHook(symbol) ||
         IsFableVertexShaderRegisterHook(symbol) ||
         IsFableShaderUtilityHook(symbol) ||
         IsFableTextureCreateHook(symbol) ||
         IsFableSurfaceCreateHook(symbol) ||
         IsFableSurfaceWrapperCreateHook(symbol) ||
         IsFableRenderDepthSurfaceCtorHook(symbol) ||
         IsFableRenderTargetSetupHook(symbol) ||
         IsFableBinkVideoPlaneSetupHook(symbol) ||
         IsFableBinkVideoPlaneSurfaceSetupHook(symbol) ||
         IsFableTextureSurfaceLockHook(symbol) ||
         IsFableSurfaceCopyHook(symbol) ||
         IsFableSurfaceMemoryCopyHook(symbol) ||
         IsFableTextureFillHook(symbol) ||
         IsFableTextureFileCreateHook(symbol) ||
         IsFableResourceQueryHook(symbol) ||
         IsFableTextureAddressOffsetHook(symbol) ||
         IsFableTextureHeaderBuildHook(symbol) ||
         IsFableTextureHeaderUtilityHook(symbol) ||
         IsFableCpuResourceHelperHook(symbol) ||
         IsFableResourceLockHook(symbol) ||
         IsFableResourceUnlockHook(symbol) ||
         IsFableResourceLifetimeHook(symbol) ||
         IsFableResourceHeaderBuildHook(symbol) ||
         IsFableResourceUpdateReplacementHook(symbol) ||
         IsFableGetRenderTargetHook(symbol) ||
         IsFableSurfaceSizeHook(symbol);
}

bool CanEmulateMandatoryFableResourceReplacementHook(
    std::string_view symbol, const RendererHookArgs &args, uint8_t *base) {
  if (IsFableTextureSurfaceLockHook(symbol)) {
    return CanEmulateFableTextureSurfaceLock(symbol, args, base);
  }
  if (symbol == "sub_8225F980" || symbol == "sub_82196980" ||
      symbol == "rex_lhGfx_LockBuffer") {
    return CanEmulateFableResourceLock(symbol, args, base);
  }
  if (symbol == "sub_821FA070" || IsFableResourceQueryHook(symbol)) {
    return CanEmulateFableResourceQuery(symbol, args, base);
  }
  if (symbol == "sub_82B98B28") {
    return CanEmulateFableTextureCreate(symbol, args, base);
  }
  if (IsFableResourceUpdateReplacementHook(symbol) ||
      IsFableResourceHeaderBuildHook(symbol) ||
      IsFableTextureHeaderUtilityHook(symbol) ||
      IsFableGetRenderTargetHook(symbol) ||
      IsFableSurfaceSizeHook(symbol) ||
      IsFableRenderTargetSetupHook(symbol) ||
      IsFableBinkVideoPlaneSurfaceSetupHook(symbol) ||
      IsFableCpuResourceHelperHook(symbol)) {
    return RendererReplaceResourcesEnabled() && base != nullptr;
  }
  if (IsFableResourceLifetimeHook(symbol)) {
    return RendererReplaceResourcesEnabled() && base != nullptr &&
           IsLikelyFableObjectAddress(args.r3);
  }
  if (IsFableBufferCreateHook(symbol)) {
    return CanEmulateFableBufferCreate(symbol, args, base);
  }
  if (IsFableVertexDeclarationCreateHook(symbol)) {
    return CanEmulateFableVertexDeclarationCreate(symbol, args, base);
  }
  if (IsFableShaderCreateHook(symbol)) {
    return CanEmulateFableShaderCreate(symbol, args, base);
  }
  if (IsFableVertexShaderRegisterHook(symbol)) {
    return CanEmulateFableVertexShaderRegister(symbol, args, base);
  }
  if (IsFableShaderUtilityHook(symbol)) {
    return CanEmulateFableShaderUtility(symbol, args, base);
  }
  if (IsFableSurfaceCreateHook(symbol)) {
    return CanEmulateFableSurfaceCreate(symbol, args, base);
  }
  if (IsFableSurfaceWrapperCreateHook(symbol)) {
    return CanEmulateFableSurfaceWrapperCreate(symbol, args, base);
  }
  if (IsFableRenderDepthSurfaceCtorHook(symbol)) {
    return CanEmulateFableRenderDepthSurfaceCtor(symbol, args, base);
  }
  if (IsFableBinkVideoPlaneSetupHook(symbol)) {
    return CanEmulateFableBinkVideoPlaneSetup(symbol, args, base);
  }
  if (IsFableSurfaceViewCreateHook(symbol)) {
    return CanEmulateFableSurfaceViewCreate(symbol, args, base);
  }
  if (IsFableSurfaceCopyHook(symbol)) {
    return CanEmulateFableSurfaceCopy(symbol, args, base);
  }
  if (IsFableSurfaceMemoryCopyHook(symbol)) {
    return CanEmulateFableSurfaceMemoryCopy(symbol, args, base);
  }
  if (IsFableTextureFillHook(symbol)) {
    return CanEmulateFableTextureFill(symbol, args, base);
  }
  if (IsFableTextureFileCreateHook(symbol)) {
    return CanEmulateFableTextureFileCreate(symbol, args, base);
  }
  if (IsFableTextureAddressOffsetHook(symbol)) {
    return CanEmulateFableTextureAddressOffset(symbol, args, base);
  }
  if (IsFableTextureHeaderBuildHook(symbol)) {
    return CanEmulateFableTextureHeaderBuild(symbol, base);
  }
  return false;
}

bool ShouldLogFableUnsupportedAdapterShapeCount(uint64_t count) {
  return count <= 8 || (count & (count - 1u)) == 0;
}

void LogFableUnsupportedAdapterShape(const char *symbol,
                                     const RendererHookArgs &args,
                                     uint8_t *base) {
  const char *category = FableUnsupportedAdapterShapeCategory(symbol);
  uint64_t count = 0;
  {
    std::lock_guard lock(g_fable_unsupported_adapter_shape_mutex);
    const std::string key = std::string(category) + ":" + symbol;
    count = ++g_fable_unsupported_adapter_shape_counts[key];
  }
  if (!ShouldLogFableUnsupportedAdapterShapeCount(count)) {
    return;
  }

  REXLOG_WARN(
      "Refii renderer resource replacement rejected unsupported Fable adapter "
      "shape: count={} "
      "category={} symbol={} r3=0x{:08X} r4=0x{:08X} r5=0x{:08X} "
      "r6=0x{:08X} r7=0x{:08X} r8=0x{:08X} r9=0x{:08X} r10=0x{:08X} "
      "base={} lr=0x{:08X}",
      count, category, FableHookLogName(symbol), args.r3, args.r4, args.r5,
      args.r6, args.r7, args.r8, args.r9, args.r10, base != nullptr,
      args.lr);
}

void TraceHookDecision(const char *symbol, RendererHookKind kind) {
  if (!ShouldTraceHook(kind)) {
    return;
  }

  const bool run_guest = ShouldRunGuestRendererHook(symbol, kind);
  const std::string log_key = std::string(symbol) + ":" +
                              RendererBackendName(GetRendererBackend()) + ":" +
                              (run_guest ? "guest-run" : "plume-owned");
  if (!g_logged_hook_decisions.insert(log_key).second) {
    return;
  }

  const RendererBackend backend = GetRendererBackend();
  const std::string hook_name = FableHookLogName(symbol);
  const bool exact_replacement = IsFableUnleashedExactReplacementHook(symbol);
  const char *exact_role =
      exact_replacement ? FableUnleashedExactReplacementRole(symbol) : nullptr;
  const char *exact_source =
      exact_replacement ? FableUnleashedExactReplacementSource(symbol)
                        : nullptr;
  if (backend == RendererBackend::kPlume) {
    if (run_guest) {
      if (exact_replacement) {
        REXLOG_INFO(
            "Refii renderer Fable replacement hook {} ({}, from {}) observed "
            "for plume/{}; {}",
            hook_name, exact_role ? exact_role : "unknown",
            exact_source ? exact_source : "unknown",
            RendererGraphicsApiName(GetRendererGraphicsApi()),
            PlumeGuestPathDescription(kind));
      } else {
        REXLOG_INFO("Refii renderer hook {} observed for plume/{}; {}",
                    hook_name,
                    RendererGraphicsApiName(GetRendererGraphicsApi()),
                    PlumeGuestPathDescription(kind));
      }
    } else {
      if (exact_replacement) {
        REXLOG_INFO(
            "Refii renderer Fable replacement hook {} ({}, from {}) observed "
            "for plume/{}; Plume owns this hook through the replacement path",
            hook_name, exact_role ? exact_role : "unknown",
            exact_source ? exact_source : "unknown",
            RendererGraphicsApiName(GetRendererGraphicsApi()));
      } else {
        REXLOG_INFO(
            "Refii renderer hook {} observed for plume/{}; Plume owns this "
            "hook through the replacement path",
            hook_name, RendererGraphicsApiName(GetRendererGraphicsApi()));
      }
    }
    return;
  }

  REXLOG_INFO("Refii renderer hook {} observed for {}; native backend active",
              hook_name, RendererBackendName(backend));
}

void MaybeLogRendererBackendState() {
  if (!RendererHooksEnabled() ||
      GetRendererBackend() != RendererBackend::kPlume) {
    return;
  }

  if (!PlumeAvailable()) {
    if (!g_logged_plume_unavailable) {
      REXLOG_WARN(
          "Refii renderer backend is set to plume/{}, but Plume is not linked "
          "into this build. Select renderer_backend=xenia to use the original "
          "Fable 2 renderer.",
          RendererGraphicsApiName(GetRendererGraphicsApi()));
      g_logged_plume_unavailable = true;
    }
    return;
  }

  if (!g_logged_plume_observer) {
    REXLOG_INFO(
        "Refii renderer backend is set to plume/{}. Plume owns presentation "
        "and routes captured Fable command/state/draw work through the "
        "Plume-owned replacement path. "
        "Resource hooks feed Plume-owned guest resources; guest-visible setup, "
        "allocation, upload, and descriptor side effects are emulated through "
        "the Fable adapter layer rather than guest D3D rendering or "
        "presentation.",
        RendererGraphicsApiName(GetRendererGraphicsApi()));
    g_logged_plume_observer = true;
  }

  EnsurePlumePresentPumpStarted();
}

} // namespace

bool PlumeAvailable() { return REFII_RENDERER_HAS_PLUME != 0; }

void ShutdownRendererPresentPump() { StopPlumePresentPumpThread(); }

// Present-decoupling STEP 6 cross-TU interface (declared locally in
// plume_backend.cpp). The record path samples the boundary seq BEFORE its
// drain+record and publishes it back AFTER RecordFableDirectFrame returns —
// at that point every CPU-side guest-memory read for that frame (deferred
// texel captures + record) is done, which is what "captured" means here.
uint64_t FableGuestPresentBoundarySeqForCapture() {
  return g_guest_present_signal.load(std::memory_order_acquire);
}

void PublishFableCapturesCompletedThroughBoundary(uint64_t boundary_seq) {
  uint64_t previous =
      g_capture_ack_captured_boundary.load(std::memory_order_relaxed);
  while (previous < boundary_seq &&
         !g_capture_ack_captured_boundary.compare_exchange_weak(
             previous, boundary_seq, std::memory_order_release,
             std::memory_order_relaxed)) {
  }
  g_capture_ack_publishes.fetch_add(1, std::memory_order_release);
  // Empty critical section pairs with the gate's locked wait_for so a publish
  // between its predicate check and its sleep cannot lose the wakeup.
  { std::lock_guard<std::mutex> lock(g_capture_ack_mutex); }
  g_capture_ack_cv.notify_all();
}

FableCaptureAckGateCensus GetFableCaptureAckGateCensus() {
  FableCaptureAckGateCensus census;
  census.checks = g_capture_ack_gate_checks.load(std::memory_order_relaxed);
  census.disarms = g_capture_ack_gate_disarms.load(std::memory_order_relaxed);
  census.waits = g_capture_ack_gate_waits.load(std::memory_order_relaxed);
  census.wait_us = g_capture_ack_gate_wait_us.load(std::memory_order_relaxed);
  census.timeouts =
      g_capture_ack_gate_timeouts.load(std::memory_order_relaxed);
  census.max_outstanding =
      g_capture_ack_gate_max_outstanding.load(std::memory_order_relaxed);
  census.last_outstanding =
      g_capture_ack_gate_last_outstanding.load(std::memory_order_relaxed);
  census.captured_boundary =
      g_capture_ack_captured_boundary.load(std::memory_order_relaxed);
  census.present_boundary =
      g_guest_present_signal.load(std::memory_order_relaxed);
  census.publishes = g_capture_ack_publishes.load(std::memory_order_relaxed);
  return census;
}

void NotifyPlumeBinkVideoUploadReady() {
  g_plume_bink_video_upload_ready_ns.store(RendererTraceNowNs(),
                                           std::memory_order_release);
  if (!RendererPlumePresentPumpEnabled()) {
    return;
  }

  g_plume_bink_video_upload_ready.store(true, std::memory_order_release);
  const uint64_t request =
      g_plume_bink_video_upload_ready_count.fetch_add(
          1, std::memory_order_relaxed) +
      1u;
  if (request <= 8u || request % 120u == 0u) {
    REXLOG_INFO("Refii plume Bink upload queued direct present request #{}",
                request);
  }
  g_present_pump_cv.notify_one();
}

uint64_t PlumeBinkVideoUploadReadyCount() {
  return g_plume_bink_video_upload_ready_count.load(std::memory_order_acquire);
}

bool FableStartupVideoPumpActive() {
  return IsFableStartupVideoPumpActive();
}

bool FableStartupVideoPumpCompletedForHandoff() {
  return g_startup_video_pump_complete_count.load(std::memory_order_acquire) !=
             0 &&
         !IsFableStartupVideoPumpActive();
}


void CompleteFableStartupVideoPumpForHandoff(uint32_t completion_update,
                                             const char *reason) {
  if (completion_update == 0) {
    return;
  }

  {
    std::lock_guard lock(g_startup_video_observed_bink_mutex);
    g_startup_video_observed_bink_base = nullptr;
    g_startup_video_observed_bink_movie = 0;
  }
  g_startup_video_observed_bink_ns.store(0, std::memory_order_release);
  g_startup_video_observed_bink_blocked_update.store(
      completion_update, std::memory_order_release);
  g_plume_bink_video_upload_ready.store(false, std::memory_order_release);
  FableDirectClearDisplayPresent();
  ResetFableDirectLivePresentSources();
  if (g_startup_video_pump_launch_count.load(std::memory_order_acquire) == 0) {
    g_startup_video_pump_serviced_update.store(0, std::memory_order_release);
    g_startup_video_pump_in_flight.store(false, std::memory_order_release);
  }
  g_startup_video_pump_native_update_waiting_update.store(
      0, std::memory_order_release);

  uint32_t previous_completed = 0;
  if (!g_startup_video_pump_completed_update.compare_exchange_strong(
          previous_completed, completion_update, std::memory_order_acq_rel)) {
    return;
  }

  const uint32_t complete_count =
      g_startup_video_pump_complete_count.fetch_add(1,
                                                    std::memory_order_relaxed) +
      1u;
  if (complete_count <= 4u || complete_count % 60u == 0u) {
    REXLOG_INFO(
        "Refii plume present pump completed native startup video run count={} "
        "update=0x{:08X} cleared_bink_present=1 reason={}",
        complete_count, completion_update, reason ? reason : "unknown");
  }
}

void RegisterFableStartupVideoPumpContext(uint8_t *base,
                                          const PPCContext &ctx,
                                          uint32_t wrapper_guest) {
  if (base == nullptr || wrapper_guest == 0) {
    return;
  }

  RegisterFableStartupVideoPumpContext(
      base, ctx, wrapper_guest, LoadGuestU32(base, wrapper_guest + 12u));
}

void RegisterFableStartupVideoPumpContext(uint8_t *base,
                                          const PPCContext &ctx,
                                          uint32_t wrapper_guest,
                                          uint32_t update_guest) {
  if (base == nullptr || wrapper_guest == 0 ||
      !LooksLikeFableStartupVideoUpdate(base, update_guest)) {
    return;
  }

  PPCContext pump_ctx = ctx;
  uint32_t stack = 0;
  {
    std::lock_guard lock(g_startup_video_pump_mutex);
    if (g_startup_video_pump_update == update_guest &&
        g_startup_video_pump_stack != 0) {
      return;
    }

    if (ctx.r1.u32 != 0) {
      stack = AllocateFableStartupVideoPumpStack(ctx, base);
    }
    if (stack == 0) {
      stack = ctx.r1.u32;
    }
    pump_ctx.r1.u32 = stack;
    pump_ctx.r3.u32 = wrapper_guest;

    g_startup_video_pump_base = base;
    g_startup_video_pump_wrapper = wrapper_guest;
    g_startup_video_pump_update = update_guest;
    g_startup_video_pump_stack = stack;
    g_startup_video_pump_context = pump_ctx;
    g_startup_video_pump_requested_update.store(update_guest,
                                                std::memory_order_release);
    g_startup_video_pump_serviced_update.store(0, std::memory_order_release);
    g_startup_video_pump_init_requested_update.store(
        0, std::memory_order_release);
    g_startup_video_pump_init_completed_update.store(
        0, std::memory_order_release);
    g_startup_video_pump_dispatch_requested_update.store(
        0, std::memory_order_release);
    g_startup_video_pump_dispatch_completed_update.store(
        0, std::memory_order_release);
    g_startup_video_pump_native_update_waiting_update.store(
        0, std::memory_order_release);
    g_startup_video_pump_in_flight.store(false, std::memory_order_release);
    g_startup_video_pump_completed_update.store(0,
                                                std::memory_order_release);
    g_startup_video_observed_bink_blocked_update.store(
        0, std::memory_order_release);
  }

  if (!g_logged_plume_pump_startup_video_context) {
    REXLOG_INFO(
        "Refii plume present pump captured native startup video PPC context "
        "wrapper=0x{:08X} update=0x{:08X} stack=0x{:08X} allocated_stack={}",
        wrapper_guest, update_guest, stack, stack != ctx.r1.u32);
    g_logged_plume_pump_startup_video_context = true;
  }
}

void NotifyFableStartupVideoPumpInitComplete(uint32_t wrapper_guest,
                                             uint32_t update_guest) {
  uint32_t registered_wrapper = 0;
  uint32_t registered_update = 0;
  {
    std::lock_guard lock(g_startup_video_pump_mutex);
    registered_wrapper = g_startup_video_pump_wrapper;
    registered_update = g_startup_video_pump_update;
  }
  if (registered_update == 0) {
    return;
  }
  if (wrapper_guest != 0 && registered_wrapper != 0 &&
      wrapper_guest != registered_wrapper) {
    return;
  }
  if (update_guest != 0 && update_guest != registered_update) {
    return;
  }

  const uint32_t completion_update =
      update_guest != 0 ? update_guest : registered_update;
  g_startup_video_pump_init_completed_update.store(
      completion_update, std::memory_order_release);
  g_startup_video_pump_serviced_update.store(0, std::memory_order_release);
  g_startup_video_pump_in_flight.store(false, std::memory_order_release);

  static std::atomic<uint32_t> init_completion_logs{0};
  const uint32_t count =
      init_completion_logs.fetch_add(1, std::memory_order_relaxed) + 1u;
  if (count <= 8u || count % 120u == 0u) {
    REXLOG_INFO(
        "Refii plume present pump observed native startup video init complete "
        "#{} wrapper=0x{:08X} update=0x{:08X} requested=0x{:08X}",
        count,
        wrapper_guest != 0 ? wrapper_guest : registered_wrapper,
        completion_update,
        g_startup_video_pump_init_requested_update.load(
            std::memory_order_acquire));
  }
  g_present_pump_cv.notify_one();
}

void NotifyFableStartupVideoPumpDispatchComplete(uint32_t wrapper_guest,
                                                 uint32_t update_guest) {
  uint32_t registered_wrapper = 0;
  uint32_t registered_update = 0;
  {
    std::lock_guard lock(g_startup_video_pump_mutex);
    registered_wrapper = g_startup_video_pump_wrapper;
    registered_update = g_startup_video_pump_update;
  }
  if (registered_update == 0) {
    return;
  }
  if (wrapper_guest != 0 && registered_wrapper != 0 &&
      wrapper_guest != registered_wrapper) {
    return;
  }
  if (update_guest != 0 && update_guest != registered_update) {
    return;
  }

  const uint32_t completion_update =
      update_guest != 0 ? update_guest : registered_update;
  g_startup_video_pump_dispatch_completed_update.store(
      completion_update, std::memory_order_release);
  g_startup_video_pump_serviced_update.store(0, std::memory_order_release);
  g_startup_video_pump_in_flight.store(false, std::memory_order_release);

  static std::atomic<uint32_t> dispatch_completion_logs{0};
  const uint32_t count =
      dispatch_completion_logs.fetch_add(1, std::memory_order_relaxed) + 1u;
  if (count <= 8u || count % 120u == 0u) {
    REXLOG_INFO(
        "Refii plume present pump observed native startup video dispatch "
        "complete #{} wrapper=0x{:08X} update=0x{:08X} requested=0x{:08X}",
        count,
        wrapper_guest != 0 ? wrapper_guest : registered_wrapper,
        completion_update,
        g_startup_video_pump_dispatch_requested_update.load(
            std::memory_order_acquire));
  }
  g_present_pump_cv.notify_one();
}

void NotifyFableStartupVideoPumpRunComplete(uint32_t update_guest) {
  const uint32_t registered_update = g_startup_video_pump_update;
  if (registered_update == 0) {
    return;
  }
  if (update_guest != 0 && registered_update != 0 &&
      update_guest != registered_update) {
    return;
  }
  const uint32_t completion_update =
      update_guest != 0 ? update_guest : registered_update;

  uint8_t *base = nullptr;
  {
    std::lock_guard lock(g_startup_video_pump_mutex);
    base = g_startup_video_pump_base;
  }
  if (base != nullptr && (FableStartupVideoObservedBinkActive(base) ||
                          FableStartupVideoRecentBinkUploadActive())) {
    g_startup_video_pump_native_update_waiting_update.store(
        0, std::memory_order_release);
    g_startup_video_pump_serviced_update.store(0, std::memory_order_release);
    g_startup_video_pump_in_flight.store(false, std::memory_order_release);
    static std::atomic<uint32_t> deferred_active_bink_completions{0};
    const uint32_t count =
        deferred_active_bink_completions.fetch_add(1,
                                                   std::memory_order_relaxed) +
        1u;
    if (count <= 8u || count % 120u == 0u) {
      REXLOG_INFO(
          "Refii plume present pump deferred startup video completion for "
          "active Bink movie #{} update=0x{:08X}",
          count, completion_update);
    }
    return;
  }

  CompleteFableStartupVideoPumpForHandoff(completion_update, "pump-run");
}


void NotifyFableStartupVideoBinkMovieActive(uint8_t *base,
                                            uint32_t movie_guest) {
  if (!LooksLikeFableStartupBinkMovie(base, movie_guest)) {
    return;
  }
  bink::RememberStartupActiveMovie(base, movie_guest);

  uint32_t update_guest = 0;
  {
    std::lock_guard lock(g_startup_video_pump_mutex);
    if (g_startup_video_pump_base == base) {
      update_guest = g_startup_video_pump_update;
    }
  }
  const bool movie_still_playing =
      FableStartupBinkMovieStillPlaying(base, movie_guest);
  const bool update_complete =
      update_guest != 0 && FableStartupVideoUpdateComplete(base, update_guest);
  const bool update_completed_for_handoff =
      update_guest != 0 &&
      g_startup_video_pump_completed_update.load(std::memory_order_acquire) ==
          update_guest;
  const bool update_blocked =
      update_completed_for_handoff ||
      (update_guest != 0 && !movie_still_playing &&
       (update_complete ||
        g_startup_video_observed_bink_blocked_update.load(
            std::memory_order_acquire) == update_guest));
  if (update_blocked) {
    {
      std::lock_guard lock(g_startup_video_observed_bink_mutex);
      g_startup_video_observed_bink_base = nullptr;
      g_startup_video_observed_bink_movie = 0;
    }
    g_startup_video_observed_bink_ns.store(0, std::memory_order_release);
    return;
  }

  {
    std::lock_guard lock(g_startup_video_observed_bink_mutex);
    g_startup_video_observed_bink_base = base;
    g_startup_video_observed_bink_movie = movie_guest;
  }
  g_startup_video_observed_bink_ns.store(RendererTraceNowNs(),
                                         std::memory_order_release);
}

void NotifyFableStartupVideoBinkMovieFinished(uint8_t *base,
                                              uint32_t movie_guest) {
  if (base == nullptr || movie_guest == 0) {
    return;
  }
  bink::ClearStartupActiveMovie(base, movie_guest);
  g_startup_video_bink_finished_ns.store(RendererTraceNowNs(),
                                         std::memory_order_release);

  uint32_t update_guest = 0;
  {
    std::lock_guard lock(g_startup_video_pump_mutex);
    if (g_startup_video_pump_base == base) {
      update_guest = g_startup_video_pump_update;
    }
  }

  bool cleared = false;
  {
    std::lock_guard lock(g_startup_video_observed_bink_mutex);
    if (g_startup_video_observed_bink_base == base &&
        g_startup_video_observed_bink_movie == movie_guest) {
      g_startup_video_observed_bink_base = nullptr;
      g_startup_video_observed_bink_movie = 0;
      cleared = true;
    }
  }

  if (cleared) {
    g_startup_video_observed_bink_ns.store(0, std::memory_order_release);
    REXLOG_INFO(
        "Refii plume present pump cleared finished observed Bink movie "
        "movie=0x{:08X}",
        movie_guest);
  }

  if (update_guest != 0 &&
      FableStartupVideoUpdateComplete(base, update_guest)) {
    CompleteFableStartupVideoPumpForHandoff(update_guest,
                                            "final-bink-finished");
  }
}

RendererHookTimingSnapshot GetRendererHookTimingSnapshot() {
  RendererHookTimingSnapshot snapshot;
  snapshot.hook_enters = g_hook_enter_count.load(std::memory_order_relaxed);
  snapshot.renderer_setup_hooks =
      g_renderer_setup_hook_count.load(std::memory_order_relaxed);
  snapshot.draw_hook_enters =
      g_draw_hook_enter_count.load(std::memory_order_relaxed);
  snapshot.display_present_hooks =
      g_display_present_hook_count.load(std::memory_order_relaxed);
  snapshot.plume_present_requests =
      g_plume_present_request_count.load(std::memory_order_relaxed);
  snapshot.plume_present_pump_requests =
      g_plume_present_pump_request_count.load(std::memory_order_relaxed);
  snapshot.plume_present_pump_fable_advances =
      g_plume_present_pump_fable_advance_count.load(std::memory_order_relaxed);
  snapshot.guest_suppressed_hooks =
      g_guest_suppressed_hook_count.load(std::memory_order_relaxed);
  snapshot.last_hook_gap_us =
      g_last_hook_gap_us.load(std::memory_order_relaxed);
  snapshot.last_draw_hook_gap_us =
      g_last_draw_hook_gap_us.load(std::memory_order_relaxed);
  snapshot.last_display_present_gap_us =
      g_last_display_present_gap_us.load(std::memory_order_relaxed);
  snapshot.last_plume_present_request_gap_us =
      g_last_plume_present_request_gap_us.load(std::memory_order_relaxed);
  snapshot.last_plume_present_pump_gap_us =
      g_last_plume_present_pump_gap_us.load(std::memory_order_relaxed);
  snapshot.fable_renderer =
      g_last_fable_renderer.load(std::memory_order_relaxed);
  snapshot.fable_renderer_owner =
      g_last_fable_renderer_owner.load(std::memory_order_relaxed);
  snapshot.fable_game = g_last_fable_game.load(std::memory_order_relaxed);
  snapshot.fable_device = g_last_fable_device.load(std::memory_order_relaxed);
  snapshot.fable_present_submitted =
      g_last_fable_present_submitted.load(std::memory_order_relaxed);
  snapshot.fable_present_completed =
      g_last_fable_present_completed.load(std::memory_order_relaxed);
  snapshot.fable_swap_callbacks =
      g_last_fable_swap_callbacks.load(std::memory_order_relaxed);
  snapshot.fable_present_pending =
      g_last_fable_present_pending.load(std::memory_order_relaxed);
  snapshot.plume_present_pump_target_fps = RendererPlumePresentTargetFps();
  snapshot.seen_display_swap_hook =
      g_seen_plume_display_swap_hook.load(std::memory_order_relaxed);
  snapshot.plume_present_pump_enabled = RendererPlumePresentPumpEnabled();

  std::lock_guard lock(g_hook_timing_mutex);
  CopyTimingText(snapshot.last_renderer_setup_symbol,
                 sizeof(snapshot.last_renderer_setup_symbol),
                 g_last_renderer_setup_symbol);
  CopyTimingText(snapshot.last_hook_symbol, sizeof(snapshot.last_hook_symbol),
                 g_last_hook_symbol);
  CopyTimingText(snapshot.last_display_present_symbol,
                 sizeof(snapshot.last_display_present_symbol),
                 g_last_display_present_symbol);
  CopyTimingText(snapshot.last_plume_present_symbol,
                 sizeof(snapshot.last_plume_present_symbol),
                 g_last_plume_present_symbol);
  CopyTimingText(snapshot.last_plume_present_reason,
                 sizeof(snapshot.last_plume_present_reason),
                 g_last_plume_present_reason);
  CopyTimingText(snapshot.last_guest_suppressed_symbol,
                 sizeof(snapshot.last_guest_suppressed_symbol),
                 g_last_guest_suppressed_symbol);
  return snapshot;
}

RendererHookStatsSnapshots GetRendererHookStatsSnapshots() {
  RendererHookStatsSnapshots snapshots = {};
  for (uint32_t index = 0; index < kRendererHookTargetCount; ++index) {
    const RendererHookTarget &target = kRendererHookTargets[index];
    const RendererHookAtomicStats &stats = g_renderer_hook_stats[index];
    RendererHookStatsSnapshot &snapshot = snapshots[index];
    snapshot.id = target.id;
    CopyTimingText(snapshot.name, sizeof(snapshot.name),
                   target.name != nullptr ? target.name : "");
    snapshot.sonic_address = target.sonic_address;
    snapshot.fable_address = target.fable_address;
    snapshot.hit_count = stats.hit_count.load(std::memory_order_relaxed);
    snapshot.guest_passthrough_count =
        stats.guest_passthrough_count.load(std::memory_order_relaxed);
    snapshot.first_frame_seen =
        stats.first_frame_seen.load(std::memory_order_relaxed);
    snapshot.last_frame_seen =
        stats.last_frame_seen.load(std::memory_order_relaxed);
  }
  return snapshots;
}

RendererHookArgs CaptureRendererHookArgs(const PPCContext &ctx) {
  return {
      .r3 = ctx.r3.u32,
      .r4 = ctx.r4.u32,
      .r5 = ctx.r5.u32,
      .r6 = ctx.r6.u32,
      .r7 = ctx.r7.u32,
      .r8 = ctx.r8.u32,
      .r9 = ctx.r9.u32,
      .r10 = ctx.r10.u32,
      .r14 = ctx.r14.u32,
      .r15 = ctx.r15.u32,
      .r16 = ctx.r16.u32,
      .r17 = ctx.r17.u32,
      .r18 = ctx.r18.u32,
      .r19 = ctx.r19.u32,
      .r20 = ctx.r20.u32,
      .r21 = ctx.r21.u32,
      .r22 = ctx.r22.u32,
      .r23 = ctx.r23.u32,
      .r24 = ctx.r24.u32,
      .r25 = ctx.r25.u32,
      .r26 = ctx.r26.u32,
      .r27 = ctx.r27.u32,
      .r28 = ctx.r28.u32,
      .r29 = ctx.r29.u32,
      .r30 = ctx.r30.u32,
      .r31 = ctx.r31.u32,
      .r1 = ctx.r1.u32,
      .lr = static_cast<uint32_t>(ctx.lr),
      .r4_u64 = ctx.r4.u64,
      .r7_u64 = ctx.r7.u64,
  };
}

RendererHookArgs CaptureRendererHookArgsLight(const PPCContext &ctx) {
  // r14-r31 stay at the struct's zero defaults -- deterministic, never stack
  // garbage -- so a handler that (wrongly) read a high register from a
  // kLight capture sees 0, not noise. f1-f6 were never filled by ANY capture
  // (see the FableHookArgsCaptureLevel audit note), so their zero defaults
  // are identical to the pre-split behaviour.
  return {
      .r3 = ctx.r3.u32,
      .r4 = ctx.r4.u32,
      .r5 = ctx.r5.u32,
      .r6 = ctx.r6.u32,
      .r7 = ctx.r7.u32,
      .r8 = ctx.r8.u32,
      .r9 = ctx.r9.u32,
      .r10 = ctx.r10.u32,
      .r1 = ctx.r1.u32,
      .lr = static_cast<uint32_t>(ctx.lr),
      .r4_u64 = ctx.r4.u64,
      .r7_u64 = ctx.r7.u64,
  };
}

RendererHookArgs CaptureRendererHookArgsFor(const FableHookSymbolDesc *desc,
                                            const PPCContext &ctx) {
  // Fast-path symbols skip the capture census entirely (two clock reads +
  // three atomics per capture, twice per passthrough call). Their eligibility
  // guarantees capture_level == kLight in EVERY mode (the kFull consumers —
  // obs_cpu_texture_layout_helper — are excluded from eligibility), so this
  // is safe even when the runtime fast-path gate is off. Their call volume is
  // visible as "hookpath fast=" instead of in capture=.
  if (desc != nullptr && desc->fast_path_eligible) {
    return CaptureRendererHookArgsLight(ctx);
  }
  const auto t0 = std::chrono::steady_clock::now();
  const bool full =
      desc == nullptr ||
      desc->capture_level == FableHookArgsCaptureLevel::kFull;
  RendererHookArgs args =
      full ? CaptureRendererHookArgs(ctx) : CaptureRendererHookArgsLight(ctx);
  g_hook_args_capture_us.fetch_add(
      static_cast<uint64_t>(
          std::chrono::duration_cast<std::chrono::microseconds>(
              std::chrono::steady_clock::now() - t0)
              .count()),
      std::memory_order_relaxed);
  g_hook_args_capture_calls.fetch_add(1, std::memory_order_relaxed);
  if (full) {
    g_hook_args_capture_full_calls.fetch_add(1, std::memory_order_relaxed);
  }
  return args;
}


std::array<std::atomic<uint64_t>, 8> g_fable_gui_pipeline_stage_hits = {};
std::array<std::atomic<uint32_t>, 256> g_fable_world_command_ids = {};

void CountFableWorldCommandId(uint32_t command_id) {
  g_fable_world_command_ids[command_id & 0xFFu].fetch_add(
      1, std::memory_order_relaxed);
}

void CountFableGuiPipelineStage(uint32_t stage) {
  if (stage < g_fable_gui_pipeline_stage_hits.size()) {
    g_fable_gui_pipeline_stage_hits[stage].fetch_add(
        1, std::memory_order_relaxed);
  }
}

// Dump cumulative per-target hook hit counters when the title scene walker
// fires. Runs on both backends so xenia-vs-plume title traversal can be
// diffed from logs; self-capped to the first walks plus powers of two.
void DumpRendererHookTraversalCounters() {
  static std::atomic<uint32_t> traversal_walker_calls{0};
  const uint32_t call =
      traversal_walker_calls.fetch_add(1, std::memory_order_relaxed) + 1u;
  if (call > 6u && (call & (call - 1u)) != 0) {
    return;
  }
  for (const RendererHookTarget &target : kRendererHookTargets) {
    const uint32_t index = static_cast<uint32_t>(target.id);
    if (index >= kRendererHookTargetCount) {
      continue;
    }
    const uint64_t hits =
        g_renderer_hook_stats[index].hit_count.load(std::memory_order_relaxed);
    if (hits == 0) {
      continue;
    }
    REXLOG_INFO(
        "Refii renderer native traversal walk={} target={} fable=0x{:08X} "
        "hits={} first_frame={} last_frame={}",
        call, target.name, target.fable_address, hits,
        g_renderer_hook_stats[index].first_frame_seen.load(
            std::memory_order_relaxed),
        g_renderer_hook_stats[index].last_frame_seen.load(
            std::memory_order_relaxed));
  }
  REXLOG_INFO(
      "Refii renderer gui pipeline stages walk={} queue_append={} "
      "enable_prep={} dispatch={} mesh_c80={} mesh_d68={} title_queue={} "
      "geom_build={} case3_invoke={}",
      call,
      g_fable_gui_pipeline_stage_hits[0].load(std::memory_order_relaxed),
      g_fable_gui_pipeline_stage_hits[1].load(std::memory_order_relaxed),
      g_fable_gui_pipeline_stage_hits[2].load(std::memory_order_relaxed),
      g_fable_gui_pipeline_stage_hits[3].load(std::memory_order_relaxed),
      g_fable_gui_pipeline_stage_hits[4].load(std::memory_order_relaxed),
      g_fable_gui_pipeline_stage_hits[5].load(std::memory_order_relaxed),
      g_fable_gui_pipeline_stage_hits[6].load(std::memory_order_relaxed),
      g_fable_gui_pipeline_stage_hits[7].load(std::memory_order_relaxed));
  std::string command_bins;
  for (uint32_t id = 0; id < g_fable_world_command_ids.size(); ++id) {
    const uint32_t hits =
        g_fable_world_command_ids[id].load(std::memory_order_relaxed);
    if (hits != 0) {
      command_bins += fmt::format(" {}:{}", id, hits);
    }
  }
  REXLOG_INFO("Refii renderer world command ids walk={}{}", call,
              command_bins);
}

void ObserveNativeBackendHookEnter(const char *symbol, RendererHookKind kind,
                                   const RendererHookArgs &args,
                                   uint8_t *base) {
  (void)kind;
  static std::atomic<bool> title_walk_seen{false};
  const std::string_view symbol_view(symbol);
  // The title-frame work package missing under plume (CreateSurface +
  // DrawPrimitive + SetScissorRect) needs its guest callsites; sample the
  // link register on the working backend once the title walk is live so
  // boot-time calls do not consume the cap.
  if (symbol_view == "sub_821F4EC0" || symbol_view == "sub_8221C3E8") {
    if (!title_walk_seen.load(std::memory_order_relaxed)) {
      return;
    }
    static std::atomic<uint32_t> logged_native_callsites{0};
    const uint32_t count =
        logged_native_callsites.fetch_add(1, std::memory_order_relaxed) + 1u;
    if (count <= 96u || (count & (count - 1u)) == 0) {
      REXLOG_INFO(
          "Refii renderer native callsite sample #{} symbol={} lr=0x{:08X} "
          "r3=0x{:08X} r4=0x{:08X} r5=0x{:08X} r6=0x{:08X} r7=0x{:08X}",
          count, symbol_view, args.lr, args.r3, args.r4, args.r5, args.r6,
          args.r7);
    }
    return;
  }
  if (symbol_view != "sub_8223CFA0") {
    return;
  }
  title_walk_seen.store(true, std::memory_order_relaxed);
  ObserveNativeSceneWalkerProbe(args, base);
  DumpRendererHookTraversalCounters();
}

// WAIT/PRESENT ROOT PROBE (2026-07-28, temporary — remove once the title stall
// is located). Counts ENTER and EXIT separately for the presentation roots and
// for the four guest GPU waits that were previously unhooked.
//
// Why enter/exit and not just a call count: every one of these waits is a
// no-yield spin whose watchdog cannot fire in our port (D3D_CBlocker_Check
// compares KTHREAD+0x58, which ReXGlue declares as `uint8_t unk_58[4]` and
// never writes). A thread parked in one of them therefore never returns, and
// shows up here as a PERMANENT enter>exit imbalance naming the exact wait.
// A call count alone cannot distinguish "called a lot" from "called once and
// never came back".
struct RendererWaitProbeRoot {
  std::string_view symbol;
  const char *name;
  // false => this root runs while the GUEST holds a critical section, so
  // logging from either hook side can deadlock. Counters only; the pump
  // thread reports them.
  bool log_safe;
};
// Namespace scope (was function-local) so the per-symbol probe index can be
// resolved ONCE into FableHookSymbolDesc::wait_probe_index instead of a
// full-table string_view scan on every hook enter AND exit.
constexpr RendererWaitProbeRoot kWaitProbeRoots[] = {
      {"sub_82BA34D8", "Swap", true},
      {"sub_821E8EC0", "KickOff", true},
      {"sub_821E8D38", "KickOffSegment", true},
      {"sub_82BA2DB8", "SwapCallback", true},
      {"sub_82BA32F8", "ComplexSwap", true},
      {"sub_82206400", "W2_SecondaryPosition", true},
      {"sub_82286618", "W3_PrimaryRange", true},
      {"sub_82BA4D80", "W5_XpsConfigure", true},
      {"sub_82BA5818", "W6_DispatchCompletion", true},
      // PRIME SUSPECT for the ~124ms unaccounted frame time (2026-07-28).
      // sub_822DFE08 is the render thread's wait for the guest to commit a
      // render command. Read from the core dump: dword_83360358 = 2
      // (D3DPRESENT_INTERVAL_TWO) and flt_8336035C = 60.0, so its timeout is
      // 1.0/(60/2) = 33.3ms. sub_82388380 enters the GPU-access lock THREE
      // times per frame (D/F/G), so if the producer has not committed, that is
      // 3 x 33ms ~= 100ms of pure yield-spinning per frame — the right size for
      // the gap. Its spin site 0x822DFF2C was one of only two SwitchToThread
      // addresses in the probe, at ~817k yields/sec, which is exactly what a
      // 33ms yield loop looks like.
      {"sub_822DFE08", "RingCommandWait", false},
      // THE PRODUCER (2026-07-28). sub_821A5BD0 is the publish-cursor sync —
      // the second half of the two-stage handoff (a1[15]=a1[10]) without which
      // the consumer cannot see a commit. The guest's design publishes one list
      // per 30Hz tick, and the consumer only waits when nothing has been
      // published for >150ms. So the single number that decides whether the
      // producer is starving is its publish RATE:
      //   ~30/sec  -> producer is fine, the consumer's staleness is elsewhere
      //   ~5/sec   -> producer is starving; fix scheduling, not the renderer
      // log_safe=false: publish runs inside the guest's ring critical section
      // (a1[18]) and is also reached from Render_GpuAccessLock_Enter/Leave
      // pairs, so logging here risks the same AB-BA that hung build 105.
      {"sub_821A5BD0", "ProducerPublish", false},
      // FRAME-BODY BISECTION (2026-07-28). Frame accounting proved our renderer
      // works ~30% of the title frame and idles ~70%, and that ~100-150ms sits
      // in guest code inside sub_82388380 that has never been measured. These
      // roots bisect that body. Ranked by the static cost map (IDA agent pass):
      // sub_821C3858's subtree is larger than the rest of the body combined.
      //
      // ALL log_safe=false. sub_82388380 holds the critical section at a1+0 for
      // its ENTIRE body (enter 0x823883E4, leave 0x82388708) and its caller
      // sub_823881C0 already holds the same section, and span #1
      // (0x8238852C-0x82388540) additionally holds stru_8349F7FC across
      // sub_821C3858. Every root below therefore runs under at least one guest
      // lock — logging from any of them is the build-105 AB-BA deadlock.
      {"sub_821C3858", "FrameDirector", false},
      // THE PRIME SUSPECT (IDA 2026-07-28). sub_82181B68 (sole call site
      // 0x821C3AB0, inside sub_821C3858) contains an UNBOUNDED, NON-YIELDING
      // busy loop at 0x82181D3C-0x82181D78 that pumps a resource-load state
      // machine (sub_82ABAAE8 -> sub_82C698F0 file open / sub_82C69990 exists)
      // until it completes:
      //   do { if (!*(a3+3712)) { if (*(a3+3404)) if (sub_82ABAAE8()) *(a3+3712)=1; } }
      //   while (!*(a3+3712));
      // That is a SYNCHRONOUS FILE LOAD on the render thread, inside GPU LOCK #1
      // and the frame-wide section. It uses no wait primitive at all, which is
      // exactly why every SwitchToThread/Sleep census we have run missed it.
      // Do NOT probe sub_82ABAAE8 itself — it is the loop BODY, and hooking a
      // spin body is the 6.29M-hit mistake that cost 126s of wall time.
      {"sub_82181B68", "ResourceLoadSpin", false},
      {"sub_821C3C60", "SubPassOrchestrator", false},
      {"sub_821C3670", "ScenePass", false},
      {"sub_8226E738", "SceneDrawSubmit", false},
      // Cloth rendezvous (IDA 2026-07-30). ClothSimWait = render thread
      // blocking on the cloth worker's completion event (dword_83496F14)
      // inside SceneDrawSubmit; its enter also takes CS dword_834A5030, so
      // log_safe=false. ClothWorkerBatch = the worker's queue drain, which
      // re-enters the same CS per item. Together they split wait time into
      // wake-up latency vs. actual sim compute. Natively ~0: the worker had
      // hardware thread 2 to itself and menu-scale cloth is sub-ms.
      {"sub_82A4F5D8", "ClothSimWait", false},
      {"rex_lhClothSim_ProcessQueue", "ClothWorkerBatch", false},
      // The overlay/2D executor, and the one place static analysis is
      // structurally blind: its inner loop is pure `bctrl` virtual dispatch at
      // 0x82B66B00, so its "13 functions / 2323 instructions" static subtree is
      // an artifact of bctrl being unfollowable, not evidence it is cheap.
      // Its enter comes via OnRendererPassQueueHook, which calls
      // OnRendererGuestHookEnter("sub_82B668D0") — so enter/exit ARE symmetric
      // and the duration is real.
      {"sub_82B668D0", "OverlayExec", false},
      // NOT PROBED: sub_8218FA68 (walks the same list). It is hooked in
      // src/vfs/src/hooks/boot_lifecycle.cpp — which also means a renderer hook
      // for it is a link-time multiple-definition error — and that hook emits
      // no enter/exit callbacks, so a root would never fire.
  };
  constexpr size_t kWaitProbeRootCount =
      sizeof(kWaitProbeRoots) / sizeof(kWaitProbeRoots[0]);

int RendererWaitProbeIndexForSymbol(std::string_view symbol) {
  for (size_t i = 0; i < kWaitProbeRootCount; ++i) {
    if (symbol == kWaitProbeRoots[i].symbol) {
      return static_cast<int>(i);
    }
  }
  return -1;
}

void RendererWaitProbeNoteIndexed(int probe_index,
                                  const RendererHookArgs &args,
                                  bool is_enter) {
  constexpr size_t kRootCount = kWaitProbeRootCount;
  static_assert(kRootCount <= kWaitProbeCapacity,
                "wait_ns_storage too small for the probe root table");
  static std::atomic<uint64_t> enters[kRootCount]{};
  static std::atomic<uint64_t> exits[kRootCount]{};
  // Published ONCE for the pump thread (the only place it is SAFE to log
  // these). This used to re-run the name-copy loop plus the four global
  // stores on EVERY hook enter AND exit — ~24 stores per call of pure fixed
  // overhead at ~5,800 hooked calls/frame. Idempotent, so a benign race
  // between two first callers publishes identical values.
  static const char *names[kRootCount] = {};
  static std::atomic<bool> probe_table_published{false};
  if (!probe_table_published.load(std::memory_order_acquire)) {
    for (size_t i = 0; i < kRootCount; ++i) {
      names[i] = kWaitProbeRoots[i].name;
    }
    g_wait_probe_exits = &exits[0];
    g_wait_probe_ns_public = &wait_ns_storage[0];
    g_wait_probe_names = &names[0];
    g_wait_probe_count = kRootCount;
    probe_table_published.store(true, std::memory_order_release);
  }

  if (probe_index < 0 ||
      static_cast<size_t>(probe_index) >= kRootCount) {
    return;
  }
  const size_t i = static_cast<size_t>(probe_index);
  // DURATION, not just count. An enter/exit imbalance detects a PARKED wait,
  // but the suspicion here is different: a wait that returns normally every
  // time and still burns its full 33.3ms timeout. Only elapsed time
  // distinguishes "called and returned" from "called, spun the whole
  // interval, and returned". One thread_local start per root is enough —
  // these roots are not re-entered on the same thread.
  static thread_local std::chrono::steady_clock::time_point
      wait_enter_at[kRootCount]{};
  std::atomic<uint64_t> *wait_ns = wait_ns_storage;
  // NEVER LOG FROM HERE. `sub_822DFE08` is called from inside
  // `Render_GpuAccessLock_Enter` (0x82A4F0E0), which has already taken the
  // `stru_8349F7FC` critical section and does NOT release it until after this
  // returns. A REXLOG on this path therefore runs while the guest holds a
  // lock other threads are blocked on, and build 105 hung at frame 1 with the
  // renderer silent and only netplay threads still logging — the classic
  // AB-BA shape already recorded in refii-title-freeze-deadlock.
  // Accumulate into atomics only; the pump thread reports them.
  if (!is_enter) {
    exits[i].fetch_add(1, std::memory_order_relaxed);
    if (wait_enter_at[i].time_since_epoch().count() != 0) {
      const uint64_t ns = static_cast<uint64_t>(
          std::chrono::duration_cast<std::chrono::nanoseconds>(
              std::chrono::steady_clock::now() - wait_enter_at[i])
              .count());
      wait_ns[i].fetch_add(ns, std::memory_order_relaxed);
      wait_enter_at[i] = {};
    }
    return;
  }
  wait_enter_at[i] = std::chrono::steady_clock::now();
  // Only the 0x8231A118 site publishes; see kProducerPublishReturnAddress.
  if (args.lr == kProducerPublishReturnAddress) {
    g_producer_true_publishes.fetch_add(1, std::memory_order_relaxed);
  }
  const uint64_t n = enters[i].fetch_add(1, std::memory_order_relaxed) + 1u;
  const uint64_t done = exits[i].load(std::memory_order_relaxed);
  if (kWaitProbeRoots[i].log_safe && (n <= 8u || (n % 256u) == 0u)) {
    REXLOG_INFO(
        "Refii wait probe {} enter={} exit={} inflight={} r3=0x{:08X} "
        "lr=0x{:08X}",
        kWaitProbeRoots[i].name, static_cast<unsigned long long>(n),
        static_cast<unsigned long long>(done),
        static_cast<long long>(static_cast<int64_t>(n) -
                               static_cast<int64_t>(done)),
        args.r3, args.lr);
  }
}

void RendererWaitProbeNote(const char *symbol, const RendererHookArgs &args,
                           bool is_enter) {
  RendererWaitProbeNoteIndexed(
      RendererWaitProbeIndexForSymbol(
          symbol != nullptr ? std::string_view(symbol) : std::string_view()),
      args, is_enter);
}

void NoteFableProducerPublishSpin(uint32_t lr) {
  static const int probe_index =
      RendererWaitProbeIndexForSymbol("sub_821A5BD0");
  RendererHookArgs args{};
  args.lr = lr;
  RendererWaitProbeNoteIndexed(probe_index, args, true);
  RendererWaitProbeNoteIndexed(probe_index, args, false);
}

// ---------------------------------------------------------------------------
// FableHookSymbolDesc: per-call-site descriptor resolution.
//
// Every field below is computed by the EXISTING string-based helper for that
// question — this function is the only place they run for a hot symbol, once,
// instead of on every hooked guest call. See the struct comment in
// renderer_hook_targets.h for the measurement that motivated it.
// ---------------------------------------------------------------------------

namespace {

std::mutex g_hook_symbol_desc_mutex;
// Node-stable containers: descriptors and their name storage must never move,
// because wrappers cache raw pointers in function-local statics.
std::deque<std::string> g_hook_symbol_desc_names;
std::unordered_map<std::string, std::unique_ptr<FableHookSymbolDesc>>
    g_hook_symbol_desc_registry;

// Symbol-only mirror of the cvar-gated groups inside IsNativeRenderWorkHook.
// The cvars stay runtime; only the symbol-set membership is hoisted.
void FillNativeRenderWorkDescFields(std::string_view symbol,
                                    FableHookSymbolDesc &desc) {
  desc.title_scene_population = IsFableTitleScenePopulationHook(symbol);
  desc.exact_stub = FableUnleashedExactHookKindForSymbol(symbol) ==
                    FableUnleashedExactHookKind::kStub;
  desc.native_work_plume_owned =
      desc.exact_stub || IsFableGuestShaderConstantReplacementHook(symbol) ||
      IsFableGuestCommandSubmissionHook(symbol) ||
      IsFableGuestClearSubmissionHook(symbol) ||
      IsFableGuestBindingStateSubmissionHook(symbol) ||
      IsFableGuestStateEmulationHook(symbol) ||
      IsFableGuestPendingStateEmulationHook(symbol) ||
      IsFableGuestTerminalDrawSubmissionHook(symbol);
  desc.native_work_replace_resources =
      IsFableResourceLifetimeHook(symbol) ||
      IsFableResourceHeaderBuildHook(symbol) ||
      IsFableResourceUpdateReplacementHook(symbol) ||
      IsFableBufferCreateHook(symbol) || IsFableSurfaceCopyHook(symbol) ||
      IsFableSurfaceMemoryCopyHook(symbol) ||
      IsFableTextureHeaderUtilityHook(symbol) ||
      IsFableGetRenderTargetHook(symbol) || IsFableSurfaceSizeHook(symbol);
  desc.native_work_replace_present = IsNativePresentationWorkHook(symbol);
}

// Exact runtime mirror of IsNativeRenderWorkHook over the cached membership
// bits. Group order matches the original; the groups are independent ORs so
// ordering only matters for the title-population early-out, which is kept
// first.
bool IsNativeRenderWorkHookForDesc(const FableHookSymbolDesc &desc) {
  if (desc.title_scene_population) {
    return false;
  }
  if (RendererPlumeOwnsCommandSubmissionEnabled() &&
      desc.native_work_plume_owned) {
    return true;
  }
  if (RendererReplaceResourcesEnabled() &&
      desc.native_work_replace_resources) {
    return true;
  }
  if (RendererReplacePresentEnabled() && desc.native_work_replace_present) {
    return true;
  }
  return false;
}

// Exact runtime mirror of FableRendererHookAllowedInCurrentMode: the
// physical-wrapper predicate result and the target pointer are symbol
// constants; the mode cvars stay runtime.
bool FableRendererHookAllowedForDesc(const FableHookSymbolDesc &desc) {
  if (RendererRootOnlyHooksEnabled() && !RendererDeepObserverHooksEnabled() &&
      desc.physical_wrapper_required_root) {
    return true;
  }
  return RendererHookAllowedBySurface(desc.target);
}

// Runtime half of the wrapper fast-path decision (the symbol half is
// FableHookSymbolDesc::fast_path_eligible). Same gate as the scene-scope
// marker short-circuit and deliberately NOT cached: cvars can flip at
// runtime, and every desc field follows the rule "symbol identity is cached,
// runtime state is re-read per call". Five cheap getter calls per hook call.
//   * root-only ON + deep observer OFF is what makes
//     RendererHookAllowedBySurface(nullptr) false, i.e. what proves the
//     LOAD-BEARING ObserveRendererHookEnter/Exit were never running for a
//     null-target symbol anyway;
//   * telemetry OFF removes the Tracy plots/zone-text consumers;
//   * Plume backend keeps the native-backend diagnostics path intact.
bool RendererHookWrapperFastPathModeActive() {
  return RendererHooksEnabled() && PlumeRendererBackendEnabled() &&
         RendererRootOnlyHooksEnabled() &&
         !RendererDeepObserverHooksEnabled() &&
         !RendererHookTelemetryEnabled();
}

// TraceHookDecision cost cap: the trace is once per (symbol, decision), but
// the un-cached function pays a std::string key build + unordered_set lookup
// + a string-ladder ShouldRunGuestRendererHook on every call whenever any
// trace cvar is on — and the shipping toml has renderer_trace_hooks=true
// (run 429). With a descriptor the repeat check is two relaxed atomic ops.
void TraceHookDecisionCached(const char *symbol, RendererHookKind kind,
                             const FableHookSymbolDesc *desc) {
  if (desc == nullptr) {
    TraceHookDecision(symbol, kind);
    return;
  }
  if (!ShouldTraceHook(kind)) {
    return;
  }
  const bool run_guest = GetRendererBackend() != RendererBackend::kPlume ||
                         !IsNativeRenderWorkHookForDesc(*desc);
  const uint8_t decision_bit = run_guest ? 1u : 2u;
  if (desc->trace_decisions_logged.load(std::memory_order_relaxed) &
      decision_bit) {
    return;
  }
  desc->trace_decisions_logged.fetch_or(decision_bit,
                                        std::memory_order_relaxed);
  TraceHookDecision(symbol, kind);
}

}  // namespace

void EmulatePlumeOwnedFablePixCaptureControlBookkeeping(uint32_t device,
                                                        uint8_t *base) {
  if (base == nullptr || device == 0) {
    return;
  }

  constexpr uint32_t kPixCaptureControlOffset = 0x5420;
  constexpr uint32_t kPixCapturePacketBranchControlBit = 0x40000000u;
  const uint32_t control =
      LoadGuestU32(base, device + kPixCaptureControlOffset);
  const uint32_t updated_control =
      control | kPixCapturePacketBranchControlBit;
  StoreGuestU32(base, device + kPixCaptureControlOffset, updated_control);

  if (!g_logged_fable_pix_capture_control_bookkeeping) {
    REXLOG_INFO(
        "Refii plume is emulating Fable Pix capture control bookkeeping "
        "device=0x{:08X} control=0x{:08X}->0x{:08X}",
        device, control, updated_control);
    g_logged_fable_pix_capture_control_bookkeeping = true;
  }
}

void EmulatePlumeOwnedFableDisplaySwapBookkeeping(
    std::string_view symbol, const RendererHookArgs &args, uint8_t *base) {
  if (symbol != "sub_82BA34D8" || base == nullptr || args.r3 == 0) {
    return;
  }

  constexpr uint32_t kSubmittedOffset = 0x40B0;
  const uint32_t device = args.r3;
  RememberFableMemoryBase(base);
  const uint32_t submitted = LoadGuestU32(base, device + kSubmittedOffset) + 1u;
  StoreGuestU32(base, device + kSubmittedOffset, submitted);
  CompleteFableDisplaySwapBookkeeping(device, base, submitted);
}

bool RendererPhysicalWrapperRequiredForRootPath(std::string_view symbol,
                                                RendererHookKind /*kind*/) {
  return IsMandatoryFableResourceReplacementHook(symbol) ||
         IsFableGuestTerminalDrawSubmissionHook(symbol) ||
         symbol == "sub_82BA95E0" || // D3D::PixCaptureVideoFrame
         // Pending state dispatch: the GTL's render-state authority — draw
         // packets carry at-draw state only if the dispatch is observed, so
         // the root path requires it (plan doc root-only reduction: "roots
         // needed for packets").
         symbol == "sub_8221E550";
}

bool FableStartupVideoUpdateComplete(uint8_t *base, uint32_t update_guest) {
  if (!LooksLikeFableStartupVideoUpdate(base, update_guest)) {
    return true;
  }

  const uint32_t vector_begin = LoadGuestU32(base, update_guest + 56u);
  const uint32_t vector_end = LoadGuestU32(base, update_guest + 60u);
  const uint32_t vector_count =
      vector_end >= vector_begin ? (vector_end - vector_begin) / 8u : 0u;
  const uint32_t video_index = LoadGuestU32(base, update_guest + 84u);
  return LoadGuestU8(base, update_guest + 96u) != 0 ||
         (vector_count != 0 && video_index >= vector_count);
}

bool FableStartupVideoObservedBinkActive(uint8_t *base) {
  const int64_t observed_ns =
      g_startup_video_observed_bink_ns.load(std::memory_order_acquire);
  if (observed_ns == 0) {
    return false;
  }

  uint8_t *observed_base = nullptr;
  uint32_t movie_guest = 0;
  {
    std::lock_guard lock(g_startup_video_observed_bink_mutex);
    observed_base = g_startup_video_observed_bink_base;
    movie_guest = g_startup_video_observed_bink_movie;
  }

  if (observed_base != base ||
      !LooksLikeFableStartupBinkMovie(observed_base, movie_guest)) {
    return false;
  }

  uint32_t update_guest = 0;
  {
    std::lock_guard lock(g_startup_video_pump_mutex);
    if (g_startup_video_pump_base == base) {
      update_guest = g_startup_video_pump_update;
    }
  }
  const bool movie_still_playing =
      FableStartupBinkMovieStillPlaying(observed_base, movie_guest);
  const bool update_completed_for_handoff =
      update_guest != 0 &&
      g_startup_video_pump_completed_update.load(std::memory_order_acquire) ==
          update_guest;
  const bool update_complete =
      update_guest != 0 && FableStartupVideoUpdateComplete(base, update_guest);
  if (update_completed_for_handoff ||
      (update_complete && !movie_still_playing)) {
    {
      std::lock_guard lock(g_startup_video_observed_bink_mutex);
      if (g_startup_video_observed_bink_base == observed_base &&
          g_startup_video_observed_bink_movie == movie_guest) {
        g_startup_video_observed_bink_base = nullptr;
        g_startup_video_observed_bink_movie = 0;
      }
    }
    g_startup_video_observed_bink_ns.store(0, std::memory_order_release);
    g_startup_video_observed_bink_blocked_update.store(
        update_guest, std::memory_order_release);
    return false;
  }
  if (!movie_still_playing) {
    // A startup update can own more than one movie.  Finishing the current
    // Bink releases only this observed-movie stage; the native update remains
    // authoritative until its vector is exhausted and +96 becomes complete.
    {
      std::lock_guard lock(g_startup_video_observed_bink_mutex);
      if (g_startup_video_observed_bink_base == observed_base &&
          g_startup_video_observed_bink_movie == movie_guest) {
        g_startup_video_observed_bink_base = nullptr;
        g_startup_video_observed_bink_movie = 0;
      }
    }
    g_startup_video_observed_bink_ns.store(0, std::memory_order_release);
    return false;
  }

  const uint32_t count =
      g_startup_video_observed_bink_active_logs.fetch_add(
          1, std::memory_order_relaxed) +
      1u;
  const bool verbose_startup_video_logs = RendererDeepObserverHooksEnabled();
  const uint32_t initial_log_limit = verbose_startup_video_logs ? 4u : 1u;
  const uint32_t periodic_log_interval =
      verbose_startup_video_logs ? 240u : 480u;
  if (count <= initial_log_limit || count % periodic_log_interval == 0u) {
    REXLOG_INFO(
        "Refii plume present pump holding startup video policy for observed "
        "Bink movie #{} movie=0x{:08X} frame={}/{} planes={}",
        count, movie_guest, LoadGuestU32(observed_base, movie_guest + 12u),
        LoadGuestU32(observed_base, movie_guest + 8u),
        LoadGuestU32(observed_base, movie_guest + 276u));
  }
  return true;
}

bool FableStartupVideoRecentBinkUploadActive() {
  constexpr int64_t kRecentBinkUploadGraceNs = 2'500'000'000ll;
  if (g_startup_video_pump_completed_update.load(std::memory_order_acquire) !=
      0) {
    return false;
  }
  uint8_t *base = nullptr;
  uint32_t update = 0;
  {
    std::lock_guard lock(g_startup_video_pump_mutex);
    base = g_startup_video_pump_base;
    update = g_startup_video_pump_update;
  }
  if (base != nullptr && update != 0 &&
      FableStartupVideoUpdateComplete(base, update)) {
    return false;
  }
  const int64_t upload_ns =
      g_plume_bink_video_upload_ready_ns.load(std::memory_order_acquire);
  const int64_t finished_ns =
      g_startup_video_bink_finished_ns.load(std::memory_order_acquire);
  if (upload_ns != 0 && finished_ns >= upload_ns) {
    return false;
  }
  return upload_ns != 0 &&
         RendererTraceNowNs() - upload_ns <= kRecentBinkUploadGraceNs;
}


bool FableRendererHookAllowedInCurrentMode(std::string_view symbol,
                                           RendererHookKind kind) {
  return RendererHookAllowedInCurrentMode(
      symbol, kind, RendererPhysicalWrapperRequiredForRootPath);
}

bool RendererStateDispatchObserverAllowed() {
  return FableRendererHookAllowedInCurrentMode("sub_8221E550",
                                               RendererHookKind::kDraw);
}

bool IsFableStartupVideoPumpActive() {
  uint8_t *base = nullptr;
  uint32_t update = 0;
  {
    std::lock_guard lock(g_startup_video_pump_mutex);
    base = g_startup_video_pump_base;
    update = g_startup_video_pump_update;
  }
  const bool pump_update_incomplete =
      base != nullptr && update != 0 &&
      !FableStartupVideoUpdateComplete(base, update);

  // HANDOFF LATCH (run 212 freeze fix). Once the startup video sequence has
  // handed off (complete_count != 0), the boot/legal/attract movies are done.
  // The guest then plays its OWN video — the looping title-background Bink —
  // through the normal render loop, NOT this pump. That generic Bink activity
  // used to keep the pump "active" (RecentBinkUploadActive / ObservedBinkActive
  // fire for ANY Bink), which re-grabbed device ownership indefinitely and
  // DEADLOCKED the guest render thread: it spins in Render_D3DDeviceOwnership_
  // WaitAndReclaim (sub_8222A5D8 -> sub_82294830, 473k calls) waiting for its
  // command ring's consumed pointer, which never advances while the pump owns
  // the device — so the live title/menu never draws. After handoff only a
  // genuinely NEW, still-incomplete pump update (a re-registered boot movie)
  // may re-activate it; the title loop may not.
  if (g_startup_video_pump_complete_count.load(std::memory_order_acquire) != 0) {
    return pump_update_incomplete;
  }

  if (FableStartupVideoRecentBinkUploadActive()) {
    return true;
  }
  return pump_update_incomplete ||
         (base != nullptr && FableStartupVideoObservedBinkActive(base));
}



const FableHookSymbolDesc *ResolveFableHookSymbolDesc(const char *symbol,
                                                      RendererHookKind kind) {
  if (symbol == nullptr || symbol[0] == '\0') {
    return nullptr;
  }
  std::lock_guard<std::mutex> lock(g_hook_symbol_desc_mutex);
  std::string key(symbol);
  key.push_back('#');
  key.push_back(static_cast<char>('0' + static_cast<int>(kind)));
  auto existing = g_hook_symbol_desc_registry.find(key);
  if (existing != g_hook_symbol_desc_registry.end()) {
    return existing->second.get();
  }

  auto desc = std::make_unique<FableHookSymbolDesc>();
  g_hook_symbol_desc_names.emplace_back(symbol);
  desc->symbol = g_hook_symbol_desc_names.back().c_str();
  const std::string_view symbol_view(desc->symbol);
  desc->kind = static_cast<uint8_t>(kind);
  desc->address = refii::gtl::ResolveHookSymbolAddress(desc->symbol);
  desc->target = ResolveRendererHookTarget(symbol_view);
  desc->wait_probe_index =
      static_cast<int16_t>(RendererWaitProbeIndexForSymbol(symbol_view));
  desc->physical_wrapper_required_root =
      RendererPhysicalWrapperRequiredForRootPath(symbol_view, kind);
  FillNativeRenderWorkDescFields(symbol_view, *desc);
  desc->shader_patch_helper = IsFableShaderPatchHelperHook(symbol_view);
  desc->get_render_target_hook = IsFableGetRenderTargetHook(symbol_view);
  desc->shader_constant_replacement =
      IsFableGuestShaderConstantReplacementHook(symbol_view);
  desc->shader_constant_command_hook =
      IsFableGuestShaderConstantCommandHook(symbol_view);
  desc->shader_constant_state_hook =
      IsFableGuestShaderConstantStateHook(symbol_view);
  desc->private_texture_rect_hook =
      IsFableGuestPrivateTextureRectangleHook(symbol_view);
  desc->surface_size_hook = IsFableSurfaceSizeHook(symbol_view);
  desc->scene_stream_boundary = IsFableSceneStreamBoundaryHook(symbol_view);
  desc->scene_factory_boundary = IsFableSceneFactoryBoundaryHook(symbol_view);
  desc->deep_observer_draw_boundary =
      IsFableDeepObserverDrawCommandBufferBoundary(symbol_view);
  desc->gtl_known = refii::gtl::IsKnownHookSymbolAddress(desc->address);
  desc->gtl_scene_scope =
      refii::gtl::IsSceneSubmitScopeAddress(desc->address);
  FillFableHookSymbolDescObserverFields(*desc);
  // Args-capture width (audit 2026-07-29, see FableHookArgsCaptureLevel):
  // the only handlers reading r14-r31 from args are the observer's CPU
  // texture-layout (tile/untile) probes, and obs_cpu_texture_layout_helper
  // is exactly that symbol set. Everything else reads r3-r10/r1/lr only.
  desc->capture_level = desc->obs_cpu_texture_layout_helper
                            ? FableHookArgsCaptureLevel::kFull
                            : FableHookArgsCaptureLevel::kLight;
  // Wrapper fast-path eligibility (2026-07-30). TRUE only when EVERY consumer
  // in OnRendererGuestHookEnter/Exit beyond the bare counters is provably
  // inert for this symbol under the runtime gate
  // (RendererHookWrapperFastPathModeActive, re-read per call):
  //   * address != 0 — keeps the GTL unmapped-root census intact (address-0
  //     symbols still reach gtl::OnHookEnter's unmapped counter/log);
  //   * !gtl_known && !gtl_scene_scope — gtl::OnHookEnter reduces to the
  //     begin-pin drain (performed by OnHookEnterUnknownSymbolFastPath) and
  //     gtl::OnHookExit's whole address ladder is a no-op, because
  //     IsGtlKnownSymbolAddress is a deliberate SUPERSET of every GTL symbol
  //     constant;
  //   * wait_probe_index < 0 — no wait-probe enter or exit side;
  //   * target == nullptr — RecordRendererHookStatsForDesc reduces to
  //     desc.hits (bumped by the fast path itself), and with root-only ON +
  //     deep observer OFF this is also what makes observer_allowed false, so
  //     the LOAD-BEARING ObserveRendererHookEnter/Exit never ran for this
  //     symbol on the full path either;
  //   * !physical_wrapper_required_root — the other observer grant;
  //   * !scene_stream_boundary — its exit runs
  //     RepairFableSceneStreamPublishHandoff UNGATED. scene_factory_boundary
  //     and deep_observer_draw_boundary stay ELIGIBLE: their only consumers
  //     (CaptureFablePlumeCommandBufferBoundary, the observer feeds) are
  //     behind RendererDeepObserverHooksEnabled, which the runtime gate
  //     requires OFF;
  //   * !obs_cpu_texture_layout_helper — the only kFull args consumers, so
  //     eligibility implies capture_level == kLight (the args-capture
  //     shortcut relies on this);
  //   * none of the symbols the enter/exit bodies special-case by address or
  //     name (GUI-stage census, traversal dump, panel-scroll/compositor
  //     probes, RenderFrame frame seal, DisplaySwap bookkeeping).
  // Symbol constants ONLY — no cvar is baked in, so nothing here needs
  // invalidation when a cvar flips.
  //
  // NOT preserved on the fast path, deliberately: MaybeLogRendererBackendState
  // (whose EnsurePlumePresentPumpStarted keeps the pump alive) and the 1/64
  // RenderDoc poll. Both stay serviced because every phase that draws or
  // presents fires GTL-known roots (Present/DisplaySwap/draw family), which
  // are ineligible and take the full path continuously.
  const bool special_cased_symbol =
      symbol_view == "rex_lhRenderer_RenderFrame" ||
      desc->address == 0x821E1F40u ||  // GUI pipeline stage census
      desc->address == 0x8223CFA0u ||  // traversal counter dump
      desc->address == 0x8221E4A0u ||  // panel inner-call timeline...
      desc->address == 0x821B6EE0u ||
      desc->address == 0x8222C210u ||
      desc->address == 0x821E8C78u ||
      desc->address == 0x821C3858u ||
      desc->address == 0x821EB130u ||  // ...panel inner-call timeline
      desc->address == 0x8220A3F0u ||  // compositor/panel scroll probes
      desc->address == 0x82BA34D8u;    // display swap exit bookkeeping
  desc->fast_path_eligible =
      desc->address != 0u && !special_cased_symbol &&
      desc->wait_probe_index < 0 && desc->target == nullptr &&
      !desc->physical_wrapper_required_root && !desc->gtl_known &&
      !desc->gtl_scene_scope && !desc->scene_stream_boundary &&
      !desc->obs_cpu_texture_layout_helper;

  const FableHookSymbolDesc *result = desc.get();
  g_hook_symbol_desc_registry.emplace(std::move(key), std::move(desc));
  return result;
}

// Scene-scope marker short-circuit. Static proof of equivalence under the
// gate below (root-only, no deep observer, no telemetry/tracing, Plume
// backend):
//   * the four markers are IsFableTitleScenePopulationHook members, so
//     IsNativeRenderWorkHook is unconditionally false -> the guest body always
//     runs and the suppressed path is unreachable;
//   * they have no RendererHookTarget entry (checked 2026-07-29), so
//     RecordRendererHookStats is a no-op and root-only mode never grants the
//     observer (RendererHookAllowedBySurface(nullptr) == false);
//   * they are not wait-probe roots, not scene stream/factory boundaries, and
//     IsFableDeepObserverCommandBufferBoundary is deep-observer-gated;
//   * the only remaining consumer is the GTL scene_submit_depth pair, which
//     the fast path performs (plus the begin-pin drain, which OnHookEnter ran
//     for every mapped symbol).
bool BeginFableSceneScopeMarkerFastPath(const char *symbol, uint8_t *base,
                                        const FableHookSymbolDesc *desc) {
  if (!RendererHookWrapperFastPathModeActive()) {
    return false;
  }
  // Tracing configs (renderer_trace_hooks / renderer_trace_draws) emit a
  // ONE-TIME per-symbol decision log, not per-call behaviour — run 429 shipped
  // renderer_trace_hooks=true and the old gate on it zeroed scene_fast for the
  // whole run. Satisfy the once-only log here and keep the fast path.
  TraceHookDecisionCached(symbol, RendererHookKind::kDraw, desc);
  g_hook_enter_count.fetch_add(1, std::memory_order_relaxed);
  g_draw_hook_enter_count.fetch_add(1, std::memory_order_relaxed);
  g_scene_scope_fast_hits.fetch_add(1, std::memory_order_relaxed);
  refii::gtl::OnSceneSubmitScopeEnter(base);
  return true;
}

void EndFableSceneScopeMarkerFastPath() {
  refii::gtl::OnSceneSubmitScopeExit();
}

namespace {
// Cross-thread autopsy telemetry registry (see renderer_runtime.h). Slots are
// claimed once per guest thread by CAS on tid and never released; symbol
// pointers are string literals so cross-thread reads are always valid.
struct HookThreadTelemetrySlot {
  std::atomic<uint32_t> tid{0};
  std::atomic<const char *> symbol{nullptr};
  std::atomic<int64_t> enter_ns{0};
};
constexpr size_t kHookThreadTelemetrySlots = 64;
HookThreadTelemetrySlot g_hook_thread_telemetry[kHookThreadTelemetrySlots];

inline int64_t HookTelemetryNowNs() {
  return std::chrono::duration_cast<std::chrono::nanoseconds>(
             std::chrono::steady_clock::now().time_since_epoch())
      .count();
}
}  // namespace

void NoteRendererHookThreadTelemetry(const char *symbol) {
  thread_local HookThreadTelemetrySlot *tls_slot = nullptr;
  if (tls_slot == nullptr) {
    auto *thread = rex::system::XThread::GetCurrentThread();
    const uint32_t tid = thread != nullptr ? thread->thread_id() : 0u;
    if (tid == 0u) {
      return;
    }
    for (auto &candidate : g_hook_thread_telemetry) {
      uint32_t expected = 0u;
      if (candidate.tid.load(std::memory_order_relaxed) == tid ||
          candidate.tid.compare_exchange_strong(expected, tid,
                                                std::memory_order_relaxed)) {
        tls_slot = &candidate;
        break;
      }
    }
    if (tls_slot == nullptr) {
      return;  // Registry full; drop telemetry rather than pay for growth.
    }
  }
  tls_slot->symbol.store(symbol, std::memory_order_relaxed);
  tls_slot->enter_ns.store(HookTelemetryNowNs(), std::memory_order_relaxed);
}

bool QueryRendererHookThreadTelemetry(uint32_t guest_tid,
                                      const char **last_symbol,
                                      uint64_t *us_since_enter) {
  if (guest_tid == 0u) {
    return false;
  }
  for (auto &slot : g_hook_thread_telemetry) {
    if (slot.tid.load(std::memory_order_relaxed) != guest_tid) {
      continue;
    }
    const char *symbol = slot.symbol.load(std::memory_order_relaxed);
    if (symbol == nullptr) {
      return false;
    }
    if (last_symbol != nullptr) {
      *last_symbol = symbol;
    }
    if (us_since_enter != nullptr) {
      const int64_t enter_ns = slot.enter_ns.load(std::memory_order_relaxed);
      const int64_t now_ns = HookTelemetryNowNs();
      *us_since_enter =
          now_ns > enter_ns ? static_cast<uint64_t>((now_ns - enter_ns) / 1000)
                            : 0u;
    }
    return true;
  }
  return false;
}

size_t SnapshotRendererHookThreadTelemetry(
    RendererHookThreadTelemetryEntry *out, size_t capacity) {
  if (out == nullptr || capacity == 0u) {
    return 0u;
  }
  const int64_t now_ns = HookTelemetryNowNs();
  size_t written = 0u;
  for (auto &slot : g_hook_thread_telemetry) {
    if (written >= capacity) {
      break;
    }
    const uint32_t tid = slot.tid.load(std::memory_order_relaxed);
    if (tid == 0u) {
      continue;  // Slots are claimed in order but never released.
    }
    const char *symbol = slot.symbol.load(std::memory_order_relaxed);
    if (symbol == nullptr) {
      continue;  // Claimed the slot but has not completed a hook enter yet.
    }
    const int64_t enter_ns = slot.enter_ns.load(std::memory_order_relaxed);
    out[written].guest_tid = tid;
    out[written].last_symbol = symbol;
    out[written].us_since_enter =
        now_ns > enter_ns ? static_cast<uint64_t>((now_ns - enter_ns) / 1000)
                          : 0u;
    ++written;
  }
  return written;
}

void OnRendererGuestHookEnter(const char *symbol, RendererHookKind kind,
                              const RendererHookArgs &args, uint8_t *base,
                              const FableHookSymbolDesc *desc) {
  NoteRendererHookThreadTelemetry(symbol);
  // FAST PATH (2026-07-30). For a symbol whose desc proves every enter-body
  // consumer inert under the shipping policy (see the eligibility proof in
  // ResolveFableHookSymbolDesc), do only what has a reader and return:
  // the call counters, the memory-base mirror, the GTL begin-pin drain
  // (contract: "this thread's NEXT hooked D3D call"), and the once-per-
  // (symbol, decision) trace log. Everything skipped — wrapper/segment
  // timers (8 steady_clock reads), wait-probe accounting, Tracy zone,
  // RenderDoc poll, backend-state/pump upkeep, panel probes, the observer —
  // is either provably a no-op for this symbol or serviced continuously by
  // the ineligible GTL-known roots (draw/present family) on the full path.
  if (desc != nullptr && desc->fast_path_eligible &&
      RendererHookWrapperFastPathModeActive()) {
    g_hook_fast_path_enter_calls.fetch_add(1, std::memory_order_relaxed);
    g_hook_enter_count.fetch_add(1, std::memory_order_relaxed);
    desc->hits.fetch_add(1, std::memory_order_relaxed);
    if (kind == RendererHookKind::kDraw) {
      g_draw_hook_enter_count.fetch_add(1, std::memory_order_relaxed);
    }
    RememberFableMemoryBase(base);
    refii::gtl::OnHookEnterUnknownSymbolFastPath(base);
    TraceHookDecisionCached(symbol, kind, desc);
    return;
  }
  const auto wrapper_t0 = std::chrono::steady_clock::now();
  struct WrapperTimer {
    std::chrono::steady_clock::time_point t0;
    ~WrapperTimer() {
      g_hook_wrapper_total_us.fetch_add(
          static_cast<uint64_t>(
              std::chrono::duration_cast<std::chrono::microseconds>(
                  std::chrono::steady_clock::now() - t0)
                  .count()),
          std::memory_order_relaxed);
      const uint64_t n =
          g_hook_wrapper_calls.fetch_add(1, std::memory_order_relaxed) + 1;
      if ((n & 0xFFFFu) == 0u) {
        REXLOG_INFO(
            "Refii hook wrapper census calls={} desc_hits={} scene_fast={} "
            "hookpath fast={} fast_exit={} full={} "
            "enter_suppressed_ms={} "
            "exit_ms={} seg[gtl={} stats={} misc={} probes={} allowed={} "
            "observe={} capture={}/{}c/full={}]",
            n, g_hook_desc_hits.load(std::memory_order_relaxed),
            g_scene_scope_fast_hits.load(std::memory_order_relaxed),
            g_hook_fast_path_enter_calls.load(std::memory_order_relaxed),
            g_hook_fast_path_exit_calls.load(std::memory_order_relaxed),
            n,
            g_hook_wrapper_total_us.load(std::memory_order_relaxed) / 1000u,
            g_hook_wrapper_exit_us.load(std::memory_order_relaxed) / 1000u,
            g_hook_enter_seg_gtl_us.load(std::memory_order_relaxed) / 1000u,
            g_hook_enter_seg_stats_us.load(std::memory_order_relaxed) / 1000u,
            g_hook_enter_seg_misc_us.load(std::memory_order_relaxed) / 1000u,
            g_hook_enter_seg_probes_us.load(std::memory_order_relaxed) / 1000u,
            g_hook_enter_seg_allowed_us.load(std::memory_order_relaxed) /
                1000u,
            g_hook_phase_observe_enter_us.load(std::memory_order_relaxed) /
                1000u,
            g_hook_args_capture_us.load(std::memory_order_relaxed) / 1000u,
            g_hook_args_capture_calls.load(std::memory_order_relaxed),
            g_hook_args_capture_full_calls.load(std::memory_order_relaxed));
        DumpRendererHookTargetCensus();
      }
    }
  } wrapper_timer{wrapper_t0};
  if (desc != nullptr) {
    g_hook_desc_hits.fetch_add(1, std::memory_order_relaxed);
  }
  // Symbol-identity fast compare: with a descriptor, every "is this symbol X"
  // question below is an integer compare against the resolved guest address
  // instead of a string_view compare. Queries for rex_* shim names pass
  // address 0 and keep the string compare (their GTL address is a mapped
  // constant or 0, so an address compare would be ambiguous).
  const uint32_t desc_address = desc != nullptr ? desc->address : 0u;
  const auto symbol_is = [&](uint32_t address, std::string_view name) {
    if (desc != nullptr && address != 0u && desc_address != 0u) {
      return desc_address == address;
    }
    return std::string_view(symbol) == name;
  };

  // Zone active-flag + text gated with the plots (2026-07-29): Tracy is
  // compiled in, so even an unconnected zone pair + strlen is real per-call
  // work at 5,790 hooked calls/frame.
  const bool hook_telemetry = RendererHookTelemetryEnabled();
  ZoneNamedN(tracy_hook_zone, "RefiiRenderer/GuestHookEnter", hook_telemetry);
  if (hook_telemetry) {
    ZoneTextV(tracy_hook_zone, symbol, std::strlen(symbol));
  }
  if (!RendererHooksEnabled()) {
    return;
  }
  if (!PlumeRendererBackendEnabled()) {
    // RenderDoc for the native/xenia backend: it renders through rexglue's
    // own Vulkan instance, bypassing Plume's arming (PrepareRenderDoc
    // VulkanLayer + InitializeRenderDocDebugger run only in Plume init), and
    // this native branch returns BEFORE the PollRenderDocCaptureRequest at
    // the bottom of the function — so xenia runs captured nothing (run 147).
    // Arm once on the first hook (before the guest's D3D CreateDevice ->
    // rexglue's vkCreateInstance, so the layer env is in time) and poll the
    // capture_request file every hook. Gated on the enable cvar; idempotent
    // with Plume since only one backend runs per process.
    if (RenderDocDebuggerEnabled()) {
      static std::once_flag native_renderdoc_arm_once;
      std::call_once(native_renderdoc_arm_once, [] {
        PrepareRenderDocVulkanLayer();
        InitializeRenderDocDebugger();
        REXLOG_INFO(
            "Refii RenderDoc armed for the native/xenia backend (layer + "
            "API); F12 and the capture_request file now work in xenia mode");
      });
      PollRenderDocCaptureRequest();
    }
    // Native-backend observation (renderer_observe_hooks_on_native) is a
    // traversal diagnostic: full observers are far too heavy at the native
    // backend's real call frequency (measured ~0.7 fps). Keep only atomic
    // per-target hit counters plus the scene-walk probe, and dump the
    // traversal counters from the walker so runs stay playable.
    if (desc != nullptr) {
      RecordRendererHookStatsForDesc(*desc, false);
    } else {
      RecordRendererHookStats(symbol, false);
    }
    if (symbol_is(0x821E1F40u, "sub_821E1F40")) {
      CountFableGuiPipelineStage(7);
      CountFableWorldCommandId(args.r4);
    }
    ObserveNativeBackendHookEnter(symbol, kind, args, base);
    return;
  }
  const auto seg_t0 = std::chrono::steady_clock::now();
  RememberFableMemoryBase(base);
  if (desc != nullptr) {
    refii::gtl::OnHookEnter(symbol, desc->address, args, base);
  } else {
    refii::gtl::OnHookEnter(symbol, args, base);
  }
  const auto seg_t1 = std::chrono::steady_clock::now();
  g_hook_enter_seg_gtl_us.fetch_add(
      std::chrono::duration_cast<std::chrono::microseconds>(seg_t1 - seg_t0)
          .count(),
      std::memory_order_relaxed);

  // DIAGNOSTIC 2026-07-28 (temporary; pairs with the GTL kickoff-exit probe).
  // Run 364: zero PM4 submits in the 107s title phase and the fence-retirement
  // log never printed, but every mention of these roots in the log is a
  // ONCE-PER-SYMBOL registration line, so call FREQUENCY is unknown. IDA order
  // inside D3DDevice_Swap is: KickOff (0x82BA3B28) -> BlockOnFence
  // (0x82BA3B64) -> swap-ack loop (0x82BA3B78, released ONLY by
  // D3D_SwapCallback, which we suppress via IsNativePresentationWorkHook).
  // This says which of those the guest actually reaches per phase.
  {
    if (desc != nullptr) {
      RendererWaitProbeNoteIndexed(desc->wait_probe_index, args,
                                   /*is_enter=*/true);
    } else {
      RendererWaitProbeNote(symbol, args, /*is_enter=*/true);
    }
  }

  // PER-CALL TELEMETRY, GATED (2026-07-28). Measured at ~37us per hooked call
  // across 2,424,832 calls = 43% of wall time. None of it is needed to render a
  // frame:
  //   ConfigureRendererTracyPlots + 4x TracyPlot (Tracy is COMPILED IN, so each
  //   plot does real work even with no profiler attached), two clock reads via
  //   PlotDeltaUs, and SetLastTimingText — a STRING COPY — on every call.
  //
  // Counters that other logic reads (hit totals, draw counts) stay unconditional
  // and are plain relaxed atomics. Only the profiler plumbing and the timing
  // text are gated.
  g_hook_enter_count.fetch_add(1, std::memory_order_relaxed);
  if (desc != nullptr) {
    RecordRendererHookStatsForDesc(*desc, false);
  } else {
    RecordRendererHookStats(symbol, false);
  }
  if (kind == RendererHookKind::kDraw) {
    g_draw_hook_enter_count.fetch_add(1, std::memory_order_relaxed);
  }
  if (hook_telemetry) {
    ConfigureRendererTracyPlots();
    const int64_t now_ns = RendererTraceNowNs();
    g_last_hook_gap_us.store(PlotDeltaUs("Refii Renderer Hook Gap Us",
                                         g_last_renderer_hook_enter_ns, now_ns),
                             std::memory_order_relaxed);
    SetLastTimingText(g_last_hook_symbol, sizeof(g_last_hook_symbol), symbol);
    TracyPlot("Refii Renderer Hook Kind", static_cast<int64_t>(kind));
    TracyPlot("Refii.RendererHookHits",
              static_cast<int64_t>(
                  g_renderer_hook_hit_total.load(std::memory_order_relaxed)));
    TracyPlot("Refii.RendererHookGuestPassthroughs",
              static_cast<int64_t>(g_renderer_hook_passthrough_total.load(
                  std::memory_order_relaxed)));
    if (kind == RendererHookKind::kDraw) {
      g_last_draw_hook_gap_us.store(
          PlotDeltaUs("Refii Renderer Draw Hook Gap Us",
                      g_last_draw_hook_enter_ns, now_ns),
          std::memory_order_relaxed);
    }
  }

  const auto seg_t2 = std::chrono::steady_clock::now();
  g_hook_enter_seg_stats_us.fetch_add(
      std::chrono::duration_cast<std::chrono::microseconds>(seg_t2 - seg_t1)
          .count(),
      std::memory_order_relaxed);
  MaybeLogRendererBackendState();
  MaybeLogRendererHookPolicy();
  // PUBLISH THE GUEST MEMORY BASE (2026-08-04).
  //
  // NoteFableGuestDisplayMemoryBase had ZERO CALLERS anywhere in the tree, so
  // g_guest_memory_base was permanently null and every consumer of
  // FableGuestMemoryBase() silently did nothing:
  //   * refii_gtl.cpp:5850 / :8117 / :8148 — the GTL's STATED-BASE content
  //     reads. This is the path that serves resources with no observable
  //     upload event (the tone curve is filled by raw CPU stores with no
  //     Lock/Unlock at all), so it was the one feed that could ever supply
  //     them, and it bailed to `no_header` on every call.
  //   * CurrentGuestDisplayPair() — could never read qword_834A5FE8 and always
  //     fell through to the stock 1280x720 constant. Benign only because the
  //     guest hardcodes exactly that; a resize or override would be invisible.
  // Measured, run 602: "STATED-BASE upload skipped ... (guest_base=null)".
  //
  // A relaxed atomic store of a pointer that never changes, on a path that
  // already does far more per call. Cheap enough not to need a gate.
  NoteFableGuestDisplayMemoryBase(base);
  // Run 415 seg census: this misc block cost ~7us/call (~27s of the run),
  // most of it the RenderDoc poll chain running GetRenderDocApi + the
  // completed-capture publish check on EVERY hooked call before the poller's
  // own 1-in-30 file-stat gate. Capture triggering tolerates ~100ms latency,
  // so poll from 1 in 64 hook enters (still ~90 polls/sec at title rates).
  {
    static std::atomic<uint32_t> renderdoc_poll_tick{0};
    if ((renderdoc_poll_tick.fetch_add(1, std::memory_order_relaxed) & 63u) ==
        0u) {
      PollRenderDocCaptureRequest();
    }
  }
  // Unconditional: the !PlumeRendererBackendEnabled() branch returned above.
  NotifyPlumeBackendHook(symbol, kind);
  const auto seg_t3 = std::chrono::steady_clock::now();
  g_hook_enter_seg_misc_us.fetch_add(
      std::chrono::duration_cast<std::chrono::microseconds>(seg_t3 - seg_t2)
          .count(),
      std::memory_order_relaxed);
  if (symbol_is(0x821E1F40u, "sub_821E1F40")) {
    CountFableGuiPipelineStage(7);
    CountFableWorldCommandId(args.r4);
  }
  if (symbol_is(0x8223CFA0u, "sub_8223CFA0")) {
    DumpRendererHookTraversalCounters();
  }
  // Scroll-cadence probe, PRE-policy-gate (sub_8220A3F0 is not a root-mode
  // target, so the observer never sees it): the storybook panel renderer
  // submits 4-vert stride-0x48 quads with scroll baked into vertex X.
  // Timestamps + first vertex separate game-side chunky animation from
  // renderer-side stale vertex uploads.
  // Inner-call timeline: where inside one panel render do the ~34ms go?
  // Tags the panel's hooked sub-calls with wall time so the gap's location
  // (inside a specific hook vs between guest calls) is readable from one
  // run.
  {
    // Window flag first: one relaxed load gates the six symbol_is compares
    // (and the probe runs only inside the panel-scroll window anyway). All
    // predicates are pure, so the reorder cannot change which calls log.
    if (g_panel_scroll_window_active.load(std::memory_order_relaxed) &&
        (symbol_is(0x8221E4A0u, "sub_8221E4A0") ||
         symbol_is(0x821B6EE0u, "sub_821B6EE0") ||
         symbol_is(0x8222C210u, "sub_8222C210") ||
         symbol_is(0x821E8C78u, "sub_821E8C78") ||
         symbol_is(0x821C3858u, "sub_821C3858") ||
         symbol_is(0x821EB130u, "sub_821EB130"))) {
      static std::atomic<uint32_t> logged_panel_inner_calls{0};
      const uint32_t inner = ++logged_panel_inner_calls;
      if (inner <= 4096u) {
        const auto now_us =
            std::chrono::duration_cast<std::chrono::microseconds>(
                std::chrono::steady_clock::now().time_since_epoch())
                .count();
        REXLOG_INFO("Refii renderer panel inner call #{} {} t_us={} lr=0x{:08X}",
                    inner, std::string_view(symbol),
                    static_cast<long long>(now_us), args.lr);
      }
    }
  }
  // Flag first for the same reason as above: symbol_is(0u, ...) is a string
  // compare on every full-path call (rex_* names carry no address).
  if (g_panel_scroll_window_active.load(std::memory_order_relaxed) &&
      symbol_is(0u, "rex_lhRenderer_RenderFrame")) {
    static std::atomic<uint32_t> logged_render_frame_marks{0};
    const uint32_t mark = ++logged_render_frame_marks;
    if (mark <= 512u) {
      const auto now_us = std::chrono::duration_cast<std::chrono::microseconds>(
                              std::chrono::steady_clock::now().time_since_epoch())
                              .count();
      REXLOG_INFO("Refii renderer guest RenderFrame mark #{} t_us={}", mark,
                  static_cast<long long>(now_us));
    }
  }
  // Billboard-page compositor signature (sub_82A7D1F0): 4-vert stride-0x14
  // quads drawing sources into the page RT. Logs correlate compose activity
  // with the still-blank page list. Remove when done.
  if (symbol_is(0x8220A3F0u, "sub_8220A3F0") && args.r3 == 4u &&
      args.r7 == 0x14u) {
    static std::atomic<uint32_t> logged_compose_draws{0};
    const uint32_t n = ++logged_compose_draws;
    if (n <= 256u) {
      REXLOG_INFO("Refii compositor draw #{} lr=0x{:08X}", n,
                  static_cast<uint32_t>(args.lr));
    }
  }
  if (symbol_is(0x8220A3F0u, "sub_8220A3F0") && args.r3 == 4u &&
      args.r7 == 0x48u && base != nullptr) {
    g_panel_scroll_window_active.store(true, std::memory_order_relaxed);
    static std::atomic<uint32_t> logged_panel_scroll_samples{0};
    const uint32_t sample = ++logged_panel_scroll_samples;
    // Contiguous window: reconstructing per-frame motion needs every submit
    // for a few seconds, not sparse power-of-two samples.
    if (sample <= 1024u) {
      const uint32_t x_bits = LoadGuestU32(base, args.r6);
      const uint32_t y_bits = LoadGuestU32(base, args.r6 + 4u);
      float x_value = 0.0f;
      float y_value = 0.0f;
      std::memcpy(&x_value, &x_bits, sizeof(x_value));
      std::memcpy(&y_value, &y_bits, sizeof(y_value));
      const auto now_us = std::chrono::duration_cast<std::chrono::microseconds>(
                              std::chrono::steady_clock::now().time_since_epoch())
                              .count();
      REXLOG_INFO(
          "Refii renderer panel scroll sample #{} t_us={} verts=0x{:08X} "
          "x={:.3f} y={:.3f} lr=0x{:08X}",
          sample, static_cast<long long>(now_us), args.r6, x_value, y_value,
          args.lr);
    }
  }
  // REVERTED 2026-07-28 — DO NOT GATE THE ENTER PATH.
  //
  // Applying the draw/resource observer cvars here (as the EXIT path does)
  // produced a BLACK SCREEN: nothing displayed at all, and the framerate did not
  // improve either. So `ObserveRendererHookEnter` is LOAD-BEARING for the
  // renderer in a way `ObserveRendererHookExit` is not — it is what feeds the
  // direct renderer its per-draw state, whereas the exit path was largely
  // redundant with the GTL.
  //
  // This asymmetry is the useful fact: the two halves are NOT interchangeable,
  // and "the observer costs 95%" does not license removing all of it. Any future
  // enter-path reduction has to be per-feed with its own falsifier, not a
  // wholesale gate.
  const auto seg_t4 = std::chrono::steady_clock::now();
  g_hook_enter_seg_probes_us.fetch_add(
      std::chrono::duration_cast<std::chrono::microseconds>(seg_t4 - seg_t3)
          .count(),
      std::memory_order_relaxed);
  const bool observer_allowed = FableRendererHookAllowedForDesc(*desc);
  const auto seg_t5 = std::chrono::steady_clock::now();
  g_hook_enter_seg_allowed_us.fetch_add(
      std::chrono::duration_cast<std::chrono::microseconds>(seg_t5 - seg_t4)
          .count(),
      std::memory_order_relaxed);
  if (observer_allowed) {
    const auto observe_enter_t0 = std::chrono::steady_clock::now();
    ObserveRendererHookEnter(symbol, kind, args, base, desc);
    const uint64_t observe_enter_us = static_cast<uint64_t>(
        std::chrono::duration_cast<std::chrono::microseconds>(
            std::chrono::steady_clock::now() - observe_enter_t0)
            .count());
    g_hook_phase_observe_enter_us.fetch_add(observe_enter_us,
                                            std::memory_order_relaxed);
    // Per-symbol attribution — this phase is 37.6s of the title frame and the
    // only existing per-symbol census is gated on a panel-scroll window.
    RecordObserveEnterCost(symbol, observe_enter_us);
  }
  TraceHookDecisionCached(symbol, kind, desc);
}

void OnRendererStateDispatchHook(const RendererHookArgs &args, uint8_t *base) {
  ZoneScopedN("RefiiRenderer/StateDispatchHook");
  if (!RendererHooksEnabled()) {
    return;
  }
  if (!PlumeRendererBackendEnabled()) {
    RecordRendererHookStats("sub_8221E550", false);
    return;
  }

  MaybeLogRendererBackendState();
  PollRenderDocCaptureRequest();
  if (PlumeRendererBackendEnabled()) {
    NotifyPlumeBackendHook("sub_8221E550", RendererHookKind::kDraw);
  }
  // GTL decodes the dispatch record itself, UNGATED by the observer policy.
  // This is the seam that turns "GTL owns submission" into "GTL owns
  // derivation": it was the only device-state feed with no self-read
  // equivalent, and therefore the only reason the observer had to stay running
  // in root-only mode for GTL packets to carry correct at-draw state.
  refii::gtl::OnStateDispatch(args, base);
  if (RendererStateDispatchObserverAllowed()) {
    ObserveRendererStateDispatch(args, base);
  }
  TraceHookDecision("sub_8221E550", RendererHookKind::kDraw);
}

void OnRendererPassQueueHook(const RendererHookArgs &args, uint8_t *base) {
  ZoneScopedN("RefiiRenderer/PassQueueHook");
  static const FableHookSymbolDesc *pass_queue_desc =
      ResolveFableHookSymbolDesc("sub_82B668D0", RendererHookKind::kDraw);
  OnRendererGuestHookEnter("sub_82B668D0", RendererHookKind::kDraw, args,
                           nullptr, pass_queue_desc);
  if (!RendererHooksEnabled()) {
    return;
  }

  if (PlumeRendererBackendEnabled() && RendererDeepObserverHooksEnabled()) {
    ObserveRendererPassQueue(args, base);
  }
}

void OnRendererPassQueueSnapshot(const RendererHookArgs &args, uint8_t *base) {
  ZoneScopedN("RefiiRenderer/PassQueueSnapshot");
  if (!RendererHooksEnabled()) {
    return;
  }

  if (PlumeRendererBackendEnabled() && RendererDeepObserverHooksEnabled()) {
    ObserveRendererPassQueue(args, base);
  }
}

void RepairFableSceneStreamPublishHandoffForWait(const char *reason,
                                                 uint8_t *base) {
  RepairFableSceneStreamPublishHandoff(
      reason != nullptr ? std::string_view(reason) : std::string_view{},
      base);
}

bool BridgeFableRendererGetTimeDeltaForPlume(const RendererHookArgs& args,
                                             PPCContext& ctx, uint8_t* base) {
  if (!PlumeRendererBackendEnabled()) {
    return false;
  }
  // OFF BY DEFAULT since 2026-07-28 — this bridge made a coherent guest
  // comparison incoherent, and the reason it existed is now fixed elsewhere.
  //
  // IDA (agent pass): the guest's ring gate at 0x822DFE80 compares
  //   dbl_8335F7D8[slot]  (producer stamp)   vs   f29  (consumer deadline)
  // and BOTH are seconds on the SAME epoch — the mftb value captured once in
  // LHTiming_Init (dbl_834970A0), divided by QueryPerformanceFrequency
  // (dbl_834970A8). The producer stamps via LHTiming_GetTime/sub_8221EA28
  // (0x8221EA7C); the consumer's deadline comes from sub_82B6FCE8, which is
  // mftb-at-last-GPU-completion (stored 0x82B901A8) run through the IDENTICAL
  // (x - dbl_834970A0)/dbl_834970A8 conversion (0x82B900E4/0x82B900EC) plus
  // (frames_in_flight + 1) display periods (0x82B6FD4C). The guest is coherent.
  //
  // Returning `1.0 + seconds_since_our_first_call` put the CONSUMER side on a
  // host wall-clock epoch while the PRODUCER side stayed on the guest's, so the
  // two epochs differed by however long elapsed between LHTiming_Init and our
  // first interception — making `stamp >= deadline` arbitrary.
  //
  // The bridge was added because the native clock was frozen: the GPU ring
  // callback Render_FrameCompletionCallback (0x82B90160) never fired, leaving
  // record[0] and the completion counter stale. The GTL now emulates that
  // callback directly in AdvanceGuestFrameCompletionLocked (called
  // UNCONDITIONALLY at Present exit) and writes the timestamp with
  // rex::chrono::Clock::QueryGuestTickCount() — the same source the recompiler
  // emits for `mftb`, so it lands in the guest's own units and epoch. Feeding
  // the native computation is therefore strictly better than replacing it.
  //
  // Set REFII_RENDERER_TIME_DELTA_BRIDGE=1 to restore the old substitution for
  // an A/B. If it is ever re-enabled permanently, it MUST be expressed in guest
  // units: (guest_ticks - dbl_834970A0) / dbl_834970A8, both read from guest
  // memory — never a host epoch.
  static const bool time_delta_bridge_enabled = [] {
    const char* value = std::getenv("REFII_RENDERER_TIME_DELTA_BRIDGE");
    return value != nullptr && value[0] != '\0' && value[0] != '0';
  }();
  if (!time_delta_bridge_enabled) {
    return false;
  }
  static const RendererTraceClock::time_point start_time =
      RendererTraceClock::now();
  const RendererTraceClock::time_point now = RendererTraceClock::now();
  const double elapsed_seconds =
      std::chrono::duration<double>(now - start_time).count();
  const double bridged_time = std::max(1.0 + elapsed_seconds, 1.0 / 60.0);
  ctx.f1.f64 = bridged_time;

  if (!g_logged_fable_time_delta_bridge) {
    REXLOG_INFO(
        "Refii renderer exact Fable GetTimeDelta clock bridge active "
        "result={:.9f} elapsed={:.9f} native=sub_82B6FCE8 "
        "snapshot_helper=sub_82B90048 caller=0x82388380 lr=0x{:08X} base={}",
        bridged_time, elapsed_seconds, args.lr, base != nullptr);
    g_logged_fable_time_delta_bridge = true;
  }
  return true;
}

void OnRendererGuestHookExit(const char *symbol, RendererHookKind kind,
                             const RendererHookArgs &args,
                             const PPCContext &ctx, uint8_t *base,
                             const FableHookSymbolDesc *desc) {
  // FAST PATH (2026-07-30), pair of the enter-side gate. For an eligible
  // symbol the ENTIRE exit body is provably inert: no wait probe
  // (wait_probe_index < 0 — and the probe's "count exits even when hooks are
  // off" rule is moot for a symbol that has no probe), stats reduce to a
  // no-op (target == nullptr), the exit-args capture feeds only
  // gtl::OnHookExit (whole address ladder no-ops — not a GTL constant) and
  // ObserveRendererHookExit (observer_allowed false under this gate, same as
  // the full path computes), and the RenderFrame/DisplaySwap/boundary arms
  // are excluded by eligibility or behind the deep-observer cvar this gate
  // requires OFF. So: count and return — this deletes two clock reads, a
  // full args re-capture (2 more clocks + 3 atomics), and ~10 predicate
  // calls per passthrough call.
  if (desc != nullptr && desc->fast_path_eligible &&
      RendererHookWrapperFastPathModeActive()) {
    g_hook_fast_path_exit_calls.fetch_add(1, std::memory_order_relaxed);
    return;
  }
  const uint32_t desc_address = desc != nullptr ? desc->address : 0u;
  const auto symbol_is = [&](uint32_t address, std::string_view name) {
    if (desc != nullptr && address != 0u && desc_address != 0u) {
      return desc_address == address;
    }
    return std::string_view(symbol) == name;
  };
  // THE LAST UNTIMED PATH. The wrapper census covers OnRendererGuestHookEnter
  // and OnRendererGuestHookSuppressedForPlume but never covered this one, so
  // for every hook that actually RUNS rather than being suppressed the exit
  // path — including ObserveRendererHookExit — has been outside every
  // measurement. The menu has ~71% of its frame unattributed and the guest's
  // own scene-stream wait measured 1ms per 65,536 calls, so this is the
  // remaining candidate.
  const auto wrapper_exit_t0 = std::chrono::steady_clock::now();
  struct WrapperExitTimer {
    std::chrono::steady_clock::time_point t0;
    ~WrapperExitTimer() {
      g_hook_wrapper_exit_us.fetch_add(
          static_cast<uint64_t>(
              std::chrono::duration_cast<std::chrono::microseconds>(
                  std::chrono::steady_clock::now() - t0)
                  .count()),
          std::memory_order_relaxed);
    }
  } wrapper_exit_timer{wrapper_exit_t0};

  // Zone gated like the enter path (2026-07-29): Tracy is compiled in, so an
  // unconnected zone pair + strlen is real per-call work on every passthrough
  // exit.
  const bool exit_hook_telemetry = RendererHookTelemetryEnabled();
  ZoneNamedN(tracy_hook_exit_zone, "RefiiRenderer/GuestHookExit",
             exit_hook_telemetry);
  if (exit_hook_telemetry) {
    ZoneTextV(tracy_hook_exit_zone, symbol, std::strlen(symbol));
  }
  // Wait-probe exit side. Deliberately BEFORE the early returns below: a wait
  // that returns must be counted even when hooks/Plume are disabled, otherwise
  // the imbalance reads as "parked" when it is only "not observed".
  if (desc != nullptr) {
    RendererWaitProbeNoteIndexed(desc->wait_probe_index, args,
                                 /*is_enter=*/false);
  } else {
    RendererWaitProbeNote(symbol, args, /*is_enter=*/false);
  }
  if (!RendererHooksEnabled()) {
    return;
  }
  if (!PlumeRendererBackendEnabled()) {
    return;
  }

  if (GetRendererBackend() == RendererBackend::kPlume) {
    if (desc != nullptr) {
      RecordRendererHookStatsForDesc(*desc, true);
    } else {
      RecordRendererHookStats(symbol, true);
    }
    if (exit_hook_telemetry) {
      TracyPlot("Refii.RendererHookGuestPassthroughs",
                static_cast<int64_t>(g_renderer_hook_passthrough_total.load(
                    std::memory_order_relaxed)));
    }
  }

  const RendererHookArgs exit_args = CaptureRendererHookArgsFor(desc, ctx);
  refii::gtl::OnHookExit(symbol, desc->address, exit_args);
  // REVERTED 2026-07-28 — DO NOT GATE THE OBSERVER ON THIS PATH.
  //
  // Applying the draw/resource observer gates here produced NO DISPLAY. The
  // suppressed path and this passthrough path are NOT equivalent:
  //
  //   OnRendererGuestHookSuppressedForPlume  -> gating the exit observer is safe
  //                                             (95% of wrapper cost removed,
  //                                              no visual change)
  //   OnRendererGuestHookExit (this path)    -> LOAD-BEARING. Gating blanks the
  //                                             screen.
  //
  // The difference: hooks reaching here EXECUTED their real guest body, so this
  // is the only place the observer sees the guest's actual post-call state.
  // Suppressed hooks never produced that state, which is why their exit
  // observation is redundant with the GTL and this one is not.
  //
  // Three observer paths, three different answers — enter (load-bearing),
  // suppressed exit (removable), passthrough exit (load-bearing). Do not
  // generalise from one to another again; each needs its own falsifier.
  const bool observer_allowed = FableRendererHookAllowedForDesc(*desc);
  if (observer_allowed) {
    ObserveRendererHookExit(symbol, kind, args, ctx.r3.u32, base, &exit_args,
                            desc);
  }
  // The remaining arms drop their PlumeRendererBackendEnabled() re-checks:
  // the !PlumeRendererBackendEnabled() branch returned above, and at -O0
  // each re-check was a real per-call function-call cost.
  if (kind == RendererHookKind::kDraw &&
      symbol_is(0u, "rex_lhRenderer_RenderFrame")) {
    static std::atomic<uint64_t> guest_frame_boundary_serial{0};
    FableDirectSealNonVideoGuestFrame(
        guest_frame_boundary_serial.fetch_add(1, std::memory_order_relaxed) +
        1u);
  }
  // Present-boundary seal, passthrough twin of the suppressed-path seal in
  // OnRendererGuestHookSuppressedForPlume (see the run-481 comment there).
  // rex_lhRenderer_EndScene is normally suppressed (IsNativePresentationWork-
  // Hook) and never reaches this ladder; if replace-present is off it passes
  // through here instead, and with the direct producer paths inactive the
  // seal is an empty-staged no-op. Same lock order: g_state.mutex released
  // by GetStatedPresentSource before the seal takes g_fable_direct.mutex.
  if (kind == RendererHookKind::kDraw &&
      symbol_is(0x82B6F1D0u, "rex_lhRenderer_EndScene")) {
    FableDirectSealNonVideoGuestFrame(
        refii::gtl::GetStatedPresentSource().guest_frame_boundary_packet_id);
  }
  if (kind == RendererHookKind::kDraw &&
      symbol_is(0x82BA34D8u, "sub_82BA34D8")) {
    CompletePlumeOwnedFableDisplaySwapBookkeeping(args, base);
    const int64_t now_ns = RendererTraceNowNs();
    ConfigureRendererTracyPlots();
    g_display_present_hook_count.fetch_add(1, std::memory_order_relaxed);
    g_last_display_present_gap_us.store(
        PlotDeltaUs("Refii Fable Present Hook Gap Us",
                    g_last_display_present_hook_ns, now_ns),
        std::memory_order_relaxed);
    SetLastTimingText(g_last_display_present_symbol,
                      sizeof(g_last_display_present_symbol), symbol);
    g_seen_plume_display_swap_hook.store(true, std::memory_order_relaxed);
  }
  if (kind == RendererHookKind::kDraw) {
    const bool draw_boundary = desc->deep_observer_draw_boundary &&
                               RendererDeepObserverHooksEnabled();
    if (draw_boundary) {
      CaptureFablePlumeCommandBufferBoundary(std::string_view(symbol));
    }
  }
  if (kind == RendererHookKind::kGeneral) {
    const std::string_view symbol_view(symbol);
    const bool stream_boundary = desc->scene_stream_boundary;
    const bool factory_boundary = desc->scene_factory_boundary;
    if (stream_boundary || factory_boundary) {
      if (stream_boundary) {
        RepairFableSceneStreamPublishHandoff(symbol_view, base);
      }
      // kGeneral deep-observer boundary == stream/factory boundary + the
      // deep-observer cvar (IsFableDeepObserverCommandBufferBoundary).
      if (RendererDeepObserverHooksEnabled()) {
        CaptureFablePlumeCommandBufferBoundary(symbol_view);
      }
    }
  }
}

bool ShouldRunGuestRendererHook(const char *symbol, RendererHookKind kind) {
  ZoneScopedN("RefiiRenderer/ShouldRunGuestHook");
  if (GetRendererBackend() != RendererBackend::kPlume) {
    return true;
  }
  if (kind != RendererHookKind::kDraw) {
    return !IsNativeRenderWorkHook(symbol);
  }

  return !IsNativeRenderWorkHook(symbol);
}

bool ShouldRunGuestRendererHook(const char *symbol, RendererHookKind kind,
                                const RendererHookArgs &args, uint8_t *base,
                                const FableHookSymbolDesc *desc) {
  // Desc fast path. The full args-dependent ladder below only concerns
  // kResource symbols and kGeneral shader-patch helpers; for every other
  // (symbol, kind) all its branch conditions are provably false, so the
  // decision reduces to backend + IsNativeRenderWorkHook — which the desc
  // answers without any string work. Suppression RESULTS are unchanged: the
  // desc mirrors the same predicates, and args/cvar-dependent decisions stay
  // on the string path.
  if (desc != nullptr && kind != RendererHookKind::kResource &&
      !(kind == RendererHookKind::kGeneral && desc->shader_patch_helper)) {
    if (GetRendererBackend() != RendererBackend::kPlume) {
      return true;
    }
    return !IsNativeRenderWorkHookForDesc(*desc);
  }
  ZoneScopedN("RefiiRenderer/ShouldRunGuestHookWithArgs");
  if (GetRendererBackend() != RendererBackend::kPlume) {
    return true;
  }
  if (kind == RendererHookKind::kResource &&
      CanEmulateFableDirectResourceHook(
          symbol, desc != nullptr ? desc->address : 0u, args, base)) {
    return false;
  }
  if (kind == RendererHookKind::kResource &&
      CanEmulateFableVertexShaderRegister(symbol, args, base)) {
    return false;
  }
  if (kind == RendererHookKind::kGeneral &&
      CanEmulateFableShaderPatchHelper(symbol, args, base)) {
    return false;
  }
  if (kind == RendererHookKind::kResource &&
      CanEmulateFableShaderUtility(symbol, args, base)) {
    return false;
  }
  if (kind == RendererHookKind::kResource &&
      CanEmulateFableRenderDepthSurfaceCtor(symbol, args, base)) {
    return false;
  }
  if (kind == RendererHookKind::kResource &&
      CanEmulateFableRenderTargetSetup(symbol, base)) {
    return false;
  }
  if (kind == RendererHookKind::kResource &&
      RendererReplaceResourcesEnabled() &&
      IsFableRenderDepthSurfaceCtorHook(symbol) &&
      !g_logged_fable_render_depth_surface_ctor_shape) {
    const bool shape_mem =
        base != nullptr && IsLikelyFableReadableRange(args.r4, 8u);
    const bool format_mem =
        base != nullptr && IsLikelyFableWritableDataRange(args.r5, 4u);
    REXLOG_WARN(
        "Refii renderer rejected unsupported Fable render/depth surface ctor "
        "replacement shape for Plume adapter: symbol={} object=0x{:08X} "
        "shape=0x{:08X} format_shape=0x{:08X} base={} object_mem={} "
        "shape_mem={} format_mem={} width={} height={} d3d_format=0x{:08X}",
        FableHookLogName(symbol), args.r3, args.r4, args.r5, base != nullptr,
        IsLikelyFableObjectRange(args.r3, 32u), shape_mem, format_mem,
        shape_mem ? LoadGuestU32(base, args.r4 + 0u) : 0u,
        shape_mem ? LoadGuestU32(base, args.r4 + 4u) : 0u,
        format_mem ? FableSurfaceWrapperD3DFormat(base, args.r5) : 0u);
    g_logged_fable_render_depth_surface_ctor_shape = true;
  }
  if (kind == RendererHookKind::kResource &&
      CanEmulateFableBinkVideoPlaneSetup(symbol, args, base)) {
    return false;
  }
  if (kind == RendererHookKind::kResource &&
      CanEmulateFableBinkVideoPlaneSurfaceSetup(symbol, base)) {
    return false;
  }
  if (kind == RendererHookKind::kResource &&
      RendererReplaceResourcesEnabled() &&
      IsFableBinkVideoPlaneSetupHook(symbol) &&
      !g_logged_fable_bink_video_plane_shape) {
    REXLOG_WARN(
        "Refii renderer suppressed unsupported Fable Bink video plane setup "
        "replacement shape: symbol={} movie=0x{:08X} "
        "size={}x{} plane=0x{:08X} output_slot=0x{:08X} base={} "
        "plane_mem={} output_mem={}",
        FableHookLogName(symbol), args.r3, args.r4, args.r5, args.r6, args.r7,
        base != nullptr, IsLikelyFableWritableDataRange(args.r6 + 4u, 8u),
        IsLikelyFableWritableDataRange(args.r7, 4u));
    g_logged_fable_bink_video_plane_shape = true;
  }
  if (kind == RendererHookKind::kResource &&
      CanEmulateFableSurfaceWrapperCreate(symbol, args, base)) {
    return false;
  }
  if (kind == RendererHookKind::kResource &&
      RendererReplaceResourcesEnabled() &&
      IsFableSurfaceWrapperCreateHook(symbol) &&
      !g_logged_fable_surface_wrapper_shape) {
    REXLOG_WARN(
        "Refii renderer rejected unsupported Fable surface wrapper create "
        "replacement shape for Plume adapter: symbol={} wrapper=0x{:08X} "
        "shape=0x{:08X} format_shape=0x{:08X} msaa=0x{:08X} "
        "tile_or_flags=0x{:08X} base={} wrapper_mem={} shape_mem={} "
        "format_mem={} d3d_format=0x{:08X}",
        FableHookLogName(symbol), args.r3, args.r4, args.r5, args.r6, args.r7,
        base != nullptr, IsLikelyFableWritableDataRange(args.r3 + 4u, 8u),
        IsLikelyFableReadableRange(args.r4, 8u),
        IsLikelyFableWritableDataRange(args.r5, 4u),
        FableSurfaceWrapperD3DFormat(base, args.r5));
    g_logged_fable_surface_wrapper_shape = true;
  }
  if (kind == RendererHookKind::kResource &&
      CanEmulateFableSurfaceViewCreate(symbol, args, base)) {
    return false;
  }
  if (RendererReplaceResourcesEnabled() &&
      ((kind == RendererHookKind::kResource &&
        IsMandatoryFableResourceReplacementHook(symbol)) ||
       (kind == RendererHookKind::kGeneral &&
        IsFableShaderPatchHelperHook(symbol)))) {
    const bool shape_supported =
        kind == RendererHookKind::kGeneral
            ? CanEmulateFableShaderPatchHelper(symbol, args, base)
            : CanEmulateMandatoryFableResourceReplacementHook(symbol, args,
                                                              base);
    if (!shape_supported) {
      LogFableUnsupportedAdapterShape(symbol, args, base);
    }
    return false;
  }

  const bool run_guest = ShouldRunGuestRendererHook(symbol, kind);
  if (run_guest && RendererReplaceResourcesEnabled() &&
      (kind == RendererHookKind::kResource ||
       (kind == RendererHookKind::kGeneral &&
        IsFableShaderPatchHelperHook(symbol)))) {
    LogFableUnsupportedAdapterShape(symbol, args, base);
  }

  return run_guest;
}

void OnRendererGuestHookSuppressedForPlume(
    const char *symbol, RendererHookKind kind, const RendererHookArgs &args,
    PPCContext &ctx, uint8_t *base, const FableHookSymbolDesc *desc) {
  const auto wrapper_t0 = std::chrono::steady_clock::now();
  struct WrapperTimer {
    std::chrono::steady_clock::time_point t0;
    ~WrapperTimer() {
      g_hook_wrapper_total_us.fetch_add(
          static_cast<uint64_t>(
              std::chrono::duration_cast<std::chrono::microseconds>(
                  std::chrono::steady_clock::now() - t0)
                  .count()),
          std::memory_order_relaxed);
      const uint64_t n =
          g_hook_wrapper_calls.fetch_add(1, std::memory_order_relaxed) + 1;
      if ((n & 0xFFFFu) == 0u) {
        REXLOG_INFO(
            "Refii hook wrapper census calls={} desc_hits={} scene_fast={} "
            "hookpath fast={} fast_exit={} full={} "
            "enter_suppressed_ms={} "
            "exit_ms={} seg[gtl={} stats={} misc={} probes={} allowed={} "
            "observe={} capture={}/{}c/full={}]",
            n, g_hook_desc_hits.load(std::memory_order_relaxed),
            g_scene_scope_fast_hits.load(std::memory_order_relaxed),
            g_hook_fast_path_enter_calls.load(std::memory_order_relaxed),
            g_hook_fast_path_exit_calls.load(std::memory_order_relaxed),
            n,
            g_hook_wrapper_total_us.load(std::memory_order_relaxed) / 1000u,
            g_hook_wrapper_exit_us.load(std::memory_order_relaxed) / 1000u,
            g_hook_enter_seg_gtl_us.load(std::memory_order_relaxed) / 1000u,
            g_hook_enter_seg_stats_us.load(std::memory_order_relaxed) / 1000u,
            g_hook_enter_seg_misc_us.load(std::memory_order_relaxed) / 1000u,
            g_hook_enter_seg_probes_us.load(std::memory_order_relaxed) / 1000u,
            g_hook_enter_seg_allowed_us.load(std::memory_order_relaxed) /
                1000u,
            g_hook_phase_observe_enter_us.load(std::memory_order_relaxed) /
                1000u,
            g_hook_args_capture_us.load(std::memory_order_relaxed) / 1000u,
            g_hook_args_capture_calls.load(std::memory_order_relaxed),
            g_hook_args_capture_full_calls.load(std::memory_order_relaxed));
        DumpRendererHookTargetCensus();
      }
    }
  } wrapper_timer{wrapper_t0};
  // The census counts a suppressed call in BOTH wrapper timers (enter +
  // here), so desc_hits must be bumped on both paths for calls/desc_hits to
  // be comparable (run 429 read 47% partly because of this asymmetry).
  if (desc != nullptr) {
    g_hook_desc_hits.fetch_add(1, std::memory_order_relaxed);
  }
  const uint32_t desc_address = desc != nullptr ? desc->address : 0u;
  const auto symbol_is = [&](uint32_t address, std::string_view name) {
    if (desc != nullptr && address != 0u && desc_address != 0u) {
      return desc_address == address;
    }
    return std::string_view(symbol) == name;
  };

  // NO gtl::OnHookEnter HERE. The GTL does see every wrapper-root call,
  // suppressed or passthrough (design doc §3.1) — but it already got this one:
  // every caller of this function invokes OnRendererGuestHookEnter FIRST and
  // only then branches to run-guest / suppress, and OnRendererGuestHookEnter
  // calls gtl::OnHookEnter unconditionally under Plume (see its body).
  //
  // BUG FIXED 2026-07-17: this line delivered a SECOND enter for every
  // suppressed root — which is most of the GTL's surface under Plume. Effects:
  //   * t_draw_indexed_up_depth / t_begin_vertices_depth incremented twice per
  //     call but decremented once, so after the first call on a thread they
  //     never returned to 0 again: Begin-family packets took the "inner
  //     allocation" branch forever and geometry pins stopped firing.
  //   * Draw/Resolve/Clear packets were built TWICE, and the pump consumes them
  //     for real — suppressed draws were submitted twice.
  //   * resources_registered and address_generation double-counted, which is
  //     why "created=3438 vs 60 live entries" overstated the eviction.
  // Verified: all ~30 call sites of this function are preceded by
  // OnRendererGuestHookEnter, except REX_EXTERN(sub_8221E550) and
  // REX_EXTERN(sub_82B668D0) in d3d_pass_through.cpp — and neither address is a
  // GTL root, so the GTL's switch has no case for them and the enter was a
  // no-op there anyway.
  // Per-symbol cost census for the panel window (title scroll campaign;
  // remove when done): accumulates total handling time of every plume-owned
  // hook on any thread while panels are submitting, dumped every 8192 calls.
  const bool census_this_hook =
      g_panel_scroll_window_active.load(std::memory_order_relaxed);
  const int64_t hook_enter_us =
      census_this_hook
          ? std::chrono::duration_cast<std::chrono::microseconds>(
                std::chrono::steady_clock::now().time_since_epoch())
                .count()
          : 0;
  struct HookDurationLogger {
    bool active = false;
    const char *symbol = nullptr;
    int64_t enter_us = 0;
    ~HookDurationLogger() {
      if (!active) {
        return;
      }
      const int64_t exit_us =
          std::chrono::duration_cast<std::chrono::microseconds>(
              std::chrono::steady_clock::now().time_since_epoch())
              .count();
      static std::mutex census_mutex;
      static std::unordered_map<std::string_view,
                                std::pair<uint64_t, uint64_t>>
          census;
      static uint32_t census_calls = 0;
      std::lock_guard<std::mutex> lock(census_mutex);
      auto &entry = census[symbol];
      ++entry.first;
      entry.second += static_cast<uint64_t>(exit_us - enter_us);
      if ((++census_calls & 8191u) == 0) {
        std::vector<std::pair<std::string_view, std::pair<uint64_t, uint64_t>>>
            rows(census.begin(), census.end());
        std::sort(rows.begin(), rows.end(), [](const auto &a, const auto &b) {
          return a.second.second > b.second.second;
        });
        for (size_t i = 0; i < rows.size() && i < 12; ++i) {
          REXLOG_INFO("Refii hook census {} calls={} total_us={}",
                      rows[i].first, rows[i].second.first,
                      rows[i].second.second);
        }
        // Release cost split + cut-execution proof (run 465 follow-up: body
        // cuts landed but Release rose 167.7 -> 178.6us/call while sibling
        // cuts proved out — this line says whether the wall is mutex WAIT or
        // under-lock body, and whether each cut actually runs). Cumulative
        // run totals, same cadence as the rows above.
        {
          const ResourceReleaseHookCostStats release_stats =
              GetResourceReleaseHookCostStats();
          REXLOG_INFO(
              "Refii hook census sub_821FC320 detail locked_calls={} "
              "observer_wait_us={} registry_wait_us={} body_us={} "
              "exit_early_outs={} dispatch_hits={} payload_strips={} "
              "keyed_invalidations={} iter_miss_hits={} "
              "teardowns_inline={} teardowns_deferred={} drains={} "
              "drain_depth_sum={} drain_depth_max={} gate_skips={} "
              "gate_hazard_waits={}",
              release_stats.locked_calls, release_stats.observer_wait_us,
              release_stats.registry_wait_us, release_stats.body_us,
              release_stats.exit_early_outs,
              g_release_dispatch_hits.load(std::memory_order_relaxed),
              release_stats.payload_strips, release_stats.keyed_invalidations,
              release_stats.iterator_pure_miss_hits,
              release_stats.teardowns_inline, release_stats.teardowns_deferred,
              release_stats.teardown_drains,
              release_stats.teardown_drain_depth_sum,
              release_stats.teardown_drain_depth_max,
              release_stats.teardown_gate_skips,
              release_stats.teardown_gate_hazard_waits);
        }
        REXLOG_INFO(
            "Refii hook phase census enter_observe_ms={} submit_ms={} "
            "resource_ms={} observe_ms={} gtlexit_ms={}",
            g_hook_phase_observe_enter_us.load(std::memory_order_relaxed) /
                1000u,
            g_hook_phase_submit_us.load(std::memory_order_relaxed) / 1000u,
            g_hook_phase_resource_us.load(std::memory_order_relaxed) / 1000u,
            g_hook_phase_observe_us.load(std::memory_order_relaxed) / 1000u,
            g_hook_phase_gtlexit_us.load(std::memory_order_relaxed) / 1000u);
      }
    }
  } duration_logger{census_this_hook, symbol, hook_enter_us};
  ZoneNamedN(tracy_suppressed_zone,
             "RefiiRenderer/GuestHookSuppressedForPlume", true);
  ZoneTextV(tracy_suppressed_zone, symbol, std::strlen(symbol));
  g_guest_suppressed_hook_count.fetch_add(1, std::memory_order_relaxed);
  SetLastTimingText(g_last_guest_suppressed_symbol,
                    sizeof(g_last_guest_suppressed_symbol), symbol);
  const uint32_t previous_result = ctx.r3.u32;
  const auto phase_t0 = std::chrono::steady_clock::now();
  const FableGuestCommandSubmitEmulation command_submit_emulation =
      EmulateFableGuestCommandSubmission(symbol, args, base, desc);
  const auto phase_submit_t1 = std::chrono::steady_clock::now();
  if (census_this_hook) {
    g_hook_phase_submit_us.fetch_add(
        static_cast<uint64_t>(
            std::chrono::duration_cast<std::chrono::microseconds>(
                phase_submit_t1 - phase_t0)
                .count()),
        std::memory_order_relaxed);
  }
  const bool shader_patch_helper_hook = desc->shader_patch_helper;
  const FableGuestResourceEmulation resource_emulation =
      (kind == RendererHookKind::kResource ||
       (kind == RendererHookKind::kGeneral && shader_patch_helper_hook))
          ? EmulateFableGuestResourceHook(symbol, args, ctx, base,
                                          desc_address)
          : FableGuestResourceEmulation{};
  const auto phase_resource_t1 = std::chrono::steady_clock::now();
  if (census_this_hook) {
    g_hook_phase_resource_us.fetch_add(
        static_cast<uint64_t>(
            std::chrono::duration_cast<std::chrono::microseconds>(
                phase_resource_t1 - phase_submit_t1)
                .count()),
        std::memory_order_relaxed);
  }
  const bool preserve_previous_result = desc->exact_stub;
  const uint32_t observer_result =
      command_submit_emulation.handled
          ? static_cast<uint32_t>(command_submit_emulation.result)
          : resource_emulation.handled
          ? static_cast<uint32_t>(resource_emulation.result)
          : previous_result;
  const bool get_render_target_hook = desc->get_render_target_hook;
  const bool shader_constant_replacement_hook =
      desc->shader_constant_replacement;
  if (kind == RendererHookKind::kDraw ||
      (kind == RendererHookKind::kResource && resource_emulation.handled) ||
      (kind == RendererHookKind::kGeneral && resource_emulation.handled &&
       shader_patch_helper_hook) ||
      (kind == RendererHookKind::kGeneral && command_submit_emulation.handled &&
       get_render_target_hook) ||
      (kind == RendererHookKind::kGeneral &&
       shader_constant_replacement_hook)) {
    // A/B gate: the observer's draw-kind exit measured 95% of ALL hook-wrapper
    // cost (observe_ms=20871 vs submit=360 / resource=841 / gtlexit=8) on a run
    // where the guest collapsed to 0.3 frames/sec. The hook policy is already at
    // its minimum (root_only=true, deep_observer=false), so this is the
    // load-bearing root path — not optional proof hooks — which is why it is a
    // measured switch and not a deletion.
    // RESOURCE-LIFETIME EXEMPTION — added 2026-07-28 after the releasezero
    // census came back all-zero on run 391.
    //
    // D3DResource_Release (sub_821FC320) and D3DResource_AddRef (sub_821FC178)
    // are SUPPRESSED under Plume (EmulateFableResourceLifetime owns the
    // refcount), so they reach the observer only through this path. Turning
    // renderer_observer_resource_exit off earlier today therefore did not just
    // skip a texture-rebase cost — it silently switched off the ONLY retirement
    // path for the resource table:
    //
    //   Release exit -> hook_observer sub_821FC320 handler (result == 0)
    //     -> FableDirectGuestResourceRegistry().resources.erase
    //     -> MarkResourceDestroyedLocked
    //     -> fable_direct_renderer.cpp:53177 g_direct_renderer.resources.erase
    //
    // With the gate off, nothing outside the scratch-only LRU at
    // hook_observer.cpp:17008 ever erases a resource, so the table grows for
    // the life of the run and every alias-lookup miss scans a bigger table.
    // That is the title's degradation curve (742 -> 1650 ms/frame, run 390).
    //
    // IDA confirms the signal is exact, not heuristic: 0x821FC320 decrements
    // with lwarx/stwcx., and on reaching zero calls the destructor
    // sub_821F65C0(pThis) and `return 0`. result == 0 IS destruction.
    //
    // The cvar keeps doing its job for every other resource root (the 2.38ms
    // rex_XGOffsetBaseTextureAddress case that motivated it); it must never
    // gate lifetime.
    //
    // RELEASE ONLY — NOT AddRef. Exempting both cost 22.5us/call on AddRef
    // (6.4 -> 28.9us, run 391 vs 393) for a handler that does nothing we need:
    // SetGuestResourceRawRefCount writes record.raw.ref_count, which has NO
    // READER anywhere in the tree, and StoreLastResourceLocked deep-copies the
    // whole RendererResourceRecord into `last_resource`. 38,329 calls per
    // census window paying two contended locks for that. Retirement is driven
    // entirely by Release reaching refcount zero, so AddRef stays gated — which
    // is exactly its run-391 behaviour, and run 391 had no visual regression.
    const bool resource_lifetime_exempt =
        kind == RendererHookKind::kResource &&
        symbol_is(0x821FC320u, "sub_821FC320");
    // BINK PLANE SETUP EXEMPTION — added 2026-07-29 (run 427 green movie).
    //
    // Fable::BinkVideoPlaneSetup (sub_82A42FA0) is a kResource hook whose
    // guest body is Plume-emulated (resource-replaced), so it only ever
    // reaches the observer through THIS suppressed path. Its observer exit is
    // not instrumentation: ObserveBinkVideoPlaneSetupLocked is the ONLY
    // writer of g_observer.bink_planes_by_surface, and every per-frame Bink
    // plane content publish (RefreshBinkVideoPlaneUploadsLocked, triggered
    // unconditionally from the sub_832BCB30 decode wrapper) returns
    // immediately while that map is empty. With the kind-wide resource-exit
    // gate off, plane registration never happened: run 427 shows all six
    // plane records stuck at channel=0/gen=1 with one blank frame-860
    // capture (5 of 6 uploads hash=0x1), i.e. Y=Cb=Cr=0 sampled for the
    // whole movie — the persistent GREEN video. Same shape as the Release
    // retirement exemption above: the gate keeps its perf job for every
    // other resource root; it must never gate load-bearing registration.
    const bool bink_plane_setup_exempt =
        kind == RendererHookKind::kResource &&
        symbol_is(0x82A42FA0u, "sub_82A42FA0");
    const bool run_observer_exit =
        (kind != RendererHookKind::kDraw ||
         RendererObserverDrawExitEnabled()) &&
        (kind != RendererHookKind::kResource || resource_lifetime_exempt ||
         bink_plane_setup_exempt ||
         RendererObserverResourceExitEnabled());
    if (run_observer_exit && FableRendererHookAllowedForDesc(*desc)) {
      const auto observe_t0 = std::chrono::steady_clock::now();
      ObserveRendererHookExit(symbol, kind, args, observer_result, base,
                              nullptr, desc);
      if (census_this_hook) {
        g_hook_phase_observe_us.fetch_add(
            static_cast<uint64_t>(
                std::chrono::duration_cast<std::chrono::microseconds>(
                    std::chrono::steady_clock::now() - observe_t0)
                    .count()),
            std::memory_order_relaxed);
      }
    }
  }
  if (command_submit_emulation.handled) {
    CaptureFablePlumeCommandBufferBoundary(symbol);
  }

  if (command_submit_emulation.handled) {
    ctx.r3.u64 = command_submit_emulation.result;
  } else if (resource_emulation.handled) {
    ctx.r3.u64 = resource_emulation.result;
  } else if (preserve_previous_result) {
    ctx.r3.u64 = previous_result;
  } else {
    ctx.r3.u64 = 0;
  }

  // Suppressed roots have no passthrough exit, but the GTL's exit pairing
  // (create results, Begin-family ring pointers) needs the POST-emulation
  // result register — the emulated ring pointer for BeginVertices lives in
  // ctx.r3 right here and nowhere else (run 119: pins never fired because
  // the draw-hook macro's suppressed branch skips the exit callback).
  {
    const RendererHookArgs suppressed_exit_args =
        CaptureRendererHookArgsFor(desc, ctx);
    // Wait-probe exit for SUPPRESSED roots. KickOff and KickOffSegment are
    // suppressed (IsFableGuestCommandSubmissionHook), so they never reach
    // OnRendererGuestHookExit. Without this they would show a permanent
    // enter>exit imbalance and read as "parked" when they in fact returned
    // normally through the replacement path.
    RendererWaitProbeNoteIndexed(desc->wait_probe_index, suppressed_exit_args,
                                 /*is_enter=*/false);
    const auto gtlexit_t0 = std::chrono::steady_clock::now();
    refii::gtl::OnHookExit(symbol, desc->address, suppressed_exit_args);
    if (census_this_hook) {
      g_hook_phase_gtlexit_us.fetch_add(
          static_cast<uint64_t>(
              std::chrono::duration_cast<std::chrono::microseconds>(
                  std::chrono::steady_clock::now() - gtlexit_t0)
                  .count()),
          std::memory_order_relaxed);
    }
  }

  if (RendererHookTracingEnabled() &&
      g_logged_guest_hook_suppressed.insert(symbol).second) {
    const char *hook_result_mode =
        command_submit_emulation.handled
            ? "command-replaced"
            : resource_emulation.handled ? "resource-replaced"
            : preserve_previous_result ? "preserve-previous-result"
                                       : "zero-result";
    REXLOG_INFO(
        "Refii renderer hook {} uses Plume-owned replacement path "
        "reason=plume-owned-hook-replacement kind={} command_handled={} "
        "resource_handled={} hook_result_mode={} result=0x{:08X}",
        FableHookLogName(symbol), static_cast<int>(kind),
        command_submit_emulation.handled, resource_emulation.handled,
        hook_result_mode, ctx.r3.u32);
  }

  if (kind == RendererHookKind::kDraw) {
    const std::string_view symbol_view(symbol);
    // rex_lhRenderer_EndScene resolves to the GTL's kPresent address
    // (0x82B6F1D0, D3D_Present), so the desc path compares that.
    const bool is_end_scene_present =
        desc != nullptr ? desc_address == 0x82B6F1D0u
                        : symbol_view == "rex_lhRenderer_EndScene";
    const bool is_display_swap = symbol_is(0x82BA34D8u, "sub_82BA34D8");
    const bool is_complex_swap = symbol_is(0x82BA32F8u, "sub_82BA32F8");
    const bool is_native_present = is_display_swap || is_complex_swap;
    const bool is_present_boundary = is_end_scene_present || is_display_swap;
    // GUEST-FRAME SEAL AT THE PRESENT BOUNDARY (run 481). Backlog-supersession
    // generations were minted only by the rex_lhRenderer_RenderFrame kDraw
    // exit, which the title attract never calls — the record thread then
    // consumed a multi-frame backlog as ONE generation and supersession never
    // engaged (frames_superseded=0 forever against 700-1000ms composes). The
    // proven title-valid boundary is THIS hook: gtl::OnHookEnter's kPresent
    // case advanced guest_frame_boundary_packet_id on this very call (guest
    // thread, run 480: kPlumeRecordWhyBoundary fired 426 times at title), and
    // the gtl::OnHookExit above ran AdvanceGuestFrameCompletionLocked. Seal
    // once per guest present. Double-seal with the RenderFrame exit is benign
    // and ordered: Present is called INSIDE RenderFrame (0x8238866C,
    // IDA-proven), so this seal runs first and the RenderFrame-exit seal
    // reduces to an empty-staged no-op / post-present tail catcher.
    // Lock order: GetStatedPresentSource takes and RELEASES g_state.mutex
    // before the seal takes g_fable_direct.mutex; no registry/observer mutex
    // is held here. Immediate producer paths (PIX/Bink) are untouched and
    // stay generation-0 (run-475 lesson).
    if (is_end_scene_present) {
      FableDirectSealNonVideoGuestFrame(
          refii::gtl::GetStatedPresentSource().guest_frame_boundary_packet_id);
    }
    if (is_end_scene_present || is_native_present) {
      const int64_t now_ns = RendererTraceNowNs();
      ConfigureRendererTracyPlots();
      if (is_display_swap && !command_submit_emulation.handled) {
        EmulatePlumeOwnedFableDisplaySwapBookkeeping(symbol_view, args, base);
      }
      g_display_present_hook_count.fetch_add(1, std::memory_order_relaxed);
      if (is_display_swap) {
        g_last_display_present_gap_us.store(
            PlotDeltaUs("Refii Fable Present Hook Gap Us",
                        g_last_display_present_hook_ns, now_ns),
            std::memory_order_relaxed);
      }
      SetLastTimingText(g_last_display_present_symbol,
                        sizeof(g_last_display_present_symbol), symbol);
    }
    if (!is_present_boundary) {
      return;
    }

    if (is_display_swap) {
      g_seen_plume_display_swap_hook.store(true, std::memory_order_relaxed);
    }
    if (RendererPlumePresentPumpEnabled()) {
      return;
    }
    const char *present_reason =
        is_end_scene_present ? "end-scene" : "display-swap";
    const bool presented = PresentPlumeFrameFromRendererHook(symbol,
                                                             present_reason);
    if (presented &&
        ((is_display_swap && !g_logged_plume_present_driver) ||
         (is_end_scene_present && !g_logged_plume_end_scene_present_driver))) {
      if (is_display_swap) {
        REXLOG_INFO(
            "Refii plume output is presenting from Plume-owned Fable display "
            "swap hook {}",
            symbol);
        g_logged_plume_present_driver = true;
      } else if (is_end_scene_present &&
                 !g_logged_plume_end_scene_present_driver) {
        REXLOG_INFO(
            "Refii plume output is presenting from Plume-owned Fable EndScene "
            "hook {}",
            symbol);
        g_logged_plume_end_scene_present_driver = true;
      }
    } else if (!presented && !g_logged_plume_present_failed) {
      REXLOG_WARN("Refii plume output could not present from Fable {} hook {}",
                  present_reason, symbol);
      g_logged_plume_present_failed = true;
    }
  }
}


} // namespace refii::renderer
