#pragma once

#include "runtime/refii_renderer/hooks/resource/fable_observer_resources.h"

#include <cstdint>
#include <unordered_map>

namespace refii::renderer {

struct RendererHookArgs;

using FablePixDecodedVideoFrameResourceRefresh =
    void (*)(RendererResourceRecord& record, uint8_t* base, void* user);

struct FablePixDecodedVideoFrameCaptureContext {
  std::unordered_map<uint32_t, PendingPixDecodedVideoFrame>& pending_frames;
  std::unordered_map<uint32_t, RendererResourceRecord>& resources;
  uint64_t& serial;
  uint64_t& decode_frames;
  uint32_t& last_decode_base;
  uint32_t& last_decode_width;
  uint32_t& last_decode_height;
  uint32_t& logged_packet_shapes;
  bool log_packet_shapes = false;
  FablePixDecodedVideoFrameResourceRefresh refresh_resource = nullptr;
  void* refresh_user = nullptr;
};

void FableCapturePixDecodedVideoFrameUpload(
    const RendererHookArgs& args,
    uint32_t result,
    uint8_t* base,
    FablePixDecodedVideoFrameCaptureContext& context);

}  // namespace refii::renderer
