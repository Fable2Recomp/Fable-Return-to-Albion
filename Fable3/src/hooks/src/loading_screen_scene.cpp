// refii - Fable 3's loading screen quads, cut: Godot draws the loading screen
// (Godot/src/Titles/Fable3/Fable3LoadingScreen.cs, stated by RefiiHostGetLoadingScreen/…Art from
// the screen's own fields), so the guest's quads for it are pixel work. The screen's state
// machine — fade, dwell, closes, done, its scene and the display it points — runs unchanged.
//
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
// WHO READS THE IDS BACK. Only the releases: sub_823022D0 (ls+236.., ls+220..),
// sub_82303AF8/B78/BF8/FD8, sub_82304058 and sub_82305CC0's own clear, each `sub_82C4F128(id)`,
// which returns at once for -1 (`if (result != -1)`). No screen function reads the twin table
// dword_8355F464; no host feed reads these twins (the screen's host statements are +0xD0, +0x150,
// +0x170, +0x19C, +0x1B1, level_feed.cpp). The builders' return values gate nothing. The screen's
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

#include <rex/logging.h>
#include <rex/ppc.h>

REX_EXTERN(__imp__rex_LoadingScreen_Update);
REX_EXTERN(__imp__rex_LoadingScreen_BuildQuad);
REX_EXTERN(__imp__rex_LoadingScreen_BuildTextQuads);
REX_EXTERN(__imp__rex_RenderTwin_Create);

namespace {

constexpr uint32_t kSceneTableBegin = 0x8355F454u;
constexpr uint32_t kSceneTableEnd = 0x8355F458u;
constexpr uint32_t kNoScene = 0xFFFFFFFFu;

constexpr uint32_t kLsScene = 0x0u;
constexpr uint32_t kLsDone = 0x19Du;
constexpr uint32_t kLsBorrowed = 0x1A6u;

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
