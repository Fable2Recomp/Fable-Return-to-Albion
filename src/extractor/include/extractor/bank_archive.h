#pragma once

// Random-access reads from a Fable 2 .bnk bank.
//
// ExtractBanks() walks a whole tree and writes it to disk. This is the other
// shape: open a bank once, keep its parsed file table in memory, and pull
// individual entries on demand. Nothing is written, and nothing is held beyond
// the table itself, so a host-side consumer can stream assets straight out of
// the shipped banks.
//
// Both shapes share one parser. The bank layout is only described in
// bnk_extractor.cpp, and a second implementation would be free to drift from
// the one the guest's VFS agrees with.

#include <cstddef>
#include <cstdint>
#include <filesystem>
#include <memory>
#include <string>
#include <vector>

namespace refii::bnk {

/// One file inside a bank. Sizes are of the decoded bytes, so a caller can size
/// a buffer without touching the payload.
struct BankArchiveEntry {
    std::string relative_path;
    std::uint32_t size = 0;
    std::uint32_t stored_size = 0;
    bool is_compressed = false;
};

/// An open bank. Holds the file handle and the decoded entry table; entry
/// payloads are read and inflated per call, never cached.
class BankArchive {
public:
    /// Opens a bank and parses its table. Returns null on failure, with the
    /// reason in `error` when supplied.
    static std::unique_ptr<BankArchive> Open(const std::filesystem::path& bank_path,
                                             std::string* error = nullptr);

    ~BankArchive();

    BankArchive(const BankArchive&) = delete;
    BankArchive& operator=(const BankArchive&) = delete;

    const std::vector<BankArchiveEntry>& Entries() const;

    /// Index of `relative_path`, or -1. Matching is case-insensitive and treats
    /// '\\' and '/' alike, because bank names are authored in Windows form while
    /// callers tend to hold posix paths.
    std::ptrdiff_t Find(std::string_view relative_path) const;

    /// Reads and decodes one entry. The payload is read from disk on every call.
    bool Read(std::size_t index,
              std::vector<std::uint8_t>* output_bytes,
              std::string* error = nullptr) const;

private:
    BankArchive();

    struct Impl;
    std::unique_ptr<Impl> impl_;
};

}  // namespace refii::bnk

// ---------------------------------------------------------------------------
// C ABI
//
// Lets a non-C++ host (the Godot launcher is C#) stream from banks by P/Invoke,
// without a GDExtension or any godot-cpp dependency. Strings are UTF-8; returned
// pointers stay valid until the handle is closed.
// ---------------------------------------------------------------------------

extern "C" {

typedef struct RefiiBankArchive RefiiBankArchive;

/// Opens a bank. Returns null on failure and writes a NUL-terminated reason into
/// `error` when it is non-null.
RefiiBankArchive* RefiiBankArchiveOpen(const char* bank_path, char* error, size_t error_capacity);

void RefiiBankArchiveClose(RefiiBankArchive* archive);

size_t RefiiBankArchiveEntryCount(const RefiiBankArchive* archive);

/// Borrowed UTF-8 path for an entry, or null when out of range.
const char* RefiiBankArchiveEntryPath(const RefiiBankArchive* archive, size_t index);

/// Decoded size of an entry, or 0 when out of range.
uint32_t RefiiBankArchiveEntrySize(const RefiiBankArchive* archive, size_t index);

/// Entry index for a path, or -1. Same matching rules as BankArchive::Find.
int64_t RefiiBankArchiveFind(const RefiiBankArchive* archive, const char* relative_path);

/// Reads one entry into `buffer`. Writes the decoded length to `written` even
/// when the buffer is too small, so a caller can size then re-read. Returns 0 on
/// failure, 1 on success, and -1 when the buffer is too small.
int RefiiBankArchiveRead(const RefiiBankArchive* archive,
                         size_t index,
                         uint8_t* buffer,
                         size_t buffer_capacity,
                         size_t* written,
                         char* error,
                         size_t error_capacity);

}  // extern "C"
