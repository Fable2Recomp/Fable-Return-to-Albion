// refii - ReXGlue Recompiled Project
//
// Shared host entry points for standalone and embedded launches.

#include "refii_host.h"

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
#else
#include <cerrno>
#include <fcntl.h>
#include <pthread.h>
#include <unistd.h>
#endif

namespace {

// The stable-tree shadow context names the loop RunMainSDLLoop; the netplay
// SDK's own SDL context names it RunMainMessageLoop.
int RunHostMainLoop(rex::ui::SDLWindowedAppContext& appContext) {
#if defined(REFII_REXGLUE_NETPLAY) && REFII_REXGLUE_NETPLAY
    return appContext.RunMainMessageLoop();
#else
    return appContext.RunMainSDLLoop();
#endif
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
#if REFII_HAS_RENDERER_RUNTIME
        refii::renderer::ShutdownRendererPresentPump();
        refii::renderer::RequestPlumeBackendShutdown();
#endif
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
    if (haveAccessInfo) {
        const char* kindText = accessKind == 0   ? "read"
                               : accessKind == 1 ? "write"
                               : accessKind == 8 ? "execute"
                                                 : "unknown";
        std::fprintf(crashLog, "access_kind\t%s\n", kindText);
        std::fprintf(crashLog, "access_address\t0x%llX\n",
                     static_cast<unsigned long long>(accessAddress));
    }
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
            std::fprintf(crashLog, "  #%02d\t%s+0x%llX\t%s\n", depth, ownerName,
                         static_cast<unsigned long long>(rva), name);
        }
    }

    std::fprintf(crashLog, "\n");
    std::fclose(crashLog);
}

LONG WINAPI RefiiWindowsUnhandledExceptionFilter(
    EXCEPTION_POINTERS* exceptionPointers) {
    WriteWindowsCrashRecord("unhandled_exception_filter", exceptionPointers);
    return EXCEPTION_EXECUTE_HANDLER;
}

LONG WINAPI RefiiWindowsVectoredExceptionHandler(
    EXCEPTION_POINTERS* exceptionPointers) {
    if (exceptionPointers != nullptr &&
        exceptionPointers->ExceptionRecord != nullptr &&
        exceptionPointers->ExceptionRecord->ExceptionCode == EXCEPTION_ACCESS_VIOLATION) {
        WriteWindowsCrashRecord("vectored_exception_handler", exceptionPointers);
    }
    return EXCEPTION_CONTINUE_SEARCH;
}

void InstallWindowsCrashRecorder() {
    static std::once_flag once;
    std::call_once(once, [] {
        AddVectoredExceptionHandler(1, RefiiWindowsVectoredExceptionHandler);
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
