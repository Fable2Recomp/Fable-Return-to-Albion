// refii - host bridge: the hang watchdog
//
// WHAT IT ANSWERS. Twice the host's main thread stopped mid-frame (2026-10-21 parked on a futex
// after `BUG: Unreferenced static string` with the StringName mutex held; 2026-10-23 during a
// region build while the machine swapped) and nobody could say where it was: ptrace is refused
// (yama ptrace_scope=1), dotnet-dump deadlocks in its fork, the debug channel is served by the
// stalled thread, and a background thread's GD.Print never reaches a log. This thread stands
// outside all of that and writes what a debugger would have shown.
//
// THE CONTRACT.
//   * The main thread stamps RefiiHostFrameAlive every frame (Engine_FrameStartNode/EndNode).
//     The first stamp names the main thread (its pthread_t and tid) and starts the watchdog.
//   * Phases: the host registers each frame-phase name once (RefiiHostRegisterFramePhase ->
//     index) and stamps the index on entry (RefiiHostSetFramePhase); the last 16 entries are
//     kept with their times, so a report says "in phase X for N s".
//   * Host log: NOT fed from the host. Godot's logger chain dispatches into every managed
//     Logger through the C# glue, which looks StringNames up — and the 2026-10-21 freeze printed
//     from INSIDE the non-recursive StringName mutex, so a Logger of ours would be a hang vector
//     of its own. The host states the path of its log file once (RefiiHostSetHostLogPath) and
//     the report names it; its tail is the last lines before the stall.
//   * A stall is the stamp not moving for kStallNs (8 s). A report is written once per stall,
//     then every kRepeatNs (30 s) while it persists, into `exe_dir/logs/refii_hang_<utc>.txt`
//     beside the guest logs, and summarised on stderr. Recovery is appended to the same file.
//   * The report is written with open/write only — NEVER through Godot's or the runtime's
//     logger, whose mutex the stalled thread may hold — and every buffer it uses is static,
//     so the stalled path allocates nothing and takes no lock of ours.
//
// WHAT A REPORT HOLDS, in the order it is written (so a report cut short still says the most):
//   1. the stall: frame counter, seconds since the stamp moved, phase ring, the host log's path;
//   2. memory: /proc/self/status (VmRSS, VmSwap, VmHWM, Threads), /proc/meminfo MemAvailable
//      and SwapFree, /proc/pressure/memory;
//   3. the guest's time base counters (granted / pending / watchdog lapses / dropped);
//   4. the main thread: its /proc state, wchan and syscall, then its native backtrace taken by
//      pthread_kill(main, SIGUSR2) -> a handler that calls backtrace() into a static array.
//      Raw addresses are written first, then dladdr names (dladdr takes the loader lock, so it
//      runs on THIS thread after the raw block is safe on disk). Managed (JIT) frames show as
//      addresses in no module: the C# side cannot be walked from native, and the report says so.
//   5. every thread of the process from /proc/self/task: comm, state, wchan, syscall, context
//      switch counts — a dead thread and an idle one tell apart by the switch counts between
//      two reports;
//   6. the guest's kernel thread table with back-chain frames (host_bridge.cpp's writer). This
//      goes last because the object table takes a lock: a report that ends before it names
//      the lock as held.
//
// ASYNC-SIGNAL CAVEATS, stated as the Godot crash handler's are: backtrace() unwinds through
// libgcc, whose first call loads libgcc_s through the dynamic loader (primed here on a healthy
// thread, as host_bridge.cpp does for the fault handler); the unwinder's dl_iterate_phdr takes
// the loader's write lock, which is recursive for the owning thread but blocks if ANOTHER thread
// holds it — the watchdog waits at most kCaptureWaitNs for the handler and reports a timeout
// instead of hanging itself. SA_RESTART keeps a stalled futex/read restarting after the signal;
// poll/nanosleep return EINTR regardless, and the stalled thread, if it was in one, sees that.

#include "hang_watchdog.h"
#include "host_abi.h"

#include <atomic>
#include <cstdint>
#include <cstdio>
#include <cstring>

#include "core/rex_payload_bridge.h"

#ifndef _WIN32
#include <csignal>
#include <ctime>
#include <dlfcn.h>
#include <execinfo.h>
#include <fcntl.h>
#include <pthread.h>
#include <sys/stat.h>
#include <sys/syscall.h>
#include <sys/types.h>
#include <thread>
#include <ucontext.h>
#include <unistd.h>
#else
#include <chrono>
#endif

namespace refii_hang_watchdog {
namespace {

// ------------------------------------------------------------------ the stamps the host writes

constexpr uint32_t kPhaseCapacity = 64;
constexpr uint32_t kPhaseNameBytes = 48;
constexpr uint32_t kPhaseRing = 16;

std::atomic<uint64_t> g_alive_ns{0};
std::atomic<uint64_t> g_alive_frame{0};
std::atomic<uint64_t> g_alive_calls{0};

char g_phase_names[kPhaseCapacity][kPhaseNameBytes] = {};
std::atomic<uint32_t> g_phase_count{0};
std::atomic_flag g_phase_register_lock = ATOMIC_FLAG_INIT;

struct PhaseEntry {
    std::atomic<uint32_t> index{0};
    std::atomic<uint64_t> ns{0};
};
PhaseEntry g_phase_ring[kPhaseRing];
std::atomic<uint32_t> g_phase_pos{0};

/// The host's own log file, stated once at install; the report names it.
char g_host_log_path[4096] = {};

GuestTableWriter g_guest_table_writer = nullptr;

// ------------------------------------------------------------------------- report bookkeeping

std::atomic<uint32_t> g_reports{0};
std::atomic<uint64_t> g_last_stall_ns{0};
std::atomic<int32_t> g_stalled{0};
char g_last_report_path[4096] = {};

uint64_t MonotonicNs() {
#ifndef _WIN32
    timespec now{};
    ::clock_gettime(CLOCK_MONOTONIC, &now);
    return static_cast<uint64_t>(now.tv_sec) * 1000000000ull + static_cast<uint64_t>(now.tv_nsec);
#else
    // Stamps only: the watchdog thread and its report are not built on Windows.
    return static_cast<uint64_t>(std::chrono::duration_cast<std::chrono::nanoseconds>(
                                     std::chrono::steady_clock::now().time_since_epoch())
                                     .count());
#endif
}

}  // namespace

void SetGuestTableWriter(GuestTableWriter writer) { g_guest_table_writer = writer; }

}  // namespace refii_hang_watchdog

#ifndef _WIN32
namespace refii_hang_watchdog {
namespace {

constexpr uint64_t kStallNs = 8ull * 1000000000ull;
constexpr uint64_t kRepeatNs = 30ull * 1000000000ull;
constexpr uint64_t kPollNs = 250ull * 1000000ull;
constexpr uint64_t kCaptureWaitNs = 3ull * 1000000000ull;
constexpr int kCaptureSignal = SIGUSR2;  // free: the SDK's thread signals are SIGRTMIN+n on Linux
constexpr int kMaxFrames = 128;

// The main thread, as named by its first stamp.
std::atomic<bool> g_started{false};
pthread_t g_main_pthread{};
std::atomic<long> g_main_tid{0};
uint64_t g_watchdog_start_ns = 0;

// The capture the signal handler fills. `g_capture_armed` rejects a stray SIGUSR2; the handler
// publishes `g_capture_done` last, after every other field is written.
std::atomic<int> g_capture_armed{0};
std::atomic<int> g_capture_done{0};
void* g_capture_frames[kMaxFrames] = {};
std::atomic<int> g_capture_count{0};
std::atomic<uintptr_t> g_capture_pc{0};
std::atomic<uintptr_t> g_capture_sp{0};
std::atomic<long> g_capture_tid{0};

char g_log_dir[4096] = {};

// Scratch for the report writer: one buffer each, never shared between two writers because the
// watchdog is the only one.
char g_line[2048];
char g_file_buffer[16384];

/// `write(2)` the whole string; a short write ends the attempt rather than spinning.
void Append(int fd, const char* text, size_t length) {
    while (length > 0) {
        const ssize_t written = ::write(fd, text, length);
        if (written <= 0) {
            return;
        }
        text += written;
        length -= static_cast<size_t>(written);
    }
}

void Append(int fd, const char* text) { Append(fd, text, std::strlen(text)); }

/// snprintf into the shared line buffer, then write it. Not for the signal handler.
template <typename... Args>
void Line(int fd, const char* format, Args... args) {
    const int n = std::snprintf(g_line, sizeof(g_line), format, args...);
    if (n > 0) {
        Append(fd, g_line, static_cast<size_t>(n) < sizeof(g_line) ? static_cast<size_t>(n)
                                                                      : sizeof(g_line) - 1);
    }
}

/// Reads a whole small file into `out` (NUL-terminated); returns the byte count, 0 when unreadable.
size_t ReadFile(const char* path, char* out, size_t capacity) {
    const int fd = ::open(path, O_RDONLY | O_CLOEXEC);
    if (fd < 0) {
        out[0] = '\0';
        return 0;
    }
    size_t total = 0;
    while (total + 1 < capacity) {
        const ssize_t got = ::read(fd, out + total, capacity - 1 - total);
        if (got <= 0) {
            break;
        }
        total += static_cast<size_t>(got);
    }
    ::close(fd);
    out[total] = '\0';
    return total;
}

/// Copies the rest of the line that starts with `key` (after it) into `value`, trimmed.
bool FindLine(const char* text, const char* key, char* value, size_t capacity) {
    const size_t key_length = std::strlen(key);
    const char* at = text;
    while ((at = std::strstr(at, key)) != nullptr) {
        if (at == text || at[-1] == '\n') {
            const char* from = at + key_length;
            while (*from == ' ' || *from == '\t' || *from == ':') {
                ++from;
            }
            size_t n = 0;
            while (from[n] != '\0' && from[n] != '\n' && n + 1 < capacity) {
                value[n] = from[n];
                ++n;
            }
            value[n] = '\0';
            return true;
        }
        at += key_length;
    }
    value[0] = '\0';
    return false;
}

/// One field of /proc/self/task/<tid>/<leaf>, read whole, newline stripped, into `value`.
void TaskFile(long tid, const char* leaf, char* value, size_t capacity) {
    char path[128];
    std::snprintf(path, sizeof(path), "/proc/self/task/%ld/%s", tid, leaf);
    const size_t n = ReadFile(path, value, capacity);
    for (size_t i = 0; i < n; ++i) {
        if (value[i] == '\n') {
            value[i] = '\0';
            break;
        }
    }
}

/// The state letter and the two context-switch counters, from `status`.
void TaskState(long tid, char* state, size_t state_capacity, char* vctx, size_t vctx_capacity,
               char* nvctx, size_t nvctx_capacity) {
    char path[128];
    std::snprintf(path, sizeof(path), "/proc/self/task/%ld/status", tid);
    ReadFile(path, g_file_buffer, sizeof(g_file_buffer));
    FindLine(g_file_buffer, "State:", state, state_capacity);
    FindLine(g_file_buffer, "voluntary_ctxt_switches:", vctx, vctx_capacity);
    FindLine(g_file_buffer, "nonvoluntary_ctxt_switches:", nvctx, nvctx_capacity);
}

/// Beside the guest logs: `ReXApp::OnInitialize` logs to `exe_dir/logs`, and embedded, exe_dir
/// is Godot's — the same resolution as host_bridge.cpp's crash record.
void ResolveLogDir() {
    if (g_log_dir[0] != '\0') {
        return;
    }
    char exe[4096];
    const ssize_t n = ::readlink("/proc/self/exe", exe, sizeof(exe) - 1);
    if (n > 0) {
        exe[n] = '\0';
        char* slash = std::strrchr(exe, '/');
        if (slash != nullptr) {
            *slash = '\0';
        }
    } else if (::getcwd(exe, sizeof(exe)) == nullptr) {
        std::snprintf(exe, sizeof(exe), ".");
    }
    std::snprintf(g_log_dir, sizeof(g_log_dir), "%s/logs", exe);
    ::mkdir(g_log_dir, 0755);
}

// ----------------------------------------------------------------- the main-thread capture

/// Runs ON THE MAIN THREAD, inside SIGUSR2, after pthread_kill from the watchdog. Only what the
/// comment at the top allows: backtrace() into a static array, a few atomics, no I/O.
void CaptureHandler(int, siginfo_t*, void* context) {
    if (g_capture_armed.load(std::memory_order_acquire) == 0) {
        return;
    }
    g_capture_tid.store(static_cast<long>(::syscall(SYS_gettid)), std::memory_order_relaxed);
    if (context != nullptr) {
        const auto* uc = static_cast<const ucontext_t*>(context);
#if defined(__x86_64__)
        g_capture_pc.store(static_cast<uintptr_t>(uc->uc_mcontext.gregs[REG_RIP]),
                           std::memory_order_relaxed);
        g_capture_sp.store(static_cast<uintptr_t>(uc->uc_mcontext.gregs[REG_RSP]),
                           std::memory_order_relaxed);
#elif defined(__aarch64__)
        g_capture_pc.store(static_cast<uintptr_t>(uc->uc_mcontext.pc), std::memory_order_relaxed);
        g_capture_sp.store(static_cast<uintptr_t>(uc->uc_mcontext.sp), std::memory_order_relaxed);
#else
        (void)uc;
#endif
    }
    const int count = ::backtrace(g_capture_frames, kMaxFrames);
    g_capture_count.store(count, std::memory_order_relaxed);
    g_capture_done.store(1, std::memory_order_release);
}

void InstallCaptureHandler() {
    struct sigaction action{};
    action.sa_sigaction = CaptureHandler;
    action.sa_flags = SA_SIGINFO | SA_ONSTACK | SA_RESTART;
    sigemptyset(&action.sa_mask);
    ::sigaction(kCaptureSignal, &action, nullptr);
}

/// Names one address: module and nearest exported symbol, or "no module" (managed JIT code or
/// unmapped). dladdr takes the loader lock: called only after the raw addresses are on disk.
void DescribeAddress(int fd, int index, uintptr_t pc) {
    Dl_info info{};
    if (pc != 0 && ::dladdr(reinterpret_cast<void*>(pc), &info) != 0 && info.dli_fname != nullptr) {
        const uintptr_t anchor = reinterpret_cast<uintptr_t>(
            info.dli_saddr != nullptr ? info.dli_saddr : info.dli_fbase);
        const char* file = std::strrchr(info.dli_fname, '/');
        file = file != nullptr ? file + 1 : info.dli_fname;
        Line(fd, "  #%-3d 0x%016lx %s+0x%lx  [%s]\n", index, static_cast<unsigned long>(pc),
             info.dli_sname != nullptr ? info.dli_sname : "<no symbol>",
             static_cast<unsigned long>(pc - anchor), file);
    } else {
        Line(fd, "  #%-3d 0x%016lx <no module: managed JIT code or unmapped>\n", index,
             static_cast<unsigned long>(pc));
    }
}

/// Signals the main thread and writes what the handler captured, or why it could not.
void WriteMainThreadBacktrace(int fd) {
    const long main_tid = g_main_tid.load();
    char state[32], wchan[64], sys[128], vctx[32], nvctx[32];
    TaskState(main_tid, state, sizeof(state), vctx, sizeof(vctx), nvctx, sizeof(nvctx));
    TaskFile(main_tid, "wchan", wchan, sizeof(wchan));
    TaskFile(main_tid, "syscall", sys, sizeof(sys));
    Line(fd, "--- main thread tid=%ld state=%s wchan=%s syscall=%s vctx=%s nvctx=%s\n", main_tid,
         state, wchan, sys, vctx, nvctx);

    g_capture_count.store(0);
    g_capture_pc.store(0);
    g_capture_sp.store(0);
    g_capture_tid.store(0);
    g_capture_done.store(0);
    g_capture_armed.store(1, std::memory_order_release);
    const int sent = ::pthread_kill(g_main_pthread, kCaptureSignal);
    if (sent != 0) {
        g_capture_armed.store(0);
        Line(fd, "--- main thread backtrace: pthread_kill failed (%d)\n", sent);
        return;
    }

    const uint64_t deadline = MonotonicNs() + kCaptureWaitNs;
    while (g_capture_done.load(std::memory_order_acquire) == 0 && MonotonicNs() < deadline) {
        timespec nap{0, 10 * 1000 * 1000};
        ::nanosleep(&nap, nullptr);
    }
    g_capture_armed.store(0);

    if (g_capture_done.load(std::memory_order_acquire) == 0) {
        Line(fd,
             "--- main thread backtrace: the SIGUSR2 handler did not complete within %llu ms — "
             "the thread is not taking signals (uninterruptible wait, stopped) or backtrace() "
             "itself blocked on the loader lock held by another thread\n",
             static_cast<unsigned long long>(kCaptureWaitNs / 1000000ull));
        return;
    }

    const int count = g_capture_count.load();
    Line(fd, "--- main thread backtrace: %d frames, handler ran on tid=%ld, pc=0x%lx sp=0x%lx\n",
         count, g_capture_tid.load(), static_cast<unsigned long>(g_capture_pc.load()),
         static_cast<unsigned long>(g_capture_sp.load()));
    Append(fd, "  raw:");
    for (int i = 0; i < count; ++i) {
        Line(fd, " 0x%lx", static_cast<unsigned long>(reinterpret_cast<uintptr_t>(g_capture_frames[i])));
    }
    Append(fd, "\n  frames 0-2 are the signal delivery (handler, libc trampoline); the stalled "
               "instruction is the interrupted pc above and the frame after the trampoline.\n"
               "  Managed (.NET JIT) frames cannot be walked from native and appear as "
               "<no module>; InvokeGodotClassMethod marks the native->managed edge.\n");
    for (int i = 0; i < count; ++i) {
        DescribeAddress(fd, i, reinterpret_cast<uintptr_t>(g_capture_frames[i]));
    }
}

// ------------------------------------------------------------------------- every thread

/// The kernel's getdents64 record; d_name runs to d_reclen, never to this declared bound.
struct LinuxDirent64 {
    uint64_t d_ino;
    int64_t d_off;
    unsigned short d_reclen;
    unsigned char d_type;
    char d_name[256];
};

/// /proc/self/task via getdents64 into a static buffer: no opendir, no malloc.
void WriteAllThreads(int fd) {
    Append(fd, "--- threads (/proc/self/task): tid comm state wchan syscall vctx nvctx\n");
    const int dir = ::open("/proc/self/task", O_RDONLY | O_DIRECTORY | O_CLOEXEC);
    if (dir < 0) {
        Append(fd, "  (cannot open /proc/self/task)\n");
        return;
    }
    static char entries[16384];
    int listed = 0;
    for (;;) {
        const long got = ::syscall(SYS_getdents64, dir, entries, sizeof(entries));
        if (got <= 0) {
            break;
        }
        for (long at = 0; at < got;) {
            const auto* entry = reinterpret_cast<const LinuxDirent64*>(entries + at);
            at += entry->d_reclen;
            if (entry->d_name[0] < '0' || entry->d_name[0] > '9') {
                continue;
            }
            const long tid = std::strtol(entry->d_name, nullptr, 10);
            char comm[32], state[32], wchan[64], sys[128], vctx[32], nvctx[32];
            TaskFile(tid, "comm", comm, sizeof(comm));
            TaskState(tid, state, sizeof(state), vctx, sizeof(vctx), nvctx, sizeof(nvctx));
            TaskFile(tid, "wchan", wchan, sizeof(wchan));
            TaskFile(tid, "syscall", sys, sizeof(sys));
            Line(fd, "  %6ld %-16s %-12s %-24s %-40s %s %s%s\n", tid, comm, state, wchan, sys, vctx,
                 nvctx, tid == g_main_tid.load() ? "  <- main" : "");
            ++listed;
        }
    }
    ::close(dir);
    Line(fd, "  %d threads\n", listed);
}

// --------------------------------------------------------------------------- the report

void WriteMemory(int fd) {
    char rss[64], swap[64], hwm[64], threads[64], avail[64], swapfree[64];
    ReadFile("/proc/self/status", g_file_buffer, sizeof(g_file_buffer));
    FindLine(g_file_buffer, "VmRSS:", rss, sizeof(rss));
    FindLine(g_file_buffer, "VmSwap:", swap, sizeof(swap));
    FindLine(g_file_buffer, "VmHWM:", hwm, sizeof(hwm));
    FindLine(g_file_buffer, "Threads:", threads, sizeof(threads));
    ReadFile("/proc/meminfo", g_file_buffer, sizeof(g_file_buffer));
    FindLine(g_file_buffer, "MemAvailable:", avail, sizeof(avail));
    FindLine(g_file_buffer, "SwapFree:", swapfree, sizeof(swapfree));
    Line(fd, "memory: VmRSS=%s VmSwap=%s VmHWM=%s Threads=%s | MemAvailable=%s SwapFree=%s\n", rss,
         swap, hwm, threads, avail, swapfree);
    if (ReadFile("/proc/pressure/memory", g_file_buffer, sizeof(g_file_buffer)) > 0) {
        Append(fd, "pressure/memory: ");
        for (char* c = g_file_buffer; *c != '\0'; ++c) {
            if (*c == '\n') {
                *c = ' ';
            }
        }
        Append(fd, g_file_buffer);
        Append(fd, "\n");
    }
}

void WritePhases(int fd, uint64_t now) {
    const uint32_t pos = g_phase_pos.load();
    const uint32_t count = pos < kPhaseRing ? pos : kPhaseRing;
    Append(fd, "phases entered (oldest first, seconds ago):");
    for (uint32_t i = 0; i < count; ++i) {
        const uint32_t slot = (pos - count + i) % kPhaseRing;
        const uint32_t index = g_phase_ring[slot].index.load();
        const uint64_t ns = g_phase_ring[slot].ns.load();
        const char* name = index >= 1u && index <= g_phase_count.load() ? g_phase_names[index - 1u] : "?";
        Line(fd, " %s@%.1f", name, ns <= now ? (now - ns) / 1e9 : 0.0);
    }
    Append(fd, "\n");
    if (count > 0) {
        const uint32_t slot = (pos - 1) % kPhaseRing;
        const uint32_t index = g_phase_ring[slot].index.load();
        const uint64_t ns = g_phase_ring[slot].ns.load();
        Line(fd, "in phase '%s' for %.1f s (the last phase entered; the frame may have moved past it)\n",
             index >= 1u && index <= g_phase_count.load() ? g_phase_names[index - 1u] : "?",
             ns <= now ? (now - ns) / 1e9 : 0.0);
    }
}

void WriteTimeBase(int fd) {
    uint64_t capped = 0, granted = 0, watchdog = 0;
    refii_bridge_rex_time_base_stats(&capped, &granted, &watchdog);
    Line(fd, "time base: mode=%d granted=%llu pending=%llu watchdogLapses=%llu capped=%llu dropped=%llu lockedPerMille=%llu\n",
         refii_bridge_rex_time_base_mode(), static_cast<unsigned long long>(granted & 0xFFFFFFFFull),
         static_cast<unsigned long long>(watchdog >> 32),
         static_cast<unsigned long long>(watchdog & 0xFFFFFFFFull),
         static_cast<unsigned long long>(capped & 0xFFFFFFFFull),
         static_cast<unsigned long long>(capped >> 32),
         static_cast<unsigned long long>(granted >> 32));
}

void UtcStamp(char* out, size_t capacity, bool for_name) {
    const time_t now = ::time(nullptr);
    tm parts{};
    ::gmtime_r(&now, &parts);
    std::snprintf(out, capacity, for_name ? "%04d%02d%02d_%02d%02d%02d" : "%04d-%02d-%02dT%02d:%02d:%02dZ",
                  parts.tm_year + 1900, parts.tm_mon + 1, parts.tm_mday, parts.tm_hour, parts.tm_min,
                  parts.tm_sec);
}

/// One section of the report for the current stall. `path` is the stall's file; `ordinal` counts
/// sections within it.
void WriteReport(const char* path, uint32_t ordinal, uint64_t now, uint64_t alive, uint64_t stalled_ns) {
    const int fd = ::open(path, O_WRONLY | O_CREAT | O_APPEND | O_CLOEXEC, 0644);
    if (fd < 0) {
        Line(2, "[hang-watchdog] cannot open %s\n", path);
        return;
    }
    char stamp[48];
    UtcStamp(stamp, sizeof(stamp), false);
    Line(fd, "\n=== refii hang report %s section %u pid=%ld\n", stamp, ordinal,
         static_cast<long>(::getpid()));
    Line(fd, "stall: the main thread (tid %ld) has not stamped a frame for %.1f s; last frame %llu, "
             "%llu stamps, %.1f s after the watchdog started\n",
         g_main_tid.load(), stalled_ns / 1e9, static_cast<unsigned long long>(g_alive_frame.load()),
         static_cast<unsigned long long>(g_alive_calls.load()),
         alive >= g_watchdog_start_ns ? (alive - g_watchdog_start_ns) / 1e9 : 0.0);
    WritePhases(fd, now);
    Line(fd, "host log: %s (its tail is the last lines before the stall; not mirrored here — a "
             "managed Logger would be a hang vector itself)\n",
         g_host_log_path[0] != '\0' ? g_host_log_path : "<not stated>");
    WriteMemory(fd);
    WriteTimeBase(fd);
    WriteMainThreadBacktrace(fd);
    WriteAllThreads(fd);
    if (g_guest_table_writer != nullptr) {
        Append(fd, "--- guest threads (kernel object table; takes its lock — a report that ends "
                   "here means that lock was held):\n");
        g_guest_table_writer(fd);
    } else {
        Append(fd, "--- guest threads: no writer registered (bridge not up)\n");
    }
    Append(fd, "=== end of section\n");
    ::close(fd);

    Line(2, "[hang-watchdog] main thread stalled %.1f s at frame %llu — report %s (section %u)\n",
         stalled_ns / 1e9, static_cast<unsigned long long>(g_alive_frame.load()), path, ordinal);
}

void WriteRecovery(const char* path, uint64_t stalled_ns) {
    const int fd = ::open(path, O_WRONLY | O_CREAT | O_APPEND | O_CLOEXEC, 0644);
    if (fd < 0) {
        return;
    }
    char stamp[48];
    UtcStamp(stamp, sizeof(stamp), false);
    Line(fd, "\n=== %s recovered: the main thread stamped a frame again after %.1f s\n", stamp,
         stalled_ns / 1e9);
    ::close(fd);
    Line(2, "[hang-watchdog] main thread recovered after %.1f s\n", stalled_ns / 1e9);
}

/// The stall schedule, pure: given the clock, the last stamp and the last report of this stall
/// (0 = none yet), whether to write now. The first report fires at kStallNs, repeats every kRepeatNs.
bool ShouldReport(uint64_t now, uint64_t alive, uint64_t last_report) {
    if (now < alive || now - alive < kStallNs) {
        return false;
    }
    return last_report == 0 || now - last_report >= kRepeatNs;
}

void WatchdogLoop() {
    uint64_t last_report = 0;   // of the current stall; 0 when not stalled
    uint64_t stall_alive = 0;   // the stamp the current stall is measured from
    uint32_t sections = 0;
    char path[4096] = {};
    for (;;) {
        timespec nap{static_cast<time_t>(kPollNs / 1000000000ull),
                     static_cast<long>(kPollNs % 1000000000ull)};
        ::nanosleep(&nap, nullptr);

        const uint64_t now = MonotonicNs();
        const uint64_t alive = g_alive_ns.load(std::memory_order_acquire);
        if (alive == 0) {
            continue;
        }

        if (last_report != 0 && alive != stall_alive) {
            // The stamp moved: the stall is over.
            WriteRecovery(path, now - stall_alive);
            g_stalled.store(0);
            last_report = 0;
            sections = 0;
            continue;
        }

        if (!ShouldReport(now, alive, last_report)) {
            continue;
        }

        if (last_report == 0) {
            char stamp[48];
            UtcStamp(stamp, sizeof(stamp), true);
            std::snprintf(path, sizeof(path), "%s/refii_hang_%s.txt", g_log_dir, stamp);
            stall_alive = alive;
            sections = 0;
            g_stalled.store(1);
            g_reports.fetch_add(1);
            std::snprintf(g_last_report_path, sizeof(g_last_report_path), "%s", path);
        }
        ++sections;
        last_report = now;
        g_last_stall_ns.store(now - alive);
        WriteReport(path, sections, now, alive, now - alive);
    }
}

/// On the first stamp, from the main thread: names it, primes what the handler needs, starts the
/// thread. Once.
void StartFromMainThread() {
    bool expected = false;
    if (!g_started.compare_exchange_strong(expected, true)) {
        return;
    }
    g_main_pthread = ::pthread_self();
    g_main_tid.store(static_cast<long>(::syscall(SYS_gettid)));
    g_watchdog_start_ns = MonotonicNs();
    ResolveLogDir();
    {
        // backtrace() loads libgcc_s on its first call and gmtime_r may read the zone tables:
        // both taken here, on a healthy thread, so the stalled-path calls are walks only.
        void* frames[4];
        (void)::backtrace(frames, 4);
        char stamp[48];
        UtcStamp(stamp, sizeof(stamp), true);
    }
    InstallCaptureHandler();
    std::thread(WatchdogLoop).detach();
    Line(2, "[hang-watchdog] watching main thread tid=%ld; reports go to %s/refii_hang_<utc>.txt\n",
         g_main_tid.load(), g_log_dir);
}

}  // namespace
}  // namespace refii_hang_watchdog
#endif  // !_WIN32

// ------------------------------------------------------------------------------- exports

using namespace refii_hang_watchdog;

/// The main thread's per-frame stamp. The first call names the main thread and starts the watchdog.
extern "C" REFII_HOST_ABI_EXPORT void RefiiHostFrameAlive(uint64_t frame) {
    g_alive_frame.store(frame, std::memory_order_relaxed);
    g_alive_calls.fetch_add(1, std::memory_order_relaxed);
    g_alive_ns.store(MonotonicNs(), std::memory_order_release);
#ifndef _WIN32
    StartFromMainThread();
#endif
}

/// Registers a frame-phase name once; returns its index (1-based), or 0 when the table is full.
/// The host caches the index per name and passes it to RefiiHostSetFramePhase.
extern "C" REFII_HOST_ABI_EXPORT uint32_t RefiiHostRegisterFramePhase(const char* name) {
    if (name == nullptr || name[0] == '\0') {
        return 0;
    }
    while (g_phase_register_lock.test_and_set(std::memory_order_acquire)) {
    }
    uint32_t index = 0;
    const uint32_t count = g_phase_count.load();
    for (uint32_t i = 0; i < count; ++i) {
        if (std::strncmp(g_phase_names[i], name, kPhaseNameBytes) == 0) {
            index = i + 1u;
            break;
        }
    }
    if (index == 0 && count < kPhaseCapacity) {
        std::snprintf(g_phase_names[count], kPhaseNameBytes, "%s", name);
        g_phase_count.store(count + 1u);
        index = count + 1u;
    }
    g_phase_register_lock.clear(std::memory_order_release);
    return index;
}

/// The main thread entered phase `index`. Kept in a ring of the last 16 entries with their times.
extern "C" REFII_HOST_ABI_EXPORT void RefiiHostSetFramePhase(uint32_t index) {
    const uint32_t pos = g_phase_pos.fetch_add(1, std::memory_order_relaxed);
    PhaseEntry& entry = g_phase_ring[pos % kPhaseRing];
    entry.ns.store(MonotonicNs(), std::memory_order_relaxed);
    entry.index.store(index, std::memory_order_release);
}

/// The host's log file path (Godot's `debug/file_logging/log_path`, globalised), stated once at
/// install so a report can name it. Never per print.
extern "C" REFII_HOST_ABI_EXPORT void RefiiHostSetHostLogPath(const char* path) {
    std::snprintf(g_host_log_path, sizeof(g_host_log_path), "%s", path != nullptr ? path : "");
}

/// What the watchdog has written: the number of stalls reported, the last report's path, the last
/// stall's length in seconds, and whether a stall is in progress (answered by the main thread, so
/// 1 here means the stamp moved since but the watchdog has not yet noticed). Returns the count.
extern "C" REFII_HOST_ABI_EXPORT uint32_t RefiiHostHangReport(char* out_path, uint32_t path_capacity,
                                                   double* out_last_stall_seconds, int32_t* out_stalled) {
    if (out_path != nullptr && path_capacity > 0) {
        std::snprintf(out_path, path_capacity, "%s", g_last_report_path);
    }
    if (out_last_stall_seconds != nullptr) {
        *out_last_stall_seconds = g_last_stall_ns.load() / 1e9;
    }
    if (out_stalled != nullptr) {
        *out_stalled = g_stalled.load();
    }
    return g_reports.load();
}
