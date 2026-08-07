#include "runtime/refii_renderer/gtl/refii_gtl.h"
#include "runtime/refii_renderer/gtl/refii_gtl_state.h"
#include "runtime/refii_renderer/gtl/refii_gtl_symbols.h"

#include "runtime/refii_renderer/gtl/refii_gtl_state.h"
#include "runtime/refii_renderer/gtl/refii_gtl_symbols.h"
#include <algorithm>
#include <array>
#include <bit>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <atomic>
#include <mutex>
#include <string>
#include <unordered_map>
#include <unordered_set>
#include "renderer_runtime.h"
#include "rex/logging.h"
#include "runtime/cvars.h"
#include <rex/chrono/clock.h>
#include <rex/graphics/pipeline/texture/info.h>
#include "runtime/refii_renderer/hooks/general/fable_hook_stack.h"
#include "runtime/refii_renderer/hooks/root/fable_hook_symbols.h"
#include "runtime/refii_renderer/bik/bink_guest_memory.h"
#include "runtime/refii_renderer/plume/fable/direct/fable_direct_state.h"
#include "runtime/refii_renderer/plume/fable/resource/fable_resource_snapshots.h"
#include "runtime/refii_renderer/plume/fable/resource/fable_upload_data.h"
#include "runtime/refii_renderer/plume/general/plume_fable_addressing.h"


// ===========================================================================
// RESOURCE SNAPSHOT FEED — the GTL's replacement for the deleted observer's
// GetFableDirectResourceSnapshotsForAddresses family.
// ===========================================================================
//
// WHY THIS LIVES HERE. hook_observer.cpp was deleted on 2026-08-04 and its
// inert stub returned {} from every one of these, which starved the direct
// renderer: no snapshots -> no tracked resources -> DirectVertexShaderHashLocked
// returns 0 -> `missing_pipeline` -> nothing renders. The feed is rebuilt on the
// GTL's OWN registry (g_state.resources / g_state.content_images), which is fed
// by the guest's stated Create / header-init / unlock / tile / resolve roots.
// There is no observer behind it and none is wanted.
//
// STATED DATA ONLY (§5). Every field below either comes from a call argument
// the guest passed, from a header the guest's own factory wrote, or from bytes
// a stated write root delivered. Nothing here decodes a Xenos register to
// recover something an API call already said plainly. Where the GTL genuinely
// has no source for a field it is LEFT AT ITS DEFAULT with a comment saying so
// — an honest zero, not an invented value.
//
// KNOWN GAPS, stated rather than papered over:
//   * texture.normalized_fetch_dwords  — the GTL captures the HEADER-stated
//     fetch block (raw_fetch_dwords). "Normalized" is the observer's bind-time
//     view of the same constant; the consumer already prefers raw when
//     normalized is all-zero (PreferredTextureFetchDwords), so leaving it zero
//     selects the stated block. This is the correct answer, not a stub.
//   * texture.upload_hash / buffer.upload_hash — the GTL tracks content by
//     GENERATION, not by content hash. The consumer's upload cache falls back
//     to upload_generation when the hash is zero, so change detection still
//     works; re-hashing every texture per sync would cost more than it saves.
//   * buffer.gpu_address / buffer.header_address — CLOSED 2026-08-04. The
//     premise ("no Create root states the buffer's GPU data pointer") was
//     correct and is exactly why the create call is the wrong place to ask:
//     buffers are created EMPTY and their payload RELOCATES. The answer comes
//     from the D3D header the guest's own bind sites name, re-read per
//     snapshot; see the kVertexBuffer/kIndexBuffer arm below.
//   * surface.render_target_info / depth_stencil_info — these are Xenos
//     RB_*_INFO register words. The GTL never sees them; it sees the STATED
//     D3DFORMAT from D3DDevice_CreateSurface, which is reported in d3d_format
//     (the consumer's own preferred source, metadata line 341).
//   * surface.byte_size — no root states a surface's allocation size. Zero,
//     rather than a width*height*4 guess that is wrong for every non-32bpp
//     format.
//   * content_channel — the GTL does not tag channels. Bink planes arrive
//     through DrainBinkPlaneSnapshotsForPlume, a separate feed.
//   * last_upload_capture_site / residency_capture_seen — §4 census
//     attribution, diagnostic-only in the consumer, and meaningless without
//     the observer's capture sites. Left null/false.

namespace refii::renderer {
namespace {

using refii::gtl::Resource;
using refii::gtl::ResourceKind;

// The GTL's registry kind, translated to the consumer's enum.
//
// kDepthSurface is NOT inferred from a format field — the consumer takes the
// kind verbatim and never re-derives it. A surface becomes kDepthSurface only
// because D3DDevice_SetDepthStencilSurface NAMED it (Resource::was_depth_target).
FableGuestResourceKind PublicResourceKind(const Resource &resource) {
  switch (resource.kind) {
    case ResourceKind::kTexture:
      return FableGuestResourceKind::kTexture;
    case ResourceKind::kSurface:
      return resource.was_depth_target ? FableGuestResourceKind::kDepthSurface
                                       : FableGuestResourceKind::kSurface;
    case ResourceKind::kVertexBuffer:
      return FableGuestResourceKind::kVertexBuffer;
    case ResourceKind::kIndexBuffer:
      return FableGuestResourceKind::kIndexBuffer;
    case ResourceKind::kVertexShader:
      return FableGuestResourceKind::kVertexShader;
    case ResourceKind::kPixelShader:
      return FableGuestResourceKind::kPixelShader;
    case ResourceKind::kVertexDeclaration:
      return FableGuestResourceKind::kVertexDeclaration;
    case ResourceKind::kUnknown:
    default:
      return FableGuestResourceKind::kUnknown;
  }
}

// Surface extent packing. Bit layout verified against the decoder the consumer
// actually runs — DecodeFableSurfaceExtentFromPacked (plume_surface_shape.cpp):
// bits 31..18 hold width-1, bits 17..3 hold height-1 (15 bits), depth is always
// 1. The clamps match the in-tree encoder in fable_direct_renderer.cpp.
uint32_t PackSurfaceExtent(uint32_t width, uint32_t height) {
  if (width == 0 || height == 0) {
    return 0;  // 0 means "no extent stated"; the consumer defaults to 1x1.
  }
  width = width > 0x4000u ? 0x4000u : width;
  height = height > 0x8000u ? 0x8000u : height;
  return ((width - 1u) << 18u) | ((height - 1u) << 3u);
}

bool IsImageKind(FableGuestResourceKind kind) {
  return kind == FableGuestResourceKind::kTexture ||
         kind == FableGuestResourceKind::kSurface ||
         kind == FableGuestResourceKind::kDepthSurface;
}

// Does the consumer already hold this exact content generation?
//
// The upload cache the consumer passes in is the protocol for skipping payload
// copies: it records, per address, the generation it last successfully
// materialized. Re-copying a multi-megabyte texture that the consumer already
// uploaded is pure cost (this path measured 194-230ms per slow sync call in the
// observer era). A resource with generation 0 has no content at all and is
// never skipped.
bool ConsumerHasCurrentUpload(
    const std::unordered_map<uint32_t, FableGuestResourceUploadCacheEntry>
        *known,
    uint32_t guest_address, uint64_t content_generation) {
  if (known == nullptr || content_generation == 0) {
    return false;
  }
  const auto it = known->find(guest_address);
  return it != known->end() &&
         it->second.upload_generation == content_generation;
}

// Build one snapshot. g_state.mutex MUST be held.
FableGuestResourceSnapshot BuildResourceSnapshotLocked(
    const Resource &resource,
    const std::unordered_map<uint32_t, FableGuestResourceUploadCacheEntry>
        *known,
    bool want_upload_data) {
  FableGuestResourceSnapshot snapshot;
  snapshot.kind = PublicResourceKind(resource);
  snapshot.guest_address = resource.guest_address;
  // Verbatim from GetSurfaceLevel / GetCubeMapSurface — the call named the
  // parent outright, so nothing recovers it from address arithmetic.
  snapshot.parent_address = resource.view_parent_texture;
  // Registry-wide monotonic "last mutated" stamp (see Resource::change_serial).
  // Falls back to the create serial for a resource registered before the
  // journal ever stamped it.
  snapshot.serial = resource.change_serial != 0 ? resource.change_serial
                                                : resource.create_serial;
  snapshot.upload_generation = resource.content_generation;

  switch (snapshot.kind) {
    case FableGuestResourceKind::kVertexBuffer:
    case FableGuestResourceKind::kIndexBuffer: {
      snapshot.buffer.object_address = resource.guest_address;
      // For a buffer the registry key IS the D3D header -- see the create-exit
      // note on why buffers are registered at wrapper+8 -- so object and header
      // are the same address here, and deliberately so: the header is the only
      // one of the two that every guest bind site names.
      snapshot.buffer.header_address = resource.guest_address;
      snapshot.buffer.stride_or_index_format = resource.stride;

      // *** RE-READ THE HEADER. NEVER SERVE A CACHED BUFFER ADDRESS. ***
      //
      // A buffer's payload moves under it: the physical pool compacts, memcpys
      // the bytes and dispatches the move callback (kRelocateResource), which
      // rewrites the header's stated address through XGOffsetResourceAddress.
      // The header ADDRESS is stable across that; the address it CONTAINS is
      // not. Reading it here, at the moment the consumer asks, is what makes
      // the answer current -- and it costs two guest loads.
      //
      // The stored fields are the fallback, not the source: they are what the
      // last bind stated, which is right up to the next compaction.
      const auto stated = refii::gtl::ReadStatedBufferHeaderLocked(
          refii::renderer::FableGuestMemoryBase(), resource.guest_address,
          snapshot.kind == FableGuestResourceKind::kIndexBuffer);
      const uint32_t data_address =
          stated.valid ? stated.data_address : resource.base_address;
      const uint32_t byte_size =
          stated.valid ? stated.byte_size : resource.byte_size;
      snapshot.buffer.gpu_address = data_address;
      snapshot.buffer.byte_size = byte_size;
      if (stated.index_stride != 0) {
        // Index element size is a header fact (Common>>29), not a bind
        // argument, so it wins over whatever the registry carried.
        snapshot.buffer.stride_or_index_format = stated.index_stride;
      }

      // THE PAYLOAD. This is the field whose absence produced
      // `missing_vertex_input`: the consumer creates an UPLOAD-heap buffer from
      // byte_size and then fills it from `upload_data`, and buffers were the
      // one kind this feed never gave any (IsImageKind excluded them), so every
      // vertex/index buffer materialized 4 bytes wide and empty.
      //
      // The bytes are read from the address the guest STATED in the header --
      // the same address, through the same decode, that D3DDevice_SetStreamSource
      // writes into the fetch constant and that the GPU would fetch from. That
      // is honouring stated data (§5), not reconstructing a descriptor.
      if (want_upload_data && byte_size != 0 &&
          !ConsumerHasCurrentUpload(known, resource.guest_address,
                                    resource.content_generation)) {
        // Cap: a buffer larger than this is not a buffer we can serve in one
        // copy per sync, and silently truncating would hand the consumer a
        // short view it would then bind as if complete.
        constexpr uint32_t kMaxBufferPayloadBytes = 32u << 20;
        uint8_t *const guest_base = refii::renderer::FableGuestMemoryBase();
        if (data_address == 0 || guest_base == nullptr) {
          ++refii::gtl::g_state.stats.buffer_content_no_header;
        } else if (byte_size > kMaxBufferPayloadBytes) {
          ++refii::gtl::g_state.stats.buffer_content_oversize;
        } else if (!refii::renderer::bink::IsReadableGuestByteRange(
                       guest_base, data_address, byte_size)) {
          ++refii::gtl::g_state.stats.buffer_content_unreadable;
        } else {
          const uint8_t *src =
              guest_base + data_address +
              refii::renderer::bink::GuestPhysicalHostOffset(data_address);
          snapshot.upload_data.assign(src, src + byte_size);
          ++refii::gtl::g_state.stats.buffer_content_served;
        }
      }
      if (!snapshot.upload_data.empty() && snapshot.upload_generation == 0) {
        // Content exists but no write root ever announced it. A vertex buffer
        // filled through Lock/Unlock gets a real generation from that root and
        // a relocation bumps it again; a buffer that has neither is still
        // holding bytes the GPU would fetch, and a zero generation makes every
        // upload path discard them (UploadTrackedBuffer's first test).
        snapshot.upload_generation = 1;
      }
      snapshot.buffer.upload_generation = snapshot.upload_generation;
      // upload_hash: still no GTL source; the GTL tracks content by generation.
      break;
    }

    case FableGuestResourceKind::kTexture:
      snapshot.texture.object_address = resource.guest_address;
      snapshot.texture.parent_address = resource.view_parent_texture;
      // The base a re-point (XGOffsetBaseTextureAddress) last stated. It is
      // authoritative over fetch_dwords[1] — the re-point keeps it current and
      // deliberately does not refresh the fetch block.
      snapshot.texture.base_address = resource.base_address;
      snapshot.texture.byte_size = resource.byte_size;
      if (resource.fetch_dwords_stated) {
        snapshot.texture.raw_fetch_dwords = resource.fetch_dwords;
      }
      snapshot.texture.upload_generation = resource.content_generation;
      break;

    case FableGuestResourceKind::kSurface:
    case FableGuestResourceKind::kDepthSurface:
      snapshot.surface.object_address = resource.guest_address;
      snapshot.surface.parent_address = resource.view_parent_texture;
      // STATED at D3DDevice_CreateSurface(Width, Height, Format, ...). The
      // extent is re-packed, not decoded from a hardware descriptor.
      snapshot.surface.packed_extent =
          PackSurfaceExtent(resource.width, resource.height);
      snapshot.surface.d3d_format = resource.format;
      snapshot.surface.pitch_msaa = resource.width;
      break;

    case FableGuestResourceKind::kVertexShader:
    case FableGuestResourceKind::kPixelShader:
      // THE FIELD THAT UNBLOCKS `missing_pipeline`: bytecode_hash is the only
      // key that finds a compiled artifact
      // (FindRendererShaderLibraryEntryByGuestHash). It is FNV-1a-64 over the
      // whole stated shader container — byte-for-byte the range the offline
      // extractor hashed into RendererShaderLibraryEntry::guest_bytecode_hash.
      snapshot.shader.object_address = resource.guest_address;
      snapshot.shader.bytecode_address = resource.shader_bytecode_address;
      snapshot.shader.bytecode_size = resource.shader_bytecode_size;
      snapshot.shader.bytecode_offset = resource.shader_bytecode_offset;
      snapshot.shader.bytecode_hash = resource.shader_bytecode_hash;
      break;

    case FableGuestResourceKind::kVertexDeclaration:
      snapshot.vertex_declaration.object_address = resource.guest_address;
      snapshot.vertex_declaration.elements_address =
          resource.decl_elements_address;
      snapshot.vertex_declaration.elements_size = resource.decl_elements_size;
      snapshot.vertex_declaration.element_count = resource.decl_element_count;
      snapshot.vertex_declaration.max_stream = resource.decl_max_stream;
      snapshot.vertex_declaration.stream_mask = resource.decl_stream_mask;
      snapshot.vertex_declaration.declaration_flags = resource.decl_flags;
      snapshot.vertex_declaration.elements_hash = resource.decl_elements_hash;
      // A D3D9 input layout cannot be rebuilt from a hash, so the elements
      // ALWAYS ride along: DecodeVertexDeclaration rejects an empty payload
      // outright (plume_vertex_declaration.cpp:181). They are tens of bytes.
      snapshot.upload_data = resource.decl_elements;
      if (!snapshot.upload_data.empty() && snapshot.upload_generation == 0) {
        // The elements are content, delivered with the create. Without a
        // non-zero generation every upload path treats them as absent.
        snapshot.upload_generation = 1;
      }
      break;

    case FableGuestResourceKind::kUnknown:
    default:
      break;
  }

  // Content payload, served from the GTL's own content store (unlock ranges,
  // tile writes, and the stated-base tier added 2026-08-04 for content written
  // by raw CPU stores). Only the BASE allocation is served: the store keys base
  // and mip separately because the guest states them separately, and the
  // consumer's upload path takes one linear payload per snapshot.
  if (want_upload_data && IsImageKind(snapshot.kind) &&
      snapshot.upload_data.empty() &&
      !ConsumerHasCurrentUpload(known, resource.guest_address,
                                resource.content_generation)) {
    bool source_linear = false;
    if (refii::gtl::CopyResourceContentLocked(resource.guest_address,
                                              /*mip_range=*/false,
                                              snapshot.upload_data,
                                              source_linear)) {
      snapshot.upload_data_linear = source_linear;
      // upload_data_mip_level stays 0: this is the base allocation, never a
      // split-mip fallback.
      if (snapshot.upload_generation == 0) {
        // Content exists (the stated-base tier can serve bytes for a resource
        // no write root ever announced). A zero generation would make every
        // upload path discard them.
        snapshot.upload_generation = 1;
      }
    }
  }

  return snapshot;
}

std::vector<FableGuestResourceSnapshot> BuildSnapshotsForAddresses(
    const std::unordered_set<uint32_t> &addresses,
    const std::unordered_map<uint32_t, FableGuestResourceUploadCacheEntry>
        *known_resources,
    const std::unordered_set<uint32_t> *upload_data_filter) {
  std::vector<FableGuestResourceSnapshot> out;
  if (addresses.empty()) {
    return out;
  }
  out.reserve(addresses.size());
  std::lock_guard<std::mutex> lock(refii::gtl::g_state.mutex);
  ++refii::gtl::g_state.snapshot_calls;
  for (const uint32_t address : addresses) {
    const auto it = refii::gtl::g_state.resources.find(address);
    if (it == refii::gtl::g_state.resources.end()) {
      // Not a registered resource. Returning nothing is the honest answer and
      // is what the consumer's starved-request recovery expects.
      continue;
    }
    const bool want_upload_data =
        upload_data_filter == nullptr || upload_data_filter->count(address) != 0;
    out.push_back(
        BuildResourceSnapshotLocked(it->second, known_resources,
                                    want_upload_data));
  }
  refii::gtl::g_state.snapshots_served += out.size();
  return out;
}

}  // namespace

std::vector<FableGuestResourceSnapshot>
GetFableDirectResourceSnapshotsForAddresses(
    const std::unordered_set<uint32_t> &addresses,
    const std::unordered_map<uint32_t, FableGuestResourceUploadCacheEntry>
        *known_resources,
    const std::unordered_set<uint32_t> *upload_data_filter,
    bool *observer_busy) {
  if (observer_busy != nullptr) {
    // There is no observer. The GTL lock is held for the duration of the
    // harvest and never degrades to a cache-only fallback, so this feed is
    // never "busy" — and the consumer's busy-path retries must therefore never
    // fire.
    *observer_busy = false;
  }
  return BuildSnapshotsForAddresses(addresses, known_resources,
                                    upload_data_filter);
}

std::vector<FableGuestResourceSnapshot>
GetFableDirectResourceSnapshotsForAddressesBlocking(
    const std::unordered_set<uint32_t> &addresses,
    const std::unordered_map<uint32_t, FableGuestResourceUploadCacheEntry>
        *known_resources,
    const std::unordered_set<uint32_t> *upload_data_filter) {
  // Identical to the non-blocking form: the GTL harvest ALWAYS waits for its
  // own mutex, so there is no degraded variant to escalate from. The two entry
  // points are kept distinct only because the consumer's lock-ordering
  // discipline differs between them (it drops its registry lock around this
  // one).
  return BuildSnapshotsForAddresses(addresses, known_resources,
                                    upload_data_filter);
}

FableGuestResourceChangeSet GetFableDirectResourceChanges(
    bool /*wait_for_observer*/) {
  FableGuestResourceChangeSet changes;
  std::lock_guard<std::mutex> lock(refii::gtl::g_state.mutex);
  changes.serial = refii::gtl::g_state.change_serial;
  changes.observer_busy = false;
  changes.changed_addresses.reserve(
      refii::gtl::g_state.changed_resources.size());
  for (const auto &[address, serial] : refii::gtl::g_state.changed_resources) {
    (void)serial;
    changes.changed_addresses.push_back(address);
  }
  changes.destroyed_addresses.reserve(
      refii::gtl::g_state.destroyed_resources.size());
  changes.destroyed_serials.reserve(
      refii::gtl::g_state.destroyed_resources.size());
  for (const auto &[address, serial] :
       refii::gtl::g_state.destroyed_resources) {
    changes.destroyed_addresses.push_back(address);
    changes.destroyed_serials.push_back(serial);
  }
  // lock_wait_ms / teardown_drain_ms / refresh_ms describe the observer's three
  // internal phases. None of them exists here; leaving them zero is what the
  // sync timing line should report.
  return changes;
}

void DrainPendingFableDirectTextureIdentityOperations() {
  // NOTHING TO DRAIN, BY CONSTRUCTION.
  //
  // The observer queued texture-identity operations because it discovered
  // identity asynchronously (scans, deferred captures) and had to hand them to
  // the direct renderer out of band. The GTL states identity INLINE: every
  // snapshot carries its own fetch block, base and parent, and the consumer
  // registers shader/declaration identity straight off the snapshot in
  // RegisterDirectShaderAndDeclarationIdentityFromSnapshotLocked. There is no
  // second channel to flush.
  //
  // Kept (rather than deleted with its call sites) because it is on the
  // published snapshot contract and its two callers are ordering barriers in
  // the consumer's sync flow.
}

void AcknowledgeFableDirectResourceChanges(uint64_t serial) {
  std::lock_guard<std::mutex> lock(refii::gtl::g_state.mutex);
  // Retire only what the consumer actually saw. An address re-stamped after
  // the watermark it is acknowledging has changed AGAIN since that harvest and
  // must survive into the next one — this is the whole reason the journal
  // stores a serial per address instead of a flag.
  for (auto it = refii::gtl::g_state.changed_resources.begin();
       it != refii::gtl::g_state.changed_resources.end();) {
    if (it->second <= serial) {
      it = refii::gtl::g_state.changed_resources.erase(it);
    } else {
      ++it;
    }
  }
}

void AcknowledgeFableDirectDestroyedResources(
    const std::vector<uint32_t> &addresses,
    const std::vector<uint64_t> &serials) {
  std::lock_guard<std::mutex> lock(refii::gtl::g_state.mutex);
  for (size_t i = 0; i < addresses.size(); ++i) {
    const auto it =
        refii::gtl::g_state.destroyed_resources.find(addresses[i]);
    if (it == refii::gtl::g_state.destroyed_resources.end()) {
      continue;
    }
    // Per-address, and only for the destruction the consumer named. If the
    // address was destroyed AGAIN after this one (recycled heap objects do
    // exactly that), the newer entry stays pending.
    const uint64_t acknowledged = i < serials.size() ? serials[i] : 0ull;
    if (it->second <= acknowledged) {
      refii::gtl::g_state.destroyed_resources.erase(it);
    }
  }
}

}  // namespace refii::renderer
