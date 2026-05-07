#include "tools/decompiler/lua_decompiler_internal.h"

#include "tools/decompiler/rendered/rendered_lua_text.h"

#include <algorithm>
#include <cctype>
#include <cstdlib>
#include <functional>
#include <limits>
#include <string>
#include <string_view>
#include <unordered_set>
#include <utility>
#include <vector>

namespace refii::lua::internal {

bool TryRemoveEndsBeforeTopLevelBoundaries(std::string& decompiled_source,
                                           std::string_view validation_error,
                                           std::string* note);

namespace {

int ScoreValidationOutcome(std::string_view raw_error) {
    if (raw_error.empty()) {
        return std::numeric_limits<int>::max();
    }

    const SyntaxErrorDescriptor descriptor = DescribeSyntaxError(raw_error);
    int score = 0;
    const int line =
        static_cast<int>(std::min<std::size_t>(descriptor.line, 100000));
    score += line * 1000;

    if (raw_error.find("<eof>") == std::string_view::npos) {
        score += 200;
    }
    if (raw_error.find("'end' expected") == std::string_view::npos) {
        score += 100;
    }
    if (raw_error.find("'until' expected") == std::string_view::npos) {
        score += 50;
    }

    score -= static_cast<int>(std::min<std::size_t>(descriptor.summary.size(), 255));
    return score;
}

class SyntaxRepairCandidateTracker {
public:
    SyntaxRepairCandidateTracker(std::string_view baseline_error,
                                 bool had_trailing_newline,
                                 std::string* output_source,
                                 std::string* output_note)
        : baseline_error_(baseline_error),
          baseline_descriptor_(DescribeSyntaxError(baseline_error)),
          had_trailing_newline_(had_trailing_newline),
          output_source_(output_source),
          output_note_(output_note) {}

    bool Consider(std::vector<std::string> candidate_lines,
                  std::string_view success_note) {
        return ConsiderSource(JoinLines(candidate_lines, had_trailing_newline_),
                              success_note);
    }

    bool ConsiderSource(std::string candidate, std::string_view success_note) {
        const std::string candidate_error = ValidateLuaRepairSource(candidate);
        if (candidate_error.empty()) {
            if (output_source_ != nullptr) {
                *output_source_ = std::move(candidate);
            }
            AppendNote(success_note);
            return true;
        }

        const SyntaxErrorDescriptor candidate_descriptor =
            DescribeSyntaxError(candidate_error);
        if (candidate_descriptor.line <= baseline_descriptor_.line ||
            !IsValidationOutcomeBetter(baseline_error_, candidate_error) ||
            (!best_error_.empty() &&
             !IsValidationOutcomeBetter(best_error_, candidate_error))) {
            return false;
        }

        best_candidate_ = std::move(candidate);
        best_error_ = std::move(candidate_error);
        return false;
    }

    bool CommitBestProgress(std::string_view progress_note) {
        if (best_candidate_.empty() || output_source_ == nullptr) {
            return false;
        }

        *output_source_ = std::move(best_candidate_);
        AppendNote(progress_note);
        return true;
    }

private:
    void AppendNote(std::string_view note_text) {
        if (output_note_ == nullptr || note_text.empty()) {
            return;
        }
        *output_note_ = JoinNotes(*output_note_, std::string(note_text));
    }

    std::string_view baseline_error_;
    SyntaxErrorDescriptor baseline_descriptor_;
    bool had_trailing_newline_ = false;
    std::string* output_source_ = nullptr;
    std::string* output_note_ = nullptr;
    std::string best_candidate_;
    std::string best_error_;
};

void AppendReindentedLineRange(const std::vector<std::string>& lines,
                               std::size_t start,
                               std::size_t end_exclusive,
                               std::string_view source_base,
                               std::string_view target_base,
                               std::vector<std::string>* out_lines) {
    if (out_lines == nullptr || start >= end_exclusive || end_exclusive > lines.size()) {
        return;
    }

    for (std::size_t i = start; i < end_exclusive; ++i) {
        const std::string_view trimmed =
            TrimWhitespace(refii::lua::StripLuaLineComment(lines[i]));
        if (trimmed.empty()) {
            out_lines->push_back(std::string(lines[i]));
            continue;
        }

        const std::size_t first_non_ws = lines[i].find_first_not_of(" \t");
        const std::string_view current_indent =
            first_non_ws == std::string_view::npos
                ? std::string_view(lines[i])
                : std::string_view(lines[i]).substr(0, first_non_ws);
        if (current_indent.starts_with(source_base)) {
            out_lines->push_back(std::string(target_base) +
                                 std::string(current_indent.substr(source_base.size())) +
                                 std::string(std::string_view(lines[i]).substr(current_indent.size())));
        } else {
            out_lines->push_back(std::string(target_base) + std::string(trimmed));
        }
    }
}

void AppendReindentedSingleLine(const std::vector<std::string>& lines,
                                std::size_t index,
                                std::string_view source_base,
                                std::string_view target_base,
                                std::vector<std::string>* out_lines) {
    if (index >= lines.size()) {
        return;
    }
    AppendReindentedLineRange(lines, index, index + 1, source_base, target_base, out_lines);
}

std::size_t LastNonemptyNonEndBefore(const std::vector<std::string>& lines,
                                     std::size_t stop,
                                     std::size_t min_inclusive) {
    if (stop == 0 || stop > lines.size() || stop <= min_inclusive) {
        return lines.size();
    }
    for (std::size_t i = stop; i-- > min_inclusive;) {
        const std::string_view trimmed =
            TrimWhitespace(refii::lua::StripLuaLineComment(lines[i]));
        if (trimmed.empty() || trimmed == "end") {
            continue;
        }
        return i;
    }
    return lines.size();
}

bool IsTopLevelAnonymousFunctionBoundaryLine(std::string_view raw_line) {
    if (raw_line.find_first_not_of(" \t") != 0) {
        return false;
    }

    const std::string_view trimmed =
        TrimWhitespace(refii::lua::StripLuaLineComment(raw_line));
    if (trimmed.empty()) {
        return false;
    }

    if (StartsWith(trimmed, ";(function") || StartsWith(trimmed, "(function") ||
        StartsWith(trimmed, "function(") || StartsWith(trimmed, "function ")) {
        return true;
    }

    const std::size_t equal = trimmed.find('=');
    if (equal == std::string_view::npos) {
        return false;
    }

    const std::string_view lhs = TrimWhitespace(trimmed.substr(0, equal));
    const std::string_view rhs = TrimWhitespace(trimmed.substr(equal + 1));
    return !lhs.empty() && StartsWith(rhs, "function");
}

bool TryRestoreMalformedLoopCallArguments(std::string& decompiled_source,
                                          std::string_view validation_error,
                                          std::string* note) {
    if (validation_error.find("near ','") == std::string_view::npos &&
        validation_error.find("near ')'") == std::string_view::npos) {
        return false;
    }

    LuaMutableTextContext source_context(decompiled_source);
    const std::vector<std::string>& lines = source_context.EnsureLines();
    if (lines.empty()) {
        return false;
    }

    const auto parse_ipairs_loop = [](std::string_view line,
                                      std::string* collection,
                                      std::string* loop_variable) -> bool {
        if (collection == nullptr || loop_variable == nullptr) {
            return false;
        }

        const std::string_view trimmed = refii::lua::TrimLuaWhitespace(
            refii::lua::StripLuaLineComment(line));
        if (!StartsWith(trimmed, "for ") || !EndsWith(trimmed, " do")) {
            return false;
        }

        const std::size_t in_ipairs = trimmed.find(" in ipairs(");
        if (in_ipairs == std::string_view::npos || in_ipairs <= 4) {
            return false;
        }

        const std::size_t close_paren = trimmed.rfind(')');
        if (close_paren == std::string_view::npos || close_paren <= in_ipairs + 11 ||
            close_paren + 4 != trimmed.size()) {
            return false;
        }

        std::string_view header = refii::lua::TrimLuaWhitespace(trimmed.substr(4, in_ipairs - 4));
        if (header.empty()) {
            return false;
        }

        std::string_view variable = header;
        const std::size_t last_comma = header.rfind(',');
        if (last_comma != std::string_view::npos) {
            variable = refii::lua::TrimLuaWhitespace(header.substr(last_comma + 1));
        }
        if (variable.empty()) {
            return false;
        }

        const std::string_view extracted_collection =
            refii::lua::TrimLuaWhitespace(trimmed.substr(in_ipairs + 11,
                                                         close_paren - (in_ipairs + 11)));
        if (extracted_collection.empty()) {
            return false;
        }

        *collection = std::string(extracted_collection);
        *loop_variable = std::string(variable);
        return true;
    };

    const auto rewrite_missing_call_argument = [](std::string_view line,
                                                  std::string_view loop_variable,
                                                  std::string* rewritten) -> bool {
        if (rewritten == nullptr || loop_variable.empty()) {
            return false;
        }

        std::string result(line);
        bool changed = false;
        bool in_single = false;
        bool in_double = false;
        bool escaped = false;

        auto is_blank = [](char ch) { return ch == ' ' || ch == '\t'; };

        for (std::size_t i = 0; i < result.size(); ++i) {
            const char ch = result[i];
            if (escaped) {
                escaped = false;
                continue;
            }
            if ((in_single || in_double) && ch == '\\') {
                escaped = true;
                continue;
            }
            if (!in_double && ch == '\'') {
                in_single = !in_single;
                continue;
            }
            if (!in_single && ch == '"') {
                in_double = !in_double;
                continue;
            }
            if (in_single || in_double) {
                continue;
            }

            if (ch != '(' && ch != ',') {
                continue;
            }

            std::size_t probe = i + 1;
            while (probe < result.size() && is_blank(result[probe])) {
                ++probe;
            }
            if (probe >= result.size() || result[probe] != ',') {
                continue;
            }

            const std::string replacement =
                ch == '(' ? std::string(loop_variable) : " " + std::string(loop_variable);
            result.replace(i + 1, probe - (i + 1), replacement);
            changed = true;
            i += replacement.size();
        }

        if (!changed) {
            return false;
        }

        *rewritten = std::move(result);
        return true;
    };

    LuaMutableTextContext candidate_context;
    std::string best_candidate;
    std::string best_error;
    for (std::size_t line_index = 0; line_index < lines.size(); ++line_index) {
        std::string collection;
        std::string loop_variable;
        if (!parse_ipairs_loop(lines[line_index], &collection, &loop_variable)) {
            continue;
        }

        const std::size_t loop_end = FindBalancedLuaBlockEnd(lines, line_index);
        if (loop_end >= lines.size() || loop_end <= line_index + 1) {
            continue;
        }

        const std::string loop_indent = GetLuaLineIndent(lines[line_index]);
        const std::string body_indent = loop_indent + "  ";
        std::vector<std::pair<std::size_t, std::string>> rewrites;
        for (std::size_t probe = line_index + 1; probe < loop_end; ++probe) {
            if (!GetLuaLineIndent(lines[probe]).starts_with(body_indent)) {
                continue;
            }
            if (lines[probe].find(loop_variable) != std::string::npos) {
                continue;
            }

            std::string rewritten;
            if (!rewrite_missing_call_argument(lines[probe], loop_variable, &rewritten)) {
                continue;
            }
            rewrites.emplace_back(probe, std::move(rewritten));
        }

        if (rewrites.empty()) {
            continue;
        }

        std::vector<std::string> candidate_lines = lines;
        for (const auto& rewrite : rewrites) {
            candidate_lines[rewrite.first] = rewrite.second;
        }

        candidate_context.SetLines(std::move(candidate_lines),
                                   source_context.HadTrailingNewline());
        std::string candidate(candidate_context.EnsureSource());
        const std::string candidate_error = candidate_context.ValidateRepairCached();
        if (candidate_error.empty()) {
            decompiled_source = std::move(candidate);
            if (note != nullptr) {
                *note = JoinNotes(*note, "syntax_repaired_restored_loop_call_argument");
            }
            return true;
        }
        if (IsValidationOutcomeBetter(validation_error, candidate_error) &&
            (best_error.empty() || IsValidationOutcomeBetter(best_error, candidate_error))) {
            best_candidate = std::move(candidate);
            best_error = candidate_error;
        }
    }

    if (!best_candidate.empty()) {
        decompiled_source = std::move(best_candidate);
        if (note != nullptr) {
            *note = JoinNotes(*note, "syntax_repaired_restored_loop_call_argument_progress");
        }
        return true;
    }

    return false;
}

bool TryReplaceFakeGeneratorLoopWithSiblingElseIf(std::string& decompiled_source,
                                                  std::string_view validation_error,
                                                  std::string* note) {
    if (validation_error.find("near 'for'") == std::string_view::npos) {
        return false;
    }

    LuaMutableTextContext source_context(decompiled_source);
    const std::vector<std::string>& lines = source_context.EnsureLines();
    if (lines.size() < 8) {
        return false;
    }

    const std::size_t reported_line = ExtractLuaErrorLine(validation_error);
    const auto raw_trim_line = [](std::string_view line) -> std::string_view {
        return TrimWhitespace(line);
    };
    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimWhitespace(refii::lua::StripLuaLineComment(line));
    };
    const auto line_indent = [](std::string_view line) -> std::string {
        return GetLuaLineIndent(line);
    };
    const auto next_nonempty = [&](std::size_t start) -> std::size_t {
        return FindNextNonEmptyLuaLine(lines, start);
    };
    const auto previous_nonempty = [&](std::size_t start) -> std::size_t {
        return FindPreviousNonEmptyLuaLine(lines, start);
    };
    const auto previous_nonempty_in_segment =
        [&](const std::vector<std::string>& segment, std::size_t start) -> std::size_t {
            if (start == 0 || start > segment.size()) {
                return segment.size();
            }
            for (std::size_t i = start; i-- > 0;) {
                if (!trim_line(segment[i]).empty()) {
                    return i;
                }
                if (i == 0) {
                    break;
                }
            }
            return segment.size();
        };
    const auto parse_if_condition = [](std::string_view line,
                                       std::string* condition,
                                       std::string* lhs,
                                       std::string* rhs) -> bool {
        if (condition == nullptr || lhs == nullptr || rhs == nullptr) {
            return false;
        }

        const std::string_view trimmed = refii::lua::TrimLuaWhitespace(
            refii::lua::StripLuaLineComment(line));
        if (!StartsWith(trimmed, "if ") || !EndsWith(trimmed, " then")) {
            return false;
        }

        const std::string_view extracted =
            refii::lua::TrimLuaWhitespace(trimmed.substr(3, trimmed.size() - 8));
        const std::size_t equals = extracted.find("==");
        if (equals == std::string_view::npos) {
            return false;
        }

        const std::string_view extracted_lhs =
            refii::lua::TrimLuaWhitespace(extracted.substr(0, equals));
        const std::string_view extracted_rhs =
            refii::lua::TrimLuaWhitespace(extracted.substr(equals + 2));
        if (extracted_lhs.empty() || extracted_rhs.empty()) {
            return false;
        }

        *condition = std::string(extracted);
        *lhs = std::string(extracted_lhs);
        *rhs = std::string(extracted_rhs);
        return true;
    };
    const auto parse_if_header = [](std::string_view line, std::string* condition) -> bool {
        return ParseLuaIfCondition(line, condition);
    };
    const auto is_simple_predicate_condition = [](std::string_view condition) {
        if (condition.empty()) {
            return false;
        }
        if (condition.find("==") != std::string_view::npos ||
            condition.find("~=") != std::string_view::npos ||
            condition.find("<") != std::string_view::npos ||
            condition.find(">") != std::string_view::npos ||
            condition.find(" and ") != std::string_view::npos ||
            condition.find(" or ") != std::string_view::npos) {
            return false;
        }

        std::string_view candidate = refii::lua::TrimLuaWhitespace(condition);
        if (StartsWith(candidate, "not ")) {
            candidate = refii::lua::TrimLuaWhitespace(candidate.substr(4));
        }
        return candidate.find('(') != std::string_view::npos &&
               candidate.find(')') != std::string_view::npos;
    };
    const auto find_block_end = [&](std::size_t start) -> std::size_t {
        return FindBalancedLuaBlockEnd(lines, start);
    };
    SyntaxRepairCandidateTracker candidate_tracker(validation_error,
                                                   source_context.HadTrailingNewline(),
                                                   &decompiled_source,
                                                   note);

    for (std::size_t fake_for_line = 0; fake_for_line < lines.size(); ++fake_for_line) {
        const std::string_view fake_trimmed = trim_line(lines[fake_for_line]);
        if (!StartsWith(fake_trimmed, "for ") ||
            fake_trimmed.find(" in (for generator) do") == std::string_view::npos) {
            continue;
        }
        if (reported_line != 0) {
            const std::size_t current_line = fake_for_line + 1;
            if (current_line + 64 < reported_line || current_line > reported_line + 8) {
                continue;
            }
        }

        const std::string fake_indent(line_indent(lines[fake_for_line]));
        const std::size_t fake_end_line = next_nonempty(fake_for_line + 1);
        if (fake_end_line >= lines.size() || trim_line(lines[fake_end_line]) != "end" ||
            line_indent(lines[fake_end_line]) != fake_indent) {
            continue;
        }

        std::size_t outer_if_line = previous_nonempty(fake_for_line);
        while (outer_if_line < lines.size()) {
            const std::string_view candidate_trimmed = trim_line(lines[outer_if_line]);
            if (StartsWith(candidate_trimmed, "if ") &&
                line_indent(lines[outer_if_line]).size() < fake_indent.size()) {
                break;
            }
            outer_if_line = previous_nonempty(outer_if_line);
        }
        if (outer_if_line >= lines.size()) {
            continue;
        }

        std::string outer_condition;
        std::string outer_lhs;
        std::string outer_rhs;
        if (!parse_if_header(lines[outer_if_line], &outer_condition)) {
            continue;
        }
        const bool outer_is_equality = parse_if_condition(lines[outer_if_line],
                                                          &outer_condition,
                                                          &outer_lhs,
                                                          &outer_rhs);
        const bool outer_is_simple_predicate =
            !outer_is_equality && is_simple_predicate_condition(outer_condition);
        if (!outer_is_equality && !outer_is_simple_predicate) {
            continue;
        }

        const std::string outer_indent(line_indent(lines[outer_if_line]));
        if (!fake_indent.starts_with(outer_indent) ||
            fake_indent.size() != outer_indent.size() + 2) {
            continue;
        }

        std::size_t parent_block_line = previous_nonempty(outer_if_line);
        while (parent_block_line < lines.size()) {
            const std::string_view candidate_trimmed = trim_line(lines[parent_block_line]);
            if ((StartsWith(candidate_trimmed, "for ") && EndsWith(candidate_trimmed, " do")) ||
                StartsWith(candidate_trimmed, "if ")) {
                if (line_indent(lines[parent_block_line]).size() < outer_indent.size()) {
                    break;
                }
            }
            parent_block_line = previous_nonempty(parent_block_line);
        }
        if (parent_block_line >= lines.size()) {
            continue;
        }

        std::size_t containing_for_line = lines.size();
        const std::string parent_indent(line_indent(lines[parent_block_line]));
        const std::string_view parent_trimmed = trim_line(lines[parent_block_line]);
        if (StartsWith(parent_trimmed, "for ") && EndsWith(parent_trimmed, " do")) {
            containing_for_line = parent_block_line;
            if (!outer_indent.starts_with(parent_indent) ||
                outer_indent.size() != parent_indent.size() + 2) {
                continue;
            }
        } else if (StartsWith(parent_trimmed, "if ")) {
            if (!outer_indent.starts_with(parent_indent) ||
                outer_indent.size() != parent_indent.size() + 2) {
                continue;
            }

            containing_for_line = previous_nonempty(parent_block_line);
            while (containing_for_line < lines.size()) {
                const std::string_view candidate_trimmed =
                    trim_line(lines[containing_for_line]);
                if (StartsWith(candidate_trimmed, "for ") &&
                    EndsWith(candidate_trimmed, " do") &&
                    line_indent(lines[containing_for_line]).size() < parent_indent.size()) {
                    break;
                }
                containing_for_line = previous_nonempty(containing_for_line);
            }
            if (containing_for_line >= lines.size()) {
                continue;
            }
            const std::string for_indent(line_indent(lines[containing_for_line]));
            if (!parent_indent.starts_with(for_indent) ||
                parent_indent.size() != for_indent.size() + 2) {
                continue;
            }
        } else {
            continue;
        }

        const std::string for_indent(line_indent(lines[containing_for_line]));

        const std::size_t nested_if_line = next_nonempty(fake_end_line + 1);
        if (nested_if_line >= lines.size()) {
            continue;
        }

        std::string nested_condition;
        std::string nested_lhs;
        std::string nested_rhs;
        if (!parse_if_header(lines[nested_if_line], &nested_condition)) {
            continue;
        }
        const bool nested_is_equality = parse_if_condition(lines[nested_if_line],
                                                           &nested_condition,
                                                           &nested_lhs,
                                                           &nested_rhs);
        const bool nested_is_simple_predicate =
            !nested_is_equality && is_simple_predicate_condition(nested_condition);
        if (!nested_is_equality && !nested_is_simple_predicate) {
            continue;
        }

        const std::string nested_indent(line_indent(lines[nested_if_line]));
        if (nested_indent != fake_indent) {
            continue;
        }
        if (outer_is_equality != nested_is_equality) {
            continue;
        }
        if (outer_is_equality &&
            (nested_lhs != outer_lhs || nested_rhs == outer_rhs)) {
            continue;
        }

        const std::size_t nested_if_end_line = find_block_end(nested_if_line);
        if (nested_if_end_line >= lines.size() ||
            trim_line(lines[nested_if_end_line]) != "end") {
            continue;
        }

        const std::size_t outer_if_end_line = next_nonempty(nested_if_end_line + 1);
        if (outer_if_end_line >= lines.size() ||
            trim_line(lines[outer_if_end_line]) != "end" ||
            line_indent(lines[outer_if_end_line]) != outer_indent) {
            continue;
        }

        const std::size_t continuation_line = next_nonempty(outer_if_end_line + 1);
        const std::size_t next_outer_branch_line =
            continuation_line < lines.size() ? next_nonempty(continuation_line + 1)
                                             : lines.size();
        const bool should_close_for_before_continuation =
            continuation_line < lines.size() && next_outer_branch_line < lines.size() &&
            line_indent(lines[continuation_line]) == outer_indent &&
            StartsWith(trim_line(lines[next_outer_branch_line]), "elseif ");

        std::vector<std::string> candidate_lines;
        candidate_lines.reserve(lines.size());
        candidate_lines.insert(candidate_lines.end(),
                               lines.begin(),
                               lines.begin() + static_cast<std::ptrdiff_t>(fake_for_line));
        candidate_lines.push_back(outer_indent + "elseif " + nested_condition + " then");

        for (std::size_t probe = nested_if_line + 1; probe < nested_if_end_line; ++probe) {
            std::string moved_line = lines[probe];
            const std::string current_indent = line_indent(moved_line);
            if (current_indent.starts_with(nested_indent)) {
                moved_line = outer_indent +
                             std::string(current_indent.substr(nested_indent.size())) +
                             std::string(moved_line.substr(current_indent.size()));
            }
            candidate_lines.push_back(std::move(moved_line));
        }

        candidate_lines.push_back(lines[outer_if_end_line]);
        if (should_close_for_before_continuation) {
            candidate_lines.push_back(for_indent + "end");
        }
        candidate_lines.insert(candidate_lines.end(),
                               lines.begin() + static_cast<std::ptrdiff_t>(outer_if_end_line + 1),
                               lines.end());

        if (candidate_tracker.Consider(
                std::move(candidate_lines),
                "syntax_repaired_replaced_fake_generator_loop_with_sibling_elseif")) {
            return true;
        }
    }

    return candidate_tracker.CommitBestProgress(
        "syntax_repaired_replaced_fake_generator_loop_with_sibling_elseif_progress");
}

bool TryReplaceFakeGeneratorLoopWithElseBreak(std::string& decompiled_source,
                                              std::string_view validation_error,
                                              std::string* note) {
    if (validation_error.find("near 'for'") == std::string_view::npos) {
        return false;
    }

    bool had_trailing_newline = false;
    const std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);
    if (lines.size() < 8) {
        return false;
    }

    const std::size_t reported_line = ExtractLuaErrorLine(validation_error);
    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimWhitespace(refii::lua::StripLuaLineComment(line));
    };
    const auto line_indent = [](std::string_view line) -> std::string_view {
        const std::size_t first_non_ws = line.find_first_not_of(" \t");
        if (first_non_ws == std::string_view::npos) {
            return line;
        }
        return line.substr(0, first_non_ws);
    };
    const auto next_nonempty = [&](std::size_t start) -> std::size_t {
        for (std::size_t i = start; i < lines.size(); ++i) {
            if (!trim_line(lines[i]).empty()) {
                return i;
            }
        }
        return lines.size();
    };
    const auto previous_nonempty = [&](std::size_t start) -> std::size_t {
        if (start == 0 || start > lines.size()) {
            return lines.size();
        }
        for (std::size_t i = start; i-- > 0;) {
            if (!trim_line(lines[i]).empty()) {
                return i;
            }
            if (i == 0) {
                break;
            }
        }
        return lines.size();
    };
    const auto is_if_header = [&](std::size_t line_index) -> bool {
        if (line_index >= lines.size()) {
            return false;
        }
        const std::string_view trimmed = trim_line(lines[line_index]);
        return StartsWith(trimmed, "if ") && EndsWith(trimmed, " then");
    };

    SyntaxRepairCandidateTracker candidate_tracker(validation_error,
                                                   had_trailing_newline,
                                                   &decompiled_source,
                                                   note);

    for (std::size_t fake_for_line = 0; fake_for_line < lines.size(); ++fake_for_line) {
        const std::string_view fake_trimmed = trim_line(lines[fake_for_line]);
        if (!StartsWith(fake_trimmed, "for ") ||
            fake_trimmed.find(" in (for generator) do") == std::string_view::npos) {
            continue;
        }
        if (reported_line != 0) {
            const std::size_t current_line = fake_for_line + 1;
            if (current_line + 32 < reported_line || current_line > reported_line + 8) {
                continue;
            }
        }

        const std::string fake_indent(line_indent(lines[fake_for_line]));
        const std::size_t fake_end_line = next_nonempty(fake_for_line + 1);
        const std::size_t if_end_line = previous_nonempty(fake_for_line);
        if (fake_end_line >= lines.size() || if_end_line >= lines.size() ||
            trim_line(lines[fake_end_line]) != "end" ||
            trim_line(lines[if_end_line]) != "end" ||
            line_indent(lines[fake_end_line]) != fake_indent ||
            line_indent(lines[if_end_line]) != fake_indent) {
            continue;
        }

        std::size_t if_line = lines.size();
        for (std::size_t probe = if_end_line; probe-- > 0 && if_end_line - probe <= 32;) {
            if (is_if_header(probe) && line_indent(lines[probe]) == fake_indent) {
                if_line = probe;
                break;
            }
            if (probe == 0) {
                break;
            }
        }
        if (if_line >= lines.size()) {
            continue;
        }

        bool saw_enclosing_for = false;
        for (std::size_t probe = if_line; probe-- > 0 && if_line - probe <= 32;) {
            const std::string_view probe_trimmed = trim_line(lines[probe]);
            const std::string_view probe_indent = line_indent(lines[probe]);
            if (StartsWith(probe_trimmed, "for ") && EndsWith(probe_trimmed, " do") &&
                probe_indent.size() < fake_indent.size() &&
                StartsWith(fake_indent, probe_indent)) {
                saw_enclosing_for = true;
                break;
            }
            if (probe == 0) {
                break;
            }
        }
        if (!saw_enclosing_for) {
            continue;
        }

        std::vector<std::string> candidate_lines = lines;
        candidate_lines[if_end_line] = fake_indent + "else";
        candidate_lines[fake_for_line] = fake_indent + "  break";
        if (candidate_tracker.Consider(
                std::move(candidate_lines),
                "syntax_repaired_replaced_fake_generator_loop_with_else_break")) {
            return true;
        }
    }

    return candidate_tracker.CommitBestProgress(
        "syntax_repaired_replaced_fake_generator_loop_with_else_break_progress");
}

bool TryRebalanceNestedStateElseIfChains(std::string& decompiled_source,
                                         std::string_view validation_error,
                                         std::string* note) {
    if (validation_error.find("near 'else'") == std::string_view::npos &&
        validation_error.find("near 'elseif'") == std::string_view::npos &&
        validation_error.find("near 'end'") == std::string_view::npos) {
        return false;
    }

    bool had_trailing_newline = false;
    const std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);
    if (lines.size() < 4) {
        return false;
    }
    const std::size_t reported_line = ExtractLuaErrorLine(validation_error);

    const auto next_nonempty = [&](std::size_t start) -> std::size_t {
        for (std::size_t i = start; i < lines.size(); ++i) {
            if (!TrimWhitespace(refii::lua::StripLuaLineComment(lines[i])).empty()) {
                return i;
            }
        }
        return lines.size();
    };

    const auto previous_nonempty = [&](std::size_t start) -> std::size_t {
        if (start == 0 || start > lines.size()) {
            return lines.size();
        }
        for (std::size_t i = start; i-- > 0;) {
            if (!TrimWhitespace(refii::lua::StripLuaLineComment(lines[i])).empty()) {
                return i;
            }
            if (i == 0) {
                break;
            }
        }
        return lines.size();
    };
    const auto line_indent = [](std::string_view line) -> std::string_view {
        const std::size_t first_non_ws = line.find_first_not_of(" \t");
        if (first_non_ws == std::string_view::npos) {
            return line;
        }
        return line.substr(0, first_non_ws);
    };

    const auto parse_if_condition = [](std::string_view line, std::string* condition) -> bool {
        if (condition == nullptr) {
            return false;
        }

        const std::string_view trimmed = refii::lua::TrimLuaWhitespace(
            refii::lua::StripLuaLineComment(line));
        if (!StartsWith(trimmed, "if ") || !EndsWith(trimmed, " then")) {
            return false;
        }

        const std::string_view extracted =
            refii::lua::TrimLuaWhitespace(trimmed.substr(3, trimmed.size() - 8));
        if (extracted.empty()) {
            return false;
        }

        *condition = std::string(extracted);
        return true;
    };

    const auto extract_state_lhs = [](std::string_view condition, std::string* lhs) -> bool {
        if (lhs == nullptr) {
            return false;
        }
        const std::size_t equals = condition.find("==");
        if (equals == std::string_view::npos) {
            return false;
        }

        const std::string_view extracted =
            refii::lua::TrimLuaWhitespace(condition.substr(0, equals));
        if (extracted.empty()) {
            return false;
        }

        if (extracted.find("State") == std::string_view::npos &&
            extracted.find("ThreadVar") == std::string_view::npos) {
            return false;
        }

        *lhs = std::string(extracted);
        return true;
    };

    const auto has_recent_state_assignment = [&](std::size_t else_line,
                                                 std::string_view lhs,
                                                 std::string_view else_indent) -> bool {
        std::size_t matches = 0;
        for (std::size_t probe = else_line; probe-- > 0 && matches < 64;) {
            const std::string_view trimmed = refii::lua::TrimLuaWhitespace(
                refii::lua::StripLuaLineComment(lines[probe]));
            if (trimmed.empty()) {
                if (probe == 0) {
                    break;
                }
                continue;
            }

            ++matches;
            if (trimmed == "end" || trimmed == "else" || StartsWith(trimmed, "elseif ")) {
                if (probe == 0) {
                    break;
                }
                continue;
            }

            const std::string_view indent = line_indent(lines[probe]);
            if (indent.size() < else_indent.size()) {
                break;
            }

            const std::string expected = std::string(lhs) + " =";
            if (StartsWith(trimmed, expected)) {
                return true;
            }

            if (probe == 0) {
                break;
            }
        }
        return false;
    };

    const auto rewrite_local_state_elseif =
        [&](const std::vector<std::string>& source_lines,
            std::size_t else_line,
            std::size_t if_line,
            std::string_view condition,
            bool replace_else_with_end,
            std::string* candidate_out) -> bool {
        if (candidate_out == nullptr || else_line >= source_lines.size() ||
            if_line >= source_lines.size()) {
            return false;
        }

        const std::string else_indent(line_indent(source_lines[else_line]));
        const std::string if_indent(line_indent(source_lines[if_line]));
        if (if_indent.size() <= else_indent.size() || !StartsWith(if_indent, else_indent)) {
            return false;
        }

        std::vector<std::string> candidate_lines = source_lines;
        if (replace_else_with_end) {
            candidate_lines[else_line] = else_indent + "end";
            candidate_lines[if_line] = else_indent + "elseif " + std::string(condition) + " then";
        } else {
            candidate_lines.erase(candidate_lines.begin() + static_cast<std::ptrdiff_t>(else_line));
            candidate_lines[if_line - 1] = else_indent + "elseif " + std::string(condition) + " then";
        }

        *candidate_out = JoinLines(candidate_lines, had_trailing_newline);
        return true;
    };

    auto try_record_candidate = [&](std::string candidate,
                                    std::string_view candidate_note,
                                    std::string* best_candidate_out,
                                    std::string* best_error_out,
                                    std::string* best_note_out) -> bool {
        const std::string candidate_error = ValidateLuaRepairSource(candidate);
        if (candidate_error.empty()) {
            decompiled_source = std::move(candidate);
            if (note != nullptr) {
                *note = JoinNotes(*note, std::string(candidate_note));
            }
            return true;
        }

        if (IsValidationOutcomeBetter(validation_error, candidate_error) &&
            (best_error_out->empty() ||
             IsValidationOutcomeBetter(*best_error_out, candidate_error))) {
            *best_candidate_out = std::move(candidate);
            *best_error_out = candidate_error;
            *best_note_out = std::string(candidate_note) + "_progress";
        }
        return false;
    };

    std::string best_candidate;
    std::string best_error;
    std::string best_note;

    if (reported_line != 0 && reported_line - 1 < lines.size() &&
        TrimWhitespace(refii::lua::StripLuaLineComment(lines[reported_line - 1])) == "else") {
        const std::size_t inner_else_line = reported_line - 1;
        const std::size_t inner_if_line = next_nonempty(inner_else_line + 1);
        std::string inner_condition;
        std::string inner_lhs;
        if (inner_if_line < lines.size() &&
            parse_if_condition(lines[inner_if_line], &inner_condition) &&
            extract_state_lhs(inner_condition, &inner_lhs)) {
            const std::size_t previous_index = previous_nonempty(inner_else_line);
            const std::string inner_else_indent(line_indent(lines[inner_else_line]));
            if (previous_index < lines.size() &&
                TrimWhitespace(refii::lua::StripLuaLineComment(lines[previous_index])) == "end") {
                for (std::size_t outer_else_line = inner_else_line;
                     outer_else_line-- > 0 && inner_else_line - outer_else_line <= 24;) {
                    if (TrimWhitespace(refii::lua::StripLuaLineComment(lines[outer_else_line])) !=
                        "else") {
                        continue;
                    }

                    const std::size_t outer_if_line = next_nonempty(outer_else_line + 1);
                    std::string outer_condition;
                    std::string outer_lhs;
                    if (outer_if_line >= lines.size() ||
                        !parse_if_condition(lines[outer_if_line], &outer_condition) ||
                        !extract_state_lhs(outer_condition, &outer_lhs) ||
                        outer_lhs != inner_lhs) {
                        continue;
                    }

                    const std::string outer_else_indent(line_indent(lines[outer_else_line]));
                    if (inner_else_indent.size() <= outer_else_indent.size() ||
                        !StartsWith(inner_else_indent, outer_else_indent) ||
                        !has_recent_state_assignment(outer_else_line, outer_lhs, outer_else_indent)) {
                        continue;
                    }

                    std::string paired_candidate;
                    if (!rewrite_local_state_elseif(lines,
                                                    inner_else_line,
                                                    inner_if_line,
                                                    inner_condition,
                                                    true,
                                                    &paired_candidate)) {
                        continue;
                    }

                    bool nested_had_trailing_newline = false;
                    const std::vector<std::string> paired_lines =
                        SplitLines(paired_candidate, &nested_had_trailing_newline);
                    std::string paired_candidate_final;
                    if (!rewrite_local_state_elseif(paired_lines,
                                                    outer_else_line,
                                                    outer_if_line,
                                                    outer_condition,
                                                    false,
                                                    &paired_candidate_final)) {
                        continue;
                    }

                    if (try_record_candidate(
                            std::move(paired_candidate_final),
                            "syntax_repaired_rebalanced_nested_state_elseif_double",
                            &best_candidate,
                            &best_error,
                            &best_note)) {
                        return true;
                    }
                }
            }
        }
    }
    for (std::size_t else_line = 0; else_line < lines.size(); ++else_line) {
        if (TrimWhitespace(refii::lua::StripLuaLineComment(lines[else_line])) != "else") {
            continue;
        }
        if (reported_line != 0) {
            const std::size_t current_line = else_line + 1;
            if (current_line + 96 < reported_line || current_line > reported_line + 8) {
                continue;
            }
        }

        const std::size_t if_line = next_nonempty(else_line + 1);
        std::string condition;
        std::string lhs;
        if (if_line >= lines.size() || !parse_if_condition(lines[if_line], &condition)) {
            continue;
        }

        const std::string else_indent(line_indent(lines[else_line]));
        const std::string if_indent(line_indent(lines[if_line]));
        if (if_indent.size() <= else_indent.size() || !StartsWith(if_indent, else_indent)) {
            continue;
        }

        if (!extract_state_lhs(condition, &lhs) ||
            !has_recent_state_assignment(else_line, lhs, else_indent)) {
            continue;
        }

        for (std::size_t inner_else = else_line + 1;
             inner_else < lines.size() && inner_else <= else_line + 40;
             ++inner_else) {
            if (TrimWhitespace(refii::lua::StripLuaLineComment(lines[inner_else])) != "else") {
                continue;
            }
            if (reported_line != 0) {
                const std::size_t current_line = inner_else + 1;
                if (current_line + 24 < reported_line || current_line > reported_line + 16) {
                    continue;
                }
            }

            const std::size_t inner_if_line = next_nonempty(inner_else + 1);
            std::string inner_condition;
            std::string inner_lhs;
            if (inner_if_line >= lines.size() ||
                !parse_if_condition(lines[inner_if_line], &inner_condition) ||
                !extract_state_lhs(inner_condition, &inner_lhs) || inner_lhs != lhs) {
                continue;
            }

            const std::size_t previous_index = previous_nonempty(inner_else);
            if (previous_index >= lines.size() ||
                TrimWhitespace(refii::lua::StripLuaLineComment(lines[previous_index])) != "end") {
                continue;
            }

            std::string paired_candidate;
            if (!rewrite_local_state_elseif(lines,
                                            inner_else,
                                            inner_if_line,
                                            inner_condition,
                                            true,
                                            &paired_candidate)) {
                continue;
            }
            bool nested_had_trailing_newline = false;
            const std::vector<std::string> paired_lines =
                SplitLines(paired_candidate, &nested_had_trailing_newline);
            std::string paired_candidate_final;
            if (!rewrite_local_state_elseif(paired_lines,
                                            else_line,
                                            if_line,
                                            condition,
                                            false,
                                            &paired_candidate_final)) {
                continue;
            }

            if (try_record_candidate(std::move(paired_candidate_final),
                                     "syntax_repaired_rebalanced_nested_state_elseif_double",
                                     &best_candidate,
                                     &best_error,
                                     &best_note)) {
                return true;
            }
        }

        std::string candidate;
        if (!rewrite_local_state_elseif(lines,
                                        else_line,
                                        if_line,
                                        condition,
                                        false,
                                        &candidate)) {
            continue;
        }

        if (try_record_candidate(std::move(candidate),
                                 "syntax_repaired_rebalanced_nested_state_elseif",
                                 &best_candidate,
                                 &best_error,
                                 &best_note)) {
            return true;
        }
    }

    if (!best_candidate.empty()) {
        decompiled_source = std::move(best_candidate);
        if (note != nullptr) {
            *note = JoinNotes(*note,
                              best_note.empty()
                                  ? "syntax_repaired_rebalanced_nested_state_elseif_progress"
                                  : best_note);
        }
        return true;
    }

    return false;
}

bool TryRestoreNestedIfElseAfterDoubleEnd(std::string& decompiled_source,
                                          std::string_view validation_error,
                                          std::string* note) {
    if (validation_error.find("near 'else'") == std::string_view::npos &&
        validation_error.find("near 'elseif'") == std::string_view::npos &&
        validation_error.find("near 'end'") == std::string_view::npos) {
        return false;
    }

    bool had_trailing_newline = false;
    const std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);
    if (lines.size() < 6) {
        return false;
    }

    const std::size_t reported_line = ExtractLuaErrorLine(validation_error);
    SyntaxRepairCandidateTracker candidate_tracker(validation_error,
                                                   had_trailing_newline,
                                                   &decompiled_source,
                                                   note);
    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimWhitespace(refii::lua::StripLuaLineComment(line));
    };
    const auto line_indent = [](std::string_view line) -> std::string_view {
        const std::size_t first_non_ws = line.find_first_not_of(" \t");
        if (first_non_ws == std::string_view::npos) {
            return line;
        }
        return line.substr(0, first_non_ws);
    };
    const auto previous_nonempty = [&](std::size_t start) -> std::size_t {
        if (start == 0 || start > lines.size()) {
            return lines.size();
        }
        for (std::size_t i = start; i-- > 0;) {
            if (!trim_line(lines[i]).empty()) {
                return i;
            }
            if (i == 0) {
                break;
            }
        }
        return lines.size();
    };
    const auto next_nonempty = [&](std::size_t start) -> std::size_t {
        for (std::size_t i = start; i < lines.size(); ++i) {
            if (!trim_line(lines[i]).empty()) {
                return i;
            }
        }
        return lines.size();
    };
    const auto is_if_header = [&](std::size_t line_index) -> bool {
        if (line_index >= lines.size()) {
            return false;
        }
        const std::string_view trimmed = trim_line(lines[line_index]);
        return StartsWith(trimmed, "if ") && EndsWith(trimmed, " then");
    };

    for (std::size_t else_line = 2; else_line < lines.size(); ++else_line) {
        if (reported_line != 0) {
            const std::size_t current_line = else_line + 1;
            if (current_line + 96 < reported_line || current_line > reported_line + 8) {
                continue;
            }
        }

        if (trim_line(lines[else_line]) != "else") {
            continue;
        }

        const std::string else_indent(line_indent(lines[else_line]));
        const std::size_t end_line_1 = previous_nonempty(else_line);
        const std::size_t end_line_2 = previous_nonempty(end_line_1);
        if (end_line_1 >= lines.size() || end_line_2 >= lines.size() ||
            trim_line(lines[end_line_1]) != "end" ||
            trim_line(lines[end_line_2]) != "end") {
            continue;
        }

        if (line_indent(lines[end_line_1]) != else_indent ||
            line_indent(lines[end_line_2]) != else_indent) {
            continue;
        }

        const std::size_t else_body_line = next_nonempty(else_line + 1);
        if (else_body_line >= lines.size()) {
            continue;
        }
        const std::string else_body_indent(line_indent(lines[else_body_line]));
        if (else_body_indent.size() <= else_indent.size() ||
            !StartsWith(else_body_indent, else_indent)) {
            continue;
        }

        std::size_t inner_if_line = lines.size();
        for (std::size_t probe = end_line_2; probe-- > 0 && end_line_2 - probe <= 48;) {
            if (!is_if_header(probe)) {
                continue;
            }
            if (line_indent(lines[probe]) != else_indent) {
                continue;
            }
            inner_if_line = probe;
            break;
        }
        if (inner_if_line >= lines.size()) {
            continue;
        }

        bool saw_outer_if = false;
        for (std::size_t probe = inner_if_line; probe-- > 0 && inner_if_line - probe <= 48;) {
            if (!is_if_header(probe)) {
                continue;
            }
            const std::string_view probe_indent = line_indent(lines[probe]);
            if (probe_indent.size() >= else_indent.size() ||
                !StartsWith(else_indent, probe_indent)) {
                continue;
            }
            saw_outer_if = true;
            break;
        }
        if (!saw_outer_if) {
            continue;
        }

        std::vector<std::string> candidate_lines = lines;
        candidate_lines.erase(
            candidate_lines.begin() + static_cast<std::ptrdiff_t>(end_line_2),
            candidate_lines.begin() + static_cast<std::ptrdiff_t>(end_line_1 + 1));
        if (candidate_tracker.Consider(
                std::move(candidate_lines),
                "syntax_repaired_restored_nested_if_else_after_double_end")) {
            return true;
        }
    }

    return candidate_tracker.CommitBestProgress(
        "syntax_repaired_restored_nested_if_else_after_double_end_progress");
}

bool TryRebalanceTerminalMissionElseIf(std::string& decompiled_source,
                                       std::string_view validation_error,
                                       std::string* note) {
    if (validation_error.find("near 'elseif'") == std::string_view::npos &&
        validation_error.find("near 'else'") == std::string_view::npos &&
        validation_error.find("near 'end'") == std::string_view::npos) {
        return false;
    }

    bool had_trailing_newline = false;
    const std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);
    if (lines.size() < 4) {
        return false;
    }

    const SyntaxErrorDescriptor baseline_error = DescribeSyntaxError(validation_error);
    const auto line_indent = [](std::string_view line) -> std::string_view {
        const std::size_t first_non_ws = line.find_first_not_of(" \t");
        if (first_non_ws == std::string_view::npos) {
            return line;
        }
        return line.substr(0, first_non_ws);
    };
    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimWhitespace(refii::lua::StripLuaLineComment(line));
    };
    const auto previous_nonempty = [&](std::size_t start) -> std::size_t {
        if (start == 0 || start > lines.size()) {
            return lines.size();
        }
        for (std::size_t i = start; i-- > 0;) {
            if (!trim_line(lines[i]).empty()) {
                return i;
            }
            if (i == 0) {
                break;
            }
        }
        return lines.size();
    };
    const auto next_nonempty = [&](std::size_t start) -> std::size_t {
        for (std::size_t i = start; i < lines.size(); ++i) {
            if (!trim_line(lines[i]).empty()) {
                return i;
            }
        }
        return lines.size();
    };
    const auto is_mission_terminal_branch = [](std::string_view trimmed) -> bool {
        if (!StartsWith(trimmed, "elseif ") || !EndsWith(trimmed, " then")) {
            return false;
        }
        return trimmed.find("MissionSucceeded") != std::string_view::npos ||
               trimmed.find("MissionFailed") != std::string_view::npos;
    };
    const auto has_mission_root_guard = [&](std::string_view trimmed_branch) -> bool {
        std::string_view needle;
        if (trimmed_branch.find("MissionSucceeded") != std::string_view::npos) {
            needle = ".MissionSucceeded";
        } else if (trimmed_branch.find("MissionFailed") != std::string_view::npos) {
            needle = ".MissionFailed";
        } else {
            return false;
        }

        const std::size_t mission_pos = trimmed_branch.find(needle);
        if (mission_pos == std::string_view::npos) {
            return false;
        }

        std::size_t base_begin = mission_pos;
        while (base_begin > 0) {
            const char ch = trimmed_branch[base_begin - 1];
            if (std::isalnum(static_cast<unsigned char>(ch)) == 0 &&
                ch != '_' && ch != '.') {
                break;
            }
            --base_begin;
        }
        const std::string_view base = trimmed_branch.substr(base_begin, mission_pos - base_begin);
        if (base.empty()) {
            return false;
        }

        const std::string opener = "if not " + std::string(base) +
                                   ".MissionSucceeded and not " + std::string(base) +
                                   ".MissionFailed then";
        for (const std::string& line : lines) {
            if (trim_line(line) == opener) {
                return true;
            }
        }
        return false;
    };

    std::string best_candidate;
    std::string best_error;
    for (std::size_t branch_line = 0; branch_line < lines.size(); ++branch_line) {
        const std::string_view branch_trimmed = trim_line(lines[branch_line]);
        if (!is_mission_terminal_branch(branch_trimmed) ||
            !has_mission_root_guard(branch_trimmed)) {
            continue;
        }
        if (baseline_error.line != 0) {
            const std::size_t current_line = branch_line + 1;
            if (current_line + 64 < baseline_error.line || current_line > baseline_error.line + 16) {
                continue;
            }
        }

        const std::size_t yield_line = previous_nonempty(branch_line);
        if (yield_line >= lines.size() || trim_line(lines[yield_line]) != "coroutine.yield()") {
            continue;
        }

        const std::size_t run_end = previous_nonempty(yield_line);
        if (run_end >= lines.size() || trim_line(lines[run_end]) != "end") {
            continue;
        }

        std::size_t run_begin = run_end;
        while (run_begin > 0) {
            const std::size_t previous = previous_nonempty(run_begin);
            if (previous >= lines.size() || trim_line(lines[previous]) != "end") {
                break;
            }
            run_begin = previous;
        }

        const std::size_t run_length = run_end - run_begin + 1;
        const std::string_view branch_indent = line_indent(lines[branch_line]);
        const std::string_view yield_indent = line_indent(lines[yield_line]);
        if (run_length < 2 || yield_indent.size() != branch_indent.size() + 2 ||
            !StartsWith(yield_indent, branch_indent)) {
            continue;
        }

        const std::size_t branch_body = next_nonempty(branch_line + 1);
        if (branch_body >= lines.size() ||
            line_indent(lines[branch_body]).size() <= branch_indent.size() ||
            !StartsWith(line_indent(lines[branch_body]), branch_indent)) {
            continue;
        }

        for (int end_count = 1; end_count <= 4; ++end_count) {
            std::vector<std::string> candidate_lines = lines;
            candidate_lines.insert(candidate_lines.begin() +
                                       static_cast<std::ptrdiff_t>(branch_line),
                                   static_cast<std::size_t>(end_count),
                                   std::string(branch_indent) + "end");
            std::string candidate = JoinLines(candidate_lines, had_trailing_newline);
            const std::string candidate_error = ValidateLuaRepairSource(candidate);
            if (candidate_error.empty()) {
                decompiled_source = std::move(candidate);
                if (note != nullptr) {
                    *note = JoinNotes(*note,
                                      "syntax_repaired_rebalanced_terminal_mission_elseif");
                }
                return true;
            }

            const SyntaxErrorDescriptor candidate_descriptor =
                DescribeSyntaxError(candidate_error);
            if (candidate_descriptor.line <= baseline_error.line) {
                continue;
            }
            if (!IsValidationOutcomeBetter(validation_error, candidate_error)) {
                continue;
            }
            if (!best_error.empty()) {
                const SyntaxErrorDescriptor best_descriptor = DescribeSyntaxError(best_error);
                if (candidate_descriptor.line < best_descriptor.line ||
                    !IsValidationOutcomeBetter(best_error, candidate_error)) {
                    continue;
                }
            }

            best_candidate = std::move(candidate);
            best_error = candidate_error;
        }
    }

    if (best_candidate.empty()) {
        return false;
    }

    decompiled_source = std::move(best_candidate);
    if (note != nullptr) {
        *note = JoinNotes(*note,
                          "syntax_repaired_rebalanced_terminal_mission_elseif_progress");
    }
    return true;
}

bool TryCollapseNestedElseIfInsideStateClause(std::string& decompiled_source,
                                              std::string_view validation_error,
                                              std::string* note) {
    if (validation_error.find("near 'elseif'") == std::string_view::npos &&
        validation_error.find("near 'else'") == std::string_view::npos) {
        return false;
    }

    bool had_trailing_newline = false;
    const std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);
    if (lines.size() < 8) {
        return false;
    }

    const std::size_t reported_line = ExtractLuaErrorLine(validation_error);
    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimWhitespace(refii::lua::StripLuaLineComment(line));
    };
    const auto line_indent = [](std::string_view line) -> std::string_view {
        const std::size_t first_non_ws = line.find_first_not_of(" \t");
        if (first_non_ws == std::string_view::npos) {
            return line;
        }
        return line.substr(0, first_non_ws);
    };
    const auto next_nonempty = [&](std::size_t start) -> std::size_t {
        for (std::size_t i = start; i < lines.size(); ++i) {
            if (!trim_line(lines[i]).empty()) {
                return i;
            }
        }
        return lines.size();
    };
    const auto previous_nonempty = [&](std::size_t start) -> std::size_t {
        if (start == 0 || start > lines.size()) {
            return lines.size();
        }
        for (std::size_t i = start; i-- > 0;) {
            if (!trim_line(lines[i]).empty()) {
                return i;
            }
            if (i == 0) {
                break;
            }
        }
        return lines.size();
    };
    const auto previous_nonempty_in_segment =
        [&](const std::vector<std::string>& segment, std::size_t start) -> std::size_t {
            if (start == 0 || start > segment.size()) {
                return segment.size();
            }
            for (std::size_t i = start; i-- > 0;) {
                if (!trim_line(segment[i]).empty()) {
                    return i;
                }
                if (i == 0) {
                    break;
                }
            }
            return segment.size();
        };
    const auto parse_if_condition = [](std::string_view line, std::string* condition) -> bool {
        if (condition == nullptr) {
            return false;
        }

        const std::string_view trimmed = refii::lua::TrimLuaWhitespace(
            refii::lua::StripLuaLineComment(line));
        if ((!StartsWith(trimmed, "if ") && !StartsWith(trimmed, "elseif ")) ||
            !EndsWith(trimmed, " then")) {
            return false;
        }

        const std::size_t prefix = StartsWith(trimmed, "elseif ") ? 7 : 3;
        const std::string_view extracted =
            refii::lua::TrimLuaWhitespace(trimmed.substr(prefix, trimmed.size() - (prefix + 5)));
        if (extracted.empty()) {
            return false;
        }

        *condition = std::string(extracted);
        return true;
    };
    const auto extract_state_lhs = [](std::string_view condition, std::string* lhs) -> bool {
        if (lhs == nullptr) {
            return false;
        }
        const std::size_t equals = condition.find("==");
        if (equals == std::string_view::npos) {
            return false;
        }

        const std::string_view extracted =
            refii::lua::TrimLuaWhitespace(condition.substr(0, equals));
        if (extracted.empty()) {
            return false;
        }
        if (extracted.find("State") == std::string_view::npos &&
            extracted.find("ThreadVar") == std::string_view::npos) {
            return false;
        }

        *lhs = std::string(extracted);
        return true;
    };

    std::string best_candidate;
    std::string best_error;
    for (std::size_t state_clause_line = 0; state_clause_line < lines.size(); ++state_clause_line) {
        if (reported_line != 0) {
            const std::size_t current_line = state_clause_line + 1;
            if (current_line + 96 < reported_line || current_line > reported_line) {
                continue;
            }
        }

        std::string state_condition;
        std::string state_lhs;
        if (!parse_if_condition(lines[state_clause_line], &state_condition) ||
            !extract_state_lhs(state_condition, &state_lhs)) {
            continue;
        }

        const std::string state_indent(line_indent(lines[state_clause_line]));
        for (std::size_t outer_if_line = next_nonempty(state_clause_line + 1);
             outer_if_line < lines.size() && outer_if_line <= state_clause_line + 32 &&
             (reported_line == 0 || outer_if_line + 1 <= reported_line);
             outer_if_line = next_nonempty(outer_if_line + 1)) {
            std::string outer_condition;
            std::string discard_state_lhs;
            if (!parse_if_condition(lines[outer_if_line], &outer_condition) ||
                extract_state_lhs(outer_condition, &discard_state_lhs)) {
                continue;
            }

            const std::string outer_indent(line_indent(lines[outer_if_line]));
            if (outer_indent.size() != state_indent.size() + 2 ||
                !StartsWith(outer_indent, state_indent)) {
                continue;
            }

            std::size_t outer_else_line = lines.size();
            for (std::size_t probe = outer_if_line + 1;
                 probe < lines.size() && probe <= outer_if_line + 24;
                 ++probe) {
                if (trim_line(lines[probe]) == "else" &&
                    line_indent(lines[probe]) == outer_indent) {
                    outer_else_line = probe;
                    break;
                }
            }
            if (outer_else_line >= lines.size()) {
                continue;
            }

            const std::size_t nested_if_line = next_nonempty(outer_else_line + 1);
            std::string nested_condition;
            if (nested_if_line >= lines.size() ||
                !parse_if_condition(lines[nested_if_line], &nested_condition)) {
                continue;
            }

            const std::string nested_indent(line_indent(lines[nested_if_line]));
            if (nested_indent.size() != outer_indent.size() + 2 ||
                !StartsWith(nested_indent, outer_indent)) {
                continue;
            }

            std::size_t stray_else_line = lines.size();
            for (std::size_t probe = nested_if_line + 1;
                 probe < lines.size() && probe <= nested_if_line + 24 &&
                 (reported_line == 0 || probe + 1 <= reported_line + 8);
                 ++probe) {
                if (trim_line(lines[probe]) == "else" &&
                    line_indent(lines[probe]) == nested_indent) {
                    stray_else_line = probe;
                    break;
                }
            }
            if (stray_else_line >= lines.size()) {
                continue;
            }

            const std::size_t outer_next_branch = next_nonempty(stray_else_line + 1);
            if (outer_next_branch >= lines.size()) {
                continue;
            }

            bool found_next_state_branch = false;
            for (std::size_t probe = outer_next_branch;
                 probe < lines.size() && probe <= stray_else_line + 24;
                 probe = next_nonempty(probe + 1)) {
                std::string probe_condition;
                std::string probe_lhs;
                const std::string_view probe_indent = line_indent(lines[probe]);
                if (!parse_if_condition(lines[probe], &probe_condition) ||
                    !extract_state_lhs(probe_condition, &probe_lhs) || probe_lhs != state_lhs ||
                    (probe_indent != state_indent && probe_indent != outer_indent)) {
                    continue;
                }
                found_next_state_branch = true;
                break;
            }
            if (!found_next_state_branch) {
                continue;
            }

            const std::size_t run_end = previous_nonempty(stray_else_line);
            if (run_end >= lines.size() || trim_line(lines[run_end]) != "end") {
                continue;
            }

            std::size_t run_begin = run_end;
            while (run_begin > 0) {
                const std::size_t previous = previous_nonempty(run_begin);
                if (previous >= lines.size() || trim_line(lines[previous]) != "end" ||
                    line_indent(lines[previous]) != nested_indent) {
                    break;
                }
                run_begin = previous;
            }

            const std::size_t run_length = run_end - run_begin + 1;
            for (std::size_t remove_count = 1;
                 remove_count <= std::min<std::size_t>(run_length, 4);
                 ++remove_count) {
                std::vector<std::string> candidate_lines = lines;
                candidate_lines[outer_else_line] =
                    outer_indent + "elseif " + nested_condition + " then";

                const std::ptrdiff_t nested_if_offset =
                    static_cast<std::ptrdiff_t>(nested_if_line);
                candidate_lines.erase(candidate_lines.begin() + nested_if_offset);

                std::size_t adjusted_run_end = run_end;
                if (nested_if_line < adjusted_run_end) {
                    --adjusted_run_end;
                }

                candidate_lines.erase(
                    candidate_lines.begin() +
                        static_cast<std::ptrdiff_t>(adjusted_run_end - remove_count + 1),
                    candidate_lines.begin() +
                        static_cast<std::ptrdiff_t>(adjusted_run_end + 1));

                std::string candidate = JoinLines(candidate_lines, had_trailing_newline);
                const std::string candidate_error = ValidateLuaRepairSource(candidate);
                if (candidate_error.empty()) {
                    decompiled_source = std::move(candidate);
                    if (note != nullptr) {
                        *note = JoinNotes(
                            *note,
                            "syntax_repaired_collapsed_nested_state_else_if");
                    }
                    return true;
                }
                if (IsValidationOutcomeBetter(validation_error, candidate_error) &&
                    (best_error.empty() ||
                     IsValidationOutcomeBetter(best_error, candidate_error))) {
                    best_candidate = std::move(candidate);
                    best_error = candidate_error;
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
                          "syntax_repaired_collapsed_nested_state_else_if_progress");
    }
    return true;
}

bool TryCollapseNestedElseIfInsideClauseImpl(std::string& decompiled_source,
                                             std::string_view validation_error,
                                             std::string* note) {
    if (validation_error.find("near 'elseif'") == std::string_view::npos &&
        validation_error.find("near 'else'") == std::string_view::npos) {
        return false;
    }

    bool had_trailing_newline = false;
    const std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);
    if (lines.size() < 8) {
        return false;
    }

    const std::size_t reported_line = ExtractLuaErrorLine(validation_error);
    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimWhitespace(refii::lua::StripLuaLineComment(line));
    };
    const auto line_indent = [](std::string_view line) -> std::string_view {
        const std::size_t first_non_ws = line.find_first_not_of(" \t");
        if (first_non_ws == std::string_view::npos) {
            return line;
        }
        return line.substr(0, first_non_ws);
    };
    const auto next_nonempty = [&](std::size_t start) -> std::size_t {
        for (std::size_t i = start; i < lines.size(); ++i) {
            if (!trim_line(lines[i]).empty()) {
                return i;
            }
        }
        return lines.size();
    };
    const auto previous_nonempty_in_segment =
        [&](const std::vector<std::string>& segment, std::size_t start) -> std::size_t {
            if (start == 0 || start > segment.size()) {
                return segment.size();
            }
            for (std::size_t i = start; i-- > 0;) {
                if (!trim_line(segment[i]).empty()) {
                    return i;
                }
                if (i == 0) {
                    break;
                }
            }
            return segment.size();
        };
    const auto parse_if_condition = [](std::string_view line, std::string* condition) -> bool {
        if (condition == nullptr) {
            return false;
        }

        const std::string_view trimmed = refii::lua::TrimLuaWhitespace(
            refii::lua::StripLuaLineComment(line));
        if ((!StartsWith(trimmed, "if ") && !StartsWith(trimmed, "elseif ")) ||
            !EndsWith(trimmed, " then")) {
            return false;
        }

        const std::size_t prefix = StartsWith(trimmed, "elseif ") ? 7 : 3;
        const std::string_view extracted =
            refii::lua::TrimLuaWhitespace(trimmed.substr(prefix, trimmed.size() - (prefix + 5)));
        if (extracted.empty()) {
            return false;
        }

        *condition = std::string(extracted);
        return true;
    };

    std::string best_candidate;
    std::string best_error;
    for (std::size_t outer_if_line = 0; outer_if_line < lines.size(); ++outer_if_line) {
        if (reported_line != 0) {
            const std::size_t current_line = outer_if_line + 1;
            if (current_line + 48 < reported_line || current_line > reported_line + 8) {
                continue;
            }
        }

        std::string outer_condition;
        if (!parse_if_condition(lines[outer_if_line], &outer_condition)) {
            continue;
        }

        const std::string outer_indent(line_indent(lines[outer_if_line]));
        std::size_t outer_else_line = lines.size();
        for (std::size_t probe = outer_if_line + 1;
             probe < lines.size() && probe <= outer_if_line + 24;
             ++probe) {
            if (trim_line(lines[probe]) == "else" &&
                line_indent(lines[probe]) == outer_indent) {
                outer_else_line = probe;
                break;
            }
        }
        if (outer_else_line >= lines.size()) {
            continue;
        }

        const std::size_t nested_if_line = next_nonempty(outer_else_line + 1);
        std::string nested_condition;
        if (nested_if_line >= lines.size() ||
            !parse_if_condition(lines[nested_if_line], &nested_condition)) {
            continue;
        }

        const std::string nested_indent(line_indent(lines[nested_if_line]));
        if (nested_indent.size() != outer_indent.size() + 2 ||
            !StartsWith(nested_indent, outer_indent)) {
            continue;
        }

        const std::size_t nested_if_body_start = next_nonempty(nested_if_line + 1);
        if (nested_if_body_start >= lines.size()) {
            continue;
        }

        std::size_t nested_if_end_line = lines.size();
        for (std::size_t probe = nested_if_body_start + 1;
             probe < lines.size() && probe <= nested_if_line + 12;
             ++probe) {
            if (trim_line(lines[probe]) == "end" &&
                line_indent(lines[probe]) == nested_indent) {
                nested_if_end_line = probe;
                break;
            }
        }
        if (nested_if_end_line >= lines.size()) {
            continue;
        }

        std::size_t stray_else_line = next_nonempty(nested_if_end_line + 1);
        if (stray_else_line < lines.size() &&
            trim_line(lines[stray_else_line]) == "end" &&
            line_indent(lines[stray_else_line]) == nested_indent) {
            stray_else_line = next_nonempty(stray_else_line + 1);
        }
        if (stray_else_line >= lines.size() ||
            trim_line(lines[stray_else_line]) != "else" ||
            line_indent(lines[stray_else_line]) != nested_indent) {
            continue;
        }

        const std::size_t inner_else_body_start = next_nonempty(stray_else_line + 1);
        if (inner_else_body_start >= lines.size()) {
            continue;
        }

        std::size_t inner_else_end_line = lines.size();
        for (std::size_t probe = inner_else_body_start + 1;
             probe < lines.size() && probe <= stray_else_line + 12;
             ++probe) {
            if (trim_line(lines[probe]) == "end" &&
                line_indent(lines[probe]) == nested_indent) {
                inner_else_end_line = probe;
                break;
            }
        }
        if (inner_else_end_line >= lines.size()) {
            continue;
        }

        bool found_next_outer_branch = false;
        for (std::size_t probe = next_nonempty(inner_else_end_line + 1);
             probe < lines.size() && probe <= inner_else_end_line + 16;
             probe = next_nonempty(probe + 1)) {
            const std::string_view probe_trimmed = trim_line(lines[probe]);
            if ((probe_trimmed == "else" || StartsWith(probe_trimmed, "elseif ")) &&
                line_indent(lines[probe]) == outer_indent) {
                found_next_outer_branch = true;
                break;
            }
        }
        if (!found_next_outer_branch) {
            continue;
        }

        const std::string nested_body_indent = nested_indent + "  ";
        const std::string outer_body_indent = outer_indent + "  ";

        std::vector<std::string> candidate_lines;
        candidate_lines.reserve(lines.size());
        candidate_lines.insert(candidate_lines.end(),
                               lines.begin(),
                               lines.begin() + static_cast<std::ptrdiff_t>(outer_else_line));
        candidate_lines.push_back(outer_indent + "elseif " + nested_condition + " then");
        AppendReindentedLuaBlock(lines,
                                 nested_if_body_start,
                                 nested_if_end_line,
                                 nested_body_indent,
                                 outer_body_indent,
                                 &candidate_lines,
                                 true);
        candidate_lines.push_back(outer_indent + "else");
        AppendReindentedLuaBlock(lines,
                                 inner_else_body_start,
                                 inner_else_end_line,
                                 nested_body_indent,
                                 outer_body_indent,
                                 &candidate_lines,
                                 true);
        candidate_lines.push_back(outer_indent + "end");
        candidate_lines.insert(candidate_lines.end(),
                               lines.begin() +
                                   static_cast<std::ptrdiff_t>(inner_else_end_line + 1),
                               lines.end());

        std::string candidate = JoinLines(candidate_lines, had_trailing_newline);
        const std::string candidate_error =
            ValidateLuaRepairSource(candidate);
        if (candidate_error.empty()) {
            decompiled_source = std::move(candidate);
            if (note != nullptr) {
                *note = JoinNotes(
                    *note,
                    "syntax_repaired_collapsed_nested_else_if_inside_clause");
            }
            return true;
        }
        if (IsValidationOutcomeBetter(validation_error, candidate_error) &&
            (best_error.empty() ||
             IsValidationOutcomeBetter(best_error, candidate_error))) {
            best_candidate = std::move(candidate);
            best_error = candidate_error;
        }
    }

    if (best_candidate.empty()) {
        return false;
    }

    decompiled_source = std::move(best_candidate);
    if (note != nullptr) {
        *note = JoinNotes(
            *note,
            "syntax_repaired_collapsed_nested_else_if_inside_clause_progress");
    }
    return true;
}

bool TryRebuildFlattenedStateGuardElseIfBranch(std::string& decompiled_source,
                                               std::string_view validation_error,
                                               std::string* note) {
    if (validation_error.find("'end' expected") == std::string_view::npos &&
        validation_error.find("'<eof>' expected") == std::string_view::npos &&
        validation_error.find("near 'end'") == std::string_view::npos) {
        return false;
    }

    bool had_trailing_newline = false;
    const std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);
    if (lines.size() < 16) {
        return false;
    }

    const std::size_t reported_line = ExtractLuaErrorLine(validation_error);
    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimWhitespace(refii::lua::StripLuaLineComment(line));
    };
    const auto line_indent = [](std::string_view line) -> std::string_view {
        const std::size_t first_non_ws = line.find_first_not_of(" \t");
        if (first_non_ws == std::string_view::npos) {
            return line;
        }
        return line.substr(0, first_non_ws);
    };
    const auto next_nonempty = [&](std::size_t start) -> std::size_t {
        for (std::size_t i = start; i < lines.size(); ++i) {
            if (!trim_line(lines[i]).empty()) {
                return i;
            }
        }
        return lines.size();
    };
    const auto previous_nonempty = [&](std::size_t start) -> std::size_t {
        if (start == 0 || start > lines.size()) {
            return lines.size();
        }
        for (std::size_t i = start; i-- > 0;) {
            if (!trim_line(lines[i]).empty()) {
                return i;
            }
            if (i == 0) {
                break;
            }
        }
        return lines.size();
    };
    const auto parse_elseif_condition = [](std::string_view line,
                                           std::string* condition) -> bool {
        if (condition == nullptr) {
            return false;
        }

        const std::string_view trimmed = refii::lua::TrimLuaWhitespace(
            refii::lua::StripLuaLineComment(line));
        if (!StartsWith(trimmed, "elseif ") || !EndsWith(trimmed, " then")) {
            return false;
        }

        const std::string_view extracted =
            refii::lua::TrimLuaWhitespace(trimmed.substr(7, trimmed.size() - 12));
        if (extracted.empty()) {
            return false;
        }

        *condition = std::string(extracted);
        return true;
    };
    const auto split_on_and = [](std::string_view condition) {
        std::vector<std::string> parts;
        std::size_t start = 0;
        while (start < condition.size()) {
            const std::size_t and_pos = condition.find(" and ", start);
            const std::size_t end =
                and_pos == std::string_view::npos ? condition.size() : and_pos;
            const std::string_view piece =
                refii::lua::TrimLuaWhitespace(condition.substr(start, end - start));
            if (!piece.empty()) {
                parts.emplace_back(piece);
            }
            if (and_pos == std::string_view::npos) {
                break;
            }
            start = and_pos + 5;
        }
        return parts;
    };
    const auto append_reindented_range =
        [&](std::size_t start,
            std::size_t end_exclusive,
            std::string_view source_base,
            std::string_view target_base,
            std::vector<std::string>* out_lines) {
            if (out_lines == nullptr || start >= end_exclusive || end_exclusive > lines.size()) {
                return;
            }

            for (std::size_t i = start; i < end_exclusive; ++i) {
                const std::string_view trimmed = trim_line(lines[i]);
                if (trimmed.empty()) {
                    out_lines->push_back(std::string(lines[i]));
                    continue;
                }

                const std::string_view current_indent = line_indent(lines[i]);
                if (current_indent.starts_with(source_base)) {
                    out_lines->push_back(std::string(target_base) +
                                         std::string(current_indent.substr(source_base.size())) +
                                         std::string(lines[i].substr(current_indent.size())));
                } else {
                    out_lines->push_back(std::string(target_base) + std::string(trimmed));
                }
            }
        };
    const auto is_true_assignment = [&](std::size_t start,
                                        std::size_t end_exclusive,
                                        std::string_view expression) {
        if (expression.empty()) {
            return false;
        }
        const std::string exact = std::string(expression) + " = true";
        const std::string paren = std::string(expression) + " = (true)";
        for (std::size_t i = start; i < end_exclusive && i < lines.size(); ++i) {
            const std::string_view trimmed = trim_line(lines[i]);
            if (trimmed == exact || trimmed == paren) {
                return true;
            }
        }
        return false;
    };

    SyntaxRepairCandidateTracker candidate_tracker(validation_error,
                                                   had_trailing_newline,
                                                   &decompiled_source,
                                                   note);

    for (std::size_t flattened_elseif_line = 0; flattened_elseif_line < lines.size();
         ++flattened_elseif_line) {
        std::string flattened_condition;
        if (!parse_elseif_condition(lines[flattened_elseif_line], &flattened_condition)) {
            continue;
        }
        if (reported_line != 0) {
            const std::size_t current_line = flattened_elseif_line + 1;
            if (current_line + 128 < reported_line || current_line > reported_line + 16) {
                continue;
            }
        }

        const std::vector<std::string> condition_parts = split_on_and(flattened_condition);
        if (condition_parts.size() < 4) {
            continue;
        }

        const std::string& state_condition = condition_parts.front();
        if (state_condition.find("State") == std::string::npos) {
            continue;
        }

        const std::string flat_indent(line_indent(lines[flattened_elseif_line]));
        std::string next_condition;
        std::size_t next_sibling_elseif_line = lines.size();
        for (std::size_t probe = flattened_elseif_line + 1;
             probe < lines.size() && probe <= flattened_elseif_line + 96;
             ++probe) {
            if (line_indent(lines[probe]) != flat_indent) {
                continue;
            }
            if (parse_elseif_condition(lines[probe], &next_condition)) {
                next_sibling_elseif_line = probe;
                break;
            }
        }
        if (next_sibling_elseif_line >= lines.size()) {
            continue;
        }

        std::size_t dangling_else_line = lines.size();
        for (std::size_t probe = flattened_elseif_line + 1; probe < next_sibling_elseif_line;
             ++probe) {
            if (trim_line(lines[probe]) == "else" && line_indent(lines[probe]) == flat_indent) {
                dangling_else_line = probe;
                break;
            }
        }
        if (dangling_else_line >= lines.size()) {
            continue;
        }

        std::size_t body_end_exclusive = dangling_else_line;
        while (body_end_exclusive > flattened_elseif_line + 1) {
            const std::size_t trailing = previous_nonempty(body_end_exclusive);
            if (trailing >= lines.size() || trailing <= flattened_elseif_line ||
                trim_line(lines[trailing]) != "end" ||
                line_indent(lines[trailing]) != flat_indent) {
                break;
            }
            body_end_exclusive = trailing;
        }
        const std::size_t body_start = next_nonempty(flattened_elseif_line + 1);
        if (body_start >= body_end_exclusive) {
            continue;
        }

        const std::size_t else_body_start = next_nonempty(dangling_else_line + 1);
        if (else_body_start >= next_sibling_elseif_line) {
            continue;
        }
        std::size_t else_body_end_exclusive = next_sibling_elseif_line;
        while (else_body_end_exclusive > else_body_start) {
            const std::size_t trailing = previous_nonempty(else_body_end_exclusive);
            if (trailing >= lines.size() || trailing < else_body_start ||
                trim_line(lines[trailing]) != "end" ||
                line_indent(lines[trailing]) != flat_indent) {
                break;
            }
            else_body_end_exclusive = trailing;
        }
        if (else_body_start >= else_body_end_exclusive) {
            continue;
        }

        std::size_t flag_index = condition_parts.size();
        std::string flag_expression;
        for (std::size_t i = 2; i < condition_parts.size(); ++i) {
            const std::string_view piece =
                refii::lua::TrimLuaWhitespace(condition_parts[i]);
            if (!StartsWith(piece, "not ")) {
                continue;
            }
            const std::string expression =
                std::string(refii::lua::TrimLuaWhitespace(piece.substr(4)));
            if (expression.empty() ||
                !is_true_assignment(body_start, body_end_exclusive, expression)) {
                continue;
            }
            flag_index = i;
            flag_expression = expression;
            break;
        }
        if (flag_index >= condition_parts.size() || flag_index < 2 ||
            StartsWith(refii::lua::TrimLuaWhitespace(condition_parts[1]), "not ")) {
            continue;
        }

        const std::size_t body_first = next_nonempty(body_start);
        const std::size_t else_body_first = next_nonempty(else_body_start);
        if (body_first >= body_end_exclusive || else_body_first >= else_body_end_exclusive) {
            continue;
        }

        const std::string body_base(line_indent(lines[body_first]));
        const std::string else_body_base(line_indent(lines[else_body_first]));
        if (body_base.empty() || else_body_base.empty()) {
            continue;
        }

        std::vector<std::string> candidate_lines;
        candidate_lines.reserve(lines.size() + 12);
        candidate_lines.insert(candidate_lines.end(),
                               lines.begin(),
                               lines.begin() +
                                   static_cast<std::ptrdiff_t>(flattened_elseif_line));

        candidate_lines.push_back(flat_indent + "elseif " + state_condition + " then");

        std::string current_indent = flat_indent + "  ";
        candidate_lines.push_back(current_indent + "if " + condition_parts[1] + " then");

        current_indent += "  ";
        const std::string flag_indent = current_indent;
        candidate_lines.push_back(flag_indent + "if not " + flag_expression + " then");

        std::vector<std::string> suffix_indents;
        std::string body_target_indent = flag_indent + "  ";
        for (std::size_t i = flag_index + 1; i < condition_parts.size(); ++i) {
            const std::string guard_indent = body_target_indent;
            candidate_lines.push_back(guard_indent + "if " + condition_parts[i] + " then");
            suffix_indents.push_back(guard_indent);
            body_target_indent += "  ";
        }

        append_reindented_range(body_start,
                                body_end_exclusive,
                                body_base,
                                body_target_indent,
                                &candidate_lines);

        for (auto it = suffix_indents.rbegin(); it != suffix_indents.rend(); ++it) {
            candidate_lines.push_back(*it + "end");
        }

        candidate_lines.push_back(flag_indent + "else");
        append_reindented_range(else_body_start,
                                else_body_end_exclusive,
                                else_body_base,
                                flag_indent + "  ",
                                &candidate_lines);
        candidate_lines.push_back(flag_indent + "end");

        candidate_lines.insert(candidate_lines.end(),
                               lines.begin() +
                                   static_cast<std::ptrdiff_t>(next_sibling_elseif_line),
                               lines.end());

        if (candidate_tracker.Consider(
                std::move(candidate_lines),
                "syntax_repaired_rebuilt_flattened_state_guard_branch")) {
            return true;
        }
    }

    return candidate_tracker.CommitBestProgress(
        "syntax_repaired_rebuilt_flattened_state_guard_branch_progress");
}

bool TryRebuildFlattenedInteractionGuardElseIfBranch(std::string& decompiled_source,
                                                     std::string_view validation_error,
                                                     std::string* note) {
    if (validation_error.find("'end' expected") == std::string_view::npos &&
        validation_error.find("'<eof>' expected") == std::string_view::npos &&
        validation_error.find("near 'end'") == std::string_view::npos &&
        validation_error.find("near 'else'") == std::string_view::npos &&
        validation_error.find("near 'elseif'") == std::string_view::npos) {
        return false;
    }

    bool had_trailing_newline = false;
    const std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);
    if (lines.size() < 14) {
        return false;
    }

    const std::size_t reported_line = ExtractLuaErrorLine(validation_error);
    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimWhitespace(refii::lua::StripLuaLineComment(line));
    };
    const auto line_indent = [](std::string_view line) -> std::string_view {
        const std::size_t first_non_ws = line.find_first_not_of(" \t");
        if (first_non_ws == std::string_view::npos) {
            return line;
        }
        return line.substr(0, first_non_ws);
    };
    const auto next_nonempty = [&](std::size_t start) -> std::size_t {
        for (std::size_t i = start; i < lines.size(); ++i) {
            if (!trim_line(lines[i]).empty()) {
                return i;
            }
        }
        return lines.size();
    };
    const auto previous_nonempty = [&](std::size_t start) -> std::size_t {
        if (start == 0 || start > lines.size()) {
            return lines.size();
        }
        for (std::size_t i = start; i-- > 0;) {
            if (!trim_line(lines[i]).empty()) {
                return i;
            }
            if (i == 0) {
                break;
            }
        }
        return lines.size();
    };
    const auto parse_if_condition = [](std::string_view line, std::string* condition) -> bool {
        if (condition == nullptr) {
            return false;
        }

        const std::string_view trimmed = refii::lua::TrimLuaWhitespace(
            refii::lua::StripLuaLineComment(line));
        if ((!StartsWith(trimmed, "if ") && !StartsWith(trimmed, "elseif ")) ||
            !EndsWith(trimmed, " then")) {
            return false;
        }

        const std::size_t prefix = StartsWith(trimmed, "elseif ") ? 7 : 3;
        const std::string_view extracted =
            refii::lua::TrimLuaWhitespace(trimmed.substr(prefix, trimmed.size() - (prefix + 5)));
        if (extracted.empty()) {
            return false;
        }

        *condition = std::string(extracted);
        return true;
    };
    const auto split_on_and = [](std::string_view condition) {
        std::vector<std::string> parts;
        std::size_t start = 0;
        while (start < condition.size()) {
            const std::size_t and_pos = condition.find(" and ", start);
            const std::size_t end =
                and_pos == std::string_view::npos ? condition.size() : and_pos;
            const std::string_view piece =
                refii::lua::TrimLuaWhitespace(condition.substr(start, end - start));
            if (!piece.empty()) {
                parts.emplace_back(piece);
            }
            if (and_pos == std::string_view::npos) {
                break;
            }
            start = and_pos + 5;
        }
        return parts;
    };
    const auto append_reindented_range =
        [&](std::size_t start,
            std::size_t end_exclusive,
            std::string_view source_base,
            std::string_view target_base,
            std::vector<std::string>* out_lines) {
            if (out_lines == nullptr || start >= end_exclusive || end_exclusive > lines.size()) {
                return;
            }

            for (std::size_t i = start; i < end_exclusive; ++i) {
                const std::string_view trimmed = trim_line(lines[i]);
                if (trimmed.empty()) {
                    out_lines->push_back(std::string(lines[i]));
                    continue;
                }

                const std::string_view current_indent = line_indent(lines[i]);
                if (current_indent.starts_with(source_base)) {
                    out_lines->push_back(std::string(target_base) +
                                         std::string(current_indent.substr(source_base.size())) +
                                         std::string(lines[i].substr(current_indent.size())));
                } else {
                    out_lines->push_back(std::string(target_base) + std::string(trimmed));
                }
            }
        };
    SyntaxRepairCandidateTracker candidate_tracker(validation_error,
                                                   had_trailing_newline,
                                                   &decompiled_source,
                                                   note);

    for (std::size_t flattened_if_line = 0; flattened_if_line < lines.size(); ++flattened_if_line) {
        if (reported_line != 0) {
            const std::size_t current_line = flattened_if_line + 1;
            if (current_line + 128 < reported_line || current_line > reported_line + 24) {
                continue;
            }
        }

        std::string flattened_condition;
        if (!parse_if_condition(lines[flattened_if_line], &flattened_condition) ||
            !StartsWith(trim_line(lines[flattened_if_line]), "if ")) {
            continue;
        }

        const std::vector<std::string> condition_parts = split_on_and(flattened_condition);
        if (condition_parts.size() != 2 ||
            !StartsWith(refii::lua::TrimLuaWhitespace(condition_parts[1]), "not ")) {
            continue;
        }

        const std::string primary_condition = condition_parts[0];
        const std::string flag_expression = std::string(refii::lua::TrimLuaWhitespace(
            std::string_view(condition_parts[1]).substr(4)));
        if (primary_condition.find("Trigger.IsTriggerEntityInsideTriggerVolume(") ==
                std::string::npos &&
            primary_condition.find("Trigger.IsSpecificTriggerEntityInsideTriggerVolume(") ==
                std::string::npos) {
            continue;
        }
        if (flag_expression.empty()) {
            continue;
        }

        const std::string flat_indent(line_indent(lines[flattened_if_line]));
        std::size_t sibling_elseif_line = lines.size();
        std::string sibling_condition;
        for (std::size_t probe = flattened_if_line + 1;
             probe < lines.size() && probe <= flattened_if_line + 16;
             ++probe) {
            if (line_indent(lines[probe]) != flat_indent) {
                continue;
            }
            if (!parse_if_condition(lines[probe], &sibling_condition) ||
                !StartsWith(trim_line(lines[probe]), "elseif ")) {
                continue;
            }
            const std::string expected =
                "not " + primary_condition + " and " + flag_expression;
            if (sibling_condition != expected) {
                continue;
            }
            sibling_elseif_line = probe;
            break;
        }
        if (sibling_elseif_line >= lines.size()) {
            continue;
        }

        const std::size_t body_start = next_nonempty(flattened_if_line + 1);
        if (body_start >= sibling_elseif_line) {
            continue;
        }

        std::size_t sibling_end_line = lines.size();
        for (std::size_t probe = sibling_elseif_line + 1;
             probe < lines.size() && probe <= sibling_elseif_line + 12;
             ++probe) {
            if (trim_line(lines[probe]) == "end" && line_indent(lines[probe]) == flat_indent) {
                sibling_end_line = probe;
                break;
            }
        }
        if (sibling_end_line >= lines.size()) {
            continue;
        }

        std::size_t extra_end_line = next_nonempty(sibling_end_line + 1);
        if (extra_end_line >= lines.size() || trim_line(lines[extra_end_line]) != "end" ||
            line_indent(lines[extra_end_line]) != flat_indent) {
            continue;
        }

        const std::size_t interaction_if_line = next_nonempty(extra_end_line + 1);
        std::string interaction_if_condition;
        std::string interaction_elseif_condition;
        if (interaction_if_line >= lines.size() ||
            !parse_if_condition(lines[interaction_if_line], &interaction_if_condition) ||
            interaction_if_condition.find("Player.GetLookAtButtonDown(") == std::string::npos) {
            continue;
        }

        std::size_t interaction_elseif_line = lines.size();
        for (std::size_t probe = interaction_if_line + 1;
             probe < lines.size() && probe <= interaction_if_line + 16;
             ++probe) {
            if (line_indent(lines[probe]) != flat_indent) {
                continue;
            }
            if (!parse_if_condition(lines[probe], &interaction_elseif_condition) ||
                !StartsWith(trim_line(lines[probe]), "elseif ")) {
                continue;
            }
            if (interaction_elseif_condition.find("CheckForInteractiveCutsceneFinished(") ==
                std::string::npos) {
                continue;
            }
            interaction_elseif_line = probe;
            break;
        }
        if (interaction_elseif_line >= lines.size()) {
            continue;
        }

        std::size_t body_end_exclusive = sibling_elseif_line;
        while (body_end_exclusive > flattened_if_line + 1) {
            const std::size_t trailing = previous_nonempty(body_end_exclusive);
            if (trailing >= lines.size() || trailing <= flattened_if_line ||
                trim_line(lines[trailing]) != "end" ||
                line_indent(lines[trailing]) != flat_indent) {
                break;
            }
            body_end_exclusive = trailing;
        }
        if (body_start >= body_end_exclusive) {
            continue;
        }

        const std::size_t else_body_start = next_nonempty(sibling_elseif_line + 1);
        std::size_t else_body_end_exclusive = sibling_end_line;
        while (else_body_end_exclusive > else_body_start) {
            const std::size_t trailing = previous_nonempty(else_body_end_exclusive);
            if (trailing >= lines.size() || trailing < else_body_start ||
                trim_line(lines[trailing]) != "end" ||
                line_indent(lines[trailing]) != flat_indent) {
                break;
            }
            else_body_end_exclusive = trailing;
        }
        if (else_body_start >= else_body_end_exclusive) {
            continue;
        }

        const std::size_t body_first = next_nonempty(body_start);
        const std::size_t else_body_first = next_nonempty(else_body_start);
        if (body_first >= body_end_exclusive || else_body_first >= else_body_end_exclusive) {
            continue;
        }

        const std::string body_base(line_indent(lines[body_first]));
        const std::string else_body_base(line_indent(lines[else_body_first]));
        if (body_base.empty() || else_body_base.empty()) {
            continue;
        }

        std::vector<std::string> candidate_lines;
        candidate_lines.reserve(lines.size() + 8);
        candidate_lines.insert(candidate_lines.end(),
                               lines.begin(),
                               lines.begin() +
                                   static_cast<std::ptrdiff_t>(flattened_if_line));

        candidate_lines.push_back(flat_indent + "if " + primary_condition + " then");
        candidate_lines.push_back(flat_indent + "  if not " + flag_expression + " then");
        append_reindented_range(body_start,
                                body_end_exclusive,
                                body_base,
                                flat_indent + "    ",
                                &candidate_lines);
        candidate_lines.push_back(flat_indent + "  end");
        candidate_lines.push_back(flat_indent + "elseif not " + primary_condition + " and " +
                                  flag_expression + " then");
        append_reindented_range(else_body_start,
                                else_body_end_exclusive,
                                else_body_base,
                                flat_indent + "  ",
                                &candidate_lines);
        candidate_lines.push_back(flat_indent + "end");
        candidate_lines.insert(candidate_lines.end(),
                               lines.begin() +
                                   static_cast<std::ptrdiff_t>(extra_end_line + 1),
                               lines.end());

        if (candidate_tracker.Consider(
                std::move(candidate_lines),
                "syntax_repaired_rebuilt_flattened_interaction_guard_branch")) {
            return true;
        }
    }

    return candidate_tracker.CommitBestProgress(
        "syntax_repaired_rebuilt_flattened_interaction_guard_branch_progress");
}

bool TryPromoteOuterStateElseIfAfterInteractiveCutscene(std::string& decompiled_source,
                                                        std::string_view validation_error,
                                                        std::string* note) {
    if (validation_error.find("'end' expected") == std::string_view::npos &&
        validation_error.find("'<eof>' expected") == std::string_view::npos &&
        validation_error.find("near 'end'") == std::string_view::npos &&
        validation_error.find("near 'else'") == std::string_view::npos &&
        validation_error.find("near 'elseif'") == std::string_view::npos) {
        return false;
    }

    bool had_trailing_newline = false;
    const std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);
    if (lines.size() < 12) {
        return false;
    }

    const std::size_t reported_line = ExtractLuaErrorLine(validation_error);
    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimWhitespace(refii::lua::StripLuaLineComment(line));
    };
    const auto line_indent = [](std::string_view line) -> std::string_view {
        const std::size_t first_non_ws = line.find_first_not_of(" \t");
        if (first_non_ws == std::string_view::npos) {
            return line;
        }
        return line.substr(0, first_non_ws);
    };
    const auto parse_if_condition = [](std::string_view line, std::string* condition) -> bool {
        if (condition == nullptr) {
            return false;
        }

        const std::string_view trimmed = refii::lua::TrimLuaWhitespace(
            refii::lua::StripLuaLineComment(line));
        if ((!StartsWith(trimmed, "if ") && !StartsWith(trimmed, "elseif ")) ||
            !EndsWith(trimmed, " then")) {
            return false;
        }

        const std::size_t prefix = StartsWith(trimmed, "elseif ") ? 7 : 3;
        const std::string_view extracted =
            refii::lua::TrimLuaWhitespace(trimmed.substr(prefix, trimmed.size() - (prefix + 5)));
        if (extracted.empty()) {
            return false;
        }

        *condition = std::string(extracted);
        return true;
    };
    const auto extract_branch_lhs = [](std::string_view condition, std::string* lhs) -> bool {
        if (lhs == nullptr) {
            return false;
        }

        const std::size_t equals = condition.find("==");
        if (equals == std::string_view::npos) {
            return false;
        }

        const std::string_view extracted =
            refii::lua::TrimLuaWhitespace(condition.substr(0, equals));
        if (extracted.empty()) {
            return false;
        }

        *lhs = std::string(extracted);
        return true;
    };

    SyntaxRepairCandidateTracker candidate_tracker(validation_error,
                                                   had_trailing_newline,
                                                   &decompiled_source,
                                                   note);

    for (std::size_t interaction_if_line = 0; interaction_if_line < lines.size();
         ++interaction_if_line) {
        if (reported_line != 0) {
            const std::size_t current_line = interaction_if_line + 1;
            if (current_line + 128 < reported_line || current_line > reported_line + 24) {
                continue;
            }
        }

        std::string interaction_if_condition;
        if (!parse_if_condition(lines[interaction_if_line], &interaction_if_condition) ||
            !StartsWith(trim_line(lines[interaction_if_line]), "if ") ||
            interaction_if_condition.find("Player.GetLookAtButtonDown(") == std::string::npos) {
            continue;
        }

        const std::string interaction_indent(line_indent(lines[interaction_if_line]));
        if (interaction_indent.empty()) {
            continue;
        }

        std::size_t interaction_elseif_line = lines.size();
        std::string interaction_elseif_condition;
        for (std::size_t probe = interaction_if_line + 1;
             probe < lines.size() && probe <= interaction_if_line + 20;
             ++probe) {
            if (line_indent(lines[probe]) != interaction_indent) {
                continue;
            }
            if (!parse_if_condition(lines[probe], &interaction_elseif_condition) ||
                !StartsWith(trim_line(lines[probe]), "elseif ")) {
                continue;
            }
            if (interaction_elseif_condition.find("CheckForInteractiveCutsceneFinished(") ==
                std::string::npos) {
                continue;
            }
            interaction_elseif_line = probe;
            break;
        }
        if (interaction_elseif_line >= lines.size()) {
            continue;
        }

        std::size_t swallowed_state_line = lines.size();
        std::string swallowed_state_condition;
        std::string swallowed_state_lhs;
        std::string swallowed_indent;
        for (std::size_t probe = interaction_elseif_line + 1;
             probe < lines.size() && probe <= interaction_elseif_line + 12;
             ++probe) {
            if (line_indent(lines[probe]).size() <= interaction_indent.size()) {
                continue;
            }
            if (!parse_if_condition(lines[probe], &swallowed_state_condition) ||
                !StartsWith(trim_line(lines[probe]), "elseif ") ||
                !extract_branch_lhs(swallowed_state_condition, &swallowed_state_lhs) ||
                swallowed_state_lhs.find("CurrentState") == std::string::npos) {
                continue;
            }
            swallowed_state_line = probe;
            swallowed_indent = std::string(line_indent(lines[probe]));
            break;
        }
        if (swallowed_state_line >= lines.size() || swallowed_indent.empty()) {
            continue;
        }

        std::size_t outer_state_line = lines.size();
        std::string outer_state_condition;
        for (std::size_t probe = interaction_if_line; probe-- > 0;) {
            if (line_indent(lines[probe]).size() >= interaction_indent.size()) {
                if (probe == 0 || probe + 64 < interaction_if_line) {
                    break;
                }
                continue;
            }

            std::string probe_condition;
            std::string probe_lhs;
            if (parse_if_condition(lines[probe], &probe_condition) &&
                extract_branch_lhs(probe_condition, &probe_lhs) &&
                probe_lhs == swallowed_state_lhs) {
                outer_state_line = probe;
                outer_state_condition = std::move(probe_condition);
                break;
            }

            if (probe == 0 || probe + 64 < interaction_if_line) {
                break;
            }
        }
        if (outer_state_line >= lines.size()) {
            continue;
        }

        const std::string target_indent(line_indent(lines[outer_state_line]));
        if (target_indent.empty() || target_indent.size() >= swallowed_indent.size()) {
            continue;
        }

        std::size_t swallowed_state_end_line = lines.size();
        for (std::size_t probe = swallowed_state_line + 1;
             probe < lines.size() && probe <= swallowed_state_line + 16;
             ++probe) {
            if (trim_line(lines[probe]) == "end" &&
                line_indent(lines[probe]) == swallowed_indent) {
                swallowed_state_end_line = probe;
                break;
            }
        }
        if (swallowed_state_end_line >= lines.size()) {
            continue;
        }

        std::vector<std::string> candidate_lines = lines;
        candidate_lines.insert(candidate_lines.begin() +
                                   static_cast<std::ptrdiff_t>(swallowed_state_line),
                               interaction_indent + "end");

        const std::size_t adjusted_state_begin = swallowed_state_line + 1;
        const std::size_t adjusted_state_end = swallowed_state_end_line + 1;
        for (std::size_t i = adjusted_state_begin;
             i <= adjusted_state_end && i < candidate_lines.size();
             ++i) {
            const std::string_view trimmed = trim_line(candidate_lines[i]);
            if (trimmed.empty()) {
                continue;
            }

            const std::string_view current_indent = line_indent(candidate_lines[i]);
            if (!StartsWith(current_indent, swallowed_indent)) {
                continue;
            }

            candidate_lines[i] = target_indent +
                                 std::string(current_indent.substr(swallowed_indent.size())) +
                                 std::string(candidate_lines[i].substr(current_indent.size()));
        }

        if (candidate_tracker.Consider(
                std::move(candidate_lines),
                "syntax_repaired_promoted_outer_state_elseif_after_interactive_cutscene")) {
            return true;
        }
    }

    return candidate_tracker.CommitBestProgress(
        "syntax_repaired_promoted_outer_state_elseif_after_interactive_cutscene_progress");
}

bool TryInsertMissingEndBeforeOuterElseIf(std::string& decompiled_source,
                                          std::string_view validation_error,
                                          std::string* note) {
    if (validation_error.find("'end' expected") == std::string_view::npos ||
        validation_error.find("near 'elseif'") == std::string_view::npos) {
        return false;
    }

    bool had_trailing_newline = false;
    const std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);
    if (lines.size() < 8) {
        return false;
    }

    const std::size_t reported_line = ExtractLuaErrorLine(validation_error);
    if (reported_line == 0 || reported_line > lines.size()) {
        return false;
    }

    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimWhitespace(refii::lua::StripLuaLineComment(line));
    };
    const auto line_indent = [](std::string_view line) -> std::string_view {
        const std::size_t first_non_ws = line.find_first_not_of(" \t");
        if (first_non_ws == std::string_view::npos) {
            return line;
        }
        return line.substr(0, first_non_ws);
    };
    const auto next_nonempty = [&](std::size_t start) -> std::size_t {
        for (std::size_t i = start; i < lines.size(); ++i) {
            if (!trim_line(lines[i]).empty()) {
                return i;
            }
        }
        return lines.size();
    };
    const auto parse_if_condition = [](std::string_view line, std::string* condition) -> bool {
        if (condition == nullptr) {
            return false;
        }

        const std::string_view trimmed = refii::lua::TrimLuaWhitespace(
            refii::lua::StripLuaLineComment(line));
        if ((!StartsWith(trimmed, "if ") && !StartsWith(trimmed, "elseif ")) ||
            !EndsWith(trimmed, " then")) {
            return false;
        }

        const std::size_t prefix = StartsWith(trimmed, "elseif ") ? 7 : 3;
        const std::string_view extracted =
            refii::lua::TrimLuaWhitespace(trimmed.substr(prefix, trimmed.size() - (prefix + 5)));
        if (extracted.empty()) {
            return false;
        }

        *condition = std::string(extracted);
        return true;
    };
    const auto extract_branch_lhs = [](std::string_view condition, std::string* lhs) -> bool {
        if (lhs == nullptr) {
            return false;
        }
        const std::size_t equals = condition.find("==");
        if (equals == std::string_view::npos) {
            return false;
        }

        const std::string_view extracted =
            refii::lua::TrimLuaWhitespace(condition.substr(0, equals));
        if (extracted.empty()) {
            return false;
        }

        *lhs = std::string(extracted);
        return true;
    };

    const std::size_t elseif_line = reported_line - 1;
    std::string reported_condition;
    std::string reported_lhs;
    if (!parse_if_condition(lines[elseif_line], &reported_condition) ||
        !extract_branch_lhs(reported_condition, &reported_lhs)) {
        return false;
    }
    if (reported_lhs.find("State") != std::string::npos ||
        reported_lhs.find("ThreadVar") != std::string::npos) {
        return false;
    }

    const std::string outer_indent(line_indent(lines[elseif_line]));
    if (outer_indent.empty()) {
        return false;
    }
    if (outer_indent.size() < 2) {
        return false;
    }
    const std::string promoted_indent = outer_indent.substr(0, outer_indent.size() - 2);

    std::size_t chain_end_line = lines.size();
    for (std::size_t probe = elseif_line + 1;
         probe < lines.size() && probe <= elseif_line + 24;
         ++probe) {
        if (trim_line(lines[probe]) == "end" &&
            line_indent(lines[probe]) == outer_indent) {
            chain_end_line = probe;
            break;
        }
    }
    if (chain_end_line >= lines.size()) {
        return false;
    }

    const std::size_t outer_end_line = next_nonempty(chain_end_line + 1);
    if (outer_end_line >= lines.size() ||
        trim_line(lines[outer_end_line]) != "end" ||
        line_indent(lines[outer_end_line]) != promoted_indent) {
        return false;
    }

    std::size_t outer_branch_line = lines.size();
    for (std::size_t probe = elseif_line; probe-- > 0;) {
        const std::string_view probe_trimmed = trim_line(lines[probe]);
        if (probe_trimmed.empty()) {
            if (probe == 0) {
                break;
            }
            continue;
        }
        const std::string_view probe_indent = line_indent(lines[probe]);
        if (probe_indent.size() < promoted_indent.size()) {
            break;
        }
        if (probe_indent == promoted_indent) {
            std::string probe_condition;
            std::string probe_lhs;
            if (StartsWith(trim_line(lines[probe]), "if ") &&
                parse_if_condition(lines[probe], &probe_condition) &&
                extract_branch_lhs(probe_condition, &probe_lhs) &&
                probe_lhs == reported_lhs) {
                outer_branch_line = probe;
                break;
            }
        }
        if (probe == 0 || probe + 64 < elseif_line) {
            break;
        }
    }
    if (outer_branch_line >= lines.size()) {
        return false;
    }

    std::size_t inner_else_line = lines.size();
    for (std::size_t probe = elseif_line; probe-- > outer_branch_line + 1;) {
        if (trim_line(lines[probe]) == "else" && line_indent(lines[probe]) == outer_indent) {
            inner_else_line = probe;
            break;
        }
    }
    if (inner_else_line >= lines.size()) {
        return false;
    }

    std::size_t inner_if_line = lines.size();
    for (std::size_t probe = inner_else_line; probe-- > outer_branch_line;) {
        std::string ignored_condition;
        if (line_indent(lines[probe]) == outer_indent &&
            parse_if_condition(lines[probe], &ignored_condition)) {
            inner_if_line = probe;
            break;
        }
    }
    if (inner_if_line >= lines.size()) {
        return false;
    }

    const std::size_t inner_then_body = next_nonempty(inner_if_line + 1);
    const std::size_t inner_else_body = next_nonempty(inner_else_line + 1);
    if (inner_then_body >= lines.size() || inner_then_body >= inner_else_line ||
        inner_else_body >= lines.size() || inner_else_body >= elseif_line) {
        return false;
    }

    std::vector<std::string> candidate_lines = lines;
    candidate_lines.insert(candidate_lines.begin() + static_cast<std::ptrdiff_t>(elseif_line),
                           outer_indent + "end");

    const std::size_t adjusted_chain_begin = elseif_line + 1;
    const std::size_t adjusted_chain_end = chain_end_line + 1;
    for (std::size_t i = adjusted_chain_begin;
         i <= adjusted_chain_end && i < candidate_lines.size();
         ++i) {
        const std::string_view trimmed = trim_line(candidate_lines[i]);
        if (trimmed.empty()) {
            continue;
        }

        const std::string_view current_indent = line_indent(candidate_lines[i]);
        if (!StartsWith(current_indent, outer_indent)) {
            continue;
        }

        candidate_lines[i] = promoted_indent +
                             std::string(current_indent.substr(outer_indent.size())) +
                             std::string(candidate_lines[i].substr(current_indent.size()));
    }

    std::string candidate = JoinLines(candidate_lines, had_trailing_newline);
    const std::string candidate_error = ValidateLuaRepairSource(candidate);
    if (candidate_error.empty()) {
        decompiled_source = std::move(candidate);
        if (note != nullptr) {
            *note = JoinNotes(
                *note,
                "syntax_repaired_inserted_missing_end_before_outer_elseif");
        }
        return true;
    }

    if (!IsValidationOutcomeBetter(validation_error, candidate_error)) {
        return false;
    }

    decompiled_source = std::move(candidate);
    if (note != nullptr) {
        *note = JoinNotes(*note,
                          "syntax_repaired_inserted_missing_end_before_outer_elseif_progress");
    }
    return true;
}

bool TryPromoteWeekGuardIfToLoopAndCollapseNestedInteractionWhile(
    std::string& decompiled_source,
    std::string_view validation_error,
    std::string* note) {
    if (validation_error.find("near 'elseif'") == std::string_view::npos) {
        return false;
    }

    bool had_trailing_newline = false;
    const std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);
    if (lines.size() < 12) {
        return false;
    }

    const std::size_t reported_line = ExtractLuaErrorLine(validation_error);
    if (reported_line == 0 || reported_line > lines.size()) {
        return false;
    }

    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimWhitespace(refii::lua::StripLuaLineComment(line));
    };
    const auto line_indent = [](std::string_view line) -> std::string_view {
        const std::size_t first_non_ws = line.find_first_not_of(" \t");
        if (first_non_ws == std::string_view::npos) {
            return line;
        }
        return line.substr(0, first_non_ws);
    };
    const auto next_nonempty = [&](std::size_t start) -> std::size_t {
        for (std::size_t i = start; i < lines.size(); ++i) {
            if (!trim_line(lines[i]).empty()) {
                return i;
            }
        }
        return lines.size();
    };
    const auto previous_nonempty = [&](std::size_t start) -> std::size_t {
        if (start == 0 || start > lines.size()) {
            return lines.size();
        }
        for (std::size_t i = start; i-- > 0;) {
            if (!trim_line(lines[i]).empty()) {
                return i;
            }
            if (i == 0) {
                break;
            }
        }
        return lines.size();
    };
    const auto parse_if_condition = [](std::string_view line, std::string* condition) -> bool {
        if (condition == nullptr) {
            return false;
        }

        const std::string_view trimmed = refii::lua::TrimLuaWhitespace(
            refii::lua::StripLuaLineComment(line));
        if ((!StartsWith(trimmed, "if ") && !StartsWith(trimmed, "elseif ")) ||
            !EndsWith(trimmed, " then")) {
            return false;
        }

        const std::size_t prefix = StartsWith(trimmed, "elseif ") ? 7 : 3;
        const std::string_view extracted =
            refii::lua::TrimLuaWhitespace(trimmed.substr(prefix, trimmed.size() - (prefix + 5)));
        if (extracted.empty()) {
            return false;
        }

        *condition = std::string(extracted);
        return true;
    };
    const auto parse_while_condition = [](std::string_view line, std::string* condition) -> bool {
        if (condition == nullptr) {
            return false;
        }

        const std::string_view trimmed = refii::lua::TrimLuaWhitespace(
            refii::lua::StripLuaLineComment(line));
        if (!StartsWith(trimmed, "while ") || !EndsWith(trimmed, " do")) {
            return false;
        }

        const std::string_view extracted =
            refii::lua::TrimLuaWhitespace(trimmed.substr(6, trimmed.size() - 9));
        if (extracted.empty()) {
            return false;
        }

        *condition = std::string(extracted);
        return true;
    };

    const std::size_t elseif_line = reported_line - 1;
    std::string elseif_condition;
    if (!parse_if_condition(lines[elseif_line], &elseif_condition) ||
        elseif_condition.find("CheckForInteractiveCutsceneFinished(") == std::string::npos) {
        return false;
    }

    const std::string branch_indent(line_indent(lines[elseif_line]));
    std::string interaction_condition;
    std::size_t interaction_while_line = lines.size();
    if (branch_indent.size() < 2) {
        return false;
    }
    const std::string loop_indent = branch_indent.substr(0, branch_indent.size() - 2);
    for (std::size_t probe = elseif_line; probe-- > 0;) {
        if (elseif_line - probe > 24) {
            break;
        }
        if (line_indent(lines[probe]) != branch_indent) {
            continue;
        }
        if (parse_while_condition(lines[probe], &interaction_condition) &&
            interaction_condition.find("Player.GetLookAtButtonDown(") != std::string::npos) {
            interaction_while_line = probe;
            break;
        }
    }
    if (interaction_while_line >= lines.size()) {
        return false;
    }

    std::string week_condition;
    std::size_t week_if_line = lines.size();
    for (std::size_t probe = interaction_while_line; probe-- > 0;) {
        if (interaction_while_line - probe > 24) {
            break;
        }
        if (line_indent(lines[probe]) != loop_indent) {
            continue;
        }
        if (parse_if_condition(lines[probe], &week_condition) &&
            week_condition.find("CurrentWeek ==") != std::string::npos) {
            week_if_line = probe;
            break;
        }
    }
    if (week_if_line >= lines.size()) {
        return false;
    }

    const std::size_t stray_end_line = previous_nonempty(interaction_while_line);
    if (stray_end_line >= lines.size() ||
        trim_line(lines[stray_end_line]) != "end" ||
        line_indent(lines[stray_end_line]) != branch_indent) {
        return false;
    }

    std::size_t elseif_end_line = lines.size();
    for (std::size_t probe = elseif_line + 1;
         probe < lines.size() && probe <= elseif_line + 12;
         ++probe) {
        if (trim_line(lines[probe]) == "end" &&
            line_indent(lines[probe]) == branch_indent) {
            elseif_end_line = probe;
            break;
        }
    }
    if (elseif_end_line >= lines.size()) {
        return false;
    }

    const std::size_t yield_line = next_nonempty(elseif_end_line + 1);
    const std::size_t loop_end_line = next_nonempty(yield_line + 1);
    if (yield_line >= lines.size() || trim_line(lines[yield_line]) != "coroutine.yield()" ||
        loop_end_line >= lines.size() || trim_line(lines[loop_end_line]) != "end" ||
        line_indent(lines[loop_end_line]) != branch_indent) {
        return false;
    }

    std::size_t promoted_tail_end = next_nonempty(loop_end_line + 1);
    if (promoted_tail_end == lines.size()) {
        promoted_tail_end = lines.size();
    }
    for (std::size_t probe = loop_end_line + 1; probe < lines.size(); ++probe) {
        const std::string_view trimmed = trim_line(lines[probe]);
        if (trimmed.empty()) {
            continue;
        }
        if (line_indent(lines[probe]).size() < branch_indent.size()) {
            promoted_tail_end = probe;
            break;
        }
    }

    for (std::size_t probe = elseif_line + 1; probe < elseif_end_line; ++probe) {
        if (trim_line(lines[probe]) == "break") {
            return false;
        }
    }

    std::vector<std::string> candidate_lines = lines;
    candidate_lines[week_if_line] = loop_indent + "while " + week_condition + " do";
    candidate_lines.erase(candidate_lines.begin() + static_cast<std::ptrdiff_t>(stray_end_line));

    std::size_t adjusted_interaction_while_line = interaction_while_line;
    std::size_t adjusted_elseif_end_line = elseif_end_line;
    std::size_t adjusted_loop_end_line = loop_end_line;
    std::size_t adjusted_promoted_tail_end = promoted_tail_end;
    if (stray_end_line < adjusted_interaction_while_line) {
        --adjusted_interaction_while_line;
    }
    if (stray_end_line < adjusted_elseif_end_line) {
        --adjusted_elseif_end_line;
    }
    if (stray_end_line < adjusted_loop_end_line) {
        --adjusted_loop_end_line;
    }
    if (stray_end_line < adjusted_promoted_tail_end) {
        --adjusted_promoted_tail_end;
    }

    candidate_lines[adjusted_interaction_while_line] =
        branch_indent + "if " + interaction_condition + " then";
    candidate_lines.insert(candidate_lines.begin() +
                               static_cast<std::ptrdiff_t>(adjusted_elseif_end_line),
                           branch_indent + "  break");

    ++adjusted_loop_end_line;
    ++adjusted_promoted_tail_end;
    for (std::size_t i = adjusted_loop_end_line;
         i < adjusted_promoted_tail_end && i < candidate_lines.size();
         ++i) {
        const std::string_view trimmed = trim_line(candidate_lines[i]);
        if (trimmed.empty()) {
            continue;
        }
        const std::string_view current_indent = line_indent(candidate_lines[i]);
        if (!StartsWith(current_indent, branch_indent)) {
            continue;
        }

        candidate_lines[i] = loop_indent +
                             std::string(current_indent.substr(branch_indent.size())) +
                             std::string(candidate_lines[i].substr(current_indent.size()));
    }

    std::string candidate = JoinLines(candidate_lines, had_trailing_newline);
    const std::string candidate_error = ValidateLuaRepairSource(candidate);
    if (candidate_error.empty()) {
        decompiled_source = std::move(candidate);
        if (note != nullptr) {
            *note = JoinNotes(
                *note,
                "syntax_repaired_promoted_week_guard_if_to_loop_and_collapsed_nested_interaction_while");
        }
        return true;
    }

    if (!IsValidationOutcomeBetter(validation_error, candidate_error)) {
        return false;
    }

    decompiled_source = std::move(candidate);
    if (note != nullptr) {
        *note = JoinNotes(
            *note,
            "syntax_repaired_promoted_week_guard_if_to_loop_and_collapsed_nested_interaction_while_progress");
    }
    return true;
}

bool TryRebuildWeekCutsceneStateLadder(std::string& decompiled_source,
                                       std::string_view validation_error,
                                       std::string* note) {
    if (validation_error.find("'end' expected") == std::string_view::npos &&
        validation_error.find("'<eof>' expected") == std::string_view::npos &&
        validation_error.find("near 'else'") == std::string_view::npos &&
        validation_error.find("near 'elseif'") == std::string_view::npos) {
        return false;
    }

    bool had_trailing_newline = false;
    const std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);
    if (lines.size() < 32) {
        return false;
    }

    const std::size_t reported_line = ExtractLuaErrorLine(validation_error);
    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimWhitespace(refii::lua::StripLuaLineComment(line));
    };
    const auto line_indent = [](std::string_view line) -> std::string_view {
        const std::size_t first_non_ws = line.find_first_not_of(" \t");
        if (first_non_ws == std::string_view::npos) {
            return line;
        }
        return line.substr(0, first_non_ws);
    };
    const auto next_nonempty = [&](std::size_t start) -> std::size_t {
        for (std::size_t i = start; i < lines.size(); ++i) {
            if (!trim_line(lines[i]).empty()) {
                return i;
            }
        }
        return lines.size();
    };
    const auto previous_nonempty = [&](std::size_t start) -> std::size_t {
        if (start == 0 || start > lines.size()) {
            return lines.size();
        }
        for (std::size_t i = start; i-- > 0;) {
            if (!trim_line(lines[i]).empty()) {
                return i;
            }
            if (i == 0) {
                break;
            }
        }
        return lines.size();
    };
    const auto parse_if_condition = [](std::string_view line, std::string* condition) -> bool {
        if (condition == nullptr) {
            return false;
        }

        const std::string_view trimmed = refii::lua::TrimLuaWhitespace(
            refii::lua::StripLuaLineComment(line));
        if ((!StartsWith(trimmed, "if ") && !StartsWith(trimmed, "elseif ")) ||
            !EndsWith(trimmed, " then")) {
            return false;
        }

        const std::size_t prefix = StartsWith(trimmed, "elseif ") ? 7 : 3;
        const std::string_view extracted =
            refii::lua::TrimLuaWhitespace(trimmed.substr(prefix, trimmed.size() - (prefix + 5)));
        if (extracted.empty()) {
            return false;
        }

        *condition = std::string(extracted);
        return true;
    };
    const auto parse_while_condition = [](std::string_view line, std::string* condition) -> bool {
        if (condition == nullptr) {
            return false;
        }

        const std::string_view trimmed = refii::lua::TrimLuaWhitespace(
            refii::lua::StripLuaLineComment(line));
        if (!StartsWith(trimmed, "while ") || !EndsWith(trimmed, " do")) {
            return false;
        }

        const std::string_view extracted =
            refii::lua::TrimLuaWhitespace(trimmed.substr(6, trimmed.size() - 9));
        if (extracted.empty()) {
            return false;
        }

        *condition = std::string(extracted);
        return true;
    };
    const auto find_matching_header = [&](std::size_t start,
                                          std::size_t stop,
                                          std::string_view prefix,
                                          std::string_view must_contain,
                                          std::string* condition) -> std::size_t {
        for (std::size_t probe = start; probe < stop && probe < lines.size(); ++probe) {
            std::string parsed_condition;
            if (!parse_if_condition(lines[probe], &parsed_condition)) {
                continue;
            }
            if (!prefix.empty() && !StartsWith(trim_line(lines[probe]), prefix)) {
                continue;
            }
            if (!must_contain.empty() &&
                parsed_condition.find(must_contain) == std::string::npos) {
                continue;
            }
            if (condition != nullptr) {
                *condition = std::move(parsed_condition);
            }
            return probe;
        }
        return lines.size();
    };
    const auto find_line_with_text = [&](std::size_t start,
                                         std::size_t stop,
                                         std::string_view text,
                                         std::string_view required_indent) -> std::size_t {
        for (std::size_t probe = start; probe < stop && probe < lines.size(); ++probe) {
            if (!required_indent.empty() &&
                line_indent(lines[probe]) != required_indent) {
                continue;
            }
            if (lines[probe].find(text) != std::string::npos) {
                return probe;
            }
        }
        return lines.size();
    };
    const auto find_end_at_indent = [&](std::size_t start,
                                        std::size_t stop,
                                        std::string_view indent) -> std::size_t {
        for (std::size_t probe = start; probe < stop && probe < lines.size(); ++probe) {
            if (trim_line(lines[probe]) == "end" &&
                line_indent(lines[probe]) == indent) {
                return probe;
            }
        }
        return lines.size();
    };
    const auto append_reindented_range =
        [&](std::size_t start,
            std::size_t end_exclusive,
            std::string_view source_base,
            std::string_view target_base,
            std::vector<std::string>* out_lines) {
            if (out_lines == nullptr || start >= end_exclusive || end_exclusive > lines.size()) {
                return;
            }

            for (std::size_t i = start; i < end_exclusive; ++i) {
                const std::string_view trimmed = trim_line(lines[i]);
                if (trimmed.empty()) {
                    out_lines->push_back(std::string(lines[i]));
                    continue;
                }

                const std::string_view current_indent = line_indent(lines[i]);
                if (current_indent.starts_with(source_base)) {
                    out_lines->push_back(std::string(target_base) +
                                         std::string(current_indent.substr(source_base.size())) +
                                         std::string(lines[i].substr(current_indent.size())));
                } else {
                    out_lines->push_back(std::string(target_base) + std::string(trimmed));
                }
            }
        };

    SyntaxRepairCandidateTracker candidate_tracker(validation_error,
                                                   had_trailing_newline,
                                                   &decompiled_source,
                                                   note);

    for (std::size_t week1_outer_while_line = 0; week1_outer_while_line < lines.size();
         ++week1_outer_while_line) {
        if (reported_line != 0) {
            const std::size_t current_line = week1_outer_while_line + 1;
            if (current_line + 192 < reported_line || current_line > reported_line + 24) {
                continue;
            }
        }

        std::string week1_condition;
        if (!parse_while_condition(lines[week1_outer_while_line], &week1_condition) ||
            week1_condition.find("ParentQuest.CurrentWeek == 1") == std::string::npos) {
            continue;
        }

        const std::string function_body_indent(line_indent(lines[week1_outer_while_line]));
        if (function_body_indent.empty()) {
            continue;
        }

        const std::string branch_indent = function_body_indent + "  ";
        const std::string loop_indent = function_body_indent + "    ";
        const std::string state_indent = function_body_indent + "      ";

        const std::size_t week1_inner_wrapper_while =
            find_line_with_text(week1_outer_while_line + 1,
                                week1_outer_while_line + 24,
                                "while true do",
                                branch_indent);
        if (week1_inner_wrapper_while >= lines.size()) {
            continue;
        }

        std::string week1_wrapper_condition;
        const std::size_t week1_wrapper_if =
            find_matching_header(week1_inner_wrapper_while + 1,
                                 week1_inner_wrapper_while + 8,
                                 "if ",
                                 week1_condition,
                                 &week1_wrapper_condition);
        if (week1_wrapper_if >= lines.size()) {
            continue;
        }

        const std::size_t week1_outer_end_line =
            find_end_at_indent(week1_outer_while_line + 1, lines.size(), function_body_indent);
        if (week1_outer_end_line >= lines.size()) {
            continue;
        }

        const std::size_t week3_if_line =
            find_matching_header(week1_outer_end_line + 1,
                                 week1_outer_end_line + 32,
                                 "if ",
                                 "ParentQuest.CurrentWeek == 3",
                                 nullptr);
        if (week3_if_line >= lines.size()) {
            continue;
        }

        const std::size_t week1_yield_line =
            find_line_with_text(week1_wrapper_if + 1, week3_if_line, "coroutine.yield()", "");
        if (week1_yield_line >= lines.size()) {
            continue;
        }

        const std::size_t week1_body_start = next_nonempty(week1_wrapper_if + 1);
        const std::size_t week1_body_end =
            previous_nonempty(week1_yield_line) + 1;
        if (week1_body_start >= week1_body_end) {
            continue;
        }

        std::string week3_condition;
        if (!parse_if_condition(lines[week3_if_line], &week3_condition)) {
            continue;
        }

        const std::string week3_outer_indent(line_indent(lines[week3_if_line]));
        const std::size_t week3_setup_end =
            find_line_with_text(week3_if_line + 1, week3_if_line + 24, "while true do", "");
        if (week3_setup_end >= lines.size()) {
            continue;
        }
        const std::size_t week3_wrapper_if =
            find_matching_header(week3_setup_end + 1,
                                 week3_setup_end + 8,
                                 "if ",
                                 week3_condition,
                                 nullptr);
        if (week3_wrapper_if >= lines.size()) {
            continue;
        }
        const std::size_t week3_else_line =
            find_line_with_text(week3_wrapper_if + 1, week3_wrapper_if + 96, "else", line_indent(lines[week3_wrapper_if]));
        if (week3_else_line >= lines.size()) {
            continue;
        }

        const std::size_t week137_if_line =
            find_matching_header(week3_else_line + 1,
                                 week3_else_line + 8,
                                 "if ",
                                 "ParentQuest.CurrentWeek == 137",
                                 nullptr);
        if (week137_if_line >= lines.size()) {
            continue;
        }

        const std::size_t tail_resume_line = [&]() -> std::size_t {
            for (std::size_t probe = week137_if_line + 1; probe < lines.size(); ++probe) {
                const std::string_view trimmed = trim_line(lines[probe]);
                if (trimmed.empty()) {
                    continue;
                }
                if (line_indent(lines[probe]).empty() && trimmed != "end") {
                    return probe;
                }
            }
            return lines.size();
        }();
        if (tail_resume_line >= lines.size()) {
            continue;
        }

        if (find_line_with_text(week1_outer_while_line, week3_if_line, "QC130_Wk1_GarthCell", "") ==
                lines.size() ||
            find_line_with_text(week3_if_line, week137_if_line, "QC130_Wk3_Garth", "") ==
                lines.size() ||
            find_line_with_text(week137_if_line, tail_resume_line, "QC130_WK38_GarthPassing", "") ==
                lines.size()) {
            continue;
        }

        std::string week3_state0_condition;
        const std::size_t week3_state0_line =
            find_matching_header(week3_wrapper_if + 1,
                                 week3_else_line,
                                 "if ",
                                 "CurrentState == 0",
                                 &week3_state0_condition);
        std::string week3_state1_condition;
        const std::size_t week3_state1_line =
            find_matching_header(week3_state0_line + 1,
                                 week3_else_line,
                                 "elseif ",
                                 "CurrentState == 1",
                                 &week3_state1_condition);
        std::string week3_state2_condition;
        const std::size_t week3_state2_line =
            find_matching_header(week3_state1_line + 1,
                                 week3_else_line,
                                 "elseif ",
                                 "CurrentState == 2",
                                 &week3_state2_condition);
        if (week3_state0_line >= lines.size() || week3_state1_line >= lines.size() ||
            week3_state2_line >= lines.size()) {
            continue;
        }

        const std::size_t week3_split = week3_state0_condition.find(" and ");
        if (week3_split == std::string::npos) {
            continue;
        }
        const std::string week3_outer_state_condition =
            week3_state0_condition.substr(0, week3_split);
        const std::string week3_inner_trigger_condition =
            week3_state0_condition.substr(week3_split + 5);

        const std::size_t week3_camera_if_line =
            find_matching_header(week3_state1_line + 1,
                                 week3_state2_line,
                                 "if ",
                                 "Trigger.IsTriggerEntityInsideTriggerVolume(",
                                 nullptr);
        const std::size_t week3_camera_else_line =
            find_matching_header(week3_camera_if_line + 1,
                                 week3_state2_line,
                                 "elseif ",
                                 "not Trigger.IsTriggerEntityInsideTriggerVolume(",
                                 nullptr);
        const std::size_t week3_interaction_if_line =
            find_matching_header(week3_camera_else_line + 1,
                                 week3_state2_line,
                                 "if ",
                                 "look_at and not garth_cutscene_played",
                                 nullptr);
        std::string week3_interaction_elseif_condition;
        const std::size_t week3_interaction_elseif_line =
            find_matching_header(week3_interaction_if_line + 1,
                                 week3_state2_line,
                                 "elseif ",
                                 "CheckForInteractiveCutsceneFinished(",
                                 &week3_interaction_elseif_condition);
        const std::size_t week3_yield_line =
            find_line_with_text(week3_state2_line + 1, week3_else_line, "coroutine.yield()", "");
        if (week3_camera_if_line >= lines.size() || week3_camera_else_line >= lines.size() ||
            week3_interaction_if_line >= lines.size() ||
            week3_interaction_elseif_line >= lines.size() ||
            week3_yield_line >= lines.size()) {
            continue;
        }
        const std::size_t week3_camera_if_body_end =
            LastNonemptyNonEndBefore(lines, week3_camera_else_line, week3_camera_if_line + 1);
        const std::size_t week3_camera_else_body_end =
            LastNonemptyNonEndBefore(lines, week3_interaction_if_line, week3_camera_else_line + 1);
        const std::size_t week3_interaction_if_body_end =
            LastNonemptyNonEndBefore(lines,
                                     week3_interaction_elseif_line,
                                     week3_interaction_if_line + 1);
        const std::size_t week3_interaction_else_body_end =
            LastNonemptyNonEndBefore(lines, week3_state2_line, week3_interaction_elseif_line + 1);
        const std::size_t week3_state2_body_end =
            LastNonemptyNonEndBefore(lines, week3_yield_line, week3_state2_line + 1);
        if (week3_camera_if_body_end >= lines.size() ||
            week3_camera_else_body_end >= lines.size() ||
            week3_interaction_if_body_end >= lines.size() ||
            week3_interaction_else_body_end >= lines.size() ||
            week3_state2_body_end >= lines.size()) {
            continue;
        }

        std::string week137_condition;
        if (!parse_if_condition(lines[week137_if_line], &week137_condition)) {
            continue;
        }

        const std::size_t week137_setup_end =
            find_line_with_text(week137_if_line + 1, week137_if_line + 24, "while true do", "");
        if (week137_setup_end >= lines.size()) {
            continue;
        }
        const std::size_t week137_wrapper_if =
            find_matching_header(week137_setup_end + 1,
                                 week137_setup_end + 8,
                                 "if ",
                                 week137_condition,
                                 nullptr);
        if (week137_wrapper_if >= lines.size()) {
            continue;
        }

        std::string week137_state0_condition;
        const std::size_t week137_state0_line =
            find_matching_header(week137_wrapper_if + 1,
                                 tail_resume_line,
                                 "if ",
                                 "CurrentState == 0",
                                 &week137_state0_condition);
        std::string week137_state1_condition;
        const std::size_t week137_state1_line =
            find_matching_header(week137_state0_line + 1,
                                 tail_resume_line,
                                 "elseif ",
                                 "CurrentState == 1",
                                 &week137_state1_condition);
        if (week137_state0_line >= lines.size() || week137_state1_line >= lines.size()) {
            continue;
        }

        const std::size_t week137_yield_line =
            find_line_with_text(week137_state1_line + 1,
                                tail_resume_line,
                                "coroutine.yield()",
                                "");
        const std::size_t week137_camera_if_line =
            find_matching_header(week137_state0_line + 1,
                                 week137_state1_line,
                                 "if ",
                                 "Trigger.IsTriggerEntityInsideTriggerVolume(",
                                 nullptr);
        const std::size_t week137_camera_else_line =
            find_matching_header(week137_camera_if_line + 1,
                                 week137_state1_line,
                                 "elseif ",
                                 "not Trigger.IsTriggerEntityInsideTriggerVolume(",
                                 nullptr);
        const std::size_t week137_interaction_if_line =
            find_matching_header(week137_camera_else_line + 1,
                                 week137_state1_line,
                                 "if ",
                                 "look_at and not garth_cutscene_played",
                                 nullptr);
        const std::size_t week137_interaction_elseif_line =
            find_matching_header(week137_interaction_if_line + 1,
                                 week137_state1_line,
                                 "elseif ",
                                 "CheckForInteractiveCutsceneFinished(",
                                 nullptr);
        if (week137_camera_if_line >= lines.size() || week137_camera_else_line >= lines.size() ||
            week137_interaction_if_line >= lines.size() ||
            week137_interaction_elseif_line >= lines.size() ||
            week137_yield_line >= lines.size()) {
            continue;
        }
        const std::size_t week137_camera_if_body_end =
            LastNonemptyNonEndBefore(lines, week137_camera_else_line, week137_camera_if_line + 1);
        const std::size_t week137_camera_else_body_end =
            LastNonemptyNonEndBefore(lines,
                                     week137_interaction_if_line,
                                     week137_camera_else_line + 1);
        const std::size_t week137_interaction_if_body_end =
            LastNonemptyNonEndBefore(lines,
                                     week137_interaction_elseif_line,
                                     week137_interaction_if_line + 1);
        const std::size_t week137_interaction_else_body_end =
            LastNonemptyNonEndBefore(lines,
                                     week137_state1_line,
                                     week137_interaction_elseif_line + 1);
        const std::size_t week137_state1_body_end =
            LastNonemptyNonEndBefore(lines, week137_yield_line, week137_state1_line + 1);
        if (week137_camera_if_body_end >= lines.size() ||
            week137_camera_else_body_end >= lines.size() ||
            week137_interaction_if_body_end >= lines.size() ||
            week137_interaction_else_body_end >= lines.size() ||
            week137_state1_body_end >= lines.size()) {
            continue;
        }

        std::vector<std::string> candidate_lines;
        candidate_lines.reserve(lines.size());
        candidate_lines.insert(candidate_lines.end(),
                               lines.begin(),
                               lines.begin() + static_cast<std::ptrdiff_t>(week1_outer_while_line));

        candidate_lines.push_back(function_body_indent + "while true do");
        candidate_lines.push_back(branch_indent + "if " + week1_condition + " then");
        append_reindented_range(week1_outer_while_line + 1,
                                week1_inner_wrapper_while,
                                function_body_indent + "  ",
                                loop_indent,
                                &candidate_lines);
        candidate_lines.push_back(loop_indent + "while " + week1_condition + " do");
        append_reindented_range(week1_body_start,
                                week1_body_end,
                                std::string(line_indent(lines[week1_body_start])),
                                state_indent,
                                &candidate_lines);
        candidate_lines.push_back(state_indent + "coroutine.yield()");
        candidate_lines.push_back(loop_indent + "end");

        candidate_lines.push_back(branch_indent + "elseif " + week3_condition + " then");
        append_reindented_range(week3_if_line + 1,
                                week3_setup_end,
                                week3_outer_indent + "  ",
                                loop_indent,
                                &candidate_lines);
        candidate_lines.push_back(loop_indent + "while " + week3_condition + " do");
        AppendReindentedSingleLine(lines,
                                   week3_state0_line,
                                   line_indent(lines[week3_state0_line]),
                                   state_indent,
                                   &candidate_lines);
        append_reindented_range(week3_state0_line + 1,
                                week3_state1_line,
                                std::string(line_indent(lines[week3_state0_line + 1])),
                                state_indent + "  ",
                                &candidate_lines);
        AppendReindentedSingleLine(lines,
                                   week3_state1_line,
                                   line_indent(lines[week3_state1_line]),
                                   state_indent,
                                   &candidate_lines);
        AppendReindentedSingleLine(lines,
                                   week3_camera_if_line,
                                   line_indent(lines[week3_camera_if_line]),
                                   state_indent + "  ",
                                   &candidate_lines);
        append_reindented_range(week3_camera_if_line + 1,
                                week3_camera_if_body_end + 1,
                                std::string(line_indent(lines[week3_camera_if_line + 1])),
                                state_indent + "    ",
                                &candidate_lines);
        AppendReindentedSingleLine(lines,
                                   week3_camera_else_line,
                                   line_indent(lines[week3_camera_else_line]),
                                   state_indent + "  ",
                                   &candidate_lines);
        append_reindented_range(week3_camera_else_line + 1,
                                week3_camera_else_body_end + 1,
                                std::string(line_indent(lines[week3_camera_else_line + 1])),
                                state_indent + "    ",
                                &candidate_lines);
        candidate_lines.push_back(state_indent + "  end");
        AppendReindentedSingleLine(lines,
                                   week3_interaction_if_line,
                                   line_indent(lines[week3_interaction_if_line]),
                                   state_indent + "  ",
                                   &candidate_lines);
        append_reindented_range(week3_interaction_if_line + 1,
                                week3_interaction_if_body_end + 1,
                                std::string(line_indent(lines[week3_interaction_if_line + 1])),
                                state_indent + "    ",
                                &candidate_lines);
        AppendReindentedSingleLine(lines,
                                   week3_interaction_elseif_line,
                                   line_indent(lines[week3_interaction_elseif_line]),
                                   state_indent + "  ",
                                   &candidate_lines);
        append_reindented_range(
            week3_interaction_elseif_line + 1,
            week3_interaction_else_body_end + 1,
            std::string(line_indent(lines[week3_interaction_elseif_line + 1])),
            state_indent + "    ",
            &candidate_lines);
        candidate_lines.push_back(state_indent + "  end");
        AppendReindentedSingleLine(lines,
                                   week3_state2_line,
                                   line_indent(lines[week3_state2_line]),
                                   state_indent,
                                   &candidate_lines);
        append_reindented_range(week3_state2_line + 1,
                                week3_state2_body_end + 1,
                                std::string(line_indent(lines[week3_state2_line + 1])),
                                state_indent + "  ",
                                &candidate_lines);
        candidate_lines.push_back(state_indent + "end");
        candidate_lines.push_back(state_indent + "coroutine.yield()");
        candidate_lines.push_back(loop_indent + "end");

        candidate_lines.push_back(branch_indent + "elseif " + week137_condition + " then");
        append_reindented_range(week137_if_line + 1,
                                week137_setup_end,
                                std::string(line_indent(lines[week137_if_line + 1])),
                                loop_indent,
                                &candidate_lines);
        candidate_lines.push_back(loop_indent + "while " + week137_condition + " do");
        AppendReindentedSingleLine(lines,
                                   week137_state0_line,
                                   line_indent(lines[week137_state0_line]),
                                   state_indent,
                                   &candidate_lines);
        AppendReindentedSingleLine(lines,
                                   week137_camera_if_line,
                                   line_indent(lines[week137_camera_if_line]),
                                   state_indent + "  ",
                                   &candidate_lines);
        append_reindented_range(week137_camera_if_line + 1,
                                week137_camera_if_body_end + 1,
                                std::string(line_indent(lines[week137_camera_if_line + 1])),
                                state_indent + "    ",
                                &candidate_lines);
        AppendReindentedSingleLine(lines,
                                   week137_camera_else_line,
                                   line_indent(lines[week137_camera_else_line]),
                                   state_indent + "  ",
                                   &candidate_lines);
        append_reindented_range(
            week137_camera_else_line + 1,
            week137_camera_else_body_end + 1,
            std::string(line_indent(lines[week137_camera_else_line + 1])),
            state_indent + "    ",
            &candidate_lines);
        candidate_lines.push_back(state_indent + "  end");
        AppendReindentedSingleLine(lines,
                                   week137_interaction_if_line,
                                   line_indent(lines[week137_interaction_if_line]),
                                   state_indent + "  ",
                                   &candidate_lines);
        append_reindented_range(
            week137_interaction_if_line + 1,
            week137_interaction_if_body_end + 1,
            std::string(line_indent(lines[week137_interaction_if_line + 1])),
            state_indent + "    ",
            &candidate_lines);
        AppendReindentedSingleLine(lines,
                                   week137_interaction_elseif_line,
                                   line_indent(lines[week137_interaction_elseif_line]),
                                   state_indent + "  ",
                                   &candidate_lines);
        append_reindented_range(
            week137_interaction_elseif_line + 1,
            week137_interaction_else_body_end + 1,
            std::string(line_indent(lines[week137_interaction_elseif_line + 1])),
            state_indent + "    ",
            &candidate_lines);
        candidate_lines.push_back(state_indent + "  end");
        AppendReindentedSingleLine(lines,
                                   week137_state1_line,
                                   line_indent(lines[week137_state1_line]),
                                   state_indent,
                                   &candidate_lines);
        append_reindented_range(week137_state1_line + 1,
                                week137_state1_body_end + 1,
                                std::string(line_indent(lines[week137_state1_line + 1])),
                                state_indent + "  ",
                                &candidate_lines);
        candidate_lines.push_back(state_indent + "end");
        candidate_lines.push_back(state_indent + "coroutine.yield()");
        candidate_lines.push_back(loop_indent + "end");

        candidate_lines.push_back(branch_indent + "end");
        candidate_lines.push_back(branch_indent + "coroutine.yield()");
        candidate_lines.push_back(function_body_indent + "end");

        candidate_lines.insert(candidate_lines.end(),
                               lines.begin() + static_cast<std::ptrdiff_t>(tail_resume_line),
                               lines.end());

        if (candidate_tracker.Consider(
                std::move(candidate_lines),
                "syntax_repaired_rebuilt_week_cutscene_state_ladder")) {
            return true;
        }
    }

    return candidate_tracker.CommitBestProgress(
        "syntax_repaired_rebuilt_week_cutscene_state_ladder_progress");
}

bool TryRestoreWeeklyStateResumeFromSiblingTemplate(std::string& decompiled_source,
                                                    std::string_view validation_error,
                                                    std::string* note) {
    if (validation_error.find("'end' expected") == std::string_view::npos &&
        validation_error.find("'<eof>' expected") == std::string_view::npos) {
        return false;
    }

    bool had_trailing_newline = false;
    const std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);
    if (lines.size() < 32) {
        return false;
    }

    const std::size_t reported_line = ExtractLuaErrorLine(validation_error);
    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimWhitespace(refii::lua::StripLuaLineComment(line));
    };
    const auto line_indent = [](std::string_view line) -> std::string_view {
        const std::size_t first_non_ws = line.find_first_not_of(" \t");
        if (first_non_ws == std::string_view::npos) {
            return line;
        }
        return line.substr(0, first_non_ws);
    };
    const auto next_nonempty = [&](std::size_t start) -> std::size_t {
        for (std::size_t i = start; i < lines.size(); ++i) {
            if (!trim_line(lines[i]).empty()) {
                return i;
            }
        }
        return lines.size();
    };
    const auto find_matching_header = [&](std::size_t start,
                                          std::size_t stop,
                                          std::string_view prefix,
                                          std::string_view must_contain,
                                          std::string* condition) -> std::size_t {
        for (std::size_t probe = start; probe < stop && probe < lines.size(); ++probe) {
            const std::string_view trimmed = trim_line(lines[probe]);
            if (!StartsWith(trimmed, prefix) || !EndsWith(trimmed, " then")) {
                continue;
            }

            const std::size_t prefix_len = prefix.size();
            const std::string_view parsed =
                TrimWhitespace(trimmed.substr(prefix_len, trimmed.size() - (prefix_len + 5)));
            if (!must_contain.empty() && parsed.find(must_contain) == std::string_view::npos) {
                continue;
            }
            if (condition != nullptr) {
                *condition = std::string(parsed);
            }
            return probe;
        }
        return lines.size();
    };
    const auto find_matching_while = [&](std::size_t start,
                                         std::size_t stop,
                                         std::string_view must_contain,
                                         std::string* condition) -> std::size_t {
        for (std::size_t probe = start; probe < stop && probe < lines.size(); ++probe) {
            const std::string_view trimmed = trim_line(lines[probe]);
            if (!StartsWith(trimmed, "while ") || !EndsWith(trimmed, " do")) {
                continue;
            }
            const std::string_view parsed =
                TrimWhitespace(trimmed.substr(6, trimmed.size() - 9));
            if (!must_contain.empty() && parsed.find(must_contain) == std::string_view::npos) {
                continue;
            }
            if (condition != nullptr) {
                *condition = std::string(parsed);
            }
            return probe;
        }
        return lines.size();
    };
    const auto find_end_at_indent = [&](std::size_t start,
                                        std::size_t stop,
                                        std::string_view indent) -> std::size_t {
        for (std::size_t probe = start; probe < stop && probe < lines.size(); ++probe) {
            if (trim_line(lines[probe]) == "end" && line_indent(lines[probe]) == indent) {
                return probe;
            }
        }
        return lines.size();
    };
    const auto parse_top_level_anonymous_assignment = [&](std::size_t line_index) -> bool {
        if (line_index >= lines.size() || lines[line_index].find_first_not_of(" \t") != 0) {
            return false;
        }

        const std::string_view trimmed = trim_line(lines[line_index]);
        const std::size_t equal = trimmed.find('=');
        if (equal == std::string_view::npos) {
            return false;
        }

        const std::string_view rhs = TrimWhitespace(trimmed.substr(equal + 1));
        return StartsWith(rhs, "function");
    };
    const auto find_next_top_level_binding = [&](std::size_t start) -> std::size_t {
        for (std::size_t probe = start; probe < lines.size(); ++probe) {
            if (lines[probe].find_first_not_of(" \t") != 0) {
                continue;
            }
            const std::string_view trimmed = trim_line(lines[probe]);
            if (trimmed.empty()) {
                continue;
            }
            const std::size_t equal = trimmed.find('=');
            if (equal == std::string_view::npos) {
                continue;
            }
            return probe;
        }
        return lines.size();
    };
    const auto extract_branch_lhs = [](std::string_view condition, std::string* lhs) -> bool {
        if (lhs == nullptr) {
            return false;
        }
        const std::size_t equals = condition.find("==");
        if (equals == std::string_view::npos) {
            return false;
        }
        const std::string_view extracted = TrimWhitespace(condition.substr(0, equals));
        if (extracted.empty()) {
            return false;
        }
        *lhs = std::string(extracted);
        return true;
    };
    const auto append_reindented_range =
        [&](std::vector<std::string>* out_lines,
            std::size_t start,
            std::size_t end_exclusive,
            std::string_view source_base,
            std::string_view target_base) {
            if (out_lines == nullptr || start >= end_exclusive || end_exclusive > lines.size()) {
                return;
            }

            for (std::size_t i = start; i < end_exclusive; ++i) {
                const std::string_view trimmed = trim_line(lines[i]);
                if (trimmed.empty()) {
                    out_lines->push_back(std::string(lines[i]));
                    continue;
                }
                const std::string_view current_indent = line_indent(lines[i]);
                if (current_indent.starts_with(source_base)) {
                    out_lines->push_back(std::string(target_base) +
                                         std::string(current_indent.substr(source_base.size())) +
                                         std::string(lines[i].substr(current_indent.size())));
                } else {
                    out_lines->push_back(std::string(target_base) + std::string(trimmed));
                }
            }
        };

    SyntaxRepairCandidateTracker candidate_tracker(validation_error,
                                                   had_trailing_newline,
                                                   &decompiled_source,
                                                   note);

    for (std::size_t function_start = 0; function_start < lines.size(); ++function_start) {
        if (!parse_top_level_anonymous_assignment(function_start)) {
            continue;
        }
        if (reported_line != 0) {
            const std::size_t current_line = function_start + 1;
            if (current_line + 320 < reported_line || current_line > reported_line + 16) {
                continue;
            }
        }

        const std::string function_indent(line_indent(lines[function_start]));
        const std::size_t outer_while_line =
            next_nonempty(function_start + 1);
        if (outer_while_line >= lines.size() ||
            trim_line(lines[outer_while_line]) != "while true do") {
            continue;
        }

        std::string week1_condition;
        const std::size_t week1_if_line =
            find_matching_header(outer_while_line + 1,
                                 outer_while_line + 24,
                                 "if ",
                                 "ParentQuest.CurrentWeek == 1",
                                 &week1_condition);
        const std::size_t week3_if_line =
            find_matching_header(week1_if_line + 1,
                                 lines.size(),
                                 "elseif ",
                                 "ParentQuest.CurrentWeek == 3",
                                 nullptr);
        const std::size_t week137_if_line =
            find_matching_header(week3_if_line + 1,
                                 lines.size(),
                                 "elseif ",
                                 "ParentQuest.CurrentWeek == 137",
                                 nullptr);
        if (week1_if_line >= lines.size() || week3_if_line >= lines.size() ||
            week137_if_line >= lines.size()) {
            continue;
        }

        const std::size_t week1_loop_line =
            find_matching_while(week1_if_line + 1, week3_if_line, "ParentQuest.CurrentWeek == 1", nullptr);
        if (week1_loop_line >= lines.size()) {
            continue;
        }

        std::string week1_state1_condition;
        const std::size_t week1_state1_line =
            find_matching_header(week1_loop_line + 1,
                                 week3_if_line,
                                 "elseif ",
                                 "CurrentState == 1",
                                 &week1_state1_condition);
        if (week1_state1_line >= lines.size()) {
            continue;
        }

        const std::size_t week1_inner_loop_line =
            find_matching_while(week1_state1_line + 1,
                                week3_if_line,
                                "ParentQuest.CurrentWeek == 1",
                                nullptr);
        if (week1_inner_loop_line >= lines.size()) {
            continue;
        }
        const std::size_t week1_inner_yield_line = [&]() -> std::size_t {
            for (std::size_t probe = week1_inner_loop_line + 1; probe < week3_if_line; ++probe) {
                if (trim_line(lines[probe]) == "coroutine.yield()") {
                    return probe;
                }
            }
            return lines.size();
        }();
        const std::size_t week1_inner_loop_end =
            next_nonempty(week1_inner_yield_line + 1);
        if (week1_inner_yield_line >= lines.size() || week1_inner_loop_end >= lines.size() ||
            trim_line(lines[week1_inner_loop_end]) != "end") {
            continue;
        }

        std::string week3_state2_condition;
        const std::size_t week3_state2_line =
            find_matching_header(week3_if_line + 1,
                                 week137_if_line,
                                 "elseif ",
                                 "CurrentState == 2",
                                 &week3_state2_condition);
        const std::size_t week3_yield_line =
            find_matching_header(week3_if_line + 1,
                                 week137_if_line,
                                 "",
                                 "",
                                 nullptr);
        (void)week3_yield_line;
        const std::size_t copied_yield_line = [&]() -> std::size_t {
            for (std::size_t probe = week3_state2_line + 1; probe < week137_if_line; ++probe) {
                if (trim_line(lines[probe]) == "coroutine.yield()") {
                    return probe;
                }
            }
            return lines.size();
        }();
        if (week3_state2_line >= lines.size() || copied_yield_line >= lines.size()) {
            continue;
        }

        std::string state_lhs;
        if (!extract_branch_lhs(week1_state1_condition, &state_lhs)) {
            continue;
        }

        std::vector<std::string> candidate_lines;
        candidate_lines.reserve(lines.size() + 8);
        candidate_lines.insert(candidate_lines.end(),
                               lines.begin(),
                               lines.begin() + static_cast<std::ptrdiff_t>(week1_inner_loop_end + 1));
        candidate_lines.push_back(std::string(line_indent(lines[week1_inner_loop_line])) +
                                  state_lhs + " = 2");
        append_reindented_range(&candidate_lines,
                                week3_state2_line,
                                copied_yield_line + 1,
                                line_indent(lines[week3_state2_line]),
                                line_indent(lines[week1_state1_line]));
        candidate_lines.push_back(std::string(line_indent(lines[week1_loop_line])) + "end");
        candidate_lines.insert(candidate_lines.end(),
                               lines.begin() + static_cast<std::ptrdiff_t>(week3_if_line),
                               lines.end());

        if (candidate_tracker.Consider(
                std::move(candidate_lines),
                "syntax_repaired_restored_weekly_state_resume_from_sibling_template")) {
            return true;
        }
    }

    return candidate_tracker.CommitBestProgress(
        "syntax_repaired_restored_weekly_state_resume_from_sibling_template_progress");
}

bool TryRebalanceDanglingEndBeforeElseIf(std::string& decompiled_source,
                                         std::string_view validation_error,
                                         std::string* note) {
    if (validation_error.find("'end' expected") == std::string_view::npos ||
        validation_error.find("near 'elseif'") == std::string_view::npos) {
        return false;
    }

    bool had_trailing_newline = false;
    const std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);
    if (lines.size() < 8) {
        return false;
    }

    const std::size_t reported_line = ExtractLuaErrorLine(validation_error);
    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimWhitespace(refii::lua::StripLuaLineComment(line));
    };
    const auto line_indent = [](std::string_view line) -> std::string_view {
        const std::size_t first_non_ws = line.find_first_not_of(" \t");
        if (first_non_ws == std::string_view::npos) {
            return line;
        }
        return line.substr(0, first_non_ws);
    };
    const auto previous_nonempty = [&](std::size_t start) -> std::size_t {
        if (start == 0 || start > lines.size()) {
            return lines.size();
        }
        for (std::size_t i = start; i-- > 0;) {
            if (!trim_line(lines[i]).empty()) {
                return i;
            }
            if (i == 0) {
                break;
            }
        }
        return lines.size();
    };
    const auto is_if_header = [&](std::size_t index) -> bool {
        if (index >= lines.size()) {
            return false;
        }
        const std::string_view trimmed = trim_line(lines[index]);
        return StartsWith(trimmed, "if ") && EndsWith(trimmed, " then");
    };

    SyntaxRepairCandidateTracker candidate_tracker(validation_error,
                                                   had_trailing_newline,
                                                   &decompiled_source,
                                                   note);

    for (std::size_t elseif_line = 0; elseif_line < lines.size(); ++elseif_line) {
        const std::size_t current_line = elseif_line + 1;
        if (reported_line != 0 &&
            (current_line + 24 < reported_line || current_line > reported_line + 4)) {
            continue;
        }

        const std::string_view elseif_trimmed = trim_line(lines[elseif_line]);
        if (!StartsWith(elseif_trimmed, "elseif ") || !EndsWith(elseif_trimmed, " then")) {
            continue;
        }

        const std::string elseif_indent(line_indent(lines[elseif_line]));
        std::size_t else_line = lines.size();
        for (std::size_t probe = elseif_line; probe-- > 0;) {
            const std::string_view probe_trimmed = trim_line(lines[probe]);
            if (probe_trimmed.empty()) {
                continue;
            }
            if (probe_trimmed == "else" && line_indent(lines[probe]) == elseif_indent) {
                else_line = probe;
                break;
            }
            if (line_indent(lines[probe]).size() < elseif_indent.size()) {
                break;
            }
            if (probe == 0 || probe + 64 < elseif_line) {
                break;
            }
        }
        if (else_line >= lines.size()) {
            continue;
        }

        const std::size_t stray_end_line = previous_nonempty(else_line);
        if (stray_end_line >= lines.size() ||
            trim_line(lines[stray_end_line]) != "end" ||
            line_indent(lines[stray_end_line]) != elseif_indent) {
            continue;
        }

        std::size_t if_line = lines.size();
        for (std::size_t probe = stray_end_line; probe-- > 0;) {
            const std::string_view probe_trimmed = trim_line(lines[probe]);
            if (probe_trimmed.empty()) {
                continue;
            }
            if (line_indent(lines[probe]) == elseif_indent && is_if_header(probe)) {
                if_line = probe;
                break;
            }
            if (line_indent(lines[probe]).size() < elseif_indent.size()) {
                break;
            }
            if (probe == 0 || probe + 64 < stray_end_line) {
                break;
            }
        }
        if (if_line >= lines.size()) {
            continue;
        }

        std::vector<std::string> candidate_lines = lines;
        candidate_lines.erase(candidate_lines.begin() +
                              static_cast<std::ptrdiff_t>(stray_end_line));
        const std::size_t adjusted_elseif_line =
            elseif_line - (stray_end_line < elseif_line ? 1 : 0);
        candidate_lines.insert(
            candidate_lines.begin() + static_cast<std::ptrdiff_t>(adjusted_elseif_line),
            elseif_indent + "end");

        if (candidate_tracker.Consider(
                std::move(candidate_lines),
                "syntax_repaired_rebalanced_dangling_end_before_elseif")) {
            return true;
        }
    }

    return candidate_tracker.CommitBestProgress(
        "syntax_repaired_rebalanced_dangling_end_before_elseif_progress");
}

bool TryCloseInnerIfBeforeStateElse(std::string& decompiled_source,
                                    std::string_view validation_error,
                                    std::string* note) {
    if (validation_error.find("'end' expected") == std::string_view::npos ||
        validation_error.find("near 'else'") == std::string_view::npos) {
        return false;
    }

    bool had_trailing_newline = false;
    const std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);
    if (lines.size() < 8) {
        return false;
    }

    const std::size_t reported_line = ExtractLuaErrorLine(validation_error);
    if (reported_line == 0 || reported_line > lines.size()) {
        return false;
    }

    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimWhitespace(refii::lua::StripLuaLineComment(line));
    };
    const auto line_indent = [](std::string_view line) -> std::string_view {
        const std::size_t first_non_ws = line.find_first_not_of(" \t");
        if (first_non_ws == std::string_view::npos) {
            return line;
        }
        return line.substr(0, first_non_ws);
    };
    const auto next_nonempty = [&](std::size_t start) -> std::size_t {
        for (std::size_t i = start; i < lines.size(); ++i) {
            if (!trim_line(lines[i]).empty()) {
                return i;
            }
        }
        return lines.size();
    };
    const auto previous_nonempty = [&](std::size_t start) -> std::size_t {
        if (start == 0 || start > lines.size()) {
            return lines.size();
        }
        for (std::size_t i = start; i-- > 0;) {
            if (!trim_line(lines[i]).empty()) {
                return i;
            }
            if (i == 0) {
                break;
            }
        }
        return lines.size();
    };
    const auto parse_if_condition = [](std::string_view line, std::string* condition) -> bool {
        if (condition == nullptr) {
            return false;
        }

        const std::string_view trimmed = refii::lua::TrimLuaWhitespace(
            refii::lua::StripLuaLineComment(line));
        if (!StartsWith(trimmed, "if ") || !EndsWith(trimmed, " then")) {
            return false;
        }

        const std::string_view extracted =
            refii::lua::TrimLuaWhitespace(trimmed.substr(3, trimmed.size() - 8));
        if (extracted.empty()) {
            return false;
        }

        *condition = std::string(extracted);
        return true;
    };
    const auto extract_state_lhs = [](std::string_view condition, std::string* lhs) -> bool {
        if (lhs == nullptr) {
            return false;
        }
        const std::size_t equals = condition.find("==");
        if (equals == std::string_view::npos) {
            return false;
        }

        const std::string_view extracted =
            refii::lua::TrimLuaWhitespace(condition.substr(0, equals));
        if (extracted.empty()) {
            return false;
        }
        if (extracted.find("State") == std::string_view::npos &&
            extracted.find("ThreadVar") == std::string_view::npos) {
            return false;
        }

        *lhs = std::string(extracted);
        return true;
    };

    const std::size_t else_line = reported_line - 1;
    if (trim_line(lines[else_line]) != "else") {
        return false;
    }

    const std::size_t state_if_line = next_nonempty(else_line + 1);
    std::string state_condition;
    std::string state_lhs;
    if (state_if_line >= lines.size() ||
        !parse_if_condition(lines[state_if_line], &state_condition) ||
        !extract_state_lhs(state_condition, &state_lhs)) {
        return false;
    }

    const std::string else_indent(line_indent(lines[else_line]));
    if (else_indent.empty() || line_indent(lines[state_if_line]).size() <= else_indent.size()) {
        return false;
    }

    std::size_t inner_if_line = lines.size();
    std::size_t inner_if_else_line = lines.size();
    for (std::size_t probe = else_line; probe-- > 0;) {
        const std::string_view probe_trimmed = trim_line(lines[probe]);
        if (probe_trimmed.empty()) {
            if (probe == 0) {
                break;
            }
            continue;
        }

        if (line_indent(lines[probe]) == else_indent && probe_trimmed == "else") {
            inner_if_else_line = probe;
            break;
        }

        if (line_indent(lines[probe]).size() < else_indent.size() || probe + 32 < else_line) {
            break;
        }
        if (probe == 0) {
            break;
        }
    }
    if (inner_if_else_line >= lines.size()) {
        return false;
    }

    for (std::size_t probe = inner_if_else_line; probe-- > 0;) {
        const std::string_view probe_trimmed = trim_line(lines[probe]);
        if (probe_trimmed.empty()) {
            if (probe == 0) {
                break;
            }
            continue;
        }

        std::string ignored_condition;
        if (line_indent(lines[probe]) == else_indent &&
            parse_if_condition(lines[probe], &ignored_condition)) {
            inner_if_line = probe;
            break;
        }

        if (line_indent(lines[probe]).size() < else_indent.size() ||
            probe + 32 < inner_if_else_line) {
            break;
        }
        if (probe == 0) {
            break;
        }
    }
    if (inner_if_line >= lines.size()) {
        return false;
    }

    bool saw_matching_state_branch = false;
    for (std::size_t probe = inner_if_line; probe-- > 0;) {
        std::string probe_condition;
        std::string probe_lhs;
        const std::string_view probe_trimmed = trim_line(lines[probe]);
        if ((StartsWith(probe_trimmed, "if ") || StartsWith(probe_trimmed, "elseif ")) &&
            EndsWith(probe_trimmed, " then") &&
            parse_if_condition(StartsWith(probe_trimmed, "elseif ")
                                   ? std::string("if ") +
                                         std::string(refii::lua::TrimLuaWhitespace(
                                             probe_trimmed.substr(7)))
                                   : std::string(lines[probe]),
                               &probe_condition) &&
            extract_state_lhs(probe_condition, &probe_lhs) &&
            probe_lhs == state_lhs) {
            saw_matching_state_branch = true;
            break;
        }

        if (line_indent(lines[probe]).size() + 2 < else_indent.size() ||
            probe + 64 < inner_if_line) {
            break;
        }
        if (probe == 0) {
            break;
        }
    }
    if (!saw_matching_state_branch) {
        return false;
    }

    std::vector<std::string> candidate_lines = lines;
    candidate_lines.insert(candidate_lines.begin() + static_cast<std::ptrdiff_t>(else_line),
                           std::string(else_indent) + "end");
    std::string candidate = JoinLines(candidate_lines, had_trailing_newline);
    const std::string candidate_error = ValidateLuaRepairSource(candidate);
    if (candidate_error.empty()) {
        decompiled_source = std::move(candidate);
        if (note != nullptr) {
            *note = JoinNotes(*note,
                              "syntax_repaired_closed_inner_if_before_state_else");
        }
        return true;
    }

    if (!IsValidationOutcomeBetter(validation_error, candidate_error)) {
        return false;
    }

    decompiled_source = std::move(candidate);
    if (note != nullptr) {
        *note = JoinNotes(*note,
                          "syntax_repaired_closed_inner_if_before_state_else_progress");
    }
    return true;
}

bool TryReframeLocalStateBranchAfterDanglingElse(std::string& decompiled_source,
                                                 std::string_view validation_error,
                                                 std::string* note) {
    if (validation_error.find("near 'else'") == std::string_view::npos) {
        return false;
    }

    bool had_trailing_newline = false;
    const std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);
    if (lines.size() < 12) {
        return false;
    }

    const std::size_t reported_line = ExtractLuaErrorLine(validation_error);
    if (reported_line == 0 || reported_line > lines.size()) {
        return false;
    }

    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimWhitespace(refii::lua::StripLuaLineComment(line));
    };
    const auto line_indent = [](std::string_view line) -> std::string_view {
        const std::size_t first_non_ws = line.find_first_not_of(" \t");
        if (first_non_ws == std::string_view::npos) {
            return line;
        }
        return line.substr(0, first_non_ws);
    };
    const auto next_nonempty = [&](std::size_t start) -> std::size_t {
        for (std::size_t i = start; i < lines.size(); ++i) {
            if (!trim_line(lines[i]).empty()) {
                return i;
            }
        }
        return lines.size();
    };
    const auto previous_nonempty = [&](std::size_t start) -> std::size_t {
        if (start == 0 || start > lines.size()) {
            return lines.size();
        }
        for (std::size_t i = start; i-- > 0;) {
            if (!trim_line(lines[i]).empty()) {
                return i;
            }
            if (i == 0) {
                break;
            }
        }
        return lines.size();
    };
    const auto parse_if_or_while_state_condition =
        [](std::string_view line, std::string* condition) -> bool {
        if (condition == nullptr) {
            return false;
        }

        const std::string_view trimmed = refii::lua::TrimLuaWhitespace(
            refii::lua::StripLuaLineComment(line));
        if (StartsWith(trimmed, "if ") && EndsWith(trimmed, " then")) {
            const std::string_view extracted =
                refii::lua::TrimLuaWhitespace(trimmed.substr(3, trimmed.size() - 8));
            if (extracted.empty()) {
                return false;
            }
            *condition = std::string(extracted);
            return true;
        }
        if (StartsWith(trimmed, "while ") && EndsWith(trimmed, " do")) {
            const std::string_view extracted =
                refii::lua::TrimLuaWhitespace(trimmed.substr(6, trimmed.size() - 9));
            if (extracted.empty()) {
                return false;
            }
            *condition = std::string(extracted);
            return true;
        }
        return false;
    };
    const auto extract_state_lhs = [](std::string_view condition, std::string* lhs) -> bool {
        if (lhs == nullptr) {
            return false;
        }

        const std::size_t equals = condition.find("==");
        if (equals == std::string_view::npos) {
            return false;
        }

        const std::string_view extracted =
            refii::lua::TrimLuaWhitespace(condition.substr(0, equals));
        if (extracted.empty()) {
            return false;
        }
        if (extracted.find("State") == std::string_view::npos &&
            extracted.find("ThreadVar") == std::string_view::npos) {
            return false;
        }

        *lhs = std::string(extracted);
        return true;
    };
    const auto assigns_same_state_lhs = [&](std::string_view line,
                                            std::string_view expected_lhs) -> bool {
        const std::string_view trimmed = trim_line(line);
        if (trimmed.empty()) {
            return false;
        }

        const std::size_t equals = trimmed.find('=');
        if (equals == std::string_view::npos) {
            return false;
        }
        if ((equals > 0 && trimmed[equals - 1] == '=') ||
            (equals + 1 < trimmed.size() && trimmed[equals + 1] == '=')) {
            return false;
        }

        const std::string_view lhs =
            refii::lua::TrimLuaWhitespace(trimmed.substr(0, equals));
        return lhs == expected_lhs;
    };

    SyntaxRepairCandidateTracker candidate_tracker(validation_error,
                                                   had_trailing_newline,
                                                   &decompiled_source,
                                                   note);

    const auto is_continuation_statement = [&](std::string_view line) -> bool {
        const std::string_view trimmed = trim_line(line);
        if (trimmed.empty()) {
            return false;
        }
        return !StartsWith(trimmed, "if ") && !StartsWith(trimmed, "elseif ") &&
               !StartsWith(trimmed, "while ") && !StartsWith(trimmed, "for ") &&
               !StartsWith(trimmed, "repeat") && !StartsWith(trimmed, "function ") &&
               !StartsWith(trimmed, "local function") && trimmed != "else" &&
               trimmed != "end";
    };

    for (std::size_t promoted_else_line = 0; promoted_else_line < lines.size();
         ++promoted_else_line) {
        const std::size_t current_line = promoted_else_line + 1;
        if (current_line + 96 < reported_line || current_line > reported_line + 8) {
            continue;
        }
        if (trim_line(lines[promoted_else_line]) != "else") {
            continue;
        }

        const std::size_t current_state_header_line = next_nonempty(promoted_else_line + 1);
        std::string current_state_condition;
        std::string state_lhs;
        if (current_state_header_line >= lines.size() ||
            !parse_if_or_while_state_condition(
                lines[current_state_header_line], &current_state_condition) ||
            !extract_state_lhs(current_state_condition, &state_lhs)) {
            continue;
        }
        const std::string_view current_header_trimmed =
            trim_line(lines[current_state_header_line]);
        if (!StartsWith(current_header_trimmed, "while ") ||
            !EndsWith(current_header_trimmed, " do")) {
            continue;
        }

        const std::string current_header_indent(line_indent(lines[current_state_header_line]));
        const std::string current_else_indent(line_indent(lines[promoted_else_line]));
        if (current_header_indent.size() <= current_else_indent.size() ||
            !StartsWith(current_header_indent, current_else_indent)) {
            continue;
        }

        bool saw_prior_state_guard = false;
        for (std::size_t probe = promoted_else_line; probe-- > 0 &&
                                                   promoted_else_line - probe <= 512;) {
            std::string probe_condition;
            std::string probe_lhs;
            if (parse_if_or_while_state_condition(lines[probe], &probe_condition) &&
                extract_state_lhs(probe_condition, &probe_lhs) &&
                probe_lhs == state_lhs) {
                saw_prior_state_guard = true;
                break;
            }

            if (probe == 0) {
                break;
            }
        }
        if (!saw_prior_state_guard) {
            continue;
        }

        std::size_t continuation_else_line = lines.size();
        for (std::size_t probe = promoted_else_line; probe-- > 0 &&
                                                   promoted_else_line - probe <= 64;) {
            if (trim_line(lines[probe]) != "else") {
                continue;
            }

            const std::size_t next_line = next_nonempty(probe + 1);
            const std::size_t previous_line = previous_nonempty(probe);
            if (next_line >= lines.size() || previous_line >= lines.size()) {
                continue;
            }
            if (trim_line(lines[previous_line]) != "end" ||
                !is_continuation_statement(lines[next_line])) {
                continue;
            }

            continuation_else_line = probe;
            break;
        }
        if (continuation_else_line >= lines.size()) {
            continue;
        }

        bool saw_transition_to_same_state = false;
        for (std::size_t probe = continuation_else_line + 1;
             probe < promoted_else_line && probe <= continuation_else_line + 48;
             ++probe) {
            if (assigns_same_state_lhs(lines[probe], state_lhs)) {
                saw_transition_to_same_state = true;
                break;
            }
        }
        if (!saw_transition_to_same_state) {
            continue;
        }

        std::vector<std::string> candidate_lines = lines;
        candidate_lines[continuation_else_line] =
            std::string(line_indent(candidate_lines[continuation_else_line])) + "end";
        candidate_lines[promoted_else_line] =
            current_else_indent + "if " + current_state_condition + " then";
        candidate_lines.erase(candidate_lines.begin() +
                              static_cast<std::ptrdiff_t>(current_state_header_line));

        if (candidate_tracker.Consider(
                std::move(candidate_lines),
                "syntax_repaired_reframed_local_state_branch_after_dangling_else")) {
            return true;
        }
    }

    return candidate_tracker.CommitBestProgress(
        "syntax_repaired_reframed_local_state_branch_after_dangling_else_progress");
}

bool TryRebuildStateTransitionWaitHandoff(std::string& decompiled_source,
                                          std::string_view validation_error,
                                          std::string* note) {
    if (validation_error.find("near 'else'") == std::string_view::npos) {
        return false;
    }

    bool had_trailing_newline = false;
    const std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);
    if (lines.size() < 18) {
        return false;
    }

    const std::size_t reported_line = ExtractLuaErrorLine(validation_error);
    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimWhitespace(refii::lua::StripLuaLineComment(line));
    };
    const auto line_indent = [](std::string_view line) -> std::string_view {
        const std::size_t first_non_ws = line.find_first_not_of(" \t");
        if (first_non_ws == std::string_view::npos) {
            return line;
        }
        return line.substr(0, first_non_ws);
    };
    const auto next_nonempty = [&](std::size_t start) -> std::size_t {
        for (std::size_t i = start; i < lines.size(); ++i) {
            if (!trim_line(lines[i]).empty()) {
                return i;
            }
        }
        return lines.size();
    };
    const auto previous_nonempty = [&](std::size_t start) -> std::size_t {
        if (start == 0 || start > lines.size()) {
            return lines.size();
        }
        for (std::size_t i = start; i-- > 0;) {
            if (!trim_line(lines[i]).empty()) {
                return i;
            }
            if (i == 0) {
                break;
            }
        }
        return lines.size();
    };
    const auto parse_if_condition = [](std::string_view line, std::string* condition) -> bool {
        if (condition == nullptr) {
            return false;
        }

        const std::string_view trimmed = refii::lua::TrimLuaWhitespace(
            refii::lua::StripLuaLineComment(line));
        if (!StartsWith(trimmed, "if ") || !EndsWith(trimmed, " then")) {
            return false;
        }

        const std::string_view extracted =
            refii::lua::TrimLuaWhitespace(trimmed.substr(3, trimmed.size() - 8));
        if (extracted.empty()) {
            return false;
        }

        *condition = std::string(extracted);
        return true;
    };
    const auto parse_elseif_condition = [](std::string_view line,
                                           std::string* condition) -> bool {
        if (condition == nullptr) {
            return false;
        }

        const std::string_view trimmed = refii::lua::TrimLuaWhitespace(
            refii::lua::StripLuaLineComment(line));
        if (!StartsWith(trimmed, "elseif ") || !EndsWith(trimmed, " then")) {
            return false;
        }

        const std::string_view extracted =
            refii::lua::TrimLuaWhitespace(trimmed.substr(7, trimmed.size() - 12));
        if (extracted.empty()) {
            return false;
        }

        *condition = std::string(extracted);
        return true;
    };
    const auto extract_state_lhs = [](std::string_view condition, std::string* lhs) -> bool {
        if (lhs == nullptr) {
            return false;
        }

        const std::size_t equals = condition.find("==");
        if (equals == std::string_view::npos) {
            return false;
        }

        const std::string_view extracted =
            refii::lua::TrimLuaWhitespace(condition.substr(0, equals));
        if (extracted.empty()) {
            return false;
        }
        if (extracted.find("State") == std::string_view::npos &&
            extracted.find("ThreadVar") == std::string_view::npos) {
            return false;
        }

        *lhs = std::string(extracted);
        return true;
    };
    const auto assigns_same_state_lhs = [&](std::string_view line,
                                            std::string_view expected_lhs) -> bool {
        const std::string_view trimmed = trim_line(line);
        const std::size_t equals = trimmed.find('=');
        if (equals == std::string_view::npos) {
            return false;
        }
        if ((equals > 0 && trimmed[equals - 1] == '=') ||
            (equals + 1 < trimmed.size() && trimmed[equals + 1] == '=')) {
            return false;
        }

        const std::string_view lhs =
            refii::lua::TrimLuaWhitespace(trimmed.substr(0, equals));
        return lhs == expected_lhs;
    };

    SyntaxRepairCandidateTracker candidate_tracker(validation_error,
                                                   had_trailing_newline,
                                                   &decompiled_source,
                                                   note);

    for (std::size_t elseif_line = 0; elseif_line < lines.size(); ++elseif_line) {
        const std::size_t current_line = elseif_line + 1;
        if (reported_line != 0 &&
            (current_line + 96 < reported_line || current_line > reported_line + 16)) {
            continue;
        }

        std::string elseif_condition;
        std::string state_lhs;
        if (!parse_elseif_condition(lines[elseif_line], &elseif_condition) ||
            !extract_state_lhs(elseif_condition, &state_lhs)) {
            continue;
        }

        const std::size_t outer_end_line = previous_nonempty(elseif_line);
        const std::size_t state_assign_line = previous_nonempty(outer_end_line);
        const std::size_t dangling_else_line = previous_nonempty(state_assign_line);
        const std::size_t yield_line = previous_nonempty(dangling_else_line);
        const std::size_t inner_else_line = previous_nonempty(yield_line);
        if (outer_end_line >= lines.size() || state_assign_line >= lines.size() ||
            dangling_else_line >= lines.size() || yield_line >= lines.size() ||
            inner_else_line >= lines.size() || trim_line(lines[outer_end_line]) != "end" ||
            trim_line(lines[dangling_else_line]) != "else" ||
            trim_line(lines[yield_line]) != "coroutine.yield()" ||
            trim_line(lines[inner_else_line]) != "else" ||
            !assigns_same_state_lhs(lines[state_assign_line], state_lhs)) {
            continue;
        }

        const std::size_t then_body_last = previous_nonempty(inner_else_line);
        if (then_body_last >= lines.size() || trim_line(lines[then_body_last]) == "end") {
            continue;
        }

        std::size_t inner_if_line = lines.size();
        std::string inner_if_condition;
        const std::string inner_if_indent(line_indent(lines[inner_else_line]));
        for (std::size_t probe = inner_else_line; probe-- > 0 && inner_else_line - probe <= 16;) {
            if (line_indent(lines[probe]) != inner_if_indent ||
                !parse_if_condition(lines[probe], &inner_if_condition)) {
                if (probe == 0) {
                    break;
                }
                continue;
            }
            inner_if_line = probe;
            break;
        }
        if (inner_if_line >= lines.size()) {
            continue;
        }

        const std::size_t inner_wait_wrapper_line = previous_nonempty(inner_if_line);
        const std::size_t outer_wait_wrapper_line = previous_nonempty(inner_wait_wrapper_line);
        if (inner_wait_wrapper_line >= lines.size() ||
            outer_wait_wrapper_line >= lines.size() ||
            trim_line(lines[inner_wait_wrapper_line]) != "while true do" ||
            trim_line(lines[outer_wait_wrapper_line]) != "while true do") {
            continue;
        }

        std::size_t guard_while_line = lines.size();
        std::size_t guard_yield_line = lines.size();
        std::size_t guard_wrapper_line = lines.size();
        for (std::size_t probe = outer_wait_wrapper_line; probe-- > 0 &&
                                                    outer_wait_wrapper_line - probe <= 24;) {
            const std::string_view trimmed = trim_line(lines[probe]);
            if (!StartsWith(trimmed, "while ") || !EndsWith(trimmed, " do")) {
                if (probe == 0) {
                    break;
                }
                continue;
            }
            if (trimmed.find("== false") == std::string_view::npos &&
                trimmed.find("~= true") == std::string_view::npos) {
                if (probe == 0) {
                    break;
                }
                continue;
            }

            const std::size_t next_line = next_nonempty(probe + 1);
            const std::size_t previous_line = previous_nonempty(probe);
            if (next_line >= lines.size() || previous_line >= lines.size() ||
                trim_line(lines[next_line]) != "coroutine.yield()" ||
                trim_line(lines[previous_line]) != "while true do") {
                if (probe == 0) {
                    break;
                }
                continue;
            }

            guard_while_line = probe;
            guard_yield_line = next_line;
            guard_wrapper_line = previous_line;
            break;
        }
        if (guard_while_line >= lines.size() || guard_yield_line >= lines.size() ||
            guard_wrapper_line >= lines.size()) {
            continue;
        }

        const std::size_t setup_start = next_nonempty(guard_yield_line + 1);
        if (setup_start >= outer_wait_wrapper_line) {
            continue;
        }

        std::vector<std::string> candidate_lines;
        candidate_lines.reserve(lines.size() + 4);
        candidate_lines.insert(candidate_lines.end(),
                               lines.begin(),
                               lines.begin() + static_cast<std::ptrdiff_t>(guard_wrapper_line));
        candidate_lines.push_back(lines[guard_while_line]);
        candidate_lines.push_back(lines[guard_yield_line]);
        candidate_lines.push_back(std::string(line_indent(lines[guard_while_line])) + "end");
        candidate_lines.insert(candidate_lines.end(),
                               lines.begin() + static_cast<std::ptrdiff_t>(setup_start),
                               lines.begin() +
                                   static_cast<std::ptrdiff_t>(outer_wait_wrapper_line));
        candidate_lines.push_back(lines[outer_wait_wrapper_line]);
        candidate_lines.push_back(lines[inner_if_line]);
        candidate_lines.insert(candidate_lines.end(),
                               lines.begin() + static_cast<std::ptrdiff_t>(inner_if_line + 1),
                               lines.begin() + static_cast<std::ptrdiff_t>(then_body_last + 1));
        candidate_lines.push_back(std::string(line_indent(lines[inner_if_line])) + "  break");
        candidate_lines.push_back(lines[inner_else_line]);
        candidate_lines.push_back(lines[yield_line]);
        candidate_lines.push_back(std::string(line_indent(lines[inner_if_line])) + "end");
        candidate_lines.push_back(std::string(line_indent(lines[outer_wait_wrapper_line])) +
                                  "end");
        candidate_lines.push_back(lines[state_assign_line]);
        candidate_lines.insert(candidate_lines.end(),
                               lines.begin() + static_cast<std::ptrdiff_t>(elseif_line),
                               lines.end());

        if (candidate_tracker.Consider(
                std::move(candidate_lines),
                "syntax_repaired_rebuilt_state_transition_wait_handoff")) {
            return true;
        }
    }

    return candidate_tracker.CommitBestProgress(
        "syntax_repaired_rebuilt_state_transition_wait_handoff_progress");
}

bool TryReplaceDanglingElseElseIfWithSiblingIf(std::string& decompiled_source,
                                               std::string_view validation_error,
                                               std::string* note) {
    if (validation_error.find("near 'elseif'") == std::string_view::npos) {
        return false;
    }

    bool had_trailing_newline = false;
    const std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);
    if (lines.size() < 8) {
        return false;
    }

    const std::size_t reported_line = ExtractLuaErrorLine(validation_error);
    if (reported_line == 0 || reported_line > lines.size()) {
        return false;
    }

    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimWhitespace(refii::lua::StripLuaLineComment(line));
    };
    const auto line_indent = [](std::string_view line) -> std::string_view {
        const std::size_t first_non_ws = line.find_first_not_of(" \t");
        if (first_non_ws == std::string_view::npos) {
            return line;
        }
        return line.substr(0, first_non_ws);
    };
    const auto previous_nonempty = [&](std::size_t start) -> std::size_t {
        if (start == 0 || start > lines.size()) {
            return lines.size();
        }
        for (std::size_t i = start; i-- > 0;) {
            if (!trim_line(lines[i]).empty()) {
                return i;
            }
            if (i == 0) {
                break;
            }
        }
        return lines.size();
    };
    const auto parse_elseif_condition = [](std::string_view line,
                                           std::string* condition) -> bool {
        if (condition == nullptr) {
            return false;
        }

        const std::string_view trimmed = refii::lua::TrimLuaWhitespace(
            refii::lua::StripLuaLineComment(line));
        if (!StartsWith(trimmed, "elseif ") || !EndsWith(trimmed, " then")) {
            return false;
        }

        const std::string_view extracted =
            refii::lua::TrimLuaWhitespace(trimmed.substr(7, trimmed.size() - 12));
        if (extracted.empty()) {
            return false;
        }

        *condition = std::string(extracted);
        return true;
    };

    SyntaxRepairCandidateTracker candidate_tracker(validation_error,
                                                   had_trailing_newline,
                                                   &decompiled_source,
                                                   note);

    for (std::size_t elseif_line = 0; elseif_line < lines.size(); ++elseif_line) {
        const std::size_t current_line = elseif_line + 1;
        if (current_line + 128 < reported_line || current_line > reported_line + 12) {
            continue;
        }

        std::string elseif_condition;
        if (!parse_elseif_condition(lines[elseif_line], &elseif_condition)) {
            continue;
        }

        const std::size_t dangling_else_line = previous_nonempty(elseif_line);
        if (dangling_else_line >= lines.size() ||
            trim_line(lines[dangling_else_line]) != "else") {
            continue;
        }

        const std::string elseif_indent(line_indent(lines[elseif_line]));
        if (elseif_indent.empty() || line_indent(lines[dangling_else_line]) != elseif_indent) {
            continue;
        }

        std::size_t prior_end_count = 0;
        std::size_t probe = dangling_else_line;
        while (probe > 0) {
            probe = previous_nonempty(probe);
            if (probe >= lines.size() || trim_line(lines[probe]) != "end") {
                break;
            }
            ++prior_end_count;
            if (line_indent(lines[probe]).size() < elseif_indent.size()) {
                break;
            }
            if (prior_end_count >= 3) {
                break;
            }
        }
        if (prior_end_count < 2) {
            continue;
        }

        std::vector<std::string> candidate_lines = lines;
        candidate_lines[dangling_else_line] = elseif_indent + "end";
        candidate_lines[elseif_line] = elseif_indent + "if " + elseif_condition + " then";
        if (candidate_tracker.Consider(
                std::move(candidate_lines),
                "syntax_repaired_replaced_dangling_else_elseif_with_sibling_if")) {
            return true;
        }
    }

    return candidate_tracker.CommitBestProgress(
        "syntax_repaired_replaced_dangling_else_elseif_with_sibling_if_progress");
}

bool TryInsertMissingWhileTrueBeforeYieldingStateBody(std::string& decompiled_source,
                                                      std::string_view validation_error,
                                                      std::string* note) {
    if (validation_error.find("<eof>") == std::string_view::npos &&
        validation_error.find("near 'end'") == std::string_view::npos) {
        return false;
    }

    bool had_trailing_newline = false;
    const std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);
    if (lines.size() < 8) {
        return false;
    }

    const std::size_t reported_line = ExtractLuaErrorLine(validation_error);
    const auto trimmed_line = [](std::string_view line) -> std::string_view {
        return TrimWhitespace(line);
    };
    const auto code_line = [](std::string_view line) -> std::string_view {
        return TrimWhitespace(refii::lua::StripLuaLineComment(line));
    };
    const auto line_indent = [](std::string_view line) -> std::string_view {
        const std::size_t first_non_ws = line.find_first_not_of(" \t");
        if (first_non_ws == std::string_view::npos) {
            return line;
        }
        return line.substr(0, first_non_ws);
    };
    const auto next_nonempty = [&](std::size_t start) -> std::size_t {
        for (std::size_t i = start; i < lines.size(); ++i) {
            if (!trimmed_line(lines[i]).empty()) {
                return i;
            }
        }
        return lines.size();
    };
    const auto previous_nonempty = [&](std::size_t start) -> std::size_t {
        if (start == 0 || start > lines.size()) {
            return lines.size();
        }
        for (std::size_t i = start; i-- > 0;) {
            if (!trimmed_line(lines[i]).empty()) {
                return i;
            }
            if (i == 0) {
                break;
            }
        }
        return lines.size();
    };
    const auto looks_like_state_if = [&](std::string_view line) -> bool {
        const std::string_view trimmed = code_line(line);
        return StartsWith(trimmed, "if ") && EndsWith(trimmed, " then") &&
               (trimmed.find("CurrentState") != std::string_view::npos ||
                trimmed.find("ThreadVar") != std::string_view::npos ||
                trimmed.find("State ==") != std::string_view::npos);
    };

    SyntaxRepairCandidateTracker candidate_tracker(validation_error,
                                                   had_trailing_newline,
                                                   &decompiled_source,
                                                   note);

    for (std::size_t yield_line = 0; yield_line < lines.size(); ++yield_line) {
        if (code_line(lines[yield_line]) != "coroutine.yield()") {
            continue;
        }

        if (reported_line != 0) {
            const std::size_t current_line = yield_line + 1;
            if (current_line + 96 < reported_line || current_line > reported_line + 96) {
                continue;
            }
        }

        const std::size_t previous_line = previous_nonempty(yield_line);
        const std::size_t next_line = next_nonempty(yield_line + 1);
        if (previous_line >= lines.size() || next_line >= lines.size() ||
            code_line(lines[previous_line]) != "end") {
            continue;
        }

        bool supported_tail_shape =
            StartsWith(trimmed_line(lines[next_line]), "-- unsupported backward jump");
        if (!supported_tail_shape && code_line(lines[next_line]) == "end") {
            std::size_t boundary_line = next_line;
            while (boundary_line < lines.size() && code_line(lines[boundary_line]) == "end") {
                boundary_line = next_nonempty(boundary_line + 1);
            }
            supported_tail_shape =
                boundary_line < lines.size() &&
                LooksLikeTopLevelBoundary(trimmed_line(lines[boundary_line]));
        }
        if (!supported_tail_shape) {
            continue;
        }

        std::size_t function_line = lines.size();
        for (std::size_t probe = yield_line; probe-- > 0 && yield_line - probe <= 192;) {
            const std::string_view probe_trimmed = trimmed_line(lines[probe]);
            if (probe_trimmed.find("= function(") != std::string_view::npos) {
                function_line = probe;
                break;
            }
            if (probe == 0) {
                break;
            }
        }
        if (function_line >= lines.size()) {
            continue;
        }

        bool already_has_while = false;
        for (std::size_t probe = function_line + 1; probe < yield_line; ++probe) {
            if (code_line(lines[probe]) == "while true do") {
                already_has_while = true;
                break;
            }
        }
        if (already_has_while) {
            continue;
        }

        std::size_t body_start = function_line + 1;
        while (body_start < yield_line) {
            const std::string_view raw = trimmed_line(lines[body_start]);
            const std::string_view code = code_line(lines[body_start]);
            if (raw.empty() || StartsWith(raw, "--")) {
                ++body_start;
                continue;
            }
            if (!code.empty()) {
                break;
            }
            ++body_start;
        }
        if (body_start >= yield_line || !looks_like_state_if(lines[body_start])) {
            continue;
        }

        const std::string while_indent(line_indent(lines[body_start]));
        std::vector<std::string> candidate_lines = lines;
        candidate_lines.insert(candidate_lines.begin() + static_cast<std::ptrdiff_t>(body_start),
                               while_indent + "while true do");

        if (candidate_tracker.Consider(
                std::move(candidate_lines),
                "syntax_repaired_inserted_missing_while_true_before_yield_state_body")) {
            return true;
        }
    }

    return candidate_tracker.CommitBestProgress(
        "syntax_repaired_inserted_missing_while_true_before_yield_state_body_progress");
}

bool TryCloseYieldingGuardLoopBeforeStateSetup(std::string& decompiled_source,
                                               std::string_view validation_error,
                                               std::string* note) {
    if (validation_error.find("near 'else'") == std::string_view::npos &&
        validation_error.find("near 'elseif'") == std::string_view::npos &&
        validation_error.find("near 'end'") == std::string_view::npos &&
        validation_error.find("<eof>") == std::string_view::npos) {
        return false;
    }

    bool had_trailing_newline = false;
    const std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);
    if (lines.size() < 10) {
        return false;
    }

    const std::size_t reported_line = ExtractLuaErrorLine(validation_error);
    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimWhitespace(refii::lua::StripLuaLineComment(line));
    };
    const auto line_indent = [](std::string_view line) -> std::string_view {
        const std::size_t first_non_ws = line.find_first_not_of(" \t");
        if (first_non_ws == std::string_view::npos) {
            return line;
        }
        return line.substr(0, first_non_ws);
    };
    const auto next_nonempty = [&](std::size_t start) -> std::size_t {
        for (std::size_t i = start; i < lines.size(); ++i) {
            if (!trim_line(lines[i]).empty()) {
                return i;
            }
        }
        return lines.size();
    };
    const auto previous_nonempty = [&](std::size_t start) -> std::size_t {
        if (start == 0 || start > lines.size()) {
            return lines.size();
        }
        for (std::size_t i = start; i-- > 0;) {
            if (!trim_line(lines[i]).empty()) {
                return i;
            }
            if (i == 0) {
                break;
            }
        }
        return lines.size();
    };
    const auto looks_like_state_line = [&](std::string_view line) -> bool {
        const std::string_view trimmed = trim_line(line);
        if (trimmed.find("CurrentState") != std::string_view::npos ||
            trimmed.find("ThreadVar") != std::string_view::npos) {
            return true;
        }
        return StartsWith(trimmed, "elseif ") && trimmed.find("State") != std::string_view::npos;
    };
    const auto looks_like_supported_continuation = [&](std::string_view line) -> bool {
        const std::string_view trimmed = trim_line(line);
        if (trimmed.empty() || trimmed == "end") {
            return false;
        }
        if (looks_like_state_line(line) || StartsWith(trimmed, "local ") ||
            StartsWith(trimmed, "if ") || StartsWith(trimmed, "while ") ||
            StartsWith(trimmed, "for ") || StartsWith(trimmed, "repeat") ||
            StartsWith(trimmed, "return ")) {
            return true;
        }
        return trimmed.find(':') != std::string_view::npos ||
               trimmed.find('=') != std::string_view::npos;
    };

    SyntaxRepairCandidateTracker candidate_tracker(validation_error,
                                                   had_trailing_newline,
                                                   &decompiled_source,
                                                   note);
    struct GuardLoopRepair {
        std::size_t wrapper_line = 0;
        std::size_t yield_line = 0;
        std::string guard_indent;
        bool remove_wrapper = false;
    };
    std::vector<GuardLoopRepair> qualifying_repairs;
    const auto try_record_candidate = [&](std::vector<std::string> candidate_lines,
                                          std::string_view candidate_note) -> bool {
        return candidate_tracker.Consider(std::move(candidate_lines), candidate_note);
    };

    for (std::size_t guard_while_line = 0; guard_while_line < lines.size(); ++guard_while_line) {
        const std::size_t current_line = guard_while_line + 1;
        if (reported_line != 0 &&
            (current_line + 96 < reported_line || current_line > reported_line + 8)) {
            continue;
        }

        const std::string_view guard_trimmed = trim_line(lines[guard_while_line]);
        if (!StartsWith(guard_trimmed, "while ") || !EndsWith(guard_trimmed, " do")) {
            continue;
        }
        if (guard_trimmed.find("== false") == std::string_view::npos &&
            guard_trimmed.find("~= true") == std::string_view::npos) {
            continue;
        }

        const std::string guard_indent(line_indent(lines[guard_while_line]));
        std::size_t wrapper_line = lines.size();
        bool remove_wrapper = false;
        {
            const std::size_t candidate_wrapper_line = previous_nonempty(guard_while_line);
            if (candidate_wrapper_line < lines.size() &&
                trim_line(lines[candidate_wrapper_line]) == "while true do") {
                const std::string wrapper_indent(line_indent(lines[candidate_wrapper_line]));
                if (guard_indent.size() == wrapper_indent.size() + 2 &&
                    StartsWith(guard_indent, wrapper_indent)) {
                    wrapper_line = candidate_wrapper_line;
                    remove_wrapper = true;
                }
            }
        }

        const std::size_t yield_line = next_nonempty(guard_while_line + 1);
        if (yield_line >= lines.size() || trim_line(lines[yield_line]) != "coroutine.yield()") {
            continue;
        }

        const std::string yield_indent(line_indent(lines[yield_line]));
        if (yield_indent.size() != guard_indent.size() + 2 ||
            !StartsWith(yield_indent, guard_indent)) {
            continue;
        }

        const std::size_t continuation_start = next_nonempty(yield_line + 1);
        if (continuation_start >= lines.size() || trim_line(lines[continuation_start]) == "end") {
            continue;
        }
        if (line_indent(lines[continuation_start]).size() <= guard_indent.size() ||
            !StartsWith(line_indent(lines[continuation_start]), guard_indent)) {
            continue;
        }

        bool found_supported_continuation = false;
        for (std::size_t probe = continuation_start;
             probe < lines.size() && probe <= continuation_start + 48;
             ++probe) {
            const std::string_view probe_trimmed = trim_line(lines[probe]);
            if (probe_trimmed.empty()) {
                continue;
            }
            if (looks_like_supported_continuation(lines[probe])) {
                found_supported_continuation = true;
                break;
            }
        }
        if (!found_supported_continuation) {
            continue;
        }

        qualifying_repairs.push_back({wrapper_line, yield_line, guard_indent, remove_wrapper});

        std::vector<std::string> candidate_lines = lines;
        if (remove_wrapper) {
            candidate_lines.erase(candidate_lines.begin() +
                                  static_cast<std::ptrdiff_t>(wrapper_line));
        }

        const std::size_t adjusted_yield_line =
            yield_line - (remove_wrapper && yield_line > wrapper_line ? 1u : 0u);
        candidate_lines.insert(candidate_lines.begin() +
                                   static_cast<std::ptrdiff_t>(adjusted_yield_line + 1),
                               guard_indent + "end");

        if (try_record_candidate(
                std::move(candidate_lines),
                "syntax_repaired_closed_yielding_guard_loop_before_state_setup")) {
            return true;
        }
    }

    if (qualifying_repairs.size() >= 2) {
        std::sort(qualifying_repairs.begin(),
                  qualifying_repairs.end(),
                  [](const GuardLoopRepair& left, const GuardLoopRepair& right) {
                      return left.wrapper_line < right.wrapper_line;
                  });

        const std::size_t max_chain =
            std::min<std::size_t>(qualifying_repairs.size(), 4);
        for (std::size_t chain_size = max_chain; chain_size >= 2; --chain_size) {
            for (std::size_t start = 0;
                 start + chain_size <= qualifying_repairs.size();
                 ++start) {
                const GuardLoopRepair& first = qualifying_repairs[start];
                const GuardLoopRepair& last =
                    qualifying_repairs[start + chain_size - 1];
                if (last.wrapper_line > first.wrapper_line + 96) {
                    continue;
                }

                std::vector<std::string> candidate_lines = lines;
                for (std::size_t reverse_index = start + chain_size;
                     reverse_index-- > start;) {
                    const GuardLoopRepair& repair =
                        qualifying_repairs[reverse_index];
                    if (repair.remove_wrapper) {
                        candidate_lines.erase(candidate_lines.begin() +
                                              static_cast<std::ptrdiff_t>(
                                                  repair.wrapper_line));
                    }
                    candidate_lines.insert(
                        candidate_lines.begin() +
                            static_cast<std::ptrdiff_t>(
                                repair.yield_line +
                                (repair.remove_wrapper ? 0u : 1u)),
                        repair.guard_indent + "end");
                }

                if (try_record_candidate(
                        std::move(candidate_lines),
                        "syntax_repaired_closed_yielding_guard_loop_before_state_setup")) {
                    return true;
                }
            }

            if (chain_size == 2) {
                break;
            }
        }
    }

    return candidate_tracker.CommitBestProgress(
        "syntax_repaired_closed_yielding_guard_loop_before_state_setup_progress");
}

bool TryReplaceEmptyIfCallBeforeTopLevelBoundary(std::string& decompiled_source,
                                                 std::string_view validation_error,
                                                 std::string* note) {
    if (validation_error.find("<eof>") == std::string_view::npos &&
        validation_error.find("near 'end'") == std::string_view::npos) {
        return false;
    }

    bool had_trailing_newline = false;
    const std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);
    if (lines.size() < 6) {
        return false;
    }

    const std::size_t reported_line = ExtractLuaErrorLine(validation_error);
    const auto trimmed_line = [](std::string_view line) -> std::string_view {
        return TrimWhitespace(line);
    };
    const auto code_line = [](std::string_view line) -> std::string_view {
        return TrimWhitespace(refii::lua::StripLuaLineComment(line));
    };
    const auto line_indent = [](std::string_view line) -> std::string_view {
        const std::size_t first_non_ws = line.find_first_not_of(" \t");
        if (first_non_ws == std::string_view::npos) {
            return line;
        }
        return line.substr(0, first_non_ws);
    };
    const auto next_nonempty = [&](std::size_t start) -> std::size_t {
        for (std::size_t i = start; i < lines.size(); ++i) {
            if (!trimmed_line(lines[i]).empty()) {
                return i;
            }
        }
        return lines.size();
    };
    const auto parse_if_call_condition = [&](std::string_view line,
                                             std::string* condition) -> bool {
        if (condition == nullptr) {
            return false;
        }

        const std::string_view trimmed = code_line(line);
        if (!StartsWith(trimmed, "if ") || !EndsWith(trimmed, " then")) {
            return false;
        }

        const std::string_view extracted =
            refii::lua::TrimLuaWhitespace(trimmed.substr(3, trimmed.size() - 8));
        if (extracted.empty() || !EndsWith(extracted, ")") ||
            extracted.find('(') == std::string_view::npos) {
            return false;
        }
        if (extracted.find(" and ") != std::string_view::npos ||
            extracted.find(" or ") != std::string_view::npos ||
            extracted.find("==") != std::string_view::npos ||
            extracted.find("~=") != std::string_view::npos ||
            extracted.find('<') != std::string_view::npos ||
            extracted.find('>') != std::string_view::npos) {
            return false;
        }

        *condition = std::string(extracted);
        return true;
    };

    SyntaxRepairCandidateTracker candidate_tracker(validation_error,
                                                   had_trailing_newline,
                                                   &decompiled_source,
                                                   note);

    for (std::size_t if_line = 0; if_line < lines.size(); ++if_line) {
        const std::size_t current_line = if_line + 1;
        if (reported_line != 0 &&
            (current_line + 64 < reported_line || current_line > reported_line + 64)) {
            continue;
        }

        std::string condition;
        if (!parse_if_call_condition(lines[if_line], &condition)) {
            continue;
        }

        const std::size_t first_end_line = next_nonempty(if_line + 1);
        const std::size_t second_end_line =
            first_end_line < lines.size() ? next_nonempty(first_end_line + 1) : lines.size();
        const std::size_t third_end_line =
            second_end_line < lines.size() ? next_nonempty(second_end_line + 1) : lines.size();
        const std::size_t boundary_line =
            third_end_line < lines.size() ? next_nonempty(third_end_line + 1) : lines.size();
        if (first_end_line >= lines.size() || second_end_line >= lines.size() ||
            third_end_line >= lines.size() || boundary_line >= lines.size() ||
            code_line(lines[first_end_line]) != "end" ||
            code_line(lines[second_end_line]) != "end" ||
            code_line(lines[third_end_line]) != "end" ||
            !LooksLikeTopLevelBoundary(trimmed_line(lines[boundary_line]))) {
            continue;
        }

        const std::string if_indent(line_indent(lines[if_line]));
        if (if_indent.empty() || line_indent(lines[second_end_line]).size() > if_indent.size()) {
            continue;
        }

        std::vector<std::string> candidate_lines = lines;
        candidate_lines[if_line] = if_indent + condition;
        candidate_lines.erase(
            candidate_lines.begin() + static_cast<std::ptrdiff_t>(first_end_line),
            candidate_lines.begin() + static_cast<std::ptrdiff_t>(second_end_line + 1));

        if (candidate_tracker.Consider(
                std::move(candidate_lines),
                "syntax_repaired_replaced_empty_if_call_before_top_level_boundary")) {
            return true;
        }
    }

    return candidate_tracker.CommitBestProgress(
        "syntax_repaired_replaced_empty_if_call_before_top_level_boundary_progress");
}

bool TryPromoteNestedStateBranchesOutOfInnerElse(std::string& decompiled_source,
                                                 std::string_view validation_error,
                                                 std::string* note) {
    if (validation_error.find("near 'end'") == std::string_view::npos &&
        validation_error.find("near 'else'") == std::string_view::npos &&
        validation_error.find("near 'elseif'") == std::string_view::npos) {
        return false;
    }

    bool had_trailing_newline = false;
    const std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);
    if (lines.size() < 12) {
        return false;
    }

    const std::size_t reported_line = ExtractLuaErrorLine(validation_error);
    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimWhitespace(refii::lua::StripLuaLineComment(line));
    };
    const auto line_indent = [](std::string_view line) -> std::string_view {
        const std::size_t first_non_ws = line.find_first_not_of(" \t");
        if (first_non_ws == std::string_view::npos) {
            return line;
        }
        return line.substr(0, first_non_ws);
    };
    const auto next_nonempty = [&](std::size_t start) -> std::size_t {
        for (std::size_t i = start; i < lines.size(); ++i) {
            if (!trim_line(lines[i]).empty()) {
                return i;
            }
        }
        return lines.size();
    };
    const auto previous_nonempty = [&](std::size_t start) -> std::size_t {
        if (start == 0 || start > lines.size()) {
            return lines.size();
        }
        for (std::size_t i = start; i-- > 0;) {
            if (!trim_line(lines[i]).empty()) {
                return i;
            }
            if (i == 0) {
                break;
            }
        }
        return lines.size();
    };
    const auto skip_optional_do_wrapper = [&](std::size_t start) -> std::size_t {
        std::size_t probe = next_nonempty(start);
        if (probe < lines.size() && trim_line(lines[probe]) == "do") {
            probe = next_nonempty(probe + 1);
        }
        return probe;
    };
    const auto parse_if_condition = [](std::string_view line, std::string* condition) -> bool {
        if (condition == nullptr) {
            return false;
        }

        const std::string_view trimmed = refii::lua::TrimLuaWhitespace(
            refii::lua::StripLuaLineComment(line));
        if (!StartsWith(trimmed, "if ") || !EndsWith(trimmed, " then")) {
            return false;
        }

        const std::string_view extracted =
            refii::lua::TrimLuaWhitespace(trimmed.substr(3, trimmed.size() - 8));
        if (extracted.empty()) {
            return false;
        }

        *condition = std::string(extracted);
        return true;
    };
    const auto parse_if_or_elseif_condition =
        [&](std::string_view line, std::string* condition) -> bool {
        if (condition == nullptr) {
            return false;
        }

        const std::string_view trimmed = refii::lua::TrimLuaWhitespace(
            refii::lua::StripLuaLineComment(line));
        if (StartsWith(trimmed, "if ")) {
            return parse_if_condition(line, condition);
        }
        if (!StartsWith(trimmed, "elseif ") || !EndsWith(trimmed, " then")) {
            return false;
        }

        const std::string_view extracted =
            refii::lua::TrimLuaWhitespace(trimmed.substr(7, trimmed.size() - 12));
        if (extracted.empty()) {
            return false;
        }

        *condition = std::string(extracted);
        return true;
    };
    const auto extract_state_lhs = [](std::string_view condition, std::string* lhs) -> bool {
        if (lhs == nullptr) {
            return false;
        }

        const std::size_t equals = condition.find("==");
        if (equals == std::string_view::npos) {
            return false;
        }

        const std::string_view extracted =
            refii::lua::TrimLuaWhitespace(condition.substr(0, equals));
        if (extracted.empty()) {
            return false;
        }
        if (extracted.find("State") == std::string_view::npos &&
            extracted.find("ThreadVar") == std::string_view::npos) {
            return false;
        }

        *lhs = std::string(extracted);
        return true;
    };
    const auto split_state_condition_tail =
        [](std::string_view condition,
           std::string_view lhs,
           std::string* state_test,
           std::string* tail) -> bool {
        if (state_test == nullptr || tail == nullptr) {
            return false;
        }

        const std::string_view trimmed = refii::lua::TrimLuaWhitespace(condition);
        if (!StartsWith(trimmed, lhs)) {
            return false;
        }

        const std::size_t and_pos = trimmed.find(" and ");
        if (and_pos == std::string_view::npos) {
            return false;
        }

        const std::string_view head = refii::lua::TrimLuaWhitespace(trimmed.substr(0, and_pos));
        const std::string_view rest =
            refii::lua::TrimLuaWhitespace(trimmed.substr(and_pos + 5));
        if (head.empty() || rest.empty()) {
            return false;
        }

        *state_test = std::string(head);
        *tail = std::string(rest);
        return true;
    };

    struct StateBranchCandidate {
        std::size_t marker_line = 0;
        std::size_t header_line = 0;
        std::size_t body_start = 0;
        std::size_t body_end_exclusive = 0;
        bool marker_is_else = false;
        std::string condition;
    };

    SyntaxRepairCandidateTracker candidate_tracker(validation_error,
                                                   had_trailing_newline,
                                                   &decompiled_source,
                                                   note);
    const auto consider_candidate = [&](std::string candidate,
                                        std::string_view candidate_note) -> bool {
        return candidate_tracker.ConsiderSource(std::move(candidate), candidate_note);
    };

    for (std::size_t yield_line = 0; yield_line < lines.size(); ++yield_line) {
        if (trim_line(lines[yield_line]) != "coroutine.yield()") {
            continue;
        }
        if (reported_line != 0) {
            const std::size_t current_line = yield_line + 1;
            if (current_line + 96 < reported_line || current_line > reported_line + 32) {
                continue;
            }
        }

        const std::size_t state_block_end = previous_nonempty(yield_line);
        if (state_block_end >= lines.size() || state_block_end <= 1) {
            continue;
        }

        for (std::size_t outer_state_if_line = state_block_end + 1; outer_state_if_line-- > 0;) {
            if (reported_line != 0) {
                const std::size_t current_line = outer_state_if_line + 1;
                if (current_line + 128 < reported_line || current_line > reported_line) {
                    if (outer_state_if_line == 0) {
                        break;
                    }
                    continue;
                }
            }

            std::string outer_state_condition;
            std::string state_lhs;
            if (!parse_if_or_elseif_condition(lines[outer_state_if_line], &outer_state_condition) ||
                !extract_state_lhs(outer_state_condition, &state_lhs)) {
                if (outer_state_if_line == 0) {
                    break;
                }
                continue;
            }

            const std::string outer_state_indent(line_indent(lines[outer_state_if_line]));
            std::size_t inner_if_line = lines.size();
            std::size_t inner_else_line = lines.size();
            for (std::size_t probe = outer_state_if_line + 1;
                 probe < state_block_end && probe <= outer_state_if_line + 24;
                 ++probe) {
                std::string probe_condition;
                std::string probe_lhs;
                if (!parse_if_condition(lines[probe], &probe_condition) ||
                    extract_state_lhs(probe_condition, &probe_lhs)) {
                    continue;
                }

                const std::string probe_indent(line_indent(lines[probe]));
                if (probe_indent.size() != outer_state_indent.size() + 2 ||
                    !StartsWith(probe_indent, outer_state_indent)) {
                    continue;
                }

                for (std::size_t else_probe = probe + 1;
                     else_probe <= state_block_end && else_probe <= probe + 24;
                     ++else_probe) {
                    if (trim_line(lines[else_probe]) == "else" &&
                        line_indent(lines[else_probe]) == probe_indent) {
                        inner_if_line = probe;
                        inner_else_line = else_probe;
                        break;
                    }
                }
                if (inner_else_line < lines.size()) {
                    break;
                }
            }
            if (inner_else_line >= lines.size()) {
                if (outer_state_if_line == 0) {
                    break;
                }
                continue;
            }

            const std::size_t first_branch_header =
                skip_optional_do_wrapper(inner_else_line + 1);
            std::string first_branch_condition;
            std::string first_branch_lhs;
            if (first_branch_header >= lines.size() ||
                !parse_if_condition(lines[first_branch_header], &first_branch_condition) ||
                !extract_state_lhs(first_branch_condition, &first_branch_lhs) ||
                first_branch_lhs != state_lhs) {
                if (outer_state_if_line == 0) {
                    break;
                }
                continue;
            }

            const std::string first_branch_indent(line_indent(lines[first_branch_header]));
            if (first_branch_indent.size() <= outer_state_indent.size() ||
                !StartsWith(first_branch_indent, outer_state_indent)) {
                if (outer_state_if_line == 0) {
                    break;
                }
                continue;
            }

            std::vector<StateBranchCandidate> branches;
            StateBranchCandidate current_branch;
            current_branch.marker_line = first_branch_header;
            current_branch.header_line = first_branch_header;
            current_branch.body_start = first_branch_header + 1;
            current_branch.condition = first_branch_condition;

            for (std::size_t probe = first_branch_header + 1; probe <= state_block_end; ++probe) {
                if (trim_line(lines[probe]) == "elseif" ||
                    trim_line(lines[probe]) == "if") {
                    continue;
                }

                std::string probe_condition;
                std::string probe_lhs;
                const std::string probe_indent(line_indent(lines[probe]));
                const bool matches_branch_indent =
                    probe_indent == first_branch_indent || probe_indent == outer_state_indent;
                if (matches_branch_indent &&
                    parse_if_or_elseif_condition(lines[probe], &probe_condition) &&
                    extract_state_lhs(probe_condition, &probe_lhs) &&
                    probe_lhs == state_lhs) {
                    current_branch.body_end_exclusive = probe;
                    branches.push_back(current_branch);

                    current_branch = {};
                    current_branch.marker_line = probe;
                    current_branch.header_line = probe;
                    current_branch.body_start = probe + 1;
                    current_branch.condition = std::move(probe_condition);
                    continue;
                }

                if (trim_line(lines[probe]) != "else") {
                    continue;
                }

                const std::size_t paired_if_line = skip_optional_do_wrapper(probe + 1);
                if (paired_if_line >= lines.size()) {
                    continue;
                }

                std::string paired_condition;
                std::string paired_lhs;
                if (!parse_if_condition(lines[paired_if_line], &paired_condition) ||
                    !extract_state_lhs(paired_condition, &paired_lhs) ||
                    paired_lhs != state_lhs) {
                    continue;
                }

                current_branch.body_end_exclusive = probe;
                branches.push_back(current_branch);

                current_branch = {};
                current_branch.marker_line = probe;
                current_branch.header_line = paired_if_line;
                current_branch.body_start = paired_if_line + 1;
                current_branch.marker_is_else = true;
                current_branch.condition = std::move(paired_condition);
            }

            if (current_branch.body_start == 0) {
                if (outer_state_if_line == 0) {
                    break;
                }
                continue;
            }
            current_branch.body_end_exclusive = state_block_end + 1;
            while (current_branch.body_end_exclusive > current_branch.body_start) {
                const std::size_t trailing =
                    previous_nonempty(current_branch.body_end_exclusive);
                if (trailing >= lines.size() || trailing < current_branch.body_start ||
                    trim_line(lines[trailing]) != "end") {
                    break;
                }
                current_branch.body_end_exclusive = trailing;
            }
            branches.push_back(current_branch);

            if (branches.size() < 2) {
                if (outer_state_if_line == 0) {
                    break;
                }
                continue;
            }

            const std::string inner_if_indent(line_indent(lines[inner_if_line]));
            std::vector<std::string> candidate_lines;
            candidate_lines.reserve(lines.size() + 8);
            candidate_lines.insert(candidate_lines.end(),
                                   lines.begin(),
                                   lines.begin() + static_cast<std::ptrdiff_t>(outer_state_if_line));
            candidate_lines.insert(candidate_lines.end(),
                                   lines.begin() + static_cast<std::ptrdiff_t>(outer_state_if_line),
                                   lines.begin() + static_cast<std::ptrdiff_t>(inner_else_line));
            candidate_lines.push_back(inner_if_indent + "end");

            for (std::size_t branch_index = 0; branch_index < branches.size(); ++branch_index) {
                const StateBranchCandidate& branch = branches[branch_index];
                std::string branch_condition = branch.condition;
                std::string state_test;
                std::string condition_tail;
                const bool split_first_branch =
                    branch_index == 0 &&
                    split_state_condition_tail(branch_condition,
                                               state_lhs,
                                               &state_test,
                                               &condition_tail);

                candidate_lines.push_back(
                    outer_state_indent + "elseif " +
                    std::string(split_first_branch ? state_test : branch_condition) + " then");
                if (split_first_branch) {
                    candidate_lines.push_back(first_branch_indent + "if " + condition_tail + " then");
                }

                if (branch.body_start < branch.body_end_exclusive &&
                    branch.body_end_exclusive <= lines.size()) {
                    candidate_lines.insert(
                        candidate_lines.end(),
                        lines.begin() + static_cast<std::ptrdiff_t>(branch.body_start),
                        lines.begin() + static_cast<std::ptrdiff_t>(branch.body_end_exclusive));
                }

                if (split_first_branch) {
                    candidate_lines.push_back(first_branch_indent + "end");
                }
            }
            candidate_lines.push_back(outer_state_indent + "end");
            candidate_lines.insert(candidate_lines.end(),
                                   lines.begin() + static_cast<std::ptrdiff_t>(state_block_end + 1),
                                   lines.end());

            if (consider_candidate(JoinLines(candidate_lines, had_trailing_newline),
                                   "syntax_repaired_promoted_nested_state_branches")) {
                return true;
            }

            if (outer_state_if_line == 0) {
                break;
            }
        }
    }

    return candidate_tracker.CommitBestProgress(
        "syntax_repaired_promoted_nested_state_branches_progress");
}

bool TryPromoteStateElseDoWrapperToElseIfChain(std::string& decompiled_source,
                                               std::string_view validation_error,
                                               std::string* note) {
    if (validation_error.find("near 'elseif'") == std::string_view::npos) {
        return false;
    }

    bool had_trailing_newline = false;
    const std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);
    if (lines.size() < 12) {
        return false;
    }

    const std::size_t reported_line = ExtractLuaErrorLine(validation_error);
    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimWhitespace(refii::lua::StripLuaLineComment(line));
    };
    const auto line_indent = [](std::string_view line) -> std::string_view {
        const std::size_t first_non_ws = line.find_first_not_of(" \t");
        if (first_non_ws == std::string_view::npos) {
            return line;
        }
        return line.substr(0, first_non_ws);
    };
    const auto next_nonempty = [&](std::size_t start) -> std::size_t {
        for (std::size_t i = start; i < lines.size(); ++i) {
            if (!trim_line(lines[i]).empty()) {
                return i;
            }
        }
        return lines.size();
    };
    const auto previous_nonempty = [&](std::size_t start) -> std::size_t {
        if (start == 0 || start > lines.size()) {
            return lines.size();
        }
        for (std::size_t i = start; i-- > 0;) {
            if (!trim_line(lines[i]).empty()) {
                return i;
            }
            if (i == 0) {
                break;
            }
        }
        return lines.size();
    };
    const auto parse_if_condition = [](std::string_view line, std::string* condition) -> bool {
        if (condition == nullptr) {
            return false;
        }

        const std::string_view trimmed = refii::lua::TrimLuaWhitespace(
            refii::lua::StripLuaLineComment(line));
        if (!StartsWith(trimmed, "if ") || !EndsWith(trimmed, " then")) {
            return false;
        }

        const std::string_view extracted =
            refii::lua::TrimLuaWhitespace(trimmed.substr(3, trimmed.size() - 8));
        if (extracted.empty()) {
            return false;
        }

        *condition = std::string(extracted);
        return true;
    };
    const auto parse_if_or_elseif_condition =
        [&](std::string_view line, std::string* condition) -> bool {
        if (condition == nullptr) {
            return false;
        }

        const std::string_view trimmed = refii::lua::TrimLuaWhitespace(
            refii::lua::StripLuaLineComment(line));
        if (StartsWith(trimmed, "if ")) {
            return parse_if_condition(line, condition);
        }
        if (!StartsWith(trimmed, "elseif ") || !EndsWith(trimmed, " then")) {
            return false;
        }

        const std::string_view extracted =
            refii::lua::TrimLuaWhitespace(trimmed.substr(7, trimmed.size() - 12));
        if (extracted.empty()) {
            return false;
        }

        *condition = std::string(extracted);
        return true;
    };
    const auto extract_state_lhs = [](std::string_view condition, std::string* lhs) -> bool {
        if (lhs == nullptr) {
            return false;
        }

        const std::size_t equals = condition.find("==");
        if (equals == std::string_view::npos) {
            return false;
        }

        const std::string_view extracted =
            refii::lua::TrimLuaWhitespace(condition.substr(0, equals));
        if (extracted.empty()) {
            return false;
        }
        if (extracted.find("State") == std::string_view::npos &&
            extracted.find("ThreadVar") == std::string_view::npos) {
            return false;
        }

        *lhs = std::string(extracted);
        return true;
    };
    const auto split_state_condition_tail =
        [](std::string_view condition,
           std::string_view lhs,
           std::string* state_test,
           std::string* tail) -> bool {
        if (state_test == nullptr || tail == nullptr) {
            return false;
        }

        const std::string_view trimmed = refii::lua::TrimLuaWhitespace(condition);
        if (!StartsWith(trimmed, lhs)) {
            return false;
        }

        const std::size_t and_pos = trimmed.find(" and ");
        if (and_pos == std::string_view::npos) {
            return false;
        }

        const std::string_view head = refii::lua::TrimLuaWhitespace(trimmed.substr(0, and_pos));
        const std::string_view rest =
            refii::lua::TrimLuaWhitespace(trimmed.substr(and_pos + 5));
        if (head.empty() || rest.empty()) {
            return false;
        }

        *state_test = std::string(head);
        *tail = std::string(rest);
        return true;
    };
    const auto block_delta = [&](std::string_view line) -> int {
        const std::string_view trimmed = trim_line(line);
        if (trimmed.empty()) {
            return 0;
        }
        if (trimmed == "else" || StartsWith(trimmed, "elseif ")) {
            return 0;
        }
        if (trimmed == "repeat" || trimmed == "do") {
            return 1;
        }
        if (trimmed == "end" || StartsWith(trimmed, "until ")) {
            return -1;
        }
        if (StartsWith(trimmed, "if ") || StartsWith(trimmed, "for ") ||
            StartsWith(trimmed, "while ") || StartsWith(trimmed, "function ") ||
            StartsWith(trimmed, "local function ") ||
            StartsWith(trimmed, "return function(") ||
            StartsWith(trimmed, "return function ") ||
            trimmed.find("= function(") != std::string_view::npos ||
            trimmed.find("= function ") != std::string_view::npos) {
            return 1;
        }
        return 0;
    };

    SyntaxRepairCandidateTracker candidate_tracker(validation_error,
                                                   had_trailing_newline,
                                                   &decompiled_source,
                                                   note);

    for (std::size_t elseif_line = 0; elseif_line < lines.size(); ++elseif_line) {
        const std::size_t current_line = elseif_line + 1;
        if (reported_line != 0 &&
            (current_line + 128 < reported_line || current_line > reported_line + 16)) {
            continue;
        }

        std::string elseif_condition;
        std::string state_lhs;
        if (!parse_if_or_elseif_condition(lines[elseif_line], &elseif_condition) ||
            !extract_state_lhs(elseif_condition, &state_lhs)) {
            continue;
        }

        const std::string outer_indent(line_indent(lines[elseif_line]));
        const std::size_t previous_line = previous_nonempty(elseif_line);
        if (previous_line >= lines.size() || trim_line(lines[previous_line]) != "end") {
            continue;
        }

        std::size_t first_if_line = lines.size();
        std::string first_if_condition;
        std::string state_test;
        std::string condition_tail;
        for (std::size_t probe = elseif_line; probe-- > 0 && elseif_line - probe <= 32;) {
            std::string probe_condition;
            std::string probe_lhs;
            if (!parse_if_condition(lines[probe], &probe_condition) ||
                !extract_state_lhs(probe_condition, &probe_lhs) || probe_lhs != state_lhs ||
                !split_state_condition_tail(probe_condition,
                                           state_lhs,
                                           &state_test,
                                           &condition_tail)) {
                if (probe == 0) {
                    break;
                }
                continue;
            }

            const std::string probe_indent(line_indent(lines[probe]));
            if (probe_indent.size() <= outer_indent.size() ||
                !StartsWith(probe_indent, outer_indent)) {
                if (probe == 0) {
                    break;
                }
                continue;
            }

            first_if_line = probe;
            first_if_condition = std::move(probe_condition);
            break;
        }
        if (first_if_line >= lines.size()) {
            continue;
        }

        std::size_t else_line = lines.size();
        for (std::size_t probe = first_if_line; probe-- > 0 && first_if_line - probe <= 12;) {
            if (trim_line(lines[probe]) == "else" && line_indent(lines[probe]) == outer_indent) {
                else_line = probe;
                break;
            }
            if (probe == 0) {
                break;
            }
        }
        if (else_line >= lines.size()) {
            continue;
        }

        const std::size_t after_else_line = next_nonempty(else_line + 1);
        std::size_t do_line = lines.size();
        if (after_else_line < lines.size() && trim_line(lines[after_else_line]) == "do") {
            do_line = after_else_line;
        }

        const std::size_t expected_first_if_line =
            do_line < lines.size() ? next_nonempty(do_line + 1) : after_else_line;
        if (expected_first_if_line != first_if_line) {
            continue;
        }

        std::size_t do_end_line = lines.size();
        if (do_line < lines.size()) {
            int balance = 1;
            for (std::size_t probe = do_line + 1; probe < lines.size() && probe <= elseif_line + 32;
                 ++probe) {
                balance += block_delta(lines[probe]);
                if (balance == 0) {
                    do_end_line = probe;
                    break;
                }
            }
            if (do_end_line >= lines.size()) {
                continue;
            }
        }

        std::vector<std::string> candidate_lines = lines;
        candidate_lines[else_line] = outer_indent + "elseif " + state_test + " then";
        candidate_lines[first_if_line] =
            std::string(line_indent(lines[first_if_line])) + "if " + condition_tail + " then";
        candidate_lines[elseif_line] = outer_indent + "elseif " + elseif_condition + " then";

        if (do_end_line < candidate_lines.size()) {
            candidate_lines.erase(candidate_lines.begin() +
                                  static_cast<std::ptrdiff_t>(do_end_line));
        }
        if (do_line < candidate_lines.size()) {
            candidate_lines.erase(candidate_lines.begin() +
                                  static_cast<std::ptrdiff_t>(do_line));
        }

        if (candidate_tracker.Consider(
                std::move(candidate_lines),
                "syntax_repaired_promoted_state_else_do_wrapper_to_elseif_chain")) {
            return true;
        }
    }

    return candidate_tracker.CommitBestProgress(
        "syntax_repaired_promoted_state_else_do_wrapper_to_elseif_chain_progress");
}

bool TryNormalizeTerminalGuardedStateFlagLoops(std::string& decompiled_source,
                                               std::string_view validation_error,
                                               std::string* note) {
    if (validation_error.find("'<eof>' expected") == std::string_view::npos &&
        validation_error.find("near 'end'") == std::string_view::npos) {
        return false;
    }

    bool had_trailing_newline = false;
    const std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);
    if (lines.size() < 12) {
        return false;
    }

    const std::size_t reported_line = ExtractLuaErrorLine(validation_error);
    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimWhitespace(refii::lua::StripLuaLineComment(line));
    };
    const auto line_indent = [](std::string_view line) -> std::string_view {
        const std::size_t first_non_ws = line.find_first_not_of(" \t");
        if (first_non_ws == std::string_view::npos) {
            return line;
        }
        return line.substr(0, first_non_ws);
    };
    const auto next_nonempty = [&](std::size_t start) -> std::size_t {
        for (std::size_t i = start; i < lines.size(); ++i) {
            if (!trim_line(lines[i]).empty()) {
                return i;
            }
        }
        return lines.size();
    };
    const auto previous_nonempty = [&](std::size_t start) -> std::size_t {
        if (start == 0 || start > lines.size()) {
            return lines.size();
        }
        for (std::size_t i = start; i-- > 0;) {
            if (!trim_line(lines[i]).empty()) {
                return i;
            }
            if (i == 0) {
                break;
            }
        }
        return lines.size();
    };
    const auto parse_if_condition = [](std::string_view line, std::string* condition) -> bool {
        if (condition == nullptr) {
            return false;
        }

        const std::string_view trimmed = refii::lua::TrimLuaWhitespace(
            refii::lua::StripLuaLineComment(line));
        if (!StartsWith(trimmed, "if ") || !EndsWith(trimmed, " then")) {
            return false;
        }

        const std::string_view extracted =
            refii::lua::TrimLuaWhitespace(trimmed.substr(3, trimmed.size() - 8));
        if (extracted.empty()) {
            return false;
        }

        *condition = std::string(extracted);
        return true;
    };
    const auto parse_elseif_condition = [](std::string_view line, std::string* condition) -> bool {
        if (condition == nullptr) {
            return false;
        }

        const std::string_view trimmed = refii::lua::TrimLuaWhitespace(
            refii::lua::StripLuaLineComment(line));
        if (!StartsWith(trimmed, "elseif ") || !EndsWith(trimmed, " then")) {
            return false;
        }

        const std::string_view extracted =
            refii::lua::TrimLuaWhitespace(trimmed.substr(7, trimmed.size() - 12));
        if (extracted.empty()) {
            return false;
        }

        *condition = std::string(extracted);
        return true;
    };
    const auto split_on_and = [](std::string_view condition) {
        std::vector<std::string> parts;
        std::size_t start = 0;
        while (start < condition.size()) {
            const std::size_t and_pos = condition.find(" and ", start);
            const std::size_t end =
                and_pos == std::string_view::npos ? condition.size() : and_pos;
            const std::string_view piece =
                refii::lua::TrimLuaWhitespace(condition.substr(start, end - start));
            if (!piece.empty()) {
                parts.emplace_back(piece);
            }
            if (and_pos == std::string_view::npos) {
                break;
            }
            start = and_pos + 5;
        }
        return parts;
    };
    const auto join_parts = [](const std::vector<std::string>& parts,
                               std::size_t begin,
                               std::size_t end) -> std::string {
        std::string joined;
        for (std::size_t i = begin; i < end; ++i) {
            if (!joined.empty()) {
                joined += " and ";
            }
            joined += parts[i];
        }
        return joined;
    };
    const auto append_reindented_range =
        [&](std::size_t start,
            std::size_t end_exclusive,
            std::string_view source_base,
            std::string_view target_base,
            std::vector<std::string>* out_lines) {
            if (out_lines == nullptr || start >= end_exclusive || end_exclusive > lines.size()) {
                return;
            }

            for (std::size_t i = start; i < end_exclusive; ++i) {
                const std::string_view trimmed = trim_line(lines[i]);
                if (trimmed.empty()) {
                    out_lines->push_back(std::string(lines[i]));
                    continue;
                }

                const std::string_view current_indent = line_indent(lines[i]);
                if (current_indent.starts_with(source_base)) {
                    out_lines->push_back(std::string(target_base) +
                                         std::string(current_indent.substr(source_base.size())) +
                                         std::string(lines[i].substr(current_indent.size())));
                } else {
                    out_lines->push_back(std::string(target_base) +
                                         std::string(trimmed));
                }
            }
        };

    SyntaxRepairCandidateTracker candidate_tracker(validation_error,
                                                   had_trailing_newline,
                                                   &decompiled_source,
                                                   note);

    for (std::size_t while_line = 0; while_line < lines.size(); ++while_line) {
        if (trim_line(lines[while_line]) != "while true do") {
            continue;
        }

        if (reported_line != 0) {
            const std::size_t current_line = while_line + 1;
            if (current_line + 128 < reported_line || current_line > reported_line) {
                continue;
            }
        }

        const std::string while_indent(line_indent(lines[while_line]));
        const std::string guard_indent = while_indent + "  ";
        const std::string state_indent = while_indent + "  ";
        const std::string condition_indent = while_indent + "    ";
        const std::string not_flag_indent = while_indent + "      ";

        std::string guard_condition;
        const std::size_t guard_if_line = next_nonempty(while_line + 1);
        if (guard_if_line >= lines.size() ||
            line_indent(lines[guard_if_line]) != guard_indent ||
            !parse_if_condition(lines[guard_if_line], &guard_condition) ||
            !StartsWith(refii::lua::TrimLuaWhitespace(guard_condition), "not ")) {
            continue;
        }

        std::string flattened_condition;
        const std::size_t flattened_if_line = next_nonempty(guard_if_line + 1);
        if (flattened_if_line >= lines.size() ||
            line_indent(lines[flattened_if_line]) != condition_indent ||
            !parse_if_condition(lines[flattened_if_line], &flattened_condition)) {
            continue;
        }

        const std::vector<std::string> condition_parts = split_on_and(flattened_condition);
        if (condition_parts.size() < 3) {
            continue;
        }

        const std::string& state_condition = condition_parts.front();
        if (state_condition.find("State") == std::string::npos &&
            state_condition.find("ThreadVar") == std::string::npos) {
            continue;
        }

        const std::string& trailing_part = condition_parts.back();
        if (!StartsWith(refii::lua::TrimLuaWhitespace(trailing_part), "not ")) {
            continue;
        }

        const std::string flag_condition = std::string(
            refii::lua::TrimLuaWhitespace(trailing_part.substr(4)));
        if (flag_condition.empty()) {
            continue;
        }

        const std::string nested_condition =
            join_parts(condition_parts, 1, condition_parts.size() - 1);
        if (nested_condition.empty()) {
            continue;
        }

        std::size_t elseif_line = lines.size();
        std::string elseif_condition;
        for (std::size_t probe = flattened_if_line + 1;
             probe < lines.size() && probe <= flattened_if_line + 24;
             ++probe) {
            if (line_indent(lines[probe]) != condition_indent) {
                continue;
            }
            if (parse_elseif_condition(lines[probe], &elseif_condition)) {
                elseif_line = probe;
                break;
            }
        }
        if (elseif_line >= lines.size() || elseif_condition != flag_condition) {
            continue;
        }

        const std::size_t then_body_start = next_nonempty(flattened_if_line + 1);
        std::size_t then_body_end_exclusive = elseif_line;
        while (then_body_end_exclusive > then_body_start) {
            const std::size_t trailing = previous_nonempty(then_body_end_exclusive);
            if (trailing >= lines.size() || trailing < then_body_start ||
                trim_line(lines[trailing]) != "end") {
                break;
            }
            then_body_end_exclusive = trailing;
        }
        if (then_body_start >= then_body_end_exclusive) {
            continue;
        }

        std::size_t flattened_end_line = lines.size();
        for (std::size_t probe = elseif_line + 1;
             probe < lines.size() && probe <= elseif_line + 24;
             ++probe) {
            if (trim_line(lines[probe]) == "end" &&
                line_indent(lines[probe]) == condition_indent) {
                flattened_end_line = probe;
                break;
            }
        }
        if (flattened_end_line >= lines.size()) {
            continue;
        }

        const std::size_t else_body_start = next_nonempty(elseif_line + 1);
        std::size_t else_body_end_exclusive = flattened_end_line;
        while (else_body_end_exclusive > else_body_start) {
            const std::size_t trailing = previous_nonempty(else_body_end_exclusive);
            if (trailing >= lines.size() || trailing < else_body_start ||
                trim_line(lines[trailing]) != "end") {
                break;
            }
            else_body_end_exclusive = trailing;
        }
        if (else_body_start >= else_body_end_exclusive) {
            continue;
        }

        const std::size_t yield_line = next_nonempty(flattened_end_line + 1);
        if (yield_line >= lines.size() || trim_line(lines[yield_line]) != "coroutine.yield()") {
            continue;
        }

        std::size_t boundary_line = lines.size();
        for (std::size_t probe = yield_line + 1; probe < lines.size(); ++probe) {
            if (lines[probe].find_first_not_of(" \t") != 0) {
                continue;
            }
            if (LooksLikeTopLevelBoundary(trim_line(lines[probe]))) {
                boundary_line = probe;
                break;
            }
        }

        const std::size_t function_end_line =
            previous_nonempty(boundary_line == lines.size() ? lines.size() : boundary_line);
        if (function_end_line >= lines.size() || function_end_line <= yield_line ||
            trim_line(lines[function_end_line]) != "end" ||
            !line_indent(lines[function_end_line]).empty()) {
            continue;
        }

        bool trailing_tail_is_only_ends = true;
        for (std::size_t probe = yield_line + 1; probe < function_end_line; ++probe) {
            const std::string_view probe_trimmed = trim_line(lines[probe]);
            if (probe_trimmed.empty()) {
                continue;
            }
            if (probe_trimmed != "end") {
                trailing_tail_is_only_ends = false;
                break;
            }
        }
        if (!trailing_tail_is_only_ends) {
            continue;
        }

        const std::size_t then_body_first = next_nonempty(then_body_start);
        const std::size_t else_body_first = next_nonempty(else_body_start);
        if (then_body_first >= then_body_end_exclusive ||
            else_body_first >= else_body_end_exclusive) {
            continue;
        }

        const std::string then_body_base(line_indent(lines[then_body_first]));
        const std::string else_body_base(line_indent(lines[else_body_first]));
        if (then_body_base.empty() || else_body_base.empty()) {
            continue;
        }

        std::vector<std::string> candidate_lines;
        candidate_lines.reserve(lines.size() + 8);
        candidate_lines.insert(candidate_lines.end(),
                               lines.begin(),
                               lines.begin() + static_cast<std::ptrdiff_t>(while_line));
        candidate_lines.push_back(while_indent + "while " + guard_condition + " do");
        candidate_lines.push_back(state_indent + "if " + state_condition + " then");
        candidate_lines.push_back(condition_indent + "if " + nested_condition + " then");
        candidate_lines.push_back(not_flag_indent + "if not " + flag_condition + " then");
        append_reindented_range(then_body_start,
                                then_body_end_exclusive,
                                then_body_base,
                                not_flag_indent + "  ",
                                &candidate_lines);
        candidate_lines.push_back(not_flag_indent + "end");
        candidate_lines.push_back(condition_indent + "elseif " + flag_condition + " then");
        append_reindented_range(else_body_start,
                                else_body_end_exclusive,
                                else_body_base,
                                condition_indent + "  ",
                                &candidate_lines);
        candidate_lines.push_back(condition_indent + "end");
        candidate_lines.push_back(state_indent + "end");
        candidate_lines.push_back(state_indent + "coroutine.yield()");
        candidate_lines.push_back(while_indent + "end");
        candidate_lines.insert(candidate_lines.end(),
                               lines.begin() + static_cast<std::ptrdiff_t>(function_end_line),
                               lines.end());

        if (candidate_tracker.Consider(
                std::move(candidate_lines),
                "syntax_repaired_normalized_terminal_guarded_state_loop")) {
            return true;
        }
    }

    return candidate_tracker.CommitBestProgress(
        "syntax_repaired_normalized_terminal_guarded_state_loop_progress");
}

bool TryRestoreSmallHelperFooterBeforeTopLevelBoundary(std::string& decompiled_source,
                                                       std::string_view validation_error,
                                                       std::string* note) {
    if (validation_error.find("'end' expected") == std::string_view::npos &&
        validation_error.find("'<eof>' expected") == std::string_view::npos &&
        validation_error.find("unexpected symbol near") == std::string_view::npos &&
        validation_error.find("expected near") == std::string_view::npos) {
        return false;
    }

    bool had_trailing_newline = false;
    const std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);
    if (lines.size() < 8) {
        return false;
    }

    const SyntaxErrorDescriptor baseline_descriptor = DescribeSyntaxError(validation_error);
    const std::size_t reported_line = ExtractLuaErrorLine(validation_error);
    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimWhitespace(refii::lua::StripLuaLineComment(line));
    };
    const auto line_indent = [](std::string_view line) -> std::string_view {
        const std::size_t first_non_ws = line.find_first_not_of(" \t");
        if (first_non_ws == std::string_view::npos) {
            return line;
        }
        return line.substr(0, first_non_ws);
    };
    const auto previous_nonempty = [&](std::size_t start) -> std::size_t {
        if (start == 0 || start > lines.size()) {
            return lines.size();
        }
        for (std::size_t i = start; i-- > 0;) {
            if (!trim_line(lines[i]).empty()) {
                return i;
            }
            if (i == 0) {
                break;
            }
        }
        return lines.size();
    };
    const auto is_top_level_function_start = [&](std::size_t line_index) {
        if (line_index >= lines.size() || !line_indent(lines[line_index]).empty()) {
            return false;
        }
        const std::string_view trimmed = trim_line(lines[line_index]);
        return StartsWith(trimmed, "function ") ||
               StartsWith(trimmed, "local function ") ||
               trimmed.find("= function(") != std::string_view::npos ||
               trimmed.find("= function ") != std::string_view::npos;
    };
    const auto is_generic_for_header = [&](std::size_t line_index) {
        if (line_index >= lines.size()) {
            return false;
        }
        const std::string_view trimmed = trim_line(lines[line_index]);
        return StartsWith(trimmed, "for ") && EndsWith(trimmed, " do") &&
               trimmed.find(" in ") != std::string_view::npos;
    };
    const auto is_if_header = [&](std::size_t line_index) {
        if (line_index >= lines.size()) {
            return false;
        }
        const std::string_view trimmed = trim_line(lines[line_index]);
        return StartsWith(trimmed, "if ") && EndsWith(trimmed, " then");
    };
    const auto is_true_return = [&](std::size_t line_index) {
        if (line_index >= lines.size()) {
            return false;
        }
        const std::string_view trimmed = trim_line(lines[line_index]);
        return StartsWith(trimmed, "return true");
    };

    std::string best_candidate;
    std::string best_error;
    for (std::size_t boundary_line = 1; boundary_line < lines.size(); ++boundary_line) {
        if (lines[boundary_line].find_first_not_of(" \t") != 0) {
            continue;
        }
        if (!LooksLikeTopLevelBoundary(trim_line(lines[boundary_line])) &&
            !is_top_level_function_start(boundary_line)) {
            continue;
        }
        if (reported_line != 0) {
            const std::size_t current_line = boundary_line + 1;
            if (current_line + 8 < reported_line || current_line > reported_line + 8) {
                continue;
            }
        }

        const std::size_t return_line = previous_nonempty(boundary_line);
        const std::size_t if_line = previous_nonempty(return_line);
        const std::size_t for_line = previous_nonempty(if_line);
        if (!is_true_return(return_line) || !is_if_header(if_line) ||
            !is_generic_for_header(for_line)) {
            continue;
        }

        std::size_t function_start = lines.size();
        for (std::size_t probe = for_line + 1; probe-- > 0;) {
            if (is_top_level_function_start(probe)) {
                function_start = probe;
                break;
            }
            if (probe == 0 || probe + 24 < for_line) {
                break;
            }
        }
        if (function_start >= lines.size()) {
            continue;
        }

        const std::string function_indent(line_indent(lines[function_start]));
        const std::string function_body_indent(line_indent(lines[for_line]));
        const std::string if_indent(line_indent(lines[if_line]));
        if (function_body_indent.empty() || if_indent.empty() ||
            !StartsWith(if_indent, function_body_indent)) {
            continue;
        }

        std::vector<std::string> candidate_lines = lines;
        candidate_lines.insert(candidate_lines.begin() +
                                   static_cast<std::ptrdiff_t>(boundary_line),
                               {if_indent + "end",
                                function_body_indent + "end",
                                function_body_indent + "return false",
                                function_indent + "end"});

        std::string candidate = JoinLines(candidate_lines, had_trailing_newline);
        const std::string candidate_error = ValidateLuaRepairSource(candidate);
        if (candidate_error.empty()) {
            decompiled_source = std::move(candidate);
            if (note != nullptr) {
                *note = JoinNotes(
                    *note,
                    "syntax_repaired_restored_small_helper_footer_before_boundary");
            }
            return true;
        }

        const SyntaxErrorDescriptor candidate_descriptor = DescribeSyntaxError(candidate_error);
        if (candidate_descriptor.line <= baseline_descriptor.line ||
            !IsValidationOutcomeBetter(validation_error, candidate_error) ||
            (!best_error.empty() &&
             !IsValidationOutcomeBetter(best_error, candidate_error))) {
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
            "syntax_repaired_restored_small_helper_footer_before_boundary_progress");
    }
    return true;
}

bool TryRestoreBooleanMethodFooterBeforeSiblingAssignment(
    std::string& decompiled_source,
    std::string_view validation_error,
    std::string* note) {
    if (validation_error.find("'=' expected") == std::string_view::npos &&
        validation_error.find("'end' expected") == std::string_view::npos &&
        validation_error.find("'<eof>' expected") == std::string_view::npos &&
        validation_error.find("unexpected symbol near") == std::string_view::npos &&
        validation_error.find("expected near") == std::string_view::npos) {
        return false;
    }

    bool had_trailing_newline = false;
    const std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);
    if (lines.size() < 8) {
        return false;
    }

    const SyntaxErrorDescriptor baseline_descriptor = DescribeSyntaxError(validation_error);
    const std::size_t reported_line = ExtractLuaErrorLine(validation_error);
    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimWhitespace(refii::lua::StripLuaLineComment(line));
    };
    const auto line_indent = [](std::string_view line) -> std::string_view {
        const std::size_t first_non_ws = line.find_first_not_of(" \t");
        if (first_non_ws == std::string_view::npos) {
            return line;
        }
        return line.substr(0, first_non_ws);
    };
    const auto previous_nonempty = [&](std::size_t start) -> std::size_t {
        if (start == 0 || start > lines.size()) {
            return lines.size();
        }
        for (std::size_t i = start; i-- > 0;) {
            if (!trim_line(lines[i]).empty()) {
                return i;
            }
            if (i == 0) {
                break;
            }
        }
        return lines.size();
    };
    const auto parse_top_level_anonymous_assignment =
        [&](std::size_t line_index, std::string* lhs, std::string* owner_prefix) -> bool {
            if (lhs == nullptr || owner_prefix == nullptr || line_index >= lines.size() ||
                lines[line_index].find_first_not_of(" \t") != 0) {
                return false;
            }

            const std::string_view trimmed = trim_line(lines[line_index]);
            const std::size_t equal = trimmed.find('=');
            if (equal == std::string_view::npos) {
                return false;
            }

            const std::string_view parsed_lhs = TrimWhitespace(trimmed.substr(0, equal));
            const std::string_view rhs = TrimWhitespace(trimmed.substr(equal + 1));
            if (parsed_lhs.empty() || !StartsWith(rhs, "function")) {
                return false;
            }

            const std::size_t owner_split = parsed_lhs.rfind('.');
            if (owner_split == std::string_view::npos || owner_split == 0 ||
                owner_split + 1 >= parsed_lhs.size()) {
                return false;
            }

            *lhs = std::string(parsed_lhs);
            *owner_prefix = std::string(parsed_lhs.substr(0, owner_split));
            return true;
        };
    const auto is_if_header = [&](std::size_t line_index) {
        if (line_index >= lines.size()) {
            return false;
        }
        const std::string_view trimmed = trim_line(lines[line_index]);
        return StartsWith(trimmed, "if ") && EndsWith(trimmed, " then");
    };
    const auto is_true_return = [&](std::size_t line_index) {
        if (line_index >= lines.size()) {
            return false;
        }
        return StartsWith(trim_line(lines[line_index]), "return true");
    };

    std::string best_candidate;
    std::string best_error;
    for (std::size_t boundary_line = 1; boundary_line < lines.size(); ++boundary_line) {
        std::string boundary_lhs;
        std::string boundary_owner;
        if (!parse_top_level_anonymous_assignment(boundary_line, &boundary_lhs, &boundary_owner)) {
            continue;
        }
        if (reported_line != 0) {
            const std::size_t current_line = boundary_line + 1;
            if (current_line + 24 < reported_line || current_line > reported_line + 8) {
                continue;
            }
        }

        std::size_t function_start = lines.size();
        std::string function_lhs;
        std::string function_owner;
        for (std::size_t probe = boundary_line; probe-- > 0;) {
            if (parse_top_level_anonymous_assignment(probe, &function_lhs, &function_owner)) {
                function_start = probe;
                break;
            }
            if (probe == 0 || probe + 64 < boundary_line) {
                break;
            }
        }
        if (function_start >= lines.size() || function_owner != boundary_owner ||
            function_lhs == boundary_lhs) {
            continue;
        }

        const std::size_t return_line = previous_nonempty(boundary_line);
        if (!is_true_return(return_line)) {
            continue;
        }

        std::size_t inner_if_line = lines.size();
        for (std::size_t probe = return_line; probe-- > function_start + 1;) {
            if (is_if_header(probe)) {
                inner_if_line = probe;
                break;
            }
            if (probe == 0 || probe + 16 < return_line) {
                break;
            }
        }
        if (inner_if_line >= lines.size()) {
            continue;
        }

        std::size_t outer_if_line = lines.size();
        for (std::size_t probe = inner_if_line; probe-- > function_start;) {
            if (is_if_header(probe)) {
                outer_if_line = probe;
                break;
            }
            if (probe == 0 || probe + 16 < inner_if_line) {
                break;
            }
        }
        if (outer_if_line >= lines.size()) {
            continue;
        }

        bool saw_closing_end = false;
        for (std::size_t probe = inner_if_line + 1; probe < boundary_line; ++probe) {
            if (trim_line(lines[probe]) == "end") {
                saw_closing_end = true;
                break;
            }
        }
        if (saw_closing_end) {
            continue;
        }

        const std::string function_indent(line_indent(lines[function_start]));
        const std::string outer_if_indent(line_indent(lines[outer_if_line]));
        const std::string inner_if_indent(line_indent(lines[inner_if_line]));
        const std::string function_body_indent(line_indent(lines[return_line]));
        if (outer_if_indent.empty() || inner_if_indent.empty() || function_body_indent.empty()) {
            continue;
        }

        std::vector<std::string> candidate_lines = lines;
        candidate_lines.insert(candidate_lines.begin() +
                                   static_cast<std::ptrdiff_t>(boundary_line),
                               {inner_if_indent + "end",
                                outer_if_indent + "end",
                                function_body_indent + "return false",
                                function_indent + "end"});

        std::string candidate = JoinLines(candidate_lines, had_trailing_newline);
        const std::string candidate_error = ValidateLuaRepairSource(candidate);
        if (candidate_error.empty()) {
            decompiled_source = std::move(candidate);
            if (note != nullptr) {
                *note = JoinNotes(
                    *note,
                    "syntax_repaired_restored_boolean_method_footer_before_sibling_assignment");
            }
            return true;
        }

        const SyntaxErrorDescriptor candidate_descriptor = DescribeSyntaxError(candidate_error);
        if (candidate_descriptor.line <= baseline_descriptor.line ||
            !IsValidationOutcomeBetter(validation_error, candidate_error) ||
            (!best_error.empty() &&
             !IsValidationOutcomeBetter(best_error, candidate_error))) {
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
            "syntax_repaired_restored_boolean_method_footer_before_sibling_assignment_progress");
    }
    return true;
}

bool TryRemoveStandaloneIdentifierLineBeforeCall(std::string& decompiled_source,
                                                 std::string_view validation_error,
                                                 std::string* note) {
    if (validation_error.find("'=' expected") == std::string_view::npos &&
        validation_error.find("unexpected symbol") == std::string_view::npos) {
        return false;
    }

    bool had_trailing_newline = false;
    const std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);
    if (lines.size() < 4) {
        return false;
    }

    const SyntaxErrorDescriptor baseline_descriptor = DescribeSyntaxError(validation_error);
    const std::size_t reported_line = ExtractLuaErrorLine(validation_error);
    if (reported_line == 0 || reported_line > lines.size()) {
        return false;
    }

    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimWhitespace(refii::lua::StripLuaLineComment(line));
    };
    const auto line_indent = [](std::string_view line) -> std::string_view {
        const std::size_t first_non_ws = line.find_first_not_of(" \t");
        if (first_non_ws == std::string_view::npos) {
            return line;
        }
        return line.substr(0, first_non_ws);
    };
    const auto previous_nonempty = [&](std::size_t start) -> std::size_t {
        if (start == 0 || start > lines.size()) {
            return lines.size();
        }
        for (std::size_t i = start; i-- > 0;) {
            if (!trim_line(lines[i]).empty()) {
                return i;
            }
            if (i == 0) {
                break;
            }
        }
        return lines.size();
    };

    const std::size_t call_line = reported_line - 1;
    const std::size_t bare_line = previous_nonempty(call_line);
    if (bare_line >= lines.size() || bare_line >= call_line) {
        return false;
    }

    const std::string_view bare_trimmed = trim_line(lines[bare_line]);
    const std::string_view call_trimmed = trim_line(lines[call_line]);
    if (!IsValidLuaIdentifier(bare_trimmed)) {
        return false;
    }
    if (call_trimmed.empty() || call_trimmed.find('(') == std::string_view::npos) {
        return false;
    }

    std::vector<std::string> candidate_lines = lines;
    candidate_lines.erase(candidate_lines.begin() + static_cast<std::ptrdiff_t>(bare_line));

    std::string candidate = JoinLines(candidate_lines, had_trailing_newline);
    const std::string candidate_error = ValidateLuaRepairSource(candidate);
    if (candidate_error.empty()) {
        decompiled_source = std::move(candidate);
        if (note != nullptr) {
            *note = JoinNotes(*note,
                              "syntax_repaired_removed_standalone_identifier_before_call");
        }
        return true;
    }

    const SyntaxErrorDescriptor candidate_descriptor = DescribeSyntaxError(candidate_error);
    if (candidate_descriptor.line <= baseline_descriptor.line ||
        !IsValidationOutcomeBetter(validation_error, candidate_error)) {
        return false;
    }

    decompiled_source = std::move(candidate);
    if (note != nullptr) {
        *note = JoinNotes(*note,
                          "syntax_repaired_removed_standalone_identifier_before_call_progress");
    }
    return true;
}

bool TryInsertEndBeforeTopLevelAnonymousFunctionAssignment(
    std::string& decompiled_source,
    std::string_view validation_error,
    std::string* note) {
    if (validation_error.find("'end' expected") == std::string_view::npos &&
        validation_error.find("unexpected symbol near") == std::string_view::npos &&
        validation_error.find("expected near") == std::string_view::npos) {
        return false;
    }
    if (note != nullptr &&
        (note->find(
             "syntax_repaired_inserted_ends_across_top_level_anonymous_function_method_chain") !=
             std::string::npos)) {
        return false;
    }

    bool had_trailing_newline = false;
    const std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);
    if (lines.size() < 4) {
        return false;
    }

    const auto reported_open_function_is_nested =
        [&](std::string_view raw_error) -> bool {
            constexpr std::string_view kNeedle = "to close 'function' at line ";
            const std::size_t start = raw_error.find(kNeedle);
            if (start == std::string_view::npos) {
                return false;
            }

            std::size_t number_begin = start + kNeedle.size();
            std::size_t number_end = number_begin;
            while (number_end < raw_error.size() &&
                   std::isdigit(static_cast<unsigned char>(raw_error[number_end])) != 0) {
                ++number_end;
            }
            if (number_end == number_begin) {
                return false;
            }

            const std::size_t line_number = static_cast<std::size_t>(
                std::strtoul(std::string(raw_error.substr(number_begin, number_end - number_begin))
                                 .c_str(),
                             nullptr,
                             10));
            if (line_number == 0 || line_number > lines.size()) {
                return false;
            }

            const std::string_view function_line =
                TrimWhitespace(refii::lua::StripLuaLineComment(lines[line_number - 1]));
            if (function_line.find("function(") == std::string_view::npos &&
                function_line.find("function ") == std::string_view::npos) {
                return false;
            }

            const std::size_t indent =
                lines[line_number - 1].find_first_not_of(" \t");
            return indent != std::string::npos && indent != 0;
        };
    if (reported_open_function_is_nested(validation_error)) {
        return false;
    }

    const SyntaxErrorDescriptor baseline_descriptor = DescribeSyntaxError(validation_error);
    const std::size_t reported_line = ExtractLuaErrorLine(validation_error);
    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimWhitespace(refii::lua::StripLuaLineComment(line));
    };
    const auto line_indent = [](std::string_view line) -> std::string_view {
        const std::size_t first_non_ws = line.find_first_not_of(" \t");
        if (first_non_ws == std::string_view::npos) {
            return line;
        }
        return line.substr(0, first_non_ws);
    };
    const auto previous_nonempty = [&](std::size_t start) -> std::size_t {
        if (start == 0 || start > lines.size()) {
            return lines.size();
        }
        for (std::size_t i = start; i-- > 0;) {
            if (!trim_line(lines[i]).empty()) {
                return i;
            }
            if (i == 0) {
                break;
            }
        }
        return lines.size();
    };
    const auto is_top_level_anonymous_function_assignment = [&](std::size_t line_index) {
        return line_index < lines.size() &&
               IsTopLevelAnonymousFunctionBoundaryLine(lines[line_index]);
    };

    std::size_t candidate_boundary_count = 0;
    for (std::size_t boundary_line = 1; boundary_line < lines.size(); ++boundary_line) {
        if (!is_top_level_anonymous_function_assignment(boundary_line)) {
            continue;
        }
        if (reported_line != 0) {
            const std::size_t current_line = boundary_line + 1;
            if (current_line + 128 < reported_line || current_line > reported_line + 8) {
                continue;
            }
        }
        ++candidate_boundary_count;
    }
    if (candidate_boundary_count > 24) {
        return false;
    }

    std::string best_candidate;
    std::string best_error;
    for (std::size_t boundary_line = 1; boundary_line < lines.size(); ++boundary_line) {
        if (!is_top_level_anonymous_function_assignment(boundary_line)) {
            continue;
        }
        if (reported_line != 0) {
            const std::size_t current_line = boundary_line + 1;
            if (current_line + 128 < reported_line || current_line > reported_line + 8) {
                continue;
            }
        }

        const std::size_t previous_line = previous_nonempty(boundary_line);
        if (previous_line >= lines.size()) {
            continue;
        }
        const std::string_view previous_trimmed = trim_line(lines[previous_line]);
        const bool previous_is_top_level_end =
            previous_trimmed == "end" && line_indent(lines[previous_line]).empty();
        if (previous_trimmed.empty() || previous_is_top_level_end ||
            LooksLikeTopLevelBoundary(previous_trimmed)) {
            continue;
        }

        std::vector<std::string> candidate_lines = lines;
        candidate_lines.insert(candidate_lines.begin() +
                                   static_cast<std::ptrdiff_t>(boundary_line),
                               "end");

        std::string candidate = JoinLines(candidate_lines, had_trailing_newline);
        const std::string candidate_error = ValidateLuaRepairSource(candidate);
        if (candidate_error.empty()) {
            decompiled_source = std::move(candidate);
            if (note != nullptr) {
                *note = JoinNotes(
                    *note,
                    "syntax_repaired_inserted_end_before_top_level_anonymous_function");
            }
            return true;
        }

        const SyntaxErrorDescriptor candidate_descriptor = DescribeSyntaxError(candidate_error);
        if (candidate_descriptor.line <= baseline_descriptor.line ||
            !IsValidationOutcomeBetter(validation_error, candidate_error) ||
            (!best_error.empty() &&
             !IsValidationOutcomeBetter(best_error, candidate_error))) {
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
            "syntax_repaired_inserted_end_before_top_level_anonymous_function_progress");
    }
    return true;
}

bool TryCloseReportedFunctionBeforeSiblingBoundary(std::string& decompiled_source,
                                                   std::string_view validation_error,
                                                   std::string* note) {
    constexpr std::string_view kNeedle = "to close 'function' at line ";
    if (validation_error.find("'end' expected") == std::string_view::npos ||
        validation_error.find(kNeedle) == std::string_view::npos) {
        return false;
    }

    bool had_trailing_newline = false;
    const std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);
    if (lines.size() < 4) {
        return false;
    }

    const auto parse_open_function_line = [&](std::string_view raw_error) -> std::size_t {
        const std::size_t start = raw_error.find(kNeedle);
        if (start == std::string_view::npos) {
            return 0;
        }

        std::size_t number_begin = start + kNeedle.size();
        std::size_t number_end = number_begin;
        while (number_end < raw_error.size() &&
               std::isdigit(static_cast<unsigned char>(raw_error[number_end])) != 0) {
            ++number_end;
        }
        if (number_end == number_begin) {
            return 0;
        }

        return static_cast<std::size_t>(
            std::strtoul(std::string(raw_error.substr(number_begin, number_end - number_begin))
                             .c_str(),
                         nullptr,
                         10));
    };

    const std::size_t open_function_line = parse_open_function_line(validation_error);
    if (open_function_line == 0 || open_function_line > lines.size()) {
        return false;
    }

    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimWhitespace(refii::lua::StripLuaLineComment(line));
    };
    const auto raw_trimmed = [&](std::size_t line_index) -> std::string_view {
        return line_index < lines.size() ? TrimWhitespace(lines[line_index]) : std::string_view();
    };
    const auto line_indent = [](std::string_view line) -> std::string_view {
        const std::size_t first_non_ws = line.find_first_not_of(" \t");
        if (first_non_ws == std::string_view::npos) {
            return line;
        }
        return line.substr(0, first_non_ws);
    };
    const auto previous_nonempty = [&](std::size_t start) -> std::size_t {
        if (start == 0 || start > lines.size()) {
            return lines.size();
        }
        for (std::size_t i = start; i-- > 0;) {
            if (!trim_line(lines[i]).empty() ||
                raw_trimmed(i) == "-- refii-suppressed-control: end") {
                return i;
            }
            if (i == 0) {
                break;
            }
        }
        return lines.size();
    };
    const auto suppressed_end_run = [&](std::size_t boundary_line,
                                        std::size_t* run_begin,
                                        std::size_t* run_end) -> std::size_t {
        if (run_begin != nullptr) {
            *run_begin = lines.size();
        }
        if (run_end != nullptr) {
            *run_end = lines.size();
        }
        if (boundary_line == 0 || boundary_line > lines.size()) {
            return 0;
        }

        std::size_t current = boundary_line;
        while (current > 0) {
            const std::size_t probe = previous_nonempty(current);
            if (probe >= lines.size() ||
                raw_trimmed(probe) != "-- refii-suppressed-control: end") {
                break;
            }
            if (run_end != nullptr && *run_end >= lines.size()) {
                *run_end = probe;
            }
            if (run_begin != nullptr) {
                *run_begin = probe;
            }
            current = probe;
            if (probe == 0) {
                break;
            }
        }

        if (run_begin == nullptr || *run_begin >= lines.size() ||
            run_end == nullptr || *run_end >= lines.size() || *run_begin > *run_end) {
            return 0;
        }
        return *run_end - *run_begin + 1;
    };

    const std::size_t open_line_index = open_function_line - 1;
    const std::string_view open_trimmed = trim_line(lines[open_line_index]);
    if (open_trimmed.find("function(") == std::string_view::npos &&
        open_trimmed.find("function ") == std::string_view::npos) {
        return false;
    }

    const std::string open_indent(line_indent(lines[open_line_index]));
    const SyntaxErrorDescriptor baseline_descriptor = DescribeSyntaxError(validation_error);
    std::string best_candidate;
    std::string best_error;

    for (std::size_t boundary_line = open_line_index + 1;
         boundary_line < lines.size() && boundary_line <= open_line_index + 256;
         ++boundary_line) {
        const std::string boundary_indent(line_indent(lines[boundary_line]));
        if (boundary_indent.size() > open_indent.size()) {
            continue;
        }

        const std::string_view boundary_trimmed = trim_line(lines[boundary_line]);
        if (boundary_trimmed.empty() || boundary_trimmed == "end" ||
            boundary_trimmed == "else" ||
            StartsWith(boundary_trimmed, "elseif ") ||
            StartsWith(boundary_trimmed, "until ")) {
            continue;
        }

        std::size_t suppressed_run_begin = lines.size();
        std::size_t suppressed_run_end = lines.size();
        const std::size_t suppressed_run_count =
            suppressed_end_run(boundary_line, &suppressed_run_begin, &suppressed_run_end);
        const int max_end_count =
            suppressed_run_count == 0
                ? 4
                : std::max<int>(4, std::min<int>(8, static_cast<int>(suppressed_run_count)));

        for (int end_count = 1; end_count <= max_end_count; ++end_count) {
            std::vector<std::string> candidate_lines = lines;
            if (suppressed_run_count != 0) {
                const std::size_t replace_count =
                    std::min<std::size_t>(static_cast<std::size_t>(end_count),
                                          suppressed_run_count);
                const std::size_t replace_begin = suppressed_run_end - replace_count + 1;
                for (std::size_t offset = 0; offset < replace_count; ++offset) {
                    candidate_lines[replace_begin + offset] = open_indent + "end";
                }
                if (static_cast<std::size_t>(end_count) > replace_count) {
                    candidate_lines.insert(
                        candidate_lines.begin() +
                            static_cast<std::ptrdiff_t>(boundary_line),
                        static_cast<std::size_t>(end_count) - replace_count,
                        open_indent + "end");
                }
            } else {
                candidate_lines.insert(candidate_lines.begin() +
                                           static_cast<std::ptrdiff_t>(boundary_line),
                                       static_cast<std::size_t>(end_count),
                                       open_indent + "end");
            }

            std::string candidate = JoinLines(candidate_lines, had_trailing_newline);
            const std::string candidate_error = ValidateLuaRepairSource(candidate);
            if (candidate_error.empty()) {
                decompiled_source = std::move(candidate);
                if (note != nullptr) {
                    *note = JoinNotes(
                        *note,
                        "syntax_repaired_closed_reported_function_before_sibling_boundary");
                }
                return true;
            }

            const SyntaxErrorDescriptor candidate_descriptor = DescribeSyntaxError(candidate_error);
            if (candidate_descriptor.line < baseline_descriptor.line ||
                !IsValidationOutcomeBetter(validation_error, candidate_error) ||
                (!best_error.empty() &&
                 !IsValidationOutcomeBetter(best_error, candidate_error))) {
                continue;
            }

            best_candidate = std::move(candidate);
            best_error = candidate_error;
        }
    }

    if (best_candidate.empty()) {
        return false;
    }

    decompiled_source = std::move(best_candidate);
    if (note != nullptr) {
        *note = JoinNotes(
            *note,
            "syntax_repaired_closed_reported_function_before_sibling_boundary_progress");
    }
    return true;
}

bool TryInsertEndsAcrossTopLevelAnonymousFunctionChain(
    std::string& decompiled_source,
    std::string_view validation_error,
    std::string* note) {
    if (validation_error.find("'end' expected") == std::string_view::npos &&
        validation_error.find("unexpected symbol near") == std::string_view::npos &&
        validation_error.find("expected near") == std::string_view::npos) {
        return false;
    }
    if (note != nullptr &&
        (note->find(
             "syntax_repaired_inserted_ends_across_top_level_anonymous_function_method_chain") !=
             std::string::npos)) {
        return false;
    }

    bool had_trailing_newline = false;
    const std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);
    if (lines.size() < 6) {
        return false;
    }

    const auto reported_open_function_is_nested =
        [&](std::string_view raw_error) -> bool {
            constexpr std::string_view kNeedle = "to close 'function' at line ";
            const std::size_t start = raw_error.find(kNeedle);
            if (start == std::string_view::npos) {
                return false;
            }

            std::size_t number_begin = start + kNeedle.size();
            std::size_t number_end = number_begin;
            while (number_end < raw_error.size() &&
                   std::isdigit(static_cast<unsigned char>(raw_error[number_end])) != 0) {
                ++number_end;
            }
            if (number_end == number_begin) {
                return false;
            }

            const std::size_t line_number = static_cast<std::size_t>(
                std::strtoul(std::string(raw_error.substr(number_begin, number_end - number_begin))
                                 .c_str(),
                             nullptr,
                             10));
            if (line_number == 0 || line_number > lines.size()) {
                return false;
            }

            const std::string_view function_line =
                TrimWhitespace(refii::lua::StripLuaLineComment(lines[line_number - 1]));
            if (function_line.find("function(") == std::string_view::npos &&
                function_line.find("function ") == std::string_view::npos) {
                return false;
            }

            const std::size_t indent =
                lines[line_number - 1].find_first_not_of(" \t");
            return indent != std::string::npos && indent != 0;
        };
    if (reported_open_function_is_nested(validation_error)) {
        return false;
    }

    const SyntaxErrorDescriptor baseline_descriptor = DescribeSyntaxError(validation_error);
    const std::size_t reported_line = ExtractLuaErrorLine(validation_error);
    if (reported_line == 0 || reported_line > lines.size()) {
        return false;
    }
    const bool near_eof = validation_error.find("<eof>") != std::string_view::npos;

    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimWhitespace(refii::lua::StripLuaLineComment(line));
    };
    const auto line_indent = [](std::string_view line) -> std::string_view {
        const std::size_t first_non_ws = line.find_first_not_of(" \t");
        if (first_non_ws == std::string_view::npos) {
            return line;
        }
        return line.substr(0, first_non_ws);
    };
    const auto previous_nonempty = [&](std::size_t start) -> std::size_t {
        if (start == 0 || start > lines.size()) {
            return lines.size();
        }
        for (std::size_t i = start; i-- > 0;) {
            if (!trim_line(lines[i]).empty()) {
                return i;
            }
            if (i == 0) {
                break;
            }
        }
        return lines.size();
    };
    const auto is_top_level_anonymous_function_assignment = [&](std::size_t line_index) {
        return line_index < lines.size() &&
               IsTopLevelAnonymousFunctionBoundaryLine(lines[line_index]);
    };

    std::vector<std::size_t> malformed_boundaries;
    std::size_t anonymous_function_count = 0;
    for (std::size_t boundary_line = 1; boundary_line < lines.size(); ++boundary_line) {
        if (!is_top_level_anonymous_function_assignment(boundary_line)) {
            continue;
        }
        const std::size_t current_line = boundary_line + 1;
        const bool in_report_window =
            !(current_line + 192 < reported_line || current_line > reported_line + 16);
        const bool in_tail_window =
            near_eof && current_line + 256 >= lines.size();
        if (!in_report_window && !in_tail_window) {
            continue;
        }

        ++anonymous_function_count;
        const std::size_t previous_line = previous_nonempty(boundary_line);
        if (previous_line >= lines.size()) {
            continue;
        }

        const std::string_view previous_trimmed = trim_line(lines[previous_line]);
        const bool previous_is_top_level_end =
            previous_trimmed == "end" && line_indent(lines[previous_line]).empty();
        if (previous_trimmed.empty() || previous_is_top_level_end ||
            LooksLikeTopLevelBoundary(previous_trimmed)) {
            continue;
        }

        malformed_boundaries.push_back(boundary_line);
    }

    if (anonymous_function_count < 2 || malformed_boundaries.empty()) {
        return false;
    }

    std::vector<std::string> candidate_lines = lines;
    for (auto it = malformed_boundaries.rbegin(); it != malformed_boundaries.rend(); ++it) {
        candidate_lines.insert(candidate_lines.begin() +
                                   static_cast<std::ptrdiff_t>(*it),
                               "end");
    }

    std::string candidate = JoinLines(candidate_lines, had_trailing_newline);
    const std::string candidate_error = ValidateLuaRepairSource(candidate);
    if (candidate_error.empty()) {
        decompiled_source = std::move(candidate);
        if (note != nullptr) {
            *note = JoinNotes(
                *note,
                "syntax_repaired_inserted_ends_across_top_level_anonymous_function_chain");
        }
        return true;
    }

    const SyntaxErrorDescriptor candidate_descriptor = DescribeSyntaxError(candidate_error);
    if (candidate_descriptor.line <= baseline_descriptor.line ||
        !IsValidationOutcomeBetter(validation_error, candidate_error)) {
        return false;
    }

    decompiled_source = std::move(candidate);
    if (note != nullptr) {
        *note = JoinNotes(
            *note,
            "syntax_repaired_inserted_ends_across_top_level_anonymous_function_chain_progress");
    }
    return true;
}

bool TryRestoreDuplicatedTopLevelDebugKeyCallbackChain(std::string& decompiled_source,
                                                       std::string_view validation_error,
                                                       std::string* note) {
    if (validation_error.find("unexpected symbol near ')'") == std::string_view::npos) {
        return false;
    }

    const SyntaxErrorDescriptor baseline_descriptor = DescribeSyntaxError(validation_error);

    bool had_trailing_newline = false;
    const std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);
    if (lines.size() < 16) {
        return false;
    }

    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimWhitespace(refii::lua::StripLuaLineComment(line));
    };
    const auto line_indent = [](std::string_view line) -> std::string_view {
        const std::size_t first_non_ws = line.find_first_not_of(" \t");
        if (first_non_ws == std::string_view::npos) {
            return line;
        }
        return line.substr(0, first_non_ws);
    };
    std::size_t top_level_indent = std::numeric_limits<std::size_t>::max();
    for (const std::string& line : lines) {
        const std::string_view trimmed = trim_line(line);
        if (trimmed.empty()) {
            continue;
        }
        const std::size_t indent = line.find_first_not_of(" \t");
        top_level_indent = std::min(top_level_indent,
                                    indent == std::string::npos ? std::size_t{0} : indent);
    }
    if (top_level_indent == std::numeric_limits<std::size_t>::max()) {
        top_level_indent = 0;
    }

    const auto is_top_level_function_assignment = [&](std::size_t line_index) {
        if (line_index >= lines.size()) {
            return false;
        }
        const std::size_t indent = lines[line_index].find_first_not_of(" \t");
        if ((indent == std::string::npos ? std::size_t{0} : indent) != top_level_indent) {
            return false;
        }
        const std::string_view trimmed = trim_line(lines[line_index]);
        const std::size_t equal = trimmed.find('=');
        if (equal == std::string_view::npos) {
            return false;
        }
        return StartsWith(TrimWhitespace(trimmed.substr(equal + 1)), "function(");
    };
    const auto is_broken_debug_registration = [&](std::size_t line_index) {
        if (line_index >= lines.size()) {
            return false;
        }
        const std::string_view trimmed = trim_line(lines[line_index]);
        return StartsWith(trimmed, "Debug.AddLuaDebugKeyFunc(") &&
               EndsWith(trimmed, ", function(key))");
    };
    const auto choose_callback_name = [&](std::size_t function_begin,
                                          std::size_t function_end) {
        std::string name = "refii_debug_key_callback";
        int suffix = 2;
        while (true) {
            bool collides = false;
            for (std::size_t line_index = function_begin;
                 line_index <= function_end && line_index < lines.size();
                 ++line_index) {
                if (lines[line_index].find(name) != std::string::npos) {
                    collides = true;
                    break;
                }
            }
            if (!collides) {
                return name;
            }
            name = "refii_debug_key_callback" + std::to_string(suffix++);
        }
    };

    for (std::size_t first_registration = 0; first_registration < lines.size();
         ++first_registration) {
        if (!is_broken_debug_registration(first_registration)) {
            continue;
        }

        std::size_t registration_end = first_registration;
        while (registration_end + 1 < lines.size() &&
               is_broken_debug_registration(registration_end + 1)) {
            ++registration_end;
        }
        const std::size_t registration_count = registration_end - first_registration + 1;
        if (registration_count < 3 || registration_end + 1 >= lines.size() ||
            trim_line(lines[registration_end + 1]) != "end") {
            continue;
        }

        std::vector<std::pair<std::size_t, std::size_t>> duplicate_blocks = {
            {first_registration, registration_end + 1},
        };
        std::size_t cursor = registration_end + 2;
        while (cursor < lines.size()) {
            if (!is_broken_debug_registration(cursor)) {
                break;
            }

            std::size_t block_end = cursor;
            while (block_end + 1 < lines.size() &&
                   is_broken_debug_registration(block_end + 1)) {
                ++block_end;
            }
            if (block_end - cursor + 1 != registration_count || block_end + 1 >= lines.size() ||
                trim_line(lines[block_end + 1]) != "end") {
                break;
            }

            bool block_matches = true;
            for (std::size_t offset = 0; offset < registration_count; ++offset) {
                if (trim_line(lines[first_registration + offset]) !=
                    trim_line(lines[cursor + offset])) {
                    block_matches = false;
                    break;
                }
            }
            if (!block_matches) {
                break;
            }

            duplicate_blocks.push_back({cursor, block_end + 1});
            cursor = block_end + 2;
        }
        std::size_t function_begin = first_registration;
        while (function_begin > 0) {
            --function_begin;
            if (is_top_level_function_assignment(function_begin)) {
                break;
            }
        }
        if (!is_top_level_function_assignment(function_begin) ||
            first_registration <= function_begin + 1) {
            continue;
        }

        bool saw_key_use = false;
        for (std::size_t probe = function_begin + 1; probe < first_registration; ++probe) {
            if (lines[probe].find("key") != std::string::npos) {
                saw_key_use = true;
                break;
            }
        }
        if (!saw_key_use) {
            continue;
        }

        const std::string callback_name =
            choose_callback_name(function_begin, duplicate_blocks.back().second);
        const std::string function_indent(line_indent(lines[function_begin]));
        const std::string callback_indent = function_indent + "  ";

        std::vector<std::string> candidate_lines = lines;
        candidate_lines.insert(candidate_lines.begin() +
                                   static_cast<std::ptrdiff_t>(function_begin + 1),
                               callback_indent + "local " + callback_name + " = function(key)");

        const std::size_t first_registration_after_insert = first_registration + 1;
        candidate_lines.insert(candidate_lines.begin() +
                                   static_cast<std::ptrdiff_t>(first_registration_after_insert),
                               callback_indent + "end");

        const std::size_t shifted_registration_count = registration_count;
        for (std::size_t offset = 0; offset < shifted_registration_count; ++offset) {
            const std::size_t line_index = first_registration_after_insert + 1 + offset;
            const std::string_view stripped =
                refii::lua::StripLuaLineComment(candidate_lines[line_index]);
            const std::string_view trimmed = TrimWhitespace(stripped);
            const std::string_view suffix = ", function(key))";
            if (!EndsWith(trimmed, suffix)) {
                continue;
            }

            const std::string_view comment =
                stripped.size() < candidate_lines[line_index].size()
                    ? std::string_view(candidate_lines[line_index]).substr(stripped.size())
                    : std::string_view();
            const std::string prefix(trimmed.substr(0, trimmed.size() - suffix.size()));
            const std::string indent(line_indent(candidate_lines[line_index]));
            candidate_lines[line_index] =
                indent + prefix + ", " + callback_name + ")" + std::string(comment);
        }

        std::size_t removed = 0;
        for (std::size_t block_index = duplicate_blocks.size(); block_index-- > 1;) {
            const auto [begin, end] = duplicate_blocks[block_index];
            const std::size_t adjusted_begin = begin + 2 - removed;
            const std::size_t adjusted_end = end + 2 - removed;
            candidate_lines.erase(candidate_lines.begin() +
                                      static_cast<std::ptrdiff_t>(adjusted_begin),
                                  candidate_lines.begin() +
                                      static_cast<std::ptrdiff_t>(adjusted_end + 1));
            removed += end - begin + 1;
        }

        std::string candidate = JoinLines(candidate_lines, had_trailing_newline);
        const std::string candidate_error = ValidateLuaRepairSource(candidate);
        if (candidate_error.empty()) {
            decompiled_source = std::move(candidate);
            if (note != nullptr) {
                *note = JoinNotes(
                    *note,
                    "syntax_repaired_restored_top_level_debug_key_callback_chain");
            }
            return true;
        }

        const SyntaxErrorDescriptor candidate_descriptor = DescribeSyntaxError(candidate_error);
        if (candidate_descriptor.line <= baseline_descriptor.line ||
            !IsValidationOutcomeBetter(validation_error, candidate_error)) {
            continue;
        }

        decompiled_source = std::move(candidate);
        if (note != nullptr) {
            *note = JoinNotes(
                *note,
                "syntax_repaired_restored_top_level_debug_key_callback_chain_progress");
        }
        return true;
    }

    return false;
}

bool TryInsertEndsAcrossTopLevelAnonymousFunctionMethodCallChain(
    std::string& decompiled_source,
    std::string_view validation_error,
    std::string* note) {
    if ((validation_error.find("'end' expected") == std::string_view::npos &&
         validation_error.find("unexpected symbol near") == std::string_view::npos &&
         validation_error.find("expected near") == std::string_view::npos) ||
        (validation_error.find("<eof>") == std::string_view::npos &&
         validation_error.find("unexpected symbol near") == std::string_view::npos &&
         validation_error.find("expected near") == std::string_view::npos)) {
        return false;
    }

    bool had_trailing_newline = false;
    const std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);
    if (lines.size() < 6) {
        return false;
    }

    const auto reported_open_function_is_nested =
        [&](std::string_view raw_error) -> bool {
            constexpr std::string_view kNeedle = "to close 'function' at line ";
            const std::size_t start = raw_error.find(kNeedle);
            if (start == std::string_view::npos) {
                return false;
            }

            std::size_t number_begin = start + kNeedle.size();
            std::size_t number_end = number_begin;
            while (number_end < raw_error.size() &&
                   std::isdigit(static_cast<unsigned char>(raw_error[number_end])) != 0) {
                ++number_end;
            }
            if (number_end == number_begin) {
                return false;
            }

            const std::size_t line_number = static_cast<std::size_t>(
                std::strtoul(std::string(raw_error.substr(number_begin, number_end - number_begin))
                                 .c_str(),
                             nullptr,
                             10));
            if (line_number == 0 || line_number > lines.size()) {
                return false;
            }

            const std::string_view function_line =
                TrimWhitespace(refii::lua::StripLuaLineComment(lines[line_number - 1]));
            if (function_line.find("function(") == std::string_view::npos &&
                function_line.find("function ") == std::string_view::npos) {
                return false;
            }

            const std::size_t indent =
                lines[line_number - 1].find_first_not_of(" \t");
            return indent != std::string::npos && indent != 0;
        };
    if (reported_open_function_is_nested(validation_error)) {
        return false;
    }

    const SyntaxErrorDescriptor baseline_descriptor = DescribeSyntaxError(validation_error);
    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimWhitespace(refii::lua::StripLuaLineComment(line));
    };
    const auto previous_nonempty = [&](std::size_t start) -> std::size_t {
        if (start == 0 || start > lines.size()) {
            return lines.size();
        }
        for (std::size_t i = start; i-- > 0;) {
            if (!trim_line(lines[i]).empty()) {
                return i;
            }
            if (i == 0) {
                break;
            }
        }
        return lines.size();
    };
    const auto is_top_level_anonymous_function_assignment = [&](std::size_t line_index) {
        return line_index < lines.size() &&
               IsTopLevelAnonymousFunctionBoundaryLine(lines[line_index]);
    };
    const auto looks_like_method_call_tail = [&](std::string_view line) -> bool {
        const std::string_view trimmed = trim_line(line);
        if (trimmed.empty() || trimmed == "end" || trimmed == "else") {
            return false;
        }
        if (!EndsWith(trimmed, ")") || trimmed.find(':') == std::string_view::npos) {
            return false;
        }
        return trimmed.find("function") == std::string_view::npos &&
               trimmed.find(" then") == std::string_view::npos &&
               trimmed.find(" do") == std::string_view::npos;
    };

    std::vector<std::size_t> candidate_boundaries;
    for (std::size_t boundary_line = 1; boundary_line < lines.size(); ++boundary_line) {
        if (!is_top_level_anonymous_function_assignment(boundary_line)) {
            continue;
        }
        if (boundary_line + 1 + 256 < lines.size()) {
            continue;
        }

        const std::size_t previous_line = previous_nonempty(boundary_line);
        if (previous_line >= lines.size() || !looks_like_method_call_tail(lines[previous_line])) {
            continue;
        }

        candidate_boundaries.push_back(boundary_line);
    }

    if (candidate_boundaries.size() < 2) {
        return false;
    }

    std::size_t cluster_start = candidate_boundaries.size() - 1;
    while (cluster_start > 0 &&
           candidate_boundaries[cluster_start] - candidate_boundaries[cluster_start - 1] <= 64) {
        --cluster_start;
    }

    const std::vector<std::size_t> cluster(candidate_boundaries.begin() +
                                               static_cast<std::ptrdiff_t>(cluster_start),
                                           candidate_boundaries.end());
    if (cluster.size() < 2) {
        return false;
    }

    std::vector<std::string> candidate_lines = lines;
    for (auto it = cluster.rbegin(); it != cluster.rend(); ++it) {
        candidate_lines.insert(candidate_lines.begin() +
                                   static_cast<std::ptrdiff_t>(*it),
                               "end");
    }

    std::string candidate = JoinLines(candidate_lines, had_trailing_newline);
    const std::string candidate_error = ValidateLuaRepairSource(candidate);
    if (candidate_error.empty()) {
        decompiled_source = std::move(candidate);
        if (note != nullptr) {
            *note = JoinNotes(
                *note,
                "syntax_repaired_inserted_ends_across_top_level_anonymous_function_method_chain");
        }
        return true;
    }

    const SyntaxErrorDescriptor candidate_descriptor = DescribeSyntaxError(candidate_error);
    if (candidate_descriptor.line <= baseline_descriptor.line ||
        !IsValidationOutcomeBetter(validation_error, candidate_error)) {
        return false;
    }

    decompiled_source = std::move(candidate);
    if (note != nullptr) {
        *note = JoinNotes(
            *note,
            "syntax_repaired_inserted_ends_across_top_level_anonymous_function_method_chain_progress");
    }
    return true;
}

bool TryRepairStrandedPollElseYieldWaitLoops(std::string& decompiled_source,
                                             std::string_view validation_error,
                                             std::string* note) {
    if (validation_error.find("'end' expected") == std::string_view::npos ||
        validation_error.find("near 'else'") == std::string_view::npos) {
        return false;
    }

    bool had_trailing_newline = false;
    const std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);
    if (lines.size() < 12) {
        return false;
    }

    const std::size_t reported_line = ExtractLuaErrorLine(validation_error);
    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimWhitespace(refii::lua::StripLuaLineComment(line));
    };
    const auto line_indent = [](std::string_view line) -> std::string_view {
        const std::size_t first_non_ws = line.find_first_not_of(" \t");
        if (first_non_ws == std::string_view::npos) {
            return line;
        }
        return line.substr(0, first_non_ws);
    };
    const auto next_nonempty = [&](std::size_t start) -> std::size_t {
        for (std::size_t i = start; i < lines.size(); ++i) {
            if (!trim_line(lines[i]).empty()) {
                return i;
            }
        }
        return lines.size();
    };
    const auto previous_nonempty = [&](std::size_t start) -> std::size_t {
        if (start == 0 || start > lines.size()) {
            return lines.size();
        }
        for (std::size_t i = start; i-- > 0;) {
            if (!trim_line(lines[i]).empty()) {
                return i;
            }
            if (i == 0) {
                break;
            }
        }
        return lines.size();
    };
    const auto parse_if_condition = [](std::string_view line, std::string* condition) -> bool {
        if (condition == nullptr) {
            return false;
        }

        const std::string_view trimmed = refii::lua::TrimLuaWhitespace(
            refii::lua::StripLuaLineComment(line));
        if (!StartsWith(trimmed, "if ") || !EndsWith(trimmed, " then")) {
            return false;
        }

        const std::string_view extracted =
            refii::lua::TrimLuaWhitespace(trimmed.substr(3, trimmed.size() - 8));
        if (extracted.empty()) {
            return false;
        }

        *condition = std::string(extracted);
        return true;
    };

    const SyntaxErrorDescriptor baseline_descriptor = DescribeSyntaxError(validation_error);
    std::string best_candidate;
    std::string best_error;
    const auto consider_candidate = [&](std::vector<std::string> candidate_lines,
                                        std::string_view validated_note,
                                        std::string_view progress_note) -> bool {
        std::string candidate = JoinLines(candidate_lines, had_trailing_newline);
        const std::string candidate_error =
            ValidateLuaRepairSource(candidate);
        if (candidate_error.empty()) {
            decompiled_source = std::move(candidate);
            if (note != nullptr) {
                *note = JoinNotes(*note, std::string(validated_note));
            }
            return true;
        }

        const SyntaxErrorDescriptor candidate_descriptor =
            DescribeSyntaxError(candidate_error);
        if (candidate_descriptor.line <= baseline_descriptor.line ||
            !IsValidationOutcomeBetter(validation_error, candidate_error) ||
            (!best_error.empty() &&
             !IsValidationOutcomeBetter(best_error, candidate_error))) {
            return false;
        }

        best_candidate = std::move(candidate);
        best_error = candidate_error;
        if (note != nullptr && !progress_note.empty()) {
            *note = JoinNotes(*note, std::string(progress_note));
        }
        return false;
    };
    for (std::size_t outer_else_line = 0; outer_else_line < lines.size(); ++outer_else_line) {
        if (reported_line != 0) {
            const std::size_t current_line = outer_else_line + 1;
            if (current_line + 32 < reported_line || current_line > reported_line) {
                continue;
            }
        }

        if (trim_line(lines[outer_else_line]) != "else") {
            continue;
        }

        const std::string outer_indent(line_indent(lines[outer_else_line]));
        const std::size_t continuation_start = next_nonempty(outer_else_line + 1);
        if (continuation_start >= lines.size()) {
            continue;
        }

        const std::string continuation_body_prefix = outer_indent + "  ";
        if (!line_indent(lines[continuation_start]).starts_with(continuation_body_prefix)) {
            continue;
        }

        const std::size_t inner_end_line = previous_nonempty(outer_else_line);
        const std::size_t yield_line = previous_nonempty(inner_end_line);
        const std::size_t inner_else_line = previous_nonempty(yield_line);
        if (inner_end_line >= lines.size() || yield_line >= lines.size() ||
            inner_else_line >= lines.size() || trim_line(lines[inner_end_line]) != "end" ||
            trim_line(lines[yield_line]) != "coroutine.yield()" ||
            trim_line(lines[inner_else_line]) != "else") {
            continue;
        }

        std::size_t direct_if_line = lines.size();
        const std::string if_indent = outer_indent + "  ";
        for (std::size_t probe = inner_else_line + 1; probe-- > 0;) {
            if (probe < inner_else_line &&
                line_indent(lines[probe]) == if_indent) {
                std::string ignored_condition;
                if (!parse_if_condition(lines[probe], &ignored_condition)) {
                    continue;
                }
                direct_if_line = probe;
                break;
            }
            if (probe == 0 || probe + 32 < inner_else_line) {
                break;
            }
        }

        std::size_t prefix_end = inner_else_line;
        if (direct_if_line < lines.size()) {
            const std::size_t then_body_last = previous_nonempty(inner_else_line);
            if (then_body_last < lines.size() &&
                then_body_last > direct_if_line &&
                trim_line(lines[then_body_last]) == "end") {
                std::size_t run_begin = then_body_last;
                while (run_begin > direct_if_line + 1) {
                    const std::size_t previous = previous_nonempty(run_begin);
                    if (previous >= lines.size() || previous <= direct_if_line ||
                        trim_line(lines[previous]) != "end" ||
                        line_indent(lines[previous]) != if_indent) {
                        break;
                    }
                    run_begin = previous;
                }

                const std::size_t body_last = previous_nonempty(run_begin);
                if (body_last < lines.size() && body_last > direct_if_line &&
                    trim_line(lines[body_last]) != "end") {
                    prefix_end = body_last + 1;
                }
            }
        }

        std::size_t direct_while_line = lines.size();
        if (direct_if_line < lines.size()) {
            for (std::size_t probe = direct_if_line + 1; probe-- > 0;) {
                if (trim_line(lines[probe]) == "while true do" &&
                    line_indent(lines[probe]) == outer_indent) {
                    direct_while_line = probe;
                    break;
                }
                if (probe == 0 || probe + 48 < direct_if_line) {
                    break;
                }
            }
        }

        std::size_t direct_continuation_boundary = lines.size();
        for (std::size_t probe = continuation_start; probe < lines.size(); ++probe) {
            if (probe == continuation_start) {
                continue;
            }
            const std::string_view probe_trimmed = trim_line(lines[probe]);
            if (probe_trimmed.empty()) {
                continue;
            }
            if (line_indent(lines[probe]).size() <= outer_indent.size()) {
                direct_continuation_boundary = probe;
                break;
            }
        }

        if (direct_if_line < lines.size() && direct_while_line < lines.size() &&
            direct_continuation_boundary < lines.size() &&
            trim_line(lines[direct_continuation_boundary]) == "end") {
            std::size_t direct_continuation_last =
                previous_nonempty(direct_continuation_boundary);
            while (direct_continuation_last < lines.size() &&
                   direct_continuation_last >= continuation_start &&
                   trim_line(lines[direct_continuation_last]) == "end" &&
                   line_indent(lines[direct_continuation_last]).size() > outer_indent.size()) {
                const std::size_t previous = previous_nonempty(direct_continuation_last);
                if (previous >= direct_continuation_last) {
                    break;
                }
                direct_continuation_last = previous;
            }

            if (direct_continuation_last >= continuation_start &&
                direct_continuation_last < lines.size()) {
                std::vector<std::string> candidate_lines;
                candidate_lines.reserve(lines.size() + 4);
                candidate_lines.insert(
                    candidate_lines.end(),
                    lines.begin(),
                    lines.begin() + static_cast<std::ptrdiff_t>(prefix_end));
                candidate_lines.push_back(if_indent + "  break");
                candidate_lines.push_back(lines[inner_else_line]);
                candidate_lines.push_back(lines[yield_line]);
                candidate_lines.push_back(lines[inner_end_line]);
                candidate_lines.push_back(outer_indent + "end");
                for (std::size_t probe = continuation_start;
                     probe <= direct_continuation_last;
                     ++probe) {
                    std::string moved_line = lines[probe];
                    const std::string_view current_indent = line_indent(moved_line);
                    if (current_indent.starts_with(continuation_body_prefix)) {
                        moved_line =
                            outer_indent +
                            std::string(current_indent.substr(continuation_body_prefix.size())) +
                            std::string(moved_line.substr(current_indent.size()));
                    }
                    candidate_lines.push_back(std::move(moved_line));
                }
                candidate_lines.insert(
                    candidate_lines.end(),
                    lines.begin() +
                        static_cast<std::ptrdiff_t>(direct_continuation_boundary + 1),
                    lines.end());

                if (consider_candidate(
                        std::move(candidate_lines),
                        "syntax_repaired_rebuilt_stranded_poll_wait_loop",
                        std::string())) {
                    return true;
                }
            }
        }

        std::size_t top_level_boundary = lines.size();
        if (direct_if_line < lines.size() && direct_while_line < lines.size()) {
            for (std::size_t probe = continuation_start; probe < lines.size(); ++probe) {
                if (lines[probe].find_first_not_of(" \t") != 0) {
                    continue;
                }
                const std::string_view probe_trimmed = trim_line(lines[probe]);
                if (LooksLikeTopLevelBoundary(probe_trimmed)) {
                    top_level_boundary = probe;
                    break;
                }
            }
        }

        if (top_level_boundary < lines.size()) {
            const std::size_t preserved_end_line = previous_nonempty(top_level_boundary);
            if (preserved_end_line < lines.size() &&
                trim_line(lines[preserved_end_line]) == "end") {
                std::size_t leaked_end_run_begin = preserved_end_line;
                while (leaked_end_run_begin > 0) {
                    const std::size_t previous = previous_nonempty(leaked_end_run_begin);
                    if (previous >= lines.size() || trim_line(lines[previous]) != "end") {
                        break;
                    }
                    leaked_end_run_begin = previous;
                }

                if (leaked_end_run_begin < preserved_end_line) {
                    std::size_t moved_continuation_last =
                        previous_nonempty(leaked_end_run_begin);
                    if (line_indent(lines[leaked_end_run_begin]).size() >
                            outer_indent.size() &&
                        moved_continuation_last >= continuation_start &&
                        moved_continuation_last < leaked_end_run_begin) {
                        moved_continuation_last = leaked_end_run_begin;
                    }

                    if (moved_continuation_last >= continuation_start &&
                        moved_continuation_last < lines.size()) {
                        std::vector<std::string> candidate_lines;
                        candidate_lines.reserve(lines.size() + 4);
                        candidate_lines.insert(
                            candidate_lines.end(),
                            lines.begin(),
                            lines.begin() + static_cast<std::ptrdiff_t>(prefix_end));
                        candidate_lines.push_back(if_indent + "  break");
                        candidate_lines.push_back(lines[inner_else_line]);
                        candidate_lines.push_back(lines[yield_line]);
                        candidate_lines.push_back(lines[inner_end_line]);
                        candidate_lines.push_back(outer_indent + "end");
                        for (std::size_t probe = continuation_start;
                             probe <= moved_continuation_last;
                             ++probe) {
                            std::string moved_line = lines[probe];
                            const std::string_view current_indent =
                                line_indent(moved_line);
                            if (current_indent.starts_with(continuation_body_prefix)) {
                                moved_line =
                                    outer_indent +
                                    std::string(current_indent.substr(
                                        continuation_body_prefix.size())) +
                                    std::string(moved_line.substr(current_indent.size()));
                            }
                            candidate_lines.push_back(std::move(moved_line));
                        }
                        candidate_lines.insert(
                            candidate_lines.end(),
                            lines.begin() +
                                static_cast<std::ptrdiff_t>(preserved_end_line),
                            lines.end());

                        if (consider_candidate(
                                std::move(candidate_lines),
                                "syntax_repaired_rebuilt_stranded_poll_wait_loop",
                                std::string())) {
                            return true;
                        }
                    }
                }
            }
        }

        const std::size_t extra_end_run_end = previous_nonempty(inner_else_line);
        if (extra_end_run_end >= lines.size() ||
            trim_line(lines[extra_end_run_end]) != "end") {
            continue;
        }

        std::size_t extra_end_run_begin = extra_end_run_end;
        while (extra_end_run_begin > 0) {
            const std::size_t previous = previous_nonempty(extra_end_run_begin);
            if (previous >= lines.size() || trim_line(lines[previous]) != "end" ||
                line_indent(lines[previous]) != line_indent(lines[inner_else_line])) {
                break;
            }
            extra_end_run_begin = previous;
        }

        const std::size_t then_body_last = previous_nonempty(extra_end_run_begin);
        if (then_body_last >= lines.size()) {
            continue;
        }

        std::size_t if_line = lines.size();
        for (std::size_t probe = then_body_last + 1; probe-- > 0;) {
            if (probe < extra_end_run_begin &&
                line_indent(lines[probe]) == if_indent) {
                std::string ignored_condition;
                if (!parse_if_condition(lines[probe], &ignored_condition)) {
                    continue;
                }
                if_line = probe;
                break;
            }
            if (probe == 0 || probe + 32 < then_body_last) {
                break;
            }
        }
        if (if_line >= lines.size()) {
            continue;
        }

        std::size_t while_line = lines.size();
        for (std::size_t probe = if_line + 1; probe-- > 0;) {
            if (trim_line(lines[probe]) == "while true do" &&
                line_indent(lines[probe]) == outer_indent) {
                while_line = probe;
                break;
            }
            if (probe == 0 || probe + 48 < if_line) {
                break;
            }
        }
        if (while_line >= lines.size()) {
            continue;
        }

        std::size_t continuation_boundary = lines.size();
        for (std::size_t probe = continuation_start; probe < lines.size(); ++probe) {
            if (probe == continuation_start) {
                continue;
            }
            const std::string_view probe_trimmed = trim_line(lines[probe]);
            if (probe_trimmed.empty()) {
                continue;
            }
            if (line_indent(lines[probe]).size() <= outer_indent.size()) {
                continuation_boundary = probe;
                break;
            }
        }
        if (continuation_boundary >= lines.size()) {
            continue;
        }

        const std::size_t continuation_last = previous_nonempty(continuation_boundary);
        if (continuation_last < continuation_start || continuation_last >= lines.size()) {
            continue;
        }

        std::vector<std::string> candidate_lines;
        candidate_lines.reserve(lines.size() + 4);
        candidate_lines.insert(candidate_lines.end(), lines.begin(),
                               lines.begin() + static_cast<std::ptrdiff_t>(then_body_last + 1));
        candidate_lines.push_back(if_indent + "  break");
        candidate_lines.push_back(lines[inner_else_line]);
        candidate_lines.push_back(lines[yield_line]);
        candidate_lines.push_back(lines[inner_end_line]);
        candidate_lines.push_back(outer_indent + "end");
        for (std::size_t probe = continuation_start; probe <= continuation_last; ++probe) {
            std::string moved_line = lines[probe];
            const std::string_view current_indent = line_indent(moved_line);
            if (current_indent.starts_with(continuation_body_prefix)) {
                moved_line = outer_indent +
                             std::string(current_indent.substr(continuation_body_prefix.size())) +
                             std::string(moved_line.substr(current_indent.size()));
            }
            candidate_lines.push_back(std::move(moved_line));
        }
        candidate_lines.insert(candidate_lines.end(),
                               lines.begin() +
                                   static_cast<std::ptrdiff_t>(continuation_boundary),
                               lines.end());

        if (consider_candidate(
                std::move(candidate_lines),
                "syntax_repaired_rebuilt_stranded_poll_wait_loop",
                std::string())) {
            return true;
        }
    }

    if (best_candidate.empty()) {
        return false;
    }

    decompiled_source = std::move(best_candidate);
    if (note != nullptr) {
        *note = JoinNotes(*note,
                          "syntax_repaired_rebuilt_stranded_poll_wait_loop_progress");
    }
    return true;
}

bool TryRepairDuplicateElseGuardedWaitLoops(std::string& decompiled_source,
                                            std::string_view validation_error,
                                            std::string* note) {
    if ((validation_error.find("'end' expected") == std::string_view::npos &&
         validation_error.find("'<eof>' expected") == std::string_view::npos) ||
        validation_error.find("near 'else'") == std::string_view::npos) {
        return false;
    }

    bool had_trailing_newline = false;
    const std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);
    if (lines.size() < 16) {
        return false;
    }

    const std::size_t reported_line = ExtractLuaErrorLine(validation_error);
    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimWhitespace(refii::lua::StripLuaLineComment(line));
    };
    const auto line_indent = [](std::string_view line) -> std::string_view {
        const std::size_t first_non_ws = line.find_first_not_of(" \t");
        if (first_non_ws == std::string_view::npos) {
            return line;
        }
        return line.substr(0, first_non_ws);
    };
    const auto next_nonempty = [&](std::size_t start) -> std::size_t {
        for (std::size_t i = start; i < lines.size(); ++i) {
            if (!trim_line(lines[i]).empty()) {
                return i;
            }
        }
        return lines.size();
    };
    const auto previous_nonempty = [&](std::size_t start) -> std::size_t {
        if (start == 0 || start > lines.size()) {
            return lines.size();
        }
        for (std::size_t i = start; i-- > 0;) {
            if (!trim_line(lines[i]).empty()) {
                return i;
            }
            if (i == 0) {
                break;
            }
        }
        return lines.size();
    };
    const auto parse_if_condition = [](std::string_view line, std::string* condition) -> bool {
        if (condition == nullptr) {
            return false;
        }

        const std::string_view trimmed = refii::lua::TrimLuaWhitespace(
            refii::lua::StripLuaLineComment(line));
        if (!StartsWith(trimmed, "if ") || !EndsWith(trimmed, " then")) {
            return false;
        }

        const std::string_view extracted =
            refii::lua::TrimLuaWhitespace(trimmed.substr(3, trimmed.size() - 8));
        if (extracted.empty()) {
            return false;
        }

        *condition = std::string(extracted);
        return true;
    };

    const SyntaxErrorDescriptor baseline_descriptor = DescribeSyntaxError(validation_error);
    std::string best_candidate;
    std::string best_error;
    const auto consider_candidate = [&](std::vector<std::string> candidate_lines) -> bool {
        std::string candidate = JoinLines(candidate_lines, had_trailing_newline);
        const std::string candidate_error =
            ValidateLuaRepairSource(candidate);
        if (candidate_error.empty()) {
            decompiled_source = std::move(candidate);
            if (note != nullptr) {
                *note = JoinNotes(*note,
                                  "syntax_repaired_rebalanced_guarded_wait_else");
            }
            return true;
        }

        const SyntaxErrorDescriptor candidate_descriptor =
            DescribeSyntaxError(candidate_error);
        if (candidate_descriptor.line <= baseline_descriptor.line) {
            return false;
        }

        if (!best_error.empty()) {
            const SyntaxErrorDescriptor best_descriptor = DescribeSyntaxError(best_error);
            if (candidate_descriptor.line < best_descriptor.line) {
                return false;
            }
            if (candidate_descriptor.line == best_descriptor.line &&
                !IsValidationOutcomeBetter(best_error, candidate_error)) {
                return false;
            }
        } else if (!IsValidationOutcomeBetter(validation_error, candidate_error)) {
            return false;
        }

        best_candidate = std::move(candidate);
        best_error = candidate_error;
        return false;
    };

    for (std::size_t first_else_line = 0; first_else_line < lines.size(); ++first_else_line) {
        if (reported_line != 0) {
            const std::size_t current_line = first_else_line + 1;
            if (current_line + 48 < reported_line || current_line > reported_line + 4) {
                continue;
            }
        }

        if (trim_line(lines[first_else_line]) != "else") {
            continue;
        }

        const std::string if_indent(line_indent(lines[first_else_line]));
        if (if_indent.size() < 2) {
            continue;
        }
        const std::string outer_indent = if_indent.substr(0, if_indent.size() - 2);
        const std::size_t duplicate_else_line = next_nonempty(first_else_line + 1);
        if (duplicate_else_line >= lines.size() ||
            trim_line(lines[duplicate_else_line]) != "else" ||
            line_indent(lines[duplicate_else_line]) != if_indent) {
            continue;
        }

        const std::size_t continuation_start = next_nonempty(duplicate_else_line + 1);
        if (continuation_start >= lines.size()) {
            continue;
        }

        const std::string continuation_indent(line_indent(lines[continuation_start]));
        if (continuation_indent.size() <= if_indent.size()) {
            continue;
        }

        bool continuation_has_yield = false;
        std::size_t continuation_boundary = lines.size();
        for (std::size_t probe = continuation_start; probe < lines.size(); ++probe) {
            const std::string_view probe_trimmed = trim_line(lines[probe]);
            if (probe_trimmed.empty()) {
                continue;
            }
            if (probe_trimmed == "coroutine.yield()") {
                continuation_has_yield = true;
            }
            if (probe > continuation_start && probe_trimmed == "end" &&
                line_indent(lines[probe]).size() <= continuation_indent.size()) {
                continuation_boundary = probe;
                break;
            }
        }
        if (continuation_boundary >= lines.size() || !continuation_has_yield) {
            continue;
        }

        const std::size_t continuation_last = previous_nonempty(continuation_boundary);
        if (continuation_last < continuation_start || continuation_last >= lines.size()) {
            continue;
        }

        std::size_t then_body_last = previous_nonempty(first_else_line);
        if (then_body_last >= lines.size() || trim_line(lines[then_body_last]) != "end") {
            continue;
        }
        while (then_body_last < lines.size() && trim_line(lines[then_body_last]) == "end") {
            const std::size_t previous = previous_nonempty(then_body_last);
            if (previous >= then_body_last) {
                then_body_last = lines.size();
                break;
            }
            then_body_last = previous;
        }
        if (then_body_last >= lines.size() || trim_line(lines[then_body_last]) == "end") {
            continue;
        }

        bool then_body_has_nested_wait = false;
        bool then_body_has_yield = false;
        std::size_t if_line = lines.size();
        for (std::size_t probe = then_body_last + 1; probe-- > 0;) {
            if (probe < first_else_line) {
                const std::string_view probe_trimmed = trim_line(lines[probe]);
                if (probe_trimmed == "coroutine.yield()") {
                    then_body_has_yield = true;
                }
                if (StartsWith(probe_trimmed, "while ") &&
                    line_indent(lines[probe]).size() > if_indent.size()) {
                    then_body_has_nested_wait = true;
                }
                if (line_indent(lines[probe]) == if_indent) {
                    std::string ignored_condition;
                    if (parse_if_condition(lines[probe], &ignored_condition)) {
                        if_line = probe;
                        break;
                    }
                }
            }
            if (probe == 0 || probe + 64 < then_body_last) {
                break;
            }
        }
        if (if_line >= lines.size() || !then_body_has_nested_wait || !then_body_has_yield) {
            continue;
        }

        std::size_t while_line = lines.size();
        for (std::size_t probe = if_line + 1; probe-- > 0;) {
            if (trim_line(lines[probe]) == "while true do" &&
                line_indent(lines[probe]) == outer_indent) {
                while_line = probe;
                break;
            }
            if (probe == 0 || probe + 64 < if_line) {
                break;
            }
        }
        if (while_line >= lines.size()) {
            continue;
        }

        std::size_t outer_while_end = lines.size();
        for (std::size_t probe = continuation_boundary; probe < lines.size(); ++probe) {
            if (trim_line(lines[probe]) == "end" &&
                line_indent(lines[probe]) == outer_indent) {
                outer_while_end = probe;
                break;
            }
        }
        if (outer_while_end >= lines.size()) {
            continue;
        }

        std::vector<std::string> candidate_lines;
        candidate_lines.reserve(lines.size());
        candidate_lines.insert(candidate_lines.end(),
                               lines.begin(),
                               lines.begin() + static_cast<std::ptrdiff_t>(while_line));
        candidate_lines.insert(candidate_lines.end(),
                               lines.begin() + static_cast<std::ptrdiff_t>(while_line),
                               lines.begin() + static_cast<std::ptrdiff_t>(then_body_last + 1));
        candidate_lines.push_back(if_indent + "  end");
        candidate_lines.push_back(if_indent + "else");
        for (std::size_t probe = continuation_start; probe <= continuation_last; ++probe) {
            std::string moved_line = lines[probe];
            const std::string_view current_indent = line_indent(moved_line);
            if (current_indent.size() >= if_indent.size() + 4 &&
                moved_line.size() >= current_indent.size()) {
                const std::string new_indent =
                    std::string(current_indent.substr(0, current_indent.size() - 2));
                moved_line = new_indent + std::string(moved_line.substr(current_indent.size()));
            }
            candidate_lines.push_back(std::move(moved_line));
        }
        candidate_lines.push_back(if_indent + "end");
        candidate_lines.push_back(outer_indent + "end");
        candidate_lines.insert(candidate_lines.end(),
                               lines.begin() + static_cast<std::ptrdiff_t>(outer_while_end + 1),
                               lines.end());

        if (consider_candidate(std::move(candidate_lines))) {
            return true;
        }
    }

    if (best_candidate.empty()) {
        return false;
    }

    decompiled_source = std::move(best_candidate);
    if (note != nullptr) {
        *note = JoinNotes(*note,
                          "syntax_repaired_rebalanced_guarded_wait_else_progress");
    }
    return true;
}

bool TryRepairStrayEndPollElseYieldWaitLoops(std::string& decompiled_source,
                                             std::string_view validation_error,
                                             std::string* note) {
    if ((validation_error.find("'end' expected") == std::string_view::npos &&
         validation_error.find("'<eof>' expected") == std::string_view::npos) ||
        validation_error.find("near 'else'") == std::string_view::npos) {
        return false;
    }

    bool had_trailing_newline = false;
    const std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);
    if (lines.size() < 16) {
        return false;
    }

    const std::size_t reported_line = ExtractLuaErrorLine(validation_error);
    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimWhitespace(refii::lua::StripLuaLineComment(line));
    };
    const auto line_indent = [](std::string_view line) -> std::string_view {
        const std::size_t first_non_ws = line.find_first_not_of(" \t");
        if (first_non_ws == std::string_view::npos) {
            return line;
        }
        return line.substr(0, first_non_ws);
    };
    const auto next_nonempty = [&](std::size_t start) -> std::size_t {
        for (std::size_t i = start; i < lines.size(); ++i) {
            if (!trim_line(lines[i]).empty()) {
                return i;
            }
        }
        return lines.size();
    };
    const auto previous_nonempty = [&](std::size_t start) -> std::size_t {
        if (start == 0 || start > lines.size()) {
            return lines.size();
        }
        for (std::size_t i = start; i-- > 0;) {
            if (!trim_line(lines[i]).empty()) {
                return i;
            }
            if (i == 0) {
                break;
            }
        }
        return lines.size();
    };
    const auto parse_if_condition = [](std::string_view line, std::string* condition) -> bool {
        if (condition == nullptr) {
            return false;
        }

        const std::string_view trimmed = refii::lua::TrimLuaWhitespace(
            refii::lua::StripLuaLineComment(line));
        if (!StartsWith(trimmed, "if ") || !EndsWith(trimmed, " then")) {
            return false;
        }

        const std::string_view extracted =
            refii::lua::TrimLuaWhitespace(trimmed.substr(3, trimmed.size() - 8));
        if (extracted.empty()) {
            return false;
        }

        *condition = std::string(extracted);
        return true;
    };

    SyntaxRepairCandidateTracker candidate_tracker(validation_error,
                                                   had_trailing_newline,
                                                   &decompiled_source,
                                                   note);

    for (std::size_t outer_else_line = 0; outer_else_line < lines.size(); ++outer_else_line) {
        if (reported_line != 0) {
            const std::size_t current_line = outer_else_line + 1;
            if (current_line + 64 < reported_line || current_line > reported_line + 4) {
                continue;
            }
        }

        if (trim_line(lines[outer_else_line]) != "else") {
            continue;
        }

        const std::string outer_indent(line_indent(lines[outer_else_line]));
        const std::string continuation_body_prefix = outer_indent + "  ";
        const std::size_t continuation_start = next_nonempty(outer_else_line + 1);
        if (continuation_start >= lines.size() ||
            !line_indent(lines[continuation_start]).starts_with(continuation_body_prefix)) {
            continue;
        }

        const std::size_t inner_end_line = previous_nonempty(outer_else_line);
        const std::size_t yield_line = previous_nonempty(inner_end_line);
        const std::size_t inner_else_line = previous_nonempty(yield_line);
        if (inner_end_line >= lines.size() || yield_line >= lines.size() ||
            inner_else_line >= lines.size() ||
            trim_line(lines[inner_end_line]) != "end" ||
            trim_line(lines[yield_line]) != "coroutine.yield()" ||
            trim_line(lines[inner_else_line]) != "else") {
            continue;
        }

        const std::string if_indent(line_indent(lines[inner_else_line]));
        std::size_t direct_if_line = lines.size();
        for (std::size_t probe = inner_else_line; probe-- > 0;) {
            if (line_indent(lines[probe]) == if_indent) {
                std::string ignored_condition;
                if (parse_if_condition(lines[probe], &ignored_condition)) {
                    direct_if_line = probe;
                    break;
                }
            }
            if (probe == 0 || probe + 48 < inner_else_line) {
                break;
            }
        }
        if (direct_if_line >= lines.size()) {
            continue;
        }

        const std::size_t stray_end_run_end = previous_nonempty(inner_else_line);
        if (stray_end_run_end >= lines.size() ||
            trim_line(lines[stray_end_run_end]) != "end" ||
            line_indent(lines[stray_end_run_end]) != if_indent) {
            continue;
        }

        std::size_t stray_end_run_begin = stray_end_run_end;
        while (stray_end_run_begin > direct_if_line + 1) {
            const std::size_t previous = previous_nonempty(stray_end_run_begin);
            if (previous >= lines.size() || previous <= direct_if_line ||
                trim_line(lines[previous]) != "end" ||
                line_indent(lines[previous]) != if_indent) {
                break;
            }
            stray_end_run_begin = previous;
        }

        const std::size_t then_body_last = previous_nonempty(stray_end_run_begin);
        if (then_body_last >= lines.size() || then_body_last <= direct_if_line ||
            trim_line(lines[then_body_last]) == "end") {
            continue;
        }

        std::size_t while_line = lines.size();
        for (std::size_t probe = direct_if_line + 1; probe-- > 0;) {
            if (trim_line(lines[probe]) == "while true do" &&
                line_indent(lines[probe]) == outer_indent) {
                while_line = probe;
                break;
            }
            if (probe == 0 || probe + 64 < direct_if_line) {
                break;
            }
        }
        if (while_line >= lines.size()) {
            continue;
        }

        std::size_t top_level_boundary = lines.size();
        for (std::size_t probe = continuation_start; probe < lines.size(); ++probe) {
            if (lines[probe].find_first_not_of(" \t") != 0) {
                continue;
            }
            const std::string_view probe_trimmed = trim_line(lines[probe]);
            if (LooksLikeTopLevelBoundary(probe_trimmed)) {
                top_level_boundary = probe;
                break;
            }
        }
        if (top_level_boundary >= lines.size()) {
            continue;
        }

        const std::size_t preserved_end_line = previous_nonempty(top_level_boundary);
        if (preserved_end_line >= lines.size() ||
            trim_line(lines[preserved_end_line]) != "end") {
            continue;
        }

        std::size_t leaked_end_run_begin = preserved_end_line;
        while (leaked_end_run_begin > 0) {
            const std::size_t previous = previous_nonempty(leaked_end_run_begin);
            if (previous >= lines.size() || trim_line(lines[previous]) != "end") {
                break;
            }
            leaked_end_run_begin = previous;
        }
        if (leaked_end_run_begin >= preserved_end_line) {
            continue;
        }

        std::size_t moved_continuation_last = previous_nonempty(leaked_end_run_begin);
        if (line_indent(lines[leaked_end_run_begin]).size() > outer_indent.size() &&
            moved_continuation_last >= continuation_start &&
            moved_continuation_last < leaked_end_run_begin) {
            moved_continuation_last = leaked_end_run_begin;
        }
        if (moved_continuation_last < continuation_start ||
            moved_continuation_last >= lines.size()) {
            continue;
        }

        std::vector<std::string> candidate_lines;
        candidate_lines.reserve(lines.size() + 4);
        candidate_lines.insert(candidate_lines.end(),
                               lines.begin(),
                               lines.begin() + static_cast<std::ptrdiff_t>(then_body_last + 1));
        candidate_lines.push_back(if_indent + "  break");
        candidate_lines.push_back(lines[inner_else_line]);
        candidate_lines.push_back(lines[yield_line]);
        candidate_lines.push_back(lines[inner_end_line]);
        candidate_lines.push_back(outer_indent + "end");
        for (std::size_t probe = continuation_start; probe <= moved_continuation_last; ++probe) {
            std::string moved_line = lines[probe];
            const std::string_view current_indent = line_indent(moved_line);
            if (current_indent.starts_with(continuation_body_prefix)) {
                moved_line =
                    outer_indent +
                    std::string(current_indent.substr(continuation_body_prefix.size())) +
                    std::string(moved_line.substr(current_indent.size()));
            }
            candidate_lines.push_back(std::move(moved_line));
        }
        candidate_lines.push_back(lines[preserved_end_line]);
        candidate_lines.insert(candidate_lines.end(),
                               lines.begin() +
                                   static_cast<std::ptrdiff_t>(preserved_end_line + 1),
                               lines.end());

        if (candidate_tracker.Consider(
                std::move(candidate_lines),
                "syntax_repaired_rebuilt_stray_end_poll_wait_loop")) {
            return true;
        }
    }

    return candidate_tracker.CommitBestProgress(
        "syntax_repaired_rebuilt_stray_end_poll_wait_loop_progress");
}

bool TryRepairEmptyBreakElseYieldWaitLoops(std::string& decompiled_source,
                                           std::string_view validation_error,
                                           std::string* note) {
    if ((validation_error.find("'end' expected") == std::string_view::npos &&
         validation_error.find("'<eof>' expected") == std::string_view::npos) ||
        validation_error.find("near 'else'") == std::string_view::npos) {
        return false;
    }

    bool had_trailing_newline = false;
    const std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);
    if (lines.size() < 12) {
        return false;
    }

    const std::size_t reported_line = ExtractLuaErrorLine(validation_error);
    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimWhitespace(refii::lua::StripLuaLineComment(line));
    };
    const auto line_indent = [](std::string_view line) -> std::string_view {
        const std::size_t first_non_ws = line.find_first_not_of(" \t");
        if (first_non_ws == std::string_view::npos) {
            return line;
        }
        return line.substr(0, first_non_ws);
    };
    const auto next_nonempty = [&](std::size_t start) -> std::size_t {
        for (std::size_t i = start; i < lines.size(); ++i) {
            if (!trim_line(lines[i]).empty()) {
                return i;
            }
        }
        return lines.size();
    };
    const auto previous_nonempty = [&](std::size_t start) -> std::size_t {
        if (start == 0 || start > lines.size()) {
            return lines.size();
        }
        for (std::size_t i = start; i-- > 0;) {
            if (!trim_line(lines[i]).empty()) {
                return i;
            }
            if (i == 0) {
                break;
            }
        }
        return lines.size();
    };
    const auto parse_if_condition = [](std::string_view line, std::string* condition) -> bool {
        if (condition == nullptr) {
            return false;
        }

        const std::string_view trimmed = refii::lua::TrimLuaWhitespace(
            refii::lua::StripLuaLineComment(line));
        if (!StartsWith(trimmed, "if ") || !EndsWith(trimmed, " then")) {
            return false;
        }

        const std::string_view extracted =
            refii::lua::TrimLuaWhitespace(trimmed.substr(3, trimmed.size() - 8));
        if (extracted.empty()) {
            return false;
        }

        *condition = std::string(extracted);
        return true;
    };

    SyntaxRepairCandidateTracker candidate_tracker(validation_error,
                                                   had_trailing_newline,
                                                   &decompiled_source,
                                                   note);

    for (std::size_t outer_else_line = 0; outer_else_line < lines.size(); ++outer_else_line) {
        if (reported_line != 0) {
            const std::size_t current_line = outer_else_line + 1;
            if (current_line + 64 < reported_line || current_line > reported_line + 4) {
                continue;
            }
        }

        if (trim_line(lines[outer_else_line]) != "else") {
            continue;
        }

        const std::string while_indent(line_indent(lines[outer_else_line]));
        const std::string loop_body_indent = while_indent + "  ";
        const std::size_t continuation_start = next_nonempty(outer_else_line + 1);
        if (continuation_start >= lines.size() ||
            line_indent(lines[continuation_start]).size() <= while_indent.size()) {
            continue;
        }

        std::size_t wrapper_end_line = lines.size();
        std::size_t yield_line = previous_nonempty(outer_else_line);
        if (yield_line >= lines.size()) {
            continue;
        }
        if (trim_line(lines[yield_line]) == "end" &&
            line_indent(lines[yield_line]) == loop_body_indent) {
            wrapper_end_line = yield_line;
            yield_line = previous_nonempty(wrapper_end_line);
        }

        const std::size_t empty_if_end_line = previous_nonempty(yield_line);
        const std::size_t empty_if_line = previous_nonempty(empty_if_end_line);
        if (yield_line >= lines.size() || empty_if_end_line >= lines.size() ||
            empty_if_line >= lines.size() || trim_line(lines[yield_line]) != "coroutine.yield()" ||
            trim_line(lines[empty_if_end_line]) != "end") {
            continue;
        }

        const std::string empty_if_indent =
            wrapper_end_line < lines.size() ? loop_body_indent + "  " : loop_body_indent;
        std::string empty_if_condition;
        if (line_indent(lines[empty_if_line]) != empty_if_indent ||
            !parse_if_condition(lines[empty_if_line], &empty_if_condition) ||
            next_nonempty(empty_if_line + 1) != empty_if_end_line) {
            continue;
        }

        std::size_t while_line = lines.size();
        for (std::size_t probe = empty_if_line + 1; probe-- > 0;) {
            if (trim_line(lines[probe]) == "while true do" &&
                line_indent(lines[probe]) == while_indent) {
                while_line = probe;
                break;
            }
            if (probe == 0 || probe + 96 < empty_if_line) {
                break;
            }
        }
        if (while_line >= lines.size()) {
            continue;
        }

        if (wrapper_end_line < lines.size()) {
            bool found_wrapper_begin = false;
            for (std::size_t probe = empty_if_line; probe-- > while_line + 1;) {
                if (trim_line(lines[probe]) == "do" &&
                    line_indent(lines[probe]) == loop_body_indent) {
                    found_wrapper_begin = true;
                    break;
                }
                if (probe == while_line + 1) {
                    break;
                }
            }
            if (!found_wrapper_begin) {
                continue;
            }
        }

        std::size_t continuation_boundary = lines.size();
        for (std::size_t probe = continuation_start; probe < lines.size(); ++probe) {
            if (probe == continuation_start) {
                continue;
            }
            const std::string_view probe_trimmed = trim_line(lines[probe]);
            if (probe_trimmed.empty()) {
                continue;
            }
            if (line_indent(lines[probe]).size() <= while_indent.size()) {
                continuation_boundary = probe;
                break;
            }
        }

        const std::size_t continuation_last =
            previous_nonempty(continuation_boundary == lines.size() ? lines.size()
                                                                    : continuation_boundary);
        if (continuation_last < continuation_start || continuation_last >= lines.size()) {
            continue;
        }

        std::vector<std::string> candidate_lines;
        candidate_lines.reserve(lines.size() + 4);
        candidate_lines.insert(candidate_lines.end(),
                               lines.begin(),
                               lines.begin() + static_cast<std::ptrdiff_t>(empty_if_line + 1));
        candidate_lines.push_back(empty_if_indent + "  break");
        candidate_lines.push_back(lines[empty_if_end_line]);
        candidate_lines.push_back(lines[yield_line]);
        if (wrapper_end_line < lines.size()) {
            candidate_lines.push_back(lines[wrapper_end_line]);
        }
        candidate_lines.push_back(while_indent + "end");
        for (std::size_t probe = continuation_start; probe <= continuation_last; ++probe) {
            std::string moved_line = lines[probe];
            const std::string_view current_indent = line_indent(moved_line);
            if (current_indent.starts_with(loop_body_indent)) {
                moved_line = while_indent +
                             std::string(current_indent.substr(loop_body_indent.size())) +
                             std::string(moved_line.substr(current_indent.size()));
            }
            candidate_lines.push_back(std::move(moved_line));
        }
        if (continuation_boundary < lines.size()) {
            candidate_lines.insert(
                candidate_lines.end(),
                lines.begin() + static_cast<std::ptrdiff_t>(continuation_boundary),
                lines.end());
        }

        if (candidate_tracker.Consider(
                std::move(candidate_lines),
                "syntax_repaired_rebuilt_empty_break_wait_loop")) {
            return true;
        }
    }

    return candidate_tracker.CommitBestProgress(
        "syntax_repaired_rebuilt_empty_break_wait_loop_progress");
}

bool TryCloseOuterElseAfterNestedYieldWaitLoop(std::string& decompiled_source,
                                               std::string_view validation_error,
                                               std::string* note) {
    if ((validation_error.find("'end' expected") == std::string_view::npos &&
         validation_error.find("'<eof>' expected") == std::string_view::npos) ||
        validation_error.find("near 'else'") == std::string_view::npos) {
        return false;
    }

    bool had_trailing_newline = false;
    const std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);
    if (lines.size() < 14) {
        return false;
    }

    const std::size_t reported_line = ExtractLuaErrorLine(validation_error);
    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimWhitespace(refii::lua::StripLuaLineComment(line));
    };
    const auto line_indent = [](std::string_view line) -> std::string_view {
        const std::size_t first_non_ws = line.find_first_not_of(" \t");
        if (first_non_ws == std::string_view::npos) {
            return line;
        }
        return line.substr(0, first_non_ws);
    };
    const auto next_nonempty = [&](std::size_t start) -> std::size_t {
        for (std::size_t i = start; i < lines.size(); ++i) {
            if (!trim_line(lines[i]).empty()) {
                return i;
            }
        }
        return lines.size();
    };
    const auto previous_nonempty = [&](std::size_t start) -> std::size_t {
        if (start == 0 || start > lines.size()) {
            return lines.size();
        }
        for (std::size_t i = start; i-- > 0;) {
            if (!trim_line(lines[i]).empty()) {
                return i;
            }
            if (i == 0) {
                break;
            }
        }
        return lines.size();
    };
    const auto parse_if_condition = [](std::string_view line, std::string* condition) -> bool {
        if (condition == nullptr) {
            return false;
        }

        const std::string_view trimmed = refii::lua::TrimLuaWhitespace(
            refii::lua::StripLuaLineComment(line));
        if (!StartsWith(trimmed, "if ") || !EndsWith(trimmed, " then")) {
            return false;
        }

        const std::string_view extracted =
            refii::lua::TrimLuaWhitespace(trimmed.substr(3, trimmed.size() - 8));
        if (extracted.empty()) {
            return false;
        }

        *condition = std::string(extracted);
        return true;
    };
    const auto looks_like_carry_statement = [&](std::string_view line) {
        const std::string_view trimmed = trim_line(line);
        if (trimmed.empty() || trimmed == "end" || trimmed == "else" ||
            StartsWith(trimmed, "elseif ") || StartsWith(trimmed, "if ") ||
            StartsWith(trimmed, "while ") || StartsWith(trimmed, "for ") ||
            StartsWith(trimmed, "repeat") || StartsWith(trimmed, "return ")) {
            return false;
        }
        return true;
    };

    SyntaxRepairCandidateTracker candidate_tracker(validation_error,
                                                   had_trailing_newline,
                                                   &decompiled_source,
                                                   note);

    for (std::size_t outer_else_line = 0; outer_else_line < lines.size(); ++outer_else_line) {
        if (trim_line(lines[outer_else_line]) != "else") {
            continue;
        }
        if (reported_line != 0) {
            const std::size_t current_line = outer_else_line + 1;
            if (current_line + 64 < reported_line || current_line > reported_line + 8) {
                continue;
            }
        }

        const std::string outer_indent(line_indent(lines[outer_else_line]));
        const std::size_t outer_yield_line = previous_nonempty(outer_else_line);
        if (outer_yield_line >= lines.size() ||
            trim_line(lines[outer_yield_line]) != "coroutine.yield()") {
            continue;
        }

        const std::string outer_body_indent(line_indent(lines[outer_yield_line]));
        if (outer_body_indent.size() <= outer_indent.size() ||
            !StartsWith(outer_body_indent, outer_indent)) {
            continue;
        }

        const std::size_t carry_line = previous_nonempty(outer_yield_line);
        if (carry_line >= lines.size() || line_indent(lines[carry_line]) != outer_body_indent ||
            !looks_like_carry_statement(lines[carry_line])) {
            continue;
        }

        const std::size_t inner_wait_end_line = previous_nonempty(carry_line);
        const std::size_t inner_yield_line = previous_nonempty(inner_wait_end_line);
        const std::size_t empty_if_end_line = previous_nonempty(inner_yield_line);
        const std::size_t empty_if_line = previous_nonempty(empty_if_end_line);
        const std::size_t inner_while_line = previous_nonempty(empty_if_line);
        if (inner_wait_end_line >= lines.size() || inner_yield_line >= lines.size() ||
            empty_if_end_line >= lines.size() || empty_if_line >= lines.size() ||
            inner_while_line >= lines.size()) {
            continue;
        }
        if (trim_line(lines[inner_wait_end_line]) != "end" ||
            trim_line(lines[inner_yield_line]) != "coroutine.yield()" ||
            trim_line(lines[empty_if_end_line]) != "end") {
            continue;
        }

        std::string empty_if_condition;
        if (!parse_if_condition(lines[empty_if_line], &empty_if_condition) ||
            next_nonempty(empty_if_line + 1) != empty_if_end_line) {
            continue;
        }

        const std::string inner_wait_indent(line_indent(lines[inner_wait_end_line]));
        if (line_indent(lines[inner_while_line]) != inner_wait_indent ||
            !StartsWith(trim_line(lines[inner_while_line]), "while ") ||
            !EndsWith(trim_line(lines[inner_while_line]), " do")) {
            continue;
        }

        if (line_indent(lines[inner_yield_line]).size() <= inner_wait_indent.size() ||
            line_indent(lines[empty_if_line]).size() <= inner_wait_indent.size()) {
            continue;
        }

        std::vector<std::string> candidate_lines = lines;
        candidate_lines.insert(candidate_lines.begin() +
                                   static_cast<std::ptrdiff_t>(outer_yield_line),
                               outer_indent + "end");
        candidate_lines[outer_else_line + 1] = outer_indent + "end";

        if (candidate_tracker.Consider(
                std::move(candidate_lines),
                "syntax_repaired_closed_outer_else_after_nested_yield_wait_loop")) {
            return true;
        }
    }

    return candidate_tracker.CommitBestProgress(
        "syntax_repaired_closed_outer_else_after_nested_yield_wait_loop_progress");
}

bool TryCollapseWhileTrueGuardedElseEpilogue(std::string& decompiled_source,
                                            std::string_view validation_error,
                                            std::string* note) {
    if ((validation_error.find("'end' expected") == std::string_view::npos &&
         validation_error.find("'<eof>' expected") == std::string_view::npos) ||
        (validation_error.find("near 'else'") == std::string_view::npos &&
         validation_error.find("near 'elseif'") == std::string_view::npos)) {
        return false;
    }

    bool had_trailing_newline = false;
    const std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);
    if (lines.size() < 12) {
        return false;
    }

    const std::size_t reported_line = ExtractLuaErrorLine(validation_error);
    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimWhitespace(refii::lua::StripLuaLineComment(line));
    };
    const auto line_indent = [](std::string_view line) -> std::string_view {
        const std::size_t first_non_ws = line.find_first_not_of(" \t");
        if (first_non_ws == std::string_view::npos) {
            return line;
        }
        return line.substr(0, first_non_ws);
    };
    const auto next_nonempty = [&](std::size_t start) -> std::size_t {
        for (std::size_t i = start; i < lines.size(); ++i) {
            if (!trim_line(lines[i]).empty()) {
                return i;
            }
        }
        return lines.size();
    };
    const auto previous_nonempty = [&](std::size_t start) -> std::size_t {
        if (start == 0 || start > lines.size()) {
            return lines.size();
        }
        for (std::size_t i = start; i-- > 0;) {
            if (!trim_line(lines[i]).empty()) {
                return i;
            }
            if (i == 0) {
                break;
            }
        }
        return lines.size();
    };
    const auto parse_if_condition = [](std::string_view line, std::string* condition) -> bool {
        if (condition == nullptr) {
            return false;
        }

        const std::string_view trimmed = refii::lua::TrimLuaWhitespace(
            refii::lua::StripLuaLineComment(line));
        if (!StartsWith(trimmed, "if ") || !EndsWith(trimmed, " then")) {
            return false;
        }

        const std::string_view extracted =
            refii::lua::TrimLuaWhitespace(trimmed.substr(3, trimmed.size() - 8));
        if (extracted.empty()) {
            return false;
        }

        *condition = std::string(extracted);
        return true;
    };
    const auto looks_like_simple_epilogue = [&](std::string_view line) {
        const std::string_view trimmed = trim_line(line);
        if (trimmed.empty() || trimmed == "end" || trimmed == "else" ||
            StartsWith(trimmed, "elseif ") || StartsWith(trimmed, "if ") ||
            StartsWith(trimmed, "while ") || StartsWith(trimmed, "for ") ||
            StartsWith(trimmed, "repeat") || StartsWith(trimmed, "function ") ||
            StartsWith(trimmed, "local function")) {
            return false;
        }
        return true;
    };
    SyntaxRepairCandidateTracker candidate_tracker(validation_error,
                                                   had_trailing_newline,
                                                   &decompiled_source,
                                                   note);

    for (std::size_t else_line = 0; else_line < lines.size(); ++else_line) {
        if (trim_line(lines[else_line]) != "else") {
            continue;
        }
        if (reported_line != 0) {
            const std::size_t current_line = else_line + 1;
            if (current_line + 64 < reported_line || current_line > reported_line + 8) {
                continue;
            }
        }

        std::size_t while_line = lines.size();
        std::size_t guard_if_line = lines.size();
        std::string guard_condition;
        for (std::size_t probe = else_line; probe-- > 0;) {
            if (else_line - probe > 160) {
                break;
            }
            if (trim_line(lines[probe]) != "while true do") {
                if (probe == 0) {
                    break;
                }
                continue;
            }
            const std::size_t possible_if_line = next_nonempty(probe + 1);
            if (possible_if_line >= else_line) {
                continue;
            }
            if (!parse_if_condition(lines[possible_if_line], &guard_condition)) {
                continue;
            }
            while_line = probe;
            guard_if_line = possible_if_line;
            break;
        }
        if (while_line >= lines.size() || guard_if_line >= lines.size()) {
            continue;
        }

        const std::string while_indent(line_indent(lines[while_line]));
        const std::string guard_indent(line_indent(lines[guard_if_line]));
        const std::string else_indent(line_indent(lines[else_line]));
        if (guard_indent.size() <= while_indent.size() ||
            !StartsWith(guard_indent, while_indent) ||
            else_indent.size() <= while_indent.size()) {
            continue;
        }

        std::size_t body_start = guard_if_line + 1;
        if (body_start >= else_line) {
            continue;
        }

        bool saw_body_yield = false;
        for (std::size_t probe = body_start; probe < else_line; ++probe) {
            if (trim_line(lines[probe]) == "coroutine.yield()") {
                saw_body_yield = true;
                break;
            }
        }
        if (!saw_body_yield) {
            continue;
        }

        const std::size_t epilogue_start = next_nonempty(else_line + 1);
        if (epilogue_start >= lines.size()) {
            continue;
        }

        std::size_t else_end_line = lines.size();
        std::size_t epilogue_last = lines.size();
        bool saw_simple_epilogue = false;
        bool boundary_is_explicit_end = false;
        for (std::size_t probe = epilogue_start; probe < lines.size() && probe <= else_line + 24;
             ++probe) {
            const std::string_view trimmed = trim_line(lines[probe]);
            if (trimmed.empty()) {
                continue;
            }
            if (trimmed == "end" && line_indent(lines[probe]).size() <= else_indent.size()) {
                else_end_line = probe;
                boundary_is_explicit_end = true;
                break;
            }
            if (StartsWith(trimmed, "elseif ") &&
                line_indent(lines[probe]).size() <= else_indent.size()) {
                else_end_line = probe;
                break;
            }
            if (!looks_like_simple_epilogue(lines[probe])) {
                saw_simple_epilogue = false;
                else_end_line = lines.size();
                break;
            }
            saw_simple_epilogue = true;
            epilogue_last = probe;
        }
        if (!saw_simple_epilogue || else_end_line >= lines.size() ||
            epilogue_last < epilogue_start) {
            continue;
        }

        std::string epilogue_base_indent;
        for (std::size_t probe = epilogue_start; probe <= epilogue_last; ++probe) {
            const std::string_view trimmed = trim_line(lines[probe]);
            if (!trimmed.empty()) {
                epilogue_base_indent = std::string(line_indent(lines[probe]));
                break;
            }
        }
        if (epilogue_base_indent.empty()) {
            continue;
        }

        std::vector<std::string> body_lines(
            lines.begin() + static_cast<std::ptrdiff_t>(body_start),
            lines.begin() + static_cast<std::ptrdiff_t>(else_line));
        const auto previous_nonempty_body_line =
            [&](std::size_t start) -> std::size_t {
                if (start == 0 || start > body_lines.size()) {
                    return body_lines.size();
                }
                for (std::size_t i = start; i-- > 0;) {
                    if (!trim_line(body_lines[i]).empty()) {
                        return i;
                    }
                    if (i == 0) {
                        break;
                    }
                }
                return body_lines.size();
            };
        for (std::size_t local_else = 0; local_else < body_lines.size(); ++local_else) {
            if (trim_line(body_lines[local_else]) != "else") {
                continue;
            }
            const std::size_t local_end_1 = previous_nonempty_body_line(local_else);
            const std::size_t local_end_2 = previous_nonempty_body_line(local_end_1);
            if (local_end_1 >= body_lines.size() || local_end_2 >= body_lines.size() ||
                trim_line(body_lines[local_end_1]) != "end" ||
                trim_line(body_lines[local_end_2]) != "end") {
                continue;
            }
            const std::string_view local_else_indent = line_indent(body_lines[local_else]);
            if (line_indent(body_lines[local_end_1]) != local_else_indent ||
                line_indent(body_lines[local_end_2]) != local_else_indent) {
                continue;
            }
            body_lines.erase(body_lines.begin() + static_cast<std::ptrdiff_t>(local_end_2),
                             body_lines.begin() +
                                 static_cast<std::ptrdiff_t>(local_end_1 + 1));
            break;
        }

        std::vector<std::string> candidate_lines;
        candidate_lines.reserve(lines.size() + 4);
        candidate_lines.insert(candidate_lines.end(),
                               lines.begin(),
                               lines.begin() + static_cast<std::ptrdiff_t>(while_line));
        candidate_lines.push_back(while_indent + "while " + guard_condition + " do");
        candidate_lines.insert(candidate_lines.end(), body_lines.begin(), body_lines.end());
        candidate_lines.push_back(std::string(while_indent) + "end");
        for (std::size_t probe = epilogue_start; probe <= epilogue_last; ++probe) {
            std::string moved_line = lines[probe];
            const std::string_view current_indent = line_indent(moved_line);
            if (current_indent.starts_with(epilogue_base_indent)) {
                moved_line = while_indent +
                             std::string(current_indent.substr(epilogue_base_indent.size())) +
                             std::string(moved_line.substr(current_indent.size()));
            }
            candidate_lines.push_back(std::move(moved_line));
        }
        candidate_lines.insert(candidate_lines.end(),
                               lines.begin() + static_cast<std::ptrdiff_t>(
                                   else_end_line + (boundary_is_explicit_end ? 1 : 0)),
                               lines.end());

        if (candidate_tracker.Consider(
                std::move(candidate_lines),
                "syntax_repaired_collapsed_while_true_guarded_else_epilogue")) {
            return true;
        }
    }

    return candidate_tracker.CommitBestProgress(
        "syntax_repaired_collapsed_while_true_guarded_else_epilogue_progress");
}

bool TryRewriteInnerPollLoopAsIfElseWithFooter(std::string& decompiled_source,
                                               std::string_view validation_error,
                                               std::string* note) {
    if ((validation_error.find("'end' expected") == std::string_view::npos &&
         validation_error.find("'<eof>' expected") == std::string_view::npos) ||
        (validation_error.find("near 'end'") == std::string_view::npos &&
         validation_error.find("near 'else'") == std::string_view::npos)) {
        return false;
    }

    bool had_trailing_newline = false;
    const std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);
    if (lines.size() < 18) {
        return false;
    }

    const std::size_t reported_line = ExtractLuaErrorLine(validation_error);
    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimWhitespace(refii::lua::StripLuaLineComment(line));
    };
    const auto line_indent = [](std::string_view line) -> std::string_view {
        const std::size_t first_non_ws = line.find_first_not_of(" \t");
        if (first_non_ws == std::string_view::npos) {
            return line;
        }
        return line.substr(0, first_non_ws);
    };
    const auto next_nonempty = [&](std::size_t start) -> std::size_t {
        for (std::size_t i = start; i < lines.size(); ++i) {
            if (!trim_line(lines[i]).empty()) {
                return i;
            }
        }
        return lines.size();
    };
    const auto previous_nonempty = [&](std::size_t start) -> std::size_t {
        if (start == 0 || start > lines.size()) {
            return lines.size();
        }
        for (std::size_t i = start; i-- > 0;) {
            if (!trim_line(lines[i]).empty()) {
                return i;
            }
            if (i == 0) {
                break;
            }
        }
        return lines.size();
    };
    const auto parse_while_condition = [](std::string_view line, std::string* condition) -> bool {
        if (condition == nullptr) {
            return false;
        }

        const std::string_view trimmed = refii::lua::TrimLuaWhitespace(
            refii::lua::StripLuaLineComment(line));
        if (!StartsWith(trimmed, "while ") || !EndsWith(trimmed, " do")) {
            return false;
        }

        const std::string_view extracted =
            refii::lua::TrimLuaWhitespace(trimmed.substr(6, trimmed.size() - 9));
        if (extracted.empty()) {
            return false;
        }

        *condition = std::string(extracted);
        return true;
    };
    const auto looks_like_simple_footer = [&](std::string_view line) {
        const std::string_view trimmed = trim_line(line);
        if (trimmed.empty() || trimmed == "end" || trimmed == "else" ||
            StartsWith(trimmed, "elseif ") || StartsWith(trimmed, "if ") ||
            StartsWith(trimmed, "while ") || StartsWith(trimmed, "for ") ||
            StartsWith(trimmed, "repeat") || StartsWith(trimmed, "function ") ||
            StartsWith(trimmed, "local function")) {
            return false;
        }
        return true;
    };

    SyntaxRepairCandidateTracker candidate_tracker(validation_error,
                                                   had_trailing_newline,
                                                   &decompiled_source,
                                                   note);

    for (std::size_t else_line = 0; else_line < lines.size(); ++else_line) {
        if (trim_line(lines[else_line]) != "else") {
            continue;
        }
        if (reported_line != 0) {
            const std::size_t current_line = else_line + 1;
            if (current_line + 64 < reported_line || current_line > reported_line + 16) {
                continue;
            }
        }

        const std::string outer_else_indent(line_indent(lines[else_line]));
        if (outer_else_indent.size() < 2) {
            continue;
        }
        const std::string outer_indent = outer_else_indent.substr(0, outer_else_indent.size() - 2);

        const std::size_t footer_start = next_nonempty(else_line + 1);
        if (footer_start >= lines.size() ||
            !looks_like_simple_footer(lines[footer_start]) ||
            line_indent(lines[footer_start]).size() <= outer_else_indent.size()) {
            continue;
        }

        const std::string footer_base_indent(line_indent(lines[footer_start]));
        std::size_t footer_end_line = lines.size();
        for (std::size_t probe = footer_start + 1; probe < lines.size(); ++probe) {
            const std::string_view trimmed = trim_line(lines[probe]);
            if (trimmed.empty()) {
                continue;
            }
            if (trimmed == "end" && line_indent(lines[probe]).size() <= footer_base_indent.size()) {
                footer_end_line = probe;
                break;
            }
            if (!looks_like_simple_footer(lines[probe])) {
                footer_end_line = lines.size();
                break;
            }
        }
        if (footer_end_line >= lines.size()) {
            continue;
        }
        const std::size_t footer_last = previous_nonempty(footer_end_line);
        if (footer_last < footer_start || footer_last >= lines.size()) {
            continue;
        }

        std::size_t top_level_boundary = lines.size();
        for (std::size_t probe = footer_end_line + 1; probe < lines.size(); ++probe) {
            if (lines[probe].find_first_not_of(" \t") != 0) {
                continue;
            }
            if (LooksLikeTopLevelBoundary(trim_line(lines[probe]))) {
                top_level_boundary = probe;
                break;
            }
        }
        if (top_level_boundary >= lines.size()) {
            continue;
        }

        const std::size_t preserved_end_line = previous_nonempty(top_level_boundary);
        if (preserved_end_line >= lines.size() ||
            trim_line(lines[preserved_end_line]) != "end" ||
            line_indent(lines[preserved_end_line]).size() >= outer_indent.size()) {
            continue;
        }

        std::size_t extra_end_count = 0;
        for (std::size_t probe = footer_end_line; probe < preserved_end_line; ++probe) {
            const std::string_view trimmed = trim_line(lines[probe]);
            if (trimmed.empty()) {
                continue;
            }
            if (trimmed == "end") {
                ++extra_end_count;
            }
        }
        if (extra_end_count < 2) {
            continue;
        }

        std::size_t outer_while_line = lines.size();
        for (std::size_t probe = else_line; probe-- > 0;) {
            if (trim_line(lines[probe]) == "while true do" &&
                line_indent(lines[probe]) == outer_indent) {
                outer_while_line = probe;
                break;
            }
            if (probe == 0 || probe + 96 < else_line) {
                break;
            }
        }
        if (outer_while_line >= lines.size()) {
            continue;
        }

        const std::size_t inner_poll_line = next_nonempty(outer_while_line + 1);
        if (inner_poll_line >= else_line) {
            continue;
        }
        const std::string inner_poll_indent(line_indent(lines[inner_poll_line]));
        if (inner_poll_indent.size() <= outer_else_indent.size() ||
            !StartsWith(inner_poll_indent, outer_indent)) {
            continue;
        }

        std::string guard_condition;
        if (!parse_while_condition(lines[inner_poll_line], &guard_condition)) {
            continue;
        }

        std::size_t inner_poll_end_line = lines.size();
        for (std::size_t probe = inner_poll_line + 1; probe < else_line; ++probe) {
            if (trim_line(lines[probe]) == "end" &&
                line_indent(lines[probe]) == inner_poll_indent) {
                inner_poll_end_line = probe;
                break;
            }
        }
        if (inner_poll_end_line >= else_line) {
            continue;
        }

        bool body_has_yield = false;
        for (std::size_t probe = inner_poll_line + 1; probe < inner_poll_end_line; ++probe) {
            if (trim_line(lines[probe]) == "coroutine.yield()") {
                body_has_yield = true;
                break;
            }
        }
        if (!body_has_yield) {
            continue;
        }

        const std::size_t epilogue_start = next_nonempty(inner_poll_end_line + 1);
        if (epilogue_start >= else_line) {
            continue;
        }

        std::size_t if_end_line = lines.size();
        for (std::size_t probe = epilogue_start; probe < else_line; ++probe) {
            if (trim_line(lines[probe]) == "end" &&
                line_indent(lines[probe]) == outer_else_indent) {
                if_end_line = probe;
                break;
            }
        }
        if (if_end_line >= else_line) {
            continue;
        }
        const std::size_t epilogue_last = previous_nonempty(if_end_line);
        if (epilogue_last < epilogue_start || epilogue_last >= lines.size()) {
            continue;
        }

        std::string body_base_indent;
        for (std::size_t probe = inner_poll_line + 1; probe < inner_poll_end_line; ++probe) {
            if (!trim_line(lines[probe]).empty()) {
                body_base_indent = std::string(line_indent(lines[probe]));
                break;
            }
        }
        if (body_base_indent.empty()) {
            continue;
        }

        std::string epilogue_base_indent;
        for (std::size_t probe = epilogue_start; probe <= epilogue_last; ++probe) {
            if (!trim_line(lines[probe]).empty()) {
                epilogue_base_indent = std::string(line_indent(lines[probe]));
                break;
            }
        }
        if (epilogue_base_indent.empty()) {
            continue;
        }

        const std::string new_body_indent = outer_else_indent + "  ";
        std::vector<std::string> candidate_lines;
        candidate_lines.reserve(lines.size() + 8);
        candidate_lines.insert(candidate_lines.end(),
                               lines.begin(),
                               lines.begin() + static_cast<std::ptrdiff_t>(outer_while_line));
        candidate_lines.push_back(lines[outer_while_line]);
        candidate_lines.push_back(outer_else_indent + "if " + guard_condition + " then");
        for (std::size_t probe = inner_poll_line + 1; probe < inner_poll_end_line; ++probe) {
            std::string moved_line = lines[probe];
            const std::string_view current_indent = line_indent(moved_line);
            if (current_indent.starts_with(body_base_indent)) {
                moved_line = new_body_indent +
                             std::string(current_indent.substr(body_base_indent.size())) +
                             std::string(moved_line.substr(current_indent.size()));
            }
            candidate_lines.push_back(std::move(moved_line));
        }
        candidate_lines.push_back(outer_else_indent + "else");
        for (std::size_t probe = epilogue_start; probe <= epilogue_last; ++probe) {
            std::string moved_line = lines[probe];
            const std::string_view current_indent = line_indent(moved_line);
            if (current_indent.starts_with(epilogue_base_indent)) {
                moved_line = new_body_indent +
                             std::string(current_indent.substr(epilogue_base_indent.size())) +
                             std::string(moved_line.substr(current_indent.size()));
            }
            candidate_lines.push_back(std::move(moved_line));
        }
        candidate_lines.push_back(outer_else_indent + "end");
        candidate_lines.push_back(outer_indent + "end");
        for (std::size_t probe = footer_start; probe <= footer_last; ++probe) {
            std::string moved_line = lines[probe];
            const std::string_view current_indent = line_indent(moved_line);
            if (current_indent.starts_with(footer_base_indent)) {
                moved_line = outer_indent +
                             std::string(current_indent.substr(footer_base_indent.size())) +
                             std::string(moved_line.substr(current_indent.size()));
            }
            candidate_lines.push_back(std::move(moved_line));
        }
        candidate_lines.push_back(lines[preserved_end_line]);
        candidate_lines.insert(candidate_lines.end(),
                               lines.begin() + static_cast<std::ptrdiff_t>(
                                   preserved_end_line + 1),
                               lines.end());

        if (candidate_tracker.Consider(
                std::move(candidate_lines),
                "syntax_repaired_rewrote_inner_poll_loop_as_if_else_with_footer")) {
            return true;
        }
    }

    return candidate_tracker.CommitBestProgress(
        "syntax_repaired_rewrote_inner_poll_loop_as_if_else_with_footer_progress");
}

bool TryRebuildLevelLoadedWhileNotIntoIfElseWhile(std::string& decompiled_source,
                                                  std::string_view validation_error,
                                                  std::string* note) {
    if ((validation_error.find("'end' expected") == std::string_view::npos &&
         validation_error.find("'<eof>' expected") == std::string_view::npos) ||
        validation_error.find("near 'else'") == std::string_view::npos) {
        return false;
    }

    bool had_trailing_newline = false;
    const std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);
    if (lines.size() < 24) {
        return false;
    }

    const std::size_t reported_line = ExtractLuaErrorLine(validation_error);
    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimWhitespace(refii::lua::StripLuaLineComment(line));
    };
    const auto line_indent = [](std::string_view line) -> std::string_view {
        const std::size_t first_non_ws = line.find_first_not_of(" \t");
        if (first_non_ws == std::string_view::npos) {
            return line;
        }
        return line.substr(0, first_non_ws);
    };
    const auto next_nonempty = [&](std::size_t start) -> std::size_t {
        for (std::size_t i = start; i < lines.size(); ++i) {
            if (!trim_line(lines[i]).empty()) {
                return i;
            }
        }
        return lines.size();
    };
    const auto previous_nonempty = [&](std::size_t start) -> std::size_t {
        if (start == 0 || start > lines.size()) {
            return lines.size();
        }
        for (std::size_t i = start; i-- > 0;) {
            if (!trim_line(lines[i]).empty()) {
                return i;
            }
            if (i == 0) {
                break;
            }
        }
        return lines.size();
    };
    const auto parse_while_not_condition = [](std::string_view line, std::string* condition) {
        if (condition == nullptr) {
            return false;
        }
        const std::string_view trimmed = refii::lua::TrimLuaWhitespace(
            refii::lua::StripLuaLineComment(line));
        if (!StartsWith(trimmed, "while not ") || !EndsWith(trimmed, " do")) {
            return false;
        }
        const std::string_view extracted =
            refii::lua::TrimLuaWhitespace(trimmed.substr(10, trimmed.size() - 13));
        if (extracted.empty()) {
            return false;
        }
        *condition = std::string(extracted);
        return true;
    };
    const auto parse_if_condition = [](std::string_view line, std::string* condition) {
        if (condition == nullptr) {
            return false;
        }
        const std::string_view trimmed = refii::lua::TrimLuaWhitespace(
            refii::lua::StripLuaLineComment(line));
        if (!StartsWith(trimmed, "if ") || !EndsWith(trimmed, " then")) {
            return false;
        }
        const std::string_view extracted =
            refii::lua::TrimLuaWhitespace(trimmed.substr(3, trimmed.size() - 8));
        if (extracted.empty()) {
            return false;
        }
        *condition = std::string(extracted);
        return true;
    };
    const auto looks_like_simple_footer = [&](std::string_view line) {
        const std::string_view trimmed = trim_line(line);
        if (trimmed.empty() || trimmed == "end" || trimmed == "else" ||
            StartsWith(trimmed, "elseif ") || StartsWith(trimmed, "if ") ||
            StartsWith(trimmed, "while ") || StartsWith(trimmed, "for ") ||
            StartsWith(trimmed, "repeat") || StartsWith(trimmed, "function ") ||
            StartsWith(trimmed, "local function")) {
            return false;
        }
        return true;
    };

    SyntaxRepairCandidateTracker candidate_tracker(validation_error,
                                                   had_trailing_newline,
                                                   &decompiled_source,
                                                   note);
    for (std::size_t second_else_line = 0; second_else_line < lines.size(); ++second_else_line) {
        if (trim_line(lines[second_else_line]) != "else") {
            continue;
        }
        if (reported_line != 0) {
            const std::size_t current_line = second_else_line + 1;
            if (current_line + 64 < reported_line || current_line > reported_line + 8) {
                continue;
            }
        }

        const std::string function_body_indent(line_indent(lines[second_else_line]));
        const std::size_t epilogue_start = next_nonempty(second_else_line + 1);
        if (epilogue_start >= lines.size()) {
            continue;
        }

        std::size_t function_end_line = lines.size();
        for (std::size_t probe = epilogue_start + 1; probe < lines.size(); ++probe) {
            const std::string_view trimmed = trim_line(lines[probe]);
            if (trimmed == "end" && line_indent(lines[probe]) == function_body_indent) {
                function_end_line = probe;
                break;
            }
        }
        if (function_end_line >= lines.size()) {
            continue;
        }

        const std::size_t epilogue_last = previous_nonempty(function_end_line);
        if (epilogue_last < epilogue_start) {
            continue;
        }

        bool epilogue_ok = false;
        for (std::size_t probe = epilogue_start; probe <= epilogue_last; ++probe) {
            const std::string_view trimmed = trim_line(lines[probe]);
            if (trimmed == "end") {
                continue;
            }
            if (!looks_like_simple_footer(lines[probe])) {
                epilogue_ok = false;
                break;
            }
            epilogue_ok = true;
        }
        if (!epilogue_ok) {
            continue;
        }

        std::size_t first_else_line = lines.size();
        for (std::size_t probe = second_else_line; probe-- > 0;) {
            if (second_else_line - probe > 32) {
                break;
            }
            if (trim_line(lines[probe]) == "else" &&
                line_indent(lines[probe]) == function_body_indent) {
                first_else_line = probe;
                break;
            }
            if (probe == 0) {
                break;
            }
        }
        if (first_else_line >= lines.size()) {
            continue;
        }

        const std::size_t short_footer_start = next_nonempty(first_else_line + 1);
        const std::size_t short_footer_last = previous_nonempty(second_else_line);
        if (short_footer_start >= second_else_line || short_footer_last < short_footer_start) {
            continue;
        }

        bool short_footer_has_yield = false;
        bool short_footer_ok = true;
        for (std::size_t probe = short_footer_start; probe <= short_footer_last; ++probe) {
            const std::string_view trimmed = trim_line(lines[probe]);
            if (trimmed == "coroutine.yield()") {
                short_footer_has_yield = true;
            }
            if (!looks_like_simple_footer(lines[probe])) {
                short_footer_ok = false;
                break;
            }
        }
        if (!short_footer_ok || !short_footer_has_yield) {
            continue;
        }

        const std::string outer_if_indent = function_body_indent + "  ";
        const std::string inner_while_indent = outer_if_indent + "  ";

        std::size_t if_loaded_line = lines.size();
        std::string loaded_condition;
        for (std::size_t probe = first_else_line; probe-- > 0;) {
            if (first_else_line - probe > 384) {
                break;
            }
            if (line_indent(lines[probe]) != outer_if_indent) {
                continue;
            }
            if (parse_if_condition(lines[probe], &loaded_condition) &&
                loaded_condition.find("IsLevelLoaded(") == 0 &&
                loaded_condition.find(" and ") == std::string::npos &&
                loaded_condition.find(" or ") == std::string::npos) {
                if_loaded_line = probe;
                break;
            }
            if (probe == 0) {
                break;
            }
        }
        if (if_loaded_line >= lines.size()) {
            continue;
        }

        const std::size_t wait_loop_end_line = previous_nonempty(if_loaded_line);
        if (wait_loop_end_line >= lines.size() ||
            trim_line(lines[wait_loop_end_line]) != "end" ||
            line_indent(lines[wait_loop_end_line]) != outer_if_indent) {
            continue;
        }

        std::size_t while_not_line = lines.size();
        std::string not_loaded_condition;
        for (std::size_t probe = wait_loop_end_line; probe-- > 0;) {
            if (wait_loop_end_line - probe > 16) {
                break;
            }
            if (line_indent(lines[probe]) != outer_if_indent) {
                continue;
            }
            if (parse_while_not_condition(lines[probe], &not_loaded_condition)) {
                while_not_line = probe;
                break;
            }
            if (probe == 0) {
                break;
            }
        }
        if (while_not_line >= lines.size() || not_loaded_condition != loaded_condition) {
            continue;
        }

        const std::size_t wait_loop_body_start = next_nonempty(while_not_line + 1);
        if (wait_loop_body_start >= wait_loop_end_line) {
            continue;
        }

        std::vector<std::string> candidate_lines;
        candidate_lines.reserve(lines.size() + 16);
        candidate_lines.insert(candidate_lines.end(),
                               lines.begin(),
                               lines.begin() + static_cast<std::ptrdiff_t>(while_not_line));
        candidate_lines.push_back(outer_if_indent + "if not " + loaded_condition + " then");
        candidate_lines.insert(candidate_lines.end(),
                               lines.begin() + static_cast<std::ptrdiff_t>(wait_loop_body_start),
                               lines.begin() + static_cast<std::ptrdiff_t>(wait_loop_end_line));
        candidate_lines.push_back(outer_if_indent + "else");
        candidate_lines.push_back(inner_while_indent + "while " + loaded_condition + " do");

        for (std::size_t probe = if_loaded_line + 1; probe < first_else_line; ++probe) {
            std::string moved_line = lines[probe];
            if (!moved_line.empty()) {
                moved_line = "  " + moved_line;
            }
            candidate_lines.push_back(std::move(moved_line));
        }

        candidate_lines.push_back(inner_while_indent + "end");

        for (std::size_t probe = short_footer_start; probe <= short_footer_last; ++probe) {
            std::string moved_line = lines[probe];
            if (!moved_line.empty()) {
                moved_line = "  " + moved_line;
            }
            candidate_lines.push_back(std::move(moved_line));
        }

        candidate_lines.push_back(outer_if_indent + "end");

        for (std::size_t probe = epilogue_start; probe <= epilogue_last; ++probe) {
            const std::string_view trimmed = trim_line(lines[probe]);
            if (trimmed == "end") {
                continue;
            }
            candidate_lines.push_back(function_body_indent + std::string(trimmed));
        }

        candidate_lines.insert(candidate_lines.end(),
                               lines.begin() + static_cast<std::ptrdiff_t>(function_end_line),
                               lines.end());

        if (candidate_tracker.Consider(
                std::move(candidate_lines),
                "syntax_repaired_rebuilt_level_loaded_while_not_into_if_else_while")) {
            return true;
        }
    }

    return candidate_tracker.CommitBestProgress(
        "syntax_repaired_rebuilt_level_loaded_while_not_into_if_else_while_progress");
}

bool TryReplaceDanglingElseBeforeStateLadderWithEnd(std::string& decompiled_source,
                                                    std::string_view validation_error,
                                                    std::string* note) {
    if ((validation_error.find("'end' expected") == std::string_view::npos &&
         validation_error.find("'<eof>' expected") == std::string_view::npos) ||
        validation_error.find("near 'else'") == std::string_view::npos) {
        return false;
    }

    bool had_trailing_newline = false;
    const std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);
    if (lines.size() < 10) {
        return false;
    }

    const std::size_t reported_line = ExtractLuaErrorLine(validation_error);
    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimWhitespace(refii::lua::StripLuaLineComment(line));
    };
    const auto line_indent = [](std::string_view line) -> std::string_view {
        const std::size_t first_non_ws = line.find_first_not_of(" \t");
        if (first_non_ws == std::string_view::npos) {
            return line;
        }
        return line.substr(0, first_non_ws);
    };
    const auto next_nonempty = [&](std::size_t start) -> std::size_t {
        for (std::size_t i = start; i < lines.size(); ++i) {
            if (!trim_line(lines[i]).empty()) {
                return i;
            }
        }
        return lines.size();
    };
    const auto previous_nonempty = [&](std::size_t start) -> std::size_t {
        if (start == 0 || start > lines.size()) {
            return lines.size();
        }
        for (std::size_t i = start; i-- > 0;) {
            if (!trim_line(lines[i]).empty()) {
                return i;
            }
            if (i == 0) {
                break;
            }
        }
        return lines.size();
    };
    const auto extract_assignment_lhs = [&](std::string_view line) -> std::string {
        const std::string_view trimmed = trim_line(line);
        const std::size_t equal = trimmed.find('=');
        if (equal == std::string_view::npos) {
            return {};
        }
        const std::string_view lhs = TrimWhitespace(trimmed.substr(0, equal));
        if (lhs.find("State") == std::string_view::npos &&
            lhs.find("ThreadVar") == std::string_view::npos) {
            return {};
        }
        return std::string(lhs);
    };
    const auto extract_if_condition_lhs = [&](std::string_view line) -> std::string {
        const std::string_view trimmed = trim_line(line);
        if (!StartsWith(trimmed, "if ") || !EndsWith(trimmed, " then")) {
            return {};
        }
        std::string_view condition = TrimWhitespace(trimmed.substr(3, trimmed.size() - 8));
        const std::size_t compare = condition.find("==");
        if (compare == std::string_view::npos) {
            return {};
        }
        const std::string_view lhs = TrimWhitespace(condition.substr(0, compare));
        if (lhs.find("State") == std::string_view::npos &&
            lhs.find("ThreadVar") == std::string_view::npos) {
            return {};
        }
        return std::string(lhs);
    };

    SyntaxRepairCandidateTracker candidate_tracker(validation_error,
                                                   had_trailing_newline,
                                                   &decompiled_source,
                                                   note);

    for (std::size_t else_line = 0; else_line < lines.size(); ++else_line) {
        if (trim_line(lines[else_line]) != "else") {
            continue;
        }
        if (reported_line != 0) {
            const std::size_t current_line = else_line + 1;
            if (current_line + 48 < reported_line || current_line > reported_line + 8) {
                continue;
            }
        }

        const std::size_t previous_line = previous_nonempty(else_line);
        const std::size_t branch_end_line = previous_nonempty(previous_line);
        const std::size_t state_if_line = next_nonempty(else_line + 1);
        if (previous_line >= lines.size() || branch_end_line >= lines.size() ||
            state_if_line >= lines.size()) {
            continue;
        }
        if (trim_line(lines[branch_end_line]) != "end" ||
            !StartsWith(trim_line(lines[state_if_line]), "if ")) {
            continue;
        }

        const std::string else_indent(line_indent(lines[else_line]));
        const std::string state_if_indent(line_indent(lines[state_if_line]));
        if (state_if_indent.size() <= else_indent.size() ||
            !StartsWith(state_if_indent, else_indent)) {
            continue;
        }

        bool supports_existing_pattern = trim_line(lines[previous_line]) == "coroutine.yield()";
        bool supports_assignment_handoff = false;
        if (!supports_existing_pattern) {
            const std::string assigned_lhs = extract_assignment_lhs(lines[previous_line]);
            const std::string condition_lhs = extract_if_condition_lhs(lines[state_if_line]);
            supports_assignment_handoff =
                !assigned_lhs.empty() && assigned_lhs == condition_lhs;
        }

        bool saw_state_elseif = false;
        if (supports_existing_pattern) {
            for (std::size_t probe = state_if_line + 1;
                 probe < lines.size() && probe <= state_if_line + 24;
                 ++probe) {
                const std::string_view probe_trimmed = trim_line(lines[probe]);
                if (probe_trimmed.empty()) {
                    continue;
                }
                if (line_indent(lines[probe]).size() <= else_indent.size()) {
                    break;
                }
                if (line_indent(lines[probe]) == state_if_indent &&
                    StartsWith(probe_trimmed, "elseif ")) {
                    saw_state_elseif = true;
                    break;
                }
            }
        }
        if (!supports_assignment_handoff &&
            (!supports_existing_pattern || !saw_state_elseif)) {
            continue;
        }

        std::vector<std::string> candidate_lines = lines;
        candidate_lines[else_line] = else_indent + "end";
        if (candidate_tracker.Consider(
                std::move(candidate_lines),
                "syntax_repaired_replaced_dangling_else_before_state_ladder")) {
            return true;
        }
    }

    return candidate_tracker.CommitBestProgress(
        "syntax_repaired_replaced_dangling_else_before_state_ladder_progress");
}

bool TryTrimTerminalIdleLoopEndPrefixes(std::string& decompiled_source,
                                        std::string_view validation_error,
                                        std::string* note) {
    if (validation_error.find("'<eof>' expected") == std::string_view::npos &&
        validation_error.find("near 'end'") == std::string_view::npos) {
        return false;
    }

    bool had_trailing_newline = false;
    const std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);
    if (lines.size() < 8) {
        return false;
    }

    const std::size_t reported_line = ExtractLuaErrorLine(validation_error);
    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimWhitespace(refii::lua::StripLuaLineComment(line));
    };
    const auto line_indent = [](std::string_view line) -> std::string_view {
        const std::size_t first_non_ws = line.find_first_not_of(" \t");
        if (first_non_ws == std::string_view::npos) {
            return line;
        }
        return line.substr(0, first_non_ws);
    };
    const auto previous_nonempty = [&](std::size_t start) -> std::size_t {
        if (start == 0 || start > lines.size()) {
            return lines.size();
        }
        for (std::size_t i = start; i-- > 0;) {
            if (!trim_line(lines[i]).empty()) {
                return i;
            }
            if (i == 0) {
                break;
            }
        }
        return lines.size();
    };
    const auto looks_like_top_level_boundary = [](std::string_view trimmed) -> bool {
        if (trimmed.empty() || StartsWith(trimmed, "--")) {
            return false;
        }

        const unsigned char first = static_cast<unsigned char>(trimmed.front());
        if (std::isalpha(first) == 0 && trimmed.front() != '_') {
            return false;
        }

        if (StartsWith(trimmed, "if ") || StartsWith(trimmed, "elseif ") ||
            StartsWith(trimmed, "while ") || StartsWith(trimmed, "for ") ||
            StartsWith(trimmed, "repeat") || StartsWith(trimmed, "until ") ||
            StartsWith(trimmed, "return") || StartsWith(trimmed, "local ") ||
            trimmed == "end" || trimmed == "else" || trimmed == "do") {
            return false;
        }

        if (trimmed.find("= function") != std::string_view::npos ||
            StartsWith(trimmed, "function ")) {
            return true;
        }

        if (trimmed.find('=') != std::string_view::npos) {
            return true;
        }

        const std::size_t paren = trimmed.find('(');
        if (paren == std::string_view::npos) {
            return false;
        }

        return trimmed.find('.') != std::string_view::npos ||
               trimmed.find(':') != std::string_view::npos;
    };

    SyntaxRepairCandidateTracker candidate_tracker(validation_error,
                                                   had_trailing_newline,
                                                   &decompiled_source,
                                                   note);

    for (std::size_t boundary_line = 1; boundary_line < lines.size(); ++boundary_line) {
        if (reported_line != 0) {
            const std::size_t current_line = boundary_line + 1;
            if (current_line + 48 < reported_line || current_line > reported_line + 24) {
                continue;
            }
        }

        if (lines[boundary_line].find_first_not_of(" \t") != 0) {
            continue;
        }

        const std::string_view boundary_trimmed = trim_line(lines[boundary_line]);
        if (!looks_like_top_level_boundary(boundary_trimmed)) {
            continue;
        }

        const std::size_t run_end = previous_nonempty(boundary_line);
        if (run_end >= lines.size() || trim_line(lines[run_end]) != "end") {
            continue;
        }

        std::size_t run_begin = run_end;
        while (run_begin > 0) {
            const std::size_t previous = previous_nonempty(run_begin);
            if (previous >= lines.size() || trim_line(lines[previous]) != "end") {
                break;
            }
            run_begin = previous;
        }

        const std::size_t run_length = run_end - run_begin + 1;
        if (run_length < 2) {
            continue;
        }

        std::size_t idle_end_line = previous_nonempty(run_begin);
        std::size_t erase_begin_min = run_begin;
        if (idle_end_line >= lines.size() || trim_line(lines[idle_end_line]) != "end") {
            idle_end_line = run_begin;
            erase_begin_min = run_begin + 1;
        }
        const std::size_t idle_yield_line = previous_nonempty(idle_end_line);
        const std::size_t idle_while_line = previous_nonempty(idle_yield_line);
        if (idle_end_line >= lines.size() || idle_yield_line >= lines.size() ||
            idle_while_line >= lines.size() ||
            trim_line(lines[idle_end_line]) != "end" ||
            trim_line(lines[idle_yield_line]) != "coroutine.yield()" ||
            trim_line(lines[idle_while_line]) != "while true do") {
            continue;
        }

        const std::string idle_indent(line_indent(lines[idle_while_line]));
        const std::size_t idle_end_indent = line_indent(lines[idle_end_line]).size();
        if (idle_end_indent < idle_indent.size() ||
            idle_end_indent > idle_indent.size() + 2 ||
            !line_indent(lines[idle_yield_line]).starts_with(idle_indent + "  ")) {
            continue;
        }

        if (erase_begin_min > run_end) {
            continue;
        }

        const std::size_t max_window_remove =
            std::min<std::size_t>(run_end - erase_begin_min + 1, 6);
        for (std::size_t remove_count = 1; remove_count <= max_window_remove; ++remove_count) {
            for (std::size_t erase_begin = erase_begin_min;
                 erase_begin + remove_count - 1 <= run_end;
                 ++erase_begin) {
                if (run_length <= remove_count) {
                    continue;
                }
                std::vector<std::string> candidate_lines = lines;
                candidate_lines.erase(
                    candidate_lines.begin() + static_cast<std::ptrdiff_t>(erase_begin),
                    candidate_lines.begin() +
                        static_cast<std::ptrdiff_t>(erase_begin + remove_count));
                if (candidate_tracker.Consider(
                        std::move(candidate_lines),
                        "syntax_repaired_trimmed_terminal_idle_loop_end_prefix")) {
                    return true;
                }
            }
        }
    }

    return candidate_tracker.CommitBestProgress(
        "syntax_repaired_trimmed_terminal_idle_loop_end_prefix_progress");
}

bool TryCloseTrueWaitWrapperBeforeStateAssignment(std::string& decompiled_source,
                                                  std::string_view validation_error,
                                                  std::string* note) {
    if ((validation_error.find("near 'else'") == std::string_view::npos &&
         validation_error.find("near 'elseif'") == std::string_view::npos) ||
        validation_error.find("to close 'while' at line ") == std::string_view::npos) {
        return false;
    }

    const auto extract_open_while_line = [](std::string_view raw_error) -> std::size_t {
        const std::string_view needle = "to close 'while' at line ";
        const std::size_t start = raw_error.find(needle);
        if (start == std::string_view::npos) {
            return 0;
        }

        std::size_t number_begin = start + needle.size();
        std::size_t number_end = number_begin;
        while (number_end < raw_error.size() &&
               std::isdigit(static_cast<unsigned char>(raw_error[number_end])) != 0) {
            ++number_end;
        }
        if (number_end == number_begin) {
            return 0;
        }

        return static_cast<std::size_t>(
            std::strtoul(std::string(raw_error.substr(number_begin, number_end - number_begin))
                             .c_str(),
                         nullptr,
                         10));
    };

    bool had_trailing_newline = false;
    const std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);
    if (lines.empty()) {
        return false;
    }

    const std::size_t reported_line = ExtractLuaErrorLine(validation_error);
    const std::size_t while_line = extract_open_while_line(validation_error);
    if (reported_line == 0 || reported_line > lines.size()) {
        return false;
    }

    const auto line_indent = [](std::string_view line) -> std::string_view {
        const std::size_t first_non_ws = line.find_first_not_of(" \t");
        if (first_non_ws == std::string_view::npos) {
            return line;
        }
        return line.substr(0, first_non_ws);
    };
    const auto next_nonempty = [&](std::size_t start) -> std::size_t {
        for (std::size_t i = start; i < lines.size(); ++i) {
            if (!TrimWhitespace(refii::lua::StripLuaLineComment(lines[i])).empty()) {
                return i;
            }
        }
        return lines.size();
    };
    const auto is_state_ladder_clause = [](std::string_view line) -> bool {
        const std::string_view trimmed = TrimWhitespace(
            refii::lua::StripLuaLineComment(line));
        if ((!StartsWith(trimmed, "if ") && !StartsWith(trimmed, "elseif ")) ||
            !EndsWith(trimmed, " then") || trimmed.find("==") == std::string_view::npos) {
            return false;
        }
        return trimmed.find("State") != std::string_view::npos ||
               trimmed.find("ThreadVar") != std::string_view::npos;
    };
    const auto indents_are_related = [](std::string_view lhs, std::string_view rhs) -> bool {
        return StartsWith(lhs, rhs) || StartsWith(rhs, lhs);
    };
    const auto try_candidate = [&](std::size_t state_assignment_line,
                                   std::string_view state_assignment_indent) -> bool {
        if (state_assignment_line >= lines.size() || state_assignment_indent.empty()) {
            return false;
        }

        std::vector<std::string> candidate_lines = lines;
        candidate_lines.insert(candidate_lines.begin() +
                                   static_cast<std::ptrdiff_t>(state_assignment_line),
                               std::string(state_assignment_indent) + "end");
        std::string candidate = JoinLines(candidate_lines, had_trailing_newline);
        const std::string candidate_error = ValidateLuaRepairSource(candidate);
        if (candidate_error.empty()) {
            decompiled_source = std::move(candidate);
            if (note != nullptr) {
                *note = JoinNotes(
                    *note,
                    "syntax_repaired_closed_true_wait_wrapper_before_state_assignment");
            }
            return true;
        }

        if (!IsValidationOutcomeBetter(validation_error, candidate_error)) {
            return false;
        }

        decompiled_source = std::move(candidate);
        if (note != nullptr) {
            *note = JoinNotes(
                *note,
                "syntax_repaired_closed_true_wait_wrapper_before_state_assignment_progress");
        }
        return true;
    };
    const auto collect_candidates = [&](std::size_t wrapper_line_index,
                                        std::size_t scan_end,
                                        bool require_reported_else,
                                        std::vector<std::size_t>* candidates) {
        if (candidates == nullptr || wrapper_line_index >= lines.size()) {
            return;
        }

        const std::string_view while_trimmed = TrimWhitespace(
            refii::lua::StripLuaLineComment(lines[wrapper_line_index]));
        if (while_trimmed != "while true do") {
            return;
        }

        const std::string wrapper_indent(line_indent(lines[wrapper_line_index]));
        std::string wrapper_body_indent;
        const std::size_t first_body_line = next_nonempty(wrapper_line_index + 1);
        if (first_body_line < lines.size()) {
            wrapper_body_indent = std::string(line_indent(lines[first_body_line]));
        }

        bool saw_yield = false;
        for (std::size_t probe = wrapper_line_index + 1;
             probe < scan_end && probe < lines.size();
             ++probe) {
            const std::string_view trimmed =
                TrimWhitespace(refii::lua::StripLuaLineComment(lines[probe]));
            if (trimmed.empty()) {
                continue;
            }

            if (trimmed == "coroutine.yield()") {
                saw_yield = true;
                continue;
            }
            if (!saw_yield) {
                continue;
            }

            const std::string current_indent(line_indent(lines[probe]));
            const bool matches_wrapper_indent = current_indent == wrapper_indent;
            const bool matches_body_indent =
                !wrapper_body_indent.empty() && current_indent == wrapper_body_indent;
            if (!matches_wrapper_indent && !matches_body_indent) {
                continue;
            }
            if (trimmed.find(" = ") == std::string_view::npos ||
                (trimmed.find("State") == std::string_view::npos &&
                 trimmed.find("ThreadVar") == std::string_view::npos)) {
                continue;
            }

            const std::size_t else_line = next_nonempty(probe + 1);
            if (else_line >= lines.size()) {
                continue;
            }
            const std::string_view else_trimmed = TrimWhitespace(
                refii::lua::StripLuaLineComment(lines[else_line]));
            if (else_trimmed != "else" && !StartsWith(else_trimmed, "elseif ")) {
                continue;
            }
            if (!indents_are_related(line_indent(lines[else_line]), current_indent)) {
                continue;
            }
            if (require_reported_else && else_line + 1 != reported_line) {
                continue;
            }

            if (StartsWith(else_trimmed, "elseif ")) {
                if (!is_state_ladder_clause(lines[else_line])) {
                    continue;
                }
            } else {
                const std::size_t continuation_line = next_nonempty(else_line + 1);
                if (continuation_line >= lines.size() ||
                    !is_state_ladder_clause(lines[continuation_line]) ||
                    !indents_are_related(line_indent(lines[continuation_line]),
                                         current_indent)) {
                    continue;
                }
            }

            candidates->push_back(probe);
        }
    };

    const std::string_view reported_trimmed =
        TrimWhitespace(refii::lua::StripLuaLineComment(lines[reported_line - 1]));
    if (reported_trimmed != "else" && !StartsWith(reported_trimmed, "elseif ")) {
        return false;
    }

    std::vector<std::size_t> candidates;
    if (while_line != 0 && while_line <= lines.size() && while_line < reported_line) {
        collect_candidates(while_line - 1, reported_line - 1, true, &candidates);
        for (std::size_t candidate_line : candidates) {
            const std::string candidate_indent(line_indent(lines[candidate_line]));
            if (try_candidate(candidate_line, candidate_indent)) {
                return true;
            }
        }
    }

    return false;
}

}  // namespace

bool TryCollapseNestedElseIfInsideClause(std::string& decompiled_source,
                                         std::string_view validation_error,
                                         std::string* note) {
    return TryCollapseNestedElseIfInsideClauseImpl(
        decompiled_source, validation_error, note);
}

bool LooksLikeTopLevelBoundary(std::string_view trimmed) {
    if (trimmed.empty() || StartsWith(trimmed, "--")) {
        return false;
    }

    if (StartsWith(trimmed, ";(function") || StartsWith(trimmed, "(function") ||
        StartsWith(trimmed, "function(") || StartsWith(trimmed, "function ") ||
        StartsWith(trimmed, "local function ") ||
        (StartsWith(trimmed, "local ") &&
         trimmed.find("= function") != std::string_view::npos)) {
        return true;
    }

    const unsigned char first = static_cast<unsigned char>(trimmed.front());
    if (std::isalpha(first) == 0 && trimmed.front() != '_') {
        return false;
    }

    if (StartsWith(trimmed, "if ") || StartsWith(trimmed, "elseif ") ||
        StartsWith(trimmed, "while ") || StartsWith(trimmed, "for ") ||
        StartsWith(trimmed, "repeat") || StartsWith(trimmed, "until ") ||
        StartsWith(trimmed, "return") || StartsWith(trimmed, "local ") ||
        trimmed == "end" || trimmed == "else" || trimmed == "do") {
        return false;
    }

    if (trimmed.find("= function") != std::string_view::npos ||
        StartsWith(trimmed, "function ")) {
        return true;
    }

    if (trimmed.find('=') != std::string_view::npos) {
        return true;
    }

    const std::size_t paren = trimmed.find('(');
    if (paren == std::string_view::npos) {
        return false;
    }

    return trimmed.find('.') != std::string_view::npos ||
           trimmed.find(':') != std::string_view::npos;
}

std::string SummarizeSyntaxError(std::string_view raw_error) {
    std::string message(raw_error);
    const std::size_t detail_pos = message.rfind(": ");
    if (detail_pos != std::string::npos && detail_pos + 2 < message.size()) {
        message = message.substr(detail_pos + 2);
    }

    std::string sanitized;
    sanitized.reserve(message.size());
    bool previous_space = false;
    for (char ch : message) {
        const unsigned char uch = static_cast<unsigned char>(ch);
        if (ch == ';' || ch == '\n' || ch == '\r' || ch == '\t') {
            ch = ' ';
        }
        if (std::isspace(uch) != 0 || ch == ' ') {
            if (!previous_space) {
                sanitized.push_back('_');
                previous_space = true;
            }
        } else {
            sanitized.push_back(ch);
            previous_space = false;
        }
        if (sanitized.size() >= 96) {
            break;
        }
    }

    while (!sanitized.empty() && sanitized.back() == '_') {
        sanitized.pop_back();
    }
    while (!sanitized.empty() && sanitized.front() == '_') {
        sanitized.erase(sanitized.begin());
    }

    return sanitized.empty() ? "unknown" : sanitized;
}

std::size_t ExtractLuaErrorLine(std::string_view raw_error) {
    std::size_t first_colon = raw_error.find(':');
    while (first_colon != std::string_view::npos) {
        const std::size_t number_begin = first_colon + 1;
        std::size_t number_end = number_begin;
        while (number_end < raw_error.size() &&
               std::isdigit(static_cast<unsigned char>(raw_error[number_end])) != 0) {
            ++number_end;
        }
        if (number_end > number_begin && number_end < raw_error.size() && raw_error[number_end] == ':') {
            return static_cast<std::size_t>(
                std::strtoul(std::string(raw_error.substr(number_begin, number_end - number_begin)).c_str(),
                             nullptr,
                             10));
        }
        first_colon = raw_error.find(':', first_colon + 1);
    }
    return 0;
}

SyntaxErrorDescriptor DescribeSyntaxError(std::string_view raw_error) {
    SyntaxErrorDescriptor descriptor;
    descriptor.summary = SummarizeSyntaxError(raw_error);
    descriptor.line = ExtractLuaErrorLine(raw_error);
    return descriptor;
}

void AppendSyntaxErrorNotes(std::string* note,
                            std::string_view summary_key,
                            std::string_view line_key,
                            std::string_view raw_error) {
    if (note == nullptr || raw_error.empty() || summary_key.empty()) {
        return;
    }

    const SyntaxErrorDescriptor descriptor = DescribeSyntaxError(raw_error);
    *note = JoinNotes(*note,
                      std::string(summary_key) + "=" + descriptor.summary);
    if (!line_key.empty() && descriptor.line != 0) {
        *note = JoinNotes(*note,
                          std::string(line_key) + "=" +
                              std::to_string(descriptor.line));
    }
}

bool IsValidationOutcomeBetter(std::string_view baseline_error,
                               std::string_view candidate_error) {
    if (candidate_error.empty()) {
        return true;
    }
    if (baseline_error.empty()) {
        return false;
    }
    return ScoreValidationOutcome(candidate_error) > ScoreValidationOutcome(baseline_error);
}

bool TryInsertEndsBeforeTopLevelBoundaries(std::string& decompiled_source,
                                           std::string_view validation_error,
                                           std::string* note) {
    if (validation_error.find("<eof>") == std::string_view::npos &&
        validation_error.find("'end' expected") == std::string_view::npos &&
        validation_error.find("unexpected symbol near") == std::string_view::npos &&
        validation_error.find("expected near") == std::string_view::npos &&
        validation_error.find("chunk has too many syntax levels") ==
            std::string_view::npos) {
        return false;
    }

    bool had_trailing_newline = false;
    const std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);
    if (lines.size() < 2) {
        return false;
    }
    const std::size_t reported_line = ExtractLuaErrorLine(validation_error);
    const SyntaxErrorDescriptor baseline_descriptor =
        DescribeSyntaxError(validation_error);
    const bool syntax_depth_overflow =
        validation_error.find("chunk has too many syntax levels") !=
        std::string_view::npos;

    const auto previous_significant = [&](std::size_t start) -> std::size_t {
        if (start == 0 || start > lines.size()) {
            return lines.size();
        }
        for (std::size_t i = start; i-- > 0;) {
            if (!TrimWhitespace(StripLineComment(lines[i])).empty()) {
                return i;
            }
            if (i == 0) {
                break;
            }
        }
        return lines.size();
    };
    const auto previous_nonblank = [&](std::size_t start) -> std::size_t {
        if (start == 0 || start > lines.size()) {
            return lines.size();
        }
        for (std::size_t i = start; i-- > 0;) {
            if (!TrimWhitespace(lines[i]).empty()) {
                return i;
            }
            if (i == 0) {
                break;
            }
        }
        return lines.size();
    };
    enum class MissingControlCloseKind {
        kEnd,
        kUntil,
    };
    struct MissingControlCloseFrame {
        MissingControlCloseKind kind;
        std::string indent;
    };
    const auto parse_suppressed_close_line =
        [&](std::string_view line, std::string* replacement) -> bool {
        constexpr std::string_view kPrefix = "-- refii-suppressed-control:";

        const std::string_view trimmed = TrimWhitespace(line);
        if (!StartsWith(trimmed, kPrefix) || replacement == nullptr) {
            return false;
        }

        std::string_view payload = TrimWhitespace(trimmed.substr(kPrefix.size()));
        if (!(payload == "end" || payload == "until" || StartsWith(payload, "until "))) {
            return false;
        }

        const std::size_t indent_end = line.find_first_not_of(" \t");
        const std::string indent =
            indent_end == std::string_view::npos ? std::string()
                                                 : std::string(line.substr(0, indent_end));
        *replacement = indent + std::string(payload);
        return true;
    };
    const auto build_stack_aware_suppressed_close_run =
        [&](std::size_t run_begin,
            std::size_t close_count,
            std::size_t scan_begin) -> std::vector<std::string> {
        std::vector<MissingControlCloseKind> open_controls;
        open_controls.reserve(64);

        const auto code_trimmed = [](std::string_view line) -> std::string_view {
            return TrimWhitespace(StripLineComment(line));
        };
        const auto pop_last_kind = [&](MissingControlCloseKind kind) {
            for (std::size_t i = open_controls.size(); i-- > 0;) {
                if (open_controls[i] == kind) {
                    open_controls.erase(open_controls.begin() +
                                        static_cast<std::ptrdiff_t>(i));
                    return;
                }
            }
        };
        const auto update_control_stack = [&](std::string_view line) {
            const std::string_view trimmed = code_trimmed(line);
            if (trimmed.empty() || trimmed == "else" || StartsWith(trimmed, "elseif ")) {
                return;
            }

            if (trimmed == "repeat") {
                open_controls.push_back(MissingControlCloseKind::kUntil);
                return;
            }

            if (trimmed == "end" || trimmed == "end,") {
                pop_last_kind(MissingControlCloseKind::kEnd);
                return;
            }

            if (trimmed == "until" || StartsWith(trimmed, "until ")) {
                pop_last_kind(MissingControlCloseKind::kUntil);
                return;
            }

            if (trimmed == "do" || StartsWith(trimmed, "if ") ||
                StartsWith(trimmed, "for ") || StartsWith(trimmed, "while ") ||
                StartsWith(trimmed, "function ") ||
                StartsWith(trimmed, "local function ") ||
                StartsWith(trimmed, "return function(") ||
                StartsWith(trimmed, "return function ") ||
                trimmed.find("= function(") != std::string_view::npos ||
                trimmed.find("= function ") != std::string_view::npos) {
                open_controls.push_back(MissingControlCloseKind::kEnd);
            }
        };

        for (std::size_t i = std::min(scan_begin, lines.size());
             i < std::min(run_begin, lines.size());
             ++i) {
            update_control_stack(lines[i]);
        }

        std::vector<std::string> replacements;
        replacements.reserve(close_count);
        for (std::size_t offset = 0; offset < close_count && run_begin + offset < lines.size();
             ++offset) {
            const std::string_view line = lines[run_begin + offset];
            const std::size_t indent_end = line.find_first_not_of(" \t");
            const std::string indent =
                indent_end == std::string_view::npos ? std::string()
                                                     : std::string(line.substr(0, indent_end));
            const bool close_repeat =
                !open_controls.empty() &&
                open_controls.back() == MissingControlCloseKind::kUntil;
            replacements.push_back(indent + (close_repeat ? "until true" : "end"));
            if (!open_controls.empty()) {
                open_controls.pop_back();
            }
        }
        return replacements;
    };
    const auto build_structured_missing_close_run =
        [&](std::size_t boundary_index,
            std::size_t close_count,
            std::size_t scan_begin) -> std::vector<std::string> {
        std::vector<MissingControlCloseFrame> open_controls;
        open_controls.reserve(64);

        const auto local_line_indent = [](std::string_view line) -> std::string_view {
            const std::size_t first_non_ws = line.find_first_not_of(" \t");
            if (first_non_ws == std::string_view::npos) {
                return line;
            }
            return line.substr(0, first_non_ws);
        };
        const auto code_trimmed = [](std::string_view line) -> std::string_view {
            return TrimWhitespace(StripLineComment(line));
        };
        const auto pop_last_kind = [&](MissingControlCloseKind kind) {
            for (std::size_t i = open_controls.size(); i-- > 0;) {
                if (open_controls[i].kind == kind) {
                    open_controls.erase(open_controls.begin() +
                                        static_cast<std::ptrdiff_t>(i));
                    return;
                }
            }
        };
        const auto update_control_stack = [&](std::string_view line) {
            const std::string_view trimmed = code_trimmed(line);
            if (trimmed.empty() || trimmed == "else" || StartsWith(trimmed, "elseif ")) {
                return;
            }

            const std::string indent(local_line_indent(line));
            if (trimmed == "repeat") {
                open_controls.push_back({MissingControlCloseKind::kUntil, indent});
                return;
            }
            if (trimmed == "end" || trimmed == "end,") {
                pop_last_kind(MissingControlCloseKind::kEnd);
                return;
            }
            if (trimmed == "until" || StartsWith(trimmed, "until ")) {
                pop_last_kind(MissingControlCloseKind::kUntil);
                return;
            }
            if (trimmed == "do" || StartsWith(trimmed, "if ") ||
                StartsWith(trimmed, "for ") || StartsWith(trimmed, "while ") ||
                StartsWith(trimmed, "function ") ||
                StartsWith(trimmed, "local function ") ||
                StartsWith(trimmed, "return function(") ||
                StartsWith(trimmed, "return function ") ||
                trimmed.find("= function(") != std::string_view::npos ||
                trimmed.find("= function ") != std::string_view::npos) {
                open_controls.push_back({MissingControlCloseKind::kEnd, indent});
            }
        };

        for (std::size_t i = std::min(scan_begin, lines.size());
             i < std::min(boundary_index, lines.size());
             ++i) {
            update_control_stack(lines[i]);
        }

        std::vector<std::string> replacements;
        replacements.reserve(std::min(close_count, open_controls.size()));
        for (std::size_t offset = 0;
             offset < close_count && offset < open_controls.size();
             ++offset) {
            const MissingControlCloseFrame& frame =
                open_controls[open_controls.size() - 1 - offset];
            replacements.push_back(frame.indent +
                                   (frame.kind == MissingControlCloseKind::kUntil
                                        ? "until true"
                                        : "end"));
        }
        return replacements;
    };
    const auto suppressed_end_run = [&](std::size_t boundary_index,
                                        std::size_t* run_begin,
                                        std::size_t* run_end) -> std::size_t {
        if (run_begin != nullptr) {
            *run_begin = lines.size();
        }
        if (run_end != nullptr) {
            *run_end = lines.size();
        }
        if (boundary_index == 0 || boundary_index > lines.size()) {
            return 0;
        }

        std::size_t current = boundary_index;
        while (current > 0) {
            const std::size_t probe = previous_significant(current);
            if (probe >= lines.size()) {
                break;
            }
            if (TrimWhitespace(lines[probe]) != "-- refii-suppressed-control: end") {
                break;
            }
            if (run_end != nullptr && *run_end >= lines.size()) {
                *run_end = probe;
            }
            if (run_begin != nullptr) {
                *run_begin = probe;
            }
            current = probe;
            if (probe == 0) {
                break;
            }
        }

        if (run_begin == nullptr || *run_begin >= lines.size() ||
            run_end == nullptr || *run_end >= lines.size() || *run_begin > *run_end) {
            return 0;
        }
        return *run_end - *run_begin + 1;
    };
    const auto line_indent = [](std::string_view line) -> std::string_view {
        const std::size_t first_non_ws = line.find_first_not_of(" \t");
        if (first_non_ws == std::string_view::npos) {
            return line;
        }
        return line.substr(0, first_non_ws);
    };
    const auto block_delta = [](std::string_view line) -> int {
        const std::string_view trimmed =
            TrimWhitespace(StripLineComment(line));
        if (trimmed.empty()) {
            return 0;
        }
        if (trimmed == "else" || trimmed == "end else" ||
            StartsWith(trimmed, "elseif ")) {
            return 0;
        }
        if (trimmed == "repeat" || trimmed == "do") {
            return 1;
        }
        if (trimmed == "end" || trimmed == "end," || StartsWith(trimmed, "until ")) {
            return -1;
        }
        if (StartsWith(trimmed, "if ") || StartsWith(trimmed, "for ") ||
            StartsWith(trimmed, "while ") || StartsWith(trimmed, "function ") ||
            StartsWith(trimmed, "local function ") ||
            StartsWith(trimmed, "return function(") ||
            StartsWith(trimmed, "return function ") ||
            trimmed.find("= function(") != std::string_view::npos ||
            trimmed.find("= function ") != std::string_view::npos) {
            return 1;
        }
        return 0;
    };

    std::vector<std::size_t> candidates;
    candidates.reserve(lines.size());
    for (std::size_t i = 1; i < lines.size(); ++i) {
        if (lines[i].find_first_not_of(" \t") != 0) {
            continue;
        }
        const std::string_view trimmed = TrimWhitespace(lines[i]);
        if (LooksLikeTopLevelBoundary(trimmed)) {
            candidates.push_back(i);
        }
    }
    const auto local_top_level_scan_begin = [&](std::size_t boundary_index) -> std::size_t {
        if (boundary_index == 0 || boundary_index > lines.size()) {
            return 0;
        }
        for (std::size_t probe = boundary_index; probe-- > 0;) {
            if (lines[probe].find_first_not_of(" \t") == 0 &&
                LooksLikeTopLevelBoundary(TrimWhitespace(lines[probe]))) {
                return probe;
            }
            if (probe == 0) {
                break;
            }
        }
        return 0;
    };

    std::string best_candidate;
    std::string best_error;
    const auto overflow_progresses =
        [&](const SyntaxErrorDescriptor& baseline, const SyntaxErrorDescriptor& candidate) {
            return syntax_depth_overflow &&
                   baseline.summary == candidate.summary &&
                   candidate.line > baseline.line;
        };
    for (auto it = candidates.rbegin(); it != candidates.rend(); ++it) {
        const std::size_t index = *it;
        const std::size_t scan_begin = local_top_level_scan_begin(index);
        if (reported_line != 0) {
            const std::size_t boundary_line = index + 1;
            if (boundary_line + 128 < reported_line || boundary_line > reported_line + 24) {
                continue;
            }
        }

        std::size_t suppressed_close_begin = lines.size();
        std::size_t suppressed_close_end = lines.size();
        std::vector<std::string> suppressed_close_lines;
        {
            std::size_t current = index;
            while (current > 0) {
                const std::size_t probe = previous_nonblank(current);
                if (probe >= lines.size()) {
                    break;
                }

                std::string replacement;
                if (!parse_suppressed_close_line(lines[probe], &replacement)) {
                    break;
                }

                if (suppressed_close_end >= lines.size()) {
                    suppressed_close_end = probe;
                }
                suppressed_close_begin = probe;
                suppressed_close_lines.insert(suppressed_close_lines.begin(),
                                             std::move(replacement));
                current = probe;
                if (probe == 0) {
                    break;
                }
            }
        }

        if (!suppressed_close_lines.empty()) {
            const std::vector<std::string> stack_aware_close_lines =
                build_stack_aware_suppressed_close_run(suppressed_close_begin,
                                                       suppressed_close_lines.size(),
                                                       scan_begin);
            std::vector<std::vector<std::string>> close_run_variants;
            close_run_variants.push_back(stack_aware_close_lines);
            if (stack_aware_close_lines != suppressed_close_lines) {
                close_run_variants.push_back(suppressed_close_lines);
            }

            for (const std::vector<std::string>& close_run : close_run_variants) {
                std::vector<std::string> candidate_lines = lines;
                for (std::size_t offset = 0; offset < close_run.size(); ++offset) {
                    candidate_lines[suppressed_close_begin + offset] =
                        close_run[offset];
                }

                std::string candidate = JoinLines(candidate_lines, had_trailing_newline);
                const std::string candidate_error =
                    ValidateLuaRepairSource(candidate);
                if (candidate_error.empty()) {
                    decompiled_source = std::move(candidate);
                    if (note != nullptr) {
                        *note = JoinNotes(
                            *note,
                            "syntax_repaired_restored_suppressed_close_run_before_top_level_boundary");
                    }
                    return true;
                }
                const SyntaxErrorDescriptor candidate_descriptor =
                    DescribeSyntaxError(candidate_error);
                if ((IsValidationOutcomeBetter(validation_error, candidate_error) ||
                     overflow_progresses(baseline_descriptor, candidate_descriptor)) &&
                    (best_error.empty() ||
                     IsValidationOutcomeBetter(best_error, candidate_error) ||
                     overflow_progresses(DescribeSyntaxError(best_error),
                                         candidate_descriptor))) {
                    best_candidate = std::move(candidate);
                    best_error = candidate_error;
                }
            }
        }

        const std::size_t previous_line = previous_significant(index);
        if (previous_line < lines.size()) {
            const std::vector<std::string> structured_close_lines =
                build_structured_missing_close_run(index, 6, scan_begin);
            const bool missing_nested_close_first =
                !structured_close_lines.empty() &&
                line_indent(structured_close_lines.front()).size() != 0;
            for (std::size_t close_count = 1;
                 close_count <= structured_close_lines.size();
                 ++close_count) {
                std::vector<std::string> candidate_lines = lines;
                candidate_lines.insert(candidate_lines.begin() +
                                           static_cast<std::ptrdiff_t>(index),
                                       structured_close_lines.begin(),
                                       structured_close_lines.begin() +
                                           static_cast<std::ptrdiff_t>(close_count));

                std::string candidate = JoinLines(candidate_lines, had_trailing_newline);
                const std::string candidate_error =
                    ValidateLuaRepairSource(candidate);
                if (candidate_error.empty()) {
                    decompiled_source = std::move(candidate);
                    if (note != nullptr) {
                        *note = JoinNotes(
                            *note,
                            "syntax_repaired_inserted_structured_close_run_before_top_level_boundary");
                    }
                    return true;
                }
                const SyntaxErrorDescriptor candidate_descriptor =
                    DescribeSyntaxError(candidate_error);
                if ((IsValidationOutcomeBetter(validation_error, candidate_error) ||
                     overflow_progresses(baseline_descriptor, candidate_descriptor)) &&
                    (best_error.empty() ||
                     IsValidationOutcomeBetter(best_error, candidate_error) ||
                     overflow_progresses(DescribeSyntaxError(best_error),
                                         candidate_descriptor))) {
                    best_candidate = std::move(candidate);
                    best_error = candidate_error;
                }
            }

            if (missing_nested_close_first) {
                continue;
            }

            std::vector<std::string> structured_end_lines;
            structured_end_lines.reserve(12);
            int close_balance = 0;
            const std::size_t min_probe = index > 192 ? index - 192 : 0;
            for (std::size_t probe = previous_line + 1; probe-- > min_probe;) {
                const int delta = block_delta(lines[probe]);
                if (delta < 0) {
                    close_balance += -delta;
                } else if (delta > 0) {
                    int missing = delta;
                    if (close_balance >= missing) {
                        close_balance -= missing;
                        missing = 0;
                    } else {
                        missing -= close_balance;
                        close_balance = 0;
                    }

                    for (int count = 0;
                         count < missing && structured_end_lines.size() < 12;
                         ++count) {
                        structured_end_lines.push_back(
                            std::string(line_indent(lines[probe])) + "end");
                    }

                    if (!structured_end_lines.empty() &&
                        line_indent(lines[probe]).empty()) {
                        break;
                    }
                }

                if (probe == 0) {
                    break;
                }
            }

            std::size_t suppressed_run_begin = lines.size();
            std::size_t suppressed_run_end = lines.size();
            const std::size_t suppressed_run_count =
                suppressed_end_run(index, &suppressed_run_begin, &suppressed_run_end);
            for (std::size_t end_count = 1;
                 end_count <= structured_end_lines.size();
                 ++end_count) {
                const std::string_view previous_trimmed =
                    TrimWhitespace(lines[previous_line]);
                if (previous_trimmed == "-- refii-suppressed-control: end") {
                    std::vector<std::string> candidate_lines = lines;
                    candidate_lines[previous_line] = structured_end_lines.front();
                    if (end_count > 1) {
                        candidate_lines.insert(
                            candidate_lines.begin() +
                                static_cast<std::ptrdiff_t>(previous_line + 1),
                            structured_end_lines.begin() + 1,
                            structured_end_lines.begin() +
                                static_cast<std::ptrdiff_t>(end_count));
                    }
                    std::string candidate = JoinLines(candidate_lines, had_trailing_newline);
                    const std::string candidate_error =
                        ValidateLuaRepairSource(candidate);
                    if (candidate_error.empty()) {
                        decompiled_source = std::move(candidate);
                        if (note != nullptr) {
                            *note = JoinNotes(
                                *note,
                                "syntax_repaired_restored_suppressed_end_before_top_level_boundary");
                        }
                        return true;
                    }
                    const SyntaxErrorDescriptor candidate_descriptor =
                        DescribeSyntaxError(candidate_error);
                    if ((IsValidationOutcomeBetter(validation_error, candidate_error) ||
                         overflow_progresses(baseline_descriptor, candidate_descriptor)) &&
                        (best_error.empty() ||
                         IsValidationOutcomeBetter(best_error, candidate_error) ||
                         overflow_progresses(DescribeSyntaxError(best_error),
                                             candidate_descriptor))) {
                        best_candidate = std::move(candidate);
                        best_error = candidate_error;
                    }
                }

                if (suppressed_run_count != 0) {
                    std::vector<std::string> candidate_lines = lines;
                    const std::size_t replace_count =
                        std::min<std::size_t>(end_count, suppressed_run_count);
                    const std::size_t replace_begin =
                        suppressed_run_end - replace_count + 1;
                    for (std::size_t offset = 0; offset < replace_count; ++offset) {
                        candidate_lines[replace_begin + offset] = structured_end_lines[offset];
                    }
                    if (end_count > replace_count) {
                        candidate_lines.insert(
                            candidate_lines.begin() +
                                static_cast<std::ptrdiff_t>(index),
                            structured_end_lines.begin() +
                                static_cast<std::ptrdiff_t>(replace_count),
                            structured_end_lines.begin() +
                                static_cast<std::ptrdiff_t>(end_count));
                    }

                    std::string candidate = JoinLines(candidate_lines, had_trailing_newline);
                    const std::string candidate_error =
                        ValidateLuaRepairSource(candidate);
                    if (candidate_error.empty()) {
                        decompiled_source = std::move(candidate);
                        if (note != nullptr) {
                            *note = JoinNotes(
                                *note,
                                "syntax_repaired_restored_suppressed_end_run_before_top_level_boundary");
                        }
                        return true;
                    }
                    const SyntaxErrorDescriptor candidate_descriptor =
                        DescribeSyntaxError(candidate_error);
                    if ((IsValidationOutcomeBetter(validation_error, candidate_error) ||
                         overflow_progresses(baseline_descriptor, candidate_descriptor)) &&
                        (best_error.empty() ||
                         IsValidationOutcomeBetter(best_error, candidate_error) ||
                         overflow_progresses(DescribeSyntaxError(best_error),
                                             candidate_descriptor))) {
                        best_candidate = std::move(candidate);
                        best_error = candidate_error;
                    }
                }

                std::vector<std::string> candidate_lines = lines;
                candidate_lines.insert(
                    candidate_lines.begin() + static_cast<std::ptrdiff_t>(index),
                    structured_end_lines.begin(),
                    structured_end_lines.begin() +
                        static_cast<std::ptrdiff_t>(end_count));
                std::string candidate = JoinLines(candidate_lines, had_trailing_newline);
                const std::string candidate_error =
                    ValidateLuaRepairSource(candidate);
                if (candidate_error.empty()) {
                    decompiled_source = std::move(candidate);
                    if (note != nullptr) {
                        *note = JoinNotes(
                            *note,
                            "syntax_repaired_inserted_structured_end_before_top_level_boundary");
                    }
                    return true;
                }
                const SyntaxErrorDescriptor candidate_descriptor =
                    DescribeSyntaxError(candidate_error);
                if ((IsValidationOutcomeBetter(validation_error, candidate_error) ||
                     overflow_progresses(baseline_descriptor, candidate_descriptor)) &&
                    (best_error.empty() ||
                     IsValidationOutcomeBetter(best_error, candidate_error) ||
                     overflow_progresses(DescribeSyntaxError(best_error),
                                         candidate_descriptor))) {
                    best_candidate = std::move(candidate);
                    best_error = candidate_error;
                }
            }
        }

        for (int end_count = 1; end_count <= 12; ++end_count) {
            std::vector<std::string> candidate_lines = lines;
            candidate_lines.insert(candidate_lines.begin() + static_cast<std::ptrdiff_t>(index),
                                   static_cast<std::size_t>(end_count),
                                   std::string("end"));
            std::string candidate = JoinLines(candidate_lines, had_trailing_newline);
            const std::string candidate_error = ValidateLuaRepairSource(candidate);
            if (candidate_error.empty()) {
                decompiled_source = std::move(candidate);
                if (note != nullptr) {
                    *note = JoinNotes(*note,
                                      "syntax_repaired_inserted_end_before_top_level_boundary");
                }
                return true;
            }
            if (IsValidationOutcomeBetter(validation_error, candidate_error) &&
                (best_error.empty() || IsValidationOutcomeBetter(best_error, candidate_error))) {
                best_candidate = std::move(candidate);
                best_error = candidate_error;
            }
        }
    }

    if (!best_candidate.empty()) {
        decompiled_source = std::move(best_candidate);
        if (note != nullptr) {
            *note = JoinNotes(
                *note,
                "syntax_repaired_inserted_end_before_top_level_boundary_progress");
        }
        return true;
    }

    return false;
}

bool TryRemoveEndsBeforeTopLevelBoundaries(std::string& decompiled_source,
                                           std::string_view validation_error,
                                           std::string* note) {
    if (validation_error.find("unexpected symbol near") == std::string_view::npos &&
        validation_error.find("<eof>") == std::string_view::npos) {
        return false;
    }

    bool had_trailing_newline = false;
    const std::vector<std::string> lines = SplitLines(decompiled_source, &had_trailing_newline);
    if (lines.size() < 3) {
        return false;
    }
    const std::size_t reported_line = ExtractLuaErrorLine(validation_error);

    const auto previous_significant = [&](std::size_t start) -> std::size_t {
        if (start == 0 || start > lines.size()) {
            return lines.size();
        }
        for (std::size_t i = start; i-- > 0;) {
            if (!TrimWhitespace(lines[i]).empty()) {
                return i;
            }
            if (i == 0) {
                break;
            }
        }
        return lines.size();
    };

    std::string best_candidate;
    std::string best_error;
    std::string best_note;
    const auto try_record_candidate =
        [&](std::vector<std::string> candidate_lines, std::string_view candidate_note) -> bool {
        std::string candidate = JoinLines(candidate_lines, had_trailing_newline);
        const std::string candidate_error = ValidateLuaRepairSource(candidate);
        if (candidate_error.empty()) {
            decompiled_source = std::move(candidate);
            if (note != nullptr) {
                *note = JoinNotes(*note, std::string(candidate_note));
            }
            return true;
        }
        if (!IsValidationOutcomeBetter(validation_error, candidate_error) ||
            (!best_error.empty() && !IsValidationOutcomeBetter(best_error, candidate_error))) {
            return false;
        }

        best_candidate = std::move(candidate);
        best_error = candidate_error;
        best_note = std::string(candidate_note) + "_progress";
        return false;
    };

    for (std::size_t i = 1; i < lines.size(); ++i) {
        if (lines[i].find_first_not_of(" \t") != 0) {
            continue;
        }
        const std::string_view trimmed = TrimWhitespace(lines[i]);
        if (!LooksLikeTopLevelBoundary(trimmed)) {
            continue;
        }

        const std::size_t run_end = previous_significant(i);
        if (run_end >= lines.size() || TrimWhitespace(lines[run_end]) != "end") {
            continue;
        }

        std::size_t run_begin = run_end;
        while (run_begin > 0) {
            const std::size_t previous = previous_significant(run_begin);
            if (previous >= lines.size() || TrimWhitespace(lines[previous]) != "end") {
                break;
            }
            run_begin = previous;
        }

        const std::size_t run_length = run_end - run_begin + 1;
        const std::size_t max_remove = std::min<std::size_t>(run_length, 4);
        for (std::size_t remove_count = 1; remove_count <= max_remove; ++remove_count) {
            std::vector<std::string> candidate_lines = lines;
            candidate_lines.erase(
                candidate_lines.begin() + static_cast<std::ptrdiff_t>(run_end - remove_count + 1),
                candidate_lines.begin() + static_cast<std::ptrdiff_t>(run_end + 1));
            if (try_record_candidate(std::move(candidate_lines),
                                     "syntax_repaired_removed_end_before_top_level_boundary")) {
                return true;
            }
        }

        if (run_length < 2) {
            continue;
        }

        bool allow_short_prefix_trim = run_length >= 6;
        if (!allow_short_prefix_trim && reported_line != 0) {
            const std::size_t boundary_line = i + 1;
            const std::size_t min_line = reported_line > 192 ? reported_line - 192 : 1;
            allow_short_prefix_trim =
                boundary_line >= min_line && boundary_line <= reported_line + 32;
        }
        if (!allow_short_prefix_trim) {
            continue;
        }

        std::size_t max_prefix_remove =
            std::min<std::size_t>(run_length - 1, 32);
        if (reported_line != 0) {
            const std::size_t boundary_line = i + 1;
            const std::size_t min_line = reported_line > 192 ? reported_line - 192 : 1;
            if (boundary_line >= min_line && boundary_line <= reported_line + 64) {
                max_prefix_remove = run_length - 1;
            }
        }
        for (std::size_t remove_count = 1; remove_count <= max_prefix_remove; ++remove_count) {
            std::vector<std::string> candidate_lines = lines;
            candidate_lines.erase(
                candidate_lines.begin() + static_cast<std::ptrdiff_t>(run_begin),
                candidate_lines.begin() +
                    static_cast<std::ptrdiff_t>(run_begin + remove_count));
            if (try_record_candidate(
                    std::move(candidate_lines),
                    "syntax_repaired_removed_leaked_end_prefix_before_top_level_boundary")) {
                return true;
            }
        }
    }

    if (!best_candidate.empty()) {
        decompiled_source = std::move(best_candidate);
        if (note != nullptr) {
            *note = JoinNotes(
                *note,
                best_note.empty() ? "syntax_repaired_removed_end_before_top_level_boundary_progress"
                                  : best_note);
        }
        return true;
    }

    return false;
}

SyntaxRepairAttemptResult TryRunAnchoredValidatedSyntaxRepairs(
    std::string& decompiled_source,
    std::string& validation_error,
    std::string_view validation_label,
    std::string* note) {
    SyntaxRepairAttemptResult result = SyntaxRepairAttemptResult::kNoChange;
    std::unordered_set<std::size_t> seen_states;
    for (int attempt = 0; attempt < 64 && !validation_error.empty(); ++attempt) {
        const std::size_t state_hash =
            std::hash<std::string>{}(validation_error) ^
            (std::hash<std::string>{}(decompiled_source) + 0x9e3779b97f4a7c15ULL +
             (std::hash<std::string>{}(validation_error) << 6) +
             (std::hash<std::string>{}(validation_error) >> 2));
        if (!seen_states.insert(state_hash).second) {
            break;
        }

        if (TryRestoreMalformedLoopCallArguments(decompiled_source, validation_error, note)) {
            result = SyntaxRepairAttemptResult::kChanged;
            validation_error = ValidateLuaRepairSource(decompiled_source);
            if (!validation_error.empty() &&
                TryRebalanceNestedStateElseIfChains(
                    decompiled_source, validation_error, note)) {
                validation_error = ValidateLuaRepairSource(decompiled_source);
            }
            if (validation_error.empty()) {
                return SyntaxRepairAttemptResult::kValidated;
            }
            continue;
        }

        if (TryReplaceFakeGeneratorLoopWithSiblingElseIf(
                decompiled_source, validation_error, note)) {
            result = SyntaxRepairAttemptResult::kChanged;
            validation_error = ValidateLuaRepairSource(decompiled_source);
            if (validation_error.empty()) {
                return SyntaxRepairAttemptResult::kValidated;
            }
            continue;
        }

        if (TryReplaceFakeGeneratorLoopWithElseBreak(
                decompiled_source, validation_error, note)) {
            result = SyntaxRepairAttemptResult::kChanged;
            validation_error = ValidateLuaRepairSource(decompiled_source);
            if (validation_error.empty()) {
                return SyntaxRepairAttemptResult::kValidated;
            }
            continue;
        }

        if (TryRebalanceNestedStateElseIfChains(decompiled_source, validation_error, note)) {
            result = SyntaxRepairAttemptResult::kChanged;
            validation_error = ValidateLuaRepairSource(decompiled_source);
            if (validation_error.empty()) {
                return SyntaxRepairAttemptResult::kValidated;
            }
            continue;
        }

        if (TryRestoreNestedIfElseAfterDoubleEnd(
                decompiled_source, validation_error, note)) {
            result = SyntaxRepairAttemptResult::kChanged;
            validation_error = ValidateLuaRepairSource(decompiled_source);
            if (validation_error.empty()) {
                return SyntaxRepairAttemptResult::kValidated;
            }
            continue;
        }

        if (TryCollapseNestedElseIfInsideStateClause(
                decompiled_source, validation_error, note)) {
            result = SyntaxRepairAttemptResult::kChanged;
            validation_error = ValidateLuaRepairSource(decompiled_source);
            if (validation_error.empty()) {
                return SyntaxRepairAttemptResult::kValidated;
            }
            continue;
        }

        if (TryCollapseNestedElseIfInsideClauseImpl(
                decompiled_source, validation_error, note)) {
            result = SyntaxRepairAttemptResult::kChanged;
            validation_error = ValidateLuaRepairSource(decompiled_source);
            if (validation_error.empty()) {
                return SyntaxRepairAttemptResult::kValidated;
            }
            continue;
        }

        if (TryRebuildFlattenedStateGuardElseIfBranch(
                decompiled_source, validation_error, note)) {
            result = SyntaxRepairAttemptResult::kChanged;
            validation_error = ValidateLuaRepairSource(decompiled_source);
            if (validation_error.empty()) {
                return SyntaxRepairAttemptResult::kValidated;
            }
            continue;
        }

        if (TryRebuildFlattenedInteractionGuardElseIfBranch(
                decompiled_source, validation_error, note)) {
            result = SyntaxRepairAttemptResult::kChanged;
            validation_error = ValidateLuaRepairSource(decompiled_source);
            if (validation_error.empty()) {
                return SyntaxRepairAttemptResult::kValidated;
            }
            continue;
        }

        if (TryPromoteOuterStateElseIfAfterInteractiveCutscene(
                decompiled_source, validation_error, note)) {
            result = SyntaxRepairAttemptResult::kChanged;
            validation_error = ValidateLuaRepairSource(decompiled_source);
            if (validation_error.empty()) {
                return SyntaxRepairAttemptResult::kValidated;
            }
            continue;
        }

        if (TryInsertMissingEndBeforeOuterElseIf(
                decompiled_source, validation_error, note)) {
            result = SyntaxRepairAttemptResult::kChanged;
            validation_error = ValidateLuaRepairSource(decompiled_source);
            if (validation_error.empty()) {
                return SyntaxRepairAttemptResult::kValidated;
            }
            continue;
        }

        if (TryPromoteWeekGuardIfToLoopAndCollapseNestedInteractionWhile(
                decompiled_source, validation_error, note)) {
            result = SyntaxRepairAttemptResult::kChanged;
            validation_error = ValidateLuaRepairSource(decompiled_source);
            if (validation_error.empty()) {
                return SyntaxRepairAttemptResult::kValidated;
            }
            continue;
        }

        if (TryRebuildWeekCutsceneStateLadder(
                decompiled_source, validation_error, note)) {
            result = SyntaxRepairAttemptResult::kChanged;
            validation_error = ValidateLuaRepairSource(decompiled_source);
            if (validation_error.empty()) {
                return SyntaxRepairAttemptResult::kValidated;
            }
            continue;
        }

        if (TryRestoreWeeklyStateResumeFromSiblingTemplate(
                decompiled_source, validation_error, note)) {
            result = SyntaxRepairAttemptResult::kChanged;
            validation_error = ValidateLuaRepairSource(decompiled_source);
            if (validation_error.empty()) {
                return SyntaxRepairAttemptResult::kValidated;
            }
            continue;
        }

        if (TryRebalanceDanglingEndBeforeElseIf(
                decompiled_source, validation_error, note)) {
            result = SyntaxRepairAttemptResult::kChanged;
            validation_error = ValidateLuaRepairSource(decompiled_source);
            if (validation_error.empty()) {
                return SyntaxRepairAttemptResult::kValidated;
            }
            continue;
        }

        if (TryCloseInnerIfBeforeStateElse(
                decompiled_source, validation_error, note)) {
            result = SyntaxRepairAttemptResult::kChanged;
            validation_error = ValidateLuaRepairSource(decompiled_source);
            if (validation_error.empty()) {
                return SyntaxRepairAttemptResult::kValidated;
            }
            continue;
        }

        if (TryReframeLocalStateBranchAfterDanglingElse(
                decompiled_source, validation_error, note)) {
            result = SyntaxRepairAttemptResult::kChanged;
            validation_error = ValidateLuaRepairSource(decompiled_source);
            if (validation_error.empty()) {
                return SyntaxRepairAttemptResult::kValidated;
            }
            continue;
        }

        if (TryRebuildStateTransitionWaitHandoff(
                decompiled_source, validation_error, note)) {
            result = SyntaxRepairAttemptResult::kChanged;
            validation_error = ValidateLuaRepairSource(decompiled_source);
            if (validation_error.empty()) {
                return SyntaxRepairAttemptResult::kValidated;
            }
            continue;
        }

        if (TryReplaceDanglingElseElseIfWithSiblingIf(
                decompiled_source, validation_error, note)) {
            result = SyntaxRepairAttemptResult::kChanged;
            validation_error = ValidateLuaRepairSource(decompiled_source);
            if (validation_error.empty()) {
                return SyntaxRepairAttemptResult::kValidated;
            }
            continue;
        }

        if (TryInsertMissingWhileTrueBeforeYieldingStateBody(
                decompiled_source, validation_error, note)) {
            result = SyntaxRepairAttemptResult::kChanged;
            validation_error = ValidateLuaRepairSource(decompiled_source);
            if (validation_error.empty()) {
                return SyntaxRepairAttemptResult::kValidated;
            }
            continue;
        }

        if (TryCloseYieldingGuardLoopBeforeStateSetup(
                decompiled_source, validation_error, note)) {
            result = SyntaxRepairAttemptResult::kChanged;
            validation_error = ValidateLuaRepairSource(decompiled_source);
            if (validation_error.empty()) {
                return SyntaxRepairAttemptResult::kValidated;
            }
            continue;
        }

        if (TryRemoveEndsBeforeTopLevelBoundaries(
                decompiled_source, validation_error, note)) {
            result = SyntaxRepairAttemptResult::kChanged;
            validation_error = ValidateLuaRepairSource(decompiled_source);
            if (validation_error.empty()) {
                return SyntaxRepairAttemptResult::kValidated;
            }
            continue;
        }

        if (TryReplaceEmptyIfCallBeforeTopLevelBoundary(
                decompiled_source, validation_error, note)) {
            result = SyntaxRepairAttemptResult::kChanged;
            validation_error = ValidateLuaRepairSource(decompiled_source);
            if (validation_error.empty()) {
                return SyntaxRepairAttemptResult::kValidated;
            }
            continue;
        }

        if (TryPromoteNestedStateBranchesOutOfInnerElse(
                decompiled_source, validation_error, note)) {
            result = SyntaxRepairAttemptResult::kChanged;
            validation_error = ValidateLuaRepairSource(decompiled_source);
            if (validation_error.empty()) {
                return SyntaxRepairAttemptResult::kValidated;
            }
            continue;
        }

        if (TryPromoteStateElseDoWrapperToElseIfChain(
                decompiled_source, validation_error, note)) {
            result = SyntaxRepairAttemptResult::kChanged;
            validation_error = ValidateLuaRepairSource(decompiled_source);
            if (validation_error.empty()) {
                return SyntaxRepairAttemptResult::kValidated;
            }
            continue;
        }

        if (TryNormalizeTerminalGuardedStateFlagLoops(
                decompiled_source, validation_error, note)) {
            result = SyntaxRepairAttemptResult::kChanged;
            validation_error = ValidateLuaRepairSource(decompiled_source);
            if (validation_error.empty()) {
                return SyntaxRepairAttemptResult::kValidated;
            }
            continue;
        }

        if (TryRestoreSmallHelperFooterBeforeTopLevelBoundary(
                decompiled_source, validation_error, note)) {
            result = SyntaxRepairAttemptResult::kChanged;
            validation_error = ValidateLuaRepairSource(decompiled_source);
            if (validation_error.empty()) {
                return SyntaxRepairAttemptResult::kValidated;
            }
            continue;
        }

        if (TryRestoreBooleanMethodFooterBeforeSiblingAssignment(
                decompiled_source, validation_error, note)) {
            result = SyntaxRepairAttemptResult::kChanged;
            validation_error = ValidateLuaRepairSource(decompiled_source);
            if (validation_error.empty()) {
                return SyntaxRepairAttemptResult::kValidated;
            }
            continue;
        }

        if (TryRemoveStandaloneIdentifierLineBeforeCall(
                decompiled_source, validation_error, note)) {
            result = SyntaxRepairAttemptResult::kChanged;
            validation_error = ValidateLuaRepairSource(decompiled_source);
            if (validation_error.empty()) {
                return SyntaxRepairAttemptResult::kValidated;
            }
            continue;
        }

        if (TryRestoreDuplicatedTopLevelDebugKeyCallbackChain(
                decompiled_source, validation_error, note)) {
            result = SyntaxRepairAttemptResult::kChanged;
            validation_error = ValidateLuaRepairSource(decompiled_source);
            if (validation_error.empty()) {
                return SyntaxRepairAttemptResult::kValidated;
            }
            continue;
        }

        if (TryCloseReportedFunctionBeforeSiblingBoundary(
                decompiled_source, validation_error, note)) {
            result = SyntaxRepairAttemptResult::kChanged;
            validation_error = ValidateLuaRepairSource(decompiled_source);
            if (validation_error.empty()) {
                return SyntaxRepairAttemptResult::kValidated;
            }
            continue;
        }

        if (TryInsertEndsAcrossTopLevelAnonymousFunctionMethodCallChain(
                decompiled_source, validation_error, note)) {
            result = SyntaxRepairAttemptResult::kChanged;
            validation_error = ValidateLuaRepairSource(decompiled_source);
            if (validation_error.empty()) {
                return SyntaxRepairAttemptResult::kValidated;
            }
            continue;
        }

        if (TryInsertEndsAcrossTopLevelAnonymousFunctionChain(
                decompiled_source, validation_error, note)) {
            result = SyntaxRepairAttemptResult::kChanged;
            validation_error = ValidateLuaRepairSource(decompiled_source);
            if (validation_error.empty()) {
                return SyntaxRepairAttemptResult::kValidated;
            }
            continue;
        }

        if (TryInsertEndBeforeTopLevelAnonymousFunctionAssignment(
                decompiled_source, validation_error, note)) {
            result = SyntaxRepairAttemptResult::kChanged;
            validation_error = ValidateLuaRepairSource(decompiled_source);
            if (validation_error.empty()) {
                return SyntaxRepairAttemptResult::kValidated;
            }
            continue;
        }

        if (TryRebalanceTerminalMissionElseIf(decompiled_source, validation_error, note)) {
            result = SyntaxRepairAttemptResult::kChanged;
            validation_error = ValidateLuaRepairSource(decompiled_source);
            if (validation_error.empty()) {
                return SyntaxRepairAttemptResult::kValidated;
            }
            continue;
        }

        if (TryCloseTrueWaitWrapperBeforeStateAssignment(
                decompiled_source, validation_error, note)) {
            result = SyntaxRepairAttemptResult::kChanged;
            validation_error = ValidateLuaRepairSource(decompiled_source);
            if (validation_error.empty()) {
                return SyntaxRepairAttemptResult::kValidated;
            }
            continue;
        }

        if (TryRepairDuplicateElseGuardedWaitLoops(
                decompiled_source, validation_error, note)) {
            result = SyntaxRepairAttemptResult::kChanged;
            validation_error = ValidateLuaRepairSource(decompiled_source);
            if (validation_error.empty()) {
                return SyntaxRepairAttemptResult::kValidated;
            }
            continue;
        }

        if (TryCollapseWhileTrueGuardedElseEpilogue(
                decompiled_source, validation_error, note)) {
            result = SyntaxRepairAttemptResult::kChanged;
            validation_error = ValidateLuaRepairSource(decompiled_source);
            if (validation_error.empty()) {
                return SyntaxRepairAttemptResult::kValidated;
            }
            continue;
        }

        if (TryCloseOuterElseAfterNestedYieldWaitLoop(
                decompiled_source, validation_error, note)) {
            result = SyntaxRepairAttemptResult::kChanged;
            validation_error = ValidateLuaRepairSource(decompiled_source);
            if (validation_error.empty()) {
                return SyntaxRepairAttemptResult::kValidated;
            }
            continue;
        }

        if (TryRewriteInnerPollLoopAsIfElseWithFooter(
                decompiled_source, validation_error, note)) {
            result = SyntaxRepairAttemptResult::kChanged;
            validation_error = ValidateLuaRepairSource(decompiled_source);
            if (validation_error.empty()) {
                return SyntaxRepairAttemptResult::kValidated;
            }
            continue;
        }

        if (TryRebuildLevelLoadedWhileNotIntoIfElseWhile(
                decompiled_source, validation_error, note)) {
            result = SyntaxRepairAttemptResult::kChanged;
            validation_error = ValidateLuaRepairSource(decompiled_source);
            if (validation_error.empty()) {
                return SyntaxRepairAttemptResult::kValidated;
            }
            continue;
        }

        if (TryReplaceDanglingElseBeforeStateLadderWithEnd(
                decompiled_source, validation_error, note)) {
            result = SyntaxRepairAttemptResult::kChanged;
            validation_error = ValidateLuaRepairSource(decompiled_source);
            if (validation_error.empty()) {
                return SyntaxRepairAttemptResult::kValidated;
            }
            continue;
        }

        if (TryRepairEmptyBreakElseYieldWaitLoops(
                decompiled_source, validation_error, note)) {
            result = SyntaxRepairAttemptResult::kChanged;
            validation_error = ValidateLuaRepairSource(decompiled_source);
            if (validation_error.empty()) {
                return SyntaxRepairAttemptResult::kValidated;
            }
            continue;
        }

        if (TryRepairStrayEndPollElseYieldWaitLoops(
                decompiled_source, validation_error, note)) {
            result = SyntaxRepairAttemptResult::kChanged;
            validation_error = ValidateLuaRepairSource(decompiled_source);
            if (validation_error.empty()) {
                return SyntaxRepairAttemptResult::kValidated;
            }
            continue;
        }

        if (TryRepairStrandedPollElseYieldWaitLoops(
                decompiled_source, validation_error, note)) {
            result = SyntaxRepairAttemptResult::kChanged;
            validation_error = ValidateLuaRepairSource(decompiled_source);
            if (validation_error.empty()) {
                return SyntaxRepairAttemptResult::kValidated;
            }
            continue;
        }

        if (TryTrimTerminalIdleLoopEndPrefixes(
                decompiled_source, validation_error, note)) {
            result = SyntaxRepairAttemptResult::kChanged;
            validation_error = ValidateLuaRepairSource(decompiled_source);
            if (validation_error.empty()) {
                return SyntaxRepairAttemptResult::kValidated;
            }
            continue;
        }

        break;
    }

    return result;
}

}  // namespace refii::lua::internal
