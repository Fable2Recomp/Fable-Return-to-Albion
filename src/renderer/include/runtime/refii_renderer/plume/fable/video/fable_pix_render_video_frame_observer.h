#pragma once

#include "renderer_runtime.h"
#include "runtime/refii_renderer/hooks/resource/fable_observer_resources.h"
#include "runtime/refii_renderer/plume/fable/direct/fable_direct_state.h"
#include "runtime/refii_renderer/plume/fable/packets/fable_render_types.h"
#include "runtime/refii_renderer/plume/fable/video/fable_pix_video_geometry.h"
#include "runtime/refii_renderer/plume/fable/video/fable_pix_video_uploads.h"

#include <cstdint>

namespace refii::renderer {

struct FablePixRenderVideoFrameObserverCallbacks {
  void (*note_hook)(uint32_t packet) = nullptr;
  void (*note_bad_packet)() = nullptr;
  void (*note_bad_vertices)() = nullptr;
  void (*note_draw)() = nullptr;
  void (*note_event)() = nullptr;
  void (*record_fetch)(uint32_t base_address,
                       uint32_t byte_size,
                       uint32_t format) = nullptr;
  void (*record_texture)(uint32_t texture) = nullptr;
  void (*record_uv_override)(const FablePixVideoUvScale& uv_scale) = nullptr;
  void (*dump_inline_shaders)(uint8_t* base) = nullptr;
  bool (*is_likely_guest_dword_address)(uint32_t address) = nullptr;
  uint64_t (*next_serial)() = nullptr;
  uint64_t (*current_serial)() = nullptr;
  void (*publish_inline_texture)(
      const RendererInlineTextureUploadSnapshot& upload) = nullptr;
  void (*store_texture_fetch)(uint32_t texture,
                              uint32_t fetch_address,
                              uint32_t slot,
                              uint8_t* base,
                              uint32_t lr) = nullptr;
  void (*refresh_bound_surfaces)(uint8_t* base, uint32_t device) = nullptr;
  void (*upsert_scratch_buffer)(RendererResourceKind kind,
                                uint32_t address,
                                const void* data,
                                uint32_t byte_size,
                                uint32_t stride,
                                uint32_t device) = nullptr;
  void (*apply_scratch_buffers)(RendererDrawEventSnapshot& event,
                                uint32_t vertex_buffer,
                                uint32_t vertex_stride,
                                uint32_t index_buffer) = nullptr;
  void (*upsert_shader)(RendererResourceKind kind,
                        uint32_t address,
                        uint64_t hash,
                        uint32_t device) = nullptr;
  RendererDrawEventSnapshot (*build_draw_event)(
      uint32_t device,
      const RendererLastDrawSnapshot& last_draw) = nullptr;
  RendererDrawEventSnapshot (*record_draw_event)(
      RendererDrawEventSnapshot event) = nullptr;
  void (*capture_submitted_render_commands)(uint64_t serial) = nullptr;
  void (*trace_draw)(const RendererLastDrawSnapshot& last_draw) = nullptr;
  void (*trim_scratch_buffers)() = nullptr;
};

void FablePixRenderVideoFrameQueueDrawEvent(
    const RendererHookArgs& args,
    uint8_t* base,
    const FablePixRenderVideoFrameObserverCallbacks& callbacks);

}  // namespace refii::renderer
