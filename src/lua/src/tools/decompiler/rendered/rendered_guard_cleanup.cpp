#include "tools/decompiler/rendered/rendered_lua_text.h"

#include "tools/decompiler/rendered/rendered_cleanup_common_helpers.h"
#include "tools/decompiler/straight_line_render.h"

#include <algorithm>
#include <cstddef>
#include <string>
#include <string_view>
#include <vector>

namespace refii::lua {

bool SimplifyRenderedLuaGuardedSelectionLocals(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto parse_local_declaration = [](std::string_view line,
                                            std::string* name) -> bool {
        if (name == nullptr) {
            return false;
        }
        const std::string_view trimmed = TrimLuaWhitespace(StripLuaLineComment(line));
        if (!trimmed.starts_with("local ")) {
            return false;
        }
        const std::string_view remainder = TrimLuaWhitespace(trimmed.substr(6));
        if (remainder.empty() || remainder.find('=') != std::string_view::npos ||
            remainder.find(',') != std::string_view::npos ||
            !IsValidLuaIdentifier(remainder)) {
            return false;
        }
        *name = std::string(remainder);
        return true;
    };

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 3 < lines->size(); ++line_index) {
        std::string local_name;
        if (!parse_local_declaration((*lines)[line_index], &local_name)) {
            continue;
        }

        const std::string_view trimmed_if =
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 1]));
        if (!trimmed_if.starts_with("if ") || !trimmed_if.ends_with(" then")) {
            continue;
        }

        const std::string condition = StripRenderedWholeParentheses(
            TrimLuaWhitespace(trimmed_if.substr(3, trimmed_if.size() - 3 - 5)),
            nullptr);
        constexpr std::string_view kOrNot = " or not ";
        if (!condition.starts_with("not ") ||
            condition.find(kOrNot) == std::string::npos) {
            continue;
        }

        const std::size_t split = condition.find(kOrNot);
        const std::string present_expr =
            StripRenderedWholeParentheses(TrimLuaWhitespace(condition.substr(4, split - 4)),
                                          nullptr);
        const std::string selected_expr =
            StripRenderedWholeParentheses(
                TrimLuaWhitespace(condition.substr(split + kOrNot.size())),
                nullptr);
        if (present_expr.empty() || selected_expr.empty()) {
            continue;
        }

        bool assign_is_local = false;
        std::string assign_lhs;
        std::string assign_rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[line_index + 2],
                                               &assign_is_local,
                                               &assign_lhs,
                                               &assign_rhs) ||
            assign_is_local || assign_lhs != local_name ||
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 3])) != "end") {
            continue;
        }

        const std::string indent(GetRenderedLuaIndent((*lines)[line_index]));
        (*lines)[line_index] = indent + "local " + local_name + " = ((" + present_expr +
                               " and " + selected_expr + ") or " + assign_rhs + ")";
        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index + 1),
                     lines->begin() + static_cast<std::ptrdiff_t>(line_index + 4));
        changed = true;
    }

    return changed;
}

bool SimplifyRenderedLuaNestedGuardedSelectionLocals(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 4 < lines->size(); ++line_index) {
        const std::string_view trimmed_outer =
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index]));
        if (!trimmed_outer.starts_with("if ") || !trimmed_outer.ends_with(" then")) {
            continue;
        }
        std::string outer_condition = std::string(
            TrimLuaWhitespace(trimmed_outer.substr(3, trimmed_outer.size() - 3 - 5)));
        outer_condition = NormalizeRenderedConditionExpression(outer_condition, nullptr);
        if (outer_condition.empty()) {
            continue;
        }

        std::vector<std::string> outer_parts;
        if (!SplitRenderedTopLevelBooleanChain(outer_condition, "or", &outer_parts) ||
            outer_parts.size() != 2) {
            continue;
        }

        const std::string first_expr =
            NormalizeRenderedConditionOperand(TrimLuaWhitespace(outer_parts[0]), nullptr);
        const std::string second_expr =
            NormalizeRenderedConditionOperand(TrimLuaWhitespace(outer_parts[1]), nullptr);
        if (first_expr.empty() || second_expr.empty() ||
            !IsRenderedSafePrefixExpression(first_expr) ||
            !IsRenderedSafePrefixExpression(second_expr)) {
            continue;
        }

        int block_depth = 0;
        std::size_t outer_end = lines->size();
        for (std::size_t probe = line_index; probe < lines->size(); ++probe) {
            block_depth += GetRenderedLuaBlockDelta((*lines)[probe]);
            if (probe > line_index && block_depth == 0) {
                outer_end = probe;
                break;
            }
        }
        if (outer_end == lines->size() || outer_end <= line_index + 3) {
            continue;
        }

        for (std::size_t nested_if = line_index + 1; nested_if + 2 < outer_end; ++nested_if) {
            const std::string_view trimmed_if =
                TrimLuaWhitespace(StripLuaLineComment((*lines)[nested_if]));
            const std::string expected_first = "if not " + first_expr + " then";
            const std::string expected_second = "if not " + second_expr + " then";
            bool use_first = false;
            if (trimmed_if == expected_first) {
                use_first = true;
            } else if (trimmed_if != expected_second) {
                continue;
            }

            bool assign_is_local = false;
            std::string assign_lhs;
            std::string assign_rhs;
            if (!ParseRenderedSingleAssignmentLine((*lines)[nested_if + 1],
                                                   &assign_is_local,
                                                   &assign_lhs,
                                                   &assign_rhs) ||
                !assign_is_local ||
                TrimLuaWhitespace(StripLuaLineComment((*lines)[nested_if + 2])) != "end") {
                continue;
            }

            const std::string expected_rhs = use_first ? second_expr : first_expr;
            const std::string guarded_expr = use_first ? first_expr : second_expr;
            const std::string fallback_expr = use_first ? second_expr : first_expr;
            if (NormalizeRenderedConditionOperand(TrimLuaWhitespace(assign_rhs), nullptr) !=
                expected_rhs) {
                continue;
            }

            bool used_after = false;
            for (std::size_t probe = nested_if + 3; probe < outer_end; ++probe) {
                const std::string_view probe_trimmed =
                    TrimLuaWhitespace(StripLuaLineComment((*lines)[probe]));
                if (probe_trimmed.empty()) {
                    continue;
                }
                if (FindIdentifierUse(probe_trimmed, assign_lhs) != std::string_view::npos) {
                    used_after = true;
                    break;
                }
            }
            if (!used_after) {
                continue;
            }

            const std::string indent(GetRenderedLuaIndent((*lines)[nested_if]));
            (*lines)[nested_if] =
                indent + "local " + assign_lhs + " = " +
                RenderLogicalExpression(guarded_expr, "or", fallback_expr);
            lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(nested_if + 1),
                         lines->begin() + static_cast<std::ptrdiff_t>(nested_if + 3));
            changed = true;
            break;
        }
    }

    return changed;
}

bool SimplifyRenderedLuaGuardedMemberSelectionFallbackAssignments(
    std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 3 < lines->size(); ++line_index) {
        bool seed_is_local = false;
        std::string seed_lhs;
        std::string seed_rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[line_index],
                                               &seed_is_local,
                                               &seed_lhs,
                                               &seed_rhs) ||
            seed_lhs.empty()) {
            continue;
        }

        const std::string_view trimmed_if =
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 1]));
        if (trimmed_if != ("if not " + seed_lhs + " then") ||
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 3])) != "end") {
            continue;
        }

        bool fallback_is_local = false;
        std::string fallback_lhs;
        std::string fallback_rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[line_index + 2],
                                               &fallback_is_local,
                                               &fallback_lhs,
                                               &fallback_rhs) ||
            fallback_is_local || fallback_lhs != seed_lhs) {
            continue;
        }

        std::vector<std::string> seed_parts;
        if (!SplitRenderedTopLevelBooleanChain(seed_rhs, "or", &seed_parts) ||
            seed_parts.size() != 2) {
            continue;
        }

        const std::string present_expr = NormalizeRenderedConditionOperand(
            TrimLuaWhitespace(seed_parts[0]),
            nullptr);
        const std::string_view second_part = TrimLuaWhitespace(seed_parts[1]);
        if (present_expr.empty() || !IsRenderedSafePrefixExpression(present_expr) ||
            !StartsWithLuaKeyword(second_part, "not")) {
            continue;
        }

        const std::string selected_expr = NormalizeRenderedConditionOperand(
            TrimLuaWhitespace(second_part.substr(3)),
            nullptr);
        if (selected_expr.empty() || !IsRenderedSafePrefixExpression(selected_expr)) {
            continue;
        }

        const std::string indent(GetRenderedLuaIndent((*lines)[line_index]));
        (*lines)[line_index] =
            indent + (seed_is_local ? "local " : "") + seed_lhs + " = " +
            RenderLogicalExpression(
                RenderLogicalExpression(present_expr, "and", selected_expr),
                "or",
                std::string(TrimLuaWhitespace(fallback_rhs)));
        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index + 1),
                     lines->begin() + static_cast<std::ptrdiff_t>(line_index + 4));
        changed = true;
    }

    return changed;
}

bool SimplifyRenderedLuaGuardedTruthySelectionLocals(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 3 < lines->size(); ++line_index) {
        bool assign_is_local = false;
        std::string assign_lhs;
        std::string assign_rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[line_index],
                                               &assign_is_local,
                                               &assign_lhs,
                                               &assign_rhs) ||
            assign_lhs.empty()) {
            continue;
        }

        const std::string_view trimmed_if =
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 1]));
        if (trimmed_if != ("if not " + assign_lhs + " then") ||
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 3])) != "end") {
            continue;
        }

        bool fallback_is_local = false;
        std::string fallback_lhs;
        std::string fallback_rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[line_index + 2],
                                               &fallback_is_local,
                                               &fallback_lhs,
                                               &fallback_rhs) ||
            fallback_is_local || fallback_lhs != assign_lhs) {
            continue;
        }

        std::vector<std::string> parts;
        if (!SplitRenderedTopLevelBooleanChain(assign_rhs, "or", &parts) || parts.size() < 3) {
            continue;
        }
        if (StartsWithLuaKeyword(TrimLuaWhitespace(parts.front()), "not")) {
            continue;
        }

        std::string rebuilt =
            NormalizeRenderedConditionExpression(TrimLuaWhitespace(parts.front()), nullptr);
        if (rebuilt.empty()) {
            continue;
        }

        bool valid = true;
        for (std::size_t part_index = 1; part_index < parts.size(); ++part_index) {
            const std::string_view part = TrimLuaWhitespace(parts[part_index]);
            if (!StartsWithLuaKeyword(part, "not")) {
                valid = false;
                break;
            }

            const std::string positive = NormalizeRenderedConditionExpression(
                TrimLuaWhitespace(part.substr(3)),
                nullptr);
            const std::string_view positive_view = TrimLuaWhitespace(positive);
            const bool definitely_truthy_literal =
                positive_view == "true" ||
                LooksLikeRenderedNumericLiteral(positive_view) ||
                positive_view.starts_with('"') ||
                positive_view.starts_with('\'') ||
                positive_view.starts_with('{') ||
                positive_view.starts_with("function(");
            if (positive.empty() || definitely_truthy_literal) {
                valid = false;
                break;
            }
            rebuilt = RenderLogicalExpression(rebuilt, "and", positive);
        }
        if (!valid) {
            continue;
        }

        const std::string indent(GetRenderedLuaIndent((*lines)[line_index]));
        (*lines)[line_index] =
            indent + (assign_is_local ? "local " : "") + assign_lhs + " = " + rebuilt;
        changed = true;
    }

    return changed;
}

}  // namespace refii::lua
