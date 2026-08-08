/**
 * @file        ui/rex_app.cpp
 * @brief       ReXApp implementation — compiled as part of the consumer executable
 *
 * @copyright   Copyright (c) 2026 Tom Clay <tomc@tctechstuff.com>
 *              All rights reserved.
 *
 * @license     BSD 3-Clause License
 *              See LICENSE file in the project root for full license text.
 */

#include <rex/rex_app.h>

#include <rex/cvar.h>
#include <rex/ui/flags.h>
#include <rex/kernel/crt/heap.h>
#include <rex/filesystem.h>
#include <rex/logging/sink.h>
#include <rex/logging.h>
#include <rex/ui/overlay/console_overlay.h>
#include <rex/ui/overlay/debug_overlay.h>
#include <rex/ui/overlay/settings_overlay.h>
#include <rex/graphics/graphics_system.h>
#if REX_HAS_VULKAN
#include <rex/graphics/vulkan/graphics_system.h>
#endif
#if REX_HAS_D3D12
#include <rex/graphics/d3d12/graphics_system.h>
#endif
#include <rex/audio/audio_system.h>
#include <rex/audio/sdl/sdl_audio_system.h>
#include <rex/input/input_system.h>
#include <rex/kernel/init.h>
#include <rex/system.h>
#include <rex/system/kernel_state.h>
#include <rex/system/xthread.h>
#include <rex/ui/graphics_provider.h>
#include <rex/ui/keybinds.h>
#include <rex/version.h>

#include <fmt/format.h>
#include <imgui.h>
#include <toml++/toml.hpp>

#include <algorithm>
#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <exception>
#include <filesystem>
#include <optional>
#include <system_error>
#include <string_view>
#include <thread>

namespace rex {

namespace {

constexpr auto kExternalShutdownGracePeriod = std::chrono::seconds(5);
constexpr auto kExternalShutdownWatchdogPeriod = kExternalShutdownGracePeriod + std::chrono::seconds(1);
constexpr auto kExternalShutdownPollInterval = std::chrono::milliseconds(10);

std::optional<std::string> TomlNodeToCvarString(const toml::node& value) {
  if (const auto* bool_value = value.as_boolean()) {
    return bool_value->get() ? "true" : "false";
  }
  if (const auto* int_value = value.as_integer()) {
    return std::to_string(int_value->get());
  }
  if (const auto* float_value = value.as_floating_point()) {
    return std::to_string(float_value->get());
  }
  if (const auto* string_value = value.as_string()) {
    return string_value->get();
  }
  return std::nullopt;
}

void ApplyEarlyLogCvarValue(const toml::table& table, std::string_view toml_key,
                            std::string_view cvar_name) {
  const auto* value = table.get(toml_key);
  if (!value) {
    return;
  }

  auto cvar_value = TomlNodeToCvarString(*value);
  if (!cvar_value) {
    return;
  }

  rex::cvar::SetFlagByName(cvar_name, *cvar_value);
}

void ApplyEarlyLogCvars(const std::filesystem::path& config_path) {
  if (!std::filesystem::exists(config_path)) {
    return;
  }

  try {
    const auto config = toml::parse_file(config_path.string());

    ApplyEarlyLogCvarValue(config, "log_level", "log_level");
    ApplyEarlyLogCvarValue(config, "log_file", "log_file");
    ApplyEarlyLogCvarValue(config, "log_verbose", "log_verbose");
    ApplyEarlyLogCvarValue(config, "log_noisy", "log_noisy");
    ApplyEarlyLogCvarValue(config, "log_flush_interval", "log_flush_interval");
    ApplyEarlyLogCvarValue(config, "log_max_file_size_mb", "log_max_file_size_mb");
    ApplyEarlyLogCvarValue(config, "log_max_files", "log_max_files");

    if (const auto* log_table = config["log"].as_table()) {
      ApplyEarlyLogCvarValue(*log_table, "level", "log_level");
      ApplyEarlyLogCvarValue(*log_table, "file", "log_file");
      ApplyEarlyLogCvarValue(*log_table, "verbose", "log_verbose");
      ApplyEarlyLogCvarValue(*log_table, "noisy", "log_noisy");
      ApplyEarlyLogCvarValue(*log_table, "flush_interval", "log_flush_interval");
      ApplyEarlyLogCvarValue(*log_table, "max_file_size_mb", "log_max_file_size_mb");
      ApplyEarlyLogCvarValue(*log_table, "max_files", "log_max_files");
    }
  } catch (const toml::parse_error&) {
    // The normal config load below will report the parse failure once logging
    // has been initialized with the best available defaults.
  }
}

void ApplyEarlyPathCvars(const std::filesystem::path& config_path) {
  if (!std::filesystem::exists(config_path)) {
    return;
  }

  try {
    const auto config = toml::parse_file(config_path.string());

    ApplyEarlyLogCvarValue(config, "game_data_root", "game_data_root");
    ApplyEarlyLogCvarValue(config, "user_data_root", "user_data_root");
    ApplyEarlyLogCvarValue(config, "update_data_root", "update_data_root");
    ApplyEarlyLogCvarValue(config, "cache_path", "cache_path");

    if (const auto* runtime_table = config["runtime"].as_table()) {
      ApplyEarlyLogCvarValue(*runtime_table, "game_data_root", "game_data_root");
      ApplyEarlyLogCvarValue(*runtime_table, "user_data_root", "user_data_root");
      ApplyEarlyLogCvarValue(*runtime_table, "update_data_root", "update_data_root");
      ApplyEarlyLogCvarValue(*runtime_table, "cache_path", "cache_path");
    }
  } catch (const toml::parse_error&) {
    // The normal config load below will report the parse failure once logging
    // has been initialized with the best available defaults.
  }
}


// True when refii runs as a LIBRARY inside a host process (Godot) rather than owning the
// process it is in. The host bridge sets this alongside the headless flag.
//
// Every hard exit below is CORRECT for a standalone app: a straggler guest thread must
// not be allowed to wedge shutdown, so the app stops asking politely and calls
// std::_Exit. Hosted, that same call takes the HOST down with it — closing the game
// window killed the launcher, because ReXApp::OnDestroy exits the process when its
// shutdown threads miss the grace period, which they routinely do.
//
// Embedded, teardown therefore stops early and RETURNS instead. Guest threads may still
// be running afterwards; that is accepted, because the session is single-shot anyway
// (the runtime holds process-global state and cannot be started twice), and a live
// thread is a smaller problem than killing the application hosting it.
bool RexIsEmbeddedHost() {
  static const bool embedded = []() {
    const char* value = std::getenv("REX_EMBED_HEADLESS");
    return value != nullptr && value[0] == '1';
  }();
  return embedded;
}

}  // namespace

// --- ReXApp ---

ReXApp::~ReXApp() {
  if (!destroyed_.load(std::memory_order_acquire)) {
    OnDestroy();
  }
  if (title_termination_thread_.joinable() || module_thread_.joinable()) {
    if (!WaitForShutdownThreads()) {
      if (RexIsEmbeddedHost()) {
        // Detach rather than join: these threads missed the deadline, so joining them
        // blocks the host's main thread for as long as they keep running.
        std::fputs("Shutdown thread cleanup timed out; leaving threads detached\n", stderr);
        std::fflush(nullptr);
        if (title_termination_thread_.joinable()) {
          title_termination_thread_.detach();
        }
        if (module_thread_.joinable()) {
          module_thread_.detach();
        }
        return;
      }

      std::fputs("Shutdown thread cleanup timed out; exiting process\n", stderr);
      std::fflush(nullptr);
      std::_Exit(EXIT_SUCCESS);
    }
    if (title_termination_thread_.joinable()) {
      title_termination_thread_.join();
    }
    if (module_thread_.joinable()) {
      module_thread_.join();
    }
  }
}

ReXApp::ReXApp(ui::WindowedAppContext& ctx, std::string_view name, PPCImageInfo ppc_info,
               std::string_view usage)
    : WindowedApp(ctx, name, usage), ppc_info_(ppc_info) {}

bool ReXApp::OnInitialize() {
  if (!SetupEnvironment())
    return false;
  if (!SetupPresentation())
    return false;

  auto paths = OnFinalizePaths(resolved_defaults_, MakeResumeCallback());
  if (!paths) {
    // Async: consumer will invoke resume when ready. OnInitialize returns
    // true so the event loop keeps pumping (wizard dialogs render).
    return true;
  }

  if (!ConstructRuntime(*paths))
    return false;
  LaunchModule();
  return true;
}

bool ReXApp::SetupEnvironment() {
  auto exe_dir = rex::filesystem::GetExecutableFolder();
  auto default_config_path = exe_dir / (std::string(GetName()) + ".toml");

  ApplyEarlyPathCvars(default_config_path);

  std::filesystem::path game_dir;
  std::string game_data_cvar = REXCVAR_GET(game_data_root);
  if (!game_data_cvar.empty()) {
    game_dir = game_data_cvar;
  }

  // User data: cvar override, or platform user directory
  std::filesystem::path user_dir;
  std::string user_data_cvar = REXCVAR_GET(user_data_root);
  if (!user_data_cvar.empty()) {
    user_dir = user_data_cvar;
  } else {
    user_dir = rex::filesystem::GetUserFolder() / GetName();
  }

  // Update data: cvar override, or empty (opt-in)
  std::filesystem::path update_dir;
  std::string update_data_cvar = REXCVAR_GET(update_data_root);
  if (!update_data_cvar.empty()) {
    update_dir = update_data_cvar;
  }

  // Cache: cvar override, or user_dir/cache
  std::filesystem::path cache_dir;
  std::string cache_path_cvar = REXCVAR_GET(cache_path);
  if (!cache_path_cvar.empty()) {
    cache_dir = cache_path_cvar;
  } else {
    cache_dir = user_dir / "cache";
  }

  PathConfig path_config{game_dir, user_dir, update_dir, cache_dir, default_config_path};
  OnConfigurePaths(path_config);
  game_data_root_ = path_config.game_data_root;
  user_data_root_ = path_config.user_data_root;
  update_data_root_ = path_config.update_data_root;
  cache_root_ = path_config.cache_root;
  config_path_ = path_config.config_path;
  resolved_defaults_ = std::move(path_config);

  ApplyEarlyLogCvars(config_path_);

  // Late-phase logging
  std::string log_file_cvar = REXCVAR_GET(log_file);
  std::string log_level_str = REXCVAR_GET(log_level);
  if (REXCVAR_GET(log_verbose) && log_level_str == "info")
    log_level_str = "trace";

  auto category_levels = rex::ParseCategoryLevelsFromConfig(config_path_);
  auto log_config = rex::BuildLogConfig(log_file_cvar.empty() ? nullptr : log_file_cvar.c_str(),
                                        log_level_str, category_levels);
  if (log_file_cvar.empty()) {
    log_config.app_name = std::string(GetName());
    log_config.log_dir = (exe_dir / "logs").string();
  }

  rex::InitLogging(log_config);
  rex::RegisterLogLevelCallback();

  log_sink_ = std::make_shared<rex::LogCaptureSink>();
  rex::AddSink(log_sink_);

  if (std::filesystem::exists(config_path_)) {
    rex::cvar::LoadConfig(config_path_);
    rex::cvar::ApplyEnvironment();
  }

  OnPostInitLogging();

  REXLOG_INFO("{} starting", GetName());
  if (!game_data_root_.empty()) {
    REXLOG_INFO("  Game directory: {}", game_data_root_.string());
  }
  if (!user_data_root_.empty()) {
    REXLOG_INFO("  User data:      {}", user_data_root_.string());
  }
  if (!update_data_root_.empty()) {
    REXLOG_INFO("  Update data:    {}", update_data_root_.string());
  }
  REXLOG_INFO("  Cache root:     {}", cache_root_.string());

  return true;
}

bool ReXApp::ConstructRuntime(const PathConfig& paths) {
  game_data_root_ = paths.game_data_root;
  user_data_root_ = paths.user_data_root;
  update_data_root_ = paths.update_data_root;
  cache_root_ = paths.cache_root;

  if (paths.game_data_root.empty()) {
    auto msg = std::string("--game_data_root was not provided.");
    REXLOG_ERROR("{}", msg);
    rex::ShowSimpleMessageBox(rex::SimpleMessageBoxType::Error, msg);
    return false;
  }
  if (!std::filesystem::is_directory(paths.game_data_root)) {
    auto msg = fmt::format("--game_data_root does not exist: {}", paths.game_data_root.string());
    REXLOG_ERROR("{}", msg);
    rex::ShowSimpleMessageBox(rex::SimpleMessageBoxType::Error, msg);
    return false;
  }

  runtime_ = std::make_unique<rex::Runtime>(paths.game_data_root, paths.user_data_root,
                                            paths.update_data_root, paths.cache_root);
  runtime_->set_app_context(&app_context());

  // Window and ImGui drawer already exist from SetupPresentation; publish them
  // to the runtime before Setup so hooks and native rendering see them.
  if (window_) {
    runtime_->set_display_window(window_.get());
  }
  if (imgui_drawer_) {
    runtime_->set_imgui_drawer(imgui_drawer_.get());
  }

  auto status = runtime_->Setup(ppc_info_, std::move(config_));
  if (XFAILED(status)) {
    REXLOG_ERROR("Runtime setup failed: {:08X}", status);
    return false;
  }

  if (window_ && runtime_->input_system()) {
    static_cast<rex::input::InputSystem*>(runtime_->input_system())->AttachWindow(window_.get());
  }

  if (ppc_info_.register_modules) {
    ppc_info_.register_modules(runtime_->kernel_state());
  }

  if (imgui_drawer_) {
    auto* input_sys = static_cast<rex::input::InputSystem*>(runtime_->input_system());
    if (input_sys) {
      input_sys->SetActiveCallback([this]() {
        if (!debug_overlay_ && !console_overlay_ && !settings_overlay_)
          return true;
        return !imgui_drawer_->GetIO().WantCaptureMouse;
      });
    }
  }

  std::string xex_image = "game:\\default.xex";
  OnLoadXexImage(xex_image);

  // Mirrors the game:\ / d:\ -> game_data_root mapping in Runtime::SetupVfs.
  {
    constexpr std::string_view kGameDevice = "game:\\";
    constexpr std::string_view kDDevice = "d:\\";
    std::string_view tail = xex_image;
    if (tail.starts_with(kGameDevice)) {
      tail.remove_prefix(kGameDevice.size());
    } else if (tail.starts_with(kDDevice)) {
      tail.remove_prefix(kDDevice.size());
    }
    std::string host_tail{tail};
    std::replace(host_tail.begin(), host_tail.end(), '\\', '/');
    auto xex_host = paths.game_data_root / host_tail;
    if (!std::filesystem::is_regular_file(xex_host)) {
      auto msg = fmt::format("Entrypoint XEX not found: {}", xex_host.string());
      REXLOG_ERROR("{}", msg);
      rex::ShowSimpleMessageBox(rex::SimpleMessageBoxType::Error, msg);
      return false;
    }
  }

  status = runtime_->LoadXexImage(xex_image);
  if (XFAILED(status)) {
    auto msg = fmt::format("Failed to load XEX ({}): {:08X}", xex_image, status);
    REXLOG_ERROR("{}", msg);
    rex::ShowSimpleMessageBox(rex::SimpleMessageBoxType::Error, msg);
    return false;
  }

  OnPostLoadXexImage();

  if (ppc_info_.rexcrt_heap) {
    if (!rex::kernel::crt::InitHeap(REXCVAR_GET(rexcrt_heap_size_mb), runtime_->memory())) {
      REXLOG_ERROR("Failed to initialize rexcrt heap");
      return false;
    }
  }

  OnPostSetup();

  return true;
}

bool ReXApp::SetupPresentation() {
#if REX_HAS_D3D12
  config_.graphics = REX_GRAPHICS_BACKEND(rex::graphics::d3d12::D3D12GraphicsSystem);
#elif REX_HAS_VULKAN
  config_.graphics = REX_GRAPHICS_BACKEND(rex::graphics::vulkan::VulkanGraphicsSystem);
#endif
  config_.audio_factory = REX_AUDIO_BACKEND(rex::audio::sdl::SDLAudioSystem);
  config_.input_factory = REX_INPUT_BACKEND(rex::input::CreateDefaultInputSystem);
  config_.kernel_init = rex::kernel::InitializeKernel;

  OnPreSetup(config_);

  // Embedded: do NOT set up presentation. SetupPresentation builds a swapchain-capable
  // provider and expects a window to present into; with no window it either fails or
  // presents nowhere. The guest GPU still gets built lazily on the headless path
  // (CreateProvider(with_presentation=false)), which is what keeps the guest's own
  // device state readable — the "conformant null device" rather than "no device".
  if (config_.graphics && !headless_) {
    X_STATUS status = config_.graphics->SetupPresentation(&app_context());
    if (XFAILED(status)) {
      REXLOG_ERROR("Graphics presentation setup failed: {:08X}", status);
      return false;
    }
  }

  // EMBEDDED: the host owns the window, so the guest creates none.
  //
  // Set REX_EMBED_HEADLESS=1 when the guest runs inside a host that presents for it —
  // Godot, in this tree. A window of its own is not a lesser version of the right
  // thing; it is a second empty window competing for focus and input with the host's.
  //
  // Window::Create is a LINK-time choice (window_sdl.cpp / window_win.cpp), not a
  // runtime factory, so a host cannot supply its own Window subclass. Not creating one
  // is the seam that actually exists.
  //
  // NOT named REX_HEADLESS: that maps to the existing `headless` KERNEL cvar, which
  // suppresses XAM UI dialogs and has nothing to do with window creation. Using it here
  // silently changed guest dialog behaviour as a side effect.
  //
  // An env var rather than a cvar because the config file is loaded AFTER the command
  // line and would override it — which is why --renderer_backend=plume did not take.
  const char* embedded = std::getenv("REX_EMBED_HEADLESS");
  headless_ = embedded != nullptr && embedded[0] == '1';

  if (headless_) {
    REXLOG_INFO(
        "REX_HEADLESS=1: no window, no ImGui drawer. The host owns presentation; "
        "the guest simulates only.");
  } else {
    // Create window
    window_ = rex::ui::Window::Create(app_context(), GetName(), 1280, 720);
    if (!window_) {
      REXLOG_ERROR("Failed to create window");
      return false;
    }

    // Set window title with SDK build stamp
    std::string title = std::string(GetName()) + " " + REXGLUE_BUILD_TITLE;
    window_->SetTitle(title);

    window_->AddListener(this);
    window_->AddInputListener(this, 0);

    if (REXCVAR_GET(fullscreen)) {
      window_->SetFullscreen(true);
    }
    window_->Open();
  }

  auto create_imgui_drawer = [this]() {
    if (imgui_drawer_) {
      return;
    }

    imgui_drawer_ = std::make_unique<rex::ui::ImGuiDrawer>(
        window_.get(), 64, [this](ImFontAtlas* atlas) { OnConfigureFonts(atlas); });
    rex::ui::RegisterBind("bind_debug_overlay", "F3", "Toggle debug overlay", [this] {
      if (debug_overlay_) {
        debug_overlay_.reset();
      } else {
        debug_overlay_ =
            std::make_unique<ui::DebugOverlayDialog>(imgui_drawer_.get(),
                                                     frame_stats_provider_);
      }
    });
    rex::ui::RegisterBind("bind_console", "Backtick", "Toggle console overlay", [this] {
      if (console_overlay_) {
        console_overlay_.reset();
      } else {
        console_overlay_ = std::make_unique<ui::ConsoleDialog>(imgui_drawer_.get(), log_sink_);
      }
    });
    rex::ui::RegisterBind("bind_settings", "F4", "Toggle settings overlay", [this] {
      if (settings_overlay_) {
        settings_overlay_.reset();
      } else {
        settings_overlay_ =
            std::make_unique<ui::SettingsDialog>(imgui_drawer_.get(), config_path_);
      }
    });

    OnCreateDialogs(imgui_drawer_.get());
  };

  // The ImGuiDrawer binds to window_ and registers an input listener on it, so with no
  // window it dereferences null inside AddInputListener. Both creation sites have to be
  // guarded, not just the one below: this one fires whenever a presenter exists, which
  // is independent of whether an overlay was asked for.
  auto* graphics_system =
      headless_ ? nullptr : static_cast<rex::graphics::GraphicsSystem*>(config_.graphics.get());
  if (graphics_system && graphics_system->presenter()) {
    auto* presenter = graphics_system->presenter();
    auto* provider = graphics_system->provider();
    if (provider) {
      immediate_drawer_ = provider->CreateImmediateDrawer();
      if (immediate_drawer_) {
        immediate_drawer_->SetPresenter(presenter);
        create_imgui_drawer();
        imgui_drawer_->SetPresenterAndImmediateDrawer(presenter, immediate_drawer_.get());
      }
    }
    if (window_) {
      window_->SetPresenter(presenter);
    }
  }
  // The drawer binds to the window, so headless has nothing to bind to. It is also
  // exactly the overlay the host should be drawing itself.
  if (!imgui_drawer_ && !headless_ && OnNeedsImGuiDrawerWithoutPresenter()) {
    create_imgui_drawer();
  }

  return true;
}

void ReXApp::LaunchModule() {
  app_context().CallInUIThreadDeferred([this]() {
    if (shutting_down_.load(std::memory_order_acquire)) {
      app_context().QuitFromUIThread();
      return;
    }

    OnPreLaunchModule();

    auto main_thread = runtime_->PrepareModuleLaunch();
    if (!main_thread) {
      REXLOG_ERROR("Failed to launch module");
      app_context().QuitFromUIThread();
      return;
    }

    auto* graphics_system =
        static_cast<rex::graphics::GraphicsSystem*>(runtime_->graphics_system());
    if (graphics_system && !runtime_->cache_root().empty()) {
      uint32_t title_id = runtime_->kernel_state()->title_id();
      if (title_id != 0) {
        REXLOG_INFO("Initializing shader storage for title {:08X}...", title_id);
        graphics_system->InitializeShaderStorage(runtime_->cache_root(), title_id, true);
      }
    }

    OnPostLaunchModule(main_thread.get());
    main_thread->Resume();

    module_thread_finished_.store(false, std::memory_order_release);
    module_thread_ = std::thread([this, main_thread = std::move(main_thread)]() mutable {
      try {
        main_thread->Wait(0, 0, 0, nullptr);
        OnGuestThreadExit(main_thread.get());
        REXLOG_INFO("Execution complete");
      } catch (const std::system_error& e) {
        std::fprintf(stderr, "Guest module thread shutdown failed: %s\n", e.what());
      } catch (const std::exception& e) {
        std::fprintf(stderr, "Guest module thread shutdown failed: %s\n", e.what());
      } catch (...) {
        std::fputs("Guest module thread shutdown failed with an unknown exception\n", stderr);
      }
      module_thread_finished_.store(true, std::memory_order_release);
      if (!shutting_down_.load(std::memory_order_acquire)) {
        app_context().CallInUIThread([this]() { app_context().QuitFromUIThread(); });
      }
    });
  });
}

std::function<void(PathConfig)> ReXApp::MakeResumeCallback() {
  return [this](PathConfig paths) {
    if (shutting_down_.load(std::memory_order_acquire))
      return;
    if (!ConstructRuntime(std::move(paths))) {
      app_context().QuitFromUIThread();
      return;
    }
    LaunchModule();
  };
}

void ReXApp::OnKeyDown(ui::KeyEvent& e) {
  rex::ui::ProcessKeyEvent(e);
}

void ReXApp::OnClosing(ui::UIEvent& e) {
  (void)e;
  REXLOG_INFO("Window closing, shutting down...");
  RequestShutdown();
}

void ReXApp::RequestShutdown() {
  shutting_down_.store(true, std::memory_order_release);
  StartExternalShutdownWatchdog();
  StartTitleTermination();
  if (!shutdown_hook_invoked_.exchange(true, std::memory_order_acq_rel)) {
    OnShutdown();
  }
  app_context().QuitFromUIThread();
}

namespace {
std::atomic_bool g_app_teardown_started{false};
}  // namespace

bool ui::AppTeardownStarted() {
  return g_app_teardown_started.load(std::memory_order_acquire);
}

void ReXApp::RequestExternalShutdown() {
  std::fputs("ReXApp external shutdown requested; terminating title\n", stderr);
  g_app_teardown_started.store(true, std::memory_order_release);
  external_shutdown_requested_.store(true, std::memory_order_release);
  shutting_down_.store(true, std::memory_order_release);
  StartExternalShutdownWatchdog();
  StartTitleTermination();
  // This is called from the POSIX signal waiter / console-control thread, not
  // the UI thread. Renderer and window teardown may synchronously marshal back
  // to UI, so leave full cleanup to OnDestroy if the UI loop gets to run before
  // the external signal bridge exits the process.
  app_context().RequestDeferredQuit();
}

void ReXApp::StartTitleTermination() {
  if (title_termination_started_.exchange(true, std::memory_order_acq_rel)) {
    return;
  }

  if (!runtime_ || !runtime_->kernel_state()) {
    title_termination_finished_.store(true, std::memory_order_release);
    return;
  }

  title_termination_finished_.store(false, std::memory_order_release);
  title_termination_thread_ = std::thread([this]() {
    try {
      runtime_->kernel_state()->TerminateTitle();
    } catch (const std::system_error& e) {
      std::fprintf(stderr, "Title termination failed: %s\n", e.what());
    } catch (const std::exception& e) {
      std::fprintf(stderr, "Title termination failed: %s\n", e.what());
    } catch (...) {
      std::fputs("Title termination failed with an unknown exception\n", stderr);
    }
    title_termination_finished_.store(true, std::memory_order_release);
  });
}

void ReXApp::StartExternalShutdownWatchdog() {
  if (external_shutdown_watchdog_started_.exchange(true, std::memory_order_acq_rel)) {
    return;
  }

  // The watchdog exists only to kill the process after a grace period. Hosted, there is
  // nothing it could do that the host would survive.
  if (RexIsEmbeddedHost()) {
    return;
  }

  std::thread([]() {
    std::this_thread::sleep_for(kExternalShutdownWatchdogPeriod);
    std::fputs("External shutdown grace period expired; exiting process\n", stderr);
    std::fflush(nullptr);
    std::_Exit(EXIT_SUCCESS);
  }).detach();
}

bool ReXApp::WaitForShutdownThreads() {
  const auto deadline = std::chrono::steady_clock::now() + kExternalShutdownGracePeriod;
  while (std::chrono::steady_clock::now() < deadline) {
    const bool title_done =
        !title_termination_thread_.joinable() ||
        title_termination_finished_.load(std::memory_order_acquire);
    const bool module_done =
        !module_thread_.joinable() || module_thread_finished_.load(std::memory_order_acquire);
    if (title_done && module_done) {
      return true;
    }
    std::this_thread::sleep_for(kExternalShutdownPollInterval);
  }

  return (!title_termination_thread_.joinable() ||
          title_termination_finished_.load(std::memory_order_acquire)) &&
         (!module_thread_.joinable() || module_thread_finished_.load(std::memory_order_acquire));
}

void ReXApp::OnDestroy() {
  if (destroyed_.exchange(true, std::memory_order_acq_rel)) {
    return;
  }

  g_app_teardown_started.store(true, std::memory_order_release);
  shutting_down_.store(true, std::memory_order_release);
  StartTitleTermination();

  // Notify subclass before cleanup
  if (!shutdown_hook_invoked_.exchange(true, std::memory_order_acq_rel)) {
    OnShutdown();
  }

  if (!WaitForShutdownThreads()) {
    std::fputs("Graceful shutdown timed out; skipping further teardown\n", stderr);
    std::fflush(nullptr);
    if (!RexIsEmbeddedHost()) {
      std::_Exit(EXIT_SUCCESS);
    }

    return;
  }

  if (external_shutdown_requested_.load(std::memory_order_acquire)) {
    REXLOG_INFO(
        "External shutdown completed title termination; skipping extended UI "
        "teardown");
    std::fflush(nullptr);
    if (!RexIsEmbeddedHost()) {
      std::_Exit(EXIT_SUCCESS);
    }

    return;
  }

  // Unregister overlay keybinds before destroying dialogs
  rex::ui::UnregisterBind("bind_debug_overlay");
  rex::ui::UnregisterBind("bind_console");
  rex::ui::UnregisterBind("bind_settings");

  // ImGui cleanup (reverse of setup)
  settings_overlay_.reset();
  console_overlay_.reset();
  debug_overlay_.reset();
  if (imgui_drawer_) {
    imgui_drawer_->SetPresenterAndImmediateDrawer(nullptr, nullptr);
    imgui_drawer_.reset();
  }
  if (immediate_drawer_) {
    immediate_drawer_->SetPresenter(nullptr);
    immediate_drawer_.reset();
  }
  if (runtime_) {
    runtime_->set_display_window(nullptr);
    runtime_->set_imgui_drawer(nullptr);
  }
  // Window/runtime cleanup
  if (window_) {
    window_->SetPresenter(nullptr);
  }
  if (title_termination_thread_.joinable()) {
    title_termination_thread_.join();
  }
  if (module_thread_.joinable()) {
    module_thread_.join();
  }
  if (window_) {
    window_->RemoveInputListener(this);
    window_->RemoveListener(this);
  }
  window_.reset();
  runtime_.reset();
}

void ReXApp::SetGuestFrameStats(ui::DebugOverlayDialog::FrameStatsProvider provider) {
  frame_stats_provider_ = provider;
  if (debug_overlay_) {
    debug_overlay_->SetStatsProvider(provider);
  }
}

}  // namespace rex
