#pragma once

#include "runtime/refii_renderer/plume/fable/resource/fable_resource_snapshots.h"
#include "runtime/plume/resource/plume_resource_manager_internal.h"

#include <array>
#include <cstddef>
#include <cstdint>

namespace refii::renderer {

constexpr uint32_t kFableBinkVideoPlaneCount = 3u;

struct FableVideoBinkWrapperPlaneSetReadinessFacts {
  bool wrapper_candidate = false;
  bool command_plane_set_ready = false;
  bool plane_address_set_ready = false;
  std::array<uint32_t, kFableBinkVideoPlaneCount> guests = {};
  std::array<bool, kFableBinkVideoPlaneCount> fetch_decoded = {};
  std::array<bool, kFableBinkVideoPlaneCount> resource_image_ready = {};
};

struct FableVideoBinkWrapperPlaneResourceInterestFacts {
  bool wrapper_candidate = false;
  uint32_t guest = 0;
  bool fetch_valid = false;
  uint32_t fetch_base_address = 0;
};

struct FableVideoBinkWrapperPlaneResourceInterests {
  bool include_texture_slot_identity = false;
  std::array<uint32_t, kFableBinkVideoPlaneCount> resource_addresses = {};
  std::array<uint32_t, kFableBinkVideoPlaneCount> texture_upload_addresses = {};
  uint32_t resource_count = 0;
  uint32_t texture_upload_count = 0;
};

bool FableVideoBinkPlaneHasDecodedUpload(const PlumeTrackedResource& resource);

bool FableVideoBinkPlaneChromaShapeMatchesLuma(
    const PlumeTrackedResource& y,
    const PlumeTrackedResource& cr,
    const PlumeTrackedResource& cb);

bool FableVideoBinkPlaneResourcesReadyDecoded(
    const PlumeTrackedResource& y,
    const PlumeTrackedResource& cr,
    const PlumeTrackedResource& cb);

bool FableVideoBinkWrapperPlaneSetReady(
    const FableVideoBinkWrapperPlaneSetReadinessFacts& facts,
    uint32_t* missing_slot,
    uint32_t* missing_guest);

FableVideoBinkWrapperPlaneResourceInterests
FableVideoCollectBinkWrapperPlaneResourceInterests(
    const FableVideoBinkWrapperPlaneResourceInterestFacts& facts);

bool FableVideoBinkPlanePayloadSnapshot(
    const RendererTrackedResourceSnapshot& snapshot,
    size_t upload_size);

}  // namespace refii::renderer
