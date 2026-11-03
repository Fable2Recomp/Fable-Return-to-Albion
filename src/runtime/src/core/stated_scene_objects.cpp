// refii — the GUI scenes a title states (core/payload_bridge.h): a scene the guest presents IN
// PLACE OF the world (Fable 3's map tables), the camera it is presented through, and the mesh
// render objects its hooks see added to it, with their transforms as the guest's per-object
// commands move them.
//
// Written from a title's hooks (the payload, on the guest's threads), read by its host bridge (on
// Godot's): the one table both link, as stated_world.cpp is for the world. Neither side knows a
// title: a holder is an opaque key, a row is the words the hook took from the guest's own message
// or command.

#include <atomic>
#include <cstdint>
#include <cstring>
#include <iterator>
#include <mutex>
#include <unordered_map>

#include "core/payload_bridge.h"

namespace {

struct SceneObject {
  uint32_t holder = 0u;
  uint32_t words[REFII_SCENE_OBJECT_WORDS] = {};
};

struct SceneHolder {
  uint32_t holder = 0u;
  int32_t presented = 0;
  uint32_t camera[REFII_SCENE_CAMERA_WORDS] = {};
  int32_t camera_stated = 0;
};

/// Few at once: Fable 3 stands at most its world map's and its town map's.
constexpr uint32_t kHolders = 8u;

std::mutex g_scene_lock;
std::unordered_map<uint32_t, SceneObject>& Objects() {
  static std::unordered_map<uint32_t, SceneObject> objects;
  return objects;
}
SceneHolder g_holders[kHolders];
/// The holder presented last, 0 while none is.
uint32_t g_presented = 0u;
uint32_t g_scene_serial = 0u;

/// The holder's slot, made when `make`; nullptr when full or absent. Holds g_scene_lock.
SceneHolder* HolderSlot(uint32_t holder, bool make) {
  SceneHolder* free_slot = nullptr;
  for (SceneHolder& slot : g_holders) {
    if (slot.holder == holder) {
      return &slot;
    }
    if (free_slot == nullptr && slot.holder == 0u) {
      free_slot = &slot;
    }
  }
  if (!make || free_slot == nullptr) {
    return nullptr;
  }
  *free_slot = SceneHolder{};
  free_slot->holder = holder;
  return free_slot;
}

}  // namespace

extern "C" {

void refii_bridge_scene_object_stated(uint32_t holder, uint32_t id, const uint32_t* words) {
  if (holder == 0u || words == nullptr || id == 0xFFFFFFFFu) {
    return;
  }
  std::lock_guard<std::mutex> lock(g_scene_lock);
  SceneObject& object = Objects()[id];
  object.holder = holder;
  std::memcpy(object.words, words, sizeof(object.words));
  ++g_scene_serial;
}

void refii_bridge_scene_object_patch(uint32_t id, uint32_t first, const uint32_t* words, uint32_t count) {
  if (words == nullptr || first >= REFII_SCENE_OBJECT_WORDS || count > REFII_SCENE_OBJECT_WORDS - first) {
    return;
  }
  std::lock_guard<std::mutex> lock(g_scene_lock);
  auto found = Objects().find(id);
  if (found == Objects().end()) {
    return;
  }
  std::memcpy(found->second.words + first, words, count * sizeof(uint32_t));
}

void refii_bridge_scene_object_removed(uint32_t id) {
  std::lock_guard<std::mutex> lock(g_scene_lock);
  if (Objects().erase(id) != 0u) {
    ++g_scene_serial;
  }
}

void refii_bridge_scene_holder_dropped(uint32_t holder) {
  std::lock_guard<std::mutex> lock(g_scene_lock);
  for (auto it = Objects().begin(); it != Objects().end();) {
    it = it->second.holder == holder ? Objects().erase(it) : std::next(it);
  }
  if (SceneHolder* slot = HolderSlot(holder, false)) {
    *slot = SceneHolder{};
  }
  if (g_presented == holder) {
    g_presented = 0u;
  }
  ++g_scene_serial;
}

void refii_bridge_scene_camera_stated(uint32_t holder, const uint32_t* words) {
  if (holder == 0u || words == nullptr) {
    return;
  }
  std::lock_guard<std::mutex> lock(g_scene_lock);
  if (SceneHolder* slot = HolderSlot(holder, true)) {
    std::memcpy(slot->camera, words, sizeof(slot->camera));
    slot->camera_stated = 1;
  }
}

void refii_bridge_scene_presented(uint32_t holder, int32_t on) {
  if (holder == 0u) {
    return;
  }
  std::lock_guard<std::mutex> lock(g_scene_lock);
  if (SceneHolder* slot = HolderSlot(holder, true)) {
    slot->presented = on != 0 ? 1 : 0;
  }
  if (on != 0) {
    g_presented = holder;
  } else if (g_presented == holder) {
    g_presented = 0u;
  }
  ++g_scene_serial;
}

uint32_t refii_bridge_scene_presented_read(uint32_t* out_camera_words, int32_t* out_camera_stated) {
  std::lock_guard<std::mutex> lock(g_scene_lock);
  const SceneHolder* slot = g_presented == 0u ? nullptr : HolderSlot(g_presented, false);
  if (out_camera_stated != nullptr) {
    *out_camera_stated = slot != nullptr ? slot->camera_stated : 0;
  }
  if (out_camera_words != nullptr) {
    if (slot != nullptr) {
      std::memcpy(out_camera_words, slot->camera, sizeof(slot->camera));
    } else {
      std::memset(out_camera_words, 0, REFII_SCENE_CAMERA_WORDS * sizeof(uint32_t));
    }
  }
  return slot != nullptr ? g_presented : 0u;
}

uint32_t refii_bridge_scene_objects_read(uint32_t holder, uint32_t* out, uint32_t capacity, uint32_t* out_serial) {
  constexpr uint32_t kRow = 1u + REFII_SCENE_OBJECT_WORDS;
  std::lock_guard<std::mutex> lock(g_scene_lock);
  if (out_serial != nullptr) {
    *out_serial = g_scene_serial;
  }
  uint32_t count = 0u;
  for (const auto& [id, object] : Objects()) {
    if (object.holder != holder) {
      continue;
    }
    if (out == nullptr || (count + 1u) * kRow > capacity) {
      break;
    }
    uint32_t* row = out + count * kRow;
    row[0] = id;
    std::memcpy(row + 1, object.words, sizeof(object.words));
    ++count;
  }
  return count;
}

}  // extern "C"
