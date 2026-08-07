#include "runtime/refii_renderer/plume/fable/video/fable_video_constants.h"

namespace refii::renderer {

static_assert(kFableBinkIndexedVerticesWrapperBuilderSymbol != 0);
static_assert(kFablePixRenderVideoFramePixelShaderHash != 0);
static_assert(kFableBinkIndexedWrapperPixelShaderHash != 0);
static_assert(kFablePixRenderVideoFrameInlineShaderCount == 2);
static_assert(kFablePixRenderVideoFrameInlineShaders[0].guest_address ==
              0x82095810u);
static_assert(kFablePixRenderVideoFrameInlineShaders[0].byte_size == 1236u);
static_assert(kFablePixRenderVideoFrameInlineShaders[1].guest_address ==
              0x82093C7Cu);
static_assert(kFablePixRenderVideoFrameInlineShaders[1].byte_size == 36u);

}  // namespace refii::renderer
