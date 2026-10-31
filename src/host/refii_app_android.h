// refii - the app's Android differences, one call each from refii_app.h
//
// Every function here has a desktop answer that leaves the app as it always was: no copy
// observer, the SDK's NOP fallback always installed, and the assets/game + assets/update layout.

#pragma once

#include <filesystem>
#include <system_error>

#include <rex/input/input_system.h>

#include "host_input.h"

namespace refii::host {

/// Registers the host driver's copy observer (HostInputDriver::ObserveStateCopy), which
/// acknowledges Android's tracked controller phases. Desktop requests no receipts.
inline void ObserveHostStateCopies(rex::input::InputSystem* input, HostInputDriver* driver) {
#ifdef __ANDROID__
    rex::input::SetInputStateCopyObserver(input, &HostInputDriver::ObserveStateCopy, driver);
#else
    (void)input;
    (void)driver;
#endif
}

/// Whether the SDK's NOP driver goes in behind the host's. Desktop retains the SDK's NOP
/// fallback. Android gameplay uses the host driver alone: NOP otherwise contributes a second
/// always-successful user-zero state and defeats exact single-driver copy receipts. Tool mode
/// retains its fallback.
inline bool KeepsNopFallback(bool tool_mode) {
#ifdef __ANDROID__
    return tool_mode;
#else
    (void)tool_mode;
    return true;
#endif
}

/// Whether `base` is itself the game and update root. A privately imported Android dump can be
/// a flat directory of any name, with default.xex/default.xexp beside data/. The normal
/// assets/game + assets/update layout is preserved when the supplied base contains no XEX.
inline bool IsFlatGameDump(const std::filesystem::path& base) {
#ifdef __ANDROID__
    std::error_code direct_game_error;
    return std::filesystem::is_regular_file(base / "default.xex", direct_game_error);
#else
    (void)base;
    return false;
#endif
}

}  // namespace refii::host
