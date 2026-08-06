#pragma once

#include <cstdint>

namespace refii::renderer {

struct FableTitleWorldRegistryAddressCallbacks {
  bool (*is_likely_guest_dword_address)(uint32_t guest_address) = nullptr;
  bool (*is_likely_fable_object_address)(uint32_t guest_address) = nullptr;
  uint32_t (*load_guest_u32)(uint8_t* base, uint32_t guest_address) = nullptr;
};

struct FableTitleRenderableBucketSnapshot {
  bool valid = false;
  uint32_t entry = 0;
  uint32_t key = 0;
  uint32_t objects_begin = 0;
  uint32_t objects_end = 0;
  uint32_t object_count = 0;
  uint32_t first_object = 0;
  uint32_t first_object_vtable = 0;
};

struct FableTitleWorldRenderableRegistrySnapshot {
  bool valid = false;
  uint32_t scene = 0;
  uint32_t world = 0;
  uint32_t registry = 0;
  uint32_t begin = 0;
  uint32_t end = 0;
  uint32_t count = 0;
  uint32_t render_object = 0;
  uint32_t render_object_vtable = 0;
  uint32_t search_key = 0;
  FableTitleRenderableBucketSnapshot first_bucket;
  FableTitleRenderableBucketSnapshot matched_bucket;
};

FableTitleWorldRenderableRegistrySnapshot
FableReadTitleWorldRenderableRegistry(
    uint8_t* base,
    uint32_t world,
    const FableTitleWorldRegistryAddressCallbacks& callbacks);

FableTitleWorldRenderableRegistrySnapshot
FableReadTitleWorldRenderableRegistryForScene(
    uint8_t* base,
    uint32_t scene,
    const FableTitleWorldRegistryAddressCallbacks& callbacks);

FableTitleWorldRenderableRegistrySnapshot
FableReadTitleWorldRenderableRegistryForRenderCallback(
    uint8_t* base,
    uint32_t render_object,
    uint32_t world,
    const FableTitleWorldRegistryAddressCallbacks& callbacks);

}  // namespace refii::renderer
