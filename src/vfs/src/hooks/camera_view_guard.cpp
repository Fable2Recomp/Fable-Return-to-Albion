#include "vfs_hooks_private.h"

#include <atomic>
#include <cstdio>
#include <cstring>
#include <string>

#include "core/guest_state.h"
#include "hooks/ppc_externs.h"

namespace refii::vfs {

namespace {

bool PlausibleGuestPointer(uint32_t value) {
  return value >= 0x10000u && value < 0xC0000000u;
}

// The camera manager the world update ticks: game = *(*(0x83496AB8)+12), the view keyed 1
// (2 in split screen) in the container at game+0x9C, then *(view+4). Same walk sub_821D4628
// makes before calling sub_821B9258 — see resolve_camera_manager in host_bridge.cpp.
uint32_t ResolveCameraManager(uint8_t* base, uint32_t game) {
  uint32_t key = 1u;
  const uint32_t split = LoadGuestU32(base, game + 0x8Cu);
  if (PlausibleGuestPointer(split) && LoadGuestU8(base, split + 0x34u) != 0 &&
      LoadGuestU8(base, split + 0x36u) != 0) {
    key = 2u;
  }
  const uint32_t container = LoadGuestU32(base, game + 0x9Cu);
  if (!PlausibleGuestPointer(container)) {
    return 0;
  }
  const uint32_t begin = LoadGuestU32(base, container + 0x08u);
  const uint32_t end = LoadGuestU32(base, container + 0x0Cu);
  if (!PlausibleGuestPointer(begin) || end <= begin || (end - begin) > 0x1000u) {
    return 0;
  }
  for (uint32_t entry = begin; entry + 12u <= end; entry += 12u) {
    if (LoadGuestU32(base, entry) != key) {
      continue;
    }
    const uint32_t view = LoadGuestU32(base, entry + 4u);
    if (!PlausibleGuestPointer(view)) {
      return 0;
    }
    const uint32_t manager = LoadGuestU32(base, view + 4u);
    return PlausibleGuestPointer(manager) ? manager : 0u;
  }
  return 0;
}

}  // namespace

void TraceCameraProbe(uint8_t* base, uint64_t tick) {
  if (base == nullptr || tick < 600u || (tick % 150u) != 0u) {
    return;
  }
  const uint32_t mode = LoadGuestU32(base, 0x83496AB8u);
  if (!PlausibleGuestPointer(mode)) {
    return;
  }
  const uint32_t game = LoadGuestU32(base, mode + 0x0Cu);
  if (!PlausibleGuestPointer(game)) {
    return;
  }
  const uint32_t manager = ResolveCameraManager(base, game);
  const uint32_t view_primary = manager != 0u ? LoadGuestU32(base, manager + 60u) : 0u;
  const uint32_t view_fallback = manager != 0u ? LoadGuestU32(base, manager + 44u) : 0u;
  const uint32_t memo_tick = manager != 0u ? LoadGuestU32(base, manager + 0xD0u) : 0u;
  REXLOG_INFO(
      "Refii camera probe: tick={} game=0x{:08X} +0x104={} +0x105={} sim=0x{:08X} "
      "cache_gen=0x{:08X} transition_state={} mgr=0x{:08X} [+60]=0x{:08X} [+44]=0x{:08X} "
      "mgr+0xD0=0x{:08X}",
      tick, game, LoadGuestU8(base, game + 0x104u), LoadGuestU8(base, game + 0x105u),
      LoadGuestU32(base, 0x83319650u), LoadGuestU32(base, 0x8331906Cu),
      LoadGuestU32(base, 0x834C20B8u + 52u), manager, view_primary, view_fallback, memo_tick);

  // FIND the camera block rather than trusting the documented offsets.
  //
  // Every previous attempt to read cameraManager+0xE0 was made while the guest was deadlocked,
  // so the block had never been written and "those offsets are wrong" was never a fair test.
  // A camera's FORWARD vector is unit length, which is a strong, layout-independent signature:
  // scan the object for any float triple whose length is 1, and print it with the scalar that
  // follows the block (fov, ~0.7-1.6 rad). Whatever the real layout is, this finds it.
  const auto f32 = [base](uint32_t at) {
    const uint32_t bits = __builtin_bswap32(LoadGuestU32(base, at));
    float v = 0.0f;
    std::memcpy(&v, &bits, sizeof(v));
    return v;
  };
  const auto finite = [](float v) { return v == v && v > -1e18f && v < 1e18f; };

  std::string hits;
  for (uint32_t off = 0; off + 12u <= 0x200u; off += 4u) {
    const float x = f32(manager + off), y = f32(manager + off + 4u), z = f32(manager + off + 8u);
    if (!finite(x) || !finite(y) || !finite(z)) continue;
    const float len2 = x * x + y * y + z * z;
    if (len2 < 0.98f || len2 > 1.02f) continue;
    char buf[160];
    std::snprintf(buf, sizeof(buf), "+0x%02X=(%.3f,%.3f,%.3f) ", off, x, y, z);
    hits += buf;
  }
  if (!hits.empty()) {
    REXLOG_INFO("Refii camera probe: unit-length triples in mgr 0x{:08X}: {}", manager, hits);
  }

  // The scalars that could be a field of view, in radians.
  std::string fovs;
  for (uint32_t off = 0; off < 0x200u; off += 4u) {
    const float v = f32(manager + off);
    if (finite(v) && v > 0.6f && v < 1.7f) {
      char buf[48];
      std::snprintf(buf, sizeof(buf), "+0x%02X=%.4f ", off, v);
      fovs += buf;
    }
  }
  if (!fovs.empty()) {
    REXLOG_INFO("Refii camera probe: fov-like scalars: {}", fovs);
  }
}

}  // namespace refii::vfs

// lhCamera_Update -> sub_821D4628 -> sub_821B9258 walks a camera object and takes the
// first non-null of two views:
//
//   r11 = [obj + 60];  r30 = r11 ? r11 : [obj + 44];
//
// then dereferences r30 for the rest of the block — sub_8219A758(r30 + 44, ...), then
// [r30 + 105], and on.
//
// THIS HOOK USED TO SKIP THE CALL when both views were null, to dodge a fault at +0x2C.
// That "fix" was the reason there was no camera at all: this function is what COMPUTES the
// camera pose, so skipping it left `sub_82265F90` returning the engine default, the pull
// cache at 0x8331A230 faithfully caching that default every sim tick, and
// `RefiiHostGetCamera` reporting DEFAULT for ever. The file is untracked — Linux has never
// had it, which is exactly why Linux has a camera and Windows did not.
//
// So it now runs the guest's own update, like Linux does. All that is left here is
// OBSERVATION: report the view pointers when they change, so a null view is visible as a
// fact rather than silently swallowed. If the null-view fault comes back it is a real bug
// to fix at its cause, not to paper over by throwing the camera away.
REX_EXTERN(sub_821B9258) {
  REX_FUNC_PROLOGUE();

  const uint32_t object = ctx.r3.u32;
  const uint32_t primary = object >= 0x1000u ? refii::vfs::LoadGuestU32(base, object + 60) : 0u;
  const uint32_t fallback = object >= 0x1000u ? refii::vfs::LoadGuestU32(base, object + 44) : 0u;

  static std::atomic<uint32_t> last_state{0xFFFFFFFFu};
  const uint32_t state = (object < 0x1000u) ? 2u : ((primary != 0u || fallback != 0u) ? 1u : 0u);
  const bool changed = last_state.exchange(state, std::memory_order_relaxed) != state;

  if (state == 1u) {
    if (changed) {
      REXLOG_INFO("Refii camera view: obj=0x{:08X} [+60]=0x{:08X} [+44]=0x{:08X} — live, "
                  "running the guest camera update",
                  object, primary, fallback);
    }
    __imp__sub_821B9258(ctx, base);
    return;
  }

  // NO VIEW. Skipping is a CRASH GUARD ONLY — every use in the block below is through the
  // view pointer, so running on would fault at +0x2C. But understand what the skip costs:
  // this function is what computes the camera pose, so skipping it means there is no camera
  // at all, and `RefiiHostGetCamera` will read the engine default for ever.
  //
  // Reaching here is a BUG somewhere upstream, not a state to live in. The known cause is the
  // reconstructed Lua workspace (`lua_use_mod_overrides`, `assets/unpacked/scripts/**`): its
  // `nonexact` `scripts/camera/cameramanager.lua` never stands the view up. The host bridge
  // defaults that off now, so this should not fire — say so loudly if it does.
  if (changed) {
    REXLOG_WARN(
        "Refii camera view: obj=0x{:08X} has NO VIEW ([+60]=0 [+44]=0) — skipping the camera "
        "update to avoid a null dereference. THERE WILL BE NO IN-GAME CAMERA while this is "
        "true. Usual cause: the reconstructed Lua workspace is enabled and its cameramanager "
        "reconstruction never creates the view — check REX_LUA_USE_MOD_OVERRIDES.",
        object);
  }
}
