#include "runtime/shader_library.h"

#include "runtime/cvars.h"

#include <algorithm>
#include <cctype>
#include <filesystem>
#include <fstream>
#include <map>
#include <mutex>
#include <optional>
#include <string>
#include <vector>

#include <rex/logging.h>

namespace refii::renderer {
namespace {

struct RendererShaderLibraryState {
  std::mutex mutex;
  RendererShaderLibrarySnapshot snapshot;
  std::vector<RendererShaderLibraryEntry> entries;
  bool logged = false;
};

RendererShaderLibraryState g_shader_library;

const char* RendererGraphicsApiName(RendererGraphicsApi api) {
  switch (api) {
    case RendererGraphicsApi::kVulkan:
      return "vulkan";
    case RendererGraphicsApi::kD3D12:
      return "d3d12";
  }

  return "unknown";
}

std::string ShaderRootForApi(RendererGraphicsApi api) {
  switch (api) {
    case RendererGraphicsApi::kVulkan:
      return ConfiguredRendererVulkanShaderRoot();
    case RendererGraphicsApi::kD3D12:
      return ConfiguredRendererD3D12ShaderRoot();
  }

  return {};
}

std::filesystem::path GuestShaderBlobRootForShaderRoot(
    const std::filesystem::path& shader_root) {
  return shader_root.parent_path() / "blobs" / "shaders";
}

bool EndsWith(const std::string& value, const std::string& suffix) {
  return value.size() >= suffix.size() &&
         value.compare(value.size() - suffix.size(), suffix.size(), suffix) == 0;
}

std::string RemoveSuffix(const std::string& value, const std::string& suffix) {
  if (!EndsWith(value, suffix)) {
    return value;
  }
  return value.substr(0, value.size() - suffix.size());
}

std::string NormalizeShaderArtifactKey(std::string filename) {
  filename = RemoveSuffix(filename, ".hlsl");
  filename = RemoveSuffix(filename, ".bin");
  filename = RemoveSuffix(filename, ".vulkan");
  filename = RemoveSuffix(filename, ".d3d12");
  return filename;
}

uint64_t HashBytesFnv1a64(const uint8_t* data, size_t size) {
  uint64_t hash = 14695981039346656037ull;
  for (size_t i = 0; i < size; ++i) {
    hash ^= data[i];
    hash *= 1099511628211ull;
  }
  return hash;
}

uint32_t LoadBigEndianU32(const uint8_t* data) {
  return (static_cast<uint32_t>(data[0]) << 24) |
         (static_cast<uint32_t>(data[1]) << 16) |
         (static_cast<uint32_t>(data[2]) << 8) |
         static_cast<uint32_t>(data[3]);
}

bool TryGetXenosPayloadRange(const std::vector<uint8_t>& bytes,
                             uint32_t& payload_offset,
                             uint32_t& payload_size) {
  if (bytes.size() < 0x24) {
    return false;
  }

  const uint32_t flags = LoadBigEndianU32(bytes.data() + 0x00);
  if ((flags & 0xFFFFFF00u) != 0x102A1100u) {
    return false;
  }

  const uint32_t virtual_size = LoadBigEndianU32(bytes.data() + 0x04);
  const uint32_t physical_size = LoadBigEndianU32(bytes.data() + 0x08);
  if (virtual_size == 0 || physical_size == 0 ||
      virtual_size > bytes.size() ||
      physical_size > bytes.size() - virtual_size) {
    return false;
  }

  payload_offset = virtual_size;
  payload_size = physical_size;
  return true;
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

std::optional<uint32_t> ParseShaderId(const std::string& filename) {
  if (filename.size() < 4) {
    return std::nullopt;
  }

  uint32_t id = 0;
  for (size_t i = 0; i < 4; ++i) {
    const unsigned char ch = static_cast<unsigned char>(filename[i]);
    if (!std::isdigit(ch)) {
      return std::nullopt;
    }
    id = id * 10 + static_cast<uint32_t>(filename[i] - '0');
  }
  return id;
}

RendererShaderStage ClassifyShaderStage(const std::string& filename) {
  const size_t name_start = filename.find('_');
  const std::string name =
      name_start == std::string::npos ? filename : filename.substr(name_start + 1);
  if (name.rfind("vshader_", 0) == 0 ||
      name.find("_vertex_shader") != std::string::npos) {
    return RendererShaderStage::kVertex;
  }
  if (name.rfind("pshader_", 0) == 0 ||
      name.find("_pixel_shader") != std::string::npos) {
    return RendererShaderStage::kPixel;
  }
  if (name.rfind("shader_", 0) == 0) {
    return RendererShaderStage::kCombined;
  }
  return RendererShaderStage::kUnknown;
}

const char* RendererShaderStageName(RendererShaderStage stage) {
  switch (stage) {
    case RendererShaderStage::kUnknown:
      return "unknown";
    case RendererShaderStage::kVertex:
      return "vertex";
    case RendererShaderStage::kPixel:
      return "pixel";
    case RendererShaderStage::kCombined:
      return "combined";
  }

  return "unknown";
}

struct MutableShaderEntry {
  RendererShaderLibraryEntry entry;
  bool has_binary = false;
  bool has_hlsl = false;
};

RendererShaderLibrarySnapshot ScanShaderRoot(
    RendererGraphicsApi api, std::vector<RendererShaderLibraryEntry>& entries) {
  RendererShaderLibrarySnapshot snapshot;
  snapshot.api = api;
  snapshot.root = ShaderRootForApi(api);
  if (snapshot.root.empty()) {
    return snapshot;
  }

  std::error_code ec;
  const std::filesystem::path root(snapshot.root);
  snapshot.root_exists = std::filesystem::is_directory(root, ec);
  if (!snapshot.root_exists || ec) {
    return snapshot;
  }

  std::map<std::string, MutableShaderEntry> grouped_entries;

  for (std::filesystem::recursive_directory_iterator it(
           root, std::filesystem::directory_options::skip_permission_denied,
           ec);
       it != std::filesystem::recursive_directory_iterator(); it.increment(ec)) {
    if (ec) {
      ec.clear();
      continue;
    }
    if (!it->is_regular_file(ec) || ec) {
      ec.clear();
      continue;
    }

    const std::filesystem::path& path = it->path();
    const std::string filename = path.filename().string();
    const std::string extension = path.extension().string();
    if (extension == ".bin") {
      ++snapshot.compiled_binary_count;
      const std::string key = NormalizeShaderArtifactKey(filename);
      auto& mutable_entry = grouped_entries[key];
      mutable_entry.has_binary = true;
      mutable_entry.entry.binary_path = path.string();
      mutable_entry.entry.name = key;
      if (const std::optional<uint32_t> id = ParseShaderId(filename)) {
        mutable_entry.entry.id = *id;
      }
      mutable_entry.entry.stage = ClassifyShaderStage(filename);
    } else if (extension == ".hlsl") {
      ++snapshot.hlsl_source_count;
      const std::string key = NormalizeShaderArtifactKey(filename);
      auto& mutable_entry = grouped_entries[key];
      mutable_entry.has_hlsl = true;
      mutable_entry.entry.hlsl_path = path.string();
      mutable_entry.entry.name = key;
      if (const std::optional<uint32_t> id = ParseShaderId(filename)) {
        mutable_entry.entry.id = *id;
      }
      mutable_entry.entry.stage = ClassifyShaderStage(filename);
    } else if (EndsWith(filename, ".log") || extension == ".txt") {
      ++snapshot.log_file_count;
    }
  }

  const std::filesystem::path guest_blob_root =
      GuestShaderBlobRootForShaderRoot(root);
  if (std::filesystem::is_directory(guest_blob_root, ec) && !ec) {
    for (std::filesystem::directory_iterator it(
             guest_blob_root,
             std::filesystem::directory_options::skip_permission_denied, ec);
         it != std::filesystem::directory_iterator(); it.increment(ec)) {
      if (ec) {
        ec.clear();
        continue;
      }
      if (!it->is_regular_file(ec) || ec) {
        ec.clear();
        continue;
      }
      const std::filesystem::path& path = it->path();
      const std::string filename = path.filename().string();
      if (path.extension().string() != ".bin") {
        continue;
      }

      std::vector<uint8_t> bytes;
      if (!ReadFileBytes(path, bytes)) {
        continue;
      }

      const std::string key = NormalizeShaderArtifactKey(filename);
      auto& mutable_entry = grouped_entries[key];
      mutable_entry.entry.name = key;
      mutable_entry.entry.guest_blob_path = path.string();
      mutable_entry.entry.guest_bytecode_size =
          static_cast<uint32_t>(bytes.size());
      mutable_entry.entry.guest_bytecode_hash =
          HashBytesFnv1a64(bytes.data(), bytes.size());
      uint32_t payload_offset = 0;
      uint32_t payload_size = 0;
      if (TryGetXenosPayloadRange(bytes, payload_offset, payload_size)) {
        mutable_entry.entry.guest_payload_size = payload_size;
        mutable_entry.entry.guest_payload_hash =
            HashBytesFnv1a64(bytes.data() + payload_offset, payload_size);
      }
      if (const std::optional<uint32_t> id = ParseShaderId(filename)) {
        mutable_entry.entry.id = *id;
      }
      if (mutable_entry.entry.stage == RendererShaderStage::kUnknown) {
        mutable_entry.entry.stage = ClassifyShaderStage(filename);
      }
      ++snapshot.guest_blob_count;
    }
  } else {
    ec.clear();
  }

  entries.clear();
  entries.reserve(grouped_entries.size());
  for (auto& [key, mutable_entry] : grouped_entries) {
    (void)key;
    if (!mutable_entry.has_binary) {
      ++snapshot.missing_binary_count;
    }
    if (mutable_entry.entry.id == 0 &&
        mutable_entry.entry.name.rfind("0000_", 0) != 0) {
      continue;
    }
    switch (mutable_entry.entry.stage) {
      case RendererShaderStage::kVertex:
        ++snapshot.vertex_shader_count;
        break;
      case RendererShaderStage::kPixel:
        ++snapshot.pixel_shader_count;
        break;
      case RendererShaderStage::kCombined:
        ++snapshot.combined_shader_count;
        break;
      case RendererShaderStage::kUnknown:
        break;
    }
    entries.push_back(std::move(mutable_entry.entry));
  }
  snapshot.unique_shader_count = static_cast<uint32_t>(entries.size());

  std::sort(entries.begin(), entries.end(),
            [](const RendererShaderLibraryEntry& lhs,
               const RendererShaderLibraryEntry& rhs) {
              if (lhs.id != rhs.id) {
                return lhs.id < rhs.id;
              }
              return lhs.name < rhs.name;
            });

  return snapshot;
}

}  // namespace

void RefreshRendererShaderLibrary() {
  const RendererGraphicsApi api = GetRendererGraphicsApi();
  std::vector<RendererShaderLibraryEntry> entries;
  RendererShaderLibrarySnapshot snapshot = ScanShaderRoot(api, entries);

  std::lock_guard<std::mutex> lock(g_shader_library.mutex);
  const bool changed =
      snapshot.api != g_shader_library.snapshot.api ||
      snapshot.root != g_shader_library.snapshot.root ||
      snapshot.root_exists != g_shader_library.snapshot.root_exists ||
      snapshot.compiled_binary_count !=
          g_shader_library.snapshot.compiled_binary_count ||
      snapshot.hlsl_source_count != g_shader_library.snapshot.hlsl_source_count ||
      snapshot.unique_shader_count != g_shader_library.snapshot.unique_shader_count;
  g_shader_library.snapshot = std::move(snapshot);
  g_shader_library.entries = std::move(entries);

  if (!g_shader_library.logged || changed) {
    if (g_shader_library.snapshot.root_exists) {
      REXLOG_INFO(
          "Refii renderer shader library {} root={} shaders={} "
          "vertex={} pixel={} combined={} binaries={} hlsl={} missing_bin={} "
          "guest_blobs={} logs={}",
          RendererGraphicsApiName(api), g_shader_library.snapshot.root,
          g_shader_library.snapshot.unique_shader_count,
          g_shader_library.snapshot.vertex_shader_count,
          g_shader_library.snapshot.pixel_shader_count,
          g_shader_library.snapshot.combined_shader_count,
          g_shader_library.snapshot.compiled_binary_count,
          g_shader_library.snapshot.hlsl_source_count,
          g_shader_library.snapshot.missing_binary_count,
          g_shader_library.snapshot.guest_blob_count,
          g_shader_library.snapshot.log_file_count);
    } else {
      REXLOG_WARN("Refii renderer shader library {} root is missing: {}",
                  RendererGraphicsApiName(api), g_shader_library.snapshot.root);
    }
    g_shader_library.logged = true;
  }
}

RendererShaderLibrarySnapshot GetRendererShaderLibrarySnapshot() {
  std::lock_guard<std::mutex> lock(g_shader_library.mutex);
  return g_shader_library.snapshot;
}

std::vector<RendererShaderLibraryEntry> GetRendererShaderLibraryEntries() {
  std::lock_guard<std::mutex> lock(g_shader_library.mutex);
  return g_shader_library.entries;
}

bool FindRendererShaderLibraryEntryById(uint32_t id,
                                        RendererShaderLibraryEntry& entry) {
  std::lock_guard<std::mutex> lock(g_shader_library.mutex);
  const auto it = std::find_if(
      g_shader_library.entries.begin(), g_shader_library.entries.end(),
      [id](const RendererShaderLibraryEntry& candidate) {
        return candidate.id == id && !candidate.binary_path.empty();
      });
  if (it == g_shader_library.entries.end()) {
    return false;
  }

  entry = *it;
  return true;
}

bool FindRendererShaderLibraryEntryByIdLocked(
    uint32_t id, RendererShaderLibraryEntry& entry) {
  const auto it = std::find_if(
      g_shader_library.entries.begin(), g_shader_library.entries.end(),
      [id](const RendererShaderLibraryEntry& candidate) {
        return candidate.id == id && !candidate.binary_path.empty();
      });
  if (it == g_shader_library.entries.end()) {
    return false;
  }

  entry = *it;
  return true;
}

bool FindFableInlineShaderAliasLocked(uint64_t guest_bytecode_hash,
                                      RendererShaderLibraryEntry& entry) {
  struct InlineShaderAlias {
    uint64_t guest_bytecode_hash;
    uint32_t generated_shader_id;
  };

  // Fable's PixRenderVideoFrame submits these shaders as inline PM4 payloads
  // from the executable instead of through Shaders.sbk. Keep the renderer
  // bound to generated XenosRecomp output by aliasing the exact payload hashes
  // to the extracted shader entries whose translated HLSL matches the observed
  // binding shape.
  constexpr InlineShaderAlias kFableInlineShaderAliases[] = {
      {0x4724831317AA37A8ull, 152u},  // D3D::PixRenderVideoFrame VS
      {0x69E9772C76BD6253ull, 171u},  // D3D::PixRenderVideoFrame PS
  };

  const auto alias_it = std::find_if(
      std::begin(kFableInlineShaderAliases),
      std::end(kFableInlineShaderAliases),
      [guest_bytecode_hash](const InlineShaderAlias& alias) {
        return alias.guest_bytecode_hash == guest_bytecode_hash;
      });
  if (alias_it == std::end(kFableInlineShaderAliases)) {
    return false;
  }

  return FindRendererShaderLibraryEntryByIdLocked(
      alias_it->generated_shader_id, entry);
}

bool FindRendererShaderLibraryEntryByGuestHash(
    uint64_t guest_bytecode_hash, RendererShaderLibraryEntry& entry) {
  if (guest_bytecode_hash == 0) {
    return false;
  }

  std::lock_guard<std::mutex> lock(g_shader_library.mutex);
  const auto it = std::find_if(
      g_shader_library.entries.begin(), g_shader_library.entries.end(),
      [guest_bytecode_hash](const RendererShaderLibraryEntry& candidate) {
        return (candidate.guest_bytecode_hash == guest_bytecode_hash ||
                candidate.guest_payload_hash == guest_bytecode_hash) &&
               !candidate.binary_path.empty();
      });
  if (it == g_shader_library.entries.end()) {
    return FindFableInlineShaderAliasLocked(guest_bytecode_hash, entry);
  }

  entry = *it;
  return true;
}

}  // namespace refii::renderer
