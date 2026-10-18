// refii — the mod event bus (core/refii_mod_events.h): the subscriber table, the dispatch, the
// game seams that raise each event, and the hand-off to the mods' Lua handlers.
//
// ONE OWNER PER SEAM. Every function hooked here is hooked by refii, so a mod that hooks the
// same function by name chains outside this (ModNative.PlanChains) and runs before the event
// fires; a mod that subscribes to the event needs no chain at all. The seams are the ones the
// first two hard mods proved in runs, each verified in the binary first — the registers and
// offsets are quoted beside each hook, as the mods quoted them.
//
// LUA HANDLERS. A mod's init.lua subscribes with Refii.on("hit", fn) (the runtime is composed
// into the init chunk by Engine_ModLua.cs). The game VM is asked for the mask of kinds with
// handlers once per Lua generation and again whenever a registration marked it dirty; an event
// with a Lua handler is run through Refii._fire(kind, {fields}) as one chunk, and the assertion
// failing is a handler's `return true` — the cancel. Chunks run on the guest thread inside the
// seam, as native handlers do, never nested inside another event's chunk.

#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <string>
#include <vector>

#include <rex/logging.h>

#include "core/guest_build.h"
#include "core/refii_mod.h"
#include "core/refii_mod_events.h"
#include "hooks/ppc_externs.h"
#include "vfs_hooks_private.h"

PPC_EXTERN_FUNC(__imp__rex_CPlayerActionReceiver_ProcessAction);
PPC_EXTERN_FUNC(__imp__rex_CECAbilities_PerformRequest);
PPC_EXTERN_FUNC(__imp__rex_CAbilityActionMelee_CommitSwing);
PPC_EXTERN_FUNC(__imp__rex_CAbilityActionStrafeRoll_CanStart);
PPC_EXTERN_FUNC(__imp__rex_CAbilityActionStrafeRoll_Start);
PPC_EXTERN_FUNC(__imp__rex_CActionBlock_OnHitBlockPose);
PPC_EXTERN_FUNC(__imp__rex_CActionBlock_OnHitBlockedResponse);
PPC_EXTERN_FUNC(__imp__rex_CAction_OnAnimEvent);
PPC_EXTERN_FUNC(__imp__rex_CECHitReceiver_OnHit);
PPC_EXTERN_FUNC(__imp__rex_CECHitResponsePlayAction_OnHit);
PPC_EXTERN_FUNC(__imp__rex_CAbilityActionMagicCharge_CanStart);
PPC_EXTERN_FUNC(__imp__rex_CECSpellManager_ChargeLevelFromTime);
PPC_EXTERN_FUNC(__imp__rex_CECSpellManager_LaunchSlot);
PPC_EXTERN_FUNC(__imp__rex_CECSpellManager_OnSpellLaunched);
PPC_EXTERN_FUNC(__imp__rex_CSpellRegistry_Create);
PPC_EXTERN_FUNC(__imp__rex_CECExperience_Modify);
PPC_EXTERN_FUNC(__imp__rex_CECExperience_Spend);
PPC_EXTERN_FUNC(__imp__rex_CECCreature_Die);
PPC_EXTERN_FUNC(__imp__rex_CECHealth_OnHeroDeath);
PPC_EXTERN_FUNC(__imp__rex_CECHealth_OnHit);
PPC_EXTERN_FUNC(__imp__rex_lhLuaScriptSystem_OnLevelLoad);
PPC_EXTERN_FUNC(__imp__rex_lhWorld_UnloadLevel);
PPC_EXTERN_FUNC(__imp__rex_lhSaveManager_WriteSave);
PPC_EXTERN_FUNC(__imp__rex_CECInventory_AddOneOfRecord);
PPC_EXTERN_FUNC(__imp__rex_lhEntity_UseInventoryItem);
PPC_EXTERN_FUNC(__imp__rex_CECHeldItems_AttachItemToSlot);
PPC_EXTERN_FUNC(__imp__rex_CECHeldItems_ReleaseSlot);
PPC_EXTERN_FUNC(__imp__rex_CECWeaponSet_RecordDrawnItem);
PPC_EXTERN_FUNC(__imp__rex_CECStats_SetAbilityLevel);
PPC_EXTERN_FUNC(__imp__rex_CECMoney_Modify);
// Called, never overridden here: the game's own bodies by their config names.
PPC_EXTERN_FUNC(__imp__rex_lhSmartPtr_Deref);
PPC_EXTERN_FUNC(__imp__rex_CECHealth_CalculateDamage);

using namespace refii::mods;

namespace {

struct Subscription {
  int32_t kind;
  refii_mod_event_fn fn;
  void (*user)(void);
  int32_t priority;
  const char* owner;
  int32_t order;
};

// Function-local: a mod's static registrar may run before this TU's own statics.
std::vector<Subscription>& Subscriptions() {
  static std::vector<Subscription> table;
  return table;
}

constexpr uint32_t kHealthComponent = 36;
constexpr uint32_t kActionManagerComponent = 49;
constexpr uint32_t kExperienceComponent = 164;
constexpr uint32_t kHitAttackerRef = 0x00;
constexpr uint32_t kHitDamage = 0x38;
constexpr uint32_t kHitSerial = 0x44;
constexpr uint32_t kHitLevel = 0x48;
constexpr uint32_t kHitSpellType = 0x54;
constexpr uint32_t kHitNoResponse = 0x1C;
constexpr uint32_t kHitKnockdown = 0x18;
constexpr uint32_t kHitUnblockable = 0x19;
constexpr uint32_t kHitKind = 0x58;
constexpr uint32_t kManagerCaster = 0x4;
constexpr uint32_t kManagerState = 0x70;
constexpr uint32_t kManagerSlotTable = 0x80;
constexpr uint32_t kManagerSlotsToCome = 0x170;
constexpr uint32_t kStateCascade = 5;
constexpr uint32_t kSpellSerial = 0x44;
constexpr uint32_t kSlots = 5;

inline uint32_t Load32(const uint8_t* base, uint32_t at) { return refii::mods::LoadU32(base, at); }
inline uint8_t Load8(const uint8_t* base, uint32_t at) { return refii::mods::LoadU8(base, at); }
inline float LoadF(const uint8_t* base, uint32_t at) { return refii::mods::LoadF32(base, at); }

int32_t PlayerOf(uint8_t* base, uint32_t entity) { return refii_mod_player_of(base, entity); }

// ---------------------------------------------------------------------------------------------
// Lua handlers
// ---------------------------------------------------------------------------------------------

/// Which kinds have a Lua handler (bit = kind), as the VM last said; re-read on a generation
/// change and when Refii._dirty says a registration happened since.
uint32_t g_lua_mask = 0;
uint32_t g_lua_mask2 = 0;
uint32_t g_lua_mask_generation = 0xFFFFFFFFu;
float g_lua_dirty_poll = 0.0f;
bool g_lua_running = false;
float g_lua_tick_accumulated = 0.0f;
constexpr float kLuaTickPeriod = 0.1f;
constexpr float kLuaDirtyPollSeconds = 1.0f;

bool LuaWants(int32_t kind) {
  if (kind < 0 || kind >= 62) {
    return false;
  }
  return kind < 31 ? ((g_lua_mask >> kind) & 1u) != 0u : ((g_lua_mask2 >> (kind - 31)) & 1u) != 0u;
}

/// The Lua runtime keeps two masks (Refii._mask for kinds 0..30, Refii._mask2 for 31..61), each
/// read as a positive int.
void ReadLuaMask(PPCContext& ctx, uint8_t* base) {
  int32_t mask = 0;
  if (refii_mod_game_lua_int(&ctx, base, "Refii and Refii._mask or 0", 0, 0x7FFFFFFF, &mask) == 0) {
    mask = 0;
  }
  g_lua_mask = static_cast<uint32_t>(mask);
  mask = 0;
  if (refii_mod_game_lua_int(&ctx, base, "Refii and Refii._mask2 or 0", 0, 0x7FFFFFFF, &mask) == 0) {
    mask = 0;
  }
  g_lua_mask2 = static_cast<uint32_t>(mask);
  refii_mod_run_game_lua(&ctx, base, "if Refii then Refii._dirty = false end");
}

/// Once per tick: keep the mask current.
void PumpLuaMask(PPCContext& ctx, uint8_t* base, float seconds) {
  const uint32_t generation = refii_mod_lua_generation();
  if (generation != g_lua_mask_generation) {
    g_lua_mask_generation = generation;
    ReadLuaMask(ctx, base);
    g_lua_dirty_poll = 0.0f;
    return;
  }
  g_lua_dirty_poll += seconds;
  if (g_lua_dirty_poll >= kLuaDirtyPollSeconds) {
    g_lua_dirty_poll = 0.0f;
    if (refii_mod_run_game_lua(&ctx, base, "assert(Refii and Refii._dirty)") != 0) {
      ReadLuaMask(ctx, base);
    }
  }
}

/// Runs `Refii._fire(kind, {fields})`; true when a handler cancelled. Never nested.
bool FireLua(PPCContext& ctx, uint8_t* base, int32_t kind, const char* fields) {
  if (g_lua_running) {
    return false;
  }
  char chunk[900];
  std::snprintf(chunk, sizeof(chunk), "assert(Refii._fire(%d,{%s}))", kind, fields);
  g_lua_running = true;
  const bool kept = refii_mod_run_game_lua(&ctx, base, chunk) != 0;
  g_lua_running = false;
  return !kept;
}

/// A guest text as a single-quoted Lua literal's body: quotes, backslashes and newlines become
/// underscores, the length is bounded.
void LuaString(const char* text, char* out, size_t size) {
  size_t n = 0;
  for (const char* c = text; c != nullptr && *c != 0 && n + 1 < size; ++c) {
    out[n++] = (*c == '\'' || *c == '\\' || *c == '\n' || *c == '\r') ? '_' : *c;
  }
  out[n] = 0;
}

/// The Lua table for an event, by kind. Entities are their guest addresses (opaque ids to Lua).
void LuaFields(int32_t kind, const void* event, char* out, size_t size) {
  switch (kind) {
    case REFII_EVENT_TICK: {
      const auto& e = *static_cast<const TickEvent*>(event);
      std::snprintf(out, size, "seconds=%g", static_cast<double>(e.seconds));
      return;
    }
    case REFII_EVENT_HERO_READY:
    case REFII_EVENT_HERO_GONE: {
      const auto& e = *static_cast<const HeroEvent*>(event);
      std::snprintf(out, size, "player=%d,hero=%u", e.player, e.hero);
      return;
    }
    case REFII_EVENT_LUA_READY:
    case REFII_EVENT_SAVE_LOADED: {
      const auto& e = *static_cast<const LuaEvent*>(event);
      std::snprintf(out, size, "generation=%u", e.generation);
      return;
    }
    case REFII_EVENT_GAME_ACTION: {
      const auto& e = *static_cast<const GameActionEvent*>(event);
      std::snprintf(out, size, "hero=%u,player=%d,action=%u", e.hero, e.player, e.action);
      return;
    }
    case REFII_EVENT_ABILITY_REQUEST: {
      const auto& e = *static_cast<const AbilityRequestEvent*>(event);
      std::snprintf(out, size, "entity=%u,player=%d,type=%u", e.entity, e.player, e.type);
      return;
    }
    case REFII_EVENT_MELEE_SWING: {
      const auto& e = *static_cast<const MeleeSwingEvent*>(event);
      std::snprintf(out, size, "entity=%u,player=%d", e.entity, e.player);
      return;
    }
    case REFII_EVENT_ROLL_CAN_START:
    case REFII_EVENT_ROLL_STARTED: {
      const auto& e = *static_cast<const RollEvent*>(event);
      std::snprintf(out, size, "entity=%u,player=%d", e.entity, e.player);
      return;
    }
    case REFII_EVENT_BLOCK: {
      const auto& e = *static_cast<const BlockEvent*>(event);
      std::snprintf(out, size, "blocker=%u,player=%d,damage=%g,unblockable=%s", e.blocker, e.player,
                    static_cast<double>(e.damage), e.unblockable ? "true" : "false");
      return;
    }
    case REFII_EVENT_ANIM_EVENT: {
      const auto& e = *static_cast<const AnimEventEvent*>(event);
      char name[96];
      LuaString(e.name, name, sizeof(name));
      std::snprintf(out, size, "entity=%u,player=%d,name='%s'", e.entity, e.player, name);
      return;
    }
    case REFII_EVENT_HIT:
    case REFII_EVENT_HIT_LANDED: {
      const auto& e = *static_cast<const HitEvent*>(event);
      std::snprintf(out, size,
                    "victim=%u,victim_player=%d,attacker=%u,attacker_player=%d,damage=%g,spell_type=%d,spell_level=%d,spell_serial=%u",
                    e.victim, e.victim_player, e.attacker, e.attacker_player, static_cast<double>(e.damage), e.spell_type,
                    e.spell_level, e.spell_serial);
      return;
    }
    case REFII_EVENT_HIT_RESPONSE: {
      const auto& e = *static_cast<const HitResponseEvent*>(event);
      std::snprintf(out, size, "victim=%u,victim_player=%d,damage=%g,health=%g,max_health=%g", e.victim, e.victim_player,
                    static_cast<double>(e.damage), static_cast<double>(e.health), static_cast<double>(e.max_health));
      return;
    }
    case REFII_EVENT_SPELL_CHARGE: {
      const auto& e = *static_cast<const SpellChargeEvent*>(event);
      std::snprintf(out, size, "entity=%u,player=%d", e.entity, e.player);
      return;
    }
    case REFII_EVENT_SPELL_CHARGE_LEVEL: {
      const auto& e = *static_cast<const SpellChargeLevelEvent*>(event);
      std::snprintf(out, size, "caster=%u,player=%d,level=%d", e.caster, e.player, e.level);
      return;
    }
    case REFII_EVENT_SPELL_LAUNCH: {
      const auto& e = *static_cast<const SpellLaunchEvent*>(event);
      std::snprintf(out, size, "caster=%u,player=%d,targeted=%s,slot=%d,type=%d,cascading=%s", e.caster, e.player,
                    e.targeted ? "true" : "false", e.slot, e.type, e.cascading ? "true" : "false");
      return;
    }
    case REFII_EVENT_SPELL_LAUNCHED: {
      const auto& e = *static_cast<const SpellLaunchedEvent*>(event);
      std::snprintf(out, size, "caster=%u,player=%d,slot=%d", e.caster, e.player, e.slot);
      return;
    }
    case REFII_EVENT_SPELL_CREATED: {
      const auto& e = *static_cast<const SpellCreatedEvent*>(event);
      std::snprintf(out, size, "spell=%u,type=%d,targeted=%s,target=%u,level=%d,caster=%u,caster_player=%d,serial=%u",
                    e.spell, e.type, e.targeted ? "true" : "false", e.target, e.level, e.caster, e.caster_player, e.serial);
      return;
    }
    case REFII_EVENT_EXPERIENCE: {
      const auto& e = *static_cast<const ExperienceEvent*>(event);
      std::snprintf(out, size, "entity=%u,player=%d,pool=%d,amount=%g,spend=%s", e.entity, e.player, e.pool,
                    static_cast<double>(e.amount), e.spend ? "true" : "false");
      return;
    }
    case REFII_EVENT_ENTITY_DIED: {
      const auto& e = *static_cast<const EntityDiedEvent*>(event);
      std::snprintf(out, size, "entity=%u,player=%d,killer=%u,killer_player=%d", e.entity, e.player, e.killer, e.killer_player);
      return;
    }
    case REFII_EVENT_HERO_DOWNED: {
      const auto& e = *static_cast<const HeroDownedEvent*>(event);
      std::snprintf(out, size, "hero=%u,player=%d,killer=%u,killer_player=%d", e.hero, e.player, e.killer, e.killer_player);
      return;
    }
    case REFII_EVENT_DAMAGE: {
      const auto& e = *static_cast<const DamageEvent*>(event);
      std::snprintf(out, size,
                    "victim=%u,victim_player=%d,attacker=%u,attacker_player=%d,damage=%g,health=%g,max_health=%g,lethal=%s,spell_type=%d,spell_level=%d,spell_serial=%u",
                    e.victim, e.victim_player, e.attacker, e.attacker_player, static_cast<double>(e.damage),
                    static_cast<double>(e.health), static_cast<double>(e.max_health), e.lethal ? "true" : "false", e.spell_type,
                    e.spell_level, e.spell_serial);
      return;
    }
    case REFII_EVENT_LEVEL_LOADED: {
      const auto& e = *static_cast<const LevelLoadedEvent*>(event);
      char level[128];
      LuaString(e.level, level, sizeof(level));
      std::snprintf(out, size, "level='%s',from_save=%s", level, e.from_save ? "true" : "false");
      return;
    }
    case REFII_EVENT_LEVEL_UNLOADING: {
      const auto& e = *static_cast<const LevelUnloadingEvent*>(event);
      char level[128];
      LuaString(e.level, level, sizeof(level));
      std::snprintf(out, size, "level='%s'", level);
      return;
    }
    case REFII_EVENT_GAME_SAVING: {
      const auto& e = *static_cast<const GameSavingEvent*>(event);
      char file[128];
      LuaString(e.file, file, sizeof(file));
      std::snprintf(out, size, "file='%s'", file);
      return;
    }
    case REFII_EVENT_ITEM_ADDED: {
      const auto& e = *static_cast<const ItemAddedEvent*>(event);
      std::snprintf(out, size, "entity=%u,player=%d,definition=%u,notify=%s", e.entity, e.player, e.definition,
                    e.notify ? "true" : "false");
      return;
    }
    case REFII_EVENT_ITEM_USED: {
      const auto& e = *static_cast<const ItemUsedEvent*>(event);
      std::snprintf(out, size, "entity=%u,player=%d,item=%u,definition=%u,count=%d", e.entity, e.player, e.item, e.definition,
                    e.count);
      return;
    }
    case REFII_EVENT_HELD_ITEM: {
      const auto& e = *static_cast<const HeldItemEvent*>(event);
      char slot[64];
      LuaString(e.slot, slot, sizeof(slot));
      std::snprintf(out, size, "entity=%u,player=%d,slot='%s',item=%u,attached=%s", e.entity, e.player, slot, e.item,
                    e.attached ? "true" : "false");
      return;
    }
    case REFII_EVENT_WEAPON_EQUIPPED: {
      const auto& e = *static_cast<const WeaponEquippedEvent*>(event);
      std::snprintf(out, size, "entity=%u,player=%d,item=%u,definition=%u", e.entity, e.player, e.item, e.definition);
      return;
    }
    case REFII_EVENT_ABILITY_LEVEL: {
      const auto& e = *static_cast<const AbilityLevelEvent*>(event);
      std::snprintf(out, size, "entity=%u,player=%d,type=%d,level=%d,old_level=%d", e.entity, e.player, e.type, e.level,
                    e.old_level);
      return;
    }
    case REFII_EVENT_GOLD: {
      const auto& e = *static_cast<const GoldEvent*>(event);
      std::snprintf(out, size, "entity=%u,player=%d,gold=%d,delta=%d,passive=%s", e.entity, e.player, e.gold, e.delta,
                    e.passive ? "true" : "false");
      return;
    }
    default:
      out[0] = 0;
      return;
  }
}

/// A Lua `cancel` lands in the event's flag; Lua cannot write other fields.
void DispatchLua(int32_t kind, void* event) {
  if (!LuaWants(kind)) {
    return;
  }
  auto& e = *static_cast<Event*>(event);
  if (kind == REFII_EVENT_TICK) {
    // Ten times a second at most: a chunk run is a compile.
    g_lua_tick_accumulated += static_cast<TickEvent*>(event)->seconds;
    if (g_lua_tick_accumulated < kLuaTickPeriod) {
      return;
    }
    char fields[64];
    std::snprintf(fields, sizeof(fields), "seconds=%g", static_cast<double>(g_lua_tick_accumulated));
    g_lua_tick_accumulated = 0.0f;
    FireLua(*e.ctx, e.base, kind, fields);
    return;
  }
  char fields[640];
  LuaFields(kind, event, fields, sizeof(fields));
  if (FireLua(*e.ctx, e.base, kind, fields)) {
    e.cancel = true;
  }
}

// ---------------------------------------------------------------------------------------------
// Hit decoding, shared by the hit seams
// ---------------------------------------------------------------------------------------------

uint32_t Deref(const PPCContext& ctx, uint8_t* base, uint32_t ref) {
  PPCContext work = ctx;
  work.r3.u32 = ref;
  __imp__rex_lhSmartPtr_Deref(work, base);
  return work.r3.u32;
}

void ReadHit(PPCContext& ctx, uint8_t* base, uint32_t victim, uint32_t hit, HitEvent& e) {
  e.victim = victim;
  e.victim_player = PlayerOf(base, victim);
  e.attacker = hit != 0 ? Deref(ctx, base, hit + kHitAttackerRef) : 0u;
  e.attacker_player = PlayerOf(base, e.attacker);
  e.hit = hit;
  e.damage = hit != 0 ? LoadF(base, hit + kHitDamage) : 0.0f;
  e.spell_type = hit != 0 ? static_cast<int32_t>(Load32(base, hit + kHitSpellType)) : -1;
  if (e.spell_type < -1 || e.spell_type > 7) {
    e.spell_type = -1;
  }
  e.spell_level = hit != 0 ? static_cast<int32_t>(Load32(base, hit + kHitLevel)) : 0;
  e.spell_serial = hit != 0 ? Load32(base, hit + kHitSerial) : 0u;
}

/// The game's own damage for a hit, through rex_CECHealth_CalculateDamage.
float GameDamage(PPCContext& ctx, uint8_t* base, uint32_t health, uint32_t hit) {
  PPCContext work = ctx;
  work.r3.u32 = health;
  work.r4.u32 = hit;
  __imp__rex_CECHealth_CalculateDamage(work, base);
  return static_cast<float>(work.f1.f64);
}

/// One Block event per hit: the game runs two block listeners per hit, and the second takes the
/// first's verdict. The verdict is ONE-SHOT — the second listener's call consumes it — and keyed
/// by the hit's SEQUENCE (counted at the receiver every hit reaches, before its listeners run)
/// and the blocker: the dispatch's hit copy is one stack local (sub_825C5220's), at the same
/// address for every hit of a tick, so the address tells two hits of a tick apart from nothing.
struct BlockVerdict {
  uint32_t sequence = 0;
  uint32_t blocker = 0;
  bool holds = true;
};
BlockVerdict g_block_verdicts[8];
int g_block_next = 0;
uint32_t g_tick = 0;
/// Hits delivered so far, counted at the receiver (rex_CECHitReceiver_OnHit).
uint32_t g_hit_sequence = 0;

bool AnySubscriber(int32_t kind) {
  if (LuaWants(kind)) {
    return true;
  }
  for (const Subscription& s : Subscriptions()) {
    if (s.kind == kind) {
      return true;
    }
  }
  return false;
}

/// Heroes seen last tick, for HeroReady / HeroGone.
uint32_t g_hero_seen[2] = {0, 0};

}  // namespace

// ---------------------------------------------------------------------------------------------
// The bus
// ---------------------------------------------------------------------------------------------

extern "C" int32_t refii_mod_subscribe(int32_t kind, refii_mod_event_fn fn, void (*user)(void), int32_t priority,
                                       const char* owner) {
  if (fn == nullptr || kind < 0) {
    return -1;
  }
  std::vector<Subscription>& table = Subscriptions();
  const int32_t order = static_cast<int32_t>(table.size());
  table.push_back(Subscription{kind, fn, user, priority, owner != nullptr ? owner : "?", order});
  // Higher priority first; ties keep registration order.
  for (size_t i = table.size() - 1; i > 0; --i) {
    if (table[i - 1].priority >= table[i].priority) {
      break;
    }
    std::swap(table[i - 1], table[i]);
  }
  return order;
}

extern "C" int32_t refii_mod_api_version(void) { return REFII_MOD_API_VERSION; }

extern "C" void refii_mod_dispatch(int32_t kind, void* event) {
  if (event == nullptr) {
    return;
  }
  for (const Subscription& s : Subscriptions()) {
    if (s.kind == kind) {
      s.fn(event, s.user);
    }
  }
  DispatchLua(kind, event);
}

namespace refii::vfs {

/// From RunModTicks, after the registered tick callbacks: the Tick event, the hero events,
/// and the Lua mask upkeep.
void RunModEventsTick(PPCContext& ctx, uint8_t* base, float seconds) {
  ++g_tick;
  PumpLuaMask(ctx, base, seconds);

  for (int32_t p = 1; p <= 2; ++p) {
    const uint32_t hero = refii_mod_player_hero(base, p);
    uint32_t& seen = g_hero_seen[p - 1];
    if (hero == seen) {
      continue;
    }
    if (seen != 0) {
      HeroGoneEvent gone{};
      PPCContext gone_ctx = ctx;
      gone.ctx = &gone_ctx;
      gone.base = base;
      gone.player = p;
      gone.hero = seen;
      Dispatch(gone);
    }
    seen = hero;
    if (hero != 0) {
      HeroEvent ready{};
      PPCContext ready_ctx = ctx;
      ready.ctx = &ready_ctx;
      ready.base = base;
      ready.player = p;
      ready.hero = hero;
      Dispatch(ready);
    }
    char chunk[96];
    std::snprintf(chunk, sizeof(chunk), "if Refii then Refii._hero[%d] = %u end", p, hero);
    refii_mod_run_game_lua(&ctx, base, chunk);
  }

  TickEvent tick{};
  PPCContext tick_ctx = ctx;
  tick.ctx = &tick_ctx;
  tick.base = base;
  tick.seconds = seconds;
  Dispatch(tick);
}

/// From mod_lua_hooks.cpp: the game's Lua was (re)built and the mods' init ran, or a save's
/// Lua table was loaded.
void DispatchLuaEvent(PPCContext& ctx, uint8_t* base, bool save_loaded) {
  const uint32_t generation = refii_mod_lua_generation();
  g_lua_mask_generation = 0xFFFFFFFFu;  // re-read on the next tick: handlers may have changed
  if (save_loaded) {
    SaveLoadedEvent e{};
    PPCContext e_ctx = ctx;
    e.ctx = &e_ctx;
    e.base = base;
    e.generation = generation;
    ReadLuaMask(ctx, base);
    g_lua_mask_generation = generation;
    Dispatch(e);
  } else {
    LuaEvent e{};
    PPCContext e_ctx = ctx;
    e.ctx = &e_ctx;
    e.base = base;
    e.generation = generation;
    ReadLuaMask(ctx, base);
    g_lua_mask_generation = generation;
    Dispatch(e);
  }
}

}  // namespace refii::vfs

// ---------------------------------------------------------------------------------------------
// The seams
// ---------------------------------------------------------------------------------------------

// The hero's GameAction receiver: r3 = the receiver, r4 = the 24-byte action message whose
// first word is the GameAction (`lwz r11,0(r4)`); the receiver's entity is the weak handle at
// +8 resolved as *(*(r3+0xC)) (sub_82173E38). TU1 0x826E5B30 / disc 0x827428B0.
REX_EXTERN(rex_CPlayerActionReceiver_ProcessAction) {
  REX_FUNC_PROLOGUE();
  if (AnySubscriber(REFII_EVENT_GAME_ACTION)) {
    const uint32_t control = Load32(base, ctx.r3.u32 + 0xCu);
    const uint32_t hero = control != 0 ? Load32(base, control) : 0u;
    const int32_t player = PlayerOf(base, hero);
    if (player != 0) {
      GameActionEvent e{};
      PPCContext e_ctx = ctx;
      e.ctx = &e_ctx;
      e.base = base;
      e.hero = hero;
      e.player = player;
      e.action = Load32(base, ctx.r4.u32);
      e.message = ctx.r4.u32;
      Dispatch(e);
      if (e.cancel) {
        return;
      }
    }
  }
  __imp__rex_CPlayerActionReceiver_ProcessAction(ctx, base);
}

// Every ability request any creature makes: r3 = the entity, r4 = the request, whose vtable
// slot 1 returns its AbilityType (0x826f5f7c `cmpwi r3,0x10` is the melee press). Not calling
// the original refuses the request; r3 = 0 is what it returns when nothing matched.
// TU1 0x826F57E8 / disc 0x8273DA00.
REX_EXTERN(rex_CECAbilities_PerformRequest) {
  REX_FUNC_PROLOGUE();
  if (AnySubscriber(REFII_EVENT_ABILITY_REQUEST)) {
    PPCContext query = ctx;
    AbilityRequestEvent e{};
    PPCContext e_ctx = ctx;
    e.ctx = &e_ctx;
    e.base = base;
    e.entity = ctx.r3.u32;
    e.player = PlayerOf(base, e.entity);
    e.request = ctx.r4.u32;
    e.type = refii::mods::CallVirtual(query, base, ctx.r4.u32, 1) ? query.r3.u32 : 0u;
    Dispatch(e);
    if (e.cancel) {
      ctx.r3.u64 = 0;
      return;
    }
  }
  __imp__rex_CECAbilities_PerformRequest(ctx, base);
}

// Called once by every strike's Perform after the swing's animation has started: r3 = the
// ability, r4 = the entity. The entity's action manager (component 49) then holds the live
// clone at +0x10. TU1 0x8297BD68 / disc 0x829AEB98.
REX_EXTERN(rex_CAbilityActionMelee_CommitSwing) {
  REX_FUNC_PROLOGUE();
  const uint32_t ability = ctx.r3.u32;
  const uint32_t entity = ctx.r4.u32;
  __imp__rex_CAbilityActionMelee_CommitSwing(ctx, base);
  if (AnySubscriber(REFII_EVENT_MELEE_SWING)) {
    MeleeSwingEvent e{};
    PPCContext e_ctx = ctx;
    e.ctx = &e_ctx;
    e.base = base;
    e.entity = entity;
    e.player = PlayerOf(base, entity);
    e.ability = ability;
    const uint32_t manager = entity != 0 ? refii_mod_component(base, entity, kActionManagerComponent) : 0u;
    e.action = manager != 0 ? Load32(base, manager + 0x10u) : 0u;
    Dispatch(e);
  }
}

// The roll ability's vtable slots 1 and 2 (TU1 0x820CC5D8, disc 0x820CC480): CanStart (r4 =
// the entity; 1 allows; asked once per matching roll record, so it gates without spending) and
// Start (r4 = the entity; returns 1 only when the roll was queued). TU1 0x8297A100 / 0x8297A7E8,
// disc 0x829A9718 / 0x829A9E00.
REX_EXTERN(rex_CAbilityActionStrafeRoll_CanStart) {
  REX_FUNC_PROLOGUE();
  if (AnySubscriber(REFII_EVENT_ROLL_CAN_START)) {
    RollEvent e{};
    PPCContext e_ctx = ctx;
    e.ctx = &e_ctx;
    e.base = base;
    e.entity = ctx.r4.u32;
    e.player = PlayerOf(base, e.entity);
    Dispatch(e);
    if (e.cancel) {
      ctx.r3.u64 = 0;
      return;
    }
  }
  __imp__rex_CAbilityActionStrafeRoll_CanStart(ctx, base);
}

REX_EXTERN(rex_CAbilityActionStrafeRoll_Start) {
  REX_FUNC_PROLOGUE();
  const uint32_t entity = ctx.r4.u32;
  __imp__rex_CAbilityActionStrafeRoll_Start(ctx, base);
  if (ctx.r3.u32 != 0 && AnySubscriber(REFII_EVENT_ROLL_STARTED)) {
    RollStartedEvent e{};
    PPCContext e_ctx = ctx;
    e.ctx = &e_ctx;
    e.base = base;
    e.entity = entity;
    e.player = PlayerOf(base, entity);
    Dispatch(e);
  }
}

namespace {

// The two hit listeners a block action registers on its victim's hit dispatch
// (`sub_825C4FC8(comp31, X+0x58, X+0x54, 0)` in the block base's Start, TU1 0x8270B698), at
// priority 0, ahead of health's 30. Both take r3 = the listener at X+0x54 of the block action X,
// r4 = the hit, r5 = the result word; a non-zero result makes the health listener skip the hit
// (0x824caa1c). NOT calling the original leaves the result at zero and the hit lands in full.
// The blocker is *(*(X+8)). Hit +0x19 marks it unblockable (the originals skip those).
bool GuardHolds(PPCContext& ctx, uint8_t* base, uint32_t listener, uint32_t hit) {
  if (!AnySubscriber(REFII_EVENT_BLOCK) || hit == 0) {
    return true;
  }
  const uint32_t action = listener - 0x54u;
  const uint32_t holder = Load32(base, action + 8u);
  const uint32_t blocker = holder != 0 ? Load32(base, holder) : 0u;
  for (BlockVerdict& v : g_block_verdicts) {
    if (v.sequence == g_hit_sequence && v.blocker == blocker) {
      const bool holds = v.holds;
      v = BlockVerdict{};  // consumed: the next hit is its own event
      return holds;
    }
  }
  BlockEvent e{};
  PPCContext e_ctx = ctx;
  e.ctx = &e_ctx;
  e.base = base;
  e.blocker = blocker;
  e.player = PlayerOf(base, e.blocker);
  e.hit = hit;
  e.damage = LoadF(base, hit + kHitDamage);
  e.unblockable = Load8(base, hit + kHitUnblockable) != 0;
  Dispatch(e);
  const bool holds = e.unblockable || !e.cancel;
  BlockVerdict& slot = g_block_verdicts[g_block_next];
  g_block_next = (g_block_next + 1) % 8;
  slot = BlockVerdict{g_hit_sequence, blocker, holds};
  return holds;
}

}  // namespace

REX_EXTERN(rex_CActionBlock_OnHitBlockPose) {
  REX_FUNC_PROLOGUE();
  if (!GuardHolds(ctx, base, ctx.r3.u32, ctx.r4.u32)) {
    return;
  }
  __imp__rex_CActionBlock_OnHitBlockPose(ctx, base);
}

REX_EXTERN(rex_CActionBlock_OnHitBlockedResponse) {
  REX_FUNC_PROLOGUE();
  if (!GuardHolds(ctx, base, ctx.r3.u32, ctx.r4.u32)) {
    return;
  }
  __imp__rex_CActionBlock_OnHitBlockedResponse(ctx, base);
}

// Every action's animation-event handler: r3 = the live action, r4 = the animation id, r6 =
// the event's value object (`**r6` is the text). The action states its owner: the weak
// reference at action+4, body at +8, the entity its word (`lwz r11,8(r3)` then `lwz r11,0(r11)`
// 0x82709958/0x8270997c — the handler itself looks the owner's components up through it). For
// "ALLOW_ACTION_INTERRUPT" the game does `if (!*(action+0x4C)) *(action+0x4D) = 1` (0x82709cac)
// and nothing else. TU1 0x82709938 / disc 0x826DF198.
REX_EXTERN(rex_CAction_OnAnimEvent) {
  REX_FUNC_PROLOGUE();
  if (AnySubscriber(REFII_EVENT_ANIM_EVENT)) {
    const uint32_t holder = ctx.r6.u32 != 0 ? Load32(base, ctx.r6.u32) : 0u;
    const uint32_t text = holder != 0 ? Load32(base, holder) : 0u;
    AnimEventEvent e{};
    PPCContext e_ctx = ctx;
    e.ctx = &e_ctx;
    e.base = base;
    e.action = ctx.r3.u32;
    e.name = text != 0 ? reinterpret_cast<const char*>(base + text) : "";
    const uint32_t owner_body = e.action != 0 ? Load32(base, e.action + 8u) : 0u;
    e.entity = owner_body != 0 ? Load32(base, owner_body) : 0u;
    e.player = PlayerOf(base, e.entity);
    Dispatch(e);
    if (e.cancel) {
      return;
    }
  }
  __imp__rex_CAction_OnAnimEvent(ctx, base);
}

// The one receiver every hit reaches: r3 = the victim's component 31 (owner entity at +4,
// `lwz r4,4(r23)` 0x825c5288), r4 = the hit (the dispatch's own mutable copy), r5 = a flag.
// Its own gates come first, before the copy and before any listener: a receiver with +0x18
// clear drops every hit (`lbz r10,0x18(r23); beq` 0x825c5264), and one with +0x19 clear drops
// every hit whose kind at +0x58 is not 8 (`lwz r10,0x58(r4); cmpwi r10,8` 0x825c5248,
// `lbz r11,0x19(r23)` 0x825c5258, `beq` 0x825c5278). Those never fire Hit or HitLanded.
// TU1 0x825C5220 / disc 0x82483AB0.
REX_EXTERN(rex_CECHitReceiver_OnHit) {
  REX_FUNC_PROLOGUE();
  const uint32_t component = ctx.r3.u32;
  const uint32_t hit = ctx.r4.u32;
  ++g_hit_sequence;
  const bool pre = AnySubscriber(REFII_EVENT_HIT);
  const bool post = AnySubscriber(REFII_EVENT_HIT_LANDED);
  const bool taken = component != 0 && hit != 0 && Load8(base, component + 0x18u) != 0 &&
                     (Load32(base, hit + kHitKind) == 8u || Load8(base, component + 0x19u) != 0);
  if ((!pre && !post) || !taken) {
    __imp__rex_CECHitReceiver_OnHit(ctx, base);
    return;
  }
  const uint32_t victim = component != 0 ? Load32(base, component + 4u) : 0u;
  HitEvent e{};
  PPCContext e_ctx = ctx;
  e.ctx = &e_ctx;
  e.base = base;
  ReadHit(ctx, base, victim, hit, e);
  if (pre) {
    const float damage = e.damage;
    Dispatch(e);
    if (e.cancel) {
      return;
    }
    if (hit != 0 && e.damage != damage) {
      refii::mods::StoreF32(base, hit + kHitDamage, e.damage);
    }
  }
  __imp__rex_CECHitReceiver_OnHit(ctx, base);
  if (post) {
    HitLandedEvent landed{};
    PPCContext landed_ctx = ctx;
    landed.ctx = &landed_ctx;
    landed.base = base;
    static_cast<HitEvent&>(landed) = e;
    landed.cancel = false;
    Dispatch(landed);
  }
}

// The hit-response component's listener on the victim's hit dispatch, at priority 20 (before
// health's 30): r3 = the listener (component+12; the entity is at r3-8), r4 = the dispatch's
// copy of the hit, r5 = the result word (non-zero once a block or parry took the hit). It alone
// starts the flinch (GetHit, sub_828B6F38) or the knockdown (sub_82689268); returning without it
// plays nothing and leaves no state. Its own early-outs are kept: hit+0x1C (no animated
// response), the result word, hit+0x18 (a knockdown request), a hit of kind 7 at +0x58 (nothing
// at all, 0x8295e014), and a lethal hit by the game's own test (sub_824CA908: never lethal while
// health+0x131 or +0x133 is set, or +0x132 is set with the hit's +0x18 clear; else CalculateDamage
// >= health+0x14). The hero-invulnerable globals that test also reads are left out.
// TU1 0x8295CF08 / disc 0x8296D570.
REX_EXTERN(rex_CECHitResponsePlayAction_OnHit) {
  REX_FUNC_PROLOGUE();
  if (AnySubscriber(REFII_EVENT_HIT_RESPONSE)) {
    const uint32_t listener = ctx.r3.u32;
    const uint32_t hit = ctx.r4.u32;
    const uint32_t result = ctx.r5.u32;
    const uint32_t entity = listener != 0 ? Load32(base, listener - 8u) : 0u;
    const uint32_t health = entity != 0 ? refii_mod_component(base, entity, kHealthComponent) : 0u;
    const bool pass = health == 0 || hit == 0 || result == 0 || Load32(base, result) != 0 ||
                      Load8(base, hit + kHitNoResponse) != 0 || Load8(base, hit + kHitKnockdown) != 0 ||
                      Load32(base, hit + kHitKind) == 7u;
    if (!pass) {
      HitResponseEvent e{};
      PPCContext e_ctx = ctx;
      e.ctx = &e_ctx;
      e.base = base;
      e.victim = entity;
      e.victim_player = PlayerOf(base, entity);
      e.hit = hit;
      e.damage = GameDamage(ctx, base, health, hit);
      e.health = LoadF(base, health + 0x14u);
      e.max_health = LoadF(base, health + 0x1Cu);
      const bool never_lethal = Load8(base, health + 0x131u) != 0 || Load8(base, health + 0x133u) != 0 ||
                                (Load8(base, health + 0x132u) != 0 && Load8(base, hit + kHitKnockdown) == 0);
      if (never_lethal || e.damage < e.health) {  // a killing blow's fall always plays
        Dispatch(e);
        if (e.cancel) {
          return;
        }
      }
    }
  }
  __imp__rex_CECHitResponsePlayAction_OnHit(ctx, base);
}

// The B-press ability's gate (AbilityType 18): r4 = the entity; 0 refuses the charge, no side
// effects. TU1 0x82989A80 / disc 0x829B9040.
REX_EXTERN(rex_CAbilityActionMagicCharge_CanStart) {
  REX_FUNC_PROLOGUE();
  if (AnySubscriber(REFII_EVENT_SPELL_CHARGE)) {
    SpellChargeEvent e{};
    PPCContext e_ctx = ctx;
    e.ctx = &e_ctx;
    e.base = base;
    e.entity = ctx.r4.u32;
    e.player = PlayerOf(base, e.entity);
    Dispatch(e);
    if (e.cancel) {
      ctx.r3.u64 = 0;
      return;
    }
  }
  __imp__rex_CAbilityActionMagicCharge_CanStart(ctx, base);
}

// Turns the charge time into the slots to cast, through TWO int* outputs: r6 = the COUNT of
// charged slots (`stw r10,0(r31)` zero, `addi r11,r11,1; stw r11,0(r31)` per slot reached
// 0x8253e514, floored at 1 0x8253e53c), r7 = the FIRST slot, 0 unless the single-slot mode byte
// is set (0x8253e544: `*r7 = *r6 - 1; *r6 = 1`). The release reads both: the count becomes the
// slots still to come (+0x16C -> +0x170, 0x8253e62c) and the cascade starts at the first
// (+0x168, 0x8253ee3c), so the top slot cast is first + count - 1 in either mode. r3 = the spell
// manager (component 56, caster at +4). TU1 0x8253E4B8 / disc 0x82428EF8.
REX_EXTERN(rex_CECSpellManager_ChargeLevelFromTime) {
  REX_FUNC_PROLOGUE();
  const uint32_t manager = ctx.r3.u32;
  const uint32_t count_out = ctx.r6.u32;
  const uint32_t first_out = ctx.r7.u32;
  __imp__rex_CECSpellManager_ChargeLevelFromTime(ctx, base);
  if (count_out != 0 && first_out != 0 && manager != 0 && AnySubscriber(REFII_EVENT_SPELL_CHARGE_LEVEL)) {
    const int32_t count = static_cast<int32_t>(Load32(base, count_out));
    const int32_t first = static_cast<int32_t>(Load32(base, first_out));
    SpellChargeLevelEvent e{};
    PPCContext e_ctx = ctx;
    e.ctx = &e_ctx;
    e.base = base;
    e.caster = Load32(base, manager + kManagerCaster);
    e.player = PlayerOf(base, e.caster);
    e.level = first + count - 1;
    const int32_t level = e.level;
    Dispatch(e);
    if (e.level != level) {
      const int32_t clamped = e.level < 0 ? 0 : (e.level > 4 ? 4 : e.level);
      if (first != 0) {
        // Single-slot mode: the one slot moves, the count stays 1.
        refii::mods::StoreU32(base, first_out, static_cast<uint32_t>(clamped));
      } else {
        // The cascade: slots 0..top, so the count is top + 1.
        refii::mods::StoreU32(base, count_out, static_cast<uint32_t>(clamped + 1));
      }
    }
  }
}

// Launches one slot: r3 = the spell manager (component 56; the caster at +4), r4 = targeted
// flag, r5 = the slot 0..4 (also the power level). The type is the manager's slot table, int8
// [2][5] at +0x80, row = flag, column = slot, -1 empty (`lbz r9,0x80(r10)` 0x8253f0e8). The
// release cascade (state +0x70 == 5) launches slots 0..top in turn, +0x170 the slots still to
// come. TU1 0x8253F010 / disc 0x82429A50.
REX_EXTERN(rex_CECSpellManager_LaunchSlot) {
  REX_FUNC_PROLOGUE();
  const uint32_t manager = ctx.r3.u32;
  const uint32_t flag = ctx.r4.u32;
  const uint32_t slot = ctx.r5.u32;
  if (manager == 0 || flag > 1 || slot >= kSlots || !AnySubscriber(REFII_EVENT_SPELL_LAUNCH)) {
    __imp__rex_CECSpellManager_LaunchSlot(ctx, base);
    return;
  }
  const uint32_t cell = manager + kManagerSlotTable + flag * kSlots + slot;
  const int32_t stated = static_cast<int8_t>(Load8(base, cell));
  SpellLaunchEvent e{};
  PPCContext e_ctx = ctx;
  e.ctx = &e_ctx;
  e.base = base;
  e.caster = Load32(base, manager + kManagerCaster);
  e.player = PlayerOf(base, e.caster);
  e.manager = manager;
  e.targeted = flag != 0;
  e.slot = static_cast<int32_t>(slot);
  e.type = stated;
  e.cascading = Load32(base, manager + kManagerState) == kStateCascade &&
                static_cast<int32_t>(Load32(base, manager + kManagerSlotsToCome)) > 0;
  Dispatch(e);
  if (e.cancel) {
    return;
  }
  // The stated type stands in the slot cell for the length of the game's own launch, so
  // position, target, targeted/area and the launch notification are all the game's.
  const bool swap = e.type != stated && e.type >= -1 && e.type <= 7;
  if (swap) {
    refii::mods::StoreU8(base, cell, static_cast<uint8_t>(static_cast<int8_t>(e.type)));
  }
  __imp__rex_CECSpellManager_LaunchSlot(ctx, base);
  if (swap) {
    refii::mods::StoreU8(base, cell, static_cast<uint8_t>(static_cast<int8_t>(stated)));
  }
}

// Called only when a spell was really created (0x8253f148, behind the launch's own success
// test): r3 = the spell manager, r4 = the slot launched. TU1 0x82543A20 / disc 0x8242E460.
REX_EXTERN(rex_CECSpellManager_OnSpellLaunched) {
  REX_FUNC_PROLOGUE();
  if (ctx.r3.u32 != 0 && AnySubscriber(REFII_EVENT_SPELL_LAUNCHED)) {
    SpellLaunchedEvent e{};
    PPCContext e_ctx = ctx;
    e.ctx = &e_ctx;
    e.base = base;
    e.manager = ctx.r3.u32;
    e.caster = Load32(base, e.manager + kManagerCaster);
    e.player = PlayerOf(base, e.caster);
    e.slot = static_cast<int32_t>(ctx.r4.u32);
    Dispatch(e);
  }
  __imp__rex_CECSpellManager_OnSpellLaunched(ctx, base);
}

// The spell factory the launcher calls for every cast: r3 registry = *(W + 0x44), r4 spell
// type, r5 1 targeted / 0 area, r6 target entity, r7 power level 0..4, r8 caster entity, r9 the
// manager's definitions word; v1 position, v2 direction. The spell object's serial is at +0x44.
// TU1 0x824B1828 / disc 0x8249BB60.
REX_EXTERN(rex_CSpellRegistry_Create) {
  REX_FUNC_PROLOGUE();
  const PPCContext entry = ctx;
  __imp__rex_CSpellRegistry_Create(ctx, base);
  if (ctx.r3.u32 != 0 && AnySubscriber(REFII_EVENT_SPELL_CREATED)) {
    SpellCreatedEvent e{};
    PPCContext e_ctx = ctx;
    e.ctx = &e_ctx;
    e.base = base;
    e.spell = ctx.r3.u32;
    e.type = static_cast<int32_t>(entry.r4.u32);
    e.targeted = entry.r5.u32 != 0;
    e.target = entry.r6.u32;
    e.level = static_cast<int32_t>(entry.r7.u32);
    e.caster = entry.r8.u32;
    e.caster_player = PlayerOf(base, e.caster);
    e.serial = Load32(base, e.spell + kSpellSerial);
    Dispatch(e);
  }
}

namespace {

/// Experience: Modify (r3 component 164, r4 pool, f1 amount, r6..r8 flags) and Spend (r3
/// component, r4 pool, f1 cost). The component's owner entity is at +4, as every component's.
/// TU1 0x8256DA78 / 0x8256E2F0, disc 0x8251C0A8 / 0x8251C920.
bool ExperienceEventOf(PPCContext& ctx, uint8_t* base, bool spend, ExperienceEvent& e) {
  PPCContext e_ctx = ctx;
  e.ctx = &e_ctx;
  e.base = base;
  e.component = ctx.r3.u32;
  e.entity = e.component != 0 ? Load32(base, e.component + 4u) : 0u;
  // The owner at +4 is trusted only when the entity's own experience component is this one.
  if (e.entity != 0 && refii_mod_component(base, e.entity, kExperienceComponent) != e.component) {
    e.entity = 0;
  }
  e.player = PlayerOf(base, e.entity);
  e.pool = static_cast<int32_t>(ctx.r4.u32);
  e.amount = static_cast<float>(ctx.f1.f64);
  e.spend = spend;
  const float amount = e.amount;
  Dispatch(e);
  if (e.cancel) {
    return false;
  }
  if (e.amount != amount) {
    ctx.f1.f64 = static_cast<double>(e.amount);
  }
  return true;
}

}  // namespace

REX_EXTERN(rex_CECExperience_Modify) {
  REX_FUNC_PROLOGUE();
  if (AnySubscriber(REFII_EVENT_EXPERIENCE)) {
    ExperienceEvent e{};
    if (!ExperienceEventOf(ctx, base, false, e)) {
      return;
    }
  }
  __imp__rex_CECExperience_Modify(ctx, base);
}

REX_EXTERN(rex_CECExperience_Spend) {
  REX_FUNC_PROLOGUE();
  if (AnySubscriber(REFII_EVENT_EXPERIENCE)) {
    ExperienceEvent e{};
    if (!ExperienceEventOf(ctx, base, true, e)) {
      return;
    }
  }
  __imp__rex_CECExperience_Spend(ctx, base);
}

// ---------------------------------------------------------------------------------------------
// The second wave: death, damage, the level, the save, items, abilities, gold
// ---------------------------------------------------------------------------------------------

namespace {

constexpr uint32_t kHealthCurrent = 0x14;
constexpr uint32_t kHealthMax = 0x1C;
constexpr uint32_t kCreatureAlive = 0x18;
constexpr uint32_t kEntityDefinition = 0x1C;
constexpr uint32_t kMoneyAmount = 12;
constexpr uint32_t kAbilityTable = 0x110;

/// The game's own CantDie answer: health back above the zero test (1e-4), so the next hit asks
/// again. 1.0, as the hero path writes at 0x824ce8c4.
void KeepAlive(uint8_t* base, uint32_t entity) {
  const uint32_t health = refii_mod_component(base, entity, kHealthComponent);
  if (health != 0) {
    const float max = LoadF(base, health + kHealthMax);
    refii::mods::StoreF32(base, health + kHealthCurrent, max > 0.0f && max < 1.0f ? max : 1.0f);
  }
}

/// A text through an LHString handle (a pointer to the body whose first word is the char*).
const char* LhString(uint8_t* base, uint32_t handle_at) {
  const uint32_t body = handle_at != 0 ? Load32(base, handle_at) : 0u;
  const uint32_t text = body >= 0x10000u && body < 0xC0000000u ? Load32(base, body) : 0u;
  return text >= 0x10000u && text < 0xC0000000u ? reinterpret_cast<const char*>(base + text) : "";
}

/// The current level's name from the world manager (W+8): +0x24 the holder, +4 the level, +0xAC
/// its name — the walk the game's own IsLevelLoaded makes (0x8228766c..0x822876ac).
const char* LevelName(uint8_t* base, uint32_t manager) {
  const uint32_t holder = manager != 0 ? Load32(base, manager + 0x24u) : 0u;
  const uint32_t level = holder >= 0x10000u && holder < 0xC0000000u ? Load32(base, holder + 4u) : 0u;
  return level >= 0x10000u && level < 0xC0000000u ? LhString(base, level + 0xACu) : "";
}

}  // namespace

// Every creature death: r3 = the creature component (20; owner at +4, alive byte +0x18, the
// only writer that clears it is this function at 0x823a4ec8), r4 = the killer entity or 0. It
// no-ops itself when the byte is already clear (0x823a4ebc), so the event follows that.
// TU1 0x823A4E98 (size 0x1DBC).
REX_EXTERN(rex_CECCreature_Die) {
  REX_FUNC_PROLOGUE();
  const uint32_t creature = ctx.r3.u32;
  if (creature != 0 && Load8(base, creature + kCreatureAlive) != 0 && AnySubscriber(REFII_EVENT_ENTITY_DIED)) {
    EntityDiedEvent e{};
    PPCContext e_ctx = ctx;
    e.ctx = &e_ctx;
    e.base = base;
    e.entity = Load32(base, creature + 4u);
    e.player = PlayerOf(base, e.entity);
    e.killer = ctx.r4.u32;
    e.killer_player = PlayerOf(base, e.killer);
    Dispatch(e);
    if (e.cancel) {
      KeepAlive(base, e.entity);
      return;
    }
  }
  __imp__rex_CECCreature_Die(ctx, base);
}

// The hero branch of the zero-health dispatch (0x824ce568): r3 = the health component (owner
// at +4), r4 = the killer or 0. It writes health 1.0 itself on its way in (0x824ce8c4).
// TU1 0x824CE7F8 (size 0xC88).
REX_EXTERN(rex_CECHealth_OnHeroDeath) {
  REX_FUNC_PROLOGUE();
  const uint32_t health = ctx.r3.u32;
  if (health != 0 && AnySubscriber(REFII_EVENT_HERO_DOWNED)) {
    HeroDownedEvent e{};
    PPCContext e_ctx = ctx;
    e.ctx = &e_ctx;
    e.base = base;
    e.hero = Load32(base, health + 4u);
    e.player = PlayerOf(base, e.hero);
    e.killer = ctx.r4.u32;
    e.killer_player = PlayerOf(base, e.killer);
    Dispatch(e);
    if (e.cancel) {
      KeepAlive(base, e.hero);
      return;
    }
  }
  __imp__rex_CECHealth_OnHeroDeath(ctx, base);
}

// The health component's listener on the hit dispatch, priority 30: r3 = the listener
// (health+0xC; `addi r29,r23,-0xC` 0x824caa30; the entity at r3-8), r4 = the hit, r5 = the
// handled word (`lwz r11,0(r5)` 0x824caa1c, non-zero skips everything — a block or parry took
// the hit). It computes the damage (0x824caa38 CalculateDamage), then — unless health+0x132 is
// set and the hit's knockdown byte +0x18 is clear (`lbz r10,0x126(r23)` 0x824caa54, `lbz
// r11,0x18(r25)` 0x824caa64: a "knockdown only" guard that removes nothing) — decrements through
// Modify with the attacker (0x824cae6c), which runs the zero-health dispatch.
// TU1 0x824CAA08 (size 0x1070).
REX_EXTERN(rex_CECHealth_OnHit) {
  REX_FUNC_PROLOGUE();
  const uint32_t listener = ctx.r3.u32;
  const uint32_t hit = ctx.r4.u32;
  const uint32_t handled = ctx.r5.u32;
  const uint32_t health = listener != 0 ? listener - 0xCu : 0u;
  const bool removes = health != 0 && hit != 0 &&
                       (Load8(base, health + 0x132u) == 0 || Load8(base, hit + kHitKnockdown) != 0);
  if (removes && (handled == 0 || Load32(base, handled) == 0) && AnySubscriber(REFII_EVENT_DAMAGE)) {
    DamageEvent e{};
    PPCContext e_ctx = ctx;
    e.ctx = &e_ctx;
    e.base = base;
    e.victim = Load32(base, listener - 8u);
    e.victim_player = PlayerOf(base, e.victim);
    e.attacker = Deref(ctx, base, hit + kHitAttackerRef);
    e.attacker_player = PlayerOf(base, e.attacker);
    e.hit = hit;
    e.damage = GameDamage(ctx, base, health, hit);
    e.health = LoadF(base, health + kHealthCurrent);
    e.max_health = LoadF(base, health + kHealthMax);
    e.lethal = e.damage >= e.health;
    e.spell_type = static_cast<int32_t>(Load32(base, hit + kHitSpellType));
    if (e.spell_type < -1 || e.spell_type > 7) {
      e.spell_type = -1;
    }
    e.spell_level = static_cast<int32_t>(Load32(base, hit + kHitLevel));
    e.spell_serial = Load32(base, hit + kHitSerial);
    Dispatch(e);
    if (e.cancel) {
      return;
    }
  }
  __imp__rex_CECHealth_OnHit(ctx, base);
}

// The game's "level loaded" call into Lua (AIManager:OnLevelLoad): r3 = the script-system
// owner, r4 = the from-save byte. Called from the travel command's state 5 (r4 = 0) after every
// player's hero was placed, and from the load command's state 2 (r4 = 1). TU1 0x82460BF0.
REX_EXTERN(rex_lhLuaScriptSystem_OnLevelLoad) {
  REX_FUNC_PROLOGUE();
  const bool from_save = ctx.r4.u8 != 0;
  __imp__rex_lhLuaScriptSystem_OnLevelLoad(ctx, base);
  if (AnySubscriber(REFII_EVENT_LEVEL_LOADED)) {
    const uint32_t world = refii_mod_world(base);
    LevelLoadedEvent e{};
    PPCContext e_ctx = ctx;
    e.ctx = &e_ctx;
    e.base = base;
    e.level = LevelName(base, world != 0 ? Load32(base, world + 8u) : 0u);
    e.from_save = from_save;
    Dispatch(e);
  }
}

// Unloads the current level: r3 = the world manager; `lwz r4,0x24(r31)` 0x8231cba8 is the
// holder, 0 when nothing is loaded. TU1 0x8231CB90.
REX_EXTERN(rex_lhWorld_UnloadLevel) {
  REX_FUNC_PROLOGUE();
  const uint32_t manager = ctx.r3.u32;
  if (manager != 0 && Load32(base, manager + 0x24u) != 0 && AnySubscriber(REFII_EVENT_LEVEL_UNLOADING)) {
    LevelUnloadingEvent e{};
    PPCContext e_ctx = ctx;
    e.ctx = &e_ctx;
    e.base = base;
    e.level = LevelName(base, manager);
    Dispatch(e);
  }
  __imp__rex_lhWorld_UnloadLevel(ctx, base);
}

// Writes one save file: r3 = the save manager, r4 = the file name (an LHString handle), r5 a
// second name or 0; the Lua GetSaveTable runs inside (0x8244635c). TU1 0x82445DB8.
REX_EXTERN(rex_lhSaveManager_WriteSave) {
  REX_FUNC_PROLOGUE();
  if (AnySubscriber(REFII_EVENT_GAME_SAVING)) {
    GameSavingEvent e{};
    PPCContext e_ctx = ctx;
    e.ctx = &e_ctx;
    e.base = base;
    e.file = LhString(base, ctx.r4.u32);
    Dispatch(e);
  }
  __imp__rex_lhSaveManager_WriteSave(ctx, base);
}

// The one add every item goes through: r3 = the out shared pointer (four words; the item in the
// first), r4 = the inventory component (13; owner at +4), r5 = the definition hash, r6 = notify.
// A refusal writes four zero words at r3 and returns r3, as the hash-0 path does (0x824f0a34).
// TU1 0x824F0820.
REX_EXTERN(rex_CECInventory_AddOneOfRecord) {
  REX_FUNC_PROLOGUE();
  const uint32_t out = ctx.r3.u32;
  const uint32_t inventory = ctx.r4.u32;
  if (inventory != 0 && AnySubscriber(REFII_EVENT_ITEM_ADDED)) {
    ItemAddedEvent e{};
    PPCContext e_ctx = ctx;
    e.ctx = &e_ctx;
    e.base = base;
    e.inventory = inventory;
    e.entity = Load32(base, inventory + 4u);
    e.player = PlayerOf(base, e.entity);
    e.definition = ctx.r5.u32;
    e.notify = ctx.r6.u8 != 0;
    Dispatch(e);
    if (e.cancel) {
      if (out != 0) {
        for (uint32_t i = 0; i < 4; ++i) {
          refii::mods::StoreU32(base, out + 4u * i, 0u);
        }
      }
      ctx.r3.u32 = out;
      return;
    }
  }
  __imp__rex_CECInventory_AddOneOfRecord(ctx, base);
}

// Uses an inventory item: r3 = the entity, r4 = the item's shared pointer (item at *r4,
// definition at item+4, count at item+12), r5 = a script name override. Returns 1 when used.
// TU1 0x823E0FF8.
REX_EXTERN(rex_lhEntity_UseInventoryItem) {
  REX_FUNC_PROLOGUE();
  const uint32_t holder = ctx.r4.u32;
  const uint32_t item = holder != 0 ? Load32(base, holder) : 0u;
  if (item != 0 && AnySubscriber(REFII_EVENT_ITEM_USED)) {
    ItemUsedEvent e{};
    PPCContext e_ctx = ctx;
    e.ctx = &e_ctx;
    e.base = base;
    e.entity = ctx.r3.u32;
    e.player = PlayerOf(base, e.entity);
    e.item = item;
    e.definition = Load32(base, item + 4u);
    e.count = static_cast<int32_t>(Load32(base, item + 12u));
    Dispatch(e);
    if (e.cancel) {
      ctx.r3.u64 = 0;
      return;
    }
  }
  __imp__rex_lhEntity_UseInventoryItem(ctx, base);
}

// Held items (component 24): attach r3 = the component (owner at +4), r4 = the slot name
// (LHString), r5 = the item entity; release r3, r4, returning the item released or 0.
// TU1 0x824FB5D8 / 0x824FCB20.
REX_EXTERN(rex_CECHeldItems_AttachItemToSlot) {
  REX_FUNC_PROLOGUE();
  if (ctx.r3.u32 != 0 && AnySubscriber(REFII_EVENT_HELD_ITEM)) {
    HeldItemEvent e{};
    PPCContext e_ctx = ctx;
    e.ctx = &e_ctx;
    e.base = base;
    e.entity = Load32(base, ctx.r3.u32 + 4u);
    e.player = PlayerOf(base, e.entity);
    e.slot = LhString(base, ctx.r4.u32);
    e.item = ctx.r5.u32;
    e.attached = true;
    Dispatch(e);
    if (e.cancel) {
      return;
    }
  }
  __imp__rex_CECHeldItems_AttachItemToSlot(ctx, base);
}

REX_EXTERN(rex_CECHeldItems_ReleaseSlot) {
  REX_FUNC_PROLOGUE();
  if (ctx.r3.u32 != 0 && AnySubscriber(REFII_EVENT_HELD_ITEM)) {
    HeldItemEvent e{};
    PPCContext e_ctx = ctx;
    e.ctx = &e_ctx;
    e.base = base;
    e.entity = Load32(base, ctx.r3.u32 + 4u);
    e.player = PlayerOf(base, e.entity);
    e.slot = LhString(base, ctx.r4.u32);
    e.item = 0;
    e.attached = false;
    Dispatch(e);
    if (e.cancel) {
      ctx.r3.u64 = 0;
      return;
    }
  }
  __imp__rex_CECHeldItems_ReleaseSlot(ctx, base);
}

// Records an equipped weapon in the weapon set (component 28): r3 = the set (owner at +4), r4 =
// the item entity (definition at +0x1C). At equip time only. TU1 0x824AD108.
REX_EXTERN(rex_CECWeaponSet_RecordDrawnItem) {
  REX_FUNC_PROLOGUE();
  if (ctx.r3.u32 != 0 && ctx.r4.u32 != 0 && AnySubscriber(REFII_EVENT_WEAPON_EQUIPPED)) {
    WeaponEquippedEvent e{};
    PPCContext e_ctx = ctx;
    e.ctx = &e_ctx;
    e.base = base;
    e.entity = Load32(base, ctx.r3.u32 + 4u);
    e.player = PlayerOf(base, e.entity);
    e.item = ctx.r4.u32;
    e.definition = Load32(base, e.item + kEntityDefinition);
    Dispatch(e);
  }
  __imp__rex_CECWeaponSet_RecordDrawnItem(ctx, base);
}

// Sets an ability's level: r3 = the stats component (owner at +4; the table at +0x110 + 4*type),
// r4 = the type 1..14, r5 = the level (clamped inside), r6 = force. TU1 0x824E2F28.
REX_EXTERN(rex_CECStats_SetAbilityLevel) {
  REX_FUNC_PROLOGUE();
  const uint32_t stats = ctx.r3.u32;
  const int32_t type = static_cast<int32_t>(ctx.r4.u32);
  if (stats != 0 && type >= 0 && type <= 14 && AnySubscriber(REFII_EVENT_ABILITY_LEVEL)) {
    AbilityLevelEvent e{};
    PPCContext e_ctx = ctx;
    e.ctx = &e_ctx;
    e.base = base;
    e.entity = Load32(base, stats + 4u);
    e.player = PlayerOf(base, e.entity);
    e.type = type;
    e.level = static_cast<int32_t>(ctx.r5.u32);
    e.old_level = static_cast<int32_t>(Load32(base, stats + kAbilityTable + 4u * static_cast<uint32_t>(type)));
    const int32_t level = e.level;
    Dispatch(e);
    if (e.cancel) {
      return;
    }
    if (e.level != level) {
      ctx.r5.s32 = e.level < 0 ? 0 : e.level;
    }
  }
  __imp__rex_CECStats_SetAbilityLevel(ctx, base);
}

// Changes a purse (component 81): r3 = the component (owner at +4, the amount at +12), r4 = the
// signed delta, r5 = no split, r6 = co-op share, r7 = passive income. TU1 0x8242E578.
REX_EXTERN(rex_CECMoney_Modify) {
  REX_FUNC_PROLOGUE();
  const uint32_t purse = ctx.r3.u32;
  if (purse != 0 && AnySubscriber(REFII_EVENT_GOLD)) {
    GoldEvent e{};
    PPCContext e_ctx = ctx;
    e.ctx = &e_ctx;
    e.base = base;
    e.entity = Load32(base, purse + 4u);
    e.player = PlayerOf(base, e.entity);
    e.gold = static_cast<int32_t>(Load32(base, purse + kMoneyAmount));
    e.delta = ctx.r4.s32;
    e.passive = ctx.r7.u8 != 0;
    const int32_t delta = e.delta;
    Dispatch(e);
    if (e.cancel) {
      return;
    }
    if (e.delta != delta) {
      ctx.r4.s32 = e.delta;
    }
  }
  __imp__rex_CECMoney_Modify(ctx, base);
}
