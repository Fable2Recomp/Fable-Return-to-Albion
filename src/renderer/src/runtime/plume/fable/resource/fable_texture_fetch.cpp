#include "runtime/refii_renderer/plume/fable/resource/fable_texture_fetch.h"

#include "runtime/refii_renderer/bik/bink_guest_memory.h"
#include "runtime/refii_renderer/plume/fable/resource/fable_guest_resource_registry.h"
#include "runtime/refii_renderer/plume/general/plume_fable_addressing.h"
#include "runtime/cvars.h"

#include <algorithm>

#include <rex/graphics/pipeline/texture/info.h>
#include <rex/graphics/pipeline/texture/util.h>
#include <rex/graphics/xenos.h>

namespace refii::renderer {
namespace {

constexpr uint32_t kFableNeutralTextureBaseAddress = 0x1FC40000u;
constexpr uint32_t kMaxTextureUploadSnapshotSize = 64u << 20;
constexpr uint32_t kMinTextureUploadSnapshotAddress = 0x10000u;

uint32_t ClampFableTextureFetchByteSize(uint64_t byte_size) {
  constexpr uint64_t kMaxFableTextureFetchBytes = 64ull << 20;
  if (byte_size == 0 || byte_size > kMaxFableTextureFetchBytes) {
    return 0;
  }
  return static_cast<uint32_t>(byte_size);
}

bool IsTextureFetchShapeSupportedForSnapshot(
    const rex::graphics::xenos::xe_gpu_texture_fetch_t& fetch) {
  namespace xenos = rex::graphics::xenos;

  switch (fetch.dimension) {
    case xenos::DataDimension::k1D:
    case xenos::DataDimension::k3D:
      return !fetch.stacked;
    case xenos::DataDimension::k2DOrStacked:
      return !fetch.stacked;
    case xenos::DataDimension::kCube:
      return !fetch.stacked && fetch.size_2d.stack_depth == 5;
    default:
      return false;
  }
}
void NormalizeTextureFetchForSnapshotInfo(
    rex::graphics::xenos::xe_gpu_texture_fetch_t& fetch) {
  if (fetch.mip_address == 0) {
    fetch.mip_min_level = 0;
    fetch.mip_max_level = 0;
    return;
  }

  if (fetch.mip_max_level < fetch.mip_min_level) {
    fetch.mip_max_level = fetch.mip_min_level;
  }
}

uint32_t ClampTextureSnapshotSize(uint64_t size) {
  return static_cast<uint32_t>(
      std::min<uint64_t>(size, kMaxTextureUploadSnapshotSize));
}

// Xenos mip semantics as the general rule: the sampler never reads below
// mip_min_level, and while a texture's mip0 streams in the game raises
// mip_min_level so the resident mip chain is what's on screen. When the min
// level sits in the (unpacked) mip chain, the EFFECTIVE source for content
// capture is that level at its own dimensions — the game lowering
// mip_min_level once mip0 lands changes the fetch dwords and re-derives
// everything through here, so streamed textures stay correct game-wide.
struct EffectiveMipSource {
  uint32_t address = 0;
  uint32_t byte_size = 0;
  uint32_t width = 0;
  uint32_t height = 0;
  bool from_mip_chain = false;
};

EffectiveMipSource ResolveEffectiveMipSource(
    const rex::graphics::xenos::xe_gpu_texture_fetch_t& fetch,
    const rex::graphics::TextureInfo& texture, uint32_t full_width,
    uint32_t full_height) {
  EffectiveMipSource source;
  source.address = texture.memory.base_address;
  source.byte_size = texture.memory.base_size;
  source.width = full_width;
  source.height = full_height;

  const uint32_t min_level = fetch.mip_min_level;
  if (min_level == 0 || texture.memory.mip_address == 0) {
    return source;
  }
  // Preconditions before touching the layout math (GetMipLocation asserts on
  // degenerate inputs — SIGABRT in the tile path on real runs): 2D only,
  // sane level range, and never inside the packed mip tail.
  if (fetch.dimension != rex::graphics::xenos::DataDimension::k2DOrStacked ||
      min_level > fetch.mip_max_level || min_level >= 12u ||
      full_width == 0 || full_height == 0) {
    return source;
  }
  if (fetch.packed_mips != 0 &&
      min_level >= rex::graphics::texture_util::GetPackedMipLevel(
                       full_width, full_height)) {
    return source;
  }
  uint32_t offset_x = 0;
  uint32_t offset_y = 0;
  const uint32_t mip_location =
      texture.GetMipLocation(min_level, &offset_x, &offset_y, true);
  // Packed tail mips share a tile (non-zero block offsets); a raw region
  // snapshot cannot represent those, so keep the base source for them.
  if (mip_location == 0 || offset_x != 0 || offset_y != 0) {
    return source;
  }
  const auto* format_info = texture.format_info();
  if (format_info == nullptr || format_info->bytes_per_block() == 0) {
    return source;
  }
  const uint64_t mip_bytes =
      static_cast<uint64_t>(
          texture.GetMipExtent(min_level, true).all_blocks()) *
      format_info->bytes_per_block();
  if (mip_bytes == 0) {
    return source;
  }
  source.address = mip_location;
  source.byte_size = static_cast<uint32_t>(
      std::min<uint64_t>(mip_bytes, std::numeric_limits<uint32_t>::max()));
  source.width = std::max(1u, full_width >> min_level);
  source.height = std::max(1u, full_height >> min_level);
  source.from_mip_chain = true;
  return source;
}

}  // namespace

bool FableTextureFetchHasDwords(const std::array<uint32_t, 6>& dwords) {
  for (uint32_t dword : dwords) {
    if (dword != 0) {
      return true;
    }
  }
  return false;
}

uint32_t FableDecodeTextureFetchAddressDword(uint32_t value) {
  return ((((value >> 20u) + 512u) & 0x1000u) +
          (value & 0x1FFFF000u));
}

uint32_t FableNormalizeTextureFetchAddressDword(uint32_t value) {
  return FableDecodeTextureFetchAddressDword(value) | (value & 0xFFFu);
}

uint32_t NormalizeTextureFetchAddressDword(uint32_t value) {
  return FableNormalizeTextureFetchAddressDword(value);
}

std::array<uint32_t, 6> FableNormalizeTextureFetchDwords(
    std::array<uint32_t, 6> dwords) {
  dwords[1] = FableNormalizeTextureFetchAddressDword(dwords[1]);
  dwords[5] = FableNormalizeTextureFetchAddressDword(dwords[5]);
  return dwords;
}

std::array<uint32_t, 6> NormalizeTextureFetchDwords(
    std::array<uint32_t, 6> dwords) {
  return FableNormalizeTextureFetchDwords(dwords);
}

std::array<uint32_t, 6> FableLoadTextureFetchDwords(uint8_t* base,
                                                    uint32_t source_address) {
  std::array<uint32_t, 6> dwords = {};
  if (base == nullptr || source_address == 0) {
    return dwords;
  }

  for (uint32_t i = 0; i < dwords.size(); ++i) {
    dwords[i] =
        bink::LoadGuestU32(base, source_address + i * sizeof(uint32_t));
  }

  return FableNormalizeTextureFetchDwords(dwords);
}

std::array<uint32_t, 6> LoadTextureFetchDwords(uint8_t* base,
                                               uint32_t source_address) {
  return FableLoadTextureFetchDwords(base, source_address);
}

bool HasTextureFetchDwords(const std::array<uint32_t, 6>& dwords) {
  return FableTextureFetchHasDwords(dwords);
}

uint32_t FableTextureFetchIdentityBaseFromDwords(
    const std::array<uint32_t, 6>& dwords) {
  // IDENTITY IS NOT SNAPSHOT DECODABILITY (2026-08-03).
  //
  // FableTextureFetchInfoFromDwords below answers "can we decode and snapshot
  // this texture?" — it requires a supported shape, a known format with a
  // non-degenerate FormatInfo, a sane mip configuration and a successful
  // TextureInfo::Prepare. Callers that only need to know WHICH guest memory a
  // slot samples must not inherit those requirements: on Xenos the TFETCH
  // samples the fetch constant's base address, and the CPU header object
  // address never reaches the GPU, so the base alone is the Xbox semantic.
  // Header-object keying is our bookkeeping.
  //
  // The one field that is consulted is `type`. The 32 fetch constants are a
  // SHARED file — slots 26..31 (device+0x6F0) hold 2-dword VERTEX fetch
  // constants — so a constant that does not declare itself a texture must
  // never have its dword[1] read as a texture base.
  if (!FableTextureFetchHasDwords(dwords)) {
    return 0;
  }
  namespace xenos = rex::graphics::xenos;
  xenos::xe_gpu_texture_fetch_t fetch = {};
  fetch.dword_0 = dwords[0];
  fetch.dword_1 = dwords[1];
  fetch.dword_2 = dwords[2];
  fetch.dword_3 = dwords[3];
  fetch.dword_4 = dwords[4];
  fetch.dword_5 = dwords[5];
  if (fetch.type != xenos::FetchConstantType::kTexture) {
    return 0;
  }
  // Normalise then decode, in exactly the order FableTextureFetchInfoFromDwords
  // uses for its own base_address, so a base derived here compares equal to one
  // derived there and to the registry's stored identity.
  const std::array<uint32_t, 6> normalized_dwords =
      FableNormalizeTextureFetchDwords(dwords);
  const uint32_t base =
      FableDecodeTextureFetchAddressDword(normalized_dwords[1]);
  if (base != 0) {
    return base;
  }
  return FableDecodeTextureFetchAddressDword(normalized_dwords[5]);
}

FableTextureFetchInfo FableTextureFetchInfoFromDwords(
    const std::array<uint32_t, 6>& dwords) {
  FableTextureFetchInfo info;
  if (!FableTextureFetchHasDwords(dwords)) {
    return info;
  }

  const std::array<uint32_t, 6> normalized_dwords =
      FableNormalizeTextureFetchDwords(dwords);
  namespace xenos = rex::graphics::xenos;
  xenos::xe_gpu_texture_fetch_t fetch = {};
  fetch.dword_0 = normalized_dwords[0];
  fetch.dword_1 = normalized_dwords[1];
  fetch.dword_2 = normalized_dwords[2];
  fetch.dword_3 = normalized_dwords[3];
  fetch.dword_4 = normalized_dwords[4];
  fetch.dword_5 = normalized_dwords[5];
  if (fetch.type != xenos::FetchConstantType::kTexture ||
      static_cast<uint32_t>(fetch.format) >= 64u) {
    return info;
  }
  if (!IsTextureFetchShapeSupportedForSnapshot(fetch)) {
    return info;
  }
  // Formats whose FormatInfo carries zero block dimensions or zero
  // bytes-per-block (unsupported/placeholder table entries) divide by zero
  // inside TextureInfo::Prepare -> TextureExtent::Calculate. SIGFPE'd the
  // record thread in run 409 via a never-uploaded fmt=59 GUI record whose
  // stale dwords reached the tiny-title classifier. Same guard idiom as
  // DescribeFableTextureFetchForSnapshot below.
  const rex::graphics::FormatInfo* format_info =
      rex::graphics::FormatInfo::Get(fetch.format);
  if (!format_info || format_info->block_width == 0 ||
      format_info->block_height == 0 || format_info->bytes_per_block() == 0) {
    return info;
  }
  NormalizeTextureFetchForSnapshotInfo(fetch);
  // See GetTextureFetchSnapshotInfo: SetupMemoryInfo assert_always()s on this
  // mip configuration (run 413 SIGABRT).
  if (fetch.mip_min_level > 0 && fetch.base_address != 0 &&
      fetch.mip_address != 0 && fetch.base_address != fetch.mip_address) {
    return info;
  }

  rex::graphics::TextureInfo texture;
  if (!rex::graphics::TextureInfo::Prepare(fetch, &texture)) {
    return info;
  }

  switch (fetch.dimension) {
    case xenos::DataDimension::k1D:
      info.width = fetch.size_1d.width + 1u;
      info.height = 1u;
      break;
    case xenos::DataDimension::k3D:
      info.width = fetch.size_3d.width + 1u;
      info.height = fetch.size_3d.height + 1u;
      break;
    case xenos::DataDimension::k2DOrStacked:
    case xenos::DataDimension::kCube:
      info.width = fetch.size_2d.width + 1u;
      info.height = fetch.size_2d.height + 1u;
      break;
    default:
      return info;
  }

  info.base_address =
      FableDecodeTextureFetchAddressDword(normalized_dwords[1]);
  info.byte_size = ClampFableTextureFetchByteSize(texture.memory.base_size);
  if (info.base_address == 0 || info.byte_size == 0) {
    info.base_address =
        FableDecodeTextureFetchAddressDword(normalized_dwords[5]);
    info.byte_size = ClampFableTextureFetchByteSize(texture.memory.mip_size);
  }
  info.format = static_cast<uint32_t>(fetch.format);
  info.valid = info.base_address != 0 && info.byte_size != 0 &&
               info.width != 0 && info.height != 0;
  return info;
}

std::array<uint32_t, 6> FableCommandTextureFetchDwords(
    const FableDirectRenderCommand& command, uint32_t slot) {
  std::array<uint32_t, 6> dwords = {};
  constexpr uint32_t kTextureFetchConstantDwords =
      static_cast<uint32_t>(dwords.size());
  const uint32_t fetch_dword = slot * kTextureFetchConstantDwords;
  if (slot >= kRendererD3DTextureSlots ||
      fetch_dword + kTextureFetchConstantDwords >
          command.constants.fetch.size()) {
    return dwords;
  }
  std::copy_n(command.constants.fetch.begin() + fetch_dword,
              kTextureFetchConstantDwords, dwords.begin());
  return dwords;
}

bool FableTextureFetchDwordsMatch(const std::array<uint32_t, 6>& lhs,
                                  const std::array<uint32_t, 6>& rhs) {
  const uint64_t lhs_key = FableDirectTextureFetchKey(lhs);
  return lhs_key != 0 && lhs_key == FableDirectTextureFetchKey(rhs);
}

TextureFetchSnapshotInfo GetTextureFetchSnapshotInfo(
    const std::array<uint32_t, 6>& dwords) {
  TextureFetchSnapshotInfo info;
  if (!HasTextureFetchDwords(dwords)) {
    info.failure_reason = "empty_fetch";
    return info;
  }

  namespace texture_info = rex::graphics;

  rex::graphics::xenos::xe_gpu_texture_fetch_t fetch = {};
  fetch.dword_0 = dwords[0];
  fetch.dword_1 = dwords[1];
  fetch.dword_2 = dwords[2];
  fetch.dword_3 = dwords[3];
  fetch.dword_4 = dwords[4];
  fetch.dword_5 = dwords[5];
  if (fetch.type != rex::graphics::xenos::FetchConstantType::kTexture) {
    info.failure_reason = "not_texture";
    return info;
  }
  if (static_cast<uint32_t>(fetch.format) >= 64u) {
    info.failure_reason = "unknown_format";
    return info;
  }
  const texture_info::FormatInfo* format_info =
      texture_info::FormatInfo::Get(fetch.format);
  if (!format_info) {
    info.failure_reason = "unknown_format";
    return info;
  }
  if (format_info->bytes_per_block() == 0) {
    info.failure_reason = "zero_bpb";
    return info;
  }
  if (!IsTextureFetchShapeSupportedForSnapshot(fetch)) {
    info.failure_reason = "unsupported_shape";
    return info;
  }
  NormalizeTextureFetchForSnapshotInfo(fetch);
  // TextureInfo::SetupMemoryInfo assert_always()s on mip_min_level > 0 with
  // distinct non-zero base and mip addresses ("WTF?" branch, info.cpp:336) —
  // SIGABRT'd the record thread in run 413 via a stale record's garbage
  // dwords. Reject that configuration here instead of asserting the process
  // down; release builds of the SDK would have proceeded anyway.
  if (fetch.mip_min_level > 0 && fetch.base_address != 0 &&
      fetch.mip_address != 0 && fetch.base_address != fetch.mip_address) {
    info.failure_reason = "wtf_mips";
    return info;
  }

  texture_info::TextureInfo texture;
  if (!texture_info::TextureInfo::Prepare(fetch, &texture)) {
    info.failure_reason = "prepare_failed";
    return info;
  }

  info.format = static_cast<uint32_t>(fetch.format);
  switch (fetch.dimension) {
    case rex::graphics::xenos::DataDimension::k1D:
      info.width = fetch.size_1d.width + 1u;
      info.height = 1u;
      break;
    case rex::graphics::xenos::DataDimension::k3D:
      info.width = fetch.size_3d.width + 1u;
      info.height = fetch.size_3d.height + 1u;
      break;
    case rex::graphics::xenos::DataDimension::k2DOrStacked:
    case rex::graphics::xenos::DataDimension::kCube:
      info.width = fetch.size_2d.width + 1u;
      info.height = fetch.size_2d.height + 1u;
      break;
    default:
      break;
  }

  info.base_address = texture.memory.base_address;
  info.size = ClampTextureSnapshotSize(texture.memory.base_size);
  if (info.base_address == 0 || info.size == 0) {
    info.base_address = texture.memory.mip_address;
    info.size = ClampTextureSnapshotSize(texture.memory.mip_size);
  }
  info.effective_address = info.base_address;
  info.effective_size = info.size;
  info.effective_width = info.width;
  info.effective_height = info.height;
  const EffectiveMipSource mip_source =
      ResolveEffectiveMipSource(fetch, texture, info.width, info.height);
  if (mip_source.from_mip_chain) {
    info.effective_address = mip_source.address;
    info.effective_size = ClampTextureSnapshotSize(mip_source.byte_size);
    info.effective_width = mip_source.width;
    info.effective_height = mip_source.height;
    info.effective_mip_level = fetch.mip_min_level;
  }
  // Read addresses trail identity: same bytes, addressable form. Behind the
  // cvar so the raw-address behaviour stays available for A/B.
  if (RendererPhysicalViewReadsEnabled()) {
    info.read_address = FableGuestPhysicalViewAddress(info.base_address);
    info.effective_read_address =
        FableGuestPhysicalViewAddress(info.effective_address);
  } else {
    info.read_address = info.base_address;
    info.effective_read_address = info.effective_address;
  }
  if (info.base_address == 0) {
    info.failure_reason = "zero_base";
  } else if (info.size == 0) {
    info.failure_reason = "zero_size";
  }
  return info;
}

bool IsUsableTextureFetchSnapshot(const TextureFetchSnapshotInfo& info) {
  return info.base_address != 0 && info.size != 0;
}

bool IsTextureUploadSnapshotSourceRangeValid(
    uint8_t* base, const TextureFetchSnapshotInfo& info) {
  // Range/identity gates key on base_address; the readability probe keys on
  // read_address, which is the same bytes in addressable form.
  const uint32_t read_address =
      info.read_address != 0 ? info.read_address : info.base_address;
  return base != nullptr && IsUsableTextureFetchSnapshot(info) &&
         info.base_address >= kMinTextureUploadSnapshotAddress &&
         info.size <= kMaxTextureUploadSnapshotSize &&
         read_address <= UINT32_MAX - info.size &&
         bink::IsReadableGuestByteRange(base, read_address, info.size);
}

bool TileDestinationMatchesTextureFetchBase(
    uint32_t destination, const TextureFetchSnapshotInfo& info) {
  if (!IsUsableTextureFetchSnapshot(info) || destination == 0) {
    return false;
  }

  return destination == info.base_address ||
         DecodeFableGpuMemoryPointer(destination) == info.base_address;
}

bool TextureFetchSnapshotsMatchForAliasUpload(
    const TextureFetchSnapshotInfo& lhs, const TextureFetchSnapshotInfo& rhs) {
  return IsUsableTextureFetchSnapshot(lhs) &&
         IsUsableTextureFetchSnapshot(rhs) &&
         lhs.base_address == rhs.base_address && lhs.size == rhs.size &&
         lhs.width == rhs.width && lhs.height == rhs.height &&
         lhs.format == rhs.format;
}

bool IsNeutralTextureFetchSnapshot(const TextureFetchSnapshotInfo& info) {
  return IsUsableTextureFetchSnapshot(info) &&
         info.base_address == kFableNeutralTextureBaseAddress;
}

std::array<uint32_t, 6> TextureFetchDwordsForRecord(
    const RendererResourceRecord& record) {
  const GuestTextureRecord texture = GuestTextureRecordFor(record);
  const std::array<uint32_t, 6> raw_dwords =
      NormalizeTextureFetchDwords(texture.raw_fetch_dwords);
  if (!HasTextureFetchDwords(texture.normalized_fetch_dwords)) {
    return raw_dwords;
  }
  if (!HasTextureFetchDwords(raw_dwords)) {
    return texture.normalized_fetch_dwords;
  }

  const TextureFetchSnapshotInfo normalized_info =
      GetTextureFetchSnapshotInfo(texture.normalized_fetch_dwords);
  const TextureFetchSnapshotInfo raw_info =
      GetTextureFetchSnapshotInfo(raw_dwords);
  if (raw_info.base_address != 0 && raw_info.size != 0 &&
      (normalized_info.base_address == 0 || normalized_info.size == 0 ||
       normalized_info.base_address != raw_info.base_address ||
       normalized_info.size != raw_info.size)) {
    return raw_dwords;
  }
  return texture.normalized_fetch_dwords;
}

bool IsNeutralTextureResource(const RendererResourceRecord& record) {
  return GuestObjectKind(record) == RendererResourceKind::kTexture &&
         IsNeutralTextureFetchSnapshot(
             GetTextureFetchSnapshotInfo(TextureFetchDwordsForRecord(record)));
}

GuestTextureRecord TextureRecordWithFetchSnapshotIdentity(
    const RendererResourceRecord& record) {
  GuestTextureRecord texture = GuestTextureRecordFor(record);
  const std::array<uint32_t, 6> dwords = TextureFetchDwordsForRecord(record);
  if (!HasTextureFetchDwords(dwords)) {
    return texture;
  }

  const TextureFetchSnapshotInfo info = GetTextureFetchSnapshotInfo(dwords);
  if (!IsUsableTextureFetchSnapshot(info)) {
    return texture;
  }

  if (texture.base_address == 0) {
    texture.base_address = info.base_address;
  }
  if (texture.byte_size < info.size) {
    texture.byte_size = info.size;
  }
  if (!HasTextureFetchDwords(texture.raw_fetch_dwords)) {
    texture.raw_fetch_dwords = dwords;
  }
  if (!HasTextureFetchDwords(texture.normalized_fetch_dwords)) {
    texture.normalized_fetch_dwords = dwords;
  }
  return texture;
}

}  // namespace refii::renderer
