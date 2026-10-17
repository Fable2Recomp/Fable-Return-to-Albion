// Souls Combat — the dodge roll costs stamina and is refused on an empty pool.
//
// The seams, from the binary (TU1 / disc, named in versions/*/refii_config.toml), the roll
// ability CAbilityActionStrafeRoll's vtable slots 1 and 2 (TU1 0x820CC5D8, disc 0x820CC480):
//   rex_CAbilityActionStrafeRoll_CanStart (0x8297A100 / 0x829A9718): r4 = the entity; 1 allows.
//     Asked once per roll record that matches the request (up to four), all in one dispatch, so
//     it gates without spending.
//   rex_CAbilityActionStrafeRoll_Start (0x8297A7E8 / 0x829A9E00): r4 = the entity; returns 1
//     only when the roll action was queued (it returns 0 on a neutral stick), so the cost is
//     taken after the original, on 1.
// Only player heroes carry roll records (AbilitySet #b8424a93, reached only from the hero's
// PlayerAbilityComponent), so the player filter excludes nothing that rolls.
//
// INVULNERABILITY IS THE GAME'S OWN. The hit dispatch (TU1 0x825C5220) drops every hit at entry
// unless the victim's component 31 has both +0x18 and +0x19 set (0x825c5254..0x825c5278); the
// roll's OnStart (0x826FFFF0) saves +0x19 and clears it (0x8270087c) and its end restores it
// (0x82700b50). So a roll is already invulnerable for its whole length -- nothing to add here.

#include "core/refii_mod.h"
#include "souls_combat.h"

REFII_MOD_ORIGINAL(rex_CAbilityActionStrafeRoll_CanStart);
REFII_MOD_ORIGINAL(rex_CAbilityActionStrafeRoll_Start);

using namespace souls_combat;

REX_EXTERN(rex_CAbilityActionStrafeRoll_CanStart) {
  REX_FUNC_PROLOGUE();
  const int player = PlayerOf(base, ctx.r4.u32);
  if (player >= 0 && g_pools[player].value <= 0.0f) {
    ctx.r3.u64 = 0;
    return;
  }

  __imp__rex_CAbilityActionStrafeRoll_CanStart(ctx, base);
}

REX_EXTERN(rex_CAbilityActionStrafeRoll_Start) {
  REX_FUNC_PROLOGUE();
  const int player = PlayerOf(base, ctx.r4.u32);
  __imp__rex_CAbilityActionStrafeRoll_Start(ctx, base);

  if (player >= 0 && ctx.r3.u32 != 0) {
    Spend(player, souls_combat::Knob("souls_combat.roll_cost", kRollCost));
  }
}
