#include "tools/lua_bytecode_parity_audit.h"

#include "tools/lua_bytecode_oracles.h"
#include "tools/lua_decompiler_internal.h"

#include <algorithm>
#include <array>
#include <cstdlib>
#include <fstream>
#include <limits>
#include <sstream>
#include <unordered_map>
#include <vector>

namespace refii::lua::audit {

namespace {

struct ManifestAuditRow {
    std::string status;
    std::string note;
};

bool LoadManifestAuditRows(
    const std::filesystem::path& manifest_path,
    std::unordered_map<std::string, ManifestAuditRow>* rows) {
    if (rows == nullptr) {
        return false;
    }

    rows->clear();
    std::ifstream stream(manifest_path, std::ios::binary);
    if (!stream) {
        return false;
    }

    std::string line;
    if (!std::getline(stream, line)) {
        return false;
    }
    while (std::getline(stream, line)) {
        std::vector<std::string> columns;
        std::size_t start = 0;
        while (start <= line.size()) {
            const std::size_t tab = line.find('\t', start);
            if (tab == std::string::npos) {
                columns.push_back(line.substr(start));
                break;
            }
            columns.push_back(line.substr(start, tab - start));
            start = tab + 1;
        }
        if (columns.size() < 5 || columns[2].empty()) {
            continue;
        }
        rows->emplace(internal::NormalizePathString(columns[2]),
                      ManifestAuditRow{std::move(columns[0]), std::move(columns[4])});
    }
    return true;
}

std::string BoolString(bool value) {
    return value ? "true" : "false";
}

struct OriginalBytecodeInfo {
    bool available = false;
    bool is_bytecode = false;
    std::filesystem::path absolute_path;
    std::filesystem::path relative_path;
    std::vector<char> bytes;
    std::string source_name;
    std::string note;
    refii::lua::Lua51Chunk chunk;
    refii::lua::Lua51ChunkStats full_stats;
    Lua51FunctionStats main_stats;
    std::uintmax_t byte_size = 0;
    LuaBytecodeParityReference parity_reference;
};

struct CandidateScore {
    int tier = 3;
    std::array<long long, 7> deltas = {
        std::numeric_limits<long long>::max(),
        std::numeric_limits<long long>::max(),
        std::numeric_limits<long long>::max(),
        std::numeric_limits<long long>::max(),
        std::numeric_limits<long long>::max(),
        std::numeric_limits<long long>::max(),
        std::numeric_limits<long long>::max(),
    };
};

bool CandidateScoreBetter(const CandidateScore& lhs,
                          int lhs_tiebreak,
                          const CandidateScore& rhs,
                          int rhs_tiebreak) {
    if (lhs.tier != rhs.tier) {
        return lhs.tier < rhs.tier;
    }
    if (lhs.deltas != rhs.deltas) {
        return lhs.deltas < rhs.deltas;
    }
    return lhs_tiebreak < rhs_tiebreak;
}

CandidateScore BuildCandidateScore(const OriginalBytecodeInfo& original,
                                   const CompiledLuaChunkInfo& candidate,
                                   bool main_exact,
                                   bool full_exact) {
    CandidateScore score;
    if (!original.available || !original.is_bytecode || !candidate.syntax_valid ||
        !candidate.chunk_parsed) {
        return score;
    }

    score.tier = full_exact ? 0 : (main_exact ? 1 : 2);
    score.deltas = {
        std::llabs(static_cast<long long>(candidate.main_stats.instruction_count) -
                   static_cast<long long>(original.main_stats.instruction_count)),
        std::llabs(static_cast<long long>(candidate.main_stats.constant_count) -
                   static_cast<long long>(original.main_stats.constant_count)),
        std::llabs(static_cast<long long>(candidate.full_stats.function_count) -
                   static_cast<long long>(original.full_stats.function_count)),
        std::llabs(static_cast<long long>(candidate.full_stats.instruction_count) -
                   static_cast<long long>(original.full_stats.instruction_count)),
        std::llabs(static_cast<long long>(candidate.full_stats.constant_count) -
                   static_cast<long long>(original.full_stats.constant_count)),
        std::llabs(static_cast<long long>(candidate.full_stats.local_count) -
                   static_cast<long long>(original.full_stats.local_count)),
        std::llabs(static_cast<long long>(candidate.byte_size) -
                   static_cast<long long>(original.byte_size)),
    };
    return score;
}

std::string DescribeScoreTuple(const CandidateScore& score) {
    if (score.tier >= 3) {
        return "unavailable";
    }
    std::ostringstream stream;
    stream << "tier=" << score.tier;
    stream << ";main_instructions_delta=" << score.deltas[0];
    stream << ";main_constants_delta=" << score.deltas[1];
    stream << ";functions_delta=" << score.deltas[2];
    stream << ";instructions_delta=" << score.deltas[3];
    stream << ";constants_delta=" << score.deltas[4];
    stream << ";locals_delta=" << score.deltas[5];
    stream << ";chunk_bytes_delta=" << score.deltas[6];
    return stream.str();
}

struct CandidateAuditInfo {
    std::string name;
    std::string source;
    std::string note;
    CompiledLuaChunkInfo compiled;
    bool main_exact = false;
    bool full_exact = false;
    FunctionMismatchInfo first_mismatch;
    CandidateScore score;
};

void EvaluateCandidateAgainstOriginal(const OriginalBytecodeInfo& original,
                                      CandidateAuditInfo* candidate) {
    if (candidate == nullptr) {
        return;
    }

    LuaBytecodeParityResult parity_result;
    EvaluateCompiledLuaChunkBytecodeParity(original.parity_reference,
                                           candidate->compiled,
                                           &parity_result);
    candidate->main_exact = parity_result.main_stats_exact;
    candidate->full_exact = parity_result.full_stats_exact;
    candidate->score =
        BuildCandidateScore(original, candidate->compiled, candidate->main_exact, candidate->full_exact);
    candidate->first_mismatch = parity_result.first_mismatch;
}

std::string SafeRelativePathString(const std::filesystem::path& path,
                                   const std::filesystem::path& root) {
    std::error_code error;
    const std::filesystem::path relative = std::filesystem::relative(path, root, error);
    if (!error && !relative.empty() && relative.native() != ".") {
        return relative.generic_string();
    }
    return path.generic_string();
}

struct ParitySummaryRow {
    std::filesystem::path output_relative;
    std::filesystem::path source_relative;
    std::string manifest_status;
    std::string manifest_note;
    std::string current_note;
    std::string unluac_note;
    std::string original_main_stats;
    std::string original_full_stats;
    std::string current_main_stats;
    std::string current_full_stats;
    std::string unluac_main_stats;
    std::string unluac_full_stats;
    bool source_is_bytecode = false;
    bool current_syntax_valid = false;
    bool current_main_exact = false;
    bool current_full_exact = false;
    bool unluac_syntax_valid = false;
    bool unluac_main_exact = false;
    bool unluac_full_exact = false;
    bool has_failure = false;
    std::string winner;
    std::string recommended_action;
    FunctionMismatchInfo first_mismatch;
    CandidateScore current_score;
    CandidateScore unluac_score;
    std::filesystem::path artifact_relative;
};

void WriteParityRow(std::ofstream& stream, const ParitySummaryRow& row) {
    stream << row.output_relative.generic_string() << '\t'
           << row.source_relative.generic_string() << '\t' << row.manifest_status << '\t'
           << row.manifest_note << '\t' << BoolString(row.source_is_bytecode) << '\t'
           << BoolString(row.current_syntax_valid) << '\t' << BoolString(row.current_main_exact)
           << '\t' << BoolString(row.current_full_exact) << '\t'
           << BoolString(row.unluac_syntax_valid) << '\t' << BoolString(row.unluac_main_exact)
           << '\t' << BoolString(row.unluac_full_exact) << '\t' << row.original_main_stats
           << '\t' << row.original_full_stats << '\t' << row.current_main_stats << '\t'
           << row.current_full_stats << '\t' << row.unluac_main_stats << '\t'
           << row.unluac_full_stats << '\t' << row.winner << '\t' << row.recommended_action
           << '\t';
    if (row.first_mismatch.available) {
        stream << row.first_mismatch.function_index << '\t' << row.first_mismatch.line_defined
               << '\t' << DescribeMismatchForSummary(row.first_mismatch);
    } else {
        stream << "\t\t";
    }
    stream << '\t' << row.current_note << '\t' << row.unluac_note << '\t'
           << row.artifact_relative.generic_string() << '\n';
}

bool WriteArtifactBundle(const std::filesystem::path& report_root,
                         const std::filesystem::path& output_relative,
                         const ParitySummaryRow& row,
                         std::string_view current_source,
                         std::string_view unluac_source,
                         std::filesystem::path* artifact_relative) {
    if (artifact_relative == nullptr) {
        return false;
    }

    const std::filesystem::path bundle_relative =
        std::filesystem::path("artifacts") / output_relative.parent_path() / output_relative.stem();
    const std::filesystem::path bundle_root = report_root / bundle_relative;
    std::filesystem::create_directories(bundle_root);

    bool wrote_any = false;
    if (!current_source.empty() && internal::WriteTextFile(bundle_root / "current.lua", current_source)) {
        wrote_any = true;
    }
    if (!unluac_source.empty() && internal::WriteTextFile(bundle_root / "unluac.lua", unluac_source)) {
        wrote_any = true;
    }

    std::ostringstream note_stream;
    note_stream << "winner=" << row.winner << "\n";
    note_stream << "recommended_action=" << row.recommended_action << "\n";
    note_stream << "first_mismatch=" << DescribeMismatchForSummary(row.first_mismatch) << "\n";
    note_stream << "current_score=" << DescribeScoreTuple(row.current_score) << "\n";
    note_stream << "unluac_score=" << DescribeScoreTuple(row.unluac_score) << "\n";
    note_stream << "current_note=" << row.current_note << "\n";
    note_stream << "unluac_note=" << row.unluac_note << "\n";
    note_stream << "original_main_stats=" << row.original_main_stats << "\n";
    note_stream << "original_full_stats=" << row.original_full_stats << "\n";
    note_stream << "current_main_stats=" << row.current_main_stats << "\n";
    note_stream << "current_full_stats=" << row.current_full_stats << "\n";
    note_stream << "unluac_main_stats=" << row.unluac_main_stats << "\n";
    note_stream << "unluac_full_stats=" << row.unluac_full_stats << "\n";

    if (internal::WriteTextFile(bundle_root / "note.txt", note_stream.str())) {
        wrote_any = true;
    }

    if (!wrote_any) {
        return false;
    }

    *artifact_relative = bundle_relative;
    return true;
}

bool CurrentSeverityWorse(const ParitySummaryRow& lhs, const ParitySummaryRow& rhs) {
    if (lhs.current_score.tier != rhs.current_score.tier) {
        return lhs.current_score.tier > rhs.current_score.tier;
    }
    if (lhs.current_score.deltas != rhs.current_score.deltas) {
        return lhs.current_score.deltas > rhs.current_score.deltas;
    }
    return internal::NormalizePathString(lhs.output_relative.generic_string()) <
           internal::NormalizePathString(rhs.output_relative.generic_string());
}

std::string BuildSummaryLine(const ParitySummaryRow& row) {
    std::ostringstream stream;
    stream << "`" << row.output_relative.generic_string() << "`";
    if (row.source_is_bytecode) {
        stream << " winner=`" << row.winner << "`";
        stream << " action=`" << row.recommended_action << "`";
        stream << " mismatch=`" << DescribeMismatchForSummary(row.first_mismatch) << "`";
        stream << " current=`" << DescribeScoreTuple(row.current_score) << "`";
        if (!row.artifact_relative.empty()) {
            stream << " artifacts=`" << row.artifact_relative.generic_string() << "`";
        }
    } else {
        stream << " source=`non-bytecode`";
    }
    return stream.str();
}

bool WriteParitySummaryReport(const std::filesystem::path& summary_path,
                              const std::vector<ParitySummaryRow>& rows,
                              const refii::lua::LuaBytecodeParityAuditSummary& summary) {
    std::vector<ParitySummaryRow> current_best_rows;
    std::vector<ParitySummaryRow> unluac_best_rows;
    std::vector<ParitySummaryRow> no_candidate_exact_rows;
    std::vector<ParitySummaryRow> failure_rows;

    for (const ParitySummaryRow& row : rows) {
        if (row.source_is_bytecode && row.winner == "current" && !row.current_full_exact) {
            current_best_rows.push_back(row);
        }
        if (row.source_is_bytecode && row.winner == "unluac") {
            unluac_best_rows.push_back(row);
        }
        if (row.source_is_bytecode && !row.current_full_exact && !row.unluac_full_exact) {
            no_candidate_exact_rows.push_back(row);
        }
        if (row.has_failure) {
            failure_rows.push_back(row);
        }
    }

    auto sort_rows = [](std::vector<ParitySummaryRow>* values) {
        std::sort(values->begin(), values->end(), CurrentSeverityWorse);
    };
    sort_rows(&current_best_rows);
    sort_rows(&unluac_best_rows);
    sort_rows(&no_candidate_exact_rows);
    sort_rows(&failure_rows);

    std::ostringstream stream;
    stream << "# Lua Bytecode Parity Audit\n\n";
    stream << "- Files scanned: " << summary.total_files << "\n";
    stream << "- Exact current matches: " << summary.exact_matches << "\n";
    stream << "- Current best but inexact: " << summary.current_best << "\n";
    stream << "- Unluac wins: " << summary.unluac_best << "\n";
    stream << "- No candidate exact: " << summary.no_candidate_exact << "\n";
    stream << "- Compile/parse failures: " << summary.compile_or_parse_failures << "\n";
    stream << "- Non-bytecode emitted files: " << summary.non_bytecode_sources << "\n\n";
    stream << "## Exact matches\n\n";
    stream << "- Count: " << summary.exact_matches << "\n\n";

    auto append_section = [&](std::string_view title, const std::vector<ParitySummaryRow>& section_rows) {
        stream << "## " << title << "\n\n";
        if (section_rows.empty()) {
            stream << "- None\n\n";
            return;
        }
        for (const ParitySummaryRow& row : section_rows) {
            stream << "- " << BuildSummaryLine(row) << "\n";
        }
        stream << "\n";
    };

    append_section("Current-best-but-not-exact", current_best_rows);
    append_section("Unluac-wins", unluac_best_rows);
    append_section("No candidate exact", no_candidate_exact_rows);
    append_section("Compile/parse failures", failure_rows);

    return internal::WriteTextFile(summary_path, stream.str());
}

}  // namespace

bool RunLuaBytecodeParityAudit(const LuaBytecodeParityAuditOptions& raw_options,
                               LuaBytecodeParityAuditSummary* summary,
                               std::string* error) {
    try {
        LuaBytecodeParityAuditOptions options = raw_options;
        options.workspace_root =
            std::filesystem::absolute(GetDefaultLuaDecompileInputRoot(raw_options.workspace_root));
        if (options.generated_root.empty()) {
            options.generated_root = GetDefaultLuaDecompiledOutputRoot(options.workspace_root);
        } else {
            options.generated_root = std::filesystem::absolute(options.generated_root);
        }
        if (options.report_root.empty()) {
            options.report_root = options.generated_root / "_parity";
        } else {
            options.report_root = std::filesystem::absolute(options.report_root);
        }
        if (!options.single_tool_path.empty()) {
            options.single_tool_path = std::filesystem::absolute(options.single_tool_path);
        }
        if (!options.oracle_unluac_path.empty()) {
            options.oracle_unluac_path = std::filesystem::absolute(options.oracle_unluac_path);
        }
        if (options.progress_update_stride == 0) {
            options.progress_update_stride = 1;
        }

        if (!std::filesystem::is_directory(options.workspace_root)) {
            if (error != nullptr) {
                *error = "workspace not found: " + options.workspace_root.string();
            }
            return false;
        }
        if (!std::filesystem::is_directory(options.generated_root)) {
            if (error != nullptr) {
                *error = "generated root not found: " + options.generated_root.string();
            }
            return false;
        }
        if (!options.oracle_unluac_path.empty() &&
            !std::filesystem::exists(options.oracle_unluac_path)) {
            if (error != nullptr) {
                *error = "oracle unluac path not found: " + options.oracle_unluac_path.string();
            }
            return false;
        }

        std::filesystem::create_directories(options.report_root);
        const std::string ignored_report_prefix =
            internal::BuildIgnoredOutputPrefix(options.generated_root, options.report_root);

        std::vector<std::filesystem::path> relative_outputs;
        for (std::filesystem::recursive_directory_iterator it(options.generated_root), end; it != end;
             ++it) {
            if (!it->is_regular_file() || it->path().extension() != ".lua") {
                continue;
            }

            const std::filesystem::path relative =
                std::filesystem::relative(it->path(), options.generated_root);
            const std::string normalized = internal::NormalizePathString(relative.generic_string());
            if (internal::StartsWith(normalized, ".source/") ||
                internal::StartsWith(normalized, "_failed/") ||
                internal::StartsWith(normalized, "_disasm/") ||
                internal::StartsWith(normalized, ".cache/")) {
                continue;
            }
            if (!ignored_report_prefix.empty() &&
                internal::StartsWith(normalized, ignored_report_prefix)) {
                continue;
            }
            relative_outputs.push_back(relative);
        }

        std::sort(relative_outputs.begin(), relative_outputs.end(),
                  [](const std::filesystem::path& lhs, const std::filesystem::path& rhs) {
                      const int lhs_priority = internal::GetInputPriority(lhs);
                      const int rhs_priority = internal::GetInputPriority(rhs);
                      if (lhs_priority != rhs_priority) {
                          return lhs_priority < rhs_priority;
                      }
                      return internal::NormalizePathString(lhs.generic_string()) <
                             internal::NormalizePathString(rhs.generic_string());
                  });

        std::unordered_map<std::string, ManifestAuditRow> manifest_rows;
        LoadManifestAuditRows(options.generated_root / "_manifest.tsv", &manifest_rows);

        const std::filesystem::path parity_report_path = options.report_root / "_parity.tsv";
        const std::filesystem::path summary_report_path =
            options.report_root / "_parity_summary.md";
        std::ofstream parity_stream(parity_report_path, std::ios::binary | std::ios::trunc);
        if (!parity_stream) {
            if (error != nullptr) {
                *error = "failed to open parity report: " + parity_report_path.string();
            }
            return false;
        }
        parity_stream
            << "output_path\tsource_bytecode_path\tmanifest_status\tmanifest_note\t"
               "source_is_bytecode\tsyntax_valid\tmain_exact\tfull_exact\t"
               "unluac_syntax_valid\tunluac_main_exact\tunluac_full_exact\t"
               "original_main_stats\toriginal_full_stats\tcurrent_main_stats\tcurrent_full_stats\t"
               "unluac_main_stats\tunluac_full_stats\twinner\trecommended_action\t"
               "first_mismatch_function_index\tfirst_mismatch_line\tfirst_mismatch_note\t"
               "current_note\tunluac_note\tartifact_dir\n";

        LuaBytecodeParityAuditSummary local_summary;
        local_summary.workspace_root = options.workspace_root;
        local_summary.generated_root = options.generated_root;
        local_summary.report_root = options.report_root;
        local_summary.parity_report_path = parity_report_path;
        local_summary.summary_report_path = summary_report_path;
        local_summary.total_files = relative_outputs.size();

        auto emit_progress = [&](std::size_t completed,
                                 const std::filesystem::path& current_input,
                                 const char* phase,
                                 bool force) {
            if (!options.progress_callback) {
                return;
            }
            if (!force && options.progress_update_stride > 1 &&
                (completed % options.progress_update_stride) != 0) {
                return;
            }

            LuaWorkspaceDecompileProgress progress;
            progress.completed_files = completed;
            progress.total_files = local_summary.total_files;
            progress.current_input = current_input;
            progress.phase = phase;
            options.progress_callback(progress);
        };

        emit_progress(0, {}, "starting", true);

        std::vector<ParitySummaryRow> summary_rows;
        summary_rows.reserve(relative_outputs.size());
        std::size_t processed_files = 0;
        for (const std::filesystem::path& relative : relative_outputs) {
            emit_progress(processed_files, relative, "auditing", false);

            const std::filesystem::path output_path = options.generated_root / relative;
            const std::string manifest_key = internal::NormalizePathString(relative.generic_string());
            const auto manifest_it = manifest_rows.find(manifest_key);

            ParitySummaryRow row;
            row.output_relative = relative;
            row.manifest_status =
                manifest_it != manifest_rows.end() ? manifest_it->second.status : "unknown";
            row.manifest_note = manifest_it != manifest_rows.end() ? manifest_it->second.note : "";

            std::string current_source;
            if (!internal::ReadTextFile(output_path, current_source)) {
                row.current_note = "current_source_read_failed";
                row.has_failure = true;
                row.winner = "current";
                row.recommended_action = "new_logic";
                WriteParityRow(parity_stream, row);
                summary_rows.push_back(row);
                ++processed_files;
                emit_progress(processed_files, relative, "audited", false);
                continue;
            }

            internal::ResolvedInput resolved =
                internal::ResolveBestInputPath(options.workspace_root, relative);
            OriginalBytecodeInfo original;
            original.absolute_path = resolved.path;
            {
                std::error_code relative_error;
                original.relative_path =
                    std::filesystem::relative(resolved.path, options.workspace_root, relative_error);
                if (relative_error) {
                    original.relative_path = resolved.path;
                }
            }
            row.source_relative = original.relative_path;

            if (!resolved.note.empty()) {
                row.manifest_note = internal::JoinNotes(row.manifest_note, resolved.note);
            }

            if (std::filesystem::exists(resolved.path)) {
                original.available = internal::ReadFile(resolved.path, original.bytes);
                if (!original.available) {
                    row.has_failure = true;
                    row.current_note = internal::JoinNotes(row.current_note, "source_read_failed");
                } else if (internal::IsLuaBytecode(original.bytes)) {
                    original.is_bytecode = true;
                    original.source_name = internal::DecodeSourceName(original.bytes);
                    original.byte_size = original.bytes.size();
                    std::string parse_error;
                    if (!refii::lua::ParseLua51Chunk(original.bytes, &original.chunk, &parse_error)) {
                        row.has_failure = true;
                        row.current_note = internal::JoinNotes(
                            row.current_note,
                            parse_error.empty() ? "source_chunk_parse_failed"
                                                : "source_chunk_parse_failed=" + parse_error);
                    } else {
                        original.full_stats = refii::lua::SummarizeLua51Chunk(original.chunk);
                        original.main_stats = SummarizeLua51FunctionStats(original.chunk.main);
                        BuildLuaBytecodeParityReference(original.bytes, &original.parity_reference);
                        row.source_is_bytecode = true;
                    }
                } else {
                    local_summary.non_bytecode_sources++;
                    row.source_is_bytecode = false;
                }
            } else {
                row.has_failure = true;
                row.current_note = internal::JoinNotes(row.current_note, "source_missing");
            }

            const std::string compile_chunk_name =
                !original.source_name.empty() ? original.source_name : relative.generic_string();

            CandidateAuditInfo current_candidate;
            current_candidate.name = "current";
            current_candidate.source = current_source;
            CompileLuaSourceToChunk(current_source, compile_chunk_name, &current_candidate.compiled);
            if (!current_candidate.compiled.syntax_valid || !current_candidate.compiled.chunk_parsed) {
                row.has_failure = true;
                current_candidate.note = current_candidate.compiled.error;
            }
            EvaluateCandidateAgainstOriginal(original, &current_candidate);

            CandidateAuditInfo unluac_candidate;
            unluac_candidate.name = "unluac";
            if (row.source_is_bytecode && original.available &&
                !options.oracle_unluac_path.empty()) {
                if (TryRenderUnluacWholeFile(options,
                                             original.absolute_path,
                                             &unluac_candidate.source,
                                             &unluac_candidate.note)) {
                    CompileLuaSourceToChunk(
                        unluac_candidate.source, compile_chunk_name, &unluac_candidate.compiled);
                    if (!unluac_candidate.compiled.syntax_valid ||
                        !unluac_candidate.compiled.chunk_parsed) {
                        row.has_failure = true;
                        unluac_candidate.note =
                            internal::JoinNotes(unluac_candidate.note, unluac_candidate.compiled.error);
                    }
                } else {
                    row.has_failure = true;
                }
                EvaluateCandidateAgainstOriginal(original, &unluac_candidate);
            }

            row.current_syntax_valid = current_candidate.compiled.syntax_valid;
            row.current_main_exact = current_candidate.main_exact;
            row.current_full_exact = current_candidate.full_exact;
            row.current_note = internal::JoinNotes(row.current_note, current_candidate.note);
            row.current_score = current_candidate.score;
            row.first_mismatch = current_candidate.first_mismatch;
            if (row.source_is_bytecode) {
                row.original_main_stats = DescribeLua51FunctionStats(original.main_stats);
                row.original_full_stats =
                    DescribeLua51ChunkStatsWithBytes(original.full_stats, original.byte_size);
            }
            if (current_candidate.compiled.chunk_parsed) {
                row.current_main_stats =
                    DescribeLua51FunctionStats(current_candidate.compiled.main_stats);
                row.current_full_stats = DescribeLua51ChunkStatsWithBytes(
                    current_candidate.compiled.full_stats, current_candidate.compiled.byte_size);
            }

            row.unluac_syntax_valid = unluac_candidate.compiled.syntax_valid;
            row.unluac_main_exact = unluac_candidate.main_exact;
            row.unluac_full_exact = unluac_candidate.full_exact;
            row.unluac_note = internal::JoinNotes(row.unluac_note, unluac_candidate.note);
            row.unluac_score = unluac_candidate.score;
            if (unluac_candidate.compiled.chunk_parsed) {
                row.unluac_main_stats =
                    DescribeLua51FunctionStats(unluac_candidate.compiled.main_stats);
                row.unluac_full_stats = DescribeLua51ChunkStatsWithBytes(
                    unluac_candidate.compiled.full_stats, unluac_candidate.compiled.byte_size);
            }

            if (row.source_is_bytecode) {
                std::string winner = "current";
                CandidateScore best_score = current_candidate.score;
                int best_tiebreak = 0;
                if (CandidateScoreBetter(unluac_candidate.score, 1, best_score, best_tiebreak)) {
                    winner = "unluac";
                }
                row.winner = winner;
                if (row.current_full_exact) {
                    row.recommended_action = "none";
                    local_summary.exact_matches++;
                } else if (winner == "unluac") {
                    row.recommended_action = "port_unluac";
                    local_summary.unluac_best++;
                } else {
                    row.recommended_action = "new_logic";
                    local_summary.current_best++;
                }
                if (!row.current_full_exact && !row.unluac_full_exact) {
                    local_summary.no_candidate_exact++;
                }
            } else {
                row.winner = "current";
                row.recommended_action = "none";
            }

            if (row.has_failure) {
                local_summary.compile_or_parse_failures++;
            }

            if (row.source_is_bytecode && !row.current_full_exact) {
                WriteArtifactBundle(options.report_root,
                                    row.output_relative,
                                    row,
                                    current_candidate.source,
                                    unluac_candidate.source,
                                    &row.artifact_relative);
            }

            WriteParityRow(parity_stream, row);
            summary_rows.push_back(row);

            ++processed_files;
            emit_progress(processed_files, relative, "audited", false);
        }

        parity_stream.flush();
        if (!WriteParitySummaryReport(summary_report_path, summary_rows, local_summary)) {
            if (error != nullptr) {
                *error = "failed to write parity summary: " + summary_report_path.string();
            }
            return false;
        }

        emit_progress(processed_files, {}, "done", true);
        if (summary != nullptr) {
            *summary = std::move(local_summary);
        }
        return true;
    } catch (const std::exception& exception) {
        if (error != nullptr) {
            *error = exception.what();
        }
        return false;
    }
}

}  // namespace refii::lua::audit

namespace refii::lua {

bool AuditLuaWorkspaceBytecodeParity(const LuaBytecodeParityAuditOptions& options,
                                     LuaBytecodeParityAuditSummary* summary,
                                     std::string* error) {
    return audit::RunLuaBytecodeParityAudit(options, summary, error);
}

}  // namespace refii::lua
