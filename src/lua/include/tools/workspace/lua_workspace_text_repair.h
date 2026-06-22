#pragma once

#include "tools/decompiler/lua_decompiler_internal.h"

#include <cctype>
#include <cstddef>
#include <initializer_list>
#include <string>
#include <string_view>
#include <vector>

namespace refii::lua::workspace::text_repair {

inline bool IsIdentifierChar(char ch) {
    const unsigned char uch = static_cast<unsigned char>(ch);
    return std::isalnum(uch) != 0 || ch == '_';
}

inline bool ContainsFunctionBlockKeyword(std::string_view trimmed) {
    bool in_single_quote = false;
    bool in_double_quote = false;
    for (std::size_t index = 0; index < trimmed.size(); ++index) {
        const char ch = trimmed[index];
        if (in_single_quote) {
            if (ch == '\\' && index + 1 < trimmed.size()) {
                ++index;
            } else if (ch == '\'') {
                in_single_quote = false;
            }
            continue;
        }
        if (in_double_quote) {
            if (ch == '\\' && index + 1 < trimmed.size()) {
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

        constexpr std::string_view kFunction = "function";
        if (index + kFunction.size() > trimmed.size() ||
            trimmed.substr(index, kFunction.size()) != kFunction) {
            continue;
        }
        const bool left_boundary =
            index == 0 ||
            (!IsIdentifierChar(trimmed[index - 1]) && trimmed[index - 1] != '.');
        const std::size_t after = index + kFunction.size();
        const bool right_boundary =
            after >= trimmed.size() || !IsIdentifierChar(trimmed[after]);
        if (left_boundary && right_boundary) {
            return true;
        }
    }
    return false;
}

inline bool LuaLineStartsBlock(std::string_view trimmed) {
    return (internal::StartsWith(trimmed, "if ") &&
            internal::EndsWith(trimmed, " then")) ||
           (internal::StartsWith(trimmed, "while ") &&
            internal::EndsWith(trimmed, " do")) ||
           (internal::StartsWith(trimmed, "for ") &&
            internal::EndsWith(trimmed, " do")) ||
           internal::StartsWith(trimmed, "function ") ||
           internal::StartsWith(trimmed, "local function ") ||
           ContainsFunctionBlockKeyword(trimmed) ||
           trimmed == "repeat";
}

inline bool FindLuaMatchingEnd(const std::vector<std::string>& lines,
                               std::size_t block_start,
                               std::size_t* block_end) {
    if (block_end == nullptr || block_start >= lines.size()) {
        return false;
    }

    int nested_depth = 0;
    for (std::size_t line_index = block_start + 1; line_index < lines.size();
         ++line_index) {
        const std::string_view trimmed =
            internal::TrimWhitespace(internal::StripLineComment(lines[line_index]));
        if (LuaLineStartsBlock(trimmed)) {
            ++nested_depth;
            continue;
        }
        if (trimmed == "end" || internal::StartsWith(trimmed, "until ")) {
            if (nested_depth == 0) {
                *block_end = line_index;
                return true;
            }
            --nested_depth;
        }
    }
    return false;
}

inline bool ReplaceAllLiteral(std::string* source,
                              std::string_view from,
                              std::string_view to) {
    if (source == nullptr || from.empty()) {
        return false;
    }

    bool changed = false;
    std::size_t cursor = 0;
    while ((cursor = source->find(from, cursor)) != std::string::npos) {
        source->replace(cursor, from.size(), to);
        cursor += to.size();
        changed = true;
    }
    return changed;
}

struct LiteralRule {
    std::string_view from;
    std::string_view to;
};

inline bool ApplyLiteralRules(std::string* source,
                              std::initializer_list<LiteralRule> rules) {
    bool changed = false;
    for (const LiteralRule& rule : rules) {
        changed |= ReplaceAllLiteral(source, rule.from, rule.to);
    }
    return changed;
}

inline bool ReplaceFunctionByHeader(std::string* source,
                                    std::string_view header,
                                    std::string_view replacement) {
    if (source == nullptr || header.empty() || replacement.empty()) {
        return false;
    }

    bool had_trailing_newline = false;
    std::vector<std::string> lines =
        internal::SplitLines(*source, &had_trailing_newline);
    std::vector<std::string> replacement_lines =
        internal::SplitLines(std::string(replacement), nullptr);
    if (!replacement_lines.empty() && replacement_lines.back().empty()) {
        replacement_lines.pop_back();
    }
    if (replacement_lines.empty()) {
        return false;
    }

    for (std::size_t line_index = 0; line_index < lines.size(); ++line_index) {
        if (internal::TrimWhitespace(lines[line_index]) != header) {
            continue;
        }
        std::size_t function_end = 0;
        if (!FindLuaMatchingEnd(lines, line_index, &function_end) ||
            function_end < line_index) {
            return false;
        }
        lines.erase(lines.begin() + static_cast<std::ptrdiff_t>(line_index),
                    lines.begin() + static_cast<std::ptrdiff_t>(function_end + 1));
        lines.insert(lines.begin() + static_cast<std::ptrdiff_t>(line_index),
                     replacement_lines.begin(),
                     replacement_lines.end());
        *source = internal::JoinLines(lines, had_trailing_newline);
        return true;
    }
    return false;
}

}  // namespace refii::lua::workspace::text_repair
