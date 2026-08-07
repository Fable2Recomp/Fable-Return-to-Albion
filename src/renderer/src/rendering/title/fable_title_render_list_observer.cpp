#include "runtime/refii_renderer/plume/fable/title/fable_title_render_list_observer.h"

namespace refii::renderer {

namespace {

uint32_t LoadGuestU32IfLikely(
    uint8_t* base,
    uint32_t guest_address,
    const FableTitleRenderListAddressCallbacks& callbacks) {
  if (base == nullptr || callbacks.is_likely_guest_dword_address == nullptr ||
      callbacks.load_guest_u32 == nullptr ||
      !callbacks.is_likely_guest_dword_address(guest_address)) {
    return 0;
  }
  return callbacks.load_guest_u32(base, guest_address);
}

uint32_t LoadObjectVtable(
    uint8_t* base,
    uint32_t object,
    const FableTitleRenderListAddressCallbacks& callbacks) {
  return object != 0 ? LoadGuestU32IfLikely(base, object, callbacks) : 0u;
}

}  // namespace

bool FableObserveTitleRenderListRoot(
    std::string_view symbol,
    const FableTitleRenderListHookArgs& args,
    uint8_t* base,
    const FableTitleRenderListAddressCallbacks& callbacks,
    FableTitleRenderListObserverState& state) {
  if (symbol == "sub_82231ED8") {
    ++state.dispatches;
    state.last_list = args.r6;
    state.last_flags = args.r5;
    const uint32_t object =
        LoadGuestU32IfLikely(base, args.r6 + 16u, callbacks);
    if (object != 0) {
      state.last_object = object;
      state.last_object_vtable = LoadObjectVtable(base, object, callbacks);
    }
    return true;
  }

  if (symbol == "sub_821A4ED0") {
    ++state.insert_callbacks;
    state.last_object = args.r3;
    state.last_object_vtable = LoadObjectVtable(base, args.r3, callbacks);
    state.last_list_owner = args.r4;
    const uint32_t owner = LoadGuestU32IfLikely(base, args.r4, callbacks);
    if (owner != 0) {
      state.last_list = owner + 0x2A0u;
    }
    if (base != nullptr && callbacks.is_likely_guest_dword_address != nullptr &&
        callbacks.load_guest_u32 != nullptr &&
        callbacks.is_likely_guest_dword_address(args.r3 + 84u)) {
      state.last_sort_key = callbacks.load_guest_u32(base, args.r3 + 84u);
    }
    return true;
  }

  if (symbol == "sub_8222C2B0") {
    ++state.sorted_inserts;
    state.last_list = args.r3;
    state.last_node = args.r4;
    if (base != nullptr && callbacks.is_likely_guest_dword_address != nullptr &&
        callbacks.load_guest_u32 != nullptr &&
        callbacks.is_likely_guest_dword_address(args.r4 + 24u)) {
      state.last_node_vtable = callbacks.load_guest_u32(base, args.r4);
      const uint32_t object = callbacks.load_guest_u32(base, args.r4 + 4u);
      if (object != 0) {
        ++state.inserted_nodes;
        state.last_object = object;
        state.last_object_vtable = LoadObjectVtable(base, object, callbacks);
      }
      state.last_sort_key = callbacks.load_guest_u32(base, args.r4 + 24u);
    }
    return true;
  }

  return false;
}

}  // namespace refii::renderer
