#include "extractor/bnk_extractor.h"
#include "babel/babel_database.h"
#include "gdb/gdb_database.h"

#include <exception>
#include <filesystem>
#include <iostream>
#include <string>
#include <vector>

namespace {

void PrintUsage(const char* executable_name) {
    std::cerr << "Usage: " << executable_name
              << " [--manifest-only] [--workers N] [--no-gdb-csv] "
                 "[--no-babel-csv] "
                 "<game-content-root> <output-root> <bank> [bank ...]\n";
    std::cerr << "       " << executable_name
              << " --build-vfs-database [--no-gdb-csv] [--no-babel-csv] "
                 "<unpacked-root> [database-path]\n";
    std::cerr << "       " << executable_name
              << " --gdb2csv <input.gdb> [output.csv]\n";
    std::cerr << "       " << executable_name
              << " --csv2gdb <input.csv> [output.gdb]\n";
    std::cerr << "       " << executable_name
              << " --babel2csv <input.babel> [output.csv]\n";
    std::cerr << "       " << executable_name
              << " --csv2babel <input.csv> [output.babel]\n";
}

// Runs one of the four database <-> CSV conversion commands. `binary_ext` is
// the binary format's extension (".gdb" / ".babel").
int RunDatabaseCsvConversion(bool to_csv, const char* binary_ext,
                             bool (*to_csv_fn)(const std::filesystem::path&,
                                               const std::filesystem::path&,
                                               std::string*),
                             bool (*to_bin_fn)(const std::filesystem::path&,
                                               const std::filesystem::path&,
                                               std::string*),
                             int argc, char** argv) {
    if (argc < 3 || argc > 4) {
        PrintUsage(argv[0]);
        return 2;
    }
    const std::filesystem::path input(argv[2]);
    std::filesystem::path output;
    if (argc == 4) {
        output = std::filesystem::path(argv[3]);
    } else if (to_csv) {
        output = input;
        output += ".csv";
    } else {
        // foo.<ext>.csv -> foo.<ext>, otherwise swap the extension.
        output = input;
        if (output.extension() == ".csv") {
            output.replace_extension();
        }
        if (output.extension() != binary_ext) {
            output.replace_extension(binary_ext);
        }
    }

    std::string error;
    const bool ok = to_csv ? to_csv_fn(input, output, &error)
                           : to_bin_fn(input, output, &error);
    if (!ok) {
        std::cerr << "conversion failed: " << error << '\n';
        return 1;
    }
    std::cout << "Wrote " << output.generic_string() << '\n';
    return 0;
}

}  // namespace

int main(int argc, char** argv) {
    int first_positional = 1;
    bool manifest_only = false;
    bool convert_gdb_databases = true;
    bool convert_babel_databases = true;
    std::size_t payload_worker_count = 0;
    if (argc > 1 && std::string(argv[1]) == "--gdb2csv") {
        return RunDatabaseCsvConversion(
            true, ".gdb", refii::vfs::gdb::ConvertGdbFileToCsvFile,
            refii::vfs::gdb::ConvertCsvFileToGdbFile, argc, argv);
    }
    if (argc > 1 && std::string(argv[1]) == "--csv2gdb") {
        return RunDatabaseCsvConversion(
            false, ".gdb", refii::vfs::gdb::ConvertGdbFileToCsvFile,
            refii::vfs::gdb::ConvertCsvFileToGdbFile, argc, argv);
    }
    if (argc > 1 && std::string(argv[1]) == "--babel2csv") {
        return RunDatabaseCsvConversion(
            true, ".babel", refii::vfs::babel::ConvertBabelFileToCsvFile,
            refii::vfs::babel::ConvertCsvFileToBabelFile, argc, argv);
    }
    if (argc > 1 && std::string(argv[1]) == "--csv2babel") {
        return RunDatabaseCsvConversion(
            false, ".babel", refii::vfs::babel::ConvertBabelFileToCsvFile,
            refii::vfs::babel::ConvertCsvFileToBabelFile, argc, argv);
    }
    if (argc > 1 && std::string(argv[1]) == "--build-vfs-database") {
        refii::bnk::VfsDatabaseBuildOptions options;
        int next = 2;
        for (; next < argc; ++next) {
            const std::string flag(argv[next]);
            if (flag == "--gdb-csv") {
                options.convert_gdb_databases = true;
            } else if (flag == "--babel-csv") {
                options.convert_babel_databases = true;
            } else if (flag == "--no-gdb-csv") {
                options.convert_gdb_databases = false;
            } else if (flag == "--no-babel-csv") {
                options.convert_babel_databases = false;
            } else {
                break;
            }
        }
        if (argc - next < 1 || argc - next > 2) {
            PrintUsage(argv[0]);
            return 2;
        }
        options.unpacked_root = std::filesystem::path(argv[next]);
        if (argc - next == 2) {
            options.database_path = std::filesystem::path(argv[next + 1]);
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
        if (summary.database_csv_emitted != 0) {
            std::cout << "Converted " << summary.database_csv_emitted
                      << " GDB/Babel database(s) to editable loose CSV\n";
        }
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

    while (first_positional < argc) {
        const std::string argument(argv[first_positional]);
        if (argument == "--manifest-only") {
            manifest_only = true;
            ++first_positional;
            continue;
        }
        if (argument == "--gdb-csv") {
            convert_gdb_databases = true;
            ++first_positional;
            continue;
        }
        if (argument == "--babel-csv") {
            convert_babel_databases = true;
            ++first_positional;
            continue;
        }
        if (argument == "--no-gdb-csv") {
            convert_gdb_databases = false;
            ++first_positional;
            continue;
        }
        if (argument == "--no-babel-csv") {
            convert_babel_databases = false;
            ++first_positional;
            continue;
        }
        if (argument == "--workers") {
            if (first_positional + 1 >= argc) {
                PrintUsage(argv[0]);
                return 2;
            }
            try {
                payload_worker_count =
                    static_cast<std::size_t>(std::stoull(argv[first_positional + 1]));
            } catch (const std::exception&) {
                std::cerr << "--workers requires a non-negative integer\n";
                return 2;
            }
            first_positional += 2;
            continue;
        }
        break;
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
    options.convert_gdb_databases = convert_gdb_databases;
    options.convert_babel_databases = convert_babel_databases;
    options.payload_worker_count = payload_worker_count;
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
        if (summary.database_csv_converted != 0) {
            std::cout << "Converted " << summary.database_csv_converted
                      << " loose GDB/Babel database(s) to editable CSV\n";
        }
    }
    return 0;
}
