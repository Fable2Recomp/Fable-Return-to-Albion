#include "runtime/refii_renderer/plume/fable/title/fable_title_scene_table.h"

#include <algorithm>

namespace refii::renderer {

namespace {

constexpr uint32_t kSceneTableBeginAddress = 0x8349F7ACu;
constexpr uint32_t kSceneTableEndAddress = 0x8349F7B0u;
constexpr uint32_t kSceneTableCapacityEndAddress = 0x8349F7B4u;
constexpr uint32_t kMaxSceneTableScanCount = 512u;

bool CanReadGuestU32(const FableTitleSceneTableAddressCallbacks& callbacks) {
  return callbacks.load_guest_u32 != nullptr;
}

bool IsLikelyGuestDwordAddress(
    uint32_t address,
    const FableTitleSceneTableAddressCallbacks& callbacks) {
  return callbacks.is_likely_guest_dword_address != nullptr &&
         callbacks.is_likely_guest_dword_address(address);
}

bool IsLikelyFableObjectAddress(
    uint32_t address,
    const FableTitleSceneTableAddressCallbacks& callbacks) {
  return callbacks.is_likely_fable_object_address != nullptr &&
         callbacks.is_likely_fable_object_address(address);
}

}  // namespace

FableTitleSceneTableSnapshot FableReadTitleSceneTable(
    uint8_t* base,
    const FableTitleSceneTableAddressCallbacks& callbacks) {
  FableTitleSceneTableSnapshot snapshot;
  if (base == nullptr || !CanReadGuestU32(callbacks)) {
    return snapshot;
  }

  snapshot.begin = callbacks.load_guest_u32(base, kSceneTableBeginAddress);
  snapshot.end = callbacks.load_guest_u32(base, kSceneTableEndAddress);
  snapshot.capacity_end =
      callbacks.load_guest_u32(base, kSceneTableCapacityEndAddress);
  snapshot.count =
      snapshot.end >= snapshot.begin ? (snapshot.end - snapshot.begin) / 4u
                                     : 0u;
  snapshot.capacity = snapshot.capacity_end >= snapshot.begin
                          ? (snapshot.capacity_end - snapshot.begin) / 4u
                          : 0u;
  return snapshot;
}

uint32_t FableFindTitleSceneIndexForObject(
    uint8_t* base,
    uint32_t scene,
    const FableTitleSceneTableAddressCallbacks& callbacks) {
  const FableTitleSceneTableSnapshot table =
      FableReadTitleSceneTable(base, callbacks);
  if (scene == 0 || table.count == 0 ||
      !IsLikelyGuestDwordAddress(table.begin, callbacks) ||
      !CanReadGuestU32(callbacks)) {
    return UINT32_MAX;
  }

  const uint32_t capped_count =
      std::min<uint32_t>(table.count, kMaxSceneTableScanCount);
  for (uint32_t index = 0; index < capped_count; ++index) {
    if (callbacks.load_guest_u32(base, table.begin + index * 4u) == scene) {
      return index;
    }
  }
  return UINT32_MAX;
}

FableTitleSceneSlotSnapshot FableReadTitleSceneSlot(
    uint8_t* base,
    uint32_t index,
    const FableTitleSceneTableAddressCallbacks& callbacks) {
  FableTitleSceneSlotSnapshot slot;
  const FableTitleSceneTableSnapshot table =
      FableReadTitleSceneTable(base, callbacks);
  if (index == UINT32_MAX || index >= table.count ||
      !IsLikelyGuestDwordAddress(table.begin, callbacks) ||
      !CanReadGuestU32(callbacks)) {
    return slot;
  }

  slot.valid_index = true;
  slot.scene = callbacks.load_guest_u32(base, table.begin + index * 4u);
  if (!IsLikelyFableObjectAddress(slot.scene, callbacks)) {
    return slot;
  }

  slot.scene_type = callbacks.load_guest_u32(base, slot.scene + 4u);
  if (callbacks.load_guest_u8 != nullptr) {
    slot.scene_enabled = callbacks.load_guest_u8(base, slot.scene + 72u);
  }
  slot.scene_world = callbacks.load_guest_u32(base, slot.scene + 20u);
  return slot;
}

}  // namespace refii::renderer
