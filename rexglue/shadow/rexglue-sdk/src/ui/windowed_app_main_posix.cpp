/**
 ******************************************************************************
 * Xenia : Xbox 360 Emulator Research Project                                 *
 ******************************************************************************
 * Copyright 2021 Ben Vanik. All rights reserved.                             *
 * Released under the BSD license - see LICENSE in the root for more details. *
 ******************************************************************************
 *
 * @modified    Tom Clay, 2026 - Adapted for ReXGlue runtime
 */

#include <cstdio>
#include <cstdlib>
#include <atomic>
#include <cerrno>
#include <csignal>
#include <exception>
#include <fcntl.h>
#include <functional>
#include <map>
#include <memory>
#include <string>
#include <system_error>
#include <thread>
#include <unistd.h>
#include <vector>

#include <rex/cvar.h>
#include <rex/logging.h>
#include <rex/ui/windowed_app.h>
#include <rex/ui/windowed_app_context_sdl.h>

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

}  // namespace

int main(int argc_pre_gtk, char** argv_pre_gtk) {
  InstallExternalShutdownTerminateHandler();
  PosixTerminationSignalBridge termination_signal_bridge;

  auto remaining = rex::cvar::Init(argc_pre_gtk, argv_pre_gtk);
  rex::cvar::ApplyEnvironment();
  rex::InitLoggingEarly();

  int result = EXIT_FAILURE;
  {
    rex::ui::SDLWindowedAppContext app_context;
    if (!app_context.Initialize()) {
      std::fputs("Failed to initialize SDL3 video\n", stderr);
      rex::ShutdownLogging();
      return EXIT_FAILURE;
    }
    std::unique_ptr<rex::ui::WindowedApp> app = rex::ui::GetWindowedAppCreator()(app_context);
    ApplyPositionalArguments(*app, remaining);
    termination_signal_bridge.Start(app_context, MakeShutdownCallback(*app));

    if (app->OnInitialize()) {
      termination_signal_bridge.RefreshHandlers();
      app_context.RunMainSDLLoop();
      MarkExternalShutdownInProgress();
      result = EXIT_SUCCESS;
    }

    termination_signal_bridge.Stop();
    app->InvokeOnDestroy();
  }

  // Logging may still be needed in the destructors.
  rex::ShutdownLogging();

  return result;
}
