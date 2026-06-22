#include "tools/decompiler/rendered/rendered_cleanup_common_helpers.h"

#include "tools/decompiler/rendered/rendered_lua_text.h"

#include <algorithm>
#include <array>
#include <cctype>
#include <utility>

namespace refii::lua {

std::vector<std::string> SplitRenderedLines(std::string_view text) {
    std::vector<std::string> lines;
    std::size_t start = 0;
    while (start < text.size()) {
        const std::size_t end = text.find('\n', start);
        if (end == std::string_view::npos) {
            lines.emplace_back(text.substr(start));
            break;
        }
        lines.emplace_back(text.substr(start, end - start));
        start = end + 1;
    }
    if (text.empty()) {
        lines.emplace_back();
    }
    return lines;
}

std::string_view TrimLuaWhitespace(std::string_view value) {
    while (!value.empty() &&
           std::isspace(static_cast<unsigned char>(value.front())) != 0) {
        value.remove_prefix(1);
    }
    while (!value.empty() &&
           std::isspace(static_cast<unsigned char>(value.back())) != 0) {
        value.remove_suffix(1);
    }
    return value;
}

std::string_view StripLuaLineComment(std::string_view line) {
    bool in_single_quote = false;
    bool in_double_quote = false;
    for (std::size_t i = 0; i < line.size(); ++i) {
        const char ch = line[i];
        if (in_single_quote) {
            if (ch == '\\' && i + 1 < line.size()) {
                ++i;
            } else if (ch == '\'') {
                in_single_quote = false;
            }
            continue;
        }
        if (in_double_quote) {
            if (ch == '\\' && i + 1 < line.size()) {
                ++i;
            } else if (ch == '"') {
                in_double_quote = false;
            }
            continue;
        }
        if (ch == '\'') {
            in_single_quote = true;
            continue;
        }
        if (ch == '"') {
            in_double_quote = true;
            continue;
        }
        if (ch == '-' && i + 1 < line.size() && line[i + 1] == '-') {
            return line.substr(0, i);
        }
    }
    return line;
}

std::string_view GetRenderedLuaIndent(std::string_view line) {
    const std::size_t first_non_ws = line.find_first_not_of(" \t");
    if (first_non_ws == std::string_view::npos) {
        return line;
    }
    return line.substr(0, first_non_ws);
}

bool IsAssignmentToIdentifier(std::string_view line, std::string_view identifier) {
    std::string_view trimmed = TrimLuaWhitespace(line);
    if (trimmed.substr(0, 6) == "local ") {
        trimmed = TrimLuaWhitespace(trimmed.substr(6));
    }

    const std::size_t equal = trimmed.find('=');
    if (equal == std::string_view::npos) {
        return false;
    }

    std::string_view lhs = TrimLuaWhitespace(trimmed.substr(0, equal));
    std::size_t cursor = 0;
    while (cursor < lhs.size()) {
        const std::size_t comma = lhs.find(',', cursor);
        const std::string_view part = TrimLuaWhitespace(
            comma == std::string_view::npos ? lhs.substr(cursor)
                                            : lhs.substr(cursor, comma - cursor));
        if (part == identifier) {
            return true;
        }
        if (comma == std::string_view::npos) {
            break;
        }
        cursor = comma + 1;
    }
    return false;
}

bool ParseRenderedSingleAssignmentLine(std::string_view line,
                                       bool* is_local,
                                       std::string* lhs,
                                       std::string* rhs) {
    if (lhs == nullptr || rhs == nullptr) {
        return false;
    }

    std::string_view trimmed = TrimLuaWhitespace(StripLuaLineComment(line));
    if (trimmed.empty()) {
        return false;
    }

    bool local_assignment = false;
    if (trimmed.starts_with("local ")) {
        local_assignment = true;
        trimmed = TrimLuaWhitespace(trimmed.substr(6));
    }

    const std::size_t equal = trimmed.find('=');
    if (equal == std::string_view::npos) {
        return false;
    }

    const std::string_view lhs_view = TrimLuaWhitespace(trimmed.substr(0, equal));
    const std::string_view rhs_view = TrimLuaWhitespace(trimmed.substr(equal + 1));
    if (lhs_view.empty() || rhs_view.empty() ||
        lhs_view.find(',') != std::string_view::npos) {
        return false;
    }

    if (!IsRenderedSafePrefixExpression(lhs_view)) {
        return false;
    }

    if (is_local != nullptr) {
        *is_local = local_assignment;
    }
    *lhs = std::string(lhs_view);
    *rhs = std::string(rhs_view);
    return true;
}

bool ParseRenderedLocalDeclarationLine(std::string_view line,
                                       std::vector<std::string>* names) {
    if (names == nullptr) {
        return false;
    }

    std::string_view trimmed = TrimLuaWhitespace(StripLuaLineComment(line));
    if (!trimmed.starts_with("local ")) {
        return false;
    }
    trimmed = TrimLuaWhitespace(trimmed.substr(6));
    if (trimmed.empty() || trimmed.find('=') != std::string_view::npos) {
        return false;
    }

    names->clear();
    std::size_t cursor = 0;
    while (cursor < trimmed.size()) {
        const std::size_t comma = trimmed.find(',', cursor);
        const std::string_view part = TrimLuaWhitespace(
            comma == std::string_view::npos ? trimmed.substr(cursor)
                                            : trimmed.substr(cursor, comma - cursor));
        if (part.empty() || !IsValidLuaIdentifier(part)) {
            names->clear();
            return false;
        }
        names->emplace_back(part);
        if (comma == std::string_view::npos) {
            break;
        }
        cursor = comma + 1;
    }

    return !names->empty();
}

bool ParseRenderedAssignmentLineAllowFunctionPlaceholders(
    std::string_view line,
    bool* is_local,
    std::vector<std::string>* lhs_names,
    std::string* rhs_expression) {
    if (lhs_names == nullptr || rhs_expression == nullptr) {
        return false;
    }

    std::string_view trimmed = TrimLuaWhitespace(line);
    if (trimmed.empty()) {
        return false;
    }

    if (is_local != nullptr) {
        *is_local = false;
    }
    if (trimmed.starts_with("local ")) {
        if (is_local != nullptr) {
            *is_local = true;
        }
        trimmed = TrimLuaWhitespace(trimmed.substr(6));
    }

    const std::size_t equal = trimmed.find('=');
    if (equal == std::string_view::npos) {
        return false;
    }

    const std::string_view lhs = TrimLuaWhitespace(trimmed.substr(0, equal));
    const std::string_view rhs = TrimLuaWhitespace(trimmed.substr(equal + 1));
    if (lhs.empty() || rhs.empty()) {
        return false;
    }

    std::vector<std::string> parsed_names;
    std::size_t cursor = 0;
    while (cursor < lhs.size()) {
        const std::size_t comma = lhs.find(',', cursor);
        const std::string_view part = TrimLuaWhitespace(
            comma == std::string_view::npos ? lhs.substr(cursor)
                                            : lhs.substr(cursor, comma - cursor));
        if (part.empty()) {
            return false;
        }
        if (!IsValidLuaIdentifier(part) && part != "function()" && part != "function ()") {
            return false;
        }
        parsed_names.emplace_back(part);
        if (comma == std::string_view::npos) {
            break;
        }
        cursor = comma + 1;
    }

    if (parsed_names.empty()) {
        return false;
    }

    *lhs_names = std::move(parsed_names);
    *rhs_expression = std::string(rhs);
    return true;
}

bool IsRenderedShadowingLocalDeclaration(std::string_view line, std::string_view identifier) {
    if (identifier.empty()) {
        return false;
    }

    std::vector<std::string> names;
    if (ParseRenderedLocalDeclarationLine(line, &names)) {
        return std::find(names.begin(), names.end(), identifier) != names.end();
    }

    std::string_view trimmed = TrimLuaWhitespace(StripLuaLineComment(line));
    if (!trimmed.starts_with("local function ")) {
        return false;
    }

    trimmed = TrimLuaWhitespace(trimmed.substr(15));
    const std::size_t open = trimmed.find('(');
    if (open == std::string_view::npos) {
        return false;
    }

    const std::string_view name = TrimLuaWhitespace(trimmed.substr(0, open));
    return name == identifier;
}

void SeedRenderedFunctionParameters(std::string_view line,
                                    std::unordered_set<std::string>* seen_names) {
    if (seen_names == nullptr) {
        return;
    }

    const std::string_view trimmed = TrimLuaWhitespace(StripLuaLineComment(line));
    const std::size_t function_pos = trimmed.find("function(");
    if (function_pos == std::string_view::npos) {
        return;
    }

    const std::size_t params_begin = function_pos + 9;
    const std::size_t params_end = trimmed.find(')', params_begin);
    if (params_end == std::string_view::npos || params_end <= params_begin) {
        return;
    }

    const std::string_view params = trimmed.substr(params_begin, params_end - params_begin);
    std::size_t cursor = 0;
    while (cursor < params.size()) {
        const std::size_t comma = params.find(',', cursor);
        const std::string_view part = TrimLuaWhitespace(
            comma == std::string_view::npos ? params.substr(cursor)
                                            : params.substr(cursor, comma - cursor));
        if (!part.empty() && part != "..." && IsValidLuaIdentifier(part)) {
            seen_names->insert(std::string(part));
        }
        if (comma == std::string_view::npos) {
            break;
        }
        cursor = comma + 1;
    }
}

bool RenderedLineBeginsScopedBlock(std::string_view trimmed) {
    trimmed = TrimLuaWhitespace(StripLuaLineComment(trimmed));
    return (trimmed.starts_with("if ") && trimmed.ends_with(" then")) ||
           (trimmed.starts_with("for ") && trimmed.ends_with(" do")) ||
           (trimmed.starts_with("while ") && trimmed.ends_with(" do")) ||
           trimmed == "do" ||
           trimmed == "repeat" ||
           trimmed.starts_with("function ") ||
           trimmed.starts_with("local function ");
}

bool RenderedLineBeginsLoopBlock(std::string_view trimmed) {
    trimmed = TrimLuaWhitespace(StripLuaLineComment(trimmed));
    return (trimmed.starts_with("for ") && trimmed.ends_with(" do")) ||
           (trimmed.starts_with("while ") && trimmed.ends_with(" do")) ||
           trimmed == "repeat";
}

void ComputeRenderedScopePaths(const std::vector<std::string>& lines,
                               std::vector<std::vector<std::size_t>>* scope_paths) {
    if (scope_paths == nullptr) {
        return;
    }

    scope_paths->assign(lines.size(), {});
    std::vector<std::size_t> scope_stack;
    for (std::size_t line_index = 0; line_index < lines.size(); ++line_index) {
        (*scope_paths)[line_index] = scope_stack;
        const std::string_view trimmed =
            TrimLuaWhitespace(StripLuaLineComment(lines[line_index]));
        if (trimmed.empty()) {
            continue;
        }
        if (trimmed == "end" || trimmed.starts_with("until ")) {
            if (!scope_stack.empty()) {
                scope_stack.pop_back();
            }
            continue;
        }
        if (trimmed == "else" || trimmed.starts_with("elseif ")) {
            if (!scope_stack.empty()) {
                scope_stack.back() = line_index;
            }
            continue;
        }
        if (RenderedLineBeginsScopedBlock(trimmed)) {
            scope_stack.push_back(line_index);
        }
    }
}

std::size_t FindEnclosingRenderedFunctionBlockBegin(const std::vector<std::string>& lines,
                                                    std::size_t line_index) {
    std::size_t best_start = lines.size();
    const std::size_t limit = std::min(line_index, lines.size());
    for (std::size_t candidate = 0; candidate < limit; ++candidate) {
        if (!IsRenderedFunctionStartLine(lines[candidate])) {
            continue;
        }

        const std::size_t function_end = FindRenderedFunctionBlockEnd(lines, candidate);
        if (function_end >= lines.size() || function_end < line_index) {
            continue;
        }

        if (best_start == lines.size() || candidate > best_start) {
            best_start = candidate;
        }
    }

    return best_start;
}

bool ShouldEmitLocalForRenderedIdentifierTarget(const std::vector<std::string>& lines,
                                                std::size_t line_index,
                                                std::string_view target_expression) {
    if (!IsValidLuaIdentifier(target_expression)) {
        return false;
    }

    const std::size_t function_begin =
        FindEnclosingRenderedFunctionBlockBegin(lines, line_index);
    std::size_t scan_begin = 0;
    if (function_begin != lines.size()) {
        scan_begin = function_begin;
        std::unordered_set<std::string> function_parameters;
        SeedRenderedFunctionParameters(lines[function_begin], &function_parameters);
        if (function_parameters.find(std::string(target_expression)) != function_parameters.end()) {
            return false;
        }
        ++scan_begin;
    }

    for (std::size_t probe = scan_begin; probe < line_index && probe < lines.size(); ++probe) {
        if (IsRenderedShadowingLocalDeclaration(lines[probe], target_expression)) {
            return false;
        }
        if (IsAssignmentToIdentifier(lines[probe], target_expression)) {
            return false;
        }
    }

    if (function_begin != lines.size()) {
        const std::size_t outer_function_begin =
            FindEnclosingRenderedFunctionBlockBegin(lines, function_begin);
        if (outer_function_begin != lines.size() && outer_function_begin < function_begin) {
            for (std::size_t probe = outer_function_begin + 1;
                 probe < function_begin && probe < lines.size();
                 ++probe) {
                if (IsRenderedFunctionStartLine(lines[probe])) {
                    const std::size_t nested_end = FindRenderedFunctionBlockEnd(lines, probe);
                    if (nested_end >= lines.size()) {
                        return false;
                    }
                    if (nested_end < function_begin) {
                        probe = nested_end;
                        continue;
                    }
                }

                if (IsRenderedShadowingLocalDeclaration(lines[probe], target_expression) ||
                    IsAssignmentToIdentifier(lines[probe], target_expression)) {
                    return false;
                }
            }
        }
    }

    return true;
}

bool LooksLikeRenderedNumericLiteral(std::string_view value) {
    value = TrimLuaWhitespace(value);
    if (value.empty()) {
        return false;
    }

    std::size_t index = 0;
    if (value[index] == '+' || value[index] == '-') {
        ++index;
    }
    bool saw_digit = false;
    bool saw_decimal = false;
    bool saw_exponent = false;
    for (; index < value.size(); ++index) {
        const char ch = value[index];
        if (std::isdigit(static_cast<unsigned char>(ch)) != 0) {
            saw_digit = true;
            continue;
        }
        if (ch == '.' && !saw_decimal && !saw_exponent) {
            saw_decimal = true;
            continue;
        }
        if ((ch == 'e' || ch == 'E') && saw_digit && !saw_exponent) {
            saw_exponent = true;
            saw_digit = false;
            if (index + 1 < value.size() && (value[index + 1] == '+' || value[index + 1] == '-')) {
                ++index;
            }
            continue;
        }
        return false;
    }
    return saw_digit;
}

bool FindRenderedMatchingParen(std::string_view text,
                               std::size_t open_index,
                               std::size_t* close_index) {
    if (close_index == nullptr || open_index >= text.size() || text[open_index] != '(') {
        return false;
    }

    int depth = 0;
    bool in_single_quote = false;
    bool in_double_quote = false;
    for (std::size_t i = open_index; i < text.size(); ++i) {
        const char ch = text[i];
        if (in_single_quote) {
            if (ch == '\\' && i + 1 < text.size()) {
                ++i;
            } else if (ch == '\'') {
                in_single_quote = false;
            }
            continue;
        }
        if (in_double_quote) {
            if (ch == '\\' && i + 1 < text.size()) {
                ++i;
            } else if (ch == '"') {
                in_double_quote = false;
            }
            continue;
        }
        if (ch == '\'') {
            in_single_quote = true;
            continue;
        }
        if (ch == '"') {
            in_double_quote = true;
            continue;
        }
        if (ch == '(') {
            ++depth;
        } else if (ch == ')') {
            --depth;
            if (depth == 0) {
                *close_index = i;
                return true;
            }
        }
    }
    return false;
}

namespace {

bool HasRenderedTruthyOperator(std::string_view value) {
    bool in_single_quote = false;
    bool in_double_quote = false;
    int paren_depth = 0;
    int brace_depth = 0;
    for (std::size_t i = 0; i < value.size(); ++i) {
        const char ch = value[i];
        if (in_single_quote) {
            if (ch == '\\' && i + 1 < value.size()) {
                ++i;
            } else if (ch == '\'') {
                in_single_quote = false;
            }
            continue;
        }
        if (in_double_quote) {
            if (ch == '\\' && i + 1 < value.size()) {
                ++i;
            } else if (ch == '"') {
                in_double_quote = false;
            }
            continue;
        }
        if (ch == '\'') {
            in_single_quote = true;
            continue;
        }
        if (ch == '"') {
            in_double_quote = true;
            continue;
        }
        if (ch == '(') {
            ++paren_depth;
            continue;
        }
        if (ch == ')') {
            --paren_depth;
            continue;
        }
        if (ch == '{') {
            ++brace_depth;
            continue;
        }
        if (ch == '}') {
            --brace_depth;
            continue;
        }
        if (paren_depth != 0 || brace_depth != 0) {
            continue;
        }
        if (ch == '.' && i + 1 < value.size() && value[i + 1] == '.') {
            return true;
        }
        if (ch == '+' || ch == '*' || ch == '/' || ch == '%' || ch == '^' || ch == '#') {
            return true;
        }
        if (ch == '-') {
            if (i == 0) {
                return true;
            }
            const char previous = value[i - 1];
            if (previous != 'e' && previous != 'E') {
                return true;
            }
        }
    }
    return false;
}

bool IsRenderedWholeParenthesized(std::string_view value) {
    value = TrimLuaWhitespace(value);
    if (value.size() < 2 || value.front() != '(' || value.back() != ')') {
        return false;
    }
    std::size_t close = std::string_view::npos;
    return FindRenderedMatchingParen(value, 0, &close) && close + 1 == value.size();
}

}  // namespace

std::string StripRenderedWholeParentheses(std::string_view value, bool* changed) {
    value = TrimLuaWhitespace(value);
    while (IsRenderedWholeParenthesized(value)) {
        value = TrimLuaWhitespace(value.substr(1, value.size() - 2));
        if (changed != nullptr) {
            *changed = true;
        }
    }
    return std::string(value);
}

bool SplitRenderedTopLevelBooleanChain(std::string_view value,
                                       std::string_view keyword,
                                       std::vector<std::string>* parts) {
    if (parts == nullptr) {
        return false;
    }

    value = TrimLuaWhitespace(value);
    parts->clear();

    bool in_single_quote = false;
    bool in_double_quote = false;
    int paren_depth = 0;
    int brace_depth = 0;
    int bracket_depth = 0;
    std::size_t part_begin = 0;
    bool found = false;
    for (std::size_t i = 0; i < value.size(); ++i) {
        const char ch = value[i];
        if (in_single_quote) {
            if (ch == '\\' && i + 1 < value.size()) {
                ++i;
            } else if (ch == '\'') {
                in_single_quote = false;
            }
            continue;
        }
        if (in_double_quote) {
            if (ch == '\\' && i + 1 < value.size()) {
                ++i;
            } else if (ch == '"') {
                in_double_quote = false;
            }
            continue;
        }
        if (ch == '\'') {
            in_single_quote = true;
            continue;
        }
        if (ch == '"') {
            in_double_quote = true;
            continue;
        }
        if (ch == '(') {
            ++paren_depth;
            continue;
        }
        if (ch == ')') {
            --paren_depth;
            continue;
        }
        if (ch == '{') {
            ++brace_depth;
            continue;
        }
        if (ch == '}') {
            --brace_depth;
            continue;
        }
        if (ch == '[') {
            ++bracket_depth;
            continue;
        }
        if (ch == ']') {
            --bracket_depth;
            continue;
        }
        if (paren_depth != 0 || brace_depth != 0 || bracket_depth != 0) {
            continue;
        }

        if (i + keyword.size() <= value.size() &&
            value.substr(i, keyword.size()) == keyword) {
            const bool boundary_before =
                i == 0 || std::isspace(static_cast<unsigned char>(value[i - 1])) != 0;
            const std::size_t after = i + keyword.size();
            const bool boundary_after =
                after >= value.size() ||
                std::isspace(static_cast<unsigned char>(value[after])) != 0;
            if (boundary_before && boundary_after) {
                parts->push_back(
                    std::string(TrimLuaWhitespace(value.substr(part_begin, i - part_begin))));
                part_begin = after;
                found = true;
                i = after;
            }
        }
    }

    if (!found) {
        parts->clear();
        return false;
    }

    parts->push_back(std::string(TrimLuaWhitespace(value.substr(part_begin))));
    return std::all_of(parts->begin(), parts->end(), [](const std::string& part) {
        return !part.empty();
    });
}

bool IsRenderedSafePrefixExpression(std::string_view candidate) {
    candidate = TrimLuaWhitespace(candidate);
    const auto is_identifier_start = [](char ch) {
        const unsigned char uch = static_cast<unsigned char>(ch);
        return std::isalpha(uch) != 0 || ch == '_';
    };
    const auto is_identifier_char = [&](char ch) {
        const unsigned char uch = static_cast<unsigned char>(ch);
        return std::isalnum(uch) != 0 || ch == '_';
    };

    if (candidate.empty() || IsLuaKeyword(candidate) ||
        !is_identifier_start(candidate.front())) {
        return false;
    }

    auto skip_identifier = [&](std::size_t index) {
        while (index < candidate.size() && is_identifier_char(candidate[index])) {
            ++index;
        }
        return index;
    };

    auto skip_balanced = [&](std::size_t index, char open, char close) {
        if (index >= candidate.size() || candidate[index] != open) {
            return std::string_view::npos;
        }

        int depth = 0;
        bool in_single_quote = false;
        bool in_double_quote = false;
        for (; index < candidate.size(); ++index) {
            const char ch = candidate[index];
            if (in_single_quote) {
                if (ch == '\\' && index + 1 < candidate.size()) {
                    ++index;
                } else if (ch == '\'') {
                    in_single_quote = false;
                }
                continue;
            }
            if (in_double_quote) {
                if (ch == '\\' && index + 1 < candidate.size()) {
                    ++index;
                } else if (ch == '"') {
                    in_double_quote = false;
                }
                continue;
            }
            if (ch == '\'') {
                in_single_quote = true;
                continue;
            }
            if (ch == '"') {
                in_double_quote = true;
                continue;
            }
            if (ch == open) {
                ++depth;
            } else if (ch == close) {
                --depth;
                if (depth == 0) {
                    return index + 1;
                }
            }
        }

        return std::string_view::npos;
    };

    std::size_t index = skip_identifier(0);
    if (!IsValidLuaIdentifier(candidate.substr(0, index))) {
        return false;
    }
    while (index < candidate.size()) {
        const char ch = candidate[index];
        if (ch == '.') {
            ++index;
            if (index >= candidate.size() || !is_identifier_start(candidate[index])) {
                return false;
            }
            const std::size_t member_begin = index;
            index = skip_identifier(index);
            if (!IsValidLuaIdentifier(candidate.substr(member_begin, index - member_begin))) {
                return false;
            }
            continue;
        }
        if (ch == ':') {
            ++index;
            if (index >= candidate.size() || !is_identifier_start(candidate[index])) {
                return false;
            }
            const std::size_t member_begin = index;
            index = skip_identifier(index);
            if (!IsValidLuaIdentifier(candidate.substr(member_begin, index - member_begin))) {
                return false;
            }
            continue;
        }
        if (ch == '[') {
            index = skip_balanced(index, '[', ']');
            if (index == std::string_view::npos) {
                return false;
            }
            continue;
        }
        if (ch == '(') {
            index = skip_balanced(index, '(', ')');
            if (index == std::string_view::npos) {
                return false;
            }
            continue;
        }
        if (std::isspace(static_cast<unsigned char>(ch)) != 0) {
            ++index;
            continue;
        }
        return false;
    }

    return true;
}

bool IsRenderedSimpleMemberPath(std::string_view candidate) {
    candidate = TrimLuaWhitespace(candidate);
    if (!IsRenderedSafePrefixExpression(candidate) ||
        candidate.find('[') != std::string_view::npos ||
        candidate.find('(') != std::string_view::npos ||
        candidate.find(':') != std::string_view::npos) {
        return false;
    }

    const std::size_t dot = candidate.find('.');
    if (dot == std::string_view::npos) {
        return false;
    }

    std::size_t cursor = 0;
    while (cursor < candidate.size()) {
        const std::size_t next_dot = candidate.find('.', cursor);
        const std::string_view part =
            next_dot == std::string_view::npos
                ? candidate.substr(cursor)
                : candidate.substr(cursor, next_dot - cursor);
        if (!IsValidLuaIdentifier(part)) {
            return false;
        }
        if (next_dot == std::string_view::npos) {
            break;
        }
        cursor = next_dot + 1;
    }

    return true;
}

bool TryRewriteRenderedBinaryMethodCarryExpression(
    std::string_view expression,
    std::string* repaired_expression) {
    if (repaired_expression == nullptr) {
        return false;
    }

    static constexpr std::array<std::string_view, 4> kLengthMethodSuffixes = {
        ":GetLength()", ":GetLengthXY()", ":GetSquaredLength()", ":GetSquaredLengthXY()"};

    const auto find_top_level_add_sub = [](std::string_view expression,
                                           std::size_t* op_pos_out) -> bool {
        if (op_pos_out == nullptr) {
            return false;
        }

        bool in_single_quote = false;
        bool in_double_quote = false;
        int paren_depth = 0;
        int brace_depth = 0;
        int bracket_depth = 0;
        std::size_t best = std::string_view::npos;
        for (std::size_t i = 0; i + 2 < expression.size(); ++i) {
            const char ch = expression[i];
            if (in_single_quote) {
                if (ch == '\\' && i + 1 < expression.size()) {
                    ++i;
                } else if (ch == '\'') {
                    in_single_quote = false;
                }
                continue;
            }
            if (in_double_quote) {
                if (ch == '\\' && i + 1 < expression.size()) {
                    ++i;
                } else if (ch == '"') {
                    in_double_quote = false;
                }
                continue;
            }
            if (ch == '\'') {
                in_single_quote = true;
                continue;
            }
            if (ch == '"') {
                in_double_quote = true;
                continue;
            }
            if (ch == '(') {
                ++paren_depth;
                continue;
            }
            if (ch == ')') {
                paren_depth = std::max(0, paren_depth - 1);
                continue;
            }
            if (ch == '{') {
                ++brace_depth;
                continue;
            }
            if (ch == '}') {
                brace_depth = std::max(0, brace_depth - 1);
                continue;
            }
            if (ch == '[') {
                ++bracket_depth;
                continue;
            }
            if (ch == ']') {
                bracket_depth = std::max(0, bracket_depth - 1);
                continue;
            }
            if (paren_depth != 0 || brace_depth != 0 || bracket_depth != 0) {
                continue;
            }
            if (expression.substr(i, 3) == " + " || expression.substr(i, 3) == " - ") {
                best = i;
            }
        }

        if (best == std::string_view::npos) {
            return false;
        }
        *op_pos_out = best;
        return true;
    };

    const std::string normalized_expression =
        StripRenderedWholeParentheses(TrimLuaWhitespace(expression), nullptr);
    if (normalized_expression.empty()) {
        return false;
    }

    for (std::string_view suffix : kLengthMethodSuffixes) {
        std::size_t suffix_pos = normalized_expression.rfind(suffix);
        if (suffix_pos == std::string::npos) {
            continue;
        }

        const std::string_view prefix_view =
            TrimLuaWhitespace(std::string_view(normalized_expression).substr(0, suffix_pos));
        if (prefix_view.empty()) {
            continue;
        }

        std::size_t op_pos = std::string_view::npos;
        if (!find_top_level_add_sub(prefix_view, &op_pos)) {
            continue;
        }

        const std::string_view left = TrimLuaWhitespace(prefix_view.substr(0, op_pos));
        const std::string_view right = TrimLuaWhitespace(prefix_view.substr(op_pos + 3));
        if (left.empty() || right.empty() || !IsRenderedSafePrefixExpression(right) ||
            LooksLikeRenderedNumericLiteral(left)) {
            continue;
        }

        *repaired_expression = "(" + std::string(prefix_view) + ")" + std::string(suffix) +
                               normalized_expression.substr(suffix_pos + suffix.size());
        return true;
    }

    return false;
}

bool TryRewriteRenderedBinaryMethodCarryExpressionRecursive(
    std::string_view expression,
    std::string* repaired_expression,
    int depth) {
    if (repaired_expression == nullptr || depth > 8) {
        return false;
    }

    if (TryRewriteRenderedBinaryMethodCarryExpression(expression, repaired_expression)) {
        return true;
    }

    bool in_single_quote = false;
    bool in_double_quote = false;
    std::vector<std::size_t> open_stack;
    for (std::size_t index = 0; index < expression.size(); ++index) {
        const char ch = expression[index];
        if (in_single_quote) {
            if (ch == '\\' && index + 1 < expression.size()) {
                ++index;
            } else if (ch == '\'') {
                in_single_quote = false;
            }
            continue;
        }
        if (in_double_quote) {
            if (ch == '\\' && index + 1 < expression.size()) {
                ++index;
            } else if (ch == '"') {
                in_double_quote = false;
            }
            continue;
        }
        if (ch == '\'') {
            in_single_quote = true;
            continue;
        }
        if (ch == '"') {
            in_double_quote = true;
            continue;
        }
        if (ch == '(') {
            open_stack.push_back(index);
            continue;
        }
        if (ch != ')' || open_stack.empty()) {
            continue;
        }

        const std::size_t open = open_stack.back();
        open_stack.pop_back();
        if (index <= open + 1) {
            continue;
        }

        const std::string_view inner = expression.substr(open + 1, index - open - 1);
        std::string repaired_inner;
        if (!TryRewriteRenderedBinaryMethodCarryExpressionRecursive(
                inner,
                &repaired_inner,
                depth + 1)) {
            continue;
        }

        *repaired_expression = std::string(expression.substr(0, open + 1)) + repaired_inner +
                               std::string(expression.substr(index));
        return true;
    }

    return false;
}

bool IsRenderedSetlistErrorBanner(std::string_view line) {
    const std::string_view trimmed = TrimLuaWhitespace(line);
    return trimmed == "-- DECOMPILER ERROR: No list found. Setlist fails" ||
           trimmed == "-- DECOMPILER ERROR: Overwrote pending register.";
}

bool IsRenderedSetlistMissingListBanner(std::string_view line) {
    return TrimLuaWhitespace(line) == "-- DECOMPILER ERROR: No list found. Setlist fails";
}

void CollectRenderedTemporaryNames(std::string_view expression,
                                   std::unordered_set<std::string>* names) {
    if (names == nullptr) {
        return;
    }
    for (std::size_t index = 0; index < expression.size();) {
        const unsigned char ch = static_cast<unsigned char>(expression[index]);
        if ((std::isalpha(ch) == 0) && expression[index] != '_') {
            ++index;
            continue;
        }
        const std::size_t start = index;
        ++index;
        while (index < expression.size()) {
            const unsigned char next = static_cast<unsigned char>(expression[index]);
            if ((std::isalnum(next) == 0) && expression[index] != '_') {
                break;
            }
            ++index;
        }
        const std::string_view token = expression.substr(start, index - start);
        if (IsRenderTemporaryName(token)) {
            names->insert(std::string(token));
        }
    }
}

bool HasRenderedTemporaryAssignmentInRange(const std::vector<std::string>& lines,
                                           std::size_t begin,
                                           std::size_t end,
                                           std::string_view temporary_name) {
    if (begin >= lines.size() || temporary_name.empty()) {
        return false;
    }
    end = std::min(end, lines.size());
    for (std::size_t probe = begin; probe < end; ++probe) {
        std::vector<std::string> lhs_names;
        std::string rhs_expression;
        bool assign_is_local = false;
        if (!ParseRenderedAssignmentLine(lines[probe],
                                         &assign_is_local,
                                         &lhs_names,
                                         &rhs_expression)) {
            continue;
        }
        for (const std::string& lhs_name : lhs_names) {
            if (TrimLuaWhitespace(lhs_name) == temporary_name) {
                return true;
            }
        }
    }
    return false;
}

int ComputeRenderedBraceDeltaIgnoringStringsAndComments(std::string_view line) {
    int delta = 0;
    bool in_single_quote = false;
    bool in_double_quote = false;
    for (std::size_t index = 0; index < line.size(); ++index) {
        if (!in_single_quote && !in_double_quote && index + 1 < line.size() &&
            line[index] == '-' && line[index + 1] == '-') {
            break;
        }

        if (in_single_quote) {
            if (line[index] == '\\' && index + 1 < line.size()) {
                ++index;
            } else if (line[index] == '\'') {
                in_single_quote = false;
            }
            continue;
        }

        if (in_double_quote) {
            if (line[index] == '\\' && index + 1 < line.size()) {
                ++index;
            } else if (line[index] == '"') {
                in_double_quote = false;
            }
            continue;
        }

        if (line[index] == '\'') {
            in_single_quote = true;
            continue;
        }
        if (line[index] == '"') {
            in_double_quote = true;
            continue;
        }
        if (line[index] == '{') {
            ++delta;
        } else if (line[index] == '}') {
            --delta;
        }
    }
    return delta;
}

bool IsRenderedScopePathPrefix(const std::vector<std::size_t>& prefix,
                               const std::vector<std::size_t>& candidate) {
    return prefix.size() <= candidate.size() &&
           std::equal(prefix.begin(), prefix.end(), candidate.begin());
}

bool IsRenderedSyntheticRootWrapperFunction(const std::vector<std::string>& lines,
                                            std::size_t function_start,
                                            std::size_t function_end) {
    if (function_start >= lines.size() || function_end >= lines.size() ||
        function_end <= function_start) {
        return false;
    }

    const std::string_view header =
        TrimLuaWhitespace(StripLuaLineComment(lines[function_start]));
    if (!header.starts_with("function(")) {
        return false;
    }

    for (std::size_t probe = 0; probe < function_start; ++probe) {
        if (!TrimLuaWhitespace(StripLuaLineComment(lines[probe])).empty()) {
            return false;
        }
    }

    const std::string_view footer =
        TrimLuaWhitespace(StripLuaLineComment(lines[function_end]));
    if (footer != "end") {
        return false;
    }

    for (std::size_t probe = function_end + 1; probe < lines.size(); ++probe) {
        if (!TrimLuaWhitespace(StripLuaLineComment(lines[probe])).empty()) {
            return false;
        }
    }

    return true;
}

bool ParseRenderedCreateSubclassExpression(std::string_view text,
                                           std::string* base_expression,
                                           std::string* subclass_name) {
    std::string_view trimmed = TrimLuaWhitespace(StripLuaLineComment(text));
    if (trimmed.empty()) {
        return false;
    }

    constexpr std::string_view kNeedle = ":CreateSubClass(";
    const std::size_t needle = trimmed.find(kNeedle);
    if (needle == std::string_view::npos || needle == 0) {
        return false;
    }

    const std::string_view parsed_base = TrimLuaWhitespace(trimmed.substr(0, needle));
    if (parsed_base.empty() || !IsRenderedSafePrefixExpression(parsed_base)) {
        return false;
    }

    const std::size_t name_start = needle + kNeedle.size();
    if (name_start >= trimmed.size()) {
        return false;
    }

    const char quote = trimmed[name_start];
    if (quote != '"' && quote != '\'') {
        return false;
    }

    const std::size_t name_end = trimmed.find(quote, name_start + 1);
    if (name_end == std::string_view::npos || name_end <= name_start + 1) {
        return false;
    }

    const std::string_view parsed_name =
        trimmed.substr(name_start + 1, name_end - name_start - 1);
    const std::string_view tail = TrimLuaWhitespace(trimmed.substr(name_end + 1));
    if (tail != ")") {
        return false;
    }
    if (!IsValidLuaIdentifier(parsed_name)) {
        return false;
    }

    if (base_expression != nullptr) {
        *base_expression = std::string(parsed_base);
    }
    if (subclass_name != nullptr) {
        *subclass_name = std::string(parsed_name);
    }
    return true;
}

bool ParseRenderedCreateSubclassStatement(std::string_view line,
                                          std::string* base_expression,
                                          std::string* subclass_name) {
    std::string_view trimmed = TrimLuaWhitespace(StripLuaLineComment(line));
    if (trimmed.empty() || trimmed.starts_with("local ") ||
        trimmed.find('=') != std::string_view::npos) {
        return false;
    }

    return ParseRenderedCreateSubclassExpression(trimmed, base_expression, subclass_name);
}

bool ParseRenderedIndexedTargetExpression(std::string_view expression,
                                          std::string* base,
                                          std::string* key,
                                          std::string* suffix) {
    expression = TrimLuaWhitespace(expression);
    if (expression.empty()) {
        return false;
    }

    const std::size_t open = expression.find('[');
    if (open == std::string_view::npos || open == 0) {
        return false;
    }

    const std::string_view base_view = TrimLuaWhitespace(expression.substr(0, open));
    if (base_view.empty() || !IsRenderedSafePrefixExpression(base_view) ||
        base_view.find('[') != std::string_view::npos ||
        base_view.find('(') != std::string_view::npos ||
        base_view.find(':') != std::string_view::npos) {
        return false;
    }

    bool in_single_quote = false;
    bool in_double_quote = false;
    int bracket_depth = 1;
    std::size_t close = std::string_view::npos;
    for (std::size_t i = open + 1; i < expression.size(); ++i) {
        const char ch = expression[i];
        if (in_single_quote) {
            if (ch == '\\' && i + 1 < expression.size()) {
                ++i;
            } else if (ch == '\'') {
                in_single_quote = false;
            }
            continue;
        }
        if (in_double_quote) {
            if (ch == '\\' && i + 1 < expression.size()) {
                ++i;
            } else if (ch == '"') {
                in_double_quote = false;
            }
            continue;
        }
        if (ch == '\'') {
            in_single_quote = true;
            continue;
        }
        if (ch == '"') {
            in_double_quote = true;
            continue;
        }
        if (ch == '[') {
            ++bracket_depth;
        } else if (ch == ']') {
            --bracket_depth;
            if (bracket_depth == 0) {
                close = i;
                break;
            }
        }
    }

    if (close == std::string_view::npos || close <= open + 1) {
        return false;
    }

    const std::string_view key_view =
        TrimLuaWhitespace(expression.substr(open + 1, close - open - 1));
    const std::string_view suffix_view = TrimLuaWhitespace(expression.substr(close + 1));
    if (key_view.empty() ||
        (!suffix_view.empty() && suffix_view.front() != '.' &&
         suffix_view.front() != '[')) {
        return false;
    }

    if (base != nullptr) {
        *base = std::string(base_view);
    }
    if (key != nullptr) {
        *key = std::string(key_view);
    }
    if (suffix != nullptr) {
        *suffix = std::string(suffix_view);
    }
    return true;
}

bool TryParseRenderedTemporaryBackedTableField(const std::vector<std::string>& lines,
                                               std::size_t line_index,
                                               std::string_view table_name,
                                               std::string* field_key,
                                               std::string* rhs_expression,
                                               bool* rhs_is_function,
                                               int* array_index,
                                               std::size_t* consumed_lines) {
    if (line_index + 1 >= lines.size()) {
        return false;
    }

    std::string temporary_name;
    std::string temporary_expression;
    if (!IsTemporaryAssignmentLine(lines[line_index], &temporary_name, &temporary_expression)) {
        return false;
    }

    std::string parsed_field_key;
    std::string parsed_rhs_expression;
    bool parsed_rhs_is_function = false;
    int parsed_array_index = -1;
    if (!ParseRenderedTableFieldAssignment(lines[line_index + 1],
                                           table_name,
                                           &parsed_field_key,
                                           &parsed_rhs_expression,
                                           &parsed_rhs_is_function,
                                           &parsed_array_index)) {
        return false;
    }

    if (TrimLuaWhitespace(parsed_rhs_expression) != temporary_name) {
        return false;
    }

    if (field_key != nullptr) {
        *field_key = std::move(parsed_field_key);
    }
    if (rhs_expression != nullptr) {
        *rhs_expression = std::move(temporary_expression);
    }
    if (rhs_is_function != nullptr) {
        const std::string_view trimmed_rhs = TrimLuaWhitespace(*rhs_expression);
        *rhs_is_function = trimmed_rhs.size() >= 9 &&
                           trimmed_rhs.substr(0, 9) == std::string_view("function(");
    }
    if (array_index != nullptr) {
        *array_index = parsed_array_index;
    }
    if (consumed_lines != nullptr) {
        *consumed_lines = 2;
    }
    return true;
}

bool TryParseRenderedConditionalTableField(const std::vector<std::string>& lines,
                                           std::size_t line_index,
                                           std::string_view table_name,
                                           std::string* condition,
                                           std::string* field_key,
                                           std::string* then_expression,
                                           std::string* else_expression,
                                           int* array_index,
                                           std::size_t* consumed_lines) {
    if (line_index + 4 >= lines.size()) {
        return false;
    }

    const std::string_view trimmed_header =
        TrimLuaWhitespace(StripLuaLineComment(lines[line_index]));
    if (!StartsWithLuaKeyword(trimmed_header, "if") || trimmed_header.size() < 8 ||
        !trimmed_header.ends_with("then")) {
        return false;
    }

    const std::string_view condition_view =
        TrimLuaWhitespace(trimmed_header.substr(2, trimmed_header.size() - 2 - 4));
    if (condition_view.empty()) {
        return false;
    }

    std::string parsed_then_field_key;
    std::string parsed_then_expression;
    bool parsed_then_is_function = false;
    int parsed_then_array_index = -1;
    std::size_t then_consumed_lines = 1;
    if (!ParseRenderedTableFieldAssignment(lines[line_index + 1],
                                           table_name,
                                           &parsed_then_field_key,
                                           &parsed_then_expression,
                                           &parsed_then_is_function,
                                           &parsed_then_array_index)) {
        if (!TryParseRenderedTemporaryBackedTableField(lines,
                                                       line_index + 1,
                                                       table_name,
                                                       &parsed_then_field_key,
                                                       &parsed_then_expression,
                                                       &parsed_then_is_function,
                                                       &parsed_then_array_index,
                                                       &then_consumed_lines)) {
            return false;
        }
    }

    const std::size_t else_line_index = line_index + 1 + then_consumed_lines;
    if (else_line_index + 2 >= lines.size() ||
        TrimLuaWhitespace(StripLuaLineComment(lines[else_line_index])) != "else") {
        return false;
    }

    std::string parsed_else_field_key;
    std::string parsed_else_expression;
    bool parsed_else_is_function = false;
    int parsed_else_array_index = -1;
    std::size_t else_consumed_lines = 1;
    if (!ParseRenderedTableFieldAssignment(lines[else_line_index + 1],
                                           table_name,
                                           &parsed_else_field_key,
                                           &parsed_else_expression,
                                           &parsed_else_is_function,
                                           &parsed_else_array_index)) {
        if (!TryParseRenderedTemporaryBackedTableField(lines,
                                                       else_line_index + 1,
                                                       table_name,
                                                       &parsed_else_field_key,
                                                       &parsed_else_expression,
                                                       &parsed_else_is_function,
                                                       &parsed_else_array_index,
                                                       &else_consumed_lines)) {
            return false;
        }
    }

    const std::size_t end_line_index = else_line_index + 1 + else_consumed_lines;
    if (TrimLuaWhitespace(StripLuaLineComment(lines[end_line_index])) != "end") {
        return false;
    }

    if (parsed_then_field_key != parsed_else_field_key ||
        parsed_then_array_index != parsed_else_array_index ||
        parsed_then_is_function || parsed_else_is_function ||
        parsed_then_expression == "{" || parsed_else_expression == "{") {
        return false;
    }

    int then_paren_balance = 0;
    int then_brace_balance = 0;
    int else_paren_balance = 0;
    int else_brace_balance = 0;
    if (HasPositiveLuaExpressionBalance(parsed_then_expression,
                                        &then_paren_balance,
                                        &then_brace_balance) ||
        HasPositiveLuaExpressionBalance(parsed_else_expression,
                                        &else_paren_balance,
                                        &else_brace_balance)) {
        return false;
    }

    if (condition != nullptr) {
        *condition = std::string(condition_view);
    }
    if (field_key != nullptr) {
        *field_key = std::move(parsed_then_field_key);
    }
    if (then_expression != nullptr) {
        *then_expression = std::move(parsed_then_expression);
    }
    if (else_expression != nullptr) {
        *else_expression = std::move(parsed_else_expression);
    }
    if (array_index != nullptr) {
        *array_index = parsed_then_array_index;
    }
    if (consumed_lines != nullptr) {
        *consumed_lines = 1 + then_consumed_lines + 1 + else_consumed_lines + 1;
    }
    return true;
}

bool IsRenderedExactReturnIdentifier(std::string_view line, std::string_view identifier) {
    const std::string_view trimmed = TrimLuaWhitespace(StripLuaLineComment(line));
    if (!StartsWithLuaKeyword(trimmed, "return")) {
        return false;
    }
    return TrimLuaWhitespace(trimmed.substr(6)) == identifier;
}

std::size_t FindEnclosingRenderedFunctionBlockEnd(const std::vector<std::string>& lines,
                                                  std::size_t line_index) {
    std::size_t best_start = lines.size();
    std::size_t best_end = lines.size();
    const std::size_t limit = std::min(line_index, lines.size());
    for (std::size_t candidate = 0; candidate < limit; ++candidate) {
        if (!IsRenderedFunctionStartLine(lines[candidate])) {
            continue;
        }

        const std::size_t function_end = FindRenderedFunctionBlockEnd(lines, candidate);
        if (function_end >= lines.size() || function_end < line_index) {
            continue;
        }

        if (best_start == lines.size() || candidate > best_start) {
            best_start = candidate;
            best_end = function_end;
        }
    }

    return best_end;
}

bool IsRenderedAssignmentUsedAfterContainingControlBlock(
    const std::vector<std::string>& lines,
    std::size_t assignment_line,
    std::string_view identifier) {
    if (assignment_line >= lines.size() || identifier.empty()) {
        return false;
    }

    std::size_t control_end = lines.size();
    for (std::size_t probe = 0; probe < assignment_line; ++probe) {
        const std::string_view trimmed =
            TrimLuaWhitespace(StripLuaLineComment(lines[probe]));
        const bool control_header =
            (trimmed.starts_with("if ") && trimmed.ends_with(" then")) ||
            (trimmed.starts_with("elseif ") && trimmed.ends_with(" then")) ||
            (trimmed.starts_with("while ") && trimmed.ends_with(" do")) ||
            trimmed == "do";
        if (!control_header) {
            continue;
        }

        const std::size_t block_end = FindRenderedFunctionBlockEnd(lines, probe);
        if (block_end > assignment_line && block_end < lines.size()) {
            control_end = block_end;
        }
    }
    if (control_end + 1 >= lines.size()) {
        return false;
    }

    const std::size_t outer_end =
        FindEnclosingRenderedFunctionBlockEnd(lines, assignment_line);
    const std::size_t probe_limit =
        outer_end < lines.size() ? outer_end + 1 : lines.size();
    for (std::size_t probe = control_end + 1; probe < probe_limit; ++probe) {
        if (IsRenderedShadowingLocalDeclaration(lines[probe], identifier) ||
            IsAssignmentToIdentifier(lines[probe], identifier)) {
            break;
        }
        if (CountIdentifierUses(lines[probe], identifier) != 0) {
            return true;
        }
    }

    return false;
}

}  // namespace refii::lua
