// Souls Combat — LT toggles the lock-on instead of being held.
//
// THE LOCK IS OURS: one state per hero, flipped by the LT PRESS itself, and the game's two lock
// switches are set to match it. Two earlier versions keyed off the game's own look-at flag and
// both stuck locked: that flag is set only by a camera event on LT down, which is not delivered
// again while the lock holds, so no second press was ever seen.
//
// The seams, from the binary (TU1 / disc, named in versions/*/refii_config.toml):
//   rex_CPlayerActionReceiver_ProcessAction (0x826E5B30 / 0x827428B0): the hero's GameAction
//     receiver, r3 = the receiver, r4 = the 24-byte action message whose first word is the
//     GameAction (`lwz r11,0(r4)`, switched on `- 0x14`). LT is 49, TARGET_LOCK_SET, on press
//     and 50, TARGET_LOCK_RELEASE, on release; case 50 clears the look-at flag. The receiver's
//     entity is a weak handle at +8 resolved as `*(*(r3+0xC))` (sub_82173E38: `v1 = a1[1]; if
//     (v1 && *v1) return *v1`).
//   rex_CPlayer_SetLookAtButtonDown (0x823E9F10 / 0x823C8940): the only writer of the player's
//     look-at flag (`stb r29,0x20(r30)`), the byte every lock-camera script reads. Hooked so the
//     flag always equals our lock, whoever asks.
//   The FORCE byte, targeting component 77 +0x14 (Targeting.SetForceTargetLock, sub_8290F078
//     `stb r4,0x14(r3)`): while it is set and a target exists the component's update adds player
//     mode 4, TARGET_LOCK — the mode that outlined the target when the first version forced it.
// The player object is the hero's component 33, which keeps its owner entity at +4 (`stw
// r4,4(r31)` in its constructor, 0x823e0110).
//
// THE LOCK ENDS WHEN THERE IS NOTHING TO LOCK. The game never clears the force byte itself: with
// it set, the targeting component's update (sub_82181EA0 / sub_821818E8) re-selects a target
// within 8 ticks of the old one dying, and when none is found it only drops player mode 4 — the
// force byte and the look-at flag stay, and the camera is locked on nothing. So a tick drops our
// lock after half a second without a valid target, read the way the game reads it (same offsets
// in both builds):
//   target  = *(*(comp77 + 12) + 28) -> *cb      GetTarget, sub_821E66F0 / sub_821E62B0
//   in world: (target + 0x90) & 0x40               selector sub_82844950 @0x82844b28 / 0x8277b488
//   alive:    component 20 of target, byte +0x18   same selector @0x82844c44 / 0x8277b5a4
//             (a target without component 20 is not a creature; it counts as alive)

#include "core/refii_mod.h"
#include "souls_combat.h"

REFII_MOD_ORIGINAL(rex_CPlayerActionReceiver_ProcessAction);
REFII_MOD_ORIGINAL(rex_CPlayer_SetLookAtButtonDown);

namespace {

constexpr uint32_t kTargetLockSet = 49;
constexpr uint32_t kTargetLockRelease = 50;
constexpr uint32_t kPlayerComponent = 33;
constexpr uint32_t kTargetingComponent = 77;
constexpr uint32_t kForceLock = 0x14;

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

/// The game's own test for "a target worth locking": present, in the world, and not killed.
bool HasValidTarget(uint8_t* base, uint32_t hero) {
  const uint32_t targeting = refii_mod_component(base, hero, kTargetingComponent);
  if (targeting == 0) {
    return false;
  }
  const uint32_t strategy = refii::mods::LoadU32(base, targeting + 12u);
  const uint32_t block = strategy != 0 ? refii::mods::LoadU32(base, strategy + 28u) : 0u;
  const uint32_t target = block != 0 ? refii::mods::LoadU32(base, block) : 0u;
  if (target == 0 || (refii::mods::LoadU8(base, target + 0x90u) & 0x40u) == 0) {
    return false;
  }
  const uint32_t creature = refii_mod_component(base, target, kCreatureComponent);
  return creature == 0 || refii::mods::LoadU8(base, creature + 0x18u) != 0;
}

uint32_t ReceiverHero(uint8_t* base, uint32_t receiver) {
  const uint32_t control = refii::mods::LoadU32(base, receiver + 0xCu);
  return control != 0 ? refii::mods::LoadU32(base, control) : 0u;
}

/// Puts the game's two lock switches where our lock says: the force byte, then the look-at flag
/// through the game's own writer (which also re-picks the camera mode).
void Apply(PPCContext& ctx, uint8_t* base, uint32_t hero, bool on) {
  if (const uint32_t targeting = refii_mod_component(base, hero, kTargetingComponent)) {
    refii::mods::StoreU8(base, targeting + kForceLock, on ? 1u : 0u);
  }
  if (const uint32_t player = refii_mod_component(base, hero, kPlayerComponent)) {
    PPCContext work = ctx;
    work.r3.u32 = player;
    work.r4.u64 = on ? 1u : 0u;
    __imp__rex_CPlayer_SetLookAtButtonDown(work, base);
  }
}

void DropLostLocks(PPCContext* ctx, uint8_t* base, float seconds) {
  for (int i = 0; i < 2; ++i) {
    const uint32_t hero = g_locked[i];
    if (hero == 0) {
      continue;
    }
    if (!refii::mods::IsPlayerHero(base, hero)) {
      // The hero itself is gone (a level change); nothing to release on.
      g_locked[i] = 0;
      continue;
    }
    if (HasValidTarget(base, hero)) {
      g_without_target[i] = 0.0f;
      continue;
    }
    g_without_target[i] += seconds;
    if (g_without_target[i] >= souls_combat::Knob("souls_combat.lock_drop_seconds", kDropAfterSeconds)) {
      g_locked[i] = 0;
      Apply(*ctx, base, hero, false);
      REXLOG_INFO("Souls Combat: lock-on OFF (no target)");
    }
  }
}
REFII_MOD_ON_TICK(DropLostLocks);

}  // namespace

REX_EXTERN(rex_CPlayerActionReceiver_ProcessAction) {
  REX_FUNC_PROLOGUE();
  const uint32_t action = refii::mods::LoadU32(base, ctx.r4.u32);
  if (action == kTargetLockSet || action == kTargetLockRelease) {
    const uint32_t hero = ReceiverHero(base, ctx.r3.u32);
    if (refii::mods::IsPlayerHero(base, hero)) {
      if (action == kTargetLockSet) {
        const bool on = !IsLocked(hero);
        SetLocked(hero, on);
        Apply(ctx, base, hero, on);
        REXLOG_INFO("Souls Combat: lock-on {}", on ? "ON" : "OFF");
        return;
      }
      // While locked the release ends nothing — the next press does. Unlocked, the game's own
      // release handling runs (it does more than clear the flag, which the hook below holds at 0).
      if (IsLocked(hero)) {
        return;
      }
    }
  }

  __imp__rex_CPlayerActionReceiver_ProcessAction(ctx, base);
}

REX_EXTERN(rex_CPlayer_SetLookAtButtonDown) {
  REX_FUNC_PROLOGUE();
  const uint32_t hero = refii::mods::LoadU32(base, ctx.r3.u32 + 4u);
  if (refii::mods::IsPlayerHero(base, hero)) {
    // The flag is the lock, whoever asks — the camera's press event included.
    ctx.r4.u64 = IsLocked(hero) ? 1u : 0u;
  }

  __imp__rex_CPlayer_SetLookAtButtonDown(ctx, base);
}
