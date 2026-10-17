#include "shader/shader_sbk_pipeline.h"
#include "shader/refii_shader_xenos_translator.h"

#include "shader/sha256.h"

#include <algorithm>
#include <array>
#include <cctype>
#include <cstdint>
#include <cstring>
#include <cstdlib>
#include <exception>
#include <fstream>
#include <functional>
#include <iomanip>
#include <limits>
#include <optional>
#include <sstream>
#include <string>
#include <string_view>
#include <unordered_set>
#include <unordered_map>
#include <utility>
#include <vector>

#include <zlib.h>

namespace refii::shader {
namespace {

constexpr std::string_view kMagic = "ShaderBankFile";
// sub_82B83378 rejects the bank unless the version dword is exactly 3.
constexpr std::uint32_t kSupportedBankVersion = 3;
// sub_82B846B0 resolves a payload id as (chunk = id / 4, slot = id % 4); every
// chunk therefore begins with a base dword plus this many slot offsets.
constexpr std::uint32_t kPayloadsPerChunk = 4;
constexpr std::array<std::string_view, 2> kExpectedBankFiles = {
    "Shaders.sbk",
    "ShadersRelease.sbk",
};
constexpr std::size_t kFablePixRenderVideoFramePixelShaderBlobId = 9000;
constexpr std::string_view kFablePixRenderVideoFramePixelShaderName =
    "PSHADER_FABLE_PIXRENDER_VIDEO";
constexpr std::array<std::uint8_t, 36> kFablePixRenderVideoFramePixelShaderBlob = {
    0x00, 0x00, 0x00, 0x00, 0x10, 0x01, 0xc4, 0x00, 0x22, 0x00, 0x00, 0x00,
    0xc8, 0x0f, 0x80, 0x00, 0x02, 0x00, 0x00, 0x00, 0x20, 0x00, 0x00, 0x00,
    0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
};

// Authoritative .sbk record layout, decoded from the guest binary
// (default.xex, imagebase 0x82000000):
//
//   sub_82B83378  lhShader_LoadShaderBank  - header + section dispatch
//   sub_82B82AE8  section A (render-state groups) count + loop
//   sub_82B82310  section A record
//   sub_82B82C08  section B (shader-resource binding names)
//   sub_82B82EB8  section C (shader entries, chunk tables, zlib payload)
//   sub_82B90E40  section C per-entry record
//   sub_82B846B0  chunk inflate + slot seek (chunk = id/4, slot = id%4)
//   sub_82B91130  per-payload parse (const tables, masks, microcode)
//   sub_823B0A38  name hash (CRC-32 reflected, init 0, no final xor)
//
// A bank holds `entry_count` *bindings* but only `chunk_count * 4` distinct
// microcode payloads: many bindings legitimately alias one payload because
// Fable II folds shader permutations into boolean-constant branches.
struct ParsedEntry {
    std::size_t entry_index = 0;
    // Payload selector stored at guest shader-struct offset +8; consumed by
    // sub_82B846B0 as (chunk = id / 4, slot = id % 4). Also surfaced as
    // `entry_id` in the manifest for backwards compatibility.
    std::uint32_t entry_id = 0;
    // Stage byte at guest shader-struct offset +12 (sub_82B91130: 1 -> pixel,
    // 2 -> vertex).
    std::uint32_t stage_raw = 0;
    ShaderStage stage = ShaderStage::kUnknown;
    std::string entry_name;
    std::string entry_hash_hex;
};

struct ParsedBank {
    std::string bank_name;
    std::filesystem::path bank_path;
    std::vector<std::uint8_t> bytes;
    bool has_debug_names = false;
    std::vector<ParsedEntry> entries;
    // Section C tail: parallel per-chunk tables plus the zlib payload region.
    std::vector<std::uint32_t> chunk_decompressed_sizes;
    std::vector<std::uint32_t> chunk_compressed_offsets;
    std::size_t compressed_blob_base = 0;
    std::size_t compressed_blob_size = 0;
};

struct TranslateBlobRecord {
    std::string bank_name;
    std::size_t blob_index = 0;
    ShaderStage stage = ShaderStage::kUnknown;
    std::string name_hint;
    std::string sha256;
    std::filesystem::path input_path;
};

struct CompressedChunkHeader {
    std::uint32_t offset = 0;
    std::uint32_t compressed_size = 0;
};

struct XenosContainerCandidate {
    std::size_t offset = 0;
    std::uint32_t flags = 0;
    std::uint32_t virtual_size = 0;
    std::uint32_t physical_size = 0;
    std::uint32_t constant_table_offset = 0;
    std::uint32_t definition_table_offset = 0;
    std::uint32_t shader_offset = 0;
};

std::string EscapeTsv(std::string_view value) {
    std::string escaped;
    escaped.reserve(value.size());
    for (const char ch : value) {
        if (ch == '\t' || ch == '\n' || ch == '\r') {
            escaped.push_back(' ');
        } else {
            escaped.push_back(ch);
        }
    }
    return escaped;
}

std::vector<std::string> SplitTsvLine(std::string_view line) {
    std::vector<std::string> fields;
    std::size_t cursor = 0;
    while (cursor <= line.size()) {
        const std::size_t tab = line.find('\t', cursor);
        if (tab == std::string_view::npos) {
            fields.emplace_back(line.substr(cursor));
            break;
        }
        fields.emplace_back(line.substr(cursor, tab - cursor));
        cursor = tab + 1;
    }
    return fields;
}

std::string ToLowerAscii(std::string value) {
    for (char& ch : value) {
        ch = static_cast<char>(std::tolower(static_cast<unsigned char>(ch)));
    }
    return value;
}

std::string TrimAscii(std::string value) {
    std::size_t begin = 0;
    while (begin < value.size() &&
           std::isspace(static_cast<unsigned char>(value[begin])) != 0) {
        ++begin;
    }

    std::size_t end = value.size();
    while (end > begin &&
           std::isspace(static_cast<unsigned char>(value[end - 1])) != 0) {
        --end;
    }
    return value.substr(begin, end - begin);
}

std::vector<std::string> SplitCommaList(std::string_view text) {
    std::vector<std::string> fields;
    std::size_t cursor = 0;
    while (cursor <= text.size()) {
        const std::size_t comma = text.find(',', cursor);
        if (comma == std::string_view::npos) {
            fields.emplace_back(text.substr(cursor));
            break;
        }
        fields.emplace_back(text.substr(cursor, comma - cursor));
        cursor = comma + 1;
    }
    return fields;
}

// "godot" is on every platform: it is the classic renderer's material source, Godot's shading
// language straight from the recompiler, and it needs no backend compiler.
std::vector<std::string> BuildDefaultTranslateTargets() {
#if defined(__APPLE__)
    return {"vulkan", "metal", "godot"};
#elif defined(_WIN32)
    // Windows can run either backend, so it produces both.
    return {"vulkan", "d3d12", "godot"};
#else
    // BY DESIGN (project owner, 2026-07-26): Linux has no D3D12 backend — Plume
    // reports `Vulkan=1 Metal= D3D12=` on this host — so a DXIL artifact can
    // never be consumed here.  Translating it anyway doubles the work of every
    // shader rebuild to produce something the platform cannot load.
    //
    // The vendored DXC *can* emit signed DXIL on Linux (libdxil.so ships in
    // thirdparty/XenosRecomp/thirdparty/dxc-bin/lib/x64), and an earlier change
    // used that to argue for cross-producing the Windows artifact set from a
    // Linux run.  That argument was rejected: "capable of" is not "should".
    // Windows builds produce their own D3D12 artifacts.
    return {"vulkan", "godot"};
#endif
}

std::string JoinCsv(const std::vector<std::string>& values) {
    std::ostringstream stream;
    for (std::size_t index = 0; index < values.size(); ++index) {
        if (index != 0) {
            stream << ',';
        }
        stream << values[index];
    }
    return stream.str();
}

bool ResolveTranslateTargets(std::string_view requested_targets,
                             std::vector<std::string>* resolved_targets,
                             std::string* error) {
    if (resolved_targets == nullptr) {
        if (error != nullptr) {
            *error = "internal target resolution output missing";
        }
        return false;
    }

    resolved_targets->clear();
    const std::string normalized = ToLowerAscii(TrimAscii(std::string(requested_targets)));
    if (normalized.empty() || normalized == "auto") {
        *resolved_targets = BuildDefaultTranslateTargets();
        return true;
    }

    std::unordered_set<std::string> seen_targets;
    const std::vector<std::string> parts = SplitCommaList(normalized);
    for (std::string part : parts) {
        part = TrimAscii(part);
        if (part.empty()) {
            continue;
        }

        if (part != "vulkan" && part != "d3d12" && part != "metal" && part != "godot") {
            if (error != nullptr) {
                *error = "Unsupported compile target: " + part +
                         " (allowed: vulkan, d3d12, metal, godot, auto)";
            }
            return false;
        }

        if (!seen_targets.contains(part)) {
            seen_targets.insert(part);
            resolved_targets->push_back(part);
        }
    }

    if (resolved_targets->empty()) {
        if (error != nullptr) {
            *error = "No valid compile targets selected";
        }
        return false;
    }

    return true;
}

std::string SanitizeNameForFile(std::string_view name, std::size_t max_length = 80) {
    std::string output;
    output.reserve(std::min(name.size(), max_length));
    for (const char ch : name) {
        if (output.size() >= max_length) {
            break;
        }
        const bool safe = (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') ||
                          (ch >= '0' && ch <= '9') || ch == '_' || ch == '-' || ch == '.';
        output.push_back(safe ? ch : '_');
    }
    while (!output.empty() && (output.back() == '.' || output.back() == '_')) {
        output.pop_back();
    }
    if (output.empty()) {
        output = "unnamed";
    }
    return output;
}

std::string BuildBankOutputStem(std::string_view bank_name) {
    const std::string stem =
        ToLowerAscii(std::filesystem::path(std::string(bank_name)).stem().string());
    if (stem == "shadersrelease") {
        return "shaders";
    }
    return stem;
}

std::string BuildCompileTargetOutputStem(std::string_view target) {
    const std::string normalized = ToLowerAscii(std::string(target));
    if (normalized == "d3d12") {
        return "D3D12";
    }
    return normalized;
}

std::string FormatHex32(std::uint32_t value) {
    std::ostringstream stream;
    stream << std::hex << std::nouppercase << std::setfill('0') << std::setw(8) << value;
    return stream.str();
}

bool ReadBinaryFile(const std::filesystem::path& path,
                    std::vector<std::uint8_t>* bytes,
                    std::string* error) {
    if (bytes == nullptr) {
        if (error != nullptr) {
            *error = "output buffer missing";
        }
        return false;
    }

    std::ifstream file(path, std::ios::binary);
    if (!file) {
        if (error != nullptr) {
            *error = "Failed to open " + path.string();
        }
        return false;
    }

    file.seekg(0, std::ios::end);
    const std::streampos size = file.tellg();
    if (size < 0) {
        if (error != nullptr) {
            *error = "Failed to determine size of " + path.string();
        }
        return false;
    }

    file.seekg(0, std::ios::beg);
    bytes->resize(static_cast<std::size_t>(size));
    if (!bytes->empty()) {
        file.read(reinterpret_cast<char*>(bytes->data()),
                  static_cast<std::streamsize>(bytes->size()));
        if (!file) {
            if (error != nullptr) {
                *error = "Failed to read " + path.string();
            }
            return false;
        }
    }
    return true;
}

bool WriteBinaryFile(const std::filesystem::path& path,
                     const std::uint8_t* bytes,
                     std::size_t size,
                     std::string* error) {
    try {
        std::filesystem::create_directories(path.parent_path());
    } catch (const std::exception& exception) {
        if (error != nullptr) {
            *error = exception.what();
        }
        return false;
    }

    std::ofstream file(path, std::ios::binary | std::ios::trunc);
    if (!file) {
        if (error != nullptr) {
            *error = "Failed to open " + path.string() + " for writing";
        }
        return false;
    }

    if (size != 0) {
        file.write(reinterpret_cast<const char*>(bytes), static_cast<std::streamsize>(size));
        if (!file) {
            if (error != nullptr) {
                *error = "Failed to write " + path.string();
            }
            return false;
        }
    }

    return true;
}

bool WriteTextFile(const std::filesystem::path& path,
                   std::string_view text,
                   std::string* error) {
    try {
        std::filesystem::create_directories(path.parent_path());
    } catch (const std::exception& exception) {
        if (error != nullptr) {
            *error = exception.what();
        }
        return false;
    }

    std::ofstream file(path, std::ios::binary | std::ios::trunc);
    if (!file) {
        if (error != nullptr) {
            *error = "Failed to open " + path.string() + " for writing";
        }
        return false;
    }

    file.write(text.data(), static_cast<std::streamsize>(text.size()));
    if (!file) {
        if (error != nullptr) {
            *error = "Failed to write " + path.string();
        }
        return false;
    }
    return true;
}

bool PrepareTextFile(const std::filesystem::path& path, std::string* error) {
    try {
        std::filesystem::create_directories(path.parent_path());
    } catch (const std::exception& exception) {
        if (error != nullptr) {
            *error = exception.what();
        }
        return false;
    }

    std::ofstream file(path, std::ios::binary | std::ios::trunc);
    if (!file) {
        if (error != nullptr) {
            *error = "Failed to open " + path.string() + " for writing";
        }
        return false;
    }
    return true;
}

bool ReadTextFile(const std::filesystem::path& path,
                  std::string* text,
                  std::string* error) {
    if (text == nullptr) {
        if (error != nullptr) {
            *error = "text output buffer missing";
        }
        return false;
    }
    text->clear();

    std::ifstream file(path, std::ios::binary);
    if (!file) {
        if (error != nullptr) {
            *error = "Failed to open " + path.string();
        }
        return false;
    }

    std::ostringstream stream;
    stream << file.rdbuf();
    if (!file.good() && !file.eof()) {
        if (error != nullptr) {
            *error = "Failed to read " + path.string();
        }
        return false;
    }

    *text = stream.str();
    return true;
}

std::uint32_t ReadBigEndianU32(const std::vector<std::uint8_t>& bytes, std::size_t offset) {
    return (static_cast<std::uint32_t>(bytes[offset]) << 24) |
           (static_cast<std::uint32_t>(bytes[offset + 1]) << 16) |
           (static_cast<std::uint32_t>(bytes[offset + 2]) << 8) |
           static_cast<std::uint32_t>(bytes[offset + 3]);
}

// Sequential big-endian cursor over a bank image. Every multi-byte field in a
// ShaderBankFile is big-endian; the guest byte-swaps only when the runtime
// endianness flag at stream+24 is set (sub_82B82310 and friends), which never
// happens for the shipped Xbox 360 banks.
class BankReader {
public:
    BankReader(const std::vector<std::uint8_t>& bytes, std::size_t cursor)
        : bytes_(bytes), cursor_(cursor) {}

    std::size_t cursor() const { return cursor_; }
    bool failed() const { return failed_; }
    std::size_t remaining() const {
        return cursor_ <= bytes_.size() ? bytes_.size() - cursor_ : 0;
    }

    std::uint8_t U8() {
        if (remaining() < 1) {
            failed_ = true;
            return 0;
        }
        return bytes_[cursor_++];
    }

    std::uint32_t U32() {
        if (remaining() < 4) {
            failed_ = true;
            cursor_ = bytes_.size();
            return 0;
        }
        const std::uint32_t value = ReadBigEndianU32(bytes_, cursor_);
        cursor_ += 4;
        return value;
    }

    // sub_82A213F0: the stream's virtual ReadString appends bytes until it
    // consumes a NUL terminator.
    std::string CString() {
        std::string value;
        while (true) {
            if (remaining() < 1) {
                failed_ = true;
                return value;
            }
            const std::uint8_t byte = bytes_[cursor_++];
            if (byte == 0) {
                return value;
            }
            value.push_back(static_cast<char>(byte));
        }
    }

    bool Skip(std::size_t count) {
        if (remaining() < count) {
            failed_ = true;
            cursor_ = bytes_.size();
            return false;
        }
        cursor_ += count;
        return true;
    }

private:
    const std::vector<std::uint8_t>& bytes_;
    std::size_t cursor_ = 0;
    bool failed_ = false;
};

// sub_823B0A38: reflected CRC-32 (IEEE polynomial, table at 0x82098C40) with
// init 0 and no final xor, applied to "SHADER_RESOURCE_BINDING_" + name (the
// prefix is added by sub_82B82DC0 when the name does not already carry it).
std::uint32_t ComputeShaderBindingHash(std::string_view name) {
    static const std::array<std::uint32_t, 256> table = [] {
        std::array<std::uint32_t, 256> generated{};
        for (std::uint32_t index = 0; index < 256; ++index) {
            std::uint32_t value = index;
            for (int bit = 0; bit < 8; ++bit) {
                value = (value & 1u) != 0u ? (0xEDB88320u ^ (value >> 1))
                                           : (value >> 1);
            }
            generated[index] = value;
        }
        return generated;
    }();

    constexpr std::string_view kBindingPrefix = "SHADER_RESOURCE_BINDING_";
    std::string hashed;
    if (name.rfind(kBindingPrefix, 0) != 0) {
        hashed.assign(kBindingPrefix);
    }
    hashed.append(name);

    std::uint32_t crc = 0;
    for (const char ch : hashed) {
        crc = table[(crc ^ static_cast<std::uint8_t>(ch)) & 0xffu] ^ (crc >> 8);
    }
    return crc;
}

// The stage byte is authoritative: sub_82B91130 dispatches on the value stored
// at guest shader-struct offset +12, creating a vertex shader for 2 and a pixel
// shader for 1. Names are only a fallback, because a minority of entries use
// neither the VSHADER_ nor the PSHADER_ prefix (FACE_*, TRAILS_*, OUTLINE_*,
// DIFFUSEPS, ...).
ShaderStage InferShaderStage(std::uint32_t stage_raw, std::string_view name_hint) {
    const std::uint8_t stage_tag = static_cast<std::uint8_t>(stage_raw & 0xffu);
    if (stage_tag == 2) {
        return ShaderStage::kVertex;
    }
    if (stage_tag == 1) {
        return ShaderStage::kPixel;
    }

    if (!name_hint.empty()) {
        if (name_hint.rfind("VSHADER_", 0) == 0) {
            return ShaderStage::kVertex;
        }
        if (name_hint.rfind("PSHADER_", 0) == 0) {
            return ShaderStage::kPixel;
        }
    }
    return ShaderStage::kUnknown;
}

// Section A (sub_82B82AE8 / sub_82B82310): render-state groups. Nothing in the
// extraction pipeline consumes these, but they must be walked exactly to reach
// section B and C. Each record is nine u32 fields followed by a counted list of
// (NUL-terminated name, u32) pairs.
bool SkipRenderStateSection(BankReader* reader, std::string* error) {
    const std::uint32_t record_count = reader->U32();
    for (std::uint32_t index = 0; index < record_count; ++index) {
        if (!reader->Skip(9 * sizeof(std::uint32_t))) {
            break;
        }
        const std::uint32_t pair_count = reader->U32();
        for (std::uint32_t pair = 0; pair < pair_count && !reader->failed(); ++pair) {
            (void)reader->CString();
            (void)reader->U32();
        }
        if (reader->failed()) {
            break;
        }
    }

    if (reader->failed()) {
        if (error != nullptr) {
            *error = "Truncated render-state section";
        }
        return false;
    }
    return true;
}

// Section B (sub_82B82C08): the shader-resource binding name table.
bool SkipBindingNameSection(BankReader* reader, std::string* error) {
    const std::uint32_t name_count = reader->U32();
    for (std::uint32_t index = 0; index < name_count && !reader->failed(); ++index) {
        (void)reader->CString();
    }

    if (reader->failed()) {
        if (error != nullptr) {
            *error = "Truncated binding-name section";
        }
        return false;
    }
    return true;
}

// Section C (sub_82B82EB8): shader entry records, the per-chunk tables, and the
// zlib payload region.
bool ParseShaderSection(ParsedBank* bank, BankReader* reader, std::string* error) {
    const std::uint32_t entry_count = reader->U32();
    (void)reader->U32();  // stored into dword_834970F0; unused by extraction.

    bank->entries.clear();
    bank->entries.reserve(entry_count);

    for (std::uint32_t index = 0; index < entry_count; ++index) {
        ParsedEntry entry;
        entry.entry_index = index;

        // sub_82B90E40: debug banks carry the name inline and derive the hash
        // from it; release banks store the precomputed hash instead.
        if (bank->has_debug_names) {
            entry.entry_name = reader->CString();
            entry.entry_hash_hex =
                FormatHex32(ComputeShaderBindingHash(entry.entry_name));
        } else {
            entry.entry_hash_hex = FormatHex32(reader->U32());
        }

        entry.stage_raw = reader->U8();
        entry.entry_id = reader->U32();
        entry.stage = InferShaderStage(entry.stage_raw, entry.entry_name);

        if (reader->failed()) {
            if (error != nullptr) {
                *error = "Truncated shader entry table in " + bank->bank_name;
            }
            return false;
        }
        bank->entries.push_back(std::move(entry));
    }

    const std::uint32_t chunk_count = reader->U32();
    if (reader->failed() || chunk_count == 0 ||
        chunk_count > reader->remaining() / sizeof(std::uint32_t)) {
        if (error != nullptr) {
            *error = "Implausible compressed chunk count in " + bank->bank_name;
        }
        return false;
    }

    bank->chunk_decompressed_sizes.resize(chunk_count);
    for (std::uint32_t index = 0; index < chunk_count; ++index) {
        bank->chunk_decompressed_sizes[index] = reader->U32();
    }
    bank->chunk_compressed_offsets.resize(chunk_count);
    for (std::uint32_t index = 0; index < chunk_count; ++index) {
        bank->chunk_compressed_offsets[index] = reader->U32();
    }

    const std::uint32_t blob_size = reader->U32();
    if (reader->failed() || blob_size > reader->remaining()) {
        if (error != nullptr) {
            *error = "Compressed payload region overruns " + bank->bank_name;
        }
        return false;
    }

    bank->compressed_blob_base = reader->cursor();
    bank->compressed_blob_size = blob_size;
    (void)reader->Skip(blob_size);
    return true;
}

bool ParseBank(const std::filesystem::path& bank_path,
               ParsedBank* bank,
               std::string* error) {
    if (bank == nullptr) {
        if (error != nullptr) {
            *error = "internal parser output missing";
        }
        return false;
    }

    if (!ReadBinaryFile(bank_path, &bank->bytes, error)) {
        return false;
    }

    bank->bank_name = bank_path.filename().string();
    bank->bank_path = bank_path;

    // sub_82B83378: magic, u32 version (must be 3), then a byte that selects
    // whether shader entries carry inline debug names.
    if (bank->bytes.size() < kMagic.size() + 5) {
        if (error != nullptr) {
            *error = bank->bank_name + " is too small to be a supported ShaderBankFile";
        }
        return false;
    }

    const std::string_view magic(reinterpret_cast<const char*>(bank->bytes.data()),
                                 kMagic.size());
    if (magic != kMagic) {
        if (error != nullptr) {
            *error = bank->bank_name + " does not start with ShaderBankFile magic";
        }
        return false;
    }

    BankReader reader(bank->bytes, kMagic.size());
    const std::uint32_t version = reader.U32();
    if (version != kSupportedBankVersion) {
        if (error != nullptr) {
            *error = bank->bank_name + " reports unsupported ShaderBankFile version " +
                     std::to_string(version);
        }
        return false;
    }
    bank->has_debug_names = reader.U8() != 0;

    if (!SkipRenderStateSection(&reader, error)) {
        if (error != nullptr && !error->empty()) {
            *error = *error + " in " + bank->bank_name;
        }
        return false;
    }
    if (!SkipBindingNameSection(&reader, error)) {
        if (error != nullptr && !error->empty()) {
            *error = *error + " in " + bank->bank_name;
        }
        return false;
    }
    if (!ParseShaderSection(bank, &reader, error)) {
        return false;
    }

    if (bank->entries.empty()) {
        if (error != nullptr) {
            *error = "Parsed zero shader entries in " + bank->bank_name;
        }
        return false;
    }

    // The parse is exact: a well-formed bank is fully consumed. Anything left
    // over means a field width is wrong, so refuse rather than guess.
    if (reader.cursor() != bank->bytes.size()) {
        if (error != nullptr) {
            *error = bank->bank_name + " has " +
                     std::to_string(bank->bytes.size() - reader.cursor()) +
                     " unconsumed trailing bytes after the compressed payload";
        }
        return false;
    }

    return true;
}

// Release banks omit debug names. Both shipped banks describe the same shader
// set in the same order (verified: identical entry ids, stage bytes, section A
// and section B contents), so names transfer by binding hash.
void CrossFillEntryNamesAndHashes(std::vector<ParsedBank>* banks) {
    if (banks == nullptr) {
        return;
    }

    std::unordered_map<std::string, std::string> hash_to_name;
    for (const ParsedBank& bank : *banks) {
        for (const ParsedEntry& entry : bank.entries) {
            if (entry.entry_name.empty() || entry.entry_hash_hex.empty()) {
                continue;
            }
            hash_to_name.emplace(entry.entry_hash_hex, entry.entry_name);
        }
    }

    for (ParsedBank& bank : *banks) {
        for (ParsedEntry& entry : bank.entries) {
            if (entry.entry_name.empty()) {
                const auto name_it = hash_to_name.find(entry.entry_hash_hex);
                if (name_it != hash_to_name.end()) {
                    entry.entry_name = name_it->second;
                }
            }
            entry.stage = InferShaderStage(entry.stage_raw, entry.entry_name);
        }
    }
}

bool InflateCompressedChunk(const std::uint8_t* compressed_data,
                            std::size_t compressed_size,
                            std::vector<std::uint8_t>* decompressed,
                            std::string* error) {
    if (compressed_data == nullptr || decompressed == nullptr) {
        if (error != nullptr) {
            *error = "internal zlib decode state missing";
        }
        return false;
    }

    decompressed->clear();

    z_stream stream;
    std::memset(&stream, 0, sizeof(stream));

    stream.next_in = const_cast<Bytef*>(
        reinterpret_cast<const Bytef*>(compressed_data));
    stream.avail_in = static_cast<uInt>(
        std::min<std::size_t>(compressed_size,
                              static_cast<std::size_t>(
                                  std::numeric_limits<uInt>::max())));

    const int init_status = inflateInit(&stream);
    if (init_status != Z_OK) {
        if (error != nullptr) {
            *error = "inflateInit failed";
        }
        return false;
    }

    std::array<std::uint8_t, 16384> out_buffer{};
    bool success = true;

    while (true) {
        stream.next_out = out_buffer.data();
        stream.avail_out = static_cast<uInt>(out_buffer.size());

        const int inflate_status = inflate(&stream, Z_NO_FLUSH);
        const std::size_t produced = out_buffer.size() - stream.avail_out;
        if (produced != 0) {
            decompressed->insert(
                decompressed->end(),
                out_buffer.begin(),
                out_buffer.begin() + static_cast<std::ptrdiff_t>(produced));
        }

        if (inflate_status == Z_STREAM_END) {
            break;
        }

        // Some banks appear to omit/tamper zlib trailer checksums. We still
        // accept the decoded payload when input is exhausted and output exists.
        if ((inflate_status == Z_OK || inflate_status == Z_BUF_ERROR ||
             inflate_status == Z_DATA_ERROR) &&
            stream.avail_in == 0) {
            break;
        }

        if (inflate_status != Z_OK && inflate_status != Z_BUF_ERROR &&
            inflate_status != Z_DATA_ERROR) {
            success = false;
            if (error != nullptr) {
                *error = "inflate failed with status " +
                         std::to_string(inflate_status);
            }
            break;
        }

        if (inflate_status == Z_BUF_ERROR && produced == 0) {
            break;
        }
    }

    inflateEnd(&stream);

    if (!success) {
        return false;
    }

    if (decompressed->empty()) {
        if (error != nullptr) {
            *error = "decoded compressed chunk produced no output";
        }
        return false;
    }

    return true;
}

bool TryParseXenosContainer(const std::vector<std::uint8_t>& bytes,
                            std::size_t offset,
                            std::size_t* data_size,
                            ShaderStage* stage) {
    if (offset + 0x24 > bytes.size()) {
        return false;
    }

    const std::uint32_t flags = ReadBigEndianU32(bytes, offset + 0x00);
    if ((flags & 0xffffff00u) != 0x102a1100u) {
        return false;
    }

    const std::uint32_t virtual_size = ReadBigEndianU32(bytes, offset + 0x04);
    const std::uint32_t physical_size = ReadBigEndianU32(bytes, offset + 0x08);
    const std::uint32_t constant_table_offset =
        ReadBigEndianU32(bytes, offset + 0x10);
    const std::uint32_t definition_table_offset =
        ReadBigEndianU32(bytes, offset + 0x14);
    const std::uint32_t shader_offset = ReadBigEndianU32(bytes, offset + 0x18);
    const std::uint32_t field1c = ReadBigEndianU32(bytes, offset + 0x1c);
    const std::uint32_t field20 = ReadBigEndianU32(bytes, offset + 0x20);

    const std::size_t container_size =
        static_cast<std::size_t>(virtual_size) + physical_size;
    if (virtual_size == 0 || physical_size == 0) {
        return false;
    }
    if (offset + container_size > bytes.size()) {
        return false;
    }
    if (constant_table_offset == 0 || constant_table_offset >= container_size) {
        return false;
    }
    if (shader_offset == 0 || shader_offset >= container_size) {
        return false;
    }
    if (definition_table_offset >= container_size) {
        return false;
    }
    if (field1c != 0 || field20 != 0) {
        return false;
    }

    if (data_size != nullptr) {
        *data_size = container_size;
    }

    if (stage != nullptr) {
        const bool is_pixel_shader = (flags & 0x1u) == 0;
        *stage = is_pixel_shader ? ShaderStage::kPixel : ShaderStage::kVertex;
    }

    return true;
}

std::string BuildEntryNameHint(const ParsedEntry& entry) {
    if (!entry.entry_name.empty()) {
        return entry.entry_name;
    }
    if (!entry.entry_hash_hex.empty()) {
        return "hash_" + entry.entry_hash_hex;
    }
    return "unnamed";
}

std::filesystem::path BuildBlobOutputPath(const std::filesystem::path& extract_root,
                                          std::string_view bank_name,
                                          std::size_t blob_index,
                                          std::string_view name_hint) {
    const std::string bank_stem = BuildBankOutputStem(bank_name);
    const std::string clean_name = ToLowerAscii(SanitizeNameForFile(name_hint, 120));
    std::ostringstream filename;
    filename << std::setfill('0') << std::setw(4) << blob_index << "_"
             << clean_name << ".bin";
    return extract_root / "blobs" / bank_stem / filename.str();
}

bool AppendFableInlineShaderBlobs(const std::filesystem::path& extract_root,
                                  std::vector<ShaderSbkBlobMetadata>* blobs,
                                  std::unordered_set<std::string>* seen_blob_hashes,
                                  std::string* error) {
    if (blobs == nullptr) {
        if (error != nullptr) {
            *error = "blob metadata output missing";
        }
        return false;
    }

    const std::string sha256 = refii::shader::crypto::ComputeSha256Hex(
        kFablePixRenderVideoFramePixelShaderBlob.data(),
        kFablePixRenderVideoFramePixelShaderBlob.size());
    if (seen_blob_hashes != nullptr &&
        !seen_blob_hashes->insert(sha256).second) {
        return true;
    }

    const std::filesystem::path output_path = BuildBlobOutputPath(
        extract_root,
        "Shaders.sbk",
        kFablePixRenderVideoFramePixelShaderBlobId,
        kFablePixRenderVideoFramePixelShaderName);
    if (!WriteBinaryFile(output_path,
                         kFablePixRenderVideoFramePixelShaderBlob.data(),
                         kFablePixRenderVideoFramePixelShaderBlob.size(),
                         error)) {
        if (error != nullptr && !error->empty()) {
            *error = "Failed to write Fable PixRenderVideoFrame inline shader "
                     "blob: " +
                     *error;
        }
        return false;
    }

    ShaderSbkBlobMetadata metadata;
    metadata.bank_name = "Shaders.sbk";
    metadata.blob_index = kFablePixRenderVideoFramePixelShaderBlobId;
    metadata.source_offset = 0x82093C7Cu;
    metadata.source_size =
        static_cast<std::uint32_t>(kFablePixRenderVideoFramePixelShaderBlob.size());
    metadata.stage = ShaderStage::kPixel;
    metadata.name_hint = std::string(kFablePixRenderVideoFramePixelShaderName);
    metadata.output_path = output_path;
    metadata.sha256 = sha256;
    blobs->push_back(std::move(metadata));
    return true;
}

// Inflate one compressed chunk. Each chunk record in the payload region is a
// u32 big-endian compressed length followed by a standard zlib stream
// (sub_82B846B0 seeks the payload stream to chunk_compressed_offsets[i] and
// hands it to LHCompression_CZlibCompressor with the declared decompressed
// size as the output budget).
bool InflateBankChunk(const ParsedBank& bank,
                      std::size_t chunk_index,
                      std::vector<std::uint8_t>* decompressed,
                      std::string* error) {
    const std::size_t record_offset =
        bank.compressed_blob_base + bank.chunk_compressed_offsets[chunk_index];
    if (record_offset + 4 > bank.bytes.size()) {
        if (error != nullptr) {
            *error = "Compressed chunk record starts past end of " + bank.bank_name;
        }
        return false;
    }

    const std::uint32_t compressed_size = ReadBigEndianU32(bank.bytes, record_offset);
    if (record_offset + 4 + compressed_size > bank.bytes.size()) {
        if (error != nullptr) {
            *error = "Compressed chunk overruns " + bank.bank_name;
        }
        return false;
    }

    if (!InflateCompressedChunk(bank.bytes.data() + record_offset + 4,
                                compressed_size,
                                decompressed,
                                error)) {
        if (error != nullptr && !error->empty()) {
            *error = "Failed to inflate shader chunk " + std::to_string(chunk_index) +
                     " of " + bank.bank_name + ": " + *error;
        }
        return false;
    }

    const std::size_t expected = bank.chunk_decompressed_sizes[chunk_index];
    if (decompressed->size() != expected) {
        if (error != nullptr) {
            *error = "Shader chunk " + std::to_string(chunk_index) + " of " +
                     bank.bank_name + " inflated to " +
                     std::to_string(decompressed->size()) + " bytes, expected " +
                     std::to_string(expected);
        }
        return false;
    }
    return true;
}

// sub_82B91130: a payload is two counted tables of 16-byte constant-map
// records, four 8-byte usage masks, then a u32 microcode length followed by the
// microcode itself. Only the microcode is an extraction artifact; the rest is
// walked to establish its exact bounds.
bool LocatePayloadMicrocode(const std::vector<std::uint8_t>& chunk,
                            std::size_t payload_offset,
                            std::size_t* code_offset,
                            std::size_t* code_size,
                            std::string* error) {
    std::size_t cursor = payload_offset;
    const auto read_u32 = [&chunk, &cursor](std::uint32_t* out) {
        if (cursor + 4 > chunk.size()) {
            return false;
        }
        *out = ReadBigEndianU32(chunk, cursor);
        cursor += 4;
        return true;
    };

    for (int table = 0; table < 2; ++table) {
        std::uint32_t record_count = 0;
        if (!read_u32(&record_count)) {
            if (error != nullptr) {
                *error = "payload constant-table count is out of bounds";
            }
            return false;
        }
        const std::size_t table_bytes = static_cast<std::size_t>(record_count) * 16u;
        if (record_count > chunk.size() / 16u || cursor + table_bytes > chunk.size()) {
            if (error != nullptr) {
                *error = "payload constant table overruns its chunk";
            }
            return false;
        }
        cursor += table_bytes;
    }

    // Four 64-bit register/sampler usage masks.
    if (cursor + 32 > chunk.size()) {
        if (error != nullptr) {
            *error = "payload usage masks overrun their chunk";
        }
        return false;
    }
    cursor += 32;

    std::uint32_t microcode_size = 0;
    if (!read_u32(&microcode_size)) {
        if (error != nullptr) {
            *error = "payload microcode length is out of bounds";
        }
        return false;
    }
    if (cursor + microcode_size > chunk.size()) {
        if (error != nullptr) {
            *error = "payload microcode overruns its chunk";
        }
        return false;
    }

    *code_offset = cursor;
    *code_size = microcode_size;
    return true;
}

// A bank stores one microcode payload per (chunk, slot) pair, and many shader
// entries may select the same payload. Extraction is therefore driven by the
// payload id space, not by the entry list: every payload is emitted exactly
// once, named after the entries that select it.
bool ExtractBankBlobs(const ParsedBank& bank,
                      const std::filesystem::path& extract_root,
                      std::vector<ShaderSbkBlobMetadata>* blobs,
                      std::unordered_set<std::string>* seen_blob_hashes,
                      bool* wrote_blob,
                      std::string* error) {
    if (blobs == nullptr) {
        if (error != nullptr) {
            *error = "blob metadata output missing";
        }
        return false;
    }
    if (wrote_blob != nullptr) {
        *wrote_blob = false;
    }

    const std::size_t chunk_count = bank.chunk_decompressed_sizes.size();
    if (chunk_count == 0) {
        if (error != nullptr) {
            *error = "No compressed shader chunks found in " + bank.bank_name;
        }
        return false;
    }

    const std::size_t payload_count = chunk_count * kPayloadsPerChunk;

    // Group entries by the payload they select, preserving bank order so the
    // first entry naming a payload becomes its canonical name.
    std::vector<std::vector<const ParsedEntry*>> entries_by_payload(payload_count);
    for (const ParsedEntry& entry : bank.entries) {
        if (entry.entry_id >= payload_count) {
            if (error != nullptr) {
                *error = "Shader entry " + std::to_string(entry.entry_index) + " of " +
                         bank.bank_name + " selects payload " +
                         std::to_string(entry.entry_id) + " but the bank only holds " +
                         std::to_string(payload_count);
            }
            return false;
        }
        entries_by_payload[entry.entry_id].push_back(&entry);
    }

    std::vector<std::uint8_t> chunk;
    std::size_t loaded_chunk = std::numeric_limits<std::size_t>::max();

    for (std::size_t payload_id = 0; payload_id < payload_count; ++payload_id) {
        const std::size_t chunk_index = payload_id / kPayloadsPerChunk;
        const std::size_t slot = payload_id % kPayloadsPerChunk;

        if (loaded_chunk != chunk_index) {
            if (!InflateBankChunk(bank, chunk_index, &chunk, error)) {
                return false;
            }
            loaded_chunk = chunk_index;
        }

        // Chunk header: a base offset followed by one relative offset per slot.
        const std::size_t header_bytes =
            static_cast<std::size_t>(kPayloadsPerChunk + 1) * 4u;
        if (chunk.size() < header_bytes) {
            if (error != nullptr) {
                *error = "Shader chunk " + std::to_string(chunk_index) + " of " +
                         bank.bank_name + " is too small to hold a slot table";
            }
            return false;
        }

        const std::uint32_t base = ReadBigEndianU32(chunk, 0);
        const std::uint32_t relative = ReadBigEndianU32(chunk, 4 + 4 * slot);
        const std::size_t payload_offset =
            static_cast<std::size_t>(base) + static_cast<std::size_t>(relative);
        if (payload_offset >= chunk.size()) {
            if (error != nullptr) {
                *error = "Payload " + std::to_string(payload_id) + " of " +
                         bank.bank_name + " points past its chunk";
            }
            return false;
        }

        std::size_t code_offset = 0;
        std::size_t code_size = 0;
        std::string locate_error;
        if (!LocatePayloadMicrocode(
                chunk, payload_offset, &code_offset, &code_size, &locate_error)) {
            if (error != nullptr) {
                *error = "Payload " + std::to_string(payload_id) + " of " +
                         bank.bank_name + ": " + locate_error;
            }
            return false;
        }
        if (code_size == 0) {
            if (error != nullptr) {
                *error = "Payload " + std::to_string(payload_id) + " of " +
                         bank.bank_name + " carries no microcode";
            }
            return false;
        }

        // The declared length must bound a well-formed Xenos container; this is
        // an independent check on the offsets we just walked.
        std::size_t container_size = 0;
        ShaderStage container_stage = ShaderStage::kUnknown;
        if (!TryParseXenosContainer(
                chunk, code_offset, &container_size, &container_stage) ||
            container_size > code_size) {
            if (error != nullptr) {
                *error = "Payload " + std::to_string(payload_id) + " of " +
                         bank.bank_name +
                         " does not begin with a well-formed Xenos container";
            }
            return false;
        }

        const std::vector<const ParsedEntry*>& owners = entries_by_payload[payload_id];
        std::string name_hint;
        ShaderStage stage = container_stage;
        if (!owners.empty()) {
            name_hint = BuildEntryNameHint(*owners.front());
            stage = owners.front()->stage;
        } else {
            // Reachable only if a bank ships a payload no entry selects.
            name_hint = "unreferenced_payload_" + std::to_string(payload_id);
        }

        const std::string sha256 = refii::shader::crypto::ComputeSha256Hex(
            chunk.data() + static_cast<std::ptrdiff_t>(code_offset), code_size);
        if (seen_blob_hashes != nullptr && !seen_blob_hashes->insert(sha256).second) {
            // Already emitted by an earlier bank describing the same shader set.
            continue;
        }

        const std::filesystem::path output_path =
            BuildBlobOutputPath(extract_root, bank.bank_name, payload_id, name_hint);
        if (!WriteBinaryFile(output_path,
                             chunk.data() + static_cast<std::ptrdiff_t>(code_offset),
                             code_size,
                             error)) {
            if (error != nullptr && !error->empty()) {
                *error = "Failed to write shader payload " +
                         std::to_string(payload_id) + " from " + bank.bank_name +
                         ": " + *error;
            }
            return false;
        }

        ShaderSbkBlobMetadata metadata;
        metadata.bank_name = bank.bank_name;
        metadata.blob_index = payload_id;
        metadata.source_offset = static_cast<std::uint32_t>(code_offset);
        metadata.source_size = static_cast<std::uint32_t>(code_size);
        metadata.stage = stage;
        metadata.name_hint = name_hint;
        metadata.output_path = output_path;
        metadata.sha256 = sha256;
        metadata.chunk_index = static_cast<std::uint32_t>(chunk_index);
        metadata.alias_count = static_cast<std::uint32_t>(owners.size());
        blobs->push_back(std::move(metadata));

        if (wrote_blob != nullptr) {
            *wrote_blob = true;
        }
    }

    return true;
}

bool WriteExtractManifest(const std::filesystem::path& manifest_path,
                          const std::vector<ParsedBank>& banks,
                          const std::vector<ShaderSbkBlobMetadata>& blobs,
                          const std::unordered_set<std::string>& banks_with_blobs,
                          std::string* error) {
    // Entry rows now carry blob_index, so every named binding resolves to its
    // microcode deterministically instead of by scan order.
    std::unordered_map<std::string, std::unordered_map<std::size_t, std::string>>
        blob_paths_by_bank;
    for (const ShaderSbkBlobMetadata& blob : blobs) {
        blob_paths_by_bank[blob.bank_name].emplace(blob.blob_index,
                                                   blob.output_path.generic_string());
    }

    std::ostringstream manifest;
    manifest
        << "row_type\tbank\tentry_index\tblob_index\tentry_id\tstage\tstage_raw\tentry_hash"
           "\tentry_name\tsource_offset\tsource_size\toutput_path\tsha256"
           "\tchunk_index\talias_count\n";

    for (const ParsedBank& bank : banks) {
        if (!banks_with_blobs.contains(bank.bank_name)) {
            continue;
        }
        const auto bank_paths_it = blob_paths_by_bank.find(bank.bank_name);
        for (const ParsedEntry& entry : bank.entries) {
            std::string output_path;
            if (bank_paths_it != blob_paths_by_bank.end()) {
                const auto path_it = bank_paths_it->second.find(entry.entry_id);
                if (path_it != bank_paths_it->second.end()) {
                    output_path = path_it->second;
                }
            }
            manifest << "entry"
                     << '\t' << EscapeTsv(bank.bank_name)
                     << '\t' << entry.entry_index
                     << '\t' << entry.entry_id
                     << '\t' << entry.entry_id
                     << '\t' << DescribeShaderStage(entry.stage)
                     << '\t' << entry.stage_raw
                     << '\t' << EscapeTsv(entry.entry_hash_hex)
                     << '\t' << EscapeTsv(entry.entry_name)
                     << '\t'
                     << '\t'
                     << '\t' << EscapeTsv(output_path)
                     << '\t'
                     << '\t' << (entry.entry_id / kPayloadsPerChunk)
                     << '\t'
                     << '\n';
        }
    }

    for (const ShaderSbkBlobMetadata& blob : blobs) {
        manifest << "blob"
                 << '\t' << EscapeTsv(blob.bank_name)
                 << '\t'
                 << '\t' << blob.blob_index
                 << '\t' << blob.blob_index
                 << '\t' << DescribeShaderStage(blob.stage)
                 << '\t'
                 << '\t'
                 << '\t' << EscapeTsv(blob.name_hint)
                 << '\t' << blob.source_offset
                 << '\t' << blob.source_size
                 << '\t' << EscapeTsv(blob.output_path.generic_string())
                 << '\t' << blob.sha256
                 << '\t' << blob.chunk_index
                 << '\t' << blob.alias_count
                 << '\n';
    }

    return WriteTextFile(manifest_path, manifest.str(), error);
}

bool ReadExtractManifestBlobs(const std::filesystem::path& manifest_path,
                              std::vector<TranslateBlobRecord>* blobs,
                              std::string* error) {
    if (blobs == nullptr) {
        if (error != nullptr) {
            *error = "blob output buffer missing";
        }
        return false;
    }

    std::ifstream file(manifest_path, std::ios::binary);
    if (!file) {
        if (error != nullptr) {
            *error = "Failed to open " + manifest_path.string();
        }
        return false;
    }

    std::string line;
    bool first = true;
    blobs->clear();

    while (std::getline(file, line)) {
        if (first) {
            first = false;
            continue;
        }
        if (line.empty()) {
            continue;
        }

        const std::vector<std::string> fields = SplitTsvLine(line);
        if (fields.size() < 13) {
            continue;
        }
        if (fields[0] != "blob") {
            continue;
        }

        TranslateBlobRecord record;
        record.bank_name = fields[1];
        if (!fields[3].empty()) {
            try {
                record.blob_index = static_cast<std::size_t>(std::stoull(fields[3]));
            } catch (const std::exception&) {
                record.blob_index = 0;
            }
        }

        const std::string stage_text = ToLowerAscii(fields[5]);
        if (stage_text == "vertex") {
            record.stage = ShaderStage::kVertex;
        } else if (stage_text == "pixel") {
            record.stage = ShaderStage::kPixel;
        } else {
            record.stage = ShaderStage::kUnknown;
        }

        record.name_hint = fields[8];
        record.sha256 = fields[12];
        record.input_path = std::filesystem::path(fields[11]);
        if (record.input_path.empty()) {
            continue;
        }
        blobs->push_back(std::move(record));
    }

    return true;
}

bool TranslateShaderBlobWithXenos(const TranslateBlobRecord& blob,
                                 const std::string& target,
                                 const std::filesystem::path& hlsl_path,
                                 const std::filesystem::path& output_path,
                                 const std::filesystem::path& tess_output_path,
                                 const std::filesystem::path& log_path,
                                 int* exit_code,
                                 bool* timed_out,
                                 std::string* note,
                                 std::string* memexport,
                                 std::string* error) {
    if (timed_out != nullptr) {
        *timed_out = false;
    }
    if (memexport != nullptr) {
        *memexport = "none";
    }
    if (exit_code != nullptr) {
        *exit_code = 0;
    }

    if (!PrepareTextFile(log_path, error)) {
        if (note != nullptr) {
            *note = "translator_log_init_failed";
        }
        if (exit_code != nullptr) {
            *exit_code = 1;
        }
        return false;
    }

    const std::string stage = DescribeShaderStage(blob.stage);
    const std::string name =
        blob.name_hint.empty() ? ("blob_" + std::to_string(blob.blob_index)) : blob.name_hint;

    std::string translate_error;
    refii::shader::XenosMemoryExportStats memexport_stats;
    const bool translated = refii::shader::TranslateXenosShaderBlob(
        blob.input_path,
        target,
        stage,
        name,
        output_path,
        log_path,
        &translate_error,
        &memexport_stats,
        tess_output_path);
    if (memexport != nullptr) {
        *memexport = refii::shader::DescribeXenosMemoryExportStats(memexport_stats);
    }
    if (!translated) {
        if (note != nullptr) {
            *note = translate_error.empty() ? "translator_failed" : translate_error;
        }
        if (exit_code != nullptr) {
            *exit_code = 1;
        }
        return false;
    }

    if (note != nullptr) {
        *note = "ok";
    }

    if (!hlsl_path.empty() && !std::filesystem::exists(hlsl_path)) {
        if (note != nullptr) {
            *note = "missing_hlsl_output";
        }
        if (exit_code != nullptr) {
            *exit_code = 1;
        }
        return false;
    }

    if (!tess_output_path.empty() && !std::filesystem::exists(tess_output_path)) {
        if (note != nullptr) {
            *note = "missing_tess_output";
        }
        if (exit_code != nullptr) {
            *exit_code = 1;
        }
        return false;
    }

    return true;
}

std::filesystem::path BuildTranslatedHlslPath(const std::filesystem::path& output_path) {
    return std::filesystem::path(output_path.string() + ".hlsl");
}

std::string BuildTranslatedOutputStem(const std::filesystem::path& input_path,
                                      std::string_view target) {
    return input_path.stem().string() + "." + std::string(target);
}

// Single definition of where a translated artifact lands.  Both the translator
// loop and VerifyTranslatedShaderCoverage() go through here so the writer and
// the checker can never disagree about the layout.
std::filesystem::path BuildTranslatedArtifactPath(const std::filesystem::path& translate_root,
                                                  std::string_view target,
                                                  std::string_view bank_name,
                                                  const std::filesystem::path& input_path) {
    // The Godot artifact is source, not a compiled binary: Godot's shader preprocessor includes
    // it, and the extension is what Godot's resource loader recognises.
    const char* extension = target == "godot" ? ".gdshaderinc" : ".bin";
    return translate_root /
           BuildCompileTargetOutputStem(target) /
           BuildBankOutputStem(bank_name) /
           (BuildTranslatedOutputStem(input_path, target) + extension);
}

// Hardware-tessellation variant of a VERTEX shader: same directory, same
// pairing convention, with a ".tess" marker between the blob stem and the
// target — "<blob stem>.tess.<target>.bin". Compiled from the identical
// generated HLSL with -D REFII_TESSELLATION_VARIANT=1 (entry seeds r0 from
// SV_VertexID/SV_InstanceID per the Xenos tessellation input contract; see
// XenosRecomp/shader_common.h). It shares the normal artifact's ".bin.hlsl"
// sidecar — no separate sidecar is written. Every vertex blob gets one; pixel
// and unknown-stage blobs never do.
std::filesystem::path BuildTranslatedTessArtifactPath(
    const std::filesystem::path& translate_root,
    std::string_view target,
    std::string_view bank_name,
    const std::filesystem::path& input_path) {
    return translate_root /
           BuildCompileTargetOutputStem(target) /
           BuildBankOutputStem(bank_name) /
           (input_path.stem().string() + ".tess." + std::string(target) + ".bin");
}

std::filesystem::path BuildTranslatorLogPath(const std::filesystem::path& translate_root,
                                             std::string_view target,
                                             std::string_view bank_name,
                                             std::string_view output_stem) {
    return translate_root /
           "logs" /
           BuildCompileTargetOutputStem(target) /
           BuildBankOutputStem(bank_name) /
           (std::string(output_stem) + ".stderr.log");
}

std::string BuildDedupeStageTag(ShaderStage stage) {
    switch (stage) {
    case ShaderStage::kVertex:
        return "vertex";
    case ShaderStage::kPixel:
        return "pixel";
    case ShaderStage::kUnknown:
    default:
        return "unknown";
    }
}

std::string BuildTranslateDedupeKey(const TranslateBlobRecord& blob,
                                    std::string_view target) {
    std::ostringstream key;
    if (!blob.sha256.empty()) {
        key << blob.sha256;
    } else {
        key << blob.input_path.generic_string();
    }
    key << '|' << BuildDedupeStageTag(blob.stage) << '|' << target;
    return key.str();
}

bool LinkOrCopyArtifact(const std::filesystem::path& source_path,
                        const std::filesystem::path& destination_path,
                        std::string* error) {
    if (source_path.empty() || destination_path.empty() || source_path == destination_path) {
        return true;
    }
    if (!std::filesystem::exists(source_path)) {
        return true;
    }

    std::error_code io_error;
    std::filesystem::create_directories(destination_path.parent_path(), io_error);
    if (io_error) {
        if (error != nullptr) {
            *error = "Failed to create alias artifact directory: " + io_error.message();
        }
        return false;
    }

    std::filesystem::remove(destination_path, io_error);
    io_error.clear();

    std::filesystem::create_hard_link(source_path, destination_path, io_error);
    if (!io_error) {
        return true;
    }

    io_error.clear();
    std::filesystem::copy_file(
        source_path,
        destination_path,
        std::filesystem::copy_options::overwrite_existing,
        io_error);
    if (!io_error) {
        return true;
    }

    if (error != nullptr) {
        *error = "Failed to materialize alias artifact " +
                 destination_path.string() + " from " + source_path.string() +
                 ": " + io_error.message();
    }
    return false;
}

bool ContainsLowered(std::string_view lowered_text, std::string_view lowered_pattern) {
    return lowered_text.find(lowered_pattern) != std::string_view::npos;
}

std::string DeriveFailureClass(std::string_view status,
                               std::string_view note,
                               const std::filesystem::path& stderr_path) {
    if (status == "ok") {
        return "none";
    }
    if (status == "backend_missing") {
        return "backend_missing";
    }
    if (status == "timeout") {
        return "timeout";
    }
    if (note == "translator_launch_failed") {
        return "launch_failed";
    }
    if (note == "translator_did_not_write_output") {
        return "missing_output";
    }
    if (note.size() >= std::string_view("dedupe_alias_copy_failed").size() &&
        note.compare(0,
                     std::string_view("dedupe_alias_copy_failed").size(),
                     std::string_view("dedupe_alias_copy_failed")) == 0) {
        return "alias_materialization_failed";
    }

    std::string stderr_text;
    std::string read_error;
    if (!stderr_path.empty() && std::filesystem::exists(stderr_path)) {
        (void)ReadTextFile(stderr_path, &stderr_text, &read_error);
    }
    const std::string lowered = ToLowerAscii(stderr_text);

    if (ContainsLowered(lowered, "tfetch1d") ||
        ContainsLowered(lowered, "texture1d") ||
        ContainsLowered(lowered, "_texture1ddescriptorindex")) {
        return "texture1d_support";
    }
    if (ContainsLowered(lowered, "vk::location") ||
        ContainsLowered(lowered, "partial explicit stage input location assignment") ||
        ContainsLowered(lowered, "redefinition of parameter")) {
        return "vk_location";
    }
    if (ContainsLowered(lowered, "undeclared identifier") ||
        ContainsLowered(lowered, "not declared")) {
        return "undeclared_symbol";
    }
    if (ContainsLowered(lowered, "assertion") ||
        ContainsLowered(lowered, "xenosrecomp:")) {
        return "xenos_internal_assert";
    }
    if (ContainsLowered(lowered, "dxc exited with code")) {
        return "dxc_compile_failure";
    }
    if (ContainsLowered(lowered, "xenosrecomp exited with code")) {
        return "xenos_recompile_failure";
    }
    if (note == "translator_nonzero_exit") {
        return "translator_nonzero_exit";
    }
    return "unknown_failure";
}

std::vector<XenosContainerCandidate> FindXenosContainerCandidates(
    const std::vector<std::uint8_t>& bytes) {
    std::vector<XenosContainerCandidate> candidates;
    if (bytes.size() < 0x24) {
        return candidates;
    }

    for (std::size_t offset = 0; offset + 0x24 <= bytes.size(); offset += 4) {
        const std::uint32_t flags = ReadBigEndianU32(bytes, offset + 0x00);
        if ((flags & 0xffffff00u) != 0x102a1100u) {
            continue;
        }

        const std::uint32_t virtual_size = ReadBigEndianU32(bytes, offset + 0x04);
        const std::uint32_t physical_size = ReadBigEndianU32(bytes, offset + 0x08);
        const std::uint32_t constant_table_offset = ReadBigEndianU32(bytes, offset + 0x10);
        const std::uint32_t definition_table_offset = ReadBigEndianU32(bytes, offset + 0x14);
        const std::uint32_t shader_offset = ReadBigEndianU32(bytes, offset + 0x18);
        const std::uint32_t field1c = ReadBigEndianU32(bytes, offset + 0x1c);
        const std::uint32_t field20 = ReadBigEndianU32(bytes, offset + 0x20);

        const std::uint64_t data_size =
            static_cast<std::uint64_t>(virtual_size) + physical_size;
        const std::size_t remaining = bytes.size() - offset;

        if (field1c != 0 || field20 != 0) {
            continue;
        }
        if (virtual_size == 0 || physical_size == 0 || data_size > remaining) {
            continue;
        }
        if (constant_table_offset == 0 || constant_table_offset >= data_size) {
            continue;
        }
        if (shader_offset == 0 || shader_offset >= data_size) {
            continue;
        }
        if (definition_table_offset >= data_size) {
            continue;
        }

        XenosContainerCandidate candidate;
        candidate.offset = offset;
        candidate.flags = flags;
        candidate.virtual_size = virtual_size;
        candidate.physical_size = physical_size;
        candidate.constant_table_offset = constant_table_offset;
        candidate.definition_table_offset = definition_table_offset;
        candidate.shader_offset = shader_offset;
        candidates.push_back(candidate);
    }

    return candidates;
}

// ---------------------------------------------------------------------------
// Translated-shader coverage verification.
//
// Kept in lockstep with tools/check_refii_shader_translation_coverage.py; that
// script and this function must implement the same checks.  The native copy
// exists because the launcher is the shipping entry point and cannot assume a
// Python interpreter on an end-user machine.
// ---------------------------------------------------------------------------

// src/shader/src/refii_shader_xenos_translator_main.cpp writes this stub before
// real translation runs; a surviving stub means translation never happened.
constexpr std::string_view kTranslatorPlaceholderSidecar =
    "// refii-shader-xenos-translator placeholder sidecar";

constexpr std::array<std::uint8_t, 4> kSpirvMagic = {0x03, 0x02, 0x23, 0x07};
constexpr std::string_view kDxbcMagic = "DXBC";
constexpr std::string_view kMetallibMagic = "MTLB";

bool ReadTsvTable(const std::filesystem::path& path,
                  std::vector<std::string>* header,
                  std::vector<std::vector<std::string>>* rows,
                  std::string* error) {
    if (header == nullptr || rows == nullptr) {
        if (error != nullptr) {
            *error = "internal tsv output buffers missing";
        }
        return false;
    }

    header->clear();
    rows->clear();

    std::ifstream file(path, std::ios::binary);
    if (!file) {
        if (error != nullptr) {
            *error = "shader_translation_coverage=REJECTED missing_file=" + path.string();
        }
        return false;
    }

    std::string line;
    if (!std::getline(file, line)) {
        if (error != nullptr) {
            *error = "shader_translation_coverage=REJECTED empty_file=" + path.string();
        }
        return false;
    }
    if (!line.empty() && line.back() == '\r') {
        line.pop_back();
    }
    *header = SplitTsvLine(line);

    while (std::getline(file, line)) {
        if (!line.empty() && line.back() == '\r') {
            line.pop_back();
        }
        if (TrimAscii(line).empty()) {
            continue;
        }
        rows->push_back(SplitTsvLine(line));
    }

    return true;
}

bool ResolveTsvColumn(const std::vector<std::string>& header,
                      std::string_view name,
                      const std::filesystem::path& path,
                      std::size_t* index,
                      std::string* error) {
    for (std::size_t i = 0; i < header.size(); ++i) {
        if (header[i] == name) {
            if (index != nullptr) {
                *index = i;
            }
            return true;
        }
    }

    if (error != nullptr) {
        *error = "shader_translation_coverage=REJECTED missing_column=" + std::string(name) +
                 " file=" + path.string();
    }
    return false;
}

std::string BuildCoverageRowKey(std::string_view bank,
                                std::string_view blob_index,
                                std::string_view target) {
    std::string key(bank);
    key.push_back('\t');
    key.append(blob_index);
    key.push_back('\t');
    key.append(target);
    return key;
}

bool HasMagic(const std::vector<std::uint8_t>& bytes, std::string_view magic) {
    if (bytes.size() < magic.size()) {
        return false;
    }
    return std::memcmp(bytes.data(), magic.data(), magic.size()) == 0;
}

// Check 2/3 from the Python checker: right container, non-empty, and (for DXIL)
// actually signed.  Unsigned DXIL is rejected by D3D12 outside developer mode.
bool CheckTranslatedBinary(const std::filesystem::path& path,
                           std::string_view target,
                           std::string_view label,
                           std::vector<std::string>* failures) {
    std::error_code exists_error;
    if (!std::filesystem::is_regular_file(path, exists_error)) {
        failures->push_back(std::string(label) + ": missing translated binary " + path.string());
        return false;
    }

    std::vector<std::uint8_t> bytes;
    std::string read_error;
    if (!ReadBinaryFile(path, &bytes, &read_error)) {
        failures->push_back(std::string(label) + ": unreadable translated binary " +
                            path.string() + " (" + read_error + ")");
        return false;
    }

    if (bytes.empty()) {
        failures->push_back(std::string(label) + ": empty translated binary " + path.string());
        return false;
    }

    if (target == "godot") {
        // Source text: a stage function must be in it.
        const std::string_view text(reinterpret_cast<const char*>(bytes.data()), bytes.size());
        if (text.find("void fragment()") == std::string_view::npos &&
            text.find("void vertex()") == std::string_view::npos) {
            failures->push_back(std::string(label) + ": not a Godot stage " + path.string());
            return false;
        }
    } else if (target == "vulkan") {
        if (bytes.size() < kSpirvMagic.size() ||
            std::memcmp(bytes.data(), kSpirvMagic.data(), kSpirvMagic.size()) != 0) {
            failures->push_back(std::string(label) + ": not a SPIR-V module " + path.string());
            return false;
        }
        if ((bytes.size() % 4) != 0) {
            failures->push_back(std::string(label) + ": SPIR-V size not word aligned " +
                                path.string());
            return false;
        }
    } else if (target == "d3d12") {
        if (!HasMagic(bytes, kDxbcMagic)) {
            failures->push_back(std::string(label) + ": not a DXBC container " + path.string());
            return false;
        }
        if (bytes.size() < 20) {
            failures->push_back(std::string(label) + ": DXBC truncated (" +
                                std::to_string(bytes.size()) + " bytes) " + path.string());
            return false;
        }
        const bool signed_container =
            std::any_of(bytes.begin() + 4, bytes.begin() + 20,
                        [](std::uint8_t byte) { return byte != 0; });
        if (!signed_container) {
            failures->push_back(std::string(label) +
                                ": unsigned DXIL (zero digest); dxil signing did not run " +
                                path.string());
            return false;
        }
    } else if (target == "metal") {
        if (!HasMagic(bytes, kMetallibMagic)) {
            failures->push_back(std::string(label) + ": not a metallib " + path.string());
            return false;
        }
    }

    return true;
}

// Check 4: the HLSL sidecar must exist and must not still be the placeholder.
void CheckTranslatedSidecar(const std::filesystem::path& path,
                            std::string_view target,
                            std::string_view label,
                            std::vector<std::string>* failures) {
    if (target == "metal" || target == "godot") {
        return;
    }

    std::error_code exists_error;
    if (!std::filesystem::is_regular_file(path, exists_error)) {
        failures->push_back(std::string(label) + ": missing HLSL sidecar " + path.string());
        return;
    }

    std::string text;
    std::string read_error;
    if (!ReadTextFile(path, &text, &read_error)) {
        failures->push_back(std::string(label) + ": unreadable HLSL sidecar " + path.string() +
                            " (" + read_error + ")");
        return;
    }

    if (TrimAscii(text).rfind(std::string(kTranslatorPlaceholderSidecar), 0) == 0) {
        failures->push_back(
            std::string(label) +
            ": HLSL sidecar is still the translator placeholder stub; real translation "
            "never ran " +
            path.string());
    }
}

}  // namespace

const char* DescribeShaderStage(ShaderStage stage) {
    switch (stage) {
    case ShaderStage::kVertex:
        return "vertex";
    case ShaderStage::kPixel:
        return "pixel";
    case ShaderStage::kUnknown:
    default:
        return "unknown";
    }
}

bool ExtractShaderSbkWorkspace(const ShaderSbkExtractOptions& options,
                               ShaderSbkExtractSummary* summary,
                               std::string* error,
                               const ShaderSbkProgressCallback& progress_callback) {
    ShaderSbkExtractSummary local_summary;
    ShaderSbkExtractSummary* const active_summary = summary != nullptr ? summary : &local_summary;
    *active_summary = {};

    const std::filesystem::path input_dir = std::filesystem::absolute(options.input_dir);
    const std::filesystem::path extract_out_dir = std::filesystem::absolute(options.extract_out_dir);
    const std::filesystem::path manifest_path = std::filesystem::absolute(options.manifest_path);

    active_summary->input_dir = input_dir;
    active_summary->extract_out_dir = extract_out_dir;
    active_summary->manifest_path = manifest_path;

    try {
        const std::array<std::string_view, 4> legacy_blob_stems = {
            "shaders",
            "shadersrelease",
            "Shaders",
            "ShadersRelease",
        };
        for (const std::string_view blob_stem : legacy_blob_stems) {
            std::filesystem::remove_all(extract_out_dir / "blobs" / blob_stem);
        }
        for (const std::string_view bank_file : kExpectedBankFiles) {
            const std::string normalized_bank_stem = BuildBankOutputStem(bank_file);
            std::filesystem::remove_all(
                extract_out_dir / "blobs" / normalized_bank_stem);

            const std::string legacy_bank_stem =
                std::filesystem::path(bank_file).stem().string();
            if (legacy_bank_stem != normalized_bank_stem) {
                std::filesystem::remove_all(extract_out_dir / legacy_bank_stem);
            }
            std::filesystem::remove_all(extract_out_dir / normalized_bank_stem);
        }
    } catch (const std::exception& exception) {
        if (error != nullptr) {
            *error = "Failed to prepare extract output directories: " +
                     std::string(exception.what());
        }
        return false;
    }

    std::vector<ParsedBank> banks;
    banks.reserve(kExpectedBankFiles.size());

    for (std::size_t bankIndex = 0; bankIndex < kExpectedBankFiles.size(); ++bankIndex) {
        const std::string_view bank_file = kExpectedBankFiles[bankIndex];
        const std::filesystem::path bank_path = input_dir / bank_file;
        if (!std::filesystem::exists(bank_path)) {
            if (error != nullptr) {
                *error = "Missing required shader bank: " + bank_path.string();
            }
            return false;
        }

        if (progress_callback) {
            progress_callback(ShaderSbkProgress{
                "extracting_bank",
                std::filesystem::path(bank_file).stem().generic_string(),
                bankIndex + 1,
                kExpectedBankFiles.size(),
                0,
                0,
                bankIndex + 1,
                kExpectedBankFiles.size(),
                {},
                bank_path.generic_string(),
                {}});
        }

        ParsedBank bank;
        if (!ParseBank(bank_path, &bank, error)) {
            return false;
        }
        banks.push_back(std::move(bank));

        if (progress_callback) {
            progress_callback(ShaderSbkProgress{
                "bank_ready",
                std::filesystem::path(bank_file).stem().generic_string(),
                bankIndex + 1,
                kExpectedBankFiles.size(),
                0,
                0,
                bankIndex + 1,
                kExpectedBankFiles.size(),
                {},
                bank_path.generic_string(),
                {}});
        }
    }

    CrossFillEntryNamesAndHashes(&banks);

    std::vector<ShaderSbkBlobMetadata> blobs;
    std::unordered_set<std::string> seen_blob_hashes;
    std::unordered_set<std::string> banks_with_blobs;
    for (const ParsedBank& bank : banks) {
        bool wrote_blob = false;
        if (!ExtractBankBlobs(
                bank, extract_out_dir, &blobs, &seen_blob_hashes, &wrote_blob, error)) {
            return false;
        }
        if (wrote_blob) {
            banks_with_blobs.insert(bank.bank_name);
            active_summary->entry_count += bank.entries.size();
        }

        if (progress_callback) {
            progress_callback(ShaderSbkProgress{
                "extracting_blobs",
                bank.bank_name,
                0,
                0,
                0,
                0,
                static_cast<std::size_t>(blobs.size()),
                0,
                {},
                bank.bank_path.generic_string(),
                {}});
        }
    }

    if (!AppendFableInlineShaderBlobs(
            extract_out_dir, &blobs, &seen_blob_hashes, error)) {
        return false;
    }
    banks_with_blobs.insert("Shaders.sbk");

    if (!WriteExtractManifest(manifest_path, banks, blobs, banks_with_blobs, error)) {
        return false;
    }

    active_summary->bank_count = banks_with_blobs.size();
    active_summary->blob_count = blobs.size();
    return true;
}

bool TranslateExtractedShaderSbk(const ShaderSbkTranslateOptions& options,
                                 ShaderSbkTranslateSummary* summary,
                                 std::string* error,
                                 const ShaderSbkProgressCallback& progress_callback) {
    ShaderSbkTranslateSummary local_summary;
    ShaderSbkTranslateSummary* const active_summary = summary != nullptr ? summary : &local_summary;
    *active_summary = {};

    active_summary->extract_manifest_path =
        std::filesystem::absolute(options.extract_manifest_path);
    active_summary->translate_out_dir = std::filesystem::absolute(options.translate_out_dir);
    active_summary->report_path = std::filesystem::absolute(options.report_path);
    active_summary->translator_path = options.translator_path;

    std::vector<TranslateBlobRecord> blobs;
    if (!ReadExtractManifestBlobs(active_summary->extract_manifest_path, &blobs, error)) {
        return false;
    }

    active_summary->blob_count = blobs.size();

    std::vector<std::string> targets;
    if (!ResolveTranslateTargets(options.targets, &targets, error)) {
        return false;
    }
    active_summary->resolved_targets = JoinCsv(targets);
    active_summary->target_count = targets.size();
    const std::size_t total_items = blobs.size() * targets.size();
    std::size_t completed_items = 0;

    std::unordered_set<std::string> bank_output_stems = {
        "shaders",
        "shadersrelease",
        "Shaders",
        "ShadersRelease",
    };
    for (const TranslateBlobRecord& blob : blobs) {
        bank_output_stems.insert(BuildBankOutputStem(blob.bank_name));
    }
    try {
        for (const std::string& target : targets) {
            const std::string target_stem = BuildCompileTargetOutputStem(target);
            for (const std::string& bank_stem : bank_output_stems) {
                std::filesystem::remove_all(
                    active_summary->translate_out_dir / target_stem / bank_stem);
                std::filesystem::remove_all(
                    active_summary->translate_out_dir / "logs" / target_stem / bank_stem);
            }
        }
    } catch (const std::exception& exception) {
        if (error != nullptr) {
            *error = "Failed to prepare compile output directories: " +
                     std::string(exception.what());
        }
        return false;
    }

    const bool translator_available = true;

    struct CachedTranslateResult {
        std::string status;
        std::string note;
        std::string memexport;
        int exit_code = 0;
        bool timed_out = false;
        std::filesystem::path output_path;
        std::filesystem::path tess_output_path;
        std::filesystem::path hlsl_path;
        std::filesystem::path stderr_path;
        std::string origin_bank;
        std::size_t origin_blob_index = 0;
    };
    std::unordered_map<std::string, CachedTranslateResult> dedupe_cache;

    std::ostringstream report;
    // memexport is appended last so existing positional readers keep working.
    // "none" = the translator did not report; "streams=0" = no Xenos memory
    // export in this blob; "streams=N,eM=0xNN[,...]" = this shader writes to
    // memory, not just to the render target.
    report << "bank\tblob_index\ttarget\tstage\tname\tinput_path\toutput_path\tstatus\texit_code\t"
              "note\thlsl_path\tfailure_class\tstderr_path\t"
              "dedupe_key\tdedupe_origin_bank\tdedupe_origin_blob_index\tdedupe_mode\t"
              "memexport\n";

    for (const TranslateBlobRecord& blob : blobs) {
        for (const std::string& target : targets) {
            if (progress_callback) {
                progress_callback(ShaderSbkProgress{
                    "compiling_shader",
                    blob.bank_name,
                    0,
                    0,
                    blob.blob_index,
                    blobs.size(),
                    completed_items,
                    total_items,
                    target,
                    blob.input_path.generic_string(),
                    DescribeShaderStage(blob.stage)});
            }

            const std::string output_stem = BuildTranslatedOutputStem(blob.input_path, target);
            const std::filesystem::path output_path = BuildTranslatedArtifactPath(
                active_summary->translate_out_dir, target, blob.bank_name, blob.input_path);
            // Vertex shaders additionally get the hardware-tessellation variant
            // artifact (same HLSL, tess entry seeding). No whitelist.
            // The Godot target has neither: it is the shader source itself, and Godot has no
            // hardware-tessellation path for a material.
            const bool godot_target = target == "godot";
            const std::filesystem::path tess_output_path =
                blob.stage == ShaderStage::kVertex && !godot_target
                    ? BuildTranslatedTessArtifactPath(active_summary->translate_out_dir,
                                                      target, blob.bank_name,
                                                      blob.input_path)
                    : std::filesystem::path{};
            const std::filesystem::path hlsl_path =
                godot_target ? std::filesystem::path{} : BuildTranslatedHlslPath(output_path);
            const std::filesystem::path stderr_path = BuildTranslatorLogPath(
                active_summary->translate_out_dir, target, blob.bank_name, output_stem);
            const std::filesystem::path crash_breadcrumb_path =
                active_summary->translate_out_dir / "compile_crash_breadcrumb.txt";

            std::string status;
            std::string note;
            std::string memexport = "none";
            int exit_code = 0;
            bool timed_out = false;
            std::string dedupe_origin_bank = blob.bank_name;
            std::size_t dedupe_origin_blob_index = blob.blob_index;
            std::string dedupe_mode = "executed";
            const std::string dedupe_key = BuildTranslateDedupeKey(blob, target);

            if (!translator_available) {
                status = "backend_missing";
                note = "translator_not_configured";
            } else {
                ++active_summary->attempted;
                bool used_alias = false;
                if (options.dedupe_identical_blobs) {
                    auto cached = dedupe_cache.find(dedupe_key);
                    if (cached != dedupe_cache.end()) {
                        used_alias = true;
                        dedupe_mode = "aliased";
                        ++active_summary->dedupe_aliased;
                        status = cached->second.status;
                        note = cached->second.note;
                        memexport = cached->second.memexport;
                        exit_code = cached->second.exit_code;
                        timed_out = cached->second.timed_out;
                        dedupe_origin_bank = cached->second.origin_bank;
                        dedupe_origin_blob_index = cached->second.origin_blob_index;

                        std::string alias_error;
                        if (!LinkOrCopyArtifact(cached->second.output_path, output_path, &alias_error) ||
                            !LinkOrCopyArtifact(cached->second.tess_output_path, tess_output_path, &alias_error) ||
                            !LinkOrCopyArtifact(cached->second.hlsl_path, hlsl_path, &alias_error) ||
                            !LinkOrCopyArtifact(cached->second.stderr_path, stderr_path, &alias_error)) {
                            status = "failed";
                            note = alias_error.empty()
                                       ? "dedupe_alias_copy_failed"
                                       : ("dedupe_alias_copy_failed: " + alias_error);
                        }
                    }
                }

                if (!used_alias) {
                    ++active_summary->dedupe_executed;
                    std::string run_error;
                    {
                        std::ostringstream breadcrumb;
                        breadcrumb << "phase\tcompiling_shader\n"
                                   << "completed_items\t" << completed_items << '\n'
                                   << "total_items\t" << total_items << '\n'
                                   << "bank\t" << blob.bank_name << '\n'
                                   << "blob_index\t" << blob.blob_index << '\n'
                                   << "target\t" << target << '\n'
                                   << "stage\t" << DescribeShaderStage(blob.stage) << '\n'
                                   << "name\t" << blob.name_hint << '\n'
                                   << "input_path\t" << blob.input_path.generic_string() << '\n'
                                   << "output_path\t" << output_path.generic_string() << '\n'
                                   << "hlsl_path\t" << hlsl_path.generic_string() << '\n'
                                   << "stderr_path\t" << stderr_path.generic_string() << '\n';
                        std::string breadcrumb_error;
                        (void)WriteTextFile(
                            crash_breadcrumb_path, breadcrumb.str(), &breadcrumb_error);
                    }
                    const bool translated = TranslateShaderBlobWithXenos(
                        blob,
                        target,
                        hlsl_path,
                        output_path,
                        tess_output_path,
                        stderr_path,
                        &exit_code,
                        &timed_out,
                        &note,
                        &memexport,
                        &run_error);
                    if (!translated) {
                        status = "failed";
                        note = run_error.empty() ? "translator_launch_failed" : run_error;
                    } else {
                        status = "ok";
                        note = "ok";
                    }
                    {
                        std::ostringstream breadcrumb;
                        breadcrumb << "phase\tcompiled_shader\n"
                                   << "status\t" << status << '\n'
                                   << "note\t" << note << '\n'
                                   << "exit_code\t" << exit_code << '\n'
                                   << "completed_items\t" << completed_items << '\n'
                                   << "total_items\t" << total_items << '\n'
                                   << "bank\t" << blob.bank_name << '\n'
                                   << "blob_index\t" << blob.blob_index << '\n'
                                   << "target\t" << target << '\n'
                                   << "stage\t" << DescribeShaderStage(blob.stage) << '\n'
                                   << "name\t" << blob.name_hint << '\n'
                                   << "input_path\t" << blob.input_path.generic_string() << '\n'
                                   << "output_path\t" << output_path.generic_string() << '\n'
                                   << "hlsl_path\t" << hlsl_path.generic_string() << '\n'
                                   << "stderr_path\t" << stderr_path.generic_string() << '\n';
                        std::string breadcrumb_error;
                        (void)WriteTextFile(
                            crash_breadcrumb_path, breadcrumb.str(), &breadcrumb_error);
                    }

                    if (timed_out) {
                        status = "timeout";
                        note = "translator_timeout";
                    }
                    if (status == "ok" && !std::filesystem::exists(output_path)) {
                        status = "failed";
                        note = "translator_did_not_write_output";
                    }
                    if (status == "ok" && !tess_output_path.empty() &&
                        !std::filesystem::exists(tess_output_path)) {
                        status = "failed";
                        note = "translator_did_not_write_tess_output";
                    }

                    if (options.dedupe_identical_blobs) {
                        CachedTranslateResult cache_entry;
                        cache_entry.status = status;
                        cache_entry.note = note;
                        cache_entry.memexport = memexport;
                        cache_entry.exit_code = exit_code;
                        cache_entry.timed_out = timed_out;
                        cache_entry.output_path = output_path;
                        cache_entry.tess_output_path = tess_output_path;
                        cache_entry.hlsl_path = hlsl_path;
                        cache_entry.stderr_path = stderr_path;
                        cache_entry.origin_bank = blob.bank_name;
                        cache_entry.origin_blob_index = blob.blob_index;
                        dedupe_cache[dedupe_key] = std::move(cache_entry);
                    }
                }
            }

            if (status == "ok") {
                ++active_summary->succeeded;
            } else if (status == "timeout") {
                ++active_summary->timed_out;
            } else if (status == "backend_missing") {
                ++active_summary->backend_missing;
            } else {
                ++active_summary->failed;
            }

            ++completed_items;

            const std::string failure_class =
                DeriveFailureClass(status, note, stderr_path);
            report << EscapeTsv(blob.bank_name)
                   << '\t' << blob.blob_index
                   << '\t' << target
                   << '\t' << DescribeShaderStage(blob.stage)
                   << '\t' << EscapeTsv(blob.name_hint)
                   << '\t' << EscapeTsv(blob.input_path.generic_string())
                   << '\t' << EscapeTsv(output_path.generic_string())
                   << '\t' << status
                   << '\t' << exit_code
                   << '\t' << EscapeTsv(note)
                   << '\t' << EscapeTsv(hlsl_path.generic_string())
                   << '\t' << EscapeTsv(failure_class)
                   << '\t' << EscapeTsv(stderr_path.generic_string())
                   << '\t' << EscapeTsv(dedupe_key)
                   << '\t' << EscapeTsv(dedupe_origin_bank)
                   << '\t' << dedupe_origin_blob_index
                   << '\t' << EscapeTsv(dedupe_mode)
                   << '\t' << EscapeTsv(memexport)
                   << '\n';
        }
    }

    if (!WriteTextFile(active_summary->report_path, report.str(), error)) {
        return false;
    }

    {
        const std::filesystem::path crash_breadcrumb_path =
            active_summary->translate_out_dir / "compile_crash_breadcrumb.txt";
        std::ostringstream breadcrumb;
        breadcrumb << "phase\tcompile_complete\n"
                   << "total_items\t" << total_items << '\n'
                   << "succeeded\t" << active_summary->succeeded << '\n'
                   << "failed\t" << active_summary->failed << '\n'
                   << "timed_out\t" << active_summary->timed_out << '\n'
                   << "report_path\t" << active_summary->report_path.generic_string() << '\n';
        std::string breadcrumb_error;
        (void)WriteTextFile(crash_breadcrumb_path, breadcrumb.str(), &breadcrumb_error);
    }

    if (progress_callback) {
        progress_callback(ShaderSbkProgress{
            "compile_complete",
            {},
            0,
            0,
            0,
            0,
            total_items,
            total_items,
            {},
            {},
            {}});
    }

    return true;
}

bool BuildShaderSbkWorkspace(const ShaderSbkBuildOptions& options,
                             ShaderSbkBuildSummary* summary,
                             std::string* error,
                             const ShaderSbkProgressCallback& progress_callback) {
    ShaderSbkBuildSummary local_summary;
    ShaderSbkBuildSummary* const active_summary = summary != nullptr ? summary : &local_summary;
    *active_summary = {};

    if (!ExtractShaderSbkWorkspace(
            options.extract, &active_summary->extract, error, progress_callback)) {
        return false;
    }

    ShaderSbkTranslateOptions translate_options = options.translate;
    if (translate_options.extract_manifest_path.empty()) {
        translate_options.extract_manifest_path = options.extract.manifest_path;
    }

    if (!TranslateExtractedShaderSbk(
            translate_options, &active_summary->translate, error, progress_callback)) {
        return false;
    }

    return true;
}

bool ProbeExtractedShaderSbkForXenos(const ShaderSbkXenosProbeOptions& options,
                                     ShaderSbkXenosProbeSummary* summary,
                                     std::string* error) {
    ShaderSbkXenosProbeSummary local_summary;
    ShaderSbkXenosProbeSummary* const active_summary =
        summary != nullptr ? summary : &local_summary;
    *active_summary = {};

    active_summary->extract_manifest_path =
        std::filesystem::absolute(options.extract_manifest_path);
    active_summary->report_path = std::filesystem::absolute(options.report_path);

    std::vector<TranslateBlobRecord> blobs;
    if (!ReadExtractManifestBlobs(active_summary->extract_manifest_path, &blobs, error)) {
        return false;
    }

    active_summary->blob_count = blobs.size();

    std::ostringstream report;
    report << "bank\tblob_index\tinput_path\tcandidate_count\tfirst_offset\tfirst_flags\t"
              "first_virtual_size\tfirst_physical_size\tfirst_constant_table_offset\t"
              "first_definition_table_offset\tfirst_shader_offset\tstatus\tnote\n";

    for (const TranslateBlobRecord& blob : blobs) {
        std::vector<std::uint8_t> bytes;
        if (!ReadBinaryFile(blob.input_path, &bytes, error)) {
            if (error != nullptr && !error->empty()) {
                *error = "Failed to read blob during Xenos probe: " + blob.input_path.string() +
                         ": " + *error;
            }
            return false;
        }

        const std::vector<XenosContainerCandidate> candidates =
            FindXenosContainerCandidates(bytes);
        active_summary->total_candidates += candidates.size();

        std::string status = "no_xenos_container";
        std::string note = "xenosrecomp_signature_not_found";
        std::string first_offset;
        std::string first_flags;
        std::string first_virtual_size;
        std::string first_physical_size;
        std::string first_constant_table_offset;
        std::string first_definition_table_offset;
        std::string first_shader_offset;

        if (!candidates.empty()) {
            ++active_summary->blobs_with_candidates;
            status = "xenos_container_found";
            note = "candidate_is_compatible_with_xenosrecomp_scan";

            const XenosContainerCandidate& first = candidates.front();
            first_offset = std::to_string(first.offset);
            first_flags = "0x" + FormatHex32(first.flags);
            first_virtual_size = std::to_string(first.virtual_size);
            first_physical_size = std::to_string(first.physical_size);
            first_constant_table_offset = std::to_string(first.constant_table_offset);
            first_definition_table_offset = std::to_string(first.definition_table_offset);
            first_shader_offset = std::to_string(first.shader_offset);
        }

        report << EscapeTsv(blob.bank_name)
               << '\t' << blob.blob_index
               << '\t' << EscapeTsv(blob.input_path.generic_string())
               << '\t' << candidates.size()
               << '\t' << first_offset
               << '\t' << first_flags
               << '\t' << first_virtual_size
               << '\t' << first_physical_size
               << '\t' << first_constant_table_offset
               << '\t' << first_definition_table_offset
               << '\t' << first_shader_offset
               << '\t' << status
               << '\t' << note
               << '\n';
    }

    if (!WriteTextFile(active_summary->report_path, report.str(), error)) {
        return false;
    }

    return true;
}

std::string DescribeShaderCoverageSummary(const ShaderSbkCoverageSummary& summary) {
    std::ostringstream stream;
    for (std::size_t index = 0; index < summary.per_target.size(); ++index) {
        if (index != 0) {
            stream << ' ';
        }
        stream << summary.per_target[index].target << '='
               << summary.per_target[index].covered << '/' << summary.blob_count;
    }
    return stream.str();
}

bool VerifyTranslatedShaderCoverage(const ShaderSbkCoverageOptions& options,
                                    ShaderSbkCoverageSummary* summary,
                                    std::string* error) {
    ShaderSbkCoverageSummary local_summary;
    ShaderSbkCoverageSummary* const active_summary =
        summary != nullptr ? summary : &local_summary;
    *active_summary = {};

    if (options.shader_root.empty()) {
        if (error != nullptr) {
            *error = "shader_translation_coverage=REJECTED missing_shader_root";
        }
        return false;
    }

    active_summary->shader_root = std::filesystem::absolute(options.shader_root);
    active_summary->extract_manifest_path = std::filesystem::absolute(
        options.extract_manifest_path.empty()
            ? active_summary->shader_root / "extract_manifest.tsv"
            : options.extract_manifest_path);
    active_summary->report_path = std::filesystem::absolute(
        options.report_path.empty() ? active_summary->shader_root / "compile_report.tsv"
                                    : options.report_path);

    std::vector<std::string> targets;
    if (!ResolveTranslateTargets(options.targets, &targets, error)) {
        return false;
    }
    active_summary->resolved_targets = JoinCsv(targets);

    std::vector<TranslateBlobRecord> blobs;
    if (!ReadExtractManifestBlobs(active_summary->extract_manifest_path, &blobs, error)) {
        return false;
    }
    if (blobs.empty()) {
        if (error != nullptr) {
            *error = "shader_translation_coverage=REJECTED no_blob_rows file=" +
                     active_summary->extract_manifest_path.string();
        }
        return false;
    }
    active_summary->blob_count = blobs.size();

    std::vector<std::string> report_header;
    std::vector<std::vector<std::string>> report_rows;
    if (!ReadTsvTable(active_summary->report_path, &report_header, &report_rows, error)) {
        return false;
    }

    std::size_t bank_column = 0;
    std::size_t blob_index_column = 0;
    std::size_t target_column = 0;
    std::size_t status_column = 0;
    std::size_t note_column = 0;
    if (!ResolveTsvColumn(report_header, "bank", active_summary->report_path, &bank_column,
                          error) ||
        !ResolveTsvColumn(report_header, "blob_index", active_summary->report_path,
                          &blob_index_column, error) ||
        !ResolveTsvColumn(report_header, "target", active_summary->report_path, &target_column,
                          error) ||
        !ResolveTsvColumn(report_header, "status", active_summary->report_path, &status_column,
                          error) ||
        !ResolveTsvColumn(report_header, "note", active_summary->report_path, &note_column,
                          error)) {
        return false;
    }

    struct ReportStatus {
        std::string bank;
        std::string blob_index;
        std::string target;
        std::string status;
        std::string note;
    };
    std::unordered_map<std::string, ReportStatus> report_status;
    const std::size_t widest_column = std::max(
        {bank_column, blob_index_column, target_column, status_column, note_column});
    for (const std::vector<std::string>& row : report_rows) {
        if (row.size() <= widest_column) {
            continue;
        }
        ReportStatus entry;
        entry.bank = row[bank_column];
        entry.blob_index = row[blob_index_column];
        entry.target = ToLowerAscii(row[target_column]);
        entry.status = row[status_column];
        entry.note = row[note_column];
        report_status.insert_or_assign(
            BuildCoverageRowKey(entry.bank, entry.blob_index, entry.target), std::move(entry));
    }
    active_summary->report_row_count = report_status.size();

    std::unordered_set<std::string> known_blobs;
    known_blobs.reserve(blobs.size());
    for (const TranslateBlobRecord& blob : blobs) {
        known_blobs.insert(blob.bank_name + '\t' + std::to_string(blob.blob_index));
    }

    for (const std::string& target : targets) {
        ShaderSbkCoverageTargetSummary target_summary;
        target_summary.target = target;

        for (const TranslateBlobRecord& blob : blobs) {
            const std::string label = target + "/" + blob.bank_name + "#" +
                                      std::to_string(blob.blob_index) + " (" +
                                      DescribeShaderStage(blob.stage) + " " + blob.name_hint +
                                      ")";
            const std::filesystem::path binary_path = BuildTranslatedArtifactPath(
                active_summary->shader_root, target, blob.bank_name, blob.input_path);

            const bool binary_ok = CheckTranslatedBinary(binary_path, target, label,
                                                         &active_summary->failures);
            if (binary_ok) {
                CheckTranslatedSidecar(BuildTranslatedHlslPath(binary_path), target, label,
                                       &active_summary->failures);
            }

            // Vertex blobs must also carry the hardware-tessellation variant
            // ("<stem>.tess.<target>.bin"). It is compiled from the SAME HLSL
            // (dxc -D REFII_TESSELLATION_VARIANT=1) and shares the normal
            // artifact's sidecar, so only the binary itself is checked.
            if (blob.stage == ShaderStage::kVertex && target != "godot") {
                const std::filesystem::path tess_path = BuildTranslatedTessArtifactPath(
                    active_summary->shader_root, target, blob.bank_name, blob.input_path);
                (void)CheckTranslatedBinary(tess_path, target,
                                            std::string(label) + " [tess variant]",
                                            &active_summary->failures);
            }

            const auto entry = report_status.find(BuildCoverageRowKey(
                blob.bank_name, std::to_string(blob.blob_index), target));
            if (entry == report_status.end()) {
                active_summary->failures.push_back(
                    label + ": no compile-report row for this blob/target pair");
            } else if (entry->second.status != "ok") {
                active_summary->failures.push_back(label + ": compile report status=" +
                                                   entry->second.status + " note=" +
                                                   entry->second.note);
            } else if (binary_ok) {
                ++target_summary.covered;
            }
        }

        // Stale artifacts: report rows the manifest no longer knows about.
        for (const auto& [key, entry] : report_status) {
            (void)key;
            if (entry.target != target) {
                continue;
            }
            if (known_blobs.contains(entry.bank + '\t' + entry.blob_index)) {
                continue;
            }
            active_summary->failures.push_back(
                target + "/" + entry.bank + "#" + entry.blob_index +
                ": compile-report row has no matching blob in " +
                active_summary->extract_manifest_path.filename().string() + " (stale report)");
        }

        active_summary->per_target.push_back(std::move(target_summary));
    }

    if (!active_summary->failures.empty()) {
        if (error != nullptr) {
            std::ostringstream stream;
            stream << "shader_translation_coverage=REJECTED blobs="
                   << active_summary->blob_count << " targets="
                   << active_summary->resolved_targets << " covered="
                   << DescribeShaderCoverageSummary(*active_summary)
                   << " failures=" << active_summary->failures.size();
            const std::size_t shown =
                std::min<std::size_t>(active_summary->failures.size(), 5);
            for (std::size_t index = 0; index < shown; ++index) {
                stream << "\n  " << active_summary->failures[index];
            }
            if (active_summary->failures.size() > shown) {
                stream << "\n  ... and " << (active_summary->failures.size() - shown)
                       << " more; see " << active_summary->report_path.string();
            }
            *error = stream.str();
        }
        return false;
    }

    return true;
}

}  // namespace refii::shader
