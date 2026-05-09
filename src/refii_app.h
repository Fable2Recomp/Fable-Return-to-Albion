// refii - ReXGlue Recompiled Project
//
// This file is yours to edit. 'rexglue migrate' will NOT overwrite it.
// Customize your app by overriding virtual hooks from rex::ReXApp.

#pragma once

#include "generated/refii_init.h"

#include <rex/rex_app.h>

class RefiiApp : public rex::ReXApp {
 public:
  using rex::ReXApp::ReXApp;

  static std::unique_ptr<rex::ui::WindowedApp> Create(
      rex::ui::WindowedAppContext& ctx) {
    return std::unique_ptr<RefiiApp>(new RefiiApp(ctx, "refii",
        PPCImageConfig));
  }

  // Set game and update paths relative to game_directory.
  // The assets layout is: game_directory/game/ and game_directory/update/.
  // Accept both `<game_root>/` and `<game_root>/game` to keep launch args forgiving.
  void OnConfigurePaths(rex::PathConfig& paths) override {
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
    paths.update_data_root = base / "update";
  }
};
