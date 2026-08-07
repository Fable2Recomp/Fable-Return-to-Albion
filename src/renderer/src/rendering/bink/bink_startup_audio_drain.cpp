#include "runtime/refii_renderer/bik/bink_startup_audio_drain.h"

#include "runtime/refii_renderer/bik/bink_guest_layout.h"
#include "runtime/refii_renderer/bik/bink_guest_memory.h"
#include "runtime/refii_renderer/bik/bink_movie_clock.h"
#include "runtime/refii_renderer/bik/bink_startup_audio_pump.h"
#include "runtime/refii_renderer/bik/bink_startup_ownership.h"
#include "renderer_runtime.h"
#include "../../include/hooks/ppc_externs.h"

#include <atomic>
#include <algorithm>
#include <chrono>
#include <mutex>
#include <unordered_map>

#include <rex/logging.h>

namespace refii::renderer {
bool RendererBinkRealtimeAudioClockEnabled();
} // namespace refii::renderer

namespace refii::renderer::bink {
namespace {

struct StartupPumpMovieKey {
  uint8_t *base = nullptr;
  uint32_t movie_guest = 0;

  bool operator==(const StartupPumpMovieKey &other) const {
    return base == other.base && movie_guest == other.movie_guest;
  }
};

struct StartupPumpMovieKeyHash {
  size_t operator()(const StartupPumpMovieKey &key) const {
    return (reinterpret_cast<uintptr_t>(key.base) >> 4u) ^
           static_cast<size_t>(key.movie_guest);
  }
};

struct StartupPumpMovieSignature {
  uint32_t total_frames = 0;
  uint32_t rate = 0;
  uint32_t divisor = 0;
  uint32_t plane_base = 0;
};

std::mutex g_startup_pump_movie_mutex;
std::unordered_map<StartupPumpMovieKey, StartupPumpMovieSignature,
                   StartupPumpMovieKeyHash>
    g_startup_pump_movies;

StartupPumpMovieSignature CurrentStartupPumpMovieSignature(uint8_t *base,
                                                           uint32_t movie) {
  StartupPumpMovieSignature signature = {};
  if (base == nullptr || movie == 0 ||
      !IsReadableGuestByteRange(base, movie, kBinkMovieBytes)) {
    return signature;
  }

  signature.total_frames = LoadGuestU32(base, movie + 8u);
  signature.rate = LoadGuestU32(base, movie + 20u);
  signature.divisor = LoadGuestU32(base, movie + 24u);
  signature.plane_base = LoadGuestU32(base, movie + 280u);
  return signature;
}

bool SameStartupPumpMovieSignature(
    const StartupPumpMovieSignature &lhs,
    const StartupPumpMovieSignature &rhs) {
  return lhs.total_frames != 0 && lhs.total_frames == rhs.total_frames &&
         lhs.rate == rhs.rate && lhs.divisor == rhs.divisor &&
         lhs.plane_base == rhs.plane_base;
}

void RememberStartupPumpMovie(uint8_t *base, uint32_t movie) {
  if (!refii::renderer::FableStartupVideoPumpActive() || base == nullptr ||
      movie == 0 || !LooksLikeBinkMovie(base, movie)) {
    return;
  }

  std::lock_guard<std::mutex> lock(g_startup_pump_movie_mutex);
  g_startup_pump_movies[StartupPumpMovieKey{base, movie}] =
      CurrentStartupPumpMovieSignature(base, movie);
}

bool IsRetiredStartupPumpMovie(uint8_t *base, uint32_t movie) {
  if (!refii::renderer::FableStartupVideoPumpCompletedForHandoff() ||
      refii::renderer::FableStartupVideoPumpActive() || base == nullptr ||
      movie == 0 || !IsReadableGuestByteRange(base, movie, kBinkMovieBytes)) {
    return false;
  }

  std::lock_guard<std::mutex> lock(g_startup_pump_movie_mutex);
  const auto it = g_startup_pump_movies.find(StartupPumpMovieKey{base, movie});
  return it != g_startup_pump_movies.end() &&
         SameStartupPumpMovieSignature(
             it->second, CurrentStartupPumpMovieSignature(base, movie));
}

uint32_t RingDistance(uint32_t write, uint32_t read, uint32_t size) {
  if (size == 0) {
    return 0;
  }
  return write >= read ? write - read : write + size - read;
}

uint32_t AdvanceRingAddress(uint32_t address, uint32_t step,
                            uint32_t ring_base, uint32_t ring_end) {
  if (ring_base == 0 || ring_end <= ring_base) {
    return address + step;
  }
  const uint32_t ring_size = ring_end - ring_base;
  uint32_t next = address + step;
  while (next >= ring_end) {
    next = ring_base + (next - ring_end);
  }
  if (next < ring_base) {
    next = ring_base + ((next - ring_base) % ring_size);
  }
  return next;
}

uint32_t AdvanceRingOffset(uint32_t offset, uint32_t step,
                           uint32_t ring_size) {
  if (ring_size == 0) {
    return offset + step;
  }
  return (offset + step) % ring_size;
}

bool CopyBinkAudioPlaneBytes(uint8_t *base, uint32_t plane_guest,
                             uint32_t source_bytes,
                             uint32_t output_bytes) {
  if (base == nullptr || plane_guest == 0 || output_bytes == 0 ||
      !IsReadableGuestByteRange(base, plane_guest, kBinkPlaneBytes)) {
    return false;
  }

  const bool high_byte_plane = LoadGuestU32(base, plane_guest + 88u) != 0;
  const uint32_t source_base = LoadGuestU32(base, plane_guest + 12u);
  const uint32_t source_end = LoadGuestU32(base, plane_guest + 16u);
  const uint32_t output_base = LoadGuestU32(base, plane_guest + 184u);
  const uint32_t output_size = LoadGuestU32(base, plane_guest + 188u);
  if (source_base == 0 || source_end <= source_base || output_base == 0 ||
      output_size == 0) {
    return false;
  }

  uint32_t source_read = LoadGuestU32(base, plane_guest + 24u);
  uint32_t output_read = LoadGuestU32(base, plane_guest + 216u);
  if (source_read < source_base || source_read >= source_end ||
      output_read >= output_size) {
    return false;
  }

  const uint32_t source_step = high_byte_plane ? 2u : 1u;
  if (high_byte_plane && source_bytes < output_bytes * 2u) {
    return false;
  }

  for (uint32_t i = 0; i < output_bytes; ++i) {
    const uint8_t value = LoadGuestU8(base, source_read);
    const uint8_t output_value =
        high_byte_plane ? static_cast<uint8_t>(value + 0x80u) : value;
    StoreGuestU8(base, output_base + output_read, output_value);
    source_read = AdvanceRingAddress(source_read, source_step, source_base,
                                     source_end);
    output_read = AdvanceRingOffset(output_read, 1u, output_size);
  }

  StoreGuestU32(base, plane_guest + 24u, source_read);
  return true;
}

bool TryDrainBinkAudioPlaneBudgeted(const RendererHookArgs &args,
                                    PPCContext &ctx, uint8_t *base,
                                    uint32_t movie_guest,
                                    uint32_t plane_guest) {
  if (base == nullptr || movie_guest == 0 || plane_guest == 0 ||
      !IsReadableGuestByteRange(base, movie_guest, kBinkMovieBytes) ||
      !IsReadableGuestByteRange(base, plane_guest, kBinkPlaneBytes)) {
    return false;
  }

  const bool realtime_clock =
      refii::renderer::RendererBinkRealtimeAudioClockEnabled();
  if (realtime_clock) {
    // The native Bink voice service (sub_832BDEA0) is the authority on the
    // output ring's clock: it queries the REAL device play cursor (with its
    // stability re-read loop), caches it at plane+228, publishes the
    // 4-aligned write window at plane+220, and fills underruns with SILENCE
    // so the looping voice buffer never replays stale audio (the "double
    // audio" failure). Our copy below only feeds fresh source bytes into
    // the window it computed — no synthetic clock, no budget pacing.
    PPCContext service_ctx = ctx;
    service_ctx.r3.u32 = plane_guest;
    __imp__sub_832BDEA0(service_ctx, base);
  } else if (!StartupAudioDrainBudgetAvailableForPlane(plane_guest)) {
    PrimeStartupAudioDrainBudgetForMovie(base, movie_guest);
  }

  const bool high_byte_plane = LoadGuestU32(base, plane_guest + 88u) != 0;
  const uint32_t source_budget =
      realtime_clock ? UINT32_MAX
                     : StartupAudioDrainSourceBudgetForPlane(plane_guest,
                                                             high_byte_plane);
  if (source_budget == 0) {
    return false;
  }

  const uint32_t source_write = LoadGuestU32(base, plane_guest + 0u);
  const uint32_t source_read = LoadGuestU32(base, plane_guest + 24u);
  const uint32_t source_size = LoadGuestU32(base, plane_guest + 8u);
  const uint32_t source_base = LoadGuestU32(base, plane_guest + 12u);
  const uint32_t source_end = LoadGuestU32(base, plane_guest + 16u);
  if (source_size == 0 || source_base == 0 || source_end <= source_base) {
    return false;
  }

  uint32_t available = RingDistance(source_write, source_read, source_size);
  if (available <= LoadGuestU32(base, plane_guest + 48u) &&
      (LoadGuestU32(base, movie_guest + 12u) <=
           LoadGuestU32(base, plane_guest + 68u) ||
       available == 0)) {
    return false;
  }

  uint32_t dst_guest = 0;
  uint32_t output_bytes = 0;
  const uint32_t output_size = LoadGuestU32(base, plane_guest + 188u);
  const uint32_t pending_before = LoadGuestU32(base, plane_guest + 220u);
  if (output_size == 0 || pending_before > output_size) {
    StoreGuestU32(base, plane_guest + 220u, 0u);
    return false;
  }
  if (!TryGetBinkPlaneWriteWindow(base, plane_guest, dst_guest,
                                  output_bytes) ||
      output_bytes == 0) {
    // Real-time clock: sub_832BDEA0 just computed the window from the REAL
    // device cursor — an empty window means the device genuinely has no
    // free ring space yet. Fabricating a cursor jump here forges audio
    // time and laps playback (doubled/overlapping audio).
    if (realtime_clock) {
      return false;
    }
    const uint32_t budget =
        StartupAudioDrainOutputBudgetForPlane(plane_guest);
    if (budget == 0 ||
        !PublishStartupAudioDrainBudgetForPlane(base, plane_guest, budget) ||
        !TryGetBinkPlaneWriteWindow(base, plane_guest, dst_guest,
                                    output_bytes)) {
      return false;
    }
  }
  const uint32_t pending_window = LoadGuestU32(base, plane_guest + 220u);
  if (pending_window == 0 || pending_window > output_size) {
    StoreGuestU32(base, plane_guest + 220u, 0u);
    return false;
  }
  output_bytes = std::min<uint32_t>(output_bytes, pending_window);

  uint32_t source_bytes = high_byte_plane ? output_bytes * 2u : output_bytes;
  source_bytes = std::min<uint32_t>(source_bytes, available);
  source_bytes = std::min<uint32_t>(source_bytes, source_budget);
  if (LoadGuestU32(base, movie_guest + 12u) <=
      LoadGuestU32(base, plane_guest + 68u)) {
    source_bytes &= LoadGuestU32(base, plane_guest + 52u)
                    << (high_byte_plane ? 1u : 0u);
  }
  if (high_byte_plane) {
    source_bytes &= ~1u;
    output_bytes = std::min<uint32_t>(output_bytes, source_bytes >> 1u);
  } else {
    output_bytes = std::min<uint32_t>(output_bytes, source_bytes);
  }
  // Keep ring writes sample-frame aligned: the real-time window can be any
  // byte count, and an off-by-one write shears every later sample into
  // noise.
  const uint32_t sample_frame = std::clamp<uint32_t>(
      std::max<uint32_t>(LoadGuestU32(base, plane_guest + 44u), 1u) *
          (high_byte_plane
               ? 1u
               : std::max<uint32_t>(
                     LoadGuestU32(base, plane_guest + 40u) / 8u, 1u)),
      1u, 64u);
  if (sample_frame > 1u) {
    output_bytes -= output_bytes % sample_frame;
    source_bytes = high_byte_plane ? output_bytes * 2u : output_bytes;
  }
  if (output_bytes == 0 || source_bytes == 0) {
    return false;
  }

  if (!CopyBinkAudioPlaneBytes(base, plane_guest, source_bytes,
                               output_bytes)) {
    return false;
  }

  PPCContext done_ctx = ctx;
  done_ctx.r3.u32 = plane_guest;
  done_ctx.r4.u32 = output_bytes;
  __imp__sub_832BE238(done_ctx, base);
  ConsumeStartupAudioDrainBudgetForPlane(plane_guest, output_bytes);
  ctx.r3.u32 = 1u;
  (void)args;
  return true;
}

} // namespace

bool TryRunBinkNativeAudioDrain(const RendererHookArgs &args, PPCContext &ctx,
                                uint8_t *base) {
  if (base == nullptr || args.r3 == 0 ||
      !IsReadableGuestByteRange(base, args.r3, kBinkMovieBytes)) {
    return false;
  }
  RememberStartupPumpMovie(base, args.r3);
  RememberStartupActiveMovie(base, args.r3);
  refii::renderer::NotifyFableStartupVideoBinkMovieActive(base, args.r3);
  RegisterStartupAudioCursorBridgesForMovie(base, args.r3);

  if (BinkMovieFinished(base, args.r3) ||
      IsRetiredStartupPumpMovie(base, args.r3)) {
    refii::renderer::NotifyFableStartupVideoBinkMovieFinished(base, args.r3);
    ClearMovie(args.r3);
    ClearStartupAudioCursorsForMovie(base, args.r3);
    return false;
  }

  const uint32_t plane_count = LoadGuestU32(base, args.r3 + 276u);
  const uint32_t plane_base = LoadGuestU32(base, args.r3 + 280u);
  if (plane_count == 0 || plane_count > 4u || plane_base == 0) {
    return false;
  }

  bool drained_any = false;
  for (uint32_t plane_index = 0; plane_index < plane_count; ++plane_index) {
    drained_any |= TryDrainBinkAudioPlaneBudgeted(
        args, ctx, base, args.r3,
        plane_base + plane_index * kStartupBinkPlaneBytes);
  }
  if (!drained_any) {
    ctx.r3.u32 = 0;
  }
  return drained_any;
}

} // namespace refii::renderer::bink
