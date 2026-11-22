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
/// its config beside whatever binary happened to be hosting it — in a dev tree the
/// user's Godot install, which is a file the launcher never writes. The host knows where
/// the app actually lives; it states it here rather than letting the guest guess.
///
/// Empty means "not stated", and the SDK's executable-relative defaults stand.
void RefiiSetAppRoot(const std::filesystem::path& root);
const std::filesystem::path& RefiiGetAppRoot();

/// The runtime config file the host states: the RUNNING TITLE's own, which the guest loads its
/// cvars from. Every title shares this host and the app root, so the file's name is the title's
/// and only the host knows which title it started; the runtime names none.
///
/// Empty means "not stated", and the SDK's executable-relative default stands.
void RefiiSetConfigPath(const std::filesystem::path& path);
const std::filesystem::path& RefiiGetConfigPath();

/// The running title's log folder the host states (<app root>/logs/<Title>): the guest's
/// segments, its crash record and the hang watchdog's dumps. Empty means "not stated", and the
/// executable-relative logs/ stands.
void RefiiSetLogDirectory(const std::filesystem::path& path);
const std::filesystem::path& RefiiGetLogDirectory();

/// The host engine's most recent errors, which a crash record carries: a fault inside the
/// engine is usually the second half of an allocation it reported failing a moment earlier,
/// and that report is otherwise only in a log the player rarely sends.
///
/// RefiiNoteEngineError keeps the last 32 distinct lines, folding a repeat of a kept line into
/// its count. RefiiVisitEngineErrors hands them over in the order each was first seen, as
/// "<seconds it was last seen, from the first note> x<count> <text>"; it takes no lock and
/// allocates nothing, so a fault handler may call it.
///
/// RefiiNoteEngineState keeps ONE line, the newest: what the engine last said of itself
/// (adapter, graphics memory in use, window). RefiiEngineState is that line, empty until stated.
void RefiiNoteEngineError(const char* text);
void RefiiNoteEngineState(const char* text);
const char* RefiiEngineState();
void RefiiVisitEngineErrors(void (*visit)(void* context, const char* line), void* context);

std::unique_ptr<rex::ui::WindowedApp> CreateRefiiApp(rex::ui::WindowedAppContext& app_context);

int RunRefiiEmbedded(
    const std::filesystem::path& game_directory,
    const std::vector<std::string>& launch_arguments);
