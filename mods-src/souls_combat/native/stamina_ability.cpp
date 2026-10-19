// Souls Combat — the Stamina ability: a sixth Strength ability, bought with Strength experience,
// that raises a player's maximum stamina. Each player has their own level.
//
// MOD STATE, NOT A GUEST ABILITY. The guest's ability types are a fixed 1..14 enum written into
// its switches, so the level is a SAVED player value (souls_combat.stamina_ability, through
// refii_mod_player_save): it goes home with the hero, into the save the game writes that hero
// into, and is the same on both co-op machines. The row on the Strength page is declared in
// ui/abilities.json; pressing A on it hands this file the setting souls_combat.buy_stamina as an
// incrementing count.
//
// WHOSE BOOK: the row acts for the player whose menus are open (refii_mod_menu_player) — player
// two's own book in local co-op and on an online joiner's machine — and the level, cost and
// refund it shows are that player's. A purchase spends that player's hero's experience.
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
//
// SAVES FROM BEFORE: the level used to live in the world save's Lua (RefiiModSave.SoulsStamina).
// A player-one hero with no saved level takes that one, once, after each Lua rebuild or load.

#include "core/refii_mod.h"
#include "souls_combat.h"

using namespace souls_combat;

namespace {

constexpr int kToughnessType = 3;
constexpr int kStrengthPool = 1;
constexpr int kGeneralPool = 0;
/// Where saves from before kept player one's level.
constexpr const char* kLegacyKey = "SoulsStamina";

float g_seen_buy = 0.0f;
float g_seen_refund = 0.0f;

int CostOf(PPCContext* ctx, uint8_t* base, uint32_t hero, int level) {
  return refii_mod_ability_cost(ctx, base, hero, kToughnessType, level);
}

void Buy(PPCContext* ctx, uint8_t* base, int player, uint32_t hero) {
  const int level = StaminaLevel(player);
  if (level >= kStaminaMaxLevel) {
    REXLOG_INFO("Souls Combat: player {} Stamina is already at level {}", player + 1, level);
    return;
  }

  float strength = 0.0f;
  float general = 0.0f;
  if (refii_mod_experience(base, hero, kStrengthPool, &strength) == 0 ||
      refii_mod_experience(base, hero, kGeneralPool, &general) == 0) {
    REXLOG_WARN("Souls Combat: no stats or experience component on player {}'s hero; Stamina not bought", player + 1);
    return;
  }

  const int cost = CostOf(ctx, base, hero, level + 1);
  const int wallet = static_cast<int>(strength) + static_cast<int>(general);
  if (cost <= 0 || wallet < cost) {
    REXLOG_INFO("Souls Combat: Stamina {} costs {}, player {} has {}", level + 1, cost, player + 1, wallet);
    return;
  }

  refii_mod_spend_experience(ctx, base, hero, kStrengthPool, static_cast<float>(cost));
  refii_mod_player_save(player + 1, kStaminaAbilityKey, static_cast<float>(level + 1));
  REXLOG_INFO("Souls Combat: player {} bought Stamina {} for {} experience", player + 1, level + 1, cost);
}

void Refund(PPCContext* ctx, uint8_t* base, int player, uint32_t hero) {
  const int level = StaminaLevel(player);
  if (level <= 0) {
    return;
  }

  const int cost = CostOf(ctx, base, hero, level);
  if (cost <= 0) {
    return;  // no price table (no stats component): nothing to give back, nothing to unlearn
  }
  const int refund = cost / 2;
  if (refii_mod_give_experience(ctx, base, hero, kGeneralPool, static_cast<float>(refund)) == 0) {
    return;
  }

  refii_mod_player_save(player + 1, kStaminaAbilityKey, static_cast<float>(level - 1));
  REXLOG_INFO("Souls Combat: player {} unlearned Stamina {}, {} experience back to General", player + 1, level,
              refund);
}

/// A save from before keeps player one's level in its Lua table. The Lua and SaveLoaded events say
/// the table changed; the read waits for the next tick with the hero present, where the game VM is
/// up and a chunk can run (refii_mod_save_get_int goes through the game's VM pointer).
bool g_reread = true;

void SaveChanged(refii::mods::LuaEvent& e) {
  (void)e;
  g_reread = true;
}
REFII_MOD_ON(Lua, SaveChanged);

void SaveLoaded(refii::mods::SaveLoadedEvent& e) { SaveChanged(e); }
REFII_MOD_ON(SaveLoaded, SaveLoaded);

void OnTick(refii::mods::TickEvent& e) {
  if (refii_mod_player_hero(e.base, 1) == 0) {
    return;
  }

  if (g_reread) {
    g_reread = false;
    int32_t legacy = 0;
    if (refii_mod_player_here(1) != 0 && refii_mod_player_get(1, kStaminaAbilityKey, nullptr) == 0 &&
        refii_mod_save_get_int(e.ctx, e.base, kLegacyKey, 1, kStaminaMaxLevel, &legacy) != 0) {
      refii_mod_player_save(1, kStaminaAbilityKey, static_cast<float>(legacy));
      REXLOG_INFO("Souls Combat: player 1's Stamina {} taken from the save's Lua table", legacy);
    }
  }

  // The book's player: the row's presses act for them and its numbers are theirs.
  const int player = refii_mod_menu_player(e.base) == 2 ? 1 : 0;
  const uint32_t hero = refii_mod_player_hero(e.base, player + 1);

  const float buy = refii::mods::Setting("souls_combat.buy_stamina", 0.0f);
  if (buy != g_seen_buy) {
    g_seen_buy = buy;
    if (hero != 0) {
      Buy(e.ctx, e.base, player, hero);
    }
  }

  const float refund = refii::mods::Setting("souls_combat.refund_stamina", 0.0f);
  if (refund != g_seen_refund) {
    g_seen_refund = refund;
    if (hero != 0) {
      Refund(e.ctx, e.base, player, hero);
    }
  }

  if (hero == 0) {
    return;
  }

  const int level = StaminaLevel(player);
  refii::mods::Publish("souls_combat.stamina_level", static_cast<float>(level));
  refii::mods::Publish("souls_combat.stamina_next", MaxStamina(level < kStaminaMaxLevel ? level + 1 : level));
  if (level < kStaminaMaxLevel) {
    refii::mods::Publish("souls_combat.stamina_cost", static_cast<float>(CostOf(e.ctx, e.base, hero, level + 1)));
  }
  // What Y would hand back: half the level held, as the shipped unlearn refunds.
  refii::mods::Publish("souls_combat.stamina_refund",
                       level > 0 ? static_cast<float>(CostOf(e.ctx, e.base, hero, level) / 2) : 0.0f);
}
REFII_MOD_ON(Tick, OnTick);

}  // namespace
