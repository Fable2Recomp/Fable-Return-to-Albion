// refii - ReXGlue Recompiled Project
//
// Shared host entry points for standalone and embedded launches.

#include "refii_host.h"
#include "refii_build_id.h"

#include "refii_app.h"

#include <algorithm>
#include <cstdio>
#include <cstdlib>
#include <atomic>
#include <csignal>
#include <exception>
#include <functional>
#include <map>
#include <memory>
#include <mutex>
#include <string>
#include <system_error>
#include <thread>
#include <vector>

#include <rex/cvar.h>
#include <rex/logging.h>
#include <rex/ui/windowed_app.h>
#include <rex/ui/windowed_app_context_sdl.h>

#if defined(_WIN32)
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <objbase.h>
#include <processthreadsapi.h>
// Symbolizing the fault stack in WriteWindowsCrashRecord. refii_host is a static library
// inside refii_hostbridge.dll, which already links dbghelp for ReportEmbeddedFailure.
#include <dbghelp.h>
#include <cstring>
// Naming the guest side of a crash record: the payload's function table owns every host pc
// inside the recompiled module, the kernel's memory owns every host address inside the arena,
// and the faulting thread's PPC context carries the guest stack.
#include <rex/exception_handler.h>
#include <rex/system/mmio_handler.h>
#include <rex/system/thread_state.h>
#include <rex/system/xmemory.h>
#else
#include <cerrno>
#include <fcntl.h>
#include <pthread.h>
#include <unistd.h>
#endif

namespace {

int RunHostMainLoop(rex::ui::SDLWindowedAppContext& appContext) {
    return appContext.RunMainMessageLoop();
}

#if !defined(_WIN32)
std::atomic_bool g_posix_external_shutdown_requested{false};
std::once_flag g_posix_terminate_handler_once;
std::terminate_handler g_previous_posix_terminate_handler = nullptr;

void PosixExternalShutdownTerminateHandler() {
    if (g_posix_external_shutdown_requested.load(std::memory_order_acquire)) {
        try {
            std::exception_ptr exception = std::current_exception();
            if (exception) {
                std::rethrow_exception(exception);
            }
        } catch (const std::system_error& e) {
            std::fprintf(stderr, "External shutdown hit system error after termination request: %s\n", e.what());
            std::fflush(nullptr);
            std::_Exit(EXIT_SUCCESS);
        } catch (const std::exception& e) {
            std::fprintf(stderr, "External shutdown hit exception after termination request: %s\n", e.what());
            std::fflush(nullptr);
            std::_Exit(EXIT_SUCCESS);
        } catch (...) {
            std::fputs("External shutdown hit unknown exception after termination request\n", stderr);
            std::fflush(nullptr);
            std::_Exit(EXIT_SUCCESS);
        }

        std::fputs("External shutdown requested terminate without an active exception\n", stderr);
        std::fflush(nullptr);
        std::_Exit(EXIT_SUCCESS);
    }

    if (g_previous_posix_terminate_handler != nullptr) {
        g_previous_posix_terminate_handler();
    }
    std::abort();
}

void InstallPosixExternalShutdownTerminateHandler() {
    std::call_once(g_posix_terminate_handler_once, [] {
        g_previous_posix_terminate_handler =
            std::set_terminate(PosixExternalShutdownTerminateHandler);
    });
}

void ReinstatePosixExternalShutdownTerminateHandler() {
    InstallPosixExternalShutdownTerminateHandler();
    std::set_terminate(PosixExternalShutdownTerminateHandler);
}

void MarkPosixExternalShutdownInProgress() {
    g_posix_external_shutdown_requested.store(true, std::memory_order_release);
    ReinstatePosixExternalShutdownTerminateHandler();
}
#endif

std::vector<char*> BuildArgvPointers(std::vector<std::string>& ownedArguments) {
    std::vector<char*> argvPointers;
    argvPointers.reserve(ownedArguments.size());
    for (std::string& argument : ownedArguments) {
        argvPointers.push_back(argument.data());
    }
    return argvPointers;
}

void ApplyPositionalArguments(
    rex::ui::WindowedApp& app,
    const std::vector<std::string>& remainingArguments) {
    const auto& optionNames = app.GetPositionalOptions();
    std::map<std::string, std::string> parsedArguments;
    const std::size_t count = std::min(remainingArguments.size(), optionNames.size());
    for (std::size_t i = 0; i < count; ++i) {
        parsedArguments[optionNames[i]] = remainingArguments[i];
    }
    app.SetParsedArguments(std::move(parsedArguments));
}

std::function<void()> MakeShutdownCallback(rex::ui::WindowedApp& app) {
    return [&app]() {
        app.RequestExternalShutdown();
    };
}

#if !defined(_WIN32)
class PosixTerminationSignalBridge {
public:
    PosixTerminationSignalBridge() {
        int pipeFds[2] = {-1, -1};
        if (pipe(pipeFds) != 0) {
            return;
        }
        readFd_ = pipeFds[0];
        writeFd_ = pipeFds[1];
        fcntl(readFd_, F_SETFD, FD_CLOEXEC);
        fcntl(writeFd_, F_SETFD, FD_CLOEXEC);
        fcntl(writeFd_, F_SETFL, fcntl(writeFd_, F_GETFL, 0) | O_NONBLOCK);

        InstallHandlers();
    }

    ~PosixTerminationSignalBridge() {
        Stop();
        if (handlersCaptured_) {
            sigaction(SIGINT, &previousSigintAction_, nullptr);
            sigaction(SIGTERM, &previousSigtermAction_, nullptr);
            signalWriteFd_.store(-1, std::memory_order_release);
        }
        if (readFd_ != -1) {
            close(readFd_);
        }
        if (writeFd_ != -1) {
            close(writeFd_);
        }
    }

    PosixTerminationSignalBridge(const PosixTerminationSignalBridge&) = delete;
    PosixTerminationSignalBridge& operator=(const PosixTerminationSignalBridge&) = delete;

    void RefreshHandlers() {
        InstallHandlers();
    }

    void Start(
        rex::ui::WindowedAppContext& appContext,
        std::function<void()> shutdownCallback) {
        if (!installed_ || waiter_.joinable()) {
            return;
        }
        appContext_.store(&appContext, std::memory_order_release);
        shutdownCallback_ = std::move(shutdownCallback);
        waiter_ = std::thread([this]() {
            while (!stop_.load(std::memory_order_acquire)) {
                unsigned char signalByte = 0;
                const ssize_t readResult = read(readFd_, &signalByte, sizeof(signalByte));
                if (readResult < 0 && errno == EINTR) {
                    continue;
                }
                if (stop_.load(std::memory_order_acquire)) {
                    break;
                }
                if (readResult <= 0) {
                    continue;
                }
                std::fputs("Received termination signal; requesting graceful shutdown\n", stderr);
                MarkPosixExternalShutdownInProgress();
                if (appContext_.load(std::memory_order_acquire)) {
                    try {
                        shutdownCallback_();
                    } catch (const std::system_error& e) {
                        std::fprintf(stderr, "External shutdown callback hit system error: %s\n", e.what());
                    } catch (const std::exception& e) {
                        std::fprintf(stderr, "External shutdown callback hit exception: %s\n", e.what());
                    } catch (...) {
                        std::fputs("External shutdown callback hit unknown exception\n", stderr);
                    }
                    std::fflush(nullptr);
                }
                std::fputs("External shutdown request handled; exiting process\n", stderr);
                std::fflush(nullptr);
                std::_Exit(EXIT_SUCCESS);
            }
        });
    }

    void Stop() {
        stop_.store(true, std::memory_order_release);
        appContext_.store(nullptr, std::memory_order_release);
        if (waiter_.joinable()) {
            WakeSignalThread();
            waiter_.join();
        }
    }

private:
    void InstallHandlers() {
        if (writeFd_ == -1) {
            return;
        }
        struct sigaction action {};
        action.sa_handler = &SignalHandler;
        sigemptyset(&action.sa_mask);
        action.sa_flags = SA_RESTART;
        if (!handlersCaptured_) {
            sigaction(SIGINT, nullptr, &previousSigintAction_);
            sigaction(SIGTERM, nullptr, &previousSigtermAction_);
            handlersCaptured_ = true;
        }
        const bool sigintInstalled = sigaction(SIGINT, &action, nullptr) == 0;
        const bool sigtermInstalled = sigaction(SIGTERM, &action, nullptr) == 0;
        installed_ = sigintInstalled || sigtermInstalled;
        if (installed_) {
            signalWriteFd_.store(writeFd_, std::memory_order_release);
        }
    }

    static void SignalHandler(int) {
        const int fd = signalWriteFd_.load(std::memory_order_acquire);
        if (fd == -1) {
            return;
        }
        const unsigned char signalByte = 1;
        const ssize_t ignored = write(fd, &signalByte, sizeof(signalByte));
        (void)ignored;
    }

    void WakeSignalThread() const {
        if (writeFd_ == -1) {
            return;
        }
        const unsigned char signalByte = 0;
        const ssize_t ignored = write(writeFd_, &signalByte, sizeof(signalByte));
        (void)ignored;
    }

    std::atomic<rex::ui::WindowedAppContext*> appContext_{nullptr};
    struct sigaction previousSigintAction_ {};
    struct sigaction previousSigtermAction_ {};
    std::atomic<bool> stop_{false};
    int readFd_ = -1;
    int writeFd_ = -1;
    bool installed_ = false;
    bool handlersCaptured_ = false;
    std::function<void()> shutdownCallback_;
    std::thread waiter_;

    static std::atomic<int> signalWriteFd_;
};

std::atomic<int> PosixTerminationSignalBridge::signalWriteFd_{-1};

#endif

#if defined(_WIN32)
/// Names a host pc inside the recompiled game module as the guest function whose body holds
/// it, `sub_8XXXXXXX+0xNN` — the address IDA wants.
///
/// The module exports nothing a symbol lookup can see and ships no PDB, so `SymFromAddr` names
/// none of it and a fault there used to be a bare RVA into a module that is built on the
/// player's machine for the player's CPU, which no map on this side can resolve. The payload's
/// function table pairs every guest entry with its host body: the body owning `pc` is the one
/// with the greatest host address not above it, bounded by the next body up. A hook's strong
/// override is what the table holds for a hooked function, so hooks name themselves too.
/// Nothing here allocates or locks; it reads a table the payload published at load.
bool DescribeGuestFunction(uintptr_t pc, char* out, size_t capacity) {
    if (pc == 0 || !refii::payload::IsLoaded()) {
        return false;
    }
    const PPCFuncMapping* mappings = refii::payload::FuncMappings();
    if (mappings == nullptr) {
        return false;
    }
    uintptr_t bestHost = 0;
    size_t bestGuest = 0;
    uintptr_t nextHost = UINTPTR_MAX;
    for (size_t i = 0; mappings[i].guest != 0; ++i) {
        const uintptr_t host = reinterpret_cast<uintptr_t>(mappings[i].host);
        if (host == 0) {
            continue;
        }
        if (host <= pc) {
            if (host > bestHost) {
                bestHost = host;
                bestGuest = mappings[i].guest;
            }
        } else if (host < nextHost) {
            nextHost = host;
        }
    }
    if (bestHost == 0 || pc >= nextHost) {
        return false;
    }
    // Past the table's last body the bound above is open, so a pc in some other module would
    // otherwise be handed to the highest guest function. The body and the pc must share a
    // module.
    HMODULE pcModule = nullptr;
    HMODULE bodyModule = nullptr;
    constexpr DWORD kFromAddress =
        GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT;
    if (!GetModuleHandleExA(kFromAddress, reinterpret_cast<LPCSTR>(pc), &pcModule) ||
        !GetModuleHandleExA(kFromAddress, reinterpret_cast<LPCSTR>(bestHost), &bodyModule) ||
        pcModule != bodyModule) {
        return false;
    }
    std::snprintf(out, capacity, "sub_%08llX+0x%llX",
                  static_cast<unsigned long long>(bestGuest),
                  static_cast<unsigned long long>(pc - bestHost));
    return true;
}

/// The guest address a host address stands for, when it lies inside the guest arena: the 4 GB
/// virtual view first, the physical views above it (`Memory::MapViews`). A faulting access
/// address reported this way is the pointer the guest actually dereferenced.
bool GuestAddressOf(uintptr_t host, uint32_t* out, bool* physical) {
    rex::system::KernelState* kernel = rex::system::kernel_state();
    rex::memory::Memory* memory = kernel != nullptr ? kernel->memory() : nullptr;
    const uintptr_t base =
        memory != nullptr ? reinterpret_cast<uintptr_t>(memory->virtual_membase()) : 0;
    if (base == 0 || host < base) {
        return false;
    }
    const uintptr_t offset = host - base;
    if (offset >= 0x120000000ull) {
        return false;
    }
    *physical = offset >= 0x100000000ull;
    *out = static_cast<uint32_t>(*physical ? offset - 0x100000000ull : offset);
    return true;
}

/// A big-endian guest word, or false when the page is not committed. The Windows arena is a
/// SEC_RESERVE mapping (`CreateFileMappingHandle(..., commit=false)`), so an unallocated guest
/// page faults on read — and a fault inside the crash recorder would end the record mid-line.
/// No C++ object with a destructor lives in this frame, which is what SEH requires of it.
bool TryReadGuestWord(const uint8_t* base, uint32_t address, uint32_t* out) {
    __try {
        uint32_t raw = 0;
        std::memcpy(&raw, base + address, sizeof(raw));
        *out = _byteswap_ulong(raw);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

/// The faulting thread's GUEST stack, when it has one: `ThreadState::Get()` is the null-safe
/// TLS read, and the walk is the title's own back chain — the 32-bit word at [r1], saved LR at
/// back-chain - 8 — the same walk the Linux recorder does. Each frame is a guest return
/// address, which IDA takes as is.
void WriteGuestThread(FILE* crashLog) {
    rex::runtime::ThreadState* state = rex::runtime::ThreadState::Get();
    rex::system::KernelState* kernel = rex::system::kernel_state();
    rex::memory::Memory* memory = kernel != nullptr ? kernel->memory() : nullptr;
    if (state == nullptr || state->context() == nullptr || memory == nullptr) {
        return;
    }
    const uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return;
    }

    PPCContext* context = state->context();
    std::fprintf(crashLog, "guest_thread\ttid=%u r1=%08X lr=%08X\n",
                 static_cast<unsigned>(state->thread_id()), context->r1.u32,
                 static_cast<uint32_t>(context->lr));
    std::fprintf(crashLog, "guest_stack\n");
    uint32_t sp = context->r1.u32;
    for (int frame = 0; frame < 32 && sp >= 0x10000u && sp < 0xC0000000u; ++frame) {
        uint32_t back = 0;
        if (!TryReadGuestWord(base, sp, &back) || back == 0u || back <= sp ||
            back >= 0xC0000000u) {
            break;
        }
        uint32_t lr = 0;
        if (!TryReadGuestWord(base, back - 8u, &lr) || lr == 0u) {
            break;
        }
        std::fprintf(crashLog, "  %08X\n", lr);
        sp = back;
    }
}

void WriteWindowsCrashRecord(const char* source,
                             EXCEPTION_POINTERS* exceptionPointers) {
    char modulePath[MAX_PATH] = {};
    const DWORD modulePathLength =
        GetModuleFileNameA(nullptr, modulePath, static_cast<DWORD>(sizeof(modulePath)));
    if (modulePathLength == 0 || modulePathLength >= sizeof(modulePath)) {
        return;
    }

    char* lastSlash = std::max(std::strrchr(modulePath, '\\'),
                               std::strrchr(modulePath, '/'));
    if (lastSlash == nullptr) {
        return;
    }
    *lastSlash = '\0';

    std::string logsDirectory = std::string(modulePath) + "\\logs";
    CreateDirectoryA(logsDirectory.c_str(), nullptr);

    const std::string crashLogPath = logsDirectory + "\\refii-crash.log";
    FILE* crashLog = nullptr;
    if (fopen_s(&crashLog, crashLogPath.c_str(), "ab") != 0 || crashLog == nullptr) {
        return;
    }

    DWORD exceptionCode = 0;
    void* exceptionAddress = nullptr;
    void* instructionPointer = nullptr;
    bool haveAccessInfo = false;
    ULONG_PTR accessKind = 0;
    ULONG_PTR accessAddress = 0;
    if (exceptionPointers != nullptr) {
        if (exceptionPointers->ExceptionRecord != nullptr) {
            exceptionCode = exceptionPointers->ExceptionRecord->ExceptionCode;
            exceptionAddress = exceptionPointers->ExceptionRecord->ExceptionAddress;
            if (exceptionCode == EXCEPTION_ACCESS_VIOLATION &&
                exceptionPointers->ExceptionRecord->NumberParameters >= 2) {
                haveAccessInfo = true;
                accessKind =
                    exceptionPointers->ExceptionRecord->ExceptionInformation[0];
                accessAddress =
                    exceptionPointers->ExceptionRecord->ExceptionInformation[1];
            }
        }
        if (exceptionPointers->ContextRecord != nullptr) {
#if defined(_M_X64) || defined(__x86_64__)
            instructionPointer =
                reinterpret_cast<void*>(exceptionPointers->ContextRecord->Rip);
#elif defined(_M_IX86) || defined(__i386__)
            instructionPointer =
                reinterpret_cast<void*>(exceptionPointers->ContextRecord->Eip);
#endif
        }
    }

    std::fprintf(crashLog, "=== refii unhandled exception ===\n");
    std::fprintf(crashLog, "source\t%s\n", source != nullptr ? source : "unknown");
    // Which build wrote this. The record file is append-only and lives beside the exe, so it
    // carries records across upgrades; without this line a beta-5 fault reads as a beta-6 one.
    std::fprintf(crashLog, "build\t%s\n", REFII_BUILD_ID);
    std::fprintf(crashLog, "process_id\t%lu\n",
                 static_cast<unsigned long>(GetCurrentProcessId()));
    std::fprintf(crashLog, "thread_id\t%lu\n",
                 static_cast<unsigned long>(GetCurrentThreadId()));
    std::fprintf(crashLog, "code\t0x%08lX\n", static_cast<unsigned long>(exceptionCode));
    std::fprintf(crashLog, "exception_address\t%p\n", exceptionAddress);
    std::fprintf(crashLog, "instruction_pointer\t%p\n", instructionPointer);
    // THE MODULE CONTAINING THE FAULT, not the process image.
    //
    // This used to report `GetModuleHandleA(nullptr)` — the EXE — and subtract that from the
    // instruction pointer. Embedded, the exe is Godot, while every interesting fault is inside
    // refii_hostbridge.dll or rexruntime.dll, so the "rva" was a difference between two
    // unrelated modules: a large number that resolves to nothing and cannot be looked up in any
    // map. Name the owning module and give an RVA relative to IT, which is what
    // refii_hostbridge.map is keyed by (see the /MAP: link option in src/hostbridge).
    HMODULE faultModule = nullptr;
    char faultModuleName[MAX_PATH] = "?";
    if (instructionPointer != nullptr &&
        GetModuleHandleExA(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
                               GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                           reinterpret_cast<LPCSTR>(instructionPointer), &faultModule) &&
        faultModule != nullptr) {
        char path[MAX_PATH] = {};
        if (GetModuleFileNameA(faultModule, path, MAX_PATH) != 0) {
            const char* slash = std::strrchr(path, '\\');
            std::snprintf(faultModuleName, sizeof(faultModuleName), "%s",
                          slash != nullptr ? slash + 1 : path);
        }
    }

    std::fprintf(crashLog, "fault_module\t%s\n", faultModuleName);
    std::fprintf(crashLog, "fault_module_base\t%p\n",
                 reinterpret_cast<void*>(faultModule));
    if (instructionPointer != nullptr && faultModule != nullptr) {
        std::fprintf(crashLog, "fault_rva\t0x%llX\n",
                     static_cast<unsigned long long>(
                         reinterpret_cast<uintptr_t>(instructionPointer) -
                         reinterpret_cast<uintptr_t>(faultModule)));
    }
    // The guest side of the fault: which recompiled function, and which guest pointer. An RVA
    // into refii_generated.dll resolves to nothing off the player's machine; `sub_8XXXXXXX` and
    // a guest address are what the binary can be asked about.
    char guestFunction[64];
    if (DescribeGuestFunction(reinterpret_cast<uintptr_t>(instructionPointer), guestFunction,
                              sizeof(guestFunction))) {
        std::fprintf(crashLog, "guest_function\t%s\n", guestFunction);
    }
    if (haveAccessInfo) {
        const char* kindText = accessKind == 0   ? "read"
                               : accessKind == 1 ? "write"
                               : accessKind == 8 ? "execute"
                                                 : "unknown";
        std::fprintf(crashLog, "access_kind\t%s\n", kindText);
        std::fprintf(crashLog, "access_address\t0x%llX\n",
                     static_cast<unsigned long long>(accessAddress));
        uint32_t guestAddress = 0;
        bool physical = false;
        if (GuestAddressOf(static_cast<uintptr_t>(accessAddress), &guestAddress, &physical)) {
            std::fprintf(crashLog, "guest_address\t0x%08X%s\n", guestAddress,
                         physical ? " (physical view)" : "");
        }
    }
    // Everything above is on disk before anything below walks memory the fault just proved
    // unreliable.
    std::fflush(crashLog);
    // THE STACK, symbolized as far as it can be.
    //
    // Without this a crash record is an address and nothing else, and on this box there is no
    // debugger to open a dump with — so an intermittent fault stays unattributable however many
    // times it happens. `StackWalk64` on the exception's own ContextRecord gives the faulting
    // thread's real frames. dbghelp is already linked (refii_host is a static lib inside
    // refii_hostbridge.dll); there are no PDBs, so `SymFromAddr` resolves exports only and the
    // rest is module+RVA — which is exactly what refii_hostbridge.map is keyed by.
    //
    // StackWalk64 MUTATES the context it walks, so it gets a copy: the real one belongs to the
    // exception dispatcher and corrupting it would turn a diagnosable fault into a mystery.
    if (exceptionPointers != nullptr && exceptionPointers->ContextRecord != nullptr) {
        CONTEXT walkContext = *exceptionPointers->ContextRecord;
        STACKFRAME64 frame = {};
        // The seed registers and the machine type are the only architecture-specific part of
        // this walk: ARM64's CONTEXT has no Rip/Rbp/Rsp, and its frame pointer is X29, which
        // winnt.h surfaces as CONTEXT::Fp.
#if defined(_M_ARM64) || defined(__aarch64__)
        constexpr DWORD kStackWalkMachine = IMAGE_FILE_MACHINE_ARM64;
        frame.AddrPC.Offset = walkContext.Pc;
        frame.AddrPC.Mode = AddrModeFlat;
        frame.AddrFrame.Offset = walkContext.Fp;
        frame.AddrFrame.Mode = AddrModeFlat;
        frame.AddrStack.Offset = walkContext.Sp;
        frame.AddrStack.Mode = AddrModeFlat;
#else
        constexpr DWORD kStackWalkMachine = IMAGE_FILE_MACHINE_AMD64;
        frame.AddrPC.Offset = walkContext.Rip;
        frame.AddrPC.Mode = AddrModeFlat;
        frame.AddrFrame.Offset = walkContext.Rbp;
        frame.AddrFrame.Mode = AddrModeFlat;
        frame.AddrStack.Offset = walkContext.Rsp;
        frame.AddrStack.Mode = AddrModeFlat;
#endif

        const HANDLE process = GetCurrentProcess();
        static std::once_flag symbolsOnce;
        std::call_once(symbolsOnce, [process] {
            SymSetOptions(SYMOPT_DEFERRED_LOADS | SYMOPT_UNDNAME);
            SymInitialize(process, nullptr, TRUE);
        });

        alignas(SYMBOL_INFO) char symbolStorage[sizeof(SYMBOL_INFO) + 512] = {};
        auto* symbol = reinterpret_cast<SYMBOL_INFO*>(symbolStorage);
        symbol->SizeOfStruct = sizeof(SYMBOL_INFO);
        symbol->MaxNameLen = 511;

        std::fprintf(crashLog, "stack\n");
        for (int depth = 0; depth < 24; ++depth) {
            if (!StackWalk64(kStackWalkMachine, process, GetCurrentThread(), &frame,
                             &walkContext, nullptr, SymFunctionTableAccess64,
                             SymGetModuleBase64, nullptr) ||
                frame.AddrPC.Offset == 0) {
                break;
            }

            const auto pc = static_cast<uintptr_t>(frame.AddrPC.Offset);
            HMODULE owner = nullptr;
            char ownerName[MAX_PATH] = "?";
            uintptr_t rva = 0;
            if (GetModuleHandleExA(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
                                       GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                                   reinterpret_cast<LPCSTR>(pc), &owner) &&
                owner != nullptr) {
                char path[MAX_PATH] = {};
                if (GetModuleFileNameA(owner, path, MAX_PATH) != 0) {
                    const char* slash = std::strrchr(path, '\\');
                    std::snprintf(ownerName, sizeof(ownerName), "%s",
                                  slash != nullptr ? slash + 1 : path);
                }
                rva = pc - reinterpret_cast<uintptr_t>(owner);
            }

            DWORD64 displacement = 0;
            const char* name =
                SymFromAddr(process, frame.AddrPC.Offset, &displacement, symbol) ? symbol->Name
                                                                                : "";
            // A frame in the recompiled module has no export to be named by; the payload's
            // table names it.
            if (name[0] == '\0' &&
                DescribeGuestFunction(pc, guestFunction, sizeof(guestFunction))) {
                name = guestFunction;
            }
            std::fprintf(crashLog, "  #%02d\t%s+0x%llX\t%s\n", depth, ownerName,
                         static_cast<unsigned long long>(rva), name);
        }
    }
    std::fflush(crashLog);

    // The host walk stops at the first recompiled frame (no frame pointers, and the generated
    // code's unwind data is not what StackWalk64 follows), so the guest's own back chain is
    // the stack that says how the title got here.
    WriteGuestThread(crashLog);

    std::fprintf(crashLog, "\n");
    std::fclose(crashLog);
}

/// The fault the vectored path already recorded on this thread, so the unhandled filter does
/// not write it a second time when the same exception reaches it (a guest thread's does).
thread_local const void* t_recordedExceptionAddress = nullptr;
thread_local ULONG_PTR t_recordedAccessAddress = 0;

bool AlreadyRecorded(const EXCEPTION_POINTERS* exceptionPointers) {
    const EXCEPTION_RECORD* record = exceptionPointers->ExceptionRecord;
    const ULONG_PTR access =
        record->NumberParameters >= 2 ? record->ExceptionInformation[1] : 0;
    return t_recordedExceptionAddress == record->ExceptionAddress &&
           t_recordedAccessAddress == access;
}

void NoteRecorded(const EXCEPTION_POINTERS* exceptionPointers) {
    const EXCEPTION_RECORD* record = exceptionPointers->ExceptionRecord;
    t_recordedExceptionAddress = record->ExceptionAddress;
    t_recordedAccessAddress =
        record->NumberParameters >= 2 ? record->ExceptionInformation[1] : 0;
}

LONG WINAPI RefiiWindowsUnhandledExceptionFilter(
    EXCEPTION_POINTERS* exceptionPointers) {
    if (!AlreadyRecorded(exceptionPointers)) {
        WriteWindowsCrashRecord("unhandled_exception_filter", exceptionPointers);
    }
    return EXCEPTION_EXECUTE_HANDLER;
}

/// A read or write of the guest arena that the runtime declined to handle, seen in its
/// vectored handler — BEFORE the managed runtime takes the fault. On a managed thread (Godot's
/// main thread reading guest state through the host bridge) coreclr's own handling ends the
/// process without ever reaching SetUnhandledExceptionFilter, so a fault there left no record:
/// a player's log ended at "Unhandled guest access violation: read of guest 0x4328910A on
/// thread 0x19E4" (2026-10-24) and nothing named the reader. This writes the record from the
/// one place that sees every such fault.
void RecordUnhandledGuestFault(uint32_t /*guestAddress*/, bool /*isWrite*/,
                               rex::arch::Exception* exception) {
    auto* exceptionPointers =
        static_cast<EXCEPTION_POINTERS*>(exception != nullptr ? exception->native_info() : nullptr);
    if (exceptionPointers == nullptr) {
        return;
    }
    WriteWindowsCrashRecord("guest_access_violation", exceptionPointers);
    NoteRecorded(exceptionPointers);
}

void InstallWindowsCrashRecorder() {
    static std::once_flag once;
    std::call_once(once, [] {
        // Guest-memory probes and MMIO can handle access violations normally. A vectored
        // handler runs before those handlers, so walking and logging every fault here
        // turns normal reads into synchronous crash reports. Record only unhandled faults:
        // the runtime names them in its own vectored handler, and the filter takes the rest.
        rex::runtime::SetUnhandledGuestFaultObserver(RecordUnhandledGuestFault);
        SetUnhandledExceptionFilter(RefiiWindowsUnhandledExceptionFilter);
    });
}

class WindowsConsoleControlBridge {
public:
    WindowsConsoleControlBridge(
        rex::ui::WindowedAppContext& appContext,
        std::function<void()> shutdownCallback)
        : appContext_(&appContext), shutdownCallback_(std::move(shutdownCallback)) {
        {
            std::lock_guard<std::mutex> lock(activeContextMutex_);
            activeContext_ = appContext_;
            activeShutdownCallback_ = &shutdownCallback_;
        }
        installed_ = SetConsoleCtrlHandler(&ConsoleControlHandler, TRUE) != FALSE;
        if (!installed_) {
            std::lock_guard<std::mutex> lock(activeContextMutex_);
            if (activeContext_ == appContext_) {
                activeContext_ = nullptr;
                activeShutdownCallback_ = nullptr;
            }
        }
    }

    ~WindowsConsoleControlBridge() {
        if (installed_) {
            SetConsoleCtrlHandler(&ConsoleControlHandler, FALSE);
        }
        std::lock_guard<std::mutex> lock(activeContextMutex_);
        if (activeContext_ == appContext_) {
            activeContext_ = nullptr;
            activeShutdownCallback_ = nullptr;
        }
    }

    WindowsConsoleControlBridge(const WindowsConsoleControlBridge&) = delete;
    WindowsConsoleControlBridge& operator=(const WindowsConsoleControlBridge&) = delete;

private:
    static BOOL WINAPI ConsoleControlHandler(DWORD controlType) {
        switch (controlType) {
            case CTRL_C_EVENT:
            case CTRL_BREAK_EVENT:
            case CTRL_CLOSE_EVENT:
            case CTRL_LOGOFF_EVENT:
            case CTRL_SHUTDOWN_EVENT:
                break;
            default:
                return FALSE;
        }

        std::function<void()> shutdownCallback;
        {
            std::lock_guard<std::mutex> lock(activeContextMutex_);
            if (activeContext_ == nullptr || activeShutdownCallback_ == nullptr) {
                return FALSE;
            }
            shutdownCallback = *activeShutdownCallback_;
        }
        shutdownCallback();
        return TRUE;
    }

    rex::ui::WindowedAppContext* appContext_ = nullptr;
    std::function<void()> shutdownCallback_;
    bool installed_ = false;

    static std::mutex activeContextMutex_;
    static rex::ui::WindowedAppContext* activeContext_;
    static std::function<void()>* activeShutdownCallback_;
};

std::mutex WindowsConsoleControlBridge::activeContextMutex_;
rex::ui::WindowedAppContext* WindowsConsoleControlBridge::activeContext_ = nullptr;
std::function<void()>* WindowsConsoleControlBridge::activeShutdownCallback_ = nullptr;

int RunEmbeddedOnWindows(std::vector<std::string> ownedArguments) {
    InstallWindowsCrashRecorder();

    std::vector<char*> argvPointers = BuildArgvPointers(ownedArguments);
    auto remaining = rex::cvar::Init(static_cast<int>(argvPointers.size()), argvPointers.data());
    rex::cvar::ApplyEnvironment();

    rex::ui::SDLWindowedAppContext appContext;
    if (!appContext.Initialize()) {
        return EXIT_FAILURE;
    }
    std::unique_ptr<rex::ui::WindowedApp> app = CreateRefiiApp(appContext);
    ApplyPositionalArguments(*app, remaining);
    WindowsConsoleControlBridge consoleControlBridge(appContext, MakeShutdownCallback(*app));

    const HRESULT coInitializeResult = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    if (FAILED(coInitializeResult)) {
        return EXIT_FAILURE;
    }

    const int result = app->OnInitialize() ? RunHostMainLoop(appContext) : EXIT_FAILURE;

    app->InvokeOnDestroy();
    rex::ShutdownLogging();
    CoUninitialize();
    return result;
}
#else
int RunEmbeddedOnPosix(std::vector<std::string> ownedArguments) {
    InstallPosixExternalShutdownTerminateHandler();

    std::vector<char*> argvPointers = BuildArgvPointers(ownedArguments);
    PosixTerminationSignalBridge signalBridge;

    auto remaining = rex::cvar::Init(
        static_cast<int>(argvPointers.size()),
        argvPointers.data());
    rex::cvar::ApplyEnvironment();

    rex::ui::SDLWindowedAppContext appContext;
    if (!appContext.Initialize()) {
        std::fputs("Failed to initialize SDL3 video\n", stderr);
        return EXIT_FAILURE;
    }
    std::unique_ptr<rex::ui::WindowedApp> app = CreateRefiiApp(appContext);
    ApplyPositionalArguments(*app, remaining);
    signalBridge.Start(appContext, MakeShutdownCallback(*app));

    int result = EXIT_FAILURE;
    if (app->OnInitialize()) {
        signalBridge.RefreshHandlers();
        RunHostMainLoop(appContext);
        MarkPosixExternalShutdownInProgress();
        result = EXIT_SUCCESS;
    }

    signalBridge.Stop();
    app->InvokeOnDestroy();
    rex::ShutdownLogging();
    return result;
}
#endif
}  // namespace

namespace {
std::filesystem::path g_refii_app_root;

}  // namespace

void RefiiSetAppRoot(const std::filesystem::path& root) { g_refii_app_root = root; }

const std::filesystem::path& RefiiGetAppRoot() { return g_refii_app_root; }

std::unique_ptr<rex::ui::WindowedApp> CreateRefiiApp(rex::ui::WindowedAppContext& app_context) {
#if defined(_WIN32)
    InstallWindowsCrashRecorder();
#endif
    return RefiiApp::Create(app_context);
}

int RunRefiiEmbedded(
    const std::filesystem::path& game_directory,
    const std::vector<std::string>& launch_arguments) {
    std::vector<std::string> ownedArguments;
    ownedArguments.reserve(launch_arguments.size() + 2);
    ownedArguments.emplace_back("refii");
    ownedArguments.insert(ownedArguments.end(), launch_arguments.begin(), launch_arguments.end());
    ownedArguments.push_back(game_directory.string());

#if defined(_WIN32)
    return RunEmbeddedOnWindows(std::move(ownedArguments));
#else
    return RunEmbeddedOnPosix(std::move(ownedArguments));
#endif
}
