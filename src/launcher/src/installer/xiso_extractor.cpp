/*
 * Derived from the extraction logic in XboxDev/extract-xiso:
 * https://github.com/XboxDev/extract-xiso
 *
 * The bundled license notice is retained in
 * src/launcher/src/installer/third_party/extract-xiso-LICENSE.txt.
 */

#include "installer/xiso_extractor.h"

#include <algorithm>
#include <array>
#include <cstdint>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <string>
#include <string_view>
#include <unordered_set>
#include <vector>

namespace {

constexpr std::uint64_t kGlobalLseekOffset = 0x0FD90000ull;
constexpr std::uint64_t kXgd3LseekOffset = 0x02080000ull;
constexpr std::uint64_t kXgd1LseekOffset = 0x18300000ull;

constexpr std::uint64_t kHeaderOffset = 0x10000ull;
constexpr std::uint64_t kOptimizedTagOffset = 31337ull;
constexpr std::size_t kHeaderDataLength = 20;
constexpr std::size_t kSectorSize = 2048;
constexpr std::size_t kEntryHeaderSize = 14;
constexpr std::size_t kCopyBufferSize = 2 * 1024 * 1024;
constexpr std::uint16_t kPadShort = 0xffffu;
constexpr std::uint8_t kAttributeDirectory = 0x10u;

constexpr char kHeaderData[kHeaderDataLength + 1] = "MICROSOFT*XBOX*MEDIA";
constexpr char kOptimizedTagPrefix[] = "in!xiso";

std::uint16_t ReadLittleEndian16(const std::uint8_t* data) {
    return static_cast<std::uint16_t>(data[0]) |
           static_cast<std::uint16_t>(data[1] << 8);
}

std::uint32_t ReadLittleEndian32(const std::uint8_t* data) {
    return static_cast<std::uint32_t>(data[0]) |
           (static_cast<std::uint32_t>(data[1]) << 8) |
           (static_cast<std::uint32_t>(data[2]) << 16) |
           (static_cast<std::uint32_t>(data[3]) << 24);
}

std::size_t AlignToSector(std::size_t value) {
    if (value == 0) {
        return 0;
    }

    const std::size_t remainder = value % kSectorSize;
    return remainder == 0 ? value : value + (kSectorSize - remainder);
}

std::string PathString(const std::filesystem::path& path) {
    const std::u8string utf8Path = path.u8string();
    std::string result;
    result.reserve(utf8Path.size());
    for (char8_t byte : utf8Path) {
        result.push_back(static_cast<char>(byte));
    }
    return result;
}

bool HasInvalidFilename(std::string_view filename) {
    auto to_lower = [](unsigned char ch) {
        return static_cast<char>(std::tolower(ch));
    };

    if (filename.empty() || filename == "." || filename == ".." ||
        filename.find('\0') != std::string_view::npos) {
        return true;
    }
    if (filename.back() == ' ' || filename.back() == '.') {
        return true;
    }

    for (unsigned char ch : filename) {
        if (ch <= 0x1F || ch == '<' || ch == '>' || ch == ':' || ch == '"' || ch == '/' ||
            ch == '\\' || ch == '|' || ch == '?' || ch == '*') {
            return true;
        }
    }

    std::string lowered = std::string(filename);
    std::transform(lowered.begin(), lowered.end(), lowered.begin(), to_lower);
    const std::string base_name = lowered.substr(0, lowered.find('.'));
    return lowered.empty() || base_name.empty() ||
           base_name == "con" || base_name == "prn" || base_name == "aux" ||
           base_name == "nul" || base_name == "clock$" ||
           ((base_name.size() == 4 && base_name[0] == 'c' && base_name[1] == 'o' &&
             base_name[2] == 'm' && std::isdigit(static_cast<unsigned char>(base_name[3]))) ||
            (base_name.size() == 4 && base_name[0] == 'l' && base_name[1] == 'p' &&
             base_name[2] == 't' && std::isdigit(static_cast<unsigned char>(base_name[3]))));
}

struct DirectoryEntry {
    std::uint16_t leftOffset = 0;
    std::uint16_t rightOffset = 0;
    std::uint32_t startSector = 0;
    std::uint32_t fileSize = 0;
    bool isDirectory = false;
    std::string filename;
};

struct ParsedEntry {
    DirectoryEntry entry;
    std::size_t entryOffset = 0;
    std::size_t nextOffset = 0;
};

class Extractor {
public:
    bool Extract(
        const std::filesystem::path& isoPath,
        const std::filesystem::path& destinationDirectory,
        std::string* error) {
        imagePath_ = isoPath;
        imageStream_.open(isoPath, std::ios::binary);
        if (!imageStream_.is_open()) {
            return Fail("Failed to open " + PathString(isoPath) + ".", error);
        }

        bool optimized = false;
        DetectOptimizedTag(optimized);

        std::uint32_t rootDirectorySector = 0;
        std::uint32_t rootDirectorySize = 0;
        if (!DetectDiscLayout(rootDirectorySector, rootDirectorySize, error)) {
            return false;
        }

        if (rootDirectorySector == 0 && rootDirectorySize == 0) {
            return Fail("The Xbox ISO contains no files.", error);
        }

        copyBuffer_.resize(kCopyBufferSize);
        return TraverseDirectory(
            static_cast<std::uint64_t>(rootDirectorySector) * kSectorSize + discOffset_,
            rootDirectorySize,
            destinationDirectory,
            !optimized,
            error);
    }

private:
    bool Seek(std::uint64_t offset) {
        imageStream_.clear();
        imageStream_.seekg(static_cast<std::streamoff>(offset), std::ios::beg);
        return !imageStream_.fail();
    }

    bool ReadExact(void* buffer, std::size_t size) {
        if (size == 0) {
            return true;
        }

        imageStream_.read(static_cast<char*>(buffer), static_cast<std::streamsize>(size));
        return static_cast<std::size_t>(imageStream_.gcount()) == size;
    }

    bool ReadAt(std::uint64_t offset, void* buffer, std::size_t size) {
        return Seek(offset) && ReadExact(buffer, size);
    }

    bool DetectOptimizedTag(bool& optimized) {
        std::array<char, sizeof(kOptimizedTagPrefix) - 1> buffer = {};
        optimized = ReadAt(kOptimizedTagOffset, buffer.data(), buffer.size()) &&
                    std::memcmp(
                        buffer.data(),
                        kOptimizedTagPrefix,
                        sizeof(kOptimizedTagPrefix) - 1) == 0;
        return true;
    }

    bool ReadFilesystemHeader(
        std::uint64_t candidateOffset,
        std::uint32_t& rootDirectorySector,
        std::uint32_t& rootDirectorySize) {
        std::array<char, kHeaderDataLength> header = {};
        if (!ReadAt(candidateOffset + kHeaderOffset, header.data(), header.size()) ||
            std::memcmp(header.data(), kHeaderData, header.size()) != 0) {
            return false;
        }

        std::array<std::uint8_t, 8> rootData = {};
        if (!ReadAt(
                candidateOffset + kHeaderOffset + kHeaderDataLength,
                rootData.data(),
                rootData.size())) {
            return false;
        }

        std::array<char, kHeaderDataLength> footer = {};
        const std::uint64_t footerOffset =
            candidateOffset + kHeaderOffset + kHeaderDataLength + 4 + 4 + 8 + 0x7c8;
        if (!ReadAt(footerOffset, footer.data(), footer.size()) ||
            std::memcmp(footer.data(), kHeaderData, footer.size()) != 0) {
            return false;
        }

        rootDirectorySector = ReadLittleEndian32(rootData.data());
        rootDirectorySize = ReadLittleEndian32(rootData.data() + 4);
        return true;
    }

    bool DetectDiscLayout(
        std::uint32_t& rootDirectorySector,
        std::uint32_t& rootDirectorySize,
        std::string* error) {
        const std::array<std::uint64_t, 4> offsets = {
            0ull,
            kGlobalLseekOffset,
            kXgd3LseekOffset,
            kXgd1LseekOffset,
        };

        for (const std::uint64_t offset : offsets) {
            if (ReadFilesystemHeader(offset, rootDirectorySector, rootDirectorySize)) {
                discOffset_ = offset;
                return true;
            }
        }

        return Fail(
            PathString(imagePath_) + " does not appear to be a valid Xbox ISO image.",
            error);
    }

    bool LoadDirectoryTable(
        std::uint64_t absoluteOffset,
        std::uint32_t directorySize,
        std::vector<std::uint8_t>& table,
        std::string* error) {
        if (directorySize == 0) {
            table.clear();
            return true;
        }

        const std::size_t bytesToRead = std::max<std::size_t>(
            AlignToSector(static_cast<std::size_t>(directorySize)),
            kSectorSize);
        table.resize(bytesToRead);
        if (!ReadAt(absoluteOffset, table.data(), table.size())) {
            return Fail(
                "Failed to read an XDVDFS directory table from " + PathString(imagePath_) + ".",
                error);
        }

        return true;
    }

    bool ParseEntry(
        const std::vector<std::uint8_t>& table,
        std::size_t requestedOffset,
        ParsedEntry& parsedEntry,
        bool& emptyDirectory,
        std::string* error) {
        emptyDirectory = false;

        if (requestedOffset >= table.size()) {
            return Fail("Encountered an invalid directory entry offset.", error);
        }

        std::size_t offset = requestedOffset;
        while (true) {
            if (offset + sizeof(std::uint16_t) > table.size()) {
                return Fail("Encountered a truncated directory table.", error);
            }

            const std::uint16_t marker = ReadLittleEndian16(table.data() + offset);
            if (marker != kPadShort) {
                break;
            }

            if (offset == 0) {
                emptyDirectory = true;
                return true;
            }

            offset = AlignToSector(offset + 1);
            if (offset >= table.size()) {
                return Fail("Encountered a truncated directory table.", error);
            }
        }

        if (offset + kEntryHeaderSize > table.size()) {
            return Fail("Encountered a truncated directory entry.", error);
        }

        const auto* entryData = table.data() + offset;
        const std::size_t filenameLength = static_cast<std::size_t>(entryData[13]);
        if (filenameLength == 0 || offset + kEntryHeaderSize + filenameLength > table.size()) {
            return Fail("Encountered a truncated XDVDFS filename.", error);
        }

        parsedEntry.entryOffset = offset;
        parsedEntry.entry.leftOffset = ReadLittleEndian16(entryData);
        parsedEntry.entry.rightOffset = ReadLittleEndian16(entryData + 2);
        parsedEntry.entry.startSector = ReadLittleEndian32(entryData + 4);
        parsedEntry.entry.fileSize = ReadLittleEndian32(entryData + 8);
        parsedEntry.entry.isDirectory = (entryData[12] & kAttributeDirectory) != 0;
        parsedEntry.entry.filename.assign(
            reinterpret_cast<const char*>(entryData + kEntryHeaderSize),
            filenameLength);
        parsedEntry.nextOffset = offset + kEntryHeaderSize + filenameLength;

        if (HasInvalidFilename(parsedEntry.entry.filename)) {
            return Fail(
                "Encountered an invalid filename while extracting " + PathString(imagePath_) +
                    ".",
                error);
        }

        return true;
    }

    bool TraverseTree(
        const std::vector<std::uint8_t>& table,
        std::size_t entryOffset,
        const std::filesystem::path& outputPath,
        bool linkedListCompat,
        std::unordered_set<std::size_t>& visitedEntries,
        std::string* error) {
        ParsedEntry parsedEntry;
        bool emptyDirectory = false;
        if (!ParseEntry(table, entryOffset, parsedEntry, emptyDirectory, error)) {
            return false;
        }

        if (emptyDirectory) {
            return true;
        }

        if (!visitedEntries.insert(parsedEntry.entryOffset).second) {
            return Fail("Encountered a cyclic directory tree inside the Xbox ISO.", error);
        }

        const DirectoryEntry& entry = parsedEntry.entry;
        if (entry.leftOffset != 0) {
            linkedListCompat = false;
        }

        if (entry.leftOffset != 0 &&
            !TraverseTree(
                table,
                static_cast<std::size_t>(entry.leftOffset) * 4,
                outputPath,
                linkedListCompat,
                visitedEntries,
                error)) {
            return false;
        }

        const std::filesystem::path entryPath = outputPath / entry.filename;
        if (entry.isDirectory) {
            try {
                std::filesystem::create_directories(entryPath);
            } catch (const std::exception& exception) {
                return Fail(exception.what(), error);
            }

            if (entry.fileSize > 0 &&
                !TraverseDirectory(
                    static_cast<std::uint64_t>(entry.startSector) * kSectorSize + discOffset_,
                    entry.fileSize,
                    entryPath,
                    linkedListCompat,
                    error)) {
                return false;
            }
        } else if (!ExtractFile(entry, entryPath, error)) {
            return false;
        }

        if (entry.rightOffset != 0) {
            std::uint16_t rightOffset = entry.rightOffset;
            const std::size_t currentSector = parsedEntry.nextOffset / kSectorSize;
            if (linkedListCompat &&
                static_cast<std::size_t>(rightOffset) * 4 / kSectorSize > currentSector) {
                rightOffset = static_cast<std::uint16_t>(
                    (currentSector + 1) * (kSectorSize / 4));
            }

            if (!TraverseTree(
                    table,
                    static_cast<std::size_t>(rightOffset) * 4,
                    outputPath,
                    linkedListCompat,
                    visitedEntries,
                    error)) {
                return false;
            }
        }

        return true;
    }

    bool TraverseDirectory(
        std::uint64_t absoluteOffset,
        std::uint32_t directorySize,
        const std::filesystem::path& outputPath,
        bool linkedListCompat,
        std::string* error) {
        std::vector<std::uint8_t> table;
        if (!LoadDirectoryTable(absoluteOffset, directorySize, table, error)) {
            return false;
        }

        if (table.empty()) {
            return true;
        }

        std::unordered_set<std::size_t> visitedEntries;
        return TraverseTree(table, 0, outputPath, linkedListCompat, visitedEntries, error);
    }

    bool ExtractFile(
        const DirectoryEntry& entry,
        const std::filesystem::path& outputPath,
        std::string* error) {
        try {
            std::filesystem::create_directories(outputPath.parent_path());
        } catch (const std::exception& exception) {
            return Fail(exception.what(), error);
        }

        std::ofstream output(outputPath, std::ios::binary | std::ios::trunc);
        if (!output.is_open()) {
            return Fail("Failed to create " + PathString(outputPath) + ".", error);
        }

        if (entry.fileSize == 0) {
            return true;
        }

        const std::uint64_t fileOffset =
            static_cast<std::uint64_t>(entry.startSector) * kSectorSize + discOffset_;
        if (!Seek(fileOffset)) {
            return Fail("Failed to seek inside " + PathString(imagePath_) + ".", error);
        }

        std::uint32_t remaining = entry.fileSize;
        while (remaining > 0) {
            const std::size_t chunkSize = std::min<std::size_t>(remaining, copyBuffer_.size());
            if (!ReadExact(copyBuffer_.data(), chunkSize)) {
                return Fail(
                    "Unexpected end of file while extracting " + entry.filename + ".",
                    error);
            }

            output.write(copyBuffer_.data(), static_cast<std::streamsize>(chunkSize));
            if (!output) {
                return Fail("Failed to write " + PathString(outputPath) + ".", error);
            }

            remaining -= static_cast<std::uint32_t>(chunkSize);
        }

        return true;
    }

    bool Fail(const std::string& message, std::string* error) {
        if (error) {
            *error = message;
        }
        return false;
    }

    std::filesystem::path imagePath_;
    std::ifstream imageStream_;
    std::uint64_t discOffset_ = 0;
    std::vector<char> copyBuffer_;
};

}  // namespace

namespace Installer::XisoExtractor {

bool ExtractImage(
    const std::filesystem::path& isoPath,
    const std::filesystem::path& destinationDirectory,
    std::string* error) {
    Extractor extractor;
    return extractor.Extract(isoPath, destinationDirectory, error);
}

}  // namespace Installer::XisoExtractor
