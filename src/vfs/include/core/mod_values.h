// refii — hard mods' named values. A mod's hook PUBLISHES a float under "<mod>.<name>"
// (combat.stamina) and Godot reads it; Godot SETS a setting and the hook reads it.
//
// A feed, not a mirror: the value is whatever the hook states, sampled nowhere else. Header-only
// wrappers over the C entries in core/payload_bridge.h, so a mod's hook imports only C names.

#pragma once

#include <string_view>

#include "core/payload_bridge.h"

namespace refii::mods {

inline void Publish(std::string_view key, float value) {
  refii_bridge_mod_publish_f32(key.data(), key.size(), value);
}

/// The last value published under `key`, or false when none has been.
inline bool Published(std::string_view key, float& out) {
  return refii_bridge_mod_value_f32(key.data(), key.size(), &out) != 0;
}

inline void SetSetting(std::string_view key, float value) {
  refii_bridge_mod_set_setting_f32(key.data(), key.size(), value);
}

/// The setting Godot gave `key`, or `fallback` when it has given none.
inline float Setting(std::string_view key, float fallback) {
  return refii_bridge_mod_setting_f32(key.data(), key.size(), fallback);
}

/// Posts an event to the host when its mask wants `kind` (the bus calls this after the native
/// and Lua handlers ran, so the fields are the verdict).
inline void PostEvent(int32_t kind, std::string_view fields) {
  refii_bridge_mod_event_post(kind, fields.data(), fields.size());
}

inline bool HostWantsEvent(int32_t kind) {
  return kind >= 0 && kind < 64 && ((refii_bridge_mod_host_event_mask() >> kind) & 1u) != 0u;
}

// THE HOST'S SIDE of the same state (src/core/mod_values.cpp; linked by the host bridge, never
// by a mod): what RefiiHostDrainModEvents, RefiiHostSetModEventMask and RefiiHostPostModInput do.
/// Moves queued events into `out` as "<kind>\t<fields>\n" records up to `capacity` bytes;
/// an event that does not fit stays queued. Returns the bytes written.
size_t DrainEventsForHost(char* out, size_t capacity);
void SetHostEventMask(uint64_t mask);
void PostInputFromHost(std::string_view action, int32_t player, bool pressed);
void PostCommandFromHost(std::string_view line);
/// Moves the queued mod errors into `out`, one per line, oldest first. Returns the bytes written.
size_t DrainErrorsForHost(char* out, size_t capacity);

}  // namespace refii::mods
