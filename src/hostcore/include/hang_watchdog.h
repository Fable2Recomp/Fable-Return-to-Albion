#pragma once

// The hang watchdog: a thread of the bridge's own that notices when the HOST's main thread
// stops stamping frames and writes a report nobody else can take once ptrace is refused
// (yama ptrace_scope=1, dotnet-dump deadlocks, the debug channel is served by the stalled
// thread itself). Exports live in hang_watchdog.cpp; this header is the bridge's internal
// seam to it.

namespace refii_hang_watchdog {

/// Writes the guest's kernel thread table (tid, flags, name, r1, lr, back-chain frames) into
/// `fd` with write(2) only. Supplied by host_bridge.cpp, which owns the rex:: headers; the
/// watchdog calls it LAST in a report because the object table takes a lock.
using GuestTableWriter = void (*)(int fd);

/// Registers the guest table writer. Called once at bridge start; safe to call again.
void SetGuestTableWriter(GuestTableWriter writer);

}  // namespace refii_hang_watchdog
