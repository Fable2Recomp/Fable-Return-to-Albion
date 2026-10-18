// Souls Combat — a guard costs stamina, and breaks when the pool cannot pay.
//
// The event (core/refii_mod_events.h): Block fires once per hit a block is taking (the game runs
// two block listeners per hit, and the bus gives the second the first's verdict, so the cost is
// taken once). Cancelling BREAKS the guard: the result word the block would have set stays zero
// and the hit lands in full. An unblockable hit bypasses the block in the game already and is
// never charged.
//
// The one other thing that zeroes a hit before the block runs is not a block: the dispatch sets
// damage to 0 while the victim's spell manager holds a live Blades "shielded swords" group — an
// enemy caster's conjured swords soaking hits. That is the spell working as designed.

#include "core/refii_mod.h"
#include "souls_combat.h"

using namespace souls_combat;

namespace {

void OnBlock(refii::mods::BlockEvent& e) {
  const int player = Index(e.player);
  if (player < 0 || e.unblockable) {
    return;
  }
  const float cost = (e.damage > 0.0f ? e.damage : 0.0f) * Knob("souls_combat.block_cost_per_damage", kBlockCostPerDamage);
  const bool holds = g_pools[player].value >= cost && g_pools[player].value > 0.0f;
  if (!holds) {
    e.cancel = true;  // The guard breaks and the hit lands in full: that is the whole price.
    return;
  }
  Spend(player, cost);
}
REFII_MOD_ON(Block, OnBlock);

}  // namespace
