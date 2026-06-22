#include "tools/decompiler/rendered/rendered_lua_text.h"
#include "tools/decompiler/rendered/rendered_cleanup_common_helpers.h"

#include <algorithm>
#include <cstddef>
#include <map>
#include <optional>
#include <set>
#include <string>
#include <string_view>
#include <vector>

namespace refii::lua {

static bool ParseRenderedGuardHeaderLine(std::string_view line,
                                         std::string* prefix,
                                         std::string* condition,
                                         std::string* suffix,
                                         bool* negated) {
    if (prefix == nullptr || condition == nullptr || suffix == nullptr || negated == nullptr) {
        return false;
    }

    const std::string indent(std::string(GetRenderedLuaIndent(line)));
    std::string extracted;
    if (TryExtractRenderedIfCondition(line, &extracted)) {
        *prefix = indent + "if ";
        *suffix = " then";
    } else if (TryExtractRenderedWhileCondition(line, &extracted)) {
        *prefix = indent + "while ";
        *suffix = " do";
    } else {
        const std::string_view trimmed = TrimLuaWhitespace(StripLuaLineComment(line));
        if (!trimmed.starts_with("elseif ") || !trimmed.ends_with(" then")) {
            return false;
        }
        extracted = std::string(
            TrimLuaWhitespace(trimmed.substr(7, trimmed.size() - 7 - 5)));
        *prefix = indent + "elseif ";
        *suffix = " then";
    }

    extracted = StripRenderedWholeParentheses(TrimLuaWhitespace(extracted), nullptr);
    *negated = false;
    if (StartsWithLuaKeyword(extracted, "not")) {
        *negated = true;
        extracted = StripRenderedWholeParentheses(
            TrimLuaWhitespace(std::string_view(extracted).substr(3)),
            nullptr);
    }
    if (extracted.empty()) {
        return false;
    }

    *condition = std::move(extracted);
    return true;
}

static bool FindRenderedConditionCleanupInlineTableTopLevelEquals(
    std::string_view entry,
    std::size_t* equal_out) {
    if (equal_out == nullptr) {
        return false;
    }

    bool in_single_quote = false;
    bool in_double_quote = false;
    bool escaped = false;
    int paren_depth = 0;
    int brace_depth = 0;
    int bracket_depth = 0;
    for (std::size_t index = 0; index < entry.size(); ++index) {
        const char ch = entry[index];
        if (escaped) {
            escaped = false;
            continue;
        }
        if ((in_single_quote || in_double_quote) && ch == '\\') {
            escaped = true;
            continue;
        }
        if (!in_double_quote && ch == '\'') {
            in_single_quote = !in_single_quote;
            continue;
        }
        if (!in_single_quote && ch == '"') {
            in_double_quote = !in_double_quote;
            continue;
        }
        if (in_single_quote || in_double_quote) {
            continue;
        }

        switch (ch) {
        case '(':
            ++paren_depth;
            break;
        case ')':
            if (paren_depth > 0) {
                --paren_depth;
            }
            break;
        case '{':
            ++brace_depth;
            break;
        case '}':
            if (brace_depth > 0) {
                --brace_depth;
            }
            break;
        case '[':
            ++bracket_depth;
            break;
        case ']':
            if (bracket_depth > 0) {
                --bracket_depth;
            }
            break;
        case '=':
            if (paren_depth == 0 && brace_depth == 0 && bracket_depth == 0) {
                *equal_out = index;
                return true;
            }
            break;
        default:
            break;
        }
    }

    return false;
}

static bool ParseRenderedConditionCleanupInlineTableEntry(std::string_view entry,
                                                          std::string* rendered_entry_out) {
    if (rendered_entry_out == nullptr) {
        return false;
    }

    std::size_t equal_pos = std::string_view::npos;
    if (!FindRenderedConditionCleanupInlineTableTopLevelEquals(entry, &equal_pos)) {
        return false;
    }

    const std::string_view lhs = TrimLuaWhitespace(entry.substr(0, equal_pos));
    const std::string_view rhs = TrimLuaWhitespace(entry.substr(equal_pos + 1));
    if (lhs.empty() || rhs.empty() || rhs.starts_with("function(") ||
        rhs.starts_with("function ") || rhs == "{") {
        return false;
    }

    if (IsValidLuaIdentifier(lhs) || (lhs.front() == '[' && lhs.back() == ']')) {
        *rendered_entry_out = std::string(lhs) + " = " + std::string(rhs);
        return true;
    }

    return false;
}

static bool TryParseRenderedConditionCleanupInlineTableEntries(
    std::string_view rhs_expression,
    std::size_t max_entry_count,
    std::vector<std::string>* entries_out) {
    if (entries_out == nullptr) {
        return false;
    }

    const std::string normalized_rhs =
        StripRenderedWholeParentheses(TrimLuaWhitespace(rhs_expression), nullptr);
    if (normalized_rhs.size() < 2 || normalized_rhs.front() != '{' ||
        normalized_rhs.back() != '}') {
        return false;
    }

    const std::string_view inner =
        TrimLuaWhitespace(std::string_view(normalized_rhs).substr(1, normalized_rhs.size() - 2));
    if (inner.empty()) {
        return false;
    }

    std::vector<std::string> raw_entries;
    if (!SplitRenderedTopLevelCommaChain(inner, &raw_entries) || raw_entries.empty() ||
        raw_entries.size() > max_entry_count) {
        return false;
    }

    std::vector<std::string> parsed_entries;
    parsed_entries.reserve(raw_entries.size());
    for (const std::string& raw_entry : raw_entries) {
        std::string rendered_entry;
        if (!ParseRenderedConditionCleanupInlineTableEntry(raw_entry, &rendered_entry)) {
            return false;
        }
        parsed_entries.push_back(std::move(rendered_entry));
    }

    *entries_out = std::move(parsed_entries);
    return true;
}

bool SimplifyRenderedLuaRedundantGuardConditionParentheses(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (std::string& line : *lines) {
        const std::string_view stripped = StripLuaLineComment(line);

        std::string prefix;
        std::string condition;
        std::string suffix;
        bool negated = false;
        if (!ParseRenderedGuardHeaderLine(line, &prefix, &condition, &suffix, &negated)) {
            continue;
        }

        const std::string rebuilt =
            prefix + (negated ? "not " : std::string()) + condition + suffix;
        if (TrimLuaWhitespace(stripped) == TrimLuaWhitespace(rebuilt)) {
            continue;
        }

        std::string comment_suffix;
        if (stripped.size() < line.size()) {
            comment_suffix = std::string(line.substr(stripped.size()));
            if (!comment_suffix.empty() && comment_suffix.front() == '-') {
                comment_suffix.insert(comment_suffix.begin(), ' ');
            }
        }

        line = rebuilt + comment_suffix;
        changed = true;
    }

    return changed;
}

bool HoistRenderedLuaSingleWhileElseTailsForExactParity(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto trim_line = [](std::string_view line) {
        return TrimLuaWhitespace(StripLuaLineComment(line));
    };
    const auto find_block_end = [&](std::size_t block_start,
                                    std::size_t limit,
                                    std::size_t* block_end_out) -> bool {
        if (block_end_out == nullptr || block_start >= lines->size() || block_start > limit) {
            return false;
        }

        int depth = 0;
        for (std::size_t index = block_start; index <= limit && index < lines->size(); ++index) {
            const std::string_view trimmed = trim_line((*lines)[index]);
            if (trimmed.empty()) {
                continue;
            }

            depth += GetRenderedLuaBlockDelta(trimmed);
            if (index > block_start && depth == 0) {
                *block_end_out = index;
                return true;
            }
        }
        return false;
    };

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        std::string if_condition;
        if (!TryExtractRenderedIfCondition((*lines)[line_index], &if_condition)) {
            continue;
        }

        std::size_t else_line = lines->size();
        std::size_t end_line = lines->size();
        int depth = 0;
        for (std::size_t probe = line_index; probe < lines->size(); ++probe) {
            const std::string_view trimmed = trim_line((*lines)[probe]);
            if (trimmed.empty()) {
                continue;
            }
            if (probe > line_index && depth == 1 && trimmed == "else" &&
                else_line == lines->size()) {
                else_line = probe;
            }

            depth += GetRenderedLuaBlockDelta(trimmed);
            if (probe > line_index && depth == 0) {
                end_line = probe;
                break;
            }
        }
        if (else_line >= end_line || end_line >= lines->size()) {
            continue;
        }

        std::size_t while_line = lines->size();
        std::size_t while_end = lines->size();
        bool unsupported_then_body = false;
        int then_nested_depth = 0;
        for (std::size_t probe = line_index + 1; probe < else_line; ++probe) {
            const std::string_view trimmed = trim_line((*lines)[probe]);
            if (trimmed.empty()) {
                continue;
            }

            if (then_nested_depth == 0) {
                if (while_line == lines->size()) {
                    if (!TryExtractRenderedWhileCondition((*lines)[probe], &if_condition)) {
                        unsupported_then_body = true;
                        break;
                    }
                    while_line = probe;
                } else if (probe > while_end) {
                    unsupported_then_body = true;
                    break;
                }
            }

            then_nested_depth += GetRenderedLuaBlockDelta(trimmed);
            if (probe == while_line && while_end == lines->size()) {
                if (!find_block_end(while_line, else_line - 1, &while_end)) {
                    unsupported_then_body = true;
                    break;
                }
            }
        }
        if (unsupported_then_body || while_line == lines->size() ||
            while_end == lines->size() || while_end + 1 != else_line) {
            continue;
        }

        bool has_else_body = false;
        for (std::size_t probe = else_line + 1; probe < end_line; ++probe) {
            if (!trim_line((*lines)[probe]).empty()) {
                has_else_body = true;
                break;
            }
        }
        if (!has_else_body) {
            continue;
        }

        const std::vector<std::string> else_body(lines->begin() +
                                                     static_cast<std::ptrdiff_t>(else_line + 1),
                                                 lines->begin() +
                                                     static_cast<std::ptrdiff_t>(end_line));
        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(else_line),
                     lines->begin() + static_cast<std::ptrdiff_t>(end_line));
        lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(else_line + 1),
                      else_body.begin(),
                      else_body.end());
        changed = true;
        line_index = else_line;
    }

    return changed;
}

bool CollapseRenderedLuaNestedSharedElseGuardsForExactParity(
    std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto trim_line = [](std::string_view line) {
        return TrimLuaWhitespace(StripLuaLineComment(line));
    };
    const auto find_if_else_end = [&](std::size_t block_start,
                                      std::size_t limit,
                                      std::size_t* else_out,
                                      std::size_t* end_out) -> bool {
        if (else_out == nullptr || end_out == nullptr || block_start >= lines->size() ||
            block_start > limit) {
            return false;
        }

        *else_out = lines->size();
        *end_out = lines->size();
        int depth = 0;
        for (std::size_t index = block_start; index <= limit && index < lines->size(); ++index) {
            const std::string_view trimmed = trim_line((*lines)[index]);
            if (trimmed.empty()) {
                continue;
            }

            if (index > block_start && depth == 1 && trimmed == "else" &&
                *else_out == lines->size()) {
                *else_out = index;
            }

            depth += GetRenderedLuaBlockDelta(trimmed);
            if (index > block_start && depth == 0) {
                *end_out = index;
                return *else_out < *end_out;
            }
        }
        return false;
    };
    const auto find_if_end_without_else = [&](std::size_t block_start,
                                              std::size_t limit,
                                              std::size_t* end_out) -> bool {
        if (end_out == nullptr || block_start >= lines->size() || block_start > limit) {
            return false;
        }

        *end_out = lines->size();
        int depth = 0;
        for (std::size_t index = block_start; index <= limit && index < lines->size(); ++index) {
            const std::string_view trimmed = trim_line((*lines)[index]);
            if (trimmed.empty()) {
                continue;
            }

            if (index > block_start && depth == 1 &&
                (trimmed == "else" || trimmed.starts_with("elseif "))) {
                return false;
            }

            depth += GetRenderedLuaBlockDelta(trimmed);
            if (index > block_start && depth == 0) {
                *end_out = index;
                return true;
            }
        }
        return false;
    };

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        std::string outer_condition;
        if (!TryExtractRenderedIfCondition((*lines)[line_index], &outer_condition)) {
            continue;
        }

        std::size_t outer_else = lines->size();
        std::size_t outer_end = lines->size();
        if (!find_if_else_end(line_index, lines->size() - 1, &outer_else, &outer_end) ||
            outer_else >= outer_end || outer_end >= lines->size()) {
            continue;
        }

        std::size_t inner_if = lines->size();
        std::size_t inner_end = lines->size();
        std::string inner_condition;
        bool unsupported_then_body = false;
        int nested_depth = 0;
        for (std::size_t probe = line_index + 1; probe < outer_else; ++probe) {
            const std::string_view trimmed = trim_line((*lines)[probe]);
            if (trimmed.empty()) {
                continue;
            }

            if (nested_depth == 0) {
                if (inner_if == lines->size()) {
                    if (!TryExtractRenderedIfCondition((*lines)[probe], &inner_condition)) {
                        unsupported_then_body = true;
                        break;
                    }
                    inner_if = probe;
                    if (!find_if_end_without_else(inner_if, outer_else - 1, &inner_end)) {
                        unsupported_then_body = true;
                        break;
                    }
                } else if (probe > inner_end) {
                    unsupported_then_body = true;
                    break;
                }
            }

            nested_depth += GetRenderedLuaBlockDelta(trimmed);
        }
        if (unsupported_then_body || inner_if == lines->size() || inner_end == lines->size() ||
            inner_end + 1 != outer_else) {
            continue;
        }

        bool has_inner_body = false;
        for (std::size_t probe = inner_if + 1; probe < inner_end; ++probe) {
            if (!trim_line((*lines)[probe]).empty()) {
                has_inner_body = true;
                break;
            }
        }
        if (!has_inner_body) {
            continue;
        }

        const std::string normalized_outer =
            StripRenderedWholeParentheses(TrimLuaWhitespace(outer_condition), nullptr);
        const std::string normalized_inner =
            StripRenderedWholeParentheses(TrimLuaWhitespace(inner_condition), nullptr);
        if (normalized_outer.empty() || normalized_inner.empty()) {
            continue;
        }

        const std::string indent(GetRenderedLuaIndent((*lines)[line_index]));
        (*lines)[line_index] =
            indent + "if (" + normalized_outer + ") and (" + normalized_inner + ") then";
        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(inner_end));
        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(inner_if));
        changed = true;
        line_index = inner_if;
    }

    return changed;
}

bool CollapseRenderedLuaReusedConditionTempElseGuardsForExactParity(
    std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto trim_line = [](std::string_view line) {
        return TrimLuaWhitespace(StripLuaLineComment(line));
    };
    const auto next_nonempty_line = [&](std::size_t start) {
        std::size_t index = start;
        while (index < lines->size() && trim_line((*lines)[index]).empty()) {
            ++index;
        }
        return index;
    };
    const auto previous_nonempty_line = [&](std::size_t start) {
        std::size_t index = std::min(start, lines->size());
        while (index > 0) {
            --index;
            if (!trim_line((*lines)[index]).empty()) {
                return index;
            }
        }
        return lines->size();
    };
    const auto find_if_else_end = [&](std::size_t block_start,
                                      std::size_t limit,
                                      std::size_t* else_out,
                                      std::size_t* end_out) -> bool {
        if (else_out == nullptr || end_out == nullptr || block_start >= lines->size() ||
            block_start > limit) {
            return false;
        }

        *else_out = lines->size();
        *end_out = lines->size();
        int depth = 0;
        for (std::size_t index = block_start; index <= limit && index < lines->size(); ++index) {
            const std::string_view trimmed = trim_line((*lines)[index]);
            if (trimmed.empty()) {
                continue;
            }

            if (index > block_start && depth == 1 && trimmed == "else" &&
                *else_out == lines->size()) {
                *else_out = index;
            }

            depth += GetRenderedLuaBlockDelta(trimmed);
            if (index > block_start && depth == 0) {
                *end_out = index;
                return *else_out < *end_out;
            }
        }
        return false;
    };
    const auto find_if_end_without_else = [&](std::size_t block_start,
                                              std::size_t limit,
                                              std::size_t* end_out) -> bool {
        if (end_out == nullptr || block_start >= lines->size() || block_start > limit) {
            return false;
        }

        *end_out = lines->size();
        int depth = 0;
        for (std::size_t index = block_start; index <= limit && index < lines->size(); ++index) {
            const std::string_view trimmed = trim_line((*lines)[index]);
            if (trimmed.empty()) {
                continue;
            }

            if (index > block_start && depth == 1 &&
                (trimmed == "else" || trimmed.starts_with("elseif "))) {
                return false;
            }

            depth += GetRenderedLuaBlockDelta(trimmed);
            if (index > block_start && depth == 0) {
                *end_out = index;
                return true;
            }
        }
        return false;
    };

    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        bool seed_is_local = false;
        std::string seed_lhs;
        std::string seed_rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[line_index],
                                               &seed_is_local,
                                               &seed_lhs,
                                               &seed_rhs) ||
            !seed_is_local || !IsValidLuaIdentifier(seed_lhs)) {
            continue;
        }

        const std::string normalized_seed_rhs =
            StripRenderedWholeParentheses(TrimLuaWhitespace(seed_rhs), nullptr);
        if (normalized_seed_rhs.empty()) {
            continue;
        }

        const std::size_t outer_if_line = next_nonempty_line(line_index + 1);
        std::string outer_condition;
        if (outer_if_line >= lines->size() ||
            !TryExtractRenderedIfCondition((*lines)[outer_if_line], &outer_condition) ||
            StripRenderedWholeParentheses(TrimLuaWhitespace(outer_condition), nullptr) != seed_lhs) {
            continue;
        }

        std::size_t outer_else = lines->size();
        std::size_t outer_end = lines->size();
        if (!find_if_else_end(outer_if_line, lines->size() - 1, &outer_else, &outer_end) ||
            outer_else >= outer_end || outer_end >= lines->size()) {
            continue;
        }

        const std::size_t reassignment_line = next_nonempty_line(outer_if_line + 1);
        bool reassignment_is_local = false;
        std::string reassignment_lhs;
        std::string reassignment_rhs;
        if (reassignment_line >= outer_else ||
            !ParseRenderedSingleAssignmentLine((*lines)[reassignment_line],
                                               &reassignment_is_local,
                                               &reassignment_lhs,
                                               &reassignment_rhs) ||
            reassignment_lhs != seed_lhs) {
            continue;
        }

        const std::string normalized_reassignment_rhs =
            StripRenderedWholeParentheses(TrimLuaWhitespace(reassignment_rhs), nullptr);
        if (normalized_reassignment_rhs.empty()) {
            continue;
        }

        const std::size_t inner_if_line = next_nonempty_line(reassignment_line + 1);
        std::string inner_condition;
        if (inner_if_line >= outer_else ||
            !TryExtractRenderedIfCondition((*lines)[inner_if_line], &inner_condition)) {
            continue;
        }

        const std::string normalized_inner_condition =
            StripRenderedWholeParentheses(TrimLuaWhitespace(inner_condition), nullptr);
        if (normalized_inner_condition != seed_lhs &&
            normalized_inner_condition != normalized_seed_rhs &&
            normalized_inner_condition != normalized_reassignment_rhs) {
            continue;
        }

        std::size_t inner_end = lines->size();
        if (!find_if_end_without_else(inner_if_line, outer_else - 1, &inner_end) ||
            inner_end >= outer_else) {
            continue;
        }

        if (next_nonempty_line(reassignment_line + 1) != inner_if_line ||
            next_nonempty_line(inner_end + 1) != outer_else) {
            continue;
        }

        std::size_t first_inner_body = next_nonempty_line(inner_if_line + 1);
        std::size_t first_else_body = next_nonempty_line(outer_else + 1);
        if (first_inner_body >= inner_end || first_else_body >= outer_end) {
            continue;
        }

        const std::string outer_indent(GetRenderedLuaIndent((*lines)[outer_if_line]));
        const std::string collapsed_header =
            outer_indent + "if (" + normalized_seed_rhs + ") and (" +
            normalized_reassignment_rhs + ") then";
        const std::string inner_body_indent(GetRenderedLuaIndent((*lines)[first_inner_body]));
        const std::string outer_body_indent = outer_indent + "  ";

        bool use_inline_table_builder_else = false;
        std::size_t prefix_begin = line_index;
        std::string table_temp_name;
        std::string table_call_callee;
        std::vector<std::string> table_call_arguments;
        std::vector<std::string> table_entries;

        std::vector<std::size_t> else_significant_lines;
        for (std::size_t probe = outer_else + 1; probe < outer_end; ++probe) {
            if (!trim_line((*lines)[probe]).empty()) {
                else_significant_lines.push_back(probe);
            }
        }
        if (else_significant_lines.size() >= 2) {
            std::string table_assign_lhs;
            std::string table_assign_rhs;
            if (ParseRenderedSingleAssignmentLine((*lines)[else_significant_lines.front()],
                                                  nullptr,
                                                  &table_assign_lhs,
                                                  &table_assign_rhs) &&
                IsValidLuaIdentifier(table_assign_lhs)) {
                table_temp_name = std::move(table_assign_lhs);

                const std::size_t previous_nonempty = previous_nonempty_line(line_index);
                if (previous_nonempty < line_index &&
                    !TrimLuaWhitespace(StripLuaLineComment(
                        (*lines)[else_significant_lines.front()]))
                         .starts_with("local ")) {
                    bool table_seed_is_local = false;
                    std::string table_seed_lhs;
                    std::string table_seed_rhs;
                    if (ParseRenderedSingleAssignmentLine((*lines)[previous_nonempty],
                                                          &table_seed_is_local,
                                                          &table_seed_lhs,
                                                          &table_seed_rhs) &&
                        table_seed_is_local && !table_seed_rhs.empty() &&
                        table_seed_lhs == table_temp_name) {
                        prefix_begin = previous_nonempty;
                    }
                }

                const std::size_t call_line = else_significant_lines.back();
                if (ParseRenderedSingleCallLine((*lines)[call_line],
                                               &table_call_callee,
                                               &table_call_arguments) &&
                    !table_call_arguments.empty()) {
                    const std::string normalized_last_argument =
                        StripRenderedWholeParentheses(
                            TrimLuaWhitespace(table_call_arguments.back()),
                            nullptr);
                    if (normalized_last_argument == table_temp_name) {
                        if (TrimLuaWhitespace(table_assign_rhs) == "{}") {
                            bool valid_field_builder = true;
                            for (std::size_t index = 1; index + 1 < else_significant_lines.size();
                                 ++index) {
                                bool field_assign_is_local = false;
                                std::string field_lhs;
                                std::string field_rhs;
                                if (!ParseRenderedSingleAssignmentLine(
                                        (*lines)[else_significant_lines[index]],
                                        &field_assign_is_local,
                                        &field_lhs,
                                        &field_rhs) ||
                                    field_assign_is_local ||
                                    !field_lhs.starts_with(table_temp_name + ".")) {
                                    valid_field_builder = false;
                                    break;
                                }

                                const std::string field_name =
                                    field_lhs.substr(table_temp_name.size() + 1);
                                if (!IsValidLuaIdentifier(field_name)) {
                                    valid_field_builder = false;
                                    break;
                                }
                                table_entries.push_back(field_name + " = " + field_rhs);
                            }

                            use_inline_table_builder_else =
                                valid_field_builder && !table_entries.empty();
                        } else if (else_significant_lines.size() == 2) {
                            use_inline_table_builder_else =
                                TryParseRenderedConditionCleanupInlineTableEntries(
                                    table_assign_rhs,
                                    8,
                                    &table_entries);
                        }
                    }
                }
            }
        }

        std::vector<std::string> rewritten_lines;
        rewritten_lines.reserve(lines->size());
        rewritten_lines.insert(rewritten_lines.end(), lines->begin(), lines->begin() +
                               static_cast<std::ptrdiff_t>(prefix_begin));
        rewritten_lines.push_back(collapsed_header);
        for (std::size_t probe = inner_if_line + 1; probe < inner_end; ++probe) {
            std::string rewritten = (*lines)[probe];
            if (!inner_body_indent.empty() &&
                rewritten.starts_with(inner_body_indent)) {
                rewritten =
                    outer_body_indent + rewritten.substr(inner_body_indent.size());
            }
            rewritten_lines.push_back(std::move(rewritten));
        }
        if (use_inline_table_builder_else) {
            const std::string call_indent(GetRenderedLuaIndent((*lines)[else_significant_lines.back()]));
            rewritten_lines.push_back((*lines)[outer_else]);

            std::string call_open = call_indent + table_call_callee + "(";
            for (std::size_t arg_index = 0; arg_index + 1 < table_call_arguments.size();
                 ++arg_index) {
                if (arg_index != 0) {
                    call_open += ", ";
                }
                call_open += table_call_arguments[arg_index];
            }
            if (table_call_arguments.size() > 1) {
                call_open += ", ";
            }
            call_open += "{";
            rewritten_lines.push_back(std::move(call_open));
            for (const std::string& table_entry : table_entries) {
                rewritten_lines.push_back(call_indent + "  " + table_entry + ",");
            }
            rewritten_lines.push_back(call_indent + "})");
            rewritten_lines.push_back((*lines)[outer_end]);
            rewritten_lines.insert(rewritten_lines.end(),
                                   lines->begin() + static_cast<std::ptrdiff_t>(outer_end + 1),
                                   lines->end());
        } else {
            rewritten_lines.insert(rewritten_lines.end(),
                                   lines->begin() + static_cast<std::ptrdiff_t>(inner_end + 1),
                                   lines->end());
        }
        *lines = std::move(rewritten_lines);
        return true;
    }

    return false;
}

bool RemoveRenderedLuaSubsumedTailBreakGuardsForExactParity(
    std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto trim_line = [](std::string_view line) {
        return TrimLuaWhitespace(StripLuaLineComment(line));
    };
    const auto normalize_condition = [](std::string_view condition) {
        return StripRenderedWholeParentheses(TrimLuaWhitespace(condition), nullptr);
    };
    const auto canonicalize_comparison = [&](std::string_view expression) {
        const std::string normalized = normalize_condition(expression);

        std::size_t compare_position = std::string_view::npos;
        std::string_view compare_op;
        if (!FindRenderedTopLevelComparison(normalized, &compare_position, &compare_op)) {
            return normalized;
        }

        const std::string lhs =
            normalize_condition(std::string_view(normalized).substr(0, compare_position));
        const std::string rhs = normalize_condition(
            std::string_view(normalized).substr(compare_position + compare_op.size()));
        if (lhs.empty() || rhs.empty()) {
            return normalized;
        }

        std::string swapped_op;
        if (compare_op == "<") {
            swapped_op = ">";
        } else if (compare_op == ">") {
            swapped_op = "<";
        } else if (compare_op == "<=") {
            swapped_op = ">=";
        } else if (compare_op == ">=") {
            swapped_op = "<=";
        } else {
            return normalized;
        }

        const std::string swapped = rhs + " " + swapped_op + " " + lhs;
        return std::min(normalized, swapped);
    };
    const auto next_nonempty_line =
        [&](std::size_t start, std::size_t end_exclusive) -> std::size_t {
            for (std::size_t probe = start; probe < end_exclusive && probe < lines->size();
                 ++probe) {
                if (!trim_line((*lines)[probe]).empty()) {
                    return probe;
                }
            }
            return end_exclusive;
        };
    const auto find_block_end =
        [&](std::size_t block_start,
            std::size_t limit,
            std::size_t* end_out) -> bool {
            if (end_out == nullptr || block_start >= lines->size() || block_start > limit) {
                return false;
            }

            *end_out = lines->size();
            int depth = 0;
            for (std::size_t index = block_start; index <= limit && index < lines->size(); ++index) {
                const std::string_view trimmed = trim_line((*lines)[index]);
                if (trimmed.empty()) {
                    continue;
                }

                depth += GetRenderedLuaBlockDelta(trimmed);
                if (index > block_start && depth == 0) {
                    *end_out = index;
                    return true;
                }
            }
            return false;
        };
    const auto find_if_end_without_else =
        [&](std::size_t block_start,
            std::size_t limit,
            std::size_t* end_out) -> bool {
            if (end_out == nullptr || block_start >= lines->size() || block_start > limit) {
                return false;
            }

            *end_out = lines->size();
            int depth = 0;
            for (std::size_t index = block_start; index <= limit && index < lines->size(); ++index) {
                const std::string_view trimmed = trim_line((*lines)[index]);
                if (trimmed.empty()) {
                    continue;
                }

                if (index > block_start && depth == 1 &&
                    (trimmed == "else" || trimmed.starts_with("elseif "))) {
                    return false;
                }

                depth += GetRenderedLuaBlockDelta(trimmed);
                if (index > block_start && depth == 0) {
                    *end_out = index;
                    return true;
                }
            }
            return false;
        };
    const auto expression_mentions_condition =
        [&](std::string_view expression,
            std::string_view target,
            const auto& self) -> bool {
            const std::string normalized_expression = normalize_condition(expression);
            if (canonicalize_comparison(normalized_expression) ==
                canonicalize_comparison(target)) {
                return true;
            }

            std::vector<std::string> parts;
            if (SplitRenderedTopLevelBooleanChain(normalized_expression, "or", &parts) &&
                parts.size() > 1) {
                for (const std::string& part : parts) {
                    if (self(part, target, self)) {
                        return true;
                    }
                }
            }

            parts.clear();
            if (SplitRenderedTopLevelBooleanChain(normalized_expression, "and", &parts) &&
                parts.size() > 1) {
                for (const std::string& part : parts) {
                    if (self(part, target, self)) {
                        return true;
                    }
                }
            }

            return false;
        };

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        std::string while_condition;
        if (!TryExtractRenderedWhileCondition((*lines)[line_index], &while_condition)) {
            continue;
        }

        std::size_t loop_end = lines->size();
        if (!find_block_end(line_index, lines->size() - 1, &loop_end) ||
            loop_end >= lines->size()) {
            continue;
        }

        std::size_t yield_line = lines->size();
        for (std::size_t probe = line_index + 1; probe < loop_end; ++probe) {
            if (trim_line((*lines)[probe]).find("coroutine.yield(") != std::string::npos) {
                yield_line = probe;
                break;
            }
        }
        if (yield_line >= loop_end) {
            continue;
        }

        std::size_t break_if_line = lines->size();
        std::size_t break_if_end = lines->size();
        std::string break_condition;
        for (std::size_t probe = line_index + 1; probe < yield_line; ++probe) {
            std::string candidate_condition;
            if (!TryExtractRenderedIfCondition((*lines)[probe], &candidate_condition)) {
                continue;
            }

            std::size_t candidate_end = lines->size();
            if (!find_if_end_without_else(probe, yield_line - 1, &candidate_end) ||
                candidate_end >= yield_line) {
                continue;
            }

            const std::size_t break_line = next_nonempty_line(probe + 1, candidate_end);
            if (break_line >= candidate_end || trim_line((*lines)[break_line]) != "break" ||
                next_nonempty_line(break_line + 1, candidate_end) != candidate_end ||
                next_nonempty_line(candidate_end + 1, loop_end) != yield_line) {
                continue;
            }

            break_if_line = probe;
            break_if_end = candidate_end;
            break_condition = std::move(candidate_condition);
            break;
        }
        if (break_if_line >= yield_line || break_if_end >= yield_line ||
            break_condition.empty()) {
            continue;
        }

        const std::string normalized_break_condition =
            normalize_condition(break_condition);
        bool removed = false;
        for (std::size_t probe = line_index + 1; probe < break_if_line; ++probe) {
            std::string guard_condition;
            if (!TryExtractRenderedIfCondition((*lines)[probe], &guard_condition)) {
                continue;
            }

            std::size_t guard_end = lines->size();
            if (!find_if_end_without_else(probe, break_if_line - 1, &guard_end) ||
                guard_end >= break_if_line ||
                next_nonempty_line(guard_end + 1, loop_end) != break_if_line ||
                next_nonempty_line(probe + 1, guard_end) >= guard_end ||
                !expression_mentions_condition(guard_condition,
                                              normalized_break_condition,
                                              expression_mentions_condition)) {
                continue;
            }

            lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(break_if_line),
                         lines->begin() + static_cast<std::ptrdiff_t>(break_if_end + 1));
            changed = true;
            removed = true;
            break;
        }
        if (removed && line_index != 0) {
            --line_index;
        }
    }

    return changed;
}

bool SimplifyRenderedLuaImmediateAssignedConditionExpressionUses(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto next_nonempty_index = [&](std::size_t start) -> std::size_t {
        std::size_t index = start;
        while (index < lines->size() &&
               TrimLuaWhitespace(StripLuaLineComment((*lines)[index])).empty()) {
            ++index;
        }
        return index;
    };

    std::vector<std::vector<std::size_t>> scope_paths;
    ComputeRenderedScopePaths(*lines, &scope_paths);

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 1 < lines->size(); ++line_index) {
        if (line_index >= scope_paths.size()) {
            continue;
        }

        bool assign_is_local = false;
        std::string assign_lhs;
        std::string assign_rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[line_index],
                                               &assign_is_local,
                                               &assign_lhs,
                                               &assign_rhs)) {
            continue;
        }

        const std::string normalized_lhs =
            StripRenderedWholeParentheses(TrimLuaWhitespace(assign_lhs), nullptr);
        const std::string normalized_rhs =
            StripRenderedWholeParentheses(TrimLuaWhitespace(assign_rhs), nullptr);
        int paren_balance = 0;
        int brace_balance = 0;
        if (!IsValidLuaIdentifier(normalized_lhs) ||
            normalized_rhs.empty() ||
            !IsRenderedSafePrefixExpression(normalized_rhs) ||
            assign_rhs.find('\n') != std::string::npos ||
            assign_rhs.find("function(") != std::string::npos ||
            HasPositiveLuaExpressionBalance(assign_rhs, &paren_balance, &brace_balance) ||
            CountIdentifierUses(normalized_rhs, normalized_lhs) != 0) {
            continue;
        }

        const std::size_t guard_index = next_nonempty_index(line_index + 1);
        if (guard_index >= lines->size() || guard_index >= scope_paths.size() ||
            scope_paths[guard_index] != scope_paths[line_index]) {
            continue;
        }

        std::string guard_prefix;
        std::string guard_condition;
        std::string guard_suffix;
        bool negated = false;
        if (!ParseRenderedGuardHeaderLine((*lines)[guard_index],
                                          &guard_prefix,
                                          &guard_condition,
                                          &guard_suffix,
                                          &negated) ||
            CountIdentifierUses(guard_condition, normalized_rhs) == 0) {
            continue;
        }

        std::string rewritten_condition;
        if (!ReplaceRenderedSafePrefixExpressionChainUses(guard_condition,
                                                          normalized_rhs,
                                                          normalized_lhs,
                                                          &rewritten_condition) ||
            rewritten_condition == guard_condition) {
            continue;
        }

        (*lines)[guard_index] =
            guard_prefix + (negated ? "not " : "") + rewritten_condition + guard_suffix;
        changed = true;
    }

    return changed;
}

bool SimplifyRenderedLuaImmediateAssignedCallGuards(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 2 < lines->size(); ++line_index) {
        bool assign_is_local = false;
        std::vector<std::string> lhs_names;
        std::string assign_rhs;
        if (!ParseRenderedAssignmentLine((*lines)[line_index],
                                         &assign_is_local,
                                         &lhs_names,
                                         &assign_rhs) ||
            lhs_names.empty()) {
            continue;
        }

        std::string guard_prefix;
        std::string guard_condition;
        std::string guard_suffix;
        bool negated = false;
        if (!ParseRenderedGuardHeaderLine((*lines)[line_index + 1],
                                          &guard_prefix,
                                          &guard_condition,
                                          &guard_suffix,
                                          &negated)) {
            continue;
        }

        const std::string normalized_assign_rhs =
            StripRenderedWholeParentheses(TrimLuaWhitespace(assign_rhs), nullptr);
        const std::string guard_name =
            StripRenderedWholeParentheses(TrimLuaWhitespace(lhs_names.front()), nullptr);
        if (normalized_assign_rhs.empty() || normalized_assign_rhs != guard_condition ||
            !IsValidLuaIdentifier(guard_name)) {
            continue;
        }

        (*lines)[line_index + 1] =
            guard_prefix + (negated ? "not " : "") + guard_name + guard_suffix;
        changed = true;
    }

    return changed;
}

bool SimplifyRenderedLuaImmediateAssignedMethodCallGuards(std::vector<std::string>* lines) {
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
            assign_lhs.empty()) {
            continue;
        }

        std::string if_condition;
        if (!TryExtractRenderedIfCondition((*lines)[line_index + 1], &if_condition)) {
            continue;
        }

        const std::string normalized_assign_rhs =
            StripRenderedWholeParentheses(TrimLuaWhitespace(assign_rhs), nullptr);
        if (normalized_assign_rhs.empty()) {
            continue;
        }

        std::string normalized_condition =
            StripRenderedWholeParentheses(TrimLuaWhitespace(if_condition), nullptr);
        std::string prefix;
        if (normalized_condition.starts_with("not ")) {
            prefix = "not ";
            normalized_condition = std::string(
                TrimLuaWhitespace(normalized_condition.substr(4)));
        }

        const std::string method_prefix = normalized_assign_rhs + ":";
        if (!normalized_condition.starts_with(method_prefix)) {
            continue;
        }

        const std::string rewritten_condition =
            prefix + assign_lhs + normalized_condition.substr(normalized_assign_rhs.size());
        const std::string indent(GetRenderedLuaIndent((*lines)[line_index + 1]));
        (*lines)[line_index + 1] = indent + "if " + rewritten_condition + " then";
        changed = true;
    }

    return changed;
}

bool SimplifyRenderedLuaImmediateAssignedRepeatedCallLoops(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 1 < lines->size(); ++line_index) {
        bool assign_is_local = false;
        std::vector<std::string> lhs_names;
        std::string assign_rhs;
        if (!ParseRenderedAssignmentLine((*lines)[line_index],
                                         &assign_is_local,
                                         &lhs_names,
                                         &assign_rhs) ||
            lhs_names.empty()) {
            continue;
        }

        std::string while_condition;
        if (!TryExtractRenderedWhileCondition((*lines)[line_index + 1], &while_condition)) {
            continue;
        }

        const std::string normalized_assign_rhs =
            StripRenderedWholeParentheses(TrimLuaWhitespace(assign_rhs), nullptr);
        const std::string normalized_condition =
            StripRenderedWholeParentheses(TrimLuaWhitespace(while_condition), nullptr);
        const std::string guard_name =
            StripRenderedWholeParentheses(TrimLuaWhitespace(lhs_names.front()), nullptr);
        if (normalized_assign_rhs.empty() || normalized_assign_rhs != normalized_condition ||
            !IsValidLuaIdentifier(guard_name)) {
            continue;
        }

        const std::string indent(GetRenderedLuaIndent((*lines)[line_index + 1]));
        (*lines)[line_index + 1] = indent + "while " + guard_name + " do";
        changed = true;
    }

    return changed;
}

bool SimplifyRenderedLuaRepeatedCallGuards(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 4 < lines->size(); ++line_index) {
        bool assign_is_local = false;
        std::string assign_lhs;
        std::string assign_rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[line_index],
                                               &assign_is_local,
                                               &assign_lhs,
                                               &assign_rhs)) {
            continue;
        }

        std::string call_callee;
        std::vector<std::string> call_arguments;
        if (!ParseRenderedSingleCallLine(assign_rhs, &call_callee, &call_arguments)) {
            continue;
        }

        std::string if_condition;
        if (!TryExtractRenderedIfCondition((*lines)[line_index + 1], &if_condition) ||
            StripRenderedWholeParentheses(TrimLuaWhitespace(if_condition), nullptr) !=
                StripRenderedWholeParentheses(TrimLuaWhitespace(assign_rhs), nullptr)) {
            continue;
        }

        std::size_t end_index = std::string::npos;
        for (std::size_t probe = line_index + 2; probe < lines->size(); ++probe) {
            const std::string_view trimmed =
                TrimLuaWhitespace(StripLuaLineComment((*lines)[probe]));
            if (trimmed == "end") {
                end_index = probe;
                break;
            }
            if (trimmed == "else" || LooksLikeRenderedControlBlockHeader(trimmed)) {
                end_index = std::string::npos;
                break;
            }
        }
        if (end_index == std::string::npos || end_index + 1 >= lines->size()) {
            continue;
        }

        if (TrimLuaWhitespace(StripLuaLineComment((*lines)[end_index + 1])) != "break") {
            continue;
        }

        const std::string if_indent(GetRenderedLuaIndent((*lines)[line_index + 1]));
        (*lines)[line_index + 1] = if_indent + "if " + assign_lhs + " then";
        const std::string break_line = (*lines)[end_index + 1];
        lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(end_index), break_line);
        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(end_index + 2));
        changed = true;
    }

    return changed;
}

bool SimplifyRenderedLuaRepeatedCallFieldGuardsForParity(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto split_trailing_field = [](std::string_view expression,
                                         std::string* base_out,
                                         std::string* field_out) -> bool {
        if (base_out == nullptr || field_out == nullptr) {
            return false;
        }
        expression = TrimLuaWhitespace(expression);
        int paren_depth = 0;
        std::size_t dot_position = std::string::npos;
        for (std::size_t index = 0; index < expression.size(); ++index) {
            const char ch = expression[index];
            if (ch == '(') {
                ++paren_depth;
            } else if (ch == ')') {
                if (paren_depth <= 0) {
                    return false;
                }
                --paren_depth;
            } else if (ch == '.' && paren_depth == 0) {
                dot_position = index;
            }
        }
        if (paren_depth != 0 || dot_position == std::string::npos ||
            dot_position + 1 >= expression.size()) {
            return false;
        }
        const std::string field =
            std::string(TrimLuaWhitespace(expression.substr(dot_position + 1)));
        if (!IsValidLuaIdentifier(field)) {
            return false;
        }
        *base_out = std::string(TrimLuaWhitespace(expression.substr(0, dot_position)));
        *field_out = field;
        return !base_out->empty();
    };

    const auto calls_match = [](std::string_view lhs_call,
                                std::string_view rhs_call) -> bool {
        std::string lhs_callee;
        std::vector<std::string> lhs_args;
        std::string rhs_callee;
        std::vector<std::string> rhs_args;
        if (!ParseRenderedSingleCallLine(lhs_call, &lhs_callee, &lhs_args) ||
            !ParseRenderedSingleCallLine(rhs_call, &rhs_callee, &rhs_args) ||
            lhs_callee != rhs_callee ||
            lhs_args.size() != rhs_args.size()) {
            return false;
        }
        for (std::size_t index = 0; index < lhs_args.size(); ++index) {
            if (NormalizeRenderedConditionOperand(lhs_args[index], nullptr) !=
                NormalizeRenderedConditionOperand(rhs_args[index], nullptr)) {
                return false;
            }
        }
        return true;
    };

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 1 < lines->size(); ++line_index) {
        bool assign_is_local = false;
        std::vector<std::string> lhs_names;
        std::string rhs_expression;
        if (!ParseRenderedAssignmentLine((*lines)[line_index],
                                         &assign_is_local,
                                         &lhs_names,
                                         &rhs_expression) ||
            !assign_is_local || lhs_names.size() != 1 ||
            !IsValidLuaIdentifier(lhs_names.front())) {
            continue;
        }
        std::string assigned_callee;
        std::vector<std::string> assigned_args;
        if (!ParseRenderedSingleCallLine(rhs_expression, &assigned_callee, &assigned_args)) {
            continue;
        }

        std::string condition;
        if (!TryExtractRenderedIfCondition((*lines)[line_index + 1], &condition)) {
            continue;
        }

        std::string condition_base;
        std::string field_name;
        if (!split_trailing_field(condition, &condition_base, &field_name) ||
            !calls_match(rhs_expression, condition_base)) {
            continue;
        }

        const std::string indent(GetRenderedLuaIndent((*lines)[line_index + 1]));
        (*lines)[line_index + 1] =
            indent + "if " + lhs_names.front() + "." + field_name + " then";
        changed = true;
    }

    return changed;
}

bool NormalizeRenderedLuaMeterPercentLocalForParity(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto contains_identifier = [](std::string_view text,
                                        std::string_view identifier) -> bool {
        if (identifier.empty()) {
            return false;
        }
        std::size_t position = text.find(identifier);
        while (position != std::string_view::npos) {
            const bool left_ok =
                position == 0 ||
                (!std::isalnum(static_cast<unsigned char>(text[position - 1])) &&
                 text[position - 1] != '_');
            const std::size_t right = position + identifier.size();
            const bool right_ok =
                right >= text.size() ||
                (!std::isalnum(static_cast<unsigned char>(text[right])) &&
                 text[right] != '_');
            if (left_ok && right_ok) {
                return true;
            }
            position = text.find(identifier, position + 1);
        }
        return false;
    };

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        bool is_local = false;
        std::vector<std::string> lhs_names;
        std::string rhs_expression;
        if (!ParseRenderedAssignmentLine((*lines)[line_index],
                                         &is_local,
                                         &lhs_names,
                                         &rhs_expression) ||
            !is_local || lhs_names.size() != 1 || lhs_names.front() != "percent") {
            continue;
        }

        const std::string rhs = std::string(TrimLuaWhitespace(rhs_expression));
        if (rhs.empty() || rhs.find("function") != std::string::npos ||
            rhs.find("...") != std::string::npos ||
            rhs.find("Drink") == std::string::npos ||
            rhs.find("PINT_SIZE") == std::string::npos) {
            continue;
        }

        const std::size_t function_end =
            FindEnclosingRenderedFunctionBlockEnd(*lines, line_index);
        if (function_end >= lines->size() || function_end <= line_index + 1) {
            continue;
        }

        bool has_later_percent_use = false;
        bool has_meter_set_z_use = false;
        bool has_second_local_percent = false;
        for (std::size_t probe = line_index + 1; probe < function_end; ++probe) {
            const std::string_view stripped =
                TrimLuaWhitespace(StripLuaLineComment((*lines)[probe]));
            if (stripped.empty()) {
                continue;
            }

            bool probe_is_local = false;
            std::vector<std::string> probe_lhs;
            std::string probe_rhs;
            if (ParseRenderedAssignmentLine(stripped,
                                            &probe_is_local,
                                            &probe_lhs,
                                            &probe_rhs) &&
                probe_is_local) {
                for (const std::string& lhs : probe_lhs) {
                    if (lhs == "percent") {
                        has_second_local_percent = true;
                    }
                }
            }
            if (has_second_local_percent) {
                break;
            }

            if (contains_identifier(stripped, "percent")) {
                has_later_percent_use = true;
                if (stripped.find(":SetZ") != std::string_view::npos) {
                    has_meter_set_z_use = true;
                }
            }
        }

        if (has_second_local_percent || !has_later_percent_use || !has_meter_set_z_use) {
            continue;
        }

        const std::string indent(GetRenderedLuaIndent((*lines)[line_index]));
        (*lines)[line_index] = indent + "percent = " + rhs;
        changed = true;
    }

    return changed;
}

bool NormalizeRenderedLuaDebugDrawTextZOffsetForParity(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimLuaWhitespace(StripLuaLineComment(line));
    };

    const auto parse_zero_offset_set_z = [&](std::string_view line,
                                             std::string* variable_out) -> bool {
        if (variable_out == nullptr) {
            return false;
        }

        const std::string_view trimmed = trim_line(line);
        const std::size_t colon = trimmed.find(":SetZ(");
        if (colon == std::string_view::npos) {
            return false;
        }

        const std::string_view variable = TrimLuaWhitespace(trimmed.substr(0, colon));
        if (!IsValidLuaIdentifier(variable)) {
            return false;
        }

        const std::string expected =
            std::string(variable) + ":SetZ(" + std::string(variable) + ":GetZ())";
        if (trimmed != expected) {
            return false;
        }

        *variable_out = std::string(variable);
        return true;
    };

    const auto debug_draws_variable = [&](std::string_view line,
                                          std::string_view variable) -> bool {
        const std::string_view trimmed = trim_line(line);
        constexpr std::string_view kPrefix = "Debug.Draw3DText(";
        if (!trimmed.starts_with(kPrefix) || !trimmed.ends_with(")")) {
            return false;
        }

        const std::string_view args =
            trimmed.substr(kPrefix.size(), trimmed.size() - kPrefix.size() - 1);
        std::vector<std::string> parts;
        if (!SplitRenderedTopLevelCommaChain(args, &parts) || parts.empty()) {
            return false;
        }

        return TrimLuaWhitespace(parts.front()) == variable;
    };

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 1 < lines->size(); ++line_index) {
        std::string variable;
        if (!parse_zero_offset_set_z((*lines)[line_index], &variable)) {
            continue;
        }

        std::size_t next_line = line_index + 1;
        while (next_line < lines->size() && trim_line((*lines)[next_line]).empty()) {
            ++next_line;
        }
        if (next_line >= lines->size() ||
            !debug_draws_variable((*lines)[next_line], variable)) {
            continue;
        }

        const std::string indent(GetRenderedLuaIndent((*lines)[line_index]));
        (*lines)[line_index] = indent + variable + ":SetZ(" + variable + ":GetZ() + 2)";
        changed = true;
    }

    return changed;
}

bool SimplifyRenderedLuaScopedRepeatedCallComparisonGuardsForParity(
    std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto replace_exact_call_prefix = [](std::string_view condition,
                                              std::string_view call_expression,
                                              std::string_view local_name,
                                              std::string* replacement) -> bool {
        if (replacement == nullptr || call_expression.empty() || local_name.empty()) {
            return false;
        }

        condition = TrimLuaWhitespace(condition);
        call_expression = TrimLuaWhitespace(call_expression);
        if (condition.size() <= call_expression.size() ||
            condition.substr(0, call_expression.size()) != call_expression) {
            return false;
        }

        const char next = condition[call_expression.size()];
        if (next != ' ' && next != '<' && next != '>' && next != '=' && next != '~') {
            return false;
        }

        *replacement = std::string(local_name) +
                       std::string(condition.substr(call_expression.size()));
        return true;
    };

    bool changed = false;
    constexpr std::size_t kMaxForwardScan = 8;
    for (std::size_t line_index = 0; line_index + 1 < lines->size(); ++line_index) {
        bool is_local = false;
        std::vector<std::string> lhs_names;
        std::string rhs_expression;
        if (!ParseRenderedAssignmentLine((*lines)[line_index],
                                         &is_local,
                                         &lhs_names,
                                         &rhs_expression) ||
            !is_local || lhs_names.size() != 1 ||
            !IsValidLuaIdentifier(lhs_names.front())) {
            continue;
        }

        std::string assigned_callee;
        std::vector<std::string> assigned_args;
        if (!ParseRenderedSingleCallLine(rhs_expression, &assigned_callee, &assigned_args)) {
            continue;
        }

        const std::size_t function_begin =
            FindEnclosingRenderedFunctionBlockBegin(*lines, line_index);
        const std::size_t function_end =
            FindEnclosingRenderedFunctionBlockEnd(*lines, line_index);
        if (function_begin >= lines->size() || function_end >= lines->size()) {
            continue;
        }

        const std::size_t scan_end =
            std::min(function_end, line_index + kMaxForwardScan + 1);
        bool blocked = false;
        for (std::size_t probe = line_index + 1; probe < scan_end; ++probe) {
            const std::string_view stripped =
                TrimLuaWhitespace(StripLuaLineComment((*lines)[probe]));
            if (stripped.empty()) {
                continue;
            }

            std::string condition;
            if (TryExtractRenderedIfCondition(stripped, &condition)) {
                std::string replacement_condition;
                if (!replace_exact_call_prefix(condition,
                                               rhs_expression,
                                               lhs_names.front(),
                                               &replacement_condition)) {
                    break;
                }

                const std::string indent(GetRenderedLuaIndent((*lines)[probe]));
                (*lines)[probe] = indent + "if " + replacement_condition + " then";
                changed = true;
                break;
            }

            bool probe_is_local = false;
            std::vector<std::string> probe_lhs;
            std::string probe_rhs;
            if (!ParseRenderedAssignmentLine(stripped,
                                            &probe_is_local,
                                            &probe_lhs,
                                            &probe_rhs) ||
                !probe_is_local) {
                blocked = true;
                break;
            }
            for (const std::string& lhs : probe_lhs) {
                if (lhs == lhs_names.front()) {
                    blocked = true;
                    break;
                }
            }
            if (blocked) {
                break;
            }
        }
    }

    return changed;
}

bool ExpandRenderedLuaAndNotElseGuardsForParity(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto find_outer_else = [](const std::vector<std::string>& text_lines,
                                    std::size_t if_line,
                                    std::size_t* else_line) -> bool {
        if (else_line == nullptr) {
            return false;
        }
        int depth = 0;
        for (std::size_t probe = if_line + 1; probe < text_lines.size(); ++probe) {
            const std::string_view stripped =
                TrimLuaWhitespace(StripLuaLineComment(text_lines[probe]));
            if (stripped.empty()) {
                continue;
            }
            if (depth == 0 && stripped == "else") {
                *else_line = probe;
                return true;
            }
            if (stripped == "end") {
                if (depth == 0) {
                    return false;
                }
                --depth;
                continue;
            }
            if (RenderedLineBeginsScopedBlock(stripped)) {
                ++depth;
            }
        }
        return false;
    };

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 2 < lines->size(); ++line_index) {
        std::string condition;
        if (!TryExtractRenderedIfCondition((*lines)[line_index], &condition)) {
            continue;
        }

        std::vector<std::string> parts;
        if (!SplitRenderedTopLevelBooleanChain(condition, "and", &parts) ||
            parts.size() != 2) {
            continue;
        }

        const std::string outer_condition =
            StripRenderedWholeParentheses(TrimLuaWhitespace(parts[0]), nullptr);
        std::string inner_condition =
            StripRenderedWholeParentheses(TrimLuaWhitespace(parts[1]), nullptr);
        if (outer_condition.empty() || inner_condition.empty() ||
            !StartsWithLuaKeyword(inner_condition, "not")) {
            continue;
        }

        const std::string next_trimmed =
            std::string(TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 1])));
        if (next_trimmed == "if " + inner_condition + " then") {
            continue;
        }

        std::size_t else_line = lines->size();
        if (!find_outer_else(*lines, line_index, &else_line) || else_line <= line_index + 1) {
            continue;
        }

        const std::string indent(GetRenderedLuaIndent((*lines)[line_index]));
        (*lines)[line_index] = indent + "if " + outer_condition + " then";
        lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(line_index + 1),
                      indent + "if " + inner_condition + " then");
        lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(else_line + 1),
                      indent + "end");
        changed = true;
        ++line_index;
    }

    return changed;
}

bool SimplifyRenderedLuaImmediateRedundantConditionTemps(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto parse_guard_header = [](std::string_view line,
                                       std::string* condition,
                                       bool* negated) {
        if (condition == nullptr || negated == nullptr) {
            return false;
        }

        std::string extracted;
        if (!TryExtractRenderedIfCondition(line, &extracted) &&
            !TryExtractRenderedWhileCondition(line, &extracted)) {
            return false;
        }

        extracted = StripRenderedWholeParentheses(TrimLuaWhitespace(extracted), nullptr);
        *negated = false;
        if (StartsWithLuaKeyword(extracted, "not")) {
            *negated = true;
            extracted = StripRenderedWholeParentheses(
                TrimLuaWhitespace(std::string_view(extracted).substr(3)),
                nullptr);
        }

        if (extracted.empty()) {
            return false;
        }
        *condition = std::move(extracted);
        return true;
    };

    const auto guarded_block_assigns_target = [&](std::size_t guard_line,
                                                  std::string_view target) {
        if (guard_line >= lines->size() || target.empty()) {
            return false;
        }

        const std::size_t block_end = FindRenderedFunctionBlockEnd(*lines, guard_line);
        if (block_end >= lines->size() || block_end <= guard_line) {
            return false;
        }

        for (std::size_t probe = guard_line + 1; probe < block_end; ++probe) {
            if (IsRenderedShadowingLocalDeclaration((*lines)[probe], target)) {
                return false;
            }

            bool assign_is_local = false;
            std::string lhs;
            std::string rhs;
            if (ParseRenderedSingleAssignmentLine((*lines)[probe],
                                                  &assign_is_local,
                                                  &lhs,
                                                  &rhs) &&
                !assign_is_local &&
                lhs == target) {
                return true;
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
            !assign_is_local || !IsValidLuaIdentifier(assign_lhs)) {
            continue;
        }

        std::string normalized_rhs =
            StripRenderedWholeParentheses(TrimLuaWhitespace(assign_rhs), nullptr);
        if (normalized_rhs.empty()) {
            continue;
        }

        std::string guard_condition;
        bool negated = false;
        if (!parse_guard_header((*lines)[line_index + 1], &guard_condition, &negated) ||
            guard_condition != normalized_rhs) {
            continue;
        }

        if (guarded_block_assigns_target(line_index + 1, assign_lhs)) {
            continue;
        }

        const std::size_t scope_end =
            FindEnclosingRenderedFunctionBlockEnd(*lines, line_index);
        bool used_later = false;
        for (std::size_t probe = line_index + 2;
             probe < lines->size() &&
             (scope_end >= lines->size() || probe <= scope_end);
             ++probe) {
            if (IsRenderedShadowingLocalDeclaration((*lines)[probe], assign_lhs) ||
                IsAssignmentToIdentifier((*lines)[probe], assign_lhs)) {
                break;
            }
            if (CountIdentifierUses((*lines)[probe], assign_lhs) != 0) {
                used_later = true;
                break;
            }
        }
        if (used_later) {
            continue;
        }

        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index));
        changed = true;
        if (line_index != 0) {
            --line_index;
        }
    }

    return changed;
}

bool SimplifyRenderedLuaRepeatedCallResultGuards(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 1 < lines->size(); ++line_index) {
        bool assign_is_local = false;
        std::vector<std::string> lhs_names;
        std::string rhs_expression;
        if (!ParseRenderedAssignmentLine((*lines)[line_index],
                                         &assign_is_local,
                                         &lhs_names,
                                         &rhs_expression) ||
            lhs_names.empty() || !IsValidLuaIdentifier(lhs_names.front())) {
            continue;
        }

        std::string callee;
        std::vector<std::string> arguments;
        if (!ParseRenderedSingleCallLine(rhs_expression, &callee, &arguments)) {
            continue;
        }

        std::string condition;
        if (!TryExtractRenderedIfCondition((*lines)[line_index + 1], &condition)) {
            continue;
        }

        const std::string normalized_rhs =
            NormalizeRenderedConditionOperand(TrimLuaWhitespace(rhs_expression), nullptr);
        const std::string normalized_condition =
            NormalizeRenderedConditionExpression(condition, nullptr);
        if (normalized_rhs.empty() || normalized_condition != ("not " + normalized_rhs)) {
            continue;
        }

        const std::string indent(GetRenderedLuaIndent((*lines)[line_index + 1]));
        (*lines)[line_index + 1] = indent + "if not " + lhs_names.front() + " then";
        changed = true;
    }

    return changed;
}

bool SimplifyRenderedLuaAdjacentDuplicateCallValueConditions(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto try_parse_conditional_header =
        [](std::string_view line, std::string* prefix_out, std::string* condition_out) {
            if (prefix_out == nullptr || condition_out == nullptr) {
                return false;
            }

            const std::string_view stripped = StripLuaLineComment(line);
            if (stripped.size() != line.size()) {
                return false;
            }
            if (stripped.find('\'') != std::string_view::npos ||
                stripped.find('"') != std::string_view::npos) {
                return false;
            }

            const std::string_view trimmed = TrimLuaWhitespace(stripped);
            if (trimmed.starts_with("if ") && trimmed.ends_with(" then")) {
                *prefix_out = "if ";
                *condition_out = std::string(
                    TrimLuaWhitespace(trimmed.substr(3, trimmed.size() - 8)));
                return true;
            }
            if (trimmed.starts_with("elseif ") && trimmed.ends_with(" then")) {
                *prefix_out = "elseif ";
                *condition_out = std::string(
                    TrimLuaWhitespace(trimmed.substr(7, trimmed.size() - 12)));
                return true;
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
                                               &assign_rhs)) {
            continue;
        }

        const std::string normalized_lhs =
            StripRenderedWholeParentheses(TrimLuaWhitespace(assign_lhs), nullptr);
        if (normalized_lhs.empty() || !IsValidLuaIdentifier(normalized_lhs) ||
            IsRenderTemporaryName(normalized_lhs)) {
            continue;
        }

        std::string call_callee;
        std::vector<std::string> call_arguments;
        if (!ParseRenderedSingleCallLine(assign_rhs, &call_callee, &call_arguments)) {
            continue;
        }

        const std::string normalized_rhs =
            StripRenderedWholeParentheses(TrimLuaWhitespace(assign_rhs), nullptr);
        if (normalized_rhs.empty()) {
            continue;
        }

        std::string header_prefix;
        std::string header_condition;
        if (!try_parse_conditional_header((*lines)[line_index + 1],
                                          &header_prefix,
                                          &header_condition)) {
            continue;
        }

        const std::string normalized_condition =
            StripRenderedWholeParentheses(TrimLuaWhitespace(header_condition), nullptr);
        if (normalized_condition != normalized_rhs) {
            continue;
        }

        const std::string indent(GetRenderedLuaIndent((*lines)[line_index + 1]));
        (*lines)[line_index + 1] = indent + header_prefix + normalized_lhs + " then";
        changed = true;
    }

    return changed;
}

bool SimplifyRenderedLuaImmediateLocalCallConditions(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto try_parse_conditional_header =
        [](std::string_view line,
           std::string* prefix_out,
           std::string* condition_out,
           bool* negate_out) {
            if (prefix_out == nullptr || condition_out == nullptr ||
                negate_out == nullptr) {
                return false;
            }

            const std::string_view stripped = StripLuaLineComment(line);
            if (stripped.size() != line.size()) {
                return false;
            }
            const std::string_view trimmed = TrimLuaWhitespace(stripped);
            std::string_view condition_view;
            if (trimmed.starts_with("if ") && trimmed.ends_with(" then")) {
                *prefix_out = "if ";
                condition_view = TrimLuaWhitespace(trimmed.substr(3, trimmed.size() - 8));
            } else if (trimmed.starts_with("elseif ") && trimmed.ends_with(" then")) {
                *prefix_out = "elseif ";
                condition_view = TrimLuaWhitespace(trimmed.substr(7, trimmed.size() - 12));
            } else {
                return false;
            }

            bool negate = false;
            if (condition_view.starts_with("not ")) {
                negate = true;
                condition_view = TrimLuaWhitespace(condition_view.substr(4));
            }
            std::string normalized_condition =
                StripRenderedWholeParentheses(condition_view, nullptr);
            if (normalized_condition.empty()) {
                return false;
            }

            *condition_out = std::move(normalized_condition);
            *negate_out = negate;
            return true;
        };

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 1 < lines->size(); ++line_index) {
        const std::string_view assignment_without_comment =
            StripLuaLineComment((*lines)[line_index]);
        if (assignment_without_comment.size() != (*lines)[line_index].size()) {
            continue;
        }

        bool assign_is_local = false;
        std::string assign_lhs;
        std::string assign_rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[line_index],
                                               &assign_is_local,
                                               &assign_lhs,
                                               &assign_rhs) ||
            !assign_is_local) {
            continue;
        }

        const std::string normalized_lhs =
            StripRenderedWholeParentheses(TrimLuaWhitespace(assign_lhs), nullptr);
        if (normalized_lhs.empty() || !IsValidLuaIdentifier(normalized_lhs) ||
            CountIdentifierUses(assign_rhs, normalized_lhs) != 0) {
            continue;
        }

        std::string call_callee;
        std::vector<std::string> call_arguments;
        if (!ParseRenderedSingleCallLine(assign_rhs, &call_callee, &call_arguments)) {
            continue;
        }

        std::string header_prefix;
        std::string header_condition;
        bool header_negated = false;
        if (!try_parse_conditional_header((*lines)[line_index + 1],
                                          &header_prefix,
                                          &header_condition,
                                          &header_negated) ||
            header_condition != normalized_lhs) {
            continue;
        }

        bool used_after_header = false;
        bool unsafe_later_assignment = false;
        for (std::size_t probe = line_index + 2; probe < lines->size(); ++probe) {
            bool probe_is_local = false;
            std::string probe_lhs;
            std::string probe_rhs;
            if (ParseRenderedSingleAssignmentLine((*lines)[probe],
                                                  &probe_is_local,
                                                  &probe_lhs,
                                                  &probe_rhs) &&
                StripRenderedWholeParentheses(TrimLuaWhitespace(probe_lhs), nullptr) ==
                    normalized_lhs) {
                if (probe_is_local) {
                    break;
                }
                if (assign_is_local || CountIdentifierUses(probe_rhs, normalized_lhs) != 0) {
                    unsafe_later_assignment = true;
                }
                break;
            }

            if (CountIdentifierUses((*lines)[probe], normalized_lhs) != 0) {
                used_after_header = true;
                break;
            }
        }
        if (used_after_header || unsafe_later_assignment) {
            continue;
        }

        const std::string normalized_rhs =
            std::string(TrimLuaWhitespace(assign_rhs));
        if (normalized_rhs.empty()) {
            continue;
        }

        const std::string indent(GetRenderedLuaIndent((*lines)[line_index + 1]));
        (*lines)[line_index + 1] =
            indent + header_prefix +
            (header_negated ? "not " + normalized_rhs : normalized_rhs) + " then";
        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index));
        changed = true;
        if (line_index != 0) {
            --line_index;
        }
    }

    return changed;
}

bool SimplifyRenderedLuaCallReassignmentSelfCompareGuards(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto try_parse_conditional_header =
        [](std::string_view line, std::string* prefix_out, std::string* condition_out) {
            if (prefix_out == nullptr || condition_out == nullptr) {
                return false;
            }

            const std::string_view stripped = StripLuaLineComment(line);
            if (stripped.size() != line.size()) {
                return false;
            }
            const std::string_view trimmed = TrimLuaWhitespace(stripped);
            if (trimmed.starts_with("if ") && trimmed.ends_with(" then")) {
                *prefix_out = "if ";
                *condition_out = std::string(
                    TrimLuaWhitespace(trimmed.substr(3, trimmed.size() - 8)));
                return true;
            }
            if (trimmed.starts_with("elseif ") && trimmed.ends_with(" then")) {
                *prefix_out = "elseif ";
                *condition_out = std::string(
                    TrimLuaWhitespace(trimmed.substr(7, trimmed.size() - 12)));
                return true;
            }
            return false;
        };

    const auto is_identifier_char = [](char ch) {
        const unsigned char uch = static_cast<unsigned char>(ch);
        return std::isalnum(uch) != 0 || ch == '_';
    };

    const auto find_self_compare_range =
        [&](std::string_view condition,
            std::string_view identifier,
            std::size_t* begin_out,
            std::size_t* end_out,
            std::string* op_out) {
            if (begin_out == nullptr || end_out == nullptr || op_out == nullptr ||
                identifier.empty()) {
                return false;
            }

            bool in_single_quote = false;
            bool in_double_quote = false;
            bool escaped = false;
            for (std::size_t index = 0; index + identifier.size() <= condition.size(); ++index) {
                const char ch = condition[index];
                if (escaped) {
                    escaped = false;
                    continue;
                }
                if ((in_single_quote || in_double_quote) && ch == '\\') {
                    escaped = true;
                    continue;
                }
                if (!in_double_quote && ch == '\'') {
                    in_single_quote = !in_single_quote;
                    continue;
                }
                if (!in_single_quote && ch == '"') {
                    in_double_quote = !in_double_quote;
                    continue;
                }
                if (in_single_quote || in_double_quote) {
                    continue;
                }

                if (condition.compare(index, identifier.size(), identifier) != 0) {
                    continue;
                }
                if (index > 0 && is_identifier_char(condition[index - 1])) {
                    continue;
                }
                const std::size_t first_end = index + identifier.size();
                if (first_end < condition.size() &&
                    is_identifier_char(condition[first_end])) {
                    continue;
                }

                std::size_t cursor = first_end;
                while (cursor < condition.size() &&
                       std::isspace(static_cast<unsigned char>(condition[cursor])) != 0) {
                    ++cursor;
                }

                std::string op;
                if (cursor + 1 < condition.size() &&
                    condition.compare(cursor, 2, "==") == 0) {
                    op = "==";
                    cursor += 2;
                } else if (cursor + 1 < condition.size() &&
                           condition.compare(cursor, 2, "~=") == 0) {
                    op = "~=";
                    cursor += 2;
                } else {
                    continue;
                }

                while (cursor < condition.size() &&
                       std::isspace(static_cast<unsigned char>(condition[cursor])) != 0) {
                    ++cursor;
                }
                if (cursor + identifier.size() > condition.size() ||
                    condition.compare(cursor, identifier.size(), identifier) != 0) {
                    continue;
                }
                if (cursor > 0 && is_identifier_char(condition[cursor - 1])) {
                    continue;
                }
                const std::size_t second_end = cursor + identifier.size();
                if (second_end < condition.size() &&
                    is_identifier_char(condition[second_end])) {
                    continue;
                }

                *begin_out = index;
                *end_out = second_end;
                *op_out = std::move(op);
                return true;
            }
            return false;
        };

    const auto find_identifier_token_from =
        [&](std::string_view text,
            std::string_view identifier,
            std::size_t start_index) {
            if (identifier.empty() || start_index >= text.size()) {
                return std::string_view::npos;
            }

            bool in_single_quote = false;
            bool in_double_quote = false;
            bool escaped = false;
            for (std::size_t index = start_index;
                 index + identifier.size() <= text.size();
                 ++index) {
                const char ch = text[index];
                if (escaped) {
                    escaped = false;
                    continue;
                }
                if ((in_single_quote || in_double_quote) && ch == '\\') {
                    escaped = true;
                    continue;
                }
                if (!in_double_quote && ch == '\'') {
                    in_single_quote = !in_single_quote;
                    continue;
                }
                if (!in_single_quote && ch == '"') {
                    in_double_quote = !in_double_quote;
                    continue;
                }
                if (in_single_quote || in_double_quote ||
                    text.compare(index, identifier.size(), identifier) != 0 ||
                    (index > 0 && is_identifier_char(text[index - 1])) ||
                    (index + identifier.size() < text.size() &&
                     is_identifier_char(text[index + identifier.size()]))) {
                    continue;
                }
                return index;
            }
            return std::string_view::npos;
        };

    const auto replace_identifier_token_at =
        [&](std::string_view text,
            std::size_t token_begin,
            std::size_t token_length,
            std::string_view replacement,
            std::string* rewritten_out) {
            if (rewritten_out == nullptr || token_begin == std::string_view::npos ||
                token_begin + token_length > text.size()) {
                return false;
            }
            std::string rewritten;
            rewritten.reserve(text.size() + replacement.size());
            rewritten.append(text.substr(0, token_begin));
            rewritten.append(replacement);
            rewritten.append(text.substr(token_begin + token_length));
            *rewritten_out = std::move(rewritten);
            return true;
        };

    const auto try_parse_simple_equality = [](std::string_view expression,
                                              std::string* lhs_out,
                                              std::string* rhs_out) {
        if (lhs_out == nullptr || rhs_out == nullptr) {
            return false;
        }
        const std::string normalized =
            StripRenderedWholeParentheses(TrimLuaWhitespace(expression), nullptr);
        const std::size_t equal = normalized.find("==");
        if (equal == std::string::npos) {
            return false;
        }
        const std::string lhs = std::string(TrimLuaWhitespace(normalized.substr(0, equal)));
        const std::string rhs = std::string(TrimLuaWhitespace(normalized.substr(equal + 2)));
        if (lhs.empty() || rhs.empty()) {
            return false;
        }
        *lhs_out = lhs;
        *rhs_out = rhs;
        return true;
    };

    const auto try_rewrite_duplicate_state_and_condition =
        [&](std::string_view condition,
            std::string_view previous_identifier,
            std::string_view current_identifier,
            std::string* rewritten_out) {
            if (rewritten_out == nullptr || previous_identifier.empty() ||
                current_identifier.empty()) {
                return false;
            }

            const std::size_t and_pos = condition.find(" and ");
            if (and_pos == std::string_view::npos) {
                return false;
            }
            const std::string_view left_view = TrimLuaWhitespace(condition.substr(0, and_pos));
            const std::string_view right_view =
                TrimLuaWhitespace(condition.substr(and_pos + 5));
            if (left_view.empty() || right_view.empty()) {
                return false;
            }

            std::string left_lhs;
            std::string left_rhs;
            std::string right_lhs;
            std::string right_rhs;
            if (!try_parse_simple_equality(left_view, &left_lhs, &left_rhs) ||
                !try_parse_simple_equality(right_view, &right_lhs, &right_rhs)) {
                return false;
            }
            if (left_lhs != right_lhs || left_rhs == right_rhs) {
                return false;
            }
            if (left_lhs != current_identifier && left_lhs != previous_identifier) {
                return false;
            }

            *rewritten_out = std::string(previous_identifier) + " == " + left_rhs +
                             " and " + std::string(current_identifier) + " == " + right_rhs;
            return true;
        };

    const auto identifier_used_in_lines = [&](std::string_view identifier) {
        for (const std::string& line : *lines) {
            if (CountIdentifierUses(StripLuaLineComment(line), identifier) != 0) {
                return true;
            }
        }
        return false;
    };

    const auto make_fresh_identifier = [&](std::string_view base) {
        std::string candidate = std::string(base) + "_current";
        if (!IsValidLuaIdentifier(candidate)) {
            candidate = "current_" + std::string(base);
        }
        if (!IsValidLuaIdentifier(candidate) || IsRenderTemporaryName(candidate)) {
            candidate = "__refii_current";
        }
        if (!identifier_used_in_lines(candidate)) {
            return candidate;
        }
        for (int suffix = 2; suffix < 1024; ++suffix) {
            std::string numbered = candidate + "_" + std::to_string(suffix);
            if (!identifier_used_in_lines(numbered)) {
                return numbered;
            }
        }
        return candidate + "_next";
    };

    bool changed = false;
    for (std::size_t first_assign = 0; first_assign + 2 < lines->size(); ++first_assign) {
        bool first_is_local = false;
        std::string first_lhs;
        std::string first_rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[first_assign],
                                               &first_is_local,
                                               &first_lhs,
                                               &first_rhs) ||
            !first_is_local) {
            continue;
        }

        const std::string identifier =
            StripRenderedWholeParentheses(TrimLuaWhitespace(first_lhs), nullptr);
        if (identifier.empty() || !IsValidLuaIdentifier(identifier) ||
            IsRenderTemporaryName(identifier)) {
            continue;
        }

        std::string first_callee;
        std::vector<std::string> first_arguments;
        if (!ParseRenderedSingleCallLine(first_rhs, &first_callee, &first_arguments)) {
            continue;
        }
        const std::string normalized_first_rhs =
            StripRenderedWholeParentheses(TrimLuaWhitespace(first_rhs), nullptr);
        if (normalized_first_rhs.empty()) {
            continue;
        }

        const std::size_t scan_limit = std::min(lines->size(), first_assign + 18);
        for (std::size_t second_assign = first_assign + 1;
             second_assign + 1 < scan_limit;
             ++second_assign) {
            bool second_is_local = false;
            std::string second_lhs;
            std::string second_rhs;
            if (!ParseRenderedSingleAssignmentLine((*lines)[second_assign],
                                                   &second_is_local,
                                                   &second_lhs,
                                                   &second_rhs)) {
                continue;
            }
            if (second_is_local) {
                continue;
            }
            const std::string normalized_second_lhs =
                StripRenderedWholeParentheses(TrimLuaWhitespace(second_lhs), nullptr);
            if (normalized_second_lhs != identifier) {
                continue;
            }

            std::string second_callee;
            std::vector<std::string> second_arguments;
            if (!ParseRenderedSingleCallLine(second_rhs, &second_callee, &second_arguments)) {
                continue;
            }
            const std::string normalized_second_rhs =
                StripRenderedWholeParentheses(TrimLuaWhitespace(second_rhs), nullptr);
            if (normalized_second_rhs.empty() ||
                normalized_second_rhs != normalized_first_rhs) {
                continue;
            }

            bool invalidated = false;
            for (std::size_t probe = first_assign + 1; probe < second_assign; ++probe) {
                if (IsRenderedShadowingLocalDeclaration((*lines)[probe], identifier) ||
                    IsAssignmentToIdentifier((*lines)[probe], identifier)) {
                    invalidated = true;
                    break;
                }
            }
            if (invalidated) {
                continue;
            }

            std::string header_prefix;
            std::string header_condition;
            if (!try_parse_conditional_header((*lines)[second_assign + 1],
                                              &header_prefix,
                                              &header_condition)) {
                continue;
            }

            std::size_t compare_begin = 0;
            std::size_t compare_end = 0;
            std::string compare_op;
            if (!find_self_compare_range(header_condition,
                                         identifier,
                                         &compare_begin,
                                         &compare_end,
                                         &compare_op)) {
                continue;
            }

            const std::string fresh_identifier = make_fresh_identifier(identifier);
            const std::string assign_indent(GetRenderedLuaIndent((*lines)[second_assign]));
            (*lines)[second_assign] =
                assign_indent + "local " + fresh_identifier + " = " + second_rhs;

            std::string rewritten_condition;
            rewritten_condition.reserve(header_condition.size() + fresh_identifier.size() + 8);
            rewritten_condition.append(header_condition.substr(0, compare_begin));
            rewritten_condition.append(fresh_identifier);
            rewritten_condition.push_back(' ');
            rewritten_condition.append(compare_op);
            rewritten_condition.push_back(' ');
            rewritten_condition.append(identifier);
            rewritten_condition.append(header_condition.substr(compare_end));
            const std::string header_indent(GetRenderedLuaIndent((*lines)[second_assign + 1]));
            (*lines)[second_assign + 1] =
                header_indent + header_prefix + rewritten_condition + " then";

            const std::size_t follow_limit = std::min(lines->size(), second_assign + 10);
            for (std::size_t probe = second_assign + 2; probe < follow_limit; ++probe) {
                const std::string_view trimmed =
                    TrimLuaWhitespace(StripLuaLineComment((*lines)[probe]));
                if (trimmed == "else" || trimmed.starts_with("elseif ") || trimmed == "end") {
                    break;
                }
                if (trimmed.starts_with("while ") || trimmed.starts_with("for ") ||
                    trimmed.starts_with("repeat")) {
                    break;
                }

                bool probe_is_local = false;
                std::string probe_lhs;
                std::string probe_rhs;
                if (ParseRenderedSingleAssignmentLine((*lines)[probe],
                                                      &probe_is_local,
                                                      &probe_lhs,
                                                      &probe_rhs)) {
                    const std::string normalized_probe_lhs =
                        StripRenderedWholeParentheses(TrimLuaWhitespace(probe_lhs), nullptr);
                    if (normalized_probe_lhs == identifier) {
                        break;
                    }
                }
                if (IsRenderedShadowingLocalDeclaration((*lines)[probe], identifier)) {
                    break;
                }

                std::string probe_header_prefix;
                std::string probe_header_condition;
                if (!try_parse_conditional_header((*lines)[probe],
                                                  &probe_header_prefix,
                                                  &probe_header_condition) ||
                    probe_header_condition.find(" and ") == std::string::npos ||
                    probe_header_condition.find("==") == std::string::npos) {
                    continue;
                }

                if (probe_header_condition.find(fresh_identifier) != std::string::npos) {
                    std::string rewritten_condition;
                    if (try_rewrite_duplicate_state_and_condition(probe_header_condition,
                                                                  identifier,
                                                                  fresh_identifier,
                                                                  &rewritten_condition) &&
                        !rewritten_condition.empty()) {
                        const std::string probe_indent(GetRenderedLuaIndent((*lines)[probe]));
                        (*lines)[probe] =
                            probe_indent + probe_header_prefix + rewritten_condition + " then";
                    }
                    continue;
                }

                const std::size_t first_original =
                    find_identifier_token_from(probe_header_condition, identifier, 0);
                const std::size_t second_original =
                    first_original == std::string_view::npos
                        ? std::string_view::npos
                        : find_identifier_token_from(probe_header_condition,
                                                     identifier,
                                                     first_original + identifier.size());
                if (first_original == std::string_view::npos ||
                    second_original == std::string_view::npos) {
                    continue;
                }

                std::string rewritten_condition;
                if (!replace_identifier_token_at(probe_header_condition,
                                                 second_original,
                                                 identifier.size(),
                                                 fresh_identifier,
                                                 &rewritten_condition) ||
                    rewritten_condition.empty()) {
                    continue;
                }

                const std::string probe_indent(GetRenderedLuaIndent((*lines)[probe]));
                (*lines)[probe] =
                    probe_indent + probe_header_prefix + rewritten_condition + " then";
            }

            changed = true;
            break;
        }
    }

    for (std::size_t current_assign = 1; current_assign + 2 < lines->size(); ++current_assign) {
        bool current_is_local = false;
        std::string current_lhs;
        std::string current_rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[current_assign],
                                               &current_is_local,
                                               &current_lhs,
                                               &current_rhs) ||
            !current_is_local) {
            continue;
        }
        const std::string current_identifier =
            StripRenderedWholeParentheses(TrimLuaWhitespace(current_lhs), nullptr);
        const std::string normalized_current_rhs =
            StripRenderedWholeParentheses(TrimLuaWhitespace(current_rhs), nullptr);
        if (current_identifier.empty() || normalized_current_rhs.empty() ||
            !IsValidLuaIdentifier(current_identifier) || IsRenderTemporaryName(current_identifier)) {
            continue;
        }

        std::string previous_identifier;
        const std::size_t backward_begin =
            current_assign > 4 ? current_assign - 4 : 0;
        for (std::size_t probe = current_assign; probe > backward_begin;) {
            --probe;
            bool probe_is_local = false;
            std::string probe_lhs;
            std::string probe_rhs;
            if (!ParseRenderedSingleAssignmentLine((*lines)[probe],
                                                   &probe_is_local,
                                                   &probe_lhs,
                                                   &probe_rhs) ||
                !probe_is_local) {
                continue;
            }
            const std::string normalized_probe_lhs =
                StripRenderedWholeParentheses(TrimLuaWhitespace(probe_lhs), nullptr);
            const std::string normalized_probe_rhs =
                StripRenderedWholeParentheses(TrimLuaWhitespace(probe_rhs), nullptr);
            if (normalized_probe_lhs.empty() || normalized_probe_lhs == current_identifier ||
                normalized_probe_rhs != normalized_current_rhs ||
                !IsValidLuaIdentifier(normalized_probe_lhs) ||
                IsRenderTemporaryName(normalized_probe_lhs)) {
                continue;
            }
            previous_identifier = normalized_probe_lhs;
            break;
        }
        if (previous_identifier.empty()) {
            continue;
        }

        std::string transition_prefix;
        std::string transition_condition;
        if (!try_parse_conditional_header((*lines)[current_assign + 1],
                                          &transition_prefix,
                                          &transition_condition) ||
            transition_condition.find("==") == std::string::npos ||
            CountIdentifierUses(transition_condition, current_identifier) == 0 ||
            CountIdentifierUses(transition_condition, previous_identifier) == 0) {
            continue;
        }

        const std::size_t follow_limit = std::min(lines->size(), current_assign + 8);
        for (std::size_t probe = current_assign + 2; probe < follow_limit; ++probe) {
            std::string probe_prefix;
            std::string probe_condition;
            if (!try_parse_conditional_header((*lines)[probe], &probe_prefix, &probe_condition) ||
                probe_condition.find(" and ") == std::string::npos ||
                probe_condition.find("==") == std::string::npos ||
                CountIdentifierUses(probe_condition, previous_identifier) < 2 ||
                CountIdentifierUses(probe_condition, current_identifier) != 0) {
                continue;
            }

            const std::size_t and_pos = probe_condition.find(" and ");
            if (and_pos == std::string::npos) {
                continue;
            }
            const std::string left_view = std::string(
                TrimLuaWhitespace(probe_condition.substr(0, and_pos)));
            const std::string right_view = std::string(
                TrimLuaWhitespace(probe_condition.substr(and_pos + 5)));
            std::string left_lhs;
            std::string left_rhs;
            std::string right_lhs;
            std::string right_rhs;
            if (!try_parse_simple_equality(left_view, &left_lhs, &left_rhs) ||
                !try_parse_simple_equality(right_view, &right_lhs, &right_rhs) ||
                left_lhs != previous_identifier ||
                right_lhs != previous_identifier ||
                left_rhs == right_rhs) {
                continue;
            }

            const std::size_t first_token =
                find_identifier_token_from(probe_condition, previous_identifier, 0);
            const std::size_t second_token =
                first_token == std::string_view::npos
                    ? std::string_view::npos
                    : find_identifier_token_from(probe_condition,
                                                 previous_identifier,
                                                 first_token + previous_identifier.size());
            std::string rewritten_condition;
            if (second_token == std::string_view::npos ||
                !replace_identifier_token_at(probe_condition,
                                             second_token,
                                             previous_identifier.size(),
                                             current_identifier,
                                             &rewritten_condition) ||
                rewritten_condition.empty()) {
                continue;
            }

            const std::string indent(GetRenderedLuaIndent((*lines)[probe]));
            (*lines)[probe] = indent + probe_prefix + rewritten_condition + " then";
            changed = true;
            break;
        }
    }

    return changed;
}

}  // namespace refii::lua
