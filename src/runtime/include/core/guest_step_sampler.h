// refii — the guest step sampler's host-side reader (src/core/guest_step_sampler.cpp).
//
// A title's tick pump states each HF step through the C entries in core/payload_bridge.h; this
// is what the rest of refii_runtime reads back. Ordinary C++: both sides are our build.

#pragma once

#include <cstdint>

namespace refii::pace {

/// Whether a stated step has been open longer than `step_ticks` of the guest tick clock at
/// `real_now` (both in guest ticks, rex::chrono::Clock::QueryGuestTickCount). Lock-free: two
/// relaxed atomic loads. False when no title states steps.
bool StepBlocked(uint64_t real_now, uint64_t step_ticks);

/// The back-chain rule of the walk, pure: `back` (the word at [sp]) is the next frame when it
/// ascends from `sp`, is 8-aligned, and lies inside [stack_low, stack_high) with the saved LR at
/// back - 8 and the next back-chain word at back both inside. Anything else ends the walk.
bool AcceptBackChain(uint32_t sp, uint32_t back, uint32_t stack_low, uint32_t stack_high);

}  // namespace refii::pace
