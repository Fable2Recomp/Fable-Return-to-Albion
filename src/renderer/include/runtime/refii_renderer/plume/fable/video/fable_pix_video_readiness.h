#pragma once

#include "runtime/refii_renderer/plume/fable/video/fable_pix_video_uploads.h"

#include <array>
#include <cstdint>

namespace refii::renderer {

struct FablePixVideoPlaneReadinessFacts {
  bool pix_video_draw = false;
  std::array<uint32_t, kFablePixVideoPlaneCount> guests = {};
  std::array<bool, kFablePixVideoPlaneCount> ready = {};
};

struct FablePixVideoPlaneTextureInterests {
  std::array<uint32_t, kFablePixVideoPlaneCount> textures = {};
  uint32_t count = 0;
};

bool FablePixVideoPlaneSetReady(const FablePixVideoPlaneReadinessFacts& facts,
                                uint32_t* missing_slot,
                                uint32_t* missing_guest);

FablePixVideoPlaneTextureInterests FablePixVideoCollectPlaneTextureInterests(
    const std::array<uint32_t, kRendererD3DTextureSlots>& textures);

}  // namespace refii::renderer
