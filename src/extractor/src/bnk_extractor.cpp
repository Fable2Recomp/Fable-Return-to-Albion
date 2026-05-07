#include "extractor/bnk_extractor.h"

#include <zlib.h>

#include <algorithm>
#include <array>
#include <cctype>
#include <cstdint>
#include <filesystem>
#include <fstream>
#include <iomanip>
#include <limits>
#include <sstream>
#include <string>
#include <string_view>
#include <system_error>
#include <unordered_map>
#include <unordered_set>
#include <vector>

namespace refii::bnk {
namespace {

struct BankEntry {
    std::string relative_path;
    std::uint32_t offset = 0;
    std::uint32_t size = 0;
    std::uint32_t stored_size = 0;
    bool is_compressed = false;
    std::vector<std::uint32_t> extra_words;
};

struct BankTable {
    BankFormatVersion format = BankFormatVersion::kUnknown;
    std::uint32_t data_base_offset = 0;
    bool entry_offsets_are_absolute = false;
    bool file_data_compressed = false;
    std::vector<BankEntry> entries;
};

struct CompressedChunk {
    std::uint32_t compressed_size = 0;
    std::uint32_t uncompressed_size = 0;
    std::vector<std::uint8_t> bytes;
};

struct ExtractionContext {
    std::filesystem::path output_root;
    std::filesystem::path conflict_report_path;
    bool extract_payloads = true;
    bool extract_nested_banks = true;
    BankExtractionSummary* summary = nullptr;
};

struct VfsDatabaseRow {
    std::uint32_t hash = 0;
    std::string alias;
    std::string candidate;
};

struct VfsBankRecord {
    std::string alias;
    std::string candidate;
    std::string manifest_root;
};

struct VfsLuaRecord {
    std::string alias;
    std::string candidate;
    std::string kind;
};

struct PlannedBankEntry {
    std::size_t entry_index = 0;
    std::filesystem::path relative_path;
    std::filesystem::path host_relative_path;
    bool skip_duplicate = false;
};

struct BankDuplicateConflict {
    std::filesystem::path bank_path;
    std::filesystem::path lowercase_path;
    std::string first_entry_path;
    std::string duplicate_entry_path;
    std::uint64_t first_size = 0;
    std::uint64_t duplicate_size = 0;
    bool byte_equal = false;
    std::string action;
    std::filesystem::path conflict_path;
};

constexpr std::size_t kV3HeaderSize = 9;
constexpr std::size_t kChunkHeaderSize = 8;
constexpr std::size_t kCompressedChunkSize = 0x8000;

std::uint32_t ReadBigEndianU32(const std::vector<std::uint8_t>& bytes, std::size_t offset) {
    return (static_cast<std::uint32_t>(bytes[offset]) << 24) |
           (static_cast<std::uint32_t>(bytes[offset + 1]) << 16) |
           (static_cast<std::uint32_t>(bytes[offset + 2]) << 8) |
           static_cast<std::uint32_t>(bytes[offset + 3]);
}

std::uint32_t ReadBigEndianU32(const std::array<std::uint8_t, 16>& bytes, std::size_t offset) {
    return (static_cast<std::uint32_t>(bytes[offset]) << 24) |
           (static_cast<std::uint32_t>(bytes[offset + 1]) << 16) |
           (static_cast<std::uint32_t>(bytes[offset + 2]) << 8) |
           static_cast<std::uint32_t>(bytes[offset + 3]);
}

bool ReadExact(std::istream& stream,
               void* data,
               std::size_t size,
               const std::string& description,
               std::string* error) {
    if (size == 0) {
        return true;
    }

    stream.read(reinterpret_cast<char*>(data), static_cast<std::streamsize>(size));
    if (!stream) {
        if (error != nullptr) {
            *error = "failed to read " + description;
        }
        return false;
    }
    return true;
}

bool InflateWithWindowBits(const std::uint8_t* compressed_data,
                           std::size_t compressed_size,
                           std::size_t expected_size,
                           int window_bits,
                           int flush_mode,
                           std::vector<std::uint8_t>* output,
                           std::string* error) {
    if (compressed_data == nullptr && compressed_size != 0) {
        if (error != nullptr) {
            *error = "invalid compressed buffer";
        }
        return false;
    }

    if (compressed_size > static_cast<std::size_t>(std::numeric_limits<uInt>::max()) ||
        expected_size > static_cast<std::size_t>(std::numeric_limits<uInt>::max())) {
        if (error != nullptr) {
            *error = "compressed buffer exceeds zlib limits";
        }
        return false;
    }

    output->assign(expected_size, 0);

    z_stream zstream{};
    zstream.next_in = const_cast<Bytef*>(reinterpret_cast<const Bytef*>(compressed_data));
    zstream.avail_in = static_cast<uInt>(compressed_size);

    const int init_result = inflateInit2(&zstream, window_bits);
    if (init_result != Z_OK) {
        if (error != nullptr) {
            *error = "inflateInit2 failed";
        }
        return false;
    }

    int inflate_result = Z_OK;
    std::size_t produced = 0;
    while (produced < expected_size) {
        const std::size_t output_remaining = expected_size - produced;
        const std::size_t output_step =
            std::min<std::size_t>(output_remaining, std::numeric_limits<uInt>::max());
        zstream.next_out = reinterpret_cast<Bytef*>(output->data() + produced);
        zstream.avail_out = static_cast<uInt>(output_step);

        const std::size_t previous_produced = produced;
        inflate_result = inflate(&zstream, flush_mode);
        produced += output_step - zstream.avail_out;

        if (inflate_result == Z_STREAM_END) {
            break;
        }
        if (inflate_result != Z_OK && inflate_result != Z_BUF_ERROR) {
            inflateEnd(&zstream);
            if (error != nullptr) {
                *error = "inflate failed with code " + std::to_string(inflate_result);
            }
            return false;
        }

        if (produced == previous_produced &&
            (zstream.avail_in == 0 || inflate_result == Z_BUF_ERROR)) {
            break;
        }
    }

    inflateEnd(&zstream);

    if (produced != expected_size) {
        if (error != nullptr) {
            std::ostringstream message;
            message << "inflate produced " << produced << " bytes, expected " << expected_size;
            *error = message.str();
        }
        return false;
    }

    return true;
}

bool InflateTryAll(const std::uint8_t* compressed_data,
                   std::size_t compressed_size,
                   std::size_t expected_size,
                   int flush_mode,
                   std::vector<std::uint8_t>* output,
                   std::string* error) {
    std::string last_error;
    for (const int window_bits : {15, -15, 31}) {
        std::string attempt_error;
        if (InflateWithWindowBits(compressed_data,
                                  compressed_size,
                                  expected_size,
                                  window_bits,
                                  flush_mode,
                                  output,
                                  &attempt_error)) {
            return true;
        }
        last_error = std::move(attempt_error);
    }

    if (error != nullptr) {
        *error = last_error.empty() ? "inflate failed for all zlib window modes" : last_error;
    }
    return false;
}

bool InflateChunkStreamTryAll(const std::vector<CompressedChunk>& chunks,
                              std::vector<std::uint8_t>* output,
                              std::string* error) {
    std::size_t expected_size = 0;
    std::size_t compressed_size = 0;
    for (const CompressedChunk& chunk : chunks) {
        expected_size += chunk.uncompressed_size;
        compressed_size += chunk.bytes.size();
    }

    std::vector<std::uint8_t> compressed;
    compressed.reserve(compressed_size);
    for (const CompressedChunk& chunk : chunks) {
        compressed.insert(compressed.end(), chunk.bytes.begin(), chunk.bytes.end());
    }

    if (InflateTryAll(
            compressed.data(), compressed.size(), expected_size, Z_NO_FLUSH, output, error)) {
        return true;
    }

    bool chunks_are_stored = !chunks.empty();
    for (const CompressedChunk& chunk : chunks) {
        if (chunk.compressed_size != chunk.uncompressed_size) {
            chunks_are_stored = false;
            break;
        }
    }
    if (chunks_are_stored) {
        output->clear();
        output->reserve(compressed_size);
        for (const CompressedChunk& chunk : chunks) {
            output->insert(output->end(), chunk.bytes.begin(), chunk.bytes.end());
        }
        return true;
    }

    return false;
}

bool ReadCompressedTableChunks(std::ifstream& file,
                               const std::filesystem::path& bank_path,
                               std::uint64_t chunk_offset,
                               std::uint64_t chunk_limit,
                               bool has_zero_terminator,
                               std::vector<std::uint8_t>* table_bytes,
                               std::string* error) {
    table_bytes->clear();
    std::vector<CompressedChunk> chunks;
    bool saw_zero_terminator = false;

    while (chunk_offset + kChunkHeaderSize <= chunk_limit) {
        file.clear();
        file.seekg(static_cast<std::streamoff>(chunk_offset), std::ios::beg);
        if (!file) {
            if (error != nullptr) {
                *error = "failed to seek file-table chunk in " + bank_path.string();
            }
            return false;
        }

        std::array<std::uint8_t, kChunkHeaderSize> chunk_header{};
        if (!ReadExact(file, chunk_header.data(), chunk_header.size(), "file-table chunk", error)) {
            return false;
        }

        const std::uint32_t compressed_size =
            (static_cast<std::uint32_t>(chunk_header[0]) << 24) |
            (static_cast<std::uint32_t>(chunk_header[1]) << 16) |
            (static_cast<std::uint32_t>(chunk_header[2]) << 8) |
            static_cast<std::uint32_t>(chunk_header[3]);
        const std::uint32_t uncompressed_size =
            (static_cast<std::uint32_t>(chunk_header[4]) << 24) |
            (static_cast<std::uint32_t>(chunk_header[5]) << 16) |
            (static_cast<std::uint32_t>(chunk_header[6]) << 8) |
            static_cast<std::uint32_t>(chunk_header[7]);

        if (compressed_size == 0) {
            saw_zero_terminator = true;
            break;
        }

        const std::uint64_t data_offset = chunk_offset + kChunkHeaderSize;
        const std::uint64_t next_chunk_offset = data_offset + compressed_size;
        if (next_chunk_offset > chunk_limit) {
            if (error != nullptr) {
                *error = "compressed file-table chunk exceeds bank bounds in " +
                         bank_path.string();
            }
            return false;
        }

        CompressedChunk chunk;
        chunk.compressed_size = compressed_size;
        chunk.uncompressed_size = uncompressed_size;
        chunk.bytes.resize(compressed_size);
        if (!ReadExact(file,
                       chunk.bytes.data(),
                       chunk.bytes.size(),
                       "compressed file-table payload",
                       error)) {
            return false;
        }

        chunks.push_back(std::move(chunk));
        chunk_offset = next_chunk_offset;
    }

    if (has_zero_terminator && !saw_zero_terminator) {
        if (error != nullptr) {
            *error = "missing compressed file-table terminator in " + bank_path.string();
        }
        return false;
    }

    if (chunks.empty()) {
        table_bytes->assign(4, 0);
        return true;
    }

    if (!InflateChunkStreamTryAll(chunks, table_bytes, error)) {
        if (error != nullptr && !error->empty()) {
            *error = "failed to decompress file-table stream in " + bank_path.string() +
                     ": " + *error;
        }
        return false;
    }
    return true;
}

bool ParseEntryTable(const std::vector<std::uint8_t>& table_bytes,
                     const std::filesystem::path& bank_path,
                     bool file_data_compressed,
                     std::vector<BankEntry>* entries,
                     std::string* error) {
    if (table_bytes.size() < 4) {
        if (error != nullptr) {
            *error = "decompressed file table is too small for " + bank_path.string();
        }
        return false;
    }

    const std::uint32_t file_count = ReadBigEndianU32(table_bytes, 0);
    std::size_t position = 4;
    entries->clear();
    entries->reserve(file_count);

    for (std::uint32_t index = 0; index < file_count; ++index) {
        if (position + 4 > table_bytes.size()) {
            if (error != nullptr) {
                *error = "unexpected end of file table while reading name length in " +
                         bank_path.string();
            }
            return false;
        }

        const std::uint32_t stored_name_length = ReadBigEndianU32(table_bytes, position);
        position += 4;
        if (stored_name_length == 0) {
            if (error != nullptr) {
                *error = "encountered zero-length entry name in " + bank_path.string();
            }
            return false;
        }

        const std::size_t max_name_end =
            std::min(table_bytes.size(),
                     position + static_cast<std::size_t>(stored_name_length) + 1);
        const auto null_it =
            std::find(table_bytes.begin() + static_cast<std::ptrdiff_t>(position),
                      table_bytes.begin() + static_cast<std::ptrdiff_t>(max_name_end),
                      0);
        if (null_it == table_bytes.begin() + static_cast<std::ptrdiff_t>(max_name_end)) {
            if (error != nullptr) {
                *error = "failed to locate a null terminator for a bank entry in " +
                         bank_path.string();
            }
            return false;
        }

        const std::size_t null_position =
            static_cast<std::size_t>(std::distance(table_bytes.begin(), null_it));
        BankEntry entry;
        entry.relative_path.assign(reinterpret_cast<const char*>(table_bytes.data() + position),
                                   null_position - position);
        position = null_position + 1;

        if (position + 8 > table_bytes.size()) {
            if (error != nullptr) {
                *error = "unexpected end of file table while reading entry metadata in " +
                         bank_path.string();
            }
            return false;
        }

        if (!file_data_compressed) {
            if (position + 8 > table_bytes.size()) {
                if (error != nullptr) {
                    *error = "unexpected end of file table while reading entry metadata in " +
                             bank_path.string();
                }
                return false;
            }
            entry.offset = ReadBigEndianU32(table_bytes, position);
            position += 4;
            entry.size = ReadBigEndianU32(table_bytes, position);
            position += 4;
            entry.stored_size = entry.size;
            entry.is_compressed = false;
        } else {
            if (position + 16 > table_bytes.size()) {
                if (error != nullptr) {
                    *error =
                        "unexpected end of compressed file table while reading entry metadata in " +
                        bank_path.string();
                }
                return false;
            }
            entry.offset = ReadBigEndianU32(table_bytes, position);
            position += 4;
            entry.size = ReadBigEndianU32(table_bytes, position);
            position += 4;
            entry.stored_size = ReadBigEndianU32(table_bytes, position);
            position += 4;
            const std::uint32_t chunk_count = ReadBigEndianU32(table_bytes, position);
            position += 4;
            if (position + static_cast<std::size_t>(chunk_count) * 4 > table_bytes.size()) {
                if (error != nullptr) {
                    *error = "compressed entry chunk table exceeds file table in " +
                             bank_path.string();
                }
                return false;
            }
            entry.is_compressed = true;
            entry.extra_words.reserve(chunk_count);
            for (std::uint32_t chunk_index = 0; chunk_index < chunk_count; ++chunk_index) {
                entry.extra_words.push_back(ReadBigEndianU32(table_bytes, position));
                position += 4;
            }
        }

        entries->push_back(std::move(entry));
    }

    return true;
}

bool ParseBankTable(const std::filesystem::path& bank_path,
                    std::ifstream& file,
                    std::uint64_t file_size,
                    BankTable* table,
                    std::string* error) {
    if (file_size < kV3HeaderSize + kChunkHeaderSize) {
        if (error != nullptr) {
            *error = bank_path.string() + " is too small to contain a valid BNK header";
        }
        return false;
    }

    file.clear();
    file.seekg(0, std::ios::beg);
    std::array<std::uint8_t, 16> header{};
    if (!ReadExact(file, header.data(), header.size(), "BNK header", error)) {
        return false;
    }

    const std::uint32_t header_pointer = ReadBigEndianU32(header, 0);
    const std::uint32_t format_word = ReadBigEndianU32(header, 4);
    const bool file_data_compressed = header[8] != 0;
    if (format_word != 2 && format_word != 3) {
        if (error != nullptr) {
            std::ostringstream message;
            message << "unsupported BNK format word 0x" << std::hex << std::setw(8)
                    << std::setfill('0') << format_word << " in " << bank_path.string();
            *error = message.str();
        }
        return false;
    }
    const BankFormatVersion format =
        format_word == 2 ? BankFormatVersion::kV2 : BankFormatVersion::kV3;

    std::vector<std::uint8_t> table_bytes;
    if (format == BankFormatVersion::kV2) {
        if (header_pointer > file_size) {
            if (error != nullptr) {
                *error = "V2 file-table offset exceeds bank size in " + bank_path.string();
            }
            return false;
        }
        if (!ReadCompressedTableChunks(file,
                                       bank_path,
                                       header_pointer,
                                       file_size,
                                       false,
                                       &table_bytes,
                                       error)) {
            return false;
        }
        table->format = BankFormatVersion::kV2;
        table->data_base_offset = 0;
        table->entry_offsets_are_absolute = true;
        table->file_data_compressed = file_data_compressed;
    } else {
        if (header_pointer > file_size) {
            if (error != nullptr) {
                *error = "V3 file-data offset exceeds bank size in " + bank_path.string();
            }
            return false;
        }
        if (!ReadCompressedTableChunks(file,
                                       bank_path,
                                       kV3HeaderSize,
                                       header_pointer,
                                       true,
                                       &table_bytes,
                                       error)) {
            return false;
        }
        table->format = BankFormatVersion::kV3;
        table->data_base_offset = header_pointer;
        table->entry_offsets_are_absolute = false;
        table->file_data_compressed = file_data_compressed;
    }

    return ParseEntryTable(
        table_bytes, bank_path, table->file_data_compressed, &table->entries, error);
}

std::filesystem::path NormalizeRelativePath(const std::string& raw_path) {
    std::string normalized = raw_path;
    std::replace(normalized.begin(), normalized.end(), '\\', '/');
    return std::filesystem::path(normalized);
}

std::string LowercaseAscii(std::string value) {
    std::transform(value.begin(), value.end(), value.begin(), [](unsigned char ch) {
        return static_cast<char>(std::tolower(ch));
    });
    return value;
}

std::filesystem::path LowercaseRelativePath(const std::filesystem::path& path) {
    return std::filesystem::path(LowercaseAscii(path.generic_string()));
}

bool IsWindowsReservedName(std::string_view name) {
    if (name.empty()) {
        return false;
    }
    const std::string_view base_name =
        name.substr(0, name.find('.'));
    if (base_name.empty()) {
        return false;
    }

    if (base_name == "con" || base_name == "prn" || base_name == "aux" ||
        base_name == "nul" || base_name == "clock$") {
        return true;
    }

    if ((base_name.size() == 4 && base_name[0] == 'c' && base_name[1] == 'o' &&
         base_name[2] == 'm') &&
        std::isdigit(static_cast<unsigned char>(base_name[3]))) {
        return true;
    }
    if (base_name.size() == 4 && base_name[0] == 'l' && base_name[1] == 'p' &&
        base_name[2] == 't' && std::isdigit(static_cast<unsigned char>(base_name[3]))) {
        return true;
    }
    return false;
}

bool IsWindowsCompatibleFilename(std::string_view filename) {
    if (filename.empty()) {
        return false;
    }

    for (unsigned char ch : filename) {
        if (ch <= 0x1F || ch == '<' || ch == '>' || ch == ':' || ch == '"' || ch == '/' ||
            ch == '\\' || ch == '|' || ch == '?' || ch == '*') {
            return false;
        }
    }

    if (filename.back() == ' ' || filename.back() == '.') {
        return false;
    }

    const std::string lower = LowercaseAscii(std::string(filename));
    return !IsWindowsReservedName(lower);
}

bool IsSafeRelativePath(const std::filesystem::path& path) {
    if (path.empty() || path.is_absolute()) {
        return false;
    }
    for (const auto& component : path) {
        if (component == ".." || component == "." || component.empty()) {
            return false;
        }
        if (!IsWindowsCompatibleFilename(component.generic_string())) {
            return false;
        }
    }
    return true;
}

bool NormalizeLowercaseRelativePath(const std::string& raw_path,
                                    std::filesystem::path* normalized,
                                    std::string* error) {
    if (normalized == nullptr) {
        return false;
    }

    const std::filesystem::path candidate =
        LowercaseRelativePath(NormalizeRelativePath(raw_path).lexically_normal());
    if (!IsSafeRelativePath(candidate)) {
        if (error != nullptr) {
            *error = "path may not be absolute or escape the output directory: " + raw_path;
        }
        return false;
    }

    *normalized = candidate;
    return true;
}

bool IsBankPath(const std::filesystem::path& path) {
    std::string extension = path.extension().generic_string();
    std::transform(extension.begin(), extension.end(), extension.begin(), [](unsigned char ch) {
        return static_cast<char>(std::tolower(ch));
    });
    return extension == ".bnk";
}

bool NormalizeBankRelativePath(const std::filesystem::path& relative_path,
                               std::filesystem::path* normalized,
                               std::string* error) {
    const std::filesystem::path candidate = relative_path.lexically_normal();
    if (candidate.empty() || candidate.is_absolute()) {
        if (error != nullptr) {
            *error = "bank path must be relative";
        }
        return false;
    }

    for (const auto& component : candidate) {
        if (component == "..") {
            if (error != nullptr) {
                *error = "bank path may not escape the game content directory";
            }
            return false;
        }
    }

    *normalized = candidate;
    return true;
}

std::filesystem::path ResolveCaseInsensitiveExistingPath(
    const std::filesystem::path& root,
    const std::filesystem::path& relative_path) {
    std::filesystem::path current = root;
    std::error_code error;
    if (!std::filesystem::is_directory(current, error)) {
        return {};
    }

    for (const auto& component : relative_path) {
        if (component.empty() || component == ".") {
            continue;
        }

        const std::filesystem::path exact = current / component;
        error.clear();
        if (std::filesystem::exists(exact, error)) {
            current = exact;
            continue;
        }

        const std::string wanted = LowercaseAscii(component.generic_string());
        bool found = false;
        error.clear();
        for (std::filesystem::directory_iterator it(current, error), end;
             !error && it != end;
             it.increment(error)) {
            if (LowercaseAscii(it->path().filename().generic_string()) == wanted) {
                current = it->path();
                found = true;
                break;
            }
        }
        if (!found) {
            return {};
        }
    }

    return current;
}

std::string JoinExtraWords(const std::vector<std::uint32_t>& words) {
    if (words.empty()) {
        return {};
    }

    std::ostringstream stream;
    for (std::size_t index = 0; index < words.size(); ++index) {
        if (index != 0) {
            stream << ',';
        }
        stream << "0x" << std::hex << std::setw(8) << std::setfill('0') << words[index];
    }
    return stream.str();
}

const char* DescribeFormat(BankFormatVersion format) {
    switch (format) {
    case BankFormatVersion::kV2:
        return "v2";
    case BankFormatVersion::kV3:
        return "v3";
    case BankFormatVersion::kUnknown:
        return "unknown";
    }
    return "unknown";
}

bool StartsWith(std::string_view value, std::string_view prefix) {
    return value.size() >= prefix.size() && value.substr(0, prefix.size()) == prefix;
}

bool EndsWith(std::string_view value, std::string_view suffix) {
    return value.size() >= suffix.size() &&
           value.substr(value.size() - suffix.size()) == suffix;
}

std::string NormalizeVfsKey(std::string_view value) {
    std::string key(value);
    std::replace(key.begin(), key.end(), '\\', '/');
    while (!key.empty() && key.back() == '\0') {
        key.pop_back();
    }
    std::transform(key.begin(), key.end(), key.begin(), [](unsigned char ch) {
        return static_cast<char>(std::tolower(ch));
    });
    if (StartsWith(key, "game:/")) {
        key.erase(0, 6);
    } else if (StartsWith(key, "game:")) {
        key.erase(0, 5);
    }
    while (!key.empty() && key.front() == '/') {
        key.erase(key.begin());
    }
    return key;
}

bool IsLuaSourceRoot(std::string_view normalized) {
    return StartsWith(normalized, "luasource/") || StartsWith(normalized, "luasource");
}

bool ShouldIndexVfsCandidate(std::string_view value) {
    const std::string normalized = NormalizeVfsKey(value);
    if (normalized.empty() || normalized == "_refii_vfs.tsv") {
        return false;
    }
    return !IsLuaSourceRoot(normalized) && !StartsWith(normalized, ".baseline/") &&
           !StartsWith(normalized, ".cache/") && !StartsWith(normalized, "_disasm/") &&
           !StartsWith(normalized, "refii_vfs/") &&
           !StartsWith(normalized, "_case_conflicts/") &&
           normalized.find("/_case_conflicts/") == std::string::npos &&
           !StartsWith(normalized, "scripts/") && !StartsWith(normalized, "scripts_r/") &&
           !StartsWith(normalized, "data/scripts/") &&
           !StartsWith(normalized, "data/scripts_r/") &&
           normalized != "_manifest.tsv" && !EndsWith(normalized, "/_manifest.tsv");
}

std::uint32_t ComputeResourceNameHash(std::string_view value) {
    std::uint32_t hash = 0x811C9DC5u;
    for (const char ch : value) {
        hash = (hash * 0x01000193u) ^
               static_cast<std::uint8_t>(static_cast<unsigned char>(ch));
    }
    return hash;
}

std::string HexByte(std::uint8_t value) {
    constexpr char kDigits[] = "0123456789abcdef";
    std::string text(2, '0');
    text[0] = kDigits[(value >> 4) & 0xF];
    text[1] = kDigits[value & 0xF];
    return text;
}

std::string HexUint32(std::uint32_t value) {
    std::ostringstream stream;
    stream << std::hex << std::setw(8) << std::setfill('0') << value;
    return stream.str();
}

std::uint8_t RuntimeIndexShardForHash(std::uint32_t hash) {
    return static_cast<std::uint8_t>((hash >> 24) & 0xFFu);
}

std::uint16_t RuntimeAliasShardForHash(std::uint32_t hash) {
    return static_cast<std::uint16_t>((hash >> 20) & 0xFFFu);
}

std::string HexUint12(std::uint16_t value) {
    std::ostringstream stream;
    stream << std::hex << std::setw(3) << std::setfill('0')
           << static_cast<unsigned>(value & 0x0FFFu);
    return stream.str();
}

void AddUniqueString(std::vector<std::string>* values, std::string value) {
    if (value.empty()) {
        return;
    }
    if (std::find(values->begin(), values->end(), value) == values->end()) {
        values->push_back(std::move(value));
    }
}

void AddVfsDatabaseRow(std::vector<VfsDatabaseRow>* rows,
                       std::unordered_set<std::string>* row_keys,
                       std::uint32_t hash,
                       std::string_view alias,
                       std::string_view candidate) {
    if (hash == 0 || !ShouldIndexVfsCandidate(candidate)) {
        return;
    }

    std::string normalized_candidate = NormalizeVfsKey(candidate);
    std::string normalized_alias = NormalizeVfsKey(alias);
    if (normalized_alias.empty()) {
        normalized_alias = normalized_candidate;
    }
    if (normalized_candidate.empty()) {
        return;
    }

    std::ostringstream key_stream;
    key_stream << std::hex << std::setw(8) << std::setfill('0') << hash << std::dec
               << '\t' << normalized_alias << '\t' << normalized_candidate;
    const std::string key = key_stream.str();
    if (!row_keys->insert(key).second) {
        return;
    }

    rows->push_back(VfsDatabaseRow{
        hash,
        std::move(normalized_alias),
        std::move(normalized_candidate),
    });
}

void AddVfsDatabaseAlias(std::vector<VfsDatabaseRow>* rows,
                         std::unordered_set<std::string>* row_keys,
                         std::string_view hash_input,
                         std::string_view candidate) {
    if (hash_input.empty()) {
        return;
    }
    AddVfsDatabaseRow(
        rows, row_keys, ComputeResourceNameHash(hash_input), hash_input, candidate);
}

void AddVfsDatabaseExplicitHash(std::vector<VfsDatabaseRow>* rows,
                                std::unordered_set<std::string>* row_keys,
                                std::uint32_t hash,
                                std::string_view alias,
                                std::string_view candidate) {
    AddVfsDatabaseRow(rows, row_keys, hash, alias, candidate);
}

std::string BuildMip0TextureHeaderRedirectAlias(std::string_view candidate) {
    const std::string normalized = NormalizeVfsKey(candidate);
    constexpr std::string_view kCanonicalBank = "globals/1024mip0_textures.bnk";
    constexpr std::string_view kAliasBank = "globals/1024mip0_texture_headers.bnk";
    constexpr std::string_view kCanonicalRoot = "globals/1024mip0_textures/";
    constexpr std::string_view kAliasRoot = "globals/1024mip0_texture_headers/";

    if (normalized == kCanonicalBank) {
        return std::string(kAliasBank);
    }
    if (StartsWith(normalized, kCanonicalRoot)) {
        return std::string(kAliasRoot) +
               std::string(std::string_view(normalized).substr(kCanonicalRoot.size()));
    }
    return {};
}

void AddVfsDatabasePathAliases(
    std::vector<VfsDatabaseRow>* rows,
    std::unordered_set<std::string>* row_keys,
    std::string_view candidate,
    std::string_view hash_input) {
    if (!ShouldIndexVfsCandidate(candidate)) {
        return;
    }

    const std::string normalized = NormalizeVfsKey(hash_input.empty() ? candidate : hash_input);
    std::vector<std::string> aliases;
    AddUniqueString(&aliases, normalized);
    AddUniqueString(&aliases, BuildMip0TextureHeaderRedirectAlias(candidate));

    for (const std::string& alias : aliases) {
        std::string backslash_alias = alias;
        std::replace(backslash_alias.begin(), backslash_alias.end(), '/', '\\');
        AddVfsDatabaseAlias(rows, row_keys, alias, candidate);
        AddVfsDatabaseAlias(rows, row_keys, backslash_alias, candidate);
    }
}

void AddVfsBankRecord(std::vector<VfsBankRecord>* records,
                      std::unordered_set<std::string>* keys,
                      std::string_view alias,
                      std::string_view candidate,
                      std::string_view manifest_root) {
    if (records == nullptr || keys == nullptr || !ShouldIndexVfsCandidate(candidate)) {
        return;
    }

    std::string normalized_alias = NormalizeVfsKey(alias);
    std::string normalized_candidate = NormalizeVfsKey(candidate);
    std::string normalized_manifest_root = NormalizeVfsKey(manifest_root);
    if (normalized_alias.empty()) {
        normalized_alias = normalized_candidate;
    }
    if (normalized_candidate.empty() || normalized_manifest_root.empty()) {
        return;
    }

    const std::string key =
        normalized_alias + '\t' + normalized_candidate + '\t' + normalized_manifest_root;
    if (!keys->insert(key).second) {
        return;
    }
    records->push_back(VfsBankRecord{std::move(normalized_alias),
                                     std::move(normalized_candidate),
                                     std::move(normalized_manifest_root)});
}

bool ShouldIndexLuaVfsRecordCandidate(std::string_view value) {
    const std::string normalized = NormalizeVfsKey(value);
    if (normalized.empty() || !EndsWith(normalized, ".lua")) {
        return false;
    }
    return !StartsWith(normalized, ".baseline/") && !StartsWith(normalized, ".cache/") &&
           !StartsWith(normalized, "_disasm/") && !StartsWith(normalized, "refii_vfs/");
}

void AddVfsLuaRecord(std::vector<VfsLuaRecord>* records,
                     std::unordered_set<std::string>* keys,
                     std::string_view alias,
                     std::string_view candidate,
                     std::string_view kind) {
    if (records == nullptr || keys == nullptr ||
        !ShouldIndexLuaVfsRecordCandidate(candidate)) {
        return;
    }

    std::string normalized_alias = NormalizeVfsKey(alias);
    std::string normalized_candidate = NormalizeVfsKey(candidate);
    std::string normalized_kind = NormalizeVfsKey(kind);
    if (normalized_alias.empty()) {
        normalized_alias = normalized_candidate;
    }
    if (normalized_candidate.empty() || normalized_kind.empty()) {
        return;
    }

    const std::string key =
        normalized_alias + '\t' + normalized_candidate + '\t' + normalized_kind;
    if (!keys->insert(key).second) {
        return;
    }
    records->push_back(VfsLuaRecord{
        std::move(normalized_alias), std::move(normalized_candidate), std::move(normalized_kind)});
}

bool IsDirectLooseVfsFile(std::string_view candidate) {
    const std::string normalized = NormalizeVfsKey(candidate);
    if (normalized == "startup.vfsconfig" || normalized == "dir.manifest" ||
        normalized == "filehash.csv" || EndsWith(normalized, "/filehash.csv")) {
        return true;
    }

    const size_t dot = normalized.find_last_of('.');
    if (dot == std::string::npos) {
        return false;
    }
    const std::string_view extension = std::string_view(normalized).substr(dot);
    return extension == ".bik" || extension == ".bft" || extension == ".babel" ||
           extension == ".adb" || extension == ".gdb" || extension == ".ini";
}

bool IsDirectGlobalsMirrorFile(const std::filesystem::path& source_path) {
    const std::string filename = NormalizeVfsKey(source_path.filename().generic_string());
    if (filename.empty()) {
        return false;
    }

    const size_t dot = filename.find_last_of('.');
    if (dot == std::string::npos) {
        return false;
    }

    const std::string_view extension = std::string_view(filename).substr(dot);
    if (extension == ".bnk" || extension == ".sbk") {
        return false;
    }
    if (extension == ".list") {
        return true;
    }
    return IsDirectLooseVfsFile("globals/" + filename);
}

bool PublishDirectGlobalsFilesForVfsIndex(const std::filesystem::path& unpacked_root,
                                          std::string* error) {
    const std::filesystem::path source_root = unpacked_root / "Globals";
    const std::filesystem::path destination_root = unpacked_root / "globals";

    std::error_code status_error;
    if (!std::filesystem::is_directory(source_root, status_error)) {
        return true;
    }

    std::error_code create_error;
    std::filesystem::create_directories(destination_root, create_error);
    if (create_error) {
        if (error != nullptr) {
            *error = "failed to create lowercase globals directory: " +
                     destination_root.string() + ": " + create_error.message();
        }
        return false;
    }

    std::error_code iterator_error;
    for (std::filesystem::directory_iterator it(source_root, iterator_error), end;
         it != end; it.increment(iterator_error)) {
        if (iterator_error) {
            iterator_error.clear();
            continue;
        }

        std::error_code file_status_error;
        if (!it->is_regular_file(file_status_error) ||
            !IsDirectGlobalsMirrorFile(it->path())) {
            file_status_error.clear();
            continue;
        }

        const std::filesystem::path destination_path =
            destination_root / it->path().filename();
        std::error_code copy_error;
        std::filesystem::copy_file(it->path(),
                                   destination_path,
                                   std::filesystem::copy_options::overwrite_existing,
                                   copy_error);
        if (copy_error) {
            if (error != nullptr) {
                *error = "failed to mirror Globals file to lowercase globals: " +
                         it->path().string() + " -> " + destination_path.string() +
                         ": " + copy_error.message();
            }
            return false;
        }
    }

    return true;
}

std::uint32_t GetRuntimeCandidateId(std::unordered_map<std::string, std::uint32_t>* ids,
                                    std::vector<std::string>* candidates,
                                    std::string_view candidate) {
    const std::string normalized_candidate = NormalizeVfsKey(candidate);
    if (!ShouldIndexVfsCandidate(normalized_candidate)) {
        return std::numeric_limits<std::uint32_t>::max();
    }

    if (const auto existing = ids->find(normalized_candidate); existing != ids->end()) {
        return existing->second;
    }

    const std::uint32_t id = static_cast<std::uint32_t>(candidates->size());
    candidates->push_back(normalized_candidate);
    ids->emplace(normalized_candidate, id);
    return id;
}

void AddRuntimeIndexAlias(
    std::array<std::vector<std::pair<std::string, std::uint32_t>>, 4096>* shards,
    std::unordered_set<std::string>* keys,
    std::unordered_map<std::string, std::uint32_t>* candidate_ids,
    std::vector<std::string>* candidates,
    std::string_view alias,
    std::string_view candidate) {
    const std::string normalized_alias = NormalizeVfsKey(alias);
    const std::uint32_t candidate_id =
        GetRuntimeCandidateId(candidate_ids, candidates, candidate);
    if (normalized_alias.empty() ||
        candidate_id == std::numeric_limits<std::uint32_t>::max()) {
        return;
    }

    const std::string key = normalized_alias + '\t' + std::to_string(candidate_id);
    if (!keys->insert(key).second) {
        return;
    }

    const std::uint16_t shard =
        RuntimeAliasShardForHash(ComputeResourceNameHash(normalized_alias));
    (*shards)[shard].emplace_back(normalized_alias, candidate_id);
}

void AddRuntimeLanguageAliases(
    std::array<std::vector<std::pair<std::string, std::uint32_t>>, 4096>* shards,
    std::unordered_set<std::string>* keys,
    std::unordered_map<std::string, std::uint32_t>* candidate_ids,
    std::vector<std::string>* candidates,
    std::string_view candidate) {
    constexpr std::string_view kLanguagePrefix = "language/";
    constexpr std::string_view kKnownLanguageLocales[] = {
        "en-uk", "hu-hu", "zh-cht", "cs-cz", "ko-kr", "pl-pl", "sk-sk"};

    const std::string normalized_candidate = NormalizeVfsKey(candidate);
    if (!StartsWith(normalized_candidate, kLanguagePrefix)) {
        return;
    }

    const std::string_view language_tail =
        std::string_view(normalized_candidate).substr(kLanguagePrefix.size());
    const size_t locale_end = language_tail.find('/');
    if (locale_end == std::string_view::npos || locale_end + 1 >= language_tail.size()) {
        return;
    }

    const std::string asset_tail(language_tail.substr(locale_end + 1));
    auto add_tail_for_known_locales = [&](std::string_view tail) {
        for (const std::string_view locale : kKnownLanguageLocales) {
            AddRuntimeIndexAlias(shards,
                                 keys,
                                 candidate_ids,
                                 candidates,
                                 std::string(kLanguagePrefix) + std::string(locale) +
                                     "/" + std::string(tail),
                                 normalized_candidate);
        }
    };

    add_tail_for_known_locales(asset_tail);

    constexpr std::string_view kPatchTextPrefix = "patch_text/";
    constexpr std::string_view kTextPrefix = "text/";
    if (StartsWith(asset_tail, kPatchTextPrefix)) {
        add_tail_for_known_locales(std::string(kTextPrefix) +
                                   std::string(std::string_view(asset_tail).substr(
                                       kPatchTextPrefix.size())));
    } else if (StartsWith(asset_tail, kTextPrefix)) {
        add_tail_for_known_locales(std::string(kPatchTextPrefix) +
                                   std::string(std::string_view(asset_tail).substr(
                                       kTextPrefix.size())));
    }
}

void AddRuntimeDirectoryAlias(std::unordered_set<std::string>* directories,
                              std::string_view directory) {
    if (directories == nullptr) {
        return;
    }

    std::string normalized = NormalizeVfsKey(directory);
    while (!normalized.empty() && normalized.back() == '/') {
        normalized.pop_back();
    }
    if (normalized.empty() || !ShouldIndexVfsCandidate(normalized)) {
        return;
    }

    directories->insert(normalized);
    if (StartsWith(normalized, "data/")) {
        directories->insert(normalized.substr(5));
    } else {
        directories->insert("data/" + normalized);
    }
}

void AddRuntimeParentDirectories(std::unordered_set<std::string>* directories,
                                 std::string_view candidate) {
    std::string normalized = NormalizeVfsKey(candidate);
    while (!normalized.empty() && normalized.back() == '/') {
        normalized.pop_back();
    }
    if (normalized.empty() || !ShouldIndexVfsCandidate(normalized)) {
        return;
    }

    size_t separator = normalized.find_last_of('/');
    while (separator != std::string::npos) {
        AddRuntimeDirectoryAlias(
            directories, std::string_view(normalized).substr(0, separator));
        if (separator == 0) {
            break;
        }
        separator = normalized.find_last_of('/', separator - 1);
    }
}

bool WriteRuntimeVfsIndex(const std::filesystem::path& runtime_index_root,
                          const std::vector<VfsDatabaseRow>& rows,
                          const std::vector<VfsBankRecord>& bank_records,
                          const std::vector<VfsLuaRecord>& lua_records,
                          VfsDatabaseBuildSummary* summary,
                          std::string* error) {
    std::array<std::vector<std::pair<std::string, std::uint32_t>>, 4096> alias_shards;
    std::array<std::vector<std::pair<std::uint32_t, std::uint32_t>>, 256> hash_shards;
    std::unordered_set<std::string> alias_keys;
    std::unordered_set<std::string> hash_keys;
    std::unordered_set<std::uint32_t> owned_hashes;
    std::unordered_set<std::string> directories;
    std::unordered_map<std::string, std::uint32_t> candidate_ids;
    std::vector<std::string> candidates;
    alias_keys.reserve(rows.size());
    hash_keys.reserve(rows.size());
    owned_hashes.reserve(rows.size());
    directories.reserve(rows.size());
    candidate_ids.reserve(rows.size());
    candidates.reserve(rows.size() / 2);

    for (const VfsDatabaseRow& row : rows) {
        const std::string alias = row.alias.empty() ? row.candidate : row.alias;
        AddRuntimeIndexAlias(
            &alias_shards, &alias_keys, &candidate_ids, &candidates, alias, row.candidate);
        AddRuntimeLanguageAliases(
            &alias_shards, &alias_keys, &candidate_ids, &candidates, row.candidate);
        AddRuntimeParentDirectories(&directories, row.candidate);
        if (!row.alias.empty()) {
            AddRuntimeParentDirectories(&directories, row.alias);
        }

        if (row.hash == 0 || !ShouldIndexVfsCandidate(row.candidate)) {
            continue;
        }

        const std::uint32_t candidate_id =
            GetRuntimeCandidateId(&candidate_ids, &candidates, row.candidate);
        if (candidate_id == std::numeric_limits<std::uint32_t>::max()) {
            continue;
        }
        const std::string key = HexUint32(row.hash) + '\t' + std::to_string(candidate_id);
        if (!hash_keys.insert(key).second) {
            continue;
        }
        const std::uint8_t shard = RuntimeIndexShardForHash(row.hash);
        hash_shards[shard].emplace_back(row.hash, candidate_id);
        owned_hashes.insert(row.hash);
    }

    const std::filesystem::path alias_root = runtime_index_root / "aliases";
    const std::filesystem::path hash_root = runtime_index_root / "hashes";
    try {
        std::filesystem::remove_all(runtime_index_root);
        std::filesystem::create_directories(alias_root);
        std::filesystem::create_directories(hash_root);
    } catch (const std::exception& exception) {
        if (error != nullptr) {
            *error = exception.what();
        }
        return false;
    }

    std::size_t alias_entry_count = 0;
    std::size_t hash_entry_count = 0;
    const std::filesystem::path candidates_path = runtime_index_root / "candidates.tsv";
    std::ofstream candidate_output(candidates_path, std::ios::trunc);
    if (!candidate_output) {
        if (error != nullptr) {
            *error = "failed to open VFS candidate table for writing: " +
                     candidates_path.string();
        }
        return false;
    }
    candidate_output << "# refii_vfs_candidates_v2\n";
    candidate_output << "# id\tcandidate\n";
    for (std::uint32_t id = 0; id < candidates.size(); ++id) {
        candidate_output << id << '\t' << candidates[id] << '\n';
    }

    const std::filesystem::path directories_path = runtime_index_root / "directories.tsv";
    std::ofstream directory_output(directories_path, std::ios::trunc);
    if (!directory_output) {
        if (error != nullptr) {
            *error = "failed to open VFS directory table for writing: " +
                     directories_path.string();
        }
        return false;
    }
    std::vector<std::string> sorted_directories(directories.begin(), directories.end());
    std::sort(sorted_directories.begin(), sorted_directories.end());
    directory_output << "# refii_vfs_directories_v1\n";
    directory_output << "# directory\n";
    for (const std::string& directory : sorted_directories) {
        directory_output << directory << '\n';
    }

    const std::filesystem::path banks_path = runtime_index_root / "banks.tsv";
    std::ofstream bank_output(banks_path, std::ios::trunc);
    if (!bank_output) {
        if (error != nullptr) {
            *error = "failed to open VFS bank table for writing: " + banks_path.string();
        }
        return false;
    }
    bank_output << "# refii_vfs_banks_v1\n";
    bank_output << "# alias\tcandidate\tmanifest_root\n";
    for (const VfsBankRecord& record : bank_records) {
        bank_output << record.alias << '\t' << record.candidate << '\t'
                    << record.manifest_root << '\n';
    }

    const std::filesystem::path lua_path = runtime_index_root / "lua.tsv";
    std::ofstream lua_output(lua_path, std::ios::trunc);
    if (!lua_output) {
        if (error != nullptr) {
            *error = "failed to open VFS Lua table for writing: " + lua_path.string();
        }
        return false;
    }
    lua_output << "# refii_vfs_lua_v1\n";
    lua_output << "# alias\tcandidate\tkind\n";
    for (const VfsLuaRecord& record : lua_records) {
        lua_output << record.alias << '\t' << record.candidate << '\t'
                   << record.kind << '\n';
    }

    for (size_t shard = 0; shard < alias_shards.size(); ++shard) {
        auto& aliases = alias_shards[shard];
        if (aliases.empty()) {
            continue;
        }
        std::sort(aliases.begin(), aliases.end());
        const std::filesystem::path shard_path =
            alias_root / (HexUint12(static_cast<std::uint16_t>(shard)) + ".tsv");
        std::ofstream output(shard_path, std::ios::trunc);
        if (!output) {
            if (error != nullptr) {
                *error = "failed to open VFS alias shard for writing: " +
                         shard_path.string();
            }
            return false;
        }
        output << "# refii_vfs_alias_index_v2\n";
        output << "# alias\tcandidate\n";
        for (const auto& [alias, candidate_id] : aliases) {
            if (candidate_id >= candidates.size()) {
                continue;
            }
            output << alias << '\t' << candidates[candidate_id] << '\n';
            ++alias_entry_count;
        }
    }

    for (size_t shard = 0; shard < hash_shards.size(); ++shard) {
        auto& hashes = hash_shards[shard];
        if (hashes.empty()) {
            continue;
        }
        std::sort(hashes.begin(), hashes.end());
        const std::filesystem::path shard_path =
            hash_root / (HexByte(static_cast<std::uint8_t>(shard)) + ".tsv");
        std::ofstream output(shard_path, std::ios::trunc);
        if (!output) {
            if (error != nullptr) {
                *error = "failed to open VFS hash shard for writing: " +
                         shard_path.string();
            }
            return false;
        }
        output << "# refii_vfs_hash_index_v2\n";
        output << "# hash_hex\tcandidate\n";
        for (const auto& [hash, candidate_id] : hashes) {
            if (candidate_id >= candidates.size()) {
                continue;
            }
            output << HexUint32(hash) << '\t' << candidates[candidate_id] << '\n';
            ++hash_entry_count;
        }
    }

    const std::filesystem::path owned_hashes_path =
        runtime_index_root / "hash_ownership.tsv";
    std::ofstream owned_hashes_output(owned_hashes_path, std::ios::trunc);
    if (!owned_hashes_output) {
        if (error != nullptr) {
            *error = "failed to open VFS hash ownership table for writing: " +
                     owned_hashes_path.string();
        }
        return false;
    }
    std::vector<std::uint32_t> sorted_owned_hashes(owned_hashes.begin(),
                                                   owned_hashes.end());
    std::sort(sorted_owned_hashes.begin(), sorted_owned_hashes.end());
    owned_hashes_output << "# refii_vfs_hash_ownership_v1\n";
    owned_hashes_output << "# hash_hex\n";
    for (const std::uint32_t hash : sorted_owned_hashes) {
        owned_hashes_output << HexUint32(hash) << '\n';
    }

    const std::filesystem::path metadata_path = runtime_index_root / "meta.tsv";
    std::ofstream metadata(metadata_path, std::ios::trunc);
    if (!metadata) {
        if (error != nullptr) {
            *error = "failed to open VFS index metadata for writing: " +
                     metadata_path.string();
        }
        return false;
    }
    metadata << "# refii_vfs_runtime_index_v5\n";
    metadata << "candidates\t" << candidates.size() << '\n';
    metadata << "alias_entries\t" << alias_entry_count << '\n';
    metadata << "hash_entries\t" << hash_entry_count << '\n';
    metadata << "owned_hashes\t" << sorted_owned_hashes.size() << '\n';
    metadata << "directories\t" << sorted_directories.size() << '\n';
    metadata << "bank_records\t" << bank_records.size() << '\n';
    metadata << "lua_records\t" << lua_records.size() << '\n';

    if (summary != nullptr) {
        summary->runtime_index_root = runtime_index_root;
        summary->runtime_alias_entries = alias_entry_count;
        summary->runtime_hash_entries = hash_entry_count;
        summary->runtime_directories = sorted_directories.size();
    }
    return true;
}

void AddVfsBasenameCandidate(
    std::unordered_map<std::string, std::vector<std::string>>* basename_candidates,
    std::string_view candidate) {
    const std::string normalized = NormalizeVfsKey(candidate);
    if (!ShouldIndexVfsCandidate(normalized)) {
        return;
    }
    const size_t separator = normalized.find_last_of('/');
    const std::string basename =
        separator == std::string::npos ? normalized : normalized.substr(separator + 1);
    if (basename.empty() || basename == "filehash.csv") {
        return;
    }
    AddUniqueString(&(*basename_candidates)[basename], normalized);
}

std::vector<std::string_view> SplitTabs(std::string_view line) {
    std::vector<std::string_view> columns;
    size_t start = 0;
    while (start <= line.size()) {
        const size_t tab = line.find('\t', start);
        columns.push_back(line.substr(start, tab == std::string_view::npos
                                                 ? std::string_view::npos
                                                 : tab - start));
        if (tab == std::string_view::npos) {
            break;
        }
        start = tab + 1;
    }
    return columns;
}

bool WriteManifest(const std::filesystem::path& bank_root,
                   const BankTable& table,
                   const std::vector<PlannedBankEntry>& planned_entries,
                   std::string* error) {
    try {
        std::filesystem::create_directories(bank_root);
    } catch (const std::exception& exception) {
        if (error != nullptr) {
            *error = exception.what();
        }
        return false;
    }

    std::ofstream manifest(bank_root / "_manifest.tsv", std::ios::trunc);
    if (!manifest) {
        if (error != nullptr) {
            *error = "failed to open manifest for writing in " + bank_root.string();
        }
        return false;
    }

    manifest << "path\toffset\tsize\tstored_size\tcompressed\tchunk_sizes\tformat\thost_path\n";
    for (const PlannedBankEntry& planned_entry : planned_entries) {
        if (planned_entry.skip_duplicate || planned_entry.entry_index >= table.entries.size()) {
            continue;
        }
        const BankEntry& entry = table.entries[planned_entry.entry_index];
        const std::filesystem::path& relative_path = planned_entry.relative_path;
        const std::filesystem::path& host_relative_path =
            planned_entry.host_relative_path.empty()
                ? planned_entry.relative_path
                : planned_entry.host_relative_path;
        manifest << relative_path.generic_string() << '\t' << entry.offset << '\t'
                 << entry.size << '\t'
                 << entry.stored_size << '\t' << (entry.is_compressed ? 1 : 0) << '\t'
                 << JoinExtraWords(entry.extra_words) << '\t'
                 << DescribeFormat(table.format) << '\t'
                 << host_relative_path.generic_string() << '\n';
    }
    return true;
}

std::string TsvEscape(std::string value) {
    for (char& ch : value) {
        if (ch == '\t' || ch == '\n' || ch == '\r') {
            ch = ' ';
        }
    }
    return value;
}

bool AppendDuplicateConflictReport(const ExtractionContext* context,
                                   const BankDuplicateConflict& conflict,
                                   std::string* error) {
    if (context == nullptr || context->conflict_report_path.empty()) {
        return true;
    }

    const bool write_header =
        !std::filesystem::exists(context->conflict_report_path);
    try {
        std::filesystem::create_directories(context->conflict_report_path.parent_path());
    } catch (const std::exception& exception) {
        if (error != nullptr) {
            *error = exception.what();
        }
        return false;
    }

    std::ofstream report(context->conflict_report_path, std::ios::app);
    if (!report) {
        if (error != nullptr) {
            *error = "failed to open duplicate conflict report for writing: " +
                     context->conflict_report_path.string();
        }
        return false;
    }

    if (write_header) {
        report << "bank\tlowercase_path\tfirst_entry\tduplicate_entry\tfirst_size\t"
                  "duplicate_size\tbyte_equal\taction\tconflict_path\n";
    }
    report << TsvEscape(conflict.bank_path.generic_string()) << '\t'
           << TsvEscape(conflict.lowercase_path.generic_string()) << '\t'
           << TsvEscape(conflict.first_entry_path) << '\t'
           << TsvEscape(conflict.duplicate_entry_path) << '\t'
           << conflict.first_size << '\t' << conflict.duplicate_size << '\t'
           << (conflict.byte_equal ? 1 : 0) << '\t'
           << TsvEscape(conflict.action) << '\t'
           << TsvEscape(conflict.conflict_path.generic_string()) << '\n';
    return true;
}

bool CopyFileRange(std::ifstream& source,
                   std::uint64_t offset,
                   std::uint64_t size,
                   const std::filesystem::path& output_path,
                   std::string* error) {
    try {
        std::filesystem::create_directories(output_path.parent_path());
    } catch (const std::exception& exception) {
        if (error != nullptr) {
            *error = exception.what();
        }
        return false;
    }

    std::ofstream output(output_path, std::ios::binary | std::ios::trunc);
    if (!output) {
        if (error != nullptr) {
            *error = "failed to open " + output_path.string() + " for writing";
        }
        return false;
    }

    source.clear();
    source.seekg(static_cast<std::streamoff>(offset), std::ios::beg);
    if (!source) {
        if (error != nullptr) {
            *error = "failed to seek source bank payload";
        }
        return false;
    }

    std::array<char, 64 * 1024> buffer{};
    std::uint64_t remaining = size;
    while (remaining != 0) {
        const std::size_t to_read =
            static_cast<std::size_t>(std::min<std::uint64_t>(remaining, buffer.size()));
        source.read(buffer.data(), static_cast<std::streamsize>(to_read));
        if (!source) {
            if (error != nullptr) {
                *error = "failed to read source bank payload";
            }
            return false;
        }
        output.write(buffer.data(), static_cast<std::streamsize>(to_read));
        if (!output) {
            if (error != nullptr) {
                *error = "failed to write " + output_path.string();
            }
            return false;
        }
        remaining -= to_read;
    }

    return true;
}

bool WriteBytes(const std::filesystem::path& output_path,
                const std::vector<std::uint8_t>& bytes,
                std::string* error) {
    try {
        std::filesystem::create_directories(output_path.parent_path());
    } catch (const std::exception& exception) {
        if (error != nullptr) {
            *error = exception.what();
        }
        return false;
    }

    std::ofstream output(output_path, std::ios::binary | std::ios::trunc);
    if (!output) {
        if (error != nullptr) {
            *error = "failed to open " + output_path.string() + " for writing";
        }
        return false;
    }

    if (!bytes.empty()) {
        output.write(reinterpret_cast<const char*>(bytes.data()),
                     static_cast<std::streamsize>(bytes.size()));
        if (!output) {
            if (error != nullptr) {
                *error = "failed to write " + output_path.string();
            }
            return false;
        }
    }

    return true;
}

bool ReadPayloadBytes(std::ifstream& file,
                      std::uint64_t offset,
                      std::size_t size,
                      std::vector<std::uint8_t>* bytes,
                      std::string* error) {
    bytes->assign(size, 0);
    file.clear();
    file.seekg(static_cast<std::streamoff>(offset), std::ios::beg);
    if (!file) {
        if (error != nullptr) {
            *error = "failed to seek compressed payload";
        }
        return false;
    }
    return ReadExact(file, bytes->data(), bytes->size(), "compressed payload", error);
}

std::uint64_t EntryDataOffset(const BankTable& table, const BankEntry& entry) {
    return table.entry_offsets_are_absolute
               ? static_cast<std::uint64_t>(entry.offset)
               : static_cast<std::uint64_t>(table.data_base_offset) +
                     static_cast<std::uint64_t>(entry.offset);
}

bool EntryPayloadWithinBankBounds(const std::filesystem::path& bank_path,
                                  const BankTable& table,
                                  const BankEntry& entry,
                                  std::uint64_t file_size,
                                  std::string* error) {
    const std::uint64_t data_offset = EntryDataOffset(table, entry);
    if (data_offset + entry.stored_size > file_size) {
        if (error != nullptr) {
            *error = "entry " + entry.relative_path + " exceeds bank bounds in " +
                     bank_path.string();
        }
        return false;
    }
    return true;
}

bool DecodeCompressedEntryBytes(std::ifstream& file,
                                std::uint64_t data_offset,
                                const BankEntry& entry,
                                std::vector<std::uint8_t>* output_bytes,
                                std::string* error) {
    if (output_bytes == nullptr) {
        return false;
    }
    output_bytes->clear();
    if (entry.extra_words.empty()) {
        std::vector<std::uint8_t> compressed;
        if (!ReadPayloadBytes(file, data_offset, entry.stored_size, &compressed, error)) {
            return false;
        }
        if (!InflateTryAll(compressed.data(),
                           compressed.size(),
                           entry.size,
                           Z_SYNC_FLUSH,
                           output_bytes,
                           error)) {
            return false;
        }
        return true;
    }

    output_bytes->reserve(entry.size);
    std::uint64_t cursor = data_offset;
    std::size_t remaining_stored_size = entry.stored_size;
    for (std::size_t segment_index = 0; segment_index < entry.extra_words.size(); ++segment_index) {
        if (remaining_stored_size == 0) {
            if (error != nullptr) {
                *error = "compressed entry ran out of stored bytes before all segments were read";
            }
            return false;
        }

        const std::size_t segment_stored_size =
            std::min<std::size_t>(remaining_stored_size, kCompressedChunkSize);
        std::vector<std::uint8_t> compressed;
        if (!ReadPayloadBytes(file, cursor, segment_stored_size, &compressed, error)) {
            return false;
        }

        std::vector<std::uint8_t> segment_bytes;
        if (!InflateTryAll(compressed.data(),
                           compressed.size(),
                           entry.extra_words[segment_index],
                           Z_SYNC_FLUSH,
                           &segment_bytes,
                           error)) {
            return false;
        }

        output_bytes->insert(output_bytes->end(), segment_bytes.begin(), segment_bytes.end());
        cursor += segment_stored_size;
        remaining_stored_size -= segment_stored_size;
    }

    if (output_bytes->size() != entry.size) {
        if (error != nullptr) {
            std::ostringstream message;
            message << "segmented inflate produced " << output_bytes->size()
                    << " bytes, expected " << entry.size;
            *error = message.str();
        }
        return false;
    }

    return true;
}

bool ReadEntryOutputBytes(std::ifstream& file,
                          const BankTable& table,
                          const BankEntry& entry,
                          std::vector<std::uint8_t>* output_bytes,
                          std::string* error) {
    if (output_bytes == nullptr) {
        return false;
    }

    const std::uint64_t data_offset = EntryDataOffset(table, entry);
    if (entry.is_compressed) {
        return DecodeCompressedEntryBytes(file, data_offset, entry, output_bytes, error);
    }

    return ReadPayloadBytes(file, data_offset, entry.stored_size, output_bytes, error);
}

bool EntriesHaveIdenticalOutput(std::ifstream& file,
                                const BankTable& table,
                                const BankEntry& left,
                                const BankEntry& right,
                                bool* identical,
                                std::string* error) {
    if (identical == nullptr) {
        return false;
    }
    *identical = false;

    const std::uint64_t left_output_size = left.is_compressed ? left.size : left.stored_size;
    const std::uint64_t right_output_size = right.is_compressed ? right.size : right.stored_size;
    if (left_output_size != right_output_size) {
        return true;
    }

    std::vector<std::uint8_t> left_bytes;
    std::vector<std::uint8_t> right_bytes;
    if (!ReadEntryOutputBytes(file, table, left, &left_bytes, error) ||
        !ReadEntryOutputBytes(file, table, right, &right_bytes, error)) {
        return false;
    }

    *identical = left_bytes == right_bytes;
    return true;
}

bool ExtractCompressedEntry(std::ifstream& file,
                            std::uint64_t data_offset,
                            const BankEntry& entry,
                            const std::filesystem::path& output_path,
                            std::string* error) {
    std::vector<std::uint8_t> output_bytes;
    if (!DecodeCompressedEntryBytes(file, data_offset, entry, &output_bytes, error)) {
        return false;
    }
    return WriteBytes(output_path, output_bytes, error);
}

std::filesystem::path ConflictReportPath(const std::filesystem::path& output_root) {
    return output_root / "refii_vfs" / "extraction_conflicts.tsv";
}

std::filesystem::path ConflictStashRoot(const std::filesystem::path& output_root) {
    return output_root / "refii_vfs" / "conflicts";
}

bool ExtractOneBank(const std::filesystem::path& bank_path,
                    const std::filesystem::path& bank_root,
                    ExtractionContext* context,
                    std::string* error) {
    std::ifstream file(bank_path, std::ios::binary);
    if (!file) {
        if (error != nullptr) {
            *error = "failed to open " + bank_path.string();
        }
        return false;
    }

    file.seekg(0, std::ios::end);
    const std::streampos stream_size = file.tellg();
    if (stream_size < 0) {
        if (error != nullptr) {
            *error = "failed to determine size of " + bank_path.string();
        }
        return false;
    }
    const std::uint64_t file_size = static_cast<std::uint64_t>(stream_size);

    BankTable table;
    if (!ParseBankTable(bank_path, file, file_size, &table, error)) {
        return false;
    }

    std::vector<PlannedBankEntry> planned_entries;
    planned_entries.reserve(table.entries.size());
    std::unordered_map<std::string, std::size_t> first_entry_by_relative_path;
    for (std::size_t entry_index = 0; entry_index < table.entries.size(); ++entry_index) {
        const BankEntry& entry = table.entries[entry_index];
        std::filesystem::path relative_path;
        if (!NormalizeLowercaseRelativePath(entry.relative_path, &relative_path, error)) {
            if (error != nullptr && !error->empty()) {
                *error = "refusing to extract unsafe path " + entry.relative_path + ": " +
                         *error;
            }
            return false;
        }
        const std::string relative_key = relative_path.generic_string();
        if (!EntryPayloadWithinBankBounds(bank_path, table, entry, file_size, error)) {
            return false;
        }

        PlannedBankEntry planned_entry;
        planned_entry.entry_index = entry_index;
        planned_entry.relative_path = relative_path;
        planned_entry.host_relative_path = relative_path;

        const auto [first_match, inserted] =
            first_entry_by_relative_path.emplace(relative_key, entry_index);
        if (!inserted) {
            const BankEntry& first_entry = table.entries[first_match->second];
            bool identical = false;
            if (!EntriesHaveIdenticalOutput(
                    file, table, first_entry, entry, &identical, error)) {
                if (error != nullptr && !error->empty()) {
                    *error = "failed to compare duplicate bank entries " +
                             first_entry.relative_path + " and " + entry.relative_path +
                             ": " + *error;
                }
                return false;
            }
            if (!identical) {
                BankDuplicateConflict conflict;
                conflict.bank_path = bank_path;
                conflict.lowercase_path = relative_path;
                conflict.first_entry_path = first_entry.relative_path;
                conflict.duplicate_entry_path = entry.relative_path;
                conflict.first_size =
                    first_entry.is_compressed ? first_entry.size : first_entry.stored_size;
                conflict.duplicate_size = entry.is_compressed ? entry.size : entry.stored_size;
                conflict.byte_equal = false;
                conflict.action = "kept_first_routed_duplicate_host_path";

                planned_entry.host_relative_path =
                    std::filesystem::path("_case_conflicts") /
                    (relative_key + "." + std::to_string(entry_index) + ".bin");
                conflict.conflict_path = planned_entry.host_relative_path;
                if (!AppendDuplicateConflictReport(context, conflict, error)) {
                    return false;
                }
                if (context != nullptr && context->summary != nullptr) {
                    ++context->summary->duplicate_conflicts;
                }
            } else {
                BankDuplicateConflict conflict;
                conflict.bank_path = bank_path;
                conflict.lowercase_path = relative_path;
                conflict.first_entry_path = first_entry.relative_path;
                conflict.duplicate_entry_path = entry.relative_path;
                conflict.first_size =
                    first_entry.is_compressed ? first_entry.size : first_entry.stored_size;
                conflict.duplicate_size = entry.is_compressed ? entry.size : entry.stored_size;
                conflict.byte_equal = true;
                conflict.action = "skipped_identical_duplicate";
                if (!AppendDuplicateConflictReport(context, conflict, error)) {
                    return false;
                }
            }
            planned_entry.skip_duplicate = identical;
        }

        planned_entries.push_back(std::move(planned_entry));
    }

    if (!WriteManifest(bank_root, table, planned_entries, error)) {
        return false;
    }

    for (const PlannedBankEntry& planned_entry : planned_entries) {
        if (planned_entry.skip_duplicate) {
            continue;
        }
        if (planned_entry.entry_index >= table.entries.size()) {
            if (error != nullptr) {
                *error = "invalid planned bank entry index";
            }
            return false;
        }
        const BankEntry& entry = table.entries[planned_entry.entry_index];
        const std::filesystem::path& relative_path = planned_entry.relative_path;

        const std::uint64_t data_offset = EntryDataOffset(table, entry);
        const std::uint64_t stored_size = entry.stored_size;

        if (context == nullptr || !context->extract_payloads) {
            continue;
        }

        const std::filesystem::path output_path = bank_root / relative_path;
        if (entry.is_compressed) {
            if (!ExtractCompressedEntry(file, data_offset, entry, output_path, error)) {
                if (error != nullptr && !error->empty()) {
                    *error = "failed to decompress " + entry.relative_path + " from " +
                             bank_path.string() + ": " + *error;
                }
                return false;
            }
        } else if (!CopyFileRange(file, data_offset, stored_size, output_path, error)) {
            if (error != nullptr && !error->empty()) {
                *error = "failed to write extracted file " + output_path.string() + ": " +
                         *error;
            }
            return false;
        }

        if (context->extract_nested_banks && IsBankPath(relative_path)) {
            const std::filesystem::path nested_output_root =
                output_path.parent_path() / output_path.stem();
            std::error_code remove_error;
            std::filesystem::remove_all(nested_output_root, remove_error);
            if (remove_error) {
                if (error != nullptr) {
                    *error = "failed to clean nested bank output " +
                             nested_output_root.string() + ": " + remove_error.message();
                }
                return false;
            }

            if (!ExtractOneBank(output_path, nested_output_root, context, error)) {
                if (error != nullptr && !error->empty()) {
                    *error = "failed to unpack nested bank " + output_path.string() + ": " +
                             *error;
                }
                return false;
            }

            std::error_code delete_error;
            std::filesystem::remove(output_path, delete_error);
            if (delete_error) {
                if (error != nullptr) {
                    *error = "failed to remove extracted nested bank " + output_path.string() +
                             ": " + delete_error.message();
                }
                return false;
            }
        } else if (context->summary != nullptr) {
            ++context->summary->extracted_files;
        }
    }

    if (context != nullptr && context->summary != nullptr) {
        ++context->summary->extracted_banks;
    }
    return true;
}

void EmitProgress(const std::function<void(const BankExtractionProgress&)>& callback,
                  std::string phase,
                  std::size_t completed_banks,
                  std::size_t total_banks,
                  const std::filesystem::path& current_bank = {}) {
    if (!callback) {
        return;
    }

    BankExtractionProgress progress;
    progress.phase = std::move(phase);
    progress.completed_banks = completed_banks;
    progress.total_banks = total_banks;
    progress.current_bank = current_bank;
    callback(progress);
}

}  // namespace

std::filesystem::path GetBankOutputRoot(const std::filesystem::path& output_root,
                                        const std::filesystem::path& bank_relative_path) {
    return (output_root / LowercaseRelativePath(bank_relative_path.parent_path()) /
            LowercaseAscii(bank_relative_path.stem().generic_string()))
        .lexically_normal();
}

bool BuildVfsDatabase(const VfsDatabaseBuildOptions& options,
                      VfsDatabaseBuildSummary* summary,
                      std::string* error) {
    VfsDatabaseBuildSummary local_summary;
    VfsDatabaseBuildSummary* const active_summary =
        summary != nullptr ? summary : &local_summary;
    *active_summary = {};

    if (options.unpacked_root.empty()) {
        if (error != nullptr) {
            *error = "unpacked root is required";
        }
        return false;
    }

    const std::filesystem::path unpacked_root =
        std::filesystem::absolute(options.unpacked_root).lexically_normal();
    std::error_code status_error;
    if (!std::filesystem::is_directory(unpacked_root, status_error)) {
        if (error != nullptr) {
            *error = "unpacked root is not a directory: " + unpacked_root.string();
        }
        return false;
    }
    if (!PublishDirectGlobalsFilesForVfsIndex(unpacked_root, error)) {
        return false;
    }

    const std::filesystem::path default_legacy_database_path =
        unpacked_root / "_refii_vfs.tsv";
    const bool write_legacy_database =
        options.write_legacy_database || !options.database_path.empty();
    active_summary->database_path =
        write_legacy_database
            ? (options.database_path.empty() ? default_legacy_database_path
                                             : options.database_path)
            : std::filesystem::path();
    active_summary->runtime_index_root =
        options.runtime_index_root.empty() ? unpacked_root / "refii_vfs"
                                           : options.runtime_index_root;

    std::vector<VfsDatabaseRow> rows;
    std::vector<VfsBankRecord> bank_records;
    std::vector<VfsLuaRecord> lua_records;
    std::unordered_set<std::string> row_keys;
    std::unordered_set<std::string> bank_record_keys;
    std::unordered_set<std::string> lua_record_keys;
    std::unordered_map<std::string, std::vector<std::string>> basename_candidates;
    std::unordered_set<std::string> manifest_covered_candidates;
    std::vector<std::string> file_candidates;
    std::vector<std::filesystem::path> manifest_paths;
    std::vector<std::filesystem::path> filehash_paths;

    std::error_code iterator_error;
    constexpr auto iterator_options =
        std::filesystem::directory_options::skip_permission_denied;
    for (std::filesystem::recursive_directory_iterator it(
             unpacked_root, iterator_options, iterator_error),
         end;
         it != end; it.increment(iterator_error)) {
        if (iterator_error) {
            iterator_error.clear();
            continue;
        }
        if (!it->is_regular_file(status_error)) {
            status_error.clear();
            continue;
        }

        const std::filesystem::path relative_path =
            it->path().lexically_relative(unpacked_root);
        const std::string relative_key = relative_path.generic_string();
        const std::string normalized_key = NormalizeVfsKey(relative_key);
        if (IsLuaSourceRoot(normalized_key)) {
            if (EndsWith(normalized_key, ".lua")) {
                const std::string_view prefix =
                    StartsWith(normalized_key, "luasource/")
                        ? std::string_view("luasource/")
                        : std::string_view("luasource");
                AddVfsLuaRecord(
                    &lua_records, &lua_record_keys, normalized_key, normalized_key, "carrier");
                AddVfsLuaRecord(&lua_records,
                                &lua_record_keys,
                                std::string_view(normalized_key).substr(prefix.size()),
                                normalized_key,
                                "carrier");
            }
            continue;
        }
        if (StartsWith(normalized_key, ".baseline/") ||
            StartsWith(normalized_key, ".cache/") ||
            StartsWith(normalized_key, "refii_vfs/") ||
            StartsWith(normalized_key, "_disasm/")) {
            continue;
        }

        const std::string filename = NormalizeVfsKey(it->path().filename().generic_string());
        if (filename == "_manifest.tsv") {
            manifest_paths.push_back(it->path());
            continue;
        }
        if (filename == "filehash.csv") {
            filehash_paths.push_back(it->path());
        }

        if (EndsWith(normalized_key, ".lua")) {
            AddVfsLuaRecord(
                &lua_records, &lua_record_keys, normalized_key, normalized_key, "override");
        }

        file_candidates.push_back(NormalizeVfsKey(relative_key));
        AddVfsBasenameCandidate(&basename_candidates, relative_key);
        ++active_summary->indexed_files;
    }

    for (const std::filesystem::path& manifest_path : manifest_paths) {
        std::ifstream manifest(manifest_path);
        if (!manifest) {
            continue;
        }

        const std::filesystem::path manifest_root =
            manifest_path.parent_path().lexically_relative(unpacked_root);
        const std::string manifest_root_key = manifest_root.generic_string();
        const std::string normalized_root = NormalizeVfsKey(manifest_root_key);
        if (normalized_root.empty() || IsLuaSourceRoot(normalized_root) ||
            StartsWith(normalized_root, ".baseline/") ||
            StartsWith(normalized_root, ".cache/") ||
            StartsWith(normalized_root, "_disasm/")) {
            continue;
        }

        const std::string synthetic_bank_candidate = normalized_root + ".bnk";
        if (ShouldIndexVfsCandidate(synthetic_bank_candidate)) {
            AddVfsDatabasePathAliases(
                &rows, &row_keys, synthetic_bank_candidate, synthetic_bank_candidate);
            AddVfsBankRecord(&bank_records,
                             &bank_record_keys,
                             synthetic_bank_candidate,
                             synthetic_bank_candidate,
                             normalized_root);
            if (normalized_root == "globals/1024mip0_textures") {
                AddVfsBankRecord(&bank_records,
                                 &bank_record_keys,
                                 "globals/1024mip0_texture_headers.bnk",
                                 synthetic_bank_candidate,
                                 normalized_root);
            }

            constexpr std::string_view kLanguagePrefix = "language/";
            if (StartsWith(normalized_root, kLanguagePrefix)) {
                const std::string_view language_tail =
                    std::string_view(normalized_root).substr(kLanguagePrefix.size());
                const size_t locale_end = language_tail.find('/');
                if (locale_end != std::string_view::npos &&
                    locale_end + 1 < language_tail.size()) {
                    const std::string alias =
                        std::string(kLanguagePrefix) +
                        std::string(language_tail.substr(locale_end + 1)) + ".bnk";
                    AddVfsDatabaseAlias(
                        &rows, &row_keys, alias, synthetic_bank_candidate);
                    std::string backslash_alias = alias;
                    std::replace(backslash_alias.begin(), backslash_alias.end(), '/', '\\');
                    AddVfsDatabaseAlias(
                        &rows, &row_keys, backslash_alias, synthetic_bank_candidate);
                    AddVfsBankRecord(&bank_records,
                                     &bank_record_keys,
                                     alias,
                                     synthetic_bank_candidate,
                                     normalized_root);
                    AddVfsBankRecord(&bank_records,
                                     &bank_record_keys,
                                     backslash_alias,
                                     synthetic_bank_candidate,
                                     normalized_root);
                }
            }
        }

        std::string line;
        std::getline(manifest, line);
        while (std::getline(manifest, line)) {
            if (!line.empty() && line.back() == '\r') {
                line.pop_back();
            }
            if (line.empty()) {
                continue;
            }

            const std::vector<std::string_view> columns = SplitTabs(line);
            if (columns.empty() || columns[0].empty()) {
                continue;
            }

            const std::string entry_path = std::string(columns[0]);
            const std::filesystem::path rooted_candidate =
                manifest_root / NormalizeRelativePath(entry_path);
            const std::string candidate = rooted_candidate.generic_string();
            if (!ShouldIndexVfsCandidate(candidate)) {
                continue;
            }

            manifest_covered_candidates.insert(NormalizeVfsKey(candidate));
            AddVfsDatabasePathAliases(&rows, &row_keys, candidate, entry_path);
            AddVfsBasenameCandidate(&basename_candidates, candidate);
        }
        ++active_summary->indexed_manifests;
    }

    for (const std::string& candidate : file_candidates) {
        if (manifest_covered_candidates.find(candidate) != manifest_covered_candidates.end() &&
            !IsDirectLooseVfsFile(candidate)) {
            continue;
        }
        AddVfsDatabasePathAliases(&rows, &row_keys, candidate, candidate);
    }

    for (const std::filesystem::path& filehash_path : filehash_paths) {
        std::ifstream csv(filehash_path);
        if (!csv) {
            continue;
        }

        const std::filesystem::path csv_relative =
            filehash_path.lexically_relative(unpacked_root);
        const std::string csv_parent =
            NormalizeVfsKey(csv_relative.parent_path().generic_string());
        const std::string csv_prefix =
            csv_parent.empty() ? std::string() : csv_parent + "/";

        std::string line;
        while (std::getline(csv, line)) {
            if (!line.empty() && line.back() == '\r') {
                line.pop_back();
            }
            const size_t comma = line.find(',');
            if (comma == std::string::npos || comma == 0 || comma + 1 >= line.size()) {
                continue;
            }

            std::uint64_t parsed_hash = 0;
            try {
                parsed_hash = std::stoull(line.substr(0, comma), nullptr, 10);
            } catch (const std::exception&) {
                continue;
            }
            if (parsed_hash == 0 || parsed_hash > std::numeric_limits<std::uint32_t>::max()) {
                continue;
            }

            const std::string basename = NormalizeVfsKey(line.substr(comma + 1));
            const auto match = basename_candidates.find(basename);
            if (match == basename_candidates.end()) {
                continue;
            }

            bool added_scoped_candidate = false;
            for (const std::string& candidate : match->second) {
                if (!csv_prefix.empty() && !StartsWith(candidate, csv_prefix)) {
                    continue;
                }
                AddVfsDatabaseExplicitHash(&rows,
                                           &row_keys,
                                           static_cast<std::uint32_t>(parsed_hash),
                                           basename,
                                           candidate);
                added_scoped_candidate = true;
            }
            if (added_scoped_candidate) {
                continue;
            }
            for (const std::string& candidate : match->second) {
                AddVfsDatabaseExplicitHash(&rows,
                                           &row_keys,
                                           static_cast<std::uint32_t>(parsed_hash),
                                           basename,
                                           candidate);
            }
        }
        ++active_summary->indexed_filehash_tables;
    }

    active_summary->entries = rows.size();
    if (write_legacy_database) {
        try {
            std::filesystem::create_directories(active_summary->database_path.parent_path());
        } catch (const std::exception& exception) {
            if (error != nullptr) {
                *error = exception.what();
            }
            return false;
        }

        std::ofstream database(active_summary->database_path, std::ios::trunc);
        if (!database) {
            if (error != nullptr) {
                *error = "failed to open VFS database for writing: " +
                         active_summary->database_path.string();
            }
            return false;
        }

        database << "# refii_vfs_database_v2\n";
        database << "# hash_hex\talias\tcandidate\n";
        for (const VfsDatabaseRow& row : rows) {
            const bool alias_matches_candidate = row.alias == row.candidate;
            database << std::hex << std::setw(8) << std::setfill('0') << row.hash
                     << std::dec << '\t';
            if (!alias_matches_candidate) {
                database << row.alias;
            }
            database << '\t' << row.candidate << '\n';
        }
    } else {
        std::error_code remove_error;
        std::filesystem::remove(default_legacy_database_path, remove_error);
    }

    if (!WriteRuntimeVfsIndex(active_summary->runtime_index_root,
                              rows,
                              bank_records,
                              lua_records,
                              active_summary,
                              error)) {
        return false;
    }

    return true;
}

bool ExtractBanks(const BankExtractionOptions& options,
                  BankExtractionSummary* summary,
                  std::string* error) {
    BankExtractionSummary local_summary;
    BankExtractionSummary* const active_summary = summary != nullptr ? summary : &local_summary;
    *active_summary = {};

    if (options.game_content_root.empty()) {
        if (error != nullptr) {
            *error = "game content root is required";
        }
        return false;
    }
    if (options.output_root.empty()) {
        if (error != nullptr) {
            *error = "bank extraction output root is required";
        }
        return false;
    }

    active_summary->game_content_root = std::filesystem::absolute(options.game_content_root);
    active_summary->output_root = std::filesystem::absolute(options.output_root);

    if (options.bank_relative_paths.empty()) {
        if (error != nullptr) {
            *error = "no game banks were selected for extraction";
        }
        return false;
    }

    try {
        std::filesystem::create_directories(active_summary->output_root);
    } catch (const std::exception& exception) {
        if (error != nullptr) {
            *error = exception.what();
        }
        return false;
    }

    if (options.clean_output) {
        std::error_code cleanup_error;
        std::filesystem::remove(ConflictReportPath(active_summary->output_root),
                                cleanup_error);
        cleanup_error.clear();
        std::filesystem::remove_all(ConflictStashRoot(active_summary->output_root),
                                    cleanup_error);
        if (cleanup_error) {
            if (error != nullptr) {
                *error = "failed to clean duplicate conflict stash: " +
                         cleanup_error.message();
            }
            return false;
        }
    }

    const std::size_t total_banks = options.bank_relative_paths.size();
    for (std::size_t index = 0; index < total_banks; ++index) {
        std::filesystem::path bank_relative_path;
        if (!NormalizeBankRelativePath(
                options.bank_relative_paths[index], &bank_relative_path, error)) {
            return false;
        }

        std::filesystem::path bank_path =
            active_summary->game_content_root / bank_relative_path;
        if (!std::filesystem::exists(bank_path)) {
            const std::filesystem::path case_insensitive_bank_path =
                ResolveCaseInsensitiveExistingPath(active_summary->game_content_root,
                                                   bank_relative_path);
            if (!case_insensitive_bank_path.empty() &&
                std::filesystem::exists(case_insensitive_bank_path)) {
                bank_path = case_insensitive_bank_path;
            } else {
                if (error != nullptr) {
                    *error = "bank not found: " + bank_path.string();
                }
                return false;
            }
        }

        const std::filesystem::path bank_output_root =
            GetBankOutputRoot(active_summary->output_root, bank_relative_path);
        if (options.clean_output) {
            std::error_code remove_error;
            std::filesystem::remove_all(bank_output_root, remove_error);
            if (remove_error) {
                if (error != nullptr) {
                    *error = "failed to clean " + bank_output_root.string() + ": " +
                             remove_error.message();
                }
                return false;
            }

            const std::filesystem::path legacy_bank_output_root =
                (active_summary->output_root / bank_relative_path.parent_path() /
                 bank_relative_path.stem())
                    .lexically_normal();
            if (legacy_bank_output_root != bank_output_root) {
                remove_error.clear();
                std::filesystem::remove_all(legacy_bank_output_root, remove_error);
                if (remove_error) {
                    if (error != nullptr) {
                        *error = "failed to clean " + legacy_bank_output_root.string() + ": " +
                                 remove_error.message();
                    }
                    return false;
                }
            }
        }

        EmitProgress(options.progress_callback,
                     "extracting_banks",
                     index,
                     total_banks,
                     bank_path);
        ExtractionContext context;
        context.output_root = active_summary->output_root;
        context.conflict_report_path = ConflictReportPath(active_summary->output_root);
        context.extract_payloads = options.extract_payloads;
        context.extract_nested_banks = options.extract_nested_banks;
        context.summary = active_summary;
        if (!ExtractOneBank(bank_path, bank_output_root, &context, error)) {
            return false;
        }
    }

    EmitProgress(options.progress_callback, "extracting_banks", total_banks, total_banks);
    if (options.build_vfs_database) {
        EmitProgress(options.progress_callback,
                     "building_vfs_database",
                     total_banks,
                     total_banks);
        VfsDatabaseBuildOptions database_options;
        database_options.unpacked_root = active_summary->output_root;
        VfsDatabaseBuildSummary database_summary;
        if (!BuildVfsDatabase(database_options, &database_summary, error)) {
            return false;
        }
        active_summary->vfs_database_entries = database_summary.entries;
    }
    return true;
}

}  // namespace refii::bnk
