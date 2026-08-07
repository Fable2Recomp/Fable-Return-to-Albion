#include "runtime/refii_renderer/plume/fable/video/fable_pix_render_video_frame_observer.h"

#include "runtime/refii_renderer/plume/fable/private_rect/fable_direct_synthetic_private_rect.h"
#include "runtime/refii_renderer/plume/fable/private_rect/fable_private_rect_geometry.h"
#include "runtime/refii_renderer/plume/fable/resource/fable_texture_fetch.h"
#include "runtime/refii_renderer/plume/fable/video/fable_pix_video_draw_event.h"
#include "runtime/refii_renderer/plume/fable/video/fable_pix_video_packets.h"
#include "runtime/refii_renderer/plume/fable/video/fable_pix_video_state.h"
#include "runtime/refii_renderer/plume/fable/video/fable_video_constants.h"
#include "runtime/shader_library.h"

#include <array>
#include <cstdint>
#include <utility>

namespace refii::renderer {
namespace {

constexpr uint32_t kPixRenderVideoFrameTextureSlot = 0;

bool FablePixRenderVideoFrameCallbacksReady(
    const FablePixRenderVideoFrameObserverCallbacks& callbacks) {
  return callbacks.note_hook && callbacks.note_bad_packet &&
         callbacks.note_bad_vertices && callbacks.note_draw &&
         callbacks.note_event && callbacks.record_fetch &&
         callbacks.record_texture && callbacks.record_uv_override &&
         callbacks.dump_inline_shaders &&
         callbacks.is_likely_guest_dword_address && callbacks.next_serial &&
         callbacks.current_serial && callbacks.publish_inline_texture &&
         callbacks.store_texture_fetch && callbacks.refresh_bound_surfaces &&
         callbacks.upsert_scratch_buffer && callbacks.apply_scratch_buffers &&
         callbacks.upsert_shader && callbacks.build_draw_event &&
         callbacks.record_draw_event &&
         callbacks.capture_submitted_render_commands &&
         callbacks.trace_draw && callbacks.trim_scratch_buffers;
}

}  // namespace

void FablePixRenderVideoFrameQueueDrawEvent(
    const RendererHookArgs& args,
    uint8_t* base,
    const FablePixRenderVideoFrameObserverCallbacks& callbacks) {
  if (!FablePixRenderVideoFrameCallbacksReady(callbacks)) {
    return;
  }

  callbacks.note_hook(args.r8);
  callbacks.dump_inline_shaders(base);

  if (!base || args.r3 == 0 ||
      !callbacks.is_likely_guest_dword_address(args.r8 + 28u)) {
    callbacks.note_bad_packet();
    return;
  }

  const FablePixRenderVideoFrameTexturePacket pix_packet =
      FablePixVideoReadRenderTexturePacket(base, args.r8);
  callbacks.record_fetch(pix_packet.fetch.base_address,
                         pix_packet.fetch.byte_size,
                         pix_packet.fetch.format);
  if (!pix_packet.valid) {
    callbacks.note_bad_packet();
    return;
  }

  const uint32_t pix_texture_handle = pix_packet.texture_handle;
  callbacks.record_texture(pix_texture_handle);
  const uint64_t pix_upload_serial =
      FablePixVideoInlineUploadSourceReadable(base,
                                              pix_packet.fetch.base_address,
                                              pix_packet.fetch.byte_size)
          ? callbacks.next_serial()
          : 0;
  const RendererInlineTextureUploadSnapshot pix_inline_upload =
      FablePixVideoBuildInlineUploadSnapshot(
          pix_texture_handle, pix_packet.fetch_dwords,
          pix_packet.fetch.base_address, pix_packet.fetch.byte_size, base,
          pix_upload_serial);
  callbacks.publish_inline_texture(pix_inline_upload);

  std::array<float, 6> synthetic_vertices = {};
  if (!FablePixVideoReadFrameVerticesFromArgs(base, args.r9, args.r10,
                                              synthetic_vertices)) {
    callbacks.note_bad_vertices();
    return;
  }

  const uint32_t vertex_buffer =
      FablePixVideoSyntheticVertexBufferAddress(args.r8, args.lr ^ args.r3);

  const FableDirectStateSnapshot direct_snapshot =
      GetFableDirectStateSnapshot();
  const FablePixVideoReplacementStateRestore pix_restore =
      FablePixVideoCaptureReplacementState(direct_snapshot);
  FableDirectSetTexture(args.r3, kPixRenderVideoFrameTextureSlot,
                        pix_texture_handle);
  callbacks.store_texture_fetch(pix_texture_handle, args.r8 + 28u,
                                kPixRenderVideoFrameTextureSlot, base, args.lr);

  callbacks.refresh_bound_surfaces(base, args.r3);
  const RendererLastDrawSnapshot last_draw = {
      .indexed = false,
      .primitive_type = 4,
      .base_vertex = 0,
      .start = 0,
      .primitive_count = 3,
  };
  callbacks.note_draw();

  const auto direct_vertices =
      BuildPrivateTextureRectangleSingleTriangleDirectVertices(
          synthetic_vertices);
  callbacks.upsert_scratch_buffer(
      RendererResourceKind::kVertexBuffer, vertex_buffer,
      direct_vertices.data(),
      static_cast<uint32_t>(sizeof(FablePrivateRectDirectVertex) *
                            direct_vertices.size()),
      static_cast<uint32_t>(sizeof(FablePrivateRectDirectVertex)), args.r3);
  const RendererStreamBindingSnapshot stream0 = {
      .buffer = vertex_buffer,
      .offset = 0,
      .stride = static_cast<uint32_t>(sizeof(FablePrivateRectDirectVertex)),
  };
  FableDirectSetStream(args.r3, 0, stream0);

  RendererDrawEventSnapshot event = callbacks.build_draw_event(args.r3,
                                                               last_draw);
  FablePixVideoBindInlineUploadPlanes(event, pix_texture_handle,
                                      pix_packet.fetch_dwords,
                                      pix_inline_upload);
  callbacks.apply_scratch_buffers(event, vertex_buffer, stream0.stride, 0);
  const FablePixVideoUvScale uv_scale =
      FablePixVideoReadUvScaleFromStack(base, args.r1, synthetic_vertices);
  FablePixVideoConfigureDrawEventShape(event, synthetic_vertices, uv_scale);
  FableVideoConfigureGuiQuadVertexConstants(event);
  callbacks.record_uv_override(uv_scale);
  FableVideoConfigureSyntheticPrivateTexturePipelineState(event);
  callbacks.upsert_shader(RendererResourceKind::kVertexShader,
                          kFableSyntheticPrivateRectVertexShader,
                          kFableGuiQuadVertexShaderHash, args.r3);
  callbacks.upsert_shader(RendererResourceKind::kPixelShader,
                          kFableSyntheticPrivateRectPixelShader,
                          kFablePixRenderVideoFramePixelShaderHash, args.r3);
  event.vertex_shader = kFableSyntheticPrivateRectVertexShader;
  event.pixel_shader = kFableSyntheticPrivateRectPixelShader;
  event.resource_serial = callbacks.current_serial();
  callbacks.note_event();

  const RendererDrawEventSnapshot recorded_event =
      callbacks.record_draw_event(std::move(event));
  callbacks.capture_submitted_render_commands(recorded_event.serial);

  FablePixVideoRestoreReplacementState(args.r3, pix_restore);
  callbacks.trace_draw(last_draw);
  callbacks.trim_scratch_buffers();
}

}  // namespace refii::renderer
