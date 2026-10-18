// Souls Combat — the dodge roll costs stamina and is refused on an empty pool.
//
// The events (core/refii_mod_events.h): Roll asks whether the roll may start (once per matching
// roll record, so it gates without spending); RollStarted says one was queued (the game answers
// 0 on a neutral stick, and the event fires only for a real roll), so the cost is taken there.
// Only player heroes carry roll records, so the player filter excludes nothing that rolls.
//
// INVULNERABILITY IS THE GAME'S OWN. The hit dispatch drops every hit at entry unless the
// victim's component 31 has both +0x18 and +0x19 set (TU1 0x825c5254..0x825c5278); the roll's
// OnStart saves +0x19 and clears it (0x8270087c) and its end restores it (0x82700b50). So a roll
// is already invulnerable for its whole length -- nothing to add here.

#include "core/refii_mod.h"
#include "souls_combat.h"

using namespace souls_combat;

namespace {

void OnRoll(refii::mods::RollEvent& e) {
  const int player = Index(e.player);
  if (player >= 0 && g_pools[player].value <= 0.0f) {
    e.cancel = true;
  }
}
REFII_MOD_ON(Roll, OnRoll);

void OnRollStarted(refii::mods::RollStartedEvent& e) {
  const int player = Index(e.player);
  if (player >= 0) {
    Spend(player, Knob("souls_combat.roll_cost", kRollCost));
  }
}
REFII_MOD_ON(RollStarted, OnRollStarted);

}  // namespace
