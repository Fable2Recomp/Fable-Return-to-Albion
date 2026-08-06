#include "runtime/refii_renderer/plume/fable/video/fable_pix_video_uploads.h"

#include "runtime/refii_renderer/bik/bink_guest_memory.h"
#include "runtime/refii_renderer/plume/fable/resource/fable_texture_fetch.h"

#include <algorithm>
#include <cstddef>
#include <cstring>
#include <memory>
#include <vector>

#include <rex/logging.h>

namespace refii::renderer {
namespace {

constexpr uint32_t kMinPixVideoInlineUploadAddress = 0x10000u;
constexpr uint32_t kMaxPixVideoInlineUploadSize = 64u << 20;

uint64_t FablePixVideoHashBytesFnv1a64(const uint8_t* data, size_t size) {
  if (data == nullptr || size == 0) {
    return 0;
  }

  uint64_t hash = 14695981039346656037ull;
  for (size_t index = 0; index < size; ++index) {
    hash ^= data[index];
    hash *= 1099511628211ull;
  }
  return hash;
}

bool FablePixVideoFetchHasAnyValue(
    const std::array<uint32_t, kFablePixVideoFetchConstantDwords>& fetch_dwords) {
  return std::any_of(fetch_dwords.begin(), fetch_dwords.end(),
                     [](uint32_t value) { return value != 0; });
}

}  // namespace

bool FablePixVideoInlineUploadSourceReadable(uint8_t* base,
                                             uint32_t base_address,
                                             uint32_t byte_size) {
  return base != nullptr && base_address >= kMinPixVideoInlineUploadAddress &&
         byte_size != 0 && byte_size <= kMaxPixVideoInlineUploadSize &&
         base_address <= UINT32_MAX - byte_size &&
         bink::IsReadableGuestByteRange(base, base_address, byte_size);
}

RendererInlineTextureUploadSnapshot FablePixVideoBuildInlineUploadSnapshot(
    uint32_t texture_handle,
    const std::array<uint32_t, 6>& fetch_dwords,
    uint32_t base_address,
    uint32_t byte_size,
    uint8_t* base,
    uint64_t upload_serial) {
  RendererInlineTextureUploadSnapshot upload;
  if (texture_handle == 0 || upload_serial == 0 ||
      !FablePixVideoInlineUploadSourceReadable(base, base_address, byte_size)) {
    return upload;
  }

  const uint8_t* source =
      base + base_address + bink::GuestPhysicalHostOffset(base_address);
  auto data = std::make_shared<std::vector<uint8_t>>(byte_size);
  std::memcpy(data->data(), source, data->size());

  // Green-strip diagnosis: the attract video draws as strips, each with its
  // own plane set from one packed buffer — a strip whose chroma regions are
  // zero renders solid green. Census the packed buffer's Y/U/V-region
  // content so the failing strip's upload is identifiable in the log.
  {
    static uint32_t logged_pix_video_upload_census = 0;
    if (logged_pix_video_upload_census < 32u ||
        (logged_pix_video_upload_census % 512u) == 0u) {
      const uint8_t* census_source =
          base + base_address + bink::GuestPhysicalHostOffset(base_address);
      // I420 layout: Y = 2/3 of the buffer, U and V = 1/6 each.
      const uint32_t y_span = (byte_size * 2u) / 3u;
      const uint32_t uv_span = byte_size / 6u;
      auto region_nonzero = [&](uint32_t offset, uint32_t span) {
        span = std::min(span, byte_size - std::min(byte_size, offset));
        for (uint32_t i = 0; i < span; i += 64u) {
          if (census_source[offset + i] != 0) {
            return 1;
          }
        }
        return 0;
      };
      REXLOG_INFO(
          "Refii pix video upload census #{} handle=0x{:08X} base=0x{:08X} "
          "size={} y_nz={} u_nz={} v_nz={} serial={}",
          logged_pix_video_upload_census, texture_handle, base_address,
          byte_size, region_nonzero(0, y_span),
          region_nonzero(y_span, uv_span),
          region_nonzero(y_span + uv_span, uv_span),
          static_cast<unsigned long long>(upload_serial));
    }
    ++logged_pix_video_upload_census;
  }

  upload.address = texture_handle;
  upload.parent_address = texture_handle;
  upload.base_address = base_address;
  upload.byte_size = byte_size;
  upload.raw_fetch_dwords = fetch_dwords;
  upload.normalized_fetch_dwords =
      FableNormalizeTextureFetchDwords(fetch_dwords);
  upload.upload_generation = upload_serial;
  upload.upload_hash = FablePixVideoHashBytesFnv1a64(data->data(), data->size());
  upload.serial = upload_serial;
  upload.data = std::move(data);
  return upload;
}

void FablePixVideoBindInlineUploadPlanes(
    RendererDrawEventSnapshot& event,
    uint32_t texture_handle,
    const std::array<uint32_t, kFablePixVideoFetchConstantDwords>& fetch_dwords,
    const RendererInlineTextureUploadSnapshot& upload) {
  for (uint32_t slot = 0; slot < kFablePixVideoPlaneCount; ++slot) {
    event.textures[slot] = texture_handle;
    if (upload.ready()) {
      event.pix_video_planes[slot] = upload;
    }

    const uint32_t fetch_dword = slot * kFablePixVideoFetchConstantDwords;
    if (fetch_dword + kFablePixVideoFetchConstantDwords <=
        event.fetch_constants.size()) {
      std::copy(fetch_dwords.begin(), fetch_dwords.end(),
                event.fetch_constants.begin() + fetch_dword);
    }
  }
}

FableGuestResourceSnapshot FablePixVideoBuildInlineUploadResourceSnapshot(
    const RendererInlineTextureUploadSnapshot& upload) {
  FableGuestResourceSnapshot snapshot;
  if (!upload.ready()) {
    return snapshot;
  }

  snapshot.kind = FableGuestResourceKind::kTexture;
  snapshot.guest_address = upload.address;
  snapshot.parent_address = upload.parent_address;
  snapshot.texture.object_address = upload.address;
  snapshot.texture.parent_address = upload.parent_address;
  snapshot.texture.base_address = upload.base_address;
  snapshot.texture.byte_size =
      upload.byte_size != 0 ? upload.byte_size
                            : static_cast<uint32_t>(upload.data->size());
  snapshot.texture.raw_fetch_dwords =
      FablePixVideoFetchHasAnyValue(upload.raw_fetch_dwords)
          ? upload.raw_fetch_dwords
          : upload.normalized_fetch_dwords;
  snapshot.texture.normalized_fetch_dwords =
      FablePixVideoFetchHasAnyValue(upload.normalized_fetch_dwords)
          ? upload.normalized_fetch_dwords
          : snapshot.texture.raw_fetch_dwords;
  snapshot.texture.upload_generation = upload.upload_generation;
  snapshot.texture.upload_hash = upload.upload_hash;
  snapshot.upload_generation = upload.upload_generation;
  snapshot.upload_data.assign(upload.data->begin(), upload.data->end());
  snapshot.serial = upload.serial;
  // Pix channel owns this record's content: decode-event uploads are the
  // only valid writers, so generic re-capture paths must skip it.
  snapshot.content_channel = FableGuestContentChannel::kPix;
  return snapshot;
}

bool FablePixVideoApplyInlineUploadToResource(
    RendererResourceRecord& record,
    const RendererInlineTextureUploadSnapshot& upload) {
  if (!upload.ready()) {
    return false;
  }

  SetGuestResourceIdentity(record, RendererResourceKind::kTexture,
                           upload.address, upload.parent_address);
  GuestTextureRecord texture = GuestTextureRecordFor(record);
  texture.object_address = upload.address;
  texture.parent_address = upload.parent_address;
  texture.base_address = upload.base_address;
  texture.byte_size = std::max<uint32_t>(texture.byte_size, upload.byte_size);
  texture.raw_fetch_dwords = upload.raw_fetch_dwords;
  texture.normalized_fetch_dwords =
      FableTextureFetchHasDwords(upload.normalized_fetch_dwords)
          ? upload.normalized_fetch_dwords
          : FableNormalizeTextureFetchDwords(upload.raw_fetch_dwords);
  texture.upload_generation = upload.upload_generation;
  ApplyGuestTextureRecord(record, texture);

  record.upload_data.assign(upload.data->begin(), upload.data->end());
  record.upload_generation = upload.upload_generation;
  record.raw.hash = upload.upload_hash != 0
                        ? upload.upload_hash
                        : FablePixVideoHashBytesFnv1a64(
                              record.upload_data.data(),
                              record.upload_data.size());
  record.serial = upload.serial;
  record.content_channel = FableGuestContentChannel::kPix;
  return true;
}

}  // namespace refii::renderer
