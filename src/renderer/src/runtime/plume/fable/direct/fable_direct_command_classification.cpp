#include "runtime/refii_renderer/plume/fable/direct/fable_direct_command_classification.h"

#include <algorithm>

namespace refii::renderer {

bool IsDirectDrawCommand(const FableDirectRenderCommand& command) {
  return command.type == FableDirectRenderCommandType::kDrawPrimitive ||
         command.type == FableDirectRenderCommandType::kDrawIndexedPrimitive ||
         command.type == FableDirectRenderCommandType::kDrawPrimitiveUP;
}

bool IsDirectActionCommand(const FableDirectRenderCommand& command) {
  return IsDirectDrawCommand(command) ||
         command.type == FableDirectRenderCommandType::kClear ||
         command.type == FableDirectRenderCommandType::kResolveCopy ||
         command.type == FableDirectRenderCommandType::kGtlResolve ||
         command.type == FableDirectRenderCommandType::kGtlTessDraw;
}

bool DirectDrawHasUnrecoverableMissingPipelineLocked(
    const FableDirectRenderCommand& command) {
  return IsDirectDrawCommand(command) &&
         (command.resources.vertex_shader == 0 ||
          command.resources.pixel_shader == 0);
}

bool DirectCommandFramebufferTargetsEmpty(
    const FableDirectRenderCommand& command) {
  return std::all_of(command.resources.render_targets.begin(),
                     command.resources.render_targets.end(),
                     [](uint32_t target) { return target == 0; }) &&
         command.resources.depth_stencil == 0;
}

bool DirectCommandHasCompleteShaderBinding(
    const FableDirectRenderCommand& command) {
  return command.resources.vertex_shader != 0 &&
         command.resources.pixel_shader != 0;
}

bool DirectRenderCommandTypeIsDraw(FableDirectRenderCommandType type) {
  return type == FableDirectRenderCommandType::kDrawPrimitive ||
         type == FableDirectRenderCommandType::kDrawIndexedPrimitive ||
         type == FableDirectRenderCommandType::kDrawPrimitiveUP;
}

uint64_t DirectRenderCommandDrawId(const FableDirectRenderCommand& command) {
  return command.draw_serial != 0 ? command.draw_serial : command.serial;
}

bool DirectDrawUsesSyntheticQuadListIndices(
    const FableDirectRenderCommand& command) {
  return !command.draw.draw.indexed && command.draw.draw.primitive_type == 13u;
}

uint32_t DirectQuadListIndexCount(uint32_t vertex_count) {
  return (vertex_count / 4u) * 6u;
}

int32_t DirectDrawBaseVertex(const FableDirectRenderCommand& command) {
  if (command.draw.base_vertex_valid) {
    return command.draw.base_vertex;
  }
  return static_cast<int32_t>(command.draw.draw.base_vertex);
}

}  // namespace refii::renderer
