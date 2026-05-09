#include "extractor/bnk_extractor.h"

#include <filesystem>
#include <iostream>
#include <string>
#include <vector>

namespace {

void PrintUsage(const char* executable_name) {
    std::cerr << "Usage: " << executable_name
              << " [--manifest-only] <game-content-root> <output-root> <bank> [bank ...]\n";
    std::cerr << "       " << executable_name
              << " --build-vfs-database <unpacked-root> [database-path]\n";
}

}  // namespace

int main(int argc, char** argv) {
    int first_positional = 1;
    bool manifest_only = false;
    if (argc > 1 && std::string(argv[1]) == "--build-vfs-database") {
        if (argc < 3 || argc > 4) {
            PrintUsage(argv[0]);
            return 2;
        }

        refii::bnk::VfsDatabaseBuildOptions options;
        options.unpacked_root = std::filesystem::path(argv[2]);
        if (argc == 4) {
            options.database_path = std::filesystem::path(argv[3]);
        }

        refii::bnk::VfsDatabaseBuildSummary summary;
        std::string error;
        if (!refii::bnk::BuildVfsDatabase(options, &summary, &error)) {
            std::cerr << "VFS database build failed: " << error << '\n';
            return 1;
        }

        std::cout << "Indexed " << summary.entries << " VFS source row(s) from "
                  << summary.indexed_files << " file(s), "
                  << summary.indexed_manifests << " manifest(s), and "
                  << summary.indexed_filehash_tables << " filehash table(s)\n";
        if (!summary.database_path.empty()) {
            std::cout << "Wrote legacy VFS database to "
                      << summary.database_path.generic_string() << '\n';
        }
        std::cout << "Wrote " << summary.runtime_alias_entries
                  << " alias index row(s) and " << summary.runtime_hash_entries
                  << " hash index row(s), " << summary.runtime_candidates
                  << " candidate(s), plus "
                  << summary.runtime_directories << " virtual director"
                  << (summary.runtime_directories == 1 ? "y" : "ies")
                  << " to "
                  << summary.runtime_index_root.generic_string() << '\n';
        if (summary.unindexed_files != 0) {
            std::cout << "VFS audit found " << summary.unindexed_files
                      << " unindexed loose file candidate"
                      << (summary.unindexed_files == 1 ? "" : "s")
                      << " in "
                      << (summary.runtime_index_root / "unindexed_candidates.tsv")
                             .generic_string()
                      << '\n';
        }
        return 0;
    }

    if (argc > 1 && std::string(argv[1]) == "--manifest-only") {
        manifest_only = true;
        first_positional = 2;
    }

    if (argc - first_positional < 3) {
        PrintUsage(argv[0]);
        return 2;
    }

    refii::bnk::BankExtractionOptions options;
    options.game_content_root = std::filesystem::path(argv[first_positional]);
    options.output_root = std::filesystem::path(argv[first_positional + 1]);
    options.clean_output = true;
    options.extract_payloads = !manifest_only;
    for (int index = first_positional + 2; index < argc; ++index) {
        options.bank_relative_paths.emplace_back(argv[index]);
    }
    options.progress_callback = [](const refii::bnk::BankExtractionProgress& progress) {
        if (!progress.current_bank.empty()) {
            std::cout << "[" << progress.completed_banks << "/" << progress.total_banks
                      << "] " << progress.current_bank.generic_string() << '\n';
        }
    };

    refii::bnk::BankExtractionSummary summary;
    std::string error;
    if (!refii::bnk::ExtractBanks(options, &summary, &error)) {
        std::cerr << "Extraction failed: " << error << '\n';
        return 1;
    }

    if (manifest_only) {
        std::cout << "Wrote manifest(s) for " << summary.extracted_banks << " bank(s) into "
                  << summary.output_root.generic_string() << '\n';
    } else {
        std::cout << "Extracted " << summary.extracted_files << " file(s) from "
                  << summary.extracted_banks << " bank(s) into "
                  << summary.output_root.generic_string();
        if (summary.duplicate_conflicts != 0) {
            std::cout << " (" << summary.duplicate_conflicts
                      << " differing duplicate conflict(s) routed)";
        }
        std::cout << '\n';
    }
    return 0;
}
