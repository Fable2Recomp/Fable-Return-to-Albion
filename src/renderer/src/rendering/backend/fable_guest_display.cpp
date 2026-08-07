#include "runtime/refii_renderer/plume/fable/fable_guest_display.h"

#include "runtime/cvars.h"
#include "runtime/refii_renderer/bik/bink_guest_memory.h"

#include <rex/logging.h>

#include <atomic>

namespace refii::renderer {
namespace {

// qword_834A5FE8 — the guest's single resolution source of truth. See the
// header comment and refii-native-resolution-patch-point.md.
constexpr uint32_t kFableGuestDisplayPairAddress = 0x834A5FE8u;
constexpr uint32_t kFableStockDisplayWidth = 1280u;
constexpr uint32_t kFableStockDisplayHeight = 720u;

std::atomic<uintptr_t> g_guest_memory_base{0};
// Packed (width << 32) | height of the last plausible pair read from guest
// memory; 0 = never read.
std::atomic<uint64_t> g_last_guest_display_pair{0};
std::atomic<bool> g_logged_guest_display_pair{false};

bool PlausibleGuestDisplayPair(uint32_t width, uint32_t height) {
  // The guest aligns width to 160 and height to 16 (sub_82B6EBC0); anything
  // outside a sane display envelope is an unwritten/garbage global.
  return width >= 320u && width <= 7680u && height >= 240u && height <= 4320u;
}

uint64_t PackDisplayPair(uint32_t width, uint32_t height) {
  return (static_cast<uint64_t>(width) << 32) | height;
}

// Cvar-derived expectation, aligned exactly like the sub_82B6EA60 entry hook
// aligns the boot param block, for the window before the guest publishes the
// global (device create).
uint64_t CvarAlignedDisplayPair() {
  const uint32_t override_w = RendererNativeWidthOverride();
  const uint32_t override_h = RendererNativeHeightOverride();
  if (override_w == 0u || override_h == 0u) {
    return 0;
  }
  const uint32_t aligned_w = 160u * ((override_w + 159u) / 160u);
  const uint32_t aligned_h = (override_h + 15u) & ~15u;
  return PackDisplayPair(aligned_w, aligned_h);
}

uint64_t CurrentGuestDisplayPair() {
  uint8_t* base =
      reinterpret_cast<uint8_t*>(g_guest_memory_base.load(std::memory_order_relaxed));
  if (base != nullptr) {
    const uint32_t width =
        bink::LoadGuestU32(base, kFableGuestDisplayPairAddress);
    const uint32_t height =
        bink::LoadGuestU32(base, kFableGuestDisplayPairAddress + 4u);
    if (PlausibleGuestDisplayPair(width, height)) {
      const uint64_t pair = PackDisplayPair(width, height);
      const uint64_t previous =
          g_last_guest_display_pair.exchange(pair, std::memory_order_relaxed);
      if (!g_logged_guest_display_pair.exchange(true,
                                                std::memory_order_relaxed) ||
          (previous != 0 && previous != pair)) {
        REXLOG_INFO(
            "Refii guest display dims {}x{} (qword_834A5FE8; stock 1280x720; "
            "previous={}x{})",
            width, height, static_cast<uint32_t>(previous >> 32),
            static_cast<uint32_t>(previous & 0xFFFFFFFFu));
      }
      return pair;
    }
  }
  const uint64_t cached =
      g_last_guest_display_pair.load(std::memory_order_relaxed);
  if (cached != 0) {
    return cached;
  }
  const uint64_t cvar_pair = CvarAlignedDisplayPair();
  if (cvar_pair != 0) {
    return cvar_pair;
  }
  return PackDisplayPair(kFableStockDisplayWidth, kFableStockDisplayHeight);
}

}  // namespace

void NoteFableGuestDisplayMemoryBase(uint8_t* base) {
  if (base != nullptr) {
    g_guest_memory_base.store(reinterpret_cast<uintptr_t>(base),
                              std::memory_order_relaxed);
  }
}

uint8_t* FableGuestMemoryBase() {
  return reinterpret_cast<uint8_t*>(
      g_guest_memory_base.load(std::memory_order_relaxed));
}

uint32_t FableGuestDisplayWidth() {
  return static_cast<uint32_t>(CurrentGuestDisplayPair() >> 32);
}

uint32_t FableGuestDisplayHeight() {
  return static_cast<uint32_t>(CurrentGuestDisplayPair() & 0xFFFFFFFFu);
}

namespace {
// E IS PER-VIEW, SO ONE GLOBAL IS WRONG — measured run 571.
//
// A single last-writer-wins slot was the first design, and the run refuted it
// immediately: E alternated 1920x1080 <-> 1120x720 four times in one session.
// Both values are legitimate. `Render_SubPassOrchestrator` 0x821C3C60 reads the
// view descriptor at view+0x5D0/+0x5D4 and falls back to qword_834A5FE8 only
// when BOTH are zero, so a view with an explicit override states 1120x720 while
// a view without one states the display extent — and our own boot hook has made
// that display extent 1920x1080.
//
// With one slot, promotion silently switched off for the whole scene chain
// whenever the display-sized view allocated last: a 280x180 surface matches
// E/4 for E=1120x720 but nothing for E=1920x1080. The symptom would be
// intermittently un-supersampled targets with NO log line — the worst kind.
//
// So remember the SET of stated extents. It is tiny (2 in run 571), it is
// append-only, and a surface is scene-sized if it matches the chain of ANY
// observed E. Extents that coincide across views resolve to a no-op promotion
// rather than a conflict: E=1920x1080 with a 1920x1080 output makes E/2 and E/4
// map to themselves, so those entries can only ever return "no change".
constexpr size_t kMaxSceneRenderExtents = 8;
std::atomic<uint64_t> g_scene_render_extents[kMaxSceneRenderExtents] = {};
}  // namespace

void NoteFableSceneRenderTargetExtent(uint32_t width, uint32_t height) {
  // Guard the pair the same way the display pair is guarded: a scene set is
  // never 0 and never larger than the largest surface Xenos can address. A bad
  // read must never enter the set at all.
  if (width == 0 || height == 0 || width > 8192u || height > 8192u) {
    return;
  }
  const uint64_t packed = (static_cast<uint64_t>(width) << 32) | height;
  for (auto& slot : g_scene_render_extents) {
    const uint64_t seen = slot.load(std::memory_order_relaxed);
    if (seen == packed) {
      return;  // already known
    }
    if (seen == 0) {
      uint64_t expected = 0;
      if (slot.compare_exchange_strong(expected, packed,
                                       std::memory_order_relaxed)) {
        REXLOG_INFO(
            "Refii Fable scene render extent E={}x{} ADDED — source: "
            "Render_AllocateSceneRenderTargetSet stated argument. Scene "
            "surfaces are E, E/2, E/4. E is PER-VIEW, so this is a set.",
            width, height);
        return;
      }
      // Lost the race; re-examine this slot on the next iteration.
      if (slot.load(std::memory_order_relaxed) == packed) {
        return;
      }
    }
  }
  // Full. Not an error worth dropping data over silently — say so once.
  static std::atomic<bool> logged_full{false};
  bool expected_flag = false;
  if (logged_full.compare_exchange_strong(expected_flag, true,
                                          std::memory_order_relaxed)) {
    REXLOG_WARN(
        "Refii Fable scene render extent set FULL ({} entries); {}x{} not "
        "recorded. More distinct views than expected — raise the cap.",
        kMaxSceneRenderExtents, width, height);
  }
}

bool FableSceneRenderTargetExtentAt(size_t index, uint32_t& width,
                                    uint32_t& height) {
  if (index >= kMaxSceneRenderExtents) {
    return false;
  }
  const uint64_t packed =
      g_scene_render_extents[index].load(std::memory_order_relaxed);
  if (packed == 0) {
    return false;
  }
  width = static_cast<uint32_t>(packed >> 32);
  height = static_cast<uint32_t>(packed & 0xFFFFFFFFu);
  return true;
}

size_t FableSceneRenderTargetExtentCapacity() { return kMaxSceneRenderExtents; }

}  // namespace refii::renderer
