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
#include <unordered_map>
#include <unordered_set>
#include <vector>

#include <rex/logging.h>

namespace refii::renderer {
namespace {

struct RendererShaderLibraryState {
  std::mutex mutex;
  RendererShaderLibrarySnapshot snapshot;
  std::vector<RendererShaderLibraryEntry> entries;
  // Post-patch guest hash -> canonical guest hash, registered by the hook
  // observer when a normalized (patch-slots-masked) hash proves an in-place
  // patched VS is the same program as an extraction-time entry. Keyed by
  // content hash, so it deliberately survives RefreshRendererShaderLibrary.
  std::unordered_map<uint64_t, uint64_t> guest_hash_aliases;
  bool logged = false;
};

RendererShaderLibraryState g_shader_library;

const char* RendererGraphicsApiName(RendererGraphicsApi api) {
  switch (api) {
    case RendererGraphicsApi::kVulkan:
      return "vulkan";
    case RendererGraphicsApi::kD3D12:
      return "d3d12";
    case RendererGraphicsApi::kMetal:
      return "metal";
  }

  return "unknown";
}

std::string ShaderRootForApi(RendererGraphicsApi api) {
  switch (api) {
    case RendererGraphicsApi::kVulkan:
      return ConfiguredRendererVulkanShaderRoot();
    case RendererGraphicsApi::kD3D12:
      return ConfiguredRendererD3D12ShaderRoot();
    case RendererGraphicsApi::kMetal:
      return ConfiguredRendererMetalShaderRoot();
  }

  return {};
}

std::filesystem::path GuestShaderBlobRootForShaderRoot(
    const std::filesystem::path& shader_root) {
  return shader_root.parent_path() / "blobs" / "shaders";
}

std::filesystem::path ShaderArtifactRootForApi(RendererGraphicsApi api) {
  const std::string root = ShaderRootForApi(api);
  if (root.empty()) {
    return {};
  }
  return std::filesystem::path(root) / "shaders";
}

std::string ExistingPathString(const std::filesystem::path& path) {
  std::error_code ec;
  return !path.empty() && std::filesystem::is_regular_file(path, ec) && !ec
             ? path.string()
             : std::string();
}

void PopulateShaderBackendArtifactPaths(RendererShaderLibraryEntry& entry) {
  if (entry.name.empty()) {
    return;
  }

  const std::filesystem::path vulkan_root =
      ShaderArtifactRootForApi(RendererGraphicsApi::kVulkan);
  const std::filesystem::path d3d12_root =
      ShaderArtifactRootForApi(RendererGraphicsApi::kD3D12);
  const std::filesystem::path metal_root =
      ShaderArtifactRootForApi(RendererGraphicsApi::kMetal);

  entry.vulkan_binary_path =
      ExistingPathString(vulkan_root / (entry.name + ".vulkan.bin"));
  entry.vulkan_hlsl_path =
      ExistingPathString(vulkan_root / (entry.name + ".vulkan.bin.hlsl"));
  entry.d3d12_binary_path =
      ExistingPathString(d3d12_root / (entry.name + ".d3d12.bin"));
  entry.d3d12_hlsl_path =
      ExistingPathString(d3d12_root / (entry.name + ".d3d12.bin.hlsl"));
  entry.metal_binary_path =
      ExistingPathString(metal_root / (entry.name + ".metallib"));
  entry.metal_source_path =
      ExistingPathString(metal_root / (entry.name + ".metal"));
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
  bool changed = false;
  do {
    changed = false;
    const std::string before = filename;
    filename = RemoveSuffix(filename, ".hlsl");
    filename = RemoveSuffix(filename, ".bin");
    filename = RemoveSuffix(filename, ".metallib");
    filename = RemoveSuffix(filename, ".metal");
    filename = RemoveSuffix(filename, ".vulkan");
    filename = RemoveSuffix(filename, ".d3d12");
    changed = filename != before;
  } while (changed);
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

bool ReadFileText(const std::filesystem::path& path, std::string& text) {
  std::vector<uint8_t> bytes;
  if (!ReadFileBytes(path, bytes)) {
    text.clear();
    return false;
  }
  text.assign(reinterpret_cast<const char*>(bytes.data()), bytes.size());
  return true;
}

std::string_view TrimAscii(std::string_view value) {
  while (!value.empty() &&
         std::isspace(static_cast<unsigned char>(value.front()))) {
    value.remove_prefix(1);
  }
  while (!value.empty() &&
         std::isspace(static_cast<unsigned char>(value.back()))) {
    value.remove_suffix(1);
  }
  return value;
}

std::string_view FirstToken(std::string_view value) {
  value = TrimAscii(value);
  size_t token_end = 0;
  while (token_end < value.size() &&
         !std::isspace(static_cast<unsigned char>(value[token_end]))) {
    ++token_end;
  }
  return value.substr(0, token_end);
}

bool IsDescriptorIndexSymbol(std::string_view symbol) {
  return symbol.find("DescriptorIndex") != std::string_view::npos &&
         (symbol.find("_Texture") != std::string_view::npos ||
          symbol.find("_Sampler") != std::string_view::npos);
}

std::optional<uint32_t> ParseDirectSlotPrefix(std::string_view symbol) {
  if (symbol.size() < 3u || symbol[0] != 's' ||
      !std::isdigit(static_cast<unsigned char>(symbol[1]))) {
    return std::nullopt;
  }
  uint32_t slot = 0;
  size_t cursor = 1u;
  while (cursor < symbol.size() &&
         std::isdigit(static_cast<unsigned char>(symbol[cursor]))) {
    slot = slot * 10u + static_cast<uint32_t>(symbol[cursor] - '0');
    ++cursor;
  }
  if (slot >= 32u || cursor >= symbol.size() || symbol[cursor] != '_') {
    return std::nullopt;
  }
  return slot;
}

std::vector<std::string_view> ExtractCallArguments(std::string_view text,
                                                   size_t open_paren) {
  std::vector<std::string_view> arguments;
  if (open_paren >= text.size() || text[open_paren] != '(') {
    return arguments;
  }

  size_t argument_begin = open_paren + 1u;
  uint32_t depth = 0;
  for (size_t cursor = open_paren + 1u; cursor < text.size(); ++cursor) {
    const char ch = text[cursor];
    if (ch == '(') {
      ++depth;
    } else if (ch == ')') {
      if (depth == 0) {
        arguments.push_back(
            TrimAscii(text.substr(argument_begin, cursor - argument_begin)));
        return arguments;
      }
      --depth;
    } else if (ch == ',' && depth == 0) {
      arguments.push_back(
          TrimAscii(text.substr(argument_begin, cursor - argument_begin)));
      argument_begin = cursor + 1u;
    }
  }
  return arguments;
}

void AddDescriptorSymbolsFromCalls(std::string_view text,
                                   std::string_view function_name,
                                   std::unordered_set<std::string>& symbols) {
  size_t cursor = 0;
  while ((cursor = text.find(function_name, cursor)) != std::string_view::npos) {
    const size_t name_end = cursor + function_name.size();
    if (cursor > 0 &&
        (std::isalnum(static_cast<unsigned char>(text[cursor - 1u])) ||
         text[cursor - 1u] == '_')) {
      cursor = name_end;
      continue;
    }
    if (name_end < text.size() &&
        (std::isalnum(static_cast<unsigned char>(text[name_end])) ||
         text[name_end] == '_')) {
      cursor = name_end;
      continue;
    }

    size_t open_paren = name_end;
    while (open_paren < text.size() &&
           std::isspace(static_cast<unsigned char>(text[open_paren]))) {
      ++open_paren;
    }
    const std::vector<std::string_view> arguments =
        ExtractCallArguments(text, open_paren);
    const size_t descriptor_argument_count =
        function_name == "getPixelCoord" ? 1u : 2u;
    for (size_t i = 0;
         i < std::min(arguments.size(), descriptor_argument_count); ++i) {
      const std::string_view symbol = FirstToken(arguments[i]);
      if (IsDescriptorIndexSymbol(symbol)) {
        symbols.insert(std::string(symbol));
      }
    }
    cursor = name_end;
  }
}

std::unordered_set<std::string> CollectUsedDescriptorIndexSymbols(
    const std::string& text) {
  std::unordered_set<std::string> symbols;
  AddDescriptorSymbolsFromCalls(text, "tfetch1D", symbols);
  AddDescriptorSymbolsFromCalls(text, "tfetch2D", symbols);
  AddDescriptorSymbolsFromCalls(text, "tfetch2DBicubic", symbols);
  AddDescriptorSymbolsFromCalls(text, "tfetch3D", symbols);
  AddDescriptorSymbolsFromCalls(text, "tfetchCube", symbols);
  AddDescriptorSymbolsFromCalls(text, "getWeights2D", symbols);
  AddDescriptorSymbolsFromCalls(text, "getPixelCoord", symbols);
  return symbols;
}

std::optional<uint32_t> ParseSharedConstantOffset(const std::string& line) {
  constexpr const char* kNeedle = "SharedConstants + ";
  const size_t offset_begin = line.find(kNeedle);
  if (offset_begin == std::string::npos) {
    return std::nullopt;
  }
  size_t cursor = offset_begin + std::char_traits<char>::length(kNeedle);
  while (cursor < line.size() &&
         std::isspace(static_cast<unsigned char>(line[cursor]))) {
    ++cursor;
  }
  uint32_t value = 0;
  bool parsed_digit = false;
  while (cursor < line.size() &&
         std::isdigit(static_cast<unsigned char>(line[cursor]))) {
    parsed_digit = true;
    value = value * 10u + static_cast<uint32_t>(line[cursor] - '0');
    ++cursor;
  }
  if (!parsed_digit) {
    return std::nullopt;
  }
  return value;
}

uint32_t ParseHlslTextureSlotMask(const std::filesystem::path& path) {
  std::string text;
  if (!ReadFileText(path, text)) {
    return 0;
  }

  std::unordered_map<std::string, uint32_t> symbol_slots;
  std::vector<std::pair<std::string, std::string>> aliases;
  size_t line_begin = 0;
  while (line_begin < text.size()) {
    const size_t line_end = text.find('\n', line_begin);
    const size_t line_size =
        (line_end == std::string::npos ? text.size() : line_end) - line_begin;
    const std::string line = text.substr(line_begin, line_size);
    line_begin =
        line_end == std::string::npos ? text.size() : line_end + 1u;

    constexpr const char* kDefine = "#define ";
    if (line.rfind(kDefine, 0) != 0 ||
        line.find("DescriptorIndex") == std::string::npos) {
      continue;
    }

    size_t symbol_begin = std::char_traits<char>::length(kDefine);
    while (symbol_begin < line.size() &&
           std::isspace(static_cast<unsigned char>(line[symbol_begin]))) {
      ++symbol_begin;
    }
    size_t symbol_end = symbol_begin;
    while (symbol_end < line.size() &&
           !std::isspace(static_cast<unsigned char>(line[symbol_end]))) {
      ++symbol_end;
    }
    if (symbol_end <= symbol_begin) {
      continue;
    }

    const std::string symbol =
        line.substr(symbol_begin, symbol_end - symbol_begin);
    if (!IsDescriptorIndexSymbol(symbol)) {
      continue;
    }

    if (const std::optional<uint32_t> direct_slot =
            ParseDirectSlotPrefix(symbol)) {
      symbol_slots[symbol] = *direct_slot;
    }

    const std::string_view replacement = TrimAscii(std::string_view(line).substr(
        symbol_end));
    if (line.find("RawBufferLoad<uint>") != std::string::npos) {
      const std::optional<uint32_t> offset = ParseSharedConstantOffset(line);
      if (!offset) {
        continue;
      }
      const uint32_t slot = (*offset % 128u) / 4u;
      if (slot >= 32u || (*offset % 4u) != 0u) {
        continue;
      }
      symbol_slots[symbol] = slot;
    } else {
      const std::string_view aliased_symbol = FirstToken(replacement);
      if (IsDescriptorIndexSymbol(aliased_symbol)) {
        aliases.emplace_back(symbol, std::string(aliased_symbol));
      }
    }
  }

  bool changed = true;
  while (changed) {
    changed = false;
    for (const auto& [symbol, aliased_symbol] : aliases) {
      if (symbol_slots.find(symbol) != symbol_slots.end()) {
        continue;
      }
      const auto aliased_it = symbol_slots.find(aliased_symbol);
      if (aliased_it == symbol_slots.end()) {
        continue;
      }
      symbol_slots[symbol] = aliased_it->second;
      changed = true;
    }
  }

  const std::unordered_set<std::string> used_symbols =
      CollectUsedDescriptorIndexSymbols(text);
  uint32_t mask = 0;
  for (const auto& [symbol, slot] : symbol_slots) {
    if (used_symbols.find(symbol) != used_symbols.end()) {
      mask |= 1u << slot;
    }
  }
  return mask;
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
    if (extension == ".bin" || extension == ".metallib") {
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
    } else if (extension == ".hlsl" || extension == ".metal") {
      ++snapshot.hlsl_source_count;
      const std::string key = NormalizeShaderArtifactKey(filename);
      auto& mutable_entry = grouped_entries[key];
      mutable_entry.has_hlsl = true;
      mutable_entry.entry.hlsl_path = path.string();
      mutable_entry.entry.name = key;
      if (extension == ".hlsl") {
        const uint32_t texture_slot_mask = ParseHlslTextureSlotMask(path);
        mutable_entry.entry.texture_slot_mask = texture_slot_mask;
        mutable_entry.entry.texture_slot_mask_valid = texture_slot_mask != 0;
      }
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
    PopulateShaderBackendArtifactPaths(mutable_entry.entry);
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
  // to generated shader entries built from the same inline executable payload.
  constexpr InlineShaderAlias kFableInlineShaderAliases[] = {
      {0x4724831317AA37A8ull, 152u},  // D3D::PixRenderVideoFrame VS
      {0x69E9772C76BD6253ull, 9000u}, // D3D::PixRenderVideoFrame PS
      {0x85922739383C71C8ull, 87u},   // D3D::BinkMovie textured quad VS
      // D3D::BinkMovie PS. Targets the 9000 pseudo-entry, NOT a blob index.
      //
      // This was `180u` and broke video on 2026-07-26. Blob 180 used to be
      // named SHADER_BINK_MOVIE, and the translator installs a hand-written YUV
      // composer for it via an exact NAME LITERAL comparison
      // (refii_shader_xenos_translator_main.cpp:602/604,
      // `options.name == "SHADER_BINK_MOVIE"`). When the SBK extractor's naming
      // was corrected — 563 of 588 blob names had been wrong, bound positionally
      // instead of by payloadId — blob 180 became
      // PSHADER_LANDSCAPEMATERIAL_BLT7_GLT1_MIST0 and the Bink names moved to
      // 171/172. The name literal then matched nothing, so 180 fell through to a
      // plain XenosRecomp translation (2788 -> 30404 bytes, no YUV matrix) and
      // every Bink compose draw ran a landscape material shader.
      //
      // 9000 is a purpose-built pseudo-entry for an inline PM4 payload that has
      // no sbk entry of its own, so it is immune to blob renumbering and
      // renaming. Verified equivalent: 9000_pshader_fable_pixrender_video.vulkan.bin
      // is md5-identical to the known-good 0180_shader_bink_movie.vulkan.bin.
      // The sibling alias above was already moved here for the same reason.
      //
      // The name-literal gate in the translator is still fragile and should be
      // keyed on payload content/hash rather than a name — but fixing it alone
      // would place the composer at 171/172 while this alias pointed at 180, so
      // the two must be changed together if that is ever done.
      {0x5DBB593C197FF7EEull, 9000u}, // D3D::BinkMovie PS
      {0x9CEFC0421AC2A68Cull, 137u},  // D3D::PrivateTextureRectangleDraw PS
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

bool FindRendererShaderLibraryEntryByGuestHashLocked(
    uint64_t guest_bytecode_hash, RendererShaderLibraryEntry& entry) {
  if (FindFableInlineShaderAliasLocked(guest_bytecode_hash, entry)) {
    return true;
  }

  const auto it = std::find_if(
      g_shader_library.entries.begin(), g_shader_library.entries.end(),
      [guest_bytecode_hash](const RendererShaderLibraryEntry& candidate) {
        return (candidate.guest_bytecode_hash == guest_bytecode_hash ||
                candidate.guest_payload_hash == guest_bytecode_hash) &&
               !candidate.binary_path.empty();
      });
  if (it == g_shader_library.entries.end()) {
    return false;
  }

  entry = *it;
  return true;
}

bool FindRendererShaderLibraryEntryByGuestHash(
    uint64_t guest_bytecode_hash, RendererShaderLibraryEntry& entry) {
  if (guest_bytecode_hash == 0) {
    return false;
  }

  std::lock_guard<std::mutex> lock(g_shader_library.mutex);
  if (FindRendererShaderLibraryEntryByGuestHashLocked(guest_bytecode_hash,
                                                      entry)) {
    return true;
  }

  const auto alias_it =
      g_shader_library.guest_hash_aliases.find(guest_bytecode_hash);
  return alias_it != g_shader_library.guest_hash_aliases.end() &&
         FindRendererShaderLibraryEntryByGuestHashLocked(alias_it->second,
                                                         entry);
}

void RegisterRendererShaderGuestHashAlias(uint64_t guest_bytecode_hash,
                                          uint64_t canonical_guest_hash) {
  if (guest_bytecode_hash == 0 || canonical_guest_hash == 0 ||
      guest_bytecode_hash == canonical_guest_hash) {
    return;
  }

  std::lock_guard<std::mutex> lock(g_shader_library.mutex);
  g_shader_library.guest_hash_aliases.emplace(guest_bytecode_hash,
                                              canonical_guest_hash);
}

}  // namespace refii::renderer
