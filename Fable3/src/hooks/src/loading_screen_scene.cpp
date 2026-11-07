// refii - Fable 3's loading screen: stated at its own entry points for Godot, which draws it
// (Godot/src/Titles/Fable3/Fable3LoadingScreen.cs, through RefiiHostGetLoadingScreen/…Art reading
// the table core/payload_bridge.h names), and its quads cut. The screen's state machine — fade,
// dwell, closes, done, its scene and the display it points — runs unchanged; IsLoadingScreenActive
// (sub_82176F38) and every other guest reader see the guest's own object.

// Every address is the Fable 3 IDB; each offset names its base (ls = the screen, the r3 of its
// per-frame update sub_823022D0).
//
// THE QUADS. The screen's quad builders are sub_82302EB8 (one quad: 0x82302FE0, 0x82303034,
// 0x82303088) and sub_82305CC0 (a text run: 0x82306088, 0x82306108, 0x82306188, 0x82306208,
// 0x8230634C). Each site calls the twin create sub_821F17B0(desc r3, f1 time, r5 =
// slot[ls+0] read straight from dword_8355F454 with no test, r6 = -1 "allot an id", r7) and keeps
// the id it returns in one of the screen's id vectors (ls+232/248/264 through sub_82308B88; the
// a31 vector its callers pass — ls+280/296/312 and others). The create's own "nothing made" path
// is the descriptor making no object (`beq loc_821F192C` at 0x821F17F0) -> `mr r3, r31` with r31
// = r6 (0x821F17D0, 0x821F192C): it returns the requested id, -1 at every site above, and touches
// neither the scene, the twin table nor the id pool.
//
// THE STATEMENTS (offsets from ls; mgr = the manager, the r3 of its create and stop):
//   - Creates: sub_82307750(mgr, a2, a3, kind, a5) and sub_82307890(mgr, a2, kind), each making
//     the screen only while mgr+4 is empty (`if (!a1[1])` at 0x823077B8 / 0x823078F0) through its
//     constructor (sub_82300580 at 0x823077F4 / sub_82300768 at 0x82307928), which stores `kind`
//     at ls+0x170 (`stw` at 0x82300470 in sub_823002F0, reached with the create's kind), and
//     storing it at mgr+4 (0x82307814 / 0x82307950). Stated when mgr+4 changed across the call.
//   - Destruction: sub_82300968(ls), the one body every release runs (the stop's sub_82308260 at
//     0x823082F4, the creates' replace at 0x82307808 / 0x82307944, the manager's destructor
//     sub_82307638 at 0x82307684).
//   - The art: sub_823030B0(ls), run from sub_82300BB0 (0x82300C50) when the update finds ls+0x1A7
//     set (0x82302764), writes the CCharString handle ls+0x150 on every branch (sub_822295F0 at
//     0x82303144..0x823033B4, the E32010 form then formatted in place at 0x82303168); the kind it
//     switches on is ls+0x170 (0x8230319C).
//   - The update (r3 = ls) states, as it returns, what it moved: ls+0xD0 the cover opacity (up by
//     1/dbl_8329EA48 per update while it shows, 0x82302874; down by the same after the close,
//     0x82302380), ls+0x19C the close requested (set only by the close sub_823020D0 at 0x823020E8,
//     cleared by the update's teardown at 0x823024B8) and ls+0x19D finished (set at 0x82302530 /
//     0x82302570 beside the PulseEvent that releases the stop's waiter).
//   - The close sub_823020D0(ls), from the stop sub_82307B18(mgr, imm) (0x82307BE8) and from the
//     update's own dwell expiry (0x82302634), sets ls+0x19C and ls+0xD0 = 1.0 (0x823020F4): stated
//     as it returns, so a stop reaches the host before the next update.
//
// WHO READS THE IDS BACK. Only the releases: sub_823022D0 (ls+236.., ls+220..),
// sub_82303AF8/B78/BF8/FD8, sub_82304058 and sub_82305CC0's own clear, each `sub_82C4F128(id)`,
// which returns at once for -1 (`if (result != -1)`). No screen function reads the twin table
// dword_8355F464; no host feed reads these twins (the screen's statements are below). The builders' return values gate nothing. The screen's
// own scene (sub_82C4F1A8 in sub_82300BB0) and its view twin ls+4 (sub_82301B58, the camera the
// display names; the frame tests it, `a1 != -1 && twin[a1]`, sub_821988C8) are not quads and stay.
//
// THE FAULT IT REMOVES. A close that lands inside the screen's dwell tears an own-scene screen
// down in its next frame (0x82302354) and hands its scene to the particle thread for destruction
// (0x823024A8); the frame after, still inside the dwell (0x823025D4), the screen borrows the
// pending display (0x82302650..68) — that same scene — and its next quad refresh read the emptied
// slot: sub_82302EB8 -> sub_821F17B0 -> slot 13 (thunk sub_821BC5C0) -> sub_82258F00 ->
// sub_821DBAE0(0 + 1000), the read of 0x3EC at 0x821DBAEC (r7 refiii_012.log 15:24:18). Every
// other reader of that scene id tests the slot (the ring consumer sub_822A21A8, the display add
// sub_8219AC68 -> ring opcode 3). With the quads answered "nothing made", nothing reads it.
//
// The warning below marks the race still happening (the screen holding a destroyed scene); with
// the cut it is followed by no fault.

#include <atomic>
#include <cstdint>
#include <cstring>

#include <string>

#include <rex/logging.h>
#include <rex/ppc.h>

#include "core/payload_bridge.h"
#include "gui_statements.h"

REX_EXTERN(__imp__rex_LoadingScreen_Update);
REX_EXTERN(__imp__rex_LoadingScreen_Create);
REX_EXTERN(__imp__rex_LoadingScreen_CreateShown);
REX_EXTERN(__imp__rex_LoadingScreen_Destroy);
REX_EXTERN(__imp__rex_LoadingScreen_ChooseArt);
REX_EXTERN(__imp__rex_LoadingScreen_Close);
REX_EXTERN(__imp__rex_LoadingScreen_BuildQuad);
REX_EXTERN(__imp__rex_LoadingScreen_BuildTextQuads);
REX_EXTERN(__imp__rex_RenderTwin_Create);

namespace {

constexpr uint32_t kSceneTableBegin = 0x8355F454u;
constexpr uint32_t kSceneTableEnd = 0x8355F458u;
constexpr uint32_t kNoScene = 0xFFFFFFFFu;

constexpr uint32_t kLsScene = 0x0u;
constexpr uint32_t kLsFade = 0xD0u;
constexpr uint32_t kLsArt = 0x150u;
constexpr uint32_t kLsKind = 0x170u;
constexpr uint32_t kLsRequested = 0x19Cu;
constexpr uint32_t kLsDone = 0x19Du;
constexpr uint32_t kLsBorrowed = 0x1A6u;
constexpr uint32_t kMgrScreen = 0x4u;

/// Depth of the screen's quad builders on this thread (they run on the 3D Engine thread).
thread_local uint32_t t_building_quads = 0u;

std::atomic<uint64_t> g_reported_empty{~0ull};

uint32_t LoadGuestU32(const uint8_t* base, uint32_t address) {
  uint32_t value = 0;
  std::memcpy(&value, base + address, sizeof(value));
  return __builtin_bswap32(value);
}

/// The scene's slot, or kNoScene when the id lies outside the vector.
uint32_t SceneSlot(const uint8_t* base, uint32_t id) {
  const uint32_t begin = LoadGuestU32(base, kSceneTableBegin);
  const uint32_t end = LoadGuestU32(base, kSceneTableEnd);
  if (id == kNoScene || begin == 0u || end <= begin || id >= (end - begin) / 4u) {
    return kNoScene;
  }
  return LoadGuestU32(base, begin + id * 4u);
}

void StateUpdate(const uint8_t* base, uint32_t screen) {
  refii_bridge_loading_screen_updated(screen, refii::fable3::LoadGuestF32(base, screen + kLsFade),
                                      base[screen + kLsRequested] != 0u ? 1 : 0, base[screen + kLsDone] != 0u ? 1 : 0);
}

/// A create made a screen when mgr+4 changed across it.
void StateCreated(const uint8_t* base, uint32_t manager, uint32_t before, uint32_t kind) {
  const uint32_t screen = refii::fable3::Plausible(manager) ? LoadGuestU32(base, manager + kMgrScreen) : 0u;
  if (screen == 0u || screen == before) {
    return;
  }
  refii_bridge_loading_screen_created(manager, screen, kind);
  REXLOG_INFO("[fable3-loading] screen {:08X} created by manager {:08X}, kind {}", screen, manager, kind);
}

struct QuadScope {
  QuadScope() { ++t_building_quads; }
  ~QuadScope() { --t_building_quads; }
};

}  // namespace

REX_EXTERN(rex_LoadingScreen_Update) {
  const uint32_t screen = ctx.r3.u32;
  if (screen != 0u && base[screen + kLsDone] == 0u) {
    const uint32_t scene = LoadGuestU32(base, screen + kLsScene);
    const uint32_t slot = SceneSlot(base, scene);
    const uint64_t key = (static_cast<uint64_t>(screen) << 32) | scene;
    if (scene != kNoScene && (slot == 0u || slot == kNoScene) &&
        g_reported_empty.exchange(key, std::memory_order_relaxed) != key) {
      REXLOG_WARN("[ls-scene] loading screen 0x{:08X} holds scene {} (borrowed={}) and its slot is "
                  "EMPTY; its quads are cut, nothing reads it",
                  screen, static_cast<int32_t>(scene), base[screen + kLsBorrowed]);
    }
  }
  __imp__rex_LoadingScreen_Update(ctx, base);
  if (refii::fable3::Plausible(screen)) {
    StateUpdate(base, screen);
  }
}

// (mgr, a2, a3, kind, a5).
REX_EXTERN(rex_LoadingScreen_Create) {
  const uint32_t manager = ctx.r3.u32;
  const uint32_t kind = ctx.r6.u32;
  const uint32_t before = refii::fable3::Plausible(manager) ? LoadGuestU32(base, manager + kMgrScreen) : 0u;
  __imp__rex_LoadingScreen_Create(ctx, base);
  StateCreated(base, manager, before, kind);
}

// (mgr, a2, kind).
REX_EXTERN(rex_LoadingScreen_CreateShown) {
  const uint32_t manager = ctx.r3.u32;
  const uint32_t kind = ctx.r5.u32;
  const uint32_t before = refii::fable3::Plausible(manager) ? LoadGuestU32(base, manager + kMgrScreen) : 0u;
  __imp__rex_LoadingScreen_CreateShown(ctx, base);
  StateCreated(base, manager, before, kind);
}

// (ls): the screen's destructor body.
REX_EXTERN(rex_LoadingScreen_Destroy) {
  const uint32_t screen = ctx.r3.u32;
  refii_bridge_loading_screen_destroyed(screen);
  REXLOG_INFO("[fable3-loading] screen {:08X} destroyed", screen);
  __imp__rex_LoadingScreen_Destroy(ctx, base);
}

// (ls): the art decision, written into ls+0x150.
REX_EXTERN(rex_LoadingScreen_ChooseArt) {
  const uint32_t screen = ctx.r3.u32;
  __imp__rex_LoadingScreen_ChooseArt(ctx, base);
  if (!refii::fable3::Plausible(screen)) {
    return;
  }
  const std::string art = refii::fable3::ReadCharString(base, screen + kLsArt, 255u);
  const uint32_t kind = LoadGuestU32(base, screen + kLsKind);
  refii_bridge_loading_screen_art(screen, kind, LoadGuestU32(base, screen + kLsArt), art.data(),
                                  static_cast<uint32_t>(art.size()));
  REXLOG_INFO("[fable3-loading] screen {:08X} chose '{}' (kind {})", screen, art, kind);
}

// (ls): the close, from the stop or the dwell's expiry.
REX_EXTERN(rex_LoadingScreen_Close) {
  const uint32_t screen = ctx.r3.u32;
  __imp__rex_LoadingScreen_Close(ctx, base);
  if (refii::fable3::Plausible(screen)) {
    StateUpdate(base, screen);
  }
}

REX_EXTERN(rex_LoadingScreen_BuildQuad) {
  QuadScope scope;
  __imp__rex_LoadingScreen_BuildQuad(ctx, base);
}

REX_EXTERN(rex_LoadingScreen_BuildTextQuads) {
  QuadScope scope;
  __imp__rex_LoadingScreen_BuildTextQuads(ctx, base);
}

REX_EXTERN(rex_RenderTwin_Create) {
  if (t_building_quads != 0u) {
    // The create's "nothing made" answer: the requested id, untouched (0x821F192C `mr r3, r31`).
    ctx.r3.u64 = ctx.r6.u64;
    return;
  }
  __imp__rex_RenderTwin_Create(ctx, base);
}
