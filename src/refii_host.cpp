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
#else
#include <cerrno>
#include <fcntl.h>
#include <pthread.h>
#include <unistd.h>
#endif

namespace {

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

    const int result = app->OnInitialize() ? appContext.RunMainSDLLoop() : EXIT_FAILURE;

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
        appContext.RunMainSDLLoop();
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

std::unique_ptr<rex::ui::WindowedApp> CreateRefiiApp(rex::ui::WindowedAppContext& app_context) {
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
