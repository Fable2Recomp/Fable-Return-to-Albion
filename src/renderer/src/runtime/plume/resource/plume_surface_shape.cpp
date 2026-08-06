#include "runtime/plume/resource/plume_resource_manager_internal.h"

#include <algorithm>
#include <cstddef>
#include <cstdint>
#include <vector>

namespace refii::renderer {

#if REFII_RENDERER_HAS_PLUME
namespace {

bool IsPackedPrintableAsciiWord(uint32_t word) {
  uint32_t printable_bytes = 0;
  for (uint32_t byte_index = 0; byte_index < 4; ++byte_index) {
    const uint8_t byte =
        static_cast<uint8_t>((word >> (byte_index * 8u)) & 0xFFu);
    if (byte >= 0x20u && byte <= 0x7Eu) {
      ++printable_bytes;
    }
  }
  return printable_bytes >= 3;
}

bool PackedAsciiWordContainsPathByte(uint32_t word) {
  for (uint32_t byte_index = 0; byte_index < 4; ++byte_index) {
    const uint8_t byte =
        static_cast<uint8_t>((word >> (byte_index * 8u)) & 0xFFu);
    if (byte == '\\' || byte == '/' || byte == '.') {
      return true;
    }
  }
  return false;
}

}  // namespace




bool IsPlausibleFableColorRenderTargetExtent(uint32_t width, uint32_t height) {
  constexpr uint32_t kMaxDecodedColorTargetExtent = 2048;
  constexpr uint64_t kMaxDecodedColorTargetPixels =
      static_cast<uint64_t>(kMaxDecodedColorTargetExtent) *
      static_cast<uint64_t>(kMaxDecodedColorTargetExtent);
  return width != 0 && height != 0 &&
         width <= kMaxDecodedColorTargetExtent &&
         height <= kMaxDecodedColorTargetExtent &&
         static_cast<uint64_t>(width) * height <=
             kMaxDecodedColorTargetPixels;
}



bool DecodeFableSurfaceExtentFromPacked(uint32_t packed_extent,
                                        uint32_t packed_dimension,
                                        uint32_t& width, uint32_t& height,
                                        uint32_t* depth) {
  (void)packed_dimension;
  if (packed_extent == 0) {
    return false;
  }

  const uint32_t decoded_width = (packed_extent >> 18u) + 1u;
  const uint32_t decoded_height = ((packed_extent >> 3u) & 0x7FFFu) + 1u;
  constexpr uint32_t decoded_depth = 1;

  if (decoded_width == 0 || decoded_height == 0 || decoded_width > 8192u ||
      decoded_height > 8192u || decoded_depth > 8192u) {
    return false;
  }

  width = decoded_width;
  height = decoded_height;
  if (depth) {
    *depth = decoded_depth;
  }
  return true;
}


plume::RenderFormat ConvertFableD3DDepthFormat(uint32_t d3d_format) {
  switch (d3d_format & 0x3Fu) {
    case 6:   // D3DFMT_D16_LOCKABLE & 0x3F.
    case 16:  // D3DFMT_D16 & 0x3F.
      return plume::RenderFormat::D16_UNORM;
    case 7:   // D3DFMT_D32 & 0x3F.
    case 13:  // D3DFMT_D24X8 & 0x3F.
    case 18:  // D3DFMT_D32F_LOCKABLE & 0x3F.
    case 20:  // D3DFMT_D32_LOCKABLE & 0x3F.
      return plume::RenderFormat::D32_FLOAT;
    case 9:   // D3DFMT_D15S1 & 0x3F.
    case 11:  // D3DFMT_D24S8 & 0x3F.
    case 15:  // D3DFMT_D24X4S4 & 0x3F.
    case 19:  // D3DFMT_D24FS8 & 0x3F.
    case 0:
    default:
      return plume::RenderFormat::D32_FLOAT_S8_UINT;
  }
}


#endif

}  // namespace refii::renderer
