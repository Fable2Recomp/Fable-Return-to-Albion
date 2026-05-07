#include "tools/decompiler/rendered/rendered_lua_text.h"

namespace refii::lua {
namespace {

template <typename PassFn>
bool RunTableTempCleanupPassLoop(int max_iterations, PassFn&& run_single_pass) {
    bool changed = false;
    for (int pass = 0; pass < max_iterations; ++pass) {
        if (!run_single_pass()) {
            break;
        }
        changed = true;
    }
    return changed;
}

}  // namespace

bool SimplifyRenderedLuaTableInitializerTemporaries(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    RunTableTempCleanupPassLoop(32, [&]() {
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

            if (!IsValidLuaIdentifier(table_name)) {
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
        return pass_changed;
    });

    return changed;
}

bool SimplifyRenderedLuaTableLiteralTemporaries(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    RunTableTempCleanupPassLoop(32, [&]() {
        bool pass_changed = false;
        for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
            const std::string_view trimmed_line =
                TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index]));
            if (!trimmed_line.starts_with("local ")) {
                continue;
            }

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
        return pass_changed;
    });

    return changed;
}

}  // namespace refii::lua
