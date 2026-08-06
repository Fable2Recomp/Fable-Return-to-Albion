#pragma once

#include "runtime/refii_renderer/plume/fable/direct/fable_direct_renderer.h"

#include <cstdint>

namespace refii::renderer {

struct FableSurfaceDestinationTextureRelation;
struct PlumeTrackedResource;

inline constexpr uint32_t kFableDirectPresentOutputResource = 0xF1FFFF00u;

struct FableDirectPresentBlitCommandPacketInput {
  uint32_t source_target = 0;
  uint32_t source_width = 0;
  uint32_t source_height = 0;
  uint32_t texture_width = 0;
  uint32_t texture_height = 0;
  uint64_t source_write_serial = 0;
  uint32_t source_frame = 0;
  const char* reason = nullptr;
};

FableDirectFrameDatabaseCommandPacket
BuildFableDirectPresentBlitFrameDatabaseCommandPacket(
    const FableDirectPresentBlitCommandPacketInput& source,
    uint32_t output_width,
    uint32_t output_height);

FableDirectFrameDatabaseCommandPacket
BuildFableDirectSurfaceDestinationFrameDatabaseCommandPacket(
    const FableSurfaceDestinationTextureRelation& relation,
    const PlumeTrackedResource& source,
    const PlumeTrackedResource& target,
    bool copy_used_scaled_path);

}  // namespace refii::renderer
