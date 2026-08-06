#include "runtime/refii_renderer/bik/bink_video_uploads.h"

#include "runtime/refii_renderer/bik/bink_guest_memory.h"
#include "runtime/refii_renderer/plume/general/plume_fable_addressing.h"

#include <algorithm>
#include <limits>

namespace refii::renderer::bink {

namespace {

uint32_t DecodeGpuPointerOrZero(
    uint32_t pointer,
    const BinkVideoUploadAddressCallbacks& callbacks) {
  return callbacks.decode_gpu_pointer != nullptr
             ? callbacks.decode_gpu_pointer(pointer)
             : 0u;
}

bool IsReadableUploadAddress(uint8_t* base,
                             uint32_t address,
                             uint32_t upload_size) {
  return address != 0 && IsReadableGuestByteRange(base, address, upload_size);
}

bool IsHighScratchByteBufferRange(
    uint32_t address,
    uint32_t upload_size,
    const BinkVideoUploadAddressCallbacks& callbacks) {
  return callbacks.is_high_scratch_byte_buffer_range != nullptr &&
         callbacks.is_high_scratch_byte_buffer_range(address, upload_size);
}

bool IsGpuByteBufferRange(
    uint32_t address,
    uint32_t upload_size,
    const BinkVideoUploadAddressCallbacks& callbacks) {
  return callbacks.is_gpu_byte_buffer_range != nullptr &&
         callbacks.is_gpu_byte_buffer_range(address, upload_size);
}

uint32_t SelectReadableUploadAddressOrZero(
    uint8_t* base,
    uint32_t raw_address,
    uint32_t upload_size,
    const BinkVideoUploadAddressCallbacks& callbacks) {
  return callbacks.select_readable_upload_address != nullptr
             ? callbacks.select_readable_upload_address(base, raw_address,
                                                        upload_size)
             : 0u;
}

}  // namespace

}  // namespace refii::renderer::bink

namespace refii::renderer {

BinkVideoPlaneBinding MakeBinkVideoPlaneBinding(
    uint32_t movie,
    uint32_t surface,
    uint32_t plane_info,
    uint32_t plane_output_slot,
    uint32_t physical_address,
    uint32_t row_pitch,
    uint32_t upload_size,
    uint64_t serial) {
  return BinkVideoPlaneBinding{
      .movie = movie,
      .surface = surface,
      .plane_info = plane_info,
      .plane_output_slot = plane_output_slot,
      .physical_address = physical_address,
      .row_pitch = row_pitch,
      .upload_size = upload_size,
      .serial = serial,
  };
}

}  // namespace refii::renderer

namespace refii::renderer::bink {

uint32_t SelectBinkVideoPlaneUploadAddress(
    uint8_t* base,
    uint32_t physical_address,
    uint32_t upload_size,
    const BinkVideoUploadAddressCallbacks& callbacks) {
  if (!base || physical_address == 0 || upload_size == 0) {
    return 0;
  }

  const uint32_t decoded_address =
      DecodeGpuPointerOrZero(physical_address, callbacks);
  if (IsHighScratchByteBufferRange(physical_address, upload_size, callbacks) &&
      IsGpuByteBufferRange(decoded_address, upload_size, callbacks) &&
      IsReadableGuestByteRange(base, decoded_address, upload_size)) {
    return decoded_address;
  }

  const uint32_t selected = SelectReadableUploadAddressOrZero(
      base, physical_address, upload_size, callbacks);
  if (IsReadableUploadAddress(base, selected, upload_size)) {
    return selected;
  }

  return IsReadableUploadAddress(base, physical_address, upload_size)
             ? physical_address
             : 0;
}

uint32_t SelectBinkDecodedPlaneAddress(
    uint8_t* base,
    uint32_t address,
    uint32_t upload_size,
    const BinkVideoUploadAddressCallbacks& callbacks) {
  if (!base || address == 0 || upload_size == 0) {
    return 0;
  }

  const std::array<uint32_t, 3> candidates = {
      address,
      DecodeGpuPointerOrZero(address, callbacks),
      SelectReadableUploadAddressOrZero(base, address, upload_size, callbacks),
  };
  for (uint32_t candidate : candidates) {
    if (IsReadableUploadAddress(base, candidate, upload_size)) {
      return candidate;
    }
  }
  return 0;
}

uint32_t SelectFableBinkReadableUploadAddress(uint8_t* base,
                                              uint32_t raw_address,
                                              uint32_t upload_size) {
  if (!base || raw_address == 0 || upload_size == 0) {
    return 0;
  }

  const auto is_upload_address = [upload_size](uint32_t address) {
    return IsLikelyFableGpuByteBufferRange(address, upload_size) ||
           IsLikelyFableHighScratchByteBufferRange(address, upload_size);
  };
  const auto first_nonzero = [&](uint32_t address) {
    if (!is_upload_address(address) ||
        !IsReadableGuestByteRange(base, address, upload_size)) {
      return std::numeric_limits<uint32_t>::max();
    }
    const uint8_t* data = GuestMemoryPtr(base, address, upload_size);
    if (data == nullptr) {
      return std::numeric_limits<uint32_t>::max();
    }
    for (uint32_t index = 0; index < upload_size; ++index) {
      if (data[index] != 0) {
        return index;
      }
    }
    return std::numeric_limits<uint32_t>::max();
  };

  const uint32_t decoded_address = DecodeFableGpuBufferPointer(raw_address);
  const uint32_t raw_nonzero = first_nonzero(raw_address);
  const uint32_t decoded_nonzero = first_nonzero(decoded_address);
  if (raw_nonzero != std::numeric_limits<uint32_t>::max()) {
    return raw_address;
  }
  if (decoded_nonzero != std::numeric_limits<uint32_t>::max() ||
      is_upload_address(decoded_address)) {
    return decoded_address;
  }
  return is_upload_address(raw_address) ? raw_address : 0;
}

BinkDecodedFramePlaneMatch BinkDecodedPlaneMatchForBinding(
    uint8_t* base,
    uint32_t physical_address,
    uint32_t upload_size,
    const BinkDecodedFramePlaneAliases& aliases,
    const BinkVideoUploadAddressCallbacks& callbacks) {
  BinkDecodedFramePlaneMatch match;
  if (!base || physical_address == 0 || upload_size == 0) {
    return match;
  }

  const uint32_t selected_physical = SelectBinkDecodedPlaneAddress(
      base, physical_address, upload_size, callbacks);
  for (uint32_t plane = 0; plane < aliases.raw_addresses.size(); ++plane) {
    for (uint32_t frame = 0; frame < aliases.raw_addresses[plane].size();
         ++frame) {
      if (physical_address != 0 &&
          physical_address == aliases.raw_addresses[plane][frame]) {
        match.plane_index = plane;
        match.frame_slot = frame;
        return match;
      }
    }
  }

  for (uint32_t plane = 0; plane < aliases.selected_addresses.size(); ++plane) {
    for (uint32_t frame = 0; frame < aliases.selected_addresses[plane].size();
         ++frame) {
      if (selected_physical != 0 &&
          selected_physical == aliases.selected_addresses[plane][frame]) {
        match.plane_index = plane;
        match.frame_slot = frame;
        return match;
      }
    }
  }
  return match;
}

bool BinkDecodedFramePlaneSourceValid(
    uint8_t* base,
    const BinkDecodedFramePlaneSource& plane) {
  const uint8_t* data = GuestMemoryPtr(base, plane.address, plane.byte_size);
  if (data == nullptr) {
    return false;
  }
  for (uint32_t byte_index = 0; byte_index < plane.byte_size; ++byte_index) {
    if (data[byte_index] != 0) {
      return true;
    }
  }
  return false;
}

BinkVideoPlaneSetupProbe ProbeBinkVideoPlaneSetup(
    uint8_t* base,
    const BinkVideoPlaneBinding& binding,
    const BinkVideoUploadAddressCallbacks& upload_address_callbacks) {
  BinkVideoPlaneSetupProbe probe;
  if (base == nullptr || binding.physical_address == 0 ||
      binding.upload_size == 0) {
    return probe;
  }

  probe.selected_address = SelectBinkVideoPlaneUploadAddress(
      base, binding.physical_address, binding.upload_size,
      upload_address_callbacks);
  if (probe.selected_address == 0) {
    return probe;
  }

  for (uint32_t byte_index = 0; byte_index < binding.upload_size;
       ++byte_index) {
    if (LoadGuestU8(base, probe.selected_address + byte_index) != 0) {
      probe.first_nonzero = static_cast<int32_t>(byte_index);
      break;
    }
  }
  return probe;
}

bool ReadBinkDecodedFrameTable(
    uint8_t* base,
    uint32_t frame_table,
    uint32_t max_plane_upload_size,
    const BinkVideoUploadAddressCallbacks& callbacks,
    BinkDecodedFrameTable* table) {
  if (!base || frame_table == 0 || max_plane_upload_size == 0 ||
      table == nullptr ||
      !IsReadableGuestByteRange(base, frame_table, 124u)) {
    return false;
  }

  BinkDecodedFrameTable parsed;
  parsed.frame_table = frame_table;
  parsed.y_width = LoadGuestU32(base, frame_table + 4u);
  parsed.y_height = LoadGuestU32(base, frame_table + 8u);
  parsed.c_width = LoadGuestU32(base, frame_table + 12u);
  parsed.c_height = LoadGuestU32(base, frame_table + 16u);
  parsed.current_frame = LoadGuestU32(base, frame_table + 20u);
  if (parsed.y_width == 0 || parsed.y_height == 0 ||
      parsed.c_width == 0 || parsed.c_height == 0 ||
      parsed.y_width > 4096u || parsed.y_height > 4096u ||
      parsed.c_width > 4096u || parsed.c_height > 4096u ||
      parsed.current_frame > 1u) {
    return false;
  }

  const uint64_t y_size64 = uint64_t(parsed.y_width) * parsed.y_height;
  const uint64_t c_size64 = uint64_t(parsed.c_width) * parsed.c_height;
  if (y_size64 == 0 || c_size64 == 0 ||
      y_size64 > max_plane_upload_size ||
      c_size64 > max_plane_upload_size) {
    return false;
  }

  const uint32_t entry = frame_table + parsed.current_frame * 48u;
  if (!IsReadableGuestByteRange(base, entry + 56u, sizeof(uint32_t))) {
    return false;
  }

  parsed.planes[0].raw_address = LoadGuestU32(base, entry + 28u);
  parsed.planes[0].row_pitch = LoadGuestU32(base, entry + 32u);
  parsed.planes[0].byte_size =
      std::max<uint32_t>(static_cast<uint32_t>(y_size64),
                         parsed.planes[0].row_pitch * parsed.y_height);
  parsed.planes[0].address = SelectBinkDecodedPlaneAddress(
      base, parsed.planes[0].raw_address, parsed.planes[0].byte_size,
      callbacks);

  parsed.planes[1].raw_address = LoadGuestU32(base, entry + 40u);
  parsed.planes[1].row_pitch = LoadGuestU32(base, entry + 44u);
  parsed.planes[1].byte_size =
      std::max<uint32_t>(static_cast<uint32_t>(c_size64),
                         parsed.planes[1].row_pitch * parsed.c_height);
  parsed.planes[1].address = SelectBinkDecodedPlaneAddress(
      base, parsed.planes[1].raw_address, parsed.planes[1].byte_size,
      callbacks);

  parsed.planes[2].raw_address = LoadGuestU32(base, entry + 52u);
  parsed.planes[2].row_pitch = LoadGuestU32(base, entry + 56u);
  parsed.planes[2].byte_size =
      std::max<uint32_t>(static_cast<uint32_t>(c_size64),
                         parsed.planes[2].row_pitch * parsed.c_height);
  parsed.planes[2].address = SelectBinkDecodedPlaneAddress(
      base, parsed.planes[2].raw_address, parsed.planes[2].byte_size,
      callbacks);

  for (const BinkDecodedFramePlaneSource& plane : parsed.planes) {
    if (plane.byte_size > max_plane_upload_size) {
      return false;
    }
  }

  for (uint32_t frame = 0; frame < 2u; ++frame) {
    const uint32_t alias_entry = frame_table + frame * 48u;
    if (!IsReadableGuestByteRange(base, alias_entry + 56u,
                                  sizeof(uint32_t))) {
      continue;
    }
    parsed.aliases.raw_addresses[0][frame] =
        LoadGuestU32(base, alias_entry + 28u);
    parsed.aliases.raw_addresses[1][frame] =
        LoadGuestU32(base, alias_entry + 40u);
    parsed.aliases.raw_addresses[2][frame] =
        LoadGuestU32(base, alias_entry + 52u);
    parsed.aliases.selected_addresses[0][frame] =
        SelectBinkDecodedPlaneAddress(
            base, parsed.aliases.raw_addresses[0][frame],
            parsed.planes[0].byte_size, callbacks);
    parsed.aliases.selected_addresses[1][frame] =
        SelectBinkDecodedPlaneAddress(
            base, parsed.aliases.raw_addresses[1][frame],
            parsed.planes[1].byte_size, callbacks);
    parsed.aliases.selected_addresses[2][frame] =
        SelectBinkDecodedPlaneAddress(
            base, parsed.aliases.raw_addresses[2][frame],
            parsed.planes[2].byte_size, callbacks);
  }

  *table = parsed;
  return true;
}

}  // namespace refii::renderer::bink
