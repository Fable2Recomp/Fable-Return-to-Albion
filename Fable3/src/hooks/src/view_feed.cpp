// refii - Fable 3's world view, stated to the host: the camera the guest renders with, each new
// guest frame, and its preload gate, which waits while Godot builds the level.
//
// THE CAMERA (Fable 3 IDB). sub_82261CB8(V, block) fills the view's render camera from the final
// blended camera block, every high-frequency step (0x82256B44, after the blend sub_821A9048) and
// on a region transition (0x82349EA4, the live block unblended); it skips a view whose +0xC is -1
// (`cmpwi r11,-1` at 0x82261CD8). The block is the camera manager's 0x40-byte layout:
//   +0x00 position, +0x10 up, +0x20 forward (vec4 each, w unused), +0x30 horizontal FOV at 4:3 in
//   radians (`lfs f30,0x30(r4)` at 0x82261CE4), +0x34 snap byte (the blend forces alpha 1 on it).
// Scripted cameras (Lua ActivateCamera, the manager's override slot) reach the same call.
//
// ONE VIEW PER GAME, AND ONLY THE PRESENTED ONE IS FOLLOWED. The front end keeps two games standing
// (presented_world.h in the host bridge); each fills its own view, V = *(*(game+0x10)+4) (the
// high-frequency caller: `mr r31,r3` at 0x82256AE0, `lwz r30,0x10(r31)` / `lwz r3,4(r30)` before
// the call). The camera stated is the one whose game was last made current, *0x83556534.
//
// THE PRELOAD GATE. sub_822E18E0(engine) answers "the 3D Engine is still preloading": 1 while
// engine+0xC8 < 5 or engine+0x264 is set (0x822E1904 / 0x822E1910). The level-change command
// waits on it in its state 9 (`bl sub_822E18E0` at 0x82722D74, `bne` stays), the load-from-save
// command in its state 4, and the game tick's loading-screen fade holds on it. Answering 1 while
// the host builds keeps the guest in its own loading screen, the hold Fable 2's bridge makes.

#include <cstdint>
#include <cstring>

#include <rex/ppc.h>

#include "core/payload_bridge.h"

REX_EXTERN(__imp__rex_View_SetRenderCamera);
REX_EXTERN(__imp__rex_Camera_Blend);
REX_EXTERN(__imp__rex_GameLoop_HighFrequencyUpdate);
REX_EXTERN(__imp__rex_Engine_IsPreLoading);

namespace {

constexpr uint32_t kPresentedGame = 0x83556534u;

uint32_t LoadGuestU32(const uint8_t* base, uint32_t address) {
  uint32_t value = 0;
  std::memcpy(&value, base + address, sizeof(value));
  return __builtin_bswap32(value);
}

float LoadGuestF32(const uint8_t* base, uint32_t address) {
  const uint32_t bits = LoadGuestU32(base, address);
  float value = 0.0f;
  std::memcpy(&value, &bits, sizeof(value));
  return value;
}

void LoadGuestVec3(const uint8_t* base, uint32_t address, float* out) {
  for (uint32_t i = 0; i < 3u; ++i) {
    out[i] = LoadGuestF32(base, address + (i * 4u));
  }
}

/// One 0x40-byte camera block in the stated 11-float layout.
void ReadCameraBlock(const uint8_t* base, uint32_t block, float* out) {
  LoadGuestVec3(base, block + 0x00u, out);
  LoadGuestVec3(base, block + 0x10u, out + 3);
  LoadGuestVec3(base, block + 0x20u, out + 6);
  out[9] = LoadGuestF32(base, block + 0x30u);
  out[10] = base[block + 0x34u] != 0u ? 1.0f : 0.0f;
}

/// The view of the game last made current (sub_82344C48: `stw r26,dword_83556534` at 0x82344C90).
uint32_t PresentedView(const uint8_t* base) {
  const uint32_t game = LoadGuestU32(base, kPresentedGame);
  const uint32_t holder = game == 0u ? 0u : LoadGuestU32(base, game + 0x10u);
  return holder == 0u ? 0u : LoadGuestU32(base, holder + 4u);
}

}  // namespace

REX_EXTERN(rex_View_SetRenderCamera) {
  const uint32_t view = ctx.r3.u32;
  const uint32_t block = ctx.r4.u32;
  if (view != 0u && block != 0u && view == PresentedView(base) &&
      LoadGuestU32(base, view + 0xCu) != 0xFFFFFFFFu) {
    float position[3];
    float up[3];
    float forward[3];
    LoadGuestVec3(base, block + 0x00u, position);
    LoadGuestVec3(base, block + 0x10u, up);
    LoadGuestVec3(base, block + 0x20u, forward);
    refii_bridge_camera_stated(position, up, forward, LoadGuestF32(base, block + 0x30u),
                               base[block + 0x34u] != 0u ? 1 : 0);
  }

  __imp__rex_View_SetRenderCamera(ctx, base);
}

// THE PAIR. sub_821A9048(out, previous, live, alpha) is the high-frequency blend: called at
// 0x82256B38 with r4 = game+0xD0 (the camera latched last step, `addi r4,r31,0xD0` at
// 0x82256B28) and r5 = the camera manager's block copied this step (sub_821CB288 at 0x82256B20),
// f1 the alpha; the live block's +0x34 byte forces alpha 1 (a cut). The shell blends the two on
// its own clock, as it does for Fable 2's pair, so only the presented game's call is stated —
// sub_8253B940 also blends, for something else.
REX_EXTERN(rex_Camera_Blend) {
  const uint32_t previous = ctx.r4.u32;
  const uint32_t live = ctx.r5.u32;
  const uint32_t presented = LoadGuestU32(base, kPresentedGame);
  if (presented != 0u && previous == presented + 0xD0u && live != 0u) {
    float stated_live[11];
    float stated_previous[11];
    ReadCameraBlock(base, live, stated_live);
    ReadCameraBlock(base, previous, stated_previous);
    refii_bridge_camera_pair_stated(stated_live, stated_previous);
  }

  __imp__rex_Camera_Blend(ctx, base);
}

// THE GUEST FRAME. sub_82256AC0(game) is the main loop's high-frequency pass (its one caller,
// sub_821CB758 at 0x821CBEE8): the camera blend and publish above, the posed state of the step.
// The guest keeps no counter of it, so this is where the host learns a new frame of guest state
// is in memory — what Fable 2's rex_lhGameLoop_HighFrequencyUpdate states.
REX_EXTERN(rex_GameLoop_HighFrequencyUpdate) {
  __imp__rex_GameLoop_HighFrequencyUpdate(ctx, base);
  refii_bridge_note_guest_frame();
}

REX_EXTERN(rex_Engine_IsPreLoading) {
  __imp__rex_Engine_IsPreLoading(ctx, base);
  if (refii_bridge_world_build_pending() != 0) {
    ctx.r3.u64 = 1;
  }
}
