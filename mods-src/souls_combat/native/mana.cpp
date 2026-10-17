// Souls Combat — mana (FP) for spells.
//
// A pool per player hero. Its maximum grows with the spells the hero has learned, along the same
// curve health takes: from the untrained length to a maxed health bar's, evenly over all forty
// ranks of the eight spells. A cast costs kManaPerLevel per charge level; an empty pool refuses the
// charge, a short one caps how far it charges. Mana refills slowly after a pause — Fable 2 has no
// flask to refill it with. Published as souls_combat.mana / mana_max (player 1) and mana2 /
// mana2_max (player 2) for the FP bar in ui/hud.json.
//
// The seams, from the binary (TU1 / disc, named in versions/*/refii_config.toml):
//   rex_CAbilityActionMagicCharge_CanStart (0x82989A80 / 0x829B9040): the B-press ability's gate
//     (AbilityType 18), r4 = the entity; 0 refuses the charge. No side effects.
//   rex_CECSpellManager_ChargeLevelFromTime (0x8253E4B8 / 0x82428EF8): turns the charge time into
//     the top slot to cast, written through r7 (an int*); every caller — the cast animation, the
//     release and the cascade — reads it from there, so clamping it after the original caps them
//     all. r3 = the spell manager (component 56), whose +4 is the caster.
//   rex_CECSpellManager_OnSpellLaunched (0x82543A20 / 0x8242E460): called only when a spell was
//     really created (0x8253f148, behind the launch's own success test); r3 = the spell manager,
//     r4 = the slot launched (charge level - 1). The cost is taken here.
// Learned ranks are the stats component's ability levels for ids 7..14 (the eight spells) at
// +0x110 + 4*id — the table the abilities book and the guest's SetAbilityLevel use.

#include "core/refii_mod.h"
#include "souls_combat.h"

REFII_MOD_ORIGINAL(rex_CAbilityActionMagicCharge_CanStart);
REFII_MOD_ORIGINAL(rex_CECSpellManager_ChargeLevelFromTime);
REFII_MOD_ORIGINAL(rex_CECSpellManager_OnSpellLaunched);

using namespace souls_combat;

namespace {

constexpr int kAllRanks = 8 * 5;
constexpr float kManaPerLevel = 12.0f;
constexpr float kManaRegenDelay = 2.5f;
constexpr float kManaRegenPerSecond = 6.0f;
constexpr uint32_t kStatsComponent = 57;
constexpr uint32_t kFirstSpell = 7;
constexpr uint32_t kLastSpell = 14;
constexpr int kMaxChargeLevel = 5;

struct Mana {
  float value = 0.0f;
  float max = 0.0f;
  float since_spend = 99.0f;
  bool known = false;
};

Mana g_mana[2];

float Cost(int level) { return souls_combat::Knob("souls_combat.mana_per_level", kManaPerLevel) * static_cast<float>(level); }

float MaxMana(uint8_t* base, uint32_t hero) {
  const uint32_t stats = refii_mod_component(base, hero, kStatsComponent);
  if (stats == 0) {
    return 0.0f;
  }
  int ranks = 0;
  for (uint32_t id = kFirstSpell; id <= kLastSpell; ++id) {
    const int rank = static_cast<int32_t>(refii::mods::LoadU32(base, stats + 0x110u + 4u * id));
    if (rank > 0 && rank <= kMaxChargeLevel) {
      ranks += rank;
    }
  }
  // Forty ranks are worth what five Toughness levels are: the whole 600 health points.
  return AlongHealthCurve(600.0f * static_cast<float>(ranks) / static_cast<float>(kAllRanks));
}

/// The charge levels the pool can pay for, 0..5.
int AffordableLevels(const Mana& mana) {
  int levels = static_cast<int>(mana.value / souls_combat::Knob("souls_combat.mana_per_level", kManaPerLevel));
  return levels < 0 ? 0 : (levels > kMaxChargeLevel ? kMaxChargeLevel : levels);
}

void Tick(PPCContext* ctx, uint8_t* base, float seconds) {
  (void)ctx;
  for (int player = 0; player < 2; ++player) {
    Mana& mana = g_mana[player];
    const uint32_t hero = refii_mod_player_hero(base, player + 1);
    if (hero == 0) {
      mana = Mana{};
      continue;
    }

    mana.max = MaxMana(base, hero);
    if (!mana.known) {
      // A hero arriving (a load, a join) starts full.
      mana.value = mana.max;
      mana.known = true;
    }

    mana.since_spend += seconds;
    if (mana.since_spend >= souls_combat::Knob("souls_combat.mana_regen_delay", kManaRegenDelay)) {
      mana.value += souls_combat::Knob("souls_combat.mana_regen_per_second", kManaRegenPerSecond) * seconds;
    }
    if (mana.value > mana.max) {
      mana.value = mana.max;
    }
  }

  refii::mods::Publish("souls_combat.bar_cap", kBarCap);
  refii::mods::Publish("souls_combat.mana", g_mana[0].value);
  refii::mods::Publish("souls_combat.mana_max", g_mana[0].max);
  refii::mods::Publish("souls_combat.mana2", g_mana[1].value);
  refii::mods::Publish("souls_combat.mana2_max", g_mana[1].max);
}

REFII_MOD_ON_TICK(Tick);

}  // namespace

REX_EXTERN(rex_CAbilityActionMagicCharge_CanStart) {
  REX_FUNC_PROLOGUE();
  const int player = PlayerOf(base, ctx.r4.u32);
  if (player >= 0 && AffordableLevels(g_mana[player]) == 0) {
    ctx.r3.u64 = 0;
    return;
  }

  __imp__rex_CAbilityActionMagicCharge_CanStart(ctx, base);
}

REX_EXTERN(rex_CECSpellManager_ChargeLevelFromTime) {
  REX_FUNC_PROLOGUE();
  const uint32_t manager = ctx.r3.u32;
  const uint32_t top_slot = ctx.r7.u32;
  __imp__rex_CECSpellManager_ChargeLevelFromTime(ctx, base);

  const int player = manager != 0 ? PlayerOf(base, refii::mods::LoadU32(base, manager + 4u)) : -1;
  if (player < 0 || top_slot == 0) {
    return;
  }

  const int affordable = AffordableLevels(g_mana[player]);
  const int slot = static_cast<int32_t>(refii::mods::LoadU32(base, top_slot));
  if (affordable > 0 && slot + 1 > affordable) {
    refii::mods::StoreU32(base, top_slot, static_cast<uint32_t>(affordable - 1));
  }
}

REX_EXTERN(rex_CECSpellManager_OnSpellLaunched) {
  REX_FUNC_PROLOGUE();
  const uint32_t manager = ctx.r3.u32;
  const int level = static_cast<int32_t>(ctx.r4.u32) + 1;
  const int player = manager != 0 ? PlayerOf(base, refii::mods::LoadU32(base, manager + 4u)) : -1;
  if (player >= 0 && level >= 1 && level <= kMaxChargeLevel) {
    Mana& mana = g_mana[player];
    mana.value -= Cost(level);
    if (mana.value < 0.0f) {
      mana.value = 0.0f;
    }
    mana.since_spend = 0.0f;
  }

  __imp__rex_CECSpellManager_OnSpellLaunched(ctx, base);
}
