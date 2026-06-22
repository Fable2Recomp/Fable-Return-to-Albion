#pragma once

#include "runtime/plume/direct/fable_hook_symbols.h"
#include "runtime/plume/direct/fable_resource_snapshots.h"

#include <array>
#include <cstdint>
#include <string_view>
#include <vector>

namespace refii::renderer {

enum class RendererResourceKind {
  kUnknown,
  kTexture,
  kSurfaceView,
  kRenderTargetSurface,
  kDepthSurface,
  kVertexBuffer,
  kIndexBuffer,
  kVertexDeclaration,
  kVertexShader,
  kPixelShader,
};

struct GuestBufferRecord {
  uint32_t object_address = 0;
  uint32_t gpu_address = 0;
  uint32_t byte_size = 0;
  uint32_t stride_or_index_format = 0;
  uint32_t header_address = 0;
  uint64_t upload_hash = 0;
  uint64_t upload_generation = 0;
  bool scratch = false;
};

struct GuestTextureRecord {
  uint32_t object_address = 0;
  uint32_t parent_address = 0;
  uint32_t base_address = 0;
  uint32_t byte_size = 0;
  std::array<uint32_t, 6> raw_fetch_dwords = {};
  std::array<uint32_t, 6> normalized_fetch_dwords = {};
  uint64_t upload_generation = 0;
};

struct GuestSurfaceRecord {
  uint32_t object_address = 0;
  uint32_t parent_address = 0;
  uint32_t render_target_info = 0;
  uint32_t depth_stencil_info = 0;
  uint32_t packed_extent = 0;
  uint32_t d3d_format = 0;
  uint32_t pitch_msaa = 0;
  uint32_t byte_size = 0;
};

struct GuestShaderRecord {
  uint32_t object_address = 0;
  uint32_t bytecode_address = 0;
  uint32_t bytecode_size = 0;
  uint32_t bytecode_offset = 0;
  uint64_t bytecode_hash = 0;
};

struct GuestVertexDeclarationRecord {
  uint32_t object_address = 0;
  uint32_t elements_address = 0;
  uint32_t elements_size = 0;
  uint32_t element_count = 0;
  uint32_t max_stream = 0;
  uint32_t stream_mask = 0;
  uint32_t declaration_flags = 0;
  uint64_t elements_hash = 0;
};

struct GuestResourceObject {
  RendererResourceKind kind = RendererResourceKind::kUnknown;
  uint32_t object_address = 0;
  uint32_t parent_address = 0;
  GuestBufferRecord buffer;
  GuestTextureRecord texture;
  GuestSurfaceRecord surface;
  GuestShaderRecord shader;
  GuestVertexDeclarationRecord vertex_declaration;
};

struct RendererResourceRawMirror {
  uint32_t buffer_object = 0;
  uint32_t size = 0;
  uint32_t stride_or_format = 0;
  uint32_t flags = 0;
  uint32_t ref_count = 0;
  uint32_t aux20 = 0;
  uint32_t parent24 = 0;
  uint32_t packed28 = 0;
  uint32_t packed32 = 0;
  uint32_t packed36 = 0;
  uint32_t packed40 = 0;
  uint32_t packed44 = 0;
  uint32_t packed48 = 0;
  uint64_t hash = 0;
};

struct GuestResourceRuntime {
  RendererResourceKind kind = RendererResourceKind::kUnknown;
  uint32_t guest_address = 0;
  uint32_t parent_address = 0;
  GuestResourceObject object;
  RendererResourceRawMirror raw;
  uint32_t scratch_upload_source_address = 0;
  uint32_t locked_address = 0;
  uint32_t locked_write_address = 0;
  uint32_t locked_size = 0;
  uint64_t upload_generation = 0;
  std::vector<uint8_t> upload_data;
  uint64_t serial = 0;
  bool scratch = false;
};

using RendererResourceRecord = GuestResourceRuntime;

struct PendingPixDecodedVideoFrame {
  uint32_t width = 0;
  uint32_t height = 0;
  uint32_t frame_base = 0;
  uint64_t serial = 0;
};

struct BinkVideoPlaneBinding {
  uint32_t surface = 0;
  uint32_t plane_info = 0;
  uint32_t plane_output_slot = 0;
  uint32_t physical_address = 0;
  uint32_t lock_address = 0;
  uint32_t upload_size = 0;
  uint64_t serial = 0;
};

inline bool IsShaderResourceKind(RendererResourceKind kind) {
  return kind == RendererResourceKind::kVertexShader ||
         kind == RendererResourceKind::kPixelShader;
}

inline bool IsBufferResourceKind(RendererResourceKind kind) {
  return kind == RendererResourceKind::kVertexBuffer ||
         kind == RendererResourceKind::kIndexBuffer;
}

inline bool IsImageResourceKind(RendererResourceKind kind) {
  return kind == RendererResourceKind::kTexture ||
         kind == RendererResourceKind::kSurfaceView ||
         kind == RendererResourceKind::kRenderTargetSurface ||
         kind == RendererResourceKind::kDepthSurface;
}

inline bool IsSurfaceResourceKind(RendererResourceKind kind) {
  return kind == RendererResourceKind::kSurfaceView ||
         kind == RendererResourceKind::kRenderTargetSurface ||
         kind == RendererResourceKind::kDepthSurface;
}

inline bool IsUploadResourceKind(RendererResourceKind kind) {
  return IsBufferResourceKind(kind) || IsImageResourceKind(kind);
}

inline GuestResourceObject CreateGuestResourceObject(
    RendererResourceKind kind, uint32_t object_address,
    uint32_t parent_address = 0) {
  GuestResourceObject object;
  object.kind = kind;
  object.object_address = object_address;
  object.parent_address = parent_address;
  switch (kind) {
    case RendererResourceKind::kTexture:
      object.texture.object_address = object_address;
      object.texture.parent_address = parent_address;
      break;
    case RendererResourceKind::kSurfaceView:
    case RendererResourceKind::kRenderTargetSurface:
    case RendererResourceKind::kDepthSurface:
      object.surface.object_address = object_address;
      object.surface.parent_address = parent_address;
      break;
    case RendererResourceKind::kVertexBuffer:
    case RendererResourceKind::kIndexBuffer:
      object.buffer.object_address = object_address;
      break;
    case RendererResourceKind::kVertexShader:
    case RendererResourceKind::kPixelShader:
      object.shader.object_address = object_address;
      break;
    case RendererResourceKind::kVertexDeclaration:
      object.vertex_declaration.object_address = object_address;
      break;
    case RendererResourceKind::kUnknown:
      break;
  }
  return object;
}

inline RendererResourceRecord CreateGuestResourceRecord(
    RendererResourceKind kind, uint32_t object_address,
    uint32_t parent_address = 0) {
  RendererResourceRecord record;
  record.object = CreateGuestResourceObject(kind, object_address,
                                            parent_address);
  record.kind = record.object.kind;
  record.guest_address = record.object.object_address;
  record.parent_address = record.object.parent_address;
  return record;
}

inline void SetGuestResourceIdentity(RendererResourceRecord& record,
                                     RendererResourceKind kind,
                                     uint32_t object_address,
                                     uint32_t parent_address = 0) {
  record.object.kind = kind;
  record.object.object_address = object_address;
  record.object.parent_address = parent_address;
  record.kind = kind;
  record.guest_address = object_address;
  record.parent_address = parent_address;
  switch (kind) {
    case RendererResourceKind::kTexture:
      record.object.texture.object_address = object_address;
      record.object.texture.parent_address = parent_address;
      break;
    case RendererResourceKind::kSurfaceView:
    case RendererResourceKind::kRenderTargetSurface:
    case RendererResourceKind::kDepthSurface:
      record.object.surface.object_address = object_address;
      record.object.surface.parent_address = parent_address;
      break;
    case RendererResourceKind::kVertexBuffer:
    case RendererResourceKind::kIndexBuffer:
      record.object.buffer.object_address = object_address;
      break;
    case RendererResourceKind::kVertexShader:
    case RendererResourceKind::kPixelShader:
      record.object.shader.object_address = object_address;
      break;
    case RendererResourceKind::kVertexDeclaration:
      record.object.vertex_declaration.object_address = object_address;
      break;
    case RendererResourceKind::kUnknown:
      break;
  }
}

inline RendererResourceRecord CreateGuestTextureResource(
    uint32_t object_address, uint32_t parent_address = 0) {
  return CreateGuestResourceRecord(RendererResourceKind::kTexture,
                                   object_address, parent_address);
}

inline RendererResourceRecord CreateGuestSurfaceResource(
    RendererResourceKind kind, uint32_t object_address,
    uint32_t parent_address = 0) {
  return CreateGuestResourceRecord(kind, object_address, parent_address);
}

inline RendererResourceRecord CreateGuestBufferResource(
    RendererResourceKind kind, uint32_t object_address,
    uint32_t parent_address = 0) {
  return CreateGuestResourceRecord(kind, object_address, parent_address);
}

inline RendererResourceRecord CreateGuestShaderResource(
    RendererResourceKind kind, uint32_t object_address,
    uint32_t parent_address = 0) {
  return CreateGuestResourceRecord(kind, object_address, parent_address);
}

inline RendererResourceRecord CreateGuestVertexDeclarationResource(
    uint32_t object_address, uint32_t parent_address = 0) {
  return CreateGuestResourceRecord(RendererResourceKind::kVertexDeclaration,
                                   object_address, parent_address);
}

inline const GuestResourceObject& GuestObjectFor(
    const RendererResourceRecord& record) {
  return record.object;
}

inline GuestResourceObject& MutableGuestObjectFor(
    RendererResourceRecord& record) {
  return record.object;
}

inline void SyncGuestResourceRuntimeIdentity(RendererResourceRecord& record) {
  record.object.kind = record.kind;
  record.object.object_address = record.guest_address;
  record.object.parent_address = record.parent_address;
}

inline RendererResourceKind GuestObjectKind(
    const RendererResourceRecord& record) {
  return record.object.kind != RendererResourceKind::kUnknown
             ? record.object.kind
             : record.kind;
}

inline uint32_t GuestObjectAddress(const RendererResourceRecord& record) {
  return record.object.object_address != 0 ? record.object.object_address
                                           : record.guest_address;
}

inline uint32_t GuestObjectParentAddress(
    const RendererResourceRecord& record) {
  return record.object.parent_address != 0 ? record.object.parent_address
                                           : record.parent_address;
}

inline void SetGuestResourceParent(RendererResourceRecord& record,
                                   uint32_t parent_address) {
  record.object.parent_address = parent_address;
  record.parent_address = parent_address;
  switch (GuestObjectKind(record)) {
    case RendererResourceKind::kTexture:
      record.object.texture.parent_address = parent_address;
      break;
    case RendererResourceKind::kSurfaceView:
    case RendererResourceKind::kRenderTargetSurface:
    case RendererResourceKind::kDepthSurface:
      record.object.surface.parent_address = parent_address;
      break;
    case RendererResourceKind::kUnknown:
    case RendererResourceKind::kVertexBuffer:
    case RendererResourceKind::kIndexBuffer:
    case RendererResourceKind::kVertexDeclaration:
    case RendererResourceKind::kVertexShader:
    case RendererResourceKind::kPixelShader:
      break;
  }
}

inline void SetGuestResourceRawHeader(RendererResourceRecord& record,
                                      uint32_t flags, uint32_t ref_count,
                                      uint32_t aux20, uint32_t parent24) {
  record.raw.flags = flags;
  record.raw.ref_count = ref_count;
  record.raw.aux20 = aux20;
  record.raw.parent24 = parent24;
}

inline void ClearGuestResourceRawHeader(RendererResourceRecord& record) {
  SetGuestResourceRawHeader(record, 0, 0, 0, record.raw.parent24);
}

inline void SetGuestResourceRawRefCount(RendererResourceRecord& record,
                                        uint32_t ref_count) {
  record.raw.ref_count = ref_count;
}

inline uint32_t GuestResourceRawFlags(const RendererResourceRecord& record) {
  return record.raw.flags;
}

inline std::array<uint32_t, 6> RawFetchDwordsForRecord(
    const RendererResourceRecord& record) {
  return {record.raw.packed28, record.raw.packed32, record.raw.packed36,
          record.raw.packed40, record.raw.packed44, record.raw.packed48};
}

inline void StoreRawFetchDwords(RendererResourceRecord& record,
                                const std::array<uint32_t, 6>& dwords) {
  record.raw.packed28 = dwords[0];
  record.raw.packed32 = dwords[1];
  record.raw.packed36 = dwords[2];
  record.raw.packed40 = dwords[3];
  record.raw.packed44 = dwords[4];
  record.raw.packed48 = dwords[5];
}

inline bool GuestDwordsHaveAnyValue(const std::array<uint32_t, 6>& dwords) {
  for (uint32_t dword : dwords) {
    if (dword != 0) {
      return true;
    }
  }
  return false;
}

inline GuestBufferRecord GuestBufferRecordFor(
    const RendererResourceRecord& record) {
  GuestBufferRecord buffer = record.object.buffer;
  if (!IsBufferResourceKind(GuestObjectKind(record))) {
    return buffer;
  }
  if (buffer.object_address == 0) {
    buffer.object_address = GuestObjectAddress(record);
  }
  if (buffer.gpu_address == 0) {
    buffer.gpu_address = record.raw.buffer_object;
  }
  if (buffer.byte_size == 0) {
    buffer.byte_size = record.raw.size;
  }
  if (buffer.stride_or_index_format == 0) {
    buffer.stride_or_index_format = record.raw.stride_or_format;
  }
  if (buffer.header_address == 0) {
    buffer.header_address = record.raw.parent24;
  }
  if (buffer.upload_hash == 0) {
    buffer.upload_hash = record.raw.hash;
  }
  if (buffer.upload_generation == 0) {
    buffer.upload_generation = record.upload_generation;
  }
  buffer.scratch = buffer.scratch || record.scratch;
  return buffer;
}

inline GuestTextureRecord GuestTextureRecordFor(
    const RendererResourceRecord& record) {
  GuestTextureRecord texture = record.object.texture;
  if (!IsImageResourceKind(GuestObjectKind(record))) {
    return texture;
  }
  if (texture.object_address == 0) {
    texture.object_address = GuestObjectAddress(record);
  }
  if (texture.parent_address == 0) {
    texture.parent_address = GuestObjectParentAddress(record);
  }
  if (texture.base_address == 0) {
    texture.base_address = record.raw.buffer_object;
  }
  if (texture.byte_size == 0) {
    texture.byte_size = record.raw.size;
  }
  if (!GuestDwordsHaveAnyValue(texture.raw_fetch_dwords)) {
    texture.raw_fetch_dwords = RawFetchDwordsForRecord(record);
  }
  if (texture.upload_generation == 0) {
    texture.upload_generation = record.upload_generation;
  }
  return texture;
}

inline GuestSurfaceRecord GuestSurfaceRecordFor(
    const RendererResourceRecord& record) {
  GuestSurfaceRecord surface = record.object.surface;
  if (!IsSurfaceResourceKind(GuestObjectKind(record))) {
    return surface;
  }
  if (surface.object_address == 0) {
    surface.object_address = GuestObjectAddress(record);
  }
  if (surface.parent_address == 0) {
    surface.parent_address = GuestObjectParentAddress(record);
  }
  if (surface.render_target_info == 0) {
    surface.render_target_info = record.raw.packed28;
  }
  if (surface.depth_stencil_info == 0) {
    surface.depth_stencil_info = record.raw.packed32;
  }
  if (surface.packed_extent == 0) {
    surface.packed_extent = record.raw.packed36;
  }
  if (surface.d3d_format == 0) {
    surface.d3d_format = record.raw.packed40;
  }
  if (surface.pitch_msaa == 0) {
    surface.pitch_msaa = record.raw.packed44;
  }
  if (surface.byte_size == 0) {
    surface.byte_size = record.raw.packed48;
  }
  return surface;
}

inline GuestShaderRecord GuestShaderRecordFor(
    const RendererResourceRecord& record) {
  GuestShaderRecord shader = record.object.shader;
  if (!IsShaderResourceKind(GuestObjectKind(record))) {
    return shader;
  }
  if (shader.object_address == 0) {
    shader.object_address = GuestObjectAddress(record);
  }
  if (shader.bytecode_address == 0) {
    shader.bytecode_address = record.raw.buffer_object;
  }
  if (shader.bytecode_size == 0) {
    shader.bytecode_size = record.raw.size;
  }
  if (shader.bytecode_offset == 0) {
    shader.bytecode_offset = record.raw.stride_or_format;
  }
  if (shader.bytecode_hash == 0) {
    shader.bytecode_hash = record.raw.hash;
  }
  return shader;
}

inline GuestVertexDeclarationRecord GuestVertexDeclarationRecordFor(
    const RendererResourceRecord& record) {
  GuestVertexDeclarationRecord declaration = record.object.vertex_declaration;
  if (GuestObjectKind(record) != RendererResourceKind::kVertexDeclaration) {
    return declaration;
  }
  if (declaration.object_address == 0) {
    declaration.object_address = GuestObjectAddress(record);
  }
  if (declaration.elements_address == 0) {
    declaration.elements_address = record.raw.buffer_object;
  }
  if (declaration.elements_size == 0) {
    declaration.elements_size = record.raw.size;
  }
  if (declaration.element_count == 0) {
    declaration.element_count = record.raw.stride_or_format;
  }
  if (declaration.max_stream == 0) {
    declaration.max_stream = record.raw.packed28;
  }
  if (declaration.stream_mask == 0) {
    declaration.stream_mask = record.raw.packed32;
  }
  if (declaration.declaration_flags == 0) {
    declaration.declaration_flags = record.raw.packed48;
  }
  if (declaration.elements_hash == 0) {
    declaration.elements_hash = record.raw.hash;
  }
  return declaration;
}

inline uint32_t GuestResourceDataAddress(const RendererResourceRecord& record) {
  const GuestBufferRecord buffer = GuestBufferRecordFor(record);
  if (buffer.gpu_address != 0) {
    return buffer.gpu_address;
  }
  const GuestTextureRecord texture = GuestTextureRecordFor(record);
  if (texture.base_address != 0) {
    return texture.base_address;
  }
  const GuestShaderRecord shader = GuestShaderRecordFor(record);
  if (shader.bytecode_address != 0) {
    return shader.bytecode_address;
  }
  return GuestVertexDeclarationRecordFor(record).elements_address;
}

inline uint32_t GuestResourceByteSize(const RendererResourceRecord& record) {
  const GuestBufferRecord buffer = GuestBufferRecordFor(record);
  if (buffer.byte_size != 0) {
    return buffer.byte_size;
  }
  const GuestTextureRecord texture = GuestTextureRecordFor(record);
  if (texture.byte_size != 0) {
    return texture.byte_size;
  }
  const GuestShaderRecord shader = GuestShaderRecordFor(record);
  if (shader.bytecode_size != 0) {
    return shader.bytecode_size;
  }
  return GuestVertexDeclarationRecordFor(record).elements_size;
}

inline uint32_t GuestResourceAuxiliaryAddress(
    const RendererResourceRecord& record) {
  const GuestBufferRecord buffer = GuestBufferRecordFor(record);
  return buffer.header_address != 0 ? buffer.header_address
                                    : record.raw.parent24;
}

inline void ApplyGuestBufferRecord(RendererResourceRecord& record,
                                   const GuestBufferRecord& buffer) {
  GuestBufferRecord applied = buffer;
  if (applied.object_address != 0) {
    record.guest_address = applied.object_address;
  } else {
    applied.object_address = record.guest_address;
  }
  SyncGuestResourceRuntimeIdentity(record);
  record.object.buffer = applied;
  record.raw.buffer_object = applied.gpu_address;
  record.raw.size = applied.byte_size;
  record.raw.stride_or_format = applied.stride_or_index_format;
  if (applied.header_address != 0) {
    record.raw.parent24 = applied.header_address;
  }
  record.raw.hash = applied.upload_hash;
  record.upload_generation = applied.upload_generation;
  record.scratch = applied.scratch;
}

inline void ApplyGuestTextureRecord(RendererResourceRecord& record,
                                    const GuestTextureRecord& texture) {
  GuestTextureRecord applied = texture;
  if (applied.object_address != 0) {
    record.guest_address = applied.object_address;
  } else {
    applied.object_address = record.guest_address;
  }
  if (applied.parent_address != 0) {
    record.parent_address = applied.parent_address;
  } else {
    applied.parent_address = record.parent_address;
  }
  SyncGuestResourceRuntimeIdentity(record);
  record.object.texture = applied;
  record.raw.buffer_object = applied.base_address;
  record.raw.size = applied.byte_size;
  StoreRawFetchDwords(record, applied.raw_fetch_dwords);
  record.upload_generation = applied.upload_generation;
}

inline void ApplyGuestSurfaceRecord(RendererResourceRecord& record,
                                    const GuestSurfaceRecord& surface) {
  GuestSurfaceRecord applied = surface;
  if (applied.object_address != 0) {
    record.guest_address = applied.object_address;
  } else {
    applied.object_address = record.guest_address;
  }
  if (applied.parent_address != 0) {
    record.parent_address = applied.parent_address;
  } else {
    applied.parent_address = record.parent_address;
  }
  SyncGuestResourceRuntimeIdentity(record);
  record.object.surface = applied;
  record.raw.packed28 = applied.render_target_info;
  record.raw.packed32 = applied.depth_stencil_info;
  record.raw.packed36 = applied.packed_extent;
  record.raw.packed40 = applied.d3d_format;
  record.raw.packed44 = applied.pitch_msaa;
  record.raw.packed48 = applied.byte_size;
}

inline void ApplyGuestShaderRecord(RendererResourceRecord& record,
                                   const GuestShaderRecord& shader) {
  GuestShaderRecord applied = shader;
  if (applied.object_address != 0) {
    record.guest_address = applied.object_address;
  } else {
    applied.object_address = record.guest_address;
  }
  SyncGuestResourceRuntimeIdentity(record);
  record.object.shader = applied;
  record.raw.buffer_object = applied.bytecode_address;
  record.raw.size = applied.bytecode_size;
  record.raw.stride_or_format = applied.bytecode_offset;
  record.raw.hash = applied.bytecode_hash;
}

inline void ApplyGuestVertexDeclarationRecord(
    RendererResourceRecord& record,
    const GuestVertexDeclarationRecord& declaration) {
  GuestVertexDeclarationRecord applied = declaration;
  if (applied.object_address != 0) {
    record.guest_address = applied.object_address;
  } else {
    applied.object_address = record.guest_address;
  }
  SyncGuestResourceRuntimeIdentity(record);
  record.object.vertex_declaration = applied;
  record.raw.buffer_object = applied.elements_address;
  record.raw.size = applied.elements_size;
  record.raw.stride_or_format = applied.element_count;
  record.raw.packed28 = applied.max_stream;
  record.raw.packed32 = applied.stream_mask;
  record.raw.packed48 = applied.declaration_flags;
  record.raw.hash = applied.elements_hash;
}

inline const char* ResourceKindName(RendererResourceKind kind) {
  switch (kind) {
    case RendererResourceKind::kUnknown:
      return "unknown";
    case RendererResourceKind::kTexture:
      return "texture";
    case RendererResourceKind::kSurfaceView:
      return "surface_view";
    case RendererResourceKind::kRenderTargetSurface:
      return "render_target_surface";
    case RendererResourceKind::kDepthSurface:
      return "depth_surface";
    case RendererResourceKind::kVertexBuffer:
      return "vertex_buffer";
    case RendererResourceKind::kIndexBuffer:
      return "index_buffer";
    case RendererResourceKind::kVertexDeclaration:
      return "vertex_declaration";
    case RendererResourceKind::kVertexShader:
      return "vertex_shader";
    case RendererResourceKind::kPixelShader:
      return "pixel_shader";
  }
  return "unknown";
}

inline RendererTrackedResourceKind PublicResourceKind(
    RendererResourceKind kind) {
  switch (kind) {
    case RendererResourceKind::kTexture:
      return RendererTrackedResourceKind::kTexture;
    case RendererResourceKind::kSurfaceView:
    case RendererResourceKind::kRenderTargetSurface:
      return RendererTrackedResourceKind::kSurface;
    case RendererResourceKind::kDepthSurface:
      return RendererTrackedResourceKind::kDepthSurface;
    case RendererResourceKind::kVertexBuffer:
      return RendererTrackedResourceKind::kVertexBuffer;
    case RendererResourceKind::kIndexBuffer:
      return RendererTrackedResourceKind::kIndexBuffer;
    case RendererResourceKind::kVertexDeclaration:
      return RendererTrackedResourceKind::kVertexDeclaration;
    case RendererResourceKind::kVertexShader:
      return RendererTrackedResourceKind::kVertexShader;
    case RendererResourceKind::kPixelShader:
      return RendererTrackedResourceKind::kPixelShader;
    case RendererResourceKind::kUnknown:
      return RendererTrackedResourceKind::kUnknown;
  }
  return RendererTrackedResourceKind::kUnknown;
}

inline RendererResourceKind ToRendererResourceKind(FableHookResourceKind kind) {
  switch (kind) {
    case FableHookResourceKind::kTexture:
      return RendererResourceKind::kTexture;
    case FableHookResourceKind::kSurfaceView:
      return RendererResourceKind::kSurfaceView;
    case FableHookResourceKind::kRenderTargetSurface:
      return RendererResourceKind::kRenderTargetSurface;
    case FableHookResourceKind::kDepthSurface:
      return RendererResourceKind::kDepthSurface;
    case FableHookResourceKind::kVertexBuffer:
      return RendererResourceKind::kVertexBuffer;
    case FableHookResourceKind::kIndexBuffer:
      return RendererResourceKind::kIndexBuffer;
    case FableHookResourceKind::kVertexDeclaration:
      return RendererResourceKind::kVertexDeclaration;
    case FableHookResourceKind::kVertexShader:
      return RendererResourceKind::kVertexShader;
    case FableHookResourceKind::kPixelShader:
      return RendererResourceKind::kPixelShader;
    case FableHookResourceKind::kUnknown:
      return RendererResourceKind::kUnknown;
  }
  return RendererResourceKind::kUnknown;
}

inline RendererResourceKind FableCreatedRendererResourceKind(
    std::string_view symbol) {
  return ToRendererResourceKind(FableCreatedResourceKind(symbol));
}

inline RendererResourceKind FableLockedBufferRendererResourceKind(
    std::string_view symbol) {
  return ToRendererResourceKind(FableLockedBufferResourceKind(symbol));
}

inline RendererResourceKind FableBoundShaderRendererResourceKind(
    std::string_view symbol) {
  return ToRendererResourceKind(FableBoundShaderKindForSymbol(symbol));
}

inline RendererResourceKind FableRegisteredShaderRendererResourceKind(
    std::string_view symbol) {
  return ToRendererResourceKind(FableRegisteredShaderKindForSymbol(symbol));
}

}  // namespace refii::renderer
