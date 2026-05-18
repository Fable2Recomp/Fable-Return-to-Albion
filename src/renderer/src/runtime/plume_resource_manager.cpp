#include "runtime/plume_resource_manager.h"

#include "runtime/hook_observer.h"
#include "runtime/cvars.h"
#include "runtime/shader_library.h"

#include <algorithm>
#include <array>
#include <cctype>
#include <cmath>
#include <cstddef>
#include <cstdio>
#include <cstdint>
#include <cstring>
#include <deque>
#include <filesystem>
#include <fstream>
#include <limits>
#include <memory>
#include <mutex>
#include <string>
#include <string_view>
#include <unordered_map>
#include <unordered_set>
#include <vector>

#include <rex/logging.h>
#include <rex/math.h>
#include <rex/graphics/pipeline/texture/conversion.h>
#include <rex/graphics/pipeline/texture/info.h>
#include <rex/graphics/pipeline/texture/util.h>
#include <rex/graphics/xenos.h>
#include <tracy/Tracy.hpp>

#if REFII_RENDERER_HAS_PLUME
#include <plume_render_interface.h>
#if defined(_WIN64)
#include "plume_present_blit_ps.hlsl.dxil.h"
#include "plume_present_blit_vs.hlsl.dxil.h"
#include "plume_private_texture_copy_ps.hlsl.dxil.h"
#include "plume_rectangle_list_gs.hlsl.dxil.h"
#endif
#include "plume_present_blit_ps.hlsl.spirv.h"
#include "plume_present_blit_vs.hlsl.spirv.h"
#include "plume_private_texture_copy_ps.hlsl.spirv.h"
#include "plume_rectangle_list_gs.hlsl.spirv.h"
#endif

namespace refii::renderer {
namespace {

#if REFII_RENDERER_HAS_PLUME
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
  std::unique_ptr<plume::RenderBuffer> buffer;
  std::unique_ptr<plume::RenderShader> shader;
  std::unique_ptr<plume::RenderTexture> texture;
  std::unique_ptr<plume::RenderTextureView> texture_view;
  mutable std::unordered_map<uint32_t, std::unique_ptr<plume::RenderTextureView>>
      texture_mapped_views;
  std::unique_ptr<plume::RenderBuffer> texture_upload;
  std::vector<plume::RenderInputSlot> input_slots;
  std::vector<plume::RenderInputElement> input_elements;
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
  bool texture_guest_tiled = false;
  bool texture_render_target = false;
  bool texture_depth_target = false;
  bool texture_upload_has_source = false;
  bool texture_upload_decoded = false;
  bool texture_upload_pending = false;
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
  uint32_t source_address = 0;
  uint32_t source_slot = 0;
  uint32_t destination_pitch = 0;
  uint32_t destination_height = 0;
  uint32_t destination_info = 0;
  uint32_t copy_control = 0;
  uint64_t serial = 0;
  bool depth = false;
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

constexpr size_t kMaxPlumeScratchResources = 512;
constexpr uint64_t kRetiredPlumeResourceEpochs = 16;
constexpr uint64_t kRetiredPlumeBufferResourceEpochs = 256;
constexpr size_t kMaxRetiredPlumeResources = 4096;
constexpr uint32_t kMaxFableTextureSlots = 32;
constexpr uint32_t kMaxFableRenderTargets = 4;
constexpr uint32_t kFableDescriptorCount = 256;
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
  std::array<plume::RenderFormat, kMaxFableRenderTargets> color_formats = {};
  std::array<plume::RenderTexture*, kMaxFableRenderTargets> color_textures = {};
  std::array<plume::RenderTextureView*, kMaxFableRenderTargets> color_views = {};
  uint32_t color_width = 0;
  uint32_t color_height = 0;
  uint32_t depth_width = 0;
  uint32_t depth_height = 0;
  plume::RenderFormat color_format = plume::RenderFormat::B8G8R8A8_UNORM;
  plume::RenderFormat depth_format = plume::RenderFormat::UNKNOWN;
  plume::RenderTexture* color_texture = nullptr;
  plume::RenderTextureView* color_view = nullptr;
  plume::RenderTexture* depth_texture = nullptr;
  plume::RenderTextureView* depth_view = nullptr;
  bool uses_output = true;
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

struct FablePrivateTextureRectangleReplayVertex {
  float position[4] = {};
  float texcoord0[4] = {};
  float texcoord1[4] = {};
};

struct PlumeCachedIndexBuffer {
  uint32_t index_count = 0;
  bool strip_0132_order = false;
  std::unique_ptr<plume::RenderBuffer> buffer;
};

struct PlumeCachedTextureDescriptor {
  plume::RenderTexture* texture = nullptr;
  plume::RenderTextureView* view = nullptr;
  plume::RenderTextureLayout layout = plume::RenderTextureLayout::UNKNOWN;
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
#endif

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
  std::unordered_map<std::string, PlumeDepthSampleCopy> fallback_depth_targets;
  std::unordered_map<std::string, PlumeColorSampleCopy> color_sample_copies;
  std::unordered_map<std::string, PlumeColorAttachmentPadding>
      color_attachment_padding;
  std::unordered_map<uint32_t, std::unique_ptr<plume::RenderSampler>>
      fable_samplers;
  std::unique_ptr<plume::RenderDescriptorSet> texture_descriptor_sets[3];
  std::unique_ptr<plume::RenderDescriptorSet> sampler_descriptor_set;
  std::array<std::array<PlumeCachedTextureDescriptor, kMaxFableTextureSlots + 1>,
             3>
      cached_texture_descriptors = {};
  std::array<plume::RenderSampler*, kMaxFableTextureSlots + 1>
      cached_sampler_descriptors = {};
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
  RendererTrackedResourceVersion texture_lookup_index_version;
  bool texture_lookup_index_valid = false;
  std::unordered_map<uint32_t, FableRenderTargetResolveAlias>
      render_target_resolve_aliases;
  std::unordered_map<const plume::RenderTexture*, const PlumeTrackedResource*>
      texture_pointer_lookup_cache;
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
  std::vector<std::unique_ptr<plume::RenderBuffer>> frame_constant_buffers;
  std::unordered_set<uint32_t> logged_unmatched_render_targets;
  std::unordered_set<uint32_t> logged_rejected_decoded_color_targets;
  std::unordered_set<uint32_t> logged_rejected_decoded_depth_targets;
  std::unordered_set<uint32_t> logged_present_copy_failures;
  std::unordered_set<uint32_t> logged_failed_vertex_declarations;
  std::unordered_set<uint32_t> logged_vulkan_unsafe_translated_shaders;
  std::unordered_set<uint64_t> logged_rectangle_shader_pairs;
  std::unordered_set<uint64_t> logged_triangle_shader_pairs;
  std::unordered_set<uint64_t> logged_texture_slot_shader_pairs;
  plume::RenderShaderFormat present_blit_shader_format =
      plume::RenderShaderFormat::UNKNOWN;
  std::unique_ptr<plume::RenderPipelineLayout> present_blit_pipeline_layout;
  std::unique_ptr<plume::RenderPipeline> present_blit_pipeline;
  std::vector<std::unique_ptr<plume::RenderDescriptorSet>>
      present_blit_texture_sets;
  uint32_t present_blit_texture_set_index = 0;
  std::unique_ptr<plume::RenderDescriptorSet> present_blit_sampler_set;
  std::unique_ptr<plume::RenderSampler> present_blit_sampler;
  plume::RenderShaderFormat rectangle_list_shader_format =
      plume::RenderShaderFormat::UNKNOWN;
  std::unique_ptr<plume::RenderShader> rectangle_list_geometry_shader;
  plume::RenderShaderFormat private_texture_copy_shader_format =
      plume::RenderShaderFormat::UNKNOWN;
  std::unique_ptr<plume::RenderShader> private_texture_copy_pixel_shader;
  bool logged_present_blit_ready = false;
  bool logged_rectangle_list_geometry_shader_ready = false;
  bool logged_private_texture_copy_pixel_shader_ready = false;
  bool logged_quad_list_sequential_order = false;
  bool logged_quad_list_strip_order = false;
  bool logged_implicit_rectangle_vertices = false;
  bool logged_resolve_alias = false;
  uint32_t logged_quad_list_draws = 0;
  uint32_t logged_rectangle_list_draws = 0;
  uint32_t logged_triangle_list_draws = 0;
  uint32_t logged_invalid_indexed_draws = 0;
  uint32_t logged_video_ycrcb_draws = 0;
  uint32_t logged_title_shader_draws = 0;
  uint32_t logged_missing_vertex_buffer_draws = 0;
  uint32_t logged_fable_video_texture_upload_samples = 0;
  uint32_t logged_fable_pix_external_plane_bindings = 0;
#endif
  PlumeDrawReplayStats last_draw_replay_stats;
  RendererTrackedResourceVersion last_resource_sync_version;
  bool resource_sync_version_valid = false;
  bool logged_waiting_for_buffers = false;
  bool logged_waiting_for_declarations = false;
  bool logged_first_recorded_draw_bridge = false;
  bool logged_pipeline_layout_ready = false;
  bool logged_live_constants = false;
  bool logged_draw_constant_nonzero = false;
  bool fallback_textures_upload_pending = false;
  bool logged_fable_shared_constants = false;
  bool logged_vulkan_depth_sample_source = false;
  bool logged_vulkan_depth_sample_copy = false;
  bool logged_vulkan_fallback_depth_target = false;
  bool logged_vulkan_depth_export_unavailable = false;
  bool logged_vulkan_depth_export_skip = false;
  bool logged_vulkan_depth_export_details = false;
  bool logged_vulkan_target_feedback_skip = false;
  bool logged_vulkan_unsafe_translated_shader_skip = false;
  bool logged_xenos_raster_skip = false;
  bool logged_fallback_texture_upload = false;
  bool logged_tracked_texture_upload = false;
  uint32_t logged_fable_clears = 0;
  uint32_t logged_present_target_summaries = 0;
  uint32_t logged_present_copy_summaries = 0;
  uint32_t logged_present_target = 0;
  uint32_t logged_present_target_width = 0;
  uint32_t logged_present_target_height = 0;
  uint32_t logged_present_copy_target = 0;
  uint64_t logged_present_copy_serial = 0;
  uint32_t preferred_present_render_target = 0;
  uint32_t preferred_present_width = 0;
  uint32_t preferred_present_height = 0;
  uint32_t tracked_texture_uploads_this_frame = 0;
  uint32_t decoded_tracked_texture_uploads_this_frame = 0;
  uint32_t raw_tracked_texture_uploads_this_frame = 0;
  uint32_t placeholder_tracked_texture_uploads_this_frame = 0;
  uint32_t pending_tracked_texture_uploads = 0;
  uint32_t resolve_texture_alias_hits_this_frame = 0;
  uint64_t tracked_texture_uploads_total = 0;
};

PlumeResourceManagerState g_plume_resources;

#if REFII_RENDERER_HAS_PLUME
bool IsBufferResource(RendererTrackedResourceKind kind);
bool IsImageResource(RendererTrackedResourceKind kind);
bool SameTrackedResourceVersion(const RendererTrackedResourceVersion& lhs,
                                const RendererTrackedResourceVersion& rhs);
plume::RenderFormat ConvertXenosTextureFormat(
    rex::graphics::xenos::TextureFormat format);

void InvalidateFableDescriptorCacheLocked() {
  for (auto& descriptor_set_cache :
       g_plume_resources.cached_texture_descriptors) {
    for (PlumeCachedTextureDescriptor& descriptor : descriptor_set_cache) {
      descriptor = {};
    }
  }
  g_plume_resources.cached_sampler_descriptors.fill(nullptr);
}

void ClearFableFrameLookupCacheLocked() {
  g_plume_resources.resource_lookup_cache.clear();
  g_plume_resources.texture_fetch_lookup_cache.clear();
  g_plume_resources.texture_fetch_serial_lookup_cache.clear();
  g_plume_resources.texture_fetch_frame_misses.clear();
  g_plume_resources.render_target_resolve_aliases.clear();
  g_plume_resources.texture_pointer_lookup_cache.clear();
}

void ClearFableTextureLookupIndexLocked() {
  g_plume_resources.texture_fetch_exact_index.clear();
  g_plume_resources.texture_fetch_shape_index.clear();
  g_plume_resources.texture_lookup_index_version = {};
  g_plume_resources.texture_lookup_index_valid = false;
}

bool IsFableResolveCopyDraw(const RendererDrawEventSnapshot& draw_event) {
  constexpr uint32_t kXenosEdramModeCopy = 6u;
  return draw_event.pm4_registers_valid &&
         (draw_event.rb_modecontrol & 0x7u) == kXenosEdramModeCopy;
}

uint32_t NormalizeFableResolveDestinationBase(uint32_t destination_base) {
  return destination_base & ~0xFFFu;
}

uint32_t NormalizeFableGpuBase(uint32_t base) {
  if (base >= 0x41000000u && base < 0x50000000u) {
    return base - 0x41000000u;
  }
  if (base >= 0x40000000u && base < 0x41000000u) {
    return base - 0x40000000u;
  }
  return base;
}

uint32_t DecodeFableGpuBase(uint32_t base) {
  return (((base >> 20u) + 512u) & 0x1000u) + (base & 0x1FFFFFFFu);
}

void AddFableDisplayBaseCandidate(std::array<uint32_t, 4>& candidates,
                                  uint32_t candidate) {
  if (candidate == 0) {
    return;
  }
  for (uint32_t existing : candidates) {
    if (existing == candidate) {
      return;
    }
  }
  for (uint32_t& slot : candidates) {
    if (slot == 0) {
      slot = candidate;
      return;
    }
  }
}

std::array<uint32_t, 4> FableDisplayBaseCandidates(uint32_t base) {
  std::array<uint32_t, 4> candidates = {};
  const uint32_t normalized = NormalizeFableGpuBase(base);
  const uint32_t decoded = DecodeFableGpuBase(normalized);
  AddFableDisplayBaseCandidate(candidates, normalized);
  AddFableDisplayBaseCandidate(candidates, decoded);
  AddFableDisplayBaseCandidate(candidates,
                               NormalizeFableResolveDestinationBase(normalized));
  AddFableDisplayBaseCandidate(candidates,
                               NormalizeFableResolveDestinationBase(decoded));
  return candidates;
}

bool FableDisplayBasesMatch(uint32_t lhs, uint32_t rhs) {
  if (lhs == 0 || rhs == 0) {
    return false;
  }
  const std::array<uint32_t, 4> lhs_candidates =
      FableDisplayBaseCandidates(lhs);
  const std::array<uint32_t, 4> rhs_candidates =
      FableDisplayBaseCandidates(rhs);
  for (uint32_t lhs_candidate : lhs_candidates) {
    if (lhs_candidate == 0) {
      continue;
    }
    for (uint32_t rhs_candidate : rhs_candidates) {
      if (rhs_candidate != 0 && lhs_candidate == rhs_candidate) {
        return true;
      }
    }
  }
  return false;
}

const FableRenderTargetResolveAlias* FindFableResolveAliasLocked(
    uint32_t destination_base) {
  if (destination_base == 0) {
    return nullptr;
  }

  const std::array<uint32_t, 4> candidates =
      FableDisplayBaseCandidates(destination_base);
  for (uint32_t candidate : candidates) {
    if (candidate == 0) {
      continue;
    }
    const auto it =
        g_plume_resources.render_target_resolve_aliases.find(candidate);
    if (it != g_plume_resources.render_target_resolve_aliases.end()) {
      return &it->second;
    }
  }

  return nullptr;
}

const PlumeTrackedResource* FindTrackedColorRenderTargetLocked(
    uint32_t guest_address);

bool SurfaceCopyAliasMatchesDisplayLocked(
    const RendererSurfaceCopyAliasSnapshot& alias,
    const RendererHookObserverSnapshot& observer_snapshot) {
  if (alias.source_surface == 0 || alias.destination_surface == 0) {
    return false;
  }

  if (observer_snapshot.display_present_surface_object != 0 &&
      alias.destination_surface ==
          observer_snapshot.display_present_surface_object) {
    return true;
  }

  if (observer_snapshot.display_present_texture_base != 0 &&
      FableDisplayBasesMatch(alias.destination_surface,
                             observer_snapshot.display_present_texture_base)) {
    return true;
  }

  const PlumeTrackedResource* destination =
      FindTrackedColorRenderTargetLocked(alias.destination_surface);
  if (!destination) {
    return false;
  }
  if (observer_snapshot.display_present_surface_object != 0 &&
      (destination->guest_address ==
           observer_snapshot.display_present_surface_object ||
       destination->parent_address ==
           observer_snapshot.display_present_surface_object ||
       destination->buffer_object ==
           observer_snapshot.display_present_surface_object)) {
    return true;
  }
  return observer_snapshot.display_present_texture_base != 0 &&
         FableDisplayBasesMatch(destination->texture_guest_base,
                                observer_snapshot.display_present_texture_base);
}

const RendererSurfaceCopyAliasSnapshot* FindDisplaySurfaceCopyAliasLocked(
    const RendererHookObserverSnapshot& observer_snapshot) {
  const RendererSurfaceCopyAliasSnapshot* best = nullptr;
  const uint32_t count = std::min<uint32_t>(
      observer_snapshot.surface_copy_alias_count,
      static_cast<uint32_t>(observer_snapshot.surface_copy_aliases.size()));
  for (uint32_t i = 0; i < count; ++i) {
    const RendererSurfaceCopyAliasSnapshot& alias =
        observer_snapshot.surface_copy_aliases[i];
    if (!SurfaceCopyAliasMatchesDisplayLocked(alias, observer_snapshot)) {
      continue;
    }
    if (!best || alias.serial > best->serial) {
      best = &alias;
    }
  }
  return best;
}

uint32_t FableFetchTextureWidth(
    const rex::graphics::xenos::xe_gpu_texture_fetch_t& fetch) {
  namespace xenos = rex::graphics::xenos;
  switch (fetch.dimension) {
    case xenos::DataDimension::k1D:
      return fetch.size_1d.width + 1u;
    case xenos::DataDimension::k3D:
      return fetch.size_3d.width + 1u;
    case xenos::DataDimension::k2DOrStacked:
    case xenos::DataDimension::kCube:
      return fetch.size_2d.width + 1u;
    default:
      return 0;
  }
}

uint32_t FableFetchTextureHeight(
    const rex::graphics::xenos::xe_gpu_texture_fetch_t& fetch) {
  namespace xenos = rex::graphics::xenos;
  switch (fetch.dimension) {
    case xenos::DataDimension::k1D:
      return 1;
    case xenos::DataDimension::k3D:
      return fetch.size_3d.height + 1u;
    case xenos::DataDimension::k2DOrStacked:
    case xenos::DataDimension::kCube:
      return fetch.size_2d.height + 1u;
    default:
      return 0;
  }
}

bool FableResolveAliasMatchesFetch(
    const PlumeTrackedResource& resource,
    const rex::graphics::xenos::xe_gpu_texture_fetch_t& fetch) {
  namespace xenos = rex::graphics::xenos;
  if (fetch.dimension != xenos::DataDimension::k2DOrStacked) {
    return false;
  }

  const uint32_t fetch_width = FableFetchTextureWidth(fetch);
  const uint32_t fetch_height = FableFetchTextureHeight(fetch);
  if (fetch_width == 0 || fetch_height == 0 ||
      resource.texture_width != fetch_width ||
      resource.texture_height != fetch_height ||
      resource.texture_depth != 1) {
    return false;
  }

  if (resource.texture_depth_target) {
    return true;
  }

  const plume::RenderFormat fetch_format =
      ConvertXenosTextureFormat(fetch.format);
  return fetch_format == plume::RenderFormat::UNKNOWN ||
         resource.texture_format == fetch_format;
}

void CacheNewestTextureShapeResourceLocked(
    const FableTextureShapeKey& key, const PlumeTrackedResource& resource) {
  auto [it, inserted] =
      g_plume_resources.texture_fetch_shape_index.emplace(key, &resource);
  if (!inserted && (!it->second || resource.serial > it->second->serial)) {
    it->second = &resource;
  }
}

void CacheNewestTextureFetchResourceLocked(
    const FableTextureFetchKey& key, const PlumeTrackedResource& resource) {
  auto [it, inserted] =
      g_plume_resources.texture_fetch_exact_index.emplace(key, &resource);
  if (!inserted && (!it->second || resource.serial > it->second->serial)) {
    it->second = &resource;
  }
}

void CacheTextureLookupForResourceLocked(const PlumeTrackedResource& resource) {
  if (!IsImageResource(resource.kind) || !resource.texture ||
      !resource.texture_view) {
    return;
  }

  FableTextureFetchKey exact_key{};
  exact_key.dwords = resource.texture_fetch_dwords;
  const bool has_exact_key =
      (exact_key.dwords[0] | exact_key.dwords[1] | exact_key.dwords[2] |
       exact_key.dwords[3] | exact_key.dwords[4] | exact_key.dwords[5]) != 0;
  if (has_exact_key) {
    CacheNewestTextureFetchResourceLocked(exact_key, resource);
  }

  const FableTextureShapeKey shape_key{
      resource.texture_guest_base, resource.texture_guest_mips,
      resource.texture_guest_pitch, resource.texture_guest_format,
      resource.texture_guest_dimension};
  if ((shape_key.base | shape_key.mips | shape_key.pitch | shape_key.format |
       shape_key.dimension) != 0) {
    CacheNewestTextureShapeResourceLocked(shape_key, resource);
  }
}

bool SameTrackedTextureShape(const PlumeTrackedResource& lhs,
                             const PlumeTrackedResource& rhs) {
  return lhs.texture_dimension == rhs.texture_dimension &&
         lhs.texture_view_dimension == rhs.texture_view_dimension &&
         lhs.texture_format == rhs.texture_format &&
         lhs.texture_width == rhs.texture_width &&
         lhs.texture_height == rhs.texture_height &&
         lhs.texture_depth == rhs.texture_depth &&
         lhs.texture_mip_levels == rhs.texture_mip_levels &&
         lhs.texture_guest_base == rhs.texture_guest_base &&
         lhs.texture_guest_mips == rhs.texture_guest_mips &&
         lhs.texture_guest_pitch == rhs.texture_guest_pitch &&
         lhs.texture_guest_format == rhs.texture_guest_format &&
         lhs.texture_guest_dimension == rhs.texture_guest_dimension &&
         lhs.texture_fetch_dwords == rhs.texture_fetch_dwords &&
         lhs.texture_guest_tiled == rhs.texture_guest_tiled &&
         lhs.texture_render_target == rhs.texture_render_target &&
         lhs.texture_depth_target == rhs.texture_depth_target;
}

void RebuildFableTextureLookupIndexLocked() {
  g_plume_resources.texture_fetch_exact_index.clear();
  g_plume_resources.texture_fetch_shape_index.clear();
  for (const auto& [address, resource] : g_plume_resources.resources) {
    (void)address;
    CacheTextureLookupForResourceLocked(resource);
  }
  for (const auto& [address, resource] :
       g_plume_resources.decoded_color_targets) {
    (void)address;
    CacheTextureLookupForResourceLocked(resource);
  }
  for (const auto& [address, resource] :
       g_plume_resources.decoded_depth_targets) {
    (void)address;
    CacheTextureLookupForResourceLocked(resource);
  }
}

void EnsureFableTextureLookupIndexLocked() {
  if (g_plume_resources.texture_lookup_index_valid &&
      g_plume_resources.resource_sync_version_valid &&
      SameTrackedResourceVersion(
          g_plume_resources.texture_lookup_index_version,
          g_plume_resources.last_resource_sync_version)) {
    return;
  }

  RebuildFableTextureLookupIndexLocked();
  g_plume_resources.texture_lookup_index_version =
      g_plume_resources.resource_sync_version_valid
          ? g_plume_resources.last_resource_sync_version
          : RendererTrackedResourceVersion{};
  g_plume_resources.texture_lookup_index_valid =
      g_plume_resources.resource_sync_version_valid;
}

bool SetFableTextureDescriptorAtIndexLocked(
    uint32_t set_index, uint32_t descriptor_index,
    plume::RenderTexture* texture, plume::RenderTextureLayout layout,
    plume::RenderTextureView* view) {
  if (set_index >= std::size(g_plume_resources.texture_descriptor_sets) ||
      descriptor_index >= kFableDescriptorCount ||
      !g_plume_resources.texture_descriptor_sets[set_index] || !texture ||
      !view) {
    return false;
  }

  PlumeCachedTextureDescriptor& cached =
      g_plume_resources.cached_texture_descriptors[set_index][descriptor_index];
  if (cached.texture == texture && cached.view == view &&
      cached.layout == layout) {
    return true;
  }

  g_plume_resources.texture_descriptor_sets[set_index]->setTexture(
      descriptor_index, texture, layout, view);
  cached.texture = texture;
  cached.view = view;
  cached.layout = layout;
  return true;
}

bool SetFableSamplerDescriptorAtIndexLocked(uint32_t descriptor_index,
                                            plume::RenderSampler* sampler) {
  if (descriptor_index >= kFableDescriptorCount ||
      !g_plume_resources.sampler_descriptor_set || !sampler) {
    return false;
  }

  plume::RenderSampler*& cached =
      g_plume_resources.cached_sampler_descriptors[descriptor_index];
  if (cached == sampler) {
    return true;
  }

  g_plume_resources.sampler_descriptor_set->setSampler(descriptor_index,
                                                       sampler);
  cached = sampler;
  return true;
}

void TrimRetiredFableFramebuffersLocked() {
  while (!g_plume_resources.retired_framebuffers.empty()) {
    const PlumeRetiredFramebuffer& retired =
        g_plume_resources.retired_framebuffers.front();
    const bool old_enough =
        retired.retire_epoch + kRetiredPlumeResourceEpochs <=
        g_plume_resources.resource_retire_epoch;
    const bool over_budget =
        g_plume_resources.retired_framebuffers.size() >
        kMaxFableFramebufferCacheEntries * 2u;
    if (!old_enough && !over_budget) {
      break;
    }
    g_plume_resources.retired_framebuffers.pop_front();
  }
}

void RetireFableFramebuffersLocked() {
  if (g_plume_resources.framebuffers.empty()) {
    return;
  }
  for (auto& [key, framebuffer] : g_plume_resources.framebuffers) {
    (void)key;
    if (framebuffer) {
      g_plume_resources.retired_framebuffers.push_back(
          PlumeRetiredFramebuffer{g_plume_resources.resource_retire_epoch,
                                  std::move(framebuffer)});
    }
  }
  g_plume_resources.framebuffers.clear();
  TrimRetiredFableFramebuffersLocked();
}

void TrimRetiredPlumeResourcesLocked() {
  TrimRetiredFableFramebuffersLocked();
  while (!g_plume_resources.retired_resources.empty()) {
    const PlumeRetiredResource& retired =
        g_plume_resources.retired_resources.front();
    const uint64_t retire_epochs =
        IsBufferResource(retired.resource.kind)
            ? kRetiredPlumeBufferResourceEpochs
            : kRetiredPlumeResourceEpochs;
    const bool old_enough =
        retired.retire_epoch + retire_epochs <=
        g_plume_resources.resource_retire_epoch;
    const bool over_budget =
        g_plume_resources.retired_resources.size() > kMaxRetiredPlumeResources;
    if (!old_enough && !over_budget) {
      break;
    }
    g_plume_resources.retired_resources.pop_front();
  }
}

void RetirePlumeResourceLocked(PlumeTrackedResource&& resource,
                               uint64_t valid_until_serial = 0) {
  if (!resource.buffer && !resource.shader && !resource.texture &&
      !resource.texture_view && !resource.texture_upload) {
    return;
  }
  if (resource.texture || resource.texture_view) {
    RetireFableFramebuffersLocked();
    InvalidateFableDescriptorCacheLocked();
  }
  g_plume_resources.retired_resources.push_back(
      PlumeRetiredResource{g_plume_resources.resource_retire_epoch,
                           valid_until_serial, std::move(resource)});
  TrimRetiredPlumeResourcesLocked();
}
#endif

bool SameTrackedResourceVersion(
    const RendererTrackedResourceVersion& lhs,
    const RendererTrackedResourceVersion& rhs) {
  return lhs.serial == rhs.serial &&
         lhs.resource_creates == rhs.resource_creates &&
         lhs.resource_destroys == rhs.resource_destroys &&
         lhs.resource_unlocks == rhs.resource_unlocks;
}

#if REFII_RENDERER_HAS_PLUME
constexpr uint32_t kDefaultVertexInputSlot = 15;
constexpr uint32_t kDefaultVertexInputStride = 4;
constexpr uint32_t kDefaultVertexInputBufferSize = 1024 * 1024;
constexpr uint32_t kTextureUploadRowAlignment = 256;

constexpr uint32_t kTextureUploadPlacementAlignment = 512;
constexpr uint64_t kMaxTextureUploadBufferSize = 128ull << 20;
constexpr uint64_t kFrameConstantSlabSize = 16ull << 20;
constexpr uint64_t kFrameConstantAlignment = 256;
constexpr size_t kTrackedBufferDebugSampleBytes = 64 * 1024;
constexpr uint32_t kFableSpecConstantR11G11B10Normal = 1u << 0;
constexpr uint32_t kFableSpecConstantAlphaTest = 1u << 1;
constexpr uint32_t kFableSpecConstantReverseZ = 1u << 4;
constexpr float kFallbackAlphaTestThreshold = 1.0f / 255.0f;

enum FableRenderStateSlot : uint32_t {
  kFableRenderStateZFunc = 0,
  kFableRenderStateColorWriteEnable = 1,
  kFableRenderStateAlphaBlendEnable = 2,
  kFableRenderStateSrcBlend = 3,
  kFableRenderStateZWriteEnable = 4,
  kFableRenderStateDestBlend = 5,
  kFableRenderStateAlphaTestEnable = 6,
  kFableRenderStateZEnable = 7,
  kFableRenderStateHiZWriteEnable = 8,
  kFableRenderStateBlendOp = 9,
  kFableRenderStateBlendOpAlpha = 10,
  kFableRenderStateSrcBlendAlpha = 11,
  kFableRenderStateDestBlendAlpha = 12,
  kFableRenderStateSeparateAlphaBlendEnable = 13,
  kFableRenderStateClipPlaneEnable = 14,
  kFableRenderStateHighPrecisionBlendEnable = 15,
  kFableRenderStateStencilFail = 16,
  kFableRenderStateStencilZFail = 17,
  kFableRenderStateStencilEnable = 18,
  kFableRenderStateCullMode = 19,
  kFableRenderStateStencilPass = 20,
  kFableRenderStateStencilFunc = 21,
  kFableRenderStateStencilReadMask = 22,
  kFableRenderStateStencilRef = 23,
  kFableRenderStateAlphaFunc = 24,
  kFableRenderStateAlphaRef = 25,
  kFableRenderStatePixelCenterOffset = 26,
  kFableRenderStateStencilWriteMask = 27,
};

struct FablePipelineRenderState {
  plume::RenderBlendDesc blend = plume::RenderBlendDesc::Copy();
  plume::RenderCullMode cull_mode = plume::RenderCullMode::NONE;
  plume::RenderFrontFace front_face = plume::RenderFrontFace::CLOCKWISE;
  plume::RenderComparisonFunction depth_function =
      plume::RenderComparisonFunction::LESS;
  plume::RenderStencilFaceDesc stencil_front_face;
  plume::RenderStencilFaceDesc stencil_back_face;
  uint32_t stencil_read_mask = 0xFFu;
  uint32_t stencil_write_mask = 0xFFu;
  uint32_t stencil_reference = 0;
  bool depth_enabled = false;
  bool depth_write_enabled = false;
  bool stencil_enabled = false;
  bool alpha_test_enabled = false;
};

struct FableIndexRangeSummary {
  bool available = false;
  bool range_valid = false;
  uint32_t stride = 0;
  uint32_t total_indices = 0;
  uint32_t first = 0;
  uint32_t count = 0;
  uint32_t min_index = 0;
  uint32_t max_index = 0;
  std::array<uint32_t, 8> sample = {};
  uint32_t sample_count = 0;
};

uint32_t PackF32(float value) {
  uint32_t bits = 0;
  static_assert(sizeof(bits) == sizeof(value));
  std::memcpy(&bits, &value, sizeof(bits));
  return bits;
}

float UnpackF32(uint32_t bits) {
  float value = 0.0f;
  static_assert(sizeof(bits) == sizeof(value));
  std::memcpy(&value, &bits, sizeof(value));
  return value;
}

uint32_t FableAlphaFunc(const RendererDrawEventSnapshot& draw_event) {
  return draw_event.pm4_registers_valid
             ? (draw_event.rb_colorcontrol & 0x7u)
             : draw_event.render_states[kFableRenderStateAlphaFunc];
}

bool FableAlphaFuncMatchesGeneratedClip(uint32_t alpha_func) {
  // The translated XenosRecomp shaders currently expose one alpha-test shape:
  // clip(oC0.a - g_AlphaThreshold), which represents GREATER/GREATER_EQUAL.
  return alpha_func == 4u || alpha_func == 6u;
}

bool FableAlphaTestEnabled(const RendererDrawEventSnapshot& draw_event) {
  if (draw_event.pm4_registers_valid) {
    const uint32_t alpha_func = FableAlphaFunc(draw_event);
    const bool alpha_test_enable = (draw_event.rb_colorcontrol & 0x8u) != 0;
    return alpha_test_enable && FableAlphaFuncMatchesGeneratedClip(alpha_func);
  }
  return draw_event.render_states[kFableRenderStateAlphaTestEnable] != 0 &&
         FableAlphaFuncMatchesGeneratedClip(FableAlphaFunc(draw_event));
}

bool FableViewportUsesReverseZ(const RendererDrawEventSnapshot& draw_event) {
  return draw_event.viewport.valid && std::isfinite(draw_event.viewport.min_depth) &&
         std::isfinite(draw_event.viewport.max_depth) &&
         draw_event.viewport.min_depth > draw_event.viewport.max_depth;
}

uint32_t FableAlphaThresholdBits(const RendererDrawEventSnapshot& draw_event) {
  if (!FableAlphaTestEnabled(draw_event)) {
    return 0;
  }
  if (draw_event.pm4_registers_valid) {
    float alpha_ref = UnpackF32(draw_event.rb_alpha_ref);
    if (!std::isfinite(alpha_ref) || alpha_ref < 0.0f || alpha_ref > 1.0f ||
        (draw_event.rb_alpha_ref != 0 && alpha_ref < (1.0f / 65535.0f) &&
         draw_event.rb_alpha_ref <= 0xFFu)) {
      alpha_ref = static_cast<float>(draw_event.rb_alpha_ref & 0xFFu) /
                  255.0f;
    }
    if (FableAlphaFunc(draw_event) == 4u) {
      alpha_ref = std::min(1.0f, alpha_ref + (0.5f / 255.0f));
    }
    return PackF32(alpha_ref);
  }
  float alpha_ref =
      static_cast<float>(draw_event.render_states[kFableRenderStateAlphaRef] &
                         0xFFu) /
      255.0f;
  if (draw_event.render_states[kFableRenderStateAlphaRef] == 0) {
    alpha_ref = kFallbackAlphaTestThreshold;
  }
  return PackF32(alpha_ref);
}

const char* TrackedResourceKindName(RendererTrackedResourceKind kind) {
  switch (kind) {
    case RendererTrackedResourceKind::kUnknown:
      return "unknown";
    case RendererTrackedResourceKind::kTexture:
      return "texture";
    case RendererTrackedResourceKind::kSurface:
      return "surface";
    case RendererTrackedResourceKind::kDepthSurface:
      return "depth_surface";
    case RendererTrackedResourceKind::kVertexBuffer:
      return "vertex_buffer";
    case RendererTrackedResourceKind::kIndexBuffer:
      return "index_buffer";
    case RendererTrackedResourceKind::kVertexDeclaration:
      return "vertex_declaration";
    case RendererTrackedResourceKind::kVertexShader:
      return "vertex_shader";
    case RendererTrackedResourceKind::kPixelShader:
      return "pixel_shader";
  }

  return "unknown";
}

bool IsBufferResource(RendererTrackedResourceKind kind) {
  return kind == RendererTrackedResourceKind::kVertexBuffer ||
         kind == RendererTrackedResourceKind::kIndexBuffer;
}

bool IsShaderResource(RendererTrackedResourceKind kind) {
  return kind == RendererTrackedResourceKind::kVertexShader ||
         kind == RendererTrackedResourceKind::kPixelShader;
}

bool IsDeclarationResource(RendererTrackedResourceKind kind) {
  return kind == RendererTrackedResourceKind::kVertexDeclaration;
}

bool IsTextureResource(RendererTrackedResourceKind kind) {
  return kind == RendererTrackedResourceKind::kTexture;
}

bool IsSurfaceResource(RendererTrackedResourceKind kind) {
  return kind == RendererTrackedResourceKind::kSurface ||
         kind == RendererTrackedResourceKind::kDepthSurface;
}

bool IsDepthSurfaceResource(RendererTrackedResourceKind kind) {
  return kind == RendererTrackedResourceKind::kDepthSurface;
}

bool IsImageResource(RendererTrackedResourceKind kind) {
  return IsTextureResource(kind) || IsSurfaceResource(kind);
}

bool IsLikelyFableObjectAddress(uint32_t guest_address) {
  return guest_address >= 0x40000000u && guest_address < 0x80000000u &&
         (guest_address & (alignof(uint32_t) - 1u)) == 0;
}

bool IsPackedPrintableAsciiWord(uint32_t word) {
  uint32_t printable_bytes = 0;
  for (uint32_t byte_index = 0; byte_index < 4; ++byte_index) {
    const uint8_t byte =
        static_cast<uint8_t>((word >> (byte_index * 8u)) & 0xFFu);
    if (byte >= 0x20u && byte <= 0x7Eu) {
      ++printable_bytes;
    }
  }
  return printable_bytes >= 3;
}

bool PackedAsciiWordContainsPathByte(uint32_t word) {
  for (uint32_t byte_index = 0; byte_index < 4; ++byte_index) {
    const uint8_t byte =
        static_cast<uint8_t>((word >> (byte_index * 8u)) & 0xFFu);
    if (byte == '\\' || byte == '/' || byte == '.') {
      return true;
    }
  }
  return false;
}

bool FableWordsLookLikeInlinePathString(const std::vector<uint32_t>& words) {
  if (words.size() < 4) {
    return false;
  }

  uint32_t printable_words = 0;
  bool has_path_byte = false;
  const size_t word_count = std::min<size_t>(words.size(), 16);
  for (size_t i = 1; i < word_count; ++i) {
    if (!IsPackedPrintableAsciiWord(words[i])) {
      continue;
    }
    ++printable_words;
    has_path_byte = has_path_byte || PackedAsciiWordContainsPathByte(words[i]);
  }

  return has_path_byte && printable_words >= 2;
}

void AppendFableHandlePointerCandidates(const std::vector<uint32_t>& words,
                                        std::vector<uint32_t>& candidates) {
  if (words.empty()) {
    return;
  }

  const bool inline_path = FableWordsLookLikeInlinePathString(words);
  if (inline_path) {
    constexpr size_t kInlinePathPointerWords[] = {
        0,   // Next wrapper/list node.
        12,  // Secondary payload pointer after the inline path payload.
    };
    for (const size_t index : kInlinePathPointerWords) {
      if (index >= words.size() ||
          !IsLikelyFableObjectAddress(words[index])) {
        continue;
      }
      candidates.push_back(words[index]);
    }
    return;
  }

  for (uint32_t word : words) {
    if (IsLikelyFableObjectAddress(word)) {
      candidates.push_back(word);
    }
  }
}

struct FableSurfaceShape {
  uint32_t width = 0;
  uint32_t height = 0;
  uint32_t pitch_width = 0;
  uint32_t aligned_width = 0;
  uint32_t multisample_type = 0;
  plume::RenderFormat format = plume::RenderFormat::UNKNOWN;
};

bool IsPlausibleFableSurfaceShape(const FableSurfaceShape& shape) {
  if (shape.width == 0 || shape.height == 0 || shape.width > 8192u ||
      shape.height > 8192u || shape.multisample_type > 3u) {
    return false;
  }

  const uint32_t max_expected_pitch =
      std::max(shape.width * 2u, shape.width + 512u);
  if (shape.pitch_width != 0 &&
      (shape.pitch_width < shape.width ||
       shape.pitch_width > max_expected_pitch)) {
    return false;
  }
  if (shape.aligned_width != 0 &&
      (shape.aligned_width < shape.width ||
       shape.aligned_width > max_expected_pitch)) {
    return false;
  }

  return true;
}

bool IsPlausibleFableColorRenderTargetExtent(uint32_t width, uint32_t height) {
  constexpr uint32_t kMaxDecodedColorTargetExtent = 2048;
  constexpr uint64_t kMaxDecodedColorTargetPixels =
      static_cast<uint64_t>(kMaxDecodedColorTargetExtent) *
      static_cast<uint64_t>(kMaxDecodedColorTargetExtent);
  return width != 0 && height != 0 &&
         width <= kMaxDecodedColorTargetExtent &&
         height <= kMaxDecodedColorTargetExtent &&
         static_cast<uint64_t>(width) * height <=
             kMaxDecodedColorTargetPixels;
}

bool IsPlausibleDecodedFableColorRenderTargetShape(
    const FableSurfaceShape& shape) {
  if (!IsPlausibleFableSurfaceShape(shape)) {
    return false;
  }

  // Fable 2's D3D9 render targets seen on the hook path are screen-sized or
  // small post-process surfaces. Treat huge fallback-decoded color targets as
  // false positives so a random surface-looking object cannot make Plume
  // allocate and replay through an enormous offscreen framebuffer.
  constexpr uint32_t kMinDecodedColorTargetExtent = 16;
  if (shape.width < kMinDecodedColorTargetExtent ||
      shape.height < kMinDecodedColorTargetExtent) {
    return false;
  }
  if (!IsPlausibleFableColorRenderTargetExtent(shape.width, shape.height)) {
    return false;
  }

  return true;
}

bool IsPlausibleDecodedFableDepthTargetShape(
    const FableSurfaceShape& shape) {
  if (!IsPlausibleFableSurfaceShape(shape)) {
    return false;
  }

  // The Fable 2 depth targets seen on the D3D9 path are real render/depth
  // surfaces, not single-column metadata-looking aliases. Reject tiny decoded
  // fallback shapes so an object with surface-like words cannot replace a
  // valid tracked depth target.
  constexpr uint32_t kMinDecodedDepthTargetExtent = 16;
  constexpr uint32_t kMaxDecodedDepthTargetExtent = 4096;
  constexpr uint64_t kMaxDecodedDepthTargetPixels =
      static_cast<uint64_t>(kMaxDecodedDepthTargetExtent) *
      static_cast<uint64_t>(kMaxDecodedDepthTargetExtent);
  return shape.width >= kMinDecodedDepthTargetExtent &&
         shape.height >= kMinDecodedDepthTargetExtent &&
         shape.width <= kMaxDecodedDepthTargetExtent &&
         shape.height <= kMaxDecodedDepthTargetExtent &&
         static_cast<uint64_t>(shape.width) * shape.height <=
             kMaxDecodedDepthTargetPixels;
}

bool DecodeFableSurfaceExtentFromPacked(uint32_t packed_extent,
                                        uint32_t packed_dimension,
                                        uint32_t& width, uint32_t& height,
                                        uint32_t* depth = nullptr) {
  (void)packed_dimension;
  if (packed_extent == 0) {
    return false;
  }

  const uint32_t decoded_width = (packed_extent >> 18u) + 1u;
  const uint32_t decoded_height = ((packed_extent >> 3u) & 0x7FFFu) + 1u;
  constexpr uint32_t decoded_depth = 1;

  if (decoded_width == 0 || decoded_height == 0 || decoded_width > 8192u ||
      decoded_height > 8192u || decoded_depth > 8192u) {
    return false;
  }

  width = decoded_width;
  height = decoded_height;
  if (depth) {
    *depth = decoded_depth;
  }
  return true;
}

bool DecodeFableSurfaceExtentFromWords(const std::vector<uint32_t>& words,
                                       uint32_t& width, uint32_t& height) {
  constexpr size_t kPackedExtentWord = 9;
  constexpr size_t kPackedDimensionWord = 12;
  if (FableWordsLookLikeInlinePathString(words)) {
    return false;
  }
  if (words.size() <= kPackedExtentWord || words[kPackedExtentWord] == 0) {
    return false;
  }

  const uint32_t packed_dimension =
      words.size() > kPackedDimensionWord ? words[kPackedDimensionWord] : 0;
  return DecodeFableSurfaceExtentFromPacked(words[kPackedExtentWord],
                                           packed_dimension, width, height);
}

plume::RenderFormat ConvertXenosColorRenderTargetFormat(
    rex::graphics::xenos::ColorRenderTargetFormat format);
bool IsBlockCompressedFormat(plume::RenderFormat format);
uint32_t ClampGuestTextureExtent(uint32_t extent);

plume::RenderFormat ConvertFableD3DDepthFormat(uint32_t d3d_format) {
  switch (d3d_format & 0x3Fu) {
    case 6:   // D3DFMT_D16_LOCKABLE & 0x3F.
    case 16:  // D3DFMT_D16 & 0x3F.
      return plume::RenderFormat::D16_UNORM;
    case 7:   // D3DFMT_D32 & 0x3F.
    case 13:  // D3DFMT_D24X8 & 0x3F.
    case 18:  // D3DFMT_D32F_LOCKABLE & 0x3F.
    case 20:  // D3DFMT_D32_LOCKABLE & 0x3F.
      return plume::RenderFormat::D32_FLOAT;
    case 9:   // D3DFMT_D15S1 & 0x3F.
    case 11:  // D3DFMT_D24S8 & 0x3F.
    case 15:  // D3DFMT_D24X4S4 & 0x3F.
    case 19:  // D3DFMT_D24FS8 & 0x3F.
    case 0:
    default:
      return plume::RenderFormat::D32_FLOAT_S8_UINT;
  }
}

plume::RenderFormat DecodeFableSurfaceRenderFormatFromWords(
    const std::vector<uint32_t>& words, RendererTrackedResourceKind kind) {
  constexpr size_t kPackedRenderTargetInfoWord = 7;
  constexpr size_t kPackedDepthFormatWord = 10;
  if (IsDepthSurfaceResource(kind)) {
    const uint32_t d3d_format =
        words.size() > kPackedDepthFormatWord
            ? (words[kPackedDepthFormatWord] & 0x3Fu)
            : 0;
    return ConvertFableD3DDepthFormat(d3d_format);
  }
  if (words.size() <= kPackedRenderTargetInfoWord) {
    return plume::RenderFormat::UNKNOWN;
  }

  namespace xenos = rex::graphics::xenos;
  const auto color_format = static_cast<xenos::ColorRenderTargetFormat>(
      (words[kPackedRenderTargetInfoWord] >> 16u) & 0xFu);
  return ConvertXenosColorRenderTargetFormat(color_format);
}

bool DecodeFableSurfaceShapeFromWords(const std::vector<uint32_t>& words,
                                      RendererTrackedResourceKind kind,
                                      FableSurfaceShape& shape) {
  constexpr size_t kPackedPitchWord = 6;
  if (FableWordsLookLikeInlinePathString(words)) {
    return false;
  }

  uint32_t width = 0;
  uint32_t height = 0;
  if (!DecodeFableSurfaceExtentFromWords(words, width, height)) {
    return false;
  }

  shape.width = width;
  shape.height = height;
  shape.format = DecodeFableSurfaceRenderFormatFromWords(words, kind);
  if (shape.format == plume::RenderFormat::UNKNOWN) {
    return false;
  }
  if (words.size() > kPackedPitchWord) {
    const uint32_t packed_pitch = words[kPackedPitchWord];
    const uint32_t width_and_msaa = packed_pitch >> 16u;
    shape.multisample_type = width_and_msaa & 0x3u;
    shape.aligned_width = (width_and_msaa & ~0x3u) >> 2u;
    shape.pitch_width = packed_pitch & 0x3FFFu;
  }
  return IsPlausibleFableSurfaceShape(shape);
}

template <typename T>
uint32_t CountNonZeroDwords(const T& values) {
  return static_cast<uint32_t>(
      std::count_if(values.begin(), values.end(),
                    [](uint32_t value) { return value != 0; }));
}

bool TrackedResourceMatches(const PlumeTrackedResource& tracked,
                            uint32_t address,
                            RendererTrackedResourceKind kind) {
  if (tracked.kind != kind) {
    return false;
  }

  if (IsBufferResource(kind) && !tracked.buffer) {
    return false;
  }
  if (IsShaderResource(kind) && !tracked.shader) {
    return false;
  }
  if (IsDeclarationResource(kind) && tracked.input_elements.empty()) {
    return false;
  }
  if (IsImageResource(kind) && !tracked.texture) {
    return false;
  }

  if (tracked.guest_address == address || tracked.parent_address == address ||
      tracked.buffer_object == address) {
    return true;
  }

  if (!IsBufferResource(kind) || tracked.size == 0) {
    return false;
  }

  const auto contains_address = [address, size = tracked.size](uint32_t base) {
    if (base == 0 || address < base) {
      return false;
    }
    return static_cast<uint64_t>(address - base) < size;
  };
  return contains_address(tracked.guest_address) ||
         contains_address(tracked.buffer_object);
}

uint32_t ResolveTrackedBufferOffset(const PlumeTrackedResource& tracked,
                                    uint32_t bound_address,
                                    uint32_t bound_offset) {
  uint64_t resolved_offset = bound_offset;
  const auto add_base_delta = [&](uint32_t base) {
    if (base == 0 || bound_address < base) {
      return false;
    }
    const uint64_t delta = static_cast<uint64_t>(bound_address - base);
    if (delta >= tracked.size) {
      return false;
    }
    resolved_offset += delta;
    return true;
  };

  if (!add_base_delta(tracked.buffer_object)) {
    add_base_delta(tracked.guest_address);
  }
  return resolved_offset > std::numeric_limits<uint32_t>::max()
             ? std::numeric_limits<uint32_t>::max()
             : static_cast<uint32_t>(resolved_offset);
}

const PlumeTrackedResource* FindTrackedResourceLocked(
    uint32_t guest_address, RendererTrackedResourceKind kind);

bool HasTrackedResourceLocked(uint32_t guest_address,
                              RendererTrackedResourceKind kind) {
  return FindTrackedResourceLocked(guest_address, kind) != nullptr;
}

struct ScopedPlumeResourceLookupSerial {
  explicit ScopedPlumeResourceLookupSerial(uint64_t serial)
      : previous(g_plume_resources.active_lookup_resource_serial) {
    g_plume_resources.active_lookup_resource_serial = serial;
  }

  ~ScopedPlumeResourceLookupSerial() {
    g_plume_resources.active_lookup_resource_serial = previous;
  }

  uint64_t previous = 0;
};

const PlumeTrackedResource* FindTrackedResourceDirectLocked(
    uint32_t guest_address, RendererTrackedResourceKind kind) {
  if (guest_address == 0) {
    return nullptr;
  }

  const uint64_t lookup_serial =
      g_plume_resources.active_lookup_resource_serial;
  if (lookup_serial != 0) {
    const FableResourceLookupKey cache_key{
        guest_address, static_cast<uint32_t>(kind), lookup_serial};
    auto cached_it = g_plume_resources.resource_lookup_cache.find(cache_key);
    if (cached_it != g_plume_resources.resource_lookup_cache.end()) {
      return cached_it->second;
    }

    const PlumeTrackedResource* best = nullptr;
    auto consider = [&](const PlumeTrackedResource& resource,
                        uint64_t valid_until_serial) {
      if (!TrackedResourceMatches(resource, guest_address, kind)) {
        return;
      }
      if (resource.serial > lookup_serial) {
        return;
      }
      if (valid_until_serial != 0 && lookup_serial > valid_until_serial) {
        return;
      }
      if (!best || resource.serial > best->serial) {
        best = &resource;
      }
    };

    auto it = g_plume_resources.resources.find(guest_address);
    if (it != g_plume_resources.resources.end()) {
      consider(it->second, 0);
    }
    for (const auto& [address, resource] : g_plume_resources.resources) {
      (void)address;
      consider(resource, 0);
    }
    for (const PlumeRetiredResource& retired :
         g_plume_resources.retired_resources) {
      consider(retired.resource, retired.valid_until_serial);
    }
    g_plume_resources.resource_lookup_cache.emplace(cache_key, best);
    return best;
  }

  auto it = g_plume_resources.resources.find(guest_address);
  if (it != g_plume_resources.resources.end() &&
      TrackedResourceMatches(it->second, guest_address, kind)) {
    return &it->second;
  }

  for (const auto& [address, resource] : g_plume_resources.resources) {
    (void)address;
    if (TrackedResourceMatches(resource, guest_address, kind)) {
      return &resource;
    }
  }

  return nullptr;
}

const PlumeTrackedResource* FindCurrentTrackedResourceDirectLocked(
    uint32_t guest_address, RendererTrackedResourceKind kind) {
  if (guest_address == 0) {
    return nullptr;
  }

  const PlumeTrackedResource* best = nullptr;
  for (const auto& [address, resource] : g_plume_resources.resources) {
    (void)address;
    if (!TrackedResourceMatches(resource, guest_address, kind)) {
      continue;
    }
    if (!best || resource.serial > best->serial) {
      best = &resource;
    }
  }
  return best;
}

const PlumeTrackedResource* FindTrackedResourceLocked(
    uint32_t guest_address, RendererTrackedResourceKind kind) {
  if (const PlumeTrackedResource* resource =
          FindTrackedResourceDirectLocked(guest_address, kind)) {
    return resource;
  }

  if (!IsImageResource(kind)) {
    return nullptr;
  }

  constexpr uint32_t kMaxImageHandleAliasDepth = 2;
  constexpr uint32_t kMaxImageHandleAliasWords = 16;
  constexpr size_t kMaxImageHandleAliasNodes = 32;
  std::vector<uint32_t> pending;
  std::unordered_set<uint32_t> visited;
  pending.reserve(kMaxImageHandleAliasNodes);
  visited.insert(guest_address);
  pending.push_back(guest_address);

  for (uint32_t depth = 0;
       depth < kMaxImageHandleAliasDepth && !pending.empty(); ++depth) {
    std::vector<uint32_t> next;
    next.reserve(std::min<size_t>(pending.size() * 2,
                                  kMaxImageHandleAliasNodes));

    for (uint32_t address : pending) {
      const std::vector<uint32_t> words =
          GetRendererGuestWords(address, kMaxImageHandleAliasWords);
      std::vector<uint32_t> candidates;
      candidates.reserve(words.size());
      AppendFableHandlePointerCandidates(words, candidates);
      for (uint32_t word : candidates) {
        if (word == 0 || !IsLikelyFableObjectAddress(word) ||
            !visited.insert(word).second) {
          continue;
        }
        if (const PlumeTrackedResource* resource =
                FindTrackedResourceDirectLocked(word, kind)) {
          return resource;
        }
        if (visited.size() < kMaxImageHandleAliasNodes) {
          next.push_back(word);
        }
      }
    }

    pending = std::move(next);
  }

  if (IsSurfaceResource(kind)) {
    uint32_t handle_width = 0;
    uint32_t handle_height = 0;
    const std::vector<uint32_t> words =
        GetRendererGuestWords(guest_address, kMaxImageHandleAliasWords);
    if (!FableWordsLookLikeInlinePathString(words) &&
        DecodeFableSurfaceExtentFromWords(words, handle_width,
                                          handle_height)) {
      const bool wants_depth = IsDepthSurfaceResource(kind);
      const plume::RenderFormat handle_format =
          DecodeFableSurfaceRenderFormatFromWords(words, kind);
      const PlumeTrackedResource* best = nullptr;
      uint32_t best_score = std::numeric_limits<uint32_t>::max();
      for (const auto& [address, resource] : g_plume_resources.resources) {
        (void)address;
        if (!IsSurfaceResource(resource.kind) || !resource.texture) {
          continue;
        }
        const bool is_depth = resource.texture_depth_target ||
                              plume::RenderFormatIsDepth(
                                  resource.texture_format);
        if (is_depth != wants_depth) {
          continue;
        }
        if (resource.guest_address == guest_address ||
            resource.parent_address == guest_address ||
            resource.buffer_object == guest_address) {
          return &resource;
        }

        const bool exact_extent = resource.texture_width == handle_width &&
                                  resource.texture_height == handle_height;
        const bool doubled_handle_extent =
            handle_width == resource.texture_width * 2u &&
            handle_height == resource.texture_height * 2u;
        if (!exact_extent && !doubled_handle_extent) {
          continue;
        }

        uint32_t score = exact_extent ? 0u : 20u;
        if (handle_format != plume::RenderFormat::UNKNOWN &&
            resource.texture_format != handle_format) {
          score += 10u;
        }
        if (score < best_score ||
            (score == best_score && best &&
             resource.serial > best->serial)) {
          best = &resource;
          best_score = score;
        }
      }
      if (best) {
        return best;
      }
    }
  }

  return nullptr;
}

const PlumeTrackedResource* FindTrackedColorRenderTargetLocked(
    uint32_t guest_address) {
  const PlumeTrackedResource* target = FindTrackedResourceLocked(
      guest_address, RendererTrackedResourceKind::kSurface);

  const auto is_valid_color_target =
      [](const PlumeTrackedResource* resource) {
        return resource && resource->texture && resource->texture_view &&
               resource->texture_render_target &&
               !plume::RenderFormatIsDepth(resource->texture_format);
      };

  const auto find_decoded_color_target =
      [&](uint32_t address) -> const PlumeTrackedResource* {
    const auto decoded_it = g_plume_resources.decoded_color_targets.find(address);
    if (decoded_it == g_plume_resources.decoded_color_targets.end()) {
      return nullptr;
    }
    const PlumeTrackedResource* decoded = &decoded_it->second;
    return is_valid_color_target(decoded) ? decoded : nullptr;
  };

  const auto find_existing_color_target =
      [&](uint32_t address) -> const PlumeTrackedResource* {
    if (!IsLikelyFableObjectAddress(address)) {
      return nullptr;
    }
    constexpr int32_t kNearbySurfaceHeaderOffsets[] = {
        0,      -0x30, 0x30,  -0x60, 0x60,  -0x90, 0x90,
        -0xC0,  0xC0,  -0xF0, 0xF0,  -0x120, 0x120,
        -0x150, 0x150, -0x180, 0x180};
    for (const int32_t offset : kNearbySurfaceHeaderOffsets) {
      const uint32_t nearby_address =
          static_cast<uint32_t>(address + offset);
      if (!IsLikelyFableObjectAddress(nearby_address)) {
        continue;
      }
      const PlumeTrackedResource* nearby = FindTrackedResourceDirectLocked(
          nearby_address, RendererTrackedResourceKind::kSurface);
      if (is_valid_color_target(nearby)) {
        return nearby;
      }
      if (const PlumeTrackedResource* decoded =
              find_decoded_color_target(nearby_address)) {
        return decoded;
      }
    }
    return nullptr;
  };

  const auto find_pointed_color_target =
      [&](uint32_t address) -> const PlumeTrackedResource* {
    constexpr uint32_t kMaxSurfaceHeaderHops = 3;
    constexpr size_t kMaxSurfaceHeaderNodes = 32;
    std::unordered_set<uint32_t> visited;
    std::vector<uint32_t> pending;
    pending.reserve(kMaxSurfaceHeaderNodes);
    pending.push_back(address);

    for (uint32_t hop = 0;
         hop < kMaxSurfaceHeaderHops && !pending.empty(); ++hop) {
      std::vector<uint32_t> next;
      next.reserve(std::min<size_t>(pending.size() * 2u,
                                    kMaxSurfaceHeaderNodes));
      for (uint32_t current : pending) {
        if (!IsLikelyFableObjectAddress(current) ||
            !visited.insert(current).second) {
          continue;
        }
        if (const PlumeTrackedResource* existing =
                find_existing_color_target(current)) {
          return existing;
        }
        if (const PlumeTrackedResource* decoded =
                find_decoded_color_target(current)) {
          return decoded;
        }

        const std::vector<uint32_t> words = GetRendererGuestWords(current, 16);
        std::vector<uint32_t> candidates;
        candidates.reserve(words.size());
        AppendFableHandlePointerCandidates(words, candidates);
        for (uint32_t candidate : candidates) {
          if (!IsLikelyFableObjectAddress(candidate) ||
              visited.find(candidate) != visited.end()) {
            continue;
          }
          if (const PlumeTrackedResource* pointed =
                  find_existing_color_target(candidate)) {
            return pointed;
          }
          if (const PlumeTrackedResource* pointed =
                  find_decoded_color_target(candidate)) {
            return pointed;
          }
          if (visited.size() + next.size() < kMaxSurfaceHeaderNodes) {
            next.push_back(candidate);
          }
        }
      }
      pending = std::move(next);
    }
    return nullptr;
  };

  if (!is_valid_color_target(target) &&
      IsLikelyFableObjectAddress(guest_address)) {
    target = find_existing_color_target(guest_address);
  }
  if (!is_valid_color_target(target) &&
      IsLikelyFableObjectAddress(guest_address)) {
    target = find_pointed_color_target(guest_address);
  }
  if (!is_valid_color_target(target)) {
    target = find_decoded_color_target(guest_address);
  }
  if (!is_valid_color_target(target)) {
    return nullptr;
  }
  return target;
}

const PlumeTrackedResource* FindTrackedDepthTargetLocked(
    uint32_t guest_address) {
  const auto is_valid_depth_target =
      [](const PlumeTrackedResource* resource) {
        return resource && resource->texture && resource->texture_view &&
               resource->texture_depth_target &&
               plume::RenderFormatIsDepth(resource->texture_format);
      };

  const auto find_decoded_depth_target =
      [&](uint32_t address) -> const PlumeTrackedResource* {
    const auto decoded_it =
        g_plume_resources.decoded_depth_targets.find(address);
    if (decoded_it == g_plume_resources.decoded_depth_targets.end()) {
      return nullptr;
    }
    const PlumeTrackedResource* decoded = &decoded_it->second;
    return is_valid_depth_target(decoded) ? decoded : nullptr;
  };

  const PlumeTrackedResource* target = FindTrackedResourceLocked(
      guest_address, RendererTrackedResourceKind::kDepthSurface);
  if (is_valid_depth_target(target)) {
    return target;
  }

  target = FindTrackedResourceLocked(guest_address,
                                     RendererTrackedResourceKind::kSurface);
  if (is_valid_depth_target(target)) {
    return target;
  }

  if (const PlumeTrackedResource* decoded =
          find_decoded_depth_target(guest_address)) {
    return decoded;
  }

  if (IsLikelyFableObjectAddress(guest_address)) {
    constexpr int32_t kNearbySurfaceHeaderOffsets[] = {-0x30, 0x30};
    for (const int32_t offset : kNearbySurfaceHeaderOffsets) {
      const uint32_t nearby_address =
          static_cast<uint32_t>(guest_address + offset);
      if (!IsLikelyFableObjectAddress(nearby_address)) {
        continue;
      }

      target = FindTrackedResourceDirectLocked(
          nearby_address, RendererTrackedResourceKind::kDepthSurface);
      if (is_valid_depth_target(target)) {
        return target;
      }

      target = FindTrackedResourceDirectLocked(
          nearby_address, RendererTrackedResourceKind::kSurface);
      if (is_valid_depth_target(target)) {
        return target;
      }

      if (const PlumeTrackedResource* decoded =
              find_decoded_depth_target(nearby_address)) {
        return decoded;
      }
    }
  }

  return nullptr;
}

bool DecodeFableSurfaceShapeForHandleLocked(uint32_t guest_address,
                                            RendererTrackedResourceKind kind,
                                            FableSurfaceShape& shape,
                                            uint32_t& header_address) {
  header_address = guest_address;
  constexpr uint32_t kMaxSurfaceHeaderHops = 3;
  constexpr size_t kMaxSurfaceHeaderNodes = 32;
  constexpr int32_t kNearbySurfaceHeaderOffsets[] = {
      0,      -0x30, 0x30,  -0x60, 0x60,  -0x90, 0x90,
      -0xC0,  0xC0,  -0xF0, 0xF0,  -0x120, 0x120,
      -0x150, 0x150, -0x180, 0x180};
  std::unordered_set<uint32_t> visited;
  std::vector<uint32_t> pending;
  pending.reserve(kMaxSurfaceHeaderNodes);
  pending.push_back(guest_address);

  const auto try_decode_nearby =
      [&](uint32_t center_address, FableSurfaceShape& decoded_shape,
          uint32_t& decoded_header) -> bool {
    if (!IsLikelyFableObjectAddress(center_address)) {
      return false;
    }
    for (const int32_t offset : kNearbySurfaceHeaderOffsets) {
      const uint32_t candidate_address =
          static_cast<uint32_t>(center_address + offset);
      if (!IsLikelyFableObjectAddress(candidate_address)) {
        continue;
      }
      const std::vector<uint32_t> words =
          GetRendererGuestWords(candidate_address, 16);
      if (DecodeFableSurfaceShapeFromWords(words, kind, decoded_shape)) {
        decoded_header = candidate_address;
        return true;
      }
    }
    return false;
  };

  for (uint32_t hop = 0;
       hop < kMaxSurfaceHeaderHops && !pending.empty(); ++hop) {
    std::vector<uint32_t> next;
    next.reserve(std::min<size_t>(pending.size() * 2u,
                                  kMaxSurfaceHeaderNodes));
    for (uint32_t address : pending) {
      if (address == 0 || !IsLikelyFableObjectAddress(address) ||
          !visited.insert(address).second) {
        continue;
      }

      std::vector<uint32_t> words = GetRendererGuestWords(address, 16);
      if (!FableWordsLookLikeInlinePathString(words) &&
          DecodeFableSurfaceShapeFromWords(words, kind, shape)) {
        header_address = address;
        return true;
      }
      if (!FableWordsLookLikeInlinePathString(words) &&
          try_decode_nearby(address, shape, header_address)) {
        return true;
      }

      std::vector<uint32_t> candidates;
      candidates.reserve(words.size());
      AppendFableHandlePointerCandidates(words, candidates);
      for (uint32_t candidate : candidates) {
        if (!IsLikelyFableObjectAddress(candidate) ||
            visited.find(candidate) != visited.end()) {
          continue;
        }
        if (visited.size() + next.size() < kMaxSurfaceHeaderNodes) {
          next.push_back(candidate);
        }
      }
    }
    pending = std::move(next);
  }

  return false;
}

bool BuildFableSurfaceTargetKeyLocked(uint32_t header_address,
                                      RendererTrackedResourceKind kind,
                                      FableSurfaceTargetKey& key) {
  if (!IsLikelyFableObjectAddress(header_address)) {
    return false;
  }

  const std::vector<uint32_t> words = GetRendererGuestWords(header_address, 16);
  if (words.size() < 13 || FableWordsLookLikeInlinePathString(words)) {
    return false;
  }

  FableSurfaceShape shape;
  if (!DecodeFableSurfaceShapeFromWords(words, kind, shape)) {
    return false;
  }

  key.kind = static_cast<uint32_t>(kind);
  for (uint32_t i = 0; i < key.packed_words.size(); ++i) {
    key.packed_words[i] = words[6u + i];
  }
  return true;
}

PlumeTrackedResource* FindDecodedTargetBySurfaceKeyLocked(
    std::unordered_map<uint32_t, PlumeTrackedResource>& targets,
    std::unordered_map<FableSurfaceTargetKey, uint32_t, FableSurfaceTargetKeyHash>&
        keys,
    const FableSurfaceTargetKey& key) {
  auto key_it = keys.find(key);
  if (key_it == keys.end()) {
    return nullptr;
  }

  auto target_it = targets.find(key_it->second);
  if (target_it == targets.end()) {
    keys.erase(key_it);
    return nullptr;
  }
  return &target_it->second;
}

PlumeTrackedResource* GetOrCreateDecodedColorRenderTargetLocked(
    plume::RenderDevice* device, uint32_t guest_address) {
  if (!device || !IsLikelyFableObjectAddress(guest_address)) {
    return nullptr;
  }

  if (FindTrackedResourceDirectLocked(guest_address,
                                      RendererTrackedResourceKind::kDepthSurface)) {
    return nullptr;
  }

  if (const PlumeTrackedResource* existing =
          FindTrackedColorRenderTargetLocked(guest_address)) {
    return const_cast<PlumeTrackedResource*>(existing);
  }

  FableSurfaceShape shape;
  uint32_t header_address = guest_address;
  if (!DecodeFableSurfaceShapeForHandleLocked(
          guest_address, RendererTrackedResourceKind::kSurface, shape,
          header_address) ||
      shape.width == 0 || shape.height == 0 ||
      shape.format == plume::RenderFormat::UNKNOWN ||
      plume::RenderFormatIsDepth(shape.format) ||
      IsBlockCompressedFormat(shape.format)) {
    return nullptr;
  }

  FableSurfaceTargetKey surface_key;
  const bool has_surface_key = BuildFableSurfaceTargetKeyLocked(
      header_address, RendererTrackedResourceKind::kSurface, surface_key);
  if (has_surface_key) {
    if (PlumeTrackedResource* existing_by_key =
            FindDecodedTargetBySurfaceKeyLocked(
                g_plume_resources.decoded_color_targets,
                g_plume_resources.decoded_color_target_keys, surface_key);
        existing_by_key && existing_by_key->texture &&
        existing_by_key->texture_view &&
        existing_by_key->texture_render_target &&
        existing_by_key->texture_width == ClampGuestTextureExtent(shape.width) &&
        existing_by_key->texture_height ==
            ClampGuestTextureExtent(shape.height) &&
        existing_by_key->texture_format == shape.format) {
      return existing_by_key;
    }
  }

  if (!IsPlausibleDecodedFableColorRenderTargetShape(shape)) {
    if (g_plume_resources.logged_rejected_decoded_color_targets.size() < 8 &&
        g_plume_resources.logged_rejected_decoded_color_targets
            .insert(guest_address)
            .second) {
      REXLOG_INFO(
          "Refii plume resource manager rejected decoded Fable color RT "
          "guest=0x{:08X} header=0x{:08X} size={}x{} pitch={} aligned={} "
          "msaa={} fmt={}",
          guest_address, header_address, shape.width, shape.height,
          shape.pitch_width, shape.aligned_width, shape.multisample_type,
          static_cast<uint32_t>(shape.format));
    }
    return nullptr;
  }

  auto existing_it = g_plume_resources.decoded_color_targets.find(guest_address);
  if (existing_it != g_plume_resources.decoded_color_targets.end()) {
    PlumeTrackedResource& existing = existing_it->second;
    if (existing.texture && existing.texture_view &&
        existing.texture_render_target &&
        existing.texture_width == shape.width &&
        existing.texture_height == shape.height &&
        existing.texture_format == shape.format) {
      if (has_surface_key) {
        g_plume_resources.decoded_color_target_keys[surface_key] =
            guest_address;
      }
      return &existing;
    }
    RetirePlumeResourceLocked(std::move(existing));
    g_plume_resources.decoded_color_targets.erase(existing_it);
  }

  PlumeTrackedResource tracked;
  tracked.kind = RendererTrackedResourceKind::kSurface;
  tracked.guest_address = guest_address;
  tracked.parent_address = header_address != guest_address ? header_address : 0;
  tracked.serial = ++g_plume_resources.resource_retire_epoch;
  tracked.texture_dimension = plume::RenderTextureDimension::TEXTURE_2D;
  tracked.texture_view_dimension = plume::RenderTextureViewDimension::TEXTURE_2D;
  tracked.texture_format = shape.format;
  tracked.texture_width = ClampGuestTextureExtent(shape.width);
  tracked.texture_height = ClampGuestTextureExtent(shape.height);
  tracked.texture_depth = 1;
  tracked.texture_mip_levels = 1;
  tracked.texture_render_target = true;
  tracked.texture = device->createTexture(plume::RenderTextureDesc::ColorTarget(
      tracked.texture_width, tracked.texture_height, tracked.texture_format));
  if (tracked.texture) {
    char name[160] = {};
    std::snprintf(name, sizeof(name),
                  "Refii Fable decoded surface 0x%08X %ux%u",
                  guest_address, tracked.texture_width, tracked.texture_height);
    tracked.texture->setName(name);
    tracked.texture_view = tracked.texture->createTextureView(
        plume::RenderTextureViewDesc::Texture2D(tracked.texture_format));
  }
  if (!tracked.texture || !tracked.texture_view) {
    REXLOG_WARN(
        "Refii plume resource manager could not adopt decoded Fable color RT "
        "guest=0x{:08X} size={}x{} fmt={}",
        guest_address, shape.width, shape.height,
        static_cast<uint32_t>(shape.format));
    return nullptr;
  }

  auto [inserted_it, inserted] =
      g_plume_resources.decoded_color_targets.emplace(guest_address,
                                                      std::move(tracked));
  (void)inserted;
  if (has_surface_key) {
    g_plume_resources.decoded_color_target_keys[surface_key] = guest_address;
  }
  ClearFableFrameLookupCacheLocked();
  CacheTextureLookupForResourceLocked(inserted_it->second);
  REXLOG_INFO(
      "Refii plume resource manager adopted decoded Fable color RT "
      "guest=0x{:08X} header=0x{:08X} size={}x{} pitch={} aligned={} fmt={}",
      guest_address, header_address, inserted_it->second.texture_width,
      inserted_it->second.texture_height, shape.pitch_width,
      shape.aligned_width, static_cast<uint32_t>(shape.format));
  return &inserted_it->second;
}

PlumeTrackedResource* GetOrCreateDecodedDepthTargetLocked(
    plume::RenderDevice* device, uint32_t guest_address) {
  if (!device || !IsLikelyFableObjectAddress(guest_address)) {
    return nullptr;
  }

  if (const PlumeTrackedResource* existing =
          FindTrackedDepthTargetLocked(guest_address)) {
    return const_cast<PlumeTrackedResource*>(existing);
  }

  FableSurfaceShape shape;
  uint32_t header_address = guest_address;
  if (!DecodeFableSurfaceShapeForHandleLocked(
          guest_address, RendererTrackedResourceKind::kDepthSurface, shape,
          header_address) ||
      shape.width == 0 || shape.height == 0 ||
      !plume::RenderFormatIsDepth(shape.format)) {
    return nullptr;
  }

  FableSurfaceTargetKey surface_key;
  const bool has_surface_key = BuildFableSurfaceTargetKeyLocked(
      header_address, RendererTrackedResourceKind::kDepthSurface, surface_key);
  if (has_surface_key) {
    if (PlumeTrackedResource* existing_by_key =
            FindDecodedTargetBySurfaceKeyLocked(
                g_plume_resources.decoded_depth_targets,
                g_plume_resources.decoded_depth_target_keys, surface_key);
        existing_by_key && existing_by_key->texture &&
        existing_by_key->texture_view &&
        existing_by_key->texture_depth_target &&
        existing_by_key->texture_width == ClampGuestTextureExtent(shape.width) &&
        existing_by_key->texture_height ==
            ClampGuestTextureExtent(shape.height) &&
        existing_by_key->texture_format == shape.format) {
      return existing_by_key;
    }
  }

  if (!IsPlausibleDecodedFableDepthTargetShape(shape)) {
    if (g_plume_resources.logged_rejected_decoded_depth_targets.size() < 8 &&
        g_plume_resources.logged_rejected_decoded_depth_targets
            .insert(guest_address)
            .second) {
      REXLOG_INFO(
          "Refii plume resource manager rejected decoded Fable depth target "
          "guest=0x{:08X} header=0x{:08X} size={}x{} pitch={} aligned={} "
          "msaa={} fmt={}",
          guest_address, header_address, shape.width, shape.height,
          shape.pitch_width, shape.aligned_width, shape.multisample_type,
          static_cast<uint32_t>(shape.format));
    }
    return nullptr;
  }

  auto existing_it = g_plume_resources.decoded_depth_targets.find(guest_address);
  if (existing_it != g_plume_resources.decoded_depth_targets.end()) {
    PlumeTrackedResource& existing = existing_it->second;
    if (existing.texture && existing.texture_view &&
        existing.texture_depth_target &&
        existing.texture_width == shape.width &&
        existing.texture_height == shape.height &&
        existing.texture_format == shape.format) {
      if (has_surface_key) {
        g_plume_resources.decoded_depth_target_keys[surface_key] =
            guest_address;
      }
      return &existing;
    }
    RetirePlumeResourceLocked(std::move(existing));
    g_plume_resources.decoded_depth_targets.erase(existing_it);
  }

  PlumeTrackedResource tracked;
  tracked.kind = RendererTrackedResourceKind::kDepthSurface;
  tracked.guest_address = guest_address;
  tracked.parent_address = header_address != guest_address ? header_address : 0;
  tracked.serial = ++g_plume_resources.resource_retire_epoch;
  tracked.texture_dimension = plume::RenderTextureDimension::TEXTURE_2D;
  tracked.texture_view_dimension = plume::RenderTextureViewDimension::TEXTURE_2D;
  tracked.texture_format = shape.format;
  tracked.texture_width = ClampGuestTextureExtent(shape.width);
  tracked.texture_height = ClampGuestTextureExtent(shape.height);
  tracked.texture_depth = 1;
  tracked.texture_mip_levels = 1;
  tracked.texture_depth_target = true;
  tracked.texture = device->createTexture(plume::RenderTextureDesc::DepthTarget(
      tracked.texture_width, tracked.texture_height, tracked.texture_format));
  if (tracked.texture) {
    char name[160] = {};
    std::snprintf(name, sizeof(name),
                  "Refii Fable decoded depth 0x%08X %ux%u",
                  guest_address, tracked.texture_width, tracked.texture_height);
    tracked.texture->setName(name);
    tracked.texture_view = tracked.texture->createTextureView(
        plume::RenderTextureViewDesc::Texture2D(tracked.texture_format));
  }
  if (!tracked.texture || !tracked.texture_view) {
    REXLOG_WARN(
        "Refii plume resource manager could not adopt decoded Fable depth "
        "target guest=0x{:08X} size={}x{} fmt={}",
        guest_address, shape.width, shape.height,
        static_cast<uint32_t>(shape.format));
    return nullptr;
  }

  auto [inserted_it, inserted] =
      g_plume_resources.decoded_depth_targets.emplace(guest_address,
                                                      std::move(tracked));
  (void)inserted;
  if (has_surface_key) {
    g_plume_resources.decoded_depth_target_keys[surface_key] = guest_address;
  }
  ClearFableFrameLookupCacheLocked();
  CacheTextureLookupForResourceLocked(inserted_it->second);
  REXLOG_INFO(
      "Refii plume resource manager adopted decoded Fable depth target "
      "guest=0x{:08X} header=0x{:08X} size={}x{} pitch={} aligned={} fmt={}",
      guest_address, header_address, inserted_it->second.texture_width,
      inserted_it->second.texture_height, shape.pitch_width,
      shape.aligned_width, static_cast<uint32_t>(shape.format));
  return &inserted_it->second;
}

void LogUnmatchedRenderTargetLocked(uint32_t render_target) {
  if (render_target == 0 ||
      g_plume_resources.logged_unmatched_render_targets.size() >= 8 ||
      !g_plume_resources.logged_unmatched_render_targets.insert(render_target)
           .second) {
    return;
  }

  std::string observer_match = " observer=none";
  const std::vector<RendererTrackedResourceSnapshot> observer_snapshots =
      GetRendererTrackedResourceSnapshots();
  for (const RendererTrackedResourceSnapshot& snapshot : observer_snapshots) {
    if (snapshot.guest_address != render_target &&
        snapshot.parent_address != render_target &&
        snapshot.buffer_object != render_target) {
      continue;
    }

    char buffer[224] = {};
    std::snprintf(buffer, sizeof(buffer),
                  " observer=%s guest=0x%08X parent=0x%08X object=0x%08X "
                  "size=%u meta=%u raw28/32/36/40/44/48=%08X/%08X/%08X/"
                  "%08X/%08X/%08X",
                  TrackedResourceKindName(snapshot.kind),
                  snapshot.guest_address, snapshot.parent_address,
                  snapshot.buffer_object, snapshot.size,
                  snapshot.stride_or_format, snapshot.raw_packed28,
                  snapshot.raw_packed32, snapshot.raw_packed36,
                  snapshot.raw_packed40, snapshot.raw_packed44,
                  snapshot.raw_packed48);
    observer_match = buffer;
    break;
  }

  std::string guest_words;
  std::string decoded_shape;
  if (observer_match == " observer=none") {
    const std::vector<uint32_t> words =
        GetRendererGuestWords(render_target, 16);
    if (!words.empty()) {
      guest_words = " words=";
      char buffer[16] = {};
      for (size_t i = 0; i < words.size(); ++i) {
        if (i != 0) {
          guest_words += "/";
        }
        std::snprintf(buffer, sizeof(buffer), "%08X", words[i]);
        guest_words += buffer;
      }
      FableSurfaceShape shape;
      if (DecodeFableSurfaceShapeFromWords(
              words, RendererTrackedResourceKind::kSurface, shape)) {
        char shape_buffer[160] = {};
        std::snprintf(shape_buffer, sizeof(shape_buffer),
                      " decoded=%ux%u pitch=%u aligned=%u msaa=%u fmt=%u",
                      shape.width, shape.height, shape.pitch_width,
                      shape.aligned_width, shape.multisample_type,
                      static_cast<uint32_t>(shape.format));
        decoded_shape = shape_buffer;
      }
    }
  }

  std::string surfaces;
  uint32_t surface_count = 0;
  for (const auto& [address, resource] : g_plume_resources.resources) {
    (void)address;
    if (!IsSurfaceResource(resource.kind) ||
        !resource.texture) {
      continue;
    }

    if (surface_count < 12) {
      char buffer[160] = {};
      std::snprintf(
          buffer, sizeof(buffer),
          " guest=0x%08X parent=0x%08X object=0x%08X %ux%u fmt=%u",
          resource.guest_address, resource.parent_address,
          resource.buffer_object, resource.texture_width,
          resource.texture_height,
          static_cast<uint32_t>(resource.texture_format));
      surfaces += buffer;
    }
    ++surface_count;
  }

  REXLOG_INFO(
      "Refii plume render target handle 0x{:08X} is not a tracked surface; "
      "live_surfaces={}{}{}{}{}",
      render_target, surface_count, observer_match, guest_words, decoded_shape,
      surfaces);
}

PlumeTrackedResource* FindTrackedResourceMutableLocked(
    uint32_t guest_address, RendererTrackedResourceKind kind) {
  return const_cast<PlumeTrackedResource*>(
      FindTrackedResourceLocked(guest_address, kind));
}

const PlumeTrackedResource* FindDecodedSampledImageLocked(
    uint32_t guest_address) {
  if (guest_address == 0) {
    return nullptr;
  }

  const auto color_it = g_plume_resources.decoded_color_targets.find(
      guest_address);
  if (color_it != g_plume_resources.decoded_color_targets.end()) {
    const PlumeTrackedResource& resource = color_it->second;
    if (resource.texture && resource.texture_view) {
      return &resource;
    }
  }

  const auto depth_it = g_plume_resources.decoded_depth_targets.find(
      guest_address);
  if (depth_it != g_plume_resources.decoded_depth_targets.end()) {
    const PlumeTrackedResource& resource = depth_it->second;
    if (resource.texture && resource.texture_view) {
      return &resource;
    }
  }

  return nullptr;
}

const PlumeTrackedResource* FindTrackedTextureByFetchConstantLocked(
    const RendererDrawEventSnapshot& draw_event, uint32_t slot) {
  constexpr uint32_t kTextureFetchConstantDwords = 6;
  const uint32_t fetch_dword = slot * kTextureFetchConstantDwords;
  if (slot >= 32u ||
      fetch_dword + kTextureFetchConstantDwords >
          draw_event.fetch_constants.size()) {
    return nullptr;
  }

  const uint32_t dword0 = draw_event.fetch_constants[fetch_dword + 0u];
  const uint32_t dword1 = draw_event.fetch_constants[fetch_dword + 1u];
  const uint32_t dword2 = draw_event.fetch_constants[fetch_dword + 2u];
  const uint32_t dword3 = draw_event.fetch_constants[fetch_dword + 3u];
  const uint32_t dword4 = draw_event.fetch_constants[fetch_dword + 4u];
  const uint32_t dword5 = draw_event.fetch_constants[fetch_dword + 5u];
  if ((dword0 | dword1 | dword2 | dword3 | dword4 | dword5) == 0) {
    return nullptr;
  }

  const FableTextureFetchKey key{{dword0, dword1, dword2,
                                  dword3, dword4, dword5}};

  namespace xenos = rex::graphics::xenos;
  xenos::xe_gpu_texture_fetch_t fetch = {};
  fetch.dword_0 = dword0;
  fetch.dword_1 = dword1;
  fetch.dword_2 = dword2;
  fetch.dword_3 = dword3;
  fetch.dword_4 = dword4;
  fetch.dword_5 = dword5;
  const bool valid_texture_fetch =
      fetch.type == xenos::FetchConstantType::kTexture;
  const uint64_t lookup_serial =
      g_plume_resources.active_lookup_resource_serial;
  const FableTextureFetchLookupKey serial_key{key, lookup_serial};
  if (!valid_texture_fetch) {
    if (lookup_serial != 0) {
      g_plume_resources.texture_fetch_serial_lookup_cache.emplace(serial_key,
                                                                 nullptr);
    } else {
      g_plume_resources.texture_fetch_lookup_cache.emplace(key, nullptr);
    }
    return nullptr;
  }
  const uint32_t fetch_base = fetch.base_address << 12;
  const uint32_t fetch_mips = fetch.mip_address << 12;
  const uint32_t fetch_pitch = fetch.pitch << 5;
  const uint32_t fetch_format = static_cast<uint32_t>(fetch.format);
  const uint32_t fetch_dimension = static_cast<uint32_t>(fetch.dimension);

  if (lookup_serial != 0) {
    const auto cached_it =
        g_plume_resources.texture_fetch_serial_lookup_cache.find(serial_key);
    if (cached_it !=
        g_plume_resources.texture_fetch_serial_lookup_cache.end()) {
      return cached_it->second;
    }
  }
  if (lookup_serial == 0 &&
      g_plume_resources.texture_fetch_frame_misses.find(key) !=
          g_plume_resources.texture_fetch_frame_misses.end()) {
    g_plume_resources.texture_fetch_lookup_cache.emplace(key, nullptr);
    return nullptr;
  }

  if (valid_texture_fetch) {
    if (const FableRenderTargetResolveAlias* alias =
            FindFableResolveAliasLocked(fetch_base)) {
      ScopedPlumeResourceLookupSerial scoped_alias_lookup(alias->serial);
      const PlumeTrackedResource* resolved =
          alias->depth ? FindTrackedDepthTargetLocked(alias->source_address)
                       : FindTrackedColorRenderTargetLocked(
                             alias->source_address);
      if (resolved && resolved->texture && resolved->texture_view &&
          FableResolveAliasMatchesFetch(*resolved, fetch)) {
        ++g_plume_resources.resolve_texture_alias_hits_this_frame;
        if (lookup_serial != 0) {
          g_plume_resources.texture_fetch_serial_lookup_cache.emplace(
              serial_key, resolved);
        } else {
          g_plume_resources.texture_fetch_lookup_cache.emplace(key, resolved);
        }
        return resolved;
      }
    }
  }

  if (valid_texture_fetch && lookup_serial == 0) {
    const auto exact_it = g_plume_resources.texture_fetch_exact_index.find(key);
    if (exact_it != g_plume_resources.texture_fetch_exact_index.end()) {
      g_plume_resources.texture_fetch_lookup_cache.emplace(key,
                                                           exact_it->second);
      return exact_it->second;
    }

    const auto cached_it =
        g_plume_resources.texture_fetch_lookup_cache.find(key);
    if (cached_it != g_plume_resources.texture_fetch_lookup_cache.end()) {
      return cached_it->second;
    }

    const FableTextureShapeKey shape_key{
        fetch_base, fetch_mips, fetch_pitch, fetch_format, fetch_dimension};
    const auto shape_it =
        g_plume_resources.texture_fetch_shape_index.find(shape_key);
    if (shape_it != g_plume_resources.texture_fetch_shape_index.end()) {
      g_plume_resources.texture_fetch_lookup_cache.emplace(key,
                                                           shape_it->second);
      return shape_it->second;
    }
  }

  const PlumeTrackedResource* best = nullptr;
  bool exact_match_found = false;
  const auto consider_resource = [&](const PlumeTrackedResource& resource,
                                     uint64_t valid_until_serial) {
    if (!IsImageResource(resource.kind) ||
        !resource.texture || !resource.texture_view) {
      return;
    }

    if (lookup_serial != 0) {
      if (resource.serial > lookup_serial) {
        return;
      }
      if (valid_until_serial != 0 && lookup_serial > valid_until_serial) {
        return;
      }
    }

    if (resource.texture_fetch_dwords[0] == dword0 &&
        resource.texture_fetch_dwords[1] == dword1 &&
        resource.texture_fetch_dwords[2] == dword2 &&
        resource.texture_fetch_dwords[3] == dword3 &&
        resource.texture_fetch_dwords[4] == dword4 &&
        resource.texture_fetch_dwords[5] == dword5) {
      if (!best || resource.serial > best->serial) {
        best = &resource;
      }
      exact_match_found = true;
      return;
    }

    if (!valid_texture_fetch || exact_match_found) {
      return;
    }

    if (resource.texture_guest_base == fetch_base &&
        resource.texture_guest_mips == fetch_mips &&
        resource.texture_guest_pitch == fetch_pitch &&
        resource.texture_guest_format == fetch_format &&
        resource.texture_guest_dimension == fetch_dimension) {
      if (!best || resource.serial > best->serial) {
        best = &resource;
      }
    }
  };

  for (const auto& [address, resource] : g_plume_resources.resources) {
    (void)address;
    consider_resource(resource, 0);
  }
  if (lookup_serial != 0) {
    for (const PlumeRetiredResource& retired :
         g_plume_resources.retired_resources) {
      consider_resource(retired.resource, retired.valid_until_serial);
    }
  }

  if (!best && lookup_serial != 0) {
    const auto exact_it = g_plume_resources.texture_fetch_exact_index.find(key);
    if (exact_it != g_plume_resources.texture_fetch_exact_index.end()) {
      best = exact_it->second;
    }
    if (!best) {
      const FableTextureShapeKey shape_key{
          fetch_base, fetch_mips, fetch_pitch, fetch_format, fetch_dimension};
      const auto shape_it =
          g_plume_resources.texture_fetch_shape_index.find(shape_key);
      if (shape_it != g_plume_resources.texture_fetch_shape_index.end()) {
        best = shape_it->second;
      }
    }
  }

  if (lookup_serial == 0) {
    g_plume_resources.texture_fetch_lookup_cache.emplace(key, best);
  } else {
    g_plume_resources.texture_fetch_serial_lookup_cache.emplace(serial_key,
                                                               best);
  }
  if (!best && lookup_serial == 0) {
    g_plume_resources.texture_fetch_frame_misses.insert(key);
  }
  return best;
}

bool DrawEventHasTextureFetchConstant(const RendererDrawEventSnapshot& draw_event,
                                      uint32_t slot);

bool TrackedTexturesAliasFableMemory(const PlumeTrackedResource& sampled,
                                     const PlumeTrackedResource& target);

const PlumeTrackedResource* FindLiveRenderTargetAliasForSampledTextureLocked(
    const PlumeTrackedResource* sampled,
    bool allow_depth) {
  if (!sampled || !sampled->texture || !sampled->texture_view) {
    return nullptr;
  }
  if (sampled->texture_render_target ||
      (allow_depth && sampled->texture_depth_target)) {
    return sampled;
  }

  const uint64_t lookup_serial =
      g_plume_resources.active_lookup_resource_serial;
  const auto shape_matches = [sampled](const PlumeTrackedResource& target) {
    if (sampled->texture_width != 0 && target.texture_width != 0 &&
        sampled->texture_width != target.texture_width) {
      return false;
    }
    if (sampled->texture_height != 0 && target.texture_height != 0 &&
        sampled->texture_height != target.texture_height) {
      return false;
    }
    if (sampled->texture_format != plume::RenderFormat::UNKNOWN &&
        target.texture_format != plume::RenderFormat::UNKNOWN &&
        sampled->texture_format != target.texture_format) {
      return false;
    }
    return true;
  };

  const PlumeTrackedResource* best = nullptr;
  const auto consider = [&](const PlumeTrackedResource& candidate) {
    if (!candidate.texture || !candidate.texture_view) {
      return;
    }
    if (!candidate.texture_render_target &&
        !(allow_depth && candidate.texture_depth_target)) {
      return;
    }
    if (lookup_serial != 0 && candidate.serial > lookup_serial) {
      return;
    }
    if (!shape_matches(candidate) ||
        !TrackedTexturesAliasFableMemory(*sampled, candidate)) {
      return;
    }
    if (!best || candidate.serial > best->serial) {
      best = &candidate;
    }
  };

  for (const auto& [address, resource] : g_plume_resources.resources) {
    (void)address;
    consider(resource);
  }
  for (const auto& [address, resource] :
       g_plume_resources.decoded_color_targets) {
    (void)address;
    consider(resource);
  }
  if (allow_depth) {
    for (const auto& [address, resource] :
         g_plume_resources.decoded_depth_targets) {
      (void)address;
      consider(resource);
    }
  }
  return best;
}

const PlumeTrackedResource* FindTrackedSampledImageByObjectLocked(
    uint32_t guest_texture) {
  const PlumeTrackedResource* texture = FindTrackedResourceLocked(
      guest_texture, RendererTrackedResourceKind::kTexture);
  if (!texture) {
    texture = FindCurrentTrackedResourceDirectLocked(
        guest_texture, RendererTrackedResourceKind::kTexture);
  }
  if (!texture || !texture->texture || !texture->texture_view) {
    texture = FindTrackedResourceLocked(guest_texture,
                                        RendererTrackedResourceKind::kSurface);
  }
  if (!texture) {
    texture = FindCurrentTrackedResourceDirectLocked(
        guest_texture, RendererTrackedResourceKind::kSurface);
  }
  if (texture && texture->kind == RendererTrackedResourceKind::kSurface &&
      texture->parent_address != 0) {
    const PlumeTrackedResource* parent_texture = FindTrackedResourceDirectLocked(
        texture->parent_address, RendererTrackedResourceKind::kTexture);
    if (!parent_texture) {
      parent_texture = FindCurrentTrackedResourceDirectLocked(
          texture->parent_address, RendererTrackedResourceKind::kTexture);
    }
    if (parent_texture && parent_texture->texture &&
        parent_texture->texture_view &&
        !plume::RenderFormatIsDepth(parent_texture->texture_format)) {
      return parent_texture;
    }
    const PlumeTrackedResource* parent_surface = FindTrackedResourceDirectLocked(
        texture->parent_address, RendererTrackedResourceKind::kSurface);
    if (!parent_surface) {
      parent_surface = FindCurrentTrackedResourceDirectLocked(
          texture->parent_address, RendererTrackedResourceKind::kSurface);
    }
    if (parent_surface && parent_surface->texture &&
        parent_surface->texture_view && parent_surface->texture_render_target &&
        !plume::RenderFormatIsDepth(parent_surface->texture_format)) {
      return parent_surface;
    }
  }
  if (!texture || !texture->texture || !texture->texture_view) {
    texture = FindDecodedSampledImageLocked(guest_texture);
  }
  if (texture && texture->texture && texture->texture_view &&
      !plume::RenderFormatIsDepth(texture->texture_format)) {
    return texture;
  }
  return nullptr;
}

const PlumeTrackedResource* FindTrackedSampledImageByObjectAllowDepthLocked(
    uint32_t guest_texture) {
  const PlumeTrackedResource* texture = FindTrackedResourceLocked(
      guest_texture, RendererTrackedResourceKind::kTexture);
  if (!texture) {
    texture = FindCurrentTrackedResourceDirectLocked(
        guest_texture, RendererTrackedResourceKind::kTexture);
  }
  if (!texture || !texture->texture || !texture->texture_view) {
    texture = FindTrackedResourceLocked(guest_texture,
                                        RendererTrackedResourceKind::kSurface);
  }
  if (!texture) {
    texture = FindCurrentTrackedResourceDirectLocked(
        guest_texture, RendererTrackedResourceKind::kSurface);
  }
  if (!texture || !texture->texture || !texture->texture_view) {
    texture = FindTrackedResourceLocked(
        guest_texture, RendererTrackedResourceKind::kDepthSurface);
  }
  if (!texture) {
    texture = FindCurrentTrackedResourceDirectLocked(
        guest_texture, RendererTrackedResourceKind::kDepthSurface);
  }
  if (texture && IsSurfaceResource(texture->kind) &&
      texture->parent_address != 0) {
    const PlumeTrackedResource* parent_texture = FindTrackedResourceDirectLocked(
        texture->parent_address, RendererTrackedResourceKind::kTexture);
    if (!parent_texture) {
      parent_texture = FindCurrentTrackedResourceDirectLocked(
          texture->parent_address, RendererTrackedResourceKind::kTexture);
    }
    if (parent_texture && parent_texture->texture &&
        parent_texture->texture_view) {
      return parent_texture;
    }
    const PlumeTrackedResource* parent_surface = FindTrackedResourceDirectLocked(
        texture->parent_address, RendererTrackedResourceKind::kSurface);
    if (!parent_surface) {
      parent_surface = FindCurrentTrackedResourceDirectLocked(
          texture->parent_address, RendererTrackedResourceKind::kSurface);
    }
    if (parent_surface && parent_surface->texture &&
        parent_surface->texture_view) {
      return parent_surface;
    }
    const PlumeTrackedResource* parent_depth = FindTrackedResourceDirectLocked(
        texture->parent_address, RendererTrackedResourceKind::kDepthSurface);
    if (!parent_depth) {
      parent_depth = FindCurrentTrackedResourceDirectLocked(
          texture->parent_address, RendererTrackedResourceKind::kDepthSurface);
    }
    if (parent_depth && parent_depth->texture && parent_depth->texture_view) {
      return parent_depth;
    }
  }
  if (!texture || !texture->texture || !texture->texture_view) {
    texture = FindDecodedSampledImageLocked(guest_texture);
  }
  return texture && texture->texture && texture->texture_view ? texture
                                                              : nullptr;
}

const PlumeTrackedResource* FindTrackedSampledImageLocked(
    uint32_t guest_texture, const RendererDrawEventSnapshot& draw_event,
    uint32_t slot) {
  if (guest_texture == 0 && !DrawEventHasTextureFetchConstant(draw_event, slot)) {
    return nullptr;
  }

  const PlumeTrackedResource* object_texture =
      FindTrackedSampledImageByObjectLocked(guest_texture);
  const PlumeTrackedResource* fetch_texture =
      FindTrackedTextureByFetchConstantLocked(draw_event, slot);
  if (fetch_texture && (!fetch_texture->texture ||
                        !fetch_texture->texture_view ||
                        plume::RenderFormatIsDepth(fetch_texture->texture_format) ||
                        (draw_event.resource_serial != 0 &&
                         fetch_texture->serial > draw_event.resource_serial))) {
    fetch_texture = nullptr;
  }

  if (!object_texture) {
    if (const PlumeTrackedResource* live_target =
            FindLiveRenderTargetAliasForSampledTextureLocked(fetch_texture,
                                                             false)) {
      return live_target;
    }
    return fetch_texture;
  }
  if (!fetch_texture) {
    if (const PlumeTrackedResource* live_target =
            FindLiveRenderTargetAliasForSampledTextureLocked(object_texture,
                                                             false)) {
      return live_target;
    }
    return object_texture;
  }

  if (fetch_texture == object_texture ||
      fetch_texture->texture.get() == object_texture->texture.get()) {
    return object_texture;
  }

  const bool fetch_is_rendered =
      fetch_texture->texture_render_target || fetch_texture->texture_depth_target;
  const bool object_is_rendered =
      object_texture->texture_render_target || object_texture->texture_depth_target;
  if (fetch_is_rendered && !object_is_rendered) {
    return fetch_texture;
  }
  if (fetch_texture->texture_guest_base != 0 &&
      object_texture->texture_guest_base != fetch_texture->texture_guest_base) {
    return fetch_texture;
  }
  if (fetch_texture->serial > object_texture->serial &&
      (fetch_texture->texture_width != object_texture->texture_width ||
       fetch_texture->texture_height != object_texture->texture_height ||
       fetch_texture->texture_format != object_texture->texture_format)) {
    if (const PlumeTrackedResource* live_target =
            FindLiveRenderTargetAliasForSampledTextureLocked(fetch_texture,
                                                             false)) {
      return live_target;
    }
    return fetch_texture;
  }
  if (const PlumeTrackedResource* live_target =
          FindLiveRenderTargetAliasForSampledTextureLocked(object_texture,
                                                           false)) {
    return live_target;
  }
  return object_texture;
}

const PlumeTrackedResource* FindTrackedSampledImageAllowDepthLocked(
    uint32_t guest_texture, const RendererDrawEventSnapshot& draw_event,
    uint32_t slot) {
  if (guest_texture == 0 && !DrawEventHasTextureFetchConstant(draw_event, slot)) {
    return nullptr;
  }

  const PlumeTrackedResource* object_texture =
      FindTrackedSampledImageByObjectAllowDepthLocked(guest_texture);
  const PlumeTrackedResource* fetch_texture =
      FindTrackedTextureByFetchConstantLocked(draw_event, slot);
  if (fetch_texture && (!fetch_texture->texture ||
                        !fetch_texture->texture_view ||
                        (draw_event.resource_serial != 0 &&
                         fetch_texture->serial > draw_event.resource_serial))) {
    fetch_texture = nullptr;
  }

  if (!object_texture) {
    if (const PlumeTrackedResource* live_target =
            FindLiveRenderTargetAliasForSampledTextureLocked(fetch_texture,
                                                             true)) {
      return live_target;
    }
    return fetch_texture;
  }
  if (!fetch_texture) {
    if (const PlumeTrackedResource* live_target =
            FindLiveRenderTargetAliasForSampledTextureLocked(object_texture,
                                                             true)) {
      return live_target;
    }
    return object_texture;
  }

  if (fetch_texture == object_texture ||
      fetch_texture->texture.get() == object_texture->texture.get()) {
    return object_texture;
  }

  const bool fetch_is_rendered =
      fetch_texture->texture_render_target || fetch_texture->texture_depth_target;
  const bool object_is_rendered =
      object_texture->texture_render_target || object_texture->texture_depth_target;
  if (fetch_is_rendered && !object_is_rendered) {
    return fetch_texture;
  }
  if (fetch_texture->texture_guest_base != 0 &&
      object_texture->texture_guest_base != fetch_texture->texture_guest_base) {
    return fetch_texture;
  }
  if (fetch_texture->serial > object_texture->serial &&
      (fetch_texture->texture_width != object_texture->texture_width ||
       fetch_texture->texture_height != object_texture->texture_height ||
       fetch_texture->texture_format != object_texture->texture_format)) {
    if (const PlumeTrackedResource* live_target =
            FindLiveRenderTargetAliasForSampledTextureLocked(fetch_texture,
                                                             true)) {
      return live_target;
    }
    return fetch_texture;
  }
  if (const PlumeTrackedResource* live_target =
          FindLiveRenderTargetAliasForSampledTextureLocked(object_texture,
                                                           true)) {
    return live_target;
  }
  return object_texture;
}

const PlumeTrackedResource* FindFableDepthSampleSourceLocked(
    const RendererDrawEventSnapshot& draw_event,
    bool* used_active_depth_fallback = nullptr) {
  constexpr uint32_t kFableDepthTextureSamplerSlot = 13;
  if (used_active_depth_fallback) {
    *used_active_depth_fallback = false;
  }
  auto is_usable_depth_sample_source =
      [](const PlumeTrackedResource* source) {
        return source && source->texture && source->texture_view &&
               source->texture_width != 0 && source->texture_height != 0 &&
               source->texture_depth <= 1;
      };
  const uint32_t guest_texture =
      kFableDepthTextureSamplerSlot < std::size(draw_event.textures)
          ? draw_event.textures[kFableDepthTextureSamplerSlot]
          : 0;
  const PlumeTrackedResource* source = FindTrackedSampledImageAllowDepthLocked(
      guest_texture, draw_event, kFableDepthTextureSamplerSlot);
  if (is_usable_depth_sample_source(source)) {
    return source;
  }

  source =
      FindTrackedTextureByFetchConstantLocked(draw_event,
                                              kFableDepthTextureSamplerSlot);
  if (is_usable_depth_sample_source(source)) {
    return source;
  }

  source = FindTrackedDepthTargetLocked(draw_event.depth_stencil);
  if (is_usable_depth_sample_source(source)) {
    if (used_active_depth_fallback) {
      *used_active_depth_fallback = true;
    }
    return source;
  }

  return nullptr;
}

bool DrawEventHasTextureFetchConstant(const RendererDrawEventSnapshot& draw_event,
                                      uint32_t slot) {
  constexpr uint32_t kTextureFetchConstantDwords = 6;
  const uint32_t fetch_dword = slot * kTextureFetchConstantDwords;
  if (slot >= 32u ||
      fetch_dword + kTextureFetchConstantDwords >
          draw_event.fetch_constants.size()) {
    return false;
  }

  for (uint32_t i = 0; i < kTextureFetchConstantDwords; ++i) {
    if (draw_event.fetch_constants[fetch_dword + i] != 0) {
      namespace xenos = rex::graphics::xenos;
      xenos::xe_gpu_texture_fetch_t fetch = {};
      fetch.dword_0 = draw_event.fetch_constants[fetch_dword + 0u];
      fetch.dword_1 = draw_event.fetch_constants[fetch_dword + 1u];
      fetch.dword_2 = draw_event.fetch_constants[fetch_dword + 2u];
      fetch.dword_3 = draw_event.fetch_constants[fetch_dword + 3u];
      fetch.dword_4 = draw_event.fetch_constants[fetch_dword + 4u];
      fetch.dword_5 = draw_event.fetch_constants[fetch_dword + 5u];
      return fetch.type == xenos::FetchConstantType::kTexture;
    }
  }
  return false;
}

void AccumulateTextureBindingStatsLocked(
    const RendererDrawEventSnapshot& draw_event,
    PlumeDrawReplayStats& stats) {
  for (uint32_t slot = 0; slot < kMaxFableTextureSlots; ++slot) {
    const uint32_t guest_texture =
        slot < std::size(draw_event.textures) ? draw_event.textures[slot] : 0;
    const bool has_fetch_constant =
        DrawEventHasTextureFetchConstant(draw_event, slot);
    if (guest_texture == 0 && !has_fetch_constant) {
      continue;
    }

    const PlumeTrackedResource* texture =
        FindTrackedSampledImageByObjectLocked(guest_texture);
    if (texture && texture->texture && texture->texture_view &&
        !plume::RenderFormatIsDepth(texture->texture_format)) {
      ++stats.texture_object_bindings;
      continue;
    }

    if (has_fetch_constant) {
      ++stats.texture_fetch_bindings;
    } else {
      ++stats.missing_texture_bindings;
    }
  }
}

bool ReadFileBytes(const std::filesystem::path& path,
                   std::vector<uint8_t>& bytes) {
  std::error_code ec;
  const auto size = std::filesystem::file_size(path, ec);
  if (ec || size == static_cast<uintmax_t>(-1)) {
    return false;
  }

  bytes.resize(static_cast<size_t>(size));
  std::ifstream file(path, std::ios::binary);
  if (!file) {
    bytes.clear();
    return false;
  }

  file.read(reinterpret_cast<char*>(bytes.data()),
            static_cast<std::streamsize>(bytes.size()));
  if (!file && !file.eof()) {
    bytes.clear();
    return false;
  }
  return true;
}

bool HasAnyBoundVertexBufferLocked(const RendererDrawEventSnapshot& draw_event) {
  for (const RendererStreamBindingSnapshot& stream : draw_event.streams) {
    if (HasTrackedResourceLocked(stream.buffer,
                                 RendererTrackedResourceKind::kVertexBuffer)) {
      return true;
    }
  }
  return false;
}

bool HasUsableVertexInputLocked(const RendererDrawEventSnapshot& draw_event,
                                bool has_vertex_buffer,
                                bool has_vertex_shader) {
  if (const PlumeTrackedResource* declaration = FindTrackedResourceLocked(
          draw_event.vertex_declaration,
          RendererTrackedResourceKind::kVertexDeclaration);
      declaration && !declaration->input_elements.empty()) {
    return true;
  }

  return has_vertex_buffer && has_vertex_shader;
}

plume::RenderPrimitiveTopology ConvertPrimitiveTopology(uint32_t primitive_type) {
  switch (primitive_type) {
    case 1:
      return plume::RenderPrimitiveTopology::POINT_LIST;
    case 2:
      return plume::RenderPrimitiveTopology::LINE_LIST;
    case 3:
      return plume::RenderPrimitiveTopology::LINE_STRIP;
    case 4:
    case 8:
    case 13:
      return plume::RenderPrimitiveTopology::TRIANGLE_LIST;
    case 5:
      return plume::RenderPrimitiveTopology::TRIANGLE_FAN;
    case 6:
      return plume::RenderPrimitiveTopology::TRIANGLE_STRIP;
    default:
      return plume::RenderPrimitiveTopology::UNKNOWN;
  }
}

uint32_t DrawVertexOrIndexCount(uint32_t primitive_type,
                                uint32_t captured_count) {
  switch (primitive_type) {
    case 1:
    case 2:
    case 3:
    case 4:
    case 8:
    case 5:
    case 6:
    case 13:
      return captured_count;
    default:
      return 0;
  }
}

bool IsFablePrimitivePolygonal(uint32_t primitive_type) {
  switch (primitive_type) {
    case 4:   // Triangle list.
    case 5:   // Triangle fan.
    case 6:   // Triangle strip.
    case 7:   // Triangle with edge/w flags.
    case 13:  // Quad list.
    case 14:  // Quad strip.
    case 15:  // Polygon.
      return true;
    default:
      return false;
  }
}

bool IsFableRasterizationPotentiallyDone(
    const RendererDrawEventSnapshot& draw_event) {
  if (!draw_event.pm4_registers_valid) {
    return true;
  }

  const uint32_t edram_mode = draw_event.rb_modecontrol & 0x7u;
  if (edram_mode != 4u && edram_mode != 5u) {
    return false;
  }
  if ((draw_event.rb_surface_info & 0x3FFFu) == 0) {
    return false;
  }
  if (IsFablePrimitivePolygonal(draw_event.draw.primitive_type) &&
      (draw_event.pa_su_sc_mode_cntl & 0x3u) == 0x3u) {
    return false;
  }
  return true;
}

uint64_t AlignUp(uint64_t value, uint64_t alignment) {
  return alignment == 0 ? value : ((value + alignment - 1u) / alignment) *
                                  alignment;
}

bool IsPowerOfTwo(uint32_t value) {
  return value != 0 && (value & (value - 1u)) == 0;
}

bool EnsureFablePipelineLayoutLocked(plume::RenderDevice* device) {
  if (!device) {
    return false;
  }
  if (g_plume_resources.pipeline_layout &&
      g_plume_resources.texture_descriptor_sets[0] &&
      g_plume_resources.texture_descriptor_sets[1] &&
      g_plume_resources.texture_descriptor_sets[2] &&
      g_plume_resources.sampler_descriptor_set &&
      g_plume_resources.zero_constants &&
      g_plume_resources.default_vertex_input_buffer) {
    return true;
  }

  plume::RenderDescriptorRange texture_range(
      plume::RenderDescriptorRangeType::TEXTURE, 0, kFableDescriptorCount);
  plume::RenderDescriptorRange sampler_range(
      plume::RenderDescriptorRangeType::SAMPLER, 0, kFableDescriptorCount);
  plume::RenderDescriptorSetDesc descriptor_sets[4] = {
      plume::RenderDescriptorSetDesc(&texture_range, 1, true,
                                     kFableDescriptorCount),
      plume::RenderDescriptorSetDesc(&texture_range, 1, true,
                                     kFableDescriptorCount),
      plume::RenderDescriptorSetDesc(&texture_range, 1, true,
                                     kFableDescriptorCount),
      plume::RenderDescriptorSetDesc(&sampler_range, 1, true,
                                     kFableDescriptorCount),
  };
  plume::RenderPushConstantRange push_constant_range(
      0, 4, 0, sizeof(PlumeFablePushConstants),
      plume::RenderShaderStageFlag::VERTEX | plume::RenderShaderStageFlag::PIXEL);
  plume::RenderPipelineLayoutDesc pipeline_layout_desc;
  pipeline_layout_desc.pushConstantRanges = &push_constant_range;
  pipeline_layout_desc.pushConstantRangesCount = 1;
  pipeline_layout_desc.descriptorSetDescs = descriptor_sets;
  pipeline_layout_desc.descriptorSetDescsCount = 4;
  pipeline_layout_desc.allowInputLayout = true;

  g_plume_resources.pipeline_layout =
      device->createPipelineLayout(pipeline_layout_desc);
  if (!g_plume_resources.pipeline_layout) {
    return false;
  }

  for (size_t i = 0; i < std::size(g_plume_resources.texture_descriptor_sets);
       ++i) {
    g_plume_resources.texture_descriptor_sets[i] =
        device->createDescriptorSet(descriptor_sets[i]);
  }
  g_plume_resources.sampler_descriptor_set =
      device->createDescriptorSet(descriptor_sets[3]);
  if (!g_plume_resources.texture_descriptor_sets[0] ||
      !g_plume_resources.texture_descriptor_sets[1] ||
      !g_plume_resources.texture_descriptor_sets[2] ||
      !g_plume_resources.sampler_descriptor_set) {
    return false;
  }

  g_plume_resources.null_textures[0] = device->createTexture(
      plume::RenderTextureDesc::Texture2D(1, 1, 1,
                                          plume::RenderFormat::R8G8B8A8_UNORM));
  g_plume_resources.null_textures[1] = device->createTexture(
      plume::RenderTextureDesc::Texture3D(1, 1, 1, 1,
                                          plume::RenderFormat::R8G8B8A8_UNORM));
  plume::RenderTextureDesc cube_desc = plume::RenderTextureDesc::Texture(
      plume::RenderTextureDimension::TEXTURE_2D, 1, 1, 1, 1, 6,
      plume::RenderFormat::R8G8B8A8_UNORM, plume::RenderTextureFlag::CUBE);
  g_plume_resources.null_textures[2] = device->createTexture(cube_desc);
  if (!g_plume_resources.null_textures[0] ||
      !g_plume_resources.null_textures[1] ||
      !g_plume_resources.null_textures[2]) {
    return false;
  }

  g_plume_resources.null_texture_views[0] =
      g_plume_resources.null_textures[0]->createTextureView(
          plume::RenderTextureViewDesc::Texture2D(
              plume::RenderFormat::R8G8B8A8_UNORM));
  g_plume_resources.null_texture_views[1] =
      g_plume_resources.null_textures[1]->createTextureView(
          plume::RenderTextureViewDesc::Texture3D(
              plume::RenderFormat::R8G8B8A8_UNORM));
  g_plume_resources.null_texture_views[2] =
      g_plume_resources.null_textures[2]->createTextureView(
          plume::RenderTextureViewDesc::TextureCube(
              plume::RenderFormat::R8G8B8A8_UNORM));
  if (!g_plume_resources.null_texture_views[0] ||
      !g_plume_resources.null_texture_views[1] ||
      !g_plume_resources.null_texture_views[2]) {
    return false;
  }

  for (uint32_t i = 0; i < kFableDescriptorCount; ++i) {
    for (size_t set = 0; set < 3; ++set) {
      g_plume_resources.texture_descriptor_sets[set]->setTexture(
          i, g_plume_resources.null_textures[set].get(),
          plume::RenderTextureLayout::SHADER_READ,
          g_plume_resources.null_texture_views[set].get());
    }
  }

  constexpr uint32_t kFallbackTexturePlacementBytes = 512;
  const uint32_t upload_sizes[3] = {
      kFallbackTexturePlacementBytes,
      kFallbackTexturePlacementBytes,
      kFallbackTexturePlacementBytes * 6u,
  };
  for (size_t texture_index = 0;
       texture_index < std::size(g_plume_resources.null_texture_uploads);
       ++texture_index) {
    g_plume_resources.null_texture_uploads[texture_index] =
        device->createBuffer(
            plume::RenderBufferDesc::UploadBuffer(upload_sizes[texture_index]));
    if (!g_plume_resources.null_texture_uploads[texture_index]) {
      return false;
    }
    if (void* dst =
            g_plume_resources.null_texture_uploads[texture_index]->map()) {
      std::memset(dst, 0xFF, upload_sizes[texture_index]);
      plume::RenderRange written_range(0, upload_sizes[texture_index]);
      g_plume_resources.null_texture_uploads[texture_index]->unmap(
          0, &written_range);
    }
  }
  g_plume_resources.fallback_textures_upload_pending = true;

  plume::RenderSamplerDesc sampler_desc;
  g_plume_resources.null_sampler = device->createSampler(sampler_desc);
  if (!g_plume_resources.null_sampler) {
    return false;
  }
  for (uint32_t i = 0; i < kFableDescriptorCount; ++i) {
    g_plume_resources.sampler_descriptor_set->setSampler(
        i, g_plume_resources.null_sampler.get());
  }

  g_plume_resources.zero_constants = device->createBuffer(
      plume::RenderBufferDesc::UploadBuffer(
          4096, plume::RenderBufferFlag::CONSTANT |
                    plume::RenderBufferFlag::DEVICE_ADDRESSABLE));
  if (!g_plume_resources.zero_constants) {
    return false;
  }
  if (void* dst = g_plume_resources.zero_constants->map()) {
    std::memset(dst, 0, 4096);
    plume::RenderRange written_range(0, 4096);
    g_plume_resources.zero_constants->unmap(0, &written_range);
  }

  g_plume_resources.default_vertex_input_buffer =
      device->createBuffer(plume::RenderBufferDesc::VertexBuffer(
          kDefaultVertexInputBufferSize, plume::RenderHeapType::UPLOAD));
  if (!g_plume_resources.default_vertex_input_buffer) {
    return false;
  }
  g_plume_resources.default_vertex_input_buffer->setName(
      "Refii Fable default vertex input");
  if (void* dst = g_plume_resources.default_vertex_input_buffer->map()) {
    std::memset(dst, 0, kDefaultVertexInputBufferSize);
    plume::RenderRange written_range(0, kDefaultVertexInputBufferSize);
    g_plume_resources.default_vertex_input_buffer->unmap(0, &written_range);
  }

  if (!g_plume_resources.logged_pipeline_layout_ready) {
    REXLOG_INFO(
        "Refii plume resource manager created fallback Fable descriptor "
        "sets, constants, and pipeline layout");
    g_plume_resources.logged_pipeline_layout_ready = true;
  }
  InvalidateFableDescriptorCacheLocked();
  return true;
}

bool CreatePresentBlitShaderForFormat(
    plume::RenderDevice* device, plume::RenderShaderFormat shader_format,
    bool vertex_shader, std::unique_ptr<plume::RenderShader>& shader) {
  switch (shader_format) {
    case plume::RenderShaderFormat::SPIRV:
      shader = device->createShader(
          vertex_shader ? plume_present_blit_vsBlobSPIRV
                        : plume_present_blit_psBlobSPIRV,
          vertex_shader ? plume_present_blit_vsBlobSPIRV_size
                        : plume_present_blit_psBlobSPIRV_size,
          vertex_shader ? "VSMain" : "PSMain", shader_format);
      return shader != nullptr;
#if defined(_WIN64)
    case plume::RenderShaderFormat::DXIL:
      shader = device->createShader(
          vertex_shader ? plume_present_blit_vsBlobDXIL
                        : plume_present_blit_psBlobDXIL,
          vertex_shader ? plume_present_blit_vsBlobDXIL_size
                        : plume_present_blit_psBlobDXIL_size,
          vertex_shader ? "VSMain" : "PSMain", shader_format);
      return shader != nullptr;
#endif
    default:
      return false;
  }
}

bool EnsureRectangleListGeometryShaderLocked(
    plume::RenderDevice* device, plume::RenderShaderFormat shader_format) {
  if (!device || shader_format == plume::RenderShaderFormat::UNKNOWN) {
    return false;
  }
  if (g_plume_resources.rectangle_list_geometry_shader &&
      g_plume_resources.rectangle_list_shader_format == shader_format) {
    return true;
  }

  g_plume_resources.rectangle_list_shader_format = shader_format;
  g_plume_resources.rectangle_list_geometry_shader.reset();
  switch (shader_format) {
    case plume::RenderShaderFormat::SPIRV:
      g_plume_resources.rectangle_list_geometry_shader = device->createShader(
          plume_rectangle_list_gsBlobSPIRV,
          plume_rectangle_list_gsBlobSPIRV_size, "GSMain", shader_format);
      break;
#if defined(_WIN64)
    case plume::RenderShaderFormat::DXIL:
      g_plume_resources.rectangle_list_geometry_shader = device->createShader(
          plume_rectangle_list_gsBlobDXIL,
          plume_rectangle_list_gsBlobDXIL_size, "GSMain", shader_format);
      break;
#endif
    default:
      break;
  }

  if (!g_plume_resources.rectangle_list_geometry_shader) {
    return false;
  }
  if (!g_plume_resources.logged_rectangle_list_geometry_shader_ready) {
    REXLOG_INFO(
        "Refii plume resource manager created rectangle-list geometry shader");
    g_plume_resources.logged_rectangle_list_geometry_shader_ready = true;
  }
  return true;
}

bool EnsurePrivateTextureCopyPixelShaderLocked(
    plume::RenderDevice* device, plume::RenderShaderFormat shader_format) {
  if (!device || shader_format == plume::RenderShaderFormat::UNKNOWN) {
    return false;
  }
  if (g_plume_resources.private_texture_copy_pixel_shader &&
      g_plume_resources.private_texture_copy_shader_format == shader_format) {
    return true;
  }

  g_plume_resources.private_texture_copy_shader_format = shader_format;
  g_plume_resources.private_texture_copy_pixel_shader.reset();
  switch (shader_format) {
    case plume::RenderShaderFormat::SPIRV:
      g_plume_resources.private_texture_copy_pixel_shader =
          device->createShader(plume_private_texture_copy_psBlobSPIRV,
                               plume_private_texture_copy_psBlobSPIRV_size,
                               "PSMain", shader_format);
      break;
#if defined(_WIN64)
    case plume::RenderShaderFormat::DXIL:
      g_plume_resources.private_texture_copy_pixel_shader =
          device->createShader(plume_private_texture_copy_psBlobDXIL,
                               plume_private_texture_copy_psBlobDXIL_size,
                               "PSMain", shader_format);
      break;
#endif
    default:
      break;
  }

  if (!g_plume_resources.private_texture_copy_pixel_shader) {
    return false;
  }
  if (!g_plume_resources.logged_private_texture_copy_pixel_shader_ready) {
    REXLOG_INFO(
        "Refii plume resource manager created private texture copy pixel "
        "shader");
    g_plume_resources.logged_private_texture_copy_pixel_shader_ready = true;
  }
  return true;
}

bool BuildInputLayoutForDrawLocked(
    const RendererDrawEventSnapshot& draw_event,
    std::vector<plume::RenderInputSlot>& input_slots,
    std::vector<plume::RenderInputElement>& input_elements);

bool IsFableImplicitFullscreenRectangleShapeLocked(
    const RendererDrawEventSnapshot& draw_event,
    const std::vector<plume::RenderInputSlot>& input_slots,
    const std::vector<plume::RenderInputElement>& input_elements,
    uint32_t count);

bool ShouldUseRectangleListGeometryShader(
    const RendererDrawEventSnapshot& draw_event,
    const PlumeTrackedResource& vertex_shader) {
  (void)vertex_shader;
  if (draw_event.draw.primitive_type != 8) {
    return false;
  }

  const uint32_t count = DrawVertexOrIndexCount(
      draw_event.draw.primitive_type, draw_event.draw.primitive_count);
  return count != 0 && count % 3u == 0;
}

bool EnsurePresentBlitPipelineLocked(plume::RenderDevice* device,
                                     plume::RenderShaderFormat shader_format) {
  if (!device || shader_format == plume::RenderShaderFormat::UNKNOWN) {
    return false;
  }
  if (g_plume_resources.present_blit_pipeline &&
      g_plume_resources.present_blit_texture_sets.size() ==
          kPresentBlitDescriptorSetRingSize &&
      g_plume_resources.present_blit_sampler_set &&
      g_plume_resources.present_blit_shader_format == shader_format) {
    return true;
  }

  g_plume_resources.present_blit_shader_format = shader_format;
  g_plume_resources.present_blit_pipeline_layout.reset();
  g_plume_resources.present_blit_pipeline.reset();
  g_plume_resources.present_blit_texture_sets.clear();
  g_plume_resources.present_blit_texture_set_index = 0;
  g_plume_resources.present_blit_sampler_set.reset();
  g_plume_resources.present_blit_sampler.reset();

  plume::RenderDescriptorRange texture_range(
      plume::RenderDescriptorRangeType::TEXTURE, 0, 1);
  plume::RenderDescriptorRange sampler_range(
      plume::RenderDescriptorRangeType::SAMPLER, 0, 1);
  plume::RenderDescriptorSetDesc descriptor_sets[2] = {
      plume::RenderDescriptorSetDesc(&texture_range, 1),
      plume::RenderDescriptorSetDesc(&sampler_range, 1),
  };
  plume::RenderPushConstantRange push_constant_range(
      0, 2, 0, sizeof(PlumePresentBlitPushConstants),
      plume::RenderShaderStageFlag::VERTEX | plume::RenderShaderStageFlag::PIXEL);
  plume::RenderPipelineLayoutDesc layout_desc;
  layout_desc.pushConstantRanges = &push_constant_range;
  layout_desc.pushConstantRangesCount = 1;
  layout_desc.descriptorSetDescs = descriptor_sets;
  layout_desc.descriptorSetDescsCount = 2;
  g_plume_resources.present_blit_pipeline_layout =
      device->createPipelineLayout(layout_desc);
  if (!g_plume_resources.present_blit_pipeline_layout) {
    return false;
  }

  std::unique_ptr<plume::RenderShader> vertex_shader;
  std::unique_ptr<plume::RenderShader> pixel_shader;
  if (!CreatePresentBlitShaderForFormat(device, shader_format, true,
                                        vertex_shader) ||
      !CreatePresentBlitShaderForFormat(device, shader_format, false,
                                        pixel_shader)) {
    return false;
  }

  plume::RenderGraphicsPipelineDesc pipeline_desc;
  pipeline_desc.pipelineLayout =
      g_plume_resources.present_blit_pipeline_layout.get();
  pipeline_desc.vertexShader = vertex_shader.get();
  pipeline_desc.pixelShader = pixel_shader.get();
  pipeline_desc.renderTargetFormat[0] = plume::RenderFormat::B8G8R8A8_UNORM;
  pipeline_desc.renderTargetBlend[0] = plume::RenderBlendDesc::Copy();
  pipeline_desc.renderTargetCount = 1;
  pipeline_desc.primitiveTopology =
      plume::RenderPrimitiveTopology::TRIANGLE_LIST;
  pipeline_desc.cullMode = plume::RenderCullMode::NONE;
  g_plume_resources.present_blit_pipeline =
      device->createGraphicsPipeline(pipeline_desc);
  if (!g_plume_resources.present_blit_pipeline) {
    return false;
  }
  g_plume_resources.present_blit_pipeline->setName("Refii Present Blit");

  plume::RenderSamplerDesc sampler_desc;
  sampler_desc.minFilter = plume::RenderFilter::NEAREST;
  sampler_desc.magFilter = plume::RenderFilter::NEAREST;
  sampler_desc.mipmapMode = plume::RenderMipmapMode::NEAREST;
  sampler_desc.addressU = plume::RenderTextureAddressMode::CLAMP;
  sampler_desc.addressV = plume::RenderTextureAddressMode::CLAMP;
  sampler_desc.addressW = plume::RenderTextureAddressMode::CLAMP;
  sampler_desc.anisotropyEnabled = false;
  g_plume_resources.present_blit_sampler =
      device->createSampler(sampler_desc);
  g_plume_resources.present_blit_texture_sets.reserve(
      kPresentBlitDescriptorSetRingSize);
  for (uint32_t i = 0; i < kPresentBlitDescriptorSetRingSize; ++i) {
    g_plume_resources.present_blit_texture_sets.emplace_back(
        device->createDescriptorSet(descriptor_sets[0]));
    if (!g_plume_resources.present_blit_texture_sets.back()) {
      return false;
    }
  }
  g_plume_resources.present_blit_sampler_set =
      device->createDescriptorSet(descriptor_sets[1]);
  if (!g_plume_resources.present_blit_sampler ||
      !g_plume_resources.present_blit_sampler_set) {
    return false;
  }
  g_plume_resources.present_blit_sampler_set->setSampler(
      0, g_plume_resources.present_blit_sampler.get());

  if (!g_plume_resources.logged_present_blit_ready) {
    REXLOG_INFO("Refii plume present blit pipeline initialized");
    g_plume_resources.logged_present_blit_ready = true;
  }
  return true;
}

void RecordFallbackTextureUploadsLocked(plume::RenderCommandList* command_list) {
  if (!command_list || !g_plume_resources.fallback_textures_upload_pending) {
    return;
  }

  constexpr uint32_t kFallbackTextureRowWidth = 64;
  constexpr uint32_t kFallbackTexturePlacementBytes = 512;
  for (size_t texture_index = 0;
       texture_index < std::size(g_plume_resources.null_textures);
       ++texture_index) {
    plume::RenderTexture* texture =
        g_plume_resources.null_textures[texture_index].get();
    plume::RenderBuffer* upload =
        g_plume_resources.null_texture_uploads[texture_index].get();
    if (!texture || !upload) {
      continue;
    }

    command_list->barriers(
        plume::RenderBarrierStage::COPY,
        plume::RenderTextureBarrier(texture,
                                    plume::RenderTextureLayout::COPY_DEST));

    const uint32_t subresource_count = texture_index == 2 ? 6u : 1u;
    for (uint32_t subresource = 0; subresource < subresource_count;
         ++subresource) {
      command_list->copyTextureRegion(
          plume::RenderTextureCopyLocation::Subresource(texture, 0,
                                                        subresource),
          plume::RenderTextureCopyLocation::PlacedFootprint(
              upload, plume::RenderFormat::R8G8B8A8_UNORM, 1, 1, 1,
              kFallbackTextureRowWidth,
              uint64_t(subresource) * kFallbackTexturePlacementBytes));
    }

    command_list->barriers(
        plume::RenderBarrierStage::GRAPHICS | plume::RenderBarrierStage::COPY,
        plume::RenderTextureBarrier(texture,
                                    plume::RenderTextureLayout::SHADER_READ));
  }

  g_plume_resources.fallback_textures_upload_pending = false;
  if (!g_plume_resources.logged_fallback_texture_upload) {
    REXLOG_INFO("Refii plume uploaded opaque white fallback Fable textures");
    g_plume_resources.logged_fallback_texture_upload = true;
  }
}

void RecordTrackedTextureUploadsLocked(plume::RenderCommandList* command_list) {
  g_plume_resources.tracked_texture_uploads_this_frame = 0;
  g_plume_resources.decoded_tracked_texture_uploads_this_frame = 0;
  g_plume_resources.raw_tracked_texture_uploads_this_frame = 0;
  g_plume_resources.placeholder_tracked_texture_uploads_this_frame = 0;
  g_plume_resources.pending_tracked_texture_uploads = 0;
  if (!command_list) {
    return;
  }

  uint32_t uploaded_count = 0;
  uint32_t decoded_count = 0;
  uint32_t raw_count = 0;
  uint32_t placeholder_count = 0;
  uint32_t pending_count = 0;

  for (auto& [address, tracked] : g_plume_resources.resources) {
    (void)address;
    if (!tracked.texture || !tracked.texture_upload ||
        !tracked.texture_upload_pending) {
      continue;
    }
    ++pending_count;

    command_list->barriers(
        plume::RenderBarrierStage::COPY,
        plume::RenderTextureBarrier(tracked.texture.get(),
                                    plume::RenderTextureLayout::COPY_DEST));

    const uint32_t subresource_count =
        std::max<uint32_t>(1, tracked.texture_upload_subresources);
    for (uint32_t subresource = 0; subresource < subresource_count;
         ++subresource) {
      command_list->copyTextureRegion(
          plume::RenderTextureCopyLocation::Subresource(
              tracked.texture.get(), 0, subresource),
          plume::RenderTextureCopyLocation::PlacedFootprint(
              tracked.texture_upload.get(), tracked.texture_format,
              tracked.texture_width, tracked.texture_height,
              std::max<uint32_t>(1, tracked.texture_depth),
              tracked.texture_upload_row_width,
              uint64_t(subresource) *
                  tracked.texture_upload_subresource_bytes));
    }

    command_list->barriers(
        plume::RenderBarrierStage::GRAPHICS | plume::RenderBarrierStage::COPY,
        plume::RenderTextureBarrier(tracked.texture.get(),
                                    plume::RenderTextureLayout::SHADER_READ));
    if (tracked.texture_upload_decoded) {
      ++decoded_count;
    } else if (tracked.texture_upload_has_source) {
      ++raw_count;
    } else {
      ++placeholder_count;
    }
    tracked.texture_upload_pending = false;
    ++uploaded_count;
  }

  g_plume_resources.tracked_texture_uploads_this_frame = uploaded_count;
  g_plume_resources.decoded_tracked_texture_uploads_this_frame = decoded_count;
  g_plume_resources.raw_tracked_texture_uploads_this_frame = raw_count;
  g_plume_resources.placeholder_tracked_texture_uploads_this_frame =
      placeholder_count;
  g_plume_resources.pending_tracked_texture_uploads =
      pending_count >= uploaded_count ? pending_count - uploaded_count : 0;
  g_plume_resources.tracked_texture_uploads_total += uploaded_count;

  if (uploaded_count != 0 && RendererResourceTracingEnabled()) {
    REXLOG_INFO(
        "Refii plume uploaded {} tracked Fable texture placeholders "
        "(decoded {} raw {} placeholder {}, total {})",
        uploaded_count, decoded_count, raw_count, placeholder_count,
        g_plume_resources.tracked_texture_uploads_total);
    g_plume_resources.logged_tracked_texture_upload = true;
  }
}

plume::RenderBuffer* CreateFrameConstantBufferLocked(
    plume::RenderDevice* device,
    const std::array<uint32_t, kRendererShaderFloatConstantDwords>& constants) {
  if (!device) {
    return nullptr;
  }

  std::unique_ptr<plume::RenderBuffer> buffer = device->createBuffer(
      plume::RenderBufferDesc::UploadBuffer(
          sizeof(uint32_t) * constants.size(),
          plume::RenderBufferFlag::CONSTANT |
              plume::RenderBufferFlag::DEVICE_ADDRESSABLE));
  if (!buffer) {
    return nullptr;
  }

  if (void* dst = buffer->map()) {
    std::memcpy(dst, constants.data(), sizeof(uint32_t) * constants.size());
    plume::RenderRange written_range(0, sizeof(uint32_t) * constants.size());
    buffer->unmap(0, &written_range);
  }

  plume::RenderBuffer* result = buffer.get();
  g_plume_resources.frame_constant_buffers.emplace_back(std::move(buffer));
  return result;
}

plume::RenderBuffer* CreateFrameByteBufferLocked(plume::RenderDevice* device,
                                                 const void* data,
                                                 uint32_t size) {
  if (!device || !data || size == 0) {
    return nullptr;
  }

  std::unique_ptr<plume::RenderBuffer> buffer = device->createBuffer(
      plume::RenderBufferDesc::UploadBuffer(
          size, plume::RenderBufferFlag::CONSTANT |
                    plume::RenderBufferFlag::DEVICE_ADDRESSABLE));
  if (!buffer) {
    return nullptr;
  }

  if (void* dst = buffer->map()) {
    std::memcpy(dst, data, size);
    plume::RenderRange written_range(0, size);
    buffer->unmap(0, &written_range);
  }

  plume::RenderBuffer* result = buffer.get();
  g_plume_resources.frame_constant_buffers.emplace_back(std::move(buffer));
  return result;
}

plume::RenderBuffer* CreateFrameVertexBufferLocked(plume::RenderDevice* device,
                                                   const void* data,
                                                   uint32_t size,
                                                   const char* name) {
  if (!device || !data || size == 0) {
    return nullptr;
  }

  std::unique_ptr<plume::RenderBuffer> buffer =
      device->createBuffer(plume::RenderBufferDesc::VertexBuffer(
          size, plume::RenderHeapType::UPLOAD));
  if (!buffer) {
    return nullptr;
  }

  if (void* dst = buffer->map()) {
    std::memcpy(dst, data, size);
    plume::RenderRange written_range(0, size);
    buffer->unmap(0, &written_range);
  }
  if (name) {
    buffer->setName(name);
  }

  plume::RenderBuffer* result = buffer.get();
  g_plume_resources.frame_constant_buffers.emplace_back(std::move(buffer));
  return result;
}

plume::RenderBuffer* CreateFrameIndexBufferLocked(plume::RenderDevice* device,
                                                  const uint32_t* indices,
                                                  uint32_t index_count) {
  if (!device || !indices || index_count == 0) {
    return nullptr;
  }

  const uint64_t byte_size =
      static_cast<uint64_t>(index_count) * sizeof(uint32_t);
  if (byte_size > std::numeric_limits<uint32_t>::max()) {
    return nullptr;
  }

  std::unique_ptr<plume::RenderBuffer> buffer = device->createBuffer(
      plume::RenderBufferDesc::IndexBuffer(static_cast<uint32_t>(byte_size),
                                           plume::RenderHeapType::UPLOAD));
  if (!buffer) {
    return nullptr;
  }

  if (void* dst = buffer->map()) {
    std::memcpy(dst, indices, static_cast<size_t>(byte_size));
    plume::RenderRange written_range(0, byte_size);
    buffer->unmap(0, &written_range);
  }

  buffer->setName("Refii Fable indexed quad-list expansion");
  plume::RenderBuffer* result = buffer.get();
  g_plume_resources.frame_constant_buffers.emplace_back(std::move(buffer));
  return result;
}

uint64_t UploadFrameConstantBytesLocked(plume::RenderDevice* device,
                                        const void* data, uint32_t size) {
  if (!device || !data || size == 0) {
    return 0;
  }

  if (!g_plume_resources.frame_constant_slab ||
      g_plume_resources.frame_constant_slab_size < kFrameConstantSlabSize) {
    if (g_plume_resources.frame_constant_slab_mapped &&
        g_plume_resources.frame_constant_slab) {
      g_plume_resources.frame_constant_slab->unmap();
    }
    g_plume_resources.frame_constant_slab_mapped = nullptr;
    g_plume_resources.frame_constant_slab = device->createBuffer(
        plume::RenderBufferDesc::UploadBuffer(
            kFrameConstantSlabSize,
            plume::RenderBufferFlag::CONSTANT |
                plume::RenderBufferFlag::DEVICE_ADDRESSABLE));
    g_plume_resources.frame_constant_slab_size =
        g_plume_resources.frame_constant_slab ? kFrameConstantSlabSize : 0;
    g_plume_resources.frame_constant_slab_offset = 0;
    if (g_plume_resources.frame_constant_slab) {
      g_plume_resources.frame_constant_slab->setName(
          "Refii Fable per-frame constants");
      g_plume_resources.frame_constant_slab_mapped = static_cast<uint8_t*>(
          g_plume_resources.frame_constant_slab->map());
    }
  }

  const uint64_t offset = AlignUp(g_plume_resources.frame_constant_slab_offset,
                                  kFrameConstantAlignment);
  if (!g_plume_resources.frame_constant_slab ||
      offset + size > g_plume_resources.frame_constant_slab_size) {
    plume::RenderBuffer* fallback = CreateFrameByteBufferLocked(device, data, size);
    return fallback ? fallback->getDeviceAddress() : 0;
  }

  if (!g_plume_resources.frame_constant_slab_mapped) {
    plume::RenderBuffer* fallback = CreateFrameByteBufferLocked(device, data, size);
    return fallback ? fallback->getDeviceAddress() : 0;
  }

  std::memcpy(g_plume_resources.frame_constant_slab_mapped + offset, data,
              size);
  g_plume_resources.frame_constant_slab_offset = offset + size;
  return g_plume_resources.frame_constant_slab->getDeviceAddress() + offset;
}

uint64_t UploadFrameConstantsLocked(
    plume::RenderDevice* device,
    const std::array<uint32_t, kRendererShaderFloatConstantDwords>& constants) {
  return UploadFrameConstantBytesLocked(
      device, constants.data(),
      static_cast<uint32_t>(sizeof(uint32_t) * constants.size()));
}

plume::RenderBuffer* PrepareQuadListIndexBufferLocked(plume::RenderDevice* device,
                                                      uint32_t vertex_count,
                                                      uint32_t& index_count) {
  index_count = 0;
  if (!device || vertex_count < 4) {
    return nullptr;
  }

  const uint32_t quad_count = vertex_count / 4u;
  if (quad_count == 0) {
    return nullptr;
  }

  const bool strip_order = RendererQuadListStrip0132Order();
  bool& logged_order = strip_order
                           ? g_plume_resources.logged_quad_list_strip_order
                           : g_plume_resources.logged_quad_list_sequential_order;
  if (!logged_order) {
    REXLOG_INFO(
        "Refii plume direct quad-list split is {} for Fable primitive 13 "
        "(quad vertices={}, expanded indices={})",
        strip_order ? "strip0132 0,1,3 / 1,2,3"
                    : "sequential0123 0,1,2 / 0,2,3",
        vertex_count, quad_count * 6u);
    logged_order = true;
  }

  index_count = quad_count * 6u;
  plume::RenderBuffer* best_buffer = nullptr;
  uint32_t best_index_count = std::numeric_limits<uint32_t>::max();
  for (const PlumeCachedIndexBuffer& cached :
       g_plume_resources.quad_list_index_buffers) {
    if (cached.buffer && cached.strip_0132_order == strip_order &&
        cached.index_count >= index_count &&
        cached.index_count < best_index_count) {
      best_buffer = cached.buffer.get();
      best_index_count = cached.index_count;
    }
  }
  if (best_buffer) {
    return best_buffer;
  }

  std::vector<uint32_t> indices(index_count);
  for (uint32_t quad = 0; quad < quad_count; ++quad) {
    const uint32_t vertex = quad * 4u;
    const uint32_t index = quad * 6u;
    indices[index + 0] = vertex + 0;
    indices[index + 1] = vertex + 1;
    indices[index + 2] = strip_order ? vertex + 3 : vertex + 2;
    indices[index + 3] = strip_order ? vertex + 1 : vertex + 0;
    indices[index + 4] = vertex + 2;
    indices[index + 5] = vertex + 3;
  }

  std::unique_ptr<plume::RenderBuffer> buffer = device->createBuffer(
      plume::RenderBufferDesc::IndexBuffer(
          sizeof(uint32_t) * indices.size(), plume::RenderHeapType::UPLOAD));
  if (!buffer) {
    index_count = 0;
    return nullptr;
  }

  if (void* dst = buffer->map()) {
    std::memcpy(dst, indices.data(), sizeof(uint32_t) * indices.size());
    plume::RenderRange written_range(0, sizeof(uint32_t) * indices.size());
    buffer->unmap(0, &written_range);
  }

  buffer->setName("Refii Fable quad-list index expansion");
  plume::RenderBuffer* result = buffer.get();
  g_plume_resources.quad_list_index_buffers.push_back(
      PlumeCachedIndexBuffer{index_count, strip_order, std::move(buffer)});
  return result;
}

plume::RenderBuffer* PrepareIndexedQuadListIndexBufferLocked(
    plume::RenderDevice* device, const PlumeTrackedResource& source_index_buffer,
    uint32_t first_index, uint32_t source_index_count,
    uint32_t& expanded_index_count) {
  expanded_index_count = 0;
  if (!device || source_index_count < 4 ||
      source_index_buffer.index_shadow_upload.empty()) {
    return nullptr;
  }

  const uint32_t source_stride =
      source_index_buffer.stride_or_format == 4u ? 4u : 2u;
  const uint64_t source_index_end =
      static_cast<uint64_t>(first_index) + source_index_count;
  const uint64_t source_byte_end = source_index_end * source_stride;
  if (source_byte_end > source_index_buffer.index_shadow_upload.size()) {
    return nullptr;
  }

  const uint32_t quad_count = source_index_count / 4u;
  if (quad_count == 0) {
    return nullptr;
  }

  expanded_index_count = quad_count * 6u;
  std::vector<uint32_t> expanded_indices(expanded_index_count);
  const bool strip_order = RendererQuadListStrip0132Order();
  const uint8_t* source = source_index_buffer.index_shadow_upload.data() +
                          static_cast<size_t>(first_index) * source_stride;
  const auto load_source_index = [&](uint32_t index) -> uint32_t {
    const uint8_t* index_data = source + static_cast<size_t>(index) * source_stride;
    if (source_stride == 4u) {
      uint32_t value = 0;
      std::memcpy(&value, index_data, sizeof(value));
      return value;
    }
    uint16_t value = 0;
    std::memcpy(&value, index_data, sizeof(value));
    return value;
  };

  for (uint32_t quad = 0; quad < quad_count; ++quad) {
    const uint32_t src = quad * 4u;
    const uint32_t dst = quad * 6u;
    const uint32_t i0 = load_source_index(src + 0u);
    const uint32_t i1 = load_source_index(src + 1u);
    const uint32_t i2 = load_source_index(src + 2u);
    const uint32_t i3 = load_source_index(src + 3u);
    expanded_indices[dst + 0u] = i0;
    expanded_indices[dst + 1u] = i1;
    expanded_indices[dst + 2u] = strip_order ? i3 : i2;
    expanded_indices[dst + 3u] = strip_order ? i1 : i0;
    expanded_indices[dst + 4u] = i2;
    expanded_indices[dst + 5u] = i3;
  }

  return CreateFrameIndexBufferLocked(device, expanded_indices.data(),
                                      expanded_index_count);
}

uint32_t FoldFableShaderBooleans(
    const std::array<uint32_t, kRendererShaderBoolConstantDwords>& constants) {
  uint32_t packed = 0;
  for (uint32_t value : constants) {
    packed |= value;
  }
  return packed;
}

constexpr uint32_t kFablePixRenderVideoPixelShader = 0x0E010171u;
constexpr uint64_t kFablePixRenderVideoPixelShaderHash =
    0x69E9772C76BD6253ull;
constexpr uint64_t kFableBinkMoviePixelShaderHash =
    kFablePixRenderVideoPixelShaderHash;
constexpr uint32_t kFablePixVideoPlaneSlots[] = {0u, 1u, 2u};

bool IsFablePixVideoRectangleDraw(const RendererDrawEventSnapshot& draw_event) {
  return draw_event.private_texture_rectangle &&
         draw_event.pixel_shader == kFablePixRenderVideoPixelShader;
}

bool FableSingleSourceVideoUsesDecodedChannels(uint32_t guest_format);

bool IsFableVideoNeutralPlaneTextureLocked(
    uint32_t guest_texture, const RendererDrawEventSnapshot& draw_event,
    uint32_t slot) {
  if (guest_texture == 0) {
    return true;
  }

  const PlumeTrackedResource* texture =
      FindTrackedSampledImageLocked(guest_texture, draw_event, slot);
  return texture && texture->texture_width <= 1 && texture->texture_height <= 1 &&
         texture->texture_depth <= 1;
}

bool IsFableVideoYCrCbPixelShaderLocked(uint32_t pixel_shader_address) {
  if (pixel_shader_address == kFablePixRenderVideoPixelShader) {
    return true;
  }

  const PlumeTrackedResource* pixel_shader = FindTrackedResourceLocked(
      pixel_shader_address, RendererTrackedResourceKind::kPixelShader);
  return pixel_shader &&
         (pixel_shader->shader_name ==
              "0171_shader_postprocessing_horizontal_gaussblur" ||
          pixel_shader->guest_bytecode_hash ==
              kFablePixRenderVideoPixelShaderHash ||
	          pixel_shader->guest_bytecode_hash == kFableBinkMoviePixelShaderHash);
}

bool IsFablePixDecodedVideoPlaneResource(const PlumeTrackedResource& resource,
                                         uint32_t width, uint32_t height) {
  namespace xenos = rex::graphics::xenos;
  return resource.kind == RendererTrackedResourceKind::kTexture &&
         resource.texture && resource.texture_view &&
         resource.texture_format == plume::RenderFormat::R8_UNORM &&
         resource.texture_view_dimension ==
             plume::RenderTextureViewDimension::TEXTURE_2D &&
         resource.texture_width == width && resource.texture_height == height &&
         resource.texture_depth == 1 && resource.texture_upload_has_source &&
         resource.upload_generation != 0 &&
         static_cast<xenos::TextureFormat>(resource.texture_guest_format & 0x3Fu) ==
             xenos::TextureFormat::k_8;
}

bool IsFablePixDecodedVideoPlaneResource(const PlumeTrackedResource* resource,
                                         uint32_t width, uint32_t height) {
  return resource &&
         IsFablePixDecodedVideoPlaneResource(*resource, width, height);
}

bool IsLikelyFablePixDecodedVideoPlaneResource(
    const PlumeTrackedResource* resource) {
  namespace xenos = rex::graphics::xenos;
  return resource && resource->kind == RendererTrackedResourceKind::kTexture &&
         resource->texture && resource->texture_view &&
         resource->texture_format == plume::RenderFormat::R8_UNORM &&
         resource->texture_view_dimension ==
             plume::RenderTextureViewDimension::TEXTURE_2D &&
         resource->texture_width >= 320 && resource->texture_height >= 180 &&
         resource->texture_depth == 1 && resource->texture_upload_has_source &&
         resource->upload_generation != 0 &&
         static_cast<xenos::TextureFormat>(
             resource->texture_guest_format & 0x3Fu) ==
             xenos::TextureFormat::k_8;
}

bool FableDecodedVideoPlaneHasUploadContent(
    const PlumeTrackedResource& resource) {
  return resource.texture_upload_has_source &&
         resource.upload_generation != 0 &&
         resource.debug_first_nonzero_offset != UINT32_MAX;
}

bool IsCoherentFableDecodedVideoPlaneSet(
    const PlumeTrackedResource* y, const PlumeTrackedResource* cr,
    const PlumeTrackedResource* cb) {
  return y && cr && cb && y != cr && y != cb && cr != cb &&
         y->guest_address != cr->guest_address &&
         y->guest_address != cb->guest_address &&
         cr->guest_address != cb->guest_address;
}

bool FindFableDecodedChromaPairForLumaLocked(
    const PlumeTrackedResource& y, uint32_t chroma_width,
    uint32_t chroma_height, const PlumeTrackedResource*& cr,
    const PlumeTrackedResource*& cb) {
  cr = nullptr;
  cb = nullptr;
  uint32_t cr_address = 0;
  uint32_t cb_address = 0;
  for (const auto& [chroma_address, chroma] : g_plume_resources.resources) {
    if (chroma_address >= y.guest_address ||
        y.guest_address - chroma_address > 0x100000u ||
        !IsFablePixDecodedVideoPlaneResource(chroma, chroma_width,
                                             chroma_height) ||
        !FableDecodedVideoPlaneHasUploadContent(chroma)) {
      continue;
    }
    if (chroma_address > cr_address) {
      cb = cr;
      cb_address = cr_address;
      cr = &chroma;
      cr_address = chroma_address;
    } else if (chroma_address > cb_address) {
      cb = &chroma;
      cb_address = chroma_address;
    }
  }

  return IsCoherentFableDecodedVideoPlaneSet(&y, cr, cb);
}

void UpdateTrackedUploadDebugSample(PlumeTrackedResource& tracked,
                                    const uint8_t* data, size_t size) {
  tracked.debug_first_nonzero_offset = UINT32_MAX;
  tracked.debug_first_nonzero_sample.fill(0);
  tracked.debug_first_nonzero_sample_size = 0;
  for (size_t i = 0; i < size; ++i) {
    if (data[i] == 0) {
      continue;
    }
    tracked.debug_first_nonzero_offset = static_cast<uint32_t>(i);
    tracked.debug_first_nonzero_sample_size = static_cast<uint32_t>(
        std::min<size_t>(tracked.debug_first_nonzero_sample.size(), size - i));
    std::memcpy(tracked.debug_first_nonzero_sample.data(), data + i,
                tracked.debug_first_nonzero_sample_size);
    break;
  }

  tracked.debug_upload_sample.assign(
      data, data + std::min<size_t>(size, kTrackedBufferDebugSampleBytes));
}

const PlumeTrackedResource* FindFableVideoPlaneSampledImageLocked(
    const RendererDrawEventSnapshot& draw_event, uint32_t slot) {
  const uint32_t guest_texture =
      slot < std::size(draw_event.textures) ? draw_event.textures[slot] : 0;
  const PlumeTrackedResource* texture =
      FindTrackedResourceLocked(guest_texture,
                                RendererTrackedResourceKind::kTexture);
  if (IsLikelyFablePixDecodedVideoPlaneResource(texture)) {
    return texture;
  }

  texture = FindDecodedSampledImageLocked(guest_texture);
  if (IsLikelyFablePixDecodedVideoPlaneResource(texture)) {
    return texture;
  }

  texture = FindTrackedTextureByFetchConstantLocked(draw_event, slot);
  if (IsLikelyFablePixDecodedVideoPlaneResource(texture)) {
    return texture;
  }

  return nullptr;
}

FablePixVideoPlaneSet ResolveFableDrawBoundVideoPlaneSetLocked(
    const RendererDrawEventSnapshot& draw_event) {
  FablePixVideoPlaneSet planes;
  if (!IsFableVideoYCrCbPixelShaderLocked(draw_event.pixel_shader)) {
    return planes;
  }

  const PlumeTrackedResource* y =
      FindFableVideoPlaneSampledImageLocked(draw_event, 0);
  if (!y || y->texture_width < 640 || y->texture_height < 360 ||
      !IsFablePixDecodedVideoPlaneResource(*y, y->texture_width,
                                           y->texture_height)) {
    return planes;
  }

  const uint32_t chroma_width = std::max<uint32_t>(1, y->texture_width / 2u);
  const uint32_t chroma_height = std::max<uint32_t>(1, y->texture_height / 2u);
  const PlumeTrackedResource* cr =
      FindFableVideoPlaneSampledImageLocked(draw_event, 1);
  const PlumeTrackedResource* cb =
      FindFableVideoPlaneSampledImageLocked(draw_event, 2);
  const bool has_explicit_chroma =
      cr && cb &&
      IsFablePixDecodedVideoPlaneResource(*cr, chroma_width, chroma_height) &&
      IsFablePixDecodedVideoPlaneResource(*cb, chroma_width, chroma_height) &&
      IsCoherentFableDecodedVideoPlaneSet(y, cr, cb);
  if (!has_explicit_chroma &&
      !FindFableDecodedChromaPairForLumaLocked(*y, chroma_width,
                                               chroma_height, cr, cb)) {
    return planes;
  }

  planes.y = y;
  planes.cr = cr;
  planes.cb = cb;
  return planes;
}

FablePixVideoPlaneSet ResolveFablePixDecodedPlaneSetLocked(
    const RendererDrawEventSnapshot& draw_event);

bool LooksLikeFableDirectVideoPlaneDrawLocked(
    const RendererDrawEventSnapshot& draw_event) {
  return !draw_event.clear && !IsFablePixVideoRectangleDraw(draw_event) &&
         IsFableVideoYCrCbPixelShaderLocked(draw_event.pixel_shader) &&
         draw_event.render_targets[0] != 0 && draw_event.draw.indexed &&
         draw_event.draw.primitive_type == 4 &&
         std::size(draw_event.textures) >= 3 && draw_event.textures[0] != 0 &&
         draw_event.textures[1] != 0 && draw_event.textures[2] != 0;
}

bool IsLikelyFablePixPrivateVideoTarget(
    const PlumeTrackedResource& resource) {
  namespace xenos = rex::graphics::xenos;
  return resource.kind == RendererTrackedResourceKind::kTexture &&
         resource.texture_width >= 640 && resource.texture_height >= 360 &&
         resource.texture_depth == 1 &&
         resource.texture_format == plume::RenderFormat::R8G8B8A8_UNORM &&
         static_cast<xenos::TextureFormat>(resource.texture_guest_format & 0x3Fu) ==
             xenos::TextureFormat::k_8_8_8_8 &&
         resource.texture_guest_base != 0 && resource.texture_guest_tiled;
}

FablePixVideoPlaneSet ResolveFablePixDecodedPlaneSetLocked(
    const RendererDrawEventSnapshot& draw_event) {
  FablePixVideoPlaneSet planes;
  if (!IsFableVideoYCrCbPixelShaderLocked(draw_event.pixel_shader)) {
    return planes;
  }

  planes = ResolveFableDrawBoundVideoPlaneSetLocked(draw_event);
  if (planes) {
    return planes;
  }

  if (!IsFablePixVideoRectangleDraw(draw_event)) {
    return planes;
  }

  const uint32_t texture_slot =
      std::min<uint32_t>(draw_event.private_texture_slot,
                         kMaxFableTextureSlots - 1u);
  const uint32_t source_texture =
      texture_slot < std::size(draw_event.textures)
          ? draw_event.textures[texture_slot]
          : 0;
  const PlumeTrackedResource* source_resource =
      FindTrackedSampledImageLocked(source_texture, draw_event, texture_slot);
  if (!source_resource ||
      !IsLikelyFablePixPrivateVideoTarget(*source_resource)) {
    return planes;
  }

  const uint32_t luma_width = source_resource->texture_width;
  const uint32_t luma_height = source_resource->texture_height;
  const uint32_t chroma_width = std::max<uint32_t>(1, luma_width / 2u);
  const uint32_t chroma_height = std::max<uint32_t>(1, luma_height / 2u);

  for (const auto& [guest_address, candidate] : g_plume_resources.resources) {
    (void)guest_address;
    if (!IsFablePixDecodedVideoPlaneResource(candidate, luma_width,
                                             luma_height) ||
        !FableDecodedVideoPlaneHasUploadContent(candidate)) {
      continue;
    }

    const PlumeTrackedResource* cr = nullptr;
    const PlumeTrackedResource* cb = nullptr;
    if (!FindFableDecodedChromaPairForLumaLocked(candidate, chroma_width,
                                                 chroma_height, cr, cb)) {
      continue;
    }

    if (!planes || candidate.serial > planes.y->serial) {
      planes.y = &candidate;
      planes.cr = cr;
      planes.cb = cb;
    }
  }

  return planes;
}

bool IsFableSingleSourcePixVideoRectangleDrawLocked(
    const RendererDrawEventSnapshot& draw_event) {
  if (!IsFablePixVideoRectangleDraw(draw_event)) {
    return false;
  }

  const uint32_t texture_slot =
      std::min<uint32_t>(draw_event.private_texture_slot,
                         kMaxFableTextureSlots - 1u);
  const uint32_t source_texture =
      texture_slot < std::size(draw_event.textures)
          ? draw_event.textures[texture_slot]
          : 0;
  if (source_texture == 0) {
    return false;
  }
  const PlumeTrackedResource* source_resource =
      FindTrackedSampledImageLocked(source_texture, draw_event, texture_slot);
  if (!source_resource ||
      !FableSingleSourceVideoUsesDecodedChannels(
          source_resource->texture_guest_format)) {
    return false;
  }

  for (uint32_t slot : kFablePixVideoPlaneSlots) {
    if (slot == texture_slot || slot >= std::size(draw_event.textures)) {
      continue;
    }
    const uint32_t plane_texture = draw_event.textures[slot];
    if (plane_texture != 0 && plane_texture != source_texture &&
        !IsFableVideoNeutralPlaneTextureLocked(plane_texture, draw_event,
                                               slot)) {
      return false;
    }
  }
  return true;
}

struct FableDrawShaderRouting {
  FablePixVideoPlaneSet decoded_pix_video_planes;
  bool video_ycrcb_draw = false;
  bool private_texture_copy_draw = false;
};

FableDrawShaderRouting ResolveFableDrawShaderRoutingLocked(
    const RendererDrawEventSnapshot& draw_event) {
  FableDrawShaderRouting routing;
  if (!IsFableVideoYCrCbPixelShaderLocked(draw_event.pixel_shader)) {
    return routing;
  }

  routing.decoded_pix_video_planes =
      ResolveFablePixDecodedPlaneSetLocked(draw_event);
  routing.video_ycrcb_draw = routing.decoded_pix_video_planes.operator bool();
  routing.private_texture_copy_draw =
      IsFablePixVideoRectangleDraw(draw_event) && !routing.video_ycrcb_draw &&
      !IsFableSingleSourcePixVideoRectangleDrawLocked(draw_event);
  return routing;
}

std::array<uint32_t, kRendererShaderFloatConstantDwords>
BuildFablePixelShaderConstants(const RendererDrawEventSnapshot& draw_event,
                               bool fable_video_ycrcb_draw) {
  std::array<uint32_t, kRendererShaderFloatConstantDwords> constants =
      draw_event.pixel_shader_constants;

  if (fable_video_ycrcb_draw) {
    constexpr uint32_t kSyntheticVideoShader = kFablePixRenderVideoPixelShader;
    const auto float_bits = [](float value) {
      uint32_t bits = 0;
      std::memcpy(&bits, &value, sizeof(bits));
      return bits;
    };
    const auto constant_vector_is_zero = [&constants](uint32_t reg) {
      const uint32_t dword = reg * 4u;
      return dword + 3u < constants.size() &&
             constants[dword + 0u] == 0 && constants[dword + 1u] == 0 &&
             constants[dword + 2u] == 0 && constants[dword + 3u] == 0;
    };

    const bool force_synthetic_video_matrix =
        draw_event.pixel_shader == kSyntheticVideoShader;
    if (force_synthetic_video_matrix ||
        (constant_vector_is_zero(253u) && constant_vector_is_zero(254u) &&
         constant_vector_is_zero(255u))) {
      constexpr uint32_t kColorMatrixRegister = 253u;
      constexpr uint32_t kColorOffsetRegister = 254u;
      constexpr uint32_t kRedOffsetRegister = 255u;
      const uint32_t color_matrix = kColorMatrixRegister * 4u;
      const uint32_t color_offset = kColorOffsetRegister * 4u;
      const uint32_t red_offset = kRedOffsetRegister * 4u;
      constants[color_matrix + 0u] = float_bits(-0.344136f);
      constants[color_matrix + 1u] = float_bits(1.772000f);
      constants[color_matrix + 2u] = float_bits(1.000000f);
      constants[color_matrix + 3u] = float_bits(1.402000f);
      constants[color_offset + 0u] = float_bits(0.529136f);
      constants[color_offset + 1u] = float_bits(-0.886000f);
      constants[color_offset + 2u] = float_bits(1.000000f);
      constants[color_offset + 3u] = float_bits(-0.714136f);
      constants[red_offset + 0u] = float_bits(-0.701000f);
    }

    constexpr uint32_t kFixedAlphaRegister = 31u;
    constexpr uint32_t kFixedAlphaDword = kFixedAlphaRegister * 4u;
    if (kFixedAlphaDword + 3u < constants.size() &&
        constants[kFixedAlphaDword + 0u] == 0 &&
        constants[kFixedAlphaDword + 1u] == 0 &&
        constants[kFixedAlphaDword + 2u] == 0 &&
        constants[kFixedAlphaDword + 3u] == 0) {
      constexpr uint32_t kFloatOneBits = 0x3F800000u;
      constants[kFixedAlphaDword + 0u] = kFloatOneBits;
      constants[kFixedAlphaDword + 1u] = kFloatOneBits;
      constants[kFixedAlphaDword + 2u] = kFloatOneBits;
      constants[kFixedAlphaDword + 3u] = kFloatOneBits;
    }
  }

  return constants;
}

constexpr uint32_t kFableSharedIntegerConstantVectorCount = 32u;
constexpr uint32_t kFableSharedTextureDescriptorSlots = 32u;
constexpr uint32_t kFableSharedControlDwordBase =
    kFableSharedTextureDescriptorSlots * 4u;
constexpr uint32_t kFableSharedVertexIntegerConstantDwordBase = 144u;
constexpr uint32_t kFableSharedPixelIntegerConstantDwordBase =
    kFableSharedVertexIntegerConstantDwordBase +
    kFableSharedIntegerConstantVectorCount * 4u;
constexpr uint32_t kFableSharedConstantDwords =
    kFableSharedPixelIntegerConstantDwordBase +
    kFableSharedIntegerConstantVectorCount * 4u;

std::array<uint32_t, kFableSharedConstantDwords> BuildFableSharedConstants(
    const RendererDrawEventSnapshot& draw_event,
    const FableDrawDescriptorIndices& descriptor_indices) {
  std::array<uint32_t, kFableSharedConstantDwords> shared_constants = {};
  constexpr uint32_t kTexture2DBase = 0;
  constexpr uint32_t kTexture3DBase = kFableSharedTextureDescriptorSlots;
  constexpr uint32_t kTextureCubeBase = kFableSharedTextureDescriptorSlots * 2u;
  constexpr uint32_t kSamplerBase = kFableSharedTextureDescriptorSlots * 3u;

  for (uint32_t slot = 0; slot < kMaxFableTextureSlots; ++slot) {
    shared_constants[kTexture2DBase + slot] =
        descriptor_indices.texture2d[slot];
    shared_constants[kTexture3DBase + slot] =
        descriptor_indices.texture3d[slot];
    shared_constants[kTextureCubeBase + slot] =
        descriptor_indices.texture_cube[slot];
    shared_constants[kSamplerBase + slot] =
        descriptor_indices.samplers[slot];
  }

  // Keep most XenosRecomp common controls bitwise-zero until the Fable render
  // states feeding them are decoded. XenosRecomp currently exposes one packed
  // bool dword, so fold Fable's observed bool words together until the shader
  // ABI grows to a real bool array.
  shared_constants[kFableSharedControlDwordBase + 0u] =
      FoldFableShaderBooleans(draw_event.vertex_shader_bool_constants) |
      FoldFableShaderBooleans(draw_event.pixel_shader_bool_constants);
  if (const PlumeTrackedResource* declaration = FindTrackedResourceLocked(
          draw_event.vertex_declaration,
          RendererTrackedResourceKind::kVertexDeclaration)) {
    shared_constants[kFableSharedControlDwordBase + 1u] =
        declaration->swapped_texcoords;  // g_SwappedTexcoords
  } else {
    shared_constants[kFableSharedControlDwordBase + 1u] =
        0;  // g_SwappedTexcoords
  }
  shared_constants[kFableSharedControlDwordBase + 2u] =
      0;  // g_HalfPixelOffset.x
  shared_constants[kFableSharedControlDwordBase + 3u] =
      0;  // g_HalfPixelOffset.y
  shared_constants[kFableSharedControlDwordBase + 4u] =
      FableAlphaThresholdBits(draw_event);  // g_AlphaThreshold
  shared_constants[kFableSharedControlDwordBase + 5u] =
      std::min<uint32_t>(draw_event.private_texture_slot,
                         kMaxFableTextureSlots - 1u);  // g_PrivateTextureSlot
  shared_constants[kFableSharedControlDwordBase + 6u] =
      draw_event.vertex_shader_int_constant_valid_mask;
  shared_constants[kFableSharedControlDwordBase + 7u] =
      draw_event.pixel_shader_int_constant_valid_mask;

  std::copy(draw_event.vertex_shader_int_constants.begin(),
            draw_event.vertex_shader_int_constants.end(),
            shared_constants.begin() +
                kFableSharedVertexIntegerConstantDwordBase);
  std::copy(draw_event.pixel_shader_int_constants.begin(),
            draw_event.pixel_shader_int_constants.end(),
            shared_constants.begin() +
                kFableSharedPixelIntegerConstantDwordBase);

  return shared_constants;
}

std::unique_ptr<plume::RenderBuffer> CreateBufferForTrackedResource(
    plume::RenderDevice* device, const RendererTrackedResourceSnapshot& record) {
  if (!device || record.size == 0) {
    return nullptr;
  }

  if (record.kind == RendererTrackedResourceKind::kVertexBuffer) {
    return device->createBuffer(plume::RenderBufferDesc::VertexBuffer(
        record.size, plume::RenderHeapType::UPLOAD));
  }
  if (record.kind == RendererTrackedResourceKind::kIndexBuffer) {
    return device->createBuffer(plume::RenderBufferDesc::IndexBuffer(
        record.size, plume::RenderHeapType::UPLOAD));
  }

  return nullptr;
}

std::unique_ptr<plume::RenderShader> CreateShaderForTrackedResource(
    plume::RenderDevice* device, plume::RenderShaderFormat shader_format,
    const RendererTrackedResourceSnapshot& record,
    RendererShaderLibraryEntry& shader_entry) {
  if (!device || record.guest_bytecode_hash == 0) {
    return nullptr;
  }
  if (!FindRendererShaderLibraryEntryByGuestHash(record.guest_bytecode_hash,
                                                 shader_entry)) {
    return nullptr;
  }

  std::vector<uint8_t> shader_bytes;
  if (!ReadFileBytes(shader_entry.binary_path, shader_bytes) ||
      shader_bytes.empty()) {
    return nullptr;
  }
  return device->createShader(shader_bytes.data(), shader_bytes.size(), "main",
                              shader_format);
}

void NameTrackedBuffer(plume::RenderBuffer* buffer,
                       const RendererTrackedResourceSnapshot& record) {
  if (!buffer) {
    return;
  }

  char name[96] = {};
  std::snprintf(name, sizeof(name), "Refii Fable %s 0x%08X",
                TrackedResourceKindName(record.kind), record.guest_address);
  buffer->setName(name);
}

void NameTrackedShader(plume::RenderShader* shader,
                       const RendererTrackedResourceSnapshot& record,
                       const RendererShaderLibraryEntry& shader_entry) {
  if (!shader) {
    return;
  }

  char name[192] = {};
  std::snprintf(name, sizeof(name),
                "Refii Fable %s %04u %s guest=0x%08X hash=0x%016llX",
                TrackedResourceKindName(record.kind), shader_entry.id,
                shader_entry.name.c_str(), record.guest_address,
                static_cast<unsigned long long>(
                    shader_entry.guest_bytecode_hash));
  shader->setName(name);
}

plume::RenderTextureViewDimension ViewDimensionForTextureDimension(
    plume::RenderTextureDimension dimension, bool cube) {
  if (cube) {
    return plume::RenderTextureViewDimension::TEXTURE_CUBE;
  }
  switch (dimension) {
    case plume::RenderTextureDimension::TEXTURE_1D:
      return plume::RenderTextureViewDimension::TEXTURE_1D;
    case plume::RenderTextureDimension::TEXTURE_2D:
      return plume::RenderTextureViewDimension::TEXTURE_2D;
    case plume::RenderTextureDimension::TEXTURE_3D:
      return plume::RenderTextureViewDimension::TEXTURE_3D;
    case plume::RenderTextureDimension::UNKNOWN:
      break;
  }
  return plume::RenderTextureViewDimension::TEXTURE_2D;
}

plume::RenderTextureViewDesc TextureViewDescForDimension(
    plume::RenderTextureViewDimension dimension, plume::RenderFormat format) {
  switch (dimension) {
    case plume::RenderTextureViewDimension::TEXTURE_1D:
      return plume::RenderTextureViewDesc::Texture1D(format);
    case plume::RenderTextureViewDimension::TEXTURE_3D:
      return plume::RenderTextureViewDesc::Texture3D(format);
    case plume::RenderTextureViewDimension::TEXTURE_CUBE:
      return plume::RenderTextureViewDesc::TextureCube(format);
    case plume::RenderTextureViewDimension::TEXTURE_2D:
    case plume::RenderTextureViewDimension::UNKNOWN:
      break;
  }
  return plume::RenderTextureViewDesc::Texture2D(format);
}

plume::RenderSwizzle MapXenosTextureSwizzle(uint32_t component) {
  switch (component) {
    case rex::graphics::xenos::XE_GPU_TEXTURE_SWIZZLE_R:
      return plume::RenderSwizzle::R;
    case rex::graphics::xenos::XE_GPU_TEXTURE_SWIZZLE_G:
      return plume::RenderSwizzle::G;
    case rex::graphics::xenos::XE_GPU_TEXTURE_SWIZZLE_B:
      return plume::RenderSwizzle::B;
    case rex::graphics::xenos::XE_GPU_TEXTURE_SWIZZLE_A:
      return plume::RenderSwizzle::A;
    case rex::graphics::xenos::XE_GPU_TEXTURE_SWIZZLE_0:
      return plume::RenderSwizzle::ZERO;
    case rex::graphics::xenos::XE_GPU_TEXTURE_SWIZZLE_1:
      return plume::RenderSwizzle::ONE;
    case 6:
      return plume::RenderSwizzle::ZERO;
    case 7:
      return plume::RenderSwizzle::ONE;
    default:
      return plume::RenderSwizzle::IDENTITY;
  }
}

plume::RenderSwizzle ComposeTextureSwizzle(
    uint32_t component,
    const std::array<plume::RenderSwizzle, 4>& base_mapping) {
  if (component < base_mapping.size()) {
    return base_mapping[component];
  }
  return MapXenosTextureSwizzle(component);
}

std::array<plume::RenderSwizzle, 4> BaseXenosTextureComponentMapping(
    uint32_t guest_format) {
  using Swizzle = plume::RenderSwizzle;
  namespace xenos = rex::graphics::xenos;

  switch (static_cast<xenos::TextureFormat>(guest_format & 0x3Fu)) {
    case xenos::TextureFormat::k_8:
    case xenos::TextureFormat::k_8_A:
    case xenos::TextureFormat::k_8_B:
    case xenos::TextureFormat::k_24_8:
    case xenos::TextureFormat::k_24_8_FLOAT:
    case xenos::TextureFormat::k_16:
    case xenos::TextureFormat::k_16_EXPAND:
    case xenos::TextureFormat::k_16_FLOAT:
    case xenos::TextureFormat::k_32:
    case xenos::TextureFormat::k_32_FLOAT:
    case xenos::TextureFormat::k_32_AS_8:
    case xenos::TextureFormat::k_16_MPEG:
    case xenos::TextureFormat::k_8_INTERLACED:
    case xenos::TextureFormat::k_32_AS_8_INTERLACED:
    case xenos::TextureFormat::k_16_INTERLACED:
    case xenos::TextureFormat::k_16_MPEG_INTERLACED:
    case xenos::TextureFormat::k_DXT3A:
    case xenos::TextureFormat::k_DXT5A:
      return {Swizzle::R, Swizzle::R, Swizzle::R, Swizzle::R};

    case xenos::TextureFormat::k_8_8:
    case xenos::TextureFormat::k_16_16_EDRAM:
    case xenos::TextureFormat::k_16_16:
    case xenos::TextureFormat::k_16_16_EXPAND:
    case xenos::TextureFormat::k_16_16_FLOAT:
    case xenos::TextureFormat::k_32_32:
    case xenos::TextureFormat::k_32_32_FLOAT:
    case xenos::TextureFormat::k_32_AS_8_8:
    case xenos::TextureFormat::k_16_16_MPEG:
    case xenos::TextureFormat::k_32_AS_8_8_INTERLACED:
    case xenos::TextureFormat::k_16_16_MPEG_INTERLACED:
    case xenos::TextureFormat::k_DXN:
    case xenos::TextureFormat::k_CTX1:
      return {Swizzle::R, Swizzle::G, Swizzle::G, Swizzle::G};

    case xenos::TextureFormat::k_10_11_11:
    case xenos::TextureFormat::k_11_11_10:
    case xenos::TextureFormat::k_10_11_11_AS_16_16_16_16:
    case xenos::TextureFormat::k_11_11_10_AS_16_16_16_16:
    case xenos::TextureFormat::k_32_32_32_FLOAT:
      return {Swizzle::R, Swizzle::G, Swizzle::B, Swizzle::B};

    case xenos::TextureFormat::k_6_5_5:
      return {Swizzle::R, Swizzle::B, Swizzle::G, Swizzle::G};

    default:
      return {Swizzle::R, Swizzle::G, Swizzle::B, Swizzle::A};
  }
}

plume::RenderComponentMapping XenosTextureComponentMapping(
    uint32_t guest_format, uint32_t fetch_swizzle) {
  const std::array<plume::RenderSwizzle, 4> base_mapping =
      BaseXenosTextureComponentMapping(guest_format);
  return plume::RenderComponentMapping(
      ComposeTextureSwizzle((fetch_swizzle >> 0u) & 0x7u, base_mapping),
      ComposeTextureSwizzle((fetch_swizzle >> 3u) & 0x7u, base_mapping),
      ComposeTextureSwizzle((fetch_swizzle >> 6u) & 0x7u, base_mapping),
      ComposeTextureSwizzle((fetch_swizzle >> 9u) & 0x7u, base_mapping));
}

plume::RenderComponentMapping IdentityTextureComponentMapping() {
  return plume::RenderComponentMapping(plume::RenderSwizzle::R,
                                       plume::RenderSwizzle::G,
                                       plume::RenderSwizzle::B,
                                       plume::RenderSwizzle::A);
}

bool FableSingleSourceVideoUsesDecodedChannels(uint32_t guest_format) {
  namespace xenos = rex::graphics::xenos;
  switch (static_cast<xenos::TextureFormat>(guest_format & 0x3Fu)) {
    case xenos::TextureFormat::k_8_8_8_8:
    case xenos::TextureFormat::k_8_8_8_8_A:
    case xenos::TextureFormat::k_8_8_8_8_AS_16_16_16_16:
    case xenos::TextureFormat::k_8_8_8_8_GAMMA_EDRAM:
    case xenos::TextureFormat::k_Cr_Y1_Cb_Y0_REP:
    case xenos::TextureFormat::k_Y1_Cr_Y0_Cb_REP:
      return true;
    default:
      return false;
  }
}

bool IsLikelyFableVideoNeutralChromaTexture(
    const RendererTrackedResourceSnapshot& snapshot,
    const PlumeTrackedResource& tracked) {
  namespace xenos = rex::graphics::xenos;
  return snapshot.kind == RendererTrackedResourceKind::kTexture &&
         tracked.texture_width == 1 && tracked.texture_height == 1 &&
         tracked.texture_depth == 1 &&
         tracked.texture_format == plume::RenderFormat::R8G8B8A8_UNORM &&
         static_cast<xenos::TextureFormat>(tracked.texture_guest_format & 0x3Fu) ==
             xenos::TextureFormat::k_8_8_8_8 &&
         snapshot.raw_packed36 == 0 && snapshot.raw_packed44 == 3;
}

uint32_t PackTextureComponentMapping(
    const plume::RenderComponentMapping& mapping) {
  return static_cast<uint32_t>(mapping.r) |
         (static_cast<uint32_t>(mapping.g) << 4u) |
         (static_cast<uint32_t>(mapping.b) << 8u) |
         (static_cast<uint32_t>(mapping.a) << 12u);
}

uint32_t DrawSlotTextureFetchSwizzle(
    const RendererDrawEventSnapshot& draw_event, uint32_t slot) {
  namespace xenos = rex::graphics::xenos;
  constexpr uint32_t kTextureFetchConstantDwords = 6;

  const auto swizzle_from_dwords =
      [](const std::array<uint32_t, 6>& dwords) -> uint32_t {
    namespace xenos = rex::graphics::xenos;
    xenos::xe_gpu_texture_fetch_t fetch = {};
    fetch.dword_0 = dwords[0];
    fetch.dword_1 = dwords[1];
    fetch.dword_2 = dwords[2];
    fetch.dword_3 = dwords[3];
    fetch.dword_4 = dwords[4];
    fetch.dword_5 = dwords[5];
    if ((fetch.dword_0 | fetch.dword_1 | fetch.dword_2 | fetch.dword_3 |
         fetch.dword_4 | fetch.dword_5) == 0 ||
        (fetch.type != xenos::FetchConstantType::kTexture &&
         fetch.type != xenos::FetchConstantType::kInvalidTexture)) {
      return xenos::XE_GPU_TEXTURE_SWIZZLE_RGBA;
    }
    return fetch.swizzle;
  };

  const uint32_t fetch_dword = slot * kTextureFetchConstantDwords;
  if (slot < 32u &&
      fetch_dword + kTextureFetchConstantDwords <=
          draw_event.fetch_constants.size()) {
    const std::array<uint32_t, 6> slot_fetch = {
        draw_event.fetch_constants[fetch_dword + 0u],
        draw_event.fetch_constants[fetch_dword + 1u],
        draw_event.fetch_constants[fetch_dword + 2u],
        draw_event.fetch_constants[fetch_dword + 3u],
        draw_event.fetch_constants[fetch_dword + 4u],
        draw_event.fetch_constants[fetch_dword + 5u],
    };
    if (std::any_of(slot_fetch.begin(), slot_fetch.end(),
                    [](uint32_t dword) { return dword != 0; })) {
      return swizzle_from_dwords(slot_fetch);
    }
  }

  if (slot < std::size(draw_event.textures)) {
    const PlumeTrackedResource* object_texture =
        FindTrackedSampledImageByObjectAllowDepthLocked(draw_event.textures[slot]);
    if (object_texture &&
        std::any_of(object_texture->texture_fetch_dwords.begin(),
                    object_texture->texture_fetch_dwords.end(),
                    [](uint32_t dword) { return dword != 0; })) {
      return swizzle_from_dwords(object_texture->texture_fetch_dwords);
    }
  }

  return xenos::XE_GPU_TEXTURE_SWIZZLE_RGBA;
}

plume::RenderTextureView* TextureViewForFableMappingLocked(
    const PlumeTrackedResource* resource,
    const plume::RenderComponentMapping& mapping);

plume::RenderTextureView* TextureViewForFableDrawSlotLocked(
    const PlumeTrackedResource* resource,
    const RendererDrawEventSnapshot& draw_event, uint32_t slot) {
  if (!resource || !resource->texture || !resource->texture_view) {
    return nullptr;
  }

  const plume::RenderComponentMapping mapping = XenosTextureComponentMapping(
      resource->texture_guest_format,
      DrawSlotTextureFetchSwizzle(draw_event, slot));
  return TextureViewForFableMappingLocked(resource, mapping);
}

plume::RenderTextureView* TextureViewForFableMappingLocked(
    const PlumeTrackedResource* resource,
    const plume::RenderComponentMapping& mapping) {
  if (!resource || !resource->texture || !resource->texture_view) {
    return nullptr;
  }

  const uint32_t mapping_key = PackTextureComponentMapping(mapping);
  const uint32_t base_mapping_key = PackTextureComponentMapping(
      XenosTextureComponentMapping(
          resource->texture_guest_format,
          rex::graphics::xenos::XE_GPU_TEXTURE_SWIZZLE_RGBA));
  if (mapping_key == base_mapping_key) {
    return resource->texture_view.get();
  }

  auto view_it = resource->texture_mapped_views.find(mapping_key);
  if (view_it != resource->texture_mapped_views.end()) {
    return view_it->second.get();
  }

  plume::RenderTextureViewDesc view_desc = TextureViewDescForDimension(
      resource->texture_view_dimension, resource->texture_format);
  view_desc.componentMapping = mapping;
  std::unique_ptr<plume::RenderTextureView> view =
      resource->texture->createTextureView(view_desc);
  plume::RenderTextureView* view_ptr = view.get();
  if (view_ptr) {
    resource->texture_mapped_views.emplace(mapping_key, std::move(view));
    return view_ptr;
  }
  return resource->texture_view.get();
}

plume::RenderTextureView* TextureViewForFablePixVideoPlaneLocked(
    const PlumeTrackedResource* resource,
    const RendererDrawEventSnapshot& draw_event, uint32_t source_slot,
    uint32_t plane_slot) {
  if (!resource || !resource->texture || !resource->texture_view) {
    return nullptr;
  }

  namespace xenos = rex::graphics::xenos;
  plume::RenderComponentMapping mapping =
      FableSingleSourceVideoUsesDecodedChannels(resource->texture_guest_format)
          ? IdentityTextureComponentMapping()
          : XenosTextureComponentMapping(
                resource->texture_guest_format,
                DrawSlotTextureFetchSwizzle(draw_event, source_slot));
  if (resource->texture_format == plume::RenderFormat::R8_UNORM &&
      IsLikelyFablePixDecodedVideoPlaneResource(resource)) {
    mapping.a = plume::RenderSwizzle::R;
    return TextureViewForFableMappingLocked(resource, mapping);
  }
  switch (plane_slot) {
    case 0:  // g_SamplerY reads .w.
      mapping.a = mapping.r;
      break;
    case 1:  // g_SamplerCR reads .w.
      mapping.a = mapping.g;
      break;
    case 2:  // g_SamplerCB reads .w.
      mapping.a = mapping.b;
      break;
    default:
      break;
  }
  return TextureViewForFableMappingLocked(resource, mapping);
}

plume::RenderTextureView* TextureViewForFableColorSampleCopyPlaneLocked(
    const PlumeColorSampleCopy* copy, uint32_t plane_slot) {
  if (!copy || !copy->texture || !copy->view || plane_slot >= 3u) {
    return nullptr;
  }

  plume::RenderComponentMapping mapping = IdentityTextureComponentMapping();
  switch (plane_slot) {
    case 0:  // g_SamplerY reads .w.
      mapping.a = plume::RenderSwizzle::R;
      break;
    case 1:  // g_SamplerCR reads .w.
      mapping.a = plume::RenderSwizzle::G;
      break;
    case 2:  // g_SamplerCB reads .w.
      mapping.a = plume::RenderSwizzle::B;
      break;
    default:
      break;
  }

  std::unique_ptr<plume::RenderTextureView>& view =
      copy->plane_views[plane_slot];
  if (view) {
    return view.get();
  }

  plume::RenderTextureViewDesc view_desc =
      plume::RenderTextureViewDesc::Texture2D(copy->format);
  view_desc.componentMapping = mapping;
  view = copy->texture->createTextureView(view_desc);
  return view ? view.get() : copy->view.get();
}

plume::RenderTextureView* TextureViewForFableVideoPlaneSlotLocked(
    const PlumeTrackedResource* resource,
    const RendererDrawEventSnapshot& draw_event, uint32_t slot) {
  if (!resource || !resource->texture || !resource->texture_view) {
    return nullptr;
  }

  return TextureViewForFableDrawSlotLocked(resource, draw_event, slot);
}

uint32_t ClampGuestTextureExtent(uint32_t extent) {
  return std::clamp<uint32_t>(extent, 1, 4096);
}

plume::RenderFormat ConvertXenosTextureFormat(
    rex::graphics::xenos::TextureFormat format) {
  namespace xenos = rex::graphics::xenos;
  switch (format) {
    case xenos::TextureFormat::k_8:
    case xenos::TextureFormat::k_8_A:
    case xenos::TextureFormat::k_8_B:
      return plume::RenderFormat::R8_UNORM;
    case xenos::TextureFormat::k_8_8:
    case xenos::TextureFormat::k_CTX1:
      return plume::RenderFormat::R8G8_UNORM;
    case xenos::TextureFormat::k_8_8_8_8:
    case xenos::TextureFormat::k_8_8_8_8_A:
    case xenos::TextureFormat::k_8_8_8_8_AS_16_16_16_16:
    case xenos::TextureFormat::k_8_8_8_8_GAMMA_EDRAM:
      return plume::RenderFormat::R8G8B8A8_UNORM;
    case xenos::TextureFormat::k_1_5_5_5:
    case xenos::TextureFormat::k_5_6_5:
    case xenos::TextureFormat::k_4_4_4_4:
      return plume::RenderFormat::R8G8B8A8_UNORM;
    case xenos::TextureFormat::k_2_10_10_10:
    case xenos::TextureFormat::k_2_10_10_10_AS_16_16_16_16:
      return plume::RenderFormat::R10G10B10A2_UNORM;
    case xenos::TextureFormat::k_DXT1:
    case xenos::TextureFormat::k_DXT1_AS_16_16_16_16:
      return plume::RenderFormat::BC1_UNORM;
    case xenos::TextureFormat::k_DXT2_3:
    case xenos::TextureFormat::k_DXT2_3_AS_16_16_16_16:
      return plume::RenderFormat::BC2_UNORM;
    case xenos::TextureFormat::k_DXT4_5:
    case xenos::TextureFormat::k_DXT4_5_AS_16_16_16_16:
      return plume::RenderFormat::BC3_UNORM;
    case xenos::TextureFormat::k_DXN:
      return plume::RenderFormat::BC5_UNORM;
    case xenos::TextureFormat::k_16:
    case xenos::TextureFormat::k_16_EXPAND:
      return plume::RenderFormat::R16_UNORM;
    case xenos::TextureFormat::k_16_16:
    case xenos::TextureFormat::k_16_16_EXPAND:
      return plume::RenderFormat::R16G16_UNORM;
    case xenos::TextureFormat::k_16_16_16_16:
    case xenos::TextureFormat::k_16_16_16_16_EXPAND:
      return plume::RenderFormat::R16G16B16A16_UNORM;
    case xenos::TextureFormat::k_DXT3A:
    case xenos::TextureFormat::k_DXT3A_AS_1_1_1_1:
      return plume::RenderFormat::R8_UNORM;
    case xenos::TextureFormat::k_DXT5A:
      return plume::RenderFormat::BC4_UNORM;
    case xenos::TextureFormat::k_16_FLOAT:
      return plume::RenderFormat::R16_FLOAT;
    case xenos::TextureFormat::k_16_16_FLOAT:
      return plume::RenderFormat::R16G16_FLOAT;
    case xenos::TextureFormat::k_16_16_16_16_FLOAT:
      return plume::RenderFormat::R16G16B16A16_FLOAT;
    case xenos::TextureFormat::k_32:
      return plume::RenderFormat::R32_UINT;
    case xenos::TextureFormat::k_32_32:
      return plume::RenderFormat::R32G32_UINT;
    case xenos::TextureFormat::k_32_32_32_32:
      return plume::RenderFormat::R32G32B32A32_UINT;
    case xenos::TextureFormat::k_32_FLOAT:
      return plume::RenderFormat::R32_FLOAT;
    case xenos::TextureFormat::k_32_32_FLOAT:
      return plume::RenderFormat::R32G32_FLOAT;
    case xenos::TextureFormat::k_32_32_32_32_FLOAT:
      return plume::RenderFormat::R32G32B32A32_FLOAT;
    case xenos::TextureFormat::k_24_8:
    case xenos::TextureFormat::k_24_8_FLOAT:
      return plume::RenderFormat::R32_FLOAT;
    case xenos::TextureFormat::k_Cr_Y1_Cb_Y0_REP:
    case xenos::TextureFormat::k_Y1_Cr_Y0_Cb_REP:
      return plume::RenderFormat::R8G8B8A8_UNORM;
    default:
      return plume::RenderFormat::R8G8B8A8_UNORM;
  }
}

plume::RenderFormat ConvertXenosColorRenderTargetFormat(
    rex::graphics::xenos::ColorRenderTargetFormat format) {
  namespace xenos = rex::graphics::xenos;
  switch (xenos::GetStorageColorFormat(format)) {
    case xenos::ColorRenderTargetFormat::k_8_8_8_8:
    case xenos::ColorRenderTargetFormat::k_8_8_8_8_GAMMA:
      return plume::RenderFormat::R8G8B8A8_UNORM;
    case xenos::ColorRenderTargetFormat::k_2_10_10_10:
      return plume::RenderFormat::R10G10B10A2_UNORM;
    case xenos::ColorRenderTargetFormat::k_2_10_10_10_FLOAT:
      return plume::RenderFormat::R16G16B16A16_FLOAT;
    case xenos::ColorRenderTargetFormat::k_16_16:
      return plume::RenderFormat::R16G16_SNORM;
    case xenos::ColorRenderTargetFormat::k_16_16_16_16:
      return plume::RenderFormat::R16G16B16A16_SNORM;
    case xenos::ColorRenderTargetFormat::k_16_16_FLOAT:
      return plume::RenderFormat::R16G16_FLOAT;
    case xenos::ColorRenderTargetFormat::k_16_16_16_16_FLOAT:
      return plume::RenderFormat::R16G16B16A16_FLOAT;
    case xenos::ColorRenderTargetFormat::k_32_FLOAT:
      return plume::RenderFormat::R32_FLOAT;
    case xenos::ColorRenderTargetFormat::k_32_32_FLOAT:
      return plume::RenderFormat::R32G32_FLOAT;
    default:
      return plume::RenderFormat::R8G8B8A8_UNORM;
  }
}

plume::RenderFormat DecodeFableDepthSurfaceFormat(
    const RendererTrackedResourceSnapshot& snapshot,
    uint32_t surface_d3d_format) {
  const uint32_t d3d_format =
      surface_d3d_format != 0 ? (surface_d3d_format & 0x3Fu)
                              : (snapshot.raw_packed40 & 0x3Fu);
  return ConvertFableD3DDepthFormat(d3d_format);
}

bool IsBlockCompressedFormat(plume::RenderFormat format) {
  switch (format) {
    case plume::RenderFormat::BC1_TYPELESS:
    case plume::RenderFormat::BC1_UNORM:
    case plume::RenderFormat::BC1_UNORM_SRGB:
    case plume::RenderFormat::BC2_TYPELESS:
    case plume::RenderFormat::BC2_UNORM:
    case plume::RenderFormat::BC2_UNORM_SRGB:
    case plume::RenderFormat::BC3_TYPELESS:
    case plume::RenderFormat::BC3_UNORM:
    case plume::RenderFormat::BC3_UNORM_SRGB:
    case plume::RenderFormat::BC4_TYPELESS:
    case plume::RenderFormat::BC4_UNORM:
    case plume::RenderFormat::BC4_SNORM:
    case plume::RenderFormat::BC5_TYPELESS:
    case plume::RenderFormat::BC5_UNORM:
    case plume::RenderFormat::BC5_SNORM:
    case plume::RenderFormat::BC6H_TYPELESS:
    case plume::RenderFormat::BC6H_UF16:
    case plume::RenderFormat::BC6H_SF16:
    case plume::RenderFormat::BC7_TYPELESS:
    case plume::RenderFormat::BC7_UNORM:
    case plume::RenderFormat::BC7_UNORM_SRGB:
      return true;
    default:
      return false;
  }
}

uint32_t TextureBlockWidth(plume::RenderFormat format) {
  return std::max<uint32_t>(1, plume::RenderFormatBlockWidth(format));
}

uint32_t TextureBlockHeight(plume::RenderFormat format) {
  return IsBlockCompressedFormat(format) ? 4u : 1u;
}

uint32_t TextureRowCount(plume::RenderFormat format, uint32_t height) {
  const uint32_t block_height = TextureBlockHeight(format);
  return std::max<uint32_t>(1, (height + block_height - 1u) / block_height);
}

uint32_t TextureTightRowBytes(plume::RenderFormat format, uint32_t width) {
  const uint32_t block_width = TextureBlockWidth(format);
  const uint32_t block_count =
      std::max<uint32_t>(1, (width + block_width - 1u) / block_width);
  return block_count * plume::RenderFormatSize(format);
}

uint32_t TextureRowWidthForPitch(plume::RenderFormat format,
                                 uint32_t row_pitch_bytes) {
  const uint32_t format_size = std::max<uint32_t>(1, plume::RenderFormatSize(format));
  const uint32_t block_width = TextureBlockWidth(format);
  return std::max<uint32_t>(block_width,
                            (row_pitch_bytes / format_size) * block_width);
}

uint8_t ExpandUnormBits(uint32_t value, uint32_t bits) {
  const uint32_t max_value = (1u << bits) - 1u;
  return static_cast<uint8_t>((value * 255u + max_value / 2u) / max_value);
}

bool DecodePacked16XenosTexel(rex::graphics::xenos::TextureFormat format,
                              uint16_t texel, uint8_t* rgba) {
  namespace xenos = rex::graphics::xenos;
  switch (format) {
    case xenos::TextureFormat::k_1_5_5_5:
      rgba[0] = ExpandUnormBits(texel & 0x1Fu, 5);
      rgba[1] = ExpandUnormBits((texel >> 5u) & 0x1Fu, 5);
      rgba[2] = ExpandUnormBits((texel >> 10u) & 0x1Fu, 5);
      rgba[3] = (texel & 0x8000u) != 0 ? 0xFFu : 0x00u;
      return true;
    case xenos::TextureFormat::k_5_6_5:
      rgba[0] = ExpandUnormBits(texel & 0x1Fu, 5);
      rgba[1] = ExpandUnormBits((texel >> 5u) & 0x3Fu, 6);
      rgba[2] = ExpandUnormBits((texel >> 11u) & 0x1Fu, 5);
      rgba[3] = 0xFFu;
      return true;
    case xenos::TextureFormat::k_4_4_4_4:
      rgba[0] = ExpandUnormBits(texel & 0xFu, 4);
      rgba[1] = ExpandUnormBits((texel >> 4u) & 0xFu, 4);
      rgba[2] = ExpandUnormBits((texel >> 8u) & 0xFu, 4);
      rgba[3] = ExpandUnormBits((texel >> 12u) & 0xFu, 4);
      return true;
    default:
      return false;
  }
}

bool IsPacked16XenosTextureFormat(
    rex::graphics::xenos::TextureFormat format) {
  namespace xenos = rex::graphics::xenos;
  return format == xenos::TextureFormat::k_1_5_5_5 ||
         format == xenos::TextureFormat::k_5_6_5 ||
         format == xenos::TextureFormat::k_4_4_4_4;
}

bool DecodeGuestTextureBaseLevel(const RendererTrackedResourceSnapshot& snapshot,
                                 const PlumeTrackedResource& tracked,
                                 std::vector<uint8_t>& decoded_bytes) {
  decoded_bytes.clear();
  if (snapshot.kind != RendererTrackedResourceKind::kTexture ||
      snapshot.upload_data.empty() || tracked.texture_width == 0 ||
      tracked.texture_height == 0 || tracked.texture_depth == 0) {
    return false;
  }

  namespace xenos = rex::graphics::xenos;
  namespace texture_conversion = rex::graphics::texture_conversion;
  namespace texture_util = rex::graphics::texture_util;

  const std::array<uint32_t, 6> raw_fetch_dwords = {
      snapshot.raw_packed28, snapshot.raw_packed32, snapshot.raw_packed36,
      snapshot.raw_packed40, snapshot.raw_packed44, snapshot.raw_packed48};
  const std::array<uint32_t, 6>& fetch_dwords =
      std::any_of(snapshot.normalized_fetch_dwords.begin(),
                  snapshot.normalized_fetch_dwords.end(),
                  [](uint32_t dword) { return dword != 0; })
          ? snapshot.normalized_fetch_dwords
          : raw_fetch_dwords;

  xenos::xe_gpu_texture_fetch_t fetch = {};
  fetch.dword_0 = fetch_dwords[0];
  fetch.dword_1 = fetch_dwords[1];
  fetch.dword_2 = fetch_dwords[2];
  fetch.dword_3 = fetch_dwords[3];
  fetch.dword_4 = fetch_dwords[4];
  fetch.dword_5 = fetch_dwords[5];
  if (fetch.type != xenos::FetchConstantType::kTexture &&
      fetch.type != xenos::FetchConstantType::kInvalidTexture) {
    return false;
  }

  auto decode_packed_yuv = [&]() -> bool {
    const bool cr_y1_cb_y0 =
        fetch.format == xenos::TextureFormat::k_Cr_Y1_Cb_Y0_REP;
    const bool y1_cr_y0_cb =
        fetch.format == xenos::TextureFormat::k_Y1_Cr_Y0_Cb_REP;
    if (!cr_y1_cb_y0 && !y1_cr_y0_cb) {
      return false;
    }
    if (tracked.texture_format != plume::RenderFormat::R8G8B8A8_UNORM ||
        tracked.texture_width == 0 || tracked.texture_height == 0 ||
        tracked.texture_depth != 1 ||
        tracked.texture_view_dimension !=
            plume::RenderTextureViewDimension::TEXTURE_2D) {
      return false;
    }

    const rex::graphics::FormatInfo* guest_format_info =
        rex::graphics::FormatInfo::Get(fetch.format);
    if (!guest_format_info || guest_format_info->bytes_per_block() != 4 ||
        guest_format_info->block_width != 2 ||
        guest_format_info->block_height != 1) {
      return false;
    }

    const uint32_t width_blocks =
        std::max<uint32_t>(1, (tracked.texture_width + 1u) / 2u);
    const uint32_t height_blocks = tracked.texture_height;
    const uint64_t decoded_size =
        uint64_t(tracked.texture_width) * tracked.texture_height * 4u;
    if (decoded_size == 0 || decoded_size > kMaxTextureUploadBufferSize) {
      return false;
    }

    const texture_util::TextureGuestLayout layout =
        texture_util::GetGuestTextureLayout(
            fetch.dimension, fetch.pitch, tracked.texture_width,
            tracked.texture_height, 1, fetch.tiled != 0, fetch.format,
            fetch.packed_mips != 0, true, 0);
    if (layout.base.row_pitch_bytes == 0 ||
        layout.base.array_slice_stride_bytes == 0) {
      return false;
    }

    uint32_t guest_pitch_blocks =
        layout.base.row_pitch_bytes / guest_format_info->bytes_per_block();
    guest_pitch_blocks = std::max<uint32_t>(guest_pitch_blocks, width_blocks);
    constexpr uint32_t kGuestBytesPerBlock = 4;
    constexpr uint32_t kGuestBytesPerBlockLog2 = 2;

    decoded_bytes.assign(static_cast<size_t>(decoded_size), 0xFF);
    const uint8_t* source = snapshot.upload_data.data();
    const size_t source_size = snapshot.upload_data.size();
    for (uint32_t y = 0; y < height_blocks; ++y) {
      for (uint32_t block_x = 0; block_x < width_blocks; ++block_x) {
        uint64_t source_offset = 0;
        if (fetch.tiled) {
          source_offset = static_cast<uint32_t>(
              texture_util::GetTiledOffset2D(
                  static_cast<int32_t>(block_x), static_cast<int32_t>(y),
                  guest_pitch_blocks, kGuestBytesPerBlockLog2));
        } else {
          source_offset = uint64_t(y) * layout.base.row_pitch_bytes +
                          uint64_t(block_x) * kGuestBytesPerBlock;
        }
        if (source_offset + kGuestBytesPerBlock > source_size) {
          continue;
        }

        uint8_t block[4] = {};
        texture_conversion::CopySwapBlock(fetch.endianness, block,
                                          source + source_offset,
                                          kGuestBytesPerBlock);
        uint8_t cr = 0;
        uint8_t cb = 0;
        uint8_t y0 = 0;
        uint8_t y1 = 0;
        if (cr_y1_cb_y0) {
          cr = block[0];
          y1 = block[1];
          cb = block[2];
          y0 = block[3];
        } else {
          y1 = block[0];
          cr = block[1];
          y0 = block[2];
          cb = block[3];
        }

        const uint32_t pixel_x = block_x * 2u;
        const auto write_pixel = [&](uint32_t x, uint8_t y_value) {
          if (x >= tracked.texture_width) {
            return;
          }
          const uint64_t decoded_offset =
              (uint64_t(y) * tracked.texture_width + x) * 4u;
          if (decoded_offset + 4u > decoded_size) {
            return;
          }
          decoded_bytes[decoded_offset + 0u] = y_value;
          decoded_bytes[decoded_offset + 1u] = cr;
          decoded_bytes[decoded_offset + 2u] = cb;
          decoded_bytes[decoded_offset + 3u] = 0xFFu;
        };
        write_pixel(pixel_x + 0u, y0);
        write_pixel(pixel_x + 1u, y1);
      }
    }
    return true;
  };

  if (decode_packed_yuv()) {
    return true;
  }

  const rex::graphics::FormatInfo* guest_format_info =
      rex::graphics::FormatInfo::Get(fetch.format);
  if (!guest_format_info) {
    return false;
  }

  auto decode_packed_16_to_rgba8 = [&]() -> bool {
    if (tracked.texture_format != plume::RenderFormat::R8G8B8A8_UNORM ||
        guest_format_info->bytes_per_block() != 2 ||
        guest_format_info->block_width != 1 ||
        guest_format_info->block_height != 1 ||
        !IsPacked16XenosTextureFormat(fetch.format)) {
      return false;
    }

    const uint32_t width = tracked.texture_width;
    const uint32_t height = tracked.texture_height;
    const uint32_t depth = std::max<uint32_t>(1, tracked.texture_depth);
    const uint32_t subresource_count =
        tracked.texture_view_dimension ==
                plume::RenderTextureViewDimension::TEXTURE_CUBE
            ? 6u
            : 1u;
    const uint64_t row_bytes = uint64_t(width) * 4u;
    const uint64_t tight_subresource_bytes = row_bytes * height * depth;
    const uint64_t decoded_size = tight_subresource_bytes * subresource_count;
    if (decoded_size == 0 || decoded_size > kMaxTextureUploadBufferSize) {
      return false;
    }

    const uint32_t depth_or_array_size =
        fetch.dimension == xenos::DataDimension::kCube
            ? 6u
            : (fetch.dimension == xenos::DataDimension::k3D ? depth : 1u);
    const texture_util::TextureGuestLayout layout =
        texture_util::GetGuestTextureLayout(
            fetch.dimension, fetch.pitch, width, height, depth_or_array_size,
            fetch.tiled != 0, fetch.format, fetch.packed_mips != 0, true, 0);
    if (layout.base.row_pitch_bytes == 0 ||
        layout.base.array_slice_stride_bytes == 0) {
      return false;
    }

    constexpr uint32_t kGuestBytesPerBlock = 2u;
    constexpr uint32_t kGuestBytesPerBlockLog2 = 1u;
    uint32_t guest_pitch_blocks =
        layout.base.row_pitch_bytes / kGuestBytesPerBlock;
    guest_pitch_blocks = std::max<uint32_t>(guest_pitch_blocks, width);
    const uint32_t guest_z_slice_stride_bytes =
        layout.base.row_pitch_bytes *
        std::max<uint32_t>(1, layout.base.z_slice_stride_block_rows);

    decoded_bytes.assign(static_cast<size_t>(decoded_size), 0xFF);
    const uint8_t* source = snapshot.upload_data.data();
    const size_t source_size = snapshot.upload_data.size();
    for (uint32_t subresource = 0; subresource < subresource_count;
         ++subresource) {
      const uint64_t source_subresource_base =
          uint64_t(subresource) * layout.base.array_slice_stride_bytes;
      const uint64_t decoded_subresource_base =
          uint64_t(subresource) * tight_subresource_bytes;
      if (source_subresource_base >= source_size ||
          decoded_subresource_base >= decoded_size) {
        continue;
      }

      for (uint32_t z = 0; z < depth; ++z) {
        for (uint32_t y = 0; y < height; ++y) {
          for (uint32_t x = 0; x < width; ++x) {
            uint64_t source_offset = source_subresource_base;
            if (fetch.tiled) {
              if (fetch.dimension == xenos::DataDimension::k3D) {
                source_offset += static_cast<uint32_t>(
                    texture_util::GetTiledOffset3D(
                        static_cast<int32_t>(x), static_cast<int32_t>(y),
                        static_cast<int32_t>(z), guest_pitch_blocks,
                        std::max<uint32_t>(
                            height, layout.base.z_slice_stride_block_rows),
                        kGuestBytesPerBlockLog2));
              } else {
                source_offset += static_cast<uint32_t>(
                    texture_util::GetTiledOffset2D(
                        static_cast<int32_t>(x), static_cast<int32_t>(y),
                        guest_pitch_blocks, kGuestBytesPerBlockLog2));
              }
            } else {
              source_offset += uint64_t(z) * guest_z_slice_stride_bytes +
                               uint64_t(y) * layout.base.row_pitch_bytes +
                               uint64_t(x) * kGuestBytesPerBlock;
            }

            const uint64_t decoded_offset =
                decoded_subresource_base +
                (uint64_t(z) * height + y) * row_bytes + uint64_t(x) * 4u;
            if (source_offset + kGuestBytesPerBlock > source_size ||
                decoded_offset + 4u > decoded_size) {
              continue;
            }
            uint8_t block[2] = {};
            texture_conversion::CopySwapBlock(fetch.endianness, block,
                                              source + source_offset,
                                              kGuestBytesPerBlock);
            const uint16_t texel =
                static_cast<uint16_t>(block[0]) |
                (static_cast<uint16_t>(block[1]) << 8u);
            DecodePacked16XenosTexel(fetch.format, texel,
                                     decoded_bytes.data() + decoded_offset);
          }
        }
      }
    }
    return true;
  };

  if (decode_packed_16_to_rgba8()) {
    return true;
  }

  auto decode_dxt3a_to_r8 = [&]() -> bool {
    if ((fetch.format != xenos::TextureFormat::k_DXT3A &&
         fetch.format != xenos::TextureFormat::k_DXT3A_AS_1_1_1_1) ||
        tracked.texture_format != plume::RenderFormat::R8_UNORM ||
        guest_format_info->bytes_per_block() != 8 ||
        guest_format_info->block_width != 4 ||
        guest_format_info->block_height != 4 ||
        tracked.texture_view_dimension !=
            plume::RenderTextureViewDimension::TEXTURE_2D) {
      return false;
    }

    const uint32_t width = tracked.texture_width;
    const uint32_t height = tracked.texture_height;
    const uint32_t width_blocks = std::max<uint32_t>(1, (width + 3u) / 4u);
    const uint32_t height_blocks = std::max<uint32_t>(1, (height + 3u) / 4u);
    const uint64_t row_bytes = width;
    const uint64_t decoded_size = row_bytes * height;
    if (decoded_size == 0 || decoded_size > kMaxTextureUploadBufferSize) {
      return false;
    }

    const texture_util::TextureGuestLayout layout =
        texture_util::GetGuestTextureLayout(
            fetch.dimension, fetch.pitch, width, height, 1,
            fetch.tiled != 0, fetch.format, fetch.packed_mips != 0, true, 0);
    if (layout.base.row_pitch_bytes == 0 ||
        layout.base.array_slice_stride_bytes == 0) {
      return false;
    }

    constexpr uint32_t kGuestBytesPerBlock = 8u;
    constexpr uint32_t kGuestBytesPerBlockLog2 = 3u;
    uint32_t guest_pitch_blocks =
        layout.base.row_pitch_bytes / kGuestBytesPerBlock;
    guest_pitch_blocks =
        std::max<uint32_t>(guest_pitch_blocks, width_blocks);

    decoded_bytes.assign(static_cast<size_t>(decoded_size), 0);
    const uint8_t* source = snapshot.upload_data.data();
    const size_t source_size = snapshot.upload_data.size();
    for (uint32_t block_y = 0; block_y < height_blocks; ++block_y) {
      for (uint32_t block_x = 0; block_x < width_blocks; ++block_x) {
        uint64_t source_offset = 0;
        if (fetch.tiled) {
          source_offset = static_cast<uint32_t>(
              texture_util::GetTiledOffset2D(
                  static_cast<int32_t>(block_x),
                  static_cast<int32_t>(block_y), guest_pitch_blocks,
                  kGuestBytesPerBlockLog2));
        } else {
          source_offset = uint64_t(block_y) * layout.base.row_pitch_bytes +
                          uint64_t(block_x) * kGuestBytesPerBlock;
        }
        if (source_offset + kGuestBytesPerBlock > source_size) {
          continue;
        }

        uint8_t block[8] = {};
        texture_conversion::CopySwapBlock(fetch.endianness, block,
                                          source + source_offset,
                                          kGuestBytesPerBlock);
        for (uint32_t local_y = 0; local_y < 4u; ++local_y) {
          const uint32_t y = block_y * 4u + local_y;
          if (y >= height) {
            continue;
          }
          for (uint32_t local_x = 0; local_x < 4u; ++local_x) {
            const uint32_t x = block_x * 4u + local_x;
            if (x >= width) {
              continue;
            }
            const uint32_t texel = local_y * 4u + local_x;
            const uint8_t packed_alpha =
                block[texel >> 1u] >> ((texel & 1u) * 4u);
            const uint8_t alpha4 = packed_alpha & 0xFu;
            decoded_bytes[uint64_t(y) * row_bytes + x] =
                static_cast<uint8_t>((alpha4 << 4u) | alpha4);
          }
        }
      }
    }
    return true;
  };

  if (decode_dxt3a_to_r8()) {
    return true;
  }

  const uint32_t guest_bytes_per_block =
      guest_format_info->bytes_per_block();
  const uint32_t host_bytes_per_block =
      plume::RenderFormatSize(tracked.texture_format);
  const uint32_t host_block_width = TextureBlockWidth(tracked.texture_format);
  const uint32_t host_block_height = TextureBlockHeight(tracked.texture_format);
  if (guest_bytes_per_block == 0 || host_bytes_per_block == 0 ||
      guest_bytes_per_block != host_bytes_per_block ||
      guest_format_info->block_width != host_block_width ||
      guest_format_info->block_height != host_block_height) {
    return false;
  }

  const uint32_t width_blocks =
      std::max<uint32_t>(1, (tracked.texture_width +
                             guest_format_info->block_width - 1u) /
                                guest_format_info->block_width);
  const uint32_t height_blocks =
      std::max<uint32_t>(1, (tracked.texture_height +
                             guest_format_info->block_height - 1u) /
                                guest_format_info->block_height);
  const uint32_t depth = std::max<uint32_t>(1, tracked.texture_depth);
  const uint32_t subresource_count =
      tracked.texture_view_dimension ==
              plume::RenderTextureViewDimension::TEXTURE_CUBE
          ? 6u
          : 1u;
  const uint64_t row_bytes = uint64_t(width_blocks) * host_bytes_per_block;
  const uint64_t tight_subresource_bytes = row_bytes * height_blocks * depth;
  const uint64_t decoded_size = tight_subresource_bytes * subresource_count;
  if (decoded_size == 0 || decoded_size > kMaxTextureUploadBufferSize) {
    return false;
  }

  const uint32_t depth_or_array_size =
      fetch.dimension == xenos::DataDimension::kCube
          ? 6u
          : (fetch.dimension == xenos::DataDimension::k3D ? depth : 1u);
  const texture_util::TextureGuestLayout layout =
      texture_util::GetGuestTextureLayout(
          fetch.dimension, fetch.pitch, tracked.texture_width,
          tracked.texture_height, depth_or_array_size, fetch.tiled != 0,
          fetch.format, fetch.packed_mips != 0, true, 0);
  if (layout.base.row_pitch_bytes == 0 ||
      layout.base.array_slice_stride_bytes == 0) {
    return false;
  }

  uint32_t guest_pitch_blocks =
      layout.base.row_pitch_bytes / guest_bytes_per_block;
  guest_pitch_blocks =
      std::max<uint32_t>(guest_pitch_blocks, width_blocks);
  const uint32_t guest_z_slice_stride_bytes =
      layout.base.row_pitch_bytes *
      std::max<uint32_t>(1, layout.base.z_slice_stride_block_rows);
  if (fetch.tiled && !IsPowerOfTwo(guest_bytes_per_block)) {
    return false;
  }
  const uint32_t guest_bytes_per_block_log2 =
      rex::log2_floor(guest_bytes_per_block);

  decoded_bytes.assign(static_cast<size_t>(decoded_size), 0xFF);
  const uint8_t* source = snapshot.upload_data.data();
  const size_t source_size = snapshot.upload_data.size();
  for (uint32_t subresource = 0; subresource < subresource_count;
       ++subresource) {
    const uint64_t source_subresource_base =
        uint64_t(subresource) * layout.base.array_slice_stride_bytes;
    const uint64_t decoded_subresource_base =
        uint64_t(subresource) * tight_subresource_bytes;
    if (source_subresource_base >= source_size ||
        decoded_subresource_base >= decoded_size) {
      continue;
    }

    for (uint32_t z = 0; z < depth; ++z) {
      for (uint32_t y = 0; y < height_blocks; ++y) {
        for (uint32_t x = 0; x < width_blocks; ++x) {
          uint64_t source_offset = source_subresource_base;
          if (fetch.tiled) {
            if (fetch.dimension == xenos::DataDimension::k3D) {
              source_offset += static_cast<uint32_t>(
                  texture_util::GetTiledOffset3D(
                      static_cast<int32_t>(x), static_cast<int32_t>(y),
                      static_cast<int32_t>(z), guest_pitch_blocks,
                      std::max<uint32_t>(
                          height_blocks,
                          layout.base.z_slice_stride_block_rows),
                      guest_bytes_per_block_log2));
            } else {
              source_offset += static_cast<uint32_t>(
                  texture_util::GetTiledOffset2D(
                      static_cast<int32_t>(x), static_cast<int32_t>(y),
                      guest_pitch_blocks, guest_bytes_per_block_log2));
            }
          } else {
            source_offset += uint64_t(z) * guest_z_slice_stride_bytes +
                             uint64_t(y) * layout.base.row_pitch_bytes +
                             uint64_t(x) * guest_bytes_per_block;
          }

          const uint64_t decoded_offset =
              decoded_subresource_base +
              (uint64_t(z) * height_blocks + y) * row_bytes +
              uint64_t(x) * host_bytes_per_block;
          if (source_offset + guest_bytes_per_block > source_size ||
              decoded_offset + host_bytes_per_block > decoded_size) {
            continue;
          }
          texture_conversion::CopySwapBlock(
              fetch.endianness, decoded_bytes.data() + decoded_offset,
              source + source_offset, host_bytes_per_block);
        }
      }
    }
  }

  return true;
}

bool PrepareTextureUploadBytes(const RendererTrackedResourceSnapshot& snapshot,
                               PlumeTrackedResource& tracked,
                               std::vector<uint8_t>& upload_bytes) {
  upload_bytes.clear();
  if (plume::RenderFormatIsDepth(tracked.texture_format)) {
    tracked.texture_upload_decoded = false;
    tracked.texture_upload_has_source = false;
    return false;
  }
  if (IsSurfaceResource(snapshot.kind) && snapshot.upload_data.empty()) {
    tracked.texture_upload_decoded = false;
    tracked.texture_upload_has_source = false;
    return false;
  }
  const uint32_t row_bytes =
      TextureTightRowBytes(tracked.texture_format, tracked.texture_width);
  const uint32_t row_pitch_bytes = static_cast<uint32_t>(
      AlignUp(row_bytes, kTextureUploadRowAlignment));
  const uint32_t row_count =
      TextureRowCount(tracked.texture_format, tracked.texture_height);
  const uint32_t depth = std::max<uint32_t>(1, tracked.texture_depth);
  const uint32_t subresource_count =
      tracked.texture_view_dimension ==
              plume::RenderTextureViewDimension::TEXTURE_CUBE
          ? 6u
          : 1u;
  const uint64_t tight_subresource_bytes =
      uint64_t(row_bytes) * row_count * depth;
  const uint64_t placed_subresource_bytes =
      AlignUp(uint64_t(row_pitch_bytes) * row_count * depth,
              kTextureUploadPlacementAlignment);
  const uint64_t upload_size = placed_subresource_bytes * subresource_count;
  if (row_bytes == 0 || row_pitch_bytes == 0 || upload_size == 0 ||
      upload_size > kMaxTextureUploadBufferSize) {
    return false;
  }

  upload_bytes.assign(static_cast<size_t>(upload_size), 0xFF);
  std::vector<uint8_t> decoded_source;
  const bool decoded_guest_texture =
      DecodeGuestTextureBaseLevel(snapshot, tracked, decoded_source);
  tracked.texture_upload_decoded = decoded_guest_texture;
  const uint8_t* source =
      decoded_guest_texture
          ? decoded_source.data()
          : (snapshot.upload_data.empty() ? nullptr
                                          : snapshot.upload_data.data());
  const size_t source_size =
      decoded_guest_texture ? decoded_source.size() : snapshot.upload_data.size();
  tracked.texture_upload_has_source = source != nullptr && source_size != 0;
  for (uint32_t subresource = 0; subresource < subresource_count;
       ++subresource) {
    const uint64_t source_base =
        uint64_t(subresource) * tight_subresource_bytes;
    const uint64_t upload_base =
        uint64_t(subresource) * placed_subresource_bytes;
    if (!source || source_base >= source_size) {
      continue;
    }

    for (uint32_t z = 0; z < depth; ++z) {
      for (uint32_t row = 0; row < row_count; ++row) {
        const uint64_t source_offset =
            source_base + (uint64_t(z) * row_count + row) * row_bytes;
        const uint64_t upload_offset =
            upload_base + (uint64_t(z) * row_count + row) * row_pitch_bytes;
        if (source_offset >= source_size || upload_offset >= upload_size) {
          continue;
        }
        const size_t copy_size = static_cast<size_t>(std::min<uint64_t>(
            row_bytes, std::min<uint64_t>(source_size - source_offset,
                                          upload_size - upload_offset)));
        if (copy_size != 0) {
          std::memcpy(upload_bytes.data() + upload_offset,
                      source + source_offset, copy_size);
        }
      }
    }
  }

  if (IsLikelyFableVideoNeutralChromaTexture(snapshot, tracked) &&
      upload_bytes.size() >= 4u) {
    upload_bytes[0] = 0x80u;
    upload_bytes[1] = 0x80u;
    upload_bytes[2] = 0x80u;
    upload_bytes[3] = 0x80u;
  }

  tracked.texture_upload_row_width =
      TextureRowWidthForPitch(tracked.texture_format, row_pitch_bytes);
  tracked.texture_upload_subresources = subresource_count;
  tracked.texture_upload_subresource_bytes = placed_subresource_bytes;
  tracked.texture_upload_size = upload_size;
  if (RendererResourceTracingEnabled() &&
      g_plume_resources.logged_fable_video_texture_upload_samples < 6 &&
      tracked.texture_format == plume::RenderFormat::R8G8B8A8_UNORM &&
      tracked.texture_width >= 640 && tracked.texture_height >= 360 &&
      FableSingleSourceVideoUsesDecodedChannels(
          static_cast<uint32_t>(tracked.texture_guest_format))) {
    const auto pixel_at = [&](uint32_t x, uint32_t y) {
      std::array<uint8_t, 4> pixel = {};
      const uint64_t offset =
          uint64_t(y) * row_pitch_bytes + uint64_t(x) * 4u;
      if (offset + 3u < upload_bytes.size()) {
        pixel[0] = upload_bytes[static_cast<size_t>(offset + 0u)];
        pixel[1] = upload_bytes[static_cast<size_t>(offset + 1u)];
        pixel[2] = upload_bytes[static_cast<size_t>(offset + 2u)];
        pixel[3] = upload_bytes[static_cast<size_t>(offset + 3u)];
      }
      return pixel;
    };

    uint64_t sum[4] = {};
    uint32_t sample_count = 0;
    const uint32_t step_x = std::max<uint32_t>(1, tracked.texture_width / 16u);
    const uint32_t step_y =
        std::max<uint32_t>(1, tracked.texture_height / 16u);
    for (uint32_t y = 0; y < tracked.texture_height; y += step_y) {
      for (uint32_t x = 0; x < tracked.texture_width; x += step_x) {
        const std::array<uint8_t, 4> pixel = pixel_at(x, y);
        for (uint32_t c = 0; c < 4; ++c) {
          sum[c] += pixel[c];
        }
        ++sample_count;
      }
    }
    if (sample_count == 0) {
      sample_count = 1;
    }

    const std::array<uint8_t, 4> p0 = pixel_at(0, 0);
    const std::array<uint8_t, 4> pc =
        pixel_at(tracked.texture_width / 2u, tracked.texture_height / 2u);
    const std::array<uint8_t, 4> p1 =
        pixel_at(tracked.texture_width - 1u, tracked.texture_height - 1u);
    uint32_t raw_nonzero_samples = 0;
    uint32_t raw_sample_count = 0;
    std::array<uint8_t, 16> raw0 = {};
    if (!snapshot.upload_data.empty()) {
      const size_t raw_copy_size = std::min(raw0.size(), snapshot.upload_data.size());
      std::memcpy(raw0.data(), snapshot.upload_data.data(), raw_copy_size);
      const size_t raw_step =
          std::max<size_t>(1, snapshot.upload_data.size() / 256u);
      for (size_t offset = 0; offset < snapshot.upload_data.size();
           offset += raw_step) {
        raw_nonzero_samples += snapshot.upload_data[offset] != 0 ? 1u : 0u;
        ++raw_sample_count;
      }
    }
    REXLOG_INFO(
        "Refii plume Fable video upload sample #{} guest=0x{:08X} "
        "size={}x{} row={} decoded={} fmt={} raw36=0x{:08X} "
        "p0={}/{}/{}/{} pc={}/{}/{}/{} p1={}/{}/{}/{} "
        "avg={}/{}/{}/{} raw_nz={}/{} raw0="
        "{:02X}{:02X}{:02X}{:02X}/{:02X}{:02X}{:02X}{:02X}/"
        "{:02X}{:02X}{:02X}{:02X}/{:02X}{:02X}{:02X}{:02X}",
        g_plume_resources.logged_fable_video_texture_upload_samples + 1u,
        tracked.guest_address, tracked.texture_width, tracked.texture_height,
        row_pitch_bytes, decoded_guest_texture ? 1 : 0,
        static_cast<uint32_t>(tracked.texture_guest_format),
        snapshot.raw_packed36, p0[0], p0[1], p0[2], p0[3], pc[0], pc[1],
        pc[2], pc[3], p1[0], p1[1], p1[2], p1[3],
        static_cast<uint32_t>(sum[0] / sample_count),
        static_cast<uint32_t>(sum[1] / sample_count),
        static_cast<uint32_t>(sum[2] / sample_count),
        static_cast<uint32_t>(sum[3] / sample_count), raw_nonzero_samples,
        raw_sample_count, raw0[0], raw0[1], raw0[2], raw0[3], raw0[4],
        raw0[5], raw0[6], raw0[7], raw0[8], raw0[9], raw0[10], raw0[11],
        raw0[12], raw0[13], raw0[14], raw0[15]);
    ++g_plume_resources.logged_fable_video_texture_upload_samples;
  }
  return true;
}

bool RefreshTrackedTextureUpload(plume::RenderDevice* device,
                                 const RendererTrackedResourceSnapshot& snapshot,
                                 PlumeTrackedResource& tracked) {
  if (!device || !tracked.texture || snapshot.upload_generation == 0 ||
      tracked.upload_generation == snapshot.upload_generation) {
    return true;
  }

  std::vector<uint8_t> upload_bytes;
  if (!PrepareTextureUploadBytes(snapshot, tracked, upload_bytes)) {
    return false;
  }

  if (!tracked.texture_upload ||
      tracked.texture_upload_size != upload_bytes.size()) {
    tracked.texture_upload = device->createBuffer(
        plume::RenderBufferDesc::UploadBuffer(
            static_cast<uint32_t>(upload_bytes.size())));
  }
  if (!tracked.texture_upload) {
    return false;
  }

  if (void* dst = tracked.texture_upload->map()) {
    std::memcpy(dst, upload_bytes.data(), upload_bytes.size());
    plume::RenderRange written_range(0, upload_bytes.size());
    tracked.texture_upload->unmap(0, &written_range);
  }
  tracked.upload_generation = snapshot.upload_generation;
  tracked.texture_upload_pending = true;
  UpdateTrackedUploadDebugSample(tracked, upload_bytes.data(),
                                 upload_bytes.size());
  return true;
}

void DecodeFableTextureMetadata(const RendererTrackedResourceSnapshot& snapshot,
                                PlumeTrackedResource& tracked) {
  tracked.texture_format = plume::RenderFormat::R8G8B8A8_UNORM;
  tracked.texture_width = 1;
  tracked.texture_height = 1;
  tracked.texture_depth = 1;
  tracked.texture_mip_levels = 1;
  tracked.texture_dimension = plume::RenderTextureDimension::TEXTURE_2D;
  tracked.texture_view_dimension = plume::RenderTextureViewDimension::TEXTURE_2D;

  namespace xenos = rex::graphics::xenos;

  if (IsSurfaceResource(snapshot.kind)) {
    if ((snapshot.size == 0 || snapshot.stride_or_format == 0) &&
        snapshot.raw_packed36 != 0) {
      uint32_t decoded_width = 0;
      uint32_t decoded_height = 0;
      uint32_t decoded_depth = 1;
      if (DecodeFableSurfaceExtentFromPacked(
              snapshot.raw_packed36, snapshot.raw_packed48, decoded_width,
              decoded_height, &decoded_depth)) {
        tracked.texture_width = ClampGuestTextureExtent(decoded_width);
        tracked.texture_height = ClampGuestTextureExtent(decoded_height);
        tracked.texture_depth = ClampGuestTextureExtent(decoded_depth);
      }
    } else {
      tracked.texture_width =
          ClampGuestTextureExtent(std::max<uint32_t>(snapshot.size, 1u));
      tracked.texture_height = ClampGuestTextureExtent(
          std::max<uint32_t>(snapshot.stride_or_format, 1u));
    }
    if (tracked.texture_depth == 0) {
      tracked.texture_depth = 1;
    }
    tracked.texture_mip_levels = 1;
    const uint32_t surface_d3d_format =
        snapshot.raw_packed40 != 0
            ? (snapshot.raw_packed40 & 0x3Fu)
            : (snapshot.raw_packed32 != 0 ? (snapshot.raw_packed32 & 0x3Fu)
                                          : (snapshot.raw_packed28 & 0x3Fu));
    tracked.texture_guest_format = surface_d3d_format;
    if (IsDepthSurfaceResource(snapshot.kind)) {
      tracked.texture_format =
          DecodeFableDepthSurfaceFormat(snapshot, surface_d3d_format);
    } else {
      const auto color_format =
          static_cast<xenos::ColorRenderTargetFormat>(
              (snapshot.raw_packed28 >> 16u) & 0xFu);
      tracked.texture_format = ConvertXenosColorRenderTargetFormat(color_format);
    }
    if (!IsDepthSurfaceResource(snapshot.kind) &&
        IsBlockCompressedFormat(tracked.texture_format)) {
      tracked.texture_format = plume::RenderFormat::R8G8B8A8_UNORM;
    }
    const bool has_normalized_fetch = std::any_of(
        snapshot.normalized_fetch_dwords.begin(),
        snapshot.normalized_fetch_dwords.end(),
        [](uint32_t dword) { return dword != 0; });
    if (has_normalized_fetch) {
      tracked.texture_fetch_dwords = snapshot.normalized_fetch_dwords;
      xenos::xe_gpu_texture_fetch_t fetch = {};
      fetch.dword_0 = snapshot.normalized_fetch_dwords[0];
      fetch.dword_1 = snapshot.normalized_fetch_dwords[1];
      fetch.dword_2 = snapshot.normalized_fetch_dwords[2];
      fetch.dword_3 = snapshot.normalized_fetch_dwords[3];
      fetch.dword_4 = snapshot.normalized_fetch_dwords[4];
      fetch.dword_5 = snapshot.normalized_fetch_dwords[5];
      if (fetch.type == xenos::FetchConstantType::kTexture ||
          fetch.type == xenos::FetchConstantType::kInvalidTexture) {
        tracked.texture_guest_base = fetch.base_address << 12;
        tracked.texture_guest_mips = fetch.mip_address << 12;
        tracked.texture_guest_pitch = fetch.pitch << 5;
        tracked.texture_guest_format = static_cast<uint32_t>(fetch.format);
        tracked.texture_guest_dimension = static_cast<uint32_t>(fetch.dimension);
        tracked.texture_guest_tiled = fetch.tiled != 0;
      }
    }
    return;
  }

  const std::array<uint32_t, 6> raw_fetch_dwords = {
      snapshot.raw_packed28, snapshot.raw_packed32, snapshot.raw_packed36,
      snapshot.raw_packed40, snapshot.raw_packed44, snapshot.raw_packed48};
  const std::array<uint32_t, 6>& fetch_dwords =
      std::any_of(snapshot.normalized_fetch_dwords.begin(),
                  snapshot.normalized_fetch_dwords.end(),
                  [](uint32_t dword) { return dword != 0; })
          ? snapshot.normalized_fetch_dwords
          : raw_fetch_dwords;
  tracked.texture_fetch_dwords =
      fetch_dwords;

  xenos::xe_gpu_texture_fetch_t fetch = {};
  fetch.dword_0 = fetch_dwords[0];
  fetch.dword_1 = fetch_dwords[1];
  fetch.dword_2 = fetch_dwords[2];
  fetch.dword_3 = fetch_dwords[3];
  fetch.dword_4 = fetch_dwords[4];
  fetch.dword_5 = fetch_dwords[5];

  if (fetch.type != xenos::FetchConstantType::kTexture &&
      fetch.type != xenos::FetchConstantType::kInvalidTexture) {
    return;
  }

  tracked.texture_guest_base = fetch.base_address << 12;
  tracked.texture_guest_mips = fetch.mip_address << 12;
  tracked.texture_guest_pitch = fetch.pitch << 5;
  tracked.texture_guest_format = static_cast<uint32_t>(fetch.format);
  tracked.texture_guest_dimension = static_cast<uint32_t>(fetch.dimension);
  tracked.texture_guest_tiled = fetch.tiled != 0;
  tracked.texture_format = ConvertXenosTextureFormat(fetch.format);

  const bool cube = fetch.dimension == xenos::DataDimension::kCube;
  if (fetch.dimension == xenos::DataDimension::k1D) {
    tracked.texture_dimension = plume::RenderTextureDimension::TEXTURE_1D;
  } else if (fetch.dimension == xenos::DataDimension::k3D) {
    tracked.texture_dimension = plume::RenderTextureDimension::TEXTURE_3D;
  } else {
    tracked.texture_dimension = plume::RenderTextureDimension::TEXTURE_2D;
  }
  tracked.texture_view_dimension =
      ViewDimensionForTextureDimension(tracked.texture_dimension, cube);

  if (fetch.dimension == xenos::DataDimension::k1D) {
    tracked.texture_width =
        ClampGuestTextureExtent(fetch.size_1d.width + 1u);
  } else if (fetch.dimension == xenos::DataDimension::k3D) {
    tracked.texture_width =
        ClampGuestTextureExtent(fetch.size_3d.width + 1u);
    tracked.texture_height =
        ClampGuestTextureExtent(fetch.size_3d.height + 1u);
    tracked.texture_depth =
        ClampGuestTextureExtent(fetch.size_3d.depth + 1u);
  } else {
    tracked.texture_width =
        ClampGuestTextureExtent(fetch.size_2d.width + 1u);
    tracked.texture_height =
        ClampGuestTextureExtent(fetch.size_2d.height + 1u);
    tracked.texture_depth = 1;
  }

  tracked.texture_mip_levels =
      fetch.mip_address != 0 && fetch.mip_max_level >= fetch.mip_min_level
          ? std::max<uint32_t>(
                uint32_t(fetch.mip_max_level - fetch.mip_min_level + 1), 1u)
          : 1u;
}

std::unique_ptr<plume::RenderTexture> CreateTextureForTrackedResource(
    plume::RenderDevice* device, const RendererTrackedResourceSnapshot& snapshot,
    PlumeTrackedResource& tracked) {
  if (!device || !IsImageResource(snapshot.kind)) {
    return nullptr;
  }

  DecodeFableTextureMetadata(snapshot, tracked);

  if (snapshot.kind == RendererTrackedResourceKind::kSurface &&
      snapshot.raw_packed36 != 0) {
    uint32_t surface_width = 0;
    uint32_t surface_height = 0;
    if (DecodeFableSurfaceExtentFromPacked(snapshot.raw_packed36,
                                           snapshot.raw_packed48,
                                           surface_width, surface_height) &&
        !IsPlausibleFableColorRenderTargetExtent(surface_width,
                                                surface_height)) {
      return nullptr;
    }
  }

  plume::RenderTextureDesc desc;
  if (IsDepthSurfaceResource(snapshot.kind)) {
    tracked.texture_dimension = plume::RenderTextureDimension::TEXTURE_2D;
    tracked.texture_view_dimension =
        plume::RenderTextureViewDimension::TEXTURE_2D;
    tracked.texture_depth = 1;
    if (!plume::RenderFormatIsDepth(tracked.texture_format)) {
      tracked.texture_format =
          DecodeFableDepthSurfaceFormat(snapshot, tracked.texture_guest_format);
    }
    desc = plume::RenderTextureDesc::DepthTarget(
        tracked.texture_width, tracked.texture_height, tracked.texture_format);
  } else if (snapshot.kind == RendererTrackedResourceKind::kSurface) {
    desc = plume::RenderTextureDesc::ColorTarget(
        tracked.texture_width, tracked.texture_height, tracked.texture_format);
  } else if (tracked.texture_view_dimension ==
      plume::RenderTextureViewDimension::TEXTURE_CUBE) {
    desc = plume::RenderTextureDesc::Texture(
        plume::RenderTextureDimension::TEXTURE_2D, tracked.texture_width,
        tracked.texture_height, 1, 1, 6, tracked.texture_format,
        plume::RenderTextureFlag::CUBE);
  } else if (tracked.texture_dimension ==
             plume::RenderTextureDimension::TEXTURE_3D) {
    desc = plume::RenderTextureDesc::Texture3D(
        tracked.texture_width, tracked.texture_height, tracked.texture_depth, 1,
        tracked.texture_format);
  } else if (tracked.texture_dimension ==
             plume::RenderTextureDimension::TEXTURE_1D) {
    desc = plume::RenderTextureDesc::Texture1D(tracked.texture_width, 1,
                                               tracked.texture_format);
  } else {
    desc = plume::RenderTextureDesc::Texture2D(
        tracked.texture_width, tracked.texture_height, 1,
        tracked.texture_format);
  }

  tracked.texture_render_target =
      snapshot.kind == RendererTrackedResourceKind::kSurface;
  tracked.texture_depth_target =
      snapshot.kind == RendererTrackedResourceKind::kDepthSurface;
  return device->createTexture(desc);
}

void NameTrackedTexture(plume::RenderTexture* texture,
                        const RendererTrackedResourceSnapshot& snapshot,
                        const PlumeTrackedResource& tracked) {
  if (!texture) {
    return;
  }

  char name[160] = {};
  std::snprintf(name, sizeof(name),
                "Refii Fable %s guest=0x%08X base=0x%08X %ux%ux%u mips=%u fmt=%u gfmt=%u pitch=%u %s%s%s",
                TrackedResourceKindName(snapshot.kind), snapshot.guest_address,
                tracked.texture_guest_base,
                tracked.texture_width, tracked.texture_height,
                tracked.texture_depth, tracked.texture_mip_levels,
                static_cast<uint32_t>(tracked.texture_format),
                tracked.texture_guest_format, tracked.texture_guest_pitch,
                tracked.texture_guest_tiled ? "tiled " : "linear ",
                tracked.texture_upload_decoded ? "decoded " : "",
                tracked.texture_upload_pending ? "pending" : "");
  texture->setName(name);
}

void UploadTrackedBuffer(PlumeTrackedResource& tracked,
                         const RendererTrackedResourceSnapshot& snapshot) {
  if (!tracked.buffer || snapshot.upload_generation == 0 ||
      snapshot.upload_data.empty()) {
    return;
  }

  const size_t copy_size =
      std::min<size_t>(snapshot.upload_data.size(), tracked.size);
  if (copy_size == 0) {
    return;
  }

  auto* dst = static_cast<uint8_t*>(tracked.buffer->map());
  if (!dst) {
    REXLOG_WARN(
        "Refii plume resource manager could not map {} guest=0x{:08X} "
        "upload={} size={}",
        TrackedResourceKindName(snapshot.kind), snapshot.guest_address,
        snapshot.upload_generation, copy_size);
    return;
  }

  std::memcpy(dst, snapshot.upload_data.data(), copy_size);
  plume::RenderRange written_range(0, copy_size);
  tracked.buffer->unmap(0, &written_range);
  tracked.upload_generation = snapshot.upload_generation;
  UpdateTrackedUploadDebugSample(tracked, snapshot.upload_data.data(),
                                 copy_size);
  if (snapshot.kind == RendererTrackedResourceKind::kIndexBuffer) {
    tracked.index_shadow_upload = snapshot.upload_data;
  } else {
    tracked.index_shadow_upload.clear();
  }

  if (RendererResourceTracingEnabled()) {
    REXLOG_INFO(
        "Refii plume resource manager uploaded {} guest=0x{:08X} bytes={} "
        "generation={}",
        TrackedResourceKindName(snapshot.kind), snapshot.guest_address,
        copy_size, snapshot.upload_generation);
  }
}

uint16_t LoadBigEndianU16(const uint8_t* data) {
  return static_cast<uint16_t>((static_cast<uint16_t>(data[0]) << 8) |
                               static_cast<uint16_t>(data[1]));
}

uint32_t LoadBigEndianU32(const uint8_t* data) {
  return (static_cast<uint32_t>(data[0]) << 24) |
         (static_cast<uint32_t>(data[1]) << 16) |
         (static_cast<uint32_t>(data[2]) << 8) |
         static_cast<uint32_t>(data[3]);
}

const char* ConvertGuestDeclUsageName(uint8_t usage) {
  switch (usage) {
    case 0:
      return "POSITION";
    case 1:
      return "BLENDWEIGHT";
    case 2:
      return "BLENDINDICES";
    case 3:
      return "NORMAL";
    case 4:
      return "PSIZE";
    case 5:
      return "TEXCOORD";
    case 6:
      return "TANGENT";
    case 7:
      return "BINORMAL";
    case 8:
      return "TESSFACTOR";
    case 9:
      return "POSITIONT";
    case 10:
      return "COLOR";
    case 11:
      return "FOG";
    case 12:
      return "DEPTH";
    case 13:
      return "SAMPLE";
    default:
      return "UNKNOWN";
  }
}

uint32_t ConvertGuestDeclLocation(uint8_t usage, uint8_t usage_index) {
  struct Location {
    uint8_t usage;
    uint8_t usage_index;
    uint32_t location;
  };

  // Keep this in lock-step with XenosRecomp's USAGE_LOCATIONS table. The
  // translated shaders only emit these Vulkan locations; extending the table
  // here makes Plume feed data to locations the shader never reads.
  static constexpr std::array<Location, 17> kLocations = {{
      {0, 0, 0},   {3, 0, 1},   {6, 0, 2},   {7, 0, 3},
      {5, 0, 4},   {5, 1, 5},   {5, 2, 6},   {5, 3, 7},
      {10, 0, 8},  {2, 0, 9},   {1, 0, 10},  {10, 1, 11},
      {5, 4, 12},  {5, 5, 13},  {5, 6, 14},  {5, 7, 15},
      {0, 1, 15},
  }};

  for (const Location& location : kLocations) {
    if (location.usage == usage && location.usage_index == usage_index) {
      return location.location;
    }
  }
  // Fable 2 uses a few semantics outside the Unleashed-focused fixed table
  // above. Keep them as real declaration elements so shader-signature remapping
  // can bind them to the exact translated HLSL locations instead of falling
  // back to default vertex data.
  if (usage == 0 && usage_index < 8) {
    return 16u + usage_index;
  }
  if (usage == 5 && usage_index < 16) {
    return 24u + usage_index;
  }
  if (usage == 10 && usage_index < 4) {
    return 40u + usage_index;
  }
  return UINT32_MAX;
}

plume::RenderFormat ConvertGuestDeclType(uint32_t type) {
  switch (type) {
    case 0x2C83A4:
      return plume::RenderFormat::R32_FLOAT;
    case 0x2C23A5:
      return plume::RenderFormat::R32G32_FLOAT;
    case 0x2A23B9:
      return plume::RenderFormat::R32G32B32_FLOAT;
    case 0x1A23A6:
      return plume::RenderFormat::R32G32B32A32_FLOAT;
    case 0x182886:
      return plume::RenderFormat::B8G8R8A8_UNORM;
    case 0x1A2286:
    case 0x1A2386:
      return plume::RenderFormat::R8G8B8A8_UINT;
    case 0x2C2359:
      return plume::RenderFormat::R16G16_SINT;
    case 0x1A235A:
      return plume::RenderFormat::R16G16B16A16_SINT;
    case 0x1A2086:
    case 0x1A2186:
      return plume::RenderFormat::R8G8B8A8_UNORM;
    case 0x2C2159:
      return plume::RenderFormat::R16G16_SNORM;
    case 0x1A215A:
      return plume::RenderFormat::R16G16B16A16_SNORM;
    case 0x2C2059:
      return plume::RenderFormat::R16G16_UNORM;
    case 0x1A205A:
      return plume::RenderFormat::R16G16B16A16_UNORM;
    case 0x2C82A1:
    case 0x2C83A1:
      return plume::RenderFormat::R32_UINT;
    case 0x2C23A2:
      return plume::RenderFormat::R32G32_UINT;
    case 0x1A23A3:
      return plume::RenderFormat::R32G32B32A32_UINT;
    case 0x1A2187:
    case 0x1A2287:
    case 0x2A2187:
    case 0x2A2287:
    case 0x2A2190:
    case 0x2A2390:
      return plume::RenderFormat::R32_UINT;
    case 0x2C235F:
      return plume::RenderFormat::R16G16_FLOAT;
    case 0x1A2360:
      return plume::RenderFormat::R16G16B16A16_FLOAT;
    default:
      return plume::RenderFormat::UNKNOWN;
  }
}

bool GuestDeclTypeNeedsSwappedTexcoords(uint32_t type) {
  switch (type) {
    case 0x2C2359:  // SHORT2
    case 0x1A235A:  // SHORT4
    case 0x2C2159:  // SHORT2N
    case 0x1A215A:  // SHORT4N
    case 0x2C2059:  // USHORT2N
    case 0x1A205A:  // USHORT4N
    case 0x2C235F:  // FLOAT16_2
    case 0x1A2360:  // FLOAT16_4
      return true;
    default:
      return false;
  }
}

bool DecodeVertexDeclaration(PlumeTrackedResource& tracked,
                             const RendererTrackedResourceSnapshot& snapshot) {
  tracked.input_slots.clear();
  tracked.input_elements.clear();
  tracked.debug_summary.clear();
  tracked.swapped_texcoords = 0;
  tracked.has_r11g11b10_normal = false;
  if (snapshot.kind != RendererTrackedResourceKind::kVertexDeclaration ||
      snapshot.upload_data.empty() || snapshot.upload_data.size() % 12 != 0) {
    return false;
  }

  std::array<bool, 16> used_slots = {};
  const size_t element_count = snapshot.upload_data.size() / 12;
  tracked.input_elements.reserve(element_count);

  for (size_t i = 0; i < element_count; ++i) {
    const uint8_t* element = snapshot.upload_data.data() + i * 12;
    const uint16_t stream = LoadBigEndianU16(element + 0);
    const uint16_t offset = LoadBigEndianU16(element + 2);
    const uint32_t type = LoadBigEndianU32(element + 4);
    const uint8_t usage = element[9];
    const uint8_t usage_index = element[10];
    if (stream == 0xFF || type == 0xFFFFFFFFu || stream >= used_slots.size()) {
      continue;
    }

    plume::RenderFormat format = ConvertGuestDeclType(type);
    if (format == plume::RenderFormat::UNKNOWN) {
      REXLOG_WARN(
          "Refii plume vertex declaration skipped unknown element type "
          "guest=0x{:08X} type=0x{:08X} usage={} usage_index={}",
          snapshot.guest_address, type, usage, usage_index);
      continue;
    }

    if (usage == 3 || usage == 6 || usage == 7) {
      if (type == 0x2A23B9) {
        format = plume::RenderFormat::R32G32B32_UINT;
      } else {
        tracked.has_r11g11b10_normal = true;
      }
    }
    if (usage == 5 && usage_index < 32 &&
        GuestDeclTypeNeedsSwappedTexcoords(type)) {
      tracked.swapped_texcoords |= 1u << usage_index;
    }

    const uint32_t location = ConvertGuestDeclLocation(usage, usage_index);
    if (location == UINT32_MAX) {
      REXLOG_WARN(
          "Refii plume vertex declaration skipped unknown semantic "
          "guest=0x{:08X} usage={} usage_index={}",
          snapshot.guest_address, usage, usage_index);
      continue;
    }

    tracked.input_elements.emplace_back(ConvertGuestDeclUsageName(usage),
                                        usage_index, location, format, stream,
                                        offset);
    char element_summary[192] = {};
    std::snprintf(
        element_summary, sizeof(element_summary),
        "%s#%zu:s%u+%u/t%08X/u%u.%u/l%u/f%u raw=%02X%02X%02X%02X%02X%02X"
        "%02X%02X%02X%02X%02X%02X",
        tracked.debug_summary.empty() ? "" : ";", i, stream, offset, type,
        usage, usage_index, location, static_cast<uint32_t>(format),
        element[0], element[1], element[2], element[3], element[4],
        element[5], element[6], element[7], element[8], element[9],
        element[10], element[11]);
    tracked.debug_summary += element_summary;
    used_slots[stream] = true;
  }

  for (uint32_t slot = 0; slot < used_slots.size(); ++slot) {
    if (used_slots[slot]) {
      tracked.input_slots.emplace_back(slot, 0);
    }
  }

  return !tracked.input_elements.empty();
}

bool HasInputLocation(const std::vector<plume::RenderInputElement>& input_elements,
                      uint32_t location) {
  return std::any_of(input_elements.begin(), input_elements.end(),
                     [location](const plume::RenderInputElement& element) {
                       return element.location == location;
                     });
}

bool HasInputSlot(const std::vector<plume::RenderInputSlot>& input_slots,
                  uint32_t slot) {
  return std::any_of(input_slots.begin(), input_slots.end(),
                     [slot](const plume::RenderInputSlot& input_slot) {
                       return input_slot.index == slot;
                     });
}

plume::RenderFormat DefaultInputFormatForUsage(uint8_t usage) {
  switch (usage) {
    case 2:
    case 3:
    case 6:
    case 7:
      return plume::RenderFormat::R32_UINT;
    default:
      return plume::RenderFormat::R32_FLOAT;
  }
}

uint32_t InputFormatByteSize(plume::RenderFormat format) {
  switch (format) {
    case plume::RenderFormat::R32_FLOAT:
    case plume::RenderFormat::R32_UINT:
    case plume::RenderFormat::R32_SINT:
    case plume::RenderFormat::B8G8R8A8_UNORM:
    case plume::RenderFormat::R8G8B8A8_UNORM:
    case plume::RenderFormat::R8G8B8A8_UINT:
    case plume::RenderFormat::R8G8B8A8_SNORM:
    case plume::RenderFormat::R8G8B8A8_SINT:
    case plume::RenderFormat::R16G16_FLOAT:
    case plume::RenderFormat::R16G16_UNORM:
    case plume::RenderFormat::R16G16_UINT:
    case plume::RenderFormat::R16G16_SNORM:
    case plume::RenderFormat::R16G16_SINT:
      return 4;
    case plume::RenderFormat::R16_FLOAT:
    case plume::RenderFormat::R16_UNORM:
    case plume::RenderFormat::R16_UINT:
    case plume::RenderFormat::R16_SNORM:
    case plume::RenderFormat::R16_SINT:
    case plume::RenderFormat::R8G8_UNORM:
    case plume::RenderFormat::R8G8_UINT:
    case plume::RenderFormat::R8G8_SNORM:
    case plume::RenderFormat::R8G8_SINT:
      return 2;
    case plume::RenderFormat::R8_UNORM:
    case plume::RenderFormat::R8_UINT:
    case plume::RenderFormat::R8_SNORM:
    case plume::RenderFormat::R8_SINT:
      return 1;
    case plume::RenderFormat::R32G32_FLOAT:
    case plume::RenderFormat::R32G32_UINT:
    case plume::RenderFormat::R32G32_SINT:
    case plume::RenderFormat::R16G16B16A16_FLOAT:
    case plume::RenderFormat::R16G16B16A16_UNORM:
    case plume::RenderFormat::R16G16B16A16_UINT:
    case plume::RenderFormat::R16G16B16A16_SNORM:
    case plume::RenderFormat::R16G16B16A16_SINT:
      return 8;
    case plume::RenderFormat::R32G32B32_FLOAT:
    case plume::RenderFormat::R32G32B32_UINT:
    case plume::RenderFormat::R32G32B32_SINT:
      return 12;
    case plume::RenderFormat::R32G32B32A32_FLOAT:
    case plume::RenderFormat::R32G32B32A32_UINT:
    case plume::RenderFormat::R32G32B32A32_SINT:
      return 16;
    default:
      return 0;
  }
}

uint32_t InputLayoutSlotMaxElementEnd(
    const std::vector<plume::RenderInputElement>& elements, uint32_t slot) {
  uint32_t max_end = 0;
  for (const plume::RenderInputElement& element : elements) {
    if (element.slotIndex != slot) {
      continue;
    }
    const uint32_t element_size = InputFormatByteSize(element.format);
    if (element_size == 0) {
      continue;
    }
    max_end =
        std::max(max_end, element.alignedByteOffset + element_size);
  }
  return max_end;
}

uint64_t DrawVertexFetchEndForSlot(
    const RendererDrawEventSnapshot& draw_event,
    const std::vector<plume::RenderInputElement>& elements, uint32_t slot,
    uint32_t vertex_count,
    const FableIndexRangeSummary* index_summary = nullptr) {
  if (slot >= std::size(draw_event.streams) || vertex_count == 0) {
    return 0;
  }

  const RendererStreamBindingSnapshot& stream = draw_event.streams[slot];
  const uint32_t max_element_end = InputLayoutSlotMaxElementEnd(elements, slot);
  if (stream.stride == 0 || max_element_end == 0) {
    return uint64_t(stream.offset) + max_element_end;
  }

  const uint32_t effective_stride = std::max(stream.stride, max_element_end);
  uint64_t max_vertex = uint64_t(draw_event.draw.start + vertex_count - 1u);
  if (draw_event.draw.indexed && index_summary && index_summary->range_valid) {
    const int64_t base_vertex =
        static_cast<int64_t>(static_cast<int32_t>(draw_event.draw.base_vertex));
    const int64_t indexed_max_vertex =
        base_vertex + int64_t(index_summary->max_index);
    max_vertex = indexed_max_vertex > 0 ? uint64_t(indexed_max_vertex) : 0;
  }
  return uint64_t(stream.offset) +
         max_vertex * effective_stride + max_element_end;
}

const char* StaticSemanticName(std::string_view semantic) {
  if (semantic == "POSITION") return "POSITION";
  if (semantic == "NORMAL") return "NORMAL";
  if (semantic == "TANGENT") return "TANGENT";
  if (semantic == "BINORMAL") return "BINORMAL";
  if (semantic == "TEXCOORD") return "TEXCOORD";
  if (semantic == "COLOR") return "COLOR";
  if (semantic == "BLENDWEIGHT") return "BLENDWEIGHT";
  if (semantic == "BLENDINDICES") return "BLENDINDICES";
  return nullptr;
}

uint8_t GuestDeclUsageFromSemantic(std::string_view semantic) {
  if (semantic == "POSITION") return 0;
  if (semantic == "BLENDWEIGHT") return 1;
  if (semantic == "BLENDINDICES") return 2;
  if (semantic == "NORMAL") return 3;
  if (semantic == "TEXCOORD") return 5;
  if (semantic == "TANGENT") return 6;
  if (semantic == "BINORMAL") return 7;
  if (semantic == "COLOR") return 10;
  return UINT8_MAX;
}

plume::RenderFormat ShaderInputTypeFormat(std::string_view type) {
  if (type == "float") return plume::RenderFormat::R32_FLOAT;
  if (type == "float2") return plume::RenderFormat::R32G32_FLOAT;
  if (type == "float3") return plume::RenderFormat::R32G32B32_FLOAT;
  if (type == "float4") return plume::RenderFormat::R32G32B32A32_FLOAT;
  if (type == "uint") return plume::RenderFormat::R32_UINT;
  if (type == "uint2") return plume::RenderFormat::R32G32_UINT;
  if (type == "uint3") return plume::RenderFormat::R32G32B32_UINT;
  if (type == "uint4") return plume::RenderFormat::R32G32B32A32_UINT;
  return plume::RenderFormat::UNKNOWN;
}

plume::RenderFormat HeuristicShaderInputFormat(
    const plume::RenderInputElement& shader_input) {
  const std::string_view semantic =
      shader_input.semanticName != nullptr ? shader_input.semanticName : "";
  if (semantic == "POSITION") {
    return plume::RenderFormat::R32G32B32_FLOAT;
  }
  if (semantic == "NORMAL" || semantic == "TANGENT" ||
      semantic == "BINORMAL" || semantic == "BLENDINDICES") {
    return plume::RenderFormat::R32_UINT;
  }
  if (semantic == "COLOR") {
    return plume::RenderFormat::B8G8R8A8_UNORM;
  }
  if (semantic == "TEXCOORD") {
    return plume::RenderFormat::R32G32_FLOAT;
  }
  if (semantic == "BLENDWEIGHT") {
    return plume::RenderFormat::R32G32B32A32_FLOAT;
  }
  return shader_input.format != plume::RenderFormat::UNKNOWN
             ? shader_input.format
             : plume::RenderFormat::R32_FLOAT;
}

bool ParseShaderSemanticToken(std::string_view token, std::string& semantic,
                              uint32_t& semantic_index) {
  semantic.clear();
  semantic_index = 0;
  while (!token.empty() &&
         std::isspace(static_cast<unsigned char>(token.front()))) {
    token.remove_prefix(1);
  }
  size_t end = token.find_first_of(", \t\r\n)");
  if (end == std::string_view::npos) {
    end = token.size();
  }
  token = token.substr(0, end);
  while (!token.empty() &&
         (token.back() == ',' || token.back() == '\r' || token.back() == '\n')) {
    token.remove_suffix(1);
  }
  size_t digit_start = token.size();
  while (digit_start > 0 &&
         std::isdigit(static_cast<unsigned char>(token[digit_start - 1]))) {
    --digit_start;
  }
  if (digit_start == 0 || digit_start == token.size()) {
    return false;
  }
  semantic.assign(token.substr(0, digit_start));
  for (size_t i = digit_start; i < token.size(); ++i) {
    semantic_index =
        semantic_index * 10u + static_cast<uint32_t>(token[i] - '0');
  }
  return true;
}

bool ParseShaderInputLine(std::string_view line, uint32_t& location,
                          plume::RenderFormat& declared_format,
                          std::string& semantic,
                          uint32_t& semantic_index,
                          uint32_t& next_auto_location) {
  while (!line.empty() &&
         std::isspace(static_cast<unsigned char>(line.front()))) {
    line.remove_prefix(1);
  }

  bool has_explicit_location = false;
  if (line.starts_with("[[vk::location(")) {
    const size_t location_begin = std::string_view("[[vk::location(").size();
    const size_t location_end = line.find(")", location_begin);
    if (location_end == std::string_view::npos ||
        location_end <= location_begin) {
      return false;
    }

    location = 0;
    for (size_t i = location_begin; i < location_end; ++i) {
      if (!std::isdigit(static_cast<unsigned char>(line[i]))) {
        return false;
      }
      location = location * 10u + static_cast<uint32_t>(line[i] - '0');
    }
    has_explicit_location = true;

    const size_t close = line.find("]]", location_end);
    if (close == std::string_view::npos) {
      return false;
    }
    line.remove_prefix(close + 2);
    while (!line.empty() &&
           std::isspace(static_cast<unsigned char>(line.front()))) {
      line.remove_prefix(1);
    }
  }

  if (!line.starts_with("in ")) {
    return false;
  }
  line.remove_prefix(3);
  while (!line.empty() &&
         std::isspace(static_cast<unsigned char>(line.front()))) {
    line.remove_prefix(1);
  }

  const size_t colon = line.find(':');
  if (colon == std::string_view::npos) {
    return false;
  }

  const size_t type_end = line.find_first_of(" \t\r\n", 0);
  declared_format =
      type_end != std::string_view::npos
          ? ShaderInputTypeFormat(line.substr(0, type_end))
          : plume::RenderFormat::UNKNOWN;

  if (!ParseShaderSemanticToken(line.substr(colon + 1), semantic,
                                semantic_index)) {
    return false;
  }

  if (!has_explicit_location) {
    location = next_auto_location;
  }
  next_auto_location = std::max(next_auto_location, location + 1u);
  return true;
}

std::vector<plume::RenderInputElement> ParseShaderInputSignature(
    const std::string& hlsl_path) {
  std::vector<plume::RenderInputElement> inputs;
  if (hlsl_path.empty()) {
    return inputs;
  }

  std::ifstream file(hlsl_path);
  if (!file) {
    return inputs;
  }

  std::string line;
  uint32_t next_auto_location = 0;
  while (std::getline(file, line)) {
    uint32_t location = 0;
    plume::RenderFormat declared_format = plume::RenderFormat::UNKNOWN;
    std::string semantic;
    uint32_t semantic_index = 0;
    if (!ParseShaderInputLine(line, location, declared_format, semantic,
                              semantic_index, next_auto_location)) {
      continue;
    }

    const char* semantic_name = StaticSemanticName(semantic);
    if (!semantic_name) {
      continue;
    }

    const uint8_t usage = GuestDeclUsageFromSemantic(semantic);
    plume::RenderFormat format =
        declared_format != plume::RenderFormat::UNKNOWN
            ? declared_format
            : (usage != UINT8_MAX ? DefaultInputFormatForUsage(usage)
                                  : plume::RenderFormat::R32_FLOAT);
    if (declared_format == plume::RenderFormat::UNKNOWN) {
      if (semantic == "POSITION") {
        format = plume::RenderFormat::R32G32B32A32_FLOAT;
      } else if (semantic == "TEXCOORD") {
        format = plume::RenderFormat::R32G32_FLOAT;
      } else if (semantic == "COLOR") {
        format = plume::RenderFormat::B8G8R8A8_UNORM;
      } else if (semantic == "BLENDWEIGHT") {
        format = plume::RenderFormat::R32G32B32A32_FLOAT;
      }
    }

    inputs.emplace_back(semantic_name, semantic_index, location, format, 0, 0);
  }

  std::sort(inputs.begin(), inputs.end(),
            [](const plume::RenderInputElement& lhs,
               const plume::RenderInputElement& rhs) {
              if (lhs.location != rhs.location) {
                return lhs.location < rhs.location;
              }
              return lhs.semanticIndex < rhs.semanticIndex;
            });
  inputs.erase(std::unique(inputs.begin(), inputs.end(),
                           [](const plume::RenderInputElement& lhs,
                              const plume::RenderInputElement& rhs) {
                             return lhs.location == rhs.location;
                           }),
               inputs.end());
  return inputs;
}

bool ShaderHlslContainsToken(const std::string& hlsl_path,
                             std::string_view token) {
  if (hlsl_path.empty() || token.empty()) {
    return false;
  }

  std::ifstream file(hlsl_path);
  if (!file) {
    return false;
  }

  std::string line;
  while (std::getline(file, line)) {
    if (line.find(token) != std::string::npos) {
      return true;
    }
  }
  return false;
}

bool IsUnsafeVulkanDepthSampleExportShader(
    const PlumeTrackedResource* pixel_shader) {
  return pixel_shader && pixel_shader->pixel_shader_writes_depth &&
         pixel_shader->pixel_shader_samples_depth_texture;
}

constexpr bool kEnableVulkanDepthSampleExportShaderCopy = true;
constexpr bool kEnableVulkanDepthSampleExportShaderDirect = true;

const PlumeTrackedResource* FindUnsafeVulkanDepthSampleExportShaderLocked(
    const RendererDrawEventSnapshot& draw_event,
    plume::RenderShaderFormat shader_format) {
  if (shader_format != plume::RenderShaderFormat::SPIRV) {
    return nullptr;
  }

  const PlumeTrackedResource* pixel_shader = FindTrackedResourceLocked(
      draw_event.pixel_shader, RendererTrackedResourceKind::kPixelShader);
  return IsUnsafeVulkanDepthSampleExportShader(pixel_shader) ? pixel_shader
                                                            : nullptr;
}

const PlumeTrackedResource* FindVulkanDepthSampleShaderLocked(
    const RendererDrawEventSnapshot& draw_event,
    plume::RenderShaderFormat shader_format) {
  if (shader_format != plume::RenderShaderFormat::SPIRV) {
    return nullptr;
  }

  const PlumeTrackedResource* pixel_shader = FindTrackedResourceLocked(
      draw_event.pixel_shader, RendererTrackedResourceKind::kPixelShader);
  return pixel_shader && pixel_shader->pixel_shader_samples_depth_texture
             ? pixel_shader
             : nullptr;
}

std::string MakeDepthSampleCopyKey(const FableDrawTargetDesc& target) {
  return std::to_string(target.depth_address) + ":" +
         std::to_string(target.depth_width) + ":" +
         std::to_string(target.depth_height) + ":" +
         std::to_string(static_cast<uint32_t>(target.depth_format));
}

PlumeDepthSampleCopy* GetOrCreateDepthSampleCopyLocked(
    plume::RenderDevice* device,
    const FableDrawTargetDesc& target) {
  if (!device || !target.has_depth || !target.depth_texture ||
      target.depth_format == plume::RenderFormat::UNKNOWN ||
      target.depth_width == 0 || target.depth_height == 0) {
    return nullptr;
  }

  const std::string key = MakeDepthSampleCopyKey(target);
  auto existing = g_plume_resources.depth_sample_copies.find(key);
  if (existing != g_plume_resources.depth_sample_copies.end()) {
    return &existing->second;
  }

  PlumeDepthSampleCopy copy;
  copy.width = target.depth_width;
  copy.height = target.depth_height;
  copy.format = target.depth_format;
  copy.texture = device->createTexture(plume::RenderTextureDesc::DepthTarget(
      copy.width, copy.height, copy.format));
  if (!copy.texture) {
    return nullptr;
  }
  copy.view = copy.texture->createTextureView(
      plume::RenderTextureViewDesc::Texture2D(copy.format));
  if (!copy.view) {
    return nullptr;
  }

  char name[128] = {};
  std::snprintf(name, sizeof(name), "Refii Fable depth sample copy %08X",
                target.depth_address);
  copy.texture->setName(name);

  auto [it, inserted] =
      g_plume_resources.depth_sample_copies.emplace(key, std::move(copy));
  (void)inserted;
  return &it->second;
}

PlumeDepthSampleCopy* PrepareDepthSampleCopyLocked(
    plume::RenderDevice* device,
    plume::RenderCommandList* command_list,
    const FableDrawTargetDesc& target) {
  if (!command_list) {
    return nullptr;
  }
  PlumeDepthSampleCopy* copy =
      GetOrCreateDepthSampleCopyLocked(device, target);
  if (!copy || !copy->texture) {
    return nullptr;
  }

  const plume::RenderTextureBarrier copy_barriers[2] = {
      plume::RenderTextureBarrier(target.depth_texture,
                                  plume::RenderTextureLayout::COPY_SOURCE),
      plume::RenderTextureBarrier(copy->texture.get(),
                                  plume::RenderTextureLayout::COPY_DEST),
  };
  command_list->barriers(plume::RenderBarrierStage::COPY, copy_barriers, 2);
  command_list->copyTexture(copy->texture.get(), target.depth_texture);

  const plume::RenderTextureBarrier draw_barriers[2] = {
      plume::RenderTextureBarrier(target.depth_texture,
                                  plume::RenderTextureLayout::DEPTH_WRITE),
      plume::RenderTextureBarrier(copy->texture.get(),
                                  plume::RenderTextureLayout::SHADER_READ),
  };
  command_list->barriers(plume::RenderBarrierStage::GRAPHICS, draw_barriers, 2);
  return copy;
}

std::string MakeFallbackDepthTargetKey(uint32_t depth_address, uint32_t width,
                                       uint32_t height,
                                       plume::RenderFormat format) {
  return std::to_string(depth_address) + ":" + std::to_string(width) + ":" +
         std::to_string(height) + ":" +
         std::to_string(static_cast<uint32_t>(format));
}

PlumeDepthSampleCopy* GetOrCreateFallbackDepthTargetLocked(
    plume::RenderDevice* device, uint32_t depth_address, uint32_t width,
    uint32_t height, plume::RenderFormat format) {
  if (!device || width == 0 || height == 0 ||
      !plume::RenderFormatIsDepth(format)) {
    return nullptr;
  }

  const std::string key =
      MakeFallbackDepthTargetKey(depth_address, width, height, format);
  auto existing = g_plume_resources.fallback_depth_targets.find(key);
  if (existing != g_plume_resources.fallback_depth_targets.end()) {
    return &existing->second;
  }

  PlumeDepthSampleCopy fallback;
  fallback.width = width;
  fallback.height = height;
  fallback.format = format;
  fallback.texture = device->createTexture(
      plume::RenderTextureDesc::DepthTarget(width, height, format));
  if (!fallback.texture) {
    return nullptr;
  }
  fallback.view = fallback.texture->createTextureView(
      plume::RenderTextureViewDesc::Texture2D(format));
  if (!fallback.view) {
    return nullptr;
  }

  char name[128] = {};
  std::snprintf(name, sizeof(name), "Refii Fable fallback depth %08X",
                depth_address);
  fallback.texture->setName(name);

  auto [it, inserted] = g_plume_resources.fallback_depth_targets.emplace(
      key, std::move(fallback));
  (void)inserted;
  return &it->second;
}

bool AttachFallbackDepthTargetForDepthExportLocked(
    plume::RenderDevice* device, FableDrawTargetDesc& target,
    const RendererDrawEventSnapshot& draw_event,
    const PlumeTrackedResource& depth_sample_source) {
  if (!device || target.depth_texture) {
    return false;
  }

  const uint32_t width =
      target.depth_width != 0 ? target.depth_width
                              : depth_sample_source.texture_width;
  const uint32_t height =
      target.depth_height != 0 ? target.depth_height
                               : depth_sample_source.texture_height;
  plume::RenderFormat format =
      plume::RenderFormatIsDepth(target.depth_format)
          ? target.depth_format
          : plume::RenderFormat::D32_FLOAT;
  if (format == plume::RenderFormat::UNKNOWN) {
    format = plume::RenderFormat::D32_FLOAT;
  }

  const uint32_t depth_address =
      draw_event.depth_stencil != 0 ? draw_event.depth_stencil
                                    : depth_sample_source.guest_address;
  PlumeDepthSampleCopy* fallback =
      GetOrCreateFallbackDepthTargetLocked(device, depth_address, width, height,
                                           format);
  if (!fallback || !fallback->texture || !fallback->view) {
    return false;
  }

  target.depth_address = depth_address;
  target.depth_texture = fallback->texture.get();
  target.depth_view = fallback->view.get();
  target.depth_format = format;
  target.depth_width = fallback->width;
  target.depth_height = fallback->height;
  target.has_depth = true;
  target.uses_output = false;

  if (!g_plume_resources.logged_vulkan_fallback_depth_target) {
    REXLOG_WARN(
        "Refii plume is using a fallback Fable depth target for "
        "depth-sample/SV_Depth replay target=0x{:08X} size={}x{} fmt={} "
        "source=0x{:08X} source_size={}x{} source_fmt={}",
        target.depth_address, target.depth_width, target.depth_height,
        static_cast<uint32_t>(target.depth_format),
        depth_sample_source.guest_address, depth_sample_source.texture_width,
        depth_sample_source.texture_height,
        static_cast<uint32_t>(depth_sample_source.texture_format));
    g_plume_resources.logged_vulkan_fallback_depth_target = true;
  }
  return true;
}

std::string MakeColorSampleCopyKey(const FableDrawTargetDesc& target,
                                   uint32_t color_slot) {
  return std::to_string(color_slot) + ":" +
         std::to_string(target.color_addresses[color_slot]) + ":" +
         std::to_string(target.color_widths[color_slot]) + ":" +
         std::to_string(target.color_heights[color_slot]) + ":" +
         std::to_string(static_cast<uint32_t>(
             target.color_formats[color_slot]));
}

std::string MakeColorAttachmentPaddingKey(uint32_t width, uint32_t height,
                                          plume::RenderFormat format) {
  return std::to_string(width) + "x" + std::to_string(height) + ":" +
         std::to_string(static_cast<uint32_t>(format));
}

PlumeColorAttachmentPadding* GetOrCreateColorAttachmentPaddingLocked(
    plume::RenderDevice* device, uint32_t width, uint32_t height,
    plume::RenderFormat format) {
  if (!device || width == 0 || height == 0 ||
      format == plume::RenderFormat::UNKNOWN) {
    return nullptr;
  }

  const std::string key = MakeColorAttachmentPaddingKey(width, height, format);
  auto existing = g_plume_resources.color_attachment_padding.find(key);
  if (existing != g_plume_resources.color_attachment_padding.end()) {
    return &existing->second;
  }

  PlumeColorAttachmentPadding padding;
  padding.width = width;
  padding.height = height;
  padding.format = format;
  padding.texture =
      device->createTexture(plume::RenderTextureDesc::ColorTarget(
          padding.width, padding.height, padding.format));
  if (!padding.texture) {
    return nullptr;
  }
  padding.view = padding.texture->createTextureView(
      plume::RenderTextureViewDesc::Texture2D(padding.format));
  if (!padding.view) {
    return nullptr;
  }

  char name[128] = {};
  std::snprintf(name, sizeof(name),
                "Refii Fable color attachment padding %ux%u fmt=%u",
                padding.width, padding.height,
                static_cast<uint32_t>(padding.format));
  padding.texture->setName(name);

  auto [it, inserted] =
      g_plume_resources.color_attachment_padding.emplace(key,
                                                         std::move(padding));
  return &it->second;
}

PlumeColorSampleCopy* GetOrCreateColorSampleCopyLocked(
    plume::RenderDevice* device,
    const FableDrawTargetDesc& target,
    uint32_t color_slot) {
  if (!device || color_slot >= target.color_count ||
      !target.color_textures[color_slot] ||
      target.color_formats[color_slot] == plume::RenderFormat::UNKNOWN ||
      target.color_widths[color_slot] == 0 ||
      target.color_heights[color_slot] == 0) {
    return nullptr;
  }

  const std::string key = MakeColorSampleCopyKey(target, color_slot);
  auto existing = g_plume_resources.color_sample_copies.find(key);
  if (existing != g_plume_resources.color_sample_copies.end()) {
    return &existing->second;
  }

  PlumeColorSampleCopy copy;
  copy.width = target.color_widths[color_slot];
  copy.height = target.color_heights[color_slot];
  copy.format = target.color_formats[color_slot];
  copy.texture = device->createTexture(plume::RenderTextureDesc::ColorTarget(
      copy.width, copy.height, copy.format));
  if (!copy.texture) {
    return nullptr;
  }
  copy.view = copy.texture->createTextureView(
      plume::RenderTextureViewDesc::Texture2D(copy.format));
  if (!copy.view) {
    return nullptr;
  }

  char name[128] = {};
  std::snprintf(name, sizeof(name), "Refii Fable color sample copy %u %08X",
                color_slot, target.color_addresses[color_slot]);
  copy.texture->setName(name);

  auto [it, inserted] =
      g_plume_resources.color_sample_copies.emplace(key, std::move(copy));
  (void)inserted;
  return &it->second;
}

PlumeColorSampleCopy* PrepareColorSampleCopyLocked(
    plume::RenderDevice* device,
    plume::RenderCommandList* command_list,
    const FableDrawTargetDesc& target,
    uint32_t color_slot) {
  if (!command_list) {
    return nullptr;
  }
  PlumeColorSampleCopy* copy =
      GetOrCreateColorSampleCopyLocked(device, target, color_slot);
  if (!copy || !copy->texture) {
    return nullptr;
  }

  const plume::RenderTextureBarrier copy_barriers[2] = {
      plume::RenderTextureBarrier(target.color_textures[color_slot],
                                  plume::RenderTextureLayout::COPY_SOURCE),
      plume::RenderTextureBarrier(copy->texture.get(),
                                  plume::RenderTextureLayout::COPY_DEST),
  };
  command_list->barriers(plume::RenderBarrierStage::COPY, copy_barriers, 2);
  command_list->copyTexture(copy->texture.get(),
                            target.color_textures[color_slot]);

  const plume::RenderTextureBarrier draw_barriers[2] = {
      plume::RenderTextureBarrier(target.color_textures[color_slot],
                                  plume::RenderTextureLayout::COLOR_WRITE),
      plume::RenderTextureBarrier(copy->texture.get(),
                                  plume::RenderTextureLayout::SHADER_READ),
  };
  command_list->barriers(plume::RenderBarrierStage::GRAPHICS, draw_barriers, 2);
  return copy;
}

void RouteUnsafeVulkanDepthSampleExportAsColorOnlyLocked(
    const RendererDrawEventSnapshot& draw_event,
    const PlumeTrackedResource* pixel_shader,
    FableDrawTargetDesc& target) {
  if (!pixel_shader || !target.has_depth) {
    return;
  }

  if (!g_plume_resources.logged_vulkan_depth_export_skip) {
    REXLOG_WARN(
        "Refii plume is filtering Vulkan Fable draws whose pixel shader samples "
        "the depth texture and exports SV_Depth until the Fable depth-sample "
        "export path is decoded shader=0x{:08X} "
        "name={}",
        draw_event.pixel_shader, pixel_shader->shader_name);
    g_plume_resources.logged_vulkan_depth_export_skip = true;
  }

  if (!g_plume_resources.logged_vulkan_depth_export_details) {
    constexpr uint32_t kFableDepthTextureSamplerSlot = 13;
    constexpr uint32_t kTextureFetchConstantDwords = 6;
    const uint32_t guest_texture =
        kFableDepthTextureSamplerSlot < std::size(draw_event.textures)
            ? draw_event.textures[kFableDepthTextureSamplerSlot]
            : 0;
    std::array<uint32_t, kTextureFetchConstantDwords> fetch_dwords = {};
    const uint32_t fetch_dword =
        kFableDepthTextureSamplerSlot * kTextureFetchConstantDwords;
    if (fetch_dword + kTextureFetchConstantDwords <=
        draw_event.fetch_constants.size()) {
      for (uint32_t i = 0; i < kTextureFetchConstantDwords; ++i) {
        fetch_dwords[i] = draw_event.fetch_constants[fetch_dword + i];
      }
    }

    const PlumeTrackedResource* source =
        FindTrackedSampledImageAllowDepthLocked(
            guest_texture, draw_event, kFableDepthTextureSamplerSlot);
    if (source) {
      REXLOG_WARN(
      "Refii plume Vulkan depth-sample/SV_Depth details slot13=0x{:08X} "
          "fetch13={:08X}/{:08X}/{:08X}/{:08X}/{:08X}/{:08X} "
          "source={} guest=0x{:08X} parent=0x{:08X} object=0x{:08X} "
          "base=0x{:08X} mips=0x{:08X} pitch={} size={}x{}x{} fmt={} "
          "rt={} depth={} target_rt=0x{:08X} target_ds=0x{:08X}",
          guest_texture, fetch_dwords[0], fetch_dwords[1], fetch_dwords[2],
          fetch_dwords[3], fetch_dwords[4], fetch_dwords[5],
          TrackedResourceKindName(source->kind), source->guest_address,
          source->parent_address, source->buffer_object,
          source->texture_guest_base, source->texture_guest_mips,
          source->texture_guest_pitch, source->texture_width,
          source->texture_height, source->texture_depth,
          static_cast<uint32_t>(source->texture_format),
          source->texture_render_target, source->texture_depth_target,
          target.color_address, target.depth_address);
    } else {
      REXLOG_WARN(
          "Refii plume Vulkan depth-sample/SV_Depth details slot13=0x{:08X} "
          "fetch13={:08X}/{:08X}/{:08X}/{:08X}/{:08X}/{:08X} "
          "source=none target_rt=0x{:08X} target_ds=0x{:08X}",
          guest_texture, fetch_dwords[0], fetch_dwords[1], fetch_dwords[2],
          fetch_dwords[3], fetch_dwords[4], fetch_dwords[5],
          target.color_address, target.depth_address);
    }
    g_plume_resources.logged_vulkan_depth_export_details = true;
  }

  target.depth_address = 0;
  target.depth_format = plume::RenderFormat::UNKNOWN;
  target.depth_texture = nullptr;
  target.depth_view = nullptr;
  target.depth_width = 0;
  target.depth_height = 0;
  target.has_depth = false;
}

std::string FormatFableSampleSlotForLogLocked(
    const RendererDrawEventSnapshot& draw_event, uint32_t slot) {
  constexpr uint32_t kTextureFetchConstantDwords = 6;
  const uint32_t guest_texture =
      slot < std::size(draw_event.textures) ? draw_event.textures[slot] : 0;
  const uint32_t sampler_state =
      slot < draw_event.sampler_states.size() ? draw_event.sampler_states[slot]
                                              : 0;
  std::array<uint32_t, kTextureFetchConstantDwords> fetch_dwords = {};
  const uint32_t fetch_dword = slot * kTextureFetchConstantDwords;
  if (fetch_dword + kTextureFetchConstantDwords <=
      draw_event.fetch_constants.size()) {
    for (uint32_t i = 0; i < kTextureFetchConstantDwords; ++i) {
      fetch_dwords[i] = draw_event.fetch_constants[fetch_dword + i];
    }
  }

  const PlumeTrackedResource* source =
      FindTrackedSampledImageAllowDepthLocked(guest_texture, draw_event, slot);
  if (source &&
      std::any_of(source->texture_fetch_dwords.begin(),
                  source->texture_fetch_dwords.end(),
                  [](uint32_t dword) { return dword != 0; })) {
    fetch_dwords = source->texture_fetch_dwords;
  }
  char buffer[768] = {};
  if (source) {
    std::snprintf(
        buffer, sizeof(buffer),
        "slot%u tex=0x%08X sampler=0x%08X "
        "fetch=%08X/%08X/%08X/%08X/%08X/%08X source=%s guest=0x%08X "
        "parent=0x%08X object=0x%08X base=0x%08X mips=0x%08X pitch=%u "
        "size=%ux%ux%u fmt=%u rt=%u depth=%u",
        slot, guest_texture, sampler_state, fetch_dwords[0], fetch_dwords[1],
        fetch_dwords[2], fetch_dwords[3], fetch_dwords[4], fetch_dwords[5],
        TrackedResourceKindName(source->kind), source->guest_address,
        source->parent_address, source->buffer_object,
        source->texture_guest_base, source->texture_guest_mips,
        source->texture_guest_pitch, source->texture_width,
        source->texture_height, source->texture_depth,
        static_cast<uint32_t>(source->texture_format),
        source->texture_render_target ? 1u : 0u,
        source->texture_depth_target ? 1u : 0u);
  } else {
    std::snprintf(
        buffer, sizeof(buffer),
        "slot%u tex=0x%08X sampler=0x%08X "
        "fetch=%08X/%08X/%08X/%08X/%08X/%08X source=none",
        slot, guest_texture, sampler_state, fetch_dwords[0], fetch_dwords[1],
        fetch_dwords[2], fetch_dwords[3], fetch_dwords[4], fetch_dwords[5]);
  }
  return buffer;
}

const PlumeTrackedResource* FindUnsafeVulkanTranslatedShaderLocked(
    const RendererDrawEventSnapshot& draw_event,
    plume::RenderShaderFormat shader_format) {
  if (shader_format != plume::RenderShaderFormat::SPIRV) {
    return nullptr;
  }

  const PlumeTrackedResource* pixel_shader = FindTrackedResourceLocked(
      draw_event.pixel_shader, RendererTrackedResourceKind::kPixelShader);
  if (!pixel_shader || !pixel_shader->shader) {
    return nullptr;
  }

  // Depth-export shaders are handled by
  // FindUnsafeVulkanDepthSampleExportShaderLocked so they can bind the sampled
  // depth source and the Fable depth target directly.

  return nullptr;
}

bool InputSemanticMatches(const plume::RenderInputElement& lhs,
                          const plume::RenderInputElement& rhs) {
  const std::string_view lhs_semantic =
      lhs.semanticName != nullptr ? lhs.semanticName : "";
  const std::string_view rhs_semantic =
      rhs.semanticName != nullptr ? rhs.semanticName : "";
  return lhs_semantic == rhs_semantic &&
         lhs.semanticIndex == rhs.semanticIndex;
}

bool HasInputSemantic(
    const std::vector<plume::RenderInputElement>& input_elements,
    const plume::RenderInputElement& candidate) {
  return std::any_of(input_elements.begin(), input_elements.end(),
                     [&candidate](const plume::RenderInputElement& element) {
                       return InputSemanticMatches(element, candidate);
                     });
}

void FilterInputElementsToShaderInputs(
    std::vector<plume::RenderInputElement>& input_elements,
    const std::vector<plume::RenderInputElement>& shader_inputs) {
  if (shader_inputs.empty()) {
    return;
  }

  input_elements.erase(
      std::remove_if(input_elements.begin(), input_elements.end(),
                     [&shader_inputs](const plume::RenderInputElement& element) {
                       return !HasInputSemantic(shader_inputs, element);
                     }),
      input_elements.end());

  std::sort(input_elements.begin(), input_elements.end(),
            [](const plume::RenderInputElement& lhs,
               const plume::RenderInputElement& rhs) {
              if (lhs.location != rhs.location) {
                return lhs.location < rhs.location;
              }
              if (lhs.slotIndex != rhs.slotIndex) {
                return lhs.slotIndex < rhs.slotIndex;
              }
              return lhs.alignedByteOffset < rhs.alignedByteOffset;
            });
  input_elements.erase(
      std::unique(input_elements.begin(), input_elements.end(),
                  [](const plume::RenderInputElement& lhs,
                     const plume::RenderInputElement& rhs) {
                    return lhs.location == rhs.location;
                  }),
      input_elements.end());
}

void RemapInputLocationsFromShader(
    std::vector<plume::RenderInputElement>& input_elements,
    const std::vector<plume::RenderInputElement>& shader_inputs) {
  for (plume::RenderInputElement& element : input_elements) {
    for (const plume::RenderInputElement& shader_input : shader_inputs) {
      if (InputSemanticMatches(element, shader_input)) {
        element.location = shader_input.location;
        break;
      }
    }
  }
}

bool IsSemantic(const plume::RenderInputElement& element,
                std::string_view semantic, uint32_t semantic_index) {
  const std::string_view element_semantic =
      element.semanticName != nullptr ? element.semanticName : "";
  return element_semantic == semantic && element.semanticIndex == semantic_index;
}

void NormalizeInputElementFormatsToBoundStreams(
    const RendererDrawEventSnapshot& draw_event,
    std::vector<plume::RenderInputElement>& input_elements) {
  for (plume::RenderInputElement& element : input_elements) {
    if (element.slotIndex >= std::size(draw_event.streams)) {
      continue;
    }

    const RendererStreamBindingSnapshot& stream =
        draw_event.streams[element.slotIndex];
    if (stream.stride == 0) {
      continue;
    }

    const uint32_t element_size = InputFormatByteSize(element.format);
    if (element_size != 0 &&
        element.alignedByteOffset + element_size <= stream.stride) {
      continue;
    }

    if (IsSemantic(element, "POSITION", 0) &&
        element.format == plume::RenderFormat::R32G32B32_FLOAT &&
        element.alignedByteOffset + 8u <= stream.stride) {
      element.format = plume::RenderFormat::R32G32_FLOAT;
    }
  }
}

void PadMissingInputLocations(
    std::vector<plume::RenderInputSlot>& input_slots,
    std::vector<plume::RenderInputElement>& input_elements,
    const std::vector<plume::RenderInputElement>* shader_inputs = nullptr) {
  if (shader_inputs != nullptr && !shader_inputs->empty()) {
    bool needs_default_slot = false;
    for (const plume::RenderInputElement& shader_input : *shader_inputs) {
      if (HasInputSemantic(input_elements, shader_input) ||
          HasInputLocation(input_elements, shader_input.location)) {
        continue;
      }

      plume::RenderInputElement element = shader_input;
      element.format = HeuristicShaderInputFormat(shader_input);
      element.slotIndex = kDefaultVertexInputSlot;
      element.alignedByteOffset = 0;
      input_elements.push_back(element);
      needs_default_slot = true;
    }

    if (needs_default_slot &&
        !HasInputSlot(input_slots, kDefaultVertexInputSlot)) {
      input_slots.emplace_back(kDefaultVertexInputSlot,
                               kDefaultVertexInputStride);
    }
    return;
  }

  struct DefaultInputElement {
    uint8_t usage;
    uint8_t usage_index;
  };

  static constexpr std::array<DefaultInputElement, 11> kDefaultInputs = {{
      {0, 0},
      {3, 0},
      {6, 0},
      {7, 0},
      {5, 0},
      {5, 1},
      {5, 2},
      {5, 3},
      {10, 0},
      {1, 0},
      {2, 0},
  }};

  bool needs_default_slot = false;
  for (const DefaultInputElement& default_input : kDefaultInputs) {
    const uint32_t location =
        ConvertGuestDeclLocation(default_input.usage,
                                 default_input.usage_index);
    if (location == UINT32_MAX || HasInputLocation(input_elements, location)) {
      continue;
    }

    input_elements.emplace_back(
        ConvertGuestDeclUsageName(default_input.usage),
        default_input.usage_index, location,
        DefaultInputFormatForUsage(default_input.usage),
        kDefaultVertexInputSlot, 0);
    needs_default_slot = true;
  }

  if (needs_default_slot &&
      !HasInputSlot(input_slots, kDefaultVertexInputSlot)) {
    input_slots.emplace_back(kDefaultVertexInputSlot,
                             kDefaultVertexInputStride);
  }
}

void TrimInputSlotsToElements(
    std::vector<plume::RenderInputSlot>& input_slots,
    const std::vector<plume::RenderInputElement>& input_elements) {
  input_slots.erase(
      std::remove_if(input_slots.begin(), input_slots.end(),
                     [&input_elements](const plume::RenderInputSlot& slot) {
                       return std::none_of(
                           input_elements.begin(), input_elements.end(),
                           [&slot](const plume::RenderInputElement& element) {
                             return element.slotIndex == slot.index;
                           });
                     }),
      input_slots.end());
}

bool BuildInputLayoutForDrawLocked(
    const RendererDrawEventSnapshot& draw_event,
    std::vector<plume::RenderInputSlot>& input_slots,
    std::vector<plume::RenderInputElement>& input_elements) {
  input_slots.clear();
  input_elements.clear();

  const PlumeTrackedResource* private_rect_vertex_shader =
      FindTrackedResourceLocked(draw_event.vertex_shader,
                                RendererTrackedResourceKind::kVertexShader);
  if (draw_event.private_texture_rectangle && !draw_event.draw.indexed &&
      draw_event.draw.primitive_type == 8 &&
      private_rect_vertex_shader &&
      private_rect_vertex_shader->shader_name == "0152_vshader_guiquad") {
    input_slots.emplace_back(
        0, static_cast<uint32_t>(
               sizeof(FablePrivateTextureRectangleReplayVertex)));
    input_elements.emplace_back(
        "POSITION", 0, 0, plume::RenderFormat::R32G32B32A32_FLOAT, 0,
        static_cast<uint32_t>(
            offsetof(FablePrivateTextureRectangleReplayVertex, position)));
    input_elements.emplace_back(
        "TEXCOORD", 0, 4, plume::RenderFormat::R32G32B32A32_FLOAT, 0,
        static_cast<uint32_t>(
            offsetof(FablePrivateTextureRectangleReplayVertex, texcoord0)));
    input_elements.emplace_back(
        "TEXCOORD", 1, 5, plume::RenderFormat::R32G32B32A32_FLOAT, 0,
        static_cast<uint32_t>(
            offsetof(FablePrivateTextureRectangleReplayVertex, texcoord1)));
    return true;
  }

  if (const PlumeTrackedResource* declaration = FindTrackedResourceLocked(
          draw_event.vertex_declaration,
          RendererTrackedResourceKind::kVertexDeclaration)) {
    input_elements = declaration->input_elements;
    if (!input_elements.empty()) {
      std::array<bool, 16> used_slots = {};
      for (const plume::RenderInputElement& element : input_elements) {
        if (element.slotIndex < used_slots.size()) {
          used_slots[element.slotIndex] = true;
        }
      }
      for (uint32_t slot = 0; slot < used_slots.size(); ++slot) {
        if (!used_slots[slot]) {
          continue;
        }
        const uint32_t declaration_stride =
            InputLayoutSlotMaxElementEnd(input_elements, slot);
        uint32_t stride = draw_event.streams[slot].stride;
        if (stride < declaration_stride) {
          stride = declaration_stride;
        }
        if (stride == 0) {
          return false;
        }
        input_slots.emplace_back(slot, stride);
      }

      const PlumeTrackedResource* vertex_shader = FindTrackedResourceLocked(
          draw_event.vertex_shader, RendererTrackedResourceKind::kVertexShader);
      if (vertex_shader && !vertex_shader->input_elements.empty()) {
        RemapInputLocationsFromShader(input_elements,
                                      vertex_shader->input_elements);
        FilterInputElementsToShaderInputs(input_elements,
                                          vertex_shader->input_elements);
        NormalizeInputElementFormatsToBoundStreams(draw_event, input_elements);
        PadMissingInputLocations(input_slots, input_elements,
                                 &vertex_shader->input_elements);
      } else {
        NormalizeInputElementFormatsToBoundStreams(draw_event, input_elements);
        PadMissingInputLocations(input_slots, input_elements);
      }
      TrimInputSlotsToElements(input_slots, input_elements);
      return !input_slots.empty() && !input_elements.empty();
    }
  }

  const PlumeTrackedResource* vertex_shader = FindTrackedResourceLocked(
      draw_event.vertex_shader, RendererTrackedResourceKind::kVertexShader);
  for (uint32_t slot = 0; slot < std::size(draw_event.streams); ++slot) {
    const RendererStreamBindingSnapshot& stream = draw_event.streams[slot];
    if (!HasTrackedResourceLocked(stream.buffer,
                                  RendererTrackedResourceKind::kVertexBuffer)) {
      continue;
    }

    if (vertex_shader && !vertex_shader->input_elements.empty()) {
      uint32_t offset = 0;
      bool needs_default_slot = false;
      const uint32_t stride_limit =
          stream.stride != 0 ? stream.stride : UINT32_MAX;
      for (const plume::RenderInputElement& shader_input :
           vertex_shader->input_elements) {
        plume::RenderInputElement element = shader_input;
        element.format = HeuristicShaderInputFormat(shader_input);
        uint32_t element_size = InputFormatByteSize(element.format);
        const std::string_view semantic =
            element.semanticName != nullptr ? element.semanticName : "";

        if (semantic == "POSITION" && element.semanticIndex > 0) {
          element.slotIndex = kDefaultVertexInputSlot;
          element.alignedByteOffset = 0;
          element.format = plume::RenderFormat::R32G32B32_FLOAT;
          needs_default_slot = true;
          input_elements.push_back(element);
          continue;
        }

        if (element_size == 0 || offset + element_size > stride_limit) {
          element.slotIndex = kDefaultVertexInputSlot;
          element.alignedByteOffset = 0;
          if (element.format == plume::RenderFormat::UNKNOWN) {
            element.format = plume::RenderFormat::R32_FLOAT;
          }
          needs_default_slot = true;
          input_elements.push_back(element);
          continue;
        }

        element.slotIndex = slot;
        element.alignedByteOffset = offset;
        input_elements.push_back(element);
        offset += element_size;
      }

      const uint32_t inferred_stride =
          std::max<uint32_t>(InputLayoutSlotMaxElementEnd(input_elements, slot),
                             kDefaultVertexInputStride);
      input_slots.emplace_back(slot, std::max(stream.stride, inferred_stride));
      if (needs_default_slot && !HasInputSlot(input_slots, kDefaultVertexInputSlot)) {
        input_slots.emplace_back(kDefaultVertexInputSlot,
                                 kDefaultVertexInputStride);
      }
      return !input_elements.empty();
    }

    const uint32_t fallback_stride = std::max(stream.stride, 12u);
    input_slots.emplace_back(slot, fallback_stride);
    if (fallback_stride >= 12) {
      input_elements.emplace_back("POSITION", 0, 0,
                                  plume::RenderFormat::R32G32B32_FLOAT, slot,
                                  0);
    }
    if (fallback_stride >= 20) {
      input_elements.emplace_back("TEXCOORD", 0, 4,
                                  plume::RenderFormat::R32G32_FLOAT, slot, 12);
    }
    PadMissingInputLocations(input_slots, input_elements);
    return !input_elements.empty();
  }

  return false;
}

plume::RenderBlend ConvertFableBlend(uint32_t blend,
                                     plume::RenderBlend fallback) {
  // Fable writes raw Xenos RB_BLENDCONTROL blend factors, not D3D9 one-based
  // D3DBLEND values.
  switch (blend) {
    case 0:
      return plume::RenderBlend::ZERO;
    case 1:
      return plume::RenderBlend::ONE;
    case 4:
      return plume::RenderBlend::SRC_COLOR;
    case 5:
      return plume::RenderBlend::INV_SRC_COLOR;
    case 6:
      return plume::RenderBlend::SRC_ALPHA;
    case 7:
      return plume::RenderBlend::INV_SRC_ALPHA;
    case 8:
      return plume::RenderBlend::DEST_COLOR;
    case 9:
      return plume::RenderBlend::INV_DEST_COLOR;
    case 10:
      return plume::RenderBlend::DEST_ALPHA;
    case 11:
      return plume::RenderBlend::INV_DEST_ALPHA;
    case 12:
    case 14:
      return plume::RenderBlend::BLEND_FACTOR;
    case 13:
    case 15:
      return plume::RenderBlend::INV_BLEND_FACTOR;
    case 16:
      return plume::RenderBlend::SRC_ALPHA_SAT;
    default:
      return fallback;
  }
}

plume::RenderBlend ConvertFableAlphaBlend(uint32_t blend,
                                          plume::RenderBlend fallback) {
  // Xenos uses the same raw blend-factor enum for color and alpha equations,
  // but color factors collapse to their alpha equivalents in the alpha path.
  switch (blend) {
    case 4:
    case 6:
      return plume::RenderBlend::SRC_ALPHA;
    case 5:
    case 7:
      return plume::RenderBlend::INV_SRC_ALPHA;
    case 8:
    case 10:
      return plume::RenderBlend::DEST_ALPHA;
    case 9:
    case 11:
      return plume::RenderBlend::INV_DEST_ALPHA;
    default:
      return ConvertFableBlend(blend, fallback);
  }
}

plume::RenderBlendOperation ConvertFableBlendOp(
    uint32_t blend_op, plume::RenderBlendOperation fallback) {
  // Raw Xenos RB_BLENDCONTROL blend operation values.
  switch (blend_op) {
    case 0:
      return plume::RenderBlendOperation::ADD;
    case 1:
      return plume::RenderBlendOperation::SUBTRACT;
    case 2:
      return plume::RenderBlendOperation::MIN;
    case 3:
      return plume::RenderBlendOperation::MAX;
    case 4:
      return plume::RenderBlendOperation::REV_SUBTRACT;
    default:
      return fallback;
  }
}

plume::RenderComparisonFunction ConvertFableCompare(
    uint32_t compare, plume::RenderComparisonFunction fallback) {
  // Fable writes raw Xenos RB_DEPTHCONTROL enum values, not D3D9 one-based
  // D3DCMPFUNC values.
  switch (compare) {
    case 0:
      return plume::RenderComparisonFunction::NEVER;
    case 1:
      return plume::RenderComparisonFunction::LESS;
    case 2:
      return plume::RenderComparisonFunction::EQUAL;
    case 3:
      return plume::RenderComparisonFunction::LESS_EQUAL;
    case 4:
      return plume::RenderComparisonFunction::GREATER;
    case 5:
      return plume::RenderComparisonFunction::NOT_EQUAL;
    case 6:
      return plume::RenderComparisonFunction::GREATER_EQUAL;
    case 7:
      return plume::RenderComparisonFunction::ALWAYS;
    default:
      return fallback;
  }
}

plume::RenderCullMode ConvertFableCullMode(uint32_t cull_mode) {
  // Raw Xenos PA_SU_SC_MODE_CNTL low bits: bit 0 culls front faces, bit 1
  // culls back faces, bit 2 chooses front-face winding.
  const bool cull_front = (cull_mode & 0x1u) != 0;
  const bool cull_back = (cull_mode & 0x2u) != 0;
  if (cull_front && !cull_back) {
    return plume::RenderCullMode::FRONT;
  }
  if (cull_back && !cull_front) {
    return plume::RenderCullMode::BACK;
  }
  if (cull_front && cull_back) {
    return plume::RenderCullMode::BACK;
  }
  return plume::RenderCullMode::NONE;
}

plume::RenderFrontFace ConvertFableFrontFace(uint32_t cull_mode) {
  return (cull_mode & 0x4u) != 0 ? plume::RenderFrontFace::CLOCKWISE
                                 : plume::RenderFrontFace::COUNTER_CLOCKWISE;
}

plume::RenderStencilOp ConvertFableStencilOp(uint32_t op) {
  // Raw Xenos RB_DEPTHCONTROL stencil op values.
  switch (op) {
    case 0:
      return plume::RenderStencilOp::KEEP;
    case 1:
      return plume::RenderStencilOp::ZERO;
    case 2:
      return plume::RenderStencilOp::REPLACE;
    case 3:
      return plume::RenderStencilOp::INCREMENT_AND_CLAMP;
    case 4:
      return plume::RenderStencilOp::DECREMENT_AND_CLAMP;
    case 5:
      return plume::RenderStencilOp::INVERT;
    case 6:
      return plume::RenderStencilOp::INCREMENT_AND_WRAP;
    case 7:
      return plume::RenderStencilOp::DECREMENT_AND_WRAP;
    default:
      return plume::RenderStencilOp::KEEP;
  }
}

uint32_t FableDepthControlStencilFail(uint32_t depth_control,
                                      bool back_face) {
  return back_face ? ((depth_control >> 23u) & 0x7u)
                   : ((depth_control >> 11u) & 0x7u);
}

uint32_t FableDepthControlStencilZPass(uint32_t depth_control,
                                       bool back_face) {
  return back_face ? ((depth_control >> 26u) & 0x7u)
                   : ((depth_control >> 14u) & 0x7u);
}

uint32_t FableDepthControlStencilZFail(uint32_t depth_control,
                                       bool back_face) {
  return back_face ? ((depth_control >> 29u) & 0x7u)
                   : ((depth_control >> 17u) & 0x7u);
}

uint32_t FableDepthControlStencilFunc(uint32_t depth_control,
                                      bool back_face) {
  return back_face ? ((depth_control >> 20u) & 0x7u)
                   : ((depth_control >> 8u) & 0x7u);
}

plume::RenderStencilFaceDesc FableStencilFaceFromDepthControl(
    uint32_t depth_control, bool back_face) {
  plume::RenderStencilFaceDesc face;
  face.failOp =
      ConvertFableStencilOp(FableDepthControlStencilFail(depth_control,
                                                        back_face));
  face.depthFailOp =
      ConvertFableStencilOp(FableDepthControlStencilZFail(depth_control,
                                                         back_face));
  face.passOp =
      ConvertFableStencilOp(FableDepthControlStencilZPass(depth_control,
                                                         back_face));
  face.compareFunction =
      ConvertFableCompare(FableDepthControlStencilFunc(depth_control,
                                                       back_face),
                          plume::RenderComparisonFunction::ALWAYS);
  return face;
}

FableDrawTargetDesc BuildFableDrawTargetDescLocked(
    plume::RenderDevice* device,
    const RendererDrawEventSnapshot& draw_event) {
  FableDrawTargetDesc target;
  target.color_address = draw_event.render_targets[0];
  target.depth_address = draw_event.depth_stencil;

  uint32_t first_color_slot = UINT32_MAX;
  for (uint32_t slot = 0; slot < kMaxFableRenderTargets; ++slot) {
    const uint32_t color_address = draw_event.render_targets[slot];
    target.color_addresses[slot] = color_address;
    if (color_address == 0) {
      continue;
    }

    const PlumeTrackedResource* color =
        FindTrackedColorRenderTargetLocked(color_address);
    if (!color) {
      color = GetOrCreateDecodedColorRenderTargetLocked(device, color_address);
    }
    if (!color) {
      LogUnmatchedRenderTargetLocked(color_address);
      continue;
    }

    if (first_color_slot == UINT32_MAX) {
      first_color_slot = slot;
    }
    target.color_textures[slot] = color->texture.get();
    target.color_views[slot] = color->texture_view.get();
    target.color_formats[slot] = color->texture_format;
    target.color_widths[slot] = color->texture_width;
    target.color_heights[slot] = color->texture_height;
    target.color_count = std::max(target.color_count, slot + 1u);
  }

  if (target.color_count != 0 && first_color_slot != UINT32_MAX) {
    const uint32_t padding_width = target.color_widths[first_color_slot];
    const uint32_t padding_height = target.color_heights[first_color_slot];
    const plume::RenderFormat padding_format =
        target.color_formats[first_color_slot] != plume::RenderFormat::UNKNOWN
            ? target.color_formats[first_color_slot]
            : plume::RenderFormat::B8G8R8A8_UNORM;
    for (uint32_t slot = 0; slot < target.color_count; ++slot) {
      if (target.color_textures[slot] && target.color_views[slot]) {
        continue;
      }
      PlumeColorAttachmentPadding* padding =
          GetOrCreateColorAttachmentPaddingLocked(
              device, padding_width, padding_height, padding_format);
      if (!padding || !padding->texture || !padding->view) {
        target.color_count = slot;
        break;
      }
      target.color_addresses[slot] = 0;
      target.color_textures[slot] = padding->texture.get();
      target.color_views[slot] = padding->view.get();
      target.color_formats[slot] = padding->format;
      target.color_widths[slot] = padding->width;
      target.color_heights[slot] = padding->height;
    }
  }

  target.has_color = target.color_count != 0;
  if (target.has_color) {
    const uint32_t primary_slot =
        first_color_slot != UINT32_MAX && first_color_slot < target.color_count
            ? first_color_slot
            : 0u;
    target.color_address = target.color_addresses[primary_slot];
    target.color_texture = target.color_textures[primary_slot];
    target.color_view = target.color_views[primary_slot];
    target.color_format = target.color_formats[primary_slot];
    target.color_width = target.color_widths[primary_slot];
    target.color_height = target.color_heights[primary_slot];
    target.uses_output = false;
  }

  const PlumeTrackedResource* depth =
      FindTrackedDepthTargetLocked(target.depth_address);
  if (!depth) {
    depth = GetOrCreateDecodedDepthTargetLocked(device, target.depth_address);
  }
  if (depth) {
    target.depth_texture = depth->texture.get();
    target.depth_view = depth->texture_view.get();
    target.depth_format = depth->texture_format;
    target.depth_width = depth->texture_width;
    target.depth_height = depth->texture_height;
    target.has_depth = true;
    if (!target.has_color && target.color_address == 0) {
      target.uses_output = false;
    }
  }

  if (target.has_color && target.has_depth &&
      (target.depth_width < target.color_width ||
       target.depth_height < target.color_height)) {
    target.depth_address = 0;
    target.depth_format = plume::RenderFormat::UNKNOWN;
    target.depth_texture = nullptr;
    target.depth_view = nullptr;
    target.depth_width = 0;
    target.depth_height = 0;
    target.has_depth = false;
  }

  return target;
}

bool ApplyFableResolveCopyDrawLocked(plume::RenderDevice* device,
                                     const RendererDrawEventSnapshot& draw_event,
                                     PlumeDrawReplayStats& stats) {
  if (!IsFableResolveCopyDraw(draw_event)) {
    return false;
  }

  ++stats.resolve_copy_draws;
  const uint32_t copy_src_select = draw_event.rb_copy_control & 0x7u;
  const bool depth_resolve = copy_src_select >= kMaxFableRenderTargets;
  const uint32_t source_address =
      depth_resolve ? draw_event.depth_stencil
                    : draw_event.render_targets[copy_src_select];
  if (source_address == 0 || draw_event.rb_copy_dest_base == 0) {
    return true;
  }

  const PlumeTrackedResource* source =
      depth_resolve ? FindTrackedDepthTargetLocked(source_address)
                    : FindTrackedColorRenderTargetLocked(source_address);
  if (!source) {
    source = depth_resolve
                 ? GetOrCreateDecodedDepthTargetLocked(device, source_address)
                 : GetOrCreateDecodedColorRenderTargetLocked(device,
                                                             source_address);
  }
  if (!source || !source->texture || !source->texture_view) {
    return true;
  }

  FableRenderTargetResolveAlias alias;
  alias.destination_base = draw_event.rb_copy_dest_base;
  alias.source_address = source_address;
  alias.source_slot = copy_src_select;
  alias.destination_pitch = draw_event.rb_copy_dest_pitch & 0x3FFFu;
  alias.destination_height = (draw_event.rb_copy_dest_pitch >> 16u) & 0x3FFFu;
  alias.destination_info = draw_event.rb_copy_dest_info;
  alias.copy_control = draw_event.rb_copy_control;
  alias.serial =
      draw_event.resource_serial != 0 ? draw_event.resource_serial
                                      : draw_event.serial;
  alias.depth = depth_resolve;

  const std::array<uint32_t, 4> destination_base_candidates =
      FableDisplayBaseCandidates(alias.destination_base);
  for (uint32_t candidate : destination_base_candidates) {
    if (candidate != 0) {
      g_plume_resources.render_target_resolve_aliases[candidate] = alias;
    }
  }
  const uint32_t page_base =
      NormalizeFableResolveDestinationBase(alias.destination_base);
  g_plume_resources.texture_fetch_lookup_cache.clear();
  g_plume_resources.texture_fetch_serial_lookup_cache.clear();

  if (depth_resolve) {
    ++stats.resolve_depth_aliases;
  } else {
    ++stats.resolve_color_aliases;
  }

  if (!g_plume_resources.logged_resolve_alias) {
    REXLOG_INFO(
        "Refii plume mapped Xenos resolve copy src={} 0x{:08X} slot={} "
        "dest=0x{:08X}/page=0x{:08X} pitch={} height={} fmt={} "
        "source_size={}x{}",
        depth_resolve ? "depth" : "color", source_address, copy_src_select,
        alias.destination_base, page_base, alias.destination_pitch,
        alias.destination_height, (draw_event.rb_copy_dest_info >> 7u) & 0x3Fu,
        source->texture_width, source->texture_height);
    g_plume_resources.logged_resolve_alias = true;
  }

  return true;
}

void PopulateFableDepthTarget(FableDrawTargetDesc& target,
                              uint32_t depth_address,
                              const PlumeTrackedResource* depth) {
  if (!depth || !depth->texture || !depth->texture_view) {
    return;
  }
  target.depth_address = depth_address;
  target.depth_texture = depth->texture.get();
  target.depth_view = depth->texture_view.get();
  target.depth_format = depth->texture_format;
  target.depth_width = depth->texture_width;
  target.depth_height = depth->texture_height;
  target.has_depth = true;
}

const PlumeTrackedResource* FindTrackedTextureByPointerLocked(
    const plume::RenderTexture* target_texture) {
  if (!target_texture) {
    return nullptr;
  }
  const auto cached_it =
      g_plume_resources.texture_pointer_lookup_cache.find(target_texture);
  if (cached_it != g_plume_resources.texture_pointer_lookup_cache.end()) {
    return cached_it->second;
  }
  for (const auto& [address, resource] : g_plume_resources.resources) {
    (void)address;
    if (resource.texture.get() == target_texture) {
      g_plume_resources.texture_pointer_lookup_cache.emplace(target_texture,
                                                             &resource);
      return &resource;
    }
  }
  for (const auto& [address, resource] :
       g_plume_resources.decoded_color_targets) {
    (void)address;
    if (resource.texture.get() == target_texture) {
      g_plume_resources.texture_pointer_lookup_cache.emplace(target_texture,
                                                             &resource);
      return &resource;
    }
  }
  for (const auto& [address, resource] :
       g_plume_resources.decoded_depth_targets) {
    (void)address;
    if (resource.texture.get() == target_texture) {
      g_plume_resources.texture_pointer_lookup_cache.emplace(target_texture,
                                                             &resource);
      return &resource;
    }
  }
  g_plume_resources.texture_pointer_lookup_cache.emplace(target_texture,
                                                         nullptr);
  return nullptr;
}

bool TrackedTexturesAliasFableMemory(const PlumeTrackedResource& sampled,
                                     const PlumeTrackedResource& target) {
  if (sampled.texture.get() == target.texture.get()) {
    return true;
  }
  if (sampled.guest_address != 0 &&
      (sampled.guest_address == target.guest_address ||
       sampled.guest_address == target.parent_address)) {
    return true;
  }
  if (sampled.parent_address != 0 &&
      (sampled.parent_address == target.guest_address ||
       sampled.parent_address == target.parent_address)) {
    return true;
  }
  if (sampled.buffer_object != 0 &&
      sampled.buffer_object == target.buffer_object) {
    return true;
  }
  if (sampled.texture_guest_base != 0 &&
      sampled.texture_guest_base == target.texture_guest_base) {
    return true;
  }

  auto add_alias_address = [](std::vector<uint32_t>& addresses,
                              uint32_t address) {
    if (address == 0) {
      return;
    }
    if (std::find(addresses.begin(), addresses.end(), address) ==
        addresses.end()) {
      addresses.push_back(address);
    }
  };

  auto find_direct_image_resource =
      [](uint32_t address) -> const PlumeTrackedResource* {
    if (!IsLikelyFableObjectAddress(address)) {
      return nullptr;
    }
    if (const PlumeTrackedResource* texture = FindTrackedResourceDirectLocked(
            address, RendererTrackedResourceKind::kTexture)) {
      return texture;
    }
    if (const PlumeTrackedResource* surface = FindTrackedResourceDirectLocked(
            address, RendererTrackedResourceKind::kSurface)) {
      return surface;
    }
    return FindTrackedResourceDirectLocked(
        address, RendererTrackedResourceKind::kDepthSurface);
  };

  auto collect_alias_addresses =
      [&](const PlumeTrackedResource& resource) {
    constexpr uint32_t kMaxAliasHops = 4;
    std::vector<uint32_t> addresses;
    std::vector<uint32_t> pending;
    addresses.reserve(16);
    pending.reserve(8);

    add_alias_address(addresses, resource.guest_address);
    add_alias_address(addresses, resource.parent_address);
    add_alias_address(addresses, resource.buffer_object);
    add_alias_address(addresses, resource.texture_guest_base);
    if (resource.parent_address != 0) {
      pending.push_back(resource.parent_address);
    }
    if (resource.buffer_object != 0) {
      pending.push_back(resource.buffer_object);
    }

    for (uint32_t hop = 0; hop < kMaxAliasHops && !pending.empty(); ++hop) {
      std::vector<uint32_t> next;
      next.reserve(pending.size());
      for (uint32_t address : pending) {
        const PlumeTrackedResource* linked =
            find_direct_image_resource(address);
        if (!linked) {
          continue;
        }
        add_alias_address(addresses, linked->guest_address);
        add_alias_address(addresses, linked->parent_address);
        add_alias_address(addresses, linked->buffer_object);
        add_alias_address(addresses, linked->texture_guest_base);
        if (linked->parent_address != 0 &&
            std::find(addresses.begin(), addresses.end(),
                      linked->parent_address) == addresses.end()) {
          next.push_back(linked->parent_address);
        }
        if (linked->buffer_object != 0 &&
            std::find(addresses.begin(), addresses.end(),
                      linked->buffer_object) == addresses.end()) {
          next.push_back(linked->buffer_object);
        }
      }
      pending = std::move(next);
    }
    return addresses;
  };

  const std::vector<uint32_t> sampled_aliases =
      collect_alias_addresses(sampled);
  const std::vector<uint32_t> target_aliases =
      collect_alias_addresses(target);
  for (uint32_t sampled_alias : sampled_aliases) {
    if (sampled_alias == 0) {
      continue;
    }
    if (std::find(target_aliases.begin(), target_aliases.end(),
                  sampled_alias) != target_aliases.end()) {
      return true;
    }
  }
  return false;
}

bool DrawSamplesFableTargetLocked(const RendererDrawEventSnapshot& draw_event,
                                  const FableDrawTargetDesc& target,
                                  uint32_t ignored_feedback_slot =
                                      UINT32_MAX,
                                  const PlumeDepthSampleCopySlots*
                                      depth_sample_copies = nullptr,
                                  const PlumeColorSampleCopySlots*
                                      color_sample_copies = nullptr) {
  if ((!target.has_color || !target.color_texture) &&
      (!target.has_depth || !target.depth_texture)) {
    return false;
  }

  for (uint32_t slot = 0; slot < kMaxFableTextureSlots; ++slot) {
    if (slot == ignored_feedback_slot) {
      continue;
    }
    const uint32_t guest_texture =
        slot < std::size(draw_event.textures) ? draw_event.textures[slot] : 0;
    const PlumeTrackedResource* texture =
        FindTrackedSampledImageAllowDepthLocked(guest_texture, draw_event, slot);
    if (!texture || !texture->texture) {
      continue;
    }
    const PlumeTrackedResource* depth_target =
        target.has_depth
            ? FindTrackedTextureByPointerLocked(target.depth_texture)
            : nullptr;
    uint32_t aliased_color_slot = UINT32_MAX;
    for (uint32_t color_slot = 0; color_slot < target.color_count;
         ++color_slot) {
      const PlumeTrackedResource* color_target =
          target.color_textures[color_slot]
              ? FindTrackedTextureByPointerLocked(
                    target.color_textures[color_slot])
              : nullptr;
      if (color_target &&
          TrackedTexturesAliasFableMemory(*texture, *color_target)) {
        aliased_color_slot = color_slot;
        break;
      }
    }
    const bool aliases_color = aliased_color_slot != UINT32_MAX;
    const bool aliases_depth =
        depth_target &&
        TrackedTexturesAliasFableMemory(*texture, *depth_target);
    if (aliases_color && color_sample_copies &&
        slot < color_sample_copies->size() && (*color_sample_copies)[slot]) {
      continue;
    }
    if (aliases_depth && depth_sample_copies &&
        slot < depth_sample_copies->size() && (*depth_sample_copies)[slot]) {
      continue;
    }
    if (aliases_color || aliases_depth) {
      if (!g_plume_resources.logged_vulkan_target_feedback_skip) {
        REXLOG_WARN(
            "Refii plume skipped Fable draw that samples its active target "
            "until the guest resolve/alias path is decoded rt=0x{:08X} "
            "ds=0x{:08X} slot={} texture=0x{:08X} kind={} guest=0x{:08X} "
            "parent=0x{:08X} base=0x{:08X} target_kind={} color_slot={} "
            "size={}x{} fmt={}",
            target.color_address, target.depth_address, slot, guest_texture,
            TrackedResourceKindName(texture->kind), texture->guest_address,
            texture->parent_address, texture->texture_guest_base,
            aliases_color ? "color" : "depth",
            aliases_color ? aliased_color_slot : UINT32_MAX,
            texture->texture_width, texture->texture_height,
            static_cast<uint32_t>(texture->texture_format));
        g_plume_resources.logged_vulkan_target_feedback_skip = true;
      }
      return true;
    }
  }

  return false;
}

PlumeDepthSampleCopySlots PrepareDepthFeedbackCopiesLocked(
    plume::RenderDevice* device,
    plume::RenderCommandList* command_list,
    const RendererDrawEventSnapshot& draw_event,
    const FableDrawTargetDesc& target,
    PlumeDrawReplayStats& stats,
    PlumeDepthSampleCopy* existing_copy,
    bool& copied_active_framebuffer) {
  PlumeDepthSampleCopySlots copies = {};
  copied_active_framebuffer = false;
  if (!target.has_depth || !target.depth_texture) {
    return copies;
  }

  const PlumeTrackedResource* depth_target =
      FindTrackedTextureByPointerLocked(target.depth_texture);
  if (!depth_target) {
    return copies;
  }

  PlumeDepthSampleCopy* copy = existing_copy;
  for (uint32_t slot = 0; slot < kMaxFableTextureSlots; ++slot) {
    const uint32_t guest_texture =
        slot < std::size(draw_event.textures) ? draw_event.textures[slot] : 0;
    const PlumeTrackedResource* texture =
        FindTrackedSampledImageAllowDepthLocked(guest_texture, draw_event, slot);
    if (!texture || !texture->texture ||
        !TrackedTexturesAliasFableMemory(*texture, *depth_target)) {
      continue;
    }

    if (!copy) {
      copy = PrepareDepthSampleCopyLocked(device, command_list, target);
      if (!copy) {
        break;
      }
      copied_active_framebuffer = true;
      ++stats.depth_sample_copy_draws;
    }
    copies[slot] = copy;
    ++stats.depth_sample_copy_slots;
  }

  return copies;
}

PlumeColorSampleCopySlots PrepareColorFeedbackCopiesLocked(
    plume::RenderDevice* device,
    plume::RenderCommandList* command_list,
    const RendererDrawEventSnapshot& draw_event,
    const FableDrawTargetDesc& target,
    PlumeDrawReplayStats& stats,
    bool& copied_active_framebuffer) {
  PlumeColorSampleCopySlots copies = {};
  copied_active_framebuffer = false;
  if (!target.has_color || target.color_count == 0) {
    return copies;
  }

  std::array<PlumeColorSampleCopy*, kMaxFableRenderTargets> prepared_copies =
      {};
  bool counted_copy_draw = false;
  for (uint32_t slot = 0; slot < kMaxFableTextureSlots; ++slot) {
    const uint32_t guest_texture =
        slot < std::size(draw_event.textures) ? draw_event.textures[slot] : 0;
    const PlumeTrackedResource* texture =
        FindTrackedSampledImageAllowDepthLocked(guest_texture, draw_event, slot);
    if (!texture || !texture->texture) {
      continue;
    }

    for (uint32_t color_slot = 0; color_slot < target.color_count;
         ++color_slot) {
      const PlumeTrackedResource* color_target =
          target.color_textures[color_slot]
              ? FindTrackedTextureByPointerLocked(
                    target.color_textures[color_slot])
              : nullptr;
      if (!color_target ||
          !TrackedTexturesAliasFableMemory(*texture, *color_target)) {
        continue;
      }

      PlumeColorSampleCopy* copy = prepared_copies[color_slot];
      if (!copy) {
        copy = PrepareColorSampleCopyLocked(device, command_list, target,
                                            color_slot);
        prepared_copies[color_slot] = copy;
      }
      if (!copy) {
        break;
      }
      copied_active_framebuffer = true;
      if (!counted_copy_draw) {
        ++stats.target_feedback_copy_draws;
        counted_copy_draw = true;
      }
      copies[slot] = copy;
      ++stats.target_feedback_copy_slots;
      break;
    }
  }

  return copies;
}

bool FableDepthSampleSourceAliasesTargetLocked(
    const PlumeTrackedResource* depth_sample_source,
    const FableDrawTargetDesc& target) {
  if (!depth_sample_source || !target.has_depth || !target.depth_texture) {
    return false;
  }
  const PlumeTrackedResource* depth_target =
      FindTrackedTextureByPointerLocked(target.depth_texture);
  return depth_target &&
         TrackedTexturesAliasFableMemory(*depth_sample_source, *depth_target);
}

FablePipelineRenderState BuildFablePipelineRenderState(
    const RendererDrawEventSnapshot& draw_event,
    const FableDrawTargetDesc& target) {
  FablePipelineRenderState state;
  state.blend = plume::RenderBlendDesc::Copy();

  const auto& render_states = draw_event.render_states;
  const uint32_t pa_su_sc_mode_cntl =
      draw_event.pm4_registers_valid ? draw_event.pa_su_sc_mode_cntl
                                     : render_states[kFableRenderStateCullMode];
  state.cull_mode = ConvertFableCullMode(pa_su_sc_mode_cntl);
  state.front_face = ConvertFableFrontFace(pa_su_sc_mode_cntl);
  if (draw_event.draw.primitive_type == 8 ||
      draw_event.draw.primitive_type == 13) {
    // Fable rectangle and quad lists are console primitive shapes that we
    // expand into regular triangles for Plume, so preserve them regardless of
    // translated winding.
    state.cull_mode = plume::RenderCullMode::NONE;
  }
  if (draw_event.pm4_registers_valid) {
    const uint32_t blend_control = draw_event.rb_blendcontrol[0];
    const uint32_t edram_mode = draw_event.rb_modecontrol & 0x7u;
    state.blend.renderTargetWriteMask =
        edram_mode == 4u
            ? static_cast<uint8_t>(draw_event.rb_color_mask &
                                   uint32_t(plume::RenderColorWriteEnable::ALL))
            : 0u;
    state.blend.srcBlend =
        ConvertFableBlend(blend_control & 0x1Fu, plume::RenderBlend::ONE);
    state.blend.blendOp =
        ConvertFableBlendOp((blend_control >> 5u) & 0x7u,
                            plume::RenderBlendOperation::ADD);
    state.blend.dstBlend =
        ConvertFableBlend((blend_control >> 8u) & 0x1Fu,
                          plume::RenderBlend::ZERO);
    state.blend.srcBlendAlpha =
        ConvertFableAlphaBlend((blend_control >> 16u) & 0x1Fu,
                               plume::RenderBlend::ONE);
    state.blend.blendOpAlpha =
        ConvertFableBlendOp((blend_control >> 21u) & 0x7u,
                            plume::RenderBlendOperation::ADD);
    state.blend.dstBlendAlpha =
        ConvertFableAlphaBlend((blend_control >> 24u) & 0x1Fu,
                               plume::RenderBlend::ZERO);
    state.blend.blendEnabled =
        state.blend.srcBlend != plume::RenderBlend::ONE ||
        state.blend.dstBlend != plume::RenderBlend::ZERO ||
        state.blend.blendOp != plume::RenderBlendOperation::ADD ||
        state.blend.srcBlendAlpha != plume::RenderBlend::ONE ||
        state.blend.dstBlendAlpha != plume::RenderBlend::ZERO ||
        state.blend.blendOpAlpha != plume::RenderBlendOperation::ADD;
  } else {
    state.blend.blendEnabled =
        render_states[kFableRenderStateAlphaBlendEnable] != 0;
    state.blend.renderTargetWriteMask =
        static_cast<uint8_t>(render_states[kFableRenderStateColorWriteEnable] &
                             uint32_t(plume::RenderColorWriteEnable::ALL));

    state.blend.srcBlend = ConvertFableBlend(
        render_states[kFableRenderStateSrcBlend], plume::RenderBlend::ONE);
    state.blend.dstBlend = ConvertFableBlend(
        render_states[kFableRenderStateDestBlend], plume::RenderBlend::ZERO);
    state.blend.blendOp =
        ConvertFableBlendOp(render_states[kFableRenderStateBlendOp],
                            plume::RenderBlendOperation::ADD);

    const bool separate_alpha =
        render_states[kFableRenderStateSeparateAlphaBlendEnable] != 0;
    if (separate_alpha) {
      state.blend.srcBlendAlpha =
          ConvertFableAlphaBlend(render_states[kFableRenderStateSrcBlendAlpha],
                                 state.blend.srcBlend);
      state.blend.dstBlendAlpha =
          ConvertFableAlphaBlend(render_states[kFableRenderStateDestBlendAlpha],
                                 state.blend.dstBlend);
      state.blend.blendOpAlpha =
          ConvertFableBlendOp(render_states[kFableRenderStateBlendOpAlpha],
                              state.blend.blendOp);
    } else {
      state.blend.srcBlendAlpha = state.blend.srcBlend;
      state.blend.dstBlendAlpha = state.blend.dstBlend;
      state.blend.blendOpAlpha = state.blend.blendOp;
    }
  }

  const bool depth_target_available =
      draw_event.depth_stencil != 0 && target.has_depth;
  if (draw_event.pm4_registers_valid) {
    const uint32_t depth_control = draw_event.rb_depthcontrol;
    const uint32_t edram_mode = draw_event.rb_modecontrol & 0x7u;
    const bool depth_mode = edram_mode == 4u || edram_mode == 5u;
    const uint32_t z_func = (depth_control >> 4u) & 0x7u;
    const bool z_enabled = (depth_control & 0x2u) != 0;
    const bool z_write_enabled = (depth_control & 0x4u) != 0;
    const bool normalized_z_enabled =
        z_enabled && (z_write_enabled || z_func != 7u);
    state.depth_function =
        ConvertFableCompare(normalized_z_enabled ? z_func : 7u,
                            plume::RenderComparisonFunction::LESS);
    state.depth_enabled =
        depth_mode && depth_target_available && normalized_z_enabled;
    state.depth_write_enabled = state.depth_enabled && z_write_enabled;
    state.stencil_enabled =
        depth_mode && (depth_control & 0x1u) != 0 && target.has_depth &&
        plume::RenderFormatIsStencil(target.depth_format);
    state.stencil_front_face =
        FableStencilFaceFromDepthControl(depth_control, false);
    state.stencil_back_face =
        (depth_control & 0x80u) != 0
            ? FableStencilFaceFromDepthControl(depth_control, true)
            : state.stencil_front_face;
    state.stencil_reference = draw_event.rb_stencilrefmask & 0xFFu;
    state.stencil_read_mask = (draw_event.rb_stencilrefmask >> 8u) & 0xFFu;
    state.stencil_write_mask = (draw_event.rb_stencilrefmask >> 16u) & 0xFFu;
  } else {
    state.depth_function =
        ConvertFableCompare(render_states[kFableRenderStateZFunc],
                            plume::RenderComparisonFunction::LESS);
    const bool explicit_depth_enable =
        render_states[kFableRenderStateZEnable] != 0;
    const bool explicit_depth_write =
        render_states[kFableRenderStateZWriteEnable] != 0 ||
        render_states[kFableRenderStateHiZWriteEnable] != 0;
    state.depth_enabled = depth_target_available && explicit_depth_enable;
    state.depth_write_enabled = state.depth_enabled && explicit_depth_write;
    state.stencil_enabled =
        render_states[kFableRenderStateStencilEnable] != 0 &&
        target.has_depth && plume::RenderFormatIsStencil(target.depth_format);
    state.stencil_front_face.failOp =
        ConvertFableStencilOp(render_states[kFableRenderStateStencilFail]);
    state.stencil_front_face.depthFailOp =
        ConvertFableStencilOp(render_states[kFableRenderStateStencilZFail]);
    state.stencil_front_face.passOp =
        ConvertFableStencilOp(render_states[kFableRenderStateStencilPass]);
    state.stencil_front_face.compareFunction =
        ConvertFableCompare(render_states[kFableRenderStateStencilFunc],
                            plume::RenderComparisonFunction::ALWAYS);
    state.stencil_back_face = state.stencil_front_face;
    if (draw_event.pm4_registers_valid) {
      state.stencil_read_mask = draw_event.rb_stencil_read_mask;
      state.stencil_write_mask = draw_event.rb_stencil_write_mask;
      state.stencil_reference = draw_event.rb_stencil_ref;
    } else {
      state.stencil_read_mask =
          render_states[kFableRenderStateStencilReadMask] & 0xFFu;
      state.stencil_write_mask =
          render_states[kFableRenderStateStencilWriteMask] & 0xFFu;
      state.stencil_reference =
          render_states[kFableRenderStateStencilRef] & 0xFFu;
    }
  }
  state.alpha_test_enabled = FableAlphaTestEnabled(draw_event);

  return state;
}

plume::RenderFormat EffectiveFableDepthFormat(
    const FableDrawTargetDesc& target) {
  return target.depth_format;
}

uint32_t FablePipelineRenderTargetCount(const FableDrawTargetDesc& target) {
  return target.uses_output ? 1u : target.color_count;
}

plume::RenderFormat FablePipelineRenderTargetFormat(
    const FableDrawTargetDesc& target, uint32_t slot) {
  if (target.uses_output) {
    return target.color_format;
  }
  return slot < target.color_count ? target.color_formats[slot]
                                   : plume::RenderFormat::UNKNOWN;
}

std::string FormatInputLayoutSummary(
    const std::vector<plume::RenderInputSlot>& slots,
    const std::vector<plume::RenderInputElement>& elements) {
  std::string summary = "slots[";
  for (size_t i = 0; i < slots.size(); ++i) {
    if (i != 0) {
      summary += ",";
    }
    summary += std::to_string(slots[i].index) + ":" +
               std::to_string(slots[i].stride);
  }
  summary += "] elems[";
  const size_t element_limit = std::min<size_t>(elements.size(), 6);
  for (size_t i = 0; i < element_limit; ++i) {
    if (i != 0) {
      summary += ",";
    }
    const plume::RenderInputElement& element = elements[i];
    summary +=
        std::string(element.semanticName ? element.semanticName : "?") +
        std::to_string(element.semanticIndex) + "@l" +
        std::to_string(element.location) + "/s" +
        std::to_string(element.slotIndex) + "+" +
        std::to_string(element.alignedByteOffset) + "/f" +
        std::to_string(static_cast<uint32_t>(element.format));
  }
  if (elements.size() > element_limit) {
    summary += ",...";
  }
  summary += "]";
  return summary;
}

std::string FormatTrackedBufferSample(const PlumeTrackedResource* resource,
                                      uint32_t offset, uint32_t bytes) {
  if (!resource || resource->debug_upload_sample.empty() ||
      offset >= resource->debug_upload_sample.size() || bytes == 0) {
    return "none";
  }

  const uint32_t available =
      static_cast<uint32_t>(resource->debug_upload_sample.size() - offset);
  const uint32_t sample_bytes = std::min<uint32_t>(bytes, available);
  std::string summary;
  summary.reserve(sample_bytes * 2u + sample_bytes / 4u);
  char buffer[16] = {};
  for (uint32_t byte = 0; byte < sample_bytes; ++byte) {
    if (byte != 0 && byte % 4u == 0) {
      summary += "/";
    }
    std::snprintf(buffer, sizeof(buffer), "%02X",
                  resource->debug_upload_sample[offset + byte]);
    summary += buffer;
  }
  bool sample_has_nonzero = false;
  for (uint32_t byte = 0; byte < sample_bytes; ++byte) {
    if (resource->debug_upload_sample[offset + byte] != 0) {
      sample_has_nonzero = true;
      break;
    }
  }
  if (!sample_has_nonzero) {
    uint32_t first_nonzero = UINT32_MAX;
    const uint32_t scan_begin = std::min<uint32_t>(
        static_cast<uint32_t>(resource->debug_upload_sample.size()), offset);
    for (uint32_t byte = scan_begin;
         byte < resource->debug_upload_sample.size(); ++byte) {
      if (resource->debug_upload_sample[byte] != 0) {
        first_nonzero = byte;
        break;
      }
    }
    if (first_nonzero != UINT32_MAX) {
      summary += " nz@";
      summary += std::to_string(first_nonzero);
      summary += "=";
      const uint32_t nz_sample_bytes = std::min<uint32_t>(
          32u,
          static_cast<uint32_t>(resource->debug_upload_sample.size() -
                                first_nonzero));
      for (uint32_t byte = 0; byte < nz_sample_bytes; ++byte) {
        if (byte != 0 && byte % 4u == 0) {
          summary += "/";
        }
        std::snprintf(buffer, sizeof(buffer), "%02X",
                      resource->debug_upload_sample[first_nonzero + byte]);
        summary += buffer;
      }
    } else if (resource->debug_first_nonzero_offset != UINT32_MAX &&
               resource->debug_first_nonzero_sample_size != 0) {
      summary += " full_nz@";
      summary += std::to_string(resource->debug_first_nonzero_offset);
      summary += "=";
      for (uint32_t byte = 0; byte < resource->debug_first_nonzero_sample_size;
           ++byte) {
        if (byte != 0 && byte % 4u == 0) {
          summary += "/";
        }
        std::snprintf(buffer, sizeof(buffer), "%02X",
                      resource->debug_first_nonzero_sample[byte]);
        summary += buffer;
      }
    } else {
      summary += " full_nz=none/";
      summary += std::to_string(resource->size);
    }
  }
  if (offset + sample_bytes < resource->size) {
    summary += "...";
  }
  return summary;
}

std::vector<uint32_t> CollectInputLayoutSlots(
    const std::vector<plume::RenderInputSlot>& slots,
    const std::vector<plume::RenderInputElement>& elements) {
  std::vector<uint32_t> indices;
  indices.reserve(slots.size());
  for (const plume::RenderInputSlot& slot : slots) {
    if (std::find(indices.begin(), indices.end(), slot.index) ==
        indices.end()) {
      indices.push_back(slot.index);
    }
  }
  for (const plume::RenderInputElement& element : elements) {
    if (std::find(indices.begin(), indices.end(), element.slotIndex) ==
        indices.end()) {
      indices.push_back(element.slotIndex);
    }
  }
  std::sort(indices.begin(), indices.end());
  return indices;
}

std::string FormatBoundInputStreams(
    const RendererDrawEventSnapshot& draw_event,
    const std::vector<plume::RenderInputSlot>& slots,
    const std::vector<plume::RenderInputElement>& elements,
    uint32_t vertex_count,
    const FableIndexRangeSummary* index_summary = nullptr) {
  const std::vector<uint32_t> indices = CollectInputLayoutSlots(slots, elements);
  std::string summary = "streams[";
  char buffer[192] = {};
  for (size_t i = 0; i < indices.size(); ++i) {
    if (i != 0) {
      summary += ",";
    }
    const uint32_t slot = indices[i];
    if (slot >= std::size(draw_event.streams)) {
      std::snprintf(buffer, sizeof(buffer), "s%u=out_of_range", slot);
      summary += buffer;
      continue;
    }

    const RendererStreamBindingSnapshot& stream = draw_event.streams[slot];
    const PlumeTrackedResource* vertex_buffer = FindTrackedResourceLocked(
        stream.buffer, RendererTrackedResourceKind::kVertexBuffer);
    const uint64_t size = vertex_buffer != nullptr ? vertex_buffer->size : 0;
    const uint32_t resolved_offset =
        vertex_buffer != nullptr
            ? ResolveTrackedBufferOffset(*vertex_buffer, stream.buffer,
                                         stream.offset)
            : stream.offset;
    const uint64_t fetch_end =
        DrawVertexFetchEndForSlot(draw_event, elements, slot, vertex_count,
                                  index_summary);
    std::snprintf(buffer, sizeof(buffer),
                  "s%u=0x%08X+%u/%u resolved=%u size=%llu end=%llu oob=%u",
                  slot, stream.buffer, stream.offset, stream.stride,
                  resolved_offset,
                  static_cast<unsigned long long>(size),
                  static_cast<unsigned long long>(fetch_end),
                  size != 0 && resolved_offset + fetch_end > size ? 1u : 0u);
    summary += buffer;
  }
  summary += "]";
  return summary;
}

std::string FormatBoundInputStreamSamples(
    const RendererDrawEventSnapshot& draw_event,
    const std::vector<plume::RenderInputSlot>& slots,
    const std::vector<plume::RenderInputElement>& elements,
    uint32_t sample_bytes) {
  const std::vector<uint32_t> indices = CollectInputLayoutSlots(slots, elements);
  std::string summary = "samples[";
  char label[32] = {};
  for (size_t i = 0; i < indices.size(); ++i) {
    if (i != 0) {
      summary += ",";
    }
    const uint32_t slot = indices[i];
    std::snprintf(label, sizeof(label), "s%u=", slot);
    summary += label;
    if (slot >= std::size(draw_event.streams)) {
      summary += "out_of_range";
      continue;
    }

    const RendererStreamBindingSnapshot& stream = draw_event.streams[slot];
    const PlumeTrackedResource* vertex_buffer = FindTrackedResourceLocked(
        stream.buffer, RendererTrackedResourceKind::kVertexBuffer);
    const uint32_t resolved_offset =
        vertex_buffer != nullptr
            ? ResolveTrackedBufferOffset(*vertex_buffer, stream.buffer,
                                         stream.offset)
            : stream.offset;
    summary += FormatTrackedBufferSample(vertex_buffer, resolved_offset,
                                         sample_bytes);
  }
  summary += "]";
  return summary;
}

std::string FormatDrawTextureSummary(
    const RendererDrawEventSnapshot& draw_event, uint32_t max_slots = 6) {
  std::string summary = "tex[";
  char buffer[192] = {};
  const uint32_t slot_count =
      std::min<uint32_t>(max_slots, static_cast<uint32_t>(std::size(draw_event.textures)));
  for (uint32_t slot = 0; slot < slot_count; ++slot) {
    if (slot != 0) {
      summary += ",";
    }

    const uint32_t guest_texture = draw_event.textures[slot];
    const PlumeTrackedResource* texture =
        FindTrackedSampledImageAllowDepthLocked(guest_texture, draw_event, slot);
    if (texture) {
      std::snprintf(buffer, sizeof(buffer),
                    "s%u=0x%08X->0x%08X %ux%ux%u fmt=%u gfmt=%u pitch=%u "
                    "rt=%u depth=%u decoded=%u pending=%u",
                    slot, guest_texture, texture->guest_address,
                    texture->texture_width, texture->texture_height,
                    std::max<uint32_t>(texture->texture_depth, 1u),
                    static_cast<uint32_t>(texture->texture_format),
                    texture->texture_guest_format, texture->texture_guest_pitch,
                    texture->texture_render_target ? 1u : 0u,
                    texture->texture_depth_target ? 1u : 0u,
                    texture->texture_upload_decoded ? 1u : 0u,
                    texture->texture_upload_pending ? 1u : 0u);
    } else {
      const uint32_t fetch_dword = slot * 6u;
      std::snprintf(
          buffer, sizeof(buffer),
          "s%u=0x%08X missing fetch=%08X/%08X/%08X/%08X/%08X/%08X",
          slot, guest_texture,
          fetch_dword + 5u < draw_event.fetch_constants.size()
              ? draw_event.fetch_constants[fetch_dword + 0u]
              : 0u,
          fetch_dword + 5u < draw_event.fetch_constants.size()
              ? draw_event.fetch_constants[fetch_dword + 1u]
              : 0u,
          fetch_dword + 5u < draw_event.fetch_constants.size()
              ? draw_event.fetch_constants[fetch_dword + 2u]
              : 0u,
          fetch_dword + 5u < draw_event.fetch_constants.size()
              ? draw_event.fetch_constants[fetch_dword + 3u]
              : 0u,
          fetch_dword + 5u < draw_event.fetch_constants.size()
              ? draw_event.fetch_constants[fetch_dword + 4u]
              : 0u,
          fetch_dword + 5u < draw_event.fetch_constants.size()
              ? draw_event.fetch_constants[fetch_dword + 5u]
              : 0u);
    }
    summary += buffer;
  }
  summary += "]";
  return summary;
}

std::string FormatDrawTextureSlotsSummary(
    const RendererDrawEventSnapshot& draw_event,
    std::initializer_list<uint32_t> slots) {
  std::string summary = "tex_slots[";
  bool first = true;
  for (const uint32_t slot : slots) {
    if (!first) {
      summary += " | ";
    }
    first = false;
    summary += FormatFableSampleSlotForLogLocked(draw_event, slot);
  }
  summary += "]";
  return summary;
}

uint32_t ReadTrackedIndexValue(const PlumeTrackedResource& index_buffer,
                               uint32_t index_number) {
  const uint32_t stride = index_buffer.stride_or_format == 4u ? 4u : 2u;
  const uint64_t byte_offset = uint64_t(index_number) * stride;
  if (byte_offset + stride > index_buffer.index_shadow_upload.size()) {
    return 0;
  }

  const uint8_t* source =
      index_buffer.index_shadow_upload.data() + static_cast<size_t>(byte_offset);
  if (stride == 4u) {
    uint32_t value = 0;
    std::memcpy(&value, source, sizeof(value));
    return value;
  }

  uint16_t value = 0;
  std::memcpy(&value, source, sizeof(value));
  return value;
}

FableIndexRangeSummary AnalyzeIndexRange(
    const PlumeTrackedResource* index_buffer, uint32_t first_index,
    uint32_t index_count) {
  FableIndexRangeSummary summary;
  summary.first = first_index;
  summary.count = index_count;
  if (!index_buffer || index_buffer->index_shadow_upload.empty()) {
    return summary;
  }

  summary.available = true;
  summary.stride = index_buffer->stride_or_format == 4u ? 4u : 2u;
  summary.total_indices = static_cast<uint32_t>(
      index_buffer->index_shadow_upload.size() / summary.stride);
  const uint64_t index_end = uint64_t(first_index) + index_count;
  if (index_count == 0 || index_end > summary.total_indices) {
    return summary;
  }

  summary.range_valid = true;
  summary.min_index = std::numeric_limits<uint32_t>::max();
  for (uint32_t i = 0; i < index_count; ++i) {
    const uint32_t value = ReadTrackedIndexValue(*index_buffer, first_index + i);
    summary.min_index = std::min(summary.min_index, value);
    summary.max_index = std::max(summary.max_index, value);
    if (summary.sample_count < summary.sample.size()) {
      summary.sample[summary.sample_count++] = value;
    }
  }
  if (summary.min_index == std::numeric_limits<uint32_t>::max()) {
    summary.min_index = 0;
  }
  return summary;
}

std::string FormatIndexRangeSummary(const FableIndexRangeSummary& summary) {
  if (!summary.available) {
    return "index[shadow=0]";
  }

  std::string text = "index[stride=" + std::to_string(summary.stride) +
                     " total=" + std::to_string(summary.total_indices) +
                     " first=" + std::to_string(summary.first) +
                     " count=" + std::to_string(summary.count) +
                     " valid=" + std::to_string(summary.range_valid ? 1 : 0);
  if (summary.range_valid) {
    text += " min=" + std::to_string(summary.min_index) +
            " max=" + std::to_string(summary.max_index) + " sample=";
    for (uint32_t i = 0; i < summary.sample_count; ++i) {
      if (i != 0) {
        text += "/";
      }
      text += std::to_string(summary.sample[i]);
    }
  }
  text += "]";
  return text;
}

bool IndexedDrawVertexFetchRangeValid(
    const RendererDrawEventSnapshot& draw_event,
    const std::vector<plume::RenderInputElement>& input_elements,
    const FableIndexRangeSummary& index_summary, std::string* failure) {
  if (!index_summary.available || !index_summary.range_valid) {
    if (failure && index_summary.available) {
      *failure = "index range exceeds captured index buffer";
    }
    return index_summary.available ? false : true;
  }

  const int64_t base_vertex =
      static_cast<int64_t>(static_cast<int32_t>(draw_event.draw.base_vertex));
  const int64_t min_vertex = base_vertex + index_summary.min_index;
  const int64_t max_vertex = base_vertex + index_summary.max_index;
  if (min_vertex < 0) {
    if (failure) {
      *failure = "negative base/index vertex";
    }
    return false;
  }

  std::array<bool, 16> checked_slots = {};
  for (const plume::RenderInputElement& element : input_elements) {
    if (element.slotIndex >= checked_slots.size() ||
        checked_slots[element.slotIndex]) {
      continue;
    }
    checked_slots[element.slotIndex] = true;

    const RendererStreamBindingSnapshot& stream =
        draw_event.streams[element.slotIndex];
    const PlumeTrackedResource* vertex_buffer = FindTrackedResourceLocked(
        stream.buffer, RendererTrackedResourceKind::kVertexBuffer);
    if (!vertex_buffer || vertex_buffer->size == 0) {
      continue;
    }

    const uint32_t stride = stream.stride != 0 ? stream.stride
                                               : InputLayoutSlotMaxElementEnd(
                                                     input_elements,
                                                     element.slotIndex);
    const uint32_t max_element_end =
        InputLayoutSlotMaxElementEnd(input_elements, element.slotIndex);
    if (stride == 0 || max_element_end == 0) {
      continue;
    }

    const uint64_t resolved_offset =
        ResolveTrackedBufferOffset(*vertex_buffer, stream.buffer,
                                   stream.offset);
    const uint64_t fetch_end =
        resolved_offset + uint64_t(max_vertex) * stride + max_element_end;
    if (fetch_end > vertex_buffer->size) {
      if (failure) {
        *failure = "stream" + std::to_string(element.slotIndex) +
                   " fetch_end=" + std::to_string(fetch_end) +
                   " size=" + std::to_string(vertex_buffer->size) +
                   " max_index=" + std::to_string(index_summary.max_index) +
                   " base=" + std::to_string(base_vertex);
      }
      return false;
    }
  }

  return true;
}

bool IsAllZeroTrackedBufferRange(const PlumeTrackedResource* buffer,
                                 uint32_t offset, uint32_t byte_count) {
  if (!buffer || buffer->debug_upload_sample.empty() ||
      offset >= buffer->debug_upload_sample.size()) {
    return false;
  }

  const uint32_t available =
      static_cast<uint32_t>(buffer->debug_upload_sample.size() - offset);
  const uint32_t count = std::min(byte_count, available);
  if (count == 0) {
    return false;
  }

  for (uint32_t i = 0; i < count; ++i) {
    if (buffer->debug_upload_sample[offset + i] != 0) {
      return false;
    }
  }
  return true;
}

bool BuildInputLayoutForDrawLocked(
    const RendererDrawEventSnapshot& draw_event,
    std::vector<plume::RenderInputSlot>& input_slots,
    std::vector<plume::RenderInputElement>& input_elements);

bool IsFableImplicitFullscreenRectangleShapeLocked(
    const RendererDrawEventSnapshot& draw_event,
    const std::vector<plume::RenderInputSlot>& input_slots,
    const std::vector<plume::RenderInputElement>& input_elements,
    uint32_t count) {
  if (draw_event.draw.primitive_type != 8 || draw_event.draw.indexed ||
      count != 3 || input_slots.size() != 1 || input_elements.size() != 1) {
    return false;
  }

  const plume::RenderInputElement& element = input_elements.front();
  if (element.location != 0 || element.slotIndex != 0 ||
      element.alignedByteOffset != 0 ||
      element.format != plume::RenderFormat::R32G32_FLOAT) {
    return false;
  }

  const RendererStreamBindingSnapshot& stream0 = draw_event.streams[0];
  if (stream0.offset != 0 ||
      (stream0.stride != 0 && stream0.stride != sizeof(float) * 2u)) {
    return false;
  }

  const PlumeTrackedResource* vertex_shader = FindTrackedResourceLocked(
      draw_event.vertex_shader, RendererTrackedResourceKind::kVertexShader);
  return vertex_shader &&
         vertex_shader->shader_name == "0156_vshader_gui_text_3d";
}

bool NeedsFableImplicitFullscreenRectangleVerticesLocked(
    const RendererDrawEventSnapshot& draw_event,
    const std::vector<plume::RenderInputSlot>& input_slots,
    const std::vector<plume::RenderInputElement>& input_elements,
    uint32_t count) {
  if (!IsFableImplicitFullscreenRectangleShapeLocked(
          draw_event, input_slots, input_elements, count)) {
    return false;
  }

  const RendererStreamBindingSnapshot& stream0 = draw_event.streams[0];
  if (stream0.buffer == 0) {
    return true;
  }

  const PlumeTrackedResource* vertex_buffer = FindTrackedResourceLocked(
      stream0.buffer, RendererTrackedResourceKind::kVertexBuffer);
  return IsAllZeroTrackedBufferRange(vertex_buffer, stream0.offset,
                                     count * stream0.stride);
}

bool CanUseFableImplicitFullscreenRectangleInputLocked(
    const RendererDrawEventSnapshot& draw_event) {
  const uint32_t count = DrawVertexOrIndexCount(
      draw_event.draw.primitive_type, draw_event.draw.primitive_count);
  if (count == 0) {
    return false;
  }

  std::vector<plume::RenderInputSlot> input_slots;
  std::vector<plume::RenderInputElement> input_elements;
  return BuildInputLayoutForDrawLocked(draw_event, input_slots,
                                       input_elements) &&
         NeedsFableImplicitFullscreenRectangleVerticesLocked(
             draw_event, input_slots, input_elements, count);
}

plume::RenderBuffer* PrepareFableImplicitFullscreenRectangleVertexBufferLocked(
    plume::RenderDevice* device) {
  if (!device) {
    return nullptr;
  }
  if (g_plume_resources.implicit_rectangle_vertex_buffer) {
    return g_plume_resources.implicit_rectangle_vertex_buffer.get();
  }

  // Fable 2 uses primitive-8 post-process rectangles with an all-zero scratch
  // stream. Feed the translated vertex shader the implicit clip-space corners
  // that Xenos expands for this shape.
  static constexpr float kFableRectangleCorners[] = {
      -1.0f, -1.0f,
      -1.0f,  1.0f,
       1.0f, -1.0f,
  };
  std::unique_ptr<plume::RenderBuffer> buffer =
      device->createBuffer(plume::RenderBufferDesc::VertexBuffer(
          sizeof(kFableRectangleCorners), plume::RenderHeapType::UPLOAD));
  if (!buffer) {
    return nullptr;
  }

  if (void* dst = buffer->map()) {
    std::memcpy(dst, kFableRectangleCorners, sizeof(kFableRectangleCorners));
    plume::RenderRange written_range(0, sizeof(kFableRectangleCorners));
    buffer->unmap(0, &written_range);
  }
  buffer->setName("Refii Fable implicit fullscreen rectangle vertices");

  g_plume_resources.implicit_rectangle_vertex_buffer = std::move(buffer);
  if (!g_plume_resources.logged_implicit_rectangle_vertices) {
    REXLOG_INFO(
        "Refii plume using Fable primitive-8 implicit fullscreen rectangle "
        "vertices");
    g_plume_resources.logged_implicit_rectangle_vertices = true;
  }
  return g_plume_resources.implicit_rectangle_vertex_buffer.get();
}

bool ReadFablePrivateTextureRectangleCorners(
    const RendererDrawEventSnapshot& draw_event,
    const PlumeTrackedResource* vertex_buffer, float (&corners)[6]) {
  if (draw_event.private_texture_corners_valid) {
    std::copy(draw_event.private_texture_corners.begin(),
              draw_event.private_texture_corners.end(), corners);
    for (const float value : corners) {
      if (!std::isfinite(value) || std::fabs(value) > 32768.0f) {
        return false;
      }
    }
    return true;
  }

  if (!vertex_buffer ||
      vertex_buffer->debug_upload_sample.size() < sizeof(float) * 6u) {
    return false;
  }

  std::memcpy(corners, vertex_buffer->debug_upload_sample.data(),
              sizeof(float) * 6u);
  for (const float value : corners) {
    if (!std::isfinite(value) || std::fabs(value) > 32768.0f) {
      return false;
    }
  }
  return true;
}

bool IsFablePrivateTextureRectangleGuiQuadLocked(
    const RendererDrawEventSnapshot& draw_event,
    const std::vector<plume::RenderInputSlot>& input_slots,
    const std::vector<plume::RenderInputElement>& input_elements,
    uint32_t count) {
  if (!draw_event.private_texture_rectangle || draw_event.draw.indexed ||
      draw_event.draw.primitive_type != 8 || count != 3 ||
      input_slots.size() != 1 || input_elements.size() != 3) {
    return false;
  }

  const RendererStreamBindingSnapshot& stream0 = draw_event.streams[0];
  if (stream0.buffer == 0 || stream0.offset != 0 ||
      (stream0.stride != 0 && stream0.stride != sizeof(float) * 2u &&
       stream0.stride !=
           sizeof(FablePrivateTextureRectangleReplayVertex))) {
    return false;
  }

  const PlumeTrackedResource* vertex_shader = FindTrackedResourceLocked(
      draw_event.vertex_shader, RendererTrackedResourceKind::kVertexShader);
  return vertex_shader && vertex_shader->shader_name == "0152_vshader_guiquad";
}

bool CanUseFablePrivateTextureRectangleInputLocked(
    const RendererDrawEventSnapshot& draw_event) {
  const uint32_t count = DrawVertexOrIndexCount(
      draw_event.draw.primitive_type, draw_event.draw.primitive_count);
  if (count == 0 || !draw_event.private_texture_corners_valid) {
    return false;
  }

  std::vector<plume::RenderInputSlot> input_slots;
  std::vector<plume::RenderInputElement> input_elements;
  return BuildInputLayoutForDrawLocked(draw_event, input_slots,
                                       input_elements) &&
         IsFablePrivateTextureRectangleGuiQuadLocked(
             draw_event, input_slots, input_elements, count);
}

plume::RenderBuffer* PrepareFablePrivateTextureRectangleVertexBufferLocked(
    plume::RenderDevice* device, const RendererDrawEventSnapshot& draw_event) {
  if (!device) {
    return nullptr;
  }

  const RendererStreamBindingSnapshot& stream0 = draw_event.streams[0];
  const PlumeTrackedResource* source_buffer = FindTrackedResourceLocked(
      stream0.buffer, RendererTrackedResourceKind::kVertexBuffer);
  float corners[6] = {};
  if (!ReadFablePrivateTextureRectangleCorners(draw_event, source_buffer,
                                               corners)) {
    return nullptr;
  }

  const float x0 = corners[0];
  const float y0 = corners[1];
  const float x1 = corners[2];
  const float y1 = corners[5];
  const float width = std::fabs(x1 - x0);
  const float height = std::fabs(y1 - y0);
  float u0 = 0.0f;
  float v0 = 0.0f;
  float u1 = 1.0f;
  float v1 = 1.0f;

  if (draw_event.private_texture_uv_override) {
    u0 = draw_event.private_texture_u0;
    v0 = draw_event.private_texture_v0;
    u1 = draw_event.private_texture_u1;
    v1 = draw_event.private_texture_v1;
  } else {
    const uint32_t texture_slot =
        std::min<uint32_t>(draw_event.private_texture_slot,
                           kMaxFableTextureSlots - 1u);
    const PlumeTrackedResource* texture = FindTrackedSampledImageLocked(
        draw_event.textures[texture_slot], draw_event, texture_slot);
    if (texture && texture->texture_width != 0 &&
        texture->texture_height != 0 && width > 0.0f && height > 0.0f &&
        width <= static_cast<float>(texture->texture_width) * 4.0f &&
        height <= static_cast<float>(texture->texture_height) * 4.0f) {
      const float source_x =
          static_cast<float>(draw_event.private_texture_source_x);
      const float source_y =
          static_cast<float>(draw_event.private_texture_source_y);
      u0 = source_x / static_cast<float>(texture->texture_width);
      v0 = source_y / static_cast<float>(texture->texture_height);
      u1 = (source_x + width) / static_cast<float>(texture->texture_width);
      v1 = (source_y + height) / static_cast<float>(texture->texture_height);
    }
  }

  const auto make_vertex = [](float x, float y, float u, float v) {
    FablePrivateTextureRectangleReplayVertex vertex;
    // The translated Fable GUI vertex shader consumes POSITION.zx after the
    // original Xenos vertex fetch swizzle. Expand the CPU-written two-float
    // rectangle into that fetched shape for replay.
    vertex.position[0] = x;
    vertex.position[1] = 0.0f;
    vertex.position[2] = y;
    vertex.position[3] = 1.0f;
    vertex.texcoord0[0] = u;
    vertex.texcoord0[1] = v;
    vertex.texcoord0[2] = 0.0f;
    vertex.texcoord0[3] = 1.0f;
    vertex.texcoord1[0] = u;
    vertex.texcoord1[1] = v;
    vertex.texcoord1[2] = 0.0f;
    vertex.texcoord1[3] = 1.0f;
    return vertex;
  };

  const FablePrivateTextureRectangleReplayVertex vertices[3] = {
      make_vertex(x0, y0, u0, v0),
      make_vertex(x0, y1, u0, v1),
      make_vertex(x1, y0, u1, v0),
  };
  return CreateFrameVertexBufferLocked(
      device, vertices, static_cast<uint32_t>(sizeof(vertices)),
      "Refii Fable private texture rectangle replay vertices");
}

std::string StableShaderPipelineKey(const PlumeTrackedResource& shader) {
  if (!shader.shader_name.empty()) {
    return shader.shader_name;
  }
  if (shader.guest_bytecode_hash != 0) {
    return "hash:" + std::to_string(shader.guest_bytecode_hash);
  }
  return "guest:" + std::to_string(shader.guest_address);
}

std::string MakePipelineKey(const PlumeTrackedResource& vertex_shader,
                            const PlumeTrackedResource& pixel_shader,
                            const std::vector<plume::RenderInputSlot>& slots,
                            const std::vector<plume::RenderInputElement>& elements,
                            plume::RenderPrimitiveTopology topology,
                            const FablePipelineRenderState& render_state,
                            const FableDrawTargetDesc& target) {
  std::string key = "vs:" + StableShaderPipelineKey(vertex_shader) +
                    ":ps:" + StableShaderPipelineKey(pixel_shader) +
                    ":topo:" +
                    std::to_string(static_cast<uint32_t>(topology));
  const uint32_t render_target_count =
      FablePipelineRenderTargetCount(target);
  key += ":target:" + std::to_string(render_target_count) + "/";
  for (uint32_t i = 0; i < render_target_count; ++i) {
    key += std::to_string(static_cast<uint32_t>(
               FablePipelineRenderTargetFormat(target, i))) +
           ",";
  }
  key += "/" +
         std::to_string(
             static_cast<uint32_t>(EffectiveFableDepthFormat(target)));
  key += ":rs:" +
         std::to_string(render_state.blend.blendEnabled ? 1u : 0u) + "/" +
         std::to_string(static_cast<uint32_t>(render_state.blend.srcBlend)) +
         "/" +
         std::to_string(static_cast<uint32_t>(render_state.blend.dstBlend)) +
         "/" +
         std::to_string(static_cast<uint32_t>(render_state.blend.blendOp)) +
         "/" +
         std::to_string(
             static_cast<uint32_t>(render_state.blend.srcBlendAlpha)) +
         "/" +
         std::to_string(
             static_cast<uint32_t>(render_state.blend.dstBlendAlpha)) +
         "/" +
         std::to_string(
             static_cast<uint32_t>(render_state.blend.blendOpAlpha)) +
         "/" +
         std::to_string(render_state.blend.renderTargetWriteMask) + "/" +
         std::to_string(static_cast<uint32_t>(render_state.depth_function)) +
         "/" + std::to_string(render_state.depth_enabled ? 1u : 0u) + "/" +
         std::to_string(render_state.depth_write_enabled ? 1u : 0u) + "/" +
         std::to_string(render_state.stencil_enabled ? 1u : 0u) + "/" +
         std::to_string(
             static_cast<uint32_t>(render_state.stencil_front_face.failOp)) +
         "/" +
         std::to_string(static_cast<uint32_t>(
             render_state.stencil_front_face.depthFailOp)) +
         "/" +
         std::to_string(
             static_cast<uint32_t>(render_state.stencil_front_face.passOp)) +
         "/" +
         std::to_string(static_cast<uint32_t>(
             render_state.stencil_front_face.compareFunction)) +
         "/" +
         std::to_string(
             static_cast<uint32_t>(render_state.stencil_back_face.failOp)) +
         "/" +
         std::to_string(static_cast<uint32_t>(
             render_state.stencil_back_face.depthFailOp)) +
         "/" +
         std::to_string(
             static_cast<uint32_t>(render_state.stencil_back_face.passOp)) +
         "/" +
         std::to_string(static_cast<uint32_t>(
             render_state.stencil_back_face.compareFunction)) +
         "/" + std::to_string(render_state.stencil_read_mask) + "/" +
         std::to_string(render_state.stencil_write_mask) + "/" +
         std::to_string(render_state.stencil_reference) +
         "/" +
         std::to_string(render_state.alpha_test_enabled ? 1u : 0u) + "/" +
         std::to_string(static_cast<uint32_t>(render_state.cull_mode)) + "/" +
         std::to_string(static_cast<uint32_t>(render_state.front_face));
  for (const plume::RenderInputSlot& slot : slots) {
    key += ":" + std::to_string(slot.index) + "/" + std::to_string(slot.stride);
  }
  key += ":e";
  for (const plume::RenderInputElement& element : elements) {
    key += ":" + std::to_string(element.location) + "/" +
           std::to_string(static_cast<uint32_t>(element.format)) + "/" +
           std::to_string(element.slotIndex) + "/" +
           std::to_string(element.alignedByteOffset);
  }
  return key;
}

plume::RenderPipeline* GetOrCreateFablePipelineLocked(
    plume::RenderDevice* device, const RendererDrawEventSnapshot& draw_event,
    const std::vector<plume::RenderInputSlot>& input_slots,
    const std::vector<plume::RenderInputElement>& input_elements,
    const FableDrawTargetDesc& target,
    plume::RenderShaderFormat shader_format,
    bool private_texture_copy_pixel_shader) {
  if (!EnsureFablePipelineLayoutLocked(device)) {
    return nullptr;
  }

  const PlumeTrackedResource* vertex_shader = FindTrackedResourceLocked(
      draw_event.vertex_shader, RendererTrackedResourceKind::kVertexShader);
  const PlumeTrackedResource* pixel_shader = FindTrackedResourceLocked(
      draw_event.pixel_shader, RendererTrackedResourceKind::kPixelShader);
  if (!vertex_shader || !vertex_shader->shader || !pixel_shader ||
      !pixel_shader->shader) {
    return nullptr;
  }

  const plume::RenderPrimitiveTopology topology =
      ConvertPrimitiveTopology(draw_event.draw.primitive_type);
  if (topology == plume::RenderPrimitiveTopology::UNKNOWN) {
    return nullptr;
  }

  const bool use_rectangle_list_geometry_shader =
      ShouldUseRectangleListGeometryShader(draw_event, *vertex_shader);
  if (private_texture_copy_pixel_shader &&
      !EnsurePrivateTextureCopyPixelShaderLocked(device, shader_format)) {
    return nullptr;
  }
  const bool use_private_texture_copy_pixel_shader =
      private_texture_copy_pixel_shader;
  const FablePipelineRenderState render_state =
      BuildFablePipelineRenderState(draw_event, target);
  const PlumeTrackedResource* vertex_declaration = FindTrackedResourceLocked(
      draw_event.vertex_declaration, RendererTrackedResourceKind::kVertexDeclaration);
  const bool has_r11g11b10_normal =
      vertex_declaration && vertex_declaration->has_r11g11b10_normal;
  const uint32_t spec_constant_bits =
      (has_r11g11b10_normal ? kFableSpecConstantR11G11B10Normal : 0u) |
      (render_state.alpha_test_enabled ? kFableSpecConstantAlphaTest : 0u) |
      (FableViewportUsesReverseZ(draw_event) ? kFableSpecConstantReverseZ : 0u);
  const std::string key =
      MakePipelineKey(*vertex_shader, *pixel_shader, input_slots,
                      input_elements, topology, render_state, target) +
      (use_rectangle_list_geometry_shader ? ":rect_gs=1" : ":rect_gs=0") +
      (use_private_texture_copy_pixel_shader ? ":private_copy_ps=1"
                                             : ":private_copy_ps=0") +
      ":spec=" + std::to_string(spec_constant_bits);
  auto pipeline_it = g_plume_resources.pipelines.find(key);
  if (pipeline_it != g_plume_resources.pipelines.end()) {
    return pipeline_it->second.get();
  }
  if (use_rectangle_list_geometry_shader &&
      !EnsureRectangleListGeometryShaderLocked(device, shader_format)) {
    return nullptr;
  }

  plume::RenderGraphicsPipelineDesc pipeline_desc;
  const plume::RenderSpecConstant spec_constants[] = {
      {0, spec_constant_bits},
  };
  pipeline_desc.pipelineLayout = g_plume_resources.pipeline_layout.get();
  pipeline_desc.vertexShader = vertex_shader->shader.get();
  pipeline_desc.geometryShader =
      use_rectangle_list_geometry_shader
          ? g_plume_resources.rectangle_list_geometry_shader.get()
          : nullptr;
  pipeline_desc.pixelShader =
      use_private_texture_copy_pixel_shader
          ? g_plume_resources.private_texture_copy_pixel_shader.get()
          : pixel_shader->shader.get();
  const uint32_t render_target_count =
      FablePipelineRenderTargetCount(target);
  for (uint32_t i = 0; i < render_target_count; ++i) {
    pipeline_desc.renderTargetFormat[i] =
        FablePipelineRenderTargetFormat(target, i);
    pipeline_desc.renderTargetBlend[i] = render_state.blend;
  }
  pipeline_desc.renderTargetCount = render_target_count;
  pipeline_desc.depthFunction = render_state.depth_function;
  pipeline_desc.depthTargetFormat = EffectiveFableDepthFormat(target);
  pipeline_desc.depthEnabled = render_state.depth_enabled;
  pipeline_desc.depthWriteEnabled = render_state.depth_write_enabled;
  pipeline_desc.stencilEnabled = render_state.stencil_enabled;
  pipeline_desc.stencilReadMask = render_state.stencil_read_mask;
  pipeline_desc.stencilWriteMask = render_state.stencil_write_mask;
  pipeline_desc.stencilReference = render_state.stencil_reference;
  pipeline_desc.stencilFrontFace = render_state.stencil_front_face;
  pipeline_desc.stencilBackFace = render_state.stencil_back_face;
  pipeline_desc.inputSlots = input_slots.data();
  pipeline_desc.inputSlotsCount = static_cast<uint32_t>(input_slots.size());
  pipeline_desc.inputElements = input_elements.data();
  pipeline_desc.inputElementsCount =
      static_cast<uint32_t>(input_elements.size());
  pipeline_desc.specConstants = spec_constants;
  pipeline_desc.specConstantsCount = std::size(spec_constants);
  pipeline_desc.primitiveTopology = topology;
  pipeline_desc.cullMode = render_state.cull_mode;
  pipeline_desc.frontFace = render_state.front_face;

  std::unique_ptr<plume::RenderPipeline> pipeline =
      device->createGraphicsPipeline(pipeline_desc);
  if (!pipeline) {
    REXLOG_WARN(
        "Refii plume resource manager could not create Fable pipeline "
        "vs=0x{:08X} ps=0x{:08X} decl=0x{:08X} elements={} slots={} prim={}",
        draw_event.vertex_shader, draw_event.pixel_shader,
        draw_event.vertex_declaration, input_elements.size(),
        input_slots.size(), draw_event.draw.primitive_type);
    return nullptr;
  }

  char name[192] = {};
  std::snprintf(
      name, sizeof(name),
      "Refii Fable Pipeline VS%08X PS%08X prim=%u decl=%08X rt=%08X depth=%08X",
      draw_event.vertex_shader, draw_event.pixel_shader,
      draw_event.draw.primitive_type, draw_event.vertex_declaration,
      draw_event.render_targets[0], draw_event.depth_stencil);
  pipeline->setName(name);
  plume::RenderPipeline* pipeline_ptr = pipeline.get();
  g_plume_resources.pipelines.emplace(key, std::move(pipeline));
  REXLOG_INFO(
      "Refii plume resource manager created Fable pipeline vs=0x{:08X} "
      "ps=0x{:08X} decl=0x{:08X} elements={} slots={} prim={} "
      "rt_count={} rt0_fmt={} depth_fmt={} pm4={} rb_mode=0x{:08X} "
      "rb_color_mask=0x{:08X} write_mask=0x{:02X} rb_depth=0x{:08X} "
      "rb_blend0=0x{:08X} rb_colorctl=0x{:08X} "
      "depth={}/{} zfunc={} cull={}/{} blend={}/{}/{} alpha_test={} stencil={} "
      "stencil_func={} stencil_ops={}/{}/{} stencil_mask=0x{:02X} ref=0x{:02X} "
      "rect_gs={} private_copy_ps={} vs_name={} ps_name={}",
      draw_event.vertex_shader, draw_event.pixel_shader,
      draw_event.vertex_declaration, input_elements.size(), input_slots.size(),
      draw_event.draw.primitive_type, render_target_count,
      static_cast<uint32_t>(pipeline_desc.renderTargetFormat[0]),
      static_cast<uint32_t>(pipeline_desc.depthTargetFormat),
      draw_event.pm4_registers_valid ? 1 : 0, draw_event.rb_modecontrol,
      draw_event.rb_color_mask, render_state.blend.renderTargetWriteMask,
      draw_event.rb_depthcontrol, draw_event.rb_blendcontrol[0],
      draw_event.rb_colorcontrol, render_state.depth_enabled ? 1 : 0,
      render_state.depth_write_enabled ? 1 : 0,
      static_cast<uint32_t>(render_state.depth_function),
      static_cast<uint32_t>(render_state.cull_mode),
      static_cast<uint32_t>(render_state.front_face),
      render_state.blend.blendEnabled ? 1 : 0,
      static_cast<uint32_t>(render_state.blend.srcBlend),
      static_cast<uint32_t>(render_state.blend.dstBlend),
      render_state.alpha_test_enabled ? 1 : 0,
      render_state.stencil_enabled ? 1 : 0,
      static_cast<uint32_t>(render_state.stencil_front_face.compareFunction),
      static_cast<uint32_t>(render_state.stencil_front_face.failOp),
      static_cast<uint32_t>(render_state.stencil_front_face.depthFailOp),
      static_cast<uint32_t>(render_state.stencil_front_face.passOp),
      render_state.stencil_read_mask, render_state.stencil_reference,
      use_rectangle_list_geometry_shader ? 1 : 0,
      use_private_texture_copy_pixel_shader ? 1 : 0,
      vertex_shader->shader_name,
      pixel_shader->shader_name);
  return pipeline_ptr;
}

std::string MakeFramebufferKey(const FableDrawTargetDesc& target) {
  std::string key = std::to_string(target.color_count);
  for (uint32_t i = 0; i < target.color_count; ++i) {
    key += ":" + std::to_string(target.color_addresses[i]) + "/" +
           std::to_string(target.color_widths[i]) + "x" +
           std::to_string(target.color_heights[i]) + "/" +
           std::to_string(static_cast<uint32_t>(target.color_formats[i]));
  }
  key += ":depth:" + std::to_string(target.depth_address) + "/" +
         std::to_string(target.depth_width) + "x" +
         std::to_string(target.depth_height) + "/" +
         std::to_string(static_cast<uint32_t>(target.depth_format));
  return key;
}

plume::RenderFramebuffer* GetOrCreateFableFramebufferLocked(
    plume::RenderDevice* device, const FableDrawTargetDesc& target) {
  if (!device || target.uses_output) {
    return nullptr;
  }
  if (target.has_color) {
    for (uint32_t i = 0; i < target.color_count; ++i) {
      if (!target.color_textures[i] || !target.color_views[i]) {
        LogUnmatchedRenderTargetLocked(target.color_addresses[i]);
        return nullptr;
      }
      if (target.color_addresses[i] != 0 &&
          !FindTrackedColorRenderTargetLocked(target.color_addresses[i])) {
        LogUnmatchedRenderTargetLocked(target.color_addresses[i]);
        return nullptr;
      }
    }
  }
  if ((!target.has_color || !target.color_texture) &&
      (!target.has_depth || !target.depth_texture)) {
    return nullptr;
  }

  const std::string key = MakeFramebufferKey(target);
  auto framebuffer_it = g_plume_resources.framebuffers.find(key);
  if (framebuffer_it != g_plume_resources.framebuffers.end()) {
    return framebuffer_it->second.get();
  }
  if (g_plume_resources.framebuffers.size() >=
      kMaxFableFramebufferCacheEntries) {
    RetireFableFramebuffersLocked();
  }

  std::array<const plume::RenderTexture*, kMaxFableRenderTargets>
      color_attachments = {};
  std::array<const plume::RenderTextureView*, kMaxFableRenderTargets>
      color_attachment_views = {};
  for (uint32_t i = 0; i < target.color_count; ++i) {
    color_attachments[i] = target.color_textures[i];
    color_attachment_views[i] = target.color_views[i];
  }
  plume::RenderFramebufferDesc framebuffer_desc;
  framebuffer_desc.colorAttachments =
      target.has_color ? color_attachments.data() : nullptr;
  framebuffer_desc.colorAttachmentViews =
      target.has_color ? color_attachment_views.data() : nullptr;
  framebuffer_desc.colorAttachmentsCount = target.color_count;
  framebuffer_desc.depthAttachment = target.depth_texture;
  framebuffer_desc.depthAttachmentView = target.depth_view;

  std::unique_ptr<plume::RenderFramebuffer> framebuffer =
      device->createFramebuffer(framebuffer_desc);
  if (!framebuffer) {
    REXLOG_WARN(
        "Refii plume resource manager could not create Fable framebuffer "
        "rt=0x{:08X} depth=0x{:08X} color_fmt={} depth_fmt={}",
        target.color_address, target.depth_address,
        static_cast<uint32_t>(target.color_format),
        static_cast<uint32_t>(target.depth_format));
    return nullptr;
  }

  plume::RenderFramebuffer* framebuffer_ptr = framebuffer.get();
  g_plume_resources.framebuffers.emplace(key, std::move(framebuffer));
  return framebuffer_ptr;
}

void BindFableDrawTexturesLocked(plume::RenderCommandList* command_list,
                                 const RendererDrawEventSnapshot& draw_event,
                                 const PlumeDepthSampleCopy* depth_sample_copy,
                                 const PlumeTrackedResource* depth_sample_source,
                                 const PlumeDepthSampleCopySlots*
                                     depth_sample_copies,
                                 const PlumeColorSampleCopySlots*
                                     color_sample_copies,
                                 const FablePixVideoPlaneSet&
                                     decoded_pix_video_planes,
                                 bool fable_video_ycrcb_shader,
                                 FableDrawDescriptorIndices& descriptor_indices) {
  constexpr uint32_t kFableDepthTextureSamplerSlot = 13;
  const bool fable_pix_video_rectangle =
      !fable_video_ycrcb_shader &&
      IsFableSingleSourcePixVideoRectangleDrawLocked(draw_event);
  std::unordered_set<plume::RenderTexture*> shader_read_barriers;
  const auto barrier_texture_for_shader_read =
      [&](plume::RenderTexture* texture) {
        if (!command_list || !texture ||
            !shader_read_barriers.insert(texture).second) {
          return;
        }
        command_list->barriers(
            plume::RenderBarrierStage::GRAPHICS,
            plume::RenderTextureBarrier(texture,
                                        plume::RenderTextureLayout::SHADER_READ));
      };
  const auto set_texture_slot_descriptor =
      [&](uint32_t set_index, uint32_t slot, plume::RenderTexture* texture,
          plume::RenderTextureLayout layout,
          plume::RenderTextureView* view) -> uint32_t {
        if (slot >= kMaxFableTextureSlots) {
          return 0;
        }
        const uint32_t descriptor = slot + 1u;
        if (!SetFableTextureDescriptorAtIndexLocked(set_index, descriptor,
                                                    texture, layout, view)) {
          return 0;
        }
        switch (set_index) {
          case 0:
            descriptor_indices.texture2d[slot] = descriptor;
            break;
          case 1:
            descriptor_indices.texture3d[slot] = descriptor;
            break;
          case 2:
            descriptor_indices.texture_cube[slot] = descriptor;
            break;
          default:
            break;
        }
        return descriptor;
      };
  if (decoded_pix_video_planes &&
      g_plume_resources.logged_fable_pix_external_plane_bindings < 8u) {
    REXLOG_INFO(
        "Refii plume binding Fable Pix decoded planes "
        "#{} Y=0x{:08X} CR=0x{:08X} CB=0x{:08X} size={}x{} chroma={}x{} "
        "indexed={} prim={} ps=0x{:08X} private_rect={} src_slot={}",
        g_plume_resources.logged_fable_pix_external_plane_bindings + 1u,
        decoded_pix_video_planes.y->guest_address,
        decoded_pix_video_planes.cr->guest_address,
        decoded_pix_video_planes.cb->guest_address,
        decoded_pix_video_planes.y->texture_width,
        decoded_pix_video_planes.y->texture_height,
        decoded_pix_video_planes.cr->texture_width,
        decoded_pix_video_planes.cr->texture_height,
        draw_event.draw.indexed ? 1 : 0, draw_event.draw.primitive_type,
        draw_event.pixel_shader, draw_event.private_texture_rectangle ? 1 : 0,
        draw_event.private_texture_slot);
    ++g_plume_resources.logged_fable_pix_external_plane_bindings;
  }
  const uint32_t fable_pix_video_texture =
      fable_pix_video_rectangle &&
              draw_event.private_texture_slot < std::size(draw_event.textures)
          ? draw_event.textures[draw_event.private_texture_slot]
          : 0;
  const PlumeTrackedResource* fable_pix_video_plane_source =
      fable_pix_video_rectangle
          ? FindTrackedSampledImageLocked(fable_pix_video_texture, draw_event,
                                          draw_event.private_texture_slot)
          : nullptr;
  const PlumeColorSampleCopy* fable_pix_video_feedback_copy =
      fable_pix_video_rectangle && color_sample_copies &&
              draw_event.private_texture_slot < color_sample_copies->size()
          ? (*color_sample_copies)[draw_event.private_texture_slot]
          : nullptr;
  for (uint32_t slot = 0; slot < kMaxFableTextureSlots; ++slot) {
    if (const PlumeTrackedResource* decoded_plane =
            decoded_pix_video_planes.plane(slot)) {
      barrier_texture_for_shader_read(decoded_plane->texture.get());
      set_texture_slot_descriptor(
          0, slot, decoded_plane->texture.get(),
          plume::RenderTextureLayout::SHADER_READ,
          TextureViewForFablePixVideoPlaneLocked(decoded_plane, draw_event,
                                                 slot, slot));
      continue;
    }
    if (fable_pix_video_feedback_copy &&
        std::find(std::begin(kFablePixVideoPlaneSlots),
                  std::end(kFablePixVideoPlaneSlots),
                  slot) != std::end(kFablePixVideoPlaneSlots)) {
      barrier_texture_for_shader_read(fable_pix_video_feedback_copy->texture.get());
      set_texture_slot_descriptor(
          0, slot, fable_pix_video_feedback_copy->texture.get(),
          plume::RenderTextureLayout::SHADER_READ,
          TextureViewForFableColorSampleCopyPlaneLocked(
              fable_pix_video_feedback_copy, slot));
      continue;
    }
    if (fable_pix_video_plane_source &&
        std::find(std::begin(kFablePixVideoPlaneSlots),
                  std::end(kFablePixVideoPlaneSlots),
                  slot) != std::end(kFablePixVideoPlaneSlots)) {
      const uint32_t slot_texture = draw_event.textures[slot];
      const bool use_direct_slot_texture =
          slot != draw_event.private_texture_slot && slot_texture != 0 &&
          slot_texture != fable_pix_video_texture &&
          !IsFableVideoNeutralPlaneTextureLocked(slot_texture, draw_event,
                                                 slot);
      if (use_direct_slot_texture) {
        // Fable 2 can bind decoded chroma textures explicitly in slots 1/2.
        // Preserve real direct bindings, while neutral placeholders fall back
        // to the expanded video plane view below.
      } else {
        barrier_texture_for_shader_read(fable_pix_video_plane_source->texture.get());
        const uint32_t set_index =
            fable_pix_video_plane_source->texture_view_dimension ==
                    plume::RenderTextureViewDimension::TEXTURE_3D
                ? 1u
                : (fable_pix_video_plane_source->texture_view_dimension ==
                           plume::RenderTextureViewDimension::TEXTURE_CUBE
                       ? 2u
                       : 0u);
        set_texture_slot_descriptor(
            set_index, slot, fable_pix_video_plane_source->texture.get(),
            plume::RenderTextureLayout::SHADER_READ,
            TextureViewForFablePixVideoPlaneLocked(
                fable_pix_video_plane_source, draw_event,
                draw_event.private_texture_slot, slot));
        continue;
      }
    }
    if (depth_sample_copies && slot < depth_sample_copies->size() &&
        (*depth_sample_copies)[slot] &&
        (*depth_sample_copies)[slot]->texture &&
        (*depth_sample_copies)[slot]->view) {
      set_texture_slot_descriptor(
          0, slot, (*depth_sample_copies)[slot]->texture.get(),
          plume::RenderTextureLayout::SHADER_READ,
          (*depth_sample_copies)[slot]->view.get());
      continue;
    }
    if (color_sample_copies && slot < color_sample_copies->size() &&
        (*color_sample_copies)[slot] &&
        (*color_sample_copies)[slot]->texture &&
        (*color_sample_copies)[slot]->view) {
      set_texture_slot_descriptor(
          0, slot, (*color_sample_copies)[slot]->texture.get(),
          plume::RenderTextureLayout::SHADER_READ,
          (*color_sample_copies)[slot]->view.get());
      continue;
    }
    if (slot == kFableDepthTextureSamplerSlot && depth_sample_copy &&
        depth_sample_copy->texture && depth_sample_copy->view) {
      set_texture_slot_descriptor(
          0, slot, depth_sample_copy->texture.get(),
          plume::RenderTextureLayout::SHADER_READ, depth_sample_copy->view.get());
      continue;
    }
    if (slot == kFableDepthTextureSamplerSlot && depth_sample_source &&
        depth_sample_source->texture && depth_sample_source->texture_view) {
      barrier_texture_for_shader_read(depth_sample_source->texture.get());
      set_texture_slot_descriptor(
          0, slot, depth_sample_source->texture.get(),
          plume::RenderTextureLayout::SHADER_READ,
          TextureViewForFableDrawSlotLocked(depth_sample_source, draw_event,
                                            slot));
      continue;
    }

    const uint32_t guest_texture =
        slot < std::size(draw_event.textures) ? draw_event.textures[slot] : 0;
    const bool has_fetch_constant =
        DrawEventHasTextureFetchConstant(draw_event, slot);
    if (guest_texture == 0 && !has_fetch_constant) {
      continue;
    }
    const PlumeTrackedResource* texture =
        FindTrackedSampledImageLocked(guest_texture, draw_event, slot);
    if (!texture || !texture->texture || !texture->texture_view) {
      continue;
    }
    barrier_texture_for_shader_read(texture->texture.get());

    const uint32_t set_index =
        texture->texture_view_dimension ==
                plume::RenderTextureViewDimension::TEXTURE_3D
            ? 1u
            : (texture->texture_view_dimension ==
                       plume::RenderTextureViewDimension::TEXTURE_CUBE
                   ? 2u
                   : 0u);
    if (set_index >= std::size(g_plume_resources.texture_descriptor_sets) ||
        !g_plume_resources.texture_descriptor_sets[set_index]) {
      continue;
    }

    set_texture_slot_descriptor(
        set_index, slot, texture->texture.get(),
        plume::RenderTextureLayout::SHADER_READ,
        fable_video_ycrcb_shader &&
                std::find(std::begin(kFablePixVideoPlaneSlots),
                          std::end(kFablePixVideoPlaneSlots),
                          slot) != std::end(kFablePixVideoPlaneSlots)
            ? TextureViewForFableVideoPlaneSlotLocked(texture, draw_event, slot)
            : TextureViewForFableDrawSlotLocked(texture, draw_event, slot));
  }
}

constexpr uint32_t kPackedSamplerStateValid = 0x80000000u;
constexpr uint32_t kPackedSamplerStateMagLinear = 0x00000001u;
constexpr uint32_t kPackedSamplerStateMinLinear = 0x00000002u;
constexpr uint32_t kPackedSamplerStateMipLinear = 0x00000008u;
constexpr uint32_t kPackedSamplerStateAddressUShift = 8u;
constexpr uint32_t kPackedSamplerStateAddressVShift = 11u;
constexpr uint32_t kPackedSamplerStateAddressWShift = 14u;
constexpr uint32_t kPackedSamplerStateAddressMask = 0x7u;

bool LoadTextureFetchConstant(const RendererDrawEventSnapshot& draw_event,
                              uint32_t slot,
                              rex::graphics::xenos::xe_gpu_texture_fetch_t& fetch) {
  constexpr uint32_t kTextureFetchConstantDwords = 6;
  const uint32_t fetch_dword = slot * kTextureFetchConstantDwords;
  if (slot < 32u &&
      fetch_dword + kTextureFetchConstantDwords <=
          draw_event.fetch_constants.size()) {
    fetch.dword_0 = draw_event.fetch_constants[fetch_dword + 0u];
    fetch.dword_1 = draw_event.fetch_constants[fetch_dword + 1u];
    fetch.dword_2 = draw_event.fetch_constants[fetch_dword + 2u];
    fetch.dword_3 = draw_event.fetch_constants[fetch_dword + 3u];
    fetch.dword_4 = draw_event.fetch_constants[fetch_dword + 4u];
    fetch.dword_5 = draw_event.fetch_constants[fetch_dword + 5u];
    if ((fetch.dword_0 | fetch.dword_1 | fetch.dword_2 | fetch.dword_3 |
         fetch.dword_4 | fetch.dword_5) != 0) {
      return true;
    }
  }

  if (slot < std::size(draw_event.textures)) {
    const PlumeTrackedResource* object_texture =
        FindTrackedSampledImageByObjectAllowDepthLocked(
            draw_event.textures[slot]);
    if (object_texture &&
        std::any_of(object_texture->texture_fetch_dwords.begin(),
                    object_texture->texture_fetch_dwords.end(),
                    [](uint32_t dword) { return dword != 0; })) {
      fetch.dword_0 = object_texture->texture_fetch_dwords[0];
      fetch.dword_1 = object_texture->texture_fetch_dwords[1];
      fetch.dword_2 = object_texture->texture_fetch_dwords[2];
      fetch.dword_3 = object_texture->texture_fetch_dwords[3];
      fetch.dword_4 = object_texture->texture_fetch_dwords[4];
      fetch.dword_5 = object_texture->texture_fetch_dwords[5];
      return true;
    }
  }

  return false;
}

uint32_t PackFableSamplerClampMode(uint32_t packed_state,
                                   uint32_t shift,
                                   rex::graphics::xenos::ClampMode clamp_mode) {
  packed_state &= ~(kPackedSamplerStateAddressMask << shift);
  return packed_state |
         ((static_cast<uint32_t>(clamp_mode) & kPackedSamplerStateAddressMask)
          << shift);
}

rex::graphics::xenos::ClampMode UnpackFableSamplerClampMode(
    uint32_t packed_state, uint32_t shift) {
  return static_cast<rex::graphics::xenos::ClampMode>(
      (packed_state >> shift) & kPackedSamplerStateAddressMask);
}

plume::RenderTextureAddressMode FableClampModeToPlumeAddressMode(
    rex::graphics::xenos::ClampMode clamp_mode) {
  namespace xenos = rex::graphics::xenos;
  switch (clamp_mode) {
    case xenos::ClampMode::kRepeat:
      return plume::RenderTextureAddressMode::WRAP;
    case xenos::ClampMode::kMirroredRepeat:
      return plume::RenderTextureAddressMode::MIRROR;
    case xenos::ClampMode::kClampToBorder:
      return plume::RenderTextureAddressMode::BORDER;
    case xenos::ClampMode::kMirrorClampToEdge:
    case xenos::ClampMode::kMirrorClampToHalfway:
    case xenos::ClampMode::kMirrorClampToBorder:
      return plume::RenderTextureAddressMode::MIRROR_ONCE;
    case xenos::ClampMode::kClampToEdge:
    case xenos::ClampMode::kClampToHalfway:
    default:
      return plume::RenderTextureAddressMode::CLAMP;
  }
}

uint32_t PackFableSamplerStateFromFetch(
    uint32_t packed_state, const RendererDrawEventSnapshot& draw_event,
    uint32_t slot) {
  namespace xenos = rex::graphics::xenos;

  xenos::xe_gpu_texture_fetch_t fetch = {};
  if (!LoadTextureFetchConstant(draw_event, slot, fetch)) {
    return packed_state;
  }
  if (fetch.type != xenos::FetchConstantType::kTexture) {
    return packed_state;
  }

  xenos::ClampMode clamp_x = xenos::ClampMode::kRepeat;
  xenos::ClampMode clamp_y = xenos::ClampMode::kRepeat;
  xenos::ClampMode clamp_z = xenos::ClampMode::kRepeat;
  rex::graphics::texture_util::GetClampModesForDimension(fetch, clamp_x,
                                                         clamp_y, clamp_z);

  if ((packed_state & kPackedSamplerStateValid) == 0) {
    packed_state = kPackedSamplerStateValid | kPackedSamplerStateMagLinear |
                   kPackedSamplerStateMinLinear | kPackedSamplerStateMipLinear;
  }
  packed_state = PackFableSamplerClampMode(
      packed_state, kPackedSamplerStateAddressUShift, clamp_x);
  packed_state = PackFableSamplerClampMode(
      packed_state, kPackedSamplerStateAddressVShift, clamp_y);
  packed_state = PackFableSamplerClampMode(
      packed_state, kPackedSamplerStateAddressWShift, clamp_z);
  return packed_state;
}

plume::RenderSamplerDesc BuildFableSamplerDesc(uint32_t packed_state) {
  plume::RenderSamplerDesc desc;
  if ((packed_state & kPackedSamplerStateValid) == 0) {
    return desc;
  }

  desc.magFilter = (packed_state & kPackedSamplerStateMagLinear) != 0
                       ? plume::RenderFilter::LINEAR
                       : plume::RenderFilter::NEAREST;
  desc.minFilter = (packed_state & kPackedSamplerStateMinLinear) != 0
                       ? plume::RenderFilter::LINEAR
                       : plume::RenderFilter::NEAREST;
  desc.mipmapMode = (packed_state & kPackedSamplerStateMipLinear) != 0
                        ? plume::RenderMipmapMode::LINEAR
                        : plume::RenderMipmapMode::NEAREST;
  desc.addressU = FableClampModeToPlumeAddressMode(
      UnpackFableSamplerClampMode(packed_state,
                                  kPackedSamplerStateAddressUShift));
  desc.addressV = FableClampModeToPlumeAddressMode(
      UnpackFableSamplerClampMode(packed_state,
                                  kPackedSamplerStateAddressVShift));
  desc.addressW = FableClampModeToPlumeAddressMode(
      UnpackFableSamplerClampMode(packed_state,
                                  kPackedSamplerStateAddressWShift));
  desc.anisotropyEnabled = false;
  return desc;
}

plume::RenderSampler* GetOrCreateFableSamplerLocked(plume::RenderDevice* device,
                                                    uint32_t packed_state) {
  if (!device) {
    return g_plume_resources.null_sampler.get();
  }

  const uint32_t key = (packed_state & kPackedSamplerStateValid) != 0
                           ? packed_state
                           : 0u;
  auto it = g_plume_resources.fable_samplers.find(key);
  if (it != g_plume_resources.fable_samplers.end()) {
    return it->second.get();
  }

  std::unique_ptr<plume::RenderSampler> sampler =
      device->createSampler(BuildFableSamplerDesc(packed_state));
  if (!sampler) {
    return g_plume_resources.null_sampler.get();
  }

  plume::RenderSampler* sampler_ptr = sampler.get();
  g_plume_resources.fable_samplers.emplace(key, std::move(sampler));
  return sampler_ptr;
}

void BindFableDrawSamplersLocked(plume::RenderDevice* device,
                                 const RendererDrawEventSnapshot& draw_event,
                                 FableDrawDescriptorIndices& descriptor_indices) {
  if (!g_plume_resources.sampler_descriptor_set) {
    return;
  }

  if (IsFablePixVideoRectangleDraw(draw_event)) {
    const uint32_t source_slot =
        std::min<uint32_t>(draw_event.private_texture_slot,
                           kMaxFableTextureSlots - 1u);
    uint32_t packed_state = source_slot < draw_event.sampler_states.size()
                                ? draw_event.sampler_states[source_slot]
                                : 0u;
    packed_state =
        PackFableSamplerStateFromFetch(packed_state, draw_event, source_slot);
    plume::RenderSampler* sampler =
        GetOrCreateFableSamplerLocked(device, packed_state);
    if (!sampler) {
      sampler = g_plume_resources.null_sampler.get();
    }
    for (uint32_t slot : kFablePixVideoPlaneSlots) {
      if (slot < descriptor_indices.samplers.size()) {
        const uint32_t descriptor = slot + 1u;
        if (SetFableSamplerDescriptorAtIndexLocked(descriptor, sampler)) {
          descriptor_indices.samplers[slot] = descriptor;
        }
      }
    }
  }

  for (uint32_t slot = 0; slot < kMaxFableTextureSlots; ++slot) {
    const uint32_t guest_texture =
        slot < std::size(draw_event.textures) ? draw_event.textures[slot] : 0;
    if (guest_texture == 0 && !DrawEventHasTextureFetchConstant(draw_event, slot)) {
      continue;
    }

    uint32_t packed_state =
        slot < draw_event.sampler_states.size() ? draw_event.sampler_states[slot]
                                                : 0u;
    packed_state = PackFableSamplerStateFromFetch(packed_state, draw_event, slot);
    plume::RenderSampler* sampler =
        GetOrCreateFableSamplerLocked(device, packed_state);
    if (!sampler) {
      sampler = g_plume_resources.null_sampler.get();
    }
    const uint32_t descriptor = slot + 1u;
    if (SetFableSamplerDescriptorAtIndexLocked(descriptor, sampler)) {
      descriptor_indices.samplers[slot] = descriptor;
    }
  }
}

uint32_t FableDrawTargetWidth(const FableDrawTargetDesc& target) {
  if (target.has_color && target.color_width != 0) {
    return target.color_width;
  }
  if (target.has_depth && target.depth_width != 0) {
    return target.depth_width;
  }
  return static_cast<uint32_t>(
      std::max(1, RendererOutputWidth()) * std::max(1, RendererInternalScale()));
}

uint32_t FableDrawTargetHeight(const FableDrawTargetDesc& target) {
  if (target.has_color && target.color_height != 0) {
    return target.color_height;
  }
  if (target.has_depth && target.depth_height != 0) {
    return target.depth_height;
  }
  return static_cast<uint32_t>(std::max(1, RendererOutputHeight()) *
                               std::max(1, RendererInternalScale()));
}

plume::RenderViewport BuildFableDrawViewport(
    const RendererDrawEventSnapshot& draw_event,
    const FableDrawTargetDesc& target) {
  const float target_width =
      static_cast<float>(std::max<uint32_t>(1, FableDrawTargetWidth(target)));
  const float target_height =
      static_cast<float>(std::max<uint32_t>(1, FableDrawTargetHeight(target)));

  if (draw_event.viewport.valid) {
    const float x = std::clamp(draw_event.viewport.x, 0.0f,
                               std::max(0.0f, target_width - 1.0f));
    const float y = std::clamp(draw_event.viewport.y, 0.0f,
                               std::max(0.0f, target_height - 1.0f));
    const float width =
        std::clamp(draw_event.viewport.width, 1.0f, target_width - x);
    const float height =
        std::clamp(draw_event.viewport.height, 1.0f, target_height - y);
    const float min_depth =
        std::clamp(draw_event.viewport.min_depth, 0.0f, 1.0f);
    const float max_depth =
        std::clamp(draw_event.viewport.max_depth, 0.0f, 1.0f);
    // Fable uses reverse-Z viewports (minZ > maxZ) and the generated shaders
    // receive a matching reverse-Z spec constant. Preserve the guest depth
    // range here instead of normalizing it.
    return plume::RenderViewport(x, y, width, height, min_depth, max_depth);
  }

  return plume::RenderViewport(0.0f, 0.0f, target_width, target_height, 0.0f,
                               1.0f);
}

plume::RenderRect BuildFableDrawScissor(
    const RendererDrawEventSnapshot& draw_event,
    const plume::RenderViewport& viewport) {
  const int32_t viewport_left = std::max(0, static_cast<int32_t>(viewport.x));
  const int32_t viewport_top = std::max(0, static_cast<int32_t>(viewport.y));
  const int32_t viewport_right =
      std::max(viewport_left + 1,
               static_cast<int32_t>(viewport.x + viewport.width));
  const int32_t viewport_bottom =
      std::max(viewport_top + 1,
               static_cast<int32_t>(viewport.y + viewport.height));

  if (!draw_event.scissor.valid) {
    return plume::RenderRect(viewport_left, viewport_top, viewport_right,
                             viewport_bottom);
  }

  const int32_t left =
      std::clamp(draw_event.scissor.left, viewport_left, viewport_right - 1);
  const int32_t top =
      std::clamp(draw_event.scissor.top, viewport_top, viewport_bottom - 1);
  const int32_t right =
      std::clamp(draw_event.scissor.right, left + 1, viewport_right);
  const int32_t bottom =
      std::clamp(draw_event.scissor.bottom, top + 1, viewport_bottom);
  return plume::RenderRect(left, top, right, bottom);
}

bool RecordReadyFableDrawLocked(plume::RenderDevice* device,
                                plume::RenderCommandList* command_list,
                                const RendererDrawEventSnapshot& draw_event,
                                const FableDrawTargetDesc& target,
                                plume::RenderPipeline* pipeline,
                                const std::vector<plume::RenderInputSlot>&
                                    input_slots,
                                const std::vector<plume::RenderInputElement>&
                                    input_elements,
                                const PlumeDepthSampleCopy* depth_sample_copy,
                                const PlumeTrackedResource* depth_sample_source,
                                const PlumeDepthSampleCopySlots*
                                    depth_sample_copies,
                                const PlumeColorSampleCopySlots*
                                    color_sample_copies,
                                const FableDrawShaderRouting& shader_routing) {
  if (!device || !command_list || !pipeline) {
    return false;
  }

  uint32_t first_slot = UINT32_MAX;
  uint32_t last_slot = 0;
  for (const plume::RenderInputSlot& input_slot : input_slots) {
    first_slot = std::min(first_slot, input_slot.index);
    last_slot = std::max(last_slot, input_slot.index);
  }
  if (first_slot == UINT32_MAX || last_slot >= std::size(draw_event.streams)) {
    return false;
  }

  std::vector<plume::RenderInputSlot> bound_input_slots;
  std::vector<plume::RenderVertexBufferView> vertex_buffer_views;
  bound_input_slots.reserve(last_slot - first_slot + 1);
  vertex_buffer_views.reserve(last_slot - first_slot + 1);
  for (uint32_t slot = first_slot; slot <= last_slot; ++slot) {
    const auto input_slot_it =
        std::find_if(input_slots.begin(), input_slots.end(),
                     [slot](const plume::RenderInputSlot& input_slot) {
                       return input_slot.index == slot;
                     });
    plume::RenderInputSlot bound_slot(
        slot, kDefaultVertexInputStride,
        plume::RenderInputSlotClassification::PER_VERTEX_DATA);
    if (input_slot_it != input_slots.end()) {
      bound_slot = *input_slot_it;
    }
    const RendererStreamBindingSnapshot& stream = draw_event.streams[slot];
    const PlumeTrackedResource* vertex_buffer = FindTrackedResourceLocked(
        stream.buffer, RendererTrackedResourceKind::kVertexBuffer);
    const uint32_t resolved_offset =
        vertex_buffer != nullptr
            ? ResolveTrackedBufferOffset(*vertex_buffer, stream.buffer,
                                         stream.offset)
            : stream.offset;
    if (vertex_buffer && vertex_buffer->buffer &&
        resolved_offset < vertex_buffer->size &&
        input_slot_it != input_slots.end() &&
        slot != kDefaultVertexInputSlot) {
      vertex_buffer_views.emplace_back(
          vertex_buffer->buffer->at(resolved_offset),
          vertex_buffer->size - resolved_offset);
    } else {
      if (!g_plume_resources.default_vertex_input_buffer) {
        return false;
      }
      vertex_buffer_views.emplace_back(
          g_plume_resources.default_vertex_input_buffer->at(0),
          kDefaultVertexInputBufferSize);
    }
    bound_input_slots.push_back(bound_slot);
  }

  const uint32_t count = DrawVertexOrIndexCount(
      draw_event.draw.primitive_type, draw_event.draw.primitive_count);
  if (count == 0) {
    return false;
  }
  const PlumeTrackedResource* draw_index_buffer =
      draw_event.draw.indexed
          ? FindTrackedResourceLocked(draw_event.index_buffer,
                                      RendererTrackedResourceKind::kIndexBuffer)
          : nullptr;
  const FableIndexRangeSummary index_summary =
      draw_event.draw.indexed
          ? AnalyzeIndexRange(draw_index_buffer, draw_event.draw.start, count)
          : FableIndexRangeSummary{};
  std::string indexed_draw_failure;
  if (draw_event.draw.indexed &&
      !IndexedDrawVertexFetchRangeValid(draw_event, input_elements,
                                        index_summary,
                                        &indexed_draw_failure)) {
    if (g_plume_resources.logged_invalid_indexed_draws < 16) {
      REXLOG_WARN(
          "Refii plume skipped invalid indexed Fable draw #{} prim={} "
          "ib=0x{:08X} {} reason={} decl=0x{:08X} vs=0x{:08X} "
          "ps=0x{:08X} rt0=0x{:08X} depth=0x{:08X}",
          g_plume_resources.logged_invalid_indexed_draws + 1u,
          draw_event.draw.primitive_type, draw_event.index_buffer,
          FormatIndexRangeSummary(index_summary), indexed_draw_failure,
          draw_event.vertex_declaration, draw_event.vertex_shader,
          draw_event.pixel_shader, draw_event.render_targets[0],
          draw_event.depth_stencil);
      ++g_plume_resources.logged_invalid_indexed_draws;
    }
    return false;
  }
  const bool implicit_fullscreen_rectangle =
      NeedsFableImplicitFullscreenRectangleVerticesLocked(
          draw_event, input_slots, input_elements, count);
  const bool private_texture_rectangle_replay =
      IsFablePrivateTextureRectangleGuiQuadLocked(draw_event, input_slots,
                                                  input_elements, count);
  if (private_texture_rectangle_replay && first_slot == 0 &&
      !vertex_buffer_views.empty()) {
    plume::RenderBuffer* replay_vertices =
        PrepareFablePrivateTextureRectangleVertexBufferLocked(device,
                                                              draw_event);
    if (replay_vertices) {
      vertex_buffer_views[0] = plume::RenderVertexBufferView(
          replay_vertices->at(0),
          sizeof(FablePrivateTextureRectangleReplayVertex) * 3u);
    }
  }
  if (implicit_fullscreen_rectangle && first_slot == 0 &&
      !vertex_buffer_views.empty()) {
    plume::RenderBuffer* implicit_vertices =
        PrepareFableImplicitFullscreenRectangleVertexBufferLocked(device);
    if (implicit_vertices) {
      vertex_buffer_views[0] =
          plume::RenderVertexBufferView(implicit_vertices->at(0),
                                        sizeof(float) * 2u * 3u);
    }
  }
  const uint64_t rectangle_shader_pair_key =
      (uint64_t(draw_event.vertex_shader) << 32u) | draw_event.pixel_shader;
  const bool log_rectangle_shader_pair =
      draw_event.draw.primitive_type == 8 &&
      g_plume_resources.logged_rectangle_shader_pairs.size() < 48 &&
      g_plume_resources.logged_rectangle_shader_pairs.insert(
                                                         rectangle_shader_pair_key)
          .second;
  if (RendererDrawTracingEnabled() && draw_event.draw.primitive_type == 8 &&
      (g_plume_resources.logged_rectangle_list_draws < 8 ||
       log_rectangle_shader_pair)) {
    const RendererStreamBindingSnapshot& stream0 = draw_event.streams[0];
    const PlumeTrackedResource* rect_declaration = FindTrackedResourceLocked(
        draw_event.vertex_declaration,
        RendererTrackedResourceKind::kVertexDeclaration);
    const PlumeTrackedResource* stream0_buffer = FindTrackedResourceLocked(
        stream0.buffer, RendererTrackedResourceKind::kVertexBuffer);
    const uint64_t stream0_fetch_end =
        DrawVertexFetchEndForSlot(draw_event, input_elements, 0, count,
                                  &index_summary);
    const uint64_t stream0_size =
        stream0_buffer != nullptr ? stream0_buffer->size : 0;
    const PlumeTrackedResource* rect_vertex_shader = FindTrackedResourceLocked(
        draw_event.vertex_shader, RendererTrackedResourceKind::kVertexShader);
    const PlumeTrackedResource* rect_pixel_shader = FindTrackedResourceLocked(
        draw_event.pixel_shader, RendererTrackedResourceKind::kPixelShader);
    const bool rect_gs =
        rect_vertex_shader &&
        ShouldUseRectangleListGeometryShader(draw_event, *rect_vertex_shader);
    REXLOG_INFO(
        "Refii plume Fable rectangle-list draw #{} indexed={} gs={} "
        "implicit_rect={} private_rect={} "
        "count={} start={} base={} decl=0x{:08X} vs=0x{:08X} ps=0x{:08X} "
        "rt0=0x{:08X} depth=0x{:08X} pm4={} rb_mode=0x{:08X} "
        "color_mask=0x{:08X} rb_surf=0x{:08X} "
        "rb_color={:08X}/{:08X}/{:08X}/{:08X} rb_depth=0x{:08X} "
        "stream0=0x{:08X}+{} stride={} "
        "vb0_size={} fetch_end={} fetch_oob={} input_slots={} "
        "input_elements={} cull_state={} vp={}/{}/{}/{} sc={}/{}/{}/{} "
        "layout={} {} sample0={} {} decl={} vs_name={} ps_name={}",
        g_plume_resources.logged_rectangle_list_draws + 1u,
        draw_event.draw.indexed ? 1 : 0, rect_gs ? 1 : 0,
        implicit_fullscreen_rectangle ? 1 : 0,
        private_texture_rectangle_replay ? 1 : 0, count,
        draw_event.draw.start, draw_event.draw.base_vertex,
        draw_event.vertex_declaration, draw_event.vertex_shader,
        draw_event.pixel_shader, draw_event.render_targets[0],
        draw_event.depth_stencil, draw_event.pm4_registers_valid ? 1 : 0,
        draw_event.rb_modecontrol, draw_event.rb_color_mask,
        draw_event.rb_surface_info, draw_event.rb_color_info[0],
        draw_event.rb_color_info[1], draw_event.rb_color_info[2],
        draw_event.rb_color_info[3], draw_event.rb_depth_info, stream0.buffer,
        stream0.offset, stream0.stride, stream0_size, stream0_fetch_end,
        stream0_size != 0 && stream0_fetch_end > stream0_size ? 1 : 0,
        input_slots.size(), input_elements.size(),
        draw_event.render_states[kFableRenderStateCullMode],
        draw_event.viewport.x, draw_event.viewport.y, draw_event.viewport.width,
        draw_event.viewport.height, draw_event.scissor.left,
        draw_event.scissor.top, draw_event.scissor.right,
        draw_event.scissor.bottom,
        FormatInputLayoutSummary(input_slots, input_elements),
        FormatBoundInputStreams(draw_event, input_slots, input_elements, count,
                                &index_summary),
        FormatTrackedBufferSample(stream0_buffer, stream0.offset, 96),
        FormatBoundInputStreamSamples(draw_event, input_slots, input_elements,
                                      96),
        rect_declaration != nullptr ? rect_declaration->debug_summary
                                    : "untracked",
        rect_vertex_shader != nullptr ? rect_vertex_shader->shader_name
                                      : "untracked",
        rect_pixel_shader != nullptr ? rect_pixel_shader->shader_name
                                     : "untracked");
    ++g_plume_resources.logged_rectangle_list_draws;
  }
  if (RendererDrawTracingEnabled() && draw_event.draw.primitive_type == 13 &&
      g_plume_resources.logged_quad_list_draws < 4) {
    const RendererStreamBindingSnapshot& stream0 = draw_event.streams[0];
    const PlumeTrackedResource* quad_declaration = FindTrackedResourceLocked(
        draw_event.vertex_declaration,
        RendererTrackedResourceKind::kVertexDeclaration);
    const PlumeTrackedResource* stream0_buffer = FindTrackedResourceLocked(
        stream0.buffer, RendererTrackedResourceKind::kVertexBuffer);
    const uint64_t stream0_fetch_end =
        DrawVertexFetchEndForSlot(draw_event, input_elements, 0, count,
                                  &index_summary);
    const uint64_t stream0_size =
        stream0_buffer != nullptr ? stream0_buffer->size : 0;
    REXLOG_INFO(
        "Refii plume Fable quad-list draw #{} indexed={} split={} "
        "count={} start={} base={} decl=0x{:08X} vs=0x{:08X} ps=0x{:08X} "
        "rt0=0x{:08X} depth=0x{:08X} stream0=0x{:08X}+{} stride={} "
        "vb0_size={} fetch_end={} fetch_oob={} input_slots={} "
        "input_elements={} vp={}/{}/{}/{} sc={}/{}/{}/{} layout={} "
        "{} sample0={} {} decl={}",
        g_plume_resources.logged_quad_list_draws + 1u,
        draw_event.draw.indexed ? 1 : 0,
        RendererQuadListStrip0132Order() ? "strip0132" : "sequential0123",
        count, draw_event.draw.start, draw_event.draw.base_vertex,
        draw_event.vertex_declaration, draw_event.vertex_shader,
        draw_event.pixel_shader, draw_event.render_targets[0],
        draw_event.depth_stencil, stream0.buffer, stream0.offset,
        stream0.stride, stream0_size, stream0_fetch_end,
        stream0_size != 0 && stream0_fetch_end > stream0_size ? 1 : 0,
        input_slots.size(), input_elements.size(),
        draw_event.viewport.x, draw_event.viewport.y, draw_event.viewport.width,
        draw_event.viewport.height, draw_event.scissor.left,
        draw_event.scissor.top, draw_event.scissor.right,
        draw_event.scissor.bottom,
        FormatInputLayoutSummary(input_slots, input_elements),
        FormatBoundInputStreams(draw_event, input_slots, input_elements, count,
                                &index_summary),
        FormatTrackedBufferSample(stream0_buffer, stream0.offset, 128),
        FormatBoundInputStreamSamples(draw_event, input_slots, input_elements,
                                      128),
        quad_declaration != nullptr ? quad_declaration->debug_summary
                                    : "untracked");
    ++g_plume_resources.logged_quad_list_draws;
  }
  const uint64_t triangle_shader_pair_key =
      (uint64_t(draw_event.vertex_shader) << 32u) | draw_event.pixel_shader;
  const bool log_triangle_shader_pair =
      draw_event.draw.primitive_type == 4 &&
      g_plume_resources.logged_triangle_shader_pairs.size() < 32 &&
      g_plume_resources.logged_triangle_shader_pairs.insert(
                                                         triangle_shader_pair_key)
          .second;
  if (RendererDrawTracingEnabled() && draw_event.draw.primitive_type == 4 &&
      (g_plume_resources.logged_triangle_list_draws < 12 ||
       log_triangle_shader_pair)) {
    const RendererStreamBindingSnapshot& stream0 = draw_event.streams[0];
    const PlumeTrackedResource* tri_declaration = FindTrackedResourceLocked(
        draw_event.vertex_declaration,
        RendererTrackedResourceKind::kVertexDeclaration);
    const PlumeTrackedResource* stream0_buffer = FindTrackedResourceLocked(
        stream0.buffer, RendererTrackedResourceKind::kVertexBuffer);
    const PlumeTrackedResource* tri_vertex_shader = FindTrackedResourceLocked(
        draw_event.vertex_shader, RendererTrackedResourceKind::kVertexShader);
    const PlumeTrackedResource* tri_pixel_shader = FindTrackedResourceLocked(
        draw_event.pixel_shader, RendererTrackedResourceKind::kPixelShader);
    const uint64_t stream0_fetch_end =
        DrawVertexFetchEndForSlot(draw_event, input_elements, 0, count,
                                  &index_summary);
    const uint64_t stream0_size =
        stream0_buffer != nullptr ? stream0_buffer->size : 0;
    const uint32_t edram_mode = draw_event.rb_modecontrol & 0x7u;
    const uint32_t write_mask =
        edram_mode == 4u
            ? draw_event.rb_color_mask &
                  uint32_t(plume::RenderColorWriteEnable::ALL)
            : 0u;
    REXLOG_INFO(
        "Refii plume Fable triangle-list draw #{} indexed={} count={} "
        "start={} base={} ib=0x{:08X} {} decl=0x{:08X} vs=0x{:08X} "
        "ps=0x{:08X} rt0=0x{:08X} depth=0x{:08X} pm4={} rb_mode=0x{:08X} "
        "edram={} color_mask=0x{:08X} write_mask=0x{:X} depth_ctl=0x{:08X} "
        "blend0=0x{:08X} colorctl=0x{:08X} stream0=0x{:08X}+{} stride={} "
        "vb0_size={} fetch_end={} fetch_oob={} input_slots={} "
        "input_elements={} vp={}/{}/{}/{} sc={}/{}/{}/{} layout={} {} "
        "{} sample0={} {} decl={} vs_name={} ps_name={}",
        g_plume_resources.logged_triangle_list_draws + 1u,
        draw_event.draw.indexed ? 1 : 0, count, draw_event.draw.start,
        draw_event.draw.base_vertex, draw_event.index_buffer,
        FormatIndexRangeSummary(index_summary),
        draw_event.vertex_declaration, draw_event.vertex_shader,
        draw_event.pixel_shader, draw_event.render_targets[0],
        draw_event.depth_stencil, draw_event.pm4_registers_valid ? 1 : 0,
        draw_event.rb_modecontrol, edram_mode, draw_event.rb_color_mask,
        write_mask, draw_event.rb_depthcontrol, draw_event.rb_blendcontrol[0],
        draw_event.rb_colorcontrol, stream0.buffer, stream0.offset,
        stream0.stride, stream0_size, stream0_fetch_end,
        stream0_size != 0 && stream0_fetch_end > stream0_size ? 1 : 0,
        input_slots.size(), input_elements.size(), draw_event.viewport.x,
        draw_event.viewport.y, draw_event.viewport.width,
        draw_event.viewport.height, draw_event.scissor.left,
        draw_event.scissor.top, draw_event.scissor.right,
        draw_event.scissor.bottom,
        FormatInputLayoutSummary(input_slots, input_elements),
        FormatBoundInputStreams(draw_event, input_slots, input_elements, count,
                                &index_summary),
        FormatDrawTextureSummary(draw_event),
        FormatTrackedBufferSample(stream0_buffer, stream0.offset, 128),
        FormatBoundInputStreamSamples(draw_event, input_slots, input_elements,
                                      128),
        tri_declaration != nullptr ? tri_declaration->debug_summary
                                   : "untracked",
        tri_vertex_shader != nullptr ? tri_vertex_shader->shader_name
                                     : "untracked",
        tri_pixel_shader != nullptr ? tri_pixel_shader->shader_name
                                    : "untracked");
    ++g_plume_resources.logged_triangle_list_draws;
  }
  if (RendererDrawTracingEnabled() &&
      shader_routing.video_ycrcb_draw &&
      g_plume_resources.logged_video_ycrcb_draws < 8) {
    const RendererStreamBindingSnapshot& stream0 = draw_event.streams[0];
    const PlumeTrackedResource* video_declaration = FindTrackedResourceLocked(
        draw_event.vertex_declaration,
        RendererTrackedResourceKind::kVertexDeclaration);
    const PlumeTrackedResource* stream0_buffer = FindTrackedResourceLocked(
        stream0.buffer, RendererTrackedResourceKind::kVertexBuffer);
    const PlumeTrackedResource* video_vertex_shader = FindTrackedResourceLocked(
        draw_event.vertex_shader, RendererTrackedResourceKind::kVertexShader);
    const PlumeTrackedResource* video_pixel_shader = FindTrackedResourceLocked(
        draw_event.pixel_shader, RendererTrackedResourceKind::kPixelShader);
    REXLOG_INFO(
        "Refii plume Fable video-ycrcb draw #{} indexed={} prim={} count={} "
        "start={} base={} decl=0x{:08X} vs=0x{:08X} ps=0x{:08X} "
        "rt0=0x{:08X} depth=0x{:08X} stream0=0x{:08X}+{} guest_stride={} "
        "layout={} {} sample0={} {} c8={:08X}/{:08X}/{:08X}/{:08X} "
        "tex=0x{:08X}/0x{:08X}/0x{:08X} decl={} vs_name={} ps_name={}",
        g_plume_resources.logged_video_ycrcb_draws + 1u,
        draw_event.draw.indexed ? 1 : 0, draw_event.draw.primitive_type,
        count, draw_event.draw.start, draw_event.draw.base_vertex,
        draw_event.vertex_declaration, draw_event.vertex_shader,
        draw_event.pixel_shader, draw_event.render_targets[0],
        draw_event.depth_stencil, stream0.buffer, stream0.offset,
        stream0.stride, FormatInputLayoutSummary(input_slots, input_elements),
        FormatBoundInputStreams(draw_event, input_slots, input_elements, count,
                                &index_summary),
        FormatTrackedBufferSample(stream0_buffer, stream0.offset, 160),
        FormatBoundInputStreamSamples(draw_event, input_slots, input_elements,
                                      160),
        draw_event.vertex_shader_constants[32],
        draw_event.vertex_shader_constants[33],
        draw_event.vertex_shader_constants[34],
        draw_event.vertex_shader_constants[35],
        std::size(draw_event.textures) > 0 ? draw_event.textures[0] : 0,
        std::size(draw_event.textures) > 1 ? draw_event.textures[1] : 0,
        std::size(draw_event.textures) > 2 ? draw_event.textures[2] : 0,
        video_declaration != nullptr ? video_declaration->debug_summary
                                     : "untracked",
        video_vertex_shader != nullptr ? video_vertex_shader->shader_name
                                       : "untracked",
        video_pixel_shader != nullptr ? video_pixel_shader->shader_name
                                      : "untracked");
    ++g_plume_resources.logged_video_ycrcb_draws;
  }

  const uint64_t texture_slot_shader_pair_key =
      (uint64_t(draw_event.vertex_shader) << 32u) | draw_event.pixel_shader;
  const bool verbose_fable_draw_trace =
      RendererDrawTracingEnabled() || RendererResourceTracingEnabled();
  const bool log_texture_slots =
      verbose_fable_draw_trace &&
      !shader_routing.video_ycrcb_draw &&
      (draw_event.draw.primitive_type == 4 ||
       draw_event.draw.primitive_type == 8 ||
       draw_event.draw.primitive_type == 13) &&
      g_plume_resources.logged_texture_slot_shader_pairs.size() < 48 &&
      g_plume_resources.logged_texture_slot_shader_pairs.insert(
                                                       texture_slot_shader_pair_key)
          .second;
  if (log_texture_slots) {
    const PlumeTrackedResource* slot_vertex_shader = FindTrackedResourceLocked(
        draw_event.vertex_shader, RendererTrackedResourceKind::kVertexShader);
    const PlumeTrackedResource* slot_pixel_shader = FindTrackedResourceLocked(
        draw_event.pixel_shader, RendererTrackedResourceKind::kPixelShader);
    REXLOG_INFO(
        "Refii plume Fable draw texture slots prim={} indexed={} count={} "
        "decl=0x{:08X} vs=0x{:08X} ps=0x{:08X} rt={:08X}/{:08X}/{:08X}/{:08X} "
        "depth=0x{:08X} vp={}/{}/{}/{} sc={}/{}/{}/{} "
        "rb_mode=0x{:08X} rb_mask=0x{:08X} rb_depth=0x{:08X} "
        "rb_blend0=0x{:08X} rb_colorctl=0x{:08X} vs_name={} ps_name={} {}",
        draw_event.draw.primitive_type, draw_event.draw.indexed ? 1 : 0,
        count, draw_event.vertex_declaration, draw_event.vertex_shader,
        draw_event.pixel_shader, draw_event.render_targets[0],
        draw_event.render_targets[1], draw_event.render_targets[2],
        draw_event.render_targets[3],
        draw_event.depth_stencil, draw_event.viewport.x, draw_event.viewport.y,
        draw_event.viewport.width, draw_event.viewport.height,
        draw_event.scissor.left, draw_event.scissor.top,
        draw_event.scissor.right, draw_event.scissor.bottom,
        draw_event.rb_modecontrol, draw_event.rb_color_mask,
        draw_event.rb_depthcontrol, draw_event.rb_blendcontrol[0],
        draw_event.rb_colorcontrol,
        slot_vertex_shader != nullptr ? slot_vertex_shader->shader_name
                                      : "untracked",
        slot_pixel_shader != nullptr ? slot_pixel_shader->shader_name
                                     : "untracked",
        FormatDrawTextureSlotsSummary(draw_event,
                                      {0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11,
                                       12, 13, 14, 15}));
  }
  if (verbose_fable_draw_trace &&
      g_plume_resources.logged_title_shader_draws < 8) {
    const PlumeTrackedResource* title_vertex_shader = FindTrackedResourceLocked(
        draw_event.vertex_shader, RendererTrackedResourceKind::kVertexShader);
    const PlumeTrackedResource* title_pixel_shader = FindTrackedResourceLocked(
        draw_event.pixel_shader, RendererTrackedResourceKind::kPixelShader);
    const std::string_view vertex_name =
        title_vertex_shader != nullptr ? title_vertex_shader->shader_name
                                       : std::string_view{};
    const std::string_view pixel_name =
        title_pixel_shader != nullptr ? title_pixel_shader->shader_name
                                      : std::string_view{};
    const bool title_shader_pair =
        vertex_name.find("ocean_water_misted") != std::string_view::npos ||
        pixel_name.find("texture_morph_weightmap") != std::string_view::npos;
    if (title_shader_pair) {
      constexpr uint32_t kTextureFetchConstantDwords = 6;
      constexpr uint32_t kTitleTextureSlot = 13;
      const uint32_t fetch_dword =
          kTitleTextureSlot * kTextureFetchConstantDwords;
      std::array<uint32_t, kTextureFetchConstantDwords> slot_fetch = {};
      if (fetch_dword + slot_fetch.size() <=
          draw_event.fetch_constants.size()) {
        std::copy_n(draw_event.fetch_constants.begin() + fetch_dword,
                    slot_fetch.size(), slot_fetch.begin());
      }
      REXLOG_INFO(
          "Refii plume title shader draw #{} prim={} indexed={} count={} "
          "rt={:08X}/{:08X}/{:08X}/{:08X} depth=0x{:08X} "
          "vp={}/{}/{}/{} sc={}/{}/{}/{} "
          "rb_mode=0x{:08X} rb_mask=0x{:08X} rb_depth=0x{:08X} "
          "rb_blend0=0x{:08X} rb_colorctl=0x{:08X} "
          "vs=0x{:08X} ps=0x{:08X} c8={:08X}/{:08X}/{:08X}/{:08X} "
          "ps20={:08X}/{:08X}/{:08X}/{:08X} "
          "ps31={:08X}/{:08X}/{:08X}/{:08X} "
          "slot13=0x{:08X} fetch={:08X}/{:08X}/{:08X}/{:08X}/{:08X}/{:08X} "
          "streams={} slots={} vs_name={} ps_name={}",
          g_plume_resources.logged_title_shader_draws + 1u,
          draw_event.draw.primitive_type, draw_event.draw.indexed ? 1 : 0,
          count, draw_event.render_targets[0], draw_event.render_targets[1],
          draw_event.render_targets[2], draw_event.render_targets[3],
          draw_event.depth_stencil,
          draw_event.viewport.x, draw_event.viewport.y,
          draw_event.viewport.width, draw_event.viewport.height,
          draw_event.scissor.left, draw_event.scissor.top,
          draw_event.scissor.right, draw_event.scissor.bottom,
          draw_event.rb_modecontrol, draw_event.rb_color_mask,
          draw_event.rb_depthcontrol, draw_event.rb_blendcontrol[0],
          draw_event.rb_colorcontrol,
          draw_event.vertex_shader, draw_event.pixel_shader,
          draw_event.vertex_shader_constants[32],
          draw_event.vertex_shader_constants[33],
          draw_event.vertex_shader_constants[34],
          draw_event.vertex_shader_constants[35],
          draw_event.pixel_shader_constants[80],
          draw_event.pixel_shader_constants[81],
          draw_event.pixel_shader_constants[82],
          draw_event.pixel_shader_constants[83],
          draw_event.pixel_shader_constants[124],
          draw_event.pixel_shader_constants[125],
          draw_event.pixel_shader_constants[126],
          draw_event.pixel_shader_constants[127],
          draw_event.textures[kTitleTextureSlot], slot_fetch[0],
          slot_fetch[1], slot_fetch[2], slot_fetch[3], slot_fetch[4],
          slot_fetch[5],
          FormatBoundInputStreams(draw_event, input_slots, input_elements,
                                  count),
          FormatDrawTextureSlotsSummary(draw_event,
                                        {0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10,
                                         11, 12, 13, 14, 15}),
          vertex_name, pixel_name);
      ++g_plume_resources.logged_title_shader_draws;
    }
  }

  command_list->setGraphicsPipelineLayout(g_plume_resources.pipeline_layout.get());
  FableDrawDescriptorIndices draw_descriptors;
  BindFableDrawTexturesLocked(command_list, draw_event, depth_sample_copy,
                              depth_sample_source, depth_sample_copies,
                              color_sample_copies,
                              shader_routing.decoded_pix_video_planes,
                              shader_routing.video_ycrcb_draw,
                              draw_descriptors);
  BindFableDrawSamplersLocked(device, draw_event, draw_descriptors);
  for (uint32_t set = 0; set < 3; ++set) {
    command_list->setGraphicsDescriptorSet(
        g_plume_resources.texture_descriptor_sets[set].get(), set);
  }
  command_list->setGraphicsDescriptorSet(
      g_plume_resources.sampler_descriptor_set.get(), 3);

  const uint64_t vertex_constants =
      UploadFrameConstantsLocked(device, draw_event.vertex_shader_constants);
  const std::array<uint32_t, kRendererShaderFloatConstantDwords>
      pixel_constant_data =
          BuildFablePixelShaderConstants(draw_event,
                                         shader_routing.video_ycrcb_draw);
  const uint64_t pixel_constants =
      UploadFrameConstantsLocked(device, pixel_constant_data);
  const std::array<uint32_t, kFableSharedConstantDwords> shared_constant_data =
      BuildFableSharedConstants(draw_event, draw_descriptors);
  const uint64_t shared_constants = UploadFrameConstantBytesLocked(
      device, shared_constant_data.data(),
      static_cast<uint32_t>(sizeof(uint32_t) * shared_constant_data.size()));
  if (!vertex_constants || !pixel_constants || !shared_constants) {
    return false;
  }

  PlumeFablePushConstants push_constants;
  push_constants.vertex_shader_constants = vertex_constants;
  push_constants.pixel_shader_constants = pixel_constants;
  push_constants.shared_constants = shared_constants;
  command_list->setGraphicsPushConstants(0, &push_constants);

  if (!g_plume_resources.logged_live_constants) {
    REXLOG_INFO("Refii plume resource manager is binding live Fable shader constants");
    g_plume_resources.logged_live_constants = true;
  }
  if (!g_plume_resources.logged_fable_shared_constants) {
    REXLOG_INFO(
        "Refii plume resource manager is binding Fable shared constants and "
        "fallback descriptors");
    g_plume_resources.logged_fable_shared_constants = true;
  }
  if (!g_plume_resources.logged_draw_constant_nonzero) {
    REXLOG_INFO(
        "Refii plume first replayed draw shader constants nonzero vs={} ps={}",
        CountNonZeroDwords(draw_event.vertex_shader_constants),
        CountNonZeroDwords(draw_event.pixel_shader_constants));
    g_plume_resources.logged_draw_constant_nonzero = true;
  }

  command_list->setPipeline(pipeline);
  const plume::RenderViewport viewport =
      BuildFableDrawViewport(draw_event, target);
  const plume::RenderRect scissor = BuildFableDrawScissor(draw_event, viewport);
  command_list->setViewports(viewport);
  command_list->setScissors(scissor);
  command_list->setVertexBuffers(
      first_slot, vertex_buffer_views.data(),
      static_cast<uint32_t>(vertex_buffer_views.size()),
      bound_input_slots.data());

  if (draw_event.draw.indexed && draw_event.draw.primitive_type == 13) {
    if (!draw_index_buffer || !draw_index_buffer->buffer) {
      return false;
    }

    uint32_t quad_index_count = 0;
    plume::RenderBuffer* quad_index_buffer =
        PrepareIndexedQuadListIndexBufferLocked(
            device, *draw_index_buffer, draw_event.draw.start, count,
            quad_index_count);
    if (!quad_index_buffer || quad_index_count == 0) {
      return false;
    }

    plume::RenderIndexBufferView index_buffer_view(
        quad_index_buffer->at(0), uint64_t(quad_index_count) * sizeof(uint32_t),
        plume::RenderFormat::R32_UINT);
    command_list->setIndexBuffer(&index_buffer_view);
    command_list->drawIndexedInstanced(quad_index_count, 1, 0,
                                       draw_event.draw.base_vertex, 0);
  } else if (draw_event.draw.indexed) {
    if (!draw_index_buffer || !draw_index_buffer->buffer) {
      return false;
    }
    const plume::RenderFormat index_format =
        draw_index_buffer->stride_or_format == 4 ? plume::RenderFormat::R32_UINT
                                                 : plume::RenderFormat::R16_UINT;
    plume::RenderIndexBufferView index_buffer_view(
        draw_index_buffer->buffer->at(0), draw_index_buffer->size, index_format);
    command_list->setIndexBuffer(&index_buffer_view);
    command_list->drawIndexedInstanced(count, 1, draw_event.draw.start,
                                       draw_event.draw.base_vertex, 0);
  } else if (draw_event.draw.primitive_type == 13) {
    uint32_t quad_index_count = 0;
    plume::RenderBuffer* quad_index_buffer =
        PrepareQuadListIndexBufferLocked(device, count, quad_index_count);
    if (!quad_index_buffer || quad_index_count == 0) {
      return false;
    }

    plume::RenderIndexBufferView index_buffer_view(
        quad_index_buffer->at(0), uint64_t(quad_index_count) * sizeof(uint32_t),
        plume::RenderFormat::R32_UINT);
    command_list->setIndexBuffer(&index_buffer_view);
    command_list->drawIndexedInstanced(quad_index_count, 1, 0,
                                       draw_event.draw.start, 0);
  } else {
    command_list->drawInstanced(count, 1, draw_event.draw.start, 0);
  }

  return true;
}

uint32_t SelectPresentRenderTargetLocked(
    const std::vector<RendererDrawEventSnapshot>& draw_events,
    uint64_t* out_resource_serial) {
  if (out_resource_serial) {
    *out_resource_serial = 0;
  }

  struct TargetSummary {
    uint32_t address = 0;
    uint32_t draws = 0;
    uint32_t slot0_draws = 0;
    uint32_t rectangle_draws = 0;
    uint32_t quad_draws = 0;
    uint32_t indexed_draws = 0;
    uint32_t width = 0;
    uint32_t height = 0;
    bool present_shaped = false;
    uint64_t last_serial = 0;
    uint64_t last_resource_serial = 0;
  };

  const auto select_target = [&](uint32_t address,
                                 uint64_t resource_serial) -> uint32_t {
    if (out_resource_serial) {
      *out_resource_serial = resource_serial;
    }
    return address;
  };

  const auto draw_has_seen_target =
      [](const std::array<uint32_t, kMaxFableRenderTargets>& seen_targets,
         uint32_t seen_count, uint32_t render_target) {
        for (uint32_t i = 0; i < seen_count; ++i) {
          if (seen_targets[i] == render_target) {
            return true;
          }
        }
        return false;
      };

  std::vector<TargetSummary> summaries;
  auto summary_for = [&summaries](uint32_t address) -> TargetSummary& {
    for (TargetSummary& summary : summaries) {
      if (summary.address == address) {
        return summary;
      }
    }
    summaries.push_back(TargetSummary{.address = address});
    return summaries.back();
  };

  for (const RendererDrawEventSnapshot& draw_event : draw_events) {
    if (draw_event.clear) {
      continue;
    }
    std::array<uint32_t, kMaxFableRenderTargets> seen_targets = {};
    uint32_t seen_count = 0;
    for (uint32_t slot = 0; slot < kMaxFableRenderTargets; ++slot) {
      const uint32_t render_target = draw_event.render_targets[slot];
      if (render_target == 0 ||
          draw_has_seen_target(seen_targets, seen_count, render_target)) {
        continue;
      }
      if (seen_count < seen_targets.size()) {
        seen_targets[seen_count++] = render_target;
      }
      TargetSummary& summary = summary_for(render_target);
      ++summary.draws;
      if (slot == 0) {
        ++summary.slot0_draws;
      }
      if (draw_event.draw.primitive_type == 8) {
        ++summary.rectangle_draws;
      }
      if (draw_event.draw.primitive_type == 13) {
        ++summary.quad_draws;
      }
      if (draw_event.draw.indexed) {
        ++summary.indexed_draws;
      }
      summary.last_serial = std::max(summary.last_serial, draw_event.serial);
      summary.last_resource_serial =
          std::max(summary.last_resource_serial, draw_event.resource_serial);
    }
  }

  uint32_t fallback_render_target = 0;
  uint64_t fallback_serial = 0;
  uint64_t fallback_resource_serial = 0;
  for (const TargetSummary& summary : summaries) {
    if (summary.address != 0 && summary.last_serial >= fallback_serial) {
      fallback_render_target = summary.address;
      fallback_serial = summary.last_serial;
      fallback_resource_serial = summary.last_resource_serial;
    }
  }

  TargetSummary* best = nullptr;
  uint64_t best_area = 0;
  TargetSummary* best_display_source = nullptr;
  uint64_t best_display_source_area = 0;
  TargetSummary* best_display_object = nullptr;
  uint64_t best_display_object_area = 0;
  TargetSummary* best_present_shaped_slot0 = nullptr;
  uint64_t best_present_shaped_slot0_area = 0;
  TargetSummary* best_present_shaped = nullptr;
  uint64_t best_present_shaped_area = 0;
  const RendererHookObserverSnapshot observer_snapshot =
      GetRendererHookObserverSnapshot();
  const uint32_t display_base = observer_snapshot.display_present_texture_base;
  const uint32_t normalized_display_base =
      NormalizeFableResolveDestinationBase(display_base);
  if (display_base != 0) {
    for (auto draw_it = draw_events.rbegin(); draw_it != draw_events.rend();
         ++draw_it) {
      const RendererDrawEventSnapshot& draw_event = *draw_it;
      if (!IsFableResolveCopyDraw(draw_event) ||
          draw_event.rb_copy_dest_base == 0) {
        continue;
      }

      const uint32_t copy_dest_base = draw_event.rb_copy_dest_base;
      const uint32_t normalized_copy_dest_base =
          NormalizeFableResolveDestinationBase(copy_dest_base);
      if (!FableDisplayBasesMatch(copy_dest_base, display_base) &&
          !FableDisplayBasesMatch(copy_dest_base, normalized_display_base) &&
          !FableDisplayBasesMatch(normalized_copy_dest_base, display_base) &&
          !FableDisplayBasesMatch(normalized_copy_dest_base,
                                  normalized_display_base)) {
        continue;
      }

      const uint32_t copy_src_select = draw_event.rb_copy_control & 0x7u;
      if (copy_src_select >= kMaxFableRenderTargets) {
        continue;
      }
      const uint32_t source_address =
          draw_event.render_targets[copy_src_select];
      ScopedPlumeResourceLookupSerial scoped_lookup(
          draw_event.resource_serial);
      const PlumeTrackedResource* resolved_display =
          FindTrackedColorRenderTargetLocked(source_address);
      if (!resolved_display || !resolved_display->texture ||
          !resolved_display->texture_view) {
        continue;
      }

      if (g_plume_resources.logged_present_target != source_address &&
          g_plume_resources.logged_present_target_summaries < 12) {
        REXLOG_INFO(
            "Refii plume present target selected from current PM4 resolve "
            "display=0x{:08X}/0x{:08X} rt=0x{:08X} slot={} size={}x{} "
            "dest=0x{:08X} candidates={}",
            display_base, normalized_display_base, source_address,
            copy_src_select, resolved_display->texture_width,
            resolved_display->texture_height, copy_dest_base,
            summaries.size());
        ++g_plume_resources.logged_present_target_summaries;
        g_plume_resources.logged_present_target = source_address;
        g_plume_resources.logged_present_target_width =
            resolved_display->texture_width;
        g_plume_resources.logged_present_target_height =
            resolved_display->texture_height;
      }
      return select_target(source_address, draw_event.resource_serial);
    }
  }
  const uint64_t output_width = static_cast<uint64_t>(
      std::max(1, RendererOutputWidth()) * std::max(1, RendererInternalScale()));
  const uint64_t output_height = static_cast<uint64_t>(
      std::max(1, RendererOutputHeight()) * std::max(1, RendererInternalScale()));
  constexpr uint64_t kMinimumStablePresentArea = 640ull * 360ull;
  for (TargetSummary& summary : summaries) {
    ScopedPlumeResourceLookupSerial scoped_lookup(
        summary.last_resource_serial);
    const PlumeTrackedResource* target =
        FindTrackedColorRenderTargetLocked(summary.address);
    if (!target) {
      continue;
    }
    summary.width = target->texture_width;
    summary.height = target->texture_height;
    const uint64_t area =
        uint64_t(std::max(1u, summary.width)) * std::max(1u, summary.height);
    const bool display_source_matches =
        observer_snapshot.display_present_texture_base != 0 &&
        FableDisplayBasesMatch(
            target->texture_guest_base,
            observer_snapshot.display_present_texture_base);
    const bool display_object_matches =
        observer_snapshot.display_present_surface_object != 0 &&
        (summary.address == observer_snapshot.display_present_surface_object ||
         target->guest_address ==
             observer_snapshot.display_present_surface_object ||
         target->parent_address ==
             observer_snapshot.display_present_surface_object ||
         target->buffer_object ==
             observer_snapshot.display_present_surface_object);
    const bool display_size_matches =
        observer_snapshot.display_present_width == 0 ||
        observer_snapshot.display_present_height == 0 ||
        (summary.width == observer_snapshot.display_present_width &&
         summary.height == observer_snapshot.display_present_height);
    if (display_source_matches && display_size_matches &&
        (!best_display_source || area > best_display_source_area ||
         (area == best_display_source_area &&
          summary.last_serial > best_display_source->last_serial))) {
      best_display_source = &summary;
      best_display_source_area = area;
    }
    if (display_object_matches && display_size_matches &&
        (!best_display_object || area > best_display_object_area ||
         (area == best_display_object_area &&
          summary.last_serial > best_display_object->last_serial))) {
      best_display_object = &summary;
      best_display_object_area = area;
    }
    const uint64_t aspect_lhs =
        uint64_t(std::max(1u, summary.width)) * output_height;
    const uint64_t aspect_rhs =
        uint64_t(std::max(1u, summary.height)) * output_width;
    const uint64_t aspect_error =
        aspect_lhs > aspect_rhs ? aspect_lhs - aspect_rhs
                                : aspect_rhs - aspect_lhs;
    const uint64_t aspect_den = std::max<uint64_t>(1, aspect_rhs);
    summary.present_shaped =
        area >= kMinimumStablePresentArea &&
        aspect_error * 100ull <= aspect_den * 8ull &&
        uint64_t(summary.width) <= (output_width * 4ull) / 3ull &&
        uint64_t(summary.height) <= (output_height * 4ull) / 3ull;
    if (summary.present_shaped && summary.slot0_draws != 0 &&
        (!best_present_shaped_slot0 ||
         area > best_present_shaped_slot0_area ||
         (area == best_present_shaped_slot0_area &&
          summary.last_serial > best_present_shaped_slot0->last_serial))) {
      best_present_shaped_slot0 = &summary;
      best_present_shaped_slot0_area = area;
    }
    if (summary.present_shaped &&
        (!best_present_shaped || area > best_present_shaped_area ||
         (area == best_present_shaped_area &&
          summary.last_serial > best_present_shaped->last_serial))) {
      best_present_shaped = &summary;
      best_present_shaped_area = area;
    }
    if (!best || area > best_area ||
        (area == best_area && summary.last_serial > best->last_serial)) {
      best = &summary;
      best_area = area;
    }
  }

  if (best_display_object) {
    best = best_display_object;
    best_area = best_display_object_area;
  } else if (best_display_source) {
    best = best_display_source;
    best_area = best_display_source_area;
    const bool display_source_is_authoritative =
        observer_snapshot.display_present_texture_base != 0 ||
        observer_snapshot.display_present_surface_object != 0;
    if (!display_source_is_authoritative && best_present_shaped_slot0 &&
        best_present_shaped_slot0->address != best_display_source->address &&
        best_present_shaped_slot0->last_serial > best_display_source->last_serial &&
        best_present_shaped_slot0_area >= kMinimumStablePresentArea) {
      if (g_plume_resources.logged_present_target_summaries < 12) {
        REXLOG_INFO(
            "Refii plume present target handed off from display source "
            "rt=0x{:08X} serial={} size={}x{} to newer frame target "
            "rt=0x{:08X} serial={} size={}x{} candidates={}",
            best_display_source->address,
            static_cast<unsigned long long>(best_display_source->last_serial),
            best_display_source->width, best_display_source->height,
            best_present_shaped_slot0->address,
            static_cast<unsigned long long>(
                best_present_shaped_slot0->last_serial),
            best_present_shaped_slot0->width,
            best_present_shaped_slot0->height, summaries.size());
        ++g_plume_resources.logged_present_target_summaries;
      }
      best = best_present_shaped_slot0;
      best_area = best_present_shaped_slot0_area;
    }
  } else if (best_present_shaped_slot0) {
    best = best_present_shaped_slot0;
    best_area = best_present_shaped_slot0_area;
  } else if (best_present_shaped) {
    best = best_present_shaped;
    best_area = best_present_shaped_area;
  }

  auto find_summary_by_address = [&summaries](uint32_t address) -> TargetSummary* {
    for (TargetSummary& summary : summaries) {
      if (summary.address == address && summary.width != 0 &&
          summary.height != 0) {
        return &summary;
      }
    }
    return nullptr;
  };

  if (const RendererSurfaceCopyAliasSnapshot* copy_alias =
          FindDisplaySurfaceCopyAliasLocked(observer_snapshot)) {
    TargetSummary* alias_summary =
        find_summary_by_address(copy_alias->source_surface);
    const bool alias_part_of_current_frame = alias_summary != nullptr;
    const bool alias_is_only_usable_target = best == nullptr;
    ScopedPlumeResourceLookupSerial scoped_alias_lookup(copy_alias->serial);
    const PlumeTrackedResource* copied_source =
        FindTrackedColorRenderTargetLocked(copy_alias->source_surface);
    if (copied_source && copied_source->texture &&
        copied_source->texture_view &&
        (alias_part_of_current_frame || alias_is_only_usable_target)) {
      const uint64_t alias_resource_serial =
          alias_summary ? alias_summary->last_resource_serial
                        : copied_source->serial;
      if (alias_summary) {
        best = alias_summary;
        best_area = uint64_t(std::max(1u, alias_summary->width)) *
                    std::max(1u, alias_summary->height);
      }
      if (g_plume_resources.logged_present_target !=
              copy_alias->source_surface &&
          g_plume_resources.logged_present_target_summaries < 12) {
        REXLOG_INFO(
            "Refii plume present target selected from D3DX surface copy "
            "dst=0x{:08X} src=0x{:08X} hook=0x{:08X} size={}x{} "
            "current={} candidates={}",
            copy_alias->destination_surface, copy_alias->source_surface,
            copy_alias->hook_address, copied_source->texture_width,
            copied_source->texture_height,
            alias_part_of_current_frame ? 1 : 0, summaries.size());
        ++g_plume_resources.logged_present_target_summaries;
        g_plume_resources.logged_present_target = copy_alias->source_surface;
        g_plume_resources.logged_present_target_width =
            copied_source->texture_width;
        g_plume_resources.logged_present_target_height =
            copied_source->texture_height;
      }
      g_plume_resources.preferred_present_render_target =
          copy_alias->source_surface;
      g_plume_resources.preferred_present_width = copied_source->texture_width;
      g_plume_resources.preferred_present_height =
          copied_source->texture_height;
      return select_target(copy_alias->source_surface, alias_resource_serial);
    }
  }

  if (const FableRenderTargetResolveAlias* display_alias =
          FindFableResolveAliasLocked(
              observer_snapshot.display_present_texture_base)) {
    TargetSummary* alias_summary =
        find_summary_by_address(display_alias->source_address);
    const bool alias_part_of_current_frame = alias_summary != nullptr;
    const bool alias_is_only_usable_target = best == nullptr;
    ScopedPlumeResourceLookupSerial scoped_alias_lookup(display_alias->serial);
    const PlumeTrackedResource* resolved_display =
        display_alias->depth
            ? FindTrackedDepthTargetLocked(display_alias->source_address)
            : FindTrackedColorRenderTargetLocked(display_alias->source_address);
    if (!display_alias->depth && resolved_display &&
        resolved_display->texture && resolved_display->texture_view &&
        (alias_part_of_current_frame || alias_is_only_usable_target)) {
      if (alias_summary) {
        best = alias_summary;
        best_area = uint64_t(std::max(1u, alias_summary->width)) *
                    std::max(1u, alias_summary->height);
      }
      if (g_plume_resources.logged_present_target !=
              display_alias->source_address &&
          g_plume_resources.logged_present_target_summaries < 12) {
        REXLOG_INFO(
            "Refii plume present target selected from live resolve alias "
            "display=0x{:08X} rt=0x{:08X} size={}x{} current={} candidates={}",
            observer_snapshot.display_present_texture_base,
            display_alias->source_address, resolved_display->texture_width,
            resolved_display->texture_height,
            alias_part_of_current_frame ? 1 : 0, summaries.size());
        ++g_plume_resources.logged_present_target_summaries;
        g_plume_resources.logged_present_target = display_alias->source_address;
        g_plume_resources.logged_present_target_width =
            resolved_display->texture_width;
        g_plume_resources.logged_present_target_height =
            resolved_display->texture_height;
      }
      if (!alias_summary) {
        return select_target(display_alias->source_address,
                             display_alias->serial);
      }
    }
  }

  if (best) {
    const uint64_t preferred_area =
        uint64_t(g_plume_resources.preferred_present_width) *
        g_plume_resources.preferred_present_height;
    TargetSummary* preferred_summary =
        find_summary_by_address(g_plume_resources.preferred_present_render_target);
    const uint64_t preferred_resource_serial =
        preferred_summary ? preferred_summary->last_resource_serial : 0;
    ScopedPlumeResourceLookupSerial scoped_preferred_lookup(
        preferred_resource_serial);
    const PlumeTrackedResource* preferred_target =
        g_plume_resources.preferred_present_render_target != 0
            ? FindTrackedColorRenderTargetLocked(
                  g_plume_resources.preferred_present_render_target)
            : nullptr;
    const bool preferred_part_of_current_frame =
        preferred_target && preferred_summary != nullptr;
    if (!preferred_target) {
      g_plume_resources.preferred_present_render_target = 0;
      g_plume_resources.preferred_present_width = 0;
      g_plume_resources.preferred_present_height = 0;
    }

    if (best_area >= kMinimumStablePresentArea) {
      g_plume_resources.preferred_present_render_target = best->address;
      g_plume_resources.preferred_present_width = best->width;
      g_plume_resources.preferred_present_height = best->height;
    } else if (preferred_target && preferred_part_of_current_frame &&
               preferred_area > best_area) {
      if (RendererResourceTracingEnabled() &&
          g_plume_resources.logged_present_target_summaries < 12) {
        REXLOG_INFO(
            "Refii plume present target kept stable rt=0x{:08X} size={}x{} "
            "over unstable rt=0x{:08X} size={}x{} candidates={}",
            g_plume_resources.preferred_present_render_target,
            g_plume_resources.preferred_present_width,
            g_plume_resources.preferred_present_height, best->address,
            best->width, best->height, summaries.size());
        ++g_plume_resources.logged_present_target_summaries;
      }
      return select_target(g_plume_resources.preferred_present_render_target,
                           preferred_resource_serial);
    }

    const bool present_target_changed =
        g_plume_resources.logged_present_target != best->address ||
        g_plume_resources.logged_present_target_width != best->width ||
        g_plume_resources.logged_present_target_height != best->height;
    if (present_target_changed &&
        g_plume_resources.logged_present_target_summaries < 12) {
      REXLOG_INFO(
          "Refii plume present target selected rt=0x{:08X} draws={} slot0={} rect={} "
          "quad={} indexed={} size={}x{} shaped={} candidates={}",
          best->address, best->draws, best->slot0_draws,
          best->rectangle_draws, best->quad_draws, best->indexed_draws,
          best->width, best->height,
          best->present_shaped ? 1 : 0, summaries.size());
      ++g_plume_resources.logged_present_target_summaries;
      g_plume_resources.logged_present_target = best->address;
      g_plume_resources.logged_present_target_width = best->width;
      g_plume_resources.logged_present_target_height = best->height;
    }
    return select_target(best->address, best->last_resource_serial);
  }

  if (fallback_render_target != 0 &&
      g_plume_resources.logged_present_target != fallback_render_target &&
      g_plume_resources.logged_present_target_summaries < 12) {
    TargetSummary& summary = summary_for(fallback_render_target);
    REXLOG_INFO(
        "Refii plume present target fallback rt=0x{:08X} draws={} slot0={} rect={} "
        "quad={} indexed={} candidates={}",
        fallback_render_target, summary.draws, summary.slot0_draws,
        summary.rectangle_draws, summary.quad_draws, summary.indexed_draws,
        summaries.size());
    ++g_plume_resources.logged_present_target_summaries;
    g_plume_resources.logged_present_target = fallback_render_target;
    g_plume_resources.logged_present_target_width = 0;
    g_plume_resources.logged_present_target_height = 0;
  }
  return select_target(fallback_render_target, fallback_resource_serial);
}

uint32_t CountBoundRenderTargets(const RendererDrawEventSnapshot& draw_event) {
  std::array<uint32_t, kMaxFableRenderTargets> seen_targets = {};
  uint32_t seen_count = 0;
  for (uint32_t render_target : draw_event.render_targets) {
    if (render_target == 0) {
      continue;
    }
    bool seen = false;
    for (uint32_t i = 0; i < seen_count; ++i) {
      if (seen_targets[i] == render_target) {
        seen = true;
        break;
      }
    }
    if (seen) {
      continue;
    }
    if (seen_count < seen_targets.size()) {
      seen_targets[seen_count] = render_target;
    }
    ++seen_count;
  }
  return seen_count;
}

bool DrawHasSparseRenderTargetSlots(
    const RendererDrawEventSnapshot& draw_event) {
  bool saw_empty_slot = false;
  for (uint32_t render_target : draw_event.render_targets) {
    if (render_target == 0) {
      saw_empty_slot = true;
      continue;
    }
    if (saw_empty_slot) {
      return true;
    }
  }
  return false;
}

bool DrawHasAnyRenderTarget(const RendererDrawEventSnapshot& draw_event) {
  return CountBoundRenderTargets(draw_event) != 0;
}

bool DrawTargetsContainRenderTarget(
    const RendererDrawEventSnapshot& draw_event, uint32_t render_target) {
  if (render_target == 0) {
    return false;
  }
  for (uint32_t bound_render_target : draw_event.render_targets) {
    if (bound_render_target == render_target) {
      return true;
    }
  }
  return false;
}

bool ShouldReplayDrawToCurrentOutput(
    const RendererDrawEventSnapshot& draw_event,
    uint32_t present_render_target) {
  if (present_render_target == 0) {
    return true;
  }
  if (!DrawHasAnyRenderTarget(draw_event)) {
    return true;
  }
  return draw_event.render_targets[0] == present_render_target;
}

bool DrawTargetsSelectedPresentTarget(
    const RendererDrawEventSnapshot& draw_event,
    uint32_t present_render_target) {
  return DrawTargetsContainRenderTarget(draw_event, present_render_target);
}

uint32_t ResolvePresentRenderTargetFromFrameAliasesLocked(
    uint32_t selected_present_render_target,
    uint64_t selected_present_resource_serial,
    uint64_t* out_present_resource_serial) {
  if (out_present_resource_serial) {
    *out_present_resource_serial = selected_present_resource_serial;
  }
  const RendererHookObserverSnapshot observer_snapshot =
      GetRendererHookObserverSnapshot();
  const uint32_t display_base = observer_snapshot.display_present_texture_base;
  if (const RendererSurfaceCopyAliasSnapshot* copy_alias =
          FindDisplaySurfaceCopyAliasLocked(observer_snapshot)) {
    ScopedPlumeResourceLookupSerial scoped_copy_lookup(copy_alias->serial);
    const PlumeTrackedResource* copied_source =
        FindTrackedColorRenderTargetLocked(copy_alias->source_surface);
    if (copied_source && copied_source->texture &&
        copied_source->texture_view) {
      if (selected_present_render_target != copy_alias->source_surface &&
          g_plume_resources.logged_present_target_summaries < 12) {
        REXLOG_INFO(
            "Refii plume present target corrected from surface copy "
            "dst=0x{:08X} src=0x{:08X} previous=0x{:08X} hook=0x{:08X} "
            "size={}x{}",
            copy_alias->destination_surface, copy_alias->source_surface,
            selected_present_render_target, copy_alias->hook_address,
            copied_source->texture_width, copied_source->texture_height);
        ++g_plume_resources.logged_present_target_summaries;
      }
      g_plume_resources.preferred_present_render_target =
          copy_alias->source_surface;
      g_plume_resources.preferred_present_width =
          copied_source->texture_width;
      g_plume_resources.preferred_present_height =
          copied_source->texture_height;
      g_plume_resources.logged_present_target = copy_alias->source_surface;
      g_plume_resources.logged_present_target_width =
          copied_source->texture_width;
      g_plume_resources.logged_present_target_height =
          copied_source->texture_height;
      if (out_present_resource_serial) {
        *out_present_resource_serial = copied_source->serial;
      }
      return copy_alias->source_surface;
    }
  }
  if (display_base == 0) {
    return selected_present_render_target;
  }

  const FableRenderTargetResolveAlias* display_alias =
      FindFableResolveAliasLocked(display_base);
  if (!display_alias || display_alias->depth ||
      display_alias->source_address == 0) {
    return selected_present_render_target;
  }

  const PlumeTrackedResource* resolved_display = nullptr;
  {
    ScopedPlumeResourceLookupSerial scoped_alias_lookup(display_alias->serial);
    resolved_display =
        FindTrackedColorRenderTargetLocked(display_alias->source_address);
    if (!resolved_display || !resolved_display->texture ||
        !resolved_display->texture_view) {
      return selected_present_render_target;
    }
  }

  if (selected_present_render_target != display_alias->source_address &&
      g_plume_resources.logged_present_target_summaries < 12) {
    REXLOG_INFO(
        "Refii plume present target corrected from frame resolve "
        "display=0x{:08X} rt=0x{:08X} previous=0x{:08X} slot={} "
        "size={}x{}",
        display_base, display_alias->source_address,
        selected_present_render_target, display_alias->source_slot,
        resolved_display->texture_width, resolved_display->texture_height);
    ++g_plume_resources.logged_present_target_summaries;
  }

  g_plume_resources.preferred_present_render_target =
      display_alias->source_address;
  g_plume_resources.preferred_present_width = resolved_display->texture_width;
  g_plume_resources.preferred_present_height = resolved_display->texture_height;
  g_plume_resources.logged_present_target = display_alias->source_address;
  g_plume_resources.logged_present_target_width =
      resolved_display->texture_width;
  g_plume_resources.logged_present_target_height =
      resolved_display->texture_height;
  if (out_present_resource_serial) {
    *out_present_resource_serial = display_alias->serial;
  }
  return display_alias->source_address;
}

PlumePresentBlitPushConstants BuildPresentBlitPushConstants(
    const PlumeTrackedResource& present) {
  PlumePresentBlitPushConstants push_constants;
  const RendererHookObserverSnapshot observer_snapshot =
      GetRendererHookObserverSnapshot();
  const RendererScalerSnapshot& scaler = observer_snapshot.scaler;
  if (!scaler.valid || present.texture_width == 0 ||
      present.texture_height == 0) {
    return push_constants;
  }

  const uint32_t source_width = scaler.source_right - scaler.source_left;
  const uint32_t source_height = scaler.source_bottom - scaler.source_top;
  if (source_width == 0 || source_height == 0 ||
      scaler.source_right > present.texture_width ||
      scaler.source_bottom > present.texture_height) {
    return push_constants;
  }

  if (scaler.source_left == 0 && scaler.source_top == 0 &&
      source_width == present.texture_width &&
      source_height == present.texture_height) {
    return push_constants;
  }

  push_constants.uv_bias[0] =
      static_cast<float>(scaler.source_left) /
      static_cast<float>(present.texture_width);
  push_constants.uv_bias[1] =
      static_cast<float>(scaler.source_top) /
      static_cast<float>(present.texture_height);
  push_constants.uv_scale[0] =
      static_cast<float>(source_width) /
      static_cast<float>(present.texture_width);
  push_constants.uv_scale[1] =
      static_cast<float>(source_height) /
      static_cast<float>(present.texture_height);
  return push_constants;
}

bool CopyPresentRenderTargetToOutputLocked(
    plume::RenderDevice* device, plume::RenderCommandList* command_list,
    plume::RenderShaderFormat shader_format, uint32_t present_render_target,
    uint64_t present_resource_serial, plume::RenderTexture* output_texture,
    plume::RenderFramebuffer* output_framebuffer) {
  if (!command_list || !output_texture || present_render_target == 0) {
    if (command_list && output_framebuffer) {
      command_list->setFramebuffer(output_framebuffer);
    }
    return false;
  }

  ScopedPlumeResourceLookupSerial scoped_lookup(present_resource_serial);
  const PlumeTrackedResource* present =
      FindTrackedColorRenderTargetLocked(present_render_target);
  if (!present) {
    if (output_framebuffer) {
      command_list->setFramebuffer(output_framebuffer);
    }
    return false;
  }

  const uint32_t output_width = static_cast<uint32_t>(
      std::max(1, RendererOutputWidth()) * std::max(1, RendererInternalScale()));
  const uint32_t output_height = static_cast<uint32_t>(
      std::max(1, RendererOutputHeight()) * std::max(1, RendererInternalScale()));

  const PlumePresentBlitPushConstants present_blit_push_constants =
      BuildPresentBlitPushConstants(*present);
  const bool needs_present_scaler =
      present_blit_push_constants.uv_scale[0] != 1.0f ||
      present_blit_push_constants.uv_scale[1] != 1.0f ||
      present_blit_push_constants.uv_bias[0] != 0.0f ||
      present_blit_push_constants.uv_bias[1] != 0.0f;
  const bool needs_present_blit =
      present->texture_format != plume::RenderFormat::B8G8R8A8_UNORM ||
      present->texture_width != output_width ||
      present->texture_height != output_height || needs_present_scaler;
  if (RendererResourceTracingEnabled() &&
      g_plume_resources.logged_present_copy_summaries < 32 &&
      (g_plume_resources.logged_present_copy_target != present_render_target ||
       g_plume_resources.logged_present_copy_serial != present_resource_serial)) {
    const RendererHookObserverSnapshot observer_snapshot =
        GetRendererHookObserverSnapshot();
    const RendererScalerSnapshot& scaler = observer_snapshot.scaler;
    REXLOG_INFO(
        "Refii plume present copy rt=0x{:08X} req_serial={} res_serial={} "
        "kind={} guest=0x{:08X} parent=0x{:08X} base=0x{:08X} "
        "size={}x{} fmt={} output={}x{} blit={} scaler={} "
        "src={},{},{},{} out={}x{} display=0x{:08X}/0x{:08X}",
        present_render_target,
        static_cast<unsigned long long>(present_resource_serial),
        static_cast<unsigned long long>(present->serial),
        TrackedResourceKindName(present->kind), present->guest_address,
        present->parent_address, present->texture_guest_base,
        present->texture_width, present->texture_height,
        static_cast<uint32_t>(present->texture_format), output_width,
        output_height, needs_present_blit ? 1 : 0,
        needs_present_scaler ? 1 : 0,
        scaler.valid ? scaler.source_left : 0,
        scaler.valid ? scaler.source_top : 0,
        scaler.valid ? scaler.source_right : 0,
        scaler.valid ? scaler.source_bottom : 0,
        scaler.valid ? scaler.output_width : 0,
        scaler.valid ? scaler.output_height : 0,
        observer_snapshot.display_present_texture_base,
        observer_snapshot.display_present_surface_object);
    ++g_plume_resources.logged_present_copy_summaries;
    g_plume_resources.logged_present_copy_target = present_render_target;
    g_plume_resources.logged_present_copy_serial = present_resource_serial;
  }
  if (needs_present_blit) {
    if (!output_framebuffer ||
        !EnsurePresentBlitPipelineLocked(device, shader_format)) {
      if (g_plume_resources.logged_present_copy_failures
              .insert(present_render_target)
              .second) {
        REXLOG_INFO(
            "Refii plume present blit unavailable rt=0x{:08X}: format {} "
            "{}x{} -> output {}x{}",
            present_render_target,
            static_cast<uint32_t>(present->texture_format),
            present->texture_width, present->texture_height, output_width,
            output_height);
      }
      if (output_framebuffer) {
        command_list->setFramebuffer(output_framebuffer);
      }
      return false;
    }

    if (g_plume_resources.logged_present_copy_failures
            .insert(present_render_target)
            .second) {
      REXLOG_INFO(
          "Refii plume present blit active rt=0x{:08X}: format {} -> output "
          "format {} ({}x{} -> {}x{})",
          present_render_target, static_cast<uint32_t>(present->texture_format),
          static_cast<uint32_t>(plume::RenderFormat::B8G8R8A8_UNORM),
          present->texture_width, present->texture_height, output_width,
          output_height);
    }

    const plume::RenderTextureBarrier blit_barriers[2] = {
        plume::RenderTextureBarrier(present->texture.get(),
                                    plume::RenderTextureLayout::SHADER_READ),
        plume::RenderTextureBarrier(output_texture,
                                    plume::RenderTextureLayout::COLOR_WRITE),
    };
    command_list->barriers(plume::RenderBarrierStage::GRAPHICS,
                           blit_barriers, 2);
    command_list->setFramebuffer(output_framebuffer);
    command_list->setGraphicsPipelineLayout(
        g_plume_resources.present_blit_pipeline_layout.get());
    command_list->setPipeline(g_plume_resources.present_blit_pipeline.get());
    command_list->setGraphicsPushConstants(0, &present_blit_push_constants);
    plume::RenderDescriptorSet* texture_set =
        g_plume_resources
            .present_blit_texture_sets[g_plume_resources
                                           .present_blit_texture_set_index %
                                       g_plume_resources
                                           .present_blit_texture_sets.size()]
            .get();
    g_plume_resources.present_blit_texture_set_index =
        (g_plume_resources.present_blit_texture_set_index + 1u) %
        kPresentBlitDescriptorSetRingSize;
    texture_set->setTexture(
        0, present->texture.get(), plume::RenderTextureLayout::SHADER_READ,
        present->texture_view.get());
    command_list->setGraphicsDescriptorSet(texture_set, 0);
    command_list->setGraphicsDescriptorSet(
        g_plume_resources.present_blit_sampler_set.get(), 1);
    command_list->setViewports(plume::RenderViewport(
        0.0f, 0.0f, float(output_width), float(output_height)));
    command_list->setScissors(
        plume::RenderRect(0, 0, int32_t(output_width), int32_t(output_height)));
    command_list->drawInstanced(3, 1, 0, 0);
    return true;
  }

  const plume::RenderTextureBarrier copy_barriers[2] = {
      plume::RenderTextureBarrier(present->texture.get(),
                                  plume::RenderTextureLayout::COPY_SOURCE),
      plume::RenderTextureBarrier(output_texture,
                                  plume::RenderTextureLayout::COPY_DEST),
  };
  command_list->barriers(plume::RenderBarrierStage::COPY, copy_barriers, 2);
  command_list->copyTexture(output_texture, present->texture.get());

  const plume::RenderTextureBarrier draw_barriers[2] = {
      plume::RenderTextureBarrier(present->texture.get(),
                                  plume::RenderTextureLayout::SHADER_READ),
      plume::RenderTextureBarrier(output_texture,
                                  plume::RenderTextureLayout::COLOR_WRITE),
  };
  command_list->barriers(plume::RenderBarrierStage::GRAPHICS, draw_barriers, 2);
  if (output_framebuffer) {
    command_list->setFramebuffer(output_framebuffer);
  }
  return true;
}

bool RecordFableClearLocked(plume::RenderDevice* device,
                            plume::RenderCommandList* command_list,
                            const RendererDrawEventSnapshot& clear_event,
                            plume::RenderFramebuffer* output_framebuffer,
                            plume::RenderFormat output_depth_format,
                            uint32_t present_render_target,
                            std::string& current_framebuffer_key,
                            PlumeDrawReplayStats& stats) {
  if (!device || !command_list || !clear_event.clear) {
    return false;
  }

  FableDrawTargetDesc target =
      BuildFableDrawTargetDescLocked(device, clear_event);
  if (!ShouldReplayDrawToCurrentOutput(clear_event, present_render_target)) {
    target.uses_output = false;
  }
  const uint32_t color_flags =
      clear_event.clear_flags & kFableClearColorTargetMask;
  const bool clear_depth_requested =
      (clear_event.clear_flags & kFableClearDepth) != 0;
  const bool clear_stencil_requested =
      (clear_event.clear_flags & kFableClearStencil) != 0;
  if (target.uses_output && color_flags == 0 &&
      (clear_depth_requested || clear_stencil_requested) &&
      target.has_depth && clear_event.depth_stencil != 0) {
    target.uses_output = false;
  }
  if (target.uses_output && output_depth_format != plume::RenderFormat::UNKNOWN) {
    target.depth_address = clear_event.depth_stencil;
    target.depth_format = output_depth_format;
    target.has_depth = true;
  } else if (target.uses_output) {
    target.depth_address = 0;
    target.depth_format = plume::RenderFormat::UNKNOWN;
    target.depth_texture = nullptr;
    target.depth_view = nullptr;
    target.has_depth = false;
  }

  plume::RenderFramebuffer* clear_framebuffer = output_framebuffer;
  std::string clear_framebuffer_key = "output";
  if (!target.uses_output) {
    clear_framebuffer = GetOrCreateFableFramebufferLocked(device, target);
    if (clear_framebuffer) {
      clear_framebuffer_key = MakeFramebufferKey(target);
    }
  }
  if (!clear_framebuffer) {
    ++stats.missing_render_targets;
    return false;
  }

  if (clear_framebuffer_key != current_framebuffer_key) {
    if (!target.uses_output) {
      for (uint32_t i = 0; i < target.color_count; ++i) {
        if (target.color_textures[i]) {
          command_list->barriers(
              plume::RenderBarrierStage::GRAPHICS,
              plume::RenderTextureBarrier(
                  target.color_textures[i],
                  plume::RenderTextureLayout::COLOR_WRITE));
        }
      }
    }
    if (!target.uses_output && target.has_depth && target.depth_texture) {
      command_list->barriers(
          plume::RenderBarrierStage::GRAPHICS,
          plume::RenderTextureBarrier(target.depth_texture,
                                      plume::RenderTextureLayout::DEPTH_WRITE));
    }
    command_list->setFramebuffer(clear_framebuffer);
    current_framebuffer_key = clear_framebuffer_key;
    ++stats.framebuffer_switches;
  }

  const plume::RenderRect* clear_rect = nullptr;
  plume::RenderRect rect;
  if (clear_event.clear_rect.valid) {
    const int32_t target_width =
        static_cast<int32_t>(std::max<uint32_t>(1, FableDrawTargetWidth(target)));
    const int32_t target_height = static_cast<int32_t>(
        std::max<uint32_t>(1, FableDrawTargetHeight(target)));
    const int32_t left =
        std::clamp(clear_event.clear_rect.left, 0, target_width);
    const int32_t top =
        std::clamp(clear_event.clear_rect.top, 0, target_height);
    const int32_t right =
        std::clamp(clear_event.clear_rect.right, left, target_width);
    const int32_t bottom =
        std::clamp(clear_event.clear_rect.bottom, top, target_height);
    if (right > left && bottom > top) {
      rect = plume::RenderRect(left, top, right, bottom);
      clear_rect = &rect;
    }
  }

  const bool clear_color = color_flags != 0 && target.has_color;
  const bool clear_depth = clear_depth_requested && target.has_depth;
  const bool clear_stencil =
      clear_stencil_requested && target.has_depth &&
      plume::RenderFormatIsStencil(target.depth_format);
  if (clear_stencil_requested) {
    ++stats.stencil_clear_requests;
  }
  if (clear_stencil) {
    ++stats.stencil_clears;
  }
  if ((RendererDrawTracingEnabled() || RendererResourceTracingEnabled()) &&
      g_plume_resources.logged_fable_clears < 16 &&
      (clear_color || clear_depth || clear_stencil)) {
    REXLOG_INFO(
        "Refii plume replay clear flags=0x{:08X} color={} depth={} stencil={} "
        "rt=0x{:08X} ds=0x{:08X} output={} rect={} color=({:.3f}, {:.3f}, "
        "{:.3f}, {:.3f}) z={:.3f} stencil={}",
        clear_event.clear_flags, clear_color ? 1 : 0,
        clear_depth ? 1 : 0, clear_stencil ? 1 : 0, target.color_address,
        target.depth_address, target.uses_output ? 1 : 0,
        clear_event.clear_rect.valid ? 1 : 0, clear_event.clear_color[0],
        clear_event.clear_color[1], clear_event.clear_color[2],
        clear_event.clear_color[3], clear_event.clear_depth,
        clear_event.clear_stencil);
    ++g_plume_resources.logged_fable_clears;
  }
  if (clear_color) {
    for (uint32_t i = 0; i < target.color_count; ++i) {
      if ((color_flags & (1u << i)) == 0) {
        continue;
      }
      command_list->clearColor(
          i, plume::RenderColor(clear_event.clear_color[0],
                                clear_event.clear_color[1],
                                clear_event.clear_color[2],
                                clear_event.clear_color[3]),
          clear_rect, clear_rect ? 1u : 0u);
    }
  }
  if (clear_depth || clear_stencil) {
    command_list->clearDepthStencil(clear_depth, clear_stencil,
                                    clear_event.clear_depth,
                                    clear_event.clear_stencil, clear_rect,
                                    clear_rect ? 1u : 0u);
  }
  return clear_color || clear_depth || clear_stencil;
}

void AddReferencedFableResourcesToLiveSetLocked(
    std::unordered_set<uint32_t>& live_addresses,
    const std::vector<RendererDrawEventSnapshot>* draw_events) {
  if (!draw_events) {
    return;
  }

  const auto add_address = [&live_addresses](uint32_t address) {
    if (address != 0) {
      live_addresses.insert(address);
    }
  };

  for (const RendererDrawEventSnapshot& draw_event : *draw_events) {
    add_address(draw_event.vertex_declaration);
    add_address(draw_event.vertex_shader);
    add_address(draw_event.pixel_shader);
    add_address(draw_event.index_buffer);
    add_address(draw_event.depth_stencil);

    for (uint32_t render_target : draw_event.render_targets) {
      add_address(render_target);
    }
    for (uint32_t texture : draw_event.textures) {
      add_address(texture);
    }
    for (const RendererStreamBindingSnapshot& stream : draw_event.streams) {
      add_address(stream.buffer);
    }
  }
}
#endif

}  // namespace

#if REFII_RENDERER_HAS_PLUME
void SyncPlumeTrackedResources(plume::RenderDevice* device,
                               plume::RenderShaderFormat shader_format,
                               const std::vector<RendererDrawEventSnapshot>*
                                   retained_draw_events) {
  ZoneScopedN("RefiiPlumeResources/SyncTrackedResources");
  if (!device) {
    return;
  }

  const RendererTrackedResourceVersion resource_version =
      GetRendererTrackedResourceVersion();
  std::unordered_map<uint32_t, RendererTrackedResourceUploadCacheEntry>
      known_resources;
  {
    std::lock_guard<std::mutex> lock(g_plume_resources.mutex);
    if (g_plume_resources.resource_sync_version_valid &&
        SameTrackedResourceVersion(g_plume_resources.last_resource_sync_version,
                                   resource_version)) {
      return;
    }
    known_resources.reserve(g_plume_resources.resources.size());
    for (const auto& [guest_address, resource] :
         g_plume_resources.resources) {
      known_resources.emplace(
          guest_address,
          RendererTrackedResourceUploadCacheEntry{
              resource.kind, resource.upload_generation, resource.serial});
    }
  }

  const std::vector<RendererTrackedResourceSnapshot> snapshots =
      GetRendererTrackedResourceSnapshots(&known_resources);
  ZoneValue(static_cast<int64_t>(snapshots.size()));
  std::lock_guard<std::mutex> lock(g_plume_resources.mutex);
  ++g_plume_resources.resource_retire_epoch;

  std::unordered_set<uint32_t> live_addresses;
  live_addresses.reserve(snapshots.size() +
                         (retained_draw_events
                              ? retained_draw_events->size() * 8u
                              : 0u));
  uint32_t staged_snapshot_count = 0;
  uint32_t declaration_snapshot_count = 0;
  uint32_t image_snapshot_count = 0;
  bool resource_lookup_cache_dirty = false;

  for (const RendererTrackedResourceSnapshot& snapshot : snapshots) {
    if (!IsBufferResource(snapshot.kind) && !IsShaderResource(snapshot.kind) &&
        !IsDeclarationResource(snapshot.kind) && !IsImageResource(snapshot.kind)) {
      continue;
    }
    live_addresses.insert(snapshot.guest_address);
    ++staged_snapshot_count;
    if (IsDeclarationResource(snapshot.kind)) {
      ++declaration_snapshot_count;
    }
    if (IsImageResource(snapshot.kind)) {
      ++image_snapshot_count;
    }

    auto it = g_plume_resources.resources.find(snapshot.guest_address);
    bool same_texture_shape = false;
    if (it != g_plume_resources.resources.end() &&
        IsImageResource(snapshot.kind) && it->second.kind == snapshot.kind) {
      PlumeTrackedResource decoded_texture_metadata;
      DecodeFableTextureMetadata(snapshot, decoded_texture_metadata);
      decoded_texture_metadata.texture_render_target =
          snapshot.kind == RendererTrackedResourceKind::kSurface;
      decoded_texture_metadata.texture_depth_target =
          snapshot.kind == RendererTrackedResourceKind::kDepthSurface;
      same_texture_shape =
          SameTrackedTextureShape(it->second, decoded_texture_metadata);
    }

    bool needs_create =
        it == g_plume_resources.resources.end() ||
        it->second.kind != snapshot.kind || it->second.size != snapshot.size ||
        it->second.stride_or_format != snapshot.stride_or_format ||
        it->second.guest_bytecode_hash != snapshot.guest_bytecode_hash ||
        (IsImageResource(snapshot.kind) && !same_texture_shape) ||
        (IsDeclarationResource(snapshot.kind) &&
         it->second.upload_generation != snapshot.upload_generation);
    if (!needs_create) {
      if (IsBufferResource(snapshot.kind) &&
          it->second.upload_generation != snapshot.upload_generation) {
        UploadTrackedBuffer(it->second, snapshot);
      }
      if (IsTextureResource(snapshot.kind) &&
          it->second.upload_generation != snapshot.upload_generation &&
          !RefreshTrackedTextureUpload(device, snapshot, it->second)) {
        needs_create = true;
      }
    }
    if (!needs_create) {
      if (it->second.serial != snapshot.serial ||
          it->second.parent_address != snapshot.parent_address ||
          it->second.buffer_object != snapshot.buffer_object ||
          it->second.scratch != snapshot.scratch) {
        resource_lookup_cache_dirty = true;
      }
      it->second.serial = snapshot.serial;
      it->second.parent_address = snapshot.parent_address;
      it->second.buffer_object = snapshot.buffer_object;
      it->second.scratch = snapshot.scratch;
      continue;
    }

    PlumeTrackedResource tracked;
    tracked.kind = snapshot.kind;
    tracked.guest_address = snapshot.guest_address;
    tracked.parent_address = snapshot.parent_address;
    tracked.buffer_object = snapshot.buffer_object;
    tracked.size = snapshot.size;
    tracked.stride_or_format = snapshot.stride_or_format;
    tracked.scratch = snapshot.scratch;
    tracked.guest_bytecode_hash = snapshot.guest_bytecode_hash;
    tracked.serial = snapshot.serial;
    if (IsBufferResource(snapshot.kind)) {
      tracked.buffer = CreateBufferForTrackedResource(device, snapshot);
      if (!tracked.buffer) {
        REXLOG_WARN(
            "Refii plume resource manager could not create {} guest=0x{:08X} "
            "size={} meta={}",
            TrackedResourceKindName(snapshot.kind), snapshot.guest_address,
            snapshot.size, snapshot.stride_or_format);
        continue;
      }

      NameTrackedBuffer(tracked.buffer.get(), snapshot);
      UploadTrackedBuffer(tracked, snapshot);
      if (RendererResourceTracingEnabled()) {
        REXLOG_INFO(
            "Refii plume resource manager staged {} guest=0x{:08X} size={} "
            "meta={} serial={}",
            TrackedResourceKindName(snapshot.kind), snapshot.guest_address,
            snapshot.size, snapshot.stride_or_format, snapshot.serial);
      }
    } else if (IsShaderResource(snapshot.kind)) {
      RendererShaderLibraryEntry shader_entry;
      tracked.shader = CreateShaderForTrackedResource(
          device, shader_format, snapshot, shader_entry);
      if (tracked.shader) {
        tracked.shader_name = shader_entry.name;
        if (snapshot.kind == RendererTrackedResourceKind::kVertexShader) {
          tracked.input_elements =
              ParseShaderInputSignature(shader_entry.hlsl_path);
        } else if (snapshot.kind == RendererTrackedResourceKind::kPixelShader) {
          tracked.pixel_shader_writes_depth =
              ShaderHlslContainsToken(shader_entry.hlsl_path, "SV_Depth");
          tracked.pixel_shader_samples_depth_texture = ShaderHlslContainsToken(
              shader_entry.hlsl_path, "g_DepthTextureSampler_");
        }
        NameTrackedShader(tracked.shader.get(), snapshot, shader_entry);
        if (RendererResourceTracingEnabled()) {
          REXLOG_INFO(
              "Refii plume resource manager staged {} guest=0x{:08X} "
              "shader_id={:04d} inputs={} hash=0x{:016X} serial={}",
              TrackedResourceKindName(snapshot.kind), snapshot.guest_address,
              shader_entry.id, tracked.input_elements.size(),
              snapshot.guest_bytecode_hash, snapshot.serial);
        }
      } else {
        REXLOG_WARN(
            "Refii plume resource manager could not resolve {} guest=0x{:08X} "
            "hash=0x{:016X} size={}",
            TrackedResourceKindName(snapshot.kind), snapshot.guest_address,
            snapshot.guest_bytecode_hash, snapshot.size);
      }
    } else if (IsDeclarationResource(snapshot.kind)) {
      if (DecodeVertexDeclaration(tracked, snapshot)) {
        tracked.upload_generation = snapshot.upload_generation;
        if (RendererResourceTracingEnabled()) {
          REXLOG_INFO(
              "Refii plume resource manager staged {} guest=0x{:08X} "
              "elements={} streams={} hash=0x{:016X} serial={} decl={}",
              TrackedResourceKindName(snapshot.kind), snapshot.guest_address,
              tracked.input_elements.size(), tracked.input_slots.size(),
              snapshot.guest_bytecode_hash, snapshot.serial,
              tracked.debug_summary);
        }
      } else {
        if (g_plume_resources.logged_failed_vertex_declarations.insert(
                snapshot.guest_address)
                .second) {
          REXLOG_WARN(
              "Refii plume resource manager could not decode {} guest=0x{:08X} "
              "bytes={} element_count={} hash=0x{:016X}; falling back to "
              "shader-derived input layout when possible",
              TrackedResourceKindName(snapshot.kind), snapshot.guest_address,
              snapshot.upload_data.size(), snapshot.stride_or_format,
              snapshot.guest_bytecode_hash);
        }
      }
    } else if (IsImageResource(snapshot.kind)) {
      tracked.texture = CreateTextureForTrackedResource(device, snapshot, tracked);
      if (tracked.texture) {
        NameTrackedTexture(tracked.texture.get(), snapshot, tracked);
        plume::RenderTextureViewDesc view_desc = TextureViewDescForDimension(
            tracked.texture_view_dimension, tracked.texture_format);
        view_desc.componentMapping = XenosTextureComponentMapping(
            tracked.texture_guest_format,
            rex::graphics::xenos::XE_GPU_TEXTURE_SWIZZLE_RGBA);
        tracked.texture_view = tracked.texture->createTextureView(view_desc);
      }
      if (!tracked.texture || !tracked.texture_view) {
        REXLOG_WARN(
            "Refii plume resource manager could not create {} guest=0x{:08X} "
            "packed36=0x{:08X} packed44=0x{:08X} packed48=0x{:08X}",
            TrackedResourceKindName(snapshot.kind), snapshot.guest_address,
            snapshot.raw_packed36, snapshot.raw_packed44,
            snapshot.raw_packed48);
        continue;
      }

      std::vector<uint8_t> upload_bytes;
      if (PrepareTextureUploadBytes(snapshot, tracked, upload_bytes)) {
        tracked.texture_upload = device->createBuffer(
            plume::RenderBufferDesc::UploadBuffer(
                static_cast<uint32_t>(upload_bytes.size())));
      }
      if (tracked.texture_upload && !upload_bytes.empty()) {
        if (void* dst = tracked.texture_upload->map()) {
          std::memcpy(dst, upload_bytes.data(), upload_bytes.size());
          plume::RenderRange written_range(0, upload_bytes.size());
          tracked.texture_upload->unmap(0, &written_range);
        }
        tracked.upload_generation = snapshot.upload_generation;
        tracked.texture_upload_pending = true;
      }

      if (RendererResourceTracingEnabled()) {
        REXLOG_INFO(
            "Refii plume resource manager staged {} guest=0x{:08X} "
            "view={} fmt={} guest_fmt={} guest_size={}x{}x{} mips={} "
            "upload={} layout={} tiled={} raw36=0x{:08X} raw44=0x{:08X}",
            TrackedResourceKindName(snapshot.kind), snapshot.guest_address,
            static_cast<uint32_t>(tracked.texture_view_dimension),
            static_cast<uint32_t>(tracked.texture_format),
            tracked.texture_guest_format, tracked.texture_width,
            tracked.texture_height, tracked.texture_depth,
            tracked.texture_mip_levels, snapshot.upload_data.size(),
            tracked.texture_upload_decoded ? "decoded" : "raw",
            tracked.texture_guest_tiled ? 1 : 0,
            snapshot.raw_packed36, snapshot.raw_packed44);
      }
    }
    auto existing_resource =
        g_plume_resources.resources.find(snapshot.guest_address);
    if (existing_resource != g_plume_resources.resources.end()) {
      const uint64_t valid_until =
          snapshot.serial != 0 ? snapshot.serial - 1u : resource_version.serial;
      RetirePlumeResourceLocked(std::move(existing_resource->second),
                                valid_until);
      existing_resource->second = std::move(tracked);
      resource_lookup_cache_dirty = true;
    } else {
      g_plume_resources.resources.emplace(snapshot.guest_address,
                                          std::move(tracked));
      resource_lookup_cache_dirty = true;
    }
  }

  AddReferencedFableResourcesToLiveSetLocked(live_addresses,
                                             retained_draw_events);

  for (auto it = g_plume_resources.resources.begin();
       it != g_plume_resources.resources.end();) {
    if (live_addresses.find(it->first) == live_addresses.end()) {
      if (it->second.scratch) {
        ++it;
        continue;
      }
      if (RendererResourceTracingEnabled()) {
        REXLOG_INFO("Refii plume resource manager released {} guest=0x{:08X}",
                    TrackedResourceKindName(it->second.kind), it->first);
      }
      RetirePlumeResourceLocked(std::move(it->second),
                                resource_version.serial);
      it = g_plume_resources.resources.erase(it);
      resource_lookup_cache_dirty = true;
    } else {
      ++it;
    }
  }

  size_t plume_scratch_count = 0;
  for (const auto& [address, resource] : g_plume_resources.resources) {
    (void)address;
    if (resource.scratch) {
      ++plume_scratch_count;
    }
  }
  while (plume_scratch_count > kMaxPlumeScratchResources) {
    auto oldest = g_plume_resources.resources.end();
    for (auto it = g_plume_resources.resources.begin();
         it != g_plume_resources.resources.end(); ++it) {
      if (!it->second.scratch) {
        continue;
      }
      if (oldest == g_plume_resources.resources.end() ||
          it->second.serial < oldest->second.serial) {
        oldest = it;
      }
    }
    if (oldest == g_plume_resources.resources.end()) {
      break;
    }
    RetirePlumeResourceLocked(std::move(oldest->second),
                              resource_version.serial);
    g_plume_resources.resources.erase(oldest);
    resource_lookup_cache_dirty = true;
    --plume_scratch_count;
  }
  TrimRetiredPlumeResourcesLocked();
  if (resource_lookup_cache_dirty) {
    g_plume_resources.resource_lookup_cache.clear();
  }

  if (staged_snapshot_count == 0 &&
      !g_plume_resources.logged_waiting_for_buffers) {
    REXLOG_INFO(
        "Refii plume resource manager is waiting for Fable vertex/index buffer "
        "creation hooks before staging draw resources");
    g_plume_resources.logged_waiting_for_buffers = true;
  }
  if (declaration_snapshot_count == 0 &&
      !g_plume_resources.logged_waiting_for_declarations) {
    REXLOG_INFO(
        "Refii plume resource manager is waiting for Fable vertex declaration "
        "creation before building draw pipelines");
    g_plume_resources.logged_waiting_for_declarations = true;
  }
  if (image_snapshot_count != 0 && RendererResourceTracingEnabled()) {
    REXLOG_INFO(
        "Refii plume resource manager has {} live Fable image resources staged",
        image_snapshot_count);
  }
  g_plume_resources.last_resource_sync_version = resource_version;
  g_plume_resources.resource_sync_version_valid = true;
  g_plume_resources.texture_lookup_index_valid = false;
}

void RecordPlumeResourceUploads(plume::RenderDevice* device,
                                plume::RenderCommandList* command_list) {
  ZoneScopedN("RefiiPlumeResources/RecordResourceUploads");
  std::lock_guard<std::mutex> lock(g_plume_resources.mutex);
  if (!EnsureFablePipelineLayoutLocked(device)) {
    return;
  }
  RecordFallbackTextureUploadsLocked(command_list);
  RecordTrackedTextureUploadsLocked(command_list);
}

PlumeDrawReplayStats RecordPlumeFableDraws(
    plume::RenderDevice* device, plume::RenderCommandList* command_list,
    const std::vector<RendererDrawEventSnapshot>& draw_events,
    plume::RenderFramebuffer* output_framebuffer,
    plume::RenderTexture* output_texture,
    plume::RenderFramebuffer* present_framebuffer,
    plume::RenderShaderFormat shader_format,
    plume::RenderFormat output_depth_format) {
  ZoneScopedN("RefiiPlumeResources/RecordFableDraws");
  ZoneValue(static_cast<int64_t>(draw_events.size()));
  PlumeDrawReplayStats stats;

  std::lock_guard<std::mutex> lock(g_plume_resources.mutex);
  stats.present_render_target =
      SelectPresentRenderTargetLocked(
          draw_events, &stats.present_render_target_resource_serial);
  g_plume_resources.frame_constant_slab_offset = 0;
  g_plume_resources.frame_constant_buffers.clear();
  g_plume_resources.resolve_texture_alias_hits_this_frame = 0;
  InvalidateFableDescriptorCacheLocked();
  ClearFableFrameLookupCacheLocked();
  EnsureFableTextureLookupIndexLocked();
  std::unordered_set<uint32_t> render_targets_seen;
  std::unordered_set<uint32_t> direct_video_plane_targets;
  for (const RendererDrawEventSnapshot& draw_event : draw_events) {
    ScopedPlumeResourceLookupSerial scoped_resource_lookup(
        draw_event.resource_serial);

    if (LooksLikeFableDirectVideoPlaneDrawLocked(draw_event)) {
      for (uint32_t render_target : draw_event.render_targets) {
        if (render_target != 0) {
          direct_video_plane_targets.insert(render_target);
        }
      }
    }
  }
  std::string current_framebuffer_key = "output";
  for (const RendererDrawEventSnapshot& draw_event : draw_events) {
    ScopedPlumeResourceLookupSerial scoped_resource_lookup(
        draw_event.resource_serial);

    if (draw_event.clear) {
      RecordFableClearLocked(device, command_list, draw_event,
                             output_framebuffer, output_depth_format,
                             stats.present_render_target,
                             current_framebuffer_key, stats);
      continue;
    }

    ++stats.total_draws;
    if (draw_event.draw.primitive_type <
        PlumeDrawReplayStats::kPrimitiveStatsCount) {
      ++stats.primitive_draws[draw_event.draw.primitive_type];
      stats.primitive_counts[draw_event.draw.primitive_type] +=
          draw_event.draw.primitive_count;
    }
    for (uint32_t i = 0; i < kMaxFableRenderTargets; ++i) {
      if (draw_event.render_targets[i] != 0) {
        render_targets_seen.insert(draw_event.render_targets[i]);
        if (stats.first_render_target == 0) {
          stats.first_render_target = draw_event.render_targets[i];
        }
      }
    }
    if (ApplyFableResolveCopyDrawLocked(device, draw_event, stats)) {
      continue;
    }
    const uint32_t bound_render_target_count =
        CountBoundRenderTargets(draw_event);
    if (bound_render_target_count > 1) {
      ++stats.multi_render_target_draws;
    }
    if (DrawHasSparseRenderTargetSlots(draw_event)) {
      ++stats.sparse_render_target_draws;
    }
    stats.max_render_targets_per_draw =
        std::max(stats.max_render_targets_per_draw, bound_render_target_count);
    const bool replay_to_output =
        ShouldReplayDrawToCurrentOutput(draw_event, stats.present_render_target);
    if (replay_to_output) {
      ++stats.present_target_draws;
    } else {
      ++stats.offscreen_target_draws;
    }
    if (DrawTargetsSelectedPresentTarget(draw_event,
                                         stats.present_render_target) &&
        draw_event.render_targets[0] != stats.present_render_target) {
      ++stats.present_target_nonzero_slot_draws;
    }

    AccumulateTextureBindingStatsLocked(draw_event, stats);

    if (draw_event.draw.indexed) {
      ++stats.indexed_draws;
    }
    if (draw_event.draw.primitive_type == 8) {
      ++stats.rectangle_list_draws;
      stats.rectangle_list_vertex_or_index_count +=
          draw_event.draw.primitive_count;
      if (draw_event.draw.indexed) {
        ++stats.rectangle_list_indexed_draws;
      } else {
        ++stats.rectangle_list_direct_draws;
      }
    }
    if (draw_event.draw.primitive_type == 13) {
      ++stats.quad_list_draws;
      stats.quad_list_vertex_or_index_count += draw_event.draw.primitive_count;
      if (draw_event.draw.indexed) {
        ++stats.quad_list_indexed_draws;
        stats.quad_list_expanded_index_count +=
            uint64_t(draw_event.draw.primitive_count / 4u) * 6u;
      } else {
        ++stats.quad_list_direct_draws;
        stats.quad_list_expanded_index_count +=
            uint64_t(draw_event.draw.primitive_count / 4u) * 6u;
      }
    }
    if (draw_event.depth_stencil != 0) {
      ++stats.depth_bound_draws;
    } else {
      const bool z_requested =
          draw_event.render_states[kFableRenderStateZEnable] != 0 ||
          draw_event.render_states[kFableRenderStateZWriteEnable] != 0;
      const bool hiz_requested =
          draw_event.render_states[kFableRenderStateHiZWriteEnable] != 0;
      if (z_requested || hiz_requested) {
        ++stats.depth_requested_without_surface_draws;
      }
      if (z_requested) {
        ++stats.z_requested_without_surface_draws;
      }
      if (hiz_requested) {
        ++stats.hiz_requested_without_surface_draws;
      }
    }

    if (IsFablePixVideoRectangleDraw(draw_event) &&
        draw_event.render_targets[0] != 0 &&
        direct_video_plane_targets.contains(draw_event.render_targets[0])) {
      ++stats.target_filtered_draws;
      continue;
    }

    if (!IsFableRasterizationPotentiallyDone(draw_event)) {
      if (!g_plume_resources.logged_xenos_raster_skip) {
        REXLOG_INFO(
            "Refii plume skipped non-rasterizing Xenos draw prim={} "
            "indexed={} count={} rt0=0x{:08X} depth=0x{:08X} "
            "rb_mode=0x{:08X} rb_surface=0x{:08X} pa_su=0x{:08X} "
            "vs=0x{:08X} ps=0x{:08X}",
            draw_event.draw.primitive_type, draw_event.draw.indexed ? 1 : 0,
            draw_event.draw.primitive_count, draw_event.render_targets[0],
            draw_event.depth_stencil, draw_event.rb_modecontrol,
            draw_event.rb_surface_info, draw_event.pa_su_sc_mode_cntl,
            draw_event.vertex_shader, draw_event.pixel_shader);
        g_plume_resources.logged_xenos_raster_skip = true;
      }
      ++stats.target_filtered_draws;
      continue;
    }

    const bool has_vertex_buffer =
        HasAnyBoundVertexBufferLocked(draw_event) ||
        CanUseFableImplicitFullscreenRectangleInputLocked(draw_event) ||
        CanUseFablePrivateTextureRectangleInputLocked(draw_event);

    const bool has_index_buffer =
        !draw_event.draw.indexed ||
        HasTrackedResourceLocked(draw_event.index_buffer,
                                 RendererTrackedResourceKind::kIndexBuffer);
    const bool has_vertex_shader = HasTrackedResourceLocked(
        draw_event.vertex_shader, RendererTrackedResourceKind::kVertexShader);
    const bool has_pixel_shader = HasTrackedResourceLocked(
        draw_event.pixel_shader, RendererTrackedResourceKind::kPixelShader);
    const bool has_vertex_declaration = HasUsableVertexInputLocked(
        draw_event, has_vertex_buffer, has_vertex_shader);

    if (!has_vertex_buffer) {
      ++stats.missing_vertex_buffer;
      if (RendererDrawTracingEnabled() &&
          g_plume_resources.logged_missing_vertex_buffer_draws < 16) {
        std::string streams;
        char stream_buffer[192] = {};
        for (uint32_t slot = 0; slot < 4 && slot < std::size(draw_event.streams);
             ++slot) {
          const RendererStreamBindingSnapshot& stream =
              draw_event.streams[slot];
          if (stream.buffer == 0 && stream.stride == 0 && stream.offset == 0) {
            continue;
          }
          const PlumeTrackedResource* resource = FindTrackedResourceLocked(
              stream.buffer, RendererTrackedResourceKind::kVertexBuffer);
          std::snprintf(
              stream_buffer, sizeof(stream_buffer),
              "%ss%u=0x%08X+%u/%u tracked=%u size=%u serial=%llu",
              streams.empty() ? "" : " ", slot, stream.buffer, stream.offset,
              stream.stride, resource ? 1u : 0u,
              resource ? resource->size : 0u,
              static_cast<unsigned long long>(resource ? resource->serial : 0));
          streams += stream_buffer;
        }
        if (streams.empty()) {
          streams = "none";
        }
        REXLOG_INFO(
            "Refii plume missing Fable vertex buffer draw #{} prim={} "
            "indexed={} count={} start={} base={} decl=0x{:08X} "
            "vs=0x{:08X} ps=0x{:08X} rt0=0x{:08X} depth=0x{:08X} "
            "resource_serial={} streams={}",
            g_plume_resources.logged_missing_vertex_buffer_draws + 1u,
            draw_event.draw.primitive_type, draw_event.draw.indexed ? 1 : 0,
            draw_event.draw.primitive_count, draw_event.draw.start,
            draw_event.draw.base_vertex, draw_event.vertex_declaration,
            draw_event.vertex_shader, draw_event.pixel_shader,
            draw_event.render_targets[0], draw_event.depth_stencil,
            static_cast<unsigned long long>(draw_event.resource_serial),
            streams);
        ++g_plume_resources.logged_missing_vertex_buffer_draws;
      }
    }
    if (!has_index_buffer) {
      ++stats.missing_index_buffer;
    }
    if (has_vertex_declaration) {
      ++stats.declaration_ready_draws;
    } else {
      ++stats.missing_vertex_declaration;
      if (stats.first_missing_vertex_declaration == 0) {
        stats.first_missing_vertex_declaration = draw_event.vertex_declaration;
      }
    }
    if (!has_vertex_shader) {
      ++stats.missing_vertex_shader;
      if (stats.first_missing_vertex_shader == 0) {
        stats.first_missing_vertex_shader = draw_event.vertex_shader;
      }
    }
    if (!has_pixel_shader) {
      ++stats.missing_pixel_shader;
      if (stats.first_missing_pixel_shader == 0) {
        stats.first_missing_pixel_shader = draw_event.pixel_shader;
      }
    }

    if (has_vertex_buffer && has_index_buffer && has_vertex_declaration &&
        has_vertex_shader && has_pixel_shader) {
      ++stats.resource_ready_draws;
      std::vector<plume::RenderInputSlot> input_slots;
      std::vector<plume::RenderInputElement> input_elements;
      FableDrawTargetDesc target =
          BuildFableDrawTargetDescLocked(device, draw_event);
      if (!replay_to_output) {
        target.uses_output = false;
      }
      const PlumeTrackedResource* unsafe_depth_export_shader =
          FindUnsafeVulkanDepthSampleExportShaderLocked(draw_event,
                                                       shader_format);
      const PlumeTrackedResource* depth_sample_shader =
          FindVulkanDepthSampleShaderLocked(draw_event, shader_format);
      if (depth_sample_shader) {
        ++stats.depth_sample_shader_draws;
      }
      const PlumeTrackedResource* unsafe_translated_shader =
          FindUnsafeVulkanTranslatedShaderLocked(draw_event, shader_format);
      if (unsafe_translated_shader) {
        if (g_plume_resources.logged_vulkan_unsafe_translated_shaders
                .insert(draw_event.pixel_shader)
                .second) {
          REXLOG_WARN(
              "Refii plume is filtering Vulkan Fable draws for unsafe "
              "translated shader until its Fable 2 resource shape is decoded "
              "shader=0x{:08X} name={} prim={} indexed={} count={} "
              "rt=0x{:08X} depth=0x{:08X} texture_sampler={} mask_sampler={}",
              draw_event.pixel_shader, unsafe_translated_shader->shader_name,
              draw_event.draw.primitive_type, draw_event.draw.indexed ? 1 : 0,
              draw_event.draw.primitive_count, draw_event.render_targets[0],
              draw_event.depth_stencil,
              FormatFableSampleSlotForLogLocked(draw_event, 13),
              FormatFableSampleSlotForLogLocked(draw_event, 14));
          g_plume_resources.logged_vulkan_unsafe_translated_shader_skip = true;
        }
        ++stats.target_filtered_draws;
        continue;
      }
      bool depth_sample_used_active_depth_fallback = false;
      const PlumeTrackedResource* depth_sample_source =
          kEnableVulkanDepthSampleExportShaderDirect &&
                  depth_sample_shader
              ? FindFableDepthSampleSourceLocked(
                    draw_event, &depth_sample_used_active_depth_fallback)
              : nullptr;
      if (depth_sample_source) {
        ++stats.depth_sample_source_draws;
        if (depth_sample_used_active_depth_fallback) {
          ++stats.depth_sample_active_depth_fallback_draws;
        }
      }
      if (depth_sample_shader && depth_sample_source &&
          target.has_depth && target.depth_texture) {
        target.uses_output = false;
        if (!g_plume_resources.logged_vulkan_depth_sample_source) {
          REXLOG_WARN(
              "Refii plume is replaying Vulkan Fable depth-sample "
              "draws with slot13 source=0x{:08X} {} {}x{} fmt={} target_ds=0x{:08X} "
              "{}x{} shader=0x{:08X} name={} active_depth_fallback={}",
              depth_sample_source->guest_address,
              TrackedResourceKindName(depth_sample_source->kind),
              depth_sample_source->texture_width,
              depth_sample_source->texture_height,
              static_cast<uint32_t>(depth_sample_source->texture_format),
              target.depth_address, target.depth_width, target.depth_height,
              draw_event.pixel_shader, depth_sample_shader->shader_name,
              depth_sample_used_active_depth_fallback ? 1 : 0);
          g_plume_resources.logged_vulkan_depth_sample_source = true;
        }
      }
      bool depth_sample_source_aliases_target =
          FableDepthSampleSourceAliasesTargetLocked(depth_sample_source, target);
      if (target.uses_output && output_depth_format != plume::RenderFormat::UNKNOWN) {
        target.depth_address = draw_event.depth_stencil;
        target.depth_format = output_depth_format;
        target.has_depth = true;
      } else if (target.uses_output) {
        target.depth_address = 0;
        target.depth_format = plume::RenderFormat::UNKNOWN;
        target.depth_texture = nullptr;
        target.depth_view = nullptr;
        target.has_depth = false;
      }
      if (unsafe_depth_export_shader && depth_sample_source &&
          (!target.has_depth || !target.depth_texture) &&
          draw_event.depth_stencil != 0) {
        PopulateFableDepthTarget(
            target, draw_event.depth_stencil,
            FindTrackedDepthTargetLocked(draw_event.depth_stencil));
      }
      if (unsafe_depth_export_shader && depth_sample_source &&
          (!target.has_depth || !target.depth_texture)) {
        AttachFallbackDepthTargetForDepthExportLocked(
            device, target, draw_event, *depth_sample_source);
      }
      if (unsafe_depth_export_shader && depth_sample_source &&
          target.has_depth && target.depth_texture) {
        target.uses_output = false;
      }
      depth_sample_source_aliases_target =
          FableDepthSampleSourceAliasesTargetLocked(depth_sample_source, target);
      if (depth_sample_source_aliases_target) {
        ++stats.depth_sample_alias_draws;
      }
      PlumeDepthSampleCopy* depth_sample_copy = nullptr;
      if (kEnableVulkanDepthSampleExportShaderCopy &&
          depth_sample_shader && depth_sample_source_aliases_target &&
          target.has_depth && target.depth_texture) {
        depth_sample_copy =
            PrepareDepthSampleCopyLocked(device, command_list, target);
        if (depth_sample_copy) {
          ++stats.depth_sample_copy_draws;
          current_framebuffer_key.clear();
        }
        if (depth_sample_copy &&
            !g_plume_resources.logged_vulkan_depth_sample_copy) {
          REXLOG_WARN(
              "Refii plume is feeding Vulkan Fable depth-sample "
              "draws from a copied active depth surface shader=0x{:08X} "
              "name={} depth=0x{:08X}",
              draw_event.pixel_shader, depth_sample_shader->shader_name,
              target.depth_address);
          g_plume_resources.logged_vulkan_depth_sample_copy = true;
        }
      }
      if (depth_sample_shader && depth_sample_source_aliases_target &&
          !depth_sample_copy) {
        if (!g_plume_resources.logged_vulkan_depth_export_unavailable) {
          REXLOG_WARN(
              "Refii plume filtered Vulkan Fable depth-sample draw because the "
              "source aliases the active depth target and no copy was available "
              "shader=0x{:08X} name={} depth=0x{:08X} size={}x{} fmt={}",
              draw_event.pixel_shader, depth_sample_shader->shader_name,
              target.depth_address, target.depth_width, target.depth_height,
              static_cast<uint32_t>(target.depth_format));
          g_plume_resources.logged_vulkan_depth_export_unavailable = true;
        }
        ++stats.depth_sample_unavailable_draws;
        ++stats.target_filtered_draws;
        continue;
      }
      if (depth_sample_copy) {
        depth_sample_source = nullptr;
      }
      if (unsafe_depth_export_shader && !depth_sample_copy &&
          !(depth_sample_source && target.has_depth && target.depth_texture)) {
        if (!g_plume_resources.logged_vulkan_depth_export_unavailable) {
          REXLOG_WARN(
              "Refii plume Vulkan depth-sample/SV_Depth direct path unavailable "
              "source={} source_size={}x{}x{} target_has_depth={} "
              "target_texture={} target=0x{:08X} size={}x{} fmt={} uses_output={}",
              depth_sample_source ? 1 : 0,
              depth_sample_source ? depth_sample_source->texture_width : 0,
              depth_sample_source ? depth_sample_source->texture_height : 0,
              depth_sample_source ? depth_sample_source->texture_depth : 0,
              target.has_depth ? 1 : 0, target.depth_texture ? 1 : 0,
              target.depth_address, target.depth_width, target.depth_height,
              static_cast<uint32_t>(target.depth_format),
              target.uses_output ? 1 : 0);
          g_plume_resources.logged_vulkan_depth_export_unavailable = true;
        }
        RouteUnsafeVulkanDepthSampleExportAsColorOnlyLocked(
            draw_event, unsafe_depth_export_shader, target);
        depth_sample_source = nullptr;
        ++stats.depth_sample_unavailable_draws;
        ++stats.target_filtered_draws;
        continue;
      }
      constexpr uint32_t kFableDepthTextureSamplerSlot = 13;
      const uint32_t ignored_feedback_slot =
          depth_sample_copy ? kFableDepthTextureSamplerSlot : UINT32_MAX;
      bool depth_feedback_copied = false;
      const PlumeDepthSampleCopySlots depth_sample_copies =
          PrepareDepthFeedbackCopiesLocked(device, command_list, draw_event,
                                           target, stats, depth_sample_copy,
                                           depth_feedback_copied);
      if (depth_feedback_copied) {
        current_framebuffer_key.clear();
      }
      bool color_feedback_copied = false;
      const PlumeColorSampleCopySlots color_sample_copies =
          PrepareColorFeedbackCopiesLocked(device, command_list, draw_event,
                                           target, stats,
                                           color_feedback_copied);
      if (color_feedback_copied) {
        current_framebuffer_key.clear();
      }
      if (DrawSamplesFableTargetLocked(draw_event, target,
                                       ignored_feedback_slot,
                                       &depth_sample_copies,
                                       &color_sample_copies)) {
        ++stats.target_filtered_draws;
        continue;
      }
      if (target.has_depth && draw_event.depth_stencil != 0) {
        ++stats.depth_tracked_draws;
        if (!target.has_color && !target.uses_output) {
          ++stats.depth_only_draws;
        }
      }
      const FablePipelineRenderState replay_render_state =
          BuildFablePipelineRenderState(draw_event, target);
      if (replay_render_state.depth_enabled) {
        ++stats.depth_enabled_draws;
      }
      if (replay_render_state.depth_write_enabled) {
        ++stats.depth_write_draws;
      }
      if (replay_render_state.stencil_enabled) {
        ++stats.stencil_enabled_draws;
      }
      if (replay_render_state.alpha_test_enabled) {
        ++stats.alpha_test_draws;
      }
      const FableDrawShaderRouting shader_routing =
          ResolveFableDrawShaderRoutingLocked(draw_event);
      plume::RenderPipeline* pipeline = nullptr;
      if (BuildInputLayoutForDrawLocked(draw_event, input_slots,
                                        input_elements)) {
        pipeline = GetOrCreateFablePipelineLocked(
            device, draw_event, input_slots, input_elements, target,
            shader_format, shader_routing.private_texture_copy_draw);
      }
      if (!pipeline) {
        ++stats.missing_pipeline;
        continue;
      } else {
        ++stats.pipeline_ready_draws;
      }

      plume::RenderFramebuffer* draw_framebuffer = output_framebuffer;
      std::string draw_framebuffer_key = "output";
      if (!target.uses_output) {
        draw_framebuffer =
            GetOrCreateFableFramebufferLocked(device, target);
        if (draw_framebuffer) {
          draw_framebuffer_key = MakeFramebufferKey(target);
        } else if (!replay_to_output) {
          ++stats.missing_render_targets;
        }
      }

      if (!draw_framebuffer && !replay_to_output) {
        ++stats.target_filtered_draws;
      } else if (draw_framebuffer) {
        if (draw_framebuffer_key != current_framebuffer_key) {
          if (!target.uses_output) {
            for (uint32_t i = 0; i < target.color_count; ++i) {
              if (target.color_textures[i]) {
                command_list->barriers(
                    plume::RenderBarrierStage::GRAPHICS,
                    plume::RenderTextureBarrier(
                        target.color_textures[i],
                        plume::RenderTextureLayout::COLOR_WRITE));
              }
            }
          }
          if (!target.uses_output && target.has_depth && target.depth_texture) {
            command_list->barriers(
                plume::RenderBarrierStage::GRAPHICS,
                plume::RenderTextureBarrier(
                    target.depth_texture,
                    plume::RenderTextureLayout::DEPTH_WRITE));
          }
          command_list->setFramebuffer(draw_framebuffer);
          current_framebuffer_key = draw_framebuffer_key;
          ++stats.framebuffer_switches;
        }
        if (RecordReadyFableDrawLocked(device, command_list, draw_event,
                                       target, pipeline, input_slots,
                                       input_elements, depth_sample_copy,
                                       depth_sample_source,
                                       &depth_sample_copies,
                                       color_feedback_copied
                                           ? &color_sample_copies
                                           : nullptr,
                                       shader_routing)) {
          ++stats.recorded_draws;
          if (LooksLikeFableDirectVideoPlaneDrawLocked(draw_event)) {
            for (uint32_t render_target : draw_event.render_targets) {
              if (render_target != 0) {
                direct_video_plane_targets.insert(render_target);
              }
            }
          }
        } else {
          ++stats.pipeline_create_failed;
        }
      } else {
        ++stats.pipeline_create_failed;
      }
    }
  }
  stats.unique_render_targets =
      static_cast<uint32_t>(render_targets_seen.size());
  stats.texture_uploads_this_frame =
      g_plume_resources.tracked_texture_uploads_this_frame;
  stats.decoded_texture_uploads_this_frame =
      g_plume_resources.decoded_tracked_texture_uploads_this_frame;
  stats.raw_texture_uploads_this_frame =
      g_plume_resources.raw_tracked_texture_uploads_this_frame;
  stats.placeholder_texture_uploads_this_frame =
      g_plume_resources.placeholder_tracked_texture_uploads_this_frame;
  stats.pending_texture_uploads =
      g_plume_resources.pending_tracked_texture_uploads;
  stats.texture_uploads_total =
      g_plume_resources.tracked_texture_uploads_total;
  stats.resolve_texture_alias_hits =
      g_plume_resources.resolve_texture_alias_hits_this_frame;
  const uint32_t resolved_present_target =
      ResolvePresentRenderTargetFromFrameAliasesLocked(
          stats.present_render_target,
          stats.present_render_target_resource_serial,
          &stats.present_render_target_resource_serial);
  stats.present_render_target = resolved_present_target;
  if (stats.present_render_target != 0) {
    ScopedPlumeResourceLookupSerial scoped_present_lookup(
        stats.present_render_target_resource_serial);
    if (const PlumeTrackedResource* present =
            FindTrackedColorRenderTargetLocked(stats.present_render_target)) {
      stats.present_render_target_width = present->texture_width;
      stats.present_render_target_height = present->texture_height;
    }
  }
  if (CopyPresentRenderTargetToOutputLocked(device, command_list,
                                            shader_format,
                                            stats.present_render_target,
                                            stats.present_render_target_resource_serial,
                                            output_texture,
                                            present_framebuffer)) {
    ++stats.present_target_copies;
  }

  if (stats.resource_ready_draws != 0 &&
      !g_plume_resources.logged_first_recorded_draw_bridge) {
    REXLOG_INFO(
        "Refii plume draw bridge ready: resource_ready={} "
        "pipeline_ready={} recorded={} failed={}",
        stats.resource_ready_draws, stats.pipeline_ready_draws,
        stats.recorded_draws, stats.pipeline_create_failed);
    g_plume_resources.logged_first_recorded_draw_bridge = true;
  }

  g_plume_resources.last_draw_replay_stats = stats;
  return stats;
}

PlumeDrawReplayStats RecordPlumeCachedFablePresent(
    plume::RenderDevice* device, plume::RenderCommandList* command_list,
    plume::RenderTexture* output_texture,
    plume::RenderFramebuffer* present_framebuffer,
    plume::RenderShaderFormat shader_format) {
  ZoneScopedN("RefiiPlumeResources/RecordCachedFablePresent");
  std::lock_guard<std::mutex> lock(g_plume_resources.mutex);
  PlumeDrawReplayStats stats = g_plume_resources.last_draw_replay_stats;
  if (stats.present_render_target == 0) {
    return stats;
  }

  const uint32_t resolved_present_target =
      ResolvePresentRenderTargetFromFrameAliasesLocked(
          stats.present_render_target,
          stats.present_render_target_resource_serial,
          &stats.present_render_target_resource_serial);
  stats.present_render_target = resolved_present_target;
  if (stats.present_render_target != 0) {
    ScopedPlumeResourceLookupSerial scoped_present_lookup(
        stats.present_render_target_resource_serial);
    if (const PlumeTrackedResource* present =
            FindTrackedColorRenderTargetLocked(stats.present_render_target)) {
      stats.present_render_target_width = present->texture_width;
      stats.present_render_target_height = present->texture_height;
    }
  }
  if (CopyPresentRenderTargetToOutputLocked(
          device, command_list, shader_format, stats.present_render_target,
          stats.present_render_target_resource_serial, output_texture,
          present_framebuffer)) {
    ++stats.present_target_copies;
  }
  return stats;
}
#endif

PlumeDrawResourceReadiness AnalyzePlumeDrawResourceReadiness(
    const std::vector<RendererDrawEventSnapshot>& draw_events) {
  ZoneScopedN("RefiiPlumeResources/AnalyzeDrawReadiness");
  ZoneValue(static_cast<int64_t>(draw_events.size()));
  PlumeDrawResourceReadiness readiness;
#if REFII_RENDERER_HAS_PLUME
  std::lock_guard<std::mutex> lock(g_plume_resources.mutex);
#endif
  for (const RendererDrawEventSnapshot& draw_event : draw_events) {
    if (draw_event.clear) {
      continue;
    }
    ++readiness.total_draws;
    if (draw_event.draw.indexed) {
      ++readiness.indexed_draws;
    }

#if REFII_RENDERER_HAS_PLUME
    const bool has_vertex_buffer =
        HasAnyBoundVertexBufferLocked(draw_event) ||
        CanUseFableImplicitFullscreenRectangleInputLocked(draw_event) ||
        CanUseFablePrivateTextureRectangleInputLocked(draw_event);
    const bool has_index_buffer =
        !draw_event.draw.indexed ||
        HasTrackedResourceLocked(draw_event.index_buffer,
                                 RendererTrackedResourceKind::kIndexBuffer);
    const bool has_vertex_shader = HasTrackedResourceLocked(
        draw_event.vertex_shader, RendererTrackedResourceKind::kVertexShader);
    const bool has_pixel_shader = HasTrackedResourceLocked(
        draw_event.pixel_shader, RendererTrackedResourceKind::kPixelShader);
    const bool has_vertex_declaration = HasUsableVertexInputLocked(
        draw_event, has_vertex_buffer, has_vertex_shader);
#else
    const bool has_vertex_buffer = false;
    const bool has_index_buffer = !draw_event.draw.indexed;
    const bool has_vertex_declaration = false;
    const bool has_vertex_shader = false;
    const bool has_pixel_shader = false;
#endif

    if (has_vertex_buffer) {
      ++readiness.draws_with_vertex_buffer;
    } else {
      ++readiness.missing_vertex_buffer;
    }
    if (has_index_buffer) {
      ++readiness.draws_with_index_buffer;
    } else {
      ++readiness.missing_index_buffer;
    }
    if (has_vertex_declaration) {
      ++readiness.draws_with_vertex_declaration;
    } else {
      ++readiness.missing_vertex_declaration;
    }
    if (has_vertex_shader) {
      ++readiness.draws_with_vertex_shader;
    } else {
      ++readiness.missing_vertex_shader;
    }
    if (has_pixel_shader) {
      ++readiness.draws_with_pixel_shader;
    } else {
      ++readiness.missing_pixel_shader;
    }
  }

  return readiness;
}

PlumeDrawReplayStats GetLastPlumeDrawReplayStats() {
  std::lock_guard<std::mutex> lock(g_plume_resources.mutex);
  PlumeDrawReplayStats stats = g_plume_resources.last_draw_replay_stats;
  stats.texture_uploads_this_frame =
      g_plume_resources.tracked_texture_uploads_this_frame;
  stats.decoded_texture_uploads_this_frame =
      g_plume_resources.decoded_tracked_texture_uploads_this_frame;
  stats.raw_texture_uploads_this_frame =
      g_plume_resources.raw_tracked_texture_uploads_this_frame;
  stats.placeholder_texture_uploads_this_frame =
      g_plume_resources.placeholder_tracked_texture_uploads_this_frame;
  stats.pending_texture_uploads =
      g_plume_resources.pending_tracked_texture_uploads;
  stats.texture_uploads_total =
      g_plume_resources.tracked_texture_uploads_total;
  return stats;
}

void ShutdownPlumeResourceManager() {
  std::lock_guard<std::mutex> lock(g_plume_resources.mutex);
#if REFII_RENDERER_HAS_PLUME
  g_plume_resources.framebuffers.clear();
  g_plume_resources.retired_framebuffers.clear();
  g_plume_resources.pipelines.clear();
  g_plume_resources.present_blit_pipeline.reset();
  g_plume_resources.present_blit_pipeline_layout.reset();
  g_plume_resources.pipeline_layout.reset();
  g_plume_resources.texture_descriptor_sets[0].reset();
  g_plume_resources.texture_descriptor_sets[1].reset();
  g_plume_resources.texture_descriptor_sets[2].reset();
  g_plume_resources.sampler_descriptor_set.reset();
  g_plume_resources.present_blit_texture_sets.clear();
  g_plume_resources.present_blit_texture_set_index = 0;
  g_plume_resources.present_blit_sampler_set.reset();
  g_plume_resources.fable_samplers.clear();
  g_plume_resources.present_blit_sampler.reset();
  g_plume_resources.null_sampler.reset();
  g_plume_resources.null_texture_views[0].reset();
  g_plume_resources.null_texture_views[1].reset();
  g_plume_resources.null_texture_views[2].reset();
  g_plume_resources.null_textures[0].reset();
  g_plume_resources.null_textures[1].reset();
  g_plume_resources.null_textures[2].reset();
  g_plume_resources.null_texture_uploads[0].reset();
  g_plume_resources.null_texture_uploads[1].reset();
  g_plume_resources.null_texture_uploads[2].reset();
  if (g_plume_resources.frame_constant_slab_mapped &&
      g_plume_resources.frame_constant_slab) {
    g_plume_resources.frame_constant_slab->unmap();
  }
  g_plume_resources.frame_constant_slab_mapped = nullptr;
  g_plume_resources.frame_constant_slab.reset();
  g_plume_resources.frame_constant_slab_size = 0;
  g_plume_resources.frame_constant_slab_offset = 0;
  g_plume_resources.frame_constant_buffers.clear();
  g_plume_resources.quad_list_index_buffers.clear();
  g_plume_resources.depth_sample_copies.clear();
  g_plume_resources.fallback_depth_targets.clear();
  g_plume_resources.color_sample_copies.clear();
  ClearFableFrameLookupCacheLocked();
  ClearFableTextureLookupIndexLocked();
  g_plume_resources.default_vertex_input_buffer.reset();
  g_plume_resources.implicit_rectangle_vertex_buffer.reset();
  g_plume_resources.zero_constants.reset();
  g_plume_resources.private_texture_copy_pixel_shader.reset();
  g_plume_resources.rectangle_list_geometry_shader.reset();
  g_plume_resources.decoded_color_targets.clear();
  g_plume_resources.decoded_depth_targets.clear();
  g_plume_resources.decoded_color_target_keys.clear();
  g_plume_resources.decoded_depth_target_keys.clear();
  g_plume_resources.resources.clear();
  g_plume_resources.retired_resources.clear();
  g_plume_resources.resource_retire_epoch = 0;
  g_plume_resources.last_resource_sync_version = {};
  g_plume_resources.resource_sync_version_valid = false;
  g_plume_resources.logged_unmatched_render_targets.clear();
  g_plume_resources.logged_rejected_decoded_color_targets.clear();
  g_plume_resources.logged_rejected_decoded_depth_targets.clear();
  g_plume_resources.logged_present_copy_failures.clear();
  g_plume_resources.logged_vulkan_unsafe_translated_shaders.clear();
  g_plume_resources.logged_rectangle_shader_pairs.clear();
  g_plume_resources.logged_triangle_shader_pairs.clear();
  g_plume_resources.logged_texture_slot_shader_pairs.clear();
  g_plume_resources.present_blit_shader_format =
      plume::RenderShaderFormat::UNKNOWN;
  g_plume_resources.rectangle_list_shader_format =
      plume::RenderShaderFormat::UNKNOWN;
  g_plume_resources.private_texture_copy_shader_format =
      plume::RenderShaderFormat::UNKNOWN;
  g_plume_resources.logged_present_blit_ready = false;
  g_plume_resources.logged_rectangle_list_geometry_shader_ready = false;
  g_plume_resources.logged_private_texture_copy_pixel_shader_ready = false;
  g_plume_resources.logged_resolve_alias = false;
  g_plume_resources.logged_quad_list_sequential_order = false;
  g_plume_resources.logged_quad_list_strip_order = false;
  g_plume_resources.logged_quad_list_draws = 0;
  g_plume_resources.logged_implicit_rectangle_vertices = false;
  g_plume_resources.logged_rectangle_list_draws = 0;
  g_plume_resources.logged_triangle_list_draws = 0;
  g_plume_resources.logged_invalid_indexed_draws = 0;
  g_plume_resources.logged_video_ycrcb_draws = 0;
  g_plume_resources.logged_title_shader_draws = 0;
  g_plume_resources.logged_missing_vertex_buffer_draws = 0;
  g_plume_resources.logged_fable_pix_external_plane_bindings = 0;
#endif
  g_plume_resources.last_draw_replay_stats = {};
  g_plume_resources.logged_waiting_for_buffers = false;
  g_plume_resources.logged_waiting_for_declarations = false;
  g_plume_resources.logged_first_recorded_draw_bridge = false;
  g_plume_resources.logged_pipeline_layout_ready = false;
  g_plume_resources.logged_live_constants = false;
  g_plume_resources.logged_draw_constant_nonzero = false;
  g_plume_resources.fallback_textures_upload_pending = false;
  g_plume_resources.logged_fable_shared_constants = false;
  g_plume_resources.logged_vulkan_depth_sample_source = false;
  g_plume_resources.logged_vulkan_depth_sample_copy = false;
  g_plume_resources.logged_vulkan_fallback_depth_target = false;
  g_plume_resources.logged_vulkan_depth_export_unavailable = false;
  g_plume_resources.logged_vulkan_depth_export_skip = false;
  g_plume_resources.logged_vulkan_depth_export_details = false;
  g_plume_resources.logged_vulkan_target_feedback_skip = false;
  g_plume_resources.logged_vulkan_unsafe_translated_shader_skip = false;
  g_plume_resources.logged_fallback_texture_upload = false;
  g_plume_resources.logged_tracked_texture_upload = false;
  g_plume_resources.logged_fable_clears = 0;
  g_plume_resources.logged_present_target_summaries = 0;
  g_plume_resources.logged_present_copy_summaries = 0;
  g_plume_resources.logged_present_target = 0;
  g_plume_resources.logged_present_target_width = 0;
  g_plume_resources.logged_present_target_height = 0;
  g_plume_resources.logged_present_copy_target = 0;
  g_plume_resources.logged_present_copy_serial = 0;
  g_plume_resources.preferred_present_render_target = 0;
  g_plume_resources.preferred_present_width = 0;
  g_plume_resources.preferred_present_height = 0;
  g_plume_resources.tracked_texture_uploads_this_frame = 0;
  g_plume_resources.decoded_tracked_texture_uploads_this_frame = 0;
  g_plume_resources.raw_tracked_texture_uploads_this_frame = 0;
  g_plume_resources.placeholder_tracked_texture_uploads_this_frame = 0;
  g_plume_resources.pending_tracked_texture_uploads = 0;
  g_plume_resources.tracked_texture_uploads_total = 0;
}

}  // namespace refii::renderer
