#include "tools/decompiler/rendered/rendered_lua_text.h"

#include "tools/decompiler/rendered/rendered_cleanup_common_helpers.h"

#include <algorithm>
#include <cstddef>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

namespace refii::lua {

bool TryParseRenderedConditionHeaderLine(std::string_view line,
                                         std::string* condition,
                                         std::string* condition_prefix,
                                         std::string* condition_suffix) {
    if (condition == nullptr || condition_prefix == nullptr || condition_suffix == nullptr) {
        return false;
    }
    const std::string_view trimmed = TrimLuaWhitespace(StripLuaLineComment(line));
    if (StartsWithLuaKeyword(trimmed, "if") && trimmed.ends_with(" then")) {
        *condition = std::string(
            TrimLuaWhitespace(trimmed.substr(3, trimmed.size() - 3 - 5)));
        *condition_prefix = "if ";
        *condition_suffix = " then";
        return !condition->empty();
    }
    if (StartsWithLuaKeyword(trimmed, "elseif") && trimmed.ends_with(" then")) {
        *condition = std::string(
            TrimLuaWhitespace(trimmed.substr(7, trimmed.size() - 7 - 5)));
        *condition_prefix = "elseif ";
        *condition_suffix = " then";
        return !condition->empty();
    }
    if (StartsWithLuaKeyword(trimmed, "while") && trimmed.ends_with(" do")) {
        *condition = std::string(
            TrimLuaWhitespace(trimmed.substr(6, trimmed.size() - 6 - 3)));
        *condition_prefix = "while ";
        *condition_suffix = " do";
        return !condition->empty();
    }
    return false;
}

bool NormalizeRenderedLuaLoopBreakElseToPostIfForParity(std::vector<std::string>* lines) {
    if (lines == nullptr || lines->size() < 8) {
        return false;
    }

    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimLuaWhitespace(StripLuaLineComment(line));
    };

    const auto strip_leading_not = [](std::string_view condition,
                                      std::string* positive_out) -> bool {
        if (positive_out == nullptr) {
            return false;
        }
        condition = TrimLuaWhitespace(condition);
        if (!StartsWithLuaKeyword(condition, "not")) {
            return false;
        }
        condition = TrimLuaWhitespace(condition.substr(3));
        const std::string stripped_condition =
            StripRenderedWholeParentheses(condition, nullptr);
        if (stripped_condition.empty()) {
            return false;
        }
        *positive_out = stripped_condition;
        return true;
    };

    const auto find_top_level_else = [&](std::size_t if_line,
                                         std::size_t if_end,
                                         std::size_t* else_out) -> bool {
        if (else_out == nullptr || if_line >= if_end || if_end >= lines->size()) {
            return false;
        }
        int depth = 1;
        for (std::size_t probe = if_line + 1; probe < if_end; ++probe) {
            const std::string_view trimmed = trim_line((*lines)[probe]);
            if (trimmed.empty()) {
                continue;
            }
            if (depth == 1) {
                if (trimmed == "else") {
                    *else_out = probe;
                    return true;
                }
                if (StartsWithLuaKeyword(trimmed, "elseif")) {
                    return false;
                }
            }
            depth += GetRenderedLuaBlockDelta(trimmed);
            if (depth <= 0) {
                return false;
            }
        }
        return false;
    };

    const auto previous_nonempty = [&](std::size_t begin,
                                       std::size_t end,
                                       std::size_t* line_out) -> bool {
        if (line_out == nullptr || begin >= end || end > lines->size()) {
            return false;
        }
        for (std::size_t probe = end; probe > begin; --probe) {
            const std::size_t index = probe - 1;
            if (!trim_line((*lines)[index]).empty()) {
                *line_out = index;
                return true;
            }
        }
        return false;
    };

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 7 < lines->size(); ++line_index) {
        std::string outer_condition;
        if (!TryExtractRenderedIfCondition((*lines)[line_index], &outer_condition)) {
            continue;
        }

        const std::size_t inner_if_line = line_index + 1;
        std::string inner_condition;
        if (!TryExtractRenderedIfCondition((*lines)[inner_if_line], &inner_condition)) {
            continue;
        }

        std::string positive_inner_condition;
        if (!strip_leading_not(inner_condition, &positive_inner_condition)) {
            continue;
        }
        if (trim_line((*lines)[line_index + 2]) != "break" ||
            trim_line((*lines)[line_index + 3]) != "end") {
            continue;
        }

        const std::size_t outer_end = FindRenderedCleanupBlockEnd(*lines, line_index);
        if (outer_end >= lines->size() || trim_line((*lines)[outer_end]) != "end") {
            continue;
        }

        std::size_t outer_else = lines->size();
        if (!find_top_level_else(line_index, outer_end, &outer_else) ||
            outer_else <= line_index + 4 || outer_else + 1 >= outer_end) {
            continue;
        }

        std::size_t then_last = lines->size();
        if (!previous_nonempty(line_index + 4, outer_else, &then_last)) {
            continue;
        }
        const std::string_view then_last_trimmed = trim_line((*lines)[then_last]);
        if (!StartsWithLuaKeyword(then_last_trimmed, "return")) {
            continue;
        }

        const std::string outer_indent(GetRenderedLuaIndent((*lines)[line_index]));
        const std::string inner_indent(GetRenderedLuaIndent((*lines)[inner_if_line]));
        std::vector<std::string> replacement;
        replacement.reserve(outer_end - line_index + 2);
        replacement.push_back((*lines)[line_index]);
        replacement.push_back(inner_indent + "if " + positive_inner_condition + " then");
        replacement.insert(replacement.end(),
                           lines->begin() + static_cast<std::ptrdiff_t>(line_index + 4),
                           lines->begin() + static_cast<std::ptrdiff_t>(outer_else));
        replacement.push_back(inner_indent + "end");
        replacement.push_back(outer_indent + "break");
        replacement.push_back(outer_indent + "end");
        replacement.insert(replacement.end(),
                           lines->begin() + static_cast<std::ptrdiff_t>(outer_else + 1),
                           lines->begin() + static_cast<std::ptrdiff_t>(outer_end));

        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                     lines->begin() + static_cast<std::ptrdiff_t>(outer_end + 1));
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

bool NormalizeRenderedLuaPostGuardDebugErrorElseForParity(std::vector<std::string>* lines) {
    if (lines == nullptr || lines->size() < 4) {
        return false;
    }

    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimLuaWhitespace(StripLuaLineComment(line));
    };

    const auto has_top_level_else = [&](std::size_t if_line,
                                        std::size_t if_end) -> bool {
        int depth = 1;
        for (std::size_t probe = if_line + 1; probe < if_end; ++probe) {
            const std::string_view trimmed = trim_line((*lines)[probe]);
            if (trimmed.empty()) {
                continue;
            }
            if (depth == 1 &&
                (trimmed == "else" || StartsWithLuaKeyword(trimmed, "elseif"))) {
                return true;
            }
            depth += GetRenderedLuaBlockDelta(trimmed);
            if (depth <= 0) {
                return true;
            }
        }
        return false;
    };

    for (std::size_t if_line = 0; if_line + 2 < lines->size(); ++if_line) {
        std::string condition;
        if (!TryExtractRenderedIfCondition((*lines)[if_line], &condition)) {
            continue;
        }

        const std::size_t if_end = FindRenderedCleanupBlockEnd(*lines, if_line);
        if (if_end == std::string::npos || if_end + 1 >= lines->size()) {
            continue;
        }
        if (trim_line((*lines)[if_end]) != "end" || has_top_level_else(if_line, if_end)) {
            continue;
        }

        const std::size_t error_line = if_end + 1;
        const std::string_view error_trimmed = trim_line((*lines)[error_line]);
        if (!error_trimmed.starts_with("Debug.Error(") || !error_trimmed.ends_with(")")) {
            continue;
        }

        // Avoid changing statement chains where the diagnostic is already separated from the guard.
        if (!TrimLuaWhitespace((*lines)[if_end - 1]).empty() &&
            StartsWithLuaKeyword(trim_line((*lines)[if_end - 1]), "return")) {
            continue;
        }

        const std::string indent(GetRenderedLuaIndent((*lines)[if_line]));
        (*lines)[if_end] = indent + "else";
        lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(error_line + 1),
                      indent + "end");
        return true;
    }

    return false;
}

bool SimplifyRenderedLuaBooleanCarryConditions(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        std::string condition;
        if (!TryExtractRenderedIfCondition((*lines)[line_index], &condition)) {
            continue;
        }

        std::vector<std::string> or_parts;
        if (!SplitRenderedTopLevelBooleanChain(condition, "or", &or_parts) ||
            or_parts.size() != 2) {
            continue;
        }

        std::vector<std::string> and_parts;
        if (!SplitRenderedTopLevelBooleanChain(or_parts[0], "and", &and_parts) ||
            and_parts.size() != 2 ||
            !IsRenderedDefinitelyTruthyExpression(and_parts[1])) {
            continue;
        }

        const std::string fallback =
            StripRenderedWholeParentheses(TrimLuaWhitespace(or_parts[1]), nullptr);
        if (fallback.empty() || !IsRenderedSafePrefixExpression(fallback)) {
            continue;
        }

        bool initialized_to_false = false;
        const std::size_t probe_begin = line_index > 8 ? line_index - 8 : 0;
        for (std::size_t probe = probe_begin; probe < line_index; ++probe) {
            bool assign_is_local = false;
            std::string assign_lhs;
            std::string assign_rhs;
            if (!ParseRenderedSingleAssignmentLine((*lines)[probe],
                                                   &assign_is_local,
                                                   &assign_lhs,
                                                   &assign_rhs) ||
                assign_lhs != fallback) {
                continue;
            }

            const std::string normalized_rhs =
                StripRenderedWholeParentheses(TrimLuaWhitespace(assign_rhs), nullptr);
            if (normalized_rhs == "false" || normalized_rhs == "nil") {
                initialized_to_false = true;
                break;
            }
        }
        if (!initialized_to_false) {
            continue;
        }

        const std::string guard =
            NormalizeRenderedConditionOperand(TrimLuaWhitespace(and_parts[0]), nullptr);
        if (guard.empty()) {
            continue;
        }

        const std::string outer_indent(GetRenderedLuaIndent((*lines)[line_index]));
        const std::string inner_indent = outer_indent + "  ";
        lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                      outer_indent + "if " + guard + " then");
        lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(line_index + 1),
                      inner_indent + fallback + " = true");
        lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(line_index + 2),
                      outer_indent + "end");
        (*lines)[line_index + 3] = outer_indent + "if " + fallback + " then";
        changed = true;
        line_index += 3;
    }

    return changed;
}

bool SimplifyRenderedLuaSeededBooleanTrueConditions(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    std::vector<std::vector<std::size_t>> scope_paths;
    ComputeRenderedScopePaths(*lines, &scope_paths);
    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        std::string condition;
        if (!TryExtractRenderedIfCondition((*lines)[line_index], &condition)) {
            continue;
        }

        const std::string identifier =
            StripRenderedWholeParentheses(TrimLuaWhitespace(condition), nullptr);
        if (!IsValidLuaIdentifier(identifier)) {
            continue;
        }

        const std::vector<std::size_t>& target_scope = scope_paths[line_index];
        bool saw_seed = false;
        bool saw_true_assignment = false;
        bool saw_non_boolean_assignment = false;
        const std::size_t probe_begin = line_index > 24 ? line_index - 24 : 0;
        for (std::size_t probe = probe_begin; probe < line_index; ++probe) {
            if (scope_paths[probe] != target_scope) {
                continue;
            }

            if (IsRenderedShadowingLocalDeclaration((*lines)[probe], identifier)) {
                saw_non_boolean_assignment = true;
                break;
            }

            bool assign_is_local = false;
            std::string assign_lhs;
            std::string assign_rhs;
            if (!ParseRenderedSingleAssignmentLine((*lines)[probe],
                                                   &assign_is_local,
                                                   &assign_lhs,
                                                   &assign_rhs) ||
                assign_lhs != identifier) {
                continue;
            }

            const std::string normalized_rhs =
                StripRenderedWholeParentheses(TrimLuaWhitespace(assign_rhs), nullptr);
            if (normalized_rhs == "false" || normalized_rhs == "nil") {
                saw_seed = true;
                continue;
            }
            if (normalized_rhs == "true") {
                saw_true_assignment = true;
                continue;
            }

            saw_non_boolean_assignment = true;
            break;
        }

        if (saw_non_boolean_assignment || !saw_seed || !saw_true_assignment) {
            continue;
        }

        const std::string indent(GetRenderedLuaIndent((*lines)[line_index]));
        (*lines)[line_index] = indent + "if " + identifier + " == true then";
        changed = true;
    }

    return changed;
}

bool SimplifyRenderedLuaImmediateAssignedRepeatedConditions(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 1 < lines->size(); ++line_index) {
        bool assign_is_local = false;
        std::string assign_lhs;
        std::string assign_rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[line_index],
                                               &assign_is_local,
                                               &assign_lhs,
                                               &assign_rhs) ||
            !IsValidLuaIdentifier(assign_lhs) || assign_rhs.find('\n') != std::string::npos ||
            assign_rhs.find("function(") != std::string::npos) {
            continue;
        }

        int paren_balance = 0;
        int brace_balance = 0;
        if (HasPositiveLuaExpressionBalance(assign_rhs, &paren_balance, &brace_balance)) {
            continue;
        }

        std::string condition;
        std::string condition_prefix;
        std::string condition_suffix;
        if (!TryParseRenderedConditionHeaderLine((*lines)[line_index + 1],
                                                 &condition,
                                                 &condition_prefix,
                                                 &condition_suffix)) {
            continue;
        }

        const std::string normalized_assign_rhs =
            StripRenderedWholeParentheses(TrimLuaWhitespace(assign_rhs), nullptr);

        std::string rewritten_condition;
        if (!ReplaceRenderedSafePrefixExpressionChainUses(condition,
                                                          normalized_assign_rhs,
                                                          assign_lhs,
                                                          &rewritten_condition) ||
            rewritten_condition == condition) {
            continue;
        }

        const std::string indent(GetRenderedLuaIndent((*lines)[line_index + 1]));
        (*lines)[line_index + 1] =
            indent + condition_prefix + rewritten_condition + condition_suffix;
        changed = true;
    }

    return changed;
}

bool SimplifyRenderedLuaDependentAssignedRepeatedConditions(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 2 < lines->size(); ++line_index) {
        bool anchor_is_local = false;
        std::string anchor_lhs;
        std::string anchor_rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[line_index],
                                               &anchor_is_local,
                                               &anchor_lhs,
                                               &anchor_rhs) ||
            !IsValidLuaIdentifier(anchor_lhs) || anchor_rhs.find('\n') != std::string::npos ||
            anchor_rhs.find("function(") != std::string::npos) {
            continue;
        }

        int paren_balance = 0;
        int brace_balance = 0;
        if (HasPositiveLuaExpressionBalance(anchor_rhs, &paren_balance, &brace_balance)) {
            continue;
        }

        bool dependent_is_local = false;
        std::string dependent_lhs;
        std::string dependent_rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[line_index + 1],
                                               &dependent_is_local,
                                               &dependent_lhs,
                                               &dependent_rhs) ||
            !IsValidLuaIdentifier(dependent_lhs) || dependent_lhs == anchor_lhs ||
            dependent_rhs.find('\n') != std::string::npos ||
            dependent_rhs.find("function(") != std::string::npos) {
            continue;
        }

        if (CountIdentifierUses(dependent_rhs, anchor_lhs) == 0) {
            continue;
        }

        paren_balance = 0;
        brace_balance = 0;
        if (HasPositiveLuaExpressionBalance(dependent_rhs, &paren_balance, &brace_balance)) {
            continue;
        }

        std::string condition;
        std::string condition_prefix;
        std::string condition_suffix;
        if (!TryParseRenderedConditionHeaderLine((*lines)[line_index + 2],
                                                 &condition,
                                                 &condition_prefix,
                                                 &condition_suffix)) {
            continue;
        }

        std::string rewritten_condition = condition;
        bool condition_changed = false;

        std::string rewritten_step;
        if (ReplaceRenderedSafePrefixExpressionChainUses(rewritten_condition,
                                                         TrimLuaWhitespace(anchor_rhs),
                                                         anchor_lhs,
                                                         &rewritten_step) &&
            rewritten_step != rewritten_condition) {
            rewritten_condition = std::move(rewritten_step);
            condition_changed = true;
        }

        if (ReplaceRenderedSafePrefixExpressionChainUses(rewritten_condition,
                                                         TrimLuaWhitespace(dependent_rhs),
                                                         dependent_lhs,
                                                         &rewritten_step) &&
            rewritten_step != rewritten_condition) {
            rewritten_condition = std::move(rewritten_step);
            condition_changed = true;
        }

        if (!condition_changed || rewritten_condition == condition) {
            continue;
        }

        const std::string indent(GetRenderedLuaIndent((*lines)[line_index + 2]));
        (*lines)[line_index + 2] =
            indent + condition_prefix + rewritten_condition + condition_suffix;
        changed = true;
    }

    return changed;
}

bool SimplifyRenderedLuaNearbyAssignedRepeatedConditions(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto is_harmless_seed_line = [&](std::size_t line_index,
                                           std::string_view anchor_name,
                                           std::string_view anchor_expression) -> bool {
        if (line_index >= lines->size()) {
            return false;
        }

        bool is_local = false;
        std::vector<std::string> lhs_names;
        std::string rhs_expression;
        if (!ParseRenderedAssignmentLine((*lines)[line_index],
                                         &is_local,
                                         &lhs_names,
                                         &rhs_expression) ||
            !is_local || lhs_names.empty() || lhs_names.size() > 2) {
            return false;
        }

        const std::string normalized_rhs =
            StripRenderedWholeParentheses(TrimLuaWhitespace(rhs_expression), nullptr);
        if (normalized_rhs != "nil" && normalized_rhs != "false" && normalized_rhs != "0.0" &&
            normalized_rhs != "0") {
            return false;
        }

        if (CountIdentifierUses((*lines)[line_index], anchor_name) != 0 ||
            (!anchor_expression.empty() &&
             (*lines)[line_index].find(anchor_expression) != std::string::npos)) {
            return false;
        }

        for (const std::string& lhs_name : lhs_names) {
            if (!IsValidLuaIdentifier(lhs_name)) {
                return false;
            }
        }

        return true;
    };

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 2 < lines->size(); ++line_index) {
        bool assign_is_local = false;
        std::string assign_lhs;
        std::string assign_rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[line_index],
                                               &assign_is_local,
                                               &assign_lhs,
                                               &assign_rhs) ||
            !IsValidLuaIdentifier(assign_lhs) ||
            (!assign_is_local && !IsRenderTemporaryName(assign_lhs)) ||
            assign_rhs.find('\n') != std::string::npos ||
            assign_rhs.find("function(") != std::string::npos) {
            continue;
        }

        int paren_balance = 0;
        int brace_balance = 0;
        if (HasPositiveLuaExpressionBalance(assign_rhs, &paren_balance, &brace_balance)) {
            continue;
        }

        const std::string normalized_rhs =
            std::string(TrimLuaWhitespace(assign_rhs));
        std::size_t condition_index = line_index + 1;
        while (condition_index < lines->size() && condition_index <= line_index + 2 &&
               is_harmless_seed_line(condition_index, assign_lhs, normalized_rhs)) {
            ++condition_index;
        }

        if (condition_index >= lines->size() || condition_index == line_index + 1 ||
            condition_index > line_index + 2) {
            continue;
        }

        std::string condition;
        std::string condition_prefix;
        std::string condition_suffix;
        if (!TryParseRenderedConditionHeaderLine((*lines)[condition_index],
                                                 &condition,
                                                 &condition_prefix,
                                                 &condition_suffix)) {
            continue;
        }

        std::string rewritten_condition;
        if (!ReplaceRenderedSafePrefixExpressionChainUses(condition,
                                                          normalized_rhs,
                                                          assign_lhs,
                                                          &rewritten_condition) ||
            rewritten_condition == condition) {
            continue;
        }

        const std::string indent(GetRenderedLuaIndent((*lines)[condition_index]));
        (*lines)[condition_index] =
            indent + condition_prefix + rewritten_condition + condition_suffix;
        changed = true;
    }

    return changed;
}

bool SimplifyRenderedLuaNearbyAssignedStatementChainUses(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    static constexpr std::size_t kMaxProbeDistance = 16;

    std::vector<std::vector<std::size_t>> scope_paths;
    ComputeRenderedScopePaths(*lines, &scope_paths);

    const auto enters_new_loop_scope = [&](std::size_t anchor_line_index,
                                           std::size_t probe_line_index) -> bool {
        if (anchor_line_index >= scope_paths.size() || probe_line_index >= scope_paths.size()) {
            return false;
        }

        const std::vector<std::size_t>& anchor_path = scope_paths[anchor_line_index];
        const std::vector<std::size_t>& probe_path = scope_paths[probe_line_index];
        if (probe_path.size() <= anchor_path.size()) {
            return false;
        }

        if (!std::equal(anchor_path.begin(), anchor_path.end(), probe_path.begin())) {
            return false;
        }

        for (std::size_t index = anchor_path.size(); index < probe_path.size(); ++index) {
            const std::size_t scope_start = probe_path[index];
            if (scope_start < lines->size() &&
                RenderedLineBeginsLoopBlock((*lines)[scope_start])) {
                return true;
            }
        }

        return false;
    };

    const auto introduces_degenerate_self_compare = [](std::string_view statement) {
        std::string condition;
        if (!TryExtractRenderedIfCondition(statement, &condition) &&
            !TryExtractRenderedWhileCondition(statement, &condition)) {
            const std::string_view trimmed = TrimLuaWhitespace(StripLuaLineComment(statement));
            if (!trimmed.starts_with("elseif ") || !trimmed.ends_with(" then")) {
                return false;
            }
            condition = std::string(
                TrimLuaWhitespace(trimmed.substr(7, trimmed.size() - 12)));
        }

        condition = std::string(TrimLuaWhitespace(condition));
        if (condition.empty()) {
            return false;
        }

        std::vector<std::string> worklist;
        worklist.push_back(condition);
        while (!worklist.empty()) {
            const std::string current =
                std::string(TrimLuaWhitespace(worklist.back()));
            worklist.pop_back();
            if (current.empty()) {
                continue;
            }

            std::size_t compare_position = std::string::npos;
            std::string_view compare_op;
            if (FindRenderedTopLevelComparison(current, &compare_position, &compare_op)) {
                if (compare_op != "==" && compare_op != "~=") {
                    continue;
                }

                const std::string lhs = NormalizeRenderedConditionOperand(
                    current.substr(0, compare_position), nullptr);
                const std::string rhs = NormalizeRenderedConditionOperand(
                    current.substr(compare_position + compare_op.size()), nullptr);
                if (!lhs.empty() && lhs == rhs) {
                    return true;
                }
                continue;
            }

            std::vector<std::string> parts;
            if (SplitRenderedTopLevelBooleanChain(current, "and", &parts) &&
                parts.size() > 1) {
                for (const std::string& part : parts) {
                    worklist.push_back(part);
                }
                continue;
            }

            parts.clear();
            if (SplitRenderedTopLevelBooleanChain(current, "or", &parts) &&
                parts.size() > 1) {
                for (const std::string& part : parts) {
                    worklist.push_back(part);
                }
                continue;
            }
        }

        return false;
    };

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 1 < lines->size(); ++line_index) {
        bool assign_is_local = false;
        std::string assign_lhs;
        std::string assign_rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[line_index],
                                               &assign_is_local,
                                               &assign_lhs,
                                               &assign_rhs) ||
            !assign_is_local || !IsValidLuaIdentifier(assign_lhs) ||
            assign_rhs.find('\n') != std::string::npos ||
            assign_rhs.find("function(") != std::string::npos) {
            continue;
        }

        int paren_balance = 0;
        int brace_balance = 0;
        if (HasPositiveLuaExpressionBalance(assign_rhs, &paren_balance, &brace_balance)) {
            continue;
        }

        const std::string normalized_rhs =
            StripRenderedWholeParentheses(TrimLuaWhitespace(assign_rhs), nullptr);
        const bool rhs_is_named_identifier =
            IsValidLuaIdentifier(normalized_rhs) &&
            !IsRenderTemporaryName(normalized_rhs);
        std::string_view rhs_call_callee;
        std::string_view rhs_call_arguments;
        const bool rhs_is_volatile_call =
            TryParseRenderedTrailingCall(normalized_rhs, &rhs_call_callee, &rhs_call_arguments);
        if (normalized_rhs.empty() ||
            !IsRenderedSafePrefixExpression(normalized_rhs) ||
            CountIdentifierUses(normalized_rhs, assign_lhs) != 0) {
            continue;
        }
        if (rhs_is_volatile_call) {
            continue;
        }
        if (rhs_is_named_identifier) {
            continue;
        }
        const std::size_t scope_end =
            FindEnclosingRenderedFunctionBlockEnd(*lines, line_index);
        const std::size_t probe_limit =
            std::min(lines->size(),
                     std::min(line_index + kMaxProbeDistance + 1,
                              scope_end < lines->size() ? scope_end + 1 : lines->size()));
        bool saw_intervening_nonempty_statement = false;
        for (std::size_t probe = line_index + 1; probe < probe_limit; ++probe) {
            const std::string_view trimmed =
                TrimLuaWhitespace(StripLuaLineComment((*lines)[probe]));
            if (trimmed.empty()) {
                continue;
            }
            if (trimmed.ends_with(",")) {
                saw_intervening_nonempty_statement = true;
                continue;
            }

            if (enters_new_loop_scope(line_index, probe)) {
                saw_intervening_nonempty_statement = true;
                continue;
            }

            if (IsRenderedShadowingLocalDeclaration((*lines)[probe], assign_lhs) ||
                IsAssignmentToIdentifier((*lines)[probe], assign_lhs)) {
                break;
            }

            bool probe_is_local = false;
            std::string probe_lhs;
            std::string probe_rhs;
            const bool probe_is_single_assignment =
                ParseRenderedSingleAssignmentLine((*lines)[probe],
                                                  &probe_is_local,
                                                  &probe_lhs,
                                                  &probe_rhs);
            const std::string normalized_probe_lhs =
                probe_is_single_assignment
                    ? StripRenderedWholeParentheses(TrimLuaWhitespace(probe_lhs), nullptr)
                    : std::string();
            const bool rebinds_anchor_expression =
                probe_is_single_assignment && normalized_probe_lhs == normalized_rhs;
            std::string rewritten_line;
            if (!ReplaceRenderedSafePrefixExpressionChainUsesInStatementLine((*lines)[probe],
                                                                             normalized_rhs,
                                                                             assign_lhs,
                                                                             &rewritten_line) ||
                rewritten_line == (*lines)[probe]) {
                saw_intervening_nonempty_statement = true;
                if (rebinds_anchor_expression) {
                    break;
                }
                continue;
            }
            if (introduces_degenerate_self_compare(rewritten_line)) {
                saw_intervening_nonempty_statement = true;
                continue;
            }

            (*lines)[probe] = std::move(rewritten_line);
            changed = true;
            saw_intervening_nonempty_statement = true;

            if (rebinds_anchor_expression) {
                break;
            }
        }
    }

    return changed;
}

}  // namespace refii::lua
