// Souls Combat — a committed swing: once the hero's strike has started, nothing interrupts it
// until later in the animation than the game allows.
//
// The rule, from the binary (TU1 / disc, named in versions/*/refii_config.toml):
//   rex_CAction_OnAnimEvent (0x82709938 / 0x826DF198): every action's animation-event handler,
//     r3 = the live action, r4 = the animation id, r6 = the event's value string (`**r6` is the
//     text). For "ALLOW_ACTION_INTERRUPT" it does `if (!*(action+0x4C)) *(action+0x4D) = 1`
//     (0x82709cac / 0x82709cb8) and nothing else; the interrupt rule sub_822CB150 lets a new
//     action of the same priority in only once that byte is set (strikes, rolls and the plain
//     blocks all play at priority 6; the preemptive block and the parry at 8 need no permission).
//   The action's clock: vtable slot 7 (sub_8226D418) returns the elapsed animation seconds.
//   The live strike is the clone the manager runs, recorded by the CommitSwing hook. A stopped
//   action has +0x4B set (sub_82709FD8); the manager's current action is at manager+0x10.
// So the permission is not written when the event fires; it is written by a tick kExtraCommit
// seconds of animation later, only while the same strike is still the current action.

#include "core/refii_mod.h"
#include "souls_combat.h"

#include <cstring>

REFII_MOD_ORIGINAL(rex_CAction_OnAnimEvent);

namespace {

constexpr float kExtraCommitSeconds = 0.25f;
constexpr uint32_t kActionManagerComponent = 49;
constexpr uint32_t kElapsedSlot = 7;

struct Deferred {
  uint32_t action = 0;
  float allow_at = 0.0f;
};

Deferred g_deferred[2];

/// The event's text, `**r6`, or null.
const char* EventValue(uint8_t* base, uint32_t value_object) {
  if (value_object == 0) {
    return nullptr;
  }
  const uint32_t holder = refii::mods::LoadU32(base, value_object);
  const uint32_t text = holder != 0 ? refii::mods::LoadU32(base, holder) : 0u;
  return text != 0 ? reinterpret_cast<const char*>(base + text) : nullptr;
}

float Elapsed(PPCContext& ctx, uint8_t* base, uint32_t action) {
  PPCContext work = ctx;
  return refii::mods::CallVirtual(work, base, action, kElapsedSlot) ? static_cast<float>(work.f1.f64) : 0.0f;
}

void Allow(uint8_t* base, uint32_t action) {
  if (refii::mods::LoadU8(base, action + 0x4Cu) == 0) {
    refii::mods::StoreU8(base, action + 0x4Du, 1u);
  }
}

void ReleaseDeferred(PPCContext* ctx, uint8_t* base, float) {
  for (int player = 0; player < 2; ++player) {
    Deferred& d = g_deferred[player];
    if (d.action == 0) {
      continue;
    }
    const uint32_t hero = refii_mod_player_hero(base, player + 1);
    const uint32_t manager = hero != 0 ? refii_mod_component(base, hero, kActionManagerComponent) : 0u;
    const bool current = manager != 0 && refii::mods::LoadU32(base, manager + 0x10u) == d.action &&
                         refii::mods::LoadU8(base, d.action + 0x4Bu) == 0;
    if (!current) {
      d.action = 0;
      continue;
    }
    if (Elapsed(*ctx, base, d.action) >= d.allow_at) {
      Allow(base, d.action);
      d.action = 0;
    }
  }
}
REFII_MOD_ON_TICK(ReleaseDeferred);

}  // namespace

REX_EXTERN(rex_CAction_OnAnimEvent) {
  REX_FUNC_PROLOGUE();
  const uint32_t action = ctx.r3.u32;
  const char* value = EventValue(base, ctx.r6.u32);
  if (value != nullptr && std::strcmp(value, "ALLOW_ACTION_INTERRUPT") == 0) {
    for (int player = 0; player < 2; ++player) {
      if (action != 0 && action == souls_combat::g_committed_strike[player]) {
        g_deferred[player] = {action, Elapsed(ctx, base, action) + souls_combat::Knob("souls_combat.commit_extra_seconds", kExtraCommitSeconds)};
        return;
      }
    }
  }

  __imp__rex_CAction_OnAnimEvent(ctx, base);
}
