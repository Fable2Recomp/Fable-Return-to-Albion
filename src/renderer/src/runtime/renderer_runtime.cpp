#include "renderer_runtime.h"

#include "renderdoc_debugger.h"
#include "runtime/cvars.h"
#include "runtime/hook_observer.h"
#include "runtime/plume_backend.h"

#include <rex/logging.h>
#include <rex/ppc/context.h>
#include <tracy/Tracy.hpp>

#include <algorithm>
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstdint>
#include <cstdlib>
#include <cstring>
#include <mutex>
#include <string>
#include <string_view>
#include <thread>
#include <unordered_set>

namespace refii::renderer {
namespace {

bool g_logged_plume_unavailable = false;
bool g_logged_plume_observer = false;
bool g_logged_plume_present_driver = false;
bool g_logged_plume_bootstrap_present_driver = false;
bool g_logged_plume_present_pump_driver = false;
bool g_logged_plume_present_failed = false;
bool g_logged_fable_present_bookkeeping = false;
bool g_logged_plume_pump_fable_bookkeeping = false;
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
std::atomic<uint64_t> g_draw_hook_enter_count{0};
std::atomic<uint64_t> g_display_present_hook_count{0};
std::atomic<uint64_t> g_plume_present_request_count{0};
std::atomic<uint64_t> g_plume_present_pump_request_count{0};
std::atomic<uint64_t> g_plume_present_pump_fable_advance_count{0};
std::atomic<uint64_t> g_native_skipped_hook_count{0};
std::atomic<int64_t> g_last_hook_gap_us{0};
std::atomic<int64_t> g_last_draw_hook_gap_us{0};
std::atomic<int64_t> g_last_display_present_gap_us{0};
std::atomic<int64_t> g_last_plume_present_request_gap_us{0};
std::atomic<int64_t> g_last_plume_present_pump_gap_us{0};
std::atomic<uint32_t> g_last_fable_device{0};
std::atomic<uintptr_t> g_last_fable_memory_base{0};
std::atomic<uint32_t> g_last_fable_present_submitted{0};
std::atomic<uint32_t> g_last_fable_present_completed{0};
std::atomic<uint32_t> g_last_fable_swap_callbacks{0};
std::atomic<uint32_t> g_last_fable_present_pending{0};
std::mutex g_hook_timing_mutex;
std::mutex g_present_pump_mutex;
std::condition_variable g_present_pump_cv;
std::thread g_present_pump_thread;
std::once_flag g_present_pump_atexit_once;
char g_last_hook_symbol[64] = {};
char g_last_display_present_symbol[64] = {};
char g_last_plume_present_symbol[64] = {};
char g_last_plume_present_reason[32] = {};
char g_last_native_skipped_symbol[64] = {};
std::unordered_set<std::string> g_logged_hook_fallbacks;
std::unordered_set<std::string> g_logged_hook_suppressed;

using RendererTraceClock = std::chrono::steady_clock;

int64_t RendererTraceNowNs() {
  return std::chrono::duration_cast<std::chrono::nanoseconds>(
             RendererTraceClock::now().time_since_epoch())
      .count();
}

void ConfigureRendererTracyPlots() {
  static std::once_flag configure_once;
  std::call_once(configure_once, [] {
    TracyPlotConfig("Refii Renderer Hook Gap Us",
                    tracy::PlotFormatType::Number,
                    false,
                    false,
                    0x38BDF8);
    TracyPlotConfig("Refii Renderer Draw Hook Gap Us",
                    tracy::PlotFormatType::Number,
                    false,
                    false,
                    0x22C55E);
    TracyPlotConfig("Refii Fable Present Hook Gap Us",
                    tracy::PlotFormatType::Number,
                    false,
                    false,
                    0xF59E0B);
    TracyPlotConfig("Refii Plume Present Request Gap Us",
                    tracy::PlotFormatType::Number,
                    false,
                    false,
                    0xA855F7);
    TracyPlotConfig("Refii Plume Present Pump Gap Us",
                    tracy::PlotFormatType::Number,
                    false,
                    false,
                    0x14B8A6);
    TracyPlotConfig("Refii Plume Present Result",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0x84CC16);
    TracyPlotConfig("Refii Renderer Hook Kind",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0x60A5FA);
    TracyPlotConfig("Refii Fable Presents Pending",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0xFB7185);
  });
}

void CopyTimingText(char* destination, size_t destination_size,
                    std::string_view text) {
  if (destination_size == 0) {
    return;
  }
  const size_t copy_size = std::min(destination_size - 1, text.size());
  std::memcpy(destination, text.data(), copy_size);
  destination[copy_size] = '\0';
}

void SetLastTimingText(char* destination, size_t destination_size,
                       std::string_view text) {
  std::lock_guard lock(g_hook_timing_mutex);
  CopyTimingText(destination, destination_size, text);
}

int64_t PlotDeltaUs(const char* name, std::atomic<int64_t>& previous_ns,
                    int64_t now_ns) {
  const int64_t prior_ns = previous_ns.exchange(now_ns, std::memory_order_relaxed);
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

uint32_t GuestPhysicalHostOffset(uint32_t guest_address) {
#if defined(_WIN32)
  return guest_address >= 0xE0000000u ? 0x1000u : 0u;
#else
  return 0;
#endif
}

uint32_t LoadGuestU32(uint8_t* base, uint32_t guest_address) {
  uint32_t raw = 0;
  std::memcpy(&raw,
              base + guest_address + GuestPhysicalHostOffset(guest_address),
              sizeof(raw));
  return __builtin_bswap32(raw);
}

void StoreGuestU32(uint8_t* base, uint32_t guest_address, uint32_t value) {
  const uint32_t raw = __builtin_bswap32(value);
  std::memcpy(base + guest_address + GuestPhysicalHostOffset(guest_address),
              &raw, sizeof(raw));
}

uint8_t LoadGuestU8(uint8_t* base, uint32_t guest_address) {
  return *(base + guest_address + GuestPhysicalHostOffset(guest_address));
}

void StoreGuestU8(uint8_t* base, uint32_t guest_address, uint8_t value) {
  *(base + guest_address + GuestPhysicalHostOffset(guest_address)) = value;
}

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

void RememberFableMemoryBase(uint8_t* base) {
  if (base != nullptr) {
    g_last_fable_memory_base.store(reinterpret_cast<uintptr_t>(base),
                                   std::memory_order_relaxed);
  }
}

void EmulateSuppressedFableSyncIntervalBookkeeping(uint32_t device,
                                                   uint8_t* base) {
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

void CompleteFableDisplaySwapBookkeeping(uint32_t device, uint8_t* base,
                                         uint32_t submitted) {
  if (base == nullptr || device == 0 || submitted == 0) {
    return;
  }

  constexpr uint32_t kSwapCallbackOffset = 0x40B4;
  constexpr uint32_t kCompletedOffset = 0x40B8;
  constexpr uint32_t kCurrentBackBufferOffset = 0x2A9C;
  constexpr uint32_t kLastBackBufferOffset = 0x2AB0;
  constexpr uint32_t kDisplayFlagsOffset = 0x2ABD;

  const uint32_t callbacks =
      std::max(LoadGuestU32(base, device + kSwapCallbackOffset) + 1u,
               submitted);
  const uint32_t completed =
      std::max(LoadGuestU32(base, device + kCompletedOffset), submitted);
  StoreGuestU32(base, device + kSwapCallbackOffset, callbacks);
  StoreGuestU32(base, device + kCompletedOffset, completed);
  EmulateSuppressedFableSyncIntervalBookkeeping(device, base);

  const uint32_t current_back_buffer =
      LoadGuestU32(base, device + kCurrentBackBufferOffset);
  StoreGuestU32(base, device + kLastBackBufferOffset, current_back_buffer);
  StoreGuestU8(base, device + kDisplayFlagsOffset,
               LoadGuestU8(base, device + kDisplayFlagsOffset) & 0xEFu);

  StoreFablePresentCounters(device, submitted, callbacks, completed);
  if (!g_logged_fable_present_bookkeeping) {
    REXLOG_INFO(
        "Refii plume is emulating suppressed Fable display bookkeeping "
        "device=0x{:08X} submitted={} completed={} callbacks={}",
        device, submitted, completed, callbacks);
    g_logged_fable_present_bookkeeping = true;
  }
}

void EmulateSuppressedFableDisplaySwapBookkeeping(
    std::string_view symbol, const RendererHookArgs& args, uint8_t* base) {
  if (symbol != "sub_82BA34D8" || base == nullptr || args.r3 == 0) {
    return;
  }

  constexpr uint32_t kSubmittedOffset = 0x40B0;
  const uint32_t device = args.r3;
  RememberFableMemoryBase(base);
  const uint32_t submitted =
      LoadGuestU32(base, device + kSubmittedOffset) + 1u;
  StoreGuestU32(base, device + kSubmittedOffset, submitted);
  CompleteFableDisplaySwapBookkeeping(device, base, submitted);
}

void CompleteNativeFableDisplaySwapBookkeeping(const RendererHookArgs& args,
                                               uint8_t* base) {
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
        "Refii plume present pump is not advancing Fable display bookkeeping; "
        "guest display counters now move only from suppressed Fable display "
        "swap hooks");
    g_logged_plume_pump_fable_bookkeeping = true;
  }
}

bool PresentPlumeFrameFromRendererHook(const char* symbol,
                                       std::string_view reason,
                                       bool drive_fable_frame = false) {
  ZoneNamedN(tracy_present_zone, "RefiiRenderer/PresentPlumeFrameFromHook", true);
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

  auto next_present = RendererTraceClock::now();
  while (!g_plume_present_pump_stop_requested.load(std::memory_order_acquire)) {
    const uint32_t target_fps = RendererPlumePresentTargetFps();
    const std::chrono::nanoseconds interval =
        PlumePresentPumpInterval(target_fps);
    next_present += interval;

    {
      std::unique_lock lock(g_present_pump_mutex);
      g_present_pump_cv.wait_until(lock, next_present, [] {
        return g_plume_present_pump_stop_requested.load(
            std::memory_order_acquire);
      });
    }

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

    const bool presented =
        PresentPlumeFrameFromRendererHook("plume_present_pump", "fps-pump",
                                          true);
    if (presented) {
      LogPlumePumpFablePresentationPolicy();
    }
    if (presented && !g_logged_plume_present_pump_driver) {
      REXLOG_INFO(
          "Refii plume output is presenting from the Plume FPS pump at {} FPS",
          target_fps);
      g_logged_plume_present_pump_driver = true;
    }

    const auto now = RendererTraceClock::now();
    if (next_present + interval < now) {
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

  if (!g_plume_present_pump_running.exchange(false, std::memory_order_acq_rel) &&
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
  if (g_plume_present_pump_shutdown_requested.load(
          std::memory_order_acquire)) {
    return;
  }
  if (!RendererPlumePresentPumpEnabled()) {
    return;
  }

  bool expected = false;
  if (!g_plume_present_pump_running.compare_exchange_strong(
          expected, true, std::memory_order_acq_rel)) {
    return;
  }

  std::call_once(g_present_pump_atexit_once, [] {
    std::atexit([] { StopPlumePresentPumpThread(); });
  });
  if (g_plume_present_pump_shutdown_requested.load(
          std::memory_order_acquire)) {
    g_plume_present_pump_running.store(false, std::memory_order_release);
    return;
  }
  g_plume_present_pump_stop_requested.store(false, std::memory_order_release);
  g_present_pump_thread = std::thread(PlumePresentPumpMain);
}

const char* RendererBackendName(RendererBackend backend) {
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

const char* RendererGraphicsApiName(RendererGraphicsApi api) {
  switch (api) {
    case RendererGraphicsApi::kVulkan:
      return "vulkan";
    case RendererGraphicsApi::kD3D12:
      return "d3d12";
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

bool IsPlumeBootstrapPresentHook(std::string_view symbol) {
  return symbol == "sub_821F9F58" ||  // SetViewport
         symbol == "sub_821E2FE0" ||  // Clear
         symbol == "sub_8221C3E8" ||  // DrawPrimitive
         symbol == "sub_8221DFC0";    // DrawIndexedPrimitive
}

bool IsNativeRenderWorkHook(std::string_view symbol) {
  // D3DDevice_Swap owns Fable's PixCaptureVideoFrame call, so the wrapper must
  // run even when Plume owns presentation. Suppress the lower swap/sync helpers
  // that submit native presentation work instead.
  return symbol == "sub_82BA32F8" ||      // ComplexSwap
         symbol == "sub_82BA2DB8" ||      // SwapCallback
         symbol == "sub_82BA2F68" ||      // SynchronizeToPresentationInterval
         symbol == "sub_82BA2748";        // D3DDevice_BlockUntilIdle
}

void TraceHookFallback(const char* symbol, RendererHookKind kind) {
  if (!ShouldTraceHook(kind)) {
    return;
  }

  const std::string log_key =
      std::string(symbol) + ":" + RendererBackendName(GetRendererBackend()) +
      ":" + (ShouldRunNativeRendererHook(symbol, kind) ? "fallback"
                                                       : "suppressed");
  if (!g_logged_hook_fallbacks.insert(log_key).second) {
    return;
  }

  const RendererBackend backend = GetRendererBackend();
  if (backend == RendererBackend::kPlume) {
    if (ShouldRunNativeRendererHook(symbol, kind)) {
      REXLOG_INFO(
          "Refii renderer hook {} observed for plume/{}; native fallback active",
          symbol, RendererGraphicsApiName(GetRendererGraphicsApi()));
    } else {
      REXLOG_INFO(
          "Refii renderer hook {} observed for plume/{}; host presentation path "
          "will be suppressed",
          symbol, RendererGraphicsApiName(GetRendererGraphicsApi()));
    }
    return;
  }

  REXLOG_INFO("Refii renderer hook {} observed for {}; native fallback active",
              symbol, RendererBackendName(backend));
}

void MaybeLogRendererBackendState() {
  if (!RendererHooksEnabled() || GetRendererBackend() != RendererBackend::kPlume) {
    return;
  }

  if (!PlumeAvailable()) {
    if (!g_logged_plume_unavailable) {
      REXLOG_WARN(
          "Refii renderer backend is set to plume/{}, but Plume is not linked "
          "into this build. Renderer hooks will continue to fall back to the "
          "native Fable 2 renderer.",
          RendererGraphicsApiName(GetRendererGraphicsApi()));
      g_logged_plume_unavailable = true;
    }
    return;
  }

  if (!g_logged_plume_observer) {
    if (RendererNativeFallbackAllowed()) {
      REXLOG_INFO(
          "Refii renderer backend is set to plume/{}. Native draw/state fallback "
          "is allowed while the replacement path is being built.",
          RendererGraphicsApiName(GetRendererGraphicsApi()));
    } else {
      REXLOG_INFO(
          "Refii renderer backend is set to plume/{}. Guest draw/state hooks "
          "will run for staging; host presentation hooks stay suppressed.",
          RendererGraphicsApiName(GetRendererGraphicsApi()));
    }
    g_logged_plume_observer = true;
  }

  EnsurePlumePresentPumpStarted();
}

}  // namespace

bool PlumeAvailable() {
  return REFII_RENDERER_HAS_PLUME != 0;
}

void ShutdownRendererPresentPump() {
  StopPlumePresentPumpThread();
}

RendererHookTimingSnapshot GetRendererHookTimingSnapshot() {
  RendererHookTimingSnapshot snapshot;
  snapshot.hook_enters = g_hook_enter_count.load(std::memory_order_relaxed);
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
  snapshot.native_skipped_hooks =
      g_native_skipped_hook_count.load(std::memory_order_relaxed);
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
  snapshot.fable_device =
      g_last_fable_device.load(std::memory_order_relaxed);
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
  CopyTimingText(snapshot.last_native_skipped_symbol,
                 sizeof(snapshot.last_native_skipped_symbol),
                 g_last_native_skipped_symbol);
  return snapshot;
}

RendererHookArgs CaptureRendererHookArgs(const PPCContext& ctx) {
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
      .r3_u64 = ctx.r3.u64,
      .r4_u64 = ctx.r4.u64,
      .r5_u64 = ctx.r5.u64,
      .r6_u64 = ctx.r6.u64,
      .f1 = ctx.f1.f64,
      .f2 = ctx.f2.f64,
      .f3 = ctx.f3.f64,
      .f4 = ctx.f4.f64,
      .f5 = ctx.f5.f64,
      .f6 = ctx.f6.f64,
  };
}

void OnRendererGuestHookEnter(const char* symbol, RendererHookKind kind,
                              const RendererHookArgs& args, uint8_t* base) {
  ZoneNamedN(tracy_hook_zone, "RefiiRenderer/GuestHookEnter", true);
  ZoneTextV(tracy_hook_zone, symbol, std::strlen(symbol));
  if (!RendererHooksEnabled()) {
    return;
  }
  RememberFableMemoryBase(base);

  ConfigureRendererTracyPlots();
  const int64_t now_ns = RendererTraceNowNs();
  g_hook_enter_count.fetch_add(1, std::memory_order_relaxed);
  g_last_hook_gap_us.store(
      PlotDeltaUs("Refii Renderer Hook Gap Us", g_last_renderer_hook_enter_ns,
                  now_ns),
      std::memory_order_relaxed);
  SetLastTimingText(g_last_hook_symbol, sizeof(g_last_hook_symbol), symbol);
  TracyPlot("Refii Renderer Hook Kind", static_cast<int64_t>(kind));
  if (kind == RendererHookKind::kDraw) {
    g_draw_hook_enter_count.fetch_add(1, std::memory_order_relaxed);
    g_last_draw_hook_gap_us.store(
        PlotDeltaUs("Refii Renderer Draw Hook Gap Us",
                    g_last_draw_hook_enter_ns, now_ns),
        std::memory_order_relaxed);
  }

  MaybeLogRendererBackendState();
  PollRenderDocCaptureRequest();
  if (PlumeRendererBackendEnabled()) {
    NotifyPlumeBackendHook(symbol, kind);
  }
  ObserveRendererHookEnter(symbol, kind, args, base);
  TraceHookFallback(symbol, kind);
}

void OnRendererStateDispatchHook(const RendererHookArgs& args, uint8_t* base) {
  ZoneScopedN("RefiiRenderer/StateDispatchHook");
  if (!RendererHooksEnabled()) {
    return;
  }

  MaybeLogRendererBackendState();
  PollRenderDocCaptureRequest();
  if (PlumeRendererBackendEnabled()) {
    NotifyPlumeBackendHook("sub_8221E550", RendererHookKind::kDraw);
  }
  ObserveRendererStateDispatch(args, base);
  TraceHookFallback("sub_8221E550", RendererHookKind::kDraw);
}

void OnRendererPassQueueHook(const RendererHookArgs& args, uint8_t* base) {
  ZoneScopedN("RefiiRenderer/PassQueueHook");
  OnRendererGuestHookEnter("sub_82B668D0", RendererHookKind::kDraw, args);
  if (!RendererHooksEnabled()) {
    return;
  }

  ObserveRendererPassQueue(args, base);
}

void OnRendererPassQueueSnapshot(const RendererHookArgs& args, uint8_t* base) {
  ZoneScopedN("RefiiRenderer/PassQueueSnapshot");
  if (!RendererHooksEnabled()) {
    return;
  }

  ObserveRendererPassQueue(args, base);
}

void OnRendererGuestHookExit(const char* symbol, RendererHookKind kind,
                             const RendererHookArgs& args,
                             const PPCContext& ctx, uint8_t* base) {
  ZoneNamedN(tracy_hook_exit_zone, "RefiiRenderer/GuestHookExit", true);
  ZoneTextV(tracy_hook_exit_zone, symbol, std::strlen(symbol));
  if (!RendererHooksEnabled()) {
    return;
  }

  ObserveRendererHookExit(symbol, kind, args, ctx.r3.u32, base);
  if (PlumeRendererBackendEnabled() && kind == RendererHookKind::kDraw &&
      std::string_view(symbol) == "sub_82BA34D8") {
    CompleteNativeFableDisplaySwapBookkeeping(args, base);
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
  if (PlumeRendererBackendEnabled() && kind == RendererHookKind::kDraw &&
      std::string_view(symbol) == "rex_lhRenderer_EndScene") {
    if (!RendererPlumePresentPumpEnabled() &&
        !g_seen_plume_display_swap_hook.load(std::memory_order_relaxed)) {
      PresentPlumeFrameFromRendererHook(symbol, "EndScene");
    }
    return;
  }
  if (PlumeRendererBackendEnabled() && kind == RendererHookKind::kDraw &&
      IsPlumeBootstrapPresentHook(symbol) &&
      !RendererPlumePresentPumpEnabled() &&
      !g_seen_plume_display_swap_hook.load(std::memory_order_relaxed)) {
    PresentPlumeFrameFromRendererHook(symbol, "bootstrap");
  }
}

bool ShouldRunNativeRendererHook(const char* symbol, RendererHookKind kind) {
  ZoneScopedN("RefiiRenderer/ShouldRunNativeHook");
  if (!PlumeRendererBackendEnabled()) {
    return true;
  }
  if (RendererNativeFallbackAllowed()) {
    return true;
  }
  if (std::string_view(symbol) == "rex_lhRenderer_EndScene") {
    return true;
  }

  if (kind != RendererHookKind::kDraw) {
    return true;
  }

  return !IsNativeRenderWorkHook(symbol);
}

void OnRendererGuestHookNativeSkipped(const char* symbol, RendererHookKind kind,
                                      const RendererHookArgs& args,
                                      PPCContext& ctx, uint8_t* base) {
  ZoneNamedN(tracy_skip_zone, "RefiiRenderer/NativeHookSkipped", true);
  ZoneTextV(tracy_skip_zone, symbol, std::strlen(symbol));
  g_native_skipped_hook_count.fetch_add(1, std::memory_order_relaxed);
  SetLastTimingText(g_last_native_skipped_symbol,
                    sizeof(g_last_native_skipped_symbol), symbol);
  if (kind == RendererHookKind::kDraw &&
      std::string_view(symbol) == "sub_82265CD8") {
    ObserveRendererHookExit(symbol, kind, args, ctx.r3.u32, base);
  }

  ctx.r3.u64 = 0;

  if (RendererHookTracingEnabled() &&
      g_logged_hook_suppressed.insert(symbol).second) {
    REXLOG_INFO("Refii renderer hook {} suppressed native Fable 2 path",
                symbol);
  }

  if (kind == RendererHookKind::kDraw) {
    const std::string_view symbol_view(symbol);
    const bool is_display_swap = symbol_view == "sub_82BA34D8" ||
                                 symbol_view == "sub_82BA32F8";
    if (is_display_swap) {
      const int64_t now_ns = RendererTraceNowNs();
      ConfigureRendererTracyPlots();
      EmulateSuppressedFableDisplaySwapBookkeeping(symbol_view, args, base);
      g_display_present_hook_count.fetch_add(1, std::memory_order_relaxed);
      g_last_display_present_gap_us.store(
          PlotDeltaUs("Refii Fable Present Hook Gap Us",
                      g_last_display_present_hook_ns, now_ns),
          std::memory_order_relaxed);
      SetLastTimingText(g_last_display_present_symbol,
                        sizeof(g_last_display_present_symbol), symbol);
    }
    if (!is_display_swap &&
        (!IsPlumeBootstrapPresentHook(symbol_view) ||
         g_seen_plume_display_swap_hook.load(std::memory_order_relaxed))) {
      return;
    }

    if (is_display_swap) {
      g_seen_plume_display_swap_hook.store(true, std::memory_order_relaxed);
    }
    if (RendererPlumePresentPumpEnabled()) {
      return;
    }
    const bool presented = PresentPlumeFrameFromRendererHook(
        symbol, is_display_swap ? "display-swap" : "bootstrap-skipped");
    if (presented && !g_logged_plume_present_driver) {
      if (is_display_swap) {
        REXLOG_INFO(
            "Refii plume output is presenting from suppressed Fable display "
            "swap hook {}",
            symbol);
        g_logged_plume_present_driver = true;
      } else if (!g_logged_plume_bootstrap_present_driver) {
        REXLOG_INFO(
            "Refii plume output is presenting from bootstrap Fable frame hook "
            "{} until the display swap path is reached",
            symbol);
        g_logged_plume_bootstrap_present_driver = true;
      }
    } else if (!presented && !g_logged_plume_present_failed) {
      if (is_display_swap) {
        REXLOG_WARN(
            "Refii plume output could not present from Fable display swap hook {}",
            symbol);
      } else {
        REXLOG_WARN(
            "Refii plume output could not present from bootstrap Fable frame "
            "hook {}",
            symbol);
      }
      g_logged_plume_present_failed = true;
    }
  }
}

void OnRendererGuestHook(const char* symbol, RendererHookKind kind) {
  OnRendererGuestHookEnter(symbol, kind, RendererHookArgs{});
}

}  // namespace refii::renderer
