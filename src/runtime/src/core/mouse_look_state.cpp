// refii — the mouse's camera turn, as SHARED STATE.
//
// In librefii_runtime for the reason decal_state.cpp gives, in the other direction too: the HOST
// posts the turn the mouse made (the host bridge, on Godot's thread) and the PAYLOAD hands it to
// the local player's camera (Fable2/src/vfs/src/hooks/mouse_look.cpp, on the guest's tick thread), then states
// what the camera made of it.
//
// THE CONTRACT. The host posts degrees of turn at the profile's middle camera sensitivity
// (rightward and upward positive). Once per camera update the payload takes ALL of it (`taken`,
// in posted degrees) and states the turn the camera made for it in that same update (`absorbed`,
// the camera's own degrees, rightward and upward), the camera's degrees per posted degree, and
// how a turn of this camera is drawn: about the vertical through its focus at `pivot_forward`
// metres along the level line of sight (ORBIT), about its own position (IN PLACE), rigidly about
// the stated point with the ranged aim turning with it (AIM: the yaw about the vertical through
// it, then the pitch about the level right axis through it, bounded at `pitch_limit` degrees of
// elevation), or not at all (NONE — the camera takes no look input now). Vertical turn under the
// third-person camera's own dead zone is returned to the pending turn rather than taken. Posted
// and taken share one origin, so `(posted - taken) * factor` is exactly the turn no camera update
// has taken yet. `seq` is odd from the take until the update's statement, so a host read that
// straddles an update is seen and refused.
//
// THE DEVICE. The host also states whether the mouse is the player's look device (the device
// switch: the keyboard and mouse were touched last). While it is, the camera it drives keeps its
// heading as the hero moves, the ranged aim turns with the view, and a target lock takes the
// mouse as flicks (mouse_look.cpp); a pad keeps the shipped camera. `flicks` counts the target
// switches handed to a lock, a run total.
//
// THE WHEEL. The host posts the mouse wheel's notches while the mouse is the camera's (captured),
// rightward positive: a notch down +1, up -1. Each camera update takes them all; they switch the
// target of a lock a mod has stated switches by the wheel (refii_mod_wheel_switches_target), and
// are dropped otherwise — the wheel has no other use in play.

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
double g_posted_pitch = 0.0;
double g_taken_pitch = 0.0;

/// Everything the camera turned for the mouse, in its own degrees, rightward and upward.
double g_absorbed_yaw = 0.0;
double g_absorbed_pitch = 0.0;

uint32_t g_tick = 0u;
float g_factor = 1.0f;
float g_pivot_forward = 0.0f;
float g_pivot_right = 0.0f;
float g_pivot_up = 0.0f;
float g_pitch_limit = 0.0f;
int32_t g_mode = 0;
uint32_t g_aim = 0u;

std::atomic<uint32_t> g_seq{0u};

std::atomic<bool> g_device{false};
std::atomic<uint32_t> g_flicks{0u};

/// Wheel notches posted and not yet taken, rightward positive.
std::atomic<int32_t> g_wheel{0};

}  // namespace

/// Adds the mouse's turn since the last post.
void MouseLookPost(float yaw_degrees, float pitch_degrees) {
  std::lock_guard<std::mutex> guard(g_lock);
  g_pending_yaw += yaw_degrees;
  g_pending_pitch += pitch_degrees;
  g_posted_yaw += yaw_degrees;
  g_posted_pitch += pitch_degrees;
}

/// Whether the mouse is the player's look device, as the host's device switch last stated it.
void MouseLookSetDevice(bool mouse) { g_device.store(mouse, std::memory_order_release); }

/// Adds wheel notches, rightward positive (a notch down +1, up -1).
void MouseLookWheel(int32_t notches) { g_wheel.fetch_add(notches, std::memory_order_acq_rel); }

/// The account as of the last camera update, into `out` in the order the host bridge documents
/// (RefiiHostGetMouseLook). Returns the sequence; odd means an update was mid-way.
uint32_t MouseLookState(double* out, uint32_t count) {
  std::lock_guard<std::mutex> guard(g_lock);
  const double values[] = {
      static_cast<double>(g_tick),
      g_posted_yaw,
      g_taken_yaw,
      g_absorbed_yaw,
      g_posted_pitch,
      g_taken_pitch,
      g_absorbed_pitch,
      static_cast<double>(g_factor),
      static_cast<double>(g_pivot_forward),
      static_cast<double>(g_pivot_right),
      static_cast<double>(g_pivot_up),
      static_cast<double>(g_mode),
      static_cast<double>(g_flicks.load(std::memory_order_acquire)),
      static_cast<double>(g_pitch_limit),
      static_cast<double>(g_aim),
  };
  constexpr uint32_t kValues = sizeof(values) / sizeof(values[0]);
  for (uint32_t i = 0; out != nullptr && i < count && i < kValues; ++i) {
    out[i] = values[i];
  }
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

void refii_bridge_mouse_look_state(uint32_t tick, float taken_yaw, float taken_pitch,
                                   float held_pitch, float absorbed_yaw, float absorbed_pitch,
                                   float factor, float pivot_forward, float pivot_right,
                                   float pivot_up, float pitch_limit, int32_t mode,
                                   uint32_t aim) {
  using namespace refii::vfs;
  std::lock_guard<std::mutex> guard(g_lock);
  g_taken_yaw += taken_yaw;
  g_taken_pitch += taken_pitch - held_pitch;
  g_absorbed_yaw += absorbed_yaw;
  g_absorbed_pitch += absorbed_pitch;
  g_pending_pitch += held_pitch;
  g_tick = tick;
  if (factor > 0.0f) {
    g_factor = factor;
  }
  g_pivot_forward = pivot_forward;
  g_pivot_right = pivot_right;
  g_pivot_up = pivot_up;
  g_pitch_limit = pitch_limit;
  g_mode = mode;
  g_aim = aim;
  if ((g_seq.load(std::memory_order_relaxed) & 1u) != 0u) {
    g_seq.fetch_add(1u, std::memory_order_acq_rel);
  }
}

int32_t refii_bridge_mouse_look_device(void) {
  return refii::vfs::g_device.load(std::memory_order_acquire) ? 1 : 0;
}

int32_t refii_bridge_mouse_look_take_wheel(void) {
  return refii::vfs::g_wheel.exchange(0, std::memory_order_acq_rel);
}

void refii_bridge_mouse_look_flicked(void) {
  refii::vfs::g_flicks.fetch_add(1u, std::memory_order_acq_rel);
}

}  // extern "C"
