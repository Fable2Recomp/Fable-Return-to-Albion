// Souls Combat — mana (FP) for spells.
//
// A pool per player hero. Its maximum grows with the spells the hero has learned, along the same
// curve health takes: from the untrained length to a maxed health bar's, evenly over all forty
// ranks of the eight spells. EVERY SPELL THAT LAUNCHES COSTS kManaPerLevel per power level: a
// release at level L is, in Fable 2, a cascade of slots 1..L each at its own power (the cascade
// decrements its count before each launch, sub_8253ED78), so a full release costs the sum. An
// empty pool refuses the charge; a short one caps the charge at the top slot it can pay for, and
// a lower slot of the cascade is skipped when launching it would leave the top slot unpaid — the
// charged spell always fires. Mana refills slowly after a pause — Fable 2 has no flask to refill
// it with. Published as souls_combat.mana / mana_max (player 1) and mana2 / mana2_max (player 2)
// for the FP bar in ui/hud.json.
//
// THE POOL IS IN THE PLAYER STORE (souls_combat.mana, mana_rest; souls_combat.h), so both co-op
// machines read the same pool: a launch spends on whichever machine it happens, the refill runs
// where the hero is played, and a hero arriving there starts full.
//
// The events (core/refii_mod_events.h):
//   SpellCharge — the B press; cancelling refuses the charge, with no side effects.
//   SpellChargeLevel — the charge time became the top slot (0..4); writing `level` caps the
//     cascade (it launches 0..level).
//   SpellLaunch — one slot about to launch, `cascading` at the lower ones; cancel skips it.
//   SpellLaunched — a spell was really created for a slot; the cost is taken here.
// Learned ranks are the stats component's ability levels for ids 7..14 (the eight spells),
// read with refii_mod_ability_level.

#include "core/refii_mod.h"
#include "souls_combat.h"

using namespace souls_combat;

namespace {

constexpr int kAllRanks = 8 * 5;
constexpr float kManaPerLevel = 12.0f;
constexpr float kManaRegenDelay = 2.5f;
constexpr float kManaRegenPerSecond = 6.0f;
constexpr int kFirstSpell = 7;
constexpr int kLastSpell = 14;
constexpr int kMaxChargeLevel = 5;

constexpr const char* kManaKey = "souls_combat.mana";
constexpr const char* kManaRestKey = "souls_combat.mana_rest";
constexpr float kRestCap = 10.0f;

/// Each player's maximum, as the last tick found it from their learned spells.
float g_max[2] = {0.0f, 0.0f};

/// A player's mana now (by index); none stated yet is full.
float ManaOf(int index) { return refii_mod_player_value(index + 1, kManaKey, g_max[index]); }

float Cost(int level) { return Knob("souls_combat.mana_per_level", kManaPerLevel) * static_cast<float>(level); }

float MaxMana(uint8_t* base, uint32_t hero) {
  int ranks = 0;
  for (int id = kFirstSpell; id <= kLastSpell; ++id) {
    const int rank = refii_mod_ability_level(base, hero, id);
    if (rank > 0 && rank <= kMaxChargeLevel) {
      ranks += rank;
    }
  }
  // Forty ranks are worth what five Toughness levels are: the whole 600 health points.
  return AlongHealthCurve(600.0f * static_cast<float>(ranks) / static_cast<float>(kAllRanks));
}

/// The charge levels a player's pool can pay for, 0..5.
int AffordableLevels(int index) {
  int levels = static_cast<int>(ManaOf(index) / Knob("souls_combat.mana_per_level", kManaPerLevel));
  return levels < 0 ? 0 : (levels > kMaxChargeLevel ? kMaxChargeLevel : levels);
}

void OnTick(refii::mods::TickEvent& e) {
  float value[2] = {0.0f, 0.0f};
  for (int index = 0; index < 2; ++index) {
    const int32_t player = index + 1;
    const uint32_t hero = refii_mod_player_hero(e.base, player);
    if (hero == 0) {
      g_max[index] = 0.0f;
      continue;
    }

    g_max[index] = MaxMana(e.base, hero);
    value[index] = ManaOf(index);
    if (refii_mod_player_here(player) != 0) {
      // A hero arriving (a load, a join) starts full: ManaOf is the maximum until stated.
      const float rest = refii_mod_player_value(player, kManaRestKey, kRestCap) + e.seconds;
      if (rest >= Knob("souls_combat.mana_regen_delay", kManaRegenDelay)) {
        value[index] += Knob("souls_combat.mana_regen_per_second", kManaRegenPerSecond) * e.seconds;
      }
      value[index] = value[index] > g_max[index] ? g_max[index] : (value[index] < 0.0f ? 0.0f : value[index]);
      refii_mod_player_set(player, kManaKey, value[index]);
      refii_mod_player_set(player, kManaRestKey, rest > kRestCap ? kRestCap : rest);
    }
  }

  refii::mods::Publish("souls_combat.bar_cap", kBarCap);
  refii::mods::Publish("souls_combat.mana", value[0]);
  refii::mods::Publish("souls_combat.mana_max", g_max[0]);
  refii::mods::Publish("souls_combat.mana2", value[1]);
  refii::mods::Publish("souls_combat.mana2_max", g_max[1]);
}
REFII_MOD_ON(Tick, OnTick);

void OnSpellCharge(refii::mods::SpellChargeEvent& e) {
  const int player = Index(e.player);
  if (player >= 0 && AffordableLevels(player) == 0) {
    e.cancel = true;
  }
}
REFII_MOD_ON(SpellCharge, OnSpellCharge);

/// The top slot each player last charged to: what a cascade's lower slot must leave paid for.
int g_top[2] = {-1, -1};

void OnSpellChargeLevel(refii::mods::SpellChargeLevelEvent& e) {
  const int player = Index(e.player);
  if (player < 0) {
    return;
  }
  const int affordable = AffordableLevels(player);
  if (affordable > 0 && e.level + 1 > affordable) {
    e.level = affordable - 1;
  }
  g_top[player] = e.level;
}
REFII_MOD_ON(SpellChargeLevel, OnSpellChargeLevel);

void OnSpellLaunch(refii::mods::SpellLaunchEvent& e) {
  const int player = Index(e.player);
  if (player < 0 || e.slot < 0 || e.slot >= kMaxChargeLevel) {
    return;
  }
  const float mana = ManaOf(player);
  const float cost = Cost(e.slot + 1);
  if (e.cascading) {
    // A lower slot of the cascade: skipped when paying it would leave the top slot unpaid.
    const int top = g_top[player] >= e.slot ? g_top[player] : e.slot;
    if (mana - cost < Cost(top + 1)) {
      e.cancel = true;
    }
  } else if (mana < cost) {
    e.cancel = true;
  }
}
REFII_MOD_ON(SpellLaunch, OnSpellLaunch);

void OnSpellLaunched(refii::mods::SpellLaunchedEvent& e) {
  const int player = Index(e.player);
  const int level = e.slot + 1;
  if (player < 0 || level < 1 || level > kMaxChargeLevel) {
    return;
  }
  // A pool nobody has stated yet is full, and the first statement comes from the tick where the
  // hero is played, so a launch before it has nothing to come off. The owner's tick keeps the pool
  // at or above zero.
  if (refii_mod_player_get(player + 1, kManaKey, nullptr) == 0) {
    return;
  }
  refii_mod_player_add(player + 1, kManaKey, -Cost(level), 0);
  refii_mod_player_set(player + 1, kManaRestKey, 0.0f);
}
REFII_MOD_ON(SpellLaunched, OnSpellLaunched);

}  // namespace
