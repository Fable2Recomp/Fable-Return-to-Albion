#include "runtime/refii_renderer/plume/fable/video/fable_pix_video_draw_event.h"

#include "runtime/cvars.h"
#include "runtime/refii_renderer/plume/fable/fable_guest_display.h"

#include <algorithm>
#include <bit>
#include <cmath>

namespace refii::renderer {
namespace {

// Native resolution (2026-08-02): the guest viewport fallback is the LIVE
// guest display extent (qword_834A5FE8 via FableGuestDisplayWidthF/HeightF),
// not a hardcoded 1280x720 — the synthetic video quad's clip transform must
// match the space the guest derived its vertex coordinates in.
constexpr uint32_t kFableRenderStateSlotColorWriteEnable = 1;
constexpr uint32_t kFableRenderStateSlotAlphaBlendEnable = 2;
constexpr uint32_t kFableRenderStateSlotSrcBlend = 3;
constexpr uint32_t kFableRenderStateSlotZWriteEnable = 4;
constexpr uint32_t kFableRenderStateSlotDestBlend = 5;
constexpr uint32_t kFableRenderStateSlotAlphaTestEnable = 6;
constexpr uint32_t kFableRenderStateSlotZEnable = 7;
constexpr uint32_t kFableRenderStateSlotHiZWriteEnable = 8;
constexpr uint32_t kFableRenderStateSlotSeparateAlphaBlendEnable = 13;
constexpr uint32_t kFableRenderStateSlotStencilEnable = 18;
constexpr uint32_t kFableRenderStateSlotCullMode = 19;
constexpr uint32_t kFablePixVideoSyntheticSamplerSlots = 3;
constexpr uint32_t kFablePixVideoPackedSamplerStateValid = 0x80000000u;
constexpr uint32_t kFablePixVideoPackedSamplerStateMagLinear = 0x00000001u;
constexpr uint32_t kFablePixVideoPackedSamplerStateMinLinear = 0x00000002u;
constexpr uint32_t kFablePixVideoPackedSamplerStateMipLinear = 0x00000008u;
constexpr uint32_t kFablePixVideoPackedSamplerStateAddressUShift = 8u;
constexpr uint32_t kFablePixVideoPackedSamplerStateAddressVShift = 11u;
constexpr uint32_t kFablePixVideoPackedSamplerStateAddressWShift = 14u;
constexpr uint32_t kFablePixVideoClampAddressMode = 2u;

uint32_t FableVideoFloatToBits(float value) {
  return std::bit_cast<uint32_t>(value);
}

uint32_t FablePixVideoLinearClampSamplerState() {
  return kFablePixVideoPackedSamplerStateValid |
         kFablePixVideoPackedSamplerStateMagLinear |
         kFablePixVideoPackedSamplerStateMinLinear |
         kFablePixVideoPackedSamplerStateMipLinear |
         (kFablePixVideoClampAddressMode
          << kFablePixVideoPackedSamplerStateAddressUShift) |
         (kFablePixVideoClampAddressMode
          << kFablePixVideoPackedSamplerStateAddressVShift) |
         (kFablePixVideoClampAddressMode
          << kFablePixVideoPackedSamplerStateAddressWShift);
}

}  // namespace

void FablePixVideoConfigureDrawEventShape(
    RendererDrawEventSnapshot& event,
    const std::array<float, 6>& vertices,
    const FablePixVideoUvScale& uv_scale) {
  event.private_texture_rectangle = true;
  event.private_texture_slot = 0;
  event.private_texture_corners = vertices;
  event.private_texture_corners_valid = true;

  FablePixVideoConfigureOutputState(
      event, FablePixVideoOutputWidthFromVertices(vertices),
      FablePixVideoOutputHeightFromVertices(vertices));
  FablePixVideoConfigurePixelConstants(event);
  const uint32_t video_sampler = FablePixVideoLinearClampSamplerState();
  for (uint32_t slot = 0; slot < kFablePixVideoSyntheticSamplerSlots &&
                          slot < event.sampler_states.size();
       ++slot) {
    event.sampler_states[slot] = video_sampler;
  }

  if (!uv_scale.valid) {
    return;
  }
  event.private_texture_uv_override = true;
  event.private_texture_u0 = 0.0f;
  event.private_texture_v0 = 0.0f;
  event.private_texture_u1 = uv_scale.u1;
  event.private_texture_v1 = uv_scale.v1;
}

void FableVideoConfigureGuiQuadVertexConstants(
    RendererDrawEventSnapshot& event) {
  auto& constants = event.vertex_shader_constants;
  constants.fill(0);

  float viewport_x = 0.0f;
  float viewport_y = 0.0f;
  float viewport_width = FableGuestDisplayWidthF();
  float viewport_height = FableGuestDisplayHeightF();
  if (event.viewport.valid && event.viewport.width > 0.0f &&
      event.viewport.height > 0.0f) {
    viewport_x = event.viewport.x;
    viewport_y = event.viewport.y;
    viewport_width = event.viewport.width;
    viewport_height = event.viewport.height;
  } else {
    viewport_width = static_cast<float>(
        std::max(1, RendererOutputWidth()) *
        std::max(1, RendererInternalScale()));
    viewport_height = static_cast<float>(
        std::max(1, RendererOutputHeight()) *
        std::max(1, RendererInternalScale()));
  }
  if (!std::isfinite(viewport_width) || viewport_width <= 0.0f) {
    viewport_width = FableGuestDisplayWidthF();
  }
  if (!std::isfinite(viewport_height) || viewport_height <= 0.0f) {
    viewport_height = FableGuestDisplayHeightF();
  }

  const auto store_constant = [&](uint32_t reg, float x, float y, float z,
                                  float w) {
    const uint32_t dword = reg * 4u;
    if (dword + 3u >= constants.size()) {
      return;
    }
    constants[dword + 0u] = FableVideoFloatToBits(x);
    constants[dword + 1u] = FableVideoFloatToBits(y);
    constants[dword + 2u] = FableVideoFloatToBits(z);
    constants[dword + 3u] = FableVideoFloatToBits(w);
  };

  const float clip_x_scale = 2.0f / viewport_width;
  const float clip_y_scale = -2.0f / viewport_height;
  const float clip_x_bias = -1.0f - viewport_x * clip_x_scale;
  const float clip_y_bias = 1.0f - viewport_y * clip_y_scale;

  store_constant(0, clip_x_scale, 0.0f, 0.0f, clip_x_bias);
  store_constant(1, 0.0f, clip_y_scale, 0.0f, clip_y_bias);
  store_constant(2, 0.0f, 0.0f, 0.0f, 0.0f);
  store_constant(3, 0.0f, 0.0f, 0.0f, 1.0f);
  store_constant(113, 1.0f, 0.0f, 0.0f, 0.0f);
  store_constant(114, 1.0f, 1.0f, 0.0f, 0.0f);
  store_constant(115, 0.0f, 0.0f, 0.0f, 0.0f);
}

void FableVideoConfigureSyntheticPrivateTexturePipelineState(
    RendererDrawEventSnapshot& event) {
  event.render_states[kFableRenderStateSlotColorWriteEnable] = 0xFu;
  event.render_states[kFableRenderStateSlotAlphaBlendEnable] = 0u;
  event.render_states[kFableRenderStateSlotSrcBlend] = 1u;
  event.render_states[kFableRenderStateSlotDestBlend] = 0u;
  event.render_states[kFableRenderStateSlotAlphaTestEnable] = 0u;
  event.render_states[kFableRenderStateSlotZEnable] = 0u;
  event.render_states[kFableRenderStateSlotZWriteEnable] = 0u;
  event.render_states[kFableRenderStateSlotHiZWriteEnable] = 0u;
  event.render_states[kFableRenderStateSlotCullMode] = 0u;
  event.render_states[kFableRenderStateSlotStencilEnable] = 0u;
  event.render_states[kFableRenderStateSlotSeparateAlphaBlendEnable] = 0u;

  event.pm4_registers_valid = true;
  event.rb_depthcontrol = 0u;
  event.rb_color_mask = 0xFFFFu;
  event.rb_colorcontrol = 0u;
  event.rb_alpha_ref = 0u;
  event.rb_stencilrefmask = 0x00FFFF00u;
  event.rb_stencilrefmask_bf = 0x00FFFF00u;
  event.pa_su_sc_mode_cntl = 0u;
  for (uint32_t& blend_control : event.rb_blendcontrol) {
    blend_control = 0x00010001u;
  }
}

}  // namespace refii::renderer
