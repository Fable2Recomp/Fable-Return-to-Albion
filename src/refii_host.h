// refii - ReXGlue Recompiled Project
//
// Shared host entry points for standalone and embedded launches.

#pragma once

#include <filesystem>
#include <memory>
#include <string>
#include <vector>

namespace rex::ui {
class WindowedApp;
class WindowedAppContext;
}  // namespace rex::ui

std::unique_ptr<rex::ui::WindowedApp> CreateRefiiApp(rex::ui::WindowedAppContext& app_context);

int RunRefiiEmbedded(
    const std::filesystem::path& game_directory,
    const std::vector<std::string>& launch_arguments);
