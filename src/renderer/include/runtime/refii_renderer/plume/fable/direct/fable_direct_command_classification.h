#pragma once

#include "runtime/refii_renderer/plume/fable/direct/fable_direct_state.h"

#include <cstdint>

namespace refii::renderer {

bool IsDirectDrawCommand(const FableDirectRenderCommand& command);
bool IsDirectActionCommand(const FableDirectRenderCommand& command);
bool DirectDrawHasUnrecoverableMissingPipelineLocked(
    const FableDirectRenderCommand& command);
bool DirectCommandFramebufferTargetsEmpty(
    const FableDirectRenderCommand& command);
bool DirectCommandHasCompleteShaderBinding(
    const FableDirectRenderCommand& command);
bool DirectRenderCommandTypeIsDraw(FableDirectRenderCommandType type);
uint64_t DirectRenderCommandDrawId(const FableDirectRenderCommand& command);
bool DirectDrawUsesSyntheticQuadListIndices(
    const FableDirectRenderCommand& command);
uint32_t DirectQuadListIndexCount(uint32_t vertex_count);
int32_t DirectDrawBaseVertex(const FableDirectRenderCommand& command);

}  // namespace refii::renderer
