#include "runtime/refii_renderer/plume/fable/private_rect/fable_private_rect_inline_clear.h"

#include "runtime/refii_renderer/plume/fable/private_rect/fable_private_rect_geometry.h"

#include <rex/logging.h>

#include <algorithm>
#include <cmath>
#include <utility>

namespace refii::renderer {
namespace {

bool FablePrivateRectInlineClearCallbacksReady(
    const FablePrivateRectInlineClearCallbacks& callbacks) {
  return callbacks.load_guest_f32 && callbacks.stamp_draw_event_metadata &&
         callbacks.record_draw_event && callbacks.reserve_log_index;
}

}  // namespace

bool FablePrivateTextureRectangleQueueInlineClear(
    const RendererHookArgs& args,
    uint8_t* base,
    const RendererDrawEventSnapshot& draw_event,
    bool use_bink_movie_shader,
    const TextureFetchSnapshotInfo& source_fetch_info,
    const FablePrivateRectInlineClearCallbacks& callbacks) {
  if (!FablePrivateRectInlineClearCallbacksReady(callbacks) ||
      use_bink_movie_shader || source_fetch_info.base_address != 0 ||
      (args.r4 & 0x300u) == 0) {
    return false;
  }

  uint32_t clear_flags = 0;
  const uint32_t color_slot = args.r4 & 7u;
  if ((args.r4 & 0x100u) != 0 && color_slot < 4u) {
    clear_flags |= 1u << color_slot;
  }
  if ((args.r4 & 0x200u) != 0 && draw_event.depth_stencil != 0) {
    clear_flags |= 0x10u;  // kFableClearDepth
  }
  if (clear_flags == 0) {
    return false;
  }

  RendererDrawEventSnapshot clear_event = draw_event;
  clear_event.source = RendererDrawEventSource::kClear;
  clear_event.clear = true;
  clear_event.clear_flags = clear_flags;
  clear_event.clear_depth =
      std::isfinite(args.f1) ? static_cast<float>(args.f1) : 1.0f;
  clear_event.clear_stencil = 0;
  clear_event.clear_rect =
      FablePrivateTextureRectangleClearRectFromCorners(draw_event);
  if (draw_event.private_texture_wrapper_call_serial != 0 &&
      draw_event.private_texture_wrapper_context_key != 0 &&
      draw_event.private_texture_wrapper_symbol_address != 0) {
    clear_event.clear_root_call_serial =
        draw_event.private_texture_wrapper_call_serial;
    clear_event.clear_root_context_key =
        draw_event.private_texture_wrapper_context_key;
    clear_event.clear_root_symbol_address =
        draw_event.private_texture_wrapper_symbol_address;
    clear_event.clear_root_lr = args.lr;
  }
  clear_event.draw = {};
  clear_event.draw_vertices_up = false;
  clear_event.private_texture_rectangle = false;
  clear_event.vertex_declaration = 0;
  clear_event.vertex_shader = 0;
  clear_event.pixel_shader = 0;
  clear_event.index_buffer = 0;
  std::fill(std::begin(clear_event.textures), std::end(clear_event.textures),
            0);

  const uint32_t color_pointer = args.r10;
  if (color_pointer != 0 && base != nullptr) {
    for (uint32_t i = 0; i < 4u; ++i) {
      clear_event.clear_color[i] =
          callbacks.load_guest_f32(base, color_pointer + i * sizeof(float));
    }
  }

  callbacks.stamp_draw_event_metadata(clear_event);

  const uint32_t log_index = callbacks.reserve_log_index();
  if (log_index != 0) {
    REXLOG_INFO(
        "Refii renderer private texture rect inline clear #{} "
        "lr=0x{:08X} flags=0x{:08X}->0x{:08X} color_ptr=0x{:08X} "
        "color={:.4f},{:.4f},{:.4f},{:.4f} "
        "rt=0x{:08X} depth=0x{:08X} root=0x{:08X}/{} "
        "context=0x{:016X} rect={} {}/{}/{}/{}",
        log_index, args.lr, args.r4, clear_flags, color_pointer,
        clear_event.clear_color[0], clear_event.clear_color[1],
        clear_event.clear_color[2], clear_event.clear_color[3],
        draw_event.render_targets[0], draw_event.depth_stencil,
        clear_event.clear_root_symbol_address,
        static_cast<unsigned long long>(clear_event.clear_root_call_serial),
        static_cast<unsigned long long>(clear_event.clear_root_context_key),
        clear_event.clear_rect.valid ? 1 : 0, clear_event.clear_rect.left,
        clear_event.clear_rect.top, clear_event.clear_rect.right,
        clear_event.clear_rect.bottom);
  }

  callbacks.record_draw_event(std::move(clear_event));
  return true;
}

}  // namespace refii::renderer
