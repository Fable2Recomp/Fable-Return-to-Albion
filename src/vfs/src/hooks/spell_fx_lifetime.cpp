#include "vfs_hooks_private.h"

#include <chrono>
#include <cstdint>
#include <cstdlib>
#include <mutex>

#include "core/guest_state.h"
#include "hooks/ppc_externs.h"

// Raise Dead's creation effect is never killed by the guest and cannot end itself. This is a
// Fable 2 defect; the lifetime below is host policy, since the guest states no kill.
//
// `sub_82755578`, the dead-riser update, spawns one creation effect per corpse and discards the
// handle:
//
//     8275588c  li   r6, 0            ; attach-to-parent = 0, so it is not parented either
//     82755894  lwz  r3, 0x24(r7)
//     82755898  bl   sub_825F81E0     ; returns the spawned FX entity in r3
//     8275589c  lwz  r11, 0x38(r31)   ; r3 overwritten immediately, never stored
//     827558ac  stw  r6, 0x38(r31)    ; only the riser counter is kept
//
// Its siblings do this correctly: `sub_825A6900` stores the handle in a weak-ref slot and
// `sub_825A6A50` destroys it, and the vortex's `sub_8275A548` fades with `sub_82569F20(fx, 1.3)`.
// The dead riser has no field for it at all — `sub_827553A8` (ctor) and `sub_82754D28` (dtor)
// never touch an FX entity. No caller of any of the five kill entry points lies inside the
// riser module (0x82754A88-0x827569A8).
//
// It cannot self-terminate: the node constructor `sub_8322A228` sets `+0xC4 = -1`
// (0x8322A2C4), and the per-tick advance only counts down a positive value:
//
//     8322f210  lwz   r10, 0xC4(r31)
//     8322f214  cmpwi cr6, r10, 0
//     8322f224  ble   cr6, loc_8322F230   ; -1 <= 0, so the decrement is skipped
//     8322f228  addi  r10, r10, -1
//
// so -1 is the sentinel for "no kill pending" and stays -1 forever. The liveness test
// `sub_83228BA8` is `+0xC4 != 0 && (!finite || age <= limit)`, and `fxspl_deadrise_appear` is
// authored with `finite = 0`, which short-circuits the age term, so it stays true.
//
// This keeps the entity the guest discards and ends the effect the way the guest ends its other
// spells: a fade, then the entity destroyed.
//
//   `sub_83232048(PSInstance, frames)` walks the emitter vector at `+0xC`/`+0x10` calling
//   `sub_83228F00(node, frames)`, which sets `+0xC0 = +0xC4 = frames`. From then on
//   `sub_8322F1E0` counts `+0xC4` down, `sub_83228B48` scales the element's intensity by
//   `+196/+192`, and when it reaches zero the liveness test goes false and `sub_83234338`
//   retires the effect and frees its nodes. Frames, not seconds: the seconds form
//   `sub_83232098` converts through `flt_8335F1BC[dword_83496DD4]`, and that bucket is only
//   valid inside the retire pass that sets it. An authored particle frame is 1/30 s always.
//
//   `sub_825F82B8(entity)` then finds component 85 (`cmpwi r11, 0x55` at 0x825F83B0), kills
//   whatever is left with `sub_82569F20(comp, 0.0)` and destroys the entity. It is needed
//   separately from the fade: the particle component's destructor `sub_825699E8` only releases
//   its own reference to the instance, while the registry holds an independent one taken in
//   `sub_82569C28`, so destroying the entity alone would not stop the emission.
//
// Only the FX slot the guest states as `"OnDeadRiserCreationFX"` (the property-name hash passed
// to `sub_825F81E0` in r4) is watched; every other spell kills its own.

namespace {

/// FNV-1 as `Fnv1HashWithBasis` (0x821F3D58) computes it: basis 0x811C9DC5, multiply then xor,
/// no lower-casing (`result = (16777619 * result) ^ v7` over the bytes).
constexpr uint32_t Fnv1(const char* text) {
  uint32_t hash = 0x811C9DC5u;
  for (const char* at = text; *at != '\0'; ++at) {
    hash = (hash * 0x01000193u) ^ static_cast<uint8_t>(*at);
  }

  return hash;
}

/// The string at 0x820D3CB0, spelled exactly as the binary has it.
constexpr uint32_t kOnDeadRiserCreationFx = Fnv1("OnDeadRiserCreationFX");

/// An authored particle frame is 1/30 s for every emitter, whatever the update-rate bucket:
/// `flt_8335F1D4` is 2.0 frames per update at 15 Hz and 1.0 at 30 Hz, and both land on 30.
constexpr float kAuthoredFramesPerSecond = 30.0f;

float EnvSeconds(const char* name, float fallback) {
  const char* text = std::getenv(name);
  if (text == nullptr || *text == '\0') {
    return fallback;
  }

  const double value = std::atof(text);
  return value > 0.0 && value < 60.0 ? static_cast<float>(value) : fallback;
}

/// How long the effect plays at full before the fade begins, and how long the fade takes. Host
/// values: the guest states neither. For reference, the vortex's ground FX fades over 1.3 s
/// (`sub_8275A548`, `sub_82569F20(v6, 1.3)`) and this effect's authored timeline runs to frame 30
/// (one second). Overridable by environment variable.
float PlaySeconds() {
  static const float seconds = EnvSeconds("REFII_DEADRISE_FX_SECONDS", 1.5f);
  return seconds;
}

float FadeSeconds() {
  static const float seconds = EnvSeconds("REFII_DEADRISE_FX_FADE", 0.75f);
  return seconds;
}

using Clock = std::chrono::steady_clock;

struct Pending {
  uint32_t entity;
  uint32_t instance;
  Clock::time_point fade_at;
  Clock::time_point destroy_at;
  bool faded;
};

/// One entry per raised corpse. A full table drops the new entry (that effect keeps the guest's
/// behaviour) rather than evicting, which would end an effect early.
constexpr size_t kMaxPending = 64;

std::mutex g_lock;
Pending g_pending[kMaxPending];
size_t g_pending_count = 0;

/// The PSInstance the effect being created right now placed itself with. `sub_825F8900` calls
/// `sub_83231C40(*(comp85 + 16), ...)` inside `sub_825F81E0`, synchronously and on this thread,
/// so the orientation hook in `particle_transform.cpp` sees exactly this effect's instance while
/// the window is open.
thread_local bool t_capturing = false;
thread_local uint32_t t_captured_instance = 0u;

uint32_t CallGuest(PPCContext& ctx, uint8_t* base, uint32_t address, uint32_t r3, uint32_t r4) {
  PPCFunc* function = refii::vfs::FindGuestFunction(address);
  if (function == nullptr) {
    return 0u;
  }

  PPCContext work = ctx;
  work.r1.u32 = (ctx.r1.u32 - 0x400u) & ~0xFu;
  work.r3.u32 = r3;
  work.r4.u32 = r4;
  function(work, base);
  return work.r3.u32;
}

}  // namespace

namespace refii::vfs {

void NoteSpellFxInstance(uint32_t instance) {
  if (t_capturing && instance != 0u) {
    t_captured_instance = instance;
  }
}

void SweepOrphanSpellFx(PPCContext& ctx, uint8_t* base) {
  struct Due {
    bool destroy;
    uint32_t entity;
    uint32_t instance;
  };

  Due due[kMaxPending];
  size_t due_count = 0;
  {
    std::lock_guard<std::mutex> held(g_lock);
    if (g_pending_count == 0) {
      return;
    }

    const Clock::time_point now = Clock::now();
    size_t kept = 0;
    for (size_t i = 0; i < g_pending_count; ++i) {
      Pending entry = g_pending[i];
      if (entry.faded && now >= entry.destroy_at) {
        // Dropped from the table: the entity is about to stop existing.
        due[due_count++] = Due{true, entry.entity, entry.instance};
        continue;
      }

      if (!entry.faded && now >= entry.fade_at) {
        entry.faded = true;
        due[due_count++] = Due{false, entry.entity, entry.instance};
      }

      g_pending[kept++] = entry;
    }

    g_pending_count = kept;
  }

  // Outside the lock: both calls re-enter the guest, and `sub_825F82B8` destroys an entity.
  for (size_t i = 0; i < due_count; ++i) {
    if (due[i].destroy) {
      CallGuest(ctx, base, 0x825F82B8u, due[i].entity, 0u);
      continue;
    }

    // No instance means the placement never stated one, so there is nothing to fade; the destroy
    // below still runs and `sub_825F82B8` kills whatever is there outright.
    if (due[i].instance != 0u) {
      const auto frames = static_cast<uint32_t>(FadeSeconds() * kAuthoredFramesPerSecond + 0.5f);
      CallGuest(ctx, base, 0x83232048u, due[i].instance, frames != 0u ? frames : 1u);
    }
  }
}

}  // namespace refii::vfs

REX_EXTERN(sub_825F81E0) {
  refii::vfs::EnsureAlternateSignalStack();
  REX_FUNC_PROLOGUE();

  const uint32_t property = ctx.r4.u32;
  const bool watched = property == kOnDeadRiserCreationFx;

  t_capturing = watched;
  t_captured_instance = 0u;
  __imp__sub_825F81E0(ctx, base);
  const uint32_t instance = t_captured_instance;
  t_capturing = false;
  t_captured_instance = 0u;

  const uint32_t entity = ctx.r3.u32;
  if (!watched || entity == 0u) {
    return;
  }

  const Clock::time_point fade_at = Clock::now() + std::chrono::milliseconds(
      static_cast<int64_t>(PlaySeconds() * 1000.0f));

  // Half a second past the fade, so the guest's own retire pass has run before the entity goes.
  const Clock::time_point destroy_at = fade_at + std::chrono::milliseconds(
      static_cast<int64_t>(FadeSeconds() * 1000.0f) + 500);

  std::lock_guard<std::mutex> held(g_lock);
  if (g_pending_count < kMaxPending) {
    g_pending[g_pending_count++] = Pending{entity, instance, fade_at, destroy_at, false};
  }
}
