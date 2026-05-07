#include "tools/decompiler/rendered/reported_function_bytecode_repairs.h"

#include "tools/decompiler/rendered/repair_progress_helpers.h"
#include "tools/decompiler/rendered/repair_state_keys.h"
#include "tools/decompiler/rendered/rendered_lua_text.h"
#include "tools/decompiler/repair/lua_decompile_repair_context.h"
#include "tools/decompiler/repair/lua_decompiled_function_spans.h"
#include "tools/decompiler/lua_decompiler_internal.h"
#include "tools/native/straight_line_native_render.h"

#include <algorithm>
#include <string>
#include <string_view>
#include <unordered_map>
#include <vector>

namespace refii::lua::internal {

namespace {

void AppendNoProgressRepairNote(std::string* note, std::string_view key) {
    if (note == nullptr || key.empty()) {
        return;
    }
    *note = JoinNotes(*note, std::string(key));
}

}  // namespace

bool TryRepairReportedFunctionFromBytecode(
    const std::vector<char>& input_bytes,
    std::string& decompiled_source,
    std::string_view validation_error,
    std::string* note,
    std::string_view note_prefix,
    const ReportedFunctionBytecodeRepairOptions& options) {
    if (!IsLuaBytecode(input_bytes) || validation_error.empty()) {
        return false;
    }

    const std::size_t reported_line = ExtractLuaErrorLine(validation_error);
    const std::size_t open_construct_line = ExtractLuaOpenConstructLine(validation_error);
    if (reported_line == 0 && open_construct_line == 0) {
        return false;
    }

    std::vector<std::size_t> target_lines;
    if (reported_line != 0) {
        target_lines.push_back(reported_line);
    }
    if (open_construct_line != 0 &&
        std::find(target_lines.begin(), target_lines.end(), open_construct_line) ==
            target_lines.end()) {
        target_lines.push_back(open_construct_line);
    }

    refii::lua::Lua51Chunk chunk;
    std::string parse_error;
    if (!refii::lua::ParseLua51Chunk(input_bytes, &chunk, &parse_error)) {
        if (note != nullptr && !parse_error.empty()) {
            *note = JoinNotes(*note,
                              std::string(note_prefix) +
                                  "_reported_function_parse_failed=" + parse_error);
        }
        return false;
    }

    bool had_trailing_newline = false;
    const std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);
    const std::vector<DecompiledFunctionSpan> spans = CollectDecompiledFunctionSpans(lines);
    const std::unordered_map<std::string, DecompiledFunctionSpan> prefix_span_map =
        BuildAssignedPrefixSpanMap(lines, spans);
    std::vector<const refii::lua::Lua51Function*> functions;
    std::vector<std::size_t> function_root_child_indices;
    AppendLuaFunctionChildrenPreorderWithRootChildIndex(chunk.main,
                                                        0,
                                                        &functions,
                                                        &function_root_child_indices);
    if (spans.empty() || functions.empty()) {
        return false;
    }

    struct CandidateSpan {
        std::size_t function_index = 0;
        DecompiledFunctionSpan span;
        bool contains_open_construct = false;
        bool contains_reported_line = false;
        std::size_t distance_to_target = 0;
    };

    std::vector<CandidateSpan> candidates;
    candidates.reserve(functions.size());
    for (std::size_t index = 0; index < functions.size(); ++index) {
        DecompiledFunctionSpan span;
        if (!TryResolveFunctionSpanForIndex(chunk.main,
                                            index + 1,
                                            function_root_child_indices[index],
                                            lines,
                                            spans,
                                            &prefix_span_map,
                                            &span)) {
            continue;
        }
        if (span.start_line > span.end_line || span.end_line >= lines.size()) {
            continue;
        }

        const std::size_t span_start_line = span.start_line + 1;
        const std::size_t span_end_line = span.end_line + 1;
        bool targets_span = false;
        for (const std::size_t target_line : target_lines) {
            if (target_line + 1 >= span_start_line && target_line <= span_end_line + 2) {
                targets_span = true;
                break;
            }
        }
        if (!targets_span) {
            continue;
        }

        const auto contains_one_based_line = [&](std::size_t one_based_line) {
            if (one_based_line == 0) {
                return false;
            }
            return one_based_line + 1 >= span_start_line && one_based_line <= span_end_line + 2;
        };

        std::size_t primary_target = open_construct_line != 0 ? open_construct_line : reported_line;
        std::size_t distance_to_target = 0;
        if (primary_target != 0) {
            if (primary_target < span_start_line) {
                distance_to_target = span_start_line - primary_target;
            } else if (primary_target > span_end_line + 2) {
                distance_to_target = primary_target - (span_end_line + 2);
            }
        }

        CandidateSpan candidate;
        candidate.function_index = index + 1;
        candidate.span = span;
        candidate.contains_open_construct = contains_one_based_line(open_construct_line);
        candidate.contains_reported_line = contains_one_based_line(reported_line);
        candidate.distance_to_target = distance_to_target;
        candidates.push_back(std::move(candidate));
    }

    if (candidates.empty()) {
        return false;
    }

    std::sort(candidates.begin(), candidates.end(), [](const CandidateSpan& lhs,
                                                       const CandidateSpan& rhs) {
        if (lhs.contains_open_construct != rhs.contains_open_construct) {
            return lhs.contains_open_construct > rhs.contains_open_construct;
        }
        if (lhs.contains_reported_line != rhs.contains_reported_line) {
            return lhs.contains_reported_line > rhs.contains_reported_line;
        }
        if (lhs.distance_to_target != rhs.distance_to_target) {
            return lhs.distance_to_target < rhs.distance_to_target;
        }
        const std::size_t lhs_span =
            lhs.span.end_line >= lhs.span.start_line ? lhs.span.end_line - lhs.span.start_line : 0;
        const std::size_t rhs_span =
            rhs.span.end_line >= rhs.span.start_line ? rhs.span.end_line - rhs.span.start_line : 0;
        if (lhs_span != rhs_span) {
            return lhs_span < rhs_span;
        }
        return lhs.function_index < rhs.function_index;
    });

    const DecompiledMarkerSummary baseline_marker_summary =
        SummarizeDecompilerMarkers(decompiled_source);
    std::string best_candidate;
    std::string best_error;
    bool best_exact = false;
    std::size_t best_function_index = 0;

    for (const CandidateSpan& candidate_span : candidates) {
        std::string rendered_function;
        std::string render_error;
        if (!refii::lua::RenderLua51StraightLineFunction(
                *functions[candidate_span.function_index - 1], &rendered_function, &render_error)) {
            continue;
        }

        bool rendered_trailing_newline = false;
        std::vector<std::string> replacement_lines =
            SplitLines(rendered_function, &rendered_trailing_newline);
        if (replacement_lines.empty()) {
            continue;
        }

        if (replacement_lines.front().find("function") != std::string::npos &&
            !replacement_lines.front().starts_with(candidate_span.span.prefix)) {
            const std::size_t function_pos = replacement_lines.front().find("function");
            if (function_pos != std::string::npos) {
                replacement_lines.front() =
                    candidate_span.span.prefix + replacement_lines.front().substr(function_pos);
                replacement_lines.back() += candidate_span.span.suffix;
            }
        }

        const std::vector<std::string> current_function_lines(
            lines.begin() + static_cast<std::ptrdiff_t>(candidate_span.span.start_line),
            lines.begin() + static_cast<std::ptrdiff_t>(candidate_span.span.end_line + 1));
        const bool exact_in_isolation =
            options.function_source_bytecode_exact != nullptr &&
            options.function_source_bytecode_exact(chunk,
                                                   candidate_span.function_index,
                                                   replacement_lines);
        const bool improved_in_isolation =
            options.function_source_improved_in_isolation != nullptr &&
            options.function_source_improved_in_isolation(chunk,
                                                          candidate_span.function_index,
                                                          current_function_lines,
                                                          replacement_lines);
        if (!exact_in_isolation && !improved_in_isolation) {
            continue;
        }

        std::vector<std::string> candidate_lines = lines;
        candidate_lines.erase(
            candidate_lines.begin() +
                static_cast<std::ptrdiff_t>(candidate_span.span.start_line),
            candidate_lines.begin() +
                static_cast<std::ptrdiff_t>(candidate_span.span.end_line + 1));
        candidate_lines.insert(
            candidate_lines.begin() +
                static_cast<std::ptrdiff_t>(candidate_span.span.start_line),
            replacement_lines.begin(),
            replacement_lines.end());

        std::string candidate_source = JoinLines(candidate_lines, had_trailing_newline);
        const std::string candidate_error =
            ValidateLuaSource(candidate_source, "native_candidate");
        if (candidate_error.empty()) {
            decompiled_source = std::move(candidate_source);
            if (note != nullptr) {
                *note = JoinNotes(
                    *note,
                    std::string(note_prefix) +
                        (exact_in_isolation
                             ? "_reported_function_bytecode_exact_idx="
                             : "_reported_function_bytecode_improved_idx=") +
                        std::to_string(candidate_span.function_index));
            }
            return true;
        }

        const DecompiledMarkerSummary candidate_marker_summary =
            SummarizeDecompilerMarkers(candidate_source);
        const bool acceptable_progress =
            IsValidationOutcomeBetter(validation_error, candidate_error) ||
            IsDecompilerMarkerSummaryBetter(baseline_marker_summary, candidate_marker_summary);
        if (!acceptable_progress) {
            continue;
        }

        if (!best_candidate.empty()) {
            const bool better_exact = exact_in_isolation && !best_exact;
            const bool better_validation =
                IsValidationOutcomeBetter(best_error, candidate_error);
            if (!better_exact && !better_validation) {
                continue;
            }
        }

        best_candidate = std::move(candidate_source);
        best_error = candidate_error;
        best_exact = exact_in_isolation;
        best_function_index = candidate_span.function_index;
    }

    if (best_candidate.empty()) {
        return false;
    }

    decompiled_source = std::move(best_candidate);
    if (note != nullptr) {
        *note = JoinNotes(
            *note,
            std::string(note_prefix) +
                (best_exact ? "_reported_function_bytecode_exact_progress_idx="
                            : "_reported_function_bytecode_improved_progress_idx=") +
                std::to_string(best_function_index));
    }
    return true;
}

bool TryRepairReportedFunctionsFromBytecodeIteratively(
    const std::vector<char>& input_bytes,
    std::string& decompiled_source,
    std::string* validation_error,
    std::string* note,
    std::string_view note_prefix,
    const ReportedFunctionBytecodeRepairOptions& options) {
    if (validation_error == nullptr || validation_error->empty()) {
        return false;
    }

    bool changed = false;
    std::string previous_state;
    const int max_passes = GetCurrentRepairAttemptCap(6, 3);
    for (int pass = 0; pass < max_passes; ++pass) {
        if (ShouldAbortCurrentDecompileRepair(note, note_prefix)) {
            break;
        }
        const std::string before_state =
            BuildRepairQualityStateKey(decompiled_source, *validation_error);
        if (before_state == previous_state) {
            AppendNoProgressRepairNote(
                note,
                std::string(note_prefix) + "_reported_function_bytecode_no_progress");
            break;
        }

        if (!TryRepairReportedFunctionFromBytecode(input_bytes,
                                                   decompiled_source,
                                                   *validation_error,
                                                   note,
                                                   note_prefix,
                                                   options)) {
            break;
        }

        changed = true;
        *validation_error = ValidateLuaSource(decompiled_source, "native_candidate");
        const std::string after_state =
            BuildRepairQualityStateKey(decompiled_source, *validation_error);
        if (after_state == before_state || after_state == previous_state) {
            AppendNoProgressRepairNote(
                note,
                std::string(note_prefix) + "_reported_function_bytecode_no_progress");
            break;
        }
        if (validation_error->empty()) {
            break;
        }
        previous_state = std::move(after_state);
    }

    return changed;
}

}  // namespace refii::lua::internal
