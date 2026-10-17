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

}  // namespace refii::mods
