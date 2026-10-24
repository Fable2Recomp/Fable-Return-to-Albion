#include <array>
#include <atomic>
#include <cmath>
#include <cstdio>

#include <rex/ppc/context.h>
#include <rex/ppc/func.h>

#include "core/mod_values.h"
#include "core/refii_mod.h"

extern "C" REX_FUNC(__imp__rex_CECMorph_GetWeight);

namespace {

std::atomic<uint32_t> g_hero{0};
std::atomic<bool> g_manual{false};
std::array<std::atomic<float>, 19> g_weights{};
float g_generation = -1;

void Refresh(PPCContext* ctx, uint8_t* base, uint32_t hero) {
  if (hero == 0 || refii_mod_entity_alive(base, hero) == 0) return;
  const char* source = hero == refii_mod_player_hero(base, 2)
      ? "local h=GetPlayerHenchman(); if h and h:IsAlive() and GraphicAppearanceMorph.IsAvailable(h) then GraphicAppearanceMorph.UpdateTextureMorphs(h) end"
      : "local h=GetPlayerHero(); if h and h:IsAlive() and GraphicAppearanceMorph.IsAvailable(h) then GraphicAppearanceMorph.UpdateTextureMorphs(h) end";
  refii_mod_run_game_lua(ctx, base, source);
}

void Tick(PPCContext* ctx, uint8_t* base, float) {
  if (base == nullptr) return;
  const float generation = refii::mods::Setting("refii.character_morphs.generation", 0);
  const bool changed = generation != g_generation;
  const bool was_manual = g_manual.load(std::memory_order_relaxed);
  if (!changed && !was_manual) return;

  if (changed) {
    g_generation = generation;
    for (uint32_t type = 2; type <= 18; ++type) {
      char key[48];
      std::snprintf(key, sizeof(key), "refii.character_morphs.%u", type);
      const float weight = refii::mods::Setting(key, 0);
      g_weights[type].store(std::isfinite(weight) ? weight : 0, std::memory_order_relaxed);
    }
    g_manual.store(refii::mods::Setting("refii.character_morphs.automatic", 1) == 0,
                   std::memory_order_release);
  }

  const bool manual = g_manual.load(std::memory_order_acquire);
  const uint32_t hero = manual ? refii_mod_local_hero(base) : 0;
  const uint32_t previous = g_hero.exchange(hero, std::memory_order_acq_rel);
  if (previous != hero) Refresh(ctx, base, previous);
  if (manual && (changed || previous != hero)) Refresh(ctx, base, hero);
}

REFII_MOD_ON_TICK(Tick);

}

extern "C" REX_FUNC(rex_CECMorph_GetWeight) {
  REX_FUNC_PROLOGUE();
  const uint32_t type = ctx.r4.u32;
  const uint32_t component = ctx.r3.u32;
  const uint32_t hero = g_hero.load(std::memory_order_acquire);
  if (g_manual.load(std::memory_order_acquire) && type >= 2 && type <= 18 &&
      hero != 0 && component >= 0x10000u && component < 0xC0000000u &&
      refii::mods::LoadU32(base, component + 4) == hero) {
    PPCContext adult = refii::mods::Frame(ctx);
    adult.r4.u64 = 1;
    __imp__rex_CECMorph_GetWeight(adult, base);
    ctx.f1.f64 = adult.f1.f64 > 0 ? g_weights[type].load(std::memory_order_relaxed) : 0;
    return;
  }
  __imp__rex_CECMorph_GetWeight(ctx, base);
}
