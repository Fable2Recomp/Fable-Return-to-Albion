// refii — what a hard mod's native code calls INTO: the per-tick callbacks and the hero lookup
// declared in core/refii_mod.h. Payload-internal: a mod's sources are compiled into this same
// module, so nothing here crosses the payload/host line.

#include <chrono>
#include <cstdint>
#include <cstring>
#include <vector>

#include <rex/logging.h>

#include "core/guest_state.h"
#include "core/refii_mod.h"
#include "hooks/ppc_externs.h"
#include "vfs_hooks_private.h"

#include <atomic>
#include <cmath>
#include <cstdio>
#include <string>

// The game's own bodies, by their config names — called, never overridden here.
PPC_EXTERN_FUNC(__imp__rex_CECStats_AbilityExperienceCost);
PPC_EXTERN_FUNC(__imp__rex_CSpellRegistry_FindLive);
PPC_EXTERN_FUNC(__imp__rex_CECFaction_RelationTo);
// Hooked in mod_events.cpp (the events), so a helper that must raise the event calls the name.
PPC_EXTERN_FUNC(rex_CECExperience_Modify);
PPC_EXTERN_FUNC(rex_CECExperience_Spend);
PPC_EXTERN_FUNC(rex_CSpellRegistry_Create);
PPC_EXTERN_FUNC(rex_CECHealth_Modify);
PPC_EXTERN_FUNC(rex_CECMoney_Modify);
PPC_EXTERN_FUNC(rex_CECInventory_AddOneOfRecord);

namespace refii::vfs {
uint32_t HeroEntity(uint8_t* base);
void RunModEventsTick(PPCContext& ctx, uint8_t* base, float seconds);

std::atomic<uint32_t> g_lua_generation{0};

void BumpLuaGeneration() {
  g_lua_generation.fetch_add(1u, std::memory_order_relaxed);
  refii_bridge_new_game_generation();
}
}  // namespace refii::vfs

namespace {

constexpr uint32_t kGameRoot = 0x83496AB8u;
constexpr uint32_t kHealthComponent = 36;
constexpr uint32_t kSpellManagerComponent = 56;
constexpr uint32_t kStatsComponent = 57;
constexpr uint32_t kExperienceComponent = 164;
constexpr uint32_t kHealthCurrent = 0x14;
constexpr uint32_t kHealthMax = 0x1C;
constexpr uint32_t kAbilityTable = 0x110;
constexpr uint32_t kExperiencePools = 0x10;
constexpr uint32_t kWorldRegistry = 0x44;
constexpr uint32_t kInventoryComponent = 13;
constexpr uint32_t kMoneyComponent = 81;
constexpr uint32_t kFactionComponent = 133;
constexpr uint32_t kEntityDefinition = 0x1C;
constexpr uint32_t kEntityName = 0x80;
constexpr uint32_t kPlacement = 0x7C;
constexpr uint32_t kSetPositionSlot = 25;
constexpr uint32_t kManagerDefinitions = 400;
constexpr uint32_t kSpellSerial = 0x44;

bool Plausible(uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; }

bool LuaIdentifier(const char* key) {
  if (key == nullptr || *key == 0) {
    return false;
  }
  for (const char* c = key; *c != 0; ++c) {
    const bool ok = (*c >= 'a' && *c <= 'z') || (*c >= 'A' && *c <= 'Z') || *c == '_' ||
                    (c != key && *c >= '0' && *c <= '9');
    if (!ok) {
      return false;
    }
  }
  return true;
}

/// A guest C string into `out`, bounded; false for a null or implausible pointer.
bool CopyGuestString(uint8_t* base, uint32_t text, char* out, uint32_t size) {
  if (out == nullptr || size == 0) {
    return false;
  }
  out[0] = 0;
  if (!Plausible(text)) {
    return false;
  }
  uint32_t n = 0;
  for (; n + 1 < size && n < 512u; ++n) {
    const char c = static_cast<char>(base[text + n]);
    if (c == 0) {
      break;
    }
    out[n] = c;
  }
  out[n] = 0;
  return true;
}

/// An LHString handle (a pointer to the body whose first word is the char*), or 0.
uint32_t LhStringText(uint8_t* base, uint32_t handle_at) {
  const uint32_t body = refii::vfs::LoadGuestU32(base, handle_at);
  return Plausible(body) ? refii::vfs::LoadGuestU32(base, body) : 0u;
}

}  // namespace

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

/// The players' heroes as the guest itself finds them (`sub_824CE278` 0x824ce3c0): the entity
/// registry `*(*(*(*(root)+0xC)+0x58)+4)` (the global at 0x83496B28), then `sub_8222C1B8` reads
/// player 1's handle at +0x6C and `sub_828E5A78` player 2's at +0x74, each a pointer to the
/// entity. Inlined rather than called: sub_828E5A78 is not emitted in the generated module.
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

extern "C" int32_t refii_mod_entity_position(PPCContext* ctx, uint8_t* base, uint32_t entity, float* out_xyz) {
  const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; };
  if (ctx == nullptr || out_xyz == nullptr || !plausible(entity)) {
    return 0;
  }
  using refii::vfs::LoadGuestU32;
  const uint32_t placement = LoadGuestU32(base, entity + 0x7Cu);
  const uint32_t vtable = plausible(placement) ? LoadGuestU32(base, placement) : 0u;
  PPCFunc* getter = plausible(vtable) ? refii::vfs::FindGuestFunction(LoadGuestU32(base, vtable + 64u)) : nullptr;
  if (getter == nullptr) {
    return 0;
  }
  // Scratch below the caller's frame; the getter stores the vector at r3.
  const uint32_t frame = (ctx->r1.u32 - 0x100u) & ~0xFu;
  const uint32_t out = frame + 0x60u;
  PPCContext call = *ctx;
  call.r1.u32 = frame;
  call.r3.u32 = out;
  call.r4.u32 = placement;
  getter(call, base);
  for (uint32_t i = 0; i < 3u; ++i) {
    const uint32_t bits = LoadGuestU32(base, out + 4u * i);
    std::memcpy(&out_xyz[i], &bits, sizeof(float));
  }
  return 1;
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
  RunModEventsTick(ctx, base, seconds);
}

}  // namespace refii::vfs

// ---------------------------------------------------------------------------------------------
// Entities, stats, spells, the save
// ---------------------------------------------------------------------------------------------

extern "C" int32_t refii_mod_player_of(uint8_t* base, uint32_t entity) {
  if (entity == 0) {
    return 0;
  }
  if (entity == refii_mod_player_hero(base, 1)) {
    return 1;
  }
  if (entity == refii_mod_player_hero(base, 2)) {
    return 2;
  }
  return 0;
}

extern "C" int32_t refii_mod_entity_alive(uint8_t* base, uint32_t entity) {
  return Plausible(entity) && (base[entity + 0x90u] & 0x40u) != 0 ? 1 : 0;
}

extern "C" uint32_t refii_mod_world(uint8_t* base) {
  using refii::vfs::LoadGuestU32;
  const uint32_t root = LoadGuestU32(base, refii::guest::Address(kGameRoot));
  const uint32_t game = Plausible(root) ? LoadGuestU32(base, root + 0xCu) : 0u;
  const uint32_t holder = Plausible(game) ? LoadGuestU32(base, game + 0x58u) : 0u;
  const uint32_t world = Plausible(holder) ? LoadGuestU32(base, holder + 4u) : 0u;
  return Plausible(world) ? world : 0u;
}

extern "C" int32_t refii_mod_health(uint8_t* base, uint32_t entity, float* out_current, float* out_max) {
  const uint32_t health = refii_mod_component(base, entity, kHealthComponent);
  if (health == 0) {
    return 0;
  }
  if (out_current != nullptr) {
    *out_current = refii::mods::LoadF32(base, health + kHealthCurrent);
  }
  if (out_max != nullptr) {
    *out_max = refii::mods::LoadF32(base, health + kHealthMax);
  }
  return 1;
}

extern "C" int32_t refii_mod_set_health(uint8_t* base, uint32_t entity, float value) {
  const uint32_t health = refii_mod_component(base, entity, kHealthComponent);
  if (health == 0 || std::isnan(value)) {
    return 0;
  }
  const float max = refii::mods::LoadF32(base, health + kHealthMax);
  refii::mods::StoreF32(base, health + kHealthCurrent, value < 0.0f ? 0.0f : (value > max ? max : value));
  return 1;
}

extern "C" int32_t refii_mod_ability_level(uint8_t* base, uint32_t entity, int32_t type) {
  const uint32_t stats = refii_mod_component(base, entity, kStatsComponent);
  if (stats == 0 || type < 0 || type > 14) {
    return -1;
  }
  return static_cast<int32_t>(refii::mods::LoadU32(base, stats + kAbilityTable + 4u * static_cast<uint32_t>(type)));
}

extern "C" int32_t refii_mod_experience(uint8_t* base, uint32_t entity, int32_t pool, float* out) {
  const uint32_t experience = refii_mod_component(base, entity, kExperienceComponent);
  const uint32_t pools = experience != 0 ? refii::mods::LoadU32(base, experience + kExperiencePools) : 0u;
  if (!Plausible(pools) || pool < 0 || pool > 3) {
    return 0;
  }
  if (out != nullptr) {
    *out = refii::mods::LoadF32(base, pools + 4u * static_cast<uint32_t>(pool));
  }
  return 1;
}

extern "C" int32_t refii_mod_ability_cost(PPCContext* ctx, uint8_t* base, uint32_t entity, int32_t type, int32_t level) {
  const uint32_t stats = ctx != nullptr ? refii_mod_component(base, entity, kStatsComponent) : 0u;
  if (stats == 0 || type < 1 || type > 14 || level < 1) {
    return -1;
  }
  PPCContext work = *ctx;
  work.r3.u32 = stats;
  work.r4.u32 = static_cast<uint32_t>(type);
  work.r5.u32 = static_cast<uint32_t>(level);
  __imp__rex_CECStats_AbilityExperienceCost(work, base);
  return static_cast<int32_t>(work.r3.u32);
}

extern "C" int32_t refii_mod_give_experience(PPCContext* ctx, uint8_t* base, uint32_t entity, int32_t pool, float amount) {
  const uint32_t experience = ctx != nullptr ? refii_mod_component(base, entity, kExperienceComponent) : 0u;
  if (experience == 0 || pool < 0 || pool > 3) {
    return 0;
  }
  PPCContext work = *ctx;
  work.r3.u32 = experience;
  work.r4.u32 = static_cast<uint32_t>(pool);
  work.f1.f64 = static_cast<double>(amount);
  work.r6.u64 = 0;
  work.r7.u64 = 0;
  work.r8.u64 = 0;
  rex_CECExperience_Modify(work, base);
  return 1;
}

extern "C" int32_t refii_mod_spend_experience(PPCContext* ctx, uint8_t* base, uint32_t entity, int32_t pool, float amount) {
  const uint32_t experience = ctx != nullptr ? refii_mod_component(base, entity, kExperienceComponent) : 0u;
  if (experience == 0 || pool < 0 || pool > 3) {
    return 0;
  }
  PPCContext work = *ctx;
  work.r3.u32 = experience;
  work.r4.u32 = static_cast<uint32_t>(pool);
  work.f1.f64 = static_cast<double>(amount);
  rex_CECExperience_Spend(work, base);
  return 1;
}

extern "C" uint32_t refii_mod_cast_spell(PPCContext* ctx, uint8_t* base, uint32_t caster, int32_t type, int32_t targeted,
                                         uint32_t target, int32_t level, const float* pos, const float* dir) {
  if (ctx == nullptr || type < 0 || type > 7 || !Plausible(caster)) {
    return 0;
  }
  const uint32_t manager = refii_mod_component(base, caster, kSpellManagerComponent);
  const uint32_t world = refii_mod_world(base);
  const uint32_t registry = world != 0 ? refii::mods::LoadU32(base, world + kWorldRegistry) : 0u;
  if (manager == 0 || !Plausible(registry) || (target != 0 && !refii_mod_entity_alive(base, target))) {
    return 0;
  }
  float at[3] = {0.0f, 0.0f, 0.0f};
  float facing[3] = {0.0f, 0.0f, 1.0f};
  if (pos != nullptr) {
    at[0] = pos[0];
    at[1] = pos[1];
    at[2] = pos[2];
  } else if (refii_mod_entity_position(ctx, base, target != 0 ? target : caster, at) == 0) {
    return 0;
  }
  if (dir != nullptr) {
    facing[0] = dir[0];
    facing[1] = dir[1];
    facing[2] = dir[2];
  } else if (target != 0) {
    float from[3];
    if (refii_mod_entity_position(ctx, base, caster, from) != 0) {
      const float d[3] = {at[0] - from[0], at[1] - from[1], at[2] - from[2]};
      const float length = std::sqrt(d[0] * d[0] + d[1] * d[1] + d[2] * d[2]);
      if (length > 0.001f) {
        for (int i = 0; i < 3; ++i) {
          facing[i] = d[i] / length;
        }
      }
    }
  }
  PPCContext work = refii::mods::Frame(*ctx);
  work.r3.u32 = registry;
  work.r4.u32 = static_cast<uint32_t>(type);
  work.r5.u32 = targeted != 0 ? 1u : 0u;
  work.r6.u32 = target;
  work.r7.u32 = static_cast<uint32_t>(level < 0 ? 0 : (level > 4 ? 4 : level));
  work.r8.u32 = caster;
  work.r9.u32 = refii::mods::LoadU32(base, manager + kManagerDefinitions);
  refii::mods::SetVector(work, 1, at);
  refii::mods::SetVector(work, 2, facing);
  rex_CSpellRegistry_Create(work, base);
  return Plausible(work.r3.u32) ? work.r3.u32 : 0u;
}

extern "C" uint32_t refii_mod_spell_serial(uint8_t* base, uint32_t spell) {
  return Plausible(spell) ? refii::mods::LoadU32(base, spell + kSpellSerial) : 0u;
}

extern "C" uint32_t refii_mod_live_spell(PPCContext* ctx, uint8_t* base, int32_t type, uint32_t caster) {
  const uint32_t world = ctx != nullptr ? refii_mod_world(base) : 0u;
  const uint32_t registry = world != 0 ? refii::mods::LoadU32(base, world + kWorldRegistry) : 0u;
  if (!Plausible(registry) || type < 0 || type > 7) {
    return 0;
  }
  PPCContext work = *ctx;
  work.r3.u32 = registry;
  work.r4.u32 = static_cast<uint32_t>(type);
  work.r5.u32 = static_cast<uint32_t>(-2);  // targeted or area
  work.r6.u32 = caster;
  __imp__rex_CSpellRegistry_FindLive(work, base);
  return Plausible(work.r3.u32) ? work.r3.u32 : 0u;
}

extern "C" int32_t refii_mod_save_set_int(PPCContext* ctx, uint8_t* base, const char* key, int32_t value) {
  if (ctx == nullptr || !LuaIdentifier(key)) {
    return 0;
  }
  char chunk[160];
  std::snprintf(chunk, sizeof(chunk), "RefiiModSave = RefiiModSave or {} RefiiModSave.%s = %d", key, value);
  return refii_mod_run_game_lua(ctx, base, chunk);
}

extern "C" int32_t refii_mod_save_get_int(PPCContext* ctx, uint8_t* base, const char* key, int32_t lo, int32_t hi,
                                          int32_t* out) {
  if (ctx == nullptr || !LuaIdentifier(key)) {
    return 0;
  }
  char expr[160];
  std::snprintf(expr, sizeof(expr), "RefiiModSave and RefiiModSave.%s", key);
  return refii_mod_game_lua_int(ctx, base, expr, lo, hi, out);
}

// ---------------------------------------------------------------------------------------------
// Entity identity, factions, placement, gold, inventory, the level
// ---------------------------------------------------------------------------------------------

extern "C" uint32_t refii_mod_entity_definition(uint8_t* base, uint32_t entity) {
  return Plausible(entity) ? refii::mods::LoadU32(base, entity + kEntityDefinition) : 0u;
}

extern "C" int32_t refii_mod_entity_name(uint8_t* base, uint32_t entity, char* out, uint32_t size) {
  if (!Plausible(entity)) {
    if (out != nullptr && size != 0) {
      out[0] = 0;
    }
    return 0;
  }
  return CopyGuestString(base, LhStringText(base, entity + kEntityName), out, size) ? 1 : 0;
}

extern "C" int32_t refii_mod_relation(PPCContext* ctx, uint8_t* base, uint32_t entity, uint32_t other) {
  const uint32_t faction = ctx != nullptr ? refii_mod_component(base, entity, kFactionComponent) : 0u;
  if (faction == 0 || !Plausible(other)) {
    return 0;
  }
  PPCContext work = refii::mods::Frame(*ctx);
  work.r3.u32 = faction;
  work.r4.u32 = other;
  __imp__rex_CECFaction_RelationTo(work, base);
  return static_cast<int32_t>(work.r3.u32);
}

extern "C" int32_t refii_mod_set_position(PPCContext* ctx, uint8_t* base, uint32_t entity, const float* xyz) {
  if (ctx == nullptr || xyz == nullptr || !Plausible(entity)) {
    return 0;
  }
  const uint32_t placement = refii::mods::LoadU32(base, entity + kPlacement);
  if (!Plausible(placement)) {
    return 0;
  }
  PPCContext work = refii::mods::Frame(*ctx);
  refii::mods::SetVector(work, 1, xyz);
  return refii::mods::CallVirtual(work, base, placement, kSetPositionSlot) ? 1 : 0;
}

extern "C" int32_t refii_mod_modify_health(PPCContext* ctx, uint8_t* base, uint32_t entity, float delta, uint32_t attacker) {
  const uint32_t health = ctx != nullptr ? refii_mod_component(base, entity, kHealthComponent) : 0u;
  if (health == 0 || std::isnan(delta)) {
    return 0;
  }
  PPCContext work = refii::mods::Frame(*ctx);
  work.r3.u32 = health;
  work.f1.f64 = static_cast<double>(delta);
  work.r4.u64 = 0;
  work.r5.u32 = Plausible(attacker) ? attacker : 0u;
  rex_CECHealth_Modify(work, base);
  return 1;
}

extern "C" int32_t refii_mod_level_name(uint8_t* base, char* out, uint32_t size) {
  if (out != nullptr && size != 0) {
    out[0] = 0;
  }
  const uint32_t world = refii_mod_world(base);
  const uint32_t manager = world != 0 ? refii::vfs::LoadGuestU32(base, world + 8u) : 0u;
  const uint32_t holder = Plausible(manager) ? refii::vfs::LoadGuestU32(base, manager + 0x24u) : 0u;
  const uint32_t level = Plausible(holder) ? refii::vfs::LoadGuestU32(base, holder + 4u) : 0u;
  if (!Plausible(level)) {
    return 0;
  }
  return CopyGuestString(base, LhStringText(base, level + 0xACu), out, size) ? 1 : 0;
}

extern "C" int32_t refii_mod_gold(uint8_t* base, uint32_t entity, int32_t* out) {
  const uint32_t purse = refii_mod_component(base, entity, kMoneyComponent);
  if (purse == 0) {
    return 0;
  }
  if (out != nullptr) {
    *out = static_cast<int32_t>(refii::mods::LoadU32(base, purse + 12u));
  }
  return 1;
}

extern "C" int32_t refii_mod_give_gold(PPCContext* ctx, uint8_t* base, uint32_t entity, int32_t delta) {
  const uint32_t purse = ctx != nullptr ? refii_mod_component(base, entity, kMoneyComponent) : 0u;
  if (purse == 0) {
    return 0;
  }
  // The game's own "give gold" thunk: Modify(purse, delta, 0, 1, 0).
  PPCContext work = refii::mods::Frame(*ctx);
  work.r3.u32 = purse;
  work.r4.s32 = delta;
  work.r5.u64 = 0;
  work.r6.u64 = 1;
  work.r7.u64 = 0;
  rex_CECMoney_Modify(work, base);
  return 1;
}

extern "C" int32_t refii_mod_item_count(uint8_t* base, uint32_t entity, uint32_t definition) {
  const uint32_t inventory = refii_mod_component(base, entity, kInventoryComponent);
  const uint32_t sentinel = inventory != 0 ? refii::mods::LoadU32(base, inventory + 16u) : 0u;
  if (!Plausible(sentinel)) {
    return 0;
  }
  int32_t count = 0;
  uint32_t node = refii::mods::LoadU32(base, sentinel);
  for (uint32_t walked = 0; Plausible(node) && node != sentinel && walked < 4096u; ++walked) {
    const uint32_t item = refii::mods::LoadU32(base, node + 8u);
    if (Plausible(item) && refii::mods::LoadU32(base, item + 4u) == definition) {
      count += static_cast<int32_t>(refii::mods::LoadU32(base, item + 12u));
    }
    node = refii::mods::LoadU32(base, node);
  }
  return count;
}

extern "C" uint32_t refii_mod_add_item(PPCContext* ctx, uint8_t* base, uint32_t entity, uint32_t definition) {
  const uint32_t inventory = ctx != nullptr ? refii_mod_component(base, entity, kInventoryComponent) : 0u;
  if (inventory == 0 || definition == 0) {
    return 0;
  }
  // The shared pointer the add fills: four words on our own frame below the caller's.
  PPCContext work = refii::mods::Frame(*ctx);
  const uint32_t out = work.r1.u32 + 0x40u;
  for (uint32_t i = 0; i < 4; ++i) {
    refii::mods::StoreU32(base, out + 4u * i, 0u);
  }
  work.r3.u32 = out;
  work.r4.u32 = inventory;
  work.r5.u32 = definition;
  work.r6.u64 = 1;
  rex_CECInventory_AddOneOfRecord(work, base);
  const uint32_t item = refii::mods::LoadU32(base, out);
  return Plausible(item) ? item : 0u;
}
