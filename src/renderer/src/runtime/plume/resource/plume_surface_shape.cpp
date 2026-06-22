#include "runtime/plume/resource/plume_resource_manager_internal.h"

#include <algorithm>
#include <cstddef>
#include <cstdint>
#include <vector>

namespace refii::renderer {

#if REFII_RENDERER_HAS_PLUME
bool IsLikelyFableObjectAddress(uint32_t guest_address) {
  return guest_address >= 0x40000000u && guest_address < 0x80000000u &&
         (guest_address & (alignof(uint32_t) - 1u)) == 0;
}

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

bool FableWordsLookLikeInlinePathString(const std::vector<uint32_t>& words) {
  if (words.size() < 4) {
    return false;
  }

  uint32_t printable_words = 0;
  bool has_path_byte = false;
  const size_t word_count = std::min<size_t>(words.size(), 16);
  for (size_t i = 1; i < word_count; ++i) {
    if (!IsPackedPrintableAsciiWord(words[i])) {
      continue;
    }
    ++printable_words;
    has_path_byte = has_path_byte || PackedAsciiWordContainsPathByte(words[i]);
  }

  return has_path_byte && printable_words >= 2;
}

void AppendFableHandlePointerCandidates(const std::vector<uint32_t>& words,
                                        std::vector<uint32_t>& candidates) {
  if (words.empty()) {
    return;
  }

  const bool inline_path = FableWordsLookLikeInlinePathString(words);
  if (inline_path) {
    constexpr size_t kInlinePathPointerWords[] = {
        0,   // Next wrapper/list node.
        12,  // Secondary payload pointer after the inline path payload.
    };
    for (const size_t index : kInlinePathPointerWords) {
      if (index >= words.size() ||
          !IsLikelyFableObjectAddress(words[index])) {
        continue;
      }
      candidates.push_back(words[index]);
    }
    return;
  }

  for (uint32_t word : words) {
    if (IsLikelyFableObjectAddress(word)) {
      candidates.push_back(word);
    }
  }
}

bool IsPlausibleFableSurfaceShape(const FableSurfaceShape& shape) {
  if (shape.width == 0 || shape.height == 0 || shape.width > 8192u ||
      shape.height > 8192u || shape.multisample_type > 3u) {
    return false;
  }

  const uint32_t max_expected_pitch =
      std::max(shape.width * 2u, shape.width + 512u);
  if (shape.pitch_width != 0 &&
      (shape.pitch_width < shape.width ||
       shape.pitch_width > max_expected_pitch)) {
    return false;
  }
  if (shape.aligned_width != 0 &&
      (shape.aligned_width < shape.width ||
       shape.aligned_width > max_expected_pitch)) {
    return false;
  }

  return true;
}

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

bool IsPlausibleDecodedFableColorRenderTargetShape(
    const FableSurfaceShape& shape) {
  if (!IsPlausibleFableSurfaceShape(shape)) {
    return false;
  }

  // Fable 2's D3D9 render targets seen on the hook path are screen-sized or
  // small post-process surfaces. Treat huge fallback-decoded color targets as
  // false positives so a random surface-looking object cannot make Plume
  // allocate and direct through an enormous offscreen framebuffer.
  constexpr uint32_t kMinDecodedColorTargetExtent = 16;
  if (shape.width < kMinDecodedColorTargetExtent ||
      shape.height < kMinDecodedColorTargetExtent) {
    return false;
  }
  if (!IsPlausibleFableColorRenderTargetExtent(shape.width, shape.height)) {
    return false;
  }

  return true;
}

bool IsPlausibleDecodedFableDepthTargetShape(
    const FableSurfaceShape& shape) {
  if (!IsPlausibleFableSurfaceShape(shape)) {
    return false;
  }

  // The Fable 2 depth targets seen on the D3D9 path are real render/depth
  // surfaces, not single-column metadata-looking aliases. Reject tiny decoded
  // fallback shapes so an object with surface-like words cannot replace a
  // valid tracked depth target.
  constexpr uint32_t kMinDecodedDepthTargetExtent = 16;
  constexpr uint32_t kMaxDecodedDepthTargetExtent = 4096;
  constexpr uint64_t kMaxDecodedDepthTargetPixels =
      static_cast<uint64_t>(kMaxDecodedDepthTargetExtent) *
      static_cast<uint64_t>(kMaxDecodedDepthTargetExtent);
  return shape.width >= kMinDecodedDepthTargetExtent &&
         shape.height >= kMinDecodedDepthTargetExtent &&
         shape.width <= kMaxDecodedDepthTargetExtent &&
         shape.height <= kMaxDecodedDepthTargetExtent &&
         static_cast<uint64_t>(shape.width) * shape.height <=
             kMaxDecodedDepthTargetPixels;
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

bool DecodeFableSurfaceExtentFromWords(const std::vector<uint32_t>& words,
                                       uint32_t& width, uint32_t& height) {
  constexpr size_t kPackedExtentWord = 9;
  constexpr size_t kPackedDimensionWord = 12;
  if (FableWordsLookLikeInlinePathString(words)) {
    return false;
  }
  if (words.size() <= kPackedExtentWord || words[kPackedExtentWord] == 0) {
    return false;
  }

  const uint32_t packed_dimension =
      words.size() > kPackedDimensionWord ? words[kPackedDimensionWord] : 0;
  return DecodeFableSurfaceExtentFromPacked(words[kPackedExtentWord],
                                           packed_dimension, width, height);
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

plume::RenderFormat DecodeFableSurfaceRenderFormatFromWords(
    const std::vector<uint32_t>& words, RendererTrackedResourceKind kind) {
  constexpr size_t kPackedRenderTargetInfoWord = 7;
  constexpr size_t kPackedDepthFormatWord = 10;
  if (IsDepthSurfaceResource(kind)) {
    const uint32_t d3d_format =
        words.size() > kPackedDepthFormatWord
            ? (words[kPackedDepthFormatWord] & 0x3Fu)
            : 0;
    return ConvertFableD3DDepthFormat(d3d_format);
  }
  if (words.size() <= kPackedRenderTargetInfoWord) {
    return plume::RenderFormat::UNKNOWN;
  }

  namespace xenos = rex::graphics::xenos;
  const auto color_format = static_cast<xenos::ColorRenderTargetFormat>(
      (words[kPackedRenderTargetInfoWord] >> 16u) & 0xFu);
  return ConvertXenosColorRenderTargetFormat(color_format);
}

bool DecodeFableSurfaceShapeFromWords(const std::vector<uint32_t>& words,
                                      RendererTrackedResourceKind kind,
                                      FableSurfaceShape& shape) {
  constexpr size_t kPackedPitchWord = 6;
  if (FableWordsLookLikeInlinePathString(words)) {
    return false;
  }

  uint32_t width = 0;
  uint32_t height = 0;
  if (!DecodeFableSurfaceExtentFromWords(words, width, height)) {
    return false;
  }

  shape.width = width;
  shape.height = height;
  shape.format = DecodeFableSurfaceRenderFormatFromWords(words, kind);
  if ((IsDepthSurfaceResource(kind) &&
       (!IsUsablePlumeTextureFormat(shape.format) ||
        !plume::RenderFormatIsDepth(shape.format))) ||
      (!IsDepthSurfaceResource(kind) &&
       !IsUsablePlumeColorTargetFormat(shape.format))) {
    return false;
  }
  if (words.size() > kPackedPitchWord) {
    const uint32_t packed_pitch = words[kPackedPitchWord];
    const uint32_t width_and_msaa = packed_pitch >> 16u;
    shape.multisample_type = width_and_msaa & 0x3u;
    shape.aligned_width = (width_and_msaa & ~0x3u) >> 2u;
    shape.pitch_width = packed_pitch & 0x3FFFu;
  }
  return IsPlausibleFableSurfaceShape(shape);
}
#endif

}  // namespace refii::renderer
