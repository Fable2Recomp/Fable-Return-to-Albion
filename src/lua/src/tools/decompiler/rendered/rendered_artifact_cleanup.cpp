#include "tools/decompiler/rendered/rendered_lua_text.h"

namespace refii::lua {

bool SimplifyRenderedLuaInvalidAnonymousCallArtifacts(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto is_bare_call_suffix = [](std::string_view text) {
        text = TrimLuaWhitespace(StripLuaLineComment(text));
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
    };

    const auto extract_inline_anonymous_call_suffix =
        [&](std::string_view trimmed,
            std::string* header_without_suffix,
            std::string* call_suffix) {
            if (!trimmed.starts_with("function(") && !trimmed.starts_with("function ")) {
                return false;
            }

            const std::size_t open = trimmed.find('(');
            if (open == std::string_view::npos) {
                return false;
            }

            int paren_depth = 0;
            bool in_single_quote = false;
            bool in_double_quote = false;
            bool escaped = false;
            std::size_t close = std::string_view::npos;
            for (std::size_t i = open; i < trimmed.size(); ++i) {
                const char ch = trimmed[i];
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
                if (ch == '(') {
                    ++paren_depth;
                } else if (ch == ')') {
                    --paren_depth;
                    if (paren_depth == 0) {
                        close = i;
                        break;
                    }
                }
            }

            if (close == std::string_view::npos) {
                return false;
            }

            const std::string_view suffix = TrimLuaWhitespace(trimmed.substr(close + 1));
            if (!is_bare_call_suffix(suffix)) {
                return false;
            }

            if (header_without_suffix != nullptr) {
                *header_without_suffix = std::string(trimmed.substr(0, close + 1));
            }
            if (call_suffix != nullptr) {
                *call_suffix = std::string(suffix);
            }
            return true;
        };

    bool changed = false;

    for (std::size_t line_index = 0; line_index < lines->size();) {
        const std::string_view trimmed =
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index]));
        if (extract_inline_anonymous_call_suffix(trimmed, nullptr, nullptr) &&
            trimmed.find("end") == std::string_view::npos) {
            lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index));
            changed = true;
            continue;
        }
        ++line_index;
    }

    for (std::size_t line_index = 0; line_index < lines->size();) {
        std::string_view trimmed =
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index]));
        if (trimmed.starts_with(';')) {
            trimmed = TrimLuaWhitespace(trimmed.substr(1));
        }
        if ((trimmed == "(function()" || trimmed == "(function ()") &&
            line_index + 1 < lines->size()) {
            const std::string_view next_trimmed =
                TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 1]));
            if (next_trimmed.starts_with("end)") &&
                is_bare_call_suffix(next_trimmed.substr(4))) {
                lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                             lines->begin() + static_cast<std::ptrdiff_t>(line_index + 2));
                changed = true;
                continue;
            }
        }
        if (!trimmed.starts_with("(function(") && !trimmed.starts_with("(function ")) {
            ++line_index;
            continue;
        }

        const std::size_t function_end = FindRenderedFunctionBlockEnd(*lines, line_index);
        if (function_end >= lines->size() || function_end <= line_index) {
            ++line_index;
            continue;
        }

        bool empty_body = true;
        for (std::size_t probe = line_index + 1; probe < function_end; ++probe) {
            if (!TrimLuaWhitespace(StripLuaLineComment((*lines)[probe])).empty()) {
                empty_body = false;
                break;
            }
        }
        if (!empty_body) {
            ++line_index;
            continue;
        }

        std::size_t erase_end = function_end;
        const std::string_view end_trimmed =
            TrimLuaWhitespace(StripLuaLineComment((*lines)[function_end]));
        bool has_call_suffix = false;
        if (end_trimmed.starts_with("end)")) {
            has_call_suffix = is_bare_call_suffix(end_trimmed.substr(4));
        } else if (end_trimmed == "end") {
            std::size_t suffix_line = function_end + 1;
            while (suffix_line < lines->size() &&
                   TrimLuaWhitespace(StripLuaLineComment((*lines)[suffix_line])).empty()) {
                ++suffix_line;
            }
            if (suffix_line < lines->size() &&
                is_bare_call_suffix((*lines)[suffix_line])) {
                has_call_suffix = true;
                erase_end = suffix_line;
            }
        }
        if (!has_call_suffix) {
            ++line_index;
            continue;
        }

        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                     lines->begin() + static_cast<std::ptrdiff_t>(erase_end + 1));
        changed = true;
    }

    return changed;
}

}  // namespace refii::lua
