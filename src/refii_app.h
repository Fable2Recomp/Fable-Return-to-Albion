// refii - ReXGlue Recompiled Project
//
// This file is yours to edit. 'rexglue migrate' will NOT overwrite it.
// Customize your app by overriding virtual hooks from rex::ReXApp.

#pragma once

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
  // The assets layout is: game_directory/game/ and game_directory/update/
  void OnConfigurePaths(rex::PathConfig& paths) override {
    auto base = paths.game_data_root;
    paths.game_data_root = base / "game";
    paths.update_data_root = base / "update";
  }
};
