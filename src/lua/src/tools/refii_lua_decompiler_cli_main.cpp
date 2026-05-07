#include "tools/native/native_lua_decompiler.h"
#include "tools/decompiler/lua_decompiler_internal.h"
#include "tools/decompiler/rendered/rendered_lua_text.h"

#include <filesystem>
#include <iostream>
#include <limits>
#include <mutex>
#include <string>
#include <string_view>
#include <vector>

namespace {

struct Options {
    std::string command = "decompile";
    std::filesystem::path workspace;
    std::filesystem::path output;
    std::filesystem::path debug_candidate_input;
    std::filesystem::path generated_root;
    std::filesystem::path report_root;
    std::filesystem::path relative_path;
    std::filesystem::path oracle_unluac;
    std::filesystem::path debug_input;
    std::size_t worker_count = 0;
    std::uint64_t source_wall_time_budget_ms = 600000;
    std::size_t max_phase2_heavy_exactification_jobs = 0;
    int debug_function_index = 0;
    bool audit_bytecode = false;
    bool debug_render_body = false;
    bool debug_render_raw = false;
};

bool ParseArguments(int argc, char** argv, Options& options) {
    if (argc < 2) {
        return false;
    }

    int argument_index = 1;
    const std::string_view first_argument(argv[1]);
    if (first_argument == "build" || first_argument == "verify" || first_argument == "reset-all" ||
        first_argument == "reset-file" || first_argument == "debug-native-render" ||
        first_argument == "debug-dis" || first_argument == "debug-finalize" ||
        first_argument == "debug-parity" ||
        first_argument == "debug-cleanup" || first_argument == "debug-header-compare" ||
        first_argument == "debug-repair-functions") {
        options.command = std::string(first_argument);
        ++argument_index;
        if (argc <= argument_index) {
            return false;
        }
    }

    if (options.command == "debug-native-render" || options.command == "debug-dis" ||
        options.command == "debug-finalize" || options.command == "debug-parity" ||
        options.command == "debug-cleanup" ||
        options.command == "debug-header-compare" ||
        options.command == "debug-repair-functions") {
        options.debug_input = std::filesystem::path(argv[argument_index++]);
        if (argc <= argument_index) {
            return false;
        }
        if (options.command == "debug-finalize" ||
            options.command == "debug-parity" ||
            options.command == "debug-repair-functions") {
            options.debug_candidate_input = std::filesystem::path(argv[argument_index++]);
            if (options.command != "debug-parity" && argc <= argument_index) {
                return false;
            }
        }
        if (options.command != "debug-parity") {
            options.output = std::filesystem::path(argv[argument_index++]);
        }

        for (int i = argument_index; i < argc; ++i) {
            const std::string_view argument(argv[i]);
            if (options.command == "debug-native-render" && argument == "--body") {
                options.debug_render_body = true;
            } else if (options.command == "debug-native-render" && argument == "--raw") {
                options.debug_render_raw = true;
            } else if (options.command == "debug-native-render" && argument == "-f" &&
                       i + 1 < argc) {
                try {
                    options.debug_function_index = std::stoi(argv[++i]);
                } catch (const std::exception&) {
                    return false;
                }
            } else {
                return false;
            }
        }

        return true;
    }

    options.workspace = std::filesystem::path(argv[argument_index++]);
    if (options.command == "reset-file") {
        if (argc <= argument_index) {
            return false;
        }
        options.relative_path = std::filesystem::path(argv[argument_index++]);
    }

    for (int i = argument_index; i < argc; ++i) {
        const std::string_view argument(argv[i]);
        if (argument == "--out" && i + 1 < argc) {
            options.output = std::filesystem::path(argv[++i]);
        } else if (argument == "--generated-root" && i + 1 < argc) {
            options.generated_root = std::filesystem::path(argv[++i]);
        } else if (argument == "--report-root" && i + 1 < argc) {
            options.report_root = std::filesystem::path(argv[++i]);
        } else if (argument == "--oracle-unluac" && i + 1 < argc) {
            options.oracle_unluac = std::filesystem::path(argv[++i]);
        } else if (argument == "--source-wall-time-budget-ms" && i + 1 < argc) {
            try {
                options.source_wall_time_budget_ms = std::stoull(argv[++i]);
            } catch (const std::exception&) {
                return false;
            }
        } else if (argument == "--max-heavy-exactification-jobs" && i + 1 < argc) {
            try {
                options.max_phase2_heavy_exactification_jobs =
                    static_cast<std::size_t>(std::stoull(argv[++i]));
            } catch (const std::exception&) {
                return false;
            }
        } else if (argument == "--workers" && i + 1 < argc) {
            try {
                options.worker_count = static_cast<std::size_t>(std::stoull(argv[++i]));
            } catch (const std::exception&) {
                return false;
            }
        } else if (argument == "--audit-bytecode") {
            options.audit_bytecode = true;
        } else {
            return false;
        }
    }

    return true;
}

std::function<void(const refii::lua::LuaWorkspaceDecompileProgress&)> MakeProgressCallback() {
    return [](const refii::lua::LuaWorkspaceDecompileProgress& progress) {
        static std::mutex progress_mutex;
        std::lock_guard<std::mutex> lock(progress_mutex);
        if (progress.phase == "starting") {
            std::cerr << "[0/" << progress.total_files << "] starting\n";
            return;
        }
        if (progress.phase == "done") {
            std::cerr << "[" << progress.completed_files << "/" << progress.total_files
                      << "] done\n";
            return;
        }
        if (progress.phase == "exporting_workspace" ||
            progress.phase == "snapshotting_baseline" ||
            progress.phase == "verifying_workspace" ||
            progress.phase == "resetting_workspace") {
            std::cerr << progress.phase;
            if (!progress.current_input.empty()) {
                std::cerr << " " << progress.current_input.generic_string();
            }
            std::cerr << "\n";
            return;
        }
        if (progress.phase == "phase1_processing" ||
            progress.phase == "phase2_holdout_repair" ||
            progress.phase == "phase2_heavy_exactification" ||
            progress.phase == "phase2_large_holdout_repair") {
            std::cerr << "[" << progress.completed_files << "/" << progress.total_files
                      << "] " << progress.phase;
            if (progress.phase_total_files != 0) {
                std::cerr << " phase=" << progress.phase_completed_files << "/"
                          << progress.phase_total_files;
            }
            if (progress.worker_count != 0) {
                std::cerr << " worker=" << (progress.worker_slot + 1) << "/"
                          << progress.worker_count;
            }
            if (progress.active_workers != 0) {
                std::cerr << " active=" << progress.active_workers;
            }
            if (!progress.current_input.empty()) {
                std::cerr << " " << progress.current_input.generic_string();
            }
            if (progress.active_elapsed_ms != 0 ||
                progress.source_wall_time_budget_ms != 0) {
                std::cerr << " elapsed_ms=" << progress.active_elapsed_ms;
                if (progress.source_wall_time_budget_ms != 0) {
                    std::cerr << "/" << progress.source_wall_time_budget_ms;
                }
            }
            std::cerr << "\n";
            return;
        }
        std::cerr << "[" << progress.completed_files << "/" << progress.total_files
                  << "] ";
        if (!progress.current_input.empty()) {
            std::cerr << progress.current_input.generic_string();
        } else {
            std::cerr << progress.phase;
        }
        std::cerr << "\n";
    };
}

}  // namespace

int main(int argc, char** argv) {
    Options options;
    if (!ParseArguments(argc, argv, options)) {
        std::cerr
            << "usage: refii-lua-decompiler <workspace> [--out <dir>]\n"
            << "       [--workers <n>] [--source-wall-time-budget-ms <ms>]\n"
            << "       [--max-heavy-exactification-jobs <n>]\n"
            << "       (--workers omitted or 0 uses a responsive automatic default)\n"
            << "       (default decompile mode: two-phase exact-first, strict exact outputs)\n"
            << "       refii-lua-decompiler <workspace> --audit-bytecode "
               "[--generated-root <dir>] [--report-root <dir>] [--oracle-unluac <path>]\n"
            << "       refii-lua-decompiler build <game-root> [--workers <n>]\n"
            << "       refii-lua-decompiler verify <workspace>\n"
            << "       refii-lua-decompiler reset-all <workspace>\n"
            << "       refii-lua-decompiler reset-file <workspace> <relative-path>\n"
            << "       refii-lua-decompiler debug-native-render <input> <output> "
               "[--body] [--raw] [-f <n>]\n"
            << "       refii-lua-decompiler debug-dis <input> <output>\n"
            << "       refii-lua-decompiler debug-parity <input> <candidate>\n"
            << "       refii-lua-decompiler debug-cleanup <candidate> <output>\n"
            << "       refii-lua-decompiler debug-header-compare <candidate> <output>\n"
            << "       refii-lua-decompiler debug-finalize <input> <candidate> <output>\n"
            << "       refii-lua-decompiler debug-repair-functions <input> <candidate> <output>\n";
        return 1;
    }

    if (options.command == "debug-repair-functions") {
        std::vector<char> input_bytes;
        std::string candidate_source;
        if (!refii::lua::internal::ReadFile(options.debug_input, input_bytes) ||
            !refii::lua::internal::ReadTextFile(options.debug_candidate_input, candidate_source)) {
            std::cerr << "failed to read debug-repair-functions inputs\n";
            return 1;
        }

        std::string note;
        std::string validation_error = refii::lua::internal::ValidateLuaSource(
            candidate_source, options.debug_input.filename().generic_string());
        bool changed = false;
        std::cerr << "debug-repair-functions: initial_validation="
                  << (validation_error.empty() ? "ok" : validation_error) << "\n";
        if (!validation_error.empty()) {
            std::cerr << "debug-repair-functions: start_reported_function_repair\n";
            changed = refii::lua::internal::TryRepairReportedFunctionsFromBytecodeIteratively(
                          input_bytes,
                          candidate_source,
                          &validation_error,
                          &note,
                          "debug_repair_functions") ||
                      changed;
            std::cerr << "debug-repair-functions: end_reported_function_repair validation="
                      << (validation_error.empty() ? "ok" : validation_error) << "\n";
        }
        std::cerr << "debug-repair-functions: start_function_replacement\n";
        changed = refii::lua::internal::TryReplaceDecompilerErrorFunctions(
                      input_bytes,
                      candidate_source,
                      &note,
                      false,
                      nullptr,
                      &options.debug_input) ||
                  changed;
        std::cerr << "debug-repair-functions: end_function_replacement\n";
        validation_error = refii::lua::internal::ValidateLuaSource(
            candidate_source, options.debug_input.filename().generic_string());
        std::cerr << "debug-repair-functions: final_validation="
                  << (validation_error.empty() ? "ok" : validation_error) << "\n";
        if (!refii::lua::internal::WriteTextFile(options.output, candidate_source)) {
            std::cerr << "failed to write debug-repair-functions output\n";
            return 1;
        }
        std::cerr << "changed=" << (changed ? "true" : "false") << "\n";
        if (validation_error.empty()) {
            std::cerr << "validation=ok\n";
        } else {
            std::cerr << "validation=" << validation_error << "\n";
        }
        if (!note.empty()) {
            std::cerr << note << "\n";
        }
        return validation_error.empty() ? 0 : 1;
    }

    if (options.command == "debug-finalize") {
        std::vector<char> input_bytes;
        std::string candidate_source;
        if (!refii::lua::internal::ReadFile(options.debug_input, input_bytes) ||
            !refii::lua::internal::ReadTextFile(options.debug_candidate_input, candidate_source)) {
            std::cerr << "failed to read debug-finalize inputs\n";
            return 1;
        }

        std::string note;
        const std::filesystem::path failed_output =
            options.output.parent_path() /
            (options.output.stem().generic_string() + ".failed.lua");
        const refii::lua::internal::NativeCandidateFinalizeResult result =
            refii::lua::internal::TryFinalizeNativeCandidateOutput(
                options.debug_input,
                std::move(candidate_source),
                input_bytes,
                options.output,
                failed_output,
                nullptr,
                &options.debug_input,
                &note,
                "debug_finalize");
        std::cerr << "kind=" << static_cast<int>(result.kind)
                  << " exact_parity_required="
                  << (result.exact_parity_required ? "true" : "false") << "\n";
        if (!note.empty()) {
            std::cerr << note << "\n";
        }
        return result.kind == refii::lua::internal::NativeCandidateFinalizeKind::kInvalid ? 1 : 0;
    }

    if (options.command == "debug-parity") {
        std::vector<char> input_bytes;
        std::string candidate_source;
        if (!refii::lua::internal::ReadFile(options.debug_input, input_bytes) ||
            !refii::lua::internal::ReadTextFile(options.debug_candidate_input, candidate_source)) {
            std::cerr << "failed to read debug-parity inputs\n";
            return 1;
        }

        refii::lua::audit::LuaBytecodeParityReference reference;
        if (!refii::lua::audit::BuildLuaBytecodeParityReference(input_bytes, &reference) ||
            !reference.available) {
            std::cerr << "failed to build parity reference\n";
            return 1;
        }

        refii::lua::audit::LuaBytecodeParityResult parity_result;
        if (!refii::lua::audit::EvaluateLuaSourceBytecodeParity(
                reference,
                candidate_source,
                options.debug_candidate_input.filename().generic_string(),
                &parity_result)) {
            std::cerr << "failed to evaluate candidate parity\n";
            return 1;
        }

        std::cout << "candidate_compiled="
                  << (parity_result.candidate_compiled ? "true" : "false") << "\n";
        std::cout << "candidate_chunk_parsed="
                  << (parity_result.candidate_chunk_parsed ? "true" : "false") << "\n";
        std::cout << "main_stats_exact="
                  << (parity_result.main_stats_exact ? "true" : "false") << "\n";
        std::cout << "full_stats_exact="
                  << (parity_result.full_stats_exact ? "true" : "false") << "\n";
        std::cout << "exact_bytecode="
                  << (parity_result.exact_bytecode ? "true" : "false") << "\n";
        std::cout << "reference_main_stats="
                  << refii::lua::audit::DescribeLua51FunctionStats(reference.main_stats)
                  << "\n";
        std::cout << "candidate_main_stats="
                  << refii::lua::audit::DescribeLua51FunctionStats(
                         parity_result.compiled_candidate.main_stats)
                  << "\n";
        std::cout << "reference_full_stats="
                  << refii::lua::DescribeLua51ChunkStats(reference.full_stats) << "\n";
        std::cout << "candidate_full_stats="
                  << refii::lua::DescribeLua51ChunkStats(
                         parity_result.compiled_candidate.full_stats)
                  << "\n";
        if (!parity_result.error.empty()) {
            std::cout << "error=" << parity_result.error << "\n";
        }
        if (parity_result.first_mismatch.available) {
            std::cout << "first_mismatch_function_index="
                      << parity_result.first_mismatch.function_index << "\n";
            std::cout << "first_mismatch_line="
                      << parity_result.first_mismatch.line_defined << "\n";
            std::cout << "first_mismatch_instruction_delta="
                      << parity_result.first_mismatch.instruction_delta << "\n";
            std::cout << "first_mismatch_constant_delta="
                      << parity_result.first_mismatch.constant_delta << "\n";
            std::cout << "first_mismatch_local_delta="
                      << parity_result.first_mismatch.local_delta << "\n";
            std::cout << "first_mismatch_note="
                      << parity_result.first_mismatch.note << "\n";
            std::cout << "first_mismatch_summary="
                      << refii::lua::audit::DescribeMismatchForSummary(
                             parity_result.first_mismatch)
                      << "\n";
        }
        return parity_result.exact_bytecode ? 0 : 0;
    }

    if (options.command == "debug-cleanup") {
        std::string candidate_source;
        if (!refii::lua::internal::ReadTextFile(options.debug_input, candidate_source)) {
            std::cerr << "failed to read debug-cleanup input\n";
            return 1;
        }

        bool had_trailing_newline = !candidate_source.empty() && candidate_source.back() == '\n';
        const std::string original_source = candidate_source;
        std::vector<std::string> lines = refii::lua::SplitRenderedLines(candidate_source);
        bool changed = false;
        changed |= refii::lua::internal::ApplySharedRenderedLuaCleanup(
            &lines, refii::lua::internal::RenderedLuaCleanupProfile::kValidatedOutput);
        std::string shared_cleaned_source =
            refii::lua::internal::JoinLines(lines, had_trailing_newline);
        const std::string shared_validation_error =
            refii::lua::internal::ValidateLuaSource(shared_cleaned_source,
                                                    options.debug_input.filename().generic_string());
        if (!shared_validation_error.empty()) {
            shared_cleaned_source = original_source;
            lines = refii::lua::SplitRenderedLines(shared_cleaned_source);
            std::cerr << "shared_reverted=true\n";
            std::cerr << "shared_validation=" << shared_validation_error << "\n";
        }
        candidate_source = shared_cleaned_source;

        if (refii::lua::ApplyRenderedLuaExactParityNormalizationProfile(&lines)) {
            const std::string parity_candidate_source =
                refii::lua::internal::JoinLines(lines, had_trailing_newline);
            const std::string parity_validation_error =
                refii::lua::internal::ValidateLuaSource(
                    parity_candidate_source,
                    options.debug_input.filename().generic_string());
            if (parity_validation_error.empty()) {
                candidate_source = parity_candidate_source;
                changed = true;
            } else {
                std::cerr << "parity_reverted=true\n";
                std::cerr << "parity_validation=" << parity_validation_error << "\n";
            }
        }

        if (!refii::lua::internal::WriteTextFile(options.output, candidate_source)) {
            std::cerr << "failed to write debug-cleanup output\n";
            return 1;
        }
        std::cerr << "changed=" << (changed ? "true" : "false") << "\n";
        return 0;
    }

    if (options.command == "debug-header-compare") {
        std::string candidate_source;
        if (!refii::lua::internal::ReadTextFile(options.debug_input, candidate_source)) {
            std::cerr << "failed to read debug-header-compare input\n";
            return 1;
        }

        const bool changed =
            refii::lua::internal::DebugApplyHeaderComparisonParitySwap(&candidate_source);
        if (!refii::lua::internal::WriteTextFile(options.output, candidate_source)) {
            std::cerr << "failed to write debug-header-compare output\n";
            return 1;
        }

        std::cerr << "changed=" << (changed ? "true" : "false") << "\n";
        return 0;
    }

    if (options.command == "debug-native-render" || options.command == "debug-dis") {
        refii::lua::LuaWorkspaceDecompileOptions debug_options;
        std::vector<std::string> extra_args;
        if (options.command == "debug-dis") {
            extra_args.push_back("-dis");
        } else {
            extra_args.push_back("--native-render");
            if (options.debug_render_body) {
                extra_args.push_back("--body");
            }
            if (options.debug_render_raw) {
                extra_args.push_back("--raw");
            }
            if (options.debug_function_index > 0) {
                extra_args.push_back("-f");
                extra_args.push_back(std::to_string(options.debug_function_index));
            }
        }

        const int exit_code = refii::lua::internal::RunDecompiler(debug_options,
                                                                  options.debug_input,
                                                                  options.output,
                                                                  extra_args,
                                                                  false,
                                                                  std::numeric_limits<unsigned int>::max());
        if (exit_code != 0) {
            std::cerr << "debug command failed with exit code " << exit_code << "\n";
        }
        return exit_code;
    }

    if (options.command == "build") {
        refii::lua::LuaWorkspaceBuildOptions build_options;
        build_options.game_install_root = options.workspace;
        build_options.worker_count = options.worker_count;
        build_options.progress_callback = MakeProgressCallback();
        build_options.source_wall_time_budget_ms = options.source_wall_time_budget_ms;
        build_options.max_phase2_heavy_exactification_jobs =
            options.max_phase2_heavy_exactification_jobs;

        refii::lua::LuaWorkspaceBuildSummary summary;
        std::string error;
        if (!refii::lua::BuildLuaWorkspace(build_options, &summary, &error)) {
            std::cerr << error << "\n";
            return 1;
        }

        std::cout << "Workspace: " << summary.workspace_root << "\n";
        std::cout << "Source:    " << summary.source_root << "\n";
        std::cout << "Output:    " << summary.output_root << "\n";
        std::cout << "Baseline:  " << summary.baseline_manifest_path << "\n";
        std::cout << "Banks:     " << summary.extracted_banks << "\n";
        std::cout << "Extracted: " << summary.extracted_files << "\n";
        std::cout << "Loose:     " << summary.copied_loose_files << "\n";
        std::cout << "Tracked:   " << summary.baseline_files << "\n";
        return 0;
    }

    if (options.command == "verify") {
        refii::lua::LuaWorkspaceVerifyOptions verify_options;
        verify_options.workspace_root = options.workspace;
        verify_options.include_ok_entries = true;
        verify_options.progress_callback = MakeProgressCallback();

        refii::lua::LuaWorkspaceVerifySummary summary;
        std::string error;
        if (!refii::lua::VerifyLuaWorkspace(verify_options, &summary, &error)) {
            std::cerr << error << "\n";
            return 1;
        }

        std::cout << "Workspace: " << summary.workspace_root << "\n";
        std::cout << "Baseline:  " << summary.baseline_manifest_path << "\n";
        std::cout << "Tracked:   " << summary.tracked_files << "\n";
        std::cout << "Ok:        " << summary.ok << "\n";
        std::cout << "Modified:  " << summary.modified << "\n";
        std::cout << "Missing:   " << summary.missing << "\n";
        std::cout << "Extra:     " << summary.extra << "\n";
        std::cout << "BaseMiss:  " << summary.baseline_missing << "\n";
        std::cout << "BaseBad:   " << summary.baseline_corrupt << "\n";
        for (const refii::lua::LuaWorkspaceVerifyEntry& entry : summary.entries) {
            if (entry.relative_path.empty()) {
                continue;
            }
            std::cout << refii::lua::DescribeLuaWorkspaceVerifyStatus(entry.status) << '\t'
                      << entry.relative_path.generic_string() << "\n";
        }
        return 0;
    }

    if (options.command == "reset-all" || options.command == "reset-file") {
        refii::lua::LuaWorkspaceResetOptions reset_options;
        reset_options.workspace_root = options.workspace;
        reset_options.reset_all = options.command == "reset-all";
        reset_options.relative_path = options.relative_path;
        reset_options.progress_callback = MakeProgressCallback();

        refii::lua::LuaWorkspaceResetSummary summary;
        std::string error;
        if (!refii::lua::ResetLuaWorkspace(reset_options, &summary, &error)) {
            std::cerr << error << "\n";
            return 1;
        }

        std::cout << "Workspace: " << summary.workspace_root << "\n";
        std::cout << "Baseline:  " << summary.baseline_root << "\n";
        std::cout << "Restored:  " << summary.restored << "\n";
        std::cout << "Removed:   " << summary.removed << "\n";
        std::cout << "Failed:    " << summary.failed << "\n";
        return 0;
    }

    if (options.audit_bytecode) {
        refii::lua::LuaBytecodeParityAuditOptions audit_options;
        audit_options.workspace_root = options.workspace;
        audit_options.generated_root = options.generated_root;
        audit_options.report_root = options.report_root;
        audit_options.oracle_unluac_path = options.oracle_unluac;

        refii::lua::LuaBytecodeParityAuditSummary summary;
        std::string error;
        if (!refii::lua::AuditLuaWorkspaceBytecodeParity(audit_options, &summary, &error)) {
            std::cerr << error << "\n";
            return 1;
        }

        std::cout << "Workspace: " << summary.workspace_root << "\n";
        std::cout << "Generated: " << summary.generated_root << "\n";
        std::cout << "Report:    " << summary.report_root << "\n";
        std::cout << "Files:     " << summary.total_files << "\n";
        std::cout << "Exact:     " << summary.exact_matches << "\n";
        std::cout << "Current:   " << summary.current_best << "\n";
        std::cout << "Unluac:    " << summary.unluac_best << "\n";
        std::cout << "NoExact:   " << summary.no_candidate_exact << "\n";
        std::cout << "Failures:  " << summary.compile_or_parse_failures << "\n";
        std::cout << "NoBytecode:" << summary.non_bytecode_sources << "\n";
        std::cout << "Parity:    " << summary.parity_report_path << "\n";
        std::cout << "Summary:   " << summary.summary_report_path << "\n";
        return 0;
    }

    refii::lua::LuaWorkspaceDecompileOptions decompile_options;
    decompile_options.workspace_root = options.workspace;
    decompile_options.output_root = options.output;
    decompile_options.worker_count = options.worker_count;
    decompile_options.source_wall_time_budget_ms = options.source_wall_time_budget_ms;
    decompile_options.max_phase2_heavy_exactification_jobs =
        options.max_phase2_heavy_exactification_jobs;
    decompile_options.pipeline_mode =
        refii::lua::LuaWorkspacePipelineMode::kTwoPhaseExactFirst;
    decompile_options.exact_parity_policy =
        refii::lua::LuaExactParityPolicy::kAlwaysForBytecode;
    decompile_options.strict_exact_outputs = true;
    decompile_options.progress_callback = MakeProgressCallback();

    refii::lua::LuaWorkspaceDecompileSummary summary;
    std::string error;
    if (!refii::lua::DecompileLuaWorkspace(decompile_options, &summary, &error)) {
        std::cerr << error << "\n";
        return 1;
    }

    std::cout << "Workspace: " << summary.workspace_root << "\n";
    std::cout << "Output:    " << summary.output_root << "\n";
    std::cout << "Files:     " << summary.total_files << "\n";
    std::cout << "Decompiled:" << summary.decompiled << "\n";
    std::cout << "Aliased:   " << summary.aliased << "\n";
    std::cout << "Nonexact:  " << summary.nonexact << "\n";
    std::cout << "AliasNonx: " << summary.aliased_nonexact << "\n";
    std::cout << "Disasm:    " << summary.disassembled << "\n";
    std::cout << "Invalid:   " << summary.invalid << "\n";
    std::cout << "Budget:    " << summary.budget_exceeded << "\n";
    std::cout << "Copied:    " << summary.copied << "\n";
    std::cout << "Skipped:   " << summary.skipped << "\n";
    std::cout << "Manifest:  " << summary.manifest_path << "\n";
    return 0;
}
