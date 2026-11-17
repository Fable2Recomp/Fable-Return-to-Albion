// refii — each renderable's stated APPEARANCE, as SHARED STATE.
//
// In librefii_vfs, not the PPC hook set, for the reason target_highlight_state.cpp gives: the
// PAYLOAD writes (Fable2/src/vfs/src/hooks/entity_appearance.cpp, on the guest's thread) and the HOST reads
// (the entity walk in the host bridge, on Godot's).
//
// One entry per CECGraphicAppearance (component 3) whose appearance was ever stated away from
// the constructor's: alpha +112, camera alpha +116, scale +108, tint +80..95, saturation mask
// +196, drawable +197, the definition's DisableAlphaDepthWrites +192. A component with no entry
// draws as constructed, so the table holds the faded, hidden, tinted, rescaled and
// depth-write-disabled few rather than every renderable in the region.

#include <atomic>
#include <cstdint>

#include "core/payload_bridge_fable2.h"

namespace refii::vfs {
namespace {

constexpr uint32_t kEmpty = 0u;
constexpr uint32_t kGone = 1u;  // a destroyed component; probes continue past it

struct StatedAppearance {
  std::atomic<uint32_t> component{kEmpty};
  std::atomic<uint32_t> words[kAppearanceWords] = {};
};

/// Corpses (capped at 48 by the detritus culler), hidden carry slots, faders: well under this.
constexpr uint32_t kSlots = 8192u;
constexpr uint32_t kProbes = 32u;

StatedAppearance g_appearances[kSlots];

/// A bit per first-probe slot ever claimed, so the entity walk's usual answer (no entry: nearly
/// every renderable, every guest frame) costs one read of this and not a miss in the table.
std::atomic<uint64_t> g_probed[kSlots / 64u] = {};

uint32_t SlotFor(uint32_t component) {
  const uint32_t h = component * 2654435761u;
  return (h >> 12) & (kSlots - 1u);
}

StatedAppearance* Find(uint32_t component) {
  const uint32_t start = SlotFor(component);
  if (((g_probed[start / 64u].load(std::memory_order_acquire) >> (start % 64u)) & 1u) == 0u) {
    return nullptr;
  }

  for (uint32_t probe = 0u; probe < kProbes; ++probe) {
    StatedAppearance& slot = g_appearances[(start + probe) & (kSlots - 1u)];
    const uint32_t held = slot.component.load(std::memory_order_acquire);
    if (held == component) {
      return &slot;
    }

    if (held == kEmpty) {
      return nullptr;
    }
  }

  return nullptr;
}

StatedAppearance* Claim(uint32_t component) {
  if (StatedAppearance* found = Find(component); found != nullptr) {
    return found;
  }

  const uint32_t start = SlotFor(component);

  // Before any slot can hold it: a clear bit means no component starting here was ever claimed.
  g_probed[start / 64u].fetch_or(uint64_t{1} << (start % 64u), std::memory_order_acq_rel);
  for (uint32_t probe = 0u; probe < kProbes; ++probe) {
    StatedAppearance& slot = g_appearances[(start + probe) & (kSlots - 1u)];
    uint32_t held = slot.component.load(std::memory_order_relaxed);
    if ((held == kEmpty || held == kGone) &&
        slot.component.compare_exchange_strong(held, component, std::memory_order_acq_rel)) {
      return &slot;
    }
  }

  return nullptr;
}

/// The constructor's values (`sub_82571DF8`): 1.0 at +108/+112/+116 (0x82571f1c..2c), tint
/// 1,1,1,1 at +80 (0x82571f04), +196 = 0 and +197 = 1 (0x82571f50/54), and a definition with
/// DisableAlphaDepthWrites false (+192, vf8 `sub_825720A8`).
bool IsConstructed(const uint32_t* words) {
  constexpr uint32_t kOne = 0x3F800000u;
  for (uint32_t i = 0u; i < kAppearanceFlags; ++i) {
    if (words[i] != kOne) {
      return false;
    }
  }
  return words[kAppearanceFlags] == kAppearanceDrawable;
}
}  // namespace


/// Fills `out` with what the guest last stated for one renderable; false when it never stated
/// anything but the constructor's values, which the caller then assumes.
bool StatedAppearanceFor(uint32_t component, uint32_t* out) {
  if (component <= kGone) {
    return false;
  }

  const StatedAppearance* slot = Find(component);
  if (slot == nullptr) {
    return false;
  }

  for (uint32_t i = 0u; i < kAppearanceWords; ++i) {
    out[i] = slot->words[i].load(std::memory_order_relaxed);
  }
  return true;
}
}  // namespace refii::vfs

// --- The payload's side. C only; see core/payload_bridge.h. --------------------------------

extern "C" {
void refii_bridge_note_appearance(uint32_t component, const uint32_t* words) {
  if (component <= refii::vfs::kGone) {
    return;
  }

  // Back to the constructor's look with no entry standing is the common case of a setter
  // called with the value it already had elsewhere; nothing to hold.
  refii::vfs::StatedAppearance* slot =

      refii::vfs::IsConstructed(words) ? refii::vfs::Find(component) : refii::vfs::Claim(component);
  if (slot == nullptr) {
    return;
  }

  for (uint32_t i = 0u; i < kAppearanceWords; ++i) {
    slot->words[i].store(words[i], std::memory_order_relaxed);
  }
}

void refii_bridge_forget_appearance(uint32_t component) {
  if (refii::vfs::StatedAppearance* slot = refii::vfs::Find(component); slot != nullptr) {
    slot->component.store(refii::vfs::kGone, std::memory_order_release);
  }
}
}  // extern "C"
