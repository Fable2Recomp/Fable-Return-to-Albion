// refii — the appearance a title states (core/payload_bridge.h): each character part's morph
// targets and each original texture's layers, keyed by the entity.
//
// Both cross from a title's hooks (the payload, on the guest's threads) to its host bridge (on
// Godot's), which live in different modules; this is the one table both link. Neither side knows
// a title: a hook states the lists the guest's own draw virtual built, the bridge shapes them for
// the shell in Fable 2's rows.
//
// THE REBUILD IS STAGED AND PUBLISHED WHOLE. The guest clears and re-states a character's layers
// inside one call of its draw virtual, texture by texture; the host reads this table on its own
// thread at any instant. A live record replaced statement by statement let a read land on a
// half-filled list with a fresh generation and compose the character undyed for a reconcile
// (Fable 2, texture_layer_state.cpp). So statements accumulate in a staging record from `begin`
// and replace the live one in a single step at `end`.

#include <cstdint>
#include <cstring>
#include <mutex>

#include "core/payload_bridge.h"

namespace {

// --- Part morphs: per entity, per base model, the stated {target, weight} pairs. ------------

constexpr uint32_t kMorphSlots = 1024u;
constexpr uint32_t kProbes = 32u;

struct PartEntry {
  uint32_t base = 0u;
  uint32_t count = 0u;
  uint64_t stamp = 0u;
  uint32_t pairs[REFII_PART_MORPH_TARGETS * 2u] = {};
};

struct EntityParts {
  uint32_t entity = 0u;
  uint32_t generation = 0u;
  PartEntry parts[REFII_PART_MORPH_PARTS];
};

std::mutex g_morph_lock;
EntityParts* g_morphs = nullptr;
uint64_t g_morph_statements = 0u;
uint64_t g_morph_morphed = 0u;
uint64_t g_morph_stamp = 0u;

EntityParts* MorphRecords() {
  if (g_morphs == nullptr) {
    g_morphs = new EntityParts[kMorphSlots]();
  }
  return g_morphs;
}

uint32_t SlotFor(uint32_t entity, uint32_t slots) {
  return ((entity * 2654435761u) >> 12) & (slots - 1u);
}

EntityParts* FindMorphs(uint32_t entity) {
  EntityParts* records = MorphRecords();
  const uint32_t start = SlotFor(entity, kMorphSlots);
  for (uint32_t probe = 0u; probe < kProbes; ++probe) {
    EntityParts& record = records[(start + probe) & (kMorphSlots - 1u)];
    if (record.entity == entity) {
      return &record;
    }
  }
  return nullptr;
}

bool Empty(const EntityParts& record) {
  for (const PartEntry& part : record.parts) {
    if (part.count != 0u) {
      return false;
    }
  }
  return true;
}

EntityParts* ClaimMorphs(uint32_t entity) {
  if (EntityParts* found = FindMorphs(entity); found != nullptr) {
    return found;
  }
  EntityParts* records = MorphRecords();
  const uint32_t start = SlotFor(entity, kMorphSlots);
  for (uint32_t probe = 0u; probe < kProbes; ++probe) {
    EntityParts& record = records[(start + probe) & (kMorphSlots - 1u)];
    if (record.entity == 0u || Empty(record)) {
      const uint32_t generation = record.generation;
      record = EntityParts{};
      record.entity = entity;
      record.generation = generation;
      return &record;
    }
  }
  return nullptr;
}

// --- Texture layers: per entity, per original texture and kind, the stated layers. ----------

constexpr uint32_t kLayerSlots = 512u;

struct Composite {
  uint32_t original = 0u;
  uint32_t kind = 0u;
  uint32_t count = 0u;
  uint32_t layers[REFII_TEXTURE_LAYERS_PER_TEXTURE * REFII_TEXTURE_LAYER_WORDS] = {};
};

struct EntityLayers {
  uint32_t entity = 0u;
  uint32_t generation = 0u;
  uint32_t used = 0u;
  Composite textures[REFII_TEXTURE_LAYER_TEXTURES];
};

std::mutex g_layer_lock;
EntityLayers* g_layers = nullptr;
uint64_t g_layer_statements = 0u;
uint64_t g_layer_dropped = 0u;
EntityLayers g_staging;
bool g_staging_active = false;

EntityLayers* LayerRecords() {
  if (g_layers == nullptr) {
    g_layers = new EntityLayers[kLayerSlots]();
  }
  return g_layers;
}

EntityLayers* FindLayers(uint32_t entity) {
  EntityLayers* records = LayerRecords();
  const uint32_t start = SlotFor(entity, kLayerSlots);
  for (uint32_t probe = 0u; probe < kProbes; ++probe) {
    EntityLayers& record = records[(start + probe) & (kLayerSlots - 1u)];
    if (record.entity == entity) {
      return &record;
    }
  }
  return nullptr;
}

EntityLayers* ClaimLayers(uint32_t entity) {
  if (EntityLayers* found = FindLayers(entity); found != nullptr) {
    return found;
  }
  EntityLayers* records = LayerRecords();
  const uint32_t start = SlotFor(entity, kLayerSlots);
  for (uint32_t probe = 0u; probe < kProbes; ++probe) {
    EntityLayers& record = records[(start + probe) & (kLayerSlots - 1u)];
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

/// Adds or replaces the composite for (original, kind). Caller holds g_layer_lock.
void Append(EntityLayers& record, uint32_t original, uint32_t kind, const uint32_t* layers,
            uint32_t count, bool* changed) {
  Composite* texture = nullptr;
  for (uint32_t i = 0u; i < record.used; ++i) {
    if (record.textures[i].original == original && record.textures[i].kind == kind) {
      texture = &record.textures[i];
      break;
    }
  }

  const size_t bytes = sizeof(uint32_t) * count * REFII_TEXTURE_LAYER_WORDS;
  if (texture != nullptr && texture->count == count &&
      std::memcmp(texture->layers, layers, bytes) == 0) {
    return;
  }

  if (texture == nullptr) {
    if (record.used >= REFII_TEXTURE_LAYER_TEXTURES) {
      ++g_layer_dropped;
      return;
    }
    texture = &record.textures[record.used++];
  }

  texture->original = original;
  texture->kind = kind;
  texture->count = count;
  std::memcpy(texture->layers, layers, bytes);
  *changed = true;
}

bool SameContent(const EntityLayers& a, const EntityLayers& b) {
  if (a.used != b.used) {
    return false;
  }
  for (uint32_t i = 0u; i < a.used; ++i) {
    const Composite& x = a.textures[i];
    const Composite& y = b.textures[i];
    if (x.original != y.original || x.kind != y.kind || x.count != y.count ||
        std::memcmp(x.layers, y.layers, sizeof(uint32_t) * x.count * REFII_TEXTURE_LAYER_WORDS) != 0) {
      return false;
    }
  }
  return true;
}

/// Replaces the live record with the staged one in one step; the generation moves only when the
/// content did. Caller holds g_layer_lock.
void Commit() {
  if (!g_staging_active) {
    return;
  }
  g_staging_active = false;
  EntityLayers* live = ClaimLayers(g_staging.entity);
  if (live == nullptr) {
    ++g_layer_dropped;
    return;
  }
  if (SameContent(*live, g_staging)) {
    return;
  }
  live->used = g_staging.used;
  std::memcpy(live->textures, g_staging.textures, sizeof(Composite) * g_staging.used);
  ++live->generation;
}

}  // namespace

extern "C" {

void refii_bridge_part_morph_stated(uint32_t entity, uint32_t base_model, const uint32_t* pairs,
                                    uint32_t count) {
  if (entity == 0u || base_model == 0u) {
    return;
  }

  std::lock_guard<std::mutex> guard(g_morph_lock);
  ++g_morph_statements;
  if (count != 0u) {
    ++g_morph_morphed;
  }

  EntityParts* record = count != 0u ? ClaimMorphs(entity) : FindMorphs(entity);
  if (record == nullptr) {
    return;
  }

  PartEntry* entry = nullptr;
  PartEntry* spare = nullptr;
  PartEntry* oldest = &record->parts[0];
  for (PartEntry& part : record->parts) {
    if (part.count != 0u && part.base == base_model) {
      entry = &part;
      break;
    }
    if (part.count == 0u && spare == nullptr) {
      spare = &part;
    }
    if (part.stamp < oldest->stamp) {
      oldest = &part;
    }
  }

  if (count == 0u) {
    if (entry != nullptr) {
      entry->count = 0u;
      ++record->generation;
    }
    return;
  }

  if (count > REFII_PART_MORPH_TARGETS) {
    count = REFII_PART_MORPH_TARGETS;
  }

  const size_t bytes = sizeof(uint32_t) * count * 2u;
  if (entry != nullptr && entry->count == count && std::memcmp(entry->pairs, pairs, bytes) == 0) {
    entry->stamp = ++g_morph_stamp;
    return;
  }
  if (entry == nullptr) {
    entry = spare != nullptr ? spare : oldest;
  }

  entry->base = base_model;
  entry->count = count;
  entry->stamp = ++g_morph_stamp;
  std::memcpy(entry->pairs, pairs, bytes);
  ++record->generation;
}

uint32_t refii_bridge_part_morphs_read(uint32_t entity, uint32_t* out, uint32_t capacity,
                                       uint32_t* out_generation, uint64_t* out_statements) {
  std::lock_guard<std::mutex> guard(g_morph_lock);
  if (out_statements != nullptr) {
    *out_statements = (g_morph_statements & 0xFFFFFFFFull) | (g_morph_morphed << 32);
  }
  if (out_generation != nullptr) {
    *out_generation = 0u;
  }
  if (entity == 0u || out == nullptr) {
    return 0u;
  }
  const EntityParts* record = FindMorphs(entity);
  if (record == nullptr) {
    return 0u;
  }
  if (out_generation != nullptr) {
    *out_generation = record->generation;
  }

  uint32_t written = 0u;
  for (const PartEntry& part : record->parts) {
    if (part.count == 0u) {
      continue;
    }
    const uint32_t needed = 2u + part.count * 2u;
    if (written + needed > capacity) {
      break;
    }
    out[written++] = part.base;
    out[written++] = part.count;
    std::memcpy(out + written, part.pairs, sizeof(uint32_t) * part.count * 2u);
    written += part.count * 2u;
  }
  return written;
}

void refii_bridge_texture_layers_begin(uint32_t entity) {
  std::lock_guard<std::mutex> guard(g_layer_lock);
  if (g_staging_active && g_staging.entity != entity) {
    Commit();  // a rebuild that never ended: publish what it stated
  }
  g_staging.entity = entity;
  g_staging.used = 0u;
  g_staging_active = true;
}

void refii_bridge_texture_layers_end(uint32_t entity) {
  std::lock_guard<std::mutex> guard(g_layer_lock);
  if (g_staging_active && g_staging.entity == entity) {
    Commit();
  }
}

void refii_bridge_texture_layers_stated(uint32_t entity, uint32_t original, uint32_t kind,
                                        const uint32_t* layers, uint32_t count) {
  if (entity == 0u || original == 0u || layers == nullptr || count == 0u) {
    return;
  }

  std::lock_guard<std::mutex> guard(g_layer_lock);
  ++g_layer_statements;

  if (count > REFII_TEXTURE_LAYERS_PER_TEXTURE) {
    count = REFII_TEXTURE_LAYERS_PER_TEXTURE;
    ++g_layer_dropped;
  }

  bool changed = false;
  if (g_staging_active && g_staging.entity == entity) {
    Append(g_staging, original, kind, layers, count, &changed);
    return;
  }

  // A statement outside a staged rebuild lands on the live record directly.
  EntityLayers* record = ClaimLayers(entity);
  if (record == nullptr) {
    ++g_layer_dropped;
    return;
  }
  Append(*record, original, kind, layers, count, &changed);
  if (changed) {
    ++record->generation;
  }
}

uint32_t refii_bridge_texture_layers_read(uint32_t entity, uint32_t* out, uint32_t capacity,
                                          uint32_t kind_mask, uint32_t* out_generation,
                                          uint64_t* out_statements) {
  std::lock_guard<std::mutex> guard(g_layer_lock);
  if (out_statements != nullptr) {
    *out_statements = g_layer_statements;
  }
  if (out_generation != nullptr) {
    *out_generation = 0u;
  }
  if (entity == 0u || out == nullptr) {
    return 0u;
  }
  const EntityLayers* record = FindLayers(entity);
  if (record == nullptr) {
    return 0u;
  }
  if (out_generation != nullptr) {
    *out_generation = record->generation;
  }

  uint32_t written = 0u;
  for (uint32_t i = 0u; i < record->used; ++i) {
    const Composite& texture = record->textures[i];
    if (texture.kind >= 32u || ((kind_mask >> texture.kind) & 1u) == 0u) {
      continue;
    }
    const uint32_t needed = 2u + texture.count * REFII_TEXTURE_LAYER_WORDS;
    if (written + needed > capacity) {
      break;
    }
    out[written++] = texture.original;
    out[written++] = texture.count | (texture.kind << 16);
    std::memcpy(out + written, texture.layers,
                sizeof(uint32_t) * texture.count * REFII_TEXTURE_LAYER_WORDS);
    written += texture.count * REFII_TEXTURE_LAYER_WORDS;
  }
  return written;
}

}  // extern "C"
