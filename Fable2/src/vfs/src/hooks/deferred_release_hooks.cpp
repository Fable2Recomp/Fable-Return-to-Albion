// The renderer's deferred-release list, released while a view is up.
//
// A released vertex or index buffer waits on the deferred-release list (TU1 `0x8349F9D8`: head +4,
// count +8) until the GPU is done with it. `sub_822B4F38` releases it: it unbinds the device's vertex
// streams and index buffer, polls the streamed textures and walks the list. The renderer calls it only
// on frames drawn with no view (`sub_821C3858`, a region change's loading screen). A co-op joiner keeps
// its view through a region change, so its list grew until a load no longer fit in the graphics heap
// and the allocator stopped at DbgBreakPoint.
//
// The frame body now runs the same release once the list is long, after the frame is drawn. The
// unbinding matters: walking the list alone freed buffers the device still had bound.
#include <cstdint>

#include "vfs_hooks_private.h"
#include "core/guest_build.h"
#include "core/guest_state.h"
#include "hooks/ppc_externs.h"

namespace {

constexpr uint32_t kDeferredReleaseList = 0x8349F9D8u;
constexpr uint32_t kReleaseAt = 256u;

}  // namespace

/// TU1 `sub_821C3858` (disc `sub_821C38C8`): one frame of the renderer, view or loading screen.
REX_EXTERN(rex_lhRenderer_FrameBody) {
  REX_FUNC_PROLOGUE();
  __imp__rex_lhRenderer_FrameBody(ctx, base);

  uint32_t queued = 0;
  if (!refii::vfs::TryLoadGuestU32(base, refii::guest::Address(kDeferredReleaseList) + 8u, &queued) ||
      queued < kReleaseAt) {
    return;
  }
  // ReleasePending takes no arguments (it and its callees read only globals); r3 is FrameBody's result.
  const uint32_t r3 = ctx.r3.u32;
  __imp__rex_lhRenderer_ReleasePending(ctx, base);
  ctx.r3.u32 = r3;
}
