// refii — the texture layers the guest's own dye builder states for ONE inventory item, as
// SHARED STATE.
//
// In librefii_vfs for the reason entity_appearance_state.cpp gives: the PAYLOAD writes (the
// GUI-tick sampler in Fable2/src/vfs/src/hooks/guest_tick_pump.cpp, on the guest's thread) and the HOST reads
// (the host bridge, on Godot's).
//
// One record per dyed inventory item (worn or carried), keyed by the item's record hash, holding
// the composites in the same words a character's texture layers use — so the menu's preview of
// a dyed garment goes through the very compositor that dresses the hero.

#include <cstdint>
#include <cstring>
#include <mutex>

#include "core/payload_bridge_fable2.h"

namespace refii::vfs {
namespace {

struct ItemLayers {
  uint32_t record = 0u;
  uint32_t generation = 0u;
  uint32_t count = 0u;
  uint32_t words[kItemDyeLayerWords] = {};
};

std::mutex g_lock;
ItemLayers g_items[kItemDyeLayerItems];
uint32_t g_used = 0u;

ItemLayers* Find(uint32_t record) {
  for (uint32_t i = 0u; i < g_used; ++i) {
    if (g_items[i].record == record) {
      return &g_items[i];
    }
  }
  return nullptr;
}

}  // namespace

/// Copies the item's composites into `out` ([original, count | kind << 16, count x
/// kTextureLayerWords] per texture) and returns the words written; 0 for an item the sampler
/// found undyed or never saw. `out_generation` moves when the item's layers changed.
uint32_t StatedItemDyeLayers(uint32_t record, uint32_t* out, uint32_t capacity,
                             uint32_t* out_generation) {
  std::lock_guard<std::mutex> guard(g_lock);
  if (out_generation != nullptr) {
    *out_generation = 0u;
  }
  if (record == 0u || out == nullptr) {
    return 0u;
  }
  const ItemLayers* item = Find(record);
  if (item == nullptr) {
    return 0u;
  }
  if (out_generation != nullptr) {
    *out_generation = item->generation;
  }
  const uint32_t count = item->count < capacity ? item->count : capacity;
  std::memcpy(out, item->words, sizeof(uint32_t) * count);
  return count;
}

}  // namespace refii::vfs

extern "C" {

// The sampler's whole answer for one sweep: `records` with each item's words at
// `offsets[i]..offsets[i] + counts[i]`. Items not in the sweep are dropped (they are no longer
// dyed, or no longer owned).
void refii_bridge_set_item_dye_layers(const uint32_t* records, const uint32_t* offsets,
                                      const uint32_t* counts, uint32_t items,
                                      const uint32_t* words) {
  using namespace refii::vfs;
  std::lock_guard<std::mutex> guard(g_lock);
  if (records == nullptr || offsets == nullptr || counts == nullptr || words == nullptr) {
    items = 0u;
  }
  if (items > static_cast<uint32_t>(kItemDyeLayerItems)) {
    items = static_cast<uint32_t>(kItemDyeLayerItems);
  }

  ItemLayers next[kItemDyeLayerItems];
  uint32_t used = 0u;
  for (uint32_t i = 0u; i < items; ++i) {
    const uint32_t record = records[i];
    uint32_t count = counts[i];
    if (record == 0u || count == 0u) {
      continue;
    }
    if (count > static_cast<uint32_t>(kItemDyeLayerWords)) {
      count = static_cast<uint32_t>(kItemDyeLayerWords);
    }
    ItemLayers& item = next[used++];
    item.record = record;
    item.count = count;
    std::memcpy(item.words, words + offsets[i], sizeof(uint32_t) * count);

    // The generation carries over and moves only when the content did, so a reader that keyed
    // its composite on it rebuilds nothing for a sweep that said the same thing again.
    const ItemLayers* previous = Find(record);
    if (previous != nullptr) {
      item.generation = previous->generation;
      if (previous->count != count ||
          std::memcmp(previous->words, item.words, sizeof(uint32_t) * count) != 0) {
        ++item.generation;
      }
    } else {
      item.generation = 1u;
    }
  }

  std::memcpy(g_items, next, sizeof(ItemLayers) * used);
  g_used = used;
}

}  // extern "C"
