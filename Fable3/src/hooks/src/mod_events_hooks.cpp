// refii - Fable 3's mod events: the gameplay seams, each read where the game decides, raised to the
// mods' Lua and Godot scripts with the catalog's kinds and field names (Godot/src/Engine/mods/
// Engine_ModApi.cs). Most report from the next tick (mod_lua_hooks.cpp QueueEvent). Gold, renown, an
// ability level, fast travel and a quest's start are DECIDED inside the seam before the game applies them
// (modlua::Decide), as the catalog states them: cancelled, or with their writable field rewritten.
//
// Every address is the TU3 IDB's (the disc's is bound by name, Fable3/versions/Fable3); each was
// matched to the disc's by instruction sequence, the four whose bodies differ by an identical
// prologue. Every offset names its base. A component's +4 is its owner entity (OnHeroDeath
// `lwz r10,4(r3)` then the owner's +0x94 live test, 0x8262ab70). A smart pointer's +4 is the block
// whose first word is the object (lhSmartPtr_Deref).

#include <cstdarg>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <string>

#include <rex/ppc.h>

#include "core/payload_bridge.h"
#include "mod_lua.h"
#include "../../vfs/include/fable3_guest_build.h"

REX_EXTERN(__imp__rex_CECCreature_Die);
REX_EXTERN(__imp__rex_CECHealth_OnHeroDeath);
REX_EXTERN(__imp__rex_CECHealth_Modify);
REX_EXTERN(rex_CECHealth_GetMax);
REX_EXTERN(__imp__rex_CECMoney_Modify);
REX_EXTERN(__imp__rex_CECStats_ApplyRenown);
REX_EXTERN(__imp__rex_CECStats_GrantGuildSeals);
REX_EXTERN(__imp__rex_CECStats_SpendGuildSeals);
REX_EXTERN(__imp__rex_CECStats_RecomputeGuildSeals);
REX_EXTERN(__imp__rex_CECStats_SetAbilityLevel);
REX_EXTERN(__imp__rex_CECTreasury_Modify);
REX_EXTERN(__imp__rex_CECQuestTracker_SetAsActive);
REX_EXTERN(__imp__rex_CECQuestTracker_SetObjectiveTagSilently);
REX_EXTERN(__imp__rex_CECQuestTracker_OnQuestEnded);
REX_EXTERN(__imp__rex_WorldMap_TravelToStop);
REX_EXTERN(__imp__rex_TownDemand_Accept);
REX_EXTERN(__imp__rex_TownDemand_Reject);

namespace {

namespace modlua = refii::fable3::modlua;

// The catalog's kinds (Fable2/src/vfs/include/core/refii_mod_events.h).
constexpr int32_t kAbilityLevel = 31;
constexpr int32_t kEntityDied = 21;
constexpr int32_t kHeroDowned = 22;
constexpr int32_t kGold = 32;
constexpr int32_t kLevelLoaded = 24;
constexpr int32_t kLevelUnloading = 25;
constexpr int32_t kQuestStarted = 35;
constexpr int32_t kQuestEnded = 36;
constexpr int32_t kQuestObjective = 37;
constexpr int32_t kRenown = 42;
constexpr int32_t kFastTravel = 56;
constexpr int32_t kHealthChanged = 101;
constexpr int32_t kGuildSeals = 116;
constexpr int32_t kTreasury = 117;
constexpr int32_t kKingdomDecision = 118;

bool Plausible(uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; }

uint32_t LoadU32(const uint8_t* base, uint32_t at) {
  uint32_t value;
  std::memcpy(&value, base + at, sizeof(value));
  return __builtin_bswap32(value);
}

uint32_t Word(const uint8_t* base, uint32_t at) { return Plausible(at) ? LoadU32(base, at) : 0u; }

float Float(const uint8_t* base, uint32_t at) {
  const uint32_t bits = Word(base, at);
  float value;
  std::memcpy(&value, &bits, sizeof(value));
  return value;
}

/// A guest C string, bounded, as a Lua literal's body.
std::string Text(const uint8_t* base, uint32_t chars) {
  if (!Plausible(chars)) {
    return {};
  }
  size_t n = 0;
  while (n < 256u && base[chars + n] != 0) {
    ++n;
  }
  return modlua::Quoted(std::string_view(reinterpret_cast<const char*>(base + chars), n));
}

/// A CCharString's text: the string is one word, its rep's +0 the characters (sub_82225850's copy).
std::string CharString(const uint8_t* base, uint32_t string) {
  const uint32_t rep = Word(base, string);
  return Plausible(rep) ? Text(base, Word(base, rep)) : std::string();
}

/// An entity that passes the guest's own live test (`*(u8*)(e+0x94) & 0x40`, IsPlayer 0x8220926c).
bool Live(const uint8_t* base, uint32_t entity) { return Plausible(entity) && (base[entity + 0x94u] & 0x40u) != 0u; }

uint32_t Handle(const uint8_t* base, uint32_t entity) {
  return Live(base, entity) ? refii_bridge_mod_handle(entity) : 0u;
}

std::string Fields(const char* format, ...) __attribute__((format(printf, 1, 2)));
std::string Fields(const char* format, ...) {
  char text[768];
  va_list args;
  va_start(args, format);
  const int n = std::vsnprintf(text, sizeof(text), format, args);
  va_end(args);
  return n < 0 ? std::string() : std::string(text, n < static_cast<int>(sizeof(text)) ? n : sizeof(text) - 1);
}

/// The quests are the game's; their hero is the local player's (player 1's).
std::string QuestHero(const uint8_t* base) {
  return Fields("hero=%u,player=1", Handle(base, modlua::Hero(base, 1)));
}

/// The guild seals a stats component holds unspent: Stats.GetNumberOfGuildSeals (0x823DA230:
/// `lwz r11,0x448(r3); lwz r10,0x444(r3); add`) — earned from renown, and granted.
int32_t SealsHeld(const uint8_t* base, uint32_t stats) {
  return static_cast<int32_t>(Word(base, stats + 0x444u) + Word(base, stats + 0x448u));
}

/// The grant and spend run the recompute themselves; seals earned from renown are the recompute
/// alone, outside both.
int g_seal_depth = 0;

void RaiseSeals(const uint8_t* base, uint32_t stats, int32_t before, const char* source) {
  const int32_t after = SealsHeld(base, stats);
  if (after == before || !modlua::Wants(kGuildSeals)) {
    return;
  }
  const uint32_t entity = Word(base, stats + 4u);
  modlua::QueueEvent(kGuildSeals, Fields("entity=%u,player=%d,delta=%d,seals=%d,source='%s'", Handle(base, entity),
                                         modlua::PlayerOf(base, entity), after - before, after, source));
}

}  // namespace

// entity_died. CECCreature_Die (c, killer, category, hit): runs once, gated on the alive byte
// c+0x18 and c+0x28 != 8 (`lwz r9,0x28(r3); cmpwi r9,8; beq; lbz r11,0x18(r3); beq; stb 0,0x18(r3)`,
// 0x826771cc..0x826771fc); the creature is c+4.
REX_EXTERN(rex_CECCreature_Die) {
  const uint32_t creature = ctx.r3.u32;
  const uint32_t killer = ctx.r4.u32;
  const bool dies = Plausible(creature) && base[creature + 0x18u] != 0u && Word(base, creature + 0x28u) != 8u;
  const uint32_t entity = dies ? Word(base, creature + 4u) : 0u;
  if (dies && modlua::Wants(kEntityDied)) {
    modlua::QueueEvent(kEntityDied, Fields("entity=%u,player=%d,killer=%u,killer_player=%d", Handle(base, entity),
                                           modlua::PlayerOf(base, entity), Handle(base, killer),
                                           modlua::PlayerOf(base, killer)));
  }
  __imp__rex_CECCreature_Die(ctx, base);
}

// hero_downed. CECHealth_OnHeroDeath (h, killer): the health dispatcher's call for a hero at no
// health (0x8262A560 floors it at 1.0 first). A resurrection potion revives inside the call
// (Modify(h, GetMax(h)) and an early return), so the hero is down only while h+0x14 is still at or
// below the floor after it.
REX_EXTERN(rex_CECHealth_OnHeroDeath) {
  const uint32_t health = ctx.r3.u32;
  const uint32_t killer = ctx.r4.u32;
  __imp__rex_CECHealth_OnHeroDeath(ctx, base);
  if (!Plausible(health) || Float(base, health + 0x14u) > 1.0f || !modlua::Wants(kHeroDowned)) {
    return;
  }
  const uint32_t hero = Word(base, health + 4u);
  modlua::QueueEvent(kHeroDowned, Fields("hero=%u,player=%d,killer=%u,killer_player=%d", Handle(base, hero),
                                         modlua::PlayerOf(base, hero), Handle(base, killer),
                                         modlua::PlayerOf(base, killer)));
}

// health_changed. CECHealth_Modify (h, -, f1 delta, r5 attacker, r6 hit): health h+0x14
// (`lfs f29,0x14(r3)` at 0x82627a08), clamped to 0..max, the death dispatch inside the call, so
// the value after already carries the hero's 1.0 floor and a potion's revive. Raised when it moved.
REX_EXTERN(rex_CECHealth_Modify) {
  const uint32_t health = ctx.r3.u32;
  const uint32_t attacker = ctx.r5.u32;
  const float before = Plausible(health) ? Float(base, health + 0x14u) : 0.0f;
  const PPCContext entry = ctx;
  __imp__rex_CECHealth_Modify(ctx, base);
  if (!Plausible(health) || !modlua::Wants(kHealthChanged)) {
    return;
  }
  const float after = Float(base, health + 0x14u);
  if (after == before) {
    return;
  }
  // Health.GetMax (h) -> f1: a leaf (`lfs f0,0x20(r3); lfs f13,0x1C(r3); fadds`, clamped by +0x24).
  PPCContext max = entry;
  max.r1.u32 = (entry.r1.u32 - 0x100u) & ~0xFu;
  max.r3.u32 = health;
  rex_CECHealth_GetMax(max, base);
  const uint32_t entity = Word(base, health + 4u);
  modlua::QueueEvent(kHealthChanged,
                     Fields("entity=%u,player=%d,delta=%.9g,health=%.9g,max=%.9g,source=%u", Handle(base, entity),
                            modlua::PlayerOf(base, entity), static_cast<double>(after - before),
                            static_cast<double>(after), max.f1.f64, Handle(base, attacker)));
}

/// A Lua or Godot number as an int32: NaN 0, the range clamped (a raw cast is undefined outside it).
static int32_t ClampInt(double value) {
  if (!(value == value)) {
    return 0;
  }
  return value >= 2147483647.0 ? 2147483647 : value <= -2147483648.0 ? (-2147483647 - 1) : static_cast<int32_t>(value);
}

// gold. The purse's one writer (purse, delta, type, alert, notify, notify, replay): purse+0xC +=
// delta clamped to 0..999999999 (`lwz r26,0xC(r3)` ... `stw r23,0xC(r3)`, 0x823d26c4..0x823d2700),
// and a zero delta changes nothing (`cmpwi r4,0; beq`, 0x823d26c0). Decided before it: a cancel hands
// the writer zero, a written delta replaces r4. r9 set is a replay of the other machine's change
// (the handler TU3 sub_823D3438), decided there.
REX_EXTERN(rex_CECMoney_Modify) {
  const uint32_t purse = ctx.r3.u32;
  const bool replay = (ctx.r9.u32 & 0xFFu) != 0u;
  const int32_t delta = static_cast<int32_t>(ctx.r4.u32);
  if (Plausible(purse) && !replay && delta != 0 && modlua::Wants(kGold)) {
    const uint32_t entity = Word(base, purse + 4u);
    const modlua::Verdict verdict = modlua::Decide(
        ctx, base, kGold,
        Fields("entity=%u,player=%d,gold=%d,delta=%d", Handle(base, entity), modlua::PlayerOf(base, entity),
               static_cast<int32_t>(Word(base, purse + 0xCu)), delta));
    double written = 0.0;
    if (verdict.cancelled) {
      ctx.r4.u64 = 0u;
    } else if (verdict.Read("delta", written)) {
      ctx.r4.s64 = ClampInt(written);
    }
  }
  __imp__rex_CECMoney_Modify(ctx, base);
}

// renown. The renown applier (stats, f1 delta, r5 source, r6 follower group): what every per-hero
// change funnels into — ModifyRenown's per-group recursion and the partner's share both call it —
// gated on the world byte `*(*(*(owner+8)+4)+263)` (0x823d9ca4). Renown is followers in Fable 3.
// Decided before the applier: a cancel hands it a zero delta, a written delta replaces f1.
REX_EXTERN(rex_CECStats_ApplyRenown) {
  const uint32_t stats = ctx.r3.u32;
  const double delta = ctx.f1.f64;
  const uint32_t entity = Plausible(stats) ? Word(base, stats + 4u) : 0u;
  const uint32_t world = Word(base, Word(base, entity + 8u) + 4u);
  if (delta != 0.0 && Plausible(entity) && Plausible(world) && base[world + 263u] != 0u && modlua::Wants(kRenown)) {
    const modlua::Verdict verdict = modlua::Decide(
        ctx, base, kRenown,
        Fields("entity=%u,player=%d,delta=%.9g", Handle(base, entity), modlua::PlayerOf(base, entity), delta));
    double written = 0.0;
    if (verdict.cancelled) {
      ctx.f1.f64 = 0.0;
    } else if (verdict.Read("delta", written) && written == written) {
      ctx.f1.f64 = static_cast<double>(static_cast<float>(written));
    }
  }
  __imp__rex_CECStats_ApplyRenown(ctx, base);
}

// guild_seals. Granted (stats, n): stats+0x448 += n floored at 0, then the recompute
// (Lua_Stats_ModifyGuildSeals, 0x823da2c8..0x823da32c). Spent (stats, n): from +0x444 then +0x448
// (Lua_Stats_SpendGuildSeals). The recompute (stats, ...) alone adds +0x444 for each renown
// threshold passed: seals earned.
REX_EXTERN(rex_CECStats_GrantGuildSeals) {
  const uint32_t stats = ctx.r3.u32;
  const int32_t before = Plausible(stats) ? SealsHeld(base, stats) : 0;
  ++g_seal_depth;
  __imp__rex_CECStats_GrantGuildSeals(ctx, base);
  --g_seal_depth;
  if (Plausible(stats)) {
    RaiseSeals(base, stats, before, "granted");
  }
}

REX_EXTERN(rex_CECStats_SpendGuildSeals) {
  const uint32_t stats = ctx.r3.u32;
  const int32_t before = Plausible(stats) ? SealsHeld(base, stats) : 0;
  ++g_seal_depth;
  __imp__rex_CECStats_SpendGuildSeals(ctx, base);
  --g_seal_depth;
  if (Plausible(stats)) {
    RaiseSeals(base, stats, before, "spent");
  }
}

REX_EXTERN(rex_CECStats_RecomputeGuildSeals) {
  const uint32_t stats = ctx.r3.u32;
  const int32_t before = Plausible(stats) ? SealsHeld(base, stats) : 0;
  __imp__rex_CECStats_RecomputeGuildSeals(ctx, base);
  if (g_seal_depth == 0 && Plausible(stats)) {
    RaiseSeals(base, stats, before, "earned");
  }
}

// ability_level. Stats.SetHeroAbilityLevel's method (stats, type, level, send): the level at
// stats + 0x90 + 4*type (`addi r10,r21,0x24; slwi r29,r10,2` ... `stwx`, 0x823e6974), clamped to
// the ability's maximum.
// Decided before it, for a level that changes: a cancel hands it the level it has, a written level
// replaces r5.
REX_EXTERN(rex_CECStats_SetAbilityLevel) {
  const uint32_t stats = ctx.r3.u32;
  const uint32_t type = ctx.r4.u32;
  const int32_t level = static_cast<int32_t>(ctx.r5.u32);
  const bool known = Plausible(stats) && type < 64u;
  const int32_t before = known ? static_cast<int32_t>(Word(base, stats + 0x90u + 4u * type)) : 0;
  if (known && level != before && modlua::Wants(kAbilityLevel)) {
    const uint32_t entity = Word(base, stats + 4u);
    const modlua::Verdict verdict = modlua::Decide(
        ctx, base, kAbilityLevel,
        Fields("entity=%u,player=%d,type=%u,level=%d,old_level=%d", Handle(base, entity),
               modlua::PlayerOf(base, entity), type, level, before));
    double written = 0.0;
    if (verdict.cancelled) {
      ctx.r5.s64 = before;
    } else if (verdict.Read("level", written)) {
      ctx.r5.s64 = ClampInt(written);
    }
  }
  __imp__rex_CECStats_SetAbilityLevel(ctx, base);
}

// treasury. The kingdom treasury's writer (t, delta, type, popup): t+0xC += delta clamped to
// ±999999999 (0x823d5938 / 0x823d5968). Treasury.Set writes +0xC itself and is not seen.
REX_EXTERN(rex_CECTreasury_Modify) {
  const uint32_t treasury = ctx.r3.u32;
  const int32_t type = static_cast<int32_t>(ctx.r5.u32);
  const int32_t before = Plausible(treasury) ? static_cast<int32_t>(Word(base, treasury + 0xCu)) : 0;
  __imp__rex_CECTreasury_Modify(ctx, base);
  if (!Plausible(treasury) || !modlua::Wants(kTreasury)) {
    return;
  }
  const int32_t after = static_cast<int32_t>(Word(base, treasury + 0xCu));
  if (after != before) {
    modlua::QueueEvent(kTreasury, Fields("treasury=%d,delta=%d,type=%d", after, after - before, type));
  }
}

// quest_started. QuestTracker.SetAsActive's method (tracker, name, -, make_primary): the record
// found by name gets +13 active and +14 activated (0x8250cd98 / 0x8250cd9c) on every call. Decided
// before it: a cancel leaves the quest as it was (the call not made).
REX_EXTERN(rex_CECQuestTracker_SetAsActive) {
  const std::string quest = Text(base, ctx.r4.u32);
  if (!quest.empty() && modlua::Wants(kQuestStarted)) {
    const modlua::Verdict verdict = modlua::Decide(
        ctx, base, kQuestStarted,
        Fields("quest='%s',%s,make_primary=%s", quest.c_str(), QuestHero(base).c_str(),
               (ctx.r6.u32 & 0xFFu) != 0u ? "true" : "false"));
    if (verdict.cancelled) {
      return;
    }
  }
  __imp__rex_CECQuestTracker_SetAsActive(ctx, base);
}

// quest_objective. (tracker, quest name, objective tag): the tag pushed onto the record's +0x80
// tag list (0x8250f214).
REX_EXTERN(rex_CECQuestTracker_SetObjectiveTagSilently) {
  const std::string quest = Text(base, ctx.r4.u32);
  const std::string tag = Text(base, ctx.r5.u32);
  __imp__rex_CECQuestTracker_SetObjectiveTagSilently(ctx, base);
  if (!quest.empty() && modlua::Wants(kQuestObjective)) {
    modlua::QueueEvent(kQuestObjective,
                       Fields("quest='%s',tag='%s',%s", quest.c_str(), tag.c_str(), QuestHero(base).c_str()));
  }
}

// quest_ended. (tracker, record): reached only from SetAsCompleted (`stb r29,0x15(r3)` before the
// call, 0x8250eaec) and SetAsFailed (`stb r30,0x16(r3)`, 0x8250ed8c); the record's +0 is its name.
REX_EXTERN(rex_CECQuestTracker_OnQuestEnded) {
  const uint32_t record = ctx.r4.u32;
  const std::string quest = Plausible(record) ? CharString(base, record) : std::string();
  const bool completed = Plausible(record) && base[record + 0x15u] != 0u;
  const bool failed = Plausible(record) && base[record + 0x16u] != 0u;
  __imp__rex_CECQuestTracker_OnQuestEnded(ctx, base);
  if (!quest.empty() && modlua::Wants(kQuestEnded)) {
    modlua::QueueEvent(kQuestEnded, Fields("quest='%s',%s,completed=%s,failed=%s", quest.c_str(),
                                           QuestHero(base).c_str(), completed ? "true" : "false",
                                           failed ? "true" : "false"));
  }
}

// fast_travel. (map, CCharString* level, CCharString* stop, paid): both strings handed on to the
// level load (its constructor 0x82755D10 keeps them at +24 / +28); 1 when the travel was issued.
// Decided before it: a cancel refuses it (0), as the game's own refusal does.
REX_EXTERN(rex_WorldMap_TravelToStop) {
  if (modlua::Wants(kFastTravel)) {
    const modlua::Verdict verdict = modlua::Decide(
        ctx, base, kFastTravel,
        Fields("level='%s',stop='%s',paid=%s,player=1", CharString(base, ctx.r4.u32).c_str(),
               CharString(base, ctx.r5.u32).c_str(), (ctx.r6.u32 & 0xFFu) != 0u ? "true" : "false"));
    if (verdict.cancelled) {
      ctx.r3.u64 = 0u;
      return;
    }
  }
  __imp__rex_WorldMap_TravelToStop(ctx, base);
}

// kingdom_decision. A town demand accepted or rejected (demand): its save writer lays out +0 id,
// +12 province, +16 level; 1 when the decision was taken (accept refuses an unaffordable demand).
static void RaiseDecision(const uint8_t* base, uint32_t demand, bool accepted, uint32_t taken) {
  if ((taken & 0xFFu) == 0u || !Plausible(demand) || !modlua::Wants(kKingdomDecision)) {
    return;
  }
  modlua::QueueEvent(kKingdomDecision, Fields("demand=%u,province=%u,level=%u,accepted=%s", Word(base, demand),
                                              Word(base, demand + 12u), Word(base, demand + 16u),
                                              accepted ? "true" : "false"));
}

REX_EXTERN(rex_TownDemand_Accept) {
  const uint32_t demand = ctx.r3.u32;
  __imp__rex_TownDemand_Accept(ctx, base);
  RaiseDecision(base, demand, true, ctx.r3.u32);
}

REX_EXTERN(rex_TownDemand_Reject) {
  const uint32_t demand = ctx.r3.u32;
  __imp__rex_TownDemand_Reject(ctx, base);
  RaiseDecision(base, demand, false, ctx.r3.u32);
}

namespace refii::fable3::modlua {

// level_loaded / level_unloading, from world_hooks.cpp's level manager hooks: the level is
// `*(*(mgr + 0x28) + 4)` (the load stores its holder at mgr+0x28, 0x82350590), its name the
// CCharString at level+0xAC (its constructor's `addi r29,r3,0xAC`, 0x8245c454).
void NoteLevel(const uint8_t* base, uint32_t manager, bool loaded) {
  const int32_t kind = loaded ? kLevelLoaded : kLevelUnloading;
  if (!Plausible(manager) || !Wants(kind)) {
    return;
  }
  const uint32_t level = Word(base, Word(base, manager + 0x28u) + 4u);
  if (!Plausible(level)) {
    return;
  }
  QueueEvent(kind, Fields("level='%s'", CharString(base, level + 0xACu).c_str()));
}

}  // namespace refii::fable3::modlua
