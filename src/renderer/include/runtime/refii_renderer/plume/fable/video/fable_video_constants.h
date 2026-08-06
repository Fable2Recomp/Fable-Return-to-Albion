#pragma once

#include <cstddef>
#include <cstdint>

namespace refii::renderer {

inline constexpr uint32_t kFableVideoFetchConstantDwords = 6u;

inline constexpr uint32_t kFableBinkIndexedVerticesWrapperBuilderSymbol =
    0x8220A3F0u;

inline constexpr uint64_t kFablePixRenderVideoFramePixelShaderHash =
    0x69E9772C76BD6253ull;
inline constexpr uint64_t kFableBinkIndexedWrapperPixelShaderHash =
    0x5DBB593C197FF7EEull;
inline constexpr uint64_t kFableStartupVideoCopyPixelShaderHash =
    0x022E603C28E1E163ull;

struct FableInlineShaderBlob {
  uint32_t guest_address = 0;
  uint32_t byte_size = 0;
  const char* dump_name = nullptr;
};

inline constexpr FableInlineShaderBlob kFablePixRenderVideoFrameInlineShaders[] = {
    {0x82095810u, 1236u, "pixrender_video_vs"},
    {0x82093C7Cu, 36u, "pixrender_video_ps"},
};

inline constexpr size_t kFablePixRenderVideoFrameInlineShaderCount =
    sizeof(kFablePixRenderVideoFrameInlineShaders) /
    sizeof(kFablePixRenderVideoFrameInlineShaders[0]);

}  // namespace refii::renderer
