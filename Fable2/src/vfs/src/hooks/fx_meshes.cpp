#include "vfs_hooks_private.h"

#include <cstdint>

#include "hooks/ppc_externs.h"
#include "core/guest_build.h"

// FX MESH PARTICLES, taken where the guest states them.
//
// An effect's mesh renderer (`sub_821CEA80`, kind 4) creates a render twin per live mesh particle
// through a descriptor `sub_82456D58` fills (its only caller: the descriptor in r3, flags 0x800 for
// a displacement mesh in r4) and destroys it with `sub_82175918(handle)` (queue command 4) the frame
// the particle is gone. The descriptor is noted here; its twin handle is descriptor +4.
//
// Each frame the renderer states the particle's tint and alpha to that handle as model commands
// through `lhRenderCommandStream_PostModelCommand(handle, command, queue)` (0x8222AAC8): command 17
// (vtable 0x820B6EFC, `GetECType` `li r3, 0x11`) the tint at +16..+28, command 16 (vtable
// 0x820B6EF0, `li r3, 0x10`) the alpha at +4 = particle alpha x the effect's (`sub_821CEA80`,
// posted with alpha reference -1). Queued or not, a command reaches its twin through
// `Render_ApplyCommandToHandle(handle r3, command r4, time f1)` (`sub_822222E0`; disc
// `sub_82222400`): from the poster when unqueued (0x8222ab64), from the stream drain when queued
// (0x822e0690), where each serializer (`sub_82A44900`, `sub_82A44AC8`) has copied the command
// with its vtable and fields. Taken there, for every twin; the host bridge asks by handle.

REX_EXTERN(rex_lhFx_CreateMeshDescriptor) {
  refii::vfs::EnsureAlternateSignalStack();
  REX_FUNC_PROLOGUE();
  refii::vfs::NoteFxMesh(ctx.r3.u32);
  REFII_VFS_NATIVE_PASSTHROUGH(rex_lhFx_CreateMeshDescriptor);
}

REX_EXTERN(rex_lhRender_ApplyCommandToHandle) {
  refii::vfs::EnsureAlternateSignalStack();
  REX_FUNC_PROLOGUE();
  const uint32_t handle = ctx.r3.u32;
  const uint32_t command = ctx.r4.u32;
  if (command != 0u) {
    const uint32_t vtable = refii::vfs::LoadGuestU32(base, command);
    if (vtable == refii::guest::Address(0x820B6EF0u)) {
      refii::vfs::NoteTwinAlpha(handle, refii::vfs::LoadGuestU32(base, command + 4u));
    } else if (vtable == refii::guest::Address(0x820B6EFCu)) {
      uint32_t tint[4];
      for (uint32_t lane = 0u; lane < 4u; ++lane) {
        tint[lane] = refii::vfs::LoadGuestU32(base, command + 16u + lane * 4u);
      }
      refii::vfs::NoteTwinTint(handle, tint);
    }
  }
  REFII_VFS_NATIVE_PASSTHROUGH(rex_lhRender_ApplyCommandToHandle);
}

// A twin destroyed (`sub_82175918`, r3 = its handle): any FX descriptor naming it goes, and so
// does what was stated to the handle.
REX_EXTERN(rex_lhRenderTwin_Destroy) {
  refii::vfs::EnsureAlternateSignalStack();
  REX_FUNC_PROLOGUE();
  const uint32_t handle = ctx.r3.u32;
  refii::vfs::ForgetTwin(handle);
  uint32_t descriptors[256];
  const uint32_t count = refii::vfs::FxMeshes(descriptors, 256u);
  for (uint32_t i = 0u; i < count; ++i) {
    if (refii::vfs::LoadGuestU32(base, descriptors[i] + 4u) == handle) {
      refii::vfs::DropFxMesh(descriptors[i]);
    }
  }
  REFII_VFS_NATIVE_PASSTHROUGH(rex_lhRenderTwin_Destroy);
}
