// refii — the debug channel's switch for the guest's own scene draw, as SHARED STATE.
//
// In librefii_runtime, not the PPC hook set, for the reason target_highlight_state.cpp gives: the
// HOST writes (the debug channel through the host bridge) and the PAYLOAD reads (the DrawScene
// hook in Fable2/src/vfs/src/hooks/boot_lifecycle.cpp). The host may not link the payload, so the switch lives
// here and the payload reaches it through the C entry in core/payload_bridge.h.

#include <atomic>
#include <cstdint>

#include "core/payload_bridge.h"

namespace {
// -1 follows REFII_GUEST_RENDER, 0 off, 1 on.
std::atomic<int32_t> g_guest_render_override{-1};
}  // namespace

extern "C" {
int32_t refii_bridge_guest_render_override(void) {
  return g_guest_render_override.load(std::memory_order_relaxed);
}

/// The debug channel's switch (diagnostic only): 1 on, 0 off, -1 back to the environment's choice.
void RefiiVfsSetGuestRender(int32_t on) {
  g_guest_render_override.store(on < 0 ? -1 : (on != 0 ? 1 : 0), std::memory_order_relaxed);
}
}  // extern "C"
