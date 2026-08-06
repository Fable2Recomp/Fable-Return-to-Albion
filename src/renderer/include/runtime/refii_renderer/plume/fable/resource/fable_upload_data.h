#pragma once

#include "runtime/refii_renderer/plume/fable/resource/fable_guest_resource_registry.h"

#include <cstddef>
#include <cstdint>

namespace refii::renderer {

uint64_t HashGuestBytesFnv1a64(uint8_t *base, uint32_t guest_address,
                               uint32_t size);
uint64_t HashBytesFnv1a64(const uint8_t *data, size_t size);
uint64_t HashBytesFast64(const uint8_t *data, size_t size);
uint64_t HashZeroBytesFnv1a64(size_t size);
uint64_t HashZeroBytesFast64(size_t size);

bool HasNonZeroUploadData(const RendererResourceRecord &record);
bool HasNonZeroUploadPrefixData(const RendererResourceRecord &record,
                                uint32_t max_scan);
bool IsAssetChunkHeaderLike(const uint8_t *data, size_t size);

}  // namespace refii::renderer
