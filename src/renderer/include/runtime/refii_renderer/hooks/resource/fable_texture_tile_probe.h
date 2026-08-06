#pragma once

#include <array>
#include <cstdint>

namespace refii::renderer {

struct FableGuestByteProbeSummary {
  bool valid = false;
  uint32_t first_nonzero = 0xFFFFFFFFu;
  uint64_t hash = 0;
  std::array<uint8_t, 4> first4 = {};
};

struct FableGuestByteProbeCallbacks {
  bool (*is_likely_guest_dword_address)(uint32_t guest_address) = nullptr;
  bool (*is_likely_high_scratch_byte_buffer_range)(
      uint32_t guest_address, uint32_t byte_size) = nullptr;
  bool (*is_readable_guest_byte_range)(uint8_t *base, uint32_t guest_address,
                                       uint32_t byte_size) = nullptr;
  uint32_t (*guest_physical_host_offset)(uint32_t guest_address) = nullptr;
  uint32_t (*find_first_nonzero_guest_byte)(uint8_t *base,
                                            uint32_t guest_address,
                                            uint32_t byte_size,
                                            uint32_t scan_size) = nullptr;
  uint64_t (*hash_guest_bytes_fnv1a64)(uint8_t *base, uint32_t guest_address,
                                       uint32_t byte_size) = nullptr;
};

FableGuestByteProbeSummary
FableProbeGuestBytes(uint8_t *base, uint32_t guest_address,
                     uint32_t requested_size,
                     const FableGuestByteProbeCallbacks &callbacks);

bool FableIsTitleSizedTextureTile(uint32_t width, uint32_t height);

uint32_t FableTileHelperProbeSize(uint32_t width, uint32_t height,
                                  uint32_t pitch, uint32_t bits_or_bytes,
                                  uint32_t max_probe_bytes);

bool FableShouldLogTileHelperDeepProbe(
    uint32_t dst, uint32_t src, uint32_t width, uint32_t height,
    const FableGuestByteProbeCallbacks &callbacks,
    bool (*is_title_texture_probe_address)(uint32_t address));

} // namespace refii::renderer
