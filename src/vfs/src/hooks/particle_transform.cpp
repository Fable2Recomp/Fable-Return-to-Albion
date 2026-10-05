#include "vfs_hooks_private.h"

#include <cstdint>

#include "hooks/ppc_externs.h"

// The particle transform setters, recorded per PSInstance.
//
// `instance+0x50` is an authored local translation; the world position is stated into each
// emitter at `emitter+0x50` through a call:
//
//     sub_832325A8(PSInstance* r3, position v1, velocity v2)
//       832325c8  lwz     r27, 0xC(r3)        ; emitters begin
//       832325cc  vmr128  v127, v1            ; the stated position
//       832325d0  lwz     r23, 0x10(r3)       ; emitters end
//       832325e8  li      r24, 0x50
//       83232600  lwz     r31, 0(r27)         ; each emitter
//       8323261c  stb     r28, 0xB0(r31)      ; transform dirty
//       83232628  stvx128 v12, r31, r24       ; emitter[0x50] = position
//
// `sub_83231C40(PSInstance* r3, quaternion r4:r5)` states the orientation the same way, through
// `sub_83228D78`, which spills the pair and stores it at `emitter+0x10` before recomposing the
// matrix at `emitter+0x20`:
//
//     83228d8c  std  r4, arg_20(r1)     ; the quaternion arrives in GPRs, not a VMX register
//     83228d90  std  r5, arg_28(r1)
//     83228da4  ld   r6, 0(r11)
//     83228db0  ld   r7, 8(r11)
//     83228dc0  std  r7, 0x18(r31)      ; emitter[0x18] = r5
//     83228dc4  std  r6, 0x10(r31)      ; emitter[0x10] = r4
//     83228da8  addi r3, r31, 0x20      ; then compose rows 0..3 at emitter+0x20
//
// This is an instrument, not the placement source. These two setters are not the only writers
// of an emitter's world transform; the composed matrix at `emitter+0x20` is 64 bytes, so its
// row 3 is `emitter+0x50`, and three more writers land in it:
//
//     sub_8322E4D0  8322e588 li r6,0x50 / 8322e598 stvx128 v11,r31,r6   PER TICK, type-5 path
//     sub_83230880  83230904 li r7,0x50 / 8323091c stvx128 v11,r31,r7   PER TICK, type-9 spline
//     sub_8322B090 / sub_8322B848                                       the authored .pfx load
//
// The two per-tick writers run later in the frame, from the manager update
// (`sub_83234C38` -> `sub_83233890` -> emitter vtable slot +36), so they WIN over anything these
// hooks saw; and an effect whose owner never fires component message 10 keeps the transform its
// .pfx authored without either setter ever being called. So the host places effects from
// `node+0x20`, where every writer lands, and reads this only to measure the setters' coverage.
//
// The instance pointer in r3 is the identity — the same one the host's walk reports, since a
// node's `+0xE8` names the effect instance whose emitter vector is at `+0xC`/`+0x10`.

// THE TABLE IS NOT HERE. It lives in src/core/stated_fx_state.cpp, which links into
// librefii_vfs: this file compiles into the payload and the host bridge reads what it records,
// so a table beside these hooks is one the host cannot reach on Windows. See that file.

REX_EXTERN(rex_CParticleSystemEntityInstance_SetPositionAndVelocity) {
  refii::vfs::EnsureAlternateSignalStack();
  REX_FUNC_PROLOGUE();

  // All four lanes of v1 in the recompiler's register order; the host decides which is x.
  refii::vfs::NoteFxPosition(ctx.r3.u32, ctx.v1.f32);

  REFII_VFS_NATIVE_PASSTHROUGH(rex_CParticleSystemEntityInstance_SetPositionAndVelocity);
}

REX_EXTERN(rex_CParticleSystemEntityInstance_SetOrientation) {
  refii::vfs::EnsureAlternateSignalStack();
  REX_FUNC_PROLOGUE();

  // r3 is the PSInstance. `sub_825F8900` calls this inside `sub_825F81E0`, synchronously on this
  // thread, for every spell FX it places, so it names a watched effect's instance (see
  // hooks/spell_fx_lifetime.cpp). A no-op unless a capture window is open.
  refii::vfs::NoteSpellFxInstance(ctx.r3.u32);

  // r4 then r5, high word first: `sub_83228D78` stores r4 at `emitter+0x10` and r5 at
  // `emitter+0x18`, so the four components are the register halves in that order.
  const uint32_t lanes[4] = {
      static_cast<uint32_t>(ctx.r4.u64 >> 32), static_cast<uint32_t>(ctx.r4.u64),
      static_cast<uint32_t>(ctx.r5.u64 >> 32), static_cast<uint32_t>(ctx.r5.u64)};
  refii::vfs::NoteFxOrientation(ctx.r3.u32, lanes);

  REFII_VFS_NATIVE_PASSTHROUGH(rex_CParticleSystemEntityInstance_SetOrientation);
}
