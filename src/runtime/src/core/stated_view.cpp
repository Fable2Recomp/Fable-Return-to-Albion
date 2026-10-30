// refii — the view a title states, and the host's world-build hold (core/payload_bridge.h).
//
// Both cross from a title's hooks (the payload) to its host bridge, which live in different
// modules: the hook sees the guest's call, the bridge answers the shell. Neither knows a title.

#include <array>
#include <atomic>
#include <cstdint>
#include <mutex>

#include "core/payload_bridge.h"

namespace {

std::mutex g_camera_mutex;
std::array<float, 11> g_camera{};
bool g_camera_stated = false;
std::array<float, 22> g_camera_pair{};
bool g_camera_pair_stated = false;

std::atomic<int32_t> g_world_build_pending{0};

}  // namespace

extern "C" void refii_bridge_camera_stated(const float* position, const float* up,
                                           const float* forward, float horizontal_fov,
                                           int32_t snap) {
  if (position == nullptr || up == nullptr || forward == nullptr) {
    return;
  }

  std::lock_guard<std::mutex> lock(g_camera_mutex);
  for (int i = 0; i < 3; ++i) {
    g_camera[i] = position[i];
    g_camera[3 + i] = up[i];
    g_camera[6 + i] = forward[i];
  }

  g_camera[9] = horizontal_fov;
  g_camera[10] = snap != 0 ? 1.0f : 0.0f;
  g_camera_stated = true;
}

extern "C" int32_t refii_bridge_camera_read(float* out) {
  if (out == nullptr) {
    return 0;
  }

  std::lock_guard<std::mutex> lock(g_camera_mutex);
  if (!g_camera_stated) {
    return 0;
  }

  for (size_t i = 0; i < g_camera.size(); ++i) {
    out[i] = g_camera[i];
  }

  return 1;
}

extern "C" void refii_bridge_camera_pair_stated(const float* live, const float* previous) {
  if (live == nullptr || previous == nullptr) {
    return;
  }

  std::lock_guard<std::mutex> lock(g_camera_mutex);
  for (size_t i = 0; i < 11; ++i) {
    g_camera_pair[i] = live[i];
    g_camera_pair[11 + i] = previous[i];
  }

  g_camera_pair_stated = true;
}

extern "C" int32_t refii_bridge_camera_pair_read(float* out) {
  if (out == nullptr) {
    return 0;
  }

  std::lock_guard<std::mutex> lock(g_camera_mutex);
  if (!g_camera_pair_stated) {
    return 0;
  }

  for (size_t i = 0; i < g_camera_pair.size(); ++i) {
    out[i] = g_camera_pair[i];
  }

  return 1;
}

extern "C" void refii_bridge_set_world_build_pending(int32_t pending) {
  g_world_build_pending.store(pending != 0 ? 1 : 0, std::memory_order_release);
}

extern "C" int32_t refii_bridge_world_build_pending(void) {
  return g_world_build_pending.load(std::memory_order_acquire);
}
