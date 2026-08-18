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

/// The directory the HOST says owns this install: config, save data and cache.
///
/// Embedded, `GetExecutableFolder()` is Godot's directory, not ours, so the guest resolved
/// its refii.toml beside whatever binary happened to be hosting it — in a dev tree the
/// user's Godot install, which is a file the launcher never writes. The host knows where
/// the app actually lives; it states it here rather than letting the guest guess.
///
/// Empty means "not stated", and the SDK's executable-relative defaults stand.
void RefiiSetAppRoot(const std::filesystem::path& root);
const std::filesystem::path& RefiiGetAppRoot();

std::unique_ptr<rex::ui::WindowedApp> CreateRefiiApp(rex::ui::WindowedAppContext& app_context);

int RunRefiiEmbedded(
    const std::filesystem::path& game_directory,
    const std::vector<std::string>& launch_arguments);
