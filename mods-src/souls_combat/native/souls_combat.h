// Souls Combat — the shared state of the native parts and the knobs, tuned in a run.
//
// Built on the mod API's events (core/refii_mod_events.h): every seam the mod uses is an event,
// so another mod on the same seam needs no chain. The one function still hooked by name is the
// player's look-at writer (lockon.cpp), which no event covers.

#pragma once

#include <cstdint>

#include "core/refii_mod.h"

namespace souls_combat {

/// EACH PLAYER'S STATE LIVES IN THE PLAYER STORE (refii_mod_player_*, core/refii_mod.h), not in
/// arrays here, so it is the same on both machines of an online session: a spend on either reaches
/// the hero's owner, and the refill runs where the hero is played. Keys:
///   souls_combat.stamina        the pool in points, 0..max; it may dip below zero by up to kFloor:
///                               the swing that empties it still lands, and the next one waits
///   souls_combat.stamina_max    the pool's maximum, stated where the hero is played (the HUD's bar)
///   souls_combat.stamina_rest   seconds since the last spend (the refill waits for kRegenDelay)
///   souls_combat.mana / mana_max / mana_rest   the same for mana (mana.cpp)
///   souls_combat.stamina_ability    the Stamina ability's level, SAVED with the hero: the ENGINE
///                               buys and unlearns it from ui/abilities.json (its price, its
///                               refund, saves from before); this mod only reads it
inline constexpr const char* kStaminaKey = "souls_combat.stamina";
inline constexpr const char* kStaminaMaxKey = "souls_combat.stamina_max";
inline constexpr const char* kStaminaRestKey = "souls_combat.stamina_rest";
inline constexpr const char* kStaminaAbilityKey = "souls_combat.stamina_ability";

constexpr float kBaseStamina = 100.0f;
constexpr float kSwingCost = 18.0f;
constexpr float kRollCost = 22.0f;
/// Stamina a blocked hit costs per point of its damage.
constexpr float kBlockCostPerDamage = 1.2f;
constexpr float kFloor = -25.0f;
constexpr float kRegenDelay = 0.7f;
constexpr float kRegenPerSecond = 55.0f;

constexpr int kStaminaMaxLevel = 5;

/// THE HEALTH CURVE, so stamina and mana grow exactly as health does. From globals.gdb:
/// `GUIValues.MaxMaxHealth` (#a8f8dac8) is 950, the length a maxed health bar is drawn against,
/// and Toughness levels 1..5 (AbilityToughnessBase -> AbilityGroupRecords #24876832, Record1..5)
/// state `MaxHealthModifier` 100, 300, 400, 500, 600 — so an untrained hero's 350 is 950 - 600.
constexpr float kHealthCeiling = 950.0f;
constexpr float kToughnessBonus[kStaminaMaxLevel + 1] = {0.0f, 100.0f, 300.0f, 400.0f, 500.0f, 600.0f};
constexpr float kUntrainedHealth = kHealthCeiling - 600.0f;

/// A pool that starts at kBaseStamina and grows as health would: `bonus` health points' worth.
inline float AlongHealthCurve(float bonus) { return kBaseStamina * (kUntrainedHealth + bonus) / kUntrainedHealth; }

/// The fullest any such pool gets (271.4) — what the HUD draws both bars against, so a full
/// pool is exactly as long as a maxed health bar.
constexpr float kBarCap = kBaseStamina * kHealthCeiling / kUntrainedHealth;

/// Maximum stamina at a Stamina ability level: Toughness's own steps.
inline float MaxStamina(int level) {
  return AlongHealthCurve(kToughnessBonus[level < 0 ? 0 : (level > kStaminaMaxLevel ? kStaminaMaxLevel : level)]);
}

/// A player's Stamina ability level, 0..kStaminaMaxLevel, by pool index (0 player 1, 1 player 2).
inline int StaminaLevel(int index) {
  const int level = static_cast<int>(refii_mod_player_value(index + 1, kStaminaAbilityKey, 0.0f));
  return level < 0 ? 0 : (level > kStaminaMaxLevel ? kStaminaMaxLevel : level);
}

/// A player's maximum stamina: their own Stamina level along the health curve.
inline float StaminaMax(int index) { return MaxStamina(StaminaLevel(index)); }

/// A player's stamina now; a player with none stated yet is full.
inline float Stamina(int index) { return refii_mod_player_value(index + 1, kStaminaKey, StaminaMax(index)); }

/// Each player's live committed strike action (the clone the action manager runs), 0 when none:
/// recorded from the MeleeSwing event (stamina.cpp), consumed by commitment.cpp.
extern uint32_t g_committed_strike[2];

/// A tunable: the mod's settings.json value under `key` (pushed by the host, changeable live with
/// the debug channel's `mod-setting`), or the constant it replaces.
inline float Knob(const char* key, float fallback) { return refii::mods::Setting(key, fallback); }

/// The pool index of an event's `player` field (1 or 2): 0 or 1, and -1 for anything else.
inline int Index(int32_t player) { return player == 1 ? 0 : (player == 2 ? 1 : -1); }

/// 0 for player 1's hero, 1 for player 2's, -1 for anything else.
inline int PlayerOf(uint8_t* base, uint32_t entity) { return Index(refii_mod_player_of(base, entity)); }

/// Takes `cost` from a player's pool (by index), on whichever machine the spend happens.
void Spend(int index, float cost);

}  // namespace souls_combat
