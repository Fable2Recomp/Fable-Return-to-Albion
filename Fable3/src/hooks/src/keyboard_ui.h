// refii - Fable 3's system keyboard, answered by the host (keyboard_ui.cpp).

#pragma once

#include <cstdint>

namespace refii::fable3 {

/// The guest tick pump's action prefix that answers the open keyboard with UTF-8 text.
inline constexpr char kKeyboardAnswerAction[] = "keyboard:";
/// The action that cancels it.
inline constexpr char kKeyboardCancelAction[] = "keyboard-cancel";

/// Answers the keyboard the guest opened (its buffer and XOVERLAPPED), on the guest tick thread.
/// `text` is UTF-8, null for a cancel. False when no keyboard is open.
bool AnswerKeyboardUi(uint8_t* base, const char* text);

}  // namespace refii::fable3
