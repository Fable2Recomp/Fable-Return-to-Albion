#include "runtime/refii_renderer/plume/fable/private_rect/fable_private_rect_copy_actions.h"

#include "runtime/refii_renderer/plume/fable/private_rect/fable_private_rect_geometry.h"

#include <rex/logging.h>

#include <cmath>

namespace refii::renderer {
namespace {

bool FablePrivateRectCopyActionCallbacksReady(
    const FablePrivateRectCopyActionCallbacks& callbacks) {
  return callbacks.is_likely_fable_object_address &&
         callbacks.current_frame_index && callbacks.publish_source_identity &&
         callbacks.upsert_texture_from_fetch_dwords &&
         callbacks.publish_target_surface && callbacks.attach_wrapper_action &&
         callbacks.register_copy_action &&
         callbacks.publish_exact_surface_alias &&
         callbacks.reserve_transient_log_index;
}

}  // namespace

void FablePrivateTextureRectangleRegisterCopyAction(
    uint8_t* base,
    uint32_t source,
    uint32_t target,
    uint32_t render_target,
    uint32_t depth_stencil,
    uint32_t vertex_shader,
    uint32_t pixel_shader,
    uint32_t lr,
    const TextureFetchSnapshotInfo& source_fetch_info,
    const std::array<uint32_t, 6>& source_fetch,
    FablePrivateTextureCopyKind copy_kind,
    const RendererDrawEventSnapshot& event,
    uint32_t source_slot,
    const FablePrivateRectCopyActionCallbacks& callbacks) {
  if (!FablePrivateRectCopyActionCallbacksReady(callbacks)) {
    return;
  }

  const bool persistent_source =
      callbacks.is_likely_fable_object_address(source);
  const bool transient_header_source =
      !persistent_source && FableTextureFetchHasDwords(source_fetch) &&
      (source_fetch_info.width != 0 || source_fetch_info.height != 0);
  if ((!persistent_source && !transient_header_source) ||
      !callbacks.is_likely_fable_object_address(target)) {
    return;
  }

  if (persistent_source) {
    callbacks.publish_source_identity(base, source, target, source_fetch_info,
                                      source_fetch, source_slot, lr);
  } else {
    callbacks.upsert_texture_from_fetch_dwords(source_fetch, base);
  }
  callbacks.publish_target_surface(
      target, copy_kind == FablePrivateTextureCopyKind::kDepth, event);

  FablePrivateTextureCopyAction action;
  action.source_slot = source_slot;
  action.source_texture = source;
  action.source_base_address = source_fetch_info.base_address;
  action.source_width = source_fetch_info.width;
  action.source_height = source_fetch_info.height;
  action.source_format = source_fetch_info.format;
  action.source_fetch_dwords = source_fetch;
  action.target_surface = target;
  uint32_t target_width = 0;
  uint32_t target_height = 0;
  if (FablePrivateTextureRectangleTargetExtent(event, target_width,
                                              target_height)) {
    action.target_width = target_width;
    action.target_height = target_height;
  }
  action.target_format =
      copy_kind == FablePrivateTextureCopyKind::kDepth
          ? event.rb_depth_info & 0x3Fu
          : event.rb_color_info[0] & 0x3Fu;
  action.sampled_source.kind = FableRegisteredSampleSourceKind::kGpuSurface;
  action.sampled_source.resource = target;
  action.render_target = render_target;
  action.depth_stencil = depth_stencil;
  action.vertex_shader = vertex_shader;
  action.vertex_shader_root_call_serial = event.vertex_shader_root_call_serial;
  action.vertex_shader_root_context_key = event.vertex_shader_root_context_key;
  action.vertex_shader_root_symbol_address =
      event.vertex_shader_root_symbol_address;
  action.vertex_shader_root_lr = event.vertex_shader_root_lr;
  action.pixel_shader = pixel_shader;
  action.pixel_shader_root_call_serial = event.pixel_shader_root_call_serial;
  action.pixel_shader_root_context_key = event.pixel_shader_root_context_key;
  action.pixel_shader_root_symbol_address =
      event.pixel_shader_root_symbol_address;
  action.pixel_shader_root_lr = event.pixel_shader_root_lr;
  action.lr = lr;
  action.draw_serial = event.serial;
  action.frame_index = callbacks.current_frame_index();
  action.kind = copy_kind;
  action.rectangle_valid =
      event.private_texture_rectangle && event.private_texture_corners_valid;
  if (action.rectangle_valid) {
    const auto& corners = event.private_texture_corners;
    action.rect_width = std::fabs(corners[2] - corners[0]);
    action.rect_height = std::fabs(corners[5] - corners[1]);
  }
  action.source_x = event.private_texture_source_x;
  action.source_y = event.private_texture_source_y;
  action.uv_override = event.private_texture_uv_override;
  action.u0 = event.private_texture_u0;
  action.v0 = event.private_texture_v0;
  action.u1 = event.private_texture_u1;
  action.v1 = event.private_texture_v1;
  action.wrapper_call_serial = event.private_texture_wrapper_call_serial;
  action.wrapper_context_key = event.private_texture_wrapper_context_key;
  action.wrapper_symbol_address = event.private_texture_wrapper_symbol_address;
  action.wrapper_source_texture = event.private_texture_wrapper_source_texture;
  action.wrapper_source_exact = event.private_texture_wrapper_source_exact;

  callbacks.attach_wrapper_action(action);
  callbacks.register_copy_action(action);
  callbacks.publish_exact_surface_alias(action, "registered-action");

  if (!persistent_source) {
    const uint32_t log_index = callbacks.reserve_transient_log_index();
    if (log_index != 0) {
      REXLOG_INFO(
          "Refii renderer transient private texture rect action #{} "
          "src_header=0x{:08X} target=0x{:08X} kind={} slot={} lr=0x{:08X} "
          "rt=0x{:08X} depth=0x{:08X} fetch_base=0x{:08X} "
          "fetch_size={} fetch_fmt={} draw_serial={}",
          log_index, source, target, FablePrivateTextureCopyKindName(copy_kind),
          source_slot, lr, render_target, depth_stencil,
          source_fetch_info.base_address, source_fetch_info.size,
          source_fetch_info.format, static_cast<unsigned long long>(event.serial));
    }
  }
}

}  // namespace refii::renderer
