#include "runtime/refii_renderer/hooks/video/fable_video_observer_state.h"

#include "renderer_runtime.h"
#include "runtime/refii_renderer/bik/bink_guest_memory.h"

#include <algorithm>
#include <atomic>
#include <iterator>
#include <string_view>
#include <vector>

#include <rex/logging.h>

namespace refii::renderer {
namespace {

constexpr std::string_view kFableBinkDecodedFrameRefreshSymbol =
    "sub_832C49E8";
constexpr uint32_t kPixCaptureFormatOffset = 0x54B0u;
constexpr uint32_t kPixCaptureWidthOffset = 0x54B4u;
constexpr uint32_t kPixCaptureHeightOffset = 0x54B8u;
constexpr uint32_t kPixCaptureFrameOffset = 0x54BCu;
constexpr uint32_t kPixCaptureScalerOffset = 0x54C0u;
constexpr uint32_t kPixCaptureGammaOffset = 0x54C4u;
constexpr uint32_t kPixCaptureControlOffset = 0x5420u;
constexpr uint32_t kPixCaptureDisplayFlagsOffset = 0x2ABDu;
constexpr uint32_t kPixCapturePacketBranchControlBit = 0x40000000u;
constexpr uint32_t kPixCaptureAlternateDisplayPathBit = 0x02u;

}  // namespace

FablePixCaptureHookSnapshot FableBuildPixCaptureHookSnapshot(
    uint8_t* base,
    const RendererHookArgs& args,
    bool has_device_memory,
    const std::array<uint32_t, 3>& stack_args) {
  FablePixCaptureHookSnapshot snapshot;
  snapshot.used_device_memory = has_device_memory;
  snapshot.stack_args = stack_args;
  snapshot.r5 = args.r5;
  if (has_device_memory && base != nullptr) {
    snapshot.frame =
        bink::LoadGuestU32(base, args.r3 + kPixCaptureFrameOffset);
    snapshot.format =
        bink::LoadGuestU32(base, args.r3 + kPixCaptureFormatOffset);
    snapshot.width =
        bink::LoadGuestU32(base, args.r3 + kPixCaptureWidthOffset);
    snapshot.height =
        bink::LoadGuestU32(base, args.r3 + kPixCaptureHeightOffset);
    snapshot.scaler =
        bink::LoadGuestU32(base, args.r3 + kPixCaptureScalerOffset);
    snapshot.gamma =
        bink::LoadGuestU32(base, args.r3 + kPixCaptureGammaOffset);
    if (bink::IsReadableGuestByteRange(base, args.r3 + kPixCaptureControlOffset,
                                       sizeof(uint32_t))) {
      snapshot.control =
          bink::LoadGuestU32(base, args.r3 + kPixCaptureControlOffset);
    }
    if (bink::IsReadableGuestByteRange(
            base, args.r3 + kPixCaptureDisplayFlagsOffset, sizeof(uint8_t))) {
      snapshot.display_flags =
          bink::LoadGuestU8(base, args.r3 + kPixCaptureDisplayFlagsOffset);
    }
  } else {
    snapshot.frame = args.r6;
    snapshot.format = args.r7;
    snapshot.width = args.r8;
    snapshot.height = args.r9;
    snapshot.scaler = args.r10;
    snapshot.gamma = stack_args[0];
  }

  snapshot.suppressed_missing_device_memory = !has_device_memory;
  snapshot.suppressed_control_disabled =
      has_device_memory &&
      (snapshot.control & kPixCapturePacketBranchControlBit) == 0;
  snapshot.suppressed_nonzero_r5 = snapshot.r5 != 0;
  snapshot.suppressed_alternate_display_path =
      (snapshot.display_flags & kPixCaptureAlternateDisplayPathBit) != 0;
  snapshot.packet_branch_candidate =
      has_device_memory && !snapshot.suppressed_control_disabled &&
      !snapshot.suppressed_nonzero_r5 &&
      !snapshot.suppressed_alternate_display_path;
  return snapshot;
}

void FablePrunePendingPixDecodedVideoFrames(
    std::unordered_map<uint32_t, PendingPixDecodedVideoFrame>& frames,
    size_t max_frames) {
  while (frames.size() > max_frames) {
    auto oldest = frames.begin();
    for (auto it = std::next(oldest); it != frames.end(); ++it) {
      if (it->second.serial < oldest->second.serial) {
        oldest = it;
      }
    }
    frames.erase(oldest);
  }
}

void FableRecordPixDecodedVideoFrame(
    std::unordered_map<uint32_t, PendingPixDecodedVideoFrame>& frames,
    uint64_t& serial,
    uint64_t& decode_frames,
    uint32_t& last_decode_base,
    uint32_t& last_decode_width,
    uint32_t& last_decode_height,
    uint32_t frame_base,
    uint32_t width,
    uint32_t height) {
  ++decode_frames;
  last_decode_base = frame_base;
  last_decode_width = width;
  last_decode_height = height;
  frames[frame_base] = {
      .width = width,
      .height = height,
      .frame_base = frame_base,
      .serial = ++serial,
  };
  FablePrunePendingPixDecodedVideoFrames(frames);
}


bool FableAnyBinkBindingHasDecodedFrameTable(
    const std::unordered_map<uint32_t, BinkVideoPlaneBinding>& bindings) {
  return std::any_of(bindings.begin(), bindings.end(),
                     [](const auto& entry) {
                       return entry.second.decoded_frame_table != 0;
                     });
}

bool FableBinkDecodedCurrentTargetPlanesComplete(
    const std::unordered_map<uint32_t, BinkVideoPlaneBinding>& bindings,
    uint32_t decoded_frame_table) {
  if (decoded_frame_table == 0) {
    return false;
  }

  std::array<bool, 3> planes = {};
  for (const auto& [surface, binding] : bindings) {
    (void)surface;
    if (binding.decoded_frame_table == decoded_frame_table &&
        binding.decoded_current_frame_target &&
        binding.decoded_plane_index < planes.size()) {
      planes[binding.decoded_plane_index] = true;
    }
  }
  return planes[0] && planes[1] && planes[2];
}

bool FableBinkBindingTargetsDecodedCurrentFrame(
    const BinkVideoPlaneBinding& binding,
    uint32_t decoded_frame_table) {
  return decoded_frame_table != 0 &&
         binding.decoded_frame_table == decoded_frame_table &&
         binding.decoded_current_frame_target;
}

FableBinkVideoPlaneRefreshPlan FableBuildBinkVideoPlaneRefreshPlan(
    const std::unordered_map<uint32_t, BinkVideoPlaneBinding>& bindings,
    std::string_view source_symbol,
    uint32_t decoded_frame_table) {
  FableBinkVideoPlaneRefreshPlan plan;
  plan.exact_decoded_frame_refresh =
      source_symbol == kFableBinkDecodedFrameRefreshSymbol &&
      decoded_frame_table != 0;
  plan.decoded_frame_table =
      plan.exact_decoded_frame_refresh ? decoded_frame_table : 0u;
  if (!plan.exact_decoded_frame_refresh) {
    plan.skip_refresh = FableAnyBinkBindingHasDecodedFrameTable(bindings);
    return plan;
  }

  plan.exact_current_targets_complete =
      FableBinkDecodedCurrentTargetPlanesComplete(bindings,
                                                  decoded_frame_table);
  return plan;
}

// Generation liveness by the game's own routing: plane_output_slot is the
// movie object's surface slot the compositor samples from. The attract
// loop reuses the movie object and allocates NEW plane surfaces over the
// same slots, so a binding whose slot no longer holds its surface belongs
// to a finished movie generation. Without this, the old generation
// re-associates with the new decode table through freed-buffer aliasing
// and consumes every publish while the drawn surfaces freeze on their
// first frame (run 096: Lionhead stuck flat-white; the publish census had
// every publish on the first movie's six surfaces, zero on the second's).
bool FableBinkBindingIsLiveGeneration(uint8_t* base,
                                      const BinkVideoPlaneBinding& binding) {
  if (base == nullptr || binding.plane_output_slot == 0 ||
      !bink::IsReadableGuestByteRange(base, binding.plane_output_slot,
                                      sizeof(uint32_t))) {
    return true;
  }
  const uint32_t live_surface =
      bink::LoadGuestU32(base, binding.plane_output_slot);
  return live_surface == 0 || live_surface == binding.surface;
}

bool FableBinkVideoPlaneRefreshPlanAllowsBinding(
    const FableBinkVideoPlaneRefreshPlan& plan,
    const BinkVideoPlaneBinding& binding) {
  if (plan.skip_refresh) {
    return false;
  }
  if (!plan.exact_current_targets_complete) {
    return true;
  }
  return FableBinkBindingTargetsDecodedCurrentFrame(
      binding, plan.decoded_frame_table);
}


bool FableBinkVideoBindingsContainSnapshot(
    const std::unordered_map<uint32_t, BinkVideoPlaneBinding>& bindings,
    const RendererTrackedResourceSnapshot& snapshot) {
  return bindings.find(snapshot.guest_address) != bindings.end() ||
         bindings.find(snapshot.texture.object_address) != bindings.end() ||
         bindings.find(snapshot.parent_address) != bindings.end() ||
         bindings.find(snapshot.texture.parent_address) != bindings.end();
}

void FableLogBinkDecodedFramePlaneUpdate(
    std::string_view source_symbol,
    const FableBinkDecodedFramePlaneUpdateResult& result) {
  if (!result.table_read) {
    return;
  }

  const bink::BinkDecodedFrameTable& decoded_table = result.decoded_table;
  const uint32_t frame_table = decoded_table.frame_table;
  const uint32_t current_frame = decoded_table.current_frame;
  const auto& planes = decoded_table.planes;
  if (!result.table_ready) {
    static std::atomic<uint32_t> logged_bad_decoded_tables{0};
    const uint32_t count =
        logged_bad_decoded_tables.fetch_add(1, std::memory_order_relaxed) + 1u;
    if (count <= 16u || count % 240u == 0u) {
      REXLOG_INFO(
          "Refii renderer Bink decoded frame table not ready #{} symbol={} "
          "table=0x{:08X} frame={} dims={}x{} chroma={}x{} "
          "Y=0x{:08X}/{} CR=0x{:08X}/{} CB=0x{:08X}/{}",
          count, source_symbol, frame_table, current_frame,
          decoded_table.y_width, decoded_table.y_height,
          decoded_table.c_width, decoded_table.c_height, planes[0].address,
          planes[0].row_pitch, planes[1].address, planes[1].row_pitch,
          planes[2].address, planes[2].row_pitch);
    }
    return;
  }

  if (result.updated_any) {
    static std::atomic<uint32_t> logged_decoded_tables{0};
    const uint32_t count =
        logged_decoded_tables.fetch_add(1, std::memory_order_relaxed) + 1u;
    if (count <= 16u || count % 120u == 0u) {
      REXLOG_INFO(
          "Refii renderer Bink decoded frame table #{} symbol={} "
          "table=0x{:08X} frame={} dims={}x{} chroma={}x{} "
          "Y=0x{:08X}/{} CR=0x{:08X}/{} CB=0x{:08X}/{} "
          "table_matches={} plane_matches={} planes={}/{}/{}",
          count, source_symbol, frame_table, current_frame,
          decoded_table.y_width, decoded_table.y_height,
          decoded_table.c_width, decoded_table.c_height, planes[0].address,
          planes[0].row_pitch, planes[1].address, planes[1].row_pitch,
          planes[2].address, planes[2].row_pitch,
          result.decoded_table_matches, result.plane_matches,
          result.matched_planes[0] ? 1 : 0,
          result.matched_planes[1] ? 1 : 0,
          result.matched_planes[2] ? 1 : 0);
    }
  }
}

FableBinkDecodedFramePlaneUpdateResult
FableUpdateBinkDecodedFramePlaneSources(
    std::unordered_map<uint32_t, BinkVideoPlaneBinding>& bindings,
    uint64_t& serial,
    uint8_t* base,
    uint32_t frame_table,
    uint32_t max_plane_upload_size,
    const bink::BinkVideoUploadAddressCallbacks& upload_address_callbacks) {
  FableBinkDecodedFramePlaneUpdateResult result;
  if (!bink::ReadBinkDecodedFrameTable(base, frame_table,
                                       max_plane_upload_size,
                                       upload_address_callbacks,
                                       &result.decoded_table)) {
    return result;
  }
  result.table_read = true;

  const auto& planes = result.decoded_table.planes;
  if (!bink::BinkDecodedFramePlaneSourceValid(base, planes[0]) ||
      !bink::BinkDecodedFramePlaneSourceValid(base, planes[1]) ||
      !bink::BinkDecodedFramePlaneSourceValid(base, planes[2])) {
    return result;
  }
  result.table_ready = true;

  const uint32_t decoded_frame_table = result.decoded_table.frame_table;
  const uint32_t current_frame = result.decoded_table.current_frame;
  for (const auto& [surface, binding] : bindings) {
    (void)surface;
    if ((binding.decoded_frame_table == decoded_frame_table ||
         binding.movie == decoded_frame_table) &&
        FableBinkBindingIsLiveGeneration(base, binding)) {
      ++result.decoded_table_matches;
    }
  }

  for (auto& [surface, binding] : bindings) {
    (void)surface;
    if (!FableBinkBindingIsLiveGeneration(base, binding)) {
      continue;
    }
    // Do NOT relax this skip for never-associated fresh bindings: with
    // allocator reuse a fresh binding's physical address can alias the
    // PREVIOUS movie's freed decode buffers, and associating through that
    // alias freezes the plane on stale content (flat-white logos).
    if (result.decoded_table_matches != 0 &&
        binding.decoded_frame_table != decoded_frame_table &&
        binding.movie != decoded_frame_table) {
      continue;
    }

    const bink::BinkDecodedFramePlaneMatch match =
        bink::BinkDecodedPlaneMatchForBinding(
            base, binding.physical_address, binding.upload_size,
            result.decoded_table.aliases, upload_address_callbacks);
    if (match.plane_index > 2u) {
      continue;
    }
    const bink::BinkDecodedFramePlaneSource& plane =
        planes[match.plane_index];
    if (plane.byte_size == 0 || binding.upload_size == 0 ||
        binding.upload_size > plane.byte_size) {
      continue;
    }
    binding.decoded_plane_index = match.plane_index;
    binding.decoded_address = plane.address;
    binding.decoded_row_pitch = plane.row_pitch;
    binding.decoded_frame_table = decoded_frame_table;
    binding.decoded_frame_index = current_frame;
    binding.decoded_current_frame_target = match.frame_slot == current_frame;
    binding.serial = ++serial;
    ++result.plane_matches;
    result.matched_planes[match.plane_index] = true;
    result.updated_any = true;
  }

  return result;
}

FableBinkDecodedFramePlaneUpdateResult
FableUpdateAndLogBinkDecodedFramePlaneSources(
    std::unordered_map<uint32_t, BinkVideoPlaneBinding>& bindings,
    uint64_t& serial,
    uint8_t* base,
    uint32_t frame_table,
    uint32_t max_plane_upload_size,
    const bink::BinkVideoUploadAddressCallbacks& upload_address_callbacks,
    std::string_view source_symbol) {
  FableBinkDecodedFramePlaneUpdateResult result =
      FableUpdateBinkDecodedFramePlaneSources(
          bindings, serial, base, frame_table, max_plane_upload_size,
          upload_address_callbacks);
  FableLogBinkDecodedFramePlaneUpdate(source_symbol, result);
  return result;
}

}  // namespace refii::renderer
