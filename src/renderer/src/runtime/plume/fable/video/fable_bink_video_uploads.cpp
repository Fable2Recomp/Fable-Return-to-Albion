#include "runtime/refii_renderer/plume/fable/video/fable_bink_video_uploads.h"

#include "runtime/cvars.h"
#include "runtime/refii_renderer/bik/bink_guest_memory.h"
#include "runtime/refii_renderer/plume/fable/resource/fable_texture_fetch.h"
#include "runtime/refii_renderer/plume/general/plume_fable_addressing.h"

#include <algorithm>
#include <atomic>
#include <cstddef>
#include <cstring>

#include <rex/logging.h>

namespace refii::renderer {
namespace {

uint64_t FableBinkVideoHashBytesFast64(const uint8_t* data, size_t size) {
  if (data == nullptr || size == 0) {
    return 0;
  }

  uint64_t hash = 0x9E3779B97F4A7C15ull ^ static_cast<uint64_t>(size);
  size_t offset = 0;
  for (; offset + sizeof(uint64_t) <= size; offset += sizeof(uint64_t)) {
    uint64_t lane = 0;
    std::memcpy(&lane, data + offset, sizeof(lane));
    hash ^= lane + 0x9E3779B97F4A7C15ull + (hash << 6u) + (hash >> 2u);
    hash = (hash << 27u) | (hash >> 37u);
    hash *= 0x3C79AC492BA7B653ull;
  }
  uint64_t tail = 0;
  std::memcpy(&tail, data + offset, size - offset);
  hash ^= tail + 0x1C69B3F74AC4AE35ull + (hash << 6u) + (hash >> 2u);
  hash ^= hash >> 33u;
  hash *= 0xFF51AFD7ED558CCDull;
  hash ^= hash >> 33u;
  hash *= 0xC4CEB9FE1A85EC53ull;
  hash ^= hash >> 33u;
  return hash != 0 ? hash : 1u;
}

}  // namespace

bink::BinkVideoUploadAddressCallbacks FableBinkVideoUploadAddressCallbacks() {
  bink::BinkVideoUploadAddressCallbacks callbacks;
  callbacks.decode_gpu_pointer = DecodeFableGpuBufferPointer;
  callbacks.select_readable_upload_address =
      bink::SelectFableBinkReadableUploadAddress;
  callbacks.is_gpu_byte_buffer_range = IsLikelyFableGpuByteBufferRange;
  callbacks.is_high_scratch_byte_buffer_range =
      IsLikelyFableHighScratchByteBufferRange;
  return callbacks;
}

FableBinkVideoPlaneUploadSource FableResolveBinkVideoPlaneUploadSource(
    uint8_t* base,
    const BinkVideoPlaneBinding& binding,
    uint32_t record_byte_size,
    const std::vector<uint8_t>& current_upload,
    uint32_t max_upload_size,
    const bink::BinkVideoUploadAddressCallbacks& upload_address_callbacks) {
  FableBinkVideoPlaneUploadSource result;
  if (base == nullptr || binding.surface == 0 || binding.upload_size == 0) {
    return result;
  }

  const uint32_t record_size =
      record_byte_size != 0 ? record_byte_size : binding.upload_size;
  result.copy_size = std::min(binding.upload_size, record_size);
  if (result.copy_size == 0 || result.copy_size > max_upload_size) {
    return result;
  }

  // Each plane surface owns ONE of Bink's double buffers; the last decode
  // event's pointer can belong to the OTHER buffer (run 087: surface with
  // physical=0xFF32D000 uploading from decoded=0xFF1C4000). Copying the
  // twin's bytes both shows the wrong frame and dedups this surface's real
  // updates away (pong set starved at 2 uploads vs 15 — the alternating
  // white/green flash on double-buffered movies). The decoded pointer is
  // authoritative only when it is this surface's own buffer; otherwise the
  // surface's own physical below is the source of truth.
  // The surface and decoder may name the same allocation through different
  // GPU aliases (for example a high scratch pointer versus its decoded guest
  // address). Compare the readable, decoded forms as well as the raw values.
  // Without this, an aliased own-buffer decode falls through to the flat
  // physical upload path and loses decoded_row_pitch repacking.
  const uint32_t selected_physical =
      binding.physical_address != 0
          ? bink::SelectBinkDecodedPlaneAddress(
                base, binding.physical_address, result.copy_size,
                upload_address_callbacks)
          : 0u;
  const uint32_t selected_decoded =
      binding.decoded_address != 0
          ? bink::SelectBinkDecodedPlaneAddress(
                base, binding.decoded_address, result.copy_size,
                upload_address_callbacks)
          : 0u;
  const bool decoded_is_own_buffer =
      binding.decoded_address != 0 &&
      (binding.physical_address == 0 ||
       binding.decoded_address == binding.physical_address ||
       (selected_decoded != 0 && selected_physical != 0 &&
        selected_decoded == selected_physical));
  if (decoded_is_own_buffer && binding.upload_size <= result.copy_size &&
      bink::IsReadableGuestByteRange(base, binding.decoded_address,
                                     result.copy_size)) {
    result.upload_address = binding.decoded_address;
    // The decoder's buffer rows are pitch-aligned; when that pitch differs
    // from the plane's own, the copy must repack row by row (a flat copy
    // shears the image and leaves the right edge zero — chroma-only zeros
    // render as a green strip).
    if (binding.decoded_row_pitch != 0 && binding.row_pitch != 0 &&
        binding.decoded_row_pitch != binding.row_pitch &&
        result.copy_size % binding.row_pitch == 0) {
      const uint32_t rows = result.copy_size / binding.row_pitch;
      const uint64_t required_span =
          rows != 0 ? uint64_t(rows - 1u) * binding.decoded_row_pitch +
                          binding.row_pitch
                    : 0u;
      if (rows != 0 && required_span <= UINT32_MAX &&
          bink::IsReadableGuestByteRange(base, binding.decoded_address,
                                         static_cast<uint32_t>(required_span))) {
        result.src_row_pitch = binding.decoded_row_pitch;
        result.dst_row_pitch = binding.row_pitch;
      }
    }
  }
  if (result.upload_address == 0) {
    result.upload_address = bink::SelectBinkVideoPlaneUploadAddress(
        base, binding.physical_address, result.copy_size,
        upload_address_callbacks);
  }
  if (result.upload_address == 0 ||
      !bink::IsReadableGuestByteRange(base, result.upload_address,
                                      result.copy_size)) {
    return result;
  }

  result.source =
      bink::GuestMemoryPtr(base, result.upload_address, result.copy_size);
  if (result.source == nullptr) {
    return result;
  }

  for (uint32_t byte_index = 0; byte_index < result.copy_size; ++byte_index) {
    if (result.source[byte_index] != 0) {
      result.first_nonzero = static_cast<int32_t>(byte_index);
      result.source_has_nonzero = true;
      break;
    }
  }

  result.p0 = result.copy_size == 0 ? 0u : result.source[0];
  result.pc = result.copy_size == 0 ? 0u : result.source[result.copy_size / 2u];
  result.p1 = result.copy_size == 0 ? 0u : result.source[result.copy_size - 1u];
  if (result.src_row_pitch != 0) {
    result.unchanged = current_upload.size() == result.copy_size;
    const uint32_t rows =
        result.dst_row_pitch != 0 ? result.copy_size / result.dst_row_pitch : 0u;
    for (uint32_t y = 0; result.unchanged && y < rows; ++y) {
      result.unchanged =
          std::memcmp(current_upload.data() +
                          uint64_t(y) * result.dst_row_pitch,
                      result.source + uint64_t(y) * result.src_row_pitch,
                      result.dst_row_pitch) == 0;
    }
  } else {
    result.unchanged =
        current_upload.size() == result.copy_size &&
        std::memcmp(current_upload.data(), result.source, result.copy_size) ==
            0;
  }
  result.ready = true;
  return result;
}

FableBinkVideoPlaneUploadDecisionResult
FableClassifyBinkVideoPlaneUploadSource(
    const FableBinkVideoPlaneUploadSource& upload_source) {
  FableBinkVideoPlaneUploadDecisionResult result;
  if (!upload_source.ready) {
    return result;
  }
  if (!upload_source.source_has_nonzero) {
    result.decision = FableBinkVideoPlaneUploadDecision::kBlank;
    return result;
  }
  if (upload_source.unchanged) {
    result.decision = FableBinkVideoPlaneUploadDecision::kUnchanged;
    return result;
  }
  result.decision = FableBinkVideoPlaneUploadDecision::kChanged;
  result.should_publish = true;
  return result;
}

void FableLogBlankBinkVideoPlaneUpload(
    std::string_view source_symbol,
    const BinkVideoPlaneBinding& binding,
    const FableBinkVideoPlaneUploadSource& upload_source,
    uint64_t upload_generation,
    double total_ms,
    double address_ms) {
  static std::atomic<uint32_t> logged_blank_bink_uploads{0};
  const uint32_t blank_count =
      logged_blank_bink_uploads.fetch_add(1, std::memory_order_relaxed) + 1u;
  if (!RendererDeepObserverHooksEnabled() ||
      (blank_count > 24u && blank_count % 240u != 0u)) {
    return;
  }

  REXLOG_INFO(
      "Refii renderer exact Bink zero-frame upload exclusion proof #{} "
      "symbol={} "
      "surface=0x{:08X} source=0x{:08X} physical=0x{:08X} "
      "decoded=0x{:08X}/{} table=0x{:08X} frame={} plane={} size={} "
      "generation={} source_nonzero=0 native_setup=sub_82A42FA0 "
      "native_frame_builder=sub_82A42D20 total_ms={:.3f} "
      "address_ms={:.3f}",
      blank_count, source_symbol, binding.surface, upload_source.upload_address,
      binding.physical_address, binding.decoded_address,
      binding.decoded_row_pitch, binding.decoded_frame_table,
      binding.decoded_frame_index, binding.decoded_plane_index,
      upload_source.copy_size, upload_generation, total_ms, address_ms);
}

void FableLogUnchangedBinkVideoPlaneUpload(
    std::string_view source_symbol,
    const BinkVideoPlaneBinding& binding,
    const FableBinkVideoPlaneUploadSource& upload_source,
    double total_ms,
    double address_ms,
    double compare_ms) {
  static std::atomic<uint32_t> logged_unchanged_bink_uploads{0};
  const uint32_t unchanged_count =
      logged_unchanged_bink_uploads.fetch_add(1, std::memory_order_relaxed) +
      1u;
  if (total_ms < 2.0) {
    return;
  }

  REXLOG_INFO(
      "Refii renderer Bink video stable upload skipped #{} symbol={} "
      "surface=0x{:08X} source=0x{:08X} physical=0x{:08X} "
      "decoded=0x{:08X}/{} table=0x{:08X} frame={} plane={} size={} "
      "data_first_nz={} p0={} pc={} p1={} total_ms={:.3f} "
      "address_ms={:.3f} compare_ms={:.3f}",
      unchanged_count, source_symbol, binding.surface,
      upload_source.upload_address, binding.physical_address,
      binding.decoded_address, binding.decoded_row_pitch,
      binding.decoded_frame_table, binding.decoded_frame_index,
      binding.decoded_plane_index, upload_source.copy_size,
      upload_source.first_nonzero, upload_source.p0, upload_source.pc,
      upload_source.p1, total_ms, address_ms, compare_ms);
}

void FableLogChangedBinkVideoPlaneUpload(
    std::string_view source_symbol,
    const BinkVideoPlaneBinding& binding,
    const FableBinkVideoPlaneUploadSource& upload_source,
    uint64_t upload_generation,
    double total_ms,
    double address_ms,
    double compare_ms,
    double copy_ms,
    double publish_ms) {
  static std::atomic<uint32_t> logged_changed_bink_uploads{0};
  const uint32_t changed_count =
      logged_changed_bink_uploads.fetch_add(1, std::memory_order_relaxed) + 1u;
  if (total_ms < 2.0 && changed_count > 16u && changed_count % 120u != 0u) {
    return;
  }

  REXLOG_INFO(
      "Refii renderer Bink video upload timing #{} symbol={} "
      "surface=0x{:08X} source=0x{:08X} physical=0x{:08X} "
      "decoded=0x{:08X}/{} table=0x{:08X} frame={} plane={} size={} "
      "generation={} data_first_nz={} p0={} pc={} p1={} "
      "total_ms={:.3f} address_ms={:.3f} "
      "compare_ms={:.3f} copy_ms={:.3f} publish_ms={:.3f}",
      changed_count, source_symbol, binding.surface,
      upload_source.upload_address, binding.physical_address,
      binding.decoded_address, binding.decoded_row_pitch,
      binding.decoded_frame_table, binding.decoded_frame_index,
      binding.decoded_plane_index, upload_source.copy_size, upload_generation,
      upload_source.first_nonzero, upload_source.p0, upload_source.pc,
      upload_source.p1, total_ms, address_ms, compare_ms, copy_ms, publish_ms);
}

void FableLogVerboseBinkVideoPlaneUpload(
    uint8_t* base,
    std::string_view source_symbol,
    const BinkVideoPlaneBinding& binding,
    const FableBinkVideoPlaneUploadSource& upload_source,
    const std::array<uint32_t, 6>& fetch_dwords,
    uint64_t upload_generation,
    uint32_t& logged_uploads,
    bool verbose_tracing_enabled,
    uint32_t max_logs) {
  if (!verbose_tracing_enabled || logged_uploads >= max_logs) {
    return;
  }

  const FableBinkVideoPlaneUploadStats upload_stats =
      FableBuildBinkVideoPlaneUploadStats(base, binding, upload_source,
                                          fetch_dwords);
  REXLOG_INFO(
      "Refii renderer Bink video upload #{} symbol={} surface=0x{:08X} "
      "source=0x{:08X} row_pitch=0x{:08X} physical=0x{:08X} size={} "
      "first_nz={} word0={:08X} generation={} dims={}x{} row={} fmt={} "
      "p0={} pc={} p1={} avg={} min={} max={}",
      logged_uploads + 1u, source_symbol, binding.surface,
      upload_source.upload_address, binding.row_pitch,
      binding.physical_address, binding.upload_size,
      upload_stats.first_nonzero, upload_stats.word0, upload_generation,
      upload_stats.sample_width, upload_stats.sample_height,
      upload_stats.row_pitch, upload_stats.format, upload_stats.p0,
      upload_stats.pc, upload_stats.p1, upload_stats.avg, upload_stats.min,
      upload_stats.max);
  ++logged_uploads;
}

void FableLogBinkVideoPlaneSetup(
    const BinkVideoPlaneBinding& binding,
    const BinkVideoPlaneSetupProbe& probe,
    const std::array<uint32_t, 6>& fetch_dwords,
    uint32_t width,
    uint32_t height,
    uint32_t& logged_planes,
    bool tracing_enabled,
    uint32_t max_logs) {
  if (!tracing_enabled || logged_planes >= max_logs) {
    return;
  }

  REXLOG_INFO(
      "Refii renderer Bink video plane #{} movie=0x{:08X} "
      "plane=0x{:08X} output_slot=0x{:08X} surface=0x{:08X} "
      "physical=0x{:08X} row_pitch=0x{:08X} selected=0x{:08X} "
      "size={} nz={} dims={}x{} dwords={:08X}/{:08X}/{:08X}/"
      "{:08X}/{:08X}/{:08X}",
      logged_planes + 1u, binding.movie, binding.plane_info,
      binding.plane_output_slot, binding.surface, binding.physical_address,
      binding.row_pitch, probe.selected_address, binding.upload_size,
      probe.first_nonzero, width, height, fetch_dwords[0], fetch_dwords[1],
      fetch_dwords[2], fetch_dwords[3], fetch_dwords[4], fetch_dwords[5]);
  ++logged_planes;
}

void FableLogRestoredBinkVideoPlaneResource(
    uint32_t surface,
    uint64_t upload_size,
    uint64_t serial,
    std::string_view source_symbol,
    const std::array<uint32_t, 6>& fetch_dwords,
    uint32_t& logged_uploads,
    bool tracing_enabled,
    uint32_t max_logs) {
  if (!tracing_enabled || logged_uploads >= max_logs) {
    return;
  }

  REXLOG_INFO(
      "Refii renderer restored Bink video plane resource #{} "
      "surface=0x{:08X} upload={} serial={} source={} "
      "dwords={:08X}/{:08X}/{:08X}/{:08X}/{:08X}/{:08X}",
      logged_uploads + 1u, surface, upload_size, serial, source_symbol,
      fetch_dwords[0], fetch_dwords[1], fetch_dwords[2], fetch_dwords[3],
      fetch_dwords[4], fetch_dwords[5]);
  ++logged_uploads;
}

void FableCopyBinkVideoPlaneUploadData(
    std::vector<uint8_t>& upload_data,
    const FableBinkVideoPlaneUploadSource& upload_source) {
  if (!upload_source.ready || upload_source.source == nullptr ||
      upload_source.copy_size == 0) {
    return;
  }
  upload_data.resize(upload_source.copy_size);
  if (upload_source.src_row_pitch != 0 && upload_source.dst_row_pitch != 0) {
    const uint32_t rows = upload_source.copy_size / upload_source.dst_row_pitch;
    for (uint32_t y = 0; y < rows; ++y) {
      std::memcpy(upload_data.data() + uint64_t(y) * upload_source.dst_row_pitch,
                  upload_source.source + uint64_t(y) * upload_source.src_row_pitch,
                  upload_source.dst_row_pitch);
    }
    return;
  }
  std::memcpy(upload_data.data(), upload_source.source, upload_data.size());
}

bool FableApplyCopiedBinkVideoPlaneUploadToResource(
    RendererResourceRecord& record) {
  if (record.upload_data.empty() || !IsImageResourceKind(record.kind)) {
    return false;
  }

  ++record.upload_generation;
  record.raw.hash = FableBinkVideoHashBytesFast64(record.upload_data.data(),
                                                  record.upload_data.size());

  GuestTextureRecord texture = GuestTextureRecordFor(record);
  texture.upload_generation = record.upload_generation;
  ApplyGuestTextureRecord(record, texture);
  return true;
}

FableBinkVideoPlaneUploadStats FableBuildBinkVideoPlaneUploadStats(
    uint8_t* base,
    const BinkVideoPlaneBinding& binding,
    const FableBinkVideoPlaneUploadSource& upload_source,
    const std::array<uint32_t, 6>& fetch_dwords) {
  FableBinkVideoPlaneUploadStats stats;
  if (!upload_source.ready || upload_source.upload_address == 0 ||
      binding.upload_size == 0) {
    return stats;
  }

  const uint32_t scan_size = std::min<uint32_t>(binding.upload_size, 4096u);
  for (uint32_t byte_index = 0; byte_index < scan_size; ++byte_index) {
    if (bink::LoadGuestU8(base, upload_source.upload_address + byte_index) !=
        0) {
      stats.first_nonzero = static_cast<int32_t>(byte_index);
      break;
    }
  }
  if (stats.first_nonzero >= 0) {
    stats.word0 = bink::LoadGuestU32(base, upload_source.upload_address);
  }

  const FableTextureFetchInfo fetch_info =
      FableTextureFetchInfoFromDwords(fetch_dwords);
  stats.sample_width = std::max<uint32_t>(1u, fetch_info.width);
  stats.sample_height = std::max<uint32_t>(1u, fetch_info.height);
  stats.format = fetch_info.format;
  stats.row_pitch =
      stats.sample_height != 0 && binding.upload_size % stats.sample_height == 0
          ? std::max<uint32_t>(stats.sample_width,
                               binding.upload_size / stats.sample_height)
          : stats.sample_width;

  const auto sample_byte = [&](uint32_t x, uint32_t y) {
    const uint64_t offset = uint64_t(y) * stats.row_pitch + x;
    return offset < binding.upload_size
               ? bink::LoadGuestU8(
                     base, upload_source.upload_address +
                               static_cast<uint32_t>(offset))
               : uint8_t(0);
  };

  uint64_t sample_sum = 0;
  uint32_t sample_count = 0;
  uint8_t sample_min = 0xFFu;
  uint8_t sample_max = 0u;
  const uint32_t step_x = std::max<uint32_t>(1u, stats.sample_width / 32u);
  const uint32_t step_y = std::max<uint32_t>(1u, stats.sample_height / 32u);
  for (uint32_t y = 0; y < stats.sample_height; y += step_y) {
    for (uint32_t x = 0; x < stats.sample_width; x += step_x) {
      const uint8_t value = sample_byte(x, y);
      sample_sum += value;
      sample_min = std::min<uint8_t>(sample_min, value);
      sample_max = std::max<uint8_t>(sample_max, value);
      ++sample_count;
    }
  }
  if (sample_count == 0) {
    sample_count = 1;
    sample_min = 0;
  }

  stats.p0 = sample_byte(0, 0);
  stats.pc = sample_byte(stats.sample_width / 2u, stats.sample_height / 2u);
  stats.p1 = sample_byte(stats.sample_width - 1u, stats.sample_height - 1u);
  stats.avg = static_cast<uint32_t>(sample_sum / sample_count);
  stats.min = sample_min;
  stats.max = sample_max;
  stats.valid = true;
  return stats;
}

}  // namespace refii::renderer
