// refii — what a hard mod's native code calls INTO: the per-tick callbacks and the hero lookup
// declared in core/refii_mod.h. Payload-internal: a mod's sources are compiled into this same
// module, so nothing here crosses the payload/host line.

#include <chrono>
#include <cstdint>
#include <vector>

#include <rex/logging.h>

#include "core/refii_mod.h"
#include "vfs_hooks_private.h"

#include <atomic>
#include <cstdio>
#include <string>

namespace refii::vfs {
uint32_t HeroEntity(uint8_t* base);

std::atomic<uint32_t> g_lua_generation{0};

void BumpLuaGeneration() { g_lua_generation.fetch_add(1u, std::memory_order_relaxed); }
}  // namespace refii::vfs

namespace {

// Function-local, so a mod's static registrar can run before this TU's own statics.
std::vector<refii_mod_tick_fn>& Ticks() {
  static std::vector<refii_mod_tick_fn> ticks;
  return ticks;
}

}  // namespace

extern "C" void refii_mod_register_tick(refii_mod_tick_fn fn) {
  if (fn != nullptr) {
    Ticks().push_back(fn);
  }
}

extern "C" uint32_t refii_mod_local_hero(uint8_t* base) { return refii::vfs::HeroEntity(base); }

/// The players' heroes as the guest itself finds them (`sub_824CE278` 0x824ce3c0): the player
/// manager `*(*(*(*(root)+0xC)+0x58)+4)`, then `sub_8222C1B8` reads player 1's handle at +0x6C
/// and `sub_828E5A78` player 2's at +0x74, each a pointer to the entity. Inlined rather than
/// called: sub_828E5A78 is not emitted in the generated module.
extern "C" uint32_t refii_mod_player_hero(uint8_t* base, int32_t player) {
  if (player != 1 && player != 2) {
    return 0u;
  }

  const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; };
  uint32_t at = refii::vfs::LoadGuestU32(base, refii::guest::Address(0x83496AB8u));
  for (uint32_t offset : {0xCu, 0x58u, 0x4u, 0x0u}) {
    if (!plausible(at)) {
      return 0u;
    }
    at = refii::vfs::LoadGuestU32(base, at + offset);
  }

  if (!plausible(at)) {
    return 0u;
  }

  const uint32_t handle = refii::vfs::LoadGuestU32(base, at + (player == 1 ? 0x6Cu : 0x74u));
  return plausible(handle) ? refii::vfs::LoadGuestU32(base, handle) : 0u;
}

extern "C" uint32_t refii_mod_component(uint8_t* base, uint32_t entity, uint32_t id) {
  const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; };
  if (id >= 256u || !plausible(entity)) {
    return 0u;
  }

  using refii::vfs::LoadGuestU32;
  const uint32_t present = LoadGuestU32(base, entity + 0x24u + (id / 32u) * 4u);
  if (((present >> (id % 32u)) & 1u) == 0u) {
    return 0u;
  }

  const uint32_t pairs = LoadGuestU32(base, entity + 0x48u);
  const uint32_t pairs_end = LoadGuestU32(base, entity + 0x4Cu);
  if (!plausible(pairs) || pairs_end < pairs || (pairs_end - pairs) > 8u * 512u) {
    return 0u;
  }

  const uint32_t index = LoadGuestU32(base, entity + 0x8Cu);
  if (plausible(index)) {
    const uint32_t at = pairs + 8u * base[index + id];
    if (at + 8u <= pairs_end && LoadGuestU32(base, at) == id) {
      const uint32_t component = LoadGuestU32(base, at + 4u);
      return plausible(component) ? component : 0u;
    }
  }

  for (uint32_t at = pairs; at + 8u <= pairs_end; at += 8u) {
    if (LoadGuestU32(base, at) == id) {
      const uint32_t component = LoadGuestU32(base, at + 4u);
      return plausible(component) ? component : 0u;
    }
  }
  return 0u;
}

extern "C" int32_t refii_mod_run_game_lua(PPCContext* ctx, uint8_t* base, const char* chunk) {
  if (ctx == nullptr || chunk == nullptr) {
    return 0;
  }
  return refii::vfs::RunGameLua(*ctx, base, chunk) ? 1 : 0;
}

extern "C" int32_t refii_mod_game_lua_int(PPCContext* ctx, uint8_t* base, const char* expr,
                                          int32_t lo, int32_t hi, int32_t* out) {
  if (ctx == nullptr || expr == nullptr || lo > hi) {
    return 0;
  }

  const auto holds = [&](const char* op, int32_t value) {
    char chunk[512];
    std::snprintf(chunk, sizeof(chunk),
                  "local v = (%s) assert(type(v) == 'number' and v %s %d)", expr, op, value);
    return refii::vfs::RunGameLua(*ctx, base, chunk);
  };

  if (!holds(">=", lo) || !holds("<=", hi)) {
    return 0;
  }

  // The greatest integer m with v >= m: `v >= mid` halves the range each run.
  while (lo < hi) {
    const int32_t mid = lo + (hi - lo + 1) / 2;
    if (holds(">=", mid)) {
      lo = mid;
    } else {
      hi = mid - 1;
    }
  }

  if (out != nullptr) {
    *out = lo;
  }
  return 1;
}

extern "C" uint32_t refii_mod_lua_generation(void) {
  return refii::vfs::g_lua_generation.load(std::memory_order_relaxed);
}

namespace refii::vfs {

/// Runs every mod's tick callback with the real seconds since the last guest tick. Called from
/// OnGuestTick, on the guest tick thread, where guest calls are legal.
void RunModTicks(PPCContext& ctx, uint8_t* base) {
  std::vector<refii_mod_tick_fn>& ticks = Ticks();
  if (ticks.empty()) {
    return;
  }

  using Clock = std::chrono::steady_clock;
  static Clock::time_point last = Clock::now();
  const Clock::time_point now = Clock::now();
  float seconds = std::chrono::duration<float>(now - last).count();
  last = now;
  // A hitch (a load screen, a breakpoint) is not time the player spent regenerating.
  if (seconds > 0.25f) {
    seconds = 0.25f;
  }

  for (refii_mod_tick_fn fn : ticks) {
    PPCContext work = ctx;
    fn(&work, base, seconds);
  }
}

}  // namespace refii::vfs
