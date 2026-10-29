#include "vfs_hooks_private.h"

#include <cstdint>

#include "hooks/ppc_externs.h"
#include "core/guest_build.h"

// DECALS, taken where the guest states them to its render thread.
//
// `sub_822C5B10` (the sim's per-frame world submission) turns each AddDecal and footstep into a
// render command; on the render thread `sub_82A40610` (vtable 0x820F44F8 slot +8) resolves the
// owner and queues it into the std::list at 0x8349FB9C (`sub_82A63C28`, an 80-byte node). That
// list is drained only by `sub_82181298`, two a frame, inside a render pass this host never
// runs, so every footstep of a session stayed queued. The command is the statement: it is
// taken whole here (cmd+8 owner index or -1 -> twin `dword_8349F7AC[idx]`, +12 texture name
// hash, +16 position, +32 direction, +48/+52 half sizes, +56 life, +60 flag; 0x82a40620..0x82a40660)
// and not queued, because the host draws decals itself.

REX_EXTERN(rex_lhDecal_ExecuteRenderCommand) {
  refii::vfs::EnsureAlternateSignalStack();
  REX_FUNC_PROLOGUE();
  const uint32_t command = ctx.r3.u32;
  if (command != 0u) {
    uint32_t words[refii::vfs::kDecalWordCount] = {};
    const uint32_t index = refii::vfs::LoadGuestU32(base, command + 8u);
    uint32_t owner = 0u;
    if (index != 0xFFFFFFFFu) {
      const uint32_t twins = refii::vfs::LoadGuestU32(base, refii::guest::Address(0x8349F7ACu));
      owner = twins != 0u ? refii::vfs::LoadGuestU32(base, twins + index * 4u) : 0u;
    }
    words[1] = owner;
    words[2] = refii::vfs::LoadGuestU32(base, command + 12u);
    for (uint32_t i = 0u; i < 4u; ++i) {
      words[3 + i] = refii::vfs::LoadGuestU32(base, command + 16u + i * 4u);
      words[7 + i] = refii::vfs::LoadGuestU32(base, command + 32u + i * 4u);
    }
    words[11] = refii::vfs::LoadGuestU32(base, command + 48u);
    words[12] = refii::vfs::LoadGuestU32(base, command + 52u);
    words[13] = refii::vfs::LoadGuestU32(base, command + 56u);
    words[14] = refii::vfs::LoadGuestU8(base, command + 60u);
    refii::vfs::NoteDecal(words);
  }
  ctx.r3.u64 = 0u;
}

// Clear all (`sub_82A406D8`, vtable 0x820A6A88 slot +8): the region's teardown.
REX_EXTERN(rex_lhDecal_ExecuteClearCommand) {
  refii::vfs::EnsureAlternateSignalStack();
  REX_FUNC_PROLOGUE();
  refii::vfs::ClearDecals();
  REFII_VFS_NATIVE_PASSTHROUGH(rex_lhDecal_ExecuteClearCommand);
}

// A render twin releasing its decals (`sub_82AA3D38`, r3 = the twin): its owner is going.
REX_EXTERN(rex_lhRenderTwin_ReleaseDecals) {
  refii::vfs::EnsureAlternateSignalStack();
  REX_FUNC_PROLOGUE();
  refii::vfs::DropDecalsOf(ctx.r3.u32);
  REFII_VFS_NATIVE_PASSTHROUGH(rex_lhRenderTwin_ReleaseDecals);
}
