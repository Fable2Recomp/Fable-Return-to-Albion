#include "runtime/refii_renderer/plume/fable/title/fable_title_texture_refresh.h"

#include "runtime/refii_renderer/plume/fable/title/fable_title_classification.h"

namespace refii::renderer {

uint32_t ExactTitleTextureProducerSymbolAddress(std::string_view symbol) {
  if (symbol == "rex_lhGfx_LockBuffer") {
    return 0x821F6300u;
  }
  if (symbol == "rex_lhGfx_FlushBuffer") {
    return 0x822C2630u;
  }
  if (symbol == "sub_82278138") {
    return 0x82278138u;
  }
  if (symbol == "sub_821FB650") {
    return 0x821FB650u;
  }
  if (symbol == "sub_821FBB78") {
    return 0x821FBB78u;
  }
  if (symbol == "sub_8300CB48") {
    return 0x8300CB48u;
  }
  return 0;
}

bool IsExactTitleTextureProducerSymbolAddress(uint32_t symbol) {
  switch (symbol) {
  case 0x821F6300u: // D3D::LockResource
  case 0x822C2630u: // D3D::FlushResource
  case 0x82278138u:
  case 0x821FB650u:
  case 0x821FBB78u: // XGSetTextureHeader
  case 0x8300CB48u: // XGTileTextureLevel
    return true;
  default:
    return false;
  }
}

std::string_view
ExactTitleTextureProducerDisplayName(uint32_t symbol,
                                     std::string_view default_name) {
  switch (symbol) {
  case 0x821F6300u:
    return "D3D::LockResource";
  case 0x822C2630u:
    return "D3D::FlushResource";
  case 0x82278138u:
    return "sub_82278138";
  case 0x821FB650u:
    return "sub_821FB650";
  case 0x821FBB78u:
    return "XGSetTextureHeader";
  case 0x8300CB48u:
    return "XGTileTextureLevel";
  default:
    return default_name;
  }
}

// Snapshot-shaped adapter over the scalar classifier. The three accepted tile
// shapes live in exactly one place (fable_title_classification.cpp) so the
// table cannot drift between the two call shapes -- this overload used to
// carry its own copy of it.
bool IsFableTitleBillboardTileUploadShape(
    const TextureFetchSnapshotInfo &info) {
  return IsFableTitleBillboardTileUploadShape(info.base_address, info.width,
                                              info.height, info.format,
                                              info.size);
}

bool IsKnownTitleBillboardAtlasTexture(const RendererResourceRecord &record,
                                       const TextureFetchSnapshotInfo &info) {
  const auto is_title_billboard_atlas_object = [](uint32_t address) {
    return address == 0x4C132C70u || address == 0x4C132CA4u ||
           address == 0x4C132CD8u || address == 0x4C133150u ||
           address == 0x4C133184u || address == 0x16C40000u;
  };
  const GuestTextureRecord texture = GuestTextureRecordFor(record);
  const bool canonical_atlas =
      info.base_address == 0x16C40000u && info.size == 512u * 1024u &&
      info.width == 1024u && info.height == 512u && info.format == 20u &&
      (is_title_billboard_atlas_object(GuestObjectAddress(record)) ||
       is_title_billboard_atlas_object(GuestObjectParentAddress(record)) ||
       is_title_billboard_atlas_object(texture.object_address) ||
       is_title_billboard_atlas_object(texture.parent_address));
  const bool tile_ring =
      IsFableTitleBillboardTileUploadShape(info) &&
      (IsFableTitleBillboardTileUploadObjectAddress(
           GuestObjectAddress(record)) ||
       IsFableTitleBillboardTileUploadHeaderAddress(
           GuestObjectAddress(record)) ||
       IsFableTitleBillboardTileUploadObjectAddress(
           GuestObjectParentAddress(record)) ||
       IsFableTitleBillboardTileUploadHeaderAddress(
           GuestObjectParentAddress(record)) ||
       IsFableTitleBillboardTileUploadObjectAddress(texture.object_address) ||
       IsFableTitleBillboardTileUploadHeaderAddress(texture.object_address) ||
       IsFableTitleBillboardTileUploadObjectAddress(texture.parent_address) ||
       IsFableTitleBillboardTileUploadHeaderAddress(texture.parent_address) ||
       IsFableTitleBillboardTileUploadBaseAddress(GuestObjectAddress(record)) ||
       IsFableTitleBillboardTileUploadBaseAddress(texture.object_address));
  return canonical_atlas || tile_ring;
}

bool IsExactFableTitleGuiTextAtlasTexture(
    const RendererResourceRecord &record,
    const TextureFetchSnapshotInfo &info) {
  const bool exact_shape =
      (info.base_address == 0x1A0D9000u && info.size == 65536u &&
       info.width == 512u && info.height == 256u && info.format == 59u) ||
      (info.base_address == 0x1A0EA000u && info.size == 131072u &&
       info.width == 512u && info.height == 512u && info.format == 59u);
  if (!exact_shape) {
    return false;
  }

  const GuestTextureRecord texture = GuestTextureRecordFor(record);
  return IsFableTitleTextureProbeAddress(GuestObjectAddress(record)) ||
         IsFableTitleTextureProbeAddress(GuestObjectParentAddress(record)) ||
         IsFableTitleTextureProbeAddress(texture.object_address) ||
         IsFableTitleTextureProbeAddress(texture.parent_address) ||
         IsFableTitleTextureProbeAddress(info.base_address);
}

bool IsExactKnownTitleTextureRefreshResource(
    const RendererResourceRecord &record, const TextureFetchSnapshotInfo &info,
    uint32_t max_zero_texture_refresh_bytes) {
  if (info.base_address == 0 || info.size == 0 ||
      info.size > max_zero_texture_refresh_bytes) {
    return false;
  }

  const GuestTextureRecord texture = GuestTextureRecordFor(record);
  return IsFableTitleTextureProbeAddress(GuestObjectAddress(record)) ||
         IsFableTitleTextureProbeAddress(GuestObjectParentAddress(record)) ||
         IsFableTitleTextureProbeAddress(texture.object_address) ||
         IsFableTitleTextureProbeAddress(texture.parent_address) ||
         IsFableTitleTextureProbeAddress(info.base_address) ||
         IsKnownTitleBillboardAtlasTexture(record, info);
}

uint32_t
ZeroTextureRefreshCandidateAddress(const RendererResourceRecord &record) {
  const uint32_t object_address = GuestObjectAddress(record);
  return object_address != 0 ? object_address : record.guest_address;
}

bool ExactTitleTextureBlankProbeMatchesRecord(
    const ExactTitleTextureBlankProbe &probe,
    const RendererResourceRecord &record,
    const TextureFetchSnapshotInfo &info) {
  const GuestTextureRecord texture = GuestTextureRecordFor(record);
  if (probe.object_address != 0 &&
      GuestObjectAddress(record) != probe.object_address) {
    return false;
  }
  if (probe.parent_address != 0 &&
      GuestObjectParentAddress(record) != probe.parent_address) {
    return false;
  }
  if (probe.texture_object_address != 0 &&
      texture.object_address != probe.texture_object_address) {
    return false;
  }
  if (probe.texture_parent_address != 0 &&
      texture.parent_address != probe.texture_parent_address) {
    return false;
  }
  return info.base_address == probe.base_address &&
         info.size == probe.byte_size && info.width == probe.width &&
         info.height == probe.height && info.format == probe.format;
}

bool ExactTitleTextureRefreshCandidateMatchesRecord(
    const ExactTitleTextureRefreshCandidate &candidate,
    const RendererResourceRecord &record,
    const TextureFetchSnapshotInfo &info) {
  const GuestTextureRecord texture = GuestTextureRecordFor(record);
  if (candidate.object_address != 0 &&
      GuestObjectAddress(record) != candidate.object_address) {
    return false;
  }
  if (candidate.parent_address != 0 &&
      GuestObjectParentAddress(record) != candidate.parent_address) {
    return false;
  }
  if (candidate.texture_object_address != 0 &&
      texture.object_address != candidate.texture_object_address) {
    return false;
  }
  if (candidate.texture_parent_address != 0 &&
      texture.parent_address != candidate.texture_parent_address) {
    return false;
  }
  return info.base_address == candidate.base_address &&
         info.size == candidate.byte_size && info.width == candidate.width &&
         info.height == candidate.height && info.format == candidate.format;
}

} // namespace refii::renderer
