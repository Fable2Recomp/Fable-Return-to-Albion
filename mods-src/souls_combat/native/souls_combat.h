// Souls Combat — the shared state of the native parts and the knobs, tuned in a run.

#pragma once

#include <cstdint>

#include "core/mod_values.h"

namespace souls_combat {

/// One player's stamina in points, 0..max. It may dip below zero by up to kFloor: the swing that
/// empties the pool still lands, and the next one waits for the refill.
struct Pool {
  float value = 100.0f;
  float max = 100.0f;
  float since_spend = 99.0f;
};

extern Pool g_pools[2];

constexpr float kBaseStamina = 100.0f;
constexpr float kSwingCost = 18.0f;
constexpr float kRollCost = 22.0f;
/// Stamina a blocked hit costs per point of its damage.
constexpr float kBlockCostPerDamage = 1.2f;
constexpr float kFloor = -25.0f;
constexpr float kRegenDelay = 0.7f;
constexpr float kRegenPerSecond = 55.0f;

/// The Stamina ability (stamina_ability.cpp): player 1's level, 0..kStaminaMaxLevel.
extern int g_stamina_level;
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

/// Each player's live committed strike action (the clone the action manager runs), 0 when none:
/// recorded by the CommitSwing hook (stamina.cpp), consumed by commitment.cpp.
extern uint32_t g_committed_strike[2];

/// A tunable: the mod's settings.json value under `key` (pushed by the host, changeable live with
/// the debug channel's `mod-setting`), or the constant it replaces.
inline float Knob(const char* key, float fallback) { return refii::mods::Setting(key, fallback); }

/// 0 for player 1's hero, 1 for player 2's, -1 for anything else.
int PlayerOf(uint8_t* base, uint32_t entity);

void Spend(int player, float cost);

}  // namespace souls_combat
