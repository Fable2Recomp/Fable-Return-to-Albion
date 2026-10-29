// refii — back-off policy for the guest's SwitchToThread spin waits.
//
// The title spin-waits by yielding. On the 360 each of those loops owned a hardware
// thread and NtYieldExecution cost nothing it was not already paying; on a host,
// rex::thread::MaybeYield() is sched_yield() plus a full barrier, measured at 449 ns
// on an otherwise idle machine. Two guest loops sustained 2.88 M yields/s for a whole
// session — ~1.3 cores burned, and the command-stream consumer at 0x822DFE08 spends
// them competing with the very producer thread it is waiting on.
//
// NOT KEYED ON A CALL SITE. Progress is inferred from the link register and the
// interval between yields, so any guest loop that spins gets the same treatment and
// none of this has to be re-derived per spin.

#pragma once

#include <cstdint>

namespace refii::vfs {

// Called on every guest SwitchToThread, BEFORE the real yield.
//
// Sleeps when this thread has yielded from the same place, back to back, long enough
// that the wait is plainly not a short handoff. Returns without sleeping in the
// ordinary case, which must stay a plain yield so a real handoff keeps its latency.
//
// This only ADDS a delay: the guest's SwitchToThread still runs, so the yield and its
// barrier keep their semantics. The guest's own loops keep their own exit conditions
// and timeouts — this changes how the waiting is spent, never how long it lasts.
void MaybeBackOffBeforeGuestYield(uint32_t lr);

// How many of those calls escalated to a sleep, process-wide. Reported by the spin probe
// so one log line shows both the yield count and how much of it the back-off absorbed.
uint64_t GuestYieldBackoffSleepCount();

}  // namespace refii::vfs
