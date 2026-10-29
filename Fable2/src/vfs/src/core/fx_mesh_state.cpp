// refii — the FX MESH PARTICLES the guest's effects create, as SHARED STATE.
//
// In librefii_vfs for the reason entity_appearance_state.cpp gives: the PAYLOAD writes
// (Fable2/src/vfs/src/hooks/fx_meshes.cpp) and the HOST reads (the host bridge).
//
// An effect's mesh renderer (`sub_821CEA80`, kind 4) keeps one 0xB0-byte descriptor per live mesh
// particle: `sub_82456D58` (its only caller) states the descriptor, the caller then sets its twin
// handle at +4 (`sub_8229BA10`), and the twin is destroyed through `sub_82175918(handle)` the frame
// its particle is gone, before the descriptor is freed (0x821CF1B0). Kept here: the descriptors.

#include <cstdint>
#include <mutex>
#include <vector>

#include "core/payload_bridge_fable2.h"

namespace refii::vfs {
namespace {

constexpr size_t kMaxDescriptors = 1024u;

std::mutex g_lock;
std::vector<uint32_t> g_descriptors;

}  // namespace

uint32_t StatedFxMeshes(uint32_t* out, uint32_t capacity) {
  std::lock_guard<std::mutex> guard(g_lock);
  uint32_t count = 0u;
  for (uint32_t descriptor : g_descriptors) {
    if (out == nullptr || count >= capacity) {
      break;
    }
    out[count++] = descriptor;
  }
  return count;
}

}  // namespace refii::vfs

extern "C" {

void refii_bridge_note_fx_mesh(uint32_t descriptor) {
  using namespace refii::vfs;
  if (descriptor == 0u) {
    return;
  }
  std::lock_guard<std::mutex> guard(g_lock);
  for (uint32_t known : g_descriptors) {
    if (known == descriptor) {
      return;
    }
  }
  if (g_descriptors.size() >= kMaxDescriptors) {
    g_descriptors.erase(g_descriptors.begin());
  }
  g_descriptors.push_back(descriptor);
}

void refii_bridge_drop_fx_mesh(uint32_t descriptor) {
  using namespace refii::vfs;
  std::lock_guard<std::mutex> guard(g_lock);
  for (size_t i = 0; i < g_descriptors.size(); ++i) {
    if (g_descriptors[i] == descriptor) {
      g_descriptors.erase(g_descriptors.begin() + static_cast<std::ptrdiff_t>(i));
      return;
    }
  }
}

uint32_t refii_bridge_fx_meshes(uint32_t* out, uint32_t capacity) {
  return refii::vfs::StatedFxMeshes(out, capacity);
}

}  // extern "C"
