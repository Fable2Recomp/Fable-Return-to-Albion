// Souls Combat — cast recovery: a launched spell plants its caster for a moment.
//
// What the game does (TU1): a charge plays its charge action on the action manager (component
// 49), and the locomotion update stops while that action is current (sub_822B1F78 starts with
// sub_822B5360, false while an action plays that has not reached ALLOW_MOVEMENT_INTERRUPT). A
// quick cast plays its cast animation the same way, but the cast actions hold that permission
// back only until PERFORM — the frame the spell launches — so from the launch on any stick input
// cancels the rest of the animation and the hero walks (sub_82737C38, 0x82737e14..e24). That is
// the free backstep between casts.
//
// The plant: the player component (CECPlayer, component 33) carries a movement-speed override,
// the pair the game's own scripts set through Player.SetPlayerMovementSpeedOverridden (byte +0x1E,
// sub_823DFF78) and Player.SetOverriddenMovementSpeedMultiplier (float +0xCC, sub_8283ECC8). The
// locomotion update multiplies the hero's velocity by the float while the byte is set
// (sub_822B1F78: `lbz r11,0x1E(r30)` 0x822b2c2c, `lvlx v0,r30,0xCC` .. `vmulfp128` 0x822b2c68,
// r30 = component 33 resolved at 0x822b2b38). A multiplier of 0 holds the hero in place; turning
// runs earlier in the same update and still works, and rolls and blocks are actions that do not
// pass through this velocity.
//
// The event (core/refii_mod_events.h): SpellLaunched, once a launch really made a spell. Every
// slot of a charged release's cascade fires it; each refreshes the timer. The override is taken
// only when no script holds it (byte clear), and given back only while it still holds the values
// written here — a script that took it meanwhile keeps it. In co-op only the machine that plays
// the hero plants it (refii_mod_event_replica).

#include "core/refii_mod.h"
#include "souls_combat.h"

namespace {

constexpr uint32_t kPlayerComponent = 33;
constexpr uint32_t kOverridden = 0x1E;
constexpr uint32_t kMultiplier = 0xCC;
constexpr float kRecoverySeconds = 0.6f;

struct Plant {
  uint32_t hero = 0;
  uint32_t player = 0;  // the hero's component 33
  float multiplier = 1.0f;  // the value found there, put back after
  float left = 0.0f;
};

Plant g_plants[2];

void Release(uint8_t* base, Plant& plant) {
  if (plant.player != 0 && refii::mods::IsPlayerHero(base, plant.hero) &&
      refii_mod_component(base, plant.hero, kPlayerComponent) == plant.player &&
      refii::mods::LoadU8(base, plant.player + kOverridden) == 1u &&
      refii::mods::LoadF32(base, plant.player + kMultiplier) == 0.0f) {
    refii::mods::StoreF32(base, plant.player + kMultiplier, plant.multiplier);
    refii::mods::StoreU8(base, plant.player + kOverridden, 0u);
  }
  plant = Plant{};
}

void OnSpellLaunched(refii::mods::SpellLaunchedEvent& e) {
  // The plant holds the body still, so it is made where the hero is played; the other co-op
  // machine runs a copy of the cast and leaves the body to the replication.
  const int index = souls_combat::Index(e.player);
  if (index < 0 || refii_mod_event_replica() != 0) {
    return;
  }
  const float seconds = souls_combat::Knob("souls_combat.cast_recovery_seconds", kRecoverySeconds);
  if (seconds <= 0.0f) {
    return;
  }
  Plant& plant = g_plants[index];
  if (plant.player != 0 && plant.hero == e.caster) {
    plant.left = seconds;
    return;
  }
  const uint32_t player = refii_mod_component(e.base, e.caster, kPlayerComponent);
  if (player == 0 || refii::mods::LoadU8(e.base, player + kOverridden) != 0u) {
    return;
  }
  plant = Plant{e.caster, player, refii::mods::LoadF32(e.base, player + kMultiplier), seconds};
  refii::mods::StoreF32(e.base, player + kMultiplier, 0.0f);
  refii::mods::StoreU8(e.base, player + kOverridden, 1u);
}
REFII_MOD_ON(SpellLaunched, OnSpellLaunched);

void OnTick(refii::mods::TickEvent& e) {
  for (Plant& plant : g_plants) {
    if (plant.player == 0) {
      continue;
    }
    plant.left -= e.seconds;
    if (plant.left <= 0.0f || !refii::mods::IsPlayerHero(e.base, plant.hero)) {
      Release(e.base, plant);
    }
  }
}
REFII_MOD_ON(Tick, OnTick);

}  // namespace
