#pragma once

#include <cstddef>
#include <filesystem>
#include <functional>
#include <string>
#include <vector>

namespace refii::bnk {

enum class BankFormatVersion {
    kUnknown,
    kV2,
    kV3,
};

struct BankExtractionProgress {
    std::size_t completed_banks = 0;
    std::size_t total_banks = 0;
    std::filesystem::path current_bank;
    std::string phase;
};

struct BankExtractionOptions {
    std::filesystem::path game_content_root;
    std::filesystem::path output_root;
    std::vector<std::filesystem::path> bank_relative_paths;
    bool clean_output = true;
    bool extract_payloads = true;
    bool extract_nested_banks = true;
    bool build_vfs_database = true;
    std::function<void(const BankExtractionProgress&)> progress_callback;
};

struct BankExtractionSummary {
    std::filesystem::path game_content_root;
    std::filesystem::path output_root;
    std::size_t extracted_banks = 0;
    std::size_t extracted_files = 0;
    std::size_t duplicate_conflicts = 0;
    std::size_t vfs_database_entries = 0;
};

struct VfsDatabaseBuildOptions {
    std::filesystem::path unpacked_root;
    std::filesystem::path database_path;
    std::filesystem::path runtime_index_root;
    bool write_legacy_database = false;
};

struct VfsDatabaseBuildSummary {
    std::filesystem::path database_path;
    std::filesystem::path runtime_index_root;
    std::size_t entries = 0;
    std::size_t runtime_alias_entries = 0;
    std::size_t runtime_hash_entries = 0;
    std::size_t runtime_directories = 0;
    std::size_t indexed_files = 0;
    std::size_t indexed_manifests = 0;
    std::size_t indexed_filehash_tables = 0;
};

std::filesystem::path GetBankOutputRoot(const std::filesystem::path& output_root,
                                        const std::filesystem::path& bank_relative_path);

bool ExtractBanks(const BankExtractionOptions& options,
                  BankExtractionSummary* summary = nullptr,
                  std::string* error = nullptr);

bool BuildVfsDatabase(const VfsDatabaseBuildOptions& options,
                      VfsDatabaseBuildSummary* summary = nullptr,
                      std::string* error = nullptr);

}  // namespace refii::bnk
