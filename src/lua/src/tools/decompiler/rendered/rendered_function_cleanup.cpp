#include "tools/decompiler/rendered/rendered_lua_text.h"

#include "tools/decompiler/rendered/rendered_cleanup_common_helpers.h"

#include <algorithm>
#include <cctype>
#include <cstddef>
#include <sstream>
#include <string>
#include <string_view>
#include <unordered_map>
#include <unordered_set>
#include <utility>
#include <vector>

namespace refii::lua {

bool SimplifyRenderedLuaAssignedFunctionDeclarations(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto parse_function_header = [](std::string_view rhs,
                                          std::string* params_out) -> bool {
        if (params_out == nullptr) {
            return false;
        }

        rhs = TrimLuaWhitespace(rhs);
        if (!(rhs.starts_with("function(") || rhs.starts_with("function ("))) {
            return false;
        }

        const std::size_t open = rhs.find('(');
        if (open == std::string_view::npos) {
            return false;
        }

        std::size_t close = std::string_view::npos;
        if (!FindRenderedMatchingParen(rhs, open, &close) || close + 1 != rhs.size()) {
            return false;
        }

        *params_out = std::string(TrimLuaWhitespace(rhs.substr(open + 1, close - open - 1)));
        return true;
    };

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        bool assign_is_local = false;
        std::string lhs_expression;
        std::string rhs_expression;
        if (!ParseRenderedSingleAssignmentLine((*lines)[line_index],
                                               &assign_is_local,
                                               &lhs_expression,
                                               &rhs_expression) ||
            assign_is_local) {
            continue;
        }

        const std::string normalized_lhs =
            StripRenderedWholeParentheses(TrimLuaWhitespace(lhs_expression), nullptr);
        if (!IsRenderedSimpleMemberPath(normalized_lhs)) {
            continue;
        }

        std::string params;
        if (!parse_function_header(rhs_expression, &params)) {
            continue;
        }

        const std::size_t member_separator = normalized_lhs.rfind('.');
        if (member_separator == std::string::npos || member_separator == 0 ||
            member_separator + 1 >= normalized_lhs.size()) {
            continue;
        }

        const std::string function_base = normalized_lhs.substr(0, member_separator);
        const std::string function_name = normalized_lhs.substr(member_separator + 1);
        if (!IsRenderedSafePrefixExpression(function_base) ||
            !IsValidLuaIdentifier(function_name)) {
            continue;
        }

        std::vector<std::string> param_parts;
        if (!params.empty() && !SplitRenderedTopLevelCommaChain(params, &param_parts)) {
            continue;
        }

        bool use_method_syntax = false;
        if (!param_parts.empty()) {
            const std::string first_param =
                StripRenderedWholeParentheses(TrimLuaWhitespace(param_parts.front()), nullptr);
            use_method_syntax = first_param == "self";
        }

        std::string rendered_params;
        if (use_method_syntax) {
            for (std::size_t index = 1; index < param_parts.size(); ++index) {
                if (!rendered_params.empty()) {
                    rendered_params += ", ";
                }
                rendered_params += std::string(TrimLuaWhitespace(param_parts[index]));
            }
        } else {
            rendered_params = params;
        }

        const std::string indent(GetRenderedLuaIndent((*lines)[line_index]));
        const std::string rewritten =
            indent + "function " +
            (use_method_syntax ? function_base + ":" + function_name : normalized_lhs) +
            "(" + rendered_params + ")";
        if (rewritten == (*lines)[line_index]) {
            continue;
        }

        (*lines)[line_index] = rewritten;
        changed = true;
    }

    return changed;
}

bool SimplifyRenderedLuaMethodArg0SelfParameters(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto parse_function_header = [](std::string_view line,
                                          std::string* base_out,
                                          std::string* method_out,
                                          std::vector<std::string>* params_out) -> bool {
        if (base_out == nullptr || method_out == nullptr || params_out == nullptr) {
            return false;
        }

        const std::string_view stripped = StripLuaLineComment(line);
        const std::string_view trimmed = TrimLuaWhitespace(stripped);
        if (!trimmed.starts_with("function ")) {
            return false;
        }
        const std::string_view rest = TrimLuaWhitespace(trimmed.substr(9));
        const std::size_t open_paren = rest.find('(');
        if (open_paren == std::string_view::npos) {
            return false;
        }
        std::size_t close_paren = std::string_view::npos;
        if (!FindRenderedMatchingParen(rest, open_paren, &close_paren)) {
            return false;
        }

        const std::string_view full_name = TrimLuaWhitespace(rest.substr(0, open_paren));
        if (full_name.empty() || full_name.find(':') != std::string_view::npos) {
            return false;
        }
        const std::size_t dot = full_name.rfind('.');
        if (dot == std::string_view::npos || dot == 0 || dot + 1 >= full_name.size()) {
            return false;
        }
        const std::string_view base = TrimLuaWhitespace(full_name.substr(0, dot));
        const std::string_view method = TrimLuaWhitespace(full_name.substr(dot + 1));
        if (!IsRenderedSafePrefixExpression(base) || !IsValidLuaIdentifier(method)) {
            return false;
        }

        const std::string_view params_text =
            TrimLuaWhitespace(rest.substr(open_paren + 1, close_paren - open_paren - 1));
        std::vector<std::string> params;
        if (!params_text.empty()) {
            if (!SplitRenderedTopLevelCommaChain(params_text, &params)) {
                return false;
            }
            for (std::string& part : params) {
                part = std::string(TrimLuaWhitespace(part));
            }
        }
        if (params.empty() || params.front() != "arg0") {
            return false;
        }

        *base_out = std::string(base);
        *method_out = std::string(method);
        *params_out = std::move(params);
        return true;
    };

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        std::string function_base;
        std::string function_method;
        std::vector<std::string> parameters;
        if (!parse_function_header((*lines)[line_index],
                                   &function_base,
                                   &function_method,
                                   &parameters)) {
            continue;
        }

        const std::size_t function_end = FindRenderedFunctionBlockEnd(*lines, line_index);
        if (function_end >= lines->size() || function_end <= line_index) {
            continue;
        }

        bool uses_arg0_in_body = false;
        for (std::size_t probe = line_index + 1; probe < function_end; ++probe) {
            if (CountIdentifierUses(StripLuaLineComment((*lines)[probe]), "arg0") != 0) {
                uses_arg0_in_body = true;
                break;
            }
        }
        if (uses_arg0_in_body) {
            continue;
        }

        std::string rendered_params;
        for (std::size_t index = 1; index < parameters.size(); ++index) {
            if (!rendered_params.empty()) {
                rendered_params += ", ";
            }
            rendered_params += parameters[index];
        }

        const std::string_view stripped = StripLuaLineComment((*lines)[line_index]);
        const std::string_view comment =
            stripped.size() < (*lines)[line_index].size()
                ? std::string_view((*lines)[line_index]).substr(stripped.size())
                : std::string_view();
        const std::size_t indent_end = (*lines)[line_index].find_first_not_of(" \t");
        const std::string indent =
            indent_end == std::string::npos
                ? std::string()
                : (*lines)[line_index].substr(0, indent_end);
        const std::string rewritten =
            indent + "function " + function_base + ":" + function_method +
            "(" + rendered_params + ")" + std::string(comment);
        if (rewritten == (*lines)[line_index]) {
            continue;
        }

        (*lines)[line_index] = rewritten;
        changed = true;
    }

    return changed;
}

bool SimplifyRenderedLuaAdjacentDuplicateLocalCallDeclarations(std::vector<std::string>* lines) {
    if (lines == nullptr || lines->size() < 2) {
        return false;
    }

    static constexpr std::size_t kMaxProbeDistance = 6;
    std::vector<std::vector<std::size_t>> scope_paths;
    ComputeRenderedScopePaths(*lines, &scope_paths);

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

        std::string first_callee;
        std::vector<std::string> first_arguments;
        if (!ParseRenderedSingleCallLine(first_rhs, &first_callee, &first_arguments)) {
            continue;
        }

        const std::string normalized_first_rhs =
            StripRenderedWholeParentheses(TrimLuaWhitespace(first_rhs), nullptr);
        if (normalized_first_rhs.empty()) {
            continue;
        }

        std::size_t scope_end = FindEnclosingRenderedFunctionBlockEnd(*lines, line_index);
        if (scope_end >= lines->size()) {
            scope_end = lines->size() - 1;
        }
        const std::size_t probe_limit =
            std::min(lines->size(),
                     std::min(line_index + kMaxProbeDistance + 1, scope_end + 1));

        for (std::size_t probe = line_index + 1; probe < probe_limit; ++probe) {
            if (line_index >= scope_paths.size() || probe >= scope_paths.size() ||
                scope_paths[line_index] != scope_paths[probe]) {
                break;
            }

            bool probe_is_local = false;
            std::string probe_lhs;
            std::string probe_rhs;
            if (ParseRenderedSingleAssignmentLine((*lines)[probe],
                                                  &probe_is_local,
                                                  &probe_lhs,
                                                  &probe_rhs) &&
                probe_is_local &&
                probe_lhs == first_lhs &&
                CountIdentifierUses(probe_rhs, probe_lhs) == 0) {
                std::string probe_callee;
                std::vector<std::string> probe_arguments;
                if (ParseRenderedSingleCallLine(probe_rhs, &probe_callee, &probe_arguments)) {
                    const std::string normalized_probe_rhs =
                        StripRenderedWholeParentheses(TrimLuaWhitespace(probe_rhs), nullptr);
                    if (normalized_probe_rhs == normalized_first_rhs) {
                        const std::string original_line = (*lines)[probe];
                        const std::string_view stripped = StripLuaLineComment(original_line);
                        const std::string_view comment =
                            stripped.size() < original_line.size()
                                ? std::string_view(original_line).substr(stripped.size())
                                : std::string_view();
                        const std::string indent(GetRenderedLuaIndent(original_line));
                        (*lines)[probe] =
                            indent + probe_lhs + " = " +
                            std::string(TrimLuaWhitespace(probe_rhs)) + std::string(comment);
                        changed = true;
                    }
                }
                break;
            }

            if (CountIdentifierUses((*lines)[probe], first_lhs) != 0 ||
                IsRenderedShadowingLocalDeclaration((*lines)[probe], first_lhs) ||
                IsAssignmentToIdentifier((*lines)[probe], first_lhs)) {
                break;
            }
        }
    }

    return changed;
}

bool SimplifyRenderedLuaQuestManagerCameraHelperDefaults(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto parse_function_name = [](std::string_view line, std::string* name_out) -> bool {
        if (name_out == nullptr) {
            return false;
        }

        const std::string_view trimmed = TrimLuaWhitespace(StripLuaLineComment(line));
        if (trimmed.empty()) {
            return false;
        }

        if (trimmed.starts_with("function ")) {
            const std::string_view rest = TrimLuaWhitespace(trimmed.substr(9));
            const std::size_t open = rest.find('(');
            if (open == std::string_view::npos) {
                return false;
            }
            const std::string_view full_name = TrimLuaWhitespace(rest.substr(0, open));
            const std::size_t separator = full_name.find_last_of(".:");
            const std::string_view terminal =
                separator == std::string_view::npos ? full_name : full_name.substr(separator + 1);
            if (!IsValidLuaIdentifier(terminal)) {
                return false;
            }
            *name_out = std::string(terminal);
            return true;
        }

        bool assign_is_local = false;
        std::string lhs_expression;
        std::string rhs_expression;
        if (!ParseRenderedSingleAssignmentLine(trimmed,
                                               &assign_is_local,
                                               &lhs_expression,
                                               &rhs_expression) ||
            assign_is_local) {
            return false;
        }

        rhs_expression = std::string(TrimLuaWhitespace(rhs_expression));
        if (!(std::string_view(rhs_expression).starts_with("function(") ||
              std::string_view(rhs_expression).starts_with("function ("))) {
            return false;
        }

        const std::string normalized_lhs =
            StripRenderedWholeParentheses(TrimLuaWhitespace(lhs_expression), nullptr);
        const std::size_t separator = normalized_lhs.find_last_of('.');
        if (separator == std::string::npos || separator + 1 >= normalized_lhs.size()) {
            return false;
        }

        const std::string_view terminal =
            std::string_view(normalized_lhs).substr(separator + 1);
        if (!IsValidLuaIdentifier(terminal)) {
            return false;
        }
        *name_out = std::string(terminal);
        return true;
    };

    const auto block_has_assignment_to_lhs = [&](std::size_t begin,
                                                 std::size_t end,
                                                 std::string_view lhs) {
        for (std::size_t line_index = begin; line_index <= end && line_index < lines->size();
             ++line_index) {
            bool assign_is_local = false;
            std::string assign_lhs;
            std::string assign_rhs;
            if (!ParseRenderedSingleAssignmentLine((*lines)[line_index],
                                                   &assign_is_local,
                                                   &assign_lhs,
                                                   &assign_rhs)) {
                continue;
            }
            if (TrimLuaWhitespace(assign_lhs) == TrimLuaWhitespace(lhs)) {
                return true;
            }
        }
        return false;
    };

    const auto ensure_assignment_before_first_use =
        [&](std::size_t begin, std::size_t* end_in_out, std::string_view lhs, std::string_view rhs) {
            if (end_in_out == nullptr || lhs.empty() || rhs.empty()) {
                return false;
            }
            if (block_has_assignment_to_lhs(begin, *end_in_out, lhs)) {
                return false;
            }

            for (std::size_t line_index = begin; line_index <= *end_in_out && line_index < lines->size();
                 ++line_index) {
                if ((*lines)[line_index].find(lhs) == std::string::npos) {
                    continue;
                }

                std::size_t insertion_index = line_index;
                std::string_view trimmed =
                    TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index]));
                const bool looks_like_table_field =
                    trimmed.find('=') != std::string_view::npos &&
                    !trimmed.starts_with(std::string(lhs)) &&
                    trimmed.ends_with(',');
                if (looks_like_table_field) {
                    std::size_t current_indent =
                        (*lines)[line_index].find_first_not_of(" \t");
                    if (current_indent == std::string::npos) {
                        current_indent = 0;
                    }
                    while (insertion_index > begin) {
                        const std::size_t previous_indent =
                            (*lines)[insertion_index - 1].find_first_not_of(" \t");
                        if (previous_indent != std::string::npos &&
                            previous_indent < current_indent) {
                            --insertion_index;
                            break;
                        }
                        --insertion_index;
                    }
                }

                const std::size_t indent_end = (*lines)[insertion_index].find_first_not_of(" \t");
                const std::string indent =
                    indent_end == std::string::npos ? std::string()
                                                    : (*lines)[insertion_index].substr(0, indent_end);
                lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(insertion_index),
                              indent + std::string(lhs) + " = (" + std::string(lhs) + " or " +
                                  std::string(rhs) + ")");
                ++(*end_in_out);
                return true;
            }
            return false;
        };

    const auto replace_exact_assert =
        [&](std::size_t begin,
            std::size_t end,
            std::string_view expected_message,
            std::string_view replacement_condition) {
            const std::string expected =
                "assert(params.target_entity, \"" + std::string(expected_message) + "\")";
            for (std::size_t line_index = begin; line_index <= end && line_index < lines->size();
                 ++line_index) {
                const std::string_view trimmed =
                    TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index]));
                if (trimmed != expected) {
                    continue;
                }

                const std::size_t indent_end = (*lines)[line_index].find_first_not_of(" \t");
                const std::string indent =
                    indent_end == std::string::npos ? std::string()
                                                    : (*lines)[line_index].substr(0, indent_end);
                (*lines)[line_index] =
                    indent + "assert(" + std::string(replacement_condition) + ", \"" +
                    std::string(expected_message) + "\")";
                return true;
            }
            return false;
        };

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        std::string function_name;
        if (!parse_function_name((*lines)[line_index], &function_name)) {
            continue;
        }
        if (function_name != "SetLookAtCamera" && function_name != "SetFixedCamera" &&
            function_name != "PointHeroCameraTowards" && function_name != "OverrideHeroCamera") {
            continue;
        }

        std::size_t function_end = FindRenderedFunctionBlockEnd(*lines, line_index);
        if (function_end >= lines->size() || function_end < line_index) {
            continue;
        }

        if (function_name == "SetLookAtCamera") {
            changed |= replace_exact_assert(
                line_index + 1,
                function_end,
                "No target entity or position provided to SetLookAtCamera",
                "params.target_entity or params.target_position or params.target_function");
            changed |= ensure_assignment_before_first_use(
                line_index + 1, &function_end, "params.blend_in_seconds", "2");
            changed |= ensure_assignment_before_first_use(
                line_index + 1, &function_end, "params.pan_angle", "0.25");
        } else if (function_name == "SetFixedCamera") {
            changed |= replace_exact_assert(
                line_index + 1,
                function_end,
                "No target entity provided to SetLookAtCamera",
                "params.target_entity or params.target_position or params.target_function");
            changed |= ensure_assignment_before_first_use(
                line_index + 1, &function_end, "params.turn_hero", "false");
            changed |= ensure_assignment_before_first_use(
                line_index + 1, &function_end, "params.blend_in_seconds", "2");
            changed |= ensure_assignment_before_first_use(
                line_index + 1, &function_end, "params.pan_angle", "0.25");
        } else {
            changed |= ensure_assignment_before_first_use(
                line_index + 1, &function_end, "params.blend_in_seconds", "2");
        }

        line_index = function_end;
    }

    return changed;
}

bool SimplifyRenderedLuaInsertMissingEndsBeforeTopLevelAssignments(
    std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    int open_blocks = 0;
    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        const std::string_view stripped = StripLuaLineComment((*lines)[line_index]);
        const std::string_view trimmed = TrimLuaWhitespace(stripped);
        if (trimmed.empty()) {
            continue;
        }

        if (trimmed == "end" || trimmed.starts_with("end ")) {
            open_blocks = std::max(0, open_blocks - 1);
            continue;
        }
        if (trimmed.starts_with("until ") || trimmed == "until") {
            open_blocks = std::max(0, open_blocks - 1);
            continue;
        }

        bool starts_block = false;
        if (trimmed.starts_with("if ") && trimmed.find(" then") != std::string_view::npos) {
            starts_block = true;
        } else if ((trimmed.starts_with("for ") || trimmed.starts_with("while ")) &&
                   trimmed.ends_with(" do")) {
            starts_block = true;
        } else if (trimmed == "repeat") {
            starts_block = true;
        } else if (trimmed.starts_with("function ") ||
                   trimmed.find("= function") != std::string_view::npos ||
                   trimmed.starts_with("return function")) {
            starts_block = true;
        }
        if (starts_block) {
            ++open_blocks;
        }

        if (open_blocks <= 0) {
            continue;
        }

        const std::size_t equal = trimmed.find('=');
        if (equal == std::string_view::npos) {
            continue;
        }

        const std::string lhs =
            std::string(TrimLuaWhitespace(trimmed.substr(0, equal)));
        if (!IsRenderedSafePrefixExpression(lhs)) {
            continue;
        }

        std::vector<std::string> insert_lines;
        insert_lines.reserve(static_cast<std::size_t>(open_blocks));
        for (int count = 0; count < open_blocks; ++count) {
            insert_lines.emplace_back("end");
        }
        lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                      insert_lines.begin(),
                      insert_lines.end());
        line_index += insert_lines.size();
        open_blocks = 0;
        changed = true;
    }

    return changed;
}

bool SimplifyRenderedLuaFinalFunctionPlaceholderCalls(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        const std::string_view trimmed =
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index]));
        if (!(trimmed.starts_with("function()(") || trimmed.starts_with("function ()("))) {
            continue;
        }

        const std::size_t search_begin = line_index > 32 ? line_index - 32 : 0;
        for (std::size_t probe = line_index; probe-- > search_begin;) {
            const std::string_view probe_trimmed =
                TrimLuaWhitespace(StripLuaLineComment((*lines)[probe]));
            if (!probe_trimmed.starts_with("local ")) {
                continue;
            }
            const std::size_t equal = probe_trimmed.find('=');
            if (equal == std::string_view::npos) {
                continue;
            }
            const std::string assign_lhs = std::string(
                TrimLuaWhitespace(probe_trimmed.substr(6, equal - 6)));
            const std::string_view assign_rhs =
                TrimLuaWhitespace(probe_trimmed.substr(equal + 1));
            if (!IsValidLuaIdentifier(assign_lhs) ||
                (assign_rhs != "function()" && assign_rhs != "function ()")) {
                continue;
            }

            const std::size_t placeholder_pos = (*lines)[line_index].find("function()");
            const std::size_t spaced_placeholder_pos =
                (*lines)[line_index].find("function ()");
            if (placeholder_pos != std::string::npos) {
                (*lines)[line_index].replace(placeholder_pos, 10, assign_lhs);
                changed = true;
            } else if (spaced_placeholder_pos != std::string::npos) {
                (*lines)[line_index].replace(spaced_placeholder_pos, 11, assign_lhs);
                changed = true;
            }
            break;
        }
    }

    return changed;
}

bool SimplifyRenderedLuaMultiResultMemberLhsTemps(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        bool assign_is_local = false;
        std::vector<std::string> lhs_names;
        std::string rhs_expression;
        if (!ParseRenderedAssignmentLineAllowFunctionPlaceholders((*lines)[line_index],
                                                                  &assign_is_local,
                                                                  &lhs_names,
                                                                  &rhs_expression) ||
            lhs_names.size() < 2) {
            continue;
        }

        std::string callee;
        std::vector<std::string> arguments;
        if (!ParseRenderedSingleCallLine(rhs_expression, &callee, &arguments)) {
            continue;
        }

        std::size_t member_index = lhs_names.size();
        for (std::size_t i = 0; i < lhs_names.size(); ++i) {
            const std::string lhs =
                StripRenderedWholeParentheses(TrimLuaWhitespace(lhs_names[i]), nullptr);
            if (lhs.find('.') != std::string::npos && IsRenderedSafePrefixExpression(lhs)) {
                member_index = i;
                break;
            }
        }
        if (member_index >= lhs_names.size()) {
            continue;
        }

        std::string inferred_name;
        const std::size_t scan_limit = std::min(lines->size(), line_index + 4);
        for (std::size_t probe = line_index + 1; probe < scan_limit; ++probe) {
            const std::string_view trimmed =
                TrimLuaWhitespace(StripLuaLineComment((*lines)[probe]));
            if (!trimmed.starts_with("local ")) {
                continue;
            }
            const std::size_t equal = trimmed.find('=');
            if (equal == std::string_view::npos) {
                continue;
            }
            const std::string name = std::string(
                TrimLuaWhitespace(trimmed.substr(6, equal - 6)));
            const std::string rhs =
                std::string(TrimLuaWhitespace(trimmed.substr(equal + 1)));
            if (!IsValidLuaIdentifier(name)) {
                continue;
            }
            if (rhs.find(name) == std::string::npos) {
                continue;
            }
            inferred_name = name;
            // remove the redundant local declaration to avoid shadowing.
            (*lines)[probe].erase(0, 6);
            changed = true;
            break;
        }

        if (inferred_name.empty()) {
            continue;
        }

        lhs_names[member_index] = inferred_name;
        const std::string indent(GetRenderedLuaIndent((*lines)[line_index]));
        std::ostringstream rebuilt;
        rebuilt << indent << (assign_is_local ? "local " : "");
        for (std::size_t i = 0; i < lhs_names.size(); ++i) {
            if (i != 0) {
                rebuilt << ", ";
            }
            rebuilt << lhs_names[i];
        }
        rebuilt << " = " << rhs_expression;
        (*lines)[line_index] = rebuilt.str();
        changed = true;
    }

    return changed;
}

bool SimplifyRenderedLuaSplitLocalMemberMultiAssignments(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    std::vector<std::vector<std::size_t>> scope_paths;
    ComputeRenderedScopePaths(*lines, &scope_paths);
    std::vector<std::string> scope_keys;
    scope_keys.reserve(lines->size());
    for (const auto& scope_path : scope_paths) {
        if (scope_path.empty()) {
            scope_keys.emplace_back("0");
            continue;
        }
        std::string key;
        key.reserve(scope_path.size() * 3);
        for (std::size_t i = 0; i < scope_path.size(); ++i) {
            if (i != 0) {
                key.push_back('/');
            }
            key += std::to_string(scope_path[i]);
        }
        scope_keys.emplace_back(std::move(key));
    }
    std::unordered_map<std::string, std::unordered_set<std::string>> declared_by_scope;

    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        const std::string scope_key =
            line_index < scope_keys.size() ? scope_keys[line_index] : std::string("0");
        auto& declared_locals = declared_by_scope[scope_key];
        std::vector<std::string> declared_here;
        if (ParseRenderedLocalDeclarationLine((*lines)[line_index], &declared_here)) {
            // Delay inserting until after we process this line so we only
            // account for locals declared earlier in the same scope.
        } else {
            declared_here.clear();
        }

        bool assign_is_local = false;
        std::vector<std::string> lhs_names;
        std::string rhs_expression;
        if (!ParseRenderedAssignmentLineAllowFunctionPlaceholders((*lines)[line_index],
                                                                  &assign_is_local,
                                                                  &lhs_names,
                                                                  &rhs_expression) ||
            !assign_is_local || lhs_names.size() < 2) {
            for (const std::string& name : declared_here) {
                declared_locals.insert(name);
            }
            continue;
        }

        bool has_member = false;
        std::vector<std::string> local_names;
        local_names.reserve(lhs_names.size());
        for (const std::string& raw_lhs : lhs_names) {
            const std::string lhs =
                StripRenderedWholeParentheses(TrimLuaWhitespace(raw_lhs), nullptr);
            if (IsValidLuaIdentifier(lhs)) {
                local_names.push_back(lhs);
                continue;
            }
            if ((lhs.find('.') != std::string::npos ||
                 lhs.find('[') != std::string::npos) &&
                IsRenderedSafePrefixExpression(lhs)) {
                has_member = true;
            }
        }
        if (!has_member || local_names.empty()) {
            for (const std::string& name : declared_here) {
                declared_locals.insert(name);
            }
            continue;
        }

        std::vector<std::string> missing_locals;
        missing_locals.reserve(local_names.size());
        for (const std::string& name : local_names) {
            if (declared_locals.find(name) == declared_locals.end()) {
                missing_locals.push_back(name);
            }
        }

        if (!missing_locals.empty()) {
            const std::string indent(GetRenderedLuaIndent((*lines)[line_index]));
            std::ostringstream local_decl;
            local_decl << indent << "local ";
            for (std::size_t i = 0; i < missing_locals.size(); ++i) {
                if (i != 0) {
                    local_decl << ", ";
                }
                local_decl << missing_locals[i];
            }
            lines->insert(lines->begin() + static_cast<std::ptrdiff_t>(line_index),
                          local_decl.str());
            ++line_index;
            changed = true;
        }

        const std::size_t first_non_space =
            (*lines)[line_index].find_first_not_of(" \t");
        if (first_non_space != std::string::npos &&
            (*lines)[line_index].compare(first_non_space, 6, "local ") == 0) {
            (*lines)[line_index].erase(first_non_space, 6);
            changed = true;
        }

        for (const std::string& name : declared_here) {
            declared_locals.insert(name);
        }
    }

    return changed;
}

bool SimplifyRenderedLuaInvalidNumericForLoopHeads(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        const std::string_view trimmed =
            TrimLuaWhitespace(StripLuaLineComment((*lines)[line_index]));
        if (trimmed.starts_with("for ") && trimmed.ends_with(" do)")) {
            const std::size_t equal = trimmed.find('=');
            const std::size_t do_pos = trimmed.rfind(" do)");
            if (equal != std::string_view::npos && do_pos != std::string_view::npos &&
                equal > 4 && equal < do_pos) {
                const std::string_view lhs =
                    TrimLuaWhitespace(trimmed.substr(4, equal - 4));
                const std::string_view rhs =
                    TrimLuaWhitespace(trimmed.substr(equal + 1, do_pos - equal - 1));
                if (IsValidLuaIdentifier(lhs) && rhs.starts_with("((")) {
                    const std::string indent(GetRenderedLuaIndent((*lines)[line_index]));
                    (*lines)[line_index] = indent + "for " + std::string(lhs) + " = " +
                                           std::string(rhs.substr(1)) + " do";
                    changed = true;
                    continue;
                }
            }
        }
        if (!trimmed.starts_with("for ") || !trimmed.ends_with(" do")) {
            continue;
        }

        const std::size_t equal = trimmed.find('=');
        const std::size_t do_pos = trimmed.rfind(" do");
        if (equal == std::string_view::npos || do_pos == std::string_view::npos ||
            equal <= 4 || equal >= do_pos) {
            continue;
        }

        const std::string_view lhs =
            TrimLuaWhitespace(trimmed.substr(4, equal - 4));
        if (IsValidLuaIdentifier(lhs)) {
            continue;
        }

        std::string loop_temp_name;
        const std::size_t scan_limit = std::min(lines->size(), line_index + 4);
        for (std::size_t probe = line_index + 1; probe < scan_limit; ++probe) {
            const std::string_view stripped = StripLuaLineComment((*lines)[probe]);
            std::size_t cursor = 0;
            while (cursor < stripped.size()) {
                const unsigned char ch = static_cast<unsigned char>(stripped[cursor]);
                if ((std::isalpha(ch) == 0) && stripped[cursor] != '_') {
                    ++cursor;
                    continue;
                }

                const std::size_t begin = cursor++;
                while (cursor < stripped.size()) {
                    const unsigned char token_ch = static_cast<unsigned char>(stripped[cursor]);
                    if ((std::isalnum(token_ch) == 0) && stripped[cursor] != '_') {
                        break;
                    }
                    ++cursor;
                }

                const std::string_view token = stripped.substr(begin, cursor - begin);
                if (IsRenderTemporaryName(token)) {
                    loop_temp_name = std::string(token);
                    break;
                }
            }
            if (!loop_temp_name.empty()) {
                break;
            }
        }
        if (loop_temp_name.empty()) {
            continue;
        }

        const std::string indent(GetRenderedLuaIndent((*lines)[line_index]));
        const std::string rhs =
            std::string(TrimLuaWhitespace(trimmed.substr(equal + 1, do_pos - equal - 1)));
        (*lines)[line_index] = indent + "for " + loop_temp_name + " = " + rhs + " do";
        changed = true;
    }

    return changed;
}

bool SimplifyRenderedLuaInvalidGenericForLoopHeads(std::vector<std::string>* lines) {
    if (lines == nullptr) {
        return false;
    }

    const auto extract_valid_identifier_seed = [](std::string_view value,
                                                  std::string* identifier_out) {
        if (identifier_out == nullptr) {
            return false;
        }

        const std::string normalized =
            StripRenderedWholeParentheses(TrimLuaWhitespace(value), nullptr);
        if (normalized.empty()) {
            return false;
        }

        std::size_t length = 0;
        const unsigned char first = static_cast<unsigned char>(normalized.front());
        if (std::isalpha(first) == 0 && normalized.front() != '_') {
            return false;
        }
        while (length < normalized.size()) {
            const unsigned char ch = static_cast<unsigned char>(normalized[length]);
            if (std::isalnum(ch) == 0 && normalized[length] != '_') {
                break;
            }
            ++length;
        }
        if (length == 0) {
            return false;
        }

        const std::string candidate(normalized.substr(0, length));
        if (!IsValidLuaIdentifier(candidate)) {
            return false;
        }

        *identifier_out = candidate;
        return true;
    };

    bool changed = false;
    for (std::size_t line_index = 0; line_index < lines->size(); ++line_index) {
        const std::string& original_line = (*lines)[line_index];
        const std::string_view stripped = StripLuaLineComment(original_line);
        const std::string_view trimmed = TrimLuaWhitespace(stripped);
        if (!trimmed.starts_with("for ") || !trimmed.ends_with(" do")) {
            continue;
        }

        const std::size_t in_pos = trimmed.find(" in ");
        const std::size_t do_pos = trimmed.rfind(" do");
        if (in_pos == std::string_view::npos || do_pos == std::string_view::npos ||
            in_pos <= 4 || in_pos >= do_pos) {
            continue;
        }

        const std::string_view header = trimmed.substr(4, in_pos - 4);
        std::vector<std::string> header_parts;
        if (!SplitRenderedTopLevelCommaChain(header, &header_parts) || header_parts.empty()) {
            continue;
        }

        bool saw_invalid = false;
        bool failed = false;
        for (std::string& header_part : header_parts) {
            const std::string normalized =
                StripRenderedWholeParentheses(TrimLuaWhitespace(header_part), nullptr);
            if (IsValidLuaIdentifier(normalized)) {
                header_part = normalized;
                continue;
            }

            std::string identifier_seed;
            if (!extract_valid_identifier_seed(normalized, &identifier_seed)) {
                failed = true;
                break;
            }

            header_part = std::move(identifier_seed);
            saw_invalid = true;
        }

        if (failed || !saw_invalid) {
            continue;
        }

        std::ostringstream rebuilt_header;
        for (std::size_t index = 0; index < header_parts.size(); ++index) {
            if (index != 0) {
                rebuilt_header << ", ";
            }
            rebuilt_header << header_parts[index];
        }

        const std::string indent(GetRenderedLuaIndent(original_line));
        std::string rebuilt_line =
            indent + "for " + rebuilt_header.str() + std::string(trimmed.substr(in_pos));
        if (stripped.size() < original_line.size()) {
            rebuilt_line += std::string(original_line.substr(stripped.size()));
        }
        (*lines)[line_index] = std::move(rebuilt_line);
        changed = true;
    }

    return changed;
}

}  // namespace refii::lua
