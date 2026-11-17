// refii — each character part's stated MORPH TARGETS, as SHARED STATE.
//
// In librefii_vfs for the reason entity_appearance_state.cpp gives: the PAYLOAD writes
// (Fable2/src/vfs/src/hooks/texture_layers.cpp, on the guest's thread) and the HOST reads (the host bridge, on
// Godot's).
//
// One record per entity, one entry per part the guest submitted WITH morph targets: the part's
// model hash and the {target model hash, weight} pairs that follow it in the list the guest
// hands `sub_82A48B90`. A part submitted as its own model alone removes its entry.

#include <cstdint>
#include <cstring>
#include <mutex>

#include "core/payload_bridge_fable2.h"

namespace refii::vfs {
namespace {

constexpr uint32_t kSlots = 1024u;
constexpr uint32_t kProbes = 32u;

struct PartEntry {
  uint32_t base = 0u;
  uint32_t count = 0u;
  uint64_t stamp = 0u;
  uint32_t pairs[kPartMorphTargets * 2u] = {};
};

struct EntityParts {
  uint32_t entity = 0u;
  uint32_t generation = 0u;
  PartEntry parts[kPartMorphParts];
};

std::mutex g_lock;
EntityParts* g_records = nullptr;
uint64_t g_statements = 0u;
uint64_t g_morphed = 0u;
uint64_t g_stamp = 0u;

EntityParts* Records() {
  if (g_records == nullptr) {
    g_records = new EntityParts[kSlots]();
  }
  return g_records;
}

uint32_t SlotFor(uint32_t entity) {
  const uint32_t h = entity * 2654435761u;
  return (h >> 12) & (kSlots - 1u);
}

EntityParts* Find(uint32_t entity) {
  EntityParts* records = Records();
  const uint32_t start = SlotFor(entity);
  for (uint32_t probe = 0u; probe < kProbes; ++probe) {
    EntityParts& record = records[(start + probe) & (kSlots - 1u)];
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

EntityParts* Claim(uint32_t entity) {
  if (EntityParts* found = Find(entity); found != nullptr) {
    return found;
  }
  EntityParts* records = Records();
  const uint32_t start = SlotFor(entity);
  for (uint32_t probe = 0u; probe < kProbes; ++probe) {
    EntityParts& record = records[(start + probe) & (kSlots - 1u)];
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

}  // namespace

/// Copies the entity's morphed parts into `out` as [base, count, count x (target, weight bits)]
/// per part and returns the words written. `out_statements` low word is the run total of part
/// submissions seen, morphed or not (0 means the hook never ran); high word, those that listed
/// at least one target.
uint32_t StatedPartMorphs(uint32_t entity, uint32_t* out, uint32_t capacity,
                          uint32_t* out_generation, uint64_t* out_statements) {
  std::lock_guard<std::mutex> guard(g_lock);
  if (out_statements != nullptr) {
    *out_statements = (g_statements & 0xFFFFFFFFull) | (g_morphed << 32);
  }
  if (out_generation != nullptr) {
    *out_generation = 0u;
  }
  if (entity == 0u || out == nullptr) {
    return 0u;
  }
  const EntityParts* record = Find(entity);
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

}  // namespace refii::vfs

extern "C" {

void refii_bridge_note_part_morph(uint32_t entity, uint32_t base_model, const uint32_t* pairs,
                                  uint32_t count) {
  using namespace refii::vfs;
  if (entity == 0u || base_model == 0u) {
    return;
  }

  std::lock_guard<std::mutex> guard(g_lock);
  ++g_statements;
  if (count != 0u) {
    ++g_morphed;
  }

  EntityParts* record = count != 0u ? Claim(entity) : Find(entity);
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

  if (count > kPartMorphTargets) {
    count = kPartMorphTargets;
  }

  const size_t bytes = sizeof(uint32_t) * count * 2u;
  if (entry != nullptr && entry->count == count && std::memcmp(entry->pairs, pairs, bytes) == 0) {
    entry->stamp = ++g_stamp;
    return;
  }
  if (entry == nullptr) {
    entry = spare != nullptr ? spare : oldest;
  }

  entry->base = base_model;
  entry->count = count;
  entry->stamp = ++g_stamp;
  std::memcpy(entry->pairs, pairs, bytes);
  ++record->generation;
}

// The entity left the world (`rex_lhEntity_Destroy`): its parts are freed with it.
void refii_bridge_forget_part_morphs(uint32_t entity) {
  using namespace refii::vfs;
  if (entity == 0u) {
    return;
  }
  std::lock_guard<std::mutex> guard(g_lock);
  if (EntityParts* record = Find(entity); record != nullptr) {
    record->entity = 0u;
    for (PartEntry& part : record->parts) {
      part.count = 0u;
    }
    ++record->generation;
  }
}
}  // extern "C"
