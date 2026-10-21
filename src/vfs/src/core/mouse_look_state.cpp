// refii — the mouse's camera turn, as SHARED STATE.
//
// In librefii_vfs for the reason decal_state.cpp gives, in the other direction too: the HOST
// posts the turn the mouse made (the host bridge, on Godot's thread) and the PAYLOAD hands it to
// the local player's camera (src/hooks/mouse_look.cpp, on the guest's tick thread), then states
// what the camera made of it.
//
// THE CONTRACT. The host posts degrees of turn at the profile's middle camera sensitivity
// (rightward and upward positive). Once per camera update the payload takes ALL of it
// (`taken`, in posted degrees) and states the yaw the camera turned for it in that same update
// (`absorbed`, the camera's own degrees, rightward), the camera's degrees per posted degree, and
// how a turn of this camera is drawn: about the vertical through its focus at `pivot` metres
// along the level line of sight (ORBIT), about its own position (IN PLACE), or not at all
// (NONE — the camera takes no look input now). Vertical turn under the third-person camera's own
// dead zone is returned to the pending turn rather than taken. Posted and taken share one
// origin, so `(posted - taken) * factor` is exactly the turn no camera update has taken yet.
// `seq` is odd from the take until the update's statement, so a host read that straddles an
// update is seen and refused.
//
// THE DEVICE. The host also states whether the mouse is the player's look device (the device
// switch: the keyboard and mouse were touched last). While it is, the camera it drives keeps its
// heading as the hero moves, and a target lock takes the mouse as flicks (mouse_look.cpp); a pad
// keeps the shipped camera. `flicks` counts the target switches handed to a lock, a run total.

#include <atomic>
#include <cstdint>
#include <mutex>

#include "core/payload_bridge.h"

namespace refii::vfs {
namespace {

std::mutex g_lock;

/// Posted and not yet taken.
double g_pending_yaw = 0.0;
double g_pending_pitch = 0.0;

/// Everything posted and everything taken, in posted degrees.
double g_posted_yaw = 0.0;
double g_taken_yaw = 0.0;

/// Everything the camera turned for the mouse, in its own degrees, rightward.
double g_absorbed_yaw = 0.0;

uint32_t g_tick = 0u;
float g_factor = 1.0f;
float g_pivot = 0.0f;
int32_t g_mode = 0;

std::atomic<uint32_t> g_seq{0u};

std::atomic<bool> g_device{false};
std::atomic<uint32_t> g_flicks{0u};

}  // namespace

/// Adds the mouse's turn since the last post.
void MouseLookPost(float yaw_degrees, float pitch_degrees) {
  std::lock_guard<std::mutex> guard(g_lock);
  g_pending_yaw += yaw_degrees;
  g_pending_pitch += pitch_degrees;
  g_posted_yaw += yaw_degrees;
}

/// Whether the mouse is the player's look device, as the host's device switch last stated it.
void MouseLookSetDevice(bool mouse) { g_device.store(mouse, std::memory_order_release); }

/// The account as of the last camera update. Returns the sequence; odd means an update was mid-way.
uint32_t MouseLookState(uint32_t* out_tick, double* out_posted, double* out_taken,
                        double* out_absorbed, float* out_factor, float* out_pivot,
                        int32_t* out_mode, uint32_t* out_flicks) {
  std::lock_guard<std::mutex> guard(g_lock);
  if (out_flicks != nullptr) *out_flicks = g_flicks.load(std::memory_order_acquire);
  if (out_tick != nullptr) *out_tick = g_tick;
  if (out_posted != nullptr) *out_posted = g_posted_yaw;
  if (out_taken != nullptr) *out_taken = g_taken_yaw;
  if (out_absorbed != nullptr) *out_absorbed = g_absorbed_yaw;
  if (out_factor != nullptr) *out_factor = g_factor;
  if (out_pivot != nullptr) *out_pivot = g_pivot;
  if (out_mode != nullptr) *out_mode = g_mode;
  return g_seq.load(std::memory_order_acquire);
}

}  // namespace refii::vfs

extern "C" {

void refii_bridge_mouse_look_take(float* out_yaw, float* out_pitch) {
  using namespace refii::vfs;
  std::lock_guard<std::mutex> guard(g_lock);
  if ((g_seq.load(std::memory_order_relaxed) & 1u) == 0u) {
    g_seq.fetch_add(1u, std::memory_order_acq_rel);
  }
  if (out_yaw != nullptr) *out_yaw = static_cast<float>(g_pending_yaw);
  if (out_pitch != nullptr) *out_pitch = static_cast<float>(g_pending_pitch);
  g_pending_yaw = 0.0;
  g_pending_pitch = 0.0;
}

void refii_bridge_mouse_look_state(uint32_t tick, float taken_yaw, float held_pitch,
                                   float absorbed_yaw, float factor, float pivot, int32_t mode) {
  using namespace refii::vfs;
  std::lock_guard<std::mutex> guard(g_lock);
  g_taken_yaw += taken_yaw;
  g_absorbed_yaw += absorbed_yaw;
  g_pending_pitch += held_pitch;
  g_tick = tick;
  if (factor > 0.0f) {
    g_factor = factor;
  }
  g_pivot = pivot;
  g_mode = mode;
  if ((g_seq.load(std::memory_order_relaxed) & 1u) != 0u) {
    g_seq.fetch_add(1u, std::memory_order_acq_rel);
  }
}

int32_t refii_bridge_mouse_look_device(void) {
  return refii::vfs::g_device.load(std::memory_order_acquire) ? 1 : 0;
}

void refii_bridge_mouse_look_flicked(void) {
  refii::vfs::g_flicks.fetch_add(1u, std::memory_order_acq_rel);
}

}  // extern "C"
