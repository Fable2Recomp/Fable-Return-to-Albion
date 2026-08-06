#include "runtime/refii_renderer/hooks/resource/fable_texture_tile_probe.h"

#include "runtime/refii_renderer/plume/fable/fable_guest_display.h"

#include <algorithm>
#include <limits>

namespace refii::renderer {

FableGuestByteProbeSummary
FableProbeGuestBytes(uint8_t *base, uint32_t guest_address,
                     uint32_t requested_size,
                     const FableGuestByteProbeCallbacks &callbacks) {
  FableGuestByteProbeSummary summary;
  const bool readable_guest_range =
      (callbacks.is_likely_guest_dword_address &&
       callbacks.is_likely_guest_dword_address(guest_address)) ||
      (callbacks.is_likely_high_scratch_byte_buffer_range &&
       callbacks.is_likely_high_scratch_byte_buffer_range(guest_address,
                                                          requested_size));
  if (!base || guest_address == 0 || requested_size == 0 ||
      guest_address > std::numeric_limits<uint32_t>::max() - requested_size ||
      !readable_guest_range || !callbacks.is_readable_guest_byte_range ||
      !callbacks.guest_physical_host_offset ||
      !callbacks.find_first_nonzero_guest_byte ||
      !callbacks.hash_guest_bytes_fnv1a64) {
    return summary;
  }

  const uint32_t probe_size = std::min<uint32_t>(requested_size, 64u * 1024u);
  if (!callbacks.is_readable_guest_byte_range(base, guest_address,
                                              probe_size)) {
    return summary;
  }

  const uint8_t *bytes = base + guest_address +
                         callbacks.guest_physical_host_offset(guest_address);
  summary.valid = true;
  summary.first_nonzero = callbacks.find_first_nonzero_guest_byte(
      base, guest_address, probe_size, probe_size);
  summary.hash =
      callbacks.hash_guest_bytes_fnv1a64(base, guest_address, probe_size);
  for (uint32_t i = 0; i < summary.first4.size() && i < probe_size; ++i) {
    summary.first4[i] = bytes[i];
  }
  return summary;
}

bool FableIsTitleSizedTextureTile(uint32_t width, uint32_t height) {
  return (width == 16u && height == 16u) || (width == 64u && height == 64u) ||
         (width == 128u && height == 128u) ||
         (width == 280u && height == 180u) ||
         (width == 256u && height == 256u) ||
         (width == 480u && height == 270u) ||
         (width == 512u && height == 256u) ||
         (width == 560u && height == 360u) ||
         (width == 512u && height == 512u) ||
         (width == 960u && height == 540u) ||
         (width == 1024u && height == 512u) ||
         (width == 1280u && height == 720u) ||
         (width == 1920u && height == 1080u) ||
         // Native resolution: the live guest display extent
         // (qword_834A5FE8) is title-sized by definition.
         (width == FableGuestDisplayWidth() &&
          height == FableGuestDisplayHeight());
}

uint32_t FableTileHelperProbeSize(uint32_t width, uint32_t height,
                                  uint32_t pitch, uint32_t bits_or_bytes,
                                  uint32_t max_probe_bytes) {
  const uint32_t bytes_per_pixel_or_block =
      std::clamp<uint32_t>(bits_or_bytes, 1u, 16u);
  uint64_t size = 0;
  if (pitch != 0 && height != 0) {
    size = static_cast<uint64_t>(pitch) * height;
  } else {
    size = static_cast<uint64_t>(width) * height * bytes_per_pixel_or_block;
  }
  if (size == 0) {
    size = 64u * 1024u;
  }
  return static_cast<uint32_t>(std::min<uint64_t>(size, max_probe_bytes));
}

bool FableShouldLogTileHelperDeepProbe(
    uint32_t dst, uint32_t src, uint32_t width, uint32_t height,
    const FableGuestByteProbeCallbacks &callbacks,
    bool (*is_title_texture_probe_address)(uint32_t address)) {
  if (is_title_texture_probe_address && (is_title_texture_probe_address(dst) ||
                                         is_title_texture_probe_address(src))) {
    return true;
  }
  if (!FableIsTitleSizedTextureTile(width, height)) {
    return false;
  }
  return (callbacks.is_likely_guest_dword_address &&
          (callbacks.is_likely_guest_dword_address(dst) ||
           callbacks.is_likely_guest_dword_address(src))) ||
         (callbacks.is_likely_high_scratch_byte_buffer_range &&
          (callbacks.is_likely_high_scratch_byte_buffer_range(dst, 1u) ||
           callbacks.is_likely_high_scratch_byte_buffer_range(src, 1u)));
}

} // namespace refii::renderer
