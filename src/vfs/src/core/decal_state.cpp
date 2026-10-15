// refii — the DECALS the guest states, as SHARED STATE.
//
// In librefii_vfs for the reason entity_appearance_state.cpp gives: the PAYLOAD writes
// (src/hooks/decals.cpp, on the guest's render thread) and the HOST reads (the host bridge).
//
// The guest keeps 50: a new decal replaces the oldest (`sub_82A629B8`, ring of 0x32 slots), its
// life is FLT_MAX so none expires, and a decal goes with its owner (`sub_82AA3D38` ->
// `sub_82A62AF0`) or with everything on a clear (`sub_82A406D8` -> `sub_82A62BF8`).

#include <cstdint>
#include <cstring>
#include <mutex>

#include "core/payload_bridge.h"

namespace refii::vfs {
namespace {

constexpr uint32_t kRing = 50u;

struct Decal {
  uint32_t words[kDecalWords] = {};
  bool live = false;
};

std::mutex g_lock;
Decal g_ring[kRing];
uint32_t g_next = 0u;
uint32_t g_serial = 0u;

}  // namespace

uint32_t StatedDecals(uint32_t* out, uint32_t capacity, uint32_t* out_serial) {
  std::lock_guard<std::mutex> guard(g_lock);
  if (out_serial != nullptr) {
    *out_serial = g_serial;
  }
  uint32_t count = 0u;
  for (uint32_t i = 0u; i < kRing; ++i) {
    // Oldest first: the ring from the next slot to overwrite.
    const Decal& decal = g_ring[(g_next + i) % kRing];
    if (!decal.live) {
      continue;
    }
    if (out == nullptr || (count + 1u) * kDecalWords > capacity) {
      break;
    }
    std::memcpy(out + count * kDecalWords, decal.words, sizeof(decal.words));
    ++count;
  }
  return count;
}

}  // namespace refii::vfs

extern "C" {

void refii_bridge_note_decal(const uint32_t* words) {
  using namespace refii::vfs;
  if (words == nullptr) {
    return;
  }
  std::lock_guard<std::mutex> guard(g_lock);
  Decal& decal = g_ring[g_next];
  std::memcpy(decal.words, words, sizeof(decal.words));
  decal.words[0] = ++g_serial;
  decal.live = true;
  g_next = (g_next + 1u) % kRing;
}

void refii_bridge_clear_decals(void) {
  using namespace refii::vfs;
  std::lock_guard<std::mutex> guard(g_lock);
  for (Decal& decal : g_ring) {
    decal.live = false;
  }
  ++g_serial;
}

void refii_bridge_drop_decals_of(uint32_t owner) {
  using namespace refii::vfs;
  if (owner == 0u) {
    return;
  }
  std::lock_guard<std::mutex> guard(g_lock);
  bool any = false;
  for (Decal& decal : g_ring) {
    if (decal.live && decal.words[1] == owner) {
      decal.live = false;
      any = true;
    }
  }
  if (any) {
    ++g_serial;
  }
}

}  // extern "C"
