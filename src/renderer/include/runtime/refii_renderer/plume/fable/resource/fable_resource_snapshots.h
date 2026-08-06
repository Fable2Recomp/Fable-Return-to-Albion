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

// Content ownership: resources whose bytes are delivered by a dedicated
// event channel (Bink decode publishes, pix video frames, tile uploads) must
// never be point-in-time re-captured from guest memory — the channel's own
// sequence points are the only valid read moments. Set once at the channel's
// registration site and carried through every snapshot of the record.
enum class FableGuestContentChannel : uint8_t {
  kNone = 0,
  kBink,
  kPix,
  kTile,
};

using RendererContentChannel = FableGuestContentChannel;

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
  // Mirrors GuestResourceRuntime::upload_data_linear: upload_data holds
  // row-major linear texels (no untile on upload).
  bool upload_data_linear = false;
  // Mirrors GuestResourceRuntime::upload_data_mip_level (split-mip fallback).
  uint32_t upload_data_mip_level = 0;
  uint64_t serial = 0;
  bool scratch = false;
  FableGuestContentChannel content_channel = FableGuestContentChannel::kNone;
  // §4 real_gap attribution, mirrored from GuestResourceRuntime through the
  // snapshot harvest so the content-authority census (which runs under the
  // sync/harvest locks and cannot call back into the observer) can read it.
  const char *last_upload_capture_site = nullptr;
  bool residency_capture_seen = false;
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
  // Run-489 census: changes_ms=49 in one sync call with a trivial list —
  // the harvest's own list-building is O(n) push_backs, so the time is one
  // of these three. Filled by GetFableDirectResourceChanges, reported on
  // the "plume direct sync timing" line.
  double lock_wait_ms = 0.0;      // blocking observer-mutex acquisition
  double teardown_drain_ms = 0.0; // deferred Release teardown drain + acks
  double refresh_ms = 0.0;        // zero-texture refresh from guest memory
};

using RendererTrackedResourceChangeSet = FableGuestResourceChangeSet;

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

FableGuestResourceChangeSet GetFableDirectResourceChanges(
    bool wait_for_observer = false);
// GTL content channel: full snapshots (metadata + decoded bytes) of Bink
// plane publishes since the last drain, delivered independently of the
// observer-mutex harvest. Consumed once per Plume frame.
std::vector<FableGuestResourceSnapshot> DrainBinkPlaneSnapshotsForPlume();
void DrainPendingFableDirectTextureIdentityOperations();
void AcknowledgeFableDirectResourceChanges(uint64_t serial);
void AcknowledgeFableDirectDestroyedResources(
    const std::vector<uint32_t>& addresses,
    const std::vector<uint64_t>& serials);

}  // namespace refii::renderer
