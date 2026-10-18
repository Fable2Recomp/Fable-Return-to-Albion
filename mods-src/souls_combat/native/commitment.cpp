// Souls Combat — a committed swing: once the hero's strike has started, nothing interrupts it
// until later in the animation than the game allows.
//
// The rule, from the binary (TU1 / disc): every action's animation-event handler, for
// "ALLOW_ACTION_INTERRUPT", does `if (!*(action+0x4C)) *(action+0x4D) = 1` (0x82709cac) and
// nothing else; the interrupt rule sub_822CB150 lets a new action of the same priority in only
// once that byte is set (strikes, rolls and the plain blocks all play at priority 6; the
// preemptive block and the parry at 8 need no permission). The action's clock is vtable slot 7
// (elapsed animation seconds); a stopped action has +0x4B set; the manager's current action is
// at manager+0x10.
//
// The event (core/refii_mod_events.h): AnimEvent, with the event's text, the live action and
// the action's owner. For the hero's committed strike (recorded by stamina.cpp from MeleeSwing,
// matched by the owner's player AND the action, since a freed action's address is reused by the
// next action anyone plays) the permission is cancelled when the event fires and written by the
// tick kExtraCommit seconds of animation later, only while the same strike is still the current
// action. The record is cleared once the permission is granted or the strike is no longer current.

#include "core/refii_mod.h"
#include "souls_combat.h"

#include <cstring>

namespace {

constexpr float kExtraCommitSeconds = 0.25f;
constexpr uint32_t kActionManagerComponent = 49;
constexpr uint32_t kElapsedSlot = 7;

struct Deferred {
  uint32_t action = 0;
  float allow_at = 0.0f;
};

Deferred g_deferred[2];

float Elapsed(PPCContext& ctx, uint8_t* base, uint32_t action) {
  PPCContext work = ctx;
  return refii::mods::CallVirtual(work, base, action, kElapsedSlot) ? static_cast<float>(work.f1.f64) : 0.0f;
}

void Allow(uint8_t* base, uint32_t action) {
  if (refii::mods::LoadU8(base, action + 0x4Cu) == 0) {
    refii::mods::StoreU8(base, action + 0x4Du, 1u);
  }
}

void ReleaseDeferred(refii::mods::TickEvent& e) {
  for (int player = 0; player < 2; ++player) {
    Deferred& d = g_deferred[player];
    if (d.action == 0) {
      continue;
    }
    const uint32_t hero = refii_mod_player_hero(e.base, player + 1);
    const uint32_t manager = hero != 0 ? refii_mod_component(e.base, hero, kActionManagerComponent) : 0u;
    const bool current = manager != 0 && refii::mods::LoadU32(e.base, manager + 0x10u) == d.action &&
                         refii::mods::LoadU8(e.base, d.action + 0x4Bu) == 0;
    if (!current) {
      d.action = 0;
      souls_combat::g_committed_strike[player] = 0;
      continue;
    }
    if (Elapsed(*e.ctx, e.base, d.action) >= d.allow_at) {
      Allow(e.base, d.action);
      d.action = 0;
      souls_combat::g_committed_strike[player] = 0;
    }
  }
}
REFII_MOD_ON(Tick, ReleaseDeferred);

void OnAnimEvent(refii::mods::AnimEventEvent& e) {
  if (e.action == 0 || e.name == nullptr || std::strcmp(e.name, "ALLOW_ACTION_INTERRUPT") != 0) {
    return;
  }
  const int player = souls_combat::Index(e.player);
  if (player < 0 || e.action != souls_combat::g_committed_strike[player]) {
    return;
  }
  g_deferred[player] = {e.action, Elapsed(*e.ctx, e.base, e.action) +
                                      souls_combat::Knob("souls_combat.commit_extra_seconds", kExtraCommitSeconds)};
  e.cancel = true;
}
REFII_MOD_ON(AnimEvent, OnAnimEvent);

}  // namespace
