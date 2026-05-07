#include "tools/decompiler/rendered/rendered_lua_text.h"

#include "tools/decompiler/rendered/rendered_cleanup_common_helpers.h"
#include "tools/decompiler/straight_line_render.h"

#include <cstddef>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

namespace refii::lua {

bool SimplifyRenderedLuaUnhandledRetargetBreakYieldLoops(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimLuaWhitespace(StripLuaLineComment(line));
    };
    const auto is_error_comment = [&](std::size_t line_index) {
        return line_index < lines->size() &&
               TrimLuaWhitespace((*lines)[line_index]) ==
                   "-- DECOMPILER ERROR: unhandled construct in 'if'";
    };
    const auto next_nonempty = [&](std::size_t start) -> std::size_t {
        for (std::size_t probe = start; probe < lines->size(); ++probe) {
            if (!TrimLuaWhitespace((*lines)[probe]).empty()) {
                return probe;
            }
        }
        return lines->size();
    };
    const auto next_nonempty_noncomment = [&](std::size_t start) -> std::size_t {
        for (std::size_t probe = start; probe < lines->size(); ++probe) {
            const std::string_view raw = TrimLuaWhitespace((*lines)[probe]);
            if (raw.empty() || raw.starts_with("--")) {
                continue;
            }
            return probe;
        }
        return lines->size();
    };
    const auto previous_nonempty = [&](std::size_t start_exclusive) -> std::size_t {
        std::size_t probe = start_exclusive;
        while (probe != 0) {
            --probe;
            if (!TrimLuaWhitespace((*lines)[probe]).empty()) {
                return probe;
            }
        }
        return lines->size();
    };
    const auto join_boolean_parts = [](const std::vector<std::string>& parts) -> std::string {
        if (parts.empty()) {
            return {};
        }

        std::string expression = parts.front();
        for (std::size_t index = 1; index < parts.size(); ++index) {
            expression = RenderLogicalExpression(expression, "and", parts[index]);
            if (expression.empty()) {
                return {};
            }
        }
        return expression;
    };

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        if (!is_error_comment(line_index)) {
            continue;
        }
        const std::size_t previous = previous_nonempty(line_index);
        if (previous < lines->size() && is_error_comment(previous)) {
            continue;
        }

        const std::size_t second_comment = next_nonempty(line_index + 1);
        if (!is_error_comment(second_comment)) {
            continue;
        }

        const std::size_t while_line = next_nonempty_noncomment(second_comment + 1);
        std::string while_condition;
        if (while_line >= lines->size() ||
            !TryExtractRenderedWhileCondition((*lines)[while_line], &while_condition)) {
            continue;
        }

        std::vector<std::string> while_parts;
        if (!SplitRenderedTopLevelBooleanChain(while_condition, "and", &while_parts) ||
            while_parts.size() < 3) {
            continue;
        }

        const std::string timer_condition =
            std::string(TrimLuaWhitespace(while_parts.front()));
        if (timer_condition.empty()) {
            continue;
        }

        std::string target_expression;
        std::string entity_expression;
        std::vector<std::string> retarget_parts;
        for (std::size_t index = 1; index < while_parts.size(); ++index) {
            const std::string normalized_part =
                std::string(TrimLuaWhitespace(while_parts[index]));
            std::size_t compare_position = std::string::npos;
            std::string_view compare_op;
            if (!target_expression.empty() ||
                !FindRenderedTopLevelComparison(
                    normalized_part, &compare_position, &compare_op) ||
                compare_op != "~=") {
                retarget_parts.push_back(normalized_part);
                continue;
            }

            const std::string lhs = NormalizeRenderedConditionOperand(
                normalized_part.substr(0, compare_position), nullptr);
            const std::string rhs = NormalizeRenderedConditionExpression(
                normalized_part.substr(compare_position + compare_op.size()), nullptr);
            if (lhs.find("CurrentShardBombardTarget") != std::string::npos &&
                rhs != "nil" && rhs != "current_bombard_target") {
                target_expression = lhs;
                entity_expression = rhs;
                continue;
            }
            retarget_parts.push_back(normalized_part);
        }
        if (target_expression.empty() || entity_expression.empty() || retarget_parts.empty()) {
            continue;
        }

        const std::size_t inner_if_line = next_nonempty_noncomment(while_line + 1);
        std::string distance_condition;
        if (inner_if_line >= lines->size() ||
            !TryExtractRenderedIfCondition((*lines)[inner_if_line], &distance_condition)) {
            continue;
        }
        if (CountIdentifierUses(distance_condition, entity_expression) == 0) {
            continue;
        }

        const std::size_t inner_if_end = FindRenderedCleanupBlockEnd(*lines, inner_if_line);
        if (inner_if_end >= lines->size() || trim_line((*lines)[inner_if_end]) != "end") {
            continue;
        }
        bool inner_body_empty = true;
        for (std::size_t probe = inner_if_line + 1; probe < inner_if_end; ++probe) {
            if (!trim_line((*lines)[probe]).empty()) {
                inner_body_empty = false;
                break;
            }
        }
        if (!inner_body_empty) {
            continue;
        }

        const std::size_t yield_line = next_nonempty_noncomment(inner_if_end + 1);
        const std::size_t while_end = FindRenderedCleanupBlockEnd(*lines, while_line);
        if (yield_line >= lines->size() ||
            TrimLuaWhitespace(StripLuaLineComment((*lines)[yield_line])) !=
                "coroutine.yield()" ||
            while_end >= lines->size() || trim_line((*lines)[while_end]) != "end" ||
            next_nonempty_noncomment(yield_line + 1) != while_end) {
            continue;
        }

        const std::string retarget_guard = join_boolean_parts(retarget_parts);
        if (retarget_guard.empty()) {
            continue;
        }

        const std::string entity_or_distance =
            "(" + target_expression + " == " + entity_expression + " or " + distance_condition +
            ")";
        const std::string break_condition = RenderLogicalExpression(
            "not (" + timer_condition + ")",
            "or",
            RenderLogicalExpression(retarget_guard, "and", entity_or_distance));
        if (break_condition.empty()) {
            continue;
        }

        const std::string indent(GetRenderedLuaIndent((*lines)[while_line]));
        const std::string body_indent = indent + "  ";
        const std::vector<std::string> replacement = {
            indent + "while true do",
            body_indent + "if " + break_condition + " then",
            body_indent + "  break",
            body_indent + "end",
            body_indent + "coroutine.yield()",
            indent + "end",
        };

        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                     lines->begin() + static_cast<std::ptrdiff_t>(while_end + 1));
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

bool SimplifyRenderedLuaMessagePollingLoops(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 4 < lines->size(); ++line_index) {
        std::vector<std::string> local_names;
        if (!ParseRenderedLocalDeclarationLine((*lines)[line_index], &local_names) ||
            local_names.size() != 1 || !IsValidLuaIdentifier(local_names.front())) {
            continue;
        }

        std::string outer_condition;
        std::string poll_condition;
        if (!TryExtractRenderedWhileCondition((*lines)[line_index + 1], &outer_condition) ||
            !TryExtractRenderedWhileCondition((*lines)[line_index + 2], &poll_condition) ||
            StripRenderedWholeParentheses(TrimLuaWhitespace(outer_condition), nullptr) !=
                "true" ||
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 3])) != "end" ||
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 4])) != "end") {
            continue;
        }

        std::string callee;
        std::vector<std::string> arguments;
        if (!ParseRenderedSingleCallLine(poll_condition, &callee, &arguments) ||
            !callee.starts_with("MessageEvents.") ||
            CountIdentifierUses(poll_condition, local_names.front()) == 0) {
            continue;
        }

        const std::string outer_indent(GetRenderedLuaIndent((*lines)[line_index + 1]));
        const std::string body_indent = outer_indent + "  ";
        std::vector<std::string> replacement = {
            outer_indent + "while true do",
            body_indent + "local is_posted, message = " + poll_condition,
            body_indent + "if is_posted then",
            body_indent + "  " + local_names.front() + " = message:GetID()",
            body_indent + "  break",
            body_indent + "end",
            outer_indent + "end",
        };

        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index + 1),
                     lines->begin() + static_cast<std::ptrdiff_t>(line_index + 5));
        lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(line_index + 1),
                      replacement.begin(),
                      replacement.end());
        changed = true;
        if (line_index != 0) {
            --line_index;
        }
    }

    return changed;
}

bool SimplifyRenderedLuaPrecheckedGuardedWaitLoops(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 7 < lines->size(); ++line_index) {
        std::string outer_condition;
        std::string repeated_condition;
        if (!TryExtractRenderedWhileCondition((*lines)[line_index], &outer_condition) ||
            !TryExtractRenderedWhileCondition((*lines)[line_index + 1], &repeated_condition) ||
            StripRenderedWholeParentheses(TrimLuaWhitespace(outer_condition), nullptr) !=
                "true" ||
            StripRenderedWholeParentheses(TrimLuaWhitespace(repeated_condition), nullptr) !=
                "true") {
            continue;
        }

        bool assign_is_local = false;
        std::vector<std::string> lhs_names;
        std::string assign_rhs;
        if (!ParseRenderedAssignmentLine((*lines)[line_index + 2],
                                         &assign_is_local,
                                         &lhs_names,
                                         &assign_rhs) ||
            !assign_is_local || lhs_names.size() != 1 ||
            !IsValidLuaIdentifier(lhs_names.front())) {
            continue;
        }

        const std::string flag_name = lhs_names.front();
        std::vector<std::string> or_parts;
        const std::string normalized_assign_rhs =
            StripRenderedWholeParentheses(TrimLuaWhitespace(assign_rhs), nullptr);
        if (!SplitRenderedTopLevelBooleanChain(normalized_assign_rhs, "or", &or_parts) ||
            or_parts.size() != 2 ||
            StripRenderedWholeParentheses(TrimLuaWhitespace(or_parts[1]), nullptr) !=
                "false") {
            continue;
        }

        std::vector<std::string> and_parts;
        if (!SplitRenderedTopLevelBooleanChain(
                StripRenderedWholeParentheses(TrimLuaWhitespace(or_parts[0]), nullptr),
                "and",
                &and_parts) ||
            and_parts.size() != 2) {
            continue;
        }

        std::string guard_condition;
        if (!TryExtractRenderedWhileCondition((*lines)[line_index + 3], &guard_condition) ||
            StripRenderedWholeParentheses(TrimLuaWhitespace(guard_condition), nullptr) !=
                ("not " + flag_name)) {
            continue;
        }

        const std::size_t wait_end = FindRenderedCleanupBlockEnd(*lines, line_index + 3);
        const std::size_t repeated_end = FindRenderedCleanupBlockEnd(*lines, line_index + 1);
        const std::size_t outer_end = FindRenderedCleanupBlockEnd(*lines, line_index);
        if (wait_end >= lines->size() || repeated_end >= lines->size() ||
            outer_end >= lines->size() || repeated_end != wait_end + 1 ||
            outer_end != repeated_end + 1) {
            continue;
        }

        bool saw_flag_assignment = false;
        for (std::size_t probe = line_index + 4; probe < wait_end; ++probe) {
            bool probe_is_local = false;
            std::string probe_lhs;
            std::string probe_rhs;
            if (!ParseRenderedSingleAssignmentLine((*lines)[probe],
                                                   &probe_is_local,
                                                   &probe_lhs,
                                                   &probe_rhs) ||
                probe_is_local || probe_lhs != flag_name ||
                StripRenderedWholeParentheses(TrimLuaWhitespace(probe_rhs), nullptr) !=
                    "true") {
                continue;
            }
            saw_flag_assignment = true;
            break;
        }
        if (!saw_flag_assignment) {
            continue;
        }

        const std::string normalized_guard =
            NormalizeRenderedConditionExpression(and_parts[0], nullptr);
        const std::string normalized_precheck =
            NormalizeRenderedConditionExpression(and_parts[1], nullptr);
        bool negated_changed = false;
        const std::string negated_guard =
            NegateRenderedConditionExpression(normalized_guard, &negated_changed);
        const std::string combined_precheck =
            RenderLogicalExpression(normalized_guard, "and", normalized_precheck);
        if (normalized_guard.empty() || normalized_precheck.empty() ||
            negated_guard.empty() || combined_precheck.empty()) {
            continue;
        }

        const std::string outer_indent(GetRenderedLuaIndent((*lines)[line_index]));
        const std::string outer_body_indent = outer_indent + "  ";
        const std::string nested_body_indent =
            std::string(GetRenderedLuaIndent((*lines)[line_index + 3]));

        std::vector<std::string> replacement;
        replacement.push_back(outer_indent + "while true do");
        replacement.push_back(outer_body_indent + "if " + combined_precheck + " then");
        replacement.push_back(outer_body_indent + "  break");
        replacement.push_back(outer_body_indent + "end");
        replacement.push_back(outer_body_indent + "local " + flag_name + " = false");
        for (std::size_t probe = line_index + 3; probe <= wait_end; ++probe) {
            std::string moved_line = (*lines)[probe];
            if (moved_line.starts_with(nested_body_indent)) {
                moved_line.replace(0, nested_body_indent.size(), outer_body_indent);
            }
            replacement.push_back(std::move(moved_line));
        }
        replacement.push_back(outer_body_indent + "if " + negated_guard + " then");
        replacement.push_back(outer_body_indent + "  break");
        replacement.push_back(outer_body_indent + "end");
        replacement.push_back(outer_indent + "end");

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

bool SimplifyRenderedLuaDirectBreakYieldLoops(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimLuaWhitespace(StripLuaLineComment(line));
    };
    const auto next_nonempty_noncomment = [&](std::size_t start) -> std::size_t {
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
    for (std::size_t line_index = 0; line_index + 4 < lines->size(); ++line_index) {
        std::string outer_condition;
        if (!TryExtractRenderedWhileCondition((*lines)[line_index], &outer_condition) ||
            StripRenderedWholeParentheses(TrimLuaWhitespace(outer_condition), nullptr) !=
                "true") {
            continue;
        }

        const std::size_t if_line = next_nonempty_noncomment(line_index + 1);
        std::string break_condition;
        if (if_line >= lines->size() ||
            !TryExtractRenderedIfCondition((*lines)[if_line], &break_condition)) {
            continue;
        }

        const std::size_t if_end = FindRenderedCleanupBlockEnd(*lines, if_line);
        const std::size_t while_end = FindRenderedCleanupBlockEnd(*lines, line_index);
        if (if_end >= lines->size() || while_end >= lines->size() ||
            trim_line((*lines)[if_end]) != "end" ||
            trim_line((*lines)[while_end]) != "end" ||
            RenderedLuaBlockHasTopLevelElse(*lines, if_line, if_end)) {
            continue;
        }

        const std::size_t break_line = next_nonempty_noncomment(if_line + 1);
        if (break_line >= if_end || trim_line((*lines)[break_line]) != "break" ||
            next_nonempty_noncomment(break_line + 1) != if_end) {
            continue;
        }

        const std::size_t yield_line = next_nonempty_noncomment(if_end + 1);
        if (yield_line >= while_end || trim_line((*lines)[yield_line]) != "coroutine.yield()" ||
            next_nonempty_noncomment(yield_line + 1) != while_end) {
            continue;
        }

        const std::string normalized_condition =
            StripRenderedWholeParentheses(TrimLuaWhitespace(break_condition), nullptr);
        if (normalized_condition.empty()) {
            continue;
        }

        const std::string while_indent(GetRenderedLuaIndent((*lines)[line_index]));
        const std::string body_indent = while_indent + "  ";
        const std::vector<std::string> replacement = {
            while_indent + "while not (" + normalized_condition + ") do",
            body_indent + "coroutine.yield()",
            while_indent + "end",
        };

        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                     lines->begin() + static_cast<std::ptrdiff_t>(while_end + 1));
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

bool SimplifyRenderedLuaConditionalCallFallbacks(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 10 < lines->size(); ++line_index) {
        std::string outer_condition;
        std::string guard_condition;
        std::string then_condition;
        std::string else_condition;
        if (!TryExtractRenderedIfCondition((*lines)[line_index], &outer_condition) ||
            !TryExtractRenderedIfCondition((*lines)[line_index + 1], &guard_condition) ||
            !TryExtractRenderedIfCondition((*lines)[line_index + 2], &then_condition) ||
            !TryExtractRenderedIfCondition((*lines)[line_index + 6], &else_condition)) {
            continue;
        }

        if (TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 4])) != "end" ||
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 5])) != "end" ||
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 8])) != "end" ||
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 9])) != "end") {
            continue;
        }

        std::string then_callee;
        std::vector<std::string> then_arguments;
        std::string else_callee;
        std::vector<std::string> else_arguments;
        std::string fallback_callee;
        std::vector<std::string> fallback_arguments;
        if (!ParseRenderedSingleCallLine((*lines)[line_index + 3],
                                         &then_callee,
                                         &then_arguments) ||
            !ParseRenderedSingleCallLine((*lines)[line_index + 7],
                                         &else_callee,
                                         &else_arguments) ||
            !ParseRenderedSingleCallLine((*lines)[line_index + 10],
                                         &fallback_callee,
                                         &fallback_arguments)) {
            continue;
        }

        if (then_callee != else_callee || then_arguments != else_arguments) {
            continue;
        }

        bool negated_guard_changed = false;
        const std::string negated_guard_condition = NegateRenderedConditionExpression(
            guard_condition, &negated_guard_changed);
        if (negated_guard_condition.empty()) {
            continue;
        }

        const std::string combined_then_condition = RenderLogicalExpression(
            guard_condition, "and", then_condition);
        const std::string combined_else_condition = RenderLogicalExpression(
            negated_guard_condition, "and", else_condition);
        const std::string combined_branch_condition = RenderLogicalExpression(
            combined_then_condition, "or", combined_else_condition);
        const std::string combined_condition = RenderLogicalExpression(
            outer_condition, "and", combined_branch_condition);
        if (combined_condition.empty()) {
            continue;
        }

        const std::string outer_indent(GetRenderedLuaIndent((*lines)[line_index]));
        const std::string inner_indent = outer_indent + "  ";
        const std::vector<std::string> replacement_lines = {
            outer_indent + "if " + combined_condition + " then",
            inner_indent +
                std::string(TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 3]))),
            outer_indent + "else",
            inner_indent +
                std::string(TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 10]))),
            outer_indent + "end",
        };
        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                     lines->begin() + static_cast<std::ptrdiff_t>(line_index + 11));
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

bool SimplifyRenderedLuaDanglingDestroyReplayCalls(std::vector<std::string>* lines) {
    if (lines == nullptr || lines->empty()) {
        return false;
    }

    const auto parse_bare_identifier_statement = [](std::string_view line,
                                                    std::string* identifier) -> bool {
        if (identifier == nullptr) {
            return false;
        }

        const std::string_view trimmed = TrimLuaWhitespace(StripLuaLineComment(line));
        if (trimmed.empty() || !IsValidLuaIdentifier(trimmed) || IsLuaKeyword(trimmed)) {
            return false;
        }

        *identifier = std::string(trimmed);
        return true;
    };

    const auto parse_destroy_call_statement = [](std::string_view line,
                                                 std::string* identifier) -> bool {
        if (identifier == nullptr) {
            return false;
        }

        const std::string_view trimmed = TrimLuaWhitespace(StripLuaLineComment(line));
        constexpr std::string_view kDestroySuffix = ":Destroy()";
        if (!trimmed.ends_with(kDestroySuffix) || trimmed.size() <= kDestroySuffix.size()) {
            return false;
        }

        const std::string_view candidate =
            trimmed.substr(0, trimmed.size() - kDestroySuffix.size());
        if (!IsValidLuaIdentifier(candidate) || IsLuaKeyword(candidate)) {
            return false;
        }

        *identifier = std::string(candidate);
        return true;
    };

    std::vector<std::vector<std::size_t>> scope_paths;
    ComputeRenderedScopePaths(*lines, &scope_paths);

    bool changed = false;
    for (std::size_t line_index = lines->size(); line_index-- > 0;) {
        std::string identifier;
        if (!parse_bare_identifier_statement((*lines)[line_index], &identifier)) {
            continue;
        }

        const std::string indent(GetRenderedLuaIndent((*lines)[line_index]));
        std::size_t destroy_line = lines->size();
        for (std::size_t probe = line_index; probe-- > 0;) {
            const std::string_view probe_trimmed =
                TrimLuaWhitespace(StripLuaLineComment((*lines)[probe]));
            if (probe_trimmed.empty()) {
                continue;
            }
            if (scope_paths[probe] != scope_paths[line_index] ||
                GetRenderedLuaIndent((*lines)[probe]) != indent) {
                break;
            }

            std::string skipped_identifier;
            if (parse_bare_identifier_statement((*lines)[probe], &skipped_identifier)) {
                continue;
            }

            std::string destroyed_identifier;
            if (parse_destroy_call_statement((*lines)[probe], &destroyed_identifier) &&
                destroyed_identifier == identifier) {
                destroy_line = probe;
            }
            break;
        }
        if (destroy_line >= lines->size()) {
            continue;
        }

        std::string replay_expression;
        for (std::size_t probe = destroy_line; probe-- > 0;) {
            const std::string_view probe_trimmed =
                TrimLuaWhitespace(StripLuaLineComment((*lines)[probe]));
            if (probe_trimmed.empty()) {
                continue;
            }
            if (scope_paths[probe] != scope_paths[line_index]) {
                break;
            }
            if (GetRenderedLuaIndent((*lines)[probe]) != indent) {
                continue;
            }

            bool is_local = false;
            std::string lhs_expression;
            std::string rhs_expression;
            if (ParseRenderedSingleAssignmentLine((*lines)[probe],
                                                  &is_local,
                                                  &lhs_expression,
                                                  &rhs_expression) &&
                lhs_expression == identifier) {
                std::string callee;
                std::vector<std::string> arguments;
                if (!ParseRenderedSingleCallLine(rhs_expression, &callee, &arguments)) {
                    replay_expression.clear();
                    break;
                }

                replay_expression = StripRenderedWholeParentheses(
                    TrimLuaWhitespace(rhs_expression), nullptr);
                break;
            }

            if (IsAssignmentToIdentifier((*lines)[probe], identifier) ||
                IsRenderedShadowingLocalDeclaration((*lines)[probe], identifier)) {
                replay_expression.clear();
                break;
            }
        }
        if (replay_expression.empty()) {
            continue;
        }

        (*lines)[line_index] = indent + replay_expression;
        changed = true;
    }

    return changed;
}

}  // namespace refii::lua
