#include "runtime/refii_renderer/bik/bink_startup_trace.h"

#include "runtime/refii_renderer/bik/bink_guest_layout.h"
#include "runtime/refii_renderer/bik/bink_guest_memory.h"
#include "renderer_hooks_private.h"
#include "runtime/cvars.h"
#include "renderer_runtime.h"

#include <algorithm>
#include <atomic>
#include <mutex>
#include <string>
#include <unordered_map>

namespace refii::renderer::bink {
namespace {

std::atomic<uint32_t> g_startup_bink_plume_pcm_window_traces{0};

} // namespace


uint64_t StartupBinkTimingUs(StartupBinkTimingClock::time_point begin,
                             StartupBinkTimingClock::time_point end) {
  return static_cast<uint64_t>(
      std::chrono::duration_cast<std::chrono::microseconds>(end - begin)
          .count());
}

void LogStartupBinkTiming(const char *symbol, const RendererHookArgs &args,
                          const PPCContext &ctx, uint64_t elapsed_us) {
  static std::mutex mutex;
  static std::unordered_map<std::string, uint32_t> counts;

  uint32_t count = 0;
  {
    std::lock_guard<std::mutex> lock(mutex);
    count = ++counts[std::string(symbol)];
  }
  constexpr uint64_t kStartupBinkSlowCallUs = 33333u;
  if (!RendererVerboseTracingEnabled() && elapsed_us < kStartupBinkSlowCallUs) {
    return;
  }
  if (count > 8u && (count % 120u) != 0u &&
      elapsed_us < kStartupBinkSlowCallUs) {
    return;
  }

  REXLOG_INFO("Refii renderer startup BIK timing {} #{} lr=0x{:08X} "
              "movie=0x{:08X} result=0x{:08X} elapsed_us={} pump_active={}",
              symbol, count, args.lr, args.r3, ctx.r3.u32, elapsed_us,
              FableStartupVideoPumpActive());
}


void LogStartupBinkMovieState(const char *phase, const RendererHookArgs &args,
                              const PPCContext &ctx, uint8_t *base) {
  if (base == nullptr || args.r3 == 0 ||
      !IsReadableGuestByteRange(base, args.r3, kStartupBinkMovieBytes)) {
    return;
  }

  static std::mutex mutex;
  static std::unordered_map<std::string, uint32_t> counts;

  uint32_t count = 0;
  {
    std::lock_guard<std::mutex> lock(mutex);
    count = ++counts[std::string(phase)];
  }
  if (count > 16u && (count % 60u) != 0u) {
    return;
  }

  REXLOG_INFO(
      "Refii renderer startup BIK movie state {} #{} lr=0x{:08X} "
      "movie=0x{:08X} result=0x{:08X} frame={}/{} wrapper_total={} rate={}/{} "
      "pending={} blocked={} stopped={} async={} ready={} copy={} "
      "timer=0x{:08X} wait0=0x{:08X} wait1=0x{:08X} fence=0x{:08X} "
      "decode_cb=0x{:08X} decode_ctx=0x{:08X} decode_lock=0x{:08X} "
      "decode_gate={} decode_busy={} decode_hold={} decode_frame={} "
      "decode_limit={} decode_count={}",
      phase, count, args.lr, args.r3, ctx.r3.u32,
      LoadGuestU32(base, args.r3 + 12u), LoadGuestU32(base, args.r3 + 692u),
      LoadGuestU32(base, args.r3 + 8u), LoadGuestU32(base, args.r3 + 20u),
      LoadGuestU32(base, args.r3 + 24u),
      LoadGuestU32(base, args.r3 + 236u), LoadGuestU32(base, args.r3 + 240u),
      LoadGuestU32(base, args.r3 + 244u), LoadGuestU32(base, args.r3 + 276u),
      LoadGuestU32(base, args.r3 + 328u), LoadGuestU32(base, args.r3 + 408u),
      LoadGuestU32(base, args.r3 + 704u), LoadGuestU32(base, args.r3 + 856u),
      LoadGuestU32(base, args.r3 + 600u), LoadGuestU32(base, args.r3 + 820u),
      LoadGuestU32(base, args.r3 + 352u), args.r3 + 336u,
      LoadGuestU32(base, args.r3 + 600u), LoadGuestU32(base, args.r3 + 252u),
      LoadGuestU32(base, args.r3 + 328u), LoadGuestU32(base, args.r3 + 372u),
      LoadGuestU32(base, args.r3 + 228u), LoadGuestU32(base, args.r3 + 408u),
      LoadGuestU32(base, args.r3 + 412u));
}

void LogStartupBinkDrainState(const RendererHookArgs &args,
                              const PPCContext &ctx, uint8_t *base,
                              uint64_t elapsed_us) {
  if (base == nullptr || args.r3 == 0 || elapsed_us < 33333u ||
      !IsReadableGuestByteRange(base, args.r3, kStartupBinkMovieBytes)) {
    return;
  }

  static std::atomic<uint32_t> count{0};
  const uint32_t current = count.fetch_add(1, std::memory_order_relaxed) + 1u;
  if (current > 16u && (current % 60u) != 0u) {
    return;
  }

  const uint32_t plane_count = LoadGuestU32(base, args.r3 + 276u);
  const uint32_t plane_base = LoadGuestU32(base, args.r3 + 280u);
  auto plane_field = [&](uint32_t plane, uint32_t offset) -> uint32_t {
    if (plane >= plane_count || plane_base == 0) {
      return 0;
    }
    const uint32_t plane_guest = plane_base + plane * kStartupBinkPlaneBytes;
    if (!IsReadableGuestByteRange(base, plane_guest, kStartupBinkPlaneBytes)) {
      return 0;
    }
    return LoadGuestU32(base, plane_guest + offset);
  };
  auto ring_fill = [&](uint32_t plane) -> uint32_t {
    const uint32_t write = plane_field(plane, 0u);
    const uint32_t read = plane_field(plane, 24u);
    const uint32_t size = plane_field(plane, 8u);
    if (size == 0) {
      return 0;
    }
    return write >= read ? write - read : write + size - read;
  };
  auto voice_word = [&](uint32_t plane, uint32_t offset) -> uint32_t {
    const uint32_t voice_guest = plane_field(plane, 92u);
    if (voice_guest == 0 ||
        !IsReadableGuestByteRange(base, voice_guest + offset, 4u)) {
      return 0;
    }
    return LoadGuestU32(base, voice_guest + offset);
  };

  REXLOG_INFO(
      "Refii renderer startup BIK slow drain #{} lr=0x{:08X} "
      "movie=0x{:08X} result=0x{:08X} elapsed_us={} frames={}/{} "
      "planes={} pending={} blocked={} p0_fill={} p0_limit={} p0_frame={} "
      "p0_ready=0x{:08X} p0_read=0x{:08X} p0_done=0x{:08X} "
      "p0_enabled={} p0_gate={} p0_hold={} p0_out_read={} p0_out_pending={} "
      "p0_out_write={} p0_voice=0x{:08X} p0_voice_vtbl=0x{:08X} "
      "p0_voice_driver=0x{:08X} p0_start={} p0_end={} p0_cursor={} "
      "p0_target={} p0_wrap={} p0_playing={} p0_volume={}/{} "
      "p1_fill={} p1_limit={} p1_frame={} p1_ready=0x{:08X} "
      "p1_read=0x{:08X} p1_done=0x{:08X}",
      current, args.lr, args.r3, ctx.r3.u32, elapsed_us,
      LoadGuestU32(base, args.r3 + 12u), LoadGuestU32(base, args.r3 + 692u),
      plane_count, LoadGuestU32(base, args.r3 + 236u),
      LoadGuestU32(base, args.r3 + 240u), ring_fill(0u), plane_field(0u, 48u),
      plane_field(0u, 68u), plane_field(0u, 348u), plane_field(0u, 352u),
      plane_field(0u, 356u), plane_field(0u, 56u), plane_field(0u, 208u),
      plane_field(0u, 212u), plane_field(0u, 216u), plane_field(0u, 220u),
      plane_field(0u, 228u), plane_field(0u, 92u), voice_word(0u, 0u),
      voice_word(0u, 76u), plane_field(0u, 96u), plane_field(0u, 100u),
      plane_field(0u, 136u), plane_field(0u, 188u), plane_field(0u, 192u),
      plane_field(0u, 208u), plane_field(0u, 200u), plane_field(0u, 204u),
      ring_fill(1u), plane_field(1u, 48u), plane_field(1u, 68u),
      plane_field(1u, 348u), plane_field(1u, 352u), plane_field(1u, 356u));
}

void LogStartupBinkBitstreamCopyTrace(
    const RendererHookArgs &args, const PPCContext &ctx, uint8_t *base,
    uint64_t elapsed_us, uint32_t before_write, uint32_t before_read,
    uint32_t before_fill) {
  if (!RendererVerboseTracingEnabled() && elapsed_us < 33333u) {
    return;
  }

  if (base == nullptr || args.r3 == 0 ||
      !IsReadableGuestByteRange(base, args.r3, kStartupBinkMovieBytes)) {
    return;
  }

  static std::atomic<uint32_t> count_source{0};
  const uint32_t count =
      count_source.fetch_add(1, std::memory_order_relaxed) + 1u;
  if (count > 16u && (count % 120u) != 0u && elapsed_us < 33333u) {
    return;
  }

  const uint32_t plane_count = LoadGuestU32(base, args.r3 + 276u);
  const uint32_t plane_base = LoadGuestU32(base, args.r3 + 280u);
  const uint32_t plane_index = args.r4;
  const uint32_t plane_guest =
      plane_index < plane_count && plane_base != 0
          ? plane_base + plane_index * kStartupBinkPlaneBytes
          : 0u;
  auto plane_word = [&](uint32_t offset) -> uint32_t {
    if (plane_guest == 0 ||
        !IsReadableGuestByteRange(base, plane_guest + offset, 4u)) {
      return 0;
    }
    return LoadGuestU32(base, plane_guest + offset);
  };
  auto word = [&](uint32_t guest, uint32_t offset) -> uint32_t {
    if (guest == 0 || !IsReadableGuestByteRange(base, guest + offset, 4u)) {
      return 0;
    }
    return LoadGuestU32(base, guest + offset);
  };

  const uint32_t after_write = plane_word(0u);
  const uint32_t after_read = plane_word(24u);
  const uint32_t after_fill = StartupBinkRingFill(base, plane_guest);
  REXLOG_INFO(
      "Refii startup BIK bitstream copy #{} lr=0x{:08X} movie=0x{:08X} "
      "plane_index={} plane=0x{:08X} result=0x{:08X} elapsed_us={} "
      "packet=0x{:08X} packet_bytes={} bounds=0x{:08X}-0x{:08X} "
      "ring=0x{:08X}-0x{:08X} size={} threshold={} frame_gate={} "
      "hi_byte={} write=0x{:08X}->0x{:08X} read=0x{:08X}->0x{:08X} "
      "fill={}->{} window={} consumed_cb=0x{:08X}",
      count, args.lr, args.r3, plane_index, plane_guest, ctx.r3.u32,
      elapsed_us, args.r6, word(args.r6, 0u), args.r7, args.r8,
      plane_word(12u), plane_word(16u), plane_word(8u), plane_word(48u),
      plane_word(68u), plane_word(88u), before_write, after_write,
      before_read, after_read, before_fill, after_fill, plane_word(220u),
      plane_word(356u));
}

void LogStartupBinkAudioTrace(const char *symbol,
                              const RendererHookArgs &args,
                              const PPCContext &ctx, uint8_t *base) {
  if (!RendererVerboseTracingEnabled()) {
    return;
  }

  static std::mutex mutex;
  static std::unordered_map<std::string, uint32_t> counts;

  uint32_t count = 0;
  {
    std::lock_guard<std::mutex> lock(mutex);
    count = ++counts[std::string(symbol)];
  }
  if (count > 12u && (count % 120u) != 0u) {
    return;
  }

  auto word = [&](uint32_t guest, uint32_t offset) -> uint32_t {
    if (guest == 0 || !IsReadableGuestByteRange(base, guest + offset, 4u)) {
      return 0;
    }
    return LoadGuestU32(base, guest + offset);
  };

  REXLOG_INFO("Refii startup BIK audio trace {} #{} lr=0x{:08X} "
              "args=0x{:08X}/0x{:08X}/0x{:08X}/0x{:08X}/0x{:08X}/0x{:08X} "
              "result=0x{:08X} "
              "r3_words={:08X}/{:08X}/{:08X}/{:08X}/{:08X}/{:08X} "
              "r4_words={:08X}/{:08X}/{:08X}/{:08X}/{:08X}/{:08X}",
              symbol, count, args.lr, args.r3, args.r4, args.r5, args.r6,
              args.r7, args.r8, ctx.r3.u32, word(args.r3, 0u),
              word(args.r3, 4u), word(args.r3, 8u), word(args.r3, 12u),
              word(args.r3, 16u), word(args.r3, 20u), word(args.r4, 0u),
              word(args.r4, 4u), word(args.r4, 8u), word(args.r4, 12u),
              word(args.r4, 16u), word(args.r4, 20u));
}

void LogStartupBinkCallbackTrace(const char *symbol,
                                 const RendererHookArgs &args,
                                 const PPCContext &ctx, uint8_t *base) {
  if (!RendererVerboseTracingEnabled()) {
    return;
  }

  static std::mutex mutex;
  static std::unordered_map<std::string, uint32_t> counts;

  uint32_t count = 0;
  {
    std::lock_guard<std::mutex> lock(mutex);
    count = ++counts[std::string(symbol)];
  }
  if (count > 16u && (count % 120u) != 0u) {
    return;
  }

  auto word = [&](uint32_t guest, uint32_t offset) -> uint32_t {
    if (guest == 0 || !IsReadableGuestByteRange(base, guest + offset, 4u)) {
      return 0;
    }
    return LoadGuestU32(base, guest + offset);
  };

  REXLOG_INFO("Refii startup BIK callback trace {} #{} lr=0x{:08X} "
              "args=0x{:08X}/0x{:08X}/0x{:08X}/0x{:08X} result=0x{:08X} "
              "r3_words={:08X}/{:08X}/{:08X}/{:08X}/{:08X}/{:08X} "
              "r4_words={:08X}/{:08X}/{:08X}/{:08X}/{:08X}/{:08X}",
              symbol, count, args.lr, args.r3, args.r4, args.r5, args.r6,
              ctx.r3.u32, word(args.r3, 0u), word(args.r3, 4u),
              word(args.r3, 8u), word(args.r3, 12u), word(args.r3, 16u),
              word(args.r3, 20u), word(args.r4, 0u), word(args.r4, 4u),
              word(args.r4, 8u), word(args.r4, 12u), word(args.r4, 16u),
              word(args.r4, 20u));
}

void LogStartupBinkSoundCallbackTrace(const char *symbol,
                                      const RendererHookArgs &args,
                                      const PPCContext &ctx, uint8_t *base) {
  if (!RendererVerboseTracingEnabled()) {
    return;
  }

  static std::atomic<uint32_t> count_source{0};
  const uint32_t count =
      count_source.fetch_add(1, std::memory_order_relaxed) + 1u;
  if (count > 16u && (count % 120u) != 0u) {
    return;
  }

  const uint32_t movie_guest = args.r3 >= 852u ? args.r3 - 852u : 0u;
  const bool movie_readable =
      movie_guest != 0 &&
      IsReadableGuestByteRange(base, movie_guest, kStartupBinkMovieBytes);
  auto movie_word = [&](uint32_t offset) -> uint32_t {
    return movie_readable ? LoadGuestU32(base, movie_guest + offset) : 0u;
  };

  REXLOG_INFO("Refii startup BIK sound callback {} #{} lr=0x{:08X} "
              "callback_arg=0x{:08X} value=0x{:08X} movie=0x{:08X} "
              "result=0x{:08X} frames={}/{} planes={} field276={} field848={} "
              "wait0=0x{:08X} wait1=0x{:08X} fence=0x{:08X}",
              symbol, count, args.lr, args.r3, args.r4, movie_guest, ctx.r3.u32,
              movie_word(12u), movie_word(692u), movie_word(276u),
              movie_word(276u), movie_word(848u), movie_word(704u),
              movie_word(856u), movie_word(836u));
}

} // namespace refii::renderer::bink
