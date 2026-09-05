#include "host_input.h"

#include <atomic>
#include <cstring>
#include <string>

#include <rex/input/input.h>

namespace refii::host {

// The X_* status constants live in rex; the overrides above are already qualified, and
// qualifying every return value as well would be noise.
using namespace rex;

namespace {

/// One published snapshot plus a sequence counter.
///
/// A seqlock rather than a mutex: the guest polls this on its own threads, potentially
/// every frame per pad, and must never block the host's frame thread — nor be blocked by
/// it. Writers bump the sequence odd, write, bump it even; readers retry while the
/// sequence is odd or changed underneath them. A torn controller frame would show as a
/// button held for one poll, which is exactly the artefact this avoids.
struct Slot {
    std::atomic<uint32_t> sequence{0};
    ControllerState state{};

    /// Bumped whenever the state actually changes, so the guest can tell a fresh poll
    /// from a repeat. The guest uses packet_number for exactly this.
    std::atomic<uint32_t> packet{0};

    std::atomic<uint32_t> vibration{0};  // left << 16 | right
};

Slot& SlotFor(uint32_t user_index) {
    static Slot slots[kMaxControllers];
    return slots[user_index];
}

bool SameInputs(const ControllerState& a, const ControllerState& b) {
    return a.connected == b.connected && a.buttons == b.buttons &&
           a.left_trigger == b.left_trigger && a.right_trigger == b.right_trigger &&
           a.thumb_lx == b.thumb_lx && a.thumb_ly == b.thumb_ly &&
           a.thumb_rx == b.thumb_rx && a.thumb_ry == b.thumb_ry;
}

ControllerState Read(uint32_t user_index, uint32_t* out_packet) {
    Slot& slot = SlotFor(user_index);
    ControllerState copy{};
    uint32_t before = 0;
    do {
        before = slot.sequence.load(std::memory_order_acquire);
        if ((before & 1u) != 0u) {
            // A write is in flight; spin rather than read a half-updated frame. Writes
            // are a handful of stores, so this cannot last.
            continue;
        }

        copy = slot.state;
        if (out_packet != nullptr) {
            *out_packet = slot.packet.load(std::memory_order_relaxed);
        }
    } while (before != slot.sequence.load(std::memory_order_acquire));

    return copy;
}

}  // namespace

void PublishControllerState(uint32_t user_index, const ControllerState& state) {
    if (user_index >= kMaxControllers) {
        return;
    }

    Slot& slot = SlotFor(user_index);

    // Only advance the packet number on a real change. The guest treats an unchanged
    // packet_number as "nothing happened" and may skip work; bumping it every frame
    // would defeat that and make every poll look like fresh input.
    const bool changed = !SameInputs(slot.state, state);

    slot.sequence.fetch_add(1, std::memory_order_release);
    slot.state = state;
    if (changed) {
        slot.packet.fetch_add(1, std::memory_order_relaxed);
    }
    slot.sequence.fetch_add(1, std::memory_order_release);
}

bool TakeVibration(uint32_t user_index, uint16_t* out_left, uint16_t* out_right) {
    if (user_index >= kMaxControllers) {
        return false;
    }

    const uint32_t packed = SlotFor(user_index).vibration.load(std::memory_order_relaxed);
    if (out_left != nullptr) {
        *out_left = static_cast<uint16_t>(packed >> 16);
    }
    if (out_right != nullptr) {
        *out_right = static_cast<uint16_t>(packed & 0xFFFFu);
    }
    return true;
}

HostInputDriver::HostInputDriver(rex::ui::Window* window, size_t window_z_order)
    : InputDriver(window, window_z_order) {}

HostInputDriver::~HostInputDriver() = default;

rex::X_STATUS HostInputDriver::Setup() { return X_STATUS_SUCCESS; }

uint32_t HostInputDriver::SlotOf(rex::input::DeviceId id) {
    const uint64_t raw = static_cast<uint64_t>(id);
    return raw >= 1u && raw <= kMaxControllers ? static_cast<uint32_t>(raw - 1u) : kMaxControllers;
}

void HostInputDriver::EnumerateDevices(std::vector<rex::input::DeviceInfo>& out) {
    // Every slot, every time, in slot order — see the header. Ordinals are the input
    // system's to assign; they are left at zero.
    for (uint32_t slot = 0; slot < kMaxControllers; ++slot) {
        rex::input::DeviceInfo info;
        info.id = static_cast<rex::input::DeviceId>(static_cast<uint64_t>(slot) + 1u);
        info.name = "Host pad " + std::to_string(slot);
        info.guid = "refii-host-pad-" + std::to_string(slot);
        info.synthetic = false;
        out.push_back(std::move(info));
    }
}

rex::X_RESULT HostInputDriver::GetDeviceCapabilities(rex::input::DeviceId id, uint32_t flags,
                                                     rex::input::X_INPUT_CAPABILITIES* out_caps) {
    (void)flags;
    const uint32_t user_index = SlotOf(id);
    if (user_index >= kMaxControllers || out_caps == nullptr) {
        return X_ERROR_BAD_ARGUMENTS;
    }

    if (!Read(user_index, nullptr).connected) {
        return X_ERROR_DEVICE_NOT_CONNECTED;
    }

    std::memset(out_caps, 0, sizeof(*out_caps));

    // A plain wired gamepad. The capability gamepad/vibration fields are MASKS of what
    // the device can report, not a reading — every bit set means "this pad has all the
    // usual controls", which is what a host feeding a virtual pad should claim.
    out_caps->type = 0x01;     // XINPUT_DEVTYPE_GAMEPAD
    out_caps->sub_type = 0x01; // XINPUT_DEVSUBTYPE_GAMEPAD
    out_caps->flags = 0;
    out_caps->gamepad.buttons = 0xFFFF;
    out_caps->gamepad.left_trigger = 0xFF;
    out_caps->gamepad.right_trigger = 0xFF;
    out_caps->gamepad.thumb_lx = static_cast<int16_t>(0xFFFFu);
    out_caps->gamepad.thumb_ly = static_cast<int16_t>(0xFFFFu);
    out_caps->gamepad.thumb_rx = static_cast<int16_t>(0xFFFFu);
    out_caps->gamepad.thumb_ry = static_cast<int16_t>(0xFFFFu);
    out_caps->vibration.left_motor_speed = 0xFFFF;
    out_caps->vibration.right_motor_speed = 0xFFFF;
    return X_ERROR_SUCCESS;
}

rex::X_RESULT HostInputDriver::GetDeviceState(rex::input::DeviceId id,
                                              rex::input::X_INPUT_STATE* out_state) {
    const uint32_t user_index = SlotOf(id);
    if (user_index >= kMaxControllers || out_state == nullptr) {
        return X_ERROR_BAD_ARGUMENTS;
    }

    uint32_t packet = 0;
    const ControllerState state = Read(user_index, &packet);
    if (!state.connected) {
        return X_ERROR_DEVICE_NOT_CONNECTED;
    }

    out_state->packet_number = packet;
    out_state->gamepad.buttons = state.buttons;
    out_state->gamepad.left_trigger = state.left_trigger;
    out_state->gamepad.right_trigger = state.right_trigger;
    out_state->gamepad.thumb_lx = state.thumb_lx;
    out_state->gamepad.thumb_ly = state.thumb_ly;
    out_state->gamepad.thumb_rx = state.thumb_rx;
    out_state->gamepad.thumb_ry = state.thumb_ry;
    return X_ERROR_SUCCESS;
}

rex::X_RESULT HostInputDriver::SetDeviceVibration(rex::input::DeviceId id,
                                                  rex::input::X_INPUT_VIBRATION* vibration) {
    const uint32_t user_index = SlotOf(id);
    if (user_index >= kMaxControllers || vibration == nullptr) {
        return X_ERROR_BAD_ARGUMENTS;
    }

    if (!Read(user_index, nullptr).connected) {
        return X_ERROR_DEVICE_NOT_CONNECTED;
    }

    // Stored rather than applied: the host owns the actual device, so rumble is its to
    // deliver. Kept even if nothing reads it yet, because dropping it silently would
    // make a later "why is there no rumble" hunt start in the wrong place.
    const uint32_t packed =
        (static_cast<uint32_t>(uint16_t(vibration->left_motor_speed)) << 16) |
        static_cast<uint32_t>(uint16_t(vibration->right_motor_speed));
    SlotFor(user_index).vibration.store(packed, std::memory_order_relaxed);
    return X_ERROR_SUCCESS;
}

rex::X_RESULT HostInputDriver::GetDeviceKeystroke(rex::input::DeviceId id, uint32_t flags,
                                                  rex::input::X_INPUT_KEYSTROKE* out_keystroke) {
    (void)flags;
    if (out_keystroke == nullptr) {
        return X_ERROR_BAD_ARGUMENTS;
    }

    // No keystroke translation. This is the pad-to-virtual-key path the dashboard and
    // text entry use; the guest polls the state for gameplay. X_ERROR_EMPTY is "nothing
    // queued", which is true and is what the title expects when idle.
    if (SlotOf(id) >= kMaxControllers) {
        return X_ERROR_BAD_ARGUMENTS;
    }

    std::memset(out_keystroke, 0, sizeof(*out_keystroke));
    return X_ERROR_EMPTY;
}

}  // namespace refii::host
