#pragma once

#include "runtime/refii_renderer/bik/bink_video_uploads.h"
#include "runtime/refii_renderer/hooks/resource/fable_observer_resources.h"
#include "runtime/refii_renderer/plume/fable/resource/fable_resource_snapshots.h"

#include <array>
#include <cstddef>
#include <cstdint>
#include <string_view>
#include <unordered_map>

namespace refii::renderer {

struct RendererHookArgs;
struct RendererBinkVideoPlaneSnapshot;

inline constexpr size_t kFableMaxPendingPixDecodedVideoFrames = 32;

struct FablePixCaptureHookSnapshot {
  bool used_device_memory = false;
  uint32_t frame = 0;
  uint32_t format = 0;
  uint32_t width = 0;
  uint32_t height = 0;
  uint32_t scaler = 0;
  uint32_t gamma = 0;
  uint32_t control = 0;
  uint32_t display_flags = 0;
  uint32_t r5 = 0;
  bool packet_branch_candidate = false;
  bool suppressed_missing_device_memory = false;
  bool suppressed_control_disabled = false;
  bool suppressed_nonzero_r5 = false;
  bool suppressed_alternate_display_path = false;
  std::array<uint32_t, 3> stack_args = {};
};

FablePixCaptureHookSnapshot FableBuildPixCaptureHookSnapshot(
    uint8_t* base,
    const RendererHookArgs& args,
    bool has_device_memory,
    const std::array<uint32_t, 3>& stack_args);

void FablePrunePendingPixDecodedVideoFrames(
    std::unordered_map<uint32_t, PendingPixDecodedVideoFrame>& frames,
    size_t max_frames = kFableMaxPendingPixDecodedVideoFrames);

void FableRecordPixDecodedVideoFrame(
    std::unordered_map<uint32_t, PendingPixDecodedVideoFrame>& frames,
    uint64_t& serial,
    uint64_t& decode_frames,
    uint32_t& last_decode_base,
    uint32_t& last_decode_width,
    uint32_t& last_decode_height,
    uint32_t frame_base,
    uint32_t width,
    uint32_t height);


bool FableAnyBinkBindingHasDecodedFrameTable(
    const std::unordered_map<uint32_t, BinkVideoPlaneBinding>& bindings);

bool FableBinkDecodedCurrentTargetPlanesComplete(
    const std::unordered_map<uint32_t, BinkVideoPlaneBinding>& bindings,
    uint32_t decoded_frame_table);

bool FableBinkBindingTargetsDecodedCurrentFrame(
    const BinkVideoPlaneBinding& binding,
    uint32_t decoded_frame_table);

struct FableBinkVideoPlaneRefreshPlan {
  bool exact_decoded_frame_refresh = false;
  bool skip_refresh = false;
  bool exact_current_targets_complete = false;
  uint32_t decoded_frame_table = 0;
};

FableBinkVideoPlaneRefreshPlan FableBuildBinkVideoPlaneRefreshPlan(
    const std::unordered_map<uint32_t, BinkVideoPlaneBinding>& bindings,
    std::string_view source_symbol,
    uint32_t decoded_frame_table);

bool FableBinkVideoPlaneRefreshPlanAllowsBinding(
    const FableBinkVideoPlaneRefreshPlan& plan,
    const BinkVideoPlaneBinding& binding);

// True when the movie object's surface slot (plane_output_slot) still holds
// this binding's surface — the game's own routing truth. False = the
// binding belongs to a finished movie generation (the attract loop reuses
// the movie object over new plane surfaces) and must not associate with or
// consume decode publishes.
bool FableBinkBindingIsLiveGeneration(uint8_t* base,
                                      const BinkVideoPlaneBinding& binding);


bool FableBinkVideoBindingsContainSnapshot(
    const std::unordered_map<uint32_t, BinkVideoPlaneBinding>& bindings,
    const RendererTrackedResourceSnapshot& snapshot);

struct FableBinkDecodedFramePlaneUpdateResult {
  bool table_read = false;
  bool table_ready = false;
  bool updated_any = false;
  uint32_t decoded_table_matches = 0;
  uint32_t plane_matches = 0;
  std::array<bool, 3> matched_planes = {};
  bink::BinkDecodedFrameTable decoded_table;
};

void FableLogBinkDecodedFramePlaneUpdate(
    std::string_view source_symbol,
    const FableBinkDecodedFramePlaneUpdateResult& result);

FableBinkDecodedFramePlaneUpdateResult
FableUpdateBinkDecodedFramePlaneSources(
    std::unordered_map<uint32_t, BinkVideoPlaneBinding>& bindings,
    uint64_t& serial,
    uint8_t* base,
    uint32_t frame_table,
    uint32_t max_plane_upload_size,
    const bink::BinkVideoUploadAddressCallbacks& upload_address_callbacks);

FableBinkDecodedFramePlaneUpdateResult
FableUpdateAndLogBinkDecodedFramePlaneSources(
    std::unordered_map<uint32_t, BinkVideoPlaneBinding>& bindings,
    uint64_t& serial,
    uint8_t* base,
    uint32_t frame_table,
    uint32_t max_plane_upload_size,
    const bink::BinkVideoUploadAddressCallbacks& upload_address_callbacks,
    std::string_view source_symbol);

}  // namespace refii::renderer
