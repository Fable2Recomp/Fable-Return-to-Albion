#include "tools/decompiler/rendered/native_main_chunk_render_pipeline.h"

#include "tools/decompiler/rendered/bounded_reference_repair_loop.h"
#include "tools/decompiler/rendered/native_candidate_repair_pipeline.h"
#include "tools/decompiler/native_first.h"
#include "tools/decompiler/rendered/rendered_readability.h"
#include "tools/decompiler/lua_script_profile_policy.h"
#include "tools/decompiler/lua_decompiler_internal.h"
#include "tools/native/lua_native_main_chunk_policy.h"
#include "tools/native/lua_native_render_tool_io.h"
#include "tools/decompiler/repair/lua_reference_function_repairs.h"
#include "tools/native/native_lua_chunk.h"
#include "tools/native/straight_line_native_render.h"

#include <chrono>
#include <cstdint>
#include <filesystem>
#include <string>
#include <system_error>
#include <utility>
#include <vector>

namespace refii::lua::internal {

namespace {

constexpr std::uintmax_t kLargeNativeMainChunkSubprocessThresholdBytes = 192 * 1024;
constexpr std::uintmax_t kVeryLargeChunkFastFailThresholdBytes = 170000;
constexpr std::size_t kQuestHotspotNativeSkipThresholdBytes = 100000;

std::string BuildMainChunkRenderCacheKey(std::string_view bytecode_identity,
                                         bool simplify_rendered_output,
                                         std::string_view render_backend) {
    std::string key;
    key.reserve(bytecode_identity.size() + render_backend.size() + 16);
    key.append(bytecode_identity);
    key.push_back('|');
    key.append(simplify_rendered_output ? "simplified" : "raw");
    key.push_back('|');
    key.append(render_backend);
    return key;
}

void AppendCachedNoteSuffixes(std::string* note,
                              std::string_view note_prefix,
                              const std::vector<std::string>& suffixes) {
    if (note == nullptr || note_prefix.empty()) {
        return;
    }
    for (const std::string& suffix : suffixes) {
        *note = JoinNotes(*note, std::string(note_prefix) + suffix);
    }
}

NativeMainChunkRenderCacheEntry* GetMainChunkCacheEntry(
    const NativeMainChunkRenderPipelineCallbacks& callbacks,
    const std::string& cache_key) {
    if (callbacks.main_chunk_cache_entry == nullptr) {
        return nullptr;
    }
    if (callbacks.trim_main_chunk_cache != nullptr) {
        callbacks.trim_main_chunk_cache();
    }
    return &callbacks.main_chunk_cache_entry(cache_key);
}

bool IsKnownQuestHotspotPath(
    const NativeMainChunkRenderPipelineCallbacks& callbacks,
    std::string_view normalized_path) {
    return callbacks.is_known_quest_hotspot_path != nullptr &&
           callbacks.is_known_quest_hotspot_path(normalized_path);
}

bool HasValidatedExactRepairNoProgressSignal(
    const NativeMainChunkRenderPipelineCallbacks& callbacks,
    std::string_view note) {
    return callbacks.has_validated_exact_repair_no_progress_signal != nullptr &&
           callbacks.has_validated_exact_repair_no_progress_signal(note);
}

bool TryRepairNativeCandidateBytecodeParity(
    const NativeMainChunkRenderPipelineCallbacks& callbacks,
    const std::vector<char>& input_bytes,
    std::string& decompiled_source,
    std::string* note,
    std::string_view note_prefix,
    const LuaWorkspaceDecompileOptions* options,
    const std::filesystem::path* input_path) {
    return callbacks.try_repair_native_candidate_bytecode_parity != nullptr &&
           callbacks.try_repair_native_candidate_bytecode_parity(
               input_bytes, decompiled_source, note, note_prefix, options, input_path);
}

}  // namespace

bool TryRenderNativeMainChunkCandidateViaTool(
    const refii::lua::LuaWorkspaceDecompileOptions& options,
    const std::filesystem::path& input_path,
    const std::vector<char>& input_bytes,
    bool simplify_rendered_output,
    std::string* decompiled_source,
    std::string* note,
    std::string_view note_prefix,
    const NativeMainChunkRenderPipelineCallbacks& callbacks) {
    if (decompiled_source == nullptr) {
        return false;
    }

    const std::string bytecode_identity = ComputeStableBytecodeIdentity(
        std::string_view(input_bytes.data(), input_bytes.size()));
    const std::string cache_key = BuildMainChunkRenderCacheKey(
        bytecode_identity,
        simplify_rendered_output,
        "subprocess");
    NativeMainChunkRenderCacheEntry* cache_entry_ptr =
        GetMainChunkCacheEntry(callbacks, cache_key);
    if (cache_entry_ptr == nullptr) {
        return false;
    }
    NativeMainChunkRenderCacheEntry& cache_entry = *cache_entry_ptr;
    if (cache_entry.attempted) {
        AppendCachedNoteSuffixes(note, note_prefix, cache_entry.note_suffixes);
        if (!cache_entry.success) {
            return false;
        }
        *decompiled_source = cache_entry.rendered_source;
        return true;
    }
    cache_entry.attempted = true;

    const std::filesystem::path temp_output = MakeNativeMainChunkTempOutputPath(input_path);
    std::vector<std::string> tool_args = {"--native-render", "--body"};
    if (!simplify_rendered_output) {
        tool_args.push_back("--raw");
    }
    std::error_code cleanup_error;
    const int exit_code = RunDecompiler(options,
                                        input_path,
                                        temp_output,
                                        tool_args,
                                        true,
                                        GetNativeMainChunkRenderTimeoutSeconds(
                                            input_bytes,
                                            options.native_render_timeout_seconds));
    if (exit_code != 0 || !std::filesystem::exists(temp_output) ||
        std::filesystem::file_size(temp_output) == 0) {
        std::filesystem::remove(temp_output, cleanup_error);
        cache_entry.note_suffixes = {
            "_subprocess_" + DescribeRunResult("render", exit_code)};
        AppendCachedNoteSuffixes(note, note_prefix, cache_entry.note_suffixes);
        return false;
    }

    std::vector<char> rendered_bytes;
    const bool read_ok = ReadFile(temp_output, rendered_bytes);
    std::filesystem::remove(temp_output, cleanup_error);
    if (!read_ok) {
        cache_entry.note_suffixes = {"_subprocess_read_failed"};
        AppendCachedNoteSuffixes(note, note_prefix, cache_entry.note_suffixes);
        return false;
    }
    std::string rendered_main(rendered_bytes.begin(), rendered_bytes.end());
    const bool allow_empty_body = IsTriviallyEmptyLuaMainChunk(input_bytes);

    if (TrimWhitespace(rendered_main).empty() && !input_bytes.empty() && !allow_empty_body) {
        cache_entry.note_suffixes = {"_subprocess_render_failed=empty_body"};
        AppendCachedNoteSuffixes(note, note_prefix, cache_entry.note_suffixes);
        return false;
    }
    if (allow_empty_body && TrimWhitespace(rendered_main).empty()) {
        rendered_main = "\n";
    }

    const LuaReadabilitySummary candidate_readability =
        SummarizeLuaReadability(rendered_main);
    const bool allow_small_holdout_native_quality_relaxation =
        refii::lua::IsLuaScriptProfilePath(
            NormalizePathString(input_path.generic_string()),
            refii::lua::LuaScriptProfileFlag::kScopedSmallHoldout);
    const bool can_relax_small_holdout_quality =
        allow_small_holdout_native_quality_relaxation &&
        IsConservativeSmallHoldoutQualityRelaxCandidate(candidate_readability);
    std::vector<std::string> note_suffixes = {"_subprocess_used"};
    if (allow_empty_body) {
        note_suffixes.push_back("_empty_stub_body");
    }
    if (LooksLikeRegisterizedLua(candidate_readability)) {
        if (IsRunnableLuaDespiteTemporaryNoise(candidate_readability)) {
            note_suffixes.push_back("_quality_relaxed=" +
                                    DescribeLuaReadability(candidate_readability));
        } else if (IsStructuredTableInitializerLuaDespiteTemporaryNoise(
                       candidate_readability)) {
            note_suffixes.push_back("_quality_relaxed_structured_tables=" +
                                    DescribeLuaReadability(candidate_readability));
        } else if (IsLargeNativeLuaBodyUsableDespiteTemporaryNoise(
                       candidate_readability)) {
            note_suffixes.push_back("_quality_relaxed_large_body=" +
                                    DescribeLuaReadability(candidate_readability));
        } else if (can_relax_small_holdout_quality) {
            note_suffixes.push_back("_quality_relaxed_small_holdout=" +
                                    DescribeLuaReadability(candidate_readability));
        } else {
            cache_entry.note_suffixes = {
                "_subprocess_used",
                "_quality_rejected=" + DescribeLuaReadability(candidate_readability)};
            AppendCachedNoteSuffixes(note, note_prefix, cache_entry.note_suffixes);
            return false;
        }
    }

    cache_entry.success = true;
    cache_entry.rendered_source = std::move(rendered_main);
    cache_entry.note_suffixes = std::move(note_suffixes);
    AppendCachedNoteSuffixes(note, note_prefix, cache_entry.note_suffixes);
    *decompiled_source = cache_entry.rendered_source;
    return true;
}

bool TryRenderNativeMainChunkCandidate(const std::vector<char>& input_bytes,
                                       std::string* decompiled_source,
                                       std::string* note,
                                       bool simplify_rendered_output,
                                       std::string_view note_prefix,
                                       std::string_view source_name_hint,
                                       const NativeMainChunkRenderPipelineCallbacks& callbacks) {
    if (decompiled_source == nullptr) {
        return false;
    }

    if (!IsLuaBytecode(input_bytes)) {
        return false;
    }

    const std::string bytecode_identity = ComputeStableBytecodeIdentity(
        std::string_view(input_bytes.data(), input_bytes.size()));
    const std::string cache_key = BuildMainChunkRenderCacheKey(
        bytecode_identity,
        simplify_rendered_output,
        "inprocess");
    NativeMainChunkRenderCacheEntry* cache_entry_ptr =
        GetMainChunkCacheEntry(callbacks, cache_key);
    if (cache_entry_ptr == nullptr) {
        return false;
    }
    NativeMainChunkRenderCacheEntry& cache_entry = *cache_entry_ptr;
    if (cache_entry.attempted) {
        AppendCachedNoteSuffixes(note, note_prefix, cache_entry.note_suffixes);
        if (!cache_entry.success) {
            return false;
        }
        *decompiled_source = cache_entry.rendered_source;
        return true;
    }
    cache_entry.attempted = true;

    const ParsedChunkCacheEntry& parsed_chunk = GetParsedChunkCacheEntry(input_bytes);
    if (!parsed_chunk.success) {
        cache_entry.note_suffixes = {
            "_parse_failed=" + parsed_chunk.parse_error};
        AppendCachedNoteSuffixes(note, note_prefix, cache_entry.note_suffixes);
        return false;
    }

    std::string rendered_main;
    std::string render_error;
    const bool rendered_ok =
        simplify_rendered_output
            ? refii::lua::RenderLua51StraightLineFunctionBody(
                  parsed_chunk.chunk.main, &rendered_main, &render_error)
            : refii::lua::RenderLua51StraightLineFunctionBodyRaw(
                  parsed_chunk.chunk.main, &rendered_main, &render_error);
    if (!rendered_ok) {
        cache_entry.note_suffixes = {"_render_failed=" + render_error};
        AppendCachedNoteSuffixes(note, note_prefix, cache_entry.note_suffixes);
        return false;
    }

    const bool allow_empty_body = IsTriviallyEmptyLuaMainChunk(parsed_chunk.chunk.main);
    if (TrimWhitespace(rendered_main).empty() &&
        !parsed_chunk.chunk.main.code.empty() &&
        !allow_empty_body) {
        cache_entry.note_suffixes = {"_render_failed=empty_body"};
        AppendCachedNoteSuffixes(note, note_prefix, cache_entry.note_suffixes);
        return false;
    }
    if (allow_empty_body && TrimWhitespace(rendered_main).empty()) {
        rendered_main = "\n";
    }

    const LuaReadabilitySummary candidate_readability =
        SummarizeLuaReadability(rendered_main);
    const bool allow_small_holdout_native_quality_relaxation =
        !source_name_hint.empty() &&
        refii::lua::IsLuaScriptProfilePath(
            source_name_hint, refii::lua::LuaScriptProfileFlag::kScopedSmallHoldout);
    const bool can_relax_small_holdout_quality =
        allow_small_holdout_native_quality_relaxation &&
        IsConservativeSmallHoldoutQualityRelaxCandidate(candidate_readability);
    std::vector<std::string> note_suffixes;
    if (allow_empty_body) {
        note_suffixes.push_back("_empty_stub_body");
    }
    if (LooksLikeRegisterizedLua(candidate_readability)) {
        if (!simplify_rendered_output) {
            note_suffixes.push_back("_quality_bypassed_raw=" +
                                    DescribeLuaReadability(candidate_readability));
        } else if (IsRunnableLuaDespiteTemporaryNoise(candidate_readability)) {
            note_suffixes.push_back("_quality_relaxed=" +
                                    DescribeLuaReadability(candidate_readability));
        } else if (IsStructuredTableInitializerLuaDespiteTemporaryNoise(
                       candidate_readability)) {
            note_suffixes.push_back("_quality_relaxed_structured_tables=" +
                                    DescribeLuaReadability(candidate_readability));
        } else if (IsLargeNativeLuaBodyUsableDespiteTemporaryNoise(
                       candidate_readability)) {
            note_suffixes.push_back("_quality_relaxed_large_body=" +
                                    DescribeLuaReadability(candidate_readability));
        } else if (can_relax_small_holdout_quality) {
            note_suffixes.push_back("_quality_relaxed_small_holdout=" +
                                    DescribeLuaReadability(candidate_readability));
        } else {
            cache_entry.note_suffixes = {"_quality_rejected=" +
                                         DescribeLuaReadability(candidate_readability)};
            AppendCachedNoteSuffixes(note, note_prefix, cache_entry.note_suffixes);
            return false;
        }
    }

    cache_entry.success = true;
    cache_entry.rendered_source = std::move(rendered_main);
    cache_entry.note_suffixes = std::move(note_suffixes);
    AppendCachedNoteSuffixes(note, note_prefix, cache_entry.note_suffixes);
    *decompiled_source = cache_entry.rendered_source;
    return true;
}

bool TryReplaceWithValidatedNativeMainChunk(const std::vector<char>& input_bytes,
                                            std::string& decompiled_source,
                                            std::string* note,
                                            const refii::lua::LuaWorkspaceDecompileOptions* options,
                                            const std::filesystem::path* input_path,
                                            std::string_view preferred_render_mode,
                                            const NativeMainChunkRenderPipelineCallbacks& callbacks) {
    if (input_path != nullptr) {
        const std::string normalized_input =
            NormalizePathString(input_path->generic_string());
        if (input_bytes.size() >= kQuestHotspotNativeSkipThresholdBytes &&
            IsKnownQuestHotspotPath(callbacks, normalized_input)) {
            if (note != nullptr) {
                *note = JoinNotes(*note, "native_main_chunk_skipped_quest_hotspot");
            }
            return false;
        }
    }

    const bool outlier_candidate = IsNativeMainChunkOutlierCandidate(input_bytes);
    const bool allow_exhaustive_rescue = ShouldUseExhaustiveRescueForCurrentFile();
    bool prefer_raw_first = ShouldPreferRawNativeMainChunkCandidate(input_bytes);
    // Runtime recovery: for large outliers in non-exhaustive mode, avoid running
    // raw-first candidate generation before the simplified path.
    if (outlier_candidate && !allow_exhaustive_rescue) {
        prefer_raw_first = false;
    }
    if (preferred_render_mode == "raw") {
        prefer_raw_first = true;
    } else if (preferred_render_mode == "simplified") {
        prefer_raw_first = false;
    }

    const int reference_repair_pass_budget =
        GetCurrentReferenceRepairPassBudget();

    auto try_variant = [&](bool simplify_rendered_output,
                           std::string_view note_prefix) {
        const auto deadline_exceeded = [&]() {
            return ShouldAbortCurrentDecompileRepair(note, note_prefix);
        };
        const std::string repair_chunk_name =
            input_path != nullptr ? NormalizePathString(input_path->generic_string())
                                  : std::string(note_prefix);
        const auto variant_started = std::chrono::steady_clock::now();
        std::chrono::steady_clock::duration render_duration{};
        std::chrono::steady_clock::duration repair_duration{};
        std::chrono::steady_clock::duration raw_repair_duration{};
        std::chrono::steady_clock::duration parity_duration{};
        std::string candidate_source;
        bool rendered = false;
        bool attempted_subprocess = false;
        const bool prefer_inprocess_canonical_holdout =
            input_path != nullptr &&
            ShouldPreferInProcessNativeMainChunkForCanonicalHoldout(input_bytes, *input_path);
        const auto render_started = std::chrono::steady_clock::now();
        if (options != nullptr && input_path != nullptr &&
            ShouldUseNativeMainChunkSubprocess(input_bytes) &&
            !prefer_inprocess_canonical_holdout) {
            attempted_subprocess = true;
            rendered = TryRenderNativeMainChunkCandidateViaTool(*options,
                                                                *input_path,
                                                                input_bytes,
                                                                simplify_rendered_output,
                                                                &candidate_source,
                                                                note,
                                                                note_prefix,
                                                                callbacks);
        }

        if (attempted_subprocess && !rendered) {
            if (prefer_inprocess_canonical_holdout ||
                ShouldAllowInProcessNativeMainChunkFallback(input_bytes)) {
                if (note != nullptr) {
                    *note = JoinNotes(*note,
                                      std::string(note_prefix) +
                                          "_subprocess_fallback_to_inprocess");
                }
            } else {
                if (note != nullptr) {
                    *note = JoinNotes(*note,
                                      std::string(note_prefix) +
                                          "_subprocess_only_large_chunk");
                }
                return false;
            }
        }

        if (prefer_inprocess_canonical_holdout && note != nullptr) {
            *note = JoinNotes(*note,
                              std::string(note_prefix) +
                                  "_inprocess_canonical_debug_bank_holdout");
        }

        if (!rendered &&
            !TryRenderNativeMainChunkCandidate(input_bytes,
                                               &candidate_source,
                                               note,
                                               simplify_rendered_output,
                                               note_prefix,
                                               repair_chunk_name,
                                               callbacks)) {
            return false;
        }
        render_duration += std::chrono::steady_clock::now() - render_started;

        const auto repair_started = std::chrono::steady_clock::now();
        std::string candidate_error =
            ValidateLuaSource(candidate_source, std::string(note_prefix));
        const LuaOutputQualitySummary initial_candidate_quality =
            SummarizeLuaOutputQuality(candidate_source);
        const bool skip_validated_outlier_candidate_refinement =
            !simplify_rendered_output && candidate_error.empty() && outlier_candidate &&
            !allow_exhaustive_rescue &&
            input_bytes.size() > kVeryLargeChunkFastFailThresholdBytes &&
            !HasBlockingLuaOutputQualityIssues(initial_candidate_quality) &&
            !ContainsObviouslyInvalidNilUsage(candidate_source);
        if (skip_validated_outlier_candidate_refinement) {
            if (note != nullptr) {
                *note = JoinNotes(*note,
                                  std::string(note_prefix) +
                                      "_candidate_refinement_skipped_fast_outlier");
            }
        } else {
            if (!deadline_exceeded()) {
                TryRepairNativeCandidateSource(candidate_source,
                                               &input_bytes,
                                               note,
                                               note_prefix,
                                               &candidate_error);
            }
        }
        repair_duration += std::chrono::steady_clock::now() - repair_started;
        const LuaOutputQualitySummary candidate_quality =
            SummarizeLuaOutputQuality(candidate_source);
        const bool candidate_quality_usable =
            !HasBlockingLuaOutputQualityIssues(candidate_quality) &&
            !ContainsObviouslyInvalidNilUsage(candidate_source);
        const bool large_validated_fast_finalize =
            candidate_error.empty() && simplify_rendered_output && note != nullptr &&
            IsEligibleLargeValidatedFastFinalize(candidate_source, *note, note_prefix);
        const bool outlier_fast_finalize =
            candidate_error.empty() && simplify_rendered_output && outlier_candidate &&
            candidate_quality_usable && !allow_exhaustive_rescue;
        const bool no_progress_runtime_guard =
            candidate_error.empty() && simplify_rendered_output &&
            !allow_exhaustive_rescue && outlier_candidate &&
            input_bytes.size() >= 64 * 1024 && note != nullptr &&
            HasValidatedExactRepairNoProgressSignal(callbacks, *note);
        const bool outlier_runtime_guard =
            candidate_error.empty() && simplify_rendered_output && outlier_candidate &&
            input_bytes.size() >= 64 * 1024;
        const bool skip_raw_repair = large_validated_fast_finalize || outlier_fast_finalize ||
                                     (!allow_exhaustive_rescue && outlier_candidate) ||
                                     no_progress_runtime_guard || outlier_runtime_guard;
        if (skip_raw_repair && note != nullptr) {
            *note = JoinNotes(
                *note,
                std::string(note_prefix) +
                    (large_validated_fast_finalize
                         ? "_fast_finalized_large_validated"
                         : (outlier_fast_finalize
                                ? "_fast_finalized_outlier"
                                : (no_progress_runtime_guard
                                       ? "_fast_finalized_no_progress_guard"
                                       : (outlier_runtime_guard
                                              ? "_fast_finalized_outlier_runtime_guard"
                                              : "_fast_finalized_nonfailed_outlier")))));
        }
        if (candidate_error.empty() && simplify_rendered_output && !skip_raw_repair &&
            !deadline_exceeded()) {
            const auto raw_repair_started = std::chrono::steady_clock::now();
            std::string raw_candidate_source;
            if (!refii::lua::decompiler::CouldValidatedCandidateBenefitFromRawRepair(
                    candidate_source)) {
                if (note != nullptr) {
                    *note = JoinNotes(*note,
                                      std::string(note_prefix) +
                                          "_raw_donor_skipped_clean_candidate");
                }
            } else if (TryRenderNativeMainChunkCandidate(
                           input_bytes,
                           &raw_candidate_source,
                           note,
                           false,
                           std::string(note_prefix) + "_raw_donor",
                           repair_chunk_name,
                           callbacks)) {
                std::string raw_repair_note;
                if (refii::lua::decompiler::TryRepairValidatedCandidateFromRaw(
                        input_bytes,
                        repair_chunk_name,
                        raw_candidate_source,
                        &candidate_source,
                        &raw_repair_note,
                        std::string(note_prefix) + "_candidate")) {
                    if (note != nullptr && !raw_repair_note.empty()) {
                        *note = JoinNotes(*note, raw_repair_note);
                    }
                    candidate_error =
                        ValidateLuaSource(candidate_source, std::string(note_prefix));
                }
                if (candidate_error.empty()) {
                    RunBoundedReferenceRepairLoop(
                        reference_repair_pass_budget,
                        true,
                        &candidate_source,
                        &candidate_error,
                        note,
                        std::string(note_prefix) + "_candidate_raw_no_progress",
                        [&]() { return deadline_exceeded(); },
                        [&]() {
                            return TryRepairCandidateMismatchedFunctionsFromReference(
                                input_bytes,
                                raw_candidate_source,
                                &candidate_source,
                                note,
                                std::string(note_prefix) + "_candidate_raw",
                                true);
                        },
                        [&]() {
                            return ValidateLuaSource(candidate_source,
                                                     std::string(note_prefix));
                        });
                }
            }
            raw_repair_duration += std::chrono::steady_clock::now() - raw_repair_started;
        }
        const bool skip_parity_repair_runtime_guard =
            candidate_error.empty() && simplify_rendered_output && outlier_candidate &&
            input_bytes.size() >= 64 * 1024 && !allow_exhaustive_rescue;
        const auto parity_started = std::chrono::steady_clock::now();
        if (!skip_parity_repair_runtime_guard &&
            candidate_error.empty() && !deadline_exceeded() &&
            TryRepairNativeCandidateBytecodeParity(callbacks,
                                                   input_bytes,
                                                   candidate_source,
                                                   note,
                                                   note_prefix,
                                                   options,
                                                   input_path)) {
            candidate_error = ValidateLuaSource(candidate_source, std::string(note_prefix));
        } else if (skip_parity_repair_runtime_guard && note != nullptr) {
            *note = JoinNotes(*note,
                              std::string(note_prefix) +
                                  "_parity_repair_skipped_runtime_guard");
        }
        parity_duration += std::chrono::steady_clock::now() - parity_started;
        if (!deadline_exceeded()) {
            const std::string pre_restore_source = candidate_source;
            if (refii::lua::decompiler::TryApplyBytecodeBackedMainChunkRestorations(
                    input_bytes,
                    std::string(note_prefix),
                    &candidate_source,
                    note,
                    note_prefix)) {
                candidate_error = ValidateLuaSource(candidate_source, std::string(note_prefix));
                if (!candidate_error.empty()) {
                    candidate_source = pre_restore_source;
                    candidate_error = ValidateLuaSource(candidate_source, std::string(note_prefix));
                    if (note != nullptr) {
                        *note = JoinNotes(*note,
                                          std::string(note_prefix) +
                                              "_bytecode_restoration_reverted_invalid");
                    }
                }
            }
        }

        const auto maybe_append_timing_note = [&]() {
            if (note == nullptr) {
                return;
            }
            const auto total_duration = std::chrono::steady_clock::now() - variant_started;
            const auto total_ms =
                std::chrono::duration_cast<std::chrono::milliseconds>(total_duration).count();
            if (input_bytes.size() < kLargeNativeMainChunkSubprocessThresholdBytes &&
                total_ms < 1000) {
                return;
            }
            const auto render_ms =
                std::chrono::duration_cast<std::chrono::milliseconds>(render_duration).count();
            const auto cleanup_ms =
                std::chrono::duration_cast<std::chrono::milliseconds>(repair_duration).count();
            const auto raw_cleanup_ms =
                std::chrono::duration_cast<std::chrono::milliseconds>(raw_repair_duration).count();
            const auto parity_ms =
                std::chrono::duration_cast<std::chrono::milliseconds>(parity_duration).count();
            *note = JoinNotes(*note,
                              std::string(note_prefix) + "_timing_ms=render:" +
                                  std::to_string(render_ms) + ",cleanup:" +
                                  std::to_string(cleanup_ms + raw_cleanup_ms) + ",parity:" +
                                  std::to_string(parity_ms) + ",total:" +
                                  std::to_string(total_ms));
        };
        if (!candidate_error.empty()) {
            maybe_append_timing_note();
            MaybeDumpInvalidNativeCandidate(
                note_prefix, input_path, candidate_source, candidate_error);
            if (note != nullptr) {
                *note = JoinNotes(*note,
                                  std::string(note_prefix) + "_invalid=" +
                                      SummarizeSyntaxError(candidate_error));
            }
            return false;
        }

        maybe_append_timing_note();
        if (note != nullptr) {
            *note = JoinNotes(*note, std::string(note_prefix) + "_validated");
        }
        decompiled_source = std::move(candidate_source);
        return true;
    };

    if (prefer_raw_first && try_variant(false, "native_main_chunk_raw")) {
        return true;
    }
    if (ShouldAbortCurrentDecompileRepair(note, "native_main_chunk")) {
        return false;
    }
    if (try_variant(true, "native_main_chunk")) {
        return true;
    }
    if (prefer_raw_first) {
        return false;
    }
    if (ShouldAbortCurrentDecompileRepair(note, "native_main_chunk_raw")) {
        return false;
    }
    return try_variant(false, "native_main_chunk_raw");
}

}  // namespace refii::lua::internal
