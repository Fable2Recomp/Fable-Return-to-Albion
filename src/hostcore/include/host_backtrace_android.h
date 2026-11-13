#pragma once

// The host's native backtrace on a phone below API33, which has no execinfo (host_backtrace.h).
// Built on Android only (host_backtrace_android.cpp).

namespace refii_host_backtrace {

/// The calling thread's return addresses, innermost first, from its frame-pointer chain (the
/// phone build keeps frame pointers). Async-signal-safe: no locks, no heap, and every read goes
/// through process_vm_readv, so a broken chain ends the walk instead of faulting.
int Capture(void** frames, int capacity);

/// One line per frame on `fd`: index, module offset, module and nearest exported symbol.
/// dladdr takes the loader lock, so call it after the raw frames are captured.
void WriteSymbols(void* const* frames, int count, int fd);

}  // namespace refii_host_backtrace
