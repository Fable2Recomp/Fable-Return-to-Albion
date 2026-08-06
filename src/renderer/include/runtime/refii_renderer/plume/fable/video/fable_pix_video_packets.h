#pragma once

#include "runtime/refii_renderer/plume/fable/resource/fable_texture_fetch.h"

#include <array>
#include <cstdint>

namespace refii::renderer {

struct FablePixDecodedVideoFrameInfo {
  uint32_t width = 0;
  uint32_t height = 0;
  uint32_t frame_base = 0;
};

struct FablePixDecodePacketShape {
  bool chunks_readable = false;
  bool chunk_readable = false;
  uint32_t first_chunk_pointer = 0;
  uint32_t chunk = 0;
  uint32_t q0_hi = 0;
  uint32_t q0_lo = 0;
  uint32_t flags = 0;
  uint32_t width = 0;
  uint32_t height = 0;
  uint32_t scale_u = 0;
  uint32_t scale_v = 0;
  uint32_t alpha = 0;
  std::array<uint32_t, 8> sample = {};
};

struct FablePixRenderVideoFrameTexturePacket {
  bool valid = false;
  std::array<uint32_t, 6> fetch_dwords = {};
  FableTextureFetchInfo fetch = {};
  uint32_t texture_handle = 0;
};

bool FablePixVideoReadDecodedFrameInfo(
    uint8_t* base,
    uint32_t metadata_pointer,
    FablePixDecodedVideoFrameInfo& frame);

FablePixDecodePacketShape FablePixVideoReadDecodePacketShape(
    uint8_t* base,
    uint32_t chunks_pointer,
    uint32_t chunk_offset);

FablePixRenderVideoFrameTexturePacket FablePixVideoReadRenderTexturePacket(
    uint8_t* base,
    uint32_t packet_pointer);

}  // namespace refii::renderer
