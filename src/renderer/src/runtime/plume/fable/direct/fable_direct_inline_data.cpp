#include "runtime/refii_renderer/plume/fable/direct/fable_direct_inline_data.h"

#include <algorithm>
#include <limits>

namespace refii::renderer {

bool DirectInlineBufferCoversAddress(const FableDirectInlineBuffer& buffer,
                                     uint32_t address) {
  if (!buffer.ready() || address == 0 || buffer.address == 0) {
    return false;
  }
  const uint64_t begin = buffer.address;
  const uint64_t end = begin + buffer.byte_size;
  return address >= begin && uint64_t(address) < end;
}

uint32_t DirectInlineBufferOffsetForAddress(
    const FableDirectInlineBuffer& buffer, uint32_t address) {
  if (!DirectInlineBufferCoversAddress(buffer, address)) {
    return UINT32_MAX;
  }
  const uint64_t offset = uint64_t(address) - uint64_t(buffer.address);
  if (offset > std::numeric_limits<uint32_t>::max()) {
    return UINT32_MAX;
  }
  return static_cast<uint32_t>(offset);
}


bool DirectAddressLooksLikeScratchBuffer(uint32_t address) {
  if (address == 0) {
    return true;
  }
  if ((address & (alignof(uint32_t) - 1u)) != 0) {
    return false;
  }
  return (address >= 0x1000u && address < 0x40000000u) ||
         address >= 0x80000000u;
}

bool DirectBytesHaveNonZero(const uint8_t* data, size_t size) {
  return data && std::any_of(data, data + size, [](uint8_t value) {
    return value != 0;
  });
}

bool DirectTrackedScratchBufferUploadLooksReady(
    const PlumeTrackedResource& buffer) {
  if (!buffer.scratch) {
    return true;
  }
  if (buffer.upload_generation == 0 || buffer.debug_upload_sample.empty()) {
    return false;
  }
  return buffer.debug_first_nonzero_offset != UINT32_MAX ||
         DirectBytesHaveNonZero(buffer.debug_upload_sample.data(),
                                buffer.debug_upload_sample.size());
}

bool DirectShouldPreferInlineScratchVertexBuffer(
    const FableDirectInlineBuffer& inline_buffer,
    const RendererStreamBindingSnapshot& stream) {
  // PROVENANCE, NOT ADDRESS RANGE.
  //
  // This used to also require DirectAddressLooksLikeScratchBuffer(stream.buffer),
  // i.e. `addr < 0x40000000 || addr >= 0x80000000`. That test is unsound in
  // principle and wrong in fact for Fable II:
  //
  //  * Unsound: the guest's vertex ring and its textures are allocated by the
  //    SAME routine (sub_8222C9D0's physical arm), out of the same address
  //    space. No numeric range can separate ring memory from resource memory.
  //  * Wrong in fact: Fable's ring hands out addresses like 0x4222051C /
  //    0x4260851C, which live in 0x40000000..0x7FFFFFFF -- precisely the band
  //    the predicate excluded. So for the draws whose geometry GTL had already
  //    pinned, this returned false and the bind fell back to an address-keyed
  //    lookup of a ring CURSOR, which missed ~95% of the time and scanned the
  //    whole resource map to discover it.
  //
  // The sound discriminator is provenance: the bytes were captured at a
  // BeginRingAlloc-family root, so if the stream's address resolves to an offset
  // INSIDE that pinned payload, the payload is by construction the data this
  // draw reads. DirectInlineBufferOffsetForAddress already proves exactly that,
  // and it does so without consulting any address whitelist.
  return inline_buffer.ready() &&
         DirectInlineBufferOffsetForAddress(inline_buffer, stream.buffer) !=
             UINT32_MAX;
}

}  // namespace refii::renderer
