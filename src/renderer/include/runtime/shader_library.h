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
  uint32_t guest_bytecode_size = 0;
  uint64_t guest_bytecode_hash = 0;
  uint32_t guest_payload_size = 0;
  uint64_t guest_payload_hash = 0;
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
std::vector<RendererShaderLibraryEntry> GetRendererShaderLibraryEntries();
bool FindRendererShaderLibraryEntryById(uint32_t id,
                                        RendererShaderLibraryEntry& entry);
bool FindRendererShaderLibraryEntryByGuestHash(
    uint64_t guest_bytecode_hash, RendererShaderLibraryEntry& entry);

}  // namespace refii::renderer
