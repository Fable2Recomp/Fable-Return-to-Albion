#include "runtime/refii_renderer/plume/fable/direct/fable_direct_scratch_wrapper_state.h"

#include "runtime/refii_renderer/plume/fable/direct/fable_direct_command_classification.h"
#include "runtime/refii_renderer/plume/fable/private_rect/fable_direct_private_rect_symbols.h"
#include "runtime/refii_renderer/plume/fable/private_rect/fable_direct_synthetic_private_rect.h"
#include "runtime/refii_renderer/plume/fable/video/fable_video_constants.h"

#include <rex/logging.h>

#include <algorithm>

namespace refii::renderer {
namespace {

bool DirectCommandIsBeginIndexedScratchWrapper(
    const FableDirectRenderCommand& command) {
  return IsDirectDrawCommand(command) &&
         (command.draw.source == RendererDrawEventSource::kScratchWrapper ||
          command.draw.source == RendererDrawEventSource::kSceneObject) &&
         (command.draw.private_texture_wrapper_symbol_address ==
              kFableD3DBeginIndexedVerticesWrapperSymbol ||
          command.draw.private_texture_wrapper_symbol_address ==
              0x821D7550u ||
          command.draw.private_texture_wrapper_symbol_address ==
              kFableBinkIndexedVerticesWrapperBuilderSymbol ||
          command.draw.private_texture_wrapper_symbol_address ==
              0x8222E120u ||
          command.draw.private_texture_wrapper_symbol_address ==
              0x82AA59B8u) &&
         command.draw.draw.indexed;
}

bool DirectScratchWrapperBindingHasFramebuffer(
    const DirectScratchWrapperShaderBinding& binding) {
  return std::any_of(binding.render_targets.begin(),
                     binding.render_targets.end(),
                     [](uint32_t target) { return target != 0; }) ||
         binding.depth_stencil != 0;
}

void RememberDirectScratchWrapperShaderBinding(
    const FableDirectRenderCommand& command,
    DirectScratchWrapperShaderBinding& binding,
    uint32_t current_frame_index) {
  if (!DirectCommandIsBeginIndexedScratchWrapper(command) ||
      !DirectCommandHasCompleteShaderBinding(command) ||
      DirectCommandFramebufferTargetsEmpty(command)) {
    return;
  }

  binding.vertex_declaration = command.resources.vertex_declaration;
  binding.vertex_shader = command.resources.vertex_shader;
  binding.pixel_shader = command.resources.pixel_shader;
  binding.shader_roots = command.resources.shader_roots;
  binding.render_targets = command.resources.render_targets;
  binding.depth_stencil = command.resources.depth_stencil;
  binding.frame_index = current_frame_index;
}

bool ApplyDirectScratchWrapperShaderBinding(
    FableDirectRenderCommand& command,
    const DirectScratchWrapperShaderBinding& binding,
    uint32_t current_frame_index) {
  if (!DirectCommandIsBeginIndexedScratchWrapper(command) ||
      binding.frame_index == 0 || binding.frame_index > current_frame_index) {
    return false;
  }

  constexpr uint32_t kRecentScratchWrapperShaderFrames = 3600u;
  if (current_frame_index - binding.frame_index >
      kRecentScratchWrapperShaderFrames) {
    return false;
  }

  const bool needs_vertex_shader = command.resources.vertex_shader == 0;
  const bool needs_pixel_shader = command.resources.pixel_shader == 0;
  const bool needs_framebuffer = DirectCommandFramebufferTargetsEmpty(command);
  const bool can_apply_vertex_shader =
      needs_vertex_shader && binding.vertex_shader != 0;
  const bool can_apply_pixel_shader =
      needs_pixel_shader && binding.pixel_shader != 0 &&
      !IsFableSyntheticPrivateRectCopyShader(binding.pixel_shader);
  const bool can_apply_framebuffer =
      needs_framebuffer && DirectScratchWrapperBindingHasFramebuffer(binding);
  const bool can_apply_vertex_declaration =
      command.resources.vertex_declaration == 0 &&
      binding.vertex_declaration != 0;
  bool applied = false;
  if (!can_apply_vertex_shader && !can_apply_pixel_shader &&
      !can_apply_framebuffer && !can_apply_vertex_declaration) {
    return false;
  }

  if (can_apply_vertex_shader) {
    command.resources.vertex_shader = binding.vertex_shader;
    if (command.resources.shader_roots.vertex_shader.call_serial == 0) {
      command.resources.shader_roots.vertex_shader =
          binding.shader_roots.vertex_shader;
    }
    applied = true;
  }
  if (can_apply_pixel_shader) {
    command.resources.pixel_shader = binding.pixel_shader;
    if (command.resources.shader_roots.pixel_shader.call_serial == 0) {
      command.resources.shader_roots.pixel_shader =
          binding.shader_roots.pixel_shader;
    }
    applied = true;
  }
  if (can_apply_vertex_declaration) {
    command.resources.vertex_declaration = binding.vertex_declaration;
    applied = true;
  }
  if (can_apply_framebuffer) {
    command.resources.render_targets = binding.render_targets;
    command.resources.depth_stencil = binding.depth_stencil;
    applied = true;
  }
  return applied;
}

void LogDirectInheritedScratchWrapperShaders(
    const FableDirectRenderCommand& command,
    const DirectScratchWrapperShaderBinding& binding,
    const char* inheritance_source,
    uint32_t current_frame_index) {
  static uint32_t logged_inherited_scratch_wrapper_shaders = 0;
  if (logged_inherited_scratch_wrapper_shaders >= 48u) {
    return;
  }
  REXLOG_INFO(
      "Refii plume exact inherited scratch-wrapper shader state #{} frame={} "
      "inherit={} serial={} draw={} source={} wrapper=0x{:08X}/{} "
      "context=0x{:016X} rt=0x{:08X} ds=0x{:08X} decl=0x{:08X} "
      "vs=0x{:08X} ps=0x{:08X} state_decl=0x{:08X} "
      "state_vs=0x{:08X} state_ps=0x{:08X} vs_root=0x{:08X}/{} "
      "ps_root=0x{:08X}/{} state_rt=0x{:08X} state_ds=0x{:08X}",
      ++logged_inherited_scratch_wrapper_shaders, current_frame_index,
      inheritance_source ? inheritance_source : "unknown",
      static_cast<unsigned long long>(command.serial),
      static_cast<unsigned long long>(command.draw_serial),
      static_cast<uint32_t>(command.draw.source),
      command.draw.private_texture_wrapper_symbol_address,
      static_cast<unsigned long long>(
          command.draw.private_texture_wrapper_call_serial),
      static_cast<unsigned long long>(
          command.draw.private_texture_wrapper_context_key),
      command.resources.render_targets[0], command.resources.depth_stencil,
      command.resources.vertex_declaration, command.resources.vertex_shader,
      command.resources.pixel_shader, binding.vertex_declaration,
      binding.vertex_shader, binding.pixel_shader,
      binding.shader_roots.vertex_shader.symbol_address,
      static_cast<unsigned long long>(
          binding.shader_roots.vertex_shader.call_serial),
      binding.shader_roots.pixel_shader.symbol_address,
      static_cast<unsigned long long>(
          binding.shader_roots.pixel_shader.call_serial),
      binding.render_targets[0], binding.depth_stencil);
}

}  // namespace

void NormalizeDirectInheritedScratchWrapperShaders(
    std::vector<FableDirectRenderCommand>& commands,
    const FableDirectStateSnapshot& staged_snapshot,
    DirectScratchWrapperShaderBinding& recent_binding,
    uint32_t current_frame_index) {
  DirectScratchWrapperShaderBinding local_binding;
  DirectScratchWrapperShaderBinding staged_binding;
  staged_binding.vertex_declaration =
      staged_snapshot.shaders.vertex_declaration;
  staged_binding.vertex_shader = staged_snapshot.shaders.vertex_shader;
  staged_binding.pixel_shader = staged_snapshot.shaders.pixel_shader;
  staged_binding.shader_roots = staged_snapshot.shader_roots;
  staged_binding.render_targets = staged_snapshot.targets.color;
  staged_binding.depth_stencil = staged_snapshot.targets.depth;
  staged_binding.frame_index = current_frame_index;

  for (FableDirectRenderCommand& command : commands) {
    // Superseded / backlog-capped commands are marked dropped rather than
    // erased (the record strip sweeps them after this runs); before the
    // mark-and-sweep cutover they were already gone here, so keep them out
    // of the binding propagation.
    if (command.record_dropped) {
      continue;
    }
    switch (command.type) {
      case FableDirectRenderCommandType::kSetRenderTarget:
        if (command.slot < local_binding.render_targets.size()) {
          local_binding.render_targets[command.slot] = command.value;
        }
        local_binding.frame_index = current_frame_index;
        break;
      case FableDirectRenderCommandType::kSetDepthStencil:
        local_binding.depth_stencil = command.value;
        local_binding.frame_index = current_frame_index;
        break;
      case FableDirectRenderCommandType::kSetVertexDeclaration:
        local_binding.vertex_declaration = command.value;
        break;
      case FableDirectRenderCommandType::kSetVertexShader:
        local_binding.vertex_shader = command.value;
        local_binding.shader_roots.vertex_shader = command.state_root;
        local_binding.frame_index = current_frame_index;
        break;
      case FableDirectRenderCommandType::kSetPixelShader:
        local_binding.pixel_shader = command.value;
        local_binding.shader_roots.pixel_shader = command.state_root;
        local_binding.frame_index = current_frame_index;
        break;
      default:
        break;
    }

    RememberDirectScratchWrapperShaderBinding(command, recent_binding,
                                              current_frame_index);
    if (!DirectCommandIsBeginIndexedScratchWrapper(command) ||
        (DirectCommandHasCompleteShaderBinding(command) &&
         !DirectCommandFramebufferTargetsEmpty(command))) {
      continue;
    }
    bool inherited_state = false;
    if (ApplyDirectScratchWrapperShaderBinding(command, local_binding,
                                               current_frame_index)) {
      LogDirectInheritedScratchWrapperShaders(command, local_binding, "local",
                                              current_frame_index);
      inherited_state = true;
    }
    if ((!DirectCommandHasCompleteShaderBinding(command) ||
         DirectCommandFramebufferTargetsEmpty(command)) &&
        ApplyDirectScratchWrapperShaderBinding(command, staged_binding,
                                               current_frame_index)) {
      LogDirectInheritedScratchWrapperShaders(command, staged_binding, "staged",
                                              current_frame_index);
      inherited_state = true;
    }
    if ((!DirectCommandHasCompleteShaderBinding(command) ||
         DirectCommandFramebufferTargetsEmpty(command)) &&
        ApplyDirectScratchWrapperShaderBinding(command, recent_binding,
                                               current_frame_index)) {
      LogDirectInheritedScratchWrapperShaders(command, recent_binding, "recent",
                                              current_frame_index);
      inherited_state = true;
    }
    if (inherited_state) {
      RememberDirectScratchWrapperShaderBinding(command, recent_binding,
                                                current_frame_index);
    }
  }
}

}  // namespace refii::renderer
