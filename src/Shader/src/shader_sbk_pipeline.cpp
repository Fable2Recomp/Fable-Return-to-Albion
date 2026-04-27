#include "shader/shader_sbk_pipeline.h"

#include "shader/sha256.h"

#include <algorithm>
#include <array>
#include <chrono>
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
#include <thread>
#include <unordered_set>
#include <unordered_map>
#include <utility>
#include <vector>

#include <zlib.h>

#if defined(__unix__) || defined(__APPLE__)
#include <fcntl.h>
#include <signal.h>
#include <sys/types.h>
#include <sys/wait.h>
#include <unistd.h>
#endif

namespace refii::shader {
namespace {

constexpr std::string_view kMagic = "ShaderBankFile";
constexpr std::array<std::string_view, 2> kExpectedBankFiles = {
    "Shaders.sbk",
    "ShadersRelease.sbk",
};

struct ParsedEntry {
    std::size_t entry_index = 0;
    std::uint32_t entry_id = 0;
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
    std::vector<std::uint32_t> payload_offsets;
    std::size_t payload_data_base = 0;
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

struct EntryKey {
    std::uint32_t stage_raw = 0;
    std::uint32_t entry_id = 0;

    bool operator==(const EntryKey& other) const {
        return stage_raw == other.stage_raw && entry_id == other.entry_id;
    }
};

struct EntryKeyHash {
    std::size_t operator()(const EntryKey& key) const {
        const std::uint64_t packed =
            (static_cast<std::uint64_t>(key.stage_raw) << 32) | key.entry_id;
        return std::hash<std::uint64_t>{}(packed);
    }
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

std::vector<std::string> BuildDefaultTranslateTargets() {
#if defined(_WIN32)
    return {"vulkan", "d3d12"};
#else
    return {"vulkan"};
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

        if (part != "vulkan" && part != "d3d12") {
            if (error != nullptr) {
                *error = "Unsupported translation target: " + part +
                         " (allowed: vulkan, d3d12, auto)";
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
            *error = "No valid translation targets selected";
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

std::uint32_t ReadLittleEndianU32(const std::vector<std::uint8_t>& bytes, std::size_t offset) {
    return static_cast<std::uint32_t>(bytes[offset]) |
           (static_cast<std::uint32_t>(bytes[offset + 1]) << 8) |
           (static_cast<std::uint32_t>(bytes[offset + 2]) << 16) |
           (static_cast<std::uint32_t>(bytes[offset + 3]) << 24);
}

bool ReadUleb128(const std::vector<std::uint8_t>& bytes,
                 std::size_t* cursor,
                 std::uint32_t* value,
                 std::string* error) {
    if (cursor == nullptr || value == nullptr) {
        if (error != nullptr) {
            *error = "internal parser state missing";
        }
        return false;
    }

    std::uint32_t decoded = 0;
    unsigned int shift = 0;
    while (true) {
        if (*cursor >= bytes.size()) {
            if (error != nullptr) {
                *error = "Unexpected end of stream while reading varint";
            }
            return false;
        }
        const std::uint8_t byte = bytes[*cursor];
        ++(*cursor);

        decoded |= static_cast<std::uint32_t>(byte & 0x7f) << shift;
        if ((byte & 0x80) == 0) {
            *value = decoded;
            return true;
        }

        shift += 7;
        if (shift >= 32) {
            if (error != nullptr) {
                *error = "Varint exceeds 32-bit range";
            }
            return false;
        }
    }
}

ShaderStage InferShaderStage(std::uint32_t stage_raw, std::string_view name_hint) {
    if (!name_hint.empty()) {
        if (name_hint.rfind("VSHADER_", 0) == 0) {
            return ShaderStage::kVertex;
        }
        if (name_hint.rfind("PSHADER_", 0) == 0) {
            return ShaderStage::kPixel;
        }
    }

    const std::uint8_t stage_tag = static_cast<std::uint8_t>(stage_raw & 0xffu);
    if (stage_tag == 2) {
        return ShaderStage::kVertex;
    }
    if (stage_tag == 1) {
        return ShaderStage::kPixel;
    }
    return ShaderStage::kUnknown;
}

bool ParseDebugEntries(ParsedBank* bank, std::size_t* cursor, std::string* error) {
    if (bank == nullptr || cursor == nullptr) {
        if (error != nullptr) {
            *error = "internal parser state missing";
        }
        return false;
    }

    const std::vector<std::uint8_t>& bytes = bank->bytes;
    if (*cursor >= bytes.size()) {
        if (error != nullptr) {
            *error = "entry table start exceeds file size";
        }
        return false;
    }

    const std::uint32_t entry_count = ReadBigEndianU32(bytes, 0x5dec);
    bank->entries.clear();
    bank->entries.reserve(entry_count);

    for (std::uint32_t index = 0; index < entry_count; ++index) {
        const auto null_it = std::find(bytes.begin() + static_cast<std::ptrdiff_t>(*cursor),
                                       bytes.end(),
                                       static_cast<std::uint8_t>(0));
        if (null_it == bytes.end()) {
            if (error != nullptr) {
                *error = "Missing entry name terminator";
            }
            return false;
        }

        const std::size_t null_offset = static_cast<std::size_t>(
            std::distance(bytes.begin(), null_it));
        if (null_offset == *cursor) {
            if (error != nullptr) {
                *error = "Encountered empty shader entry name";
            }
            return false;
        }

        ParsedEntry entry;
        entry.entry_index = index;
        entry.entry_name.assign(reinterpret_cast<const char*>(bytes.data() + *cursor),
                                null_offset - *cursor);

        *cursor = null_offset + 1;
        if (*cursor + 4 > bytes.size()) {
            if (error != nullptr) {
                *error = "Unexpected end of file while reading stage";
            }
            return false;
        }

        entry.stage_raw = ReadLittleEndianU32(bytes, *cursor);
        *cursor += 4;

        if (!ReadUleb128(bytes, cursor, &entry.entry_id, error)) {
            if (error != nullptr && !error->empty()) {
                *error = "Failed to parse entry id for " + bank->bank_name + ": " + *error;
            }
            return false;
        }

        entry.stage = InferShaderStage(entry.stage_raw, entry.entry_name);
        bank->entries.push_back(std::move(entry));
    }

    bank->has_debug_names = true;
    return true;
}

bool ParseReleaseEntries(ParsedBank* bank, std::size_t* cursor, std::string* error) {
    if (bank == nullptr || cursor == nullptr) {
        if (error != nullptr) {
            *error = "internal parser state missing";
        }
        return false;
    }

    const std::vector<std::uint8_t>& bytes = bank->bytes;
    if (*cursor >= bytes.size()) {
        if (error != nullptr) {
            *error = "entry table start exceeds file size";
        }
        return false;
    }

    const std::uint32_t entry_count = ReadBigEndianU32(bytes, 0x5dec);
    bank->entries.clear();
    bank->entries.reserve(entry_count);

    for (std::uint32_t index = 0; index < entry_count; ++index) {
        if (*cursor + 8 > bytes.size()) {
            if (error != nullptr) {
                *error = "Unexpected end of file while reading release entry metadata";
            }
            return false;
        }

        ParsedEntry entry;
        entry.entry_index = index;
        const std::uint32_t entry_hash = ReadLittleEndianU32(bytes, *cursor);
        entry.entry_hash_hex = FormatHex32(entry_hash);
        *cursor += 4;

        entry.stage_raw = ReadLittleEndianU32(bytes, *cursor);
        *cursor += 4;

        if (!ReadUleb128(bytes, cursor, &entry.entry_id, error)) {
            if (error != nullptr && !error->empty()) {
                *error = "Failed to parse entry id for " + bank->bank_name + ": " + *error;
            }
            return false;
        }

        entry.stage = InferShaderStage(entry.stage_raw, {});
        bank->entries.push_back(std::move(entry));
    }

    bank->has_debug_names = false;
    return true;
}

bool FindPayloadOffsetTable(const ParsedBank& bank,
                            std::size_t hint_cursor,
                            std::size_t* offset_table_start,
                            std::size_t* payload_data_base,
                            std::vector<std::uint32_t>* offsets,
                            std::string* error) {
    if (offset_table_start == nullptr || payload_data_base == nullptr || offsets == nullptr) {
        if (error != nullptr) {
            *error = "internal parser state missing";
        }
        return false;
    }

    const std::vector<std::uint8_t>& bytes = bank.bytes;
    if (bytes.size() < 16) {
        if (error != nullptr) {
            *error = "bank is too small to contain payload tables";
        }
        return false;
    }

    struct Candidate {
        std::size_t start = 0;
        std::size_t count = 0;
        std::size_t data_base = 0;
        std::uint32_t max_value = 0;
        std::vector<std::uint32_t> values;
    };

    std::optional<Candidate> best;

    const std::size_t search_begin = hint_cursor > 48 ? hint_cursor - 48 : 0;
    const std::size_t search_end = std::min(bytes.size() - 8, hint_cursor + 96);

    for (std::size_t start = search_begin; start <= search_end; ++start) {
        std::vector<std::uint32_t> values;
        values.reserve(512);

        std::uint32_t max_value = 0;
        std::size_t cursor = start;
        while (cursor + 4 <= bytes.size()) {
            const std::uint32_t value = ReadBigEndianU32(bytes, cursor);
            if (value >= bytes.size()) {
                break;
            }
            values.push_back(value);
            max_value = std::max(max_value, value);
            cursor += 4;
            if (values.size() > 8192) {
                break;
            }
        }

        if (values.size() < 64) {
            continue;
        }

        const std::size_t after_table = start + values.size() * 4;
        if (after_table < 4 || after_table > bytes.size()) {
            continue;
        }

        const std::size_t data_base = after_table - 4;
        const std::uint32_t expected_extent =
            static_cast<std::uint32_t>(bytes.size() - data_base);
        if (max_value != expected_extent) {
            continue;
        }

        Candidate candidate;
        candidate.start = start;
        candidate.count = values.size();
        candidate.data_base = data_base;
        candidate.max_value = max_value;
        candidate.values = std::move(values);

        if (!best.has_value() || candidate.count > best->count ||
            (candidate.count == best->count &&
             std::abs(static_cast<long long>(candidate.start) -
                      static_cast<long long>(hint_cursor)) <
                 std::abs(static_cast<long long>(best->start) -
                          static_cast<long long>(hint_cursor)))) {
            best = std::move(candidate);
        }
    }

    if (!best.has_value()) {
        if (error != nullptr) {
            *error = "Failed to locate payload offset table near entry metadata in " +
                     bank.bank_name;
        }
        return false;
    }

    *offset_table_start = best->start;
    *payload_data_base = best->data_base;
    *offsets = std::move(best->values);
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

    if (bank->bytes.size() < 0x5df4) {
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

    std::size_t cursor = 0x5df4;
    const bool is_debug_bank = bank_path.filename() == "Shaders.sbk";
    if (is_debug_bank) {
        if (!ParseDebugEntries(bank, &cursor, error)) {
            return false;
        }
    } else {
        if (!ParseReleaseEntries(bank, &cursor, error)) {
            return false;
        }
    }

    std::size_t offset_table_start = 0;
    if (!FindPayloadOffsetTable(
            *bank, cursor, &offset_table_start, &bank->payload_data_base, &bank->payload_offsets, error)) {
        return false;
    }

    if (bank->payload_offsets.empty()) {
        if (error != nullptr) {
            *error = "Parsed zero payload offsets in " + bank->bank_name;
        }
        return false;
    }

    (void)offset_table_start;
    return true;
}

void CrossFillEntryNamesAndHashes(std::vector<ParsedBank>* banks) {
    if (banks == nullptr) {
        return;
    }

    std::unordered_map<EntryKey, std::string, EntryKeyHash> key_to_name;
    std::unordered_map<EntryKey, std::string, EntryKeyHash> key_to_hash;

    for (const ParsedBank& bank : *banks) {
        for (const ParsedEntry& entry : bank.entries) {
            const EntryKey key{entry.stage_raw, entry.entry_id};
            if (!entry.entry_name.empty() && !key_to_name.contains(key)) {
                key_to_name.emplace(key, entry.entry_name);
            }
            if (!entry.entry_hash_hex.empty() && !key_to_hash.contains(key)) {
                key_to_hash.emplace(key, entry.entry_hash_hex);
            }
        }
    }

    for (ParsedBank& bank : *banks) {
        for (ParsedEntry& entry : bank.entries) {
            const EntryKey key{entry.stage_raw, entry.entry_id};
            if (entry.entry_name.empty()) {
                const auto name_it = key_to_name.find(key);
                if (name_it != key_to_name.end()) {
                    entry.entry_name = name_it->second;
                }
            }
            if (entry.entry_hash_hex.empty()) {
                const auto hash_it = key_to_hash.find(key);
                if (hash_it != key_to_hash.end()) {
                    entry.entry_hash_hex = hash_it->second;
                }
            }
            entry.stage = InferShaderStage(entry.stage_raw, entry.entry_name);
        }
    }
}

bool LooksLikeZlibHeader(std::uint8_t cmf, std::uint8_t flg) {
    if (cmf != 0x78) {
        return false;
    }
    if (flg != 0x01 && flg != 0x5e && flg != 0x9c && flg != 0xda) {
        return false;
    }
    return true;
}

bool ParseCompressedChunkChain(const ParsedBank& bank,
                               std::size_t data_base,
                               std::vector<CompressedChunkHeader>* chunks,
                               bool require_full_extent) {
    if (chunks == nullptr) {
        return false;
    }

    chunks->clear();

    if (data_base >= bank.bytes.size()) {
        return false;
    }

    const std::size_t extent = bank.bytes.size() - data_base;
    std::size_t cursor = 0;

    while (cursor + 6 <= extent) {
        const std::uint32_t compressed_size =
            ReadBigEndianU32(bank.bytes, data_base + cursor);
        if (compressed_size == 0) {
            break;
        }

        const std::size_t chunk_end =
            cursor + 4 + static_cast<std::size_t>(compressed_size);
        if (chunk_end > extent) {
            break;
        }

        const std::uint8_t cmf = bank.bytes[data_base + cursor + 4];
        const std::uint8_t flg = bank.bytes[data_base + cursor + 5];
        if (!LooksLikeZlibHeader(cmf, flg)) {
            break;
        }

        CompressedChunkHeader chunk;
        chunk.offset = static_cast<std::uint32_t>(cursor);
        chunk.compressed_size = compressed_size;
        chunks->push_back(chunk);

        cursor = chunk_end;
    }

    if (chunks->empty()) {
        return false;
    }
    if (require_full_extent && cursor != extent) {
        return false;
    }

    return true;
}

bool SelectCompressedDataBase(const ParsedBank& bank,
                              std::size_t hinted_data_base,
                              std::size_t* selected_data_base,
                              std::vector<CompressedChunkHeader>* chunks,
                              std::string* error) {
    if (selected_data_base == nullptr || chunks == nullptr) {
        if (error != nullptr) {
            *error = "internal compressed chunk selection state missing";
        }
        return false;
    }

    std::vector<std::size_t> candidate_bases;
    candidate_bases.push_back(hinted_data_base);
    if (hinted_data_base >= 4) {
        candidate_bases.push_back(hinted_data_base - 4);
    }
    if (hinted_data_base + 4 < bank.bytes.size()) {
        candidate_bases.push_back(hinted_data_base + 4);
    }
    std::sort(candidate_bases.begin(), candidate_bases.end());
    candidate_bases.erase(
        std::unique(candidate_bases.begin(), candidate_bases.end()),
        candidate_bases.end());

    bool found = false;
    std::size_t best_base = 0;
    std::vector<CompressedChunkHeader> best_chunks;

    for (const std::size_t candidate_base : candidate_bases) {
        std::vector<CompressedChunkHeader> candidate_chunks;
        if (!ParseCompressedChunkChain(
                bank, candidate_base, &candidate_chunks, true)) {
            continue;
        }

        if (!found || candidate_chunks.size() > best_chunks.size()) {
            found = true;
            best_base = candidate_base;
            best_chunks = std::move(candidate_chunks);
        }
    }

    if (!found) {
        if (error != nullptr) {
            *error = "Failed to locate compressed shader chunk chain in " +
                     bank.bank_name + " near parsed payload base";
        }
        return false;
    }

    *selected_data_base = best_base;
    *chunks = std::move(best_chunks);
    return true;
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
                                          std::uint32_t source_offset,
                                          std::uint32_t source_size,
                                          std::string_view name_hint) {
    const std::string bank_stem = std::filesystem::path(bank_name).stem().string();
    std::ostringstream filename;
    filename << "blob_" << std::setfill('0') << std::setw(4) << blob_index << "_"
             << std::hex << std::nouppercase << std::setfill('0') << std::setw(6)
             << source_offset << "_" << std::setw(6)
             << static_cast<std::uint32_t>(source_offset + source_size) << "_"
             << SanitizeNameForFile(name_hint) << ".bin";
    return extract_root / bank_stem / "blobs" / filename.str();
}

bool ExtractBankBlobs(const ParsedBank& bank,
                      const std::filesystem::path& extract_root,
                      std::vector<ShaderSbkBlobMetadata>* blobs,
                      std::string* error) {
    if (blobs == nullptr) {
        if (error != nullptr) {
            *error = "blob metadata output missing";
        }
        return false;
    }

    std::size_t compressed_data_base = 0;
    std::vector<CompressedChunkHeader> chunk_headers;
    if (!SelectCompressedDataBase(
            bank, bank.payload_data_base, &compressed_data_base, &chunk_headers, error)) {
        return false;
    }

    if (chunk_headers.empty()) {
        if (error != nullptr) {
            *error = "No compressed shader chunks found in " + bank.bank_name;
        }
        return false;
    }

    std::vector<std::string> vertex_names;
    std::vector<std::string> pixel_names;
    std::vector<std::string> unknown_names;
    vertex_names.reserve(bank.entries.size());
    pixel_names.reserve(bank.entries.size());
    unknown_names.reserve(bank.entries.size());

    for (const ParsedEntry& entry : bank.entries) {
        const std::string hint = BuildEntryNameHint(entry);
        switch (entry.stage) {
        case ShaderStage::kVertex:
            vertex_names.push_back(hint);
            break;
        case ShaderStage::kPixel:
            pixel_names.push_back(hint);
            break;
        case ShaderStage::kUnknown:
        default:
            unknown_names.push_back(hint);
            break;
        }
    }

    std::size_t vertex_name_index = 0;
    std::size_t pixel_name_index = 0;
    std::size_t unknown_name_index = 0;

    std::size_t extracted_count = 0;
    for (const CompressedChunkHeader& chunk : chunk_headers) {
        const std::size_t absolute_start = compressed_data_base + chunk.offset + 4;
        if (absolute_start + chunk.compressed_size > bank.bytes.size()) {
            if (error != nullptr) {
                *error = "Compressed chunk exceeds file bounds in " + bank.bank_name;
            }
            return false;
        }

        std::vector<std::uint8_t> decompressed;
        if (!InflateCompressedChunk(bank.bytes.data() + absolute_start,
                                    chunk.compressed_size,
                                    &decompressed,
                                    error)) {
            if (error != nullptr && !error->empty()) {
                *error = "Failed to decode compressed shader chunk at 0x" +
                         FormatHex32(chunk.offset) + " in " + bank.bank_name + ": " +
                         *error;
            }
            return false;
        }

        for (std::size_t local_offset = 0;
             local_offset + 0x24 <= decompressed.size();
             local_offset += 4) {
            std::size_t container_size = 0;
            ShaderStage container_stage = ShaderStage::kUnknown;
            if (!TryParseXenosContainer(
                    decompressed, local_offset, &container_size, &container_stage)) {
                continue;
            }

            std::string name_hint;
            switch (container_stage) {
            case ShaderStage::kVertex:
                if (vertex_name_index < vertex_names.size()) {
                    name_hint = vertex_names[vertex_name_index++];
                }
                break;
            case ShaderStage::kPixel:
                if (pixel_name_index < pixel_names.size()) {
                    name_hint = pixel_names[pixel_name_index++];
                }
                break;
            case ShaderStage::kUnknown:
            default:
                if (unknown_name_index < unknown_names.size()) {
                    name_hint = unknown_names[unknown_name_index++];
                }
                break;
            }
            if (name_hint.empty()) {
                name_hint = "xenos_container";
            }

            const std::uint32_t source_offset = static_cast<std::uint32_t>(
                chunk.offset + local_offset);
            const std::uint32_t source_size =
                static_cast<std::uint32_t>(container_size);

            const std::filesystem::path output_path = BuildBlobOutputPath(
                extract_root,
                bank.bank_name,
                blobs->size(),
                source_offset,
                source_size,
                name_hint);

            if (!WriteBinaryFile(output_path,
                                 decompressed.data() +
                                     static_cast<std::ptrdiff_t>(local_offset),
                                 container_size,
                                 error)) {
                if (error != nullptr && !error->empty()) {
                    *error = "Failed to write decoded shader container from " +
                             bank.bank_name + ": " + *error;
                }
                return false;
            }

            ShaderSbkBlobMetadata metadata;
            metadata.bank_name = bank.bank_name;
            metadata.blob_index = blobs->size();
            metadata.source_offset = source_offset;
            metadata.source_size = source_size;
            metadata.stage = container_stage;
            metadata.name_hint = name_hint;
            metadata.output_path = output_path;
            metadata.sha256 = refii::shader::crypto::ComputeSha256Hex(
                decompressed.data() +
                    static_cast<std::ptrdiff_t>(local_offset),
                container_size);
            blobs->push_back(std::move(metadata));

            ++extracted_count;
            local_offset += container_size - 4;
        }
    }

    if (extracted_count == 0) {
        if (error != nullptr) {
            *error = "Decoded zero Xenos shader containers from " + bank.bank_name;
        }
        return false;
    }

    return true;
}

bool WriteExtractManifest(const std::filesystem::path& manifest_path,
                          const std::vector<ParsedBank>& banks,
                          const std::vector<ShaderSbkBlobMetadata>& blobs,
                          std::string* error) {
    std::ostringstream manifest;
    manifest
        << "row_type\tbank\tentry_index\tblob_index\tentry_id\tstage\tstage_raw\tentry_hash"
           "\tentry_name\tsource_offset\tsource_size\toutput_path\tsha256\n";

    for (const ParsedBank& bank : banks) {
        for (const ParsedEntry& entry : bank.entries) {
            manifest << "entry"
                     << '\t' << EscapeTsv(bank.bank_name)
                     << '\t' << entry.entry_index
                     << '\t'
                     << '\t' << entry.entry_id
                     << '\t' << DescribeShaderStage(entry.stage)
                     << '\t' << entry.stage_raw
                     << '\t' << EscapeTsv(entry.entry_hash_hex)
                     << '\t' << EscapeTsv(entry.entry_name)
                     << '\t'
                     << '\t'
                     << '\t'
                     << '\t'
                     << '\n';
        }
    }

    for (const ShaderSbkBlobMetadata& blob : blobs) {
        manifest << "blob"
                 << '\t' << EscapeTsv(blob.bank_name)
                 << '\t'
                 << '\t' << blob.blob_index
                 << '\t'
                 << '\t' << DescribeShaderStage(blob.stage)
                 << '\t'
                 << '\t'
                 << '\t' << EscapeTsv(blob.name_hint)
                 << '\t' << blob.source_offset
                 << '\t' << blob.source_size
                 << '\t' << EscapeTsv(blob.output_path.generic_string())
                 << '\t' << blob.sha256
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

#if defined(__unix__) || defined(__APPLE__)
bool RunProcessWithTimeoutPosix(const std::vector<std::string>& args,
                                std::uint64_t timeout_ms,
                                const std::filesystem::path* output_log_path,
                                int* exit_code,
                                bool* timed_out,
                                std::string* error) {
    if (args.empty()) {
        if (error != nullptr) {
            *error = "translator command is empty";
        }
        return false;
    }

    std::vector<char*> argv;
    argv.reserve(args.size() + 1);
    for (const std::string& arg : args) {
        argv.push_back(const_cast<char*>(arg.c_str()));
    }
    argv.push_back(nullptr);

    const pid_t pid = fork();
    if (pid < 0) {
        if (error != nullptr) {
            *error = "fork() failed";
        }
        return false;
    }

    if (pid == 0) {
        if (output_log_path != nullptr && !output_log_path->empty()) {
            const int log_fd = open(output_log_path->c_str(),
                                    O_CREAT | O_WRONLY | O_TRUNC,
                                    0644);
            if (log_fd < 0) {
                _exit(126);
            }
            dup2(log_fd, STDOUT_FILENO);
            dup2(log_fd, STDERR_FILENO);
            close(log_fd);
        }
        execvp(argv[0], argv.data());
        _exit(127);
    }

    const auto start = std::chrono::steady_clock::now();
    int status = 0;
    while (true) {
        const pid_t waited = waitpid(pid, &status, WNOHANG);
        if (waited == pid) {
            if (timed_out != nullptr) {
                *timed_out = false;
            }
            if (exit_code != nullptr) {
                if (WIFEXITED(status)) {
                    *exit_code = WEXITSTATUS(status);
                } else if (WIFSIGNALED(status)) {
                    *exit_code = 128 + WTERMSIG(status);
                } else {
                    *exit_code = 1;
                }
            }
            return true;
        }
        if (waited < 0) {
            if (error != nullptr) {
                *error = "waitpid() failed";
            }
            return false;
        }

        const auto elapsed_ms = static_cast<std::uint64_t>(
            std::chrono::duration_cast<std::chrono::milliseconds>(
                std::chrono::steady_clock::now() - start)
                .count());
        if (timeout_ms != 0 && elapsed_ms >= timeout_ms) {
            kill(pid, SIGKILL);
            waitpid(pid, &status, 0);
            if (timed_out != nullptr) {
                *timed_out = true;
            }
            if (exit_code != nullptr) {
                *exit_code = -1;
            }
            return true;
        }

        std::this_thread::sleep_for(std::chrono::milliseconds(5));
    }
}
#endif

std::string ShellQuote(std::string_view value) {
    std::string quoted;
    quoted.reserve(value.size() + 2);
    quoted.push_back('"');
    for (const char ch : value) {
        if (ch == '"' || ch == '\\') {
            quoted.push_back('\\');
        }
        quoted.push_back(ch);
    }
    quoted.push_back('"');
    return quoted;
}

bool RunTranslatorCommand(const std::vector<std::string>& args,
                          std::uint64_t timeout_ms,
                          const std::filesystem::path* output_log_path,
                          int* exit_code,
                          bool* timed_out,
                          std::string* error) {
    if (output_log_path != nullptr && !output_log_path->empty()) {
        if (!PrepareTextFile(*output_log_path, error)) {
            return false;
        }
    }
#if defined(__unix__) || defined(__APPLE__)
    return RunProcessWithTimeoutPosix(
        args, timeout_ms, output_log_path, exit_code, timed_out, error);
#else
    std::ostringstream command;
    for (std::size_t i = 0; i < args.size(); ++i) {
        if (i != 0) {
            command << ' ';
        }
        command << ShellQuote(args[i]);
    }
    if (output_log_path != nullptr && !output_log_path->empty()) {
        command << " > " << ShellQuote(output_log_path->string()) << " 2>&1";
    }
    const int code = std::system(command.str().c_str());
    if (timed_out != nullptr) {
        *timed_out = false;
    }
    if (exit_code != nullptr) {
        *exit_code = code;
    }
    (void)timeout_ms;
    (void)error;
    return true;
#endif
}

std::filesystem::path BuildTranslatedHlslPath(const std::filesystem::path& output_path) {
    return std::filesystem::path(output_path.string() + ".hlsl");
}

std::filesystem::path BuildTranslatorLogPath(const std::filesystem::path& translate_root,
                                             std::string_view target,
                                             std::string_view bank_name,
                                             std::string_view output_stem) {
    return translate_root /
           "logs" /
           std::string(target) /
           std::string(bank_name) /
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
                               std::string* error) {
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
        for (const std::string_view bank_file : kExpectedBankFiles) {
            const std::filesystem::path bank_stem =
                std::filesystem::path(bank_file).stem();
            std::filesystem::remove_all(extract_out_dir / bank_stem);
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

    for (const std::string_view bank_file : kExpectedBankFiles) {
        const std::filesystem::path bank_path = input_dir / bank_file;
        if (!std::filesystem::exists(bank_path)) {
            if (error != nullptr) {
                *error = "Missing required shader bank: " + bank_path.string();
            }
            return false;
        }

        ParsedBank bank;
        if (!ParseBank(bank_path, &bank, error)) {
            return false;
        }
        banks.push_back(std::move(bank));
    }

    CrossFillEntryNamesAndHashes(&banks);

    std::vector<ShaderSbkBlobMetadata> blobs;
    for (const ParsedBank& bank : banks) {
        if (!ExtractBankBlobs(bank, extract_out_dir, &blobs, error)) {
            return false;
        }
        active_summary->entry_count += bank.entries.size();
    }

    if (!WriteExtractManifest(manifest_path, banks, blobs, error)) {
        return false;
    }

    active_summary->bank_count = banks.size();
    active_summary->blob_count = blobs.size();
    return true;
}

bool TranslateExtractedShaderSbk(const ShaderSbkTranslateOptions& options,
                                 ShaderSbkTranslateSummary* summary,
                                 std::string* error) {
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

    const bool translator_available = !options.translator_path.empty();

    struct CachedTranslateResult {
        std::string status;
        std::string note;
        int exit_code = 0;
        bool timed_out = false;
        std::filesystem::path output_path;
        std::filesystem::path hlsl_path;
        std::filesystem::path stderr_path;
        std::string origin_bank;
        std::size_t origin_blob_index = 0;
    };
    std::unordered_map<std::string, CachedTranslateResult> dedupe_cache;

    std::ostringstream report;
    report << "bank\tblob_index\ttarget\tstage\tname\tinput_path\toutput_path\tstatus\texit_code\t"
              "note\thlsl_path\tfailure_class\tstderr_path\t"
              "dedupe_key\tdedupe_origin_bank\tdedupe_origin_blob_index\tdedupe_mode\n";

    for (const TranslateBlobRecord& blob : blobs) {
        for (const std::string& target : targets) {
            const std::string output_stem = blob.input_path.stem().string() + "." + target;
            const std::filesystem::path output_path =
                active_summary->translate_out_dir /
                target /
                blob.bank_name /
                (output_stem + ".bin");
            const std::filesystem::path hlsl_path = BuildTranslatedHlslPath(output_path);
            const std::filesystem::path stderr_path = BuildTranslatorLogPath(
                active_summary->translate_out_dir, target, blob.bank_name, output_stem);

            std::string status;
            std::string note;
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
                        exit_code = cached->second.exit_code;
                        timed_out = cached->second.timed_out;
                        dedupe_origin_bank = cached->second.origin_bank;
                        dedupe_origin_blob_index = cached->second.origin_blob_index;

                        std::string alias_error;
                        if (!LinkOrCopyArtifact(cached->second.output_path, output_path, &alias_error) ||
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
                    std::vector<std::string> args;
                    args.reserve(11);
                    args.push_back(options.translator_path.string());
                    args.push_back("--input");
                    args.push_back(blob.input_path.string());
                    args.push_back("--target");
                    args.push_back(target);
                    args.push_back("--stage");
                    args.push_back(DescribeShaderStage(blob.stage));
                    args.push_back("--name");
                    args.push_back(blob.name_hint.empty()
                                       ? ("blob_" + std::to_string(blob.blob_index))
                                       : blob.name_hint);
                    args.push_back("--output");
                    args.push_back(output_path.string());

                    std::string run_error;
                    if (!RunTranslatorCommand(
                            args,
                            options.translator_timeout_ms,
                            &stderr_path,
                            &exit_code,
                            &timed_out,
                            &run_error)) {
                        status = "failed";
                        note = run_error.empty() ? "translator_launch_failed" : run_error;
                    } else if (timed_out) {
                        status = "timeout";
                        note = "translator_timeout";
                    } else if (exit_code != 0) {
                        status = "failed";
                        note = "translator_nonzero_exit";
                    } else if (!std::filesystem::exists(output_path)) {
                        status = "failed";
                        note = "translator_did_not_write_output";
                    } else {
                        status = "ok";
                    }

                    if (options.dedupe_identical_blobs) {
                        CachedTranslateResult cache_entry;
                        cache_entry.status = status;
                        cache_entry.note = note;
                        cache_entry.exit_code = exit_code;
                        cache_entry.timed_out = timed_out;
                        cache_entry.output_path = output_path;
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
                   << '\n';
        }
    }

    if (!WriteTextFile(active_summary->report_path, report.str(), error)) {
        return false;
    }

    return true;
}

bool BuildShaderSbkWorkspace(const ShaderSbkBuildOptions& options,
                             ShaderSbkBuildSummary* summary,
                             std::string* error) {
    ShaderSbkBuildSummary local_summary;
    ShaderSbkBuildSummary* const active_summary = summary != nullptr ? summary : &local_summary;
    *active_summary = {};

    if (!ExtractShaderSbkWorkspace(options.extract, &active_summary->extract, error)) {
        return false;
    }

    ShaderSbkTranslateOptions translate_options = options.translate;
    if (translate_options.extract_manifest_path.empty()) {
        translate_options.extract_manifest_path = options.extract.manifest_path;
    }

    if (!TranslateExtractedShaderSbk(translate_options, &active_summary->translate, error)) {
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

}  // namespace refii::shader
