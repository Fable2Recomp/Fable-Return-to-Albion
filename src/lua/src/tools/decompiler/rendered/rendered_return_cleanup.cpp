#include "tools/decompiler/rendered/rendered_lua_text.h"

#include "tools/decompiler/rendered/rendered_cleanup_common_helpers.h"

#include <algorithm>
#include <cstddef>
#include <string>
#include <string_view>
#include <vector>

namespace refii::lua {
namespace {

bool ReturnedLuaReturnHasTopLevelComma(std::string_view value) {
    bool in_single_quote = false;
    bool in_double_quote = false;
    int paren_depth = 0;
    int brace_depth = 0;
    int bracket_depth = 0;
    for (std::size_t index = 0; index < value.size(); ++index) {
        const char ch = value[index];
        if (in_single_quote) {
            if (ch == '\\' && index + 1 < value.size()) {
                ++index;
            } else if (ch == '\'') {
                in_single_quote = false;
            }
            continue;
        }
        if (in_double_quote) {
            if (ch == '\\' && index + 1 < value.size()) {
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
        if (paren_depth == 0 && brace_depth == 0 && bracket_depth == 0 && ch == ',') {
            return true;
        }
    }
    return false;
}

bool FindReturnedLuaCallOpen(std::string_view expression, std::size_t* open_out) {
    if (open_out == nullptr) {
        return false;
    }

    bool in_single_quote = false;
    bool in_double_quote = false;
    int paren_depth = 0;
    int brace_depth = 0;
    int bracket_depth = 0;
    for (std::size_t index = expression.size(); index > 0; --index) {
        const char ch = expression[index - 1];
        if (in_single_quote) {
            if (ch == '\'' && (index < 2 || expression[index - 2] != '\\')) {
                in_single_quote = false;
            }
            continue;
        }
        if (in_double_quote) {
            if (ch == '"' && (index < 2 || expression[index - 2] != '\\')) {
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
        if (ch == ')') {
            ++paren_depth;
            continue;
        }
        if (ch == '(') {
            if (paren_depth == 1 && brace_depth == 0 && bracket_depth == 0) {
                *open_out = index - 1;
                return true;
            }
            paren_depth = std::max(0, paren_depth - 1);
            continue;
        }
        if (ch == '}') {
            ++brace_depth;
            continue;
        }
        if (ch == '{') {
            brace_depth = std::max(0, brace_depth - 1);
            continue;
        }
        if (ch == ']') {
            ++bracket_depth;
            continue;
        }
        if (ch == '[') {
            bracket_depth = std::max(0, bracket_depth - 1);
            continue;
        }
    }
    return false;
}

std::size_t FindReturnedLuaCalleeStart(std::string_view text, std::size_t open_paren) {
    int paren_depth = 0;
    int bracket_depth = 0;
    for (std::size_t pos = open_paren; pos > 0;) {
        --pos;
        const char ch = text[pos];
        if (ch == ')') {
            ++paren_depth;
            continue;
        }
        if (ch == ']') {
            ++bracket_depth;
            continue;
        }
        if (ch == '(') {
            if (paren_depth > 0) {
                --paren_depth;
                continue;
            }
            return pos + 1;
        }
        if (ch == '[') {
            if (bracket_depth > 0) {
                --bracket_depth;
                continue;
            }
            return pos + 1;
        }
        if (paren_depth > 0 || bracket_depth > 0) {
            continue;
        }

        switch (ch) {
        case ' ':
        case '\t':
        case ',':
        case ';':
        case '+':
        case '-':
        case '*':
        case '/':
        case '%':
        case '^':
        case '#':
        case '=':
        case '<':
        case '>':
        case '~':
        case '&':
        case '|':
        case '{':
        case '}':
            return pos + 1;
        default:
            break;
        }
    }
    return std::size_t{0};
}

bool TryParseReturnedLuaCallExpression(std::string_view expression_view,
                                       std::string_view* normalized_expression_out,
                                       std::string_view* callee_out) {
    if (normalized_expression_out == nullptr || callee_out == nullptr) {
        return false;
    }

    expression_view = TrimLuaWhitespace(expression_view);
    if (expression_view.empty() || ReturnedLuaReturnHasTopLevelComma(expression_view)) {
        return false;
    }

    std::size_t open_paren = std::string_view::npos;
    if (!expression_view.ends_with(')') ||
        !FindReturnedLuaCallOpen(expression_view, &open_paren)) {
        return false;
    }

    std::size_t close_paren = std::string_view::npos;
    if (!FindRenderedMatchingParen(expression_view, open_paren, &close_paren) ||
        close_paren + 1 != expression_view.size()) {
        return false;
    }

    const std::size_t callee_start = FindReturnedLuaCalleeStart(expression_view, open_paren);
    if (callee_start >= open_paren) {
        return false;
    }

    const std::string_view callee =
        TrimLuaWhitespace(expression_view.substr(callee_start, open_paren - callee_start));
    std::string parsed_callee;
    std::vector<std::string> parsed_arguments;
    if (callee.empty() ||
        !ParseRenderedSingleCallLine(expression_view, &parsed_callee, &parsed_arguments)) {
        return false;
    }

    *normalized_expression_out = expression_view;
    *callee_out = callee;
    return true;
}

bool TryExtractRenderedFunctionHeaderName(std::string_view line, std::string* name_out) {
    if (name_out == nullptr) {
        return false;
    }

    std::string_view trimmed = TrimLuaWhitespace(StripLuaLineComment(line));
    if (trimmed.empty()) {
        return false;
    }

    std::string_view name_expression;
    if (trimmed.starts_with("function ")) {
        name_expression = TrimLuaWhitespace(trimmed.substr(9));
    } else {
        const std::size_t function_pos = trimmed.find("function");
        if (function_pos == std::string_view::npos) {
            return false;
        }
        std::string_view lhs = TrimLuaWhitespace(trimmed.substr(0, function_pos));
        if (lhs.empty() || !lhs.ends_with('=')) {
            return false;
        }
        lhs = TrimLuaWhitespace(lhs.substr(0, lhs.size() - 1));
        if (lhs.starts_with("local ")) {
            lhs = TrimLuaWhitespace(lhs.substr(6));
        }
        name_expression = lhs;
    }

    const std::size_t open_paren = name_expression.find('(');
    if (open_paren != std::string_view::npos) {
        name_expression = TrimLuaWhitespace(name_expression.substr(0, open_paren));
    }

    const std::size_t last_access = name_expression.find_last_of(".:");
    if (last_access != std::string_view::npos && last_access + 1 < name_expression.size()) {
        name_expression = name_expression.substr(last_access + 1);
    }

    if (name_expression.empty() || !IsValidLuaIdentifier(name_expression)) {
        return false;
    }

    *name_out = std::string(name_expression);
    return true;
}

bool IsRenderedPredicateFunctionName(std::string_view function_name) {
    for (std::string_view prefix :
         {"Is", "Has", "Can", "Should", "Needs", "Need", "Want", "Wants", "Must", "Was",
          "Were", "Does", "Did", "Check"}) {
        if (function_name.size() > prefix.size() && function_name.starts_with(prefix)) {
            return true;
        }
    }
    return false;
}

bool LooksLikeRenderedBooleanPredicateExpression(std::string_view expression) {
    expression = TrimLuaWhitespace(expression);
    if (expression.empty()) {
        return false;
    }

    const std::string normalized =
        StripRenderedWholeParentheses(expression, nullptr);
    if (normalized.empty()) {
        return false;
    }

    if (normalized.starts_with("not ")) {
        return true;
    }

    std::vector<std::string> parts;
    if (SplitRenderedTopLevelBooleanChain(normalized, "and", &parts) && parts.size() >= 2) {
        return true;
    }
    if (SplitRenderedTopLevelBooleanChain(normalized, "or", &parts) && parts.size() >= 2) {
        return true;
    }

    for (std::string_view token : {"==", "~=", "<=", ">=", "<", ">"}) {
        if (normalized.find(token) != std::string::npos) {
            return true;
        }
    }

    return false;
}

}  // namespace

bool ExpandRenderedLuaPredicateBooleanReturnsForParity(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 2 < lines->size(); ++line_index) {
        std::string function_name;
        if (!TryExtractRenderedFunctionHeaderName((*lines)[line_index], &function_name) ||
            !IsRenderedPredicateFunctionName(function_name)) {
            continue;
        }

        const std::size_t block_end = FindRenderedFunctionBlockEnd(*lines, line_index);
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
            if (StartsWithLuaKeyword(trimmed, "return")) {
                if (return_line != lines->size()) {
                    return_line = lines->size();
                    break;
                }
                return_line = probe;
                continue;
            }

            bool assign_is_local = false;
            std::string assign_lhs;
            std::string assign_rhs;
            if (!ParseRenderedSingleAssignmentLine((*lines)[probe],
                                                   &assign_is_local,
                                                   &assign_lhs,
                                                   &assign_rhs) ||
                !assign_is_local) {
                return_line = lines->size();
                break;
            }
        }
        if (return_line >= block_end) {
            continue;
        }

        const std::string_view trimmed_return =
            TrimLuaWhitespace(StripLuaLineComment((*lines)[return_line]));
        if (!trimmed_return.starts_with("return ")) {
            continue;
        }

        const std::string return_expression = StripRenderedWholeParentheses(
            TrimLuaWhitespace(trimmed_return.substr(7)),
            nullptr);
        if (return_expression.empty() ||
            !LooksLikeRenderedBooleanPredicateExpression(return_expression)) {
            continue;
        }

        const std::string inner_indent(GetRenderedLuaIndent((*lines)[return_line]));
        const std::string nested_indent = inner_indent + "  ";
        const std::vector<std::string> replacement_lines = {
            inner_indent + "if " + return_expression + " then",
            nested_indent + "return true",
            inner_indent + "end",
            inner_indent + "return false",
        };
        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(return_line));
        lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(return_line),
                      replacement_lines.begin(),
                      replacement_lines.end());
        changed = true;
        if (line_index != 0) {
            --line_index;
        }
    }

    return changed;
}

bool ExpandRenderedLuaNegatedBooleanReturnsForParity(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        const std::string_view stripped = StripLuaLineComment((*lines)[line_index]);
        const std::string_view trimmed = TrimLuaWhitespace(stripped);
        if (!trimmed.starts_with("return ")) {
            continue;
        }
        std::string expression = StripRenderedWholeParentheses(
            TrimLuaWhitespace(trimmed.substr(7)),
            nullptr);
        if (!StartsWithLuaKeyword(expression, "not")) {
            continue;
        }
        expression = StripRenderedWholeParentheses(
            TrimLuaWhitespace(std::string_view(expression).substr(3)),
            nullptr);
        if (expression.empty()) {
            continue;
        }

        const std::string indent(GetRenderedLuaIndent((*lines)[line_index]));
        const std::string nested_indent = indent + "  ";
        const std::vector<std::string> replacement_lines = {
            indent + "if " + expression + " then",
            nested_indent + "return false",
            indent + "end",
            indent + "return true",
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

bool UnwrapRenderedLuaReturnedCallsForExactParity(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (std::string& line : *lines) {
        const std::string_view stripped = StripLuaLineComment(line);
        const std::string_view comment =
            stripped.size() < line.size() ? std::string_view(line).substr(stripped.size())
                                          : std::string_view();
        const std::string_view trimmed = TrimLuaWhitespace(stripped);
        if (!StartsWithLuaKeyword(trimmed, "return")) {
            continue;
        }

        const std::string_view expression_view = TrimLuaWhitespace(trimmed.substr(6));
        std::size_t outer_close = std::string_view::npos;
        if (expression_view.size() < 3 || expression_view.front() != '(' ||
            !FindRenderedMatchingParen(expression_view, 0, &outer_close) ||
            outer_close + 1 != expression_view.size()) {
            continue;
        }

        const std::string_view inner_expression =
            TrimLuaWhitespace(expression_view.substr(1, expression_view.size() - 2));
        std::string_view normalized_expression;
        std::string_view callee;
        if (!TryParseReturnedLuaCallExpression(inner_expression,
                                               &normalized_expression,
                                               &callee)) {
            continue;
        }

        const std::string indent(std::string(GetRenderedLuaIndent(line)));
        line = indent + "return " + std::string(normalized_expression) + std::string(comment);
        changed = true;
    }

    return changed;
}

bool WrapRenderedLuaReturnedCallsForExactParity(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (std::string& line : *lines) {
        const std::string_view stripped = StripLuaLineComment(line);
        const std::string_view comment =
            stripped.size() < line.size() ? std::string_view(line).substr(stripped.size())
                                          : std::string_view();
        const std::string_view trimmed = TrimLuaWhitespace(stripped);
        if (!StartsWithLuaKeyword(trimmed, "return")) {
            continue;
        }

        const std::string_view expression_view = TrimLuaWhitespace(trimmed.substr(6));
        std::size_t outer_close = std::string_view::npos;
        if (expression_view.empty() ||
            (expression_view.front() == '(' &&
             FindRenderedMatchingParen(expression_view, 0, &outer_close) &&
             outer_close + 1 == expression_view.size())) {
            continue;
        }

        std::string_view normalized_expression;
        std::string_view callee;
        if (!TryParseReturnedLuaCallExpression(expression_view,
                                               &normalized_expression,
                                               &callee)) {
            continue;
        }

        const std::string indent(std::string(GetRenderedLuaIndent(line)));
        line = indent + "return (" + std::string(normalized_expression) + ")" +
               std::string(comment);
        changed = true;
    }

    return changed;
}

}  // namespace refii::lua
