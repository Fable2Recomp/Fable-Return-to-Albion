// refii — what a hard mod's native code calls INTO: the per-tick callbacks and the hero lookup
// declared in core/refii_mod.h. Payload-internal: a mod's sources are compiled into this same
// module, so nothing here crosses the payload/host line.

#include <chrono>
#include <algorithm>
#include <cstdint>
#include <cstring>
#include <unordered_map>
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
PPC_EXTERN_FUNC(rex_lhTrackedObject_RemoveHandleRef);
PPC_EXTERN_FUNC(rex_GDBManager_FindEntryByHash);
PPC_EXTERN_FUNC(rex_lhHeroStats_GetRenown);
PPC_EXTERN_FUNC(rex_lhHeroStats_GetMorality);
PPC_EXTERN_FUNC(rex_lhHeroStats_GetPurity);
PPC_EXTERN_FUNC(rex_Village_IsLineOfSight);
PPC_EXTERN_FUNC(rex_lhLuaScriptSystem_CreateEntityAtPosition);

namespace refii::vfs {
/// The script system the startup scripts ran on (mod_lua_hooks.cpp), or 0 before a session.
uint32_t ModScriptSystem();
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
  const uint32_t target = plausible(vtable) ? LoadGuestU32(base, vtable + 64u) : 0u;
  // The function table, not a scan of every mapping: this runs once per entity in a radius query.
  PPCFunc* getter = target != 0 && refii_bridge_rex_has_function(target) != 0
                        ? reinterpret_cast<PPCFunc*>(refii_bridge_rex_resolve_indirect_function(target))
                        : nullptr;
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
    const int32_t mid = static_cast<int32_t>(static_cast<int64_t>(lo) + (static_cast<int64_t>(hi) - lo + 1) / 2);
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

extern "C" uint32_t refii_mod_handle(uint32_t address) { return refii_bridge_mod_handle(address); }

extern "C" uint32_t refii_mod_handle_address(uint32_t handle) { return refii_bridge_mod_handle_address(handle); }

extern "C" uint32_t refii_mod_fnv1(const char* text) {
  uint32_t hash = 0x811C9DC5u;
  for (const char* c = text; c != nullptr && *c != 0; ++c) {
    hash = (hash * 0x01000193u) ^ static_cast<uint8_t>(*c);
  }
  return hash;
}

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
  // The world manager is *(*(world+8)+4), as the game reaches it (0x824DA760, sub_82446928).
  const uint32_t world = refii_mod_world(base);
  const uint32_t holder8 = world != 0 ? refii::vfs::LoadGuestU32(base, world + 8u) : 0u;
  const uint32_t manager = Plausible(holder8) ? refii::vfs::LoadGuestU32(base, holder8 + 4u) : 0u;
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
  if (!Plausible(item)) {
    return 0u;
  }
  // The add registered the out slot as a reference holder on the item (+40); every game caller
  // releases it before its frame goes (0x823B815C), or the item keeps a pointer into a dead frame.
  PPCContext release = refii::mods::Frame(*ctx);
  release.r3.u32 = item;
  release.r4.u32 = out;
  rex_lhTrackedObject_RemoveHandleRef(release, base);
  return item;
}

// ---------------------------------------------------------------------------------------------
// Hero stats, physique, inventory, weapons, space, spawning (v2 helpers)
// ---------------------------------------------------------------------------------------------

namespace {

constexpr uint32_t kAgeComponent = 122;
constexpr uint32_t kWeaponSetComponent = 28;
constexpr uint32_t kCreatureComponent = 20;
constexpr uint32_t kStatsFat = 56;
constexpr uint32_t kStatsHeight = 60;
constexpr uint32_t kAgeYears = 12;
constexpr uint32_t kInventoryList = 16;
constexpr uint32_t kWeaponMelee = 84;
constexpr uint32_t kWeaponRanged = 88;
/// The world's entity manager (TU1 0x83496B28): +8 the intrusive list's head (each node is
/// entity+8, node +0 the next node), the link rex_lhEntityManager_Add makes.
constexpr uint32_t kEntityManager = 0x83496B28u;
constexpr uint32_t kManagerList = 8;
constexpr uint32_t kEntityNode = 8;
constexpr uint32_t kEntityFlags = 0x90;
constexpr uint8_t kInWorld = 0x40;
/// Presence of component 2 (the placement the sphere test reads: entity+0x24 bit 2), as
/// sub_822000E8 checks before it asks for a position.
constexpr uint32_t kPresenceWord = 0x24;
constexpr uint32_t kHasPlacementBit = 1u << 2;
constexpr int32_t kNearScanLimit = 20000;

uint32_t StatsOf(uint8_t* base, uint32_t entity) {
  return Plausible(entity) ? refii_mod_component(base, entity, kStatsComponent) : 0u;
}

}  // namespace

extern "C" int32_t refii_mod_renown(PPCContext* ctx, uint8_t* base, uint32_t entity, int32_t* out) {
  const uint32_t stats = ctx != nullptr ? StatsOf(base, entity) : 0u;
  if (stats == 0) {
    return 0;
  }
  PPCContext work = refii::mods::Frame(*ctx);
  work.r3.u32 = stats;
  rex_lhHeroStats_GetRenown(work, base);
  if (out != nullptr) {
    *out = static_cast<int32_t>(work.r3.u32);
  }
  return 1;
}

extern "C" int32_t refii_mod_morality(PPCContext* ctx, uint8_t* base, uint32_t entity, float* out) {
  const uint32_t stats = ctx != nullptr ? StatsOf(base, entity) : 0u;
  if (stats == 0) {
    return 0;
  }
  PPCContext work = refii::mods::Frame(*ctx);
  work.r3.u32 = stats;
  rex_lhHeroStats_GetMorality(work, base);
  if (out != nullptr) {
    *out = static_cast<float>(work.f1.f64);
  }
  return 1;
}

extern "C" int32_t refii_mod_purity(PPCContext* ctx, uint8_t* base, uint32_t entity, float* out) {
  const uint32_t stats = ctx != nullptr ? StatsOf(base, entity) : 0u;
  if (stats == 0) {
    return 0;
  }
  PPCContext work = refii::mods::Frame(*ctx);
  work.r3.u32 = stats;
  rex_lhHeroStats_GetPurity(work, base);
  if (out != nullptr) {
    *out = static_cast<float>(work.f1.f64);
  }
  return 1;
}

extern "C" int32_t refii_mod_physique(uint8_t* base, uint32_t entity, float* out_fat, float* out_height) {
  const uint32_t stats = StatsOf(base, entity);
  if (stats == 0) {
    return 0;
  }
  if (out_fat != nullptr) {
    *out_fat = refii::mods::LoadF32(base, stats + kStatsFat);
  }
  if (out_height != nullptr) {
    *out_height = refii::mods::LoadF32(base, stats + kStatsHeight);
  }
  return 1;
}

extern "C" int32_t refii_mod_age(uint8_t* base, uint32_t entity, float* out) {
  const uint32_t age = Plausible(entity) ? refii_mod_component(base, entity, kAgeComponent) : 0u;
  if (age == 0) {
    return 0;
  }
  if (out != nullptr) {
    *out = refii::mods::LoadF32(base, age + kAgeYears);
  }
  return 1;
}

extern "C" int32_t refii_mod_inventory_stack(uint8_t* base, uint32_t entity, int32_t index, uint32_t* out_definition,
                                             int32_t* out_count) {
  const uint32_t inventory = Plausible(entity) ? refii_mod_component(base, entity, kInventoryComponent) : 0u;
  if (inventory == 0 || index < 0) {
    return 0;
  }
  const uint32_t head = refii::vfs::LoadGuestU32(base, inventory + kInventoryList);
  if (!Plausible(head)) {
    return 0;
  }
  uint32_t node = refii::vfs::LoadGuestU32(base, head);
  for (int32_t i = 0; Plausible(node) && node != head && i < 4096; ++i, node = refii::vfs::LoadGuestU32(base, node)) {
    if (i != index) {
      continue;
    }
    const uint32_t item = refii::vfs::LoadGuestU32(base, node + 8u);
    if (!Plausible(item)) {
      return 0;
    }
    if (out_definition != nullptr) {
      *out_definition = refii::vfs::LoadGuestU32(base, item + 4u);
    }
    if (out_count != nullptr) {
      *out_count = static_cast<int32_t>(refii::vfs::LoadGuestU32(base, item + 12u));
    }
    return 1;
  }
  return 0;
}

extern "C" int32_t refii_mod_equipped(uint8_t* base, uint32_t entity, uint32_t* out_melee, uint32_t* out_ranged) {
  const uint32_t set = Plausible(entity) ? refii_mod_component(base, entity, kWeaponSetComponent) : 0u;
  if (set == 0) {
    return 0;
  }
  if (out_melee != nullptr) {
    *out_melee = refii::vfs::LoadGuestU32(base, set + kWeaponMelee);
  }
  if (out_ranged != nullptr) {
    *out_ranged = refii::vfs::LoadGuestU32(base, set + kWeaponRanged);
  }
  return 1;
}

extern "C" int32_t refii_mod_entities_near(PPCContext* ctx, uint8_t* base, const float* xyz, float radius,
                                           int32_t creatures_only, uint32_t* out, int32_t max) {
  if (ctx == nullptr || xyz == nullptr || out == nullptr || max <= 0 || !(radius > 0.0f)) {
    return 0;
  }
  const uint32_t manager = refii::vfs::LoadGuestU32(base, refii::guest::Address(kEntityManager));
  if (!Plausible(manager)) {
    return 0;
  }
  struct Found {
    uint32_t entity;
    float distance2;
  };
  std::vector<Found> found;
  const float limit = radius * radius;
  uint32_t node = refii::vfs::LoadGuestU32(base, manager + kManagerList);
  for (int32_t i = 0; Plausible(node) && i < kNearScanLimit; ++i, node = refii::vfs::LoadGuestU32(base, node)) {
    const uint32_t entity = node - kEntityNode;
    if ((base[entity + kEntityFlags] & kInWorld) == 0 ||
        (refii::vfs::LoadGuestU32(base, entity + kPresenceWord) & kHasPlacementBit) == 0) {
      continue;
    }
    if (creatures_only != 0 && refii_mod_component(base, entity, kCreatureComponent) == 0) {
      continue;
    }
    float at[3];
    if (refii_mod_entity_position(ctx, base, entity, at) == 0) {
      continue;
    }
    const float dx = at[0] - xyz[0], dy = at[1] - xyz[1], dz = at[2] - xyz[2];
    const float d2 = dx * dx + dy * dy + dz * dz;
    if (d2 < limit) {
      found.push_back(Found{entity, d2});
    }
  }
  std::sort(found.begin(), found.end(), [](const Found& a, const Found& b) { return a.distance2 < b.distance2; });
  const int32_t n = static_cast<int32_t>(found.size()) < max ? static_cast<int32_t>(found.size()) : max;
  for (int32_t i = 0; i < n; ++i) {
    out[i] = found[static_cast<size_t>(i)].entity;
  }
  return n;
}

extern "C" int32_t refii_mod_line_of_sight(PPCContext* ctx, uint8_t* base, const float* from, const float* to) {
  if (ctx == nullptr || from == nullptr || to == nullptr) {
    return 0;
  }
  // The physics object the raycast is made on (*(world+0x18)): none before a session.
  const uint32_t world = refii_mod_world(base);
  if (world == 0 || !Plausible(refii::vfs::LoadGuestU32(base, world + 0x18u))) {
    return 0;
  }
  PPCContext work = refii::mods::Frame(*ctx, 0x800u);
  refii::mods::SetVector(work, 1, from);
  refii::mods::SetVector(work, 2, to);
  rex_Village_IsLineOfSight(work, base);
  return (work.r3.u32 & 0xFFu) != 0 ? 1 : 0;
}

extern "C" uint32_t refii_mod_spawn(PPCContext* ctx, uint8_t* base, uint32_t definition, const float* xyz) {
  const uint32_t system = refii::vfs::ModScriptSystem();
  if (ctx == nullptr || xyz == nullptr || definition == 0 || !Plausible(system)) {
    return 0;
  }
  // The game creates an entity even for a hash its database lacks; refuse that here. The manager
  // is g_GDBManager (TU1 0x83496DA0), its lookup GDBManager_FindEntryByHash (r3 manager, r4 hash).
  const uint32_t gdb = refii::vfs::LoadGuestU32(base, refii::guest::Address(0x83496DA0u));
  if (!Plausible(gdb)) {
    return 0;
  }
  PPCContext lookup = refii::mods::Frame(*ctx);
  lookup.r3.u32 = gdb;
  lookup.r4.u32 = definition;
  rex_GDBManager_FindEntryByHash(lookup, base);
  if (!Plausible(lookup.r3.u32)) {
    return 0;
  }
  // The name argument is an LHString handle the entity's name is copied from: an empty handle,
  // on our own frame, names nothing.
  PPCContext work = refii::mods::Frame(*ctx, 0x400u);
  const uint32_t name = work.r1.u32 + 0x80u;
  refii::mods::StoreU32(base, name, 0u);
  work.r3.u32 = system;
  work.r4.u32 = definition;
  work.r5.u32 = name;
  refii::mods::SetVector(work, 1, xyz);
  rex_lhLuaScriptSystem_CreateEntityAtPosition(work, base);
  return Plausible(work.r3.u32) ? work.r3.u32 : 0u;
}
