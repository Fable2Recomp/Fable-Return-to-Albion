#include "runtime/plume/resource/plume_resource_manager_internal.h"

#include "runtime/cvars.h"
#include "runtime/refii_renderer/gtl/refii_gtl.h"
// FableGuestMemoryBase — lets the stated-base probe below read the bytes a
// fetch constant points at without threading a guest base through the caller.
#include "runtime/refii_renderer/plume/fable/fable_guest_display.h"
#include "runtime/refii_renderer/bik/bink_guest_memory.h"
#include "runtime/refii_renderer/plume/general/plume_fable_addressing.h"

#include <algorithm>
#include <array>
#include <atomic>
#include <chrono>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <mutex>
#include <string>
#include <unordered_map>
#include <unordered_set>
#include <utility>
#include <vector>

#include <rex/logging.h>
#include <rex/math.h>
#include <rex/graphics/pipeline/texture/conversion.h>
#include <rex/graphics/pipeline/texture/info.h>
#include <rex/graphics/pipeline/texture/util.h>

namespace refii::renderer {

#if REFII_RENDERER_HAS_PLUME
namespace {
constexpr uint32_t kTextureUploadRowAlignment = 256;
constexpr uint32_t kTextureUploadPlacementAlignment = 512;
constexpr uint64_t kMaxTextureUploadBufferSize = 128ull << 20;
constexpr uint64_t kFrameTextureUploadSlabSize = 16ull << 20;
constexpr size_t kMaxFrameTextureUploadDecodeCacheEntries = 32;
constexpr size_t kTrackedBufferDebugSampleBytes = 64 * 1024;
uint32_t g_fable_title_texture_decode_probe_count = 0;
uint32_t g_fable_r8_upload_timing_log_count = 0;
uint32_t g_ctx1_decode_census_count = 0;

using TextureUploadClock = std::chrono::steady_clock;

double TextureUploadElapsedMs(TextureUploadClock::time_point begin,
                              TextureUploadClock::time_point end) {
  return std::chrono::duration<double, std::milli>(end - begin).count();
}

struct FrameSlabWriteTiming {
  double map_ms = 0.0;
  double writer_ms = 0.0;
  double unmap_ms = 0.0;
};

struct R8TiledUploadTiming {
  double hash_ms = 0.0;
  double layout_ms = 0.0;
  double runs_ms = 0.0;
  double slab_map_ms = 0.0;
  double slab_writer_ms = 0.0;
  double clear_ms = 0.0;
  double copy_ms = 0.0;
  double debug_ms = 0.0;
  double slab_unmap_ms = 0.0;
  double total_ms = 0.0;
  size_t run_count = 0;
};

struct Tiled2DOffsetCacheKey {
  uint32_t width_blocks = 0;
  uint32_t height_blocks = 0;
  uint32_t pitch_blocks = 0;
  uint32_t bytes_per_block_log2 = 0;

  bool operator==(const Tiled2DOffsetCacheKey& other) const {
    return width_blocks == other.width_blocks &&
           height_blocks == other.height_blocks &&
           pitch_blocks == other.pitch_blocks &&
           bytes_per_block_log2 == other.bytes_per_block_log2;
  }
};

struct Tiled2DOffsetCacheKeyHash {
  size_t operator()(const Tiled2DOffsetCacheKey& key) const {
    uint64_t hash = 14695981039346656037ull;
    const auto mix = [&hash](uint32_t value) {
      hash ^= value;
      hash *= 1099511628211ull;
    };
    mix(key.width_blocks);
    mix(key.height_blocks);
    mix(key.pitch_blocks);
    mix(key.bytes_per_block_log2);
    return static_cast<size_t>(hash);
  }
};

const std::vector<uint32_t>& Tiled2DOffsetTable(
    uint32_t width_blocks, uint32_t height_blocks, uint32_t pitch_blocks,
    uint32_t bytes_per_block_log2) {
  static std::unordered_map<Tiled2DOffsetCacheKey, std::vector<uint32_t>,
                            Tiled2DOffsetCacheKeyHash>
      tables;
  if (tables.size() > 64u) {
    tables.clear();
  }

  const Tiled2DOffsetCacheKey key{
      .width_blocks = width_blocks,
      .height_blocks = height_blocks,
      .pitch_blocks = pitch_blocks,
      .bytes_per_block_log2 = bytes_per_block_log2,
  };
  auto table_it = tables.find(key);
  if (table_it != tables.end()) {
    return table_it->second;
  }

  std::vector<uint32_t> offsets;
  offsets.reserve(static_cast<size_t>(width_blocks) * height_blocks);
  for (uint32_t y = 0; y < height_blocks; ++y) {
    for (uint32_t x = 0; x < width_blocks; ++x) {
      offsets.push_back(static_cast<uint32_t>(
          rex::graphics::texture_util::GetTiledOffset2D(
              static_cast<int32_t>(x), static_cast<int32_t>(y), pitch_blocks,
              bytes_per_block_log2)));
    }
  }

  auto [inserted_it, inserted] = tables.emplace(key, std::move(offsets));
  (void)inserted;
  return inserted_it->second;
}

struct Tiled2DR8CopyRun {
  uint32_t target_offset = 0;
  uint32_t source_offset = 0;
  uint32_t byte_count = 0;
};

struct Tiled2DR8CopyRunTable {
  std::vector<Tiled2DR8CopyRun> runs;
  uint32_t source_end = 0;
};

struct Tiled2DR8CopyRunCacheKey {
  uint32_t width = 0;
  uint32_t height = 0;
  uint32_t pitch_blocks = 0;
  uint32_t row_pitch_bytes = 0;

  bool operator==(const Tiled2DR8CopyRunCacheKey& other) const {
    return width == other.width && height == other.height &&
           pitch_blocks == other.pitch_blocks &&
           row_pitch_bytes == other.row_pitch_bytes;
  }
};

struct Tiled2DR8CopyRunCacheKeyHash {
  size_t operator()(const Tiled2DR8CopyRunCacheKey& key) const {
    uint64_t hash = 14695981039346656037ull;
    const auto mix = [&hash](uint32_t value) {
      hash ^= value;
      hash *= 1099511628211ull;
    };
    mix(key.width);
    mix(key.height);
    mix(key.pitch_blocks);
    mix(key.row_pitch_bytes);
    return static_cast<size_t>(hash);
  }
};

const Tiled2DR8CopyRunTable& Tiled2DR8CopyRuns(uint32_t width,
                                               uint32_t height,
                                               uint32_t pitch_blocks,
                                               uint32_t row_pitch_bytes) {
  static std::unordered_map<Tiled2DR8CopyRunCacheKey, Tiled2DR8CopyRunTable,
                            Tiled2DR8CopyRunCacheKeyHash>
      tables;
  if (tables.size() > 32u) {
    tables.clear();
  }

  const Tiled2DR8CopyRunCacheKey key{
      .width = width,
      .height = height,
      .pitch_blocks = pitch_blocks,
      .row_pitch_bytes = row_pitch_bytes,
  };
  auto table_it = tables.find(key);
  if (table_it != tables.end()) {
    return table_it->second;
  }

  Tiled2DR8CopyRunTable table;
  const std::vector<uint32_t>& offsets =
      Tiled2DOffsetTable(width, height, pitch_blocks, 0);
  const size_t required_offsets = static_cast<size_t>(width) * height;
  table.runs.reserve(required_offsets / 8u + height);
  for (uint32_t y = 0; y < height; ++y) {
    for (uint32_t x = 0; x < width; ++x) {
      const size_t offset_index = static_cast<size_t>(y) * width + x;
      if (offset_index >= offsets.size()) {
        break;
      }
      const uint32_t target_offset = y * row_pitch_bytes + x;
      const uint32_t source_offset = offsets[offset_index];
      table.source_end = std::max<uint32_t>(table.source_end,
                                            source_offset + 1u);
      if (!table.runs.empty()) {
        Tiled2DR8CopyRun& run = table.runs.back();
        if (run.target_offset + run.byte_count == target_offset &&
            run.source_offset + run.byte_count == source_offset) {
          ++run.byte_count;
          continue;
        }
      }
      table.runs.push_back(Tiled2DR8CopyRun{
          .target_offset = target_offset,
          .source_offset = source_offset,
          .byte_count = 1u,
      });
    }
  }

  auto [inserted_it, inserted] = tables.emplace(key, std::move(table));
  (void)inserted;
  return inserted_it->second;
}

void CopyR8TiledRun(uint8_t* target, const uint8_t* source,
                    uint32_t byte_count) {
  while (byte_count >= 8u) {
    uint64_t word = 0;
    std::memcpy(&word, source, sizeof(word));
    std::memcpy(target, &word, sizeof(word));
    source += sizeof(word);
    target += sizeof(word);
    byte_count -= sizeof(word);
  }
  while (byte_count != 0u) {
    *target++ = *source++;
    --byte_count;
  }
}

uint64_t HashUploadBytesFnv1a64(const uint8_t* data, size_t size) {
  uint64_t hash = 14695981039346656037ull;
  for (size_t i = 0; i < size; ++i) {
    hash ^= data[i];
    hash *= 1099511628211ull;
  }
  return hash;
}

uint64_t HashTextureUploadIdentity(
    const RendererTrackedResourceSnapshot& snapshot,
    const PlumeTrackedResource& tracked, size_t source_size,
    uint64_t content_hash) {
  uint64_t hash = 14695981039346656037ull;
  const auto mix = [&hash](uint64_t value) {
    hash ^= value;
    hash *= 1099511628211ull;
  };

  mix(static_cast<uint32_t>(snapshot.kind));
  mix(snapshot.guest_address);
  mix(snapshot.parent_address);
  mix(snapshot.texture.object_address);
  mix(snapshot.texture.parent_address);
  mix(snapshot.texture.base_address);
  mix(snapshot.texture.byte_size);
  mix(static_cast<uint64_t>(source_size));
  mix(content_hash);
  mix(static_cast<uint32_t>(PlumeGuestTextureFormat(tracked)));
  mix(PlumeGuestTextureWidth(tracked));
  mix(PlumeGuestTextureHeight(tracked));
  mix(PlumeGuestTextureDepth(tracked));
  mix(static_cast<uint32_t>(tracked.texture_view_dimension));
  mix(PlumeGuestTextureGuestFormat(tracked));
  mix(PlumeGuestTextureDimension(tracked));
  mix(PlumeGuestTexturePitch(tracked));
  for (uint32_t dword : PreferredTextureFetchDwords(snapshot.texture)) {
    mix(dword);
  }

  return hash != 0 ? hash : 1u;
}

bool IsFableTitleTextureDecodeProbe(const PlumeTrackedResource& tracked) {
  return tracked.guest_address == 0x404DE550u ||
         PlumeGuestTextureBaseAddress(tracked) == 0x1A0D7000u;
}

bool IsFableCanonicalTitleAtlasAuthoredLinearBc3Upload(
    const RendererTrackedResourceSnapshot& snapshot,
    const PlumeTrackedResource& tracked,
    const rex::graphics::xenos::xe_gpu_texture_fetch_t& fetch) {
  return snapshot.kind == RendererTrackedResourceKind::kTexture &&
         PlumeGuestTextureFormat(tracked) == plume::RenderFormat::BC3_UNORM &&
         PlumeGuestTextureBaseAddress(tracked) == 0x16C40000u &&
         fetch.format == rex::graphics::xenos::TextureFormat::k_DXT4_5 &&
         fetch.dimension != rex::graphics::xenos::DataDimension::k3D &&
         !fetch.tiled &&
         tracked.texture_view_dimension ==
             plume::RenderTextureViewDimension::TEXTURE_2D &&
         PlumeGuestTextureDepth(tracked) <= 1u;
}

uint32_t CountByteValue(const std::vector<uint8_t>& bytes, uint8_t value) {
  return static_cast<uint32_t>(
      std::count(bytes.begin(), bytes.end(), value));
}

template <typename Writer>
bool TryWriteTextureUploadInFrameSlab(plume::RenderDevice* device,
                                      PlumeTrackedResource& tracked,
                                      size_t upload_size, Writer writer,
                                      FrameSlabWriteTiming* timing = nullptr) {
  if (!device || upload_size == 0 ||
      g_plume_resources.frame_texture_upload_slabs.empty()) {
    return false;
  }

  const size_t frame_slab_index =
      g_plume_resources.frame_texture_upload_slab_index %
      g_plume_resources.frame_texture_upload_slabs.size();
  auto& frame_slabs =
      g_plume_resources.frame_texture_upload_slabs[frame_slab_index];
  uint32_t& slab_index =
      g_plume_resources.frame_texture_upload_slab_indices[frame_slab_index];
  const uint64_t requested_size =
      ((upload_size + kTextureUploadPlacementAlignment - 1u) /
       kTextureUploadPlacementAlignment) *
      kTextureUploadPlacementAlignment;

  for (;;) {
    if (frame_slabs.size() <= slab_index) {
      frame_slabs.emplace_back();
    }

    PlumeFrameTextureUploadSlab& slab = frame_slabs[slab_index];
    if (!slab.buffer || slab.size < requested_size) {
      if (slab.buffer && slab.mapped) {
        slab.buffer->unmap();
      }
      slab.size = std::max<uint64_t>(kFrameTextureUploadSlabSize,
                                     requested_size);
      slab.offset = 0;
      slab.mapped = nullptr;
      slab.buffer = device->createBuffer(plume::RenderBufferDesc::UploadBuffer(
          static_cast<uint32_t>(slab.size)));
      if (!slab.buffer) {
        slab.size = 0;
        slab.offset = 0;
        slab.mapped = nullptr;
        return false;
      }
      slab.buffer->setName("Refii Fable frame texture upload slab");
      slab.mapped = static_cast<uint8_t*>(slab.buffer->map());
      if (!slab.mapped) {
        slab.buffer.reset();
        slab.size = 0;
        slab.offset = 0;
        return false;
      }
    }

    const uint64_t allocation_offset =
        ((slab.offset + kTextureUploadPlacementAlignment - 1u) /
         kTextureUploadPlacementAlignment) *
        kTextureUploadPlacementAlignment;
    const uint64_t allocation_end = allocation_offset + upload_size;
    if (allocation_end <= slab.size) {
      if (!slab.mapped) {
        return false;
      }
      uint8_t* upload_target = slab.mapped + allocation_offset;
      const TextureUploadClock::time_point writer_begin =
          TextureUploadClock::now();
      if (!writer(upload_target, upload_size)) {
        return false;
      }
      const TextureUploadClock::time_point writer_end =
          TextureUploadClock::now();
      if (timing) {
        timing->writer_ms += TextureUploadElapsedMs(writer_begin, writer_end);
      }
      const TextureUploadClock::time_point unmap_begin =
          TextureUploadClock::now();
      const plume::RenderRange written_range(allocation_offset,
                                             allocation_end);
      slab.buffer->unmap(0, &written_range);
      slab.mapped = static_cast<uint8_t*>(slab.buffer->map());
      if (!slab.mapped) {
        return false;
      }
      const TextureUploadClock::time_point unmap_end =
          TextureUploadClock::now();
      if (timing) {
        timing->unmap_ms += TextureUploadElapsedMs(unmap_begin, unmap_end);
      }
      slab.offset = allocation_end;

      tracked.texture_upload_buffer = slab.buffer.get();
      tracked.texture_upload_offset = allocation_offset;
      tracked.guest_texture.upload_buffer = tracked.texture_upload_buffer;
      tracked.guest_texture.upload_offset = tracked.texture_upload_offset;
      return true;
    }

    ++slab_index;
  }
}

bool EnsureFrameTextureUploadSlabsInternal(plume::RenderDevice* device) {
  if (!device || g_plume_resources.frame_texture_upload_slabs.empty()) {
    return false;
  }

  for (size_t frame_index = 0;
       frame_index < g_plume_resources.frame_texture_upload_slabs.size();
       ++frame_index) {
    auto& frame_slabs = g_plume_resources.frame_texture_upload_slabs[frame_index];
    if (frame_slabs.empty()) {
      frame_slabs.emplace_back();
    }

    PlumeFrameTextureUploadSlab& slab = frame_slabs.front();
    if (slab.buffer && slab.mapped && slab.size >= kFrameTextureUploadSlabSize) {
      continue;
    }

    if (slab.buffer && slab.mapped) {
      slab.buffer->unmap();
    }
    slab.buffer.reset();
    slab.mapped = nullptr;
    slab.size = kFrameTextureUploadSlabSize;
    slab.offset = 0;
    slab.buffer = device->createBuffer(plume::RenderBufferDesc::UploadBuffer(
        static_cast<uint32_t>(slab.size)));
    if (!slab.buffer) {
      slab.size = 0;
      return false;
    }
    slab.buffer->setName("Refii Fable frame texture upload slab");
    slab.mapped = static_cast<uint8_t*>(slab.buffer->map());
    if (!slab.mapped) {
      slab.buffer.reset();
      slab.size = 0;
      return false;
    }
  }

  return true;
}

bool TryStageTextureUploadInFrameSlab(plume::RenderDevice* device,
                                      PlumeTrackedResource& tracked,
                                      const std::vector<uint8_t>& upload_bytes) {
  return TryWriteTextureUploadInFrameSlab(
      device, tracked, upload_bytes.size(),
      [&upload_bytes](uint8_t* upload_target, size_t upload_size) {
        std::memcpy(upload_target, upload_bytes.data(), upload_size);
        return true;
      });
}

struct R8TiledTextureUploadLayout {
  uint32_t width = 0;
  uint32_t height = 0;
  uint32_t row_pitch_bytes = 0;
  uint32_t guest_pitch_blocks = 0;
  uint64_t upload_size = 0;
};

struct R8LinearTextureUploadLayout {
  uint32_t width = 0;
  uint32_t height = 0;
  uint32_t row_pitch_bytes = 0;
  uint32_t guest_row_pitch_bytes = 0;
  uint64_t upload_size = 0;
};

struct Dxt3aR8TextureUploadLayout {
  uint32_t width = 0;
  uint32_t height = 0;
  uint32_t width_blocks = 0;
  uint32_t height_blocks = 0;
  uint32_t row_pitch_bytes = 0;
  uint32_t guest_pitch_blocks = 0;
  bool tiled = false;
  rex::graphics::xenos::Endian endianness =
      rex::graphics::xenos::Endian::kNone;
  uint64_t upload_size = 0;
};

bool TryGetDxt3aR8TextureUploadLayout(
    const RendererTrackedResourceSnapshot& snapshot,
    const PlumeTrackedResource& tracked,
    Dxt3aR8TextureUploadLayout& layout) {
  if (snapshot.kind != RendererTrackedResourceKind::kTexture ||
      snapshot.upload_data_mip_level != 0 || snapshot.upload_data.empty() ||
      PlumeGuestTextureFormat(tracked) != plume::RenderFormat::R8_UNORM ||
      tracked.texture_view_dimension !=
          plume::RenderTextureViewDimension::TEXTURE_2D ||
      PlumeGuestTextureDepth(tracked) != 1u) {
    return false;
  }

  namespace xenos = rex::graphics::xenos;
  namespace texture_util = rex::graphics::texture_util;
  const std::array<uint32_t, 6> fetch_dwords =
      PreferredTextureFetchDwords(snapshot.texture);
  xenos::xe_gpu_texture_fetch_t fetch = {};
  fetch.dword_0 = fetch_dwords[0];
  fetch.dword_1 = fetch_dwords[1];
  fetch.dword_2 = fetch_dwords[2];
  fetch.dword_3 = fetch_dwords[3];
  fetch.dword_4 = fetch_dwords[4];
  fetch.dword_5 = fetch_dwords[5];
  if (fetch.type != xenos::FetchConstantType::kTexture ||
      fetch.dimension == xenos::DataDimension::k3D ||
      fetch.format != xenos::TextureFormat::k_DXT3A) {
    return false;
  }
  if (snapshot.upload_data_linear) {
    fetch.tiled = 0;
  }

  const rex::graphics::FormatInfo* guest_format_info =
      rex::graphics::FormatInfo::Get(fetch.format);
  if (!guest_format_info || guest_format_info->bytes_per_block() != 8u ||
      guest_format_info->block_width != 4u ||
      guest_format_info->block_height != 4u) {
    return false;
  }

  const uint32_t width = PlumeGuestTextureWidth(tracked);
  const uint32_t height = PlumeGuestTextureHeight(tracked);
  if (width == 0 || height == 0) {
    return false;
  }

  const texture_util::TextureGuestLayout guest_layout =
      texture_util::GetGuestTextureLayout(
          fetch.dimension, fetch.pitch, width, height, 1, fetch.tiled != 0,
          fetch.format, fetch.packed_mips != 0, true, 0);
  if (guest_layout.base.row_pitch_bytes == 0) {
    return false;
  }

  const uint32_t row_pitch_bytes = static_cast<uint32_t>(
      ((uint64_t(width) + kTextureUploadRowAlignment - 1u) /
       kTextureUploadRowAlignment) *
      kTextureUploadRowAlignment);
  const uint64_t upload_size = uint64_t(row_pitch_bytes) * height;
  if (upload_size == 0 || upload_size > kMaxTextureUploadBufferSize) {
    return false;
  }

  layout.width = width;
  layout.height = height;
  layout.width_blocks = std::max<uint32_t>(1u, (width + 3u) / 4u);
  layout.height_blocks = std::max<uint32_t>(1u, (height + 3u) / 4u);
  layout.row_pitch_bytes = row_pitch_bytes;
  layout.guest_pitch_blocks =
      std::max<uint32_t>(guest_layout.base.row_pitch_bytes / 8u,
                         layout.width_blocks);
  layout.tiled = fetch.tiled != 0;
  layout.endianness = fetch.endianness;
  layout.upload_size = upload_size;
  return true;
}

bool TryGetR8LinearTextureUploadLayout(
    const RendererTrackedResourceSnapshot& snapshot,
    const PlumeTrackedResource& tracked,
    R8LinearTextureUploadLayout& layout) {
  if (snapshot.kind != RendererTrackedResourceKind::kTexture ||
      snapshot.upload_data_mip_level != 0 ||
      snapshot.upload_data.empty() ||
      PlumeGuestTextureFormat(tracked) != plume::RenderFormat::R8_UNORM ||
      tracked.texture_view_dimension !=
          plume::RenderTextureViewDimension::TEXTURE_2D ||
      PlumeGuestTextureDepth(tracked) != 1u) {
    return false;
  }

  namespace xenos = rex::graphics::xenos;
  namespace texture_util = rex::graphics::texture_util;
  const std::array<uint32_t, 6> fetch_dwords =
      PreferredTextureFetchDwords(snapshot.texture);
  xenos::xe_gpu_texture_fetch_t fetch = {};
  fetch.dword_0 = fetch_dwords[0];
  fetch.dword_1 = fetch_dwords[1];
  fetch.dword_2 = fetch_dwords[2];
  fetch.dword_3 = fetch_dwords[3];
  fetch.dword_4 = fetch_dwords[4];
  fetch.dword_5 = fetch_dwords[5];
  if (fetch.type != xenos::FetchConstantType::kTexture ||
      fetch.dimension == xenos::DataDimension::k3D ||
      (fetch.tiled && !snapshot.upload_data_linear)) {
    return false;
  }

  const rex::graphics::FormatInfo* guest_format_info =
      rex::graphics::FormatInfo::Get(fetch.format);
  if (!guest_format_info || guest_format_info->bytes_per_block() != 1u ||
      guest_format_info->block_width != 1u ||
      guest_format_info->block_height != 1u) {
    return false;
  }

  const uint32_t width = PlumeGuestTextureWidth(tracked);
  const uint32_t height = PlumeGuestTextureHeight(tracked);
  if (width == 0 || height == 0) {
    return false;
  }

  const texture_util::TextureGuestLayout guest_layout =
      texture_util::GetGuestTextureLayout(
          fetch.dimension, fetch.pitch, width, height, 1, false,
          fetch.format, fetch.packed_mips != 0, true, 0);
  if (guest_layout.base.row_pitch_bytes == 0) {
    return false;
  }

  const uint32_t row_pitch_bytes = static_cast<uint32_t>(
      ((uint64_t(width) + kTextureUploadRowAlignment - 1u) /
       kTextureUploadRowAlignment) *
      kTextureUploadRowAlignment);
  const uint64_t upload_size = uint64_t(row_pitch_bytes) * height;
  if (upload_size == 0 || upload_size > kMaxTextureUploadBufferSize) {
    return false;
  }

  const uint64_t source_end =
      uint64_t(guest_layout.base.row_pitch_bytes) * (height - 1u) + width;
  if (source_end > snapshot.upload_data.size()) {
    return false;
  }

  layout.width = width;
  layout.height = height;
  layout.row_pitch_bytes = row_pitch_bytes;
  layout.guest_row_pitch_bytes =
      std::max<uint32_t>(guest_layout.base.row_pitch_bytes, width);
  layout.upload_size = upload_size;
  return true;
}

bool TryGetR8TiledTextureUploadLayout(
    const RendererTrackedResourceSnapshot& snapshot,
    const PlumeTrackedResource& tracked,
    R8TiledTextureUploadLayout& layout) {
  if (snapshot.kind != RendererTrackedResourceKind::kTexture ||
      snapshot.upload_data_linear || snapshot.upload_data_mip_level != 0 ||
      snapshot.upload_data.empty() ||
      PlumeGuestTextureFormat(tracked) != plume::RenderFormat::R8_UNORM ||
      tracked.texture_view_dimension !=
          plume::RenderTextureViewDimension::TEXTURE_2D ||
      PlumeGuestTextureDepth(tracked) != 1u) {
    return false;
  }

  namespace xenos = rex::graphics::xenos;
  namespace texture_util = rex::graphics::texture_util;
  const std::array<uint32_t, 6> fetch_dwords =
      PreferredTextureFetchDwords(snapshot.texture);
  xenos::xe_gpu_texture_fetch_t fetch = {};
  fetch.dword_0 = fetch_dwords[0];
  fetch.dword_1 = fetch_dwords[1];
  fetch.dword_2 = fetch_dwords[2];
  fetch.dword_3 = fetch_dwords[3];
  fetch.dword_4 = fetch_dwords[4];
  fetch.dword_5 = fetch_dwords[5];
  if (fetch.type != xenos::FetchConstantType::kTexture ||
      fetch.dimension == xenos::DataDimension::k3D || !fetch.tiled) {
    return false;
  }

  const rex::graphics::FormatInfo* guest_format_info =
      rex::graphics::FormatInfo::Get(fetch.format);
  if (!guest_format_info || guest_format_info->bytes_per_block() != 1u ||
      guest_format_info->block_width != 1u ||
      guest_format_info->block_height != 1u) {
    return false;
  }

  const uint32_t width = PlumeGuestTextureWidth(tracked);
  const uint32_t height = PlumeGuestTextureHeight(tracked);
  if (width == 0 || height == 0) {
    return false;
  }

  const texture_util::TextureGuestLayout guest_layout =
      texture_util::GetGuestTextureLayout(
          fetch.dimension, fetch.pitch, width, height, 1, true,
          fetch.format, fetch.packed_mips != 0, true, 0);
  if (guest_layout.base.row_pitch_bytes == 0) {
    return false;
  }

  const uint32_t row_pitch_bytes = static_cast<uint32_t>(
      ((uint64_t(width) + kTextureUploadRowAlignment - 1u) /
       kTextureUploadRowAlignment) *
      kTextureUploadRowAlignment);
  const uint64_t upload_size = uint64_t(row_pitch_bytes) * height;
  if (upload_size == 0 || upload_size > kMaxTextureUploadBufferSize) {
    return false;
  }

  layout.width = width;
  layout.height = height;
  layout.row_pitch_bytes = row_pitch_bytes;
  layout.guest_pitch_blocks =
      std::max<uint32_t>(guest_layout.base.row_pitch_bytes, width);
  layout.upload_size = upload_size;
  return true;
}

bool TryStageR8TiledTextureUploadInFrameSlab(
    plume::RenderDevice* device,
    const RendererTrackedResourceSnapshot& snapshot,
    PlumeTrackedResource& tracked,
    const R8TiledTextureUploadLayout& layout,
    R8TiledUploadTiming* timing = nullptr) {
  const TextureUploadClock::time_point runs_begin =
      TextureUploadClock::now();
  const Tiled2DR8CopyRunTable& copy_runs =
      Tiled2DR8CopyRuns(layout.width, layout.height,
                        layout.guest_pitch_blocks, layout.row_pitch_bytes);
  const TextureUploadClock::time_point runs_end = TextureUploadClock::now();
  if (timing) {
    timing->runs_ms += TextureUploadElapsedMs(runs_begin, runs_end);
    timing->run_count = copy_runs.runs.size();
  }
  if (copy_runs.runs.empty() || copy_runs.source_end > snapshot.upload_data.size()) {
    return false;
  }

  const uint8_t* source = snapshot.upload_data.data();
  const bool has_row_padding = layout.row_pitch_bytes != layout.width;
  FrameSlabWriteTiming slab_timing;
  return TryWriteTextureUploadInFrameSlab(
      device, tracked, static_cast<size_t>(layout.upload_size),
      [&](uint8_t* upload_target, size_t upload_size) {
        if (has_row_padding) {
          const TextureUploadClock::time_point clear_begin =
              TextureUploadClock::now();
          std::memset(upload_target, 0, upload_size);
          const TextureUploadClock::time_point clear_end =
              TextureUploadClock::now();
          if (timing) {
            timing->clear_ms += TextureUploadElapsedMs(clear_begin, clear_end);
          }
        }
        const TextureUploadClock::time_point copy_begin =
            TextureUploadClock::now();
        for (const Tiled2DR8CopyRun& run : copy_runs.runs) {
          CopyR8TiledRun(upload_target + run.target_offset,
                         source + run.source_offset, run.byte_count);
        }
        const TextureUploadClock::time_point copy_end =
            TextureUploadClock::now();
        if (timing) {
          timing->copy_ms += TextureUploadElapsedMs(copy_begin, copy_end);
        }
        const bool update_debug_sample =
            layout.width < 640u || layout.height < 360u;
        if (update_debug_sample) {
          const TextureUploadClock::time_point debug_begin =
              TextureUploadClock::now();
          UpdateTrackedUploadDebugSample(tracked, upload_target, upload_size);
          const TextureUploadClock::time_point debug_end =
              TextureUploadClock::now();
          if (timing) {
            timing->debug_ms += TextureUploadElapsedMs(debug_begin, debug_end);
          }
        }
        return true;
      },
      timing ? &slab_timing : nullptr) &&
         (!timing ||
         (timing->slab_map_ms += slab_timing.map_ms,
           timing->slab_writer_ms += slab_timing.writer_ms,
           timing->slab_unmap_ms += slab_timing.unmap_ms, true));
}

bool TryStageR8LinearTextureUploadInFrameSlab(
    plume::RenderDevice* device,
    const RendererTrackedResourceSnapshot& snapshot,
    PlumeTrackedResource& tracked,
    const R8LinearTextureUploadLayout& layout,
    R8TiledUploadTiming* timing = nullptr) {
  const uint8_t* source = snapshot.upload_data.data();
  const bool direct_copy =
      layout.row_pitch_bytes == layout.guest_row_pitch_bytes &&
      layout.upload_size <= snapshot.upload_data.size();
  FrameSlabWriteTiming slab_timing;
  return TryWriteTextureUploadInFrameSlab(
      device, tracked, static_cast<size_t>(layout.upload_size),
      [&](uint8_t* upload_target, size_t upload_size) {
        if (!direct_copy && layout.row_pitch_bytes != layout.width) {
          const TextureUploadClock::time_point clear_begin =
              TextureUploadClock::now();
          std::memset(upload_target, 0, upload_size);
          const TextureUploadClock::time_point clear_end =
              TextureUploadClock::now();
          if (timing) {
            timing->clear_ms += TextureUploadElapsedMs(clear_begin, clear_end);
          }
        }

        const TextureUploadClock::time_point copy_begin =
            TextureUploadClock::now();
        if (direct_copy) {
          std::memcpy(upload_target, source, static_cast<size_t>(layout.upload_size));
        } else {
          for (uint32_t y = 0; y < layout.height; ++y) {
            std::memcpy(upload_target + uint64_t(y) * layout.row_pitch_bytes,
                        source + uint64_t(y) * layout.guest_row_pitch_bytes,
                        layout.width);
          }
        }
        const TextureUploadClock::time_point copy_end =
            TextureUploadClock::now();
        if (timing) {
          timing->copy_ms += TextureUploadElapsedMs(copy_begin, copy_end);
          timing->run_count = layout.height;
        }

        const bool update_debug_sample =
            layout.width < 640u || layout.height < 360u;
        if (update_debug_sample) {
          const TextureUploadClock::time_point debug_begin =
              TextureUploadClock::now();
          UpdateTrackedUploadDebugSample(tracked, upload_target, upload_size);
          const TextureUploadClock::time_point debug_end =
              TextureUploadClock::now();
          if (timing) {
            timing->debug_ms += TextureUploadElapsedMs(debug_begin, debug_end);
          }
        }
        return true;
      },
      timing ? &slab_timing : nullptr) &&
         (!timing ||
          (timing->slab_map_ms += slab_timing.map_ms,
           timing->slab_writer_ms += slab_timing.writer_ms,
           timing->slab_unmap_ms += slab_timing.unmap_ms, true));
}

bool TryStageDxt3aR8TextureUploadInFrameSlab(
    plume::RenderDevice* device,
    const RendererTrackedResourceSnapshot& snapshot,
    PlumeTrackedResource& tracked,
    const Dxt3aR8TextureUploadLayout& layout,
    R8TiledUploadTiming* timing = nullptr) {
  namespace texture_conversion = rex::graphics::texture_conversion;
  const uint8_t* source = snapshot.upload_data.data();
  const size_t source_size = snapshot.upload_data.size();
  const std::vector<uint32_t>* tiled_offsets = nullptr;
  if (layout.tiled) {
    const TextureUploadClock::time_point runs_begin =
        TextureUploadClock::now();
    tiled_offsets = &Tiled2DOffsetTable(layout.width_blocks,
                                        layout.height_blocks,
                                        layout.guest_pitch_blocks, 3u);
    const TextureUploadClock::time_point runs_end = TextureUploadClock::now();
    if (timing) {
      timing->runs_ms += TextureUploadElapsedMs(runs_begin, runs_end);
      timing->run_count = tiled_offsets->size();
    }
  } else if (timing) {
    timing->run_count =
        static_cast<size_t>(layout.width_blocks) * layout.height_blocks;
  }

  FrameSlabWriteTiming slab_timing;
  return TryWriteTextureUploadInFrameSlab(
      device, tracked, static_cast<size_t>(layout.upload_size),
      [&](uint8_t* upload_target, size_t upload_size) {
        const TextureUploadClock::time_point clear_begin =
            TextureUploadClock::now();
        std::memset(upload_target, 0, upload_size);
        const TextureUploadClock::time_point clear_end =
            TextureUploadClock::now();
        if (timing) {
          timing->clear_ms += TextureUploadElapsedMs(clear_begin, clear_end);
        }

        const TextureUploadClock::time_point copy_begin =
            TextureUploadClock::now();
        for (uint32_t block_y = 0; block_y < layout.height_blocks; ++block_y) {
          for (uint32_t block_x = 0; block_x < layout.width_blocks; ++block_x) {
            uint64_t source_offset = 0;
            if (tiled_offsets) {
              const size_t offset_index =
                  static_cast<size_t>(block_y) * layout.width_blocks + block_x;
              if (offset_index >= tiled_offsets->size()) {
                continue;
              }
              source_offset = (*tiled_offsets)[offset_index];
            } else {
              source_offset = (uint64_t(block_y) * layout.guest_pitch_blocks +
                               block_x) *
                              8u;
            }
            if (source_offset + 8u > source_size) {
              continue;
            }

            uint8_t block[8] = {};
            texture_conversion::CopySwapBlock(layout.endianness, block,
                                              source + source_offset, 8u);
            for (uint32_t local_y = 0; local_y < 4u; ++local_y) {
              const uint32_t y = block_y * 4u + local_y;
              if (y >= layout.height) {
                continue;
              }
              uint8_t* target_row =
                  upload_target + uint64_t(y) * layout.row_pitch_bytes;
              for (uint32_t local_x = 0; local_x < 4u; ++local_x) {
                const uint32_t x = block_x * 4u + local_x;
                if (x >= layout.width) {
                  continue;
                }
                const uint32_t texel = local_y * 4u + local_x;
                const uint8_t packed_alpha =
                    block[texel >> 1u] >> ((texel & 1u) * 4u);
                const uint8_t alpha4 = packed_alpha & 0xFu;
                target_row[x] = static_cast<uint8_t>((alpha4 << 4u) | alpha4);
              }
            }
          }
        }
        const TextureUploadClock::time_point copy_end =
            TextureUploadClock::now();
        if (timing) {
          timing->copy_ms += TextureUploadElapsedMs(copy_begin, copy_end);
        }

        const TextureUploadClock::time_point debug_begin =
            TextureUploadClock::now();
        UpdateTrackedUploadDebugSample(tracked, upload_target, upload_size);
        const TextureUploadClock::time_point debug_end =
            TextureUploadClock::now();
        if (timing) {
          timing->debug_ms += TextureUploadElapsedMs(debug_begin, debug_end);
        }
        return true;
      },
      timing ? &slab_timing : nullptr) &&
         (!timing ||
          (timing->slab_map_ms += slab_timing.map_ms,
           timing->slab_writer_ms += slab_timing.writer_ms,
           timing->slab_unmap_ms += slab_timing.unmap_ms, true));
}

PlumeTextureUploadDecodeCacheKey TextureUploadDecodeCacheKeyForTrackedResource(
    const PlumeTrackedResource& tracked, uint64_t source_hash,
    size_t source_size) {
  return PlumeTextureUploadDecodeCacheKey{
      .source_hash = source_hash,
      .source_size = static_cast<uint64_t>(source_size),
      .format = static_cast<uint32_t>(PlumeGuestTextureFormat(tracked)),
      .width = PlumeGuestTextureWidth(tracked),
      .height = PlumeGuestTextureHeight(tracked),
      .depth = PlumeGuestTextureDepth(tracked),
      .view_dimension = static_cast<uint32_t>(tracked.texture_view_dimension),
      .guest_format = PlumeGuestTextureGuestFormat(tracked),
      .guest_dimension = PlumeGuestTextureDimension(tracked),
      .guest_pitch = PlumeGuestTexturePitch(tracked),
      .fetch_dwords = PlumeGuestTextureFetchDwords(tracked),
  };
}

void ApplyTextureUploadDecodeCacheEntry(
    PlumeTrackedResource& tracked,
    const PlumeTextureUploadDecodeCacheEntry& entry) {
  tracked.texture_upload_published = false;
  tracked.texture_upload_format = PlumeGuestTextureFormat(tracked);
  tracked.texture_upload_row_width = entry.row_width;
  tracked.texture_upload_subresources = entry.subresources;
  tracked.texture_upload_subresource_bytes = entry.subresource_bytes;
  tracked.texture_upload_size = entry.upload_size;
  tracked.texture_upload_decoded = entry.decoded;
  tracked.texture_upload_has_source = entry.has_source;
}

PlumeTextureUploadDecodeCacheEntry TextureUploadDecodeCacheEntryForTrackedResource(
    const PlumeTrackedResource& tracked, std::vector<uint8_t>&& upload_bytes,
    uint64_t upload_hash) {
  return PlumeTextureUploadDecodeCacheEntry{
      .bytes = std::move(upload_bytes),
      .upload_hash = upload_hash,
      .row_width = tracked.texture_upload_row_width,
      .subresources = tracked.texture_upload_subresources,
      .subresource_bytes = tracked.texture_upload_subresource_bytes,
      .upload_size = tracked.texture_upload_size,
      .decoded = tracked.texture_upload_decoded,
      .has_source = tracked.texture_upload_has_source,
  };
}
}

uint64_t AlignUp(uint64_t value, uint64_t alignment) {
  return alignment == 0 ? value : ((value + alignment - 1u) / alignment) *
                                  alignment;
}

bool IsPowerOfTwo(uint32_t value) {
  return value != 0 && (value & (value - 1u)) == 0;
}

bool DecodeGuestTextureBaseLevel(const RendererTrackedResourceSnapshot& snapshot,
                                 const PlumeTrackedResource& tracked,
                                 std::vector<uint8_t>& decoded_bytes) {
  decoded_bytes.clear();
  if (snapshot.kind != RendererTrackedResourceKind::kTexture ||
      snapshot.upload_data.empty() || PlumeGuestTextureWidth(tracked) == 0 ||
      PlumeGuestTextureHeight(tracked) == 0 || PlumeGuestTextureDepth(tracked) == 0) {
    return false;
  }

  namespace xenos = rex::graphics::xenos;
  namespace texture_conversion = rex::graphics::texture_conversion;
  namespace texture_util = rex::graphics::texture_util;

  const std::array<uint32_t, 6> fetch_dwords =
      PreferredTextureFetchDwords(snapshot.texture);

  xenos::xe_gpu_texture_fetch_t fetch = {};
  fetch.dword_0 = fetch_dwords[0];
  fetch.dword_1 = fetch_dwords[1];
  fetch.dword_2 = fetch_dwords[2];
  fetch.dword_3 = fetch_dwords[3];
  fetch.dword_4 = fetch_dwords[4];
  fetch.dword_5 = fetch_dwords[5];
  // kTexture ONLY. kInvalidTexture (=0) is an UNWRITTEN slot, not a texture —
  // the guest tags every real texture at creation (D3D_SetTextureHeader
  // 0x821FB650: `ori r7, r7, 2`). This file's other fetch decodes already gate
  // on == kTexture; this one did not, which is how a vertex-stream slot's
  // stale dwords reached the layout code. Same fix as
  // plume_texture_metadata.cpp — see
  // [[refii-fetch-slots-are-shared-vertex-and-texture]].
  if (fetch.type != xenos::FetchConstantType::kTexture) {
    return false;
  }

  // Xenos stores 1D textures linearly; hardware ignores the fetch-constant
  // tiled bit for them, and GetGuestTextureLayout asserts on the combination.
  //
  // The SAME is true of packed mip levels, and omitting it aborted the process
  // (run 586, core dump, frame 18149):
  //     Assertion `!(has_packed_levels)' failed
  //     texture/util.cpp:214  GetGuestTextureLayout(
  //         dimension=k1D, width_texels=4096, height_texels=1,
  //         format=k_1_5_5_5, has_packed_levels=true)
  // The SDK states both preconditions together, and for the same reason —
  // `GetPackedMipOffset may result in packing along Y for width > height
  // textures`, which a height-1 texture cannot express:
  //     if (dimension == k1D) { assert_false(is_tiled);
  //                             assert_false(has_packed_levels); }
  // So clear the bit exactly as the tiled bit is cleared above, rather than
  // passing a raw descriptor bit into a function whose stated contract
  // forbids it.
  //
  // Note the width in that abort was 4096 — ClampGuestTextureExtent's ceiling,
  // i.e. a laundered garbage extent. The 1D fetch field is 24 bits, so a
  // non-texture descriptor decodes to millions and clamps to exactly 4096.
  // This guard keeps the SDK from asserting on that residue; it is not a
  // licence to upload it, and the allocation gate still rejects the image.
  if (fetch.dimension == xenos::DataDimension::k1D) {
    fetch.tiled = 0;
    fetch.packed_mips = 0;
  }
  // Linear-tagged captures were taken from a CPU-tile call's LINEAR source:
  // the bytes are already untiled even though the fetch constant says tiled
  // (endianness and format decode still apply as usual).
  if (snapshot.upload_data_linear) {
    fetch.tiled = 0;
  }
  // Split-mip captures hold the mip-chain image; Xenos mips use the same
  // pow2/tile alignment as standalone bases, so describe the fetch as a
  // single-level texture at the level's dimensions and derived pitch.
  if (snapshot.upload_data_mip_level > 0 &&
      fetch.dimension == xenos::DataDimension::k2DOrStacked) {
    const uint32_t level = std::min<uint32_t>(snapshot.upload_data_mip_level, 12u);
    const uint32_t level_width =
        std::max<uint32_t>(1u, (fetch.size_2d.width + 1u) >> level);
    const uint32_t level_height =
        std::max<uint32_t>(1u, (fetch.size_2d.height + 1u) >> level);
    fetch.size_2d.width = level_width - 1u;
    fetch.size_2d.height = level_height - 1u;
    fetch.pitch = (level_width + 31u) / 32u;
    fetch.mip_address = 0;
    fetch.mip_min_level = 0;
    fetch.mip_max_level = 0;
  }

  auto decode_packed_yuv = [&]() -> bool {
    const bool cr_y1_cb_y0 =
        fetch.format == xenos::TextureFormat::k_Cr_Y1_Cb_Y0_REP;
    const bool y1_cr_y0_cb =
        fetch.format == xenos::TextureFormat::k_Y1_Cr_Y0_Cb_REP;
    if (!cr_y1_cb_y0 && !y1_cr_y0_cb) {
      return false;
    }
    if (PlumeGuestTextureFormat(tracked) != plume::RenderFormat::R8G8B8A8_UNORM ||
        PlumeGuestTextureWidth(tracked) == 0 || PlumeGuestTextureHeight(tracked) == 0 ||
        PlumeGuestTextureDepth(tracked) != 1 ||
        tracked.texture_view_dimension !=
            plume::RenderTextureViewDimension::TEXTURE_2D) {
      return false;
    }

    const rex::graphics::FormatInfo* guest_format_info =
        rex::graphics::FormatInfo::Get(fetch.format);
    if (!guest_format_info || guest_format_info->bytes_per_block() != 4 ||
        guest_format_info->block_width != 2 ||
        guest_format_info->block_height != 1) {
      return false;
    }

    const uint32_t width_blocks =
        std::max<uint32_t>(1, (PlumeGuestTextureWidth(tracked) + 1u) / 2u);
    const uint32_t height_blocks = PlumeGuestTextureHeight(tracked);
    const uint64_t decoded_size =
        uint64_t(PlumeGuestTextureWidth(tracked)) * PlumeGuestTextureHeight(tracked) * 4u;
    if (decoded_size == 0 || decoded_size > kMaxTextureUploadBufferSize) {
      return false;
    }

    const texture_util::TextureGuestLayout layout =
        texture_util::GetGuestTextureLayout(
            fetch.dimension, fetch.pitch, PlumeGuestTextureWidth(tracked),
            PlumeGuestTextureHeight(tracked), 1, fetch.tiled != 0, fetch.format,
            fetch.packed_mips != 0, true, 0);
    if (layout.base.row_pitch_bytes == 0 ||
        layout.base.array_slice_stride_bytes == 0) {
      return false;
    }

    uint32_t guest_pitch_blocks =
        layout.base.row_pitch_bytes / guest_format_info->bytes_per_block();
    guest_pitch_blocks = std::max<uint32_t>(guest_pitch_blocks, width_blocks);
    constexpr uint32_t kGuestBytesPerBlock = 4;
    constexpr uint32_t kGuestBytesPerBlockLog2 = 2;

    decoded_bytes.assign(static_cast<size_t>(decoded_size), 0xFF);
    const uint8_t* source = snapshot.upload_data.data();
    const size_t source_size = snapshot.upload_data.size();
    for (uint32_t y = 0; y < height_blocks; ++y) {
      for (uint32_t block_x = 0; block_x < width_blocks; ++block_x) {
        uint64_t source_offset = 0;
        if (fetch.tiled) {
          source_offset = static_cast<uint32_t>(
              texture_util::GetTiledOffset2D(
                  static_cast<int32_t>(block_x), static_cast<int32_t>(y),
                  guest_pitch_blocks, kGuestBytesPerBlockLog2));
        } else {
          source_offset = uint64_t(y) * layout.base.row_pitch_bytes +
                          uint64_t(block_x) * kGuestBytesPerBlock;
        }
        if (source_offset + kGuestBytesPerBlock > source_size) {
          continue;
        }

        uint8_t block[4] = {};
        texture_conversion::CopySwapBlock(fetch.endianness, block,
                                          source + source_offset,
                                          kGuestBytesPerBlock);
        uint8_t cr = 0;
        uint8_t cb = 0;
        uint8_t y0 = 0;
        uint8_t y1 = 0;
        if (cr_y1_cb_y0) {
          cr = block[0];
          y1 = block[1];
          cb = block[2];
          y0 = block[3];
        } else {
          y1 = block[0];
          cr = block[1];
          y0 = block[2];
          cb = block[3];
        }

        const uint32_t pixel_x = block_x * 2u;
        const auto write_pixel = [&](uint32_t x, uint8_t y_value) {
          if (x >= PlumeGuestTextureWidth(tracked)) {
            return;
          }
          const uint64_t decoded_offset =
              (uint64_t(y) * PlumeGuestTextureWidth(tracked) + x) * 4u;
          if (decoded_offset + 4u > decoded_size) {
            return;
          }
          decoded_bytes[decoded_offset + 0u] = y_value;
          decoded_bytes[decoded_offset + 1u] = cr;
          decoded_bytes[decoded_offset + 2u] = cb;
          decoded_bytes[decoded_offset + 3u] = 0xFFu;
        };
        write_pixel(pixel_x + 0u, y0);
        write_pixel(pixel_x + 1u, y1);
      }
    }
    return true;
  };

  if (decode_packed_yuv()) {
    return true;
  }

  const rex::graphics::FormatInfo* guest_format_info =
      rex::graphics::FormatInfo::Get(fetch.format);
  if (!guest_format_info) {
    return false;
  }

  auto decode_packed_depth_to_r32_float = [&]() -> bool {
    if (PlumeGuestTextureFormat(tracked) != plume::RenderFormat::R32_FLOAT ||
        guest_format_info->bytes_per_block() != 4 ||
        guest_format_info->block_width != 1 ||
        guest_format_info->block_height != 1 ||
        !IsPackedDepthXenosTextureFormat(fetch.format)) {
      return false;
    }

    const uint32_t width = PlumeGuestTextureWidth(tracked);
    const uint32_t height = PlumeGuestTextureHeight(tracked);
    const uint32_t depth = std::max<uint32_t>(1, PlumeGuestTextureDepth(tracked));
    const uint32_t subresource_count =
        tracked.texture_view_dimension ==
                plume::RenderTextureViewDimension::TEXTURE_CUBE
            ? 6u
            : 1u;
    const uint64_t row_bytes = uint64_t(width) * sizeof(float);
    const uint64_t tight_subresource_bytes = row_bytes * height * depth;
    const uint64_t decoded_size = tight_subresource_bytes * subresource_count;
    if (decoded_size == 0 || decoded_size > kMaxTextureUploadBufferSize) {
      return false;
    }

    const uint32_t depth_or_array_size =
        fetch.dimension == xenos::DataDimension::kCube
            ? 6u
            : (fetch.dimension == xenos::DataDimension::k3D ? depth : 1u);
    const texture_util::TextureGuestLayout layout =
        texture_util::GetGuestTextureLayout(
            fetch.dimension, fetch.pitch, width, height, depth_or_array_size,
            fetch.tiled != 0, fetch.format, fetch.packed_mips != 0, true, 0);
    if (layout.base.row_pitch_bytes == 0 ||
        layout.base.array_slice_stride_bytes == 0) {
      return false;
    }

    constexpr uint32_t kGuestBytesPerBlock = 4u;
    constexpr uint32_t kGuestBytesPerBlockLog2 = 2u;
    uint32_t guest_pitch_blocks =
        layout.base.row_pitch_bytes / kGuestBytesPerBlock;
    guest_pitch_blocks = std::max<uint32_t>(guest_pitch_blocks, width);
    const uint32_t guest_z_slice_stride_bytes =
        layout.base.row_pitch_bytes *
        std::max<uint32_t>(1, layout.base.z_slice_stride_block_rows);

    decoded_bytes.assign(static_cast<size_t>(decoded_size), 0);
    const uint8_t* source = snapshot.upload_data.data();
    const size_t source_size = snapshot.upload_data.size();
    for (uint32_t subresource = 0; subresource < subresource_count;
         ++subresource) {
      const uint64_t source_subresource_base =
          uint64_t(subresource) * layout.base.array_slice_stride_bytes;
      const uint64_t decoded_subresource_base =
          uint64_t(subresource) * tight_subresource_bytes;
      if (source_subresource_base >= source_size ||
          decoded_subresource_base >= decoded_size) {
        continue;
      }

      for (uint32_t z = 0; z < depth; ++z) {
        for (uint32_t y = 0; y < height; ++y) {
          for (uint32_t x = 0; x < width; ++x) {
            uint64_t source_offset = source_subresource_base;
            if (fetch.tiled) {
              if (fetch.dimension == xenos::DataDimension::k3D) {
                source_offset += static_cast<uint32_t>(
                    texture_util::GetTiledOffset3D(
                        static_cast<int32_t>(x), static_cast<int32_t>(y),
                        static_cast<int32_t>(z), guest_pitch_blocks,
                        std::max<uint32_t>(
                            height, layout.base.z_slice_stride_block_rows),
                        kGuestBytesPerBlockLog2));
              } else {
                source_offset += static_cast<uint32_t>(
                    texture_util::GetTiledOffset2D(
                        static_cast<int32_t>(x), static_cast<int32_t>(y),
                        guest_pitch_blocks, kGuestBytesPerBlockLog2));
              }
            } else {
              source_offset += uint64_t(z) * guest_z_slice_stride_bytes +
                               uint64_t(y) * layout.base.row_pitch_bytes +
                               uint64_t(x) * kGuestBytesPerBlock;
            }

            const uint64_t decoded_offset =
                decoded_subresource_base +
                (uint64_t(z) * height + y) * row_bytes +
                uint64_t(x) * sizeof(float);
            if (source_offset + kGuestBytesPerBlock > source_size ||
                decoded_offset + sizeof(float) > decoded_size) {
              continue;
            }

            uint8_t block[kGuestBytesPerBlock] = {};
            texture_conversion::CopySwapBlock(fetch.endianness, block,
                                              source + source_offset,
                                              kGuestBytesPerBlock);
            const uint32_t texel =
                uint32_t(block[0]) | (uint32_t(block[1]) << 8u) |
                (uint32_t(block[2]) << 16u) | (uint32_t(block[3]) << 24u);
            float decoded_depth = 0.0f;
            if (DecodePackedDepthXenosTexel(fetch.format, texel,
                                            &decoded_depth)) {
              std::memcpy(decoded_bytes.data() + decoded_offset,
                          &decoded_depth, sizeof(decoded_depth));
            }
          }
        }
      }
    }
    return true;
  };

  if (decode_packed_depth_to_r32_float()) {
    return true;
  }

  auto decode_packed_16_to_rgba8 = [&]() -> bool {
    if (PlumeGuestTextureFormat(tracked) != plume::RenderFormat::R8G8B8A8_UNORM ||
        guest_format_info->bytes_per_block() != 2 ||
        guest_format_info->block_width != 1 ||
        guest_format_info->block_height != 1 ||
        !IsPacked16XenosTextureFormat(fetch.format)) {
      return false;
    }

    const uint32_t width = PlumeGuestTextureWidth(tracked);
    const uint32_t height = PlumeGuestTextureHeight(tracked);
    const uint32_t depth = std::max<uint32_t>(1, PlumeGuestTextureDepth(tracked));
    const uint32_t subresource_count =
        tracked.texture_view_dimension ==
                plume::RenderTextureViewDimension::TEXTURE_CUBE
            ? 6u
            : 1u;
    const uint64_t row_bytes = uint64_t(width) * 4u;
    const uint64_t tight_subresource_bytes = row_bytes * height * depth;
    const uint64_t decoded_size = tight_subresource_bytes * subresource_count;
    if (decoded_size == 0 || decoded_size > kMaxTextureUploadBufferSize) {
      return false;
    }

    const uint32_t depth_or_array_size =
        fetch.dimension == xenos::DataDimension::kCube
            ? 6u
            : (fetch.dimension == xenos::DataDimension::k3D ? depth : 1u);
    const texture_util::TextureGuestLayout layout =
        texture_util::GetGuestTextureLayout(
            fetch.dimension, fetch.pitch, width, height, depth_or_array_size,
            fetch.tiled != 0, fetch.format, fetch.packed_mips != 0, true, 0);
    if (layout.base.row_pitch_bytes == 0 ||
        layout.base.array_slice_stride_bytes == 0) {
      return false;
    }

    constexpr uint32_t kGuestBytesPerBlock = 2u;
    constexpr uint32_t kGuestBytesPerBlockLog2 = 1u;
    uint32_t guest_pitch_blocks =
        layout.base.row_pitch_bytes / kGuestBytesPerBlock;
    guest_pitch_blocks = std::max<uint32_t>(guest_pitch_blocks, width);
    const uint32_t guest_z_slice_stride_bytes =
        layout.base.row_pitch_bytes *
        std::max<uint32_t>(1, layout.base.z_slice_stride_block_rows);

    decoded_bytes.assign(static_cast<size_t>(decoded_size), 0xFF);
    const uint8_t* source = snapshot.upload_data.data();
    const size_t source_size = snapshot.upload_data.size();
    for (uint32_t subresource = 0; subresource < subresource_count;
         ++subresource) {
      const uint64_t source_subresource_base =
          uint64_t(subresource) * layout.base.array_slice_stride_bytes;
      const uint64_t decoded_subresource_base =
          uint64_t(subresource) * tight_subresource_bytes;
      if (source_subresource_base >= source_size ||
          decoded_subresource_base >= decoded_size) {
        continue;
      }

      for (uint32_t z = 0; z < depth; ++z) {
        for (uint32_t y = 0; y < height; ++y) {
          for (uint32_t x = 0; x < width; ++x) {
            uint64_t source_offset = source_subresource_base;
            if (fetch.tiled) {
              if (fetch.dimension == xenos::DataDimension::k3D) {
                source_offset += static_cast<uint32_t>(
                    texture_util::GetTiledOffset3D(
                        static_cast<int32_t>(x), static_cast<int32_t>(y),
                        static_cast<int32_t>(z), guest_pitch_blocks,
                        std::max<uint32_t>(
                            height, layout.base.z_slice_stride_block_rows),
                        kGuestBytesPerBlockLog2));
              } else {
                source_offset += static_cast<uint32_t>(
                    texture_util::GetTiledOffset2D(
                        static_cast<int32_t>(x), static_cast<int32_t>(y),
                        guest_pitch_blocks, kGuestBytesPerBlockLog2));
              }
            } else {
              source_offset += uint64_t(z) * guest_z_slice_stride_bytes +
                               uint64_t(y) * layout.base.row_pitch_bytes +
                               uint64_t(x) * kGuestBytesPerBlock;
            }

            const uint64_t decoded_offset =
                decoded_subresource_base +
                (uint64_t(z) * height + y) * row_bytes + uint64_t(x) * 4u;
            if (source_offset + kGuestBytesPerBlock > source_size ||
                decoded_offset + 4u > decoded_size) {
              continue;
            }
            uint8_t block[2] = {};
            texture_conversion::CopySwapBlock(fetch.endianness, block,
                                              source + source_offset,
                                              kGuestBytesPerBlock);
            const uint16_t texel =
                static_cast<uint16_t>(block[0]) |
                (static_cast<uint16_t>(block[1]) << 8u);
            DecodePacked16XenosTexel(fetch.format, texel,
                                     decoded_bytes.data() + decoded_offset);
          }
        }
      }
    }
    return true;
  };

  if (decode_packed_16_to_rgba8()) {
    return true;
  }

  auto decode_packed_10_11_11_to_rgba16 = [&]() -> bool {
    const bool r11g11b10 =
        fetch.format == xenos::TextureFormat::k_10_11_11 ||
        fetch.format == xenos::TextureFormat::k_10_11_11_AS_16_16_16_16;
    const bool r10g11b11 =
        fetch.format == xenos::TextureFormat::k_11_11_10 ||
        fetch.format == xenos::TextureFormat::k_11_11_10_AS_16_16_16_16;
    if ((!r11g11b10 && !r10g11b11) ||
        PlumeGuestTextureFormat(tracked) !=
            plume::RenderFormat::R16G16B16A16_UNORM ||
        guest_format_info->bytes_per_block() != 4 ||
        guest_format_info->block_width != 1 ||
        guest_format_info->block_height != 1) {
      return false;
    }

    const uint32_t width = PlumeGuestTextureWidth(tracked);
    const uint32_t height = PlumeGuestTextureHeight(tracked);
    const uint32_t depth = std::max<uint32_t>(1, PlumeGuestTextureDepth(tracked));
    const uint32_t subresource_count =
        tracked.texture_view_dimension ==
                plume::RenderTextureViewDimension::TEXTURE_CUBE
            ? 6u
            : 1u;
    const uint64_t row_bytes = uint64_t(width) * 8u;
    const uint64_t tight_subresource_bytes = row_bytes * height * depth;
    const uint64_t decoded_size = tight_subresource_bytes * subresource_count;
    if (decoded_size == 0 || decoded_size > kMaxTextureUploadBufferSize) {
      return false;
    }

    const uint32_t depth_or_array_size =
        fetch.dimension == xenos::DataDimension::kCube
            ? 6u
            : (fetch.dimension == xenos::DataDimension::k3D ? depth : 1u);
    const texture_util::TextureGuestLayout layout =
        texture_util::GetGuestTextureLayout(
            fetch.dimension, fetch.pitch, width, height, depth_or_array_size,
            fetch.tiled != 0, fetch.format, fetch.packed_mips != 0, true, 0);
    if (layout.base.row_pitch_bytes == 0 ||
        layout.base.array_slice_stride_bytes == 0) {
      return false;
    }

    constexpr uint32_t kGuestBytesPerBlock = 4u;
    constexpr uint32_t kGuestBytesPerBlockLog2 = 2u;
    uint32_t guest_pitch_blocks =
        layout.base.row_pitch_bytes / kGuestBytesPerBlock;
    guest_pitch_blocks = std::max<uint32_t>(guest_pitch_blocks, width);
    const uint32_t guest_z_slice_stride_bytes =
        layout.base.row_pitch_bytes *
        std::max<uint32_t>(1, layout.base.z_slice_stride_block_rows);

    const auto expand_unorm_to_u16 = [](uint32_t value,
                                        uint32_t bits) -> uint16_t {
      const uint32_t max_value = (1u << bits) - 1u;
      return static_cast<uint16_t>(
          (value * 65535u + max_value / 2u) / max_value);
    };
    const auto write_u16 = [](uint8_t* target, uint16_t value) {
      target[0] = static_cast<uint8_t>(value & 0xFFu);
      target[1] = static_cast<uint8_t>(value >> 8u);
    };

    decoded_bytes.assign(static_cast<size_t>(decoded_size), 0xFF);
    const uint8_t* source = snapshot.upload_data.data();
    const size_t source_size = snapshot.upload_data.size();
    for (uint32_t subresource = 0; subresource < subresource_count;
         ++subresource) {
      const uint64_t source_subresource_base =
          uint64_t(subresource) * layout.base.array_slice_stride_bytes;
      const uint64_t decoded_subresource_base =
          uint64_t(subresource) * tight_subresource_bytes;
      if (source_subresource_base >= source_size ||
          decoded_subresource_base >= decoded_size) {
        continue;
      }

      for (uint32_t z = 0; z < depth; ++z) {
        for (uint32_t y = 0; y < height; ++y) {
          for (uint32_t x = 0; x < width; ++x) {
            uint64_t source_offset = source_subresource_base;
            if (fetch.tiled) {
              if (fetch.dimension == xenos::DataDimension::k3D) {
                source_offset += static_cast<uint32_t>(
                    texture_util::GetTiledOffset3D(
                        static_cast<int32_t>(x), static_cast<int32_t>(y),
                        static_cast<int32_t>(z), guest_pitch_blocks,
                        std::max<uint32_t>(
                            height, layout.base.z_slice_stride_block_rows),
                        kGuestBytesPerBlockLog2));
              } else {
                source_offset += static_cast<uint32_t>(
                    texture_util::GetTiledOffset2D(
                        static_cast<int32_t>(x), static_cast<int32_t>(y),
                        guest_pitch_blocks, kGuestBytesPerBlockLog2));
              }
            } else {
              source_offset += uint64_t(z) * guest_z_slice_stride_bytes +
                               uint64_t(y) * layout.base.row_pitch_bytes +
                               uint64_t(x) * kGuestBytesPerBlock;
            }

            const uint64_t decoded_offset =
                decoded_subresource_base +
                (uint64_t(z) * height + y) * row_bytes + uint64_t(x) * 8u;
            if (source_offset + kGuestBytesPerBlock > source_size ||
                decoded_offset + 8u > decoded_size) {
              continue;
            }

            uint8_t block[4] = {};
            texture_conversion::CopySwapBlock(fetch.endianness, block,
                                              source + source_offset,
                                              kGuestBytesPerBlock);
            const uint32_t texel =
                uint32_t(block[0]) | (uint32_t(block[1]) << 8u) |
                (uint32_t(block[2]) << 16u) | (uint32_t(block[3]) << 24u);
            const uint32_t r_bits = r11g11b10 ? 11u : 10u;
            const uint32_t g_bits = 11u;
            const uint32_t b_bits = r11g11b10 ? 10u : 11u;
            const uint32_t g_shift = r_bits;
            const uint32_t b_shift = r_bits + g_bits;
            const uint16_t r =
                expand_unorm_to_u16(texel & ((1u << r_bits) - 1u), r_bits);
            const uint16_t g = expand_unorm_to_u16(
                (texel >> g_shift) & ((1u << g_bits) - 1u), g_bits);
            const uint16_t b = expand_unorm_to_u16(
                (texel >> b_shift) & ((1u << b_bits) - 1u), b_bits);
            uint8_t* target = decoded_bytes.data() + decoded_offset;
            write_u16(target + 0u, r);
            write_u16(target + 2u, g);
            write_u16(target + 4u, b);
            write_u16(target + 6u, 0xFFFFu);
          }
        }
      }
    }
    return true;
  };

  if (decode_packed_10_11_11_to_rgba16()) {
    return true;
  }

  auto decode_dxt3a_1111_to_rgba8 = [&]() -> bool {
    if (fetch.format != xenos::TextureFormat::k_DXT3A_AS_1_1_1_1 ||
        PlumeGuestTextureFormat(tracked) !=
            plume::RenderFormat::R8G8B8A8_UNORM ||
        guest_format_info->bytes_per_block() != 8 ||
        guest_format_info->block_width != 4 ||
        guest_format_info->block_height != 4 ||
        tracked.texture_view_dimension !=
            plume::RenderTextureViewDimension::TEXTURE_2D) {
      return false;
    }

    const uint32_t width = PlumeGuestTextureWidth(tracked);
    const uint32_t height = PlumeGuestTextureHeight(tracked);
    const uint32_t width_blocks = std::max<uint32_t>(1, (width + 3u) / 4u);
    const uint32_t height_blocks = std::max<uint32_t>(1, (height + 3u) / 4u);
    const uint64_t row_bytes = uint64_t(width) * 4u;
    const uint64_t decoded_size = row_bytes * height;
    if (decoded_size == 0 || decoded_size > kMaxTextureUploadBufferSize) {
      return false;
    }

    const texture_util::TextureGuestLayout layout =
        texture_util::GetGuestTextureLayout(
            fetch.dimension, fetch.pitch, width, height, 1,
            fetch.tiled != 0, fetch.format, fetch.packed_mips != 0, true, 0);
    if (layout.base.row_pitch_bytes == 0 ||
        layout.base.array_slice_stride_bytes == 0) {
      return false;
    }

    constexpr uint32_t kGuestBytesPerBlock = 8u;
    constexpr uint32_t kGuestBytesPerBlockLog2 = 3u;
    uint32_t guest_pitch_blocks =
        layout.base.row_pitch_bytes / kGuestBytesPerBlock;
    guest_pitch_blocks =
        std::max<uint32_t>(guest_pitch_blocks, width_blocks);

    decoded_bytes.assign(static_cast<size_t>(decoded_size), 0);
    const uint8_t* source = snapshot.upload_data.data();
    const size_t source_size = snapshot.upload_data.size();
    for (uint32_t block_y = 0; block_y < height_blocks; ++block_y) {
      for (uint32_t block_x = 0; block_x < width_blocks; ++block_x) {
        uint64_t source_offset = 0;
        if (fetch.tiled) {
          source_offset = static_cast<uint32_t>(
              texture_util::GetTiledOffset2D(
                  static_cast<int32_t>(block_x),
                  static_cast<int32_t>(block_y), guest_pitch_blocks,
                  kGuestBytesPerBlockLog2));
        } else {
          source_offset = uint64_t(block_y) * layout.base.row_pitch_bytes +
                          uint64_t(block_x) * kGuestBytesPerBlock;
        }
        if (source_offset + kGuestBytesPerBlock > source_size) {
          continue;
        }

        uint8_t block[8] = {};
        texture_conversion::CopySwapBlock(fetch.endianness, block,
                                          source + source_offset,
                                          kGuestBytesPerBlock);
        for (uint32_t local_y = 0; local_y < 4u; ++local_y) {
          const uint32_t y = block_y * 4u + local_y;
          if (y >= height) {
            continue;
          }
          for (uint32_t local_x = 0; local_x < 4u; ++local_x) {
            const uint32_t x = block_x * 4u + local_x;
            if (x >= width) {
              continue;
            }
            const uint32_t texel = local_y * 4u + local_x;
            const uint8_t packed =
                (block[texel >> 1u] >> ((texel & 1u) * 4u)) & 0xFu;
            uint8_t* target = decoded_bytes.data() + uint64_t(y) * row_bytes +
                              uint64_t(x) * 4u;
            target[0] = (packed & 0x4u) ? 0xFFu : 0x00u;
            target[1] = (packed & 0x2u) ? 0xFFu : 0x00u;
            target[2] = (packed & 0x1u) ? 0xFFu : 0x00u;
            target[3] = (packed & 0x8u) ? 0xFFu : 0x00u;
          }
        }
      }
    }
    return true;
  };

  if (decode_dxt3a_1111_to_rgba8()) {
    return true;
  }

  auto decode_dxt3a_to_r8 = [&]() -> bool {
    if (fetch.format != xenos::TextureFormat::k_DXT3A ||
        PlumeGuestTextureFormat(tracked) != plume::RenderFormat::R8_UNORM ||
        guest_format_info->bytes_per_block() != 8 ||
        guest_format_info->block_width != 4 ||
        guest_format_info->block_height != 4 ||
        tracked.texture_view_dimension !=
            plume::RenderTextureViewDimension::TEXTURE_2D) {
      return false;
    }

    const uint32_t width = PlumeGuestTextureWidth(tracked);
    const uint32_t height = PlumeGuestTextureHeight(tracked);
    const uint32_t width_blocks = std::max<uint32_t>(1, (width + 3u) / 4u);
    const uint32_t height_blocks = std::max<uint32_t>(1, (height + 3u) / 4u);
    const uint64_t row_bytes = width;
    const uint64_t decoded_size = row_bytes * height;
    if (decoded_size == 0 || decoded_size > kMaxTextureUploadBufferSize) {
      return false;
    }

    const texture_util::TextureGuestLayout layout =
        texture_util::GetGuestTextureLayout(
            fetch.dimension, fetch.pitch, width, height, 1,
            fetch.tiled != 0, fetch.format, fetch.packed_mips != 0, true, 0);
    if (layout.base.row_pitch_bytes == 0 ||
        layout.base.array_slice_stride_bytes == 0) {
      return false;
    }

    constexpr uint32_t kGuestBytesPerBlock = 8u;
    constexpr uint32_t kGuestBytesPerBlockLog2 = 3u;
    uint32_t guest_pitch_blocks =
        layout.base.row_pitch_bytes / kGuestBytesPerBlock;
    guest_pitch_blocks =
        std::max<uint32_t>(guest_pitch_blocks, width_blocks);

    decoded_bytes.assign(static_cast<size_t>(decoded_size), 0);
    const uint8_t* source = snapshot.upload_data.data();
    const size_t source_size = snapshot.upload_data.size();
    for (uint32_t block_y = 0; block_y < height_blocks; ++block_y) {
      for (uint32_t block_x = 0; block_x < width_blocks; ++block_x) {
        uint64_t source_offset = 0;
        if (fetch.tiled) {
          source_offset = static_cast<uint32_t>(
              texture_util::GetTiledOffset2D(
                  static_cast<int32_t>(block_x),
                  static_cast<int32_t>(block_y), guest_pitch_blocks,
                  kGuestBytesPerBlockLog2));
        } else {
          source_offset = uint64_t(block_y) * layout.base.row_pitch_bytes +
                          uint64_t(block_x) * kGuestBytesPerBlock;
        }
        if (source_offset + kGuestBytesPerBlock > source_size) {
          continue;
        }

        uint8_t block[8] = {};
        texture_conversion::CopySwapBlock(fetch.endianness, block,
                                          source + source_offset,
                                          kGuestBytesPerBlock);
        for (uint32_t local_y = 0; local_y < 4u; ++local_y) {
          const uint32_t y = block_y * 4u + local_y;
          if (y >= height) {
            continue;
          }
          for (uint32_t local_x = 0; local_x < 4u; ++local_x) {
            const uint32_t x = block_x * 4u + local_x;
            if (x >= width) {
              continue;
            }
            const uint32_t texel = local_y * 4u + local_x;
            const uint8_t packed_alpha =
                block[texel >> 1u] >> ((texel & 1u) * 4u);
            const uint8_t alpha4 = packed_alpha & 0xFu;
            decoded_bytes[uint64_t(y) * row_bytes + x] =
                static_cast<uint8_t>((alpha4 << 4u) | alpha4);
          }
        }
      }
    }
    return true;
  };

  if (decode_dxt3a_to_r8()) {
    return true;
  }

  auto decode_ctx1_to_r8g8 = [&]() -> bool {
    // CTX1 is the Xenos two-channel normal-map block format: 8-byte 4x4
    // blocks holding two (x, y) endpoint pairs — bytes 0-3 = y0, x0, y1, x1
    // after the fetch endian swap (each endpoint is a big-endian 16-bit word
    // with X in the HIGH byte, so the 8-in-16 swap lands Y first; ground
    // truth: ConvertTexelCTX1ToR8G8 in
    // rexglue/rexglue-sdk/src/graphics/pipeline/texture/conversion.cpp, whose
    // Xenia-provenance comment notes "R is in the higher bits") — and bytes
    // 4-7 holding sixteen 2-bit selectors, one byte per row, LSB-first within
    // each byte (DXT1 order).
    // CTX1 is always in 4-color mode; there is no punch-through comparison.
    // Vulkan has no CTX1 block format, so decode to R8G8_UNORM texels here
    // (the RGGG component mapping stays on the view).
    if (!IsCtx1DecodeConversion(fetch.format, PlumeGuestTextureFormat(tracked)) ||
        guest_format_info->bytes_per_block() != 8 ||
        guest_format_info->block_width != 4 ||
        guest_format_info->block_height != 4) {
      return false;
    }

    const uint32_t width = PlumeGuestTextureWidth(tracked);
    const uint32_t height = PlumeGuestTextureHeight(tracked);
    const uint32_t depth = std::max<uint32_t>(1, PlumeGuestTextureDepth(tracked));
    const uint32_t width_blocks = std::max<uint32_t>(1, (width + 3u) / 4u);
    const uint32_t height_blocks = std::max<uint32_t>(1, (height + 3u) / 4u);
    const uint32_t subresource_count =
        tracked.texture_view_dimension ==
                plume::RenderTextureViewDimension::TEXTURE_CUBE
            ? 6u
            : 1u;
    const uint64_t row_bytes = uint64_t(width) * 2u;
    const uint64_t tight_subresource_bytes = row_bytes * height * depth;
    const uint64_t decoded_size = tight_subresource_bytes * subresource_count;
    if (decoded_size == 0 || decoded_size > kMaxTextureUploadBufferSize) {
      return false;
    }

    const uint32_t depth_or_array_size =
        fetch.dimension == xenos::DataDimension::kCube
            ? 6u
            : (fetch.dimension == xenos::DataDimension::k3D ? depth : 1u);
    const texture_util::TextureGuestLayout layout =
        texture_util::GetGuestTextureLayout(
            fetch.dimension, fetch.pitch, width, height, depth_or_array_size,
            fetch.tiled != 0, fetch.format, fetch.packed_mips != 0, true, 0);
    if (layout.base.row_pitch_bytes == 0 ||
        layout.base.array_slice_stride_bytes == 0) {
      return false;
    }

    constexpr uint32_t kGuestBytesPerBlock = 8u;
    constexpr uint32_t kGuestBytesPerBlockLog2 = 3u;
    uint32_t guest_pitch_blocks =
        layout.base.row_pitch_bytes / kGuestBytesPerBlock;
    guest_pitch_blocks =
        std::max<uint32_t>(guest_pitch_blocks, width_blocks);
    const uint32_t guest_z_slice_stride_bytes =
        layout.base.row_pitch_bytes *
        std::max<uint32_t>(1, layout.base.z_slice_stride_block_rows);

    // CTX1 blocks store their 16-bit endpoint/selector words big-endian and
    // sample through an 8-in-16 fetch swap on real hardware, exactly like the
    // DXT family. Cached fetch identities for some CPU-tiled uploads carry
    // endian none, so apply the same 8-in-16 default the generic
    // block-compressed copy path below uses.
    xenos::Endian block_endian = fetch.endianness;
    if (block_endian == xenos::Endian::kNone) {
      block_endian = xenos::Endian::k8in16;
    }

    // 0x80 is the neutral two-channel normal value; blocks skipped by the
    // source bounds guard read as a flat normal instead of a corner-pull.
    decoded_bytes.assign(static_cast<size_t>(decoded_size), 0x80);
    const uint8_t* source = snapshot.upload_data.data();
    const size_t source_size = snapshot.upload_data.size();
    for (uint32_t subresource = 0; subresource < subresource_count;
         ++subresource) {
      const uint64_t source_subresource_base =
          uint64_t(subresource) * layout.base.array_slice_stride_bytes;
      const uint64_t decoded_subresource_base =
          uint64_t(subresource) * tight_subresource_bytes;
      if (source_subresource_base >= source_size ||
          decoded_subresource_base >= decoded_size) {
        continue;
      }

      for (uint32_t z = 0; z < depth; ++z) {
        for (uint32_t block_y = 0; block_y < height_blocks; ++block_y) {
          for (uint32_t block_x = 0; block_x < width_blocks; ++block_x) {
            uint64_t source_offset = source_subresource_base;
            if (fetch.tiled) {
              if (fetch.dimension == xenos::DataDimension::k3D) {
                source_offset += static_cast<uint32_t>(
                    texture_util::GetTiledOffset3D(
                        static_cast<int32_t>(block_x),
                        static_cast<int32_t>(block_y),
                        static_cast<int32_t>(z), guest_pitch_blocks,
                        std::max<uint32_t>(
                            height_blocks,
                            layout.base.z_slice_stride_block_rows),
                        kGuestBytesPerBlockLog2));
              } else {
                source_offset += static_cast<uint32_t>(
                    texture_util::GetTiledOffset2D(
                        static_cast<int32_t>(block_x),
                        static_cast<int32_t>(block_y), guest_pitch_blocks,
                        kGuestBytesPerBlockLog2));
              }
            } else {
              source_offset +=
                  uint64_t(z) * guest_z_slice_stride_bytes +
                  uint64_t(block_y) * layout.base.row_pitch_bytes +
                  uint64_t(block_x) * kGuestBytesPerBlock;
            }
            if (source_offset + kGuestBytesPerBlock > source_size) {
              continue;
            }

            uint8_t block[8] = {};
            texture_conversion::CopySwapBlock(block_endian, block,
                                              source + source_offset,
                                              kGuestBytesPerBlock);
            // Post-swap endpoint byte order is y, x (X is the high byte of
            // each big-endian 16-bit endpoint word) — matches Xenia's
            // ConvertTexelCTX1ToR8G8 {g0, r0, g1, r1} block layout.
            const uint8_t y0 = block[0];
            const uint8_t x0 = block[1];
            const uint8_t y1 = block[2];
            const uint8_t x1 = block[3];
            // CTX1 is always 4-color; thirds interpolation exactly as Xenia's
            // CPU decode (2/3-1/3 float weights, truncated to uint8).
            const uint8_t cx[4] = {
                x0, x1,
                static_cast<uint8_t>(2.f / 3.f * x0 + 1.f / 3.f * x1),
                static_cast<uint8_t>(1.f / 3.f * x0 + 2.f / 3.f * x1)};
            const uint8_t cy[4] = {
                y0, y1,
                static_cast<uint8_t>(2.f / 3.f * y0 + 1.f / 3.f * y1),
                static_cast<uint8_t>(1.f / 3.f * y0 + 2.f / 3.f * y1)};
            for (uint32_t local_y = 0; local_y < 4u; ++local_y) {
              const uint32_t texel_y = block_y * 4u + local_y;
              if (texel_y >= height) {
                continue;
              }
              const uint8_t row_selectors = block[4u + local_y];
              for (uint32_t local_x = 0; local_x < 4u; ++local_x) {
                const uint32_t texel_x = block_x * 4u + local_x;
                if (texel_x >= width) {
                  continue;
                }
                const uint8_t selector =
                    (row_selectors >> (local_x * 2u)) & 0x3u;
                const uint64_t decoded_offset =
                    decoded_subresource_base +
                    (uint64_t(z) * height + texel_y) * row_bytes +
                    uint64_t(texel_x) * 2u;
                if (decoded_offset + 2u > decoded_size) {
                  continue;
                }
                decoded_bytes[decoded_offset + 0u] = cx[selector];
                decoded_bytes[decoded_offset + 1u] = cy[selector];
              }
            }
          }
        }
      }
    }

    // Census: early rows then a coarse cadence, same shape as the GTL content
    // authority census, so the first run proves the decode engages without
    // flooding a texture-heavy load.
    ++g_ctx1_decode_census_count;
    if (g_ctx1_decode_census_count <= 8u ||
        (g_ctx1_decode_census_count % 256u) == 0u) {
      REXLOG_INFO("Refii ctx1 decode #{} w={} h={} tiled={} -> R8G8_UNORM",
                  g_ctx1_decode_census_count, width, height,
                  static_cast<uint32_t>(fetch.tiled));
    }
    return true;
  };

  if (decode_ctx1_to_r8g8()) {
    return true;
  }

  auto decode_decoded_rgba16_to_rgba8 = [&]() -> bool {
    // Fable's D3DX texture loader (sub_82D17250 ->
    // sub_82D14B20 D3DXLoadSurfaceFromMemory) CPU-converts unsupported
    // source formats and writes a linear big-endian 16-bit-per-channel
    // payload through LockRect while the cached fetch identity still claims
    // a DXT-class format. When the locked payload is RGBA16-sized, the
    // decoded image lives in the high byte of each big-endian channel.
    if (PlumeGuestTextureFormat(tracked) != plume::RenderFormat::R8G8B8A8_UNORM ||
        tracked.texture_view_dimension !=
            plume::RenderTextureViewDimension::TEXTURE_2D ||
        PlumeGuestTextureDepth(tracked) > 1) {
      return false;
    }
    switch (fetch.format) {
      case xenos::TextureFormat::k_DXT2_3:
      case xenos::TextureFormat::k_DXT4_5:
      case xenos::TextureFormat::k_DXT2_3_AS_16_16_16_16:
      case xenos::TextureFormat::k_DXT4_5_AS_16_16_16_16:
        break;
      default:
        return false;
    }

    const uint32_t width = PlumeGuestTextureWidth(tracked);
    const uint32_t height = PlumeGuestTextureHeight(tracked);
    const uint64_t source_row_bytes = uint64_t(width) * 8u;
    const uint64_t source_bytes = source_row_bytes * height;
    if (source_bytes == 0 || snapshot.upload_data.size() < source_bytes) {
      return false;
    }
    const uint64_t row_bytes = uint64_t(width) * 4u;
    const uint64_t decoded_size = row_bytes * height;
    if (decoded_size == 0 || decoded_size > kMaxTextureUploadBufferSize) {
      return false;
    }

    decoded_bytes.assign(static_cast<size_t>(decoded_size), 0);
    const uint8_t* source = snapshot.upload_data.data();
    for (uint32_t y = 0; y < height; ++y) {
      const uint8_t* source_row = source + uint64_t(y) * source_row_bytes;
      uint8_t* decoded_row = decoded_bytes.data() + uint64_t(y) * row_bytes;
      for (uint32_t x = 0; x < width; ++x) {
        const uint8_t* texel = source_row + uint64_t(x) * 8u;
        uint8_t* decoded = decoded_row + uint64_t(x) * 4u;
        decoded[0] = texel[0];
        decoded[1] = texel[2];
        decoded[2] = texel[4];
        decoded[3] = texel[6];
      }
    }
    return true;
  };

  if (decode_decoded_rgba16_to_rgba8()) {
    return true;
  }

  auto decode_authored_title_linear_bc3 = [&]() -> bool {
    // Fable's title atlas can arrive from the
    // D3DXCreateTextureFromFileInMemoryEx/D3DXLoadSurfaceFromMemory path as
    // already linear BC3 host block order. Replaying the cached Xenos fetch
    // swizzle would corrupt the atlas, so preserve the authored block stream.
    if (!IsFableCanonicalTitleAtlasAuthoredLinearBc3Upload(snapshot, tracked,
                                                           fetch)) {
      return false;
    }
    const uint32_t width_blocks =
        std::max<uint32_t>(1, (PlumeGuestTextureWidth(tracked) + 3u) / 4u);
    const uint32_t height_blocks =
        std::max<uint32_t>(1, (PlumeGuestTextureHeight(tracked) + 3u) / 4u);
    constexpr uint32_t kBc3BytesPerBlock = 16u;
    const uint64_t row_bytes = uint64_t(width_blocks) * kBc3BytesPerBlock;
    const uint64_t decoded_size = row_bytes * height_blocks;
    if (decoded_size == 0 || decoded_size > kMaxTextureUploadBufferSize ||
        snapshot.upload_data.size() < decoded_size) {
      return false;
    }
    decoded_bytes.assign(snapshot.upload_data.begin(),
                         snapshot.upload_data.begin() + decoded_size);
    return true;
  };

  if (decode_authored_title_linear_bc3()) {
    return true;
  }

  if (IsCtx1DecodeConversion(fetch.format, PlumeGuestTextureFormat(tracked))) {
    // CTX1 -> R8G8 is an intentional decode conversion owned by
    // decode_ctx1_to_r8g8 above; its guest 8-byte 4x4 blocks can never
    // satisfy the host block-geometry check below, and the verbatim block
    // copy would upload compressed blocks as R8G8 texels. Reaching here
    // means the decode declined (size/layout guard), so refuse instead of
    // reporting a plain guest/host mismatch.
    return false;
  }
  const uint32_t guest_bytes_per_block =
      guest_format_info->bytes_per_block();
  const uint32_t host_bytes_per_block =
      plume::RenderFormatSize(PlumeGuestTextureFormat(tracked));
  const uint32_t host_block_width = TextureBlockWidth(PlumeGuestTextureFormat(tracked));
  const uint32_t host_block_height = TextureBlockHeight(PlumeGuestTextureFormat(tracked));
  if (guest_bytes_per_block == 0 || host_bytes_per_block == 0 ||
      guest_bytes_per_block != host_bytes_per_block ||
      guest_format_info->block_width != host_block_width ||
      guest_format_info->block_height != host_block_height) {
    return false;
  }

  const uint32_t width_blocks =
      std::max<uint32_t>(1, (PlumeGuestTextureWidth(tracked) +
                             guest_format_info->block_width - 1u) /
                                guest_format_info->block_width);
  const uint32_t height_blocks =
      std::max<uint32_t>(1, (PlumeGuestTextureHeight(tracked) +
                             guest_format_info->block_height - 1u) /
                                guest_format_info->block_height);
  const uint32_t depth = std::max<uint32_t>(1, PlumeGuestTextureDepth(tracked));
  const uint32_t subresource_count =
      tracked.texture_view_dimension ==
              plume::RenderTextureViewDimension::TEXTURE_CUBE
          ? 6u
          : 1u;
  const uint64_t row_bytes = uint64_t(width_blocks) * host_bytes_per_block;
  const uint64_t tight_subresource_bytes = row_bytes * height_blocks * depth;
  const uint64_t decoded_size = tight_subresource_bytes * subresource_count;
  if (decoded_size == 0 || decoded_size > kMaxTextureUploadBufferSize) {
    return false;
  }

  const uint32_t depth_or_array_size =
      fetch.dimension == xenos::DataDimension::kCube
          ? 6u
          : (fetch.dimension == xenos::DataDimension::k3D ? depth : 1u);
  const texture_util::TextureGuestLayout layout =
      texture_util::GetGuestTextureLayout(
          fetch.dimension, fetch.pitch, PlumeGuestTextureWidth(tracked),
          PlumeGuestTextureHeight(tracked), depth_or_array_size, fetch.tiled != 0,
          fetch.format, fetch.packed_mips != 0, true, 0);
  if (layout.base.row_pitch_bytes == 0 ||
      layout.base.array_slice_stride_bytes == 0) {
    return false;
  }

  uint32_t guest_pitch_blocks =
      layout.base.row_pitch_bytes / guest_bytes_per_block;
  guest_pitch_blocks =
      std::max<uint32_t>(guest_pitch_blocks, width_blocks);
  const uint32_t guest_z_slice_stride_bytes =
      layout.base.row_pitch_bytes *
      std::max<uint32_t>(1, layout.base.z_slice_stride_block_rows);
  if (fetch.tiled && !IsPowerOfTwo(guest_bytes_per_block)) {
    return false;
  }
  const uint32_t guest_bytes_per_block_log2 =
      rex::log2_floor(guest_bytes_per_block);

  // Guest DXT blocks store their 16-bit endpoint/index fields big-endian and
  // sample through an 8-in-16 fetch swap on real hardware. Cached fetch
  // identities for some CPU-tiled title uploads carry endian none, which
  // would copy scrambled blocks, so default block-compressed data to 8-in-16.
  xenos::Endian block_endian = fetch.endianness;
  const bool native_linear_r32_float_upload =
      fetch.format == xenos::TextureFormat::k_32_FLOAT &&
      PlumeGuestTextureFormat(tracked) == plume::RenderFormat::R32_FLOAT &&
      !fetch.tiled && fetch.dimension != xenos::DataDimension::k3D &&
      depth == 1u && subresource_count == 1u &&
      snapshot.upload_data.size() >= decoded_size;
  if (native_linear_r32_float_upload) {
    block_endian = xenos::Endian::kNone;
  } else if (block_endian == xenos::Endian::kNone &&
      guest_format_info->type == rex::graphics::FormatType::kCompressed &&
      guest_format_info->block_width == 4u) {
    block_endian = xenos::Endian::k8in16;
  }

  decoded_bytes.assign(static_cast<size_t>(decoded_size), 0xFF);
  const uint8_t* source = snapshot.upload_data.data();
  const size_t source_size = snapshot.upload_data.size();
  if (fetch.tiled && fetch.dimension != xenos::DataDimension::k3D &&
      depth == 1 && subresource_count == 1) {
    const std::vector<uint32_t>& offsets =
        Tiled2DOffsetTable(width_blocks, height_blocks, guest_pitch_blocks,
                           guest_bytes_per_block_log2);
    const uint32_t block_count = width_blocks * height_blocks;
    const uint32_t table_count =
        static_cast<uint32_t>(std::min<size_t>(offsets.size(), block_count));
    for (uint32_t block = 0; block < table_count; ++block) {
      const uint64_t source_offset = offsets[block];
      const uint64_t decoded_offset =
          uint64_t(block) * host_bytes_per_block;
      if (source_offset + guest_bytes_per_block > source_size ||
          decoded_offset + host_bytes_per_block > decoded_size) {
        continue;
      }
      if (host_bytes_per_block == 1u) {
        decoded_bytes[static_cast<size_t>(decoded_offset)] =
            source[static_cast<size_t>(source_offset)];
      } else {
        texture_conversion::CopySwapBlock(
            block_endian, decoded_bytes.data() + decoded_offset,
            source + source_offset, host_bytes_per_block);
      }
    }
    return true;
  }

  uint32_t copied_blocks = 0;
  uint32_t skipped_blocks = 0;
  uint64_t max_source_offset = 0;
  for (uint32_t subresource = 0; subresource < subresource_count;
       ++subresource) {
    const uint64_t source_subresource_base =
        uint64_t(subresource) * layout.base.array_slice_stride_bytes;
    const uint64_t decoded_subresource_base =
        uint64_t(subresource) * tight_subresource_bytes;
    if (source_subresource_base >= source_size ||
        decoded_subresource_base >= decoded_size) {
      continue;
    }

    for (uint32_t z = 0; z < depth; ++z) {
      for (uint32_t y = 0; y < height_blocks; ++y) {
        for (uint32_t x = 0; x < width_blocks; ++x) {
          uint64_t source_offset = source_subresource_base;
          if (fetch.tiled) {
            if (fetch.dimension == xenos::DataDimension::k3D) {
              source_offset += static_cast<uint32_t>(
                  texture_util::GetTiledOffset3D(
                      static_cast<int32_t>(x), static_cast<int32_t>(y),
                      static_cast<int32_t>(z), guest_pitch_blocks,
                      std::max<uint32_t>(
                          height_blocks,
                          layout.base.z_slice_stride_block_rows),
                      guest_bytes_per_block_log2));
            } else {
              source_offset += static_cast<uint32_t>(
                  texture_util::GetTiledOffset2D(
                      static_cast<int32_t>(x), static_cast<int32_t>(y),
                      guest_pitch_blocks, guest_bytes_per_block_log2));
            }
          } else {
            source_offset += uint64_t(z) * guest_z_slice_stride_bytes +
                             uint64_t(y) * layout.base.row_pitch_bytes +
                             uint64_t(x) * guest_bytes_per_block;
          }

          const uint64_t decoded_offset =
              decoded_subresource_base +
              (uint64_t(z) * height_blocks + y) * row_bytes +
              uint64_t(x) * host_bytes_per_block;
          if (source_offset + guest_bytes_per_block > source_size ||
              decoded_offset + host_bytes_per_block > decoded_size) {
            ++skipped_blocks;
            continue;
          }
          texture_conversion::CopySwapBlock(
              block_endian, decoded_bytes.data() + decoded_offset,
              source + source_offset, host_bytes_per_block);
          ++copied_blocks;
          max_source_offset = std::max<uint64_t>(
              max_source_offset, source_offset + guest_bytes_per_block);
        }
      }
    }
  }

  if (RendererResourceTracingEnabled() &&
      g_fable_title_texture_decode_probe_count < 8u &&
      IsFableTitleTextureDecodeProbe(tracked)) {
    const uint32_t decoded_ff = CountByteValue(decoded_bytes, 0xFFu);
    const uint32_t decoded_zero = CountByteValue(decoded_bytes, 0x00u);
    std::array<uint8_t, 16> raw0 = {};
    std::array<uint8_t, 16> decoded0 = {};
    const size_t raw_copy = std::min(raw0.size(), source_size);
    if (raw_copy != 0) {
      std::memcpy(raw0.data(), source, raw_copy);
    }
    const size_t decoded_copy = std::min(decoded0.size(), decoded_bytes.size());
    if (decoded_copy != 0) {
      std::memcpy(decoded0.data(), decoded_bytes.data(), decoded_copy);
    }
    REXLOG_INFO(
        "Refii plume title texture decode probe #{} guest=0x{:08X} "
        "base=0x{:08X} size={}x{} fmt={} gfmt={} fetch_fmt={} "
        "pitch={} tiled={} endian={} source={} decoded={} row={} "
        "blocks={}/{} skipped={} max_src={} layout_row={} layout_slice={} "
        "decoded_ff={} decoded_zero={} raw0="
        "{:02X}{:02X}{:02X}{:02X}/{:02X}{:02X}{:02X}{:02X}/"
        "{:02X}{:02X}{:02X}{:02X}/{:02X}{:02X}{:02X}{:02X} dec0="
        "{:02X}{:02X}{:02X}{:02X}/{:02X}{:02X}{:02X}{:02X}/"
        "{:02X}{:02X}{:02X}{:02X}/{:02X}{:02X}{:02X}{:02X}",
        g_fable_title_texture_decode_probe_count + 1u, tracked.guest_address,
        PlumeGuestTextureBaseAddress(tracked), PlumeGuestTextureWidth(tracked),
        PlumeGuestTextureHeight(tracked),
        static_cast<uint32_t>(PlumeGuestTextureFormat(tracked)),
        static_cast<uint32_t>(PlumeGuestTextureGuestFormat(tracked)),
        static_cast<uint32_t>(fetch.format), static_cast<uint32_t>(fetch.pitch),
        static_cast<uint32_t>(fetch.tiled),
        static_cast<uint32_t>(fetch.endianness), source_size,
        decoded_bytes.size(), row_bytes, copied_blocks,
        width_blocks * height_blocks * depth * subresource_count,
        skipped_blocks, static_cast<unsigned long long>(max_source_offset),
        layout.base.row_pitch_bytes, layout.base.array_slice_stride_bytes,
        decoded_ff, decoded_zero, raw0[0], raw0[1], raw0[2], raw0[3],
        raw0[4], raw0[5], raw0[6], raw0[7], raw0[8], raw0[9], raw0[10],
        raw0[11], raw0[12], raw0[13], raw0[14], raw0[15], decoded0[0],
        decoded0[1], decoded0[2], decoded0[3], decoded0[4], decoded0[5],
        decoded0[6], decoded0[7], decoded0[8], decoded0[9], decoded0[10],
        decoded0[11], decoded0[12], decoded0[13], decoded0[14],
        decoded0[15]);
    ++g_fable_title_texture_decode_probe_count;
  }

  return true;
}

bool PrepareTextureUploadBytes(const RendererTrackedResourceSnapshot& snapshot,
                               PlumeTrackedResource& tracked,
                               std::vector<uint8_t>& upload_bytes) {
  upload_bytes.clear();
  if (plume::RenderFormatIsDepth(PlumeGuestTextureFormat(tracked))) {
    tracked.texture_upload_decoded = false;
    tracked.texture_upload_has_source = false;
    return false;
  }
  if (IsSurfaceResource(snapshot.kind) && snapshot.upload_data.empty()) {
    tracked.texture_upload_decoded = false;
    tracked.texture_upload_has_source = false;
    return false;
  }
  const uint32_t row_bytes =
      TextureTightRowBytes(PlumeGuestTextureFormat(tracked), PlumeGuestTextureWidth(tracked));
  const uint32_t row_pitch_bytes = static_cast<uint32_t>(
      AlignUp(row_bytes, kTextureUploadRowAlignment));
  const uint32_t row_count =
      TextureRowCount(PlumeGuestTextureFormat(tracked), PlumeGuestTextureHeight(tracked));
  const uint32_t depth = std::max<uint32_t>(1, PlumeGuestTextureDepth(tracked));
  const uint32_t subresource_count =
      tracked.texture_view_dimension ==
              plume::RenderTextureViewDimension::TEXTURE_CUBE
          ? 6u
          : 1u;
  const uint64_t tight_subresource_bytes =
      uint64_t(row_bytes) * row_count * depth;
  const uint64_t placed_subresource_bytes =
      AlignUp(uint64_t(row_pitch_bytes) * row_count * depth,
              kTextureUploadPlacementAlignment);
  const uint64_t upload_size = placed_subresource_bytes * subresource_count;
  if (row_bytes == 0 || row_pitch_bytes == 0 || upload_size == 0 ||
      upload_size > kMaxTextureUploadBufferSize) {
    return false;
  }

  std::vector<uint8_t> decoded_source;
  const bool decoded_guest_texture =
      DecodeGuestTextureBaseLevel(snapshot, tracked, decoded_source);
  tracked.texture_upload_decoded = decoded_guest_texture;
  const bool decoded_source_is_tightly_placed_upload =
      decoded_guest_texture && subresource_count == 1u &&
      placed_subresource_bytes == tight_subresource_bytes &&
      decoded_source.size() == upload_size;
  if (decoded_guest_texture && subresource_count == 1u &&
      placed_subresource_bytes == tight_subresource_bytes &&
      decoded_source.size() == upload_size) {
    upload_bytes = std::move(decoded_source);
  } else {
    upload_bytes.assign(static_cast<size_t>(upload_size), 0);
  }
  const uint8_t* source =
      decoded_guest_texture
          ? (decoded_source_is_tightly_placed_upload
                 ? upload_bytes.data()
                 : decoded_source.data())
          : (snapshot.upload_data.empty() ? nullptr
                                          : snapshot.upload_data.data());
  const size_t source_size =
      decoded_guest_texture
          ? (decoded_source_is_tightly_placed_upload
                 ? upload_bytes.size()
                 : decoded_source.size())
          : snapshot.upload_data.size();
  tracked.texture_upload_has_source = source != nullptr && source_size != 0;
  if (!decoded_source_is_tightly_placed_upload) {
    for (uint32_t subresource = 0; subresource < subresource_count;
         ++subresource) {
      const uint64_t source_base =
          uint64_t(subresource) * tight_subresource_bytes;
      const uint64_t upload_base =
          uint64_t(subresource) * placed_subresource_bytes;
      if (!source || source_base >= source_size) {
        continue;
      }

      for (uint32_t z = 0; z < depth; ++z) {
        for (uint32_t row = 0; row < row_count; ++row) {
          const uint64_t source_offset =
              source_base + (uint64_t(z) * row_count + row) * row_bytes;
          const uint64_t upload_offset =
              upload_base + (uint64_t(z) * row_count + row) * row_pitch_bytes;
          if (source_offset >= source_size || upload_offset >= upload_size) {
            continue;
          }
          const size_t copy_size = static_cast<size_t>(std::min<uint64_t>(
              row_bytes, std::min<uint64_t>(source_size - source_offset,
                                            upload_size - upload_offset)));
          if (copy_size != 0) {
            std::memcpy(upload_bytes.data() + upload_offset,
                        source + source_offset, copy_size);
          }
        }
      }
    }
  }

  if (IsLikelyFableVideoNeutralChromaTexture(snapshot, tracked) &&
      upload_bytes.size() >= 4u) {
    upload_bytes[0] = 0x80u;
    upload_bytes[1] = 0x80u;
    upload_bytes[2] = 0x80u;
    upload_bytes[3] = 0x80u;
  }
  {
    namespace xenos = rex::graphics::xenos;
    const std::array<uint32_t, 6> fetch_dwords =
        PreferredTextureFetchDwords(snapshot.texture);
    xenos::xe_gpu_texture_fetch_t fetch = {};
    fetch.dword_0 = fetch_dwords[0];
    fetch.dword_1 = fetch_dwords[1];
    fetch.dword_2 = fetch_dwords[2];
    fetch.dword_3 = fetch_dwords[3];
    fetch.dword_4 = fetch_dwords[4];
    fetch.dword_5 = fetch_dwords[5];
    const bool fable_neutral_white_texture =
        snapshot.kind == RendererTrackedResourceKind::kTexture &&
        (fetch.base_address << 12u) == 0x1FC40000u &&
        PlumeGuestTextureWidth(tracked) == 1u &&
        PlumeGuestTextureHeight(tracked) == 1u &&
        PlumeGuestTextureDepth(tracked) == 1u;
    if (fable_neutral_white_texture && !upload_bytes.empty()) {
      std::fill(upload_bytes.begin(), upload_bytes.end(), 0xFFu);
      switch (PlumeGuestTextureFormat(tracked)) {
        case plume::RenderFormat::BC1_UNORM:
          if (upload_bytes.size() >= 8u) {
            upload_bytes[4] = upload_bytes[5] = upload_bytes[6] =
                upload_bytes[7] = 0;
          }
          break;
        case plume::RenderFormat::BC2_UNORM:
          if (upload_bytes.size() >= 16u) {
            upload_bytes[12] = upload_bytes[13] = upload_bytes[14] =
                upload_bytes[15] = 0;
          }
          break;
        case plume::RenderFormat::BC3_UNORM:
          if (upload_bytes.size() >= 16u) {
            std::fill(upload_bytes.begin() + 2u, upload_bytes.begin() + 8u, 0);
            upload_bytes[12] = upload_bytes[13] = upload_bytes[14] =
                upload_bytes[15] = 0;
          }
          break;
        case plume::RenderFormat::BC4_UNORM:
          if (upload_bytes.size() >= 8u) {
            std::fill(upload_bytes.begin() + 2u, upload_bytes.begin() + 8u, 0);
          }
          break;
        default:
          break;
      }
    }
  }

  tracked.texture_upload_published = false;
  tracked.texture_upload_format = PlumeGuestTextureFormat(tracked);
  tracked.texture_upload_row_width =
      TextureRowWidthForPitch(PlumeGuestTextureFormat(tracked), row_pitch_bytes);
  tracked.texture_upload_subresources = subresource_count;
  tracked.texture_upload_subresource_bytes = placed_subresource_bytes;
  tracked.texture_upload_size = upload_size;
  if (RendererResourceTracingEnabled() &&
      g_plume_resources.logged_fable_video_texture_upload_samples < 6 &&
      PlumeGuestTextureFormat(tracked) == plume::RenderFormat::R8G8B8A8_UNORM &&
      PlumeGuestTextureWidth(tracked) >= 640 && PlumeGuestTextureHeight(tracked) >= 360 &&
      FableSingleSourceVideoUsesDecodedChannels(
          static_cast<uint32_t>(PlumeGuestTextureGuestFormat(tracked)))) {
    const auto pixel_at = [&](uint32_t x, uint32_t y) {
      std::array<uint8_t, 4> pixel = {};
      const uint64_t offset =
          uint64_t(y) * row_pitch_bytes + uint64_t(x) * 4u;
      if (offset + 3u < upload_bytes.size()) {
        pixel[0] = upload_bytes[static_cast<size_t>(offset + 0u)];
        pixel[1] = upload_bytes[static_cast<size_t>(offset + 1u)];
        pixel[2] = upload_bytes[static_cast<size_t>(offset + 2u)];
        pixel[3] = upload_bytes[static_cast<size_t>(offset + 3u)];
      }
      return pixel;
    };

    uint64_t sum[4] = {};
    uint32_t sample_count = 0;
    const uint32_t step_x = std::max<uint32_t>(1, PlumeGuestTextureWidth(tracked) / 16u);
    const uint32_t step_y =
        std::max<uint32_t>(1, PlumeGuestTextureHeight(tracked) / 16u);
    for (uint32_t y = 0; y < PlumeGuestTextureHeight(tracked); y += step_y) {
      for (uint32_t x = 0; x < PlumeGuestTextureWidth(tracked); x += step_x) {
        const std::array<uint8_t, 4> pixel = pixel_at(x, y);
        for (uint32_t c = 0; c < 4; ++c) {
          sum[c] += pixel[c];
        }
        ++sample_count;
      }
    }
    if (sample_count == 0) {
      sample_count = 1;
    }

    const std::array<uint8_t, 4> p0 = pixel_at(0, 0);
    const std::array<uint8_t, 4> pc =
        pixel_at(PlumeGuestTextureWidth(tracked) / 2u, PlumeGuestTextureHeight(tracked) / 2u);
    const std::array<uint8_t, 4> p1 =
        pixel_at(PlumeGuestTextureWidth(tracked) - 1u, PlumeGuestTextureHeight(tracked) - 1u);
    uint32_t raw_nonzero_samples = 0;
    uint32_t raw_sample_count = 0;
    std::array<uint8_t, 16> raw0 = {};
    if (!snapshot.upload_data.empty()) {
      const size_t raw_copy_size =
          std::min(raw0.size(), snapshot.upload_data.size());
      std::memcpy(raw0.data(), snapshot.upload_data.data(), raw_copy_size);
      const size_t raw_step =
          std::max<size_t>(1, snapshot.upload_data.size() / 256u);
      for (size_t offset = 0; offset < snapshot.upload_data.size();
           offset += raw_step) {
        raw_nonzero_samples += snapshot.upload_data[offset] != 0 ? 1u : 0u;
        ++raw_sample_count;
      }
    }
    REXLOG_INFO(
        "Refii plume Fable video upload sample #{} guest=0x{:08X} "
        "size={}x{} row={} decoded={} fmt={} raw36=0x{:08X} "
        "p0={}/{}/{}/{} pc={}/{}/{}/{} p1={}/{}/{}/{} "
        "avg={}/{}/{}/{} raw_nz={}/{} raw0="
        "{:02X}{:02X}{:02X}{:02X}/{:02X}{:02X}{:02X}{:02X}/"
        "{:02X}{:02X}{:02X}{:02X}/{:02X}{:02X}{:02X}{:02X}",
        g_plume_resources.logged_fable_video_texture_upload_samples + 1u,
        tracked.guest_address, PlumeGuestTextureWidth(tracked), PlumeGuestTextureHeight(tracked),
        row_pitch_bytes, decoded_guest_texture ? 1 : 0,
        static_cast<uint32_t>(PlumeGuestTextureGuestFormat(tracked)),
        snapshot.surface.packed_extent, p0[0], p0[1], p0[2], p0[3],
        pc[0], pc[1],
        pc[2], pc[3], p1[0], p1[1], p1[2], p1[3],
        static_cast<uint32_t>(sum[0] / sample_count),
        static_cast<uint32_t>(sum[1] / sample_count),
        static_cast<uint32_t>(sum[2] / sample_count),
        static_cast<uint32_t>(sum[3] / sample_count), raw_nonzero_samples,
        raw_sample_count, raw0[0], raw0[1], raw0[2], raw0[3], raw0[4],
        raw0[5], raw0[6], raw0[7], raw0[8], raw0[9], raw0[10], raw0[11],
        raw0[12], raw0[13], raw0[14], raw0[15]);
    ++g_plume_resources.logged_fable_video_texture_upload_samples;
  }
  if (g_plume_resources.logged_fable_video_plane_upload_samples < 18 &&
      PlumeGuestTextureFormat(tracked) == plume::RenderFormat::R8_UNORM &&
      PlumeGuestTextureWidth(tracked) >= 640 &&
      PlumeGuestTextureHeight(tracked) >= 360) {
    const auto sample_at = [&](uint32_t x, uint32_t y) {
      const uint64_t offset = uint64_t(y) * row_pitch_bytes + x;
      return offset < upload_bytes.size()
                 ? upload_bytes[static_cast<size_t>(offset)]
                 : uint8_t(0);
    };

    uint64_t sum = 0;
    uint8_t min_sample = 0xFFu;
    uint8_t max_sample = 0u;
    uint32_t sample_count = 0;
    const uint32_t step_x =
        std::max<uint32_t>(1, PlumeGuestTextureWidth(tracked) / 32u);
    const uint32_t step_y =
        std::max<uint32_t>(1, PlumeGuestTextureHeight(tracked) / 32u);
    for (uint32_t y = 0; y < PlumeGuestTextureHeight(tracked); y += step_y) {
      for (uint32_t x = 0; x < PlumeGuestTextureWidth(tracked); x += step_x) {
        const uint8_t value = sample_at(x, y);
        sum += value;
        min_sample = std::min<uint8_t>(min_sample, value);
        max_sample = std::max<uint8_t>(max_sample, value);
        ++sample_count;
      }
    }
    if (sample_count == 0) {
      sample_count = 1;
      min_sample = 0;
    }

    uint32_t raw_nonzero_samples = 0;
    uint32_t raw_sample_count = 0;
    std::array<uint8_t, 16> raw0 = {};
    if (!snapshot.upload_data.empty()) {
      const size_t raw_copy_size =
          std::min(raw0.size(), snapshot.upload_data.size());
      std::memcpy(raw0.data(), snapshot.upload_data.data(), raw_copy_size);
      const size_t raw_step =
          std::max<size_t>(1, snapshot.upload_data.size() / 256u);
      for (size_t offset = 0; offset < snapshot.upload_data.size();
           offset += raw_step) {
        raw_nonzero_samples += snapshot.upload_data[offset] != 0 ? 1u : 0u;
        ++raw_sample_count;
      }
    }

    const uint8_t p0 = sample_at(0, 0);
    const uint8_t pc = sample_at(PlumeGuestTextureWidth(tracked) / 2u,
                                 PlumeGuestTextureHeight(tracked) / 2u);
    const uint8_t p1 = sample_at(PlumeGuestTextureWidth(tracked) - 1u,
                                 PlumeGuestTextureHeight(tracked) - 1u);
    REXLOG_INFO(
        "Refii plume Fable R8 video plane upload #{} guest=0x{:08X} "
        "size={}x{} row={} decoded={} gen={} hash=0x{:016X} fmt={} "
        "raw36=0x{:08X} p0={} pc={} p1={} avg={} min={} max={} "
        "raw_nz={}/{} raw0="
        "{:02X}{:02X}{:02X}{:02X}/{:02X}{:02X}{:02X}{:02X}/"
        "{:02X}{:02X}{:02X}{:02X}/{:02X}{:02X}{:02X}{:02X}",
        g_plume_resources.logged_fable_video_plane_upload_samples + 1u,
        tracked.guest_address, PlumeGuestTextureWidth(tracked),
        PlumeGuestTextureHeight(tracked), row_pitch_bytes,
        decoded_guest_texture ? 1 : 0, tracked.upload_generation,
        tracked.texture_upload_hash,
        static_cast<uint32_t>(PlumeGuestTextureGuestFormat(tracked)),
        snapshot.surface.packed_extent, p0, pc, p1,
        static_cast<uint32_t>(sum / sample_count), min_sample, max_sample,
        raw_nonzero_samples, raw_sample_count, raw0[0], raw0[1], raw0[2],
        raw0[3], raw0[4], raw0[5], raw0[6], raw0[7], raw0[8], raw0[9],
        raw0[10], raw0[11], raw0[12], raw0[13], raw0[14], raw0[15]);
    ++g_plume_resources.logged_fable_video_plane_upload_samples;
  }
  return true;
}

namespace {
std::mutex g_partial_recapture_mutex;
std::vector<PartialUploadRecaptureRequest> g_partial_recapture_queue;
}  // namespace

void QueuePartialUploadRecapture(uint32_t guest_address,
                                 const std::array<uint32_t, 6>& fetch_dwords) {
  if (guest_address == 0) {
    return;
  }
  std::lock_guard<std::mutex> lock(g_partial_recapture_mutex);
  if (g_partial_recapture_queue.size() >= 64u) {
    return;
  }
  if (std::find_if(g_partial_recapture_queue.begin(),
                   g_partial_recapture_queue.end(),
                   [&](const PartialUploadRecaptureRequest& request) {
                     return request.guest_address == guest_address;
                   }) == g_partial_recapture_queue.end()) {
    g_partial_recapture_queue.push_back({guest_address, fetch_dwords});
  }
}

std::vector<PartialUploadRecaptureRequest> DrainPartialUploadRecaptures() {
  std::lock_guard<std::mutex> lock(g_partial_recapture_mutex);
  return std::exchange(g_partial_recapture_queue, {});
}

bool IsFableDirectTextureUploadAllowed(
    const RendererTrackedResourceSnapshot& snapshot,
    const PlumeTrackedResource& tracked) {
  const plume::RenderFormat format = PlumeGuestTextureFormat(tracked);
  const uint32_t width = PlumeGuestTextureWidth(tracked);
  const uint32_t height = PlumeGuestTextureHeight(tracked);
  if (format == plume::RenderFormat::UNKNOWN ||
      plume::RenderFormatIsDepth(format) || width == 0 ||
      PlumeGuestTextureHeight(tracked) == 0 || snapshot.upload_data.empty()) {
    return false;
  }

  if (tracked.texture_view_dimension !=
      plume::RenderTextureViewDimension::TEXTURE_2D) {
    return false;
  }

  const uint32_t row_bytes = TextureTightRowBytes(format, width);
  const uint32_t row_count = TextureRowCount(format, height);
  const uint64_t tight_upload_bytes =
      uint64_t(row_bytes) * row_count * PlumeGuestTextureDepth(tracked);
  if (row_bytes == 0 || row_count == 0 || tight_upload_bytes == 0 ||
      tight_upload_bytes > kMaxTextureUploadBufferSize) {
    return false;
  }

  if (snapshot.upload_data.size() >= tight_upload_bytes) {
    return true;
  }

  namespace xenos = rex::graphics::xenos;
  namespace texture_util = rex::graphics::texture_util;
  const std::array<uint32_t, 6> fetch_dwords =
      PreferredTextureFetchDwords(snapshot.texture);
  xenos::xe_gpu_texture_fetch_t fetch = {};
  fetch.dword_0 = fetch_dwords[0];
  fetch.dword_1 = fetch_dwords[1];
  fetch.dword_2 = fetch_dwords[2];
  fetch.dword_3 = fetch_dwords[3];
  fetch.dword_4 = fetch_dwords[4];
  fetch.dword_5 = fetch_dwords[5];
  if (fetch.type != xenos::FetchConstantType::kTexture &&
      fetch.type != xenos::FetchConstantType::kInvalidTexture) {
    return false;
  }

  const rex::graphics::FormatInfo* guest_format_info =
      rex::graphics::FormatInfo::Get(fetch.format);
  if (!guest_format_info || guest_format_info->bytes_per_block() == 0 ||
      guest_format_info->block_width == 0 ||
      guest_format_info->block_height == 0) {
    return false;
  }

  const texture_util::TextureGuestLayout layout =
      texture_util::GetGuestTextureLayout(
          fetch.dimension, fetch.pitch, width, height,
          PlumeGuestTextureDepth(tracked), fetch.tiled != 0, fetch.format,
          fetch.packed_mips != 0, true, 0);
  const uint64_t guest_upload_bytes = layout.base.array_slice_stride_bytes;
  return guest_upload_bytes != 0 &&
         guest_upload_bytes <= kMaxTextureUploadBufferSize &&
         snapshot.upload_data.size() >= guest_upload_bytes;
}

bool EnsureFrameTextureUploadSlabs(plume::RenderDevice* device) {
  return EnsureFrameTextureUploadSlabsInternal(device);
}

bool ShouldBypassTextureDecodeCacheForTransientUpload(
    const RendererTrackedResourceSnapshot& snapshot,
    const PlumeTrackedResource& tracked, size_t upload_size) {
  if (snapshot.kind != RendererTrackedResourceKind::kTexture ||
      snapshot.texture.base_address != 0 || upload_size < (128u * 1024u)) {
    return false;
  }

  const plume::RenderFormat format = PlumeGuestTextureFormat(tracked);
  const uint32_t width = PlumeGuestTextureWidth(tracked);
  const uint32_t height = PlumeGuestTextureHeight(tracked);
  if (width < 640u || height < 360u) {
    return false;
  }

  return format == plume::RenderFormat::R8_UNORM ||
         format == plume::RenderFormat::R8G8B8A8_UNORM;
}

// §4 delete-gate census, shared by the GTL-wins site and the observer-fallback
// site below. Plain counters, matching this file's existing convention for
// log-sampling statics; they are diagnostics, and a lost increment under
// concurrency would not change any verdict these numbers support.
struct GtlContentAuthorityCensus {
  uint64_t both = 0;             // both authorities produced bytes
  uint64_t identical = 0;        // ... and they matched exactly
  uint64_t size_differs = 0;
  uint64_t bytes_differ = 0;
  uint64_t gtl_only = 0;         // GTL had bytes, observer had none
  uint64_t observer_only = 0;    // observer had bytes, GTL had none  <-- the
                                 // number that decides what deletion COSTS
  // Run 159: all 32 sampled disagreements carried observer
  // upload_data_linear=1, while the GTL hardcodes upload_data_linear=false for
  // every unlock range. These two split `both` by that flag to test whether
  // the correlation is TOTAL — if no obs_linear=1 case ever agrees and no
  // obs_linear=0 case ever differs, the layout flag is the whole story and the
  // fix is to state the layout rather than assume it.
  uint64_t identical_obs_linear = 0;
  uint64_t differ_obs_linear = 0;
  // Layout-pair breakdown. Until the tile writer landed the GTL only ever held
  // TILED bytes, so "observer captured linear" implied disagreement and the
  // two were used interchangeably. Run 169 broke that: the GTL now stores
  // linear bytes for tile-fed textures, and 61 of 65 agreements are
  // linear-vs-linear. The old invariant did not become false — it was never
  // the real one. Comparing the two layout flags directly is.
  //
  // Expected: agreement iff the layouts match. Cross-layout agreement is only
  // possible for degenerate content (an all-zero texture matches in any
  // layout), which is the same trap the physical-view A/B hit, so it is
  // counted rather than assumed away.
  uint64_t same_layout_identical = 0;
  uint64_t same_layout_differ = 0;
  uint64_t cross_layout_identical = 0;
  uint64_t cross_layout_differ = 0;
  // DISTINCT allocations per bucket, not call counts.
  //
  // The counters above count REFRESH CALLS, and a texture is refreshed many
  // times over a run — so `observer_only=11,200` is not "11,200 allocations
  // the GTL lacks", it is that many calls across far fewer addresses. Reading
  // a call count as a coverage number is the same conflation that made
  // "77 vs 76 distinct objects" look like parity while the per-call picture
  // was nothing like it; the fix is to report both rather than argue about
  // which one is "the" number.
  //
  // Coverage — the thing §4's delete-gate actually needs — is a property of
  // ADDRESSES: how many distinct allocations does the observer carry that the
  // GTL cannot?
  std::unordered_set<uint32_t> both_addresses;
  std::unordered_set<uint32_t> gtl_only_addresses;
  std::unordered_set<uint32_t> observer_only_addresses;
  // Of the observer_only addresses, those a Resolve produced on the GPU. These
  // are NOT a coverage gap: no CPU byte source exists for them by
  // construction, so the GTL having nothing is the design working. Splitting
  // them out is what keeps §4's gap number honest — lumping them in argues for
  // keeping capture machinery to answer an unanswerable question.
  std::unordered_set<uint32_t> observer_only_gpu_addresses;
  // Addresses already named by the GAP probe, so each is described once
  // rather than once per refresh.
  std::unordered_set<uint32_t> observer_only_gap_sampled;
  // Gap addresses that belong to a DEDICATED content channel (Bink planes,
  // Pix video). Like the GPU-produced split, these are not an unlock/tile/
  // resolve coverage gap: they have their own publisher in runtime/bik and
  // their own path into the renderer, so counting them against §4 overstates
  // what the unlock+tile writers still have to cover.
  //
  // Run 172 found them by sampling: 12 of the first 40 gap addresses were
  // channel=kBink, and their shapes gave it away outright — 1280x720 paired
  // with 640x360 at fmt=50 is a luma/chroma plane set. Counting the whole
  // population beats extrapolating from a capped sample.
  std::unordered_set<uint32_t> observer_only_video_addresses;
  // Gap addresses a write root DID fire for. Non-zero means the gap is
  // UNDERSTOOD but unstored — the writer knows when content changed and not
  // yet what bytes to take. Distinguishes remaining work from remaining
  // mystery.
  std::unordered_set<uint32_t> observer_only_explained_addresses;
  // §4 real_gap capture-SITE attribution (2026-07-23). For each real_gap
  // address (not gpu-produced, not a video channel), which observer
  // fetch-capture site last ADVANCED its bytes — and, separately, which of
  // those addresses the streamed-pool RESIDENCY path ever fed. Together these
  // decide the missing "fourth writer" question WITHOUT a build-then-guess:
  //   - if store_object_fetch dominates and residency_seen stays ~0, the
  //     residency event does not precede content, so a residency-driven writer
  //     (Option A) would capture nothing and bind-time (Option B) is the real
  //     coverage;
  //   - if residency_seen covers the cohort, Option A is viable.
  // Distinct addresses per bucket (call counts already conflated coverage once
  // this run, see the header notes), keyed by the static site literal.
  std::unordered_map<std::string, std::unordered_set<uint32_t>>
      gap_addr_by_site;
  std::unordered_set<uint32_t> gap_residency_seen_addresses;
  // Gap addresses whose observer bytes are ALL ZERO. Run 185 found the 2
  // residual known=0 addresses (0x441428FC/914) are blank scratch headers
  // (every capture watch nonzero=0) — a false gap: a blank texture renders as
  // nothing regardless of which authority "owns" it, and there is no content to
  // move into the GTL. Counted out so the delete-gate reads the TRUE gap.
  std::unordered_set<uint32_t> gap_blank_addresses;
};
GtlContentAuthorityCensus g_gtl_content_authority_census;

void ReportGtlContentAuthorityCensus() {
  const GtlContentAuthorityCensus& c = g_gtl_content_authority_census;
  const uint64_t total = c.both + c.gtl_only + c.observer_only;
  // Early rows then a coarse cadence. A pure `% 256` gate risks a run that
  // never reaches the threshold reporting NOTHING, which costs a whole
  // drive to discover — the first few rows make the channel's presence
  // visible immediately even in a short or content-light run.
  if (total == 0 ||
      !(total <= 4u || (total % 64u) == 0u)) {
    return;
  }
  REXLOG_INFO(
      "Refii GTL content authority census calls[both={} identical={} "
      "size_differs={} bytes_differ={} gtl_only={} observer_only={}] "
      "| obs_linear: identical={} differ={} "
      "| layout same[ident={} differ={}] cross[ident={} differ={}] "
      "| DISTINCT addrs[both={} gtl_only={} observer_only={} "
      "(of which gpu_produced={} video={} => real_gap={}, of those explained={})]",
      static_cast<unsigned long long>(c.both),
      static_cast<unsigned long long>(c.identical),
      static_cast<unsigned long long>(c.size_differs),
      static_cast<unsigned long long>(c.bytes_differ),
      static_cast<unsigned long long>(c.gtl_only),
      static_cast<unsigned long long>(c.observer_only),
      static_cast<unsigned long long>(c.identical_obs_linear),
      static_cast<unsigned long long>(c.differ_obs_linear),
      static_cast<unsigned long long>(c.same_layout_identical),
      static_cast<unsigned long long>(c.same_layout_differ),
      static_cast<unsigned long long>(c.cross_layout_identical),
      static_cast<unsigned long long>(c.cross_layout_differ),
      static_cast<unsigned long long>(c.both_addresses.size()),
      static_cast<unsigned long long>(c.gtl_only_addresses.size()),
      static_cast<unsigned long long>(c.observer_only_addresses.size()),
      static_cast<unsigned long long>(c.observer_only_gpu_addresses.size()),
      static_cast<unsigned long long>(c.observer_only_video_addresses.size()),
      static_cast<unsigned long long>(
          c.observer_only_addresses.size() >=
                  c.observer_only_gpu_addresses.size() +
                      c.observer_only_video_addresses.size()
              ? c.observer_only_addresses.size() -
                    c.observer_only_gpu_addresses.size() -
                    c.observer_only_video_addresses.size()
              : 0u),
      static_cast<unsigned long long>(
          c.observer_only_explained_addresses.size()));

  // §4 real_gap capture-site attribution. One compact line so the missing
  // fourth-writer decision reads off a summary rather than 512 GAP lines. The
  // real_gap count here is recomputed the same way the census line does it, so
  // the two agree by construction.
  const uint64_t real_gap =
      c.observer_only_addresses.size() >=
              c.observer_only_gpu_addresses.size() +
                  c.observer_only_video_addresses.size()
          ? c.observer_only_addresses.size() -
                c.observer_only_gpu_addresses.size() -
                c.observer_only_video_addresses.size()
          : 0u;
  std::string sites;
  for (const auto& [site, addrs] : c.gap_addr_by_site) {
    if (!sites.empty()) {
      sites += ' ';
    }
    sites += site;
    sites += '=';
    sites += std::to_string(addrs.size());
  }
  const uint64_t blank = c.gap_blank_addresses.size();
  const uint64_t true_gap = real_gap >= blank ? real_gap - blank : 0u;
  REXLOG_INFO(
      "Refii GTL content GAP attribution real_gap={} blank={} true_gap={} "
      "residency_seen={} sites[{}]",
      static_cast<unsigned long long>(real_gap),
      static_cast<unsigned long long>(blank),
      static_cast<unsigned long long>(true_gap),
      static_cast<unsigned long long>(c.gap_residency_seen_addresses.size()),
      sites.empty() ? "none" : sites.c_str());
}

bool RefreshTrackedTextureUpload(
    plume::RenderDevice* device,
    const RendererTrackedResourceSnapshot& observer_snapshot,
    PlumeTrackedResource& tracked) {
  // §4 content cutover. The GTL holds the bytes the unlock roots stated,
  // copied at the flush; the observer holds bytes it went looking for after
  // the fact. When the GTL has this allocation, its bytes win.
  //
  // Deliberately ONLY the bytes. Refresh timing still comes from the observer
  // snapshot's upload_generation, so a difference in the rendered result is
  // attributable to content and nothing else — the run-145 lesson, where a
  // read-address change moved the gating and the verdict was unreadable.
  RendererTrackedResourceSnapshot gtl_snapshot;
  bool gtl_content = false;
  // §4 exception: dedicated-channel video records (Bink plane pushes, pix
  // decoded frames). Their content NEVER passes an unlock root — it is pushed
  // by RefreshBinkVideoPlaneUploadLocked / the pix decode channel — so the GTL
  // content store has no live writer for them; whatever bytes it holds are
  // creation-time leftovers, stale by construction. Preferring those bytes
  // replaced every live YUV publish with the stale store on every refresh
  // (runs 423-425: green movie, planes zero). Channel-derived, not an address
  // whitelist: the channel marker is stamped by the push paths themselves and
  // rides both the pushed snapshot and the tracked record (sticky ownership
  // below).
  const bool video_channel_record =
      observer_snapshot.content_channel == FableGuestContentChannel::kBink ||
      observer_snapshot.content_channel == FableGuestContentChannel::kPix ||
      tracked.content_channel == FableGuestContentChannel::kBink ||
      tracked.content_channel == FableGuestContentChannel::kPix;
  uint64_t gtl_store_generation = 0;
  if (RendererGtlContentAuthorityEnabled() && !video_channel_record &&
      observer_snapshot.guest_address != 0) {
    std::vector<uint8_t> bytes;
    bool gtl_source_linear = false;
    if (refii::gtl::CopyResourceContent(observer_snapshot.guest_address, false,
                                        bytes, gtl_source_linear) &&
        !bytes.empty()) {
      gtl_snapshot = observer_snapshot;
      gtl_snapshot.upload_data = std::move(bytes);
      // STATED by whichever root stored these bytes, not assumed here. An
      // unlock range is the resource's own allocation (tiled when the texture
      // is tiled); a CPU tile call's source is a linear staging buffer. This
      // was hardcoded false while unlock was the only writer — correct then,
      // and wrong the moment the tile writer landed.
      gtl_snapshot.upload_data_linear = gtl_source_linear;
      gtl_snapshot.upload_data_mip_level = 0;
      gtl_content = true;
      // Refresh timing rides the observer's upload_generation (run-145
      // attribution) — but under the 07-29 draw-event gate the observer never
      // advances it for these records, so a stated-content snapshot arrived
      // here with gen=0 and the generation gate below dropped the bytes every
      // refresh (run 422: 19 of 24 logged GTL content uploads carried gen=0).
      // The record then never gained a meaningful upload, its texture identity
      // never registered, and the exact-title preflight fabricated fresh alias
      // records for the same base every frame — the direct-registry growth.
      // When the observer has no clock for the record, the content store's own
      // generation is the refresh clock; observer-clocked records keep the
      // observer's.
      gtl_store_generation = refii::gtl::QueryResourceContentStatus(
                                 observer_snapshot.guest_address)
                                 .content_generation;
      if (gtl_snapshot.upload_generation == 0) {
        gtl_snapshot.upload_generation = gtl_store_generation;
        // The store's generation only substitutes as the refresh clock while
        // it ADVANCES. A static entry (one boot unlock, never written again)
        // must not keep re-winning against whatever live channel feeds this
        // record: once its generation has been applied, an unadvanced store
        // yields to the observer snapshot.
        if (gtl_store_generation <= tracked.texture_upload_gtl_generation) {
          gtl_content = false;
        }
      }

      // §4 delete-gate census. Step 3's binding shadow settled its cutover by
      // asking "do the two authorities name the same thing?" — this is the
      // content analogue, and the same question decides whether the observer's
      // capture machinery (CaptureTextureUploadFromFetchDwords, the blank and
      // recompose watches, alias propagation, orphan adoption) can be deleted
      // or is still carrying bytes the unlock ranges miss.
      //
      // `identical` is the one that condemns the observer path: same size AND
      // same hash means its bytes were redundant. `differ` is the interesting
      // case and needs its own diagnosis before anything is deleted — the GTL
      // is stated-at-flush and should be the more trustworthy of the two, but
      // "should be" is what run 145 punished.
      //
      // observer_only is counted at the fallback site below, not here.
      // (Skipped when the unadvanced-clock guard above dropped the bytes —
      // that refresh is an observer refresh, not a GTL one.)
      if (gtl_content) {
        GtlContentAuthorityCensus& census = g_gtl_content_authority_census;
        if (observer_snapshot.upload_data.empty()) {
          ++census.gtl_only;
          census.gtl_only_addresses.insert(observer_snapshot.guest_address);
        } else {
          ++census.both;
          census.both_addresses.insert(observer_snapshot.guest_address);
          if (observer_snapshot.upload_data.size() !=
              gtl_snapshot.upload_data.size()) {
            ++census.size_differs;
          } else if (HashUploadBytesFnv1a64(gtl_snapshot.upload_data.data(),
                                            gtl_snapshot.upload_data.size()) ==
                     HashUploadBytesFnv1a64(
                         observer_snapshot.upload_data.data(),
                         observer_snapshot.upload_data.size())) {
            ++census.identical;
            if (observer_snapshot.upload_data_linear) {
              ++census.identical_obs_linear;
            }
            if (gtl_source_linear == observer_snapshot.upload_data_linear) {
              ++census.same_layout_identical;
            } else {
              ++census.cross_layout_identical;
            }
          } else {
            ++census.bytes_differ;
            if (observer_snapshot.upload_data_linear) {
              ++census.differ_obs_linear;
            }
            if (gtl_source_linear == observer_snapshot.upload_data_linear) {
              ++census.same_layout_differ;
            } else {
              ++census.cross_layout_differ;
            }
            // Run 158: where both authorities produced bytes for the SAME
            // allocation they disagreed 31 of 37 times — and the GTL's bytes
            // win by default. Small blast radius, but it is an open
            // correctness question, so name the allocations rather than
            // reasoning about which side "should" be right (run 145).
            static uint32_t logged_content_disagreements = 0;
            if (logged_content_disagreements < 32u) {
              REXLOG_INFO(
                  "Refii GTL content DISAGREE #{} guest=0x{:08X} bytes={} "
                  "gtl_hash=0x{:016X} obs_hash=0x{:016X} {}x{} fmt={} "
                  "channel={} gen={} obs_linear={} obs_mip={}",
                  ++logged_content_disagreements,
                  observer_snapshot.guest_address,
                  gtl_snapshot.upload_data.size(),
                  HashUploadBytesFnv1a64(gtl_snapshot.upload_data.data(),
                                         gtl_snapshot.upload_data.size()),
                  HashUploadBytesFnv1a64(observer_snapshot.upload_data.data(),
                                         observer_snapshot.upload_data.size()),
                  PlumeGuestTextureWidth(tracked),
                  PlumeGuestTextureHeight(tracked),
                  static_cast<uint32_t>(PlumeGuestTextureFormat(tracked)),
                  static_cast<uint32_t>(observer_snapshot.content_channel),
                  static_cast<unsigned long long>(
                      observer_snapshot.upload_generation),
                  observer_snapshot.upload_data_linear ? 1 : 0,
                  observer_snapshot.upload_data_mip_level);
            }
          }
        }
        ReportGtlContentAuthorityCensus();
      }

      static uint64_t logged_gtl_content_uploads = 0;
      if (gtl_content &&
          (++logged_gtl_content_uploads <= 24u ||
           (logged_gtl_content_uploads % 512u) == 0u)) {
        REXLOG_INFO(
            "Refii GTL content upload #{} guest=0x{:08X} gtl_bytes={} "
            "observer_bytes={} gen={}",
            static_cast<unsigned long long>(logged_gtl_content_uploads),
            observer_snapshot.guest_address, gtl_snapshot.upload_data.size(),
            observer_snapshot.upload_data.size(),
            static_cast<unsigned long long>(observer_snapshot.upload_generation));
      }
    }
  }
  if (!gtl_content && !observer_snapshot.upload_data.empty()) {
    // The GTL had nothing for an allocation the observer did carry bytes for.
    // This is the cost of deleting the observer capture path, stated directly:
    // while this stays nonzero, the unlock-range channel is not yet the whole
    // content story and §4's deletions are not safe.
    ++g_gtl_content_authority_census.observer_only;
    g_gtl_content_authority_census.observer_only_addresses.insert(
        observer_snapshot.guest_address);
    if (refii::gtl::ResourceHasGpuProducedContent(
            observer_snapshot.guest_address)) {
      g_gtl_content_authority_census.observer_only_gpu_addresses.insert(
          observer_snapshot.guest_address);
    } else if (observer_snapshot.content_channel ==
                   FableGuestContentChannel::kBink ||
               observer_snapshot.content_channel ==
                   FableGuestContentChannel::kPix) {
      g_gtl_content_authority_census.observer_only_video_addresses.insert(
          observer_snapshot.guest_address);
    } else {
      // Explained-but-not-stored: a write root DID fire for this allocation
      // (streamed pool, most likely) even though the GTL holds no bytes for it.
      // That is a different state from "nobody ever wrote it", and it is the
      // one the streamed writer creates — generations without content. Counted
      // live rather than read off the first-sight GAP log, whose `gen=` is
      // whatever it was the first time the address appeared and never updates.
      if (refii::gtl::QueryResourceContentStatus(observer_snapshot.guest_address)
              .content_generation != 0) {
        g_gtl_content_authority_census.observer_only_explained_addresses.insert(
            observer_snapshot.guest_address);
      }
      // §4 fourth-writer decision: attribute this real_gap address to the
      // observer capture SITE that last fed it, and note if residency ever did.
      // These are the only two facts that separate Option A (residency-event
      // writer) from Option B (bind-time writer) — measured, not assumed.
      g_gtl_content_authority_census
          .gap_addr_by_site[observer_snapshot.last_upload_capture_site
                                ? observer_snapshot.last_upload_capture_site
                                : "none"]
          .insert(observer_snapshot.guest_address);
      if (observer_snapshot.residency_capture_seen) {
        g_gtl_content_authority_census.gap_residency_seen_addresses.insert(
            observer_snapshot.guest_address);
      }
      // Blank scratch (run 185): all-zero observer bytes are a FALSE gap — no
      // content exists to move into the GTL. any_of stops at the first nonzero
      // byte, so the full scan only runs for genuinely-blank buffers (few).
      if (std::none_of(observer_snapshot.upload_data.begin(),
                       observer_snapshot.upload_data.end(),
                       [](uint8_t b) { return b != 0; })) {
        g_gtl_content_authority_census.gap_blank_addresses.insert(
            observer_snapshot.guest_address);
      }
      if (g_gtl_content_authority_census.observer_only_gap_sampled.insert(
              observer_snapshot.guest_address).second) {
      // Name the GAP, once per address. `real_gap` says how many allocations
      // §4 still cannot explain; it does not say WHAT they are, and the next
      // move depends entirely on that. Chasing more tile matches assumes tile
      // is the answer — the observer already records which channel delivered
      // each resource, so ask it instead of guessing.
      // Cap raised 40 -> 512 (2026-07-23). At 40 this set was both the gap's
      // composition sample AND one side of the tile-miss intersection, and it
      // filled during the video phase — which is how the Bink share came out
      // 30% by sample against 11% measured. Deduped per address, so 512 is
      // cheap and covers the whole population at current gap sizes.
      static uint32_t logged_gap_samples = 0;
      if (logged_gap_samples < 512u) {
        // Ask the GTL what it knows about this address, not just that it has
        // no bytes. `known`/`gtl_gen` split the gap into three populations
        // needing three different fixes — unregistered resource, registered
        // but never written by any hooked root, or written yet unstored.
        const refii::gtl::ResourceContentStatus gtl_status =
            refii::gtl::QueryResourceContentStatus(
                observer_snapshot.guest_address);
        REXLOG_INFO(
            "Refii GTL content GAP #{} guest=0x{:08X} channel={} kind={} "
            "capsite={} residency_seen={} bytes={} "
            "{}x{} fmt={} linear={} mip={} gen={} "
            "gtl[known={} via=0x{:08X} lr=0x{:08X} gen={} gpu_gen={} base=0x{:08X} stored={}]",
            ++logged_gap_samples, observer_snapshot.guest_address,
            static_cast<uint32_t>(observer_snapshot.content_channel),
            static_cast<uint32_t>(observer_snapshot.kind),
            observer_snapshot.last_upload_capture_site
                ? observer_snapshot.last_upload_capture_site
                : "none",
            observer_snapshot.residency_capture_seen ? 1 : 0,
            observer_snapshot.upload_data.size(),
            PlumeGuestTextureWidth(tracked), PlumeGuestTextureHeight(tracked),
            static_cast<uint32_t>(PlumeGuestTextureFormat(tracked)),
            observer_snapshot.upload_data_linear ? 1 : 0,
            observer_snapshot.upload_data_mip_level,
            static_cast<unsigned long long>(
                observer_snapshot.upload_generation),
            gtl_status.known ? 1 : 0, gtl_status.create_symbol,
            gtl_status.create_lr,
            static_cast<unsigned long long>(gtl_status.content_generation),
            static_cast<unsigned long long>(gtl_status.gpu_content_generation),
            gtl_status.base_address, gtl_status.has_stored_bytes ? 1 : 0);
      }
      }
    }
    ReportGtlContentAuthorityCensus();
  }
  const RendererTrackedResourceSnapshot& snapshot =
      gtl_content ? gtl_snapshot : observer_snapshot;
  // Content-source proof for the dedicated video channels (green-movie fix,
  // runs 423-425): one line per decision, capped, naming which authority's
  // bytes this refresh applies.
  if (video_channel_record) {
    static uint64_t logged_bink_plane_content_sources = 0;
    if (++logged_bink_plane_content_sources <= 24u ||
        (logged_bink_plane_content_sources % 256u) == 0u) {
      REXLOG_INFO(
          "Refii bink plane content source #{} guest=0x{:08X} source={} "
          "gen={} bytes={} channel={}",
          static_cast<unsigned long long>(logged_bink_plane_content_sources),
          observer_snapshot.guest_address,
          gtl_content
              ? "gtl"
              : (observer_snapshot.content_channel ==
                         FableGuestContentChannel::kBink ||
                 observer_snapshot.content_channel ==
                         FableGuestContentChannel::kPix
                     ? "bink"
                     : "observer"),
          static_cast<unsigned long long>(snapshot.upload_generation),
          snapshot.upload_data.size(),
          static_cast<uint32_t>(observer_snapshot.content_channel));
    }
  }
  // Sticky channel ownership rides every snapshot touch: the dedicated
  // channels (Bink staging drain, pix inline uploads) refresh tracked
  // resources HERE without passing PopulateTrackedIdentity, and their
  // records rarely flow through the regular harvest at all — run 093's
  // 16 boot-window "content-channel miss" hits were this path.
  if (snapshot.content_channel != FableGuestContentChannel::kNone) {
    tracked.content_channel = snapshot.content_channel;
  }
  const bool upload_generation_already_applied =
      tracked.upload_generation == snapshot.upload_generation &&
      (snapshot.upload_data.empty() ||
       (tracked.texture_upload_has_source && tracked.texture_upload_size != 0 &&
        tracked.texture_upload_hash != 0));
  {
    // Lagging-record probe (video-black diagnosis): a tracked resource whose
    // snapshot generation runs far ahead means this refresh is being starved
    // or rejected — log which.
    static uint64_t logged_upload_generation_lags = 0;
    if (snapshot.upload_generation > tracked.upload_generation + 8u &&
        (++logged_upload_generation_lags <= 16u ||
         (logged_upload_generation_lags % 64u) == 0u)) {
      REXLOG_INFO(
          "Refii plume upload refresh lag #{} guest=0x{:08X} tracked_gen={} "
          "snapshot_gen={} snapshot_bytes={} applied={} device={} texture={}",
          logged_upload_generation_lags, snapshot.guest_address,
          static_cast<unsigned long long>(tracked.upload_generation),
          static_cast<unsigned long long>(snapshot.upload_generation),
          snapshot.upload_data.size(),
          upload_generation_already_applied ? 1 : 0, device ? 1 : 0,
          tracked.guest_texture.texture ? 1 : 0);
    }
  }
  // *** BLACK-SCREEN PROBE (2026-08-04) — WHY THE TONE CURVE IS ZERO. ***
  //
  // RenderDoc frame5350 proved the black screen exactly: the final tonemap
  // binds the CORRECT resolved scene texture (the old 1x1-null bug is fixed)
  // and still emits black, because its 512x1 R32_FLOAT tone-curve LUT
  // (guest 0x40131D90, fetch base 0x1FAA0000) reads
  // `samples=[0.0, 0.0, 0.0, ...]`. A zero curve maps every input to zero.
  //
  // That LUT is written by the guest CPU (`sub_8217E428`, extended-Reinhard)
  // straight into memory — not through an unlock or tile root — so no CAPTURE
  // path ever produces its bytes, `upload_generation` stays 0, and the early
  // return below leaves the host image at its zero-initialised contents.
  //
  // TWO CANDIDATE CAUSES, and they need OPPOSITE fixes, so do not guess:
  //   (a) guest memory HAS the curve and our upload never reads it  -> the fix
  //       is a stated-base upload path (the fetch constant states base, size
  //       and format, so per CLAUDE.md §5 the stated value is available and we
  //       are simply not using it);
  //   (b) guest memory is ALSO zero -> the guest never filled it, and the bug
  //       is upstream in whatever should have called the fill.
  //
  // So read the bytes the fetch constant points at and print them. Capped, and
  // strictly a read — it changes no behaviour. Physical/virtual view mixing is
  // a recurring bug here, so go through GuestPhysicalHostOffset exactly as the
  // GTL content copy does rather than indexing guest_base directly.
  // DO NOT SPEND THE PROBE BUDGET ON RENDER TARGETS (2026-08-04).
  //
  // A render target's pixels are produced by the GPU and live in our host
  // image; guest RAM at its stated base is legitimately empty, so probing one
  // can only ever report all-zero and teach us nothing. Run 603 burned the
  // entire 24-entry cap on 280x180 fmt=6 colour targets, all zero, and never
  // reached the resource the probe exists for — the 512x1 R32F tone curve,
  // which is filled by raw CPU stores with NO Lock/Unlock and is therefore the
  // one thing a call-observing backend cannot see. See
  // [[refii-tone-curve-has-no-upload-event]].
  //
  // Restrict to CPU-filled resources and raise the cap: this is the only
  // instrument that can tell "the guest never wrote it" from "it wrote real
  // content we are failing to upload", and those need opposite fixes.
  if (snapshot.upload_generation == 0 && tracked.texture_guest_base != 0 &&
      !tracked.texture_render_target && !tracked.texture_depth_target) {
    static std::atomic<uint32_t> logged_stated_base_probe{0};
    const uint32_t n =
        logged_stated_base_probe.fetch_add(1, std::memory_order_relaxed) + 1u;
    if (n <= 64u) {
      uint8_t* guest_base = FableGuestMemoryBase();
      const uint32_t base = tracked.texture_guest_base;
      // *** READ THE PHYSICAL VIEW, NOT THE RAW BASE. ***
      // This probe used to read `guest_base + base + GuestPhysicalHostOffset`,
      // and GuestPhysicalHostOffset returns 0 on Linux — so it read
      // `guest_base + P`, which plume_fable_addressing.h documents as the LOW
      // VIRTUAL HEAP: "unrelated memory that is frequently committed and
      // non-zero, so reading it fails silently instead of loudly". Every
      // "the guest never filled it" conclusion drawn from this probe was
      // therefore unsound. The addressable form of a physical base is
      // 0xC0000000 | P.
      const uint32_t probe_address = FableGuestPhysicalViewAddress(base);
      // Sample ACROSS the resource, not just its first 16 bytes. Four words at
      // offset 0 cannot distinguish a real curve from a poison fill or a wrong
      // stride — and reading only the head is how a texture with a bright band
      // got called "black" from nine samples that all landed on black.
      //
      // Values are decoded as BIG-ENDIAN (LoadGuestU32 byte-swaps) and printed
      // as float, because the tone curve is R32F: a raw 0000C0FF is really
      // 0xFFC00000 = -NaN, which is NOT a usable curve value and must never be
      // uploaded as if it were.
      const uint32_t texel_count =
          std::max(1u, tracked.texture_width) * std::max(1u, tracked.texture_height);
      const uint32_t probe_bytes = texel_count * 4u;
      if (guest_base != nullptr &&
          refii::renderer::bink::IsReadableGuestByteRange(guest_base,
                                                          probe_address,
                                                          probe_bytes)) {
        // Eight evenly spaced samples, first and last inclusive.
        std::array<uint32_t, 8> sample_index{};
        std::array<uint32_t, 8> raw{};
        std::array<float, 8> as_float{};
        uint32_t nonzero = 0;
        uint32_t nan_or_inf = 0;
        for (uint32_t i = 0; i < sample_index.size(); ++i) {
          const uint32_t idx =
              texel_count <= 1u
                  ? 0u
                  : (i * (texel_count - 1u)) / (sample_index.size() - 1u);
          sample_index[i] = idx;
          raw[i] = refii::renderer::bink::LoadGuestU32(guest_base,
                                                       probe_address + idx * 4u);
          float f = 0.0f;
          std::memcpy(&f, &raw[i], sizeof(f));
          as_float[i] = f;
          if (raw[i] != 0u) {
            ++nonzero;
          }
          if (!std::isfinite(f)) {
            ++nan_or_inf;
          }
        }
        REXLOG_INFO(
            "Refii plume STATED-BASE upload skipped #{} guest=0x{:08X} "
            "base=0x{:08X} {}x{} fmt={} texels={} tracked_gen={} — "
            "[{}]={:08X}({:g}) [{}]={:08X}({:g}) [{}]={:08X}({:g}) "
            "[{}]={:08X}({:g}) [{}]={:08X}({:g}) [{}]={:08X}({:g}) "
            "[{}]={:08X}({:g}) [{}]={:08X}({:g}) nonzero={}/8 nonfinite={}/8 "
            "(all-zero => guest never filled it; finite+varying => real "
            "content we are failing to upload; NaN/Inf => poison, do NOT "
            "upload)",
            n, snapshot.guest_address, base, tracked.texture_width,
            tracked.texture_height, tracked.texture_guest_format, texel_count,
            static_cast<unsigned long long>(tracked.upload_generation),
            sample_index[0], raw[0], as_float[0], sample_index[1], raw[1],
            as_float[1], sample_index[2], raw[2], as_float[2], sample_index[3],
            raw[3], as_float[3], sample_index[4], raw[4], as_float[4],
            sample_index[5], raw[5], as_float[5], sample_index[6], raw[6],
            as_float[6], sample_index[7], raw[7], as_float[7], nonzero,
            nan_or_inf);
      } else {
        REXLOG_INFO(
            "Refii plume STATED-BASE upload skipped #{} guest=0x{:08X} "
            "base=0x{:08X} — guest memory UNREADABLE at the stated base "
            "(guest_base={})",
            n, snapshot.guest_address, base,
            guest_base != nullptr ? "ok" : "null");
      }
    }
  }
  if (!device || !tracked.guest_texture.texture ||
      snapshot.upload_generation == 0 ||
      upload_generation_already_applied) {
    return true;
  }
  // From here the chosen snapshot's bytes (re)define this record's upload
  // state; remember which authority they came from — and, for GTL-fed
  // refreshes, the store generation these bytes carried, so the unadvanced-
  // clock guard above can tell a fresh store write from the same entry
  // re-serving itself.
  tracked.texture_upload_gtl_content = gtl_content;
  if (gtl_content && gtl_store_generation >
                         tracked.texture_upload_gtl_generation) {
    tracked.texture_upload_gtl_generation = gtl_store_generation;
  }
  // Content ownership = last writer, across domains. A GPU write only holds
  // ownership against CPU captures that PRECEDE it (generation at or below
  // the level seen when the GPU wrote); a newer CPU capture means the CPU
  // wrote the memory afterwards and reclaims the resource — this is how the
  // GUI text surfaces work on console (GPU copies the page background in,
  // CPU rasterizes text over it). Blocking on draw_content_serial alone
  // deadlocked them black after the first GPU copy. Depth always rejects.
  const bool gpu_write_owns_content =
      tracked.draw_content_serial != 0 &&
      snapshot.upload_generation <= tracked.upload_generation_at_gpu_write;
  if (tracked.texture_depth_target ||
      (tracked.texture_render_target &&
       (gpu_write_owns_content || snapshot.upload_data.empty()))) {
    tracked.texture_upload_decoded = false;
    tracked.texture_upload_has_source = false;
    tracked.texture_upload_pending = false;
    return true;
  }
  if (!IsFableDirectTextureUploadAllowed(snapshot, tracked)) {
    // Partial-region capture (e.g. a locked text-row strip of the GUI text
    // surfaces): the bytes can't fill this layout. Guest memory holds the
    // complete image — ask the observer to recapture the FULL surface from
    // the record's own fetch dwords; the next harvest delivers a full-size
    // snapshot that passes this gate. One request per generation.
    if (!snapshot.upload_data.empty() &&
        snapshot.upload_generation !=
            tracked.partial_recapture_requested_generation) {
      tracked.partial_recapture_requested_generation =
          snapshot.upload_generation;
      // This runs under the sync/harvest locks — a direct observer call can
      // never take the mutex here (try_lock always fails). Queue the address
      // and let the frame boundary run the recapture lock-free. The snapshot
      // fetch IS the record's stale-tenant layout (its "full" capture is the
      // short read we just rejected), so synthesize the fetch from the
      // tracked geometry — the extent the renderer actually samples.
      namespace xenos_synth = rex::graphics::xenos;
      const uint32_t synth_base = PlumeGuestTextureBaseAddress(tracked);
      const uint32_t synth_format = PlumeGuestTextureGuestFormat(tracked);
      const uint32_t synth_width = PlumeGuestTextureWidth(tracked);
      const uint32_t synth_height = PlumeGuestTextureHeight(tracked);
      std::array<uint32_t, 6> recapture_dwords =
          PreferredTextureFetchDwords(snapshot.texture);
      const rex::graphics::FormatInfo* synth_info =
          rex::graphics::FormatInfo::Get(
              static_cast<xenos_synth::TextureFormat>(synth_format));
      // Layout math divides by the format's block dimensions — a format
      // without valid block info (exotic/video formats) must fall back to
      // the snapshot fetch instead of synthesizing (SIGFPE otherwise).
      if (synth_base != 0 && synth_width != 0 && synth_height != 0 &&
          synth_info != nullptr && synth_info->block_width != 0 &&
          synth_info->block_height != 0 &&
          synth_info->bytes_per_block() != 0 &&
          PlumeGuestTexturePitch(tracked) != 0) {
        xenos_synth::xe_gpu_texture_fetch_t synth = {};
        synth.type = xenos_synth::FetchConstantType::kTexture;
        synth.pitch = std::max<uint32_t>(
            1u, PlumeGuestTexturePitch(tracked) >> 5);
        synth.tiled = tracked.texture_guest_tiled ? 1u : 0u;
        synth.format = static_cast<xenos_synth::TextureFormat>(synth_format);
        synth.endianness =
            (synth_info && synth_info->block_width == 1 &&
             synth_info->bytes_per_block() >= 4)
                ? xenos_synth::Endian::k8in32
                : xenos_synth::Endian::k8in16;
        synth.base_address = synth_base >> 12;
        synth.size_2d.width = synth_width - 1;
        synth.size_2d.height = synth_height - 1;
        synth.dimension = xenos_synth::DataDimension::k2DOrStacked;
        recapture_dwords = {synth.dword_0, synth.dword_1, synth.dword_2,
                            synth.dword_3, synth.dword_4, synth.dword_5};
      }
      QueuePartialUploadRecapture(snapshot.guest_address, recapture_dwords);
      static uint32_t logged_partial_recapture_requests = 0;
      if (logged_partial_recapture_requests < 24u) {
        REXLOG_INFO(
            "Refii plume partial-upload full recapture queued #{} "
            "guest=0x{:08X} snapshot_gen={} bytes={} {}x{} fmt={}",
            ++logged_partial_recapture_requests, snapshot.guest_address,
            static_cast<unsigned long long>(snapshot.upload_generation),
            snapshot.upload_data.size(), PlumeGuestTextureWidth(tracked),
            PlumeGuestTextureHeight(tracked),
            static_cast<uint32_t>(PlumeGuestTextureFormat(tracked)));
      }
    }
    tracked.texture_upload_decoded = false;
    tracked.texture_upload_has_source = false;
    tracked.texture_upload_pending = false;
    return true;
  }

  const TextureUploadClock::time_point hash_begin =
      TextureUploadClock::now();
  const uint64_t source_content_hash =
      snapshot.texture.upload_hash != 0
          ? snapshot.texture.upload_hash
          : HashUploadBytesFnv1a64(snapshot.upload_data.data(),
                                   snapshot.upload_data.size());
  const TextureUploadClock::time_point hash_end = TextureUploadClock::now();
  const uint64_t source_hash = HashTextureUploadIdentity(
      snapshot, tracked, snapshot.upload_data.size(), source_content_hash);
  if (!tracked.texture_upload_pending &&
      tracked.texture_upload_source_hash == source_hash &&
      tracked.texture_upload_source_size == snapshot.upload_data.size() &&
      tracked.texture_upload_hash != 0) {
    tracked.upload_generation = snapshot.upload_generation;
    return true;
  }

  R8LinearTextureUploadLayout r8_linear_layout;
  R8TiledUploadTiming r8_linear_timing;
  r8_linear_timing.hash_ms = TextureUploadElapsedMs(hash_begin, hash_end);
  const TextureUploadClock::time_point r8_linear_layout_begin =
      TextureUploadClock::now();
  if (TryGetR8LinearTextureUploadLayout(snapshot, tracked,
                                        r8_linear_layout)) {
    const TextureUploadClock::time_point r8_linear_layout_end =
        TextureUploadClock::now();
    r8_linear_timing.layout_ms =
        TextureUploadElapsedMs(r8_linear_layout_begin, r8_linear_layout_end);
    const TextureUploadClock::time_point r8_linear_total_begin =
        TextureUploadClock::now();
    const uint64_t upload_hash = HashTextureUploadIdentity(
        snapshot, tracked, r8_linear_layout.upload_size, source_content_hash);
    if (!tracked.texture_upload_pending &&
        tracked.texture_upload_hash == upload_hash) {
      tracked.upload_generation = snapshot.upload_generation;
      return true;
    }

    tracked.texture_upload_published = false;
    tracked.texture_upload_format = PlumeGuestTextureFormat(tracked);
    tracked.texture_upload_row_width = TextureRowWidthForPitch(
        PlumeGuestTextureFormat(tracked), r8_linear_layout.row_pitch_bytes);
    tracked.texture_upload_subresources = 1u;
    tracked.texture_upload_subresource_bytes = r8_linear_layout.upload_size;
    tracked.texture_upload_size = r8_linear_layout.upload_size;
    tracked.texture_upload_decoded = true;
    tracked.texture_upload_has_source = true;
    if (TryStageR8LinearTextureUploadInFrameSlab(
            device, snapshot, tracked, r8_linear_layout, &r8_linear_timing)) {
      const TextureUploadClock::time_point r8_linear_total_end =
          TextureUploadClock::now();
      r8_linear_timing.total_ms =
          TextureUploadElapsedMs(r8_linear_total_begin, r8_linear_total_end);
      tracked.upload_generation = snapshot.upload_generation;
      tracked.texture_upload_hash = upload_hash;
      tracked.texture_upload_source_hash = source_hash;
      tracked.texture_upload_source_size = snapshot.upload_data.size();
      tracked.texture_upload_pending = true;
      tracked.texture_upload_new_resource = false;
      if (r8_linear_timing.total_ms >= 2.0 &&
          g_fable_r8_upload_timing_log_count < 128u) {
        REXLOG_INFO(
            "Refii plume linear R8 upload timing #{} guest=0x{:08X} size={}x{} "
            "upload={} source={} guest_pitch={} gen={} hash_ms={:.3f} "
            "layout_ms={:.3f} map_ms={:.3f} writer_ms={:.3f} "
            "clear_ms={:.3f} copy_ms={:.3f} debug_ms={:.3f} "
            "unmap_ms={:.3f} total_ms={:.3f} rows={}",
            ++g_fable_r8_upload_timing_log_count, tracked.guest_address,
            r8_linear_layout.width, r8_linear_layout.height,
            static_cast<uint64_t>(r8_linear_layout.upload_size),
            static_cast<uint64_t>(snapshot.upload_data.size()),
            r8_linear_layout.guest_row_pitch_bytes,
            snapshot.upload_generation, r8_linear_timing.hash_ms,
            r8_linear_timing.layout_ms, r8_linear_timing.slab_map_ms,
            r8_linear_timing.slab_writer_ms, r8_linear_timing.clear_ms,
            r8_linear_timing.copy_ms, r8_linear_timing.debug_ms,
            r8_linear_timing.slab_unmap_ms, r8_linear_timing.total_ms,
            r8_linear_timing.run_count);
      }
      return true;
    }
  }

  Dxt3aR8TextureUploadLayout dxt3a_r8_layout;
  R8TiledUploadTiming dxt3a_r8_timing;
  dxt3a_r8_timing.hash_ms = TextureUploadElapsedMs(hash_begin, hash_end);
  const TextureUploadClock::time_point dxt3a_r8_layout_begin =
      TextureUploadClock::now();
  if (TryGetDxt3aR8TextureUploadLayout(snapshot, tracked,
                                       dxt3a_r8_layout)) {
    const TextureUploadClock::time_point dxt3a_r8_layout_end =
        TextureUploadClock::now();
    dxt3a_r8_timing.layout_ms =
        TextureUploadElapsedMs(dxt3a_r8_layout_begin, dxt3a_r8_layout_end);
    const TextureUploadClock::time_point dxt3a_r8_total_begin =
        TextureUploadClock::now();
    const uint64_t upload_hash = HashTextureUploadIdentity(
        snapshot, tracked, dxt3a_r8_layout.upload_size, source_content_hash);
    if (!tracked.texture_upload_pending &&
        tracked.texture_upload_hash == upload_hash) {
      tracked.upload_generation = snapshot.upload_generation;
      return true;
    }

    tracked.texture_upload_published = false;
    tracked.texture_upload_format = PlumeGuestTextureFormat(tracked);
    tracked.texture_upload_row_width = TextureRowWidthForPitch(
        PlumeGuestTextureFormat(tracked), dxt3a_r8_layout.row_pitch_bytes);
    tracked.texture_upload_subresources = 1u;
    tracked.texture_upload_subresource_bytes = dxt3a_r8_layout.upload_size;
    tracked.texture_upload_size = dxt3a_r8_layout.upload_size;
    tracked.texture_upload_decoded = true;
    tracked.texture_upload_has_source = true;
    if (TryStageDxt3aR8TextureUploadInFrameSlab(
            device, snapshot, tracked, dxt3a_r8_layout, &dxt3a_r8_timing)) {
      const TextureUploadClock::time_point dxt3a_r8_total_end =
          TextureUploadClock::now();
      dxt3a_r8_timing.total_ms =
          TextureUploadElapsedMs(dxt3a_r8_total_begin, dxt3a_r8_total_end);
      tracked.upload_generation = snapshot.upload_generation;
      tracked.texture_upload_hash = upload_hash;
      tracked.texture_upload_source_hash = source_hash;
      tracked.texture_upload_source_size = snapshot.upload_data.size();
      tracked.texture_upload_pending = true;
      tracked.texture_upload_new_resource = false;
      if (dxt3a_r8_timing.total_ms >= 2.0 &&
          g_fable_r8_upload_timing_log_count < 128u) {
        REXLOG_INFO(
            "Refii plume DXT3A R8 upload timing #{} guest=0x{:08X} size={}x{} "
            "upload={} source={} gen={} hash_ms={:.3f} layout_ms={:.3f} "
            "runs_ms={:.3f} map_ms={:.3f} writer_ms={:.3f} "
            "clear_ms={:.3f} copy_ms={:.3f} debug_ms={:.3f} "
            "unmap_ms={:.3f} total_ms={:.3f} blocks={}",
            ++g_fable_r8_upload_timing_log_count, tracked.guest_address,
            dxt3a_r8_layout.width, dxt3a_r8_layout.height,
            static_cast<uint64_t>(dxt3a_r8_layout.upload_size),
            static_cast<uint64_t>(snapshot.upload_data.size()),
            snapshot.upload_generation, dxt3a_r8_timing.hash_ms,
            dxt3a_r8_timing.layout_ms, dxt3a_r8_timing.runs_ms,
            dxt3a_r8_timing.slab_map_ms, dxt3a_r8_timing.slab_writer_ms,
            dxt3a_r8_timing.clear_ms, dxt3a_r8_timing.copy_ms,
            dxt3a_r8_timing.debug_ms, dxt3a_r8_timing.slab_unmap_ms,
            dxt3a_r8_timing.total_ms, dxt3a_r8_timing.run_count);
      }
      return true;
    }
  }

  R8TiledTextureUploadLayout r8_tiled_layout;
  R8TiledUploadTiming r8_timing;
  r8_timing.hash_ms = TextureUploadElapsedMs(hash_begin, hash_end);
  const TextureUploadClock::time_point r8_layout_begin =
      TextureUploadClock::now();
  if (TryGetR8TiledTextureUploadLayout(snapshot, tracked, r8_tiled_layout)) {
    const TextureUploadClock::time_point r8_layout_end =
        TextureUploadClock::now();
    r8_timing.layout_ms =
        TextureUploadElapsedMs(r8_layout_begin, r8_layout_end);
    const TextureUploadClock::time_point r8_total_begin =
        TextureUploadClock::now();
    const uint64_t upload_hash = HashTextureUploadIdentity(
        snapshot, tracked, r8_tiled_layout.upload_size, source_content_hash);
    if (!tracked.texture_upload_pending &&
        tracked.texture_upload_hash == upload_hash) {
      tracked.upload_generation = snapshot.upload_generation;
      return true;
    }

    tracked.texture_upload_published = false;
    tracked.texture_upload_format = PlumeGuestTextureFormat(tracked);
    tracked.texture_upload_row_width = TextureRowWidthForPitch(
        PlumeGuestTextureFormat(tracked), r8_tiled_layout.row_pitch_bytes);
    tracked.texture_upload_subresources = 1u;
    tracked.texture_upload_subresource_bytes = r8_tiled_layout.upload_size;
    tracked.texture_upload_size = r8_tiled_layout.upload_size;
    tracked.texture_upload_decoded = true;
    tracked.texture_upload_has_source = true;
    if (TryStageR8TiledTextureUploadInFrameSlab(
            device, snapshot, tracked, r8_tiled_layout, &r8_timing)) {
      const TextureUploadClock::time_point r8_total_end =
          TextureUploadClock::now();
      r8_timing.total_ms =
          TextureUploadElapsedMs(r8_total_begin, r8_total_end);
      tracked.upload_generation = snapshot.upload_generation;
      tracked.texture_upload_hash = upload_hash;
      tracked.texture_upload_source_hash = source_hash;
      tracked.texture_upload_source_size = snapshot.upload_data.size();
      tracked.texture_upload_pending = true;
      tracked.texture_upload_new_resource = false;
      if (r8_timing.total_ms >= 4.0 &&
          g_fable_r8_upload_timing_log_count < 128u) {
        REXLOG_INFO(
            "Refii plume R8 upload timing #{} guest=0x{:08X} size={}x{} "
            "upload={} source={} gen={} hash_ms={:.3f} layout_ms={:.3f} "
            "runs_ms={:.3f} map_ms={:.3f} writer_ms={:.3f} "
            "clear_ms={:.3f} copy_ms={:.3f} debug_ms={:.3f} "
            "unmap_ms={:.3f} total_ms={:.3f} runs={}",
            ++g_fable_r8_upload_timing_log_count, tracked.guest_address,
            r8_tiled_layout.width, r8_tiled_layout.height,
            static_cast<uint64_t>(r8_tiled_layout.upload_size),
            static_cast<uint64_t>(snapshot.upload_data.size()),
            snapshot.upload_generation, r8_timing.hash_ms,
            r8_timing.layout_ms, r8_timing.runs_ms, r8_timing.slab_map_ms,
            r8_timing.slab_writer_ms, r8_timing.clear_ms,
            r8_timing.copy_ms, r8_timing.debug_ms,
            r8_timing.slab_unmap_ms, r8_timing.total_ms,
            r8_timing.run_count);
      }
      return true;
    }
  } else {
    const TextureUploadClock::time_point r8_layout_end =
        TextureUploadClock::now();
    r8_timing.layout_ms =
        TextureUploadElapsedMs(r8_layout_begin, r8_layout_end);
  }

  const PlumeTextureUploadDecodeCacheKey decode_cache_key =
      TextureUploadDecodeCacheKeyForTrackedResource(
          tracked, source_hash, snapshot.upload_data.size());
  auto& decode_cache =
      g_plume_resources.frame_texture_upload_decode_caches
          [g_plume_resources.frame_texture_upload_slab_index %
           g_plume_resources.frame_texture_upload_decode_caches.size()];
  const PlumeTextureUploadDecodeCacheEntry* cache_entry = nullptr;
  auto cache_it = decode_cache.find(decode_cache_key);
  if (cache_it != decode_cache.end()) {
    cache_entry = &cache_it->second;
    ApplyTextureUploadDecodeCacheEntry(tracked, *cache_entry);
  }

  uint64_t upload_hash = cache_entry ? cache_entry->upload_hash : 0;
  std::vector<uint8_t> upload_bytes;
  if (!cache_entry) {
    if (!PrepareTextureUploadBytes(snapshot, tracked, upload_bytes)) {
      return false;
    }
    upload_hash = HashTextureUploadIdentity(snapshot, tracked,
                                            upload_bytes.size(),
                                            source_content_hash);
  }
  if (!tracked.texture_upload_pending &&
      tracked.texture_upload_hash == upload_hash) {
    tracked.upload_generation = snapshot.upload_generation;
    return true;
  }

  const std::vector<uint8_t>* bytes_to_stage = nullptr;
  if (cache_entry) {
    bytes_to_stage = &cache_entry->bytes;
  } else if (ShouldBypassTextureDecodeCacheForTransientUpload(
                 snapshot, tracked, upload_bytes.size())) {
    bytes_to_stage = &upload_bytes;
  } else {
    if (decode_cache.size() >= kMaxFrameTextureUploadDecodeCacheEntries) {
      decode_cache.clear();
    }
    auto [inserted_it, inserted] = decode_cache.emplace(
        decode_cache_key,
        TextureUploadDecodeCacheEntryForTrackedResource(
            tracked, std::move(upload_bytes), upload_hash));
    (void)inserted;
    cache_entry = &inserted_it->second;
    bytes_to_stage = &cache_entry->bytes;
  }

  if (!bytes_to_stage ||
      !TryStageTextureUploadInFrameSlab(device, tracked, *bytes_to_stage)) {
    return false;
  }
  tracked.upload_generation = snapshot.upload_generation;
  tracked.texture_upload_hash = upload_hash;
  tracked.texture_upload_source_hash = source_hash;
  tracked.texture_upload_source_size = snapshot.upload_data.size();
  tracked.texture_upload_pending = true;
  tracked.texture_upload_new_resource = false;
  UpdateTrackedUploadDebugSample(tracked, bytes_to_stage->data(),
                                 bytes_to_stage->size());
  return true;
}

void UpdateTrackedUploadDebugSample(PlumeTrackedResource& tracked,
                                    const uint8_t* data, size_t size) {
  tracked.debug_first_nonzero_offset = UINT32_MAX;
  tracked.debug_first_nonzero_sample.fill(0);
  tracked.debug_first_nonzero_sample_size = 0;
  const size_t scan_size = std::min<size_t>(size, 64u * 1024u);
  for (size_t i = 0; i < scan_size; ++i) {
    if (data[i] == 0) {
      continue;
    }
    tracked.debug_first_nonzero_offset = static_cast<uint32_t>(i);
    tracked.debug_first_nonzero_sample_size = static_cast<uint32_t>(
        std::min<size_t>(tracked.debug_first_nonzero_sample.size(), size - i));
    std::memcpy(tracked.debug_first_nonzero_sample.data(), data + i,
                tracked.debug_first_nonzero_sample_size);
    break;
  }

  tracked.debug_upload_sample.assign(
      data, data + std::min<size_t>(size, kTrackedBufferDebugSampleBytes));
}

void UploadTrackedBuffer(PlumeTrackedResource& tracked,
                         const RendererTrackedResourceSnapshot& snapshot) {
  if (!tracked.guest_buffer.buffer || snapshot.upload_generation == 0 ||
      snapshot.upload_data.empty()) {
    return;
  }

  const size_t copy_size =
      std::min<size_t>(snapshot.upload_data.size(), tracked.size);
  if (copy_size == 0) {
    return;
  }

  auto* dst = static_cast<uint8_t*>(tracked.guest_buffer.buffer->map());
  if (!dst) {
    REXLOG_WARN(
        "Refii plume resource manager could not map {} guest=0x{:08X} "
        "upload={} size={}",
        TrackedResourceKindName(snapshot.kind), snapshot.guest_address,
        snapshot.upload_generation, copy_size);
    return;
  }

  std::memcpy(dst, snapshot.upload_data.data(), copy_size);
  plume::RenderRange written_range(0, copy_size);
  tracked.guest_buffer.buffer->unmap(0, &written_range);
  tracked.upload_generation = snapshot.upload_generation;
  UpdateTrackedUploadDebugSample(tracked, snapshot.upload_data.data(),
                                 copy_size);
  if (snapshot.kind == RendererTrackedResourceKind::kIndexBuffer) {
    tracked.index_shadow_upload = snapshot.upload_data;
  } else {
    tracked.index_shadow_upload.clear();
  }

  if (RendererVerboseTracingEnabled()) {
    REXLOG_INFO(
        "Refii plume resource manager uploaded {} guest=0x{:08X} bytes={} "
        "generation={}",
        TrackedResourceKindName(snapshot.kind), snapshot.guest_address,
        copy_size, snapshot.upload_generation);
  }
}
#endif

}  // namespace refii::renderer
