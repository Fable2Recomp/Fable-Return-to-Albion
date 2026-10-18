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
// the game's own price table (refii_mod_ability_cost, Toughness's table, type 3: Stamina is
// priced and paced as Toughness is), affordable when (int)Strength + (int)General covers it,
// spent through the game's own Spend (refii_mod_spend_experience), which takes a shortfall out
// of General and raises the guest's own XP-changed event.
//
// An unlearn is the shipped one too (TU1 sub_8246D510): the level drops by one and half the
// cost of the level given up comes back — into GENERAL, through the game's own Modify
// (refii_mod_give_experience), outside the lifetime-earned total. Y on the row hands this file
// souls_combat.refund_stamina as an incrementing count.

#include "core/refii_mod.h"
#include "souls_combat.h"

namespace souls_combat {

int g_stamina_level = 0;

}  // namespace souls_combat

using namespace souls_combat;

namespace {

constexpr int kToughnessType = 3;
constexpr int kStrengthPool = 1;
constexpr int kGeneralPool = 0;
constexpr const char* kSaveKey = "SoulsStamina";

float g_seen_buy = 0.0f;
float g_seen_refund = 0.0f;

int CostOf(PPCContext* ctx, uint8_t* base, uint32_t hero, int level) {
  return refii_mod_ability_cost(ctx, base, hero, kToughnessType, level);
}

/// Writes the level into the save's RefiiModSave table.
void Record(PPCContext* ctx, uint8_t* base) {
  if (refii_mod_save_set_int(ctx, base, kSaveKey, g_stamina_level) == 0) {
    REXLOG_WARN("Souls Combat: Stamina {} NOT recorded for the save: the game Lua refused it", g_stamina_level);
  }
}

void Buy(PPCContext* ctx, uint8_t* base, uint32_t hero) {
  if (g_stamina_level >= kStaminaMaxLevel) {
    REXLOG_INFO("Souls Combat: Stamina is already at level {}", g_stamina_level);
    return;
  }

  float strength = 0.0f;
  float general = 0.0f;
  if (refii_mod_experience(base, hero, kStrengthPool, &strength) == 0 ||
      refii_mod_experience(base, hero, kGeneralPool, &general) == 0) {
    REXLOG_WARN("Souls Combat: no stats or experience component on the hero; Stamina not bought");
    return;
  }

  const int cost = CostOf(ctx, base, hero, g_stamina_level + 1);
  const int wallet = static_cast<int>(strength) + static_cast<int>(general);
  if (cost <= 0 || wallet < cost) {
    REXLOG_INFO("Souls Combat: Stamina {} costs {}, the hero has {}", g_stamina_level + 1, cost, wallet);
    return;
  }

  refii_mod_spend_experience(ctx, base, hero, kStrengthPool, static_cast<float>(cost));
  ++g_stamina_level;
  Record(ctx, base);
  REXLOG_INFO("Souls Combat: bought Stamina {} for {} experience", g_stamina_level, cost);
}

void Refund(PPCContext* ctx, uint8_t* base, uint32_t hero) {
  if (g_stamina_level <= 0) {
    return;
  }

  const int cost = CostOf(ctx, base, hero, g_stamina_level);
  if (cost <= 0) {
    return;  // no price table (no stats component): nothing to give back, nothing to unlearn
  }
  const int refund = cost / 2;
  if (refii_mod_give_experience(ctx, base, hero, kGeneralPool, static_cast<float>(refund)) == 0) {
    return;
  }

  --g_stamina_level;
  Record(ctx, base);
  REXLOG_INFO("Souls Combat: unlearned Stamina {}, {} experience back to General", g_stamina_level + 1, refund);
}

/// The level lives in the save. The Lua and SaveLoaded events say the save's table changed; the
/// read itself waits for the next tick with the hero present, where the game VM is up and a
/// chunk can run (refii_mod_save_get_int goes through the game's VM pointer).
bool g_reread = true;

void SaveChanged(refii::mods::LuaEvent& e) {
  (void)e;
  g_reread = true;
}
REFII_MOD_ON(Lua, SaveChanged);

void SaveLoaded(refii::mods::SaveLoadedEvent& e) { SaveChanged(e); }
REFII_MOD_ON(SaveLoaded, SaveLoaded);

void OnTick(refii::mods::TickEvent& e) {
  const uint32_t hero = refii_mod_player_hero(e.base, 1);
  if (hero == 0) {
    return;
  }

  if (g_reread) {
    g_reread = false;
    int32_t level = 0;
    g_stamina_level = refii_mod_save_get_int(e.ctx, e.base, kSaveKey, 0, kStaminaMaxLevel, &level) != 0 ? level : 0;
  }

  const float buy = refii::mods::Setting("souls_combat.buy_stamina", 0.0f);
  if (buy != g_seen_buy) {
    g_seen_buy = buy;
    Buy(e.ctx, e.base, hero);
  }

  const float refund = refii::mods::Setting("souls_combat.refund_stamina", 0.0f);
  if (refund != g_seen_refund) {
    g_seen_refund = refund;
    Refund(e.ctx, e.base, hero);
  }

  refii::mods::Publish("souls_combat.stamina_level", static_cast<float>(g_stamina_level));
  refii::mods::Publish("souls_combat.stamina_next",
                       MaxStamina(g_stamina_level < kStaminaMaxLevel ? g_stamina_level + 1 : g_stamina_level));
  if (g_stamina_level < kStaminaMaxLevel) {
    refii::mods::Publish("souls_combat.stamina_cost", static_cast<float>(CostOf(e.ctx, e.base, hero, g_stamina_level + 1)));
  }
  // What Y would hand back: half the level held, as the shipped unlearn refunds.
  refii::mods::Publish("souls_combat.stamina_refund",
                       g_stamina_level > 0 ? static_cast<float>(CostOf(e.ctx, e.base, hero, g_stamina_level) / 2) : 0.0f);
}
REFII_MOD_ON(Tick, OnTick);

}  // namespace
