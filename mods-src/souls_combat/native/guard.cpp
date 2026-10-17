// Souls Combat — a guard costs stamina, and breaks when the pool cannot pay.
//
// The seams, from the binary (TU1 / disc, named in versions/*/refii_config.toml): the two hit
// listeners a block action registers on its victim's hit dispatch (`sub_825C4FC8(comp31, X+0x58,
// X+0x54, 0)` in the block base's Start, TU1 0x8270B698), at priority 0, ahead of health's 30:
//   rex_CActionBlock_OnHitBlockPose (0x8270E5F8 / 0x826E3F80)
//   rex_CActionBlock_OnHitBlockedResponse (0x8270C248 / 0x826E1BD0)
// Both take r3 = the listener at X+0x54 of the block action X, r4 = the hit (the dispatch's own
// mutable copy), r5 = the result word. A non-zero result is what makes the health listener skip
// the hit (0x824caa1c `lwz r11,0(r5)` -> end), so NOT calling the original leaves the result at
// zero and the hit lands in full: that is the guard break. The blocker is `*(*(X+8))`, the
// entity the block's Start took component 31 from. Hit +0x38 is the damage float, +0x19 marks it
// unblockable (the originals skip those; so does this).
//
// The one other thing that zeroes a hit before the listeners run is not a block: the dispatch
// (0x825c593c / disc 0x824841cc) sets damage to 0 while the victim's spell manager (component
// 56) holds a live Blades "shielded swords" group at +0x1E0 — an enemy caster's conjured swords
// soaking hits, set by the Swords cast's Start (0x825A2720) and cleared by its Stop
// (0x825A2AB8). That is the spell working as designed and is left alone.

#include "core/refii_mod.h"
#include "souls_combat.h"

REFII_MOD_ORIGINAL(rex_CActionBlock_OnHitBlockPose);
REFII_MOD_ORIGINAL(rex_CActionBlock_OnHitBlockedResponse);

using namespace souls_combat;

namespace {

constexpr uint32_t kListenerOffset = 0x54;

/// Spends the block's cost and answers whether the guard holds.
bool GuardHolds(uint8_t* base, uint32_t listener, uint32_t hit) {
  const uint32_t action = listener - kListenerOffset;
  const uint32_t holder = refii::mods::LoadU32(base, action + 8u);
  const uint32_t entity = holder != 0 ? refii::mods::LoadU32(base, holder) : 0u;
  const int player = PlayerOf(base, entity);
  if (player < 0 || refii::mods::LoadU8(base, hit + 0x19u) != 0) {
    return true;
  }

  const float damage = refii::mods::LoadF32(base, hit + 0x38u);
  const float cost = (damage > 0.0f ? damage : 0.0f) * souls_combat::Knob("souls_combat.block_cost_per_damage", kBlockCostPerDamage);
  const bool holds = g_pools[player].value >= cost && g_pools[player].value > 0.0f;
  Spend(player, cost);
  return holds;
}

}  // namespace

REX_EXTERN(rex_CActionBlock_OnHitBlockPose) {
  REX_FUNC_PROLOGUE();
  if (!GuardHolds(base, ctx.r3.u32, ctx.r4.u32)) {
    return;
  }
  __imp__rex_CActionBlock_OnHitBlockPose(ctx, base);
}

REX_EXTERN(rex_CActionBlock_OnHitBlockedResponse) {
  REX_FUNC_PROLOGUE();
  if (!GuardHolds(base, ctx.r3.u32, ctx.r4.u32)) {
    return;
  }
  __imp__rex_CActionBlock_OnHitBlockedResponse(ctx, base);
}
