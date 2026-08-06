#pragma once

#include <cstdint>

struct PPCContext;

namespace refii::renderer {

struct RendererHookArgs;

namespace bink {

bool TryRunBinkNativeAudioDrain(const RendererHookArgs &args, PPCContext &ctx,
                                uint8_t *base);

} // namespace bink
} // namespace refii::renderer
