#pragma once

#include "runtime/refii_renderer/plume/fable/direct/fable_direct_framebuffer_binding.h"
#include "runtime/refii_renderer/plume/fable/direct/fable_direct_state.h"

#include <cstdint>

namespace refii::renderer {

uint64_t HashDirectInlineBufferPrefix(const FableDirectInlineBuffer& buffer);

uint32_t ReadDirectInlineDword(const FableDirectInlineBuffer& buffer,
                               uint32_t dword_index);

float DwordAsFloat(uint32_t value);

float ReadDirectInlineFloat(const FableDirectInlineBuffer& buffer,
                            uint32_t byte_offset);

uint32_t ReadDirectInlineIndex(const FableDirectInlineBuffer& buffer,
                               uint32_t index);

uint32_t DirectPixelConstantDword(const FableDirectRenderCommand& command,
                                  uint32_t constant_index,
                                  uint32_t component);

uint32_t DirectVertexConstantDword(const FableDirectRenderCommand& command,
                                   uint32_t constant_index,
                                   uint32_t component);

bool DirectPixelConstantQuadNonZero(const FableDirectRenderCommand& command,
                                    uint32_t constant_index);

bool DirectVertexConstantQuadNonZero(const FableDirectRenderCommand& command,
                                     uint32_t constant_index);

RendererDrawEventSnapshot BuildDrawEventFromCommandForShaderState(
    const FableDirectRenderCommand& command);


}  // namespace refii::renderer
