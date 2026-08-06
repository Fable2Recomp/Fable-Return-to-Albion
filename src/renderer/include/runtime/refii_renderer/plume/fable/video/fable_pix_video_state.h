#pragma once

#include "runtime/refii_renderer/plume/fable/direct/fable_direct_state.h"
#include "runtime/refii_renderer/plume/fable/video/fable_pix_video_uploads.h"

#include <array>
#include <cstdint>

namespace refii::renderer {

struct FablePixVideoReplacementStateRestore {
  std::array<uint32_t, kFablePixVideoPlaneCount> textures = {};
  std::array<std::array<uint32_t, kFablePixVideoFetchConstantDwords>,
             kFablePixVideoPlaneCount>
      fetches = {};
  RendererStreamBindingSnapshot stream0;
};

FablePixVideoReplacementStateRestore FablePixVideoCaptureReplacementState(
    const FableDirectStateSnapshot& state);

void FablePixVideoRestoreReplacementState(
    uint32_t device,
    const FablePixVideoReplacementStateRestore& state);

}  // namespace refii::renderer
