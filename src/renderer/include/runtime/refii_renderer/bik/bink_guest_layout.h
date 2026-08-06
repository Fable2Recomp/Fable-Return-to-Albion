#pragma once

#include <chrono>
#include <cstdint>

namespace refii::renderer::bink {

constexpr uint32_t kStartupBinkMovieBytes = 860u;
constexpr uint32_t kStartupBinkPlaneBytes = 388u;
constexpr uint32_t kBinkMovieBytes = kStartupBinkMovieBytes;
constexpr uint32_t kBinkPlaneBytes = kStartupBinkPlaneBytes;

bool LooksLikeBinkMovie(uint8_t *base, uint32_t movie_guest);
bool BinkMovieFinished(uint8_t *base, uint32_t movie_guest);

std::chrono::nanoseconds BinkMovieFrameInterval(uint8_t *base,
                                                uint32_t movie_guest);

uint32_t BinkRingFill(uint8_t *base, uint32_t plane_guest);


bool TryGetBinkPlaneWriteWindow(uint8_t *base, uint32_t plane_guest,
                                uint32_t &dst_guest, uint32_t &byte_count);

inline bool LooksLikeStartupBinkMovie(uint8_t *base, uint32_t movie_guest) {
  return LooksLikeBinkMovie(base, movie_guest);
}

inline std::chrono::nanoseconds StartupBinkMovieFrameInterval(
    uint8_t *base, uint32_t movie_guest) {
  return BinkMovieFrameInterval(base, movie_guest);
}

inline uint32_t StartupBinkRingFill(uint8_t *base, uint32_t plane_guest) {
  return BinkRingFill(base, plane_guest);
}

} // namespace refii::renderer::bink
