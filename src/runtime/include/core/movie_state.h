// refii — the movie the guest states, as the host reads it (src/runtime/src/core/movie_state.cpp).
//
// A title's Bink hooks write it through the refii_bridge_movie_* entries in core/payload_bridge.h;
// the host bridge's movie exports read it here. Ordinary C++ symbols: the host and refii_runtime
// are both our build.

#pragma once

#include <cstdint>

namespace refii::movie {

/// The last request's .bik in guest memory (0, 0 when it named a path), whether its words are
/// swapped, and the counter that changes on every request, a repeat of the same movie included.
void Requested(uint32_t* out_address, uint32_t* out_size, uint32_t* out_generation,
               uint32_t* out_swapped);

/// The last request's path, or empty when it carried bytes. Returns the length written.
uint32_t RequestedName(char* out, uint32_t capacity);

/// The live movie's sound: whether audible, its track ids and their linear volumes.
uint32_t Sound(uint32_t* out_sound, uint32_t* out_ids, float* out_volumes, uint32_t capacity);

/// 0 no live movie, 1 the guest still wants it, 2 the guest has skipped it.
uint32_t State(const uint8_t* base);

/// The host's copy has ended: writes the live player's done byte, as the guest's own decode
/// would, and retires it. Returns the player retired, or 0.
uint32_t Finish(uint8_t* base);

/// The rect the guest stated for the live player; 0 when none is stated yet.
uint32_t Rect(float* out_x0, float* out_y0, float* out_x1, float* out_y1);

}  // namespace refii::movie
