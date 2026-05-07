#include "tools/decompiler/rendered/rendered_lua_text.h"

#include "tools/decompiler/rendered/rendered_cleanup_common_helpers.h"

#include <algorithm>
#include <cstddef>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

namespace refii::lua {
namespace {

bool FindRenderedInlineTableSeedTopLevelEquals(std::string_view entry,
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

bool ParseRenderedInlineTableSeedEntry(std::string_view entry,
                                       std::string* access_suffix,
                                       std::string* rhs_expression) {
    if (access_suffix == nullptr || rhs_expression == nullptr) {
        return false;
    }

    std::size_t equal_pos = std::string_view::npos;
    if (!FindRenderedInlineTableSeedTopLevelEquals(entry, &equal_pos)) {
        return false;
    }

    const std::string_view lhs = TrimLuaWhitespace(entry.substr(0, equal_pos));
    const std::string_view rhs = TrimLuaWhitespace(entry.substr(equal_pos + 1));
    if (lhs.empty() || rhs.empty()) {
        return false;
    }
    if (rhs.starts_with("function(") || rhs.starts_with("function ") || rhs == "{") {
        return false;
    }

    if (IsValidLuaIdentifier(lhs)) {
        *access_suffix = "." + std::string(lhs);
    } else if (lhs.front() == '[' && lhs.back() == ']') {
        *access_suffix = std::string(lhs);
    } else {
        return false;
    }

    *rhs_expression = std::string(rhs);
    return true;
}

bool TryParseRenderedInlineTableSeedEntries(
    std::string_view rhs_expression,
    std::size_t max_entry_count,
    std::vector<std::pair<std::string, std::string>>* entries_out) {
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

    std::vector<std::string> entries;
    if (!SplitRenderedTopLevelCommaChain(inner, &entries) || entries.empty() ||
        entries.size() > max_entry_count) {
        return false;
    }

    std::vector<std::pair<std::string, std::string>> parsed_entries;
    parsed_entries.reserve(entries.size());
    for (const std::string& entry : entries) {
        std::string access_suffix;
        std::string rhs_value;
        if (!ParseRenderedInlineTableSeedEntry(entry, &access_suffix, &rhs_value)) {
            return false;
        }
        parsed_entries.emplace_back(std::move(access_suffix), std::move(rhs_value));
    }

    *entries_out = std::move(parsed_entries);
    return true;
}

}  // namespace

bool ExpandRenderedLuaInlineTopLevelTableSeedFieldsForParity(
    std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    constexpr std::size_t kMaxEntryCount = 4;

    const auto next_nonempty = [&](std::size_t start) -> std::size_t {
        for (std::size_t index = start; index < lines->size(); ++index) {
            if (!TrimLuaWhitespace(StripLuaLineComment((*lines)[index])).empty()) {
                return index;
            }
        }
        return lines->size();
    };

    const auto without_trailing_comma = [](std::string value) {
        const std::size_t last_non_ws = value.find_last_not_of(" \t");
        if (last_non_ws != std::string::npos && value[last_non_ws] == ',') {
            value.erase(last_non_ws, 1);
        }
        return std::string(TrimLuaWhitespace(value));
    };

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        if (FindEnclosingRenderedFunctionBlockBegin(*lines, line_index) != lines->size()) {
            continue;
        }

        std::string multiline_indent;
        std::string multiline_table_name;
        if (ParseRenderedTableLiteralStartLine((*lines)[line_index],
                                               &multiline_indent,
                                               nullptr,
                                               &multiline_table_name) &&
            IsValidLuaIdentifier(multiline_table_name)) {
            const std::size_t field_line = next_nonempty(line_index + 1);
            const std::size_t close_line =
                field_line < lines->size() ? next_nonempty(field_line + 1) : lines->size();
            if (field_line < lines->size() && close_line < lines->size() &&
                TrimLuaWhitespace(StripLuaLineComment((*lines)[close_line])) == "}") {
                const std::string field_entry = without_trailing_comma(
                    std::string(TrimLuaWhitespace(StripLuaLineComment((*lines)[field_line]))));

                std::string access_suffix;
                std::string rhs_expression;
                if (ParseRenderedInlineTableSeedEntry(field_entry,
                                                      &access_suffix,
                                                      &rhs_expression)) {
                    std::vector<std::string> replacement_lines;
                    replacement_lines.reserve(2);
                    replacement_lines.push_back(multiline_indent + multiline_table_name + " = {}");
                    replacement_lines.push_back(multiline_indent + multiline_table_name +
                                                access_suffix + " = " + rhs_expression);

                    lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                                 lines->begin() +
                                     static_cast<std::ptrdiff_t>(close_line + 1));
                    lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                                  replacement_lines.begin(),
                                  replacement_lines.end());
                    changed = true;
                    line_index += replacement_lines.size() - 1;
                    continue;
                }
            }
        }

        bool assign_is_local = false;
        std::string assign_lhs;
        std::string assign_rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[line_index],
                                               &assign_is_local,
                                               &assign_lhs,
                                               &assign_rhs) ||
            assign_is_local || !IsValidLuaIdentifier(assign_lhs)) {
            continue;
        }

        std::vector<std::pair<std::string, std::string>> entries;
        if (!TryParseRenderedInlineTableSeedEntries(assign_rhs, kMaxEntryCount, &entries)) {
            continue;
        }

        std::vector<std::string> replacement_lines;
        replacement_lines.reserve(entries.size() + 1);
        const std::string indent(GetRenderedLuaIndent((*lines)[line_index]));
        replacement_lines.push_back(indent + assign_lhs + " = {}");

        for (const auto& [access_suffix, rhs_expression] : entries) {
            replacement_lines.push_back(indent + assign_lhs + access_suffix + " = " +
                                        rhs_expression);
        }

        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index));
        lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                      replacement_lines.begin(),
                      replacement_lines.end());
        changed = true;
        line_index += replacement_lines.size() - 1;
    }

    return changed;
}

bool ExpandRenderedLuaMultilineTopLevelTableSeedFieldsForParity(
    std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    constexpr std::size_t kMaxEntryCount = 32;

    const auto without_trailing_comma = [](std::string value) {
        const std::size_t last_non_ws = value.find_last_not_of(" \t");
        if (last_non_ws != std::string::npos && value[last_non_ws] == ',') {
            value.erase(last_non_ws, 1);
        }
        return std::string(TrimLuaWhitespace(value));
    };

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        if (FindEnclosingRenderedFunctionBlockBegin(*lines, line_index) != lines->size()) {
            continue;
        }

        std::string indent;
        std::string table_name;
        if (!ParseRenderedTableLiteralStartLine((*lines)[line_index],
                                                &indent,
                                                nullptr,
                                                &table_name) ||
            !IsValidLuaIdentifier(table_name)) {
            continue;
        }

        std::vector<std::pair<std::string, std::string>> entries;
        std::size_t close_line = lines->size();
        bool saw_nested_or_invalid_line = false;
        for (std::size_t scan = line_index + 1; scan < lines->size(); ++scan) {
            if (FindEnclosingRenderedFunctionBlockBegin(*lines, scan) != lines->size()) {
                saw_nested_or_invalid_line = true;
                break;
            }
            const std::string stripped =
                std::string(TrimLuaWhitespace(StripLuaLineComment((*lines)[scan])));
            if (stripped.empty()) {
                continue;
            }
            if (GetRenderedLuaIndent((*lines)[scan]) != indent + "  " &&
                stripped != "}") {
                saw_nested_or_invalid_line = true;
                break;
            }

            if (stripped == "}") {
                close_line = scan;
                break;
            }

            std::string access_suffix;
            std::string rhs_expression;
            if (!ParseRenderedInlineTableSeedEntry(without_trailing_comma(stripped),
                                                   &access_suffix,
                                                   &rhs_expression)) {
                saw_nested_or_invalid_line = true;
                break;
            }
            entries.emplace_back(std::move(access_suffix), std::move(rhs_expression));
            if (entries.size() > kMaxEntryCount) {
                saw_nested_or_invalid_line = true;
                break;
            }
        }

        if (saw_nested_or_invalid_line || close_line == lines->size() || entries.size() < 2) {
            continue;
        }

        std::vector<std::string> replacement_lines;
        replacement_lines.reserve(entries.size() + 1);
        replacement_lines.push_back(indent + table_name + " = {}");
        for (const auto& [access_suffix, rhs_expression] : entries) {
            replacement_lines.push_back(indent + table_name + access_suffix + " = " +
                                        rhs_expression);
        }

        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                     lines->begin() + static_cast<std::ptrdiff_t>(close_line + 1));
        lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                      replacement_lines.begin(),
                      replacement_lines.end());
        changed = true;
        line_index += replacement_lines.size() - 1;
    }

    return changed;
}

bool CollapseRenderedLuaTopLevelDottedEmptyFieldSeedsForParity(
    std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 1 < lines->size(); ++line_index) {
        if (FindEnclosingRenderedFunctionBlockBegin(*lines, line_index) != lines->size()) {
            continue;
        }

        bool assign_is_local = false;
        std::string assign_lhs;
        std::string assign_rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[line_index],
                                               &assign_is_local,
                                               &assign_lhs,
                                               &assign_rhs) ||
            assign_is_local || TrimLuaWhitespace(assign_rhs) != "{}") {
            continue;
        }

        const std::size_t dot = assign_lhs.find('.');
        if (dot == std::string::npos || dot == 0 || dot + 1 >= assign_lhs.size()) {
            continue;
        }
        const std::string table_name = assign_lhs.substr(0, dot);
        const std::string first_field = assign_lhs.substr(dot + 1);
        if (!IsValidLuaIdentifier(table_name) || !IsValidLuaIdentifier(first_field)) {
            continue;
        }

        const std::string indent(GetRenderedLuaIndent((*lines)[line_index]));
        std::vector<std::string> fields;
        std::size_t scan = line_index;
        for (; scan < lines->size(); ++scan) {
            if (FindEnclosingRenderedFunctionBlockBegin(*lines, scan) != lines->size()) {
                break;
            }
            if (GetRenderedLuaIndent((*lines)[scan]) != indent) {
                break;
            }

            bool field_is_local = false;
            std::string field_lhs;
            std::string field_rhs;
            if (!ParseRenderedSingleAssignmentLine((*lines)[scan],
                                                   &field_is_local,
                                                   &field_lhs,
                                                   &field_rhs) ||
                field_is_local || TrimLuaWhitespace(field_rhs) != "{}") {
                break;
            }
            const std::string prefix = table_name + ".";
            if (!field_lhs.starts_with(prefix)) {
                break;
            }
            const std::string field_name = field_lhs.substr(prefix.size());
            if (!IsValidLuaIdentifier(field_name)) {
                break;
            }
            fields.push_back(field_name);
            if (fields.size() >= 16) {
                ++scan;
                break;
            }
        }

        if (fields.size() < 2) {
            continue;
        }

        std::string literal = indent + table_name + " = {";
        for (std::size_t field_index = 0; field_index < fields.size(); ++field_index) {
            if (field_index != 0) {
                literal += ", ";
            }
            literal += fields[field_index] + " = {}";
        }
        literal += "}";

        (*lines)[line_index] = std::move(literal);
        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index + 1),
                     lines->begin() + static_cast<std::ptrdiff_t>(scan));
        changed = true;
    }

    return changed;
}

bool ExpandRenderedLuaOrTableFallbackAssignmentsForParity(
    std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        bool assign_is_local = false;
        std::string assign_lhs;
        std::string assign_rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[line_index],
                                               &assign_is_local,
                                               &assign_lhs,
                                               &assign_rhs) ||
            assign_is_local) {
            continue;
        }

        const std::string normalized_lhs =
            StripRenderedWholeParentheses(TrimLuaWhitespace(assign_lhs), nullptr);
        if (normalized_lhs.empty() || !IsRenderedSafePrefixExpression(normalized_lhs)) {
            continue;
        }

        std::vector<std::string> rhs_parts;
        if (!SplitRenderedTopLevelBooleanChain(
                StripRenderedWholeParentheses(TrimLuaWhitespace(assign_rhs), nullptr),
                "or",
                &rhs_parts) ||
            rhs_parts.size() != 2) {
            continue;
        }

        const std::string fallback_lhs =
            StripRenderedWholeParentheses(TrimLuaWhitespace(rhs_parts[0]), nullptr);
        const std::string fallback_rhs =
            StripRenderedWholeParentheses(TrimLuaWhitespace(rhs_parts[1]), nullptr);
        if (fallback_lhs != normalized_lhs || fallback_rhs != "{}") {
            continue;
        }

        const std::string indent(GetRenderedLuaIndent((*lines)[line_index]));
        std::vector<std::string> replacement_lines = {
            indent + "if not " + normalized_lhs + " then",
            indent + "  " + normalized_lhs + " = {}",
            indent + "end",
        };
        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index));
        lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                      replacement_lines.begin(),
                      replacement_lines.end());
        changed = true;
        line_index += replacement_lines.size() - 1;
    }

    return changed;
}

bool ExpandRenderedLuaInlineLocalTableSeedFieldsForParity(
    std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    constexpr std::size_t kMaxEntryCount = 4;

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        if (FindEnclosingRenderedFunctionBlockBegin(*lines, line_index) == lines->size()) {
            continue;
        }

        bool assign_is_local = false;
        std::string assign_lhs;
        std::string assign_rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[line_index],
                                               &assign_is_local,
                                               &assign_lhs,
                                               &assign_rhs) ||
            !assign_is_local || !IsValidLuaIdentifier(assign_lhs) ||
            IsRenderTemporaryName(assign_lhs)) {
            continue;
        }

        std::vector<std::pair<std::string, std::string>> entries;
        if (!TryParseRenderedInlineTableSeedEntries(assign_rhs, kMaxEntryCount, &entries)) {
            continue;
        }

        const std::string indent(GetRenderedLuaIndent((*lines)[line_index]));
        std::vector<std::string> replacement_lines;
        replacement_lines.reserve(entries.size() + 1);
        replacement_lines.push_back(indent + "local " + assign_lhs + " = {}");
        for (const auto& [access_suffix, rhs_expression] : entries) {
            replacement_lines.push_back(indent + assign_lhs + access_suffix + " = " +
                                        rhs_expression);
        }

        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index));
        lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                      replacement_lines.begin(),
                      replacement_lines.end());
        changed = true;
        line_index += replacement_lines.size() - 1;
    }

    return changed;
}

bool ExpandRenderedLuaSingletonTopLevelTableLiterals(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    constexpr std::size_t kMaxExpandedSeedEntryCount = 4;

    const auto next_nonempty = [&](std::size_t start) -> std::size_t {
        for (std::size_t i = start; i < lines->size(); ++i) {
            if (!TrimLuaWhitespace((*lines)[i]).empty()) {
                return i;
            }
        }
        return lines->size();
    };

    const auto trim_trailing_comma = [](std::string line) {
        const std::size_t last_non_ws = line.find_last_not_of(" \t");
        if (last_non_ws != std::string::npos && line[last_non_ws] == ',') {
            line.erase(last_non_ws, 1);
        }
        return line;
    };

    bool changed = false;
    const char* const kStepName = __func__;
    RunRenderedLuaCleanupPassLoop(kStepName, 32, [&]() {
        bool pass_changed = false;
        for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
            std::string indent;
            std::string lhs_prefix;
            std::string table_name;
            if (!ParseRenderedTableLiteralStartLine((*lines)[line_index],
                                                    &indent,
                                                    &lhs_prefix,
                                                    &table_name) ||
                !IsValidLuaIdentifier(table_name)) {
                continue;
            }

            const std::size_t table_end = FindRenderedTableLiteralEnd(*lines, line_index);
            if (table_end >= lines->size() || table_end <= line_index + 1) {
                continue;
            }

            bool has_later_assignment = false;
            std::size_t later_assignment_count = 0;
            for (std::size_t probe = table_end + 1; probe < lines->size(); ++probe) {
                if (GetRenderedLuaIndent((*lines)[probe]) != indent) {
                    continue;
                }
                std::string later_field_key;
                std::string later_rhs_expression;
                bool later_rhs_is_function = false;
                int later_array_index = -1;
                if (!ParseRenderedTableFieldAssignment((*lines)[probe],
                                                       table_name,
                                                       &later_field_key,
                                                       &later_rhs_expression,
                                                       &later_rhs_is_function,
                                                       &later_array_index)) {
                    continue;
                }
                has_later_assignment = true;
                ++later_assignment_count;
            }
            if (!has_later_assignment) {
                continue;
            }

            std::vector<std::string> replacement_lines;
            replacement_lines.push_back(lhs_prefix + "{}");

            std::size_t cursor = next_nonempty(line_index + 1);
            std::size_t entry_count = 0;
            bool failed = false;
            while (cursor < table_end) {
                std::string_view entry_trimmed = TrimLuaWhitespace((*lines)[cursor]);
                const std::size_t equal = entry_trimmed.find('=');
                if (equal == std::string_view::npos) {
                    failed = true;
                    break;
                }

                const std::string_view lhs =
                    TrimLuaWhitespace(entry_trimmed.substr(0, equal));
                const std::string_view rhs =
                    TrimLuaWhitespace(entry_trimmed.substr(equal + 1));
                if (lhs.empty() || rhs.empty()) {
                    failed = true;
                    break;
                }

                std::string access_suffix;
                if (IsValidLuaIdentifier(lhs)) {
                    access_suffix = "." + std::string(lhs);
                } else if (lhs.front() == '[') {
                    access_suffix = std::string(lhs);
                } else {
                    failed = true;
                    break;
                }

                std::size_t entry_end = cursor;
                if (rhs == "{") {
                    entry_end = FindRenderedTableLiteralEnd(*lines, cursor);
                } else if (rhs.starts_with("function(") || rhs.starts_with("function ")) {
                    entry_end = FindRenderedFunctionBlockEnd(*lines, cursor);
                }

                if (entry_end >= table_end) {
                    failed = true;
                    break;
                }

                replacement_lines.push_back(trim_trailing_comma(
                    indent + table_name + access_suffix + " = " + std::string(rhs)));
                for (std::size_t body_line = cursor + 1; body_line <= entry_end; ++body_line) {
                    std::string moved_line = (*lines)[body_line];
                    if (body_line == entry_end) {
                        moved_line = trim_trailing_comma(std::move(moved_line));
                    }
                    replacement_lines.push_back(std::move(moved_line));
                }

                ++entry_count;
                cursor = next_nonempty(entry_end + 1);
            }

            if (failed || entry_count == 0 || entry_count > kMaxExpandedSeedEntryCount ||
                later_assignment_count < entry_count) {
                continue;
            }

            lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                         lines->begin() + static_cast<std::ptrdiff_t>(table_end + 1));
            lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                          replacement_lines.begin(),
                          replacement_lines.end());
            changed = true;
            pass_changed = true;
            break;
        }
        return pass_changed;
    });

    return changed;
}

}  // namespace refii::lua
