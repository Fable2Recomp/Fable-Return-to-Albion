/**
 * @file        ui/rex_app.cpp
 * @brief       ReXApp implementation - compiled as part of the consumer executable
 *
 * @copyright   Copyright (c) 2026 Tom Clay <tomc@tctechstuff.com>
 *              All rights reserved.
 *
 * @license     BSD 3-Clause License
 *              See LICENSE file in the project root for full license text.
 *
 * @modified    Ryan Fisher, 2026 - RexNet netplay integration
 */

#include <rex/rex_app.h>

#include <cstdlib>

#include <rex/assert.h>
#include <rex/cvar.h>
#include <rex/ui/flags.h>
#include <rex/kernel/crt/heap.h>
#include <rex/filesystem.h>
#include <rex/logging/sink.h>
#include <rex/logging.h>
#include <rex/ui/overlay/achievement_toast.h>
#include <rex/ui/overlay/achievements_overlay.h>
#include <rex/ui/overlay/console_overlay.h>
#include <rex/ui/overlay/debug_overlay.h>
#if REXGLUE_ENABLE_REXNET
#include <rex/ui/overlay/rexnet_overlay.h>
#endif
#include <rex/ui/overlay/settings_overlay.h>
#include <rex/audio/audio_system.h>
#include <rex/audio/sdl/sdl_audio_system.h>
#include <rex/input/input_system.h>
#include <rex/kernel/init.h>
#include <rex/string/numeric.h>
#include <rex/system.h>
#include <rex/system/achievement_manager.h>
#include <rex/system/gpu_plugin.h>
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

REXCVAR_DEFINE_STRING(gpu_plugin, "", "GPU",
                      "GPU emulation plugin to load at startup (e.g. 'xenos'); empty disables "
                      "GPU emulation")
    .lifecycle(rex::cvar::Lifecycle::kInitOnly);

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

  // AS CONFIG, NOT AS RUNTIME. A config file's value never outranks the command line: a host
  // that embeds the app states its paths on argv, and a config file beside the HOST's binary
  // (an exported app keeps refii.toml at its root, beside the executable) must not override them.
  rex::cvar::SetFlagFromConfig(cvar_name, *cvar_value);
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

// A fatal start-up error, reported. Standalone it is a message box; EMBEDDED it is the log line
// alone, and the failure returns to the host, which shows it. A modal raised from inside the host
// process blocks the host's own main thread (the call is synchronous on Windows) and leaves the
// player looking at a frozen window instead of the reason.
void ReportFatal(const std::string& msg) {
  REXLOG_ERROR("{}", msg);
  if (!RexIsEmbeddedHost()) {
    rex::ShowSimpleMessageBox(rex::SimpleMessageBoxType::Error, msg);
  }
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

std::unique_ptr<ui::ImGuiDialog> ReXApp::CreateAchievementsOverlay() {
  // Refii ships its own achievement runtime (src/achievement); the SDK
  // achievement UI stays disabled so the two systems never double-present.
  return nullptr;
}

std::unique_ptr<ui::AchievementNotificationDialog> ReXApp::CreateAchievementNotificationDialog() {
  // Disabled: refii's achievement runtime renders its own unlock toasts.
  return nullptr;
}

system::AchievementManager& ReXApp::achievements() const {
  assert_not_null(runtime_);
  assert_not_null(runtime_->kernel_state());
  return runtime_->kernel_state()->achievements();
}

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
  std::string cache_root_cvar = REXCVAR_GET(cache_root);
  if (!cache_root_cvar.empty()) {
    cache_dir = cache_root_cvar;
  } else {
    cache_dir = user_dir / "cache";
  }

std::filesystem::path metadata_dir;
  std::string metadata_root_cvar = REXCVAR_GET(metadata_root);
  if (!metadata_root_cvar.empty()) {
    metadata_dir = metadata_root_cvar;
  }

  PathConfig path_config{game_dir,  user_dir,     update_dir,
                         cache_dir, metadata_dir, default_config_path};
  OnConfigurePaths(path_config);
  game_data_root_ = path_config.game_data_root;
  user_data_root_ = path_config.user_data_root;
  update_data_root_ = path_config.update_data_root;
  cache_root_ = path_config.cache_root;
  metadata_root_ = path_config.metadata_root;
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
    // The title's own prefix when the host states one (refii / refiii), else the app's name:
    // both titles run from one app and one logs folder.
    const std::string log_prefix = REXCVAR_GET(log_prefix);
    log_config.app_name = log_prefix.empty() ? std::string(GetName()) : log_prefix;
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
  if (std::filesystem::exists(config_path_))
    REXLOG_DEBUG("Loaded config: {}", config_path_.filename().string());

  REXLOG_DEBUG("{} starting", GetName());
  if (!game_data_root_.empty()) {
    REXLOG_DEBUG("  Game directory: {}", game_data_root_.string());
  }
  if (!user_data_root_.empty()) {
    REXLOG_DEBUG("  User data:      {}", user_data_root_.string());
  }
  if (!update_data_root_.empty()) {
    REXLOG_DEBUG("  Update data:    {}", update_data_root_.string());
  }
  REXLOG_DEBUG("  Cache root:     {}", cache_root_.string());
  if (!metadata_root_.empty()) {
    REXLOG_DEBUG("  Metadata root:  {}", metadata_root_.string());
  }

  return true;
}

bool ReXApp::ConstructRuntime(const PathConfig& paths) {
  game_data_root_ = paths.game_data_root;
  user_data_root_ = paths.user_data_root;
  update_data_root_ = paths.update_data_root;
  cache_root_ = paths.cache_root;

  if (paths.game_data_root.empty()) {
    auto msg = std::string("--game_data_root was not provided.");
    ReportFatal(msg);
    return false;
  }
  if (!std::filesystem::is_directory(paths.game_data_root)) {
    auto msg = fmt::format("--game_data_root does not exist: {}", paths.game_data_root.string());
    ReportFatal(msg);
    return false;
  }

  game_data_root_ = paths.game_data_root;
  user_data_root_ = paths.user_data_root;
  update_data_root_ = paths.update_data_root;
  cache_root_ = paths.cache_root;
  metadata_root_ = paths.metadata_root;

  runtime_ =
      std::make_unique<rex::Runtime>(paths.game_data_root, paths.user_data_root,
                                     paths.update_data_root, paths.cache_root, paths.metadata_root);
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
        if (!debug_overlay_ && !console_overlay_ && !settings_overlay_ && !achievements_overlay_)
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
      ReportFatal(msg);
      return false;
    }
  }

  status = runtime_->LoadXexImage(xex_image);
  if (XFAILED(status)) {
    auto msg = fmt::format("Failed to load XEX ({}): {:08X}", xex_image, status);
    ReportFatal(msg);
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
  config_.gpu_plugin = REXCVAR_GET(gpu_plugin);
  config_.audio_factory = REX_AUDIO_BACKEND(rex::audio::sdl::SDLAudioSystem);
  config_.input_factory = REX_INPUT_BACKEND(rex::input::CreateDefaultInputSystem);
  config_.kernel_init = rex::kernel::InitializeKernel;

  OnPreSetup(config_);

  if (!config_.graphics && !config_.gpu_plugin.empty()) {
    config_.graphics = rex::system::LoadGpuPlugin(config_.gpu_plugin);
    if (!config_.graphics) {
      // Fatal by design: no silent headless fallback.
      auto msg =
          fmt::format("Failed to load GPU plugin '{}'. See log for details.", config_.gpu_plugin);
      ReportFatal(msg);
      return false;
    }
  }

  // EMBEDDED: the host owns the window, so the guest creates none.
  //
  // REX_EMBED_HEADLESS=1 when the guest runs inside a host that presents for it —
  // Godot, in this tree. A window of its own is not a lesser version of the right
  // thing; it is a second empty window competing for focus and input with the host's.
  //
  // Window::Create is a LINK-time choice (window_sdl.cpp / window_win.cpp), not a
  // runtime factory, so a host cannot supply its own Window subclass. Not creating one
  // is the seam that actually exists.
  //
  // NOT named REX_HEADLESS: that maps to the existing `headless` KERNEL cvar, which
  // suppresses XAM UI dialogs and has nothing to do with windowing.
  //
  // An env var rather than a cvar because the config file loads AFTER the command line
  // and would override it.
  const char* embedded = std::getenv("REX_EMBED_HEADLESS");
  headless_ = embedded != nullptr && embedded[0] == '1';

  // Do NOT set up presentation when embedded: it builds a swapchain-capable provider
  // and expects a window to present into. The guest GPU is still built lazily on the
  // headless path, which keeps the guest's own device state readable.
  if (config_.graphics && !headless_) {
    X_STATUS status = config_.graphics->SetupPresentation(&app_context());
    if (XFAILED(status)) {
      REXLOG_ERROR("Graphics presentation setup failed: {:08X}", status);
      return false;
    }
  }

  if (headless_) {
    REXLOG_INFO(
        "REX_EMBED_HEADLESS=1: no window, no overlays, no presentation. The host owns "
        "presentation; the guest simulates only.");
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

  // Overlays bind to window_ and register an input listener on it, so with no window
  // they dereference null inside Window::AddInputListener. Every attach path below has
  // to be skipped, not just the explicit-overlay one at the end.
  auto* graphics_system = headless_ ? nullptr : config_.graphics.get();
  // Shadow diagnostic (refii, 2026-07-19): black-window triage — record the
  // presentation attach decision.
  REXLOG_INFO("ReXApp presentation attach: graphics={} presenter={} provider={}",
              static_cast<void*>(graphics_system),
              static_cast<void*>(graphics_system ? graphics_system->presenter()
                                                 : nullptr),
              static_cast<void*>(graphics_system ? graphics_system->provider()
                                                 : nullptr));
  if (graphics_system && graphics_system->presenter()) {
    // SDK mode: the emulated-Xenos presenter drives the overlays.
    auto* presenter = graphics_system->presenter();
    auto* provider = graphics_system->provider();
    if (provider) {
      immediate_drawer_ = provider->CreateImmediateDrawer();
      if (immediate_drawer_) {
        immediate_drawer_->SetPresenter(presenter);
SetupOverlays(presenter, immediate_drawer_.get());
      }
    }
    window_->SetPresenter(presenter);
  } else if (!graphics_system && !headless_) {
    // Detached mode: the app brings its own renderer and drives its own paint
    // loop. ReXApp owns the returned drawer via immediate_drawer_.
    immediate_drawer_ = OnCreateImmediateDrawer();
    if (immediate_drawer_) {
      SetupOverlays(/*presenter=*/nullptr, immediate_drawer_.get());
      // No window_->SetPresenter, no drawer SetPresenter: the app owns the
      // surface and the present cadence.
    }
  }
  if (!imgui_drawer_ && !headless_ && OnNeedsImGuiDrawerWithoutPresenter()) {
    // Refii runs with a presenter-less graphics system (Plume owns present);
    // still build the ImGui drawer + binds. Null presenter/drawer are safe.
    SetupOverlays(/*presenter=*/nullptr, /*drawer=*/nullptr);
  }

  return true;
}

void ReXApp::SetupOverlays(rex::ui::Presenter* presenter, rex::ui::ImmediateDrawer* drawer) {
  imgui_drawer_ = std::make_unique<rex::ui::ImGuiDrawer>(
      window_.get(), 64, [this](ImFontAtlas* atlas) { OnConfigureFonts(atlas); },
      [this](ImGuiStyle& imgui_style, rex::ui::Style& ui_style) {
        OnConfigureStyle(imgui_style, ui_style);
      });
  // presenter is nullptr in detached mode; ImGuiDrawer tolerates that and the
  // gated eager font upload in SetImmediateDrawer is skipped (font uploads
  // lazily on the first Draw instead).
  imgui_drawer_->SetPresenterAndImmediateDrawer(presenter, drawer);
  rex::ui::RegisterBind("bind_debug_overlay", "F3", "Toggle debug overlay", [this] {
    if (debug_overlay_) {
      debug_overlay_.reset();
    } else {
      debug_overlay_ =
          std::make_unique<ui::DebugOverlayDialog>(imgui_drawer_.get(), frame_stats_provider_);
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
      settings_overlay_ = std::make_unique<ui::SettingsDialog>(imgui_drawer_.get(), config_path_);
    }
  });
  rex::ui::RegisterBind("bind_achievements", "F7", "Toggle achievements overlay", [this] {
    if (achievements_overlay_) {
      achievements_overlay_.reset();
    } else {
      achievements_overlay_ = CreateAchievementsOverlay();
    }
  });
#if REXGLUE_ENABLE_REXNET
  // Only bound when the module is compiled in: a keybind that opens nothing
  // reads as a broken feature rather than an absent one.
  rex::ui::RegisterBind("bind_rexnet", "F6", "Toggle RexNet overlay", [this] {
    if (rexnet_overlay_) {
      rexnet_overlay_.reset();
    } else {
      rexnet_overlay_ = std::make_unique<ui::RexNetOverlayDialog>(imgui_drawer_.get());
    }
  });
#endif

  OnCreateDialogs(imgui_drawer_.get());
}

void ReXApp::LaunchModule() {
  app_context().CallInUIThreadDeferred([this]() {
if (shutting_down_.load(std::memory_order_acquire)) {
      app_context().QuitFromUIThread();
      return;
    }

    // Register the achievement notification callback now that the runtime and
    // KernelState are guaranteed to exist. Done here (not OnCreateDialogs)
    // because KernelState is null during SetupPresentation.
    if (!achievement_notification_) {
      achievement_notification_ =
          std::shared_ptr<ui::AchievementNotificationDialog>(CreateAchievementNotificationDialog());
    }
    if (achievement_notification_ && achievement_notification_listener_ == 0 && runtime_ &&
        runtime_->kernel_state()) {
      std::weak_ptr<ui::AchievementNotificationDialog> notification = achievement_notification_;
      achievement_notification_listener_ = achievements().RegisterNotificationCallback(
          [notification](const rex::system::AchievementEvent& event) {
            if (auto dialog = notification.lock()) {
              dialog->Push(event);
            }
          });
    }

    OnPreLaunchModule();

    auto main_thread = runtime_->PrepareModuleLaunch();
    if (!main_thread) {
      REXLOG_ERROR("Failed to launch module");
      app_context().QuitFromUIThread();
      return;
    }

    auto* graphics_system = runtime_->graphics_system();
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
  // Hard-exit rather than run subsystem teardown, which can deadlock on a host
  // lock still held by a straggler TerminateTitle left running. Flush (not
  // ShutdownLogging, which frees loggers a straggler may still use); the OS
  // reclaims the rest.
  REXLOG_INFO("Title terminated; hard-exiting process.");
  rex::FlushLogging();
  if (!RexIsEmbeddedHost()) {
    std::_Exit(0);
  }
}

bool ReXApp::OnCloseRequested(ui::UIEvent& e) {
  (void)e;
  return OnWindowCloseRequested();
}

void ReXApp::OnResize(ui::UISetupEvent& e) {
  (void)e;
  if (!window_) {
    return;
  }
  OnWindowPixelSizeChanged(window_->GetActualPhysicalWidth(), window_->GetActualPhysicalHeight());
  OnWindowResized(window_->GetActualLogicalWidth(), window_->GetActualLogicalHeight());
}

void ReXApp::OnDpiChanged(ui::UISetupEvent& e) {
  (void)e;
  if (!window_) {
    return;
  }
  OnDpiScaleChanged(float(window_->GetDpi()) / float(window_->GetMediumDpi()));
}

void ReXApp::OnGotFocus(ui::UISetupEvent& e) {
  (void)e;
  OnWindowFocusChanged(true);
}

void ReXApp::OnLostFocus(ui::UISetupEvent& e) {
  (void)e;
  OnWindowFocusChanged(false);
}

void ReXApp::OnMinimized(ui::UIEvent& e) {
  (void)e;
  OnWindowMinimized();
}

void ReXApp::OnRestored(ui::UIEvent& e) {
  (void)e;
  OnWindowRestored();
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

bool ReXApp::GuestStragglersRunning() const noexcept {
  auto* kernel = runtime_ ? runtime_->kernel_state() : nullptr;
  return kernel != nullptr && kernel->HasStragglerThreads();
}

std::string ReXApp::GuestStragglerNames() const {
  auto* kernel = runtime_ ? runtime_->kernel_state() : nullptr;
  return kernel != nullptr ? kernel->StragglerThreadNames() : std::string();
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
  rex::ui::UnregisterBind("bind_achievements");

  // ImGui cleanup (reverse of setup)
  if (achievement_notification_listener_ != 0) {
    if (runtime_ && runtime_->kernel_state()) {
      achievements().UnregisterCallback(achievement_notification_listener_);
    }
    achievement_notification_listener_ = 0;
  }
  achievement_notification_.reset();
  achievements_overlay_.reset();
  settings_overlay_.reset();
  console_overlay_.reset();
  debug_overlay_.reset();
  if (imgui_drawer_) {
    imgui_drawer_->SetPresenterAndImmediateDrawer(nullptr, nullptr);
    imgui_drawer_.reset();
  }
  // immediate_drawer_ was already unlinked from imgui_drawer_ above. Detach it
  // from its presenter so SDK mode runs OnLeavePresenter() before disposal; in
  // detached mode the drawer never had a presenter, so SetPresenter(nullptr) is
  // a no-op.
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
