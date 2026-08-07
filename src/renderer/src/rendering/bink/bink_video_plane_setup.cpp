#include "runtime/refii_renderer/bik/bink_video_plane_setup.h"

#include "renderer_runtime.h"
#include "runtime/refii_renderer/bik/bink_guest_memory.h"

#include <algorithm>
#include <cstdint>
#include <string_view>

namespace refii::renderer::bink {
namespace {

constexpr uint32_t kTextureObjectBytes = 52u;
constexpr uint32_t kMaxBinkPlaneDimension = 4096u;
constexpr uint32_t kMaxPlaneBytes = 16u * 1024u * 1024u;
constexpr uint32_t kBinkPlaneAlignment = 4096u;

bool IsLikelyGuestMemoryAddress(uint32_t guest_address) {
  return guest_address < 0x80000000u || guest_address >= 0xE0000000u;
}

bool IsLikelyFableWritableDataDwordAddress(uint32_t guest_address) {
  constexpr uint32_t kFableDataAddressMin = 0x83300000u;
  constexpr uint32_t kFableDataAddressMax = 0x84000000u;
  return (guest_address & (alignof(uint32_t) - 1u)) == 0 &&
         (IsLikelyGuestMemoryAddress(guest_address) ||
          (guest_address >= kFableDataAddressMin &&
           guest_address < kFableDataAddressMax));
}

bool IsLikelyFableWritableDataRange(uint32_t guest_address,
                                    uint32_t byte_size) {
  if (byte_size == 0 || guest_address > UINT32_MAX - byte_size) {
    return false;
  }
  constexpr uint32_t kFableDataAddressMin = 0x83300000u;
  constexpr uint32_t kFableDataAddressMax = 0x84000000u;
  const uint32_t end_address = guest_address + byte_size - 1u;
  return IsLikelyFableWritableDataDwordAddress(guest_address) &&
         (IsLikelyGuestMemoryAddress(end_address) ||
          (end_address >= kFableDataAddressMin &&
           end_address < kFableDataAddressMax));
}

bool IsLikelyFableObjectRange(uint32_t guest_address, uint32_t byte_size) {
  if (byte_size == 0 || guest_address > UINT32_MAX - byte_size) {
    return false;
  }
  const uint32_t end_address = guest_address + byte_size - 1u;
  constexpr uint32_t kFableObjectAddressMax = 0x80000000u;
  return guest_address != 0 && guest_address < kFableObjectAddressMax &&
         end_address < kFableObjectAddressMax;
}

bool IsLikelyGuestMemoryRange(uint32_t guest_address, uint32_t byte_size) {
  if (byte_size == 0 || guest_address > UINT32_MAX - byte_size) {
    return false;
  }
  const uint32_t end_address = guest_address + byte_size - 1u;
  return IsLikelyGuestMemoryAddress(guest_address) &&
         ((guest_address < 0x80000000u && end_address < 0x80000000u) ||
          (guest_address >= 0xE0000000u && end_address >= 0xE0000000u));
}

uint32_t FableTextureCreateMemoryFlags(uint32_t usage) {
  const uint32_t heap_selector = (((~usage >> 2u) & 1u) | 2u) << 28u;
  return 0x8C800000u | heap_selector;
}

uint32_t AlignFableBinkPlaneBytes(uint32_t byte_size) {
  return (byte_size + kBinkPlaneAlignment - 1u) & ~(kBinkPlaneAlignment - 1u);
}

void ClearBinkVideoPlaneSetupOutputs(const RendererHookArgs& args,
                                     uint8_t* base) {
  StoreGuestU32(base, args.r7, 0u);
  StoreGuestU32(base, args.r6 + 4u, 0u);
  StoreGuestU32(base, args.r6 + 8u, 0u);
}

void ZeroGuestDwords(uint8_t* base, uint32_t guest_address,
                     uint32_t byte_size) {
  for (uint32_t offset = 0; offset < byte_size; offset += sizeof(uint32_t)) {
    StoreGuestU32(base, guest_address + offset, 0);
  }
}

void UpdateFableLockDirtyRange(uint8_t* base,
                               uint32_t resource,
                               uint32_t range_offset,
                               uint32_t range_base,
                               uint32_t lock_address,
                               uint32_t lock_size) {
  if (range_base == 0 || lock_address < (range_base & ~0x7Fu) ||
      lock_size == 0) {
    return;
  }

  const uint32_t relative = lock_address - (range_base & ~0x7Fu);
  const uint32_t start_block = relative >> 7u;
  const uint32_t end_block = (relative + lock_size + 0x7Fu) >> 7u;
  const uint32_t current = LoadGuestU32(base, resource + range_offset);
  const uint32_t current_start = current >> 16u;
  const uint32_t current_end = current & 0xFFFFu;
  const uint32_t next_start = std::min(current_start, start_block);
  const uint32_t next_end = std::max(current_end, end_block);
  StoreGuestU32(base, resource + range_offset,
                (next_start << 16u) | (next_end & 0xFFFFu));
}

bool HeaderInfoValid(const FableBinkVideoPlaneHeaderInfo& info) {
  return info.texture_height != 0 && info.bytes_per_pixel != 0;
}

}  // namespace

bool IsFableBinkVideoPlaneSetupSymbol(const char* symbol) {
  return IsFableBinkVideoPlaneSetupSymbol(
      symbol != nullptr ? std::string_view(symbol) : std::string_view{});
}

bool IsFableBinkVideoPlaneSetupSymbol(std::string_view symbol) {
  return symbol == "sub_82A42FA0";
}

bool IsFableBinkVideoPlaneSurfaceSetupSymbol(const char* symbol) {
  return IsFableBinkVideoPlaneSurfaceSetupSymbol(
      symbol != nullptr ? std::string_view(symbol) : std::string_view{});
}

bool IsFableBinkVideoPlaneSurfaceSetupSymbol(std::string_view symbol) {
  return symbol == "sub_82A4E8D0";
}

bool CanEmulateFableBinkVideoPlaneSetup(const RendererHookArgs& args,
                                        uint8_t* base) {
  return base != nullptr && args.r4 != 0 && args.r5 != 0 &&
         args.r4 <= kMaxBinkPlaneDimension &&
         args.r5 <= kMaxBinkPlaneDimension &&
         IsLikelyFableWritableDataRange(args.r6 + 4u, 8u) &&
         IsLikelyFableWritableDataRange(args.r7, 4u);
}

bool CanEmulateFableBinkVideoPlaneSurfaceSetup(uint8_t* base) {
  return base != nullptr;
}

FableBinkVideoPlaneSetupResult EmulateFableBinkVideoPlaneSetup(
    const RendererHookArgs& args,
    uint8_t* base,
    const FableBinkVideoPlaneSetupCallbacks& callbacks) {
  FableBinkVideoPlaneSetupResult result;
  if (!CanEmulateFableBinkVideoPlaneSetup(args, base) ||
      !callbacks.allocate_guest_memory ||
      !callbacks.initialize_texture_header) {
    return result;
  }

  const uint32_t texture =
      callbacks.allocate_guest_memory(kTextureObjectBytes, 0u);
  result.handled = true;
  if (!IsLikelyFableObjectRange(texture, kTextureObjectBytes)) {
    ClearBinkVideoPlaneSetupOutputs(args, base);
    return result;
  }

  ZeroGuestDwords(base, texture, kTextureObjectBytes);
  StoreGuestU32(base, args.r7, texture);

  const FableBinkVideoPlaneHeaderInfo header =
      callbacks.initialize_texture_header(texture, args.r4, args.r5);
  if (!HeaderInfoValid(header)) {
    ClearBinkVideoPlaneSetupOutputs(args, base);
    return result;
  }

  const uint64_t computed_plane_bytes =
      header.row_pitch_bytes != 0 &&
              header.texture_height <= UINT32_MAX / header.row_pitch_bytes
          ? static_cast<uint64_t>(header.row_pitch_bytes) *
                static_cast<uint64_t>(header.texture_height)
          : 0u;
  uint32_t plane_bytes =
      std::max(header.header_plane_bytes,
               computed_plane_bytes <= UINT32_MAX
                   ? static_cast<uint32_t>(computed_plane_bytes)
                   : 0u);
  if (plane_bytes == 0 || plane_bytes > kMaxPlaneBytes) {
    plane_bytes = args.r4 <= UINT32_MAX / args.r5 ? args.r4 * args.r5 : 0u;
  }
  if (plane_bytes == 0 || plane_bytes > kMaxPlaneBytes) {
    ClearBinkVideoPlaneSetupOutputs(args, base);
    return result;
  }

  const uint32_t aligned_plane_bytes = AlignFableBinkPlaneBytes(plane_bytes);
  const uint32_t plane_memory = callbacks.allocate_guest_memory(
      aligned_plane_bytes, FableTextureCreateMemoryFlags(0));
  if (!IsLikelyGuestMemoryRange(plane_memory, aligned_plane_bytes)) {
    ClearBinkVideoPlaneSetupOutputs(args, base);
    return result;
  }

  FillGuestBytes(base, plane_memory, aligned_plane_bytes, 0x80u);

  const uint32_t fetch_base = LoadGuestU32(base, texture + 32u);
  const uint32_t derived_pitch =
      args.r4 <= UINT32_MAX / header.bytes_per_pixel
          ? args.r4 * header.bytes_per_pixel
          : 0u;
  const uint32_t row_pitch =
      header.row_pitch_bytes != 0 ? header.row_pitch_bytes : derived_pitch;
  if (row_pitch == 0) {
    ClearBinkVideoPlaneSetupOutputs(args, base);
    return result;
  }
  StoreGuestU32(base, texture + 32u, fetch_base + plane_memory);
  StoreGuestU32(base, args.r6 + 4u, plane_memory);
  StoreGuestU32(base, args.r6 + 8u, row_pitch);
  UpdateFableLockDirtyRange(base, texture, 0x14u, plane_memory, plane_memory,
                            plane_bytes);
  StoreGuestU32(base, texture, LoadGuestU32(base, texture) + 0x100u);

  result.texture = texture;
  result.plane_memory = plane_memory;
  result.plane_bytes = plane_bytes;
  result.row_pitch_bytes = row_pitch;
  return result;
}

bool EmulateFableBinkVideoPlaneSurfaceSetup(uint8_t* base) {
  return CanEmulateFableBinkVideoPlaneSurfaceSetup(base);
}

}  // namespace refii::renderer::bink
