#pragma once

#include "runtime/plume/direct/fable_render_types.h"
#include "runtime/plume/direct/fable_resource_snapshots.h"

#include <array>
#include <algorithm>
#include <cstdint>
#include <cstddef>
#include <deque>
#include <memory>
#include <mutex>
#include <string>
#include <string_view>
#include <unordered_map>
#include <unordered_set>
#include <vector>

#if REFII_RENDERER_HAS_PLUME
#include <plume_render_interface.h>
#include <rex/graphics/xenos.h>
#endif

namespace refii::renderer {

#if REFII_RENDERER_HAS_PLUME
struct PlumeGuestBufferObject {
  uint32_t object_address = 0;
  uint32_t gpu_address = 0;
  uint32_t byte_size = 0;
  uint32_t stride_or_index_format = 0;
  uint32_t header_address = 0;
  uint64_t upload_hash = 0;
  uint64_t upload_generation = 0;
  uint64_t create_serial = 0;
  uint64_t content_serial = 0;
  bool scratch = false;
  bool plume_owned = false;
  std::unique_ptr<plume::RenderBuffer> buffer;
};

struct PlumeGuestTextureObject {
  uint32_t object_address = 0;
  uint32_t parent_address = 0;
  uint32_t base_address = 0;
  uint32_t mip_address = 0;
  uint32_t byte_size = 0;
  uint32_t width = 0;
  uint32_t height = 0;
  uint32_t depth = 0;
  uint32_t mip_levels = 0;
  uint32_t guest_pitch = 0;
  uint32_t guest_format = 0;
  uint32_t guest_dimension = 0;
  plume::RenderFormat format = plume::RenderFormat::UNKNOWN;
  std::array<uint32_t, 6> fetch_dwords = {};
  uint64_t upload_generation = 0;
  uint64_t upload_hash = 0;
  uint64_t create_serial = 0;
  uint64_t content_serial = 0;
  bool tiled = false;
  bool upload_decoded = false;
  bool upload_pending = false;
  bool plume_owned = false;
  std::unique_ptr<plume::RenderTexture> texture;
  std::unique_ptr<plume::RenderTextureView> view;
  uint32_t view_mapping_key = UINT32_MAX;
  mutable std::unordered_map<uint32_t, std::unique_ptr<plume::RenderTextureView>>
      mapped_views;
  plume::RenderBuffer* upload_buffer = nullptr;
  uint64_t upload_offset = 0;
};

struct PlumeGuestSurfaceObject {
  uint32_t object_address = 0;
  uint32_t parent_address = 0;
  uint32_t width = 0;
  uint32_t height = 0;
  uint32_t depth = 0;
  uint32_t d3d_format = 0;
  uint32_t pitch_msaa = 0;
  uint32_t byte_size = 0;
  plume::RenderFormat format = plume::RenderFormat::UNKNOWN;
  uint64_t create_serial = 0;
  uint64_t content_serial = 0;
  bool color_target = false;
  bool depth_target = false;
  bool plume_owned = false;
  plume::RenderTexture* texture = nullptr;
  plume::RenderTextureView* view = nullptr;
  std::unique_ptr<plume::RenderTextureView> attachment_view;
};

struct PlumeGuestShaderObject {
  uint32_t object_address = 0;
  uint32_t bytecode_address = 0;
  uint32_t bytecode_size = 0;
  uint32_t bytecode_offset = 0;
  uint64_t bytecode_hash = 0;
  std::string name;
  uint64_t create_serial = 0;
  uint64_t content_serial = 0;
  bool writes_depth = false;
  bool samples_depth_texture = false;
  bool plume_owned = false;
  std::unique_ptr<plume::RenderShader> shader;
};

struct PlumeGuestVertexDeclarationObject {
  uint32_t object_address = 0;
  uint32_t elements_address = 0;
  uint32_t elements_size = 0;
  uint32_t element_count = 0;
  uint32_t input_slot_count = 0;
  uint32_t input_element_count = 0;
  uint64_t elements_hash = 0;
  uint64_t create_serial = 0;
  uint64_t content_serial = 0;
  bool plume_owned = false;
  std::vector<plume::RenderInputSlot> input_slots;
  std::vector<plume::RenderInputElement> input_elements;
};

struct PlumeTrackedResource {
  RendererTrackedResourceKind kind = RendererTrackedResourceKind::kUnknown;
  uint32_t guest_address = 0;
  uint32_t parent_address = 0;
  uint32_t buffer_object = 0;
  uint32_t size = 0;
  uint32_t stride_or_format = 0;
  bool scratch = false;
  uint64_t guest_bytecode_hash = 0;
  uint64_t upload_generation = 0;
  uint64_t serial = 0;
  uint64_t content_serial = 0;
  uint64_t draw_content_serial = 0;
  uint64_t clear_content_serial = 0;
  bool plume_owned = false;
  std::string shader_name;
  std::string debug_summary;
  std::vector<uint8_t> debug_upload_sample;
  uint32_t debug_first_nonzero_offset = UINT32_MAX;
  std::array<uint8_t, 32> debug_first_nonzero_sample = {};
  uint32_t debug_first_nonzero_sample_size = 0;
  std::vector<uint8_t> index_shadow_upload;
  uint32_t swapped_texcoords = 0;
  bool has_r11g11b10_normal = false;
  bool pixel_shader_writes_depth = false;
  bool pixel_shader_samples_depth_texture = false;
  bool pixel_shader_texture_slot_mask_valid = false;
  uint32_t pixel_shader_texture_slot_mask = 0;
  plume::RenderTextureDimension texture_dimension =
      plume::RenderTextureDimension::UNKNOWN;
  plume::RenderTextureViewDimension texture_view_dimension =
      plume::RenderTextureViewDimension::UNKNOWN;
  plume::RenderFormat texture_format = plume::RenderFormat::UNKNOWN;
  uint32_t texture_width = 0;
  uint32_t texture_height = 0;
  uint32_t texture_depth = 0;
  uint32_t texture_mip_levels = 0;
  uint32_t texture_guest_base = 0;
  uint32_t texture_guest_mips = 0;
  uint32_t texture_guest_pitch = 0;
  uint32_t texture_guest_format = 0;
  uint32_t texture_guest_dimension = 0;
  std::array<uint32_t, 6> texture_fetch_dwords = {};
  uint32_t texture_upload_row_width = 0;
  uint32_t texture_upload_subresources = 0;
  uint64_t texture_upload_subresource_bytes = 0;
  uint64_t texture_upload_size = 0;
  uint64_t texture_upload_offset = 0;
  uint64_t texture_upload_hash = 0;
  uint64_t texture_upload_source_hash = 0;
  uint64_t texture_upload_source_size = 0;
  plume::RenderBuffer* texture_upload_buffer = nullptr;
  bool texture_guest_tiled = false;
  bool texture_render_target = false;
  bool texture_depth_target = false;
  bool texture_upload_has_source = false;
  bool texture_upload_decoded = false;
  bool texture_upload_pending = false;
  bool texture_upload_new_resource = false;
  PlumeGuestBufferObject guest_buffer;
  PlumeGuestTextureObject guest_texture;
  PlumeGuestSurfaceObject guest_surface;
  PlumeGuestShaderObject guest_shader;
  PlumeGuestVertexDeclarationObject guest_vertex_declaration;
};

inline void RefreshPlumeGuestResourceHandlesFromTrackedFields(
    PlumeTrackedResource& tracked) {
  tracked.guest_surface.texture = tracked.guest_texture.texture.get();
  tracked.guest_surface.view = tracked.guest_texture.view.get();
}

inline bool PlumeGuestBufferReady(const PlumeTrackedResource& tracked) {
  return tracked.guest_buffer.buffer != nullptr;
}

inline bool PlumeGuestShaderReady(const PlumeTrackedResource& tracked) {
  return tracked.guest_shader.shader != nullptr;
}

inline bool PlumeGuestDeclarationReady(const PlumeTrackedResource& tracked) {
  return !tracked.guest_vertex_declaration.input_elements.empty();
}

inline bool PlumeGuestImageReady(const PlumeTrackedResource& tracked) {
  return tracked.guest_texture.texture && tracked.guest_texture.view;
}

inline bool PlumeGuestSurfaceReady(const PlumeTrackedResource& tracked) {
  return PlumeGuestImageReady(tracked) &&
         tracked.guest_surface.attachment_view;
}

inline uint32_t PlumeGuestBufferGpuAddress(
    const PlumeTrackedResource& resource) {
  return resource.guest_buffer.gpu_address != 0
             ? resource.guest_buffer.gpu_address
             : resource.buffer_object;
}

inline uint32_t PlumeGuestBufferByteSize(
    const PlumeTrackedResource& resource) {
  return resource.guest_buffer.byte_size != 0 ? resource.guest_buffer.byte_size
                                              : resource.size;
}

inline uint32_t PlumeGuestBufferStrideOrFormat(
    const PlumeTrackedResource& resource) {
  return resource.guest_buffer.stride_or_index_format != 0
             ? resource.guest_buffer.stride_or_index_format
             : resource.stride_or_format;
}

inline uint32_t PlumeGuestTextureBaseAddress(
    const PlumeTrackedResource& resource) {
  return resource.guest_texture.base_address != 0
             ? resource.guest_texture.base_address
             : resource.texture_guest_base;
}

inline uint32_t PlumeGuestTextureMipAddress(
    const PlumeTrackedResource& resource) {
  return resource.guest_texture.mip_address != 0
             ? resource.guest_texture.mip_address
             : resource.texture_guest_mips;
}

inline uint32_t PlumeGuestTexturePitch(
    const PlumeTrackedResource& resource) {
  return resource.guest_texture.guest_pitch != 0
             ? resource.guest_texture.guest_pitch
             : resource.texture_guest_pitch;
}

inline uint32_t PlumeGuestTextureGuestFormat(
    const PlumeTrackedResource& resource) {
  return resource.guest_texture.guest_format != 0
             ? resource.guest_texture.guest_format
             : resource.texture_guest_format;
}

inline uint32_t PlumeGuestTextureDimension(
    const PlumeTrackedResource& resource) {
  return resource.guest_texture.guest_dimension != 0
             ? resource.guest_texture.guest_dimension
             : resource.texture_guest_dimension;
}

inline plume::RenderFormat PlumeGuestTextureFormat(
    const PlumeTrackedResource& resource) {
  return resource.guest_texture.format != plume::RenderFormat::UNKNOWN
             ? resource.guest_texture.format
             : resource.texture_format;
}

inline uint32_t PlumeGuestTextureWidth(const PlumeTrackedResource& resource) {
  return resource.guest_texture.width != 0 ? resource.guest_texture.width
                                           : resource.texture_width;
}

inline uint32_t PlumeGuestTextureHeight(const PlumeTrackedResource& resource) {
  return resource.guest_texture.height != 0 ? resource.guest_texture.height
                                            : resource.texture_height;
}

inline uint32_t PlumeGuestTextureDepth(const PlumeTrackedResource& resource) {
  return resource.guest_texture.depth != 0 ? resource.guest_texture.depth
                                           : resource.texture_depth;
}

inline const std::array<uint32_t, 6>& PlumeGuestTextureFetchDwords(
    const PlumeTrackedResource& resource) {
  const bool typed_fetch =
      (resource.guest_texture.fetch_dwords[0] |
       resource.guest_texture.fetch_dwords[1] |
       resource.guest_texture.fetch_dwords[2] |
       resource.guest_texture.fetch_dwords[3] |
       resource.guest_texture.fetch_dwords[4] |
       resource.guest_texture.fetch_dwords[5]) != 0;
  return typed_fetch ? resource.guest_texture.fetch_dwords
                     : resource.texture_fetch_dwords;
}

inline uint32_t PlumeGuestResourceDataAddress(
    const PlumeTrackedResource& resource) {
  switch (resource.kind) {
    case RendererTrackedResourceKind::kVertexBuffer:
    case RendererTrackedResourceKind::kIndexBuffer:
      return PlumeGuestBufferGpuAddress(resource);
    case RendererTrackedResourceKind::kTexture:
      return PlumeGuestTextureBaseAddress(resource);
    case RendererTrackedResourceKind::kSurface:
    case RendererTrackedResourceKind::kDepthSurface:
      return resource.guest_surface.object_address != 0
                 ? resource.guest_surface.object_address
                 : resource.buffer_object;
    case RendererTrackedResourceKind::kVertexShader:
    case RendererTrackedResourceKind::kPixelShader:
      return resource.guest_shader.bytecode_address != 0
                 ? resource.guest_shader.bytecode_address
                 : resource.buffer_object;
    case RendererTrackedResourceKind::kVertexDeclaration:
      return resource.guest_vertex_declaration.elements_address != 0
                 ? resource.guest_vertex_declaration.elements_address
                 : resource.buffer_object;
    case RendererTrackedResourceKind::kUnknown:
      break;
  }
  return resource.buffer_object;
}

inline uint32_t PlumeGuestResourceByteSize(
    const PlumeTrackedResource& resource) {
  switch (resource.kind) {
    case RendererTrackedResourceKind::kVertexBuffer:
    case RendererTrackedResourceKind::kIndexBuffer:
      return PlumeGuestBufferByteSize(resource);
    case RendererTrackedResourceKind::kTexture:
      return resource.guest_texture.byte_size != 0 ? resource.guest_texture.byte_size
                                                   : resource.size;
    case RendererTrackedResourceKind::kSurface:
    case RendererTrackedResourceKind::kDepthSurface:
      return resource.guest_surface.byte_size != 0 ? resource.guest_surface.byte_size
                                                   : resource.size;
    case RendererTrackedResourceKind::kVertexShader:
    case RendererTrackedResourceKind::kPixelShader:
      return resource.guest_shader.bytecode_size != 0
                 ? resource.guest_shader.bytecode_size
                 : resource.size;
    case RendererTrackedResourceKind::kVertexDeclaration:
      return resource.guest_vertex_declaration.elements_size != 0
                 ? resource.guest_vertex_declaration.elements_size
                 : resource.size;
    case RendererTrackedResourceKind::kUnknown:
      break;
  }
  return resource.size;
}

inline uint32_t PlumeGuestResourceMeta(const PlumeTrackedResource& resource) {
  switch (resource.kind) {
    case RendererTrackedResourceKind::kVertexBuffer:
    case RendererTrackedResourceKind::kIndexBuffer:
      return PlumeGuestBufferStrideOrFormat(resource);
    case RendererTrackedResourceKind::kTexture:
      return 0;
    case RendererTrackedResourceKind::kSurface:
    case RendererTrackedResourceKind::kDepthSurface:
      return resource.guest_surface.pitch_msaa != 0
                 ? resource.guest_surface.pitch_msaa
                 : resource.stride_or_format;
    case RendererTrackedResourceKind::kVertexShader:
    case RendererTrackedResourceKind::kPixelShader:
      return resource.guest_shader.bytecode_offset != 0
                 ? resource.guest_shader.bytecode_offset
                 : resource.stride_or_format;
    case RendererTrackedResourceKind::kVertexDeclaration:
      return resource.guest_vertex_declaration.element_count != 0
                 ? resource.guest_vertex_declaration.element_count
                 : resource.stride_or_format;
    case RendererTrackedResourceKind::kUnknown:
      break;
  }
  return resource.stride_or_format;
}

struct FableGuestResource {
  const PlumeTrackedResource* tracked = nullptr;

  explicit operator bool() const {
    return tracked != nullptr;
  }

  uint32_t guest_address() const {
    return tracked ? tracked->guest_address : 0u;
  }

  uint64_t serial() const {
    return tracked ? tracked->serial : 0u;
  }

  uint64_t content_serial() const {
    return tracked ? std::max(tracked->content_serial, tracked->serial) : 0u;
  }

  bool plume_owned() const {
    return tracked && tracked->plume_owned;
  }
};

struct FableGuestBuffer : FableGuestResource {
  plume::RenderBuffer* buffer() const {
    if (!tracked) {
      return nullptr;
    }
    return tracked->guest_buffer.buffer.get();
  }

  bool ready() const {
    return buffer() != nullptr;
  }

  uint32_t byte_size() const {
    if (!tracked) {
      return 0u;
    }
    return PlumeGuestBufferByteSize(*tracked);
  }

  uint32_t stride_or_format() const {
    if (!tracked) {
      return 0u;
    }
    return PlumeGuestBufferStrideOrFormat(*tracked);
  }
};

struct FableGuestTexture : FableGuestResource {
  plume::RenderTexture* texture() const {
    if (!tracked) {
      return nullptr;
    }
    return tracked->guest_texture.texture.get();
  }

  plume::RenderTextureView* view() const {
    if (!tracked) {
      return nullptr;
    }
    return tracked->guest_texture.view.get();
  }

  bool ready() const {
    return texture() != nullptr && view() != nullptr;
  }

  plume::RenderFormat format() const {
    return tracked ? PlumeGuestTextureFormat(*tracked)
                   : plume::RenderFormat::UNKNOWN;
  }

  uint32_t width() const {
    return tracked ? PlumeGuestTextureWidth(*tracked) : 0u;
  }

  uint32_t height() const {
    return tracked ? PlumeGuestTextureHeight(*tracked) : 0u;
  }
};

struct FableGuestSurface : FableGuestTexture {
  plume::RenderTextureView* attachment_view() const {
    if (!tracked) {
      return nullptr;
    }
    return tracked->guest_surface.attachment_view.get();
  }

  bool ready() const {
    return FableGuestTexture::ready() && attachment_view() != nullptr;
  }

  bool color_target() const {
    return tracked && tracked->guest_surface.color_target;
  }

  bool depth_target() const {
    return tracked && tracked->guest_surface.depth_target;
  }
};

struct FableGuestShader : FableGuestResource {
  plume::RenderShader* shader() const {
    if (!tracked) {
      return nullptr;
    }
    return tracked->guest_shader.shader.get();
  }

  bool ready() const {
    return shader() != nullptr;
  }

  std::string_view name() const {
    return tracked ? std::string_view(tracked->guest_shader.name)
                   : std::string_view();
  }
};

struct FableGuestVertexDeclaration : FableGuestResource {
  const std::vector<plume::RenderInputSlot>& input_slots() const {
    static const std::vector<plume::RenderInputSlot> empty;
    return tracked ? tracked->guest_vertex_declaration.input_slots : empty;
  }

  const std::vector<plume::RenderInputElement>& input_elements() const {
    static const std::vector<plume::RenderInputElement> empty;
    return tracked ? tracked->guest_vertex_declaration.input_elements : empty;
  }

  bool ready() const {
    return !input_elements().empty();
  }
};

struct FableTextureFetchKey {
  std::array<uint32_t, 6> dwords = {};

  bool operator==(const FableTextureFetchKey& other) const {
    return dwords == other.dwords;
  }
};

struct FableTextureFetchKeyHash {
  size_t operator()(const FableTextureFetchKey& key) const {
    size_t hash = 1469598103934665603ull;
    for (const uint32_t dword : key.dwords) {
      hash ^= dword;
      hash *= 1099511628211ull;
    }
    return hash;
  }
};

struct FableTextureShapeKey {
  uint32_t base = 0;
  uint32_t mips = 0;
  uint32_t pitch = 0;
  uint32_t format = 0;
  uint32_t dimension = 0;

  bool operator==(const FableTextureShapeKey& other) const {
    return base == other.base && mips == other.mips && pitch == other.pitch &&
           format == other.format && dimension == other.dimension;
  }
};

struct FableTextureShapeKeyHash {
  size_t operator()(const FableTextureShapeKey& key) const {
    const uint32_t fields[] = {
        key.base, key.mips, key.pitch, key.format, key.dimension};
    size_t hash = 1469598103934665603ull;
    for (const uint32_t field : fields) {
      hash ^= field;
      hash *= 1099511628211ull;
    }
    return hash;
  }
};

struct FableSurfaceTargetKey {
  uint32_t kind = 0;
  std::array<uint32_t, 7> packed_words = {};

  bool operator==(const FableSurfaceTargetKey& other) const {
    return kind == other.kind && packed_words == other.packed_words;
  }
};

struct FableSurfaceTargetKeyHash {
  size_t operator()(const FableSurfaceTargetKey& key) const {
    size_t hash = 1469598103934665603ull;
    hash ^= key.kind;
    hash *= 1099511628211ull;
    for (const uint32_t word : key.packed_words) {
      hash ^= word;
      hash *= 1099511628211ull;
    }
    return hash;
  }
};

struct FableResourceLookupKey {
  uint32_t address = 0;
  uint32_t kind = 0;
  uint64_t serial = 0;

  bool operator==(const FableResourceLookupKey& other) const {
    return address == other.address && kind == other.kind &&
           serial == other.serial;
  }
};

struct FableResourceLookupKeyHash {
  size_t operator()(const FableResourceLookupKey& key) const {
    size_t hash = 1469598103934665603ull;
    hash ^= key.address;
    hash *= 1099511628211ull;
    hash ^= key.kind;
    hash *= 1099511628211ull;
    hash ^= static_cast<uint32_t>(key.serial);
    hash *= 1099511628211ull;
    hash ^= static_cast<uint32_t>(key.serial >> 32u);
    hash *= 1099511628211ull;
    return hash;
  }
};

struct FableTextureFetchLookupKey {
  FableTextureFetchKey fetch = {};
  uint64_t serial = 0;

  bool operator==(const FableTextureFetchLookupKey& other) const {
    return fetch == other.fetch && serial == other.serial;
  }
};

struct FableTextureFetchLookupKeyHash {
  size_t operator()(const FableTextureFetchLookupKey& key) const {
    size_t hash = FableTextureFetchKeyHash{}(key.fetch);
    hash ^= static_cast<uint32_t>(key.serial);
    hash *= 1099511628211ull;
    hash ^= static_cast<uint32_t>(key.serial >> 32u);
    hash *= 1099511628211ull;
    return hash;
  }
};

struct FableRenderTargetResolveAlias {
  uint32_t destination_base = 0;
  uint32_t normalized_destination_base = 0;
  uint32_t source_address = 0;
  uint32_t source_slot = 0;
  uint32_t destination_pitch = 0;
  uint32_t destination_height = 0;
  uint32_t destination_format = 0;
  uint32_t destination_info = 0;
  uint32_t copy_control = 0;
  uint32_t source_width = 0;
  uint32_t source_height = 0;
  uint32_t display_width = 0;
  uint32_t display_height = 0;
  uint64_t serial = 0;
  bool depth = false;
  bool display_qualified = false;
};

struct FableSurfaceShape {
  uint32_t width = 0;
  uint32_t height = 0;
  uint32_t pitch_width = 0;
  uint32_t aligned_width = 0;
  uint32_t multisample_type = 0;
  plume::RenderFormat format = plume::RenderFormat::UNKNOWN;
};

struct PlumeRetiredResource {
  uint64_t retire_epoch = 0;
  uint64_t valid_until_serial = 0;
  PlumeTrackedResource resource;
};

struct PlumeRetiredFramebuffer {
  uint64_t retire_epoch = 0;
  std::unique_ptr<plume::RenderFramebuffer> framebuffer;
};

constexpr uint64_t kRetiredPlumeResourceEpochs = 16;
constexpr uint64_t kRetiredPlumeBufferResourceEpochs = 256;
constexpr size_t kMaxRetiredPlumeResources = 4096;
constexpr uint32_t kMaxFableTextureSlots = 32;
constexpr uint32_t kMaxFableRenderTargets = 4;
constexpr uint32_t kFableDescriptorCount = 4096;
#if defined(_WIN32)
constexpr uint32_t kFableSamplerDescriptorCount = 1024;
#else
constexpr uint32_t kFableSamplerDescriptorCount = kFableDescriptorCount;
#endif
constexpr uint32_t kPresentBlitDescriptorSetRingSize = 8;
constexpr uint32_t kFableClearColorTargetMask = 0xFu;
constexpr uint32_t kFableClearDepth = 0x10u;
constexpr uint32_t kFableClearStencil = 0x20u;
constexpr size_t kMaxFableFramebufferCacheEntries = 256;

struct FableDrawTargetDesc {
  uint32_t color_address = 0;
  uint32_t depth_address = 0;
  std::array<uint32_t, kMaxFableRenderTargets> color_addresses = {};
  std::array<uint32_t, kMaxFableRenderTargets> color_widths = {};
  std::array<uint32_t, kMaxFableRenderTargets> color_heights = {};
  std::array<uint32_t, kMaxFableRenderTargets> color_guest_widths = {};
  std::array<uint32_t, kMaxFableRenderTargets> color_guest_heights = {};
  std::array<plume::RenderFormat, kMaxFableRenderTargets> color_formats = {};
  std::array<plume::RenderTexture*, kMaxFableRenderTargets> color_textures = {};
  std::array<plume::RenderTextureView*, kMaxFableRenderTargets> color_views = {};
  std::array<uint64_t, kMaxFableRenderTargets> color_resource_serials = {};
  uint32_t color_width = 0;
  uint32_t color_height = 0;
  uint32_t color_guest_width = 0;
  uint32_t color_guest_height = 0;
  uint32_t depth_width = 0;
  uint32_t depth_height = 0;
  plume::RenderFormat color_format = plume::RenderFormat::B8G8R8A8_UNORM;
  plume::RenderFormat depth_format = plume::RenderFormat::UNKNOWN;
  plume::RenderTexture* color_texture = nullptr;
  plume::RenderTextureView* color_view = nullptr;
  plume::RenderTexture* depth_texture = nullptr;
  plume::RenderTextureView* depth_view = nullptr;
  uint64_t color_resource_serial = 0;
  uint64_t depth_resource_serial = 0;
  bool has_color = false;
  bool has_depth = false;
  uint32_t color_count = 0;
};

struct PlumeDepthSampleCopy {
  uint32_t width = 0;
  uint32_t height = 0;
  plume::RenderFormat format = plume::RenderFormat::UNKNOWN;
  std::unique_ptr<plume::RenderTexture> texture;
  std::unique_ptr<plume::RenderTextureView> view;
};

struct PlumeColorSampleCopy {
  uint32_t width = 0;
  uint32_t height = 0;
  plume::RenderFormat format = plume::RenderFormat::UNKNOWN;
  std::unique_ptr<plume::RenderTexture> texture;
  std::unique_ptr<plume::RenderTextureView> view;
  mutable std::array<std::unique_ptr<plume::RenderTextureView>, 3> plane_views;
};

struct PlumeColorAttachmentPadding {
  uint32_t width = 0;
  uint32_t height = 0;
  plume::RenderFormat format = plume::RenderFormat::UNKNOWN;
  std::unique_ptr<plume::RenderTexture> texture;
  std::unique_ptr<plume::RenderTextureView> view;
};

using PlumeDepthSampleCopySlots =
    std::array<const PlumeDepthSampleCopy*, kMaxFableTextureSlots>;
using PlumeColorSampleCopySlots =
    std::array<const PlumeColorSampleCopy*, kMaxFableTextureSlots>;

struct PlumeFablePushConstants {
  uint64_t vertex_shader_constants = 0;
  uint64_t pixel_shader_constants = 0;
  uint64_t shared_constants = 0;
};

struct PlumePresentBlitPushConstants {
  float uv_scale[2] = {1.0f, 1.0f};
  float uv_bias[2] = {0.0f, 0.0f};
};

struct PlumePresentOutputCacheEntry {
  const plume::RenderTexture* source_texture = nullptr;
  uint32_t present_target = 0;
  uint64_t content_serial = 0;
  uint32_t source_width = 0;
  uint32_t source_height = 0;
  uint32_t output_width = 0;
  uint32_t output_height = 0;
  plume::RenderFormat source_format = plume::RenderFormat::UNKNOWN;
  bool blit = false;
  bool scaler = false;
  PlumePresentBlitPushConstants push_constants;
};

struct FablePrivateTextureRectangleDirectVertex {
  float position[4] = {};
  float texcoord0[4] = {};
  float texcoord1[4] = {};
};

struct PlumeCachedIndexBuffer {
  uint32_t index_count = 0;
  std::unique_ptr<plume::RenderBuffer> buffer;
};

struct PlumeCachedTextureDescriptor {
  plume::RenderTexture* texture = nullptr;
  plume::RenderTextureView* view = nullptr;
  plume::RenderTextureLayout layout = plume::RenderTextureLayout::UNKNOWN;
};

struct PlumeTextureDescriptorKey {
  plume::RenderTexture* texture = nullptr;
  plume::RenderTextureView* view = nullptr;
  plume::RenderTextureLayout layout = plume::RenderTextureLayout::UNKNOWN;

  bool operator==(const PlumeTextureDescriptorKey& other) const {
    return texture == other.texture && view == other.view &&
           layout == other.layout;
  }
};

struct PlumeTextureDescriptorKeyHash {
  size_t operator()(const PlumeTextureDescriptorKey& key) const {
    const uintptr_t texture = reinterpret_cast<uintptr_t>(key.texture);
    const uintptr_t view = reinterpret_cast<uintptr_t>(key.view);
    size_t hash = std::hash<uintptr_t>{}(texture);
    hash ^= std::hash<uintptr_t>{}(view) + 0x9E3779B97F4A7C15ull +
            (hash << 6u) + (hash >> 2u);
    hash ^= std::hash<uint32_t>{}(
                static_cast<uint32_t>(key.layout)) +
            0x9E3779B97F4A7C15ull + (hash << 6u) + (hash >> 2u);
    return hash;
  }
};

struct FableSampledTextureLookupKey {
  uint32_t guest_texture = 0;
  uint32_t slot = 0;
  uint32_t render_target0 = 0;
  uint32_t depth_stencil = 0;
  uint64_t resource_serial = 0;
  bool allow_depth = false;
  std::array<uint32_t, 6> fetch_dwords = {};

  bool operator==(const FableSampledTextureLookupKey& other) const {
    return guest_texture == other.guest_texture && slot == other.slot &&
           render_target0 == other.render_target0 &&
           depth_stencil == other.depth_stencil &&
           resource_serial == other.resource_serial &&
           allow_depth == other.allow_depth &&
           fetch_dwords == other.fetch_dwords;
  }
};

struct FableSampledTextureLookupKeyHash {
  size_t operator()(const FableSampledTextureLookupKey& key) const {
    size_t hash = std::hash<uint32_t>{}(key.guest_texture);
    const auto mix = [&hash](auto value) {
      hash ^= std::hash<decltype(value)>{}(value) + 0x9E3779B97F4A7C15ull +
              (hash << 6u) + (hash >> 2u);
    };
    mix(key.slot);
    mix(key.render_target0);
    mix(key.depth_stencil);
    mix(key.resource_serial);
    mix(static_cast<uint32_t>(key.allow_depth ? 1u : 0u));
    for (uint32_t dword : key.fetch_dwords) {
      mix(dword);
    }
    return hash;
  }
};

struct FableSurfaceAliasCopyCacheEntry {
  uint64_t source_content_serial = 0;
  uint64_t destination_content_serial = 0;
};

struct FableTextureAliasPairKey {
  const PlumeTrackedResource* lhs = nullptr;
  const PlumeTrackedResource* rhs = nullptr;

  bool operator==(const FableTextureAliasPairKey& other) const {
    return lhs == other.lhs && rhs == other.rhs;
  }
};

struct FableTextureAliasPairKeyHash {
  size_t operator()(const FableTextureAliasPairKey& key) const {
    const uintptr_t lhs = reinterpret_cast<uintptr_t>(key.lhs);
    const uintptr_t rhs = reinterpret_cast<uintptr_t>(key.rhs);
    return std::hash<uintptr_t>{}(lhs ^ (rhs + 0x9E3779B97F4A7C15ull +
                                         (lhs << 6u) + (lhs >> 2u)));
  }
};

struct FableLiveRenderTargetAliasKey {
  const PlumeTrackedResource* sampled = nullptr;
  uint64_t sampled_serial = 0;
  uint64_t lookup_serial = 0;
  bool allow_depth = false;

  bool operator==(const FableLiveRenderTargetAliasKey& other) const {
    return sampled == other.sampled &&
           sampled_serial == other.sampled_serial &&
           lookup_serial == other.lookup_serial &&
           allow_depth == other.allow_depth;
  }
};

struct FableLiveRenderTargetAliasKeyHash {
  size_t operator()(const FableLiveRenderTargetAliasKey& key) const {
    const uintptr_t sampled = reinterpret_cast<uintptr_t>(key.sampled);
    size_t hash = std::hash<uintptr_t>{}(sampled);
    hash ^= std::hash<uint64_t>{}(key.sampled_serial) +
            0x9E3779B97F4A7C15ull + (hash << 6u) + (hash >> 2u);
    hash ^= std::hash<uint64_t>{}(key.lookup_serial) +
            0x9E3779B97F4A7C15ull + (hash << 6u) + (hash >> 2u);
    hash ^= std::hash<uint32_t>{}(key.allow_depth ? 1u : 0u) +
            0x9E3779B97F4A7C15ull + (hash << 6u) + (hash >> 2u);
    return hash;
  }
};

struct FableSampledObjectLookupKey {
  uint32_t guest_texture = 0;
  uint64_t lookup_serial = 0;
  bool allow_depth = false;
  bool no_alias = false;

  bool operator==(const FableSampledObjectLookupKey& other) const {
    return guest_texture == other.guest_texture &&
           lookup_serial == other.lookup_serial &&
           allow_depth == other.allow_depth && no_alias == other.no_alias;
  }
};

struct FableSampledObjectLookupKeyHash {
  size_t operator()(const FableSampledObjectLookupKey& key) const {
    size_t hash = std::hash<uint32_t>{}(key.guest_texture);
    hash ^= std::hash<uint64_t>{}(key.lookup_serial) +
            0x9E3779B97F4A7C15ull + (hash << 6u) + (hash >> 2u);
    hash ^= std::hash<uint32_t>{}(key.allow_depth ? 1u : 0u) +
            0x9E3779B97F4A7C15ull + (hash << 6u) + (hash >> 2u);
    hash ^= std::hash<uint32_t>{}(key.no_alias ? 1u : 0u) +
            0x9E3779B97F4A7C15ull + (hash << 6u) + (hash >> 2u);
    return hash;
  }
};

struct FableDrawTextureDescriptorKey {
  plume::RenderTexture* texture = nullptr;
  uint32_t set_index = 0;
  uint32_t mapping_key = 0;
  uint32_t layout = 0;
  uint64_t descriptor_epoch = 0;

  bool operator==(const FableDrawTextureDescriptorKey& other) const {
    return texture == other.texture && set_index == other.set_index &&
           mapping_key == other.mapping_key && layout == other.layout &&
           descriptor_epoch == other.descriptor_epoch;
  }
};

struct FableDrawTextureDescriptorKeyHash {
  size_t operator()(const FableDrawTextureDescriptorKey& key) const {
    const uintptr_t texture = reinterpret_cast<uintptr_t>(key.texture);
    size_t hash = std::hash<uintptr_t>{}(texture);
    const auto mix = [&hash](auto value) {
      hash ^= std::hash<decltype(value)>{}(value) + 0x9E3779B97F4A7C15ull +
              (hash << 6u) + (hash >> 2u);
    };
    mix(key.set_index);
    mix(key.mapping_key);
    mix(key.layout);
    mix(key.descriptor_epoch);
    return hash;
  }
};

struct FableDrawTextureDescriptorCacheEntry {
  plume::RenderTextureView* view = nullptr;
  uint32_t descriptor = 0;
};

struct FableDrawTextureBindingKey {
  FableSampledTextureLookupKey lookup = {};
  uint64_t descriptor_epoch = 0;

  bool operator==(const FableDrawTextureBindingKey& other) const {
    return lookup == other.lookup &&
           descriptor_epoch == other.descriptor_epoch;
  }
};

struct FableDrawTextureBindingKeyHash {
  size_t operator()(const FableDrawTextureBindingKey& key) const {
    size_t hash = FableSampledTextureLookupKeyHash{}(key.lookup);
    hash ^= std::hash<uint64_t>{}(key.descriptor_epoch) +
            0x9E3779B97F4A7C15ull + (hash << 6u) + (hash >> 2u);
    return hash;
  }
};

struct FableDrawTextureBindingCacheEntry {
  const PlumeTrackedResource* resource = nullptr;
  plume::RenderTexture* texture = nullptr;
  plume::RenderTextureView* view = nullptr;
  uint32_t set_index = 0;
  uint32_t descriptor = 0;
};

struct FableDrawDescriptorIndices {
  std::array<uint32_t, kMaxFableTextureSlots> texture2d = {};
  std::array<uint32_t, kMaxFableTextureSlots> texture3d = {};
  std::array<uint32_t, kMaxFableTextureSlots> texture_cube = {};
  std::array<uint32_t, kMaxFableTextureSlots> samplers = {};
};

struct FablePixVideoPlaneSet {
  const PlumeTrackedResource* y = nullptr;
  const PlumeTrackedResource* cr = nullptr;
  const PlumeTrackedResource* cb = nullptr;

  explicit operator bool() const {
    return y != nullptr && cr != nullptr && cb != nullptr;
  }

  const PlumeTrackedResource* plane(uint32_t slot) const {
    switch (slot) {
      case 0:
        return y;
      case 1:
        return cr;
      case 2:
        return cb;
      default:
        return nullptr;
    }
  }
};

struct PlumeCachedInputLayout {
  std::vector<plume::RenderInputSlot> input_slots;
  std::vector<plume::RenderInputElement> input_elements;
};
#endif

enum class FablePresentModelSource : uint32_t {
  kNone,
  kPm4Resolve,
  kSurfaceAlias,
  kDisplaySurfaceAlias,
  kDisplayResolveAlias,
  kCurrentFrameTarget,
};

struct FablePresentModel {
  FablePresentModelSource source = FablePresentModelSource::kNone;
  uint32_t source_render_target = 0;
  uint32_t destination_surface = 0;
  uint32_t display_texture_base = 0;
  uint32_t display_surface_object = 0;
  uint32_t width = 0;
  uint32_t height = 0;
  uint64_t present_serial = 0;
  uint64_t resource_serial = 0;

  bool valid() const {
    return source != FablePresentModelSource::kNone &&
           source_render_target != 0 && width != 0 && height != 0;
  }
};

struct FableSuppressedSurfaceCopyAlias {
  RendererSurfaceCopyAliasSnapshot alias;
  uint32_t destination_surface = 0;
  uint32_t source_surface = 0;
  uint64_t serial = 0;
};

struct FableSuppressedTextureCopyAlias {
  uint32_t destination_surface = 0;
  uint32_t source_texture = 0;
  uint64_t serial = 0;
};

struct PlumeFrameTextureUploadSlab {
  std::unique_ptr<plume::RenderBuffer> buffer;
  uint8_t* mapped = nullptr;
  uint64_t size = 0;
  uint64_t offset = 0;
};

struct PlumeTextureUploadDecodeCacheKey {
  uint64_t source_hash = 0;
  uint64_t source_size = 0;
  uint32_t format = 0;
  uint32_t width = 0;
  uint32_t height = 0;
  uint32_t depth = 0;
  uint32_t view_dimension = 0;
  uint32_t guest_format = 0;
  uint32_t guest_dimension = 0;
  uint32_t guest_pitch = 0;
  std::array<uint32_t, 6> fetch_dwords = {};

  bool operator==(const PlumeTextureUploadDecodeCacheKey& other) const {
    return source_hash == other.source_hash &&
           source_size == other.source_size && format == other.format &&
           width == other.width && height == other.height &&
           depth == other.depth && view_dimension == other.view_dimension &&
           guest_format == other.guest_format &&
           guest_dimension == other.guest_dimension &&
           guest_pitch == other.guest_pitch &&
           fetch_dwords == other.fetch_dwords;
  }
};

struct PlumeTextureUploadDecodeCacheKeyHash {
  size_t operator()(const PlumeTextureUploadDecodeCacheKey& key) const {
    uint64_t hash = 14695981039346656037ull;
    auto mix = [&hash](uint64_t value) {
      hash ^= value;
      hash *= 1099511628211ull;
    };
    mix(key.source_hash);
    mix(key.source_size);
    mix(key.format);
    mix(key.width);
    mix(key.height);
    mix(key.depth);
    mix(key.view_dimension);
    mix(key.guest_format);
    mix(key.guest_dimension);
    mix(key.guest_pitch);
    for (uint32_t dword : key.fetch_dwords) {
      mix(dword);
    }
    return static_cast<size_t>(hash);
  }
};

struct PlumeTextureUploadDecodeCacheEntry {
  std::vector<uint8_t> bytes;
  uint64_t upload_hash = 0;
  uint32_t row_width = 0;
  uint32_t subresources = 0;
  uint64_t subresource_bytes = 0;
  uint64_t upload_size = 0;
  bool decoded = false;
  bool has_source = false;
};

struct PlumeResourceManagerState {
  std::mutex mutex;
#if REFII_RENDERER_HAS_PLUME
  std::unordered_map<uint32_t, PlumeTrackedResource> resources;
  std::unordered_map<uint32_t, PlumeTrackedResource> decoded_color_targets;
  std::unordered_map<uint32_t, PlumeTrackedResource> decoded_depth_targets;
  std::unordered_map<FableSurfaceTargetKey, uint32_t, FableSurfaceTargetKeyHash>
      decoded_color_target_keys;
  std::unordered_map<FableSurfaceTargetKey, uint32_t, FableSurfaceTargetKeyHash>
      decoded_depth_target_keys;
  std::deque<PlumeRetiredResource> retired_resources;
  uint64_t resource_retire_epoch = 0;
  uint64_t active_lookup_resource_serial = 0;
  std::unique_ptr<plume::RenderPipelineLayout> pipeline_layout;
  std::unordered_map<std::string, std::unique_ptr<plume::RenderPipeline>>
      pipelines;
  std::unordered_map<std::string, std::unique_ptr<plume::RenderFramebuffer>>
      framebuffers;
  std::deque<PlumeRetiredFramebuffer> retired_framebuffers;
  std::unordered_map<std::string, PlumeDepthSampleCopy> depth_sample_copies;
  std::unordered_map<std::string, PlumeColorSampleCopy> color_sample_copies;
  std::unordered_map<std::string, PlumeColorAttachmentPadding>
      color_attachment_padding;
  std::unordered_map<uint32_t, std::unique_ptr<plume::RenderSampler>>
      fable_samplers;
  std::unique_ptr<plume::RenderDescriptorSet> texture_descriptor_sets[3];
  std::unique_ptr<plume::RenderDescriptorSet> sampler_descriptor_set;
  std::array<std::array<PlumeCachedTextureDescriptor, kFableDescriptorCount>,
             3>
      cached_texture_descriptors = {};
  std::array<std::unordered_map<PlumeTextureDescriptorKey, uint32_t,
                                PlumeTextureDescriptorKeyHash>,
             3>
      texture_descriptor_lookup;
  std::array<uint32_t, 3> next_texture_descriptor_indices = {1u, 1u, 1u};
  uint64_t texture_descriptor_epoch = 0;
  std::array<plume::RenderSampler*, kFableSamplerDescriptorCount>
      cached_sampler_descriptors = {};
  std::unordered_map<plume::RenderSampler*, uint32_t>
      sampler_descriptor_lookup;
  uint32_t next_sampler_descriptor_index = 1u;
  std::unordered_map<FableResourceLookupKey, const PlumeTrackedResource*,
                     FableResourceLookupKeyHash>
      resource_lookup_cache;
  std::unordered_map<FableTextureFetchKey, const PlumeTrackedResource*,
                     FableTextureFetchKeyHash>
      texture_fetch_lookup_cache;
  std::unordered_map<FableTextureFetchLookupKey, const PlumeTrackedResource*,
                     FableTextureFetchLookupKeyHash>
      texture_fetch_serial_lookup_cache;
  std::unordered_set<FableTextureFetchKey, FableTextureFetchKeyHash>
      texture_fetch_frame_misses;
  std::unordered_map<FableTextureFetchKey, const PlumeTrackedResource*,
                     FableTextureFetchKeyHash>
      texture_fetch_exact_index;
  std::unordered_map<FableTextureShapeKey, const PlumeTrackedResource*,
                     FableTextureShapeKeyHash>
      texture_fetch_shape_index;
  std::unordered_map<FableTextureShapeKey, uint32_t, FableTextureShapeKeyHash>
      texture_alias_owner_by_shape;
  std::unordered_map<uint32_t, const PlumeTrackedResource*> texture_base_index;
  RendererTrackedResourceVersion texture_lookup_index_version;
  bool texture_lookup_index_valid = false;
  std::unordered_map<uint32_t, FableRenderTargetResolveAlias>
      render_target_resolve_aliases;
  std::array<RendererSurfaceCopyAliasSnapshot, kRendererSurfaceCopyAliasSlots>
      surface_copy_aliases = {};
  uint32_t surface_copy_alias_count = 0;
  uint64_t surface_copy_alias_snapshot_serial = 0;
  std::unordered_map<uint32_t, uint32_t>
      surface_copy_alias_latest_index_by_destination;
  std::unordered_map<uint32_t, FableSuppressedSurfaceCopyAlias>
      suppressed_surface_copy_aliases;
  std::unordered_map<uint32_t, FableSuppressedTextureCopyAlias>
      suppressed_texture_copy_aliases;
  std::unordered_map<const plume::RenderTexture*, const PlumeTrackedResource*>
      texture_pointer_lookup_cache;
  std::unordered_map<FableTextureAliasPairKey, bool,
                     FableTextureAliasPairKeyHash>
      texture_alias_lookup_cache;
  std::unordered_map<FableLiveRenderTargetAliasKey,
                     const PlumeTrackedResource*,
                     FableLiveRenderTargetAliasKeyHash>
      live_render_target_alias_cache;
  std::unordered_map<FableSampledObjectLookupKey, const PlumeTrackedResource*,
                     FableSampledObjectLookupKeyHash>
      sampled_object_lookup_cache;
  std::unordered_map<FableSampledTextureLookupKey, const PlumeTrackedResource*,
                     FableSampledTextureLookupKeyHash>
      sampled_texture_lookup_cache;
  std::unordered_map<FableDrawTextureDescriptorKey,
                     FableDrawTextureDescriptorCacheEntry,
                     FableDrawTextureDescriptorKeyHash>
      draw_texture_descriptor_cache;
  std::unordered_map<FableDrawTextureBindingKey,
                     FableDrawTextureBindingCacheEntry,
                     FableDrawTextureBindingKeyHash>
      draw_texture_binding_cache;
  std::unordered_map<uint64_t, FableSurfaceAliasCopyCacheEntry>
      surface_alias_copy_cache;
  std::unique_ptr<plume::RenderTexture> null_textures[3];
  std::unique_ptr<plume::RenderTextureView> null_texture_views[3];
  std::unique_ptr<plume::RenderSampler> null_sampler;
  std::unique_ptr<plume::RenderBuffer> zero_constants;
  std::unique_ptr<plume::RenderBuffer> default_vertex_input_buffer;
  std::unique_ptr<plume::RenderBuffer> implicit_rectangle_vertex_buffer;
  std::vector<PlumeCachedIndexBuffer> quad_list_index_buffers;
  std::unique_ptr<plume::RenderBuffer> null_texture_uploads[3];
  std::unique_ptr<plume::RenderBuffer> frame_constant_slab;
  uint8_t* frame_constant_slab_mapped = nullptr;
  uint64_t frame_constant_slab_size = 0;
  uint64_t frame_constant_slab_offset = 0;
  std::array<std::vector<PlumeFrameTextureUploadSlab>, 3>
      frame_texture_upload_slabs;
  std::array<uint32_t, 3> frame_texture_upload_slab_indices = {};
  std::array<std::unordered_map<PlumeTextureUploadDecodeCacheKey,
                                PlumeTextureUploadDecodeCacheEntry,
                                PlumeTextureUploadDecodeCacheKeyHash>,
             3>
      frame_texture_upload_decode_caches;
  uint32_t frame_texture_upload_slab_index = 0;
  std::unique_ptr<plume::RenderBuffer> frame_vertex_slab;
  uint8_t* frame_vertex_slab_mapped = nullptr;
  uint64_t frame_vertex_slab_size = 0;
  uint64_t frame_vertex_slab_offset = 0;
  std::unordered_map<std::string, plume::RenderBuffer*> frame_vertex_buffer_cache;
  std::unordered_map<std::string, plume::RenderBuffer*> frame_index_buffer_cache;
  std::unordered_map<std::string, PlumeCachedInputLayout>
      frame_input_layout_cache;
  std::unordered_set<uint32_t> logged_unmatched_render_targets;
  std::unordered_set<uint32_t> logged_rejected_decoded_color_targets;
  std::unordered_set<uint32_t> logged_rejected_decoded_depth_targets;
  std::unordered_set<uint32_t> logged_present_copy_failures;
  std::unordered_set<uint32_t> logged_failed_vertex_declarations;
  std::unordered_set<uint64_t> logged_rectangle_shader_pairs;
  std::unordered_set<uint64_t> logged_triangle_shader_pairs;
  std::unordered_set<uint64_t> logged_texture_slot_shader_pairs;
  std::unordered_set<uint64_t> logged_private_rectangle_chain_keys;
  std::unordered_set<uint64_t> logged_rejected_resolve_alias_keys;
  std::unordered_set<uint64_t> logged_present_scaler_keys;
  plume::RenderShaderFormat present_blit_shader_format =
      plume::RenderShaderFormat::UNKNOWN;
  std::unique_ptr<plume::RenderPipelineLayout> present_blit_pipeline_layout;
  std::unique_ptr<plume::RenderPipeline> present_blit_pipeline;
  std::vector<std::unique_ptr<plume::RenderDescriptorSet>>
      present_blit_texture_sets;
  uint32_t present_blit_texture_set_index = 0;
  std::array<const plume::RenderTexture*, kPresentBlitDescriptorSetRingSize>
      present_blit_texture_set_textures = {};
  std::array<const plume::RenderTextureView*, kPresentBlitDescriptorSetRingSize>
      present_blit_texture_set_views = {};
  std::array<plume::RenderTextureLayout, kPresentBlitDescriptorSetRingSize>
      present_blit_texture_set_layouts = {};
  std::unique_ptr<plume::RenderDescriptorSet> present_blit_sampler_set;
  std::unique_ptr<plume::RenderSampler> present_blit_sampler;
  std::unordered_map<const plume::RenderTexture*, PlumePresentOutputCacheEntry>
      present_output_cache;
  plume::RenderShaderFormat rectangle_list_shader_format =
      plume::RenderShaderFormat::UNKNOWN;
  std::unique_ptr<plume::RenderShader> rectangle_list_geometry_shader;
  uint32_t logged_video_ycrcb_draws = 0;
  uint32_t logged_fable_video_texture_upload_samples = 0;
  uint32_t logged_fable_video_plane_upload_samples = 0;
  uint32_t logged_fable_pix_external_plane_bindings = 0;
  uint32_t logged_fable_surface_alias_plane_bindings = 0;
#endif
  RendererTrackedResourceVersion last_resource_sync_version;
  bool resource_sync_version_valid = false;
  FablePresentModel fable_present_model;
  uint32_t preferred_present_render_target = 0;
  uint32_t preferred_present_width = 0;
  uint32_t preferred_present_height = 0;
  uint32_t scene_present_render_target = 0;
  uint64_t scene_present_resource_serial = 0;
  uint32_t scene_present_width = 0;
  uint32_t scene_present_height = 0;
  uint32_t last_visible_present_render_target = 0;
  uint64_t last_visible_present_resource_serial = 0;
  uint32_t last_visible_present_width = 0;
  uint32_t last_visible_present_height = 0;
  uint32_t tracked_texture_uploads_this_frame = 0;
  uint32_t decoded_tracked_texture_uploads_this_frame = 0;
  uint32_t raw_tracked_texture_uploads_this_frame = 0;
  uint32_t placeholder_tracked_texture_uploads_this_frame = 0;
  uint32_t created_tracked_texture_uploads_this_frame = 0;
  uint32_t refreshed_tracked_texture_uploads_this_frame = 0;
  uint32_t display_tracked_texture_uploads_this_frame = 0;
  uint32_t first_uploaded_texture = 0;
  uint32_t first_uploaded_texture_base = 0;
  uint64_t first_uploaded_texture_generation = 0;
  uint64_t first_uploaded_texture_hash = 0;
  uint32_t pending_tracked_texture_uploads = 0;
  uint32_t resolve_texture_alias_hits_this_frame = 0;
  uint64_t tracked_texture_upload_bytes_this_frame = 0;
  uint64_t tracked_texture_uploads_total = 0;
  uint64_t tracked_texture_upload_bytes_total = 0;
  uint32_t resource_sync_full_frames = 0;
  uint32_t resource_sync_partial_frames = 0;
  uint32_t resource_sync_snapshots = 0;
  uint32_t resource_sync_referenced = 0;
  uint32_t resource_sync_live = 0;
  uint32_t resource_sync_created = 0;
  uint32_t resource_sync_reused = 0;
  uint32_t resource_sync_released = 0;
  uint32_t resource_sync_image_snapshots = 0;
};


extern PlumeResourceManagerState g_plume_resources;

#if REFII_RENDERER_HAS_PLUME
void InvalidateFableDescriptorCacheLocked();
void BeginFableFrameLookupCacheLocked();
void ClearFableFrameLookupCacheLocked();
void ClearFableTextureLookupIndexLocked();
void TrimRetiredFableFramebuffersLocked();
void RetireFableFramebuffersLocked();
void TrimRetiredPlumeResourcesLocked();
void RetirePlumeResourceLocked(PlumeTrackedResource&& resource,
                               uint64_t valid_until_serial = 0);

bool IsFableResolveCopyDraw(const RendererDrawEventSnapshot& draw_event);
uint32_t NormalizeFableResolveDestinationBase(uint32_t destination_base);
uint32_t NormalizeFableGpuBase(uint32_t base);
uint32_t DecodeFableGpuBase(uint32_t base);
std::array<uint32_t, 4> FableDisplayBaseCandidates(uint32_t base);
bool FableDisplayBasesMatch(uint32_t lhs, uint32_t rhs);
const FableRenderTargetResolveAlias* FindFableResolveAliasLocked(
    uint32_t destination_base);
bool IsLikelyFableObjectAddress(uint32_t guest_address);
bool FableWordsLookLikeInlinePathString(const std::vector<uint32_t>& words);
void AppendFableHandlePointerCandidates(const std::vector<uint32_t>& words,
                                        std::vector<uint32_t>& candidates);
bool IsPlausibleFableSurfaceShape(const FableSurfaceShape& shape);
bool IsPlausibleFableColorRenderTargetExtent(uint32_t width, uint32_t height);
bool IsPlausibleDecodedFableColorRenderTargetShape(
    const FableSurfaceShape& shape);
bool IsPlausibleDecodedFableDepthTargetShape(const FableSurfaceShape& shape);
bool DecodeFableSurfaceExtentFromPacked(uint32_t packed_extent,
                                        uint32_t packed_dimension,
                                        uint32_t& width, uint32_t& height,
                                        uint32_t* depth = nullptr);
bool DecodeFableSurfaceExtentFromWords(const std::vector<uint32_t>& words,
                                       uint32_t& width, uint32_t& height);
plume::RenderFormat ConvertFableD3DDepthFormat(uint32_t d3d_format);
plume::RenderFormat DecodeFableSurfaceRenderFormatFromWords(
    const std::vector<uint32_t>& words, RendererTrackedResourceKind kind);
bool DecodeFableSurfaceShapeFromWords(const std::vector<uint32_t>& words,
                                      RendererTrackedResourceKind kind,
                                      FableSurfaceShape& shape);

void CacheTextureLookupForResourceLocked(const PlumeTrackedResource& resource);
bool SameTrackedTextureShape(const PlumeTrackedResource& lhs,
                             const PlumeTrackedResource& rhs);
void EnsureFableTextureLookupIndexLocked();
bool SetFableTextureDescriptorAtIndexLocked(
    uint32_t set_index, uint32_t descriptor_index,
    plume::RenderTexture* texture, plume::RenderTextureLayout layout,
    plume::RenderTextureView* view);
uint32_t FindOrSetFableTextureDescriptorLocked(
    uint32_t set_index, plume::RenderTexture* texture,
    plume::RenderTextureLayout layout, plume::RenderTextureView* view);
bool SetFableSamplerDescriptorAtIndexLocked(uint32_t descriptor_index,
                                            plume::RenderSampler* sampler);
uint32_t FindOrSetFableSamplerDescriptorLocked(plume::RenderSampler* sampler);

const char* TrackedResourceKindName(RendererTrackedResourceKind kind);
bool IsBufferResource(RendererTrackedResourceKind kind);
bool IsShaderResource(RendererTrackedResourceKind kind);
bool IsDeclarationResource(RendererTrackedResourceKind kind);
bool IsTextureResource(RendererTrackedResourceKind kind);
bool IsSurfaceResource(RendererTrackedResourceKind kind);
bool IsDepthSurfaceResource(RendererTrackedResourceKind kind);
bool IsImageResource(RendererTrackedResourceKind kind);

inline void RefreshPlumeGuestImageObjectsFromTrackedFields(
    PlumeTrackedResource& tracked) {
  const uint32_t image_byte_size =
      tracked.guest_texture.byte_size != 0 ? tracked.guest_texture.byte_size
                                           : tracked.size;
  auto texture = std::move(tracked.guest_texture.texture);
  auto view = std::move(tracked.guest_texture.view);
  const uint32_t view_mapping_key = tracked.guest_texture.view_mapping_key;
  auto mapped_views = std::move(tracked.guest_texture.mapped_views);
  plume::RenderBuffer* upload_buffer = tracked.guest_texture.upload_buffer;
  const uint64_t upload_offset = tracked.guest_texture.upload_offset;
  tracked.guest_texture = {
      .object_address = tracked.guest_address,
      .parent_address = tracked.parent_address,
      .base_address = tracked.texture_guest_base,
      .mip_address = tracked.texture_guest_mips,
      .byte_size = image_byte_size,
      .width = tracked.texture_width,
      .height = tracked.texture_height,
      .depth = tracked.texture_depth,
      .mip_levels = tracked.texture_mip_levels,
      .guest_pitch = tracked.texture_guest_pitch,
      .guest_format = tracked.texture_guest_format,
      .guest_dimension = tracked.texture_guest_dimension,
      .format = tracked.texture_format,
      .fetch_dwords = tracked.texture_fetch_dwords,
      .upload_generation = tracked.upload_generation,
      .upload_hash = tracked.texture_upload_hash,
      .create_serial = tracked.serial,
      .content_serial = std::max(tracked.content_serial, tracked.serial),
      .tiled = tracked.texture_guest_tiled,
      .upload_decoded = tracked.texture_upload_decoded,
      .upload_pending = tracked.texture_upload_pending,
      .plume_owned = tracked.plume_owned,
  };
  tracked.guest_texture.texture = std::move(texture);
  tracked.guest_texture.view = std::move(view);
  tracked.guest_texture.view_mapping_key = view_mapping_key;
  tracked.guest_texture.mapped_views = std::move(mapped_views);
  tracked.guest_texture.upload_buffer =
      tracked.texture_upload_buffer ? tracked.texture_upload_buffer
                                    : upload_buffer;
  tracked.guest_texture.upload_offset =
      tracked.texture_upload_buffer ? tracked.texture_upload_offset
                                    : upload_offset;
  RefreshPlumeGuestResourceHandlesFromTrackedFields(tracked);
  if (IsSurfaceResource(tracked.kind)) {
    auto attachment_view = std::move(tracked.guest_surface.attachment_view);
    tracked.guest_surface = {
        .object_address = tracked.guest_address,
        .parent_address = tracked.parent_address,
        .width = tracked.texture_width,
        .height = tracked.texture_height,
        .depth = tracked.texture_depth,
        .pitch_msaa = tracked.stride_or_format,
        .byte_size = image_byte_size,
        .format = tracked.texture_format,
        .create_serial = tracked.serial,
        .content_serial = std::max(tracked.content_serial, tracked.serial),
        .color_target = tracked.texture_render_target,
        .depth_target = tracked.texture_depth_target,
        .plume_owned = tracked.plume_owned,
    };
    tracked.guest_surface.attachment_view = std::move(attachment_view);
    RefreshPlumeGuestResourceHandlesFromTrackedFields(tracked);
  }
}

plume::RenderFormat ConvertXenosColorRenderTargetFormat(
    rex::graphics::xenos::ColorRenderTargetFormat format);
bool IsUsablePlumeTextureFormat(plume::RenderFormat format);
bool IsUsablePlumeColorTargetFormat(plume::RenderFormat format);
bool IsBlockCompressedFormat(plume::RenderFormat format);
uint32_t TextureBlockWidth(plume::RenderFormat format);
uint32_t TextureBlockHeight(plume::RenderFormat format);
uint32_t TextureRowCount(plume::RenderFormat format, uint32_t height);
uint32_t TextureTightRowBytes(plume::RenderFormat format, uint32_t width);
uint32_t TextureRowWidthForPitch(plume::RenderFormat format,
                                 uint32_t row_pitch_bytes);
uint8_t ExpandUnormBits(uint32_t value, uint32_t bits);
bool DecodePacked16XenosTexel(rex::graphics::xenos::TextureFormat format,
                              uint16_t texel, uint8_t* rgba);
bool IsPacked16XenosTextureFormat(
    rex::graphics::xenos::TextureFormat format);
plume::RenderTextureViewDimension ViewDimensionForTextureDimension(
    plume::RenderTextureDimension dimension, bool cube);
plume::RenderTextureViewDesc TextureViewDescForDimension(
    plume::RenderTextureViewDimension dimension, plume::RenderFormat format);
plume::RenderComponentMapping XenosTextureComponentMapping(
    uint32_t guest_format, uint32_t fetch_swizzle);
plume::RenderComponentMapping IdentityTextureComponentMapping();
bool FableSingleSourceVideoUsesDecodedChannels(uint32_t guest_format);
bool IsLikelyFableVideoNeutralChromaTexture(
    const RendererTrackedResourceSnapshot& snapshot,
    const PlumeTrackedResource& tracked);
uint32_t PackTextureComponentMapping(
    const plume::RenderComponentMapping& mapping);
uint64_t AlignUp(uint64_t value, uint64_t alignment);
bool IsPowerOfTwo(uint32_t value);
inline bool HasTrackedFetchDwords(const std::array<uint32_t, 6>& dwords) {
  for (uint32_t dword : dwords) {
    if (dword != 0) {
      return true;
    }
  }
  return false;
}

inline std::array<uint32_t, 6> PreferredTextureFetchDwords(
    const RendererTrackedTextureSnapshot& texture) {
  return HasTrackedFetchDwords(texture.normalized_fetch_dwords)
             ? texture.normalized_fetch_dwords
             : texture.raw_fetch_dwords;
}

void UpdateTrackedUploadDebugSample(PlumeTrackedResource& tracked,
                                    const uint8_t* data, size_t size);
void UploadTrackedBuffer(PlumeTrackedResource& tracked,
                         const RendererTrackedResourceSnapshot& snapshot);
bool DecodeGuestTextureBaseLevel(const RendererTrackedResourceSnapshot& snapshot,
                                 const PlumeTrackedResource& tracked,
                                 std::vector<uint8_t>& decoded_bytes);
bool PrepareTextureUploadBytes(const RendererTrackedResourceSnapshot& snapshot,
                               PlumeTrackedResource& tracked,
                               std::vector<uint8_t>& upload_bytes);
bool RefreshTrackedTextureUpload(plume::RenderDevice* device,
                                 const RendererTrackedResourceSnapshot& snapshot,
                                 PlumeTrackedResource& tracked);
bool EnsureFrameTextureUploadSlabs(plume::RenderDevice* device);
plume::RenderFormat DecodeFableDepthSurfaceFormat(
    const RendererTrackedSurfaceSnapshot& surface,
    uint32_t surface_d3d_format);
void DecodeFableTextureMetadata(const RendererTrackedResourceSnapshot& snapshot,
                                PlumeTrackedResource& tracked);
std::unique_ptr<plume::RenderTexture> CreateTextureForTrackedResource(
    plume::RenderDevice* device, const RendererTrackedResourceSnapshot& snapshot,
    PlumeTrackedResource& tracked);
void NameTrackedTexture(plume::RenderTexture* texture,
                        const RendererTrackedResourceSnapshot& snapshot,
                        const PlumeTrackedResource& tracked);
uint32_t ClampGuestTextureExtent(uint32_t extent);
plume::RenderFormat ConvertXenosTextureFormat(
    rex::graphics::xenos::TextureFormat format);
uint32_t FableFetchTextureWidth(
    const rex::graphics::xenos::xe_gpu_texture_fetch_t& fetch);
uint32_t FableFetchTextureHeight(
    const rex::graphics::xenos::xe_gpu_texture_fetch_t& fetch);
bool FableResolveAliasMatchesFetch(
    const PlumeTrackedResource& resource,
    const rex::graphics::xenos::xe_gpu_texture_fetch_t& fetch);
#endif

}  // namespace refii::renderer
