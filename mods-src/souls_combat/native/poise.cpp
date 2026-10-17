// Souls Combat — poise: a hit staggers its victim only once enough damage has landed in a short
// time. Enemies no longer flinch at every tap; neither does the hero.
//
// The seam, from the binary (TU1 / disc, named in versions/*/refii_config.toml):
//   rex_CECHitResponsePlayAction_OnHit (0x8295CF08 / 0x8296D570): the hit-response component's
//     listener on the victim's hit dispatch, at priority 20 — before health's 30. r3 = the
//     listener (component+12; the entity is at r3-8), r4 = the dispatch's copy of the hit,
//     r5 = the result word (non-zero once a block or parry took the hit). It alone starts the
//     flinch (GetHit, priority 8, sub_828B6F38) or the knockdown (priority 10/11, sub_82689268);
//     returning without it plays nothing and leaves no state behind (the comp-21 state bits are
//     set inside the actions it would have started).
//   Its own early-outs are kept: hit+0x1C set (no animated response, ranged), the result word
//     set (parry responses), hit+0x18 set (a knockdown request), and a lethal hit — death's fall
//     must play. "Lethal" is the game's own arithmetic: rex_CECHealth_CalculateDamage
//     (0x824CDDC8 / 0x8268DEF0, health component 36 and the hit, returns the damage) against
//     the health at +0x14; the maximum health at +0x1C sizes the poise.

#include "core/refii_mod.h"
#include "souls_combat.h"

REFII_MOD_ORIGINAL(rex_CECHitResponsePlayAction_OnHit);

/// Called, not overridden: the game's own body, by its config name.
extern "C" REX_FUNC(rex_CECHealth_CalculateDamage);

namespace {

/// Damage, as a share of maximum health, that must land within kPoiseWindow to stagger.
constexpr float kPoiseFraction = 0.3f;
constexpr float kPoiseWindowSeconds = 3.0f;
constexpr uint32_t kHealthComponent = 36;

struct Poise {
  uint32_t entity = 0;
  float taken = 0.0f;
  float last_hit = -1000.0f;
};

Poise g_poise[32];
float g_now = 0.0f;

void Clock(PPCContext*, uint8_t*, float seconds) { g_now += seconds; }
REFII_MOD_ON_TICK(Clock);

Poise& PoiseOf(uint32_t entity) {
  Poise* oldest = &g_poise[0];
  for (Poise& p : g_poise) {
    if (p.entity == entity) {
      return p;
    }
    if (p.last_hit < oldest->last_hit) {
      oldest = &p;
    }
  }
  *oldest = Poise{entity, 0.0f, -1000.0f};
  return *oldest;
}

/// The game's own damage for this hit.
float Damage(PPCContext& ctx, uint8_t* base, uint32_t health, uint32_t hit) {
  PPCContext work = ctx;
  work.r3.u32 = health;
  work.r4.u32 = hit;
  rex_CECHealth_CalculateDamage(work, base);
  return static_cast<float>(work.f1.f64);
}

}  // namespace

REX_EXTERN(rex_CECHitResponsePlayAction_OnHit) {
  REX_FUNC_PROLOGUE();
  const uint32_t listener = ctx.r3.u32;
  const uint32_t hit = ctx.r4.u32;
  const uint32_t result = ctx.r5.u32;
  const uint32_t entity = listener != 0 ? refii::mods::LoadU32(base, listener - 8u) : 0u;
  const uint32_t health = entity != 0 ? refii_mod_component(base, entity, kHealthComponent) : 0u;

  const bool pass_through = health == 0 || hit == 0 || result == 0 ||
                            refii::mods::LoadU32(base, result) != 0 ||
                            refii::mods::LoadU8(base, hit + 0x1Cu) != 0 ||
                            refii::mods::LoadU8(base, hit + 0x18u) != 0;
  if (!pass_through) {
    const float damage = Damage(ctx, base, health, hit);
    const float current = refii::mods::LoadF32(base, health + 0x14u);
    const float maximum = refii::mods::LoadF32(base, health + 0x1Cu);
    if (damage < current && maximum > 0.0f) {
      Poise& poise = PoiseOf(entity);
      if (g_now - poise.last_hit > souls_combat::Knob("souls_combat.poise_window_seconds", kPoiseWindowSeconds)) {
        poise.taken = 0.0f;
      }
      poise.taken += damage;
      poise.last_hit = g_now;
      if (poise.taken < souls_combat::Knob("souls_combat.poise_fraction", kPoiseFraction) * maximum) {
        return;  // Absorbed: no flinch.
      }
      poise.taken = 0.0f;  // Staggered; the pool refills from here.
    }
  }

  __imp__rex_CECHitResponsePlayAction_OnHit(ctx, base);
}
