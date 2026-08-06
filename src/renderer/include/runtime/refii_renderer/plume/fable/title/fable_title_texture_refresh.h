#pragma once

#include "runtime/refii_renderer/hooks/resource/fable_observer_resources.h"
#include "runtime/refii_renderer/plume/fable/resource/fable_texture_fetch.h"

#include <cstdint>
#include <string_view>

namespace refii::renderer {

struct ExactTitleTextureRefreshCandidate {
  uint64_t serial = 0;
  uint64_t producer_serial = 0;
  uint64_t claimed_blank_probe_serial = 0;
  uint32_t record_address = 0;
  uint32_t object_address = 0;
  uint32_t parent_address = 0;
  uint32_t texture_object_address = 0;
  uint32_t texture_parent_address = 0;
  uint32_t base_address = 0;
  uint32_t byte_size = 0;
  uint32_t width = 0;
  uint32_t height = 0;
  uint32_t format = 0;
  uint32_t producer_symbol_address = 0;
  uint32_t producer_lr = 0;
  uint32_t root_header = 0;
  uint32_t lock_address = 0;
  uint32_t lock_write_address = 0;
  uint32_t lock_size = 0;
};

struct ExactTitleTextureBlankProbe {
  uint64_t serial = 0;
  uint32_t record_address = 0;
  uint32_t object_address = 0;
  uint32_t parent_address = 0;
  uint32_t texture_object_address = 0;
  uint32_t texture_parent_address = 0;
  uint32_t base_address = 0;
  uint32_t byte_size = 0;
  uint32_t width = 0;
  uint32_t height = 0;
  uint32_t format = 0;
};

uint32_t ExactTitleTextureProducerSymbolAddress(std::string_view symbol);

bool IsExactTitleTextureProducerSymbolAddress(uint32_t symbol);

std::string_view
ExactTitleTextureProducerDisplayName(uint32_t symbol,
                                     std::string_view default_name);

bool IsFableTitleBillboardTileUploadShape(const TextureFetchSnapshotInfo &info);

bool IsKnownTitleBillboardAtlasTexture(const RendererResourceRecord &record,
                                       const TextureFetchSnapshotInfo &info);

bool IsExactFableTitleGuiTextAtlasTexture(const RendererResourceRecord &record,
                                          const TextureFetchSnapshotInfo &info);

bool IsExactKnownTitleTextureRefreshResource(
    const RendererResourceRecord &record, const TextureFetchSnapshotInfo &info,
    uint32_t max_zero_texture_refresh_bytes);

uint32_t
ZeroTextureRefreshCandidateAddress(const RendererResourceRecord &record);

bool ExactTitleTextureBlankProbeMatchesRecord(
    const ExactTitleTextureBlankProbe &probe,
    const RendererResourceRecord &record, const TextureFetchSnapshotInfo &info);

bool ExactTitleTextureRefreshCandidateMatchesRecord(
    const ExactTitleTextureRefreshCandidate &candidate,
    const RendererResourceRecord &record, const TextureFetchSnapshotInfo &info);

} // namespace refii::renderer
