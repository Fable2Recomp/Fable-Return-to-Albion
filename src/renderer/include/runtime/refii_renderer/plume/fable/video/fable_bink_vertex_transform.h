#pragma once

#include "runtime/refii_renderer/plume/fable/direct/fable_direct_state.h"

#include <array>
#include <cstdint>

namespace refii::renderer {

bool FableVideoInferBinkWrapperVertexExtent(
    const FableDirectInlineBuffer& vertex_buffer,
    uint32_t stride,
    uint32_t offset,
    float& width,
    float& height);

void FableVideoApplyBinkWrapperVertexTransform(
    std::array<uint32_t, kRendererShaderFloatConstantDwords>& constants,
    float width,
    float height);

}  // namespace refii::renderer
