#include "vfs_hooks_private.h"

REX_EXTERN(sub_82B53438) {
  REX_FUNC_PROLOGUE();

  if (refii::vfs::TrySilentLargePageReserve(ctx, base)) {
    return;
  }

  __imp__sub_82B53438(ctx, base);
}
