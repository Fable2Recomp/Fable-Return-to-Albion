// refii — the FX MESH PARTICLES the guest's effects create, as SHARED STATE.
//
// In librefii_vfs for the reason entity_appearance_state.cpp gives: the PAYLOAD writes
// (Fable2/src/vfs/src/hooks/fx_meshes.cpp) and the HOST reads (the host bridge).
//
// An effect's mesh renderer (`sub_821CEA80`, kind 4) keeps one 0xB0-byte descriptor per live mesh
// particle: `sub_82456D58` (its only caller) states the descriptor, the caller then sets its twin
// handle at +4 (`sub_8229BA10`), and the twin is destroyed through `sub_82175918(handle)` the frame
// its particle is gone, before the descriptor is freed (0x821CF1B0). Kept here: the descriptors.
//
// And per render twin handle, the ALPHA (model command 16) and TINT (model command 17) last stated
// to it, taken where the command reaches the twin (`Render_ApplyCommandToHandle`, hooks/
// fx_meshes.cpp) and forgotten when the twin is destroyed. Every statement and every descriptor
// takes a number from one counter: a descriptor exists before its twin's handle is allocated
// (`sub_821CEA80`: `sub_82456D58`, then `sub_8229BA10`), so a value numbered before its descriptor
// was stated to an earlier twin on a reused handle and is not this particle's.

#include <atomic>
#include <cstdint>
#include <cstring>
#include <mutex>
#include <utility>
#include <vector>

#include "core/payload_bridge_fable2.h"

namespace refii::vfs {
namespace {

constexpr size_t kMaxDescriptors = 1024u;

std::mutex g_lock;
std::vector<std::pair<uint32_t, uint64_t>> g_descriptors;  // {descriptor, number}

std::atomic<uint64_t> g_number{1u};

uint64_t NextNumber() { return g_number.fetch_add(1u, std::memory_order_relaxed) + 1u; }

constexpr uint32_t kEmpty = 0u;
constexpr uint32_t kGone = 1u;  // a destroyed twin; probes continue past it
constexpr uint32_t kSlots = 32768u;
constexpr uint32_t kProbes = 64u;

/// Keyed by handle + 2, clear of kEmpty and kGone. A number of 0 is a lane never stated.
struct StatedTwin {
  std::atomic<uint32_t> key{kEmpty};
  std::atomic<uint32_t> alpha{0u};
  std::atomic<uint64_t> alpha_number{0u};
  std::atomic<uint32_t> tint[4] = {};
  std::atomic<uint64_t> tint_number{0u};
};

StatedTwin g_twins[kSlots];

uint32_t SlotFor(uint32_t key) { return (key * 2654435761u >> 15) & (kSlots - 1u); }

StatedTwin* FindTwin(uint32_t key) {
  const uint32_t start = SlotFor(key);
  for (uint32_t probe = 0u; probe < kProbes; ++probe) {
    StatedTwin& slot = g_twins[(start + probe) & (kSlots - 1u)];
    const uint32_t held = slot.key.load(std::memory_order_acquire);
    if (held == key) {
      return &slot;
    }
    if (held == kEmpty) {
      return nullptr;
    }
  }
  return nullptr;
}

StatedTwin* ClaimTwin(uint32_t key) {
  if (StatedTwin* found = FindTwin(key); found != nullptr) {
    return found;
  }
  const uint32_t start = SlotFor(key);
  for (uint32_t probe = 0u; probe < kProbes; ++probe) {
    StatedTwin& slot = g_twins[(start + probe) & (kSlots - 1u)];
    uint32_t held = slot.key.load(std::memory_order_relaxed);
    if (held == key) {
      return &slot;
    }
    if ((held == kEmpty || held == kGone) &&
        slot.key.compare_exchange_strong(held, key, std::memory_order_acq_rel)) {
      slot.alpha_number.store(0u, std::memory_order_relaxed);
      slot.tint_number.store(0u, std::memory_order_relaxed);
      return &slot;
    }
  }
  return nullptr;
}

uint32_t KeyOf(uint32_t handle) { return handle + 2u; }

/// `Render_ApplyCommandToHandle` reaches a twin only for a handle >= 0 (0x822222E0: `v3 < 0` out).
bool IsHandle(uint32_t handle) { return handle < 0x80000000u; }

}  // namespace

uint32_t StatedFxMeshes(uint32_t* out, uint32_t capacity) {
  std::lock_guard<std::mutex> guard(g_lock);
  uint32_t count = 0u;
  for (const auto& [descriptor, number] : g_descriptors) {
    if (out == nullptr || count >= capacity) {
      break;
    }
    out[count++] = descriptor;
  }
  return count;
}

/// The tint (out[0..3]) and alpha (out[4]) stated to `handle` since `descriptor` was noted; a
/// lane not stated since keeps the twin's constructed 1.0 (`sub_82AA2658`: +240 = splat 1.0).
void StatedFxMeshLook(uint32_t descriptor, uint32_t handle, float* out) {
  for (uint32_t i = 0u; i < 5u; ++i) {
    out[i] = 1.0f;
  }
  uint64_t since = 0u;
  {
    std::lock_guard<std::mutex> guard(g_lock);
    bool known = false;
    for (const auto& [noted, number] : g_descriptors) {
      if (noted == descriptor) {
        since = number;
        known = true;
        break;
      }
    }
    if (!known) {
      return;
    }
  }
  const StatedTwin* twin = IsHandle(handle) ? FindTwin(KeyOf(handle)) : nullptr;
  if (twin == nullptr) {
    return;
  }
  if (twin->tint_number.load(std::memory_order_acquire) > since) {
    for (uint32_t lane = 0u; lane < 4u; ++lane) {
      const uint32_t bits = twin->tint[lane].load(std::memory_order_relaxed);
      std::memcpy(out + lane, &bits, sizeof(float));
    }
  }
  if (twin->alpha_number.load(std::memory_order_acquire) > since) {
    const uint32_t bits = twin->alpha.load(std::memory_order_relaxed);
    std::memcpy(out + 4, &bits, sizeof(float));
  }
}

}  // namespace refii::vfs

extern "C" {

void refii_bridge_note_fx_mesh(uint32_t descriptor) {
  using namespace refii::vfs;
  if (descriptor == 0u) {
    return;
  }
  const uint64_t number = NextNumber();
  std::lock_guard<std::mutex> guard(g_lock);
  for (auto& [known, noted] : g_descriptors) {
    if (known == descriptor) {
      noted = number;
      return;
    }
  }
  if (g_descriptors.size() >= kMaxDescriptors) {
    g_descriptors.erase(g_descriptors.begin());
  }
  g_descriptors.emplace_back(descriptor, number);
}

void refii_bridge_drop_fx_mesh(uint32_t descriptor) {
  using namespace refii::vfs;
  std::lock_guard<std::mutex> guard(g_lock);
  for (size_t i = 0; i < g_descriptors.size(); ++i) {
    if (g_descriptors[i].first == descriptor) {
      g_descriptors.erase(g_descriptors.begin() + static_cast<std::ptrdiff_t>(i));
      return;
    }
  }
}

uint32_t refii_bridge_fx_meshes(uint32_t* out, uint32_t capacity) {
  return refii::vfs::StatedFxMeshes(out, capacity);
}

void refii_bridge_note_twin_alpha(uint32_t handle, uint32_t alpha_bits) {
  using namespace refii::vfs;
  if (!IsHandle(handle)) {
    return;
  }
  if (StatedTwin* twin = ClaimTwin(KeyOf(handle)); twin != nullptr) {
    twin->alpha.store(alpha_bits, std::memory_order_relaxed);
    twin->alpha_number.store(NextNumber(), std::memory_order_release);
  }
}

void refii_bridge_note_twin_tint(uint32_t handle, const uint32_t* tint) {
  using namespace refii::vfs;
  if (tint == nullptr || !IsHandle(handle)) {
    return;
  }
  if (StatedTwin* twin = ClaimTwin(KeyOf(handle)); twin != nullptr) {
    for (uint32_t lane = 0u; lane < 4u; ++lane) {
      twin->tint[lane].store(tint[lane], std::memory_order_relaxed);
    }
    twin->tint_number.store(NextNumber(), std::memory_order_release);
  }
}

void refii_bridge_forget_twin(uint32_t handle) {
  using namespace refii::vfs;
  if (!IsHandle(handle)) {
    return;
  }
  if (StatedTwin* twin = FindTwin(KeyOf(handle)); twin != nullptr) {
    twin->key.store(kGone, std::memory_order_release);
  }
}

}  // extern "C"
