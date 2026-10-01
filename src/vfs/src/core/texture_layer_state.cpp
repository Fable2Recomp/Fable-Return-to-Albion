// refii — each character's stated TEXTURE LAYERS, as SHARED STATE.
//
// In librefii_vfs for the reason entity_appearance_state.cpp gives: the PAYLOAD writes
// (src/hooks/texture_layers.cpp, on the guest's thread) and the HOST reads (the host bridge, on
// Godot's).
//
// One record per entity, one entry per ORIGINAL texture the guest composites for it: the layer
// list it hands `sub_825B38B0`, already in draw order. Layer 0 is the base. Each layer is
// kTextureLayerWords words, see core/payload_bridge.h.

#include <cstdint>
#include <cstring>
#include <mutex>

#include "core/payload_bridge.h"

namespace refii::vfs {
namespace {

constexpr uint32_t kSlots = 512u;
constexpr uint32_t kProbes = 32u;

struct Composite {
  uint32_t original = 0u;
  uint32_t kind = 0u;
  uint32_t count = 0u;
  uint32_t layers[kTextureLayersPerTexture * kTextureLayerWords] = {};
};

struct EntityLayers {
  uint32_t entity = 0u;
  uint32_t generation = 0u;
  uint32_t used = 0u;
  Composite textures[kTextureLayerTextures];
};

std::mutex g_lock;
EntityLayers* g_records = nullptr;
uint64_t g_statements = 0u;
uint64_t g_dropped = 0u;

EntityLayers* Records() {
  if (g_records == nullptr) {
    g_records = new EntityLayers[kSlots]();
  }
  return g_records;
}

uint32_t SlotFor(uint32_t entity) {
  const uint32_t h = entity * 2654435761u;
  return (h >> 12) & (kSlots - 1u);
}

EntityLayers* Find(uint32_t entity) {
  EntityLayers* records = Records();
  const uint32_t start = SlotFor(entity);
  for (uint32_t probe = 0u; probe < kProbes; ++probe) {
    EntityLayers& record = records[(start + probe) & (kSlots - 1u)];
    if (record.entity == entity) {
      return &record;
    }
  }
  return nullptr;
}

EntityLayers* Claim(uint32_t entity) {
  if (EntityLayers* found = Find(entity); found != nullptr) {
    return found;
  }
  EntityLayers* records = Records();
  const uint32_t start = SlotFor(entity);
  for (uint32_t probe = 0u; probe < kProbes; ++probe) {
    EntityLayers& record = records[(start + probe) & (kSlots - 1u)];
    if (record.entity == 0u || record.used == 0u) {
      const uint32_t generation = record.generation;
      record.entity = entity;
      record.used = 0u;
      record.generation = generation;
      return &record;
    }
  }
  return nullptr;
}

}  // namespace

/// Copies the entity's composites into `out` as [original, count | kind << 16, count x
/// kTextureLayerWords] per texture and returns the words written. `out_generation` moves when a statement changed
/// the record; `out_statements` is the run total, 0 when the hook never ran.
uint32_t StatedTextureLayers(uint32_t entity, uint32_t* out, uint32_t capacity,
                             uint32_t* out_generation, uint64_t* out_statements) {
  std::lock_guard<std::mutex> guard(g_lock);
  if (out_statements != nullptr) {
    *out_statements = g_statements;
  }
  if (out_generation != nullptr) {
    *out_generation = 0u;
  }
  if (entity == 0u || out == nullptr) {
    return 0u;
  }
  const EntityLayers* record = Find(entity);
  if (record == nullptr) {
    return 0u;
  }
  if (out_generation != nullptr) {
    *out_generation = record->generation;
  }

  uint32_t written = 0u;
  for (uint32_t i = 0u; i < record->used; ++i) {
    const Composite& texture = record->textures[i];
    const uint32_t needed = 2u + texture.count * kTextureLayerWords;
    if (written + needed > capacity) {
      break;
    }
    out[written++] = texture.original;
    out[written++] = texture.count | (texture.kind << 16);
    std::memcpy(out + written, texture.layers,
                sizeof(uint32_t) * texture.count * kTextureLayerWords);
    written += texture.count * kTextureLayerWords;
  }
  return written;
}
}  // namespace refii::vfs

extern "C" {
void refii_bridge_begin_texture_layers(uint32_t entity) {
  using namespace refii::vfs;
  std::lock_guard<std::mutex> guard(g_lock);
  if (EntityLayers* record = Find(entity); record != nullptr && record->used != 0u) {
    record->used = 0u;
    ++record->generation;

  }
}

void refii_bridge_note_texture_layers(uint32_t entity, uint32_t original, uint32_t kind,
                                      const uint32_t* layers, uint32_t count) {
  using namespace refii::vfs;
  if (entity == 0u || original == 0u || layers == nullptr || count == 0u) {
    return;
  }

  std::lock_guard<std::mutex> guard(g_lock);
  ++g_statements;

  EntityLayers* record = Claim(entity);
  if (record == nullptr) {
    ++g_dropped;
    return;
  }

  if (count > kTextureLayersPerTexture) {
    count = kTextureLayersPerTexture;
    ++g_dropped;
  }

  Composite* texture = nullptr;
  for (uint32_t i = 0u; i < record->used; ++i) {
    if (record->textures[i].original == original && record->textures[i].kind == kind) {
      texture = &record->textures[i];
      break;
    }
  }

  const size_t bytes = sizeof(uint32_t) * count * kTextureLayerWords;
  if (texture != nullptr && texture->count == count &&
      std::memcmp(texture->layers, layers, bytes) == 0) {
    return;
  }

  if (texture == nullptr) {
    if (record->used >= kTextureLayerTextures) {
      ++g_dropped;
      return;
    }
    texture = &record->textures[record->used++];
  }

  texture->original = original;
  texture->kind = kind;
  texture->count = count;
  std::memcpy(texture->layers, layers, bytes);
  ++record->generation;
}
}  // extern "C"
