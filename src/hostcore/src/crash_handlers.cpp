// refii — the host bridge's crash record and last-resort fault handling.
//
// A dying process loses the tail of both logs, and a fail-fast inside the guest leaves no
// exception, no log line and no exit status. These name the reason before the process goes,
// and keep the guest's own fault handling and .NET's in the order each needs.

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
#include "host_backtrace.h"
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
#include "host_core.h"
#include "host_tracy.h"
#include "hang_watchdog.h"
#include "core/traced_mutex.h"
#include "core/audio_state.h"
#include "core/mod_values.h"
#include "core/event_relay.h"
#include "core/player_state.h"

using namespace refii::hostcore;

#if defined(__GNUC__) || defined(__clang__)
#if defined(__aarch64__) && !defined(_WIN32)
extern "C" int refii_guarded_copy(void* dst, const void* src, std::size_t size);
extern "C" const char refii_guarded_copy_end[];
#endif
#if defined(__ANDROID__)
const std::filesystem::path& RefiiGetAppRoot();
#endif
#pragma GCC visibility push(hidden)
#endif
namespace refii::hostcore {

#ifndef _WIN32
/// The crash record. Godot's log is stdio-buffered and the guest log is an spdlog queue, so a
/// dying process loses the last few kilobytes of both. Written with open/write only, from
/// inside the signal handler, and closed before anything that could block.
namespace crash {
char g_path[4096] = {};


/// Beside the guest log (`ReXApp::OnInitialize` logs to `exe_dir/logs`); under the host's own
/// writable root on Android.
void ResolvePath() {
    if (g_path[0] != '\0') {
        return;
    }
    std::error_code ec;
#if defined(__ANDROID__)
    // Android's /proc/self/exe is app_process64 under /system/bin, so an executable-relative
    // crash path is unwritable. The host states its private root before initialization
    // (RefiiHostSetAppRoot); stderr stays the fallback while it is unset.
    std::filesystem::path dir = RefiiGetAppRoot();
    if (dir.empty()) {
        return;
    }
#else
    std::filesystem::path exe = std::filesystem::read_symlink("/proc/self/exe", ec);
    std::filesystem::path dir = ec ? std::filesystem::current_path(ec) : exe.parent_path();
#endif
    std::filesystem::create_directories(dir / "logs", ec);
    // Named by the title's config file (refii / refiii), so two titles' crashes do not interleave
    // in one record; refii's when the host stated none.
    std::string stem = RefiiGetConfigPath().stem().string();
    if (stem.empty()) {
        stem = "refii";
    }
    const std::string path = (dir / "logs" / (stem + "_crash.log")).string();
    std::snprintf(g_path, sizeof(g_path), "%s", path.c_str());
}

void Append(int fd, const char* text) {
    std::size_t left = std::strlen(text);
    while (left > 0) {
        const ssize_t written = ::write(fd, text, left);
        if (written <= 0) {
            return;
        }
        text += written;
        left -= static_cast<std::size_t>(written);
    }
}

/// Module and nearest symbol. A recompiled guest function names itself `sub_8XXXXXXX`,
/// which is the address IDA wants; managed (JIT) code has no module at all.
void DescribeAddress(int fd, uintptr_t pc) {
    Dl_info info{};
    char buffer[768];
    if (pc != 0 && ::dladdr(reinterpret_cast<void*>(pc), &info) != 0 &&
        info.dli_fname != nullptr) {
        const uintptr_t anchor = reinterpret_cast<uintptr_t>(
            info.dli_saddr != nullptr ? info.dli_saddr : info.dli_fbase);
        std::snprintf(buffer, sizeof(buffer), "  pc is %s+0x%lx in %s\n",
                      info.dli_sname != nullptr ? info.dli_sname : "<no symbol>",
                      static_cast<unsigned long>(pc - anchor), info.dli_fname);
    } else {
        std::snprintf(buffer, sizeof(buffer),
                      "  pc is in no loaded module (managed JIT code, or unmapped)\n");
    }
    Append(fd, buffer);
}

/// The faulting thread's GUEST stack, when it has one. ThreadState::Get() is the null-safe
/// TLS read; the walk is the title's own back chain (32-bit word at [r1], saved LR at
/// back-chain - 8, verified in the binary), pure loads against the always-mapped arena, so it
/// is as signal-safe as the host backtrace below it.
void DescribeGuestThread(int fd) {
    rex::runtime::ThreadState* state = rex::runtime::ThreadState::Get();
    rex::memory::Memory* memory = SafeKernelMemory();
    if (state == nullptr || state->context() == nullptr || memory == nullptr) {
        return;
    }
    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return;
    }

    PPCContext* context = state->context();
    char buffer[96];
    std::snprintf(buffer, sizeof(buffer), "  guest tid=%u r1=%08X lr=%08X\n",
                  state->thread_id(), context->r1.u32, static_cast<uint32_t>(context->lr));
    Append(fd, buffer);
    uint32_t sp = context->r1.u32;
    for (int frame = 0; frame < 32 && sp >= 0x10000u && sp < 0xC0000000u; ++frame) {
        uint32_t back = 0;
        std::memcpy(&back, base + sp, sizeof(back));
        back = __builtin_bswap32(back);
        if (back == 0u || back <= sp || back >= 0xC0000000u) {
            break;
        }
        uint32_t lr = 0;
        std::memcpy(&lr, base + back - 8u, sizeof(lr));
        lr = __builtin_bswap32(lr);
        if (lr == 0u) {
            break;
        }
        std::snprintf(buffer, sizeof(buffer), "  guest frame %08X\n", lr);
        Append(fd, buffer);
        sp = back;
    }
}

/// One record: a stamped header, the pc named, and the calling thread's backtrace.
void WriteRecord(const char* line, uintptr_t pc) {
    const int fd = g_path[0] != '\0'
                       ? ::open(g_path, O_WRONLY | O_CREAT | O_APPEND | O_CLOEXEC, 0644)
                       : -1;
    if (fd < 0) {
        Append(2, line);
        Append(2, "\n");
        return;
    }
    char header[160];
    std::snprintf(header, sizeof(header), "\n=== %ld tid=%ld ", static_cast<long>(::time(nullptr)),
                  static_cast<long>(::syscall(SYS_gettid)));
    Append(fd, header);
    Append(fd, line);
    Append(fd, "\n");
    if (pc != 0) {
        DescribeAddress(fd, pc);
    }
    DescribeGuestThread(fd);
#if REFII_HOST_BACKTRACE
    void* frames[64];
    const int count = ::backtrace(frames, 64);
    ::backtrace_symbols_fd(frames, count, fd);
#else
    // A crash diagnostic must not keep the library from loading on API31: Android resolves
    // strong imports eagerly, before any of this runs.
    Append(fd, "  host backtrace unavailable on Android API<33; fault PC and guest frames retained\n");
#endif
    ::close(fd);
}

/// The deadline is armed BEFORE the record, not only around the loggers. A thread that
/// aborts inside malloc or the dynamic loader still holds that lock, and the record's own
/// backtrace() can want it (its first call loads libgcc_s — primed at install for that
/// reason): a handler that never returns is a process that never ends. SIGALRM's default
/// action is a death, nothing else here uses it, and a run in which one thread hung in the
/// SIGABRT handler for ten minutes (2026-10-11, refii_crash.log cut off after the header)
/// is what this costs without it.
///
/// Cancelled on the way out, because a managed fault carries on running.
void Report(const char* line, uintptr_t pc) {
    ::alarm(5);
    WriteRecord(line, pc);
    REXLOG_INFO("{}", line);
    rex::FlushLogging();
    ::alarm(0);
}

/// `Report` for a thread that is dying: the deadline stays armed, so a death that then
/// stalls in abort(), in a chained handler or in a core dump still ends.
void ReportFatal(const char* line, uintptr_t pc) {
    ::alarm(5);
    WriteRecord(line, pc);
    // A live Tracy capture is saved before the process goes (bounded; the record is already
    // written), and the deadline is re-armed for what follows.
    refii_host_tracy::SaveOnCrash(3000);
    ::alarm(5);
    REXLOG_INFO("{}", line);
    rex::FlushLogging();
    ::alarm(5);
}
}  // namespace crash


/// The last-resort fault handler. The runtime's `ExceptionHandler` installs SIGSEGV/SIGILL over
/// .NET's and simply RETURNS when nothing claims a fault, so the faulting instruction
/// re-executes for ever.
///
/// Installed AFTER the runtime's, so it runs first and decides by address:
///   - inside the guest arena: chain to the runtime's handler. If the same thread faults three
///     times at the same pc and address within a few milliseconds, nobody is claiming it:
///     record it, restore the default action, and let the next fault kill the process.
///   - anywhere else: record it (a managed exception shows as pc in no module) and chain to
///     the handler .NET had installed, which dispatches a managed exception or re-raises.
namespace fault {
constexpr int kSignals[] = {SIGSEGV, SIGBUS, SIGILL, SIGFPE};
struct sigaction g_managed[NSIG] = {};

struct sigaction g_runtime[NSIG] = {};
bool g_managed_captured = false;
std::atomic<bool> g_installed{false};
std::atomic<int> g_reports{0};

/// What .NET left on these signals, captured before the runtime replaces any of them.
void CaptureManagedActions() {
    if (g_managed_captured) {
        return;
    }
    g_managed_captured = true;
    for (int sig : kSignals) {
        ::sigaction(sig, nullptr, &g_managed[sig]);
    }
}

uintptr_t ProgramCounter(void* context) {
    auto* ucontext = static_cast<ucontext_t*>(context);
#if defined(__x86_64__)
    return static_cast<uintptr_t>(ucontext->uc_mcontext.gregs[REG_RIP]);
#elif defined(__aarch64__)
    return static_cast<uintptr_t>(ucontext->uc_mcontext.pc);
#else
    (void)ucontext;
    return 0;
#endif
}

/// The virtual and the physical view, 4 GB each, back to back.
bool InGuestArena(uintptr_t address) {
    rex::memory::Memory* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return false;
    }
    const uintptr_t base = reinterpret_cast<uintptr_t>(memory->virtual_membase());
    return address - base < (uintptr_t{1} << 33);
}

/// The same thread, pc and address three times in five milliseconds: a re-armed watch page
/// takes a frame to refault, an unclaimed fault refaults immediately. One tracker, spin-locked,
/// because dynamic TLS is not signal-safe in a dlopen'd library.
struct Tracker {
    long tid;
    uintptr_t pc;
    uintptr_t address;
    int count;
    timespec first;
};
Tracker g_tracker = {};
std::atomic_flag g_tracker_lock = ATOMIC_FLAG_INIT;

bool IsSpinning(uintptr_t pc, uintptr_t address) {
    if (g_tracker_lock.test_and_set(std::memory_order_acquire)) {
        return false;
    }
    timespec now{};
    ::clock_gettime(CLOCK_MONOTONIC, &now);
    const long tid = static_cast<long>(::syscall(SYS_gettid));
    bool spinning = false;
    if (g_tracker.tid == tid && g_tracker.pc == pc && g_tracker.address == address) {
        const double ms = static_cast<double>(now.tv_sec - g_tracker.first.tv_sec) * 1e3 +
                          static_cast<double>(now.tv_nsec - g_tracker.first.tv_nsec) / 1e6;
        if (ms < 5.0) {
            spinning = ++g_tracker.count >= 3;
        } else {
            g_tracker = Tracker{tid, pc, address, 1, now};
        }
    } else {
        g_tracker = Tracker{tid, pc, address, 1, now};
    }
    g_tracker_lock.clear(std::memory_order_release);
    return spinning;
}

const char* Name(int sig) {
    switch (sig) {
        case SIGSEGV: return "SIGSEGV";
        case SIGBUS: return "SIGBUS";
        case SIGILL: return "SIGILL";
        case SIGFPE: return "SIGFPE";
        case SIGTERM: return "SIGTERM";
        case SIGINT: return "SIGINT";
        case SIGQUIT: return "SIGQUIT";
        case SIGPIPE: return "SIGPIPE";
        case SIGSYS: return "SIGSYS";
        case SIGTRAP: return "SIGTRAP";
        case SIGXCPU: return "SIGXCPU";
        case SIGXFSZ: return "SIGXFSZ";
        case SIGHUP: return "SIGHUP";
        default: return "signal";
    }
}

void Record(int sig, uintptr_t pc, uintptr_t address, const char* verdict) {
    if (g_reports.fetch_add(1, std::memory_order_relaxed) >= 8) {
        return;
    }
    char line[512];
    std::snprintf(line, sizeof(line), "[hostbridge] %s at pc=0x%lx address=0x%lx: %s", Name(sig),
                  static_cast<unsigned long>(pc), static_cast<unsigned long>(address), verdict);
    crash::Report(line, pc);
}

/// Runs the action that was installed before ours, or restores the default so the fault
/// reaches the kernel on re-execution.
void Chain(int sig, const struct sigaction& to, siginfo_t* info, void* context) {
    if ((to.sa_flags & SA_SIGINFO) != 0 && to.sa_sigaction != nullptr) {
        to.sa_sigaction(sig, info, context);
        return;
    }
    if ((to.sa_flags & SA_SIGINFO) == 0 && to.sa_handler == SIG_IGN) {
        return;
    }
    if ((to.sa_flags & SA_SIGINFO) == 0 && to.sa_handler != SIG_DFL && to.sa_handler != nullptr) {
        to.sa_handler(sig);
        return;
    }
    ::signal(sig, SIG_DFL);
}

#if defined(__aarch64__)
/// A fault inside `refii_guarded_copy` (guest_access.cpp) returns 0 from it: the copy is a leaf,
/// so the link register still holds its caller. Returning through sigreturn restores the mask.
bool FixupGuardedCopy(void* context) {
    auto* ucontext = static_cast<ucontext_t*>(context);
    const uintptr_t pc = static_cast<uintptr_t>(ucontext->uc_mcontext.pc);
    if (pc < reinterpret_cast<uintptr_t>(&refii_guarded_copy) ||
        pc >= reinterpret_cast<uintptr_t>(refii_guarded_copy_end)) {
        return false;
    }
    ucontext->uc_mcontext.regs[0] = 0;
    ucontext->uc_mcontext.pc = ucontext->uc_mcontext.regs[30];
    return true;
}
#endif

void LastResort(int sig, siginfo_t* info, void* context) {
#if defined(__aarch64__)
    if ((sig == SIGSEGV || sig == SIGBUS) && FixupGuardedCopy(context)) {
        return;
    }
#endif
    const uintptr_t pc = ProgramCounter(context);
    const uintptr_t address = reinterpret_cast<uintptr_t>(info != nullptr ? info->si_addr : nullptr);

    // Unreadable guest memory reads as zeroes. The signal is blocked while this handler runs
    // and the jump does not go through sigreturn, so unblock it here.
    sigjmp_buf* guard = (sig == SIGSEGV || sig == SIGBUS) ? GuardForFaultingThread() : nullptr;
    if (guard != nullptr) {
        sigset_t mask;
        sigemptyset(&mask);
        sigaddset(&mask, sig);
        ::sigprocmask(SIG_UNBLOCK, &mask, nullptr);
        siglongjmp(*guard, 1);
    }

    if (IsSpinning(pc, address)) {
        Record(sig, pc, address,
               "UNCLAIMED — three identical faults on this thread; the runtime's handler declines "
               "it and returning would re-execute it for ever. Default action restored; the next "
               "one kills the process");
        refii_host_tracy::SaveOnCrash(3000);
        ::signal(sig, SIG_DFL);
        return;
    }

    if ((sig == SIGSEGV || sig == SIGBUS) && InGuestArena(address)) {
        Chain(sig, g_runtime[sig], info, context);
        return;
    }

    Record(sig, pc, address,
           "outside guest memory — a host fault, or a managed exception (pc in no module) that "
           "the .NET handler takes next");
    // A managed exception comes back from the chain and disarms. A native fault does not:
    // the .NET handler hands it to Godot's crash handler, which symbolises a backtrace and
    // re-raises, and if any of that stalls the deadline ends the process instead.
    ::alarm(10);
    Chain(sig, g_managed[sig], info, context);
    ::alarm(0);
}

/// The exits nothing else reports: a plain `exit()`, SIGTERM/SIGINT/SIGQUIT, and
/// SIGPIPE/SIGSYS/SIGTRAP/SIGXFSZ/SIGHUP and (not on Android) SIGXCPU, whose default action dies
/// without a word. Recorded, then handed on unchanged.
// Android Mono uses self-delivered SIGXCPU to resume suspended managed threads. Its handler is
// left alone: a diagnostic here can take a lock a suspended thread holds, and those GC signals
// must not consume the fault budget.
constexpr int kExtraSignals[] = {SIGTERM, SIGINT, SIGQUIT, SIGPIPE, SIGSYS, SIGTRAP,
#if !defined(__ANDROID__)
                                 SIGXCPU,
#endif
                                 SIGXFSZ, SIGHUP};

void OnExit() {
    char line[256];
    std::snprintf(line, sizeof(line),
                  "[hostbridge] exit() called on tid=%ld — the backtrace names the caller",
                  static_cast<long>(::syscall(SYS_gettid)));
    crash::Report(line, 0);
}

void LastResortExtra(int sig, siginfo_t* info, void* context) {
    const uintptr_t pc = ProgramCounter(context);
    char verdict[160];
    std::snprintf(verdict, sizeof(verdict),
                  "delivered (si_code=%d, sender pid=%ld) — handed to the previous handler",
                  info != nullptr ? info->si_code : 0,
                  static_cast<long>(info != nullptr ? info->si_pid : 0));
    Record(sig, pc, info != nullptr ? reinterpret_cast<uintptr_t>(info->si_addr) : 0u, verdict);
    if (sig == SIGPIPE) {
        return;  // a dead socket is not a reason to die; the write gets EPIPE instead
    }
    Chain(sig, g_runtime[sig], info, context);
}

/// After the runtime's `Memory` exists (its handler must sit BEHIND ours), on both boot paths.
void Install() {
    bool expected = false;
    if (!g_installed.compare_exchange_strong(expected, true)) {
        return;
    }
    crash::ResolvePath();
    refii_bridge_ensure_alternate_signal_stack();  // Godot's thread, the one that installs this
    for (int sig : kSignals) {
        struct sigaction action{};
        action.sa_sigaction = LastResort;
        action.sa_flags = SA_SIGINFO | SA_ONSTACK;
        sigemptyset(&action.sa_mask);
        ::sigaction(sig, &action, &g_runtime[sig]);
    }
    for (int sig : kExtraSignals) {
        struct sigaction action{};
        action.sa_sigaction = LastResortExtra;
        action.sa_flags = SA_SIGINFO | SA_ONSTACK;
        sigemptyset(&action.sa_mask);
        ::sigaction(sig, &action, &g_runtime[sig]);
    }
    std::atexit(OnExit);
    REXLOG_INFO("Refii host: last-resort fault reporter installed; records go to {}",
                crash::g_path);
}

/// After the runtime is destroyed. `~MMIOHandler` → `ExceptionHandler::Uninstall` restores
/// the sigaction it saved at install, which is .NET's — ours, installed over it, goes with
/// it, and the exit crash of 2026-10-11 (inside the driver's unload, after a complete
/// guest teardown) left no record for exactly that reason. Whatever is current becomes the
/// handler to chain to; no guest arena exists any more, so every fault is a host fault.
void Reinstall() {
    if (!g_installed.load()) {
        return;
    }
    for (int sig : kSignals) {
        struct sigaction action{};
        action.sa_sigaction = LastResort;
        action.sa_flags = SA_SIGINFO | SA_ONSTACK;
        sigemptyset(&action.sa_mask);
        ::sigaction(sig, &action, &g_runtime[sig]);
    }
    for (int sig : kExtraSignals) {
        struct sigaction action{};
        action.sa_sigaction = LastResortExtra;
        action.sa_flags = SA_SIGINFO | SA_ONSTACK;
        sigemptyset(&action.sa_mask);
        ::sigaction(sig, &action, &g_runtime[sig]);
    }
}
}  // namespace fault
#endif


/// Installs the fault reporter once the guest is up. No-op on Windows, where the vectored
/// handler below already reports faults.
void InstallLastResortFaultHandler() {
#ifndef _WIN32
    fault::Install();
#endif
}

/// A fail-fast inside the guest leaves no exception, no log line and no exit status —
/// only 0xC0000409 from ucrtbase. These name the reason before the process dies.
void ReportEmbeddedFailure(const char* kind, const std::string& detail) {
    // A fixed buffer and raw writes: this runs inside the SIGABRT handler, and the thread
    // that aborted may hold the heap or the stdio lock (glibc's "free(): invalid pointer"
    // aborts from inside free with the arena locked).
    char line[1024];
    if (detail.empty()) {
        std::snprintf(line, sizeof(line), "[hostbridge] %s\n", kind);
    } else {
        std::snprintf(line, sizeof(line), "[hostbridge] %s: %s\n", kind, detail.c_str());
    }

#ifndef _WIN32
    crash::Append(2, line);
    crash::ResolvePath();
    crash::ReportFatal(line, 0);
#else
    std::fputs(line, stderr);
#endif

#ifdef _WIN32
    void* frames[62];
    const USHORT captured = ::CaptureStackBackTrace(0, 62, frames, nullptr);
    const HANDLE process = ::GetCurrentProcess();
    ::SymSetOptions(SYMOPT_DEFERRED_LOADS | SYMOPT_UNDNAME | SYMOPT_LOAD_LINES);
    ::SymInitialize(process, nullptr, TRUE);

    alignas(SYMBOL_INFO) char symbol_storage[sizeof(SYMBOL_INFO) + 512] = {};
    auto* symbol = reinterpret_cast<SYMBOL_INFO*>(symbol_storage);
    symbol->SizeOfStruct = sizeof(SYMBOL_INFO);
    symbol->MaxNameLen = 511;

    for (USHORT frame = 0; frame < captured; ++frame) {
        const auto address = reinterpret_cast<DWORD64>(frames[frame]);
        const char* name = "??";
        if (::SymFromAddr(process, address, nullptr, symbol)) {
            name = symbol->Name;
        }

        HMODULE module = nullptr;
        const char* module_name = "?";
        unsigned long long rva = 0;
        if (::GetModuleHandleExA(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
                                     GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                                 reinterpret_cast<LPCSTR>(frames[frame]), &module) &&
            module != nullptr) {
            static char module_path[MAX_PATH];
            if (::GetModuleFileNameA(module, module_path, MAX_PATH) != 0) {
                const char* slash = std::strrchr(module_path, '\\');
                module_name = slash != nullptr ? slash + 1 : module_path;
            }
            rva = address - reinterpret_cast<DWORD64>(module);
        }

        IMAGEHLP_LINE64 location = {};
        location.SizeOfStruct = sizeof(location);
        DWORD displacement = 0;
        if (::SymGetLineFromAddr64(process, address, &displacement, &location)) {
            std::fprintf(stderr, "  #%02u %s!%s (%s:%lu)\n", frame, module_name, name,
                         location.FileName, location.LineNumber);
        } else {
            std::fprintf(stderr, "  #%02u %s+0x%llx (nearest %s)\n", frame, module_name,
                         rva, name);
        }
    }
#endif

    std::fflush(stderr);
}

#ifdef _WIN32
/// MSVC keeps terminate handlers PER THREAD, so a throw on a guest worker never reaches
/// the one installed here. This catches the throw itself, before any unwinding.
LONG CALLBACK TraceThrowHandler(PEXCEPTION_POINTERS info) {
    if (info == nullptr || info->ExceptionRecord == nullptr) {
        return EXCEPTION_CONTINUE_SEARCH;
    }

    const DWORD code = info->ExceptionRecord->ExceptionCode;
    if (code == 0xE06D7363u) {
        // Opt-in: a throw is not necessarily fatal, and the guest throws in normal operation.
        static const bool trace_throws = [] {
            const char* value = std::getenv("REFII_HOST_TRACE_THROWS");
            return value != nullptr && value[0] == '1';
        }();
        if (trace_throws) {
            ReportEmbeddedFailure("C++ throw", {});
        }
    } else if (code == EXCEPTION_ACCESS_VIOLATION) {
        // A guarded read is about to absorb this one; it is not the crash this reports.
        if (g_guarded_guest_reads > 0) {
            return EXCEPTION_CONTINUE_SEARCH;
        }

        // The guest may fault by design for MMIO, so cap the noise.
        static std::atomic<int> reported{0};
        if (reported.fetch_add(1, std::memory_order_relaxed) < 3) {
            std::ostringstream detail;
            detail << "at 0x" << std::hex << info->ExceptionRecord->ExceptionAddress
                   << " accessing 0x" << info->ExceptionRecord->ExceptionInformation[1];
            ReportEmbeddedFailure("ACCESS VIOLATION", detail.str());
        }
    }
    return EXCEPTION_CONTINUE_SEARCH;
}

/// A fault nothing handled, on its way out: the live Tracy capture is saved (bounded), then the
/// filter that was there before — the .NET runtime's or Godot's crash handler — runs exactly as
/// it would have. Only truly unhandled exceptions arrive here, unlike the vectored handler above.
LPTOP_LEVEL_EXCEPTION_FILTER g_previous_unhandled_filter = nullptr;

LONG WINAPI SaveTracyOnUnhandled(PEXCEPTION_POINTERS info) {
    refii_host_tracy::SaveOnCrash(3000);
    return g_previous_unhandled_filter != nullptr ? g_previous_unhandled_filter(info)
                                                  : EXCEPTION_CONTINUE_SEARCH;
}
#endif

#ifndef _WIN32
/// The guest's kernel thread table for a hang report: tid, flags (g guest-created, m main,
/// r running), name, r1, lr and the back-chain frames (the walk RefiiHostGuestBacktrace documents),
/// one line per thread, write(2) only. Called from the watchdog thread, LAST in a report: the
/// object table takes its lock, and the kernel state may not exist yet.
void WriteGuestThreadTableForHangReport(int fd) {
    rex::system::KernelState* state = rex::system::kernel_state();
    rex::memory::Memory* memory = SafeKernelMemory();
    if (state == nullptr || memory == nullptr || memory->virtual_membase() == nullptr) {
        crash::Append(fd, "  (no kernel state: the guest is not up)\n");
        return;
    }
    const uint8_t* base = memory->virtual_membase();
    auto threads = state->object_table()->GetObjectsByType<rex::system::XThread>();
    char line[640];
    for (const auto& thread : threads) {
        PPCContext* context =
            thread->thread_state() != nullptr ? thread->thread_state()->context() : nullptr;
        const uint32_t r1 = context != nullptr ? context->r1.u32 : 0u;
        const uint32_t lr = context != nullptr ? static_cast<uint32_t>(context->lr) : 0u;
        int n = std::snprintf(line, sizeof(line), "  tid=%u %c%c%c %-24s r1=%08X lr=%08X frames:",
                              thread->thread_id(), thread->is_guest_thread() ? 'g' : '-',
                              thread->main_thread() ? 'm' : '-', thread->is_running() ? 'r' : '-',
                              thread->thread() != nullptr ? thread->thread()->name().c_str() : "",
                              r1, lr);
        uint32_t sp = r1;
        for (int frame = 0; frame < 24 && n > 0 && static_cast<size_t>(n) + 12 < sizeof(line) &&
                            sp >= 0x10000u && sp < 0xC0000000u;
             ++frame) {
            const uint32_t back = GuestWordAt(base, sp);
            if (back == 0u || back <= sp || back >= 0xC0000000u) break;
            const uint32_t saved = GuestWordAt(base, back - 8u);
            if (saved == 0u) break;
            n += std::snprintf(line + n, sizeof(line) - static_cast<size_t>(n), " %08X", saved);
            sp = back;
        }
        if (n > 0) {
            crash::Append(fd, line);
            crash::Append(fd, "\n");
        }
    }
    std::snprintf(line, sizeof(line), "  %zu threads\n", threads.size());
    crash::Append(fd, line);
}
#endif

void InstallEmbeddedFailureHandlers() {
    static bool installed = false;
    if (installed) {
        return;
    }
#ifndef _WIN32
    refii_hang_watchdog::SetGuestTableWriter(WriteGuestThreadTableForHangReport);
#endif
    installed = true;

#ifndef _WIN32
    fault::CaptureManagedActions();

    // A core for the deaths no handler sees: systemd-coredump logs the signal and a backtrace
    // in the journal for any core-producing death, as long as RLIMIT_CORE is not zero. Capped
    // because the guest arena is gigabytes; the kernel truncates at the limit.
    {
        rlimit core{};
        if (::getrlimit(RLIMIT_CORE, &core) == 0) {
            const rlim_t wanted = static_cast<rlim_t>(256u) * 1024u * 1024u;
            core.rlim_cur = core.rlim_max == RLIM_INFINITY ? wanted : std::min(wanted, core.rlim_max);
            ::setrlimit(RLIMIT_CORE, &core);
        }
    }
#endif

#ifdef _WIN32
    // Always on. A crash that prints nothing costs a whole test run to learn nothing from;
    // the handler is capped and only reports faults that were going to be fatal anyway.
    ::AddVectoredExceptionHandler(1, TraceThrowHandler);
    g_previous_unhandled_filter = ::SetUnhandledExceptionFilter(SaveTracyOnUnhandled);
#endif

#if REFII_HOST_BACKTRACE
    // backtrace() loads libgcc_s on its first call, through the dynamic loader and the
    // heap. Taken here, on a healthy thread, so the call inside a signal handler is a walk
    // and nothing more.
    {
        void* frames[4];
        (void)::backtrace(frames, 4);
    }
#endif

    // abort() raises SIGABRT before it fail-fasts, and unlike terminate this handler is
    // process-wide — it is the only one that sees a guest worker die. The default action is
    // restored and the signal re-raised here rather than left to abort()'s own second raise,
    // so a SIGABRT that arrived any other way ends the process too.
    // On POSIX the handler it replaced is restored (on Windows, the default) before the re-raise:
    // that keeps Android's debuggerd in the chain, whose tombstone carries the native backtrace
    // this report cannot take below API 33.
#ifdef _WIN32
    std::signal(SIGABRT, [](int) {
        ReportEmbeddedFailure("SIGABRT", {});
        std::signal(SIGABRT, SIG_DFL);
        std::raise(SIGABRT);
    });
#else
    static struct sigaction previous_abort {};
    struct sigaction on_abort {};
    on_abort.sa_handler = [](int) {
        ReportEmbeddedFailure("SIGABRT", {});
        sigaction(SIGABRT, &previous_abort, nullptr);
        std::raise(SIGABRT);
    };
    sigemptyset(&on_abort.sa_mask);
    sigaction(SIGABRT, &on_abort, &previous_abort);
#endif

    std::set_terminate([]() {
        std::string detail;
        if (std::exception_ptr active = std::current_exception()) {
            try {
                std::rethrow_exception(active);
            } catch (const std::exception& error) {
                detail = error.what();
            } catch (...) {
                detail = "non-std exception";
            }
        } else {
            detail = "no active exception";
        }
        ReportEmbeddedFailure("std::terminate", detail);
        std::abort();
    });

#ifdef _WIN32
    _set_invalid_parameter_handler([](const wchar_t*, const wchar_t*, const wchar_t*,
                                      unsigned int, uintptr_t) {
        ReportEmbeddedFailure("CRT invalid parameter", {});
        std::abort();
    });
#endif
}

/// Applies the switches that make the guest a simulation server rather than an app.
/// Shared by both entry points so they cannot drift.
void ApplyEmbeddedEnvironment() {
    InstallEmbeddedFailureHandlers();

    // No window, no overlays, no presentation.
    SetEnvironmentValue("REX_EMBED_HEADLESS", "1");
}

}  // namespace refii::hostcore
#if defined(__GNUC__) || defined(__clang__)
#pragma GCC visibility pop
#endif
