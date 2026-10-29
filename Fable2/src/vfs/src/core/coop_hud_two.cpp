// refii — PLAYER TWO'S SPLIT-SCREEN HUD: their targeted title and d-pad icons, as the pump states
// them from the guest's own builders called for hero two (hooks/guest_tick_pump.cpp,
// SamplePlayerTwoHud), read by the host bridge. Here, in refii_vfs, because both link it.

#include <cstdint>
#include <cstdio>
#include <mutex>
#include <string>

#include "core/payload_bridge_fable2.h"

namespace {

struct State {
  std::mutex mutex;
  bool known = false;
  uint32_t target = 0u;
  std::string title;
  std::string icons[4];
};

State& Hud() {
  static State state;
  return state;
}

}  // namespace

extern "C" void refii_bridge_player_two_hud_state(int32_t known, uint32_t target, const char* title,
                                                  const char* const* icons) {
  State& s = Hud();
  std::lock_guard lock(s.mutex);
  s.known = known != 0;
  if (!s.known) {
    return;
  }
  s.target = target;
  s.title = title != nullptr ? title : "";
  for (uint32_t i = 0; i < 4u; ++i) {
    s.icons[i] = icons != nullptr && icons[i] != nullptr ? icons[i] : "";
  }
}

extern "C" uint32_t refii_bridge_player_two_hud(uint32_t* target, char* out, uint32_t capacity) {
  State& s = Hud();
  std::lock_guard lock(s.mutex);
  if (!s.known || out == nullptr || capacity == 0u) {
    return 0u;
  }
  if (target != nullptr) {
    *target = s.target;
  }
  std::string text = s.title;
  for (const std::string& icon : s.icons) {
    text += '\n';
    text += icon;
  }
  std::snprintf(out, capacity, "%s", text.c_str());
  return 1u;
}
