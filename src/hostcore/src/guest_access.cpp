// refii — the host bridge's session state and its guarded access to guest memory.
//
// Every host-side read or write of guest memory goes through CopyGuest / StoreGuest: guest
// pages may be reserved, decommitted or freed under the host's feet, and a read of one must
// read zeroes rather than take the process down inside a P/Invoke.

#include <algorithm>
#include <atomic>
#include <cctype>
#include <chrono>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <csignal>
#ifndef _WIN32
#include <dlfcn.h>
#include <execinfo.h>
#include <fcntl.h>
#include <sys/syscall.h>
#include <sys/resource.h>
#include <setjmp.h>
#include <ucontext.h>
#include <unistd.h>
#include <ctime>
#endif
#include <cstring>
#include <span>
#include <exception>
#include <filesystem>
#include <fstream>
#include <set>
#include <memory>
#include <mutex>
#include <sstream>
#include <string>
#include <string_view>
#include <thread>
#include <unordered_map>
#include <vector>

#include <SDL3/SDL_events.h>

#ifdef _WIN32
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>
#include <dbghelp.h>
#endif

#include <achievement/achievement_runtime.h>
#include <rex/chrono/clock.h>
#include <rex/cvar.h>
#include <rex/logging.h>

namespace rex {
/// Defined in the runtime's logging.cpp and not declared by its header; the crash reporters
/// need it, since a record left in spdlog's queue when the process dies is never written.
void FlushLogging();
}  // namespace rex
#include <rex/system/kernel_state.h>

#include "core/host_asset_reader.h"
#include "core/precise_sleep.h"
#include "core/payload_bridge.h"
#include "core/rex_payload_bridge.h"
#include <rex/memory/utils.h>
#include <rex/system/mmio_handler.h>
#include <rex/system/xmemory.h>
#include <rex/rex_app.h>
#include <rex/ui/windowed_app.h>
#include <rex/ui/windowed_app_context_sdl.h>

#include <rex/filesystem/entry.h>
#include <rex/filesystem/vfs.h>
#include <rex/perf/counter.h>
#include <rex/runtime.h>
#include <rex/system/thread_state.h>
#include <rex/system/xsocket.h>
#include <rex/system/xthread.h>

#include <array>
#include <set>
#include <utility>
#ifdef REXGLUE_ENABLE_PROFILING
#include <tracy/Tracy.hpp>
#include <tracy/TracyC.h>
#endif

#include "host_input.h"
#include "refii_host.h"

#include "host_abi.h"
#include "guest_span.h"
#include "host_core.h"

#if defined(__aarch64__) && !defined(_WIN32)
/// Copies `size` bytes and returns 1. A leaf with no stack frame, so a fault on either access
/// can be answered by returning 0 to the caller (`fault::FixupGuardedCopy` in crash_handlers.cpp).
/// Unaligned accesses are fine on normal memory, which guest memory is.
extern "C" int refii_guarded_copy(void* dst, const void* src, std::size_t size);
extern "C" const char refii_guarded_copy_end[];
asm(R"(
    .text
    .p2align 4
    .globl refii_guarded_copy
    .hidden refii_guarded_copy
    .type refii_guarded_copy, %function
refii_guarded_copy:
    cmp x2, #16
    b.lo 2f
1:  ldp x3, x4, [x1], #16
    stp x3, x4, [x0], #16
    sub x2, x2, #16
    cmp x2, #16
    b.hs 1b
2:  tbz x2, #3, 3f
    ldr x3, [x1], #8
    str x3, [x0], #8
3:  tbz x2, #2, 4f
    ldr w3, [x1], #4
    str w3, [x0], #4
4:  tbz x2, #1, 5f
    ldrh w3, [x1], #2
    strh w3, [x0], #2
5:  tbz x2, #0, 6f
    ldrb w3, [x1]
    strb w3, [x0]
6:  mov w0, #1
    ret
    .globl refii_guarded_copy_end
    .hidden refii_guarded_copy_end
refii_guarded_copy_end:
    .size refii_guarded_copy, refii_guarded_copy_end - refii_guarded_copy
)");
#endif
#include "host_tracy.h"
#include "hang_watchdog.h"
#include "core/traced_mutex.h"
#include "core/audio_state.h"
#include "core/mod_values.h"
#include "core/event_relay.h"
#include "core/player_state.h"

using namespace refii::hostcore;

#if defined(__GNUC__) || defined(__clang__)
#pragma GCC visibility push(hidden)
#endif
namespace refii::hostcore {

int WriteOut(const std::string& text, char* out, std::size_t capacity) {
    const int needed = static_cast<int>(text.size());
    if (out != nullptr && capacity > 0) {
        const std::size_t copied = text.size() < capacity - 1 ? text.size() : capacity - 1;
        std::memcpy(out, text.data(), copied);
        out[copied] = '\0';
    }
    return needed;
}

GuestSession& Session() {
    static GuestSession session;
    return session;
}

HostedGuest& Hosted() {
    static HostedGuest hosted;
    return hosted;
}

/// Guest memory is not always THERE on Windows.
///
/// The guest space is RESERVED up front, committed as the guest touches it, and decommitted
/// as the guest frees it. Linux backs an untouched page with zeroes on read; Windows faults on
/// reserved-but-uncommitted memory, so a host peek at a global the guest has not written yet,
/// or through a pointer chain the guest has since torn down (a level change frees the old
/// "3D Engine" under `RefiiHostGetRenderFence`), takes the whole process down inside a
/// P/Invoke. Every host-side read of guest memory goes through here.
///
/// SEH rather than VirtualQuery-then-read: a query per read is a syscall the entity and UI
/// walks pay thousands of times a frame, and it still races the guest's own decommit between
/// the query and the read. A `__try` costs nothing on the path that does not fault.
///
/// A plain function with no C++ objects on purpose — neither MSVC nor clang will put SEH in a
/// frame that also needs unwinding. The copy itself is out of line so the faulting instruction
/// is a call the try range is guaranteed to cover, whatever the optimiser does with the body.
#ifdef _WIN32
__attribute__((noinline)) void CopyGuestBytesRaw(const uint8_t* source, void* out,
                                                 std::size_t size) {
    std::memcpy(out, source, size);
}

int GuestFaultFilter(unsigned long code) {
    return code == EXCEPTION_ACCESS_VIOLATION || code == EXCEPTION_IN_PAGE_ERROR
               ? EXCEPTION_EXECUTE_HANDLER
               : EXCEPTION_CONTINUE_SEARCH;
}

bool CopyGuestBytesGuarded(const uint8_t* source, void* out, std::size_t size) {
    __try {
        CopyGuestBytesRaw(source, out, size);
        return true;
    } __except (GuestFaultFilter(GetExceptionCode())) {
        return false;
    }
}

__attribute__((noinline)) void StoreGuestBytesRaw(uint8_t* target, const void* value,
                                                  std::size_t size) {
    std::memcpy(target, value, size);
}

bool StoreGuestBytesGuarded(uint8_t* target, const void* value, std::size_t size) {
    __try {
        StoreGuestBytesRaw(target, value, size);
        return true;
    } __except (GuestFaultFilter(GetExceptionCode())) {
        return false;
    }
}

/// Non-zero while a guarded read is in flight, so the trace handler below does not report
/// the fault that this path exists to absorb.
thread_local int g_guarded_guest_reads = 0;
#endif

#ifndef _WIN32
/// The Linux guard, doing what `__try` does for the Windows build above: reserved and
/// decommitted guest pages are PROT_NONE, so the read runs under a per-thread jump target the
/// last-resort handler takes when a fault lands inside it. `sigsetjmp(…, 0)` keeps this to a
/// register save; the handler unblocks the signal itself. The target is published in a slot
/// the handler finds by tid, not in TLS: this library is dlopen'd, so its TLS is dynamic and
/// a first touch allocates, which a signal handler must not do.
struct GuardSlot {
    std::atomic<long> tid{0};
    sigjmp_buf* guard = nullptr;
};
constexpr std::size_t kGuardSlots = 64;
GuardSlot g_guard_slots[kGuardSlots];
thread_local GuardSlot* t_guard_slot = nullptr;

[[maybe_unused]] GuardSlot* GuardSlotForThisThread() {
    if (t_guard_slot != nullptr) {
        return t_guard_slot;
    }
    const long tid = static_cast<long>(::syscall(SYS_gettid));
    for (GuardSlot& slot : g_guard_slots) {
        long expected = 0;
        // A free slot, or the slot a dead thread with this tid left behind.
        if (slot.tid.compare_exchange_strong(expected, tid) || expected == tid) {
            t_guard_slot = &slot;
            return &slot;
        }
    }
    return nullptr;
}

/// The guard the faulting thread published, or null. Called from the signal handler.
sigjmp_buf* GuardForFaultingThread() {
    const long tid = static_cast<long>(::syscall(SYS_gettid));
    for (GuardSlot& slot : g_guard_slots) {
        if (slot.tid.load(std::memory_order_relaxed) == tid) {
            return slot.guard;
        }
    }
    return nullptr;
}

#if defined(__aarch64__)
/// The ARM64 guard costs one call. The walks make thousands of small reads a frame, and on
/// Android a `sigsetjmp` per read (bionic fetches its setjmp cookie and saves every callee-saved
/// register) plus the dynamic-TLS slot lookup was 37% of the entity walker's samples and a tenth
/// of the main thread's. `refii_guarded_copy` is a leaf with no frame: a fault inside it is
/// answered by the handler setting the return value to 0 and the pc to the link register,
/// which is the same `false` the jump delivered, without a jump target per read.
bool CopyGuestBytesGuarded(const uint8_t* source, void* out, std::size_t size) {
    return refii_guarded_copy(out, source, size) != 0;
}

bool StoreGuestBytesGuarded(uint8_t* target, const void* value, std::size_t size) {
    return refii_guarded_copy(target, value, size) != 0;
}
#else
bool CopyGuestBytesGuarded(const uint8_t* source, void* out, std::size_t size) {
    GuardSlot* slot = GuardSlotForThisThread();
    if (slot == nullptr) {
        std::memcpy(out, source, size);
        return true;
    }
    sigjmp_buf guard;
    sigjmp_buf* previous = slot->guard;
    slot->guard = &guard;
    bool ok = false;
    if (sigsetjmp(guard, 0) == 0) {
        std::memcpy(out, source, size);
        ok = true;
    }
    slot->guard = previous;
    return ok;
}

bool StoreGuestBytesGuarded(uint8_t* target, const void* value, std::size_t size) {
    GuardSlot* slot = GuardSlotForThisThread();
    if (slot == nullptr) {
        std::memcpy(target, value, size);
        return true;
    }
    sigjmp_buf guard;
    sigjmp_buf* previous = slot->guard;
    slot->guard = &guard;
    bool ok = false;
    if (sigsetjmp(guard, 0) == 0) {
        std::memcpy(target, value, size);
        ok = true;
    }
    slot->guard = previous;
    return ok;
}
#endif  // __aarch64__
#endif

/// The first few unreadable reads are worth naming: this is the read that used to take
/// Windows down, and a stale chain that reads zero forever is a symptom worth a line.
///
/// The first twelve, then every power of two with the running count, so a stale chain read every
/// frame shows its rate instead of falling silent after the twelfth.
[[maybe_unused]] void ReportUnreadableGuestRead(uint32_t address, std::size_t size) {
    static std::atomic<uint32_t> reported{0};
    const uint32_t count = reported.fetch_add(1, std::memory_order_relaxed) + 1u;
    if (count <= 12u || (count & (count - 1u)) == 0u) {
        REXLOG_INFO(
            "Refii host: guest 0x{:08X} (+{}) is not readable — uncommitted or freed; "
            "reading zeroes instead of faulting (#{} this run)",
            address, size, count);
    }
}

/// Copies `size` guest bytes at `address` into `out`, or zero-fills and returns false when
/// they are not readable. The one read primitive everything else here is built on.
bool CopyGuest(const uint8_t* base, uint32_t address, void* out, std::size_t size) {
    if (base == nullptr) {
        std::memset(out, 0, size);
        return false;
    }

#ifdef _WIN32
    // The probe scope sits OUTSIDE the `__try` function, which may hold no C++ objects: it tells
    // ReXGlue's vectored handler — which Windows runs before any `__except` — to leave a fault
    // here to the guard instead of locking, querying and logging it (EnterHostProbe).
    ++g_guarded_guest_reads;
    rex::runtime::EnterHostProbe();
    const bool ok = CopyGuestBytesGuarded(base + address, out, size);
    rex::runtime::LeaveHostProbe();
    --g_guarded_guest_reads;
#else
    const bool ok = CopyGuestBytesGuarded(base + address, out, size);
#endif
    if (!ok) {
        std::memset(out, 0, size);
        ReportUnreadableGuestRead(address, size);
        return false;
    }
    return true;
}

bool CopyGuestPages(const uint8_t* base, uint32_t address, void* out, std::size_t size) {
    bool ok = true;
    ForEachGuestPage(address, out, size, [&](uint32_t at, uint8_t* into, std::size_t chunk) {
        ok = CopyGuest(base, at, into, chunk) && ok;
    });
    return ok;
}

/// One guest byte, or 0 when it is not readable.
uint8_t GuestByteAt(const uint8_t* base, uint32_t address) {
    uint8_t value = 0;
    CopyGuest(base, address, &value, sizeof(value));
    return value;
}

/// One guest word, big-endian, or 0 when it is not readable.
uint32_t GuestWordAt(const uint8_t* base, uint32_t address) {
    uint32_t value = 0;
    CopyGuest(base, address, &value, sizeof(value));
    return __builtin_bswap32(value);
}

/// One guest halfword, big-endian, or 0 when it is not readable.
uint16_t GuestHalfAt(const uint8_t* base, uint32_t address) {
    uint16_t value = 0;
    CopyGuest(base, address, &value, sizeof(value));
    return __builtin_bswap16(value);
}

/// One guest doubleword, big-endian, or 0 when it is not readable.
uint64_t GuestWord64At(const uint8_t* base, uint32_t address) {
    uint64_t value = 0;
    CopyGuest(base, address, &value, sizeof(value));
    return __builtin_bswap64(value);
}

/// Stores `size` bytes into guest memory, or returns false when the page is not there. The
/// few host-side writes are latches on objects the guest handed over, so a miss is a stale
/// pointer, not a reason to fault.
bool StoreGuest(uint8_t* base, uint32_t address, const void* value, std::size_t size) {
    if (base == nullptr) {
        return false;
    }

#ifdef _WIN32
    ++g_guarded_guest_reads;
    const bool ok = StoreGuestBytesGuarded(base + address, value, size);
    --g_guarded_guest_reads;
#else
    const bool ok = StoreGuestBytesGuarded(base + address, value, size);
#endif
    if (!ok) {
        ReportUnreadableGuestRead(address, size);
    }
    return ok;
}

void SetEnvironmentValue(const char* name, const char* value) {
#ifdef _WIN32
    ::_putenv_s(name, value);
#else
    ::setenv(name, value, 1);
#endif
}

}  // namespace refii::hostcore
#if defined(__GNUC__) || defined(__clang__)
#pragma GCC visibility pop
#endif
