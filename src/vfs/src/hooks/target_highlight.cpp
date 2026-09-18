#include "vfs_hooks_private.h"

#include <atomic>
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

namespace {

struct StatedHighlight {
  std::atomic<uint32_t> component{0};
  std::atomic<uint32_t> rgba{0x000000FFu};
  std::atomic<uint32_t> sequence{0};
};

/// Sized well above the number of things that can be targeted at once; open addressed.
constexpr uint32_t kSlots = 256u;

StatedHighlight g_highlights[kSlots];
std::atomic<uint32_t> g_sequence{0};
std::atomic<uint64_t> g_colourCalls{0};
std::atomic<uint64_t> g_targetCalls{0};

uint32_t SlotFor(uint32_t component) {
  const uint32_t h = component * 2654435761u;
  return (h >> 12) & (kSlots - 1u);
}

StatedHighlight* Claim(uint32_t component) {
  if (component == 0u) {
    return nullptr;
  }

  const uint32_t start = SlotFor(component);
  for (uint32_t probe = 0u; probe < 12u; ++probe) {
    StatedHighlight& slot = g_highlights[(start + probe) & (kSlots - 1u)];
    uint32_t held = slot.component.load(std::memory_order_relaxed);
    if (held == component) {
      return &slot;
    }

    if (held == 0u &&
        slot.component.compare_exchange_strong(held, component, std::memory_order_relaxed)) {
      return &slot;
    }
  }

  return nullptr;
}

}  // namespace

REX_EXTERN(sub_825738F0) {
  refii::vfs::EnsureAlternateSignalStack();
  REX_FUNC_PROLOGUE();

  g_colourCalls.fetch_add(1u, std::memory_order_relaxed);
  if (StatedHighlight* slot = Claim(ctx.r3.u32); slot != nullptr) {
    // The four bytes in store order (r4[0] lands at +0x60, r4[1] at +0x61, ...), packed
    // big-endian so R is the top byte.
    const uint32_t at = ctx.r4.u32;
    const uint32_t rgba =
        (static_cast<uint32_t>(refii::vfs::LoadGuestU8(base, at)) << 24) |
        (static_cast<uint32_t>(refii::vfs::LoadGuestU8(base, at + 1u)) << 16) |
        (static_cast<uint32_t>(refii::vfs::LoadGuestU8(base, at + 2u)) << 8) |
        static_cast<uint32_t>(refii::vfs::LoadGuestU8(base, at + 3u));

    slot->rgba.store(rgba, std::memory_order_relaxed);
    slot->sequence.store(g_sequence.fetch_add(1u, std::memory_order_relaxed) + 1u,
                         std::memory_order_release);
  }

  REFII_VFS_NATIVE_PASSTHROUGH(sub_825738F0);
}

REX_EXTERN(sub_82427FD0) {
  refii::vfs::EnsureAlternateSignalStack();
  REX_FUNC_PROLOGUE();
  g_targetCalls.fetch_add(1u, std::memory_order_relaxed);
  REFII_VFS_NATIVE_PASSTHROUGH(sub_82427FD0);
}

extern "C" {

/// Colour statements and targeting changes seen since boot; 0 means the hook is not loaded.
uint32_t RefiiHighlightColourCalls(void) {
  return static_cast<uint32_t>(g_colourCalls.load(std::memory_order_relaxed));
}

uint32_t RefiiHighlightTargetCalls(void) {
  return static_cast<uint32_t>(g_targetCalls.load(std::memory_order_relaxed));
}

/// The colour the guest last stated for one renderable component, or 0 when it never stated one.
uint32_t RefiiHighlightFor(uint32_t component) {
  if (component == 0u) {
    return 0u;
  }

  const uint32_t start = SlotFor(component);
  for (uint32_t probe = 0u; probe < 12u; ++probe) {
    StatedHighlight& slot = g_highlights[(start + probe) & (kSlots - 1u)];
    if (slot.component.load(std::memory_order_acquire) == component) {
      return slot.rgba.load(std::memory_order_relaxed);
    }
  }

  return 0u;
}

}  // extern "C"
