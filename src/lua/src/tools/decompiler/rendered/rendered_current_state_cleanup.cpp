#include "tools/decompiler/rendered/rendered_lua_text.h"

#include "tools/decompiler/rendered/rendered_cleanup_common_helpers.h"
#include "tools/decompiler/straight_line_render.h"

#include <algorithm>
#include <cstddef>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

namespace refii::lua {

bool SimplifyRenderedLuaUnhandledNestedCurrentStateElseIfBranches(
    std::vector<std::string>* lines) {
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
    const auto next_nonempty = [&](std::size_t start) -> std::size_t {
        for (std::size_t probe = start; probe < lines->size(); ++probe) {
            if (!TrimLuaWhitespace((*lines)[probe]).empty()) {
                return probe;
            }
        }
        return lines->size();
    };
    const auto parse_if_like_condition = [&](std::string_view line, std::string* condition) {
        if (condition == nullptr) {
            return false;
        }

        const std::string_view trimmed = trim_line(line);
        if (trimmed.starts_with("if ") && trimmed.ends_with(" then")) {
            *condition = std::string(TrimLuaWhitespace(trimmed.substr(3, trimmed.size() - 8)));
            return !condition->empty();
        }
        if (trimmed.starts_with("elseif ") && trimmed.ends_with(" then")) {
            *condition = std::string(TrimLuaWhitespace(trimmed.substr(7, trimmed.size() - 12)));
            return !condition->empty();
        }
        return false;
    };
    const auto condition_matches_state = [&](std::string_view condition,
                                             std::string_view expected_lhs,
                                             std::string_view expected_rhs) {
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
        return lhs == expected_lhs && rhs == expected_rhs;
    };
    const auto is_supported_state_lhs = [](std::string_view lhs) {
        return lhs.find("CurrentState") != std::string_view::npos ||
               lhs.find("QuestState") != std::string_view::npos ||
               lhs.find("QuestStage") != std::string_view::npos ||
               lhs.find(".QuestState") != std::string_view::npos ||
               lhs.find(".Stage") != std::string_view::npos;
    };

    bool changed = false;
    RunRenderedLuaCleanupPassLoop(
        "SimplifyRenderedLuaUnhandledNestedCurrentStateElseIfBranches",
        96,
        [&]() {
            bool local_change = false;
            for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
                if (TrimLuaWhitespace((*lines)[line_index]) !=
                    "-- DECOMPILER ERROR: unhandled construct in 'if'") {
                    continue;
                }

                std::size_t cursor = next_nonempty_noncomment(line_index + 1);
                if (cursor >= lines->size()) {
                    continue;
                }

                if (trim_line((*lines)[cursor]) == "while true do") {
                    cursor = next_nonempty_noncomment(cursor + 1);
                }
                if (cursor >= lines->size()) {
                    continue;
                }

                std::string state0_condition;
                if (!parse_if_like_condition((*lines)[cursor], &state0_condition)) {
                    continue;
                }

                std::vector<std::string> state0_parts;
                if (!SplitRenderedTopLevelBooleanChain(state0_condition, "and", &state0_parts) ||
                    state0_parts.empty()) {
                    continue;
                }

                std::size_t compare_position = std::string::npos;
                std::string_view compare_op;
                if (!FindRenderedTopLevelComparison(
                        state0_parts.front(), &compare_position, &compare_op) ||
                    compare_op != "==") {
                    continue;
                }

                const std::string state_lhs = NormalizeRenderedConditionOperand(
                    state0_parts.front().substr(0, compare_position), nullptr);
                const std::string state_rhs = NormalizeRenderedConditionExpression(
                    state0_parts.front().substr(compare_position + compare_op.size()), nullptr);
                if (state_lhs.empty() || state_rhs != "0" ||
                    !is_supported_state_lhs(state_lhs)) {
                    continue;
                }

                std::size_t state1_line = lines->size();
                for (std::size_t probe = cursor + 1; probe < lines->size(); ++probe) {
                    std::string condition;
                    if (!parse_if_like_condition((*lines)[probe], &condition)) {
                        continue;
                    }
                    if (condition_matches_state(condition, state_lhs, "1") &&
                        trim_line((*lines)[probe]).starts_with("elseif ")) {
                        state1_line = probe;
                        break;
                    }
                }
                if (state1_line >= lines->size()) {
                    continue;
                }

                std::size_t top_level_boundary = lines->size();
                for (std::size_t probe = state1_line + 1; probe < lines->size(); ++probe) {
                    const std::string_view trimmed = trim_line((*lines)[probe]);
                    if (trimmed.empty()) {
                        continue;
                    }
                    if (GetRenderedLuaIndent((*lines)[probe]).empty() &&
                        !LooksLikeRenderedControlBlockHeader(trimmed)) {
                        top_level_boundary = probe;
                        break;
                    }
                }
                if (top_level_boundary >= lines->size()) {
                    continue;
                }

                const std::string outer_branch_indent(GetRenderedLuaIndent((*lines)[state1_line]));
                if (outer_branch_indent.empty()) {
                    continue;
                }

                bool applied_here = false;
                bool saw_outer_followup_branch = false;
                bool has_trapped_branch = false;
                for (std::size_t probe = state1_line + 1; probe < top_level_boundary; ++probe) {
                    std::string branch_condition;
                    if (!parse_if_like_condition((*lines)[probe], &branch_condition) ||
                        !trim_line((*lines)[probe]).starts_with("elseif ") ||
                        (!condition_matches_state(branch_condition, state_lhs, "2") &&
                         !condition_matches_state(branch_condition, state_lhs, "3"))) {
                        continue;
                    }

                    const std::string current_indent(GetRenderedLuaIndent((*lines)[probe]));
                    if (current_indent.size() <= outer_branch_indent.size() ||
                        !current_indent.starts_with(outer_branch_indent)) {
                        if (current_indent == outer_branch_indent) {
                            saw_outer_followup_branch = true;
                        }
                        continue;
                    }
                    has_trapped_branch = true;

                    bool found_matching_inner_if = false;
                    for (std::size_t back = probe; back-- > state1_line + 1;) {
                        const std::string_view trimmed = trim_line((*lines)[back]);
                        if (trimmed.empty() || trimmed.starts_with("--")) {
                            continue;
                        }
                        if (GetRenderedLuaIndent((*lines)[back]) != current_indent) {
                            continue;
                        }
                        if (trimmed.starts_with("if ") && trimmed.ends_with(" then")) {
                            found_matching_inner_if = true;
                        }
                        break;
                    }
                    if (!found_matching_inner_if) {
                        continue;
                    }

                    lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(probe),
                                  current_indent + "end");
                    ++probe;
                    (*lines)[probe] =
                        outer_branch_indent + std::string(trim_line((*lines)[probe]));
                    changed = true;
                    local_change = true;
                    applied_here = true;
                    break;
                }

                if (applied_here) {
                    break;
                }

                bool has_nested_comment = false;
                for (std::size_t probe = line_index + 1; probe < top_level_boundary; ++probe) {
                    if (TrimLuaWhitespace((*lines)[probe]) ==
                        "-- DECOMPILER ERROR: unhandled construct in 'if'") {
                        has_nested_comment = true;
                        break;
                    }
                }
                if (saw_outer_followup_branch && !has_trapped_branch && !has_nested_comment) {
                    lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index));
                    changed = true;
                    local_change = true;
                    break;
                }
            }
            return local_change;
        });

    return changed;
}

bool SimplifyRenderedLuaUnhandledNestedCurrentStateGuardBranches(
    std::vector<std::string>* lines) {
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
    const auto parse_if_like_condition = [&](std::string_view line, std::string* condition) {
        if (condition == nullptr) {
            return false;
        }

        const std::string_view trimmed = trim_line(line);
        if (trimmed.starts_with("if ") && trimmed.ends_with(" then")) {
            *condition = std::string(TrimLuaWhitespace(trimmed.substr(3, trimmed.size() - 8)));
            return !condition->empty();
        }
        if (trimmed.starts_with("elseif ") && trimmed.ends_with(" then")) {
            *condition = std::string(TrimLuaWhitespace(trimmed.substr(7, trimmed.size() - 12)));
            return !condition->empty();
        }
        return false;
    };
    const auto append_reindented_body = [&](std::vector<std::string>* replacement,
                                            std::size_t body_start,
                                            std::size_t body_end,
                                            std::string_view target_indent) -> bool {
        if (replacement == nullptr || body_start >= body_end || body_end > lines->size()) {
            return false;
        }

        std::size_t first_nonempty = body_end;
        for (std::size_t probe = body_start; probe < body_end; ++probe) {
            if (!trim_line((*lines)[probe]).empty()) {
                first_nonempty = probe;
                break;
            }
        }
        if (first_nonempty >= body_end) {
            return false;
        }

        const std::string source_indent(GetRenderedLuaIndent((*lines)[first_nonempty]));
        if (source_indent.empty()) {
            return false;
        }

        for (std::size_t probe = body_start; probe < body_end; ++probe) {
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

            if ((*lines)[probe].starts_with(source_indent)) {
                replacement->push_back(
                    std::string(target_indent) + (*lines)[probe].substr(source_indent.size()));
            } else {
                replacement->push_back(std::string(target_indent) +
                                       std::string(stripped_trimmed));
            }
        }
        return true;
    };
    const auto condition_matches_state = [&](std::string_view condition,
                                             std::string_view expected_lhs,
                                             std::string_view expected_rhs) {
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
        return lhs == expected_lhs && rhs == expected_rhs;
    };
    const auto is_supported_state_lhs = [](std::string_view lhs) {
        return lhs.find("CurrentState") != std::string_view::npos ||
               lhs.find("QuestState") != std::string_view::npos ||
               lhs.find("QuestStage") != std::string_view::npos ||
               lhs.find(".QuestState") != std::string_view::npos ||
               lhs.find(".Stage") != std::string_view::npos;
    };
    const auto join_boolean_parts = [](const std::vector<std::string>& parts,
                                       std::size_t begin,
                                       std::size_t end) -> std::string {
        if (begin >= end || end > parts.size()) {
            return {};
        }

        std::string expression = parts[begin];
        for (std::size_t index = begin + 1; index < end; ++index) {
            expression = RenderLogicalExpression(expression, "and", parts[index]);
            if (expression.empty()) {
                return {};
            }
        }
        return expression;
    };

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        if (TrimLuaWhitespace((*lines)[line_index]) !=
            "-- DECOMPILER ERROR: unhandled construct in 'if'") {
            continue;
        }

        const std::size_t while_line = next_nonempty_noncomment(line_index + 1);
        if (while_line >= lines->size() || trim_line((*lines)[while_line]) != "while true do") {
            continue;
        }

        const std::size_t state0_line = next_nonempty_noncomment(while_line + 1);
        std::string state0_condition;
        if (state0_line >= lines->size() ||
            !parse_if_like_condition((*lines)[state0_line], &state0_condition)) {
            continue;
        }

        std::vector<std::string> state0_parts;
        if (!SplitRenderedTopLevelBooleanChain(state0_condition, "and", &state0_parts) ||
            state0_parts.size() < 2) {
            continue;
        }

        std::size_t compare_position = std::string::npos;
        std::string_view compare_op;
        if (!FindRenderedTopLevelComparison(
                state0_parts.front(), &compare_position, &compare_op) ||
            compare_op != "==") {
            continue;
        }

        const std::string state_lhs = NormalizeRenderedConditionOperand(
            state0_parts.front().substr(0, compare_position), nullptr);
        const std::string state_rhs = NormalizeRenderedConditionExpression(
            state0_parts.front().substr(compare_position + compare_op.size()), nullptr);
        const std::string nested_guard =
            join_boolean_parts(state0_parts, 1, state0_parts.size());
        if (state_lhs.empty() || state_rhs != "0" || nested_guard.empty() ||
            !is_supported_state_lhs(state_lhs)) {
            continue;
        }

        const std::string state0_indent(GetRenderedLuaIndent((*lines)[state0_line]));
        std::size_t state1_line = lines->size();
        for (std::size_t probe = state0_line + 1; probe < lines->size(); ++probe) {
            std::string condition;
            if (!parse_if_like_condition((*lines)[probe], &condition) ||
                !trim_line((*lines)[probe]).starts_with("elseif ") ||
                !condition_matches_state(condition, state_lhs, "1")) {
                continue;
            }

            const std::string current_indent(GetRenderedLuaIndent((*lines)[probe]));
            if (current_indent.size() > state0_indent.size()) {
                state1_line = probe;
                break;
            }
        }
        if (state1_line >= lines->size()) {
            continue;
        }

        const std::string state1_indent(GetRenderedLuaIndent((*lines)[state1_line]));
        std::size_t state2_line = lines->size();
        for (std::size_t probe = state1_line + 1; probe < lines->size(); ++probe) {
            std::string condition;
            if (!parse_if_like_condition((*lines)[probe], &condition) ||
                !trim_line((*lines)[probe]).starts_with("elseif ") ||
                !condition_matches_state(condition, state_lhs, "2")) {
                continue;
            }
            if (GetRenderedLuaIndent((*lines)[probe]) == state1_indent) {
                state2_line = probe;
                break;
            }
        }
        if (state2_line >= lines->size()) {
            continue;
        }

        const std::size_t state1_body_start = next_nonempty_noncomment(state1_line + 1);
        const std::size_t state2_body_start = next_nonempty_noncomment(state2_line + 1);
        if (state1_body_start >= state2_line || state2_body_start >= lines->size()) {
            continue;
        }

        std::size_t state2_body_end = lines->size();
        for (std::size_t probe = state2_body_start + 1; probe < lines->size(); ++probe) {
            if (trim_line((*lines)[probe]) == "end") {
                state2_body_end = probe;
                break;
            }
        }
        if (state2_body_end >= lines->size()) {
            continue;
        }

        std::size_t yield_line = lines->size();
        for (std::size_t probe = state2_body_end + 1; probe < lines->size(); ++probe) {
            if (trim_line((*lines)[probe]) == "coroutine.yield()") {
                yield_line = probe;
                break;
            }
        }
        if (yield_line >= lines->size() || yield_line + 1 >= lines->size() ||
            trim_line((*lines)[yield_line + 1]) != "end") {
            continue;
        }

        const std::string while_indent(GetRenderedLuaIndent((*lines)[while_line]));
        const std::string outer_indent = while_indent + "  ";
        std::vector<std::string> replacement;
        replacement.push_back(while_indent + "while true do");
        replacement.push_back(outer_indent + "if " + state_lhs + " == 0 then");
        replacement.push_back(outer_indent + "  if " + nested_guard + " then");
        if (!append_reindented_body(
                &replacement, state0_line + 1, state1_line, outer_indent + "    ")) {
            continue;
        }
        replacement.push_back(outer_indent + "    end");
        replacement.push_back(outer_indent + "  end");
        replacement.push_back(outer_indent + "elseif " + state_lhs + " == 1 then");
        if (!append_reindented_body(
                &replacement, state1_body_start, state2_line, outer_indent + "  ")) {
            continue;
        }
        replacement.push_back(outer_indent + "elseif " + state_lhs + " == 2 then");
        if (!append_reindented_body(
                &replacement, state2_body_start, state2_body_end, outer_indent + "  ")) {
            continue;
        }
        replacement.push_back(outer_indent + "end");
        replacement.push_back(outer_indent + "coroutine.yield()");
        replacement.push_back(while_indent + "end");

        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                     lines->begin() + static_cast<std::ptrdiff_t>(yield_line + 1));
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

}  // namespace refii::lua
