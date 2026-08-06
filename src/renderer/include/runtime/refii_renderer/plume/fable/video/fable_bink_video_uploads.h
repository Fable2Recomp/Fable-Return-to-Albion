#pragma once

#include "runtime/refii_renderer/bik/bink_video_uploads.h"
#include "runtime/refii_renderer/hooks/resource/fable_observer_resources.h"

#include <array>
#include <cstdint>
#include <string_view>
#include <vector>

namespace refii::renderer {

struct FableBinkVideoPlaneUploadSource {
  bool ready = false;
  bool source_has_nonzero = false;
  bool unchanged = false;
  uint32_t upload_address = 0;
  uint32_t copy_size = 0;
  const uint8_t* source = nullptr;
  // Pitch-aware repack: nonzero when the decoded buffer's row pitch differs
  // from the plane's row pitch (aligned chroma rows) — a flat copy smears
  // the padding through the texture and zeroes the right edge (green strip).
  uint32_t src_row_pitch = 0;
  uint32_t dst_row_pitch = 0;
  int32_t first_nonzero = -1;
  uint8_t p0 = 0;
  uint8_t pc = 0;
  uint8_t p1 = 0;
};

enum class FableBinkVideoPlaneUploadDecision {
  kUnavailable,
  kBlank,
  kUnchanged,
  kChanged,
};

struct FableBinkVideoPlaneUploadDecisionResult {
  FableBinkVideoPlaneUploadDecision decision =
      FableBinkVideoPlaneUploadDecision::kUnavailable;
  bool should_publish = false;
};

struct FableBinkVideoPlaneUploadStats {
  bool valid = false;
  int32_t first_nonzero = -1;
  uint32_t word0 = 0;
  uint32_t sample_width = 1;
  uint32_t sample_height = 1;
  uint32_t row_pitch = 1;
  uint32_t format = 0;
  uint8_t p0 = 0;
  uint8_t pc = 0;
  uint8_t p1 = 0;
  uint32_t avg = 0;
  uint8_t min = 0;
  uint8_t max = 0;
};

bink::BinkVideoUploadAddressCallbacks FableBinkVideoUploadAddressCallbacks();

FableBinkVideoPlaneUploadSource FableResolveBinkVideoPlaneUploadSource(
    uint8_t* base,
    const BinkVideoPlaneBinding& binding,
    uint32_t record_byte_size,
    const std::vector<uint8_t>& current_upload,
    uint32_t max_upload_size,
    const bink::BinkVideoUploadAddressCallbacks& upload_address_callbacks);

FableBinkVideoPlaneUploadDecisionResult
FableClassifyBinkVideoPlaneUploadSource(
    const FableBinkVideoPlaneUploadSource& upload_source);

void FableLogBlankBinkVideoPlaneUpload(
    std::string_view source_symbol,
    const BinkVideoPlaneBinding& binding,
    const FableBinkVideoPlaneUploadSource& upload_source,
    uint64_t upload_generation,
    double total_ms,
    double address_ms);

void FableLogUnchangedBinkVideoPlaneUpload(
    std::string_view source_symbol,
    const BinkVideoPlaneBinding& binding,
    const FableBinkVideoPlaneUploadSource& upload_source,
    double total_ms,
    double address_ms,
    double compare_ms);

void FableLogChangedBinkVideoPlaneUpload(
    std::string_view source_symbol,
    const BinkVideoPlaneBinding& binding,
    const FableBinkVideoPlaneUploadSource& upload_source,
    uint64_t upload_generation,
    double total_ms,
    double address_ms,
    double compare_ms,
    double copy_ms,
    double publish_ms);

void FableLogVerboseBinkVideoPlaneUpload(
    uint8_t* base,
    std::string_view source_symbol,
    const BinkVideoPlaneBinding& binding,
    const FableBinkVideoPlaneUploadSource& upload_source,
    const std::array<uint32_t, 6>& fetch_dwords,
    uint64_t upload_generation,
    uint32_t& logged_uploads,
    bool verbose_tracing_enabled,
    uint32_t max_logs);

void FableLogBinkVideoPlaneSetup(
    const BinkVideoPlaneBinding& binding,
    const BinkVideoPlaneSetupProbe& probe,
    const std::array<uint32_t, 6>& fetch_dwords,
    uint32_t width,
    uint32_t height,
    uint32_t& logged_planes,
    bool tracing_enabled,
    uint32_t max_logs);

void FableLogRestoredBinkVideoPlaneResource(
    uint32_t surface,
    uint64_t upload_size,
    uint64_t serial,
    std::string_view source_symbol,
    const std::array<uint32_t, 6>& fetch_dwords,
    uint32_t& logged_uploads,
    bool tracing_enabled,
    uint32_t max_logs);

void FableCopyBinkVideoPlaneUploadData(
    std::vector<uint8_t>& upload_data,
    const FableBinkVideoPlaneUploadSource& upload_source);

bool FableApplyCopiedBinkVideoPlaneUploadToResource(
    RendererResourceRecord& record);

FableBinkVideoPlaneUploadStats FableBuildBinkVideoPlaneUploadStats(
    uint8_t* base,
    const BinkVideoPlaneBinding& binding,
    const FableBinkVideoPlaneUploadSource& upload_source,
    const std::array<uint32_t, 6>& fetch_dwords);

}  // namespace refii::renderer
