#pragma once

#include "runtime/plume/resource/plume_resource_manager_internal.h"
#include "runtime/refii_renderer/plume/fable/direct/fable_direct_state.h"

#include <cstddef>
#include <cstdint>

namespace refii::renderer {

bool DirectInlineBufferCoversAddress(const FableDirectInlineBuffer& buffer,
                                     uint32_t address);

uint32_t DirectInlineBufferOffsetForAddress(
    const FableDirectInlineBuffer& buffer, uint32_t address);


bool DirectAddressLooksLikeScratchBuffer(uint32_t address);

bool DirectBytesHaveNonZero(const uint8_t* data, size_t size);

bool DirectTrackedScratchBufferUploadLooksReady(
    const PlumeTrackedResource& buffer);

bool DirectShouldPreferInlineScratchVertexBuffer(
    const FableDirectInlineBuffer& inline_buffer,
    const RendererStreamBindingSnapshot& stream);

}  // namespace refii::renderer
