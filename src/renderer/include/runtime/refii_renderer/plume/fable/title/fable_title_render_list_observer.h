#pragma once

#include <cstdint>
#include <string_view>

namespace refii::renderer {

struct FableTitleRenderListAddressCallbacks {
  bool (*is_likely_guest_dword_address)(uint32_t guest_address) = nullptr;
  uint32_t (*load_guest_u32)(uint8_t* base, uint32_t guest_address) = nullptr;
};

struct FableTitleRenderListHookArgs {
  uint32_t r3 = 0;
  uint32_t r4 = 0;
  uint32_t r5 = 0;
  uint32_t r6 = 0;
};

struct FableTitleRenderListObserverState {
  uint64_t dispatches = 0;
  uint64_t insert_callbacks = 0;
  uint64_t sorted_inserts = 0;
  uint64_t inserted_nodes = 0;
  uint32_t last_list = 0;
  uint32_t last_list_owner = 0;
  uint32_t last_object = 0;
  uint32_t last_object_vtable = 0;
  uint32_t last_node = 0;
  uint32_t last_node_vtable = 0;
  uint32_t last_sort_key = 0;
  uint32_t last_flags = 0;
};

bool FableObserveTitleRenderListRoot(
    std::string_view symbol,
    const FableTitleRenderListHookArgs& args,
    uint8_t* base,
    const FableTitleRenderListAddressCallbacks& callbacks,
    FableTitleRenderListObserverState& state);

}  // namespace refii::renderer
