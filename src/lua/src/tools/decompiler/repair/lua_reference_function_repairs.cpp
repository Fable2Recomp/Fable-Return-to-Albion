#include "tools/decompiler/repair/lua_reference_function_repairs.h"

#include "tools/decompiler/rendered/bytecode_parity_repair_context.h"
#include "tools/decompiler/rendered/rendered_readability.h"
#include "tools/bytecode/lua_bytecode_repair_analysis.h"
#include "tools/decompiler/repair/lua_decompiled_function_spans.h"
#include "tools/decompiler/lua_decompiler_internal.h"

#include <algorithm>
#include <string_view>
#include <unordered_map>
#include <utility>

namespace refii::lua::internal {
namespace {

bool HasBlockingRepairDonorSource(std::string_view source) {
    return HasBlockingLuaOutputQualityIssues(SummarizeLuaOutputQuality(source)) ||
           ContainsObviouslyInvalidNilUsage(source);
}

}  // namespace

bool TryRepairCandidateMismatchedFunctionsFromReference(
    const std::vector<char>& input_bytes,
    const std::string& reference_source,
    std::string* candidate_source,
    std::string* note,
    std::string_view note_prefix,
    bool require_reference_exact) {
    if (candidate_source == nullptr || reference_source.empty() ||
        reference_source == *candidate_source) {
        return false;
    }
    if (ShouldAbortCurrentDecompileRepair(note, note_prefix)) {
        return false;
    }

    refii::lua::Lua51Chunk original_chunk;
    std::string parse_error;
    if (!refii::lua::ParseLua51Chunk(input_bytes, &original_chunk, &parse_error)) {
        return false;
    }

    refii::lua::audit::CompiledLuaChunkInfo compiled_reference;
    if (!CompileLuaSourceToChunkCached(reference_source,
                                       "bytecode_parity_reference",
                                       &compiled_reference) ||
        !compiled_reference.chunk_parsed) {
        return false;
    }

    bool changed = false;
    std::string previous_pass_source;
    std::vector<std::size_t> previous_pass_mismatch_indices;
    const int max_passes = GetCurrentRepairAttemptCap(8, 4);
    for (int pass = 0; pass < max_passes; ++pass) {
        if (ShouldAbortCurrentDecompileRepair(note, note_prefix)) {
            break;
        }
        refii::lua::audit::CompiledLuaChunkInfo compiled_candidate;
        if (!CompileLuaSourceToChunkCached(*candidate_source,
                                           "bytecode_parity_candidate_reference",
                                           &compiled_candidate) ||
            !compiled_candidate.chunk_parsed) {
            break;
        }

        const std::vector<std::size_t> mismatch_indices =
            CollectLuaMismatchedFunctionIndices(original_chunk, compiled_candidate.chunk);
        if (pass != 0 && *candidate_source == previous_pass_source &&
            mismatch_indices == previous_pass_mismatch_indices) {
            break;
        }
        previous_pass_source = *candidate_source;
        previous_pass_mismatch_indices = mismatch_indices;
        if (mismatch_indices.empty()) {
            break;
        }

        bool reference_trailing_newline = false;
        const std::vector<std::string> reference_lines =
            SplitLines(reference_source, &reference_trailing_newline);
        refii::lua::internal::LuaMutableTextContext candidate_context(*candidate_source);
        const std::vector<std::string>& candidate_lines_view =
            candidate_context.EnsureLines();
        const bool candidate_trailing_newline = candidate_context.HadTrailingNewline();
        std::vector<std::string> candidate_lines(candidate_lines_view.begin(),
                                                 candidate_lines_view.end());
        const std::vector<DecompiledFunctionSpan> reference_spans =
            CollectDecompiledFunctionSpans(reference_lines);
        const std::vector<DecompiledFunctionSpan> candidate_spans =
            CollectDecompiledFunctionSpans(candidate_lines);
        std::vector<std::size_t> repair_indices = mismatch_indices;
        const std::size_t max_function_index =
            std::min(reference_spans.size(), candidate_spans.size());
        repair_indices.reserve(max_function_index);
        for (std::size_t function_index = 1; function_index <= max_function_index;
             ++function_index) {
            if (std::find(repair_indices.begin(), repair_indices.end(), function_index) !=
                repair_indices.end()) {
                continue;
            }
            if (!Lua51FunctionBytecodeExactInChunk(original_chunk,
                                                  compiled_reference.chunk,
                                                  function_index) ||
                Lua51FunctionBytecodeExactInChunk(original_chunk,
                                                 compiled_candidate.chunk,
                                                 function_index)) {
                continue;
            }
            repair_indices.push_back(function_index);
        }

        bool pass_changed = false;
        for (const std::size_t function_index : repair_indices) {
            if (ShouldAbortCurrentDecompileRepair(note, note_prefix)) {
                break;
            }
            if (function_index == 0) {
                continue;
            }

            DecompiledFunctionSpan reference_span;
            DecompiledFunctionSpan candidate_span;
            bool have_reference_span = false;
            bool have_candidate_span = false;
            if (function_index <= reference_spans.size() &&
                function_index <= candidate_spans.size()) {
                reference_span = reference_spans[function_index - 1];
                candidate_span = candidate_spans[function_index - 1];
                have_reference_span = true;
                have_candidate_span = true;
            }

            std::string assigned_prefix;
            if (TryInferClosureAssignmentPrefix(original_chunk.main, function_index, &assigned_prefix)) {
                DecompiledFunctionSpan exact_reference_span;
                DecompiledFunctionSpan exact_candidate_span;
                if (TryFindFunctionSpanByPrefix(reference_lines,
                                               assigned_prefix,
                                               &exact_reference_span) &&
                    TryFindFunctionSpanByPrefix(candidate_lines,
                                               assigned_prefix,
                                               &exact_candidate_span)) {
                    reference_span = std::move(exact_reference_span);
                    candidate_span = std::move(exact_candidate_span);
                    have_reference_span = true;
                    have_candidate_span = true;
                }
            }

            if (!have_reference_span || !have_candidate_span) {
                continue;
            }

            if (reference_span.start_line > reference_span.end_line ||
                reference_span.end_line >= reference_lines.size() ||
                candidate_span.start_line > candidate_span.end_line ||
                candidate_span.end_line >= candidate_lines.size()) {
                continue;
            }

            const bool reference_exact =
                Lua51FunctionBytecodeExactInChunk(original_chunk,
                                                 compiled_reference.chunk,
                                                 function_index);
            const bool reference_improved =
                IsLuaFunctionStatsImproved(original_chunk,
                                           compiled_candidate.chunk,
                                           compiled_reference.chunk,
                                           function_index);
            if ((require_reference_exact && !reference_exact) ||
                (!require_reference_exact && !reference_exact && !reference_improved)) {
                continue;
            }

            std::vector<std::string> reference_function_lines(
                reference_lines.begin() +
                    static_cast<std::ptrdiff_t>(reference_span.start_line),
                reference_lines.begin() +
                    static_cast<std::ptrdiff_t>(reference_span.end_line + 1));
            if (reference_function_lines.empty()) {
                continue;
            }

            const std::string reference_function_source =
                JoinLines(reference_function_lines, true);
            if (HasBlockingRepairDonorSource(reference_function_source)) {
                continue;
            }

            const std::size_t function_pos = reference_function_lines.front().find("function");
            if (function_pos == std::string::npos) {
                continue;
            }

            std::vector<std::string> replacement_lines = reference_function_lines;
            replacement_lines.front() =
                candidate_span.prefix + replacement_lines.front().substr(function_pos);
            replacement_lines.back() += candidate_span.suffix;

            std::vector<std::string> transformed_lines = candidate_lines;
            transformed_lines.erase(
                transformed_lines.begin() + static_cast<std::ptrdiff_t>(candidate_span.start_line),
                transformed_lines.begin() + static_cast<std::ptrdiff_t>(candidate_span.end_line + 1));
            transformed_lines.insert(
                transformed_lines.begin() + static_cast<std::ptrdiff_t>(candidate_span.start_line),
                replacement_lines.begin(),
                replacement_lines.end());

            const std::string transformed_source =
                JoinLines(transformed_lines, candidate_trailing_newline);
            refii::lua::audit::CompiledLuaChunkInfo transformed_chunk;
            if (!CompileLuaSourceToChunkCached(
                    transformed_source,
                    "bytecode_parity_candidate_reference_repaired",
                    &transformed_chunk) ||
                !transformed_chunk.chunk_parsed) {
                continue;
            }

            const bool before_exact =
                Lua51FunctionBytecodeExactInChunk(original_chunk,
                                                 compiled_candidate.chunk,
                                                 function_index);
            const bool after_exact =
                Lua51FunctionBytecodeExactInChunk(original_chunk,
                                                 transformed_chunk.chunk,
                                                 function_index);
            const std::vector<std::size_t> transformed_mismatch_indices =
                CollectLuaMismatchedFunctionIndices(original_chunk, transformed_chunk.chunk);
            const bool target_mismatch_cleared =
                std::find(transformed_mismatch_indices.begin(),
                          transformed_mismatch_indices.end(),
                          function_index) == transformed_mismatch_indices.end();
            const bool mismatch_set_improved =
                transformed_mismatch_indices.size() < mismatch_indices.size();
            if (!(after_exact && !before_exact) &&
                !(target_mismatch_cleared &&
                  transformed_mismatch_indices.size() <= mismatch_indices.size()) &&
                !mismatch_set_improved &&
                !IsLuaFunctionStatsImproved(original_chunk,
                                           compiled_candidate.chunk,
                                           transformed_chunk.chunk,
                                           function_index)) {
                continue;
            }

            *candidate_source = transformed_source;
            if (note != nullptr && !note_prefix.empty()) {
                *note = JoinNotes(
                    *note,
                    std::string(note_prefix) + "_" +
                        (reference_exact ? "bytecode_reference_exact_function_restored_idx="
                                         : "bytecode_reference_function_restored_idx=") +
                        std::to_string(function_index));
            }
            changed = true;
            pass_changed = true;
            break;
        }

        if (!pass_changed) {
            std::unordered_map<std::string, DecompiledFunctionSpan> reference_spans_by_prefix;
            std::unordered_map<std::string, DecompiledFunctionSpan> candidate_spans_by_prefix;
            for (const DecompiledFunctionSpan& span : reference_spans) {
                std::string assigned_prefix;
                if (TryExtractAssignedPrefixFromSpanHeader(reference_lines, span, &assigned_prefix) &&
                    !assigned_prefix.empty()) {
                    reference_spans_by_prefix.emplace(std::move(assigned_prefix), span);
                }
            }
            for (const DecompiledFunctionSpan& span : candidate_spans) {
                std::string assigned_prefix;
                if (TryExtractAssignedPrefixFromSpanHeader(candidate_lines, span, &assigned_prefix) &&
                    !assigned_prefix.empty()) {
                    candidate_spans_by_prefix.emplace(std::move(assigned_prefix), span);
                }
            }

            for (const auto& [assigned_prefix, reference_span] : reference_spans_by_prefix) {
                if (ShouldAbortCurrentDecompileRepair(note, note_prefix)) {
                    break;
                }
                const auto candidate_it = candidate_spans_by_prefix.find(assigned_prefix);
                if (candidate_it == candidate_spans_by_prefix.end()) {
                    continue;
                }

                const DecompiledFunctionSpan& candidate_span = candidate_it->second;
                if (reference_span.start_line > reference_span.end_line ||
                    reference_span.end_line >= reference_lines.size() ||
                    candidate_span.start_line > candidate_span.end_line ||
                    candidate_span.end_line >= candidate_lines.size()) {
                    continue;
                }

                std::vector<std::string> replacement_lines(
                    reference_lines.begin() +
                        static_cast<std::ptrdiff_t>(reference_span.start_line),
                    reference_lines.begin() +
                        static_cast<std::ptrdiff_t>(reference_span.end_line + 1));
                if (replacement_lines.empty()) {
                    continue;
                }

                const std::size_t function_pos = replacement_lines.front().find("function");
                if (function_pos == std::string::npos) {
                    continue;
                }

                replacement_lines.front() =
                    candidate_span.prefix + replacement_lines.front().substr(function_pos);
                replacement_lines.back() += candidate_span.suffix;

                std::vector<std::string> transformed_lines = candidate_lines;
                transformed_lines.erase(
                    transformed_lines.begin() +
                        static_cast<std::ptrdiff_t>(candidate_span.start_line),
                    transformed_lines.begin() +
                        static_cast<std::ptrdiff_t>(candidate_span.end_line + 1));
                transformed_lines.insert(
                    transformed_lines.begin() +
                        static_cast<std::ptrdiff_t>(candidate_span.start_line),
                    replacement_lines.begin(),
                    replacement_lines.end());

                const std::string transformed_source =
                    JoinLines(transformed_lines, candidate_trailing_newline);
                refii::lua::audit::CompiledLuaChunkInfo transformed_chunk;
                if (!CompileLuaSourceToChunkCached(
                        transformed_source,
                        "bytecode_parity_candidate_reference_named_repaired",
                        &transformed_chunk) ||
                    !transformed_chunk.chunk_parsed) {
                    continue;
                }

                const std::vector<std::size_t> transformed_mismatch_indices =
                    CollectLuaMismatchedFunctionIndices(original_chunk, transformed_chunk.chunk);
                if (transformed_mismatch_indices.size() >= mismatch_indices.size()) {
                    continue;
                }

                *candidate_source = transformed_source;
                if (note != nullptr && !note_prefix.empty()) {
                    *note = JoinNotes(*note,
                                      std::string(note_prefix) +
                                          "_bytecode_reference_named_function_restored=" +
                                          assigned_prefix);
                }
                changed = true;
                pass_changed = true;
                break;
            }
        }

        if (!pass_changed) {
            break;
        }
    }

    return changed;
}

}  // namespace refii::lua::internal
