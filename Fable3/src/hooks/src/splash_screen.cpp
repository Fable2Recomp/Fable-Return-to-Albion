// refii - Fable 3's "Press START" splash, stated to the host so Godot draws it.
//
// The splash F (vtable 0x82080AE0, built by the Startup state at S+0x48 -> +4) is a set of the
// 3D engine's render elements whose art, rects, strings and timings are all constants in the
// binary; what changes is F's own state, and that is what this states. Godot draws the screen
// (Titles/Fable3/Fable3Splash.cs) from the same constants.
//
// THE SITE. sub_82359848(F, ...) is F's per-frame update, called from the 3D Engine frame
// sub_822E1EA0 at 0x822E2220 with r3 = F (`lwz r3,4(r30)` at 0x822E2210) under F's lock. Every
// state change happens inside it or is observed by its next call:
//   F+0x288  0 build, 1 fade in (from the frame the build completes), 2 Press START (0.5 s
//            later), 3 START accepted (the input handler sub_82313128, 0x823134B8), 4, 5 done
//   F+0x265  set by the teardown sub_82359D00 once the Startup asks to close (F+0x264)
// The layout's two inputs are the guest's own: XVIDEO_MODE.fIsWideScreen at 0x8330F054
// (filled by XGetVideoMode at 0x82D4A2C8) and the back-buffer width at 0x83566C84 (0x82D4A364).

#include <cstdint>
#include <cstdio>
#include <cstring>

#include <rex/ppc.h>

#include "core/payload_bridge.h"

REX_EXTERN(__imp__rex_Splash_Update);

namespace {

/// The host-only bus kind Fable 3's native screens are stated on (Fable3Splash.StateKind).
constexpr int32_t kScreenStateKind = 1007;

constexpr uint32_t kState = 0x288u;
constexpr uint32_t kClosed = 0x265u;
constexpr uint32_t kWideScreen = 0x8330F054u;
constexpr uint32_t kBackBufferWidth = 0x83566C84u;

uint32_t LoadGuestU32(const uint8_t* base, uint32_t address) {
  uint32_t value = 0;
  std::memcpy(&value, base + address, sizeof(value));
  return __builtin_bswap32(value);
}

}  // namespace

REX_EXTERN(rex_Splash_Update) {
  const uint32_t splash = ctx.r3.u32;
  __imp__rex_Splash_Update(ctx, base);
  if (splash == 0u) {
    return;
  }

  // One splash at a time, updated only on the 3D Engine thread.
  static uint32_t last_splash = 0u;
  static uint32_t last_state = ~0u;
  static uint32_t last_closed = ~0u;

  const uint32_t state = LoadGuestU32(base, splash + kState);
  const uint32_t closed = base[splash + kClosed] != 0u ? 1u : 0u;
  if (splash == last_splash && state == last_state && closed == last_closed) {
    return;
  }

  last_splash = splash;
  last_state = state;
  last_closed = closed;

  char fields[128];
  const int length = std::snprintf(
      fields, sizeof(fields), "screen=splash,state=%u,closed=%u,widescreen=%u,width=%u", state,
      closed, LoadGuestU32(base, kWideScreen) != 0u ? 1u : 0u,
      LoadGuestU32(base, kBackBufferWidth));
  if (length > 0) {
    refii_bridge_mod_event_post(kScreenStateKind, fields, static_cast<size_t>(length));
  }
}
