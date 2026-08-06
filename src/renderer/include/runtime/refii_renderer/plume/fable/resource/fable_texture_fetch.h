#pragma once

#include "runtime/refii_renderer/plume/fable/direct/fable_direct_state.h"
#include "runtime/refii_renderer/plume/fable/resource/fable_guest_resource_registry.h"

#include <array>
#include <cstdint>

namespace refii::renderer {

struct FableTextureFetchInfo {
  uint32_t base_address = 0;
  uint32_t byte_size = 0;
  uint32_t width = 0;
  uint32_t height = 0;
  uint32_t format = 0;
  bool valid = false;
};

struct TextureFetchSnapshotInfo {
  // Raw identity fields (mip0 base, whole-resource size): comparators and
  // ownership matching key on these.
  uint32_t base_address = 0;
  uint32_t size = 0;
  // What the sampler reads right now per Xenos mip semantics: equals the
  // base when mip_min_level==0, else the resident min level's location in
  // the mip chain. Content capture reads from here; width/height already
  // reflect the effective level.
  uint32_t effective_address = 0;
  uint32_t effective_size = 0;
  // When the sampler's minimum level sits in the (unpacked) mip chain, the
  // effective source is that level at its own dimensions; consumers may only
  // substitute it when the BASE region is provably blank (min_level is also
  // just the LOD clamp on healthy textures).
  uint32_t effective_width = 0;
  uint32_t effective_height = 0;
  uint32_t effective_mip_level = 0;
  uint32_t width = 0;
  uint32_t height = 0;
  uint32_t format = 0;
  // Addresses that READ the bytes the two above IDENTIFY. A fetch constant
  // stores a Xenos physical address; `membase + physical` is the low virtual
  // heap, not the texel. These carry the 0xC0000000 physical view so content
  // capture reads the pages the GPU would sample. Identity comparisons must
  // keep using base_address/effective_address — only reads use these.
  uint32_t read_address = 0;
  uint32_t effective_read_address = 0;
  const char *failure_reason = nullptr;
};

bool FableTextureFetchHasDwords(const std::array<uint32_t, 6> &dwords);
uint32_t FableDecodeTextureFetchAddressDword(uint32_t value);
uint32_t FableNormalizeTextureFetchAddressDword(uint32_t value);
std::array<uint32_t, 6>
FableNormalizeTextureFetchDwords(std::array<uint32_t, 6> dwords);
std::array<uint32_t, 6> FableLoadTextureFetchDwords(uint8_t *base,
                                                    uint32_t source_address);
// The base address a texture fetch constant samples, WITHOUT the
// snapshot-decodability requirements of FableTextureFetchInfoFromDwords.
// Returns 0 for an empty constant or one that does not declare type==kTexture.
uint32_t
FableTextureFetchIdentityBaseFromDwords(const std::array<uint32_t, 6> &dwords);
FableTextureFetchInfo
FableTextureFetchInfoFromDwords(const std::array<uint32_t, 6> &dwords);
std::array<uint32_t, 6>
FableCommandTextureFetchDwords(const FableDirectRenderCommand &command,
                               uint32_t slot);
bool FableTextureFetchDwordsMatch(const std::array<uint32_t, 6> &lhs,
                                  const std::array<uint32_t, 6> &rhs);

bool HasTextureFetchDwords(const std::array<uint32_t, 6> &dwords);
uint32_t NormalizeTextureFetchAddressDword(uint32_t value);
std::array<uint32_t, 6>
NormalizeTextureFetchDwords(std::array<uint32_t, 6> dwords);
std::array<uint32_t, 6> LoadTextureFetchDwords(uint8_t *base,
                                               uint32_t source_address);
TextureFetchSnapshotInfo
GetTextureFetchSnapshotInfo(const std::array<uint32_t, 6> &dwords);
bool IsUsableTextureFetchSnapshot(const TextureFetchSnapshotInfo &info);
bool IsTextureUploadSnapshotSourceRangeValid(
    uint8_t *base, const TextureFetchSnapshotInfo &info);
bool TileDestinationMatchesTextureFetchBase(
    uint32_t destination, const TextureFetchSnapshotInfo &info);
bool TextureFetchSnapshotsMatchForAliasUpload(
    const TextureFetchSnapshotInfo &lhs, const TextureFetchSnapshotInfo &rhs);
bool IsNeutralTextureFetchSnapshot(const TextureFetchSnapshotInfo &info);
std::array<uint32_t, 6>
TextureFetchDwordsForRecord(const RendererResourceRecord &record);
bool IsNeutralTextureResource(const RendererResourceRecord &record);
GuestTextureRecord
TextureRecordWithFetchSnapshotIdentity(const RendererResourceRecord &record);

} // namespace refii::renderer
