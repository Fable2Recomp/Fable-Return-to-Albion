#pragma once

#include "runtime/refii_renderer/plume/fable/video/fable_pix_video_geometry.h"

#include <array>

namespace refii::renderer {

void FablePixVideoConfigureDrawEventShape(
    RendererDrawEventSnapshot& event,
    const std::array<float, 6>& vertices,
    const FablePixVideoUvScale& uv_scale);

void FableVideoConfigureGuiQuadVertexConstants(
    RendererDrawEventSnapshot& event);

void FableVideoConfigureSyntheticPrivateTexturePipelineState(
    RendererDrawEventSnapshot& event);

}  // namespace refii::renderer
