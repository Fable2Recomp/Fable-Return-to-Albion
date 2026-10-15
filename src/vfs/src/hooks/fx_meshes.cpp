#include "vfs_hooks_private.h"

#include <cstdint>

#include "hooks/ppc_externs.h"
#include "core/guest_build.h"

// FX MESH PARTICLES, taken where the guest states them.
//
// An effect's mesh renderer (`sub_821CEA80`, kind 4) creates a render twin per live mesh particle
// through a descriptor `sub_82456D58` fills (its only caller: the descriptor in r3, flags 0x800 for
// a displacement mesh in r4) and destroys it with `sub_82175918(handle)` (queue command 4) the frame
// the particle is gone. The descriptor is noted here; the twin it names (descriptor +4 -> the twin
// table `dword_8349F7AC`) holds the transform, tint and alpha the renderer's messages set, which
// the host bridge reads.

REX_EXTERN(rex_lhFx_CreateMeshDescriptor) {
  refii::vfs::EnsureAlternateSignalStack();
  REX_FUNC_PROLOGUE();
  refii::vfs::NoteFxMesh(ctx.r3.u32);
  REFII_VFS_NATIVE_PASSTHROUGH(rex_lhFx_CreateMeshDescriptor);
}

// A twin destroyed (`sub_82175918`, r3 = its handle): any FX descriptor naming it goes.
REX_EXTERN(rex_lhRenderTwin_Destroy) {
  refii::vfs::EnsureAlternateSignalStack();
  REX_FUNC_PROLOGUE();
  const uint32_t handle = ctx.r3.u32;
  uint32_t descriptors[256];
  const uint32_t count = refii::vfs::FxMeshes(descriptors, 256u);
  for (uint32_t i = 0u; i < count; ++i) {
    if (refii::vfs::LoadGuestU32(base, descriptors[i] + 4u) == handle) {
      refii::vfs::DropFxMesh(descriptors[i]);
    }
  }
  REFII_VFS_NATIVE_PASSTHROUGH(rex_lhRenderTwin_Destroy);
}
