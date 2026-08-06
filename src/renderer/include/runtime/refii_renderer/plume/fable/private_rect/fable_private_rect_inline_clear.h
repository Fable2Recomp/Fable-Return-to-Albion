#pragma once

#include "renderer_runtime.h"
#include "runtime/refii_renderer/plume/fable/packets/fable_render_types.h"
#include "runtime/refii_renderer/plume/fable/resource/fable_texture_fetch.h"

#include <cstdint>

namespace refii::renderer {

struct FablePrivateRectInlineClearCallbacks {
  float (*load_guest_f32)(uint8_t* base, uint32_t guest_address) = nullptr;
  void (*stamp_draw_event_metadata)(RendererDrawEventSnapshot& event) = nullptr;
  RendererDrawEventSnapshot (*record_draw_event)(
      RendererDrawEventSnapshot event) = nullptr;
  uint32_t (*reserve_log_index)() = nullptr;
};

bool FablePrivateTextureRectangleQueueInlineClear(
    const RendererHookArgs& args,
    uint8_t* base,
    const RendererDrawEventSnapshot& draw_event,
    bool use_bink_movie_shader,
    const TextureFetchSnapshotInfo& source_fetch_info,
    const FablePrivateRectInlineClearCallbacks& callbacks);

}  // namespace refii::renderer
