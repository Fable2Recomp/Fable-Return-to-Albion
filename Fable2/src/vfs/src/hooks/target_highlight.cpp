#include "vfs_hooks_private.h"

#include <cstdint>

#include "hooks/ppc_externs.h"

// Target highlight colour, taken at the guest's setter rather than polled.
//
// `sub_825738F0` stores four bytes at `r31+0x60..0x63` (0x82573900..0x82573988), r31 the
// CECGraphicAppearance component. Polling that field cannot work: {0,0,0,255} is the clear
// value, written by `sub_82429BB0` via `sub_825738F0(comp3, &byte_83319B68)`, and the colour is
// written only while the ramp runs — `sub_822A3BF8` stops calling the setter once
// `dword_83319650 - *(comp+76)` passes `flt_83319490[kind] * 15.0`.
//
// Hooked calls:
//
//   sub_825738F0(CECGraphicAppearance* r3, const uint8_t rgba[4] r4)
//       States the colour and the renderable. The component-159 redirect (a troll weak spot
//       sends its highlight to another entity) is already resolved, so r3 is the component
//       that should light up.
//
//   sub_82427FD0(CECTargeted* r3, ...)
//       States that an entity became or stopped being a target. `CECTargeted+33` is player one
//       and `+34` player two; this is their only writer. Counted only.
//
// Keyed by the component pointer, which the host already resolves per entity.

// THE TABLE IS NOT HERE. It lives in src/core/target_highlight_state.cpp, which links into
// librefii_vfs: this file compiles into the payload and the host bridge reads what it records,
// so a table beside these hooks is one the host cannot reach on Windows. See that file.

REX_EXTERN(rex_CECGraphicAppearance_SetHighlightColour) {
  refii::vfs::EnsureAlternateSignalStack();
  REX_FUNC_PROLOGUE();

  // The four bytes in store order (r4[0] lands at +0x60, r4[1] at +0x61, ...), packed
  // big-endian so R is the top byte.
  const uint32_t at = ctx.r4.u32;
  const uint32_t rgba = (static_cast<uint32_t>(refii::vfs::LoadGuestU8(base, at)) << 24) |
                        (static_cast<uint32_t>(refii::vfs::LoadGuestU8(base, at + 1u)) << 16) |
                        (static_cast<uint32_t>(refii::vfs::LoadGuestU8(base, at + 2u)) << 8) |
                        static_cast<uint32_t>(refii::vfs::LoadGuestU8(base, at + 3u));
  refii::vfs::NoteHighlightColour(ctx.r3.u32, rgba);

  REFII_VFS_NATIVE_PASSTHROUGH(rex_CECGraphicAppearance_SetHighlightColour);
}

REX_EXTERN(rex_lhECTargeted_SetTargetedByPlayer) {
  refii::vfs::EnsureAlternateSignalStack();
  REX_FUNC_PROLOGUE();
  refii::vfs::NoteHighlightTarget();
  REFII_VFS_NATIVE_PASSTHROUGH(rex_lhECTargeted_SetTargetedByPlayer);
}
