// Souls Combat — LT toggles the lock-on instead of being held.
//
// THE LOCK IS OURS: one state per hero, flipped by the LT PRESS itself, and the game's lock is
// set to match it through the mod API's own lock calls (core/refii_mod.h, refii_mod_lock_target /
// refii_mod_release_lock: the targeting component's force byte and the player's look-at flag, what
// a held LT states). Two earlier versions keyed off the game's own look-at flag and both stuck
// locked: that flag is set only by a camera event on LT down, which is not delivered again while
// the lock holds, so no second press was ever seen.
//
// The event (core/refii_mod_events.h): GameAction, a GameAction reaching a player hero's
// receiver — LT is 49, TARGET_LOCK_SET, on press and 50, TARGET_LOCK_RELEASE, on release. The
// receiver (sub_826E5B30) has no case for 49 at all, and its case 50 already skips the lock
// removal while the force byte is set (`if (v6[20]) goto LABEL_29` 0x826e5db4) before clearing
// two bytes on the player's handle object (0x826e5fe0): neither action is cancelled here — the
// press flips our lock, and the game's own handling runs unchanged.
//
// The one function hooked by name, which no event covers (TU1 / disc, named in
// versions/*/refii_config.toml):
//   rex_CPlayer_SetLookAtButtonDown (0x823E9F10 / 0x823C8940): the only writer of the player's
//     look-at flag (`stb r29,0x20(r30)`), the byte every lock-camera script reads. Hooked so the
//     flag always equals our lock, whoever asks — the camera's press event, and the API's own
//     lock and release, which write it through this same function. The player object is the hero's
//     component 33, which keeps its owner entity at +4 (`stw r4,4(r31)` in its constructor,
//     0x823e0110).
//
// THE LOCK ENDS WHEN THERE IS NOTHING TO LOCK. The game never clears the force byte itself: with
// it set, the targeting component's update (sub_82181EA0 / sub_821818E8) re-selects a target
// within 8 ticks of the old one dying, and when none is found it only drops player mode 4 — the
// force byte and the look-at flag stay, and the camera is locked on nothing. So a tick drops our
// lock after half a second without a valid target: the target the API reports locked
// (refii_mod_locked_target, the component's own GetTarget while the force byte or mode 4 holds),
// alive, and — for a creature — not killed (component 20, byte +0x18, the selector's own test at
// sub_82844950 0x82844c44 / 0x8277b5a4; a target without component 20 is not a creature and counts
// as alive).
//
// THE WHEEL SWITCHES TARGET. Each tick a hero's lock holds it is stated to switch by the mouse wheel
// (refii_mod_wheel_switches_target, core/refii_mod.h): on the keyboard and mouse, moving the mouse
// never switches target while locked, and a wheel notch is one switch through the camera's own
// CheckForBetterTargetInDirection — up the next target left, down the next target right. A pad's
// right-stick flick is untouched.

#include "core/refii_mod.h"
#include "souls_combat.h"

REFII_MOD_ORIGINAL(rex_CPlayer_SetLookAtButtonDown);

namespace {

constexpr uint32_t kTargetLockSet = 49;

/// The heroes currently locked, by entity, and how long each has been locked on nothing.
uint32_t g_locked[2] = {0, 0};
float g_without_target[2] = {0.0f, 0.0f};

constexpr uint32_t kCreatureComponent = 20;
constexpr float kDropAfterSeconds = 0.5f;

bool IsLocked(uint32_t hero) { return hero != 0 && (g_locked[0] == hero || g_locked[1] == hero); }

void SetLocked(uint32_t hero, bool on) {
  for (int i = 0; i < 2; ++i) {
    if (g_locked[i] == hero) {
      g_locked[i] = 0;
    }
  }
  if (on) {
    for (int i = 0; i < 2; ++i) {
      if (g_locked[i] == 0) {
        g_locked[i] = hero;
        g_without_target[i] = 0.0f;
        return;
      }
    }
  }
}

/// The game's own test for "a target worth locking": held by the lock, in the world, not killed.
bool HasValidTarget(PPCContext& ctx, uint8_t* base, int32_t player) {
  const uint32_t target = refii_mod_locked_target(&ctx, base, player);
  if (!refii_mod_entity_alive(base, target)) {
    return false;
  }
  const uint32_t creature = refii_mod_component(base, target, kCreatureComponent);
  return creature == 0 || refii::mods::LoadU8(base, creature + 0x18u) != 0;
}

/// Puts the game's lock where ours says, through the API: the force byte and the look-at flag
/// (the latter through the game's own writer, hooked below, which also re-picks the camera mode).
void Apply(PPCContext& ctx, uint8_t* base, uint32_t hero, bool on) {
  const int32_t player = refii_mod_player_of(base, hero);
  if (player == 0) {
    return;
  }
  if (on) {
    refii_mod_lock_target(&ctx, base, player, 0);  // the game's own pick
  } else {
    refii_mod_release_lock(&ctx, base, player);
  }
}

void DropLostLocks(refii::mods::TickEvent& e) {
  for (int i = 0; i < 2; ++i) {
    const uint32_t hero = g_locked[i];
    if (hero == 0) {
      continue;
    }
    if (!refii::mods::IsPlayerHero(e.base, hero)) {
      // The hero itself is gone (a level change); nothing to release on.
      g_locked[i] = 0;
      continue;
    }
    refii_mod_wheel_switches_target(e.base, hero);
    if (HasValidTarget(*e.ctx, e.base, refii_mod_player_of(e.base, hero))) {
      g_without_target[i] = 0.0f;
      continue;
    }
    g_without_target[i] += e.seconds;
    if (g_without_target[i] >= souls_combat::Knob("souls_combat.lock_drop_seconds", kDropAfterSeconds)) {
      g_locked[i] = 0;
      Apply(*e.ctx, e.base, hero, false);
      REXLOG_INFO("Souls Combat: lock-on OFF (no target)");
    }
  }
}
REFII_MOD_ON(Tick, DropLostLocks);

void OnGameAction(refii::mods::GameActionEvent& e) {
  if (e.action == kTargetLockSet) {
    const bool on = !IsLocked(e.hero);
    SetLocked(e.hero, on);
    Apply(*e.ctx, e.base, e.hero, on);
    if (on) {
      refii_mod_wheel_switches_target(e.base, e.hero);
    }
    REXLOG_INFO("Souls Combat: lock-on {}", on ? "ON" : "OFF");
  }
  // The release (50) ends nothing while locked: the game's own case honours the force byte, and
  // the look-at flag is held by the hook below.
}
REFII_MOD_ON(GameAction, OnGameAction);

}  // namespace

REX_EXTERN(rex_CPlayer_SetLookAtButtonDown) {
  REX_FUNC_PROLOGUE();
  const uint32_t hero = refii::mods::LoadU32(base, ctx.r3.u32 + 4u);
  if (refii::mods::IsPlayerHero(base, hero)) {
    // The flag is the lock, whoever asks — the camera's press event included.
    ctx.r4.u64 = IsLocked(hero) ? 1u : 0u;
  }

  __imp__rex_CPlayer_SetLookAtButtonDown(ctx, base);
}
