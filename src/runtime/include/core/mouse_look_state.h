// refii — the mouse's camera turn as shared state (src/runtime/src/core/mouse_look_state.cpp).
//
// The host posts here; a title's camera hook takes the turn and states what its camera made of
// it. Ordinary C++ symbols: the host and refii_runtime are both our build.

#pragma once

#include <cstdint>

namespace refii::vfs {

void MouseLookPost(float yaw_degrees, float pitch_degrees);
void MouseLookSetDevice(bool mouse);
void MouseLookWheel(int32_t notches);
uint32_t MouseLookState(double* out_values, uint32_t count);

}  // namespace refii::vfs
