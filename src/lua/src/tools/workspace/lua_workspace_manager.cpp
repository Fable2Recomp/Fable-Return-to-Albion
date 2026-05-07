#include "tools/native/native_lua_decompiler.h"

#include "extractor/bnk_extractor.h"
#include "tools/crypto/sha256.h"

#include <algorithm>
#include <array>
#include <cctype>
#include <filesystem>
#include <fstream>
#include <ostream>
#include <sstream>
#include <string>
#include <string_view>
#include <system_error>
#include <unordered_map>
#include <vector>

namespace refii::lua {
namespace {

struct ExtractionSummary {
    std::size_t bank_count = 0;
    std::size_t extracted_file_count = 0;
    std::size_t copied_loose_file_count = 0;
};

struct BaselineEntry {
    std::filesystem::path relative_path;
    std::string sha256;
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

constexpr std::string_view kBaselineHeader = "path\tsha256\n";
constexpr std::string_view kDisassemblyPlaceholderMarker = "-- refii-disassembly-placeholder";

std::filesystem::path GetGameContentDirectoryPath(const std::filesystem::path& game_install_root) {
    return game_install_root / "data";
}

std::string LowerAscii(std::string value) {
    std::transform(value.begin(), value.end(), value.begin(), [](unsigned char ch) {
        return static_cast<char>(std::tolower(ch));
    });
    return value;
}

bool IsWindowsReservedName(std::string_view name) {
    if (name.empty()) {
        return false;
    }
    if (name == "con" || name == "prn" || name == "aux" || name == "nul" ||
        name == "clock$") {
        return true;
    }
    if ((name.size() == 4 && name[0] == 'c' && name[1] == 'o' && name[2] == 'm' &&
         std::isdigit(static_cast<unsigned char>(name[3]))) ||
        (name.size() == 4 && name[0] == 'l' && name[1] == 'p' && name[2] == 't' &&
         std::isdigit(static_cast<unsigned char>(name[3])))) {
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
    return !IsWindowsReservedName(LowerAscii(std::string(filename)));
}

bool IsSafeRelativePath(const std::filesystem::path& path, std::string* error) {
    const std::filesystem::path candidate = path.relative_path().lexically_normal();
    if (candidate.empty()) {
        if (error != nullptr) {
            *error = "invalid output path: empty";
        }
        return false;
    }
    for (const auto& component : candidate) {
        const std::string part = component.generic_string();
        if (part.empty() || part == "." || part == "..") {
            if (error != nullptr) {
                *error = "invalid output path component: " + part;
            }
            return false;
        }
        if (!IsWindowsCompatibleFilename(part)) {
            if (error != nullptr) {
                *error = "output path component not compatible with Windows: " + part;
            }
            return false;
        }
    }
    return true;
}

std::filesystem::path GetWorkspaceBasePath(const std::filesystem::path& game_install_root) {
    std::filesystem::path base = game_install_root;
    const std::string filename = LowerAscii(base.filename().generic_string());
    if (filename == "data") {
        base = base.parent_path();
    }
    if (LowerAscii(base.filename().generic_string()) == "game") {
        base = base.parent_path();
    }
    return base;
}

std::string NormalizeRelativePathString(const std::filesystem::path& path) {
    return path.lexically_normal().generic_string();
}

void EmitProgress(const std::function<void(const LuaWorkspaceDecompileProgress&)>& callback,
                  std::string phase,
                  std::size_t completed_files = 0,
                  std::size_t total_files = 0,
                  const std::filesystem::path& current_input = {}) {
    if (!callback) {
        return;
    }
    LuaWorkspaceDecompileProgress progress;
    progress.phase = std::move(phase);
    progress.completed_files = completed_files;
    progress.total_files = total_files;
    progress.current_input = current_input;
    callback(progress);
}

bool ReadTextFile(const std::filesystem::path& path, std::string* text, std::string* error = nullptr) {
    if (text == nullptr) {
        if (error != nullptr) {
            *error = "output text buffer missing";
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

    text->assign(std::istreambuf_iterator<char>(file), std::istreambuf_iterator<char>());
    if (!file.good() && !file.eof()) {
        if (error != nullptr) {
            *error = "Failed to read " + path.string();
        }
        return false;
    }
    return true;
}

bool WriteTextFile(const std::filesystem::path& path, std::string_view text, std::string* error) {
    if (!IsSafeRelativePath(path, error)) {
        return false;
    }

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

bool CopyDirectoryTree(const std::filesystem::path& source_directory,
                       const std::filesystem::path& destination_directory,
                       ExtractionSummary* summary,
                       std::ostream* log,
                       std::string* error) {
    if (!std::filesystem::exists(source_directory) ||
        !std::filesystem::is_directory(source_directory)) {
        return true;
    }

    try {
        for (const auto& entry : std::filesystem::recursive_directory_iterator(source_directory)) {
            const std::filesystem::path relative_path =
                std::filesystem::relative(entry.path(), source_directory);
            const std::filesystem::path destination_path = destination_directory / relative_path;
            if (!IsSafeRelativePath(destination_path, error)) {
                return false;
            }

            if (entry.is_directory()) {
                std::filesystem::create_directories(destination_path);
                continue;
            }

            std::filesystem::create_directories(destination_path.parent_path());
            std::filesystem::copy_file(entry.path(),
                                       destination_path,
                                       std::filesystem::copy_options::overwrite_existing);
            if (summary != nullptr) {
                ++summary->copied_loose_file_count;
            }
        }
    } catch (const std::exception& exception) {
        if (error != nullptr) {
            *error = exception.what();
        }
        return false;
    }

    if (log != nullptr) {
        *log << "Copied loose scripts from " << source_directory.string() << " into "
             << destination_directory.string() << '\n';
    }
    return true;
}

bool WriteWorkspaceReadme(const std::filesystem::path& workspace_root, std::string* error) {
    const std::filesystem::path readme_path = workspace_root / "README.txt";
    std::ostringstream readme;
    readme << "refii Lua workspace\n\n"
           << "This folder is generated by the launcher installer.\n"
           << "It contains:\n"
           << "- mod-ready Lua files under the same paths the game expects, rooted at unpacked/\n"
           << "- a luasource/ cache with extracted script-bank payloads and copied loose scripts\n"
           << "- a hidden .baseline/ mirror and SHA-256 manifest for mod reset support\n\n"
           << "Important notes:\n"
           << "- The original game content remains intact under data/.\n"
           << "- The launcher prefers writing plaintext Lua into unpacked/ so the runtime can redirect there.\n"
           << "- gamescripts usually contains debug-info-rich versions of the same gameplay scripts found in gamescripts_r.\n"
           << "- gamescripts_r is the retail/runtime-oriented script bank.\n";
    return WriteTextFile(readme_path, readme.str(), error);
}

bool ExportLuaWorkspaceSources(const std::filesystem::path& game_install_root,
                               const std::filesystem::path& workspace_root,
                               ExtractionSummary* summary,
                               std::ostream* log,
                               std::string* error) {
    ExtractionSummary local_summary;
    ExtractionSummary* const active_summary = summary != nullptr ? summary : &local_summary;
    *active_summary = {};

    const std::filesystem::path game_content_directory =
        GetGameContentDirectoryPath(game_install_root);
    const std::filesystem::path source_root = workspace_root / "luasource";

    try {
        std::filesystem::create_directories(workspace_root);
        std::filesystem::create_directories(source_root);
    } catch (const std::exception& exception) {
        if (error != nullptr) {
            *error = exception.what();
        }
        return false;
    }

    refii::bnk::BankExtractionOptions bank_options;
    bank_options.game_content_root = game_content_directory;
    bank_options.output_root = source_root;
    bank_options.clean_output = true;
    for (const std::string_view bank_name : kScriptBankNames) {
        bank_options.bank_relative_paths.emplace_back(std::string(bank_name));
    }

    refii::bnk::BankExtractionSummary bank_summary;
    if (!refii::bnk::ExtractBanks(bank_options, &bank_summary, error)) {
        return false;
    }
    active_summary->bank_count = bank_summary.extracted_banks;
    active_summary->extracted_file_count = bank_summary.extracted_files;

    for (const std::string_view loose_directory_name : kLooseScriptDirectoryNames) {
        if (!CopyDirectoryTree(game_content_directory / loose_directory_name,
                               source_root / loose_directory_name,
                               active_summary,
                               log,
                               error)) {
            return false;
        }
    }

    if (!WriteWorkspaceReadme(workspace_root, error)) {
        return false;
    }

    if (log != nullptr) {
        *log << "Lua workspace ready at " << workspace_root.string() << " ("
             << active_summary->bank_count << " bank(s), "
             << active_summary->extracted_file_count << " extracted file(s), "
             << active_summary->copied_loose_file_count << " copied loose file(s))\n";
    }

    return true;
}

bool IsTrackedPublicLuaFile(const std::filesystem::path& workspace_root,
                            const std::filesystem::path& candidate_path) {
    std::error_code relative_error;
    const std::filesystem::path relative =
        std::filesystem::relative(candidate_path, workspace_root, relative_error);
    if (relative_error || relative.empty() || relative.is_absolute()) {
        return false;
    }

    if (candidate_path.extension() != ".lua") {
        return false;
    }

    const auto first_component_it = relative.begin();
    if (first_component_it == relative.end()) {
        return false;
    }

    const std::string first_component = first_component_it->generic_string();
    if (first_component.empty() || first_component[0] == '.' || first_component == "_disasm") {
        return false;
    }

    for (const auto& component : relative) {
        const std::string part = component.generic_string();
        if (part == "." || part == "..") {
            return false;
        }
    }

    return true;
}

bool IsDisassemblyPlaceholderLuaFile(const std::filesystem::path& path) {
    std::ifstream file(path, std::ios::binary);
    if (!file) {
        return false;
    }
    std::string first_line;
    std::getline(file, first_line);
    return first_line.find(kDisassemblyPlaceholderMarker) != std::string::npos;
}

std::vector<std::filesystem::path> EnumerateTrackedPublicLuaFiles(
    const std::filesystem::path& workspace_root,
    std::string* error) {
    std::vector<std::filesystem::path> results;
    std::error_code iterator_error;
    for (std::filesystem::recursive_directory_iterator it(workspace_root, iterator_error), end;
         it != end;
         it.increment(iterator_error)) {
        if (iterator_error) {
            if (error != nullptr) {
                *error = iterator_error.message();
            }
            return {};
        }
        if (!it->is_regular_file()) {
            continue;
        }
        const std::filesystem::path path = it->path();
        if (!IsTrackedPublicLuaFile(workspace_root, path)) {
            continue;
        }
        if (IsDisassemblyPlaceholderLuaFile(path)) {
            continue;
        }
        results.push_back(path);
    }

    std::sort(results.begin(), results.end());
    return results;
}

bool CopyFilePreservingTree(const std::filesystem::path& from,
                            const std::filesystem::path& to,
                            std::string* error) {
    if (!IsSafeRelativePath(to, error)) {
        return false;
    }
    try {
        std::filesystem::create_directories(to.parent_path());
        std::filesystem::copy_file(from, to, std::filesystem::copy_options::overwrite_existing);
    } catch (const std::exception& exception) {
        if (error != nullptr) {
            *error = exception.what();
        }
        return false;
    }
    return true;
}

bool WriteBaselineManifest(const std::filesystem::path& manifest_path,
                           const std::vector<BaselineEntry>& entries,
                           std::string* error) {
    std::ostringstream manifest;
    manifest << kBaselineHeader;
    for (const BaselineEntry& entry : entries) {
        manifest << entry.relative_path.generic_string() << '\t' << entry.sha256 << '\n';
    }
    return WriteTextFile(manifest_path, manifest.str(), error);
}

bool ReadBaselineManifest(const std::filesystem::path& manifest_path,
                          std::vector<BaselineEntry>* entries,
                          std::string* error) {
    if (entries == nullptr) {
        if (error != nullptr) {
            *error = "baseline output buffer missing";
        }
        return false;
    }

    std::string text;
    if (!ReadTextFile(manifest_path, &text, error)) {
        return false;
    }

    entries->clear();
    std::istringstream stream(text);
    std::string line;
    bool first_line = true;
    while (std::getline(stream, line)) {
        if (first_line) {
            first_line = false;
            continue;
        }
        if (line.empty()) {
            continue;
        }
        const std::size_t separator = line.find('\t');
        if (separator == std::string::npos) {
            if (error != nullptr) {
                *error = "baseline manifest row is missing a tab separator";
            }
            return false;
        }
        BaselineEntry entry;
        entry.relative_path = std::filesystem::path(line.substr(0, separator));
        entry.sha256 = line.substr(separator + 1);
        entries->push_back(std::move(entry));
    }

    return true;
}

std::unordered_map<std::string, std::string> BuildActualHashMap(
    const std::filesystem::path& workspace_root,
    std::vector<std::filesystem::path>* tracked_files,
    std::string* error) {
    std::unordered_map<std::string, std::string> hashes;
    const std::vector<std::filesystem::path> files = EnumerateTrackedPublicLuaFiles(workspace_root, error);
    if (error != nullptr && !error->empty()) {
        return hashes;
    }

    if (tracked_files != nullptr) {
        *tracked_files = files;
    }

    for (const std::filesystem::path& file : files) {
        std::string text;
        if (!ReadTextFile(file, &text, error)) {
            hashes.clear();
            return hashes;
        }
        const std::filesystem::path relative = std::filesystem::relative(file, workspace_root);
        hashes.emplace(NormalizeRelativePathString(relative), crypto::ComputeSha256Hex(text));
    }

    return hashes;
}

bool SnapshotWorkspaceBaseline(const std::filesystem::path& workspace_root,
                               const std::function<void(const LuaWorkspaceDecompileProgress&)>& progress_callback,
                               std::size_t* baseline_file_count,
                               std::string* error) {
    const std::filesystem::path baseline_root = GetLuaWorkspaceBaselineRoot(workspace_root);
    const std::filesystem::path baseline_files_root = baseline_root / "files";
    const std::filesystem::path baseline_manifest_path = baseline_root / "_hashes.tsv";

    std::error_code remove_error;
    std::filesystem::remove_all(baseline_files_root, remove_error);
    if (remove_error) {
        if (error != nullptr) {
            *error = remove_error.message();
        }
        return false;
    }

    const std::vector<std::filesystem::path> tracked_files =
        EnumerateTrackedPublicLuaFiles(workspace_root, error);
    if (error != nullptr && !error->empty()) {
        return false;
    }

    std::vector<BaselineEntry> entries;
    entries.reserve(tracked_files.size());
    EmitProgress(progress_callback, "snapshotting_baseline", 0, tracked_files.size());

    for (std::size_t index = 0; index < tracked_files.size(); ++index) {
        const std::filesystem::path& file = tracked_files[index];
        const std::filesystem::path relative = std::filesystem::relative(file, workspace_root);
        const std::filesystem::path baseline_file = baseline_files_root / relative;
        if (!CopyFilePreservingTree(file, baseline_file, error)) {
            return false;
        }

        std::string text;
        if (!ReadTextFile(file, &text, error)) {
            return false;
        }

        BaselineEntry entry;
        entry.relative_path = relative.generic_string();
        entry.sha256 = crypto::ComputeSha256Hex(text);
        entries.push_back(std::move(entry));

        EmitProgress(progress_callback,
                     "snapshotting_baseline",
                     index + 1,
                     tracked_files.size(),
                     relative);
    }

    std::sort(entries.begin(), entries.end(), [](const BaselineEntry& lhs, const BaselineEntry& rhs) {
        return lhs.relative_path.generic_string() < rhs.relative_path.generic_string();
    });

    if (!WriteBaselineManifest(baseline_manifest_path, entries, error)) {
        return false;
    }

    if (baseline_file_count != nullptr) {
        *baseline_file_count = entries.size();
    }
    return true;
}

std::string DescribeHashMismatch(const std::string& expected_hash, const std::string& actual_hash) {
    if (expected_hash.empty() || actual_hash.empty()) {
        return {};
    }
    return expected_hash == actual_hash ? "ok" : "modified";
}

bool NormalizeResetRelativePath(const std::filesystem::path& relative_path,
                                std::filesystem::path* normalized,
                                std::string* error) {
    if (normalized == nullptr) {
        if (error != nullptr) {
            *error = "normalized output path missing";
        }
        return false;
    }
    if (relative_path.empty()) {
        *normalized = std::filesystem::path();
        return true;
    }

    const std::filesystem::path candidate = relative_path.lexically_normal();
    if (candidate.empty() || candidate.is_absolute()) {
        if (error != nullptr) {
            *error = "reset path must be a relative workspace path";
        }
        return false;
    }

    for (const auto& component : candidate) {
        if (component == "..") {
            if (error != nullptr) {
                *error = "reset path may not escape the workspace";
            }
            return false;
        }
    }

    *normalized = candidate;
    return true;
}

}  // namespace

std::filesystem::path GetLuaWorkspacePath(const std::filesystem::path& game_install_root) {
    return GetWorkspaceBasePath(game_install_root) / "unpacked";
}

std::filesystem::path GetLuaWorkspaceSourcePath(const std::filesystem::path& game_install_root) {
    return GetLuaWorkspacePath(game_install_root) / "luasource";
}

std::filesystem::path GetLuaWorkspaceBaselineRoot(const std::filesystem::path& workspace_root) {
    return workspace_root / ".baseline";
}

const char* DescribeLuaWorkspaceVerifyStatus(LuaWorkspaceVerifyStatus status) {
    switch (status) {
    case LuaWorkspaceVerifyStatus::kOk:
        return "ok";
    case LuaWorkspaceVerifyStatus::kModified:
        return "modified";
    case LuaWorkspaceVerifyStatus::kMissing:
        return "missing";
    case LuaWorkspaceVerifyStatus::kExtra:
        return "extra";
    case LuaWorkspaceVerifyStatus::kBaselineMissing:
        return "baseline_missing";
    case LuaWorkspaceVerifyStatus::kBaselineCorrupt:
        return "baseline_corrupt";
    }
    return "unknown";
}

bool BuildLuaWorkspace(const LuaWorkspaceBuildOptions& options,
                       LuaWorkspaceBuildSummary* summary,
                       std::string* error) {
    LuaWorkspaceBuildSummary local_summary;
    LuaWorkspaceBuildSummary* const active_summary = summary != nullptr ? summary : &local_summary;
    *active_summary = {};

    const std::filesystem::path game_install_root =
        std::filesystem::absolute(options.game_install_root);
    const std::filesystem::path workspace_root =
        options.workspace_root.empty() ? GetLuaWorkspacePath(game_install_root)
                                       : std::filesystem::absolute(options.workspace_root);
    const std::filesystem::path source_root = workspace_root / "luasource";

    active_summary->game_install_root = game_install_root;
    active_summary->workspace_root = workspace_root;
    active_summary->source_root = source_root;
    active_summary->output_root = GetDefaultLuaDecompiledOutputRoot(workspace_root);
    active_summary->baseline_root = GetLuaWorkspaceBaselineRoot(active_summary->output_root);
    active_summary->baseline_manifest_path = active_summary->baseline_root / "_hashes.tsv";

    EmitProgress(options.progress_callback, "exporting_workspace");
    ExtractionSummary extraction_summary;
    if (!ExportLuaWorkspaceSources(
            game_install_root, workspace_root, &extraction_summary, nullptr, error)) {
        return false;
    }

    active_summary->extracted_banks = extraction_summary.bank_count;
    active_summary->extracted_files = extraction_summary.extracted_file_count;
    active_summary->copied_loose_files = extraction_summary.copied_loose_file_count;

    LuaWorkspaceDecompileOptions decompile_options;
    decompile_options.workspace_root = workspace_root;
    decompile_options.output_root = active_summary->output_root;
    decompile_options.worker_count = options.worker_count;
    decompile_options.progress_update_stride = options.progress_update_stride;
    decompile_options.child_timeout_seconds = options.child_timeout_seconds;
    decompile_options.native_render_timeout_seconds = options.native_render_timeout_seconds;
    decompile_options.source_wall_time_budget_ms = options.source_wall_time_budget_ms;
    decompile_options.max_phase2_heavy_exactification_jobs =
        options.max_phase2_heavy_exactification_jobs;
    decompile_options.write_disassembly_fallback = options.write_disassembly_fallback;
    decompile_options.progress_callback = options.progress_callback;

    if (!DecompileLuaWorkspace(decompile_options, &active_summary->decompile_summary, error)) {
        return false;
    }

    if (!SnapshotWorkspaceBaseline(active_summary->output_root,
                                   options.progress_callback,
                                   &active_summary->baseline_files,
                                   error)) {
        return false;
    }

    EmitProgress(options.progress_callback, "done");
    return true;
}

bool VerifyLuaWorkspace(const LuaWorkspaceVerifyOptions& options,
                        LuaWorkspaceVerifySummary* summary,
                        std::string* error) {
    LuaWorkspaceVerifySummary local_summary;
    LuaWorkspaceVerifySummary* const active_summary = summary != nullptr ? summary : &local_summary;
    *active_summary = {};

    const std::filesystem::path workspace_root = std::filesystem::absolute(options.workspace_root);
    const std::filesystem::path baseline_root = GetLuaWorkspaceBaselineRoot(workspace_root);
    const std::filesystem::path manifest_path = baseline_root / "_hashes.tsv";
    active_summary->workspace_root = workspace_root;
    active_summary->baseline_root = baseline_root;
    active_summary->baseline_manifest_path = manifest_path;

    if (!std::filesystem::exists(workspace_root) || !std::filesystem::is_directory(workspace_root)) {
        if (error != nullptr) {
            *error = "workspace not found: " + workspace_root.string();
        }
        return false;
    }

    if (!std::filesystem::exists(manifest_path)) {
        LuaWorkspaceVerifyEntry entry;
        entry.status = LuaWorkspaceVerifyStatus::kBaselineMissing;
        active_summary->entries.push_back(entry);
        active_summary->baseline_missing = 1;
        return true;
    }

    std::vector<BaselineEntry> baseline_entries;
    std::string manifest_error;
    if (!ReadBaselineManifest(manifest_path, &baseline_entries, &manifest_error)) {
        LuaWorkspaceVerifyEntry entry;
        entry.status = LuaWorkspaceVerifyStatus::kBaselineCorrupt;
        active_summary->entries.push_back(entry);
        active_summary->baseline_corrupt = 1;
        if (error != nullptr) {
            *error = manifest_error;
        }
        return true;
    }

    active_summary->tracked_files = baseline_entries.size();

    EmitProgress(options.progress_callback, "verifying_workspace", 0, baseline_entries.size());

    std::vector<std::filesystem::path> actual_files;
    std::string actual_error;
    const std::unordered_map<std::string, std::string> actual_hashes =
        BuildActualHashMap(workspace_root, &actual_files, &actual_error);
    if (!actual_error.empty()) {
        if (error != nullptr) {
            *error = actual_error;
        }
        return false;
    }

    std::unordered_map<std::string, BaselineEntry> baseline_map;
    baseline_map.reserve(baseline_entries.size());
    for (const BaselineEntry& entry : baseline_entries) {
        baseline_map.emplace(NormalizeRelativePathString(entry.relative_path), entry);
    }

    for (std::size_t index = 0; index < baseline_entries.size(); ++index) {
        const BaselineEntry& entry = baseline_entries[index];
        const std::string key = NormalizeRelativePathString(entry.relative_path);
        LuaWorkspaceVerifyEntry verify_entry;
        verify_entry.relative_path = entry.relative_path;
        verify_entry.expected_hash = entry.sha256;

        const std::filesystem::path baseline_file = baseline_root / "files" / entry.relative_path;
        if (!std::filesystem::exists(baseline_file)) {
            verify_entry.status = LuaWorkspaceVerifyStatus::kBaselineCorrupt;
            ++active_summary->baseline_corrupt;
        } else {
            const auto actual_it = actual_hashes.find(key);
            if (actual_it == actual_hashes.end()) {
                verify_entry.status = LuaWorkspaceVerifyStatus::kMissing;
                ++active_summary->missing;
            } else {
                verify_entry.actual_hash = actual_it->second;
                if (actual_it->second == entry.sha256) {
                    verify_entry.status = LuaWorkspaceVerifyStatus::kOk;
                    ++active_summary->ok;
                } else {
                    verify_entry.status = LuaWorkspaceVerifyStatus::kModified;
                    ++active_summary->modified;
                }
            }
        }

        if (options.include_ok_entries || verify_entry.status != LuaWorkspaceVerifyStatus::kOk) {
            active_summary->entries.push_back(std::move(verify_entry));
        }

        EmitProgress(options.progress_callback,
                     "verifying_workspace",
                     index + 1,
                     baseline_entries.size(),
                     entry.relative_path);
    }

    for (const auto& [relative_text, actual_hash] : actual_hashes) {
        if (baseline_map.find(relative_text) != baseline_map.end()) {
            continue;
        }
        LuaWorkspaceVerifyEntry entry;
        entry.relative_path = std::filesystem::path(relative_text);
        entry.actual_hash = actual_hash;
        entry.status = LuaWorkspaceVerifyStatus::kExtra;
        ++active_summary->extra;
        active_summary->entries.push_back(std::move(entry));
    }

    std::sort(active_summary->entries.begin(),
              active_summary->entries.end(),
              [](const LuaWorkspaceVerifyEntry& lhs, const LuaWorkspaceVerifyEntry& rhs) {
                  if (lhs.status != rhs.status) {
                      return static_cast<int>(lhs.status) < static_cast<int>(rhs.status);
                  }
                  return lhs.relative_path.generic_string() < rhs.relative_path.generic_string();
              });

    return true;
}

bool ResetLuaWorkspace(const LuaWorkspaceResetOptions& options,
                       LuaWorkspaceResetSummary* summary,
                       std::string* error) {
    LuaWorkspaceResetSummary local_summary;
    LuaWorkspaceResetSummary* const active_summary = summary != nullptr ? summary : &local_summary;
    *active_summary = {};

    const std::filesystem::path workspace_root = std::filesystem::absolute(options.workspace_root);
    const std::filesystem::path baseline_root = GetLuaWorkspaceBaselineRoot(workspace_root);
    const std::filesystem::path baseline_manifest_path = baseline_root / "_hashes.tsv";
    active_summary->workspace_root = workspace_root;
    active_summary->baseline_root = baseline_root;

    std::vector<BaselineEntry> baseline_entries;
    if (!ReadBaselineManifest(baseline_manifest_path, &baseline_entries, error)) {
        if (error != nullptr && error->empty()) {
            *error = "baseline manifest not found: " + baseline_manifest_path.string();
        }
        return false;
    }

    std::unordered_map<std::string, BaselineEntry> baseline_map;
    baseline_map.reserve(baseline_entries.size());
    for (const BaselineEntry& entry : baseline_entries) {
        baseline_map.emplace(NormalizeRelativePathString(entry.relative_path), entry);
    }

    std::filesystem::path normalized_relative_path;
    if (!NormalizeResetRelativePath(options.relative_path, &normalized_relative_path, error)) {
        return false;
    }

    std::vector<std::filesystem::path> restore_targets;
    std::vector<std::filesystem::path> extra_targets;
    if (!options.reset_all && !normalized_relative_path.empty()) {
        restore_targets.push_back(normalized_relative_path);
    } else {
        restore_targets.reserve(baseline_entries.size());
        for (const BaselineEntry& entry : baseline_entries) {
            restore_targets.push_back(entry.relative_path);
        }

        std::vector<std::filesystem::path> actual_files =
            EnumerateTrackedPublicLuaFiles(workspace_root, error);
        if (error != nullptr && !error->empty()) {
            return false;
        }
        for (const std::filesystem::path& file : actual_files) {
            const std::filesystem::path relative = std::filesystem::relative(file, workspace_root);
            const std::string key = NormalizeRelativePathString(relative);
            if (baseline_map.find(key) == baseline_map.end()) {
                extra_targets.push_back(relative);
            }
        }
    }

    const std::size_t total_targets = restore_targets.size() + extra_targets.size();
    EmitProgress(options.progress_callback, "resetting_workspace", 0, total_targets);

    std::size_t progress_index = 0;
    for (std::size_t index = 0; index < restore_targets.size(); ++index) {
        const std::filesystem::path& relative_path = restore_targets[index];
        const std::string key = NormalizeRelativePathString(relative_path);
        const auto baseline_it = baseline_map.find(key);
        const std::filesystem::path live_path = workspace_root / relative_path;

        if (baseline_it == baseline_map.end()) {
            std::error_code remove_error;
            const bool removed = std::filesystem::remove(live_path, remove_error);
            if (removed) {
                ++active_summary->removed;
            } else if (remove_error) {
                ++active_summary->failed;
                if (error != nullptr && error->empty()) {
                    *error = "failed to remove " + live_path.string() + ": " + remove_error.message();
                }
            }
        } else {
            const std::filesystem::path baseline_file = baseline_root / "files" / relative_path;
            if (!std::filesystem::exists(baseline_file)) {
                ++active_summary->failed;
                if (error != nullptr && error->empty()) {
                    *error = "baseline file missing: " + baseline_file.string();
                }
            } else if (!CopyFilePreservingTree(baseline_file, live_path, error)) {
                ++active_summary->failed;
            } else {
                ++active_summary->restored;
            }
        }

        EmitProgress(options.progress_callback,
                     "resetting_workspace",
                     ++progress_index,
                     total_targets,
                     relative_path);
    }

    for (const std::filesystem::path& relative_path : extra_targets) {
        const std::filesystem::path live_path = workspace_root / relative_path;
        std::error_code remove_error;
        const bool removed = std::filesystem::remove(live_path, remove_error);
        if (removed) {
            ++active_summary->removed;
        } else if (remove_error) {
            ++active_summary->failed;
            if (error != nullptr && error->empty()) {
                *error = "failed to remove " + live_path.string() + ": " + remove_error.message();
            }
        }

        EmitProgress(options.progress_callback,
                     "resetting_workspace",
                     ++progress_index,
                     total_targets,
                     relative_path);
    }

    return active_summary->failed == 0;
}

}  // namespace refii::lua
