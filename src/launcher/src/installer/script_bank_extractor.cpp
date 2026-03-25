#include "installer/script_bank_extractor.h"

#include <zlib.h>

#include <algorithm>
#include <array>
#include <cstdint>
#include <filesystem>
#include <fstream>
#include <iomanip>
#include <limits>
#include <ostream>
#include <sstream>
#include <string>
#include <string_view>
#include <system_error>
#include <vector>

namespace Installer::ScriptBankExtractor {
namespace {

struct ScriptBankEntry {
    std::string relativePath;
    uint32_t offset = 0;
    uint32_t size = 0;
    uint32_t storedSize = 0;
    bool isCompressed = false;
    std::vector<uint32_t> extraWords;
};

struct ScriptBankTable {
    uint32_t baseOffset = 0;
    std::vector<ScriptBankEntry> entries;
};

constexpr std::array<std::string_view, 3> kScriptBankNames{
    "gamescripts.bnk",
    "gamescripts_r.bnk",
    "guiscripts.bnk",
};

constexpr std::array<std::string_view, 2> kLooseScriptDirectoryNames{
    "scripts",
    "scripts_r",
};

constexpr std::size_t kCompressedChunkSize = 0x8000;

uint32_t ReadBigEndianU32(const std::vector<uint8_t>& bytes, std::size_t offset) {
    return (static_cast<uint32_t>(bytes[offset]) << 24) |
           (static_cast<uint32_t>(bytes[offset + 1]) << 16) |
           (static_cast<uint32_t>(bytes[offset + 2]) << 8) |
           static_cast<uint32_t>(bytes[offset + 3]);
}

bool ReadFileBytes(
    const std::filesystem::path& path,
    std::vector<uint8_t>* bytes,
    std::string* error) {
    std::ifstream file(path, std::ios::binary);
    if (!file) {
        if (error) {
            *error = "Failed to open " + path.string();
        }
        return false;
    }

    file.seekg(0, std::ios::end);
    const std::streampos size = file.tellg();
    if (size < 0) {
        if (error) {
            *error = "Failed to determine size of " + path.string();
        }
        return false;
    }

    file.seekg(0, std::ios::beg);
    bytes->resize(static_cast<std::size_t>(size));
    if (!bytes->empty()) {
        file.read(reinterpret_cast<char*>(bytes->data()), static_cast<std::streamsize>(bytes->size()));
        if (!file) {
            if (error) {
                *error = "Failed to read " + path.string();
            }
            return false;
        }
    }

    return true;
}

bool InflateNoError(
    const uint8_t* compressedData,
    std::size_t compressedSize,
    std::size_t expectedSize,
    std::vector<uint8_t>* output,
    std::string* error) {
    if (compressedData == nullptr && compressedSize != 0) {
        if (error) {
            *error = "Invalid compressed buffer";
        }
        return false;
    }

    if (compressedSize > static_cast<std::size_t>(std::numeric_limits<uInt>::max()) ||
        expectedSize > static_cast<std::size_t>(std::numeric_limits<uInt>::max())) {
        if (error) {
            *error = "Compressed buffer exceeds zlib limits";
        }
        return false;
    }

    output->assign(expectedSize, 0);

    z_stream stream{};
    stream.next_in = const_cast<Bytef*>(reinterpret_cast<const Bytef*>(compressedData));
    stream.avail_in = static_cast<uInt>(compressedSize);
    stream.next_out = reinterpret_cast<Bytef*>(output->data());
    stream.avail_out = static_cast<uInt>(expectedSize);

    const int initResult = inflateInit(&stream);
    if (initResult != Z_OK) {
        if (error) {
            *error = "inflateInit failed";
        }
        return false;
    }

    int inflateResult = Z_OK;
    std::size_t previousTotalOut = 0;
    while (stream.total_out < expectedSize) {
        inflateResult = inflate(&stream, Z_NO_FLUSH);
        if (inflateResult == Z_STREAM_END) {
            break;
        }
        if (inflateResult != Z_OK && inflateResult != Z_BUF_ERROR) {
            inflateEnd(&stream);
            if (error) {
                *error = "inflate failed with code " + std::to_string(inflateResult);
            }
            return false;
        }

        if (stream.total_out == previousTotalOut &&
            (stream.avail_in == 0 || inflateResult == Z_BUF_ERROR)) {
            break;
        }
        previousTotalOut = stream.total_out;
    }

    inflateEnd(&stream);

    if (stream.total_out != expectedSize) {
        if (error) {
            std::ostringstream message;
            message << "inflate produced " << stream.total_out << " bytes, expected "
                    << expectedSize;
            *error = message.str();
        }
        return false;
    }

    return true;
}

bool ParseScriptBankTable(
    const std::filesystem::path& bankPath,
    ScriptBankTable* table,
    std::vector<uint8_t>* bankBytes,
    std::string* error) {
    if (!ReadFileBytes(bankPath, bankBytes, error)) {
        return false;
    }

    if (bankBytes->size() < 0x11) {
        if (error) {
            *error = bankPath.string() + " is too small to contain a valid bank header";
        }
        return false;
    }

    table->baseOffset = ReadBigEndianU32(*bankBytes, 0);
    const uint32_t headerCompressedSize = ReadBigEndianU32(*bankBytes, 9);
    const uint32_t headerSize = ReadBigEndianU32(*bankBytes, 13);
    const std::size_t headerOffset = 0x11;
    if (headerOffset + headerCompressedSize > bankBytes->size()) {
        if (error) {
            *error = "Compressed header exceeds bank size for " + bankPath.string();
        }
        return false;
    }

    std::vector<uint8_t> headerBytes;
    if (!InflateNoError(bankBytes->data() + headerOffset, headerCompressedSize, headerSize,
                        &headerBytes, error)) {
        if (error && !error->empty()) {
            *error = "Failed to decompress header for " + bankPath.string() + ": " + *error;
        }
        return false;
    }

    if (headerBytes.size() < 4) {
        if (error) {
            *error = "Decompressed header is too small for " + bankPath.string();
        }
        return false;
    }

    const uint32_t fileCount = ReadBigEndianU32(headerBytes, 0);
    std::size_t position = 4;
    table->entries.clear();
    table->entries.reserve(fileCount);

    for (uint32_t i = 0; i < fileCount; ++i) {
        if (position + 4 > headerBytes.size()) {
            if (error) {
                *error = "Unexpected end of header while reading name length in " +
                         bankPath.string();
            }
            return false;
        }

        const uint32_t storedNameLength = ReadBigEndianU32(headerBytes, position);
        position += 4;

        if (storedNameLength == 0) {
            if (error) {
                *error = "Encountered zero-length entry name in " + bankPath.string();
            }
            return false;
        }

        const std::size_t maxNameEnd =
            std::min(headerBytes.size(), position + static_cast<std::size_t>(storedNameLength) + 1);
        const auto nullIt =
            std::find(headerBytes.begin() + static_cast<std::ptrdiff_t>(position),
                      headerBytes.begin() + static_cast<std::ptrdiff_t>(maxNameEnd), 0);
        if (nullIt == headerBytes.begin() + static_cast<std::ptrdiff_t>(maxNameEnd)) {
            if (error) {
                *error = "Failed to locate a null terminator for a bank entry in " +
                         bankPath.string();
            }
            return false;
        }

        const std::size_t nullPosition =
            static_cast<std::size_t>(std::distance(headerBytes.begin(), nullIt));
        ScriptBankEntry entry;
        entry.relativePath.assign(reinterpret_cast<const char*>(headerBytes.data() + position),
                                  nullPosition - position);
        position = nullPosition + 1;

        if (position + 8 > headerBytes.size()) {
            if (error) {
                *error = "Unexpected end of header while reading entry metadata in " +
                         bankPath.string();
            }
            return false;
        }

        entry.offset = ReadBigEndianU32(headerBytes, position);
        position += 4;
        entry.size = ReadBigEndianU32(headerBytes, position);
        position += 4;

        const std::size_t precheckPosition = position;
        if (position + 8 > headerBytes.size()) {
            entry.storedSize = entry.size;
            entry.isCompressed = false;
            table->entries.push_back(std::move(entry));
            continue;
        }

        const uint32_t candidateStoredSize = ReadBigEndianU32(headerBytes, position);
        position += 4;
        const uint32_t extraWordCount = ReadBigEndianU32(headerBytes, position);
        position += 4;

        if (extraWordCount < 100 &&
            position + static_cast<std::size_t>(extraWordCount) * 4 <= headerBytes.size()) {
            entry.storedSize = candidateStoredSize;
            entry.isCompressed = true;
            entry.extraWords.reserve(extraWordCount);
            for (uint32_t extraIndex = 0; extraIndex < extraWordCount; ++extraIndex) {
                entry.extraWords.push_back(ReadBigEndianU32(headerBytes, position));
                position += 4;
            }
        } else {
            entry.storedSize = entry.size;
            entry.isCompressed = false;
            position = precheckPosition;
        }

        table->entries.push_back(std::move(entry));
    }

    return true;
}

std::filesystem::path NormalizeRelativePath(const std::string& rawPath) {
    std::string normalized = rawPath;
    std::replace(normalized.begin(), normalized.end(), '\\', '/');
    return std::filesystem::path(normalized);
}

bool IsSafeRelativePath(const std::filesystem::path& path) {
    if (path.empty() || path.is_absolute()) {
        return false;
    }

    for (const auto& component : path) {
        if (component == "..") {
            return false;
        }
    }

    return true;
}

bool WriteFileBytes(
    const std::filesystem::path& path,
    const std::vector<uint8_t>& bytes,
    std::string* error) {
    try {
        std::filesystem::create_directories(path.parent_path());
    } catch (const std::exception& exception) {
        if (error) {
            *error = exception.what();
        }
        return false;
    }

    std::ofstream file(path, std::ios::binary | std::ios::trunc);
    if (!file) {
        if (error) {
            *error = "Failed to open " + path.string() + " for writing";
        }
        return false;
    }

    if (!bytes.empty()) {
        file.write(reinterpret_cast<const char*>(bytes.data()),
                   static_cast<std::streamsize>(bytes.size()));
        if (!file) {
            if (error) {
                *error = "Failed to write " + path.string();
            }
            return false;
        }
    }

    return true;
}

bool DecompressEntryPayload(
    const uint8_t* compressedData,
    std::size_t storedSize,
    const ScriptBankEntry& entry,
    std::vector<uint8_t>* output,
    std::string* error) {
    if (entry.extraWords.empty()) {
        return InflateNoError(compressedData, storedSize, entry.size, output, error);
    }

    output->clear();
    output->reserve(entry.size);

    std::size_t remainingStoredSize = storedSize;
    std::size_t cursor = 0;
    for (std::size_t segmentIndex = 0; segmentIndex < entry.extraWords.size(); ++segmentIndex) {
        if (remainingStoredSize == 0) {
            if (error) {
                *error = "Compressed entry ran out of stored bytes before all segments were read";
            }
            return false;
        }

        const std::size_t segmentStoredSize = std::min(remainingStoredSize, kCompressedChunkSize);
        std::vector<uint8_t> segmentBytes;
        if (!InflateNoError(compressedData + cursor, segmentStoredSize, entry.extraWords[segmentIndex],
                            &segmentBytes, error)) {
            return false;
        }

        output->insert(output->end(), segmentBytes.begin(), segmentBytes.end());
        cursor += segmentStoredSize;
        remainingStoredSize -= segmentStoredSize;
    }

    if (output->size() != entry.size) {
        if (error) {
            std::ostringstream message;
            message << "Segmented inflate produced " << output->size() << " bytes, expected "
                    << entry.size;
            *error = message.str();
        }
        return false;
    }

    return true;
}

std::string JoinExtraWords(const std::vector<uint32_t>& words) {
    if (words.empty()) {
        return "";
    }

    std::ostringstream stream;
    for (std::size_t i = 0; i < words.size(); ++i) {
        if (i != 0) {
            stream << ',';
        }
        stream << "0x" << std::hex << std::setw(8) << std::setfill('0') << words[i];
    }
    return stream.str();
}

bool WriteManifest(
    const std::filesystem::path& bankRoot,
    const std::vector<ScriptBankEntry>& entries,
    std::string* error) {
    try {
        std::filesystem::create_directories(bankRoot);
    } catch (const std::exception& exception) {
        if (error) {
            *error = exception.what();
        }
        return false;
    }

    std::ofstream manifest(bankRoot / "_manifest.tsv", std::ios::trunc);
    if (!manifest) {
        if (error) {
            *error = "Failed to open manifest for writing in " + bankRoot.string();
        }
        return false;
    }

    manifest << "path\toffset\tsize\tstored_size\tcompressed\textra_words\n";
    for (const ScriptBankEntry& entry : entries) {
        manifest << entry.relativePath << '\t' << entry.offset << '\t' << entry.size << '\t'
                 << entry.storedSize << '\t' << (entry.isCompressed ? 1 : 0) << '\t'
                 << JoinExtraWords(entry.extraWords) << '\n';
    }

    return true;
}

bool ExtractBankToWorkspace(
    const std::filesystem::path& gameDataDirectory,
    const std::filesystem::path& workspaceRoot,
    std::string_view bankName,
    ExtractionSummary* summary,
    std::ostream* log,
    std::string* error) {
    const std::filesystem::path bankPath = gameDataDirectory / bankName;
    if (!std::filesystem::exists(bankPath)) {
        return true;
    }

    ScriptBankTable table;
    std::vector<uint8_t> bankBytes;
    if (!ParseScriptBankTable(bankPath, &table, &bankBytes, error)) {
        return false;
    }

    const std::filesystem::path bankRoot = workspaceRoot / bankPath.stem();
    if (!WriteManifest(bankRoot, table.entries, error)) {
        return false;
    }

    for (const ScriptBankEntry& entry : table.entries) {
        const std::filesystem::path relativePath = NormalizeRelativePath(entry.relativePath);
        if (!IsSafeRelativePath(relativePath)) {
            if (error) {
                *error = "Refusing to extract unsafe path " + entry.relativePath;
            }
            return false;
        }

        const std::size_t dataOffset =
            static_cast<std::size_t>(table.baseOffset) + static_cast<std::size_t>(entry.offset);
        const std::size_t storedSize = static_cast<std::size_t>(entry.storedSize);
        if (dataOffset + storedSize > bankBytes.size()) {
            if (error) {
                *error = "Entry " + entry.relativePath + " exceeds bank bounds in " +
                         bankPath.string();
            }
            return false;
        }

        std::vector<uint8_t> extractedBytes;
        if (entry.isCompressed) {
            if (!DecompressEntryPayload(bankBytes.data() + dataOffset, storedSize, entry,
                                        &extractedBytes, error)) {
                if (error && !error->empty()) {
                    *error = "Failed to decompress " + entry.relativePath + " from " +
                             bankPath.string() + ": " + *error;
                }
                return false;
            }
        } else {
            extractedBytes.assign(bankBytes.begin() + static_cast<std::ptrdiff_t>(dataOffset),
                                  bankBytes.begin() +
                                      static_cast<std::ptrdiff_t>(dataOffset + entry.size));
        }

        if (!WriteFileBytes(bankRoot / relativePath, extractedBytes, error)) {
            if (error && !error->empty()) {
                *error = "Failed to write extracted file " + (bankRoot / relativePath).string() +
                         ": " + *error;
            }
            return false;
        }

        if (summary) {
            ++summary->extractedFileCount;
        }
    }

    if (summary) {
        ++summary->bankCount;
    }

    if (log) {
        *log << "Extracted " << table.entries.size() << " files from " << bankPath.filename().string()
             << " into " << bankRoot.string() << std::endl;
    }

    return true;
}

bool CopyDirectoryTree(
    const std::filesystem::path& sourceDirectory,
    const std::filesystem::path& destinationDirectory,
    ExtractionSummary* summary,
    std::ostream* log,
    std::string* error) {
    if (!std::filesystem::exists(sourceDirectory) || !std::filesystem::is_directory(sourceDirectory)) {
        return true;
    }

    try {
        for (const auto& entry : std::filesystem::recursive_directory_iterator(sourceDirectory)) {
            const std::filesystem::path relativePath =
                std::filesystem::relative(entry.path(), sourceDirectory);
            const std::filesystem::path destinationPath = destinationDirectory / relativePath;

            if (entry.is_directory()) {
                std::filesystem::create_directories(destinationPath);
                continue;
            }

            std::filesystem::create_directories(destinationPath.parent_path());
            std::filesystem::copy_file(
                entry.path(),
                destinationPath,
                std::filesystem::copy_options::overwrite_existing);
            if (summary) {
                ++summary->copiedLooseFileCount;
            }
        }
    } catch (const std::exception& exception) {
        if (error) {
            *error = exception.what();
        }
        return false;
    }

    if (log) {
        *log << "Copied loose scripts from " << sourceDirectory.string() << " into "
             << destinationDirectory.string() << std::endl;
    }

    return true;
}

bool WriteWorkspaceReadme(
    const std::filesystem::path& workspaceRoot,
    std::string* error) {
    const std::filesystem::path readmePath = workspaceRoot / "README.txt";
    std::ofstream readme(readmePath, std::ios::trunc);
    if (!readme) {
        if (error) {
            *error = "Failed to open " + readmePath.string() + " for writing";
        }
        return false;
    }

    readme
        << "refii Lua workspace\n\n"
        << "This folder is generated by the launcher installer.\n"
        << "It contains:\n"
        << "- mod-ready Lua files under the same paths the game expects, rooted at lua/\n"
        << "- a hidden .source/ cache with extracted script-bank payloads and copied loose scripts\n\n"
        << "Important notes:\n"
        << "- The original game content remains intact under data/.\n"
        << "- The installer prefers writing plaintext Lua into lua/ so the runtime can redirect there.\n"
        << "- gamescripts usually contains debug-info-rich versions of the same gameplay scripts found in gamescripts_r.\n"
        << "- gamescripts_r is the retail/runtime-oriented script bank.\n";
    return true;
}

}  // namespace

std::filesystem::path GetGameContentDirectoryPath(const std::filesystem::path& gameInstallRoot) {
    return gameInstallRoot / "data";
}

std::filesystem::path GetLuaWorkspacePath(const std::filesystem::path& gameInstallRoot) {
    return gameInstallRoot / "lua";
}

std::filesystem::path GetLuaWorkspaceSourcePath(const std::filesystem::path& gameInstallRoot) {
    return GetLuaWorkspacePath(gameInstallRoot) / ".source";
}

bool ExportLuaWorkspace(
    const std::filesystem::path& gameInstallRoot,
    ExtractionSummary* summary,
    std::ostream* log,
    std::string* error) {
    ExtractionSummary localSummary;
    ExtractionSummary* const activeSummary = summary != nullptr ? summary : &localSummary;
    *activeSummary = {};

    const std::filesystem::path gameContentDirectory = GetGameContentDirectoryPath(gameInstallRoot);
    const std::filesystem::path workspaceRoot = GetLuaWorkspacePath(gameInstallRoot);
    const std::filesystem::path sourceRoot = GetLuaWorkspaceSourcePath(gameInstallRoot);
    try {
        std::filesystem::create_directories(workspaceRoot);
        std::filesystem::create_directories(sourceRoot);
    } catch (const std::exception& exception) {
        if (error) {
            *error = exception.what();
        }
        return false;
    }

    for (const std::string_view bankName : kScriptBankNames) {
        if (!ExtractBankToWorkspace(gameContentDirectory, sourceRoot, bankName, activeSummary, log,
                                    error)) {
            return false;
        }
    }

    for (const std::string_view looseDirectoryName : kLooseScriptDirectoryNames) {
        if (!CopyDirectoryTree(gameContentDirectory / looseDirectoryName,
                               sourceRoot / looseDirectoryName, activeSummary, log, error)) {
            return false;
        }
    }

    if (!WriteWorkspaceReadme(workspaceRoot, error)) {
        return false;
    }

    if (log) {
        *log << "Lua workspace ready at " << workspaceRoot.string() << " ("
             << activeSummary->bankCount << " bank(s), " << activeSummary->extractedFileCount
             << " extracted file(s), " << activeSummary->copiedLooseFileCount
             << " copied loose file(s))" << std::endl;
    }

    return true;
}

}  // namespace Installer::ScriptBankExtractor
