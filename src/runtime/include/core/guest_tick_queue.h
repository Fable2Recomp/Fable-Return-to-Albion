// refii — the guest tick queue's host end (src/runtime/src/core/guest_tick_queue.cpp).
//
// The host posts work by name; a title's tick pump hook drains it on the guest's own tick
// thread. Ordinary C++ symbols: the host and refii_runtime are both our build.

#pragma once

#include <cstdint>

namespace refii::vfs {

uint32_t PostGuestTickAction(const char* name);
void GuestTickPumpCounters(uint64_t* out_ticks, uint64_t* out_drained, uint32_t* out_queued);

}  // namespace refii::vfs
