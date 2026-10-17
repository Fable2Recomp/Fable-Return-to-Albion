// Souls Combat — the Stamina ability: a sixth Strength ability, bought with Strength experience,
// that raises player 1's maximum stamina.
//
// MOD STATE, NOT A GUEST ABILITY. The guest's ability types are a fixed 1..14 enum written into
// its switches, so the level lives here and in the save (RefiiModSave.SoulsStamina, which refii's
// init preamble persists through the game's own GetSaveTable / LoadScriptSave). The row on the
// Strength page is declared in ui/abilities.json; pressing A on it hands this file the setting
// souls_combat.buy_stamina as an incrementing count.
//
// A purchase is the shipped one, step for step (TU1 sub_8246C970): the next level's cost from
// rex_CECStats_AbilityExperienceCost (0x824E49D0 / disc 0x824AB140; r4 = ability type, r5 = level,
// the cost in r3 — Toughness's table, type 3: Stamina is priced and paced as Toughness is), affordable
// when (int)Strength + (int)General covers it, spent through rex_CECExperience_Spend (0x8256E2F0 /
// disc 0x8251C920; r3 = the experience component 164, r4 = the pool, f1 = the cost), which takes
// a shortfall out of General and raises the guest's own XP-changed event.
//
// An unlearn is the shipped one too (TU1 sub_8246D510): the level drops by one and half the
// cost of the level given up comes back — into GENERAL, through rex_CECExperience_Modify
// (0x8256DA78 / disc 0x8251C0A8) with r4 = 0, f1 = the refund and r6 = r7 = r8 = 0 (0x8246d95c..
// 0x8246d980), the zeroes keeping it out of the lifetime-earned total. Y on the row hands this
// file souls_combat.refund_stamina as an incrementing count.

#include <cstdio>

#include "core/refii_mod.h"
#include "souls_combat.h"

REFII_MOD_ORIGINAL(rex_CECStats_AbilityExperienceCost);
REFII_MOD_ORIGINAL(rex_CECExperience_Spend);
REFII_MOD_ORIGINAL(rex_CECExperience_Modify);

namespace souls_combat {

int g_stamina_level = 0;

}  // namespace souls_combat

using namespace souls_combat;

namespace {

constexpr uint32_t kStatsComponent = 57;
constexpr uint32_t kExperienceComponent = 164;
constexpr uint32_t kToughnessType = 3;
constexpr uint32_t kStrengthPool = 1;
constexpr uint32_t kGeneralPool = 0;

uint32_t g_seen_generation = 0xFFFFFFFFu;
float g_seen_buy = 0.0f;
float g_seen_refund = 0.0f;

int CostOf(PPCContext* ctx, uint8_t* base, uint32_t stats, int level) {
  PPCContext work = *ctx;
  work.r3.u32 = stats;
  work.r4.u32 = kToughnessType;
  work.r5.u32 = static_cast<uint32_t>(level);
  __imp__rex_CECStats_AbilityExperienceCost(work, base);
  return static_cast<int32_t>(work.r3.u32);
}

/// Writes the level into the save's RefiiModSave table.
void Record(PPCContext* ctx, uint8_t* base);

void Buy(PPCContext* ctx, uint8_t* base, uint32_t hero) {
  if (g_stamina_level >= kStaminaMaxLevel) {
    REXLOG_INFO("Souls Combat: Stamina is already at level {}", g_stamina_level);
    return;
  }

  const uint32_t stats = refii_mod_component(base, hero, kStatsComponent);
  const uint32_t experience = refii_mod_component(base, hero, kExperienceComponent);
  const uint32_t pools = experience != 0 ? refii::mods::LoadU32(base, experience + 0x10u) : 0u;
  if (stats == 0 || pools == 0) {
    REXLOG_WARN("Souls Combat: no stats or experience component on the hero; Stamina not bought");
    return;
  }

  const int cost = CostOf(ctx, base, stats, g_stamina_level + 1);
  const int wallet = static_cast<int>(refii::mods::LoadF32(base, pools + 4u * kStrengthPool)) +
                     static_cast<int>(refii::mods::LoadF32(base, pools + 4u * kGeneralPool));
  if (cost <= 0 || wallet < cost) {
    REXLOG_INFO("Souls Combat: Stamina {} costs {}, the hero has {}", g_stamina_level + 1, cost,
                wallet);
    return;
  }

  PPCContext work = *ctx;
  work.r3.u32 = experience;
  work.r4.u32 = kStrengthPool;
  work.f1.f64 = static_cast<double>(cost);
  __imp__rex_CECExperience_Spend(work, base);

  ++g_stamina_level;
  Record(ctx, base);
  REXLOG_INFO("Souls Combat: bought Stamina {} for {} experience", g_stamina_level, cost);
}

void Record(PPCContext* ctx, uint8_t* base) {
  char chunk[96];
  std::snprintf(chunk, sizeof(chunk), "RefiiModSave = RefiiModSave or {} RefiiModSave.SoulsStamina = %d",
                g_stamina_level);
  if (refii_mod_run_game_lua(ctx, base, chunk) == 0) {
    REXLOG_WARN("Souls Combat: Stamina {} NOT recorded for the save: the game Lua refused it",
                g_stamina_level);
  }
}

void Refund(PPCContext* ctx, uint8_t* base, uint32_t hero) {
  if (g_stamina_level <= 0) {
    return;
  }

  const uint32_t stats = refii_mod_component(base, hero, kStatsComponent);
  const uint32_t experience = refii_mod_component(base, hero, kExperienceComponent);
  if (stats == 0 || experience == 0) {
    return;
  }

  const int refund = CostOf(ctx, base, stats, g_stamina_level) / 2;
  PPCContext work = *ctx;
  work.r3.u32 = experience;
  work.r4.u32 = kGeneralPool;
  work.f1.f64 = static_cast<double>(refund);
  work.r6.u64 = 0;
  work.r7.u64 = 0;
  work.r8.u64 = 0;
  __imp__rex_CECExperience_Modify(work, base);

  --g_stamina_level;
  Record(ctx, base);
  REXLOG_INFO("Souls Combat: unlearned Stamina {}, {} experience back to General", g_stamina_level + 1,
              refund);
}

void Tick(PPCContext* ctx, uint8_t* base, float seconds) {
  (void)seconds;
  const uint32_t hero = refii_mod_player_hero(base, 1);
  if (hero == 0) {
    return;
  }

  // The level lives in the save: re-read it whenever the game's Lua state was rebuilt or loaded.
  const uint32_t generation = refii_mod_lua_generation();
  if (generation != g_seen_generation) {
    g_seen_generation = generation;
    int32_t level = 0;
    g_stamina_level = refii_mod_game_lua_int(
                          ctx, base, "RefiiModSave and RefiiModSave.SoulsStamina or 0", 0,
                          kStaminaMaxLevel, &level) != 0
                          ? level
                          : 0;
  }

  const float buy = refii::mods::Setting("souls_combat.buy_stamina", 0.0f);
  if (buy != g_seen_buy) {
    g_seen_buy = buy;
    Buy(ctx, base, hero);
  }

  const float refund = refii::mods::Setting("souls_combat.refund_stamina", 0.0f);
  if (refund != g_seen_refund) {
    g_seen_refund = refund;
    Refund(ctx, base, hero);
  }

  refii::mods::Publish("souls_combat.stamina_level", static_cast<float>(g_stamina_level));
  refii::mods::Publish("souls_combat.stamina_next",
                       MaxStamina(g_stamina_level < kStaminaMaxLevel ? g_stamina_level + 1
                                                                     : g_stamina_level));
  if (const uint32_t stats = refii_mod_component(base, hero, kStatsComponent)) {
    if (g_stamina_level < kStaminaMaxLevel) {
      refii::mods::Publish("souls_combat.stamina_cost",
                           static_cast<float>(CostOf(ctx, base, stats, g_stamina_level + 1)));
    }
    // What Y would hand back: half the level held, as the shipped unlearn refunds.
    refii::mods::Publish("souls_combat.stamina_refund",
                         g_stamina_level > 0
                             ? static_cast<float>(CostOf(ctx, base, stats, g_stamina_level) / 2)
                             : 0.0f);
  }
}

REFII_MOD_ON_TICK(Tick);

}  // namespace
