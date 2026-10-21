// Souls Combat — stamina.
//
// A pool per player hero (both players are principals), in points. Swings spend it; it refills
// after a short pause; at zero the next swing is refused. The HUD meter (ui/hud.json) reads the
// pool and its maximum straight from the player store — player:souls_combat.stamina and
// player:souls_combat.stamina_max — keyed to whichever player's widget it is drawn beside, so one
// record serves both players on every screen.
//
// THE POOL IS IN THE PLAYER STORE (souls_combat.h), so both co-op machines read the same pool: a
// spend is an add on whichever machine the swing, roll or block happens, the refill runs only
// where the hero is played (refii_mod_player_here), and that machine keeps the pool within
// [floor, max].
//
// The events (core/refii_mod_events.h):
//   AbilityRequest — every ability request any creature makes, with its AbilityType (16 = melee
//     pressed, 17 = melee released). Cancelling refuses it. A refused PRESS also refuses the
//     RELEASE that follows it, so the pair stays together. Holding X is also the block, so an
//     empty pool cannot raise a guard either — the guard breaking at zero is the Elden Ring rule
//     this approximates.
//   MeleeSwing — once per strike, after the swing's animation has started; carries the live
//     strike action, which commitment.cpp keys its deferred interrupt on.
//   Tick — the refill.

#include "core/refii_mod.h"
#include "souls_combat.h"

namespace souls_combat {

uint32_t g_committed_strike[2] = {0, 0};

void Spend(int index, float cost) {
  // A pool nobody has stated yet is full; the first statement comes from the tick where the hero
  // is played, so a spend before it has nothing to come off.
  if (refii_mod_player_get(index + 1, kStaminaKey, nullptr) == 0) {
    return;
  }
  refii_mod_player_add(index + 1, kStaminaKey, -cost, 0);
  refii_mod_player_set(index + 1, kStaminaRestKey, 0.0f);
}

}  // namespace souls_combat

using namespace souls_combat;

namespace {

constexpr uint32_t kMeleePressed = 16;
constexpr uint32_t kMeleeReleased = 17;

constexpr float kRestCap = 10.0f;

bool g_press_refused[2] = {false, false};

void OnTick(refii::mods::TickEvent& e) {
  float value[2] = {0.0f, 0.0f};
  float max[2] = {kBaseStamina, kBaseStamina};
  for (int index = 0; index < 2; ++index) {
    const int32_t player = index + 1;
    if (refii_mod_player_hero(e.base, player) == 0) {
      continue;
    }

    // Each player's maximum grows with their own Stamina ability (the level the engine's
    // purchase saves, ui/abilities.json).
    max[index] = StaminaMax(index);
    value[index] = Stamina(index);
    if (refii_mod_player_here(player) != 0) {
      refii_mod_player_set(player, kStaminaMaxKey, max[index]);
      const float rest = refii_mod_player_value(player, kStaminaRestKey, kRestCap) + e.seconds;
      if (rest >= Knob("souls_combat.stamina_regen_delay", kRegenDelay) && value[index] < max[index]) {
        value[index] += Knob("souls_combat.stamina_regen_per_second", kRegenPerSecond) * e.seconds;
      }
      const float floor = Knob("souls_combat.stamina_floor", kFloor);
      value[index] = value[index] > max[index] ? max[index] : (value[index] < floor ? floor : value[index]);
      refii_mod_player_set(player, kStaminaKey, value[index]);
      // Held at kRestCap once past any refill delay, so a full, rested pool stops changing.
      refii_mod_player_set(player, kStaminaRestKey, rest > kRestCap ? kRestCap : rest);
    }
  }
}
REFII_MOD_ON(Tick, OnTick);

void OnAbilityRequest(refii::mods::AbilityRequestEvent& e) {
  const int player = Index(e.player);
  if (player < 0) {
    return;
  }
  if (e.type == kMeleePressed) {
    g_press_refused[player] = Stamina(player) <= 0.0f;
    if (g_press_refused[player]) {
      e.cancel = true;
    }
  } else if (e.type == kMeleeReleased && g_press_refused[player]) {
    g_press_refused[player] = false;
    e.cancel = true;
  }
}
REFII_MOD_ON(AbilityRequest, OnAbilityRequest);

void OnMeleeSwing(refii::mods::MeleeSwingEvent& e) {
  const int player = Index(e.player);
  if (player < 0) {
    return;
  }
  Spend(player, Knob("souls_combat.swing_cost", kSwingCost));
  g_committed_strike[player] = e.action;
}
REFII_MOD_ON(MeleeSwing, OnMeleeSwing);

}  // namespace
