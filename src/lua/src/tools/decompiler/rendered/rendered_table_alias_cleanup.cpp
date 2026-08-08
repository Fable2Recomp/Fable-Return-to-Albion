#include "tools/decompiler/rendered/rendered_lua_text.h"

#include "tools/decompiler/rendered/rendered_cleanup_common_helpers.h"
#include "tools/decompiler/repair/lua_decompile_repair_context.h"

#include <algorithm>
#include <cctype>
#include <cstddef>
#include <string>
#include <string_view>
#include <unordered_map>
#include <unordered_set>
#include <utility>
#include <vector>

namespace refii::lua {
namespace {

template <typename PassFn>
bool RunTableAliasCleanupPassLoop(int max_iterations, PassFn&& run_single_pass) {
    bool changed = false;
    for (int pass = 0; pass < max_iterations; ++pass) {
        if (internal::IsCurrentDecompileRepairDeadlineExceeded()) {
            break;
        }
        const bool pass_changed = run_single_pass();
        if (!pass_changed) {
            break;
        }
        changed = true;
    }
    return changed;
}

}  // namespace

bool SimplifyRenderedLuaTableLiteralCapturedTargetAliases(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 1 < lines->size(); ++line_index) {
        bool assign_is_local = false;
        std::string alias_name;
        std::string alias_expression;
        if (!ParseRenderedSingleAssignmentLine((*lines)[line_index],
                                               &assign_is_local,
                                               &alias_name,
                                               &alias_expression) ||
            !assign_is_local || !IsValidLuaIdentifier(alias_name)) {
            continue;
        }

        const std::string normalized_expression =
            StripRenderedWholeParentheses(TrimLuaWhitespace(alias_expression), nullptr);
        if (normalized_expression.empty() ||
            !IsRenderedSafePrefixExpression(normalized_expression)) {
            continue;
        }

        std::size_t literal_start = line_index + 1;
        while (literal_start < lines->size() &&
               TrimLuaWhitespace(StripLuaLineComment((*lines)[literal_start])).empty()) {
            ++literal_start;
        }
        if (literal_start >= lines->size()) {
            continue;
        }

        std::string indent;
        std::string lhs_prefix;
        std::string literal_target;
        if (!ParseRenderedTableLiteralStartLine((*lines)[literal_start],
                                                &indent,
                                                &lhs_prefix,
                                                &literal_target) ||
            literal_target != normalized_expression) {
            continue;
        }

        const std::size_t literal_end = FindRenderedTableLiteralEnd(*lines, literal_start);
        if (literal_end >= lines->size() || literal_end <= literal_start) {
            continue;
        }

        for (std::size_t body_line = literal_start + 1; body_line < literal_end; ++body_line) {
            std::string rewritten_line;
            if (!ReplaceRenderedSafePrefixExpressionChainUsesInStatementLine((*lines)[body_line],
                                                                             alias_name,
                                                                             normalized_expression,
                                                                             &rewritten_line) ||
                rewritten_line == (*lines)[body_line]) {
                continue;
            }

            (*lines)[body_line] = std::move(rewritten_line);
            changed = true;
        }
    }

    return changed;
}

bool HasRenderedMemberAccessPrefix(std::string_view expression, std::string_view table_name) {
    if (table_name.empty() || expression.size() <= table_name.size() ||
        expression.substr(0, table_name.size()) != table_name) {
        return false;
    }

    const char next = expression[table_name.size()];
    return next == '.' || next == '[' || next == ':';
}

bool ContainsRenderedIdentifierReference(std::string_view line, std::string_view identifier) {
    if (identifier.empty()) {
        return false;
    }

    const std::string_view trimmed = StripLuaLineComment(line);
    const auto is_identifier_char = [](char ch) {
        const unsigned char value = static_cast<unsigned char>(ch);
        return std::isalnum(value) != 0 || ch == '_';
    };

    for (std::size_t pos = trimmed.find(identifier);
         pos != std::string_view::npos;
         pos = trimmed.find(identifier, pos + identifier.size())) {
        const bool left_ok = pos == 0 || !is_identifier_char(trimmed[pos - 1]);
        const std::size_t end = pos + identifier.size();
        const bool right_ok = end >= trimmed.size() || !is_identifier_char(trimmed[end]);
        if (left_ok && right_ok) {
            return true;
        }
    }

    return false;
}

bool HasRenderedSubclassMemberTargetPrefix(std::string_view line,
                                           std::string_view subclass_name) {
    if (subclass_name.empty()) {
        return false;
    }

    std::string_view trimmed = TrimLuaWhitespace(StripLuaLineComment(line));
    if (trimmed.size() <= subclass_name.size() ||
        trimmed.substr(0, subclass_name.size()) != subclass_name) {
        return false;
    }

    const char next = trimmed[subclass_name.size()];
    return next == '.' || next == '[' || next == ':';
}

bool IsRenderedTopLevelSubclassUse(std::string_view line, std::string_view subclass_name) {
    if (HasRenderedSubclassMemberTargetPrefix(line, subclass_name)) {
        return true;
    }

    bool is_local = false;
    std::string lhs_expression;
    std::string rhs_expression;
    if (ParseRenderedSingleAssignmentLine(line, &is_local, &lhs_expression, &rhs_expression) &&
        HasRenderedMemberAccessPrefix(lhs_expression, subclass_name)) {
        return true;
    }

    std::string_view trimmed = TrimLuaWhitespace(StripLuaLineComment(line));
    if (!trimmed.starts_with("function ")) {
        return false;
    }

    trimmed = TrimLuaWhitespace(trimmed.substr(9));
    return HasRenderedMemberAccessPrefix(trimmed, subclass_name);
}

bool IsRenderedTopLevelSubclassBinding(std::string_view line, std::string_view subclass_name) {
    std::vector<std::string> local_names;
    if (ParseRenderedLocalDeclarationLine(line, &local_names)) {
        return std::find(local_names.begin(), local_names.end(), subclass_name) !=
               local_names.end();
    }

    bool is_local = false;
    std::string lhs_expression;
    std::string rhs_expression;
    if (!ParseRenderedSingleAssignmentLine(line, &is_local, &lhs_expression, &rhs_expression)) {
        return false;
    }

    return lhs_expression == subclass_name ||
           lhs_expression == ("BaseObjects." + std::string(subclass_name));
}

bool ParseRenderedLocalTemporaryAliasAssignment(std::string_view line,
                                                std::string_view rhs_identifier,
                                                std::string* lhs_expression) {
    bool is_local = false;
    std::string lhs;
    std::string rhs;
    if (!ParseRenderedSingleAssignmentLine(line, &is_local, &lhs, &rhs) || !is_local) {
        return false;
    }

    const std::string normalized_lhs =
        StripRenderedWholeParentheses(TrimLuaWhitespace(lhs), nullptr);
    const std::string normalized_rhs =
        StripRenderedWholeParentheses(TrimLuaWhitespace(rhs), nullptr);
    if (!IsRenderTemporaryName(normalized_lhs) || normalized_rhs != rhs_identifier) {
        return false;
    }

    if (lhs_expression != nullptr) {
        *lhs_expression = std::move(normalized_lhs);
    }
    return true;
}

bool IsRenderedTopLevelSubclassInitializerLine(std::string_view line) {
    std::string base_expression;
    std::string subclass_name;
    if (ParseRenderedCreateSubclassStatement(line, &base_expression, &subclass_name)) {
        return true;
    }

    bool is_local = false;
    std::string lhs_expression;
    std::string rhs_expression;
    if (!ParseRenderedSingleAssignmentLine(line, &is_local, &lhs_expression, &rhs_expression) ||
        is_local) {
        return false;
    }

    return ParseRenderedCreateSubclassExpression(rhs_expression,
                                                 &base_expression,
                                                 &subclass_name);
}

bool IsRenderedTopLevelSubclassBaseUse(std::string_view line, std::string_view subclass_name) {
    std::string base_expression;
    std::string created_subclass_name;
    if (ParseRenderedCreateSubclassStatement(line, &base_expression, &created_subclass_name)) {
        return base_expression == subclass_name;
    }

    bool is_local = false;
    std::string lhs_expression;
    std::string rhs_expression;
    if (!ParseRenderedSingleAssignmentLine(line, &is_local, &lhs_expression, &rhs_expression) ||
        is_local) {
        return false;
    }

    return ParseRenderedCreateSubclassExpression(rhs_expression,
                                                 &base_expression,
                                                 &created_subclass_name) &&
           base_expression == subclass_name;
}

bool HasRenderedAdjacentSiblingSubclassInitializer(const std::vector<std::string>& lines,
                                                   std::size_t line_index,
                                                   std::string_view base_expression,
                                                   std::string_view indent) {
    auto shares_same_base = [&](std::size_t probe) {
        if (GetRenderedLuaIndent(lines[probe]) != indent) {
            return false;
        }

        std::string probe_base_expression;
        std::string probe_subclass_name;
        if (!ParseRenderedCreateSubclassStatement(lines[probe],
                                                 &probe_base_expression,
                                                 &probe_subclass_name)) {
            bool is_local = false;
            std::string lhs_expression;
            std::string rhs_expression;
            if (!ParseRenderedSingleAssignmentLine(lines[probe],
                                                  &is_local,
                                                  &lhs_expression,
                                                  &rhs_expression) ||
                is_local ||
                !ParseRenderedCreateSubclassExpression(rhs_expression,
                                                      &probe_base_expression,
                                                      &probe_subclass_name)) {
                return false;
            }
        }

        return probe_base_expression == base_expression;
    };

    for (std::size_t probe = line_index; probe-- > 0;) {
        const std::string_view trimmed = TrimLuaWhitespace(StripLuaLineComment(lines[probe]));
        if (trimmed.empty()) {
            continue;
        }
        if (shares_same_base(probe)) {
            return true;
        }
        break;
    }

    for (std::size_t probe = line_index + 1; probe < lines.size(); ++probe) {
        const std::string_view trimmed = TrimLuaWhitespace(StripLuaLineComment(lines[probe]));
        if (trimmed.empty()) {
            continue;
        }
        if (shares_same_base(probe)) {
            return true;
        }
        break;
    }

    return false;
}

bool HasRenderedEarlierTopLevelSubclassBinding(const std::vector<std::string>& lines,
                                               std::size_t line_index,
                                               std::string_view subclass_name,
                                               std::string_view indent) {
    for (std::size_t probe = 0; probe < line_index; ++probe) {
        if (GetRenderedLuaIndent(lines[probe]) != indent) {
            continue;
        }

        std::vector<std::string> local_names;
        if (ParseRenderedLocalDeclarationLine(lines[probe], &local_names)) {
            if (std::find(local_names.begin(), local_names.end(), subclass_name) !=
                local_names.end()) {
                return true;
            }
            continue;
        }

        bool is_local = false;
        std::string lhs_expression;
        std::string rhs_expression;
        if (!ParseRenderedSingleAssignmentLine(lines[probe],
                                               &is_local,
                                               &lhs_expression,
                                               &rhs_expression)) {
            continue;
        }

        if (lhs_expression == subclass_name ||
            lhs_expression == ("BaseObjects." + std::string(subclass_name))) {
            return true;
        }
    }

    return false;
}

bool RestoreRenderedLuaTopLevelSubclassBindings(std::vector<std::string>* lines) {
    if (lines == nullptr || lines->empty()) {
        return false;
    }

    std::string root_indent;
    for (const std::string& line : *lines) {
        const std::string_view trimmed = TrimLuaWhitespace(StripLuaLineComment(line));
        if (trimmed.empty()) {
            continue;
        }
        root_indent = std::string(GetRenderedLuaIndent(line));
        break;
    }

    return RunTableAliasCleanupPassLoop(4, [&]() {
        bool pass_changed = false;
        for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
            if (GetRenderedLuaIndent((*lines)[line_index]) != root_indent) {
                continue;
            }

            std::string base_expression;
            std::string subclass_name;
            if (!ParseRenderedCreateSubclassStatement((*lines)[line_index],
                                                      &base_expression,
                                                      &subclass_name)) {
                continue;
            }

            if (HasRenderedEarlierTopLevelSubclassBinding(*lines,
                                                          line_index,
                                                          subclass_name,
                                                          root_indent)) {
                continue;
            }

            const std::string indent(GetRenderedLuaIndent((*lines)[line_index]));
            if (HasRenderedAdjacentSiblingSubclassInitializer(*lines,
                                                              line_index,
                                                              base_expression,
                                                              indent)) {
                (*lines)[line_index] =
                    indent + subclass_name + " = " + base_expression + ":CreateSubClass(\"" +
                    subclass_name + "\")";
                pass_changed = true;
                continue;
            }

            std::size_t lookahead_non_empty = 0;
            bool saw_supported_use = false;
            bool failed = false;
            for (std::size_t probe = line_index + 1; probe < lines->size(); ++probe) {
                const std::string_view trimmed =
                    TrimLuaWhitespace(StripLuaLineComment((*lines)[probe]));
                if (trimmed.empty()) {
                    continue;
                }

                if (GetRenderedLuaIndent((*lines)[probe]) != indent) {
                    continue;
                }

                ++lookahead_non_empty;
                if (lookahead_non_empty > 256) {
                    break;
                }

                if (IsRenderedTopLevelSubclassUse((*lines)[probe], subclass_name) ||
                    IsRenderedTopLevelSubclassBaseUse((*lines)[probe], subclass_name)) {
                    saw_supported_use = true;
                    break;
                }

                if (ParseRenderedLocalTemporaryAliasAssignment((*lines)[probe],
                                                              subclass_name,
                                                              nullptr)) {
                    saw_supported_use = true;
                    break;
                }

                if (IsRenderedTopLevelSubclassBinding((*lines)[probe], subclass_name)) {
                    failed = true;
                    break;
                }

                if (IsRenderedTopLevelSubclassInitializerLine((*lines)[probe])) {
                    continue;
                }

                if (ContainsRenderedIdentifierReference((*lines)[probe], subclass_name)) {
                    failed = true;
                    break;
                }
            }

            if (failed || !saw_supported_use) {
                continue;
            }

            (*lines)[line_index] =
                indent + subclass_name + " = " + base_expression + ":CreateSubClass(\"" +
                subclass_name + "\")";
            pass_changed = true;
        }
        return pass_changed;
    });
}

bool ParseRenderedIdentifierAliasAssignment(std::string_view line,
                                            std::string_view rhs_identifier,
                                            std::string* lhs_expression);

bool SimplifyRenderedLuaTopLevelMemberAliases(std::vector<std::string>* lines) {
    if (lines == nullptr || lines->empty()) {
        return false;
    }

    const auto has_pathological_member_chain = [](std::string_view expression) {
        if (expression.size() > 768) {
            return true;
        }

        std::string previous_segment;
        std::size_t repeated_segment_run = 0;
        std::size_t index = 0;
        while (index < expression.size()) {
            const std::size_t dot = expression.find('.', index);
            if (dot == std::string_view::npos || dot + 1 >= expression.size()) {
                break;
            }
            std::size_t segment_begin = dot + 1;
            std::size_t segment_end = segment_begin;
            while (segment_end < expression.size() &&
                   (std::isalnum(static_cast<unsigned char>(expression[segment_end])) != 0 ||
                    expression[segment_end] == '_')) {
                ++segment_end;
            }
            if (segment_end == segment_begin) {
                index = segment_begin;
                continue;
            }

            const std::string segment(
                expression.substr(segment_begin, segment_end - segment_begin));
            if (segment == previous_segment) {
                ++repeated_segment_run;
            } else {
                previous_segment = segment;
                repeated_segment_run = 1;
            }
            if (repeated_segment_run >= 2) {
                return true;
            }
            index = segment_end;
        }

        return false;
    };

    const auto control_block_assigns_target = [&](std::size_t control_line,
                                                  std::string_view target) {
        if (control_line >= lines->size() || target.empty()) {
            return false;
        }

        const std::string_view trimmed =
            TrimLuaWhitespace(StripLuaLineComment((*lines)[control_line]));
        const bool control_header =
            (trimmed.starts_with("if ") && trimmed.ends_with(" then")) ||
            (trimmed.starts_with("elseif ") && trimmed.ends_with(" then")) ||
            (trimmed.starts_with("while ") && trimmed.ends_with(" do"));
        if (!control_header || CountIdentifierUses(trimmed, target) == 0) {
            return false;
        }

        const std::size_t block_end = FindRenderedFunctionBlockEnd(*lines, control_line);
        if (block_end >= lines->size() || block_end <= control_line) {
            return false;
        }

        for (std::size_t probe = control_line + 1; probe < block_end; ++probe) {
            if (IsRenderedShadowingLocalDeclaration((*lines)[probe], target)) {
                return false;
            }

            bool assign_is_local = false;
            std::string lhs;
            std::string rhs;
            if (ParseRenderedSingleAssignmentLine((*lines)[probe],
                                                  &assign_is_local,
                                                  &lhs,
                                                  &rhs) &&
                !assign_is_local &&
                lhs == target) {
                return true;
            }
        }

        return false;
    };

    return RunTableAliasCleanupPassLoop(32, [&]() {
        std::vector<std::vector<std::size_t>> scope_paths;
        ComputeRenderedScopePaths(*lines, &scope_paths);

        for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
            if (line_index >= scope_paths.size() || !scope_paths[line_index].empty()) {
                continue;
            }

            bool is_local = false;
            std::string lhs_expression;
            std::string rhs_expression;
            if (!ParseRenderedSingleAssignmentLine((*lines)[line_index],
                                                   &is_local,
                                                   &lhs_expression,
                                                   &rhs_expression) ||
                !IsRenderTemporaryName(lhs_expression)) {
                continue;
            }

            std::string current_target =
                StripRenderedWholeParentheses(TrimLuaWhitespace(rhs_expression), nullptr);
            if (current_target.empty() || IsRenderTemporaryName(current_target) ||
                !IsRenderedSafePrefixExpression(current_target)) {
                continue;
            }
            if (CountIdentifierUses(current_target, lhs_expression) > 1) {
                continue;
            }

            std::vector<std::pair<std::size_t, std::string>> rewritten_lines;
            std::vector<std::size_t> removable_alias_lines;
            removable_alias_lines.push_back(line_index);
            bool failed = false;
            for (std::size_t probe = line_index + 1; probe < lines->size(); ++probe) {
                if (probe >= scope_paths.size() || !scope_paths[probe].empty()) {
                    continue;
                }

                bool probe_is_local = false;
                std::string probe_lhs;
                std::string probe_rhs;
                if (ParseRenderedSingleAssignmentLine(
                        (*lines)[probe], &probe_is_local, &probe_lhs, &probe_rhs) &&
                    StripRenderedWholeParentheses(TrimLuaWhitespace(probe_lhs), nullptr) ==
                        lhs_expression) {
                    std::string normalized_probe_rhs =
                        StripRenderedWholeParentheses(TrimLuaWhitespace(probe_rhs), nullptr);
                    if (CountIdentifierUses(normalized_probe_rhs, lhs_expression) != 0) {
                        std::string rewritten_probe_rhs;
                        if (!ReplaceRenderedSafePrefixExpressionChainUses(
                                normalized_probe_rhs,
                                lhs_expression,
                                current_target,
                                &rewritten_probe_rhs) ||
                            rewritten_probe_rhs.empty()) {
                            break;
                        }
                        normalized_probe_rhs = std::move(rewritten_probe_rhs);
                    }
                    if (normalized_probe_rhs.empty() ||
                        IsRenderTemporaryName(normalized_probe_rhs) ||
                        !IsRenderedSafePrefixExpression(normalized_probe_rhs) ||
                        has_pathological_member_chain(normalized_probe_rhs) ||
                        CountIdentifierUses(normalized_probe_rhs, lhs_expression) != 0) {
                        break;
                    }

                    removable_alias_lines.push_back(probe);
                    current_target = normalized_probe_rhs;
                    continue;
                }

                const std::size_t use_count = CountIdentifierUses((*lines)[probe], lhs_expression);
                if (use_count == 0) {
                    if (!TrimLuaWhitespace(StripLuaLineComment((*lines)[probe])).empty()) {
                        failed = true;
                        break;
                    }
                    continue;
                }

                if (control_block_assigns_target(probe, lhs_expression)) {
                    failed = true;
                    break;
                }

                std::string rewritten;
                if (!RewriteRenderedAliasTargetUsesInStatementLine((*lines)[probe],
                                                                   lhs_expression,
                                                                   current_target,
                                                                   &rewritten) ||
                    rewritten.empty() ||
                    has_pathological_member_chain(rewritten)) {
                    failed = true;
                    break;
                }

                rewritten_lines.emplace_back(probe, std::move(rewritten));
            }

            if (failed || (rewritten_lines.empty() && removable_alias_lines.size() <= 1)) {
                continue;
            }

            bool remaining_uses = false;
            for (std::size_t probe = line_index + 1; probe < lines->size(); ++probe) {
                const bool already_rewritten =
                    std::any_of(rewritten_lines.begin(),
                                rewritten_lines.end(),
                                [&](const auto& rewritten_entry) {
                                    return rewritten_entry.first == probe;
                                });
                if (already_rewritten) {
                    continue;
                }
                if (std::find(removable_alias_lines.begin(),
                              removable_alias_lines.end(),
                              probe) != removable_alias_lines.end()) {
                    continue;
                }

                if (CountIdentifierUses((*lines)[probe], lhs_expression) != 0) {
                    remaining_uses = true;
                    break;
                }
            }

            for (const auto& [probe, rewritten] : rewritten_lines) {
                (*lines)[probe] = rewritten;
            }
            if (!remaining_uses) {
                std::sort(removable_alias_lines.begin(), removable_alias_lines.end());
                for (auto it = removable_alias_lines.rbegin();
                     it != removable_alias_lines.rend();
                     ++it) {
                    lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(*it));
                }
            }
            return true;
        }

        return false;
    });
}

bool SimplifyRenderedLuaDeadTemporaryAliasAssignments(std::vector<std::string>* lines) {
    if (lines == nullptr || lines->empty()) {
        return false;
    }

    const auto is_ephemeral_alias_name = [](std::string_view name) {
        if (IsRenderTemporaryName(name)) {
            return true;
        }

        if (name.size() < 4 || (name.front() != 'A' && name.front() != 'L')) {
            return false;
        }

        std::size_t index = 1;
        bool saw_register_digit = false;
        while (index < name.size() &&
               std::isdigit(static_cast<unsigned char>(name[index])) != 0) {
            saw_register_digit = true;
            ++index;
        }
        if (!saw_register_digit || index >= name.size() || name[index] != '_') {
            return false;
        }
        ++index;

        bool saw_suffix_digit = false;
        while (index < name.size()) {
            if (std::isdigit(static_cast<unsigned char>(name[index])) == 0) {
                return false;
            }
            saw_suffix_digit = true;
            ++index;
        }
        return saw_suffix_digit;
    };
    const auto scope_path_is_prefix = [](const std::vector<std::size_t>& prefix,
                                         const std::vector<std::size_t>& candidate) {
        return prefix.size() <= candidate.size() &&
               std::equal(prefix.begin(), prefix.end(), candidate.begin());
    };
    const auto line_calls_alias_as_callee = [](std::string_view line,
                                               std::string_view alias_name) {
        if (alias_name.empty()) {
            return false;
        }

        std::string_view statement = TrimLuaWhitespace(StripLuaLineComment(line));
        if (statement.starts_with("local ")) {
            statement = TrimLuaWhitespace(statement.substr(6));
        }

        const std::size_t equal = statement.find('=');
        if (equal != std::string_view::npos) {
            statement = TrimLuaWhitespace(statement.substr(equal + 1));
        }

        return statement.starts_with(alias_name) &&
               statement.size() > alias_name.size() &&
               statement[alias_name.size()] == '(';
    };
    const auto has_immediate_alias_call_use = [&](std::size_t assignment_line,
                                                  std::string_view alias_name) {
        for (std::size_t probe = assignment_line + 1; probe < lines->size(); ++probe) {
            const std::string_view trimmed =
                TrimLuaWhitespace(StripLuaLineComment((*lines)[probe]));
            if (trimmed.empty()) {
                continue;
            }
            return line_calls_alias_as_callee(trimmed, alias_name);
        }
        return false;
    };

    return RunTableAliasCleanupPassLoop(32, [&]() {
        std::vector<std::vector<std::size_t>> scope_paths;
        ComputeRenderedScopePaths(*lines, &scope_paths);

        for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
            if (line_index >= scope_paths.size()) {
                continue;
            }

            bool is_local = false;
            std::string lhs_expression;
            std::string rhs_expression;
            if (!ParseRenderedSingleAssignmentLine((*lines)[line_index],
                                                   &is_local,
                                                   &lhs_expression,
                                                   &rhs_expression)) {
                continue;
            }

            const std::string alias_name =
                StripRenderedWholeParentheses(TrimLuaWhitespace(lhs_expression), nullptr);
            const std::string target_expression =
                StripRenderedWholeParentheses(TrimLuaWhitespace(rhs_expression), nullptr);
            if (!is_ephemeral_alias_name(alias_name) || target_expression.empty() ||
                IsRenderTemporaryName(target_expression) ||
                !IsRenderedSafePrefixExpression(target_expression)) {
                continue;
            }
            if (CountIdentifierUses(target_expression, alias_name) != 0) {
                if (has_immediate_alias_call_use(line_index, alias_name)) {
                    continue;
                }
            }

            const std::vector<std::size_t>& declaration_scope = scope_paths[line_index];
            bool alias_used = false;
            for (std::size_t probe = line_index + 1; probe < lines->size(); ++probe) {
                if (probe >= scope_paths.size()) {
                    break;
                }

                const std::vector<std::size_t>& probe_scope = scope_paths[probe];
                const bool same_scope = probe_scope == declaration_scope;
                const bool nested_scope = scope_path_is_prefix(declaration_scope, probe_scope) &&
                                          probe_scope != declaration_scope;
                if (!same_scope && !nested_scope) {
                    continue;
                }

                std::vector<std::string> local_names;
                if (ParseRenderedLocalDeclarationLine((*lines)[probe], &local_names) &&
                    std::find(local_names.begin(), local_names.end(), alias_name) !=
                        local_names.end()) {
                    break;
                }

                bool probe_is_local = false;
                std::string probe_lhs;
                std::string probe_rhs;
                if (ParseRenderedSingleAssignmentLine((*lines)[probe],
                                                     &probe_is_local,
                                                     &probe_lhs,
                                                     &probe_rhs) &&
                    StripRenderedWholeParentheses(TrimLuaWhitespace(probe_lhs), nullptr) ==
                        alias_name) {
                    if (CountIdentifierUses(probe_rhs, alias_name) != 0 &&
                        has_immediate_alias_call_use(probe, alias_name)) {
                        alias_used = true;
                    }
                    break;
                }

                if (CountIdentifierUses((*lines)[probe], alias_name) != 0) {
                    alias_used = true;
                    break;
                }
            }

            if (alias_used) {
                continue;
            }
            if (IsRenderedAssignmentUsedAfterContainingControlBlock(
                    *lines, line_index, alias_name)) {
                continue;
            }

            lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index));
            return true;
        }

        return false;
    });
}

bool SimplifyRenderedLuaScopedMemberAliases(std::vector<std::string>* lines) {
    if (lines == nullptr || lines->empty()) {
        return false;
    }

    const auto scope_path_is_prefix = [](const std::vector<std::size_t>& prefix,
                                         const std::vector<std::size_t>& candidate) {
        return prefix.size() <= candidate.size() &&
               std::equal(prefix.begin(), prefix.end(), candidate.begin());
    };
    const auto has_local_binding_before =
        [&](const std::vector<std::vector<std::size_t>>& scope_paths,
            std::size_t function_begin,
            std::size_t line_index,
            std::string_view identifier) {
            if (identifier.empty()) {
                return false;
            }

            std::unordered_set<std::string> function_parameters;
            if (function_begin < lines->size()) {
                SeedRenderedFunctionParameters((*lines)[function_begin], &function_parameters);
                if (function_parameters.contains(std::string(identifier))) {
                    return true;
                }
            }

            const std::vector<std::size_t> current_scope =
                line_index < scope_paths.size() ? scope_paths[line_index]
                                                : std::vector<std::size_t>{};
            const std::size_t scan_begin = function_begin < lines->size() ? function_begin + 1 : 0;
            for (std::size_t probe = scan_begin; probe < line_index && probe < lines->size(); ++probe) {
                if (probe >= scope_paths.size() || scope_paths[probe] != current_scope) {
                    continue;
                }

                std::vector<std::string> local_names;
                if (ParseRenderedLocalDeclarationLine((*lines)[probe], &local_names) &&
                    std::find(local_names.begin(), local_names.end(), identifier) !=
                        local_names.end()) {
                    return true;
                }

                bool probe_is_local = false;
                std::string probe_lhs;
                std::string probe_rhs;
                if (ParseRenderedSingleAssignmentLine((*lines)[probe],
                                                     &probe_is_local,
                                                     &probe_lhs,
                                                     &probe_rhs) &&
                    probe_is_local &&
                    StripRenderedWholeParentheses(TrimLuaWhitespace(probe_lhs), nullptr) ==
                        identifier) {
                    return true;
                }
            }

            return false;
        };

    return RunTableAliasCleanupPassLoop(32, [&]() {
        std::vector<std::vector<std::size_t>> scope_paths;
        ComputeRenderedScopePaths(*lines, &scope_paths);

        for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
            if (line_index >= scope_paths.size() || scope_paths[line_index].empty()) {
                continue;
            }

            bool is_local = false;
            std::string lhs_expression;
            std::string rhs_expression;
            if (!ParseRenderedSingleAssignmentLine((*lines)[line_index],
                                                   &is_local,
                                                   &lhs_expression,
                                                   &rhs_expression) ||
                !is_local || !IsRenderTemporaryName(lhs_expression)) {
                continue;
            }

            const std::string normalized_rhs =
                StripRenderedWholeParentheses(TrimLuaWhitespace(rhs_expression), nullptr);
            if (normalized_rhs.empty() || IsRenderTemporaryName(normalized_rhs) ||
                normalized_rhs == "self" || normalized_rhs.starts_with("self.") ||
                !IsRenderedSafePrefixExpression(normalized_rhs)) {
                continue;
            }
            if (CountIdentifierUses(normalized_rhs, lhs_expression) > 1) {
                continue;
            }

            const std::vector<std::size_t> current_scope = scope_paths[line_index];
            std::string current_target = normalized_rhs;
            std::vector<std::size_t> removable_alias_lines;
            removable_alias_lines.push_back(line_index);
            bool failed = false;
            std::vector<std::pair<std::size_t, std::string>> rewritten_lines;
            for (std::size_t probe = line_index + 1; probe < lines->size(); ++probe) {
                if (probe >= scope_paths.size()) {
                    break;
                }

                const std::vector<std::size_t>& probe_scope = scope_paths[probe];
                if (scope_path_is_prefix(current_scope, probe_scope) &&
                    probe_scope != current_scope &&
                    CountIdentifierUses((*lines)[probe], lhs_expression) != 0) {
                    failed = true;
                    break;
                }
                if (probe_scope != current_scope) {
                    continue;
                }

                bool probe_is_local = false;
                std::string probe_lhs;
                std::string probe_rhs;
                if (ParseRenderedSingleAssignmentLine(
                        (*lines)[probe], &probe_is_local, &probe_lhs, &probe_rhs) &&
                    StripRenderedWholeParentheses(TrimLuaWhitespace(probe_lhs), nullptr) ==
                        lhs_expression) {
                    std::string normalized_probe_rhs =
                        StripRenderedWholeParentheses(TrimLuaWhitespace(probe_rhs), nullptr);
                    if (CountIdentifierUses(normalized_probe_rhs, lhs_expression) != 0) {
                        std::string rewritten_probe_rhs;
                        if (!ReplaceRenderedSafePrefixExpressionChainUses(
                                normalized_probe_rhs,
                                lhs_expression,
                                current_target,
                                &rewritten_probe_rhs) ||
                            rewritten_probe_rhs.empty()) {
                            break;
                        }
                        normalized_probe_rhs = std::move(rewritten_probe_rhs);
                    }
                    if (normalized_probe_rhs.empty() ||
                        IsRenderTemporaryName(normalized_probe_rhs) ||
                        normalized_probe_rhs == "self" ||
                        normalized_probe_rhs.starts_with("self.") ||
                        !IsRenderedSafePrefixExpression(normalized_probe_rhs) ||
                        CountIdentifierUses(normalized_probe_rhs, lhs_expression) != 0) {
                        break;
                    }

                    removable_alias_lines.push_back(probe);
                    current_target = normalized_probe_rhs;
                    continue;
                }

                const std::size_t use_count = CountIdentifierUses((*lines)[probe], lhs_expression);
                if (use_count == 0) {
                    continue;
                }

                std::string rewritten;
                if (!RewriteRenderedAliasTargetUsesInStatementLine((*lines)[probe],
                                                                   lhs_expression,
                                                                   current_target,
                                                                   &rewritten) ||
                    rewritten.empty()) {
                    failed = true;
                    break;
                }

                rewritten_lines.emplace_back(probe, std::move(rewritten));
            }

            if (failed || rewritten_lines.empty()) {
                continue;
            }

            bool remaining_uses = false;
            for (std::size_t probe = line_index + 1; probe < lines->size(); ++probe) {
                if (probe >= scope_paths.size()) {
                    break;
                }

                const std::vector<std::size_t>& probe_scope = scope_paths[probe];
                if (scope_path_is_prefix(current_scope, probe_scope) &&
                    probe_scope != current_scope &&
                    CountIdentifierUses((*lines)[probe], lhs_expression) != 0) {
                    remaining_uses = true;
                    break;
                }
                if (probe_scope != current_scope) {
                    continue;
                }

                bool probe_is_local = false;
                std::string probe_lhs;
                std::string probe_rhs;
                if (ParseRenderedSingleAssignmentLine(
                        (*lines)[probe], &probe_is_local, &probe_lhs, &probe_rhs) &&
                    StripRenderedWholeParentheses(TrimLuaWhitespace(probe_lhs), nullptr) ==
                        lhs_expression) {
                    continue;
                }

                const bool already_rewritten =
                    std::any_of(rewritten_lines.begin(),
                                rewritten_lines.end(),
                                [&](const auto& rewritten_entry) {
                                    return rewritten_entry.first == probe;
                                });
                if (already_rewritten) {
                    continue;
                }
                if (std::find(removable_alias_lines.begin(),
                              removable_alias_lines.end(),
                              probe) != removable_alias_lines.end()) {
                    continue;
                }

                if (CountIdentifierUses((*lines)[probe], lhs_expression) != 0) {
                    remaining_uses = true;
                    break;
                }
            }

            for (const auto& [probe, rewritten] : rewritten_lines) {
                (*lines)[probe] = rewritten;
            }
            if (!remaining_uses) {
                std::sort(removable_alias_lines.begin(), removable_alias_lines.end());
                for (auto it = removable_alias_lines.rbegin();
                     it != removable_alias_lines.rend();
                     ++it) {
                    lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(*it));
                }
            }
            return true;
        }

        return false;
    });
}

bool SimplifyRenderedLuaScopedSequentialMemberCarries(std::vector<std::string>* lines) {
    if (lines == nullptr || lines->empty()) {
        return false;
    }

    const auto has_member_target_prefix = [](std::string_view expression,
                                             std::string_view table_name) {
        if (table_name.empty() || expression.size() <= table_name.size() ||
            expression.substr(0, table_name.size()) != table_name) {
            return false;
        }

        const char next = expression[table_name.size()];
        return next == '.' || next == '[' || next == ':';
    };

    return RunTableAliasCleanupPassLoop(32, [&]() {
        std::vector<std::vector<std::size_t>> scope_paths;
        ComputeRenderedScopePaths(*lines, &scope_paths);

        for (std::size_t line_index = 0; line_index + 1 < lines->size(); ++line_index) {
            if (line_index >= scope_paths.size() || scope_paths[line_index].empty()) {
                continue;
            }

            bool first_is_local = false;
            std::string first_lhs;
            std::string first_rhs;
            if (!ParseRenderedSingleAssignmentLine((*lines)[line_index],
                                                   &first_is_local,
                                                   &first_lhs,
                                                   &first_rhs) ||
                !first_is_local) {
                continue;
            }

            first_lhs = StripRenderedWholeParentheses(TrimLuaWhitespace(first_lhs), nullptr);
            first_rhs = StripRenderedWholeParentheses(TrimLuaWhitespace(first_rhs), nullptr);
            if (!IsValidLuaIdentifier(first_lhs) || IsLuaKeyword(first_lhs) ||
                first_rhs.empty() || !IsRenderedSafePrefixExpression(first_rhs)) {
                continue;
            }

            std::size_t next_line = line_index + 1;
            while (next_line < lines->size() &&
                   TrimLuaWhitespace(StripLuaLineComment((*lines)[next_line])).empty()) {
                ++next_line;
            }
            if (next_line >= lines->size() || next_line >= scope_paths.size() ||
                scope_paths[next_line] != scope_paths[line_index]) {
                continue;
            }

            bool second_is_local = false;
            std::string second_lhs;
            std::string second_rhs;
            if (!ParseRenderedSingleAssignmentLine((*lines)[next_line],
                                                   &second_is_local,
                                                   &second_lhs,
                                                   &second_rhs) ||
                second_is_local) {
                continue;
            }

            second_lhs =
                StripRenderedWholeParentheses(TrimLuaWhitespace(second_lhs), nullptr);
            second_rhs =
                StripRenderedWholeParentheses(TrimLuaWhitespace(second_rhs), nullptr);
            if (second_lhs != first_lhs || !has_member_target_prefix(second_rhs, first_lhs)) {
                continue;
            }

            const std::string replacement_rhs =
                first_rhs + second_rhs.substr(first_lhs.size());
            if (!IsRenderedSafePrefixExpression(replacement_rhs)) {
                continue;
            }

            const std::string indent(GetRenderedLuaIndent((*lines)[line_index]));
            const std::string_view stripped_second =
                StripLuaLineComment((*lines)[next_line]);
            const std::string_view comment =
                stripped_second.size() < (*lines)[next_line].size()
                    ? std::string_view((*lines)[next_line]).substr(stripped_second.size())
                    : std::string_view();
            (*lines)[line_index] =
                indent + "local " + first_lhs + " = " + replacement_rhs + std::string(comment);
            lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(next_line));
            return true;
        }

        return false;
    });
}

bool SimplifyRenderedLuaTopLevelImmediateResultAliases(std::vector<std::string>* lines) {
    if (lines == nullptr || lines->empty()) {
        return false;
    }

    const auto next_top_level_nonempty =
        [&](const std::vector<std::vector<std::size_t>>& scope_paths, std::size_t start) {
            while (start < lines->size()) {
                const std::string_view trimmed =
                    TrimLuaWhitespace(StripLuaLineComment((*lines)[start]));
                if (!trimmed.empty() && start < scope_paths.size() && scope_paths[start].empty()) {
                    return start;
                }
                ++start;
            }
            return lines->size();
        };
    const auto is_table_literal_seed = [](std::string_view expression) {
        const std::string normalized =
            StripRenderedWholeParentheses(TrimLuaWhitespace(expression), nullptr);
        return normalized == "{}" || normalized == "{ }" || normalized == "{";
    };

    return RunTableAliasCleanupPassLoop(32, [&]() {
        std::vector<std::vector<std::size_t>> scope_paths;
        ComputeRenderedScopePaths(*lines, &scope_paths);

        for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
            if (line_index >= scope_paths.size() || !scope_paths[line_index].empty()) {
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

            const std::string temp_name =
                StripRenderedWholeParentheses(TrimLuaWhitespace(seed_lhs), nullptr);
            const std::string normalized_rhs =
                StripRenderedWholeParentheses(TrimLuaWhitespace(seed_rhs), nullptr);
            if (!IsRenderTemporaryName(temp_name) || normalized_rhs.empty() ||
                normalized_rhs.find('\n') != std::string::npos ||
                normalized_rhs.starts_with("function(") ||
                normalized_rhs.starts_with("function ")) {
                continue;
            }

            const std::size_t alias_line = next_top_level_nonempty(scope_paths, line_index + 1);
            if (alias_line >= lines->size()) {
                continue;
            }

            bool alias_is_local = false;
            std::string alias_lhs;
            std::string alias_rhs;
            if (!ParseRenderedSingleAssignmentLine((*lines)[alias_line],
                                                   &alias_is_local,
                                                   &alias_lhs,
                                                   &alias_rhs) ||
                alias_is_local) {
                continue;
            }

            const std::string normalized_alias_rhs =
                StripRenderedWholeParentheses(TrimLuaWhitespace(alias_rhs), nullptr);
            if (normalized_alias_rhs != temp_name) {
                continue;
            }

            const std::string normalized_alias_lhs =
                StripRenderedWholeParentheses(TrimLuaWhitespace(alias_lhs), nullptr);
            if (normalized_alias_lhs.empty() ||
                CountIdentifierUses(normalized_alias_lhs, temp_name) != 0 ||
                CountIdentifierUses(normalized_rhs, temp_name) != 0) {
                continue;
            }
            if (normalized_alias_lhs.starts_with(normalized_rhs) &&
                normalized_alias_lhs.size() > normalized_rhs.size() &&
                normalized_alias_lhs[normalized_rhs.size()] == '.') {
                continue;
            }

            bool used_later = false;
            bool target_becomes_table_builder = false;
            for (std::size_t probe = alias_line + 1; probe < lines->size(); ++probe) {
                if (probe >= scope_paths.size() || !scope_paths[probe].empty()) {
                    continue;
                }

                bool probe_is_local = false;
                std::string probe_lhs;
                std::string probe_rhs;
                if (ParseRenderedSingleAssignmentLine((*lines)[probe],
                                                      &probe_is_local,
                                                      &probe_lhs,
                                                      &probe_rhs) &&
                    StripRenderedWholeParentheses(TrimLuaWhitespace(probe_lhs), nullptr) ==
                        temp_name) {
                    break;
                }
                if (ParseRenderedSingleAssignmentLine((*lines)[probe],
                                                      &probe_is_local,
                                                      &probe_lhs,
                                                      &probe_rhs) &&
                    StripRenderedWholeParentheses(TrimLuaWhitespace(probe_lhs), nullptr) ==
                        normalized_alias_lhs) {
                    break;
                }

                std::string field_key;
                std::string field_rhs_expression;
                bool rhs_is_function = false;
                int array_index = -1;
                if (ParseRenderedTableFieldAssignment((*lines)[probe],
                                                      normalized_alias_lhs,
                                                      &field_key,
                                                      &field_rhs_expression,
                                                      &rhs_is_function,
                                                      &array_index)) {
                    target_becomes_table_builder = true;
                    break;
                }

                if (CountIdentifierUses((*lines)[probe], temp_name) != 0) {
                    used_later = true;
                    break;
                }
            }
            if (used_later ||
                (target_becomes_table_builder && !is_table_literal_seed(normalized_rhs))) {
                continue;
            }

            const std::string indent(GetRenderedLuaIndent((*lines)[alias_line]));
            const std::string_view stripped = StripLuaLineComment((*lines)[alias_line]);
            const std::string_view comment =
                stripped.size() < (*lines)[alias_line].size()
                    ? std::string_view((*lines)[alias_line]).substr(stripped.size())
                    : std::string_view();
            (*lines)[alias_line] =
                indent + normalized_alias_lhs + " = " + normalized_rhs + std::string(comment);
            lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index));
            return true;
        }

        return false;
    });
}

bool SimplifyRenderedLuaLocalTableBuilderCallAliases(std::vector<std::string>* lines) {
    if (lines == nullptr || lines->empty()) {
        return false;
    }

    const auto scope_path_is_prefix = [](const std::vector<std::size_t>& prefix,
                                         const std::vector<std::size_t>& candidate) {
        return prefix.size() <= candidate.size() &&
               std::equal(prefix.begin(), prefix.end(), candidate.begin());
    };
    const auto has_member_target_prefix = [](std::string_view expression,
                                             std::string_view table_name) {
        if (table_name.empty() || expression.size() <= table_name.size() ||
            expression.substr(0, table_name.size()) != table_name) {
            return false;
        }

        const char next = expression[table_name.size()];
        return next == '.' || next == '[' || next == ':';
    };
    const auto rewrite_identifier_line = [](std::string_view line,
                                            std::string_view identifier,
                                            std::string_view replacement,
                                            std::string* rewritten_out) {
        if (rewritten_out == nullptr) {
            return false;
        }

        const std::string_view stripped = StripLuaLineComment(line);
        const std::string_view comment =
            stripped.size() < line.size() ? line.substr(stripped.size()) : std::string_view();
        std::string rewritten_body;
        if (!ReplaceRenderedIdentifierUses(stripped, identifier, replacement, &rewritten_body)) {
            return false;
        }
        *rewritten_out = rewritten_body + std::string(comment);
        return true;
    };
    const auto has_local_binding_before =
        [&](const std::vector<std::vector<std::size_t>>& scope_paths,
            std::size_t function_begin,
            std::size_t line_index,
            std::string_view identifier) {
            if (identifier.empty()) {
                return false;
            }

            std::unordered_set<std::string> function_parameters;
            if (function_begin < lines->size()) {
                SeedRenderedFunctionParameters((*lines)[function_begin], &function_parameters);
                if (function_parameters.contains(std::string(identifier))) {
                    return true;
                }
            }

            const std::vector<std::size_t> current_scope =
                line_index < scope_paths.size() ? scope_paths[line_index]
                                                : std::vector<std::size_t>{};
            const std::size_t scan_begin = function_begin < lines->size() ? function_begin + 1 : 0;
            for (std::size_t probe = scan_begin; probe < line_index && probe < lines->size(); ++probe) {
                if (probe >= scope_paths.size() || scope_paths[probe] != current_scope) {
                    continue;
                }

                std::vector<std::string> local_names;
                if (ParseRenderedLocalDeclarationLine((*lines)[probe], &local_names) &&
                    std::find(local_names.begin(), local_names.end(), identifier) !=
                        local_names.end()) {
                    return true;
                }

                bool probe_is_local = false;
                std::string probe_lhs;
                std::string probe_rhs;
                if (ParseRenderedSingleAssignmentLine((*lines)[probe],
                                                     &probe_is_local,
                                                     &probe_lhs,
                                                     &probe_rhs) &&
                    probe_is_local &&
                    StripRenderedWholeParentheses(TrimLuaWhitespace(probe_lhs), nullptr) ==
                        identifier) {
                    return true;
                }
            }

            return false;
        };

    return RunTableAliasCleanupPassLoop(16, [&]() {
        std::vector<std::vector<std::size_t>> scope_paths;
        ComputeRenderedScopePaths(*lines, &scope_paths);

        bool pass_changed = false;
        for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
            if (line_index >= scope_paths.size() || scope_paths[line_index].empty()) {
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

            const std::string temp_name =
                StripRenderedWholeParentheses(TrimLuaWhitespace(seed_lhs), nullptr);
            const std::string normalized_rhs =
                StripRenderedWholeParentheses(TrimLuaWhitespace(seed_rhs), nullptr);
            if (!IsRenderTemporaryName(temp_name) || normalized_rhs != "{}") {
                continue;
            }

            const std::vector<std::size_t> current_scope = scope_paths[line_index];
            std::string alias_name;
            std::size_t alias_line = lines->size();
            std::size_t member_mutation_count = 0;
            bool failed = false;
            for (std::size_t probe = line_index + 1; probe < lines->size(); ++probe) {
                if (probe >= scope_paths.size()) {
                    break;
                }

                const std::vector<std::size_t>& probe_scope = scope_paths[probe];
                if (scope_path_is_prefix(current_scope, probe_scope) &&
                    probe_scope != current_scope &&
                    CountIdentifierUses((*lines)[probe], temp_name) != 0) {
                    failed = true;
                    break;
                }
                if (probe_scope != current_scope) {
                    continue;
                }

                bool probe_is_local = false;
                std::string probe_lhs;
                std::string probe_rhs;
                if (ParseRenderedSingleAssignmentLine(
                        (*lines)[probe], &probe_is_local, &probe_lhs, &probe_rhs)) {
                    const std::string normalized_probe_lhs =
                        StripRenderedWholeParentheses(TrimLuaWhitespace(probe_lhs), nullptr);
                    if (normalized_probe_lhs == temp_name) {
                        failed = true;
                        break;
                    }
                    if (has_member_target_prefix(normalized_probe_lhs, temp_name)) {
                        ++member_mutation_count;
                    }
                }

                std::string callee;
                std::vector<std::string> arguments;
                if (!ParseRenderedSingleCallLine((*lines)[probe], &callee, &arguments) ||
                    arguments.size() != 1) {
                    continue;
                }

                const std::string normalized_argument =
                    StripRenderedWholeParentheses(TrimLuaWhitespace(arguments.front()), nullptr);
                if (!IsValidLuaIdentifier(normalized_argument) ||
                    IsRenderTemporaryName(normalized_argument) ||
                    normalized_argument == temp_name) {
                    continue;
                }
                if (CountIdentifierUses((*lines)[probe], temp_name) != 0) {
                    continue;
                }

                alias_name = normalized_argument;
                alias_line = probe;
                break;
            }

            if (failed || alias_name.empty() || alias_line >= lines->size() ||
                member_mutation_count == 0) {
                continue;
            }

            const std::size_t function_begin =
                FindEnclosingRenderedFunctionBlockBegin(*lines, line_index);
            if (has_local_binding_before(scope_paths, function_begin, alias_line, alias_name)) {
                continue;
            }

            std::size_t alias_use_count = 0;
            for (std::size_t probe = line_index + 1; probe < lines->size(); ++probe) {
                if (probe >= scope_paths.size()) {
                    break;
                }

                const std::vector<std::size_t>& probe_scope = scope_paths[probe];
                if (scope_path_is_prefix(current_scope, probe_scope) &&
                    probe_scope != current_scope &&
                    CountIdentifierUses((*lines)[probe], alias_name) != 0) {
                    alias_use_count = 2;
                    break;
                }
                if (probe_scope != current_scope) {
                    continue;
                }

                alias_use_count += CountIdentifierUses((*lines)[probe], alias_name);
                if (alias_use_count > 1) {
                    break;
                }
            }
            if (alias_use_count != 1) {
                continue;
            }

            const std::string seed_indent(GetRenderedLuaIndent((*lines)[line_index]));
            (*lines)[line_index] = seed_indent + "local " + alias_name + " = {}";
            bool rewrite_failed = false;
            for (std::size_t probe = line_index + 1; probe < lines->size(); ++probe) {
                if (probe >= scope_paths.size()) {
                    break;
                }

                const std::vector<std::size_t>& probe_scope = scope_paths[probe];
                if (scope_path_is_prefix(current_scope, probe_scope) &&
                    probe_scope != current_scope &&
                    CountIdentifierUses((*lines)[probe], temp_name) != 0) {
                    rewrite_failed = true;
                    break;
                }
                if (probe_scope != current_scope) {
                    continue;
                }

                bool probe_is_local = false;
                std::string probe_lhs;
                std::string probe_rhs;
                if (ParseRenderedSingleAssignmentLine(
                        (*lines)[probe], &probe_is_local, &probe_lhs, &probe_rhs) &&
                    StripRenderedWholeParentheses(TrimLuaWhitespace(probe_lhs), nullptr) ==
                        temp_name) {
                    rewrite_failed = true;
                    break;
                }

                if (CountIdentifierUses((*lines)[probe], temp_name) == 0) {
                    continue;
                }

                std::string rewritten;
                if (!rewrite_identifier_line((*lines)[probe], temp_name, alias_name, &rewritten)) {
                    rewrite_failed = true;
                    break;
                }
                (*lines)[probe] = std::move(rewritten);
            }

            if (rewrite_failed) {
                continue;
            }

            pass_changed = true;
            break;
        }
        return pass_changed;
    });
}

bool SimplifyRenderedLuaTopLevelFunctionAliasAssignments(std::vector<std::string>* lines) {
    if (lines == nullptr || lines->empty()) {
        return false;
    }

    const auto next_top_level_nonempty = [&](const std::vector<std::vector<std::size_t>>& scope_paths,
                                             std::size_t start) {
        while (start < lines->size()) {
            const std::string_view trimmed =
                TrimLuaWhitespace(StripLuaLineComment((*lines)[start]));
            if (!trimmed.empty() && start < scope_paths.size() && scope_paths[start].empty()) {
                return start;
            }
            ++start;
        }
        return lines->size();
    };

    const auto parse_temp_target_assignment = [](std::string_view line,
                                                 std::string* temp_name,
                                                 std::string* target_expression) {
        bool is_local = false;
        std::string lhs_expression;
        std::string rhs_expression;
        if (!ParseRenderedSingleAssignmentLine(line,
                                               &is_local,
                                               &lhs_expression,
                                               &rhs_expression) ||
            is_local) {
            return false;
        }

        lhs_expression =
            StripRenderedWholeParentheses(TrimLuaWhitespace(lhs_expression), nullptr);
        rhs_expression =
            StripRenderedWholeParentheses(TrimLuaWhitespace(rhs_expression), nullptr);
        if (!IsRenderTemporaryName(lhs_expression) || rhs_expression.empty() ||
            IsRenderTemporaryName(rhs_expression) ||
            !IsRenderedSafePrefixExpression(rhs_expression)) {
            return false;
        }

        if (temp_name != nullptr) {
            *temp_name = std::move(lhs_expression);
        }
        if (target_expression != nullptr) {
            *target_expression = std::move(rhs_expression);
        }
        return true;
    };

    const auto parse_temp_function_assignment = [](std::string_view line,
                                                   std::string* temp_name) {
        bool is_local = false;
        std::string lhs_expression;
        std::string rhs_expression;
        if (!ParseRenderedSingleAssignmentLine(line,
                                               &is_local,
                                               &lhs_expression,
                                               &rhs_expression)) {
            return false;
        }

        lhs_expression =
            StripRenderedWholeParentheses(TrimLuaWhitespace(lhs_expression), nullptr);
        rhs_expression = std::string(TrimLuaWhitespace(rhs_expression));
        if (!IsRenderTemporaryName(lhs_expression) ||
            (!rhs_expression.starts_with("function(") &&
             !rhs_expression.starts_with("function "))) {
            return false;
        }

        if (temp_name != nullptr) {
            *temp_name = std::move(lhs_expression);
        }
        return true;
    };

    const auto parse_temp_method_alias_assignment = [](std::string_view line,
                                                       std::string_view temp_target_name,
                                                       std::string_view temp_function_name,
                                                       std::string* method_name) {
        bool is_local = false;
        std::string lhs_expression;
        std::string rhs_expression;
        if (!ParseRenderedSingleAssignmentLine(line,
                                               &is_local,
                                               &lhs_expression,
                                               &rhs_expression) ||
            is_local) {
            return false;
        }

        lhs_expression =
            StripRenderedWholeParentheses(TrimLuaWhitespace(lhs_expression), nullptr);
        rhs_expression =
            StripRenderedWholeParentheses(TrimLuaWhitespace(rhs_expression), nullptr);
        if (rhs_expression != temp_function_name ||
            !lhs_expression.starts_with(std::string(temp_target_name) + ".")) {
            return false;
        }

        const std::string member =
            lhs_expression.substr(temp_target_name.size() + 1);
        if (!IsValidLuaIdentifier(member) || member.find('.') != std::string_view::npos ||
            member.find('[') != std::string_view::npos) {
            return false;
        }

        if (method_name != nullptr) {
            *method_name = std::string(member);
        }
        return true;
    };

    const auto has_identifier_use_between =
        [&](std::string_view identifier, std::size_t begin, std::size_t end) {
        if (identifier.empty() || begin >= end || begin >= lines->size()) {
            return false;
        }
        end = std::min(end, lines->size());
        for (std::size_t probe = begin; probe < end; ++probe) {
            if (CountIdentifierUses((*lines)[probe], identifier) != 0) {
                return true;
            }
        }
        return false;
    };

    return RunTableAliasCleanupPassLoop(32, [&]() {
        std::vector<std::vector<std::size_t>> scope_paths;
        ComputeRenderedScopePaths(*lines, &scope_paths);

        for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
            if (line_index >= scope_paths.size() || !scope_paths[line_index].empty()) {
                continue;
            }

            std::string temp_target_name;
            std::string target_expression;
            if (!parse_temp_target_assignment((*lines)[line_index],
                                              &temp_target_name,
                                              &target_expression)) {
                continue;
            }

            const std::size_t function_line = next_top_level_nonempty(scope_paths, line_index + 1);
            if (function_line >= lines->size()) {
                continue;
            }

            std::string temp_function_name;
            if (!parse_temp_function_assignment((*lines)[function_line], &temp_function_name)) {
                continue;
            }

            const std::size_t function_end = FindRenderedFunctionBlockEnd(*lines, function_line);
            if (function_end >= lines->size() || function_end <= function_line) {
                continue;
            }

            const std::size_t alias_line = next_top_level_nonempty(scope_paths, function_end + 1);
            if (alias_line >= lines->size()) {
                continue;
            }

            std::string method_name;
            if (!parse_temp_method_alias_assignment((*lines)[alias_line],
                                                    temp_target_name,
                                                    temp_function_name,
                                                    &method_name)) {
                continue;
            }

            if (has_identifier_use_between(temp_target_name, line_index + 1, alias_line) ||
                has_identifier_use_between(temp_function_name, function_end + 1, alias_line)) {
                continue;
            }

            bool temp_escapes = false;
            for (std::size_t probe = alias_line + 1; probe < lines->size(); ++probe) {
                const std::string_view trimmed =
                    TrimLuaWhitespace(StripLuaLineComment((*lines)[probe]));
                if (trimmed.empty()) {
                    continue;
                }

                if (IsAssignmentToIdentifier((*lines)[probe], temp_target_name) ||
                    IsAssignmentToIdentifier((*lines)[probe], temp_function_name)) {
                    break;
                }
                if (CountIdentifierUses((*lines)[probe], temp_target_name) != 0 ||
                    CountIdentifierUses((*lines)[probe], temp_function_name) != 0) {
                    temp_escapes = true;
                    break;
                }
            }
            if (temp_escapes) {
                continue;
            }

            const std::size_t function_pos = (*lines)[function_line].find("function");
            if (function_pos == std::string::npos) {
                continue;
            }

            const std::string indent(GetRenderedLuaIndent((*lines)[function_line]));
            (*lines)[function_line] =
                indent + target_expression + "." + method_name + " = " +
                (*lines)[function_line].substr(function_pos);
            lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(alias_line));
            lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index));
            return true;
        }

        return false;
    });
}

bool SimplifyRenderedLuaTopLevelTableLiteralAliasBuilders(std::vector<std::string>* lines) {
    if (lines == nullptr || lines->empty()) {
        return false;
    }

    std::vector<std::vector<std::size_t>> scope_paths;
    ComputeRenderedScopePaths(*lines, &scope_paths);

    return RunTableAliasCleanupPassLoop(32, [&]() {
        for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
            if (line_index >= scope_paths.size() || !scope_paths[line_index].empty()) {
                continue;
            }

            std::string indent;
            std::string lhs_prefix;
            std::string table_name;
            if (!ParseRenderedTableLiteralStartLine((*lines)[line_index],
                                                    &indent,
                                                    &lhs_prefix,
                                                    &table_name) ||
                !IsRenderTemporaryName(table_name)) {
                continue;
            }

            const std::size_t table_end = FindRenderedTableLiteralEnd(*lines, line_index);
            if (table_end >= lines->size() || table_end <= line_index) {
                continue;
            }

            std::size_t alias_line = lines->size();
            std::string target_expression;
            bool failed = false;
            for (std::size_t probe = table_end + 1; probe < lines->size(); ++probe) {
                if (probe >= scope_paths.size() || !scope_paths[probe].empty()) {
                    continue;
                }

                bool probe_is_local = false;
                std::string probe_lhs;
                std::string probe_rhs;
                if (ParseRenderedSingleAssignmentLine(
                        (*lines)[probe], &probe_is_local, &probe_lhs, &probe_rhs) &&
                    StripRenderedWholeParentheses(TrimLuaWhitespace(probe_lhs), nullptr) ==
                        table_name) {
                    break;
                }

                if (ParseRenderedIdentifierAliasAssignment(
                        (*lines)[probe], table_name, &target_expression)) {
                    alias_line = probe;
                    break;
                }

                const std::string_view trimmed =
                    TrimLuaWhitespace(StripLuaLineComment((*lines)[probe]));
                if (trimmed.empty()) {
                    continue;
                }

                std::string field_key;
                std::string rhs_expression;
                bool rhs_is_function = false;
                int array_index = -1;
                if (!ParseRenderedTableFieldAssignment((*lines)[probe],
                                                      table_name,
                                                      &field_key,
                                                      &rhs_expression,
                                                      &rhs_is_function,
                                                      &array_index)) {
                    failed = true;
                    break;
                }

                if (rhs_is_function) {
                    const std::size_t function_end = FindRenderedFunctionBlockEnd(*lines, probe);
                    if (function_end >= lines->size() || function_end < probe) {
                        failed = true;
                        break;
                    }
                    for (std::size_t body_line = probe + 1; body_line <= function_end; ++body_line) {
                        if (CountIdentifierUses((*lines)[body_line], table_name) != 0) {
                            failed = true;
                            break;
                        }
                    }
                    if (failed) {
                        break;
                    }
                    probe = function_end;
                    continue;
                }

                if (rhs_expression == "{") {
                    const std::size_t nested_end = FindRenderedTableLiteralEnd(*lines, probe);
                    if (nested_end >= lines->size() || nested_end < probe) {
                        failed = true;
                        break;
                    }
                    for (std::size_t body_line = probe + 1; body_line <= nested_end; ++body_line) {
                        if (CountIdentifierUses((*lines)[body_line], table_name) != 0) {
                            failed = true;
                            break;
                        }
                    }
                    if (failed) {
                        break;
                    }
                    probe = nested_end;
                }
            }

            if (failed || alias_line >= lines->size() || target_expression.empty() ||
                target_expression == "self" ||
                target_expression.starts_with("self.") ||
                IsRenderTemporaryName(target_expression) ||
                !IsRenderedSafePrefixExpression(target_expression)) {
                continue;
            }

            std::vector<std::pair<std::size_t, std::string>> rewritten_lines;
            for (std::size_t probe = table_end + 1; probe < alias_line; ++probe) {
                if (probe >= scope_paths.size() || !scope_paths[probe].empty()) {
                    continue;
                }

                if (CountIdentifierUses((*lines)[probe], table_name) == 0) {
                    continue;
                }

                std::string rewritten;
                if (!ReplaceRenderedSafePrefixExpressionChainUsesInStatementLine((*lines)[probe],
                                                                                 table_name,
                                                                                 target_expression,
                                                                                 &rewritten) ||
                    rewritten.empty()) {
                    failed = true;
                    break;
                }
                rewritten_lines.emplace_back(probe, std::move(rewritten));
            }
            if (failed) {
                continue;
            }

            for (std::size_t probe = alias_line + 1; probe < lines->size(); ++probe) {
                if (probe >= scope_paths.size() || !scope_paths[probe].empty()) {
                    continue;
                }

                bool probe_is_local = false;
                std::string probe_lhs;
                std::string probe_rhs;
                if (ParseRenderedSingleAssignmentLine(
                        (*lines)[probe], &probe_is_local, &probe_lhs, &probe_rhs) &&
                    StripRenderedWholeParentheses(TrimLuaWhitespace(probe_lhs), nullptr) ==
                        table_name) {
                    break;
                }
                if (CountIdentifierUses((*lines)[probe], table_name) != 0) {
                    failed = true;
                    break;
                }
            }
            if (failed) {
                continue;
            }

            (*lines)[line_index] = indent + target_expression + " = {";
            for (const auto& [probe, rewritten] : rewritten_lines) {
                (*lines)[probe] = rewritten;
            }
            lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(alias_line));
            return true;
        }

        return false;
    });
}

bool SimplifyRenderedLuaDanglingTopLevelIndexedAliases(std::vector<std::string>* lines) {
    if (lines == nullptr || lines->empty()) {
        return false;
    }

    const auto is_member_rebind_anchor_rhs = [](std::string_view rhs_expression) {
        const std::string normalized =
            StripRenderedWholeParentheses(TrimLuaWhitespace(rhs_expression), nullptr);
        return normalized == "{}" ||
               (normalized.starts_with("DeepCopyTable(") && normalized.ends_with(")"));
    };

    const auto extract_trailing_member_suffix = [](std::string_view expression,
                                                   std::string* suffix) -> bool {
        expression = TrimLuaWhitespace(expression);
        const std::size_t dot = expression.rfind('.');
        if (dot == std::string_view::npos || dot + 1 >= expression.size()) {
            return false;
        }

        const std::string_view member = expression.substr(dot + 1);
        if (!IsValidLuaIdentifier(member)) {
            return false;
        }

        if (suffix != nullptr) {
            *suffix = std::string(expression.substr(dot));
        }
        return true;
    };

    std::vector<std::vector<std::size_t>> scope_paths;
    ComputeRenderedScopePaths(*lines, &scope_paths);

    bool changed = false;
    std::string anchored_base;
    std::string anchored_key;
    std::string anchored_indent;
    std::string anchored_temp_base;
    std::string anchored_temp_key;
    std::unordered_map<std::string, std::string> base_aliases;
    std::unordered_map<std::string, std::string> identifier_aliases;

    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        if (line_index >= scope_paths.size() || !scope_paths[line_index].empty()) {
            anchored_base.clear();
            anchored_key.clear();
            anchored_indent.clear();
            anchored_temp_base.clear();
            anchored_temp_key.clear();
            base_aliases.clear();
            continue;
        }

        const std::string_view trimmed =
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index]));
        if (trimmed.empty()) {
            continue;
        }

        bool is_local = false;
        std::string lhs_expression;
        std::string rhs_expression;
        if (!ParseRenderedSingleAssignmentLine((*lines)[line_index],
                                               &is_local,
                                               &lhs_expression,
                                               &rhs_expression)) {
            anchored_key.clear();
            continue;
        }

        if (IsRenderTemporaryName(lhs_expression)) {
            identifier_aliases.erase(lhs_expression);
            for (auto it = base_aliases.begin(); it != base_aliases.end();) {
                if (it->first.starts_with(lhs_expression) &&
                    it->first.size() > lhs_expression.size() &&
                    it->first[lhs_expression.size()] == '.') {
                    it = base_aliases.erase(it);
                } else {
                    ++it;
                }
            }

            const std::string normalized_rhs =
                StripRenderedWholeParentheses(TrimLuaWhitespace(rhs_expression), nullptr);
            if (IsRenderedSafePrefixExpression(normalized_rhs) &&
                !IsRenderTemporaryName(normalized_rhs)) {
                identifier_aliases[lhs_expression] = normalized_rhs;
            }

            if (lhs_expression == anchored_temp_base || lhs_expression == anchored_temp_key) {
                anchored_base.clear();
                anchored_key.clear();
                anchored_indent.clear();
                anchored_temp_base.clear();
                anchored_temp_key.clear();
            }
            continue;
        }

        std::string lhs_base;
        std::string lhs_key;
        std::string lhs_suffix;
        if (!ParseRenderedIndexedTargetExpression(lhs_expression,
                                                  &lhs_base,
                                                  &lhs_key,
                                                  &lhs_suffix)) {
            if (!is_local && IsRenderedSafePrefixExpression(lhs_expression) &&
                is_member_rebind_anchor_rhs(rhs_expression)) {
                std::string member_suffix;
                if (extract_trailing_member_suffix(lhs_expression, &member_suffix)) {
                    for (const auto& [alias_name, target_expression] : identifier_aliases) {
                        if (target_expression.empty() ||
                            !lhs_expression.starts_with(target_expression) ||
                            lhs_expression.size() <= target_expression.size() + member_suffix.size() ||
                            !lhs_expression.ends_with(member_suffix)) {
                            continue;
                        }

                        base_aliases[alias_name + member_suffix] = lhs_expression;
                    }
                }
            }

            anchored_base.clear();
            anchored_key.clear();
            anchored_indent.clear();
            anchored_temp_base.clear();
            anchored_temp_key.clear();
            continue;
        }

        const std::string current_indent(GetRenderedLuaIndent((*lines)[line_index]));
        if (!anchored_indent.empty() && current_indent != anchored_indent) {
            anchored_base.clear();
            anchored_key.clear();
            anchored_indent.clear();
            anchored_temp_base.clear();
            anchored_temp_key.clear();
            base_aliases.clear();
        }

        const std::string original_base = lhs_base;
        const std::string original_key = lhs_key;
        bool locally_changed = false;

        if (const auto alias_it = base_aliases.find(lhs_base); alias_it != base_aliases.end()) {
            lhs_base = alias_it->second;
            locally_changed = true;
        } else if (IsRenderTemporaryName(lhs_base)) {
            const auto alias_it = base_aliases.find(lhs_base);
            if (alias_it != base_aliases.end()) {
                lhs_base = alias_it->second;
                locally_changed = true;
            }
        }

        if (!anchored_base.empty() && current_indent == anchored_indent) {
            if (IsRenderTemporaryName(original_base) && lhs_key == anchored_key) {
                base_aliases[original_base] = anchored_base;
                lhs_base = anchored_base;
                locally_changed = true;
            }

            if (lhs_base == anchored_base && IsRenderTemporaryName(lhs_key) &&
                !anchored_key.empty()) {
                lhs_key = anchored_key;
                locally_changed = true;
            }

            if (IsRenderTemporaryName(original_base) && IsRenderTemporaryName(original_key) &&
                !lhs_suffix.empty()) {
                if (anchored_temp_base.empty() && anchored_temp_key.empty()) {
                    anchored_temp_base = original_base;
                    anchored_temp_key = original_key;
                }

                if (original_base == anchored_temp_base && original_key == anchored_temp_key) {
                    lhs_base = anchored_base;
                    lhs_key = anchored_key;
                    locally_changed = true;
                }
            }
        }

        if (locally_changed) {
            (*lines)[line_index] = current_indent + (is_local ? "local " : std::string()) +
                                   lhs_base + "[" + lhs_key + "]" + lhs_suffix + " = " +
                                   rhs_expression;
            changed = true;
        }

        if (!IsRenderTemporaryName(lhs_base) && !IsRenderTemporaryName(lhs_key)) {
            if (!anchored_base.empty() && lhs_base != anchored_base) {
                base_aliases.clear();
            }
            anchored_base = lhs_base;
            anchored_key = lhs_key;
            anchored_indent = current_indent;
            anchored_temp_base.clear();
            anchored_temp_key.clear();
        }
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

}  // namespace refii::lua
