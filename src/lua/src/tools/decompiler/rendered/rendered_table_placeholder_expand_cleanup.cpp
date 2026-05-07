#include "tools/decompiler/rendered/rendered_lua_text.h"

#include "tools/decompiler/rendered/rendered_cleanup_common_helpers.h"

#include <algorithm>
#include <cstddef>
#include <string>
#include <string_view>
#include <vector>

namespace refii::lua {
namespace {

template <typename PassFn>
bool RunTablePlaceholderExpandCleanupPassLoop(int max_iterations, PassFn&& run_single_pass) {
    bool changed = false;
    for (int pass = 0; pass < max_iterations; ++pass) {
        const bool pass_changed = run_single_pass();
        if (!pass_changed) {
            break;
        }
        changed = true;
    }
    return changed;
}

}  // namespace

bool ExpandRenderedLuaQualifiedTablePlaceholderLiterals(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto split_top_level_entries =
        [](std::string_view inner, std::vector<std::string>* entries) -> bool {
            if (entries == nullptr) {
                return false;
            }
            entries->clear();

            bool in_single_quote = false;
            bool in_double_quote = false;
            int paren_depth = 0;
            int brace_depth = 0;
            int bracket_depth = 0;
            std::size_t entry_start = 0;
            for (std::size_t index = 0; index < inner.size(); ++index) {
                const char ch = inner[index];
                if (!in_single_quote && !in_double_quote && index + 1 < inner.size() &&
                    ch == '-' && inner[index + 1] == '-') {
                    break;
                }
                if (in_single_quote) {
                    if (ch == '\\' && index + 1 < inner.size()) {
                        ++index;
                    } else if (ch == '\'') {
                        in_single_quote = false;
                    }
                    continue;
                }
                if (in_double_quote) {
                    if (ch == '\\' && index + 1 < inner.size()) {
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
                if (paren_depth == 0 && brace_depth == 0 && bracket_depth == 0 &&
                    ch == ',') {
                    const std::string_view entry =
                        TrimLuaWhitespace(inner.substr(entry_start, index - entry_start));
                    if (!entry.empty()) {
                        entries->push_back(std::string(entry));
                    }
                    entry_start = index + 1;
                }
            }

            const std::string_view tail =
                TrimLuaWhitespace(inner.substr(entry_start));
            if (!tail.empty()) {
                entries->push_back(std::string(tail));
            }
            return !entries->empty();
        };
    const auto parse_single_line_initializer =
        [&](std::string_view line,
            std::string* indent,
            std::string* lhs_prefix,
            std::string* table_name,
            std::vector<std::string>* entries) -> bool {
            const std::size_t first_non_space = line.find_first_not_of(" \t");
            std::string_view trimmed = TrimLuaWhitespace(StripLuaLineComment(line));
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
            if (!IsRenderedSafePrefixExpression(lhs) || rhs.size() < 2 ||
                rhs.front() != '{' || rhs.back() != '}') {
                return false;
            }

            std::vector<std::string> parsed_entries;
            if (!split_top_level_entries(rhs.substr(1, rhs.size() - 2), &parsed_entries)) {
                return false;
            }

            if (indent != nullptr) {
                *indent = first_non_space == std::string_view::npos
                              ? std::string()
                              : std::string(line.substr(0, first_non_space));
            }
            if (lhs_prefix != nullptr) {
                *lhs_prefix =
                    (indent != nullptr
                         ? *indent
                         : (first_non_space == std::string_view::npos
                                ? std::string()
                                : std::string(line.substr(0, first_non_space)))) +
                    local_prefix + std::string(lhs) + " = ";
            }
            if (table_name != nullptr) {
                *table_name = std::string(lhs);
            }
            if (entries != nullptr) {
                *entries = std::move(parsed_entries);
            }
            return true;
        };
    const auto entry_is_placeholder = [](std::string_view entry) {
        entry = TrimLuaWhitespace(entry);
        if (entry == "{}") {
            return true;
        }

        const std::size_t equal = entry.find('=');
        if (equal == std::string_view::npos) {
            return false;
        }

        const std::string_view lhs = TrimLuaWhitespace(entry.substr(0, equal));
        const std::string_view rhs = TrimLuaWhitespace(entry.substr(equal + 1));
        if (lhs.empty() || rhs != "{}") {
            return false;
        }

        return IsValidLuaIdentifier(lhs) ||
               (lhs.front() == '[' && lhs.back() == ']');
    };

    bool changed = false;
    RunTablePlaceholderExpandCleanupPassLoop(32, [&]() {
        bool pass_changed = false;
        for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
            std::string indent;
            std::string lhs_prefix;
            std::string table_name;
            std::vector<std::string> entries;
            if (!parse_single_line_initializer((*lines)[line_index],
                                              &indent,
                                              &lhs_prefix,
                                              &table_name,
                                              &entries)) {
                continue;
            }

            if (IsValidLuaIdentifier(table_name) ||
                (table_name.find('.') == std::string::npos &&
                 table_name.find('[') == std::string::npos)) {
                continue;
            }

            const bool has_placeholder =
                std::any_of(entries.begin(), entries.end(), entry_is_placeholder);
            if (!has_placeholder) {
                continue;
            }

            std::vector<std::string> replacement_lines;
            replacement_lines.push_back(lhs_prefix + "{");
            for (const std::string& entry : entries) {
                replacement_lines.push_back(indent + "  " + entry + ",");
            }
            replacement_lines.push_back(indent + "}");
            (*lines)[line_index] = replacement_lines.front();
            lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(line_index + 1),
                          replacement_lines.begin() + 1,
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
