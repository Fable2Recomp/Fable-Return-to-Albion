// Spell Weaving — the ability: a Hybrid tree of one, bought with Will AND Skill experience.
//
// MOD STATE, NOT A GUEST ABILITY. The guest's ability types are a fixed 1..14 enum written into
// its switches, so the level lives here and in the save (RefiiModSave.SpellWeaving, persisted by
// refii's init preamble through the game's own GetSaveTable / LoadScriptSave). The row on the
// Hybrid page is declared in ui/abilities.json with disciplines ["Will", "Skill"]; the page itself
// is a patch entry in ui/templates.json. Pressing A hands this file spell_weaving.buy as an
// incrementing count, Y spell_weaving.refund.
//
// THE PRICE IS SPLIT EVENLY BETWEEN WILL AND SKILL, General filling each half's gap as it fills
// a whole in Fable 2. A purchase is the shipped one (TU1 sub_8246C970) done twice: the level's
// cost from the game's own price table (refii_mod_ability_cost, the ability whose table
// spell_weaving.cost_like names), then two Spends (refii_mod_spend_experience), Will's half (the
// odd point) then Skill's. Spend takes what the pool has and the rest from General WITHOUT
// checking General, so the affordability test is here: each half against its pool plus what
// General has left after the other half. Pools: 0 General, 1 Strength, 2 Skill, 3 Will.
//
// An unlearn is the shipped one (TU1 sub_8246D510): the level drops by one and half the cost of
// the level given up comes back into GENERAL through the game's own Modify
// (refii_mod_give_experience), outside the lifetime total.
//
// Setting spell_weaving.cost_like (default 8, Fireball): the ability whose experience table prices
// the levels. The eight spells share one curve, so any of 7..14 reads the same.

#include "core/refii_mod.h"
#include "spell_weaving.h"

namespace spell_weaving {

int g_level = 0;

}  // namespace spell_weaving

using namespace spell_weaving;

namespace {

constexpr int kGeneralPool = 0;
constexpr int kSkillPool = 2;
constexpr int kWillPool = 3;
constexpr int kDefaultCostTable = 8;
constexpr const char* kSaveKey = "SpellWeaving";

float g_seen_buy = 0.0f;
float g_seen_refund = 0.0f;

int CostOf(PPCContext* ctx, uint8_t* base, uint32_t hero, int level) {
  return refii_mod_ability_cost(ctx, base, hero, static_cast<int>(Knob("spell_weaving.cost_like", static_cast<float>(kDefaultCostTable))),
                                level);
}

int PoolOf(uint8_t* base, uint32_t hero, int pool) {
  float value = 0.0f;
  return refii_mod_experience(base, hero, pool, &value) != 0 ? static_cast<int>(value) : 0;
}

/// Will's half takes the odd point.
int WillShare(int cost) { return (cost + 1) / 2; }
int SkillShare(int cost) { return cost / 2; }

/// Each half against its own pool, the shortfalls against General, in Will's then Skill's order.
bool Affordable(uint8_t* base, uint32_t hero, int cost) {
  int general = PoolOf(base, hero, kGeneralPool);
  const int will = PoolOf(base, hero, kWillPool);
  const int skill = PoolOf(base, hero, kSkillPool);
  general -= WillShare(cost) > will ? WillShare(cost) - will : 0;
  general -= SkillShare(cost) > skill ? SkillShare(cost) - skill : 0;
  return general >= 0;
}

/// Writes the level into the save's RefiiModSave table.
void Record(PPCContext* ctx, uint8_t* base) {
  if (refii_mod_save_set_int(ctx, base, kSaveKey, g_level) == 0) {
    REXLOG_WARN("Spell Weaving: level {} NOT recorded for the save: the game Lua refused it", g_level);
  }
}

void Buy(PPCContext* ctx, uint8_t* base, uint32_t hero) {
  if (g_level >= kMaxLevel) {
    REXLOG_INFO("Spell Weaving: already at level {}", g_level);
    return;
  }

  float probe = 0.0f;
  if (refii_mod_experience(base, hero, kGeneralPool, &probe) == 0) {
    REXLOG_WARN("Spell Weaving: no stats or experience component on the hero; not bought");
    return;
  }

  const int cost = CostOf(ctx, base, hero, g_level + 1);
  if (cost <= 0 || !Affordable(base, hero, cost)) {
    REXLOG_INFO("Spell Weaving {} costs {} ({} Will + {} Skill); Will {}, Skill {}, General {}", g_level + 1, cost,
                WillShare(cost), SkillShare(cost), PoolOf(base, hero, kWillPool), PoolOf(base, hero, kSkillPool),
                PoolOf(base, hero, kGeneralPool));
    return;
  }

  if (WillShare(cost) > 0) {
    refii_mod_spend_experience(ctx, base, hero, kWillPool, static_cast<float>(WillShare(cost)));
  }
  if (SkillShare(cost) > 0) {
    refii_mod_spend_experience(ctx, base, hero, kSkillPool, static_cast<float>(SkillShare(cost)));
  }

  ++g_level;
  Record(ctx, base);
  REXLOG_INFO("Spell Weaving: bought level {} for {} experience ({} Will, {} Skill)", g_level, cost, WillShare(cost),
              SkillShare(cost));
}

void Refund(PPCContext* ctx, uint8_t* base, uint32_t hero) {
  if (g_level <= 0) {
    return;
  }

  const int refund = CostOf(ctx, base, hero, g_level) / 2;
  if (refund < 0 || refii_mod_give_experience(ctx, base, hero, kGeneralPool, static_cast<float>(refund)) == 0) {
    return;
  }

  --g_level;
  Record(ctx, base);
  REXLOG_INFO("Spell Weaving: unlearned level {}, {} experience back to General", g_level + 1, refund);
}

/// The level lives in the save. The Lua and SaveLoaded events say the save's table changed; the
/// read itself waits for the next tick with the hero present, where the game VM is up and a
/// chunk can run.
bool g_reread = true;

void SaveChanged(refii::mods::LuaEvent& e) {
  (void)e;
  g_reread = true;
}
REFII_MOD_ON(Lua, SaveChanged);

void SaveLoaded(refii::mods::SaveLoadedEvent& e) { SaveChanged(e); }
REFII_MOD_ON(SaveLoaded, SaveLoaded);

void OnTick(refii::mods::TickEvent& e) {
  const uint32_t hero = refii_mod_player_hero(e.base, 1);
  if (hero == 0) {
    return;
  }

  if (g_reread) {
    g_reread = false;
    int32_t level = 0;
    g_level = refii_mod_save_get_int(e.ctx, e.base, kSaveKey, 0, kMaxLevel, &level) != 0 ? level : 0;
  }

  const float buy = refii::mods::Setting("spell_weaving.buy", 0.0f);
  if (buy != g_seen_buy) {
    g_seen_buy = buy;
    Buy(e.ctx, e.base, hero);
  }

  const float refund = refii::mods::Setting("spell_weaving.refund", 0.0f);
  if (refund != g_seen_refund) {
    g_seen_refund = refund;
    Refund(e.ctx, e.base, hero);
  }

  refii::mods::Publish("spell_weaving.level", static_cast<float>(g_level));
  // The description's %1: the power a cast may weave at once the next level is bought.
  refii::mods::Publish("spell_weaving.next", static_cast<float>(g_level < kMaxLevel ? g_level + 1 : g_level));
  if (g_level < kMaxLevel) {
    refii::mods::Publish("spell_weaving.cost", static_cast<float>(CostOf(e.ctx, e.base, hero, g_level + 1)));
  }
  refii::mods::Publish("spell_weaving.refund_value",
                       g_level > 0 ? static_cast<float>(CostOf(e.ctx, e.base, hero, g_level) / 2) : 0.0f);
}
REFII_MOD_ON(Tick, OnTick);

}  // namespace
