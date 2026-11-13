#pragma once

// The host's own native backtrace, stated once for the crash record and the hang watchdog:
// Capture takes the calling thread's return addresses, WriteSymbols names them on a descriptor.
// execinfo's backtrace() where the platform has it. Bionic's starts at API33, and Android
// resolves strong imports eagerly, so an API31 build that referenced it would not load: that
// build walks frame pointers instead (host_backtrace_android.h).

#if defined(_WIN32)
#define REFII_HOST_BACKTRACE 0
#else
#define REFII_HOST_BACKTRACE 1
#if defined(__ANDROID__) && __ANDROID_API__ < 33
#include "host_backtrace_android.h"
#else
#include <execinfo.h>
namespace refii_host_backtrace {
inline int Capture(void** frames, int capacity) { return ::backtrace(frames, capacity); }
inline void WriteSymbols(void* const* frames, int count, int fd) { ::backtrace_symbols_fd(frames, count, fd); }
}  // namespace refii_host_backtrace
#endif
#endif
