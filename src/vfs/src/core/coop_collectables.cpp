// refii — ONE-TIME COLLECTABLES PER PLAYER (Fable 3's CECOneTimeCollectable, `sub_82948D70`):
// a silver key one player has taken stays in the world for the other, shown only to whoever still
// needs it, and goes once both have it. Which player has taken a key is a SAVED player value,
// `refii.key.<UniqueID>` (core/player_state.h), so it goes home with each hero's own save.
//
// The payload (coop_world_hooks.cpp) tracks the live key entities by address and UniqueID;
// the host bridge reads each one's taken mask for Godot, which hides the key in the view of the
// player who has it. Here, in refii_vfs, because both link it.

#include <cstdint>
#include <cstdio>
#include <cstring>
#include <mutex>
#include <unordered_map>

#include "core/payload_bridge.h"

namespace {

struct State {
  std::mutex mutex;
  std::unordered_map<uint32_t, uint64_t> keys;  // entity address -> UniqueID
};

State& Tables() {
  static State state;
  return state;
}

void KeyName(uint64_t uid, char* out, size_t size) {
  std::snprintf(out, size, "refii.key.%016llx", static_cast<unsigned long long>(uid));
}

uint32_t MaskOf(uint64_t uid) {
  char key[48];
  KeyName(uid, key, sizeof(key));
  uint32_t mask = 0u;
  for (int32_t player = 1; player <= 2; ++player) {
    float value = 0.0f;
    if (refii_bridge_player_get(player, key, std::strlen(key), &value) != 0 && value > 0.5f) {
      mask |= 1u << (player - 1);
    }
  }
  return mask;
}

}  // namespace

extern "C" void refii_bridge_collectable_track(uint32_t address, uint64_t uid) {
  State& s = Tables();
  std::lock_guard lock(s.mutex);
  s.keys[address] = uid;
}

extern "C" void refii_bridge_collectable_forget(uint32_t address) {
  State& s = Tables();
  std::lock_guard lock(s.mutex);
  s.keys.erase(address);
}

extern "C" uint32_t refii_bridge_collectable_mask(uint64_t uid) { return MaskOf(uid); }

extern "C" void refii_bridge_collectable_take(int32_t player, uint64_t uid) {
  char key[48];
  KeyName(uid, key, sizeof(key));
  refii_bridge_player_write(player, key, std::strlen(key), 1.0f, 0, 1);
}

extern "C" uint32_t refii_bridge_collectable_masks(uint32_t* out, uint32_t capacity) {
  State& s = Tables();
  std::unordered_map<uint32_t, uint64_t> keys;
  {
    std::lock_guard lock(s.mutex);
    keys = s.keys;
  }
  uint32_t written = 0;
  for (const auto& [address, uid] : keys) {
    const uint32_t mask = MaskOf(uid);
    if (mask == 0u) {
      continue;
    }
    if (written + 2u > capacity) {
      break;
    }
    out[written++] = address;
    out[written++] = mask;
  }
  return written / 2u;
}
