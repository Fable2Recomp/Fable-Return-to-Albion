// refii - Godot's answers to the boxes and the Options screen it owns, handed to the guest's own
// answer functions on the guest tick thread (display_box_hooks.cpp, options_hooks.cpp), and the
// statements the pause screen's message box shares with game_state_hooks.cpp.

#pragma once

#include <cstdint>
#include <string>

#include <rex/ppc.h>

namespace refii::fable3 {

/// `box:<HEX>:<verb>[:<value>]` — a box Godot owns, answered: `yes` / `no` (a question),
/// `dismiss` / `action` (a message, the download form's X; a quest box; a notice; the pause
/// screen's message box), `accept:<value>` / `cancel` (the numeric adjuster).
inline constexpr char kBoxAnswerAction[] = "box:";
/// `menubox:<row>` (0-based) picks a row of the open menu box; `menubox-cancel` cancels it.
inline constexpr char kMenuBoxAnswerAction[] = "menubox:";
inline constexpr char kMenuBoxCancelAction[] = "menubox-cancel";
/// `options:<action>` — one game action into the showing Options screen's own handler.
inline constexpr char kOptionsAction[] = "options:";

/// Answers a box from a `box:` / `menubox` action. False when it names nothing on screen.
bool AnswerBoxAction(PPCContext& ctx, uint8_t* base, const char* action);

/// Runs an `options:` action through the Options screen's handler. False when none shows.
bool AnswerOptionsAction(PPCContext& ctx, uint8_t* base, const char* action);

/// The pause screen's message box came up on `manager` with `text` (sub_825F9F38's arguments).
void StateModalMessageBox(uint32_t manager, const std::string& text, bool download);

/// Whether `box` is a queue's showing box, as the queue tick stated it.
bool BoxIsShowing(uint32_t box);

}  // namespace refii::fable3
