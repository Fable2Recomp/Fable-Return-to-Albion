#include "tools/decompiler/lua_decompiler_internal.h"

#include <cstddef>
#include <string>
#include <string_view>
#include <vector>

namespace refii::lua::internal {
namespace {

std::string_view TrimOwnerRepairLine(std::string_view line) {
    return TrimWhitespace(line);
}

std::string GetLuaIndentAt(const std::vector<std::string>& lines, std::size_t index) {
    return index < lines.size() ? GetLuaLineIndent(lines[index]) : std::string();
}

std::size_t FindNextOwnerSignificantLuaLine(const std::vector<std::string>& lines,
                                            std::size_t start) {
    for (std::size_t probe = start; probe < lines.size(); ++probe) {
        const std::string_view trimmed = TrimOwnerRepairLine(lines[probe]);
        if (!trimmed.empty() && !StartsWith(trimmed, "--")) {
            return probe;
        }
    }
    return lines.size();
}

std::size_t FindPreviousOwnerSignificantLuaLine(const std::vector<std::string>& lines,
                                                std::size_t start) {
    if (start == 0 || start > lines.size()) {
        return lines.size();
    }
    for (std::size_t probe = start; probe-- > 0;) {
        const std::string_view trimmed = TrimOwnerRepairLine(lines[probe]);
        if (!trimmed.empty() && !StartsWith(trimmed, "--")) {
            return probe;
        }
        if (probe == 0) {
            break;
        }
    }
    return lines.size();
}

std::string DedentLuaIndent(std::string indent) {
    if (indent.empty()) {
        return indent;
    }
    if (indent.back() == '\t') {
        indent.pop_back();
        return indent;
    }
    if (indent.size() >= 2 && indent.substr(indent.size() - 2) == "  ") {
        indent.resize(indent.size() - 2);
        return indent;
    }
    indent.pop_back();
    return indent;
}

bool IsMissingEndWarningLine(std::string_view line) {
    return line.find("missing end command somewhere! Added here") != std::string_view::npos;
}

bool TryStripRedundantEndBeforeStateElseIfImpl(std::string& decompiled_source,
                                               std::string* note) {
    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);

    bool changed = false;
    for (std::size_t i = 0; i + 1 < lines.size(); ++i) {
        if (TrimWhitespace(lines[i]) != "end") {
            continue;
        }

        const std::string current_indent = GetLuaIndentAt(lines, i);
        std::size_t next = FindNextOwnerSignificantLuaLine(lines, i + 1);
        if (next < lines.size() && TrimWhitespace(lines[next]) == "end" &&
            GetLuaIndentAt(lines, next) == current_indent) {
            const std::size_t after_next = FindNextOwnerSignificantLuaLine(lines, next + 1);
            if (after_next < lines.size()) {
                const std::string_view after_next_trimmed = TrimWhitespace(lines[after_next]);
                if (StartsWith(after_next_trimmed, "elseif ") &&
                    after_next_trimmed.find("CurrentState") != std::string_view::npos &&
                    GetLuaIndentAt(lines, after_next).size() < current_indent.size()) {
                    lines.erase(lines.begin() + static_cast<std::ptrdiff_t>(next));
                    changed = true;
                    if (i != 0) {
                        --i;
                    }
                    continue;
                }
            }
        }
        if (next >= lines.size()) {
            continue;
        }

        const std::string_view next_trimmed = TrimWhitespace(lines[next]);
        if (!StartsWith(next_trimmed, "elseif ") ||
            next_trimmed.find("CurrentState") == std::string_view::npos) {
            continue;
        }

        const std::size_t previous = FindPreviousOwnerSignificantLuaLine(lines, i);
        if (previous >= lines.size() || TrimWhitespace(lines[previous]) != "end") {
            continue;
        }

        const std::string previous_indent = GetLuaIndentAt(lines, previous);
        const std::string next_indent = GetLuaIndentAt(lines, next);
        const bool same_owner_redundant_end =
            current_indent == next_indent && previous_indent.size() > current_indent.size();
        const bool nested_redundant_end_before_owner_elseif =
            previous_indent == current_indent && next_indent.size() < current_indent.size();
        if (!same_owner_redundant_end && !nested_redundant_end_before_owner_elseif) {
            continue;
        }

        lines.erase(lines.begin() + static_cast<std::ptrdiff_t>(i));
        changed = true;
        if (i != 0) {
            --i;
        }
    }

    if (!changed) {
        return false;
    }

    decompiled_source = JoinLines(lines, had_trailing_newline);
    if (note != nullptr) {
        *note = JoinNotes(*note, "syntax_repaired_stripped_redundant_end_before_state_elseif");
    }
    return true;
}

bool TryStripWarningGuardedExtraEndBeforeTopLevelBoundaryImpl(std::string& decompiled_source,
                                                              std::string* note) {
    const std::string validation_error = ValidateLuaRepairSource(decompiled_source);
    if (validation_error.empty()) {
        return false;
    }
    const std::size_t validation_error_line = ExtractLuaErrorLine(validation_error);

    bool had_trailing_newline = false;
    const std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);

    std::string best_candidate;
    std::string best_error;
    for (std::size_t i = 2; i < lines.size(); ++i) {
        if (TrimWhitespace(lines[i]) != "end" || TrimWhitespace(lines[i - 1]) != "end" ||
            !IsMissingEndWarningLine(lines[i - 2])) {
            continue;
        }

        const std::size_t next = FindNextOwnerSignificantLuaLine(lines, i + 1);
        if (next >= lines.size() || !GetLuaIndentAt(lines, next).empty() ||
            !LooksLikeTopLevelBoundary(TrimWhitespace(lines[next]))) {
            continue;
        }

        std::vector<std::string> candidate_lines = lines;
        candidate_lines.erase(candidate_lines.begin() + static_cast<std::ptrdiff_t>(i));
        std::string candidate = JoinLines(candidate_lines, had_trailing_newline);
        const std::string candidate_error = ValidateLuaRepairSource(candidate);
        const std::size_t candidate_error_line = ExtractLuaErrorLine(candidate_error);
        const bool candidate_pushes_error_later =
            validation_error_line != 0 && candidate_error_line != 0 &&
            candidate_error_line > validation_error_line;
        if (candidate_error.empty()) {
            decompiled_source = std::move(candidate);
            if (note != nullptr) {
                *note = JoinNotes(
                    *note,
                    "syntax_repaired_stripped_warning_guarded_end_before_top_level_boundary");
            }
            return true;
        }

        if (!IsValidationOutcomeBetter(validation_error, candidate_error) &&
            !candidate_pushes_error_later) {
            continue;
        }
        const std::size_t best_error_line = ExtractLuaErrorLine(best_error);
        const bool candidate_beats_best_by_line =
            best_error_line != 0 && candidate_error_line != 0 &&
            candidate_error_line > best_error_line;
        if (!best_candidate.empty() &&
            !IsValidationOutcomeBetter(best_error, candidate_error) &&
            !candidate_beats_best_by_line) {
            continue;
        }

        best_candidate = std::move(candidate);
        best_error = candidate_error;
    }

    if (best_candidate.empty()) {
        return false;
    }

    decompiled_source = std::move(best_candidate);
    if (note != nullptr) {
        *note = JoinNotes(
            *note,
            "syntax_repaired_stripped_warning_guarded_end_before_top_level_boundary_progress");
    }
    return true;
}

bool TryStripRedundantEndBeforeSameIndentContinuationImpl(std::string& decompiled_source,
                                                          std::string* note) {
    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);

    bool changed = false;
    for (std::size_t i = 0; i < lines.size(); ++i) {
        if (TrimWhitespace(lines[i]) != "end") {
            continue;
        }

        const std::size_t previous = FindPreviousOwnerSignificantLuaLine(lines, i);
        const std::size_t next = FindNextOwnerSignificantLuaLine(lines, i + 1);
        if (previous >= lines.size() || next >= lines.size() ||
            TrimWhitespace(lines[previous]) != "end") {
            continue;
        }

        const std::string current_indent = GetLuaIndentAt(lines, i);
        const std::string next_indent = GetLuaIndentAt(lines, next);
        if (current_indent.empty() || GetLuaIndentAt(lines, previous) != current_indent) {
            continue;
        }

        const std::string_view next_trimmed = TrimWhitespace(lines[next]);
        const bool next_is_top_level_boundary =
            LooksLikeTopLevelBoundary(next_trimmed);
        const bool same_indent_continuation =
            next_indent == current_indent && next_trimmed != "end" && next_trimmed != "else" &&
            !StartsWith(next_trimmed, "elseif ") && !StartsWith(next_trimmed, "until ");
        const bool owner_branch_continuation =
            next_trimmed != "end" && !StartsWith(next_trimmed, "until ") &&
            (next_trimmed == "else" || StartsWith(next_trimmed, "elseif ")) &&
            next_indent.size() <= current_indent.size();
        if (!same_indent_continuation && !owner_branch_continuation) {
            continue;
        }
        if (same_indent_continuation && next_is_top_level_boundary) {
            continue;
        }

        lines.erase(lines.begin() + static_cast<std::ptrdiff_t>(i));
        changed = true;
        if (i != 0) {
            --i;
        }
    }

    if (!changed) {
        return false;
    }

    decompiled_source = JoinLines(lines, had_trailing_newline);
    if (note != nullptr) {
        *note = JoinNotes(*note, "syntax_repaired_stripped_redundant_end_before_continuation");
    }
    return true;
}

bool TryStripDuplicateOwnerEndAfterSameIndentIfImpl(std::string& decompiled_source,
                                                    std::string* note) {
    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);

    bool changed = false;
    for (std::size_t i = 0; i < lines.size(); ++i) {
        std::string ignored_condition;
        if (!ParseLuaIfCondition(lines[i], &ignored_condition)) {
            continue;
        }

        const std::string header_indent = GetLuaIndentAt(lines, i);
        if (header_indent.empty()) {
            continue;
        }

        const std::size_t body_start = FindNextOwnerSignificantLuaLine(lines, i + 1);
        if (body_start >= lines.size() || GetLuaIndentAt(lines, body_start) != header_indent) {
            continue;
        }

        std::size_t first_end = lines.size();
        bool saw_same_indent_body = false;
        for (std::size_t probe = body_start; probe < lines.size(); ++probe) {
            const std::string_view trimmed = TrimWhitespace(lines[probe]);
            if (trimmed.empty() || StartsWith(trimmed, "--")) {
                continue;
            }

            const std::string probe_indent = GetLuaIndentAt(lines, probe);
            if (probe_indent.size() < header_indent.size()) {
                break;
            }
            if (probe_indent.size() > header_indent.size()) {
                continue;
            }
            if (trimmed == "else" || StartsWith(trimmed, "elseif ")) {
                first_end = lines.size();
                break;
            }
            if (trimmed == "end") {
                first_end = probe;
                break;
            }
            saw_same_indent_body = true;
        }
        if (!saw_same_indent_body || first_end >= lines.size()) {
            continue;
        }

        const std::size_t second_end = FindNextOwnerSignificantLuaLine(lines, first_end + 1);
        if (second_end >= lines.size() || TrimWhitespace(lines[second_end]) != "end" ||
            GetLuaIndentAt(lines, second_end) != header_indent) {
            continue;
        }

        const std::size_t continuation = FindNextOwnerSignificantLuaLine(lines, second_end + 1);
        if (continuation >= lines.size()) {
            continue;
        }

        const std::string_view continuation_trimmed = TrimWhitespace(lines[continuation]);
        if ((continuation_trimmed != "else" && !StartsWith(continuation_trimmed, "elseif ")) ||
            GetLuaIndentAt(lines, continuation).size() >= header_indent.size()) {
            continue;
        }

        lines.erase(lines.begin() + static_cast<std::ptrdiff_t>(second_end));
        changed = true;
        if (i != 0) {
            --i;
        }
    }

    if (!changed) {
        return false;
    }

    decompiled_source = JoinLines(lines, had_trailing_newline);
    if (note != nullptr) {
        *note = JoinNotes(
            *note, "syntax_repaired_stripped_duplicate_owner_end_after_same_indent_if");
    }
    return true;
}

bool TryStripDuplicateEndRunBeforeOwnerContinuationImpl(std::string& decompiled_source,
                                                        std::string* note) {
    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);

    bool changed = false;
    for (std::size_t i = 0; i < lines.size(); ++i) {
        if (TrimWhitespace(lines[i]) != "end") {
            continue;
        }

        const std::size_t next = FindNextOwnerSignificantLuaLine(lines, i + 1);
        if (next >= lines.size()) {
            continue;
        }

        const std::string_view next_trimmed = TrimWhitespace(lines[next]);
        if (next_trimmed != "else" && !StartsWith(next_trimmed, "elseif ")) {
            continue;
        }

        const std::string current_indent = GetLuaIndentAt(lines, i);
        const std::string next_indent = GetLuaIndentAt(lines, next);
        if (current_indent.empty() || next_indent.size() > current_indent.size()) {
            continue;
        }

        const std::size_t previous = FindPreviousOwnerSignificantLuaLine(lines, i);
        if (previous >= lines.size() || TrimWhitespace(lines[previous]) != "end" ||
            GetLuaIndentAt(lines, previous) != current_indent) {
            continue;
        }

        lines.erase(lines.begin() + static_cast<std::ptrdiff_t>(i));
        changed = true;
        if (i != 0) {
            --i;
        }
    }

    if (!changed) {
        return false;
    }

    decompiled_source = JoinLines(lines, had_trailing_newline);
    if (note != nullptr) {
        *note = JoinNotes(*note, "syntax_repaired_stripped_duplicate_end_before_owner_branch");
    }
    return true;
}

bool TryStripSingleEndBeforeOwnerContinuationImpl(std::string& decompiled_source,
                                                  std::string* note) {
    const std::string validation_error = ValidateLuaRepairSource(decompiled_source);
    if (validation_error.empty()) {
        return false;
    }
    const std::size_t validation_error_line = ExtractLuaErrorLine(validation_error);

    bool had_trailing_newline = false;
    const std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);

    std::string best_candidate;
    std::string best_error;
    for (std::size_t i = 0; i < lines.size(); ++i) {
        if (TrimWhitespace(lines[i]) != "end") {
            continue;
        }

        const std::size_t next = FindNextOwnerSignificantLuaLine(lines, i + 1);
        const std::size_t previous = FindPreviousOwnerSignificantLuaLine(lines, i);
        if (next >= lines.size() || previous >= lines.size()) {
            continue;
        }

        const std::string_view next_trimmed = TrimWhitespace(lines[next]);
        if (next_trimmed != "else" && !StartsWith(next_trimmed, "elseif ")) {
            continue;
        }

        const std::string current_indent = GetLuaIndentAt(lines, i);
        const std::string previous_indent = GetLuaIndentAt(lines, previous);
        const std::string next_indent = GetLuaIndentAt(lines, next);
        if (current_indent.empty() || previous_indent.size() < current_indent.size() ||
            next_indent.size() > current_indent.size()) {
            continue;
        }

        const std::string_view previous_trimmed = TrimWhitespace(lines[previous]);
        if (previous_trimmed == "end" || previous_trimmed == "else" ||
            StartsWith(previous_trimmed, "elseif ")) {
            continue;
        }

        std::vector<std::string> candidate_lines = lines;
        candidate_lines.erase(candidate_lines.begin() + static_cast<std::ptrdiff_t>(i));
        std::string candidate = JoinLines(candidate_lines, had_trailing_newline);
        const std::string candidate_error = ValidateLuaRepairSource(candidate);
        const std::size_t candidate_error_line = ExtractLuaErrorLine(candidate_error);
        const bool candidate_pushes_error_later =
            validation_error_line != 0 && candidate_error_line != 0 &&
            candidate_error_line > validation_error_line;
        if (candidate_error.empty()) {
            decompiled_source = std::move(candidate);
            if (note != nullptr) {
                *note = JoinNotes(*note,
                                  "syntax_repaired_stripped_single_end_before_owner_branch");
            }
            return true;
        }

        if (!IsValidationOutcomeBetter(validation_error, candidate_error) &&
            !candidate_pushes_error_later) {
            continue;
        }
        const std::size_t best_error_line = ExtractLuaErrorLine(best_error);
        const bool candidate_beats_best_by_line =
            best_error_line != 0 && candidate_error_line != 0 &&
            candidate_error_line > best_error_line;
        if (!best_candidate.empty() &&
            !IsValidationOutcomeBetter(best_error, candidate_error) &&
            !candidate_beats_best_by_line) {
            continue;
        }

        best_candidate = std::move(candidate);
        best_error = candidate_error;
    }

    if (best_candidate.empty()) {
        return false;
    }

    decompiled_source = std::move(best_candidate);
    if (note != nullptr) {
        *note = JoinNotes(*note,
                          "syntax_repaired_stripped_single_end_before_owner_branch_progress");
    }
    return true;
}

bool TryReopenMisclosedEndBeforeDuplicatedElseTailImpl(std::string& decompiled_source,
                                                       std::string* note) {
    const std::string validation_error = ValidateLuaRepairSource(decompiled_source);
    if (validation_error.empty()) {
        return false;
    }
    const std::size_t validation_error_line = ExtractLuaErrorLine(validation_error);

    bool had_trailing_newline = false;
    const std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);

    std::string best_candidate;
    std::string best_error;
    for (std::size_t i = 0; i < lines.size(); ++i) {
        if (TrimWhitespace(lines[i]) != "end") {
            continue;
        }

        const std::string current_indent = GetLuaIndentAt(lines, i);
        if (current_indent.empty()) {
            continue;
        }

        const std::size_t first_else = FindNextOwnerSignificantLuaLine(lines, i + 1);
        const std::size_t second_else =
            first_else < lines.size() ? FindNextOwnerSignificantLuaLine(lines, first_else + 1)
                                      : lines.size();
        const std::size_t body_start =
            second_else < lines.size() ? FindNextOwnerSignificantLuaLine(lines, second_else + 1)
                                       : lines.size();
        if (first_else >= lines.size() || second_else >= lines.size() ||
            body_start >= lines.size()) {
            continue;
        }

        if (TrimWhitespace(lines[first_else]) != "else" ||
            TrimWhitespace(lines[second_else]) != "else") {
            continue;
        }
        const std::string first_else_indent = GetLuaIndentAt(lines, first_else);
        const std::string second_else_indent = GetLuaIndentAt(lines, second_else);
        const std::string body_start_indent = GetLuaIndentAt(lines, body_start);
        if (first_else_indent != current_indent ||
            second_else_indent.size() <= current_indent.size() ||
            !body_start_indent.starts_with(second_else_indent)) {
            continue;
        }

        bool found_owner = false;
        for (std::size_t probe = i; probe-- > 0;) {
            const std::string_view probe_trimmed = TrimWhitespace(lines[probe]);
            if (probe_trimmed.empty() || StartsWith(probe_trimmed, "--")) {
                if (probe == 0) {
                    break;
                }
                continue;
            }
            const std::string probe_indent = GetLuaIndentAt(lines, probe);
            if (probe_indent.size() < current_indent.size()) {
                break;
            }
            if (probe_indent == current_indent &&
                (StartsWith(probe_trimmed, "if ") || StartsWith(probe_trimmed, "elseif "))) {
                found_owner = true;
                break;
            }
        }
        if (!found_owner) {
            continue;
        }

        std::vector<std::string> candidate_lines = lines;
        candidate_lines[i] = current_indent + "else";
        candidate_lines.erase(candidate_lines.begin() + static_cast<std::ptrdiff_t>(first_else),
                              candidate_lines.begin() + static_cast<std::ptrdiff_t>(second_else + 1));
        std::string candidate = JoinLines(candidate_lines, had_trailing_newline);
        const std::string candidate_error = ValidateLuaRepairSource(candidate);
        const std::size_t candidate_error_line = ExtractLuaErrorLine(candidate_error);
        const bool candidate_pushes_error_later =
            validation_error_line != 0 && candidate_error_line != 0 &&
            candidate_error_line > validation_error_line;
        if (candidate_error.empty()) {
            decompiled_source = std::move(candidate);
            if (note != nullptr) {
                *note = JoinNotes(*note,
                                  "syntax_repaired_reopened_misclosed_end_before_else_tail");
            }
            return true;
        }

        if (!IsValidationOutcomeBetter(validation_error, candidate_error) &&
            !candidate_pushes_error_later) {
            continue;
        }
        const std::size_t best_error_line = ExtractLuaErrorLine(best_error);
        const bool candidate_beats_best_by_line =
            best_error_line != 0 && candidate_error_line != 0 &&
            candidate_error_line > best_error_line;
        if (!best_candidate.empty() &&
            !IsValidationOutcomeBetter(best_error, candidate_error) &&
            !candidate_beats_best_by_line) {
            continue;
        }

        best_candidate = std::move(candidate);
        best_error = candidate_error;
    }

    if (best_candidate.empty()) {
        return false;
    }

    decompiled_source = std::move(best_candidate);
    if (note != nullptr) {
        *note = JoinNotes(
            *note,
            "syntax_repaired_reopened_misclosed_end_before_else_tail_progress");
    }
    return true;
}

bool TryReopenMisclosedEndRunBeforeElseImpl(std::string& decompiled_source,
                                            std::string* note) {
    const std::string validation_error = ValidateLuaRepairSource(decompiled_source);
    if (validation_error.empty()) {
        return false;
    }
    const std::size_t validation_error_line = ExtractLuaErrorLine(validation_error);

    bool had_trailing_newline = false;
    const std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);

    std::string best_candidate;
    std::string best_error;
    for (std::size_t i = 0; i < lines.size(); ++i) {
        const std::string_view branch_trimmed = TrimWhitespace(lines[i]);
        if (branch_trimmed != "else" && !StartsWith(branch_trimmed, "elseif ")) {
            continue;
        }

        const std::string branch_indent = GetLuaIndentAt(lines, i);
        if (branch_indent.empty()) {
            continue;
        }

        const std::size_t body_start = FindNextOwnerSignificantLuaLine(lines, i + 1);
        if (body_start >= lines.size()) {
            continue;
        }
        const std::string body_indent = GetLuaIndentAt(lines, body_start);
        if (body_indent.size() <= branch_indent.size() || !body_indent.starts_with(branch_indent)) {
            continue;
        }

        std::vector<std::size_t> end_run;
        std::size_t probe = FindPreviousOwnerSignificantLuaLine(lines, i);
        while (probe < lines.size()) {
            if (TrimWhitespace(lines[probe]) != "end" || GetLuaIndentAt(lines, probe) != branch_indent) {
                break;
            }
            end_run.push_back(probe);
            probe = FindPreviousOwnerSignificantLuaLine(lines, probe);
        }
        if (end_run.empty()) {
            continue;
        }
        std::reverse(end_run.begin(), end_run.end());

        bool found_owner = false;
        for (std::size_t probe_index = end_run.front(); probe_index-- > 0;) {
            const std::string_view probe_trimmed = TrimWhitespace(lines[probe_index]);
            if (probe_trimmed.empty() || StartsWith(probe_trimmed, "--")) {
                if (probe_index == 0) {
                    break;
                }
                continue;
            }
            const std::string probe_indent = GetLuaIndentAt(lines, probe_index);
            if (probe_indent.size() < branch_indent.size()) {
                break;
            }
            if (probe_indent == branch_indent &&
                (StartsWith(probe_trimmed, "if ") || StartsWith(probe_trimmed, "elseif "))) {
                found_owner = true;
                break;
            }
        }
        if (!found_owner) {
            continue;
        }

        const auto consider_candidate =
            [&](std::size_t replace_index, std::size_t keep_end_count_after_replace) -> bool {
            std::vector<std::string> candidate_lines = lines;
            candidate_lines[replace_index] = branch_indent + std::string(branch_trimmed);

            std::vector<std::size_t> erase_indices = {i};
            std::size_t remaining_keep = keep_end_count_after_replace;
            for (std::size_t run_pos = 0; run_pos < end_run.size(); ++run_pos) {
                const std::size_t run_index = end_run[run_pos];
                if (run_index <= replace_index) {
                    continue;
                }
                if (remaining_keep > 0) {
                    --remaining_keep;
                    continue;
                }
                erase_indices.push_back(run_index);
            }

            std::sort(erase_indices.begin(), erase_indices.end(), std::greater<>());
            erase_indices.erase(std::unique(erase_indices.begin(), erase_indices.end()),
                                erase_indices.end());
            for (std::size_t erase_index : erase_indices) {
                if (erase_index >= candidate_lines.size()) {
                    return false;
                }
                candidate_lines.erase(candidate_lines.begin() +
                                      static_cast<std::ptrdiff_t>(erase_index));
            }

            std::string candidate = JoinLines(candidate_lines, had_trailing_newline);
            const std::string candidate_error = ValidateLuaRepairSource(candidate);
            const std::size_t candidate_error_line = ExtractLuaErrorLine(candidate_error);
            const bool candidate_pushes_error_later =
                validation_error_line != 0 && candidate_error_line != 0 &&
                candidate_error_line > validation_error_line;
            if (candidate_error.empty()) {
                decompiled_source = std::move(candidate);
                if (note != nullptr) {
                    *note = JoinNotes(*note,
                                      "syntax_repaired_reopened_misclosed_end_run_before_else");
                }
                return true;
            }

            if (!IsValidationOutcomeBetter(validation_error, candidate_error) &&
                !candidate_pushes_error_later) {
                return false;
            }
            const std::size_t best_error_line = ExtractLuaErrorLine(best_error);
            const bool candidate_beats_best_by_line =
                best_error_line != 0 && candidate_error_line != 0 &&
                candidate_error_line > best_error_line;
            if (!best_candidate.empty() &&
                !IsValidationOutcomeBetter(best_error, candidate_error) &&
                !candidate_beats_best_by_line) {
                return false;
            }

            best_candidate = std::move(candidate);
            best_error = candidate_error;
            return false;
        };

        for (std::size_t run_index : end_run) {
            if (consider_candidate(run_index, 0)) {
                return true;
            }
            if (consider_candidate(run_index, 1)) {
                return true;
            }
        }
    }

    if (best_candidate.empty()) {
        return false;
    }

    decompiled_source = std::move(best_candidate);
    if (note != nullptr) {
        *note = JoinNotes(*note,
                          "syntax_repaired_reopened_misclosed_end_run_before_else_progress");
    }
    return true;
}

bool TryStripEndFromTrailingCloseRunImpl(std::string& decompiled_source,
                                         std::string* note) {
    const std::string validation_error = ValidateLuaRepairSource(decompiled_source);
    if (validation_error.empty()) {
        return false;
    }
    const std::size_t validation_error_line = ExtractLuaErrorLine(validation_error);

    bool had_trailing_newline = false;
    const std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);

    std::string best_candidate;
    std::string best_error;
    for (std::size_t i = 0; i < lines.size(); ++i) {
        if (TrimWhitespace(lines[i]) != "end") {
            continue;
        }

        const std::size_t previous = FindPreviousOwnerSignificantLuaLine(lines, i);
        const std::size_t next = FindNextOwnerSignificantLuaLine(lines, i + 1);
        const bool in_close_run =
            (previous < lines.size() && TrimWhitespace(lines[previous]) == "end") ||
            (next < lines.size() && TrimWhitespace(lines[next]) == "end");
        if (!in_close_run) {
            continue;
        }

        std::size_t after_run = i + 1;
        bool saw_additional_close = false;
        while (after_run < lines.size()) {
            const std::string_view after_trimmed = TrimWhitespace(lines[after_run]);
            if (after_trimmed.empty() || IsMissingEndWarningLine(lines[after_run])) {
                ++after_run;
                continue;
            }
            if (after_trimmed == "end" || after_trimmed == "end,") {
                saw_additional_close = true;
                ++after_run;
                continue;
            }
            break;
        }
        if (after_run < lines.size() &&
            (TrimWhitespace(lines[after_run]) == "end" ||
             TrimWhitespace(lines[after_run]) == "end,")) {
            saw_additional_close = true;
        }
        if (!saw_additional_close &&
            !(previous < lines.size() && TrimWhitespace(lines[previous]) == "end")) {
            continue;
        }

        const std::string_view after_run_trimmed =
            after_run < lines.size() ? TrimWhitespace(lines[after_run]) : std::string_view();
        bool acceptable_continuation = after_run >= lines.size() ||
                                       after_run_trimmed == "coroutine.yield()" ||
                                       after_run_trimmed == "else" ||
                                       StartsWith(after_run_trimmed, "elseif ") ||
                                       LooksLikeTopLevelBoundary(after_run_trimmed);
        if (!acceptable_continuation && after_run + 1 < lines.size() &&
            after_run_trimmed == "coroutine.yield()") {
            const std::size_t after_yield = FindNextOwnerSignificantLuaLine(lines, after_run + 1);
            acceptable_continuation =
                after_yield >= lines.size() || TrimWhitespace(lines[after_yield]) == "else" ||
                StartsWith(TrimWhitespace(lines[after_yield]), "elseif ") ||
                LooksLikeTopLevelBoundary(TrimWhitespace(lines[after_yield]));
        }
        if (!acceptable_continuation) {
            continue;
        }

        std::vector<std::size_t> run_end_indices = {i};
        for (std::size_t probe = i + 1; probe < after_run && probe < lines.size(); ++probe) {
            const std::string_view trimmed = TrimWhitespace(lines[probe]);
            if (trimmed == "end" || trimmed == "end,") {
                run_end_indices.push_back(probe);
            }
        }

        const auto consider_candidate =
            [&](const std::vector<std::size_t>& erase_indices) -> bool {
            if (erase_indices.empty()) {
                return false;
            }

            std::vector<std::string> candidate_lines = lines;
            std::vector<std::size_t> sorted_erase_indices = erase_indices;
            std::sort(sorted_erase_indices.begin(), sorted_erase_indices.end(), std::greater<>());
            for (std::size_t erase_index : sorted_erase_indices) {
                if (erase_index >= candidate_lines.size()) {
                    return false;
                }
                candidate_lines.erase(
                    candidate_lines.begin() + static_cast<std::ptrdiff_t>(erase_index));
            }

            std::string candidate = JoinLines(candidate_lines, had_trailing_newline);
            const std::string candidate_error = ValidateLuaRepairSource(candidate);
            const std::size_t candidate_error_line = ExtractLuaErrorLine(candidate_error);
            const bool candidate_pushes_error_later =
                validation_error_line != 0 && candidate_error_line != 0 &&
                candidate_error_line > validation_error_line;
            if (candidate_error.empty()) {
                decompiled_source = std::move(candidate);
                if (note != nullptr) {
                    *note = JoinNotes(*note,
                                      "syntax_repaired_stripped_end_from_trailing_close_run");
                }
                return true;
            }

            if (!IsValidationOutcomeBetter(validation_error, candidate_error) &&
                !candidate_pushes_error_later) {
                return false;
            }
            const std::size_t best_error_line = ExtractLuaErrorLine(best_error);
            const bool candidate_beats_best_by_line =
                best_error_line != 0 && candidate_error_line != 0 &&
                candidate_error_line > best_error_line;
            if (!best_candidate.empty() &&
                !IsValidationOutcomeBetter(best_error, candidate_error) &&
                !candidate_beats_best_by_line) {
                return false;
            }

            best_candidate = std::move(candidate);
            best_error = candidate_error;
            return false;
        };

        if (consider_candidate({i})) {
            return true;
        }
        for (std::size_t first = 0; first < run_end_indices.size(); ++first) {
            for (std::size_t second = first + 1; second < run_end_indices.size(); ++second) {
                if (consider_candidate(
                        {run_end_indices[first], run_end_indices[second]})) {
                    return true;
                }
            }
        }
    }

    if (best_candidate.empty()) {
        return false;
    }

    decompiled_source = std::move(best_candidate);
    if (note != nullptr) {
        *note = JoinNotes(*note,
                          "syntax_repaired_stripped_end_from_trailing_close_run_progress");
    }
    return true;
}

bool TryStripSyntheticDoReturnEndAfterYieldTailImpl(std::string& decompiled_source,
                                                    std::string* note) {
    if (decompiled_source.find("do return end") == std::string::npos ||
        decompiled_source.find("coroutine.yield()") == std::string::npos ||
        decompiled_source.find("missing end command somewhere! Added here") == std::string::npos) {
        return false;
    }

    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);

    bool changed = false;
    for (std::size_t i = 0; i < lines.size(); ++i) {
        if (TrimWhitespace(lines[i]) != "coroutine.yield()") {
            continue;
        }

        const std::string yield_indent = GetLuaIndentAt(lines, i);
        std::size_t next = i + 1;
        while (next < lines.size() && TrimWhitespace(lines[next]).empty()) {
            ++next;
        }
        if (next >= lines.size() || TrimWhitespace(lines[next]) != "do return end" ||
            GetLuaIndentAt(lines, next) != yield_indent) {
            continue;
        }

        std::size_t erase_begin = next;
        std::size_t erase_end = next + 1;
        std::size_t probe = next + 1;
        bool removed_any_warning_end = false;
        while (probe < lines.size()) {
            const std::string_view trimmed = TrimWhitespace(lines[probe]);
            if (trimmed.empty()) {
                ++probe;
                continue;
            }
            if (IsMissingEndWarningLine(lines[probe])) {
                erase_end = probe + 1;
                ++probe;
                while (probe < lines.size() && TrimWhitespace(lines[probe]).empty()) {
                    erase_end = probe + 1;
                    ++probe;
                }
                if (probe < lines.size() && TrimWhitespace(lines[probe]) == "end" &&
                    GetLuaIndentAt(lines, probe) == yield_indent) {
                    erase_end = probe + 1;
                    removed_any_warning_end = true;
                    ++probe;
                    continue;
                }
                break;
            }
            break;
        }

        if (!removed_any_warning_end) {
            continue;
        }

        lines.erase(lines.begin() + static_cast<std::ptrdiff_t>(erase_begin),
                    lines.begin() + static_cast<std::ptrdiff_t>(erase_end));
        changed = true;
        if (i != 0) {
            --i;
        }
    }

    if (!changed) {
        return false;
    }

    decompiled_source = JoinLines(lines, had_trailing_newline);
    if (note != nullptr) {
        *note = JoinNotes(*note, "syntax_repaired_stripped_synthetic_yield_tail");
    }
    return true;
}

bool TryStripSyntheticElseWarningTailAfterYieldImpl(std::string& decompiled_source,
                                                    std::string* note) {
    if (decompiled_source.find("coroutine.yield()") == std::string_view::npos ||
        decompiled_source.find("missing end command somewhere! Added here") == std::string::npos) {
        return false;
    }

    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);

    bool changed = false;
    for (std::size_t i = 0; i < lines.size(); ++i) {
        if (TrimWhitespace(lines[i]) != "coroutine.yield()") {
            continue;
        }

        const std::string yield_indent = GetLuaIndentAt(lines, i);
        const std::size_t else_line = FindNextOwnerSignificantLuaLine(lines, i + 1);
        if (else_line >= lines.size() || TrimWhitespace(lines[else_line]) != "else") {
            continue;
        }

        const std::string else_indent = GetLuaIndentAt(lines, else_line);
        if (else_indent.size() >= yield_indent.size()) {
            continue;
        }

        std::size_t owner_end = lines.size();
        bool saw_warning = false;
        for (std::size_t probe = else_line + 1; probe < lines.size(); ++probe) {
            const std::string_view trimmed = TrimWhitespace(lines[probe]);
            if (trimmed.empty()) {
                continue;
            }
            if (IsMissingEndWarningLine(lines[probe])) {
                saw_warning = true;
                continue;
            }
            if (trimmed == "end") {
                if (GetLuaIndentAt(lines, probe) == else_indent) {
                    owner_end = probe;
                    break;
                }
                continue;
            }
            owner_end = lines.size();
            break;
        }
        if (owner_end >= lines.size() || !saw_warning) {
            continue;
        }

        std::size_t after_owner_end = owner_end + 1;
        while (after_owner_end < lines.size()) {
            const std::string_view after_trimmed = TrimWhitespace(lines[after_owner_end]);
            if (after_trimmed.empty() || IsMissingEndWarningLine(lines[after_owner_end])) {
                ++after_owner_end;
                continue;
            }
            break;
        }
        if (after_owner_end < lines.size() &&
            GetLuaIndentAt(lines, after_owner_end).size() > else_indent.size()) {
            continue;
        }

        lines.erase(lines.begin() + static_cast<std::ptrdiff_t>(else_line),
                    lines.begin() + static_cast<std::ptrdiff_t>(owner_end));
        std::size_t warning_probe = else_line + 1;
        while (warning_probe < lines.size()) {
            const std::string_view warning_trimmed = TrimWhitespace(lines[warning_probe]);
            if (warning_trimmed.empty()) {
                ++warning_probe;
                continue;
            }
            if (!IsMissingEndWarningLine(lines[warning_probe])) {
                break;
            }
            lines.erase(lines.begin() + static_cast<std::ptrdiff_t>(warning_probe));
        }
        changed = true;
        if (i != 0) {
            --i;
        }
    }

    if (!changed) {
        return false;
    }

    decompiled_source = JoinLines(lines, had_trailing_newline);
    if (note != nullptr) {
        *note = JoinNotes(*note, "syntax_repaired_stripped_synthetic_else_warning_tail");
    }
    return true;
}

bool TryStripWarningGuardedEndRunImpl(std::string& decompiled_source, std::string* note) {
    const std::string validation_error = ValidateLuaRepairSource(decompiled_source);
    if (validation_error.empty()) {
        return false;
    }
    const std::size_t validation_error_line = ExtractLuaErrorLine(validation_error);

    bool had_trailing_newline = false;
    const std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);

    std::string best_candidate;
    std::string best_error;
    for (std::size_t i = 0; i < lines.size(); ++i) {
        if (TrimWhitespace(lines[i]) != "end") {
            continue;
        }

        const std::size_t previous = FindPreviousOwnerSignificantLuaLine(lines, i);
        if (previous >= lines.size() || !IsMissingEndWarningLine(lines[previous])) {
            continue;
        }

        const std::string current_indent = GetLuaIndentAt(lines, i);
        bool saw_tail_close = false;
        bool tail_looks_structural = true;
        std::vector<std::size_t> tail_end_indices;
        for (std::size_t probe = i + 1; probe < lines.size(); ++probe) {
            const std::string_view trimmed = TrimWhitespace(lines[probe]);
            if (trimmed.empty()) {
                continue;
            }
            if (IsMissingEndWarningLine(lines[probe]) ||
                trimmed == "end" || trimmed == "end,") {
                if (trimmed == "end" || trimmed == "end,") {
                    tail_end_indices.push_back(probe);
                }
                saw_tail_close = true;
                continue;
            }
            if (GetLuaIndentAt(lines, probe).size() <= current_indent.size() &&
                (trimmed == "else" || StartsWith(trimmed, "elseif ") ||
                 LooksLikeTopLevelBoundary(trimmed))) {
                break;
            }
            tail_looks_structural = false;
            break;
        }
        if (!saw_tail_close || !tail_looks_structural) {
            continue;
        }

        std::size_t erase_begin = i;
        while (erase_begin > 0) {
            const std::string_view prior_trimmed = TrimWhitespace(lines[erase_begin - 1]);
            if (prior_trimmed.empty()) {
                --erase_begin;
                continue;
            }
            if (!IsMissingEndWarningLine(lines[erase_begin - 1])) {
                break;
            }
            --erase_begin;
        }
        std::size_t erase_end = i + 1;
        while (erase_end < lines.size()) {
            const std::string_view next_trimmed = TrimWhitespace(lines[erase_end]);
            if (next_trimmed.empty() || IsMissingEndWarningLine(lines[erase_end])) {
                ++erase_end;
                continue;
            }
            break;
        }
        const auto consider_candidate =
            [&](std::size_t candidate_erase_begin, std::size_t candidate_erase_end) -> bool {
            std::vector<std::string> candidate_lines = lines;
            candidate_lines.erase(
                candidate_lines.begin() + static_cast<std::ptrdiff_t>(candidate_erase_begin),
                candidate_lines.begin() + static_cast<std::ptrdiff_t>(candidate_erase_end));
            std::string candidate = JoinLines(candidate_lines, had_trailing_newline);
            const std::string candidate_error = ValidateLuaRepairSource(candidate);
            const std::size_t candidate_error_line = ExtractLuaErrorLine(candidate_error);
            const bool candidate_pushes_error_later =
                validation_error_line != 0 && candidate_error_line != 0 &&
                candidate_error_line > validation_error_line;
            if (candidate_error.empty()) {
                decompiled_source = std::move(candidate);
                if (note != nullptr) {
                    *note = JoinNotes(*note,
                                      "syntax_repaired_stripped_warning_guarded_end_run");
                }
                return true;
            }

            if (!IsValidationOutcomeBetter(validation_error, candidate_error) &&
                !candidate_pushes_error_later) {
                return false;
            }
            const std::size_t best_error_line = ExtractLuaErrorLine(best_error);
            const bool candidate_beats_best_by_line =
                best_error_line != 0 && candidate_error_line != 0 &&
                candidate_error_line > best_error_line;
            if (!best_candidate.empty() &&
                !IsValidationOutcomeBetter(best_error, candidate_error) &&
                !candidate_beats_best_by_line) {
                return false;
            }

            best_candidate = std::move(candidate);
            best_error = candidate_error;
            return false;
        };

        if (consider_candidate(erase_begin, erase_end)) {
            return true;
        }
        for (std::size_t tail_end_index : tail_end_indices) {
            if (tail_end_index >= lines.size()) {
                continue;
            }
            if (consider_candidate(tail_end_index, tail_end_index + 1)) {
                return true;
            }
        }
    }

    if (best_candidate.empty()) {
        return false;
    }

    decompiled_source = std::move(best_candidate);
    if (note != nullptr) {
        *note = JoinNotes(*note,
                          "syntax_repaired_stripped_warning_guarded_end_run_progress");
    }
    return true;
}

bool TryStripTerminalYieldCloseRunMiddleImpl(std::string& decompiled_source,
                                             std::string* note) {
    const std::string validation_error = ValidateLuaRepairSource(decompiled_source);
    if (validation_error.empty()) {
        return false;
    }
    const std::size_t validation_error_line = ExtractLuaErrorLine(validation_error);

    bool had_trailing_newline = false;
    const std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);

    std::string best_candidate;
    std::string best_error;
    for (std::size_t line_index = 0; line_index < lines.size(); ++line_index) {
        if (TrimWhitespace(lines[line_index]) != "while true do") {
            continue;
        }

        const std::string outer_indent = GetLuaIndentAt(lines, line_index);
        const std::string body_indent = outer_indent + "  ";
        const std::size_t yield_line = FindNextOwnerSignificantLuaLine(lines, line_index + 1);
        if (yield_line >= lines.size() || TrimWhitespace(lines[yield_line]) != "coroutine.yield()" ||
            GetLuaIndentAt(lines, yield_line) != body_indent) {
            continue;
        }

        std::vector<std::size_t> close_indices;
        close_indices.reserve(8);
        bool tail_is_pure_close_run = true;
        bool saw_warning = false;
        std::size_t tail_limit = lines.size();
        for (std::size_t probe = yield_line + 1; probe < lines.size(); ++probe) {
            const std::string_view trimmed = TrimWhitespace(lines[probe]);
            if (trimmed.empty()) {
                continue;
            }
            if (IsMissingEndWarningLine(lines[probe])) {
                saw_warning = true;
                continue;
            }
            if (trimmed == "end" || trimmed == "end,") {
                close_indices.push_back(probe);
                continue;
            }
            if (GetLuaIndentAt(lines, probe).size() <= outer_indent.size() &&
                (trimmed == "else" || StartsWith(trimmed, "elseif ") ||
                 LooksLikeTopLevelBoundary(trimmed))) {
                tail_limit = probe;
                break;
            }
            tail_is_pure_close_run = false;
            break;
        }

        if (!tail_is_pure_close_run || !saw_warning || close_indices.size() < 4) {
            continue;
        }

        const std::size_t erase_begin = close_indices.front() + 1;
        const std::size_t erase_end = close_indices[close_indices.size() - 2];
        if (erase_begin >= erase_end || erase_end > lines.size()) {
            continue;
        }

        std::vector<std::string> candidate_lines = lines;
        candidate_lines.erase(candidate_lines.begin() + static_cast<std::ptrdiff_t>(erase_begin),
                              candidate_lines.begin() + static_cast<std::ptrdiff_t>(erase_end));
        std::string candidate = JoinLines(candidate_lines, had_trailing_newline);
        const std::string candidate_error = ValidateLuaRepairSource(candidate);
        const std::size_t candidate_error_line = ExtractLuaErrorLine(candidate_error);
        const bool candidate_pushes_error_later =
            validation_error_line != 0 && candidate_error_line != 0 &&
            candidate_error_line > validation_error_line;
        if (candidate_error.empty()) {
            decompiled_source = std::move(candidate);
            if (note != nullptr) {
                *note = JoinNotes(*note,
                                  "syntax_repaired_stripped_terminal_yield_close_run");
            }
            return true;
        }

        if (!IsValidationOutcomeBetter(validation_error, candidate_error) &&
            !candidate_pushes_error_later) {
            continue;
        }
        const std::size_t best_error_line = ExtractLuaErrorLine(best_error);
        const bool candidate_beats_best_by_line =
            best_error_line != 0 && candidate_error_line != 0 &&
            candidate_error_line > best_error_line;
        if (!best_candidate.empty() &&
            !IsValidationOutcomeBetter(best_error, candidate_error) &&
            !candidate_beats_best_by_line) {
            continue;
        }

        best_candidate = std::move(candidate);
        best_error = candidate_error;
    }

    if (best_candidate.empty()) {
        return false;
    }

    decompiled_source = std::move(best_candidate);
    if (note != nullptr) {
        *note = JoinNotes(*note,
                          "syntax_repaired_stripped_terminal_yield_close_run_progress");
    }
    return true;
}

bool TryStripConsecutiveElseRunArtifactsImpl(std::string& decompiled_source,
                                             std::string* note) {
    const std::string validation_error = ValidateLuaRepairSource(decompiled_source);
    if (validation_error.empty()) {
        return false;
    }
    const std::size_t validation_error_line = ExtractLuaErrorLine(validation_error);

    bool had_trailing_newline = false;
    const std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);

    std::string best_candidate;
    std::string best_error;
    for (std::size_t i = 0; i + 1 < lines.size(); ++i) {
        if (TrimWhitespace(lines[i]) != "else" || TrimWhitespace(lines[i + 1]) != "else" ||
            GetLuaLineIndent(lines[i]) != GetLuaLineIndent(lines[i + 1])) {
            continue;
        }

        const std::size_t next = FindNextOwnerSignificantLuaLine(lines, i + 2);
        if (next >= lines.size()) {
            continue;
        }
        if (GetLuaLineIndent(lines[next]).size() < GetLuaLineIndent(lines[i]).size()) {
            continue;
        }

        const auto consider_candidate =
            [&](std::vector<std::string> candidate_lines) -> bool {
            std::string candidate = JoinLines(candidate_lines, had_trailing_newline);
            const std::string candidate_error = ValidateLuaRepairSource(candidate);
            const std::size_t candidate_error_line = ExtractLuaErrorLine(candidate_error);
            const bool candidate_pushes_error_later =
                validation_error_line != 0 && candidate_error_line != 0 &&
                candidate_error_line > validation_error_line;
            if (candidate_error.empty()) {
                decompiled_source = std::move(candidate);
                if (note != nullptr) {
                    *note = JoinNotes(*note,
                                      "syntax_repaired_stripped_consecutive_else_run");
                }
                return true;
            }

            if (!IsValidationOutcomeBetter(validation_error, candidate_error) &&
                !candidate_pushes_error_later) {
                return false;
            }
            const std::size_t best_error_line = ExtractLuaErrorLine(best_error);
            const bool candidate_beats_best_by_line =
                best_error_line != 0 && candidate_error_line != 0 &&
                candidate_error_line > best_error_line;
            if (!best_candidate.empty() &&
                !IsValidationOutcomeBetter(best_error, candidate_error) &&
                !candidate_beats_best_by_line) {
                return false;
            }

            best_candidate = std::move(candidate);
            best_error = candidate_error;
            return false;
        };

        for (std::size_t remove_index = i; remove_index <= i + 1; ++remove_index) {
            std::vector<std::string> candidate_lines = lines;
            candidate_lines.erase(candidate_lines.begin() +
                                  static_cast<std::ptrdiff_t>(remove_index));
            if (consider_candidate(std::move(candidate_lines))) {
                return true;
            }
        }

        {
            std::vector<std::string> candidate_lines = lines;
            candidate_lines.erase(candidate_lines.begin() + static_cast<std::ptrdiff_t>(i),
                                  candidate_lines.begin() + static_cast<std::ptrdiff_t>(i + 2));
            if (consider_candidate(std::move(candidate_lines))) {
                return true;
            }
        }

        {
            std::vector<std::string> candidate_lines = lines;
            candidate_lines[i] = GetLuaLineIndent(lines[i]) + "end";
            candidate_lines.erase(candidate_lines.begin() +
                                  static_cast<std::ptrdiff_t>(i + 1));
            if (consider_candidate(std::move(candidate_lines))) {
                return true;
            }
        }
    }

    if (best_candidate.empty()) {
        return false;
    }

    decompiled_source = std::move(best_candidate);
    if (note != nullptr) {
        *note = JoinNotes(*note, "syntax_repaired_stripped_consecutive_else_run_progress");
    }
    return true;
}

bool TryLiftOuterElseAfterClosedNestedIfImpl(std::string& decompiled_source, std::string* note) {
    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);

    bool changed = false;
    for (std::size_t i = 0; i < lines.size(); ++i) {
        if (TrimWhitespace(lines[i]) != "else") {
            continue;
        }

        const std::size_t previous = FindPreviousOwnerSignificantLuaLine(lines, i);
        const std::size_t next = FindNextOwnerSignificantLuaLine(lines, i + 1);
        if (previous >= lines.size() || next >= lines.size() ||
            TrimWhitespace(lines[previous]) != "end") {
            continue;
        }

        const std::string current_indent = GetLuaIndentAt(lines, i);
        const std::string next_indent = GetLuaIndentAt(lines, next);
        const bool next_belongs_to_current_else =
            next_indent == current_indent ||
            (next_indent.size() > current_indent.size() &&
             next_indent.starts_with(current_indent));
        if (current_indent.empty() || !next_belongs_to_current_else) {
            continue;
        }

        std::size_t nested_owner = lines.size();
        for (std::size_t probe = previous; probe-- > 0;) {
            const std::string_view probe_trimmed = TrimWhitespace(lines[probe]);
            if (probe_trimmed.empty() || StartsWith(probe_trimmed, "--")) {
                if (probe == 0) {
                    break;
                }
                continue;
            }

            const std::string probe_indent = GetLuaIndentAt(lines, probe);
            if (probe_indent.size() < current_indent.size()) {
                break;
            }
            if (probe_indent == current_indent &&
                (StartsWith(probe_trimmed, "if ") || StartsWith(probe_trimmed, "elseif "))) {
                nested_owner = probe;
                break;
            }
        }
        if (nested_owner >= lines.size()) {
            continue;
        }

        bool nested_owner_already_has_branch = false;
        for (std::size_t probe = nested_owner + 1; probe < previous; ++probe) {
            const std::string_view probe_trimmed = TrimWhitespace(lines[probe]);
            if ((probe_trimmed == "else" || StartsWith(probe_trimmed, "elseif ")) &&
                GetLuaIndentAt(lines, probe) == current_indent) {
                nested_owner_already_has_branch = true;
                break;
            }
        }
        if (!nested_owner_already_has_branch) {
            continue;
        }

        std::string owner_indent;
        for (std::size_t probe = nested_owner; probe-- > 0;) {
            const std::string_view probe_trimmed = TrimWhitespace(lines[probe]);
            if (probe_trimmed.empty() || StartsWith(probe_trimmed, "--")) {
                if (probe == 0) {
                    break;
                }
                continue;
            }

            const std::string probe_indent = GetLuaIndentAt(lines, probe);
            if (probe_indent.size() >= current_indent.size()) {
                if (probe == 0) {
                    break;
                }
                continue;
            }

            if (StartsWith(probe_trimmed, "if ") || StartsWith(probe_trimmed, "elseif ")) {
                owner_indent = probe_indent;
                break;
            }
        }
        if (owner_indent.empty() || owner_indent.size() >= current_indent.size() ||
            owner_indent != DedentLuaIndent(current_indent)) {
            continue;
        }

        lines[i] = owner_indent + "else";
        changed = true;
    }

    if (!changed) {
        return false;
    }

    decompiled_source = JoinLines(lines, had_trailing_newline);
    if (note != nullptr) {
        *note = JoinNotes(*note, "syntax_repaired_lifted_outer_else_after_nested_if");
    }
    return true;
}

bool TryLiftElseAfterPriorSameIndentBranchImpl(std::string& decompiled_source,
                                               std::string* note) {
    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);

    bool changed = false;
    for (std::size_t i = 0; i < lines.size(); ++i) {
        const std::string_view trimmed = TrimWhitespace(lines[i]);
        if (trimmed != "else" && !StartsWith(trimmed, "elseif ")) {
            continue;
        }

        const std::size_t previous = FindPreviousOwnerSignificantLuaLine(lines, i);
        const std::size_t next = FindNextOwnerSignificantLuaLine(lines, i + 1);
        if (previous >= lines.size() || next >= lines.size() ||
            TrimWhitespace(lines[previous]) != "end") {
            continue;
        }

        const std::string current_indent = GetLuaIndentAt(lines, i);
        if (current_indent.empty() || GetLuaIndentAt(lines, next) != current_indent) {
            continue;
        }

        bool saw_prior_same_indent_branch = false;
        for (std::size_t probe = previous; probe-- > 0;) {
            const std::string_view probe_trimmed = TrimWhitespace(lines[probe]);
            if (probe_trimmed.empty() || StartsWith(probe_trimmed, "--")) {
                if (probe == 0) {
                    break;
                }
                continue;
            }

            const std::string probe_indent = GetLuaIndentAt(lines, probe);
            if (probe_indent.size() < current_indent.size()) {
                break;
            }
            if (probe_indent == current_indent &&
                (probe_trimmed == "else" || StartsWith(probe_trimmed, "elseif "))) {
                saw_prior_same_indent_branch = true;
                break;
            }
        }
        if (!saw_prior_same_indent_branch) {
            continue;
        }

        const std::string owner_indent = DedentLuaIndent(current_indent);
        if (owner_indent.empty()) {
            continue;
        }

        lines[i] = owner_indent + std::string(trimmed);
        changed = true;
    }

    if (!changed) {
        return false;
    }

    decompiled_source = JoinLines(lines, had_trailing_newline);
    if (note != nullptr) {
        *note = JoinNotes(*note, "syntax_repaired_lifted_else_after_prior_same_indent_branch");
    }
    return true;
}

bool TryLiftMisindentedElseAfterNestedIfImpl(std::string& decompiled_source,
                                             std::string* note) {
    bool had_trailing_newline = false;
    std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);

    bool changed = false;
    for (std::size_t i = 0; i < lines.size(); ++i) {
        const std::string_view trimmed = TrimWhitespace(lines[i]);
        if (trimmed != "else" && !StartsWith(trimmed, "elseif ")) {
            continue;
        }

        const std::size_t previous = FindPreviousOwnerSignificantLuaLine(lines, i);
        if (previous >= lines.size() || TrimWhitespace(lines[previous]) != "end") {
            continue;
        }

        const std::string current_indent = GetLuaIndentAt(lines, i);
        if (current_indent.empty()) {
            continue;
        }

        const std::size_t next = FindNextOwnerSignificantLuaLine(lines, i + 1);
        if (next >= lines.size() || GetLuaIndentAt(lines, next) != current_indent) {
            continue;
        }

        std::string owner_indent;
        for (std::size_t probe = i; probe-- > 0;) {
            const std::string_view probe_trimmed = TrimWhitespace(lines[probe]);
            if (probe_trimmed.empty() || StartsWith(probe_trimmed, "--")) {
                if (probe == 0) {
                    break;
                }
                continue;
            }

            const std::string probe_indent = GetLuaIndentAt(lines, probe);
            if (probe_indent.size() >= current_indent.size()) {
                if (probe == 0) {
                    break;
                }
                continue;
            }

            if (StartsWith(probe_trimmed, "if ") || StartsWith(probe_trimmed, "elseif ")) {
                owner_indent = probe_indent;
                break;
            }
        }
        if (owner_indent.empty() || owner_indent.size() >= current_indent.size()) {
            continue;
        }

        lines[i] = owner_indent + std::string(trimmed);
        changed = true;
    }

    if (!changed) {
        return false;
    }

    decompiled_source = JoinLines(lines, had_trailing_newline);
    if (note != nullptr) {
        *note = JoinNotes(*note, "syntax_repaired_lifted_misindented_else");
    }
    return true;
}

}  // namespace

bool TryStripRedundantEndBeforeTopLevelBoundary(std::string& decompiled_source,
                                                std::string* note) {
    const std::string validation_error = ValidateLuaRepairSource(decompiled_source);
    if (validation_error.empty()) {
        return false;
    }
    const std::size_t validation_error_line = ExtractLuaErrorLine(validation_error);

    bool had_trailing_newline = false;
    const std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);

    std::string best_candidate;
    std::string best_error;
    for (std::size_t i = 0; i < lines.size(); ++i) {
        if (TrimWhitespace(lines[i]) != "end") {
            continue;
        }

        const std::size_t previous = FindPreviousOwnerSignificantLuaLine(lines, i);
        const std::size_t next = FindNextOwnerSignificantLuaLine(lines, i + 1);
        if (previous >= lines.size() || next >= lines.size()) {
            continue;
        }
        if (TrimWhitespace(lines[previous]) != "end" &&
            TrimWhitespace(lines[previous]) != "end,") {
            continue;
        }
        if (!GetLuaIndentAt(lines, next).empty() ||
            !LooksLikeTopLevelBoundary(TrimWhitespace(lines[next]))) {
            continue;
        }

        std::vector<std::string> candidate_lines = lines;
        candidate_lines.erase(candidate_lines.begin() + static_cast<std::ptrdiff_t>(i));
        std::string candidate = JoinLines(candidate_lines, had_trailing_newline);
        const std::string candidate_error = ValidateLuaRepairSource(candidate);
        const std::size_t candidate_error_line = ExtractLuaErrorLine(candidate_error);
        const bool candidate_pushes_error_later =
            validation_error_line != 0 && candidate_error_line != 0 &&
            candidate_error_line > validation_error_line;
        if (candidate_error.empty()) {
            decompiled_source = std::move(candidate);
            if (note != nullptr) {
                *note = JoinNotes(*note,
                                  "syntax_repaired_stripped_redundant_end_before_top_level_boundary");
            }
            return true;
        }

        if (!IsValidationOutcomeBetter(validation_error, candidate_error) &&
            !candidate_pushes_error_later) {
            continue;
        }
        const std::size_t best_error_line = ExtractLuaErrorLine(best_error);
        const bool candidate_beats_best_by_line =
            best_error_line != 0 && candidate_error_line != 0 &&
            candidate_error_line > best_error_line;
        if (!best_candidate.empty() &&
            !IsValidationOutcomeBetter(best_error, candidate_error) &&
            !candidate_beats_best_by_line) {
            continue;
        }

        best_candidate = std::move(candidate);
        best_error = candidate_error;
    }

    if (best_candidate.empty()) {
        return false;
    }

    decompiled_source = std::move(best_candidate);
    if (note != nullptr) {
        *note = JoinNotes(
            *note,
            "syntax_repaired_stripped_redundant_end_before_top_level_boundary_progress");
    }
    return true;
}

bool TryNormalizeQuestOwnerContinuationCleanup(std::string& decompiled_source,
                                               std::string* note) {
    bool changed = false;
    for (int pass = 0; pass < 4; ++pass) {
        if (ShouldAbortCurrentDecompileRepair(note, "quest_owner_cleanup")) {
            break;
        }
        bool pass_changed = false;
        std::string validation_error = ValidateLuaRepairSource(decompiled_source);
        if (!validation_error.empty()) {
            const bool collapsed_nested_else_if_inside_clause =
                TryCollapseNestedElseIfInsideClause(
                    decompiled_source, validation_error, note);
            pass_changed |= collapsed_nested_else_if_inside_clause;
            if (collapsed_nested_else_if_inside_clause) {
                validation_error = ValidateLuaRepairSource(decompiled_source);
            }

            pass_changed |= TryStripDanglingDoBeforeElse(decompiled_source, note);
            validation_error = ValidateLuaRepairSource(decompiled_source);

            const bool stripped_single_end_before_owner_branch =
                TryStripSingleEndBeforeOwnerContinuationImpl(decompiled_source, note);
            pass_changed |= stripped_single_end_before_owner_branch;
            if (stripped_single_end_before_owner_branch) {
                validation_error = ValidateLuaRepairSource(decompiled_source);
            }

            const bool stripped_dangling_do_before_boundary =
                TryStripDanglingDoBeforeBoundary(decompiled_source, validation_error, note);
            pass_changed |= stripped_dangling_do_before_boundary;
            if (stripped_dangling_do_before_boundary) {
                validation_error = ValidateLuaRepairSource(decompiled_source);
            }

            const bool stripped_dangling_do_inside_true_loop =
                TryStripDanglingDoInsideTrueLoop(decompiled_source, validation_error, note);
            pass_changed |= stripped_dangling_do_inside_true_loop;
            if (stripped_dangling_do_inside_true_loop) {
                validation_error = ValidateLuaRepairSource(decompiled_source);
            }

            const bool inserted_end_before_else =
                TryInsertEndBeforeElse(decompiled_source, validation_error, note);
            pass_changed |= inserted_end_before_else;
        }
        pass_changed |= TryLiftElseAfterPriorSameIndentBranchImpl(decompiled_source, note);
        pass_changed |= TryLiftOuterElseAfterClosedNestedIfImpl(decompiled_source, note);
        pass_changed |= TryLiftMisindentedElseAfterNestedIfImpl(decompiled_source, note);
        pass_changed |= TryStripRedundantEndBeforeStateElseIfImpl(decompiled_source, note);
        pass_changed |= TryStripRedundantEndBeforeTopLevelBoundary(decompiled_source, note);
        pass_changed |=
            TryStripWarningGuardedExtraEndBeforeTopLevelBoundaryImpl(decompiled_source, note);
        pass_changed |= TryReopenMisclosedEndBeforeDuplicatedElseTailImpl(decompiled_source, note);
        pass_changed |= TryReopenMisclosedEndRunBeforeElseImpl(decompiled_source, note);
        pass_changed |= TryStripDuplicateOwnerEndAfterSameIndentIfImpl(decompiled_source, note);
        pass_changed |= TryStripDuplicateEndRunBeforeOwnerContinuationImpl(decompiled_source, note);
        pass_changed |=
            TryStripRedundantEndBeforeSameIndentContinuationImpl(decompiled_source, note);
        pass_changed |= TryStripEndFromTrailingCloseRunImpl(decompiled_source, note);
        pass_changed |= TryStripSyntheticDoReturnEndAfterYieldTailImpl(decompiled_source, note);
        pass_changed |= TryStripSyntheticElseWarningTailAfterYieldImpl(decompiled_source, note);
        pass_changed |= TryStripTerminalYieldCloseRunMiddleImpl(decompiled_source, note);
        pass_changed |= TryStripWarningGuardedEndRunImpl(decompiled_source, note);
        pass_changed |= TryStripConsecutiveElseRunArtifactsImpl(decompiled_source, note);
        pass_changed |= TryStripRedundantEndBeforeTopLevelBoundary(decompiled_source, note);
        if (!pass_changed) {
            break;
        }
        changed = true;
    }
    return changed;
}

}  // namespace refii::lua::internal
