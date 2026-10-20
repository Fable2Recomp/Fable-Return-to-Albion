// Souls Combat — poise: a hit staggers its victim only once enough damage has landed in a short
// time. Enemies no longer flinch at every tap; neither does the hero.
//
// The event (core/refii_mod_events.h): HitResponse, raised as the victim's hit-response component
// is about to play the flinch or knockdown, with the game's own damage arithmetic for the hit
// and the victim's health. The bus keeps the game's early-outs (a blocked or parried hit, a
// ranged hit, a knockdown request, a killing blow — death's fall must play), so this only ever
// sees a hit the victim would have flinched at. Cancelling absorbs it: no animation, no state.
//
// Online, the game runs the response on both machines for a hit on or by player two; the run on
// the machine not authoritative for the victim is the copy (refii_mod_event_replica). The host
// runs every hit, so its pools are whole: it decides every victim but player two, and keeps
// player two's pool from the same hits the joiner sees, so its copy agrees with the joiner. The
// joiner runs only hits on or by player two: it decides player two's own pool, and its copy of a
// hit by player two on anyone else (whose pool there would miss player one's hits) is left to the
// game's own predicted response, the pool untouched.

#include "core/refii_mod.h"
#include "souls_combat.h"

namespace {

/// Damage, as a share of maximum health, that must land within kPoiseWindow to stagger.
constexpr float kPoiseFraction = 0.3f;
constexpr float kPoiseWindowSeconds = 3.0f;

struct Poise {
  uint32_t entity = 0;
  float taken = 0.0f;
  float last_hit = -1000.0f;
};

Poise g_poise[32];
float g_now = 0.0f;

void Clock(refii::mods::TickEvent& e) { g_now += e.seconds; }
REFII_MOD_ON(Tick, Clock);

/// A dead entity's pool goes with it: its address is the next creature's.
void OnEntityDied(refii::mods::EntityDiedEvent& e) {
  for (Poise& p : g_poise) {
    if (p.entity == e.entity) {
      p = Poise{};
    }
  }
}
REFII_MOD_ON(EntityDied, OnEntityDied);

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

void OnHitResponse(refii::mods::HitResponseEvent& e) {
  if (e.max_health <= 0.0f || (refii_mod_event_replica() != 0 && refii_mod_coop_mode(e.base) == 3)) {
    return;
  }
  Poise& poise = PoiseOf(e.victim);
  if (g_now - poise.last_hit > souls_combat::Knob("souls_combat.poise_window_seconds", kPoiseWindowSeconds)) {
    poise.taken = 0.0f;
  }
  poise.taken += e.damage;
  poise.last_hit = g_now;
  if (poise.taken < souls_combat::Knob("souls_combat.poise_fraction", kPoiseFraction) * e.max_health) {
    e.cancel = true;  // Absorbed: no flinch.
    return;
  }
  poise.taken = 0.0f;  // Staggered; the pool refills from here.
}
REFII_MOD_ON(HitResponse, OnHitResponse);

}  // namespace
