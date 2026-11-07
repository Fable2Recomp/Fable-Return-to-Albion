// refii — the options a title states for the shell (core/payload_bridge.h): each option's value as
// the last applier the running game uses was called with it, and as the player's saved settings
// last carried it. An applied value wins: it is what the game runs with now; the profile's stands
// until an applier has run.
//
// It crosses from a title's hooks (the payload, on the guest's threads) to its host bridge (on
// Godot's), which live in different modules; this is the one table both link.

#include <cstdint>
#include <mutex>

#include "core/payload_bridge.h"

namespace {

struct Option {
  bool applied = false;
  bool profiled = false;
  float applied_value = 0.0f;
  float profiled_value = 0.0f;
};

std::mutex g_lock;
Option g_options[REFII_OPTION_COUNT];

}  // namespace

extern "C" {

void refii_bridge_option_applied(uint32_t option, float value) {
  if (option >= REFII_OPTION_COUNT) {
    return;
  }
  std::lock_guard<std::mutex> guard(g_lock);
  g_options[option].applied = true;
  g_options[option].applied_value = value;
}

void refii_bridge_option_profiled(uint32_t option, float value) {
  if (option >= REFII_OPTION_COUNT) {
    return;
  }
  std::lock_guard<std::mutex> guard(g_lock);
  g_options[option].profiled = true;
  g_options[option].profiled_value = value;
}

uint32_t refii_bridge_options_read(float* out_values, uint32_t capacity) {
  std::lock_guard<std::mutex> guard(g_lock);
  uint32_t mask = 0u;
  for (uint32_t i = 0; i < REFII_OPTION_COUNT; ++i) {
    const Option& option = g_options[i];
    if (!option.applied && !option.profiled) {
      continue;
    }
    mask |= 1u << i;
    if (out_values != nullptr && i < capacity) {
      out_values[i] = option.applied ? option.applied_value : option.profiled_value;
    }
  }
  return mask;
}

}  // extern "C"
