#include "runtime/refii_renderer/plume/fable/video/fable_bink_plane_readiness.h"

#include "runtime/refii_renderer/plume/fable/resource/fable_direct_resource_readiness.h"
#include "runtime/refii_renderer/plume/fable/resource/fable_texture_fetch.h"

namespace refii::renderer {
namespace {

void AddFableBinkWrapperPlaneInterestAddress(
    std::array<uint32_t, kFableBinkVideoPlaneCount>& addresses,
    uint32_t& count,
    uint32_t address) {
  if (address == 0 || count >= addresses.size()) {
    return;
  }
  for (uint32_t index = 0; index < count; ++index) {
    if (addresses[index] == address) {
      return;
    }
  }
  addresses[count++] = address;
}

}  // namespace

bool FableVideoBinkPlaneHasDecodedUpload(
    const PlumeTrackedResource& resource) {
  if (!FableTrackedResourceHasMeaningfulTextureUpload(resource) ||
      resource.texture_upload_hash == 0) {
    return false;
  }
  return resource.texture_upload_hash !=
         FableZeroUploadHashForSize(resource.texture_upload_source_size);
}

bool FableVideoBinkPlaneChromaShapeMatchesLuma(
    const PlumeTrackedResource& y,
    const PlumeTrackedResource& cr,
    const PlumeTrackedResource& cb) {
  return PlumeGuestTextureWidth(cr) == PlumeGuestTextureWidth(cb) &&
         PlumeGuestTextureHeight(cr) == PlumeGuestTextureHeight(cb) &&
         PlumeGuestTextureWidth(cr) * 2u == PlumeGuestTextureWidth(y) &&
         PlumeGuestTextureHeight(cr) * 2u == PlumeGuestTextureHeight(y);
}

bool FableVideoBinkPlaneResourcesReadyDecoded(
    const PlumeTrackedResource& y,
    const PlumeTrackedResource& cr,
    const PlumeTrackedResource& cb) {
  return PlumeGuestImageReady(y) && PlumeGuestImageReady(cr) &&
         PlumeGuestImageReady(cb) && IsImageResource(y.kind) &&
         IsImageResource(cr.kind) && IsImageResource(cb.kind) &&
         FableVideoBinkPlaneHasDecodedUpload(y) &&
         FableVideoBinkPlaneHasDecodedUpload(cr) &&
         FableVideoBinkPlaneHasDecodedUpload(cb);
}

bool FableVideoBinkWrapperPlaneSetReady(
    const FableVideoBinkWrapperPlaneSetReadinessFacts& facts,
    uint32_t* missing_slot,
    uint32_t* missing_guest) {
  if (!facts.wrapper_candidate || facts.command_plane_set_ready) {
    return true;
  }

  for (uint32_t slot = 0; slot < kFableBinkVideoPlaneCount; ++slot) {
    if (facts.fetch_decoded[slot] && facts.guests[slot] != 0 &&
        facts.resource_image_ready[slot]) {
      continue;
    }
    if (missing_slot) {
      *missing_slot = slot;
    }
    if (missing_guest) {
      *missing_guest = facts.guests[slot];
    }
    return false;
  }

  if (facts.plane_address_set_ready) {
    return true;
  }
  if (missing_slot) {
    *missing_slot = 0u;
  }
  if (missing_guest) {
    *missing_guest = facts.guests[0];
  }
  return false;
}

FableVideoBinkWrapperPlaneResourceInterests
FableVideoCollectBinkWrapperPlaneResourceInterests(
    const FableVideoBinkWrapperPlaneResourceInterestFacts& facts) {
  FableVideoBinkWrapperPlaneResourceInterests interests;
  if (!facts.wrapper_candidate) {
    return interests;
  }

  interests.include_texture_slot_identity = true;
  AddFableBinkWrapperPlaneInterestAddress(
      interests.resource_addresses, interests.resource_count, facts.guest);
  AddFableBinkWrapperPlaneInterestAddress(interests.texture_upload_addresses,
                                          interests.texture_upload_count,
                                          facts.guest);
  if (facts.fetch_valid) {
    AddFableBinkWrapperPlaneInterestAddress(
        interests.resource_addresses, interests.resource_count,
        facts.fetch_base_address);
    AddFableBinkWrapperPlaneInterestAddress(
        interests.resource_addresses, interests.resource_count,
        facts.fetch_base_address & 0xFFFFF000u);
    AddFableBinkWrapperPlaneInterestAddress(interests.texture_upload_addresses,
                                            interests.texture_upload_count,
                                            facts.fetch_base_address);
    AddFableBinkWrapperPlaneInterestAddress(
        interests.texture_upload_addresses, interests.texture_upload_count,
        facts.fetch_base_address & 0xFFFFF000u);
  }
  return interests;
}

bool FableVideoBinkPlanePayloadSnapshot(
    const RendererTrackedResourceSnapshot& snapshot,
    size_t upload_size) {
  if (snapshot.kind != RendererTrackedResourceKind::kTexture) {
    return false;
  }

  const std::array<uint32_t, 6>& fetch =
      FableTextureFetchHasDwords(snapshot.texture.normalized_fetch_dwords)
          ? snapshot.texture.normalized_fetch_dwords
          : snapshot.texture.raw_fetch_dwords;
  const FableTextureFetchInfo info = FableTextureFetchInfoFromDwords(fetch);
  const size_t payload_size =
      upload_size != 0 ? upload_size : snapshot.texture.byte_size;
  if (!info.valid || info.format != 2u || payload_size == 0 ||
      snapshot.texture.byte_size != payload_size) {
    return false;
  }

  const bool y_plane =
      info.width == 1280u && info.height == 720u && payload_size == 921600u;
  const bool chroma_plane =
      info.width == 640u && info.height == 360u && payload_size == 276480u;
  return (y_plane || chroma_plane) && info.base_address >= 0x1F000000u &&
         info.base_address < 0x20000000u;
}

}  // namespace refii::renderer
