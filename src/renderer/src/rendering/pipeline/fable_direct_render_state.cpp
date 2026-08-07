#include "runtime/refii_renderer/plume/fable/direct/fable_direct_render_state.h"

#include <rex/graphics/xenos.h>

namespace refii::renderer {

// Xenos ClampMode -> the packed sampler address encoding (values 0-3 and 6
// coincide; the half-way modes approximate to their edge/mirror-once forms).
namespace {
uint32_t PackedAddressFromXenosClamp(rex::graphics::xenos::ClampMode mode) {
  using rex::graphics::xenos::ClampMode;
  switch (mode) {
    case ClampMode::kRepeat:
      return 0u;
    case ClampMode::kMirroredRepeat:
      return 1u;
    case ClampMode::kClampToEdge:
    case ClampMode::kClampToHalfway:
      return 2u;
    case ClampMode::kMirrorClampToEdge:
    case ClampMode::kMirrorClampToHalfway:
      return 3u;
    case ClampMode::kClampToBorder:
    case ClampMode::kMirrorClampToBorder:
      return 6u;
  }
  return 0u;
}
}  // namespace

uint32_t DirectPackedSamplerStateWithFetchClampAddress(
    uint32_t packed_state, const std::array<uint32_t, 6>& fetch_dwords) {
  rex::graphics::xenos::xe_gpu_texture_fetch_t fetch = {};
  fetch.dword_0 = fetch_dwords[0];
  fetch.dword_1 = fetch_dwords[1];
  fetch.dword_2 = fetch_dwords[2];
  fetch.dword_3 = fetch_dwords[3];
  fetch.dword_4 = fetch_dwords[4];
  fetch.dword_5 = fetch_dwords[5];
  if (fetch.type != rex::graphics::xenos::FetchConstantType::kTexture) {
    return packed_state;
  }
  constexpr uint32_t kAddressBits =
      (kDirectPackedSamplerStateAddressMask
       << kDirectPackedSamplerStateAddressUShift) |
      (kDirectPackedSamplerStateAddressMask
       << kDirectPackedSamplerStateAddressVShift) |
      (kDirectPackedSamplerStateAddressMask
       << kDirectPackedSamplerStateAddressWShift);
  if ((packed_state & kDirectPackedSamplerStateValid) == 0) {
    packed_state = kDirectPackedSamplerStateValid |
                   kDirectPackedSamplerStateMagLinear |
                   kDirectPackedSamplerStateMinLinear |
                   kDirectPackedSamplerStateMipLinear;
  }
  packed_state &= ~kAddressBits;
  packed_state |= PackedAddressFromXenosClamp(fetch.clamp_x)
                  << kDirectPackedSamplerStateAddressUShift;
  packed_state |= PackedAddressFromXenosClamp(fetch.clamp_y)
                  << kDirectPackedSamplerStateAddressVShift;
  packed_state |= PackedAddressFromXenosClamp(fetch.clamp_z)
                  << kDirectPackedSamplerStateAddressWShift;
  return packed_state;
}

uint32_t DirectPackedSamplerStateWithClampAddress(uint32_t packed_state) {
  constexpr uint32_t kClampAddressMode = 2u;
  constexpr uint32_t kAddressBits =
      (kDirectPackedSamplerStateAddressMask
       << kDirectPackedSamplerStateAddressUShift) |
      (kDirectPackedSamplerStateAddressMask
       << kDirectPackedSamplerStateAddressVShift) |
      (kDirectPackedSamplerStateAddressMask
       << kDirectPackedSamplerStateAddressWShift);
  if ((packed_state & kDirectPackedSamplerStateValid) == 0) {
    packed_state = kDirectPackedSamplerStateValid |
                   kDirectPackedSamplerStateMagLinear |
                   kDirectPackedSamplerStateMinLinear |
                   kDirectPackedSamplerStateMipLinear;
  }
  packed_state &= ~kAddressBits;
  packed_state |= kClampAddressMode << kDirectPackedSamplerStateAddressUShift;
  packed_state |= kClampAddressMode << kDirectPackedSamplerStateAddressVShift;
  packed_state |= kClampAddressMode << kDirectPackedSamplerStateAddressWShift;
  return packed_state;
}

uint32_t DirectPackedBlendControlFromRenderStates(
    const std::array<uint32_t, kRendererD3DRenderStateSlots>& states) {
  const uint32_t packed =
      (states[kFableRenderStateSrcBlend] & 0x1Fu) |
      ((states[kFableRenderStateBlendOp] & 0x7u) << 5u) |
      ((states[kFableRenderStateDestBlend] & 0x1Fu) << 8u) |
      ((states[kFableRenderStateSrcBlendAlpha] & 0x1Fu) << 16u) |
      ((states[kFableRenderStateBlendOpAlpha] & 0x7u) << 21u) |
      ((states[kFableRenderStateDestBlendAlpha] & 0x1Fu) << 24u);
  if (states[kFableRenderStateAlphaBlendEnable] == 0) {
    return kDirectDefaultBlendControl;
  }
  if (states[kFableRenderStateSeparateAlphaBlendEnable] != 0) {
    return packed;
  }
  return (((packed & 0x1010u) | ((packed << 4u) & 0xFFFF0u)) << 12u) &
             0xEFEF0000u |
         (packed & 0xFFFFu);
}

uint32_t DirectBlendControlForTarget(const FableDirectRenderCommand& command,
                                     uint32_t slot) {
  if (command.pipeline.pm4.valid &&
      slot < command.pipeline.pm4.rb_blendcontrol.size()) {
    return command.pipeline.pm4.rb_blendcontrol[slot];
  }
  return DirectPackedBlendControlFromRenderStates(command.pipeline.render_states);
}

bool DirectBlendEnabledForTarget(const FableDirectRenderCommand& command,
                                 uint32_t slot) {
  const uint32_t blend_control = DirectBlendControlForTarget(command, slot);
  return command.pipeline.render_states[kFableRenderStateAlphaBlendEnable] != 0 ||
         (command.pipeline.pm4.valid &&
          blend_control != kDirectDefaultBlendControl);
}

uint8_t DirectColorWriteMaskForTarget(
    const FableDirectRenderCommand& command, uint32_t slot) {
  if (command.pipeline.pm4.valid) {
    return static_cast<uint8_t>(
        (command.pipeline.pm4.rb_color_mask >> (slot * 4u)) & 0xFu);
  }
  return static_cast<uint8_t>(
      command.pipeline.render_states[kFableRenderStateColorWriteEnable] & 0xFu);
}

uint8_t DirectColorWriteMaskForFramebufferTarget(
    const FableDirectRenderCommand& command,
    const DirectFramebufferBinding& framebuffer, uint32_t framebuffer_slot) {
  if (!command.pipeline.pm4.valid) {
    return DirectColorWriteMaskForTarget(command, framebuffer_slot);
  }

  const auto raw_mask_for_slot = [&](uint32_t slot) -> uint8_t {
    if (slot >= kFableDirectFramebufferColorCount) {
      return 0;
    }
    return static_cast<uint8_t>(
        (command.pipeline.pm4.rb_color_mask >> (slot * 4u)) & 0xFu);
  };

  const uint8_t direct_mask = raw_mask_for_slot(framebuffer_slot);
  const uint32_t framebuffer_target =
      framebuffer_slot < framebuffer.color_addresses.size()
          ? framebuffer.color_addresses[framebuffer_slot]
          : 0u;
  if (framebuffer_target != 0) {
    for (uint32_t command_slot = 0;
         command_slot < kFableDirectFramebufferColorCount &&
         command_slot < command.resources.render_targets.size();
         ++command_slot) {
      if (command.resources.render_targets[command_slot] !=
          framebuffer_target) {
        continue;
      }
      const uint8_t command_slot_mask = raw_mask_for_slot(command_slot);
      if (command_slot_mask != 0 || command_slot != framebuffer_slot) {
        return command_slot_mask;
      }
    }
  }

  if (framebuffer.color_count == 1 && framebuffer_slot == 0 &&
      framebuffer_target != 0 && direct_mask == 0) {
    uint8_t merged_single_target_mask = 0;
    for (uint32_t slot = 0; slot < kFableDirectFramebufferColorCount; ++slot) {
      merged_single_target_mask |= raw_mask_for_slot(slot);
    }
    if (merged_single_target_mask != 0) {
      return merged_single_target_mask;
    }
  }

  return direct_mask;
}


plume::RenderBlendDesc DirectBlendDescFromEffectiveState(
    bool blend_enabled, uint32_t blend_control, uint8_t color_write_mask) {
  plume::RenderBlendDesc desc = plume::RenderBlendDesc::Copy();
  desc.blendEnabled = blend_enabled;
  desc.srcBlend = ConvertFableBlend(blend_control & 0x1Fu,
                                    plume::RenderBlend::ONE);
  desc.blendOp = ConvertFableBlendOp((blend_control >> 5u) & 0x7u,
                                     plume::RenderBlendOperation::ADD);
  desc.dstBlend = ConvertFableBlend((blend_control >> 8u) & 0x1Fu,
                                    plume::RenderBlend::ZERO);
  desc.srcBlendAlpha = ConvertFableAlphaBlend(
      (blend_control >> 16u) & 0x1Fu, desc.srcBlend);
  desc.blendOpAlpha = ConvertFableBlendOp(
      (blend_control >> 21u) & 0x7u, desc.blendOp);
  desc.dstBlendAlpha = ConvertFableAlphaBlend(
      (blend_control >> 24u) & 0x1Fu, desc.dstBlend);
  desc.renderTargetWriteMask = color_write_mask;
  if (!desc.blendEnabled) {
    const uint8_t write_mask = desc.renderTargetWriteMask;
    desc = plume::RenderBlendDesc::Copy();
    desc.renderTargetWriteMask = write_mask;
  }
  return desc;
}

uint32_t DirectDepthControlFromRenderStates(
    const std::array<uint32_t, kRendererD3DRenderStateSlots>& states) {
  const uint32_t stencil_func = states[kFableRenderStateStencilFunc] & 0x7u;
  const uint32_t stencil_fail = states[kFableRenderStateStencilFail] & 0x7u;
  const uint32_t stencil_pass = states[kFableRenderStateStencilPass] & 0x7u;
  const uint32_t stencil_zfail = states[kFableRenderStateStencilZFail] & 0x7u;
  return ((states[kFableRenderStateStencilEnable] & 0x1u) << 0u) |
         ((states[kFableRenderStateZEnable] & 0x1u) << 1u) |
         ((states[kFableRenderStateZWriteEnable] & 0x1u) << 2u) |
         ((states[kFableRenderStateZFunc] & 0x7u) << 4u) |
         (stencil_func << 8u) | (stencil_fail << 11u) |
         (stencil_pass << 14u) | (stencil_zfail << 17u) |
         (stencil_func << 20u) | (stencil_fail << 23u) |
         (stencil_pass << 26u) | (stencil_zfail << 29u);
}

uint32_t DirectDepthControl(const FableDirectRenderCommand& command) {
  // Live device word first: it IS the register the ring will carry, so it
  // needs no reconstruction and cannot be defeated by a state dispatch we
  // failed to observe. See FableDirectPipelineState::live_depth_control.
  if (command.pipeline.live_depth_control_valid) {
    return command.pipeline.live_depth_control;
  }
  return command.pipeline.pm4.valid
             ? command.pipeline.pm4.rb_depthcontrol
             : DirectDepthControlFromRenderStates(command.pipeline.render_states);
}

uint32_t DirectStencilRefMaskFromRenderStates(
    const std::array<uint32_t, kRendererD3DRenderStateSlots>& states) {
  return (states[kFableRenderStateStencilRef] & 0xFFu) |
         ((states[kFableRenderStateStencilReadMask] & 0xFFu) << 8u) |
         ((states[kFableRenderStateStencilWriteMask] & 0xFFu) << 16u);
}

uint32_t DirectStencilRefMask(const FableDirectRenderCommand& command,
                              bool back_face) {
  if (command.pipeline.pm4.valid) {
    return back_face ? command.pipeline.pm4.rb_stencilrefmask_bf
                     : command.pipeline.pm4.rb_stencilrefmask;
  }
  return DirectStencilRefMaskFromRenderStates(command.pipeline.render_states);
}

}  // namespace refii::renderer
