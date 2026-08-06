#pragma once

#include "runtime/cvars.h"

#include <cstdint>
#include <string>
#include <vector>

namespace refii::renderer {

enum class RendererShaderStage {
  kUnknown,
  kVertex,
  kPixel,
  kCombined,
};

struct RendererShaderLibraryEntry {
  uint32_t id = 0;
  RendererShaderStage stage = RendererShaderStage::kUnknown;
  std::string name;
  std::string binary_path;
  std::string hlsl_path;
  std::string guest_blob_path;
  std::string vulkan_binary_path;
  std::string vulkan_hlsl_path;
  std::string d3d12_binary_path;
  std::string d3d12_hlsl_path;
  std::string metal_binary_path;
  std::string metal_source_path;
  uint32_t guest_bytecode_size = 0;
  uint64_t guest_bytecode_hash = 0;
  uint32_t guest_payload_size = 0;
  uint64_t guest_payload_hash = 0;
  bool texture_slot_mask_valid = false;
  uint32_t texture_slot_mask = 0;
};

struct RendererShaderLibrarySnapshot {
  RendererGraphicsApi api = RendererGraphicsApi::kVulkan;
  std::string root;
  bool root_exists = false;
  uint32_t unique_shader_count = 0;
  uint32_t vertex_shader_count = 0;
  uint32_t pixel_shader_count = 0;
  uint32_t combined_shader_count = 0;
  uint32_t missing_binary_count = 0;
  uint32_t compiled_binary_count = 0;
  uint32_t hlsl_source_count = 0;
  uint32_t guest_blob_count = 0;
  uint32_t log_file_count = 0;
};

void RefreshRendererShaderLibrary();
RendererShaderLibrarySnapshot GetRendererShaderLibrarySnapshot();
bool FindRendererShaderLibraryEntryById(uint32_t id,
                                        RendererShaderLibraryEntry& entry);
bool FindRendererShaderLibraryEntryByGuestHash(
    uint64_t guest_bytecode_hash, RendererShaderLibraryEntry& entry);
// Content-hash translation cache for in-place VS microcode patching
// (D3D_PatchVertexShaderToMatchVertexDeclaration @0x821D3570 rewrites vfetch /
// export instruction dwords inside the SAME allocation, then reloads it via
// IM_LOAD 0xC0012B00). A post-patch guest hash matches no extraction-time
// entry; the observer proves the variant equal-modulo-patch-slots via a
// normalized (patch-slots-masked) hash and registers the association here so
// every later FindRendererShaderLibraryEntryByGuestHash on the patched hash
// resolves to the canonical translation. Survives RefreshRendererShaderLibrary
// (keys are content hashes, not entry indices).
void RegisterRendererShaderGuestHashAlias(uint64_t guest_bytecode_hash,
                                          uint64_t canonical_guest_hash);

}  // namespace refii::renderer
