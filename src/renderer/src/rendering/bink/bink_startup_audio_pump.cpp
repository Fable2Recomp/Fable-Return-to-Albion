#include "runtime/refii_renderer/bik/bink_startup_audio_pump.h"

#include "runtime/refii_renderer/bik/bink_audio_cursor_bridge.h"
#include "runtime/refii_renderer/bik/bink_guest_layout.h"
#include "runtime/refii_renderer/bik/bink_guest_memory.h"
#include "runtime/refii_renderer/bik/bink_movie_clock.h"
#include "runtime/refii_renderer/bik/bink_startup_ownership.h"
#include "renderer_runtime.h"
#include "runtime/cvars.h"

#include <algorithm>
#include <atomic>
#include <chrono>
#include <mutex>
#include <unordered_map>

#include <rex/logging.h>

namespace refii::renderer {
bool RendererVerboseTracingEnabled();
bool RendererBinkRealtimeAudioClockEnabled();
} // namespace refii::renderer

namespace refii::renderer::bink {
namespace {

constexpr uint32_t kStartupBinkCursorQueryRead0 = 0x832BDFF0u;
constexpr uint32_t kStartupBinkCursorQueryRead1 = 0x832BDFFCu;
constexpr uint32_t kStartupBinkCursorQueryRead2 = 0x832BE008u;

std::atomic<uint32_t> g_startup_bink_audio_cursor_bridge_logs{0};
std::atomic<uint32_t> g_startup_bink_audio_cursor_plane_advances{0};
std::atomic<uint32_t> g_startup_bink_audio_present_pump_logs{0};
std::atomic<uint32_t> g_startup_bink_audio_sync_advance_logs{0};
std::atomic<uint32_t> g_startup_bink_native_cursor_adoptions{0};
std::mutex g_startup_bink_active_movie_mutex;
uint8_t *g_startup_bink_active_movie_base = nullptr;
uint32_t g_startup_bink_active_movie_guest = 0;
uint32_t g_startup_bink_active_movie_audio_frame = 0;
MovieClock::time_point g_startup_bink_active_movie_last_pump_tick = {};
std::mutex g_startup_bink_audio_budget_mutex;
std::unordered_map<uint32_t, uint32_t> g_startup_bink_audio_output_budgets;

bool IsStartupBinkAudioCursorCaller(uint32_t link_register) {
  switch (link_register) {
  case kStartupBinkCursorQueryRead0:
  case kStartupBinkCursorQueryRead1:
  case kStartupBinkCursorQueryRead2:
    return true;
  default:
    return false;
  }
}

uint32_t StartupBinkPlaneOutputBytesForDuration(
    uint8_t *base, uint32_t plane_guest,
    std::chrono::nanoseconds duration) {
  if (base == nullptr || plane_guest == 0 ||
      !IsReadableGuestByteRange(base, plane_guest, kStartupBinkPlaneBytes) ||
      duration <= std::chrono::nanoseconds(0)) {
    return 0;
  }

  const uint32_t rate = LoadGuestU32(base, plane_guest + 36u);
  const uint32_t bits = LoadGuestU32(base, plane_guest + 40u);
  const uint32_t channels = LoadGuestU32(base, plane_guest + 44u);
  if (rate == 0 || bits == 0 || channels == 0) {
    return 0;
  }

  const uint64_t bytes_per_second =
      static_cast<uint64_t>(rate) * static_cast<uint64_t>(channels) *
      static_cast<uint64_t>(std::max<uint32_t>(bits / 8u, 1u));
  const uint64_t duration_ns = static_cast<uint64_t>(duration.count());
  return static_cast<uint32_t>(std::min<uint64_t>(
      (bytes_per_second * duration_ns) / 1000000000ull, UINT32_MAX));
}

void AddStartupAudioDrainBudgetForPlane(
    uint8_t *base, uint32_t movie_guest, uint32_t plane_guest,
    uint32_t output_bytes) {
  if (output_bytes == 0 || base == nullptr || movie_guest == 0 ||
      plane_guest == 0 ||
      !IsReadableGuestByteRange(base, movie_guest, kStartupBinkMovieBytes) ||
      !IsReadableGuestByteRange(base, plane_guest, kStartupBinkPlaneBytes)) {
    return;
  }

  const uint32_t one_frame_bytes = StartupBinkPlaneOutputBytesForDuration(
      base, plane_guest, StartupBinkMovieFrameInterval(base, movie_guest));
  if (one_frame_bytes == 0) {
    return;
  }

  constexpr uint32_t kMaxQueuedMovieFrames = 6u;
  const uint32_t max_budget = static_cast<uint32_t>(std::min<uint64_t>(
      std::max<uint64_t>(
          static_cast<uint64_t>(one_frame_bytes) * kMaxQueuedMovieFrames,
          output_bytes),
      UINT32_MAX));

  std::lock_guard<std::mutex> lock(g_startup_bink_audio_budget_mutex);
  uint32_t &budget = g_startup_bink_audio_output_budgets[plane_guest];
  budget = static_cast<uint32_t>(std::min<uint64_t>(
      static_cast<uint64_t>(budget) + output_bytes, max_budget));
}

bool PublishStartupAudioOutputWindowForPlane(uint8_t *base,
                                             uint32_t plane_guest,
                                             uint32_t output_cursor) {
  if (base == nullptr || plane_guest == 0 ||
      !IsReadableGuestByteRange(base, plane_guest, kStartupBinkPlaneBytes)) {
    return false;
  }

  const uint32_t output_ring_size = LoadGuestU32(base, plane_guest + 188u);
  const uint32_t output_read = LoadGuestU32(base, plane_guest + 216u);
  if (output_ring_size == 0 || output_read >= output_ring_size) {
    return false;
  }

  const uint32_t cursor = output_cursor % output_ring_size;
  const uint32_t cursor_distance =
      cursor >= output_read ? cursor - output_read
                            : output_ring_size - output_read + cursor;
  const uint32_t output_window =
      cursor_distance >= 1024u ? cursor_distance - 1024u : 0u;
  StoreGuestU32(base, plane_guest + 228u, cursor);
  StoreGuestU32(base, plane_guest + 220u, output_window);
  return true;
}

bool NormalizeStartupAudioOutputWindowForPlane(uint8_t *base,
                                               uint32_t plane_guest) {
  if (base == nullptr || plane_guest == 0 ||
      !IsReadableGuestByteRange(base, plane_guest, kStartupBinkPlaneBytes)) {
    return false;
  }

  const uint32_t output_ring_size = LoadGuestU32(base, plane_guest + 188u);
  if (output_ring_size == 0) {
    return false;
  }

  const uint32_t output_cursor = LoadGuestU32(base, plane_guest + 228u);
  if (output_cursor >= output_ring_size) {
    StoreGuestU32(base, plane_guest + 228u, output_cursor % output_ring_size);
  }

  const uint32_t output_pending = LoadGuestU32(base, plane_guest + 220u);
  if (output_pending > output_ring_size) {
    StoreGuestU32(base, plane_guest + 220u, 0u);
    return false;
  }
  return true;
}

bool PublishStartupAudioOutputWindowForVoice(uint8_t *base,
                                             uint32_t voice_guest,
                                             uint32_t output_cursor) {
  if (base == nullptr || voice_guest == 0) {
    return false;
  }

  uint32_t movie_guest = 0;
  {
    std::lock_guard<std::mutex> lock(g_startup_bink_active_movie_mutex);
    if (g_startup_bink_active_movie_base != base) {
      return false;
    }
    movie_guest = g_startup_bink_active_movie_guest;
  }

  if (movie_guest == 0 ||
      !IsReadableGuestByteRange(base, movie_guest, kStartupBinkMovieBytes)) {
    return false;
  }

  const uint32_t plane_count = LoadGuestU32(base, movie_guest + 276u);
  const uint32_t plane_base = LoadGuestU32(base, movie_guest + 280u);
  if (plane_count == 0 || plane_count > 4u || plane_base == 0) {
    return false;
  }

  for (uint32_t plane_index = 0; plane_index < plane_count; ++plane_index) {
    const uint32_t plane_guest =
        plane_base + plane_index * kStartupBinkPlaneBytes;
    if (IsReadableGuestByteRange(base, plane_guest, kStartupBinkPlaneBytes) &&
        LoadGuestU32(base, plane_guest + 92u) == voice_guest) {
      return PublishStartupAudioOutputWindowForPlane(base, plane_guest,
                                                     output_cursor);
    }
  }
  return false;
}

bool AdvanceStartupBinkAudioCursorForPlane(
    uint8_t *base, uint32_t movie_guest, uint32_t plane_guest,
    std::chrono::nanoseconds advance_duration, uint32_t frame_delta) {
  if (base == nullptr || plane_guest == 0 ||
      !IsReadableGuestByteRange(base, plane_guest, kStartupBinkPlaneBytes) ||
      LoadGuestU32(base, plane_guest + 56u) == 0 ||
      advance_duration <= std::chrono::nanoseconds(0)) {
    return false;
  }

  const uint32_t voice_guest = LoadGuestU32(base, plane_guest + 92u);
  if (voice_guest == 0) {
    return false;
  }
  NormalizeStartupAudioOutputWindowForPlane(base, plane_guest);

  const uint32_t queued_output_budget =
      StartupAudioDrainOutputBudgetForPlane(plane_guest);
  const uint32_t one_frame_output_bytes = StartupBinkPlaneOutputBytesForDuration(
      base, plane_guest, StartupBinkMovieFrameInterval(base, movie_guest));
  constexpr uint32_t kAudioLeadMovieFrames = 3u;
  if (one_frame_output_bytes != 0 &&
      queued_output_budget >= one_frame_output_bytes * kAudioLeadMovieFrames) {
    return false;
  }

  // frame_delta == 0 is the real-time caller: advance by wall-clock duration
  // (voice consumption semantics) instead of whole movie-frame byte steps.
  // Either way the advance stays sample-frame aligned — an odd-byte cursor
  // makes every later window odd, and off-by-one ring writes shear the
  // 16-bit sample boundary into noise.
  const uint32_t old_cursor = LoadGuestU32(base, plane_guest + 228u);
  uint32_t advance_bytes =
      frame_delta != 0
          ? static_cast<uint32_t>(std::min<uint64_t>(
                static_cast<uint64_t>(one_frame_output_bytes) *
                    static_cast<uint64_t>(frame_delta),
                UINT32_MAX))
          : StartupBinkPlaneOutputBytesForDuration(base, plane_guest,
                                                   advance_duration);
  const uint32_t sample_frame = std::clamp<uint32_t>(
      std::max<uint32_t>(LoadGuestU32(base, plane_guest + 44u), 1u) *
          std::max<uint32_t>(LoadGuestU32(base, plane_guest + 40u) / 8u, 1u),
      1u, 64u);
  if (sample_frame > 1u) {
    advance_bytes -= advance_bytes % sample_frame;
  }
  if (advance_bytes == 0) {
    return false;
  }
  const auto bridge =
      AdvanceAudioCursorByBytes(voice_guest, old_cursor, advance_bytes);
  if (!bridge.advanced) {
    return false;
  }

  AddStartupAudioDrainBudgetForPlane(base, movie_guest, plane_guest,
                                     bridge.advanced_bytes);
  PublishStartupAudioOutputWindowForPlane(base, plane_guest,
                                          bridge.new_cursor);
  const uint32_t drain_budget =
      StartupAudioDrainOutputBudgetForPlane(plane_guest);

  const uint32_t count =
      g_startup_bink_audio_cursor_plane_advances.fetch_add(
          1, std::memory_order_relaxed) +
      1u;
  if (refii::renderer::RendererVerboseTracingEnabled() &&
      (count <= 8u || count % 600u == 0u)) {
    REXLOG_INFO(
        "Refii startup BIK advanced Plume-owned audio plane cursor #{} "
        "plane=0x{:08X} voice=0x{:08X} old={} base={} new={} ring={} "
        "bytes_per_second={} elapsed_us={} requested_us={}",
        count, plane_guest, voice_guest, old_cursor, bridge.cursor_base,
        bridge.new_cursor, bridge.ring_size, bridge.bytes_per_second,
        static_cast<unsigned long long>(
            std::chrono::duration_cast<std::chrono::microseconds>(
                bridge.cursor_duration)
                .count()),
        static_cast<unsigned long long>(
            std::chrono::duration_cast<std::chrono::microseconds>(
                advance_duration)
                .count()));
  }

  const uint32_t sync_count =
      g_startup_bink_audio_sync_advance_logs.fetch_add(
          1, std::memory_order_relaxed) +
      1u;
  if (sync_count <= 24u || sync_count % 120u == 0u) {
    REXLOG_INFO(
        "Refii startup BIK audio sync advance #{} movie=0x{:08X} "
        "plane=0x{:08X} voice=0x{:08X} frame={}/{} "
        "cursor={}=>{} advanced={} budget={} ring={} rate={} bits={} "
        "channels={} out_read={} out_pending={} out_write={} source_fill={} "
        "advance_us={}",
        sync_count, movie_guest, plane_guest, voice_guest,
        IsReadableGuestByteRange(base, movie_guest, kStartupBinkMovieBytes)
            ? LoadGuestU32(base, movie_guest + 12u)
            : 0u,
        IsReadableGuestByteRange(base, movie_guest, kStartupBinkMovieBytes)
            ? LoadGuestU32(base, movie_guest + 692u)
            : 0u,
        old_cursor, bridge.new_cursor, bridge.advanced_bytes, drain_budget,
        bridge.ring_size, LoadGuestU32(base, plane_guest + 36u),
        LoadGuestU32(base, plane_guest + 40u),
        LoadGuestU32(base, plane_guest + 44u),
        LoadGuestU32(base, plane_guest + 216u),
        LoadGuestU32(base, plane_guest + 220u),
        LoadGuestU32(base, plane_guest + 228u),
        StartupBinkRingFill(base, plane_guest),
        static_cast<unsigned long long>(
            std::chrono::duration_cast<std::chrono::microseconds>(
                advance_duration)
                .count()));
  }
  return true;
}

} // namespace

void RegisterStartupAudioCursorBridge(uint8_t *base, uint32_t plane_guest) {
  if (base == nullptr || plane_guest == 0 ||
      !IsReadableGuestByteRange(base, plane_guest, kStartupBinkPlaneBytes)) {
    return;
  }

  const uint32_t voice_guest = LoadGuestU32(base, plane_guest + 92u);
  const uint32_t ring_size = LoadGuestU32(base, plane_guest + 188u);
  const uint32_t rate = LoadGuestU32(base, plane_guest + 36u);
  const uint32_t bits = LoadGuestU32(base, plane_guest + 40u);
  const uint32_t channels = LoadGuestU32(base, plane_guest + 44u);
  if (voice_guest == 0 || ring_size == 0 || rate == 0 || bits == 0 ||
      channels == 0) {
    return;
  }

  const uint64_t bytes_per_second64 =
      static_cast<uint64_t>(rate) * static_cast<uint64_t>(channels) *
      static_cast<uint64_t>(std::max<uint32_t>(bits / 8u, 1u));
  if (bytes_per_second64 == 0 || bytes_per_second64 > UINT32_MAX) {
    return;
  }

  RegisterAudioCursorBridge(voice_guest, ring_size,
                            static_cast<uint32_t>(bytes_per_second64),
                            LoadGuestU32(base, plane_guest + 228u));
}

void RegisterStartupAudioCursorBridgesForMovie(uint8_t *base,
                                               uint32_t movie_guest) {
  if (base == nullptr || movie_guest == 0 ||
      !IsReadableGuestByteRange(base, movie_guest, kStartupBinkMovieBytes)) {
    return;
  }

  const uint32_t plane_count = LoadGuestU32(base, movie_guest + 276u);
  const uint32_t plane_base = LoadGuestU32(base, movie_guest + 280u);
  if (plane_count == 0 || plane_count > 4u || plane_base == 0) {
    return;
  }

  for (uint32_t plane_index = 0; plane_index < plane_count; ++plane_index) {
    RegisterStartupAudioCursorBridge(
        base, plane_base + plane_index * kStartupBinkPlaneBytes);
  }
}

void ClearStartupAudioCursorsForMovie(uint8_t *base, uint32_t movie_guest) {
  if (base == nullptr || movie_guest == 0 ||
      !IsReadableGuestByteRange(base, movie_guest, kStartupBinkMovieBytes)) {
    return;
  }

  ClearMovie(movie_guest);
  const uint32_t plane_count = LoadGuestU32(base, movie_guest + 276u);
  const uint32_t plane_base = LoadGuestU32(base, movie_guest + 280u);
  if (plane_count == 0 || plane_count > 4u || plane_base == 0) {
    return;
  }

  for (uint32_t plane_index = 0; plane_index < plane_count; ++plane_index) {
    const uint32_t plane_guest =
        plane_base + plane_index * kStartupBinkPlaneBytes;
    if (!IsReadableGuestByteRange(base, plane_guest, kStartupBinkPlaneBytes)) {
      continue;
    }
    const uint32_t voice_guest = LoadGuestU32(base, plane_guest + 92u);
    if (voice_guest != 0) {
      ClearAudioCursorBridge(voice_guest);
    }
    std::lock_guard<std::mutex> lock(g_startup_bink_audio_budget_mutex);
    g_startup_bink_audio_output_budgets.erase(plane_guest);
  }
}

bool TryBridgeStartupAudioCursor(const RendererHookArgs &args, PPCContext &ctx,
                                 uint8_t *base) {
  (void)ctx;
  if (!IsStartupBinkAudioCursorCaller(args.lr) || base == nullptr ||
      args.r3 == 0 || args.r4 == 0 ||
      !IsReadableGuestByteRange(base, args.r4, sizeof(uint32_t))) {
    return false;
  }

  const uint32_t native_cursor = LoadGuestU32(base, args.r4);
  const auto bridge = ObserveNativeAudioCursor(args.r3, native_cursor);
  if (refii::renderer::RendererBinkRealtimeAudioClockEnabled()) {
    // Observe-only: these queries come from sub_832BDEA0's own stability
    // loop — it owns the cursor/window fields and must see the TRUE native
    // reads. The adoption log stays as evidence that the device cursor
    // ticks.
    if (bridge.native_live) {
      const uint32_t count =
          g_startup_bink_native_cursor_adoptions.fetch_add(
              1, std::memory_order_relaxed) +
          1u;
      if (count <= 4u || count % 600u == 0u) {
        REXLOG_INFO(
            "Refii startup BIK native audio cursor adopted #{} voice=0x{:08X} "
            "cursor={} consumed={} ring={}",
            count, args.r3, bridge.cursor, bridge.consumed_bytes,
            bridge.ring_size);
      }
    }
    return false;
  }
  if (!bridge.bridged) {
    return false;
  }

  StoreGuestU32(base, args.r4, bridge.cursor);
  PublishStartupAudioOutputWindowForVoice(base, args.r3, bridge.cursor);
  const uint32_t count = g_startup_bink_audio_cursor_bridge_logs.fetch_add(
                             1, std::memory_order_relaxed) +
                         1u;
  if (count <= 4u ||
      (refii::renderer::RendererVerboseTracingEnabled() &&
       count % 600u == 0u)) {
    REXLOG_INFO(
        "Refii startup BIK bridged Plume audio cursor #{} lr=0x{:08X} "
        "voice=0x{:08X} out=0x{:08X} native={} bridged={} ring={} "
        "bytes_per_second={} plume_owned={}",
        count, args.lr, args.r3, args.r4, native_cursor, bridge.cursor,
        bridge.ring_size, bridge.bytes_per_second,
        refii::renderer::FableStartupVideoPumpActive());
  }
  return true;
}

void RememberStartupActiveMovie(uint8_t *base, uint32_t movie_guest) {
  if (base == nullptr || movie_guest == 0) {
    return;
  }

  std::lock_guard<std::mutex> lock(g_startup_bink_active_movie_mutex);
  if (g_startup_bink_active_movie_base != base ||
      g_startup_bink_active_movie_guest != movie_guest) {
    g_startup_bink_active_movie_audio_frame =
        LooksLikeStartupBinkMovie(base, movie_guest)
            ? LoadGuestU32(base, movie_guest + 12u)
            : 0u;
    g_startup_bink_active_movie_last_pump_tick = {};
  }
  g_startup_bink_active_movie_base = base;
  g_startup_bink_active_movie_guest = movie_guest;
}

void ClearStartupActiveMovie(uint8_t *base, uint32_t movie_guest) {
  if (base == nullptr || movie_guest == 0) {
    return;
  }

  std::lock_guard<std::mutex> lock(g_startup_bink_active_movie_mutex);
  if (g_startup_bink_active_movie_base == base &&
      g_startup_bink_active_movie_guest == movie_guest) {
    g_startup_bink_active_movie_base = nullptr;
    g_startup_bink_active_movie_guest = 0;
    g_startup_bink_active_movie_audio_frame = 0;
    g_startup_bink_active_movie_last_pump_tick = {};
  }
}

bool AdvanceStartupAudioCursorsForMovie(
    uint8_t *base, uint32_t movie_guest,
    std::chrono::nanoseconds advance_duration, uint32_t frame_delta) {
  if (base == nullptr || movie_guest == 0 ||
      !BinkPlumeOwnsMovie(base, movie_guest) ||
      !IsReadableGuestByteRange(base, movie_guest, kStartupBinkMovieBytes)) {
    return false;
  }

  const uint32_t plane_count = LoadGuestU32(base, movie_guest + 276u);
  const uint32_t plane_base = LoadGuestU32(base, movie_guest + 280u);
  if (plane_count == 0 || plane_count > 4u || plane_base == 0) {
    return false;
  }

  RegisterStartupAudioCursorBridgesForMovie(base, movie_guest);

  bool advanced_any = false;
  for (uint32_t plane_index = 0; plane_index < plane_count; ++plane_index) {
    advanced_any |= AdvanceStartupBinkAudioCursorForPlane(
        base, movie_guest, plane_base + plane_index * kStartupBinkPlaneBytes,
        advance_duration, frame_delta);
  }
  return advanced_any;
}

void AdvanceStartupAudioCursorsForMovieFrame(uint8_t *base,
                                             uint32_t movie_guest,
                                             uint32_t frame_delta) {
  if (base == nullptr || movie_guest == 0 || frame_delta == 0 ||
      !BinkPlumeOwnsMovie(base, movie_guest) ||
      !IsReadableGuestByteRange(base, movie_guest, kStartupBinkMovieBytes)) {
    return;
  }

  const auto interval = StartupBinkMovieFrameInterval(base, movie_guest);
  const auto elapsed = FrameIntervalForDelta(interval, frame_delta);
  if (elapsed <= std::chrono::nanoseconds(0)) {
    return;
  }

  const bool advanced =
      AdvanceStartupAudioCursorsForMovie(base, movie_guest, elapsed,
                                         frame_delta);
  if (!advanced) {
    return;
  }

  {
    std::lock_guard<std::mutex> lock(g_startup_bink_active_movie_mutex);
    if (g_startup_bink_active_movie_base == base &&
        g_startup_bink_active_movie_guest == movie_guest) {
      g_startup_bink_active_movie_audio_frame =
          LoadGuestU32(base, movie_guest + 12u);
    }
  }

  const uint32_t count =
      g_startup_bink_audio_present_pump_logs.fetch_add(
          1, std::memory_order_relaxed) +
      1u;
  if (RendererVerboseTracingEnabled() && (count <= 8u || count % 600u == 0u)) {
    REXLOG_INFO(
        "Refii startup BIK Plume frame audio pump #{} movie=0x{:08X} "
        "frame={} delta={} elapsed_us={} active={}",
        count, movie_guest, LoadGuestU32(base, movie_guest + 12u), frame_delta,
        static_cast<unsigned long long>(
            std::chrono::duration_cast<std::chrono::microseconds>(elapsed)
                .count()),
        refii::renderer::FableStartupVideoPumpActive());
  }
}

void PrimeStartupAudioDrainBudgetForMovie(uint8_t *base,
                                          uint32_t movie_guest) {
  if (base == nullptr || movie_guest == 0 ||
      !IsReadableGuestByteRange(base, movie_guest, kStartupBinkMovieBytes)) {
    return;
  }

  const uint32_t plane_count = LoadGuestU32(base, movie_guest + 276u);
  const uint32_t plane_base = LoadGuestU32(base, movie_guest + 280u);
  if (plane_count == 0 || plane_count > 4u || plane_base == 0) {
    return;
  }

  const auto interval = StartupBinkMovieFrameInterval(base, movie_guest);
  for (uint32_t plane_index = 0; plane_index < plane_count; ++plane_index) {
    const uint32_t plane_guest =
        plane_base + plane_index * kStartupBinkPlaneBytes;
    AddStartupAudioDrainBudgetForPlane(
        base, movie_guest, plane_guest,
        StartupBinkPlaneOutputBytesForDuration(base, plane_guest, interval));
  }
}


bool StartupAudioDrainBudgetAvailableForPlane(uint32_t plane_guest) {
  if (plane_guest == 0) {
    return false;
  }

  std::lock_guard<std::mutex> lock(g_startup_bink_audio_budget_mutex);
  auto it = g_startup_bink_audio_output_budgets.find(plane_guest);
  return it != g_startup_bink_audio_output_budgets.end() && it->second != 0;
}

uint32_t StartupAudioDrainOutputBudgetForPlane(uint32_t plane_guest) {
  if (plane_guest == 0) {
    return 0;
  }

  std::lock_guard<std::mutex> lock(g_startup_bink_audio_budget_mutex);
  auto it = g_startup_bink_audio_output_budgets.find(plane_guest);
  return it != g_startup_bink_audio_output_budgets.end() ? it->second : 0u;
}

bool PublishStartupAudioDrainBudgetForPlane(uint8_t *base,
                                            uint32_t plane_guest,
                                            uint32_t output_bytes) {
  if (base == nullptr || plane_guest == 0 || output_bytes == 0 ||
      !IsReadableGuestByteRange(base, plane_guest, kStartupBinkPlaneBytes)) {
    return false;
  }

  const uint32_t output_ring_size = LoadGuestU32(base, plane_guest + 188u);
  const uint32_t output_read = LoadGuestU32(base, plane_guest + 216u);
  if (output_ring_size == 0 || output_read >= output_ring_size) {
    return false;
  }

  const uint32_t bridged_cursor =
      (output_read + std::min<uint32_t>(output_bytes, output_ring_size - 1u) +
       1024u) %
      output_ring_size;
  return PublishStartupAudioOutputWindowForPlane(base, plane_guest,
                                                 bridged_cursor);
}

uint32_t StartupAudioDrainSourceBudgetForPlane(uint32_t plane_guest,
                                               bool high_byte_plane) {
  if (plane_guest == 0) {
    return 0;
  }

  std::lock_guard<std::mutex> lock(g_startup_bink_audio_budget_mutex);
  auto it = g_startup_bink_audio_output_budgets.find(plane_guest);
  if (it == g_startup_bink_audio_output_budgets.end()) {
    return 0;
  }
  const uint64_t source_budget =
      high_byte_plane ? static_cast<uint64_t>(it->second) * 2u : it->second;
  return static_cast<uint32_t>(
      std::min<uint64_t>(source_budget, UINT32_MAX));
}

void ConsumeStartupAudioDrainBudgetForPlane(uint32_t plane_guest,
                                            uint32_t output_bytes) {
  if (plane_guest == 0 || output_bytes == 0) {
    return;
  }

  std::lock_guard<std::mutex> lock(g_startup_bink_audio_budget_mutex);
  auto it = g_startup_bink_audio_output_budgets.find(plane_guest);
  if (it == g_startup_bink_audio_output_budgets.end()) {
    return;
  }
  it->second = output_bytes >= it->second ? 0u : it->second - output_bytes;
  if (it->second < 4u) {
    it->second = 0u;
  }
}


void PumpActiveStartupBinkAudioCursorsFromPlume() {
  uint8_t *base = nullptr;
  uint32_t movie_guest = 0;
  uint32_t previous_frame = 0;
  MovieClock::time_point previous_tick = {};
  const auto now = MovieClock::now();
  {
    std::lock_guard<std::mutex> lock(g_startup_bink_active_movie_mutex);
    base = g_startup_bink_active_movie_base;
    movie_guest = g_startup_bink_active_movie_guest;
    previous_frame = g_startup_bink_active_movie_audio_frame;
    previous_tick = g_startup_bink_active_movie_last_pump_tick;
    g_startup_bink_active_movie_last_pump_tick = now;
  }

  if (base == nullptr || movie_guest == 0) {
    return;
  }
  if (!BinkPlumeOwnsMovie(base, movie_guest) ||
      !IsReadableGuestByteRange(base, movie_guest, kStartupBinkMovieBytes) ||
      !LooksLikeStartupBinkMovie(base, movie_guest) ||
      BinkMovieFinished(base, movie_guest)) {
    ClearStartupActiveMovie(base, movie_guest);
    return;
  }

  const uint32_t current_frame = LoadGuestU32(base, movie_guest + 12u);
  if (refii::renderer::RendererBinkRealtimeAudioClockEnabled()) {
    // The native voice service (sub_832BDEA0, invoked from the plume drain
    // path) owns the audio clock: real device cursor, window publish, and
    // underrun silence. No synthetic advance — a second clock can only
    // drift against the device and corrupt the looping voice buffer. Keep
    // the frame bookkeeping current so a live flip back to the frame-driven
    // path never replays a stale frame delta.
    (void)previous_tick;
    std::lock_guard<std::mutex> lock(g_startup_bink_active_movie_mutex);
    if (g_startup_bink_active_movie_base == base &&
        g_startup_bink_active_movie_guest == movie_guest) {
      g_startup_bink_active_movie_audio_frame = current_frame;
    }
    return;
  }

  if (current_frame == previous_frame) {
    return;
  }
  if (current_frame < previous_frame) {
    std::lock_guard<std::mutex> lock(g_startup_bink_active_movie_mutex);
    if (g_startup_bink_active_movie_base == base &&
        g_startup_bink_active_movie_guest == movie_guest) {
      g_startup_bink_active_movie_audio_frame = current_frame;
    }
    return;
  }

  AdvanceStartupAudioCursorsForMovieFrame(base, movie_guest,
                                          current_frame - previous_frame);
}

} // namespace refii::renderer::bink

namespace refii::renderer {

void PumpStartupBinkAudioCursorsFromPlume() {
  bink::PumpActiveStartupBinkAudioCursorsFromPlume();
}

} // namespace refii::renderer
