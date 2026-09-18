// refii — the targeting highlight colour the guest states, as SHARED STATE.
//
// THIS FILE MUST BE IN librefii_vfs, NOT IN THE PPC HOOK SET, for the same reason as
// stated_fx_state.cpp beside it: the PAYLOAD writes (the setter hooks in
// src/hooks/target_highlight.cpp, on the guest's thread) and the HOST reads (the entity walk in
// the host bridge, on Godot's). Both link refii_vfs; the payload does not export anything the
// host may look up, and the host may not link the payload at all.
//
// The host reaches these as ordinary C++ symbols; the PAYLOAD reaches them through the C entries
// in core/payload_bridge.h. See that header for the contract.

#include <atomic>
#include <cstdint>

#include "core/payload_bridge.h"

namespace refii::vfs {
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
std::atomic<uint64_t> g_colour_calls{0};
std::atomic<uint64_t> g_target_calls{0};

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

/// The colour the guest last stated for one renderable component, or 0 when it never stated one.
uint32_t StatedHighlightFor(uint32_t component) {
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

/// Colour statements and targeting changes seen since boot; 0 means the hook is not loaded.
uint32_t StatedHighlightColourCalls() {
  return static_cast<uint32_t>(g_colour_calls.load(std::memory_order_relaxed));
}

uint32_t StatedHighlightTargetCalls() {
  return static_cast<uint32_t>(g_target_calls.load(std::memory_order_relaxed));
}

}  // namespace refii::vfs

// --- The payload's side. C only; see core/payload_bridge.h. --------------------------------

extern "C" {

void refii_bridge_note_highlight_colour(uint32_t component, uint32_t rgba) {
  refii::vfs::g_colour_calls.fetch_add(1u, std::memory_order_relaxed);

  if (refii::vfs::StatedHighlight* slot = refii::vfs::Claim(component); slot != nullptr) {
    slot->rgba.store(rgba, std::memory_order_relaxed);
    slot->sequence.store(
        refii::vfs::g_sequence.fetch_add(1u, std::memory_order_relaxed) + 1u,
        std::memory_order_release);
  }
}

void refii_bridge_note_highlight_target(void) {
  refii::vfs::g_target_calls.fetch_add(1u, std::memory_order_relaxed);
}

}  // extern "C"
