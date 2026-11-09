// refii — the world a title states (core/payload_bridge.h): its levels' up and down, the effects
// it creates, places and kills, the decals it submits, the displacement quads it adds to a
// render scene, and the render objects its effects add for mesh particles.
//
// All of them cross from a title's hooks (the payload, on the guest's threads) to its host bridge
// (on Godot's), which live in different modules; this is the one table both link. Neither side
// knows a title: a hook states what the guest's own entry point was called with, the bridge
// shapes it for the shell.

#include <array>
#include <atomic>
#include <cstdint>
#include <cstring>
#include <mutex>

#include "core/payload_bridge.h"

namespace {

// --- Levels: a manager's level is up between its loader's success and its unload. -----------

struct StatedLevel {
  std::atomic<uint32_t> manager{0};
  std::atomic<int32_t> up{0};
};

/// A title stands few level managers at once (Fable 3: one per game component, two components).
constexpr uint32_t kManagers = 8u;
StatedLevel g_levels[kManagers];

// --- Effects, by the handle the guest keys them on. ----------------------------------------

/// One effect. Every read and write of the table holds g_fx_lock.
struct StatedEffect {
  uint32_t handle = 0u;
  uint32_t name_hash = 0u;
  uint32_t scene = 0xFFFFFFFFu;
  uint32_t sequence = 0u;
  uint32_t flags = 0u;
  float position[4]{};
  float orientation[4]{0.0f, 0.0f, 0.0f, 1.0f};
  /// The entity that plays the effect and the entity that one was attached to, 0 when unstated.
  uint32_t owner = 0u;
  uint32_t attached = 0u;
  /// The fade-out's length and the guest frame it was stated on (REFII_STATED_FX_FADING).
  float fade_seconds = 0.0f;
  uint32_t fade_frame = 0u;
  /// The stated colour (red, green, blue, alpha) and size scale; 1 until stated.
  float colour[4]{1.0f, 1.0f, 1.0f, 1.0f};
  float scale = 1.0f;
  /// Particle flushes stated since the create (REFII_STATED_FX_FLUSHES at row [21]).
  uint32_t flushes = 0u;
  /// The last point list a spline setter stated, x y z each (REFII_STATED_FX_SPLINE).
  uint32_t spline_count = 0u;
  float spline[REFII_STATED_FX_SPLINE_POINTS * 3u]{};
};

/// Sized above the live effect population: one slot per effect instance, not per particle.
constexpr uint32_t kEffectSlots = 1024u;
std::mutex g_fx_lock;
StatedEffect g_fx[kEffectSlots];
std::atomic<uint32_t> g_fx_sequence{0};

/// Open addressing on the handle. Handles are a counter, so the multiply spreads neighbours.
uint32_t EffectSlotFor(uint32_t handle) { return ((handle * 2654435761u) >> 12) & (kEffectSlots - 1u); }

/// The slot holding `handle`, or null. Caller holds g_fx_lock.
StatedEffect* FindEffect(uint32_t handle) {
  if (handle == 0u) {
    return nullptr;
  }
  const uint32_t start = EffectSlotFor(handle);
  for (uint32_t probe = 0u; probe < kEffectSlots; ++probe) {
    StatedEffect& slot = g_fx[(start + probe) & (kEffectSlots - 1u)];
    if (slot.handle == handle) {
      return &slot;
    }
    if (slot.handle == 0u) {
      return nullptr;
    }
  }
  return nullptr;
}

/// The slot for `handle`, claimed when new; the stalest slot is evicted when the table is full,
/// since the guest frees handles without a statement when a kill completes. Caller holds the lock.
StatedEffect* ClaimEffect(uint32_t handle) {
  if (handle == 0u) {
    return nullptr;
  }
  if (StatedEffect* found = FindEffect(handle); found != nullptr) {
    return found;
  }
  const uint32_t start = EffectSlotFor(handle);
  StatedEffect* oldest = nullptr;
  for (uint32_t probe = 0u; probe < kEffectSlots; ++probe) {
    StatedEffect& slot = g_fx[(start + probe) & (kEffectSlots - 1u)];
    if (slot.handle == 0u) {
      oldest = &slot;
      break;
    }
    if (oldest == nullptr || slot.sequence < oldest->sequence) {
      oldest = &slot;
    }
  }
  *oldest = StatedEffect{};
  oldest->handle = handle;
  return oldest;
}

void Stamp(StatedEffect& slot) {
  slot.sequence = g_fx_sequence.fetch_add(1u, std::memory_order_relaxed) + 1u;
}

/// Empties slot `at` and closes the gap behind it: a later slot of the same probe run moves
/// back unless its home lies cyclically in (hole, slot], so FindEffect, which stops at the
/// first empty slot, still reaches every handle placed past the one erased. Caller holds the lock.
void EraseEffectAt(uint32_t at) {
  constexpr uint32_t kMask = kEffectSlots - 1u;
  uint32_t hole = at & kMask;
  g_fx[hole] = StatedEffect{};
  for (uint32_t next = (hole + 1u) & kMask; g_fx[next].handle != 0u; next = (next + 1u) & kMask) {
    const uint32_t home = EffectSlotFor(g_fx[next].handle);
    const bool reachable = hole <= next ? (hole < home && home <= next) : (hole < home || home <= next);
    if (!reachable) {
      g_fx[hole] = g_fx[next];
      g_fx[next] = StatedEffect{};
      hole = next;
    }
  }
}

// --- Decals: the guest keeps 50, a new one replacing the oldest. ---------------------------

constexpr uint32_t kDecalRing = 50u;

struct StatedDecal {
  uint32_t words[REFII_WORLD_DECAL_WORDS] = {};
  bool live = false;
};

std::mutex g_decal_lock;
StatedDecal g_decals[kDecalRing];
uint32_t g_decal_next = 0u;
uint32_t g_decal_serial = 0u;

// --- Displacement quads: one per render object id, few at once (one warp per player). -----

constexpr uint32_t kQuadSlots = 8u;

struct StatedQuad {
  uint32_t id = 0xFFFFFFFFu;
  uint32_t scene = 0u;
  uint32_t words[REFII_WORLD_QUAD_WORDS] = {};
};

std::mutex g_quad_lock;
StatedQuad g_quads[kQuadSlots];

// --- FX mesh particles: the descriptor each render object id was added from. --------------

/// Direct-mapped on the id (an index into the guest's twin table, a few thousand in a region).
/// Written from the particle thread (adds) and any thread (removes), read from Godot's.
std::atomic<uint32_t> g_fx_meshes[REFII_WORLD_FX_MESH_IDS];
/// One past the highest id ever stated: the read scans no further.
std::atomic<uint32_t> g_fx_mesh_ceiling{0};

}  // namespace

extern "C" {

void refii_bridge_level_stated(uint32_t manager, int32_t up) {
  if (manager == 0u) {
    return;
  }
  for (StatedLevel& slot : g_levels) {
    if (slot.manager.load(std::memory_order_acquire) == manager) {
      slot.up.store(up != 0 ? 1 : 0, std::memory_order_release);
      return;
    }
  }
  if (up == 0) {
    return;
  }
  for (StatedLevel& slot : g_levels) {
    uint32_t held = 0u;
    if (slot.manager.compare_exchange_strong(held, manager, std::memory_order_acq_rel)) {
      slot.up.store(1, std::memory_order_release);
      return;
    }
  }
}

int32_t refii_bridge_level_is_up(uint32_t manager) {
  if (manager == 0u) {
    return 0;
  }
  for (StatedLevel& slot : g_levels) {
    if (slot.manager.load(std::memory_order_acquire) == manager) {
      return slot.up.load(std::memory_order_acquire);
    }
  }
  return 0;
}

void refii_bridge_fx_created(uint32_t handle, uint32_t name_hash) {
  std::lock_guard<std::mutex> lock(g_fx_lock);
  if (StatedEffect* slot = ClaimEffect(handle); slot != nullptr) {
    slot->name_hash = name_hash;
    // A handle the guest makes again is a new effect: whoever played the last one does not
    // play this one until a hook says so.
    slot->owner = 0u;
    slot->attached = 0u;
    slot->flags &= ~REFII_STATED_FX_FADING;
    slot->fade_seconds = 0.0f;
    slot->fade_frame = 0u;
    for (float& lane : slot->colour) {
      lane = 1.0f;
    }
    slot->scale = 1.0f;
    slot->flushes = 0u;
    slot->spline_count = 0u;
    slot->flags &= ~(REFII_STATED_FX_SPLINE | REFII_STATED_FX_SATURATION);
    Stamp(*slot);
  }
}

void refii_bridge_fx_started(uint32_t handle) {
  std::lock_guard<std::mutex> lock(g_fx_lock);
  if (StatedEffect* slot = ClaimEffect(handle); slot != nullptr) {
    slot->flags |= REFII_STATED_FX_STARTED;
    Stamp(*slot);
  }
}

void refii_bridge_fx_scene(uint32_t handle, uint32_t scene) {
  std::lock_guard<std::mutex> lock(g_fx_lock);
  if (StatedEffect* slot = ClaimEffect(handle); slot != nullptr) {
    slot->scene = scene;
    Stamp(*slot);
  }
}

void refii_bridge_fx_position(uint32_t handle, const float* lanes) {
  if (lanes == nullptr) {
    return;
  }
  std::lock_guard<std::mutex> lock(g_fx_lock);
  if (StatedEffect* slot = ClaimEffect(handle); slot != nullptr) {
    std::memcpy(slot->position, lanes, sizeof(slot->position));
    slot->flags |= REFII_STATED_FX_PLACED;
    Stamp(*slot);
  }
}

void refii_bridge_fx_orientation(uint32_t handle, const float* lanes) {
  if (lanes == nullptr) {
    return;
  }
  std::lock_guard<std::mutex> lock(g_fx_lock);
  if (StatedEffect* slot = ClaimEffect(handle); slot != nullptr) {
    std::memcpy(slot->orientation, lanes, sizeof(slot->orientation));
    slot->flags |= REFII_STATED_FX_ORIENTED;
    Stamp(*slot);
  }
}

void refii_bridge_fx_killed(uint32_t handle) {
  std::lock_guard<std::mutex> lock(g_fx_lock);
  if (StatedEffect* slot = FindEffect(handle); slot != nullptr) {
    // Cleared, not flagged: the guest erases the handle once the kill has run, and a slot kept
    // for a handle the guest may reuse would hand the next effect this one's name.
    EraseEffectAt(static_cast<uint32_t>(slot - g_fx));
    g_fx_sequence.fetch_add(1u, std::memory_order_relaxed);
  }
}

void refii_bridge_fx_fade(uint32_t handle, float seconds) {
  std::lock_guard<std::mutex> lock(g_fx_lock);
  // Found, not claimed: a fade ends an effect a create already stated.
  if (StatedEffect* slot = FindEffect(handle); slot != nullptr) {
    slot->fade_seconds = seconds;
    slot->fade_frame = refii_bridge_guest_frame();
    slot->flags |= REFII_STATED_FX_FADING;
    Stamp(*slot);
  }
}

void refii_bridge_fx_colour(uint32_t handle, float red, float green, float blue) {
  std::lock_guard<std::mutex> lock(g_fx_lock);
  if (StatedEffect* slot = FindEffect(handle); slot != nullptr) {
    slot->colour[0] = red;
    slot->colour[1] = green;
    slot->colour[2] = blue;
    Stamp(*slot);
  }
}

void refii_bridge_fx_alpha(uint32_t handle, float alpha) {
  std::lock_guard<std::mutex> lock(g_fx_lock);
  if (StatedEffect* slot = FindEffect(handle); slot != nullptr) {
    slot->colour[3] = alpha;
    Stamp(*slot);
  }
}

void refii_bridge_fx_scale(uint32_t handle, float scale) {
  std::lock_guard<std::mutex> lock(g_fx_lock);
  if (StatedEffect* slot = FindEffect(handle); slot != nullptr) {
    slot->scale = scale;
    Stamp(*slot);
  }
}

void refii_bridge_fx_spline(uint32_t handle, const float* points, uint32_t count) {
  if (points == nullptr || count < 2u) {
    return;
  }
  if (count > REFII_STATED_FX_SPLINE_POINTS) {
    count = REFII_STATED_FX_SPLINE_POINTS;
  }
  std::lock_guard<std::mutex> lock(g_fx_lock);
  if (StatedEffect* slot = FindEffect(handle); slot != nullptr) {
    std::memcpy(slot->spline, points, count * 3u * sizeof(float));
    slot->spline_count = count;
    slot->flags |= REFII_STATED_FX_SPLINE;
    Stamp(*slot);
  }
}

uint32_t refii_bridge_fx_spline_read(uint32_t handle, float* out, uint32_t max_points) {
  if (out == nullptr || max_points == 0u) {
    return 0u;
  }
  std::lock_guard<std::mutex> lock(g_fx_lock);
  const StatedEffect* slot = FindEffect(handle);
  if (slot == nullptr || slot->spline_count == 0u) {
    return 0u;
  }
  const uint32_t count = slot->spline_count < max_points ? slot->spline_count : max_points;
  std::memcpy(out, slot->spline, count * 3u * sizeof(float));
  return count;
}

void refii_bridge_fx_flush(uint32_t handle) {
  std::lock_guard<std::mutex> lock(g_fx_lock);
  if (StatedEffect* slot = FindEffect(handle); slot != nullptr) {
    ++slot->flushes;
    Stamp(*slot);
  }
}

void refii_bridge_fx_saturation_mask(uint32_t handle) {
  std::lock_guard<std::mutex> lock(g_fx_lock);
  if (StatedEffect* slot = FindEffect(handle); slot != nullptr) {
    slot->flags |= REFII_STATED_FX_SATURATION;
    Stamp(*slot);
  }
}

void refii_bridge_fx_scene_killed(uint32_t scene) {
  std::lock_guard<std::mutex> lock(g_fx_lock);
  // An erase can move a later slot of the scene back onto an index already passed, so the
  // sweep repeats until a pass finds none.
  for (bool erased = true; erased;) {
    erased = false;
    for (uint32_t i = 0u; i < kEffectSlots; ++i) {
      while (g_fx[i].handle != 0u && g_fx[i].scene == scene) {
        EraseEffectAt(i);
        erased = true;
      }
    }
  }
  g_fx_sequence.fetch_add(1u, std::memory_order_relaxed);
}

void refii_bridge_fx_owner(uint32_t handle, uint32_t owner, uint32_t attached) {
  std::lock_guard<std::mutex> lock(g_fx_lock);
  // Found, not claimed: an owner is stated for an effect its create already stated.
  if (StatedEffect* slot = FindEffect(handle); slot != nullptr) {
    slot->owner = owner;
    slot->attached = attached;
    Stamp(*slot);
  }
}

static_assert(REFII_STATED_FX_WORDS >= 22u, "a stated effect row carries its owner and attached words at [12] [13], its fade at [14] [15], its colour and scale at [16..20] and its flushes at [21]");

uint32_t refii_bridge_fx_rows(uint32_t* out_words, uint32_t word_capacity) {
  if (out_words == nullptr) {
    return 0u;
  }
  const uint32_t room = word_capacity / REFII_STATED_FX_WORDS;
  std::lock_guard<std::mutex> lock(g_fx_lock);
  uint32_t written = 0u;
  for (uint32_t i = 0u; i < kEffectSlots && written < room; ++i) {
    const StatedEffect& slot = g_fx[i];
    if (slot.handle == 0u) {
      continue;
    }
    uint32_t* row = out_words + (written * REFII_STATED_FX_WORDS);
    row[0] = slot.handle;
    row[1] = slot.name_hash;
    row[2] = slot.sequence;
    row[3] = slot.flags;
    std::memcpy(row + 4, slot.position, sizeof(slot.position));
    std::memcpy(row + 8, slot.orientation, sizeof(slot.orientation));
    row[12] = slot.owner;
    row[13] = slot.attached;
    std::memcpy(row + 14, &slot.fade_seconds, sizeof(uint32_t));
    row[15] = slot.fade_frame;
    std::memcpy(row + 16, slot.colour, sizeof(slot.colour));
    std::memcpy(row + 20, &slot.scale, sizeof(uint32_t));
    row[21] = slot.flushes;
    ++written;
  }
  return written;
}

uint32_t refii_bridge_fx_statement_count(void) { return g_fx_sequence.load(std::memory_order_relaxed); }

void refii_bridge_world_decal_stated(const uint32_t* words) {
  if (words == nullptr) {
    return;
  }
  std::lock_guard<std::mutex> lock(g_decal_lock);
  StatedDecal& decal = g_decals[g_decal_next];
  std::memcpy(decal.words, words, sizeof(decal.words));
  decal.words[0] = ++g_decal_serial;
  decal.live = true;
  g_decal_next = (g_decal_next + 1u) % kDecalRing;
}

void refii_bridge_world_decals_cleared(void) {
  std::lock_guard<std::mutex> lock(g_decal_lock);
  for (StatedDecal& decal : g_decals) {
    decal.live = false;
  }
  ++g_decal_serial;
}

void refii_bridge_world_decals_dropped(uint32_t owner) {
  if (owner == 0u) {
    return;
  }
  std::lock_guard<std::mutex> lock(g_decal_lock);
  bool any = false;
  for (StatedDecal& decal : g_decals) {
    if (decal.live && decal.words[1] == owner) {
      decal.live = false;
      any = true;
    }
  }
  if (any) {
    ++g_decal_serial;
  }
}

void refii_bridge_world_quad_stated(uint32_t id, uint32_t scene, const uint32_t* words) {
  if (words == nullptr || id == 0xFFFFFFFFu) {
    return;
  }
  std::lock_guard<std::mutex> lock(g_quad_lock);
  StatedQuad* slot = nullptr;
  for (StatedQuad& quad : g_quads) {
    if (quad.id == id) {
      slot = &quad;
      break;
    }
    if (slot == nullptr && quad.id == 0xFFFFFFFFu) {
      slot = &quad;
    }
  }
  if (slot == nullptr) {
    return;
  }
  slot->id = id;
  slot->scene = scene;
  std::memcpy(slot->words, words, sizeof(slot->words));
}

void refii_bridge_world_quad_removed(uint32_t id) {
  std::lock_guard<std::mutex> lock(g_quad_lock);
  for (StatedQuad& quad : g_quads) {
    if (quad.id == id) {
      quad = StatedQuad{};
    }
  }
}

uint32_t refii_bridge_world_quads_read(uint32_t* out, uint32_t capacity) {
  constexpr uint32_t kRow = 2u + REFII_WORLD_QUAD_WORDS;
  std::lock_guard<std::mutex> lock(g_quad_lock);
  uint32_t count = 0u;
  for (const StatedQuad& quad : g_quads) {
    if (quad.id == 0xFFFFFFFFu) {
      continue;
    }
    if (out == nullptr || (count + 1u) * kRow > capacity) {
      break;
    }
    uint32_t* row = out + count * kRow;
    row[0] = quad.id;
    row[1] = quad.scene;
    std::memcpy(row + 2, quad.words, sizeof(quad.words));
    ++count;
  }
  return count;
}

void refii_bridge_world_fx_mesh_stated(uint32_t id, uint32_t descriptor) {
  if (id >= REFII_WORLD_FX_MESH_IDS || descriptor == 0u) {
    return;
  }
  g_fx_meshes[id].store(descriptor, std::memory_order_release);
  uint32_t ceiling = g_fx_mesh_ceiling.load(std::memory_order_relaxed);
  while (ceiling <= id && !g_fx_mesh_ceiling.compare_exchange_weak(ceiling, id + 1u, std::memory_order_relaxed)) {
  }
}

void refii_bridge_world_fx_mesh_removed(uint32_t id) {
  if (id < REFII_WORLD_FX_MESH_IDS) {
    g_fx_meshes[id].store(0u, std::memory_order_release);
  }
}

uint32_t refii_bridge_world_fx_meshes_read(uint32_t* out_pairs, uint32_t pair_capacity) {
  if (out_pairs == nullptr) {
    return 0u;
  }
  const uint32_t ceiling = g_fx_mesh_ceiling.load(std::memory_order_relaxed);
  uint32_t count = 0u;
  for (uint32_t id = 0u; id < ceiling && count < pair_capacity; ++id) {
    const uint32_t descriptor = g_fx_meshes[id].load(std::memory_order_acquire);
    if (descriptor == 0u) {
      continue;
    }
    out_pairs[count * 2u] = id;
    out_pairs[count * 2u + 1u] = descriptor;
    ++count;
  }
  return count;
}

uint32_t refii_bridge_world_decals_read(uint32_t* out, uint32_t capacity, uint32_t* out_serial) {
  std::lock_guard<std::mutex> lock(g_decal_lock);
  if (out_serial != nullptr) {
    *out_serial = g_decal_serial;
  }
  uint32_t count = 0u;
  for (uint32_t i = 0u; i < kDecalRing; ++i) {
    // Oldest first: the ring from the next slot to overwrite.
    const StatedDecal& decal = g_decals[(g_decal_next + i) % kDecalRing];
    if (!decal.live) {
      continue;
    }
    if (out == nullptr || (count + 1u) * REFII_WORLD_DECAL_WORDS > capacity) {
      break;
    }
    std::memcpy(out + count * REFII_WORLD_DECAL_WORDS, decal.words, sizeof(decal.words));
    ++count;
  }
  return count;
}

}  // extern "C"
