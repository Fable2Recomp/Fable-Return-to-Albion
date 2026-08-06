#pragma once

#include <cstdint>

namespace refii::renderer {

struct FableTitleSceneTableAddressCallbacks {
  bool (*is_likely_guest_dword_address)(uint32_t guest_address) = nullptr;
  bool (*is_likely_fable_object_address)(uint32_t guest_address) = nullptr;
  uint32_t (*load_guest_u32)(uint8_t* base, uint32_t guest_address) = nullptr;
  uint8_t (*load_guest_u8)(uint8_t* base, uint32_t guest_address) = nullptr;
};

struct FableTitleSceneTableSnapshot {
  uint32_t begin = 0;
  uint32_t end = 0;
  uint32_t capacity_end = 0;
  uint32_t count = 0;
  uint32_t capacity = 0;
};

struct FableTitleSceneSlotSnapshot {
  bool valid_index = false;
  uint32_t scene = 0;
  uint32_t scene_type = UINT32_MAX;
  uint8_t scene_enabled = 0;
  uint32_t scene_world = 0;
};

FableTitleSceneTableSnapshot FableReadTitleSceneTable(
    uint8_t* base,
    const FableTitleSceneTableAddressCallbacks& callbacks);

uint32_t FableFindTitleSceneIndexForObject(
    uint8_t* base,
    uint32_t scene,
    const FableTitleSceneTableAddressCallbacks& callbacks);

FableTitleSceneSlotSnapshot FableReadTitleSceneSlot(
    uint8_t* base,
    uint32_t index,
    const FableTitleSceneTableAddressCallbacks& callbacks);

}  // namespace refii::renderer
