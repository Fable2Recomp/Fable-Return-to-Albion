/**
 * @file        ui/windowed_app_main_sdl.cpp
 * @brief       Entry point for windowed applications (SDL3 windowing)
 *
 * @copyright   Copyright (c) 2026 Tom Clay <tomc@tctechstuff.com>
 *              All rights reserved.
 *
 * @license     BSD 3-Clause License
 *              See LICENSE file in the project root for full license text.
 *
 * @remarks     Refii shadow: adds the external-shutdown machinery (terminate
 *              handler + POSIX signal bridge / Win32 console-control bridge)
 *              so SIGTERM/SIGINT and console close events terminate the guest
 *              title gracefully instead of killing the process mid-frame.
 */

#include <algorithm>
#include <atomic>
#include <cstdio>
#include <cstdlib>
#include <exception>
#include <functional>
#include <map>
#include <memory>
#include <mutex>
#include <string>
#include <system_error>
#include <vector>

#include <rex/cvar.h>
#include <rex/logging.h>
#include <rex/platform.h>
#include <rex/ui/windowed_app.h>
#include <rex/ui/windowed_app_context_sdl.h>

#if REX_PLATFORM_WIN32
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <objbase.h>
#include <shellapi.h>
#else
#include <cerrno>
#include <csignal>
#include <fcntl.h>
#include <thread>
#include <unistd.h>
#endif

namespace {

std::atomic_bool g_external_shutdown_requested{false};
std::once_flag g_terminate_handler_once;
std::terminate_handler g_previous_terminate_handler = nullptr;

void ExternalShutdownTerminateHandler() {
  if (g_external_shutdown_requested.load(std::memory_order_acquire) ||
      rex::ui::AppTeardownStarted()) {
    try {
      std::exception_ptr exception = std::current_exception();
      if (exception) {
        std::rethrow_exception(exception);
      }
    } catch (const std::system_error& e) {
      std::fprintf(stderr,
                   "External shutdown hit system error after termination "
                   "request: %s\n",
                   e.what());
    } catch (const std::exception& e) {
      std::fprintf(stderr,
                   "External shutdown hit exception after termination request: "
                   "%s\n",
                   e.what());
    } catch (...) {
      std::fputs(
          "External shutdown hit unknown exception after termination request\n",
          stderr);
    }
    std::fflush(nullptr);
    std::_Exit(EXIT_SUCCESS);
  }

  if (g_previous_terminate_handler != nullptr) {
    g_previous_terminate_handler();
  }
  std::abort();
}

void InstallExternalShutdownTerminateHandler() {
  std::call_once(g_terminate_handler_once, [] {
    g_previous_terminate_handler =
        std::set_terminate(ExternalShutdownTerminateHandler);
  });
}

void ReinstateExternalShutdownTerminateHandler() {
  InstallExternalShutdownTerminateHandler();
  std::set_terminate(ExternalShutdownTerminateHandler);
}

void MarkExternalShutdownInProgress() {
  g_external_shutdown_requested.store(true, std::memory_order_release);
  ReinstateExternalShutdownTerminateHandler();
}

void ApplyPositionalArguments(rex::ui::WindowedApp& app,
                              const std::vector<std::string>& remaining) {
  const auto& option_names = app.GetPositionalOptions();
  std::map<std::string, std::string> parsed;
  size_t count = std::min(remaining.size(), option_names.size());
  for (size_t i = 0; i < count; ++i) {
    parsed[option_names[i]] = remaining[i];
  }
  app.SetParsedArguments(std::move(parsed));
}

std::function<void()> MakeShutdownCallback(rex::ui::WindowedApp& app) {
  return [&app]() {
    app.RequestExternalShutdown();
  };
}

#if REX_PLATFORM_WIN32

class ConsoleControlBridge {
 public:
  ConsoleControlBridge(rex::ui::WindowedAppContext& app_context,
                       std::function<void()> shutdown_callback)
      : app_context_(&app_context), shutdown_callback_(std::move(shutdown_callback)) {
    {
      std::lock_guard<std::mutex> lock(active_context_mutex_);
      active_context_ = app_context_;
      active_shutdown_callback_ = &shutdown_callback_;
    }
    installed_ = SetConsoleCtrlHandler(&ConsoleControlHandler, TRUE) != FALSE;
    if (!installed_) {
      std::lock_guard<std::mutex> lock(active_context_mutex_);
      if (active_context_ == app_context_) {
        active_context_ = nullptr;
        active_shutdown_callback_ = nullptr;
      }
    }
  }

  ~ConsoleControlBridge() {
    if (installed_) {
      SetConsoleCtrlHandler(&ConsoleControlHandler, FALSE);
    }
    std::lock_guard<std::mutex> lock(active_context_mutex_);
    if (active_context_ == app_context_) {
      active_context_ = nullptr;
      active_shutdown_callback_ = nullptr;
    }
  }

  ConsoleControlBridge(const ConsoleControlBridge&) = delete;
  ConsoleControlBridge& operator=(const ConsoleControlBridge&) = delete;

 private:
  static BOOL WINAPI ConsoleControlHandler(DWORD control_type) {
    switch (control_type) {
      case CTRL_C_EVENT:
      case CTRL_BREAK_EVENT:
      case CTRL_CLOSE_EVENT:
      case CTRL_LOGOFF_EVENT:
      case CTRL_SHUTDOWN_EVENT:
        break;
      default:
        return FALSE;
    }

    std::function<void()> shutdown_callback;
    {
      std::lock_guard<std::mutex> lock(active_context_mutex_);
      if (!active_context_ || !active_shutdown_callback_) {
        return FALSE;
      }
      shutdown_callback = *active_shutdown_callback_;
    }
    MarkExternalShutdownInProgress();
    shutdown_callback();
    return TRUE;
  }

  rex::ui::WindowedAppContext* app_context_ = nullptr;
  std::function<void()> shutdown_callback_;
  bool installed_ = false;

  static std::mutex active_context_mutex_;
  static rex::ui::WindowedAppContext* active_context_;
  static std::function<void()>* active_shutdown_callback_;
};

std::mutex ConsoleControlBridge::active_context_mutex_;
rex::ui::WindowedAppContext* ConsoleControlBridge::active_context_ = nullptr;
std::function<void()>* ConsoleControlBridge::active_shutdown_callback_ = nullptr;

#else

class PosixTerminationSignalBridge {
 public:
  PosixTerminationSignalBridge() {
    int pipe_fds[2] = {-1, -1};
    if (pipe(pipe_fds) != 0) {
      return;
    }
    read_fd_ = pipe_fds[0];
    write_fd_ = pipe_fds[1];
    fcntl(read_fd_, F_SETFD, FD_CLOEXEC);
    fcntl(write_fd_, F_SETFD, FD_CLOEXEC);
    fcntl(write_fd_, F_SETFL, fcntl(write_fd_, F_GETFL, 0) | O_NONBLOCK);

    InstallHandlers();
  }

  ~PosixTerminationSignalBridge() {
    Stop();
    if (handlers_captured_) {
      sigaction(SIGINT, &previous_sigint_action_, nullptr);
      sigaction(SIGTERM, &previous_sigterm_action_, nullptr);
      signal_write_fd_.store(-1, std::memory_order_release);
    }
    if (read_fd_ != -1) {
      close(read_fd_);
    }
    if (write_fd_ != -1) {
      close(write_fd_);
    }
  }

  PosixTerminationSignalBridge(const PosixTerminationSignalBridge&) = delete;
  PosixTerminationSignalBridge& operator=(const PosixTerminationSignalBridge&) = delete;

  void RefreshHandlers() {
    InstallHandlers();
  }

  void Start(rex::ui::WindowedAppContext& app_context,
             std::function<void()> shutdown_callback) {
    if (!installed_ || waiter_.joinable()) {
      return;
    }
    app_context_.store(&app_context, std::memory_order_release);
    shutdown_callback_ = std::move(shutdown_callback);
    waiter_ = std::thread([this]() {
      while (!stop_.load(std::memory_order_acquire)) {
        unsigned char signal_byte = 0;
        const ssize_t read_result = read(read_fd_, &signal_byte, sizeof(signal_byte));
        if (read_result < 0 && errno == EINTR) {
          continue;
        }
        if (stop_.load(std::memory_order_acquire)) {
          break;
        }
        if (read_result <= 0) {
          continue;
        }
        std::fputs("Received termination signal; requesting graceful shutdown\n", stderr);
        MarkExternalShutdownInProgress();
        if (app_context_.load(std::memory_order_acquire)) {
          try {
            shutdown_callback_();
          } catch (const std::system_error& e) {
            std::fprintf(stderr, "External shutdown callback hit system error: %s\n",
                         e.what());
          } catch (const std::exception& e) {
            std::fprintf(stderr, "External shutdown callback hit exception: %s\n",
                         e.what());
          } catch (...) {
            std::fputs("External shutdown callback hit unknown exception\n",
                       stderr);
          }
          std::fflush(nullptr);
        }
        std::fputs("External shutdown request handled; exiting process\n",
                   stderr);
        std::fflush(nullptr);
        std::_Exit(EXIT_SUCCESS);
      }
    });
  }

  void Stop() {
    stop_.store(true, std::memory_order_release);
    app_context_.store(nullptr, std::memory_order_release);
    if (waiter_.joinable()) {
      WakeSignalThread();
      waiter_.join();
    }
  }

 private:
  void InstallHandlers() {
    if (write_fd_ == -1) {
      return;
    }
    struct sigaction action {};
    action.sa_handler = &SignalHandler;
    sigemptyset(&action.sa_mask);
    action.sa_flags = SA_RESTART;
    if (!handlers_captured_) {
      sigaction(SIGINT, nullptr, &previous_sigint_action_);
      sigaction(SIGTERM, nullptr, &previous_sigterm_action_);
      handlers_captured_ = true;
    }
    const bool sigint_installed = sigaction(SIGINT, &action, nullptr) == 0;
    const bool sigterm_installed = sigaction(SIGTERM, &action, nullptr) == 0;
    installed_ = sigint_installed || sigterm_installed;
    if (installed_) {
      signal_write_fd_.store(write_fd_, std::memory_order_release);
    }
  }

  static void SignalHandler(int) {
    const int fd = signal_write_fd_.load(std::memory_order_acquire);
    if (fd == -1) {
      return;
    }
    const unsigned char signal_byte = 1;
    const ssize_t ignored = write(fd, &signal_byte, sizeof(signal_byte));
    (void)ignored;
  }

  void WakeSignalThread() const {
    if (write_fd_ == -1) {
      return;
    }
    const unsigned char signal_byte = 0;
    const ssize_t ignored = write(write_fd_, &signal_byte, sizeof(signal_byte));
    (void)ignored;
  }

  std::atomic<rex::ui::WindowedAppContext*> app_context_{nullptr};
  struct sigaction previous_sigint_action_ {};
  struct sigaction previous_sigterm_action_ {};
  std::atomic<bool> stop_{false};
  int read_fd_ = -1;
  int write_fd_ = -1;
  bool installed_ = false;
  bool handlers_captured_ = false;
  std::function<void()> shutdown_callback_;
  std::thread waiter_;

  static std::atomic<int> signal_write_fd_;
};

std::atomic<int> PosixTerminationSignalBridge::signal_write_fd_{-1};

#endif  // REX_PLATFORM_WIN32

int RunWindowedApp(int argc, char** argv) {
  InstallExternalShutdownTerminateHandler();
#if !REX_PLATFORM_WIN32
  PosixTerminationSignalBridge termination_signal_bridge;
#endif

  auto remaining = rex::cvar::Init(argc, argv);
  rex::cvar::ApplyEnvironment();
  rex::InitLoggingEarly();

  int result = EXIT_FAILURE;
  {
    rex::ui::SDLWindowedAppContext app_context;
    if (!app_context.Initialize()) {
      return EXIT_FAILURE;
    }

#if REX_PLATFORM_WIN32
    // Apartment-threaded COM for shell dialogs.
    if (FAILED(CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED))) {
      return EXIT_FAILURE;
    }
#endif

    std::unique_ptr<rex::ui::WindowedApp> app = rex::ui::GetWindowedAppCreator()(app_context);

    ApplyPositionalArguments(*app, remaining);

#if REX_PLATFORM_WIN32
    ConsoleControlBridge console_control_bridge(app_context, MakeShutdownCallback(*app));
#else
    termination_signal_bridge.Start(app_context, MakeShutdownCallback(*app));
#endif

    if (app->OnInitialize()) {
#if !REX_PLATFORM_WIN32
      // SDL may have replaced our SIGINT/SIGTERM handlers during init.
      termination_signal_bridge.RefreshHandlers();
#endif
      result = app_context.RunMainMessageLoop();
      MarkExternalShutdownInProgress();
    }

#if !REX_PLATFORM_WIN32
    termination_signal_bridge.Stop();
#endif
    app->InvokeOnDestroy();
  }

#if REX_PLATFORM_WIN32
  CoUninitialize();
#endif

  return result;
}

#if REX_PLATFORM_WIN32
// Convert wide argv from CommandLineToArgvW to UTF-8 for cvar::Init.
std::vector<std::string> WideArgsToUtf8(int argc, wchar_t** wargv) {
  std::vector<std::string> args;
  args.reserve(static_cast<size_t>(argc));
  for (int i = 0; i < argc; ++i) {
    std::wstring wide(wargv[i]);
    if (wide.empty()) {
      args.emplace_back();
      continue;
    }
    int size = WideCharToMultiByte(CP_UTF8, 0, wide.data(), static_cast<int>(wide.size()), nullptr,
                                   0, nullptr, nullptr);
    std::string utf8(static_cast<size_t>(size), '\0');
    WideCharToMultiByte(CP_UTF8, 0, wide.data(), static_cast<int>(wide.size()), utf8.data(), size,
                        nullptr, nullptr);
    args.push_back(std::move(utf8));
  }
  return args;
}
#endif

}  // namespace

#if REX_PLATFORM_WIN32

int WINAPI wWinMain(HINSTANCE hinstance, HINSTANCE hinstance_prev, LPWSTR command_line,
                    int show_cmd) {
  (void)hinstance;
  (void)hinstance_prev;
  (void)command_line;
  (void)show_cmd;

  int wargc = 0;
  wchar_t** wargv = CommandLineToArgvW(GetCommandLineW(), &wargc);
  auto utf8_args = WideArgsToUtf8(wargc, wargv);
  LocalFree(wargv);

  std::vector<char*> argv_ptrs;
  argv_ptrs.reserve(utf8_args.size());
  for (auto& s : utf8_args) {
    argv_ptrs.push_back(s.data());
  }
  return RunWindowedApp(static_cast<int>(argv_ptrs.size()), argv_ptrs.data());
}

#else

int main(int argc, char* argv[]) {
  return RunWindowedApp(argc, argv);
}

#endif
