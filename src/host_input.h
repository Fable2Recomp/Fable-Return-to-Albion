// refii - host-fed controller input
//
// Godot owns input, exactly as it owns the window and the world. This is the seam the
// guest reads through: Godot pushes a controller snapshot per frame, and the guest's
// XInput calls are served from it.
//
// It exists because the guest's SDL input driver initialises its subsystems in
// OnWindowAvailable, and an embedded guest deliberately has no window — so that driver
// is permanently uninitialised and every XInput entry point was aborting the process
// when Fable 2 polled for controller capabilities during startup.

#pragma once

#include <cstdint>

#include <rex/input/input_driver.h>

namespace refii::host {

/// One controller's state, in XInput terms, as the host last published it.
///
/// Deliberately the Xbox's own vocabulary rather than Godot's: the guest is the consumer
/// and translating once on the way in is cheaper and less lossy than translating on
/// every read. Button bits are X_INPUT_GAMEPAD_*.
struct ControllerState {
    bool connected = false;
    uint16_t buttons = 0;
    uint8_t left_trigger = 0;
    uint8_t right_trigger = 0;
    int16_t thumb_lx = 0;
    int16_t thumb_ly = 0;
    int16_t thumb_rx = 0;
    int16_t thumb_ry = 0;
};

/// Xbox 360 supports four pads and so does the guest; anything above this is refused
/// rather than silently folded onto pad 0.
inline constexpr uint32_t kMaxControllers = 4;

/// Publishes a controller snapshot. Safe to call from the host's frame thread while the
/// guest reads on its own threads.
void PublishControllerState(uint32_t user_index, const ControllerState& state);

/// The rumble the guest most recently asked for, so the host can drive it. Returns false
/// when the index is out of range.
bool TakeVibration(uint32_t user_index, uint16_t* out_left, uint16_t* out_right);

/// The guest-facing driver. Reads only what PublishControllerState wrote.
class HostInputDriver final : public rex::input::InputDriver {
public:
    HostInputDriver(rex::ui::Window* window, size_t window_z_order);
    ~HostInputDriver() override;

    rex::X_STATUS Setup() override;

    rex::X_RESULT GetCapabilities(uint32_t user_index, uint32_t flags,
                             rex::input::X_INPUT_CAPABILITIES* out_caps) override;
    rex::X_RESULT GetState(uint32_t user_index, rex::input::X_INPUT_STATE* out_state) override;
    rex::X_RESULT SetState(uint32_t user_index,
                      rex::input::X_INPUT_VIBRATION* vibration) override;
    rex::X_RESULT GetKeystroke(uint32_t user_index, uint32_t flags,
                          rex::input::X_INPUT_KEYSTROKE* out_keystroke) override;
};

}  // namespace refii::host
