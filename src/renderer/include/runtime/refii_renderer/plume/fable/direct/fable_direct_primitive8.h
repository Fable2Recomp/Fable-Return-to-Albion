#pragma once

#include "runtime/plume/resource/plume_resource_manager_internal.h"
#include "runtime/refii_renderer/plume/fable/direct/fable_direct_state.h"

#include <array>
#include <cstddef>
#include <cstdint>

namespace refii::renderer {

bool BuildExpandedPrimitive8RectangleFromBytes(
    const uint8_t* data, size_t size, std::array<float, 12>& expanded_vertices,
    uint32_t stride = sizeof(float) * 2u);

bool BuildExpandedInlinePrimitive8Rectangle(
    const FableDirectInlineBuffer& inline_buffer,
    std::array<float, 12>& expanded_vertices);

bool BuildExpandedTrackedPrimitive8Rectangle(
    const PlumeTrackedResource& buffer, uint32_t offset,
    std::array<float, 12>& expanded_vertices);

bool InlinePrimitive8RectangleIsCollapsed(
    const FableDirectInlineBuffer& inline_buffer);

bool ShouldExpandInlinePrimitive8Rectangle(
    const FableDirectRenderCommand& command, uint32_t slot,
    bool synthetic_private_rect);

bool ShouldExpandPrimitive8Rectangle(
    const FableDirectRenderCommand& command, uint32_t slot,
    bool synthetic_private_rect, uint32_t stride);

}  // namespace refii::renderer
