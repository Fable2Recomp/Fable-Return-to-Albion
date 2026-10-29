#include "vfs_hooks_private.h"

REX_EXTERN(rex_lhMem_ReserveCommitAlignedVirtual) {
  REX_FUNC_PROLOGUE();

  if (refii::vfs::TrySilentLargePageReserve(ctx, base)) {
    return;
  }

  __imp__rex_lhMem_ReserveCommitAlignedVirtual(ctx, base);
}
