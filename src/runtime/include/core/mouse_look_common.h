// refii — shared mouse-look helpers and constants (src/runtime/include/core/mouse_look_common.h).
//
// Header-only and restricted to standard library headers so both payload hooks (Fable 2 and Fable 3)
// compile it directly into their generated payload module without importing any C++ symbols across
// the payload/host boundary (core/payload_bridge.h).

#pragma once

#include <chrono>
#include <cmath>
#include <cstdint>

namespace refii::mouselook {

/// How the host draws a turn not yet taken (matching Engine_MouseLook.Mode* and mouse_look_state.cpp).
constexpr int32_t kModeNone = 0;
constexpr int32_t kModeOrbit = 1;
constexpr int32_t kModeInPlace = 2;
constexpr int32_t kModeAim = 3;

/// SACCamera:ProcessGameAction's height dead zone (`y > 0.1 or y < -0.1`).
constexpr float kHeightDeadZone = 0.1f;

/// A flick that switches a target lock's target: this much turn in one direction inside the window.
constexpr double kFlickDegrees = 40.0;
constexpr double kFlickWindowSeconds = 0.2;

/// Flag bits carried in MouseLookState[18].
constexpr uint32_t kFlagLookInverted = 1u << 0;
constexpr uint32_t kFlagHostAimApplied = 1u << 1;
constexpr uint32_t kFlagHasHead = 1u << 2;

/// A target lock's flick accumulator: collects turns in one direction inside a time window and
/// emits at most one switch (-1 / +1) per deliberate flick.
struct TargetLockFlickTracker {
  uint32_t handle = 0u;
  double sum = 0.0;
  double window = 0.0;
  std::chrono::steady_clock::time_point last{};
  bool seen = false;
  /// The push that handed a flick, in its direction (+1 / -1); 0 when none is spent.
  int spent = 0;

  void Reset() { *this = TargetLockFlickTracker{}; }

  void ClearSpent() {
    spent = 0;
    sum = 0.0;
  }

  /// Accumulates `yaw` for `current_handle`. Returns -1 or +1 when a flick triggers, else 0.
  int Collect(uint32_t current_handle, float yaw) {
    const auto now = std::chrono::steady_clock::now();
    const double since = seen && handle == current_handle
                             ? std::chrono::duration<double>(now - last).count()
                             : 0.0;
    if (handle != current_handle) {
      *this = TargetLockFlickTracker{};
      handle = current_handle;
    }
    last = now;
    seen = true;

    const int direction = yaw > 0.0f ? 1 : (yaw < 0.0f ? -1 : 0);
    if (direction == 0 || (spent != 0 && spent != direction)) {
      spent = 0;
    }
    if (direction == 0 || spent != 0) {
      sum = 0.0;
      window = 0.0;
      return 0;
    }

    // Only the share inside kFlickWindowSeconds counts, so a slow drag never adds up to a flick.
    double share = yaw;
    if (since > kFlickWindowSeconds) {
      share *= kFlickWindowSeconds / since;
    }
    if ((sum > 0.0) != (share > 0.0) || window + since > kFlickWindowSeconds) {
      sum = 0.0;
      window = 0.0;
    } else {
      window += since;
    }
    sum += share;
    if (std::fabs(sum) < kFlickDegrees) {
      return 0;
    }

    spent = direction;
    sum = 0.0;
    window = 0.0;
    return direction;
  }
};

/// Single-waiting-notch wheel switcher for target locks.
struct WheelSwitchQueue {
  uint32_t handle = 0u;
  int waiting = 0;

  void Reset() { *this = WheelSwitchQueue{}; }

  /// Consumes `notches` against `clear_to_switch`. Returns true with `*out_send_dir` when a switch
  /// command should be sent to the camera script.
  bool Step(uint32_t current_handle, int32_t notches, bool clear_to_switch, int* out_send_dir) {
    if (handle != current_handle) {
      *this = WheelSwitchQueue{};
      handle = current_handle;
    }
    const int direction = notches > 0 ? 1 : -1;
    int count = (notches > 0 ? notches : -notches) + (waiting != 0 ? 1 : 0);
    const int first = waiting != 0 ? waiting : direction;
    const int latest = notches != 0 ? direction : waiting;
    bool sent = false;
    if (count > 0 && clear_to_switch) {
      if (out_send_dir != nullptr) {
        *out_send_dir = first;
      }
      --count;
      sent = true;
    }
    waiting = count > 0 ? latest : 0;
    return sent;
  }
};

/// Double-precision rigid yaw-then-pitch rotation about `pivot[3]`, matching
/// RangedCombatCamera.UpdateCurrentDirection's turn math for 1-tick entry fallback.
struct AimTurn {
  float pivot[3] = {0.0f, 0.0f, 0.0f};
  double cy = 1.0, sy = 0.0, ct = 1.0, st = 0.0, rx = 0.0, ry = 0.0;

  AimTurn(const float* at, double yaw_degrees, double tilt_degrees, double right_x, double right_y) {
    constexpr double kRadians = 3.14159265358979323846 / 180.0;
    for (int i = 0; i < 3; ++i) {
      pivot[i] = at[i];
    }
    // Rightward is clockwise seen from above: a negative turn about +Z.
    cy = std::cos(-yaw_degrees * kRadians);
    sy = std::sin(-yaw_degrees * kRadians);
    ct = std::cos(tilt_degrees * kRadians);
    st = std::sin(tilt_degrees * kRadians);
    rx = right_x;
    ry = right_y;
  }

  /// Turns `point[3]` in place about `pivot`; returns true if finite and updated.
  bool ApplyPoint(float point[3]) const {
    if (!std::isfinite(point[0]) || !std::isfinite(point[1]) || !std::isfinite(point[2])) {
      return false;
    }
    const double vx = static_cast<double>(point[0]) - pivot[0];
    const double vy = static_cast<double>(point[1]) - pivot[1];
    const double vz = static_cast<double>(point[2]) - pivot[2];
    const double ax = vx * cy - vy * sy;
    const double ay = vx * sy + vy * cy;
    const double az = vz;
    // Rodrigues about k = (rx, ry, 0): v cos t + (k x v) sin t + k (k . v)(1 - cos t).
    const double dot = ax * rx + ay * ry;
    const double kx = ry * az;
    const double ky = -rx * az;
    const double kz = rx * ay - ry * ax;
    const double out[3] = {ax * ct + kx * st + rx * dot * (1.0 - ct) + pivot[0],
                           ay * ct + ky * st + ry * dot * (1.0 - ct) + pivot[1],
                           az * ct + kz * st + pivot[2]};
    if (!std::isfinite(out[0]) || !std::isfinite(out[1]) || !std::isfinite(out[2])) {
      return false;
    }
    for (int i = 0; i < 3; ++i) {
      point[i] = static_cast<float>(out[i]);
    }
    return true;
  }
};

/// Re-projects `point[3]` onto the ray starting at `cam_pos[3]` along `unit_fwd[3]` while
/// preserving its scalar distance `|point - cam_pos|`. Used under host-authoritative ADS so guest
/// aim/raycast points align immediately with the host view direction.
inline bool AlignPointAlongRay(const float cam_pos[3], const float unit_fwd[3], float point[3]) {
  for (int i = 0; i < 3; ++i) {
    if (!std::isfinite(cam_pos[i]) || !std::isfinite(unit_fwd[i]) || !std::isfinite(point[i])) {
      return false;
    }
  }
  const double flen_sq = static_cast<double>(unit_fwd[0]) * unit_fwd[0] +
                         static_cast<double>(unit_fwd[1]) * unit_fwd[1] +
                         static_cast<double>(unit_fwd[2]) * unit_fwd[2];
  if (!(flen_sq > 1e-8)) {
    return false;
  }
  const double inv_flen = 1.0 / std::sqrt(flen_sq);
  const double fx = unit_fwd[0] * inv_flen;
  const double fy = unit_fwd[1] * inv_flen;
  const double fz = unit_fwd[2] * inv_flen;

  const double dx = static_cast<double>(point[0]) - cam_pos[0];
  const double dy = static_cast<double>(point[1]) - cam_pos[1];
  const double dz = static_cast<double>(point[2]) - cam_pos[2];
  const double dist = std::sqrt(dx * dx + dy * dy + dz * dz);
  if (!(dist > 1e-4 && dist < 10000.0)) {
    return false;
  }

  point[0] = static_cast<float>(cam_pos[0] + fx * dist);
  point[1] = static_cast<float>(cam_pos[1] + fy * dist);
  point[2] = static_cast<float>(cam_pos[2] + fz * dist);
  return true;
}

}  // namespace refii::mouselook
