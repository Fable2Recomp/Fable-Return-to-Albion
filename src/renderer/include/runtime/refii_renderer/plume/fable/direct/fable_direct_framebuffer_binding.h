#pragma once

#include "runtime/refii_renderer/plume/fable/direct/fable_direct_state.h"

#include <array>
#include <cstdint>
#include <tuple>

#include <plume_render_interface.h>

namespace refii::renderer {

constexpr size_t kFableDirectFramebufferColorCount =
    std::tuple_size_v<decltype(FableDirectResourceBindings{}.render_targets)>;

struct DirectFramebufferBinding {
  plume::RenderFramebuffer* framebuffer = nullptr;
  plume::RenderFormat color_format = plume::RenderFormat::B8G8R8A8_UNORM;
  plume::RenderFormat depth_format = plume::RenderFormat::UNKNOWN;
  plume::RenderTexture* color_texture = nullptr;
  plume::RenderTextureView* color_view = nullptr;
  plume::RenderTextureView* color_sample_view = nullptr;
  std::array<uint32_t, kFableDirectFramebufferColorCount> color_addresses = {};
  std::array<plume::RenderFormat, kFableDirectFramebufferColorCount>
      color_formats = {};
  std::array<plume::RenderTexture*, kFableDirectFramebufferColorCount>
      color_textures = {};
  std::array<plume::RenderTextureView*, kFableDirectFramebufferColorCount>
      color_views = {};
  std::array<plume::RenderTextureView*, kFableDirectFramebufferColorCount>
      color_sample_views = {};
  plume::RenderTexture* depth_texture = nullptr;
  plume::RenderTextureView* depth_view = nullptr;
  uint32_t color_address = 0;
  uint32_t depth_address = 0;
  uint32_t width = 0;
  uint32_t height = 0;
  // The extent the GUEST believes this target has. Equal to width/height when
  // the surface was not promoted, so `width / guest_width` is always the exact
  // scale from guest space to this attachment — 1 when nothing was promoted.
  // Zero only when no tracked colour/depth resource backed the binding, in
  // which case no scaling may be applied at all.
  uint32_t guest_width = 0;
  uint32_t guest_height = 0;
  uint32_t color_count = 0;
};

DirectFramebufferBinding FrameDatabaseFramebufferBindingForCommand(
    const FableDirectRenderCommand& command);

}  // namespace refii::renderer
