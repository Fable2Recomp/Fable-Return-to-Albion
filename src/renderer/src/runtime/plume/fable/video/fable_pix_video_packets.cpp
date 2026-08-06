#include "runtime/refii_renderer/plume/fable/video/fable_pix_video_packets.h"

#include "runtime/refii_renderer/bik/bink_guest_memory.h"

namespace refii::renderer {

bool FablePixVideoReadDecodedFrameInfo(
    uint8_t* base,
    uint32_t metadata_pointer,
    FablePixDecodedVideoFrameInfo& frame) {
  if (!bink::IsReadableGuestByteRange(base, metadata_pointer,
                                      sizeof(uint32_t) * 5u)) {
    return false;
  }

  const uint32_t frame_width = bink::LoadGuestU32(base, metadata_pointer + 8u);
  const uint32_t frame_height = bink::LoadGuestU32(base, metadata_pointer + 12u);
  const uint32_t frame_base = bink::LoadGuestU32(base, metadata_pointer + 16u);
  if (frame_width == 0 || frame_height == 0 || frame_width > 4096u ||
      frame_height > 4096u || frame_base == 0) {
    return false;
  }

  frame.width = frame_width;
  frame.height = frame_height;
  frame.frame_base = frame_base;
  return true;
}

FablePixDecodePacketShape FablePixVideoReadDecodePacketShape(
    uint8_t* base,
    uint32_t chunks_pointer,
    uint32_t chunk_offset) {
  FablePixDecodePacketShape shape;
  shape.chunks_readable = bink::IsReadableGuestByteRange(
      base, chunks_pointer, sizeof(uint32_t));
  if (!shape.chunks_readable) {
    return shape;
  }

  shape.first_chunk_pointer = bink::LoadGuestU32(base, chunks_pointer);
  const uint32_t chunk_virtual = shape.first_chunk_pointer + chunk_offset;
  shape.chunk = ((((chunk_virtual >> 20u) + 512u) & 0x1000u) +
                 (chunk_virtual & 0x1FFFFFFFu) - 0x40000000u);
  constexpr uint32_t kDecodePacketShapeReadBytes = 68u;
  shape.chunk_readable = bink::IsReadableGuestByteRange(
      base, shape.chunk, kDecodePacketShapeReadBytes);
  if (!shape.chunk_readable) {
    return shape;
  }

  shape.q0_hi = bink::LoadGuestU32(base, shape.chunk);
  shape.q0_lo = bink::LoadGuestU32(base, shape.chunk + 4u);
  shape.flags = bink::LoadGuestU32(base, shape.chunk + 12u);
  shape.width = bink::LoadGuestU32(base, shape.chunk + 16u);
  shape.height = bink::LoadGuestU32(base, shape.chunk + 20u);
  shape.scale_u = bink::LoadGuestU32(base, shape.chunk + 24u);
  shape.scale_v = bink::LoadGuestU32(base, shape.chunk + 28u);
  shape.alpha = bink::LoadGuestU32(base, shape.chunk + 32u);
  for (uint32_t i = 0; i < shape.sample.size(); ++i) {
    shape.sample[i] =
        bink::LoadGuestU32(base, shape.chunk + 36u + i * sizeof(uint32_t));
  }
  return shape;
}

FablePixRenderVideoFrameTexturePacket FablePixVideoReadRenderTexturePacket(
    uint8_t* base,
    uint32_t packet_pointer) {
  FablePixRenderVideoFrameTexturePacket packet;
  constexpr uint32_t kTextureFetchOffset = 28u;
  if (!bink::IsReadableGuestByteRange(base, packet_pointer + kTextureFetchOffset,
                                      sizeof(uint32_t) *
                                          packet.fetch_dwords.size())) {
    return packet;
  }

  for (uint32_t i = 0; i < packet.fetch_dwords.size(); ++i) {
    packet.fetch_dwords[i] = bink::LoadGuestU32(
        base, packet_pointer + kTextureFetchOffset + i * sizeof(uint32_t));
  }
  packet.fetch = FableTextureFetchInfoFromDwords(packet.fetch_dwords);
  packet.texture_handle = packet.fetch.base_address;
  packet.valid = packet.fetch.valid && packet.texture_handle != 0;
  return packet;
}

}  // namespace refii::renderer
