// refii - ReXGlue Recompiled Project
//
// This file is yours to edit. 'rexglue migrate' will NOT overwrite it.
// Customize your app by overriding virtual hooks from rex::ReXApp.

#pragma once

// NOT generated/refii_init.h. That header is emitted from the player's own game image and
// does not exist on a build machine; the payload's image descriptor arrives through the
// loader instead.
#include "refii_payload/payload.h"

// RefiiGetAppRoot — the install directory the host states, rather than the executable
// folder the SDK would otherwise infer.
#include "refii_host.h"

#include <achievement/achievement_runtime.h>
#include <fmt/format.h>
#include <rex/cvar.h>
#include <rex/filesystem.h>
#include <rex/logging.h>
#include <rex/rex_app.h>
#include <rex/system/kernel_state.h>
#include <rex/system/user_module.h>
#include <rex/input/device_assignment.h>
#include <rex/input/input_system.h>
#include <rex/input/nop/nop_input_driver.h>

#include "host_audio.h"
#include "host_input.h"

#if REFII_HAS_RENDERER_RUNTIME
#include "renderdoc_debugger.h"
#include "renderer_runtime.h"
#include "runtime/cvars.h"
#include "runtime/plume/general/plume_backend.h"
#include "runtime/plume/general/plume_graphics_system.h"
#include "runtime/plume/general/plume_imgui_overlay.h"
#endif

#include <algorithm>
#include <span>
#include <cctype>
#include <cstdlib>
#include <fstream>
#include <memory>
#include <string>
#include <vector>

namespace {

inline bool RefiiUsePackagedLinuxPaths() {
#if defined(__linux__)
  const char *appImagePath = std::getenv("APPIMAGE");
  if (appImagePath != nullptr && appImagePath[0] != '\0') {
    return true;
  }

  const char *appDirPath = std::getenv("APPDIR");
  if (appDirPath != nullptr && appDirPath[0] != '\0') {
    return true;
  }

  const char *flatpakId = std::getenv("FLATPAK_ID");
  if (flatpakId != nullptr && flatpakId[0] != '\0') {
    return true;
  }
#endif
  return false;
}

inline std::filesystem::path RefiiGetPackagedLinuxRuntimeRoot() {
#if defined(__linux__)
  if (!RefiiUsePackagedLinuxPaths()) {
    return {};
  }

  const char *xdgDataHome = std::getenv("XDG_DATA_HOME");
  if (xdgDataHome != nullptr && xdgDataHome[0] != '\0') {
    return (std::filesystem::path(xdgDataHome) / "refii").lexically_normal();
  }

  const char *homeDirectory = std::getenv("HOME");
  if (homeDirectory != nullptr && homeDirectory[0] != '\0') {
    return (std::filesystem::path(homeDirectory) / ".local" / "share" / "refii")
        .lexically_normal();
  }
#endif
  return {};
}

inline std::string RefiiTrim(std::string value) {
  const auto isWhitespace = [](unsigned char ch) {
    return std::isspace(ch) != 0;
  };

  value.erase(value.begin(),
              std::find_if(value.begin(), value.end(), [&](unsigned char ch) {
                return !isWhitespace(ch);
              }));
  value.erase(std::find_if(value.rbegin(), value.rend(),
                           [&](unsigned char ch) { return !isWhitespace(ch); })
                  .base(),
              value.end());
  return value;
}

inline std::string RefiiUnquoteToml(std::string value) {
  value = RefiiTrim(std::move(value));
  if (value.size() < 2 || value.front() != '"' || value.back() != '"') {
    return value;
  }

  std::string result;
  result.reserve(value.size() - 2);
  bool escaped = false;
  for (std::size_t i = 1; i + 1 < value.size(); ++i) {
    const char ch = value[i];
    if (escaped) {
      switch (ch) {
      case 'n':
        result.push_back('\n');
        break;
      case 'r':
        result.push_back('\r');
        break;
      case 't':
        result.push_back('\t');
        break;
      default:
        result.push_back(ch);
        break;
      }
      escaped = false;
      continue;
    }
    if (ch == '\\') {
      escaped = true;
      continue;
    }
    result.push_back(ch);
  }
  return result;
}

inline void
RefiiApplyPackagedRuntimeOverrides(const std::filesystem::path &configPath,
                                   rex::PathConfig &paths) {
  std::ifstream stream(configPath);
  if (!stream.is_open()) {
    return;
  }

  std::string line;
  while (std::getline(stream, line)) {
    const std::size_t commentPos = line.find('#');
    if (commentPos != std::string::npos) {
      line.erase(commentPos);
    }

    const std::size_t equalsPos = line.find('=');
    if (equalsPos == std::string::npos) {
      continue;
    }

    const std::string key = RefiiTrim(line.substr(0, equalsPos));
    const std::string value = RefiiUnquoteToml(line.substr(equalsPos + 1));
    if (key.empty() || value.empty()) {
      continue;
    }

    if (key == "game_data_root") {
      paths.game_data_root = value;
    } else if (key == "user_data_root") {
      paths.user_data_root = value;
    } else if (key == "update_data_root") {
      paths.update_data_root = value;
    } else if (key == "cache_path") {
      paths.cache_root = value;
    }
  }
}

inline void RefiiEnsureDirectoryExists(const std::filesystem::path &path) {
  if (path.empty()) {
    return;
  }

  std::error_code error;
  std::filesystem::create_directories(path, error);
}

/// Built-in `rexnet.toml`, written only when the install has none. Mirrors
/// assets/metadata/rexnet.toml; that file carries the reasoning for each value.
inline void RefiiWriteDefaultRexNetConfig(const std::filesystem::path &path) {
  std::error_code ec;
  if (std::filesystem::exists(path, ec)) {
    return;
  }
  std::ofstream out(path, std::ios::binary | std::ios::trunc);
  if (!out.is_open()) {
    return;
  }
  out << R"(# Written by the refii launcher on first launch. A shipped
# assets/metadata/rexnet.toml wins and replaces this once it is newer.

[game]
session_model = "private-invite"
# XRNM runs its own reliability; the game plane must stay unreliable.
game_channel = "unreliable"
# 0 = take the id from the XEX header (0x4D5307F1).
advertised_title_id = 0

# Ambient "Connected" tier. The loader defaults this off, so a title that
# wants passive presence has to say so here.
[shard]
enabled = true
cap = 255
surface_as_friends = true
)";
}

inline void
RefiiEnsurePackagedRuntimeDirectories(const rex::PathConfig &paths) {
  RefiiEnsureDirectoryExists(paths.config_path.parent_path());

  std::filesystem::path gameBase = paths.game_data_root;
  if (gameBase.filename() == "game") {
    gameBase = gameBase.parent_path();
  }

  RefiiEnsureDirectoryExists(gameBase);
  RefiiEnsureDirectoryExists(paths.user_data_root);
  RefiiEnsureDirectoryExists(paths.update_data_root);
  RefiiEnsureDirectoryExists(paths.cache_root);

  // Seed per-title metadata (rexnet.toml, ...) into the packaged runtime.
  //
  // Runtime::FindMetadataPath only searches under game_data_root, so a
  // packaged install that never receives these files silently runs with all
  // per-title configuration inactive -- RexNet shard opt-in,
  // advertised_title_id, presence mappings -- and the sole symptom is one
  // warning at startup. That cost a debugging round on 2026-07-21, where a
  // packaged instance and a build-tree instance behaved differently for no
  // visible reason.
  const std::filesystem::path runtime_metadata = gameBase / "metadata";
  RefiiEnsureDirectoryExists(runtime_metadata);

  const std::filesystem::path bundled_metadata =
      rex::filesystem::GetExecutableFolder() / "assets" / "metadata";
  std::error_code ec;
  if (std::filesystem::exists(bundled_metadata, ec)) {
    for (const auto &entry :
         std::filesystem::directory_iterator(bundled_metadata, ec)) {
      if (ec) {
        break;
      }
      std::error_code entry_ec;
      if (!entry.is_regular_file(entry_ec) || entry_ec) {
        continue;
      }
      const std::filesystem::path dst =
          runtime_metadata / entry.path().filename();
      // Copy when absent, and refresh when the shipped copy is newer. These
      // are shipped data rather than user settings (user config lives in
      // refii.toml), so an updated build must not be shadowed by a stale copy
      // left over from a previous install.
      std::error_code stat_ec;
      bool stale = !std::filesystem::exists(dst, stat_ec);
      if (!stale) {
        const auto src_time =
            std::filesystem::last_write_time(entry.path(), stat_ec);
        const auto dst_time = std::filesystem::last_write_time(dst, stat_ec);
        stale = !stat_ec && src_time > dst_time;
      }
      if (stale) {
        std::error_code copy_ec;
        std::filesystem::copy_file(
            entry.path(), dst,
            std::filesystem::copy_options::overwrite_existing, copy_ec);
      }
    }
  }

  // A package that shipped no metadata at all still needs the title's config,
  // or ambient shards stay off and nothing forms on the LAN.
  RefiiWriteDefaultRexNetConfig(runtime_metadata / "rexnet.toml");
}

inline std::vector<char> RefiiLoadWindowIconBytes() {
  const std::filesystem::path icon_path =
      rex::filesystem::GetExecutableFolder() / "assets" / "launcher" /
      "assets" / "icon.png";
  std::ifstream stream(icon_path, std::ios::binary);
  if (!stream.is_open()) {
    return {};
  }

  stream.seekg(0, std::ios::end);
  const std::streamoff size = stream.tellg();
  if (size <= 0) {
    return {};
  }
  stream.seekg(0, std::ios::beg);

  std::vector<char> bytes(static_cast<std::size_t>(size));
  if (!stream.read(bytes.data(), static_cast<std::streamsize>(bytes.size()))) {
    return {};
  }
  return bytes;
}

inline std::string RefiiGetLowerRuntimeCVar(std::string_view name) {
  std::string value = rex::cvar::GetFlagByName(name);
  std::transform(
      value.begin(), value.end(), value.begin(),
      [](unsigned char ch) { return static_cast<char>(std::tolower(ch)); });
  return value;
}

/// True when the guest is embedded in a host that owns presentation (Godot). Set by the
/// host bridge before the guest boots; see rex_app.cpp's SetupPresentation.
inline bool RefiiIsEmbeddedHeadless() {
  const char* embedded = std::getenv("REX_EMBED_HEADLESS");
  return embedded != nullptr && embedded[0] == '1';
}

inline bool RefiiShouldSuppressReXGlueRenderer() {
  // Embedded, there is no guest GPU at all — the host presents. This has to be checked
  // FIRST and cannot go through the cvar: `renderer_backend` is defined in
  // src/renderer/src/rendering/core/cvars.cpp, so with the renderer not built the cvar
  // is never registered and GetFlagByName returns EMPTY — never "plume", no matter what
  // is passed on the command line or in the environment.
  //
  // Empty then falls through to the netplay SDK's default of gpu_plugin="xenos", which
  // tries to dlopen librexgpu-xenosd.so from beside the executable. Embedded, that is
  // the HOST's directory, so it fails there and takes the boot down.
  if (RefiiIsEmbeddedHeadless()) {
    return true;
  }

  const std::string backend = RefiiGetLowerRuntimeCVar("renderer_backend");
  return backend == "plume";
}

inline void RefiiLogRuntimeBackendSplit() {
  static bool logged = false;
  if (logged) {
    return;
  }
  logged = true;

  // THE VFS IS NO LONGER A SPLIT. Godot owns the whole file system, so there is one
  // implementation and nothing to report about which was chosen — only the renderer still has
  // a backend worth naming.
  const std::string renderer_backend =
      RefiiGetLowerRuntimeCVar("renderer_backend");
  REXLOG_INFO("Refii runtime backend: renderer_backend={} plume_renderer={} refii_vfs=true",
              renderer_backend, renderer_backend == "plume" ? "true" : "false");
}

// The title's XDBF resource — the section named by the title id inside the executable
// module — handed to the achievement seam once, for Godot to parse. The definitions, the
// strings and the icons are all in it; nothing native reads them any more.
inline void RefiiPublishTitleXdbf(rex::system::KernelState *kernel_state) {
  if (kernel_state == nullptr) {
    return;
  }

  const rex::system::object_ref<rex::system::UserModule> module =
      kernel_state->GetExecutableModule();
  if (!module) {
    return;
  }

  uint32_t resource_data = 0;
  uint32_t resource_size = 0;
  if (XFAILED(module->GetSection(fmt::format("{:08X}", module->title_id()).c_str(),
                                 &resource_data, &resource_size)) ||
      resource_data == 0 || resource_size == 0) {
    return;
  }

  const uint8_t *bytes = kernel_state->memory()->TranslateVirtual(resource_data);
  refii::achievement::SetTitleXdbf(
      std::span<const uint8_t>(bytes, static_cast<size_t>(resource_size)));
}

} // namespace

class RefiiApp : public rex::ReXApp {
public:
  using rex::ReXApp::ReXApp;

  static std::unique_ptr<rex::ui::WindowedApp>
  Create(rex::ui::WindowedAppContext &ctx) {
    return std::unique_ptr<RefiiApp>(
        new RefiiApp(ctx, "refii", refii::payload::ImageConfig()));
  }

  void OnPreSetup(rex::RuntimeConfig &config) override {
    RefiiLogRuntimeBackendSplit();

    // Embedded, the HOST owns input.
    //
    // The SDK's default input system builds an SDL driver whose subsystems are
    // initialised in OnWindowAvailable. An embedded guest deliberately creates no
    // window, so that never fires and every XInput entry point is left uninitialised —
    // which aborted the process the moment Fable 2 polled controller capabilities during
    // startup. Swapping the whole input system is cleaner than teaching the SDL driver
    // to be windowless, and it is the same shape as the window: Godot reads the real
    // device and the guest is served a snapshot.
    //
    // The NOP driver stays behind ours as the fallback the SDK always installs, so a
    // guest asking about pad 4 gets the same answer it always did.
    if (RefiiIsEmbeddedHeadless()) {
      config.input_factory =
          [](bool tool_mode) -> std::unique_ptr<rex::system::IInputSystem> {
        // X_STATUS_SUCCESS is a macro that casts to an unqualified X_STATUS, so it can
        // only be used where that name is visible.
        using namespace rex;

        auto input = std::make_unique<rex::input::InputSystem>(nullptr);
        if (!tool_mode) {
          auto host_driver =
              std::make_unique<refii::host::HostInputDriver>(nullptr, 0);
          if (host_driver->Setup() == X_STATUS_SUCCESS) {
            input->AddDriver(std::move(host_driver));
          }
        }

        input->AddDriver(
            std::make_unique<rex::input::nop::NopInputDriver>(nullptr, 1));
        // THE POLICY IS NOT OPTIONAL. The v0.10 InputSystem answers every query
        // DEVICE_NOT_CONNECTED until an assignment maps device ordinals to guest users;
        // the SDK's own factory ends the same way. SlotAssignment: ordinal N feeds
        // user N, which is the contract HostInputDriver enumerates for.
        input->SetDeviceAssignment(std::make_unique<rex::input::SlotAssignment>());
        return input;
      };
      REXLOG_INFO("Refii: embedded — host-fed controller input installed");

      // Embedded, the HOST owns audio too — and for a reason beyond tidiness.
      //
      // ReXApp::SetupPresentation installs the SDL audio system BEFORE the headless
      // branch, so an embedded guest opened a real host audio device and played to it on
      // its own clock while Godot drew on another. That is not a latency problem to tune
      // out; it is two clocks.
      //
      // The guest's audio clock is the output device's CONSUMPTION rate: one semaphore
      // credit per frame consumed wakes the audio worker, which re-enters the guest's
      // render-driver callback, which mixes the next frame. Handing that credit to Godot
      // makes Godot the clock, which is the whole point — and is also why the device
      // cannot simply be closed to silence it: with nothing consuming, the guest's audio
      // thread stops being pumped and NLHAudio wedges at its ten-packet cap.
      config.audio_factory =
          [](rex::runtime::FunctionDispatcher* function_dispatcher)
          -> std::unique_ptr<rex::system::IAudioSystem> {
        return refii::host::HostAudioSystem::Create(function_dispatcher);
      };
      REXLOG_INFO("Refii: embedded — host-fed audio output installed");
    }
#if REFII_HAS_RENDERER_RUNTIME
    // The backend setting is final here (TOML + launcher env both applied);
    // freeze the cache so per-hook backend queries stop string-parsing the
    // cvar (measured on the xenia per-D3D-call path).
    refii::renderer::FreezeRendererBackendCache();

    // Native/xenia backend arms RenderDoc HERE, in pre-setup: it renders
    // through rexglue's own Vulkan instance (Plume's arming never touches
    // it), and refii's per-hook path is dormant in native mode
    // (RendererHooksEnabled() is false without the heavy observe cvar), so
    // the OnRendererGuestHookEnter arming never runs. Pre-setup is before
    // the guest boots — before rexglue's vkCreateInstance — so the layer
    // env lands in time and F12/captures work under backend=xenia. Plume
    // arms itself later in its own backend init; only arm here for native.
    if (RefiiGetLowerRuntimeCVar("renderer_backend") != "plume" &&
        refii::renderer::RenderDocNativeArmEnabled()) {
      refii::renderer::PrepareRenderDocVulkanLayer();
      refii::renderer::InitializeRenderDocDebugger();
      REXLOG_INFO(
          "Refii RenderDoc armed for the native/xenia backend in pre-setup; "
          "use F12 to capture (the capture_request file needs a per-frame "
          "poll that the native hook path does not run)");
    } else if (RefiiGetLowerRuntimeCVar("renderer_backend") != "plume" &&
               refii::renderer::RenderDocDebuggerEnabled()) {
      // renderer_renderdoc_native_arm defaults ON; this branch only fires
      // when it was explicitly disabled to isolate layer interactions.
      REXLOG_INFO(
          "Refii RenderDoc native arming SKIPPED "
          "(renderer_renderdoc_native_arm is off)");
    }
#endif  // REFII_HAS_RENDERER_RUNTIME

    if (!RefiiShouldSuppressReXGlueRenderer()) {
      // The SDK ships its Xenia-derived GPU emulation as an opt-in plugin
      // (empty gpu_plugin cvar = no graphics system = blank window).
      // backend=xenia means the user wants exactly that system, so default it.
      if (config.gpu_plugin.empty()) {
        config.gpu_plugin = "xenos";
        REXLOG_INFO(
            "Refii renderer backend is {}; defaulting the SDK GPU plugin to "
            "'xenos'",
            RefiiGetLowerRuntimeCVar("renderer_backend"));
      }
      return;
    }

#if REFII_HAS_RENDERER_RUNTIME
    config.graphics = std::make_unique<refii::renderer::PlumeGraphicsSystem>();
    REXLOG_INFO(
        "Refii renderer backend is {}; installing the Plume guest GPU frontend "
        "shim without a ReXGlue renderer presenter",
        RefiiGetLowerRuntimeCVar("renderer_backend"));
#else
    config.graphics.reset();
    REXLOG_INFO(
        "Refii renderer backend is {}; suppressing the ReXGlue graphics "
        "system so renderer hooks can own output",
        RefiiGetLowerRuntimeCVar("renderer_backend"));
#endif
  }

  bool OnNeedsImGuiDrawerWithoutPresenter() const override {
    return RefiiShouldSuppressReXGlueRenderer();
  }

  void OnPostSetup() override {
    if (window() != nullptr) {
      const std::vector<char> icon_bytes = RefiiLoadWindowIconBytes();
      if (!icon_bytes.empty()) {
        window()->SetIcon(icon_bytes.data(), icon_bytes.size());
      }
    }
#if REFII_HAS_RENDERER_RUNTIME
    if (RefiiShouldSuppressReXGlueRenderer()) {
      refii::renderer::AttachPlumeImGuiToReXGlue(window(), imgui_drawer());
    }
#endif
  }

  void OnPostLaunchModule(rex::system::XThread *thread) override {
    (void)thread;
    if (runtime() != nullptr) {
      RefiiPublishTitleXdbf(runtime()->kernel_state());
    }
  }

  void OnShutdown() override {
#if REFII_HAS_RENDERER_RUNTIME
    refii::renderer::ShutdownRendererPresentPump();
    refii::renderer::DetachPlumeImGuiFromReXGlue();
    refii::renderer::ShutdownPlumeImGuiOverlay();
    refii::renderer::ShutdownPlumeBackend();
#endif
  }

  // Set game and update paths relative to game_directory.
  // The assets layout is: game_directory/game/ and game_directory/update/.
  // Accept both `<game_root>/` and `<game_root>/game` to keep launch args
  // forgiving.
  void OnConfigurePaths(rex::PathConfig &paths) override {
    // WHAT THE HOST STATED WINS, and it is checked first for that reason. Hosted by
    // Godot, GetExecutableFolder() is Godot's directory: the SDK's defaults put the
    // config, the saves and the cache beside whatever binary is hosting us, which in a
    // dev tree is the user's Godot install. The launcher writes refii.toml at the app
    // root and the guest has to read that same file.
    if (const std::filesystem::path &appRoot = RefiiGetAppRoot(); !appRoot.empty()) {
      paths.config_path = appRoot / "refii.toml";
      if (paths.user_data_root.empty()) {
        paths.user_data_root = appRoot / "assets" / "save";
      }
      if (paths.cache_root.empty()) {
        paths.cache_root = appRoot / "cache";
      }
      RefiiEnsureDirectoryExists(paths.config_path.parent_path());
    }

    if (const std::filesystem::path runtimeRoot =
            RefiiGetPackagedLinuxRuntimeRoot();
        !runtimeRoot.empty()) {
      paths.config_path = runtimeRoot / "refii.toml";
      if (paths.game_data_root.empty()) {
        paths.game_data_root = runtimeRoot / "assets";
      }
      if (paths.cache_root.empty()) {
        paths.cache_root = runtimeRoot / "cache";
      }
      RefiiApplyPackagedRuntimeOverrides(paths.config_path, paths);
    }

    auto base = paths.game_data_root;
    if (base.empty()) {
      base = "assets";
    }
    if (base.filename() == "game" || base.filename() == "data") {
      base = base.parent_path();
    }

    if (base.is_relative() && !std::filesystem::exists(base)) {
      for (std::filesystem::path probe :
           {std::filesystem::current_path(),
            rex::filesystem::GetExecutableFolder()}) {
        bool resolved = false;
        for (int i = 0; i < 8 && !probe.empty(); ++i) {
          const std::filesystem::path candidate = probe / base;
          if (std::filesystem::exists(candidate)) {
            base = candidate;
            resolved = true;
            break;
          }
          const std::filesystem::path parent = probe.parent_path();
          if (parent == probe) {
            break;
          }
          probe = parent;
        }
        if (resolved) {
          break;
        }
      }
    }

    if (base.filename() == "game") {
      base = base.parent_path();
    }
    paths.game_data_root = base / "game";
    if (paths.update_data_root.empty()) {
      paths.update_data_root = base / "update";
    }

    if (RefiiUsePackagedLinuxPaths()) {
      RefiiEnsurePackagedRuntimeDirectories(paths);
    }
  }
};
