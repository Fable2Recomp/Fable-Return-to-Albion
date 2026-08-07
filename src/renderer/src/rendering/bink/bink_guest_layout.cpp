#include "runtime/refii_renderer/bik/bink_guest_layout.h"

#include "runtime/refii_renderer/bik/bink_guest_memory.h"

#include <algorithm>

namespace refii::renderer::bink {

bool LooksLikeBinkMovie(uint8_t *base, uint32_t movie_guest) {
  if (base == nullptr || movie_guest == 0 ||
      !IsReadableGuestByteRange(base, movie_guest, kBinkMovieBytes)) {
    return false;
  }

  const uint32_t plane_count = LoadGuestU32(base, movie_guest + 276u);
  const uint32_t plane_base = LoadGuestU32(base, movie_guest + 280u);
  const uint32_t frame_count = LoadGuestU32(base, movie_guest + 8u);
  const uint32_t rate = LoadGuestU32(base, movie_guest + 20u);
  const uint32_t divisor = LoadGuestU32(base, movie_guest + 24u);
  if (plane_count == 0 || plane_count > 4u || plane_base == 0 ||
      frame_count == 0 || rate == 0 || divisor == 0) {
    return false;
  }

  for (uint32_t plane_index = 0; plane_index < plane_count; ++plane_index) {
    const uint32_t plane_guest =
        plane_base + plane_index * kStartupBinkPlaneBytes;
    if (!IsReadableGuestByteRange(base, plane_guest, kBinkPlaneBytes) ||
        LoadGuestU32(base, plane_guest + 348u) != 0x832BDEA0u ||
        LoadGuestU32(base, plane_guest + 352u) != 0x832BE060u ||
        LoadGuestU32(base, plane_guest + 356u) != 0x832BE238u) {
      return false;
    }
  }

  return true;
}

bool BinkMovieFinished(uint8_t *base, uint32_t movie_guest) {
  if (base == nullptr || movie_guest == 0 ||
      !IsReadableGuestByteRange(base, movie_guest, kBinkMovieBytes)) {
    return false;
  }

  const uint32_t frame = LoadGuestU32(base, movie_guest + 12u);
  const uint32_t frame_count = LoadGuestU32(base, movie_guest + 8u);
  return frame_count != 0 && frame >= frame_count;
}

std::chrono::nanoseconds BinkMovieFrameInterval(uint8_t *base,
                                                uint32_t movie_guest) {
  constexpr std::chrono::nanoseconds kDefaultFrameInterval(33333333);
  if (base == nullptr || movie_guest == 0 ||
      !IsReadableGuestByteRange(base, movie_guest, 28u)) {
    return kDefaultFrameInterval;
  }

  const uint32_t rate = LoadGuestU32(base, movie_guest + 20u);
  const uint32_t divisor = LoadGuestU32(base, movie_guest + 24u);
  if (rate == 0 || divisor == 0) {
    return kDefaultFrameInterval;
  }

  const uint64_t ns = (1000000000ull * divisor + (rate / 2u)) / rate;
  constexpr uint64_t kMinIntervalNs = 1000000000ull / 240ull;
  constexpr uint64_t kMaxIntervalNs = 1000000000ull;
  return std::chrono::nanoseconds(
      std::clamp<uint64_t>(ns, kMinIntervalNs, kMaxIntervalNs));
}

uint32_t BinkRingFill(uint8_t *base, uint32_t plane_guest) {
  if (base == nullptr || plane_guest == 0 ||
      !IsReadableGuestByteRange(base, plane_guest, kBinkPlaneBytes)) {
    return 0;
  }

  const uint32_t write_guest = LoadGuestU32(base, plane_guest + 0u);
  const uint32_t read_guest = LoadGuestU32(base, plane_guest + 24u);
  const uint32_t ring_size = LoadGuestU32(base, plane_guest + 8u);
  if (ring_size == 0) {
    return 0;
  }

  uint32_t fill = write_guest - read_guest;
  if (static_cast<int32_t>(fill) < 0) {
    fill += ring_size;
  }
  return fill;
}



bool TryGetBinkPlaneWriteWindow(uint8_t *base, uint32_t plane_guest,
                                uint32_t &dst_guest, uint32_t &byte_count) {
  if (base == nullptr || plane_guest == 0 ||
      !IsReadableGuestByteRange(base, plane_guest, kBinkPlaneBytes)) {
    return false;
  }

  const uint32_t read_size = LoadGuestU32(base, plane_guest + 220u);
  const uint32_t read_offset = LoadGuestU32(base, plane_guest + 216u);
  const uint32_t ring_size = LoadGuestU32(base, plane_guest + 188u);
  const uint32_t ring_base = LoadGuestU32(base, plane_guest + 184u);
  if (ring_base == 0 || ring_size == 0 || read_offset >= ring_size ||
      read_size == 0) {
    return false;
  }

  byte_count = read_size;
  if (read_offset + byte_count > ring_size) {
    byte_count = ring_size - read_offset;
  }
  dst_guest = ring_base + read_offset;
  return byte_count != 0;
}


} // namespace refii::renderer::bink
