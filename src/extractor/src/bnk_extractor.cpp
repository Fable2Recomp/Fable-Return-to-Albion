#include "extractor/bnk_extractor.h"

#include "gdb/gdb_database.h"
#include "babel/babel_database.h"

#include <zlib.h>

#include <algorithm>
#include <array>
#include <atomic>
#include <cctype>
#include <cstdint>
#include <filesystem>
#include <fstream>
#include <iomanip>
#include <limits>
#include <mutex>
#include <sstream>
#include <string>
#include <string_view>
#include <system_error>
#include <thread>
#include <tuple>
#include <unordered_map>
#include <unordered_set>
#include <vector>

namespace refii::bnk {

std::filesystem::path GetBankOutputRoot(
    const std::filesystem::path& output_root,
    const std::filesystem::path& bank_relative_path);

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

struct VfsDatabaseBuildState {
    std::filesystem::path unpacked_root;
    std::filesystem::path database_path;
    std::filesystem::path runtime_index_root;
    bool write_legacy_database = false;
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
    std::unordered_set<std::string> extracted_bank_roots;
    std::size_t indexed_files = 0;
    std::size_t indexed_manifests = 0;
    std::size_t indexed_filehash_tables = 0;
};

template <typename Value>
void ReserveVectorAtLeast(std::vector<Value>* values, std::size_t capacity) {
    if (values != nullptr && values->capacity() < capacity) {
        values->reserve(capacity);
    }
}

template <typename Value>
void ReserveSetAtLeast(std::unordered_set<Value>* values, std::size_t count) {
    if (values != nullptr && values->bucket_count() < count) {
        values->reserve(count);
    }
}

template <typename Key, typename Value>
void ReserveMapAtLeast(std::unordered_map<Key, Value>* values, std::size_t count) {
    if (values != nullptr && values->bucket_count() < count) {
        values->reserve(count);
    }
}

void ReserveVfsDatabaseStateForEntries(VfsDatabaseBuildState* state,
                                       std::size_t entry_count) {
    if (state == nullptr || entry_count == 0) {
        return;
    }

    const std::size_t row_hint = state->rows.size() + entry_count * 4;
    const std::size_t candidate_hint =
        state->manifest_covered_candidates.size() + entry_count;
    const std::size_t basename_hint =
        state->basename_candidates.size() + entry_count;
    const std::size_t lua_hint = state->lua_records.size() + entry_count / 4 + 16;

    ReserveVectorAtLeast(&state->rows, row_hint);
    ReserveSetAtLeast(&state->row_keys, row_hint);
    ReserveVectorAtLeast(&state->bank_records,
                         state->bank_records.size() + entry_count / 256 + 8);
    ReserveSetAtLeast(&state->bank_record_keys,
                      state->bank_record_keys.size() + entry_count / 256 + 8);
    ReserveVectorAtLeast(&state->lua_records, lua_hint);
    ReserveSetAtLeast(&state->lua_record_keys, lua_hint);
    ReserveMapAtLeast(&state->basename_candidates, basename_hint);
    ReserveSetAtLeast(&state->manifest_covered_candidates, candidate_hint);
}

inline std::uint64_t Fnv1aFingerprint(const std::vector<std::uint8_t>& bytes) {
    std::uint64_t hash = 1469598103934665603ull;
    for (const std::uint8_t byte : bytes) {
        hash ^= byte;
        hash *= 1099511628211ull;
    }
    return hash;
}

struct LoosePathOwner {
    std::size_t owner_ordinal = 0;
    std::uint64_t size = 0;
    std::uint64_t fingerprint = 0;
    bool multi_bank = false;
    bool fingerprint_valid = false;
};

struct ExtractionContext {
    std::filesystem::path output_root;
    std::filesystem::path conflict_report_path;
    bool extract_payloads = true;
    bool extract_nested_banks = true;
    bool convert_gdb_databases = false;
    bool convert_babel_databases = false;
    std::size_t payload_worker_count = 0;
    BankExtractionSummary* summary = nullptr;
    VfsDatabaseBuildState* vfs_database = nullptr;
    std::unordered_set<std::string> created_directories;
    std::mutex created_directories_mutex;
    // Loose-path owner per relative path across every bank in this run
    // (pre-pass): the LAST bank in extraction order to carry a name owns the
    // shared loose file (the historical overwrite behavior the rest of the
    // pipeline was validated against). Same-name entries are routed to
    // bank-scoped entries/ files via the manifest host_path column when their
    // bytes differ from the owner's: always when the size differs (texture /
    // model headers, mip variants), and for same-size entries when the
    // content fingerprint differs (world streaming variants).
    const std::unordered_map<std::string, LoosePathOwner>* loose_path_owners =
        nullptr;
    std::size_t bank_ordinal = 0;
};

struct PlannedBankEntry {
    std::size_t entry_index = 0;
    std::filesystem::path relative_path;
    std::filesystem::path host_relative_path;
    bool skip_duplicate = false;
    // Entry bytes routed to a bank-scoped file (cross-bank size conflict);
    // reachable only through this bank's manifest, so it must not register
    // as a global loose-alias candidate.
    bool bank_scoped = false;
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
    const std::filesystem::path candidate =
        NormalizeRelativePath(relative_path.generic_string()).lexically_normal();
    if (!IsSafeRelativePath(candidate)) {
        if (error != nullptr) {
            *error = "bank path must be a safe relative path";
        }
        return false;
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

// IDA/Lumen: the original language table includes these locale roots. Keep
// en-uk first to match runtime fallback scoring.
constexpr std::string_view kKnownLanguageLocales[] = {
    "en-uk", "en-us", "ja-jp", "de-de", "fr-fr", "es-es",
    "it-it", "ko-kr", "zh-tw", "zh-cht", "pt-pt", "zh-cn",
    "pl-pl", "ru-ru", "fr-be", "cs-cz", "da-dk", "fi-fi",
    "el-gr", "hu-hu", "es-la", "nl-nl", "nb-no", "sk-sk",
    "sv-se"};

void CollapseOriginalRootPrefix(std::string* key) {
    if (key == nullptr) {
        return;
    }

    if (StartsWith(*key, "game:/")) {
        key->erase(0, 6);
    } else if (StartsWith(*key, "game:")) {
        key->erase(0, 5);
    } else if (StartsWith(*key, "d:/")) {
        key->erase(0, 3);
    } else if (StartsWith(*key, "d:")) {
        key->erase(0, 2);
    } else if (StartsWith(*key, "cache:/")) {
        key->erase(0, 7);
    } else if (StartsWith(*key, "cache:")) {
        key->erase(0, 6);
    }
}

void CollapseMountedDeviceRoot(std::string* key) {
    if (key == nullptr) {
        return;
    }

    constexpr std::string_view kMountedGameRoot = "device/harddisk0/partition1/";
    constexpr std::string_view kMountedUpdateRoot =
        "device/harddisk0/partitionupdate/";
    if (StartsWith(*key, kMountedGameRoot)) {
        key->erase(0, kMountedGameRoot.size());
    } else if (*key == "device/harddisk0/partition1") {
        key->clear();
    } else if (StartsWith(*key, kMountedUpdateRoot)) {
        key->replace(0, kMountedUpdateRoot.size(), "update/");
    } else if (*key == "device/harddisk0/partitionupdate") {
        *key = "update";
    }
}

void CollapseVfsKeySeparatorsAndDotSegments(std::string* key) {
    if (key == nullptr || key->empty()) {
        return;
    }

    std::string collapsed;
    collapsed.reserve(key->size());
    size_t position = 0;
    while (position < key->size()) {
        const size_t next_separator = key->find('/', position);
        const size_t count =
            next_separator == std::string::npos ? key->size() - position
                                                : next_separator - position;
        const std::string_view component =
            std::string_view(*key).substr(position, count);
        position = next_separator == std::string::npos ? key->size()
                                                       : next_separator + 1;
        if (component.empty() || component == ".") {
            continue;
        }
        if (!collapsed.empty()) {
            collapsed += '/';
        }
        collapsed.append(component);
    }
    *key = std::move(collapsed);
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
    CollapseOriginalRootPrefix(&key);
    while (!key.empty() && key.front() == '/') {
        key.erase(key.begin());
    }
    CollapseMountedDeviceRoot(&key);
    CollapseVfsKeySeparatorsAndDotSegments(&key);
    return key;
}

bool IsLuaSourceRoot(std::string_view normalized) {
    return StartsWith(normalized, "luasource/") || StartsWith(normalized, "luasource");
}

bool IsGameFaceBinaryAssetKey(std::string_view normalized) {
    return EndsWith(normalized, ".bgf") || EndsWith(normalized, ".bsg") ||
           EndsWith(normalized, ".fac");
}

bool IsLuaSourceGameFaceCarrier(std::string_view normalized) {
    const std::string key = NormalizeVfsKey(normalized);
    return StartsWith(key, "luasource/guiscripts/art/gui/gameface/") &&
           IsGameFaceBinaryAssetKey(key);
}

bool ShouldIndexNormalizedVfsCandidate(std::string_view normalized) {
    if (normalized.empty() || normalized == "_refii_vfs.tsv") {
        return false;
    }
    return (IsLuaSourceGameFaceCarrier(normalized) || !IsLuaSourceRoot(normalized)) &&
           !StartsWith(normalized, ".baseline/") &&
           !StartsWith(normalized, ".cache/") && !StartsWith(normalized, "_disasm/") &&
           !StartsWith(normalized, "refii_vfs/") &&
           !StartsWith(normalized, "_case_conflicts/") &&
           normalized.find("/_case_conflicts/") == std::string::npos &&
           !StartsWith(normalized, "scripts/") && !StartsWith(normalized, "scripts_r/") &&
           !StartsWith(normalized, "data/scripts/") &&
           !StartsWith(normalized, "data/scripts_r/") &&
           normalized != "_manifest.tsv" && !EndsWith(normalized, "/_manifest.tsv");
}

bool ShouldIndexVfsCandidate(std::string_view value) {
    const std::string normalized = NormalizeVfsKey(value);
    return ShouldIndexNormalizedVfsCandidate(normalized);
}

bool IsGeneratedVfsScanDirectory(std::string_view value) {
    const std::string normalized = NormalizeVfsKey(value);
    const bool refii_vfs_generated =
        StartsWith(normalized, "refii_vfs/") &&
        normalized != "refii_vfs/banks" &&
        !StartsWith(normalized, "refii_vfs/banks/");
    return normalized == ".baseline" || StartsWith(normalized, ".baseline/") ||
           normalized == ".cache" || StartsWith(normalized, ".cache/") ||
           refii_vfs_generated ||
           normalized == "_disasm" || StartsWith(normalized, "_disasm/") ||
           normalized == "_case_conflicts" ||
           StartsWith(normalized, "_case_conflicts/");
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

bool ShouldAddDataRootAlias(std::string_view normalized_alias) {
    if (normalized_alias.empty()) {
        return false;
    }
    if (EndsWith(normalized_alias, ".bnk")) {
        return true;
    }
    return normalized_alias.find('/') != std::string_view::npos;
}

void AddVfsDatabaseAlias(std::vector<VfsDatabaseRow>* rows,
                         std::unordered_set<std::string>* row_keys,
                         std::string_view hash_input,
                         std::string_view candidate) {
    if (hash_input.empty()) {
        return;
    }
    const std::string raw_alias(hash_input);
    const std::string normalized_alias = NormalizeVfsKey(hash_input);
    AddVfsDatabaseRow(rows,
                      row_keys,
                      ComputeResourceNameHash(raw_alias),
                      raw_alias,
                      candidate);
    if (!ShouldAddDataRootAlias(normalized_alias)) {
        return;
    }

    // Native game paths commonly arrive under game:\data\, while the unpacked
    // database stores candidates at the stripped logical root.
    const std::string data_alias =
        StartsWith(normalized_alias, "data/")
            ? std::string(std::string_view(normalized_alias).substr(5))
            : "data/" + normalized_alias;
    std::string data_hash_input = data_alias;
    if (raw_alias.find('\\') != std::string::npos) {
        std::replace(data_hash_input.begin(), data_hash_input.end(), '/', '\\');
    }
    AddVfsDatabaseRow(rows,
                      row_keys,
                      ComputeResourceNameHash(data_hash_input),
                      data_alias,
                      candidate);
}

void AddVfsDatabaseExplicitHash(std::vector<VfsDatabaseRow>* rows,
                                std::unordered_set<std::string>* row_keys,
                                std::uint32_t hash,
                                std::string_view alias,
                                std::string_view candidate) {
    AddVfsDatabaseRow(rows, row_keys, hash, alias, candidate);
}

void AddVfsDatabaseAliasWithSeparatorVariants(
    std::vector<VfsDatabaseRow>* rows,
    std::unordered_set<std::string>* row_keys,
    std::string_view alias,
    std::string_view candidate) {
    AddVfsDatabaseAlias(rows, row_keys, alias, candidate);
    std::string backslash_alias(alias);
    std::replace(backslash_alias.begin(), backslash_alias.end(), '/', '\\');
    AddVfsDatabaseAlias(rows, row_keys, backslash_alias, candidate);
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

bool SplitLocalizedLanguagePath(std::string_view normalized,
                                std::string_view* language_root,
                                std::string_view* language_tail,
                                std::string_view* asset_tail) {
    constexpr std::string_view kLanguagePrefix = "language/";
    constexpr std::string_view kLanguageDlcPrefix = "language_dlc/";

    std::string_view root;
    std::string_view tail;
    if (StartsWith(normalized, kLanguagePrefix)) {
        root = "language";
        tail = normalized.substr(kLanguagePrefix.size());
    } else if (StartsWith(normalized, kLanguageDlcPrefix)) {
        root = "language_dlc";
        tail = normalized.substr(kLanguageDlcPrefix.size());
    } else {
        return false;
    }

    const size_t locale_end = tail.find('/');
    if (locale_end == std::string_view::npos || locale_end + 1 >= tail.size()) {
        return false;
    }

    if (language_root != nullptr) {
        *language_root = root;
    }
    if (language_tail != nullptr) {
        *language_tail = tail;
    }
    if (asset_tail != nullptr) {
        *asset_tail = tail.substr(locale_end + 1);
    }
    return true;
}

bool IsKnownLanguageLocaleComponent(std::string_view value) {
    for (const std::string_view locale : kKnownLanguageLocales) {
        if (value == locale) {
            return true;
        }
    }
    return false;
}

std::string CanonicalizeRuntimeAliasKey(std::string_view normalized_key) {
    // Canonical database alias spelling shared with the runtime lookup
    // (CanonicalizeRefiiVfsDatabaseAliasKey): requests reachable by mechanical
    // rewrites collapse onto one stored row, so those spellings are not
    // materialized.
    std::string_view key = normalized_key;
    if (StartsWith(key, "data/")) {
        key.remove_prefix(5);
    }

    constexpr std::string_view kLanguagePrefix = "language/";
    constexpr std::string_view kLanguageDlcPrefix = "language_dlc/";
    std::string_view root;
    std::string_view tail;
    if (StartsWith(key, kLanguagePrefix)) {
        root = key.substr(0, kLanguagePrefix.size() - 1);
        tail = key.substr(kLanguagePrefix.size());
    } else if (StartsWith(key, kLanguageDlcPrefix)) {
        root = key.substr(0, kLanguageDlcPrefix.size() - 1);
        tail = key.substr(kLanguageDlcPrefix.size());
    } else {
        return std::string(key);
    }

    const size_t first_separator = tail.find('/');
    if (first_separator != std::string_view::npos &&
        first_separator + 1 < tail.size() &&
        IsKnownLanguageLocaleComponent(tail.substr(0, first_separator))) {
        tail = tail.substr(first_separator + 1);
    }

    std::string canonical(root);
    canonical += '/';
    constexpr std::string_view kPatchTextPrefix = "patch_text/";
    if (StartsWith(tail, kPatchTextPrefix)) {
        canonical += "text/";
        canonical += tail.substr(kPatchTextPrefix.size());
    } else {
        canonical += tail;
    }
    return canonical;
}

std::string BuildLanguageRootTailRedirectAlias(std::string_view candidate) {
    const std::string normalized = NormalizeVfsKey(candidate);
    std::string_view language_root;
    std::string_view asset_tail;
    if (!SplitLocalizedLanguagePath(normalized, &language_root, nullptr, &asset_tail)) {
        return {};
    }

    // Original language resource registration hashes both full bank names
    // (sub_82382DE8/sub_82383148) and tail fragments
    // (sub_82381D90/sub_82382148). sub_823817E8 walks both language and
    // language_dlc roots, so preserve the source root while dropping locale.
    return std::string(language_root) + "/" + std::string(asset_tail);
}

std::string BuildShortLanguageVideoRedirectAlias(std::string_view candidate) {
    const std::string normalized = NormalizeVfsKey(candidate);
    std::string_view asset_tail;
    if (!SplitLocalizedLanguagePath(normalized, nullptr, nullptr, &asset_tail)) {
        return {};
    }

    if (!StartsWith(asset_tail, "videos/") || !EndsWith(asset_tail, ".bik")) {
        return {};
    }

    // IDA/Lumen: sub_82381D90/sub_82382148 register localized videos by the
    // short videos/*.bik tail before the language/videos root is resolved.
    return std::string(asset_tail);
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
    AddUniqueString(&aliases, BuildLanguageRootTailRedirectAlias(candidate));
    AddUniqueString(&aliases, BuildShortLanguageVideoRedirectAlias(candidate));

    for (const std::string& alias : aliases) {
        AddVfsDatabaseAliasWithSeparatorVariants(rows, row_keys, alias, candidate);
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

    auto add_record = [&](std::string record_alias) {
        const std::string key =
            record_alias + '\t' + normalized_candidate + '\t' + normalized_manifest_root;
        if (!keys->insert(key).second) {
            return;
        }
        records->push_back(VfsBankRecord{std::move(record_alias),
                                         normalized_candidate,
                                         normalized_manifest_root});
    };

    add_record(normalized_alias);
    if (StartsWith(normalized_alias, "data/")) {
        add_record(std::string(std::string_view(normalized_alias).substr(5)));
    } else {
        add_record("data/" + normalized_alias);
    }
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

    // Extracted .gdb / .babel databases are stored as editable .csv; the
    // runtime compiles them back on demand.
    if (EndsWith(normalized, ".gdb.csv") || EndsWith(normalized, ".babel.csv")) {
        return true;
    }

    const size_t dot = normalized.find_last_of('.');
    if (dot == std::string::npos) {
        return false;
    }
    const std::string_view extension = std::string_view(normalized).substr(dot);
    return extension == ".bik" || extension == ".bft" || extension == ".babel" ||
           extension == ".adb" || extension == ".gdb" || extension == ".ini" ||
           extension == ".bgf" || extension == ".bsg" || extension == ".fac";
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

void RemoveEmptyDirectoriesUnder(const std::filesystem::path& root) {
    std::vector<std::filesystem::path> directories;
    std::error_code iterator_error;
    constexpr auto options = std::filesystem::directory_options::skip_permission_denied;
    for (std::filesystem::recursive_directory_iterator it(root, options, iterator_error), end;
         it != end; it.increment(iterator_error)) {
        if (iterator_error) {
            iterator_error.clear();
            continue;
        }

        std::error_code status_error;
        if (it->is_directory(status_error)) {
            directories.push_back(it->path());
        }
        status_error.clear();
    }

    std::sort(directories.begin(), directories.end(), [](const auto& lhs, const auto& rhs) {
        return lhs.generic_string().size() > rhs.generic_string().size();
    });

    for (const auto& directory : directories) {
        std::error_code remove_error;
        std::filesystem::remove(directory, remove_error);
    }

    std::error_code remove_root_error;
    std::filesystem::remove(root, remove_root_error);
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
    constexpr auto options = std::filesystem::directory_options::skip_permission_denied;
    for (std::filesystem::recursive_directory_iterator it(source_root, options, iterator_error), end;
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

        const std::filesystem::path relative_path =
            LowercaseRelativePath(it->path().lexically_relative(source_root).lexically_normal());
        const std::filesystem::path destination_path =
            destination_root / relative_path;
        std::error_code create_parent_error;
        std::filesystem::create_directories(destination_path.parent_path(), create_parent_error);
        if (create_parent_error) {
            if (error != nullptr) {
                *error = "failed to create canonical globals file directory: " +
                         destination_path.parent_path().string() + ": " +
                         create_parent_error.message();
            }
            return false;
        }

        // Editable database .csv twins only propagate when the source is
        // newer, so user edits on either side of the mirror survive rebuilds.
        const std::string mirror_name =
            NormalizeVfsKey(destination_path.filename().generic_string());
        if (EndsWith(mirror_name, ".gdb.csv") ||
            EndsWith(mirror_name, ".babel.csv")) {
            std::error_code time_error;
            const auto destination_time =
                std::filesystem::last_write_time(destination_path, time_error);
            if (!time_error &&
                destination_time >=
                    std::filesystem::last_write_time(it->path(), time_error)) {
                continue;
            }
        }

        std::error_code copy_error;
        std::filesystem::copy_file(it->path(),
                                   destination_path,
                                   std::filesystem::copy_options::overwrite_existing,
                                   copy_error);
        if (copy_error) {
            if (error != nullptr) {
                *error = "failed to canonicalize Globals file to lowercase globals: " +
                         it->path().string() + " -> " + destination_path.string() +
                         ": " + copy_error.message();
            }
            return false;
        }

        std::error_code remove_error;
        std::filesystem::remove(it->path(), remove_error);
        if (remove_error) {
            if (error != nullptr) {
                *error = "failed to remove duplicate uppercase Globals file after "
                         "canonicalization: " +
                         it->path().string() + ": " + remove_error.message();
            }
            return false;
        }
    }

    RemoveEmptyDirectoriesUnder(source_root);
    return true;
}

std::uint32_t GetRuntimeCandidateIdNormalized(
    std::unordered_map<std::string, std::uint32_t>* ids,
    std::vector<std::string>* candidates,
    std::string_view normalized_candidate) {
    if (!ShouldIndexNormalizedVfsCandidate(normalized_candidate)) {
        return std::numeric_limits<std::uint32_t>::max();
    }

    std::string normalized_key(normalized_candidate);
    if (const auto existing = ids->find(normalized_key); existing != ids->end()) {
        return existing->second;
    }

    const std::uint32_t id = static_cast<std::uint32_t>(candidates->size());
    candidates->push_back(std::move(normalized_key));
    ids->emplace(candidates->back(), id);
    return id;
}

std::uint32_t GetRuntimeCandidateId(std::unordered_map<std::string, std::uint32_t>* ids,
                                    std::vector<std::string>* candidates,
                                    std::string_view candidate) {
    const std::string normalized_candidate = NormalizeVfsKey(candidate);
    return GetRuntimeCandidateIdNormalized(ids, candidates, normalized_candidate);
}

void AddRuntimeIndexAliasNormalized(
    std::array<std::vector<std::pair<std::string, std::uint32_t>>, 4096>* shards,
    std::unordered_set<std::string>* keys,
    std::unordered_map<std::string, std::uint32_t>* candidate_ids,
    std::vector<std::string>* candidates,
    std::string_view normalized_alias,
    std::string_view normalized_candidate) {
    const std::uint32_t candidate_id =
        GetRuntimeCandidateIdNormalized(candidate_ids, candidates, normalized_candidate);
    if (normalized_alias.empty() ||
        candidate_id == std::numeric_limits<std::uint32_t>::max()) {
        return;
    }

    // Only the canonical spelling is stored; the runtime rewrites requests to
    // the same canonical form. An alias equal to its candidate is resolved at
    // runtime through the candidate table, not a materialized row.
    const std::string canonical_alias =
        CanonicalizeRuntimeAliasKey(normalized_alias);
    if (canonical_alias.empty() || canonical_alias == normalized_candidate) {
        return;
    }

    std::string key(canonical_alias);
    key += '\t';
    key += std::to_string(candidate_id);
    if (!keys->insert(key).second) {
        return;
    }

    const std::uint16_t shard =
        RuntimeAliasShardForHash(ComputeResourceNameHash(canonical_alias));
    (*shards)[shard].emplace_back(canonical_alias, candidate_id);
}

void AddRuntimeIndexAlias(
    std::array<std::vector<std::pair<std::string, std::uint32_t>>, 4096>* shards,
    std::unordered_set<std::string>* keys,
    std::unordered_map<std::string, std::uint32_t>* candidate_ids,
    std::vector<std::string>* candidates,
    std::string_view alias,
    std::string_view candidate) {
    const std::string normalized_alias = NormalizeVfsKey(alias);
    const std::string normalized_candidate = NormalizeVfsKey(candidate);
    AddRuntimeIndexAliasNormalized(
        shards, keys, candidate_ids, candidates, normalized_alias, normalized_candidate);
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

std::string StripRuntimeDataPrefix(std::string value) {
    if (StartsWith(value, "data/")) {
        value.erase(0, 5);
    }
    while (!value.empty() && value.back() == '/') {
        value.pop_back();
    }
    return value;
}

struct RuntimeDirectoryEntry {
    std::string parent;
    std::string name;
    bool directory = false;
};

void AddRuntimeDirectoryEntry(std::vector<RuntimeDirectoryEntry>* entries,
                              std::unordered_set<std::string>* keys,
                              std::string_view path,
                              bool directory) {
    if (entries == nullptr || keys == nullptr) {
        return;
    }

    std::string normalized = StripRuntimeDataPrefix(NormalizeVfsKey(path));
    if (normalized.empty() || !ShouldIndexVfsCandidate(normalized)) {
        return;
    }

    const size_t separator = normalized.find_last_of('/');
    std::string parent;
    std::string name;
    if (separator == std::string::npos) {
        name = std::move(normalized);
    } else {
        parent = normalized.substr(0, separator);
        name = normalized.substr(separator + 1);
    }
    if (name.empty()) {
        return;
    }

    const std::string key = parent + '\t' + name;
    if (!keys->insert(key).second) {
        return;
    }

    entries->push_back(RuntimeDirectoryEntry{
        .parent = std::move(parent),
        .name = std::move(name),
        .directory = directory,
    });
}

std::vector<RuntimeDirectoryEntry> BuildRuntimeDirectoryEntries(
    const std::vector<std::string>& candidates,
    const std::unordered_set<std::string>& directories,
    const std::vector<VfsDatabaseRow>& rows,
    const std::vector<VfsLuaRecord>& lua_records) {
    std::vector<RuntimeDirectoryEntry> entries;
    entries.reserve(directories.size() + candidates.size() + rows.size() +
                    lua_records.size() * 2);
    std::unordered_set<std::string> keys;
    keys.reserve(entries.capacity());

    for (const std::string& directory : directories) {
        AddRuntimeDirectoryEntry(&entries, &keys, directory, true);
    }
    for (const std::string& candidate : candidates) {
        AddRuntimeDirectoryEntry(&entries, &keys, candidate, false);
    }
    for (const VfsDatabaseRow& row : rows) {
        if (!row.alias.empty() && row.alias != row.candidate) {
            AddRuntimeDirectoryEntry(&entries, &keys, row.alias, false);
        }
    }
    for (const VfsLuaRecord& record : lua_records) {
        AddRuntimeDirectoryEntry(&entries, &keys, record.alias, false);
        if (record.candidate != record.alias) {
            AddRuntimeDirectoryEntry(&entries, &keys, record.candidate, false);
        }
    }

    std::sort(entries.begin(),
              entries.end(),
              [](const RuntimeDirectoryEntry& lhs, const RuntimeDirectoryEntry& rhs) {
                  if (lhs.parent != rhs.parent) {
                      return lhs.parent < rhs.parent;
                  }
                  if (lhs.directory != rhs.directory) {
                      return lhs.directory && !rhs.directory;
                  }
                  return lhs.name < rhs.name;
              });
    return entries;
}

void WriteLittleEndianU32(std::ostream* output, std::uint32_t value) {
    const std::array<char, 4> bytes{
        static_cast<char>(value & 0xFFu),
        static_cast<char>((value >> 8) & 0xFFu),
        static_cast<char>((value >> 16) & 0xFFu),
        static_cast<char>((value >> 24) & 0xFFu),
    };
    output->write(bytes.data(), static_cast<std::streamsize>(bytes.size()));
}

void WriteRuntimeDirectoryEntryString(std::ostream* output,
                                      std::string_view value) {
    WriteLittleEndianU32(output, static_cast<std::uint32_t>(value.size()));
    output->write(value.data(), static_cast<std::streamsize>(value.size()));
}

bool WriteRuntimeDirectoryEntriesBinary(
    const std::filesystem::path& path,
    const std::vector<RuntimeDirectoryEntry>& entries,
    std::string* error) {
    std::ofstream output(path, std::ios::binary | std::ios::trunc);
    if (!output) {
        if (error != nullptr) {
            *error = "failed to open binary VFS directory entry table for writing: " +
                     path.string();
        }
        return false;
    }

    std::uint32_t group_count = 0;
    std::string_view previous_parent;
    for (const RuntimeDirectoryEntry& entry : entries) {
        if (group_count == 0 || entry.parent != previous_parent) {
            ++group_count;
            previous_parent = entry.parent;
        }
    }

    output.write("RFDIREN2", 8);
    WriteLittleEndianU32(&output, group_count);

    size_t index = 0;
    while (index < entries.size()) {
        const std::string_view parent = entries[index].parent;
        size_t end = index + 1;
        while (end < entries.size() && entries[end].parent == parent) {
            ++end;
        }

        WriteRuntimeDirectoryEntryString(&output, parent);
        WriteLittleEndianU32(
            &output, static_cast<std::uint32_t>(end - index));
        for (size_t entry_index = index; entry_index < end; ++entry_index) {
            const char type = entries[entry_index].directory ? 'd' : 'f';
            output.write(&type, 1);
            WriteRuntimeDirectoryEntryString(&output, entries[entry_index].name);
        }
        index = end;
    }

    if (!output) {
        if (error != nullptr) {
            *error = "failed to write binary VFS directory entry table: " +
                     path.string();
        }
        return false;
    }
    return true;
}

template <typename ShardArray>
void SortRuntimeIndexShards(ShardArray* shards) {
    if (shards == nullptr || shards->empty()) {
        return;
    }

    const unsigned hardware_threads = std::thread::hardware_concurrency();
    const std::size_t worker_count =
        std::max<std::size_t>(1, std::min<std::size_t>(
                                     hardware_threads == 0 ? 1 : hardware_threads,
                                     shards->size()));
    if (worker_count == 1) {
        for (auto& shard : *shards) {
            std::sort(shard.begin(), shard.end());
        }
        return;
    }

    std::atomic<std::size_t> next_shard{0};
    std::vector<std::thread> workers;
    workers.reserve(worker_count);
    for (std::size_t worker = 0; worker < worker_count; ++worker) {
        workers.emplace_back([&]() {
            for (;;) {
                const std::size_t shard_index =
                    next_shard.fetch_add(1, std::memory_order_relaxed);
                if (shard_index >= shards->size()) {
                    break;
                }
                auto& shard = (*shards)[shard_index];
                if (!shard.empty()) {
                    std::sort(shard.begin(), shard.end());
                }
            }
        });
    }
    for (std::thread& worker : workers) {
        worker.join();
    }
}

template <typename Callback>
bool ForEachRuntimeIndexShardParallel(std::size_t shard_count,
                                      Callback callback,
                                      std::string* error) {
    const unsigned hardware_threads = std::thread::hardware_concurrency();
    const std::size_t worker_count =
        std::max<std::size_t>(1, std::min<std::size_t>(
                                     hardware_threads == 0 ? 1 : hardware_threads,
                                     shard_count));
    if (worker_count == 1) {
        for (std::size_t shard = 0; shard < shard_count; ++shard) {
            std::string shard_error;
            if (!callback(shard, &shard_error)) {
                if (error != nullptr) {
                    *error = std::move(shard_error);
                }
                return false;
            }
        }
        return true;
    }

    std::atomic<std::size_t> next_shard{0};
    std::atomic<bool> failed{false};
    std::mutex error_mutex;
    std::string first_error;
    std::vector<std::thread> workers;
    workers.reserve(worker_count);
    for (std::size_t worker = 0; worker < worker_count; ++worker) {
        workers.emplace_back([&]() {
            for (;;) {
                const std::size_t shard =
                    next_shard.fetch_add(1, std::memory_order_relaxed);
                if (shard >= shard_count || failed.load(std::memory_order_relaxed)) {
                    break;
                }

                std::string shard_error;
                if (!callback(shard, &shard_error)) {
                    {
                        std::lock_guard<std::mutex> lock(error_mutex);
                        if (first_error.empty()) {
                            first_error = std::move(shard_error);
                        }
                    }
                    failed.store(true, std::memory_order_relaxed);
                    break;
                }
            }
        });
    }
    for (std::thread& worker : workers) {
        worker.join();
    }

    if (failed.load(std::memory_order_relaxed)) {
        if (error != nullptr) {
            *error = first_error.empty() ? "failed to write VFS runtime shard"
                                         : std::move(first_error);
        }
        return false;
    }
    return true;
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
    std::unordered_set<std::uint64_t> hash_keys;
    std::unordered_set<std::uint32_t> owned_hashes;
    std::unordered_set<std::string> directories;
    std::unordered_set<std::string> runtime_parent_candidates;
    std::unordered_map<std::string, std::uint32_t> candidate_ids;
    std::vector<std::string> candidates;
    alias_keys.reserve(rows.size());
    hash_keys.reserve(rows.size());
    owned_hashes.reserve(rows.size());
    directories.reserve(rows.size());
    runtime_parent_candidates.reserve(rows.size() / 2 + 16);
    candidate_ids.reserve(rows.size());
    candidates.reserve(rows.size() / 2);

    for (const VfsDatabaseRow& row : rows) {
        const std::string alias = row.alias.empty() ? row.candidate : row.alias;
        const std::string& normalized_candidate = row.candidate;
        AddRuntimeIndexAliasNormalized(&alias_shards,
                                       &alias_keys,
                                       &candidate_ids,
                                       &candidates,
                                       alias,
                                       normalized_candidate);
        if (runtime_parent_candidates.insert(normalized_candidate).second) {
            AddRuntimeParentDirectories(&directories, normalized_candidate);
        }
        if (!row.alias.empty()) {
            AddRuntimeParentDirectories(&directories, row.alias);
        }

        if (row.hash == 0 || !ShouldIndexNormalizedVfsCandidate(normalized_candidate)) {
            continue;
        }

        const std::uint32_t candidate_id =
            GetRuntimeCandidateIdNormalized(
                &candidate_ids, &candidates, normalized_candidate);
        if (candidate_id == std::numeric_limits<std::uint32_t>::max()) {
            continue;
        }
        const std::uint64_t key =
            (static_cast<std::uint64_t>(row.hash) << 32) |
            static_cast<std::uint64_t>(candidate_id);
        if (!hash_keys.insert(key).second) {
            continue;
        }
        const std::uint8_t shard = RuntimeIndexShardForHash(row.hash);
        hash_shards[shard].emplace_back(row.hash, candidate_id);
        owned_hashes.insert(row.hash);
    }

    const std::filesystem::path alias_root = runtime_index_root / "aliases";
    const std::filesystem::path hash_root = runtime_index_root / "hashes";
    SortRuntimeIndexShards(&alias_shards);
    SortRuntimeIndexShards(&hash_shards);

    try {
        std::filesystem::create_directories(runtime_index_root);
        std::filesystem::remove_all(alias_root);
        std::filesystem::remove_all(hash_root);
        for (const char* filename : {"candidates.tsv",
                                     "directories.tsv",
                                     "directory_entries.tsv",
                                     "directory_entries.bin",
                                     "banks.tsv",
                                     "lua.tsv",
                                     "hash_ownership.tsv",
                                     "meta.tsv",
                                     "unindexed_candidates.tsv"}) {
            std::filesystem::remove(runtime_index_root / filename);
        }
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

    std::vector<std::string> sorted_directories(directories.begin(), directories.end());
    std::sort(sorted_directories.begin(), sorted_directories.end());

    const std::vector<RuntimeDirectoryEntry> directory_entries =
        BuildRuntimeDirectoryEntries(candidates, directories, rows, lua_records);
    const std::filesystem::path directory_entries_bin_path =
        runtime_index_root / "directory_entries.bin";
    if (!WriteRuntimeDirectoryEntriesBinary(
            directory_entries_bin_path, directory_entries, error)) {
        return false;
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
    std::vector<VfsBankRecord> sorted_bank_records = bank_records;
    std::sort(sorted_bank_records.begin(),
              sorted_bank_records.end(),
              [](const VfsBankRecord& lhs, const VfsBankRecord& rhs) {
                  return std::tie(lhs.alias, lhs.candidate, lhs.manifest_root) <
                         std::tie(rhs.alias, rhs.candidate, rhs.manifest_root);
              });
    for (const VfsBankRecord& record : sorted_bank_records) {
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
    std::vector<VfsLuaRecord> sorted_lua_records = lua_records;
    std::sort(sorted_lua_records.begin(),
              sorted_lua_records.end(),
              [](const VfsLuaRecord& lhs, const VfsLuaRecord& rhs) {
                  return std::tie(lhs.alias, lhs.candidate, lhs.kind) <
                         std::tie(rhs.alias, rhs.candidate, rhs.kind);
              });
    std::unordered_set<std::string> carrier_lua_keys;
    for (const VfsLuaRecord& record : sorted_lua_records) {
        if (record.kind == "carrier") {
            carrier_lua_keys.insert(record.alias + '\t' + record.candidate);
        }
    }
    std::size_t emitted_lua_record_count = 0;
    for (const VfsLuaRecord& record : sorted_lua_records) {
        if (record.kind == "override" &&
            carrier_lua_keys.find(record.alias + '\t' + record.candidate) !=
                carrier_lua_keys.end()) {
            continue;
        }
        lua_output << record.alias << '\t' << record.candidate << '\t'
                   << record.kind << '\n';
        ++emitted_lua_record_count;
    }

    std::atomic<std::size_t> alias_entry_count_atomic{0};
    if (!ForEachRuntimeIndexShardParallel(
            alias_shards.size(),
            [&](std::size_t shard, std::string* shard_error) {
        const auto& aliases = alias_shards[shard];
        if (aliases.empty()) {
            return true;
        }
        const std::filesystem::path shard_path =
            alias_root / (HexUint12(static_cast<std::uint16_t>(shard)) + ".tsv");
        std::ofstream output(shard_path, std::ios::trunc);
        if (!output) {
            if (shard_error != nullptr) {
                *shard_error = "failed to open VFS alias shard for writing: " +
                         shard_path.string();
            }
            return false;
        }
        output << "# refii_vfs_alias_index_v3\n";
        output << "# alias\tcandidate_id\n";
        std::size_t shard_entry_count = 0;
        for (const auto& [alias, candidate_id] : aliases) {
            if (candidate_id >= candidates.size()) {
                continue;
            }
            output << alias << '\t' << candidate_id << '\n';
            ++shard_entry_count;
        }
        if (!output) {
            if (shard_error != nullptr) {
                *shard_error = "failed to write VFS alias shard: " +
                               shard_path.string();
            }
            return false;
        }
        alias_entry_count_atomic.fetch_add(
            shard_entry_count, std::memory_order_relaxed);
        return true;
            },
            error)) {
        return false;
    }
    alias_entry_count = alias_entry_count_atomic.load(std::memory_order_relaxed);

    std::atomic<std::size_t> hash_entry_count_atomic{0};
    if (!ForEachRuntimeIndexShardParallel(
            hash_shards.size(),
            [&](std::size_t shard, std::string* shard_error) {
        const auto& hashes = hash_shards[shard];
        if (hashes.empty()) {
            return true;
        }
        const std::filesystem::path shard_path =
            hash_root / (HexByte(static_cast<std::uint8_t>(shard)) + ".tsv");
        std::ofstream output(shard_path, std::ios::trunc);
        if (!output) {
            if (shard_error != nullptr) {
                *shard_error = "failed to open VFS hash shard for writing: " +
                         shard_path.string();
            }
            return false;
        }
        output << "# refii_vfs_hash_index_v3\n";
        output << "# hash_hex\tcandidate_id\n";
        std::size_t shard_entry_count = 0;
        for (const auto& [hash, candidate_id] : hashes) {
            if (candidate_id >= candidates.size()) {
                continue;
            }
            output << HexUint32(hash) << '\t' << candidate_id << '\n';
            ++shard_entry_count;
        }
        if (!output) {
            if (shard_error != nullptr) {
                *shard_error = "failed to write VFS hash shard: " +
                               shard_path.string();
            }
            return false;
        }
        hash_entry_count_atomic.fetch_add(
            shard_entry_count, std::memory_order_relaxed);
        return true;
            },
            error)) {
        return false;
    }
    hash_entry_count = hash_entry_count_atomic.load(std::memory_order_relaxed);

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
    metadata << "# refii_vfs_runtime_index_v6\n";
    metadata << "candidates\t" << candidates.size() << '\n';
    metadata << "alias_entries\t" << alias_entry_count << '\n';
    metadata << "hash_entries\t" << hash_entry_count << '\n';
    metadata << "owned_hashes\t" << sorted_owned_hashes.size() << '\n';
    metadata << "directories\t" << sorted_directories.size() << '\n';
    metadata << "directory_entries\t" << directory_entries.size() << '\n';
    metadata << "bank_records\t" << bank_records.size() << '\n';
    metadata << "lua_records\t" << emitted_lua_record_count << '\n';

    if (summary != nullptr) {
        summary->runtime_index_root = runtime_index_root;
        summary->runtime_alias_entries = alias_entry_count;
        summary->runtime_hash_entries = hash_entry_count;
        summary->runtime_directories = sorted_directories.size();
        summary->runtime_candidates = candidates.size();
    }
    return true;
}

bool IsAuditableNormalizedVfsFileCandidate(std::string_view normalized) {
    if (!ShouldIndexVfsCandidate(normalized)) {
        return false;
    }
    const std::string filename =
        NormalizeVfsKey(std::filesystem::path(std::string(normalized))
                            .filename()
                            .generic_string());
    return filename != "filehash.csv";
}

std::vector<std::string> CollectUnindexedVfsFileCandidates(
    const std::vector<std::string>& file_candidates,
    const std::vector<VfsDatabaseRow>& rows) {
    std::unordered_set<std::string> indexed_candidates;
    indexed_candidates.reserve(rows.size());
    for (const VfsDatabaseRow& row : rows) {
        const std::string candidate = NormalizeVfsKey(row.candidate);
        if (!candidate.empty()) {
            indexed_candidates.insert(candidate);
        }
    }

    std::vector<std::string> missing;
    std::unordered_set<std::string> seen_missing;
    seen_missing.reserve(file_candidates.size());
    for (const std::string& candidate : file_candidates) {
        const std::string normalized = NormalizeVfsKey(candidate);
        if (!IsAuditableNormalizedVfsFileCandidate(normalized)) {
            continue;
        }
        if (indexed_candidates.find(normalized) != indexed_candidates.end()) {
            continue;
        }
        if (seen_missing.insert(normalized).second) {
            missing.push_back(normalized);
        }
    }
    std::sort(missing.begin(), missing.end());
    return missing;
}

bool WriteUnindexedVfsCandidateAudit(
    const std::filesystem::path& runtime_index_root,
    const std::vector<std::string>& missing_candidates,
    std::string* error) {
    const std::filesystem::path audit_path =
        runtime_index_root / "unindexed_candidates.tsv";
    if (missing_candidates.empty()) {
        std::error_code remove_error;
        std::filesystem::remove(audit_path, remove_error);
        return true;
    }

    try {
        std::filesystem::create_directories(runtime_index_root);
    } catch (const std::exception& exception) {
        if (error != nullptr) {
            *error = exception.what();
        }
        return false;
    }

    std::ofstream audit(audit_path, std::ios::trunc);
    if (!audit) {
        if (error != nullptr) {
            *error = "failed to open VFS unindexed candidate audit for writing: " +
                     audit_path.string();
        }
        return false;
    }

    audit << "# refii_vfs_unindexed_candidates_v1\n";
    audit << "# candidate\n";
    for (const std::string& candidate : missing_candidates) {
        audit << candidate << '\n';
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

bool IsPathInsideIndexedBankRoot(const VfsDatabaseBuildState& state,
                                 std::string_view normalized_path) {
    if (normalized_path.empty() || state.extracted_bank_roots.empty()) {
        return false;
    }

    std::string_view cursor(normalized_path);
    for (;;) {
        if (state.extracted_bank_roots.find(std::string(cursor)) !=
            state.extracted_bank_roots.end()) {
            return true;
        }
        const size_t separator = cursor.find_last_of('/');
        if (separator == std::string_view::npos) {
            break;
        }
        cursor = cursor.substr(0, separator);
    }
    return false;
}

void AddVfsSyntheticBankRowsForManifest(VfsDatabaseBuildState* state,
                                        std::string_view logical_root,
                                        std::string_view manifest_root) {
    if (state == nullptr) {
        return;
    }

    const std::string root = NormalizeVfsKey(logical_root);
    const std::string manifest = NormalizeVfsKey(manifest_root);
    if (root.empty() || manifest.empty() || IsLuaSourceRoot(root) ||
        StartsWith(root, ".baseline/") || StartsWith(root, ".cache/") ||
        StartsWith(root, "_disasm/") || StartsWith(root, "refii_vfs/")) {
        return;
    }

    const std::string synthetic_bank_candidate = root + ".bnk";
    if (!ShouldIndexVfsCandidate(synthetic_bank_candidate)) {
        return;
    }

    AddVfsDatabasePathAliases(
        &state->rows, &state->row_keys, synthetic_bank_candidate, synthetic_bank_candidate);
    AddVfsBankRecord(&state->bank_records,
                     &state->bank_record_keys,
                     synthetic_bank_candidate,
                     synthetic_bank_candidate,
                     manifest);
    if (root == "globals/1024mip0_textures") {
        AddVfsBankRecord(&state->bank_records,
                         &state->bank_record_keys,
                         "globals/1024mip0_texture_headers.bnk",
                         synthetic_bank_candidate,
                         manifest);
    }

    std::string_view language_root;
    std::string_view language_tail;
    std::string_view asset_tail;
    if (!SplitLocalizedLanguagePath(root, &language_root, &language_tail, &asset_tail)) {
        return;
    }

    const std::string alias =
        std::string(language_root) + "/" + std::string(asset_tail) + ".bnk";
    auto add_bank_alias_with_separator_variants = [&](std::string_view bank_alias) {
        AddVfsDatabaseAliasWithSeparatorVariants(
            &state->rows, &state->row_keys, bank_alias, synthetic_bank_candidate);
        std::string backslash_alias(bank_alias);
        std::replace(backslash_alias.begin(), backslash_alias.end(), '/', '\\');
        AddVfsBankRecord(&state->bank_records,
                         &state->bank_record_keys,
                         bank_alias,
                         synthetic_bank_candidate,
                         manifest);
        AddVfsBankRecord(&state->bank_records,
                         &state->bank_record_keys,
                         backslash_alias,
                         synthetic_bank_candidate,
                         manifest);
    };

    add_bank_alias_with_separator_variants(alias);
    if (language_tail == "en-uk/speech/speech") {
        if (language_root == "language") {
            add_bank_alias_with_separator_variants("speech/speech.bnk");
        }
        add_bank_alias_with_separator_variants(
            std::string(language_root) + "/speech.bnk");
    } else if (language_tail == "en-uk/lipsync/lipsync") {
        if (language_root == "language") {
            add_bank_alias_with_separator_variants("lipsync/lipsync.bnk");
        }
        add_bank_alias_with_separator_variants(
            std::string(language_root) + "/lipsync.bnk");
    }
}

void AddVfsSyntheticBankRows(VfsDatabaseBuildState* state,
                             std::string_view normalized_root) {
    AddVfsSyntheticBankRowsForManifest(state, normalized_root, normalized_root);
}

std::string LogicalBankRootFromManifestRoot(std::string_view manifest_root) {
    std::string normalized = NormalizeVfsKey(manifest_root);
    constexpr std::string_view kBankMetadataPrefix = "refii_vfs/banks/";
    if (StartsWith(normalized, kBankMetadataPrefix)) {
        normalized.erase(0, kBankMetadataPrefix.size());
    }
    return normalized;
}

void AddVfsExtractedCandidate(VfsDatabaseBuildState* state,
                              const std::filesystem::path& candidate_path,
                              std::string_view entry_path,
                              bool manifest_covered) {
    if (state == nullptr) {
        return;
    }

    const std::string candidate = NormalizeVfsKey(candidate_path.generic_string());
    const std::string normalized_entry = NormalizeVfsKey(entry_path);
    if (manifest_covered && EndsWith(normalized_entry, ".lua")) {
        AddVfsLuaRecord(&state->lua_records,
                        &state->lua_record_keys,
                        normalized_entry,
                        candidate,
                        "carrier");
        if (StartsWith(normalized_entry, "data/")) {
            AddVfsLuaRecord(&state->lua_records,
                            &state->lua_record_keys,
                            std::string_view(normalized_entry).substr(5),
                            candidate,
                            "carrier");
        } else {
            AddVfsLuaRecord(&state->lua_records,
                            &state->lua_record_keys,
                            "data/" + normalized_entry,
                            candidate,
                                    "carrier");
        }
    }

    if (!ShouldIndexVfsCandidate(candidate)) {
        return;
    }

    if (IsLuaSourceGameFaceCarrier(candidate)) {
        constexpr std::string_view kLuaSourcePrefix = "luasource/";
        constexpr std::string_view kGuiScriptsPrefix = "luasource/guiscripts/";
        if (StartsWith(candidate, kLuaSourcePrefix)) {
            AddVfsDatabasePathAliases(
                &state->rows,
                &state->row_keys,
                candidate,
                std::string_view(candidate).substr(kLuaSourcePrefix.size()));
        }
        if (StartsWith(candidate, kGuiScriptsPrefix)) {
            AddVfsDatabasePathAliases(
                &state->rows,
                &state->row_keys,
                candidate,
                std::string_view(candidate).substr(kGuiScriptsPrefix.size()));
        }
    }

    if (manifest_covered) {
        state->manifest_covered_candidates.insert(candidate);
    }
    AddVfsDatabasePathAliases(&state->rows, &state->row_keys, candidate, entry_path);
    AddVfsBasenameCandidate(&state->basename_candidates, candidate);
    if (!manifest_covered && !IsLuaSourceRoot(candidate) && EndsWith(candidate, ".lua")) {
        AddVfsLuaRecord(&state->lua_records,
                        &state->lua_record_keys,
                        candidate,
                        candidate,
                        "override");
    }

    const std::string filename =
        NormalizeVfsKey(std::filesystem::path(candidate).filename().generic_string());
    if (filename == "filehash.csv") {
        state->filehash_paths.push_back(state->unpacked_root / candidate_path);
    }
    if (IsDirectLooseVfsFile(candidate)) {
        AddVfsDatabasePathAliases(&state->rows, &state->row_keys, candidate, candidate);
        // A .gdb.csv / .babel.csv is the extracted form of its binary database:
        // index the database name as a virtual candidate so hash lookups still
        // route to it. The loose resolver compiles the CSV when it is opened.
        const std::string normalized_candidate = NormalizeVfsKey(candidate);
        if (EndsWith(normalized_candidate, ".gdb.csv") ||
            EndsWith(normalized_candidate, ".babel.csv")) {
            const std::string_view database_candidate =
                std::string_view(candidate).substr(0, candidate.size() - 4);
            AddVfsDatabasePathAliases(&state->rows,
                                      &state->row_keys,
                                      database_candidate,
                                      database_candidate);
            AddVfsBasenameCandidate(&state->basename_candidates,
                                    database_candidate);
        }
    }
    ++state->indexed_files;
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

void CollectSupplementalVfsFile(VfsDatabaseBuildState* state,
                                const std::filesystem::path& absolute_path,
                                const std::filesystem::path& relative_path) {
    if (state == nullptr) {
        return;
    }

    const std::string relative_key = relative_path.generic_string();
    const std::string normalized_key = NormalizeVfsKey(relative_key);
    if (normalized_key.empty()) {
        return;
    }

    if (IsLuaSourceRoot(normalized_key)) {
        if (EndsWith(normalized_key, ".lua")) {
            const std::string_view prefix =
                StartsWith(normalized_key, "luasource/")
                    ? std::string_view("luasource/")
                    : std::string_view("luasource");
            AddVfsLuaRecord(&state->lua_records,
                            &state->lua_record_keys,
                            normalized_key,
                            normalized_key,
                            "carrier");
            AddVfsLuaRecord(&state->lua_records,
                            &state->lua_record_keys,
                            std::string_view(normalized_key).substr(prefix.size()),
                            normalized_key,
                            "carrier");
        } else if (IsLuaSourceGameFaceCarrier(normalized_key)) {
            constexpr std::string_view kLuaSourcePrefix = "luasource/";
            constexpr std::string_view kGuiScriptsPrefix = "luasource/guiscripts/";
            AddVfsDatabasePathAliases(&state->rows,
                                      &state->row_keys,
                                      normalized_key,
                                      std::string_view(normalized_key)
                                          .substr(kLuaSourcePrefix.size()));
            AddVfsDatabasePathAliases(&state->rows,
                                      &state->row_keys,
                                      normalized_key,
                                      std::string_view(normalized_key)
                                          .substr(kGuiScriptsPrefix.size()));
            AddVfsBasenameCandidate(&state->basename_candidates, normalized_key);
            ++state->indexed_files;
        }
        return;
    }

    const std::string filename = NormalizeVfsKey(absolute_path.filename().generic_string());
    if (filename == "_manifest.tsv") {
        state->manifest_paths.push_back(absolute_path);
        return;
    }
    if (filename == "filehash.csv") {
        state->filehash_paths.push_back(absolute_path);
    }

    if (StartsWith(normalized_key, ".baseline/") ||
        StartsWith(normalized_key, ".cache/") ||
        StartsWith(normalized_key, "refii_vfs/") ||
        StartsWith(normalized_key, "_disasm/")) {
        return;
    }

    if (EndsWith(normalized_key, ".lua")) {
        AddVfsLuaRecord(&state->lua_records,
                        &state->lua_record_keys,
                        normalized_key,
                        normalized_key,
                        "override");
    }

    state->file_candidates.push_back(normalized_key);
    AddVfsBasenameCandidate(&state->basename_candidates, normalized_key);
    ++state->indexed_files;
}

bool CollectSupplementalVfsFiles(VfsDatabaseBuildState* state, std::string* error) {
    if (state == nullptr) {
        return true;
    }

    std::error_code iterator_error;
    std::error_code status_error;
    constexpr auto iterator_options =
        std::filesystem::directory_options::skip_permission_denied;
    for (std::filesystem::recursive_directory_iterator it(
             state->unpacked_root, iterator_options, iterator_error),
         end;
         it != end; it.increment(iterator_error)) {
        if (iterator_error) {
            iterator_error.clear();
            continue;
        }

        const std::filesystem::path relative_path =
            it->path().lexically_relative(state->unpacked_root).lexically_normal();
        const std::string normalized_key = NormalizeVfsKey(relative_path.generic_string());
        if (it->is_directory(status_error)) {
            if (IsPathInsideIndexedBankRoot(*state, normalized_key) ||
                IsGeneratedVfsScanDirectory(normalized_key)) {
                it.disable_recursion_pending();
            }
            status_error.clear();
            continue;
        }
        if (!it->is_regular_file(status_error)) {
            status_error.clear();
            continue;
        }
        if (IsPathInsideIndexedBankRoot(*state, normalized_key)) {
            continue;
        }

        CollectSupplementalVfsFile(state, it->path(), relative_path);
    }

    for (const std::filesystem::path& manifest_path : state->manifest_paths) {
        std::ifstream manifest(manifest_path);
        if (!manifest) {
            continue;
        }

        const std::filesystem::path manifest_root =
            manifest_path.parent_path().lexically_relative(state->unpacked_root);
        const std::string manifest_root_key = manifest_root.generic_string();
        const std::string normalized_manifest_root = NormalizeVfsKey(manifest_root_key);
        const std::string normalized_root =
            LogicalBankRootFromManifestRoot(normalized_manifest_root);
        if (normalized_root.empty() || IsLuaSourceRoot(normalized_root) ||
            StartsWith(normalized_root, ".baseline/") ||
            StartsWith(normalized_root, ".cache/") ||
            StartsWith(normalized_root, "_disasm/")) {
            continue;
        }

        AddVfsSyntheticBankRowsForManifest(
            state, normalized_root, normalized_manifest_root);

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
            std::filesystem::path candidate_path;
            if (columns.size() > 7 && !columns[7].empty()) {
                candidate_path = NormalizeRelativePath(std::string(columns[7]));
            } else {
                candidate_path = NormalizeRelativePath(entry_path);
                if (!StartsWith(normalized_manifest_root, "refii_vfs/banks/")) {
                    candidate_path = manifest_root / candidate_path;
                }
            }
            AddVfsExtractedCandidate(state, candidate_path, entry_path, true);
        }
        ++state->indexed_manifests;
    }

    for (const std::string& candidate : state->file_candidates) {
        if (state->manifest_covered_candidates.find(candidate) !=
                state->manifest_covered_candidates.end() &&
            !IsDirectLooseVfsFile(candidate)) {
            continue;
        }
        AddVfsDatabasePathAliases(&state->rows, &state->row_keys, candidate, candidate);
        // A .gdb.csv / .babel.csv is the extracted form of its binary database:
        // index the database name as a virtual candidate so hash lookups still
        // route to it. The loose resolver compiles the CSV when it is opened.
        const std::string normalized_candidate = NormalizeVfsKey(candidate);
        if (EndsWith(normalized_candidate, ".gdb.csv") ||
            EndsWith(normalized_candidate, ".babel.csv")) {
            const std::string_view database_candidate =
                std::string_view(candidate).substr(0, candidate.size() - 4);
            AddVfsDatabasePathAliases(&state->rows,
                                      &state->row_keys,
                                      database_candidate,
                                      database_candidate);
        }
    }

    return true;
}

bool CollectPublishedGlobalsVfsFiles(VfsDatabaseBuildState* state,
                                     std::string* error) {
    (void)error;
    if (state == nullptr) {
        return true;
    }

    const std::filesystem::path globals_root = state->unpacked_root / "globals";
    std::error_code status_error;
    if (!std::filesystem::is_directory(globals_root, status_error)) {
        return true;
    }

    std::error_code iterator_error;
    constexpr auto iterator_options =
        std::filesystem::directory_options::skip_permission_denied;
    for (std::filesystem::recursive_directory_iterator it(
             globals_root, iterator_options, iterator_error),
         end;
         it != end; it.increment(iterator_error)) {
        if (iterator_error) {
            iterator_error.clear();
            continue;
        }

        std::error_code file_status_error;
        if (!it->is_regular_file(file_status_error)) {
            file_status_error.clear();
            continue;
        }

        const std::filesystem::path relative_path =
            it->path().lexically_relative(state->unpacked_root).lexically_normal();
        CollectSupplementalVfsFile(state, it->path(), relative_path);
    }
    return true;
}

void ApplyFilehashTablesToVfsState(VfsDatabaseBuildState* state) {
    if (state == nullptr) {
        return;
    }

    for (const std::filesystem::path& filehash_path : state->filehash_paths) {
        std::ifstream csv(filehash_path);
        if (!csv) {
            continue;
        }

        const std::filesystem::path csv_relative =
            filehash_path.lexically_relative(state->unpacked_root);
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
            const auto match = state->basename_candidates.find(basename);
            if (match == state->basename_candidates.end()) {
                continue;
            }

            bool added_scoped_candidate = false;
            for (const std::string& candidate : match->second) {
                if (!csv_prefix.empty() && !StartsWith(candidate, csv_prefix)) {
                    continue;
                }
                AddVfsDatabaseExplicitHash(&state->rows,
                                           &state->row_keys,
                                           static_cast<std::uint32_t>(parsed_hash),
                                           basename,
                                           candidate);
                added_scoped_candidate = true;
            }
            if (added_scoped_candidate) {
                continue;
            }
            for (const std::string& candidate : match->second) {
                AddVfsDatabaseExplicitHash(&state->rows,
                                           &state->row_keys,
                                           static_cast<std::uint32_t>(parsed_hash),
                                           basename,
                                           candidate);
            }
        }
        ++state->indexed_filehash_tables;
    }
}

bool WriteVfsDatabaseState(VfsDatabaseBuildState* state,
                           VfsDatabaseBuildSummary* summary,
                           std::string* error) {
    if (state == nullptr) {
        return false;
    }

    ApplyFilehashTablesToVfsState(state);

    VfsDatabaseBuildSummary local_summary;
    VfsDatabaseBuildSummary* const active_summary =
        summary != nullptr ? summary : &local_summary;
    active_summary->database_path = state->database_path;
    active_summary->runtime_index_root = state->runtime_index_root;
    active_summary->entries = state->rows.size();
    active_summary->indexed_files = state->indexed_files;
    active_summary->indexed_manifests = state->indexed_manifests;
    active_summary->indexed_filehash_tables = state->indexed_filehash_tables;
    active_summary->unindexed_files = 0;

    if (state->write_legacy_database) {
        try {
            std::filesystem::create_directories(state->database_path.parent_path());
        } catch (const std::exception& exception) {
            if (error != nullptr) {
                *error = exception.what();
            }
            return false;
        }

        std::ofstream database(state->database_path, std::ios::trunc);
        if (!database) {
            if (error != nullptr) {
                *error = "failed to open VFS database for writing: " +
                         state->database_path.string();
            }
            return false;
        }

        database << "# refii_vfs_database_v2\n";
        database << "# hash_hex\talias\tcandidate\n";
        for (const VfsDatabaseRow& row : state->rows) {
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
        std::filesystem::remove(state->unpacked_root / "_refii_vfs.tsv", remove_error);
    }

    return WriteRuntimeVfsIndex(state->runtime_index_root,
                                state->rows,
                                state->bank_records,
                                state->lua_records,
                                active_summary,
                                error);
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

bool EnsureOutputParentDirectory(const std::filesystem::path& output_path,
                                 ExtractionContext* context,
                                 std::string* error) {
    const std::filesystem::path parent_path = output_path.parent_path();
    if (parent_path.empty()) {
        return true;
    }

    const std::string parent_key = parent_path.generic_string();
    if (context != nullptr) {
        {
            std::lock_guard<std::mutex> lock(context->created_directories_mutex);
            if (context->created_directories.find(parent_key) !=
                context->created_directories.end()) {
                return true;
            }
        }
    }

    try {
        std::filesystem::create_directories(parent_path);
    } catch (const std::exception& exception) {
        if (error != nullptr) {
            *error = exception.what();
        }
        return false;
    }

    if (context != nullptr) {
        std::lock_guard<std::mutex> lock(context->created_directories_mutex);
        context->created_directories.insert(parent_key);
    }
    return true;
}

// The extracted representation of a .gdb or .babel database is an editable
// .csv twin; like every other extracted format, the packed original is not
// kept. The VFS runtime compiles the CSV back to binary on demand. Bank-scoped
// payload copies under refii_vfs/ stay binary because manifest routing serves
// their raw bytes. An existing CSV is treated as the source of truth: the
// freshly written binary is dropped without overwriting it, so user edits
// survive incremental re-extraction.
bool MaybeReplaceDatabaseWithCsv(const std::filesystem::path& output_path,
                                 bool convert_gdb,
                                 bool convert_babel) {
    std::string extension = output_path.extension().string();
    std::transform(extension.begin(), extension.end(), extension.begin(),
                   [](unsigned char ch) {
                       return static_cast<char>(std::tolower(ch));
                   });
    const bool is_gdb = convert_gdb && extension == ".gdb";
    const bool is_babel = convert_babel && extension == ".babel";
    if (!is_gdb && !is_babel) {
        return false;
    }
    for (const std::filesystem::path& component : output_path) {
        const std::string name = component.string();
        if (name == "refii_vfs" || name == ".cache") {
            return false;
        }
    }
    std::filesystem::path csv_path = output_path;
    csv_path += ".csv";
    std::error_code status_error;
    if (!std::filesystem::exists(csv_path, status_error)) {
        std::string error;
        const bool converted =
            is_gdb ? refii::vfs::gdb::ConvertGdbFileToCsvFile(
                         output_path, csv_path, &error)
                   : refii::vfs::babel::ConvertBabelFileToCsvFile(
                         output_path, csv_path, &error);
        if (!converted) {
            // Unknown variants must not fail extraction; keep the binary and
            // drop the partial CSV so the runtime never sees it.
            std::filesystem::remove(csv_path, status_error);
            return false;
        }
    }
    std::filesystem::remove(output_path, status_error);
    return true;
}

bool CopyFileRange(std::ifstream& source,
                   std::uint64_t offset,
                   std::uint64_t size,
                   const std::filesystem::path& output_path,
                   ExtractionContext* context,
                   std::string* error) {
    if (!EnsureOutputParentDirectory(output_path, context, error)) {
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

    output.close();
    if (context != nullptr) {
        MaybeReplaceDatabaseWithCsv(output_path, context->convert_gdb_databases,
                                    context->convert_babel_databases);
    }
    return true;
}

bool WriteBytes(const std::filesystem::path& output_path,
                const std::vector<std::uint8_t>& bytes,
                ExtractionContext* context,
                std::string* error) {
    if (!EnsureOutputParentDirectory(output_path, context, error)) {
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

    output.close();
    if (context != nullptr) {
        MaybeReplaceDatabaseWithCsv(output_path, context->convert_gdb_databases,
                                    context->convert_babel_databases);
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
                            ExtractionContext* context,
                            std::string* error) {
    std::vector<std::uint8_t> output_bytes;
    if (!DecodeCompressedEntryBytes(file, data_offset, entry, &output_bytes, error)) {
        return false;
    }
    return WriteBytes(output_path, output_bytes, context, error);
}

std::filesystem::path ConflictReportPath(const std::filesystem::path& output_root) {
    return output_root / "refii_vfs" / "extraction_conflicts.tsv";
}

std::filesystem::path ConflictStashRoot(const std::filesystem::path& output_root) {
    return output_root / "refii_vfs" / "conflicts";
}

std::size_t ResolvePayloadWorkerCount(std::size_t requested_worker_count,
                                      std::size_t entry_count) {
    if (entry_count < 16) {
        return 1;
    }
    if (requested_worker_count != 0) {
        return std::max<std::size_t>(1, std::min(requested_worker_count, entry_count));
    }

    const unsigned int hardware_threads = std::thread::hardware_concurrency();
    const std::size_t automatic_workers =
        hardware_threads == 0 ? 4 : static_cast<std::size_t>(hardware_threads);
    return std::max<std::size_t>(
        1, std::min<std::size_t>({entry_count, automatic_workers, 8}));
}

bool ExtractPlannedEntryPayload(std::ifstream& file,
                                const std::filesystem::path& bank_path,
                                const BankTable& table,
                                const PlannedBankEntry& planned_entry,
                                ExtractionContext* context,
                                std::string* error) {
    if (context == nullptr || !context->extract_payloads) {
        return true;
    }
    if (planned_entry.entry_index >= table.entries.size()) {
        if (error != nullptr) {
            *error = "invalid planned bank entry index";
        }
        return false;
    }

    const BankEntry& entry = table.entries[planned_entry.entry_index];
    const std::filesystem::path& host_relative_path =
        planned_entry.host_relative_path.empty()
            ? planned_entry.relative_path
            : planned_entry.host_relative_path;

    const std::uint64_t data_offset = EntryDataOffset(table, entry);
    const std::uint64_t stored_size = entry.stored_size;
    const std::filesystem::path output_path =
        (context->output_root / host_relative_path).lexically_normal();
    if (entry.is_compressed) {
        if (!ExtractCompressedEntry(file, data_offset, entry, output_path, context, error)) {
            if (error != nullptr && !error->empty()) {
                *error = "failed to decompress " + entry.relative_path + " from " +
                         bank_path.string() + ": " + *error;
            }
            return false;
        }
    } else if (!CopyFileRange(file, data_offset, stored_size, output_path, context, error)) {
        if (error != nullptr && !error->empty()) {
            *error = "failed to write extracted file " + output_path.string() + ": " +
                     *error;
        }
        return false;
    }

    return true;
}

bool ExtractRegularPlannedEntries(const std::filesystem::path& bank_path,
                                  const BankTable& table,
                                  const std::vector<PlannedBankEntry>& planned_entries,
                                  const std::vector<std::size_t>& planned_entry_indices,
                                  ExtractionContext* context,
                                  std::size_t* extracted_files,
                                  std::string* error) {
    if (extracted_files != nullptr) {
        *extracted_files = 0;
    }
    if (planned_entry_indices.empty() || context == nullptr || !context->extract_payloads) {
        return true;
    }

    const std::size_t worker_count =
        ResolvePayloadWorkerCount(context->payload_worker_count,
                                  planned_entry_indices.size());
    if (worker_count <= 1) {
        std::ifstream file(bank_path, std::ios::binary);
        if (!file) {
            if (error != nullptr) {
                *error = "failed to open " + bank_path.string();
            }
            return false;
        }
        for (const std::size_t planned_index : planned_entry_indices) {
            if (!ExtractPlannedEntryPayload(file,
                                            bank_path,
                                            table,
                                            planned_entries[planned_index],
                                            context,
                                            error)) {
                return false;
            }
            if (extracted_files != nullptr) {
                ++*extracted_files;
            }
        }
        return true;
    }

    std::atomic<std::size_t> next_index{0};
    std::atomic<std::size_t> completed_count{0};
    std::atomic<bool> failed{false};
    std::mutex error_mutex;
    std::string first_error;

    auto worker = [&]() {
        std::ifstream file(bank_path, std::ios::binary);
        if (!file) {
            std::lock_guard<std::mutex> lock(error_mutex);
            if (first_error.empty()) {
                first_error = "failed to open " + bank_path.string();
            }
            failed = true;
            return;
        }

        for (;;) {
            const std::size_t cursor = next_index.fetch_add(1);
            if (cursor >= planned_entry_indices.size() || failed.load()) {
                break;
            }

            std::string entry_error;
            const std::size_t planned_index = planned_entry_indices[cursor];
            if (!ExtractPlannedEntryPayload(file,
                                            bank_path,
                                            table,
                                            planned_entries[planned_index],
                                            context,
                                            &entry_error)) {
                std::lock_guard<std::mutex> lock(error_mutex);
                if (first_error.empty()) {
                    first_error = std::move(entry_error);
                }
                failed = true;
                break;
            }
            ++completed_count;
        }
    };

    std::vector<std::thread> workers;
    workers.reserve(worker_count);
    for (std::size_t worker_index = 0; worker_index < worker_count; ++worker_index) {
        workers.emplace_back(worker);
    }
    for (std::thread& thread : workers) {
        thread.join();
    }

    if (failed.load()) {
        if (error != nullptr) {
            *error = first_error.empty() ? "failed to extract bank payloads" : first_error;
        }
        return false;
    }

    if (extracted_files != nullptr) {
        *extracted_files = completed_count.load();
    }
    return true;
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
    if (context != nullptr) {
        ReserveVfsDatabaseStateForEntries(context->vfs_database, table.entries.size());
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

        if (context != nullptr && context->loose_path_owners != nullptr &&
            !planned_entry.skip_duplicate &&
            planned_entry.host_relative_path == planned_entry.relative_path) {
            const auto owner_it = context->loose_path_owners->find(relative_key);
            bool scope_entry = false;
            if (owner_it != context->loose_path_owners->end() &&
                context->bank_ordinal != owner_it->second.owner_ordinal) {
                if (entry.size != owner_it->second.size) {
                    scope_entry = true;
                } else if (owner_it->second.multi_bank &&
                           owner_it->second.fingerprint_valid) {
                    // Same size as the owner: compare content so world
                    // streaming variants that differ only in bytes are still
                    // served per-bank.
                    std::vector<std::uint8_t> entry_bytes;
                    std::string fingerprint_error;
                    if (ReadEntryOutputBytes(file, table, entry, &entry_bytes,
                                             &fingerprint_error)) {
                        scope_entry = Fnv1aFingerprint(entry_bytes) !=
                                      owner_it->second.fingerprint;
                    }
                }
            }
            if (scope_entry) {
                const std::filesystem::path bank_relative_root =
                    bank_root.lexically_relative(context->output_root)
                        .lexically_normal();
                planned_entry.host_relative_path =
                    bank_relative_root / "entries" /
                    (std::to_string(entry_index) + ".bin");
                planned_entry.bank_scoped = true;
            }
        }

        planned_entries.push_back(std::move(planned_entry));
    }

    if (!WriteManifest(bank_root, table, planned_entries, error)) {
        return false;
    }
    if (context != nullptr && context->vfs_database != nullptr) {
        const std::filesystem::path manifest_root_path =
            bank_root.lexically_relative(context->vfs_database->unpacked_root).lexically_normal();
        const std::string manifest_root = NormalizeVfsKey(manifest_root_path.generic_string());
        if (!manifest_root.empty()) {
            context->vfs_database->extracted_bank_roots.insert(manifest_root);
            const std::string logical_bank_root =
                LogicalBankRootFromManifestRoot(manifest_root);
            AddVfsSyntheticBankRowsForManifest(
                context->vfs_database, logical_bank_root, manifest_root);
            for (const PlannedBankEntry& planned_entry : planned_entries) {
                if (planned_entry.skip_duplicate || planned_entry.bank_scoped) {
                    continue;
                }
                const std::filesystem::path candidate_path =
                    planned_entry.host_relative_path.empty()
                        ? planned_entry.relative_path
                        : planned_entry.host_relative_path;
                AddVfsExtractedCandidate(context->vfs_database,
                                         candidate_path,
                                         planned_entry.relative_path.generic_string(),
                                         true);
            }
            ++context->vfs_database->indexed_manifests;
        }
    }

    std::vector<std::size_t> regular_payload_entries;
    std::vector<std::size_t> nested_bank_entries;
    regular_payload_entries.reserve(planned_entries.size());
    for (std::size_t planned_index = 0; planned_index < planned_entries.size();
         ++planned_index) {
        const PlannedBankEntry& planned_entry = planned_entries[planned_index];
        if (planned_entry.skip_duplicate) {
            continue;
        }
        if (planned_entry.entry_index >= table.entries.size()) {
            if (error != nullptr) {
                *error = "invalid planned bank entry index";
            }
            return false;
        }
        if (context != nullptr && context->extract_nested_banks &&
            IsBankPath(planned_entry.relative_path)) {
            nested_bank_entries.push_back(planned_index);
        } else {
            regular_payload_entries.push_back(planned_index);
        }
    }

    std::size_t extracted_regular_files = 0;
    if (!ExtractRegularPlannedEntries(bank_path,
                                      table,
                                      planned_entries,
                                      regular_payload_entries,
                                      context,
                                      &extracted_regular_files,
                                      error)) {
        return false;
    }
    if (context != nullptr && context->summary != nullptr) {
        context->summary->extracted_files += extracted_regular_files;
    }

    if (context != nullptr && context->extract_payloads) {
        for (const std::size_t planned_index : nested_bank_entries) {
            const PlannedBankEntry& planned_entry = planned_entries[planned_index];
            const std::filesystem::path& host_relative_path =
                planned_entry.host_relative_path.empty()
                    ? planned_entry.relative_path
                    : planned_entry.host_relative_path;
            const std::filesystem::path output_path =
                (context->output_root / host_relative_path).lexically_normal();

            if (!ExtractPlannedEntryPayload(
                    file, bank_path, table, planned_entry, context, error)) {
                return false;
            }

            const std::filesystem::path nested_output_root =
                GetBankOutputRoot(context->output_root, planned_entry.relative_path);
            const std::filesystem::path legacy_nested_output_root =
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
            if (legacy_nested_output_root != nested_output_root) {
                remove_error.clear();
                std::filesystem::remove_all(legacy_nested_output_root, remove_error);
                if (remove_error) {
                    if (error != nullptr) {
                        *error = "failed to clean legacy nested bank output " +
                                 legacy_nested_output_root.string() + ": " +
                                 remove_error.message();
                    }
                    return false;
                }
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
    return (output_root / "refii_vfs" / "banks" /
            LowercaseRelativePath(bank_relative_path.parent_path()) /
            LowercaseAscii(bank_relative_path.stem().generic_string()))
        .lexically_normal();
}

namespace {

// Converts .gdb / .babel databases already present in the unpacked tree into
// their extracted .csv representation (extraction converts inline; this covers
// trees extracted before CSV support existed). Runtime metadata under
// refii_vfs/ and the compiled-override cache are skipped. Two-phase so the
// removal of converted binaries cannot disturb the directory walk.
std::size_t ConvertUnpackedDatabasesToCsv(
    const std::filesystem::path& unpacked_root,
    bool convert_gdb,
    bool convert_babel) {
    if (!convert_gdb && !convert_babel) {
        return 0;
    }
    std::vector<std::filesystem::path> databases;
    std::error_code iterator_error;
    constexpr auto options =
        std::filesystem::directory_options::skip_permission_denied;
    for (std::filesystem::recursive_directory_iterator
             it(unpacked_root, options, iterator_error),
         end;
         it != end; it.increment(iterator_error)) {
        if (iterator_error) {
            iterator_error.clear();
            continue;
        }
        const std::string filename = it->path().filename().string();
        if (it->is_directory(iterator_error) &&
            (filename == "refii_vfs" || filename == ".cache")) {
            it.disable_recursion_pending();
            continue;
        }
        std::error_code file_error;
        if (it->is_regular_file(file_error)) {
            databases.push_back(it->path());
        }
    }
    std::size_t converted = 0;
    for (const std::filesystem::path& path : databases) {
        if (MaybeReplaceDatabaseWithCsv(path, convert_gdb, convert_babel)) {
            ++converted;
        }
    }
    return converted;
}

// Babel keys are the same FNV-1 symbols carried by GDB name tables. Extraction
// can encounter the Babel payload before its companion GDB, so inline
// conversion initially has to preserve unknown keys as #xxxxxxxx. Once the
// tree is complete, enrich every Babel CSV from every extracted GDB CSV and
// rewrite only files whose readable links improve.
std::size_t LinkBabelCsvNamesFromGdbCsvs(
    const std::filesystem::path& unpacked_root) {
    std::vector<std::filesystem::path> gdb_csvs;
    std::vector<std::filesystem::path> babel_csvs;
    std::error_code iterator_error;
    constexpr auto options =
        std::filesystem::directory_options::skip_permission_denied;
    for (std::filesystem::recursive_directory_iterator
             it(unpacked_root, options, iterator_error),
         end;
         it != end; it.increment(iterator_error)) {
        if (iterator_error) {
            iterator_error.clear();
            continue;
        }
        const std::string filename = LowercaseAscii(it->path().filename().string());
        if (it->is_directory(iterator_error) &&
            (filename == "refii_vfs" || filename == ".cache")) {
            it.disable_recursion_pending();
            continue;
        }
        std::error_code file_error;
        if (!it->is_regular_file(file_error)) {
            continue;
        }
        if (EndsWith(filename, ".gdb.csv")) {
            gdb_csvs.push_back(it->path());
        } else if (EndsWith(filename, ".babel.csv")) {
            babel_csvs.push_back(it->path());
        }
    }

    std::unordered_map<uint32_t, std::string> linked_names;
    std::unordered_set<uint32_t> ambiguous_names;
    for (const std::filesystem::path& path : gdb_csvs) {
        std::ifstream input(path, std::ios::binary);
        const std::string csv((std::istreambuf_iterator<char>(input)),
                              std::istreambuf_iterator<char>());
        refii::vfs::gdb::GdbDatabase database;
        std::string error;
        if (!input.bad() && refii::vfs::gdb::ParseCsv(csv, &database, &error)) {
            for (const auto& [hash, name] : database.names) {
                if (ambiguous_names.count(hash) != 0) {
                    continue;
                }
                const auto [match, inserted] = linked_names.try_emplace(hash, name);
                if (!inserted && match->second != name) {
                    linked_names.erase(match);
                    ambiguous_names.insert(hash);
                }
            }
        }
    }

    std::size_t linked = 0;
    for (const std::filesystem::path& path : babel_csvs) {
        std::ifstream input(path, std::ios::binary);
        const std::string csv((std::istreambuf_iterator<char>(input)),
                              std::istreambuf_iterator<char>());
        refii::vfs::babel::BabelDatabase database;
        std::string error;
        if (input.bad() ||
            !refii::vfs::babel::ParseCsv(csv, &database, &error)) {
            continue;
        }
        for (const auto& [hash, name] : linked_names) {
            if (refii::vfs::babel::Fnv1(name) == hash) {
                database.AddName(name);
            }
        }
        std::string enriched;
        refii::vfs::babel::SerializeCsv(database, &enriched);
        if (enriched == csv) {
            continue;
        }
        std::ofstream output(path, std::ios::binary | std::ios::trunc);
        if (output.write(enriched.data(),
                         static_cast<std::streamsize>(enriched.size()))) {
            ++linked;
        }
    }
    return linked;
}

}  // namespace

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
    active_summary->database_csv_emitted = ConvertUnpackedDatabasesToCsv(
        unpacked_root, options.convert_gdb_databases,
        options.convert_babel_databases);
    if (options.convert_babel_databases) {
        LinkBabelCsvNamesFromGdbCsvs(unpacked_root);
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

    VfsDatabaseBuildState state;
    state.unpacked_root = unpacked_root;
    state.database_path = active_summary->database_path;
    state.runtime_index_root = active_summary->runtime_index_root;
    state.write_legacy_database = write_legacy_database;

    if (!CollectSupplementalVfsFiles(&state, error)) {
        return false;
    }
    return WriteVfsDatabaseState(&state, active_summary, error);
}

namespace {

// Bank-scoped entries/ files (header/payload split variants) repeat heavily
// across world banks -- ~78% of their bytes are duplicates. Collapse
// byte-identical files into hardlinks after extraction; paths are unchanged
// so the runtime manifest routing is unaffected.
void DedupeBankScopedEntryFiles(const std::filesystem::path& output_root) {
    const std::filesystem::path banks_root = output_root / "refii_vfs" / "banks";
    std::error_code ec;
    if (!std::filesystem::exists(banks_root, ec)) {
        return;
    }
    std::unordered_map<std::string, std::filesystem::path> canonical_by_key;
    for (std::filesystem::recursive_directory_iterator
             it(banks_root, std::filesystem::directory_options::skip_permission_denied, ec),
         end;
         it != end; it.increment(ec)) {
        if (ec) {
            ec.clear();
            continue;
        }
        if (!it->is_regular_file(ec) ||
            it->path().parent_path().filename() != "entries") {
            continue;
        }
        const auto size = std::filesystem::file_size(it->path(), ec);
        if (ec) {
            ec.clear();
            continue;
        }
        std::ifstream file(it->path(), std::ios::binary);
        if (!file) {
            continue;
        }
        // Key on size + content hash (FNV-1a over the bytes).
        std::uint64_t hash = 1469598103934665603ull;
        char buffer[65536];
        while (file.read(buffer, sizeof(buffer)) || file.gcount() > 0) {
            const std::streamsize got = file.gcount();
            for (std::streamsize i = 0; i < got; ++i) {
                hash ^= static_cast<unsigned char>(buffer[i]);
                hash *= 1099511628211ull;
            }
            if (got < static_cast<std::streamsize>(sizeof(buffer))) {
                break;
            }
        }
        const std::string key =
            std::to_string(size) + ":" + std::to_string(hash);
        const auto [canon, inserted] =
            canonical_by_key.try_emplace(key, it->path());
        if (inserted) {
            continue;
        }
        const std::filesystem::path tmp = it->path().string() + ".lnk";
        std::filesystem::remove(tmp, ec);
        ec.clear();
        std::filesystem::create_hard_link(canon->second, tmp, ec);
        if (ec) {
            ec.clear();
            continue;  // filesystem without hardlinks: keep the copy
        }
        std::filesystem::rename(tmp, it->path(), ec);
        if (ec) {
            ec.clear();
            std::filesystem::remove(tmp, ec);
        }
    }
}

}  // namespace

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

    VfsDatabaseBuildState database_state;
    if (options.build_vfs_database) {
        database_state.unpacked_root = active_summary->output_root;
        database_state.runtime_index_root = active_summary->output_root / "refii_vfs";
        database_state.write_legacy_database = false;
    }

    const std::size_t total_banks = options.bank_relative_paths.size();

    // Pre-pass sweep A: parse every bank table (headers only) and record, per
    // relative path, the LAST bank in extraction order to carry that name --
    // the historical loose-file owner under the old overwrite behavior.
    // Sweep B then fingerprints the owner's bytes for names carried by more
    // than one bank, so planning can route same-size-but-different-content
    // entries (world streaming variants) to bank-scoped files as well.
    std::unordered_map<std::string, LoosePathOwner> loose_path_owners;
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
            if (case_insensitive_bank_path.empty() ||
                !std::filesystem::exists(case_insensitive_bank_path)) {
                continue;  // reported properly by the extraction pass below
            }
            bank_path = case_insensitive_bank_path;
        }
        std::ifstream file(bank_path, std::ios::binary);
        if (!file) {
            continue;
        }
        file.seekg(0, std::ios::end);
        const std::streamoff stream_size = file.tellg();
        file.seekg(0, std::ios::beg);
        if (stream_size <= 0) {
            continue;
        }
        BankTable table;
        std::string prepass_error;
        if (!ParseBankTable(bank_path, file,
                            static_cast<std::uint64_t>(stream_size), &table,
                            &prepass_error)) {
            continue;
        }
        for (const BankEntry& entry : table.entries) {
            std::filesystem::path relative_path;
            std::string normalize_error;
            if (!NormalizeLowercaseRelativePath(entry.relative_path,
                                                &relative_path,
                                                &normalize_error)) {
                continue;
            }
            // Later banks overwrite: last writer owns the loose path.
            auto [owner_it, first_sighting] = loose_path_owners.try_emplace(
                relative_path.generic_string());
            if (!first_sighting) {
                owner_it->second.multi_bank = true;
            }
            owner_it->second.owner_ordinal = index;
            owner_it->second.size = entry.size;
            owner_it->second.fingerprint_valid = false;
        }
    }

    // Pre-pass sweep B: fingerprint the owner's output bytes for every name
    // carried by more than one bank, enabling content comparison for
    // same-size cross-bank entries during planning.
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
            if (case_insensitive_bank_path.empty() ||
                !std::filesystem::exists(case_insensitive_bank_path)) {
                continue;
            }
            bank_path = case_insensitive_bank_path;
        }
        std::ifstream file(bank_path, std::ios::binary);
        if (!file) {
            continue;
        }
        file.seekg(0, std::ios::end);
        const std::streamoff stream_size = file.tellg();
        file.seekg(0, std::ios::beg);
        if (stream_size <= 0) {
            continue;
        }
        BankTable table;
        std::string prepass_error;
        if (!ParseBankTable(bank_path, file,
                            static_cast<std::uint64_t>(stream_size), &table,
                            &prepass_error)) {
            continue;
        }
        for (const BankEntry& entry : table.entries) {
            std::filesystem::path relative_path;
            std::string normalize_error;
            if (!NormalizeLowercaseRelativePath(entry.relative_path,
                                                &relative_path,
                                                &normalize_error)) {
                continue;
            }
            const auto owner_it =
                loose_path_owners.find(relative_path.generic_string());
            if (owner_it == loose_path_owners.end() ||
                !owner_it->second.multi_bank ||
                owner_it->second.owner_ordinal != index ||
                owner_it->second.fingerprint_valid) {
                continue;
            }
            std::vector<std::uint8_t> owner_bytes;
            std::string read_error;
            if (!ReadEntryOutputBytes(file, table, entry, &owner_bytes,
                                      &read_error)) {
                continue;
            }
            owner_it->second.fingerprint = Fnv1aFingerprint(owner_bytes);
            owner_it->second.fingerprint_valid = true;
        }
    }

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
        context.payload_worker_count = options.payload_worker_count;
        context.convert_gdb_databases = options.convert_gdb_databases;
        context.convert_babel_databases = options.convert_babel_databases;
        context.summary = active_summary;
        context.vfs_database = options.build_vfs_database ? &database_state : nullptr;
        context.loose_path_owners = &loose_path_owners;
        context.bank_ordinal = index;
        if (!ExtractOneBank(bank_path, bank_output_root, &context, error)) {
            return false;
        }
    }

    EmitProgress(options.progress_callback, "extracting_banks", total_banks, total_banks);
    DedupeBankScopedEntryFiles(active_summary->output_root);
    // Bank payload writers already converted their own databases; this sweep
    // converts loose databases the installer copied into the output root
    // before extraction, and must run before the globals publish below so the
    // lowercase mirror propagates CSVs.
    active_summary->database_csv_converted = ConvertUnpackedDatabasesToCsv(
        active_summary->output_root, options.convert_gdb_databases,
        options.convert_babel_databases);
    if (options.convert_babel_databases) {
        LinkBabelCsvNamesFromGdbCsvs(active_summary->output_root);
    }
    if (options.build_vfs_database) {
        EmitProgress(options.progress_callback,
                     "building_vfs_database",
                     total_banks,
                     total_banks);
        VfsDatabaseBuildSummary database_summary;
        if (!PublishDirectGlobalsFilesForVfsIndex(active_summary->output_root, error)) {
            return false;
        }
        if (!CollectPublishedGlobalsVfsFiles(&database_state, error)) {
            return false;
        }
        if (!WriteVfsDatabaseState(&database_state, &database_summary, error)) {
            return false;
        }
        active_summary->vfs_database_entries = database_summary.entries;
        active_summary->vfs_unindexed_files = database_summary.unindexed_files;
    }
    return true;
}

}  // namespace refii::bnk
