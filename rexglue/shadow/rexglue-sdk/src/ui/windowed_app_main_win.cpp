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

#include <cstdlib>
#include <functional>
#include <map>
#include <memory>
#include <mutex>
#include <string>
#include <vector>

#include <rex/cvar.h>
#include <rex/logging.h>
#include <rex/platform.h>
#include <rex/ui/windowed_app.h>
#include <rex/ui/windowed_app_context_sdl.h>

#include <Objbase.h>
#include <Shellapi.h>
#include <Windows.h>

namespace {

// Convert wide argv from CommandLineToArgvW to UTF-8 argc/argv for cvar::Init
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

}  // namespace

int WINAPI wWinMain(HINSTANCE hinstance, HINSTANCE hinstance_prev, LPWSTR command_line,
                    int show_cmd) {
  (void)hinstance_prev;
  (void)command_line;

  // Convert wide command line to UTF-8 argc/argv and parse CVARs
  int wargc = 0;
  wchar_t** wargv = CommandLineToArgvW(GetCommandLineW(), &wargc);
  auto utf8_args = WideArgsToUtf8(wargc, wargv);
  LocalFree(wargv);

  // Build char* argv for cvar::Init
  std::vector<char*> argv_ptrs;
  argv_ptrs.reserve(utf8_args.size());
  for (auto& s : utf8_args) {
    argv_ptrs.push_back(s.data());
  }
  auto remaining = rex::cvar::Init(static_cast<int>(argv_ptrs.size()), argv_ptrs.data());
  rex::cvar::ApplyEnvironment();
  rex::InitLoggingEarly();

  int result;

  {
    (void)hinstance;
    (void)show_cmd;
    rex::ui::SDLWindowedAppContext app_context;
    if (!app_context.Initialize()) {
      rex::ShutdownLogging();
      return EXIT_FAILURE;
    }
    std::unique_ptr<rex::ui::WindowedApp> app = rex::ui::GetWindowedAppCreator()(app_context);
    ApplyPositionalArguments(*app, remaining);
    ConsoleControlBridge console_control_bridge(app_context, MakeShutdownCallback(*app));

    // Initialize COM on the UI thread with the apartment-threaded concurrency
    // model, so dialogs can be used.
    if (FAILED(CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED))) {
      rex::ShutdownLogging();
      return EXIT_FAILURE;
    }

    result = app->OnInitialize() ? app_context.RunMainSDLLoop() : EXIT_FAILURE;

    app->InvokeOnDestroy();
  }

  // Logging may still be needed in the destructors.
  CoUninitialize();
  rex::ShutdownLogging();

  return result;
}
