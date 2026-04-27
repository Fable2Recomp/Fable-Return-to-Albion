#include "tools/lua_decompiler_internal.h"

#include <algorithm>
#include <cctype>
#include <string>
#include <string_view>
#include <vector>

namespace refii::lua::internal {
namespace {

bool IsIdentifierChar(char ch) {
    const unsigned char uch = static_cast<unsigned char>(ch);
    return std::isalnum(uch) != 0 || ch == '_';
}

}  // namespace

std::string GetLuaLineIndent(std::string_view line) {
    const std::size_t indent_count = line.find_first_not_of(" \t");
    if (indent_count == std::string_view::npos) {
        return std::string(line);
    }
    return std::string(line.substr(0, indent_count));
}

bool ContainsWord(std::string_view text, std::string_view word) {
    std::size_t search_from = 0;
    while (true) {
        const std::size_t found = text.find(word, search_from);
        if (found == std::string_view::npos) {
            return false;
        }

        const bool boundary_before =
            found == 0 || !IsIdentifierChar(text[found - 1]);
        const std::size_t end = found + word.size();
        const bool boundary_after =
            end >= text.size() || !IsIdentifierChar(text[end]);
        if (boundary_before && boundary_after) {
            return true;
        }

        search_from = found + 1;
    }
}

bool LooksLikeDecompilerTemporaryName(std::string_view name) {
    if (name.empty()) {
        return false;
    }

    if (name.front() == 'r' && name.size() > 1) {
        return std::all_of(name.begin() + 1, name.end(), [](char ch) {
            return std::isdigit(static_cast<unsigned char>(ch)) != 0;
        });
    }

    if (StartsWith(name, "l_")) {
        bool saw_digit = false;
        for (std::size_t i = 2; i < name.size(); ++i) {
            const char ch = name[i];
            if (ch == '_') {
                continue;
            }
            if (std::isdigit(static_cast<unsigned char>(ch)) == 0) {
                return false;
            }
            saw_digit = true;
        }
        return saw_digit;
    }

    return false;
}

std::string_view ExtractLeadingIdentifier(std::string_view trimmed) {
    if (trimmed.empty()) {
        return {};
    }

    if (!IsIdentifierChar(trimmed.front()) ||
        std::isdigit(static_cast<unsigned char>(trimmed.front())) != 0) {
        return {};
    }

    std::size_t index = 1;
    while (index < trimmed.size() && IsIdentifierChar(trimmed[index])) {
        ++index;
    }
    return trimmed.substr(0, index);
}

std::string_view ExtractLocalDeclarationName(std::string_view trimmed) {
    if (!StartsWith(trimmed, "local ")) {
        return {};
    }

    trimmed = TrimWhitespace(trimmed.substr(6));
    if (trimmed.empty()) {
        return {};
    }

    const std::size_t end = trimmed.find_first_of(" \t=,");
    const std::string_view name =
        end == std::string_view::npos ? trimmed : trimmed.substr(0, end);
    if (name.empty() || !IsIdentifierChar(name.front())) {
        return {};
    }
    return name;
}

bool ContainsIdentifierReference(std::string_view line, std::string_view identifier) {
    if (identifier.empty()) {
        return false;
    }

    std::size_t search_start = 0;
    while (search_start < line.size()) {
        const std::size_t found = line.find(identifier, search_start);
        if (found == std::string_view::npos) {
            return false;
        }

        const bool valid_left =
            found == 0 || !IsIdentifierChar(line[found - 1]);
        const std::size_t end = found + identifier.size();
        const bool valid_right =
            end >= line.size() || !IsIdentifierChar(line[end]);
        if (valid_left && valid_right) {
            return true;
        }

        search_start = found + identifier.size();
    }

    return false;
}

bool IsLuaYieldWaitLine(std::string_view line) {
    const std::string_view trimmed = TrimWhitespace(StripLineComment(line));
    return trimmed == "coroutine.yield()" || StartsWith(trimmed, "coroutine.yield(") ||
           trimmed.find(":WaitForTimeInSeconds(") != std::string_view::npos ||
           StartsWith(trimmed, "WaitForTimeInSeconds(");
}

std::string GetLuaLeadingIndent(std::string_view line) {
    const std::size_t first_non_ws = line.find_first_not_of(" \t");
    return first_non_ws == std::string_view::npos ? std::string()
                                                  : std::string(line.substr(0, first_non_ws));
}

bool IsSuppressedControlMarker(std::string_view line) {
    return StartsWith(TrimWhitespace(line), "-- refii-suppressed-control:");
}

bool IsSuppressedElseControlMarker(std::string_view line) {
    return TrimWhitespace(line) == "-- refii-suppressed-control: else";
}

std::size_t FindNextNonEmptyLuaLine(const std::vector<std::string>& lines,
                                    std::size_t start,
                                    bool include_suppressed_else) {
    for (std::size_t i = start; i < lines.size(); ++i) {
        const std::string_view raw_trimmed = TrimWhitespace(lines[i]);
        if (include_suppressed_else && IsSuppressedElseControlMarker(raw_trimmed)) {
            return i;
        }
        if (!TrimWhitespace(StripLineComment(lines[i])).empty()) {
            return i;
        }
    }
    return lines.size();
}

std::size_t FindFirstNonEmptyLuaLineInRange(const std::vector<std::string>& lines,
                                            std::size_t begin,
                                            std::size_t end) {
    for (std::size_t probe = begin; probe < end && probe < lines.size(); ++probe) {
        if (!TrimWhitespace(StripLineComment(lines[probe])).empty()) {
            return probe;
        }
    }
    return lines.size();
}

std::size_t FindLastNonEmptyLuaLineInRange(const std::vector<std::string>& lines,
                                           std::size_t begin,
                                           std::size_t end) {
    if (begin >= end || end > lines.size()) {
        return lines.size();
    }
    for (std::size_t probe = end; probe-- > begin;) {
        if (!TrimWhitespace(StripLineComment(lines[probe])).empty()) {
            return probe;
        }
    }
    return lines.size();
}

std::size_t FindPreviousNonEmptyLuaLine(const std::vector<std::string>& lines,
                                        std::size_t start) {
    if (start == 0 || start > lines.size()) {
        return lines.size();
    }
    for (std::size_t probe = start; probe-- > 0;) {
        if (!TrimWhitespace(StripLineComment(lines[probe])).empty()) {
            return probe;
        }
    }
    return lines.size();
}

std::size_t FindNextSignificantLuaLine(const std::vector<std::string>& lines,
                                       std::size_t start) {
    return FindNextNonEmptyLuaLine(lines, start);
}

bool ParseLuaIfCondition(std::string_view line, std::string* condition) {
    if (condition == nullptr) {
        return false;
    }

    const std::string_view trimmed = TrimWhitespace(StripLineComment(line));
    if (!StartsWith(trimmed, "if ") || !EndsWith(trimmed, " then")) {
        return false;
    }

    const std::string_view extracted =
        TrimWhitespace(trimmed.substr(3, trimmed.size() - 8));
    if (extracted.empty()) {
        return false;
    }

    *condition = std::string(extracted);
    return true;
}

bool ParseLuaWhileCondition(std::string_view line, std::string* condition) {
    if (condition == nullptr) {
        return false;
    }

    const std::string_view trimmed = TrimWhitespace(StripLineComment(line));
    if (!StartsWith(trimmed, "while ") || !EndsWith(trimmed, " do")) {
        return false;
    }

    const std::string_view extracted =
        TrimWhitespace(trimmed.substr(6, trimmed.size() - 9));
    if (extracted.empty()) {
        return false;
    }

    *condition = std::string(extracted);
    return true;
}

bool IsBareLuaCallSuffix(std::string_view text) {
    text = TrimWhitespace(StripLineComment(text));
    if (text.size() < 2 || text.front() != '(' || text.back() != ')' ||
        text.find('=') != std::string_view::npos) {
        return false;
    }

    int paren_depth = 0;
    int brace_depth = 0;
    int bracket_depth = 0;
    bool in_single_quote = false;
    bool in_double_quote = false;
    bool escaped = false;
    for (std::size_t i = 0; i < text.size(); ++i) {
        const char ch = text[i];
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
            --paren_depth;
            if (paren_depth == 0 && i + 1 != text.size()) {
                return false;
            }
            break;
        case '{':
            ++brace_depth;
            break;
        case '}':
            --brace_depth;
            break;
        case '[':
            ++bracket_depth;
            break;
        case ']':
            --bracket_depth;
            break;
        default:
            break;
        }

        if (paren_depth < 0 || brace_depth < 0 || bracket_depth < 0) {
            return false;
        }
    }

    return paren_depth == 0 && brace_depth == 0 && bracket_depth == 0;
}

int ComputeLuaBlockDelta(std::string_view line) {
    const std::string_view trimmed = TrimWhitespace(StripLineComment(line));
    if (trimmed.empty()) {
        return 0;
    }
    if (trimmed == "else" || trimmed == "end else" || StartsWith(trimmed, "elseif ")) {
        return 0;
    }
    if (trimmed == "repeat") {
        return 1;
    }
    if (trimmed == "end" || trimmed == "end," || StartsWith(trimmed, "until ")) {
        return -1;
    }
    if (trimmed == "do") {
        return 1;
    }
    if (StartsWith(trimmed, "if ") || StartsWith(trimmed, "for ") ||
        StartsWith(trimmed, "while ") || StartsWith(trimmed, "function ") ||
        StartsWith(trimmed, "local function ") || StartsWith(trimmed, "return function(") ||
        StartsWith(trimmed, "return function ") ||
        trimmed.find("= function(") != std::string_view::npos ||
        trimmed.find("= function ") != std::string_view::npos) {
        return 1;
    }
    return 0;
}

std::size_t FindBalancedLuaBlockEnd(const std::vector<std::string>& lines, std::size_t start) {
    if (start >= lines.size()) {
        return lines.size();
    }
    int balance = ComputeLuaBlockDelta(lines[start]);
    if (balance <= 0) {
        return lines.size();
    }
    for (std::size_t probe = start + 1; probe < lines.size(); ++probe) {
        balance += ComputeLuaBlockDelta(lines[probe]);
        if (balance == 0) {
            return probe;
        }
    }
    return lines.size();
}

void AppendReindentedLuaBlock(const std::vector<std::string>& lines,
                              std::size_t begin,
                              std::size_t end,
                              std::string_view old_prefix,
                              std::string_view new_prefix,
                              std::vector<std::string>* output,
                              bool skip_suppressed_markers) {
    if (output == nullptr) {
        return;
    }
    for (std::size_t probe = begin; probe < end; ++probe) {
        if (skip_suppressed_markers && IsSuppressedControlMarker(lines[probe])) {
            continue;
        }

        std::string moved_line = lines[probe];
        if (old_prefix.empty()) {
            output->push_back(std::move(moved_line));
            continue;
        }

        const std::string current_indent = GetLuaLineIndent(moved_line);
        if (current_indent.starts_with(old_prefix)) {
            std::string rewritten =
                std::string(new_prefix) + std::string(current_indent.substr(old_prefix.size())) +
                std::string(moved_line.substr(current_indent.size()));
            output->push_back(std::move(rewritten));
        } else {
            output->push_back(std::move(moved_line));
        }
    }
}

}  // namespace refii::lua::internal
