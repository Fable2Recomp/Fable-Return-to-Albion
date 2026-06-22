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
#include "renderer_runtime.h"
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

    if (!RefiiShouldSuppressReXGlueRenderer()) {
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
