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

#include <algorithm>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <string>
#include <memory>
#include <vector>

#include <rex/logging.h>

#include "core/guest_build.h"
#include "core/mod_values.h"
#include "core/refii_mod.h"
#include "core/refii_mod_events.h"
#include "hooks/ppc_externs.h"
#include "vfs_hooks_private.h"

PPC_EXTERN_FUNC(__imp__rex_CPlayerActionReceiver_ProcessAction);
PPC_EXTERN_FUNC(__imp__rex_CECCrime_Add);
PPC_EXTERN_FUNC(__imp__rex_CECOpinionReaction_Update);
PPC_EXTERN_FUNC(__imp__rex_CECOpinionReaction_Shutdown);
PPC_EXTERN_FUNC(__imp__rex_CECOpinionReaction_Reset);
PPC_EXTERN_FUNC(__imp__rex_CECOpinionReaction_SendIcon);
PPC_EXTERN_FUNC(__imp__rex_CECOpinionReaction_SendIconRemoved);
PPC_EXTERN_FUNC(__imp__rex_lhWorldIcons_Request);
PPC_EXTERN_FUNC(__imp__rex_lhWorldIcons_Remove);
PPC_EXTERN_FUNC(__imp__rex_lhWorldIcon_Update);
PPC_EXTERN_FUNC(__imp__rex_CECVillager_SpeechReaction);
PPC_EXTERN_FUNC(__imp__rex_CECVillager_ChooseTopic);
PPC_EXTERN_FUNC(__imp__rex_CECStats_UnlockAchievement);
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
PPC_EXTERN_FUNC(__imp__rex_CECCreature_FinishDeath);
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
PPC_EXTERN_FUNC(__imp__rex_CECFaction_RelationTo);
PPC_EXTERN_FUNC(__imp__rex_CECQuestTracker_SetAsActive);
PPC_EXTERN_FUNC(__imp__rex_CECQuestTracker_OnQuestEnded);
PPC_EXTERN_FUNC(__imp__rex_CECQuestTracker_SetObjectiveTagSilently);
PPC_EXTERN_FUNC(__imp__rex_CECTalk_PlayLine);
PPC_EXTERN_FUNC(__imp__rex_lhEntityManager_Add);
PPC_EXTERN_FUNC(__imp__rex_lhEntity_Destroy);
PPC_EXTERN_FUNC(__imp__rex_CECExpressionPerformer_StartExpression);
PPC_EXTERN_FUNC(__imp__rex_lhHeroStats_ModifyRenown);
PPC_EXTERN_FUNC(__imp__rex_lhHeroStats_ModifyMorality);
PPC_EXTERN_FUNC(__imp__rex_lhHeroStats_ModifyPurity);
PPC_EXTERN_FUNC(__imp__rex_CECPlayerFamily_Marry);
PPC_EXTERN_FUNC(__imp__rex_CECDog_PerformCommand);
PPC_EXTERN_FUNC(__imp__rex_CECFirearm_Fire);
PPC_EXTERN_FUNC(__imp__rex_lhEntity_AddComponent);
PPC_EXTERN_FUNC(__imp__rex_lhEntity_RemoveComponent);
PPC_EXTERN_FUNC(__imp__rex_CECTargetingPlayer_SetTarget);
PPC_EXTERN_FUNC(__imp__rex_CECTargetingPlayer_ClearTarget);
PPC_EXTERN_FUNC(__imp__rex_CECOpinionReaction_OnDeed);
PPC_EXTERN_FUNC(__imp__rex_CECOpinionReaction_ModifyAxisValue);
PPC_EXTERN_FUNC(__imp__rex_lhGameTimeManager_Update);
PPC_EXTERN_FUNC(__imp__rex_lhEnvironmentThemeManager_BlendToTheme);
PPC_EXTERN_FUNC(__imp__rex_SleepMenu_Show);
PPC_EXTERN_FUNC(__imp__rex_CmdLevelChange_Start);
PPC_EXTERN_FUNC(__imp__rex_FastTravelManager_TravelToStop);
PPC_EXTERN_FUNC(__imp__rex_lhGuiManager_EnterGuiMode);
PPC_EXTERN_FUNC(__imp__rex_lhGuiManager_ExitScreen);
PPC_EXTERN_FUNC(__imp__rex_lhGuiManager_SetScreen);
PPC_EXTERN_FUNC(__imp__rex_lhGuiManager_EnterGuiModeQuiet);
PPC_EXTERN_FUNC(rex_lhGameFlow_IsGuiActive);
PPC_EXTERN_FUNC(__imp__rex_lhPaintMap_DaySetAt);
PPC_EXTERN_FUNC(__imp__rex_lhMenuBox_Close);
PPC_EXTERN_FUNC(__imp__rex_SpellSlowTime_SetWorldScale);
PPC_EXTERN_FUNC(__imp__rex_lhRagdoll_GoFloppy);
PPC_EXTERN_FUNC(__imp__rex_lhRagdoll_Recover);
PPC_EXTERN_FUNC(__imp__rex_CECCreature_SetUnconscious);
PPC_EXTERN_FUNC(__imp__rex_HollowMan_SetCrumbled);
PPC_EXTERN_FUNC(__imp__rex_SpellRaiseDead_BindSummoner);
PPC_EXTERN_FUNC(__imp__rex_SpellDamoclesSword_Create);
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
/// The game's single-slot spell mode flag (ChargeLevelFromTime: `if (byte_8331918F) { *first =
/// *count - 1; *count = 1; }`).
constexpr uint32_t kSingleSlotMode = 0x8331918Fu;
/// LaunchSlot's slot override (`if (dword_83319248 >= 0) slot = dword_83319248`).
constexpr uint32_t kLaunchSlotOverride = 0x83319248u;

inline uint32_t Load32(const uint8_t* base, uint32_t at) { return refii::mods::LoadU32(base, at); }
inline uint8_t Load8(const uint8_t* base, uint32_t at) { return refii::mods::LoadU8(base, at); }
inline float LoadF(const uint8_t* base, uint32_t at) { return refii::mods::LoadF32(base, at); }

int32_t PlayerOf(uint8_t* base, uint32_t entity) { return refii_mod_player_of(base, entity); }

// ---------------------------------------------------------------------------------------------
// Lua handlers
// ---------------------------------------------------------------------------------------------

/// Which kinds have a Lua handler (bit = kind), as the VM last said; re-read on a generation
/// change and when Refii._dirty says a registration happened since.
/// Kinds with a Lua handler, in groups of 20 (Refii._masks[g] holds kinds 20*(g-1)..20*g-1: a
/// 32-bit float, the game Lua's number, is exact only to 24 bits).
constexpr int32_t kLuaMaskBits = 20;
constexpr int32_t kLuaMaskGroups = 4;
uint32_t g_lua_masks[kLuaMaskGroups] = {0, 0, 0, 0};
uint32_t g_lua_mask_generation = 0xFFFFFFFFu;
float g_lua_dirty_poll = 0.0f;
bool g_lua_running = false;
float g_lua_tick_accumulated = 0.0f;
constexpr float kLuaTickPeriod = 0.1f;
constexpr float kLuaDirtyPollSeconds = 1.0f;

bool LuaWants(int32_t kind) {
  if (kind < 0 || kind >= kLuaMaskBits * kLuaMaskGroups) {
    return false;
  }
  return ((g_lua_masks[kind / kLuaMaskBits] >> (kind % kLuaMaskBits)) & 1u) != 0u;
}

/// Reads every group of Refii._masks (each a whole number below 2^20).
void ReadLuaMask(PPCContext& ctx, uint8_t* base) {
  for (int32_t g = 0; g < kLuaMaskGroups; ++g) {
    char expr[96];
    std::snprintf(expr, sizeof(expr), "Refii and Refii._masks and Refii._masks[%d] or 0", g + 1);
    int32_t mask = 0;
    if (refii_mod_game_lua_int(&ctx, base, expr, 0, (1 << kLuaMaskBits) - 1, &mask) == 0) {
      mask = 0;
    }
    g_lua_masks[g] = static_cast<uint32_t>(mask);
  }
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

}  // namespace

/// Set by the native Refii._cancelled while a chunk runs: a handler returned true. A chunk that
/// fails to run (no VM, no Refii, a runtime error outside the handlers' pcalls) is never a cancel.
bool g_lua_cancelled = false;

namespace refii::vfs {
void NoteLuaCancelled() { g_lua_cancelled = true; }
}  // namespace refii::vfs

namespace {

/// Runs `Refii._fire(kind, {fields})`; true when a handler cancelled. Never nested.
bool FireLua(PPCContext& ctx, uint8_t* base, int32_t kind, const char* fields) {
  if (g_lua_running) {
    return false;
  }
  char chunk[900];
  const int length = std::snprintf(chunk, sizeof(chunk),
                                   "if Refii and Refii._fire and not Refii._fire(%d,{%s}) and Refii._cancelled then "
                                   "Refii._cancelled() end",
                                   kind, fields);
  if (length < 0 || static_cast<size_t>(length) >= sizeof(chunk)) {
    // A cut table literal would not compile; the event goes to Lua with no fields rather than not at all.
    std::snprintf(chunk, sizeof(chunk),
                  "if Refii and Refii._fire and not Refii._fire(%d,{}) and Refii._cancelled then Refii._cancelled() end",
                  kind);
  }
  g_lua_cancelled = false;
  g_lua_running = true;
  refii_mod_run_game_lua(&ctx, base, chunk);
  g_lua_running = false;
  const bool cancelled = g_lua_cancelled;
  g_lua_cancelled = false;
  return cancelled;
}

/// A float for a Lua or host table: nine significant digits (exact for a 32-bit float), and a
/// finite value — `inf` and `nan` are not Lua literals.
double Fin(double value) {
  if (std::isnan(value)) {
    return 0.0;
  }
  if (std::isinf(value)) {
    return value > 0 ? 3.4e38 : -3.4e38;
  }
  return value;
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
  // Lua's number is a 32-bit float: objects go as handles, hashes as hex strings.
  const auto H = [](uint32_t address) { return refii_mod_handle(address); };
  switch (kind) {
    case REFII_EVENT_TICK: {
      const auto& e = *static_cast<const TickEvent*>(event);
      std::snprintf(out, size, "seconds=%.9g", Fin(e.seconds));
      return;
    }
    case REFII_EVENT_HERO_READY:
    case REFII_EVENT_HERO_GONE: {
      const auto& e = *static_cast<const HeroEvent*>(event);
      std::snprintf(out, size, "player=%d,hero=%u", e.player, H(e.hero));
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
      std::snprintf(out, size, "hero=%u,player=%d,action=%u", H(e.hero), e.player, e.action);
      return;
    }
    case REFII_EVENT_ABILITY_REQUEST: {
      const auto& e = *static_cast<const AbilityRequestEvent*>(event);
      std::snprintf(out, size, "entity=%u,player=%d,type=%u", H(e.entity), e.player, e.type);
      return;
    }
    case REFII_EVENT_MELEE_SWING: {
      const auto& e = *static_cast<const MeleeSwingEvent*>(event);
      std::snprintf(out, size, "entity=%u,player=%d", H(e.entity), e.player);
      return;
    }
    case REFII_EVENT_ROLL_CAN_START:
    case REFII_EVENT_ROLL_STARTED: {
      const auto& e = *static_cast<const RollEvent*>(event);
      std::snprintf(out, size, "entity=%u,player=%d", H(e.entity), e.player);
      return;
    }
    case REFII_EVENT_BLOCK: {
      const auto& e = *static_cast<const BlockEvent*>(event);
      std::snprintf(out, size, "blocker=%u,player=%d,damage=%.9g,unblockable=%s", H(e.blocker), e.player,
                    Fin(e.damage), e.unblockable ? "true" : "false");
      return;
    }
    case REFII_EVENT_ANIM_EVENT: {
      const auto& e = *static_cast<const AnimEventEvent*>(event);
      char name[96];
      LuaString(e.name, name, sizeof(name));
      std::snprintf(out, size, "entity=%u,player=%d,name='%s'", H(e.entity), e.player, name);
      return;
    }
    case REFII_EVENT_HIT:
    case REFII_EVENT_HIT_LANDED: {
      const auto& e = *static_cast<const HitEvent*>(event);
      std::snprintf(out, size,
                    "victim=%u,victim_player=%d,attacker=%u,attacker_player=%d,damage=%.9g,spell_type=%d,spell_level=%d,spell_serial=%u,knockdown=%s",
                    H(e.victim), e.victim_player, H(e.attacker), e.attacker_player, Fin(e.damage), e.spell_type,
                    e.spell_level, e.spell_serial, e.knockdown ? "true" : "false");
      return;
    }
    case REFII_EVENT_HIT_RESPONSE: {
      const auto& e = *static_cast<const HitResponseEvent*>(event);
      std::snprintf(out, size, "victim=%u,victim_player=%d,damage=%.9g,health=%.9g,max_health=%.9g", H(e.victim), e.victim_player,
                    Fin(e.damage), Fin(e.health), Fin(e.max_health));
      return;
    }
    case REFII_EVENT_SPELL_CHARGE: {
      const auto& e = *static_cast<const SpellChargeEvent*>(event);
      std::snprintf(out, size, "entity=%u,player=%d", H(e.entity), e.player);
      return;
    }
    case REFII_EVENT_SPELL_CHARGE_LEVEL: {
      const auto& e = *static_cast<const SpellChargeLevelEvent*>(event);
      std::snprintf(out, size, "caster=%u,player=%d,level=%d", H(e.caster), e.player, e.level);
      return;
    }
    case REFII_EVENT_SPELL_LAUNCH: {
      const auto& e = *static_cast<const SpellLaunchEvent*>(event);
      std::snprintf(out, size, "caster=%u,player=%d,targeted=%s,slot=%d,type=%d,cascading=%s", H(e.caster), e.player,
                    e.targeted ? "true" : "false", e.slot, e.type, e.cascading ? "true" : "false");
      return;
    }
    case REFII_EVENT_SPELL_LAUNCHED: {
      const auto& e = *static_cast<const SpellLaunchedEvent*>(event);
      std::snprintf(out, size, "caster=%u,player=%d,slot=%d", H(e.caster), e.player, e.slot);
      return;
    }
    case REFII_EVENT_SPELL_CREATED: {
      const auto& e = *static_cast<const SpellCreatedEvent*>(event);
      std::snprintf(out, size, "spell=%u,type=%d,targeted=%s,target=%u,level=%d,caster=%u,caster_player=%d,serial=%u",
                    H(e.spell), e.type, e.targeted ? "true" : "false", H(e.target), e.level, H(e.caster), e.caster_player, e.serial);
      return;
    }
    case REFII_EVENT_EXPERIENCE: {
      const auto& e = *static_cast<const ExperienceEvent*>(event);
      std::snprintf(out, size, "entity=%u,player=%d,pool=%d,amount=%.9g,spend=%s,nested=%s", H(e.entity), e.player,
                    e.pool, Fin(e.amount), e.spend ? "true" : "false", e.nested ? "true" : "false");
      return;
    }
    case REFII_EVENT_ENTITY_DIED: {
      const auto& e = *static_cast<const EntityDiedEvent*>(event);
      std::snprintf(out, size, "entity=%u,player=%d,killer=%u,killer_player=%d", H(e.entity), e.player, H(e.killer), e.killer_player);
      return;
    }
    case REFII_EVENT_HERO_DOWNED: {
      const auto& e = *static_cast<const HeroDownedEvent*>(event);
      std::snprintf(out, size, "hero=%u,player=%d,killer=%u,killer_player=%d", H(e.hero), e.player, H(e.killer), e.killer_player);
      return;
    }
    case REFII_EVENT_DAMAGE: {
      const auto& e = *static_cast<const DamageEvent*>(event);
      std::snprintf(out, size,
                    "victim=%u,victim_player=%d,attacker=%u,attacker_player=%d,damage=%.9g,health=%.9g,max_health=%.9g,lethal=%s,spell_type=%d,spell_level=%d,spell_serial=%u",
                    H(e.victim), e.victim_player, H(e.attacker), e.attacker_player, Fin(e.damage),
                    Fin(e.health), Fin(e.max_health), e.lethal ? "true" : "false", e.spell_type,
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
      std::snprintf(out, size, "entity=%u,player=%d,definition='0x%08x',notify=%s", H(e.entity), e.player, e.definition,
                    e.notify ? "true" : "false");
      return;
    }
    case REFII_EVENT_ITEM_USED: {
      const auto& e = *static_cast<const ItemUsedEvent*>(event);
      std::snprintf(out, size, "entity=%u,player=%d,item=%u,definition='0x%08x',count=%d", H(e.entity), e.player, H(e.item), e.definition,
                    e.count);
      return;
    }
    case REFII_EVENT_HELD_ITEM: {
      const auto& e = *static_cast<const HeldItemEvent*>(event);
      char slot[64];
      LuaString(e.slot, slot, sizeof(slot));
      std::snprintf(out, size, "entity=%u,player=%d,slot='%s',item=%u,attached=%s", H(e.entity), e.player, slot, H(e.item),
                    e.attached ? "true" : "false");
      return;
    }
    case REFII_EVENT_WEAPON_EQUIPPED: {
      const auto& e = *static_cast<const WeaponEquippedEvent*>(event);
      std::snprintf(out, size, "entity=%u,player=%d,item=%u,definition='0x%08x'", H(e.entity), e.player, H(e.item), e.definition);
      return;
    }
    case REFII_EVENT_ABILITY_LEVEL: {
      const auto& e = *static_cast<const AbilityLevelEvent*>(event);
      std::snprintf(out, size, "entity=%u,player=%d,type=%d,level=%d,old_level=%d", H(e.entity), e.player, e.type, e.level,
                    e.old_level);
      return;
    }
    case REFII_EVENT_GOLD: {
      const auto& e = *static_cast<const GoldEvent*>(event);
      std::snprintf(out, size, "entity=%u,player=%d,gold=%d,delta=%d,passive=%s,share=%s", H(e.entity), e.player, e.gold,
                    e.delta, e.passive ? "true" : "false", e.share ? "true" : "false");
      return;
    }
    case REFII_EVENT_RELATION: {
      const auto& e = *static_cast<const RelationEvent*>(event);
      std::snprintf(out, size, "entity=%u,player=%d,other=%u,other_player=%d,relation=%d", H(e.entity), e.player, H(e.other),
                    e.other_player, e.relation);
      return;
    }
    case REFII_EVENT_INPUT_ACTION: {
      const auto& e = *static_cast<const InputActionEvent*>(event);
      char name[96];
      LuaString(e.name, name, sizeof(name));
      std::snprintf(out, size, "name='%s',player=%d,pressed=%s", name, e.player, e.pressed ? "true" : "false");
      return;
    }
    case REFII_EVENT_QUEST_STARTED: {
      const auto& e = *static_cast<const QuestStartedEvent*>(event);
      char quest[128];
      LuaString(e.quest, quest, sizeof(quest));
      std::snprintf(out, size, "quest='%s',hero=%u,player=%d,make_primary=%s", quest, H(e.hero), e.player,
                    e.make_primary ? "true" : "false");
      return;
    }
    case REFII_EVENT_QUEST_ENDED: {
      const auto& e = *static_cast<const QuestEndedEvent*>(event);
      char quest[128];
      LuaString(e.quest, quest, sizeof(quest));
      std::snprintf(out, size, "quest='%s',hero=%u,player=%d,completed=%s,failed=%s", quest, H(e.hero), e.player,
                    e.completed ? "true" : "false", e.failed ? "true" : "false");
      return;
    }
    case REFII_EVENT_QUEST_OBJECTIVE: {
      const auto& e = *static_cast<const QuestObjectiveEvent*>(event);
      char quest[128];
      char tag[128];
      LuaString(e.quest, quest, sizeof(quest));
      LuaString(e.tag, tag, sizeof(tag));
      std::snprintf(out, size, "quest='%s',tag='%s',hero=%u,player=%d", quest, tag, H(e.hero), e.player);
      return;
    }
    case REFII_EVENT_ENTITY_SPAWNED:
    case REFII_EVENT_ENTITY_REMOVED: {
      const auto& e = *static_cast<const EntitySpawnedEvent*>(event);
      std::snprintf(out, size, "entity=%u,player=%d,definition='0x%08x'", H(e.entity), e.player, e.definition);
      return;
    }
    case REFII_EVENT_EXPRESSION_STARTED: {
      const auto& e = *static_cast<const ExpressionStartedEvent*>(event);
      std::snprintf(out, size, "entity=%u,player=%d,expression='0x%08x'", H(e.entity), e.player, e.expression);
      return;
    }
    case REFII_EVENT_RENOWN: {
      const auto& e = *static_cast<const RenownEvent*>(event);
      std::snprintf(out, size, "entity=%u,player=%d,delta=%d,share=%s", H(e.entity), e.player, e.delta,
                    e.share ? "true" : "false");
      return;
    }
    case REFII_EVENT_MORALITY:
    case REFII_EVENT_PURITY: {
      const auto& e = *static_cast<const MoralityEvent*>(event);
      std::snprintf(out, size, "entity=%u,player=%d,delta=%.9g", H(e.entity), e.player, Fin(e.delta));
      return;
    }
    case REFII_EVENT_MARRIAGE: {
      const auto& e = *static_cast<const MarriageEvent*>(event);
      std::snprintf(out, size, "hero=%u,player=%d,spouse=%u", H(e.hero), e.player, H(e.spouse));
      return;
    }
    case REFII_EVENT_DOG_TRICK: {
      const auto& e = *static_cast<const DogTrickEvent*>(event);
      std::snprintf(out, size, "dog=%u,trick='0x%08x'", H(e.dog), e.trick);
      return;
    }
    case REFII_EVENT_RANGED_SHOT: {
      const auto& e = *static_cast<const RangedShotEvent*>(event);
      std::snprintf(out, size, "shooter=%u,player=%d,target=%u,aimed=%s,weapon=%u", H(e.shooter), e.player, H(e.target),
                    e.aimed ? "true" : "false", H(e.weapon));
      return;
    }
    case REFII_EVENT_STATUS_EFFECT: {
      const auto& e = *static_cast<const StatusEffectEvent*>(event);
      std::snprintf(out, size, "entity=%u,player=%d,effect=%d,started=%s", H(e.entity), e.player, e.effect,
                    e.started ? "true" : "false");
      return;
    }
    case REFII_EVENT_TARGET_CHANGED: {
      const auto& e = *static_cast<const TargetChangedEvent*>(event);
      std::snprintf(out, size, "hero=%u,player=%d,target=%u", H(e.hero), e.player, H(e.target));
      return;
    }
    case REFII_EVENT_OPINION_DEED: {
      const auto& e = *static_cast<const OpinionDeedEvent*>(event);
      std::snprintf(out, size, "villager=%u,doer=%u,doer_player=%d,deed='0x%08x',kind=%d", H(e.villager), H(e.doer),
                    e.doer_player, e.deed, e.kind);
      return;
    }
    case REFII_EVENT_OPINION_MODIFIED: {
      const auto& e = *static_cast<const OpinionModifiedEvent*>(event);
      std::snprintf(out, size, "villager=%u,axis=%d,delta=%.9g", H(e.villager), e.axis, Fin(e.delta));
      return;
    }
    case REFII_EVENT_TIME_OF_DAY: {
      const auto& e = *static_cast<const TimeOfDayEvent*>(event);
      std::snprintf(out, size, "hour=%d,day=%d,time=%.9g,new_day=%s", e.hour, e.day, Fin(e.time),
                    e.new_day ? "true" : "false");
      return;
    }
    case REFII_EVENT_WEATHER_THEME: {
      const auto& e = *static_cast<const WeatherThemeEvent*>(event);
      char theme[128];
      LuaString(e.theme, theme, sizeof(theme));
      std::snprintf(out, size, "theme='%s',weight=%.9g,seconds=%.9g", theme, Fin(e.weight),
                    Fin(e.seconds));
      return;
    }
    case REFII_EVENT_SLEEP_MENU:
    case REFII_EVENT_MENU_OPENED:
    case REFII_EVENT_MENU_CLOSED:
      std::snprintf(out, size, "seen=true");
      return;
    case REFII_EVENT_MENU_SCREEN: {
      const auto& e = *static_cast<const MenuScreenEvent*>(event);
      std::snprintf(out, size, "screen=%d", e.screen);
      return;
    }
    case REFII_EVENT_TRAVEL: {
      const auto& e = *static_cast<const TravelEvent*>(event);
      char world[96], level[96], entrance[96];
      LuaString(e.world, world, sizeof(world));
      LuaString(e.level, level, sizeof(level));
      LuaString(e.entrance, entrance, sizeof(entrance));
      std::snprintf(out, size, "world='%s',level='%s',entrance='%s',type=%d,full_load=%s", world, level, entrance, e.type,
                    e.full_load ? "true" : "false");
      return;
    }
    case REFII_EVENT_FAST_TRAVEL: {
      const auto& e = *static_cast<const FastTravelEvent*>(event);
      char level[96], stop[96];
      LuaString(e.level, level, sizeof(level));
      LuaString(e.stop, stop, sizeof(stop));
      std::snprintf(out, size, "level='%s',stop='%s',paid=%s", level, stop, e.paid ? "true" : "false");
      return;
    }
    case REFII_EVENT_SLEEP_CHOSEN: {
      const auto& e = *static_cast<const SleepChosenEvent*>(event);
      std::snprintf(out, size, "bed=%u,choice=%d,cancelled=%s", H(e.bed), e.choice, e.cancelled ? "true" : "false");
      return;
    }
    case REFII_EVENT_SLOW_TIME: {
      const auto& e = *static_cast<const SlowTimeEvent*>(event);
      std::snprintf(out, size, "caster=%u,player=%d,scale=%.9g,active=%s", H(e.caster), e.player, Fin(e.scale),
                    e.active ? "true" : "false");
      return;
    }
    case REFII_EVENT_SUMMONED: {
      const auto& e = *static_cast<const SummonedEvent*>(event);
      std::snprintf(out, size, "summoner=%u,player=%d,summon=%u,kind=%d", H(e.summoner), e.player, H(e.summon), e.kind);
      return;
    }
    case REFII_EVENT_CRIME: {
      const auto& e = *static_cast<const CrimeEvent*>(event);
      std::snprintf(out, size, "criminal=%u,player=%d,victim=%u,victim_player=%d,type=%d", H(e.criminal), e.player,
                    H(e.victim), e.victim_player, e.type);
      return;
    }
    case REFII_EVENT_ACHIEVEMENT: {
      const auto& e = *static_cast<const AchievementEvent*>(event);
      std::snprintf(out, size, "id=%d,source=%u,player=%d", e.id, H(e.source), e.player);
      return;
    }
    case REFII_EVENT_COLLECTABLE_TAKEN: {
      const auto& e = *static_cast<const CollectableTakenEvent*>(event);
      std::snprintf(out, size, "player=%d,entity=%u,record='0x%08x',stays=%s", e.player, H(e.entity), e.record,
                    e.stays ? "true" : "false");
      return;
    }
    case REFII_EVENT_PARTNER_CONSENT: {
      const auto& e = *static_cast<const PartnerConsentEvent*>(event);
      std::snprintf(out, size, "ticket=%u,kind=%d,asker=%d,detail=%d,answer=%d", e.ticket, e.kind, e.asker, e.detail,
                    e.answer);
      return;
    }
    case REFII_EVENT_WEATHER_REGION: {
      const auto& e = *static_cast<const WeatherRegionEvent*>(event);
      std::snprintf(out, size, "day_set='0x%08x',previous='0x%08x',inside=%s", e.day_set, e.previous,
                    e.inside ? "true" : "false");
      return;
    }
    case REFII_EVENT_DIALOGUE: {
      const auto& e = *static_cast<const DialogueEvent*>(event);
      std::snprintf(out, size, "speaker=%u,speaker_player=%d,text='0x%08x',listener=%u,listener_player=%d,subtitle=%s",
                    H(e.speaker), e.speaker_player, e.text, H(e.listener), e.listener_player, e.subtitle ? "true" : "false");
      return;
    }
    default:
      out[0] = 0;
      return;
  }
}

/// The event a Lua chunk is running for, so Refii._write (mod_lua_natives.cpp) can put a
/// handler's changed field into the struct the game reads.
int32_t g_lua_current_kind = -1;
void* g_lua_current_event = nullptr;

/// A Lua `cancel` lands in the event's flag; a writable number a handler changed comes back
/// through Refii._write while the chunk runs (the preamble diffs the fields after the handlers).
void DispatchLua(int32_t kind, void* event) {
  // An event raised from inside a running chunk (a handler calling Refii.give_gold reaches the
  // hooked Modify) is not run in Lua: chunks never nest, and the running event's write-back target
  // must survive the nested one.
  if (!LuaWants(kind) || g_lua_running) {
    return;
  }
  auto& e = *static_cast<Event*>(event);
  const int32_t previous_kind = g_lua_current_kind;
  void* const previous_event = g_lua_current_event;
  g_lua_current_kind = kind;
  g_lua_current_event = event;
  struct Restore {
    int32_t kind;
    void* event;
    ~Restore() {
      g_lua_current_kind = kind;
      g_lua_current_event = event;
    }
  } restore{previous_kind, previous_event};
  if (kind == REFII_EVENT_SPELL_CHARGE_LEVEL) {
    // Every frame of a held charge: Lua runs when the level changes, and its verdict stands for
    // the frames between (each run is a compile).
    auto& c = *static_cast<SpellChargeLevelEvent*>(event);
    static uint32_t last_caster = 0;
    static int32_t last_level = -100;
    static int32_t last_verdict = 0;
    if (c.caster == last_caster && c.level == last_level) {
      c.level = last_verdict;
      return;
    }
    last_caster = c.caster;
    last_level = c.level;
    char fields[640];
    LuaFields(kind, event, fields, sizeof(fields));
    if (FireLua(*e.ctx, e.base, kind, fields)) {
      e.cancel = true;
    }
    last_verdict = c.level;
    return;
  }
  if (kind == REFII_EVENT_TICK) {
    // Ten times a second at most: a chunk run is a compile.
    g_lua_tick_accumulated += static_cast<TickEvent*>(event)->seconds;
    if (g_lua_tick_accumulated < kLuaTickPeriod) {
      return;
    }
    char fields[64];
    std::snprintf(fields, sizeof(fields), "seconds=%.9g", Fin(g_lua_tick_accumulated));
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
  e.knockdown = hit != 0 && Load8(base, hit + kHitKnockdown) != 0;
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
  if (LuaWants(kind) || refii::mods::HostWantsEvent(kind)) {
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

bool WriteEventField(int32_t kind, void* event, const char* field, double value);

namespace {

/// A Godot script's VERDICT: the host's handler runs on this thread, inside the seam, with the
/// event's fields, and answers "cancel=1,damage=2.5,…" — what the scripts cancelled or changed.
/// Opt-in per kind (the host's verdict mask); a verdict handler never runs nested inside another
/// one (a script cannot raise a game event synchronously, but a hook it reaches could).
bool g_verdict_running = false;

void RunGodotVerdict(int32_t kind, void* event) {
  const uint64_t verdict_word = kind < 64 ? refii_bridge_mod_verdict_mask() : refii_bridge_mod_verdict_mask_high();
  if (g_verdict_running || kind < 0 || kind >= 128 || ((verdict_word >> (kind % 64)) & 1u) == 0u) {
    return;
  }
  const refii_mod_verdict_fn handler = refii_bridge_mod_verdict_handler();
  if (handler == nullptr) {
    return;
  }
  char fields[640];
  LuaFields(kind, event, fields, sizeof(fields));
  char answer[512];
  g_verdict_running = true;
  const int32_t length = handler(kind, fields, std::strlen(fields), answer, sizeof(answer) - 1);
  g_verdict_running = false;
  if (length <= 0) {
    return;
  }
  answer[length < static_cast<int32_t>(sizeof(answer)) ? length : static_cast<int32_t>(sizeof(answer)) - 1] = 0;
  // name=value pairs, comma separated, values numbers.
  char* cursor = answer;
  while (*cursor != 0) {
    char* comma = std::strchr(cursor, ',');
    if (comma != nullptr) {
      *comma = 0;
    }
    char* equals = std::strchr(cursor, '=');
    if (equals != nullptr) {
      *equals = 0;
      const double value = std::strtod(equals + 1, nullptr);
      if (std::strcmp(cursor, "cancel") == 0) {
        if (value != 0.0) {
          static_cast<Event*>(event)->cancel = true;
        }
      } else {
        WriteEventField(kind, event, cursor, value);
      }
    }
    if (comma == nullptr) {
      break;
    }
    cursor = comma + 1;
  }
}

}  // namespace

/// WHETHER THIS RUN IS THE GAME'S COPY of something the other co-op machine decides. In an online
/// session the game runs these seams on both machines for one occurrence (read in the binary, TU1):
///   - CAction::OnAnimEvent: every replicated action is cloned and ticks on the other machine too;
///   - the spell manager's charge level, launch and launched, and the spell factory: a cast
///     action's Start (sent as 0x5B/0x5C, rebuilt by the receiver) reaches them on both
///     (sub_8260A150 0x82739af4; LaunchSlot from the manager's update, 0x8253ea60);
///   - CECHitResponsePlayAction::OnHit: the host's full run, and the joiner's run for hits on or by
///     player two through the hit router's listener slot 1 (sub_8295CEB8, masked);
///   - CECExperience::Modify: the delta, applied again by the receiver of msg 0x34 inside the
///     receive (comp91+0x6B set, tested at 0x8256dbc0).
/// The copy is the run on the machine NOT authoritative for the subject — the game's own test,
/// sub_82214AF8(comp91): no session, the host for anything not player two's, the joiner for player
/// two's, local co-op for all — and, for experience, the run inside the receive. Every other seam
/// runs on one machine only (an ability request where the hero's CPlayer is; a hit and a block on
/// the host), so it is never a copy.
bool ReplicaRun(int32_t kind, void* event) {
  using namespace refii::mods;
  const Event& base_event = *static_cast<const Event*>(event);
  uint8_t* base = base_event.base;
  if (base == nullptr || base_event.ctx == nullptr) {
    return false;
  }
  constexpr uint32_t kNetworkComponent = 91;
  uint32_t subject = 0;
  switch (kind) {
    case REFII_EVENT_ANIM_EVENT:
      subject = static_cast<const AnimEventEvent*>(event)->entity;
      break;
    case REFII_EVENT_HIT_RESPONSE:
      subject = static_cast<const HitResponseEvent*>(event)->victim;
      break;
    case REFII_EVENT_SPELL_CHARGE_LEVEL:
      subject = static_cast<const SpellChargeLevelEvent*>(event)->caster;
      break;
    case REFII_EVENT_SPELL_LAUNCH:
      subject = static_cast<const SpellLaunchEvent*>(event)->caster;
      break;
    case REFII_EVENT_SPELL_LAUNCHED:
      subject = static_cast<const SpellLaunchedEvent*>(event)->caster;
      break;
    case REFII_EVENT_SPELL_CREATED:
      subject = static_cast<const SpellCreatedEvent*>(event)->caster;
      break;
    case REFII_EVENT_EXPERIENCE: {
      const uint32_t entity = static_cast<const ExperienceEvent*>(event)->entity;
      const uint32_t network = entity != 0 ? refii_mod_component(base, entity, kNetworkComponent) : 0u;
      return network != 0 && Load8(base, network + 0x6Bu) != 0;
    }
    default:
      return false;
  }
  const uint32_t network = subject != 0 ? refii_mod_component(base, subject, kNetworkComponent) : 0u;
  if (network == 0) {
    return false;
  }
  PPCContext work = Frame(*base_event.ctx);
  work.r3.u32 = network;
  return CallGuest(work, base, refii::guest::Address(0x82214AF8u)) && work.r3.u32 == 0u;
}

extern "C" void refii_mod_dispatch(int32_t kind, void* event) {
  if (event == nullptr) {
    return;
  }
  const bool replica = ReplicaRun(kind, event);
  if (replica) {
    refii_bridge_player_replica(1);
  }
  for (const Subscription& s : Subscriptions()) {
    if (s.kind == kind) {
      s.fn(event, s.user);
    }
  }
  DispatchLua(kind, event);
  RunGodotVerdict(kind, event);
  if (replica) {
    refii_bridge_player_replica(0);
  }
  // Godot's scripts see the verdict, after every handler, as the same table text Lua gets.
  if (refii::mods::HostWantsEvent(kind)) {
    char fields[640];
    LuaFields(kind, event, fields, sizeof(fields));
    refii::mods::PostEvent(kind, fields);
  }
}

/// Refii._write(kind, field, value) from a Lua handler: the writable numbers of the event the
/// chunk is running for. False for any other kind, field or moment.
/// A Lua number as an int32: NaN is 0, anything outside the range is clamped (a raw cast is
/// undefined there and on x86 gives INT_MIN — a "gold delta" of 3e9 would take 2^31 gold).
int32_t ClampInt(double value) {
  if (std::isnan(value)) {
    return 0;
  }
  if (value >= 2147483647.0) {
    return 2147483647;
  }
  if (value <= -2147483648.0) {
    return -2147483647 - 1;
  }
  return static_cast<int32_t>(value);
}

/// A handler's change to a writable field of `event` (kind `kind`): the numbers (and the hit's
/// knockdown flag, 0 or 1) the game reads back. False for any other field.
bool WriteEventField(int32_t kind, void* event, const char* field, double value) {
  if (event == nullptr || field == nullptr || std::isnan(value)) {
    return false;
  }
  const auto is = [field](const char* name) { return std::strcmp(field, name) == 0; };
  switch (kind) {
    case REFII_EVENT_PARTNER_CONSENT:
      if (is("answer")) {
        static_cast<PartnerConsentEvent*>(event)->answer = value < 0.0 ? -1 : (value >= 0.5 ? 1 : 0);
        return true;
      }
      return false;
    case REFII_EVENT_HIT:
      if (is("damage")) {
        static_cast<HitEvent*>(event)->damage = static_cast<float>(value);
        return true;
      }
      if (is("knockdown")) {
        static_cast<HitEvent*>(event)->knockdown = value != 0.0;
        return true;
      }
      return false;
    case REFII_EVENT_SPELL_CHARGE_LEVEL:
      if (is("level")) {
        static_cast<SpellChargeLevelEvent*>(event)->level = ClampInt(value);
        return true;
      }
      return false;
    case REFII_EVENT_SPELL_LAUNCH:
      if (is("type")) {
        static_cast<SpellLaunchEvent*>(event)->type = ClampInt(value);
        return true;
      }
      return false;
    case REFII_EVENT_EXPERIENCE:
      if (is("amount")) {
        static_cast<ExperienceEvent*>(event)->amount = static_cast<float>(value);
        return true;
      }
      return false;
    case REFII_EVENT_ABILITY_LEVEL:
      if (is("level")) {
        static_cast<AbilityLevelEvent*>(event)->level = ClampInt(value);
        return true;
      }
      return false;
    case REFII_EVENT_GOLD:
      if (is("delta")) {
        static_cast<GoldEvent*>(event)->delta = ClampInt(value);
        return true;
      }
      return false;
    case REFII_EVENT_RENOWN:
      if (is("delta")) {
        static_cast<RenownEvent*>(event)->delta = ClampInt(value);
        return true;
      }
      return false;
    case REFII_EVENT_MORALITY:
    case REFII_EVENT_PURITY:
      if (is("delta")) {
        static_cast<MoralityEvent*>(event)->delta = static_cast<float>(value);
        return true;
      }
      return false;
    case REFII_EVENT_OPINION_MODIFIED:
      if (is("delta")) {
        static_cast<OpinionModifiedEvent*>(event)->delta = static_cast<float>(value);
        return true;
      }
      return false;
    case REFII_EVENT_WEATHER_THEME:
      if (is("weight")) {
        static_cast<WeatherThemeEvent*>(event)->weight = static_cast<float>(value);
        return true;
      }
      if (is("seconds")) {
        static_cast<WeatherThemeEvent*>(event)->seconds = static_cast<float>(value);
        return true;
      }
      return false;
    case REFII_EVENT_SLOW_TIME:
      if (is("scale")) {
        static_cast<SlowTimeEvent*>(event)->scale = static_cast<float>(value);
        return true;
      }
      return false;
    case REFII_EVENT_SLEEP_CHOSEN:
      if (is("choice")) {
        static_cast<SleepChosenEvent*>(event)->choice = ClampInt(value);
        return true;
      }
      return false;
    case REFII_EVENT_RELATION:
      if (is("relation")) {
        static_cast<RelationEvent*>(event)->relation = ClampInt(value);
        return true;
      }
      return false;
    default:
      return false;
  }
}

/// Refii._write from a Lua handler: only for the event the chunk is running for.
bool WriteLuaField(int32_t kind, const char* field, double value) {
  return kind == g_lua_current_kind && WriteEventField(kind, g_lua_current_event, field, value);
}

namespace refii::vfs {
bool WriteModEventField(int32_t kind, const char* field, double value) { return WriteLuaField(kind, field, value); }
}  // namespace refii::vfs

namespace refii::vfs {

/// The time-of-day baseline (below).
void ResetTimeOfDayBaseline();
/// The ragdoll and unconscious bookkeeping drops a destroyed entity (below).
void ForgetDownStates(uint32_t entity);
/// mod_commands.cpp: the host's queued helper calls and queries (Godot scripts' RefiiHost).
void RunModCommands(PPCContext& ctx, uint8_t* base);
/// The entities the manager took since the last tick (EntitySpawned), below.
void DispatchSpawned(PPCContext& ctx, uint8_t* base);

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
    std::snprintf(chunk, sizeof(chunk), "if Refii then Refii._hero[%d] = %u end", p, refii_mod_handle(hero));
    refii_mod_run_game_lua(&ctx, base, chunk);
  }

  // A mod's input actions: the edges Godot posted since the last tick.
  if (AnySubscriber(REFII_EVENT_INPUT_ACTION)) {
    char name[160];
    int32_t player = 0;
    int32_t pressed = 0;
    while (refii_bridge_mod_input_poll(name, sizeof(name), &player, &pressed) != 0) {
      InputActionEvent e{};
      PPCContext e_ctx = ctx;
      e.ctx = &e_ctx;
      e.base = base;
      e.name = name;
      e.player = player;
      e.pressed = pressed != 0;
      Dispatch(e);
    }
  } else {
    char name[160];
    while (refii_bridge_mod_input_poll(name, sizeof(name), nullptr, nullptr) != 0) {
    }
  }

  RunModCommands(ctx, base);
  DispatchSpawned(ctx, base);

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
  // A new session or a loaded save has a clock of its own: the next update re-baselines instead of
  // reporting a jump from the previous session's hour.
  ResetTimeOfDayBaseline();
  // A new VM has no Refii._hero: the heroes the bus already announced are stated to it now.
  for (int32_t p = 1; p <= 2; ++p) {
    char chunk[96];
    std::snprintf(chunk, sizeof(chunk), "if Refii then Refii._hero[%d] = %u end", p,
                  refii_mod_handle(refii_mod_player_hero(base, p)));
    refii_mod_run_game_lua(&ctx, base, chunk);
  }
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
    const bool knockdown = e.knockdown;
    Dispatch(e);
    if (e.cancel) {
      return;
    }
    if (hit != 0 && e.damage != damage) {
      refii::mods::StoreF32(base, hit + kHitDamage, e.damage);
    }
    // The hit-response listener reads +0x18 to choose a knockdown (0x8295CF08); force push's
    // knockdown is this byte (0x828B496C).
    if (hit != 0 && e.knockdown != knockdown) {
      refii::mods::StoreU8(base, hit + kHitKnockdown, e.knockdown ? 1u : 0u);
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
      // The game's own single-slot mode (byte_8331918F: count becomes 1 and first the slot).
      if (Load8(base, refii::guest::Address(kSingleSlotMode)) != 0) {
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
  const int32_t forced = static_cast<int32_t>(Load32(base, refii::guest::Address(kLaunchSlotOverride)));
  const uint32_t used = forced >= 0 && forced < static_cast<int32_t>(kSlots) ? static_cast<uint32_t>(forced) : slot;
  const uint32_t cell = manager + kManagerSlotTable + flag * kSlots + used;
  const int32_t stated = static_cast<int8_t>(Load8(base, cell));
  SpellLaunchEvent e{};
  PPCContext e_ctx = ctx;
  e.ctx = &e_ctx;
  e.base = base;
  e.caster = Load32(base, manager + kManagerCaster);
  e.player = PlayerOf(base, e.caster);
  e.manager = manager;
  e.targeted = flag != 0;
  e.slot = static_cast<int32_t>(used);
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
/// Experience changes in progress: a Spend pays through Modify, and Modify recurses for the
/// co-op share, so an event raised while one is in progress is nested.
int32_t g_experience_depth = 0;

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
  e.nested = g_experience_depth > 0;
  const float amount = e.amount;
  Dispatch(e);
  if (e.cancel && !e.nested) {
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
  ++g_experience_depth;
  __imp__rex_CECExperience_Modify(ctx, base);
  --g_experience_depth;
}

REX_EXTERN(rex_CECExperience_Spend) {
  REX_FUNC_PROLOGUE();
  if (AnySubscriber(REFII_EVENT_EXPERIENCE)) {
    ExperienceEvent e{};
    if (!ExperienceEventOf(ctx, base, true, e)) {
      return;
    }
  }
  ++g_experience_depth;
  __imp__rex_CECExperience_Spend(ctx, base);
  --g_experience_depth;
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
/// The weapon component an item must carry for the weapon set to record it.
constexpr uint32_t kWeaponComponent = 179;

/// The game's own CantDie answer: health back above the zero test (1e-4), so the next hit asks
/// again. 1.0, as the hero path writes at 0x824ce8c4.
void KeepAlive(uint8_t* base, uint32_t entity) {
  const uint32_t health = refii_mod_component(base, entity, kHealthComponent);
  if (health != 0) {
    const float max = LoadF(base, health + kHealthMax);
    refii::mods::StoreF32(base, health + kHealthCurrent, max > 0.0f && max < 1.0f ? max : 1.0f);
  }
}

/// A death the dispatch reached because health is at zero (the only caller whose refusal the
/// game's state survives); scripted, instant and fall kills are not refusable.
bool DyingOfDamage(uint8_t* base, uint32_t entity) {
  float current = 0.0f, max = 0.0f;
  return refii_mod_health(base, entity, &current, &max) != 0 && current <= 1e-4f;
}

/// A text through an LHString handle (a pointer to the body whose first word is the char*).
const char* LhString(uint8_t* base, uint32_t handle_at) {
  const uint32_t body = handle_at != 0 ? Load32(base, handle_at) : 0u;
  const uint32_t text = body >= 0x10000u && body < 0xC0000000u ? Load32(base, body) : 0u;
  return text >= 0x10000u && text < 0xC0000000u ? reinterpret_cast<const char*>(base + text) : "";
}

/// The current level's name from the world manager (*(*(W+8)+4), as sub_82446928 reaches it for
/// UnloadLevel): +0x24 the holder, +4 the level, +0xAC its name — the walk the game's own
/// IsLevelLoaded makes (0x8228766c..0x822876ac).
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
    if (e.cancel && DyingOfDamage(base, e.entity)) {
      KeepAlive(base, e.entity);
      return;
    }
  }
  __imp__rex_CECCreature_Die(ctx, base);
}

// The death finished without the dying step: r3 = the creature component (owner at +4, alive at
// +0x18): clears the alive byte and strips the living components (58, 109, 21, 49). Reached by
// knockdown and chicken ends, the vortex, scripts (9 callers). A death that went through Die
// has already cleared the alive byte, so it is reported once. Not refusable. TU1 0x823A4290.
REX_EXTERN(rex_CECCreature_FinishDeath) {
  REX_FUNC_PROLOGUE();
  const uint32_t creature = ctx.r3.u32;
  const bool was_alive = creature != 0 && Load8(base, creature + kCreatureAlive) != 0;
  __imp__rex_CECCreature_FinishDeath(ctx, base);
  if (was_alive && Load8(base, creature + kCreatureAlive) == 0 && AnySubscriber(REFII_EVENT_ENTITY_DIED)) {
    EntityDiedEvent e{};
    PPCContext e_ctx = ctx;
    e.ctx = &e_ctx;
    e.base = base;
    e.entity = Load32(base, creature + 4u);
    e.player = PlayerOf(base, e.entity);
    Dispatch(e);
  }
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
    const uint32_t holder = world != 0 ? Load32(base, world + 8u) : 0u;
    e.level = LevelName(base, holder >= 0x10000u && holder < 0xC0000000u ? Load32(base, holder + 4u) : 0u);
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
  const uint32_t inventory = ctx.r4.u32;
  const uint32_t definition = ctx.r5.u32;
  const bool notify = ctx.r6.u8 != 0;
  __imp__rex_CECInventory_AddOneOfRecord(ctx, base);
  if (inventory != 0 && definition != 0 && AnySubscriber(REFII_EVENT_ITEM_ADDED)) {
    ItemAddedEvent e{};
    PPCContext e_ctx = ctx;
    e.ctx = &e_ctx;
    e.base = base;
    e.inventory = inventory;
    e.entity = Load32(base, inventory + 4u);
    e.player = PlayerOf(base, e.entity);
    e.definition = definition;
    e.notify = notify;
    Dispatch(e);
  }
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
  if (ctx.r3.u32 != 0 && ctx.r4.u32 != 0 && refii_mod_component(base, ctx.r4.u32, kWeaponComponent) != 0 &&
      AnySubscriber(REFII_EVENT_WEAPON_EQUIPPED)) {
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
namespace {
/// Purse and renown changes in progress: in co-op the game forwards player two's change to
/// player one and sends the partner's share back, each a recursive call of the same function.
int32_t g_gold_depth = 0;
int32_t g_renown_depth = 0;
}  // namespace

REX_EXTERN(rex_CECMoney_Modify) {
  REX_FUNC_PROLOGUE();
  const uint32_t purse = ctx.r3.u32;
  struct Depth {
    Depth() { ++g_gold_depth; }
    ~Depth() { --g_gold_depth; }
  };
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
    e.share = g_gold_depth > 0;
    const int32_t delta = e.delta;
    Dispatch(e);
    if (e.cancel && !e.share) {
      return;
    }
    if (e.delta != delta) {
      ctx.r4.s32 = e.delta;
    }
  }
  Depth depth;
  __imp__rex_CECMoney_Modify(ctx, base);
}

// The faction rule every "is this my enemy" asks: r3 = the faction component (133, owner at +4),
// r4 = the other entity; returns 1 neutral, 2 ally, 4 enemy (explicit lists first, then the
// faction table). TU1 0x82203F98 / disc 0x822040D0. Asked many times a tick, so nothing is
// built unless someone listens. refii_mod_relation calls the body (__imp__) and never comes here.
REX_EXTERN(rex_CECFaction_RelationTo) {
  REX_FUNC_PROLOGUE();
  const uint32_t faction = ctx.r3.u32;
  const uint32_t other = ctx.r4.u32;
  __imp__rex_CECFaction_RelationTo(ctx, base);
  if (!AnySubscriber(REFII_EVENT_RELATION)) {
    return;
  }
  RelationEvent e{};
  PPCContext e_ctx = ctx;
  e.ctx = &e_ctx;
  e.base = base;
  e.entity = faction != 0 ? Load32(base, faction + 4u) : 0u;
  e.player = PlayerOf(base, e.entity);
  e.other = other;
  e.other_player = PlayerOf(base, other);
  e.relation = static_cast<int32_t>(ctx.r3.u32);
  Dispatch(e);
  if (e.relation == 1 || e.relation == 2 || e.relation == 4) {
    ctx.r3.u64 = static_cast<uint32_t>(e.relation);
  }
}

namespace {

/// A guest C string into `out`, bounded; empty for a null or implausible pointer.
void GuestText(uint8_t* base, uint32_t at, char* out, size_t size) {
  out[0] = 0;
  if (at < 0x10000u || at >= 0xC0000000u || size == 0) {
    return;
  }
  size_t n = 0;
  for (; n + 1 < size && n < 256; ++n) {
    const char c = static_cast<char>(base[at + n]);
    if (c == 0) {
      break;
    }
    out[n] = c;
  }
  out[n] = 0;
}

/// An LHString at `handle_at` (a pointer to a body whose first word is the text) into `out`.
void LhText(uint8_t* base, uint32_t handle_at, char* out, size_t size) {
  const uint32_t body = Load32(base, handle_at);
  GuestText(base, body >= 0x10000u && body < 0xC0000000u ? Load32(base, body) : 0u, out, size);
}

constexpr uint32_t kQuestRecordName = 0x0;
constexpr uint32_t kQuestRecordCompleted = 21;
constexpr uint32_t kQuestRecordFailed = 22;

}  // namespace

// A quest becoming active on a hero's tracker: r3 = the tracker component (owner at +4), r4 = the
// quest's name (a C string the lookup hashes with FNV-1), r5 = a flag, r6 = make primary. The
// body sets the record's +13 (active) and +14 (activated) and takes the default objective.
// TU1 0x8251C618 / disc 0x825F8FD8. Not calling it leaves the quest as it was.
REX_EXTERN(rex_CECQuestTracker_SetAsActive) {
  REX_FUNC_PROLOGUE();
  if (AnySubscriber(REFII_EVENT_QUEST_STARTED)) {
    char name[128];
    GuestText(base, ctx.r4.u32, name, sizeof(name));
    QuestStartedEvent e{};
    PPCContext e_ctx = ctx;
    e.ctx = &e_ctx;
    e.base = base;
    e.quest = name;
    e.hero = ctx.r3.u32 != 0 ? Load32(base, ctx.r3.u32 + 4u) : 0u;
    e.player = PlayerOf(base, e.hero);
    e.make_primary = (ctx.r6.u32 & 0xFFu) != 0;
    Dispatch(e);
    if (e.cancel) {
      ctx.r3.u64 = 0;
      return;
    }
  }
  __imp__rex_CECQuestTracker_SetAsActive(ctx, base);
}

// The one function a completed and a failed quest both reach, after their flags are set: r3 = the
// tracker, r4 = the record (+0 the name LHString, +21 completed, +22 failed). TU1 0x8251D0D8 /
// disc 0x825F9A98. Raised after it; the rewards were given before it was called.
REX_EXTERN(rex_CECQuestTracker_OnQuestEnded) {
  REX_FUNC_PROLOGUE();
  const uint32_t tracker = ctx.r3.u32;
  const uint32_t record = ctx.r4.u32;
  __imp__rex_CECQuestTracker_OnQuestEnded(ctx, base);
  if (!AnySubscriber(REFII_EVENT_QUEST_ENDED) || record == 0) {
    return;
  }
  char name[128];
  LhText(base, record + kQuestRecordName, name, sizeof(name));
  QuestEndedEvent e{};
  PPCContext e_ctx = ctx;
  e.ctx = &e_ctx;
  e.base = base;
  e.quest = name;
  e.hero = tracker != 0 ? Load32(base, tracker + 4u) : 0u;
  e.player = PlayerOf(base, e.hero);
  e.completed = Load8(base, record + kQuestRecordCompleted) != 0;
  e.failed = Load8(base, record + kQuestRecordFailed) != 0;
  Dispatch(e);
}

// An objective tag pushed onto a quest's record (+100 vector): r3 = the tracker, r4 = the quest's
// name, r5 = the tag text. Behind SetObjectiveTag, SetObjectiveTagFinal and the default objective
// a quest gets on activation. TU1 0x8251D480 / disc 0x825F9E40. Not calling it adds nothing.
REX_EXTERN(rex_CECQuestTracker_SetObjectiveTagSilently) {
  REX_FUNC_PROLOGUE();
  if (AnySubscriber(REFII_EVENT_QUEST_OBJECTIVE)) {
    char name[128];
    char tag[128];
    GuestText(base, ctx.r4.u32, name, sizeof(name));
    GuestText(base, ctx.r5.u32, tag, sizeof(tag));
    QuestObjectiveEvent e{};
    PPCContext e_ctx = ctx;
    e.ctx = &e_ctx;
    e.base = base;
    e.quest = name;
    e.tag = tag;
    e.hero = ctx.r3.u32 != 0 ? Load32(base, ctx.r3.u32 + 4u) : 0u;
    e.player = PlayerOf(base, e.hero);
    Dispatch(e);
    if (e.cancel) {
      ctx.r3.u64 = 0;
      return;
    }
  }
  __imp__rex_CECQuestTracker_SetObjectiveTagSilently(ctx, base);
}

// Every spoken line: r3 = the talk component (owner at +4; +16 takes the line's hash, +20 its end
// time, +52 the subtitle flag), r4 = the text hash (the TextManager key after the voice and
// variant suffixes), r5 = force subtitle, r6 = the listener entity or 0. The audio, the speech
// idle action, the listener's look and the subtitle all follow inside. Returns whether audio
// plays. TU1 0x825D7F78 / disc 0x82449448. Not calling it keeps the line unspoken.
REX_EXTERN(rex_CECTalk_PlayLine) {
  REX_FUNC_PROLOGUE();
  if (AnySubscriber(REFII_EVENT_DIALOGUE)) {
    DialogueEvent e{};
    PPCContext e_ctx = ctx;
    e.ctx = &e_ctx;
    e.base = base;
    e.speaker = ctx.r3.u32 != 0 ? Load32(base, ctx.r3.u32 + 4u) : 0u;
    e.speaker_player = PlayerOf(base, e.speaker);
    e.text = ctx.r4.u32;
    e.listener = ctx.r6.u32;
    e.listener_player = PlayerOf(base, e.listener);
    e.subtitle = (ctx.r5.u32 & 0xFFu) != 0;
    Dispatch(e);
    if (e.cancel) {
      ctx.r3.u64 = 0;
      return;
    }
  }
  __imp__rex_CECTalk_PlayLine(ctx, base);
}

namespace {

/// Entities the manager took, waiting for their first tick (their components are built after
/// the add). A removal before then takes the entity out again.
std::vector<uint32_t> g_spawned;
constexpr size_t kSpawnedQueueLimit = 8192;
constexpr int kSpawnedPerTick = 64;
constexpr uint32_t kEntityFlags = 0x90;
constexpr uint8_t kInWorld = 0x40;

}  // namespace

void refii::vfs::DispatchSpawned(PPCContext& ctx, uint8_t* base) {
  if (g_spawned.empty()) {
    return;
  }
  if (!AnySubscriber(REFII_EVENT_ENTITY_SPAWNED)) {
    g_spawned.clear();
    return;
  }
  const size_t take = g_spawned.size() < static_cast<size_t>(kSpawnedPerTick) ? g_spawned.size()
                                                                               : static_cast<size_t>(kSpawnedPerTick);
  std::vector<uint32_t> batch(g_spawned.begin(), g_spawned.begin() + static_cast<std::ptrdiff_t>(take));
  g_spawned.erase(g_spawned.begin(), g_spawned.begin() + static_cast<std::ptrdiff_t>(take));
  for (uint32_t entity : batch) {
    if ((Load8(base, entity + kEntityFlags) & kInWorld) == 0) {
      continue;
    }
    EntitySpawnedEvent e{};
    PPCContext e_ctx = ctx;
    e.ctx = &e_ctx;
    e.base = base;
    e.entity = entity;
    e.player = PlayerOf(base, entity);
    e.definition = Load32(base, entity + kEntityDefinition);
    Dispatch(e);
  }
}

// The world's entity manager taking an entity: r3 = the manager, r4 = the entity (+0x14 its 64-bit
// id; ids 1 and 2 also get the manager's player slots at +104 / +112). Every constructor path
// reaches it — the runtime create (CreateEntityFromIDAt's sub_8238B860) and the level/save stream
// (sub_8238C948) — before the components exist. TU1 0x82363BA0.
REX_EXTERN(rex_lhEntityManager_Add) {
  REX_FUNC_PROLOGUE();
  const uint32_t entity = ctx.r4.u32;
  __imp__rex_lhEntityManager_Add(ctx, base);
  if (entity != 0 && AnySubscriber(REFII_EVENT_ENTITY_SPAWNED) && g_spawned.size() < kSpawnedQueueLimit &&
      std::find(g_spawned.begin(), g_spawned.end(), entity) == g_spawned.end()) {
    g_spawned.push_back(entity);
  }
}

// An entity leaving the world: r3 = the entity. The one site that clears +0x90's in-world bit,
// then the manager's deferred removal and the components' teardown. Reached by EntityMetatable
// Destroy and 140 other callers. TU1 0x8238D828.
REX_EXTERN(rex_lhEntity_Destroy) {
  REX_FUNC_PROLOGUE();
  const uint32_t entity = ctx.r3.u32;
  struct Forget {
    uint32_t entity;
    ~Forget() { refii_bridge_mod_handle_forget(entity); }
  } forget{entity};
  refii::vfs::ForgetDownStates(entity);
  if (!g_spawned.empty()) {
    g_spawned.erase(std::remove(g_spawned.begin(), g_spawned.end(), entity), g_spawned.end());
  }
  if (entity != 0 && AnySubscriber(REFII_EVENT_ENTITY_REMOVED)) {
    EntityRemovedEvent e{};
    PPCContext e_ctx = ctx;
    e.ctx = &e_ctx;
    e.base = base;
    e.entity = entity;
    e.player = PlayerOf(base, entity);
    e.definition = Load32(base, entity + kEntityDefinition);
    Dispatch(e);
  }
  __imp__rex_lhEntity_Destroy(ctx, base);
}

// ---------------------------------------------------------------------------------------------
// Social, combat detail, world and menus (API v2, second batch). Every seam decompiled on TU1;
// the registers quoted here are the ones read.
// ---------------------------------------------------------------------------------------------

namespace {

/// A component's owner entity (+4, the base constructor's contract).
uint32_t Owner(uint8_t* base, uint32_t component) {
  return component >= 0x10000u && component < 0xC0000000u ? Load32(base, component + 4u) : 0u;
}

/// The held-item component (83): its holder's weak reference at +12 (sub_828B7F40).
constexpr uint32_t kHeldItemComponent = 83;
constexpr uint32_t kStatusElectrified = 205;
constexpr uint32_t kStatusChaos = 206;
constexpr uint32_t kStatusBurning = 208;
constexpr uint32_t kStatusInVortex = 209;
constexpr uint32_t kStatusDogMaulee = 245;
constexpr int32_t kEffectKnockedDown = 5;
constexpr int32_t kEffectUnconscious = 7;
constexpr int32_t kEffectCollapsed = 8;

int32_t StatusEffectOf(uint32_t id) {
  switch (id) {
    case kStatusElectrified:
      return 1;
    case kStatusChaos:
      return 2;
    case kStatusBurning:
      return 3;
    case kStatusInVortex:
      return 4;
    case kStatusDogMaulee:
      return 6;
    default:
      return 0;
  }
}

/// Component `id` present on `entity` (the presence bits at +0x24, one word per 32 ids).
bool HasComponent(uint8_t* base, uint32_t entity, uint32_t id) {
  return entity != 0 && ((Load32(base, entity + 0x24u + 4u * (id >> 5)) >> (id & 31u)) & 1u) != 0;
}

void RaiseStatus(PPCContext& ctx, uint8_t* base, uint32_t entity, int32_t effect, bool started) {
  StatusEffectEvent e{};
  PPCContext e_ctx = ctx;
  e.ctx = &e_ctx;
  e.base = base;
  e.entity = entity;
  e.player = PlayerOf(base, entity);
  e.effect = effect;
  e.started = started;
  Dispatch(e);
}

int32_t g_last_hour = -1;
int32_t g_last_day = -1;

}  // namespace

// r3 = the performer component, r4 = {expression hash, GDB entry}; returns 1 when the expression
// started (the can-perform check refuses with 0). TU1 0x82413698.
REX_EXTERN(rex_CECExpressionPerformer_StartExpression) {
  REX_FUNC_PROLOGUE();
  if (AnySubscriber(REFII_EVENT_EXPRESSION_STARTED) && ctx.r4.u32 != 0) {
    ExpressionStartedEvent e{};
    PPCContext e_ctx = ctx;
    e.ctx = &e_ctx;
    e.base = base;
    e.entity = Owner(base, ctx.r3.u32);
    e.player = PlayerOf(base, e.entity);
    e.expression = Load32(base, ctx.r4.u32);
    Dispatch(e);
    if (e.cancel) {
      ctx.r3.u64 = 0;
      return;
    }
  }
  __imp__rex_CECExpressionPerformer_StartExpression(ctx, base);
}

// FABLE 3'S CO-OP RENOWN (its followers modify, `sub_823B81C8` / `sub_823B8FF0`): while a partner
// is present a gain is scaled by BalancingFollowers.MultiplayerMultiplier (1.05), and every change
// is mirrored to the partner × RemoteHeroMultiplier (1.0) — any partner, not only a spouse
// (Fable 3 `Fable 3/unpacked/gdb/globals.csv`, entry #fe1c60a6). Applied once, at the top of a
// change, on the machine that decides it: not on a joiner and not inside a network receive (the
// stats owner's comp91+0x6B, the flag this function itself tests). The partner's share goes
// through the same function with the share flag set, which our pool skip
// (`RefiiCoopRenownOwnPool`) credits as it stands.
constexpr float kMultiplayerRenownMultiplier = 1.05f;
constexpr float kRemoteHeroRenownMultiplier = 1.0f;

bool RenownDecidedHere(uint8_t* base, uint32_t owner) {
  const uint32_t root = Load32(base, refii::guest::Address(0x83496AB8u));
  const uint32_t game = root >= 0x10000u && root < 0xC0000000u ? Load32(base, root + 12u) : 0u;
  const uint32_t net = game >= 0x10000u && game < 0xC0000000u ? Load32(base, game + 140u) : 0u;
  if (net >= 0x10000u && net < 0xC0000000u && Load8(base, net + 52u) != 0 && Load8(base, net + 54u) != 0) {
    return false;  // a joiner: the host decides and replicates
  }
  const uint32_t network = refii_mod_component(base, owner, 91u);
  return network == 0u || Load8(base, network + 0x6Bu) == 0;
}

// r3 = stats (57), r4 = the int delta, r5 = the partner-share flag (the body recurses for the co-op
// partner with it set). +28 is the renown, clamped at 0. TU1 0x824D62A0.
REX_EXTERN(rex_lhHeroStats_ModifyRenown) {
  REX_FUNC_PROLOGUE();
  const bool top = g_renown_depth == 0 && (ctx.r5.u32 & 0xFFu) == 0;
  uint32_t partner_stats = 0u;
  if (top) {
    const uint32_t owner = Owner(base, ctx.r3.u32);
    const int32_t player = PlayerOf(base, owner);
    const uint32_t partner = player == 1 ? refii_mod_player_hero(base, 2) : (player == 2 ? refii_mod_player_hero(base, 1) : 0u);
    if (partner != 0u && RenownDecidedHere(base, owner)) {
      partner_stats = refii_mod_component(base, partner, 57u);
      const int32_t delta = static_cast<int32_t>(ctx.r4.u32);
      if (delta > 0) {
        ctx.r4.u64 = static_cast<uint32_t>(
            static_cast<int32_t>(std::lround(static_cast<double>(delta) * kMultiplayerRenownMultiplier)));
      }
    }
  }
  if (AnySubscriber(REFII_EVENT_RENOWN)) {
    RenownEvent e{};
    PPCContext e_ctx = ctx;
    e.ctx = &e_ctx;
    e.base = base;
    e.entity = Owner(base, ctx.r3.u32);
    e.player = PlayerOf(base, e.entity);
    e.delta = static_cast<int32_t>(ctx.r4.u32);
    e.share = g_renown_depth > 0 || (ctx.r5.u32 & 0xFFu) != 0;
    Dispatch(e);
    if (e.cancel && !e.share) {
      return;
    }
    ctx.r4.u64 = static_cast<uint32_t>(e.delta);
  }
  const int32_t credited = static_cast<int32_t>(ctx.r4.u32);
  const PPCContext saved = ctx;
  ++g_renown_depth;
  __imp__rex_lhHeroStats_ModifyRenown(ctx, base);
  if (partner_stats != 0u && credited != 0) {
    PPCContext mirror = saved;
    mirror.r3.u64 = partner_stats;
    mirror.r4.u64 = static_cast<uint32_t>(
        static_cast<int32_t>(std::lround(static_cast<double>(credited) * kRemoteHeroRenownMultiplier)));
    mirror.r5.u64 = 1u;
    __imp__rex_lhHeroStats_ModifyRenown(mirror, base);
  }
  --g_renown_depth;
}

// r3 = stats, f1 = the delta; +16 morality (clamped by +20/+24 after). TU1 0x824D5BC0.
REX_EXTERN(rex_lhHeroStats_ModifyMorality) {
  REX_FUNC_PROLOGUE();
  if (AnySubscriber(REFII_EVENT_MORALITY)) {
    MoralityEvent e{};
    PPCContext e_ctx = ctx;
    e.ctx = &e_ctx;
    e.base = base;
    e.entity = Owner(base, ctx.r3.u32);
    e.player = PlayerOf(base, e.entity);
    e.delta = static_cast<float>(ctx.f1.f64);
    Dispatch(e);
    if (e.cancel) {
      return;
    }
    ctx.f1.f64 = e.delta;
  }
  __imp__rex_lhHeroStats_ModifyMorality(ctx, base);
}

// r3 = stats, f1 = the delta; +36 purity (clamped by +40/+44 after). TU1 0x824D6BE0.
REX_EXTERN(rex_lhHeroStats_ModifyPurity) {
  REX_FUNC_PROLOGUE();
  if (AnySubscriber(REFII_EVENT_PURITY)) {
    PurityEvent e{};
    PPCContext e_ctx = ctx;
    e.ctx = &e_ctx;
    e.base = base;
    e.entity = Owner(base, ctx.r3.u32);
    e.player = PlayerOf(base, e.entity);
    e.delta = static_cast<float>(ctx.f1.f64);
    Dispatch(e);
    if (e.cancel) {
      return;
    }
    ctx.f1.f64 = e.delta;
  }
  __imp__rex_lhHeroStats_ModifyPurity(ctx, base);
}

// r3 = the hero's family component, r4 = the spouse (in the world), r5 = the ceremony or 0.
// Reached by PlayerFamily.Marry and the wedding ceremony. TU1 0x827AEFD8.
REX_EXTERN(rex_CECPlayerFamily_Marry) {
  REX_FUNC_PROLOGUE();
  if (AnySubscriber(REFII_EVENT_MARRIAGE)) {
    MarriageEvent e{};
    PPCContext e_ctx = ctx;
    e.ctx = &e_ctx;
    e.base = base;
    e.hero = Owner(base, ctx.r3.u32);
    e.player = PlayerOf(base, e.hero);
    e.spouse = ctx.r4.u32;
    Dispatch(e);
    if (e.cancel) {
      ctx.r3.u64 = 0;
      return;
    }
  }
  __imp__rex_CECPlayerFamily_Marry(ctx, base);
}

// r3 = the dog component, r4 = {command hash, entry}, r5 = force (skips the unlocked list at
// +740..+744). TU1 0x82810FE0.
REX_EXTERN(rex_CECDog_PerformCommand) {
  REX_FUNC_PROLOGUE();
  if (AnySubscriber(REFII_EVENT_DOG_TRICK) && ctx.r4.u32 != 0) {
    DogTrickEvent e{};
    PPCContext e_ctx = ctx;
    e.ctx = &e_ctx;
    e.base = base;
    e.dog = Owner(base, ctx.r3.u32);
    e.trick = Load32(base, ctx.r4.u32);
    Dispatch(e);
    if (e.cancel) {
      ctx.r3.u64 = 0;
      return;
    }
  }
  __imp__rex_CECDog_PerformCommand(ctx, base);
}

// r3 = the firearm component (+16 ammunition when tracked), r4 = the target or 0, r5 = aimed.
// Its one caller is the fire message's handler, so every shot reaches it. TU1 0x82728730.
REX_EXTERN(rex_CECFirearm_Fire) {
  REX_FUNC_PROLOGUE();
  if (AnySubscriber(REFII_EVENT_RANGED_SHOT)) {
    RangedShotEvent e{};
    PPCContext e_ctx = ctx;
    e.ctx = &e_ctx;
    e.base = base;
    e.weapon = Owner(base, ctx.r3.u32);
    const uint32_t held = e.weapon != 0 ? refii_mod_component(base, e.weapon, kHeldItemComponent) : 0u;
    e.shooter = held != 0 ? Deref(ctx, base, held + 12u) : 0u;
    e.player = PlayerOf(base, e.shooter);
    e.target = ctx.r4.u32;
    e.aimed = (ctx.r5.u32 & 0xFFu) != 0;
    Dispatch(e);
    if (e.cancel) {
      ctx.r3.u64 = 0;
      return;
    }
  }
  __imp__rex_CECFirearm_Fire(ctx, base);
}

// r3 = the entity, r4 = the registry key (resolved by sub_82630218), r5 = replace; returns the new component (its id is its own
// vtable slot 4, GetECType) or 0. CECElectrified, CECInChaos and CECOnFire are added here.
// TU1 0x8238CDC8.
REX_EXTERN(rex_lhEntity_AddComponent) {
  REX_FUNC_PROLOGUE();
  const uint32_t entity = ctx.r3.u32;
  __imp__rex_lhEntity_AddComponent(ctx, base);
  const uint32_t component = ctx.r3.u32;
  if (component == 0 || !AnySubscriber(REFII_EVENT_STATUS_EFFECT)) {
    return;
  }
  PPCContext query = refii::mods::Frame(ctx);
  if (!refii::mods::CallVirtual(query, base, component, 4)) {
    return;
  }
  const int32_t effect = StatusEffectOf(query.r3.u32);
  if (effect != 0) {
    RaiseStatus(ctx, base, entity, effect, true);
  }
}

// r3 = the entity, r4 = the component id. The status components end here (their DeActivate, the
// spells' expiry). TU1 0x8238CB78.
REX_EXTERN(rex_lhEntity_RemoveComponent) {
  REX_FUNC_PROLOGUE();
  const uint32_t entity = ctx.r3.u32;
  const int32_t effect = StatusEffectOf(ctx.r4.u32);
  const bool present = effect != 0 && HasComponent(base, entity, ctx.r4.u32);
  __imp__rex_lhEntity_RemoveComponent(ctx, base);
  if (present && AnySubscriber(REFII_EVENT_STATUS_EFFECT)) {
    RaiseStatus(ctx, base, entity, effect, false);
  }
}

// The player targeting's SetTarget (vtable 0x82007600 slot 19): r3 = the component (owner at +4),
// r4 = the target or 0; the old one is cleared first through slot 18. TU1 0x82845120.
namespace {
/// The player targeting's current target: its own GetTarget (vtable slot 17), 0 when none.
uint32_t CurrentTarget(PPCContext& ctx, uint8_t* base, uint32_t component) {
  PPCContext query = refii::mods::Frame(ctx);
  return component != 0 && refii::mods::CallVirtual(query, base, component, 17) ? query.r3.u32 : 0u;
}
}  // namespace

REX_EXTERN(rex_CECTargetingPlayer_SetTarget) {
  REX_FUNC_PROLOGUE();
  const uint32_t component = ctx.r3.u32;
  const uint32_t target = ctx.r4.u32;
  const bool watched = AnySubscriber(REFII_EVENT_TARGET_CHANGED);
  const uint32_t before = watched ? CurrentTarget(ctx, base, component) : 0u;
  __imp__rex_CECTargetingPlayer_SetTarget(ctx, base);
  if (watched && target != 0 && target != before) {
    TargetChangedEvent e{};
    PPCContext e_ctx = ctx;
    e.ctx = &e_ctx;
    e.base = base;
    e.hero = Owner(base, component);
    e.player = PlayerOf(base, e.hero);
    e.target = target;
    Dispatch(e);
  }
}

// ClearTarget (slot 18): r3 = the component. TU1 0x82844F80.
REX_EXTERN(rex_CECTargetingPlayer_ClearTarget) {
  REX_FUNC_PROLOGUE();
  const uint32_t component = ctx.r3.u32;
  const bool watched = AnySubscriber(REFII_EVENT_TARGET_CHANGED);
  const uint32_t before = watched ? CurrentTarget(ctx, base, component) : 0u;
  __imp__rex_CECTargetingPlayer_ClearTarget(ctx, base);
  if (watched && before != 0) {
    TargetChangedEvent e{};
    PPCContext e_ctx = ctx;
    e.ctx = &e_ctx;
    e.base = base;
    e.hero = Owner(base, component);
    e.player = PlayerOf(base, e.hero);
    e.target = 0;
    Dispatch(e);
  }
}

// A villager's reaction to a deed message, drained once per tick: r3 = the opinion component
// (villager at +4), r4 = the message (+32 the deed record pair, +52 the kind, +72 the doer's weak
// ref), r5 = the player-manager accessor's argument. It adds the opinion effects and the renown, morality and purity the deed
// earns. TU1 0x82786F58.
// THE VILLAGER'S OPINION OF EACH HERO (core/coop_opinion.cpp; Fable 3 keeps a record per hero,
// `sub_82239488(comp+864, hero)`). Fable 2's opinion component holds one hero's worth of state and
// every reader asks hero one (players+104, a weak reference whose holder is +108: `sub_8222C1B8`
// and some twenty inline `lwz rX,0x6C(players)`). While code runs FOR HERO TWO the scope below
//   - swaps player two's per-hero state into the component (the regions under kOpinionRegions,
//     read in the rebuild `sub_8278B948`, the expiry `sub_8278B680`, the Update `sub_82261750` and
//     the relationship getters: +0x302 interest, +0x303 engaged, +0x304 waiting, +0x307 follow
//     for sex, +0x309 flirt, +0x364 icon, +0x3C4 last visit), and
//   - puts hero two's holder (+116) in hero one's (+108), so every hero-one fetch in the call
//     answers hero two;
// and puts both back after. Scopes nest: a component already in scope is left alone, and the
// holder goes back when the outermost scope ends.
struct OpinionRegion {
  uint32_t offset;
  uint32_t size;
};

// Block order is fixed: the first 40 bytes (base +24, current +44) are what the host's targeted
// readout and refii_mod_opinion read.
constexpr OpinionRegion kOpinionRegions[] = {
    {24u, 40u},   // base and current axes
    {176u, 60u},  // the five modifier lists {word, head node, size}
    {64u, 40u},   // appearance (+64) and script (+84) terms
    {492u, 4u},   // the renown scale the rebuild writes
    {716u, 20u},  // weapon terms (`sub_82266780`)
    {770u, 3u},   // shown romantic interest, engaged, waiting to marry
    {775u, 1u},   // follow for sex
    {777u, 1u},   // can flirt
    {864u, 8u},   // last sex day, icon type
    {964u, 12u},  // last visit day, visit count, harassment clock
    {980u, 8u},   // harassment count, harasser
    {108u, 4u},   // the love the relationship icon last showed (`sub_82268848`)
};

constexpr uint32_t kOpinionRegionBytes = [] {
  uint32_t total = 0u;
  for (const OpinionRegion& r : kOpinionRegions) {
    total += r.size;
  }
  return total;
}();
static_assert(kOpinionRegionBytes <= REFII_OPINION_BLOCK_BYTES, "the opinion block holds every region");

void OpinionBlock(uint8_t* base, uint32_t component, uint8_t* out) {
  uint32_t at = 0u;
  for (const OpinionRegion& r : kOpinionRegions) {
    std::memcpy(out + at, base + component + r.offset, r.size);
    at += r.size;
  }
}

void OpinionWrite(uint8_t* base, uint32_t component, const uint8_t* in) {
  uint32_t at = 0u;
  for (const OpinionRegion& r : kOpinionRegions) {
    std::memcpy(base + component + r.offset, in + at, r.size);
    at += r.size;
  }
}

/// The player record that holds the two heroes' weak references (+104 hero one, +112 hero two).
uint32_t OpinionPlayers(uint8_t* base) {
  const uint32_t root = Load32(base, refii::guest::Address(0x83496AB8u));
  const uint32_t game = root >= 0x10000u ? Load32(base, root + 12u) : 0u;
  const uint32_t manager = game >= 0x10000u ? Load32(base, game + 88u) : 0u;
  const uint32_t cell = manager >= 0x10000u ? Load32(base, manager + 4u) : 0u;
  return cell >= 0x10000u ? Load32(base, cell) : 0u;
}

/// Player two's first block for a villager: its own attitudes and terms as they stand, empty
/// modifier lists of their own (each a fresh head node, next = prev = itself, as the list walks
/// `**(list+4)` expect), and no relationship, visits or harassment.
bool OpinionFirstBlock(PPCContext& ctx, uint8_t* base, const uint8_t* main, uint8_t* out) {
  std::memcpy(out, main, REFII_OPINION_BLOCK_BYTES);
  for (uint32_t list = 0; list < 5u; ++list) {
    PPCContext work = Frame(ctx);
    work.r3.u64 = 16u;
    if (!CallGuest(work, base, refii::guest::Address(0x8221F258u)) || work.r3.u32 < 0x10000u) {
      return false;
    }
    const uint32_t node = work.r3.u32;
    refii::mods::StoreU32(base, node, node);
    refii::mods::StoreU32(base, node + 4u, node);
    uint8_t* entry = out + 40u + (12u * list);
    const uint32_t head = __builtin_bswap32(node);
    std::memcpy(entry + 4u, &head, 4u);
    std::memset(entry + 8u, 0, 4u);
  }
  // No relationship, visits or harassment yet (+770 onward).
  uint32_t at = 0u;
  for (const OpinionRegion& r : kOpinionRegions) {
    if (r.offset >= 770u) {
      std::memset(out + at, 0, r.size);
    }
    at += r.size;
  }
  return true;
}

thread_local uint32_t t_opinion_scoped[8] = {};
thread_local int32_t t_opinion_depth = 0;
thread_local uint32_t t_opinion_holder = 0u;
thread_local uint32_t t_opinion_players = 0u;

struct OpinionHeroScope {
  uint8_t* base = nullptr;
  uint32_t component = 0u;
  uint8_t main[REFII_OPINION_BLOCK_BYTES] = {};

  /// Enters when `hero` is hero two (and hero one exists); otherwise does nothing.
  OpinionHeroScope(PPCContext& ctx, uint8_t* guest, uint32_t comp, uint32_t hero) {
    if (comp < 0x10000u || hero == 0u || hero != refii_mod_player_hero(guest, 2) ||
        refii_mod_player_hero(guest, 1) == 0u || t_opinion_depth >= 8) {
      return;
    }
    for (int32_t i = 0; i < t_opinion_depth; ++i) {
      if (t_opinion_scoped[i] == comp) {
        return;
      }
    }
    const uint32_t players = t_opinion_depth == 0 ? OpinionPlayers(guest) : t_opinion_players;
    if (players < 0x10000u) {
      return;
    }
    OpinionBlock(guest, comp, main);
    uint8_t two[REFII_OPINION_BLOCK_BYTES] = {};
    if (refii_bridge_opinion_two_load(comp, two) == 0 && !OpinionFirstBlock(ctx, guest, main, two)) {
      return;
    }
    base = guest;
    component = comp;
    OpinionWrite(base, component, two);
    if (t_opinion_depth == 0) {
      t_opinion_players = players;
      t_opinion_holder = Load32(base, players + 108u);
      refii_bridge_hero_slot_lend(t_opinion_holder);
      refii::mods::StoreU32(base, players + 108u, Load32(base, players + 116u));
    }
    t_opinion_scoped[t_opinion_depth++] = comp;
  }

  bool active() const { return base != nullptr; }

  ~OpinionHeroScope() {
    if (base == nullptr) {
      return;
    }
    uint8_t two[REFII_OPINION_BLOCK_BYTES] = {};
    OpinionBlock(base, component, two);
    refii_bridge_opinion_two_store(component, two);
    OpinionWrite(base, component, main);
    --t_opinion_depth;
    if (t_opinion_depth == 0) {
      refii::mods::StoreU32(base, t_opinion_players + 108u, t_opinion_holder);
      refii_bridge_hero_slot_return();
    }
  }

  OpinionHeroScope(const OpinionHeroScope&) = delete;
  OpinionHeroScope& operator=(const OpinionHeroScope&) = delete;
};

/// The hero a villager's opinion concerns when the caller names none: the nearer hero, the
/// game's own rule for whom a villager notices (GetEntityToNoticeExpressionsOf `sub_82798578`).
uint32_t OpinionNearerHero(PPCContext& ctx, uint8_t* base, uint32_t villager) {
  const uint32_t one = refii_mod_player_hero(base, 1);
  const uint32_t two = refii_mod_player_hero(base, 2);
  float v[3], a[3], b[3];
  if (villager == 0u || one == 0u || two == 0u || refii_mod_entity_position(&ctx, base, villager, v) == 0 ||
      refii_mod_entity_position(&ctx, base, one, a) == 0 || refii_mod_entity_position(&ctx, base, two, b) == 0) {
    return one;
  }
  const auto d2 = [&v](const float* p) {
    return (p[0] - v[0]) * (p[0] - v[0]) + (p[1] - v[1]) * (p[1] - v[1]) + (p[2] - v[2]) * (p[2] - v[2]);
  };
  return d2(b) < d2(a) ? two : one;
}

REX_EXTERN(rex_CECOpinionReaction_OnDeed) {
  REX_FUNC_PROLOGUE();
  PPCContext entry = ctx;
  const uint32_t doer = ctx.r4.u32 != 0u ? Deref(ctx, base, ctx.r4.u32 + 72u) : 0u;
  OpinionHeroScope scope(entry, base, ctx.r3.u32, doer);
  if (AnySubscriber(REFII_EVENT_OPINION_DEED) && ctx.r4.u32 != 0) {
    OpinionDeedEvent e{};
    PPCContext e_ctx = ctx;
    e.ctx = &e_ctx;
    e.base = base;
    e.villager = Owner(base, ctx.r3.u32);
    e.doer = Deref(ctx, base, ctx.r4.u32 + 72u);
    e.doer_player = PlayerOf(base, e.doer);
    e.deed = Load32(base, ctx.r4.u32 + 32u);
    e.kind = static_cast<int32_t>(Load32(base, ctx.r4.u32 + 52u));
    Dispatch(e);
    if (e.cancel) {
      return;
    }
  }
  __imp__rex_CECOpinionReaction_OnDeed(ctx, base);
}

// The opinion component's per-frame update (r3 the component): the shipped pass is hero one's;
// with a partner, the opinion math runs again inside player two's scope, in the shipped order and
// behind the shipped gates (`sub_82261750`): expiry `sub_8278B680`, the rebuild `sub_8278B948`,
// then the appearance term `sub_82791658` (byte_83318FDC, +779, not `sub_821DE478`) and the weapon
// term `sub_82266780(comp, 0)` (byte_83318FDD, +779). Icons, visits and harassment stay the
// shipped pass's, except the relationship icon `sub_82268848`, which player two gets as their own
// (the world-icon hooks below). TU1 0x82261750.
REX_EXTERN(rex_CECOpinionReaction_Update) {
  REX_FUNC_PROLOGUE();
  const PPCContext entry = ctx;
  const uint32_t comp = ctx.r3.u32;
  __imp__rex_CECOpinionReaction_Update(ctx, base);
  const uint32_t two = refii_mod_player_hero(base, 2);
  if (two == 0u || comp < 0x10000u) {
    return;
  }
  PPCContext call = entry;
  OpinionHeroScope scope(call, base, comp, two);
  if (!scope.active()) {
    return;
  }
  const auto run = [&](uint32_t address, uint32_t r4) {
    PPCContext work = Frame(entry);
    work.r3.u64 = comp;
    work.r4.u64 = r4;
    CallGuest(work, base, refii::guest::Address(address));
    return work.r3.u32;
  };
  run(0x8278B680u, 0u);
  run(0x8278B948u, 0u);
  const bool engaged = Load8(base, comp + 779u) != 0u;
  if (engaged && Load8(base, refii::guest::Address(0x83318FDCu)) != 0u && (run(0x821DE478u, 0u) & 0xFFu) == 0u) {
    run(0x82791658u, 0u);
  }
  if (engaged && Load8(base, refii::guest::Address(0x83318FDDu)) != 0u) {
    run(0x82266780u, 0u);
  }
  // The relationship icon, last as shipped. A joiner is sent player two's by the host.
  if (refii_mod_coop_mode(base) != 3) {
    run(0x82268848u, 0u);
  }
}

// A villager's spoken reaction (`sub_824742D0`, a behaviour's virtual: r6 the villager; it asks
// GetAnOpinion and GetCurrentReaction for hero one, 0x82474460) and its conversation topic
// (`sub_8247CBB0`: r5 the villager; hero one inline at 0x8247D21C): both run for the hero the
// villager notices, so player two is greeted and talked to as the villager sees player two.
// TU1 0x824742D0 / 0x8247CBB0.
REX_EXTERN(rex_CECVillager_SpeechReaction) {
  REX_FUNC_PROLOGUE();
  PPCContext entry = ctx;
  const uint32_t villager = ctx.r6.u32;
  const uint32_t opinion = villager != 0u ? refii_mod_component(base, villager, 47u) : 0u;
  OpinionHeroScope scope(entry, base, opinion, opinion != 0u ? OpinionNearerHero(entry, base, villager) : 0u);
  __imp__rex_CECVillager_SpeechReaction(ctx, base);
}

REX_EXTERN(rex_CECVillager_ChooseTopic) {
  REX_FUNC_PROLOGUE();
  PPCContext entry = ctx;
  const uint32_t villager = ctx.r5.u32;
  const uint32_t opinion = villager != 0u ? refii_mod_component(base, villager, 47u) : 0u;
  OpinionHeroScope scope(entry, base, opinion, opinion != 0u ? OpinionNearerHero(entry, base, villager) : 0u);
  __imp__rex_CECVillager_ChooseTopic(ctx, base);
}

// PLAYER TWO'S OPINION ICONS. The relationship icon (`sub_82268848` -> `sub_821AD468`, key
// "<uid>_Opinion", types 22..26 from 0x8209C00C) and the "+N love" splashes (`sub_82789FC0`,
// types 1..20 from 0x8209BBD8) go through the world-icon manager's request
// `sub_823DD6B0(wim, &key, thing, &text, type)` and removal `sub_821BD798(wim, thing, &key)`, one
// icon per key. Inside player two's opinion scope the key gains "_P2", so player two's icons stand
// beside player one's on the same villager (the manager stacks them) and the host draws each in
// its own player's view (host_bridge.cpp RefiiHostGetWorldSprites, flag 4). Online the joiner is
// player two: the network copy (`sub_8279F7B0` / `sub_8279F868`, sent with the unsuffixed key) is
// sent for player two's icons only.
namespace {

constexpr char kPlayerTwoKey[] = "_P2";

/// A villager's icons going away whole: its opinion component shut down (`sub_82790988`) or reset
/// (`sub_827931C0`). Player two's go with player one's.
thread_local bool t_opinion_icons_gone = false;

/// Whether the guest string object at `string` (a pointer to its rep {chars, length, ...},
/// `sub_82208778`) ends in "_P2".
bool KeyIsPlayerTwo(const uint8_t* base, uint32_t string) {
  const uint32_t rep = string >= 0x10000u ? Load32(base, string) : 0u;
  const uint32_t chars = rep >= 0x10000u ? Load32(base, rep) : 0u;
  const uint32_t length = rep >= 0x10000u ? Load32(base, rep + 4u) : 0u;
  return chars >= 0x10000u && length >= 3u && length <= 512u &&
         std::memcmp(base + chars + length - 3u, kPlayerTwoKey, 3u) == 0;
}

/// Runs `call(ctx, copy)` with player two's copy of the key object at `key` ("<key>_P2"), built on
/// the guest stack below the caller's frame (`sub_821F0240` copy, `sub_821DA9C0` append) and
/// released after (`sub_82214DD8`); the original runs below the copy.
template <typename Call>
void WithPlayerTwoKey(PPCContext& ctx, uint8_t* base, uint32_t key, Call&& call) {
  const uint32_t caller_sp = ctx.r1.u32;
  const PPCContext work = Frame(ctx, 0x100u);
  const uint32_t copy = (work.r1.u32 + 0x80u) & ~0xFu;
  const uint32_t suffix = copy + 0x10u;
  std::memcpy(base + suffix, kPlayerTwoKey, sizeof(kPlayerTwoKey));
  refii::mods::StoreU32(base, copy, 0u);
  PPCContext step = work;
  step.r3.u64 = copy;
  step.r4.u64 = key;
  if (!CallGuest(step, base, refii::guest::Address(0x821F0240u))) {
    call(ctx, key);
    return;
  }
  step = work;
  step.r3.u64 = copy;
  step.r4.u64 = suffix;
  CallGuest(step, base, refii::guest::Address(0x821DA9C0u));
  ctx.r1.u64 = work.r1.u32;
  call(ctx, copy);
  ctx.r1.u64 = caller_sp;
  step = work;
  step.r3.u64 = copy;
  CallGuest(step, base, refii::guest::Address(0x82214DD8u));
}

/// The host's network copy of an icon is player two's alone online (the joiner plays player two).
bool HostSendsIcon(uint8_t* base) {
  return t_opinion_depth > 0 || t_opinion_icons_gone || refii_mod_coop_mode(base) != 2;
}

}  // namespace

// The world-icon manager's request (r3 manager, r4 &key, r5 thing, r6 &text, r7 type). TU1 0x823DD6B0.
REX_EXTERN(rex_lhWorldIcons_Request) {
  if (t_opinion_depth == 0 || ctx.r4.u32 < 0x10000u) {
    __imp__rex_lhWorldIcons_Request(ctx, base);
    return;
  }
  WithPlayerTwoKey(ctx, base, ctx.r4.u32, [base](PPCContext& run, uint32_t key) {
    run.r4.u64 = key;
    __imp__rex_lhWorldIcons_Request(run, base);
  });
}

// The world-icon manager's removal (r3 manager, r4 thing, r5 &key). TU1 0x821BD798.
REX_EXTERN(rex_lhWorldIcons_Remove) {
  const uint32_t key = ctx.r5.u32;
  const auto remove_two = [base](PPCContext& run, uint32_t two) {
    run.r5.u64 = two;
    __imp__rex_lhWorldIcons_Remove(run, base);
  };
  if (t_opinion_depth > 0 && key >= 0x10000u) {
    WithPlayerTwoKey(ctx, base, key, remove_two);
    return;
  }
  const PPCContext entry = ctx;
  __imp__rex_lhWorldIcons_Remove(ctx, base);
  if (t_opinion_icons_gone && key >= 0x10000u && refii_mod_player_hero(base, 2) != 0u) {
    PPCContext again = entry;
    WithPlayerTwoKey(again, base, key, remove_two);
  }
}

// The opinion component's shutdown and reset, each removing the villager's relationship icon.
// TU1 0x82790988 / 0x827931C0.
REX_EXTERN(rex_CECOpinionReaction_Shutdown) {
  const bool previous = t_opinion_icons_gone;
  t_opinion_icons_gone = true;
  __imp__rex_CECOpinionReaction_Shutdown(ctx, base);
  t_opinion_icons_gone = previous;
}

REX_EXTERN(rex_CECOpinionReaction_Reset) {
  const bool previous = t_opinion_icons_gone;
  t_opinion_icons_gone = true;
  __imp__rex_CECOpinionReaction_Reset(ctx, base);
  t_opinion_icons_gone = previous;
}

// The opinion icon's network copy, request and removal (gated in the guest on an online host,
// `game+140` +52/+53). TU1 0x8279F7B0 / 0x8279F868.
REX_EXTERN(rex_CECOpinionReaction_SendIcon) {
  if (HostSendsIcon(base)) {
    __imp__rex_CECOpinionReaction_SendIcon(ctx, base);
  }
}

REX_EXTERN(rex_CECOpinionReaction_SendIconRemoved) {
  if (HostSendsIcon(base)) {
    __imp__rex_CECOpinionReaction_SendIconRemoved(ctx, base);
  }
}

// A world icon's per-frame update (r3 the icon, r4 nonzero when it is measured against the hero):
// its size, fade and look-at cue follow hero one's distance (`sub_8222C1B8` at 0x82241CB4). Player
// two's icons are measured from player two: hero one's slot is lent to hero two for the call, as
// the opinion scope lends it. TU1 0x82241AD8.
REX_EXTERN(rex_lhWorldIcon_Update) {
  const uint32_t icon = ctx.r3.u32;
  const uint32_t players = t_opinion_depth == 0 && (ctx.r4.u32 & 0xFFu) != 0u && icon >= 0x10000u &&
                                   KeyIsPlayerTwo(base, icon + 16u) && refii_mod_player_hero(base, 2) != 0u
                               ? OpinionPlayers(base)
                               : 0u;
  if (players < 0x10000u) {
    __imp__rex_lhWorldIcon_Update(ctx, base);
    return;
  }
  const uint32_t holder = Load32(base, players + 108u);
  refii_bridge_hero_slot_lend(holder);
  refii::mods::StoreU32(base, players + 108u, Load32(base, players + 116u));
  __imp__rex_lhWorldIcon_Update(ctx, base);
  refii::mods::StoreU32(base, players + 108u, holder);
  refii_bridge_hero_slot_return();
}

// A crime recorded: r3 = the crime component, r4 = the criminal, r5 = the victim, r6 = the crime
// type, r7 a flag; v1 the place. Returns 1 when the crime was recorded (`sub_827C9C10`, which
// recurses once with type 3 for a reported murder). A refusal returns 0, as the function's own
// gates do. TU1 0x827C9C10.
REX_EXTERN(rex_CECCrime_Add) {
  REX_FUNC_PROLOGUE();
  if (AnySubscriber(REFII_EVENT_CRIME)) {
    CrimeEvent e{};
    PPCContext e_ctx = ctx;
    e.ctx = &e_ctx;
    e.base = base;
    e.criminal = ctx.r4.u32;
    e.player = PlayerOf(base, e.criminal);
    e.victim = ctx.r5.u32;
    e.victim_player = PlayerOf(base, e.victim);
    e.type = static_cast<int32_t>(ctx.r6.u32);
    Dispatch(e);
    if (e.cancel) {
      ctx.r3.u64 = 0u;
      return;
    }
  }
  __imp__rex_CECCrime_Add(ctx, base);
}

// An achievement unlocked: r3 = the stats component (owner at +4), r4 = the id (1..50). The body
// checks the owner is a player and writes it for both heroes (`sub_824D8F88`; Lua
// Stats.UnlockAchievement). TU1 0x824D8F88.
REX_EXTERN(rex_CECStats_UnlockAchievement) {
  REX_FUNC_PROLOGUE();
  if (AnySubscriber(REFII_EVENT_ACHIEVEMENT) && ctx.r4.u32 != 0u) {
    AchievementEvent e{};
    PPCContext e_ctx = ctx;
    e.ctx = &e_ctx;
    e.base = base;
    e.id = static_cast<int32_t>(ctx.r4.u32);
    e.source = Owner(base, ctx.r3.u32);
    e.player = PlayerOf(base, e.source);
    Dispatch(e);
    if (e.cancel) {
      return;
    }
  }
  __imp__rex_CECStats_UnlockAchievement(ctx, base);
}

// A script's direct opinion change: r3 = the component, r4 = the axis, f1 = the delta; the axis
// value at +24 + 4*axis, clamped to [-1, 1]. TU1 0x827915D8.
REX_EXTERN(rex_CECOpinionReaction_ModifyAxisValue) {
  REX_FUNC_PROLOGUE();
  if (AnySubscriber(REFII_EVENT_OPINION_MODIFIED)) {
    OpinionModifiedEvent e{};
    PPCContext e_ctx = ctx;
    e.ctx = &e_ctx;
    e.base = base;
    e.villager = Owner(base, ctx.r3.u32);
    e.axis = static_cast<int32_t>(ctx.r4.u32);
    e.delta = static_cast<float>(ctx.f1.f64);
    Dispatch(e);
    if (e.cancel) {
      return;
    }
    ctx.f1.f64 = e.delta;
  }
  __imp__rex_CECOpinionReaction_ModifyAxisValue(ctx, base);
}

// The world clock's per-frame update: r3 = the time manager (+4 the day fraction, +8 the day
// count; +16 a fast forward in flight; +36 stopped), f1 = seconds. Every source of time — natural,
// fast forward, a direct set — is seen here as the value it leaves. TU1 0x82298460.
REX_EXTERN(rex_lhGameTimeManager_Update) {
  REX_FUNC_PROLOGUE();
  const uint32_t manager = ctx.r3.u32;
  __imp__rex_lhGameTimeManager_Update(ctx, base);
  if (manager == 0 || !AnySubscriber(REFII_EVENT_TIME_OF_DAY)) {
    return;
  }
  const float time = LoadF(base, manager + 4u);
  const int32_t day = static_cast<int32_t>(Load32(base, manager + 8u));
  int32_t hour = static_cast<int32_t>(time * 24.0f);
  hour = hour < 0 ? 0 : (hour > 23 ? 23 : hour);
  if (hour == g_last_hour && day == g_last_day) {
    return;
  }
  const bool first = g_last_hour < 0;
  const bool new_day = !first && day != g_last_day;
  g_last_hour = hour;
  g_last_day = day;
  if (first) {
    return;
  }
  TimeOfDayEvent e{};
  PPCContext e_ctx = ctx;
  e.ctx = &e_ctx;
  e.base = base;
  e.hour = hour;
  e.day = day;
  e.time = time;
  e.new_day = new_day;
  Dispatch(e);
}

// r3 = the environment-theme manager (world+80), r4 = the theme's name (an LHString handle),
// f1 = the target weight, f2 = the blend seconds. Scripted weather goes through it.
// TU1 0x82549B80.
REX_EXTERN(rex_lhEnvironmentThemeManager_BlendToTheme) {
  REX_FUNC_PROLOGUE();
  if (AnySubscriber(REFII_EVENT_WEATHER_THEME)) {
    char theme[128];
    LhText(base, ctx.r4.u32, theme, sizeof(theme));
    WeatherThemeEvent e{};
    PPCContext e_ctx = ctx;
    e.ctx = &e_ctx;
    e.base = base;
    e.theme = theme;
    e.weight = static_cast<float>(ctx.f1.f64);
    e.seconds = static_cast<float>(ctx.f2.f64);
    Dispatch(e);
    if (e.cancel) {
      return;
    }
    ctx.f1.f64 = e.weight;
    ctx.f2.f64 = e.seconds;
  }
  __imp__rex_lhEnvironmentThemeManager_BlendToTheme(ctx, base);
}

// SleepMenu.ShowSleepMenu: builds the SLEEP_LENGTH choices and marks the menu visible.
// TU1 0x8243D5A8.
REX_EXTERN(rex_SleepMenu_Show) {
  REX_FUNC_PROLOGUE();
  if (AnySubscriber(REFII_EVENT_SLEEP_MENU)) {
    SleepMenuEvent e{};
    PPCContext e_ctx = ctx;
    e.ctx = &e_ctx;
    e.base = base;
    e.seen = true;
    Dispatch(e);
    if (e.cancel) {
      ctx.r3.u64 = 0;
      return;
    }
  }
  __imp__rex_SleepMenu_Show(ctx, base);
}

// The level-change command's Start (vtable slot 5): r3 = the command (+20 world, +24 level,
// +28 entrance as LHString handles; +36 travel type; +50 full load, cleared here when the
// destination is the loaded level). TU1 0x823D5760.
REX_EXTERN(rex_CmdLevelChange_Start) {
  REX_FUNC_PROLOGUE();
  const uint32_t command = ctx.r3.u32;
  __imp__rex_CmdLevelChange_Start(ctx, base);
  if (command == 0 || !AnySubscriber(REFII_EVENT_TRAVEL)) {
    return;
  }
  char world[96], level[96], entrance[96];
  LhText(base, command + 20u, world, sizeof(world));
  LhText(base, command + 24u, level, sizeof(level));
  LhText(base, command + 28u, entrance, sizeof(entrance));
  TravelEvent e{};
  PPCContext e_ctx = ctx;
  e.ctx = &e_ctx;
  e.base = base;
  e.world = world;
  e.level = level;
  e.entrance = entrance;
  e.type = static_cast<int32_t>(Load32(base, command + 36u));
  e.full_load = Load8(base, command + 50u) != 0;
  Dispatch(e);
}

// r3 = the fast-travel manager, r4 = the destination level (an LHString handle), r5 = the stop
// (+4 its marker's name), r6 = paid. Returns 1 when the travel was queued. TU1 0x82519B08.
REX_EXTERN(rex_FastTravelManager_TravelToStop) {
  REX_FUNC_PROLOGUE();
  if (AnySubscriber(REFII_EVENT_FAST_TRAVEL)) {
    char level[96], stop[96];
    LhText(base, ctx.r4.u32, level, sizeof(level));
    if (ctx.r5.u32 != 0) {
      LhText(base, ctx.r5.u32 + 4u, stop, sizeof(stop));
    } else {
      stop[0] = 0;
    }
    FastTravelEvent e{};
    PPCContext e_ctx = ctx;
    e.ctx = &e_ctx;
    e.base = base;
    e.level = level;
    e.stop = stop;
    e.paid = (ctx.r6.u32 & 0xFFu) != 0;
    Dispatch(e);
    if (e.cancel) {
      ctx.r3.u64 = 0;
      return;
    }
  }
  __imp__rex_FastTravelManager_TravelToStop(ctx, base);
}

namespace {
/// Whether the game is in menu mode: the top gameflow command is the GUI command
/// (rex_lhGameFlow_IsGuiActive, the game's own test, no arguments).
bool GuiActive(PPCContext& ctx, uint8_t* base) {
  PPCContext work = refii::mods::Frame(ctx);
  rex_lhGameFlow_IsGuiActive(work, base);
  return (work.r3.u32 & 0xFFu) != 0;
}

/// The screen the GUI manager states it shows: *(*(0x83496B08)+56)+4.
int32_t CurrentScreen(uint8_t* base) {
  const uint32_t gui = Load32(base, refii::guest::Address(0x83496B08u));
  const uint32_t screens = gui >= 0x10000u && gui < 0xC0000000u ? Load32(base, gui + 56u) : 0u;
  return screens >= 0x10000u && screens < 0xC0000000u ? static_cast<int32_t>(Load32(base, screens + 4u)) : -1;
}

void RaiseMenuOpened(PPCContext& ctx, uint8_t* base) {
  MenuOpenedEvent e{};
  PPCContext e_ctx = ctx;
  e.ctx = &e_ctx;
  e.base = base;
  e.seen = true;
  Dispatch(e);
}
}  // namespace

// The GUI manager entering menu mode (the PAUSE_GAME sound state, GUI:OnEnter(), the GUI gameflow
// command that pauses the world): r3 = the manager. Called again for screen switches inside a
// menu; only the call that queues the GUI command (the menu mode was off) is an opening.
// TU1 0x8236EB68.
REX_EXTERN(rex_lhGuiManager_EnterGuiMode) {
  REX_FUNC_PROLOGUE();
  const bool opening = AnySubscriber(REFII_EVENT_MENU_OPENED) && !GuiActive(ctx, base);
  if (opening) {
    RaiseMenuOpened(ctx, base);
  }
  __imp__rex_lhGuiManager_EnterGuiMode(ctx, base);
}

// The second way in (no GUI:OnEnter(); the co-op screen 29, the network): r3 = the manager; it
// queues the GUI command unless its gate byte is set. TU1 0x82370050 / disc 0x82338248.
REX_EXTERN(rex_lhGuiManager_EnterGuiModeQuiet) {
  REX_FUNC_PROLOGUE();
  const uint32_t manager = ctx.r3.u32;
  // The gate byte is +38 on TU1 and +37 on the disc build (the GUI manager's flags moved by one).
  const uint32_t gate = refii::guest::Pick(38u, 37u);
  const bool opening = AnySubscriber(REFII_EVENT_MENU_OPENED) && manager != 0 && Load8(base, manager + gate) == 0 &&
                       !GuiActive(ctx, base);
  if (opening) {
    RaiseMenuOpened(ctx, base);
  }
  __imp__rex_lhGuiManager_EnterGuiModeQuiet(ctx, base);
}

// Leaving it: GUI:OnExit(), the gameflow pop — which the body runs only while menu mode is on and
// +40 is 1 (a query box sets it otherwise). r3 = the manager, r4 = a flag. TU1 0x8236ED98.
REX_EXTERN(rex_lhGuiManager_ExitScreen) {
  REX_FUNC_PROLOGUE();
  const uint32_t manager = ctx.r3.u32;
  const bool closing = AnySubscriber(REFII_EVENT_MENU_CLOSED) && manager != 0 &&
                       Load32(base, manager + 40u) == 1u && GuiActive(ctx, base);
  __imp__rex_lhGuiManager_ExitScreen(ctx, base);
  if (closing) {
    MenuClosedEvent e{};
    PPCContext e_ctx = ctx;
    e.ctx = &e_ctx;
    e.base = base;
    e.seen = true;
    Dispatch(e);
  }
}

// The GUI manager's screen setter — every screen change states its id here (53 callers; the
// handler's activation is inline in many): r3 = the manager, r4 = the screen id, r5 = a flag.
// The event states TU1 numbering on both builds (the disc build's ids from 13 up are one lower).
// TU1 0x8236E630.
REX_EXTERN(rex_lhGuiManager_SetScreen) {
  REX_FUNC_PROLOGUE();
  const int32_t screen = static_cast<int32_t>(ctx.r4.u32);
  const bool changed = AnySubscriber(REFII_EVENT_MENU_SCREEN) && screen != CurrentScreen(base);
  __imp__rex_lhGuiManager_SetScreen(ctx, base);
  if (changed) {
    MenuScreenEvent e{};
    PPCContext e_ctx = ctx;
    e.ctx = &e_ctx;
    e.base = base;
    e.screen = static_cast<int32_t>(refii::guest::ScreenIdToTu1(static_cast<uint32_t>(screen)));
    Dispatch(e);
  }
}

namespace {
uint32_t g_painted_day_set = 0;
bool g_painted_seen = false;
bool g_painted_inside = false;
}  // namespace

// The painted weather map's lookup at the hero's position: r3 = the paint map, v1 = the position;
// returns the region's environment day-set hash, or -1 outside every painted region. Its one
// caller is the painted-weight pass (sub_821ABFD8), once a frame. TU1 0x822D6A20.
REX_EXTERN(rex_lhPaintMap_DaySetAt) {
  REX_FUNC_PROLOGUE();
  __imp__rex_lhPaintMap_DaySetAt(ctx, base);
  const bool inside = ctx.r3.u32 != 0xFFFFFFFFu;
  const uint32_t day_set = inside ? ctx.r3.u32 : 0u;
  if (g_painted_seen && day_set == g_painted_day_set && inside == g_painted_inside) {
    return;
  }
  g_painted_inside = inside;
  const uint32_t previous = g_painted_day_set;
  const bool first = !g_painted_seen;
  g_painted_seen = true;
  g_painted_day_set = day_set;
  if (first || !AnySubscriber(REFII_EVENT_WEATHER_REGION)) {
    return;
  }
  WeatherRegionEvent e{};
  PPCContext e_ctx = ctx;
  e.ctx = &e_ctx;
  e.base = base;
  e.day_set = day_set;
  e.previous = previous;
  e.inside = inside;
  Dispatch(e);
}

namespace {
/// The sleep menu's state (TU1 0x83496BBC holds a pointer to it): +0 visible, +8 the bed's weak
/// reference (ShowSleepMenu stores it; the poll reads it back for component 182's sleep type).
constexpr uint32_t kSleepMenuState = 0x83496BBCu;
constexpr int32_t kSleepChoices = 4;
}  // namespace

// The shared choice menu's delivery: r3 = the menu (+0 the selected index, +8 and +12 open, +13
// cancelled); it posts message 35 with cancelled ? 0 : index + 1 and closes. Only the sleep menu's
// answer is an event: the poll that reads message 35 runs only while that menu is up. TU1 0x823DC2B0.
REX_EXTERN(rex_lhMenuBox_Close) {
  REX_FUNC_PROLOGUE();
  const uint32_t menu = ctx.r3.u32;
  const uint32_t state = Load32(base, refii::guest::Address(kSleepMenuState));
  const bool sleep_open = state >= 0x10000u && state < 0xC0000000u && Load8(base, state) == 1u;
  if (sleep_open && menu != 0 && Load32(base, menu + 8u) != 0 && Load8(base, menu + 12u) != 0 &&
      AnySubscriber(REFII_EVENT_SLEEP_CHOSEN)) {
    SleepChosenEvent e{};
    PPCContext e_ctx = ctx;
    e.ctx = &e_ctx;
    e.base = base;
    e.bed = Deref(ctx, base, state + 8u);
    e.choice = static_cast<int32_t>(Load32(base, menu));
    e.cancelled = Load8(base, menu + 13u) != 0;
    Dispatch(e);
    if (e.cancel || e.cancelled) {
      base[menu + 13u] = 1u;
    } else if (e.choice >= 0 && e.choice < kSleepChoices) {
      refii::mods::StoreU32(base, menu, static_cast<uint32_t>(e.choice));
    }
  }
  __imp__rex_lhMenuBox_Close(ctx, base);
}

namespace {

/// The world (W = *(*(*(root+12)+88)+4)); +128 its Slow Time scale.
uint32_t WorldObject(uint8_t* base) {
  const uint32_t root = Load32(base, refii::guest::Address(0x83496AB8u));
  const uint32_t game = root >= 0x10000u ? Load32(base, root + 12u) : 0u;
  const uint32_t holder = game >= 0x10000u ? Load32(base, game + 88u) : 0u;
  return holder >= 0x10000u ? Load32(base, holder + 4u) : 0u;
}

constexpr uint32_t kWorldTimeScale = 128;
/// Raise Dead's minion component (CECDeadRiser): the bind writes its summoner and lifetime.
constexpr uint32_t kDeadRiserComponent = 207;

void RaiseSummoned(PPCContext& ctx, uint8_t* base, uint32_t summoner, uint32_t summon, int32_t kind) {
  SummonedEvent e{};
  PPCContext e_ctx = ctx;
  e.ctx = &e_ctx;
  e.base = base;
  e.summoner = summoner;
  e.player = PlayerOf(base, summoner);
  e.summon = summon;
  e.kind = kind;
  Dispatch(e);
}

}  // namespace

// Slow Time's world scale: r3 = the caster (in the world), f1 = the scale. The body gives the
// caster (and the co-op partner) the inverse rate through their temporal component and writes
// world+128 when it changed. Called by the spell's ramp each frame the target changes and with 1.0
// at its end. TU1 0x827ECA38.
REX_EXTERN(rex_SpellSlowTime_SetWorldScale) {
  REX_FUNC_PROLOGUE();
  const uint32_t caster = ctx.r3.u32;
  const uint32_t world = WorldObject(base);
  const float before = world != 0 ? LoadF(base, world + kWorldTimeScale) : 1.0f;
  float scale = static_cast<float>(ctx.f1.f64);
  const bool watched = AnySubscriber(REFII_EVENT_SLOW_TIME) && caster != 0 &&
                       (Load8(base, caster + 0x90u) & 0x40u) != 0;
  if (watched && scale != before) {
    SlowTimeEvent e{};
    PPCContext e_ctx = ctx;
    e.ctx = &e_ctx;
    e.base = base;
    e.caster = caster;
    e.player = PlayerOf(base, caster);
    e.scale = scale;
    e.active = scale < 1.0f;
    Dispatch(e);
    if (e.scale > 0.0f) {
      scale = e.scale;
      ctx.f1.f64 = scale;
    }
  }
  __imp__rex_SpellSlowTime_SetWorldScale(ctx, base);
}

// Every ragdoll fall's start: r3 = the entity, r4 = the action, r5 = fully floppy. Its four
// callers are the knockdown action's start (and the co-op replica's, through sub_82689FC8), the
// vortex throw, the kicked chicken and the limp-on-event action. TU1 0x82688D40.
namespace {
/// Entities reported down (knocked down / unconscious) and not yet up: each state starts and ends
/// once, however often the game re-enters it.
std::vector<uint32_t> g_floppy;
std::vector<uint32_t> g_unconscious;

bool Enter(std::vector<uint32_t>& set, uint32_t entity) {
  if (std::find(set.begin(), set.end(), entity) != set.end()) {
    return false;
  }
  set.push_back(entity);
  return true;
}

bool Leave(std::vector<uint32_t>& set, uint32_t entity) {
  auto found = std::find(set.begin(), set.end(), entity);
  if (found == set.end()) {
    return false;
  }
  set.erase(found);
  return true;
}
}  // namespace

REX_EXTERN(rex_lhRagdoll_GoFloppy) {
  REX_FUNC_PROLOGUE();
  const uint32_t entity = ctx.r3.u32;
  __imp__rex_lhRagdoll_GoFloppy(ctx, base);
  if (entity != 0 && Enter(g_floppy, entity) && AnySubscriber(REFII_EVENT_STATUS_EFFECT)) {
    RaiseStatus(ctx, base, entity, kEffectKnockedDown, true);
  }
}

// The matching ragdoll off: r3 = the entity. Called by those four actions' ends (the knockdown's
// through its base end sub_8268D350). A hero kept down after it is the unconscious state below.
// TU1 0x82688E28.
REX_EXTERN(rex_lhRagdoll_Recover) {
  REX_FUNC_PROLOGUE();
  const uint32_t entity = ctx.r3.u32;
  __imp__rex_lhRagdoll_Recover(ctx, base);
  if (entity != 0 && Leave(g_floppy, entity) && AnySubscriber(REFII_EVENT_STATUS_EFFECT)) {
    RaiseStatus(ctx, base, entity, kEffectKnockedDown, false);
  }
}

// A creature made unconscious (r4 = 1) or woken (0): r3 = the creature component (20, owner at +4).
// The hero's downed state, the knockdown end that keeps a lethally hit hero down, the script's
// SetAsUnconscious; waking plays KnockdownRecover. TU1 0x823A4520.
REX_EXTERN(rex_CECCreature_SetUnconscious) {
  REX_FUNC_PROLOGUE();
  const uint32_t entity = Owner(base, ctx.r3.u32);
  const bool down = (ctx.r4.u32 & 0xFFu) != 0;
  __imp__rex_CECCreature_SetUnconscious(ctx, base);
  const bool changed = entity != 0 && (down ? Enter(g_unconscious, entity) : Leave(g_unconscious, entity));
  if (changed && AnySubscriber(REFII_EVENT_STATUS_EFFECT)) {
    RaiseStatus(ctx, base, entity, kEffectUnconscious, down);
  }
}

// A hollow man crumbling to bones (r4 = 1) or rising again (0): r3 = the crumble action, its owner
// at the weak ref +4. TU1 0x82770A00.
REX_EXTERN(rex_HollowMan_SetCrumbled) {
  REX_FUNC_PROLOGUE();
  const uint32_t action = ctx.r3.u32;
  const bool crumbled = (ctx.r4.u32 & 0xFFu) != 0;
  const uint32_t entity = action != 0 ? Deref(ctx, base, action + 4u) : 0u;
  __imp__rex_HollowMan_SetCrumbled(ctx, base);
  if (entity != 0 && AnySubscriber(REFII_EVENT_STATUS_EFFECT)) {
    RaiseStatus(ctx, base, entity, kEffectCollapsed, crumbled);
  }
}

// Raise Dead binding a minion to its summoner: r3 = the summoner, r4 = the summoned entity (its
// CECDeadRiser, 207, takes the summoner's weak ref at +60 and the lifetime at +20). The network
// replica path binds through it too. TU1 0x82756F28.
REX_EXTERN(rex_SpellRaiseDead_BindSummoner) {
  REX_FUNC_PROLOGUE();
  const uint32_t summoner = ctx.r3.u32;
  const uint32_t summon = ctx.r4.u32;
  __imp__rex_SpellRaiseDead_BindSummoner(ctx, base);
  const auto in_world = [base](uint32_t entity) { return entity != 0 && (Load8(base, entity + 0x90u) & 0x40u) != 0; };
  if (in_world(summoner) && in_world(summon) && HasComponent(base, summon, kDeadRiserComponent) &&
      AnySubscriber(REFII_EVENT_SUMMONED)) {
    RaiseSummoned(ctx, base, summoner, summon, 1);
  }
}

// One Blades sword: r3 = the sword object (its SpellShotDamoclesSword entity's weak ref at +0 once
// made), r4 = the sword set, r5 = the caster (the set's weak ref, resolved by the caller), r6 the
// target. TU1 0x825A6198.
REX_EXTERN(rex_SpellDamoclesSword_Create) {
  REX_FUNC_PROLOGUE();
  const uint32_t sword = ctx.r3.u32;
  const uint32_t caster = ctx.r5.u32;
  __imp__rex_SpellDamoclesSword_Create(ctx, base);
  if (sword != 0 && AnySubscriber(REFII_EVENT_SUMMONED)) {
    const uint32_t entity = Deref(ctx, base, sword);
    if (entity != 0) {
      RaiseSummoned(ctx, base, caster, entity, 2);
    }
  }
}

void refii::vfs::ForgetDownStates(uint32_t entity) {
  Leave(g_floppy, entity);
  Leave(g_unconscious, entity);
}

void refii::vfs::ResetTimeOfDayBaseline() {
  g_last_hour = -1;
  g_last_day = -1;
}

// Raised by co-op code outside this file (coop_world_hooks.cpp, mod_lua_natives.cpp).
extern "C" void refii_mod_raise_collectable_taken(PPCContext* ctx, uint8_t* base, int32_t player, uint32_t entity,
                                                  uint32_t record, int32_t stays) {
  using namespace refii::mods;
  if (ctx == nullptr || !AnySubscriber(REFII_EVENT_COLLECTABLE_TAKEN)) {
    return;
  }
  CollectableTakenEvent e{};
  PPCContext e_ctx = *ctx;
  e.ctx = &e_ctx;
  e.base = base;
  e.player = player;
  e.entity = entity;
  e.record = record;
  e.stays = stays != 0;
  Dispatch(e);
}

/// The answer the handlers gave for the partner: -1 ask them, 0 no, 1 yes.
extern "C" int32_t refii_mod_raise_partner_consent(PPCContext* ctx, uint8_t* base, uint32_t ticket, int32_t kind,
                                                   int32_t asker, int32_t detail) {
  using namespace refii::mods;
  if (ctx == nullptr || !AnySubscriber(REFII_EVENT_PARTNER_CONSENT)) {
    return -1;
  }
  PartnerConsentEvent e{};
  PPCContext e_ctx = *ctx;
  e.ctx = &e_ctx;
  e.base = base;
  e.ticket = ticket;
  e.kind = kind;
  e.asker = asker;
  e.detail = detail;
  e.answer = -1;
  Dispatch(e);
  return e.answer;
}

// THE OPINION SCOPE FOR LUA (Refii._opinion_enter / _opinion_leave, the core Lua's wrap of the
// OpinionReaction natives): enters player two's scope for `villager` when the call concerns
// hero two — `hero` when the caller names one, else the hero the villager notices (the nearer).
// Returns 1 when a scope was entered; every 1 must be matched by a leave.
namespace {
thread_local std::vector<std::unique_ptr<OpinionHeroScope>> t_lua_opinion_scopes;
}

extern "C" int32_t refii_mod_opinion_enter(PPCContext* ctx, uint8_t* base, uint32_t villager, uint32_t hero) {
  if (ctx == nullptr || villager == 0u) {
    return 0;
  }
  const uint32_t opinion = refii_mod_component(base, villager, 47u);
  if (opinion == 0u) {
    return 0;
  }
  const uint32_t who = hero != 0u ? hero : OpinionNearerHero(*ctx, base, villager);
  auto scope = std::make_unique<OpinionHeroScope>(*ctx, base, opinion, who);
  if (!scope->active()) {
    return 0;
  }
  t_lua_opinion_scopes.push_back(std::move(scope));
  return 1;
}

extern "C" void refii_mod_opinion_leave(void) {
  if (!t_lua_opinion_scopes.empty()) {
    t_lua_opinion_scopes.pop_back();
  }
}
