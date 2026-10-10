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

namespace refii::vfs {
namespace {

// THE REBUILD IS STAGED AND PUBLISHED WHOLE. The guest clears and re-states a character's
// layers inside one call of its draw virtual, texture by texture; the host reads this table on
// its own thread at any instant. Clearing the live record at the first statement let a read
// land on an empty or half-filled list with a fresh generation, and the host composed the
// character UNDYED from it — the garment went back to its default colour on every rebuild
// (drawing a weapon, an interaction) and came back dyed one reconcile later, once the read
// caught the finished list. So statements accumulate here and replace the live record in one
// step when the virtual returns (`refii_bridge_end_texture_layers`).
EntityLayers g_staging;
bool g_staging_active = false;

void Append(EntityLayers& record, uint32_t original, uint32_t kind, const uint32_t* layers,
            uint32_t count, bool* changed) {
  Composite* texture = nullptr;
  for (uint32_t i = 0u; i < record.used; ++i) {
    if (record.textures[i].original == original && record.textures[i].kind == kind) {
      texture = &record.textures[i];
      break;
    }
  }

  const size_t bytes = sizeof(uint32_t) * count * kTextureLayerWords;
  if (texture != nullptr && texture->count == count &&
      std::memcmp(texture->layers, layers, bytes) == 0) {
    return;
  }

  if (texture == nullptr) {
    if (record.used >= kTextureLayerTextures) {
      ++g_dropped;
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
        std::memcmp(x.layers, y.layers, sizeof(uint32_t) * x.count * kTextureLayerWords) != 0) {
      return false;
    }
  }
  return true;
}

// Replaces the live record with the staged one, in one step, bumping the generation only when
// the content moved. Called with the lock held.
void Commit() {
  if (!g_staging_active) {
    return;
  }
  g_staging_active = false;
  EntityLayers* live = Claim(g_staging.entity);
  if (live == nullptr) {
    ++g_dropped;
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
}  // namespace refii::vfs

extern "C" {

void refii_bridge_begin_texture_layers(uint32_t entity) {
  using namespace refii::vfs;
  std::lock_guard<std::mutex> guard(g_lock);
  if (g_staging_active && g_staging.entity != entity) {
    Commit();  // a rebuild that never ended: publish what it stated
  }
  g_staging.entity = entity;
  g_staging.used = 0u;
  g_staging_active = true;
}

void refii_bridge_end_texture_layers(uint32_t entity) {
  using namespace refii::vfs;
  std::lock_guard<std::mutex> guard(g_lock);
  if (g_staging_active && g_staging.entity == entity) {
    Commit();
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

  if (count > kTextureLayersPerTexture) {
    count = kTextureLayersPerTexture;
    ++g_dropped;
  }

  bool changed = false;
  if (g_staging_active && g_staging.entity == entity) {
    Append(g_staging, original, kind, layers, count, &changed);
    return;
  }

  // A statement outside a staged rebuild lands on the live record directly.
  EntityLayers* record = Claim(entity);
  if (record == nullptr) {
    ++g_dropped;
    return;
  }
  Append(*record, original, kind, layers, count, &changed);
  if (changed) {
    ++record->generation;
  }
}
}  // extern "C"
