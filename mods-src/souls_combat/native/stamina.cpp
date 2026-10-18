// Souls Combat — stamina.
//
// A pool per player hero (both players are principals), in points. Swings spend it; it refills
// after a short pause; at zero the next swing is refused. Published to Godot as
// souls_combat.stamina / stamina_max (player 1) and stamina2 / stamina2_max (player 2) for the
// HUD meters in ui/hud.json.
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

Pool g_pools[2];
uint32_t g_committed_strike[2] = {0, 0};

void Spend(int player, float cost) {
  Pool& pool = g_pools[player];
  pool.value -= cost;
  const float floor = Knob("souls_combat.stamina_floor", kFloor);
  if (pool.value < floor) {
    pool.value = floor;
  }
  pool.since_spend = 0.0f;
}

}  // namespace souls_combat

using namespace souls_combat;

namespace {

constexpr uint32_t kMeleePressed = 16;
constexpr uint32_t kMeleeReleased = 17;

bool g_press_refused[2] = {false, false};

void OnTick(refii::mods::TickEvent& e) {
  for (int player = 0; player < 2; ++player) {
    Pool& pool = g_pools[player];
    if (refii_mod_player_hero(e.base, player + 1) == 0) {
      pool = Pool{};
      continue;
    }

    // Player 1's maximum grows with the Stamina ability; the partner's progression is their own
    // game's, which this one cannot see, so theirs stays at the base.
    pool.max = player == 0 ? MaxStamina(g_stamina_level) : kBaseStamina;
    pool.since_spend += e.seconds;
    if (pool.since_spend >= Knob("souls_combat.stamina_regen_delay", kRegenDelay) && pool.value < pool.max) {
      pool.value += Knob("souls_combat.stamina_regen_per_second", kRegenPerSecond) * e.seconds;
    }
    if (pool.value > pool.max) {
      pool.value = pool.max;
    }
  }

  refii::mods::Publish("souls_combat.stamina", g_pools[0].value < 0.0f ? 0.0f : g_pools[0].value);
  refii::mods::Publish("souls_combat.stamina_max", g_pools[0].max);
  refii::mods::Publish("souls_combat.stamina2", g_pools[1].value < 0.0f ? 0.0f : g_pools[1].value);
  refii::mods::Publish("souls_combat.stamina2_max", g_pools[1].max);
}
REFII_MOD_ON(Tick, OnTick);

void OnAbilityRequest(refii::mods::AbilityRequestEvent& e) {
  const int player = Index(e.player);
  if (player < 0) {
    return;
  }
  if (e.type == kMeleePressed) {
    g_press_refused[player] = g_pools[player].value <= 0.0f;
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
