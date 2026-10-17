// Souls Combat — stamina.
//
// A pool per player hero (both players are principals), in points. Swings spend it; it refills
// after a short pause; at zero the next swing is refused. Published to Godot as
// souls_combat.stamina / stamina_max (player 1) and stamina2 / stamina2_max (player 2) for the
// HUD meters in ui/hud.json.
//
// The seams, from the binary (TU1 / disc, both named in versions/*/refii_config.toml):
//   rex_CECAbilities_PerformRequest (0x826F57E8 / 0x8273DA00) — every ability request any
//     creature makes: r3 = the entity, r4 = the request, whose vtable slot 1 returns its
//     AbilityType (16 = melee pressed, 17 = melee released, 0x826f5f7c `cmpwi r3,0x10`). Not
//     calling the original refuses the request; r3 = 0 is what it returns when nothing matched.
//   rex_CAbilityActionMelee_CommitSwing (0x8297BD68 / 0x829AEB98) — called once by every strike's
//     Perform after the swing's animation has started: r3 = the ability, r4 = the entity.
//
// A refused PRESS also refuses the RELEASE that follows it, so the pair stays together: whether a
// release with no charge state behind it can still match a swing record is not traced.
// Holding X is also the block, so an empty pool cannot raise a guard either — the guard breaking
// at zero is the Elden Ring rule this approximates.

#include "core/refii_mod.h"
#include "souls_combat.h"

REFII_MOD_ORIGINAL(rex_CECAbilities_PerformRequest);
REFII_MOD_ORIGINAL(rex_CAbilityActionMelee_CommitSwing);

namespace souls_combat {

Pool g_pools[2];

int PlayerOf(uint8_t* base, uint32_t entity) {
  if (entity == 0) {
    return -1;
  }
  if (entity == refii_mod_player_hero(base, 1)) {
    return 0;
  }
  if (entity == refii_mod_player_hero(base, 2)) {
    return 1;
  }
  return -1;
}

void Spend(int player, float cost) {
  Pool& pool = g_pools[player];
  pool.value -= cost;
  if (pool.value < souls_combat::Knob("souls_combat.stamina_floor", kFloor)) {
    pool.value = souls_combat::Knob("souls_combat.stamina_floor", kFloor);
  }
  pool.since_spend = 0.0f;
}

}  // namespace souls_combat

using namespace souls_combat;

namespace {

constexpr uint32_t kMeleePressed = 16;
constexpr uint32_t kMeleeReleased = 17;

bool g_press_refused[2] = {false, false};

void Tick(PPCContext* ctx, uint8_t* base, float seconds) {
  (void)ctx;
  for (int player = 0; player < 2; ++player) {
    Pool& pool = g_pools[player];
    if (refii_mod_player_hero(base, player + 1) == 0) {
      pool = Pool{};
      continue;
    }

    // Player 1's maximum grows with the Stamina ability; the partner's progression is their own
    // game's, which this one cannot see, so theirs stays at the base.
    pool.max = player == 0 ? MaxStamina(g_stamina_level) : kBaseStamina;
    pool.since_spend += seconds;
    if (pool.since_spend >= souls_combat::Knob("souls_combat.stamina_regen_delay", kRegenDelay) && pool.value < pool.max) {
      pool.value += souls_combat::Knob("souls_combat.stamina_regen_per_second", kRegenPerSecond) * seconds;
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

REFII_MOD_ON_TICK(Tick);

}  // namespace

REX_EXTERN(rex_CECAbilities_PerformRequest) {
  REX_FUNC_PROLOGUE();
  const int player = PlayerOf(base, ctx.r3.u32);
  if (player >= 0) {
    PPCContext query = ctx;
    const uint32_t type =
        refii::mods::CallVirtual(query, base, ctx.r4.u32, 1) ? query.r3.u32 : 0u;

    if (type == kMeleePressed) {
      g_press_refused[player] = g_pools[player].value <= 0.0f;
      if (g_press_refused[player]) {
        ctx.r3.u64 = 0;
        return;
      }
    } else if (type == kMeleeReleased && g_press_refused[player]) {
      g_press_refused[player] = false;
      ctx.r3.u64 = 0;
      return;
    }
  }

  __imp__rex_CECAbilities_PerformRequest(ctx, base);
}

uint32_t souls_combat::g_committed_strike[2] = {0, 0};

// Runs after the strike's PlayAction succeeded, so the hero's action manager (component 49)
// holds the live clone at +0x10 (CECActionManagerSimple, read as `a1[4]` by the interrupt rule
// sub_822CB150) — the instance commitment.cpp keys its deferred interrupt on.
REX_EXTERN(rex_CAbilityActionMelee_CommitSwing) {
  REX_FUNC_PROLOGUE();
  const uint32_t hero = ctx.r4.u32;
  const int player = PlayerOf(base, hero);
  if (player >= 0) {
    Spend(player, souls_combat::Knob("souls_combat.swing_cost", kSwingCost));
  }

  __imp__rex_CAbilityActionMelee_CommitSwing(ctx, base);

  if (player >= 0) {
    const uint32_t manager = refii_mod_component(base, hero, 49u);
    g_committed_strike[player] = manager != 0 ? refii::mods::LoadU32(base, manager + 0x10u) : 0u;
  }
}
