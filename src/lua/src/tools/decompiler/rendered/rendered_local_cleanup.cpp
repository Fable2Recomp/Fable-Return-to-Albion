#include "tools/decompiler/rendered/rendered_lua_text.h"
#include "tools/decompiler/rendered/rendered_cleanup_common_helpers.h"

#include <algorithm>
#include <cstddef>
#include <map>
#include <set>
#include <string>
#include <string_view>
#include <vector>

namespace refii::lua {

bool SimplifyRenderedLuaRedundantLocalRedeclarations(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    std::vector<std::vector<std::size_t>> scope_paths;
    ComputeRenderedScopePaths(*lines, &scope_paths);
    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        bool assign_is_local = false;
        std::string assign_lhs;
        std::string assign_rhs;
        std::vector<std::string> local_names;
        const bool is_local_decl =
            ParseRenderedLocalDeclarationLine((*lines)[line_index], &local_names) &&
            local_names.size() == 1;
        const bool is_local_assign =
            ParseRenderedSingleAssignmentLine((*lines)[line_index],
                                             &assign_is_local,
                                             &assign_lhs,
                                             &assign_rhs) &&
            assign_is_local &&
            IsValidLuaIdentifier(assign_lhs);
        if (!is_local_decl && !is_local_assign) {
            continue;
        }

        const std::string target = is_local_decl ? local_names.front() : assign_lhs;
        const std::string indent(GetRenderedLuaIndent((*lines)[line_index]));
        const std::vector<std::size_t> current_scope =
            line_index < scope_paths.size() ? scope_paths[line_index]
                                            : std::vector<std::size_t>{};
        const std::size_t function_begin =
            FindEnclosingRenderedFunctionBlockBegin(*lines, line_index);
        if (function_begin == lines->size()) {
            continue;
        }

        std::size_t previous_decl = lines->size();
        for (std::size_t probe = function_begin + 1; probe < line_index; ++probe) {
            if (probe >= scope_paths.size() || scope_paths[probe] != current_scope) {
                continue;
            }
            if (GetRenderedLuaIndent((*lines)[probe]) != indent) {
                continue;
            }

            bool probe_assign_is_local = false;
            std::vector<std::string> probe_lhs_names;
            std::string probe_rhs;
            if (ParseRenderedAssignmentLine((*lines)[probe],
                                            &probe_assign_is_local,
                                            &probe_lhs_names,
                                            &probe_rhs) &&
                probe_assign_is_local &&
                std::find(probe_lhs_names.begin(), probe_lhs_names.end(), target) !=
                    probe_lhs_names.end()) {
                previous_decl = probe;
                continue;
            }

            std::vector<std::string> probe_local_names;
            if (ParseRenderedLocalDeclarationLine((*lines)[probe], &probe_local_names) &&
                probe_local_names.size() == 1 &&
                probe_local_names.front() == target) {
                previous_decl = probe;
                continue;
            }

            bool probe_is_local = false;
            std::string probe_lhs;
            std::string probe_single_rhs;
            if (ParseRenderedSingleAssignmentLine((*lines)[probe],
                                                 &probe_is_local,
                                                 &probe_lhs,
                                                 &probe_single_rhs) &&
                probe_is_local && probe_lhs == target) {
                previous_decl = probe;
                continue;
            }
        }

        if (previous_decl == lines->size()) {
            continue;
        }

        if (is_local_decl) {
            lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index));
            changed = true;
            ComputeRenderedScopePaths(*lines, &scope_paths);
            if (line_index != 0) {
                --line_index;
            }
            continue;
        }

        (*lines)[line_index] = indent + target + " = " + assign_rhs;
        changed = true;
        ComputeRenderedScopePaths(*lines, &scope_paths);
    }

    return changed;
}

bool SimplifyRenderedLuaIfBranchShadowLocalCarries(std::vector<std::string>* lines) {
    if (lines == nullptr || lines->size() < 4) {
        return false;
    }

    const auto trim_line = [](std::string_view line) -> std::string_view {
        return TrimLuaWhitespace(StripLuaLineComment(line));
    };
    const auto split_line_code_and_comment = [](std::string_view line,
                                                std::string* code_out,
                                                std::string* comment_out) {
        if (code_out == nullptr || comment_out == nullptr) {
            return;
        }
        const std::string_view stripped = StripLuaLineComment(line);
        *code_out = std::string(stripped);
        *comment_out = stripped.size() < line.size() ? std::string(line.substr(stripped.size()))
                                                     : std::string();
    };
    const auto find_if_end = [&](std::size_t if_line) -> std::size_t {
        if (if_line >= lines->size()) {
            return lines->size();
        }

        int balance = 0;
        for (std::size_t probe = if_line; probe < lines->size(); ++probe) {
            balance += GetRenderedLuaBlockDelta(trim_line((*lines)[probe]));
            if (probe > if_line && balance == 0) {
                return probe;
            }
        }
        return lines->size();
    };

    bool changed = false;
    for (std::size_t if_line = 0; if_line + 2 < lines->size(); ++if_line) {
        const std::string_view if_trimmed = trim_line((*lines)[if_line]);
        if (!if_trimmed.starts_with("if ") || !if_trimmed.ends_with(" then")) {
            continue;
        }

        std::size_t if_end = find_if_end(if_line);
        if (if_end >= lines->size() || if_end + 1 >= lines->size()) {
            continue;
        }

        bool has_else_arm = false;
        for (std::size_t probe = if_line + 1; probe < if_end; ++probe) {
            const std::string_view body_trimmed = trim_line((*lines)[probe]);
            if (body_trimmed == "else" || body_trimmed.starts_with("elseif ")) {
                has_else_arm = true;
                break;
            }
        }
        if (has_else_arm) {
            continue;
        }

        std::size_t single_body_line = lines->size();
        bool has_multiple_body_statements = false;
        for (std::size_t probe = if_line + 1; probe < if_end; ++probe) {
            if (trim_line((*lines)[probe]).empty()) {
                continue;
            }
            if (single_body_line != lines->size()) {
                has_multiple_body_statements = true;
                break;
            }
            single_body_line = probe;
        }
        if (single_body_line == lines->size() || has_multiple_body_statements) {
            continue;
        }

        bool branch_is_local = false;
        std::string branch_lhs;
        std::string branch_rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[single_body_line],
                                               &branch_is_local,
                                               &branch_lhs,
                                               &branch_rhs) ||
            !branch_is_local || !IsValidLuaIdentifier(branch_lhs) ||
            CountIdentifierUses(branch_rhs, branch_lhs) != 0) {
            continue;
        }

        bool after_is_local = false;
        std::string after_lhs;
        std::string after_rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[if_end + 1],
                                               &after_is_local,
                                               &after_lhs,
                                               &after_rhs) ||
            !after_is_local || after_lhs != branch_lhs ||
            CountIdentifierUses(after_rhs, after_lhs) != 0) {
            continue;
        }

        const std::string if_indent(GetRenderedLuaIndent((*lines)[if_line]));
        const std::string branch_indent(GetRenderedLuaIndent((*lines)[single_body_line]));
        const std::string after_indent(GetRenderedLuaIndent((*lines)[if_end + 1]));
        if (after_indent != if_indent) {
            continue;
        }

        std::string branch_code;
        std::string branch_comment;
        std::string after_code;
        std::string after_comment;
        split_line_code_and_comment((*lines)[single_body_line], &branch_code, &branch_comment);
        split_line_code_and_comment((*lines)[if_end + 1], &after_code, &after_comment);

        lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(if_line),
                      if_indent + "local " + after_lhs + " = " +
                          std::string(TrimLuaWhitespace(after_rhs)) + after_comment);
        ++single_body_line;
        ++if_end;
        (*lines)[single_body_line] =
            branch_indent + branch_lhs + " = " +
            std::string(TrimLuaWhitespace(branch_rhs)) + branch_comment;
        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(if_end + 1));

        changed = true;
        if (if_line != 0) {
            --if_line;
        }
    }

    return changed;
}

bool SimplifyRenderedLuaImmediateIdentifierCarryLocals(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 1 < lines->size(); ++line_index) {
        bool first_is_local = false;
        std::string first_lhs;
        std::string first_rhs;
        bool second_is_local = false;
        std::string second_lhs;
        std::string second_rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[line_index],
                                               &first_is_local,
                                               &first_lhs,
                                               &first_rhs) ||
            !ParseRenderedSingleAssignmentLine((*lines)[line_index + 1],
                                               &second_is_local,
                                               &second_lhs,
                                               &second_rhs) ||
            !first_is_local || second_is_local || first_lhs != second_lhs) {
            continue;
        }

        const std::string indent(GetRenderedLuaIndent((*lines)[line_index]));
        if (indent != GetRenderedLuaIndent((*lines)[line_index + 1])) {
            continue;
        }

        const std::string normalized_seed =
            StripRenderedWholeParentheses(TrimLuaWhitespace(first_rhs), nullptr);
        if (normalized_seed.empty() ||
            !IsRenderedSafePrefixExpression(normalized_seed) ||
            CountIdentifierUses(normalized_seed, first_lhs) != 0) {
            continue;
        }

        const std::string normalized_carry =
            StripRenderedWholeParentheses(TrimLuaWhitespace(second_rhs), nullptr);
        if (normalized_carry.empty() ||
            CountIdentifierUses(normalized_carry, first_lhs) != 1) {
            continue;
        }

        std::string rewritten_rhs;
        if (!ReplaceRenderedIdentifierUses(normalized_carry,
                                           first_lhs,
                                           WrapInlineExpression(normalized_seed),
                                           &rewritten_rhs)) {
            continue;
        }

        (*lines)[line_index] = indent + "local " + first_lhs + " = " + rewritten_rhs;
        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index + 1));
        changed = true;
        if (line_index != 0) {
            --line_index;
        }
    }

    return changed;
}

bool SimplifyRenderedLuaImmediateExpressionCarryLocals(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 1 < lines->size(); ++line_index) {
        bool first_is_local = false;
        std::string temp_name;
        std::string temp_expression;
        bool second_is_local = false;
        std::string target_expression;
        std::string carried_expression;
        if (!ParseRenderedSingleAssignmentLine((*lines)[line_index],
                                               &first_is_local,
                                               &temp_name,
                                               &temp_expression) ||
            !ParseRenderedSingleAssignmentLine((*lines)[line_index + 1],
                                               &second_is_local,
                                               &target_expression,
                                               &carried_expression) ||
            !first_is_local || second_is_local || temp_name == target_expression) {
            continue;
        }

        const std::string indent(GetRenderedLuaIndent((*lines)[line_index]));
        if (indent != GetRenderedLuaIndent((*lines)[line_index + 1])) {
            continue;
        }

        const std::string normalized_temp =
            StripRenderedWholeParentheses(TrimLuaWhitespace(temp_expression), nullptr);
        if (normalized_temp.empty() ||
            CountIdentifierUses(normalized_temp, temp_name) != 0) {
            continue;
        }
        if (normalized_temp == "self" && target_expression != "self") {
            continue;
        }

        const std::string normalized_carried =
            StripRenderedWholeParentheses(TrimLuaWhitespace(carried_expression), nullptr);
        if (normalized_carried != temp_name) {
            continue;
        }

        (*lines)[line_index] = indent + target_expression + " = " + temp_expression;
        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index + 1));
        changed = true;
        if (line_index != 0) {
            --line_index;
        }
    }

    return changed;
}

bool SimplifyRenderedLuaImmediateMemberCarryLocals(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 1 < lines->size(); ++line_index) {
        bool first_is_local = false;
        std::string local_name;
        std::string seed_expression;
        bool second_is_local = false;
        std::string target_expression;
        std::string member_expression;
        if (!ParseRenderedSingleAssignmentLine((*lines)[line_index],
                                               &first_is_local,
                                               &local_name,
                                               &seed_expression) ||
            !ParseRenderedSingleAssignmentLine((*lines)[line_index + 1],
                                               &second_is_local,
                                               &target_expression,
                                               &member_expression) ||
            !first_is_local || second_is_local || local_name != target_expression) {
            continue;
        }

        const std::string indent(GetRenderedLuaIndent((*lines)[line_index]));
        if (indent != GetRenderedLuaIndent((*lines)[line_index + 1])) {
            continue;
        }

        const std::string normalized_seed =
            StripRenderedWholeParentheses(TrimLuaWhitespace(seed_expression), nullptr);
        if (normalized_seed.empty() ||
            !IsRenderedSafePrefixExpression(normalized_seed) ||
            CountIdentifierUses(normalized_seed, local_name) != 0) {
            continue;
        }
        if (normalized_seed == "self" && target_expression != "self") {
            continue;
        }

        const std::string normalized_member =
            StripRenderedWholeParentheses(TrimLuaWhitespace(member_expression), nullptr);
        if (normalized_member.empty() ||
            CountIdentifierUses(normalized_member, local_name) != 1) {
            continue;
        }

        std::string rewritten_rhs;
        if (!ReplaceRenderedSafePrefixExpressionChainUses(normalized_member,
                                                          local_name,
                                                          normalized_seed,
                                                          &rewritten_rhs) ||
            rewritten_rhs == normalized_member) {
            continue;
        }

        (*lines)[line_index] = indent + "local " + local_name + " = " + rewritten_rhs;
        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index + 1));
        changed = true;
        if (line_index != 0) {
            --line_index;
        }
    }

    return changed;
}

bool SimplifyRenderedLuaImmediateLocalCallFieldConditions(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto normalize_for_call_match = [](std::string expression) {
        expression = StripRenderedWholeParentheses(TrimLuaWhitespace(expression), nullptr);
        std::string normalized;
        normalized.reserve(expression.size());
        for (std::size_t index = 0; index < expression.size(); ++index) {
            if (expression[index] != '(') {
                normalized.push_back(expression[index]);
                continue;
            }
            std::size_t close = index + 1;
            while (close < expression.size() && expression[close] != ')') {
                ++close;
            }
            if (close >= expression.size()) {
                normalized.push_back(expression[index]);
                continue;
            }
            const std::string_view inner =
                TrimLuaWhitespace(std::string_view(expression).substr(index + 1,
                                                                      close - index - 1));
            const bool numeric_literal =
                !inner.empty() &&
                std::all_of(inner.begin(), inner.end(), [](char ch) {
                    return std::isdigit(static_cast<unsigned char>(ch)) || ch == '.' ||
                           ch == '-' || ch == '+';
                });
            if (!numeric_literal) {
                normalized.push_back('(');
            }
            normalized.append(inner);
            if (!numeric_literal) {
                normalized.push_back(')');
            }
            index = close;
        }
        return normalized;
    };

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 1 < lines->size(); ++line_index) {
        bool seed_is_local = false;
        std::string local_name;
        std::string call_expression;
        if (!ParseRenderedSingleAssignmentLine((*lines)[line_index],
                                               &seed_is_local,
                                               &local_name,
                                               &call_expression) ||
            !seed_is_local || !IsValidLuaIdentifier(local_name) ||
            CountIdentifierUses(call_expression, local_name) != 0) {
            continue;
        }

        std::string_view seed_callee;
        std::string_view seed_args;
        if (!TryParseRenderedTrailingCall(call_expression, &seed_callee, &seed_args)) {
            continue;
        }

        const std::string indent(GetRenderedLuaIndent((*lines)[line_index]));
        if (indent != GetRenderedLuaIndent((*lines)[line_index + 1])) {
            continue;
        }

        const std::string_view guard_trimmed =
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 1]));
        if (!guard_trimmed.starts_with("if ") || !guard_trimmed.ends_with(" then")) {
            continue;
        }

        const std::string condition =
            std::string(TrimLuaWhitespace(guard_trimmed.substr(3,
                                                               guard_trimmed.size() - 8)));
        if (condition.find(" and ") != std::string::npos ||
            condition.find(" or ") != std::string::npos ||
            condition.find("==") != std::string::npos ||
            condition.find("~=") != std::string::npos ||
            condition.find("<") != std::string::npos ||
            condition.find(">") != std::string::npos) {
            continue;
        }

        const std::size_t call_end = condition.rfind(").");
        if (call_end == std::string::npos || call_end + 2 >= condition.size()) {
            continue;
        }
        const std::string condition_call = condition.substr(0, call_end + 1);
        std::string_view guard_callee;
        std::string_view guard_args;
        if (!TryParseRenderedTrailingCall(condition_call, &guard_callee, &guard_args)) {
            continue;
        }
        const std::string field_name = condition.substr(call_end + 2);
        if (!IsValidLuaIdentifier(field_name) || IsLuaKeyword(field_name)) {
            continue;
        }

        if (normalize_for_call_match(call_expression) !=
            normalize_for_call_match(condition_call)) {
            continue;
        }

        (*lines)[line_index + 1] = indent + "if " + local_name + "." + field_name + " then";
        changed = true;
    }

    return changed;
}

bool SimplifyRenderedLuaClosureShadowLocalAssignments(std::vector<std::string>* lines) {
    if (lines == nullptr || lines->size() < 4) {
        return false;
    }

    const auto is_plain_local_declaration =
        [](std::string_view line, std::string* name_out) {
            if (name_out == nullptr) {
                return false;
            }
            const std::string_view trimmed =
                TrimLuaWhitespace(StripLuaLineComment(line));
            if (!trimmed.starts_with("local ") ||
                trimmed.find('=') != std::string_view::npos ||
                trimmed.find(',') != std::string_view::npos) {
                return false;
            }
            const std::string name =
                std::string(TrimLuaWhitespace(trimmed.substr(6)));
            if (!IsValidLuaIdentifier(name) || IsLuaKeyword(name)) {
                return false;
            }
            *name_out = name;
            return true;
        };
    const auto is_return_function_header = [](std::string_view line) {
        const std::string_view trimmed = TrimLuaWhitespace(StripLuaLineComment(line));
        return trimmed.starts_with("return function(") ||
               trimmed.starts_with("return function (");
    };

    bool changed = false;
    for (std::size_t closure_line = 0; closure_line < lines->size(); ++closure_line) {
        if (!is_return_function_header((*lines)[closure_line])) {
            continue;
        }
        const std::string closure_indent(GetRenderedLuaIndent((*lines)[closure_line]));

        std::vector<std::string> carried_locals;
        for (std::size_t probe = closure_line; probe > 0;) {
            --probe;
            const std::string_view trimmed =
                TrimLuaWhitespace(StripLuaLineComment((*lines)[probe]));
            if (trimmed.empty()) {
                continue;
            }
            if (GetRenderedLuaIndent((*lines)[probe]) != closure_indent) {
                break;
            }
            std::string local_name;
            if (!is_plain_local_declaration((*lines)[probe], &local_name)) {
                break;
            }
            carried_locals.push_back(std::move(local_name));
        }
        if (carried_locals.empty()) {
            continue;
        }

        const std::size_t closure_end =
            FindRenderedFunctionBlockEnd(*lines, closure_line);
        if (closure_end >= lines->size() || closure_end <= closure_line + 1) {
            continue;
        }

        for (std::size_t body = closure_line + 1; body < closure_end; ++body) {
            bool assign_is_local = false;
            std::string lhs;
            std::string rhs;
            if (!ParseRenderedSingleAssignmentLine((*lines)[body],
                                                   &assign_is_local,
                                                   &lhs,
                                                   &rhs) ||
                !assign_is_local ||
                std::find(carried_locals.begin(), carried_locals.end(), lhs) ==
                    carried_locals.end()) {
                continue;
            }

            std::size_t previous = body;
            while (previous > closure_line + 1) {
                --previous;
                if (!TrimLuaWhitespace(StripLuaLineComment((*lines)[previous])).empty()) {
                    break;
                }
            }
            const std::string_view previous_trimmed =
                previous < lines->size()
                    ? TrimLuaWhitespace(StripLuaLineComment((*lines)[previous]))
                    : std::string_view{};
            const bool guarded_by_outer_value =
                previous_trimmed == ("if not " + lhs + " then") ||
                (previous_trimmed.starts_with("if not " + lhs + " or ") &&
                 previous_trimmed.ends_with(" then"));
            if (!guarded_by_outer_value) {
                continue;
            }

            const std::string indent(GetRenderedLuaIndent((*lines)[body]));
            (*lines)[body] = indent + lhs + " = " + rhs;
            changed = true;
        }
    }

    return changed;
}

bool SimplifyRenderedLuaUnusedLocalTableLiteralAssignmentsForParity(
    std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        bool is_local = false;
        std::string local_name;
        std::string rhs_expression;
        if (!ParseRenderedSingleAssignmentLine((*lines)[line_index],
                                               &is_local,
                                               &local_name,
                                               &rhs_expression) ||
            !is_local || !IsValidLuaIdentifier(local_name)) {
            continue;
        }

        const std::string normalized_rhs =
            StripRenderedWholeParentheses(TrimLuaWhitespace(rhs_expression), nullptr);
        if (normalized_rhs.size() < 2 || normalized_rhs.front() != '{' ||
            normalized_rhs.back() != '}' ||
            normalized_rhs.find("function") != std::string::npos ||
            normalized_rhs.find("...") != std::string::npos) {
            continue;
        }

        const std::size_t scope_end =
            FindEnclosingRenderedFunctionBlockEnd(*lines, line_index);
        const std::size_t probe_limit =
            scope_end < lines->size() ? scope_end + 1 : lines->size();
        bool used_later = false;
        for (std::size_t probe = line_index + 1; probe < probe_limit; ++probe) {
            if (IsRenderedShadowingLocalDeclaration((*lines)[probe], local_name) ||
                IsAssignmentToIdentifier((*lines)[probe], local_name)) {
                break;
            }
            if (CountIdentifierUses((*lines)[probe], local_name) != 0) {
                used_later = true;
                break;
            }
        }
        if (used_later) {
            continue;
        }

        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index));
        changed = true;
        if (line_index != 0) {
            --line_index;
        }
    }

    return changed;
}

bool SimplifyRenderedLuaImmediateIteratorArgumentCarryLocals(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto find_block_end = [](const std::vector<std::string>& local_lines,
                                   std::size_t start_line) -> std::size_t {
        if (start_line >= local_lines.size()) {
            return local_lines.size();
        }

        int balance = 0;
        for (std::size_t probe = start_line; probe < local_lines.size(); ++probe) {
            balance += GetRenderedLuaBlockDelta(
                TrimLuaWhitespace(StripLuaLineComment(local_lines[probe])));
            if (probe > start_line && balance == 0) {
                return probe;
            }
        }
        return local_lines.size();
    };

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 1 < lines->size(); ++line_index) {
        bool seed_is_local = false;
        std::string local_name;
        std::string seed_expression;
        if (!ParseRenderedSingleAssignmentLine((*lines)[line_index],
                                               &seed_is_local,
                                               &local_name,
                                               &seed_expression) ||
            !seed_is_local || !IsValidLuaIdentifier(local_name)) {
            continue;
        }

        const std::string normalized_seed =
            StripRenderedWholeParentheses(TrimLuaWhitespace(seed_expression), nullptr);
        if (normalized_seed.empty() ||
            !IsRenderedSafePrefixExpression(normalized_seed) ||
            CountIdentifierUses(normalized_seed, local_name) != 0) {
            continue;
        }

        const std::string_view trimmed_header =
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index + 1]));
        if (!trimmed_header.starts_with("for ") || !trimmed_header.ends_with(" do")) {
            continue;
        }

        std::size_t found = std::string::npos;
        std::size_t matched_length = 0;
        std::string replacement_expression;
        for (const std::string& iterator_name :
             {std::string("ipairs"), std::string("pairs"), std::string("next")}) {
            const std::string needle = iterator_name + "(" + local_name + ")";
            found = (*lines)[line_index + 1].find(needle);
            if (found != std::string::npos) {
                matched_length = needle.size();
                replacement_expression = iterator_name + "(" + normalized_seed + ")";
                break;
            }
        }
        if (found == std::string::npos) {
            continue;
        }

        const std::size_t loop_end = find_block_end(*lines, line_index + 1);
        if (loop_end >= lines->size()) {
            continue;
        }

        bool used_inside_loop = false;
        for (std::size_t probe = line_index + 2; probe < loop_end; ++probe) {
            if (CountIdentifierUses((*lines)[probe], local_name) != 0) {
                used_inside_loop = true;
                break;
            }
        }
        if (used_inside_loop) {
            continue;
        }

        (*lines)[line_index + 1].replace(found, matched_length, replacement_expression);
        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index));
        changed = true;
        if (line_index != 0) {
            --line_index;
        }
    }

    return changed;
}

bool SimplifyRenderedLuaImmediateBooleanAliasCarries(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 1 < lines->size(); ++line_index) {
        bool first_is_local = false;
        std::string first_lhs;
        std::string first_rhs;
        bool second_is_local = false;
        std::string second_lhs;
        std::string second_rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[line_index],
                                               &first_is_local,
                                               &first_lhs,
                                               &first_rhs) ||
            !ParseRenderedSingleAssignmentLine((*lines)[line_index + 1],
                                               &second_is_local,
                                               &second_lhs,
                                               &second_rhs) ||
            !first_is_local || first_lhs == second_lhs) {
            continue;
        }

        const std::string normalized_literal =
            StripRenderedWholeParentheses(TrimLuaWhitespace(first_rhs), nullptr);
        if (normalized_literal != "true" && normalized_literal != "false") {
            continue;
        }

        const std::string normalized_alias =
            StripRenderedWholeParentheses(TrimLuaWhitespace(second_rhs), nullptr);
        if (normalized_alias != first_lhs) {
            continue;
        }

        const std::string indent(GetRenderedLuaIndent((*lines)[line_index + 1]));
        (*lines)[line_index + 1] = indent + (second_is_local ? "local " : "") + second_lhs +
                                   " = " + normalized_literal;
        changed = true;
    }

    return changed;
}

bool SimplifyRenderedLuaNearbyBooleanAliasLocals(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    std::vector<std::vector<std::size_t>> scope_paths;
    ComputeRenderedScopePaths(*lines, &scope_paths);

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
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

        const std::string normalized_literal =
            StripRenderedWholeParentheses(TrimLuaWhitespace(seed_rhs), nullptr);
        if (normalized_literal != "true" && normalized_literal != "false") {
            continue;
        }

        bool line_changed = false;
        bool alias_still_used = false;
        for (std::size_t probe = line_index + 1; probe < lines->size(); ++probe) {
            if (probe >= scope_paths.size() || line_index >= scope_paths.size() ||
                scope_paths[probe] != scope_paths[line_index]) {
                break;
            }

            std::vector<std::string> lhs_names;
            std::string probe_rhs;
            bool probe_is_local_decl = false;
            if (ParseRenderedAssignmentLine((*lines)[probe],
                                           &probe_is_local_decl,
                                           &lhs_names,
                                           &probe_rhs) &&
                std::find(lhs_names.begin(), lhs_names.end(), seed_lhs) != lhs_names.end()) {
                break;
            }

            std::vector<std::string> local_names;
            if (ParseRenderedLocalDeclarationLine((*lines)[probe], &local_names) &&
                std::find(local_names.begin(), local_names.end(), seed_lhs) !=
                    local_names.end()) {
                break;
            }

            bool probe_is_local = false;
            std::string probe_lhs;
            std::string probe_single_rhs;
            if (ParseRenderedSingleAssignmentLine((*lines)[probe],
                                                 &probe_is_local,
                                                 &probe_lhs,
                                                 &probe_single_rhs) &&
                StripRenderedWholeParentheses(
                    TrimLuaWhitespace(probe_single_rhs), nullptr) == seed_lhs) {
                const std::string indent(GetRenderedLuaIndent((*lines)[probe]));
                (*lines)[probe] = indent + (probe_is_local ? "local " : "") + probe_lhs +
                                  " = " + normalized_literal;
                line_changed = true;
                continue;
            }

            if (CountIdentifierUses((*lines)[probe], seed_lhs) != 0) {
                alias_still_used = true;
            }
        }

        if (!line_changed) {
            continue;
        }

        if (!alias_still_used) {
            lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index));
            ComputeRenderedScopePaths(*lines, &scope_paths);
            changed = true;
            if (line_index != 0) {
                --line_index;
            }
            continue;
        }

        changed = true;
    }

    return changed;
}

bool SimplifyRenderedLuaEscapingBooleanAliasUses(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    std::vector<std::vector<std::size_t>> scope_paths;
    ComputeRenderedScopePaths(*lines, &scope_paths);

    const auto scope_path_is_prefix = [](const std::vector<std::size_t>& prefix,
                                         const std::vector<std::size_t>& candidate) {
        return prefix.size() <= candidate.size() &&
               std::equal(prefix.begin(), prefix.end(), candidate.begin());
    };

    bool changed = false;
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

        const std::string normalized_literal =
            StripRenderedWholeParentheses(TrimLuaWhitespace(seed_rhs), nullptr);
        if (normalized_literal != "true" && normalized_literal != "false") {
            continue;
        }

        const std::vector<std::size_t>& declaration_scope = scope_paths[line_index];
        for (std::size_t probe = line_index + 1; probe < lines->size(); ++probe) {
            if (IsRenderedFunctionStartLine((*lines)[probe])) {
                const std::size_t nested_end = FindRenderedFunctionBlockEnd(*lines, probe);
                if (nested_end >= lines->size() || nested_end <= probe) {
                    break;
                }
                probe = nested_end;
                continue;
            }
            if (probe >= scope_paths.size()) {
                continue;
            }

            std::vector<std::string> lhs_names;
            std::string probe_rhs;
            bool probe_is_local_decl = false;
            if (ParseRenderedAssignmentLine((*lines)[probe],
                                           &probe_is_local_decl,
                                           &lhs_names,
                                           &probe_rhs) &&
                std::find(lhs_names.begin(), lhs_names.end(), seed_lhs) != lhs_names.end()) {
                break;
            }

            std::vector<std::string> local_names;
            if (ParseRenderedLocalDeclarationLine((*lines)[probe], &local_names) &&
                std::find(local_names.begin(), local_names.end(), seed_lhs) !=
                    local_names.end()) {
                break;
            }

            if (CountIdentifierUses((*lines)[probe], seed_lhs) == 0 ||
                scope_path_is_prefix(declaration_scope, scope_paths[probe])) {
                continue;
            }

            std::string rewritten_line;
            if (!ReplaceRenderedIdentifierUses((*lines)[probe],
                                               seed_lhs,
                                               normalized_literal,
                                               &rewritten_line) ||
                rewritten_line == (*lines)[probe]) {
                continue;
            }

            (*lines)[probe] = std::move(rewritten_line);
            changed = true;
        }
    }

    return changed;
}

bool SimplifyRenderedLuaTemporaryIdentifierCarries(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 1 < lines->size(); ++line_index) {
        bool first_is_local = false;
        std::string first_lhs;
        std::string first_rhs;
        bool second_is_local = false;
        std::string second_lhs;
        std::string second_rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[line_index],
                                               &first_is_local,
                                               &first_lhs,
                                               &first_rhs) ||
            !ParseRenderedSingleAssignmentLine((*lines)[line_index + 1],
                                               &second_is_local,
                                               &second_lhs,
                                               &second_rhs) ||
            first_lhs != second_lhs ||
            !IsRenderTemporaryName(first_lhs)) {
            continue;
        }

        const std::string indent(GetRenderedLuaIndent((*lines)[line_index]));
        if (indent != GetRenderedLuaIndent((*lines)[line_index + 1])) {
            continue;
        }

        const std::string normalized_seed =
            StripRenderedWholeParentheses(TrimLuaWhitespace(first_rhs), nullptr);
        if (normalized_seed.empty() ||
            CountIdentifierUses(normalized_seed, first_lhs) != 0) {
            continue;
        }

        const std::string normalized_carry =
            StripRenderedWholeParentheses(TrimLuaWhitespace(second_rhs), nullptr);
        if (normalized_carry.empty() ||
            CountIdentifierUses(normalized_carry, first_lhs) != 1) {
            continue;
        }

        std::string rewritten_rhs;
        if (!ReplaceRenderedIdentifierUses(normalized_carry,
                                           first_lhs,
                                           WrapInlineExpression(normalized_seed),
                                           &rewritten_rhs)) {
            continue;
        }

        (*lines)[line_index + 1] =
            indent + (second_is_local ? "local " : "") + second_lhs + " = " + rewritten_rhs;
        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index));
        changed = true;
        if (line_index != 0) {
            --line_index;
        }
    }

    return changed;
}

bool SimplifyRenderedLuaUnusedDuplicatedTemps(std::vector<std::string>* lines);
bool SimplifyRenderedLuaDanglingTempPlaceholderCarries(std::vector<std::string>* lines);
bool SimplifyRenderedLuaAdjacentDuplicateAssignmentSeeds(std::vector<std::string>* lines);

bool SimplifyRenderedLuaStraightLineSingleUseTemps(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto is_eligible_temporary_rhs = [](std::string_view expression,
                                              std::string_view temporary_name) {
        const std::string_view trimmed = TrimLuaWhitespace(expression);
        int paren_balance = 0;
        int brace_balance = 0;
        return !trimmed.empty() &&
               trimmed.front() != '{' &&
               expression.find('\n') == std::string_view::npos &&
               expression.find("function(") == std::string_view::npos &&
               CountIdentifierUses(expression, temporary_name) == 0 &&
               !HasPositiveLuaExpressionBalance(expression, &paren_balance, &brace_balance);
    };
    const auto extract_guard_condition = [](std::string_view line,
                                            std::string* condition_out) {
        if (condition_out == nullptr) {
            return false;
        }

        const std::string_view trimmed = TrimLuaWhitespace(StripLuaLineComment(line));
        if (StartsWithLuaKeyword(trimmed, "if") && trimmed.ends_with(" then")) {
            *condition_out = std::string(
                TrimLuaWhitespace(trimmed.substr(3, trimmed.size() - 3 - 5)));
            return !condition_out->empty();
        }
        if (StartsWithLuaKeyword(trimmed, "while") && trimmed.ends_with(" do")) {
            *condition_out = std::string(
                TrimLuaWhitespace(trimmed.substr(6, trimmed.size() - 6 - 3)));
            return !condition_out->empty();
        }
        if (!trimmed.starts_with("elseif ") || !trimmed.ends_with(" then")) {
            return false;
        }

        *condition_out = std::string(TrimLuaWhitespace(trimmed.substr(7, trimmed.size() - 12)));
        return !condition_out->empty();
    };
    const auto condition_contains_degenerate_self_compare = [](std::string_view condition) {
        const auto is_self_compare = [](std::string_view comparison_text) {
            const std::string normalized =
                StripRenderedWholeParentheses(TrimLuaWhitespace(comparison_text), nullptr);
            std::size_t compare_position = std::string::npos;
            std::string_view compare_op;
            if (!FindRenderedTopLevelComparison(normalized, &compare_position, &compare_op) ||
                (compare_op != "==" && compare_op != "~=")) {
                return false;
            }

            const std::string lhs = NormalizeRenderedConditionOperand(
                normalized.substr(0, compare_position), nullptr);
            const std::string rhs = NormalizeRenderedConditionOperand(
                normalized.substr(compare_position + compare_op.size()), nullptr);
            return !lhs.empty() && lhs == rhs;
        };

        std::vector<std::string> or_parts;
        if (SplitRenderedTopLevelBooleanChain(condition, "or", &or_parts)) {
            for (const std::string& or_part : or_parts) {
                std::vector<std::string> and_parts;
                if (SplitRenderedTopLevelBooleanChain(or_part, "and", &and_parts)) {
                    for (const std::string& and_part : and_parts) {
                        if (is_self_compare(and_part)) {
                            return true;
                        }
                    }
                } else if (is_self_compare(or_part)) {
                    return true;
                }
            }
            return false;
        }

        std::vector<std::string> and_parts;
        if (SplitRenderedTopLevelBooleanChain(condition, "and", &and_parts)) {
            for (const std::string& and_part : and_parts) {
                if (is_self_compare(and_part)) {
                    return true;
                }
            }
            return false;
        }

        return is_self_compare(condition);
    };

    bool changed = false;
    RunRenderedLuaCleanupPassLoop("SimplifyRenderedLuaStraightLineSingleUseTemps", 64, [&]() {
        bool pass_changed = false;
        for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
            bool temp_is_local = false;
            std::string temporary_name;
            std::string temporary_expression;
            if (!ParseRenderedSingleAssignmentLine((*lines)[line_index],
                                                   &temp_is_local,
                                                   &temporary_name,
                                                   &temporary_expression) ||
                !IsRenderTemporaryName(temporary_name) ||
                !is_eligible_temporary_rhs(temporary_expression, temporary_name)) {
                continue;
            }

            const std::size_t scope_end =
                FindEnclosingRenderedFunctionBlockEnd(*lines, line_index);
            const std::size_t probe_limit =
                scope_end < lines->size() ? scope_end + 1 : lines->size();

            std::size_t use_line = lines->size();
            std::size_t use_found = std::string::npos;
            bool blocked = false;
            for (std::size_t probe = line_index + 1; probe < probe_limit; ++probe) {
                const std::string_view trimmed =
                    TrimLuaWhitespace(StripLuaLineComment((*lines)[probe]));
                if (trimmed.empty()) {
                    continue;
                }
                if (trimmed.find("function(") != std::string_view::npos ||
                    IsRenderedShadowingLocalDeclaration((*lines)[probe], temporary_name)) {
                    blocked = true;
                    break;
                }

                std::string assigned_name;
                if (IsTemporaryAssignmentLine((*lines)[probe], &assigned_name, nullptr)) {
                    if (assigned_name == temporary_name) {
                        blocked = true;
                        break;
                    }

                    const std::size_t line_uses =
                        CountIdentifierUses((*lines)[probe], temporary_name);
                    if (line_uses == 0) {
                        continue;
                    }
                    if (line_uses != 1) {
                        blocked = true;
                        break;
                    }

                    use_line = probe;
                    use_found = FindIdentifierUse((*lines)[probe], temporary_name);
                    break;
                }

                const std::size_t line_uses =
                    CountIdentifierUses((*lines)[probe], temporary_name);
                if (line_uses == 0) {
                    blocked = true;
                    break;
                }
                if (line_uses != 1) {
                    blocked = true;
                    break;
                }

                use_line = probe;
                use_found = FindIdentifierUse((*lines)[probe], temporary_name);
                break;
            }

            if (blocked || use_line >= lines->size() || use_found == std::string::npos) {
                continue;
            }

            const std::string wrapped_expression =
                WrapInlineExpression(temporary_expression);
            const std::size_t first_non_space =
                (*lines)[use_line].find_first_not_of(" \t");
            if (first_non_space != std::string::npos && use_found == first_non_space &&
                !wrapped_expression.empty() && wrapped_expression.front() == '(') {
                const std::string_view suffix = TrimLuaWhitespace(
                    std::string_view((*lines)[use_line]).substr(use_found +
                                                                temporary_name.size()));
                const bool safe_statement_leading_inline =
                    !suffix.empty() &&
                    (suffix.front() == '.' || suffix.front() == '[' ||
                     suffix.front() == ':' || suffix.front() == '(');
                if (!safe_statement_leading_inline) {
                    continue;
                }
            }

            std::string replacement_line = (*lines)[use_line];
            replacement_line.replace(use_found,
                                     temporary_name.size(),
                                     wrapped_expression);
            (*lines)[use_line] = std::move(replacement_line);
            lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index));
            changed = true;
            pass_changed = true;
            break;
        }

        return pass_changed;
    });

    changed |= SimplifyRenderedLuaUnusedDuplicatedTemps(lines);
    changed |= SimplifyRenderedLuaDanglingTempPlaceholderCarries(lines);
    changed |= SimplifyRenderedLuaAdjacentDuplicateAssignmentSeeds(lines);
    return changed;
}

bool SimplifyRenderedLuaUnusedDuplicatedTemps(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto find_top_level_assignment_equals = [](std::string_view text) {
        int paren_depth = 0;
        int brace_depth = 0;
        int bracket_depth = 0;
        bool in_single_quote = false;
        bool in_double_quote = false;
        bool escaped = false;
        for (std::size_t index = 0; index < text.size(); ++index) {
            const char ch = text[index];
            if ((in_single_quote || in_double_quote) && escaped) {
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

            if (ch == '=' && paren_depth == 0 && brace_depth == 0 && bracket_depth == 0) {
                const bool previous_is_compare =
                    index > 0 &&
                    (text[index - 1] == '=' || text[index - 1] == '<' ||
                     text[index - 1] == '>' || text[index - 1] == '~');
                const bool next_is_compare =
                    index + 1 < text.size() && text[index + 1] == '=';
                if (!previous_is_compare && !next_is_compare) {
                    return index;
                }
            }
        }
        return std::string_view::npos;
    };
    const auto split_top_level_comma_chain = [](std::string_view text,
                                                std::vector<std::string>* parts_out) {
        if (parts_out == nullptr) {
            return false;
        }

        parts_out->clear();
        int paren_depth = 0;
        int brace_depth = 0;
        int bracket_depth = 0;
        bool in_single_quote = false;
        bool in_double_quote = false;
        bool escaped = false;
        std::size_t segment_begin = 0;
        for (std::size_t index = 0; index < text.size(); ++index) {
            const char ch = text[index];
            if ((in_single_quote || in_double_quote) && escaped) {
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

            if (ch == ',' && paren_depth == 0 && brace_depth == 0 && bracket_depth == 0) {
                parts_out->emplace_back(
                    std::string(TrimLuaWhitespace(text.substr(segment_begin, index - segment_begin))));
                segment_begin = index + 1;
            }
        }

        parts_out->emplace_back(std::string(TrimLuaWhitespace(text.substr(segment_begin))));
        return true;
    };

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        bool temp_is_local = false;
        std::string temporary_name;
        std::string temporary_expression;
        if (!ParseRenderedSingleAssignmentLine((*lines)[line_index],
                                               &temp_is_local,
                                               &temporary_name,
                                               &temporary_expression) ||
            !temp_is_local || !IsRenderTemporaryName(temporary_name)) {
            continue;
        }

        const std::string normalized_expression =
            StripRenderedWholeParentheses(TrimLuaWhitespace(temporary_expression), nullptr);
        if (normalized_expression.empty() ||
            temporary_expression.find('\n') != std::string::npos ||
            temporary_expression.find("function(") != std::string::npos) {
            continue;
        }

        std::string_view call_callee;
        std::string_view call_arguments;
        const bool expression_is_trailing_call =
            TryParseRenderedTrailingCall(normalized_expression, &call_callee, &call_arguments);

        const std::size_t scope_end =
            FindEnclosingRenderedFunctionBlockEnd(*lines, line_index);
        const std::size_t probe_limit =
            scope_end < lines->size() ? scope_end + 1 : lines->size();

        const std::size_t usage_probe_limit =
            std::min(probe_limit, line_index + static_cast<std::size_t>(16));
        bool used_later = false;
        for (std::size_t probe = line_index + 1; probe < usage_probe_limit; ++probe) {
            if (CountIdentifierUses((*lines)[probe], temporary_name) != 0 ||
                IsRenderedShadowingLocalDeclaration((*lines)[probe], temporary_name) ||
                IsAssignmentToIdentifier((*lines)[probe], temporary_name)) {
                used_later = true;
                break;
            }
        }
        if (used_later) {
            continue;
        }

        std::size_t next_nonempty = line_index + 1;
        while (next_nonempty < probe_limit &&
               TrimLuaWhitespace(StripLuaLineComment((*lines)[next_nonempty])).empty()) {
            ++next_nonempty;
        }
        if (next_nonempty >= probe_limit) {
            continue;
        }

        const std::string_view next_code =
            TrimLuaWhitespace(StripLuaLineComment((*lines)[next_nonempty]));
        bool repeated_in_table_literal_assignment = false;
        bool next_assignment_is_table_literal = false;
        bool next_is_local = false;
        std::string next_lhs;
        std::string next_rhs;
        if (ParseRenderedSingleAssignmentLine((*lines)[next_nonempty],
                                              &next_is_local,
                                              &next_lhs,
                                              &next_rhs)) {
            const std::string normalized_next_rhs =
                StripRenderedWholeParentheses(TrimLuaWhitespace(next_rhs), nullptr);
            if (normalized_next_rhs.size() >= 2 && normalized_next_rhs.front() == '{' &&
                normalized_next_rhs.back() == '}') {
                next_assignment_is_table_literal = true;
                const std::string_view table_body = TrimLuaWhitespace(
                    std::string_view(normalized_next_rhs).substr(1, normalized_next_rhs.size() - 2));
                std::vector<std::string> entries;
                if (split_top_level_comma_chain(table_body, &entries)) {
                    for (const std::string& entry : entries) {
                        const std::string normalized_entry =
                            StripRenderedWholeParentheses(TrimLuaWhitespace(entry), nullptr);
                        const std::size_t equals =
                            find_top_level_assignment_equals(normalized_entry);
                        if (equals == std::string::npos || equals + 1 >= normalized_entry.size()) {
                            continue;
                        }
                        const std::string entry_value = StripRenderedWholeParentheses(
                            TrimLuaWhitespace(normalized_entry.substr(equals + 1)),
                            nullptr);
                        if (entry_value == normalized_expression) {
                            repeated_in_table_literal_assignment = true;
                            break;
                        }
                    }
                }
            }
        }

        const bool expression_repeated =
            next_code.find(normalized_expression) != std::string_view::npos ||
            repeated_in_table_literal_assignment;
        if (!expression_repeated) {
            continue;
        }
        if (expression_is_trailing_call &&
            !(next_assignment_is_table_literal && repeated_in_table_literal_assignment)) {
            continue;
        }

        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index));
        changed = true;
        if (line_index != 0) {
            --line_index;
        }
    }

    return changed;
}

bool SimplifyRenderedLuaDanglingTempPlaceholderCarries(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        bool temporary_is_local = false;
        std::string temporary_name;
        std::string temporary_expression;
        if (!ParseRenderedSingleAssignmentLine((*lines)[line_index],
                                               &temporary_is_local,
                                               &temporary_name,
                                               &temporary_expression) ||
            !temporary_is_local || !IsRenderTemporaryName(temporary_name) ||
            temporary_name == "temp" ||
            CountIdentifierUses(temporary_expression, temporary_name) != 0) {
            continue;
        }

        const std::size_t scope_end =
            FindEnclosingRenderedFunctionBlockEnd(*lines, line_index);
        const std::size_t function_begin =
            FindEnclosingRenderedFunctionBlockBegin(*lines, line_index);
        if (function_begin == lines->size()) {
            continue;
        }
        const std::size_t probe_limit =
            scope_end < lines->size() ? scope_end + 1 : lines->size();

        std::size_t next_nonempty = line_index + 1;
        while (next_nonempty < probe_limit &&
               TrimLuaWhitespace(StripLuaLineComment((*lines)[next_nonempty])).empty()) {
            ++next_nonempty;
        }
        if (next_nonempty >= probe_limit ||
            CountIdentifierUses((*lines)[next_nonempty], temporary_name) != 0 ||
            CountIdentifierUses((*lines)[next_nonempty], "temp") != 1) {
            continue;
        }

        bool temporary_used_later = false;
        for (std::size_t probe = line_index + 1; probe < probe_limit; ++probe) {
            if (CountIdentifierUses((*lines)[probe], temporary_name) != 0) {
                temporary_used_later = true;
                break;
            }
        }
        if (temporary_used_later) {
            continue;
        }

        if (IsRenderedShadowingLocalDeclaration((*lines)[next_nonempty], "temp")) {
            continue;
        }
        bool next_is_local = false;
        std::string next_lhs;
        std::string next_rhs;
        if (ParseRenderedSingleAssignmentLine((*lines)[next_nonempty],
                                              &next_is_local,
                                              &next_lhs,
                                              &next_rhs) &&
            next_lhs == "temp") {
            continue;
        }

        bool has_temp_collision = false;
        for (std::size_t probe = function_begin; probe < probe_limit; ++probe) {
            if (probe == line_index || probe == next_nonempty) {
                continue;
            }
            std::vector<std::string> local_names;
            if (ParseRenderedLocalDeclarationLine((*lines)[probe], &local_names) &&
                std::find(local_names.begin(), local_names.end(), "temp") != local_names.end()) {
                has_temp_collision = true;
                break;
            }
            bool probe_is_local = false;
            std::string probe_lhs;
            std::string probe_rhs;
            if (ParseRenderedSingleAssignmentLine((*lines)[probe],
                                                  &probe_is_local,
                                                  &probe_lhs,
                                                  &probe_rhs) &&
                probe_lhs == "temp") {
                has_temp_collision = true;
                break;
            }
        }
        if (has_temp_collision) {
            std::string rewritten_line;
            if (!ReplaceRenderedIdentifierUses((*lines)[next_nonempty],
                                               "temp",
                                               temporary_name,
                                               &rewritten_line)) {
                continue;
            }

            (*lines)[next_nonempty] = std::move(rewritten_line);
            changed = true;
            continue;
        }

        const std::string indent(GetRenderedLuaIndent((*lines)[line_index]));
        (*lines)[line_index] = indent + "local temp = " + temporary_expression;
        changed = true;
    }

    return changed;
}

bool SimplifyRenderedLuaAdjacentDuplicateAssignmentSeeds(std::vector<std::string>* lines) {
    if (lines == nullptr || lines->size() < 2) {
        return false;
    }

    bool changed = false;
    for (std::size_t line_index = 0; line_index + 1 < lines->size(); ++line_index) {
        bool first_is_local = false;
        std::string first_lhs;
        std::string first_rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[line_index],
                                               &first_is_local,
                                               &first_lhs,
                                               &first_rhs) ||
            !first_is_local || !IsValidLuaIdentifier(first_lhs) ||
            CountIdentifierUses(first_rhs, first_lhs) != 0) {
            continue;
        }

        bool second_is_local = false;
        std::string second_lhs;
        std::string second_rhs;
        if (!ParseRenderedSingleAssignmentLine((*lines)[line_index + 1],
                                               &second_is_local,
                                               &second_lhs,
                                               &second_rhs) ||
            second_is_local || second_lhs != first_lhs ||
            CountIdentifierUses(second_rhs, second_lhs) != 0) {
            continue;
        }

        const std::string normalized_first_rhs =
            StripRenderedWholeParentheses(TrimLuaWhitespace(first_rhs), nullptr);
        const std::string normalized_second_rhs =
            StripRenderedWholeParentheses(TrimLuaWhitespace(second_rhs), nullptr);
        if (normalized_first_rhs.empty() || normalized_first_rhs != normalized_second_rhs) {
            continue;
        }

        if (GetRenderedLuaIndent((*lines)[line_index]) !=
            GetRenderedLuaIndent((*lines)[line_index + 1])) {
            continue;
        }

        lines->erase(lines->begin() + static_cast<std::ptrdiff_t>(line_index + 1));
        changed = true;
    }

    return changed;
}

}  // namespace refii::lua
