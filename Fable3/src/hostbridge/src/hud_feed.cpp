// refii - Fable 3's HUD, as the guest states it: its element registries (texts, meters, pulsers)
// and its screen fade. Every row here comes from the table Fable3/src/hooks/src/hud_hooks.cpp fills
// at the guest's own entry points (core/payload_bridge.h, src/runtime/src/core/stated_hud.cpp).
// Nothing here reads guest memory.
//
// A NAME is stated by the registry insert sub_8291FCF0 (registry, FNV-1 of the element's
// attribute, element) and leaves when the registry unfiles it (sub_82B9A200) or is destroyed
// (sub_8291FB70) — the HUD.bgf's MeterName [1], PulserName [4] and TextName [6] registries.
//   - A TEXT is what its one writer sub_82733F30(registry, hash, LHString*) last assigned. The
//     subtitler's pages (sub_82920278, each line of the `Subtitles` box) and the TopBox publisher's
//     `TopTitle` / `TopMain` / button labels (sub_8217D880) are such writes.
//   - A METER is the value its one writer sub_8291F6E8(meter, f1) last applied to the element,
//     0..100: the meter's own update steps it toward the target its setters gave.
//   - A PULSER is stated at creation, stopped (mode 2) at phase 0, and nothing in the image
//     starts one (see hud_hooks.cpp).
//
// THE FADE is stated at its one start sub_825D5700 (from, target, seconds) and at the edges of its
// advance sub_82287090 (held, resumed, complete, locked), and run between them on the host's clock
// at the guest's own rate. The guest's fader stays alive and its readers are the guest's.

#include <cstdint>

#include "core/payload_bridge.h"
#include "host_abi.h"

namespace {

constexpr uint32_t kMaxNames = 512u;

}  // namespace

extern "C" {

/// Every HUD text by its element name's FNV-1, the string the guest last wrote (registry [6]).
/// Strings go to `text` NUL-separated in name order; returns the number of names.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetHudTexts(uint32_t* out_hashes, uint32_t capacity, char* text,
                                                   uint32_t text_capacity) {
  if (text != nullptr && text_capacity > 0u) {
    text[0] = '\0';
  }
  const uint32_t want = capacity < kMaxNames ? capacity : kMaxNames;
  return refii_bridge_hud_read(REFII_HUD_KIND_TEXT, out_hashes, nullptr, nullptr, text, text_capacity, want);
}

/// Every HUD meter by name, the value 0..100 the guest last applied (registry [1]).
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetHudMeters(uint32_t* out_hashes, float* out_values, uint32_t capacity) {
  const uint32_t want = capacity < kMaxNames ? capacity : kMaxNames;
  return refii_bridge_hud_read(REFII_HUD_KIND_METER, out_hashes, out_values, nullptr, nullptr, 0u, want);
}

/// Every HUD pulser by name (registry [4]), its PHASE 0..1 and its MODE (0 looping, 1 finishing
/// its loop, 2 stopped).
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetHudPulserStates(uint32_t* out_hashes, float* out_phases,
                                                           uint32_t* out_modes, uint32_t capacity) {
  const uint32_t want = capacity < kMaxNames ? capacity : kMaxNames;
  return refii_bridge_hud_read(REFII_HUD_KIND_PULSER, out_hashes, out_phases, out_modes, nullptr, 0u, want);
}

/// The phases alone, the shape the shell binds when the states export is missing.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetHudPulsers(uint32_t* out_hashes, float* out_phases, uint32_t capacity) {
  return RefiiHostGetHudPulserStates(out_hashes, out_phases, nullptr, capacity);
}

/// The screen fade as the guest's lerp sub_82182CB8 would give it now: `from + (to - from) * t`
/// per byte, truncated, the bytes B, G, R, A. Returns the fader, 0 before the guest stated one.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetScreenFade(float* out_rgba, float* out_progress, uint32_t* out_locked) {
  uint8_t from[4]{};
  uint8_t to[4]{};
  float t = 0.0f;
  uint32_t locked = 0u;
  const uint32_t fader = refii_bridge_screen_fade_read(from, to, &t, &locked);
  t = !(t > 0.0f) ? 0.0f : (t > 1.0f ? 1.0f : t);

  uint8_t lerped[4] = {0, 0, 0, 0};
  for (uint32_t i = 0; i < 4u; ++i) {
    const float a = static_cast<float>(from[i]);
    const float b = static_cast<float>(to[i]);
    lerped[i] = static_cast<uint8_t>(a + (t * (b - a)));
  }

  if (out_rgba != nullptr) {
    out_rgba[0] = static_cast<float>(lerped[2]) * (1.0f / 255.0f);
    out_rgba[1] = static_cast<float>(lerped[1]) * (1.0f / 255.0f);
    out_rgba[2] = static_cast<float>(lerped[0]) * (1.0f / 255.0f);
    out_rgba[3] = static_cast<float>(lerped[3]) * (1.0f / 255.0f);
  }
  if (out_progress != nullptr) *out_progress = t;
  if (out_locked != nullptr) *out_locked = locked;
  return fader;
}

}  // extern "C"
