#pragma once

#include "renderer_runtime.h"

#include <cstdint>

#if REFII_RENDERER_HAS_PLUME
#include <plume_render_interface.h>
#endif

namespace refii::renderer {

struct FableDirectRendererStats {
  uint64_t frames_begun = 0;
  uint64_t frames_ended = 0;
  uint64_t presents = 0;
  uint32_t last_frame_index = 0;
  uint32_t draw_count = 0;
  uint32_t indexed_draw_count = 0;
  uint32_t resource_creations = 0;
  uint32_t resource_destroys = 0;
  uint32_t resource_uploads = 0;
  uint32_t live_resources = 0;
  uint32_t live_textures = 0;
  uint32_t live_surfaces = 0;
  uint32_t live_vertex_buffers = 0;
  uint32_t live_index_buffers = 0;
  uint32_t live_shaders = 0;
  uint32_t pipeline_cache_hits = 0;
  uint32_t pipeline_cache_misses = 0;
  uint32_t failed_hooks = 0;
  uint32_t failed_resource_syncs = 0;
  uint32_t last_resource_address = 0;
  uint32_t last_resource_size = 0;
  uint32_t last_present_target = 0;
  uint32_t last_present_width = 0;
  uint32_t last_present_height = 0;
  uint32_t last_render_commands = 0;
  uint32_t last_draw_events = 0;
  bool frame_active = false;
  bool presented = false;
  bool renderdoc_capture_started = false;
};

#if REFII_RENDERER_HAS_PLUME
void InitializeFableDirectRenderer(plume::RenderDevice* device,
                                   plume::RenderShaderFormat shader_format);
void RecordFableDirectFrame(plume::RenderDevice* device,
                            plume::RenderCommandList* command_list,
                            plume::RenderFramebuffer* output_framebuffer,
                            plume::RenderTexture* output_texture,
                            plume::RenderFramebuffer* present_framebuffer,
                            plume::RenderShaderFormat shader_format,
                            plume::RenderFormat output_depth_format,
                            uint32_t frame_index);
#endif

void BeginFableDirectFrame(uint32_t frame_index);
void EndFableDirectFrame();
void PresentFableDirectFrame(uint32_t present_target, uint32_t width,
                             uint32_t height);
FableDirectRendererStats GetFableDirectRendererStats();
void ShutdownFableDirectRenderer();

}  // namespace refii::renderer
