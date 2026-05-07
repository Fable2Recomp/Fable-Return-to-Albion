#include "tools/decompiler/rendered/rendered_lua_text.h"

#include "tools/decompiler/rendered/rendered_cleanup_common_helpers.h"

#include <algorithm>
#include <cstddef>
#include <string>
#include <string_view>
#include <unordered_set>
#include <vector>

namespace refii::lua {
namespace {

template <typename PassFn>
bool RunTableBuilderClusterCleanupPassLoop(int max_iterations, PassFn&& run_single_pass) {
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

bool SimplifyRenderedLuaTableBuilders(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    constexpr std::size_t kMaxInlineTableBuilderEntries = 6;
    constexpr std::size_t kMaxInlineTableBuilderWidth = 200;

    bool changed = false;
    constexpr const char* kStepName = "SimplifyRenderedLuaTableBuilders";
    RunTableBuilderClusterCleanupPassLoop(32, [&]() {
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

            const bool table_name_is_plain_identifier =
                IsValidLuaIdentifier(table_name);

            std::vector<std::string> replacement_lines;
            replacement_lines.push_back(lhs_prefix + "{");
            std::vector<std::string> inline_entries;
            std::unordered_set<std::string> referenced_temporary_names;

            std::size_t cursor = line_index + 1;
            std::size_t entry_count = 0;
            bool failed = false;
            bool can_inline = true;
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

                if (!rhs_is_function && table_name_is_plain_identifier &&
                    CountIdentifierUses(rhs_expression, table_name) != 0) {
                    failed = true;
                    break;
                }
                CollectRenderedTemporaryNames(rhs_expression, &referenced_temporary_names);

                if (rhs_is_function) {
                    can_inline = false;
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
                        CollectRenderedTemporaryNames((*lines)[body_line],
                                                     &referenced_temporary_names);
                        std::string moved_line = "  " + (*lines)[body_line];
                        if (body_line == function_end) {
                            moved_line += ",";
                        }
                        replacement_lines.push_back(std::move(moved_line));
                    }
                    cursor = function_end + 1;
                } else if (rhs_is_table_literal) {
                    can_inline = false;
                    const std::size_t table_end =
                        FindRenderedTableLiteralEnd(*lines, cursor);
                    if (table_end >= lines->size() || table_end < cursor) {
                        failed = true;
                        break;
                    }
                    for (std::size_t body_line = cursor + 1; body_line < table_end; ++body_line) {
                        if (table_name_is_plain_identifier &&
                            CountIdentifierUses((*lines)[body_line], table_name) != 0) {
                            failed = true;
                            break;
                        }
                    }
                    if (failed) {
                        break;
                    }

                    if (is_array_entry) {
                        replacement_lines.push_back(indent + "  {");
                    } else {
                        replacement_lines.push_back(indent + "  " + field_key + " = {");
                    }
                    for (std::size_t body_line = cursor + 1; body_line <= table_end;
                         ++body_line) {
                        CollectRenderedTemporaryNames((*lines)[body_line],
                                                     &referenced_temporary_names);
                        std::string moved_line = "  " + (*lines)[body_line];
                        if (body_line == table_end) {
                            moved_line += ",";
                        }
                        replacement_lines.push_back(std::move(moved_line));
                    }
                    cursor = table_end + 1;
                } else {
                    inline_entries.push_back(is_array_entry ? rhs_expression
                                                            : field_key + " = " + rhs_expression);
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

            const std::size_t scope_end =
                FindEnclosingRenderedFunctionBlockEnd(*lines, line_index);
            const std::size_t probe_limit =
                scope_end < lines->size() ? scope_end + 1 : lines->size();
            for (const std::string& temporary_name : referenced_temporary_names) {
                if (!HasRenderedTemporaryAssignmentInRange(
                        *lines, line_index + 1, cursor, temporary_name) &&
                    HasRenderedTemporaryAssignmentInRange(
                        *lines, cursor, probe_limit, temporary_name)) {
                    failed = true;
                    break;
                }
            }
            if (failed) {
                continue;
            }

            if (table_name_is_plain_identifier && entry_count == 1) {
                bool has_later_assignment = false;
                for (std::size_t probe = cursor; probe < lines->size() && !has_later_assignment;
                     ++probe) {
                    if (GetRenderedLuaIndent((*lines)[probe]) != indent) {
                        continue;
                    }
                    std::string later_field_key;
                    std::string later_rhs_expression;
                    bool later_rhs_is_function = false;
                    int later_array_index = -1;
                    has_later_assignment = ParseRenderedTableFieldAssignment((*lines)[probe],
                                                                            table_name,
                                                                            &later_field_key,
                                                                            &later_rhs_expression,
                                                                            &later_rhs_is_function,
                                                                            &later_array_index);
                }
                if (has_later_assignment) {
                    continue;
                }
            }

            if (can_inline && inline_entries.size() == entry_count &&
                entry_count <= kMaxInlineTableBuilderEntries) {
                std::string inline_literal = lhs_prefix + "{";
                for (std::size_t entry_index = 0; entry_index < inline_entries.size(); ++entry_index) {
                    if (entry_index != 0) {
                        inline_literal += ", ";
                    }
                    inline_literal += inline_entries[entry_index];
                }
                inline_literal += "}";
                if (inline_literal.size() <= kMaxInlineTableBuilderWidth) {
                    lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                                 lines->begin() + static_cast<std::ptrdiff_t>(cursor));
                    lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                                  std::move(inline_literal));
                    changed = true;
                    pass_changed = true;
                    break;
                }
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
        return pass_changed;
    });

    return changed;
}

bool SimplifyRenderedLuaLocalTableBuilderCallArguments(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    constexpr const char* kStepName = "SimplifyRenderedLuaLocalTableBuilderCallArguments";
    RunTableBuilderClusterCleanupPassLoop(16, [&]() {
        std::vector<std::vector<std::size_t>> scope_paths;
        ComputeRenderedScopePaths(*lines, &scope_paths);

        bool pass_changed = false;
        for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
            if (line_index >= scope_paths.size()) {
                continue;
            }

            bool seed_is_local = false;
            std::string seed_lhs;
            std::string seed_rhs;
            if (!ParseRenderedSingleAssignmentLine((*lines)[line_index],
                                                   &seed_is_local,
                                                   &seed_lhs,
                                                   &seed_rhs) ||
                !seed_is_local) {
                continue;
            }

            const std::string table_name =
                StripRenderedWholeParentheses(TrimLuaWhitespace(seed_lhs), nullptr);
            const std::string normalized_seed_rhs =
                StripRenderedWholeParentheses(TrimLuaWhitespace(seed_rhs), nullptr);
            if (!IsRenderTemporaryName(table_name) || normalized_seed_rhs != "{}") {
                continue;
            }

            const std::string_view seed_stripped = StripLuaLineComment((*lines)[line_index]);
            if (seed_stripped.size() != (*lines)[line_index].size()) {
                continue;
            }

            const std::vector<std::size_t> current_scope = scope_paths[line_index];
            const std::size_t scope_end =
                FindEnclosingRenderedFunctionBlockEnd(*lines, line_index);
            const std::size_t probe_limit =
                scope_end < lines->size() ? scope_end + 1 : lines->size();

            std::vector<std::string> entries;
            entries.reserve(6);
            std::size_t cursor = line_index + 1;
            while (cursor < probe_limit && cursor < lines->size()) {
                if (cursor >= scope_paths.size() || scope_paths[cursor] != current_scope) {
                    break;
                }

                const std::string_view stripped = StripLuaLineComment((*lines)[cursor]);
                if (stripped.size() != (*lines)[cursor].size()) {
                    entries.clear();
                    break;
                }

                const std::string_view trimmed = TrimLuaWhitespace(stripped);
                if (trimmed.empty()) {
                    break;
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
                    break;
                }

                int rhs_paren_balance = 0;
                int rhs_brace_balance = 0;
                if (rhs_is_function || rhs_expression == "{" ||
                    HasPositiveLuaExpressionBalance(rhs_expression,
                                                   &rhs_paren_balance,
                                                   &rhs_brace_balance) ||
                    CountIdentifierUses(rhs_expression, table_name) != 0) {
                    entries.clear();
                    break;
                }

                const bool is_array_entry =
                    array_index > 0 && static_cast<std::size_t>(array_index) == entries.size() + 1;
                entries.push_back(is_array_entry ? rhs_expression
                                                 : field_key + " = " + rhs_expression);
                if (entries.size() > 6) {
                    entries.clear();
                    break;
                }
                ++cursor;
            }

            if (entries.empty() || cursor >= lines->size() || cursor >= scope_paths.size() ||
                scope_paths[cursor] != current_scope) {
                continue;
            }

            const std::string_view use_trimmed =
                TrimLuaWhitespace(StripLuaLineComment((*lines)[cursor]));
            if (use_trimmed.empty()) {
                continue;
            }

            std::string callee;
            std::vector<std::string> arguments;
            if (!ParseRenderedSingleCallLine((*lines)[cursor], &callee, &arguments) ||
                CountIdentifierUses(callee, table_name) != 0 || arguments.empty()) {
                continue;
            }

            std::size_t table_argument_index = arguments.size();
            bool failed = false;
            for (std::size_t argument_index = 0; argument_index < arguments.size(); ++argument_index) {
                const std::string normalized_argument = StripRenderedWholeParentheses(
                    TrimLuaWhitespace(arguments[argument_index]), nullptr);
                if (normalized_argument == table_name) {
                    if (table_argument_index != arguments.size()) {
                        failed = true;
                        break;
                    }
                    table_argument_index = argument_index;
                    continue;
                }
                if (CountIdentifierUses(arguments[argument_index], table_name) != 0) {
                    failed = true;
                    break;
                }
            }

            if (failed || table_argument_index != arguments.size() - 1) {
                continue;
            }

            bool used_later = false;
            for (std::size_t probe = cursor + 1; probe < probe_limit; ++probe) {
                if (probe >= scope_paths.size()) {
                    break;
                }

                const std::vector<std::size_t>& probe_scope = scope_paths[probe];
                if (IsRenderedScopePathPrefix(current_scope, probe_scope) &&
                    probe_scope != current_scope &&
                    CountIdentifierUses((*lines)[probe], table_name) != 0) {
                    used_later = true;
                    break;
                }
                if (probe_scope != current_scope) {
                    continue;
                }
                if (IsRenderedShadowingLocalDeclaration((*lines)[probe], table_name) ||
                    IsAssignmentToIdentifier((*lines)[probe], table_name)) {
                    break;
                }
                if (CountIdentifierUses((*lines)[probe], table_name) != 0) {
                    used_later = true;
                    break;
                }
            }
            if (used_later) {
                continue;
            }

            const std::string indent(GetRenderedLuaIndent((*lines)[cursor]));
            const std::string_view use_stripped = StripLuaLineComment((*lines)[cursor]);
            const std::string_view comment =
                use_stripped.size() < (*lines)[cursor].size()
                    ? std::string_view((*lines)[cursor]).substr(use_stripped.size())
                    : std::string_view();

            std::vector<std::string> replacement_lines;
            std::string first_line = indent + callee + "(";
            for (std::size_t argument_index = 0; argument_index < table_argument_index;
                 ++argument_index) {
                if (argument_index != 0) {
                    first_line += ", ";
                }
                first_line += arguments[argument_index];
            }
            if (table_argument_index != 0) {
                first_line += ", ";
            }
            first_line += "{";
            replacement_lines.push_back(std::move(first_line));
            for (const std::string& entry : entries) {
                replacement_lines.push_back(indent + "  " + entry + ",");
            }
            replacement_lines.push_back(indent + "})" + std::string(comment));

            lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                         lines->begin() + static_cast<std::ptrdiff_t>(cursor + 1));
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

bool SimplifyRenderedLuaLocalTableLiteralCallAssignments(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    constexpr const char* kStepName = "SimplifyRenderedLuaLocalTableLiteralCallAssignments";
    RunTableBuilderClusterCleanupPassLoop(16, [&]() {
        std::vector<std::vector<std::size_t>> scope_paths;
        ComputeRenderedScopePaths(*lines, &scope_paths);

        bool pass_changed = false;
        for (std::size_t line_index = 0; line_index + 1 < lines->size(); ++line_index) {
            if (line_index >= scope_paths.size()) {
                continue;
            }

            bool seed_is_local = false;
            std::string seed_lhs;
            std::string seed_rhs;
            if (!ParseRenderedSingleAssignmentLine((*lines)[line_index],
                                                   &seed_is_local,
                                                   &seed_lhs,
                                                   &seed_rhs) ||
                !seed_is_local) {
                continue;
            }

            const std::string table_name =
                StripRenderedWholeParentheses(TrimLuaWhitespace(seed_lhs), nullptr);
            const std::string table_literal =
                StripRenderedWholeParentheses(TrimLuaWhitespace(seed_rhs), nullptr);
            if (!IsRenderTemporaryName(table_name) ||
                table_literal.size() < 2 ||
                table_literal.front() != '{' ||
                table_literal.back() != '}' ||
                CountIdentifierUses(table_literal, table_name) != 0) {
                continue;
            }

            const std::string_view seed_stripped = StripLuaLineComment((*lines)[line_index]);
            if (seed_stripped.size() != (*lines)[line_index].size()) {
                continue;
            }

            const std::vector<std::size_t> current_scope = scope_paths[line_index];
            const std::size_t scope_end =
                FindEnclosingRenderedFunctionBlockEnd(*lines, line_index);
            const std::size_t probe_limit =
                scope_end < lines->size() ? scope_end + 1 : lines->size();

            std::size_t use_line = line_index + 1;
            while (use_line < probe_limit && use_line < lines->size()) {
                if (use_line >= scope_paths.size() || scope_paths[use_line] != current_scope) {
                    break;
                }
                const std::string_view stripped = StripLuaLineComment((*lines)[use_line]);
                if (stripped.size() != (*lines)[use_line].size()) {
                    break;
                }
                if (!TrimLuaWhitespace(stripped).empty()) {
                    break;
                }
                ++use_line;
            }
            if (use_line >= probe_limit || use_line >= lines->size() ||
                use_line >= scope_paths.size() || scope_paths[use_line] != current_scope) {
                continue;
            }

            bool call_assignment_is_local = false;
            std::string call_assignment_lhs;
            std::string call_assignment_rhs;
            if (!ParseRenderedSingleAssignmentLine((*lines)[use_line],
                                                   &call_assignment_is_local,
                                                   &call_assignment_lhs,
                                                   &call_assignment_rhs) ||
                call_assignment_is_local) {
                continue;
            }

            std::string callee;
            std::vector<std::string> arguments;
            if (!ParseRenderedSingleCallLine(call_assignment_rhs, &callee, &arguments) ||
                CountIdentifierUses(callee, table_name) != 0 || arguments.empty()) {
                continue;
            }

            std::size_t table_argument_index = arguments.size();
            bool failed = false;
            for (std::size_t argument_index = 0; argument_index < arguments.size(); ++argument_index) {
                const std::string normalized_argument = StripRenderedWholeParentheses(
                    TrimLuaWhitespace(arguments[argument_index]), nullptr);
                if (normalized_argument == table_name) {
                    if (table_argument_index != arguments.size()) {
                        failed = true;
                        break;
                    }
                    table_argument_index = argument_index;
                    continue;
                }
                if (CountIdentifierUses(arguments[argument_index], table_name) != 0) {
                    failed = true;
                    break;
                }
            }
            if (failed || table_argument_index == arguments.size()) {
                continue;
            }

            bool used_later = false;
            for (std::size_t probe = use_line + 1; probe < probe_limit; ++probe) {
                if (probe >= scope_paths.size()) {
                    break;
                }
                if (scope_paths[probe] != current_scope) {
                    continue;
                }
                if (IsRenderedShadowingLocalDeclaration((*lines)[probe], table_name) ||
                    IsAssignmentToIdentifier((*lines)[probe], table_name)) {
                    break;
                }
                if (CountIdentifierUses((*lines)[probe], table_name) != 0) {
                    used_later = true;
                    break;
                }
            }
            if (used_later) {
                continue;
            }

            arguments[table_argument_index] = table_literal;
            std::string rewritten_rhs = callee + "(";
            for (std::size_t argument_index = 0; argument_index < arguments.size(); ++argument_index) {
                if (argument_index != 0) {
                    rewritten_rhs += ", ";
                }
                rewritten_rhs += arguments[argument_index];
            }
            rewritten_rhs += ")";

            (*lines)[line_index].clear();
            (*lines)[use_line] =
                std::string(GetRenderedLuaIndent((*lines)[use_line])) +
                std::string(TrimLuaWhitespace(call_assignment_lhs)) + " = " + rewritten_rhs;
            changed = true;
            pass_changed = true;
            break;
        }
        return pass_changed;
    });

    return changed;
}

bool SimplifyRenderedLuaDelayedArrayTableBuilders(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    constexpr const char* kStepName = "SimplifyRenderedLuaDelayedArrayTableBuilders";
    RunTableBuilderClusterCleanupPassLoop(32, [&]() {
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

            std::vector<std::string> prefix_lines;
            std::vector<std::string> replacement_lines;
            std::size_t cursor = line_index + 1;
            std::size_t entry_count = 0;
            bool failed = false;
            int expected_array_index = 1;
            while (cursor < lines->size()) {
                const std::string_view trimmed = TrimLuaWhitespace((*lines)[cursor]);
                if (trimmed.empty()) {
                    if (entry_count != 0) {
                        break;
                    }
                    prefix_lines.push_back((*lines)[cursor]);
                    ++cursor;
                    continue;
                }

                std::string field_key;
                std::string rhs_expression;
                bool rhs_is_function = false;
                int array_index = -1;
                if (ParseRenderedTableFieldAssignment((*lines)[cursor],
                                                     table_name,
                                                     &field_key,
                                                     &rhs_expression,
                                                     &rhs_is_function,
                                                     &array_index) &&
                    array_index == expected_array_index) {
                    const bool rhs_is_table_literal = rhs_expression == "{";
                    int rhs_paren_balance = 0;
                    int rhs_brace_balance = 0;
                    if (rhs_is_function || rhs_is_table_literal ||
                        HasPositiveLuaExpressionBalance(rhs_expression,
                                                       &rhs_paren_balance,
                                                       &rhs_brace_balance)) {
                        failed = true;
                        break;
                    }

                    if (entry_count == 0) {
                        replacement_lines.push_back(lhs_prefix + "{");
                    }
                    replacement_lines.push_back(indent + "  " + rhs_expression + ",");
                    ++entry_count;
                    ++expected_array_index;
                    ++cursor;
                    continue;
                }

                if (entry_count != 0) {
                    break;
                }

                if (IsAssignmentToIdentifier((*lines)[cursor], table_name) ||
                    CountIdentifierUses((*lines)[cursor], table_name) != 0) {
                    failed = true;
                    break;
                }
                prefix_lines.push_back((*lines)[cursor]);
                ++cursor;
            }

            if (failed || entry_count == 0) {
                continue;
            }

            replacement_lines.push_back(indent + "}");
            std::vector<std::string> merged_lines = prefix_lines;
            merged_lines.insert(merged_lines.end(),
                                replacement_lines.begin(),
                                replacement_lines.end());

            lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                         lines->begin() + static_cast<std::ptrdiff_t>(cursor));
            lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                          merged_lines.begin(),
                          merged_lines.end());
            changed = true;
            pass_changed = true;
            break;
        }
        return pass_changed;
    });

    return changed;
}

}  // namespace refii::lua
