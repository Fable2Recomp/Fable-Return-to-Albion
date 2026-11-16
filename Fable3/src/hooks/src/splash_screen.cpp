// refii - Fable 3's "Press START" splash, stated to the host so Godot draws it.
//
// The splash F (vtable 0x82096D6C, built by the Startup state S: its holder H = *(S+0x48), F =
// *(H+4), H+8 F's lock) is a set of the 3D engine's render elements whose art, rects, strings and
// timings are all constants in the binary; what changes is F's state, F+0x288, and that is what
// this states. Godot draws the screen (Titles/Fable3/Fable3Splash.cs) from the same constants.
//
// THE STATE'S WRITERS (every `stw ..,0x288(..)` on F in the image):
//   0  the constructor sub_825321D8 (0x825322B8)
//   1  the build disc sub_82359F60, from F's update, the frame the build completes (disc 0x8235AA04)
//   2  F's update, 0.5 s after 1 (disc 0x82359978); and the Startup's back-to-the-prompt
//      sub_82534560(H) (0x82534594), from disc sub_82312B80 at disc 0x82312E60 when sub_82318D30 refuses
//   3  START accepted: the Startup's input handler disc sub_82313128(S, event) (vtable slot at
//      0x8207AF24) stores it at disc 0x823134B8 and stamps F+0x290 (disc 0x823134E0)
//   4  the Startup's go-on sub_82534500(H) (0x82534534), from disc sub_82312B80 at disc 0x82312E58
//   5  F's update, when the 4 timer at F+0x290 runs out (disc 0x82359B64)
// and F+0x265 "closed": F's update sets it beside the teardown sub_82532A88 (0x82532A30) once the
// Startup's close sub_82534198(H) has set F+0x264 (0x825341CC).
//
// THE STATEMENTS. 2 (back to the prompt), 3 and 4 are the Startup's calls, stated at them: the
// setters by the value each stores, the input handler when F+0x288 became 3 across it. 0 -> 1 ->
// 2, 4 -> 5 and "closed" have no source but F's own build, timers and teardown inside its update
// sub_825324F8(F, ...) (called from the 3D Engine frame sub_822E1978 at disc 0x822E2220 with r3 = F),
// so they are stated as that update returns. The layout's two inputs are the guest's own globals:
// XVIDEO_MODE.fIsWideScreen at 0x8334FB48 (filled by XGetVideoMode at 0x82D7C918) and the
// back-buffer width at 0x835A78B0 (0x82D7C9B4).

#include <cstdint>
#include <cstdio>
#include <cstring>
#include <mutex>

#include <rex/ppc.h>

#include "core/payload_bridge.h"
#include "../../vfs/include/fable3_guest_build.h"

REX_EXTERN(__imp__rex_Splash_Update);
REX_EXTERN(__imp__rex_Splash_Input);
REX_EXTERN(__imp__rex_Splash_BackToPrompt);
REX_EXTERN(__imp__rex_Splash_GoOn);
// TU3 only: the START accept (S, pad) its input and its Startup update both call; the disc's is inline
// in its input, so the disc payload loads without it.
extern "C" __attribute__((weak)) REX_FUNC(__imp__rex_Splash_Accept);

namespace {

/// The host-only bus kind Fable 3's native screens are stated on (Fable3Splash.StateKind).
constexpr int32_t kScreenStateKind = 1007;

constexpr uint32_t kStartupHolder = 0x48u;
constexpr uint32_t kHolderSplash = 0x4u;
constexpr uint32_t kState = 0x288u;
constexpr uint32_t kClosed = 0x265u;
const refii::guest::Addr kWideScreen{0x8334FB48u};
const refii::guest::Addr kBackBufferWidth{0x835A78B0u};

constexpr uint32_t kStateGoOn = 4u;
constexpr uint32_t kStatePrompt = 2u;

bool Plausible(uint32_t address) { return address >= 0x10000u && address < 0xC0000000u; }

uint32_t LoadGuestU32(const uint8_t* base, uint32_t address) {
  uint32_t value = 0;
  std::memcpy(&value, base + address, sizeof(value));
  return __builtin_bswap32(value);
}

/// F from the Startup's holder H.
uint32_t SplashOf(const uint8_t* base, uint32_t holder) {
  return Plausible(holder) ? LoadGuestU32(base, holder + kHolderSplash) : 0u;
}

/// F from the Startup state S.
uint32_t SplashOfStartup(const uint8_t* base, uint32_t startup) {
  return Plausible(startup) ? SplashOf(base, LoadGuestU32(base, startup + kStartupHolder)) : 0u;
}

std::mutex g_lock;
uint32_t g_last_splash = 0u;
uint32_t g_last_state = ~0u;
uint32_t g_last_closed = ~0u;

/// Posts F's state when it differs from the last posted. The update runs on the 3D Engine
/// thread, the Startup's calls on the game thread.
void Post(const uint8_t* base, uint32_t splash, uint32_t state) {
  if (!Plausible(splash)) {
    return;
  }
  const uint32_t closed = base[splash + kClosed] != 0u ? 1u : 0u;
  {
    std::lock_guard<std::mutex> guard(g_lock);
    if (splash == g_last_splash && state == g_last_state && closed == g_last_closed) {
      return;
    }
    g_last_splash = splash;
    g_last_state = state;
    g_last_closed = closed;
  }

  char fields[128];
  const int length = std::snprintf(
      fields, sizeof(fields), "screen=splash,state=%u,closed=%u,widescreen=%u,width=%u", state,
      closed, LoadGuestU32(base, kWideScreen) != 0u ? 1u : 0u,
      LoadGuestU32(base, kBackBufferWidth));
  if (length > 0) {
    refii_bridge_mod_event_post(kScreenStateKind, fields, static_cast<size_t>(length));
  }
}

}  // namespace

namespace refii::fable3::splash {
/// The "Press START" splash stands: built and not yet closed (F+0x265), as last stated.
bool Up() {
  std::lock_guard<std::mutex> guard(g_lock);
  return g_last_splash != 0u && g_last_closed == 0u;
}
}  // namespace refii::fable3::splash

// (F, engine): F's build, timers and teardown — the transitions with no other source.
REX_EXTERN(rex_Splash_Update) {
  const uint32_t splash = ctx.r3.u32;
  __imp__rex_Splash_Update(ctx, base);
  if (Plausible(splash)) {
    Post(base, splash, LoadGuestU32(base, splash + kState));
  }
}

/// Runs one of the Startup's calls (S in r3) and posts F+0x288 when it changed across it.
template <typename Call>
void PostAcross(PPCContext& ctx, uint8_t* base, Call call) {
  const uint32_t startup = ctx.r3.u32;
  const uint32_t splash = SplashOfStartup(base, startup);
  const uint32_t before = Plausible(splash) ? LoadGuestU32(base, splash + kState) : ~0u;
  call();
  if (Plausible(splash) && SplashOfStartup(base, startup) == splash) {
    const uint32_t after = LoadGuestU32(base, splash + kState);
    if (after != before) {
      Post(base, splash, after);
    }
  }
}

// (S, event): START accepted (F+0x288 = 3; TU3 through rex_Splash_Accept, the disc inline at disc
// 0x823134B8), and on TU3 button 12 back to the prompt (`stw r11,0x288(r9)` = 2, 0x82319B88).
REX_EXTERN(rex_Splash_Input) {
  PostAcross(ctx, base, [&] { __imp__rex_Splash_Input(ctx, base); });
}

// TU3 sub_82319C38(S, pad): F+0x288 = 3 (`stw r11,0x288(r31)` 0x8231A220), from the input (0x82319BE4)
// and from the Startup's update (0x82319788), which no input hook sees.
REX_EXTERN(rex_Splash_Accept) {
  if (&__imp__rex_Splash_Accept == nullptr) {
    return;
  }
  PostAcross(ctx, base, [&] { __imp__rex_Splash_Accept(ctx, base); });
}

// (H): F+0x288 = 2 under F's lock (0x82534594).
REX_EXTERN(rex_Splash_BackToPrompt) {
  const uint32_t splash = SplashOf(base, ctx.r3.u32);
  __imp__rex_Splash_BackToPrompt(ctx, base);
  Post(base, splash, kStatePrompt);
}

// (H): F+0x288 = 4 under F's lock (0x82534534).
REX_EXTERN(rex_Splash_GoOn) {
  const uint32_t splash = SplashOf(base, ctx.r3.u32);
  __imp__rex_Splash_GoOn(ctx, base);
  Post(base, splash, kStateGoOn);
}
