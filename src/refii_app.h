// refii - ReXGlue Recompiled Project
//
// This file is yours to edit. 'rexglue migrate' will NOT overwrite it.
// Customize your app by overriding virtual hooks from rex::ReXApp.

#pragma once

#include "generated/refii_init.h"

#include <achievement/achievement_imgui.h>
#include <achievement/achievement_xdbf.h>
#include <rex/cvar.h>
#include <rex/filesystem.h>
#include <rex/logging.h>
#include <rex/rex_app.h>
#include <rex/system/kernel_state.h>
#include <rex/system/util/xdbf_utils.h>

#if REFII_HAS_RENDERER_RUNTIME
#include "renderdoc_debugger.h"
#include "renderer_runtime.h"
#include "runtime/cvars.h"
#include "runtime/plume/general/plume_backend.h"
#include "runtime/plume/general/plume_graphics_system.h"
#include "runtime/plume/general/plume_imgui_overlay.h"
#endif

#include <algorithm>
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

inline bool RefiiShouldSuppressReXGlueRenderer() {
  const std::string backend = RefiiGetLowerRuntimeCVar("renderer_backend");
  return backend == "plume";
}

inline void RefiiLogRuntimeBackendSplit() {
  static bool logged = false;
  if (logged) {
    return;
  }
  logged = true;

  const std::string renderer_backend =
      RefiiGetLowerRuntimeCVar("renderer_backend");
  const std::string vfs_backend =
      RefiiGetLowerRuntimeCVar("vfs_loose_asset_backend");
  REXLOG_INFO(
      "Refii runtime backend split: renderer_backend={} plume_renderer={} "
      "vfs_loose_asset_backend={} refii_vfs={}",
      renderer_backend, renderer_backend == "plume" ? "true" : "false",
      vfs_backend, vfs_backend == "refii" ? "true" : "false");
}

inline void
RefiiCacheAchievementMetadata(rex::system::KernelState *kernel_state) {
  if (kernel_state == nullptr) {
    return;
  }

  const rex::system::util::XdbfGameData db = kernel_state->title_xdbf();
  if (!db.is_valid()) {
    return;
  }

  refii::achievement::CacheAchievementsFromXdbf(
      db, rex::system::util::XdbfSection::kImage, db.default_language());
}

} // namespace

class RefiiApp : public rex::ReXApp {
public:
  using rex::ReXApp::ReXApp;

  static std::unique_ptr<rex::ui::WindowedApp>
  Create(rex::ui::WindowedAppContext &ctx) {
    return std::unique_ptr<RefiiApp>(
        new RefiiApp(ctx, "refii", PPCImageConfig));
  }

  void OnPreSetup(rex::RuntimeConfig &config) override {
    RefiiLogRuntimeBackendSplit();
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

    if (!RefiiShouldSuppressReXGlueRenderer()) {
#if defined(REFII_REXGLUE_NETPLAY) && REFII_REXGLUE_NETPLAY
      // The netplay SDK ships its Xenia-derived GPU emulation as an opt-in
      // plugin (empty gpu_plugin cvar = no graphics system = blank window).
      // backend=xenia means the user wants exactly that system, so default it.
      if (config.gpu_plugin.empty()) {
        config.gpu_plugin = "xenos";
        REXLOG_INFO(
            "Refii renderer backend is {}; defaulting the netplay SDK GPU "
            "plugin to 'xenos'",
            RefiiGetLowerRuntimeCVar("renderer_backend"));
      }
#endif
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

  void OnConfigureFonts(ImFontAtlas *atlas) override {
    refii::achievement::ConfigureAchievementFonts(atlas);
  }

  void OnCreateDialogs(rex::ui::ImGuiDrawer *drawer) override {
    refii::achievement::RegisterAchievementImGui(drawer, immediate_drawer());
  }

  void OnPostLaunchModule(rex::system::XThread *thread) override {
    (void)thread;
    if (runtime() != nullptr) {
      RefiiCacheAchievementMetadata(runtime()->kernel_state());
    }
  }

  void OnShutdown() override {
    refii::achievement::UnregisterAchievementImGui();
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
      std::filesystem::path probe = std::filesystem::current_path();
      for (int i = 0; i < 8 && !probe.empty(); ++i) {
        const std::filesystem::path candidate = probe / base;
        if (std::filesystem::exists(candidate)) {
          base = candidate;
          break;
        }
        const std::filesystem::path parent = probe.parent_path();
        if (parent == probe) {
          break;
        }
        probe = parent;
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
