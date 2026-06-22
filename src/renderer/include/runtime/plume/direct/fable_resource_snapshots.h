#pragma once

#include <array>
#include <cstdint>
#include <unordered_map>
#include <unordered_set>
#include <vector>

namespace refii::renderer {

enum class FableGuestResourceKind {
  kUnknown,
  kTexture,
  kSurface,
  kDepthSurface,
  kVertexBuffer,
  kIndexBuffer,
  kVertexDeclaration,
  kVertexShader,
  kPixelShader,
};

using RendererTrackedResourceKind = FableGuestResourceKind;

struct FableGuestBufferSnapshot {
  uint32_t object_address = 0;
  uint32_t gpu_address = 0;
  uint32_t byte_size = 0;
  uint32_t stride_or_index_format = 0;
  uint32_t header_address = 0;
  uint64_t upload_hash = 0;
  uint64_t upload_generation = 0;
  bool scratch = false;
};

using RendererTrackedBufferSnapshot = FableGuestBufferSnapshot;

struct FableGuestTextureSnapshot {
  uint32_t object_address = 0;
  uint32_t parent_address = 0;
  uint32_t base_address = 0;
  uint32_t byte_size = 0;
  std::array<uint32_t, 6> raw_fetch_dwords = {};
  std::array<uint32_t, 6> normalized_fetch_dwords = {};
  uint64_t upload_generation = 0;
  uint64_t upload_hash = 0;
};

using RendererTrackedTextureSnapshot = FableGuestTextureSnapshot;

struct FableGuestSurfaceSnapshot {
  uint32_t object_address = 0;
  uint32_t parent_address = 0;
  uint32_t render_target_info = 0;
  uint32_t depth_stencil_info = 0;
  uint32_t packed_extent = 0;
  uint32_t d3d_format = 0;
  uint32_t pitch_msaa = 0;
  uint32_t byte_size = 0;
};

using RendererTrackedSurfaceSnapshot = FableGuestSurfaceSnapshot;

struct FableGuestShaderSnapshot {
  uint32_t object_address = 0;
  uint32_t bytecode_address = 0;
  uint32_t bytecode_size = 0;
  uint32_t bytecode_offset = 0;
  uint64_t bytecode_hash = 0;
};

using RendererTrackedShaderSnapshot = FableGuestShaderSnapshot;

struct FableGuestVertexDeclarationSnapshot {
  uint32_t object_address = 0;
  uint32_t elements_address = 0;
  uint32_t elements_size = 0;
  uint32_t element_count = 0;
  uint32_t max_stream = 0;
  uint32_t stream_mask = 0;
  uint32_t declaration_flags = 0;
  uint64_t elements_hash = 0;
};

using RendererTrackedVertexDeclarationSnapshot =
    FableGuestVertexDeclarationSnapshot;

struct FableGuestResourceSnapshot {
  FableGuestResourceKind kind = FableGuestResourceKind::kUnknown;
  uint32_t guest_address = 0;
  uint32_t parent_address = 0;
  FableGuestBufferSnapshot buffer;
  FableGuestTextureSnapshot texture;
  FableGuestSurfaceSnapshot surface;
  FableGuestShaderSnapshot shader;
  FableGuestVertexDeclarationSnapshot vertex_declaration;
  uint64_t upload_generation = 0;
  std::vector<uint8_t> upload_data;
  uint64_t serial = 0;
  bool scratch = false;
};

using RendererTrackedResourceSnapshot = FableGuestResourceSnapshot;

struct FableGuestResourceUploadCacheEntry {
  FableGuestResourceKind kind = FableGuestResourceKind::kUnknown;
  uint32_t data_address = 0;
  uint32_t byte_size = 0;
  uint32_t meta = 0;
  uint64_t hash = 0;
  uint64_t upload_generation = 0;
};

using RendererTrackedResourceUploadCacheEntry =
    FableGuestResourceUploadCacheEntry;

struct FableGuestResourceVersion {
  uint64_t serial = 0;
  uint64_t resource_creates = 0;
  uint64_t resource_destroys = 0;
  uint64_t resource_unlocks = 0;
};

using RendererTrackedResourceVersion = FableGuestResourceVersion;

struct FableGuestResourceChangeSet {
  uint64_t serial = 0;
  std::vector<uint32_t> changed_addresses;
  std::vector<uint32_t> destroyed_addresses;
  std::vector<uint64_t> destroyed_serials;
  bool observer_busy = false;
};

using RendererTrackedResourceChangeSet = FableGuestResourceChangeSet;

std::vector<FableGuestResourceSnapshot>
GetFableDirectResourceSnapshots(
    const std::unordered_map<uint32_t,
                             FableGuestResourceUploadCacheEntry>*
        known_resources = nullptr,
    const std::unordered_set<uint32_t>* upload_data_filter = nullptr,
    const std::unordered_set<uint32_t>* resource_filter = nullptr);

std::vector<FableGuestResourceSnapshot>
GetFableDirectResourceSnapshotsForAddresses(
    const std::unordered_set<uint32_t>& addresses,
    const std::unordered_map<uint32_t,
                             FableGuestResourceUploadCacheEntry>*
        known_resources = nullptr,
    const std::unordered_set<uint32_t>* upload_data_filter = nullptr,
    bool* observer_busy = nullptr);

std::vector<FableGuestResourceSnapshot>
GetFableDirectResourceSnapshotsForAddressesBlocking(
    const std::unordered_set<uint32_t>& addresses,
    const std::unordered_map<uint32_t,
                             FableGuestResourceUploadCacheEntry>*
        known_resources = nullptr,
    const std::unordered_set<uint32_t>* upload_data_filter = nullptr);

FableGuestResourceVersion GetFableDirectResourceVersion();
FableGuestResourceChangeSet GetFableDirectResourceChanges();
void DrainPendingFableDirectTextureIdentityOperations();
void AcknowledgeFableDirectResourceChanges(uint64_t serial);
void AcknowledgeFableDirectDestroyedResources(
    const std::vector<uint32_t>& addresses,
    const std::vector<uint64_t>& serials);

}  // namespace refii::renderer
