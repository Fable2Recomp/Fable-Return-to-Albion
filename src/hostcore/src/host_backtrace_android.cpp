// refii — the host's native backtrace on a phone below API33 (host_backtrace_android.h).
// Built on Android only.

#include "host_backtrace.h"

#if __ANDROID_API__ < 33

#include <dlfcn.h>
#include <sys/uio.h>
#include <unistd.h>

#include <cstdint>
#include <cstdio>
#include <cstring>

namespace refii_host_backtrace {
namespace {

/// Reads 16 bytes of this process's memory without faulting: false if any of it is unmapped.
bool ReadPair(uintptr_t address, uintptr_t out[2]) {
    iovec local{out, 2 * sizeof(uintptr_t)};
    iovec remote{reinterpret_cast<void*>(address), 2 * sizeof(uintptr_t)};
    return ::process_vm_readv(::getpid(), &local, 1, &remote, 1, 0) ==
           static_cast<ssize_t>(2 * sizeof(uintptr_t));
}

/// A saved return address without a pointer-authentication signature (XPACLRI is a no-op
/// before ARMv8.3) or a top-byte tag.
uintptr_t Strip(uintptr_t address) {
#if defined(__aarch64__)
    register uintptr_t x30 asm("x30") = address;
    asm("hint #7" : "+r"(x30));
    return x30 & ((uintptr_t{1} << 56) - 1);
#else
    return address;
#endif
}

}  // namespace

int Capture(void** frames, int capacity) {
    // Each record is {caller's fp, return address}. The kernel puts one in every signal frame,
    // so the walk crosses from a handler on the alternate stack back to the interrupted code;
    // those crossings are the only times fp may go down, and a few are allowed.
    uintptr_t fp = reinterpret_cast<uintptr_t>(__builtin_frame_address(0));
    int count = 0;
    int crossings = 0;
    while (count < capacity && fp != 0 && (fp & (sizeof(uintptr_t) - 1)) == 0) {
        uintptr_t record[2];
        if (!ReadPair(fp, record)) {
            break;
        }
        const uintptr_t ret = Strip(record[1]);
        if (ret == 0) {
            break;
        }
        frames[count++] = reinterpret_cast<void*>(ret);
        const uintptr_t next = record[0];
        if (next <= fp && ++crossings > 4) {
            break;
        }
        fp = next;
    }
    return count;
}

void WriteSymbols(void* const* frames, int count, int fd) {
    for (int i = 0; i < count; ++i) {
        const uintptr_t pc = reinterpret_cast<uintptr_t>(frames[i]);
        char line[512];
        Dl_info info{};
        if (::dladdr(frames[i], &info) != 0 && info.dli_fname != nullptr) {
            const char* file = std::strrchr(info.dli_fname, '/');
            file = file != nullptr ? file + 1 : info.dli_fname;
            const uintptr_t offset = pc - reinterpret_cast<uintptr_t>(info.dli_fbase);
            if (info.dli_sname != nullptr) {
                std::snprintf(line, sizeof(line), "  #%02d pc %016lx  %s (%s+%lu)\n", i,
                              static_cast<unsigned long>(offset), file, info.dli_sname,
                              static_cast<unsigned long>(pc - reinterpret_cast<uintptr_t>(info.dli_saddr)));
            } else {
                std::snprintf(line, sizeof(line), "  #%02d pc %016lx  %s\n", i,
                              static_cast<unsigned long>(offset), file);
            }
        } else {
            std::snprintf(line, sizeof(line), "  #%02d pc %016lx  (no module)\n", i,
                          static_cast<unsigned long>(pc));
        }
        const size_t length = std::strlen(line);
        size_t written = 0;
        while (written < length) {
            const ssize_t n = ::write(fd, line + written, length - written);
            if (n <= 0) {
                return;
            }
            written += static_cast<size_t>(n);
        }
    }
}

}  // namespace refii_host_backtrace

#endif
