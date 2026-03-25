#include "tools/decompiler/rendered_lua_text.h"

#include <algorithm>
#include <array>
#include <cctype>
#include <iomanip>
#include <sstream>
#include <unordered_set>

namespace refii::lua {
namespace {

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

bool IsAssignmentToIdentifier(std::string_view line, std::string_view identifier) {
    std::string_view trimmed = TrimLuaWhitespace(line);
    if (trimmed.substr(0, 6) == "local ") {
        trimmed = TrimLuaWhitespace(trimmed.substr(6));
    }

    const std::size_t equal = trimmed.find('=');
    if (equal == std::string_view::npos) {
        return false;
    }

    const std::string_view lhs = TrimLuaWhitespace(trimmed.substr(0, equal));
    return lhs == identifier;
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

bool IsRenderedExactReturnIdentifier(std::string_view line, std::string_view identifier) {
    const std::string_view trimmed = TrimLuaWhitespace(StripLuaLineComment(line));
    if (!StartsWithLuaKeyword(trimmed, "return")) {
        return false;
    }
    return TrimLuaWhitespace(trimmed.substr(6)) == identifier;
}

bool TryParseRenderedTemporaryBackedTableField(const std::vector<std::string>& lines,
                                               std::size_t line_index,
                                               std::string_view table_name,
                                               std::string* field_key,
                                               std::string* rhs_expression,
                                               bool* rhs_is_function,
                                               int* array_index,
                                               std::size_t* consumed_lines);
bool IsRenderedAtomicExpression(std::string_view candidate);
bool FindRenderedMatchingParen(std::string_view text,
                               std::size_t open_index,
                               std::size_t* close_index);

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

bool IsRenderedDefinitelyTruthyExpression(std::string_view value) {
    value = TrimLuaWhitespace(value);
    if (value.empty() || value == "nil" || value == "false") {
        return false;
    }
    if (value == "true" || LooksLikeRenderedNumericLiteral(value) ||
        value.starts_with('"') || value.starts_with('\'') || value.starts_with('{') ||
        value.starts_with("function(")) {
        return true;
    }

    if (value.starts_with('(') && value.ends_with(')')) {
        return IsRenderedDefinitelyTruthyExpression(value.substr(1, value.size() - 2));
    }

    return HasRenderedTruthyOperator(value);
}

bool IsRenderedWholeParenthesized(std::string_view value) {
    value = TrimLuaWhitespace(value);
    if (value.size() < 2 || value.front() != '(' || value.back() != ')') {
        return false;
    }
    std::size_t close = std::string_view::npos;
    return FindRenderedMatchingParen(value, 0, &close) && close + 1 == value.size();
}

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

bool FindRenderedTopLevelComparison(std::string_view value,
                                    std::size_t* position,
                                    std::string_view* op) {
    value = TrimLuaWhitespace(value);
    bool in_single_quote = false;
    bool in_double_quote = false;
    int paren_depth = 0;
    int brace_depth = 0;
    int bracket_depth = 0;
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

        const std::string_view remaining = value.substr(i);
        std::string_view matched;
        if (remaining.starts_with("<=") || remaining.starts_with(">=") ||
            remaining.starts_with("==") || remaining.starts_with("~=")) {
            matched = remaining.substr(0, 2);
        } else if (remaining.starts_with("<") || remaining.starts_with(">")) {
            matched = remaining.substr(0, 1);
        }

        if (!matched.empty()) {
            if (position != nullptr) {
                *position = i;
            }
            if (op != nullptr) {
                *op = matched;
            }
            return true;
        }
    }
    return false;
}

std::string NormalizeRenderedConditionExpression(std::string_view value, bool* changed);

bool IsRenderedReusableConditionExpression(std::string_view value) {
    value = TrimLuaWhitespace(value);
    return IsValidLuaIdentifier(value) || value == "nil" || value == "true" ||
           value == "false" || LooksLikeRenderedNumericLiteral(value);
}

std::string InvertRenderedComparison(std::string_view op) {
    if (op == "==") {
        return "~=";
    }
    if (op == "~=") {
        return "==";
    }
    if (op == "<") {
        return ">=";
    }
    if (op == "<=") {
        return ">";
    }
    if (op == ">") {
        return "<=";
    }
    if (op == ">=") {
        return "<";
    }
    return std::string(op);
}

std::string NegateRenderedConditionExpression(std::string_view value, bool* changed) {
    std::string expression = "not ";
    expression += std::string(value);
    return NormalizeRenderedConditionExpression(expression, changed);
}

std::string NormalizeRenderedConditionOperand(std::string_view value, bool* changed) {
    std::string normalized = StripRenderedWholeParentheses(value, changed);
    std::size_t compare_position = std::string_view::npos;
    std::string_view compare_op;
    if (FindRenderedTopLevelComparison(normalized, &compare_position, &compare_op)) {
        const std::string left = StripRenderedWholeParentheses(
            normalized.substr(0, compare_position), changed);
        const std::string right = StripRenderedWholeParentheses(
            normalized.substr(compare_position + compare_op.size()), changed);
        return left + " " + std::string(compare_op) + " " + right;
    }
    return normalized;
}

std::string NormalizeRenderedConditionExpression(std::string_view value, bool* changed) {
    std::string normalized = StripRenderedWholeParentheses(value, changed);

    std::vector<std::string> parts;
    if (SplitRenderedTopLevelBooleanChain(normalized, "or", &parts)) {
        std::vector<std::string> normalized_parts;
        for (const std::string& part : parts) {
            std::string normalized_part =
                NormalizeRenderedConditionExpression(part, changed);
            const bool duplicate =
                IsRenderedReusableConditionExpression(normalized_part) &&
                std::find(normalized_parts.begin(), normalized_parts.end(), normalized_part) !=
                    normalized_parts.end();
            if (duplicate) {
                if (changed != nullptr) {
                    *changed = true;
                }
                continue;
            }
            normalized_parts.push_back(std::move(normalized_part));
        }

        if (normalized_parts.size() == 1) {
            return normalized_parts.front();
        }

        std::string rebuilt;
        for (std::size_t i = 0; i < normalized_parts.size(); ++i) {
            if (i != 0) {
                rebuilt += " or ";
            }
            rebuilt += normalized_parts[i];
        }
        return rebuilt;
    }

    if (SplitRenderedTopLevelBooleanChain(normalized, "and", &parts)) {
        std::vector<std::string> normalized_parts;
        for (const std::string& part : parts) {
            std::string normalized_part =
                NormalizeRenderedConditionExpression(part, changed);
            const bool duplicate =
                IsRenderedReusableConditionExpression(normalized_part) &&
                std::find(normalized_parts.begin(), normalized_parts.end(), normalized_part) !=
                    normalized_parts.end();
            if (duplicate) {
                if (changed != nullptr) {
                    *changed = true;
                }
                continue;
            }
            normalized_parts.push_back(std::move(normalized_part));
        }

        if (normalized_parts.size() == 1) {
            return normalized_parts.front();
        }

        std::string rebuilt;
        for (std::size_t i = 0; i < normalized_parts.size(); ++i) {
            if (i != 0) {
                rebuilt += " and ";
            }
            std::string part = normalized_parts[i];
            std::vector<std::string> or_parts;
            if (SplitRenderedTopLevelBooleanChain(part, "or", &or_parts)) {
                rebuilt += "(" + part + ")";
            } else {
                rebuilt += part;
            }
        }
        return rebuilt;
    }

    if (StartsWithLuaKeyword(normalized, "not")) {
        std::string rest =
            NormalizeRenderedConditionExpression(TrimLuaWhitespace(normalized.substr(3)), changed);
        if (StartsWithLuaKeyword(rest, "not")) {
            if (changed != nullptr) {
                *changed = true;
            }
            return NormalizeRenderedConditionExpression(TrimLuaWhitespace(rest.substr(3)),
                                                        changed);
        }

        if (SplitRenderedTopLevelBooleanChain(rest, "and", &parts)) {
            if (changed != nullptr) {
                *changed = true;
            }

            std::string rebuilt;
            for (std::size_t i = 0; i < parts.size(); ++i) {
                if (i != 0) {
                    rebuilt += " or ";
                }
                rebuilt += NegateRenderedConditionExpression(parts[i], changed);
            }
            return NormalizeRenderedConditionExpression(rebuilt, changed);
        }

        if (SplitRenderedTopLevelBooleanChain(rest, "or", &parts)) {
            if (changed != nullptr) {
                *changed = true;
            }

            std::string rebuilt;
            for (std::size_t i = 0; i < parts.size(); ++i) {
                if (i != 0) {
                    rebuilt += " and ";
                }
                rebuilt += NegateRenderedConditionExpression(parts[i], changed);
            }
            return NormalizeRenderedConditionExpression(rebuilt, changed);
        }

        std::vector<std::string> boolean_parts;
        std::size_t compare_position = std::string_view::npos;
        std::string_view compare_op;
        if (!SplitRenderedTopLevelBooleanChain(rest, "and", &boolean_parts) &&
            !SplitRenderedTopLevelBooleanChain(rest, "or", &boolean_parts) &&
            FindRenderedTopLevelComparison(rest, &compare_position, &compare_op)) {
            if (changed != nullptr) {
                *changed = true;
            }
            const std::string left = NormalizeRenderedConditionOperand(
                rest.substr(0, compare_position), changed);
            const std::string right = NormalizeRenderedConditionOperand(
                rest.substr(compare_position + compare_op.size()), changed);
            return left + " " + InvertRenderedComparison(compare_op) + " " + right;
        }

        const std::string operand = NormalizeRenderedConditionOperand(rest, changed);
        if (IsRenderedAtomicExpression(operand)) {
            return "not " + operand;
        }
        return "not (" + operand + ")";
    }

    return NormalizeRenderedConditionOperand(normalized, changed);
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

    if (candidate.empty() || !is_identifier_start(candidate.front())) {
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
    while (index < candidate.size()) {
        const char ch = candidate[index];
        if (ch == '.') {
            ++index;
            if (index >= candidate.size() || !is_identifier_start(candidate[index])) {
                return false;
            }
            index = skip_identifier(index);
            continue;
        }
        if (ch == ':') {
            ++index;
            if (index >= candidate.size() || !is_identifier_start(candidate[index])) {
                return false;
            }
            index = skip_identifier(index);
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

bool IsRenderedAtomicExpression(std::string_view candidate) {
    candidate = TrimLuaWhitespace(candidate);
    if (candidate.empty()) {
        return false;
    }
    return candidate == "nil" || candidate == "true" || candidate == "false" ||
           LooksLikeRenderedNumericLiteral(candidate) || candidate.starts_with('"') ||
           candidate.starts_with('\'') || IsValidLuaIdentifier(candidate) ||
           IsRenderedSafePrefixExpression(candidate);
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

    const std::string_view condition_view = TrimLuaWhitespace(
        trimmed_header.substr(2, trimmed_header.size() - 2 - 4));
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

}  // namespace

bool IsLuaKeyword(std::string_view value) {
    static constexpr std::array<std::string_view, 21> kKeywords = {
        "and",   "break", "do",    "else",   "elseif", "end",   "false",
        "for",   "function", "if", "in",     "local",  "nil",   "not",
        "or",    "repeat", "return", "then", "true",   "until", "while",
    };
    return std::find(kKeywords.begin(), kKeywords.end(), value) != kKeywords.end();
}

bool IsValidLuaIdentifier(std::string_view value) {
    if (value.empty() || IsLuaKeyword(value)) {
        return false;
    }
    const unsigned char first = static_cast<unsigned char>(value.front());
    if (std::isalpha(first) == 0 && value.front() != '_') {
        return false;
    }
    for (std::size_t i = 1; i < value.size(); ++i) {
        const unsigned char ch = static_cast<unsigned char>(value[i]);
        if (std::isalnum(ch) == 0 && value[i] != '_') {
            return false;
        }
    }
    return true;
}

std::string EscapeLuaString(std::string_view value) {
    std::string escaped;
    escaped.reserve(value.size() + 2);
    escaped.push_back('"');
    for (char ch : value) {
        switch (ch) {
            case '\\':
                escaped += "\\\\";
                break;
            case '"':
                escaped += "\\\"";
                break;
            case '\n':
                escaped += "\\n";
                break;
            case '\r':
                escaped += "\\r";
                break;
            case '\t':
                escaped += "\\t";
                break;
            default:
                escaped.push_back(ch);
                break;
        }
    }
    escaped.push_back('"');
    return escaped;
}

std::string RenderLuaNumber(double value) {
    std::ostringstream stream;
    stream << std::setprecision(17) << value;
    std::string text = stream.str();
    if (text.find('.') == std::string::npos && text.find('e') == std::string::npos &&
        text.find('E') == std::string::npos) {
        text += ".0";
    }
    return text;
}

std::string RenderLuaConstant(const Lua51Constant& constant) {
    switch (constant.type) {
        case Lua51ConstantType::kNil:
            return "nil";
        case Lua51ConstantType::kBoolean:
            return constant.boolean_value ? "true" : "false";
        case Lua51ConstantType::kNumber:
            return RenderLuaNumber(constant.number_value);
        case Lua51ConstantType::kString:
            return EscapeLuaString(constant.string_value);
    }
    return "nil";
}

std::string RenderLuaGlobal(std::string_view name) {
    if (IsValidLuaIdentifier(name)) {
        return std::string(name);
    }
    return "_G[" + EscapeLuaString(name) + "]";
}

std::string RenderLuaUpvalue(const Lua51Function& function, int index) {
    if (index >= 0 && static_cast<std::size_t>(index) < function.upvalue_names.size() &&
        IsValidLuaIdentifier(function.upvalue_names[static_cast<std::size_t>(index)])) {
        return function.upvalue_names[static_cast<std::size_t>(index)];
    }
    return "upvalue_" + std::to_string(index);
}

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

bool IsRenderTemporaryName(std::string_view value) {
    if (value.size() >= 2 && value.front() == 'r') {
        return std::all_of(value.begin() + 1, value.end(), [](char ch) {
            return std::isdigit(static_cast<unsigned char>(ch)) != 0;
        });
    }

    const auto matches_segmented_digits =
        [](std::string_view name, std::size_t start) {
            bool saw_digit = false;
            bool saw_separator = false;
            for (std::size_t i = start; i < name.size(); ++i) {
                const char ch = name[i];
                if (ch == '_') {
                    saw_separator = true;
                    continue;
                }
                if (std::isdigit(static_cast<unsigned char>(ch)) == 0) {
                    return false;
                }
                saw_digit = true;
            }
            return saw_digit && saw_separator;
        };

    if (value.size() >= 4 && (value.front() == 'L' || value.front() == 'A')) {
        return matches_segmented_digits(value, 1);
    }

    if (value.size() >= 4 && value[0] == 'l' && value[1] == '_') {
        return matches_segmented_digits(value, 2);
    }

    return false;
}

std::size_t FindIdentifierUse(std::string_view text,
                              std::string_view identifier,
                              std::size_t offset) {
    if (identifier.empty() || offset >= text.size()) {
        return std::string_view::npos;
    }

    bool in_single_quote = false;
    bool in_double_quote = false;
    for (std::size_t i = 0; i < text.size();) {
        const char ch = text[i];

        if (!in_single_quote && !in_double_quote && ch == '-' && i + 1 < text.size() &&
            text[i + 1] == '-') {
            break;
        }

        if (in_single_quote) {
            if (ch == '\\' && i + 1 < text.size()) {
                i += 2;
                continue;
            }
            if (ch == '\'') {
                in_single_quote = false;
            }
            ++i;
            continue;
        }

        if (in_double_quote) {
            if (ch == '\\' && i + 1 < text.size()) {
                i += 2;
                continue;
            }
            if (ch == '"') {
                in_double_quote = false;
            }
            ++i;
            continue;
        }

        if (ch == '\'') {
            in_single_quote = true;
            ++i;
            continue;
        }
        if (ch == '"') {
            in_double_quote = true;
            ++i;
            continue;
        }

        if (i < offset || i + identifier.size() > text.size() ||
            text.substr(i, identifier.size()) != identifier) {
            ++i;
            continue;
        }

        const bool boundary_before =
            i == 0 ||
            ((!std::isalnum(static_cast<unsigned char>(text[i - 1])) && text[i - 1] != '_') &&
             text[i - 1] != '.' && text[i - 1] != ':');
        const std::size_t end = i + identifier.size();
        const bool boundary_after =
            end >= text.size() ||
            (!std::isalnum(static_cast<unsigned char>(text[end])) && text[end] != '_');
        if (boundary_before && boundary_after) {
            return i;
        }

        i = end;
    }

    return std::string_view::npos;
}

std::size_t CountIdentifierUses(std::string_view text, std::string_view identifier) {
    std::size_t count = 0;
    std::size_t offset = 0;
    while (true) {
        const std::size_t found = FindIdentifierUse(text, identifier, offset);
        if (found == std::string_view::npos) {
            return count;
        }
        ++count;
        offset = found + identifier.size();
    }
}

bool IsTemporaryAssignmentLine(std::string_view line,
                               std::string* name,
                               std::string* expression) {
    std::string_view trimmed = TrimLuaWhitespace(line);
    if (trimmed.empty()) {
        return false;
    }

    if (trimmed.substr(0, 6) == "local ") {
        trimmed.remove_prefix(6);
        trimmed = TrimLuaWhitespace(trimmed);
    }

    const std::size_t equal = trimmed.find('=');
    if (equal == std::string_view::npos) {
        return false;
    }

    const std::string_view lhs = TrimLuaWhitespace(trimmed.substr(0, equal));
    const std::string_view rhs = TrimLuaWhitespace(trimmed.substr(equal + 1));
    if (!IsRenderTemporaryName(lhs) || rhs.empty()) {
        return false;
    }

    if (name != nullptr) {
        *name = std::string(lhs);
    }
    if (expression != nullptr) {
        *expression = std::string(rhs);
    }
    return true;
}

bool IsLocalTemporaryAssignmentLine(std::string_view line,
                                    std::string* name,
                                    std::string* expression) {
    std::string_view trimmed = TrimLuaWhitespace(line);
    if (!trimmed.starts_with("local ")) {
        return false;
    }
    return IsTemporaryAssignmentLine(trimmed, name, expression);
}

bool IsAssignmentToTemporary(std::string_view line, std::string_view identifier) {
    std::string name;
    return IsTemporaryAssignmentLine(line, &name, nullptr) && name == identifier;
}

bool LocalizeRenderedLuaFirstAssignments(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    std::unordered_set<std::string> seen_temporaries;
    for (std::string& line : *lines) {
        std::string temporary_name;
        if (IsLocalTemporaryAssignmentLine(line, &temporary_name, nullptr)) {
            seen_temporaries.insert(std::move(temporary_name));
            continue;
        }

        if (!IsTemporaryAssignmentLine(line, &temporary_name, nullptr)) {
            continue;
        }

        if (!seen_temporaries.insert(temporary_name).second) {
            continue;
        }

        const std::size_t first_non_space = line.find_first_not_of(" \t");
        if (first_non_space == std::string::npos) {
            continue;
        }

        line.insert(first_non_space, "local ");
        changed = true;
    }

    return changed;
}

bool FindIdentifierUseInAssignmentRhs(std::string_view line,
                                      std::string_view identifier,
                                      std::size_t* absolute_found,
                                      std::size_t* use_count) {
    const std::size_t equal = line.find('=');
    if (equal == std::string_view::npos) {
        return false;
    }

    const std::string_view rhs = line.substr(equal + 1);
    const std::size_t rhs_use_count = CountIdentifierUses(rhs, identifier);
    if (rhs_use_count == 0) {
        return false;
    }

    const std::size_t rhs_found = FindIdentifierUse(rhs, identifier);
    if (rhs_found == std::string_view::npos) {
        return false;
    }

    if (absolute_found != nullptr) {
        *absolute_found = equal + 1 + rhs_found;
    }
    if (use_count != nullptr) {
        *use_count = rhs_use_count;
    }
    return true;
}

std::string WrapInlineExpression(std::string_view expression) {
    const std::string_view trimmed = TrimLuaWhitespace(expression);
    if (trimmed.empty()) {
        return "nil";
    }

    const auto is_safe_prefix_expression = [](std::string_view candidate) {
        candidate = TrimLuaWhitespace(candidate);
        const auto is_identifier_start = [](char ch) {
            const unsigned char uch = static_cast<unsigned char>(ch);
            return std::isalpha(uch) != 0 || ch == '_';
        };
        const auto is_identifier_char = [&](char ch) {
            const unsigned char uch = static_cast<unsigned char>(ch);
            return std::isalnum(uch) != 0 || ch == '_';
        };

        if (candidate.empty() || !is_identifier_start(candidate.front())) {
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
        while (index < candidate.size()) {
            const char ch = candidate[index];
            if (ch == '.') {
                ++index;
                if (index >= candidate.size() || !is_identifier_start(candidate[index])) {
                    return false;
                }
                index = skip_identifier(index);
                continue;
            }
            if (ch == ':') {
                ++index;
                if (index >= candidate.size() || !is_identifier_start(candidate[index])) {
                    return false;
                }
                index = skip_identifier(index);
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
    };

    if (trimmed.front() == '{' || trimmed.front() == '"' || trimmed.front() == '\'' ||
        trimmed.front() == '(' || trimmed == "nil" || trimmed == "true" ||
        trimmed == "false" || IsValidLuaIdentifier(trimmed) ||
        is_safe_prefix_expression(trimmed)) {
        return std::string(trimmed);
    }

    return "(" + std::string(trimmed) + ")";
}

bool HasPositiveLuaExpressionBalance(std::string_view expression,
                                     int* paren_balance_out,
                                     int* brace_balance_out) {
    int paren_balance = 0;
    int brace_balance = 0;
    bool in_single_quote = false;
    bool in_double_quote = false;
    std::string_view expression_without_comment(expression);
    for (std::size_t i = 0; i + 1 < expression_without_comment.size(); ++i) {
        const char ch = expression_without_comment[i];
        if (in_single_quote) {
            if (ch == '\\' && i + 1 < expression_without_comment.size()) {
                ++i;
            } else if (ch == '\'') {
                in_single_quote = false;
            }
            continue;
        }
        if (in_double_quote) {
            if (ch == '\\' && i + 1 < expression_without_comment.size()) {
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
        if (ch == '-' && expression_without_comment[i + 1] == '-') {
            expression_without_comment = expression_without_comment.substr(0, i);
            break;
        }
    }

    in_single_quote = false;
    in_double_quote = false;
    for (std::size_t i = 0; i < expression_without_comment.size(); ++i) {
        const char ch = expression_without_comment[i];
        if (in_single_quote) {
            if (ch == '\\' && i + 1 < expression_without_comment.size()) {
                ++i;
            } else if (ch == '\'') {
                in_single_quote = false;
            }
            continue;
        }
        if (in_double_quote) {
            if (ch == '\\' && i + 1 < expression_without_comment.size()) {
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
            ++paren_balance;
        } else if (ch == ')') {
            --paren_balance;
        } else if (ch == '{') {
            ++brace_balance;
        } else if (ch == '}') {
            --brace_balance;
        }
    }

    if (paren_balance_out != nullptr) {
        *paren_balance_out = paren_balance;
    }
    if (brace_balance_out != nullptr) {
        *brace_balance_out = brace_balance;
    }
    return paren_balance > 0 || brace_balance > 0;
}

bool SimplifyRenderedLuaTemporaries(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (int pass = 0; pass < 64; ++pass) {
        bool pass_changed = false;
        for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
            std::string temporary_name;
            std::string temporary_expression;
            if (!IsTemporaryAssignmentLine((*lines)[line_index],
                                           &temporary_name,
                                           &temporary_expression)) {
                continue;
            }

            const std::string_view temporary_trimmed = TrimLuaWhitespace(temporary_expression);
            int paren_balance = 0;
            int brace_balance = 0;
            const bool has_positive_balance = HasPositiveLuaExpressionBalance(
                temporary_expression, &paren_balance, &brace_balance);
            if (temporary_expression.find('\n') != std::string::npos ||
                (!temporary_trimmed.empty() && temporary_trimmed.front() == '{') ||
                temporary_expression.find("function(") != std::string::npos ||
                has_positive_balance) {
                continue;
            }

            std::size_t use_line = lines->size();
            std::size_t use_count = 0;
            std::size_t use_found = std::string::npos;
            bool blocked_by_reassignment = false;
            const std::size_t scope_end =
                FindEnclosingRenderedFunctionBlockEnd(*lines, line_index);
            const std::size_t probe_limit =
                scope_end < lines->size() ? scope_end + 1 : lines->size();
            for (std::size_t probe = line_index + 1; probe < probe_limit; ++probe) {
                const std::string_view probe_trimmed = TrimLuaWhitespace((*lines)[probe]);
                if (probe_trimmed.find("function(") != std::string_view::npos) {
                    const std::size_t line_uses =
                        CountIdentifierUses((*lines)[probe], temporary_name);
                    if (line_uses != 0) {
                        if (use_count == 0 && line_uses == 1) {
                            use_found = FindIdentifierUse((*lines)[probe], temporary_name);
                        } else {
                            use_found = std::string::npos;
                        }
                        use_count += line_uses;
                        use_line = probe;
                        if (use_count > 1) {
                            break;
                        }
                    }
                    const std::size_t function_end = FindRenderedFunctionBlockEnd(*lines, probe);
                    if (function_end >= lines->size() || function_end <= probe) {
                        break;
                    }
                    probe = function_end;
                    continue;
                }

                if (IsAssignmentToTemporary((*lines)[probe], temporary_name)) {
                    std::size_t rhs_found = std::string::npos;
                    std::size_t rhs_use_count = 0;
                    if (FindIdentifierUseInAssignmentRhs((*lines)[probe],
                                                         temporary_name,
                                                         &rhs_found,
                                                         &rhs_use_count)) {
                        if (use_count == 0 && rhs_use_count == 1) {
                            use_found = rhs_found;
                        } else {
                            use_found = std::string::npos;
                        }
                        use_count += rhs_use_count;
                        use_line = probe;
                    } else {
                        blocked_by_reassignment = true;
                    }
                    break;
                }

                const std::size_t line_uses =
                    CountIdentifierUses((*lines)[probe], temporary_name);
                if (line_uses == 0) {
                    continue;
                }
                if (use_count == 0 && line_uses == 1) {
                    use_found = FindIdentifierUse((*lines)[probe], temporary_name);
                } else {
                    use_found = std::string::npos;
                }
                use_count += line_uses;
                use_line = probe;
                if (use_count > 1) {
                    break;
                }
            }

            if (use_count == 0) {
                if (blocked_by_reassignment) {
                    continue;
                }
                lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index));
                changed = true;
                pass_changed = true;
                break;
            }

            if (use_count != 1 || use_line >= lines->size()) {
                continue;
            }

            const std::size_t found = use_found != std::string::npos
                                          ? use_found
                                          : FindIdentifierUse((*lines)[use_line],
                                                              temporary_name);
            if (found == std::string::npos) {
                continue;
            }

            const std::string wrapped_expression =
                WrapInlineExpression(temporary_expression);
            const std::size_t first_non_space =
                (*lines)[use_line].find_first_not_of(" \t");
            if (first_non_space != std::string::npos && found == first_non_space &&
                !wrapped_expression.empty() && wrapped_expression.front() == '(') {
                const std::string_view suffix = TrimLuaWhitespace(
                    std::string_view((*lines)[use_line]).substr(found + temporary_name.size()));
                const bool safe_statement_leading_inline =
                    !suffix.empty() &&
                    (suffix.front() == '.' || suffix.front() == '[' ||
                     suffix.front() == ':' || suffix.front() == '(');
                if (!safe_statement_leading_inline) {
                    continue;
                }
            }

            std::string replacement_line = (*lines)[use_line];
            replacement_line.replace(found,
                                     temporary_name.size(),
                                     wrapped_expression);
            (*lines)[use_line] = std::move(replacement_line);
            lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index));
            changed = true;
            pass_changed = true;
            break;
        }

        if (!pass_changed) {
            break;
        }
    }

    return changed;
}

bool SimplifyRenderedLuaReturnTemporaries(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (int pass = 0; pass < 64; ++pass) {
        bool pass_changed = false;
        for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
            std::string temporary_name;
            std::string temporary_expression;
            if (!IsTemporaryAssignmentLine((*lines)[line_index],
                                           &temporary_name,
                                           &temporary_expression)) {
                continue;
            }

            int paren_balance = 0;
            int brace_balance = 0;
            if (temporary_expression.find('\n') != std::string::npos ||
                HasPositiveLuaExpressionBalance(temporary_expression,
                                               &paren_balance,
                                               &brace_balance)) {
                continue;
            }

            std::size_t use_line = lines->size();
            std::size_t use_count = 0;
            std::size_t use_found = std::string::npos;
            const std::size_t scope_end =
                FindEnclosingRenderedFunctionBlockEnd(*lines, line_index);
            const std::size_t probe_limit =
                scope_end < lines->size() ? scope_end + 1 : lines->size();
            for (std::size_t probe = line_index + 1; probe < probe_limit; ++probe) {
                const std::string_view probe_trimmed = TrimLuaWhitespace((*lines)[probe]);
                if (probe_trimmed.find("function(") != std::string_view::npos) {
                    const std::size_t line_uses =
                        CountIdentifierUses((*lines)[probe], temporary_name);
                    if (line_uses != 0) {
                        use_count += line_uses;
                        use_line = probe;
                    }
                    const std::size_t function_end = FindRenderedFunctionBlockEnd(*lines, probe);
                    if (function_end >= lines->size() || function_end <= probe) {
                        break;
                    }
                    probe = function_end;
                    continue;
                }

                if (IsAssignmentToTemporary((*lines)[probe], temporary_name)) {
                    break;
                }

                const std::size_t line_uses =
                    CountIdentifierUses((*lines)[probe], temporary_name);
                if (line_uses == 0) {
                    continue;
                }
                if (use_count == 0 && line_uses == 1) {
                    use_found = FindIdentifierUse((*lines)[probe], temporary_name);
                } else {
                    use_found = std::string::npos;
                }
                use_count += line_uses;
                use_line = probe;
                if (use_count > 1) {
                    break;
                }
            }

            if (use_count != 1 || use_line >= lines->size()) {
                continue;
            }

            const std::string_view stripped_use = StripLuaLineComment((*lines)[use_line]);
            const std::string_view trimmed_use = TrimLuaWhitespace(stripped_use);
            if (!StartsWithLuaKeyword(trimmed_use, "return")) {
                continue;
            }
            const std::size_t found = use_found != std::string::npos
                                          ? use_found
                                          : FindIdentifierUse((*lines)[use_line],
                                                              temporary_name);
            if (found == std::string::npos) {
                continue;
            }

            const std::size_t return_prefix =
                (*lines)[use_line].find("return");
            if (return_prefix == std::string::npos || found < return_prefix + 6) {
                continue;
            }

            std::string replacement_line = (*lines)[use_line];
            replacement_line.replace(found,
                                     temporary_name.size(),
                                     WrapInlineExpression(temporary_expression));
            (*lines)[use_line] = std::move(replacement_line);
            lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index));
            changed = true;
            pass_changed = true;
            break;
        }

        if (!pass_changed) {
            break;
        }
    }

    return changed;
}

std::size_t CountIdentifierKeyword(std::string_view line, std::string_view keyword) {
    return CountIdentifierUses(line, keyword);
}

bool StartsWithLuaKeyword(std::string_view text, std::string_view keyword) {
    if (text.size() < keyword.size() || text.substr(0, keyword.size()) != keyword) {
        return false;
    }
    if (text.size() == keyword.size()) {
        return true;
    }
    const char next = text[keyword.size()];
    return std::isspace(static_cast<unsigned char>(next)) != 0 || next == '(';
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

int GetRenderedLuaBlockDelta(std::string_view line) {
    const std::string_view trimmed = TrimLuaWhitespace(StripLuaLineComment(line));
    if (trimmed.empty()) {
        return 0;
    }

    if (trimmed == "else" || trimmed == "end else" || StartsWithLuaKeyword(trimmed, "elseif")) {
        return 0;
    }

    if (trimmed == "repeat") {
        return 1;
    }

    if (trimmed == "end" || trimmed == "end," || StartsWithLuaKeyword(trimmed, "until")) {
        return -1;
    }

    if (trimmed == "do") {
        return 1;
    }

    if (StartsWithLuaKeyword(trimmed, "if") ||
        StartsWithLuaKeyword(trimmed, "for") ||
        StartsWithLuaKeyword(trimmed, "while") ||
        StartsWithLuaKeyword(trimmed, "function")) {
        return 1;
    }

    if (trimmed.find("= function(") != std::string_view::npos) {
        return 1;
    }

    return 0;
}

bool IsRenderedFunctionStartLine(std::string_view line) {
    const std::string_view trimmed = TrimLuaWhitespace(StripLuaLineComment(line));
    if (trimmed.empty()) {
        return false;
    }
    return StartsWithLuaKeyword(trimmed, "function") ||
           trimmed.find("= function(") != std::string_view::npos;
}

std::size_t FindRenderedFunctionBlockEnd(const std::vector<std::string>& lines,
                                         std::size_t start_line) {
    if (start_line >= lines.size()) {
        return lines.size();
    }

    int block_balance = 0;
    for (std::size_t line_index = start_line; line_index < lines.size(); ++line_index) {
        block_balance += GetRenderedLuaBlockDelta(lines[line_index]);
        if (block_balance <= 0) {
            return line_index;
        }
    }
    return lines.size();
}

bool ParseRenderedTableInitializerLine(std::string_view line,
                                       std::string* indent,
                                       std::string* lhs_prefix,
                                       std::string* name) {
    const std::size_t first_non_space = line.find_first_not_of(" \t");
    const std::string_view trimmed = TrimLuaWhitespace(line);
    if (trimmed.empty()) {
        return false;
    }

    std::string_view remainder = trimmed;
    std::string local_prefix;
    if (remainder.substr(0, 6) == "local ") {
        local_prefix = "local ";
        remainder = TrimLuaWhitespace(remainder.substr(6));
    }

    const std::size_t equal = remainder.find('=');
    if (equal == std::string_view::npos) {
        return false;
    }

    const std::string_view lhs = TrimLuaWhitespace(remainder.substr(0, equal));
    const std::string_view rhs = TrimLuaWhitespace(remainder.substr(equal + 1));
    if (!IsValidLuaIdentifier(lhs) || rhs != "{}") {
        return false;
    }

    if (indent != nullptr) {
        *indent = first_non_space == std::string_view::npos
                      ? std::string()
                      : std::string(line.substr(0, first_non_space));
    }
    if (lhs_prefix != nullptr) {
        *lhs_prefix = (indent != nullptr ? *indent
                                         : (first_non_space == std::string_view::npos
                                                ? std::string()
                                                : std::string(line.substr(0, first_non_space)))) +
                      local_prefix + std::string(lhs) + " = ";
    }
    if (name != nullptr) {
        *name = std::string(lhs);
    }
    return true;
}

bool ParseRenderedTableLiteralStartLine(std::string_view line,
                                        std::string* indent,
                                        std::string* lhs_prefix,
                                        std::string* name) {
    const std::size_t first_non_space = line.find_first_not_of(" \t");
    const std::string_view trimmed = TrimLuaWhitespace(line);
    if (trimmed.empty()) {
        return false;
    }

    std::string_view remainder = trimmed;
    std::string local_prefix;
    if (remainder.substr(0, 6) == "local ") {
        local_prefix = "local ";
        remainder = TrimLuaWhitespace(remainder.substr(6));
    }

    const std::size_t equal = remainder.find('=');
    if (equal == std::string_view::npos) {
        return false;
    }

    const std::string_view lhs = TrimLuaWhitespace(remainder.substr(0, equal));
    const std::string_view rhs = TrimLuaWhitespace(remainder.substr(equal + 1));
    if (!IsValidLuaIdentifier(lhs) || rhs != "{") {
        return false;
    }

    if (indent != nullptr) {
        *indent = first_non_space == std::string_view::npos
                      ? std::string()
                      : std::string(line.substr(0, first_non_space));
    }
    if (lhs_prefix != nullptr) {
        *lhs_prefix = (indent != nullptr ? *indent
                                         : (first_non_space == std::string_view::npos
                                                ? std::string()
                                                : std::string(line.substr(0, first_non_space)))) +
                      local_prefix + std::string(lhs) + " = ";
    }
    if (name != nullptr) {
        *name = std::string(lhs);
    }
    return true;
}

bool ParseRenderedTableFieldAssignment(std::string_view line,
                                       std::string_view table_name,
                                       std::string* field_key,
                                       std::string* rhs_expression,
                                       bool* rhs_is_function,
                                       int* array_index) {
    std::string_view trimmed = TrimLuaWhitespace(line);
    if (trimmed.size() < table_name.size() ||
        trimmed.substr(0, table_name.size()) != table_name) {
        return false;
    }
    trimmed.remove_prefix(table_name.size());
    trimmed = TrimLuaWhitespace(trimmed);
    if (trimmed.empty()) {
        return false;
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

    std::string rendered_key;
    int parsed_array_index = -1;
    if (lhs.front() == '.') {
        const std::string_view key = TrimLuaWhitespace(lhs.substr(1));
        if (!IsValidLuaIdentifier(key)) {
            return false;
        }
        rendered_key = std::string(key);
    } else if (lhs.front() == '[' && lhs.back() == ']') {
        const std::string_view inner = TrimLuaWhitespace(lhs.substr(1, lhs.size() - 2));
        bool numeric_inner = !inner.empty();
        for (char ch : inner) {
            if (std::isdigit(static_cast<unsigned char>(ch)) == 0) {
                numeric_inner = false;
                break;
            }
        }
        if (numeric_inner) {
            parsed_array_index = std::stoi(std::string(inner));
            rendered_key = "[" + std::string(inner) + "]";
        } else if (inner.size() >= 2 &&
                   ((inner.front() == '"' && inner.back() == '"') ||
                    (inner.front() == '\'' && inner.back() == '\''))) {
            const std::string_view unquoted = inner.substr(1, inner.size() - 2);
            if (IsValidLuaIdentifier(unquoted)) {
                rendered_key = std::string(unquoted);
            } else {
                rendered_key = "[" + std::string(inner) + "]";
            }
        } else {
            rendered_key = "[" + std::string(inner) + "]";
        }
    } else {
        return false;
    }

    if (field_key != nullptr) {
        *field_key = std::move(rendered_key);
    }
    if (rhs_expression != nullptr) {
        *rhs_expression = std::string(rhs);
    }
    if (rhs_is_function != nullptr) {
        *rhs_is_function =
            rhs.size() >= 9 && rhs.substr(0, 9) == std::string_view("function(");
    }
    if (array_index != nullptr) {
        *array_index = parsed_array_index;
    }
    return true;
}

bool ParseRenderedAssignmentLine(std::string_view line,
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
        if (part.empty() || !IsValidLuaIdentifier(part)) {
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

bool RemoveRenderedLuaEmptyIfBlocks(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 1 < lines->size();) {
        const std::string_view current =
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index]));
        const std::string_view next =
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 1]));
        if (StartsWithLuaKeyword(current, "if") && next == "end") {
            lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                         lines->begin() + static_cast<std::ptrdiff_t>(line_index + 2));
            changed = true;
            continue;
        }
        ++line_index;
    }

    return changed;
}

bool RemoveRenderedLuaNoOpAssignments(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines->size();) {
        std::string_view trimmed =
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index]));
        if (trimmed.starts_with("local ")) {
            ++line_index;
            continue;
        }

        const std::size_t equal = trimmed.find('=');
        if (equal == std::string_view::npos) {
            ++line_index;
            continue;
        }

        const std::string_view lhs = TrimLuaWhitespace(trimmed.substr(0, equal));
        const std::string_view rhs = TrimLuaWhitespace(trimmed.substr(equal + 1));
        if (lhs.empty() || rhs.empty() || lhs.find(',') != std::string_view::npos ||
            rhs.find(',') != std::string_view::npos || lhs != rhs) {
            ++line_index;
            continue;
        }

        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index));
        changed = true;
    }

    return changed;
}

std::size_t FindRenderedTableLiteralEnd(const std::vector<std::string>& lines,
                                        std::size_t start_line) {
    if (start_line >= lines.size()) {
        return lines.size();
    }

    int brace_balance = 0;
    bool in_single_quote = false;
    bool in_double_quote = false;
    for (std::size_t line_index = start_line; line_index < lines.size(); ++line_index) {
        const std::string_view line = lines[line_index];
        in_single_quote = false;
        in_double_quote = false;
        for (std::size_t i = 0; i < line.size(); ++i) {
            if (!in_single_quote && !in_double_quote && i + 1 < line.size() &&
                line[i] == '-' && line[i + 1] == '-') {
                break;
            }

            if (in_single_quote) {
                if (line[i] == '\\' && i + 1 < line.size()) {
                    ++i;
                } else if (line[i] == '\'') {
                    in_single_quote = false;
                }
                continue;
            }

            if (in_double_quote) {
                if (line[i] == '\\' && i + 1 < line.size()) {
                    ++i;
                } else if (line[i] == '"') {
                    in_double_quote = false;
                }
                continue;
            }

            if (line[i] == '\'') {
                in_single_quote = true;
                continue;
            }
            if (line[i] == '"') {
                in_double_quote = true;
                continue;
            }
            if (line[i] == '{') {
                ++brace_balance;
            } else if (line[i] == '}') {
                --brace_balance;
                if (brace_balance == 0) {
                    return line_index;
                }
            }
        }
    }

    return lines.size();
}

bool LocalizeRenderedLuaFunctionLocals(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        if (!IsRenderedFunctionStartLine((*lines)[line_index])) {
            continue;
        }

        const std::size_t function_end = FindRenderedFunctionBlockEnd(*lines, line_index);
        if (function_end >= lines->size() || function_end <= line_index) {
            continue;
        }

        std::unordered_set<std::string> seen_names;
        SeedRenderedFunctionParameters((*lines)[line_index], &seen_names);

        for (std::size_t probe = line_index + 1; probe < function_end; ++probe) {
            if (IsRenderedFunctionStartLine((*lines)[probe])) {
                const std::size_t nested_end = FindRenderedFunctionBlockEnd(*lines, probe);
                if (nested_end >= lines->size() || nested_end <= probe) {
                    break;
                }
                probe = nested_end;
                continue;
            }

            if (StripLuaLineComment((*lines)[probe]).find('{') != std::string_view::npos) {
                const std::size_t table_end = FindRenderedTableLiteralEnd(*lines, probe);
                if (table_end >= lines->size()) {
                    break;
                }
                if (table_end > probe) {
                    probe = table_end;
                    continue;
                }
            }

            std::vector<std::string> local_names;
            if (ParseRenderedLocalDeclarationLine((*lines)[probe], &local_names)) {
                for (const std::string& local_name : local_names) {
                    seen_names.insert(local_name);
                }
                continue;
            }

            bool is_local = false;
            std::vector<std::string> lhs_names;
            std::string rhs_expression;
            if (!ParseRenderedAssignmentLine((*lines)[probe],
                                            &is_local,
                                            &lhs_names,
                                            &rhs_expression) ||
                lhs_names.size() != 1) {
                continue;
            }

            const std::string& name = lhs_names.front();
            if (is_local) {
                seen_names.insert(name);
                continue;
            }

            if (name.empty() ||
                std::islower(static_cast<unsigned char>(name.front())) == 0 ||
                IsRenderTemporaryName(name)) {
                seen_names.insert(name);
                continue;
            }

            if (!seen_names.insert(name).second) {
                continue;
            }

            const std::size_t first_non_space = (*lines)[probe].find_first_not_of(" \t");
            if (first_non_space == std::string::npos) {
                continue;
            }

            (*lines)[probe].insert(first_non_space, "local ");
            changed = true;
        }
    }

    return changed;
}

bool RewriteTrailingRenderedTableBuilderTarget(std::vector<std::string>* lines,
                                               std::string_view table_name,
                                               std::string_view target_expression) {
    if (lines == nullptr || lines->empty() || table_name.empty() || target_expression.empty()) {
        return false;
    }

    for (std::size_t start = lines->size(); start-- > 0;) {
        std::string indent;
        std::string lhs_prefix;
        std::string parsed_name;
        if (!ParseRenderedTableInitializerLine((*lines)[start], &indent, &lhs_prefix, &parsed_name) ||
            parsed_name != table_name) {
            continue;
        }

        std::size_t cursor = start + 1;
        std::size_t entry_count = 0;
        while (cursor < lines->size()) {
            const std::string_view trimmed = TrimLuaWhitespace((*lines)[cursor]);
            if (trimmed.empty()) {
                return false;
            }

            std::string field_key;
            std::string rhs_expression;
            bool rhs_is_function = false;
            int array_index = -1;
            if (!ParseRenderedTableFieldAssignment((*lines)[cursor],
                                                  table_name,
                                                  &field_key,
                                                  &rhs_expression,
                                                  &rhs_is_function,
                                                  &array_index)) {
                return false;
            }

            const std::size_t first_non_space = (*lines)[cursor].find_first_not_of(" \t");
            if (first_non_space == std::string::npos ||
                (*lines)[cursor].compare(first_non_space, table_name.size(), table_name) != 0) {
                return false;
            }
            (*lines)[cursor].replace(first_non_space,
                                     table_name.size(),
                                     std::string(target_expression));

            if (rhs_is_function) {
                const std::size_t function_end = FindRenderedFunctionBlockEnd(*lines, cursor);
                if (function_end >= lines->size() || function_end < cursor) {
                    return false;
                }
                cursor = function_end + 1;
            } else if (rhs_expression == "{") {
                const std::size_t table_end = FindRenderedTableLiteralEnd(*lines, cursor);
                if (table_end >= lines->size() || table_end < cursor) {
                    return false;
                }
                cursor = table_end + 1;
            } else {
                ++cursor;
            }
            ++entry_count;
        }

        if (entry_count == 0) {
            return false;
        }

        const std::size_t first_non_space = (*lines)[start].find_first_not_of(" \t");
        const std::string start_indent =
            first_non_space == std::string::npos
                ? std::string()
                : std::string((*lines)[start].substr(0, first_non_space));
        (*lines)[start] = start_indent + std::string(target_expression) + " = {}";
        return true;
    }

    return false;
}

bool SimplifyRenderedLuaTableBuilders(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (int pass = 0; pass < 32; ++pass) {
        bool pass_changed = false;
        for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
            std::string indent;
            std::string lhs_prefix;
            std::string table_name;
            if (!ParseRenderedTableInitializerLine((*lines)[line_index],
                                                   &indent,
                                                   &lhs_prefix,
                                                   &table_name)) {
                continue;
            }

            std::vector<std::string> replacement_lines;
            replacement_lines.push_back(lhs_prefix + "{");

            std::size_t cursor = line_index + 1;
            std::size_t entry_count = 0;
            bool failed = false;
            int expected_array_index = 1;
            while (cursor < lines->size()) {
                const std::string_view trimmed = TrimLuaWhitespace((*lines)[cursor]);
                if (trimmed.empty()) {
                    break;
                }

                std::string field_key;
                std::string rhs_expression;
                bool rhs_is_function = false;
                int array_index = -1;
                std::size_t consumed_lines = 1;
                std::string conditional_expression;
                std::string else_expression;
                if (!ParseRenderedTableFieldAssignment((*lines)[cursor],
                                                      table_name,
                                                      &field_key,
                                                      &rhs_expression,
                                                      &rhs_is_function,
                                                      &array_index)) {
                    if (!TryParseRenderedTemporaryBackedTableField(*lines,
                                                                   cursor,
                                                                   table_name,
                                                                   &field_key,
                                                                   &rhs_expression,
                                                                   &rhs_is_function,
                                                                   &array_index,
                                                                   &consumed_lines)) {
                        break;
                    }
                }

                const bool is_array_entry =
                    array_index > 0 && array_index == expected_array_index;

                const bool rhs_is_table_literal = rhs_expression == "{";
                int rhs_paren_balance = 0;
                int rhs_brace_balance = 0;
                const bool rhs_starts_multiline_expression =
                    HasPositiveLuaExpressionBalance(rhs_expression,
                                                   &rhs_paren_balance,
                                                   &rhs_brace_balance);

                if (!rhs_is_function && !rhs_is_table_literal && rhs_starts_multiline_expression) {
                    failed = true;
                    break;
                }

                if (rhs_is_function) {
                    const std::size_t function_end =
                        FindRenderedFunctionBlockEnd(*lines, cursor);
                    if (function_end >= lines->size() || function_end < cursor) {
                        failed = true;
                        break;
                    }

                    if (is_array_entry) {
                        replacement_lines.push_back(indent + "  " + rhs_expression);
                    } else {
                        replacement_lines.push_back(indent + "  " + field_key + " = " +
                                                    rhs_expression);
                    }
                    for (std::size_t body_line = cursor + 1; body_line <= function_end;
                         ++body_line) {
                        std::string moved_line = "  " + (*lines)[body_line];
                        if (body_line == function_end) {
                            moved_line += ",";
                        }
                        replacement_lines.push_back(std::move(moved_line));
                    }
                    cursor = function_end + 1;
                } else if (rhs_is_table_literal) {
                    const std::size_t table_end =
                        FindRenderedTableLiteralEnd(*lines, cursor);
                    if (table_end >= lines->size() || table_end < cursor) {
                        failed = true;
                        break;
                    }

                    if (is_array_entry) {
                        replacement_lines.push_back(indent + "  {");
                    } else {
                        replacement_lines.push_back(indent + "  " + field_key + " = {");
                    }
                    for (std::size_t body_line = cursor + 1; body_line <= table_end;
                         ++body_line) {
                        std::string moved_line = "  " + (*lines)[body_line];
                        if (body_line == table_end) {
                            moved_line += ",";
                        }
                        replacement_lines.push_back(std::move(moved_line));
                    }
                    cursor = table_end + 1;
                } else {
                    if (is_array_entry) {
                        replacement_lines.push_back(indent + "  " + rhs_expression + ",");
                    } else {
                        replacement_lines.push_back(indent + "  " + field_key + " = " +
                                                    rhs_expression + ",");
                    }
                    cursor += consumed_lines;
                }
                if (is_array_entry) {
                    ++expected_array_index;
                }
                ++entry_count;
            }

            if (failed || entry_count == 0) {
                continue;
            }

            replacement_lines.push_back(indent + "}");
            lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                         lines->begin() + static_cast<std::ptrdiff_t>(cursor));
            lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                          replacement_lines.begin(),
                          replacement_lines.end());
            changed = true;
            pass_changed = true;
            break;
        }

        if (!pass_changed) {
            break;
        }
    }

    return changed;
}

bool SimplifyRenderedLuaReturnedTableBuilders(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (int pass = 0; pass < 32; ++pass) {
        bool pass_changed = false;
        for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
            std::string indent;
            std::string lhs_prefix;
            std::string table_name;
            bool parsed_initializer =
                ParseRenderedTableInitializerLine((*lines)[line_index],
                                                 &indent,
                                                 &lhs_prefix,
                                                 &table_name);
            bool parsed_literal = false;
            std::size_t literal_end = lines->size();
            if (!parsed_initializer) {
                parsed_literal = ParseRenderedTableLiteralStartLine((*lines)[line_index],
                                                                    &indent,
                                                                    &lhs_prefix,
                                                                    &table_name);
                if (!parsed_literal) {
                    continue;
                }
                literal_end = FindRenderedTableLiteralEnd(*lines, line_index);
                if (literal_end >= lines->size() || literal_end <= line_index) {
                    continue;
                }
            }

            const std::size_t scope_end =
                FindEnclosingRenderedFunctionBlockEnd(*lines, line_index);
            const std::size_t probe_limit =
                scope_end < lines->size() ? scope_end + 1 : lines->size();

            std::vector<std::string> prefix_lines;
            std::vector<std::string> replacement_lines;
            std::size_t cursor = parsed_literal ? literal_end + 1 : line_index + 1;
            std::size_t entry_count = 0;
            bool found_first_field = false;
            bool failed = false;
            int expected_array_index = 1;

            if (parsed_literal) {
                replacement_lines.push_back(indent + "return {");
                for (std::size_t body_line = line_index + 1; body_line < literal_end; ++body_line) {
                    replacement_lines.push_back((*lines)[body_line]);
                    ++entry_count;
                }
            }

            while (cursor < probe_limit) {
                if (IsRenderedExactReturnIdentifier((*lines)[cursor], table_name)) {
                    break;
                }

                if (IsAssignmentToIdentifier((*lines)[cursor], table_name)) {
                    failed = true;
                    break;
                }

                std::string field_key;
                std::string rhs_expression;
                bool rhs_is_function = false;
                int array_index = -1;
                std::size_t consumed_lines = 1;
                std::string conditional_expression;
                std::string else_expression;
                if (!ParseRenderedTableFieldAssignment((*lines)[cursor],
                                                      table_name,
                                                      &field_key,
                                                      &rhs_expression,
                                                      &rhs_is_function,
                                                      &array_index)) {
                    if (TryParseRenderedTemporaryBackedTableField(*lines,
                                                                  cursor,
                                                                  table_name,
                                                                  &field_key,
                                                                  &rhs_expression,
                                                                  &rhs_is_function,
                                                                  &array_index,
                                                                  &consumed_lines)) {
                        found_first_field = true;
                    } else {
                        if (!TryParseRenderedConditionalTableField(*lines,
                                                                   cursor,
                                                                   table_name,
                                                                   &conditional_expression,
                                                                   &field_key,
                                                                   &rhs_expression,
                                                                   &else_expression,
                                                                   &array_index,
                                                                   &consumed_lines)) {
                            if (found_first_field) {
                                failed = true;
                                break;
                            }
                            if (CountIdentifierUses((*lines)[cursor], table_name) != 0) {
                                failed = true;
                                break;
                            }
                            prefix_lines.push_back((*lines)[cursor]);
                            ++cursor;
                            continue;
                        }
                    }
                }

                found_first_field = true;
                const bool is_array_entry =
                    array_index > 0 && array_index == expected_array_index;
                const bool rhs_is_table_literal = rhs_expression == "{";
                int rhs_paren_balance = 0;
                int rhs_brace_balance = 0;
                const bool rhs_starts_multiline_expression =
                    HasPositiveLuaExpressionBalance(rhs_expression,
                                                   &rhs_paren_balance,
                                                   &rhs_brace_balance);

                if (!rhs_is_function && !rhs_is_table_literal && rhs_starts_multiline_expression) {
                    failed = true;
                    break;
                }

                if (!conditional_expression.empty()) {
                    if (entry_count == 0) {
                        replacement_lines.push_back(indent + "return {");
                    }
                    if (is_array_entry) {
                        replacement_lines.push_back(indent + "  (function()");
                    } else {
                        replacement_lines.push_back(indent + "  " + field_key +
                                                    " = (function()");
                    }
                    replacement_lines.push_back(indent + "    if " + conditional_expression +
                                                " then");
                    replacement_lines.push_back(indent + "      return " + rhs_expression);
                    replacement_lines.push_back(indent + "    else");
                    replacement_lines.push_back(indent + "      return " + else_expression);
                    replacement_lines.push_back(indent + "    end");
                    replacement_lines.push_back(indent + "  end)(),");
                    cursor += consumed_lines;
                } else if (rhs_is_function) {
                    const std::size_t function_end =
                        FindRenderedFunctionBlockEnd(*lines, cursor);
                    if (function_end >= lines->size() || function_end < cursor) {
                        failed = true;
                        break;
                    }

                    if (entry_count == 0) {
                        replacement_lines.push_back(indent + "return {");
                    }
                    if (is_array_entry) {
                        replacement_lines.push_back(indent + "  " + rhs_expression);
                    } else {
                        replacement_lines.push_back(indent + "  " + field_key + " = " +
                                                    rhs_expression);
                    }
                    for (std::size_t body_line = cursor + 1; body_line <= function_end;
                         ++body_line) {
                        std::string moved_line = "  " + (*lines)[body_line];
                        if (body_line == function_end) {
                            moved_line += ",";
                        }
                        replacement_lines.push_back(std::move(moved_line));
                    }
                    cursor = function_end + 1;
                } else if (rhs_is_table_literal) {
                    const std::size_t table_end =
                        FindRenderedTableLiteralEnd(*lines, cursor);
                    if (table_end >= lines->size() || table_end < cursor) {
                        failed = true;
                        break;
                    }

                    if (entry_count == 0) {
                        replacement_lines.push_back(indent + "return {");
                    }
                    if (is_array_entry) {
                        replacement_lines.push_back(indent + "  {");
                    } else {
                        replacement_lines.push_back(indent + "  " + field_key + " = {");
                    }
                    for (std::size_t body_line = cursor + 1; body_line <= table_end;
                         ++body_line) {
                        std::string moved_line = "  " + (*lines)[body_line];
                        if (body_line == table_end) {
                            moved_line += ",";
                        }
                        replacement_lines.push_back(std::move(moved_line));
                    }
                    cursor = table_end + 1;
                } else {
                    if (entry_count == 0) {
                        replacement_lines.push_back(indent + "return {");
                    }
                    if (is_array_entry) {
                        replacement_lines.push_back(indent + "  " + rhs_expression + ",");
                    } else {
                        replacement_lines.push_back(indent + "  " + field_key + " = " +
                                                    rhs_expression + ",");
                    }
                    cursor += consumed_lines;
                }

                if (is_array_entry) {
                    ++expected_array_index;
                }
                ++entry_count;
            }

            if (failed || entry_count == 0 || cursor >= probe_limit ||
                !IsRenderedExactReturnIdentifier((*lines)[cursor], table_name)) {
                continue;
            }

            replacement_lines.push_back(indent + "}");
            std::vector<std::string> merged_lines = prefix_lines;
            merged_lines.insert(merged_lines.end(),
                                replacement_lines.begin(),
                                replacement_lines.end());

            lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                         lines->begin() + static_cast<std::ptrdiff_t>(cursor) + 1);
            lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                          merged_lines.begin(),
                          merged_lines.end());
            changed = true;
            pass_changed = true;
            break;
        }

        if (!pass_changed) {
            break;
        }
    }

    return changed;
}

bool SimplifyRenderedLuaTableInitializerTemporaries(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (int pass = 0; pass < 32; ++pass) {
        bool pass_changed = false;
        for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
            std::string indent;
            std::string lhs_prefix;
            std::string table_name;
            if (!ParseRenderedTableInitializerLine((*lines)[line_index],
                                                   &indent,
                                                   &lhs_prefix,
                                                   &table_name)) {
                continue;
            }

            std::size_t use_line = lines->size();
            std::size_t use_count = 0;
            const std::size_t scope_end =
                FindEnclosingRenderedFunctionBlockEnd(*lines, line_index);
            const std::size_t probe_limit =
                scope_end < lines->size() ? scope_end + 1 : lines->size();
            for (std::size_t probe = line_index + 1; probe < probe_limit; ++probe) {
                if (IsAssignmentToIdentifier((*lines)[probe], table_name)) {
                    break;
                }

                const std::size_t line_uses =
                    CountIdentifierUses((*lines)[probe], table_name);
                if (line_uses == 0) {
                    continue;
                }
                use_count += line_uses;
                use_line = probe;
                if (use_count > 1) {
                    break;
                }
            }

            if (use_count == 0) {
                lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index));
                changed = true;
                pass_changed = true;
                break;
            }

            if (use_count != 1 || use_line >= lines->size()) {
                continue;
            }

            const std::size_t found = FindIdentifierUse((*lines)[use_line], table_name);
            if (found == std::string::npos) {
                continue;
            }

            const std::string prefix = (*lines)[use_line].substr(0, found);
            const std::string suffix =
                (*lines)[use_line].substr(found + table_name.size());
            const std::string_view trimmed_suffix = TrimLuaWhitespace(suffix);
            const bool requires_wrap =
                !trimmed_suffix.empty() &&
                (trimmed_suffix.front() == '.' || trimmed_suffix.front() == '[' ||
                 trimmed_suffix.front() == ':');

            (*lines)[use_line] = prefix + (requires_wrap ? "({})" : "{}") + suffix;
            lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index));
            changed = true;
            pass_changed = true;
            break;
        }

        if (!pass_changed) {
            break;
        }
    }

    return changed;
}

bool SimplifyRenderedLuaTableLiteralTemporaries(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (int pass = 0; pass < 32; ++pass) {
        bool pass_changed = false;
        for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
            std::string indent;
            std::string lhs_prefix;
            std::string table_name;
            if (!ParseRenderedTableLiteralStartLine((*lines)[line_index],
                                                    &indent,
                                                    &lhs_prefix,
                                                    &table_name)) {
                continue;
            }

            const std::size_t table_end = FindRenderedTableLiteralEnd(*lines, line_index);
            if (table_end >= lines->size() || table_end <= line_index) {
                continue;
            }

            std::size_t use_line = lines->size();
            std::size_t use_count = 0;
            const std::size_t scope_end =
                FindEnclosingRenderedFunctionBlockEnd(*lines, line_index);
            const std::size_t probe_limit =
                scope_end < lines->size() ? scope_end + 1 : lines->size();
            for (std::size_t probe = table_end + 1; probe < probe_limit; ++probe) {
                if (IsAssignmentToIdentifier((*lines)[probe], table_name)) {
                    break;
                }

                const std::size_t line_uses =
                    CountIdentifierUses((*lines)[probe], table_name);
                if (line_uses == 0) {
                    continue;
                }
                use_count += line_uses;
                use_line = probe;
                if (use_count > 1) {
                    break;
                }
            }

            if (use_count == 0) {
                lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                             lines->begin() + static_cast<std::ptrdiff_t>(table_end) + 1);
                changed = true;
                pass_changed = true;
                break;
            }

            if (use_count != 1 || use_line >= lines->size()) {
                continue;
            }

            const std::size_t found =
                FindIdentifierUse((*lines)[use_line], table_name);
            if (found == std::string::npos) {
                continue;
            }

            const std::string prefix = (*lines)[use_line].substr(0, found);
            const std::string suffix =
                (*lines)[use_line].substr(found + table_name.size());
            const std::string_view trimmed_suffix = TrimLuaWhitespace(suffix);
            const bool requires_wrap =
                !trimmed_suffix.empty() &&
                (trimmed_suffix.front() == '.' || trimmed_suffix.front() == '[' ||
                 trimmed_suffix.front() == ':');

            std::vector<std::string> replacement_lines;
            replacement_lines.push_back(prefix + (requires_wrap ? "({" : "{"));
            for (std::size_t body_line = line_index + 1; body_line < table_end; ++body_line) {
                replacement_lines.push_back((*lines)[body_line]);
            }
            replacement_lines.push_back((*lines)[table_end] +
                                        (requires_wrap ? ")" : std::string()) +
                                        suffix);

            lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                         lines->begin() + static_cast<std::ptrdiff_t>(table_end) + 1);
            const std::size_t adjusted_use_line =
                use_line - (table_end - line_index + 1);
            lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(adjusted_use_line));
            lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(adjusted_use_line),
                          replacement_lines.begin(),
                          replacement_lines.end());
            changed = true;
            pass_changed = true;
            break;
        }

        if (!pass_changed) {
            break;
        }
    }

    return changed;
}

bool SimplifyRenderedLuaFunctionTemporaries(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (int pass = 0; pass < 32; ++pass) {
        bool pass_changed = false;
        for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
            std::string temporary_name;
            std::string temporary_expression;
            if (!IsTemporaryAssignmentLine((*lines)[line_index],
                                           &temporary_name,
                                           &temporary_expression)) {
                continue;
            }

            if (temporary_expression.rfind("function(", 0) != 0) {
                continue;
            }

            const std::size_t end_line = FindRenderedFunctionBlockEnd(*lines, line_index);
            if (end_line >= lines->size() || end_line <= line_index) {
                continue;
            }

            std::size_t use_line = lines->size();
            std::size_t use_count = 0;
            const std::size_t scope_end =
                FindEnclosingRenderedFunctionBlockEnd(*lines, line_index);
            const std::size_t probe_limit =
                scope_end < lines->size() ? scope_end + 1 : lines->size();
            for (std::size_t probe = end_line + 1; probe < probe_limit; ++probe) {
                if (IsAssignmentToTemporary((*lines)[probe], temporary_name)) {
                    break;
                }
                const std::size_t line_uses =
                    CountIdentifierUses((*lines)[probe], temporary_name);
                if (line_uses == 0) {
                    continue;
                }
                use_count += line_uses;
                use_line = probe;
                if (use_count > 1) {
                    break;
                }
            }

            if (use_count == 0) {
                lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                             lines->begin() + static_cast<std::ptrdiff_t>(end_line) + 1);
                changed = true;
                pass_changed = true;
                break;
            }

            if (use_count != 1 || use_line >= lines->size()) {
                continue;
            }

            const std::size_t found =
                FindIdentifierUse((*lines)[use_line], temporary_name);
            if (found == std::string::npos) {
                continue;
            }

            const std::size_t equal = (*lines)[use_line].find('=');
            if (equal != std::string::npos && found <= equal) {
                continue;
            }

            const std::size_t function_begin = (*lines)[line_index].find("function(");
            if (function_begin == std::string::npos) {
                continue;
            }

            const std::string use_prefix = (*lines)[use_line].substr(0, found);
            const std::string use_suffix =
                (*lines)[use_line].substr(found + temporary_name.size());
            const std::string_view trimmed_suffix = TrimLuaWhitespace(use_suffix);
            const bool requires_call_wrap =
                !trimmed_suffix.empty() &&
                (trimmed_suffix.front() == '(' || trimmed_suffix.front() == '[' ||
                 trimmed_suffix.front() == '.' || trimmed_suffix.front() == ':');

            std::vector<std::string> rebuilt;
            rebuilt.reserve(lines->size() + (end_line - line_index));
            for (std::size_t source = 0; source < lines->size(); ++source) {
                if (source == line_index) {
                    source = end_line;
                    continue;
                }

                if (source == use_line) {
                    rebuilt.push_back(use_prefix +
                                      (requires_call_wrap ? "(" : std::string()) +
                                      (*lines)[line_index].substr(function_begin));
                    for (std::size_t body = line_index + 1; body <= end_line; ++body) {
                        std::string rebuilt_body_line = (*lines)[body];
                        if (body == end_line) {
                            if (requires_call_wrap) {
                                rebuilt_body_line += ")";
                            }
                            rebuilt_body_line += use_suffix;
                        }
                        rebuilt.push_back(std::move(rebuilt_body_line));
                    }
                    continue;
                }

                rebuilt.push_back((*lines)[source]);
            }

            *lines = std::move(rebuilt);
            changed = true;
            pass_changed = true;
            break;
        }

        if (!pass_changed) {
            break;
        }
    }

    return changed;
}

bool SimplifyRenderedLuaAssignedFunctionTemporaries(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (int pass = 0; pass < 32; ++pass) {
        bool pass_changed = false;
        for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
            std::string temporary_name;
            std::string temporary_expression;
            if (!IsTemporaryAssignmentLine((*lines)[line_index],
                                           &temporary_name,
                                           &temporary_expression)) {
                continue;
            }

            if (temporary_expression.rfind("function(", 0) != 0) {
                continue;
            }

            const std::size_t end_line = FindRenderedFunctionBlockEnd(*lines, line_index);
            if (end_line >= lines->size() || end_line <= line_index) {
                continue;
            }

            std::size_t use_line = lines->size();
            std::size_t use_count = 0;
            const std::size_t scope_end =
                FindEnclosingRenderedFunctionBlockEnd(*lines, line_index);
            const std::size_t probe_limit =
                scope_end < lines->size() ? scope_end + 1 : lines->size();
            for (std::size_t probe = end_line + 1; probe < probe_limit; ++probe) {
                if (IsAssignmentToTemporary((*lines)[probe], temporary_name)) {
                    break;
                }

                const std::size_t line_uses =
                    CountIdentifierUses((*lines)[probe], temporary_name);
                if (line_uses == 0) {
                    continue;
                }
                use_count += line_uses;
                use_line = probe;
                if (use_count > 1) {
                    break;
                }
            }

            if (use_count != 1 || use_line >= lines->size()) {
                continue;
            }

            const std::string_view use_trimmed =
                TrimLuaWhitespace(StripLuaLineComment((*lines)[use_line]));
            const std::size_t equal = use_trimmed.find('=');
            if (equal == std::string_view::npos) {
                continue;
            }

            const std::string_view rhs = TrimLuaWhitespace(use_trimmed.substr(equal + 1));
            if (rhs != temporary_name) {
                continue;
            }

            const std::size_t found =
                FindIdentifierUse((*lines)[use_line], temporary_name);
            if (found == std::string::npos) {
                continue;
            }

            const std::size_t function_begin = (*lines)[line_index].find("function(");
            if (function_begin == std::string::npos) {
                continue;
            }

            std::vector<std::string> rebuilt;
            rebuilt.reserve(lines->size() + (end_line - line_index));
            for (std::size_t source = 0; source < lines->size(); ++source) {
                if (source == line_index) {
                    source = end_line;
                    continue;
                }

                if (source == use_line) {
                    rebuilt.push_back((*lines)[use_line].substr(0, found) +
                                      (*lines)[line_index].substr(function_begin));
                    for (std::size_t body = line_index + 1; body <= end_line; ++body) {
                        rebuilt.push_back((*lines)[body]);
                    }
                    continue;
                }

                rebuilt.push_back((*lines)[source]);
            }

            *lines = std::move(rebuilt);
            changed = true;
            pass_changed = true;
            break;
        }

        if (!pass_changed) {
            break;
        }
    }

    return changed;
}

bool SimplifyRenderedLuaConditionalIifes(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 6 < lines->size(); ++line_index) {
        const std::string& start_line = (*lines)[line_index];
        const std::string_view start_trimmed =
            TrimLuaWhitespace(StripLuaLineComment(start_line));
        const std::size_t function_pos_trimmed = start_trimmed.find("(function()");
        if (function_pos_trimmed == std::string_view::npos) {
            continue;
        }
        const std::size_t function_pos = start_line.find("(function()");
        if (function_pos == std::string::npos) {
            continue;
        }

        const std::string_view if_trimmed =
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 1]));
        const std::string_view then_trimmed =
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 2]));
        const std::string_view else_trimmed =
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 3]));
        const std::string_view else_return_trimmed =
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 4]));
        const std::string_view end_trimmed =
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 5]));
        const std::string_view call_end_trimmed =
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 6]));

        if (!StartsWithLuaKeyword(if_trimmed, "if") || if_trimmed.size() < 8 ||
            !if_trimmed.ends_with("then") || !StartsWithLuaKeyword(then_trimmed, "return") ||
            else_trimmed != "else" || !StartsWithLuaKeyword(else_return_trimmed, "return") ||
            end_trimmed != "end" || call_end_trimmed != "end)(),") {
            continue;
        }

        const std::string_view condition =
            TrimLuaWhitespace(if_trimmed.substr(2, if_trimmed.size() - 2 - 4));
        const std::string_view then_expression = TrimLuaWhitespace(then_trimmed.substr(6));
        const std::string_view else_expression =
            TrimLuaWhitespace(else_return_trimmed.substr(6));
        if (condition.empty() || then_expression.empty() || else_expression.empty() ||
            !IsRenderedDefinitelyTruthyExpression(then_expression)) {
            continue;
        }

        const std::string prefix = start_line.substr(0, function_pos);
        const std::string combined =
            "((" + std::string(condition) + ") and (" + std::string(then_expression) +
            ") or (" + std::string(else_expression) + "))";
        (*lines)[line_index] = prefix + combined + ",";
        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index + 1),
                     lines->begin() + static_cast<std::ptrdiff_t>(line_index + 7));
        changed = true;
        if (line_index != 0) {
            --line_index;
        }
    }

    return changed;
}

bool SimplifyRenderedLuaConditionHeaders(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (std::string& line : *lines) {
        const std::string_view stripped = StripLuaLineComment(line);
        if (stripped.size() != line.size()) {
            continue;
        }

        const std::size_t first_non_space = line.find_first_not_of(" \t");
        const std::string indent =
            first_non_space == std::string::npos ? std::string() : line.substr(0, first_non_space);
        const std::string_view trimmed = TrimLuaWhitespace(stripped);

        std::string_view keyword;
        std::string_view suffix;
        std::string_view condition;
        if (trimmed.starts_with("if ") && trimmed.size() > 7 && trimmed.ends_with(" then")) {
            keyword = "if ";
            suffix = " then";
            condition = trimmed.substr(3, trimmed.size() - 3 - 5);
        } else if (trimmed.starts_with("elseif ") && trimmed.size() > 11 &&
                   trimmed.ends_with(" then")) {
            keyword = "elseif ";
            suffix = " then";
            condition = trimmed.substr(7, trimmed.size() - 7 - 5);
        } else if (trimmed.starts_with("while ") && trimmed.size() > 9 &&
                   trimmed.ends_with(" do")) {
            keyword = "while ";
            suffix = " do";
            condition = trimmed.substr(6, trimmed.size() - 6 - 3);
        } else if (trimmed.starts_with("until ") && trimmed.size() > 6) {
            keyword = "until ";
            suffix = "";
            condition = trimmed.substr(6);
        } else {
            continue;
        }

        bool local_changed = false;
        const std::string normalized =
            NormalizeRenderedConditionExpression(condition, &local_changed);
        if (!local_changed) {
            continue;
        }

        line = indent + std::string(keyword) + normalized + std::string(suffix);
        changed = true;
    }

    return changed;
}

bool SimplifyRenderedLuaExpressionParentheses(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (std::string& line : *lines) {
        std::string rewritten;
        rewritten.reserve(line.size());
        bool line_changed = false;
        for (std::size_t cursor = 0; cursor < line.size();) {
            if (line[cursor] != '(') {
                rewritten.push_back(line[cursor]);
                ++cursor;
                continue;
            }

            std::size_t outer_close = std::string::npos;
            if (!FindRenderedMatchingParen(line, cursor, &outer_close) ||
                outer_close <= cursor + 1) {
                rewritten.push_back(line[cursor]);
                ++cursor;
                continue;
            }

            const auto skip_spaces = [&](std::size_t index) {
                while (index < outer_close &&
                       std::isspace(static_cast<unsigned char>(line[index])) != 0) {
                    ++index;
                }
                return index;
            };
            const auto match_keyword = [&](std::size_t index,
                                           std::string_view keyword,
                                           std::size_t* after_index) {
                index = skip_spaces(index);
                if (index + keyword.size() > outer_close ||
                    std::string_view(line).substr(index, keyword.size()) != keyword) {
                    return false;
                }
                const std::size_t end = index + keyword.size();
                if (end < outer_close &&
                    std::isalnum(static_cast<unsigned char>(line[end])) != 0) {
                    return false;
                }
                if (after_index != nullptr) {
                    *after_index = end;
                }
                return true;
            };

            std::size_t first_open = skip_spaces(cursor + 1);
            if (first_open >= outer_close || line[first_open] != '(') {
                rewritten.append(line.substr(cursor, outer_close - cursor + 1));
                cursor = outer_close + 1;
                continue;
            }

            std::size_t first_close = std::string::npos;
            std::size_t second_open = std::string::npos;
            std::size_t second_close = std::string::npos;
            std::size_t third_open = std::string::npos;
            std::size_t third_close = std::string::npos;
            if (!FindRenderedMatchingParen(line, first_open, &first_close) ||
                first_close >= outer_close ||
                !match_keyword(first_close + 1, "and", &second_open)) {
                rewritten.append(line.substr(cursor, outer_close - cursor + 1));
                cursor = outer_close + 1;
                continue;
            }

            second_open = skip_spaces(second_open);
            if (second_open >= outer_close || line[second_open] != '(' ||
                !FindRenderedMatchingParen(line, second_open, &second_close) ||
                second_close >= outer_close ||
                !match_keyword(second_close + 1, "or", &third_open)) {
                rewritten.append(line.substr(cursor, outer_close - cursor + 1));
                cursor = outer_close + 1;
                continue;
            }

            third_open = skip_spaces(third_open);
            if (third_open >= outer_close || line[third_open] != '(' ||
                !FindRenderedMatchingParen(line, third_open, &third_close) ||
                third_close != outer_close - 1) {
                rewritten.append(line.substr(cursor, outer_close - cursor + 1));
                cursor = outer_close + 1;
                continue;
            }

            const std::string_view condition = TrimLuaWhitespace(
                std::string_view(line).substr(first_open + 1, first_close - first_open - 1));
            const std::string_view then_expression = TrimLuaWhitespace(
                std::string_view(line).substr(second_open + 1, second_close - second_open - 1));
            const std::string_view else_expression = TrimLuaWhitespace(
                std::string_view(line).substr(third_open + 1, third_close - third_open - 1));
            if (condition.empty() || then_expression.empty() || else_expression.empty()) {
                rewritten.append(line.substr(cursor, outer_close - cursor + 1));
                cursor = outer_close + 1;
                continue;
            }

            rewritten += "(";
            rewritten += std::string(condition);
            rewritten += " and ";
            rewritten += std::string(then_expression);
            rewritten += " or ";
            rewritten += std::string(else_expression);
            rewritten += ")";
            cursor = outer_close + 1;
            line_changed = true;
        }

        if (line_changed) {
            line = std::move(rewritten);
            changed = true;
        }
    }

    return changed;
}

bool SimplifyRenderedLuaDotAccess(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (std::string& line : *lines) {
        std::string rewritten;
        rewritten.reserve(line.size());
        for (std::size_t i = 0; i < line.size();) {
            if (i + 4 < line.size() && line[i] == '[' && line[i + 1] == '"') {
                std::size_t end_quote = line.find("\"]", i + 2);
                if (end_quote != std::string::npos) {
                    const std::string_view key(line.data() + i + 2, end_quote - (i + 2));
                    if (IsValidLuaIdentifier(key)) {
                        rewritten.push_back('.');
                        rewritten.append(key);
                        i = end_quote + 2;
                        changed = true;
                        continue;
                    }
                }
            }
            rewritten.push_back(line[i]);
            ++i;
        }
        line = std::move(rewritten);
    }
    return changed;
}

bool SimplifyRenderedLuaCallParentheses(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (std::string& line : *lines) {
        std::size_t start = 0;
        while (true) {
            const std::size_t open = line.find('(', start);
            if (open == std::string::npos || open + 2 >= line.size()) {
                break;
            }

            const std::size_t close = line.find(")(", open);
            if (close == std::string::npos) {
                break;
            }

            const std::string_view candidate(line.data() + open + 1,
                                             close - (open + 1));
            const std::string_view trimmed = TrimLuaWhitespace(candidate);
            const bool simple_dotted_chain =
                !trimmed.empty() &&
                std::all_of(trimmed.begin(), trimmed.end(), [](char ch) {
                    const unsigned char uch = static_cast<unsigned char>(ch);
                    return std::isalnum(uch) != 0 || ch == '_' || ch == '.';
                });
            if (!simple_dotted_chain) {
                start = open + 1;
                continue;
            }

            line.erase(close, 1);
            line.erase(open, 1);
            changed = true;
            start = open + trimmed.size();
        }
    }
    return changed;
}

bool DisambiguateRenderedLuaLeadingIifeStatements(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (std::string& line : *lines) {
        const std::string_view trimmed = TrimLuaWhitespace(StripLuaLineComment(line));
        if (!trimmed.starts_with("(function")) {
            continue;
        }

        const std::size_t first_non_space = line.find_first_not_of(" \t");
        if (first_non_space == std::string::npos || line[first_non_space] == ';') {
            continue;
        }

        line.insert(first_non_space, 1, ';');
        changed = true;
    }
    return changed;
}

bool NormalizeRenderedLuaWhitespace(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;

    for (std::string& line : *lines) {
        const std::size_t trimmed_size = TrimLuaWhitespace(line).size();
        std::size_t keep_size = line.size();
        while (keep_size > 0 &&
               std::isspace(static_cast<unsigned char>(line[keep_size - 1])) != 0) {
            --keep_size;
        }
        if (keep_size != line.size()) {
            line.erase(keep_size);
            changed = true;
        }
        if (keep_size == 0 && trimmed_size == 0 && !line.empty()) {
            line.clear();
            changed = true;
        }
    }

    std::vector<std::string> normalized;
    normalized.reserve(lines->size());
    bool previous_blank = true;
    for (const std::string& line : *lines) {
        const bool blank = TrimLuaWhitespace(line).empty();
        if (blank && previous_blank) {
            changed = true;
            continue;
        }
        normalized.push_back(line);
        previous_blank = blank;
    }

    while (!normalized.empty() && TrimLuaWhitespace(normalized.back()).empty()) {
        normalized.pop_back();
        changed = true;
    }

    if (normalized.size() != lines->size()) {
        *lines = std::move(normalized);
        return true;
    }

    if (changed) {
        *lines = std::move(normalized);
    }
    return changed;
}

void AppendIndentedStatement(std::vector<std::string>* lines,
                             std::string_view indent,
                             std::string_view statement) {
    std::string normalized_statement(statement);
    if (normalized_statement.find('\n') == std::string::npos) {
        const std::string_view trimmed = TrimLuaWhitespace(normalized_statement);
        if (!trimmed.empty() && trimmed.front() == '(') {
            normalized_statement.insert(0, ";");
        }
    }

    const std::vector<std::string> rendered_lines = SplitRenderedLines(normalized_statement);
    for (const std::string& rendered_line : rendered_lines) {
        lines->push_back(std::string(indent) + rendered_line);
    }
}

}  // namespace refii::lua
