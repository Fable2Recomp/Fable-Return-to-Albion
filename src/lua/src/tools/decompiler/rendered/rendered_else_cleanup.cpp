#include "tools/decompiler/rendered/rendered_lua_text.h"
#include "tools/decompiler/rendered/rendered_cleanup_common_helpers.h"
#include "tools/decompiler/straight_line_render.h"

#include <algorithm>
#include <cstddef>
#include <string>
#include <string_view>
#include <vector>

namespace refii::lua {

bool SimplifyRenderedLuaNestedGuardedDuplicateBodies(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto find_block_end = [](const std::vector<std::string>& local_lines,
                                   std::size_t start_line) -> std::size_t {
        if (start_line >= local_lines.size()) {
            return local_lines.size();
        }

        int balance = 0;
        for (std::size_t line_index = start_line; line_index < local_lines.size(); ++line_index) {
            balance += GetRenderedLuaBlockDelta(
                TrimLuaWhitespace(StripLuaLineComment(local_lines[line_index])));
            if (line_index > start_line && balance == 0) {
                return line_index;
            }
        }
        return local_lines.size();
    };

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 6 < lines->size(); ++line_index) {
        std::string outer_condition;
        std::string inner_condition;
        if (!TryExtractRenderedIfCondition((*lines)[line_index], &outer_condition) ||
            !TryExtractRenderedIfCondition((*lines)[line_index + 1], &inner_condition)) {
            continue;
        }

        const std::size_t nested_end = find_block_end(*lines, line_index + 1);
        const std::size_t outer_end = find_block_end(*lines, line_index);
        if (nested_end >= lines->size() || outer_end >= lines->size() ||
            nested_end + 1 >= outer_end ||
            TrimLuaWhitespace(StripLuaLineComment((*lines)[nested_end + 1])) != "else") {
            continue;
        }

        const std::size_t nested_body_start = line_index + 2;
        const std::size_t nested_body_end = nested_end;
        const std::size_t else_body_start = nested_end + 2;
        const std::size_t else_body_end = outer_end;
        const std::size_t body_size = nested_body_end - nested_body_start;
        if (body_size == 0 || body_size > 24 ||
            else_body_end - else_body_start != body_size) {
            continue;
        }

        bool bodies_are_simple = true;
        bool duplicate_matches = true;
        for (std::size_t offset = 0; offset < body_size; ++offset) {
            const std::string_view nested_trimmed = TrimLuaWhitespace(
                StripLuaLineComment((*lines)[nested_body_start + offset]));
            const std::string_view else_trimmed = TrimLuaWhitespace(
                StripLuaLineComment((*lines)[else_body_start + offset]));
            if (nested_trimmed != else_trimmed) {
                duplicate_matches = false;
                break;
            }
            if (nested_trimmed == "else" || nested_trimmed == "end" ||
                LooksLikeRenderedControlBlockHeader(nested_trimmed)) {
                bodies_are_simple = false;
                break;
            }
        }
        if (!duplicate_matches || !bodies_are_simple) {
            continue;
        }

        bool negated_changed = false;
        const std::string positive_outer =
            NegateRenderedConditionExpression(outer_condition, &negated_changed);
        if (positive_outer.empty()) {
            continue;
        }

        const std::string combined_condition =
            RenderLogicalExpression(positive_outer, "or", inner_condition);
        if (combined_condition.empty()) {
            continue;
        }

        const std::string outer_indent(GetRenderedLuaIndent((*lines)[line_index]));
        const std::string inner_indent = outer_indent + "  ";
        std::vector<std::string> replacement;
        replacement.reserve(body_size + 2);
        replacement.push_back(outer_indent + "if " + combined_condition + " then");
        for (std::size_t offset = 0; offset < body_size; ++offset) {
            replacement.push_back(
                inner_indent +
                std::string(TrimLuaWhitespace(
                    StripLuaLineComment((*lines)[nested_body_start + offset]))));
        }
        replacement.push_back(outer_indent + "end");

        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                     lines->begin() + static_cast<std::ptrdiff_t>(outer_end + 1));
        lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                      replacement.begin(),
                      replacement.end());
        changed = true;
    }

    return changed;
}

bool SimplifyRenderedLuaNestedGuardedFallbackDuplicateBodies(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto join_boolean_parts = [](const std::vector<std::string>& parts,
                                       std::size_t begin,
                                       std::size_t end,
                                       const char* op) -> std::string {
        if (begin >= end || end > parts.size()) {
            return {};
        }

        std::string expression = parts[begin];
        for (std::size_t index = begin + 1; index < end; ++index) {
            expression = RenderLogicalExpression(expression, op, parts[index]);
            if (expression.empty()) {
                return {};
            }
        }
        return expression;
    };

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 6 < lines->size(); ++line_index) {
        std::string outer_condition;
        std::string nested_condition;
        if (!TryExtractRenderedIfCondition((*lines)[line_index], &outer_condition) ||
            !TryExtractRenderedIfCondition((*lines)[line_index + 1], &nested_condition)) {
            continue;
        }

        const std::size_t nested_end = FindRenderedCleanupBlockEnd(*lines, line_index + 1);
        const std::size_t outer_end = FindRenderedCleanupBlockEnd(*lines, line_index);
        if (nested_end >= lines->size() || outer_end >= lines->size() ||
            outer_end != nested_end + 1 ||
            RenderedLuaBlockHasTopLevelElse(*lines, line_index, outer_end) ||
            RenderedLuaBlockHasTopLevelElse(*lines, line_index + 1, nested_end)) {
            continue;
        }

        const std::size_t fallback_start = outer_end + 1;
        if (fallback_start >= lines->size()) {
            continue;
        }

        std::string fallback_condition;
        if (!TryExtractRenderedIfCondition((*lines)[fallback_start], &fallback_condition)) {
            continue;
        }

        const std::size_t fallback_end = FindRenderedCleanupBlockEnd(*lines, fallback_start);
        if (fallback_end >= lines->size() ||
            RenderedLuaBlockHasTopLevelElse(*lines, fallback_start, fallback_end)) {
            continue;
        }

        const std::size_t nested_body_start = line_index + 2;
        const std::size_t nested_body_end = nested_end;
        const std::size_t fallback_body_start = fallback_start + 1;
        const std::size_t fallback_body_end = fallback_end;
        const std::size_t nested_body_size = nested_body_end - nested_body_start;
        const std::size_t fallback_body_size = fallback_body_end - fallback_body_start;
        if (nested_body_size == 0 || nested_body_size > 24 ||
            nested_body_size != fallback_body_size) {
            continue;
        }

        bool bodies_are_simple = true;
        bool bodies_match = true;
        for (std::size_t offset = 0; offset < nested_body_size; ++offset) {
            const std::string_view nested_trimmed = TrimLuaWhitespace(
                StripLuaLineComment((*lines)[nested_body_start + offset]));
            const std::string_view fallback_trimmed = TrimLuaWhitespace(
                StripLuaLineComment((*lines)[fallback_body_start + offset]));
            if (nested_trimmed.empty() || nested_trimmed == "else" || nested_trimmed == "end" ||
                LooksLikeRenderedControlBlockHeader(nested_trimmed)) {
                bodies_are_simple = false;
                break;
            }
            if (nested_trimmed == fallback_trimmed) {
                continue;
            }

            bool nested_is_local = false;
            std::string nested_lhs;
            std::string nested_rhs;
            bool fallback_is_local = false;
            std::string fallback_lhs;
            std::string fallback_rhs;
            const bool equivalent_shadowed_assignment =
                ParseRenderedSingleAssignmentLine(nested_trimmed,
                                                 &nested_is_local,
                                                 &nested_lhs,
                                                 &nested_rhs) &&
                ParseRenderedSingleAssignmentLine(fallback_trimmed,
                                                 &fallback_is_local,
                                                 &fallback_lhs,
                                                 &fallback_rhs) &&
                nested_lhs == fallback_lhs &&
                TrimLuaWhitespace(nested_rhs) == TrimLuaWhitespace(fallback_rhs) &&
                nested_is_local != fallback_is_local;
            if (!equivalent_shadowed_assignment) {
                bodies_match = false;
                break;
            }
        }
        if (!bodies_match || !bodies_are_simple) {
            continue;
        }

        bool outer_negated_changed = false;
        const std::string positive_outer =
            NegateRenderedConditionExpression(outer_condition, &outer_negated_changed);
        if (positive_outer.empty()) {
            continue;
        }

        const std::string normalized_nested =
            NormalizeRenderedConditionExpression(nested_condition, nullptr);
        const std::string normalized_fallback =
            NormalizeRenderedConditionExpression(fallback_condition, nullptr);
        if (normalized_nested.empty() || normalized_fallback.empty() ||
            normalized_nested == normalized_fallback) {
            continue;
        }

        std::vector<std::string> nested_parts;
        if (!SplitRenderedTopLevelBooleanChain(normalized_nested, "and", &nested_parts) ||
            nested_parts.size() < 2) {
            continue;
        }

        std::string prefix_condition;
        for (std::size_t split = 1; split < nested_parts.size(); ++split) {
            const std::string suffix =
                join_boolean_parts(nested_parts, split, nested_parts.size(), "and");
            if (suffix != normalized_fallback) {
                continue;
            }

            prefix_condition = join_boolean_parts(nested_parts, 0, split, "and");
            break;
        }
        if (prefix_condition.empty() && normalized_nested.size() > normalized_fallback.size() &&
            normalized_nested.ends_with(normalized_fallback)) {
            std::string prefix = std::string(TrimLuaWhitespace(normalized_nested.substr(
                0, normalized_nested.size() - normalized_fallback.size())));
            if (prefix.size() >= 5 && prefix.ends_with(" and")) {
                prefix.resize(prefix.size() - 4);
                prefix_condition = std::string(TrimLuaWhitespace(prefix));
            }
        }
        if (prefix_condition.empty()) {
            continue;
        }

        const std::string outer_or_prefix =
            RenderLogicalExpression(positive_outer, "or", prefix_condition);
        const std::string combined_condition =
            RenderLogicalExpression(outer_or_prefix, "and", normalized_fallback);
        if (combined_condition.empty()) {
            continue;
        }

        const std::string outer_indent(GetRenderedLuaIndent((*lines)[line_index]));
        const std::string inner_indent = outer_indent + "  ";
        std::vector<std::string> replacement;
        replacement.reserve(nested_body_size + 2);
        replacement.push_back(outer_indent + "if " + combined_condition + " then");
        for (std::size_t offset = 0; offset < nested_body_size; ++offset) {
            replacement.push_back(
                inner_indent +
                std::string(TrimLuaWhitespace(
                    StripLuaLineComment((*lines)[nested_body_start + offset]))));
        }
        replacement.push_back(outer_indent + "end");

        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                     lines->begin() + static_cast<std::ptrdiff_t>(fallback_end + 1));
        lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                      replacement.begin(),
                      replacement.end());
        changed = true;
        if (line_index != 0) {
            --line_index;
        }
    }

    return changed;
}

bool SimplifyRenderedLuaNestedGuardedAssignmentFallbackElse(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 5 < lines->size(); ++line_index) {
        std::string outer_condition;
        std::string inner_condition;
        if (!TryExtractRenderedIfCondition((*lines)[line_index], &outer_condition) ||
            !TryExtractRenderedIfCondition((*lines)[line_index + 1], &inner_condition)) {
            continue;
        }

        const std::size_t inner_end = FindRenderedCleanupBlockEnd(*lines, line_index + 1);
        const std::size_t outer_end = FindRenderedCleanupBlockEnd(*lines, line_index);
        if (inner_end >= lines->size() || outer_end >= lines->size() ||
            inner_end != line_index + 3 || outer_end != line_index + 4 ||
            RenderedLuaBlockHasTopLevelElse(*lines, line_index, outer_end) ||
            RenderedLuaBlockHasTopLevelElse(*lines, line_index + 1, inner_end)) {
            continue;
        }

        bool then_is_local = false;
        std::string then_lhs;
        std::string then_rhs;
        bool fallback_is_local = false;
        std::string fallback_lhs;
        std::string fallback_rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[line_index + 2],
                                               &then_is_local,
                                               &then_lhs,
                                               &then_rhs) ||
            then_is_local ||
            !ParseRenderedSingleAssignmentLine((*lines)[line_index + 5],
                                               &fallback_is_local,
                                               &fallback_lhs,
                                               &fallback_rhs) ||
            fallback_is_local || then_lhs != fallback_lhs) {
            continue;
        }

        const std::string combined_condition =
            RenderLogicalExpression(outer_condition, "and", inner_condition);
        if (combined_condition.empty()) {
            continue;
        }

        const std::string indent(GetRenderedLuaIndent((*lines)[line_index]));
        const std::string inner_indent = indent + "  ";
        std::vector<std::string> replacement = {
            indent + "if " + combined_condition + " then",
            inner_indent + then_lhs + " = " + then_rhs,
            indent + "else",
            inner_indent + fallback_lhs + " = " + fallback_rhs,
            indent + "end",
        };

        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                     lines->begin() + static_cast<std::ptrdiff_t>(line_index + 6));
        lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                      replacement.begin(),
                      replacement.end());
        changed = true;
        if (line_index != 0) {
            --line_index;
        }
    }

    return changed;
}

bool SimplifyRenderedLuaEmptyElseBranches(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        const std::string_view trimmed_if =
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index]));
        if ((!trimmed_if.starts_with("if ") || !trimmed_if.ends_with(" then")) &&
            (!trimmed_if.starts_with("elseif ") || !trimmed_if.ends_with(" then"))) {
            continue;
        }

        const std::size_t block_end = FindRenderedCleanupBlockEnd(*lines, line_index);
        if (block_end >= lines->size() || block_end <= line_index + 1) {
            continue;
        }

        std::size_t else_line = lines->size();
        int depth = 1;
        for (std::size_t probe = line_index + 1; probe < block_end; ++probe) {
            const std::string_view probe_trimmed =
                TrimLuaWhitespace(StripLuaLineComment((*lines)[probe]));
            if (probe_trimmed.empty()) {
                continue;
            }
            if (depth == 1 && probe_trimmed == "else") {
                else_line = probe;
                break;
            }
            depth += GetRenderedLuaBlockDelta(probe_trimmed);
        }
        if (else_line >= block_end ||
            FindRenderedLastNonEmptyLine(*lines, else_line + 1, block_end) != lines->size()) {
            continue;
        }

        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(else_line));
        changed = true;
        if (line_index != 0) {
            --line_index;
        }
    }

    return changed;
}

bool SimplifyRenderedLuaOuterElseBreakIntoInnerElseBreak(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto next_nonempty_noncomment =
        [&](std::size_t start, std::size_t end_exclusive) -> std::size_t {
        for (std::size_t probe = start; probe < end_exclusive && probe < lines->size(); ++probe) {
            const std::string_view trimmed = TrimLuaWhitespace((*lines)[probe]);
            if (trimmed.empty() || trimmed.starts_with("--")) {
                continue;
            }
            return probe;
        }
        return end_exclusive;
    };

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        std::string outer_condition;
        if (!TryExtractRenderedIfCondition((*lines)[line_index], &outer_condition)) {
            continue;
        }

        const std::size_t outer_end = FindRenderedCleanupBlockEnd(*lines, line_index);
        if (outer_end >= lines->size() ||
            !RenderedLuaBlockHasTopLevelElse(*lines, line_index, outer_end)) {
            continue;
        }

        std::size_t else_line = outer_end;
        int depth = 1;
        for (std::size_t probe = line_index + 1; probe < outer_end; ++probe) {
            const std::string_view trimmed =
                TrimLuaWhitespace(StripLuaLineComment((*lines)[probe]));
            if (trimmed.empty()) {
                continue;
            }
            if (depth == 1 && trimmed == "else") {
                else_line = probe;
                break;
            }
            depth += GetRenderedLuaBlockDelta(trimmed);
        }
        if (else_line >= outer_end) {
            continue;
        }

        const std::size_t inner_if_line =
            next_nonempty_noncomment(line_index + 1, else_line);
        std::string inner_condition;
        if (inner_if_line >= else_line ||
            !TryExtractRenderedIfCondition((*lines)[inner_if_line], &inner_condition)) {
            continue;
        }

        const std::size_t inner_end = FindRenderedCleanupBlockEnd(*lines, inner_if_line);
        if (inner_end >= else_line ||
            RenderedLuaBlockHasTopLevelElse(*lines, inner_if_line, inner_end)) {
            continue;
        }

        const std::size_t after_inner =
            next_nonempty_noncomment(inner_end + 1, else_line);
        if (after_inner != else_line) {
            continue;
        }

        const std::size_t break_line =
            next_nonempty_noncomment(else_line + 1, outer_end);
        if (break_line >= outer_end ||
            TrimLuaWhitespace(StripLuaLineComment((*lines)[break_line])) != "break") {
            continue;
        }

        const std::size_t trailing_else_body =
            next_nonempty_noncomment(break_line + 1, outer_end);
        if (trailing_else_body != outer_end) {
            continue;
        }

        const std::string inner_indent(GetRenderedLuaIndent((*lines)[inner_if_line]));
        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(else_line),
                     lines->begin() + static_cast<std::ptrdiff_t>(outer_end));
        lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(inner_end),
                      inner_indent + "else");
        lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(inner_end + 1),
                      inner_indent + "  break");
        changed = true;
        if (line_index != 0) {
            --line_index;
        }
    }

    return changed;
}

bool SimplifyRenderedLuaAdjacentSimpleSiblingElseIfChains(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimLuaWhitespace(StripLuaLineComment(line));
    };
    const auto extract_selector_lhs = [](std::string_view condition, std::string* lhs_out) {
        if (lhs_out == nullptr) {
            return false;
        }

        const std::string normalized =
            StripRenderedWholeParentheses(TrimLuaWhitespace(condition), nullptr);
        std::size_t compare_position = std::string::npos;
        std::string_view compare_op;
        if (!FindRenderedTopLevelComparison(normalized, &compare_position, &compare_op)) {
            return false;
        }
        if (compare_op != "==" && compare_op != "~=") {
            return false;
        }

        const std::string lhs = NormalizeRenderedConditionOperand(
            normalized.substr(0, compare_position), nullptr);
        const std::string rhs = NormalizeRenderedConditionOperand(
            normalized.substr(compare_position + compare_op.size()), nullptr);
        if (lhs.empty()) {
            return false;
        }
        if (lhs == rhs) {
            return false;
        }

        *lhs_out = lhs;
        return true;
    };
    const auto next_nonempty_noncomment =
        [&](std::size_t start, std::size_t end_exclusive) -> std::size_t {
        for (std::size_t probe = start; probe < end_exclusive && probe < lines->size(); ++probe) {
            const std::string_view trimmed = TrimLuaWhitespace((*lines)[probe]);
            if (trimmed.empty() || trimmed.starts_with("--")) {
                continue;
            }
            return probe;
        }
        return end_exclusive;
    };

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        std::string first_condition;
        if (!TryExtractRenderedIfCondition((*lines)[line_index], &first_condition)) {
            continue;
        }

        const std::size_t first_end_for_duplicate =
            FindRenderedCleanupBlockEnd(*lines, line_index);
        if (first_end_for_duplicate < lines->size() &&
            !RenderedLuaBlockHasTopLevelElse(*lines, line_index, first_end_for_duplicate)) {
            const std::size_t second_if_line_for_duplicate =
                next_nonempty_noncomment(first_end_for_duplicate + 1, lines->size());
            std::string second_condition_for_duplicate;
            if (second_if_line_for_duplicate < lines->size() &&
                GetRenderedLuaIndent((*lines)[second_if_line_for_duplicate]) ==
                    GetRenderedLuaIndent((*lines)[line_index]) &&
                TryExtractRenderedIfCondition((*lines)[second_if_line_for_duplicate],
                                             &second_condition_for_duplicate)) {
                const std::size_t second_end_for_duplicate =
                    FindRenderedCleanupBlockEnd(*lines, second_if_line_for_duplicate);
                if (second_end_for_duplicate < lines->size() &&
                    !RenderedLuaBlockHasTopLevelElse(*lines,
                                                     second_if_line_for_duplicate,
                                                     second_end_for_duplicate)) {
                    const std::string normalized_first_condition =
                        StripRenderedWholeParentheses(
                            TrimLuaWhitespace(first_condition), nullptr);
                    const std::string normalized_second_condition =
                        StripRenderedWholeParentheses(
                            TrimLuaWhitespace(second_condition_for_duplicate), nullptr);
                    const std::size_t first_body_start = line_index + 1;
                    const std::size_t second_body_start = second_if_line_for_duplicate + 1;
                    const std::size_t first_body_size =
                        first_end_for_duplicate > first_body_start
                            ? first_end_for_duplicate - first_body_start
                            : 0;
                    const std::size_t second_body_size =
                        second_end_for_duplicate > second_body_start
                            ? second_end_for_duplicate - second_body_start
                            : 0;
                    if (!normalized_first_condition.empty() &&
                        normalized_first_condition == normalized_second_condition &&
                        first_body_size != 0 &&
                        first_body_size == second_body_size &&
                        first_body_size <= 24) {
                        bool bodies_match = true;
                        bool bodies_simple = true;
                        for (std::size_t offset = 0; offset < first_body_size; ++offset) {
                            const std::string_view first_trimmed = trim_line(
                                (*lines)[first_body_start + offset]);
                            const std::string_view second_trimmed = trim_line(
                                (*lines)[second_body_start + offset]);
                            if (first_trimmed != second_trimmed) {
                                bodies_match = false;
                                break;
                            }
                            if (first_trimmed == "else" || first_trimmed == "end" ||
                                first_trimmed.starts_with("elseif ") ||
                                LooksLikeRenderedControlBlockHeader(first_trimmed) ||
                                StartsWithLuaKeyword(first_trimmed, "function")) {
                                bodies_simple = false;
                                break;
                            }
                        }
                        if (bodies_match && bodies_simple) {
                            lines->erase(
                                lines->begin() +
                                    static_cast<std::ptrdiff_t>(second_if_line_for_duplicate),
                                lines->begin() +
                                    static_cast<std::ptrdiff_t>(second_end_for_duplicate + 1));
                            changed = true;
                            if (line_index != 0) {
                                --line_index;
                            }
                            continue;
                        }
                    }
                }
            }
        }

        std::string first_selector_lhs;
        if (!extract_selector_lhs(first_condition, &first_selector_lhs)) {
            continue;
        }

        const std::size_t first_end = FindRenderedCleanupBlockEnd(*lines, line_index);
        if (first_end >= lines->size() ||
            RenderedLuaBlockHasTopLevelElse(*lines, line_index, first_end)) {
            continue;
        }

        const std::size_t second_if_line =
            next_nonempty_noncomment(first_end + 1, lines->size());
        std::string second_condition;
        if (second_if_line >= lines->size() ||
            GetRenderedLuaIndent((*lines)[second_if_line]) !=
                GetRenderedLuaIndent((*lines)[line_index]) ||
            !TryExtractRenderedIfCondition((*lines)[second_if_line], &second_condition)) {
            continue;
        }
        std::string second_selector_lhs;
        if (!extract_selector_lhs(second_condition, &second_selector_lhs) ||
            second_selector_lhs != first_selector_lhs) {
            continue;
        }

        const std::size_t second_end = FindRenderedCleanupBlockEnd(*lines, second_if_line);
        if (second_end >= lines->size() ||
            RenderedLuaBlockHasTopLevelElse(*lines, second_if_line, second_end)) {
            continue;
        }

        bool second_body_supported = true;
        for (std::size_t probe = second_if_line + 1; probe < second_end; ++probe) {
            const std::string_view trimmed = trim_line((*lines)[probe]);
            if (trimmed.empty()) {
                continue;
            }
            if (trimmed == "end" || trimmed == "else" ||
                trimmed.starts_with("elseif ") ||
                LooksLikeRenderedControlBlockHeader(trimmed) ||
                StartsWithLuaKeyword(trimmed, "return") ||
                StartsWithLuaKeyword(trimmed, "function")) {
                second_body_supported = false;
                break;
            }
        }
        if (!second_body_supported) {
            continue;
        }

        const std::string indent(GetRenderedLuaIndent((*lines)[line_index]));
        (*lines)[first_end] = indent + "elseif " + second_condition + " then";
        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(second_if_line));
        changed = true;
        if (line_index != 0) {
            --line_index;
        }
    }

    return changed;
}

bool SimplifyRenderedLuaGuardedTailBreakIntoPreviousIf(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimLuaWhitespace(StripLuaLineComment(line));
    };
    const auto next_nonempty_noncomment =
        [&](std::size_t start, std::size_t end_exclusive) -> std::size_t {
        for (std::size_t probe = start; probe < end_exclusive && probe < lines->size(); ++probe) {
            const std::string_view trimmed = TrimLuaWhitespace((*lines)[probe]);
            if (trimmed.empty() || trimmed.starts_with("--")) {
                continue;
            }
            return probe;
        }
        return end_exclusive;
    };

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        std::string first_condition;
        if (!TryExtractRenderedIfCondition((*lines)[line_index], &first_condition)) {
            continue;
        }

        const std::size_t first_end = FindRenderedCleanupBlockEnd(*lines, line_index);
        if (first_end >= lines->size() ||
            RenderedLuaBlockHasTopLevelElse(*lines, line_index, first_end)) {
            continue;
        }

        const std::size_t second_if_line =
            next_nonempty_noncomment(first_end + 1, lines->size());
        std::string second_condition;
        if (second_if_line >= lines->size() ||
            GetRenderedLuaIndent((*lines)[second_if_line]) !=
                GetRenderedLuaIndent((*lines)[line_index]) ||
            !TryExtractRenderedIfCondition((*lines)[second_if_line], &second_condition)) {
            continue;
        }

        const std::size_t second_end = FindRenderedCleanupBlockEnd(*lines, second_if_line);
        if (second_end >= lines->size() ||
            RenderedLuaBlockHasTopLevelElse(*lines, second_if_line, second_end)) {
            continue;
        }

        const std::size_t break_line =
            next_nonempty_noncomment(second_if_line + 1, second_end);
        if (break_line >= second_end || trim_line((*lines)[break_line]) != "break") {
            continue;
        }
        const std::size_t trailing_second_body =
            next_nonempty_noncomment(break_line + 1, second_end);
        if (trailing_second_body != second_end) {
            continue;
        }

        const std::size_t next_after_second =
            next_nonempty_noncomment(second_end + 1, lines->size());
        if (next_after_second >= lines->size() ||
            trim_line((*lines)[next_after_second]).find("coroutine.yield(") ==
                std::string_view::npos) {
            continue;
        }

        std::vector<std::string> moved_block(lines->begin() +
                                                 static_cast<std::ptrdiff_t>(second_if_line),
                                             lines->begin() +
                                                 static_cast<std::ptrdiff_t>(second_end + 1));
        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(second_if_line),
                     lines->begin() + static_cast<std::ptrdiff_t>(second_end + 1));
        lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(first_end),
                      moved_block.begin(),
                      moved_block.end());
        changed = true;
        if (line_index != 0) {
            --line_index;
        }
    }

    return changed;
}

bool SimplifyRenderedLuaTailElseIfBreakIntoNestedIf(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimLuaWhitespace(StripLuaLineComment(line));
    };
    const auto try_extract_elseif_condition =
        [&](std::string_view line, std::string* condition) -> bool {
        if (condition == nullptr) {
            return false;
        }

        line = trim_line(line);
        if (!line.starts_with("elseif ") || !line.ends_with(" then")) {
            return false;
        }

        *condition = std::string(TrimLuaWhitespace(line.substr(7, line.size() - 7 - 5)));
        return !condition->empty();
    };
    const auto next_nonempty_noncomment =
        [&](std::size_t start, std::size_t end_exclusive) -> std::size_t {
        for (std::size_t probe = start; probe < end_exclusive && probe < lines->size(); ++probe) {
            const std::string_view trimmed = TrimLuaWhitespace((*lines)[probe]);
            if (trimmed.empty() || trimmed.starts_with("--")) {
                continue;
            }
            return probe;
        }
        return end_exclusive;
    };

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        std::string outer_condition;
        if (!TryExtractRenderedIfCondition((*lines)[line_index], &outer_condition)) {
            continue;
        }

        const std::size_t outer_end = FindRenderedCleanupBlockEnd(*lines, line_index);
        if (outer_end >= lines->size()) {
            continue;
        }

        std::size_t top_level_elseif_line = lines->size();
        bool saw_top_level_else = false;
        bool saw_multiple_top_level_elseifs = false;
        int depth = 1;
        for (std::size_t probe = line_index + 1; probe < outer_end; ++probe) {
            const std::string_view trimmed = trim_line((*lines)[probe]);
            if (trimmed.empty()) {
                continue;
            }

            if (depth == 1) {
                if (trimmed.starts_with("elseif ") && trimmed.ends_with(" then")) {
                    if (top_level_elseif_line != lines->size()) {
                        saw_multiple_top_level_elseifs = true;
                        break;
                    }
                    top_level_elseif_line = probe;
                } else if (trimmed == "else") {
                    saw_top_level_else = true;
                    break;
                }
            }

            depth += GetRenderedLuaBlockDelta(trimmed);
        }

        if (saw_top_level_else || saw_multiple_top_level_elseifs ||
            top_level_elseif_line >= outer_end) {
            continue;
        }

        const std::size_t first_body_start =
            next_nonempty_noncomment(line_index + 1, top_level_elseif_line);
        if (first_body_start >= top_level_elseif_line ||
            !LooksLikeRenderedControlBlockHeader(trim_line((*lines)[first_body_start]))) {
            continue;
        }

        const std::size_t first_body_end = FindRenderedCleanupBlockEnd(*lines, first_body_start);
        if (first_body_end >= top_level_elseif_line ||
            next_nonempty_noncomment(first_body_end + 1, top_level_elseif_line) !=
                top_level_elseif_line) {
            continue;
        }

        std::string elseif_condition;
        if (!try_extract_elseif_condition((*lines)[top_level_elseif_line], &elseif_condition)) {
            continue;
        }

        const std::size_t break_line =
            next_nonempty_noncomment(top_level_elseif_line + 1, outer_end);
        if (break_line >= outer_end || trim_line((*lines)[break_line]) != "break") {
            continue;
        }

        const std::size_t trailing_elseif_body =
            next_nonempty_noncomment(break_line + 1, outer_end);
        if (trailing_elseif_body != outer_end) {
            continue;
        }

        const std::size_t next_after_outer =
            next_nonempty_noncomment(outer_end + 1, lines->size());
        if (next_after_outer >= lines->size() ||
            trim_line((*lines)[next_after_outer]).find("coroutine.yield(") ==
                std::string_view::npos) {
            continue;
        }

        std::vector<std::string> moved_block(lines->begin() +
                                                 static_cast<std::ptrdiff_t>(top_level_elseif_line),
                                             lines->begin() +
                                                 static_cast<std::ptrdiff_t>(outer_end));
        if (moved_block.empty()) {
            continue;
        }

        const std::string indent(GetRenderedLuaIndent((*lines)[top_level_elseif_line]));
        moved_block.front() = indent + "if " + elseif_condition + " then";
        moved_block.push_back(indent + "end");

        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(top_level_elseif_line),
                     lines->begin() + static_cast<std::ptrdiff_t>(outer_end));
        lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(top_level_elseif_line),
                      moved_block.begin(),
                      moved_block.end());
        changed = true;
        if (line_index != 0) {
            --line_index;
        }
    }

    return changed;
}

bool SimplifyRenderedLuaImmediateMissingElseAssignments(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 4 < lines->size(); ++line_index) {
        const std::string_view trimmed_if =
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index]));
        if (!trimmed_if.starts_with("if ") || !trimmed_if.ends_with(" then")) {
            continue;
        }

        const std::size_t block_end = FindRenderedCleanupBlockEnd(*lines, line_index);
        if (block_end >= lines->size() || block_end <= line_index + 2 ||
            RenderedLuaBlockHasTopLevelElse(*lines, line_index, block_end)) {
            continue;
        }

        const std::size_t then_assignment =
            FindRenderedLastNonEmptyLine(*lines, line_index + 1, block_end);
        if (then_assignment >= block_end) {
            continue;
        }

        bool then_is_local = false;
        std::string then_lhs;
        std::string then_rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[then_assignment],
                                               &then_is_local,
                                               &then_lhs,
                                               &then_rhs) ||
            then_is_local) {
            continue;
        }

        std::size_t fallback_assignment = lines->size();
        for (std::size_t probe = block_end + 1; probe < lines->size(); ++probe) {
            const std::string_view probe_trimmed =
                TrimLuaWhitespace(StripLuaLineComment((*lines)[probe]));
            if (probe_trimmed.empty()) {
                continue;
            }
            fallback_assignment = probe;
            break;
        }
        if (fallback_assignment >= lines->size()) {
            continue;
        }

        bool fallback_is_local = false;
        std::string fallback_lhs;
        std::string fallback_rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[fallback_assignment],
                                               &fallback_is_local,
                                               &fallback_lhs,
                                               &fallback_rhs) ||
            fallback_is_local || fallback_lhs != then_lhs) {
            continue;
        }

        std::size_t after_fallback = lines->size();
        for (std::size_t probe = fallback_assignment + 1; probe < lines->size(); ++probe) {
            const std::string_view probe_trimmed =
                TrimLuaWhitespace(StripLuaLineComment((*lines)[probe]));
            if (probe_trimmed.empty()) {
                continue;
            }
            after_fallback = probe;
            break;
        }
        if (after_fallback >= lines->size()) {
            continue;
        }

        const std::string_view after_fallback_trimmed =
            TrimLuaWhitespace(StripLuaLineComment((*lines)[after_fallback]));
        if (after_fallback_trimmed != "else" && !after_fallback_trimmed.starts_with("elseif ") &&
            after_fallback_trimmed != "end" && !StartsWithLuaKeyword(after_fallback_trimmed, "return")) {
            continue;
        }

        const std::string nested_indent(GetRenderedLuaIndent((*lines)[line_index]));
        const std::string fallback_line = (*lines)[fallback_assignment];
        (*lines)[block_end] = nested_indent + "else";
        lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(block_end + 1),
                      fallback_line);
        lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(block_end + 2),
                      nested_indent + "end");
        lines->erase(lines->begin() +
                     static_cast<std::ptrdiff_t>(fallback_assignment + 2));
        changed = true;
    }

    return changed;
}

bool SimplifyRenderedLuaImmediateMissingElseTrailingTableCalls(
    std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    struct ParsedTrailingTableCallBlock {
        std::size_t end_line = 0;
        std::string call_prefix;
        std::string trailer_suffix;
        std::vector<std::pair<std::string, std::string>> fields;
    };

    const auto parse_table_field = [](std::string_view line,
                                      std::string* lhs,
                                      std::string* rhs) -> bool {
        std::string text(std::string(TrimLuaWhitespace(StripLuaLineComment(line))));
        if (text.empty()) {
            return false;
        }
        if (!text.empty() && text.back() == ',') {
            text.pop_back();
        }

        bool is_local = false;
        return ParseRenderedSingleAssignmentLine(text, &is_local, lhs, rhs) && !is_local;
    };

    const auto parse_block =
        [&](const std::vector<std::string>& local_lines,
            std::size_t start_line,
            ParsedTrailingTableCallBlock* block) -> bool {
        if (block == nullptr || start_line >= local_lines.size()) {
            return false;
        }

        const std::string_view header =
            TrimLuaWhitespace(StripLuaLineComment(local_lines[start_line]));
        const std::size_t open_brace = header.rfind('{');
        if (open_brace == std::string_view::npos) {
            return false;
        }

        const std::string_view call_prefix = TrimLuaWhitespace(header.substr(0, open_brace));
        const std::string_view post_brace = TrimLuaWhitespace(header.substr(open_brace + 1));
        if (call_prefix.empty() || post_brace != "" || call_prefix.find('(') == std::string_view::npos) {
            return false;
        }

        int brace_depth = 1;
        bool in_single_quote = false;
        bool in_double_quote = false;
        std::size_t end_line = local_lines.size();
        for (std::size_t probe = start_line + 1; probe < local_lines.size(); ++probe) {
            const std::string_view stripped = StripLuaLineComment(local_lines[probe]);
            for (const char ch : stripped) {
                if (ch == '\'' && !in_double_quote) {
                    in_single_quote = !in_single_quote;
                    continue;
                }
                if (ch == '"' && !in_single_quote) {
                    in_double_quote = !in_double_quote;
                    continue;
                }
                if (in_single_quote || in_double_quote) {
                    continue;
                }
                if (ch == '{') {
                    ++brace_depth;
                } else if (ch == '}') {
                    --brace_depth;
                }
            }
            if (brace_depth == 0) {
                end_line = probe;
                break;
            }
        }
        if (end_line >= local_lines.size() || end_line <= start_line + 1) {
            return false;
        }

        const std::string_view end_trimmed =
            TrimLuaWhitespace(StripLuaLineComment(local_lines[end_line]));
        const std::size_t close_brace = end_trimmed.find('}');
        if (close_brace == std::string_view::npos ||
            TrimLuaWhitespace(end_trimmed.substr(0, close_brace)) != "") {
            return false;
        }

        const std::string_view trailer_suffix =
            TrimLuaWhitespace(end_trimmed.substr(close_brace + 1));
        if (trailer_suffix.empty() || trailer_suffix.front() != ')') {
            return false;
        }

        block->fields.clear();
        for (std::size_t probe = start_line + 1; probe < end_line; ++probe) {
            const std::string_view trimmed =
                TrimLuaWhitespace(StripLuaLineComment(local_lines[probe]));
            if (trimmed.empty()) {
                continue;
            }

            std::string field_lhs;
            std::string field_rhs;
            if (!parse_table_field(trimmed, &field_lhs, &field_rhs)) {
                return false;
            }
            block->fields.emplace_back(field_lhs,
                                       std::string(TrimLuaWhitespace(field_rhs)));
        }
        if (block->fields.empty()) {
            return false;
        }

        block->end_line = end_line;
        block->call_prefix = std::string(call_prefix);
        block->trailer_suffix = std::string(trailer_suffix);
        return true;
    };

    const auto fallback_is_subset = [](const ParsedTrailingTableCallBlock& then_block,
                                       const ParsedTrailingTableCallBlock& fallback_block) {
        if (then_block.call_prefix != fallback_block.call_prefix ||
            then_block.trailer_suffix != fallback_block.trailer_suffix ||
            then_block.fields.size() <= fallback_block.fields.size()) {
            return false;
        }

        std::size_t search_index = 0;
        for (const auto& [fallback_name, fallback_value] : fallback_block.fields) {
            bool matched = false;
            for (; search_index < then_block.fields.size(); ++search_index) {
                const auto& [then_name, then_value] = then_block.fields[search_index];
                if (then_name == fallback_name &&
                    TrimLuaWhitespace(then_value) == TrimLuaWhitespace(fallback_value)) {
                    matched = true;
                    ++search_index;
                    break;
                }
            }
            if (!matched) {
                return false;
            }
        }

        return true;
    };

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 5 < lines->size(); ++line_index) {
        const std::string_view trimmed_if =
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index]));
        if (!trimmed_if.starts_with("if ") || !trimmed_if.ends_with(" then")) {
            continue;
        }

        const std::size_t block_end = FindRenderedCleanupBlockEnd(*lines, line_index);
        if (block_end >= lines->size() || block_end <= line_index + 2 ||
            RenderedLuaBlockHasTopLevelElse(*lines, line_index, block_end)) {
            continue;
        }

        std::size_t then_start = lines->size();
        for (std::size_t probe = line_index + 1; probe < block_end; ++probe) {
            const std::string_view trimmed =
                TrimLuaWhitespace(StripLuaLineComment((*lines)[probe]));
            if (!trimmed.empty()) {
                then_start = probe;
                break;
            }
        }
        if (then_start >= block_end) {
            continue;
        }

        ParsedTrailingTableCallBlock then_block;
        if (!parse_block(*lines, then_start, &then_block) || then_block.end_line != block_end - 1) {
            continue;
        }

        std::size_t fallback_start = lines->size();
        for (std::size_t probe = block_end + 1; probe < lines->size(); ++probe) {
            const std::string_view trimmed =
                TrimLuaWhitespace(StripLuaLineComment((*lines)[probe]));
            if (!trimmed.empty()) {
                fallback_start = probe;
                break;
            }
        }
        if (fallback_start >= lines->size()) {
            continue;
        }

        ParsedTrailingTableCallBlock fallback_block;
        if (!parse_block(*lines, fallback_start, &fallback_block) ||
            !fallback_is_subset(then_block, fallback_block)) {
            continue;
        }

        std::size_t after_fallback = lines->size();
        for (std::size_t probe = fallback_block.end_line + 1; probe < lines->size(); ++probe) {
            const std::string_view trimmed =
                TrimLuaWhitespace(StripLuaLineComment((*lines)[probe]));
            if (trimmed.empty()) {
                continue;
            }
            after_fallback = probe;
            break;
        }
        if (after_fallback >= lines->size()) {
            continue;
        }

        const std::string_view after_fallback_trimmed =
            TrimLuaWhitespace(StripLuaLineComment((*lines)[after_fallback]));
        if (after_fallback_trimmed != "else" && !after_fallback_trimmed.starts_with("elseif ") &&
            after_fallback_trimmed != "end" && !StartsWithLuaKeyword(after_fallback_trimmed, "return")) {
            continue;
        }

        const std::vector<std::string> fallback_lines(
            lines->begin() + static_cast<std::ptrdiff_t>(fallback_start),
            lines->begin() + static_cast<std::ptrdiff_t>(fallback_block.end_line + 1));
        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(fallback_start),
                     lines->begin() + static_cast<std::ptrdiff_t>(fallback_block.end_line + 1));

        const std::string indent(GetRenderedLuaIndent((*lines)[line_index]));
        (*lines)[block_end] = indent + "else";
        lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(block_end + 1),
                      fallback_lines.begin(),
                      fallback_lines.end());
        lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(block_end + 1 + fallback_lines.size()),
                      indent + "end");
        changed = true;
        if (line_index != 0) {
            --line_index;
        }
    }

    return changed;
}

bool SimplifyRenderedLuaCurrentStateMissingGuardElseBodies(
    std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimLuaWhitespace(StripLuaLineComment(line));
    };
    const auto parse_current_state_condition =
        [&](std::string_view line, std::string* state_lhs) -> bool {
        if (state_lhs == nullptr) {
            return false;
        }

        std::string condition;
        if (!TryExtractRenderedIfCondition(line, &condition)) {
            return false;
        }

        std::size_t compare_position = std::string::npos;
        std::string_view compare_op;
        if (!FindRenderedTopLevelComparison(condition, &compare_position, &compare_op) ||
            compare_op != "==") {
            return false;
        }

        const std::string lhs = NormalizeRenderedConditionOperand(
            condition.substr(0, compare_position), nullptr);
        const std::string rhs = NormalizeRenderedConditionExpression(
            condition.substr(compare_position + compare_op.size()), nullptr);
        if (lhs.empty() || rhs.empty() || lhs.find("CurrentState") == std::string::npos) {
            return false;
        }

        *state_lhs = lhs;
        return true;
    };
    const auto find_branch_boundary =
        [&](std::size_t branch_line) -> std::size_t {
        if (branch_line >= lines->size()) {
            return lines->size();
        }

        int depth = 1;
        for (std::size_t probe = branch_line + 1; probe < lines->size(); ++probe) {
            const std::string_view trimmed = trim_line((*lines)[probe]);
            if (trimmed.empty()) {
                continue;
            }

            if (depth == 1 &&
                (trimmed == "else" || trimmed.starts_with("elseif "))) {
                return probe;
            }

            depth += GetRenderedLuaBlockDelta(trimmed);
            if (depth == 0) {
                return probe;
            }
        }
        return lines->size();
    };
    const auto first_nonempty = [&](std::size_t begin,
                                    std::size_t end) -> std::size_t {
        for (std::size_t probe = begin; probe < end && probe < lines->size(); ++probe) {
            if (!trim_line((*lines)[probe]).empty()) {
                return probe;
            }
        }
        return lines->size();
    };
    const auto append_reindented_range = [&](std::vector<std::string>* replacement,
                                             std::size_t begin,
                                             std::size_t end,
                                             std::string_view target_indent) {
        if (replacement == nullptr || begin > end || end > lines->size()) {
            return false;
        }

        for (std::size_t probe = begin; probe < end; ++probe) {
            const std::string_view raw_trimmed = TrimLuaWhitespace((*lines)[probe]);
            const std::string_view stripped_trimmed = trim_line((*lines)[probe]);
            if (raw_trimmed.empty()) {
                replacement->push_back(std::string(raw_trimmed));
                continue;
            }

            if (raw_trimmed.starts_with("--")) {
                replacement->push_back(std::string(target_indent) + std::string(raw_trimmed));
                continue;
            }

            if (stripped_trimmed.empty()) {
                continue;
            }

            const std::string source_indent(GetRenderedLuaIndent((*lines)[probe]));
            if (!source_indent.empty() && (*lines)[probe].starts_with(source_indent)) {
                replacement->push_back(
                    std::string(target_indent) + (*lines)[probe].substr(source_indent.size()));
            } else {
                replacement->push_back(std::string(target_indent) +
                                       std::string(stripped_trimmed));
            }
        }
        return true;
    };

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        std::string state_lhs;
        if (!parse_current_state_condition((*lines)[line_index], &state_lhs)) {
            continue;
        }

        const std::string branch_indent(GetRenderedLuaIndent((*lines)[line_index]));
        const std::size_t branch_boundary = find_branch_boundary(line_index);
        if (branch_boundary >= lines->size() || branch_boundary <= line_index + 2) {
            continue;
        }

        for (std::size_t probe = line_index + 1; probe < branch_boundary; ++probe) {
            const std::string_view probe_trimmed = trim_line((*lines)[probe]);
            if (probe_trimmed.empty() ||
                GetRenderedLuaIndent((*lines)[probe]) != branch_indent ||
                !probe_trimmed.starts_with("if ") ||
                !probe_trimmed.ends_with(" then")) {
                continue;
            }

            std::string inner_condition;
            if (!TryExtractRenderedIfCondition((*lines)[probe], &inner_condition)) {
                continue;
            }
            if (NormalizeRenderedConditionOperand(inner_condition, nullptr) == state_lhs) {
                continue;
            }

            const std::size_t inner_end = FindRenderedCleanupBlockEnd(*lines, probe);
            if (inner_end >= branch_boundary || inner_end <= probe + 1 ||
                RenderedLuaBlockHasTopLevelElse(*lines, probe, inner_end)) {
                continue;
            }

            const std::size_t fallback_start = first_nonempty(inner_end + 1, branch_boundary);
            if (fallback_start >= branch_boundary) {
                continue;
            }

            bool saw_state_transition = false;
            bool fallback_is_simple = true;
            for (std::size_t fallback_line = fallback_start; fallback_line < branch_boundary;
                 ++fallback_line) {
                const std::string_view fallback_trimmed = trim_line((*lines)[fallback_line]);
                if (fallback_trimmed.empty()) {
                    continue;
                }
                if (fallback_trimmed == "else" || fallback_trimmed == "end" ||
                    fallback_trimmed.starts_with("elseif ") ||
                    LooksLikeRenderedControlBlockHeader(fallback_trimmed)) {
                    fallback_is_simple = false;
                    break;
                }
                if (GetRenderedLuaIndent((*lines)[fallback_line]) != branch_indent) {
                    fallback_is_simple = false;
                    break;
                }

                bool assign_is_local = false;
                std::string assign_lhs;
                std::string assign_rhs;
                if (ParseRenderedSingleAssignmentLine((*lines)[fallback_line],
                                                     &assign_is_local,
                                                     &assign_lhs,
                                                     &assign_rhs) &&
                    !assign_is_local && assign_lhs == state_lhs) {
                    saw_state_transition = true;
                }
            }
            if (!fallback_is_simple || !saw_state_transition) {
                continue;
            }

            std::vector<std::string> replacement;
            replacement.push_back(branch_indent + "else");
            if (!append_reindented_range(&replacement,
                                         fallback_start,
                                         branch_boundary,
                                         branch_indent + "  ")) {
                continue;
            }
            replacement.push_back(branch_indent + "end");

            lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(inner_end),
                         lines->begin() + static_cast<std::ptrdiff_t>(branch_boundary));
            lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(inner_end),
                          replacement.begin(),
                          replacement.end());
            changed = true;
            if (line_index != 0) {
                --line_index;
            }
            break;
        }
    }

    return changed;
}


bool ExpandRenderedLuaTerminalEmptyElseIfChainsForParity(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimLuaWhitespace(StripLuaLineComment(line));
    };
    const auto next_nonempty_noncomment =
        [&](std::size_t start, std::size_t limit) -> std::size_t {
        for (std::size_t probe = start; probe < limit && probe < lines->size(); ++probe) {
            const std::string_view raw = TrimLuaWhitespace((*lines)[probe]);
            if (raw.empty() || raw.starts_with("--")) {
                continue;
            }
            return probe;
        }
        return limit;
    };

    struct BranchSpan {
        std::size_t header_line = 0;
        std::size_t body_start = 0;
        std::size_t body_end = 0;
    };

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        const std::string_view chain_head = trim_line((*lines)[line_index]);
        if (!chain_head.starts_with("if ") || !chain_head.ends_with(" then")) {
            continue;
        }

        const std::size_t block_end = FindRenderedCleanupBlockEnd(*lines, line_index);
        if (block_end >= lines->size() || trim_line((*lines)[block_end]) != "end") {
            continue;
        }

        std::vector<BranchSpan> branches;
        std::size_t current_header = line_index;
        std::size_t body_start = line_index + 1;
        int depth = 1;
        for (std::size_t probe = line_index + 1; probe < block_end; ++probe) {
            const std::string_view trimmed = trim_line((*lines)[probe]);
            if (trimmed.empty()) {
                continue;
            }

            if (depth == 1 &&
                (trimmed == "else" ||
                 (trimmed.starts_with("elseif ") && trimmed.ends_with(" then")))) {
                branches.push_back({current_header, body_start, probe});
                current_header = probe;
                body_start = probe + 1;
                continue;
            }

            depth += GetRenderedLuaBlockDelta(trimmed);
            if (depth == 0) {
                break;
            }
        }
        branches.push_back({current_header, body_start, block_end});

        if (branches.size() < 2) {
            continue;
        }

        const BranchSpan& terminal_branch = branches.back();
        const std::string_view branch_header =
            trim_line((*lines)[terminal_branch.header_line]);
        if (!branch_header.starts_with("elseif ") || !branch_header.ends_with(" then")) {
            continue;
        }

        if (next_nonempty_noncomment(terminal_branch.body_start, terminal_branch.body_end) !=
            terminal_branch.body_end) {
            continue;
        }

        const std::string_view condition_header =
            trim_line((*lines)[terminal_branch.header_line]);
        std::string condition;
        if (!condition_header.starts_with("elseif ") ||
            !condition_header.ends_with(" then")) {
            continue;
        }
        condition = std::string(TrimLuaWhitespace(
            condition_header.substr(7, condition_header.size() - 7 - 5)));
        if (condition.empty()) {
            continue;
        }

        const std::string indent(GetRenderedLuaIndent((*lines)[terminal_branch.header_line]));
        const std::string inner_indent = indent + "  ";
        (*lines)[terminal_branch.header_line] = indent + "else";
        lines->insert(lines->begin() +
                          static_cast<std::ptrdiff_t>(terminal_branch.header_line + 1),
                      inner_indent + "if " + condition + " then");

        std::size_t adjusted_block_end = block_end;
        if (adjusted_block_end > terminal_branch.header_line) {
            ++adjusted_block_end;
        }
        lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(adjusted_block_end),
                      inner_indent + "else");
        lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(adjusted_block_end + 1),
                      inner_indent + "end");

        changed = true;
        if (line_index != 0) {
            --line_index;
        }
    }

    return changed;
}

bool ExpandRenderedLuaReturnThenFallbackElseForParity(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimLuaWhitespace(StripLuaLineComment(line));
    };

    const auto next_nonempty_noncomment = [&](std::size_t start) -> std::size_t {
        for (std::size_t probe = start; probe < lines->size(); ++probe) {
            if (!trim_line((*lines)[probe]).empty()) {
                return probe;
            }
        }
        return lines->size();
    };

    const auto range_ends_with_top_level_return = [&](std::size_t begin,
                                                      std::size_t end) -> bool {
        if (next_nonempty_noncomment(begin) >= end) {
            return false;
        }
        const std::size_t last = FindRenderedLastNonEmptyLine(*lines, begin, end);
        return last < end && trim_line((*lines)[last]).starts_with("return");
    };

    const auto find_fallback_boundary = [&](std::size_t start,
                                            std::string_view indent) -> std::size_t {
        int depth = 0;
        for (std::size_t probe = start; probe < lines->size(); ++probe) {
            const std::string_view trimmed = trim_line((*lines)[probe]);
            if (trimmed.empty()) {
                continue;
            }
            if (depth == 0 && GetRenderedLuaIndent((*lines)[probe]) == indent &&
                (trimmed == "else" || trimmed == "end" || trimmed.starts_with("elseif "))) {
                return probe;
            }
            depth += GetRenderedLuaBlockDelta(trimmed);
        }
        return lines->size();
    };

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        const std::string_view header = trim_line((*lines)[line_index]);
        if (!header.starts_with("if ") || !header.ends_with(" then")) {
            continue;
        }

        const std::size_t block_end = FindRenderedCleanupBlockEnd(*lines, line_index);
        if (block_end >= lines->size() || trim_line((*lines)[block_end]) != "end" ||
            RenderedLuaBlockHasTopLevelElse(*lines, line_index, block_end)) {
            continue;
        }
        if (!range_ends_with_top_level_return(line_index + 1, block_end)) {
            continue;
        }

        const std::string indent(GetRenderedLuaIndent((*lines)[line_index]));
        const std::size_t boundary = find_fallback_boundary(block_end + 1, indent);
        if (boundary >= lines->size() || boundary <= block_end + 1) {
            continue;
        }

        const std::size_t fallback_last =
            FindRenderedLastNonEmptyLine(*lines, block_end + 1, boundary);
        if (fallback_last >= lines->size() ||
            !trim_line((*lines)[fallback_last]).starts_with("return")) {
            continue;
        }

        (*lines)[block_end] = indent + "else";
        lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(boundary), indent + "end");
        changed = true;
        line_index = boundary;
    }

    return changed;
}

bool SimplifyRenderedLuaElseIfBeforeElseTails(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimLuaWhitespace(StripLuaLineComment(line));
    };
    const auto next_nonempty_noncomment =
        [&](std::size_t start) -> std::size_t {
        for (std::size_t probe = start; probe < lines->size(); ++probe) {
            const std::string_view raw = TrimLuaWhitespace((*lines)[probe]);
            if (raw.empty() || raw.starts_with("--")) {
                continue;
            }
            return probe;
        }
        return lines->size();
    };

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        std::string condition;
        if (!TryExtractRenderedIfCondition((*lines)[line_index], &condition)) {
            continue;
        }

        const std::string_view trimmed = trim_line((*lines)[line_index]);
        if (!trimmed.starts_with("elseif ") || !trimmed.ends_with(" then")) {
            continue;
        }

        const std::string indent(GetRenderedLuaIndent((*lines)[line_index]));
        const std::size_t branch_end = FindRenderedCleanupBlockEnd(*lines, line_index);
        if (branch_end >= lines->size() || trim_line((*lines)[branch_end]) != "end") {
            continue;
        }

        const std::size_t trailing_else_line = next_nonempty_noncomment(branch_end + 1);
        if (trailing_else_line >= lines->size() ||
            GetRenderedLuaIndent((*lines)[trailing_else_line]) != indent ||
            trim_line((*lines)[trailing_else_line]) != "else") {
            continue;
        }

        (*lines)[line_index] = indent + "else";
        lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(line_index + 1),
                      indent + "if " + condition + " then");
        changed = true;
        ++line_index;
    }

    return changed;
}

bool SimplifyRenderedLuaDanglingCallAssignmentHeads(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimLuaWhitespace(StripLuaLineComment(line));
    };
    const auto try_extract_call_statement = [&](std::string_view line,
                                                std::string* expression) -> bool {
        if (expression == nullptr) {
            return false;
        }

        const std::string_view trimmed = trim_line(line);
        if (trimmed.empty() || LooksLikeRenderedControlBlockHeader(trimmed) ||
            StartsWithLuaKeyword(trimmed, "return") ||
            StartsWithLuaKeyword(trimmed, "local") ||
            StartsWithLuaKeyword(trimmed, "break")) {
            return false;
        }

        const std::size_t open_paren = trimmed.find('(');
        if (open_paren == std::string_view::npos) {
            return false;
        }

        const std::size_t equals = trimmed.find('=');
        if (equals != std::string_view::npos && equals < open_paren) {
            return false;
        }

        const std::string_view base = TrimLuaWhitespace(trimmed.substr(0, open_paren));
        if (base.empty() || !IsRenderedSafePrefixExpression(base)) {
            return false;
        }

        *expression = std::string(trimmed);
        return true;
    };

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 1 < lines->size(); ++line_index) {
        const std::string_view lhs_trimmed = trim_line((*lines)[line_index]);
        if (lhs_trimmed.empty() || !IsRenderedSafePrefixExpression(lhs_trimmed) ||
            lhs_trimmed.find('(') != std::string_view::npos ||
            IsLuaKeyword(lhs_trimmed) ||
            LooksLikeRenderedControlBlockHeader(lhs_trimmed) ||
            StartsWithLuaKeyword(lhs_trimmed, "return") ||
            StartsWithLuaKeyword(lhs_trimmed, "local") ||
            StartsWithLuaKeyword(lhs_trimmed, "break")) {
            continue;
        }

        std::string call_expression;
        if (!try_extract_call_statement((*lines)[line_index + 1], &call_expression) ||
            GetRenderedLuaIndent((*lines)[line_index]) !=
                GetRenderedLuaIndent((*lines)[line_index + 1])) {
            continue;
        }

        const std::string indent(GetRenderedLuaIndent((*lines)[line_index]));
        (*lines)[line_index] =
            indent + std::string(lhs_trimmed) + " = " + call_expression;
        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index + 1));
        changed = true;
        if (line_index != 0) {
            --line_index;
        }
    }

    return changed;
}

bool SimplifyRenderedLuaNestedDuplicateElseAssignments(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 6 < lines->size(); ++line_index) {
        const std::string_view trimmed_if =
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index]));
        if (!trimmed_if.starts_with("if ") || !trimmed_if.ends_with(" then")) {
            continue;
        }

        const std::size_t block_end = FindRenderedCleanupBlockEnd(*lines, line_index);
        if (block_end >= lines->size() || block_end <= line_index + 5) {
            continue;
        }

        std::size_t else_line = lines->size();
        int depth = 1;
        for (std::size_t probe = line_index + 1; probe < block_end; ++probe) {
            const std::string_view probe_trimmed =
                TrimLuaWhitespace(StripLuaLineComment((*lines)[probe]));
            if (probe_trimmed.empty()) {
                continue;
            }
            if (depth == 1 && probe_trimmed == "else") {
                else_line = probe;
                break;
            }
            depth += GetRenderedLuaBlockDelta(probe_trimmed);
        }
        if (else_line >= block_end) {
            continue;
        }

        const std::size_t else_assignment =
            FindRenderedLastNonEmptyLine(*lines, else_line + 1, block_end);
        if (else_assignment >= block_end) {
            continue;
        }
        if (FindRenderedLastNonEmptyLine(*lines, else_line + 1, else_assignment) !=
            lines->size()) {
            continue;
        }

        bool else_is_local = false;
        std::string else_lhs;
        std::string else_rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[else_assignment],
                                               &else_is_local,
                                               &else_lhs,
                                               &else_rhs) ||
            else_is_local) {
            continue;
        }

        const std::size_t then_fallback_assignment =
            FindRenderedLastNonEmptyLine(*lines, line_index + 1, else_line);
        if (then_fallback_assignment >= else_line) {
            continue;
        }

        bool fallback_is_local = false;
        std::string fallback_lhs;
        std::string fallback_rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[then_fallback_assignment],
                                               &fallback_is_local,
                                               &fallback_lhs,
                                               &fallback_rhs) ||
            fallback_is_local || fallback_lhs != else_lhs ||
            TrimLuaWhitespace(fallback_rhs) != TrimLuaWhitespace(else_rhs)) {
            continue;
        }

        const std::size_t nested_end =
            FindRenderedLastNonEmptyLine(*lines, line_index + 1, then_fallback_assignment);
        if (nested_end >= then_fallback_assignment ||
            TrimLuaWhitespace(StripLuaLineComment((*lines)[nested_end])) != "end") {
            continue;
        }

        std::size_t nested_start = lines->size();
        for (std::size_t probe = line_index + 1; probe < nested_end; ++probe) {
            const std::string_view probe_trimmed =
                TrimLuaWhitespace(StripLuaLineComment((*lines)[probe]));
            if (probe_trimmed.empty()) {
                continue;
            }
            if (probe_trimmed.starts_with("if ") && probe_trimmed.ends_with(" then")) {
                const std::size_t probe_end = FindRenderedCleanupBlockEnd(*lines, probe);
                if (probe_end == nested_end) {
                    nested_start = probe;
                }
                break;
            }
            if (probe_trimmed == "else" || probe_trimmed == "end" ||
                LooksLikeRenderedControlBlockHeader(probe_trimmed)) {
                break;
            }
        }
        if (nested_start >= nested_end ||
            RenderedLuaBlockHasTopLevelElse(*lines, nested_start, nested_end)) {
            continue;
        }

        const std::size_t then_assignment =
            FindRenderedLastNonEmptyLine(*lines, nested_start + 1, nested_end);
        if (then_assignment >= nested_end) {
            continue;
        }

        bool then_is_local = false;
        std::string then_lhs;
        std::string then_rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[then_assignment],
                                               &then_is_local,
                                               &then_lhs,
                                               &then_rhs) ||
            then_is_local || then_lhs != else_lhs) {
            continue;
        }

        const std::string nested_indent(GetRenderedLuaIndent((*lines)[nested_start]));
        const std::string fallback_line = (*lines)[then_fallback_assignment];
        (*lines)[nested_end] = nested_indent + "else";
        lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(nested_end + 1),
                      fallback_line);
        lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(nested_end + 2),
                      nested_indent + "end");
        lines->erase(lines->begin() +
                     static_cast<std::ptrdiff_t>(then_fallback_assignment + 2));
        changed = true;
    }

    return changed;
}

bool ExpandRenderedLuaPrepareToRunBooleanReturns(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto looks_like_boolean_prepare_expression = [](std::string_view expression) {
        expression = TrimLuaWhitespace(expression);
        if (expression.empty()) {
            return false;
        }

        for (std::string_view token : {"==", "~=", "<=", ">=", "<", ">"}) {
            if (expression.find(token) != std::string_view::npos) {
                return true;
            }
        }

        if (!expression.ends_with(')')) {
            return false;
        }

        const std::size_t open = expression.find('(');
        if (open == std::string_view::npos || open == 0) {
            return false;
        }

        std::string_view callee = TrimLuaWhitespace(expression.substr(0, open));
        const std::size_t last_access = callee.find_last_of(".:");
        if (last_access != std::string_view::npos && last_access + 1 < callee.size()) {
            callee = callee.substr(last_access + 1);
        }

        return callee.starts_with("Is") || callee.starts_with("Has") ||
               callee.starts_with("Can") || callee.starts_with("Should") ||
               callee.starts_with("Needs") || callee.starts_with("Want");
    };

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 2 < lines->size(); ++line_index) {
        const std::string_view trimmed_header =
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index]));
        if (trimmed_header.find("PrepareToRun") == std::string_view::npos ||
            trimmed_header.find("function") == std::string_view::npos) {
            continue;
        }

        const std::size_t block_end = FindRenderedCleanupBlockEnd(*lines, line_index);
        if (block_end >= lines->size() || block_end <= line_index + 1 ||
            TrimLuaWhitespace(StripLuaLineComment((*lines)[block_end])) != "end") {
            continue;
        }

        std::size_t return_line = lines->size();
        for (std::size_t probe = line_index + 1; probe < block_end; ++probe) {
            const std::string_view trimmed =
                TrimLuaWhitespace(StripLuaLineComment((*lines)[probe]));
            if (trimmed.empty()) {
                continue;
            }
            if (return_line != lines->size()) {
                return_line = lines->size();
                break;
            }
            return_line = probe;
        }
        if (return_line >= block_end) {
            continue;
        }

        const std::string_view trimmed_return =
            TrimLuaWhitespace(StripLuaLineComment((*lines)[return_line]));
        if (!trimmed_return.starts_with("return ")) {
            continue;
        }

        const std::string return_expression =
            std::string(TrimLuaWhitespace(trimmed_return.substr(7)));
        if (return_expression.empty() ||
            !looks_like_boolean_prepare_expression(return_expression)) {
            continue;
        }

        const std::string indent(GetRenderedLuaIndent((*lines)[line_index]));
        const std::string inner_indent = indent + "  ";
        const std::string nested_indent = inner_indent + "  ";
        std::vector<std::string> replacement_lines = {
            (*lines)[line_index],
            inner_indent + "if " + return_expression + " then",
            nested_indent + "return true",
            inner_indent + "end",
            inner_indent + "return false",
            indent + "end",
        };
        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                     lines->begin() + static_cast<std::ptrdiff_t>(block_end + 1));
        lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                      replacement_lines.begin(),
                      replacement_lines.end());
        changed = true;
        if (line_index != 0) {
            --line_index;
        }
    }

    return changed;
}

bool SimplifyRenderedLuaDuplicateIfElseReturnsForParity(std::vector<std::string>* lines) {
    if (lines == nullptr || lines->size() < 5) {
        return false;
    }

    const auto trim_line = [](std::string_view line) -> std::string {
        return std::string(TrimLuaWhitespace(StripLuaLineComment(line)));
    };

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 4 < lines->size(); ++line_index) {
        const std::string header = trim_line((*lines)[line_index]);
        if (!header.starts_with("if ") || !header.ends_with(" then")) {
            continue;
        }

        const std::string indent(GetRenderedLuaIndent((*lines)[line_index]));
        const std::string then_return = trim_line((*lines)[line_index + 1]);
        const std::string else_line = trim_line((*lines)[line_index + 2]);
        const std::string else_return = trim_line((*lines)[line_index + 3]);
        const std::string end_line = trim_line((*lines)[line_index + 4]);
        if (!then_return.starts_with("return") || else_line != "else" ||
            else_return != then_return || end_line != "end") {
            continue;
        }

        (*lines)[line_index + 1] = indent + "end";
        (*lines)[line_index + 2] = indent + then_return;
        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index + 3),
                     lines->begin() + static_cast<std::ptrdiff_t>(line_index + 5));
        changed = true;
    }

    return changed;
}

bool ExpandRenderedLuaTerminalIfActionEmptyElseForParity(std::vector<std::string>* lines) {
    if (lines == nullptr || lines->size() < 4) {
        return false;
    }

    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimLuaWhitespace(StripLuaLineComment(line));
    };

    const auto next_nonempty = [&](std::size_t start) -> std::size_t {
        for (std::size_t probe = start; probe < lines->size(); ++probe) {
            if (!trim_line((*lines)[probe]).empty()) {
                return probe;
            }
        }
        return lines->size();
    };

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 2 < lines->size(); ++line_index) {
        std::string condition;
        if (!TryExtractRenderedIfCondition((*lines)[line_index], &condition)) {
            continue;
        }

        const std::size_t block_end = FindRenderedCleanupBlockEnd(*lines, line_index);
        if (block_end >= lines->size() || block_end <= line_index + 1 ||
            trim_line((*lines)[block_end]) != "end" ||
            RenderedLuaBlockHasTopLevelElse(*lines, line_index, block_end)) {
            continue;
        }

        const std::size_t after_block = next_nonempty(block_end + 1);
        if (after_block >= lines->size() || trim_line((*lines)[after_block]) != "end") {
            continue;
        }

        const std::string if_indent(GetRenderedLuaIndent((*lines)[line_index]));
        const std::string after_indent(GetRenderedLuaIndent((*lines)[after_block]));
        if (after_indent.size() >= if_indent.size()) {
            continue;
        }

        bool body_has_effect = false;
        bool body_is_simple = true;
        std::size_t nonempty_body_lines = 0;
        for (std::size_t probe = line_index + 1; probe < block_end; ++probe) {
            const std::string_view trimmed = trim_line((*lines)[probe]);
            if (trimmed.empty()) {
                continue;
            }
            ++nonempty_body_lines;
            if (nonempty_body_lines > 8 || trimmed.starts_with("return") ||
                trimmed == "break" || trimmed.starts_with("goto ") ||
                LooksLikeRenderedControlBlockHeader(trimmed)) {
                body_is_simple = false;
                break;
            }
            body_has_effect = true;
        }
        if (!body_has_effect || !body_is_simple) {
            continue;
        }

        (*lines)[block_end] = if_indent + "else";
        lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(block_end + 1),
                      if_indent + "end");
        changed = true;
        line_index = block_end + 1;
    }

    return changed;
}

bool CollapseRenderedLuaNestedDuplicateReturnGuardsForParity(std::vector<std::string>* lines) {
    if (lines == nullptr || lines->size() < 7) {
        return false;
    }

    const auto trim_line = [](std::string_view line) -> std::string {
        return std::string(TrimLuaWhitespace(StripLuaLineComment(line)));
    };

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 6 < lines->size(); ++line_index) {
        std::string outer_condition;
        std::string inner_condition;
        if (!TryExtractRenderedIfCondition((*lines)[line_index], &outer_condition) ||
            !TryExtractRenderedIfCondition((*lines)[line_index + 1], &inner_condition)) {
            continue;
        }

        const std::string then_return = trim_line((*lines)[line_index + 2]);
        const std::string else_line = trim_line((*lines)[line_index + 3]);
        const std::string else_return = trim_line((*lines)[line_index + 4]);
        const std::string inner_end = trim_line((*lines)[line_index + 5]);
        if (!then_return.starts_with("return") || else_line != "else" ||
            else_return != then_return || inner_end != "end") {
            continue;
        }

        const std::string combined_condition =
            RenderLogicalExpression(outer_condition, "and", inner_condition);
        if (combined_condition.empty()) {
            continue;
        }

        const std::string indent(GetRenderedLuaIndent((*lines)[line_index]));
        const std::string inner_indent = indent + "  ";
        const std::string after_inner = trim_line((*lines)[line_index + 6]);
        if (after_inner == "else") {
            std::vector<std::string> replacement = {
                indent + "if " + combined_condition + " then",
                inner_indent + then_return,
                indent + "else",
            };
            lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                         lines->begin() + static_cast<std::ptrdiff_t>(line_index + 7));
            lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                          replacement.begin(),
                          replacement.end());
        } else if (after_inner == "end") {
            std::vector<std::string> replacement = {
                indent + "if " + combined_condition + " then",
                inner_indent + then_return,
                indent + "end",
            };
            lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                         lines->begin() + static_cast<std::ptrdiff_t>(line_index + 7));
            lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                          replacement.begin(),
                          replacement.end());
        } else {
            continue;
        }

        changed = true;
        if (line_index != 0) {
            --line_index;
        }
    }

    return changed;
}

bool SimplifyRenderedLuaPostIfFallbackElse(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto range_guarantees_assignment =
        [&](auto&& self,
            const std::vector<std::string>& local_lines,
            std::size_t begin,
            std::size_t end,
            std::string_view lhs) -> bool {
        std::size_t cursor = begin;
        while (cursor < end) {
            const std::string_view trimmed =
                TrimLuaWhitespace(StripLuaLineComment(local_lines[cursor]));
            if (trimmed.empty()) {
                ++cursor;
                continue;
            }

            bool assign_is_local = false;
            std::string assign_lhs;
            std::string assign_rhs;
            if (ParseRenderedSingleAssignmentLine(local_lines[cursor],
                                                 &assign_is_local,
                                                 &assign_lhs,
                                                 &assign_rhs) &&
                !assign_is_local && assign_lhs == lhs) {
                return FindRenderedLastNonEmptyLine(local_lines, cursor + 1, end) ==
                       local_lines.size();
            }

            if (trimmed.starts_with("if ") && trimmed.ends_with(" then")) {
                const std::size_t block_end = FindRenderedCleanupBlockEnd(local_lines, cursor);
                if (block_end >= end) {
                    return false;
                }
                if (FindRenderedLastNonEmptyLine(local_lines, block_end + 1, end) !=
                    local_lines.size()) {
                    return false;
                }

                std::size_t else_line = local_lines.size();
                int depth = 1;
                for (std::size_t probe = cursor + 1; probe < block_end; ++probe) {
                    const std::string_view probe_trimmed =
                        TrimLuaWhitespace(StripLuaLineComment(local_lines[probe]));
                    if (probe_trimmed.empty()) {
                        continue;
                    }
                    if (depth == 1 && probe_trimmed == "else") {
                        else_line = probe;
                        break;
                    }
                    depth += GetRenderedLuaBlockDelta(probe_trimmed);
                }
                if (else_line >= block_end) {
                    return false;
                }

                return self(self, local_lines, cursor + 1, else_line, lhs) &&
                       self(self, local_lines, else_line + 1, block_end, lhs);
            }

            if (trimmed == "else" || trimmed == "end" ||
                LooksLikeRenderedControlBlockHeader(trimmed)) {
                return false;
            }

            ++cursor;
        }

        return false;
    };

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 5 < lines->size(); ++line_index) {
        const std::string_view outer_if =
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index]));
        if (!outer_if.starts_with("if ") || !outer_if.ends_with(" then")) {
            continue;
        }

        const std::size_t outer_end = FindRenderedCleanupBlockEnd(*lines, line_index);
        if (outer_end >= lines->size() || outer_end <= line_index + 2 ||
            RenderedLuaBlockHasTopLevelElse(*lines, line_index, outer_end)) {
            continue;
        }

        std::size_t fallback_start = lines->size();
        for (std::size_t probe = outer_end + 1; probe < lines->size(); ++probe) {
            const std::string_view trimmed =
                TrimLuaWhitespace(StripLuaLineComment((*lines)[probe]));
            if (trimmed.empty()) {
                continue;
            }
            fallback_start = probe;
            break;
        }
        if (fallback_start >= lines->size()) {
            continue;
        }

        const std::string_view fallback_if =
            TrimLuaWhitespace(StripLuaLineComment((*lines)[fallback_start]));
        if (!fallback_if.starts_with("if ") || !fallback_if.ends_with(" then")) {
            continue;
        }

        const std::size_t fallback_end = FindRenderedCleanupBlockEnd(*lines, fallback_start);
        if (fallback_end >= lines->size() ||
            RenderedLuaBlockHasTopLevelElse(*lines, fallback_start, fallback_end)) {
            continue;
        }

        const std::size_t fallback_assignment =
            FindRenderedLastNonEmptyLine(*lines, fallback_start + 1, fallback_end);
        if (fallback_assignment >= fallback_end) {
            continue;
        }

        bool fallback_is_local = false;
        std::string fallback_lhs;
        std::string fallback_rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[fallback_assignment],
                                               &fallback_is_local,
                                               &fallback_lhs,
                                               &fallback_rhs) ||
            fallback_is_local) {
            continue;
        }

        std::size_t return_line = lines->size();
        for (std::size_t probe = fallback_end + 1; probe < lines->size(); ++probe) {
            const std::string_view trimmed =
                TrimLuaWhitespace(StripLuaLineComment((*lines)[probe]));
            if (trimmed.empty()) {
                continue;
            }
            return_line = probe;
            break;
        }
        if (return_line >= lines->size()) {
            continue;
        }

        const std::string_view trimmed_return =
            TrimLuaWhitespace(StripLuaLineComment((*lines)[return_line]));
        if (!trimmed_return.starts_with("return ") ||
            TrimLuaWhitespace(trimmed_return.substr(7)) != fallback_lhs) {
            continue;
        }

        if (!range_guarantees_assignment(range_guarantees_assignment,
                                         *lines,
                                         line_index + 1,
                                         outer_end,
                                         fallback_lhs)) {
            continue;
        }

        const std::string outer_indent(GetRenderedLuaIndent((*lines)[line_index]));
        std::vector<std::string> replacement;
        replacement.push_back((*lines)[line_index]);
        replacement.insert(replacement.end(),
                           lines->begin() + static_cast<std::ptrdiff_t>(line_index + 1),
                           lines->begin() + static_cast<std::ptrdiff_t>(outer_end));
        replacement.push_back(outer_indent + "else");
        replacement.insert(replacement.end(),
                           lines->begin() + static_cast<std::ptrdiff_t>(fallback_start),
                           lines->begin() + static_cast<std::ptrdiff_t>(fallback_end + 1));
        replacement.push_back(outer_indent + "end");
        replacement.push_back(outer_indent + std::string(trimmed_return));

        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                     lines->begin() + static_cast<std::ptrdiff_t>(return_line + 1));
        lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                      replacement.begin(),
                      replacement.end());
        changed = true;
    }

    return changed;
}

bool SimplifyRenderedLuaSignedMagnitudeClampBranches(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    constexpr std::string_view kOuterMarker = " < math.abs(";
    constexpr std::string_view kInnerPrefix = "0.0 >= ";
    constexpr std::string_view kInnerMid = " or ";

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 3 < lines->size(); ++line_index) {
        const std::string_view outer_if =
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index]));
        const std::string_view inner_if =
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 1]));
        if (!outer_if.starts_with("if ") || !outer_if.ends_with(" then") ||
            !inner_if.starts_with("if ") || !inner_if.ends_with(" then") ||
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 3])) != "end") {
            continue;
        }
        const std::string outer_condition = std::string(
            TrimLuaWhitespace(outer_if.substr(3, outer_if.size() - 3 - 5)));
        const std::string inner_condition = std::string(
            TrimLuaWhitespace(inner_if.substr(3, inner_if.size() - 3 - 5)));

        const std::size_t outer_split = outer_condition.find(kOuterMarker);
        if (outer_split == std::string::npos || !outer_condition.ends_with(')')) {
            continue;
        }

        const std::string limit_expression = StripRenderedWholeParentheses(
            TrimLuaWhitespace(outer_condition.substr(0, outer_split)), nullptr);
        const std::string value_expression = StripRenderedWholeParentheses(
            TrimLuaWhitespace(outer_condition.substr(
                outer_split + kOuterMarker.size(),
                outer_condition.size() - outer_split - kOuterMarker.size() - 1)),
            nullptr);
        if (limit_expression.empty() || value_expression.empty()) {
            continue;
        }

        const std::string expected_inner =
            std::string(kInnerPrefix) + value_expression + std::string(kInnerMid) +
            limit_expression;
        if (StripRenderedWholeParentheses(TrimLuaWhitespace(inner_condition), nullptr) !=
            expected_inner) {
            continue;
        }

        bool assign_is_local = false;
        std::string assign_lhs;
        std::string assign_rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[line_index + 2],
                                               &assign_is_local,
                                               &assign_lhs,
                                               &assign_rhs) ||
            assign_is_local ||
            StripRenderedWholeParentheses(TrimLuaWhitespace(assign_lhs), nullptr) !=
                value_expression ||
            StripRenderedWholeParentheses(TrimLuaWhitespace(assign_rhs), nullptr) !=
                ("-" + limit_expression)) {
            continue;
        }

        const std::string inner_indent(GetRenderedLuaIndent((*lines)[line_index + 1]));
        const std::string body_indent(GetRenderedLuaIndent((*lines)[line_index + 2]));
        (*lines)[line_index + 1] = inner_indent + "if 0.0 < " + value_expression + " then";
        (*lines)[line_index + 2] = body_indent + value_expression + " = " + limit_expression;
        lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(line_index + 3),
                      inner_indent + "else");
        lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(line_index + 4),
                      body_indent + value_expression + " = -" + limit_expression);
        changed = true;
    }

    return changed;
}

bool SimplifyRenderedLuaTrailingReturnFallbackElse(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto range_guarantees_assignment =
        [&](auto&& self,
            const std::vector<std::string>& local_lines,
            std::size_t begin,
            std::size_t end,
            std::string_view lhs) -> bool {
        std::size_t cursor = begin;
        while (cursor < end) {
            const std::string_view trimmed =
                TrimLuaWhitespace(StripLuaLineComment(local_lines[cursor]));
            if (trimmed.empty()) {
                ++cursor;
                continue;
            }

            bool assign_is_local = false;
            std::string assign_lhs;
            std::string assign_rhs;
            if (ParseRenderedSingleAssignmentLine(local_lines[cursor],
                                                 &assign_is_local,
                                                 &assign_lhs,
                                                 &assign_rhs) &&
                !assign_is_local && assign_lhs == lhs) {
                return FindRenderedLastNonEmptyLine(local_lines, cursor + 1, end) ==
                       local_lines.size();
            }

            if (trimmed.starts_with("if ") && trimmed.ends_with(" then")) {
                const std::size_t block_end = FindRenderedCleanupBlockEnd(local_lines, cursor);
                if (block_end >= end) {
                    return false;
                }
                if (FindRenderedLastNonEmptyLine(local_lines, block_end + 1, end) !=
                    local_lines.size()) {
                    return false;
                }

                std::size_t else_line = local_lines.size();
                int depth = 1;
                for (std::size_t probe = cursor + 1; probe < block_end; ++probe) {
                    const std::string_view probe_trimmed =
                        TrimLuaWhitespace(StripLuaLineComment(local_lines[probe]));
                    if (probe_trimmed.empty()) {
                        continue;
                    }
                    if (depth == 1 && probe_trimmed == "else") {
                        else_line = probe;
                        break;
                    }
                    depth += GetRenderedLuaBlockDelta(probe_trimmed);
                }
                if (else_line >= block_end) {
                    return false;
                }

                return self(self, local_lines, cursor + 1, else_line, lhs) &&
                       self(self, local_lines, else_line + 1, block_end, lhs);
            }

            if (trimmed == "else" || trimmed == "end" ||
                LooksLikeRenderedControlBlockHeader(trimmed)) {
                return false;
            }

            ++cursor;
        }

        return false;
    };

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 5 < lines->size(); ++line_index) {
        const std::string_view trimmed_if =
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index]));
        if (!trimmed_if.starts_with("if ") || !trimmed_if.ends_with(" then")) {
            continue;
        }

        const std::size_t block_end = FindRenderedCleanupBlockEnd(*lines, line_index);
        if (block_end >= lines->size() || block_end <= line_index + 3 ||
            RenderedLuaBlockHasTopLevelElse(*lines, line_index, block_end)) {
            continue;
        }

        const std::size_t return_line =
            FindRenderedLastNonEmptyLine(*lines, line_index + 1, block_end);
        if (return_line >= block_end) {
            continue;
        }

        const std::string_view trimmed_return =
            TrimLuaWhitespace(StripLuaLineComment((*lines)[return_line]));
        if (!trimmed_return.starts_with("return ")) {
            continue;
        }

        const std::string return_lhs =
            std::string(TrimLuaWhitespace(trimmed_return.substr(7)));
        if (return_lhs.empty() || !IsRenderedSafePrefixExpression(return_lhs)) {
            continue;
        }

        const std::size_t fallback_end =
            FindRenderedLastNonEmptyLine(*lines, line_index + 1, return_line);
        if (fallback_end >= return_line ||
            TrimLuaWhitespace(StripLuaLineComment((*lines)[fallback_end])) != "end") {
            continue;
        }

        std::size_t fallback_start = lines->size();
        for (std::size_t probe = line_index + 1; probe < fallback_end; ++probe) {
            const std::string_view probe_trimmed =
                TrimLuaWhitespace(StripLuaLineComment((*lines)[probe]));
            if (probe_trimmed.empty()) {
                continue;
            }
            if (probe_trimmed.starts_with("if ") && probe_trimmed.ends_with(" then")) {
                const std::size_t probe_end = FindRenderedCleanupBlockEnd(*lines, probe);
                if (probe_end == fallback_end) {
                    fallback_start = probe;
                }
                break;
            }
            if (probe_trimmed == "else" || probe_trimmed == "end" ||
                LooksLikeRenderedControlBlockHeader(probe_trimmed)) {
                break;
            }
        }
        if (fallback_start >= fallback_end ||
            RenderedLuaBlockHasTopLevelElse(*lines, fallback_start, fallback_end)) {
            continue;
        }

        const std::size_t fallback_assignment =
            FindRenderedLastNonEmptyLine(*lines, fallback_start + 1, fallback_end);
        if (fallback_assignment >= fallback_end) {
            continue;
        }

        bool fallback_is_local = false;
        std::string fallback_lhs;
        std::string fallback_rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[fallback_assignment],
                                               &fallback_is_local,
                                               &fallback_lhs,
                                               &fallback_rhs) ||
            fallback_is_local || fallback_lhs != return_lhs) {
            continue;
        }

        if (!range_guarantees_assignment(range_guarantees_assignment,
                                         *lines,
                                         line_index + 1,
                                         fallback_start,
                                         return_lhs)) {
            continue;
        }

        const std::string outer_indent(GetRenderedLuaIndent((*lines)[line_index]));
        std::vector<std::string> replacement;
        replacement.reserve(block_end - line_index + 2);
        replacement.push_back((*lines)[line_index]);
        replacement.insert(replacement.end(),
                           lines->begin() + static_cast<std::ptrdiff_t>(line_index + 1),
                           lines->begin() + static_cast<std::ptrdiff_t>(fallback_start));
        replacement.push_back(outer_indent + "else");
        replacement.insert(replacement.end(),
                           lines->begin() + static_cast<std::ptrdiff_t>(fallback_start),
                           lines->begin() + static_cast<std::ptrdiff_t>(fallback_end + 1));
        replacement.push_back(outer_indent + "end");
        replacement.push_back(outer_indent + std::string(trimmed_return));

        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                     lines->begin() + static_cast<std::ptrdiff_t>(block_end + 1));
        lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                      replacement.begin(),
                      replacement.end());
        changed = true;
    }

    return changed;
}

bool SimplifyRenderedLuaLoopTrailingReturnElse(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimLuaWhitespace(StripLuaLineComment(line));
    };
    const auto next_nonempty_noncomment =
        [&](std::size_t start, std::size_t limit) -> std::size_t {
        for (std::size_t probe = start; probe < limit && probe < lines->size(); ++probe) {
            const std::string_view raw = TrimLuaWhitespace((*lines)[probe]);
            if (raw.empty() || raw.starts_with("--")) {
                continue;
            }
            return probe;
        }
        return limit;
    };
    const auto reindent_range = [&](std::size_t begin,
                                    std::size_t end,
                                    std::string_view target_indent,
                                    std::vector<std::string>* out_lines) -> bool {
        if (out_lines == nullptr || begin > end || end > lines->size()) {
            return false;
        }

        for (std::size_t probe = begin; probe < end; ++probe) {
            const std::string_view raw_trimmed = TrimLuaWhitespace((*lines)[probe]);
            const std::string_view stripped_trimmed = trim_line((*lines)[probe]);
            if (raw_trimmed.empty()) {
                out_lines->push_back(std::string(raw_trimmed));
                continue;
            }
            if (raw_trimmed.starts_with("--")) {
                out_lines->push_back(std::string(target_indent) +
                                     std::string(raw_trimmed));
                continue;
            }
            if (stripped_trimmed.empty()) {
                continue;
            }

            const std::string source_indent(GetRenderedLuaIndent((*lines)[probe]));
            if (!source_indent.empty() && (*lines)[probe].starts_with(source_indent)) {
                out_lines->push_back(std::string(target_indent) +
                                     (*lines)[probe].substr(source_indent.size()));
            } else {
                out_lines->push_back(std::string(target_indent) +
                                     std::string(stripped_trimmed));
            }
        }

        return true;
    };

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 4 < lines->size(); ++line_index) {
        const std::string_view while_header =
            trim_line((*lines)[line_index]);
        if (!while_header.starts_with("while ") || !while_header.ends_with(" do")) {
            continue;
        }

        const std::size_t while_end = FindRenderedCleanupBlockEnd(*lines, line_index);
        if (while_end >= lines->size() || trim_line((*lines)[while_end]) != "end" ||
            while_end <= line_index + 3) {
            continue;
        }

        const std::size_t nested_if_line =
            next_nonempty_noncomment(line_index + 1, while_end);
        if (nested_if_line >= while_end) {
            continue;
        }

        const std::string_view nested_if_trimmed =
            trim_line((*lines)[nested_if_line]);
        if (!nested_if_trimmed.starts_with("if ") ||
            !nested_if_trimmed.ends_with(" then")) {
            continue;
        }

        const std::size_t nested_if_end =
            FindRenderedCleanupBlockEnd(*lines, nested_if_line);
        if (nested_if_end >= while_end || nested_if_end <= nested_if_line + 1 ||
            RenderedLuaBlockHasTopLevelElse(*lines, nested_if_line, nested_if_end)) {
            continue;
        }

        const std::size_t fallback_return_line =
            next_nonempty_noncomment(nested_if_end + 1, while_end);
        if (fallback_return_line >= while_end ||
            next_nonempty_noncomment(fallback_return_line + 1, while_end) != while_end) {
            continue;
        }

        const std::string_view fallback_return_trimmed =
            trim_line((*lines)[fallback_return_line]);
        if (!StartsWithLuaKeyword(fallback_return_trimmed, "return")) {
            continue;
        }

        const std::size_t after_loop_return_line =
            next_nonempty_noncomment(while_end + 1, lines->size());
        if (after_loop_return_line >= lines->size() ||
            trim_line((*lines)[after_loop_return_line]) != fallback_return_trimmed) {
            continue;
        }

        const std::string if_indent(GetRenderedLuaIndent((*lines)[nested_if_line]));
        std::vector<std::string> replacement;
        replacement.push_back(if_indent + "else");
        if (!reindent_range(nested_if_end + 1,
                            fallback_return_line + 1,
                            if_indent + "  ",
                            &replacement)) {
            continue;
        }
        replacement.push_back(if_indent + "end");

        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(nested_if_end),
                     lines->begin() +
                         static_cast<std::ptrdiff_t>(fallback_return_line + 1));
        lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(nested_if_end),
                      replacement.begin(),
                      replacement.end());
        changed = true;
        if (line_index != 0) {
            --line_index;
        }
    }

    return changed;
}

bool SimplifyRenderedLuaMissingElseDuplicateTails(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto find_block_end = [](const std::vector<std::string>& local_lines,
                                   std::size_t start_line) -> std::size_t {
        if (start_line >= local_lines.size()) {
            return local_lines.size();
        }

        int balance = 0;
        for (std::size_t line_index = start_line; line_index < local_lines.size(); ++line_index) {
            balance += GetRenderedLuaBlockDelta(
                TrimLuaWhitespace(StripLuaLineComment(local_lines[line_index])));
            if (line_index > start_line && balance == 0) {
                return line_index;
            }
        }
        return local_lines.size();
    };

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 5 < lines->size(); ++line_index) {
        const std::string_view outer_if =
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index]));
        const std::string_view nested_if =
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 1]));
        if (!outer_if.starts_with("if ") || !outer_if.ends_with(" then") ||
            !nested_if.starts_with("if ") || !nested_if.ends_with(" then")) {
            continue;
        }

        const std::size_t nested_end = find_block_end(*lines, line_index + 1);
        const std::size_t outer_end = find_block_end(*lines, line_index);
        if (nested_end >= lines->size() || outer_end >= lines->size() ||
            outer_end != nested_end + 1) {
            continue;
        }

        bool contains_else = false;
        for (std::size_t probe = line_index + 2; probe < outer_end; ++probe) {
            const std::string_view trimmed =
                TrimLuaWhitespace(StripLuaLineComment((*lines)[probe]));
            if (trimmed == "else" || trimmed.starts_with("elseif ")) {
                contains_else = true;
                break;
            }
        }
        if (contains_else) {
            continue;
        }

        const std::size_t body_start = line_index + 2;
        const std::size_t body_end = nested_end;
        const std::size_t body_size = body_end - body_start;
        if (body_size == 0 || body_size > 24 || outer_end + body_size >= lines->size()) {
            continue;
        }

        bool body_is_simple = true;
        for (std::size_t probe = body_start; probe < body_end; ++probe) {
            const std::string_view trimmed =
                TrimLuaWhitespace(StripLuaLineComment((*lines)[probe]));
            if (trimmed == "else" || trimmed == "end" ||
                LooksLikeRenderedControlBlockHeader(trimmed)) {
                body_is_simple = false;
                break;
            }
        }
        if (!body_is_simple) {
            continue;
        }

        const std::size_t duplicate_start = outer_end + 1;
        bool duplicate_matches = true;
        for (std::size_t offset = 0; offset < body_size; ++offset) {
            const std::string_view source_trimmed = TrimLuaWhitespace(
                StripLuaLineComment((*lines)[body_start + offset]));
            const std::string_view duplicate_trimmed = TrimLuaWhitespace(
                StripLuaLineComment((*lines)[duplicate_start + offset]));
            if (source_trimmed != duplicate_trimmed) {
                duplicate_matches = false;
                break;
            }
        }
        if (!duplicate_matches) {
            continue;
        }

        const std::string outer_indent(GetRenderedLuaIndent((*lines)[line_index]));
        (*lines)[outer_end] = outer_indent + "else";
        lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(duplicate_start + body_size),
                      outer_indent + "end");
        changed = true;
        line_index = duplicate_start + body_size;
    }

    return changed;
}

bool SimplifyRenderedLuaNestedMissingElseDuplicateTails(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto find_block_end = [](const std::vector<std::string>& local_lines,
                                   std::size_t start_line) -> std::size_t {
        if (start_line >= local_lines.size()) {
            return local_lines.size();
        }

        int balance = 0;
        for (std::size_t line_index = start_line; line_index < local_lines.size(); ++line_index) {
            balance += GetRenderedLuaBlockDelta(
                TrimLuaWhitespace(StripLuaLineComment(local_lines[line_index])));
            if (line_index > start_line && balance == 0) {
                return line_index;
            }
        }
        return local_lines.size();
    };

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 4 < lines->size(); ++line_index) {
        const std::string_view outer_if =
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index]));
        const std::string_view nested_if =
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 1]));
        if (!outer_if.starts_with("if ") || !outer_if.ends_with(" then") ||
            !nested_if.starts_with("if ") || !nested_if.ends_with(" then")) {
            continue;
        }

        const std::size_t nested_end = find_block_end(*lines, line_index + 1);
        const std::size_t outer_end = find_block_end(*lines, line_index);
        if (nested_end >= lines->size() || outer_end >= lines->size() ||
            outer_end <= nested_end + 1) {
            continue;
        }

        bool nested_has_top_level_else = false;
        int nested_depth = 1;
        for (std::size_t probe = line_index + 2; probe < nested_end; ++probe) {
            const std::string_view trimmed =
                TrimLuaWhitespace(StripLuaLineComment((*lines)[probe]));
            if (trimmed.empty()) {
                continue;
            }
            if (nested_depth == 1 &&
                (trimmed == "else" || trimmed.starts_with("elseif "))) {
                nested_has_top_level_else = true;
                break;
            }
            nested_depth += GetRenderedLuaBlockDelta(trimmed);
        }
        if (nested_has_top_level_else) {
            continue;
        }

        const std::size_t outer_tail_start = nested_end + 1;
        const std::size_t outer_tail_size = outer_end - outer_tail_start;
        const std::size_t inner_body_start = line_index + 2;
        const std::size_t inner_body_end = nested_end;
        const std::size_t inner_body_size = inner_body_end - inner_body_start;
        if (outer_tail_size == 0 || outer_tail_size > 8 ||
            inner_body_size <= outer_tail_size) {
            continue;
        }

        const std::size_t inner_tail_start = inner_body_end - outer_tail_size;
        if (inner_tail_start <= inner_body_start) {
            continue;
        }

        bool tail_is_simple = true;
        bool tails_match = true;
        for (std::size_t offset = 0; offset < outer_tail_size; ++offset) {
            const std::string_view inner_trimmed = TrimLuaWhitespace(
                StripLuaLineComment((*lines)[inner_tail_start + offset]));
            const std::string_view outer_trimmed = TrimLuaWhitespace(
                StripLuaLineComment((*lines)[outer_tail_start + offset]));
            if (inner_trimmed != outer_trimmed) {
                tails_match = false;
                break;
            }
            if (inner_trimmed.empty() || inner_trimmed == "else" || inner_trimmed == "end" ||
                LooksLikeRenderedControlBlockHeader(inner_trimmed)) {
                tail_is_simple = false;
                break;
            }
        }
        if (!tails_match || !tail_is_simple) {
            continue;
        }

        const std::string nested_indent(GetRenderedLuaIndent((*lines)[line_index + 1]));
        std::vector<std::string> outer_tail_lines(
            lines->begin() + static_cast<std::ptrdiff_t>(outer_tail_start),
            lines->begin() + static_cast<std::ptrdiff_t>(outer_end));

        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(outer_tail_start),
                     lines->begin() + static_cast<std::ptrdiff_t>(outer_end));
        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(inner_tail_start),
                     lines->begin() + static_cast<std::ptrdiff_t>(inner_body_end));

        const std::size_t updated_nested_end = nested_end - outer_tail_size;
        (*lines)[updated_nested_end] = nested_indent + "else";
        lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(updated_nested_end + 1),
                      outer_tail_lines.begin(),
                      outer_tail_lines.end());
        changed = true;
    }

    return changed;
}

}  // namespace refii::lua
